#if !FULL_BUILD
// ============================================================================
// 【开源版占位实现】账号 / 好友 / 私聊 / 头像模块不包含在开源内容中。
// 完整实现位于私有目录（FULL_BUILD 构建启用），这里保证开源版可正常编译运行，
// 所有需要联网的账号操作都会安全地失败，不会崩溃。
// ============================================================================
using System.Text.Json;
using System.Windows.Media.Imaging;

#pragma warning disable CS0067   // 占位实现的事件只声明不触发（完整版才有真实实现）

namespace QuartzLauncher.Services;

/// <summary>好友（按永久编码建立关系，名字是对方当前游戏 ID）。</summary>
public sealed record AccountFriend(string Code, string Name, bool Online);

/// <summary>一条聊天消息。</summary>
public sealed record ChatMessage(string Channel, string From, string Text, long Timestamp, bool Filtered,
    string Kind = "text", string SenderCode = "", string Quote = "")
{
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp)
        .ToLocalTime().ToString("HH:mm");

    public bool IsImage => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);

    public string Display => $"[{TimeText}] {From}：{Text}";
}

/// <summary>聊天客户端占位：永远不会真正连接。</summary>
public sealed class ChatClient : IDisposable
{
    public ChatClient(string host, string name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "玩家" : name.Trim();
    }

    public string Name { get; private set; }
    public string SenderCode { get; set; } = "";
    public bool IsConnected => false;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? ErrorReceived;
    public event Action? Disconnected;
    public event Action? UsersChanged;
    public event Action<JsonElement>? FriendListPushed;

    public string? GetAvatar(string name) => null;
    public bool IsOnline(string name) => false;

    public Task<bool> ConnectAsync(string channel, CancellationToken token = default) => Task.FromResult(false);

    public Task<bool> SendAsync(string text, string kind = "text", string? channel = null, string quote = "")
        => Task.FromResult(false);

    public Task<bool> SendImageAsync(byte[] imageBytes) => Task.FromResult(false);

    public void Close()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 账号服务占位：不发起任何网络请求。登录态恒为「未登录」，
/// 界面上注册 / 登录会静默失败，联机相关功能不可用。
/// </summary>
public sealed class AccountService
{
    public static AccountService? Current { get; set; }

    public static AccountService Ensure() => Current ??= new AccountService(RelayClient.RelayHost);

    public AccountService(string host)
    {
    }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string Email { get; private set; } = "";
    public List<AccountFriend> Friends { get; private set; } = new();
    public List<AccountFriend> Requests { get; private set; } = new();

    public bool IsLoggedIn => false;
    public bool NeedsReLogin => false;
    public ChatClient? Inbox => null;

    public event Action? Changed;
    public event Action<ChatMessage>? DirectMessage;
    public event Action<string>? DirectError;

    public static (string Code, string Token, string Name) LoadLocal() => ("", "", "");

    public void Logout()
    {
    }

    public Task<string?> BindEmailAsync(string email, string password)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<(string? Code, string? Error)> RecoverCodeAsync(string email, string password)
        => Task.FromResult<(string?, string?)>((null, "联机模块不包含在开源内容中"));

    public Task<string?> RequestFriendAsync(string code)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<string?> AcceptRequestAsync(string code)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<string?> DeclineRequestAsync(string code)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<string?> AddFriendAsync(string friendCode)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<string?> RemoveFriendAsync(string friendCode)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<bool> RefreshFriendsAsync() => Task.FromResult(false);

    public Task<string?> RegisterAsync(string password, string name)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<string?> LoginAsync(string code, string password, string name)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<bool> TryRestoreAsync(string name) => Task.FromResult(false);

    public Task<string?> DeleteAsync(string password)
        => Task.FromResult<string?>("联机模块不包含在开源内容中");

    public Task<bool> SendDirectAsync(string targetCode, string text, string kind = "text", string quote = "")
        => Task.FromResult(false);
}

/// <summary>本地好友备注（旧版按名字的好友列表）。</summary>
public sealed record FriendEntry(string Name, string Note, long AddedAt);

public static class FriendService
{
    public static List<FriendEntry> Load() => new();

    public static void Save(List<FriendEntry> friends)
    {
    }

    public static bool Add(string name, string note = "") => false;

    public static bool Remove(string name) => false;

    public static bool Contains(string name) => false;
}

public static class DirectMessageStore
{
    public static void Append(string peer, ChatMessage message)
    {
    }

    public static List<ChatMessage> Load(string peer) => new();

    public static void PruneAll()
    {
    }
}

public static class AccountDialogs
{
    public sealed record LoginInput(string Code, string Password);

    public static string? ShowRegister(System.Windows.Window? owner, string playerName) => null;

    public static LoginInput? ShowLogin(System.Windows.Window? owner, string playerName) => null;

    public static string? ShowAccountManage(System.Windows.Window? owner, string code) => null;

    public static string? ShowReLogin(System.Windows.Window? owner, string code) => null;

    public static (string Email, string Password)? ShowBindEmail(System.Windows.Window? owner, string currentEmail) => null;

    public static string? ShowRecoverCode(System.Windows.Window? owner) => null;

    public static string? ShowDeleteAccount(System.Windows.Window? owner, string code) => null;
}

public static class CodePromptDialog
{
    public static void Show(string expectedCode)
    {
    }
}

public static class AvatarService
{
    public static Task<BitmapSource?> GetAvatarBitmapAsync(CancellationToken token = default)
        => Task.FromResult<BitmapSource?>(null);

    public static Task<string?> GetAvatarBase64Async(CancellationToken token = default)
        => Task.FromResult<string?>(null);

    public static BitmapSource? TryCropFace(string? skinPath) => null;
}
#endif
