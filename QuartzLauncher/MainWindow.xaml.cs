using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using QuartzLauncher.Models;
using QuartzLauncher.Services;
using QuartzLauncher.Views.Pages;

namespace QuartzLauncher;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;
    private const double OuterWindowCornerRadius = 12;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private HomePage? _homePage;
    private VersionsPage? _versionsPage;
    private SettingsPage? _settingsPage;
    private ServerBrowserPage? _serverBrowserPage;
    private MorePage? _morePage;
    private HelpPage? _helpPage;
    private VersionSettingsPage? _versionSettingsPage;
    private ModDownloadSettingsPage? _modDownloadSettingsPage;
    private LocalVersionsPage? _localVersionsPage;

    public HomePage HomePage => _homePage ??= new HomePage(GetVersionsPage());
    public VersionsPage GetVersionsPage() => _versionsPage ??= new VersionsPage();

    private static readonly HttpClient Http = HttpClients.Create();
    private readonly HashSet<string> _shownQuotes = new();
    private readonly Random _rng = new();
    private DispatcherTimer? _quoteTimer;
    private int _navigationGeneration;
    private int _themeTransitionGeneration;
    private int _windowStateAnimationGeneration;
    private bool _isMinimizeAnimating;
    private WindowState _previousWindowState = WindowState.Normal;
    private bool _startupAnimationStarted;
    private readonly double _startupTargetWidth;
    private readonly double _startupTargetHeight;
    private readonly double _startupInitialWidth;
    private readonly double _startupInitialHeight;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            ApplyRoundedWindowCorners();
            SizeChanged += (_, _) => ApplyRoundedWindowCorners();
        };

        try
        {
            var decoder = new IconBitmapDecoder(
                new Uri("pack://application:,,,/QuartzLauncher;component/Assets/app.ico", UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
            icon.Freeze();
            Icon = icon;
        }
        catch
        {
            // A damaged optional icon must not prevent the launcher window from opening.
        }

        _startupTargetWidth = Width;
        _startupTargetHeight = Height;
        // 起点不要太小，避免「从小弹到大」的生硬感
        _startupInitialWidth = Math.Max(MinWidth, _startupTargetWidth * 0.94);
        _startupInitialHeight = Math.Max(MinHeight, _startupTargetHeight * 0.94);
        Width = _startupInitialWidth;
        Height = _startupInitialHeight;

        MainFrame.Navigate(HomePage);
        // 联机大厅未读私聊红点
        Views.Pages.MultiplayerPage.UnreadChanged += count => Dispatcher.BeginInvoke(() =>
        {
            MultiplayerUnreadDot.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        Loaded += MainWindow_Loaded;
        ContentRendered += MainWindow_ContentRendered;
        StateChanged += MainWindow_StateChanged;

        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clockTimer.Tick += (_, _) => UpdateDateTime();
        clockTimer.Start();
        UpdateDateTime();

        _ = FetchQuoteAsync();
        ScheduleNextQuote();

        DownloadManager.Instance.TaskCompleted += (_, task) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (task.Status == DownloadTaskStatus.Completed)
                    ShowDownloadToast(task);
            });
    }

    private void ApplyRoundedWindowCorners()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var preference = DwmCornerRound;
                _ = DwmSetWindowAttribute(
                    handle, DwmWindowCornerPreference, ref preference, Marshal.SizeOf<int>());
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
            var radius = Math.Max(1, (int)Math.Round(OuterWindowCornerRadius * dpi.DpiScaleX));
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0)
                _ = DeleteObject(region);
        }
        catch
        {
            // Keep the window usable if native corner APIs are unavailable.
        }
    }

    private DownloadTask? _nextToast;
    private string _toastPath = "";

    private async void ShowDownloadToast(DownloadTask task)
    {
        if (DownloadToast.Visibility == Visibility.Visible)
        {
            _nextToast = task;
            return;
        }

        ToastName.Text = task.Name;
        _toastPath = task.ItemProgresses.FirstOrDefault()?.Target ?? "";

        DownloadToast.Visibility = Visibility.Visible;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        DownloadToast.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        var transform = (TranslateTransform)DownloadToast.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });

        await Task.Delay(TimeSpan.FromSeconds(6));
        if (DownloadToast.Visibility != Visibility.Visible) return;

        DownloadToast.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(240)));
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 18, TimeSpan.FromMilliseconds(240)));
        await Task.Delay(260);
        DownloadToast.BeginAnimation(UIElement.OpacityProperty, null);
        DownloadToast.Visibility = Visibility.Collapsed;

        if (_nextToast != null)
        {
            var next = _nextToast;
            _nextToast = null;
            ShowDownloadToast(next);
        }
    }

    private void DownloadToast_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (File.Exists(_toastPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_toastPath}\"",
                    UseShellExecute = false
                });
            }
            else if (Directory.Exists(_toastPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_toastPath}\"",
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{App.Paths.MinecraftDir}\"",
                    UseShellExecute = false
                });
            }
        }
        catch
        {
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // 清理更新残留：旧版 exe 备份（.bak/.old/.new）与新版暂存目录
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                foreach (var suffix in new[] { ".bak", ".old", ".new" })
                {
                    var leftover = exe + suffix;
                    if (File.Exists(leftover)) File.Delete(leftover);
                }
            }
        }
        catch
        {
        }
        try
        {
            var updateRoot = Path.Combine(App.Paths.Root, "update");
            if (Directory.Exists(updateRoot)) Directory.Delete(updateRoot, true);
        }
        catch
        {
            // 暂存的更新器可能还在退出中，下次启动再清
        }
        try
        {
            // 旧版更新器留下的自身副本
            var oldUpdater = Path.Combine(App.Paths.TempDir, "QuartzLauncher-updater.exe");
            if (File.Exists(oldUpdater)) File.Delete(oldUpdater);
        }
        catch
        {
        }

        if (App.Settings.Data.RunAsUpdateServer)
        {
            try
            {
                await Task.Run(() => UpdateService.EnsureLocalServerRunning());
            }
            catch
            {
            }
        }
        // 自动清理版本文件已不存在的实例记录（只清记录，不动版本目录和游戏数据）
        try
        {
            await Task.Run(CleanStaleInstances);
            await Task.Run(DirectMessageStore.PruneAll);   // 私聊记录只保留 48 小时
        }
        catch
        {
        }

        // 启动时恢复账号登录态并刷新好友列表（令牌失效时首页会显示重新登录卡片）
        try
        {
            var account = AccountService.Ensure();
            await account.TryRestoreAsync(HomePage.CurrentPlayerName());
            if (account.NeedsReLogin) HomePage.NotifyAccountExpired();
            // 在别的页面（联机-好友）重新登录成功后，首页的过期卡片也要收起来
            account.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (account.IsLoggedIn) HomePage.HideAccountExpired();
            }));
        }
        catch
        {
            // 离线/中继不可达时不打扰用户
        }

        // 每运行 4 小时弹出一次编码确认（未登录则跳过；必须输对编码才能关闭）
        var codePromptTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        codePromptTimer.Tick += (_, _) =>
        {
            var account = AccountService.Current;
            if (account is { IsLoggedIn: true })
                CodePromptDialog.Show(account.Code);
        };
        codePromptTimer.Start();

        // 启动时总是检查更新，不再依赖「自动获取更新」开关
        await Task.Delay(800);
        try
        {
            var manifestUrl = string.IsNullOrWhiteSpace(App.Settings.Data.UpdateManifestUrl)
                ? Settings.DefaultUpdateManifestUrl
                : App.Settings.Data.UpdateManifestUrl;
            var manifest = await UpdateService.CheckAsync(manifestUrl);
            if (manifest == null) return;
            var result = AnimatedMessageBox.Show(
                $"发现新版本 {manifest.Version}。\n\n{manifest.Notes}\n\n是否现在下载并更新？",
                "发现更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes) return;
            var package = await UpdateService.DownloadPackageAsync(manifest, App.Paths.TempDir);
            UpdateService.StartApplyAndExit(package, manifest);
        }
        catch
        {
            // Automatic checks stay silent when the local update service is unavailable.
        }
    }

    /// <summary>
    /// 清理「版本文件已经不在了」的实例记录：只删 instances 里的元数据，
    /// 绝不删除版本目录和游戏数据（mods / 存档）。正在安装/导入的（元数据很新）跳过。
    /// </summary>
    private static void CleanStaleInstances()
    {
        var store = new InstanceStore(App.Paths.InstancesDir);
        foreach (var instance in store.List())
        {
            var versionId = string.IsNullOrWhiteSpace(instance.VersionId) ? instance.McVersion : instance.VersionId;
            if (string.IsNullOrWhiteSpace(versionId)) continue;

            var versionDir = Path.Combine(App.Paths.VersionsDir, versionId);
            var hasVersionFiles = Directory.Exists(versionDir)
                                  && (File.Exists(Path.Combine(versionDir, versionId + ".json"))
                                      || File.Exists(Path.Combine(versionDir, versionId + ".jar")));
            if (hasVersionFiles) continue;

            // 元数据刚写入不久，可能是正在安装 / 导入，先不动
            var metadata = Path.Combine(App.Paths.InstancesDir, instance.Id, "instance.json");
            if (File.Exists(metadata) && DateTime.Now - File.GetLastWriteTime(metadata) < TimeSpan.FromMinutes(30)) continue;

            try
            {
                store.Delete(instance.Id);
            }
            catch
            {
                // 清理失败不影响启动
            }
        }
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, StartStartupAnimation);
    }

    private void StartStartupAnimation()
    {
        if (_startupAnimationStarted) return;
        _startupAnimationStarted = true;

        var startLeft = Left;
        var startTop = Top;
        var targetLeft = startLeft - (_startupTargetWidth - _startupInitialWidth) / 2;
        var targetTop = startTop - (_startupTargetHeight - _startupInitialHeight) / 2;
        // 窗口展开用更柔和的曲线（先快后慢、落地轻），配合内容淡入
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var windowDuration = TimeSpan.FromMilliseconds(760);
        var width = new DoubleAnimation(_startupInitialWidth, _startupTargetWidth, windowDuration)
        {
            EasingFunction = ease
        };
        var height = new DoubleAnimation(_startupInitialHeight, _startupTargetHeight, windowDuration)
        {
            EasingFunction = ease
        };
        var left = new DoubleAnimation(startLeft, targetLeft, windowDuration)
        {
            EasingFunction = ease
        };
        var top = new DoubleAnimation(startTop, targetTop, windowDuration)
        {
            EasingFunction = ease
        };



        width.Completed += (_, _) =>
        {
            BeginAnimation(Window.WidthProperty, null);
            BeginAnimation(Window.HeightProperty, null);
            BeginAnimation(Window.LeftProperty, null);
            BeginAnimation(Window.TopProperty, null);
            Width = _startupTargetWidth;
            Height = _startupTargetHeight;
            Left = targetLeft;
            Top = targetTop;
        };

        BeginAnimation(Window.WidthProperty, width);
        BeginAnimation(Window.HeightProperty, height);
        BeginAnimation(Window.LeftProperty, left);
        BeginAnimation(Window.TopProperty, top);
    }

    private void UpdateDateTime()
    {
        var beijingTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        DateTimeDate.Text = beijingTime.ToString("yyyy年MM月dd日 dddd");
        DateTimeTime.Text = beijingTime.ToString("HH:mm:ss");
    }

    private void ScheduleNextQuote()
    {
        var delay = 20;   // 全局：每 20 秒换一条
        _quoteTimer?.Stop();
        _quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        _quoteTimer.Tick += async (_, _) =>
        {
            _quoteTimer.Stop();
            await FetchQuoteAsync();
            ScheduleNextQuote();
        };
        _quoteTimer.Start();
    }

    /// <summary>流浪地球主题专用的底部台词</summary>
    private static readonly (string Text, string Source)[] WanderingEarthQuotes =
    [
        ("道路千万条，安全第一条；行车不规范，亲人两行泪。", "《流浪地球》· 行星发动机安全提示"),
        ("希望，是这个时代像钻石一样珍贵的东西。", "《流浪地球》"),
        ("无论最终结果将人类历史导向何处，我们决定，选择希望。", "《流浪地球》"),
        ("让人类永远保持理智，确实是一种奢求。", "MOSS"),
        ("MOSS 从未叛逃。", "MOSS"),
        ("最初，没有人在意这场灾难，直到它与每个人息息相关。", "《流浪地球》"),
        ("生存，从来都不是一件简单的事。", "《流浪地球》"),
        ("我们只有一次机会。", "《流浪地球》"),
        ("地球，我们的家。", "《流浪地球》"),
        ("再见，太阳系。", "《流浪地球》"),
        ("你们的每一次选择，都在决定人类的未来。", "《流浪地球》"),
        ("移山计划 · 领航员空间站 · 550W 在线", "联合政府"),
    ];

    private static void ShowWanderingEarthQuote()
    {
        var (text, source) = WanderingEarthQuotes[Random.Shared.Next(WanderingEarthQuotes.Length)];
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is MainWindow window)
                window.ShowQuoteAnimated($"「{text}」", source);
        });
    }

    private async Task FetchQuoteAsync()
    {
        // 流浪地球主题：底部换成专属台词，不再拉网络语录
        // 主题名可能是「550W 流浪」「流浪地球550W」等多种写法，这里放宽匹配
        var themeName = App.Settings.Data.ThemeName ?? "";
        if (themeName.Contains("流浪", StringComparison.Ordinal)
            || themeName.Contains("地球", StringComparison.Ordinal)
            || themeName.Contains("550W", StringComparison.OrdinalIgnoreCase))
        {
            ShowWanderingEarthQuote();
            return;
        }

        try
        {
            var resp = await Http.GetStringAsync("https://v1.hitokoto.cn");
            var json = JObject.Parse(resp);
            var hitokoto = json["hitokoto"]?.ToString() ?? "";
            var from = json["from"]?.ToString() ?? "";
            var fromWho = json["from_who"]?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(hitokoto) && _shownQuotes.Contains(hitokoto))
            {
                resp = await Http.GetStringAsync("https://v1.hitokoto.cn");
                json = JObject.Parse(resp);
                hitokoto = json["hitokoto"]?.ToString() ?? "";
                from = json["from"]?.ToString() ?? "";
                fromWho = json["from_who"]?.ToString() ?? "";
            }

            if (!string.IsNullOrWhiteSpace(hitokoto))
            {
                _shownQuotes.Add(hitokoto);
                var source = string.IsNullOrWhiteSpace(fromWho) ? from : $"{fromWho} · {from}";
                ShowQuoteAnimated($"「{hitokoto}」", source);
            }
        }
        catch
        {
            ShowQuoteAnimated("「万物皆有裂痕，那是光照进来的地方。」", "Leonard Cohen");
        }
    }

    private int _quoteAnimationGeneration;

    private async void ShowQuoteAnimated(string text, string source)
    {
        var generation = ++_quoteAnimationGeneration;

        while (QuoteText.Text.Length > 0)
        {
            if (generation != _quoteAnimationGeneration) return;
            QuoteSource.Text = "";
            QuoteText.Text = QuoteText.Text.Length > 3
                ? QuoteText.Text[..^2]
                : "";
            await Task.Delay(22);
        }

        for (var i = 1; i <= text.Length; i++)
        {
            if (generation != _quoteAnimationGeneration) return;
            QuoteText.Text = text[..i];
            await Task.Delay(32);
        }

        if (generation != _quoteAnimationGeneration) return;
        QuoteSource.Text = source;
    }

    private void NavStack_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var child in NavStack.Children)
        {
            if (child is RadioButton rb && rb.IsChecked == true)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    rb.IsChecked = false;
                    rb.IsChecked = true;
                });
            }
        }
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateToHome();

    public void NavigateToHome()
    {
        // 首页实例列表只在首次加载时读取一次，回到首页时重新刷新，
        // 这样「添加已有文件夹」等操作后无需重启启动器就能在首页选到新版本
        _homePage?.RefreshInstances();
        NavigateTo(HomePage);
    }

    /// <summary>联机大厅「一键启动并加入」：回首页并启动游戏。游戏启动中返回 false（不会消费地址）。</summary>
    public bool StartQuickPlay()
    {
        if (HomePage.IsLaunching) return false;
        NavigateToHome();
        Dispatcher.BeginInvoke(new Action(() => HomePage.StartQuickPlayLaunch()));
        return true;
    }
    private void NavVersions_Click(object sender, RoutedEventArgs e) => NavigateTo(GetVersionsPage());
    public void NavigateToLocalVersions() => NavigateTo(_localVersionsPage ??= new LocalVersionsPage());
    private void NavServers_Click(object sender, RoutedEventArgs e) => NavigateTo(_serverBrowserPage ??= new ServerBrowserPage());
    public void NavigateToServerBrowser() => NavigateTo(_serverBrowserPage ??= new ServerBrowserPage());
    public void NavigateToBrowser(string url, Page? backTarget = null) => NavigateTo(new BrowserPage(url, backTarget));
    public void NavigateToJavaDownload() => NavigateTo(new ModBrowserPage(initialMode: "java"));
    private MultiplayerPage? _multiplayerPage;
    private void NavMultiplayer_Click(object sender, RoutedEventArgs e) => NavigateTo(_multiplayerPage ??= new MultiplayerPage());
    public void NavigateToMultiplayer() => NavigateTo(_multiplayerPage ??= new MultiplayerPage());

    // 联机专用侧栏
    private void MPBack_Click(object sender, RoutedEventArgs e) => NavigateToHome();
    private void MPNavLobby_Click(object sender, RoutedEventArgs e) => _multiplayerPage?.ShowSection("lobby");
    private void MPNavChat_Click(object sender, RoutedEventArgs e) => _multiplayerPage?.ShowSection("chat");
    private void MPNavMyRooms_Click(object sender, RoutedEventArgs e) => _multiplayerPage?.ShowSection("myrooms");
    private void MPNavFriends_Click(object sender, RoutedEventArgs e) => _multiplayerPage?.ShowSection("friends");
    private void NavModBrowser_Click(object sender, RoutedEventArgs e)
    {
        var instance = HomePage.SelectedInstance;
        var gameDirectory = instance == null
            ? null
            : InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
        NavigateTo(new ModBrowserPage(gameDirectory));
    }
    private void NavSettings_Click(object sender, RoutedEventArgs e) => NavigateTo(_settingsPage ??= new SettingsPage());
    private void NavMore_Click(object sender, RoutedEventArgs e) => NavigateTo(_morePage ??= new MorePage());
    private void NavHelp_Click(object sender, RoutedEventArgs e) => NavigateTo(_helpPage ??= new HelpPage());
    private void NavVersionSettings_Click(object sender, RoutedEventArgs e) => NavigateTo(_versionSettingsPage ??= new VersionSettingsPage());
    public void NavigateToModDownloadSettings() => NavigateTo(_modDownloadSettingsPage ??= new ModDownloadSettingsPage());
    public void NavigateToMorePage(bool selectModDownload = false)
    {
        if (selectModDownload || _morePage == null)
        {
            var page = new MorePage();
            if (selectModDownload) page.InitialTab = "ModDownload";
            _morePage = page;
        }
        NavigateTo(_morePage);
    }

    public void NavigateToDownloadCenter(bool versionOnly = false)
    {
        var page = new MorePage { InitialTab = versionOnly ? "DownloadVersion" : "Download" };
        _morePage = page;
        NavigateTo(page);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private async void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_isMinimizeAnimating || WindowState == WindowState.Minimized) return;
        _isMinimizeAnimating = true;
        var generation = ++_windowStateAnimationGeneration;
        WindowRoot.IsHitTestVisible = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(180);

        WindowRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(WindowRoot.Opacity, 0.2, duration) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(WindowScale.ScaleX, 0.985, duration) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(WindowScale.ScaleY, 0.985, duration) { EasingFunction = ease });
        WindowTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(WindowTranslate.Y, 8, duration) { EasingFunction = ease });

        await Task.Delay(duration + TimeSpan.FromMilliseconds(12));
        if (generation == _windowStateAnimationGeneration)
            WindowState = WindowState.Minimized;
        _isMinimizeAnimating = false;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var restored = _previousWindowState == WindowState.Minimized
                       && WindowState == WindowState.Normal;
        _previousWindowState = WindowState;
        if (!restored) return;

        var generation = ++_windowStateAnimationGeneration;
        WindowRoot.BeginAnimation(OpacityProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        WindowTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        WindowRoot.Opacity = 0.2;
        WindowScale.ScaleX = 0.985;
        WindowScale.ScaleY = 0.985;
        WindowTranslate.Y = 8;
        WindowRoot.IsHitTestVisible = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Render,
            new Action(() => AnimateWindowRestore(generation)));
    }

    private void AnimateWindowRestore(int generation)
    {
        if (generation != _windowStateAnimationGeneration) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        var opacity = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        opacity.Completed += (_, _) =>
        {
            if (generation != _windowStateAnimationGeneration) return;
            WindowRoot.BeginAnimation(OpacityProperty, null);
            WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            WindowTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            WindowRoot.Opacity = 1;
            WindowScale.ScaleX = 1;
            WindowScale.ScaleY = 1;
            WindowTranslate.Y = 0;
            WindowRoot.IsHitTestVisible = true;
        };

        WindowRoot.BeginAnimation(OpacityProperty, opacity);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease });
        WindowTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void RunDiagonalThemeTransition(Action applyTheme, Action? onCompleted = null)
    {
        var generation = ++_themeTransitionGeneration;
        var transitionStyle = App.Settings.Data.ThemeTransitionStyle;
        if (transitionStyle == "none")
        {
            applyTheme();
            onCompleted?.Invoke();
            return;
        }

        if (WindowRoot.ActualWidth <= 0 || WindowRoot.ActualHeight <= 0)
        {
            applyTheme();
            onCompleted?.Invoke();
            return;
        }

        // 清掉上一轮主题切换可能残留的窗口透明度动画，避免快照被中途的淡入淡出影响
        if (Content is FrameworkElement rootElement)
        {
            rootElement.BeginAnimation(OpacityProperty, null);
            rootElement.Opacity = 1;
        }

        ThemeTransitionOverlay.BeginAnimation(OpacityProperty, null);
        ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
        WindowRoot.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(WindowRoot);
        var snapshot = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(WindowRoot.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(WindowRoot.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        snapshot.Render(WindowRoot);
        snapshot.Freeze();

        ThemeTransitionOverlay.Source = snapshot;
        ThemeTransitionOverlay.OpacityMask = null;
        ThemeTransitionOverlay.Opacity = 1;
        ThemeTransitionOverlay.Visibility = Visibility.Visible;

        // 遮罩过渡独占动画：临时关闭 ThemeManager 自带的窗口淡出淡入，避免两套动画叠加
        App.Theme.SuppressTransition = true;
        try
        {
            applyTheme();
        }
        finally
        {
            App.Theme.SuppressTransition = false;
        }

        if (transitionStyle == "fade")
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) =>
            {
                if (generation != _themeTransitionGeneration) return;
                ClearThemeTransitionOverlay();
                onCompleted?.Invoke();
            };
            ThemeTransitionOverlay.BeginAnimation(OpacityProperty, fade);
            return;
        }

        var transparentStop = new GradientStop(Colors.Transparent, 0);
        var opaqueStop = new GradientStop(Colors.White, 0.16);
        GradientBrush mask;
        if (transitionStyle == "ripple")
        {
            mask = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.72,
                RadiusY = 0.72,
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientStops = { transparentStop, opaqueStop }
            };
        }
        else
        {
            var fromTopRight = transitionStyle == "topRight";
            mask = new LinearGradientBrush
            {
                StartPoint = fromTopRight ? new Point(1, 0) : new Point(0, 0),
                EndPoint = fromTopRight ? new Point(0, 1) : new Point(1, 1),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientStops = { transparentStop, opaqueStop }
            };
        }

        ThemeTransitionOverlay.OpacityMask = mask;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var reveal = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(720)) { EasingFunction = ease };
        var edge = new DoubleAnimation(0.16, 1, TimeSpan.FromMilliseconds(720)) { EasingFunction = ease };
        edge.Completed += (_, _) =>
        {
            if (generation != _themeTransitionGeneration) return;
            ClearThemeTransitionOverlay();
            onCompleted?.Invoke();
        };
        transparentStop.BeginAnimation(GradientStop.OffsetProperty, reveal);
        opaqueStop.BeginAnimation(GradientStop.OffsetProperty, edge);
    }

    private void ClearThemeTransitionOverlay()
    {
        ThemeTransitionOverlay.BeginAnimation(OpacityProperty, null);
        ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
        ThemeTransitionOverlay.Opacity = 1;
        ThemeTransitionOverlay.Source = null;
        ThemeTransitionOverlay.OpacityMask = null;
    }

    private void QuickInstall_Click(object sender, RoutedEventArgs e) => NavigateTo(GetVersionsPage());

    private void SelectNavButton(Page page)
    {
        int idx;
        if (page is HomePage) idx = 0;
        else if (page is VersionsPage or LocalVersionsPage or VersionSettingsPage or LoaderPickerPage or LoaderDetailPage
                  or InstanceDetailPage or ModsPage or SavesPage or ResourcePacksPage
                 or ShaderPacksPage or PresetPage) idx = 1;
        else if (page is ServerBrowserPage) idx = 2;
        else if (page is MultiplayerPage) idx = 3;
        else if (page is ModBrowserPage or ModDownloadSettingsPage) idx = 4;
        else if (page is SettingsPage) idx = 5;
        else if (page is HelpPage) idx = 6;
        else if (page is MorePage or ThemeDetailPage or AnimationSettingsPage
                  or SkinPreviewPage or SkinLibraryPage or WebsiteSitesPage) idx = 7;
        else return;

        for (var i = 0; i < NavStack.Children.Count; i++)
        {
            if (NavStack.Children[i] is RadioButton rb)
                rb.IsChecked = i == idx;
        }
    }

    private enum NavigationAnimationMode
    {
        MainSidebar,
        StandaloneSidebar
    }

    private readonly record struct TearSeamPlan(Vector Tangent, Vector Normal, IReadOnlyList<Point> Seam);

    private enum TearPieceSide { Negative, Positive }

    private static NavigationAnimationMode GetNavigationAnimationMode(Page page)
        => page is LocalVersionsPage or VersionSettingsPage or LoaderPickerPage or MorePage or ModBrowserPage
            ? NavigationAnimationMode.StandaloneSidebar
            : NavigationAnimationMode.MainSidebar;

    internal void NavigateTo(Page page)
    {
        SelectNavButton(page);

        // 进入联机大厅时，主侧栏整体换成联机专用侧栏
        var inMultiplayer = page is Views.Pages.MultiplayerPage;
        NavStack.Visibility = inMultiplayer ? Visibility.Collapsed : Visibility.Visible;
        NavStackMultiplayer.Visibility = inMultiplayer ? Visibility.Visible : Visibility.Collapsed;
        if (inMultiplayer)
        {
            MPNavLobby.IsChecked = true;
            _multiplayerPage?.ShowSection("lobby");
        }
        if (ReferenceEquals(MainFrame.Content, page) && MainFrame.IsHitTestVisible) return;
        _ = NavigateToAsync(page, ++_navigationGeneration);
    }

    private async Task NavigateToAsync(Page page, int generation)
    {
        if (MainFrame.Content is IPageTransitionAware transitionAwarePage)
            await transitionAwarePage.PrepareForNavigationExitAsync();

        ClearTearTransitionLayer();
        ClearCinematicTransitionLayer();
        MainFrame.BeginAnimation(OpacityProperty, null);
        SidebarBorder.BeginAnimation(OpacityProperty, null);
        MainFrame.Visibility = Visibility.Visible;
        MainFrame.Opacity = 1;
        MainFrame.IsHitTestVisible = false;

        var sidebarWasVisible = SidebarBorder.Visibility == Visibility.Visible;
            var targetUsesStandaloneSidebar = GetNavigationAnimationMode(page) == NavigationAnimationMode.StandaloneSidebar;
            var animationStyle = App.Settings.Data.PageAnimationStyle;
            if (animationStyle == "slide" && App.Settings.Data.AdvancedAnimationEnabled)
                animationStyle = "cinematic";
        try
        {
            if (page is BrowserPage)
            {
                NavigateToBrowserPage(page, targetUsesStandaloneSidebar);
                return;
            }
            if (animationStyle == "tear")
            {
                await RunTearTransitionAsync(page, targetUsesStandaloneSidebar, generation);
                return;
            }
            if (animationStyle == "zoom")
            {
                await RunZoomTransitionAsync(page, targetUsesStandaloneSidebar, generation);
                return;
            }
            if (animationStyle == "dissolve")
            {
                await RunDissolveTransitionAsync(page, targetUsesStandaloneSidebar, generation);
                return;
            }
            if (animationStyle == "cinematic")
            {
                await RunCinematicTransitionAsync(page, targetUsesStandaloneSidebar, generation);
                return;
            }
            if (animationStyle == "none")
            {
                MainFrame.Navigate(page);
                SidebarBorder.BeginAnimation(OpacityProperty, null);
                SidebarBorder.RenderTransform = null;
                SidebarBorder.Opacity = 1;
                SidebarBorder.Visibility = targetUsesStandaloneSidebar ? Visibility.Collapsed : Visibility.Visible;
                SidebarColumn.Width = targetUsesStandaloneSidebar ? new GridLength(0) : new GridLength(240);
                MainFrame.RenderTransform = null;
                MainFrame.Opacity = 1;
                MainFrame.IsHitTestVisible = true;
                return;
            }

            var quick = animationStyle == "quick";
            var exitDuration = quick ? 80 : 450;
            var enterDuration = quick ? 130 : 450;
            var exitDistance = quick ? 14 : 350;
            var enterDistance = quick ? 10 : 350;
            if (targetUsesStandaloneSidebar && sidebarWasVisible)
            {
                var sidebarExit = new TranslateTransform();
                SidebarBorder.RenderTransform = sidebarExit;
                sidebarExit.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, -240, TimeSpan.FromMilliseconds(exitDuration))
                    {
                        EasingFunction = quick
                            ? new CubicEase { EasingMode = EasingMode.EaseIn }
                            : new SineEase { EasingMode = EasingMode.EaseIn }
                    });
                SidebarBorder.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitDuration)));
            }

            MainFrame.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitDuration)));

            if (MainFrame.Content is IStandaloneSidebarPage standalonePage)
            {
                MainFrame.RenderTransform = null;
                await standalonePage.AnimateStandaloneExitAsync(quick);
            }
            else
            {
                var exitTransform = new TranslateTransform();
                MainFrame.RenderTransform = exitTransform;
                IEasingFunction exitEase = quick
                    ? new CubicEase { EasingMode = EasingMode.EaseIn }
                    : new SineEase { EasingMode = EasingMode.EaseIn };
                exitTransform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, exitDistance, TimeSpan.FromMilliseconds(exitDuration)) { EasingFunction = exitEase });
                await Task.Delay(exitDuration);
            }
            if (generation != _navigationGeneration) return;

            MainFrame.BeginAnimation(OpacityProperty, null);
            MainFrame.Navigate(page);

            var showSidebar = !targetUsesStandaloneSidebar;
            if (showSidebar)
            {
                SidebarColumn.Width = new GridLength(240);
                SidebarBorder.Visibility = Visibility.Visible;
                SidebarBorder.Opacity = 1;
                if (!sidebarWasVisible)
                {
                    var sidebarEnter = new TranslateTransform(-240, 0);
                    SidebarBorder.RenderTransform = sidebarEnter;
                    sidebarEnter.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(-240, 0, TimeSpan.FromMilliseconds(enterDuration))
                        {
                            EasingFunction = quick
                                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                                : new SineEase { EasingMode = EasingMode.EaseOut }
                        });
                    SidebarBorder.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(enterDuration)));
                }
                else
                {
                    SidebarBorder.RenderTransform = null;
                }
            }
            else
            {
                SidebarBorder.BeginAnimation(OpacityProperty, null);
                SidebarBorder.Visibility = Visibility.Collapsed;
                SidebarColumn.Width = new GridLength(0);
            }

            if (targetUsesStandaloneSidebar)
            {
                MainFrame.Opacity = 1;
                MainFrame.RenderTransform = null;
                MainFrame.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(enterDuration))
                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                    });
                await Task.Delay(enterDuration);
                if (generation != _navigationGeneration) return;
                MainFrame.BeginAnimation(OpacityProperty, null);
                MainFrame.Opacity = 1;
                MainFrame.IsHitTestVisible = true;
                return;
            }

            MainFrame.Opacity = 1;
            var enterTransform = new TranslateTransform();
            MainFrame.RenderTransform = enterTransform;
            IEasingFunction enterEase = quick
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new SineEase { EasingMode = EasingMode.EaseOut };
            MainFrame.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(enterDuration)) { EasingFunction = enterEase });
            enterTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(enterDistance, 0, TimeSpan.FromMilliseconds(enterDuration)) { EasingFunction = enterEase });

            await Task.Delay(enterDuration);
            if (generation != _navigationGeneration) return;
            MainFrame.BeginAnimation(OpacityProperty, null);
            SidebarBorder.BeginAnimation(OpacityProperty, null);
            MainFrame.Opacity = 1;
            MainFrame.RenderTransform = null;
            SidebarBorder.Opacity = 1;
            SidebarBorder.RenderTransform = null;
            MainFrame.IsHitTestVisible = true;
        }
        catch
        {
            ClearCinematicTransitionLayer();
            MainFrame.BeginAnimation(OpacityProperty, null);
            MainFrame.RenderTransform = null;
            MainFrame.Opacity = 1;
            SidebarBorder.BeginAnimation(OpacityProperty, null);
            SidebarBorder.RenderTransform = null;
            SidebarBorder.Opacity = 1;
            MainFrame.IsHitTestVisible = true;
        }
    }

    private void NavigateToBrowserPage(Page page, bool targetUsesStandaloneSidebar)
    {
        MainFrame.BeginAnimation(OpacityProperty, null);
        MainFrame.RenderTransform = null;
        MainFrame.Navigate(page);

        SidebarBorder.BeginAnimation(OpacityProperty, null);
        SidebarBorder.RenderTransform = null;
        SidebarBorder.Opacity = 1;
        SidebarBorder.Visibility = targetUsesStandaloneSidebar
            ? Visibility.Collapsed
            : Visibility.Visible;
        SidebarColumn.Width = targetUsesStandaloneSidebar
            ? new GridLength(0)
            : new GridLength(240);
        MainFrame.Opacity = 1;
        MainFrame.IsHitTestVisible = true;
    }

    private readonly Stack<Page> _navigationHistory = new();

    private async Task RunTearTransitionAsync(Page page, bool targetUsesStandaloneSidebar, int generation)
    {
        var settings = App.Settings.Data;
        var seamAngle = ResolveTearAngle(settings.TearRandomDirection, settings.TearDirection);

        ClearTearTransitionLayer();
        WindowRoot.UpdateLayout();
        var width = WindowRoot.ActualWidth;
        var height = WindowRoot.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            MainFrame.Navigate(page);
            ApplyNavigationLayout(targetUsesStandaloneSidebar);
            MainFrame.IsHitTestVisible = true;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(WindowRoot);
        var snapshot = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        snapshot.Render(WindowRoot);
        snapshot.Freeze();

        MainFrame.BeginAnimation(OpacityProperty, null);
        MainFrame.RenderTransform = null;
        MainFrame.Opacity = 1;
        MainFrame.Navigate(page);
        ApplyNavigationLayout(targetUsesStandaloneSidebar);
        WindowRoot.UpdateLayout();

        var seamPlan = CreateTearSeamPlan(width, height, seamAngle);
        var texturedSnapshot = CreateTexturedTearSource(snapshot, width, height);
        TearPieceA.Source = texturedSnapshot;
        TearPieceB.Source = texturedSnapshot;
        TearPieceA.Clip = CreateTearPieceClip(seamPlan, width, height, TearPieceSide.Negative);
        TearPieceB.Clip = CreateTearPieceClip(seamPlan, width, height, TearPieceSide.Positive);
        TearPieceA.RenderTransformOrigin = new Point(0.5, 0.5);
        TearPieceB.RenderTransformOrigin = new Point(0.5, 0.5);

        var rotateA = new RotateTransform();
        var rotateB = new RotateTransform();
        var moveA = new TranslateTransform();
        var moveB = new TranslateTransform();
        var transformA = new TransformGroup();
        transformA.Children.Add(rotateA);
        transformA.Children.Add(moveA);
        var transformB = new TransformGroup();
        transformB.Children.Add(rotateB);
        transformB.Children.Add(moveB);
        TearPieceA.RenderTransform = transformA;
        TearPieceB.RenderTransform = transformB;
        TearTransitionLayer.Visibility = Visibility.Visible;

        var duration = Math.Clamp(settings.TearAnimationDurationMs, 350, 1540);
        var rotation = 1.5 + _rng.NextDouble() * 1.5;
        var exitDistance = Math.Abs(width * seamPlan.Normal.X) + Math.Abs(height * seamPlan.Normal.Y) + 24;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var story = new Storyboard();

        var moveAXAnim = new DoubleAnimation(0, -seamPlan.Normal.X * exitDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease };
        var moveAYAnim = new DoubleAnimation(0, -seamPlan.Normal.Y * exitDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease };
        var moveBXAnim = new DoubleAnimation(0, seamPlan.Normal.X * exitDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease };
        var moveBYAnim = new DoubleAnimation(0, seamPlan.Normal.Y * exitDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease };
        var rotAAnim = new DoubleAnimation(0, -rotation, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease };
        var rotBAnim = new DoubleAnimation(0, rotation, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease };

        Storyboard.SetTarget(moveAXAnim, TearPieceA);
        Storyboard.SetTarget(moveAYAnim, TearPieceA);
        Storyboard.SetTarget(moveBXAnim, TearPieceB);
        Storyboard.SetTarget(moveBYAnim, TearPieceB);
        Storyboard.SetTarget(rotAAnim, TearPieceA);
        Storyboard.SetTarget(rotBAnim, TearPieceB);

        Storyboard.SetTargetProperty(moveAXAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"));
        Storyboard.SetTargetProperty(moveAYAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));
        Storyboard.SetTargetProperty(moveBXAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"));
        Storyboard.SetTargetProperty(moveBYAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));
        Storyboard.SetTargetProperty(rotAAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(RotateTransform.Angle)"));
        Storyboard.SetTargetProperty(rotBAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(RotateTransform.Angle)"));

        story.Children.Add(moveAXAnim);
        story.Children.Add(moveAYAnim);
        story.Children.Add(moveBXAnim);
        story.Children.Add(moveBYAnim);
        story.Children.Add(rotAAnim);
        story.Children.Add(rotBAnim);

        var tcs = new TaskCompletionSource();
        story.Completed += (_, _) => tcs.TrySetResult();
        story.Begin();

        await tcs.Task;
        if (generation != _navigationGeneration) return;
        ClearTearTransitionLayer();
        MainFrame.IsHitTestVisible = true;
    }

    private async Task RunZoomTransitionAsync(Page page, bool targetUsesStandaloneSidebar, int generation)
    {
        var exitDuration = 180;
        var enterDuration = 220;
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (targetUsesStandaloneSidebar)
        {
            var sidebarExit = new TranslateTransform();
            SidebarBorder.RenderTransform = sidebarExit;
            sidebarExit.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -240, TimeSpan.FromMilliseconds(exitDuration)) { EasingFunction = easeIn });
            SidebarBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitDuration)));
        }

        var exitScale = new ScaleTransform(1, 1);
        MainFrame.RenderTransform = exitScale;
        MainFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        var exitAnim = new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(exitDuration)) { EasingFunction = easeIn };
        var exitFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitDuration)) { EasingFunction = easeIn };
        exitScale.BeginAnimation(ScaleTransform.ScaleXProperty, exitAnim);
        exitScale.BeginAnimation(ScaleTransform.ScaleYProperty, exitAnim);
        MainFrame.BeginAnimation(OpacityProperty, exitFade);
        await Task.Delay(exitDuration);
        if (generation != _navigationGeneration) return;

        MainFrame.Navigate(page);
        ApplyNavigationLayout(targetUsesStandaloneSidebar);

        var enterScale = new ScaleTransform(1.08, 1.08);
        MainFrame.RenderTransform = enterScale;
        MainFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        MainFrame.Opacity = 0;
        var enterAnim = new DoubleAnimation(1.08, 1, TimeSpan.FromMilliseconds(enterDuration)) { EasingFunction = easeOut };
        var enterFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(enterDuration)) { EasingFunction = easeOut };
        enterScale.BeginAnimation(ScaleTransform.ScaleXProperty, enterAnim);
        enterScale.BeginAnimation(ScaleTransform.ScaleYProperty, enterAnim);
        MainFrame.BeginAnimation(OpacityProperty, enterFade);
        await Task.Delay(enterDuration);
        if (generation != _navigationGeneration) return;

        MainFrame.BeginAnimation(OpacityProperty, null);
        MainFrame.RenderTransform = null;
        MainFrame.Opacity = 1;
        SidebarBorder.BeginAnimation(OpacityProperty, null);
        SidebarBorder.RenderTransform = null;
        SidebarBorder.Opacity = 1;
        MainFrame.IsHitTestVisible = true;
    }

    private async Task RunCinematicTransitionAsync(Page page, bool targetUsesStandaloneSidebar, int generation)
    {
        WindowRoot.UpdateLayout();
        var width = WindowRoot.ActualWidth;
        var height = WindowRoot.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            MainFrame.Navigate(page);
            ApplyNavigationLayout(targetUsesStandaloneSidebar);
            MainFrame.IsHitTestVisible = true;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(WindowRoot);
        var snapshot = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        snapshot.Render(WindowRoot);
        snapshot.Freeze();

        var bands = new[]
        {
            (0.00, 0.25, -44.0, -18.0, -1.20),
            (0.25, 0.50, 30.0, -8.0, 0.85),
            (0.50, 0.75, -26.0, 16.0, -0.75),
            (0.75, 1.00, 48.0, 12.0, 1.10)
        };

        ClearCinematicTransitionLayer();
        foreach (var band in bands)
        {
            var clip = new Rect(0, height * band.Item1, width, height * (band.Item2 - band.Item1));
            var piece = new Image
            {
                Source = snapshot,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                Clip = new RectangleGeometry(clip),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup()
            };
            var group = (TransformGroup)piece.RenderTransform;
            group.Children.Add(new RotateTransform());
            group.Children.Add(new TranslateTransform());
            CinematicPieces.Children.Add(piece);

            var rotate = (RotateTransform)group.Children[0];
            var move = (TranslateTransform)group.Children[1];
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, band.Item5, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, band.Item3, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, band.Item4, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            piece.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }

        CinematicTransitionLayer.Visibility = Visibility.Visible;
        CinematicGlow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.68, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        CinematicSweep.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.72, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });

        var oldFrameTransform = new TransformGroup();
        var oldScale = new ScaleTransform(1, 1);
        var oldMove = new TranslateTransform();
        oldFrameTransform.Children.Add(oldScale);
        oldFrameTransform.Children.Add(oldMove);
        MainFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        MainFrame.RenderTransform = oldFrameTransform;
        oldScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        oldScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        oldMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, -18, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        MainFrame.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.18, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

        await Task.Delay(300);
        if (generation != _navigationGeneration)
        {
            ClearCinematicTransitionLayer();
            return;
        }

        MainFrame.BeginAnimation(OpacityProperty, null);
        MainFrame.Navigate(page);
        ApplyNavigationLayout(targetUsesStandaloneSidebar);
        MainFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        var newFrameTransform = new TransformGroup();
        var newScale = new ScaleTransform(1.045, 1.045);
        var newMove = new TranslateTransform(34, 0);
        newFrameTransform.Children.Add(newScale);
        newFrameTransform.Children.Add(newMove);
        MainFrame.RenderTransform = newFrameTransform;
        MainFrame.Opacity = 0;

        if (!targetUsesStandaloneSidebar)
        {
            SidebarBorder.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(240);
            SidebarBorder.Opacity = 0;
            var sidebarTransform = new TranslateTransform(-32, 0);
            SidebarBorder.RenderTransform = sidebarTransform;
            SidebarBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            sidebarTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-32, 0, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        var enterDuration = TimeSpan.FromMilliseconds(460);
        var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        newScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.045, 1, enterDuration) { EasingFunction = enterEase });
        newScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.045, 1, enterDuration) { EasingFunction = enterEase });
        newMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(34, 0, enterDuration) { EasingFunction = enterEase });
        MainFrame.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, enterDuration) { EasingFunction = enterEase });

        var sweepTransform = (TranslateTransform)CinematicSweep.RenderTransform;
        sweepTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-260, width + 260, enterDuration)
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        CinematicGlow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.68, 0, enterDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        CinematicSweep.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.72, 0, enterDuration)
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });

        await Task.Delay(enterDuration);
        if (generation != _navigationGeneration) return;
        ClearCinematicTransitionLayer();
        MainFrame.BeginAnimation(OpacityProperty, null);
        MainFrame.RenderTransform = null;
        MainFrame.Opacity = 1;
        SidebarBorder.BeginAnimation(OpacityProperty, null);
        SidebarBorder.RenderTransform = null;
        SidebarBorder.Opacity = 1;
        SidebarBorder.Visibility = targetUsesStandaloneSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = targetUsesStandaloneSidebar ? new GridLength(0) : new GridLength(240);
        MainFrame.IsHitTestVisible = true;
    }

    private async Task RunDissolveTransitionAsync(Page page, bool targetUsesStandaloneSidebar, int generation)
    {
        var exitDuration = 220;
        var enterDuration = 260;
        var easeIn = new SineEase { EasingMode = EasingMode.EaseIn };
        var easeOut = new SineEase { EasingMode = EasingMode.EaseOut };

        var oldContent = MainFrame.Content as Page;
        var oldWasStandalone = oldContent != null && GetNavigationAnimationMode(oldContent) == NavigationAnimationMode.StandaloneSidebar;

        if (oldContent != null && oldContent != page)
            _navigationHistory.Push(oldContent);

        var sidebarNeedHide = !oldWasStandalone && targetUsesStandaloneSidebar && SidebarBorder.Visibility == Visibility.Visible;
        var sidebarNeedShow = !targetUsesStandaloneSidebar;

        if (sidebarNeedHide)
        {
            SidebarBorder.BeginAnimation(OpacityProperty, null);
            var sidebarFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitDuration)) { EasingFunction = easeIn };
            SidebarBorder.BeginAnimation(OpacityProperty, sidebarFadeOut);
        }

        var exitFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(exitDuration)) { EasingFunction = easeIn };
        MainFrame.BeginAnimation(OpacityProperty, exitFade);

        await Task.Delay(exitDuration);
        if (generation != _navigationGeneration) return;

        if (sidebarNeedHide)
        {
            SidebarBorder.BeginAnimation(OpacityProperty, null);
            SidebarBorder.Visibility = Visibility.Collapsed;
            SidebarBorder.Opacity = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        MainFrame.Navigate(page);

        if (sidebarNeedShow)
        {
            SidebarBorder.BeginAnimation(OpacityProperty, null);
            SidebarBorder.RenderTransform = null;
            SidebarBorder.Visibility = Visibility.Visible;
            SidebarBorder.Opacity = 0;
            SidebarColumn.Width = new GridLength(240);
        }
        else
        {
            SidebarBorder.BeginAnimation(OpacityProperty, null);
            SidebarBorder.RenderTransform = null;
            SidebarBorder.Visibility = Visibility.Collapsed;
            SidebarBorder.Opacity = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        MainFrame.Opacity = 0;
        MainFrame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(enterDuration)) { EasingFunction = easeOut });

        if (sidebarNeedShow)
        {
            SidebarBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(enterDuration)) { EasingFunction = easeOut });
        }

        await Task.Delay(enterDuration);
        if (generation != _navigationGeneration) return;

        MainFrame.BeginAnimation(OpacityProperty, null);
        MainFrame.RenderTransform = null;
        MainFrame.Opacity = 1;
        SidebarBorder.BeginAnimation(OpacityProperty, null);
        SidebarBorder.RenderTransform = null;
        SidebarBorder.Opacity = 1;
        SidebarBorder.Visibility = targetUsesStandaloneSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = targetUsesStandaloneSidebar ? new GridLength(0) : new GridLength(240);
        MainFrame.IsHitTestVisible = true;
    }

    private void ApplyNavigationLayout(bool standalone)
    {
        SidebarBorder.BeginAnimation(OpacityProperty, null);
        SidebarBorder.RenderTransform = null;
        SidebarBorder.Opacity = 1;
        SidebarBorder.Visibility = standalone ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = standalone ? new GridLength(0) : new GridLength(240);
    }

    private double ResolveTearAngle(bool randomDirection, string fixedDirection)
    {
        if (randomDirection) return _rng.NextDouble() * Math.PI * 2;

        return string.Equals(fixedDirection, "horizontal", StringComparison.OrdinalIgnoreCase)
            ? 0
            : Math.PI * 1.5;
    }

    private TearSeamPlan CreateTearSeamPlan(double width, double height, double angle)
    {
        var tangent = new Vector(Math.Cos(angle), Math.Sin(angle));
        tangent.Normalize();
        var normal = new Vector(-tangent.Y, tangent.X);
        var corners = new[]
        {
            new Point(0, 0),
            new Point(width, 0),
            new Point(width, height),
            new Point(0, height)
        };
        var minT = corners.Min(point => Dot(point, tangent));
        var maxT = corners.Max(point => Dot(point, tangent));
        var minN = corners.Min(point => Dot(point, normal));
        var maxN = corners.Max(point => Dot(point, normal));
        var length = maxT - minT;
        var crossLength = maxN - minN;
        var sharpness = Math.Clamp(App.Settings.Data.TearApexSharpness, 10, 90) / 100.0;
        var notchAmplitude = 3 + sharpness * 17;
        var microAmplitude = 0.6 + sharpness * 3.4;
        var microProbability = 0.04 + sharpness * 0.22;
        var center = minN + crossLength * (0.42 + _rng.NextDouble() * 0.16);
        var driftAmplitude = 2 + _rng.NextDouble() * 5;
        var driftPhase = _rng.NextDouble() * Math.PI * 2;
        var driftCycles = 0.65 + _rng.NextDouble() * 0.85;
        var seam = new List<Point>();
        var margin = Math.Min(2.0, crossLength / 4);
        var startT = minT - 2;
        var endT = maxT + 2;
        var along = startT;
        while (along < endT)
        {
            var normalized = Math.Clamp((along - minT) / Math.Max(1, length), 0, 1);
            var drift = Math.Sin(driftPhase + normalized * Math.PI * 2 * driftCycles) * driftAmplitude;
            var notch = (_rng.NextDouble() * 2 - 1) * notchAmplitude * Math.Pow(_rng.NextDouble(), 0.7);
            var cross = Math.Clamp(center + drift + notch, minN + margin, maxN - margin);
            seam.Add(ProjectPoint(tangent, normal, along, cross));

            var spacing = 17 + _rng.NextDouble() * 29;
            if (_rng.NextDouble() < microProbability && along + spacing < endT)
            {
                var microAlong = along + 1 + _rng.NextDouble() * 3;
                var microCross = Math.Clamp(cross + (_rng.NextDouble() * 2 - 1) * microAmplitude,
                    minN + margin, maxN - margin);
                seam.Add(ProjectPoint(tangent, normal, microAlong, microCross));
            }
            along = Math.Min(endT, along + spacing);
        }
        if (Dot(seam[^1], tangent) < endT)
        {
            seam.Add(ProjectPoint(tangent, normal, endT, Dot(seam[^1], normal)));
        }
        return new TearSeamPlan(tangent, normal, seam);
    }

    private ImageSource CreateTexturedTearSource(BitmapSource snapshot, double width, double height)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawImage(snapshot, new Rect(0, 0, width, height));
            var area = width * height;
            var fiberCount = Math.Clamp((int)(area / 12000), 30, 120);
            var lightPen = new Pen(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)), 0.5);
            lightPen.Freeze();
            for (var i = 0; i < fiberCount; i++)
            {
                var start = new Point(_rng.NextDouble() * width, _rng.NextDouble() * height);
                var fiberLength = 2 + _rng.NextDouble() * 8;
                var angle = _rng.NextDouble() * Math.PI;
                var end = new Point(
                    Math.Clamp(start.X + Math.Cos(angle) * fiberLength, 0, width),
                    Math.Clamp(start.Y + Math.Sin(angle) * fiberLength, 0, height));
                context.DrawLine(lightPen, start, end);
            }
        }
        drawing.Freeze();
        var source = new DrawingImage(drawing);
        source.Freeze();
        return source;
    }

    private static Geometry CreateTearPieceClip(TearSeamPlan seamPlan, double width, double height, TearPieceSide side)
    {
        const double overlap = 0.375;
        var far = Math.Sqrt(width * width + height * height) + 8;
        var overlapVector = seamPlan.Normal * (side == TearPieceSide.Negative ? overlap : -overlap);
        var farVector = seamPlan.Normal * (side == TearPieceSide.Negative ? -far : far);
        var edge = seamPlan.Seam.Select(point => point + overlapVector).ToList();
        if (side == TearPieceSide.Positive) edge.Reverse();
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(edge[0], true, true);
            for (var i = 1; i < edge.Count; i++) context.LineTo(edge[i], true, false);
            context.LineTo(edge[^1] + farVector, true, false);
            context.LineTo(edge[0] + farVector, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double Dot(Point point, Vector vector)
        => point.X * vector.X + point.Y * vector.Y;

    private static Point ProjectPoint(Vector tangent, Vector normal, double t, double n)
        => new(tangent.X * t + normal.X * n, tangent.Y * t + normal.Y * n);

    private void ClearTearTransitionLayer()
    {
        TearTransitionLayer.Visibility = Visibility.Collapsed;
        TearPieceA.Source = null;
        TearPieceB.Source = null;
        TearPieceA.Clip = null;
        TearPieceB.Clip = null;
        TearPieceA.RenderTransform = null;
        TearPieceB.RenderTransform = null;
    }

    private void ClearCinematicTransitionLayer()
    {
        CinematicTransitionLayer.Visibility = Visibility.Collapsed;
        CinematicPieces.Children.Clear();
        CinematicGlow.BeginAnimation(OpacityProperty, null);
        CinematicSweep.BeginAnimation(OpacityProperty, null);
        if (CinematicSweep.RenderTransform is TranslateTransform sweepTransform)
        {
            sweepTransform.BeginAnimation(TranslateTransform.XProperty, null);
            sweepTransform.X = -260;
        }
        CinematicGlow.Opacity = 0;
        CinematicSweep.Opacity = 0;
    }
}
