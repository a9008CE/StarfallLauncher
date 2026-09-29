using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

/// <summary>
/// 自定义背景：预设图库来自便携目录 <c>Launcher/backgrounds</c>，启动器不打包任何图片，
/// 因此换图只要把图片丢进该目录。支持「自选固定某一张」和「按间隔轮播全部预设」。
/// 图片文件本身始终保持原样，不会被重新编码或写回。
/// </summary>
public static class ThemeBackgroundService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    /// <summary>
    /// 单张解码后的最大宽度。磁盘文件不动，只在解码时按需降采样，
    /// 避免 10240×5760 这种超大图解码后占用两百多 MB 内存。
    /// </summary>
    private const int MaxDecodeWidth = 5120;

    /// <summary>内存中同时保留的解码图片数量，超出后淘汰最久未使用的一张。</summary>
    private const int MaxCachedImages = 3;

    /// <summary>缩略图缓存上限。</summary>
    private const int MaxCachedThumbnails = 48;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CachedImage> ImageCache = new();
    private static readonly Dictionary<string, CachedImage> ThumbnailCache = new();
    private static List<string> _presets = new();
    private static string _presetsStamp = "";
    private static long _useCounter;

    /// <summary>预设图库目录（便携）。</summary>
    public static string PresetsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Launcher", "backgrounds");

    private sealed class CachedImage
    {
        public BitmapSource Source = null!;
        public long StampTicks;
        public long LastUsed;
    }

    /// <summary>
    /// 自定义背景对所有 UI 风格生效（极简、扁平、毛玻璃、我的世界、赛博朋克、流浪地球）。
    /// 之前只在「极简 + 默认配色」下生效，导致换风格后背景凭空消失。
    /// </summary>
    public static bool SupportsCustomBackground(Settings settings)
    {
        return true;
    }

    /// <summary>扫描预设目录，按文件名排序。目录时间戳没变时直接复用上次结果。</summary>
    public static IReadOnlyList<string> GetPresets()
    {
        var dir = PresetsDirectory;
        var stamp = "0";
        try
        {
            if (Directory.Exists(dir)) stamp = Directory.GetLastWriteTimeUtc(dir).Ticks.ToString();
        }
        catch
        {
            return Array.Empty<string>();
        }

        lock (Gate)
        {
            if (stamp == _presetsStamp) return _presets;
            try
            {
                _presets = Directory.EnumerateFiles(dir)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(Path.GetFullPath)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                _presets = new List<string>();
            }
            _presetsStamp = stamp;
            return _presets;
        }
    }

    public static bool IsPreset(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return GetPresets().Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>轮播是否应该运行：默认主题、开关打开、且至少有两张预设。</summary>
    public static bool ShouldCarousel(Settings settings)
    {
        return SupportsCustomBackground(settings) && settings.ThemeBackgroundCarousel && GetPresets().Count > 1;
    }

    /// <summary>
    /// 当前应该显示的图片：自选固定优先，其次轮播按时间取模，最后回落到手动指定的预设序号。
    /// </summary>
    public static string? ResolveActivePath(Settings settings)
    {
        if (!SupportsCustomBackground(settings)) return null;

        if (!string.IsNullOrWhiteSpace(settings.ThemeBackgroundImage))
        {
            var pinned = TryFullPath(settings.ThemeBackgroundImage);
            if (pinned is not null && File.Exists(pinned)) return pinned;
        }

        var presets = GetPresets();
        if (presets.Count == 0) return null;

        if (settings.ThemeBackgroundCarousel && presets.Count > 1)
        {
            var seconds = Math.Clamp(settings.ThemeBackgroundCarouselSeconds, 3, 600);
            var index = (int)(Environment.TickCount64 / 1000L / seconds) % presets.Count;
            return presets[index];
        }

        var chosen = Math.Clamp(settings.ThemeBackgroundIndex, 0, presets.Count - 1);
        return presets[chosen];
    }

    public static Brush? TryCreateBrush(Settings settings, string baseColor, bool isLight)
    {
        var path = ResolveActivePath(settings);
        if (path is null) return null;

        var image = LoadImage(path);
        if (image is null) return null;

        try
        {
            var canvas = new RectangleGeometry(new Rect(0, 0, 100, 100));
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(ParseColor(baseColor)), null, canvas));
            group.Children.Add(new GeometryDrawing(
                new ImageBrush(image)
                {
                    Stretch = Stretch.UniformToFill,
                    Opacity = isLight ? 0.34 : 0.42
                }, null, canvas));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(
                    isLight ? (byte)72 : (byte)96,
                    isLight ? (byte)255 : (byte)0,
                    isLight ? (byte)255 : (byte)0,
                    isLight ? (byte)255 : (byte)0)),
                null, canvas));
            group.Freeze();

            var brush = new DrawingBrush(group)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                Viewbox = new Rect(0, 0, 100, 100),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>预设列表用的小缩略图，按目标宽度降采样，不影响原图。</summary>
    public static BitmapSource? LoadThumbnail(string path, int targetWidth)
    {
        string key;
        try
        {
            key = $"{Path.GetFullPath(path)}|{targetWidth}";
        }
        catch
        {
            return null;
        }

        lock (Gate)
        {
            if (ThumbnailCache.TryGetValue(key, out var hit))
            {
                hit.LastUsed = ++_useCounter;
                return hit.Source;
            }
        }

        var thumb = DecodeThumbnail(key, path, targetWidth);
        if (thumb is null) return null;

        lock (Gate)
        {
            ThumbnailCache[key] = new CachedImage
            {
                Source = thumb,
                LastUsed = ++_useCounter
            };
            if (ThumbnailCache.Count > MaxCachedThumbnails)
            {
                var oldest = ThumbnailCache.OrderBy(kv => kv.Value.LastUsed).First();
                ThumbnailCache.Remove(oldest.Key);
            }
        }
        return thumb;
    }

    /// <summary>丢弃缩略图缓存（图库内容变化后调用）。</summary>
    public static void InvalidateThumbnails()
    {
        lock (Gate)
        {
            ThumbnailCache.Clear();
        }
    }

    private static BitmapSource? DecodeThumbnail(string key, string path, int targetWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = targetWidth;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadImage(string path)
    {
        long stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return null;
        }

        lock (Gate)
        {
            if (ImageCache.TryGetValue(path, out var hit) && hit.StampTicks == stamp)
            {
                hit.LastUsed = ++_useCounter;
                return hit.Source;
            }

            var source = Decode(path);
            if (source is null) return null;

            ImageCache[path] = new CachedImage
            {
                Source = source,
                StampTicks = stamp,
                LastUsed = ++_useCounter
            };
            if (ImageCache.Count > MaxCachedImages)
            {
                var oldestKey = ImageCache.OrderBy(kv => kv.Value.LastUsed).First().Key;
                ImageCache.Remove(oldestKey);
            }
            return source;
        }
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            // 先只读文件头判断原始尺寸，再决定是否需要降采样解码；原图不会被修改。
            int nativeWidth;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var frame = BitmapFrame.Create(
                    probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                nativeWidth = frame.PixelWidth;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            if (nativeWidth > MaxDecodeWidth) image.DecodePixelWidth = MaxDecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static Color ParseColor(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value);
    }
}
