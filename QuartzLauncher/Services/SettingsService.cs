using System.IO;
using Newtonsoft.Json;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public class SettingsService
{
    private readonly AppPaths _paths;
    public Settings Data { get; private set; }

    private SettingsService(AppPaths paths, Settings data)
    {
        _paths = paths;
        Data = data;
    }

    public static SettingsService Load(AppPaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
        {
            var javaPath = FindJava();
            return new SettingsService(paths, new Settings { JavaPath = javaPath });
        }
        try
        {
            var json = File.ReadAllText(paths.ConfigFile);
            var data = JsonConvert.DeserializeObject<Settings>(json) ?? new Settings();
            if (string.IsNullOrWhiteSpace(data.UpdateManifestUrl))
                data.UpdateManifestUrl = Settings.DefaultUpdateManifestUrl;
            if (data.PageAnimationStyle == "cinematic")
            {
                data.PageAnimationStyle = "slide";
                data.AdvancedAnimationEnabled = true;
            }
            if (data.DownloadWorkers == 8)
                data.DownloadWorkers = 64;
            ResolvePortablePaths(data);
            var service = new SettingsService(paths, data);
            service.Save();
            return service;
        }
        catch
        {
            return new SettingsService(paths, new Settings { JavaPath = FindJava() });
        }
    }

    public void Save()
    {
        _paths.Ensure();
        var portable = JsonConvert.DeserializeObject<Settings>(JsonConvert.SerializeObject(Data)) ?? new Settings();
        portable.JavaPath = ToStoredPath(Data.JavaPath);
        portable.DetectedJavas = (Data.DetectedJavas ?? new List<JavaInfo>())
            .Select(java => new JavaInfo
            {
                Path = ToStoredPath(java.Path),
                Version = java.Version,
                MajorVersion = java.MajorVersion
            })
            .ToList();
        portable.AuthInjectorPath = StorePortableFile(Data.AuthInjectorPath, "auth");
        portable.AuthAvatarPath = StorePortableFile(Data.AuthAvatarPath, "avatars");
        foreach (var account in portable.MicrosoftAccounts)
            account.AvatarPath = StorePortableFile(account.AvatarPath, "avatars");
        portable.CustomSkinPath = StorePortableFile(Data.CustomSkinPath, "skins");
        portable.ThemeBackgroundImage = StorePortableFile(Data.ThemeBackgroundImage, "backgrounds");
        var json = JsonConvert.SerializeObject(portable, Formatting.Indented);
        File.WriteAllText(_paths.ConfigFile, json);
    }

    private static void ResolvePortablePaths(Settings settings)
    {
        settings.JavaPath = ResolveStoredPath(settings.JavaPath);
        settings.DetectedJavas ??= new List<JavaInfo>();
        foreach (var java in settings.DetectedJavas)
            java.Path = ResolveStoredPath(java.Path);
        settings.AuthInjectorPath = ResolveStoredPath(settings.AuthInjectorPath);
        settings.AuthAvatarPath = ResolveStoredPath(settings.AuthAvatarPath);
        settings.MicrosoftAccounts ??= new List<MicrosoftAccount>();
        foreach (var account in settings.MicrosoftAccounts)
            account.AvatarPath = ResolveStoredPath(account.AvatarPath);
        settings.CustomSkinPath = ResolveStoredPath(settings.CustomSkinPath);
        settings.ThemeBackgroundImage = ResolveStoredPath(settings.ThemeBackgroundImage);
    }

    private static string ResolveStoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string ToStoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var fullPath = Path.GetFullPath(path);
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            if (fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(baseDir, fullPath);
        }
        catch
        {
        }
        return path;
    }

    private static string StorePortableFile(string sourcePath, string subdirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "";
        var resolved = ResolveStoredPath(sourcePath);
        if (!File.Exists(resolved)) return sourcePath;
        try
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            var fullSource = Path.GetFullPath(resolved);
            if (fullSource.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(baseDir, fullSource);

            var targetDir = Path.Combine(baseDir, "Launcher", subdirectory);
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, Path.GetFileName(fullSource));
            File.Copy(fullSource, target, true);
            return Path.GetRelativePath(baseDir, target);
        }
        catch
        {
            return sourcePath;
        }
    }

    private static string FindJava()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "java.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }
}
