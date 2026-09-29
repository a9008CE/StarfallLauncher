#if !FULL_BUILD
namespace QuartzLauncher.Services;

/// <summary>
/// 【开源版占位实现】官方预设图库需要访问更新服务器，不包含在开源内容中。
/// 完整实现位于私有目录，构建时自动启用（FULL_BUILD）。
/// </summary>
public static class PresetLibraryService
{
    public static void SyncOnStartup()
    {
    }

    public static Task<PresetSyncResult> SyncAsync(bool force = false)
        => Task.FromResult(PresetSyncResult.Unsupported());
}
#endif
