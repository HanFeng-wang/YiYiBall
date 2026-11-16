using System.Text.Json;

namespace ScreenTranslator;

/// <summary>
/// 查词与对话记录持久化（JSON 文件，按会话存储）。
/// </summary>
public sealed class HistoryStore
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    private static string FilePath => Path.Combine(DataDir, "history.json");

    private readonly object _lock = new();
    private List<HistoryItem> _items = new();
    private readonly int _limit;

    public HistoryStore(int limit) => _limit = limit;

    public sealed class HistoryItem
    {
        public string Word { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;
        public List<Msg> Messages { get; set; } = new();

        public sealed class Msg
        {
            public string Role { get; set; } = "";
            public string Content { get; set; } = "";
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                if (File.Exists(FilePath))
                    _items = (JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(FilePath)) ?? new())
                        .Where(i => !string.IsNullOrWhiteSpace(i.Word) && i.Messages.Count > 0)
                        .ToList();
            }
            catch
            {
                _items = new();
            }
        }
    }

    /// <summary>按词 upsert 会话：同词替换为最新完整对话记录。</summary>
    public void SaveSession(string word, List<(string role, string content)> messages)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.Word == word);
            var item = new HistoryItem
            {
                Word = word,
                Time = DateTime.Now,
                Messages = messages.Select(m => new HistoryItem.Msg { Role = m.role, Content = m.content }).ToList()
            };
            _items.Insert(0, item);
            if (_items.Count > _limit) _items = _items.Take(_limit).ToList();
            Persist();
        }
    }

    public HistoryItem? Remove(string word)
    {
        lock (_lock)
        {
            var removed = _items.FirstOrDefault(i => i.Word == word);
            if (removed == null) return null;
            _items.RemoveAll(i => i.Word == word);
            Persist();
            return removed;
        }
    }

    public List<HistoryItem> GetRecent(int? count = null)
    {
        lock (_lock) return _items.Take(count ?? _limit).ToList();
    }

    public HistoryItem? FindByWord(string word)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Word == word);
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 落盘失败不阻断主流程
        }
    }
}
