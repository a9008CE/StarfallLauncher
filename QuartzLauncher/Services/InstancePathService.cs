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
