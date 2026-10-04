using Newtonsoft.Json;

namespace QuartzLauncher.Models;

public class Settings
{
#if FULL_BUILD
    public const string DefaultUpdateManifestUrl = "https://starfallmc.top/update.json";
#else
    // 开源版不包含自动更新机制，更新服务端地址不予公开
    public const string DefaultUpdateManifestUrl = "";
#endif

    public string JavaPath { get; set; } = "";
    public List<JavaInfo> DetectedJavas { get; set; } = new();
    public bool AutoSelectJava { get; set; } = true;
    public int MemoryMb { get; set; } = 4096;
    public string PlayerName { get; set; } = "Steve";
    public int DownloadWorkers { get; set; } = 64;
    public string AuthMode { get; set; } = AuthModes.Offline;
    public string AuthServer { get; set; } = "https://littleskin.cn/api/yggdrasil";
    public string AuthAccount { get; set; } = "";
    public string AuthPlayerName { get; set; } = "";
    public string AuthUuid { get; set; } = "";
    public string AuthAccessToken { get; set; } = "";
    public string AuthClientToken { get; set; } = "";
    public string AuthInjectorPath { get; set; } = "";
    public string AuthAvatarPath { get; set; } = "";
    public string CustomSkinPath { get; set; } = "";
    public string ThemeName { get; set; } = "默认";
    public string ThemePrimary { get; set; } = "";
    public string ThemeBg { get; set; } = "";
    public string ThemeCard { get; set; } = "";
    public string ThemeText { get; set; } = "";
    public string ThemeTextMuted { get; set; } = "";
    public string ThemeSidebar { get; set; } = "";
    public string ThemeBorder { get; set; } = "";
    public string ThemeBackgroundImage { get; set; } = "";
    /// <summary>是否轮播 Launcher/backgrounds 里的全部预设背景。</summary>
    public bool ThemeBackgroundCarousel { get; set; }
    /// <summary>轮播切换间隔（秒）。</summary>
    public int ThemeBackgroundCarouselSeconds { get; set; } = 12;
    /// <summary>未轮播时手动选中的预设序号（按文件名排序）。</summary>
    public int ThemeBackgroundIndex { get; set; }
    public string ThemeDanger { get; set; } = "";
    public string ThemeSuccess { get; set; } = "";
    public string ThemeMode { get; set; } = "light";
    /// <summary>开箱风格 = 浅色科技（毛玻璃 + 浅色）。在 UI 风格列表里叫「默认」且排第一。</summary>
    public string UiStyle { get; set; } = "frosted";
    public string DownloadSource { get; set; } = "hybrid_priority";
    public string VersionIsolationMode { get; set; } = VersionIsolationModes.All;
    public bool AutoMemory { get; set; } = false;
    public bool MemoryOptimize { get; set; } = false;

    /// <summary>累计游戏时长（秒），用于联机功能的防滥用门槛。</summary>
    public long TotalPlaySeconds { get; set; } = 0;

    /// <summary>
    /// 联机房间是否允许非正版玩家进入（对应局域网服务端 online-mode=false）。
    /// 开启后「开房用」的启动会改用离线会话，使局域网世界不作正版验证。
    /// 注意：默认关闭 —— 强制离线会导致「加入别人的服务器」时报无效会话。
    /// </summary>
    public bool LanAllowNonPremium { get; set; } = false;
    public string LastFabricLoader { get; set; } = "";
    public string LastForgeLoader { get; set; } = "";
    public bool AutoInstallLoader { get; set; } = true;
    public string CurseForgeApiKey { get; set; } = "";
    public string ModDownloadSource { get; set; } = "Modrinth";
    public string ModDownloadSources { get; set; } = "";
    public bool AutoDownloadThreads { get; set; } = true;
    public int ManualDownloadThreads { get; set; } = 3;
    public int ConcurrentDownloads { get; set; } = 4;
    public int DownloadRetryCount { get; set; } = 3;
    public string ModDownloadPath { get; set; } = "";
    public bool AutoUpdate { get; set; } = false;
    public bool RunAsUpdateServer { get; set; } = true;
    public string UpdateManifestUrl { get; set; } = DefaultUpdateManifestUrl;
    /// <summary>最近一次标记已读的公告键（启动器版本 + 公告版本）。</summary>
    public string AnnouncementReadVersion { get; set; } = "";
    public string MicrosoftRefreshToken { get; set; } = "";
    public long MicrosoftTokenExpiresAt { get; set; }
    public List<MicrosoftAccount> MicrosoftAccounts { get; set; } = new();
    public string ActiveMicrosoftAccountId { get; set; } = "";
    public string ThemeTransitionStyle { get; set; } = "topLeft";
    public string PageAnimationStyle { get; set; } = "slide";
    public bool AdvancedAnimationEnabled { get; set; }
    public bool SidebarTextLeftAligned { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public string ResourceCenterStyle { get; set; } = "list";
    public int TearAnimationDurationMs { get; set; } = 760;
    public int TearApexSharpness { get; set; } = 50;
    public bool TearRandomDirection { get; set; } = true;
    public string TearDirection { get; set; } = "vertical";
    public List<WebsiteShortcut> WebsiteShortcuts { get; set; } = new();

    /// <summary>
    /// 新手教程是否已看过（看完或跳过后置 true，之后永不再弹）。
    /// 默认给 true：老配置里根本没有这个字段，反序列化后保留默认值，
    /// 升级上来的用户不会被教程打扰；只有真正的新装目录才会由
    /// <see cref="Services.SettingsService.Load"/> 显式改成 false。
    /// </summary>
    public bool OnboardingCompleted { get; set; } = true;
}

public static class VersionIsolationModes
{
    public const string None = "none";
    public const string Modded = "modded";
    public const string All = "all";
}

public class WebsiteShortcut
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Icon { get; set; } = "\uE774";
    public string IconUrl { get; set; } = "";
}
