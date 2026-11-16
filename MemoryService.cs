using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ScreenTranslator;

/// <summary>
/// 记忆与知识库服务（方案 §10）：SQLite 单文件 memory.db，三张表
/// memories（长期记忆）/ kb_chunks（资料知识库分块）/ skills（技能库）。
/// 向量用 qwen3.7-text-embedding-flash（云端，走 AiService.EmbedAsync），
/// 以 float[] blob 存 SQLite，查询时内存算 cosine——个人知识库规模毫秒级。
/// 线程模型：语音会话线程 / 设置页线程都会访问，用 SemaphoreSlim 串行化。
/// </summary>
public sealed class MemoryService
{
    private static readonly string DbPath =
        Path.Combine(AppContext.BaseDirectory, "data", "memory.db");

    private readonly AppConfig _cfg;
    private readonly AiService _ai;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 知识库增量扫描互斥（扫描期间再触发直接跳过）
    private int _scanning;

    public MemoryService(AppConfig cfg, AiService ai)
    {
        _cfg = cfg;
        _ai = ai;
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        EnsureTables();
    }

    private void EnsureTables()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memories(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                content TEXT NOT NULL,
                embedding BLOB,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS kb_chunks(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                mtime TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content TEXT NOT NULL,
                embedding BLOB);
            CREATE INDEX IF NOT EXISTS idx_kb_source ON kb_chunks(source);
            CREATE TABLE IF NOT EXISTS skills(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                actions TEXT NOT NULL,
                created_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection("Data Source=" + DbPath);
        conn.Open();
        return conn;
    }

    // ---------------- 向量工具 ----------------

    private static byte[] ToBlob(float[] v)
    {
        var b = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    private static float[]? FromBlob(object? o)
    {
        if (o is not byte[] b || b.Length == 0) return null;
        var v = new float[b.Length / sizeof(float)];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>分批向量化（每批 ≤ 16 条，避免单请求过大）。</summary>
    private async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new List<float[]>(texts.Count);
        for (int i = 0; i < texts.Count; i += 16)
        {
            var batch = texts.Skip(i).Take(16).ToList();
            result.AddRange(await _ai.EmbedAsync(batch, ct));
        }
        return result;
    }

    // ---------------- 长期记忆（memories，方案 §10.4） ----------------

    /// <summary>写入一条长期记忆（向量化入库）。失败抛异常由调用方兜底。</summary>
    public async Task AddMemoryAsync(string content, CancellationToken ct = default)
    {
        content = content.Trim();
        if (content.Length == 0) return;
        var vec = (await EmbedBatchAsync(new[] { content }, ct))[0];
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO memories(content, embedding, created_at, updated_at) VALUES(@c,@e,@t,@t)";
            cmd.Parameters.AddWithValue("@c", content);
            cmd.Parameters.AddWithValue("@e", ToBlob(vec));
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    /// <summary>编辑一条长期记忆内容并重新向量化。</summary>
    public async Task UpdateMemoryAsync(long id, string content, CancellationToken ct = default)
    {
        content = content.Trim();
        var vec = (await EmbedBatchAsync(new[] { content }, ct))[0];
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE memories SET content=@c, embedding=@e, updated_at=@t WHERE id=@id";
            cmd.Parameters.AddWithValue("@c", content);
            cmd.Parameters.AddWithValue("@e", ToBlob(vec));
            cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public void DeleteMemory(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<(long id, string content, string updatedAt)> ListMemories()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, content, updated_at FROM memories ORDER BY id DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string)>();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        return list;
    }

    /// <summary>长期记忆检索：向量化查询 → 内存 cosine → 阈值过滤取 top-k。</summary>
    public async Task<List<(string content, double score)>> SearchMemoriesAsync(
        string query, int topK = 3, double minScore = 0.35, CancellationToken ct = default)
    {
        // 长期记忆规模小（几十上百条），全量载入后内存比相似度
        List<(long id, string content, float[]? vec)> rows;
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, content, embedding FROM memories";
            using var r = cmd.ExecuteReader();
            rows = new();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), FromBlob(r["embedding"])));
        }
        finally { _gate.Release(); }
        rows.RemoveAll(x => x.vec == null);
        if (rows.Count == 0) return new();

