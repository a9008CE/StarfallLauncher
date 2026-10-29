using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class WebsiteSitesPage : Page
{
    private static readonly HttpClient IconHttp = CreateIconClient();
    private static readonly Dictionary<string, BitmapImage> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>星落自己的站点：官网 / GitHub / 爱发电（爱发电用它的品牌紫）。</summary>
    private static readonly WebsiteShortcut[] StarfallSites =
    {
        new() { Name = "星落官网", Url = "https://starfallmc.top/", Icon = "" },
        new() { Name = "GitHub 仓库", Url = "https://github.com/a9008CE/StarfallLauncher", Icon = "", IconUrl = "https://github.com/favicon.ico" },
        new() { Name = "爱发电", Url = "https://afdian.com/a/2840109628QQ", Icon = "", IconUrl = "https://afdian.com/favicon.ico" }
    };

    private static readonly WebsiteShortcut[] ResourceSites =
    {
        new() { Name = "Minecraft 官网", Url = "https://www.minecraft.net/", Icon = "\uE7FC", IconUrl = "https://www.minecraft.net/etc.clientlibs/minecraft/clientlibs/main/resources/favicon.ico" },
        new() { Name = "Modrinth", Url = "https://modrinth.com/", Icon = "\uE7B8", IconUrl = "https://modrinth.com/favicon.ico" },
        new() { Name = "CurseForge", Url = "https://www.curseforge.com/minecraft", Icon = "\uE896", IconUrl = "https://www.curseforge.com/favicon.ico" },
        new() { Name = "MC 百科", Url = "https://www.mcmod.cn/", Icon = "\uE82D", IconUrl = "https://www.mcmod.cn/static/public/images/favicon.ico" }
    };

    private static readonly WebsiteShortcut[] LauncherSites =
    {
        new() { Name = "Minecraft 官网", Url = "https://www.minecraft.net/", Icon = "\uE7FC", IconUrl = "https://www.minecraft.net/etc.clientlibs/minecraft/clientlibs/main/resources/favicon.ico" },
        new() { Name = "Microsoft 账户", Url = "https://account.microsoft.com/", Icon = "\uE77B", IconUrl = "https://www.microsoft.com/favicon.ico" }
    };

    public WebsiteSitesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildFixedSites(StarfallSitesPanel, StarfallSites);
            BuildFixedSites(ResourceSitesPanel, ResourceSites);
            BuildFixedSites(LauncherSitesPanel, LauncherSites);
            BuildCustomSites();
        };
    }

    private void BuildFixedSites(Panel panel, IEnumerable<WebsiteShortcut> sites)
    {
        panel.Children.Clear();
        foreach (var site in sites) panel.Children.Add(CreateSiteCard(site, false));
    }

    private void BuildCustomSites()
    {
        CustomSitesPanel.Children.Clear();
        foreach (var site in App.Settings.Data.WebsiteShortcuts)
            CustomSitesPanel.Children.Add(CreateSiteCard(site, true));
    }

    private Border CreateSiteCard(WebsiteShortcut site, bool removable)
    {
        var card = new Border
        {
            Width = 190,
            MinHeight = 62,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = site
        };
        if (removable)
        {
            card.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(6);
        }
        else
        {
            card.SetResourceReference(StyleProperty, "CardStyle");
        }
        card.MouseLeftButtonUp += (_, args) =>
        {
            if (FindParent<Button>(args.OriginalSource as DependencyObject) == null) OpenSite(site.Url);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconFallback = new TextBlock
        {
            Text = string.IsNullOrEmpty(site.Icon) ? "\uE774" : site.Icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (site.Url.Contains("afdian.com", StringComparison.OrdinalIgnoreCase))
        {
            // 爱发电品牌紫，和普通站点区分开
            iconFallback.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0x6C, 0xE6));
        }
        else
        {
            iconFallback.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        }
        var iconImage = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0
        };
        var iconLayer = new Grid { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left };
        iconLayer.Children.Add(iconFallback);
        iconLayer.Children.Add(iconImage);
        LoadSiteIcon(site, iconImage, iconFallback);

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = site.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var url = new TextBlock
        {
            Text = site.Url,
            FontSize = 10,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 5, 0, 0)
        };
        url.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        content.Children.Add(title);
        content.Children.Add(url);
        Grid.SetColumn(content, 1);
        grid.Children.Add(iconLayer);
        grid.Children.Add(content);

        if (removable)
        {
            var remove = new Button
            {
                Content = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = site,
                ToolTip = "删除站点"
            };
            remove.SetResourceReference(Control.ForegroundProperty, "TextMutedBrush");
            remove.Click += RemoveSite_Click;
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
        }

        card.Child = grid;
        return card;
    }

    private void AddSite_Click(object sender, RoutedEventArgs e)
    {
        var name = SiteNameBox.Text.Trim();
        var url = SiteUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            AnimatedMessageBox.Show("请输入名称和有效的 HTTP/HTTPS 网址。", "站点地址");
            return;
        }

        App.Settings.Data.WebsiteShortcuts.Add(new WebsiteShortcut
        {
            Name = name,
            Url = url,
            Icon = "\uE774",
            IconUrl = new Uri(url).GetLeftPart(UriPartial.Authority) + "/favicon.ico"
        });
        App.Settings.Save();
        SiteNameBox.Clear();
        SiteUrlBox.Clear();
        BuildCustomSites();
    }

    private void RemoveSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WebsiteShortcut site })
        {
            App.Settings.Data.WebsiteShortcuts.Remove(site);
            App.Settings.Save();
            BuildCustomSites();
        }
    }

    private static void OpenSite(string url)
    {
        try { ExternalOpenService.OpenUrl(url); }
        catch { }
    }

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T found) return found;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static async void LoadSiteIcon(WebsiteShortcut site, Image image, TextBlock fallback)
    {
        var url = site.IconUrl;
        if (string.IsNullOrEmpty(url) && Uri.TryCreate(site.Url, UriKind.Absolute, out var siteUri))
            url = siteUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico";
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            if (!IconCache.TryGetValue(url, out var bitmap))
            {
                var bytes = await IconHttp.GetByteArrayAsync(url);
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 40;
                bitmap.EndInit();
                bitmap.Freeze();
                IconCache[url] = bitmap;
            }
            image.Source = bitmap;
            image.Opacity = 1;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private static HttpClient CreateIconClient()
    {
        var client = HttpClients.Create(TimeSpan.FromSeconds(10));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 QuartzLauncher/1.0");
        return client;
    }
}
