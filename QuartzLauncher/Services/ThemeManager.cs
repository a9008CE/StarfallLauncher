#if !FULL_BUILD
using System.Windows;
using System.Windows.Media;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

/// <summary>
/// 【开源版】精简主题管理：仅提供一套默认深浅配色，可切换深浅色。
/// 完整版的多套 UI 风格、毛玻璃/赛博朋克/流浪地球材质、遮罩过渡动画等未包含在开源内容中。
/// </summary>
public class ThemeManager
{
    private readonly SettingsService _settings;
    public Theme Current { get; private set; }

    private ResourceDictionary? _colorDict;
    private bool _stylesLoaded;

    public ThemeManager(SettingsService settings)
    {
        _settings = settings;
        Current = ResolveTheme();
    }

    /// <summary>为 true 时跳过内置过渡动画（供外部动画接管）。</summary>
    public bool SuppressTransition { get; set; }

    private Theme ResolveTheme()
    {
        var s = _settings.Data;
        var presets = Theme.GetPresets(s.UiStyle, s.ThemeMode);
        var theme = presets.TryGetValue(s.ThemeName, out var t)
            ? t
            : presets.Values.FirstOrDefault() ?? new Theme();

        if (!string.IsNullOrEmpty(s.ThemePrimary)) theme.Primary = s.ThemePrimary;
        if (!string.IsNullOrEmpty(s.ThemeBg)) theme.Bg = s.ThemeBg;
        if (!string.IsNullOrEmpty(s.ThemeCard)) theme.Card = s.ThemeCard;
        if (!string.IsNullOrEmpty(s.ThemeText)) theme.Text = s.ThemeText;
        if (!string.IsNullOrEmpty(s.ThemeTextMuted)) theme.TextMuted = s.ThemeTextMuted;
        if (!string.IsNullOrEmpty(s.ThemeSidebar)) theme.Sidebar = s.ThemeSidebar;
        if (!string.IsNullOrEmpty(s.ThemeBorder)) theme.Border = s.ThemeBorder;
        if (!string.IsNullOrEmpty(s.ThemeDanger)) theme.Danger = s.ThemeDanger;
        return theme;
    }

