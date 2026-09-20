using System.IO;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public static class InstanceDeletionService
{
    public static void Delete(AppPaths paths, Settings settings, Instance instance)
    {
        var versionId = string.IsNullOrWhiteSpace(instance.VersionId) ? instance.McVersion : instance.VersionId;
        if (instance.Id.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
        {
            var syntheticMetadataRoot = Path.GetFullPath(Path.Combine(paths.InstancesDir, instance.Id));
            EnsureDirectChild(paths.InstancesDir, syntheticMetadataRoot);
            new InstanceStore(paths.InstancesDir).Delete(instance.Id);
            if (string.IsNullOrWhiteSpace(versionId)) return;
            var localVersionRoot = Path.GetFullPath(Path.Combine(paths.VersionsDir, versionId));
            EnsureDirectChild(paths.VersionsDir, localVersionRoot);
            DeleteDirectory(localVersionRoot);
            return;
        }

        var gameRoot = Path.GetFullPath(InstancePathService.GetGameDirectory(paths, settings, instance));
        var metadataRoot = Path.GetFullPath(Path.Combine(paths.InstancesDir, instance.Id));
        var versionRoot = string.IsNullOrWhiteSpace(versionId)
            ? ""
            : Path.GetFullPath(Path.Combine(paths.VersionsDir, versionId));
        EnsureDirectChild(paths.InstancesDir, metadataRoot);
        if (!string.IsNullOrWhiteSpace(versionRoot)) EnsureDirectChild(paths.VersionsDir, versionRoot);
        new InstanceStore(paths.InstancesDir).Delete(instance.Id);

        var remainingInstances = new InstanceStore(paths.InstancesDir).List();
        var sharedGameRoot = PathsEqual(gameRoot, paths.MinecraftDir);
        if (!PathsEqual(gameRoot, metadataRoot)
            && !sharedGameRoot
            && IsDirectChild(paths.VersionsDir, gameRoot)
            && !remainingInstances.Any(other => PathsEqual(
                InstancePathService.GetGameDirectory(paths, settings, other), gameRoot)))
            DeleteDirectory(gameRoot);

        var versionStillReferenced = !string.IsNullOrWhiteSpace(versionId)
            && remainingInstances.Any(other =>
                string.Equals(other.VersionId, versionId, StringComparison.OrdinalIgnoreCase)
                || PathsEqual(InstancePathService.GetGameDirectory(paths, settings, other), versionRoot));
        if (!versionStillReferenced && !string.IsNullOrWhiteSpace(versionRoot)) DeleteDirectory(versionRoot);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        // 「添加已有文件夹」导入的版本以目录联接方式接入，
        // 删除时只能删掉链接本身，绝不能删到外部原文件夹里的游戏文件
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try
            {
                Directory.Delete(path);
                return;
            }
            catch
            {
                try
                {
                    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "cmd.exe", $"/c rmdir \"{path}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    process?.WaitForExit(5000);
                }
                catch
                {
                    // 删除链接失败时保留目录，避免误删外部文件
                }
                return;
            }
        }

        Directory.Delete(path, true);
    }

    private static void EnsureDirectChild(string parent, string child)
    {
        if (!IsDirectChild(parent, child))
            throw new InvalidDataException("版本路径不安全，已取消删除。");
    }

    private static bool IsDirectChild(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase)
               && !childFull[parentFull.Length..].Contains(Path.DirectorySeparatorChar);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
