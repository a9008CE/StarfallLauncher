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
        playerTransform.Children.Add(new RotateTransform3D(_bodyRotation, new Point3D(0, 14, 0)));
        playerTransform.Children.Add(new RotateTransform3D(_viewPitchRotation, new Point3D(0, 14, 0)));
        playerTransform.Children.Add(new RotateTransform3D(_rotation, new Point3D(0, 14, 0)));
        _playerModel.Transform = playerTransform;
        SceneRoot.Children.Add(_playerModel);
        _walkTimer.Tick += WalkTimer_Tick;
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
        if (sender is not Button { Tag: string name }) return;
        if (name == "默认")
        {
            _walkTimer.Stop();
            ResetPose();
            _poseActive = false;
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

    private void ToggleMotion(MotionMode mode)
    {
        if (_poseActive && _motionMode == mode)
        {
            _walkTimer.Stop();
            ResetPose();
            _poseActive = false;
            return;
        }

        ResetPose();
        _motionMode = mode;
        _walkTime = 0;
        _poseActive = true;
        if (mode == MotionMode.Sit)
        {
            _walkTimer.Stop();
            ApplySittingPose();
            return;
        }
        _walkTimer.Start();
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
