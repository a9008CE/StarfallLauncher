using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class ThemeDetailPage : Page
{
    private static readonly int[] CarouselSecondOptions = { 5, 10, 15, 30, 60, 120 };

    private CancellationTokenSource? _presetGalleryCts;
    private int _presetGalleryGeneration;

    private static readonly Dictionary<string, string> UiStyleLabels = new()
    {
        ["minimal"] = "极简深色",
        ["frosted"] = "毛玻璃",
        ["flat"] = "扁平浅色",
        ["cyberpunk"] = "赛博朋克 2077",
        ["wanderingearth"] = "流浪地球 550W",
    };

    public ThemeDetailPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var mode = App.Settings.Data.ThemeMode;
        DarkBtn.IsChecked = mode == "dark";
        LightBtn.IsChecked = mode == "light";
        SidebarTextLeftSwitch.IsChecked = App.Settings.Data.SidebarTextLeftAligned;
        var resourceStyle = App.Settings.Data.ResourceCenterStyle == "grid";
        ListStyleBtn.IsChecked = !resourceStyle;
        GridStyleBtn.IsChecked = resourceStyle;

        BuildUiStyleButtons();
        BuildThemeButtons();
        BuildCarouselSecondsBox();
        RefreshCustomBackgroundControls();
    }

    private void BuildCarouselSecondsBox()
    {
        if (CarouselSecondsBox.Items.Count > 0) return;
        foreach (var seconds in CarouselSecondOptions)
        {
            var item = new ComboBoxItem { Content = $"{seconds} 秒", Tag = seconds };
            CarouselSecondsBox.Items.Add(item);
        }
    }

    private void ResourceStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string style)
        {
            App.Settings.Data.ResourceCenterStyle = style;
            App.Settings.Save();
        }
    }

    private void SidebarTextAlignment_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Data.SidebarTextLeftAligned = SidebarTextLeftSwitch.IsChecked == true;
        App.Settings.Save();
        App.Theme.Apply();
    }

    private void BuildUiStyleButtons()
    {
        UiStylePanel.Children.Clear();
        var current = App.Settings.Data.UiStyle;

        foreach (var (key, label) in UiStyleLabels)
        {
            var btn = new Button
            {
                Width = 130, Height = 44, Margin = new Thickness(4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = key == current
                    ? (Brush)FindResource("PrimaryBrush")
                    : (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(key == current ? 2 : 1),
                Padding = new Thickness(8),
                Tag = key,
                Style = (Style)FindResource("BtnBase"),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = key == current ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty,
                key == current ? "PrimaryBrush" : "TextBrush");
            btn.Content = labelBlock;
            btn.Click += UiStyle_Click;
            UiStylePanel.Children.Add(btn);
        }
    }

    private void BuildThemeButtons()
    {
        ThemePanel.Children.Clear();
        var current = App.Settings.Data.ThemeName;
        var uiStyle = App.Settings.Data.UiStyle;
        var mode = App.Settings.Data.ThemeMode;
        var presets = Theme.GetPresets(uiStyle, mode);

        foreach (var (name, theme) in presets)
        {
            var btn = new Button
            {
                Width = 120, Height = 60, Margin = new Thickness(4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = name == current
                    ? (Brush)FindResource("PrimaryBrush")
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Border)),
                BorderThickness = new Thickness(name == current ? 2 : 1),
                Padding = new Thickness(8),
                Tag = name,
                Style = (Style)FindResource("BtnBase"),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
            btn.Click += Theme_Click;

            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameBlock = new TextBlock
            {
                Text = name, FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextMuted)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
                SnapsToDevicePixels = true,
            };
            panel.Children.Add(nameBlock);

            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var c in new[] { theme.Primary, theme.Bg, theme.Card })
            {
                colorRow.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 10, Height = 10, Margin = new Thickness(3, 0, 3, 0),
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c))
                });
            }
            panel.Children.Add(colorRow);
            btn.Content = panel;
            ThemePanel.Children.Add(btn);
        }
    }

    private void RefreshCustomBackgroundControls()
    {
        var data = App.Settings.Data;
        var presets = ThemeBackgroundService.GetPresets();
        CustomBackgroundStatus.Text = presets.Count == 0
            ? "图库为空：把图片放进 Launcher\\backgrounds 目录即可，预设会自动出现。"
            : !string.IsNullOrWhiteSpace(data.ThemeBackgroundImage)
                ? $"当前固定：{System.IO.Path.GetFileName(data.ThemeBackgroundImage)}　·　点缩略图固定，✕ 删除文件"
                : $"图库共 {presets.Count} 张　·　点缩略图固定，✕ 删除文件";
        ClearBackgroundButton.IsEnabled = !string.IsNullOrWhiteSpace(data.ThemeBackgroundImage);

        BuildPresetGallery(presets);
        SyncCarouselControls();
    }

    /// <summary>
    /// 缩略图列表：点击即固定该张并暂停轮播（自选优先于轮播）。
    /// 解码放到后台线程 —— 最大那张原图 60MB 以上，同步解码会卡住 UI 线程，
    /// 导致首次进入「更多功能」时的入场动画被吞掉。
    /// </summary>
    private void BuildPresetGallery(IReadOnlyList<string> presets)
    {
        var generation = ++_presetGalleryGeneration;
        _presetGalleryCts?.Cancel();
        _presetGalleryCts?.Dispose();
        var cts = _presetGalleryCts = new CancellationTokenSource();

        var data = App.Settings.Data;
        var active = ThemeBackgroundService.ResolveActivePath(data);
        var pinned = string.IsNullOrWhiteSpace(data.ThemeBackgroundImage)
            ? null
            : System.IO.Path.GetFullPath(data.ThemeBackgroundImage);

        BackgroundPresetPanel.Children.Clear();
        if (presets.Count == 0)
        {
            BackgroundPresetPanel.Children.Add(new TextBlock
            {
                Text = "暂无预设图片",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush")
            });
            return;
        }

        var images = new List<Image>(presets.Count);
        foreach (var path in presets)
        {
            var isPinned = pinned is not null
                && string.Equals(pinned, path, StringComparison.OrdinalIgnoreCase);

            var image = new Image
            {
                Width = 92,
                Height = 52,
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            images.Add(image);

            var tile = new Border
            {
                CornerRadius = new CornerRadius(6),
                Child = image,
                ToolTip = isPinned
                    ? $"{System.IO.Path.GetFileName(path)}（已固定）"
                    : System.IO.Path.GetFileName(path),
                Tag = path
            };

            tile.BorderBrush = (Brush)FindResource(isPinned ? "PrimaryBrush" : "BorderBrush");
            tile.BorderThickness = new Thickness(isPinned ? 2 : 1);
            tile.MouseLeftButtonDown += Preset_Click;

            // 删除按钮与缩略图同级，避免点击删除时同时触发「固定」
            var remove = new Button
            {
                Content = "✕",
                Width = 18,
                Height = 18,
                FontSize = 9,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(220, 15, 18, 26)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                ToolTip = "从图库中删除这张图片",
                Tag = path
            };
            remove.Click += PresetRemove_Click;

            var cell = new Grid
            {
                Width = 100,
                Height = 60,
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Opacity = string.Equals(active, path, StringComparison.OrdinalIgnoreCase) ? 1 : 0.72,
                Children = { tile, remove }
            };
            BackgroundPresetPanel.Children.Add(cell);
        }

        _ = Task.Run(() =>
        {
            for (var i = 0; i < presets.Count; i++)
            {
                if (cts.IsCancellationRequested) return;
                var thumb = ThemeBackgroundService.LoadThumbnail(presets[i], 200);
                var index = i;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts.IsCancellationRequested || index >= images.Count) return;
                    images[index].Source = thumb;
                }), DispatcherPriority.Background);
            }
        });
    }

    private void Preset_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string path }) return;
        App.Settings.Data.ThemeBackgroundImage = path;
        App.Settings.Data.ThemeBackgroundCarousel = false;
        App.Settings.Save();
        App.Theme.Apply();
        RefreshCustomBackgroundControls();
    }

    /// <summary>把图片文件从图库里彻底删掉，「清除」只解除固定、不动文件。</summary>
    private void PresetRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;

        var name = System.IO.Path.GetFileName(path);
        var confirm = MessageBox.Show(
            $"确定要从图库中永久删除「{name}」吗？\n\n这会删除硬盘上的原图文件，无法撤销。",
            "删除预设背景", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "删除预设背景",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var pinned = string.IsNullOrWhiteSpace(App.Settings.Data.ThemeBackgroundImage)
            ? null
            : System.IO.Path.GetFullPath(App.Settings.Data.ThemeBackgroundImage);
        if (pinned is not null
            && string.Equals(pinned, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            App.Settings.Data.ThemeBackgroundImage = "";
        }

        ThemeBackgroundService.InvalidateThumbnails();
        App.Settings.Save();
        App.Theme.Apply();
        RefreshCustomBackgroundControls();
    }

    private void SyncCarouselControls()
    {
        var data = App.Settings.Data;
        BackgroundCarouselSwitch.IsChecked = data.ThemeBackgroundCarousel;

        var seconds = CarouselSecondOptions.Contains(data.ThemeBackgroundCarouselSeconds)
            ? data.ThemeBackgroundCarouselSeconds
            : 12;
        foreach (ComboBoxItem item in CarouselSecondsBox.Items)
        {
            if (item.Tag is int value && value == seconds) CarouselSecondsBox.SelectedItem = item;
        }
        RefreshCarouselStatus();
    }

    private void BackgroundCarousel_Click(object sender, RoutedEventArgs e)
    {
        var data = App.Settings.Data;
        data.ThemeBackgroundCarousel = BackgroundCarouselSwitch.IsChecked == true;
        if (data.ThemeBackgroundCarousel)
        {
            // 轮播与自选固定互斥：开启轮播即解除固定，回到图库顺序
            data.ThemeBackgroundImage = "";
        }
        App.Settings.Save();
        App.Theme.Apply();
        RefreshCustomBackgroundControls();
    }

    private void CarouselSeconds_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CarouselSecondsBox.SelectedItem is not ComboBoxItem { Tag: int seconds }) return;
        if (App.Settings.Data.ThemeBackgroundCarouselSeconds == seconds) return;
        App.Settings.Data.ThemeBackgroundCarouselSeconds = seconds;
        App.Settings.Save();
        if (App.Settings.Data.ThemeBackgroundCarousel) App.Theme.Apply();
        RefreshCarouselStatus();
    }

    private void RefreshCarouselStatus()
    {
        var data = App.Settings.Data;
        var count = ThemeBackgroundService.GetPresets().Count;
        CarouselStatus.Text = data.ThemeBackgroundCarousel
            ? $"每 {data.ThemeBackgroundCarouselSeconds} 秒切换一张，共 {count} 张预设"
            : "关闭时按预设图库的第一张显示；点击某张缩略图即可固定";
    }

    private void OpenBackgroundFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ThemeBackgroundService.PresetsDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择启动器背景",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        App.Settings.Data.ThemeBackgroundImage = dialog.FileName;
        App.Settings.Data.ThemeBackgroundCarousel = false;
        App.Settings.Save();
        App.Theme.Apply();
        RefreshCustomBackgroundControls();
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(App.Settings.Data.ThemeBackgroundImage)
            && App.Settings.Data.ThemeBackgroundIndex == 0) return;
        App.Settings.Data.ThemeBackgroundImage = "";
        App.Settings.Data.ThemeBackgroundIndex = 0;
        App.Settings.Save();
        App.Theme.Apply();
        RefreshCustomBackgroundControls();
    }

    private void UiStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string style)
            ApplyWithTransition(() => App.Theme.SetUiStyle(style));
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
            ApplyWithTransition(() => App.Theme.SelectPreset(name));
    }

    private void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        ApplyWithTransition(() => App.Theme.SetMode("dark"));
    }

    private void LightMode_Click(object sender, RoutedEventArgs e)
    {
        ApplyWithTransition(() => App.Theme.SetMode("light"));
    }

    // 统一走遮罩过渡，等动画结束后再刷新卡片，避免卡片提前变色
    private void ApplyWithTransition(Action apply)
    {
        void Refresh()
        {
            App.Settings.Save();
            BuildUiStyleButtons();
            BuildThemeButtons();
            RefreshCustomBackgroundControls();
        }

        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.RunDiagonalThemeTransition(apply, Refresh);
        else
        {
            apply();
            Refresh();
        }
    }
}
