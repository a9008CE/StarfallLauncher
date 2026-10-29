using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuartzLauncher.Services;

/// <summary>
/// 默认主题的窗口背景。
///
/// 以前默认主题（既不是毛玻璃、也不是赛博/流浪地球）背景就是一块纯色 <c>t.Bg</c>，
/// 深色下就是一片死黑，所以"默认 UI 的背景太丑"。
///
/// ⚠️ 性能教训（2026-10-29，用户反馈"深色卡卡的"）：
/// 第一版直接返回 `DrawingBrush`（矢量图形）。问题是这个笔刷被
/// `Window.Background` + 外层 `Border` + `WindowRoot` **三处同时用到**，
/// 每帧都要把 30 个星点 + 4 层渐变**重新光栅化三遍** —— 页面切换/缩放一帧就掉。
/// 现在改成：先把整张背景**画进一张位图**（`RenderTargetBitmap`），
/// 再包成 `ImageBrush` 贴图。之后无论几层复用、窗口怎么缩放，都只是一次位图拉伸，
/// 代价和一张 JPG 背景一样。位图按配色缓存，切主题只重画一次。
///
/// 只改背景笔刷，不动任何布局。
/// </summary>
public static class SignatureBackdrop
{
    private const double Size = 100;

    /// <summary>光栅化尺寸：渐变很平滑，用不到窗口分辨率，1200x760 足够且只占 ~3.5 MB。</summary>
    private const int BitmapWidth = 1200;
    private const int BitmapHeight = 760;

    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    /// <summary>默认主题的窗口背景（<paramref name="isLight"/> 走浅色版本）。</summary>
    public static Brush Create(string baseHex, string primaryHex, bool isLight)
    {
        var key = $"{baseHex}|{primaryHex}|{(isLight ? "l" : "d")}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var group = BuildGroup(baseHex, primaryHex, isLight);
        var brush = Rasterize(group) ?? ToDrawingBrush(group);

        lock (Cache)
        {
            if (Cache.Count >= 6) Cache.Clear();
            Cache[key] = brush;
        }
        return brush;
    }

