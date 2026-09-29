using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class SettingsPage : Page
{
    private bool _javaScanRunning;
    private bool _loadingJavaList;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Data;
        JavaPathBox.Text = s.JavaPath;
        AutoSelectJavaCheck.IsChecked = s.AutoSelectJava;
        AutoMemoryCheck.IsChecked = s.AutoMemory;
        MemorySlider.Value = s.MemoryMb;
        MemorySlider.IsEnabled = !s.AutoMemory;
        MemoryText.Text = $"{s.MemoryMb} MB";
        MemoryOptimizeSwitch.IsChecked = s.MemoryOptimize;
        var totalMb = MemoryAdvisor.GetTotalPhysicalMemoryMb();
        MemoryInfoText.Text = $"物理内存: {totalMb} MB";
        WorkersSlider.Value = s.DownloadWorkers;
        WorkersText.Text = s.DownloadWorkers.ToString();
        AutoInstallLoaderCheck.IsChecked = s.AutoInstallLoader;
        UpdateModeButton.Content = s.AutoUpdate ? "手动更新" : "自动获取更新";
        UpdateManifestUrlBox.Text = string.IsNullOrWhiteSpace(s.UpdateManifestUrl)
            ? Settings.DefaultUpdateManifestUrl
            : s.UpdateManifestUrl;
        RunAsUpdateServerCheck.IsChecked = s.RunAsUpdateServer;

        for (var i = 0; i < VersionIsolationCombo.Items.Count; i++)
        {
            if (VersionIsolationCombo.Items[i] is ComboBoxItem item
                && item.Tag?.ToString() == s.VersionIsolationMode)
            {
                VersionIsolationCombo.SelectedIndex = i;
                break;
            }
        }
        if (VersionIsolationCombo.SelectedIndex < 0)
            VersionIsolationCombo.SelectedIndex = 2;

        for (int i = 0; i < SourceCombo.Items.Count; i++)
        {
            if (SourceCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == s.DownloadSource)
            {
                SourceCombo.SelectedIndex = i;
                break;
            }
        }

        var cachedJavas = s.DetectedJavas
            .Where(java => !string.IsNullOrWhiteSpace(java.Path) && File.Exists(java.Path))
            .ToList();
        if (cachedJavas.Count > 0)
        {
            RenderJavaList(cachedJavas);
            JavaInfoText.Text = $"已恢复上次检测到的 {cachedJavas.Count} 个 Java (点击检测可刷新)";
        }
        else
        {
            await RefreshJavaListAsync();
        }
        if (s.AutoUpdate)
            await CheckForUpdateAsync(true);
    }

    private void ShowAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        var announcement = UpdateService.LoadAnnouncement();
        var notes = string.IsNullOrWhiteSpace(announcement.Notes)
            ? "本次更新暂无详细说明。"
            : announcement.Notes.Trim();
        AnimatedMessageBox.Show(
            $"星落 LaunCher {announcement.Version}\n\n{notes}",
            "更新公告",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task RefreshJavaListAsync()
    {
        JavaInfoText.Text = "正在检测 Java...";
        JavaSelector.Items.Clear();
        var javas = await Task.Run(() => JavaService.DetectAll());
        if (javas.Count == 0)
        {
            var localVersion = Directory.Exists(App.Paths.VersionsDir)
                ? Directory.EnumerateDirectories(App.Paths.VersionsDir)
                    .Where(directory => File.Exists(Path.Combine(directory, $"{Path.GetFileName(directory)}.jar")))
                    .OrderByDescending(directory => File.GetLastWriteTime(directory))
                    .Select(Path.GetFileName)
                    .FirstOrDefault()
                : null;
            var targetVersion = string.IsNullOrEmpty(localVersion) ? "1.21.1" : localVersion;
            var major = JavaService.RequiredMajorVersion(targetVersion);
            JavaInfoText.Text = $"未检测到适用于 Minecraft {targetVersion} 的 Java";
            var result = AnimatedMessageBox.Show(
                $"未检测到 Java。是否下载 Java {major} (Temurin JRE)？",
                "下载 Java",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            try
            {
            var option = await JavaService.GetDownloadOptionAsync(major);
            if (option == null)
            {
                if (Window.GetWindow(this) is MainWindow resourceWindow)
                    resourceWindow.NavigateToJavaDownload();
                return;
            }
            var download = JavaService.CreateDownloadItem(option);
            JavaInfo? installedJava = null;
            var task = DownloadManager.Instance.Enqueue(
                    $"Java {major} · Temurin {option.Version}",
                    [download],
                    workers: App.Settings.Data.DownloadWorkers,
                    category: "java",
                    postDownloadAction: async () =>
                    {
                        installedJava = await JavaService.InstallDownloadedAsync(option);
                    });
                if (Window.GetWindow(this) is MainWindow mainWindow)
                    mainWindow.NavigateToDownloadCenter(versionOnly: true);
                if (task.Tcs != null) await task.Tcs.Task;
                if (task.Status != DownloadTaskStatus.Completed)
                    throw new IOException($"Java 下载失败: {task.Error}");
                var installed = installedJava ?? throw new IOException($"Java {major} 安装未完成");
                if (!App.Settings.Data.AutoSelectJava)
                    App.Settings.Data.JavaPath = installed.Path;
                App.Settings.Data.DetectedJavas = App.Settings.Data.DetectedJavas
                    .Where(java => !java.Path.Equals(installed.Path, StringComparison.OrdinalIgnoreCase))
                    .Append(installed)
                    .ToList();
                App.Settings.Save();
                javas = await Task.Run(() => JavaService.DetectAll());
            }
            catch (Exception ex)
            {
                JavaInfoText.Text = ex.Message;
                return;
            }
        }
        RenderJavaList(javas);
        JavaInfoText.Text = $"检测到 {javas.Count} 个 Java 安装";
        if (javas.Count > 0 && string.IsNullOrEmpty(JavaPathBox.Text)
            && !App.Settings.Data.AutoSelectJava)
            JavaPathBox.Text = javas[0].Path;
    }

    private void RenderJavaList(IEnumerable<JavaInfo> javas)
    {
        var entries = javas.ToList();
        _loadingJavaList = true;
        try
        {
            JavaSelector.Items.Clear();
            foreach (var j in entries)
            {
                var item = new ComboBoxItem
                {
                    Content = $"Java {j.MajorVersion} ({j.Version})  {j.Path}",
                    Tag = j,
                    ToolTip = j.Path
                };
                JavaSelector.Items.Add(item);
            }

            var selectedPath = JavaPathBox.Text.Trim();
            var selectedIndex = entries.FindIndex(java =>
                java.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            JavaSelector.SelectedIndex = selectedIndex >= 0
                ? selectedIndex
                : App.Settings.Data.AutoSelectJava ? -1 : entries.Count > 0 ? 0 : -1;
        }
        finally
        {
            _loadingJavaList = false;
        }
        App.Settings.Data.DetectedJavas = entries;
    }

    private void ShowSelectedJavaPath_Click(object sender, RoutedEventArgs e)
    {
        var path = JavaPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            AnimatedMessageBox.Show("请先选择一个 Java。", "Java 路径");
            return;
        }
        AnimatedMessageBox.ShowCopyable(path, "Java 路径");
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择全局 Java",
            Filter = "Java (*.exe)|java.exe|所有文件 (*.*)|*.*",
            FileName = "java.exe"
        };
        if (dialog.ShowDialog() != true) return;

        var java = JavaService.ProbeJava(dialog.FileName);
        if (java == null)
        {
            AnimatedMessageBox.Show("选择的文件不是可用的 64 位 Java。", "Java 路径",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        JavaPathBox.Text = java.Path;
        MergeJavaResults([java]);
        JavaInfoText.Text = $"已选择 Java {java.MajorVersion}。点击底部“保存设置”后生效。";
    }

    private async void ScanJavaFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Java 安装目录"
        };
        if (dialog.ShowDialog() != true) return;

        JavaInfoText.Text = "正在扫描所选目录...";
        var found = await Task.Run(() => JavaService.DetectInDirectory(dialog.FolderName));
        if (found.Count == 0)
        {
            JavaInfoText.Text = "所选目录中没有找到可用的 64 位 Java";
            return;
        }

        var mergedCount = MergeJavaResults(found);
        JavaInfoText.Text = $"检测到 {mergedCount} 个 Java 安装";
    }

    private async void ScanAllJava_Click(object sender, RoutedEventArgs e)
    {
        if (_javaScanRunning) return;

        var firstConfirmation = AnimatedMessageBox.Show(
            "此功能可能损害磁盘寿命",
            "全盘扫描警告",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (firstConfirmation != MessageBoxResult.Yes) return;

        var secondConfirmation = AnimatedMessageBox.Show(
            "将扫描所有可用磁盘中的目录和文件，扫描时间可能较长。确定继续吗？",
            "再次确认全盘扫描",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (secondConfirmation != MessageBoxResult.Yes) return;

        _javaScanRunning = true;
        FullScanJavaButton.IsEnabled = false;
        var progressDialog = new SmoothProgressDialog("全盘扫描 Java");
        if (Window.GetWindow(this) is Window owner)
            progressDialog.Owner = owner;
        progressDialog.Show();

        try
        {
            var progress = new Progress<JavaScanProgress>(progressDialog.Update);
            var found = await JavaService.ScanAllDrivesAsync(progress, progressDialog.CancellationToken);
            var mergedCount = MergeJavaResults(found);
            JavaInfoText.Text = $"检测到 {mergedCount} 个 Java 安装";
            progressDialog.Complete($"扫描完成，共发现 {found.Count} 个可用 Java", found.Count);
        }
        catch (OperationCanceledException)
        {
            progressDialog.Complete("扫描已取消", 0);
        }
        catch (Exception ex)
        {
            progressDialog.Complete("扫描失败", 0);
            AnimatedMessageBox.Show(ex.Message, "全盘扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _javaScanRunning = false;
            FullScanJavaButton.IsEnabled = true;
        }
    }

    private int MergeJavaResults(IEnumerable<JavaInfo> found)
    {
        var existing = JavaSelector.Items.OfType<ComboBoxItem>()
            .Select(item => item.Tag switch
            {
                JavaInfo java => java,
                string path => JavaService.ProbeJava(path),
                _ => null
            })
            .Where(java => java != null)
            .Cast<JavaInfo>();
        var merged = found.Concat(existing)
            .GroupBy(java => java.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(java => Math.Abs(java.MajorVersion - 21))
            .ThenByDescending(java => java.MajorVersion)
            .ThenBy(java => java.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RenderJavaList(merged);
        return merged.Count;
    }

    private void JavaSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingJavaList) return;
        if (JavaSelector?.SelectedItem is ComboBoxItem { Tag: JavaInfo java })
        {
            JavaPathBox.Text = java.Path;
            App.Settings.Data.JavaPath = java.Path;
        }
    }

    private void AutoSelectJava_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoSelectJavaCheck == null) return;
        App.Settings.Data.AutoSelectJava = AutoSelectJavaCheck.IsChecked == true;
        App.Settings.Save();
        JavaInfoText.Text = App.Settings.Data.AutoSelectJava
            ? "已开启按 Minecraft 版本自动选择 Java。"
            : "已关闭自动选择，将使用当前选择的 Java。";
    }

    private void RefreshJavaList() => _ = RefreshJavaListAsync();

    private void DetectJava_Click(object sender, RoutedEventArgs e) => RefreshJavaList();

    private void Memory_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemoryText != null)
            MemoryText.Text = $"{(int)e.NewValue} MB";
    }

    private void AutoMemory_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoMemoryCheck == null) return;
        App.Settings.Data.AutoMemory = AutoMemoryCheck.IsChecked == true;
        MemorySlider.IsEnabled = !App.Settings.Data.AutoMemory;
        App.Settings.Save();
    }

    private void MemoryOptimize_Changed(object sender, RoutedEventArgs e)
    {
        App.Settings.Data.MemoryOptimize = MemoryOptimizeSwitch.IsChecked == true;
        App.Settings.Save();
    }

    private void Recommend_Click(object sender, RoutedEventArgs e)
    {
        var totalMb = MemoryAdvisor.GetTotalPhysicalMemoryMb();
        var recommended = MemoryAdvisor.GetRecommendedMemory();
        var info = $"物理内存: {totalMb} MB\n推荐分配: {recommended} MB\n最大可用: {(int)(totalMb * 0.95)} MB";
        AnimatedMessageBox.Show(info, "内存推荐");
    }

    private void Workers_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WorkersText != null)
            WorkersText.Text = ((int)e.NewValue).ToString();
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo?.SelectedItem is ComboBoxItem item)
        {
            var source = item.Tag?.ToString() ?? "mojang";
            DownloadService.SetSourceMode(source);
        }
    }

    private void VersionIsolation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VersionIsolationInfoText == null
            || VersionIsolationCombo?.SelectedItem is not ComboBoxItem item) return;
        VersionIsolationInfoText.Text = item.Tag?.ToString() switch
        {
            VersionIsolationModes.None => "之后安装的版本默认共用 .minecraft 下的游戏内容。",
            VersionIsolationModes.Modded => "之后安装的 Mod 版本默认使用 versions/<版本> 独立目录。",
            _ => "之后安装的所有版本默认使用 versions/<版本> 独立目录。"
        };
    }

    private void UpdateMode_Click(object sender, RoutedEventArgs e)
    {
        var enabling = !App.Settings.Data.AutoUpdate;
        if (!ValidateUpdateUrl(showMessage: true, allowEmpty: !enabling)) return;
        App.Settings.Data.AutoUpdate = !App.Settings.Data.AutoUpdate;
        App.Settings.Data.UpdateManifestUrl = UpdateManifestUrlBox.Text.Trim();
        App.Settings.Data.RunAsUpdateServer = RunAsUpdateServerCheck.IsChecked == true;
        App.Settings.Save();
        UpdateModeButton.Content = App.Settings.Data.AutoUpdate ? "手动更新" : "自动获取更新";
        UpdateStatusText.Text = App.Settings.Data.AutoUpdate
            ? "自动模式已开启，将在设置页打开时检查更新。"
            : "手动模式已开启，请点击“获取更新”检查新版本。";
    }

    private bool _checkRunning;
    private Task? _checkTask;

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_checkTask is { IsCompleted: false })
        {
            UpdateStatusText.Text = "正在检查更新，请稍候...";
            return;
        }
        _checkTask = CheckForUpdateAsync(false);
    }

    private async Task CheckForUpdateAsync(bool automatic)
    {
        if (!ValidateUpdateUrl(showMessage: !automatic))
        {
            UpdateStatusText.Text = "未配置有效的更新清单地址。";
            return;
        }

        if (_checkRunning)
        {
            UpdateStatusText.Text = automatic ? "正在检查更新，请稍候..." : "正在检查更新，请稍候...";
            return;
        }

        _checkRunning = true;
        if (!automatic)
            CheckUpdateButton.IsEnabled = false;
        try
        {
            var manifestUrl = UpdateManifestUrlBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(manifestUrl)
                && !string.Equals(App.Settings.Data.UpdateManifestUrl, manifestUrl, StringComparison.Ordinal))
            {
                App.Settings.Data.UpdateManifestUrl = manifestUrl;
                App.Settings.Save();
            }
            if (UpdateService.IsLoopbackManifestUrl(manifestUrl))
            {
                UpdateStatusText.Text = "正在确保更新服务器运行...";
                await Task.Run(() => UpdateService.EnsureLocalServerRunning());
                if (!UpdateService.IsLocalServerRunning())
                {
                    UpdateStatusText.Text = "更新服务器未启动，无法检查更新。";
                    if (!automatic)
                        AnimatedMessageBox.Show("更新服务器未启动，无法检查更新。", "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            UpdateStatusText.Text = "正在检查更新...";
            var manifest = await UpdateService.CheckAsync(manifestUrl);
            if (manifest == null)
            {
                UpdateStatusText.Text = $"当前已是最新版本 ({UpdateService.CurrentVersion})。";
                return;
            }

            var message = $"发现新版本 {manifest.Version}。\n\n{manifest.Notes}\n\n是否现在下载并更新？";
            var result = AnimatedMessageBox.Show(message, "发现更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes)
            {
                UpdateStatusText.Text = "已跳过本次更新。";
                return;
            }

            UpdateStatusText.Text = "正在下载更新补丁...";
            var package = await UpdateService.DownloadPackageAsync(manifest, App.Paths.TempDir,
                new Progress<double>(value => UpdateStatusText.Text = $"正在下载更新补丁... {value:0}%"));
            UpdateStatusText.Text = "下载完成，启动更新器...";
            UpdateService.StartApplyAndExit(package, manifest);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = automatic ? $"自动检查更新失败: {ex.Message}" : $"检查更新失败: {ex.Message}";
            if (!automatic)
                AnimatedMessageBox.Show(ex.Message, "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _checkRunning = false;
            if (!automatic)
                CheckUpdateButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Data;
        if (!ValidateUpdateUrl(showMessage: true, allowEmpty: !s.AutoUpdate)) return;
        s.JavaPath = JavaPathBox.Text;
        s.AutoSelectJava = AutoSelectJavaCheck.IsChecked == true;
        s.MemoryMb = (int)MemorySlider.Value;
        s.DownloadWorkers = (int)WorkersSlider.Value;
        s.AutoInstallLoader = AutoInstallLoaderCheck.IsChecked == true;
        s.UpdateManifestUrl = UpdateManifestUrlBox.Text.Trim();
        s.RunAsUpdateServer = RunAsUpdateServerCheck.IsChecked == true;
        if (VersionIsolationCombo.SelectedItem is ComboBoxItem isolationItem)
            s.VersionIsolationMode = isolationItem.Tag?.ToString() ?? VersionIsolationModes.All;
        if (SourceCombo.SelectedItem is ComboBoxItem item)
            s.DownloadSource = item.Tag?.ToString() ?? "mojang";

        App.Settings.Save();
        DownloadService.SetSourceMode(s.DownloadSource);
        QuartzLauncher.Services.AnimatedMessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool ValidateUpdateUrl(bool showMessage, bool allowEmpty = false)
    {
        var value = UpdateManifestUrlBox.Text.Trim();
        string? message = null;
        if (string.IsNullOrEmpty(value))
        {
            if (allowEmpty)
                return true;
            if (App.Settings.Data.AutoUpdate)
                message = "自动更新已开启，请填写更新清单地址。";
            else
                message = "请先填写更新清单地址，再手动检查更新。";
        }
        else if (!UpdateService.TryGetHttpUri(value, out _))
        {
            message = "更新清单地址必须是完整的 HTTP 或 HTTPS URL。";
        }

        if (message == null) return true;
        UpdateStatusText.Text = message;
        if (showMessage)
            AnimatedMessageBox.Show(message, "更新地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
