using System.IO;

namespace QuartzLauncher.Models;

public class AppPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string MetadataDir => Path.Combine(Root, "metadata");
    public string MinecraftDir { get; }
    public string VersionsDir => Path.Combine(MinecraftDir, "versions");
    public string LibrariesDir => Path.Combine(MinecraftDir, "libraries");
    public string AssetsDir => Path.Combine(MinecraftDir, "assets");
    public string InstancesDir => Path.Combine(MinecraftDir, "instances");
    public string CacheDir => Path.Combine(Root, "cache");
    public string TempDir => Path.Combine(Root, "temp");
    public string BrowserDataDir => Path.Combine(CacheDir, "webview2");

    public AppPaths(string root, string minecraftDir)
    {
        Root = root;
        MinecraftDir = minecraftDir;
    }

    public static AppPaths Default()
        => FromBaseDirectory(AppContext.BaseDirectory);

    public static AppPaths FromBaseDirectory(string baseDirectory)
    {
        var baseDir = Path.GetFullPath(baseDirectory);

        // 版本化更新：新版跑在 <安装根>\versions\<版本>\ 下，
        // 数据目录必须仍指回安装根，否则升级一次用户配置就"消失"了。
        // 形如 <安装根>ersions\<版本>\ → 数据目录回到安装根
        var dirInfo = new DirectoryInfo(baseDir);
        if (string.Equals(dirInfo.Parent?.Name, "versions", StringComparison.OrdinalIgnoreCase)
            && dirInfo.Parent?.Parent != null)
        {
            baseDir = dirInfo.Parent.Parent.FullName;
        }

        var root = Path.Combine(baseDir, "Launcher");
        var minecraftDir = Path.Combine(baseDir, ".minecraft");
        var legacyMinecraftDir = Path.Combine(root, ".minecraft");
        if (Directory.Exists(legacyMinecraftDir))
        {
            var targetIsEmpty = !Directory.Exists(minecraftDir)
                                || !Directory.EnumerateFileSystemEntries(minecraftDir).Any();
            if (targetIsEmpty)
            {
                if (Directory.Exists(minecraftDir)) Directory.Delete(minecraftDir);
                Directory.Move(legacyMinecraftDir, minecraftDir);
            }
            else
            {
                MergeDirectory(legacyMinecraftDir, minecraftDir);
            }
        }
        return new AppPaths(root, minecraftDir);
    }

    public void Ensure()
        => EnsureConfiguration();

    public void EnsureConfiguration()
    {
        foreach (var dir in new[]
        {
            Root, MetadataDir, CacheDir, TempDir
        })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void EnsureGame()
    {
        foreach (var dir in new[]
        {
            MinecraftDir, VersionsDir, LibrariesDir,
            Path.Combine(AssetsDir, "indexes"),
            Path.Combine(AssetsDir, "objects"),
            Path.Combine(AssetsDir, "log_configs"),
            InstancesDir
        })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void MergeDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            File.Move(file, destination, true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
            MergeDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private static void MigrateDirectory(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
            return;
        }

        MergeDirectory(source, target);
    }
}