    /// <summary>把整张背景画进位图再包成 ImageBrush（真正便宜的那条路）。</summary>
    private static Brush? Rasterize(DrawingGroup group)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(ToDrawingBrush(group), null,
                    new Rect(0, 0, BitmapWidth, BitmapHeight));
            }

            var bitmap = new RenderTargetBitmap(BitmapWidth, BitmapHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            // 极端情况（无渲染设备/远程桌面）退回矢量版本，功能不受影响
            return null;
        }
    }

    private static DrawingGroup BuildGroup(string baseHex, string primaryHex, bool isLight)
    {
        var group = new DrawingGroup();
        var canvas = new RectangleGeometry(new Rect(0, 0, Size, Size));

        // ── 底色：斜向渐变 ──
        var baseBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        if (isLight)
        {
            // 浅色不再是死平的近白色：白 → 主题色一点点 → 回白 → 右下压深，做出体积
            baseBrush.GradientStops.Add(new GradientStop(ToColor("#FFFFFF"), 0));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, primaryHex, 0.16)), 0.40));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, "#FFFFFF", 0.45)), 0.64));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, primaryHex, 0.38)), 1));
        }
        else
        {
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, "#05070F", 0.62)), 0));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, primaryHex, 0.12)), 0.30));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, "#080C18", 0.45)), 0.60));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, "#161A3C", 0.46)), 0.84));
            baseBrush.GradientStops.Add(new GradientStop(ToColor(Mix(baseHex, "#03050A", 0.72)), 1));
        }
        group.Children.Add(new GeometryDrawing(baseBrush, null, canvas));

        if (isLight)
        {
            // 顶部一层很淡的白光，做出玻璃反光（浅色最缺这个）
            group.Children.Add(Glow("#FFFFFF", 150, centerX: 50, centerY: -14, width: 150, height: 78));
        }

        // ── 左上：主题色极光 ──
        group.Children.Add(Glow(primaryHex, isLight ? (byte)86 : (byte)118,
            centerX: 16, centerY: 4, width: 82, height: 56));

        // ── 右下：偏靛紫的对角辉光（跟主题色混一点，既拉开层次又不打架）──
        var companion = Mix(primaryHex, "#6B5BFF", isLight ? 0.5 : 0.6);
        group.Children.Add(Glow(companion, isLight ? (byte)70 : (byte)96,
            centerX: 90, centerY: 100, width: 88, height: 64));

        // ── 星点（只深色加；浅色加了像脏点）──
        if (!isLight)
        {
            foreach (var (x, y, r, a) in Stars)
            {
                group.Children.Add(new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb(a, 220, 236, 255)),
                    null,
                    new EllipseGeometry(new Point(x, y), r, r)));
            }
        }

        // ── 暗角：四周压暗，中间留住视线 ──
        var vignette = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.46),
            Center = new Point(0.5, 0.46),
            RadiusX = 0.78,
            RadiusY = 0.86,
        };
        vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.44));
        vignette.GradientStops.Add(new GradientStop(
            Color.FromArgb(isLight ? (byte)0 : (byte)64, 0, 0, 0), 0.80));
        vignette.GradientStops.Add(new GradientStop(
            Color.FromArgb(isLight ? (byte)0 : (byte)124, 0, 0, 0), 1));
        group.Children.Add(new GeometryDrawing(vignette, null,
            new RectangleGeometry(new Rect(0, 0, Size, Size))));

        group.Freeze();
        return group;
    }

    private static GeometryDrawing Glow(string colorHex, byte alpha,
        double centerX, double centerY, double width, double height)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(colorHex, alpha), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(colorHex, (byte)(alpha * 0.45)), 0.45));
        brush.GradientStops.Add(new GradientStop(WithAlpha(colorHex, 0), 1));

        var geometry = new EllipseGeometry(new Point(centerX, centerY), width / 2, height / 2);
        return new GeometryDrawing(brush, null, geometry);
    }

    /// <summary>矢量版本（光栅化时用来画，也是光栅化失败时的退路）。</summary>
    private static Brush ToDrawingBrush(DrawingGroup group)
    {
        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            Viewbox = new Rect(0, 0, Size, Size),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>固定星点（x, y, 半径, alpha）——写死保证每次启动位置一致，不会闪。</summary>
    private static readonly (double X, double Y, double R, byte A)[] Stars =
    {
        (8.4, 14.2, 0.35, 60), (15.8, 33.1, 0.28, 46), (23.6, 8.9, 0.30, 52),
        (31.2, 21.7, 0.24, 38), (12.1, 52.4, 0.26, 42), (19.4, 66.8, 0.32, 34),
        (27.9, 44.3, 0.22, 40), (36.5, 12.6, 0.34, 58), (43.8, 29.4, 0.25, 44),
        (51.6, 9.7, 0.30, 50), (58.9, 22.8, 0.23, 36), (47.2, 47.9, 0.28, 30),
        (39.7, 61.2, 0.26, 26), (55.4, 70.6, 0.31, 32), (63.1, 55.8, 0.24, 38),
        (71.5, 38.2, 0.29, 46), (78.3, 17.4, 0.33, 54), (84.7, 44.6, 0.25, 40),
        (90.2, 26.9, 0.28, 48), (93.6, 63.4, 0.31, 44), (86.1, 78.7, 0.27, 36),
        (74.4, 86.2, 0.29, 30), (62.7, 91.4, 0.24, 26), (48.3, 84.1, 0.30, 28),
        (33.8, 89.6, 0.26, 24), (21.7, 79.3, 0.31, 30), (9.8, 88.5, 0.27, 34),
    };

    private static Color ToColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Black;
        }
    }

    private static Color WithAlpha(string hex, byte alpha)
    {
        var c = ToColor(hex);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    private static string Mix(string a, string b, double t)
    {
        var ca = ToColor(a);
        var cb = ToColor(b);
        t = Math.Clamp(t, 0, 1);
        return $"#{Blend(ca.R, cb.R, t):X2}{Blend(ca.G, cb.G, t):X2}{Blend(ca.B, cb.B, t):X2}";
    }

    private static byte Blend(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
}
