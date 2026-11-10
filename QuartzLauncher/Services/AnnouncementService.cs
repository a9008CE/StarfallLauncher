using System.IO;
using System.Net.Http;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuartzLauncher.Services;

/// <summary>
/// 临时公告：后台随时发布、按版本区间投递的一条弹窗。
///
/// 数据源（按顺序尝试）：
///   1. http://114.132.152.144:8080/notices.json   ← 服务器文件，上传即生效，不用发版
///   2. https://starfallmc.top/notices.json        ← 官网兜底
///
/// JSON 结构：
/// {
///   "items": [
///     {
///       "id": "20261110-01",          // 唯一，用于「只弹一次」
///       "title": "测试公告",
///       "body": "测试公告123",
///       "level": "info",              // info | warn | critical
///       "minVersion": "0.0.0",        // 含
///       "maxVersion": "1.1.13",       // 不含 → 1.1.13 及以后收不到
///       "startsAt": "", "endsAt": "", // 可空，ISO 时间
///       "once": true,
///       "link": "",                   // 可空，点「查看详情」打开
///       "enabled": true
///     }
///   ]
/// }
/// </summary>
public static class AnnouncementService
{
    private static readonly string[] Sources =
    {
        "http://114.132.152.144:8080/notices.json",
        "https://starfallmc.top/notices.json",
    };

    private static string SeenFile =>
        Path.Combine(AppContext.BaseDirectory, "Launcher", "notices-seen.json");

    public sealed class Notice
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Level { get; set; } = "info";
        public string MinVersion { get; set; } = "";
        public string MaxVersion { get; set; } = "";
        public string StartsAt { get; set; } = "";
        public string EndsAt { get; set; } = "";
        public bool Once { get; set; } = true;
        public string Link { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    /// <summary>拉取所有公告，返回「当前版本此刻应当看到」的那几条。</summary>
    public static async Task<List<Notice>> FetchPendingAsync()
    {
        var all = await FetchAllAsync().ConfigureAwait(false);
        if (all.Count == 0) return new List<Notice>();

        var now = DateTimeOffset.Now;
        var current = ParseVersion(UpdateService.CurrentVersion);
        var seen = LoadSeen();

        var result = new List<Notice>();
        foreach (var n in all)
        {
            if (!n.Enabled) continue;
            if (string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Title)) continue;
            if (n.Once && seen.Contains(n.Id)) continue;

            // 版本区间：[minVersion, maxVersion)
            if (!string.IsNullOrWhiteSpace(n.MinVersion))
            {
                var min = ParseVersion(n.MinVersion);
                if (min != null && current != null && current < min) continue;
            }
            if (!string.IsNullOrWhiteSpace(n.MaxVersion))
            {
                var max = ParseVersion(n.MaxVersion);
                if (max != null && current != null && current >= max) continue;
            }

            // 时间区间
            if (TryParseTime(n.StartsAt, out var start) && now < start) continue;
            if (TryParseTime(n.EndsAt, out var end) && now > end) continue;

            result.Add(n);
        }
        return result;
    }

    /// <summary>依次弹窗显示（每次启动最多弹几条，避免刷屏）。</summary>
    public static async Task ShowPendingAsync(Window? owner, int maxCount = 2)
    {
        List<Notice> pending;
        try { pending = await FetchPendingAsync().ConfigureAwait(true); }
        catch { return; }

        if (pending.Count == 0) return;

        var seen = LoadSeen();
        foreach (var notice in pending.Take(maxCount))
        {
            try
            {
                Views.NoticeDialog.Show(owner, notice);
            }
            catch { }

            if (notice.Once)
            {
                seen.Add(notice.Id);
                SaveSeen(seen);
            }
        }
    }

    private static async Task<List<Notice>> FetchAllAsync()
    {
        foreach (var url in Sources)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                using var res = await client.GetAsync(url).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) continue;
                var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = JToken.Parse(text);
                var items = root is JArray arr ? arr : root["items"] as JArray;
                if (items == null) continue;
                var list = items.ToObject<List<Notice>>();
                if (list is { Count: > 0 }) return list;
            }
            catch
            {
                // 换下一个源
            }
        }
        return new List<Notice>();
    }

    private static Version? ParseVersion(string? raw)
    {
        var text = (raw ?? "").Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text[..cut];
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('.');
        if (parts.Length == 1) text += ".0.0";
        else if (parts.Length == 2) text += ".0";
        return Version.TryParse(text, out var v) ? v : null;
    }

    private static bool TryParseTime(string? raw, out DateTimeOffset value)
    {
        value = default;
        return !string.IsNullOrWhiteSpace(raw)
               && DateTimeOffset.TryParse(raw, out value);
    }

    private static HashSet<string> LoadSeen()
    {
        try
        {
            if (!File.Exists(SeenFile)) return new HashSet<string>(StringComparer.Ordinal);
            var list = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(SeenFile));
            return new HashSet<string>(list ?? new List<string>(), StringComparer.Ordinal);
        }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private static void SaveSeen(HashSet<string> seen)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeenFile)!);
            // 只留最近 200 条，避免文件无限增长
            var trimmed = seen.TakeLast(200).ToList();
            File.WriteAllText(SeenFile, JsonConvert.SerializeObject(trimmed, Formatting.Indented));
        }
        catch { }
    }
}
