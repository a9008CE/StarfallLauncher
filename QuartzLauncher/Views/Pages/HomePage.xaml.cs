using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class HomePage : Page
{
    private readonly VersionsPage _versionsPage;
    private Process? _gameProcess;
    private DateTime _gameStartedAt;
    private bool _userTerminated;
    private bool _launchInProgress;
    private LanWorldWatcher? _lanWatcher;
    private RelayClient? _relayClient;
    private readonly HashSet<int> _promptedLanPorts = new();
    private DispatcherTimer? _logTimer;
    private readonly LogAnalyzer _logAnalyzer = new();
    private readonly LoginService _loginService = new(App.Settings);
    private DispatcherTimer? _panelSwitchTimer;
    private Panel? _activeLoginPanel;
    private int _panelAnimationGeneration;
    private int _homePanelAnimationGeneration;
    private bool _passwordVisible;
    private CancellationTokenSource? _microsoftLoginCts;

    public HomePage(VersionsPage versionsPage)
    {
        InitializeComponent();
        _versionsPage = versionsPage;
        Loaded += OnLoaded;
    }

    public Instance? SelectedInstance =>
        (InstanceSelector.SelectedItem as ComboBoxItem)?.Tag as Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 只初始化一次：避免从下载管理页返回首页时重置启动进度条
        Loaded -= OnLoaded;
        RefreshAnnouncement();
        SetLogPanelExpanded(false, animate: false);
        RefreshProfile();
        RefreshInstances();
        if (!_launchInProgress && _gameProcess is not { HasExited: false })
            SetLaunchState(LaunchUiState.Idle);
    }

    private void RefreshAnnouncement()
    {
        var announcement = UpdateService.LoadAnnouncement();
        AnnouncementVersionText.Text = $"QuartzLauncher {announcement.Version}";
        AnnouncementTitleText.Text = $"{announcement.Version} 更新内容";
        AnnouncementNotesText.Text = string.IsNullOrWhiteSpace(announcement.Notes)
            ? "本次更新暂无文字说明。"
            : NormalizeAnnouncementNotes(announcement.Notes);
    }

    private static string NormalizeAnnouncementNotes(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.Equals("更新内容：", StringComparison.Ordinal)
                           && !line.Equals("更新内容:", StringComparison.Ordinal))
            .Select(line => line.StartsWith("•", StringComparison.Ordinal)
                ? line
                : line.StartsWith("-", StringComparison.Ordinal)
                    ? $"• {line[1..].Trim()}"
                    : $"• {line}");
        return string.Join(Environment.NewLine, lines);
    }

    private void RefreshProfile()
    {
        var settings = App.Settings.Data;
        var offlineName = string.IsNullOrWhiteSpace(settings.PlayerName)
            ? "Steve"
            : settings.PlayerName.Trim();

        AvatarBorder.Background = (Brush)FindResource("PrimaryBrush");
        AvatarText.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(settings.AuthAvatarPath) && File.Exists(settings.AuthAvatarPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(settings.AuthAvatarPath);
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                AvatarBorder.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                AvatarText.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Keep the initial avatar when the custom image cannot be loaded.
            }
        }

        if (settings.AuthMode == AuthModes.Microsoft && !string.IsNullOrWhiteSpace(settings.AuthPlayerName))
        {
            SetProfile(settings.AuthPlayerName, "正版", "Microsoft Account");
        }
        else if (settings.AuthMode == AuthModes.External && !string.IsNullOrWhiteSpace(settings.AuthPlayerName))
        {
            SetProfile(settings.AuthPlayerName, "外置", settings.AuthAccount);
        }
        else
        {
            SetProfile(offlineName, "离线", "离线登录");
        }
    }

    private void SetProfile(string name, string badge, string info)
    {
        ProfileName.Text = name;
        ProfileBadge.Text = badge;
        ProfileInfo.Text = info;
        AvatarText.Text = GetInitial(name);
    }

    private static string GetInitial(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();

    /// <summary>重新读取实例列表（保留当前选中项）。回到首页时调用，保证新增/导入的版本立即可选。</summary>
    public void RefreshInstances()
    {
        var selectedId = (InstanceSelector.SelectedItem as ComboBoxItem)?.Tag is Instance selected
            ? selected.Id
            : null;

        InstanceSelector.Items.Clear();
        var store = new InstanceStore(App.Paths.InstancesDir);
        var instances = store.List();
        foreach (var instance in instances)
            InstanceSelector.Items.Add(CreateInstanceComboItem(instance));

        var representedVersions = instances
            .SelectMany(instance => new[] { instance.VersionId, instance.McVersion })
            .Where(version => !string.IsNullOrEmpty(version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(App.Paths.VersionsDir))
        {
            foreach (var directory in Directory.EnumerateDirectories(App.Paths.VersionsDir))
            {
                var versionId = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(versionId) || representedVersions.Contains(versionId)) continue;
                if (!File.Exists(Path.Combine(directory, $"{versionId}.json"))
                    || !File.Exists(Path.Combine(directory, $"{versionId}.jar"))) continue;

                var localInstance = new Instance
                {
                    Id = $"local-{versionId}",
                    Name = versionId,
                    VersionId = versionId,
                    McVersion = versionId,
                    Loader = "vanilla",
                    VersionIsolation = InstancePathService.GetDefaultIsolation(App.Settings.Data, false),
                    UsesVersionDirectory = true
                };
                InstanceSelector.Items.Add(CreateInstanceComboItem(localInstance));
            }
        }

        if (InstanceSelector.Items.Count > 0)
        {
            var previous = InstanceSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is Instance instance && instance.Id == selectedId);
            InstanceSelector.SelectedItem = previous ?? InstanceSelector.Items[0];
        }

        LaunchBtn.IsEnabled = InstanceSelector.SelectedItem is ComboBoxItem;
        UpdateInstanceInfo();
    }

    private void Instance_Changed(object sender, SelectionChangedEventArgs e)
    {
        LaunchBtn.IsEnabled = InstanceSelector.SelectedItem is ComboBoxItem;
        UpdateInstanceInfo();
    }

    private static ComboBoxItem CreateInstanceComboItem(Instance instance)
    {
        // 名称缺失或只是游戏文件夹名（如 .minecraft、原版）时改为显示版本号，
        // 否则多个版本的实例在首页会看不出区别
        var versionLabel = string.IsNullOrWhiteSpace(instance.VersionId) ? instance.McVersion : instance.VersionId;
        var name = instance.Name;
        var generic = string.IsNullOrWhiteSpace(name)
                      || name.StartsWith(".", StringComparison.Ordinal)
                      || name.Equals("原版", StringComparison.Ordinal)
                      || name.Equals("minecraft", StringComparison.OrdinalIgnoreCase);
        var displayName = generic && !string.IsNullOrWhiteSpace(versionLabel) ? versionLabel : name;
        return new ComboBoxItem
        {
            Content = displayName,
            Tag = instance,
            ToolTip = instance.Label
        };
    }

    private void UpdateInstanceInfo()
    {
        if (InstanceSelector.SelectedItem is not ComboBoxItem item || item.Tag is not Instance instance)
        {
            InstanceInfo.Text = "";
            return;
        }

        var parts = new List<string> { $"版本: {instance.McVersion}" };
        if (!string.IsNullOrWhiteSpace(instance.Loader)
            && !instance.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        {
            var loaderName = char.ToUpperInvariant(instance.Loader[0]) + instance.Loader[1..];
            parts.Add($"加载器: {loaderName}");
        }

        var modsDir = Path.Combine(
            InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance), "mods");
        if (Directory.Exists(modsDir))
        {
            var modCount = Directory.GetFiles(modsDir, "*.jar").Length;
            if (modCount > 0) parts.Add($"Mod: {modCount} 个");
        }

        InstanceInfo.Text = string.Join("  ·  ", parts);
    }

    private enum LaunchUiState { Idle, Launching, Running, Crashed }

    private void SetLaunchState(LaunchUiState state)
    {
        var primary = TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var danger = TryFindResource("DangerBrush") as Brush ?? Brushes.OrangeRed;
        var success = TryFindResource("SuccessBrush") as Brush ?? primary;
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;

        switch (state)
        {
            case LaunchUiState.Running:
                LaunchStatusText.Text = "游戏已启动";
                LaunchStatusText.Foreground = success;
                LaunchStatusPercent.Text = "100%";
                LaunchStatusDetail.Text = "游戏进程正在运行，祝您游玩愉快！";
                LaunchProgressBar.Foreground = primary;
                LaunchProgressBar.Value = 100;
                break;
            case LaunchUiState.Crashed:
                LaunchStatusText.Text = "游戏异常";
                LaunchStatusText.Foreground = danger;
                LaunchStatusPercent.Text = "";
                LaunchStatusDetail.Text = "游戏进程异常退出，可展开日志或点击「分析日志」排查原因。";
                LaunchProgressBar.Foreground = danger;
                LaunchProgressBar.Value = 100;
                break;
            case LaunchUiState.Launching:
                LaunchStatusText.Text = "正在启动游戏";
                LaunchStatusText.Foreground = text;
                LaunchStatusDetail.Text = "正在准备游戏文件，请稍候...";
                LaunchProgressBar.Foreground = primary;
                break;
            default:
                LaunchStatusText.Text = "请启动游戏";
                LaunchStatusText.Foreground = text;
                LaunchStatusPercent.Text = "";
                LaunchStatusDetail.Text = "选择版本后点击「启动游戏」";
                LaunchProgressBar.Foreground = primary;
                LaunchProgressBar.Value = 0;
                break;
        }
    }

    private void SetLaunchProgress(string step, double progress)
    {
        LaunchStatusText.Text = step;
        LaunchStatusText.Foreground = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        LaunchProgressBar.Foreground = TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        if (progress >= 0)
        {
            var pct = Math.Min(progress, 1.0);
            LaunchProgressBar.Value = Math.Round(pct * 100);
            LaunchStatusPercent.Text = $"{(int)Math.Round(pct * 100)}%";
        }
        else
        {
            LaunchStatusPercent.Text = "";
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceSelector.SelectedItem is not ComboBoxItem item || item.Tag is not Instance instance)
            return;

        SetLogPanelExpanded(true);
        LaunchBtn.Visibility = Visibility.Collapsed;
        ForceStopBtn.Visibility = Visibility.Visible;
        _userTerminated = false;
        _launchInProgress = true;
        void SetStep(string step, double progress = -1) => SetLaunchProgress(step, progress);
        SetLaunchState(LaunchUiState.Launching);
        try
        {
            var baseVersion = string.IsNullOrEmpty(instance.McVersion) ? instance.VersionId : instance.McVersion;
            var minecraft = new MinecraftService(App.Paths, App.Settings);

            SetStep("选择 Java 版本", 0.1);
            var requiredJavaMajor = minecraft.GetRequiredJavaMajor(instance);
            var javaInfo = await Task.Run(() => JavaService.SelectForMajor(
                requiredJavaMajor,
                instance.JavaPath,
                App.Settings.Data.JavaPath,
                App.Settings.Data.DetectedJavas,
                App.Settings.Data.AutoSelectJava));
            if (javaInfo == null)
            {
                var result = AnimatedMessageBox.Show(
                    $"Minecraft {baseVersion} 需要 Java {requiredJavaMajor}，当前没有可用的匹配版本。是否现在下载？",
                    "需要 Java", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    ForceStopBtn.Visibility = Visibility.Collapsed;
                    LaunchBtn.Visibility = Visibility.Visible;
                    LaunchBtn.IsEnabled = true;
                    SetStep("");
                    return;
                }

                var option = await JavaService.GetDownloadOptionAsync(requiredJavaMajor);
                if (option == null)
                {
                    if (Window.GetWindow(this) is MainWindow resourceWindow)
                        resourceWindow.NavigateToJavaDownload();
                    throw new IOException($"无法获取 Java {requiredJavaMajor} 的下载信息，请在资源中心 Java 下载页重试。");
                }

                var download = JavaService.CreateDownloadItem(option);
                JavaInfo? installedJava = null;
                SetStep("下载 Java", 0.15);
                var task = DownloadManager.Instance.Enqueue(
                    $"Java {requiredJavaMajor} · Temurin {option.Version}",
                    [download],
                    workers: App.Settings.Data.DownloadWorkers,
                    category: "java",
                    postDownloadAction: async () =>
                    {
                        installedJava = await JavaService.InstallDownloadedAsync(option);
                    });

                AppendLog($"[INFO] 正在下载 Java {requiredJavaMajor}...");
                if (Window.GetWindow(this) is MainWindow mainWindow)
                    mainWindow.NavigateToDownloadCenter(versionOnly: true);
                if (task.Tcs != null) await task.Tcs.Task;
                if (task.Status != DownloadTaskStatus.Completed)
                    throw new IOException($"Java 下载失败: {task.Error}");
                javaInfo = installedJava ?? throw new IOException($"Java {requiredJavaMajor} 安装未完成");
                AppendLog($"[INFO] Java 安装完成: {javaInfo.Path}");
            }

            AppendLog($"[INFO] 使用 Java {javaInfo.MajorVersion}: {javaInfo.Path}");
            if (App.Settings.Data.AuthMode == AuthModes.Microsoft)
            {
                SetStep("验证 Microsoft 登录", 0.2);
                await MicrosoftAuthService.EnsureSessionAsync();
            }

            if (App.Settings.Data.MemoryOptimize)
            {
                SetStep("内存优化", 0.25);
                var freed = await SystemMemoryOptimizer.OptimizeAsync();
                AppendLog(SystemMemoryOptimizer.IsAdministrator
                    ? $"[INFO] 内存优化完成，释放约 {freed} MB 物理内存"
                    : $"[INFO] 内存优化完成，释放约 {freed} MB（非管理员权限，已跳过系统备用内存清理）");
            }

            SetStep("检查游戏文件完整性", 0.3);
            AppendLog("[INFO] 正在检查游戏文件完整性...");
            var repairItems = await minecraft.PrepareLaunchAsync(instance);
            if (repairItems.Count > 0)
            {
                AppendLog($"[INFO] 发现 {repairItems.Count} 个文件需要补全");
                // 仅在校验出缺失文件时才跳转到下载管理页
                if (Window.GetWindow(this) is MainWindow mwRepair)
                    mwRepair.NavigateToDownloadCenter(versionOnly: true);

                var repairTask = DownloadManager.Instance.Enqueue(
                    $"修复 {baseVersion} 游戏文件",
                    repairItems,
                    workers: Math.Max(App.Settings.Data.DownloadWorkers, 16),
                    category: "version");

                // 进度条与下载任务同步刷新，不重置
                var repairProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                repairProgressTimer.Tick += (_, _) =>
                {
                    var totalFiles = Math.Max(1, repairTask.TotalFiles);
                    var doneFiles = Math.Min(repairTask.CompletedFiles, totalFiles);
                    SetStep($"补全游戏文件 ({doneFiles}/{totalFiles})",
                        0.4 + 0.2 * ((double)doneFiles / totalFiles));
                };
                repairProgressTimer.Start();
                try
                {
                    if (repairTask.Tcs != null) await repairTask.Tcs.Task;
                }
                finally
                {
                    repairProgressTimer.Stop();
                }

                if (repairTask.Status != DownloadTaskStatus.Completed)
                    throw new IOException($"游戏文件修复失败: {repairTask.Error}");

                AppendLog("[INFO] 文件补全完成，正在返回主页...");
                SetStep("文件补全完成", 0.62);
                // 补全完成后自动回到主页
                if (Window.GetWindow(this) is MainWindow mwHome)
                    mwHome.NavigateToHome();
            }
            else
            {
                AppendLog("[INFO] 资源完整，无需补全");
                SetStep("资源完整", 0.6);
            }

            SetStep("构建启动命令", 0.6);
            var quickPlay = QuickPlayRequest.Consume();
            AppendLog(quickPlay != null
                ? $"[INFO] 一键加入模式：使用当前账号会话连接 {quickPlay}"
                : App.Settings.Data.LanAllowNonPremium && App.Settings.Data.AuthMode != AuthModes.Offline
                    ? "[INFO] 联机兼容模式：本次以离线会话启动 → 局域网允许非正版玩家进入"
                    : "[INFO] 正版会话启动 → 局域网将开启正版验证（仅正版账号可加入）");

            if (quickPlay != null)
                AppendLog($"[INFO] 启动后自动加入服务器 {quickPlay}");
            var (java, args, cwd) = minecraft.BuildCommand(instance, javaInfo.Path, quickPlay);
            var processStartInfo = new ProcessStartInfo(java)
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
                processStartInfo.ArgumentList.Add(argument);

            var processTemp = Path.Combine(App.Paths.Root, "temp");
            Directory.CreateDirectory(processTemp);
            processStartInfo.Environment["TEMP"] = processTemp;
            processStartInfo.Environment["TMP"] = processTemp;

            SetStep("启动游戏进程", 0.75);
            _gameProcess = Process.Start(processStartInfo);
            _gameStartedAt = DateTime.UtcNow;
            StartLanWatcher(instance);
            try { _gameProcess!.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            ForceStopBtn.Visibility = Visibility.Collapsed;
            StopBtn.Visibility = Visibility.Visible;
            SetLaunchState(LaunchUiState.Running);
            ShowLaunchSuccessToast();
            LogBox.Document.Blocks.Clear();
            StartGameExitMonitor();

            var process = _gameProcess ?? throw new InvalidOperationException("游戏进程启动失败。");
            await ProcessOutputService.ReadAsync(
                process,
                lines => Dispatcher.BeginInvoke(() => AppendLogLines(lines)),
                lines => Dispatcher.BeginInvoke(() => AppendLogLines(lines.Select(line => "[ERR] " + line).ToList())));
            _launchInProgress = false;
        }
        catch (Exception ex)
        {
            _launchInProgress = false;
            ForceStopBtn.Visibility = Visibility.Collapsed;
            StopBtn.Visibility = Visibility.Collapsed;
            LaunchBtn.Visibility = Visibility.Visible;
            LaunchBtn.IsEnabled = true;
            AppendLog($"[ERROR] {ex.Message}");
            SetLaunchState(LaunchUiState.Crashed);
            AnimatedMessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>联机大厅「一键启动并加入」：回到首页后直接启动游戏。</summary>
    public void StartQuickPlayLaunch()
    {
        if (_launchInProgress) return;
        Launch_Click(this, new RoutedEventArgs());
    }

    // ===== 局域网世界监听（自动发现「对局域网开放」）=====

    private void StartLanWatcher(Instance instance)
    {
        StopLanWatcher();
        _promptedLanPorts.Clear();
        try
        {
            var gameDir = InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
            var watcher = new LanWorldWatcher(gameDir);
            watcher.WorldDetected += info =>
                Dispatcher.BeginInvoke(() => OnLanWorldDetected(instance, gameDir, info));
            watcher.WorldClosed += port =>
                Dispatcher.BeginInvoke(() =>
                {
                    // 允许同一端口下次开启时重新弹窗
                    _promptedLanPorts.Remove(port);
                    if (_relayClient == null) return;
                    AppendLog($"[INFO] 局域网世界已关闭（端口 {port}），正在关闭联机房间…");
                    CloseRelayClient();
                });
            watcher.Start();
            _lanWatcher = watcher;
        }
        catch
        {
            _lanWatcher = null;
        }
    }

    private void StopLanWatcher()
    {
        try { _lanWatcher?.Stop(); } catch { }
        _lanWatcher = null;
    }

    private void OnLanWorldDetected(Instance instance, string gameDir, LanWorldInfo info)
    {
        if (!_promptedLanPorts.Add(info.Port)) return;

        LocalModSnapshot snapshot;
        try
        {
            snapshot = LocalModScanner.Scan(gameDir, instance.Loader);
        }
        catch
        {
            snapshot = new LocalModSnapshot(false, "vanilla", new List<LocalModEntry>());
        }

        var result = LanShareDialog.Show(Window.GetWindow(this), info, snapshot,
            App.Settings.Data.LanAllowNonPremium);
        App.Settings.Data.LanAllowNonPremium = result.AllowNonPremium;
        App.Settings.Save();
        var choiceText = result.Choice switch
        {
            LanShareChoice.Public => "公开",
            LanShareChoice.KeyOnly => "仅密钥",
            LanShareChoice.Private => "不公开",
            _ => "未处理"
        };
        AppendLog($"[INFO] 检测到局域网世界「{info.Motd}」(端口 {info.Port}) · {snapshot.KindText} {snapshot.SummaryText} → {choiceText}");

        AppendLog(result.AllowNonPremium
            ? "[INFO] 联机房间允许非正版玩家进入（下次启动游戏生效）"
            : "[WARN] 联机房间已开启正版验证，仅正版账号可加入（下次启动游戏生效）");

        if (result.Choice is LanShareChoice.Public or LanShareChoice.KeyOnly)
            _ = PublishLanRoomAsync(instance, info, snapshot, result.Password);
    }

    // 把局域网世界发布到联机中继
    private async Task PublishLanRoomAsync(Instance instance, LanWorldInfo info, LocalModSnapshot snapshot, string password)
    {
        try
        {
            CloseRelayClient();
            var client = new RelayClient(RelayClient.RelayHost, info.Port);
            client.Log += text => Dispatcher.BeginInvoke(() => AppendLog("[联机] " + text));
            _relayClient = client;

            var mc = string.IsNullOrWhiteSpace(instance.McVersion) ? instance.VersionId : instance.McVersion;
            var gameDir = InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
            var mods = snapshot.Mods.Count > 0
                ? await Task.Run(() => LocalModScanner.Scan(gameDir, instance.Loader, withHash: true).Mods)
                : snapshot.Mods;

            var owner = App.Settings.Data.AuthMode == AuthModes.Offline
                ? App.Settings.Data.PlayerName
                : App.Settings.Data.AuthPlayerName;
            var ok = await client.CreateRoomAsync(
                info.Motd, mc, snapshot.Modded ? "modded" : "vanilla",
                instance.Loader ?? "", mods.Count, password, 10, mods, owner);

            if (ok)
                AppendLog($"[INFO] 房间已发布：房间码 {client.RoomCode}，地址 {client.PublicHost}:{client.DataPort}"
                          + (string.IsNullOrEmpty(password) ? "（公开）" : "（需密码）"));
        }
        catch (Exception ex)
        {
            AppendLog("[ERROR] 发布房间失败: " + ex.Message);
        }
    }

    private void CloseRelayClient()
    {
        try { _relayClient?.Close(); } catch { }
        _relayClient = null;
    }

    // 累计游戏时长（联机功能的防滥用门槛）
    private void AccumulatePlayTime()
    {
        if (_gameStartedAt == default) return;
        var elapsed = (long)(DateTime.UtcNow - _gameStartedAt).TotalSeconds;
        _gameStartedAt = default;
        if (elapsed <= 0) return;

        // 单次最多计 24 小时，避免长时间挂机刷时长
        App.Settings.Data.TotalPlaySeconds += Math.Min(elapsed, 24 * 3600);
        App.Settings.Save();
        AppendLog($"[INFO] 本次游戏时长 {PlayTimeGate.Format(elapsed)}，累计 {PlayTimeGate.DescribeTotal(App.Settings.Data)}");
    }

    private void StartGameExitMonitor()
    {
        _logTimer?.Stop();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _logTimer.Tick += (_, _) =>
        {
            if (_gameProcess == null || !_gameProcess.HasExited) return;

            _logTimer.Stop();
            var exitCode = _gameProcess.ExitCode;
            AccumulatePlayTime();
            StopLanWatcher();
            CloseRelayClient();
            StopBtn.Visibility = Visibility.Collapsed;
            LaunchBtn.IsEnabled = true;
            LaunchBtn.Visibility = Visibility.Visible;
            AppendLog($"\n[Game exited, code {exitCode}]");

            if (_userTerminated)
            {
                SetLaunchState(LaunchUiState.Idle);
            }
            else if (exitCode != 0)
            {
                SetLaunchState(LaunchUiState.Crashed);
                ShowCrashDialog();
            }
            else
            {
                SetLaunchState(LaunchUiState.Idle);
            }
        };
        _logTimer.Start();
    }

    private void ShowCrashDialog()
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        var dialog = new Window
        {
            Title = "Minecraft 发生崩溃",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Topmost = true,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            AllowsTransparency = true
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 20, 24, 20)
        };
        card.SetResourceReference(Border.BackgroundProperty, "DialogCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var icon = new TextBlock
        {
            Text = "\uE783",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 28,
            Margin = new Thickness(0, 0, 0, 10)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var title = new TextBlock
        {
            Text = "Minecraft 发生崩溃",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        header.Children.Add(icon);
        header.Children.Add(title);
        Grid.SetRow(header, 0);

        var message = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 18)
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        message.Inlines.Add(new System.Windows.Documents.Run("很抱歉，你的游戏出现了异常崩溃。") { FontSize = 13 });
        message.Inlines.Add(new System.Windows.Documents.LineBreak());
        message.Inlines.Add(new System.Windows.Documents.LineBreak());
        var dangerRun = new System.Windows.Documents.Run("请点击「分析日志」查看原因，或将错误报告发给他人协助排查，而不是截图这个窗口。")
        {
            FontSize = 13,
            FontWeight = FontWeights.Bold
        };
        dangerRun.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "DangerBrush");
        message.Inlines.Add(dangerRun);
        Grid.SetRow(message, 1);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var analyzeBtn = new Button
        {
            Content = "分析日志",
            Height = 36,
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.Hand
        };
        analyzeBtn.SetResourceReference(StyleProperty, "BtnPrimary");
        analyzeBtn.Click += (_, _) =>
        {
            dialog.Close();
            AnalyzeLog_Click(this, new RoutedEventArgs());
        };

        var closeBtn = new Button
        {
            Content = "关闭",
            Height = 36,
            Padding = new Thickness(16, 4, 16, 4),
            Cursor = Cursors.Hand
        };
        closeBtn.SetResourceReference(StyleProperty, "BtnBase");
        closeBtn.Click += (_, _) => dialog.Close();

        actions.Children.Add(analyzeBtn);
        actions.Children.Add(closeBtn);
        Grid.SetRow(actions, 2);

        root.Children.Add(header);
        root.Children.Add(message);
        root.Children.Add(actions);
        card.Child = root;
        dialog.Content = new Grid { Margin = new Thickness(24), Children = { card } };

        dialog.ShowDialog();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _userTerminated = true;
        try { _gameProcess?.Kill(); } catch { }
        StopBtn.Visibility = Visibility.Collapsed;
        LaunchBtn.IsEnabled = true;
        LaunchBtn.Visibility = Visibility.Visible;
    }

    private void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        _userTerminated = true;
        try { _gameProcess?.Kill(); } catch { }
        ForceStopBtn.Visibility = Visibility.Collapsed;
        LaunchBtn.IsEnabled = true;
        LaunchBtn.Visibility = Visibility.Visible;
        AppendLog("[INFO] 游戏已被强制终止");
    }

    private async void ShowLaunchSuccessToast()
    {
        LaunchSuccessToast.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        LaunchSuccessToast.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        await Task.Delay(4000);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
        fadeOut.Completed += (_, _) => LaunchSuccessToast.Visibility = Visibility.Collapsed;
        LaunchSuccessToast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void AppendLog(string text) => AppendLogLines(new[] { text });

    // 批量追加日志并限制面板长度，避免游戏输出量大时拖慢 UI
    private void AppendLogLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        var primary = (Brush)FindResource("PrimaryBrush");
        var normal = (Brush)FindResource("TextBrush");
        var font = new FontFamily("Consolas");

        foreach (var text in lines)
        {
            Brush brush;
            if (text.StartsWith("[ERR]") || text.StartsWith("[ERROR]"))
                brush = Brushes.OrangeRed;
            else if (text.StartsWith("[INFO]"))
                brush = primary;
            else if (text.StartsWith("[WARN]"))
                brush = Brushes.Gold;
            else
                brush = normal;

            LogBox.Document.Blocks.Add(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run(text))
            {
                Foreground = brush,
                FontSize = 11,
                FontFamily = font,
                Margin = new Thickness(0, 0, 0, 1)
            });
        }

        const int maxBlocks = 800;
        while (LogBox.Document.Blocks.Count > maxBlocks)
            LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);

        if (LogPanel.Visibility == Visibility.Visible)
            LogBox.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
    }

    private void ExpandLog_Click(object sender, RoutedEventArgs e) => SetLogPanelExpanded(true);

    private void CollapseLog_Click(object sender, RoutedEventArgs e) => SetLogPanelExpanded(false);

    private void SetLogPanelExpanded(bool expanded, bool animate = true)
    {
        var generation = ++_homePanelAnimationGeneration;
        var visiblePanel = expanded ? (FrameworkElement)LogPanel : AnnouncementPanel;
        var hiddenPanel = expanded ? (FrameworkElement)AnnouncementPanel : LogPanel;
        AnnouncementPanel.BeginAnimation(OpacityProperty, null);
        LogPanel.BeginAnimation(OpacityProperty, null);
        AnnouncementPanel.RenderTransform = null;
        LogPanel.RenderTransform = null;

        if (!animate)
        {
            hiddenPanel.Visibility = Visibility.Collapsed;
            hiddenPanel.Opacity = 1;
            hiddenPanel.IsHitTestVisible = false;
            visiblePanel.Visibility = Visibility.Visible;
            visiblePanel.Opacity = 1;
            visiblePanel.IsHitTestVisible = true;
            return;
        }

        const int durationMs = 420;
        hiddenPanel.Visibility = Visibility.Visible;
        hiddenPanel.Opacity = 1;
        hiddenPanel.IsHitTestVisible = false;
        visiblePanel.Visibility = Visibility.Visible;
        visiblePanel.Opacity = 0;
        visiblePanel.IsHitTestVisible = true;
        Panel.SetZIndex(hiddenPanel, 0);
        Panel.SetZIndex(visiblePanel, 1);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var transform = new TranslateTransform(expanded ? 18 : -18, 0);
        visiblePanel.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(transform.X, 0, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = ease
            });

        visiblePanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = ease
            });
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(durationMs));
        fadeOut.Completed += (_, _) =>
        {
            if (generation != _homePanelAnimationGeneration) return;
            hiddenPanel.Visibility = Visibility.Collapsed;
            hiddenPanel.BeginAnimation(OpacityProperty, null);
            hiddenPanel.Opacity = 1;
            visiblePanel.BeginAnimation(OpacityProperty, null);
            visiblePanel.Opacity = 1;
            visiblePanel.RenderTransform = null;
        };
        hiddenPanel.BeginAnimation(OpacityProperty, fadeOut);
    }

    private async void AnalyzeLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (InstanceSelector.SelectedItem is not ComboBoxItem item || item.Tag is not Instance instance)
            {
                AnimatedMessageBox.Show("请先选择一个实例", "提示");
                return;
            }

            SetLogPanelExpanded(true);
            var root = InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
            var liveLog = new System.Windows.Documents.TextRange(
                LogBox.Document.ContentStart, LogBox.Document.ContentEnd).Text;
            var report = _logAnalyzer.Analyze(root, instance.Name, liveLog);

            AppendLog("");
            AppendLog($"[INFO] === 日志分析报告 ({instance.Name}) ===");
            if (report.Sources.Length == 0)
            {
                AppendLog("[WARN] 未找到日志文件");
                return;
            }

            // 只输出「原因」和「解决方法」，不再显示冗长的日志证据
            var builder = new StringBuilder();
            foreach (var finding in report.Findings)
            {
                var prefix = finding.Severity switch
                {
                    "Critical" or "严重" => "[ERR]",
                    "Warning" or "警告" => "[WARN]",
                    _ => "[INFO]"
                };
                builder.AppendLine($"{prefix} 原因：{finding.Title}");
                builder.AppendLine("  解决方法：");
                foreach (var line in finding.Solution.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        builder.AppendLine($"    {line.Trim()}");
                builder.AppendLine();
            }

            // 输出完毕后自动汉化
            var translated = await TranslationService.ToChineseAsync(builder.ToString());
            foreach (var line in translated.Replace("\r", "").Split('\n'))
                AppendLog(line);
            AppendLog("[INFO] === 分析完成 ===");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] 日志分析失败: {ex.Message}");
        }
    }

    private void Avatar_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Skin files (*.png)|*.png|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        App.Settings.Data.AuthAvatarPath = dialog.FileName;
        App.Settings.Save();
        RefreshProfile();
    }

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings.Data;
        ResetLoginPanels();

        switch (settings.AuthMode)
        {
            case AuthModes.Microsoft:
                PopupMicrosoftRadio.IsChecked = true;
                RefreshMicrosoftAccounts();
                SetPanelStatic(PopupMicrosoftPanel, true);
                _activeLoginPanel = PopupMicrosoftPanel;
                break;
            case AuthModes.External:
                PopupExternalRadio.IsChecked = true;
                PopupAuthServerBox.Text = settings.AuthServer;
                PopupAuthAccountBox.Text = settings.AuthAccount;
                SetPanelStatic(PopupExternalPanel, true);
                _activeLoginPanel = PopupExternalPanel;
                break;
            default:
                PopupOfflineRadio.IsChecked = true;
                PopupOfflineNameBox.Text = settings.AuthMode == AuthModes.Offline
                    ? settings.AuthPlayerName
                    : settings.PlayerName;
                SetPanelStatic(PopupOfflinePanel, true);
                _activeLoginPanel = PopupOfflinePanel;
                break;
        }

        PopupLoginBtn.Content = settings.AuthMode == AuthModes.Microsoft ? "开始登录" : "登录";

        ShowLoginOverlay();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_activeLoginPanel == PopupOfflinePanel)
                Keyboard.Focus(PopupOfflineNameBox);
            else if (_activeLoginPanel == PopupExternalPanel)
                Keyboard.Focus(PopupAuthServerBox);
        }), DispatcherPriority.Input);
    }

    private void ResetLoginPanels()
    {
        _panelSwitchTimer?.Stop();
        _panelSwitchTimer = null;
        _panelAnimationGeneration++;
        _activeLoginPanel = null;
        StopPanelAnimation(PopupOfflinePanel);
        StopPanelAnimation(PopupExternalPanel);
        StopPanelAnimation(PopupMicrosoftPanel);
        SetPanelStatic(PopupOfflinePanel, false);
        SetPanelStatic(PopupExternalPanel, false);
        SetPanelStatic(PopupMicrosoftPanel, false);
        PopupMicrosoftStatus.Text = "";
        PopupMicrosoftCancelBtn.Visibility = Visibility.Collapsed;
        PopupMicrosoftLoginState(false);
    }

    private static void SetPanelStatic(Panel panel, bool visible)
    {
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        panel.Opacity = visible ? 1 : 0;
        panel.MaxHeight = visible ? 300 : 0;
        panel.RenderTransform = new TranslateTransform();
    }

    private void ShowLoginOverlay()
    {
        LoginOverlay.Visibility = Visibility.Visible;
        OverlayBrush.BeginAnimation(SolidColorBrush.OpacityProperty,
            new DoubleAnimation(0, 0.25, TimeSpan.FromMilliseconds(200)));

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
        CardTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
    }

    private void HideLoginOverlay()
    {
        _microsoftLoginCts?.Cancel();
        _panelSwitchTimer?.Stop();
        _panelSwitchTimer = null;
        _panelAnimationGeneration++;
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fadeOut = new DoubleAnimation(0.25, 0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) => LoginOverlay.Visibility = Visibility.Collapsed;
        OverlayBrush.BeginAnimation(SolidColorBrush.OpacityProperty, fadeOut);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn });
        CardTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 15, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn });
    }

    private void LoginOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) HideLoginOverlay();
    }

    private void PopupAuth_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;

        Panel? target = tag switch
        {
            AuthModes.Offline => PopupOfflinePanel,
            AuthModes.External => PopupExternalPanel,
            AuthModes.Microsoft => PopupMicrosoftPanel,
            _ => null
        };

        if (target == _activeLoginPanel)
        {
            return;
        }

        if (tag != AuthModes.Microsoft)
            _microsoftLoginCts?.Cancel();

        if (tag == AuthModes.Microsoft)
            RefreshMicrosoftAccounts();

        _panelSwitchTimer?.Stop();
        _panelSwitchTimer = null;
        var generation = ++_panelAnimationGeneration;
        var previous = _activeLoginPanel;
        _activeLoginPanel = target;

        if (previous != null)
            AnimatePanel(previous, false, generation);

        if (target != null)
        {
            _panelSwitchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _panelSwitchTimer.Tick += (_, _) =>
            {
                _panelSwitchTimer?.Stop();
                _panelSwitchTimer = null;
                if (generation == _panelAnimationGeneration)
                    AnimatePanel(target, true, generation);
            };
            _panelSwitchTimer.Start();
        }

        PopupMicrosoftStatus.Text = tag == AuthModes.Microsoft ? "使用浏览器登录 Microsoft 账户" : "";
        PopupLoginBtn.Content = tag == AuthModes.Microsoft ? "网页授权登录" : "登录";
    }

    private void AnimatePanel(Panel panel, bool show, int generation)
    {
        if (show)
        {
            StopPanelAnimation(panel);
            panel.Visibility = Visibility.Visible;
            panel.MaxHeight = 0;
            panel.Opacity = 0;
            panel.RenderTransform = new TranslateTransform(0, -6);

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            panel.BeginAnimation(FrameworkElement.MaxHeightProperty,
                new DoubleAnimation(0, 300, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
            panel.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
            panel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
            return;
        }

        if (panel.Visibility != Visibility.Visible) return;

        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var collapse = new DoubleAnimation(Math.Max(panel.ActualHeight, 1), 0,
            TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn };
        collapse.Completed += (_, _) =>
        {
            if (generation != _panelAnimationGeneration) return;
            panel.Visibility = Visibility.Collapsed;
            panel.MaxHeight = 0;
        };
        panel.BeginAnimation(FrameworkElement.MaxHeightProperty, collapse);
        if (panel.RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn });
        panel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn });
    }

    private static void StopPanelAnimation(Panel panel)
    {
        panel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        panel.BeginAnimation(UIElement.OpacityProperty, null);
        if (panel.RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void PasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        if (_passwordVisible)
        {
            PopupAuthPasswordShow.Text = PopupAuthPasswordBox.Password;
            PopupAuthPasswordShow.Visibility = Visibility.Visible;
            PopupAuthPasswordBox.Visibility = Visibility.Collapsed;
            PasswordToggleIcon.Text = "🔒";
        }
        else
        {
            PopupAuthPasswordBox.Password = PopupAuthPasswordShow.Text;
            PopupAuthPasswordBox.Visibility = Visibility.Visible;
            PopupAuthPasswordShow.Visibility = Visibility.Collapsed;
            PasswordToggleIcon.Text = "👁";
        }
    }

    private async void PopupLogin_Click(object sender, RoutedEventArgs e)
    {
        PopupLoginBtn.IsEnabled = false;
        try
        {
            if (PopupOfflineRadio.IsChecked == true)
            {
                _loginService.UseOffline(PopupOfflineNameBox.Text);
            }
            else if (PopupExternalRadio.IsChecked == true)
            {
                var password = _passwordVisible
                    ? PopupAuthPasswordShow.Text
                    : PopupAuthPasswordBox.Password;
                PopupLoginBtn.Content = "登录中...";
                await _loginService.LoginExternalAsync(
                    PopupAuthServerBox.Text.Trim(),
                    PopupAuthAccountBox.Text.Trim(),
                    password);
            }
            else if (PopupMicrosoftRadio.IsChecked == true)
            {
                _microsoftLoginCts?.Cancel();
                _microsoftLoginCts = new CancellationTokenSource();
                PopupMicrosoftLoginState(true);
                PopupMicrosoftStatus.Text = "正在打开 Microsoft 网页授权...";
                PopupMicrosoftCancelBtn.Visibility = Visibility.Visible;
                var profile = await MicrosoftAuthService.LoginWithBrowserAsync(_microsoftLoginCts.Token);
                MicrosoftAuthService.SaveProfile(profile);
                PopupMicrosoftStatus.Text = $"已登录 {profile.Name}";
                HideLoginOverlay();
                RefreshProfile();
                return;
            }

            HideLoginOverlay();
            RefreshProfile();
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                AnimatedMessageBox.Show(ex.Message, "登录失败");
        }
        finally
        {
            PopupLoginBtn.IsEnabled = true;
            PopupMicrosoftLoginState(false);
        }
    }

    private void RefreshMicrosoftAccounts()
    {
        PopupMicrosoftAccountBox.Items.Clear();
        foreach (var account in MicrosoftAuthService.GetAccounts())
        {
            PopupMicrosoftAccountBox.Items.Add(new ComboBoxItem
            {
                Content = account.PlayerName,
                Tag = account.Id,
                ToolTip = account.PlayerName
            });
        }

        var activeId = App.Settings.Data.ActiveMicrosoftAccountId;
        PopupMicrosoftAccountBox.SelectedItem = PopupMicrosoftAccountBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == activeId)
            ?? PopupMicrosoftAccountBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var hasAccount = PopupMicrosoftAccountBox.SelectedItem != null;
        PopupMicrosoftUseBtn.IsEnabled = hasAccount;
        PopupMicrosoftRemoveBtn.IsEnabled = hasAccount;
    }

    private void PopupMicrosoftLoginState(bool active)
    {
        if (!active) PopupMicrosoftCancelBtn.Visibility = Visibility.Collapsed;
        PopupLoginBtn.Content = active
            ? "等待网页授权..."
            : PopupMicrosoftRadio.IsChecked == true ? "网页授权登录" : "登录";
        PopupMicrosoftRadio.IsEnabled = !active;
        PopupOfflineRadio.IsEnabled = !active;
        PopupExternalRadio.IsEnabled = !active;
        PopupMicrosoftAccountBox.IsEnabled = !active;
        PopupMicrosoftUseBtn.IsEnabled = !active && PopupMicrosoftAccountBox.SelectedItem != null;
        PopupMicrosoftRemoveBtn.IsEnabled = !active && PopupMicrosoftAccountBox.SelectedItem != null;
    }

    private void CancelMicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        _microsoftLoginCts?.Cancel();
        PopupMicrosoftStatus.Text = "已取消 Microsoft 登录。";
        PopupMicrosoftLoginState(false);
        PopupMicrosoftCancelBtn.Visibility = Visibility.Collapsed;
    }

    private void UseMicrosoftAccount_Click(object sender, RoutedEventArgs e)
    {
        if (PopupMicrosoftAccountBox.SelectedItem is not ComboBoxItem { Tag: string accountId }) return;
        try
        {
            MicrosoftAuthService.UseAccount(accountId);
            HideLoginOverlay();
            RefreshProfile();
        }
        catch (Exception ex) { AnimatedMessageBox.Show(ex.Message, "账户切换失败"); }
    }

    private void RemoveMicrosoftAccount_Click(object sender, RoutedEventArgs e)
    {
        if (PopupMicrosoftAccountBox.SelectedItem is not ComboBoxItem { Tag: string accountId }) return;
        if (AnimatedMessageBox.Show("移除这个已保存的 Microsoft 账户？", "移除账户",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            MicrosoftAuthService.RemoveAccount(accountId);
            RefreshMicrosoftAccounts();
            RefreshProfile();
        }
        catch (Exception ex) { AnimatedMessageBox.Show(ex.Message, "移除账户失败"); }
    }

    private void QuickInstall_Click(object sender, RoutedEventArgs e)
    {
        FindFrame()?.Navigate(_versionsPage);
    }

    private void VersionList_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToLocalVersions();
        else
            FindFrame()?.Navigate(_versionsPage);
    }

    private void VersionSettings_Click(object sender, RoutedEventArgs e)
    {
        var page = InstanceSelector.SelectedItem is ComboBoxItem item && item.Tag is Instance instance
            ? new VersionSettingsPage(instance)
            : new VersionSettingsPage();
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateTo(page);
    }

    private static Frame? FindFrame()
    {
        var window = Window.GetWindow(Application.Current.MainWindow);
        return window == null ? null : FindChild<Frame>(window);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            var result = FindChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