    public void Apply()
    {
        Current = ResolveTheme();
        var t = Current;
        var app = Application.Current;
        if (app == null) return;

        var isLight = _settings.Data.ThemeMode == "light";
        var inputBg = Darken(t.Bg, 5);
        var navActive = Mix(t.Sidebar, t.Primary, 0.16);
        var font = new FontFamily("Microsoft YaHei UI, Segoe UI");

        var rd = new ResourceDictionary
        {
            ["BgColor"] = ToColor(t.Bg),
            ["CardColor"] = ToColor(t.Card),
            ["SidebarColor"] = ToColor(t.Sidebar),
            ["BorderColor"] = ToColor(t.Border),
            ["TextColor"] = ToColor(t.Text),
            ["TextMutedColor"] = ToColor(t.TextMuted),
            ["PrimaryColor"] = ToColor(t.Primary),
            ["DangerColor"] = ToColor(t.Danger),
            ["SuccessColor"] = ToColor(t.Success),
            ["InputBgColor"] = ToColor(inputBg),
            ["NavActiveBgColor"] = ToColor(navActive),
            ["NavActiveBgBrushColor"] = ToColor(navActive),
            ["InputBgBrushColor"] = ToColor(inputBg),
            ["CardShadowColor"] = ToColor(t.CardShadowColor),

            ["BgBrush"] = ToBrush(t.Bg),
            ["WindowBackgroundBrush"] = ToBrush(t.Bg),
            ["ContentBackgroundBrush"] = ToBrush(t.Bg),
            ["CardBrush"] = ToBrush(t.Card),
            ["DialogCardBrush"] = ToBrush(t.Card),
            ["CardSurfaceBrush"] = ToBrush(t.Card),
            ["SidebarBrush"] = ToBrush(t.Sidebar),
            ["BorderBrush"] = ToBrush(t.Border),
            ["TextBrush"] = ToBrush(t.Text),
            ["TextMutedBrush"] = ToBrush(t.TextMuted),
            ["PrimaryBrush"] = ToBrush(t.Primary),
            ["DangerBrush"] = ToBrush(t.Danger),
            ["SuccessBrush"] = ToBrush(t.Success),
            ["NavActiveBgBrush"] = ToBrush(navActive),
            ["NavIconBrush"] = ToBrush(t.TextMuted),

            ["GlassControlBrush"] = Brushes.Transparent,
            ["GlassControlHoverBrush"] = ToBrush(Lighten(t.Bg, 5)),
            ["GlassControlBorderBrush"] = ToBrush(t.Border),
            ["GlassPrimaryBrush"] = ToBrush(t.Primary),
            ["GlassPrimaryHoverBrush"] = ToBrush(Lighten(t.Primary, 12)),
            ["GlassPrimaryBorderBrush"] = ToBrush(t.Primary),
            ["GlassDangerBrush"] = ToBrush(t.Danger),
            ["GlassDangerHoverBrush"] = ToBrush(Lighten(t.Danger, 12)),
            ["GlassDangerBorderBrush"] = ToBrush(t.Danger),
            ["GlassBorderBrush"] = ToBrush(t.Border),
            ["GlassHighlightBrush"] = Brushes.Transparent,

            ["GuideDimBrush"] = new SolidColorBrush(
                _settings.Data.ThemeMode == "light"
                    ? Color.FromArgb(150, 12, 16, 26)
                    : Color.FromArgb(188, 4, 6, 12)),

            ["PrimaryButtonForeground"] = Brushes.White,
            ["DangerButtonForeground"] = Brushes.White,

            ["ReadablePanelBrush"] = ToBrush(t.Card),
            ["ReadableControlBrush"] = ToBrush(inputBg),
            ["ReadablePopupBrush"] = ToBrush(t.Card),
            ["ReadableItemBrush"] = ToBrush(inputBg),

            ["WindowAccentBorderBrush"] = ToBrush(t.Border),
            ["CyberpunkHudBrush"] = Brushes.Transparent,
            ["WanderingEarthHudBrush"] = Brushes.Transparent,
            ["CyberpunkVisibility"] = Visibility.Collapsed,
            ["WanderingEarthVisibility"] = Visibility.Collapsed,
            ["WanderingEarthHudHeight"] = 0d,

            ["UiFontFamily"] = font,
            ["UiDisplayFontFamily"] = font,
            ["BrandText"] = "星落 Launcher",
            ["CardPadding"] = new Thickness(20),
            ["PageTitleFontSize"] = 30d,
            ["SidebarWidth"] = (double)t.SidebarWidth,
            ["TopBarHeight"] = 40d,
            ["BottomBarHeight"] = 64d,
            ["NavTextAlignment"] = _settings.Data.SidebarTextLeftAligned
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Center,

            ["CardCornerRadius"] = new CornerRadius(Math.Max(8, t.CardCornerRadius)),
            ["ButtonCornerRadius"] = new CornerRadius(Math.Max(6, t.ButtonCornerRadius)),
            ["InputCornerRadius"] = new CornerRadius(Math.Max(6, t.InputCornerRadius)),
            ["NavButtonHeight"] = t.NavButtonHeight,
            ["CardShadowDepth"] = t.CardShadowDepth,
            ["CardShadowBlur"] = t.CardShadowBlur,
            ["CardShadowOpacity"] = t.CardShadowOpacity,
            ["CardBorderThickness"] = new Thickness(t.CardBorderThickness)
        };

        if (_colorDict != null)
            app.Resources.MergedDictionaries.Remove(_colorDict);
        _colorDict = rd;
        app.Resources.MergedDictionaries.Insert(0, rd);

        if (_stylesLoaded) return;
        _stylesLoaded = true;
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Styles.xaml")
            });
        }
        catch
        {
        }
    }

    public void SelectPreset(string name)
    {
        _settings.Data.ThemeName = name;
        _settings.Data.ThemePrimary = "";
        _settings.Data.ThemeBg = "";
        _settings.Data.ThemeCard = "";
        _settings.Data.ThemeText = "";
        _settings.Data.ThemeTextMuted = "";
        _settings.Data.ThemeSidebar = "";
        _settings.Data.ThemeBorder = "";
        _settings.Data.ThemeDanger = "";
        _settings.Data.ThemeBackgroundImage = "";
        Apply();
    }

    public void SetMode(string mode)
    {
        _settings.Data.ThemeMode = mode;
        Apply();
    }

    public void SetUiStyle(string style)
    {
        _settings.Data.UiStyle = style;
        var presets = Theme.GetPresets(style, _settings.Data.ThemeMode);
        _settings.Data.ThemeName = presets.Keys.FirstOrDefault() ?? "默认";
        Apply();
    }

    public void SetHueShift(double degrees)
    {
    }

    public void ClearHueShift()
    {
    }

    public static string RotateHue(string hex, double degrees) => hex;

    public static string Darken(string hex, int amount)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return $"#{Clamp(c.R - amount):X2}{Clamp(c.G - amount):X2}{Clamp(c.B - amount):X2}";
        }
        catch
        {
            return hex;
        }
    }

    public static string Lighten(string hex, int amount)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return $"#{Clamp(c.R + amount):X2}{Clamp(c.G + amount):X2}{Clamp(c.B + amount):X2}";
        }
        catch
        {
            return hex;
        }
    }

    public static string Mix(string baseHex, string tintHex, double ratio)
    {
        try
        {
            var a = (Color)ColorConverter.ConvertFromString(baseHex);
            var b = (Color)ColorConverter.ConvertFromString(tintHex);
            var r = (byte)Math.Round(a.R + (b.R - a.R) * ratio);
            var g = (byte)Math.Round(a.G + (b.G - a.G) * ratio);
            var bl = (byte)Math.Round(a.B + (b.B - a.B) * ratio);
            return $"#{r:X2}{g:X2}{bl:X2}";
        }
        catch
        {
            return baseHex;
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static Color ToColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Gray;
        }
    }

    private static Brush ToBrush(string hex) => new SolidColorBrush(ToColor(hex));
}
#endif
