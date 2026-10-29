#if !FULL_BUILD
namespace QuartzLauncher.Services;

public sealed record ServerDirectoryItem(
    string Id,
    string Name,
    string Description,
    string Players,
    string Ping,
    string CountryCode,
    string IconUrl,
    string PageUrl);

public sealed record ServerDirectoryResult(List<ServerDirectoryItem> Items, int Page, int TotalPages);

/// <summary>
/// 【开源版占位实现】服务器列表数据源不包含在开源内容中。
/// 完整实现位于私有目录，构建时自动启用（FULL_BUILD）。
/// </summary>
public static class MinecraftServerDirectoryService
{
    public static Task<ServerDirectoryResult> GetOnlineServersAsync(int page = 1, bool showModOnly = false)
        => Task.FromResult(new ServerDirectoryResult(new List<ServerDirectoryItem>(), page, 0));
}
#endif
