#if !FULL_BUILD
// ============================================================================
// 【开源版占位实现】联机模块不包含在开源内容中。
// 局域网自动发现、房间发布、中继客户端、Mod 同步、防滥用门槛等均为占位，
// 调用后不会产生任何实际行为。完整实现位于私有目录（FULL_BUILD 构建启用）。
// ============================================================================

namespace QuartzLauncher.Services;

public sealed record LanWorldInfo(string Motd, int Port, DateTime DetectedAt);

public sealed class LanWorldWatcher : IDisposable
{
    public LanWorldWatcher(string? instanceRoot = null)
    {
    }

    public event Action<LanWorldInfo>? WorldDetected;
    public event Action<int>? WorldClosed;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

public sealed record LocalModEntry(string FileName, long Size, string Sha1 = "")
{
    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, Size / 1024)} KB";
}

public sealed record LocalModSnapshot(bool Modded, string Loader, List<LocalModEntry> Mods)
{
    public string KindText => Modded ? "Mod" : "原版";

    public string LoaderText => string.IsNullOrWhiteSpace(Loader)
                                || Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
        ? "原版"
        : char.ToUpperInvariant(Loader[0]) + Loader[1..];

    public string SummaryText => Modded
        ? $"{LoaderText} · {Mods.Count} 个 Mod"
        : "原版（无需同步 Mod）";
}

public static class LocalModScanner
{
    public static LocalModSnapshot Scan(string gameDirectory, string? loader, bool withHash = false)
        => new(false, loader ?? "vanilla", new List<LocalModEntry>());

    public static string ComputeSha1(string path) => "";
}

public static class QuickPlayRequest
{
    public static string? Address { get; set; }
    public static bool HasPending => false;
    public static string? Consume() => null;
}

public static class PlayTimeGate
{
    public const long RequiredSeconds = 24 * 3600;

    public static bool IsUnlocked(QuartzLauncher.Models.Settings settings) => true;

    public static long RemainingSeconds(QuartzLauncher.Models.Settings settings) => RequiredSeconds;

    public static string DescribeTotal(QuartzLauncher.Models.Settings settings) => Format(0);

    public static string DescribeRemaining(QuartzLauncher.Models.Settings settings) => Format(RequiredSeconds);

    public static double ProgressRatio(QuartzLauncher.Models.Settings settings) => 0;

    public static string Format(long seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours} 小时 {time.Minutes} 分钟"
            : $"{time.Minutes} 分钟";
    }
}

public sealed class RelayClient : IDisposable
{
    public const string RelayHost = "";
    public const int ControlPort = 7000;
    public const int ChannelPort = 7001;
    public const string Secret = "";

    public RelayClient(string host, int localPort)
    {
    }

    public string? RoomCode { get; private set; }
    public int DataPort { get; private set; }
    public string PublicHost { get; private set; } = "";
    public bool IsRunning => false;
    public int PeerCount { get; private set; }

    public event Action<string>? Log;

    public Task<bool> CreateRoomAsync(string motd, string mc, string kind, string loader,
        int modCount, string password, int maxPlayers = 10,
        IReadOnlyList<LocalModEntry>? mods = null, string owner = "", string ownerCode = "",
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public void Close()
    {
    }

    public void Dispose()
    {
    }
}

public sealed record LobbyMod(string Name, long Size, string Sha1)
{
    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, Size / 1024)} KB";
}

public sealed record LobbyRoom(
    string Room, string Addr, string Owner, string Motd, string Mc, string Kind, string Loader,
    int ModCount, int Players, int Max, bool Locked, int Port, List<LobbyMod> Mods)
{
    public string TitleText => $"房间 {Room}";
    public string OwnerText => "未知";
    public string AddrText => Addr;
    public string KindText => Kind == "modded" ? "Mod" : "原版";
    public string KindDetail => "未提供";
    public string PlayerText => "0/0";
    public string CodeText => Room;
    public string ConnectAddress => "";
}

public sealed record JoinResult(bool Ok, string Message, string Room, string Addr, string Token,
    string Host, int Port, string Motd, string Kind, string Loader, string Mc, int ModCount);

public sealed class RelayLobby : IDisposable
{
    public RelayLobby(string host = "", int httpPort = 7002)
    {
    }

    public int LocalPort { get; private set; }
    public string? JoinedRoom { get; private set; }

    public Task<List<LobbyRoom>> GetRoomsAsync(CancellationToken token = default)
        => Task.FromResult(new List<LobbyRoom>());

    public Task<JoinResult> JoinAsync(string room, string password, CancellationToken token = default)
        => Task.FromResult(new JoinResult(false, "联机模块不包含在开源内容中", room, "", "", "", 0, "", "", "", "", 0));

    public Task<string> SyncModsAsync(IReadOnlyList<LobbyMod> roomMods, string gameDir, string mc, string loader,
        IProgress<string>? progress = null, CancellationToken token = default)
        => Task.FromResult("联机模块不包含在开源内容中");

    public bool StartProxy(JoinResult join) => false;

    public void StopProxy()
    {
    }

    public void Dispose()
    {
    }
}
#endif
