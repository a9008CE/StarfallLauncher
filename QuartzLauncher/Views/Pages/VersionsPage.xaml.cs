using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class VersionsPage : Page
{
    private static readonly Dictionary<string, BitmapSource> VersionIcons = new()
    {
        ["grass"] = LoadVersionIcon("grass_block.png"),
        ["command"] = LoadVersionIcon("command_block.png"),
        ["redstone"] = LoadVersionIcon("redstone.png")
    };
    private readonly MinecraftService _mc;
    private readonly LoaderService _loader;
    private List<Dictionary<string, object>> _allVersions = new();
    private string _selectedVersion = "";

    private string _filterType = "release";
    private bool _isFilterAnimating;
    private CancellationTokenSource? _versionLoadCts;
    private int _listRenderGeneration;

    public VersionsPage()
    {
        InitializeComponent();
        _mc = new MinecraftService(App.Paths, App.Settings);
        _loader = new LoaderService(App.Paths, App.Settings);
        Loaded += OnLoaded;
        SearchBox.TextChanged += SearchBox_Changed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateFilterButtons();
        UpdateLoaderInfoText();
        if (_allVersions.Count == 0)
            await RefreshVersionsAsync();
        else
            await FilterVersionsAsync();
    }

    private void UpdateLoaderInfoText()
    {
        var selected = LoaderPickerPage.SelectedLoaders;
        if (selected.Count == 0)
        {
            LoaderInfoText.Text = "原版 (无 Mod 加载器)";
            CreateInstanceBtn.IsEnabled = false;
        }
        else
        {
            var labels = selected.Values
                .Where(selection => selection.Type != "fabricapi")
                .Select(selection =>
                {
                    var name = selection.Type switch
                    {
                        "fabric" => "Fabric",
                        "forge" => "Forge",
                        "neoforge" => "NeoForge",
                        "quilt" => "Quilt",
                        "optifine" => "OptiFine",
                        "liteloader" => "LiteLoader",
                        _ => selection.Type
                    };
                    var version = string.IsNullOrWhiteSpace(selection.Version) ? "自动" : selection.Version;
                    return $"{name} [{version}]";
                }).ToList();
            if (selected.ContainsKey("fabricapi"))
                labels.Add("Fabric API");
            LoaderInfoText.Text = string.Join(" + ", labels);
            CreateInstanceBtn.IsEnabled = true;
        }
        UpdateInstanceName();
    }

    private async Task RefreshVersionsAsync()
    {
        _versionLoadCts?.Cancel();
        var cts = _versionLoadCts = new CancellationTokenSource();
        var localVersions = GetLocalVersionIds();
        try
        {
            InstallStatusText.Text = "正在加载版本列表...";
            var versions = await Task.Run(() => _mc.AvailableVersions(true), cts.Token);
            if (cts.IsCancellationRequested) return;
            _allVersions = MergeLocalVersions(versions, localVersions);
            await FilterVersionsAsync();
            if (!cts.IsCancellationRequested) InstallStatusText.Text = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _allVersions = MergeLocalVersions([], localVersions);
            await FilterVersionsAsync();
            InstallStatusText.Text = localVersions.Count > 0
                ? $"在线列表加载失败，已显示 {localVersions.Count} 个本地版本"
                : $"加载失败: {ex.Message}";
        }
    }

    private async Task FilterVersionsAsync()
    {
        var generation = ++_listRenderGeneration;
        VersionListBox.Items.Clear();
        var query = SearchBox?.Text?.ToLower() ?? "";
        var rendered = 0;
        ListBoxItem? selectedItem = null;
        var versionsDir = App.Paths.VersionsDir;
        var localVersions = GetLocalVersionIds();
        foreach (var v in _allVersions.OrderByDescending(version =>
                     localVersions.Contains(version.TryGetValue("id", out var value) ? value?.ToString() ?? "" : "")))
        {
            if (generation != _listRenderGeneration) return;
            var id = v.TryGetValue("id", out var idObj) ? idObj?.ToString() ?? "" : "";
            var type = v.TryGetValue("type", out var tObj) ? tObj?.ToString() ?? "" : "";
            if (!string.IsNullOrEmpty(query) && !id.ToLower().Contains(query)) continue;

            var match = _filterType switch
            {
                "release" => type == "release",
                "snapshot" => type == "snapshot",
                "april_fools" => IsAprilFoolsVersion(id),
                _ => true
            };
            if (!match) continue;

            var releaseTime = v.TryGetValue("releaseTime", out var rtObj) ? rtObj?.ToString() ?? "" : "";
            if (!string.IsNullOrEmpty(releaseTime) && releaseTime.Length >= 10)
                releaseTime = releaseTime[..10];
            var iconKey = _filterType == "april_fools" || IsAprilFoolsVersion(id)
                ? "redstone"
                : _filterType == "snapshot" || type == "snapshot" ? "command" : "grass";

            var isDownloaded = IsVersionComplete(id);

            var panel = new DockPanel { Tag = id };
            var dateText = new TextBlock
            {
                Text = releaseTime,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(dateText, Dock.Right);

            var iconLayer = new Grid
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconImage = new Image
            {
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.NearestNeighbor);
            iconImage.Source = VersionIcons[iconKey];
            iconLayer.Children.Add(iconImage);
            DockPanel.SetDock(iconLayer, Dock.Left);

            var versionText = new TextBlock
            {
                Text = id,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            panel.Children.Add(dateText);
            panel.Children.Add(iconLayer);
            panel.Children.Add(versionText);

            if (isDownloaded)
            {
                var downloadedBadge = new TextBlock
                {
                    Text = "已下载",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("PrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                DockPanel.SetDock(downloadedBadge, Dock.Right);
                panel.Children.Add(downloadedBadge);
            }

            var item = new ListBoxItem
            {
                Content = panel,
                Tag = id,
                Padding = new Thickness(8, 6, 8, 6),
                Foreground = (Brush)FindResource("TextBrush")
            };
            VersionListBox.Items.Add(item);
            if (id == _selectedVersion)
                selectedItem = item;
            if (++rendered % 24 == 0)
                await Dispatcher.Yield(DispatcherPriority.Background);
        }

        if (generation == _listRenderGeneration && selectedItem != null)
        {
            selectedItem.IsSelected = true;
            VersionListBox.ScrollIntoView(selectedItem);
        }
    }

    private async void FilterRelease_Click(object sender, RoutedEventArgs e) => await SwitchFilterAsync("release");
    private async void FilterSnapshot_Click(object sender, RoutedEventArgs e) => await SwitchFilterAsync("snapshot");
    private async void FilterAprilFools_Click(object sender, RoutedEventArgs e) => await SwitchFilterAsync("april_fools");

    private async Task SwitchFilterAsync(string filterType)
    {
        if (_isFilterAnimating || _filterType == filterType) return;
        _isFilterAnimating = true;
        BtnRelease.IsHitTestVisible = false;
        BtnSnapshot.IsHitTestVisible = false;
        BtnAprilFools.IsHitTestVisible = false;

        try
        {
            var exitDuration = TimeSpan.FromMilliseconds(110);
            VersionListBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, exitDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            });
            VersionListTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -14, exitDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            });
            await Task.Delay(exitDuration);

            VersionListBox.BeginAnimation(OpacityProperty, null);
            VersionListTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _filterType = filterType;
            UpdateFilterButtons();
            await FilterVersionsAsync();

            var enterDuration = TimeSpan.FromMilliseconds(220);
            VersionListBox.Opacity = 1;
            VersionListTranslate.X = 0;
            VersionListBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, enterDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            VersionListTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(16, 0, enterDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            await Task.Delay(enterDuration);
        }
        finally
        {
            _isFilterAnimating = false;
            BtnRelease.IsHitTestVisible = true;
            BtnSnapshot.IsHitTestVisible = true;
            BtnAprilFools.IsHitTestVisible = true;
        }
    }

    private void UpdateFilterButtons()
    {
        var activeBrush = (Brush)FindResource("PrimaryBrush");
        var inactiveBrush = (Brush)FindResource("TextMutedBrush");

        BtnRelease.Foreground = _filterType == "release" ? Brushes.White : inactiveBrush;
        BtnRelease.Background = _filterType == "release" ? activeBrush : Brushes.Transparent;

        BtnSnapshot.Foreground = _filterType == "snapshot" ? Brushes.White : inactiveBrush;
        BtnSnapshot.Background = _filterType == "snapshot" ? activeBrush : Brushes.Transparent;

        BtnAprilFools.Foreground = _filterType == "april_fools" ? Brushes.White : inactiveBrush;
        BtnAprilFools.Background = _filterType == "april_fools" ? activeBrush : Brushes.Transparent;
    }

    private async void SearchBox_Changed(object? sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        await FilterVersionsAsync();
    }

    private void Version_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (VersionListBox.SelectedItem is not ListBoxItem item) return;
        _selectedVersion = item.Tag?.ToString() ?? "";
        SelectedVersionText.Text = _selectedVersion;
        VersionTypeText.Text = "";
        InstallBtn.IsEnabled = true;
        UpdateInstanceName();
    }

    internal void RestoreVersionSelection(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;
        _selectedVersion = versionId;
        SelectedVersionText.Text = versionId;
        VersionTypeText.Text = "";
        InstallBtn.IsEnabled = true;
        UpdateLoaderInfoText();

        var item = VersionListBox.Items.OfType<ListBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), versionId, StringComparison.Ordinal));
        if (item != null)
        {
            item.IsSelected = true;
            VersionListBox.ScrollIntoView(item);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshVersionsAsync();
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入整合包",
            Filter = "整合包 (*.qpack;*.mrpack;*.zip)|*.qpack;*.mrpack;*.zip|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        CreateInstanceBtn.IsEnabled = false;
        InstallBtn.IsEnabled = false;
        InstallStatusText.Text = "正在读取整合包清单...";
        var ownerWindow = Window.GetWindow(this) as MainWindow;
        try
        {
            var pack = new ModpackService();
            var descriptor = await Task.Run(() => pack.Inspect(dialog.FileName));
            var loaderText = descriptor.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                ? "原版"
                : $"{descriptor.Loader} {descriptor.LoaderVersion}".Trim();
            if (AnimatedMessageBox.Show(
                    $"名称: {descriptor.Name}\nMinecraft: {descriptor.MinecraftVersion}\n加载器: {loaderText}\n格式: {descriptor.Summary}\n\n开始导入？",
                    "确认整合包", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var loader = descriptor.Loader.ToLowerInvariant() switch
            {
                "fabric" => "fabric",
                "forge" => "forge",
                "neoforge" => "neoforge",
                "quilt" => "quilt",
                _ => ""
            };
            var selections = new Dictionary<string, LoaderPickerPage.LoaderSelection>();
            if (!string.IsNullOrEmpty(loader))
            {
                selections[loader] = new LoaderPickerPage.LoaderSelection
                {
                    Type = loader,
                    Version = loader == "forge"
                              && !descriptor.LoaderVersion.StartsWith(descriptor.MinecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
                        ? $"{descriptor.MinecraftVersion}-{descriptor.LoaderVersion}"
                        : descriptor.LoaderVersion,
                    MinecraftVersion = descriptor.MinecraftVersion,
                    AutoInstall = true
                };
            }

            var proposedName = string.IsNullOrWhiteSpace(descriptor.Name)
                ? $"导入-{descriptor.MinecraftVersion}"
                : descriptor.Name.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (proposedName.Length > 80) proposedName = proposedName[..80].Trim();
            var name = EnsureUniqueInstanceName(proposedName);
            InstallStatusText.Text = $"正在准备 Minecraft {descriptor.MinecraftVersion}...";
            var baseItems = await _mc.PrepareInstallAsync(descriptor.MinecraftVersion);
            var baseTask = DownloadManager.Instance.Enqueue(
                $"导入 {name} 的 Minecraft {descriptor.MinecraftVersion}",
                baseItems,
                workers: App.Settings.Data.DownloadWorkers,
                category: "version",
                showWhenEmpty: true);
            ownerWindow?.NavigateToDownloadCenter(versionOnly: true);
            if (baseTask.Tcs != null) await baseTask.Tcs.Task;
            if (baseTask.Status != DownloadTaskStatus.Completed)
                throw new IOException($"基础游戏文件下载失败: {baseTask.Error}");

            var installedVersionId = descriptor.MinecraftVersion;
            if (selections.Count > 0)
            {
                InstallStatusText.Text = $"正在安装 {descriptor.Loader}...";
                installedVersionId = await InstallPrimaryLoaderAsync(descriptor.MinecraftVersion, selections);
            }

            var instanceId = CreateInstanceId(name);
            var stagingRoot = Path.Combine(App.Paths.TempDir, $"modpack-{instanceId}");
            var instanceRoot = Path.Combine(App.Paths.InstancesDir, instanceId);
            var committed = false;
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            Directory.CreateDirectory(stagingRoot);
            try
            {
                pack.ImportContent(dialog.FileName, stagingRoot);
                var remoteFiles = await pack.PrepareRemoteFilesAsync(dialog.FileName, stagingRoot);
                if (remoteFiles.Count > 0)
                {
                    InstallStatusText.Text = $"正在下载整合包内容（{remoteFiles.Count} 个文件）...";
                    var contentTask = DownloadManager.Instance.Enqueue(
                    $"整合包内容 · {name}",
                    remoteFiles,
                    workers: App.Settings.Data.DownloadWorkers,
                    category: "version",
                    showWhenEmpty: true);
                    if (contentTask.Tcs != null) await contentTask.Tcs.Task;
                    if (contentTask.Status != DownloadTaskStatus.Completed)
                        throw new IOException($"整合包内容下载失败: {contentTask.Error}");
                }

                var instance = new Instance
                {
                    Id = instanceId,
                    Name = name,
                    VersionId = installedVersionId,
                    McVersion = descriptor.MinecraftVersion,
                    Loader = string.IsNullOrWhiteSpace(descriptor.Loader) ? "vanilla" : descriptor.Loader.ToLowerInvariant(),
                    LoaderVersion = descriptor.LoaderVersion,
                    VersionIsolation = true,
                    UsesVersionDirectory = false,
                    MemoryMb = descriptor.MemoryMb,
                    JvmArguments = descriptor.JvmArguments,
                    GameArguments = descriptor.GameArguments
                };
                if (Directory.Exists(instanceRoot)) throw new IOException("目标实例目录已存在。");
                Directory.Move(stagingRoot, instanceRoot);
                InstancePathService.EnsureGameDirectory(App.Paths, App.Settings.Data, instance);
                var markerDir = Path.Combine(App.Paths.VersionsDir, descriptor.MinecraftVersion);
                Directory.CreateDirectory(markerDir);
                File.WriteAllText(Path.Combine(markerDir, ".quartz-installed"), DateTimeOffset.UtcNow.ToString("O"));
                new InstanceStore(App.Paths.InstancesDir).Create(instance);
                committed = true;
            }
            catch
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
                if (!committed && Directory.Exists(instanceRoot)) Directory.Delete(instanceRoot, true);
                throw;
            }

            InstallStatusText.Text = $"已导入：{name}";
            AnimatedMessageBox.Show(
                $"整合包已导入为「{name}」。\n\n版本: {descriptor.MinecraftVersion}\n格式: {descriptor.Format}",
                "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            ownerWindow?.NavigateTo(ownerWindow.HomePage);
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = $"导入失败: {ex.Message}";
            AnimatedMessageBox.Show(ex.Message, "整合包导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CreateInstanceBtn.IsEnabled = LoaderPickerPage.SelectedLoaders.Count > 0;
            InstallBtn.IsEnabled = !string.IsNullOrEmpty(_selectedVersion);
        }
    }

    // 重复下载同一版本：新建一个独立的版本文件夹（Minecraft 不允许两个同名版本）
    private string CreateDuplicateVersion(string sourceId)
    {
        var newId = sourceId + "-2";
        for (var suffix = 3; Directory.Exists(Path.Combine(App.Paths.VersionsDir, newId)); suffix++)
            newId = sourceId + "-" + suffix;

        var newDir = Path.Combine(App.Paths.VersionsDir, newId);
        Directory.CreateDirectory(newDir);

        // 继承原版本：副本只存一个 JSON，客户端 JAR 与依赖库共用
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var json = new Newtonsoft.Json.Linq.JObject
        {
            ["id"] = newId,
            ["inheritsFrom"] = sourceId,
            ["type"] = "release",
            ["time"] = now,
            ["releaseTime"] = now,
            ["libraries"] = new Newtonsoft.Json.Linq.JArray()
        };
        File.WriteAllText(Path.Combine(newDir, newId + ".json"), json.ToString());

        var baseVersion = System.Text.RegularExpressions.Regex.Match(sourceId, @"^(1\.\d+(\.\d+)?)");
        var mcVersion = baseVersion.Success ? baseVersion.Groups[1].Value : sourceId;
        var lower = sourceId.ToLowerInvariant();
        var loader = lower.Contains("neoforge") ? "neoforge"
            : lower.Contains("forge") ? "forge"
            : lower.Contains("fabric") ? "fabric"
            : lower.Contains("quilt") ? "quilt"
            : lower.Contains("optifine") ? "optifine"
            : "vanilla";

        var instance = new Instance
        {
            Id = $"copy-{Guid.NewGuid().ToString("N")[..8]}",
            Name = newId,
            VersionId = newId,
            McVersion = mcVersion,
            Loader = loader,
            VersionIsolation = true,
            UsesVersionDirectory = true
        };
        new InstanceStore(App.Paths.InstancesDir).Create(instance);
        return newId;
    }

    // 同一个版本可以建多份实例：共用版本文件，其余各自独立
    private void CreateDuplicateInstance(string versionId)
    {
        var store = new InstanceStore(App.Paths.InstancesDir);
        var existing = store.List();
        var names = existing.Select(instance => instance.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = versionId;
        for (var suffix = 2; names.Contains(name); suffix++)
            name = $"{versionId}-{suffix}";

        var id = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-').ToArray());
        while (id.Contains("--")) id = id.Replace("--", "-");
        id = id.Trim('-', '_');
        if (string.IsNullOrWhiteSpace(id)) id = "instance";
        if (id.Length > 64) id = id[..64].TrimEnd('-', '_');

        var baseVersion = System.Text.RegularExpressions.Regex.Match(versionId, @"^(1\.\d+(\.\d+)?)");
        var mcVersion = baseVersion.Success ? baseVersion.Groups[1].Value : versionId;

        var lower = versionId.ToLowerInvariant();
        var loader = lower.Contains("neoforge") ? "neoforge"
            : lower.Contains("forge") ? "forge"
            : lower.Contains("fabric") ? "fabric"
            : lower.Contains("quilt") ? "quilt"
            : lower.Contains("optifine") ? "optifine"
            : "vanilla";

        var instance = new Instance
        {
            Id = $"{id}-{Guid.NewGuid().ToString("N")[..6]}",
            Name = name,
            VersionId = versionId,
            McVersion = mcVersion,
            Loader = loader,
            VersionIsolation = true,
            UsesVersionDirectory = true
        };
        store.Create(instance);

        InstallStatusText.Text = $"已新建实例「{name}」";
        AnimatedMessageBox.Show(
            $"已新建实例「{name}」。\n\n共用 {versionId} 的版本文件，mods 与存档各自独立。\n可在首页的版本下拉框中选到它。",
            "新建实例", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedVersion)) return;
        InstallBtn.IsEnabled = false;
        InstallStatusText.Text = $"正在准备 {_selectedVersion}...";
        try
        {
            var items = await _mc.PrepareInstallAsync(_selectedVersion);

            // 已经装过的版本：询问是再新建一份实例，还是只补全缺失/损坏的文件
            var force = false;
            if (IsVersionComplete(_selectedVersion))
            {
                var choice = AnimatedMessageBox.Show(
                    $"「{_selectedVersion}」已经安装过了，想怎么处理？\n\n"
                    + "新建独立版本：另建一个版本文件夹，拥有自己的 mods、存档与配置\n"
                    + "补全文件：保留原版本，只修复缺失或损坏的文件",
                    "该版本已存在", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                    (Yes: "新建独立版本", No: "补全文件", Cancel: "取消"));

                if (choice == MessageBoxResult.Cancel)
                {
                    InstallBtn.IsEnabled = true;
                    InstallStatusText.Text = "";
                    return;
                }
                if (choice == MessageBoxResult.Yes)
                {
                    var duplicateId = CreateDuplicateVersion(_selectedVersion);
                    InstallStatusText.Text = $"已新建独立版本：{duplicateId}";
                    AnimatedMessageBox.Show(
                        $"已新建独立版本「{duplicateId}」：\n\n"
                        + "它有自己的版本文件夹、mods、存档与配置，与原版本互不影响。\n"
                        + "客户端 JAR 与依赖库和原版本共用，不额外占用空间。",
                        "已新建独立版本", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            else if (File.Exists(Path.Combine(App.Paths.VersionsDir, _selectedVersion, _selectedVersion + ".json")))
            {
                InstallStatusText.Text = $"检测到 {_selectedVersion} 的安装未完成，将补全缺失文件";
            }

            var task = DownloadManager.Instance.Enqueue(
                $"Minecraft {_selectedVersion}", items,
                workers: App.Settings.Data.DownloadWorkers,
                category: "version",
                showWhenEmpty: true,
                force: force);
            InstallStatusText.Text = $"已加入下载队列 (共 {items.Count} 个文件)";

            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToDownloadCenter(versionOnly: true);

            _ = Task.Run(async () =>
            {
                if (task.Tcs != null)
                    await task.Tcs.Task;
                Dispatcher.Invoke(() =>
                {
                    if (task.Status == DownloadTaskStatus.Completed)
                    {
                        InstallStatusText.Text = $"{_selectedVersion} 安装完成!";
                        var instanceName = InstanceNameBox.Text;
                        if (!string.IsNullOrWhiteSpace(instanceName))
                        {
                            var createdName = CreateInstance(
                                _selectedVersion,
                                instanceName,
                                selectedLoaders: new Dictionary<string, LoaderPickerPage.LoaderSelection>());
                            InstallStatusText.Text = $"实例 \"{createdName}\" 已创建";
                        }
                    }
                    else
                    {
                        InstallStatusText.Text = $"安装失败: {task.Error}";
                    }
                    InstallBtn.IsEnabled = true;
                });
            });
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = $"安装失败: {ex.Message}";
            InstallBtn.IsEnabled = true;
        }
    }

    private void LoaderSelect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedVersion))
        {
            QuartzLauncher.Services.AnimatedMessageBox.Show("请先选择一个 Minecraft 版本", "提示");
            return;
        }

        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            var loaderPage = new LoaderPickerPage(_selectedVersion, _loader);
            mainWindow.NavigateTo(loaderPage);
        }
    }

    private void UpdateInstanceName()
    {
        if (string.IsNullOrEmpty(_selectedVersion)) return;
        var selected = LoaderPickerPage.SelectedLoaders;
        var primaryType = new[] { "fabric", "forge", "neoforge", "quilt", "optifine", "liteloader" }
            .FirstOrDefault(selected.ContainsKey);
        if (primaryType == null)
        {
            InstanceNameBox.Text = EnsureUniqueInstanceName($"[{_selectedVersion}][原版]");
            return;
        }

        var loaderName = primaryType switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            "optifine" => "OptiFine",
            "liteloader" => "LiteLoader",
            _ => primaryType
        };
        var rawVersion = selected[primaryType].Version;
        var loaderVersion = FormatLoaderVersion(primaryType, rawVersion, _selectedVersion);
        InstanceNameBox.Text = EnsureUniqueInstanceName($"[{_selectedVersion}][{loaderName}][{loaderVersion}]");
    }

    private static string FormatLoaderVersion(string loaderType, string version, string minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(version)) return "自动";
        var result = version.Trim();
        if (loaderType is "forge" or "liteloader"
            && result.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase))
            result = result[(minecraftVersion.Length + 1)..];
        if (loaderType == "optifine")
        {
            result = Path.GetFileNameWithoutExtension(result);
            if (result.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase))
                result = result["OptiFine_".Length..];
            if (result.StartsWith(minecraftVersion + "_", StringComparison.OrdinalIgnoreCase))
                result = result[(minecraftVersion.Length + 1)..];
        }
        return result;
    }

    private static bool IsAprilFoolsVersion(string id)
    {
        return id.Contains("April", StringComparison.OrdinalIgnoreCase)
               || id.Contains("23w13") || id.Contains("22w13") || id.Contains("20w14")
               || id.Contains("15w14") || id.Contains("3D Shareware", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsVersionComplete(string versionId)
    {
        var directory = Path.Combine(App.Paths.VersionsDir, versionId);
        if (!File.Exists(Path.Combine(directory, versionId + ".json"))) return false;

        string baseId;
        try
        {
            baseId = _mc.BaseVersionId(versionId);
        }
        catch
        {
            baseId = versionId;
        }
        if (File.Exists(Path.Combine(App.Paths.VersionsDir, baseId, baseId + ".jar"))) return true;
        return File.Exists(Path.Combine(directory, versionId + ".jar"));
    }

    private static HashSet<string> GetLocalVersionIds()
    {
        if (!Directory.Exists(App.Paths.VersionsDir))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var installed = Directory.EnumerateDirectories(App.Paths.VersionsDir)
            .Where(directory => File.Exists(Path.Combine(directory, ".quartz-installed")))
            .Select(directory => Path.GetFileName(directory)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in new InstanceStore(App.Paths.InstancesDir).List())
        {
            if (!string.IsNullOrEmpty(instance.McVersion)) installed.Add(instance.McVersion);
        }
        return installed;
    }

    private static List<Dictionary<string, object>> MergeLocalVersions(
        List<Dictionary<string, object>> onlineVersions, HashSet<string> localVersions)
    {
        var result = onlineVersions.ToList();
        var known = result
            .Select(version => version.TryGetValue("id", out var value) ? value?.ToString() ?? "" : "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in localVersions.Where(id => !known.Contains(id)))
        {
            var directory = Path.Combine(App.Paths.VersionsDir, id);
            var jarPath = Path.Combine(directory, $"{id}.jar");
            result.Add(new Dictionary<string, object>
            {
                ["id"] = id,
                ["type"] = "release",
                ["releaseTime"] = (File.Exists(jarPath) ? File.GetLastWriteTime(jarPath) : Directory.GetLastWriteTime(directory)).ToString("yyyy-MM-dd")
            });
        }

        return result;
    }

    private static BitmapSource LoadVersionIcon(string fileName)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        var selected = LoaderPickerPage.SelectedLoaders;
        if (string.IsNullOrEmpty(_selectedVersion))
            _selectedVersion = selected.Values.Select(value => value.MinecraftVersion)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
        if (string.IsNullOrEmpty(_selectedVersion))
        {
            LoaderInfoText.Text = "请重新选择 Minecraft 版本";
            CreateInstanceBtn.IsEnabled = true;
            return;
        }

        CreateInstanceBtn.IsEnabled = false;
        LoaderInfoText.Text = "正在准备版本文件...";

        try
        {
            var installedVersionId = _selectedVersion;
            var selectedSnapshot = selected.ToDictionary(entry => entry.Key, entry => entry.Value);
            if (selectedSnapshot.ContainsKey("fabricapi") && !selectedSnapshot.ContainsKey("fabric"))
            {
                selectedSnapshot["fabric"] = new LoaderPickerPage.LoaderSelection
                {
                    Type = "fabric",
                    MinecraftVersion = _selectedVersion,
                    AutoInstall = true
                };
            }
            var instanceName = string.IsNullOrWhiteSpace(InstanceNameBox.Text)
                ? $"Instance-{_selectedVersion}"
                : InstanceNameBox.Text;
            var instanceId = CreateInstanceId(instanceName);
            var items = await _mc.PrepareInstallAsync(_selectedVersion);
            var hasLoaderWork = selectedSnapshot.Keys.Any(type =>
                type is "fabric" or "forge" or "neoforge" or "quilt" or "fabricapi" or "optifine" or "liteloader");
            var isolationForInstall = InstancePathService.GetDefaultIsolation(App.Settings.Data, hasLoaderWork);
            var task = DownloadManager.Instance.Enqueue(
                $"Minecraft {_selectedVersion}",
                items,
                workers: App.Settings.Data.DownloadWorkers,
                category: "version",
                postDownloadAction: hasLoaderWork
                    ? async () =>
                    {
                        installedVersionId = await InstallPrimaryLoaderAsync(_selectedVersion, selectedSnapshot);
                        var modsRoot = InstancePathService.GetVersionGameDirectory(
                            App.Paths, installedVersionId, isolationForInstall);
                        Directory.CreateDirectory(modsRoot);
                        var modsDir = Path.Combine(modsRoot, "mods");
                        if (selectedSnapshot.TryGetValue("fabricapi", out var fabricApi) && fabricApi.AutoInstall)
                            await _loader.DownloadFabricApiAsync(_selectedVersion, modsDir);
                        if (selectedSnapshot.TryGetValue("optifine", out var optifine)
                            && optifine.AutoInstall && !string.IsNullOrEmpty(optifine.Version))
                            await _loader.DownloadOptifineAsync(optifine.Version, modsDir);
                        if (selectedSnapshot.ContainsKey("liteloader"))
                            throw new IOException("LiteLoader 自动安装尚不可用");
                        CreateInstance(_selectedVersion, instanceName, installedVersionId, instanceId,
                            selectedSnapshot, isolationForInstall);
                    }
                    : null);
            LoaderInfoText.Text = $"已加入下载队列 (共 {items.Count} 个文件)";

            if (task.Tcs != null)
                await task.Tcs.Task;

            if (task.Status != DownloadTaskStatus.Completed)
            {
                LoaderInfoText.Text = $"下载失败: {task.Error}";
                CreateInstanceBtn.IsEnabled = true;
                return;
            }

            if (!hasLoaderWork)
                CreateInstance(_selectedVersion, instanceName, installedVersionId, instanceId,
                    selectedSnapshot, isolationForInstall);

            LoaderInfoText.Text = "实例创建完成!";
        }
        catch (Exception ex)
        {
            LoaderInfoText.Text = $"安装失败: {ex.Message}";
        }

        CreateInstanceBtn.IsEnabled = true;
    }

    private void VanillaBar_Click(object sender, MouseButtonEventArgs e)
    {
        VanillaBar.Visibility = Visibility.Collapsed;
        VanillaCard.Visibility = Visibility.Visible;
    }

    private void VanillaCollapse_Click(object sender, RoutedEventArgs e)
    {
        VanillaCard.Visibility = Visibility.Collapsed;
        VanillaBar.Visibility = Visibility.Visible;
    }

    private string CreateInstance(
        string versionId,
        string name,
        string? installedVersionId = null,
        string? instanceId = null,
        IReadOnlyDictionary<string, LoaderPickerPage.LoaderSelection>? selectedLoaders = null,
        bool? isolationOverride = null)
    {
        if (string.IsNullOrWhiteSpace(name)) name = $"Instance-{versionId}";
        name = EnsureUniqueInstanceName(name);
        var id = instanceId ?? CreateInstanceId(name);

        var selected = selectedLoaders ?? LoaderPickerPage.SelectedLoaders;
        var primaryLoader = new[] { "fabric", "forge", "neoforge", "quilt", "optifine", "liteloader" }
            .FirstOrDefault(selected.ContainsKey) ?? "vanilla";
        var loaderVersion = primaryLoader != "vanilla" && selected.TryGetValue(primaryLoader, out var loader)
            ? loader.Version
            : "";
        if (string.IsNullOrEmpty(loaderVersion))
        {
            loaderVersion = primaryLoader switch
            {
                "fabric" => App.Settings.Data.LastFabricLoader,
                "forge" => App.Settings.Data.LastForgeLoader,
                _ => ""
            };
        }

        var inst = new Instance
        {
            Id = id,
            Name = name,
            VersionId = installedVersionId ?? versionId,
            McVersion = versionId,
            Loader = primaryLoader,
            LoaderVersion = loaderVersion,
            UsesVersionDirectory = true
        };
        inst.VersionIsolation = isolationOverride ?? InstancePathService.GetDefaultIsolation(App.Settings.Data, inst);

        var store = new InstanceStore(App.Paths.InstancesDir);
        store.Create(inst);
        InstancePathService.EnsureGameDirectory(App.Paths, App.Settings.Data, inst);
        var markerDir = Path.Combine(App.Paths.VersionsDir, versionId);
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, ".quartz-installed"), DateTimeOffset.UtcNow.ToString("O"));
        return name;
    }

    private static string EnsureUniqueInstanceName(string baseName)
    {
        var existingNames = new InstanceStore(App.Paths.InstancesDir).List()
            .Select(instance => instance.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName)) return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = baseName + suffix;
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

    private async Task<string> InstallPrimaryLoaderAsync(
        string minecraftVersion,
        IReadOnlyDictionary<string, LoaderPickerPage.LoaderSelection> selected)
    {
        if (selected.TryGetValue("fabric", out var fabric))
        {
            var loaderVersion = !string.IsNullOrEmpty(fabric.Version)
                ? fabric.Version
                : App.Settings.Data.LastFabricLoader;
            if (string.IsNullOrEmpty(loaderVersion))
                loaderVersion = (await _loader.FabricVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("没有可用的 Fabric Loader 版本");
            var installedVersion = await _loader.InstallFabricAsync(minecraftVersion, loaderVersion);
            App.Settings.Data.LastFabricLoader = loaderVersion;
            App.Settings.Save();
            return installedVersion;
        }

        if (selected.TryGetValue("forge", out var forge))
        {
            var loaderVersion = !string.IsNullOrEmpty(forge.Version)
                ? forge.Version
                : App.Settings.Data.LastForgeLoader;
            if (string.IsNullOrEmpty(loaderVersion))
                loaderVersion = (await _loader.ForgeVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("没有可用的 Forge 版本");
            var installedVersion = await _loader.InstallForgeAsync(loaderVersion);
            App.Settings.Data.LastForgeLoader = loaderVersion;
            App.Settings.Save();
            return installedVersion;
        }

        if (selected.TryGetValue("neoforge", out var neoForge))
        {
            var loaderVersion = neoForge.Version;
            if (string.IsNullOrEmpty(loaderVersion))
                loaderVersion = (await _loader.NeoForgeVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("没有可用的 NeoForge 版本");
            return await _loader.InstallNeoForgeAsync(loaderVersion);
        }

        if (selected.TryGetValue("quilt", out var quilt))
        {
            var loaderVersion = quilt.Version;
            if (string.IsNullOrEmpty(loaderVersion))
                loaderVersion = (await _loader.QuiltVersionsAsync(minecraftVersion)).FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("没有可用的 Quilt Loader 版本");
            return await _loader.InstallQuiltAsync(minecraftVersion, loaderVersion);
        }

        return minecraftVersion;
    }
}
