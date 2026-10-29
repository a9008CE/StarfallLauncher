using System.IO;
using Newtonsoft.Json;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public static class InstancePathService
{
    private static readonly string[] GameContentDirectories =
        ["mods", "saves", "resourcepacks", "shaderpacks", "config"];

    public static string GetGameDirectory(AppPaths paths, Settings settings, Instance instance)
    {
        var versionId = string.IsNullOrWhiteSpace(instance.VersionId)
            ? instance.McVersion
            : instance.VersionId;
        if (string.IsNullOrWhiteSpace(versionId))
            versionId = instance.Id;

        var isolated = instance.VersionIsolation ?? ShouldIsolate(settings, IsModded(instance));

        if (!string.IsNullOrWhiteSpace(instance.CustomGameDir) && Directory.Exists(instance.CustomGameDir))
        {
            if (isolated && instance.UsesVersionDirectory == true)
                return Path.Combine(instance.CustomGameDir, "versions", versionId);
            return instance.CustomGameDir;
        }

        if (!isolated) return paths.MinecraftDir;
        return instance.UsesVersionDirectory == true
            ? Path.Combine(paths.VersionsDir, versionId)
            : Path.Combine(paths.InstancesDir, instance.Id);
    }

    public static string GetGameDirectory(AppPaths paths, Settings settings, string instanceId)
    {
        var metadataDirectory = Path.Combine(paths.InstancesDir, instanceId);
        var metadataFile = Path.Combine(metadataDirectory, "instance.json");
        if (!File.Exists(metadataFile))
            return GetFallbackGameDirectory(paths, settings, instanceId);

        try
        {
            var instance = JsonConvert.DeserializeObject<Instance>(File.ReadAllText(metadataFile));
            return instance == null
                || !string.Equals(instance.Id, instanceId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(instance.VersionId) && string.IsNullOrWhiteSpace(instance.McVersion)
                ? GetFallbackGameDirectory(paths, settings, instanceId)
                : GetGameDirectory(paths, settings, instance);
        }
        catch
        {
            return GetFallbackGameDirectory(paths, settings, instanceId);
        }
    }

    /// <summary>一个「候选 Mod 目录」的扫描结果。</summary>
    public sealed record ModDirCandidate(string Path, string Label, int Enabled, int Disabled)
    {
        public bool HasAny => Enabled > 0 || Disabled > 0;
    }

    /// <summary>
    /// 列出这个实例**可能**的 Mod 目录，并统计各自有多少 Mod（2026-10-27）。
    ///
    /// 为什么需要：我们有两种实例目录布局（`versions/<版本id>` 与 `instances/<实例id>`），
    /// 加上公共 `.minecraft/mods`，一共四个位置可能放着玩家的 Mod。
    /// 只用 `GetGameDirectory()` 那一个来数，就会出现「Mod 明明在、自检却说 0 个」——
    /// 玩家的 Mod 实际躺在另一个布局里（PCL2 迁移过来、或切换过版本隔离）。
    /// PCL2 的做法是只有一种布局（`versions/<id>`），所以不会遇到；我们有兼容目录就得自己兜住。
    /// </summary>
    public static List<ModDirCandidate> GetModDirCandidates(AppPaths paths, Settings settings, Instance instance)
    {
        var versionId = string.IsNullOrWhiteSpace(instance.VersionId) ? instance.McVersion : instance.VersionId;
        if (string.IsNullOrWhiteSpace(versionId)) versionId = instance.Id;

        var list = new List<(string Path, string Label)>
        {
            (GetGameDirectory(paths, settings, instance), "当前实例游戏目录"),
            (Path.Combine(paths.VersionsDir, versionId), $"versions/{versionId}"),
            (Path.Combine(paths.InstancesDir, instance.Id), $"instances/{instance.Id}"),
            (paths.MinecraftDir, "公共 .minecraft")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModDirCandidate>();
        foreach (var (path, label) in list)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var full = Path.GetFullPath(path);
            if (!seen.Add(full)) continue;
            var (enabled, disabled) = CountModsFast(full);
            result.Add(new ModDirCandidate(full, label, enabled, disabled));
        }
        return result;
    }

    /// <summary>
    /// 统计某个游戏目录下 Mod 状态，**按 PCL2 的口径**：
    /// 启用 = 扩展名 `.jar/.zip/.litemod`；禁用 = 这些扩展名后加了 `.disabled`/`.old`；
    /// 同一个 Mod 同时存在启用与禁用两份时**只算启用那份**（PCL2 的 EnabledName 去重）。
    /// </summary>
    private static (int Enabled, int Disabled) CountModsFast(string instanceRoot)
    {
        var modsDir = Path.Combine(instanceRoot, "mods");
        if (!Directory.Exists(modsDir)) return (0, 0);
        var enabled = 0;
        var disabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(modsDir))
        {
            var name = Path.GetFileName(file);
            var lower = name.ToLowerInvariant();
            var ext = Path.GetExtension(lower);
            var isEnabledExt = ext is ".jar" or ".zip" or ".litemod";
            if (isEnabledExt) { enabled++; enabledNames.Add(lower); continue; }
            if (ext is ".disabled" or ".old")
            {
                var enabledName = name.Replace(".disabled", "", StringComparison.OrdinalIgnoreCase)
                                      .Replace(".old", "", StringComparison.OrdinalIgnoreCase)
                                      .ToLowerInvariant();
                disabledNames.Add(enabledName);
            }
        }
        // 同名的启用项已经把禁用项「覆盖」掉，不重复计禁用
        disabledNames.ExceptWith(enabledNames);
        return (enabled, disabledNames.Count);
    }

    public static string GetVersionGameDirectory(AppPaths paths, string versionId, bool isolated) =>
        isolated ? Path.Combine(paths.VersionsDir, versionId) : paths.MinecraftDir;

    public static string GetGameDirectory(AppPaths paths, Settings settings, string versionId, bool isModded)
    {
        return GetVersionGameDirectory(paths, versionId, ShouldIsolate(settings, isModded));
    }

    public static string EnsureGameDirectory(AppPaths paths, Settings settings, Instance instance)
    {
        var root = GetGameDirectory(paths, settings, instance);
        EnsureGameContentDirectories(root);
        return root;
    }

    /// <summary>准备一个已经确定的游戏目录（整合包临时目录也使用这一套目录结构）。</summary>
    public static void EnsureGameContentDirectories(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var name in GameContentDirectories)
            Directory.CreateDirectory(Path.Combine(root, name));
    }

    private static bool IsModded(Instance instance) =>
        (!string.IsNullOrWhiteSpace(instance.Loader)
         && !instance.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(instance.VersionId)
            && !string.Equals(instance.VersionId, instance.McVersion, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldIsolate(Settings settings, bool isModded) => settings.VersionIsolationMode switch
    {
        VersionIsolationModes.None => false,
        VersionIsolationModes.Modded => isModded,
        _ => true
    };

    public static bool GetDefaultIsolation(Settings settings, Instance instance) =>
        ShouldIsolate(settings, IsModded(instance));

    public static bool GetDefaultIsolation(Settings settings, bool isModded) =>
        ShouldIsolate(settings, isModded);

    private static string GetFallbackGameDirectory(AppPaths paths, Settings settings, string instanceId)
    {
        var isLocalVanilla = instanceId.StartsWith("local-", StringComparison.OrdinalIgnoreCase);
        if (settings.VersionIsolationMode == VersionIsolationModes.None
            || settings.VersionIsolationMode == VersionIsolationModes.Modded && isLocalVanilla)
            return paths.MinecraftDir;

        if (isLocalVanilla)
            return Path.Combine(paths.VersionsDir, instanceId["local-".Length..]);

        return Path.Combine(paths.InstancesDir, instanceId);
    }
}
