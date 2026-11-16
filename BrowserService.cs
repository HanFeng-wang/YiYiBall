using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ScreenTranslator;

/// <summary>
/// 真浏览器自动化（方案 §11.1）：Playwright 驱动自带 Chromium（有头独立窗口，与用户日常浏览器互不干扰）。
/// 页面理解用「可交互元素文本快照」（无障碍树摘要，零视觉模型成本）：
/// 循环 快照 → LLM 决策（结构化 JSON）→ 执行 → 再快照，步数上限 10 步 + 单步超时。
/// 网银/支付类页面不自动操作（URL 黑名单），口播提示手动完成。
/// </summary>
public sealed class BrowserService : IDisposable
{
    private static readonly Regex PayGuard = new(
        @"bank|alipay|tenpay|unionpay|paypal|icbc|cmbchina|abchina|ccb\.cn|boc\.cn|psbc|spdb|cebbank|cib\.com|pab\.com\.cn|checkout|payment|cashier",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int MaxSteps = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private IPage? _page;
    private bool _installTried;

    public void Dispose()
    {
        try { _page?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        try { _ctx?.DisposeAsync().GetAwaiter().GetResult(); } catch { }
        try { _browser?.DisposeAsync().GetAwaiter().GetResult(); } catch { }
        try { _pw?.Dispose(); } catch { }
    }

    /// <summary>
    /// 在受控浏览器里完成一个任务。返回给用户的简短结果（成功时由调用方小字留痕）。
    /// 失败抛异常，由 AppToolbox 转成口播。
    /// </summary>
    public async Task<string> RunTaskAsync(string url, string instruction, AiService ai, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureAsync(ct);
            var page = _page!;
            if (page.IsClosed) { _page = page = await _ctx!.NewPageAsync(); }

            if (!string.IsNullOrWhiteSpace(url))
            {
                await NavigateAsync(page, url, ct);
                if (PayGuard.IsMatch(page.Url))
                    throw new Exception("这个网址涉及支付或网银，我不能自动操作，请你手动完成");
            }

            var done = new StringBuilder();
            for (int step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                if (PayGuard.IsMatch(page.Url))
                    throw new Exception("页面跳转到了支付/网银类站点，我已停止自动操作，请你手动完成");

                // ① 可交互元素文本快照（单次 JS 注入，可见 + 可点）
                var snapshot = await SnapshotAsync(page, ct);

                // ② LLM 决策：给任务、快照、已执行历史 → 下一步动作 JSON
                var decisionJson = await ai.QuickTextAsync(
                    DecideSys,
                    $"用户任务：{instruction}\n当前网址：{page.Url}\n\n页面可交互元素（i=序号, tag=类型, text=文字）：\n{snapshot}\n\n已执行：\n{(done.Length == 0 ? "（无）" : done)}",
                    ct);
                var act = ParseDecision(decisionJson);

                // ③ 执行
                switch (act.action)
                {
                    case "click":
                        if (act.index < 0) throw new Exception("浏览器操作环节没有说清要点哪个元素");
                        done.AppendLine($"click #{act.index}");
                        await page.ClickAsync($"[data-yy-id='{act.index}']",
                            new() { Timeout = 8000, Trial = false });
                        await SettleAsync(page, ct);
                        break;

                    case "fill":
                        if (act.index < 0 || act.value.Length == 0)
                            throw new Exception("浏览器操作环节没有说清要填什么");
                        done.AppendLine($"fill #{act.index}");
                        await page.FillAsync($"[data-yy-id='{act.index}']", act.value,
                            new() { Timeout = 8000 });
                        if (act.enter)
                        {
                            await page.Keyboard.PressAsync("Enter");
                            await SettleAsync(page, ct);
                        }
                        break;

                    case "navigate":
                        if (act.value.Length == 0) throw new Exception("浏览器操作环节没有说要跳到哪");
                        done.AppendLine($"goto {act.value}");
                        await NavigateAsync(page, act.value, ct);
                        break;

                    case "new_tab":
                        done.AppendLine("new_tab");
                        _page = page = await _ctx!.NewPageAsync();
                        break;

                    case "switch_tab":
                        {
                            var pages = _ctx!.Pages;
                            int idx = act.index < 0 ? pages.Count - 1 : Math.Min(act.index, pages.Count - 1);
                            if (idx < 0) throw new Exception("没有其他标签页可切换");
                            done.AppendLine($"switch_tab #{idx}");
                            _page = page = pages[idx];
                            await page.BringToFrontAsync();
                            break;
                        }

                    case "extract_text":
                        {
                            string text = (await page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''")).Trim();
                            done.AppendLine("extract_text：已读取页面文字");
                            // 文字直接作为观察结果回喂下一轮决策
                            done.AppendLine(text.Length > 1200 ? text[..1200] : text);
                            break;
                        }

                    case "done":
                        return string.IsNullOrWhiteSpace(act.summary) ? "浏览器任务已完成" : act.summary.Trim();

                    default:
                        throw new Exception("浏览器操作决策无法识别，已停止");
                }
            }
            return "浏览器任务做到步数上限停下了，已完成的部分留在打开的窗口里，你可以接着手动操作";
        }
        finally { _gate.Release(); }
    }

    // ---------------- 内部实现 ----------------

    private static readonly string DecideSys = """
        你是浏览器自动化决策器。根据用户任务、页面可交互元素快照和已执行动作，输出下一步操作的 JSON，动作七选一：
        点击元素 {"action":"click","index":元素序号}；
        填写输入框 {"action":"fill","index":元素序号,"value":"要填的文字","enter":填完是否按回车(布尔)}；
        跳转网址 {"action":"navigate","value":"网址"}；
        新建标签页 {"action":"new_tab"}；
        切换标签页 {"action":"switch_tab","index":标签页序号(-1=最后一个)}；
        抓取页面全文 {"action":"extract_text"}；
        任务完成 {"action":"done","summary":"一句话总结任务完成情况（做了什么、结果在哪）"}。
        规则：
        1. 只能操作快照里列出的元素（index 取快照里的 i 值）。
        2. 一步只做一个动作；页面没变就换个思路，不要重复同一个动作。
        3. 任务一完成立即 done，不贪多做多余操作。
        4. 页面上确实找不到目标时 done 并在 summary 里说明原因。
        5. 不碰登录、支付、验证码、短信验证码。
        只输出 JSON，不要解释。
        """;

    private record Decision(string action, int index, string value, bool enter, string summary);

    private static Decision ParseDecision(string raw)
    {
        var m = Regex.Match(raw, @"\{[\s\S]*\}");
        if (!m.Success) throw new Exception("浏览器决策输出无法解析");
        using var doc = JsonDocument.Parse(m.Value);
        var r = doc.RootElement;
        return new Decision(
            r.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
            r.TryGetProperty("index", out var i) && i.TryGetInt32(out int iv) ? iv : -1,
            r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "",
            r.TryGetProperty("enter", out var e) && e.ValueKind == JsonValueKind.True,
            r.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "");
    }

    /// <summary>
    /// 启动/复用受控浏览器：优先用系统已装的 Edge / Chrome（channel 模式，零下载、
    /// 独立自动化窗口不碰用户日常 profile）；两者都没有才退回 bundled Chromium（首次自动下载）。
    /// </summary>
    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_pw == null) _pw = await Playwright.CreateAsync();
        if (_browser == null || !_browser.IsConnected)
        {
            try
            {
                _browser = await LaunchAsync();
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist") && !_installTried)
            {
                _installTried = true;
                // 最后兜底：下载 bundled Chromium（同步 CLI，后台线程跑）
                await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }), ct);
                _browser = await LaunchAsync();
            }
            _ctx = await _browser.NewContextAsync(new() { ViewportSize = ViewportSize.NoViewport });
            _page = await _ctx.NewPageAsync();
        }
        else if (_ctx == null)
        {
            _ctx = await _browser.NewContextAsync(new() { ViewportSize = ViewportSize.NoViewport });
            _page = await _ctx.NewPageAsync();
        }
    }

    /// <summary>依次尝试系统 Edge → Chrome → bundled Chromium。</summary>
    private async Task<IBrowser> LaunchAsync()
    {
        Exception? last = null;
        foreach (var channel in new[] { "msedge", "chrome", null })
        {
            try
            {
                return await _pw!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = channel,
                    Headless = false,
                });
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception("没有可用的浏览器");
    }

    /// <summary>跳转（裸域名自动补 https://），DOMContentLoaded 即算就位。</summary>
    private static async Task NavigateAsync(IPage page, string url, CancellationToken ct)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
        await SettleAsync(page, ct);
    }

    /// <summary>点击/填表后的稳定等待：等可能的导航，SPA 不导航也不报错。</summary>
    private static async Task SettleAsync(IPage page, CancellationToken ct)
    {
        try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 }); }
        catch { }
        await page.WaitForTimeoutAsync(600);
    }

    /// <summary>可交互元素文本快照：注入 data-yy-id，返回紧凑列表文本（最多 50 个）。</summary>
    private static async Task<string> SnapshotAsync(IPage page, CancellationToken ct)
    {
        var items = await page.EvaluateAsync<string[]>(@"() => {
            const els = document.querySelectorAll('a,button,input,textarea,select,[role=button],[onclick]');
            const out = [];
            let id = 0;
            for (const e of els) {
                if (out.length >= 50) break;
                const r = e.getBoundingClientRect();
                if (r.width < 2 || r.height < 2) continue;
                const st = getComputedStyle(e);
                if (st.visibility === 'hidden' || st.display === 'none' || +st.opacity === 0) continue;
                const tag = e.tagName.toLowerCase();
                let text = '';
                if (tag === 'input' || tag === 'textarea') {
                    text = e.placeholder || e.value || e.name || e.ariaLabel || '';
                    if (e.type === 'submit' || e.type === 'button') text = e.value || text;
                } else {
                    text = (e.innerText || e.textContent || e.title || e.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ');
                }
                text = String(text).trim().slice(0, 60);
                if (!text) continue;
                e.setAttribute('data-yy-id', id);
                out.push(id + '｜' + tag + '｜' + text);
                id++;
            }
            return out;
        }");

        var sb = new StringBuilder();
        foreach (var it in items) sb.AppendLine(it);
        return sb.Length == 0 ? "（页面上没有找到可交互元素）" : sb.ToString();
    }
}
