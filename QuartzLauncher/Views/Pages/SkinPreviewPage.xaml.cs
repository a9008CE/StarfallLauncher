using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class SkinPreviewPage : Page
{
    private readonly Model3DGroup _playerModel = new();
    private readonly AxisAngleRotation3D _rotation = new(new Vector3D(0, 1, 0), -20);
    private readonly AxisAngleRotation3D _viewPitchRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _bodyRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _headRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _headPitchRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _leftArmRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _rightArmRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _leftArmZRotation = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _rightArmZRotation = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _leftLegRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _rightLegRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _leftLegYRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _rightLegYRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly DispatcherTimer _walkTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // 「邦友的酒」彩蛋：整体压扁 + 坐下时下沉（2026-11-08）
    private readonly ScaleTransform3D _squash = new(1, 1, 1);
    private readonly TranslateTransform3D _sitOffset = new(0, 0, 0);
    private bool _wineSitting;
    private Stopwatch? _wineClock;      // 用渲染帧当时间基准，避免 Task.Delay 与渲染节拍错位（残影）
    private bool _winePlaying;
    private double _wineYaw0;
    private const double WineTotalSeconds = 300.0;   // 保险上限；实际时长以音频为准
    private const double SitDrop = -1.2;             // 坐下时整体下沉（-5 → -2.5 → -1.2，越往上越贴地少）
    private readonly MediaPlayer _winePlayer = new();
    private double _wineAudioSeconds;
    private bool _winePlayerReady;
    private bool _wineWantPlay;
    private const double WineBpm = 120.0;   // 4/4 拍：0.5s 一拍，2s 一小节
    private bool _dragging;
    private Point _lastMouse;
    private double _walkTime;
    private double _targetYaw = -20;
    private double _targetPitch;
    private double _targetHeadYaw;
    private double _targetHeadPitch;
    private MotionMode _motionMode = MotionMode.Walk;
    private bool _poseActive;
    private bool _isAlexModel;
    private Window? _hostWindow;

    public SkinPreviewPage()
    {
        InitializeComponent();
        var playerTransform = new Transform3DGroup();
        playerTransform.Children.Add(_squash);
        playerTransform.Children.Add(_sitOffset);
        playerTransform.Children.Add(new RotateTransform3D(_bodyRotation, new Point3D(0, 14, 0)));
        playerTransform.Children.Add(new RotateTransform3D(_viewPitchRotation, new Point3D(0, 14, 0)));
        playerTransform.Children.Add(new RotateTransform3D(_rotation, new Point3D(0, 14, 0)));
        _playerModel.Transform = playerTransform;
        SceneRoot.Children.Add(_playerModel);
        _walkTimer.Tick += WalkTimer_Tick;
        _winePlayer.MediaOpened += (_, _) =>
        {
            if (_winePlayer.NaturalDuration.HasTimeSpan)
                _wineAudioSeconds = _winePlayer.NaturalDuration.TimeSpan.TotalSeconds;
            if (_wineWantPlay)
            {
                try { _winePlayer.Position = TimeSpan.Zero; _winePlayer.Play(); } catch { }
            }
        };
        _winePlayer.MediaEnded += (_, _) => OnWineAudioEnded();
        Loaded += (_, _) =>
        {
            LoadCurrentSkin();
            BeginEnterAnimation();
            CompositionTarget.Rendering += ViewRendering;
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
                _hostWindow.PreviewMouseMove += HostWindow_MouseMove;
        };
        Unloaded += (_, _) =>
        {
            _walkTimer.Stop();
            CompositionTarget.Rendering -= ViewRendering;
            if (_hostWindow != null)
                _hostWindow.PreviewMouseMove -= HostWindow_MouseMove;
            _hostWindow = null;
        };
    }

    private void ViewRendering(object? sender, EventArgs e)
    {
        if (_winePlaying && _wineClock != null)
            TickWine(_wineClock.Elapsed.TotalSeconds);

        _rotation.Angle += (_targetYaw - _rotation.Angle) * 0.18;
        _viewPitchRotation.Angle += (_targetPitch - _viewPitchRotation.Angle) * 0.18;
        if (!_poseActive)
        {
            _headRotation.Angle += (_targetHeadYaw - _headRotation.Angle) * 0.16;
            _headPitchRotation.Angle += (_targetHeadPitch - _headPitchRotation.Angle) * 0.16;
        }
    }

    private void BeginEnterAnimation()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        var transform = new TranslateTransform(24, 0);
        RenderTransform = transform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private async Task NavigateWithAnimationAsync(object page)
    {
        IsHitTestVisible = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        var transform = new TranslateTransform();
        RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(110)));
        await Task.Delay(110);
        NavigationService?.Navigate(page);
    }

    private void HostWindow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_poseActive || _hostWindow == null || _hostWindow.ActualWidth <= 0 || _hostWindow.ActualHeight <= 0)
            return;

        var position = e.GetPosition(_hostWindow);
        var horizontal = position.X / _hostWindow.ActualWidth * 2 - 1;
        var vertical = position.Y / _hostWindow.ActualHeight * 2 - 1;
        _targetHeadYaw = Math.Clamp(horizontal * 28, -28, 28);
        _targetHeadPitch = Math.Clamp(vertical * 18, -18, 18);
    }

    private void LoadCurrentSkin()
    {
        if (App.Settings.Data.AuthMode != AuthModes.Offline)
        {
            _playerModel.Children.Clear();
            SkinImage.Source = null;
            SkinPathText.Text = App.Settings.Data.AuthMode == AuthModes.Microsoft
                ? "正版登录：使用 Mojang 账户皮肤"
                : "第三方登录：使用认证服务器皮肤";
            EmptyHint.Text = "当前登录模式使用认证服务器皮肤";
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        var path = App.Settings.Data.CustomSkinPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            LoadSkin(path);
            return;
        }

        _playerModel.Children.Clear();
        SkinImage.Source = null;
        SkinPathText.Text = "未导入皮肤";
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureOfflineSkinMode()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Minecraft skin (*.png)|*.png|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var bitmap = LoadBitmap(dialog.FileName);
            if (bitmap.PixelWidth != 64 || bitmap.PixelHeight is not (64 or 32))
            {
                AnimatedMessageBox.Show("皮肤文件必须是 64x64 或 64x32 的 PNG。", "皮肤导入", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            App.Settings.Data.CustomSkinPath = StoreSkin(dialog.FileName);
            App.Settings.Save();
            LoadSkin(App.Settings.Data.CustomSkinPath);
        }
        catch (Exception ex)
        {
            AnimatedMessageBox.Show($"皮肤导入失败: {ex.Message}", "皮肤导入", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UseAsAvatar_Click(object sender, RoutedEventArgs e)
    {
        var path = App.Settings.Data.CustomSkinPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AnimatedMessageBox.Show("请先导入皮肤。", "皮肤/预览", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        App.Settings.Data.AuthAvatarPath = path;
        App.Settings.Save();
        AnimatedMessageBox.Show("已设为启动器头像。", "皮肤/预览", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UseCurrentSkin_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureOfflineSkinMode()) return;

        var path = App.Settings.Data.CustomSkinPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AnimatedMessageBox.Show("请先导入皮肤。", "皮肤/预览", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        App.Settings.Save();
        AnimatedMessageBox.Show("已使用当前皮肤。", "皮肤/预览", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SkinLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureOfflineSkinMode()) return;

        var frame = FindMoreFrame();
        if (frame != null)
        {
            frame.BeginAnimation(OpacityProperty, null);
            frame.Opacity = 1;
            frame.RenderTransform = null;
            frame.IsHitTestVisible = true;
            frame.Navigate(new SkinLibraryPage());
        }
    }

    private static bool EnsureOfflineSkinMode()
    {
        if (App.Settings.Data.AuthMode == AuthModes.Offline) return true;
        AnimatedMessageBox.Show("皮肤库和本地皮肤只适用于离线登录。当前登录模式将使用认证服务器提供的皮肤。",
            "皮肤模式", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private Frame? FindMoreFrame()
    {
        var parent = VisualTreeHelper.GetParent(this);
        while (parent != null)
        {
            if (parent is Frame f && f.Name == "MoreFrame") return f;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static string StoreSkin(string sourcePath)
    {
        var skinDir = Path.Combine(AppContext.BaseDirectory, "Launcher", "skins");
        Directory.CreateDirectory(skinDir);
        var fullSource = Path.GetFullPath(sourcePath);
        if (string.Equals(Path.GetDirectoryName(fullSource), skinDir, StringComparison.OrdinalIgnoreCase))
            return fullSource;

        var name = Path.GetFileNameWithoutExtension(fullSource);
        var extension = Path.GetExtension(fullSource);
        var target = Path.Combine(skinDir, name + extension);
        var index = 2;
        while (File.Exists(target))
            target = Path.Combine(skinDir, $"{name}{index++}{extension}");
        File.Copy(fullSource, target);
        return target;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Data.CustomSkinPath = "";
        App.Settings.Save();
        // 同上：撤掉服务器上那份，别让好友继续看到旧头像
        AccountService.Current?.QueueSkinRefresh();
        LoadCurrentSkin();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        _winePlaying = false;
        _wineClock = null;
        if (_wineSitting) WineBtn.Content = "邦友的酒";
        _wineSitting = false;
        SetSquash(1, 1);
        _sitOffset.OffsetY = 0;
        if (sender is not Button { Tag: string name }) return;
        if (name == "默认")
        {
            _walkTimer.Stop();
            _poseActive = false;
            _ = BlendPoseAsync(PoseState.Zero, 0.30);
            return;
        }

        var mode = name switch
        {
            "走路" => MotionMode.Walk,
            "跑步" => MotionMode.Run,
            "游泳" => MotionMode.Swim,
            "坐下" => MotionMode.Sit,
            _ => (MotionMode?)null
        };
        if (mode != null)
        {
            ToggleMotion(mode.Value);
            return;
        }

        AnimatedMessageBox.Show($"{name} 功能待配置。", "皮肤预设", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 「邦友的酒」：先坐下，然后按 4/4 拍一边转圈一边 Q 弹，持续 5 分钟。
    /// 动画由 CompositionTarget.Rendering 驱动（每帧一次），不再用 Task.Delay，
    /// 避免变换被改两次导致旋转时出现残影/抽搐。
    /// </summary>
    private void BangyouWine_Click(object sender, RoutedEventArgs e)
    {
        // 正在跑 → 停下并起身
        if (_winePlaying)
        {
            StopWineAndStandUp();
            return;
        }

        // 已经坐下了（放完 5 分钟）→ 点一下起身
        if (_wineSitting)
        {
            WineStandUp();
            return;
        }

        // 音频已在安装包里，直接释放到缓存后播放
        var withAudio = EnsureWineAudioFile();

        // ② 开始动作（有音频就同时起）
        StartWine(withAudio);
    }

    private void StartWine(bool withAudio)
    {
        _walkTimer.Stop();
        ResetPose();
        _poseActive = true;
        _motionMode = MotionMode.Sit;

        // 关掉容器上的位图缓存：缓存会把每帧的模型"定格"，旋转时就是残影的来源之一
        if (SkinViewport.Parent is FrameworkElement host)
            host.CacheMode = null;

        _wineYaw0 = _rotation.Angle;
        _wineClock = Stopwatch.StartNew();
        _winePlaying = true;
        _wineAudioSeconds = 0;

        if (withAudio) PlayWineAudio();

        WineBtn.Content = "旋转中 · " + FormatLeft(WineLength());
    }

    /// <summary>每帧推进（由 ViewRendering 调用）。</summary>
    private void TickWine(double t)
    {
        if (!_winePlaying || _wineClock == null) return;

        if (t < 0.45)
        {
            // ① 坐下
            var e = EaseOutBack(t / 0.45);
            _sitOffset.OffsetY = SitDrop * e;
            _leftLegRotation.Angle = -81 * e;
            _rightLegRotation.Angle = -81 * e;
            _leftLegYRotation.Angle = -18 * e;
            _rightLegYRotation.Angle = 18 * e;
            _leftArmRotation.Angle = -36 * e;
            _rightArmRotation.Angle = -36 * e;
            SetSquash(1 + 0.06 * Math.Sin(t / 0.45 * Math.PI), 1);
            return;
        }

        if (t >= WineLength())
        {
            _winePlaying = false;
            SetSquash(1, 1);
            _sitOffset.OffsetY = SitDrop;
            _leftArmRotation.Angle = -36;
            _rightArmRotation.Angle = -36;
            _headRotation.Angle = 0;
            _wineSitting = true;
            WineBtn.Content = "邦友的酒 · 起身";
            return;
        }

        var tt = t - 0.45;

        // ② 匀速转圈（约 2.4 秒一圈）+ 拍点上给一点角速度脉冲
        var beat = 60.0 / WineBpm;          // 0.5s
        var bar = beat * 4;                 // 2.0s
        var phase = tt % bar;
        var inBeat = phase % beat;                     // 0..0.5s
        // 匀速旋转，不加拍点脉冲（否则每拍转速不一样，看起来忽快忽慢）
        _rotation.Angle = _wineYaw0 + tt * 150.0;

        // ③ 4/4 拍 Q 弹：每一拍完全一样（同幅度、同曲线、同时长、无呼吸叠加）
        const double amp = 0.42;
        var shape = BeatShape(inBeat / beat);          // 0→1→略负（回弹过冲）
        var s = 1 - amp * shape;
        SetSquash(s, 1 / Math.Sqrt(Math.Max(0.5, s)));

        // 身体轻颠跟同一条曲线，每拍一致
        _sitOffset.OffsetY = SitDrop - 0.35 * shape;
        var swing = shape * 8;
        _leftArmRotation.Angle = -36 + swing;
        _rightArmRotation.Angle = -36 - swing;
        _headRotation.Angle = 3.5 * shape;

        WineBtn.Content = "旋转中 · " + FormatLeft(WineLength() - t);
    }

    private void StopWineAndStandUp()
    {
        StopWineAudio();
        _winePlaying = false;
        _wineClock = null;
        SetSquash(1, 1);
        WineBtn.Content = "邦友的酒";
        WineStandUp();
    }

    /// <summary>点一下就把彩蛋完全复位（起身用）。</summary>
    private async void WineStandUp()
    {
        try
        {
            await AnimateAsync(0.30, p =>
            {
                var e = EaseOutBack(p);
                _sitOffset.OffsetY = SitDrop * (1 - e);
                _leftLegRotation.Angle = -81 * (1 - e);
                _rightLegRotation.Angle = -81 * (1 - e);
                _leftLegYRotation.Angle = -18 * (1 - e);
                _rightLegYRotation.Angle = 18 * (1 - e);
                _leftArmRotation.Angle = -36 * (1 - e);
                _rightArmRotation.Angle = -36 * (1 - e);
                _headRotation.Angle = 0;
            }, CancellationToken.None);
            _wineSitting = false;
            _winePlaying = false;
            _wineClock = null;
            WineBtn.Content = "邦友的酒";
        }
        catch { }
    }

    /// <summary>把内嵌的 mp3 释放到临时目录并交给 MediaPlayer（WPF 不能直接播 pack:// 资源）。</summary>
    private static string WineAudioPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Launcher", "audio");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "bangyouwine.mp3");
    }

    private static bool WineAudioCached()
    {
        try
        {
            var p = WineAudioPath();
            return File.Exists(p) && new FileInfo(p).Length > 100_000;
        }
        catch { return false; }
    }

    /// <summary>把内嵌资源里的 mp3 释放到 Launcherudio 下（只做一次）。</summary>
    private bool EnsureWineAudioFile()
    {
        var target = WineAudioPath();
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length > 100_000) return true;

            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/bangyouwine.mp3"));
            if (res == null) return false;
            using (var src = res.Stream)
            using (var dst = File.Create(target))
                src.CopyTo(dst);
            return new FileInfo(target).Length > 100_000;
        }
        catch { return false; }
    }

    /// <summary>把缓存里的音频交给播放器。</summary>
    private bool OpenWineAudio()
    {
        try
        {
            var path = WineAudioPath();
            if (!File.Exists(path)) return false;
            _winePlayer.Open(new Uri(path));
            _winePlayerReady = true;
            return true;
        }
        catch { return false; }
    }

    private void PlayWineAudio()
    {
        try
        {
            if (!_winePlayerReady && !OpenWineAudio()) return;
            _wineWantPlay = true;
            _winePlayer.Stop();
            _winePlayer.Position = TimeSpan.Zero;
            _winePlayer.Play();
        }
        catch { }
    }

    private void StopWineAudio()
    {
        _wineWantPlay = false;
        try { _winePlayer.Stop(); } catch { }
    }

    private void OnWineAudioEnded()
    {
        // 音频播完 → 动作一起停（保持坐姿，不自动起身）
        if (!_winePlaying) return;
        _winePlaying = false;
        _wineClock = null;
        SetSquash(1, 1);
        _sitOffset.OffsetY = SitDrop;
        _leftArmRotation.Angle = -36;
        _rightArmRotation.Angle = -36;
        _headRotation.Angle = 0;
        _wineSitting = true;
        WineBtn.Content = "邦友的酒 · 起身";
    }

    /// <summary>本次动画时长：以音频长度为准，没有音频就退回 5 分钟。</summary>
    private double WineLength()
    {
        if (_wineAudioSeconds > 1) return Math.Min(WineTotalSeconds, _wineAudioSeconds);
        return WineTotalSeconds;
    }

    private static string FormatLeft(double seconds)
    {
        var left = (int)Math.Ceiling(Math.Max(0, seconds));
        return (left / 60) + ":" + (left % 60).ToString("00");
    }

    private void SetSquash(double y, double xz)
    {
        _squash.ScaleY = Math.Clamp(y, 0.45, 1.45);
        _squash.ScaleX = _squash.ScaleZ = Math.Clamp(xz, 0.75, 1.45);
    }

    /// <summary>
    /// 一拍内的压缩曲线（x: 0→1）。
    /// 前 30% 平滑压下去，后 70% 平滑弹回，末尾带一点点过冲，
    /// 全程 sin² 型缓动，没有瞬间位移，所以看起来是"软"的。
    /// </summary>
    private static double BeatShape(double x)
    {
        x = Math.Clamp(x, 0, 0.999);
        const double down = 0.45;   // 下压/回弹几乎各占一半，节奏更平均
        double v;
        if (x < down)
        {
            var u = x / down;
            v = u * u * (3 - 2 * u);            // smoothstep 压下去
        }
        else
        {
            var u = (x - down) / (1 - down);
            v = 1 - u * u * (3 - 2 * u);        // smoothstep 弹回来
            // 回弹过冲：越过 0 一点再收住
            v -= 0.14 * Math.Exp(-5.0 * u) * Math.Sin(Math.PI * u);
        }
        return v;
    }

    private async Task AnimateAsync(double seconds, Action<double> frame, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var p = Math.Min(1.0, sw.Elapsed.TotalSeconds / seconds);
            frame(p);
            if (p >= 1.0) break;
            await Task.Delay(16);
        }
    }

    private static double EaseOutBack(double p)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(p - 1, 3) + c1 * Math.Pow(p - 1, 2);
    }

    /// <summary>
    /// 切换动作：先把当前姿势平滑过渡到目标姿势（0.3s），再启动循环动作，
    /// 避免瞬间跳变（和「邦友的酒」的过渡手感一致）。
    /// </summary>
    private async void ToggleMotion(MotionMode mode)
    {
        if (_poseActive && _motionMode == mode)
        {
            _walkTimer.Stop();
            await BlendPoseAsync(PoseState.Zero, 0.30);
            _poseActive = false;
            return;
        }

        _walkTimer.Stop();
        _winePlaying = false;
        _wineClock = null;
        SetSquash(1, 1);

        var target = mode switch
        {
            MotionMode.Sit => PoseState.Sitting,
            MotionMode.Swim => PoseState.Swimming,
            _ => PoseState.Zero
        };

        await BlendPoseAsync(target, 0.30);

        _motionMode = mode;
        _walkTime = 0;
        _poseActive = true;
        if (mode != MotionMode.Sit) _walkTimer.Start();
    }

    /// <summary>姿势快照：用于两个动作之间做线性混合。</summary>
    private readonly record struct PoseState(
        double Body, double Head, double HeadPitch,
        double ArmL, double ArmR, double ArmZL, double ArmZR,
        double LegL, double LegR, double LegYL, double LegYR, double SitY)
    {
        public static readonly PoseState Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>坐下：腿折起、手臂略前、整体下沉 SitDrop。</summary>
        public static readonly PoseState Sitting =
            new(0, 0, 0, -36, -36, 0, 0, -81, -81, -18, 18, SitDrop);

        /// <summary>游泳起始姿势（对应 _walkTime = 0 的那一帧）。</summary>
        public static readonly PoseState Swimming =
            new(90, 0, 0, 0, 0, 180, 180, 17.2, -17.2, 0, 0, 0);
    }

    private PoseState CapturePose() => new(
        _bodyRotation.Angle, _headRotation.Angle, _headPitchRotation.Angle,
        _leftArmRotation.Angle, _rightArmRotation.Angle,
        _leftArmZRotation.Angle, _rightArmZRotation.Angle,
        _leftLegRotation.Angle, _rightLegRotation.Angle,
        _leftLegYRotation.Angle, _rightLegYRotation.Angle,
        _sitOffset.OffsetY);

    private void ApplyPose(PoseState s)
    {
        _bodyRotation.Angle = s.Body;
        _headRotation.Angle = s.Head;
        _headPitchRotation.Angle = s.HeadPitch;
        _leftArmRotation.Angle = s.ArmL;
        _rightArmRotation.Angle = s.ArmR;
        _leftArmZRotation.Angle = s.ArmZL;
        _rightArmZRotation.Angle = s.ArmZR;
        _leftLegRotation.Angle = s.LegL;
        _rightLegRotation.Angle = s.LegR;
        _leftLegYRotation.Angle = s.LegYL;
        _rightLegYRotation.Angle = s.LegYR;
        _sitOffset.OffsetY = s.SitY;
    }

    private static PoseState LerpPose(PoseState a, PoseState b, double t) => new(
        a.Body + (b.Body - a.Body) * t,
        a.Head + (b.Head - a.Head) * t,
        a.HeadPitch + (b.HeadPitch - a.HeadPitch) * t,
        a.ArmL + (b.ArmL - a.ArmL) * t,
        a.ArmR + (b.ArmR - a.ArmR) * t,
        a.ArmZL + (b.ArmZL - a.ArmZL) * t,
        a.ArmZR + (b.ArmZR - a.ArmZR) * t,
        a.LegL + (b.LegL - a.LegL) * t,
        a.LegR + (b.LegR - a.LegR) * t,
        a.LegYL + (b.LegYL - a.LegYL) * t,
        a.LegYR + (b.LegYR - a.LegYR) * t,
        a.SitY + (b.SitY - a.SitY) * t);

    private async Task BlendPoseAsync(PoseState target, double seconds)
    {
        var from = CapturePose();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var p = Math.Min(1.0, sw.Elapsed.TotalSeconds / seconds);
            var e = p * p * (3 - 2 * p);       // smoothstep，两端都缓
            ApplyPose(LerpPose(from, target, e));
            if (p >= 1.0) break;
            await Task.Delay(16);
        }
    }

    private void WalkTimer_Tick(object? sender, EventArgs e)
    {
        _walkTime += _motionMode switch
        {
            MotionMode.Run => 0.15,
            MotionMode.Swim => 0.32,
            _ => 0.085
        };

        if (_motionMode == MotionMode.Swim)
        {
            ApplyVanillaSwimPose();
            return;
        }

        var wave = Math.Sin(_walkTime);
        var swing = _motionMode == MotionMode.Run ? wave * 58 : wave * 32;

        _leftArmRotation.Angle = swing;
        _rightArmRotation.Angle = -swing;
        _headRotation.Angle = Math.Sin(_walkTime * 0.5) * (_motionMode == MotionMode.Run ? 6 : 3.5);
        _headPitchRotation.Angle = 0;
        _leftArmZRotation.Angle = 0;
        _rightArmZRotation.Angle = 0;
        _leftLegRotation.Angle = -swing;
        _rightLegRotation.Angle = swing;
        _leftLegYRotation.Angle = 0;
        _rightLegYRotation.Angle = 0;
        _bodyRotation.Angle = 0;
    }

    private void ApplyVanillaSwimPose()
    {
        var phase = _walkTime % 26;
        double armX;
        double leftArmZ;
        double rightArmZ;

        if (phase < 14)
        {
            var progress = phase / 14;
            var curved = 1 - Math.Pow(1 - progress, 2);
            armX = 0;
            leftArmZ = 180 + 107.2 * curved;
            rightArmZ = 180 - 107.2 * curved;
        }
        else if (phase < 22)
        {
            var progress = (phase - 14) / 8;
            armX = -90 * progress;
            leftArmZ = 287.2;
            rightArmZ = 72.8;
        }
        else
        {
            var progress = (phase - 22) / 4;
            armX = -90 * (1 - progress);
            leftArmZ = 287.2 - 107.2 * progress;
            rightArmZ = 72.8 + 107.2 * progress;
        }

        _bodyRotation.Angle = 90;
        _headRotation.Angle = 0;
        _headPitchRotation.Angle = 0;
        _leftArmRotation.Angle = armX;
        _rightArmRotation.Angle = armX;
        _leftArmZRotation.Angle = leftArmZ;
        _rightArmZRotation.Angle = rightArmZ;
        var kick = Math.Cos(_walkTime * 0.333) * 17.2;
        _leftLegRotation.Angle = kick;
        _rightLegRotation.Angle = -kick;
    }

    private void ApplySittingPose()
    {
        _bodyRotation.Angle = 0;
        _headRotation.Angle = 0;
        _headPitchRotation.Angle = 0;
        _leftArmRotation.Angle = -36;
        _rightArmRotation.Angle = -36;
        _leftArmZRotation.Angle = 0;
        _rightArmZRotation.Angle = 0;
        _leftLegRotation.Angle = -81;
        _rightLegRotation.Angle = -81;
        _leftLegYRotation.Angle = -18;
        _rightLegYRotation.Angle = 18;
    }

    private void ResetPose()
    {
        _leftArmRotation.Angle = 0;
        _rightArmRotation.Angle = 0;
        _headRotation.Angle = 0;
        _headPitchRotation.Angle = 0;
        _targetHeadYaw = 0;
        _targetHeadPitch = 0;
        _leftArmZRotation.Angle = 0;
        _rightArmZRotation.Angle = 0;
        _leftLegRotation.Angle = 0;
        _rightLegRotation.Angle = 0;
        _leftLegYRotation.Angle = 0;
        _rightLegYRotation.Angle = 0;
        _bodyRotation.Angle = 0;
    }

    private void LoadSkin(string path)
    {
        var bitmap = LoadBitmap(path);
        _playerModel.Children.Clear();
        BuildPlayerModel(bitmap);
        SkinImage.Source = bitmap;
        SkinPathText.Text = Path.GetFileName(path);
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private void ToggleModel_Click(object sender, RoutedEventArgs e)
    {
        _isAlexModel = !_isAlexModel;
        UpdateModelButtonText();
        var path = App.Settings.Data.CustomSkinPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            LoadSkin(path);
    }

    private void UpdateModelButtonText()
    {
        if (ToggleModelBtn != null)
            ToggleModelBtn.Content = _isAlexModel ? "切换至 Steve 模型" : "切换至 Alex 模型";
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void BuildPlayerModel(BitmapSource skin)
    {
        var isAlex = _isAlexModel;
        var armWidth = isAlex ? 3.0 : 4.0;
        var armCx = isAlex ? 5.5 : 6.0;

        var headTransform = CreateHeadTransform(new Point3D(0, 24, 0));
        var leftArmTransform = CreateArmTransform(_leftArmRotation, _leftArmZRotation, new Point3D(-armCx, 20, 0));
        var rightArmTransform = CreateArmTransform(_rightArmRotation, _rightArmZRotation, new Point3D(armCx, 20, 0));
        var leftLegTransform = CreateLegTransform(_leftLegRotation, _leftLegYRotation, new Point3D(-2, 8, 0));
        var rightLegTransform = CreateLegTransform(_rightLegRotation, _rightLegYRotation, new Point3D(2, 8, 0));

        AddCuboid(skin, 0, 24, 0, 8, 8, 8, new FaceRects(
            Top: Rect(8, 0, 8, 8), Bottom: Rect(16, 0, 8, 8), Right: Rect(0, 8, 8, 8),
            Front: Rect(8, 8, 8, 8), Left: Rect(16, 8, 8, 8), Back: Rect(24, 8, 8, 8)), false, headTransform);

        AddCuboid(skin, 0, 14, 0, 8, 12, 4, new FaceRects(
            Top: Rect(20, 16, 8, 4), Bottom: Rect(28, 16, 8, 4), Right: Rect(16, 20, 4, 12),
            Front: Rect(20, 20, 8, 12), Left: Rect(28, 20, 4, 12), Back: Rect(32, 20, 8, 12)));

        var upperArmBase = isAlex
            ? new FaceRects(Rect(44, 16, 3, 4), Rect(47, 16, 3, 4), Rect(40, 20, 4, 12),
                Rect(44, 20, 3, 12), Rect(47, 20, 4, 12), Rect(51, 20, 3, 12))
            : new FaceRects(Rect(44, 16, 4, 4), Rect(48, 16, 4, 4), Rect(40, 20, 4, 12),
                Rect(44, 20, 4, 12), Rect(48, 20, 4, 12), Rect(52, 20, 4, 12));
        var lowerArmBase = isAlex && skin.PixelHeight == 64
            ? new FaceRects(Rect(36, 48, 3, 4), Rect(39, 48, 3, 4), Rect(32, 52, 4, 12),
                Rect(36, 52, 3, 12), Rect(39, 52, 4, 12), Rect(43, 52, 3, 12))
            : skin.PixelHeight == 64
                ? new FaceRects(Rect(36, 48, 4, 4), Rect(40, 48, 4, 4), Rect(32, 52, 4, 12),
                    Rect(36, 52, 4, 12), Rect(40, 52, 4, 12), Rect(44, 52, 4, 12))
                : upperArmBase;

        AddCuboid(skin, -armCx, 14, 0, armWidth, 12, 4, upperArmBase, false, leftArmTransform);
        AddCuboid(skin, armCx, 14, 0, armWidth, 12, 4, lowerArmBase, false, rightArmTransform);

        AddCuboid(skin, -2, 2, 0, 4, 12, 4, new FaceRects(
            Top: Rect(4, 16, 4, 4), Bottom: Rect(8, 16, 4, 4), Right: Rect(0, 20, 4, 12),
            Front: Rect(4, 20, 4, 12), Left: Rect(8, 20, 4, 12), Back: Rect(12, 20, 4, 12)), false, leftLegTransform);

        AddCuboid(skin, 2, 2, 0, 4, 12, 4, skin.PixelHeight == 64
            ? new FaceRects(Rect(20, 48, 4, 4), Rect(24, 48, 4, 4), Rect(16, 52, 4, 12), Rect(20, 52, 4, 12), Rect(24, 52, 4, 12), Rect(28, 52, 4, 12))
            : new FaceRects(Rect(4, 16, 4, 4), Rect(8, 16, 4, 4), Rect(8, 20, 4, 12), Rect(4, 20, 4, 12), Rect(0, 20, 4, 12), Rect(12, 20, 4, 12)), false, rightLegTransform);

        AddCuboid(skin, 0, 24, 0, 9, 9, 9, new FaceRects(
            Top: Rect(40, 0, 8, 8), Bottom: Rect(48, 0, 8, 8), Right: Rect(32, 8, 8, 8),
            Front: Rect(40, 8, 8, 8), Left: Rect(48, 8, 8, 8), Back: Rect(56, 8, 8, 8)), true, headTransform);

        if (skin.PixelHeight != 64) return;

        AddCuboid(skin, 0, 14, 0, 8.5, 12.5, 4.5, new FaceRects(
            Top: Rect(20, 32, 8, 4), Bottom: Rect(28, 32, 8, 4), Right: Rect(16, 36, 4, 12),
            Front: Rect(20, 36, 8, 12), Left: Rect(28, 36, 4, 12), Back: Rect(32, 36, 8, 12)), true);
        var upperArmOverlay = isAlex
            ? new FaceRects(Rect(44, 32, 3, 4), Rect(47, 32, 3, 4), Rect(40, 36, 4, 12),
                Rect(44, 36, 3, 12), Rect(47, 36, 4, 12), Rect(51, 36, 3, 12))
            : new FaceRects(Rect(44, 32, 4, 4), Rect(48, 32, 4, 4), Rect(40, 36, 4, 12),
                Rect(44, 36, 4, 12), Rect(48, 36, 4, 12), Rect(52, 36, 4, 12));
        var lowerArmOverlay = isAlex
            ? new FaceRects(Rect(52, 48, 3, 4), Rect(55, 48, 3, 4), Rect(48, 52, 4, 12),
                Rect(52, 52, 3, 12), Rect(55, 52, 4, 12), Rect(59, 52, 3, 12))
            : new FaceRects(Rect(52, 48, 4, 4), Rect(56, 48, 4, 4), Rect(48, 52, 4, 12),
                Rect(52, 52, 4, 12), Rect(56, 52, 4, 12), Rect(60, 52, 4, 12));
        AddCuboid(skin, -armCx, 14, 0, isAlex ? 3.5 : 4.5, 12.5, 4.5, upperArmOverlay, true, leftArmTransform);
        AddCuboid(skin, armCx, 14, 0, isAlex ? 3.5 : 4.5, 12.5, 4.5, lowerArmOverlay, true, rightArmTransform);
        AddCuboid(skin, -2, 2, 0, 4.5, 12.5, 4.5, new FaceRects(
            Top: Rect(4, 32, 4, 4), Bottom: Rect(8, 32, 4, 4), Right: Rect(0, 36, 4, 12),
            Front: Rect(4, 36, 4, 12), Left: Rect(8, 36, 4, 12), Back: Rect(12, 36, 4, 12)), true, leftLegTransform);
        AddCuboid(skin, 2, 2, 0, 4.5, 12.5, 4.5, new FaceRects(
            Top: Rect(4, 48, 4, 4), Bottom: Rect(8, 48, 4, 4), Right: Rect(0, 52, 4, 12),
            Front: Rect(4, 52, 4, 12), Left: Rect(8, 52, 4, 12), Back: Rect(12, 52, 4, 12)), true, rightLegTransform);
    }

    private static Transform3D CreateArmTransform(AxisAngleRotation3D xRotation,
        AxisAngleRotation3D zRotation, Point3D pivot)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(xRotation, pivot));
        transform.Children.Add(new RotateTransform3D(zRotation, pivot));
        return transform;
    }

    private Transform3D CreateHeadTransform(Point3D pivot)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(_headPitchRotation, pivot));
        transform.Children.Add(new RotateTransform3D(_headRotation, pivot));
        return transform;
    }

    private static Transform3D CreateLegTransform(AxisAngleRotation3D xRotation,
        AxisAngleRotation3D yRotation, Point3D pivot)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(xRotation, pivot));
        transform.Children.Add(new RotateTransform3D(yRotation, pivot));
        return transform;
    }

    private void AddCuboid(BitmapSource skin, double cx, double cy, double cz, double w, double h, double d, FaceRects rects, bool overlay = false, Transform3D? transform = null)
    {
        var x0 = cx - w / 2; var x1 = cx + w / 2;
        var y0 = cy - h / 2; var y1 = cy + h / 2;
        var z0 = cz - d / 2; var z1 = cz + d / 2;

        AddFace(skin, rects.Front, P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1), overlay, transform);
        AddFace(skin, rects.Back, P(x1, y0, z0), P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0), overlay, transform);
        AddFace(skin, rects.Left, P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1), P(x0, y1, z0), overlay, transform);
        AddFace(skin, rects.Right, P(x1, y0, z1), P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1), overlay, transform);
        AddFace(skin, rects.Top, P(x0, y1, z1), P(x1, y1, z1), P(x1, y1, z0), P(x0, y1, z0), overlay, transform);
        AddFace(skin, rects.Bottom, P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1), overlay, transform);
    }

    private void AddFace(BitmapSource skin, Int32Rect rect, Point3D p0, Point3D p1, Point3D p2, Point3D p3, bool overlay, Transform3D? transform)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { p0, p1, p2, p3 },
            TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };

        var crop = ScalePixels(new CroppedBitmap(skin, rect), 16);
        var brush = new ImageBrush(crop) { Stretch = Stretch.Fill, TileMode = TileMode.None };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        Material material = new DiffuseMaterial(brush);
        var model = new GeometryModel3D(mesh, material)
        {
            BackMaterial = material
        };
        if (transform != null) model.Transform = transform;
        _playerModel.Children.Add(model);
    }

    private static MeshGeometry3D CreateOverlayMesh(BitmapSource skin, Int32Rect rect,
        Point3D p0, Point3D p1, Point3D p2, Point3D p3)
    {
        var crop = new FormatConvertedBitmap(new CroppedBitmap(skin, rect), PixelFormats.Bgra32, null, 0);
        var stride = crop.PixelWidth * 4;
        var pixels = new byte[stride * crop.PixelHeight];
        crop.CopyPixels(pixels, stride, 0);

        var mesh = new MeshGeometry3D();
        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] == 0) continue;

                var u0 = (double)x / rect.Width;
                var u1 = (double)(x + 1) / rect.Width;
                var v0 = (double)y / rect.Height;
                var v1 = (double)(y + 1) / rect.Height;
                var index = mesh.Positions.Count;

                mesh.Positions.Add(InterpolateFace(p0, p1, p2, p3, u0, v1));
                mesh.Positions.Add(InterpolateFace(p0, p1, p2, p3, u1, v1));
                mesh.Positions.Add(InterpolateFace(p0, p1, p2, p3, u1, v0));
                mesh.Positions.Add(InterpolateFace(p0, p1, p2, p3, u0, v0));
                mesh.TextureCoordinates.Add(new Point(u0, v1));
                mesh.TextureCoordinates.Add(new Point(u1, v1));
                mesh.TextureCoordinates.Add(new Point(u1, v0));
                mesh.TextureCoordinates.Add(new Point(u0, v0));
                mesh.TriangleIndices.Add(index);
                mesh.TriangleIndices.Add(index + 1);
                mesh.TriangleIndices.Add(index + 2);
                mesh.TriangleIndices.Add(index);
                mesh.TriangleIndices.Add(index + 2);
                mesh.TriangleIndices.Add(index + 3);
            }
        }
        return mesh;
    }

    private static Point3D InterpolateFace(Point3D p0, Point3D p1, Point3D p2, Point3D p3, double u, double v)
    {
        var top = p3 + (p2 - p3) * u;
        var bottom = p0 + (p1 - p0) * u;
        return top + (bottom - top) * v;
    }

    private static BitmapSource ScalePixels(BitmapSource source, int scale)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var sourceStride = converted.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * converted.PixelHeight];
        converted.CopyPixels(sourcePixels, sourceStride, 0);

        var width = converted.PixelWidth * scale;
        var height = converted.PixelHeight * scale;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = y / scale;
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = sourceY * sourceStride + (x / scale) * 4;
                var targetIndex = y * stride + x * 4;
                Buffer.BlockCopy(sourcePixels, sourceIndex, pixels, targetIndex, 4);
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastMouse = e.GetPosition(this);
        SkinViewport.CaptureMouse();
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        SkinViewport.ReleaseMouseCapture();
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var current = e.GetPosition(this);
        _targetYaw += (current.X - _lastMouse.X) * 0.7;
        _targetPitch = Math.Clamp(
            _targetPitch + (current.Y - _lastMouse.Y) * 0.6,
            -75,
            75);
        _lastMouse = current;
    }

    private static Int32Rect Rect(int x, int y, int w, int h) => new(x, y, w, h);
    private static Point3D P(double x, double y, double z) => new(x, y, z);

    private sealed record FaceRects(Int32Rect Top, Int32Rect Bottom, Int32Rect Right, Int32Rect Front, Int32Rect Left, Int32Rect Back);

    private enum MotionMode
    {
        Walk,
        Run,
        Swim,
        Sit
    }
}
