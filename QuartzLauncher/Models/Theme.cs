#if !FULL_BUILD
namespace QuartzLauncher.Models;

/// <summary>
/// 【开源版】主题数据结构。完整版包含 6 套 UI 风格与多套配色的预设表，开源版仅提供一套默认配色。
/// </summary>
public class Theme
{
    public string Name { get; set; } = "";
    public string Primary { get; set; } = "#2B86E5";
    public string Bg { get; set; } = "#E8EEF4";
    public string Card { get; set; } = "#FFFFFF";
    public string Text { get; set; } = "#1E293B";
    public string TextMuted { get; set; } = "#94A3B8";
    public string Sidebar { get; set; } = "#FFFFFF";
    public string Border { get; set; } = "#E2E8F0";
    public string Danger { get; set; } = "#EF4444";
    public string Success { get; set; } = "#22C55E";
    public string BackgroundImage { get; set; } = "";

    public int CardCornerRadius { get; set; } = 16;
    public int ButtonCornerRadius { get; set; } = 12;
    public int InputCornerRadius { get; set; } = 10;
    public int SidebarWidth { get; set; } = 240;
    public double CardShadowDepth { get; set; } = 0;
    public double CardShadowBlur { get; set; } = 0;
    public double CardShadowOpacity { get; set; } = 0;
    public string CardShadowColor { get; set; } = "#000000";
    public double CardBorderThickness { get; set; } = 1;
    public int NavButtonHeight { get; set; } = 44;

    public static Dictionary<string, Theme> GetPresets(string uiStyle, string mode)
        => new() { ["默认"] = ForMode("默认", mode, uiStyle) };

    public static Theme ForMode(string presetName, string mode, string uiStyle = "minimal")
        => mode == "light"
            ? new Theme
            {
                Name = "默认",
                 Primary = "#2878E8",
                 Bg = "#F4F7FB",
                 Card = "#FFFFFF",
                 Text = "#1B2D43",
                 TextMuted = "#7B8DA3",
                 Sidebar = "#FFFFFF",
                 Border = "#DCE5EE",
                 Danger = "#DC2626",
                 Success = "#16A34A",
                 CardCornerRadius = 18,
                 ButtonCornerRadius = 12,
                 InputCornerRadius = 10,
                 SidebarWidth = 236,
                 NavButtonHeight = 44,
                 CardShadowDepth = 4,
                 CardShadowBlur = 18,
                 CardShadowOpacity = 0.12
            }
            : new Theme
            {
                Name = "默认",
                Primary = "#2B86E5",
                Bg = "#0B1220",
                Card = "#131C2B",
                Text = "#E6EDF5",
                TextMuted = "#8494A7",
                Sidebar = "#0E1624",
                Border = "#22304A",
                Danger = "#EF4444",
                Success = "#22C55E"
            };
}
#endif
