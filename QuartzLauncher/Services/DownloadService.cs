using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public static class DownloadService
{
    private static readonly HttpClient _client = HttpClients.Create();
    private static readonly object LogLock = new();
    private static readonly ConcurrentDictionary<string, Task<string?>> ExistingFileLookups = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string DownloadLogPath = Path.Combine(AppContext.BaseDirectory, "Launcher", "download.log");

    private static string _sourceMode = "hybrid";
    private static readonly Dictionary<string, string> _mirrorMap = new();
    private static readonly object _mirrorLock = new();
    private const string BmclapiMirror = "https://bmclapi2.bangbang93.com";
    private const string CurseForgeMirror = "https://mod.mcimirror.top";
    private const string AdoptiumMirror = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium";
    private const long ParallelDownloadThreshold = 2 * 1024 * 1024;
    private const int MaxFileSegments = 8;
    private static readonly TimeSpan DownloadInactivityTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MirrorFailureCooldown = TimeSpan.FromMinutes(1);
    private static long _mirrorUnavailableUntilUtcTicks;

    // 文件校验缓存：记录已校验文件的 大小 + 修改时间 + 哈希。
    // 未变化的文件直接判定通过，避免每次启动都对成千上万个资源文件重新做 SHA1。
    private sealed class VerifyEntry
    {
        public long Length { get; set; }
        public long LastWriteTicks { get; set; }
        public string Hash { get; set; } = "";
    }

    private static readonly ConcurrentDictionary<string, VerifyEntry> VerifyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string VerifyCachePath = Path.Combine(AppContext.BaseDirectory, "Launcher", "verify-cache.json");
    private static int _verifyCacheLoaded;
    private static int _verifyCacheDirty;
    private static readonly Timer VerifyCacheSaveTimer = new(_ => SaveVerifyCache(), null, Timeout.Infinite, Timeout.Infinite);

    private sealed class RangeNotSupportedException(string message) : IOException(message);

    static DownloadService()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("QuartzLauncher/1.0");
    }

    public static void SetSourceMode(string mode)
    {
        lock (_mirrorLock)
        {
            _sourceMode = mode;
            _mirrorMap.Clear();
            if (mode is "bmclapi" or "hybrid" or "hybrid_priority")
            {
                _mirrorMap["https://piston-meta.mojang.com"] = BmclapiMirror;
                _mirrorMap["https://piston-data.mojang.com"] = BmclapiMirror;
                _mirrorMap["https://launchermeta.mojang.com"] = BmclapiMirror;
                _mirrorMap["https://launcher.mojang.com"] = BmclapiMirror;
                _mirrorMap["https://resources.download.minecraft.net"] = BmclapiMirror + "/assets";
                _mirrorMap["https://libraries.minecraft.net"] = BmclapiMirror + "/libraries";
                _mirrorMap["https://meta.fabricmc.net"] = BmclapiMirror + "/fabric-meta";
                _mirrorMap["https://maven.fabricmc.net"] = BmclapiMirror + "/fabric-maven";
            }
            _mirrorMap["https://edge.forgecdn.net"] = CurseForgeMirror;
            _mirrorMap["https://mediafilez.forgecdn.net"] = CurseForgeMirror;
            _mirrorMap["https://media.forgecdn.net"] = CurseForgeMirror;
        }
    }

    private static string ResolveUrl(string original)
    {
        lock (_mirrorLock)
        {
            if (_mirrorMap.Count == 0) return original;
            foreach (var (prefix, replacement) in _mirrorMap)
            {
                if (original.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return replacement + original[prefix.Length..];
            }
            return original;
        }
    }

    private static List<string> ResolveUrls(string url)
    {
        var mirrorUrls = ResolveMirrorUrls(url);
        var isCurseForge = url.StartsWith("https://edge.forgecdn.net", StringComparison.OrdinalIgnoreCase)
                           || url.StartsWith("https://mediafilez.forgecdn.net", StringComparison.OrdinalIgnoreCase)
                           || url.StartsWith("https://media.forgecdn.net", StringComparison.OrdinalIgnoreCase);
        var isAdoptium = url.StartsWith("https://github.com/adoptium/", StringComparison.OrdinalIgnoreCase);
        if (mirrorUrls.Count == 0 || (_sourceMode == "mojang" && !isCurseForge && !isAdoptium))
            return new List<string> { url };
        if (_sourceMode == "bmclapi")
            return mirrorUrls;
        return mirrorUrls.Concat(new[] { url }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ResolveMirrorUrls(string url)
    {
        var result = new List<string>();
        var adoptiumMirror = ResolveAdoptiumMirror(url);
        if (!string.IsNullOrEmpty(adoptiumMirror))
            result.Add(adoptiumMirror);

        if (url.StartsWith("https://libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = url["https://libraries.minecraft.net".Length..];
            result.Add(BmclapiMirror + "/maven" + suffix);
        }

        var resolved = ResolveUrl(url);
        if (!string.Equals(resolved, url, StringComparison.OrdinalIgnoreCase))
            result.Add(resolved);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolveAdoptiumMirror(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 6
            || !segments[0].Equals("adoptium", StringComparison.OrdinalIgnoreCase)
            || !segments[1].StartsWith("temurin", StringComparison.OrdinalIgnoreCase)
            || !segments[1].EndsWith("-binaries", StringComparison.OrdinalIgnoreCase))
            return null;

        var majorText = segments[1]["temurin".Length..(segments[1].Length - "-binaries".Length)];
        if (!int.TryParse(majorText, out var major) || major < 8) return null;
        var fileName = Uri.UnescapeDataString(segments[^1]);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return null;

        return $"{AdoptiumMirror}/{major}/jdk/x64/windows/{Uri.EscapeDataString(fileName)}";
    }

    public static async Task<JsonElement> FetchJsonAsync(string url)
    {
        Exception? lastError = null;
        foreach (var candidate in ResolveUrls(url))
        {
            try
            {
                var bytes = await _client.GetByteArrayAsync(candidate);
                var json = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new IOException($"无法获取 JSON 元数据: {url}", lastError);
    }

    public static async Task<(string Url, long Size)> ResolveDownloadAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var resolvedUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return (resolvedUrl, response.Content.Headers.ContentLength ?? 0);
    }

    public static int GetConnectionWeight(DownloadItem item, int workers)
    {
        if (workers <= 1 || item.Size < ParallelDownloadThreshold)
            return 1;
        // 分片要“小”：BMCLAPI 的 302 签名地址只有 60 秒有效期，
        // 大文件切大段容易下载到一半地址过期 → 卡住重试，反而更慢
        return Math.Clamp((int)Math.Ceiling(item.Size / (5d * 1024 * 1024)), 2, MaxFileSegments);
    }

    public static async Task<string> DownloadOneAsync(DownloadItem item, Action<string, int, int>? progress = null,
        CancellationToken ct = default, int maxAttempts = 3, int maxSegments = 1)
    {
        var targetKey = Path.GetFullPath(item.Target);
        var downloadLock = DownloadLocks.GetOrAdd(targetKey, _ => new SemaphoreSlim(1, 1));
        await downloadLock.WaitAsync(ct);
        try
        {
            if (ValidFile(item)) return item.Target;
            if (await TryReuseExistingFileAsync(item, ct)) return item.Target;

            Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
            var temp = item.Target + ".part";
            var urls = ResolveUrls(item.Url);

            Exception? lastError = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                foreach (var url in urls)
                {
                    if (IsMirrorCoolingDown(url) && urls.Count > 1)
                        continue;
                    try
                    {
                        var result = await DownloadCandidateAsync(item, url, temp, progress, ct, maxSegments);
                        if (IsBmclapiUrl(url)) Interlocked.Exchange(ref _mirrorUnavailableUntilUtcTicks, 0);
                        return result;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (IsBmclapiUrl(url))
                            Interlocked.Exchange(ref _mirrorUnavailableUntilUtcTicks,
                                DateTime.UtcNow.Add(MirrorFailureCooldown).Ticks);
                        WriteDownloadLog(item, url, attempt + 1, ex);
                        CleanupInvalidPartial(temp, item);
                    }
                }
                if (attempt < maxAttempts - 1)
                    await Task.Delay(1500 * (attempt + 1), ct);
            }
            throw new IOException($"下载失败: {Path.GetFileName(item.Target)}。{lastError?.Message}", lastError);
        }
        finally
        {
            downloadLock.Release();
        }
    }

    private static void WriteDownloadLog(DownloadItem item, string url, int attempt, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(DownloadLogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Attempt {attempt} | {url}{Environment.NewLine}" +
                        $"Target: {item.Target}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (LogLock)
                File.AppendAllText(DownloadLogPath, entry);
        }
        catch
        {
        }
    }

    public static void WriteTaskLog(string taskName, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(DownloadLogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Task failed | {taskName}{Environment.NewLine}" +
                        $"{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (LogLock)
                File.AppendAllText(DownloadLogPath, entry);
        }
        catch
        {
        }
    }

    private static async Task<string> DownloadCandidateAsync(DownloadItem item, string url, string temp,
        Action<string, int, int>? progress, CancellationToken ct, int maxSegments)
    {
        var segments = Math.Clamp(maxSegments, 1, MaxFileSegments);
        if (segments > 1 && item.Size >= ParallelDownloadThreshold)
        {
            try
            {
                return await DownloadSegmentedAsync(item, url, temp, progress, ct, segments);
            }
            catch (RangeNotSupportedException)
            {
                CleanupSegments(temp);
            }
        }
        return await DownloadFromUrl(item, url, temp, progress, ct);
    }

    private static async Task<string> DownloadFromUrl(DownloadItem item, string url, string temp, Action<string, int, int>? progress, CancellationToken ct)
    {
        var existing = GetPartialLength(temp, item.Size);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var inactivity = CreateInactivityTokenSource(ct);
        using var response = await SendWithInactivityTimeoutAsync(request, inactivity.Token, ct);
        response.EnsureSuccessStatusCode();
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (append && response.Content.Headers.ContentRange?.From != existing)
            throw new IOException("下载源返回了错误的续传范围");
        if (existing > 0 && !append)
            existing = 0;
        var total = response.Content.Headers.ContentLength ?? 0;
        if (append && total > 0)
            total += existing;
        inactivity.CancelAfter(DownloadInactivityTimeout);
        await using (var stream = await response.Content.ReadAsStreamAsync(inactivity.Token))
        await using (var fs = new FileStream(temp, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true))
        {
            var buffer = new byte[81920];
            long loaded = existing;
            int read;
            if (loaded > 0)
                progress?.Invoke(item.Target, ToProgressInt(loaded), ToProgressInt(total > 0 ? total : item.Size));
            while (true)
            {
                try
                {
                    read = await stream.ReadAsync(buffer, inactivity.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("下载连接超过 30 秒没有数据，正在切换下载源。");
                }
                if (read <= 0) break;
                inactivity.CancelAfter(DownloadInactivityTimeout);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                loaded += read;
                progress?.Invoke(item.Target, ToProgressInt(loaded), ToProgressInt(total > 0 ? total : item.Size));
            }
        }

        await ValidateAndMoveAsync(item, temp);
        return item.Target;
    }

    private static async Task<string> DownloadSegmentedAsync(DownloadItem item, string url, string temp,
        Action<string, int, int>? progress, CancellationToken ct, int segments)
    {
        if (item.Size <= 0)
            throw new RangeNotSupportedException("无法对未知大小的文件进行分段下载");

        var segmentDir = temp + ".segments";
        Directory.CreateDirectory(segmentDir);
        var lengths = new long[segments];
        var ranges = new (long Start, long End)[segments];
        long loaded = 0;
        for (var i = 0; i < segments; i++)
        {
            var start = item.Size * i / segments;
            var end = item.Size * (i + 1) / segments - 1;
            ranges[i] = (start, end);
            var part = Path.Combine(segmentDir, i.ToString("D2") + ".part");
            var expected = end - start + 1;
            lengths[i] = File.Exists(part) ? Math.Min(new FileInfo(part).Length, expected) : 0;
            if (File.Exists(part) && new FileInfo(part).Length > expected) {
                File.Delete(part);
                lengths[i] = 0;
            }
            loaded += lengths[i];
        }
        if (loaded > 0)
            progress?.Invoke(item.Target, ToProgressInt(loaded), ToProgressInt(item.Size));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var partTasks = Enumerable.Range(0, segments)
            .Where(i => lengths[i] < ranges[i].End - ranges[i].Start + 1)
            .Select(i => DownloadSegmentAsync(item, url, segmentDir, i, ranges[i], lengths, progress, linked.Token))
            .ToArray();
        try
        {
            await Task.WhenAll(partTasks);
        }
        catch
        {
            linked.Cancel();
            try { await Task.WhenAll(partTasks); } catch { }
            throw;
        }

        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read,
                         128 * 1024, useAsync: true))
        {
            for (var i = 0; i < segments; i++)
            {
                var part = Path.Combine(segmentDir, i.ToString("D2") + ".part");
                await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, useAsync: true);
                await input.CopyToAsync(output, 128 * 1024, ct);
            }
        }

        try
        {
            await ValidateAndMoveAsync(item, temp);
        }
        catch
        {
            CleanupSegments(temp);
            throw;
        }
        CleanupSegments(temp);
        return item.Target;
    }

    private static async Task DownloadSegmentAsync(DownloadItem item, string url, string segmentDir, int index,
        (long Start, long End) range, long[] lengths, Action<string, int, int>? progress, CancellationToken ct)
    {
        var part = Path.Combine(segmentDir, index.ToString("D2") + ".part");
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadSegmentOnceAsync(item, url, part, index, range, lengths, progress, ct);
                return;
            }
            catch (RangeNotSupportedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单片卡住/断流：只重发这一片的请求（会重新解析 302 拿到新的签名地址），
                // 已下好的部分继续续传，不让整个文件推倒重来
                lastError = ex;
                await Task.Delay(400 * (attempt + 1), ct);
            }
        }
        throw new IOException($"下载分段 {index + 1} 重试失败：{lastError?.Message}", lastError);
    }

    private static async Task DownloadSegmentOnceAsync(DownloadItem item, string url, string part, int index,
        (long Start, long End) range, long[] lengths, Action<string, int, int>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(range.Start + lengths[index], range.End);
        using var inactivity = CreateInactivityTokenSource(ct);
        using var response = await SendWithInactivityTimeoutAsync(request, inactivity.Token, ct);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException($"下载源未返回分段响应: {(int)response.StatusCode}");
        var contentRange = response.Content.Headers.ContentRange;
        var expected = range.End - range.Start + 1 - lengths[index];
        if (contentRange?.From != range.Start + lengths[index]
            || contentRange.To != range.End
            || response.Content.Headers.ContentLength != expected)
            throw new RangeNotSupportedException("下载源返回的分段范围不一致");

        inactivity.CancelAfter(DownloadInactivityTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(inactivity.Token);
        await using var output = new FileStream(part, lengths[index] > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        var readTotal = lengths[index];
        int read;
        while (true)
        {
            try
            {
                read = await stream.ReadAsync(buffer, inactivity.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"下载分段 {index + 1} 超过 {DownloadInactivityTimeout.TotalSeconds:0} 秒没有数据，正在重试。");
            }
            if (read <= 0) break;
            inactivity.CancelAfter(DownloadInactivityTimeout);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            Interlocked.Exchange(ref lengths[index], readTotal);
            var aggregate = lengths.Sum();
            progress?.Invoke(item.Target, ToProgressInt(aggregate), ToProgressInt(item.Size));
        }
    }

    public static async Task DownloadManyAsync(IEnumerable<DownloadItem> items, int workers, Action<string, int, int>? progress = null, CancellationToken ct = default)
    {
        var pending = items
            .Where(i => !ValidFile(i))
            .GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var total = pending.Count;
        if (total == 0) { progress?.Invoke("All files verified", 1, 1); return; }

        var connectionBudget = new SemaphoreSlim(Math.Max(1, workers));
        var connectionAllocationLock = new SemaphoreSlim(1, 1);
        int done = 0;
        var tasks = pending.Select(async item =>
        {
            var weight = GetConnectionWeight(item, workers);
            await WaitForConnectionsAsync(connectionBudget, connectionAllocationLock, weight, ct);
            try
            {
                await DownloadOneAsync(item, null, ct, maxSegments: weight);
                var current = Interlocked.Increment(ref done);
                progress?.Invoke(item.Target, current, total);
            }
            finally { connectionBudget.Release(weight); }
        });
        await Task.WhenAll(tasks);
    }

    private static async Task WaitForConnectionsAsync(
        SemaphoreSlim budget, SemaphoreSlim allocationLock, int count, CancellationToken ct)
    {
        await allocationLock.WaitAsync(ct);
        try
        {
            for (var i = 0; i < count; i++)
            {
                try
                {
                    await budget.WaitAsync(ct);
                }
                catch
                {
                    if (i > 0) budget.Release(i);
                    throw;
                }
            }
        }
        finally { allocationLock.Release(); }
    }

    private static long GetPartialLength(string path, long expectedSize)
    {
        if (!File.Exists(path)) return 0;
        var length = new FileInfo(path).Length;
        return length > 0 && (expectedSize <= 0 || length < expectedSize) ? length : 0;
    }

    private static void CleanupInvalidPartial(string path, DownloadItem item)
    {
        try
        {
            if (!File.Exists(path)) return;
            var length = new FileInfo(path).Length;
            if (item.Size <= 0 || length >= item.Size)
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void CleanupSegments(string temp)
    {
        try
        {
            var directory = temp + ".segments";
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch
        {
        }
    }

    private static int ToProgressInt(long value) =>
        (int)Math.Clamp(value, 0, int.MaxValue);

    private static CancellationTokenSource CreateInactivityTokenSource(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(DownloadInactivityTimeout);
        return source;
    }

    private static async Task<HttpResponseMessage> SendWithInactivityTimeoutAsync(
        HttpRequestMessage request, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new IOException("连接超过 30 秒没有响应，正在切换下载源。");
        }
    }

    private static bool IsMirrorCoolingDown(string url) =>
        IsBmclapiUrl(url) && DateTime.UtcNow.Ticks < Interlocked.Read(ref _mirrorUnavailableUntilUtcTicks);

    private static bool IsBmclapiUrl(string url) =>
        url.StartsWith(BmclapiMirror + "/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(url, BmclapiMirror, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> TryReuseExistingFileAsync(DownloadItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.Sha1) || item.Size <= 0) return false;

        var target = Path.GetFullPath(item.Target);
        var minecraftRoot = FindMinecraftRoot(target);
        if (minecraftRoot == null) return false;
        var searchRoot = GetReuseSearchRoot(minecraftRoot, target);
        if (searchRoot == null || !Directory.Exists(searchRoot)) return false;

        var key = $"{searchRoot}|{item.Size}|{item.Sha1.ToLowerInvariant()}";
        var lookup = ExistingFileLookups.GetOrAdd(key, _ =>
            Task.Run(() => FindMatchingFile(searchRoot, target, item.Size, item.Sha1, ct)));
        string? candidate;
        try
        {
            candidate = await lookup;
        }
        catch
        {
            if (ExistingFileLookups.TryGetValue(key, out var current) && ReferenceEquals(current, lookup))
                ExistingFileLookups.TryRemove(key, out _);
            throw;
        }
        if (candidate == null || !File.Exists(candidate)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(candidate, target, true);
        return ValidFile(item);
    }

    private static string? FindMinecraftRoot(string target)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(target)!);
        while (directory != null)
        {
            if (directory.Name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static string? GetReuseSearchRoot(string minecraftRoot, string target)
    {
        var relative = Path.GetRelativePath(minecraftRoot, target);
        var firstDirectory = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstDirectory.Equals("versions", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(minecraftRoot, "versions")
            : null;
    }

    private static string? FindMatchingFile(string searchRoot, string target, long size, string sha1,
        CancellationToken ct)
    {
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(candidate);
                if (info.Length != size) continue;
                if (Sha1File(candidate).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        return null;
    }

    public static bool ValidFile(DownloadItem item)
    {
        if (!File.Exists(item.Target)) return false;
        var info = new FileInfo(item.Target);
        if (item.Size > 0 && info.Length != item.Size) return false;

        var expected = !string.IsNullOrEmpty(item.Sha1) ? item.Sha1 : item.Sha256;
        if (string.IsNullOrEmpty(expected)) return true;

        // 命中校验缓存（大小 + 修改时间一致）时跳过哈希，显著加快「补全文件」
        EnsureVerifyCacheLoaded();
        if (VerifyCache.TryGetValue(item.Target, out var cached)
            && cached.Length == info.Length
            && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks
            && cached.Hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;

        var actual = !string.IsNullOrEmpty(item.Sha1) ? Sha1File(item.Target) : Sha256File(item.Target);
        var valid = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        if (valid)
        {
            VerifyCache[item.Target] = new VerifyEntry
            {
                Length = info.Length,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                Hash = actual
            };
            MarkVerifyCacheDirty();
        }
        else
        {
            VerifyCache.TryRemove(item.Target, out _);
        }
        return valid;
    }

    private static void EnsureVerifyCacheLoaded()
    {
        if (Interlocked.Exchange(ref _verifyCacheLoaded, 1) == 1) return;
        try
        {
            if (!File.Exists(VerifyCachePath)) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, VerifyEntry>>(File.ReadAllText(VerifyCachePath));
            if (entries == null) return;
            foreach (var (path, entry) in entries)
                if (entry != null) VerifyCache[path] = entry;
        }
        catch
        {
        }
    }

    private static void MarkVerifyCacheDirty()
    {
        if (Interlocked.Exchange(ref _verifyCacheDirty, 1) == 0)
            VerifyCacheSaveTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private static void SaveVerifyCache()
    {
        if (Interlocked.Exchange(ref _verifyCacheDirty, 0) == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VerifyCachePath)!);
            var snapshot = new Dictionary<string, VerifyEntry>(VerifyCache, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(VerifyCachePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
        }
    }

    public static string Sha1File(string path)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        var hash = sha1.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<string> Sha1FileAsync(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var sha1 = SHA1.Create();
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var hash = await sha1.ComputeHashAsync(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
            catch (IOException) when (i < 4)
            {
                await Task.Delay(200 * (i + 1));
            }
        }
    }

    private static async Task ValidateAndMoveAsync(DownloadItem item, string temp)
    {
        if (item.Size > 0 && new FileInfo(temp).Length != item.Size)
            throw new IOException($"Size mismatch: {item.Target}");

        if (!string.IsNullOrEmpty(item.Sha1))
        {
            var hash = await Sha1FileAsync(temp);
            if (!hash.Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA-1 mismatch: {item.Target}");
        }
        if (!string.IsNullOrEmpty(item.Sha256))
        {
            await using var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, useAsync: true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA-256 mismatch: {item.Target}");
        }

        File.Move(temp, item.Target, true);
    }
}
