using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ookii.Dialogs.Wpf;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class LocalVersionsPage : Page, IStandaloneSidebarPage
{
    private readonly ObservableCollection<LocalVersionEntry> _versions = new();
    public LocalVersionsPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _versions;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshVersions();
        VersionListBar.BeginAnimation(OpacityProperty, null);
        DetailFrame.BeginAnimation(OpacityProperty, null);
        VersionListBar.Opacity = 1;
        DetailFrame.Opacity = 1;
        if (App.Settings.Data.PageAnimationStyle is not ("none" or "tear"))
            AnimateIn();
    }

    private void RefreshVersions(string? selectedId = null)
    {
        _versions.Clear();
        var instances = new InstanceStore(App.Paths.InstancesDir).List();
        foreach (var instance in instances)
            _versions.Add(new LocalVersionEntry(instance));

        var represented = instances
            .SelectMany(instance => new[] { instance.VersionId, instance.McVersion })
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(App.Paths.VersionsDir))
        {
            foreach (var directory in Directory.EnumerateDirectories(App.Paths.VersionsDir))
            {
                var versionId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(versionId) || represented.Contains(versionId)) continue;
                if (!File.Exists(Path.Combine(directory, versionId + ".json"))) continue;
                _versions.Add(new LocalVersionEntry(new Instance
                {
                    Id = "local-" + versionId,
                    Name = "原版",
                    VersionId = versionId,
                    McVersion = versionId,
                    Loader = "vanilla",
                    VersionIsolation = true,
                    UsesVersionDirectory = true
                }));
            }
        }

        var ordered = _versions.OrderByDescending(entry => entry.CreatedAt).ToList();
        _versions.Clear();
        foreach (var entry in ordered) _versions.Add(entry);
        EmptyHint.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VersionList.Visibility = _versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_versions.Count > 0)
            VersionList.SelectedItem = _versions.FirstOrDefault(entry => entry.Instance.Id == selectedId)
                                       ?? _versions[0];
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not LocalVersionEntry entry) return;
        var detail = new InstanceDetailPage(entry.Instance);
        detail.InstanceVisualChanged += (_, _) => RefreshVersions(entry.Instance.Id);
        detail.InstanceDeleted += (_, _) =>
        {
            RefreshVersions();
            if (_versions.Count == 0) DetailFrame.Content = null;
        };
        DetailFrame.Navigate(detail);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateTo(mainWindow.HomePage);
    }

    private void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateTo(mainWindow.GetVersionsPage());
    }

    private void AddExistingFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog
        {
            Description = "选择已有的 Minecraft 游戏文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        AddExistingFolder(dialog.SelectedPath);
    }

    // （旧版单版本导入实现，保留以备回退）
    private void AddExistingFolderLegacy(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
        {
            AnimatedMessageBox.Show("所选路径不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var gameDir = DetectGameDirectory(selectedPath);
        if (gameDir == null)
        {
            AnimatedMessageBox.Show(
                "未在所选文件夹中检测到 Minecraft 游戏文件。\n\n请确保选择的是 .minecraft 文件夹或包含 versions 子目录的文件夹。",
                "无法识别", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir) || !Directory.EnumerateDirectories(versionsDir).Any())
        {
            AnimatedMessageBox.Show(
                "所选文件夹中没有已安装的 Minecraft 版本。",
                "无可用版本", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var versionEntries = Directory.EnumerateDirectories(versionsDir)
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return File.Exists(Path.Combine(dir, name + ".json"));
            })
            .Select(dir => Path.GetFileName(dir)!)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (versionEntries.Count == 0)
        {
            AnimatedMessageBox.Show(
                "versions 目录存在但未找到完整的游戏版本（需要存在 .json 文件）。",
                "无可用版本", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var detectedLoader = DetectLoaderFromDirectory(gameDir, versionEntries);
        var detectedMcVersion = DetectMcVersion(versionEntries);

        var folderName = Path.GetFileName(selectedPath);
        var instanceName = string.IsNullOrWhiteSpace(folderName) ? "导入的游戏" : folderName;

        var store = new InstanceStore(App.Paths.InstancesDir);

        // 防止重复添加同一文件夹
        var allExisting = store.List();
        var duplicate = allExisting.FirstOrDefault(i =>
            !string.IsNullOrWhiteSpace(i.CustomGameDir) &&
            string.Equals(i.CustomGameDir.TrimEnd('\\', '/'), gameDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (duplicate != null)
        {
            AnimatedMessageBox.Show(
                $"该文件夹已添加过（「{duplicate.Name}」）。\n\n路径: {gameDir}",
                "重复添加", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existingNames = allExisting.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existingNames.Contains(instanceName))
        {
            for (var suffix = 2; ; suffix++)
            {
                var candidate = instanceName + suffix;
                if (!existingNames.Contains(candidate))
                {
                    instanceName = candidate;
                    break;
                }
            }
        }

        var instanceId = new string(instanceName.Trim().Select(c =>
                char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-')
            .ToArray());
        while (instanceId.Contains("--")) instanceId = instanceId.Replace("--", "-");
        instanceId = instanceId.Trim('-', '_');
        if (string.IsNullOrWhiteSpace(instanceId)) instanceId = "imported";
        if (instanceId.Length > 64) instanceId = instanceId[..64].TrimEnd('-', '_');
        instanceId += "-" + Guid.NewGuid().ToString("N")[..6];

        var instance = new Instance
        {
            Id = instanceId,
            Name = instanceName,
            VersionId = detectedMcVersion,
            McVersion = detectedMcVersion,
            Loader = detectedLoader.loader,
            LoaderVersion = detectedLoader.version,
            VersionIsolation = null,
            UsesVersionDirectory = null,
            CustomGameDir = gameDir
        };

        store.Create(instance);
        RefreshVersions(instanceId);

        AnimatedMessageBox.Show(
            $"已添加「{instanceName}」\n\n版本: {detectedMcVersion}\n加载器: {(string.IsNullOrEmpty(detectedLoader.loader) || detectedLoader.loader == "vanilla" ? "原版" : detectedLoader.loader)}\n路径: {gameDir}",
            "添加成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void FolderDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        e.Effects = paths.Length == 1 && Directory.Exists(paths[0])
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.Length == 1 && Directory.Exists(paths[0]))
            AddExistingFolder(paths[0]);
        e.Handled = true;
    }

    // ===== 复制版本：生成一份完全独立的副本（自己的版本文件、mods、存档）=====

    private void DuplicateVersion_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not LocalVersionEntry entry)
        {
            AnimatedMessageBox.Show("请先在左侧选择要复制的版本。",
                "复制版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sourceId = string.IsNullOrWhiteSpace(entry.Instance.VersionId)
            ? entry.Instance.McVersion
            : entry.Instance.VersionId;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        var sourceDir = Path.Combine(App.Paths.VersionsDir, sourceId);
        if (!Directory.Exists(sourceDir))
        {
            AnimatedMessageBox.Show($"找不到版本文件夹：\n{sourceDir}",
                "复制版本", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newId = sourceId + "-副本";
        for (var suffix = 2; Directory.Exists(Path.Combine(App.Paths.VersionsDir, newId)); suffix++)
            newId = sourceId + "-副本" + suffix;

        var confirm = AnimatedMessageBox.Show(
            $"将把「{sourceId}」复制为「{newId}」，并创建一个独立实例。\n\n"
            + "副本拥有自己的 mods、存档与配置（版本隔离），与原版本互不影响。\n\n继续？",
            "复制版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var newDir = Path.Combine(App.Paths.VersionsDir, newId);
            Directory.CreateDirectory(newDir);

            // 参考 PCL2：副本不复制游戏文件，只写一个继承自原版本的 JSON。
            // 客户端 JAR 与依赖库与原版本共用，副本只占几 KB。
            var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var copyJson = new Newtonsoft.Json.Linq.JObject
            {
                ["id"] = newId,
                ["inheritsFrom"] = sourceId,
                ["type"] = "release",
                ["time"] = now,
                ["releaseTime"] = now,
                ["libraries"] = new Newtonsoft.Json.Linq.JArray()
            };
            File.WriteAllText(Path.Combine(newDir, newId + ".json"), copyJson.ToString());

            // 版本自己文件夹里的内容（mods、存档、配置）复制一份，保证初始状态一致
            CopyDirectoryContent(sourceDir, newDir, sourceId);

            var instance = new Instance
            {
                Id = "copy-" + Guid.NewGuid().ToString("N")[..8],
                Name = newId,
                VersionId = newId,
                McVersion = string.IsNullOrWhiteSpace(entry.Instance.McVersion) ? sourceId : entry.Instance.McVersion,
                Loader = entry.Instance.Loader,
                LoaderVersion = entry.Instance.LoaderVersion,
                VersionIsolation = true,
                UsesVersionDirectory = true
            };
            new InstanceStore(App.Paths.InstancesDir).Create(instance);
            RefreshVersions(instance.Id);

            AnimatedMessageBox.Show(
                $"副本「{newId}」已创建。\n\nmods 与存档位于 versions/{newId} 下，可以和原版本分别安装不同的 Mod。",
                "复制版本", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AnimatedMessageBox.Show("复制失败: " + ex.Message,
                "复制版本", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 复制版本文件夹的内容，但跳过版本本体文件（json / jar），它们由继承 JSON 代替
    private static void CopyDirectoryContent(string source, string target, string sourceVersionId)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(sourceVersionId + ".json", StringComparison.OrdinalIgnoreCase)
                || name.Equals(sourceVersionId + ".jar", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    // ===== 参考 PCL2 的「添加已有文件夹」：扫描整个游戏目录、预览勾选、批量链接导入 =====

    private sealed record ImportCandidate(
        string VersionId,
        string McVersion,
        string Loader,
        string LoaderVersion,
        bool Isolated,
        bool HasJar,
        string VersionDir,
        bool Flat = false);

    private void AddExistingFolder(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
        {
            AnimatedMessageBox.Show("所选路径不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var scan = ScanExistingFolder(selectedPath);
        if (scan == null)
        {
            AnimatedMessageBox.Show(
                "未在所选文件夹中检测到 Minecraft 版本。\n\n可以选 .minecraft 文件夹、其中的 versions 文件夹，或某个具体版本文件夹。",
                "无法识别", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (gameDir, candidates, skippedFolders) = scan.Value;
        var chosen = ImportPreviewDialog.Show(Window.GetWindow(this), gameDir, candidates, skippedFolders);
        if (chosen == null || chosen.Count == 0) return;

        var store = new InstanceStore(App.Paths.InstancesDir);
        var existing = store.List();
        var added = 0;
        var skipped = 0;
        var linkFailed = 0;
        string? lastId = null;

        foreach (var candidate in chosen)
        {
            var duplicated = existing.Any(instance =>
                !string.IsNullOrWhiteSpace(instance.CustomGameDir)
                && string.Equals(instance.CustomGameDir.TrimEnd('\\', '/'), gameDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(instance.VersionId, candidate.VersionId, StringComparison.OrdinalIgnoreCase));
            if (duplicated)
            {
                skipped++;
                continue;
            }

            var name = MakeUniqueInstanceName(candidate.VersionId, existing);
            var instance = new Instance
            {
                Id = MakeInstanceId(name) + "-" + Guid.NewGuid().ToString("N")[..6],
                Name = name,
                VersionId = candidate.VersionId,
                McVersion = candidate.McVersion,
                Loader = candidate.Loader,
                LoaderVersion = candidate.LoaderVersion,
                CustomGameDir = gameDir,
                VersionIsolation = candidate.Isolated,
                UsesVersionDirectory = candidate.Isolated ? true : null
            };
            store.Create(instance);
            existing.Add(instance);
            added++;
            lastId = instance.Id;

            // 版本文件不复制，链接到启动器 versions 目录，直接使用原文件夹
            if (!LinkVersionDirectory(candidate))
                linkFailed++;
        }

        RefreshVersions(lastId);

        var libraryLinks = LinkLibrariesDirectory(gameDir);

        // 版本隔离时，该版本的依赖库放在 versions/<版本名>/libraries 下，也要一并接入
        foreach (var candidate in chosen)
            libraryLinks += LinkLibrariesDirectory(candidate.VersionDir);

        var message = $"已添加 {added} 个版本";
        if (skipped > 0) message += $"\n{skipped} 个已在列表中，已跳过";
        if (linkFailed > 0) message += $"\n{linkFailed} 个版本文件未能链接，启动时会尝试重新下载";
        if (libraryLinks > 0) message += $"\n已接入游戏库目录 {libraryLinks} 个（不复制文件）";
        message += $"\n\n游戏目录: {gameDir}";
        AnimatedMessageBox.Show(message, "添加成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static (string gameDir, List<ImportCandidate> versions, List<string> skipped)? ScanExistingFolder(string selectedPath)
    {
        var gameDir = DetectGameDirectory(selectedPath);
        if (gameDir == null) return null;

        // 版本隔离（versions/<版本名>/ 自带 mods、saves 等）与未隔离（共用 .minecraft）
        // 两种存放方式都在 versions 目录下，扫描整个游戏目录即可全部覆盖
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir)) return null;

        var candidates = new List<ImportCandidate>();
        var skipped = new List<string>();
        var folderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(versionsDir))
        {
            var folderName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(folderName)) continue;
            folderIds.Add(folderName);

            // 优先用与文件夹同名的 JSON；找不到时在该文件夹里再找一个版本 JSON
            // （部分整合包/启动器的版本 JSON 文件名与文件夹名不一致）
            var jsonPath = Path.Combine(directory, folderName + ".json");
            if (!File.Exists(jsonPath))
            {
                jsonPath = FindVersionJson(directory);
                if (jsonPath == null)
                {
                    skipped.Add(folderName);
                    continue;
                }
            }

            var versionId = Path.GetFileNameWithoutExtension(jsonPath);
            var (mcVersion, loader, loaderVersion) = ReadVersionMetadata(jsonPath, versionId);
            candidates.Add(new ImportCandidate(
                versionId,
                mcVersion,
                loader,
                loaderVersion,
                HasOwnGameContent(directory),
                File.Exists(Path.Combine(directory, versionId + ".jar"))
                || File.Exists(Path.Combine(directory, folderName + ".jar")),
                directory));
        }

        // 兼容把版本 JSON/JAR 直接放在 versions 目录下（不建子文件夹）的启动器
        foreach (var file in Directory.EnumerateFiles(versionsDir, "*.json"))
        {
            var versionId = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(versionId) || folderIds.Contains(versionId)) continue;

            var (mcVersion, loader, loaderVersion) = ReadVersionMetadata(file, versionId);
            candidates.Add(new ImportCandidate(
                versionId,
                mcVersion,
                loader,
                loaderVersion,
                false,
                File.Exists(Path.Combine(versionsDir, versionId + ".jar")),
                versionsDir,
                true));
        }

        if (candidates.Count == 0) return null;
        return (gameDir,
            candidates.OrderBy(c => c.VersionId, StringComparer.OrdinalIgnoreCase).ToList(),
            skipped.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // 在版本文件夹里找一个真正的版本 JSON（含 mainClass / libraries / inheritsFrom）
    private static string? FindVersionJson(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var reader = new StreamReader(file);
                var buffer = new char[4096];
                var read = reader.Read(buffer, 0, buffer.Length);
                var text = new string(buffer, 0, read);
                if (text.Contains("\"mainClass\"") || text.Contains("\"libraries\"") || text.Contains("\"inheritsFrom\""))
                    return file;
            }
            catch
            {
                // 读不了的 JSON 直接跳过
            }
        }
        return null;
    }

    private static (string mcVersion, string loader, string loaderVersion) ReadVersionMetadata(string jsonPath, string versionId)
    {
        string json;
        try
        {
            json = File.ReadAllText(jsonPath);
        }
        catch
        {
            return (versionId, "vanilla", "");
        }

        var (loader, loaderVersion) = DetectLoaderFromJson(json);

        var mcVersion = "";
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            mcVersion = obj.Value<string>("inheritsFrom") ?? "";
            if (string.IsNullOrWhiteSpace(mcVersion))
                mcVersion = obj.Value<string>("id") ?? "";
        }
        catch
        {
            // JSON 解析失败时退回用文件夹名当版本号
        }

        if (string.IsNullOrWhiteSpace(mcVersion)) mcVersion = versionId;
        var match = System.Text.RegularExpressions.Regex.Match(mcVersion, @"^(1\.\d+(\.\d+)?)");
        if (match.Success) mcVersion = match.Groups[1].Value;

        return (mcVersion, loader, loaderVersion);
    }

    private static bool HasOwnGameContent(string versionDir)
    {
        foreach (var name in new[] { "mods", "saves", "config", "resourcepacks", "shaderpacks", "logs", "screenshots", "options.txt", "servers.dat" })
        {
            var path = Path.Combine(versionDir, name);
            if (File.Exists(path) || Directory.Exists(path)) return true;
        }
        return false;
    }

    private static bool LinkVersionDirectory(ImportCandidate candidate)
    {
        Directory.CreateDirectory(App.Paths.VersionsDir);
        var target = Path.Combine(App.Paths.VersionsDir, candidate.VersionId);
        if (Directory.Exists(target)) return true;

        // 版本 JSON/JAR 原本平铺在 versions 目录下：建同名文件夹并硬链接（失败则复制）
        if (candidate.Flat)
        {
            try
            {
                Directory.CreateDirectory(target);
                var jsonSource = Path.Combine(candidate.VersionDir, candidate.VersionId + ".json");
                if (!LinkOrCopyFile(jsonSource, Path.Combine(target, candidate.VersionId + ".json")))
                    return false;

                var jarSource = Path.Combine(candidate.VersionDir, candidate.VersionId + ".jar");
                if (File.Exists(jarSource))
                    LinkOrCopyFile(jarSource, Path.Combine(target, candidate.VersionId + ".jar"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        return CreateJunction(candidate.VersionDir, target);
    }

    private static bool LinkOrCopyFile(string source, string target)
    {
        if (File.Exists(target)) return true;
        if (!File.Exists(source)) return false;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /H \"{target}\" \"{source}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(5000);
            if (File.Exists(target)) return true;
        }
        catch
        {
            // 硬链接失败时退回复制
        }

        try
        {
            File.Copy(source, target, overwrite: false);
            return File.Exists(target);
        }
        catch
        {
            return false;
        }
    }

    // 外部文件夹的 libraries（Forge/Fabric 等依赖库）也一并接进来，
    // 否则启动时会把这些库当成缺失文件重新下载
    private static int LinkLibrariesDirectory(string gameDir)
    {
        var externalLibraries = Path.Combine(gameDir, "libraries");
        if (!Directory.Exists(externalLibraries)) return 0;

        var targetRoot = App.Paths.LibrariesDir;
        Directory.CreateDirectory(targetRoot);
        return LinkMissingDirectories(externalLibraries, targetRoot);
    }

    // 递归合并目录树：目标已有则继续深入，缺失的整棵用目录联接引入（不复制文件）
    private static int LinkMissingDirectories(string sourceDir, string targetDir)
    {
        var linked = 0;
        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var target = Path.Combine(targetDir, name);
            if (Directory.Exists(target))
            {
                linked += LinkMissingDirectories(directory, target);
                continue;
            }

            if (CreateJunction(directory, target))
            {
                linked++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(target);
                linked += LinkMissingDirectories(directory, target);
            }
            catch
            {
                // 无法创建目标目录时跳过该分支
            }
        }
        return linked;
    }

    private static bool CreateJunction(string source, string target)
    {
        if (Directory.Exists(target)) return true;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{target}\" \"{source}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit(5000);
            return Directory.Exists(target);
        }
        catch
        {
            return false;
        }
    }

    private static string MakeUniqueInstanceName(string baseName, List<Instance> existing)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "导入的游戏" : baseName;
        var names = existing.Select(instance => instance.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = name + suffix;
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private static string MakeInstanceId(string name)
    {
        var id = new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-')
            .ToArray());
        while (id.Contains("--")) id = id.Replace("--", "-");
        id = id.Trim('-', '_');
        if (string.IsNullOrWhiteSpace(id)) id = "imported";
        return id.Length > 64 ? id[..64].TrimEnd('-', '_') : id;
    }

    // 预览勾选窗口：列出扫描到的版本（版本号 / 加载器 / 隔离方式），确认后批量添加
    private sealed class ImportPreviewDialog
    {
        public static List<ImportCandidate>? Show(
            Window? owner, string gameDir, List<ImportCandidate> candidates, List<string> skipped)
        {
            var window = new Window
            {
                Title = "添加已有文件夹",
                Width = 580,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                Background = Brushes.Transparent
            };

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20)
            };
            card.SetResourceReference(Border.BackgroundProperty, "DialogCardBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var root = new StackPanel();

            var title = new TextBlock
            {
                Text = "添加已有文件夹",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            root.Children.Add(title);

            var subtitleText = $"游戏目录：{gameDir}\n共找到 {candidates.Count} 个版本，勾选后点击「添加」。";
            if (skipped.Count > 0)
            {
                var names = string.Join("、", skipped.Take(8));
                subtitleText += $"\n未识别 {skipped.Count} 个文件夹（缺少与文件夹同名的 .json）：{names}"
                                + (skipped.Count > 8 ? " …" : "");
            }

            var subtitle = new TextBlock
            {
                Text = subtitleText,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 14)
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            subtitle.Opacity = 0.7;
            root.Children.Add(subtitle);

            var listPanel = new StackPanel();
            var boxes = new List<(CheckBox Box, ImportCandidate Candidate)>();
            foreach (var candidate in candidates)
            {
                var label = string.IsNullOrWhiteSpace(candidate.Loader)
                            || candidate.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                    ? candidate.VersionId
                    : $"{candidate.VersionId}  ·  {char.ToUpperInvariant(candidate.Loader[0]) + candidate.Loader[1..]}"
                      + (string.IsNullOrWhiteSpace(candidate.LoaderVersion) ? "" : " " + candidate.LoaderVersion);

                var flags = new List<string>();
                if (candidate.Isolated) flags.Add("版本隔离");
                if (!candidate.HasJar) flags.Add("无 .jar");
                if (flags.Count > 0) label += $"  [{(string.Join("、", flags))}]";

                var box = new CheckBox
                {
                    Content = label,
                    IsChecked = true,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                boxes.Add((box, candidate));
                listPanel.Children.Add(box);
            }

            root.Children.Add(new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 280,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectAll = MakeButton("全选 / 全不选", "BtnBase");
            selectAll.Click += (_, _) =>
            {
                var allChecked = boxes.All(entry => entry.Box.IsChecked == true);
                foreach (var entry in boxes) entry.Box.IsChecked = !allChecked;
            };
            Grid.SetColumn(selectAll, 0);
            actions.Children.Add(selectAll);

            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = MakeButton("取消", "BtnBase");
            cancel.Margin = new Thickness(0, 0, 10, 0);
            var confirm = MakeButton("添加", "BtnPrimary");
            rightPanel.Children.Add(cancel);
            rightPanel.Children.Add(confirm);
            Grid.SetColumn(rightPanel, 2);
            actions.Children.Add(rightPanel);
            root.Children.Add(actions);

            List<ImportCandidate>? result = null;
            cancel.Click += (_, _) => window.Close();
            confirm.Click += (_, _) =>
            {
                result = boxes.Where(entry => entry.Box.IsChecked == true)
                    .Select(entry => entry.Candidate)
                    .ToList();
                window.Close();
            };

            card.Child = root;
            window.Content = new Grid { Margin = new Thickness(24), Children = { card } };
            window.ShowDialog();
            return result;
        }

        private static Button MakeButton(string text, string styleKey)
        {
            var button = new Button
            {
                Content = text,
                Height = 34,
                Padding = new Thickness(16, 4, 16, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
            return button;
        }
    }

    private static string DetectMcVersion(List<string> versionEntries)
    {
        // 优先选纯版本号（如 1.20.1、1.21.4），取最新
        var pureVersions = versionEntries
            .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, @"^1\.\d+(\.\d+)?$"))
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pureVersions.Count > 0)
            return pureVersions[0];

        // 次选含加载器后缀的版本号（如 1.20.1-fabric-0.15.3）
        var loaderVersions = versionEntries
            .Where(v => v.Contains('-'))
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (loaderVersions.Count > 0)
        {
            // 从 "1.20.1-fabric-0.15.3" 提取 "1.20.1"
            var match = System.Text.RegularExpressions.Regex.Match(loaderVersions[0], @"^(1\.\d+(\.\d+)?)");
            if (match.Success)
                return match.Groups[1].Value;
        }

        // 兜底：第一个版本
        return versionEntries[0];
    }

    private static string? DetectGameDirectory(string selectedPath)
    {
        // 选中的就是游戏根目录（.minecraft）
        if (Directory.Exists(Path.Combine(selectedPath, "versions")))
            return selectedPath;

        var trimmed = selectedPath.TrimEnd('\\', '/');
        var folderName = Path.GetFileName(trimmed);

        // 选中的是 versions 目录本身
        if (folderName.Equals("versions", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(trimmed)?.FullName;

        // 选中的是单个版本文件夹（如 .minecraft/versions/1.20.1）
        if (!string.IsNullOrWhiteSpace(folderName)
            && File.Exists(Path.Combine(trimmed, folderName + ".json")))
        {
            var parent = Directory.GetParent(trimmed);
            if (parent != null && Path.GetFileName(parent.FullName).Equals("versions", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(parent.FullName)?.FullName;
        }

        // 选中的是 versions 下的其他子目录
        var up = Directory.GetParent(trimmed);
        if (up != null && Directory.Exists(Path.Combine(up.FullName, "versions")))
            return up.FullName;

        return null;
    }

    private static (string loader, string version) DetectLoaderFromDirectory(string gameDir, List<string> versionEntries)
    {
        var versionsDir = Path.Combine(gameDir, "versions");

        // 扫描所有版本文件夹，找加载器线索
        foreach (var versionId in versionEntries.OrderByDescending(v => v))
        {
            var jsonPath = Path.Combine(versionsDir, versionId, versionId + ".json");
            if (!File.Exists(jsonPath)) continue;
            try
            {
                var json = File.ReadAllText(jsonPath);
                var result = DetectLoaderFromJson(json);
                if (result.loader != "vanilla")
                    return result;
            }
            catch { }
        }
        return ("vanilla", "");
    }

    private static (string loader, string version) DetectLoaderFromJson(string json)
    {
        // === 在完整 JSON 字符串中搜索库名 ===

        // Fabric / Quilt: 搜索 "net.fabricmc:fabric-loader" 或 "org.quiltmc:quilt-loader"
        if (json.Contains("net.fabricmc:fabric-loader") || json.Contains("org.quiltmc:quilt-loader"))
        {
            var version = RegexVersion(json, @"net\.fabricmc:fabric-loader:([0-9\.]+(\+build\.[0-9]+)?)")
                      ?? RegexVersion(json, @"org\.quiltmc:quilt-loader:([0-9\.]+)")
                      ?? "";
            version = version.Replace("+build", "");
            return ("fabric", version);
        }

        // Forge: 搜索 "minecraftforge"（排除 NeoForge）
        if (json.Contains("minecraftforge") && !json.Contains("net.neoforge"))
        {
            var version = RegexVersion(json, @"forge:[0-9\.]+(_pre[0-9]*)?-([0-9\.]+)")
                      ?? RegexVersion(json, @"net\.minecraftforge:minecraftforge:([0-9\.]+)")
                      ?? RegexVersion(json, @"net\.minecraftforge:fmlloader:[0-9\.]+-([0-9\.]+)")
                      ?? "";
            return ("forge", version);
        }

        // NeoForge: 搜索 "net.neoforge"
        if (json.Contains("net.neoforge"))
        {
            // 从 arguments 中提取：--fml.neoForgeVersion", "20.6.119-beta
            var version = RegexVersion(json, @"forgeVersion"",""([^""]+)""") ?? "";
            return ("neoforge", version);
        }

        // OptiFine: 搜索 "optifine"
        if (json.Contains("optifine", StringComparison.OrdinalIgnoreCase))
        {
            var version = RegexVersion(json, @"HD_U_([^""/:]+)") ?? "";
            return ("optifine", version);
        }

        // LiteLoader: 搜索 "liteloader"
        if (json.Contains("liteloader", StringComparison.OrdinalIgnoreCase))
            return ("liteloader", "");

        return ("vanilla", "");
    }

    private static string? RegexVersion(string input, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private void AnimateIn()
    {
        var style = App.Settings.Data.PageAnimationStyle;
        var quick = style == "quick";
        var duration = quick ? 180 : 450;
        var barTransform = new TranslateTransform(-260, 0);
        var detailTransform = new TranslateTransform(350, 0);
        VersionListBar.RenderTransform = barTransform;
        DetailFrame.RenderTransform = detailTransform;
        barTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(quick ? -100 : -260, 0, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        detailTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(quick ? 140 : 350, 0, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
    }

    public async Task AnimateStandaloneExitAsync(bool quick)
    {
        var duration = quick ? 180 : 450;
        var barTransform = new TranslateTransform();
        var detailTransform = new TranslateTransform();
        VersionListBar.RenderTransform = barTransform;
        DetailFrame.RenderTransform = detailTransform;
        barTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, quick ? -100 : -260, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });
        detailTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, quick ? 140 : 350, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });
        await Task.Delay(duration);
    }

    private sealed class LocalVersionEntry
    {
        public LocalVersionEntry(Instance instance)
        {
            Instance = instance;
            Name = string.IsNullOrWhiteSpace(instance.Name) ? instance.McVersion : instance.Name;
            var root = InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
            CreatedAt = Directory.Exists(root)
                ? Directory.GetCreationTime(root)
                : File.GetCreationTime(Path.Combine(App.Paths.VersionsDir, instance.VersionId, instance.VersionId + ".json"));
            Icon = InstanceIconService.Load(instance);
            var minecraft = string.IsNullOrWhiteSpace(instance.McVersion) ? instance.VersionId : instance.McVersion;
            var loader = string.IsNullOrWhiteSpace(instance.Loader) ? "vanilla" : instance.Loader;
            VersionLabel = loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                ? minecraft
                : $"{minecraft} · {char.ToUpperInvariant(loader[0]) + loader[1..]}";
            CreatedLabel = !string.IsNullOrWhiteSpace(instance.CustomGameDir)
                ? $"外部导入 · {instance.CustomGameDir}"
                : $"创建于 {CreatedAt:yyyy-MM-dd HH:mm}";
        }

        public Instance Instance { get; }
        public string Name { get; }
        public string VersionLabel { get; }
        public string CreatedLabel { get; }
        public DateTime CreatedAt { get; }
        public ImageSource Icon { get; }
    }
}
