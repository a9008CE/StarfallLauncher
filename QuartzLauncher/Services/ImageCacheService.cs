using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace QuartzLauncher.Services;

public static class ImageCacheService
{
    private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "Launcher", "cache", "images");
    private static readonly string ResolveCachePath = Path.Combine(AppContext.BaseDirectory, "Launcher", "cache", "image-urls.json");
    private static readonly string TextCachePath = Path.Combine(AppContext.BaseDirectory, "Launcher", "cache", "text-cache.json");
    private static readonly ConcurrentDictionary<string, BitmapImage> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ResolvedUrls = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ResolveLock = new();
    private static readonly object TextLock = new();
    private static int _resolvedLoaded;
    private static int _textsLoaded;

    public static string? GetText(string key)
    {
        EnsureTextsLoaded();
        return Texts.TryGetValue(key, out var value) ? value : null;
    }

    public static void SetText(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        EnsureTextsLoaded();
        if (Texts.TryGetValue(key, out var existing) && existing == value) return;
        Texts[key] = value;
        _ = Task.Run(SaveTexts);
    }

    public static string? GetResolvedUrl(string key)
    {
        EnsureResolvedLoaded();
        return ResolvedUrls.TryGetValue(key, out var url) && url.Length > 0 ? url : null;
    }

    public static void StoreResolvedUrl(string key, string url)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(url)) return;
        EnsureResolvedLoaded();
        if (ResolvedUrls.TryGetValue(key, out var existing) && existing == url) return;
        ResolvedUrls[key] = url;
        _ = Task.Run(SaveResolved);
    }

    public static async Task<BitmapImage?> GetAsync(HttpClient client, string url, int decodePixelWidth,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var key = $"{decodePixelWidth}|{url}";
        if (Memory.TryGetValue(key, out var cached)) return cached;

        byte[]? bytes = null;
        var file = Path.Combine(CacheDir, Hash(url) + ".img");
        try
        {
            if (File.Exists(file))
                bytes = await File.ReadAllBytesAsync(file, ct);
        }
        catch
        {
            bytes = null;
        }

        if (bytes is not { Length: > 0 })
        {
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
            try
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(file, bytes, ct);
            }
            catch
            {
            }
        }

        var image = Decode(bytes, decodePixelWidth);
        if (image != null) Memory[key] = image;
        return image;
    }

    private static BitmapImage? Decode(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string Hash(string url)
    {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
    }

    private static void EnsureResolvedLoaded()
    {
        if (Interlocked.Exchange(ref _resolvedLoaded, 1) == 1) return;
        try
        {
            if (!File.Exists(ResolveCachePath)) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ResolveCachePath));
            if (entries == null) return;
            foreach (var (key, value) in entries)
                ResolvedUrls[key] = value;
        }
        catch
        {
        }
    }

    private static void SaveResolved()
    {
        lock (ResolveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResolveCachePath)!);
                File.WriteAllText(ResolveCachePath, JsonSerializer.Serialize(ResolvedUrls));
            }
            catch
            {
            }
        }
    }

    private static void EnsureTextsLoaded()
    {
        if (Interlocked.Exchange(ref _textsLoaded, 1) == 1) return;
        try
        {
            if (!File.Exists(TextCachePath)) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TextCachePath));
            if (entries == null) return;
            foreach (var (key, value) in entries)
                Texts[key] = value;
        }
        catch
        {
        }
    }

    private static void SaveTexts()
    {
        lock (TextLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TextCachePath)!);
                File.WriteAllText(TextCachePath, JsonSerializer.Serialize(Texts));
            }
            catch
            {
            }
        }
    }
}
