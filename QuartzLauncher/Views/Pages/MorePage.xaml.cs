using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace QuartzLauncher.Views.Pages;

public partial class MorePage : Page, IStandaloneSidebarPage
{
    public string? InitialTab { get; set; }
    private int _contentNavigationGeneration;

    public MorePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MoreBar.BeginAnimation(OpacityProperty, null);
        MoreFrame.BeginAnimation(OpacityProperty, null);
        MoreBar.Opacity = 0;
        MoreBar.RenderTransform = new TranslateTransform(-260, 0);
        MoreFrame.Opacity = 0;
        MoreFrame.RenderTransform = new TranslateTransform(300, 0);

        if (MoreFrame.Content != null)
        {
            SelectMoreNavButton(MoreFrame.Content);
        }
        else if (InitialTab == "ModDownload")
        {
            FooterHint.Visibility = Visibility.Collapsed;
            NavModDownload.IsChecked = true;
            MoreFrame.Navigate(new ModDownloadSettingsPage());
        }
        else if (InitialTab == "DownloadVersion")
        {
            FooterHint.Visibility = Visibility.Collapsed;
            NavPreset5.IsChecked = true;
            MoreFrame.Navigate(new DownloadCenterPage(true));
        }
        else if (InitialTab == "Download")
        {
            FooterHint.Visibility = Visibility.Collapsed;
            NavPreset5.IsChecked = true;
            MoreFrame.Navigate(new DownloadCenterPage());
        }

        else
        {
            FooterHint.Visibility = Visibility.Collapsed;
            MoreFrame.Navigate(new ThemeDetailPage());
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, AnimateIn);
    }

    internal void AnimateIn()
    {
        var style = App.Settings.Data.PageAnimationStyle;
        if (style is "none" or "tear")
        {
            MoreBar.Opacity = 1;
            MoreFrame.Opacity = 1;
            MoreBar.RenderTransform = null;
            MoreFrame.RenderTransform = null;
            return;
        }
        var quick = style == "quick";
        var duration = quick ? 180 : 450;
        var barFrom = quick ? -100 : -260;
        var contentFrom = quick ? 140 : 350;
        var barSlide = new DoubleAnimation(barFrom, 0, TimeSpan.FromMilliseconds(duration));
        barSlide.EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut };
        var barTransform = new TranslateTransform(-260, 0);
        MoreBar.RenderTransform = barTransform;
        barTransform.BeginAnimation(TranslateTransform.XProperty, barSlide);
        MoreBar.Opacity = 1;

        var contentSlide = new DoubleAnimation(contentFrom, 0, TimeSpan.FromMilliseconds(duration));
        contentSlide.EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut };
        var contentTransform = new TranslateTransform(350, 0);
        MoreFrame.RenderTransform = contentTransform;
        contentTransform.BeginAnimation(TranslateTransform.XProperty, contentSlide);
        MoreFrame.Opacity = 1;
    }

    public async Task AnimateStandaloneExitAsync(bool quick)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseIn };
        var duration = quick ? 180 : 450;
        var sidebarDistance = quick ? -100 : -260;
        var contentDistance = quick ? 140 : 350;
        var barTransform = new TranslateTransform();
        var contentTransform = new TranslateTransform();
        MoreBar.RenderTransform = barTransform;
        MoreFrame.RenderTransform = contentTransform;
        barTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, sidebarDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        contentTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, contentDistance, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        await Task.Delay(duration);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateTo(mainWindow.HomePage);
    }

    private void NavigateContent(object page)
        => _ = NavigateContentAsync(page, ++_contentNavigationGeneration);

    private void SetFooterHint(bool visible)
        => FooterHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private async Task NavigateContentAsync(object page, int generation)
    {
        MoreFrame.BeginAnimation(OpacityProperty, null);
        MoreFrame.Opacity = 1;
        MoreFrame.IsHitTestVisible = false;
        var transform = new TranslateTransform();
        MoreFrame.RenderTransform = transform;
        var slideOut = new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(110));
        transform.BeginAnimation(TranslateTransform.XProperty, slideOut);
        await Task.Delay(110);
        if (generation != _contentNavigationGeneration) return;

        MoreFrame.Navigate(page);
        SelectMoreNavButton(page);
        MoreFrame.Opacity = 1;
        var nextTransform = new TranslateTransform();
        MoreFrame.RenderTransform = nextTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        nextTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        await Task.Delay(240);
        if (generation != _contentNavigationGeneration) return;
        MoreFrame.BeginAnimation(OpacityProperty, null);
        MoreFrame.Opacity = 1;
        MoreFrame.RenderTransform = null;
        MoreFrame.IsHitTestVisible = true;
    }

    private void SelectMoreNavButton(object page)
    {
        NavTheme.IsChecked = page is ThemeDetailPage;
        NavModDownload.IsChecked = page is ModDownloadSettingsPage;
        NavPreset3.IsChecked = page is AnimationSettingsPage;
        NavPreset4.IsChecked = page is WebsiteSitesPage;
        NavPreset5.IsChecked = page is DownloadCenterPage;
        NavPreset6.IsChecked = page is SkinPreviewPage or SkinLibraryPage;
    }

    private void NavTheme_Click(object sender, RoutedEventArgs e) { SetFooterHint(false); NavigateContent(new ThemeDetailPage()); }
    private void NavModDownload_Click(object sender, RoutedEventArgs e) { SetFooterHint(false); NavigateContent(new ModDownloadSettingsPage()); }
    private void NavPreset3_Click(object sender, RoutedEventArgs e) { SetFooterHint(false); NavigateContent(new AnimationSettingsPage()); }
    private void NavPreset4_Click(object sender, RoutedEventArgs e) { SetFooterHint(false); NavigateContent(new WebsiteSitesPage()); }
    private void NavPreset5_Click(object sender, RoutedEventArgs e) { SetFooterHint(false); NavigateContent(new DownloadCenterPage(true)); }
    private void NavPreset6_Click(object sender, RoutedEventArgs e) { SetFooterHint(true); NavigateContent(new SkinPreviewPage()); }

    public void NavigateToSkinLibrary() { SetFooterHint(true); NavigateContent(new SkinLibraryPage()); }
    public void NavigateToSkinPreview() { SetFooterHint(true); NavigateContent(new SkinPreviewPage()); }
}
