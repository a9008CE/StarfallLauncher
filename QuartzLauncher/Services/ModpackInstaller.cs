using System.IO;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public static class ModpackInstaller
{
    public static async Task<Instance> InstallAsync(
        string packFilePath,
        AppPaths paths,
        SettingsService settings,
        IProgress<string>? progress = null)
    {
        var pack = new ModpackService();
        var descriptor = await Task.Run(() => pack.Inspect(packFilePath));
        var name = EnsureUniqueInstanceName(descriptor.Name);
        var instanceId = CreateInstanceId(name);
        var mc = new MinecraftService(paths, settings);
        var loaders = new LoaderService(paths, settings);

        progress?.Report($"正在准备 Minecraft {descriptor.MinecraftVersion}...");
        var baseItems = await mc.PrepareInstallAsync(descriptor.MinecraftVersion);
        var baseTask = DownloadManager.Instance.Enqueue(
            $"导入 {name} 的 Minecraft {descriptor.MinecraftVersion}", baseItems,
            workers: settings.Data.DownloadWorkers, category: "version");
        if (baseTask.Tcs != null) await baseTask.Tcs.Task;
        if (baseTask.Status != DownloadTaskStatus.Completed)
            throw new IOException($"Minecraft 基础文件下载失败: {baseTask.Error}");

        var installedVersionId = descriptor.MinecraftVersion;
        var loaderKey = descriptor.Loader.ToLowerInvariant();
        if (loaderKey is "fabric" or "forge" or "neoforge" or "quilt")
        {
            progress?.Report($"正在安装 {descriptor.Loader} 加载器...");
            installedVersionId = await InstallLoaderAsync(
                loaders, loaderKey, descriptor.MinecraftVersion, descriptor.LoaderVersion);
        }

        var stagingRoot = Path.Combine(paths.TempDir, $"modpack-{instanceId}");
        var instanceRoot = Path.Combine(paths.InstancesDir, instanceId);
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            pack.ImportContent(packFilePath, stagingRoot);
            var remoteFiles = await pack.PrepareRemoteFilesAsync(packFilePath, stagingRoot);
            if (remoteFiles.Count > 0)
            {
                progress?.Report($"正在下载整合包内容（{remoteFiles.Count} 个文件）...");
                var contentTask = DownloadManager.Instance.Enqueue(
                    $"整合包内容 · {name}", remoteFiles,
                    workers: settings.Data.DownloadWorkers, category: "resource");
                if (contentTask.Tcs != null) await contentTask.Tcs.Task;
                if (contentTask.Status != DownloadTaskStatus.Completed)
                    throw new IOException($"整合包内容下载失败: {contentTask.Error}");
            }

            if (Directory.Exists(instanceRoot)) throw new IOException("目标实例目录已存在。");
            var instance = new Instance
            {
                Id = instanceId,
                Name = name,
                VersionId = installedVersionId,
                McVersion = descriptor.MinecraftVersion,
                Loader = loaderKey == "vanilla" ? "vanilla" : loaderKey,
                LoaderVersion = descriptor.LoaderVersion,
                VersionIsolation = true,
                UsesVersionDirectory = false,
                MemoryMb = descriptor.MemoryMb,
                JvmArguments = descriptor.JvmArguments,
                GameArguments = descriptor.GameArguments
            };
            // PCL2 式提交：临时目录内先补齐目录并写完、校验 instance.json，
            // 最后一次性移动为正式实例目录，避免首页扫描到半成品。
            InstancePathService.EnsureGameContentDirectories(stagingRoot);
            InstanceStore.WriteMetadata(stagingRoot, instance);
            var markerDir = Path.Combine(paths.VersionsDir, descriptor.MinecraftVersion);
            Directory.CreateDirectory(markerDir);
            File.WriteAllText(Path.Combine(markerDir, ".quartz-installed"), DateTimeOffset.UtcNow.ToString("O"));
            Directory.Move(stagingRoot, instanceRoot);
            return instance;
        }
        catch
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            if (Directory.Exists(instanceRoot)) Directory.Delete(instanceRoot, true);
            throw;
        }
    }

    private static async Task<string> InstallLoaderAsync(
        LoaderService loaders, string loader, string minecraftVersion, string requested)
    {
        switch (loader)
        {
            case "fabric":
            {
                var version = !string.IsNullOrWhiteSpace(requested)
                    ? requested
                    : (await loaders.FabricVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
                if (string.IsNullOrEmpty(version))
                    throw new IOException($"没有找到 Minecraft {minecraftVersion} 的 Fabric 版本");
                return await loaders.InstallFabricAsync(minecraftVersion, version);
            }
            case "forge":
            {
                var coordinate = !string.IsNullOrWhiteSpace(requested)
                    ? requested
                    : (await loaders.ForgeVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(coordinate)) throw new IOException($"没有找到 Minecraft {minecraftVersion} 的 Forge 版本");
                if (!coordinate.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase))
                    coordinate = $"{minecraftVersion}-{coordinate}";
                return await loaders.InstallForgeAsync(coordinate);
            }
            case "neoforge":
            {
                var version = !string.IsNullOrWhiteSpace(requested)
                    ? requested
                    : (await loaders.NeoForgeVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(version)) throw new IOException($"没有找到 Minecraft {minecraftVersion} 的 NeoForge 版本");
                return await loaders.InstallNeoForgeAsync(version);
            }
            case "quilt":
            {
                var version = !string.IsNullOrWhiteSpace(requested)
                    ? requested
                    : (await loaders.QuiltVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(version)) throw new IOException($"没有找到 Minecraft {minecraftVersion} 的 Quilt 版本");
                return await loaders.InstallQuiltAsync(minecraftVersion, version);
            }
            default:
                return minecraftVersion;
        }
    }

    private static string EnsureUniqueInstanceName(string baseName)
    {
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            baseName = baseName.Trim().Replace("\r", "").Replace("\n", "");
            if (baseName.Length > 80) baseName = baseName[..80];
        }
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "整合包";
        var existingNames = new InstanceStore(App.Paths.InstancesDir).List()
            .Select(instance => instance.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName)) return baseName;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!existingNames.Contains(candidate)) return candidate;
        }
    }

    private static string CreateInstanceId(string name)
    {
        var slug = new string(name.Trim().Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? char.ToLowerInvariant(character)
                    : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-', '_');
        if (string.IsNullOrWhiteSpace(slug)) slug = "instance";
        if (slug.Length > 64) slug = slug[..64].TrimEnd('-', '_');
        return slug + "-" + Guid.NewGuid().ToString("N")[..6];
    }
}
