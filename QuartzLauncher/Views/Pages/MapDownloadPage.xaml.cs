using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class MapDownloadPage : Page, System.ComponentModel.INotifyPropertyChanged
{
    private const int PageSize = 8;
    private const int Columns = 2;

    private readonly ObservableCollection<MapCraftItem> _items = new();
    private readonly List<MapCraftItem> _all = new();
    private int _sitePage;          // 已抓取到站点第几页（1 开始）
    private int _page = 1;          // 界面上的页码（每页 8 个）
    private string _categoryPath = MapCraftService.Categories[0].Path;
    private bool _loading;
    private double _cardWidth = 150;
    private MapCraftItem? _detail;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public double CardWidth
    {
        get => _cardWidth;
        private set
        {
            if (Math.Abs(_cardWidth - value) < 0.5) return;
            _cardWidth = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardWidth)));
        }
    }

    public MapDownloadPage()
    {
        InitializeComponent();
        DataContext = this;
        MapList.ItemsSource = _items;
        foreach (var category in MapCraftService.Categories)
            CategoryBox.Items.Add(category.Name);
        CategoryBox.SelectedIndex = 0;
        Loaded += async (_, _) => await ShowPageAsync(1);
    }

    private void MapScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double gap = 10;
        var available = MapScroll.ActualWidth - 4;
        if (available <= 0) return;
        CardWidth = Math.Floor(available / Columns) - gap;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MapCraftItem item }) ShowDetail(item);
    }

    private void ReadMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MapCraftItem item }) ShowDetail(item);
    }

    // ===== 列表 =====

    private async Task ShowPageAsync(int page)
    {
        _page = Math.Max(1, page);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusText.Text = "正在加载…";
        PrevBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;

        try
        {
            await EnsureItemsAsync(_page * PageSize);

            var slice = _all.Skip((_page - 1) * PageSize).Take(PageSize).ToList();
            _items.Clear();
            foreach (var map in slice) _items.Add(map);

            PageText.Text = $"第 {_page} 页（每页 {PageSize} 个）";
            StatusText.Text = slice.Count == 0 ? "没有更多地图了" : $"本页 {slice.Count} 张 · 已加载 {_all.Count} 张";
            PrevBtn.IsEnabled = _page > 1;
            NextBtn.IsEnabled = slice.Count == PageSize;

            _ = FillCardsAsync(slice);
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    // 站点每页数量不定：按需继续抓取，直到够用
    private async Task EnsureItemsAsync(int needed)
    {
        while (_all.Count < needed)
        {
            var items = await MapCraftService.GetListAsync(_categoryPath, _sitePage + 1);
            var maps = items.Where(MapCraftService.IsJavaMap).ToList();
            if (maps.Count == 0) break;

            _sitePage++;
            foreach (var map in maps)
            {
                if (_all.Any(exist => string.Equals(exist.DetailUrl, map.DetailUrl, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _all.Add(map);
            }

            if (_sitePage >= 40) break; // 兜底：避免无限翻页
        }
    }

    private static async Task FillCardsAsync(List<MapCraftItem> maps)
    {
        var pending = maps.Where(map => !map.DescriptionLoaded).ToList();
        foreach (var chunk in pending.Chunk(4))
            await Task.WhenAll(chunk.Select(map => MapCraftService.FillDetailAsync(map)));
    }

    private async void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        var index = CategoryBox.SelectedIndex;
        if (index < 0 || index >= MapCraftService.Categories.Length) return;

        _categoryPath = MapCraftService.Categories[index].Path;
        _all.Clear();
        _sitePage = 0;
        await ShowPageAsync(1);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _all.Clear();
        _sitePage = 0;
        await ShowPageAsync(1);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        await ShowPageAsync(_page - 1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await ShowPageAsync(_page + 1);

    // ===== 详情 =====

    private void ShowDetail(MapCraftItem item)
    {
        _detail = item;
        DetailTitle.Text = item.Title;
        DetailDescription.Text = string.IsNullOrWhiteSpace(item.Description) ? "暂无简介。" : item.Description;
        DetailVersion.Text = string.IsNullOrWhiteSpace(item.Version) ? "版本未知" : item.Version;
        DetailSize.Text = string.IsNullOrWhiteSpace(item.SizeText) ? "大小未知" : item.SizeText;
        DetailImage.Source = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(item.ThumbnailUrl);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                DetailImage.Source = bitmap;
            }
        }
        catch
        {
            // 图片加载失败时留空
        }

        ListView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;

        // 用网站详情页的完整简介替换列表页的一行摘要
        _ = RefreshDescriptionAsync(item);
    }

    private async Task RefreshDescriptionAsync(MapCraftItem item)
    {
        await MapCraftService.FillDescriptionAsync(item);
        if (!ReferenceEquals(_detail, item)) return;
        DetailDescription.Text = string.IsNullOrWhiteSpace(item.Description) ? "暂无简介。" : item.Description;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        DetailView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        var url = _detail?.DetailUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AnimatedMessageBox.Show("无法打开浏览器：" + ex.Message, "地图下载");
        }
    }

    private async void CardDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MapCraftItem item })
            await StartDownloadAsync(item);
    }

    private async void DetailDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item) return;
        await StartDownloadAsync(item);
    }

    private async Task StartDownloadAsync(MapCraftItem item)
    {

        if (string.IsNullOrWhiteSpace(item.DownloadUrl) || string.IsNullOrWhiteSpace(item.SizeText))
        {
            StatusText.Text = "正在获取下载信息…";
            var filled = await MapCraftService.FillDownloadAsync(item);
            StatusText.Text = "";
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                if (!filled)
                {
                    AnimatedMessageBox.Show(
                        "这个地图页面上没找到直接下载链接，可能是外链或已删除。\n\n可以点「在浏览器打开」手动下载。",
                        "地图下载", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择地图保存位置",
            FileName = SuggestFileName(item),
            Filter = "压缩包 (*.zip;*.rar)|*.zip;*.rar|所有文件 (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // 带上文件大小，下载器才能对 ≥4MB 的包做多线程分片下载；线程数跟随设置
            var size = MapCraftService.ParseSizeBytes(item.SizeText);
            DownloadManager.Instance.Enqueue(
                item.Title,
                [new DownloadItem(item.DownloadUrl, dialog.FileName, "", size)],
                workers: Math.Clamp(App.Settings.Data.DownloadWorkers, 1, 16),
                category: "resource");

            if (Window.GetWindow(this) is MainWindow mainWindow)
                mainWindow.NavigateToDownloadCenter();
        }
        catch (Exception ex)
        {
            AnimatedMessageBox.Show("下载失败：" + ex.Message, "地图下载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SuggestFileName(MapCraftItem item)
    {
        try
        {
            var path = Uri.UnescapeDataString(new Uri(item.DownloadUrl).AbsolutePath).Replace('+', ' ');
            var name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // 地址异常时退回用标题命名
        }

        return MapCraftService.GuessSaveName(item) + ".zip";
    }
}
