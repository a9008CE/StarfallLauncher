namespace QuartzLauncher.Services;

public enum PresetSyncStatus
{
    Success,
    Failed,
    Busy,
    Throttled,
    Unsupported,
}

public sealed class PresetSyncResult
{
    private PresetSyncResult(PresetSyncStatus status, int fetched, int total, int failedCount, string message)
    {
        Status = status;
        Fetched = fetched;
        Total = total;
        FailedCount = failedCount;
        Message = message;
    }

    public PresetSyncStatus Status { get; }

    public int Fetched { get; }

    public int Total { get; }

    public int FailedCount { get; }

    public string Message { get; }

    public bool IsSuccess => Status == PresetSyncStatus.Success;

    public static PresetSyncResult Ok(int fetched, int total, int failedCount)
        => new(PresetSyncStatus.Success, fetched, total, failedCount, "");

    public static PresetSyncResult Fail(string message)
        => new(PresetSyncStatus.Failed, 0, 0, 0, message);

    public static PresetSyncResult Busy()
        => new(PresetSyncStatus.Busy, 0, 0, 0, "");

    public static PresetSyncResult Throttled()
        => new(PresetSyncStatus.Throttled, 0, 0, 0, "");

    public static PresetSyncResult Unsupported()
        => new(PresetSyncStatus.Unsupported, 0, 0, 0, "");
}
