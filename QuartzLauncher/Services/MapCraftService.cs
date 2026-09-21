using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace QuartzLauncher.Services;

/// <summary>MapCraft（zh.mapcraft.me）上的一张地图。</summary>
public sealed class MapCraftItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _version = "";

    public string Title { get; set; } = "";
    public string DetailUrl { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";

    private string _description = "";

    /// <summary>简介：列表页只有短摘要，详情页会补全为网站的完整简介</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Description)));
        }
    }

    public bool DescriptionLoaded { get; set; }

    /// <summary>版本号（分类列表页没有，需从详情页补全，补全后通知界面刷新）</summary>
    public string Version
    {
        get => _version;
        set
        {
            if (_version == value) return;
            _version = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Version)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MetaLabel)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(VersionLabel)));
        }
    }

    public string SizeText { get; set; } = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Category { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string DownloadUrl { get; set; } = "";

    public string SizeLabel => string.IsNullOrWhiteSpace(SizeText) ? "大小未知" : SizeText;

    /// <summary>卡片上的版本标签（详情页抓取失败时显示占位符）。</summary>
    public string VersionLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Version)) return "—";
            return Version.StartsWith('v') ? Version : "v" + Version;
        }
    }

    /// <summary>卡片上显示的「版本 · 大小」，两者都没有时为空。</summary>
    public string MetaLabel
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Version)) parts.Add(Version.StartsWith('v') ? Version : "v" + Version);
            if (!string.IsNullOrWhiteSpace(SizeText)) parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>MapCraft 地图站的数据抓取（站点为静态 HTML，无公开 API）。</summary>
public static class MapCraftService
{
    private const string BaseUrl = "https://zh.mapcraft.me";

