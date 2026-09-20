using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public class LoaderService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = HttpClients.Create();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuartzLauncher/1.0");
        return client;
    }

    public LoaderService(AppPaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public async Task<List<string>> FabricVersionsAsync(string mcVersion)
    {
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
        var json = await DownloadService.FetchJsonAsync(url);
        var versions = new List<string>();
        foreach (var item in json.EnumerateArray())
        {
            var ver = item.GetProperty("loader").GetProperty("version").GetString();
            if (ver != null) versions.Add(ver);
        }
        return versions;
    }

    public async Task<string> InstallFabricAsync(string mcVersion, string loaderVersion)
    {
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = await DownloadService.FetchJsonAsync(url);
        var versionId = json.GetProperty("id").GetString()!;
        var target = Path.Combine(_paths.VersionsDir, versionId);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, $"{versionId}.json"), json.GetRawText());
        return versionId;
    }

    public async Task<List<string>> QuiltVersionsAsync(string mcVersion)
    {
        var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}";
        var json = await DownloadService.FetchJsonAsync(url);
        return json.EnumerateArray()
            .Select(item => item.GetProperty("loader").GetProperty("version").GetString())
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .ToList();
    }

    public async Task<string> InstallQuiltAsync(string mcVersion, string loaderVersion)
    {
        var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = await DownloadService.FetchJsonAsync(url);
        var versionId = json.GetProperty("id").GetString()!;
        var target = Path.Combine(_paths.VersionsDir, versionId);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, $"{versionId}.json"), json.GetRawText());
        return versionId;
    }

    public async Task<string?> GetFabricApiVersionAsync(string mcVersion)
    {
        try
        {
            var mavenUrl = $"https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/maven-metadata.xml";
            var xml = await _http.GetStringAsync(mavenUrl);
            var root = XDocument.Parse(xml).Root;
            var versions = root?.Descendants("version")
                .Select(e => e.Value)
                .Where(v => v.Contains($"+{mcVersion}"))
                .ToList() ?? new();
            return versions.LastOrDefault();
        }
        catch { return null; }
    }

    public async Task DownloadFabricApiAsync(string mcVersion, string instanceModsDir)
    {
        var apiVersion = await GetFabricApiVersionAsync(mcVersion);
        if (string.IsNullOrEmpty(apiVersion)) return;

        var jarName = $"fabric-api-{apiVersion}.jar";
        var targetPath = Path.Combine(instanceModsDir, jarName);
        if (File.Exists(targetPath)) return;

        var url = $"https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/{apiVersion}/{jarName}";
        var data = await _http.GetByteArrayAsync(url);
        Directory.CreateDirectory(instanceModsDir);
        await File.WriteAllBytesAsync(targetPath, data);
    }

    public async Task<List<string>> ForgeVersionsAsync(string mcVersion)
    {
        var xml = await _http.GetStringAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
        var root = XDocument.Parse(xml).Root;
        var prefix = mcVersion + "-";
        var versions = root?.Descendants("version")
            .Select(e => e.Value)
            .Where(v => v.StartsWith(prefix))
            .Reverse()
            .ToList() ?? new();
        return versions;
    }

    public async Task<List<string>> NeoForgeVersionsAsync(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], out var minor))
            return new List<string>();

        if (minor == 20 && parts.ElementAtOrDefault(2) == "1")
        {
            var legacyXml = await _http.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml");
            var legacyRoot = XDocument.Parse(legacyXml).Root;
            var legacyPrefix = mcVersion + "-";
            return legacyRoot?.Descendants("version")
                .Select(e => e.Value)
                .Where(v => v.StartsWith(legacyPrefix, StringComparison.Ordinal))
                .Reverse()
                .ToList() ?? new List<string>();
        }

        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;
        var neoPrefix = $"{minor}.{patch}.";
        var xml = await _http.GetStringAsync(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
        var root = XDocument.Parse(xml).Root;
        return root?.Descendants("version")
            .Select(e => e.Value)
            .Where(v => v.StartsWith(neoPrefix, StringComparison.Ordinal))
            .Reverse()
            .ToList() ?? new List<string>();
    }

    public async Task<List<(string DisplayName, string FileName)>> OptifineVersionsAsync(string mcVersion)
    {
        var html = await _http.GetStringAsync("https://optifine.net/downloads");
        var versions = new List<(string DisplayName, string FileName)>();

        var entryRegex = new Regex(
            @"<td class='colFile'>([^<]+)</td>\s*<td class='colDownload'><a href=""[^""]*?f=([^&""]+)",
            RegexOptions.Singleline);

        foreach (Match m in entryRegex.Matches(html))
        {
            var displayName = m.Groups[1].Value.Trim();
            var fileName = m.Groups[2].Value.Trim();

            if (fileName.Contains($"_{mcVersion}_") || fileName.Contains($"_{mcVersion}."))
                versions.Add((displayName, fileName));
        }

        return versions;
    }

    public async Task DownloadOptifineAsync(string fileName, string instanceModsDir)
    {
        var url = $"https://optifine.net/adloadx?f={Uri.EscapeDataString(fileName)}&x=5bcf";
        var targetPath = Path.Combine(instanceModsDir, fileName);
        if (File.Exists(targetPath)) return;

        var data = await _http.GetByteArrayAsync(url);
        Directory.CreateDirectory(instanceModsDir);
        await File.WriteAllBytesAsync(targetPath, data);
    }

    public async Task<List<(string DisplayName, string Version)>> LiteLoaderVersionsAsync(string mcVersion)
    {
        var json = await _http.GetStringAsync("http://dl.liteloader.com/versions/versions.json");
        var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
        var versionsObj = doc["versions"]?[mcVersion];
        var versions = new List<(string DisplayName, string Version)>();

        if (versionsObj == null) return versions;

        var artefacts = versionsObj["artefacts"]?["com.mumfrey:liteloader"]?["latest"];
        if (artefacts != null)
        {
            var ver = artefacts["version"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(ver))
                versions.Add(($"{mcVersion} (稳定版)", ver));
        }

        var snapshots = versionsObj["snapshots"]?["com.mumfrey:liteloader"]?["latest"];
        if (snapshots != null)
        {
            var ver = snapshots["version"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(ver))
                versions.Add(($"{mcVersion} (快照版)", ver));
        }

        return versions;
    }

    public async Task<string> InstallForgeAsync(string coordinate, string? javaPath = null)
    {
        if (!Regex.IsMatch(coordinate, @"^[0-9A-Za-z_.+\-]+$"))
            throw new Exception("Invalid Forge version");

        var filename = $"forge-{coordinate}-installer.jar";
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/{filename}";
        return await InstallClientInstallerAsync(filename, url, coordinate.Split('-', 2).Last(), javaPath);
    }

    public async Task<string> InstallNeoForgeAsync(string version, string? javaPath = null)
    {
        if (!Regex.IsMatch(version, @"^[0-9A-Za-z_.+\-]+$"))
            throw new Exception("Invalid NeoForge version");

        var legacy = version.StartsWith("1.20.1-", StringComparison.Ordinal);
        var artifact = legacy ? "forge" : "neoforge";
        var filename = $"{artifact}-{version}-installer.jar";
        var url = $"https://maven.neoforged.net/releases/net/neoforged/{artifact}/{version}/{filename}";
        var candidateFragment = legacy ? version.Split('-', 2).Last() : version;
        return await InstallClientInstallerAsync(filename, url, candidateFragment, javaPath);
    }

    private async Task<string> InstallClientInstallerAsync(string filename, string url, string candidateFragment, string? javaPath)
    {
        var installer = Path.Combine(_paths.CacheDir, filename);
        Directory.CreateDirectory(_paths.CacheDir);

        if (!File.Exists(installer))
        {
            var data = await _http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(installer, data);
        }

        await PreloadInstallerLibrariesAsync(installer);

        var profilePath = Path.Combine(_paths.MinecraftDir, "launcher_profiles.json");
        if (!File.Exists(profilePath))
            File.WriteAllText(profilePath, "{\"profiles\":{},\"settings\":{},\"version\":3}");

        var java = javaPath;
        if (string.IsNullOrEmpty(java)) java = _settings.Data.JavaPath;
        if (string.IsNullOrEmpty(java)) java = "java";

        var psi = new ProcessStartInfo(java, $"-jar \"{installer}\" --installClient \"{_paths.MinecraftDir}\"")
        {
            WorkingDirectory = _paths.CacheDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var processTemp = Path.Combine(_paths.Root, "temp");
        Directory.CreateDirectory(processTemp);
        psi.Environment["TEMP"] = processTemp;
        psi.Environment["TMP"] = processTemp;
        psi.Environment["JAVA_TOOL_OPTIONS"] = $"-Djava.io.tmpdir=\"{processTemp}\"";
        using var process = Process.Start(psi)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var err = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = output + "\n" + err;
            throw new Exception(details.Length > 4000 ? details[^4000..] : details);
        }

        var candidates = Directory.GetDirectories(_paths.VersionsDir, $"*{candidateFragment}*")
            .OrderByDescending(d => Directory.GetCreationTime(d))
            .FirstOrDefault();
        if (candidates == null) throw new Exception("Forge installer did not create version config");

        return new DirectoryInfo(candidates).Name;
    }

    private async Task PreloadInstallerLibrariesAsync(string installer)
    {
        var items = ReadInstallerLibraries(installer);
        if (items.Count == 0) return;

        var workers = Math.Clamp(_settings.Data.DownloadWorkers, 1, 64);
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (item, cancellationToken) =>
            {
                if (DownloadService.ValidFile(item)) return;
                Exception? lastError = null;
                foreach (var url in GetInstallerLibraryUrls(item.Url))
                {
                    try
                    {
                        await DownloadService.DownloadOneAsync(item with { Url = url },
                            ct: cancellationToken, maxAttempts: 3);
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                throw new IOException($"Forge 安装依赖下载失败：{Path.GetFileName(item.Target)}", lastError);
            });
    }

    private List<DownloadItem> ReadInstallerLibraries(string installer)
    {
        var result = new List<DownloadItem>();
        using var archive = ZipFile.OpenRead(installer);
        foreach (var entryName in new[] { "version.json", "install_profile.json" })
        {
            var entry = archive.GetEntry(entryName);
            if (entry == null) continue;
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Array) continue;

            foreach (var library in libraries.EnumerateArray())
            {
                if (!library.TryGetProperty("downloads", out var downloads)
                    || !downloads.TryGetProperty("artifact", out var artifact)
                    || !artifact.TryGetProperty("path", out var pathProperty)
                    || !artifact.TryGetProperty("url", out var urlProperty)) continue;
                var relativePath = pathProperty.GetString() ?? "";
                var url = urlProperty.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(url)
                    || Path.IsPathRooted(relativePath)
                    || relativePath.Split('/', '\\').Any(part => part == "..")) continue;

                var target = Path.GetFullPath(Path.Combine(_paths.LibrariesDir,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                var libraryRoot = Path.GetFullPath(_paths.LibrariesDir) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new DownloadItem(
                    url,
                    target,
                    artifact.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? "" : "",
                    artifact.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
            }
        }

        return result.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<string> GetInstallerLibraryUrls(string url)
    {
        yield return url;
        if (url.StartsWith("https://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase))
            yield return "https://bmclapi2.bangbang93.com/libraries/" +
                         url["https://libraries.minecraft.net/".Length..];
        else if (url.StartsWith("https://maven.minecraftforge.net/", StringComparison.OrdinalIgnoreCase))
            yield return "https://bmclapi2.bangbang93.com/maven/" +
                         url["https://maven.minecraftforge.net/".Length..];
        else if (url.StartsWith("https://maven.neoforged.net/releases/", StringComparison.OrdinalIgnoreCase))
            yield return "https://bmclapi2.bangbang93.com/maven/" +
                         url["https://maven.neoforged.net/releases/".Length..];
    }
}
