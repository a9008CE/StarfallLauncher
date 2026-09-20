using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public class DownloadManager : INotifyPropertyChanged
{
    private static readonly HttpClient NetworkProbe = CreateNetworkProbe();
    public static DownloadManager Instance { get; } = new();

    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    private int _maxConcurrent = 3;
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            var next = Math.Clamp(value, 1, 16);
            if (_maxConcurrent == next) return;
            _maxConcurrent = next;
            OnPropertyChanged();
            ScheduleQueuedTasks();
        }
    }

    private int _resourceMaxConcurrent = 4;
    public int ResourceMaxConcurrent
    {
        get => _resourceMaxConcurrent;
        set
        {
            var next = Math.Clamp(value, 1, 16);
            if (_resourceMaxConcurrent == next) return;
            _resourceMaxConcurrent = next;
            OnPropertyChanged();
            ScheduleQueuedTasks();
        }
    }

    private bool _autoAdjustConcurrency = true;
    public bool AutoAdjustConcurrency
    {
        get => _autoAdjustConcurrency;
        set { _autoAdjustConcurrency = value; OnPropertyChanged(); if (value) _ = DetectNetworkAsync(); }
    }

    private int _activeCount;
    public int ActiveCount
    {
        get => _activeCount;
        set { _activeCount = value; OnPropertyChanged(); }
    }

    private readonly object _lock = new();
    private readonly HashSet<string> _activeTasks = new();
    private readonly Dictionary<string, int> _taskWorkers = new();
    private readonly System.Threading.Timer _networkTimer;
    private readonly System.Threading.SemaphoreSlim _networkProbeLock = new(1, 1);
    private long _lastSampleBytes;
    private DateTime _lastSampleTime = DateTime.UtcNow;

    public event EventHandler<DownloadTask>? TaskCompleted;
    public event EventHandler? QueueChanged;

    private DownloadManager()
    {
        _networkTimer = new System.Threading.Timer(
            async _ => await DetectNetworkAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12));
    }

    private static HttpClient CreateNetworkProbe()
    {
        var client = HttpClients.Create(TimeSpan.FromSeconds(5));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuartzLauncher/1.0");
        return client;
    }

    public DownloadTask Enqueue(string name, List<DownloadItem> items, int workers = 0, string category = "general", Func<Task>? postDownloadAction = null, int maxAttempts = 0, bool showWhenEmpty = false, bool force = false)
    {
        // 先按目标去重再并行校验，避免成千上万个文件顺序哈希导致卡顿
        // force = true 时不校验本地文件，用于「重复下载」时强制重新拉取
        var pendingItems = items
            .GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .AsParallel()
            .AsOrdered()
            .Where(item => force || !DownloadService.ValidFile(item))
            .ToList();
        var task = new DownloadTask
        {
            Name = name,
            Category = category,
            MaxAttempts = maxAttempts > 0 ? maxAttempts : category == "java" ? 5 : 3,
            PostDownloadAction = postDownloadAction,
            Items = pendingItems,
            TotalBytes = pendingItems.Sum(item => item.Size)
        };

        if (task.Items.Count == 0 && postDownloadAction == null)
        {
            task.Status = DownloadTaskStatus.Completed;
            task.Progress = 100;
            task.CompletedFiles = task.TotalFiles;

            // 文件已完整：默认不占位，安装/补全流程要求时仍显示一条「已完成」，避免下载管理空白无从判断
            if (showWhenEmpty)
            {
                task.StatusText = "文件已完整，无需下载";
                Tasks.Insert(0, task);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
            return task;
        }

        task.Tcs = new TaskCompletionSource<DownloadTask>();
        task.Cts = new CancellationTokenSource();

        Tasks.Insert(0, task);
        _taskWorkers[task.Id] = workers > 0 ? workers : 1;
        QueueChanged?.Invoke(this, EventArgs.Empty);

        ScheduleQueuedTasks();
        return task;
    }

    public async Task<DownloadTask> EnqueueAsync(string name, List<DownloadItem> items, int workers = 0, string category = "general", Func<Task>? postDownloadAction = null)
    {
        var task = Enqueue(name, items, workers, category, postDownloadAction);
        if (task.Tcs != null)
            return await task.Tcs.Task;
        return task;
    }

    public void Pause(DownloadTask task)
    {
        if (task.Status != DownloadTaskStatus.Downloading && task.Status != DownloadTaskStatus.Queued)
            return;
        task.RunGeneration++;
        task.Cts?.Cancel();
        task.Status = DownloadTaskStatus.Paused;
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume(DownloadTask task)
    {
        if (task.Status != DownloadTaskStatus.Paused && task.Status != DownloadTaskStatus.Failed)
            return;
        task.RunGeneration++;
        task.Cts = new CancellationTokenSource();
        task.Status = DownloadTaskStatus.Queued;
        QueueChanged?.Invoke(this, EventArgs.Empty);
        ScheduleQueuedTasks();
    }

    public void Cancel(DownloadTask task)
    {
        if (task.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Cancelled)
            return;
        task.RunGeneration++;
        task.Cts?.Cancel();
        task.Status = DownloadTaskStatus.Cancelled;
        task.Tcs?.TrySetResult(task);
        bool wasActive;
        lock (_lock)
        {
            wasActive = _activeTasks.Contains(task.Id);
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
        if (!wasActive)
        {
            task.Tcs?.TrySetResult(task);
            TaskCompleted?.Invoke(this, task);
            ScheduleQueuedTasks();
        }
    }

    public void CancelAll()
    {
        foreach (var t in Tasks.ToList())
            Cancel(t);
    }

    public void Remove(DownloadTask task)
    {
        if (task.Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)
            return;
        Tasks.Remove(task);
        _taskWorkers.Remove(task.Id);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleQueuedTasks()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(TryStartQueuedTasks);
            return;
        }
        TryStartQueuedTasks();
    }

    private void TryStartQueuedTasks()
    {
        var starting = new List<(DownloadTask Task, int Workers)>();
        lock (_lock)
        {
            var activeResources = Tasks.Count(task => task.Category == "resource" && _activeTasks.Contains(task.Id));
            var activeOtherTasks = _activeTasks.Count - activeResources;
            foreach (var task in Tasks.Where(task => task.Status == DownloadTaskStatus.Queued))
            {
                if (task.Category == "resource" && activeResources >= _resourceMaxConcurrent) continue;
                if (task.Category != "resource" && activeOtherTasks >= _maxConcurrent) continue;
                if (!_activeTasks.Add(task.Id)) continue;
                task.Status = DownloadTaskStatus.Downloading;
                if (task.Category == "resource") activeResources++;
                else activeOtherTasks++;
                starting.Add((task, _taskWorkers.GetValueOrDefault(task.Id, 1)));
            }
            ActiveCount = _activeTasks.Count;
        }

        foreach (var entry in starting)
            _ = RunTask(entry.Task, entry.Workers, entry.Task.RunGeneration);
        if (starting.Count > 0) QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunTask(DownloadTask task, int workers, int generation)
    {
        var speedClock = Stopwatch.StartNew();
        long speedSampleBytes = 0;
        var speedSampleTime = TimeSpan.Zero;
        var lastDataTime = TimeSpan.Zero;
        double smoothedSpeed = 0;
        var speedTimer = new System.Timers.Timer(500);
        speedTimer.Elapsed += (_, _) =>
        {
            if (task.Status != DownloadTaskStatus.Downloading) return;
            var elapsedSinceData = speedClock.Elapsed - lastDataTime;
            if (task.BytesReceived == 0)
                task.Speed = "连接中...";
            else if (elapsedSinceData > TimeSpan.FromSeconds(2))
            {
                task.Speed = "等待数据...";
            }
        };
        speedTimer.Start();

        var ct = task.Cts?.Token ?? CancellationToken.None;
        var connectionBudget = new SemaphoreSlim(Math.Max(1, workers));
        var connectionAllocationLock = new SemaphoreSlim(1, 1);

        try
        {
            var pending = task.Items
                .GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .AsParallel()
                .AsOrdered()
                .Where(i => !DownloadService.ValidFile(i))
                .ToList();
            var total = pending.Count;
            var done = 0;
            var progressLock = new object();
            var loadedByTarget = pending.ToDictionary(item => item.Target, _ => 0L, StringComparer.OrdinalIgnoreCase);
            var totalByTarget = pending.ToDictionary(item => item.Target, item => item.Size, StringComparer.OrdinalIgnoreCase);

            var itemProgresses = pending.Select(item => new ItemProgress
            {
                Target = item.Target,
                FileName = Path.GetFileName(item.Target),
                BytesText = item.Size > 0 ? DownloadTask.FormatBytes(item.Size) : ""
            }).ToList();
            task.BytesReceived = 0;
            task.TotalBytes = pending.Sum(item => item.Size);
            task.CompletedFiles = 0;
            task.Error = "";
            RunOnUi(() =>
            {
                task.ItemProgresses.Clear();
                foreach (var progress in itemProgresses)
                    task.ItemProgresses.Add(progress);
            });

            var itemLookup = itemProgresses.ToDictionary(ip => ip.Target, StringComparer.OrdinalIgnoreCase);

            var downloadTasks = pending.Select(async item =>
            {
                var weight = DownloadService.GetConnectionWeight(item, workers);
                await WaitForConnectionsAsync(connectionBudget, connectionAllocationLock, weight, ct);
                try
                {
                    var fileName = Path.GetFileName(item.Target);
                    if (!IsCurrentRun(task, generation)) return;
                    if (itemLookup.TryGetValue(item.Target, out var ip))
                        ip.StatusText = "下载中";

                    await DownloadService.DownloadOneAsync(item, (target, loaded, totalBytes) =>
                    {
                        lock (progressLock)
                        {
                            loadedByTarget[target] = loaded;
                            if (totalBytes > 0) totalByTarget[target] = totalBytes;
                            task.BytesReceived = loadedByTarget.Values.Sum();
                            task.TotalBytes = totalByTarget.Values.Sum();
                            var now = speedClock.Elapsed;
                            lastDataTime = now;
                            var sampleSeconds = (now - speedSampleTime).TotalSeconds;
                            if (sampleSeconds >= 0.35)
                            {
                                var instantSpeed = Math.Max(0, task.BytesReceived - speedSampleBytes) / sampleSeconds;
                                smoothedSpeed = smoothedSpeed <= 0
                                    ? instantSpeed
                                    : smoothedSpeed * 0.65 + instantSpeed * 0.35;
                                task.Speed = $"{DownloadTask.FormatBytes((long)smoothedSpeed)}/s";
                                speedSampleBytes = task.BytesReceived;
                                speedSampleTime = now;
                            }
                            if (task.TotalBytes > 0)
                                task.Progress = Math.Min(99, (double)task.BytesReceived / task.TotalBytes * 100);
                        }

                        if (!IsCurrentRun(task, generation)) return;
                        if (itemLookup.TryGetValue(item.Target, out var p))
                        {
                            p.BytesText = totalBytes > 0
                                ? $"{DownloadTask.FormatBytes(loaded)} / {DownloadTask.FormatBytes(totalBytes)}"
                                : DownloadTask.FormatBytes(loaded);
                            if (totalBytes > 0)
                                p.Progress = Math.Min(99, (double)loaded / totalBytes * 100);
                        }
                    }, ct, maxAttempts: task.MaxAttempts, maxSegments: weight);

                    if (!IsCurrentRun(task, generation)) return;
                    var current = Interlocked.Increment(ref done);
                    task.CompletedFiles = current;
                    task.Progress = Math.Min(99, (double)current / total * 100);

                    if (itemLookup.TryGetValue(item.Target, out var completedIp))
                    {
                        completedIp.StatusText = "完成";
                        completedIp.Progress = 100;
                        completedIp.IsCompleted = true;
                    }
                }
                finally { connectionBudget.Release(weight); }
            });

            await Task.WhenAll(downloadTasks);

            if (task.PostDownloadAction != null)
            {
                task.StatusText = "安装中";
                task.Speed = "";
                await task.PostDownloadAction();
            }

            if (IsCurrentRun(task, generation))
            {
                task.Progress = 100;
                task.Status = DownloadTaskStatus.Completed;
                task.Speed = "";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRun(task, generation)
                && task.Status != DownloadTaskStatus.Paused
                && task.Status != DownloadTaskStatus.Cancelled)
                task.Status = DownloadTaskStatus.Failed;
        }
        catch (Exception ex)
        {
            if (!IsCurrentRun(task, generation)) return;
            task.Error = ex.Message;
            task.Status = DownloadTaskStatus.Failed;
            DownloadService.WriteTaskLog(task.Name, ex);
        }
        finally
        {
            speedTimer.Stop();
            speedTimer.Dispose();
            lock (_lock)
            {
                _activeTasks.Remove(task.Id);
                ActiveCount = _activeTasks.Count;
            }
            QueueChanged?.Invoke(this, EventArgs.Empty);

            if (IsCurrentRun(task, generation)
                && task.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled)
                task.Tcs?.TrySetResult(task);
            if (IsCurrentRun(task, generation))
                TaskCompleted?.Invoke(this, task);
            if (IsCurrentRun(task, generation) && task.Status == DownloadTaskStatus.Completed)
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        Tasks.Remove(task);
                        _taskWorkers.Remove(task.Id);
                        QueueChanged?.Invoke(this, EventArgs.Empty);
                    }));
                }
                else
                {
                    Tasks.Remove(task);
                    _taskWorkers.Remove(task.Id);
                }
            }
            ScheduleQueuedTasks();
        }
    }

    private static bool IsCurrentRun(DownloadTask task, int generation) =>
        task.RunGeneration == generation;

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

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }

    private async Task DetectNetworkAsync()
    {
        if (!_autoAdjustConcurrency) return;
        if (!await _networkProbeLock.WaitAsync(0)) return;

        try
        {
            await DetectNetworkCoreAsync();
        }
        finally
        {
            _networkProbeLock.Release();
        }
    }

    private async Task DetectNetworkCoreAsync()
    {
        if (!_autoAdjustConcurrency) return;
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            MaxConcurrent = 1;
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.modrinth.com/v2/tag/game_version");
            using var response = await NetworkProbe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            stopwatch.Stop();

            var now = DateTime.UtcNow;
            long totalBytes;
            lock (_lock)
            {
                totalBytes = Tasks.Sum(task => task.BytesReceived);
            }
            var elapsed = Math.Max(1, (now - _lastSampleTime).TotalSeconds);
            var bytesPerSecond = Math.Max(0, totalBytes - _lastSampleBytes) / elapsed;
            _lastSampleBytes = totalBytes;
            _lastSampleTime = now;

            var latency = stopwatch.ElapsedMilliseconds;
            var detected = bytesPerSecond switch
            {
                >= 20 * 1024 * 1024 => 16,
                >= 8 * 1024 * 1024 => 12,
                >= 3 * 1024 * 1024 => 8,
                >= 1 * 1024 * 1024 => 4,
                _ when latency <= 80 => 16,
                _ when latency <= 150 => 12,
                _ when latency <= 250 => 8,
                _ when latency <= 400 => 4,
                _ => 2
            };
            MaxConcurrent = detected;
        }
        catch
        {
            MaxConcurrent = Math.Max(1, Math.Min(_maxConcurrent, 2));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