    public static readonly (string Name, string Path)[] Categories =
    [
        ("跑酷", "/parkour-maps/"),
        ("冒险", "/adventure-maps/"),
        ("恐怖", "/horror-maps/"),
        ("解谜拼图", "/puzzle-maps/"),
        ("生存", "/survival-maps/"),
        ("游戏", "/game-maps/"),
        ("PVP", "/pvp-maps/"),
        ("CTM", "/ctm-maps/"),
        ("PvE", "/pve-maps/"),
        ("逃生", "/escape-maps/"),
        ("迷宫", "/maze-maps/"),
        ("滴管", "/dropper-maps/"),
        ("城市", "/city-maps/"),
        ("创作", "/creation-maps/"),
        ("房子", "/house-maps/"),
        ("捉迷藏", "/hide-and-seek-maps/"),
        ("热门", "/popular/")
    ];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        return client;
    }

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36";

    // 部分站点会拦截 .NET 默认请求（缺浏览器头 / TLS 指纹），失败时回退到系统自带 curl
    private static async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            return await Http.GetStringAsync(url, ct);
        }
        catch
        {
            var html = await RunCurlAsync(url, ct);
            if (!string.IsNullOrWhiteSpace(html)) return html;
            throw;
        }
    }

    private static async Task<string> RunCurlAsync(string url, CancellationToken ct)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("curl.exe",
                $"-sS -L --max-time 25 -A \"{UserAgent}\" \"{url}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return "";
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>抓取某个分类的一页地图卡片。page 从 1 开始。</summary>
    public static async Task<List<MapCraftItem>> GetListAsync(string categoryPath, int page, CancellationToken ct = default)
    {
        var url = page <= 1
            ? BaseUrl + categoryPath
            : $"{BaseUrl}{categoryPath.TrimEnd('/')}/page/{page}/";

        var html = await GetHtmlAsync(url, ct);
        return ParseCards(html, categoryPath);
    }

    /// <summary>抓取详情页，补全下载直链与文件大小。</summary>
    public static async Task<bool> FillDownloadAsync(MapCraftItem item, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(item.DownloadUrl) && !string.IsNullOrWhiteSpace(item.SizeText)) return true;
        if (string.IsNullOrWhiteSpace(item.DetailUrl)) return false;

        try
        {
            return ApplyDownload(item, await GetHtmlAsync(item.DetailUrl, ct));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>分类列表页不带版本号，进详情页取（<a href="/v1.12/" class="btn btn-secondary …">）。</summary>
    public static async Task<bool> FillVersionAsync(MapCraftItem item, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(item.Version) || string.IsNullOrWhiteSpace(item.DetailUrl)) return false;

        try
        {
            return ApplyVersion(item, await GetHtmlAsync(item.DetailUrl, ct));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>抓详情页的正文，作为完整简介（列表页只有一行摘要）。</summary>
    public static async Task<bool> FillDescriptionAsync(MapCraftItem item, CancellationToken ct = default)
    {
        if (item.DescriptionLoaded) return true;
        if (string.IsNullOrWhiteSpace(item.DetailUrl)) return false;

        try
        {
            return ApplyDescription(item, await GetHtmlAsync(item.DetailUrl, ct));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>列表预取：一次请求补全简介、版本号与文件大小。</summary>
    public static async Task FillDetailAsync(MapCraftItem item, CancellationToken ct = default)
    {
        if (item.DescriptionLoaded || string.IsNullOrWhiteSpace(item.DetailUrl)) return;

        try
        {
            var html = await GetHtmlAsync(item.DetailUrl, ct);
            if (string.IsNullOrWhiteSpace(item.Version)) ApplyVersion(item, html);
            if (string.IsNullOrWhiteSpace(item.DownloadUrl) || string.IsNullOrWhiteSpace(item.SizeText))
                ApplyDownload(item, html);
            ApplyDescription(item, html);
        }
        catch
        {
        }
    }

    private static bool ApplyVersion(MapCraftItem item, string html)
    {
        var match = Regex.Match(html, "href=\"/v(?<v>[0-9][^\"/]*)/\"", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        item.Version = "v" + WebUtility.HtmlDecode(match.Groups["v"].Value).Trim();
        return true;
    }

    private static bool ApplyDownload(MapCraftItem item, string html)
    {
        // 详情页的下载按钮：<a href="/files/xxx.zip" class="btn btn-success btn-lg">名字 (3 mb)</a>
        var match = Regex.Match(html,
            "href=\"(?<url>/files/[^\"]+)\"[^>]*class=\"[^\"]*btn-success[^\"]*\"[^>]*>(?<name>[^<]*)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(html, "href=\"(?<url>/files/[^\"]+)\"", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = BaseUrl + url;
        item.DownloadUrl = url;

        var label = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
        var size = Regex.Match(label, @"\((?<size>[^)]+)\)");
        if (size.Success && string.IsNullOrWhiteSpace(item.SizeText))
            item.SizeText = size.Groups["size"].Value.Trim();

        return true;
    }

    private static bool ApplyDescription(MapCraftItem item, string html)
    {
        var block = html;
        var start = html.IndexOf("id=\"content\"", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var openEnd = html.IndexOf('>', start);
            block = openEnd >= 0 ? html[(openEnd + 1)..] : html[start..];
        }

        var end = block.IndexOf("<div style=\"clear: both", StringComparison.OrdinalIgnoreCase);
        if (end > 0) block = block[..end];

        block = Regex.Replace(block, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, "<(br|/p|/h[1-6]|/li)[^>]*>", "\n", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, "<[^>]+>", " ");
        block = WebUtility.HtmlDecode(block);
        block = Regex.Replace(block, "[ \t]+", " ");
        block = Regex.Replace(block, "\n\\s*\n+", "\n\n").Trim();

        item.DescriptionLoaded = true;
        if (block.Length < 40) return false;

        item.Description = block.Length > 1600 ? block[..1600].TrimEnd() + " …" : block;
        return true;
    }

    /// <summary>把「23 mb」「621 kb」这类文字转成字节数（分片下载需要知道文件大小）。</summary>
    public static long ParseSizeBytes(string sizeText)
    {
        if (string.IsNullOrWhiteSpace(sizeText)) return 0;

        var match = Regex.Match(sizeText, @"([0-9]+(?:\.[0-9]+)?)\s*(gb|mb|kb|b)?", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;

        var value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value.ToLowerInvariant();
        var multiplier = unit switch
        {
            "gb" => 1024L * 1024 * 1024,
            "mb" => 1024L * 1024,
            "kb" => 1024L,
            _ => 1L
        };
        return (long)(value * multiplier);
    }

    /// <summary>按照直链猜一个安全的存档文件夹名。</summary>
    public static string GuessSaveName(MapCraftItem item)
    {
        var name = item.Title;
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(item.DownloadUrl))
        {
            name = Path.GetFileNameWithoutExtension(
                Uri.UnescapeDataString(new Uri(item.DownloadUrl).AbsolutePath.Replace('+', ' ')));
        }

        name = Regex.Replace(name, @"[\\/:*?""<>|]", "-").Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "MapCraft-Map";
        return name.Length > 80 ? name[..80].TrimEnd() : name;
    }

    /// <summary>判断是否 Java 版地图（过滤掉基岩版，基岩版在 Java 启动器里无法使用）。</summary>
    public static bool IsJavaMap(MapCraftItem item)
    {
        var version = item.Version.Trim();
        if (version.Length == 0) return true;
        if (version.Contains("bedrock", StringComparison.OrdinalIgnoreCase)) return false;
        if (version.Contains("pipijama", StringComparison.OrdinalIgnoreCase)) return false;
        // 26.2 / 26.1.2 这类是基岩版新的年份版本号
        return !Regex.IsMatch(version, @"^v?2\d(\.\d+)*$");
    }

    private static List<MapCraftItem> ParseCards(string html, string categoryPath)
    {
        var items = new List<MapCraftItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 卡片结构：<div class="card"> ... <a href="/分类/slug"><img src="缩略图"></a>
        //          <div class="card-header"><a href="...">标题</a></div>
        //          <div class="card-body"> ... <a href="/files/xxx.zip" ...>下载 (3 mb)</a>
        var cardRegex = new Regex(
            "href=\"(?<detail>/[a-z0-9-]+/(?<slug>[^\"?#/]+))\"[^>]*>\\s*<img[^>]*src=\"(?<thumb>[^\"]*)\"",
            RegexOptions.IgnoreCase);

        foreach (Match card in cardRegex.Matches(html))
        {
            var detail = WebUtility.HtmlDecode(card.Groups["detail"].Value);
            if (detail.StartsWith("/v", StringComparison.OrdinalIgnoreCase)
                || detail.StartsWith("/files/", StringComparison.OrdinalIgnoreCase)
                || detail.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
                || detail.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(detail)) continue;

            var blockEnd = html.IndexOf("</div>", card.Index, StringComparison.OrdinalIgnoreCase);
            var block = blockEnd > card.Index
                ? html[card.Index..Math.Min(html.Length, blockEnd + 6)]
                : html[card.Index..Math.Min(html.Length, card.Index + 3000)];

            var item = new MapCraftItem
            {
                DetailUrl = BaseUrl + detail,
                ThumbnailUrl = ResolveUrl(card.Groups["thumb"].Value),
                Title = WebUtility.HtmlDecode(card.Groups["slug"].Value).Replace('-', ' ').Trim(),
                Category = categoryPath
            };

            var title = Regex.Match(block, "card-header[^>]*>\\s*<a[^>]*>(?<t>[^<]*)", RegexOptions.IgnoreCase);
            if (title.Success) item.Title = WebUtility.HtmlDecode(title.Groups["t"].Value).Trim();

            var description = Regex.Match(block, "card-text[^>]*>(?<d>[^<]*)", RegexOptions.IgnoreCase);
            if (description.Success)
                item.Description = WebUtility.HtmlDecode(description.Groups["d"].Value).Trim();

            // 版本标签（卡片里有 /v1.21.10/ 这类链接时才有）
            var version = Regex.Match(block, "href=\"/v(?<v>[0-9][^\"/]*)/\"", RegexOptions.IgnoreCase);
            if (version.Success) item.Version = "v" + WebUtility.HtmlDecode(version.Groups["v"].Value).Trim();

            var download = Regex.Match(block, "href=\"(?<url>/files/[^\"]+)\"", RegexOptions.IgnoreCase);
            if (download.Success)
            {
                item.DownloadUrl = BaseUrl + WebUtility.HtmlDecode(download.Groups["url"].Value);
                var size = Regex.Match(block, "/files/[^\"]+\"[^>]*>[^<]*\\((?<size>[^)]+)\\)", RegexOptions.IgnoreCase);
                if (size.Success) item.SizeText = size.Groups["size"].Value.Trim();
            }

            items.Add(item);
        }

        return items;
    }

    private static string ResolveUrl(string url)
    {
        url = WebUtility.HtmlDecode(url).Trim();
        if (url.Length == 0) return "";
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url;
    }
}
