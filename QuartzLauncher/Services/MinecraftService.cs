using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuartzLauncher.Models;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace QuartzLauncher.Services;

public class MinecraftService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public MinecraftService(AppPaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
    }

    // 官方清单拿不到时用镜像（PCL2 同款），保证新版本能及时出现
    private const string ManifestMirrorUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    private static readonly TimeSpan ManifestMaxAge = TimeSpan.FromHours(3);

    public JsonElement Manifest(bool refresh = false)
    {
        var target = Path.Combine(_paths.MetadataDir, "version_manifest_v2.json");
        if (!refresh && IsManifestFresh(target))
        {
            var raw = File.ReadAllText(target);
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }

        Directory.CreateDirectory(_paths.MetadataDir);
        try
        {
            var data = DownloadService.FetchJsonAsync(ManifestUrl).GetAwaiter().GetResult();
            File.WriteAllText(target, data.GetRawText());
            return data;
        }
        catch when (File.Exists(target))
        {
            // 联网失败时退回本地缓存
            return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(target));
        }
    }

    public async Task<JsonElement> ManifestAsync(bool refresh = false)
    {
        var target = Path.Combine(_paths.MetadataDir, "version_manifest_v2.json");
        if (!refresh && IsManifestFresh(target))
        {
            var raw = await File.ReadAllTextAsync(target);
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }

        Directory.CreateDirectory(_paths.MetadataDir);
        try
        {
            var data = await DownloadService.FetchJsonAsync(ManifestUrl);
            await File.WriteAllTextAsync(target, data.GetRawText());
            return data;
        }
        catch
        {
            // 官方源失败 → 试 BMCLAPI 镜像
            try
            {
                var mirror = await DownloadService.FetchJsonAsync(ManifestMirrorUrl);
                await File.WriteAllTextAsync(target, mirror.GetRawText());
                return mirror;
            }
            catch when (File.Exists(target))
            {
                var raw = await File.ReadAllTextAsync(target);
                return JsonSerializer.Deserialize<JsonElement>(raw);
            }
        }
    }

    // 缓存超过 ManifestMaxAge 即视为过期，自动重新拉取（否则新版本不会出现）
    private static bool IsManifestFresh(string path)
        => File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ManifestMaxAge;

    public List<Dictionary<string, object>> AvailableVersions(bool includeSnapshots = false)
    {
        var manifest = Manifest();
        var versions = manifest.GetProperty("versions");
        var result = new List<Dictionary<string, object>>();
        foreach (var v in versions.EnumerateArray())
        {
            var type = v.GetProperty("type").GetString();
            if (type == "release" || (includeSnapshots && type == "snapshot"))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(v.GetRawText()) ?? new();
                result.Add(dict);
            }
        }
        return result;
    }

    public async Task InstallVersionAsync(string versionId, Action<string, int, int>? progress = null)
    {
        var downloads = await PrepareVersionDownloadsAsync(versionId);
        await DownloadService.DownloadManyAsync(downloads, _settings.Data.DownloadWorkers, progress);
    }

    public async Task<List<DownloadItem>> PrepareInstallAsync(string versionId)
    {
        return await PrepareVersionDownloadsAsync(versionId);
    }

    public async Task<List<DownloadItem>> PrepareLaunchAsync(Instance instance)
    {
        var baseVersion = string.IsNullOrWhiteSpace(instance.McVersion)
            ? BaseVersionId(instance.VersionId)
            : instance.McVersion;
        var downloads = await PrepareVersionDownloadsAsync(baseVersion);

        var profilePath = Path.Combine(_paths.VersionsDir, instance.VersionId, $"{instance.VersionId}.json");
        if (File.Exists(profilePath))
        {
            var resolved = ResolveVersion(instance.VersionId);
            CollectLibraryDownloads(resolved, downloads);
            CollectLoggingDownload(resolved, downloads);
        }

        var deduped = DeduplicateDownloads(downloads);

        // 只返回真正缺失或损坏的文件：资源完整时返回空列表，
        // 启动流程就不会跳转到下载管理页，也不会创建多余的修复任务。
        return await Task.Run(() => deduped
            .AsParallel()
            .AsOrdered()
            .Where(item => !DownloadService.ValidFile(item))
            .ToList());
    }

    private async Task<List<DownloadItem>> PrepareVersionDownloadsAsync(string versionId)
    {
        // 启动速度优化：本地已有版本 JSON 时直接离线解析，不再请求 Mojang
        var existingJson = Path.Combine(_paths.VersionsDir, versionId, $"{versionId}.json");
        if (File.Exists(existingJson))
        {
            try
            {
                var localJson = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(existingJson));
                var localDownloads = new List<DownloadItem>();
                CollectClientDownload(localJson, versionId, localDownloads);
                CollectLibraryDownloads(localJson, localDownloads);
                CollectLoggingDownload(localJson, localDownloads);
                await CollectAssetDownloadsAsync(localJson, localDownloads);
                return DeduplicateDownloads(localDownloads);
            }
            catch
            {
                // 本地 JSON 损坏时回退到联网获取
            }
        }

        var manifest = await ManifestAsync();
        JsonElement? entry = null;
        foreach (var v in manifest.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == versionId) { entry = v; break; }
        }
        if (entry == null)
        {
            manifest = await ManifestAsync(true);
            foreach (var v in manifest.GetProperty("versions").EnumerateArray())
            {
                if (v.GetProperty("id").GetString() == versionId) { entry = v; break; }
            }
        }
        if (entry == null) throw new Exception($"Cannot find Minecraft {versionId}");

        var versionDir = Path.Combine(_paths.VersionsDir, versionId);
        Directory.CreateDirectory(versionDir);

        var versionMeta = entry.Value;
        var jsonPath = Path.Combine(versionDir, $"{versionId}.json");
        var json = await DownloadService.FetchJsonAsync(versionMeta.GetProperty("url").GetString()!);
        File.WriteAllText(jsonPath, json.GetRawText());

        var downloads = new List<DownloadItem>();
        CollectClientDownload(json, versionId, downloads);
        CollectLibraryDownloads(json, downloads);
        CollectLoggingDownload(json, downloads);

        await CollectAssetDownloadsAsync(json, downloads);

        return DeduplicateDownloads(downloads);
    }

    public JsonElement ResolveVersion(string versionId, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>();
        if (!seen.Add(versionId)) throw new Exception("Circular version inheritance");

        var jsonPath = Path.Combine(_paths.VersionsDir, versionId, $"{versionId}.json");
        var child = JObject.Parse(File.ReadAllText(jsonPath));
        var parentId = child.Value<string>("inheritsFrom");
        if (!string.IsNullOrEmpty(parentId))
        {
            var parent = JObject.Parse(ResolveVersion(parentId, seen).GetRawText());
            parent.Merge(child, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Concat,
                MergeNullValueHandling = MergeNullValueHandling.Ignore
            });
            return JsonSerializer.Deserialize<JsonElement>(parent.ToString(Formatting.None));
        }
        return JsonSerializer.Deserialize<JsonElement>(child.ToString(Formatting.None));
    }

    public string BaseVersionId(string versionId)
    {
        var current = versionId;
        var seen = new HashSet<string>();
        while (seen.Add(current))
        {
            var jsonPath = Path.Combine(_paths.VersionsDir, current, $"{current}.json");
            if (!File.Exists(jsonPath)) return current;
            var meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(jsonPath)) ?? new();
            if (!meta.TryGetValue("inheritsFrom", out var parentObj) || parentObj is not string || string.IsNullOrEmpty(parentObj as string))
                return current;
            current = (string)parentObj;
        }
        throw new Exception("Circular version inheritance");
    }

    public int GetRequiredJavaMajor(Instance instance)
    {
        var fallbackVersion = string.IsNullOrWhiteSpace(instance.McVersion)
            ? instance.VersionId
            : instance.McVersion;
        try
        {
            return ReadRequiredJavaMajor(ResolveVersion(instance.VersionId), fallbackVersion);
        }
        catch
        {
            return JavaService.RequiredMajorVersion(fallbackVersion);
        }
    }

    public Task<string> LaunchAsync(Instance instance)
    {
        var (java, args, cwd) = BuildCommand(instance);
        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in args)
            psi.ArgumentList.Add(argument);
        var process = Process.Start(psi);
        return Task.FromResult(process?.Id.ToString() ?? "");
    }

    public (string java, List<string> args, string cwd) BuildCommand(
        Instance instance,
        string? selectedJavaPath = null,
        string? quickPlayServer = null)
    {
        var meta = ResolveVersion(instance.VersionId);
        var fallbackVersion = string.IsNullOrWhiteSpace(instance.McVersion)
            ? instance.VersionId
            : instance.McVersion;
        var requiredJavaMajor = ReadRequiredJavaMajor(meta, fallbackVersion);
        var java = string.IsNullOrWhiteSpace(selectedJavaPath)
            ? ResolveJava(instance, requiredJavaMajor)
            : selectedJavaPath.Trim();
        if (string.IsNullOrWhiteSpace(java)) throw new Exception("Java not found");
        var javaInfo = JavaService.ProbeJava(java);
        if (javaInfo == null)
            throw new IOException($"Java 无法使用: {java}");
        if (!JavaService.IsCompatible(javaInfo, requiredJavaMajor))
            throw new IOException($"Minecraft {fallbackVersion} 需要 Java {requiredJavaMajor}，当前 Java 为 {javaInfo.MajorVersion}。请安装匹配版本。");
        var javaMajor = javaInfo.MajorVersion;

        var root = InstancePathService.EnsureGameDirectory(_paths, _settings.Data, instance);
        if (instance.AutoSetChinese) ApplyChineseLanguage(root);
        var tempDir = Path.Combine(_paths.Root, "temp");
        Directory.CreateDirectory(tempDir);

        var memoryMb = instance.MemoryMb > 0
            ? Math.Clamp(instance.MemoryMb, 512, 131072)
            : _settings.Data.AutoMemory
                ? MemoryAdvisor.ApplyAutoMemory(_settings.Data, instance.VersionId, 0)
                : _settings.Data.MemoryMb;

        var authMode = _settings.Data.AuthMode;
        var external = authMode == AuthModes.External;
        var authenticated = external || authMode == AuthModes.Microsoft;

        // 开房时如勾选「允许非正版玩家进入」，改用离线会话启动，
        // 这样「对局域网开放」时集成的服务端 online-mode=false。
        // 但「一键加入别人的房间」必须使用真实会话，否则会报无效会话。
        var quickPlayJoin = !string.IsNullOrWhiteSpace(quickPlayServer);
        var offlineForLan = _settings.Data.LanAllowNonPremium && authenticated && !quickPlayJoin;

        string playerName, authUuid, accessToken, userType;
        if (offlineForLan)
        {
            playerName = string.IsNullOrWhiteSpace(_settings.Data.AuthPlayerName)
                ? _settings.Data.PlayerName
                : _settings.Data.AuthPlayerName;
            authUuid = AuthService.GenerateOfflineUuid(playerName);
            accessToken = "0";
            userType = "legacy";
            external = false;
        }
        else if (authenticated)
        {
            playerName = _settings.Data.AuthPlayerName;
            authUuid = _settings.Data.AuthUuid;
            accessToken = _settings.Data.AuthAccessToken;
            userType = authMode == AuthModes.Microsoft ? "msa" : "mojang";
        }
        else
        {
            playerName = _settings.Data.PlayerName;
            authUuid = AuthService.GenerateOfflineUuid(playerName);
            accessToken = "0";
            userType = "legacy";
        }

        var classpath = new List<string>();
        var nativesDir = Path.Combine(root, ".natives");
        var nativeArchives = new List<(string Path, JsonElement Library)>();
        foreach (var lib in GetEffectiveLibraries(meta))
        {
            if (!RulesAllow(lib.TryGetProperty("rules", out var rules) ? rules : null)) continue;
            var artifact = ResolveLibraryArtifact(lib);
            if (artifact != null)
            {
                var fullPath = Path.Combine(_paths.LibrariesDir, artifact.RelativePath);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"缺少启动库 {artifact.RelativePath}，请重新修复游戏文件。", fullPath);
                classpath.Add(fullPath);
            }

            var nativeArtifact = ResolveNativeArtifact(lib);
            if (nativeArtifact != null)
            {
                var nativePath = Path.Combine(_paths.LibrariesDir, nativeArtifact.RelativePath);
                if (!File.Exists(nativePath))
                    throw new FileNotFoundException($"缺少本地运行库 {nativeArtifact.RelativePath}，请重新修复游戏文件。", nativePath);
                nativeArchives.Add((nativePath, lib));
            }
        }

        // 启动速度优化：本地运行库未变化时复用上次解压结果，不再删除后重新解压
        EnsureNativesExtracted(nativesDir, nativeArchives);

        var clientVersion = meta.TryGetProperty("jar", out var jar)
            ? jar.GetString() ?? BaseVersionId(instance.VersionId)
            : BaseVersionId(instance.VersionId);
        var clientJar = Path.Combine(_paths.VersionsDir, clientVersion, $"{clientVersion}.jar");
        if (!File.Exists(clientJar))
            throw new FileNotFoundException($"缺少 Minecraft {clientVersion} 客户端文件，请重新修复游戏文件。", clientJar);
        classpath.Add(clientJar);

        var assetIndex = meta.TryGetProperty("assetIndex", out var ai)
            && ai.TryGetProperty("id", out var assetId)
            ? assetId.GetString() ?? ""
            : "";
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = playerName,
            ["${version_name}"] = instance.VersionId,
            ["${game_directory}"] = root,
            ["${assets_root}"] = _paths.AssetsDir,
            ["${game_assets}"] = Path.Combine(_paths.AssetsDir, "virtual", "legacy"),
            ["${assets_index_name}"] = assetIndex,
            ["${auth_uuid}"] = authUuid.Replace("-", "", StringComparison.Ordinal),
            ["${auth_access_token}"] = accessToken,
            ["${access_token}"] = accessToken,
            ["${auth_session}"] = accessToken,
            ["${user_type}"] = userType,
            ["${version_type}"] = "QuartzLauncher",
            ["${user_properties}"] = "{}",
            ["${natives_directory}"] = nativesDir,
            ["${library_directory}"] = _paths.LibrariesDir,
            ["${libraries_directory}"] = _paths.LibrariesDir,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${classpath}"] = string.Join(Path.PathSeparator, classpath),
            ["${launcher_name}"] = "QuartzLauncher",
            ["${launcher_version}"] = UpdateService.CurrentVersion,
            ["${resolution_width}"] = "854",
            ["${resolution_height}"] = "480",
            ["${clientid}"] = MicrosoftAuthService.ClientId,
            ["${auth_xuid}"] = "0",
            ["${quickPlayPath}"] = "",
            ["${quickPlaySingleplayer}"] = "",
            ["${quickPlayMultiplayer}"] = quickPlayServer ?? "",
            ["${quickPlayRealms}"] = ""
        };

        // 联机「一键启动并加入」：MC 1.20+ 支持 --quickPlayMultiplayer 直接进服务器
        var quickPlay = !string.IsNullOrWhiteSpace(quickPlayServer);
        var features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = false,
            ["has_quick_plays_support"] = quickPlay,
            ["is_quick_play_singleplayer"] = false,
            // 一键加入：必须置 true，否则 --quickPlayMultiplayer 不会被加入启动参数
            ["is_quick_play_multiplayer"] = quickPlay,
            ["is_quick_play_realms"] = false
        };

        var jvmArgs = ReadModernArguments(meta, "jvm", features)
            .Select(argument => ReplaceVariables(argument, variables))
            .Where(argument => !IsMemoryArgument(argument) && IsJvmArgumentSupported(argument, javaMajor))
            .ToList();
        if (jvmArgs.Count == 0)
        {
            jvmArgs.Add($"-Djava.library.path={nativesDir}");
            jvmArgs.Add("-cp");
            jvmArgs.Add(string.Join(Path.PathSeparator, classpath));
        }
        else if (!ContainsClasspathArgument(jvmArgs))
        {
            jvmArgs.Add("-cp");
            jvmArgs.Add(string.Join(Path.PathSeparator, classpath));
        }

        jvmArgs.Add($"-Xmx{memoryMb}M");
        jvmArgs.Add(_settings.Data.MemoryOptimize ? $"-Xms{memoryMb}M" : "-Xms512M");
        jvmArgs.Add($"-Djava.io.tmpdir={tempDir}");

        var loggingArgument = GetLoggingArgument(meta);
        if (!string.IsNullOrEmpty(loggingArgument))
            jvmArgs.Add(loggingArgument);

        if (_settings.Data.MemoryOptimize)
            ApplyGcArguments(jvmArgs, javaMajor);

        jvmArgs.Add("-Dlog4j2.formatMsgNoLookups=true");
        jvmArgs.Add("-Dfile.encoding=UTF-8");
        jvmArgs.Add("-Dstdout.encoding=UTF-8");
        jvmArgs.Add("-Dstderr.encoding=UTF-8");

        jvmArgs.AddRange(SplitLegacyArguments(instance.JvmArguments)
            .Where(argument => !IsMemoryArgument(argument) && IsJvmArgumentSupported(argument, javaMajor))
            .Select(argument => ReplaceVariables(argument, variables)));

        if (external && !string.IsNullOrEmpty(_settings.Data.AuthInjectorPath))
        {
            jvmArgs.Add($"-javaagent:{_settings.Data.AuthInjectorPath}={_settings.Data.AuthServer}");
            jvmArgs.Add("-Dauthlibinjector.side=client");
        }

        var mainClass = meta.TryGetProperty("mainClass", out var mc)
            ? mc.GetString() ?? "net.minecraft.client.main.Main"
            : "net.minecraft.client.main.Main";
        var gameArgs = ReadModernArguments(meta, "game", features);
        if (gameArgs.Count == 0 && meta.TryGetProperty("minecraftArguments", out var legacyArguments))
            gameArgs = SplitLegacyArguments(legacyArguments.GetString() ?? "");
        if (gameArgs.Count == 0)
        {
            gameArgs =
            [
                "--username", "${auth_player_name}",
                "--version", "${version_name}",
                "--gameDir", "${game_directory}",
                "--assetsDir", "${assets_root}",
                "--assetIndex", "${assets_index_name}",
                "--uuid", "${auth_uuid}",
                "--accessToken", "${auth_access_token}",
                "--userType", "${user_type}",
                "--versionType", "${version_type}"
            ];
        }
        gameArgs = gameArgs.Select(argument => ReplaceVariables(argument, variables)).ToList();
        gameArgs.AddRange(SplitLegacyArguments(instance.GameArguments)
            .Select(argument => ReplaceVariables(argument, variables)));

        var allArgs = jvmArgs.Concat(new[] { mainClass }).Concat(gameArgs).ToList();
        return (java, allArgs, root);
    }

    private static List<string> SplitLegacyArguments(string arguments)
    {
        return Regex.Matches(arguments, "\\\"(?:\\\\.|[^\\\"])*\\\"|\\S+")
            .Select(match => match.Value.Length >= 2 && match.Value[0] == '"' && match.Value[^1] == '"'
                ? match.Value[1..^1].Replace("\\\"", "\"")
                : match.Value)
            .ToList();
    }

    private string ResolveJava(Instance instance, int requiredJavaMajor)
    {
        return JavaService.SelectForMajor(
            requiredJavaMajor,
            instance.JavaPath,
            _settings.Data.JavaPath,
            _settings.Data.DetectedJavas,
            _settings.Data.AutoSelectJava)?.Path ?? "";
    }

    private static bool RulesAllow(JsonElement? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules == null || rules.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        var allowed = false;
        foreach (var rule in rules.Value.EnumerateArray())
        {
            if (!RuleMatches(rule, features)) continue;
            allowed = !rule.TryGetProperty("action", out var action) || action.GetString() != "disallow";
        }
        return allowed;
    }

    private static bool RuleMatches(JsonElement rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.TryGetProperty("os", out var os))
        {
            var osName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            if (os.TryGetProperty("name", out var name)
                && !string.Equals(name.GetString(), osName, StringComparison.OrdinalIgnoreCase)) return false;

            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x86_64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            };
            if (os.TryGetProperty("arch", out var arch)
                && !string.Equals(arch.GetString(), architecture, StringComparison.OrdinalIgnoreCase)) return false;

            if (os.TryGetProperty("version", out var version))
            {
                try
                {
                    if (!Regex.IsMatch(Environment.OSVersion.Version.ToString(), version.GetString() ?? "")) return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        if (!rule.TryGetProperty("features", out var requiredFeatures)) return true;
        foreach (var required in requiredFeatures.EnumerateObject())
        {
            var expected = required.Value.ValueKind == JsonValueKind.True;
            var actual = features?.GetValueOrDefault(required.Name, false) ?? false;
            if (actual != expected) return false;
        }
        return true;
    }

    private void CollectClientDownload(JsonElement meta, string versionId, List<DownloadItem> downloads)
    {
        if (meta.TryGetProperty("downloads", out var downloadsElement)
            && downloadsElement.TryGetProperty("client", out var client))
        {
            var jarPath = Path.Combine(_paths.VersionsDir, versionId, $"{versionId}.jar");
            downloads.Add(new DownloadItem(
                client.GetProperty("url").GetString() ?? "",
                jarPath,
                client.TryGetProperty("sha1", out var s1) ? s1.GetString() ?? "" : "",
                client.TryGetProperty("size", out var size) ? size.GetInt64() : 0
            ));
        }
    }

    private void CollectLibraryDownloads(JsonElement meta, List<DownloadItem> downloads)
    {
        foreach (var lib in GetEffectiveLibraries(meta))
        {
            if (!RulesAllow(lib.TryGetProperty("rules", out var rules) ? rules : null)) continue;
            var artifact = ResolveLibraryArtifact(lib);
            if (artifact != null)
            {
                downloads.Add(ToDownloadItem(artifact, _paths.LibrariesDir));
            }

            var nativeArtifact = ResolveNativeArtifact(lib);
            if (nativeArtifact != null)
                downloads.Add(ToDownloadItem(nativeArtifact, _paths.LibrariesDir));
        }
    }

    private void CollectLoggingDownload(JsonElement meta, List<DownloadItem> downloads)
    {
        if (!meta.TryGetProperty("logging", out var logging)
            || !logging.TryGetProperty("client", out var client)
            || !client.TryGetProperty("file", out var file)
            || !file.TryGetProperty("url", out var url)) return;

        var target = Path.Combine(_paths.AssetsDir, "log_configs", file.GetProperty("id").GetString() ?? "client.xml");
        downloads.Add(new DownloadItem(
            url.GetString() ?? "",
            target,
            file.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? "" : "",
            file.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
    }

    private static DownloadItem ToDownloadItem(LibraryArtifact artifact, string root) =>
        new(
            artifact.Url,
            Path.Combine(root, artifact.RelativePath),
            artifact.Sha1,
            artifact.Size);

    private static List<DownloadItem> DeduplicateDownloads(IEnumerable<DownloadItem> items) =>
        items.Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private static IEnumerable<JsonElement> GetEffectiveLibraries(JsonElement meta)
    {
        if (!meta.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Array) return [];
        var all = libraries.EnumerateArray().ToList();
        var selected = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = all.Count - 1; index >= 0; index--)
        {
            var library = all[index];
            var identity = GetLibraryIdentity(library);
            if (seen.Add(identity)) selected.Add(library);
        }
        selected.Reverse();
        return selected;
    }

    private static string GetLibraryIdentity(JsonElement library)
    {
        if (!library.TryGetProperty("name", out var nameProperty)) return library.GetRawText();
        var name = (nameProperty.GetString() ?? "").Split('@', 2)[0];
        var parts = name.Split(':');
        return parts.Length < 2
            ? name
            : $"{parts[0]}:{parts[1]}:{(parts.Length >= 4 ? parts[3] : "")}";
    }

    private static LibraryArtifact? ResolveLibraryArtifact(JsonElement library)
    {
        if (library.TryGetProperty("downloads", out var downloads)
            && downloads.TryGetProperty("artifact", out var artifact)
            && ReadArtifact(artifact) is { } resolved) return resolved;
        if (library.TryGetProperty("downloads", out downloads)
            && downloads.TryGetProperty("classifiers", out _)) return null;
        if (library.TryGetProperty("natives", out _)) return null;
        if (!library.TryGetProperty("name", out var name)) return null;
        return ResolveMavenArtifact(name.GetString() ?? "", library, null);
    }

    private static LibraryArtifact? ResolveNativeArtifact(JsonElement library)
    {
        if (!library.TryGetProperty("natives", out var natives)
            || !natives.TryGetProperty("windows", out var windows)) return null;
        var classifier = (windows.GetString() ?? "").Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
        if (library.TryGetProperty("downloads", out var downloads)
            && downloads.TryGetProperty("classifiers", out var classifiers)
            && classifiers.TryGetProperty(classifier, out var native))
            return ReadArtifact(native) ?? (library.TryGetProperty("name", out var fallbackName)
                ? ResolveMavenArtifact(fallbackName.GetString() ?? "", library, classifier)
                : null);

        if (!library.TryGetProperty("name", out var name)) return null;
        return ResolveMavenArtifact(name.GetString() ?? "", library, classifier);
    }

    private static LibraryArtifact? ReadArtifact(JsonElement artifact)
    {
        if (!artifact.TryGetProperty("path", out var path)
            || !artifact.TryGetProperty("url", out var url)) return null;
        var relativePath = path.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Split('/', '\\').Any(part => part == "..")) return null;
        return new LibraryArtifact(
            relativePath,
            url.GetString() ?? "",
            artifact.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? "" : "",
            artifact.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
    }

    private static LibraryArtifact? ResolveMavenArtifact(string name, JsonElement library, string? classifier)
    {
        var coordinate = name.Split('@', 2);
        var parts = coordinate[0].Split(':');
        if (parts.Length < 3) return null;
        var extension = coordinate.Length == 2 ? coordinate[1] : "jar";
        classifier ??= parts.Length >= 4 ? parts[3] : null;
        var path = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}"
                   + (string.IsNullOrEmpty(classifier) ? "" : $"-{classifier}") + $".{extension}";
        var repository = library.TryGetProperty("url", out var baseUrl)
            ? baseUrl.GetString()
            : "https://libraries.minecraft.net";
        var url = repository?.TrimEnd('/') + "/" + path;
        return new LibraryArtifact(path, url ?? "", "", 0);
    }

    private static List<string> ReadModernArguments(JsonElement meta, string section, IReadOnlyDictionary<string, bool> features)
    {
        if (!meta.TryGetProperty("arguments", out var arguments)
            || !arguments.TryGetProperty(section, out var values)
            || values.ValueKind != JsonValueKind.Array) return new();

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString() ?? "");
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object
                || value.TryGetProperty("rules", out var rules) && !RulesAllow(rules, features)
                || !value.TryGetProperty("value", out var argument)) continue;

            if (argument.ValueKind == JsonValueKind.Array)
                result.AddRange(argument.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? ""));
            else if (argument.ValueKind == JsonValueKind.String)
                result.Add(argument.GetString() ?? "");
        }
        return result;
    }

    private static string ReplaceVariables(string value, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (key, replacement) in variables)
            value = value.Replace(key, replacement, StringComparison.Ordinal);
        return value;
    }

    private static bool IsMemoryArgument(string argument) =>
        argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex GcArgumentPattern = new(
        @"^\s*-XX:[+-]?(Use\w+GC|ZGenerational|UseCompactObjectHeaders|G1\w+Percent|G1\w+Size|(Max|Min)(GCPauseMillis|HeapFreeRatio))",
        RegexOptions.Compiled);

    // 按 Java 版本自动选择 ZGC / G1GC 并套用调优参数
    private static void ApplyGcArguments(List<string> jvmArgs, int javaMajor)
    {
        // 先移除版本 JSON 里自带的 GC 参数，避免冲突
        jvmArgs.RemoveAll(argument => GcArgumentPattern.IsMatch(argument));

        var useG1 = javaMajor < 15 || !IsZgcCapableOs();
        jvmArgs.Add("-XX:+UnlockExperimentalVMOptions");
        if (javaMajor >= 24)
            jvmArgs.Add("-XX:+UseCompactObjectHeaders");

        if (useG1)
        {
            jvmArgs.Add("-XX:+UseG1GC");
            jvmArgs.Add("-XX:G1NewSizePercent=20");
            jvmArgs.Add("-XX:G1ReservePercent=20");
            jvmArgs.Add("-XX:G1HeapRegionSize=32M");
            jvmArgs.Add("-XX:MaxGCPauseMillis=50");
            jvmArgs.Add("-XX:+PerfDisableSharedMem");
            if (javaMajor <= 7) jvmArgs.Add("-XX:MaxPermSize=512m");
            if (javaMajor == 8) jvmArgs.Add("-XX:+ParallelRefProcEnabled");
            if (javaMajor >= 12)
            {
                jvmArgs.Add("-XX:MinHeapFreeRatio=25");
                jvmArgs.Add("-XX:MaxHeapFreeRatio=40");
            }
        }
        else
        {
            jvmArgs.Add("-XX:+UseZGC");
            if (javaMajor is 21 or 22)
                jvmArgs.Add("-XX:+ZGenerational");
        }
    }

    // ZGC 需要 Windows 10 1809+（Build 17763）
    private static bool IsZgcCapableOs()
        => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 17763;

    private static bool IsJvmArgumentSupported(string argument, int javaMajor) =>
        !argument.StartsWith("--sun-misc-unsafe-memory-access=", StringComparison.OrdinalIgnoreCase)
        || javaMajor >= 23;

    private static int ReadRequiredJavaMajor(JsonElement meta, string fallbackVersion)
    {
        if (meta.TryGetProperty("javaVersion", out var javaVersion)
            && javaVersion.TryGetProperty("majorVersion", out var majorVersion)
            && majorVersion.TryGetInt32(out var requiredMajor)
            && requiredMajor > 0)
            return requiredMajor;
        return JavaService.RequiredMajorVersion(fallbackVersion);
    }

    private static bool ContainsClasspathArgument(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument is "-cp" or "-classpath" or "--class-path")
        || arguments.Any(argument => argument.Contains("${classpath}", StringComparison.Ordinal));

    private string? GetLoggingArgument(JsonElement meta)
    {
        if (!meta.TryGetProperty("logging", out var logging)
            || !logging.TryGetProperty("client", out var client)
            || !client.TryGetProperty("argument", out var argument)
            || !client.TryGetProperty("file", out var file)) return null;
        var path = Path.Combine(_paths.AssetsDir, "log_configs", file.GetProperty("id").GetString() ?? "client.xml");
        return (argument.GetString() ?? "").Replace("${path}", path, StringComparison.Ordinal);
    }

    private sealed record LibraryArtifact(string RelativePath, string Url, string Sha1, long Size);

    private async Task CollectAssetDownloadsAsync(JsonElement versionJson, List<DownloadItem> downloads)
    {
        var indexInfo = versionJson.GetProperty("assetIndex");
        var indexId = indexInfo.GetProperty("id").GetString()!;
        var indexPath = Path.Combine(_paths.AssetsDir, "indexes", $"{indexId}.json");
        JsonElement indexJson;
        if (File.Exists(indexPath))
        {
            // 启动速度优化：本地已有资源索引，直接离线使用
            try
            {
                indexJson = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(indexPath));
            }
            catch
            {
                indexJson = await DownloadService.FetchJsonAsync(indexInfo.GetProperty("url").GetString()!);
                await File.WriteAllTextAsync(indexPath, indexJson.GetRawText());
            }
        }
        else
        {
            indexJson = await DownloadService.FetchJsonAsync(indexInfo.GetProperty("url").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllTextAsync(indexPath, indexJson.GetRawText());
        }

        if (!indexJson.TryGetProperty("objects", out var objects)) return;
        foreach (var asset in objects.EnumerateObject())
        {
            var hash = asset.Value.GetProperty("hash").GetString()!;
            var target = Path.Combine(_paths.AssetsDir, "objects", hash[..2], hash);
            downloads.Add(new DownloadItem(
                $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                target,
                hash,
                asset.Value.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
        }
    }

    // 仅在本地运行库发生变化时重新解压 natives，避免每次启动都全量解压
    private static void EnsureNativesExtracted(string nativesDir, List<(string Path, JsonElement Library)> nativeArchives)
    {
        var marker = Path.Combine(nativesDir, ".extracted");
        var signature = string.Join("|", nativeArchives.Select(item =>
        {
            var info = new FileInfo(item.Path);
            return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));

        var upToDate = Directory.Exists(nativesDir)
                       && File.Exists(marker)
                       && string.Equals(File.ReadAllText(marker), signature, StringComparison.Ordinal);
        if (upToDate) return;

        if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, true);
        Directory.CreateDirectory(nativesDir);
        foreach (var (archivePath, library) in nativeArchives)
            ExtractNatives(archivePath, nativesDir, library);
        try { File.WriteAllText(marker, signature); } catch { }
    }

    private static void ExtractNatives(string archivePath, string targetDir, JsonElement library)
    {
        var excludes = new List<string> { "META-INF/" };
        if (library.TryGetProperty("extract", out var extract)
            && extract.TryGetProperty("exclude", out var configuredExcludes))
        {
            excludes.AddRange(configuredExcludes.EnumerateArray().Select(value => value.GetString() ?? ""));
        }

        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(entry.Name) || excludes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            var destination = Path.GetFullPath(Path.Combine(targetDir, name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    private static void ApplyChineseLanguage(string instanceRoot)
    {
        var optionsPath = Path.Combine(instanceRoot, "options.txt");
        if (!File.Exists(optionsPath))
        {
            File.WriteAllLines(optionsPath, new[] { "lang:zh_cn" });
            return;
        }

        var lines = File.ReadAllLines(optionsPath).ToList();
        var languageIndex = lines.FindIndex(line => line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));
        if (languageIndex >= 0)
        {
            // 已是中文时不再重写，减少启动时的磁盘写入
            if (lines[languageIndex].Equals("lang:zh_cn", StringComparison.OrdinalIgnoreCase)) return;
            lines[languageIndex] = "lang:zh_cn";
        }
        else
        {
            lines.Add("lang:zh_cn");
        }
        File.WriteAllLines(optionsPath, lines);
    }

    private static string EscapeArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
