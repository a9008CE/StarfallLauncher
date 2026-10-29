using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class ServerBrowserPage : Page
{
    private bool _loading;
    private int _currentPage = 1;
    private int _totalPages = 1;
    private int _lastShown;
    private string _region = "all";     // all / cn / foreign
    private string _sort = "default";   // default / players / ping / name
    private bool _modOnly;
    private string _keyword = "";
    private readonly Dictionary<string, ServerDirectoryResult> _pageCache = new();
    private bool _hasLoaded;
    private DispatcherTimer? _searchDebounce;
    private DispatcherTimer? _spinnerTimer;

    /// <summary>简介前缀形如「[1.20.1] 服务器名 - 介绍」，版本号就藏在方括号里。</summary>
    private static readonly Regex VersionRegex = new(@"^\s*\[(?<ver>[^\]]{1,16})\]\s*", RegexOptions.Compiled);

    public ServerBrowserPage()
    {
        InitializeComponent();
        Loaded += async (_, e) =>
        {
            if (e is { } && !_hasLoaded)
            {
                _hasLoaded = true;
                await LoadServersAsync();
            }
        };
    }

    private void ShowLoading()
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        _spinnerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinnerTimer.Tick -= SpinnerTick;
        _spinnerTimer.Tick += SpinnerTick;
        _spinnerTimer.Start();
    }

    private void SpinnerTick(object? sender, EventArgs e)
    {
        if (SpinnerPath.RenderTransform is RotateTransform rotate)
            rotate.Angle = (rotate.Angle + 6) % 360;
    }

    private void HideLoading()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        _spinnerTimer?.Stop();
    }

    private string CacheKey(int page) => $"{page}:{(_modOnly ? 1 : 0)}";

    private async Task<ServerDirectoryResult> FetchPageAsync(int page)
    {
        var key = CacheKey(page);
        if (_pageCache.TryGetValue(key, out var cached)) return cached;

        ServerDirectoryResult? result = null;
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                result = await MinecraftServerDirectoryService.GetOnlineServersAsync(page, _modOnly);
                break;
            }
            catch when (retry < 2)
            {
                await Task.Delay(700 * (retry + 1));
            }
        }
        if (result == null) throw new IOException("服务器列表获取失败");
        _pageCache[key] = result;
        return result;
    }

    private async Task LoadServersAsync(bool forceRefresh = false)
    {
        if (_loading) return;
        _loading = true;
        ShowLoading();
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            if (forceRefresh)
            {
                _pageCache.Clear();
                _currentPage = 1;
            }

            // 区域/搜索是本地过滤：如果这一页过滤后为空，自动往后翻页，
            // 直到有内容或到末页（最多连翻 4 页），避免「切到国内只剩两三个/空白」。
            var hops = 0;
            ServerDirectoryResult result;
            while (true)
            {
                StatusText.Text = $"正在获取第 {_currentPage} 页…";
                result = await FetchPageAsync(_currentPage);
                _currentPage = Math.Max(1, result.Page);
                _totalPages = Math.Max(1, result.TotalPages);
                var shown = ApplyFilters(result.Items).ToList();
                var needsMore = shown.Count == 0 && _currentPage < _totalPages && hops < 4
                                && (_region != "all" || !string.IsNullOrWhiteSpace(_keyword));
                if (!needsMore) break;
                _currentPage++;
                hops++;
            }

            RenderServers(result.Items);
            PageText.Text = $"{_currentPage} / {_totalPages}";
            PreviousButton.IsEnabled = _currentPage > 1;
            NextButton.IsEnabled = _currentPage < _totalPages;
        }
        catch (Exception ex)
        {
            // 不再弹模态框：状态行 + 重试按钮，页面还能继续用
            StatusText.Text = "获取失败 · " + ex.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
            HideLoading();
        }
    }

    private IEnumerable<ServerDirectoryItem> ApplyFilters(IEnumerable<ServerDirectoryItem> source)
    {
        var query = source;

        if (_region == "cn")
            query = query.Where(item => string.Equals(item.CountryCode, "CN", StringComparison.OrdinalIgnoreCase));
        else if (_region == "foreign")
            query = query.Where(item => !string.IsNullOrWhiteSpace(item.CountryCode)
                                        && !string.Equals(item.CountryCode, "CN", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_keyword))
        {
            query = query.Where(item =>
                item.Name.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                || GetVersion(item).Contains(_keyword, StringComparison.OrdinalIgnoreCase));
        }

        query = _sort switch
        {
            "players" => query.OrderByDescending(item => ParseFirstNumber(item.Players)),
            "ping" => query.OrderBy(item => ParseFirstNumber(item.Ping)),
            "name" => query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query,
        };
        return query;
    }

    private void RenderServers(IEnumerable<ServerDirectoryItem> source)
    {
        var servers = ApplyFilters(source).ToList();
        ServerPanel.Children.Clear();
        foreach (var server in servers)
            ServerPanel.Children.Add(CreateServerCard(server));

        _lastShown = servers.Count;
        if (servers.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = _region == "all" && string.IsNullOrWhiteSpace(_keyword)
                    ? "这一页没有服务器，试试刷新或翻页。"
                    : "这一页没有符合筛选条件的服务器（筛选只作用于已加载的页）。",
                FontSize = 13,
                Margin = new Thickness(0, 40, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            ServerPanel.Children.Add(empty);
        }
        StatusText.Text = servers.Count == 0 ? "无匹配" : $"本页 {servers.Count} 个";
    }

    /// <summary>从简介里取版本号（「[1.20.1] xxx」），取不到返回空串。</summary>
    private static string GetVersion(ServerDirectoryItem server)
        => VersionRegex.Match(server.Description ?? "").Groups["ver"].Value.Trim();

    private static string StripVersion(string description)
        => VersionRegex.Replace(description ?? "", "").Trim();

    private static double ParseFirstNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.MaxValue;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : double.MaxValue;
    }

    private Border CreateServerCard(ServerDirectoryItem server)
    {
        var version = GetVersion(server);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标
        var iconBox = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x30)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Uri.TryCreate(server.IconUrl, UriKind.Absolute, out var iconUri))
        {
            iconBox.Child = new Image
            {
                Source = new BitmapImage(iconUri),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(3),
            };
        }
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        // 中间：名称 + 标签行 + 简介
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock
        {
            Text = server.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        titleRow.Children.Add(name);

        if (!string.IsNullOrWhiteSpace(version))
            titleRow.Children.Add(CreateTag("MC " + version, null, new Thickness(8, 0, 0, 0)));
        if (!string.IsNullOrWhiteSpace(server.CountryCode))
            titleRow.Children.Add(CreateTag(server.CountryCode, null, new Thickness(6, 0, 0, 0)));

        info.Children.Add(titleRow);

        var description = StripVersion(server.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var desc = new TextBlock
            {
                Text = description,
                FontSize = 11,
                Margin = new Thickness(0, 5, 12, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 32,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            info.Children.Add(desc);
        }

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // 右侧：人数徽章 + 延迟（带颜色）
        var right = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 2, 0),
            Width = 150,
        };
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var players = new TextBlock
        {
            Text = server.Players,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        players.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(players, 0);
        right.Children.Add(players);

        var ping = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.Ping) ? "—" : server.Ping + " ms",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ServerDetailDialog.PingBrush(server.Ping),
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(ping, 1);
        right.Children.Add(ping);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var card = new Border
        {
            Child = grid,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14, 10, 14, 10),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        card.SetResourceReference(Border.StyleProperty, "CardStyle");
        card.MouseLeftButtonUp += (_, _) => ServerDetailDialog.Show(Window.GetWindow(this), server, version);
        return card;
    }

    private static Border CreateTag(string text, Brush? foreground, Thickness margin)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = margin,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = foreground ?? (Brush)Application.Current.TryFindResource("TextMutedBrush")!,
        };
        border.Child = block;
        return border;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadServersAsync(true);

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadServersAsync(true);

    private async void ModOnly_Click(object sender, RoutedEventArgs e)
    {
        _modOnly = ModOnlyCheck.IsChecked == true;
        _pageCache.Clear();
        _currentPage = 1;
        await LoadServersAsync(true);
    }

    private async void Region_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string region }) return;
        _region = region;
        UpdateRegionButtons();
        // 区域过滤是本地行为，不需要重新请求；但如果这一页过滤后为空，LoadServersAsync 会自动翻页
        await LoadServersAsync();
    }

    private void UpdateRegionButtons()
    {
        void Style(Button button, bool active)
        {
            button.SetResourceReference(FrameworkElement.StyleProperty, active ? "BtnPrimary" : "BtnBase");
        }
        Style(AllButton, _region == "all");
        Style(DomesticButton, _region == "cn");
        Style(ForeignButton, _region == "foreign");
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string tag }) _sort = tag;
        if (_hasLoaded && !_loading) RenderServers(CurrentPageItems());
    }

    private List<ServerDirectoryItem> CurrentPageItems()
        => _pageCache.TryGetValue(CacheKey(_currentPage), out var result) ? result.Items : new List<ServerDirectoryItem>();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _keyword = SearchBox.Text.Trim();
        _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick -= SearchDebounceTick;
        _searchDebounce.Tick += SearchDebounceTick;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void SearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        if (!_hasLoaded) return;
        await LoadServersAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1) return;
        _currentPage--;
        await LoadServersAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage >= _totalPages) return;
        _currentPage++;
        await LoadServersAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (NavigationService?.CanGoBack == true) NavigationService.GoBack();
        else (Window.GetWindow(this) as MainWindow)?.NavigateToHome();
    }
}
