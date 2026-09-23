#if !FULL_BUILD
using System.IO;
using Newtonsoft.Json;

namespace QuartzLauncher.Services;

public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class UpdateAnnouncement
{
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>
/// 【开源版占位实现】自动更新机制不包含在开源内容中：
/// 不检查更新、不下载补丁、不提供更新服务端与直接替换 exe 的能力。
/// 完整实现位于私有目录，构建时自动启用（FULL_BUILD）。
/// </summary>
public static class UpdateService
{
    // 版本号从程序集读取（csproj 的 Version），避免发版时忘记同步
    public static readonly string CurrentVersion =
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string DisplayVersion => $"星落 LaunCher {CurrentVersion}";

    public static UpdateAnnouncement LoadAnnouncement()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Launcher", "update-announcement.json");
        try
        {
            if (File.Exists(path))
            {
                var announcement = JsonConvert.DeserializeObject<UpdateAnnouncement>(File.ReadAllText(path));
                if (announcement != null && !string.IsNullOrWhiteSpace(announcement.Version))
                    return announcement;
            }
        }
        catch
        {
        }

        return new UpdateAnnouncement
        {
            Version = CurrentVersion,
            Notes = $"星落 LaunCher {CurrentVersion}\n\n本次更新暂无详细说明。"
        };
    }

    public static bool IsLocalServerRunning(int port = 29071) => false;

    public static void EnsureLocalServerRunning()
    {
    }

    public static Task<UpdateManifest?> CheckAsync(string manifestUrl) => Task.FromResult<UpdateManifest?>(null);

    public static Task<string> DownloadPackageAsync(UpdateManifest manifest, string tempDir,
        IProgress<double>? progress = null)
        => throw new NotSupportedException("开源版不包含自动更新机制。");

    public static void StartApplyAndExit(string newExePath, UpdateManifest? manifest = null)
        => throw new NotSupportedException("开源版不包含自动更新机制。");

    public static void ApplyFromArguments(string[] args)
    {
    }

    public static void RunLocalServer(string[] args)
    {
    }

    public static bool IsLoopbackManifestUrl(string value) => false;

    public static bool TryGetHttpUri(string value, out Uri? uri)
    {
        var valid = Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (!valid) uri = null;
        return valid;
    }
}
#endif
