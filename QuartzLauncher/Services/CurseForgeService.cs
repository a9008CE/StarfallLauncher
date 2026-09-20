using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public static class CurseForgeService
{
    public sealed record PackFile(string DownloadUrl, string RelativePath, string Sha1, long Size);
    private static readonly HttpClient Http = HttpClients.Create(TimeSpan.FromSeconds(30));
    private static readonly HttpClient PublicHttp = HttpClients.Create(TimeSpan.FromSeconds(30));
    private const string BaseUrl = "https://api.curseforge.com";
    private const string PublicBaseUrl = "https://api.curse.tools/v1";

    static CurseForgeService()
    {
        PublicHttp.DefaultRequestHeaders.UserAgent.ParseAdd("QuartzLauncher/1.0");
    }
    private static string ApiKey
    {
        get
        {
            try { return App.Settings.Data.CurseForgeApiKey.Trim(); }
            catch { return ""; }
        }
    }

    public static bool IsConfigured => true;

    public static async Task<List<ModItem>> SearchModsAsync(string keyword, string? gameVersion = null, string? loader = null, int count = 20, int classId = 6, string? categoryId = null, int offset = 0)
    {
        try
        {
            var path = $"/mods/search?gameId=432&classId={classId}&searchFilter={Uri.EscapeDataString(keyword)}&pageSize={count}";
            if (!string.IsNullOrEmpty(gameVersion))
                path += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
            if (!string.IsNullOrEmpty(loader))
                path += $"&modLoaderType={GetLoaderId(loader)}";
            if (!string.IsNullOrEmpty(categoryId))
                path += $"&categoryId={Uri.EscapeDataString(categoryId)}";
            if (offset > 0)
                path += $"&index={offset}";

            var obj = JObject.Parse(await GetJsonAsync(path));
            var data = obj["data"] ?? new JArray();
            return data.Select(ParseModItem).ToList();
        }
        catch
        {
            return new();
        }
    }

    public static async Task<ModItem?> GetModAsync(long modId)
    {
        try
        {
            var obj = JObject.Parse(await GetJsonAsync($"/mods/{modId}"));
            return ParseModItem(obj["data"]!);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<ModItem?> GetModBySlugAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        try
        {
            var path = $"/mods/search?gameId=432&classId=6&searchFilter={Uri.EscapeDataString(slug)}&pageSize=50";
            var obj = JObject.Parse(await GetJsonAsync(path));
            var data = obj["data"] ?? new JArray();
            return data.Select(ParseModItem)
                .FirstOrDefault(mod => string.Equals(mod.Slug, slug, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<ModVersionItem>> GetVersionsAsync(long modId, string? gameVersion = null)
    {
        try
        {
            var path = $"/mods/{modId}/files?pageSize=10000";
            if (!string.IsNullOrEmpty(gameVersion))
                path += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

            var obj = JObject.Parse(await GetJsonAsync(path));
            var data = (obj["data"] as JArray) ?? new JArray();
            var versions = new List<ModVersionItem>();

            foreach (var v in data)
            {
                var gameVersions = v["gameVersions"]?.ToObject<List<string>>() ?? new();
                var fileName = v["fileName"]?.ToString() ?? "";
                var loaderType = int.TryParse(v["modLoader"]?.ToString(), out var parsedLoaderType)
                    ? parsedLoaderType
                    : int.TryParse(v["modLoaderType"]?.ToString(), out var fallbackLoaderType)
                        ? fallbackLoaderType
                        : 0;
                var loaders = GetFileLoaders(loaderType, fileName);
                var dependencies = (v["dependencies"] as JArray)?.OfType<JObject>()
                    .Select(node => new ModDependency
                    {
                        ProjectId = node.Value<string>("modId") ?? node.Value<string>("projectId") ?? "",
                        ProjectName = node.Value<string>("fileName") ?? "",
                        Type = node.Value<int?>("relationType") switch
                        {
                            1 => "embedded",
                            2 => "optional",
                            3 => "required",
                            4 => "tool",
                            5 => "incompatible",
                            _ => "required"
                        },
                        Source = ModSource.CurseForge
                    }).ToArray();
                versions.Add(new ModVersionItem
                {
                    ProjectId = modId.ToString(),
                    Id = v["id"]?.ToString() ?? "",
                    Name = v["displayName"]?.ToString() ?? "",
                    VersionNumber = v["version"]?.ToString() ?? "",
                    GameVersion = gameVersions.FirstOrDefault() ?? "",
                    GameVersions = gameVersions,
                    Loader = loaders.FirstOrDefault() ?? "",
                    Loaders = loaders,
                    FileSize = v["fileLength"]?.ToObject<long>() ?? 0,
                    DownloadUrl = v["downloadUrl"]?.ToString() ?? "",
                    FileName = fileName,
                    DateUploaded = ParseTimestamp(v["fileDate"]?.ToString()),
                    Source = ModSource.CurseForge,
                    Dependencies = dependencies?.ToList() ?? new()
                });
            }
            return versions;
        }
        catch
        {
            return new();
        }
    }

    public static async Task<string> GetDownloadUrlAsync(long modId, long fileId)
    {
        try
        {
            var obj = JObject.Parse(await GetJsonAsync($"/mods/{modId}/files/{fileId}/download-url"));
            var url = obj["data"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(url)) EnsureCdnUrl(url);
            return url;
        }
        catch
        {
            return "";
        }
    }

    public static async Task<PackFile> GetPackFileAsync(long modId, long fileId)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            try
            {
                return await GetOfficialPackFileAsync(modId, fileId);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
            }
        }
        return await GetPublicPackFileAsync(modId, fileId);
    }

    private static async Task<PackFile> GetOfficialPackFileAsync(long modId, long fileId)
    {
        using var fileRequest = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/mods/{modId}/files/{fileId}");
        fileRequest.Headers.Add("x-api-key", ApiKey);
        using var fileResponse = await Http.SendAsync(fileRequest);
        fileResponse.EnsureSuccessStatusCode();
        var file = JObject.Parse(await fileResponse.Content.ReadAsStringAsync())["data"] as JObject
                   ?? throw new IOException($"CurseForge 未返回文件 {fileId} 的信息。");

        var url = file.Value<string>("downloadUrl") ?? "";
        if (string.IsNullOrWhiteSpace(url)) url = await GetDownloadUrlAsync(modId, fileId);
        if (string.IsNullOrWhiteSpace(url))
            throw new IOException($"CurseForge 文件 {fileId} 禁止第三方分发，无法自动导入。");
        EnsureCdnUrl(url);
        var fileName = file.Value<string>("fileName") ?? $"{fileId}.jar";
        var sha1 = (file["hashes"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(hash => hash.Value<int?>("algo") == 1)?.Value<string>("value") ?? "";

        using var projectRequest = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/mods/{modId}");
        projectRequest.Headers.Add("x-api-key", ApiKey);
        using var projectResponse = await Http.SendAsync(projectRequest);
        projectResponse.EnsureSuccessStatusCode();
        var project = JObject.Parse(await projectResponse.Content.ReadAsStringAsync())["data"] as JObject;
        var folder = project?.Value<int?>("classId") switch
        {
            12 => "resourcepacks",
            6552 => "shaderpacks",
            _ => "mods"
        };
        return new PackFile(url, $"{folder}/{fileName}", sha1, file.Value<long?>("fileLength") ?? 0);
    }

    private static async Task<PackFile> GetPublicPackFileAsync(long modId, long fileId)
    {
        using var response = await PublicHttp.GetAsync($"{PublicBaseUrl}/mods/{modId}/files/{fileId}");
        response.EnsureSuccessStatusCode();
        var fileResponse = JObject.Parse(await response.Content.ReadAsStringAsync());
        var file = (fileResponse["data"] as JObject) ?? fileResponse;
        var fileName = file.Value<string>("fileName") ?? file.Value<string>("name");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new IOException($"公开 CurseForge 数据源没有文件 {fileId} 的文件名。");
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException($"CurseForge 文件 {fileId} 返回了非法文件名。");

        using var projectResponse = await PublicHttp.GetAsync($"{PublicBaseUrl}/mods/{modId}");
        projectResponse.EnsureSuccessStatusCode();
        var projectResponseJson = JObject.Parse(await projectResponse.Content.ReadAsStringAsync());
        var project = (projectResponseJson["data"] as JObject) ?? projectResponseJson;
        var projectType = project.Value<int?>("classId") switch
        {
            12 => "Resource Packs",
            6552 => "Shaders",
            _ => "Mods"
        };
        var folder = projectType.Contains("resource", StringComparison.OrdinalIgnoreCase)
            ? "resourcepacks"
            : projectType.Contains("shader", StringComparison.OrdinalIgnoreCase) ? "shaderpacks" : "mods";
        var url = file.Value<string>("downloadUrl") ?? "";
        if (string.IsNullOrWhiteSpace(url))
            throw new IOException($"CurseForge 文件 {fileId} 没有公开下载地址。");
        EnsureCdnUrl(url);
        var sha1 = (file["hashes"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(hash => hash.Value<int?>("algo") == 1)?.Value<string>("value") ?? "";
        return new PackFile(url, $"{folder}/{fileName}", sha1, file.Value<long?>("fileLength") ?? 0);
    }

    private static void EnsureCdnUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("CurseForge 返回了不受信任的下载地址。");
    }

    public static async Task<(List<ModItem> Items, int Total)> SearchPageAsync(string keyword, string? gameVersion, string? loader, int count, int classId, string? categoryId, int offset)
    {
        try
        {
            var path = $"/mods/search?gameId=432&classId={classId}&searchFilter={Uri.EscapeDataString(keyword)}&pageSize={count}&index={offset}";
            if (!string.IsNullOrEmpty(gameVersion))
                path += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
            if (!string.IsNullOrEmpty(loader))
                path += $"&modLoaderType={GetLoaderId(loader)}";
            if (!string.IsNullOrEmpty(categoryId))
                path += $"&categoryId={Uri.EscapeDataString(categoryId)}";

            var obj = JObject.Parse(await GetJsonAsync(path));
            var data = (obj["data"] as JArray) ?? new JArray();
            var total = obj["pagination"]?["totalCount"]?.ToObject<int>() ?? 0;
            return (data.Select(ParseModItem).ToList(), total);
        }
        catch
        {
            return (new(), 0);
        }
    }

    public static async Task<(List<ModItem> Items, int Total)> GetPopularPageAsync(int count, int classId, string? categoryId, int offset, string? gameVersion = null)
    {
        try
        {
            var path = $"/mods/search?gameId=432&classId={classId}&sortOrder=desc&sortType=6&pageSize={count}&index={offset}";
            if (!string.IsNullOrEmpty(gameVersion))
                path += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
            if (!string.IsNullOrEmpty(categoryId))
                path += $"&categoryId={Uri.EscapeDataString(categoryId)}";

            var obj = JObject.Parse(await GetJsonAsync(path));
            var data = (obj["data"] as JArray) ?? new JArray();
            var total = obj["pagination"]?["totalCount"]?.ToObject<int>() ?? 0;
            return (data.Select(ParseModItem).ToList(), total);
        }
        catch
        {
            return (new(), 0);
        }
    }

    public static async Task<List<ModItem>> GetPopularModsAsync(int count = 20, int classId = 6, string? categoryId = null, int offset = 0)
    {
        try
        {
            var path = $"/mods/search?gameId=432&classId={classId}&sortOrder=desc&sortType=6&pageSize={count}";
            if (!string.IsNullOrEmpty(categoryId))
                path += $"&categoryId={Uri.EscapeDataString(categoryId)}";
            if (offset > 0)
                path += $"&index={offset}";

            var obj = JObject.Parse(await GetJsonAsync(path));
            var data = obj["data"] ?? new JArray();
            return data.Select(ParseModItem).ToList();
        }
        catch
        {
            return new();
        }
    }

    private static int GetLoaderId(string loader) => loader.ToLower() switch
    {
        "fabric" => 4,
        "forge" => 1,
        "liteloader" => 3,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0
    };

    private static async Task<string> GetJsonAsync(string path)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1" + path);
                request.Headers.Add("x-api-key", ApiKey);
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                // The public mirror keeps the client usable when the official API rejects the key or is unavailable.
            }
        }

        using var publicResponse = await PublicHttp.GetAsync(PublicBaseUrl + path);
        publicResponse.EnsureSuccessStatusCode();
        return await publicResponse.Content.ReadAsStringAsync();
    }

    private static List<string> GetFileLoaders(int loader, string fileName = "")
    {
        if (loader is 1 or 3 or 4 or 5 or 6)
        {
            return loader switch
            {
                1 => new() { "Forge" },
                3 => new() { "LiteLoader" },
                4 => new() { "Fabric" },
                5 => new() { "Quilt" },
                6 => new() { "NeoForge" },
                _ => new()
            };
        }

        var name = fileName.ToLowerInvariant();
        var result = new List<string>();
        if (name.Contains("neoforge"))
            result.Add("NeoForge");
        else if (name.Contains("forge"))
            result.Add("Forge");
        else if (name.Contains("fabric"))
            result.Add("Fabric");
        else if (name.Contains("quilt"))
            result.Add("Quilt");
        else if (name.Contains("liteloader"))
            result.Add("LiteLoader");
        return result;
    }

    private static long ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var date) ? date.ToUnixTimeSeconds() : 0;

    private static ModItem ParseModItem(JToken v)
    {
        var authors = v["authors"]?.ToObject<List<JObject>>() ?? new();
        var links = v["links"] ?? new JObject();
        var latestFiles = v["latestFiles"]?.ToObject<List<JObject>>() ?? new();
        var versionTags = latestFiles
            .SelectMany(f => f["gameVersions"]?.ToObject<List<string>>() ?? new())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModItem
        {
            Id = v["id"]?.ToString() ?? "",
            Slug = v["slug"]?.ToString() ?? "",
            Name = v["name"]?.ToString() ?? "",
            Summary = v["summary"]?.ToString() ?? "",
            Description = v["description"]?.ToString() ?? "",
            Downloads = v["downloadCount"]?.ToObject<long>() ?? 0,
            IconUrl = v["logo"]?["thumbnailUrl"]?.ToString() ?? "",
            PageUrl = links["websiteUrl"]?.ToString() ?? "",
            Authors = authors.Select(a => a["name"]?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList(),
            Categories = v["categories"]?.ToObject<List<JObject>>()?.Select(c => c["name"]?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new(),
            Versions = versionTags.Where(IsMinecraftVersion).ToList(),
            Loaders = versionTags.Where(IsLoaderName)
                .Concat(latestFiles.SelectMany(f => GetFileLoaders(f["modLoader"]?.ToObject<int>() ?? 0, f["fileName"]?.ToString() ?? "")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Source = ModSource.CurseForge
        };
    }

    private static bool IsMinecraftVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^\d+(\.\d+)+$");

    private static bool IsLoaderName(string value) => value.ToLowerInvariant() is
        "forge" or "fabric" or "neoforge" or "quilt" or "liteloader" or "rift";
}
