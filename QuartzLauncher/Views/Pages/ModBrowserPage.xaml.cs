using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class ModBrowserPage : Page, IStandaloneSidebarPage
{
    private enum ResourceMode { Mods, Modpacks, Shaders, ResourcePacks }

    private System.Windows.Threading.DispatcherTimer? _loadingDotsTimer;
    private int _loadingDotCount;

    private sealed record CategoryDef(string Label, string MrSlug, string CfId);

    private static readonly HttpClient IconHttp = CreateIconHttpClient();
    private static readonly Dictionary<string, (DateTime Expires, List<ModItem> Items, int Total)> PopularCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> NameLookupAttempted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PopularCacheLock = new();
    private List<ModItem> _currentMods = new();
    private ModItem? _selectedMod;
    private bool _entryAnimationRunning;
    private bool _filtersInitialized;
    private bool _suppressFilterChange;
    private bool _initialJava;
    private ResourceMode _resourceMode = ResourceMode.Mods;
    private int _detailAnimationGeneration;
    private int _contentRequestGeneration;
    private readonly string? _instanceRoot;
    private int _currentPage = 1;
    private int _totalCount;
    private int _perPage = 24;
    private CategoryDef? _selectedCategory;
    private static readonly CategoryDef[] ModCategories =
    [
        new("全部", "", ""),
        new("科技", "technology", "412"),
        new("魔法", "magic", "419"),
        new("冒险", "adventure", "422"),
        new("优化", "optimization", "6814"),
        new("存储", "storage", "420"),
        new("装备", "equipment", "434"),
        new("生物", "mobs", "411"),
        new("装饰", "decoration", "424"),
        new("食物", "food", "436"),
        new("运输", "transportation", "414"),
        new("支持库", "library", "421")
    ];
    private static readonly CategoryDef[] ModpackCategories =
    [
        new("全部", "", ""),
        new("科技", "tech", "4472"),
        new("魔法", "magic", "4473"),
        new("冒险", "adventure", "4475"),
        new("探索", "exploration", "4476"),
        new("小游戏", "minigame", "4477"),
        new("科幻", "scifi", "4474"),
        new("空岛", "skyblock", "4736"),
        new("大型", "kitchen-sink", "4482"),
        new("轻量", "lightweight", "4481"),
        new("任务", "quests", "4478"),
        new("硬核", "challenging", "4479"),
        new("多人", "multiplayer", "4484"),
        new("FTB", "", "4487")
    ];
    private static readonly CategoryDef[] ShaderCategories =
    [
        new("全部", "", ""),
        new("写实风", "realistic", "6553"),
        new("幻想风", "fantasy", "6554"),
        new("原版风", "vanilla-like", "6555"),
        new("实用", "utility", "6953"),
        new("性能", "optimization", "6951")
    ];
    private static readonly CategoryDef[] ResourcepackCategories =
    [
        new("全部", "", ""),
        new("16x", "16x", "393"),
        new("32x", "32x", "394"),
        new("64x", "64x", "395"),
        new("128x", "128x", "396"),
        new("256x", "256x", "397"),
        new("超高清", "512x", "398"),
        new("写实风", "realistic", "400"),
        new("中世纪", "medieval", "402"),
        new("现代风", "modern", "401"),
        new("原版风", "vanilla-like", "403"),
        new("动态效果", "fancy", "404")
    ];

    public ModBrowserPage(string? instanceRoot = null, string? initialMode = null)
    {
        InitializeComponent();
        _instanceRoot = instanceRoot;
        if (initialMode == "modpacks")
        {
            _resourceMode = ResourceMode.Modpacks;
            NavSearch.IsChecked = false;
            NavModpacks.IsChecked = true;
        }
        else if (initialMode == "shaders")
        {
            _resourceMode = ResourceMode.Shaders;
            NavSearch.IsChecked = false;
            NavShaders.IsChecked = true;
        }
        else if (initialMode == "resourcepacks")
        {
            _resourceMode = ResourceMode.ResourcePacks;
            NavSearch.IsChecked = false;
            NavResourcePacks.IsChecked = true;
        }
        else if (initialMode == "java")
        {
            _initialJava = true;
            NavSearch.IsChecked = false;
            NavJava.IsChecked = true;
            BrowserContent.Visibility = Visibility.Collapsed;
            DownloadPage.Visibility = Visibility.Collapsed;
            JavaPage.Visibility = Visibility.Visible;
        }
        SearchPlaceholder.Text = $"搜索{GetModeTitle()}...";
        LoaderCombo.IsEnabled = _resourceMode == ResourceMode.Mods && LoaderCombo.Items.Count == 0;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyViewStyle();
        if (_initialJava) return;
        var generation = BeginContentRequest();
        BuildCategoryChips();
        if (App.Settings.Data.PageAnimationStyle is "none" or "tear")
        {
            InitFilters();
            await LoadPopularMods(generation);
            return;
        }
        PrepareEntryAnimation();
        InitFilters();
        var popularTask = LoadPopularMods(generation);
        await Dispatcher.InvokeAsync(PlayEntryAnimation, System.Windows.Threading.DispatcherPriority.Render);
        await popularTask;
    }

    private void PrepareEntryAnimation()
    {
        var quick = App.Settings.Data.PageAnimationStyle == "quick";
        Sidebar.BeginAnimation(OpacityProperty, null);
        SearchHeader.BeginAnimation(OpacityProperty, null);
        ResultsPanel.BeginAnimation(OpacityProperty, null);

        Sidebar.Opacity = 1;
        Sidebar.RenderTransform = new TranslateTransform(quick ? -24 : -42, 0);
        SearchHeader.Opacity = 1;
        SearchHeader.RenderTransform = new TranslateTransform(0, quick ? -8 : -14);
        ResultsPanel.Opacity = 1;
        ResultsPanel.RenderTransform = new TranslateTransform(quick ? 12 : 22, quick ? 6 : 12);
    }

    private async void PlayEntryAnimation()
    {
        if (_entryAnimationRunning) return;
        _entryAnimationRunning = true;

        var quick = App.Settings.Data.PageAnimationStyle == "quick";
        var sidebarDuration = quick ? 170 : 360;
        var headerDuration = quick ? 150 : 330;
        var resultsDuration = quick ? 190 : 400;
        var headerDelay = quick ? 20 : 70;
        var resultsDelay = quick ? 45 : 150;
        var sidebarEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (Sidebar.RenderTransform is not TranslateTransform sidebarTransform)
        {
            sidebarTransform = new TranslateTransform();
            Sidebar.RenderTransform = sidebarTransform;
        }
        sidebarTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(quick ? -24 : -42, 0, TimeSpan.FromMilliseconds(sidebarDuration)) { EasingFunction = sidebarEase });

        if (SearchHeader.RenderTransform is not TranslateTransform headerTransform)
        {
            headerTransform = new TranslateTransform();
            SearchHeader.RenderTransform = headerTransform;
        }
        headerTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(quick ? -8 : -14, 0, TimeSpan.FromMilliseconds(headerDuration))
            {
                BeginTime = TimeSpan.FromMilliseconds(headerDelay),
                EasingFunction = sidebarEase
            });

        if (ResultsPanel.RenderTransform is not TranslateTransform resultsTransform)
        {
            resultsTransform = new TranslateTransform();
            ResultsPanel.RenderTransform = resultsTransform;
        }
        resultsTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(quick ? 12 : 22, 0, TimeSpan.FromMilliseconds(resultsDuration))
            {
                BeginTime = TimeSpan.FromMilliseconds(resultsDelay),
                EasingFunction = sidebarEase
            });
        resultsTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(quick ? 6 : 12, 0, TimeSpan.FromMilliseconds(resultsDuration))
            {
                BeginTime = TimeSpan.FromMilliseconds(resultsDelay),
                EasingFunction = sidebarEase
            });
        await Task.Delay(quick ? 260 : 550);
        _entryAnimationRunning = false;
    }

    private void InitFilters()
    {
        if (_filtersInitialized) return;
        _filtersInitialized = true;
        VersionCombo.Items.Add(new ComboBoxItem { Content = "全部版本", IsSelected = true, Tag = "" });
        LoaderCombo.Items.Add(new ComboBoxItem { Content = "全部加载器", IsSelected = true, Tag = "" });
        foreach (var loader in new[] { "Fabric", "Forge", "NeoForge", "Quilt", "LiteLoader" })
            LoaderCombo.Items.Add(new ComboBoxItem { Content = loader, Tag = loader });

        _ = LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            var mc = new MinecraftService(App.Paths, App.Settings);
            var versions = await Task.Run(() => mc.AvailableVersions(false));
            if (versions.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[ModBrowser] AvailableVersions returned 0 items");
                return;
            }
            var versionIds = new List<string>();
            foreach (var v in versions)
            {
                if (!v.TryGetValue("id", out var idObj)) continue;
                var id = idObj switch
                {
                    string value => value,
                    JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(id))
                    versionIds.Add(id);
            }
            Dispatcher.Invoke(() =>
            {
                _suppressFilterChange = true;
                try
                {
                    foreach (var id in versionIds)
                        VersionCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
                }
                finally
                {
                    _suppressFilterChange = false;
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModBrowser] LoadVersions failed: {ex}");
        }
    }

    private string? SelectedVersion()
    {
        if (VersionCombo.SelectedItem is ComboBoxItem item)
            return item.Tag as string;
        return null;
    }

    private string? SelectedLoader()
    {
        if (LoaderCombo.SelectedItem is ComboBoxItem item)
            return item.Tag as string;
        return null;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterChange) return;
        if (!string.IsNullOrEmpty(SearchBox.Text.Trim()))
            Search_Click(sender, e);
        else
            _ = LoadPopularMods(BeginContentRequest());
    }

    private int BeginContentRequest()
    {
        SearchButton.IsEnabled = true;
        HintText.BeginAnimation(OpacityProperty, null);
        HintText.Opacity = 1;
        _selectedMod = null;
        _detailAnimationGeneration++;
        return ++_contentRequestGeneration;
    }

    private bool IsCurrentContentRequest(int generation) => generation == _contentRequestGeneration;

    private async Task LoadPopularMods(int generation)
    {
        if (!IsCurrentContentRequest(generation)) return;
        _currentPage = 1;
        await LoadPage(generation);
    }

    private async Task LoadPage(int generation)
    {
        if (!IsCurrentContentRequest(generation)) return;
        HintText.Text = "加载中...";
        StartLoadingAnimation();
        ModList.Items.Clear();
        HideDetailImmediately();
        _selectedMod = null;
        CountText.Visibility = Visibility.Collapsed;
        PagerBar.Visibility = Visibility.Collapsed;

        var mode = _resourceMode;
        var selectedCategory = _selectedCategory;
        var mrCategory = selectedCategory?.MrSlug ?? "";
        var cfCategory = selectedCategory?.CfId ?? "";
        var version = SelectedVersion();
        var loader = SelectedLoader();
        var offset = (_currentPage - 1) * _perPage;

        try
        {
            List<ModItem> mods;
            int totalNow;
            var src = App.Settings.Data.ModDownloadSource;
            var isMixed = src is "mixed" or "all";

            var mcmodOnly = mode == ResourceMode.Mods
                            && !isMixed
                            && ParseSource(src) == ModSource.MCmod;
            if (mcmodOnly)
            {
                mods = await McmodService.GetPopularModsAsync(_perPage);
                ApplyChineseNames(mods, mods);
                totalNow = mods.Count;
            }
            else
            {
                var perSource = isMixed
                    ? GetSelectedSources().Where(s => s != ModSource.MCmod).ToList()
                    : new List<ModSource> { ParseSource(src) == ModSource.MCmod
                        ? ModSource.Modrinth
                        : ParseSource(src) };
                if (perSource.Count == 0) perSource.Add(ModSource.Modrinth);

                var tasks = perSource
                    .Select(s => SafePopularPage(s, mode, mrCategory, cfCategory, version, offset, _perPage))
                    .ToList();
                var results = await Task.WhenAll(tasks);
                mods = results.SelectMany(r => r.Items).ToList();
                totalNow = results.Max(r => r.Total);

                if (mode == ResourceMode.Mods)
                {
                    var mcmodMatches = await McmodService.GetPopularModsAsync(60);
                    ApplyChineseNames(mods, mcmodMatches);
                }
            }

            if (!IsCurrentContentRequest(generation)) return;
            _currentMods = mods
                .GroupBy(mod => $"{mod.Source}:{mod.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(mod => mod.Downloads)
                .Take(_perPage)
                .ToList();
            _totalCount = totalNow;

            if (_currentMods.Count > 0)
            {
                HintText.Text = "";
                CountText.Text = $"{GetModeTitle(mode)}精选 · 共 {_totalCount} 个";
                CountText.Visibility = Visibility.Visible;
                foreach (var mod in _currentMods)
                    ModList.Items.Add(CreateModItem(mod));
                _ = FillModMetadataAsync(_currentMods.ToList());
                UpdatePager();
            }
            else
            {
                HintText.Text = $"输入关键词搜索{GetModeTitle(mode)}";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentContentRequest(generation))
                HintText.Text = $"加载{GetModeTitle(mode)}失败: {ex.Message}";
        }
        finally
        {
            StopLoadingAnimation();
            if (IsCurrentContentRequest(generation))
            {
                BrowserContent.Visibility = Visibility.Visible;
                ResultsPanel.BeginAnimation(OpacityProperty, null);
                ResultsPanel.Opacity = 1;
                ResultsPanel.RenderTransform = null;
            }
        }
    }

    private static async Task<(List<ModItem> Items, int Total)> SafePopularPage(
        ModSource source, ResourceMode mode, string mrCategory, string cfCategory, string? gameVersion, int offset, int count)
    {
        var cacheKey = $"{source}|{mode}|{mrCategory}|{cfCategory}|{gameVersion}|{offset}|{count}";
        lock (PopularCacheLock)
        {
            if (PopularCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
                return (cached.Items, cached.Total);
        }

        try
        {
            var request = source switch
            {
                ModSource.Modrinth => ModrinthService.GetPopularPageAsync(
                    count, GetModrinthType(mode), string.IsNullOrEmpty(mrCategory) ? null : mrCategory, offset, gameVersion),
                ModSource.CurseForge => CurseForgeService.GetPopularPageAsync(
                    count, GetCurseForgeClassId(mode), string.IsNullOrEmpty(cfCategory) ? null : cfCategory, offset, gameVersion),
                _ => Task.FromResult((new List<ModItem>(), 0))
            };
            var result = await request.WaitAsync(TimeSpan.FromSeconds(12));
            lock (PopularCacheLock)
                PopularCache[cacheKey] = (DateTime.UtcNow.AddMinutes(5), result.Item1, result.Item2);
            return result;
        }
        catch
        {
            return (new(), 0);
        }
    }

    private static List<ModSource> GetSelectedSources()
    {
        var src = App.Settings.Data.ModDownloadSource;
        if (src == "all")
            return new() { ModSource.Modrinth, ModSource.CurseForge, ModSource.MCmod };
        if (src != "mixed") return new() { ParseSource(src) };

        var saved = App.Settings.Data.ModDownloadSources ?? "";
        var parts = saved.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new() { ModSource.Modrinth };

        var sources = new List<ModSource>();
        foreach (var p in parts)
        {
            var s = ParseSource(p);
            if (!sources.Contains(s)) sources.Add(s);
        }
        return sources;
    }

    private static ModSource ParseSource(string src) => src switch
    {
        "CurseForge" => ModSource.CurseForge,
        "MCmod" => ModSource.MCmod,
        _ => ModSource.Modrinth
    };

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateTo(mainWindow.HomePage);
    }

    public async Task AnimateStandaloneExitAsync(bool quick)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseIn };
        var duration = quick ? 180 : 450;
        var sidebarDistance = quick ? -100 : -240;
        var contentDistance = quick ? 140 : 350;
        var sidebarTransform = new TranslateTransform();
        var contentTransform = new TranslateTransform();
        Sidebar.RenderTransform = sidebarTransform;
        FrameworkElement content = DownloadPage.Visibility == Visibility.Visible
            ? DownloadPage
            : JavaPage.Visibility == Visibility.Visible ? JavaPage : BrowserContent;
        content.RenderTransform = contentTransform;
        sidebarTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, sidebarDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        contentTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, contentDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        await Task.Delay(duration);
    }

    private async void NavSearch_Click(object sender, RoutedEventArgs e) => await SwitchMode(ResourceMode.Mods);
    private async void NavModpacks_Click(object sender, RoutedEventArgs e) => await SwitchMode(ResourceMode.Modpacks);
    private async void NavShaders_Click(object sender, RoutedEventArgs e) => await SwitchMode(ResourceMode.Shaders);
    private async void NavResourcePacks_Click(object sender, RoutedEventArgs e) => await SwitchMode(ResourceMode.ResourcePacks);
    private void NavDownloads_Click(object sender, RoutedEventArgs e)
    {
        BeginContentRequest();
        BrowserContent.Visibility = Visibility.Collapsed;
        JavaPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Visible;
        DownloadPage.Opacity = 0;
        var transform = new TranslateTransform(24, 0);
        DownloadPage.RenderTransform = transform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DownloadPage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private bool _mapPanelWired;

    private void NavMap_Click(object sender, RoutedEventArgs e)
    {
        BeginContentRequest();

        // 切到其他侧栏项时自动收起地图页（只需挂一次）
        if (!_mapPanelWired)
        {
            _mapPanelWired = true;
            foreach (var nav in new[] { NavSearch, NavModpacks, NavShaders, NavResourcePacks, NavJava, NavDownloads, NavSettings })
                nav.Checked += (_, _) => MapPage.Visibility = Visibility.Collapsed;
        }

        BrowserContent.Visibility = Visibility.Collapsed;
        JavaPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Collapsed;
        MapPage.Visibility = Visibility.Visible;
        MapPage.Opacity = 0;
        var transform = new TranslateTransform(24, 0);
        MapPage.RenderTransform = transform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MapPage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });

        if (MapPage.Content == null)
            MapPage.Navigate(new MapDownloadPage());
    }

    private void NavJava_Click(object sender, RoutedEventArgs e)
    {
        BeginContentRequest();
        BrowserContent.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Collapsed;
        JavaPage.Visibility = Visibility.Visible;
        JavaPage.Opacity = 0;
        var transform = new TranslateTransform(24, 0);
        JavaPage.RenderTransform = transform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        JavaPage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private async Task SwitchMode(ResourceMode mode)
    {
        if (_resourceMode == mode && ModList.Items.Count > 0) return;
        var generation = BeginContentRequest();
        JavaPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Collapsed;
        BrowserContent.Visibility = Visibility.Visible;
        try
        {
            _suppressFilterChange = true;
            _resourceMode = mode;
            SearchBox.Clear();
            SearchPlaceholder.Text = $"搜索{GetModeTitle()}...";
            LoaderCombo.IsEnabled = mode == ResourceMode.Mods;
            if (mode != ResourceMode.Mods) LoaderCombo.SelectedIndex = 0;
            _selectedCategory = null;
            ApplyViewStyle();
            BuildCategoryChips();
            _suppressFilterChange = false;
            await AnimateResultsOutAsync();
            if (!IsCurrentContentRequest(generation)) return;
            await LoadPopularMods(generation);
            if (!IsCurrentContentRequest(generation)) return;
            AnimateResultsIn();
        }
        catch (Exception ex)
        {
            if (IsCurrentContentRequest(generation))
                HintText.Text = $"加载{GetModeTitle(mode)}失败: {ex.Message}";
        }
        finally
        {
            _suppressFilterChange = false;
            if (IsCurrentContentRequest(generation))
            {
                BrowserContent.Visibility = Visibility.Visible;
                ResultsPanel.BeginAnimation(OpacityProperty, null);
                ResultsPanel.Opacity = 1;
                ResultsPanel.RenderTransform = null;
            }
        }
    }

    private string GetModeTitle() => GetModeTitle(_resourceMode);

    private static string GetModeTitle(ResourceMode mode) => mode switch
    {
        ResourceMode.Modpacks => "整合包",
        ResourceMode.Shaders => "光影包",
        ResourceMode.ResourcePacks => "材质包",
        _ => "Mods"
    };

    private static string GetModrinthType(ResourceMode mode) => mode switch
    {
        ResourceMode.Modpacks => "modpack",
        ResourceMode.Shaders => "shader",
        ResourceMode.ResourcePacks => "resourcepack",
        _ => "mod"
    };

    private static int GetCurseForgeClassId(ResourceMode mode) => mode switch
    {
        ResourceMode.Modpacks => 4471,
        ResourceMode.Shaders => 6552,
        ResourceMode.ResourcePacks => 12,
        _ => 6
    };

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToMorePage(selectModDownload: true);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Search_Click(sender, e);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var keyword = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword)) return;
        var generation = BeginContentRequest();

        SearchButton.IsEnabled = false;
        await AnimateResultsOutAsync();
        if (!IsCurrentContentRequest(generation)) return;
        HintText.Text = "搜索中...";
        ModList.Items.Clear();
        await CloseDetailAsync();
        _selectedMod = null;
        CountText.Visibility = Visibility.Collapsed;
        StartLoadingAnimation();

        var version = SelectedVersion();
        var loader = SelectedLoader();

        try
        {
            var mode = _resourceMode;
            var src = App.Settings.Data.ModDownloadSource;
            var isMixed = src is "mixed" or "all";
            List<ModItem> mcmodMatches;

            if (isMixed)
            {
                var sources = GetSelectedSources();
                var tasks = sources.Select(s =>
                    SearchSafe(s, keyword, version, loader, mode)).ToList();

                if (mode == ResourceMode.Mods && !sources.Contains(ModSource.MCmod))
                    tasks.Add(SearchSafe(ModSource.MCmod, keyword, version, loader, mode));

                var results = await System.Threading.Tasks.Task.WhenAll(tasks);
                _currentMods = results.SelectMany(r => r).ToList();

                if (mode == ResourceMode.Mods && !sources.Contains(ModSource.MCmod) && results.Length > sources.Count)
                    mcmodMatches = results[^1];
                else
                    mcmodMatches = new List<ModItem>();
            }
            else
            {
                var source = ParseSource(src);
                if (mode != ResourceMode.Mods && source == ModSource.MCmod)
                    source = ModSource.Modrinth;
                _currentMods = await SearchSafe(source, keyword, version, loader, mode);
                mcmodMatches = mode == ResourceMode.Mods && source == ModSource.MCmod
                    ? _currentMods
                    : mode == ResourceMode.Mods
                        ? await SearchSafe(ModSource.MCmod, keyword, null, null, mode)
                        : new List<ModItem>();
            }

            if (!IsCurrentContentRequest(generation)) return;

            ApplyChineseNames(_currentMods, mcmodMatches);
            if (mode == ResourceMode.Mods && mcmodMatches.Count > 0)
                _currentMods = mcmodMatches;
            _currentMods = _currentMods
                .GroupBy(mod => !string.IsNullOrEmpty(mod.McmodId)
                    ? $"mcmod:{mod.McmodId}"
                    : $"{mod.Source}:{mod.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(item => item.Source == ModSource.MCmod)
                    .ThenByDescending(item => item.Downloads)
                    .First())
                .OrderByDescending(mod => ContainsChinese(mod.Name))
                .ToList();

            CountText.Text = _currentMods.Count > 0 ? $"共 {_currentMods.Count} 个结果" : "";
            CountText.Visibility = _currentMods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HintText.Text = _currentMods.Count == 0 ? $"没有找到相关{GetModeTitle(mode)}" : "";

            foreach (var mod in _currentMods)
                ModList.Items.Add(CreateModItem(mod));
            _ = FillModMetadataAsync(_currentMods.ToList());
        }
        catch (Exception ex)
        {
            if (IsCurrentContentRequest(generation))
                HintText.Text = $"搜索失败: {ex.Message}";
        }
        finally
        {
            if (IsCurrentContentRequest(generation))
            {
                StopLoadingAnimation();
                AnimateResultsIn();
                SearchButton.IsEnabled = true;
            }
        }
    }

    private void BuildCategoryChips()
    {
        CategoryPanel.Children.Clear();
        var categories = _resourceMode switch
        {
            ResourceMode.Modpacks => ModpackCategories,
            ResourceMode.Shaders => ShaderCategories,
            ResourceMode.ResourcePacks => ResourcepackCategories,
            _ => ModCategories
        };
        foreach (var category in categories)
        {
            var chip = new Button
            {
                Content = category.Label,
                FontSize = 11,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Tag = category
            };
            chip.SetResourceReference(StyleProperty, "BtnBase");
            ApplyCategoryChipVisual(chip, ReferenceEquals(category, _selectedCategory));
            chip.Click += CategoryChip_Click;
            CategoryPanel.Children.Add(chip);
        }
    }

    private static void ApplyCategoryChipVisual(Button chip, bool selected)
    {
        chip.BorderThickness = selected ? new Thickness(1.6) : new Thickness(1);
        chip.SetResourceReference(
            Control.BorderBrushProperty, selected ? "PrimaryBrush" : "BorderBrush");
        chip.SetResourceReference(
            Control.ForegroundProperty, selected ? "PrimaryBrush" : "TextBrush");
    }

    private async void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button chip || chip.Tag is not CategoryDef category) return;
        var next = ReferenceEquals(category, _selectedCategory) || category.MrSlug == "" && category.CfId == ""
            ? null
            : category;
        if (Equals(next, _selectedCategory)) return;
        var generation = BeginContentRequest();
        _selectedCategory = next;
        BuildCategoryChips();
        await AnimateResultsOutAsync();
        if (!IsCurrentContentRequest(generation)) return;
        await LoadPopularMods(generation);
        if (!IsCurrentContentRequest(generation)) return;
        AnimateResultsIn();
    }

    private void UpdatePager()
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_totalCount / (double)_perPage));
        PagerBar.Visibility = _totalCount > _perPage ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Text = $"第 {_currentPage} / {totalPages} 页";
        FirstPageBtn.IsEnabled = _currentPage > 1;
        PrevPageBtn.IsEnabled = _currentPage > 1;
        NextPageBtn.IsEnabled = _currentPage < totalPages;
        LastPageBtn.IsEnabled = _currentPage < totalPages;
    }

    private async void FirstPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1) return;
        _currentPage = 1;
        await GotoPageAsync(BeginContentRequest());
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1) return;
        _currentPage--;
        await GotoPageAsync(BeginContentRequest());
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_totalCount / (double)_perPage));
        if (_currentPage >= totalPages) return;
        _currentPage++;
        await GotoPageAsync(BeginContentRequest());
    }

    private async void LastPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_totalCount / (double)_perPage));
        if (_currentPage >= totalPages) return;
        _currentPage = totalPages;
        await GotoPageAsync(BeginContentRequest());
    }

    private async Task GotoPageAsync(int generation)
    {
        await AnimateResultsOutAsync();
        if (!IsCurrentContentRequest(generation)) return;
        await LoadPage(generation);
        if (!IsCurrentContentRequest(generation)) return;
        AnimateResultsIn();
    }

    private async Task AnimateResultsOutAsync()
    {
        var fade = new DoubleAnimation(ResultsPanel.Opacity, 0, TimeSpan.FromMilliseconds(120));
        var slide = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var transform = new TranslateTransform();
        ResultsPanel.RenderTransform = transform;
        ResultsPanel.BeginAnimation(OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
        await Task.Delay(120);
        ResultsPanel.BeginAnimation(OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        ResultsPanel.Opacity = 0;
        transform.Y = 8;
    }

    private void StartLoadingAnimation()
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingOverlay.Opacity = 0.96;
        var rotate = RadarSweep.RenderTransform as RotateTransform;
        rotate?.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
        _loadingDotCount = 0;
        _loadingDotsTimer?.Stop();
        _loadingDotsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(360)
        };
        _loadingDotsTimer.Tick += (_, _) =>
        {
            _loadingDotCount = (_loadingDotCount + 1) % 4;
            LoadingDotsText.Text = "连接资源索引" + new string('.', _loadingDotCount);
        };
        _loadingDotsTimer.Start();
        ResultsPanel.BeginAnimation(OpacityProperty, null);
        ResultsPanel.Opacity = 1;
        ResultsPanel.RenderTransform = null;
        HintText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void StopLoadingAnimation()
    {
        _loadingDotsTimer?.Stop();
        _loadingDotsTimer = null;
        (RadarSweep.RenderTransform as RotateTransform)?.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingOverlay.Visibility = Visibility.Collapsed;
        HintText.BeginAnimation(OpacityProperty, null);
        HintText.Opacity = 1;
    }

    private void AnimateResultsIn()
    {
        var transform = new TranslateTransform(0, 10);
        ResultsPanel.RenderTransform = transform;
        ResultsPanel.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260));
        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ResultsPanel.BeginAnimation(OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static void ApplyChineseNames(List<ModItem> mods, List<ModItem> mcmodMatches)
    {
        var aliases = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var mcmod in mcmodMatches)
        {
            var rawName = string.IsNullOrWhiteSpace(mcmod.OriginalName) ? mcmod.Name : mcmod.OriginalName;
            mcmod.OriginalName = rawName;
            mcmod.Name = GetChineseMcmodName(rawName);
            mcmod.McmodId = mcmod.Id;
            mcmod.McmodPageUrl = mcmod.PageUrl;

            foreach (var alias in GetMcmodAliases(mcmod.Name).Concat(GetMcmodAliases(mcmod.OriginalName)))
            {
                var key = NormalizeName(alias);
                if (!string.IsNullOrEmpty(key)) aliases.TryAdd(key, mcmod);
            }
        }

        foreach (var mod in mods.Where(mod => mod.Source != ModSource.MCmod))
        {
            var match = FindAlias(aliases, mod.Name) ?? FindAlias(aliases, mod.Slug);
            if (match == null) continue;
            mod.OriginalName = match.OriginalName;
            mod.Name = match.Name;
            mod.Summary = match.Summary;
            mod.McmodId = match.Id;
            mod.McmodPageUrl = match.PageUrl;
        }
    }

    private static ModItem? FindAlias(Dictionary<string, ModItem> aliases, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (aliases.TryGetValue(NormalizeName(name), out var match)) return match;
        var baseKey = BaseName(name);
        return baseKey.Length > 0 && aliases.TryGetValue(baseKey, out match) ? match : null;
    }

    private static string BaseName(string name)
    {
        var text = Regex.Replace(name, @"^\s*\[[^\]]+\]\s*", "");
        text = Regex.Replace(text, @"[（(][^（）()]*[）)]", " ");
        return NormalizeName(text);
    }

    private static IEnumerable<string> GetMcmodAliases(string name)
    {
        yield return name;

        var bracket = Regex.Match(name, @"^\s*\[([^\]]+)\]");
        if (bracket.Success) yield return bracket.Groups[1].Value;

        var body = Regex.Replace(name, @"^\s*\[[^\]]+\]\s*", "").Trim();
        yield return body;

        var parenthetical = Regex.Match(body, @"[（(]([^（）()]*[A-Za-z][^（）()]*)[）)]\s*$");
        if (parenthetical.Success) yield return parenthetical.Groups[1].Value;
    }

    private static string GetChineseMcmodName(string name)
    {
        var bracket = Regex.Match(name, @"^\s*\[([^\]]+)\]");
        var body = Regex.Replace(name, @"^\s*\[[^\]]+\]\s*", "").Trim();
        var withoutEnglishSuffix = Regex.Replace(body, @"\s*[（(][^（）()]*[A-Za-z][^（）()]*[）)]\s*$", "").Trim();

        if (ContainsChinese(withoutEnglishSuffix)) return withoutEnglishSuffix;
        if (bracket.Success && ContainsChinese(bracket.Groups[1].Value)) return bracket.Groups[1].Value.Trim();
        return name.Trim();
    }

    private static string NormalizeName(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool ContainsChinese(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static async Task FillModMetadataAsync(IReadOnlyList<ModItem> mods)
    {
        var pending = mods
            .Where(mod => mod.Source == ModSource.MCmod
                ? mod.Loaders.Count == 0 && mod.Versions.Count == 0
                : string.IsNullOrWhiteSpace(mod.McmodId))
            .ToList();

        foreach (var chunk in pending.Chunk(3))
        {
            var tasks = chunk.Select(mod => mod.Source == ModSource.MCmod
                ? McmodService.FillSupportAsync(mod)
                : FillChineseNameAsync(mod));
            await Task.WhenAll(tasks);
            await Task.Delay(120);
        }
    }

    private static async Task FillChineseNameAsync(ModItem mod)
    {
        try
        {
            var cacheKey = $"mcmod-name:{mod.Source}:{mod.Id}:{mod.Slug}";
            var cached = ImageCacheService.GetText(cacheKey);
            if (cached == null)
            {
                if (!NameLookupAttempted.TryAdd(cacheKey, true)) return;

                var queries = new List<string>();
                var baseQuery = SanitizeQuery(string.IsNullOrWhiteSpace(mod.OriginalName) ? mod.Name : mod.OriginalName);
                if (baseQuery.Length > 0) queries.Add(baseQuery);
                if (mod.Slug.Length >= 3 && !queries.Contains(mod.Slug, StringComparer.OrdinalIgnoreCase))
                    queries.Add(mod.Slug);

                var responded = false;
                for (var pass = 0; pass < 2 && cached == null; pass++)
                {
                    if (pass > 0) await Task.Delay(2000);
                    foreach (var query in queries)
                    {
                        var results = await McmodService.SearchModsAsync(query, 5);
                        if (results.Count > 0) responded = true;
                        var matched = results.FirstOrDefault(candidate => IsNameMatch(mod, candidate));
                        if (matched == null) continue;

                        cached = $"{matched.Id}|{GetChineseMcmodName(string.IsNullOrWhiteSpace(matched.OriginalName) ? matched.Name : matched.OriginalName)}|{matched.PageUrl}";
                        break;
                    }
                    if (responded) break;
                }

                if (cached != null)
                    ImageCacheService.SetText(cacheKey, cached);
                else if (responded)
                {
                    cached = "";
                    ImageCacheService.SetText(cacheKey, cached);
                }
                else
                {
                    NameLookupAttempted.TryRemove(cacheKey, out _);
                    return;
                }
            }
            if (cached.Length == 0) return;

            var parts = cached.Split('|', 3);
            if (parts.Length < 3) return;

            mod.McmodId = parts[0];
            mod.McmodPageUrl = parts[2];
            if (parts[1].Length > 0)
            {
                if (string.IsNullOrWhiteSpace(mod.OriginalName)) mod.OriginalName = mod.Name;
                mod.Name = parts[1];
            }
        }
        catch
        {
        }
    }

    private static string SanitizeQuery(string name)
    {
        var text = Regex.Replace(name, @"[\[\(（【][^\]\)）】]*[\]\)）】]", " ");
        text = Regex.Replace(text, @"[^\w\s\-\.']", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool IsNameMatch(ModItem mod, ModItem candidate)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { mod.Name, mod.OriginalName, mod.Slug })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            targets.Add(NormalizeName(value));
            targets.Add(BaseName(value));
        }

        foreach (var alias in GetMcmodAliases(candidate.Name).Concat(GetMcmodAliases(candidate.OriginalName)))
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            var normalized = NormalizeName(alias);
            if (normalized.Length < 3) continue;
            if (targets.Contains(normalized) || targets.Contains(BaseName(alias))) return true;
        }
        return false;
    }

    private static async Task<List<ModItem>> SearchSafe(
        ModSource source, string keyword, string? version, string? loader, ResourceMode mode)
    {
        try
        {
            return source switch
            {
                ModSource.Modrinth => await ModrinthService.SearchModsAsync(
                    keyword, version, loader, 25, GetModrinthType(mode)),
                ModSource.CurseForge => await CurseForgeService.SearchModsAsync(
                    keyword, version, loader, 25, GetCurseForgeClassId(mode)),
                ModSource.MCmod when mode == ResourceMode.Mods => await McmodService.SearchModsAsync(keyword),
                _ => new()
            };
        }
        catch
        {
            return new();
        }
    }

    private bool IsGridStyle => App.Settings.Data.ResourceCenterStyle == "grid"
                                && _resourceMode != ResourceMode.Modpacks;

    private void ApplyViewStyle()
    {
        var grid = IsGridStyle;
        if (grid)
        {
            var panel = new FrameworkElementFactory(typeof(UniformGrid));
            panel.SetValue(UniformGrid.ColumnsProperty, 6);
            ModList.ItemsPanel = new ItemsPanelTemplate(panel);
            Grid.SetColumn(ResultsPanel, 0);
            Grid.SetColumnSpan(ResultsPanel, 2);
            Grid.SetRow(DetailPanel, 0);
            Grid.SetRowSpan(DetailPanel, 2);
            Grid.SetColumn(DetailPanel, 0);
            Grid.SetColumnSpan(DetailPanel, 2);
            DetailPanel.Width = double.NaN;
            DetailPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailPanel.Margin = new Thickness(0);
        }
        else
        {
            ModList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            Grid.SetColumn(ResultsPanel, 0);
            Grid.SetColumnSpan(ResultsPanel, 1);
            Grid.SetRow(DetailPanel, 1);
            Grid.SetRowSpan(DetailPanel, 1);
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetColumnSpan(DetailPanel, 1);
            DetailPanel.Width = 420;
            DetailPanel.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private ListBoxItem CreateModItem(ModItem mod)
    {
        if (IsGridStyle)
            return CreateGridModItem(mod);

        var root = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = mod
        };
        root.MouseEnter += (_, _) => root.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        root.MouseLeave += (_, _) => root.Background = Brushes.Transparent;
        root.MouseLeftButtonDown += ModItem_Click;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBorder = new Border
        {
            Width = 44, Height = 44,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var iconImg = new Image
        {
            Width = 38, Height = 38,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0
        };
        var iconFallback = new TextBlock
        {
            Text = GetIconFallback(mod.Name),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconLayer = new Grid();
        iconLayer.Children.Add(iconFallback);
        iconLayer.Children.Add(iconImg);
        LoadModIcon(mod, iconImg, iconFallback);
        iconBorder.Child = iconLayer;
        Grid.SetColumn(iconBorder, 0);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var nameBlock = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2)
        };
        nameBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModItem.Name)) { Source = mod });

        var metaLine = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 3)
        };
        var metaRun = new System.Windows.Documents.Run();
        if (mod.Authors.Count > 0)
        {
            metaRun.Text = string.Join(", ", mod.Authors);
            metaLine.Inlines.Add(metaRun);
        }

        if (mod.Source == ModSource.CurseForge || mod.Source == ModSource.MCmod)
        {
            var srcRun = new System.Windows.Documents.Run
            {
                Text = (metaRun.Text.Length > 0 ? "  ·  " : "") +
                       (mod.Source == ModSource.CurseForge ? "CurseForge" : "MC百科"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0xFF))
            };
            metaLine.Inlines.Add(srcRun);
        }

        var descBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.7,
            MaxHeight = 32,
            TextWrapping = TextWrapping.NoWrap
        };
        descBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModItem.Summary)) { Source = mod });

        var supportBlock = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        supportBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModItem.SupportLabel)) { Source = mod });

        infoStack.Children.Add(nameBlock);
        infoStack.Children.Add(metaLine);
        infoStack.Children.Add(descBlock);
        infoStack.Children.Add(supportBlock);
        Grid.SetColumn(infoStack, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(infoStack);
        root.Child = grid;

        return new ListBoxItem
        {
            Content = root,
            Tag = mod,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private ListBoxItem CreateGridModItem(ModItem mod)
    {
        var card = new Border
        {
            Margin = new Thickness(4, 3, 4, 3),
            Padding = new Thickness(6, 6, 6, 6),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("InputBgBrush"),
            Cursor = Cursors.Hand,
            Tag = mod,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Width = double.NaN
        };
        card.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && !double.IsNaN(args.NewSize.Width))
                card.Height = args.NewSize.Width;
        };
        card.MouseEnter += (_, _) => card.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        card.MouseLeave += (_, _) => card.Background = (Brush)FindResource("InputBgBrush");
        card.MouseLeftButtonDown += ModItem_Click;

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var iconBorder = new Border
        {
            Margin = new Thickness(2, 2, 2, 4),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
        };
        var iconImg = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0
        };
        var fallback = new TextBlock
        {
            Text = GetIconFallback(mod.Name),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconLayer = new Grid();
        iconLayer.Children.Add(fallback);
        iconLayer.Children.Add(iconImg);
        LoadModIcon(mod, iconImg, fallback);
        iconBorder.Child = iconLayer;
        panel.Children.Add(iconBorder);

        var nameBlock = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(2, 0, 2, 1)
        };
        nameBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModItem.Name)) { Source = mod });
        Grid.SetRow(nameBlock, 1);
        panel.Children.Add(nameBlock);

        card.SetBinding(ToolTipProperty, new Binding(nameof(ModItem.SupportLabel)) { Source = mod });

        var metaLine = new TextBlock
        {
            FontSize = 9,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(2, 0, 2, 2)
        };
        if (mod.Authors.Count > 0)
            metaLine.Text = string.Join(", ", mod.Authors);
        else
            metaLine.Text = mod.Source switch
            {
                ModSource.Modrinth => "Modrinth",
                ModSource.CurseForge => "CurseForge",
                ModSource.MCmod => "MC百科",
                _ => ""
            };
        Grid.SetRow(metaLine, 2);
        panel.Children.Add(metaLine);

        card.Child = panel;
        return new ListBoxItem
        {
            Content = card,
            Tag = mod,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static HttpClient CreateIconHttpClient()
    {
        var http = HttpClients.Create(TimeSpan.FromSeconds(12));
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 QuartzLauncher/1.0");
        return http;
    }

    private static string GetIconFallback(string name)
    {
        var text = name.Trim();
        return text.Length > 0 ? text[..1].ToUpperInvariant() : "M";
    }

    private async void LoadModIcon(ModItem mod, Image img, TextBlock fallback)
    {
        try
        {
            var url = mod.IconUrl;
            var cacheKey = $"{mod.Source}:{mod.Id}:{mod.Slug}";
            if (string.IsNullOrEmpty(url))
            {
                url = ImageCacheService.GetResolvedUrl(cacheKey);
                if (string.IsNullOrEmpty(url))
                {
                    url = await ResolveIconUrl(mod);
                    ImageCacheService.StoreResolvedUrl(cacheKey, url);
                }
                mod.IconUrl = url;
            }
            if (string.IsNullOrEmpty(url)) return;

            if (url.StartsWith("//")) url = "https:" + url;

            var bmp = await ImageCacheService.GetAsync(IconHttp, url, 88);
            if (bmp == null) return;

            img.Source = bmp;
            img.Opacity = 1;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private FrameworkElement CreateDetailOverview(string html)
    {
        var contentMatch = Regex.Match(html, @"<main\b[^>]*>(?<content>.*?)</main>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var content = contentMatch.Success ? contentMatch.Groups["content"].Value : html;
        var root = new StackPanel();
        var blocks = Regex.Matches(content,
            @"<p\b[^>]*>.*?</p>|<ul\b[^>]*>.*?</ul>|<ol\b[^>]*>.*?</ol>|<table\b[^>]*>.*?</table>|<img\b[^>]*>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match block in blocks)
        {
            var value = block.Value.Trim();
            if (Regex.IsMatch(value, @"^<p\b", RegexOptions.IgnoreCase))
                root.Children.Add(CreateDetailParagraph(Regex.Replace(value, @"^<p\b[^>]*>|</p>$", "", RegexOptions.IgnoreCase | RegexOptions.Singleline)));
            else if (Regex.IsMatch(value, @"^<(?:ul|ol)\b", RegexOptions.IgnoreCase))
                root.Children.Add(CreateDetailList(value));
            else if (Regex.IsMatch(value, @"^<table\b", RegexOptions.IgnoreCase))
                root.Children.Add(CreateDetailParagraph(CleanMarkup(value)));
            else
                AddDetailImage(root, value);
        }

        if (root.Children.Count == 0)
            root.Children.Add(CreateDetailParagraph(content));
        return root;
    }

    private StackPanel CreateDetailParagraph(string raw)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = Regex.Match(raw,
            @"<span\b[^>]*class=[""'][^""']*\bcommon-text-title\b[^""']*[""'][^>]*>(?<text>.*?)</span>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (title.Success)
        {
            panel.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("PrimaryBrush"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 0, 0, 0),
                Margin = new Thickness(0, 12, 0, 8),
                Child = new TextBlock
                {
                    Text = CleanMarkup(title.Groups["text"].Value),
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextBrush")
                }
            });
            raw = raw.Remove(title.Index, title.Length);
        }

        AddDetailInlineContent(panel, raw);
        return panel;
    }

    private StackPanel CreateDetailList(string raw)
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (Match item in Regex.Matches(raw, @"<li\b[^>]*>(?<content>.*?)</li>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bullet = new TextBlock
            {
                Text = "·",
                FontSize = 18,
                Foreground = (Brush)FindResource("PrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Top
            };
            var itemContent = new StackPanel();
            AddDetailInlineContent(itemContent, item.Groups["content"].Value);
            Grid.SetColumn(bullet, 0);
            Grid.SetColumn(itemContent, 1);
            row.Children.Add(bullet);
            row.Children.Add(itemContent);
            list.Children.Add(row);
        }
        return list;
    }

    private void AddDetailInlineContent(Panel panel, string raw)
    {
        var images = Regex.Matches(raw, @"<img\b(?<attrs>[^>]*)>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var cursor = 0;
        foreach (Match image in images)
        {
            AddDetailText(panel, raw[cursor..image.Index]);
            AddDetailImage(panel, image.Groups["attrs"].Value);
            cursor = image.Index + image.Length;
        }
        AddDetailText(panel, raw[cursor..]);
    }

    private void AddDetailText(Panel panel, string raw)
    {
        var text = CleanMarkup(raw);
        if (string.IsNullOrWhiteSpace(text)) return;
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Opacity = 0.94
        });
    }

    private void AddDetailImage(Panel panel, string imageMarkup)
    {
        var urlMatch = Regex.Match(imageMarkup,
            @"(?:data-src|data-original|src)\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (!urlMatch.Success) return;
        var url = WebUtility.HtmlDecode(urlMatch.Groups["url"].Value.Trim());
        if (url.StartsWith("//")) url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return;

        var image = new Image
        {
            MaxWidth = 900,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 16),
            Opacity = 0
        };
        panel.Children.Add(image);
        LoadDetailImage(uri.AbsoluteUri, image);
    }

    private async void LoadDetailImage(string url, Image image)
    {
        try
        {
            var bitmap = await ImageCacheService.GetAsync(IconHttp, url, 900);
            if (bitmap == null)
            {
                image.Visibility = Visibility.Collapsed;
                return;
            }
            image.Source = bitmap;
            image.Opacity = 1;
        }
        catch
        {
            image.Visibility = Visibility.Collapsed;
        }
    }

    private static string CleanMarkup(string raw)
    {
        var text = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        return text.Trim();
    }

    private static async Task<string> ResolveIconUrl(ModItem mod)
    {
        try
        {
            return mod.Source switch
            {
                ModSource.Modrinth => (await ModrinthService.GetModAsync(mod.Id))?.IconUrl ?? "",
                ModSource.CurseForge when long.TryParse(mod.Id, out var id) =>
                    (await CurseForgeService.GetModAsync(id))?.IconUrl ?? "",
                ModSource.MCmod => await McmodService.GetIconUrlAsync(mod.PageUrl),
                _ => ""
            };
        }
        catch
        {
            return "";
        }
    }

    private void ModItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ModItem mod) return;
        _selectedMod = mod;
        var generation = ++_detailAnimationGeneration;
        OpenDetailPanel();
        DetailFrame.NavigationService.Navigate(null);
        _ = LoadDetail(mod, generation);
    }

    private async void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        await CloseDetailAsync();
        _selectedMod = null;
    }

    private void OpenDetailPanel()
    {
        if (IsGridStyle)
        {
            SearchHeader.Visibility = Visibility.Collapsed;
            ResultsPanel.Visibility = Visibility.Collapsed;
        }

        DetailPanel.BeginAnimation(OpacityProperty, null);
        DetailPanel.Visibility = Visibility.Visible;
        DetailPanel.Opacity = 0;
        var transform = new TransformGroup();
        var isFullDetail = IsGridStyle;
        var scale = new ScaleTransform(isFullDetail ? 1 : 0.97, isFullDetail ? 1 : 0.97);
        var translate = new TranslateTransform(isFullDetail ? 0 : 28, 0);
        transform.Children.Add(scale);
        transform.Children.Add(translate);
        DetailPanel.RenderTransform = transform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DetailPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(isFullDetail ? 1 : 0.97, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(isFullDetail ? 1 : 0.97, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    private async Task CloseDetailAsync()
    {
        if (DetailPanel.Visibility != Visibility.Visible) return;
        var generation = ++_detailAnimationGeneration;
        var transform = DetailPanel.RenderTransform as TransformGroup ?? new TransformGroup();
        if (transform.Children.Count < 2)
        {
            transform.Children.Clear();
            transform.Children.Add(new ScaleTransform(1, 1));
            transform.Children.Add(new TranslateTransform());
            DetailPanel.RenderTransform = transform;
        }
        var scale = (ScaleTransform)transform.Children[0];
        var translate = (TranslateTransform)transform.Children[1];
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        DetailPanel.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.98, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.98, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 22, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        await Task.Delay(160);
        if (generation != _detailAnimationGeneration) return;
        DetailPanel.BeginAnimation(OpacityProperty, null);
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Opacity = 1;
        DetailPanel.RenderTransform = null;
        RestoreBrowserSurface();
    }

    private void HideDetailImmediately()
    {
        DetailPanel.BeginAnimation(OpacityProperty, null);
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Opacity = 1;
        DetailPanel.RenderTransform = null;
        RestoreBrowserSurface();
    }

    private void RestoreBrowserSurface()
    {
        if (!IsGridStyle) return;
        SearchHeader.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Visible;
    }

    private async System.Threading.Tasks.Task LoadDetail(ModItem mod, int generation)
    {
        DetailFrame.NavigationService.Navigate(null);

        if (mod.Source == ModSource.MCmod && string.IsNullOrEmpty(mod.McmodPageUrl))
            mod.McmodPageUrl = mod.PageUrl;

        if (!string.IsNullOrEmpty(mod.McmodPageUrl))
        {
            var versionsTask = GetMcmodDownloadVersions(mod);
            var detailTask = McmodService.GetDetailAsync(mod.McmodPageUrl);
            List<ModVersionItem> mcmodVersions;
            McmodDetail mcmodDetail;
            try { mcmodVersions = await versionsTask; }
            catch { mcmodVersions = new(); }
            try { mcmodDetail = await detailTask; }
            catch { mcmodDetail = new("", "", "", "", new(), "", new(), ""); }

            if (!string.IsNullOrWhiteSpace(mcmodDetail.Name))
                mod.Name = mcmodDetail.Name;
            if (!string.IsNullOrWhiteSpace(mcmodDetail.OriginalName))
                mod.OriginalName = mcmodDetail.OriginalName;
            mod.McmodStatus = mcmodDetail.Status;
            mod.McmodSourceType = mcmodDetail.SourceType;
            mod.McmodInternalInfo = mcmodDetail.InternalInfo;
            mod.DetailImageUrls = mcmodDetail.ImageUrls;
            mod.DetailHtml = mcmodDetail.Html;
            if (!string.IsNullOrWhiteSpace(mcmodDetail.Description))
            {
                mod.Description = RenderDetailText(mcmodDetail.Description);
            }
            else
            {
                var linked = await McmodService.GetOfficialProjectSlugsAsync(mod.McmodPageUrl);
                if (!string.IsNullOrEmpty(linked.ModrinthSlug))
                {
                    var full = await ModrinthService.GetModAsync(linked.ModrinthSlug);
                    if (full != null && !string.IsNullOrWhiteSpace(full.Description))
                        mod.Description = RenderDetailText(full.Description);
                }
                else if (!string.IsNullOrEmpty(linked.CurseForgeSlug))
                {
                    var cfMod = await CurseForgeService.GetModBySlugAsync(linked.CurseForgeSlug);
                    if (cfMod != null && long.TryParse(cfMod.Id, out var cfId))
                    {
                        var full = await CurseForgeService.GetModAsync(cfId);
                        if (full != null && !string.IsNullOrWhiteSpace(full.Description))
                            mod.Description = RenderDetailText(full.Description);
                    }
                }
            }
            if (generation == _detailAnimationGeneration && ReferenceEquals(mod, _selectedMod))
                ShowDetailPage(mod, mcmodVersions);
            return;
        }

        var versions = mod.Source switch
        {
            ModSource.Modrinth => await ModrinthService.GetVersionsAsync(mod.Id),
            ModSource.CurseForge => long.TryParse(mod.Id, out var cfId) ? await CurseForgeService.GetVersionsAsync(cfId) : new List<ModVersionItem>(),
            _ => new List<ModVersionItem>()
        };

        if (mod.Source == ModSource.Modrinth && string.IsNullOrWhiteSpace(mod.Description))
        {
            var full = await ModrinthService.GetModAsync(mod.Id);
            if (full != null && !string.IsNullOrWhiteSpace(full.Description))
                mod.Description = RenderDetailText(full.Description);
        }
        else if (mod.Source == ModSource.CurseForge && string.IsNullOrWhiteSpace(mod.Description)
                 && long.TryParse(mod.Id, out var cfProjectId))
        {
            var full = await CurseForgeService.GetModAsync(cfProjectId);
            if (full != null && !string.IsNullOrWhiteSpace(full.Description))
                mod.Description = RenderDetailText(full.Description);
        }

        if (generation == _detailAnimationGeneration && ReferenceEquals(mod, _selectedMod))
            ShowDetailPage(mod, versions);
    }

    private void ShowDetailPage(ModItem mod, List<ModVersionItem> versions)
    {
        DetailFrame.Navigate(new ResourceDetailPage(mod, versions, DownloadVersionAsync));
    }

    private static string RenderDetailText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw;
        // HTML 来源（CurseForge / 部分源站）：段落与换行 → 空行
        if (Regex.IsMatch(text, @"<[a-zA-Z][^>]*>"))
        {
            text = Regex.Replace(text, @"(?:<br\s*/?>|</p>|</div>|<p[^>]*>|</li>|<li[^>]*>)", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "");
            text = text.Replace("&nbsp;", " ").Replace("&amp;", "&")
                .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
        }
        else
        {
            // Markdown 来源（Modrinth 正文等）
            text = Regex.Replace(text, @"^(#{1,4})\s*", "\n", RegexOptions.Multiline);
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
            text = Regex.Replace(text, @"^\s*[-*]\s+", "  · ", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^\s*\d+\.\s+", "  · ", RegexOptions.Multiline);
            text = Regex.Replace(text, @"!\[[^]]*\]\([^)]*\)", "");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"^\s*---\s*$", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);
        }

        text = Regex.Replace(text, @"\s*\n\s*\n\s*\n+", "\n\n");
        var lines = text.Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) || line.Length == 0);
        var cleaned = new List<string>();
        foreach (var line in lines)
        {
            if (cleaned.Count > 0 && cleaned[^1] == "" && line == "") continue;
            cleaned.Add(line);
        }
        text = string.Join("\n", cleaned).Trim();
        if (text.Length <= 4000) return text;
        return text[..4000].TrimEnd() + "...";
    }

    private async Task<List<ModVersionItem>> GetMcmodDownloadVersions(ModItem mod)
    {
        var linkedProjects = await McmodService.GetOfficialProjectSlugsAsync(mod.McmodPageUrl);
        var linkedVersionTasks = new List<Task<List<ModVersionItem>>>();

        if (!string.IsNullOrEmpty(linkedProjects.ModrinthSlug))
            linkedVersionTasks.Add(ModrinthService.GetVersionsAsync(linkedProjects.ModrinthSlug));
        if (!string.IsNullOrEmpty(linkedProjects.CurseForgeSlug))
        {
            var curseForgeProject = await CurseForgeService.GetModBySlugAsync(linkedProjects.CurseForgeSlug);
            if (curseForgeProject != null && long.TryParse(curseForgeProject.Id, out var linkedCurseForgeId))
                linkedVersionTasks.Add(CurseForgeService.GetVersionsAsync(linkedCurseForgeId));
        }

        if (linkedVersionTasks.Count > 0)
        {
            var linkedResults = await Task.WhenAll(linkedVersionTasks);
            var linkedVersions = linkedResults
                .SelectMany(items => items)
                .OrderByDescending(item => item.DateUploaded)
                .ToList();
            if (linkedVersions.Count > 0) return linkedVersions;
        }

        var aliases = GetMcmodAliases(
                string.IsNullOrWhiteSpace(mod.OriginalName) ? mod.Name : mod.OriginalName)
            .Where(alias => alias.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliases.Count == 0) return new();

        var query = aliases
            .OrderByDescending(alias => alias.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'))
            .First();
        var modrinthTask = ModrinthService.SearchModsAsync(query, null, null, 10);
        var curseForgeTask = CurseForgeService.SearchModsAsync(query, null, null, 10);
        await Task.WhenAll(modrinthTask, curseForgeTask);

        var modrinth = FindBestDownloadMatch(modrinthTask.Result, aliases);
        var curseForge = FindBestDownloadMatch(curseForgeTask.Result, aliases);
        var versionTasks = new List<Task<List<ModVersionItem>>>();

        if (modrinth != null)
            versionTasks.Add(ModrinthService.GetVersionsAsync(modrinth.Id));
        if (curseForge != null && long.TryParse(curseForge.Id, out var curseForgeId))
            versionTasks.Add(CurseForgeService.GetVersionsAsync(curseForgeId));

        if (versionTasks.Count == 0) return new();
        var results = await Task.WhenAll(versionTasks);
        return results
            .SelectMany(items => items)
            .OrderByDescending(item => item.DateUploaded)
            .ToList();
    }

    private static ModItem? FindBestDownloadMatch(IEnumerable<ModItem> candidates, IEnumerable<string> aliases)
    {
        var keys = aliases.Select(NormalizeName).Where(key => key.Length > 1).Distinct().ToList();
        return candidates
            .Select(candidate => new
            {
                Item = candidate,
                Score = keys.Max(key => MatchScore(key, NormalizeName(candidate.Name), NormalizeName(candidate.Slug)))
            })
            .Where(result => result.Score >= 80)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Item.Downloads)
            .Select(result => result.Item)
            .FirstOrDefault();
    }

    private static int MatchScore(string alias, string name, string slug)
    {
        if (alias == name || alias == slug) return 100;
        if (name.StartsWith(alias) || alias.StartsWith(name)) return 90;
        if (alias.Length >= 4 && (name.Contains(alias) || slug.Contains(alias))) return 80;
        return 0;
    }

    private async Task DownloadVersionAsync(ModVersionItem ver)
    {
        if (_resourceMode == ResourceMode.Modpacks)
        {
            await InstallModpackAsync(ver);
            return;
        }

        if (string.IsNullOrEmpty(ver.DownloadUrl)
            && ver.Source == ModSource.CurseForge
            && long.TryParse(ver.ProjectId, out var projectId)
            && long.TryParse(ver.Id, out var fileId))
        {
            ver.DownloadUrl = await CurseForgeService.GetDownloadUrlAsync(projectId, fileId);
        }
        if (string.IsNullOrEmpty(ver.DownloadUrl))
        {
            AnimatedMessageBox.Show(
                ver.Source == ModSource.CurseForge
                    ? "该文件没有可用的 API 下载地址。作者可能禁止了第三方客户端分发，请在 CurseForge 页面下载。"
                    : "没有可用的下载链接");
            return;
        }

        var root = ResolveGameRoot();
        var modeFolder = _resourceMode switch
        {
            ResourceMode.Shaders => "shaderpacks",
            ResourceMode.ResourcePacks => "resourcepacks",
            _ => "mods"
        };
        var customPath = _resourceMode == ResourceMode.Mods
            ? (App.Settings.Data.ModDownloadPath ?? "").Trim()
            : "";
        var targetDir = Path.GetFullPath(string.IsNullOrEmpty(customPath)
            ? Path.Combine(root, modeFolder)
            : customPath);
        Directory.CreateDirectory(targetDir);

        var result = AnimatedMessageBox.Show(
            $"下载 \"{ver.FileName}\" 到:\n{targetDir}\n\n是否继续?", "确认下载",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var dependencyItems = await ResolveDependenciesAsync(ver, targetDir);
        var names = dependencyItems.Count > 0
            ? $"并自动下载 {dependencyItems.Count} 个必需前置 Mod" + "（依赖包的下载会略慢，请稍候）"
            : "";

        var items = new List<DownloadItem>();
        items.Add(new DownloadItem(ver.DownloadUrl, Path.Combine(targetDir, ver.FileName), Size: ver.FileSize));
        items.AddRange(dependencyItems);

        var enqueueName = string.IsNullOrEmpty(names)
            ? ver.FileName
            : $"{ver.FileName} (+{dependencyItems.Count} 个前置 Mod)";
        DownloadManager.Instance.Enqueue(
            enqueueName,
            items,
            workers: 1,
            category: "resource",
            maxAttempts: Math.Max(1, App.Settings.Data.DownloadRetryCount + 1));
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToDownloadCenter(versionOnly: false);
    }

    // 实时解析目标游戏根目录：开启版本隔离用实例独立目录，未开启用公共 .minecraft
    private string ResolveGameRoot()
    {
        if (!string.IsNullOrWhiteSpace(_instanceRoot))
            return _instanceRoot!;

        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            var instance = mainWindow.HomePage?.SelectedInstance;
            if (instance != null)
                return InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
        }
        return App.Paths.MinecraftDir;
    }

    // 从候选版本中挑选“适配版本”：优先 游戏版本 + 加载器 都匹配，其次仅游戏版本，最后取最新
    private static ModVersionItem? PickBestCompatible(IEnumerable<ModVersionItem> versions, string gameVersion, string loader)
    {
        var list = versions.Where(v => !string.IsNullOrWhiteSpace(v.DownloadUrl)).ToList();
        if (list.Count == 0) return null;

        bool GameVersionMatch(ModVersionItem v) =>
            string.IsNullOrEmpty(gameVersion) || v.GameVersions.Count == 0
            || v.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase);

        bool LoaderMatch(ModVersionItem v) =>
            string.IsNullOrEmpty(loader) || v.Loaders.Count == 0
            || v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);

        return list.Where(v => GameVersionMatch(v) && LoaderMatch(v)).OrderByDescending(v => v.DateUploaded).FirstOrDefault()
               ?? list.Where(GameVersionMatch).OrderByDescending(v => v.DateUploaded).FirstOrDefault()
               ?? list.OrderByDescending(v => v.DateUploaded).FirstOrDefault();
    }

    private static bool IsCompatible(ModVersionItem version, string gameVersion, string loader)
        => (string.IsNullOrEmpty(gameVersion) || version.GameVersions.Count == 0
            || version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
           && (string.IsNullOrEmpty(loader) || version.Loaders.Count == 0
               || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private sealed record ResolvedDependency(
        string DownloadUrl,
        string FileName,
        string Sha1,
        long FileSize,
        List<ModDependency> Dependencies);

    private async Task<List<DownloadItem>> ResolveDependenciesAsync(ModVersionItem root, string targetDir)
    {
        var results = new List<DownloadItem>();
        var pending = new Queue<(ModDependency Dependency, ModVersionItem Parent)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in root.Dependencies.Where(item => item.Type.Equals("required", StringComparison.OrdinalIgnoreCase)))
            pending.Enqueue((dependency, root));

        while (pending.Count > 0)
        {
            var (dependency, parent) = pending.Dequeue();
            var key = $"{dependency.Source}:{dependency.ProjectId}:{dependency.VersionId}";
            if (!visited.Add(key)) continue;

            try
            {
                var resolved = dependency.Source == ModSource.Modrinth
                    ? await ResolveModrinthDependencyAsync(dependency, parent)
                    : await ResolveCurseForgeDependencyAsync(dependency, parent);
                if (resolved == null) continue;
                var target = Path.Combine(targetDir, resolved.FileName);
                if (!File.Exists(target))
                    results.Add(new DownloadItem(resolved.DownloadUrl, target, resolved.Sha1, resolved.FileSize));

                foreach (var child in resolved.Dependencies.Where(item =>
                             item.Type.Equals("required", StringComparison.OrdinalIgnoreCase)))
                    pending.Enqueue((child, new ModVersionItem
                    {
                        GameVersion = parent.GameVersion,
                        Loader = parent.Loader,
                        GameVersions = parent.GameVersions,
                        Loaders = parent.Loaders
                    }));
            }
            catch
            {
                // 单个前置解析失败不阻塞主文件下载。
            }
        }
        return results;
    }

    private static async Task<ResolvedDependency?> ResolveModrinthDependencyAsync(ModDependency dependency, ModVersionItem ver)
    {
        // 依赖指定的精确版本：仅在适配当前游戏版本/加载器时使用
        if (!string.IsNullOrEmpty(dependency.VersionId))
        {
            var exact = await ModrinthService.GetVersionAsync(dependency.VersionId);
            if (exact != null && !string.IsNullOrEmpty(exact.DownloadUrl)
                && IsCompatible(exact, ver.GameVersion, ver.Loader))
                return new ResolvedDependency(exact.DownloadUrl, exact.FileName, exact.Sha1, exact.FileSize, exact.Dependencies);
        }

        // 否则取该游戏版本 + 加载器下最新的适配版本（而不是全局最新版）
        var fallback = await ModrinthService.GetVersionsAsync(dependency.ProjectId, ver.GameVersion, ver.Loader);
        var match = PickBestCompatible(fallback, ver.GameVersion, ver.Loader);
        return match == null
            ? null
            : new ResolvedDependency(match.DownloadUrl, match.FileName, match.Sha1, match.FileSize, match.Dependencies);
    }

    private static async Task<ResolvedDependency?> ResolveCurseForgeDependencyAsync(ModDependency dependency, ModVersionItem ver)
    {
        if (!long.TryParse(dependency.ProjectId, out var modId)) return null;
        var versions = await CurseForgeService.GetVersionsAsync(modId, ver.GameVersion);
        var candidates = versions.Where(item => item.Source == ModSource.CurseForge).ToList();
        var match = PickBestCompatible(candidates, ver.GameVersion, ver.Loader);
        if (match == null) return null;
        var url = match.DownloadUrl;
        if (string.IsNullOrEmpty(url)
            && long.TryParse(match.ProjectId, out var dependencyProjectId)
            && long.TryParse(match.Id, out var dependencyFileId))
        {
            url = await CurseForgeService.GetDownloadUrlAsync(dependencyProjectId, dependencyFileId);
        }
        return string.IsNullOrEmpty(url)
            ? null
             : new ResolvedDependency(url, match.FileName, match.Sha1, match.FileSize, match.Dependencies);
    }

    private static bool MatchesGameVersion(ModVersionItem item, string version)
        => string.IsNullOrEmpty(version)
           || item.GameVersions.Count == 0
           || item.GameVersions.Contains(version, StringComparer.OrdinalIgnoreCase);

    private static bool MatchesLoader(ModVersionItem item, string loader)
        => string.IsNullOrEmpty(loader)
           || item.Loaders.Count == 0
           || item.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);

    private async Task InstallModpackAsync(ModVersionItem ver)
    {
        if (string.IsNullOrEmpty(ver.DownloadUrl)
            && ver.Source == ModSource.CurseForge
            && long.TryParse(ver.ProjectId, out var projectId)
            && long.TryParse(ver.Id, out var fileId))
        {
            ver.DownloadUrl = await CurseForgeService.GetDownloadUrlAsync(projectId, fileId);
        }
        if (string.IsNullOrEmpty(ver.DownloadUrl))
        {
            AnimatedMessageBox.Show(
                ver.Source == ModSource.CurseForge
                    ? "该整合包没有可用的 API 下载地址。作者可能禁止了第三方客户端分发，请在 CurseForge 页面下载。"
                    : "没有可用的下载链接");
            return;
        }

        var dialog = new RenameInstanceDialog(_selectedMod?.Name ?? "整合包") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InstanceName)) return;

        var target = Path.Combine(App.Paths.TempDir,
            $"{Guid.NewGuid():N}-{Path.GetFileName(ver.FileName)}");
        Directory.CreateDirectory(App.Paths.TempDir);
        AnimatedMessageBox.Show(
            $"将下载并安装整合包\n\n文件名: {ver.FileName}\n大小: {FormatFileSize(ver.FileSize)}\n\n下载完成后会在后台自动安装，请稍候。",
            "安装整合包", MessageBoxButton.OK, MessageBoxImage.Information);

        var item = new DownloadItem(ver.DownloadUrl, target, ver.Sha1, ver.FileSize);
        var task = DownloadManager.Instance.Enqueue(
            $"下载整合包 · {ver.FileName}", new List<DownloadItem> { item },
            workers: 1, category: "resource", maxAttempts: Math.Max(1, App.Settings.Data.DownloadRetryCount + 1));
        if (task.Tcs != null) await task.Tcs.Task;
        if (task.Status != DownloadTaskStatus.Completed || !File.Exists(target))
        {
            AnimatedMessageBox.Show($"整合包下载失败: {task.Error}", "安装整合包", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var progress = new Progress<string>(message => HintText.Text = message);
        try
        {
            var instance = await ModpackInstaller.InstallAsync(target, App.Paths, App.Settings, progress);
            TryDeleteTemp(target);
            AnimatedMessageBox.Show(
                $"整合包已安装为实例「{instance.Name}」！\n\nMinecraft: {instance.McVersion}\n加载器: {instance.Loader}",
                "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Window.GetWindow(this) is MainWindow mainWindow)
                mainWindow.NavigateTo(mainWindow.HomePage);
        }
        catch (Exception ex)
        {
            AnimatedMessageBox.Show($"整合包安装失败: {ex.Message}", "安装整合包", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            ExternalOpenService.OpenUrl(url);
        }
        catch { }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}
