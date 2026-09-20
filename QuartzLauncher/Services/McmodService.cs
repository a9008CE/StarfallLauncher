using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public sealed record McmodDetail(
    string Name,
    string OriginalName,
    string Status,
    string SourceType,
    List<McmodInfoItem> InternalInfo,
    string Description,
    List<string> ImageUrls,
    string Html);

public static partial class McmodService
{
    private static readonly HttpClient Http = HttpClients.Create(TimeSpan.FromSeconds(30));

    static McmodService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 QuartzLauncher/1.0");
    }

    public static async Task<List<ModItem>> SearchModsAsync(string keyword, int count = 20)
    {
        try
        {
            var url = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(keyword)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var html = Encoding.UTF8.GetString(bytes);
            return ParseSearchResults(html).Take(count).ToList();
        }
        catch
        {
            return new();
        }
    }

    public static async Task<List<ModItem>> GetPopularModsAsync(int count = 25)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync("https://www.mcmod.cn/modlist.html");
            var html = Encoding.UTF8.GetString(bytes);
            var results = new List<ModItem>();
            foreach (Match blockMatch in PopularBlockRegex().Matches(html))
            {
                var block = blockMatch.Groups["block"].Value;
                var idMatch = ClassIdRegex().Match(block);
                var nameMatch = PopularNameRegex().Match(block);
                if (!idMatch.Success || !nameMatch.Success) continue;
                var summaryMatch = PopularSummaryRegex().Match(block);
                var iconMatch = PopularIconRegex().Match(block);
                var englishMatch = PopularEnglishNameRegex().Match(block);
                var id = idMatch.Groups["id"].Value;
                var pageUrl = $"https://www.mcmod.cn/class/{id}.html";
                var iconUrl = iconMatch.Success ? WebUtility.HtmlDecode(iconMatch.Groups["icon"].Value) : "";
                if (iconUrl.StartsWith("//")) iconUrl = "https:" + iconUrl;
                results.Add(new ModItem
                {
                    Id = id,
                    Name = HtmlDecode(nameMatch.Groups["name"].Value),
                    OriginalName = englishMatch.Success ? HtmlDecode(englishMatch.Groups["name"].Value) : "",
                    Summary = summaryMatch.Success ? HtmlDecode(summaryMatch.Groups["summary"].Value) : "",
                    IconUrl = iconUrl,
                    PageUrl = pageUrl,
                    McmodId = id,
                    McmodPageUrl = pageUrl,
                    Source = ModSource.MCmod
                });
                if (results.Count >= count) break;
            }
            return results;
        }
        catch
        {
            return new();
        }
    }

    public static async Task<string> GetIconUrlAsync(string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return "";
        try
        {
            var bytes = await Http.GetByteArrayAsync(pageUrl);
            var html = Encoding.UTF8.GetString(bytes);
            var match = CoverImageRegex().Match(html);
            if (!match.Success) return "";

            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            return url.StartsWith("//") ? "https:" + url : url;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>从模组页补全支持的加载器与 MC 版本（结果写入本地缓存）。</summary>
    public static async Task FillSupportAsync(ModItem item, CancellationToken ct = default)
    {
        var pageUrl = !string.IsNullOrWhiteSpace(item.McmodPageUrl) ? item.McmodPageUrl : item.PageUrl;
        if (string.IsNullOrWhiteSpace(pageUrl)) return;

        var cacheKey = $"mcmod-support:{pageUrl}";
        var cached = ImageCacheService.GetText(cacheKey);
        if (cached == null)
        {
            try
            {
                var html = await Http.GetStringAsync(pageUrl, ct);
                cached = string.Join(",", ParseSupportedLoaders(html)) + "|" +
                         string.Join(",", ParseSupportedVersions(html));
                ImageCacheService.SetText(cacheKey, cached);
            }
            catch
            {
                return;
            }
        }

        var parts = cached.Split('|');
        if (parts.Length != 2) return;

        var loaders = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var versions = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (loaders.Count > 0) item.Loaders = loaders;
        if (versions.Count > 0) item.Versions = versions;
    }

    private static List<string> ParseSupportedLoaders(string html)
    {
        var result = new List<string>();
        var match = Regex.Match(html, "运作方式[:：](?<value>.*?)</li>", RegexOptions.Singleline);
        if (!match.Success) return result;

        foreach (Match link in Regex.Matches(match.Groups["value"].Value, "<a[^>]*>(?<name>[^<]+)</a>"))
        {
            var name = WebUtility.HtmlDecode(link.Groups["name"].Value).Trim();
            if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }
        return result;
    }

    private static List<string> ParseSupportedVersions(string html)
    {
        var result = new List<string>();
        var marker = html.IndexOf("支持的MC版本", StringComparison.Ordinal);
        if (marker < 0) return result;

        var block = html[marker..Math.Min(html.Length, marker + 4000)];
        foreach (Match match in Regex.Matches(block, "mcver=(?<version>[^\"&]+)\""))
        {
            var version = WebUtility.HtmlDecode(match.Groups["version"].Value).Trim();
            if (version.Length == 0 || result.Contains(version, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(version);
            if (result.Count >= 6) break;
        }
        return result;
    }

    public static async Task<McmodDetail> GetDetailAsync(string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return EmptyDetail();
        try
        {
            var html = await Http.GetStringAsync(pageUrl);
            var title = TitleRegex().Match(html);
            var name = title.Success ? HtmlDecode(title.Groups["name"].Value) : "";
            var originalName = title.Success ? HtmlDecode(title.Groups["original"].Value) : "";
            var statusMatch = StatusRegex().Match(html);
            var sourceMatch = SourceTypeRegex().Match(html);
            var info = ParseInternalInfo(html);
            var match = DescriptionRegex().Match(html);
            if (!match.Success)
                return new(name, originalName, CleanInlineText(statusMatch.Groups["value"].Value),
                    CleanInlineText(sourceMatch.Groups["value"].Value), info, "", new(), "");

            var rawContent = match.Groups["content"].Value;
            var imageUrls = ImageRegex().Matches(rawContent)
                .Select(image => NormalizeResourceUrl(WebUtility.HtmlDecode(image.Groups["url"].Value.Trim()), pageUrl))
                .Where(url => !string.IsNullOrEmpty(url)
                    && !url.Contains("loading", StringComparison.OrdinalIgnoreCase)
                    && !url.Contains("loadfail", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            var content = ScriptRegex().Replace(rawContent, "");
            content = TitleTagRegex().Replace(content, "\n$1\n");
            content = BlockTagRegex().Replace(content, "\n");
            content = HtmlTagRegex().Replace(content, "");
            content = WebUtility.HtmlDecode(content).Replace('\u00a0', ' ');
            var lines = content.Split('\n')
                .Select(line => WhitespaceRegex().Replace(line, " ").Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            var description = string.Join(Environment.NewLine + Environment.NewLine, lines);
            return new(name, originalName,
                CleanInlineText(statusMatch.Groups["value"].Value),
                CleanInlineText(sourceMatch.Groups["value"].Value),
                info, description, imageUrls, BuildOverviewHtml(rawContent, pageUrl));
        }
        catch
        {
            return EmptyDetail();
        }
    }

    private static McmodDetail EmptyDetail() => new("", "", "", "", new(), "", new(), "");

    public static async Task<string> GetDescriptionAsync(string pageUrl) =>
        (await GetDetailAsync(pageUrl)).Description;

    public static async Task<(string CurseForgeSlug, string ModrinthSlug)> GetOfficialProjectSlugsAsync(string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return ("", "");
        try
        {
            var bytes = await Http.GetByteArrayAsync(pageUrl);
            var html = Encoding.UTF8.GetString(bytes);
            var curseForgeTarget = CurseForgeTargetRegex().Match(html);
            var modrinthTarget = ModrinthTargetRegex().Match(html);
            var curseForge = CurseForgeLinkRegex().Match(DecodeTarget(curseForgeTarget));
            var modrinth = ModrinthLinkRegex().Match(DecodeTarget(modrinthTarget));
            return (
                curseForge.Success ? curseForge.Groups["slug"].Value : "",
                modrinth.Success ? modrinth.Groups["slug"].Value : "");
        }
        catch
        {
            return ("", "");
        }
    }

    private static string DecodeTarget(Match match)
    {
        if (!match.Success) return "";
        try
        {
            var encoded = match.Groups["target"].Value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeResourceUrl(string url, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("//")) url = "https:" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
            return absolute.AbsoluteUri;
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
            && Uri.TryCreate(page, url, out var relative)
            && relative.Scheme is "http" or "https")
            return relative.AbsoluteUri;
        return "";
    }

    private static List<McmodInfoItem> ParseInternalInfo(string html)
    {
        var block = InfoBlockRegex().Match(html);
        if (!block.Success) return new();

        var result = new List<McmodInfoItem>();
        foreach (Match item in InfoItemRegex().Matches(block.Groups["content"].Value))
        {
            var text = CleanInlineText(item.Groups["value"].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var separator = text.IndexOf('：');
            if (separator < 0) separator = text.IndexOf(':');
            if (separator > 0 && separator < text.Length - 1)
            {
                result.Add(new McmodInfoItem
                {
                    Label = text[..separator].Trim(),
                    Value = text[(separator + 1)..].Trim()
                });
            }
        }
        return result;
    }

    private static string CleanInlineText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = BlockTagRegex().Replace(html, " ");
        text = HtmlTagRegex().Replace(text, "");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(text).Replace('\u00a0', ' '), " ").Trim();
    }

    private static string BuildOverviewHtml(string rawContent, string pageUrl)
    {
        var content = ScriptRegex().Replace(rawContent, "");
        content = UnsafeAttributeRegex().Replace(content, "");
        content = ImgTagRegex().Replace(content, match =>
        {
            var imageUrl = ImageRegex().Matches(match.Groups["attrs"].Value)
                .Select(image => NormalizeResourceUrl(WebUtility.HtmlDecode(image.Groups["url"].Value.Trim()), pageUrl))
                .FirstOrDefault(url => !string.IsNullOrEmpty(url)
                    && !url.Contains("loading", StringComparison.OrdinalIgnoreCase)
                    && !url.Contains("loadfail", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(imageUrl)
                ? ""
                : $"<img src=\"{WebUtility.HtmlEncode(imageUrl)}\" />";
        });
        content = Regex.Replace(content,
            @"<(?:iframe|object|embed|video)\b[^>]*>.*?</(?:iframe|object|embed|video)>",
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return $$"""
<!doctype html>
<html><head><meta charset="utf-8"><style>
html, body { margin: 0; padding: 0; background: transparent; overflow: hidden; }
body { color: #e8edf2; font-family: "Microsoft YaHei", "Segoe UI", sans-serif; font-size: 14px; line-height: 1.8; }
.mcmod-overview { padding: 2px 4px 18px; }
p { margin: 0 0 15px; }
ul, ol { margin: 0 0 15px 22px; padding: 0; }
li { margin: 0 0 6px; }
a { color: #4db6ff; }
img { display: block; max-width: 100%; height: auto; margin: 10px auto 18px; border-radius: 8px; }
.common-text-title { display: block; margin: 24px 0 10px; padding-left: 10px; border-left: 3px solid #4db6ff; color: #f5f7fa; font-size: 17px; font-weight: 600; line-height: 1.5; }
.figure { display: block; }
table { max-width: 100%; border-collapse: collapse; }
td, th { border: 1px solid #3a4652; padding: 6px 8px; }
</style></head><body><main class="mcmod-overview">{{content}}</main></body></html>
""";
    }

    private static List<ModItem> ParseSearchResults(string html)
    {
        var results = new List<ModItem>();
        var matches = ResultRegex().Matches(html);

        foreach (Match match in matches)
        {
            var pageUrl = match.Groups["url"].Value;
            if (pageUrl.StartsWith("//")) pageUrl = "https:" + pageUrl;

            var item = new ModItem
            {
                Id = match.Groups["id"].Value,
                Name = HtmlDecode(match.Groups["name"].Value),
                OriginalName = HtmlDecode(match.Groups["name"].Value),
                Summary = HtmlDecode(match.Groups["summary"].Value),
                McmodPageUrl = pageUrl,
                McmodId = match.Groups["id"].Value,
                PageUrl = pageUrl,
                Source = ModSource.MCmod
            };

            if (item.Summary.Length > 200)
                item.Summary = item.Summary[..200] + "...";

            if (!string.IsNullOrEmpty(item.Name) && !string.IsNullOrEmpty(item.PageUrl))
                results.Add(item);
        }

        return results;
    }

    private static string HtmlDecode(string text)
    {
        var withoutTags = HtmlTagRegex().Replace(text, "");
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    [GeneratedRegex(@"<div class=""result-item"">.*?<div class=""head"">.*?<a[^>]*href=""(?<url>(?:https?:)?//(?:www\.)?mcmod\.cn/class/(?<id>\d+)\.html)""[^>]*>(?<name>.*?)</a>\s*</div>\s*<div class=""body"">(?<summary>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ResultRegex();

    [GeneratedRegex(@"<div\s+class=[""']modlist-block[""'][^>]*>(?<block>.*?)(?=<div\s+class=[""']modlist-block[""']|<div\s+class=[""'](?:pagination|common-page))", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PopularBlockRegex();

    [GeneratedRegex(@"href=[""']/class/(?<id>\d+)\.html[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ClassIdRegex();

    [GeneratedRegex(@"class=[""']intro-content[""'][^>]*>\s*<span>(?<summary>.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PopularSummaryRegex();

    [GeneratedRegex(@"<div\s+class=[""']cover[""'][^>]*>.*?<img[^>]*\bsrc=[""'](?<icon>[^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PopularIconRegex();

    [GeneratedRegex(@"<p\s+class=[""']name[""'][^>]*>\s*<a[^>]*>(?<name>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PopularNameRegex();

    [GeneratedRegex(@"<p\s+class=[""']ename[""'][^>]*>\s*<a[^>]*>(?<name>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PopularEnglishNameRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[""'][^""']*\bclass-cover-image\b[^""']*[""'][^>]*>\s*<img\b[^>]*\bsrc=[""'](?<url>[^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CoverImageRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[""'][^""']*\bclass-title\b[^""']*[""'][^>]*>.*?<h3\b[^>]*>(?<name>.*?)</h3>.*?<h4\b[^>]*>(?<original>.*?)</h4>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[""'][^""']*\bclass-status\b[^""']*[""'][^>]*>(?<value>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StatusRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[""'][^""']*\bclass-source\b[^""']*[""'][^>]*>(?<value>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SourceTypeRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[""'][^""']*\bclass-info-left\b[^""']*[""'][^>]*>(?<content>.*?)(?=<div\b[^>]*class=[""'][^""']*\bclass-info-right\b)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InfoBlockRegex();

    [GeneratedRegex(@"<li\b[^>]*>(?<value>.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InfoItemRegex();

    [GeneratedRegex(@"<li\b(?=[^>]*\bdata-id\s*=\s*[""']1[""'])(?=[^>]*\bclass\s*=\s*[""'][^""']*\btext-area\b[^""']*[""'])(?=[^>]*\bclass\s*=\s*[""'][^""']*\bcommon-text\b[^""']*[""'])(?=[^>]*\bclass\s*=\s*[""'][^""']*\bfont14\b[^""']*[""'])[^>]*>(?<content>.*?)(?=<li\b[^>]*\bdata-id\s*=\s*[""']2[""']|</ul>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();
    [GeneratedRegex(@"<span\b[^>]*\bclass\s*=\s*[""'][^""']*\bcommon-text-title\b[^""']*[""'][^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleTagRegex();
    [GeneratedRegex(@"(?:data-src|data-original|src)\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();
    [GeneratedRegex(@"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTagRegex();
    [GeneratedRegex(@"\s+on[a-zA-Z]+\s*=\s*[""'][^""']*[""']", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeAttributeRegex();
    [GeneratedRegex(@"<(?:script|style)\b[^>]*>.*?</(?:script|style)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"</?(?:p|div|br|li|h[1-6]|tr|td|ul|ol)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"data-original-title=[""']CurseForge[""'][^>]*href=[""'](?://)?link\.mcmod\.cn/target/(?<target>[a-z0-9_=/+-]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CurseForgeTargetRegex();

    [GeneratedRegex(@"data-original-title=[""']Modrinth[""'][^>]*href=[""'](?://)?link\.mcmod\.cn/target/(?<target>[a-z0-9_=/+-]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ModrinthTargetRegex();

    [GeneratedRegex(@"https?://(?:www\.)?(?:curseforge\.com/minecraft/mc-mods|minecraft\.curseforge\.com/projects)/(?<slug>[a-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CurseForgeLinkRegex();

    [GeneratedRegex(@"https?://(?:www\.)?modrinth\.com/mod/(?<slug>[a-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModrinthLinkRegex();
}