        var qv = (await EmbedBatchAsync(new[] { query }, ct))[0];
        return rows
            .Select(x => (x.content, score: Cosine(qv, x.vec!)))
            .Where(x => x.score >= minScore)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();
    }

    // ---------------- 资料知识库（kb_chunks，方案 §10.3） ----------------

    private static readonly string[] KbExtensions = { ".txt", ".md", ".pdf", ".docx" };

    /// <summary>
    /// 知识库增量扫描：对比文件 mtime，变化的文件重新抽文本、切块（300~500 字带重叠）、
    /// 批量向量化入库；已删除的文件清掉对应分块。返回本轮更新的文件数。
    /// </summary>
    public async Task<int> ScanKbAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return 0;
        try
        {
            // 现有分块的 mtime 索引：source -> (mtime, ids)
            var known = new Dictionary<string, (string mtime, List<long> ids)>(StringComparer.OrdinalIgnoreCase);
            await _gate.WaitAsync(ct);
            List<string> dbSources;
            try
            {
                using (var conn = Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT DISTINCT source FROM kb_chunks";
                    dbSources = new();
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) dbSources.Add(r.GetString(0));

                    foreach (var src in dbSources)
                    {
                        using var c2 = conn.CreateCommand();
                        c2.CommandText = "SELECT mtime, id FROM kb_chunks WHERE source=@s ORDER BY chunk_index";
                        c2.Parameters.AddWithValue("@s", src);
                        using var r2 = c2.ExecuteReader();
                        string mtime = "";
                        var ids = new List<long>();
                        while (r2.Read()) { mtime = r2.IsDBNull(0) ? "" : r2.GetString(0); ids.Add(r2.GetInt64(1)); }
                        known[src] = (mtime, ids);
                    }
                }
            }
            finally { _gate.Release(); }

            int updated = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in _cfg.KbFolders)
            {
                if (!Directory.Exists(folder)) continue;
                IEnumerable<string> files;
                try
                {
                    // IgnoreInaccessible：无权限目录跳过，不因单个受保护目录让整次扫描失败
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                    files = Directory.EnumerateFiles(folder, "*", opts)
                        .Where(f => KbExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .ToList();   // 立即物化，异常（若有）留在 try 内
                }
                catch { continue; }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string full;
                    try { full = Path.GetFullPath(file); } catch { continue; }
                    seen.Add(full);
                    string mtime;
                    try { mtime = File.GetLastWriteTimeUtc(full).Ticks.ToString(); }
                    catch { continue; }

                    if (known.TryGetValue(full, out var k) && k.mtime == mtime) continue;   // 未变化
                    known.Remove(full);

                    string text;
                    try { text = ExtractText(full); } catch { continue; }   // 单文件损坏不拖垮整轮扫描
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var chunks = ChunkText(text);
                    if (chunks.Count == 0) continue;

                    // 分块向量化 + 入库（含源文件 mtime，下次对比）
                    var vecs = await EmbedBatchAsync(chunks, ct);
                    await _gate.WaitAsync(ct);
                    try
                    {
                        using var conn = Open();
                        using (var del = conn.CreateCommand())
                        {
                            del.CommandText = "DELETE FROM kb_chunks WHERE source=@s";
                            del.Parameters.AddWithValue("@s", full);
                            del.ExecuteNonQuery();
                        }
                        for (int i = 0; i < chunks.Count; i++)
                        {
                            using var ins = conn.CreateCommand();
                            ins.CommandText =
                                "INSERT INTO kb_chunks(source, mtime, chunk_index, content, embedding) VALUES(@s,@m,@i,@c,@e)";
                            ins.Parameters.AddWithValue("@s", full);
                            ins.Parameters.AddWithValue("@m", mtime);
                            ins.Parameters.AddWithValue("@i", i);
                            ins.Parameters.AddWithValue("@c", chunks[i]);
                            ins.Parameters.AddWithValue("@e", ToBlob(vecs[i]));
                            ins.ExecuteNonQuery();
                        }
                    }
                    finally { _gate.Release(); }
                    updated++;
                }
            }

            // 已被删除/移出文件夹的文件：清掉残留分块
            var stale = known.Keys.Where(s => !seen.Contains(s)).ToList();
            if (stale.Count > 0)
            {
                await _gate.WaitAsync(ct);
                try
                {
                    using var conn = Open();
                    foreach (var src in stale)
                    {
                        using var del = conn.CreateCommand();
                        del.CommandText = "DELETE FROM kb_chunks WHERE source=@s";
                        del.Parameters.AddWithValue("@s", src);
                        del.ExecuteNonQuery();
                    }
                }
                finally { _gate.Release(); }
            }
            return updated;
        }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }

    /// <summary>知识库检索：与长期记忆共用向量化/相似度代码，查 kb_chunks 表。</summary>
    public async Task<List<(string source, string content, double score)>> SearchKbAsync(
        string query, int topK = 4, double minScore = 0.30, CancellationToken ct = default)
    {
        List<(long id, string source, string content, float[]? vec)> rows;
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, source, content, embedding FROM kb_chunks";
            using var r = cmd.ExecuteReader();
            rows = new();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), FromBlob(r["embedding"])));
        }
        finally { _gate.Release(); }
        rows.RemoveAll(x => x.vec == null);
        if (rows.Count == 0) return new();

        var qv = (await EmbedBatchAsync(new[] { query }, ct))[0];
        return rows
            .Select(x => (x.source, x.content, score: Cosine(qv, x.vec!)))
            .Where(x => x.score >= minScore)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();
    }

    /// <summary>抽取文件纯文本：txt/md 直读，docx 走 Open XML SDK，pdf 走 PdfPig。（rename_selected 复用）</summary>
    internal static string ExtractText(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".txt":
            case ".md":
                return File.ReadAllText(path, DetectEncoding(path));
            case ".docx":
            {
                using var doc = WordprocessingDocument.Open(path, false);
                var sb = new StringBuilder();
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body != null)
                    foreach (var p in body.Elements<Paragraph>())
                        sb.AppendLine(p.InnerText);
                return sb.ToString();
            }
            case ".pdf":
            {
                var sb = new StringBuilder();
                using var pdf = PdfDocument.Open(path);
                foreach (var page in pdf.GetPages())
                    sb.AppendLine(page.Text);
                return sb.ToString();
            }
            default:
                return "";
        }
    }

    /// <summary>txt 编码探测：有 BOM 用 BOM，否则按 UTF-8 宽松读取（.NET 默认替换无效字节）。</summary>
    private static Encoding DetectEncoding(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> bom = stackalloc byte[3];
            int n = fs.Read(bom);
            if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
            if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
            if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        }
        catch { }
        return new UTF8Encoding(false);
    }

    /// <summary>段落切块：目标 300~500 字，带 ~60 字重叠（上下文不因切块硬断）。</summary>
    internal static List<string> ChunkText(string text, int target = 400, int overlap = 60)
    {
        // 先按空行/换行聚成段落
        var paras = Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n|\n")
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        var chunks = new List<string>();
        var buf = new StringBuilder();
        foreach (var p in paras)
        {
            if (buf.Length > 0 && buf.Length + p.Length + 1 > target)
            {
                chunks.Add(buf.ToString());
                var tail = buf.ToString();
                buf.Clear();
                buf.Append(tail.Length > overlap ? tail[^overlap..] : tail);   // 重叠衔接
                buf.Append('\n');
            }
            buf.Append(p).Append('\n');
        }
        if (buf.Length > 0) chunks.Add(buf.ToString());

        // 超长单段硬切（如整页无换行的 PDF 文本）
        var final = new List<string>();
        foreach (var c in chunks)
        {
            if (c.Length <= target * 1.5) { final.Add(c.Trim()); continue; }
            for (int i = 0; i < c.Length; i += target - overlap)
                final.Add(c.Substring(i, Math.Min(target, c.Length - i)).Trim());
        }
        return final.Where(c => c.Length > 0).ToList();
    }

    // ---------------- 技能库（skills，方案 §10.5） ----------------

    /// <summary>保存技能（同名覆盖），actions 为 AppToolbox 工具调用 JSON 列表。</summary>
    public async Task SaveSkillAsync(string name, string actionsJson, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO skills(name, actions, created_at) VALUES(@n,@a,@t)
                ON CONFLICT(name) DO UPDATE SET actions=excluded.actions
                """;
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@a", actionsJson);
            cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    /// <summary>按名取技能：先精确匹配，再包含匹配（「执行晨会」命中「晨会」）。返回 (actionsJson, 全部技能名)。</summary>
    public (string? actionsJson, List<string> allNames) GetSkill(string name)
    {
        name = name.Trim();
        var all = new List<(long id, string name, string actions)>();
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, actions FROM skills ORDER BY id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) all.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }
        var hit = all.FirstOrDefault(s => s.name == name).name ?? all.FirstOrDefault(s => s.name.Contains(name, StringComparison.OrdinalIgnoreCase)).name;
        var actions = hit == null ? null : all.First(s => s.name == hit).actions;
        return (actions, all.Select(s => s.name).ToList());
    }

    public void DeleteSkill(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skills WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<(long id, string name, string actions)> ListSkills()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, actions FROM skills ORDER BY id DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }
}
