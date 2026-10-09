using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class SkinLibraryPage : Page
{
    private readonly string _skinDir = Path.Combine(AppContext.BaseDirectory, "Launcher", "skins");

    public SkinLibraryPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshLibrary();
            BeginEnterAnimation();
        };
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

    private void RefreshLibrary()
    {
        Directory.CreateDirectory(_skinDir);
        SkinPanel.Children.Clear();
        var files = Directory.EnumerateFiles(_skinDir, "*.png")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        EmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var path in files)
            SkinPanel.Children.Add(CreateSkinCard(path));
    }

    private Border CreateSkinCard(string path)
    {
        var image = new Image
        {
            Source = LoadBitmap(path),
            Width = 128,
            Height = 128,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 10)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var content = new StackPanel();
        content.Children.Add(image);
        content.Children.Add(new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(path),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextBrush")
        });

        var card = new Border
        {
            Width = 174,
            Tag = path,
            Child = content,
            Margin = new Thickness(0, 0, 14, 14),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "右键管理皮肤"
        };
        card.SetResourceReference(StyleProperty, "CardStyle");
        card.MouseLeftButtonUp += SkinCard_Click;

        var menu = new ContextMenu();
        var previewItem = new MenuItem { Header = "导入预览界面", Tag = path };
        previewItem.Click += ImportPreview_Click;
        var deleteItem = new MenuItem { Header = "删除", Tag = path };
        deleteItem.Click += Delete_Click;
        menu.Items.Add(previewItem);
        menu.Items.Add(deleteItem);
        card.ContextMenu = menu;
        return card;
    }

    private void SkinCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (App.Settings.Data.AuthMode != AuthModes.Offline)
        {
            AnimatedMessageBox.Show("皮肤库只适用于离线登录。第三方登录和正版登录会使用认证服务器的皮肤。",
                "皮肤库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path)) return;
        App.Settings.Data.CustomSkinPath = path;
        App.Settings.Save();

        var frame = FindMoreFrame();
        if (frame != null)
        {
            frame.BeginAnimation(OpacityProperty, null);
            frame.Opacity = 1;
            frame.RenderTransform = null;
            frame.IsHitTestVisible = true;
            frame.Navigate(new SkinPreviewPage());
        }
    }

    private void ImportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path } || !File.Exists(path)) return;
        App.Settings.Data.CustomSkinPath = path;
        App.Settings.Save();
        var frame = FindMoreFrame();
        if (frame != null)
        {
            frame.BeginAnimation(OpacityProperty, null);
            frame.Opacity = 1;
            frame.RenderTransform = null;
            frame.IsHitTestVisible = true;
            frame.Navigate(new SkinPreviewPage());
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path } || !File.Exists(path)) return;
        var result = AnimatedMessageBox.Show($"确定删除皮肤“{Path.GetFileNameWithoutExtension(path)}”吗？",
            "删除皮肤", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (string.Equals(Path.GetFullPath(App.Settings.Data.CustomSkinPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                App.Settings.Data.CustomSkinPath = "";
                App.Settings.Save();
                // 用户把正在用的皮肤删了：服务器上那份也要跟着撤掉
                AccountService.Current?.QueueSkinRefresh();
            }
            File.Delete(path);
            RefreshLibrary();
        }
        catch (Exception ex)
        {
            AnimatedMessageBox.Show($"删除皮肤失败: {ex.Message}", "皮肤库", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var frame = FindMoreFrame();
        if (frame != null)
        {
            frame.BeginAnimation(OpacityProperty, null);
            frame.Opacity = 1;
            frame.RenderTransform = null;
            frame.IsHitTestVisible = true;
            frame.Navigate(new SkinPreviewPage());
        }
    }

    private Frame? FindMoreFrame()
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(this);
        while (parent != null)
        {
            if (parent is Frame f && f.Name == "MoreFrame") return f;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
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
}
