using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

/// <summary>
/// 联机大厅：展示公开房间、查看详情、加入房间（本地代理接入中继）。
/// </summary>
public partial class MultiplayerPage : Page
{
    private const string LockedMessage = "为防止滥用，请您达到累计游戏时间 24 小时后才可使用本功能。";

    private readonly RelayLobby _lobby = new();
    private readonly DispatcherTimer _refreshTimer;
    private LobbyRoom? _selected;
    private bool _busy;
    private bool _joining;
    private List<LobbyRoom> _rooms = new();

    public MultiplayerPage()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await LoadRoomsAsync(silent: true);
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            _friendRefreshTimer?.Stop();
            _chat?.Dispose();
            _chat = null;
            _chatConnecting = null;
            ChatImagePreview.Visibility = Visibility.Collapsed;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 游戏时长解锁机制已移除，联机大厅直接可用
        GateCard.Visibility = Visibility.Collapsed;
        LobbyRoot.Visibility = Visibility.Visible;

        // 尽早挂上收件箱事件：不进好友页也能收私聊红点/提示音
        _account ??= AccountService.Ensure();
        WireInbox();

        await LoadRoomsAsync();
        if (!IsLoaded) return;   // 连接期间用户可能已经切走
        _refreshTimer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadRoomsAsync();

    // ===== 世界聊天（全局主频道，敏感词由中继服务端过滤）=====

    private ChatClient? _chat;
    private Task? _chatConnecting;

    /// <summary>主窗口的联机专用侧栏调用：lobby / chat / myrooms。</summary>
    public void ShowSection(string section)
    {
        if (section == "chat")
        {
            ShowView(ChatView);
            _ = EnsureChatAsync();
            return;
        }

        if (section == "myrooms")
        {
            ShowView(MyRoomsView);
            return;
        }

        if (section == "friends")
        {
            ShowView(FriendsView);

            // 先同步渲染一次（注册/登录表单 + 好友列表提示），保证页面永远不空白，
            // 再去联网恢复登录态、同步云端好友
            try
            {
                RenderFriendAccount();
                RenderFriends();
            }
            catch
            {
                // 渲染异常不影响页面显示
            }

            _ = EnsureChatAsync();      // 在线状态来自聊天
            _ = EnsureAccountAsync();   // 恢复登录态并同步云端好友
            WireInbox();                // 私聊实时到达
            ClearUnread();              // 进好友页就算已看过
            StartFriendAutoRefresh();   // 停留在本页时每 15 秒自动同步
            return;
        }

        ShowView(LobbyView);
    }

    /// <summary>侧栏切换：三个视图互斥显示。</summary>
    private void ShowView(UIElement view)
    {
        ChatImagePreview.Visibility = Visibility.Collapsed;   // 切视图时关掉图片预览遮罩

        LobbyView.Visibility = ReferenceEquals(view, LobbyView) ? Visibility.Visible : Visibility.Collapsed;
        ChatView.Visibility = ReferenceEquals(view, ChatView) ? Visibility.Visible : Visibility.Collapsed;
        MyRoomsView.Visibility = ReferenceEquals(view, MyRoomsView) ? Visibility.Visible : Visibility.Collapsed;
        FriendsView.Visibility = ReferenceEquals(view, FriendsView) ? Visibility.Visible : Visibility.Collapsed;
        PrivateChatView.Visibility = ReferenceEquals(view, PrivateChatView) ? Visibility.Visible : Visibility.Collapsed;

        if (ReferenceEquals(view, ChatView))
            ChatScroll.ScrollToEnd();
    }

    private async Task EnsureChatAsync()
    {
        if (_chat is { IsConnected: true }) return;

        // 多个入口（切页/发消息/发图）可能同时调用：复用同一个连接任务，避免并发建连互相顶掉
        if (_chatConnecting is { IsCompleted: false })
        {
            await _chatConnecting;
            return;
        }

        _chatConnecting = ConnectChatAsync();
        try
        {
            await _chatConnecting;
        }
        finally
        {
            _chatConnecting = null;
        }
    }

    private async Task ConnectChatAsync()
    {
        ChatStatus.Text = "正在连接世界频道…";
        var name = App.Settings.Data.AuthMode == QuartzLauncher.Models.AuthModes.Offline
            ? App.Settings.Data.PlayerName
            : App.Settings.Data.AuthPlayerName;
        if (string.IsNullOrWhiteSpace(name)) name = "玩家";

        _chat?.Dispose();
        _chat = new ChatClient(RelayClient.RelayHost, name);
        _chat.MessageReceived += message => Dispatcher.BeginInvoke(() => AppendChatMessage(message));
        _chat.ErrorReceived += text => Dispatcher.BeginInvoke(() => ChatStatus.Text = text);
        _chat.UsersChanged += () => Dispatcher.BeginInvoke(RenderChat);
        _chat.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            if (ChatView.Visibility == Visibility.Visible)
                ChatStatus.Text = "连接已断开，发送消息会自动重连";
        });

        var client = _chat;
        var ok = await client.ConnectAsync("world");
        if (!ReferenceEquals(_chat, client)) return;   // 期间已被替换/销毁，别覆盖新状态
        ChatChannelText.Text = ok ? "# 世界频道" : "";
        ChatStatus.Text = ok
            ? "已连接 · 注意文明发言，违规内容会被过滤"
            : "连接失败，请稍后再试";
    }

    /// <summary>时间分隔条：今天 / 昨天 / 具体日期（QQ 风格）。</summary>
    private static UIElement BuildTimeDivider(long timestamp)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var label = time.Date == today
            ? $"今天 {time:HH:mm}"
            : time.Date == today.AddDays(-1)
                ? $"昨天 {time:HH:mm}"
                : $"{time:MM-dd HH:mm}";

        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 8)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return text;
    }

    /// <summary>同一个人 5 分钟内连发，只有第一条显示头像和尾巴（QQ 的连续消息合并）。</summary>
    private static bool ShouldShowAvatar(IReadOnlyList<ChatMessage> list, int index)
    {
        if (index <= 0) return true;
        var previous = list[index - 1];
        var current = list[index];
        return !string.Equals(previous.From, current.From, StringComparison.OrdinalIgnoreCase)
               || current.Timestamp - previous.Timestamp > 5 * 60 * 1000;
    }

    private ChatMessage? _lastWorldMessage;

    /// <summary>待发送的引用（双击气泡设置，发送后清空）</summary>
    private string _quoteText = "";
    private bool _sendingChat;
    private bool _sendingPrivate;
    private readonly List<ChatMessage> _chatHistory = new();

    /// <summary>发送成功后清掉引用与提示（状态栏写明「发送后自动清除」）。</summary>
    private void ClearQuote()
    {
        if (_quoteText.Length == 0) return;
        _quoteText = "";
        ChatStatus.Text = "";
        PrivateChatStatus.Text = "";
    }

    private void AppendChatMessage(ChatMessage message)
    {
        _chatHistory.Add(message);
        while (_chatHistory.Count > 300) _chatHistory.RemoveAt(0);

        // 连续消息合并：同一人 5 分钟内连发只有第一条显示头像和尾巴
        var showAvatar = _lastWorldMessage == null
                         || !string.Equals(_lastWorldMessage.From, message.From, StringComparison.OrdinalIgnoreCase)
                         || message.Timestamp - _lastWorldMessage.Timestamp > 5 * 60 * 1000;
        // 同一人 5 分钟内的第一条消息前插入时间分隔条
        if (_lastWorldMessage == null || message.Timestamp - _lastWorldMessage.Timestamp > 5 * 60 * 1000)
            ChatMessages.Children.Add(BuildTimeDivider(message.Timestamp));
        _lastWorldMessage = message;

        ChatMessages.Children.Add(BuildChatRow(message, null, showAvatar));
        while (ChatMessages.Children.Count > 300) ChatMessages.Children.RemoveAt(0);

        if (ChatView.Visibility == Visibility.Visible)
            ChatScroll.ScrollToEnd();
    }

    /// <summary>收听到新头像时重画一遍，让历史消息也能显示头像。</summary>
    private void RenderChat()
    {
        ChatMessages.Children.Clear();
        for (var i = 0; i < _chatHistory.Count; i++)
            ChatMessages.Children.Add(BuildChatRow(_chatHistory[i], null, ShouldShowAvatar(_chatHistory, i)));

        if (ChatView.Visibility == Visibility.Visible)
            ChatScroll.ScrollToEnd();
    }

    /// <summary>
    /// 自己发的消息靠右，别人的靠左；支持图片消息。
    /// source 是该消息来自哪个聊天连接（世界频道 / 私聊各自独立，昵称与头像缓存也独立）。
    /// </summary>
    private UIElement BuildChatRow(ChatMessage message, ChatClient? source = null, bool showAvatar = true)
    {
        source ??= _chat;
        var isMine = string.Equals(message.From, source?.Name, StringComparison.OrdinalIgnoreCase);

        // QQ 经典浅蓝（自己的气泡）
        var mineBubble = new SolidColorBrush(Color.FromRgb(0x9C, 0xDD, 0xFF));
        mineBubble.Freeze();

        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildChatAvatar(message.From, source);
        avatar.Cursor = System.Windows.Input.Cursors.Hand;
        avatar.ToolTip = "点击查看资料";
        avatar.MouseLeftButtonUp += (_, _) => ShowUserCard(message.From, message.SenderCode ?? "");
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, 0)
        };
        bubble.Background = isMine ? mineBubble : (Brush)FindResource("DialogCardBrush");

        var content = new StackPanel();

        var header = new TextBlock { FontSize = 12, Text = $"{message.From} · {message.TimeText}" };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        if (!isMine) content.Children.Add(header);

        if (message.IsImage)
        {
            var image = BuildChatImage(message.Text);
            if (image != null)
            {
                content.Children.Add(image);
            }
            else
            {
                content.Children.Add(new TextBlock { Text = $"（图片无法显示：{message.Text.Length} 字符）", FontSize = 11 });
            }
        }
        else
        {
            var text = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, LineHeight = 24 };
            if (isMine) text.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x3A));
            else text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Inlines.Add(new System.Windows.Documents.Run(message.Text));
            if (message.Filtered) text.Opacity = 0.75;
            content.Children.Add(text);
        }

        // 引用块（显示在气泡顶部）
        if (!string.IsNullOrWhiteSpace(message.Quote))
        {
            var quoted = new TextBlock
            {
                Text = message.Quote,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 0, 0, 4)
            };
            quoted.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            content.Children.Insert(0, quoted);
        }

        bubble.Child = content;

        // 双击气泡 = 引用回复
        bubble.Cursor = System.Windows.Input.Cursors.Hand;
        bubble.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2) return;
            args.Handled = true;
            var label = message.Text.Length > 40 ? message.Text[..40] + "…" : message.Text;
            _quoteText = $"{message.From}：{label}";
            ChatStatus.Text = "[引用] " + _quoteText + "（发送后自动清除，双击其它气泡可替换）";
            PrivateChatStatus.Text = ChatStatus.Text;
        };

        // QQ 风格小尖角：指向头像一侧，颜色和气泡一致
        var bubbleBrush = isMine ? mineBubble : (Brush)FindResource("DialogCardBrush");
        var tail = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(isMine ? "M 0,0 L 7,6 L 0,12 Z" : "M 7,0 L 0,6 L 7,12 Z"),
            Fill = bubbleBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };

        // 连续消息合并：同一人连发时只有第一条显示头像和尾巴，
        // 后面的用等宽占位撑住（头像 42 + 尖角 7），保证气泡边缘和第一条对齐、不往后移
        if (isMine)
        {
            line.Children.Add(bubble);
            if (showAvatar)
            {
                line.Children.Add(tail);
                line.Children.Add(avatar);
            }
            else
            {
                line.Children.Add(new Border { Width = 49 });
            }
        }
        else
        {
            if (showAvatar)
            {
                line.Children.Add(avatar);
                line.Children.Add(tail);
            }
            else
            {
                line.Children.Add(new Border { Width = 49 });
            }
            line.Children.Add(bubble);
        }

        Grid.SetColumn(line, 0);
        Grid.SetColumnSpan(line, 3);
        row.Children.Add(line);
        return row;
    }

    /// <summary>点击头像弹出的用户卡片：头像 + 昵称 + 编码 + 加好友 / 私聊。</summary>
    private void ShowUserCard(string name, string code)
    {
        // 消息里没带编码时做本地回退：先看是不是自己，再用好友列表按名字反查。
        // 注意：服务端可能给聊天昵称加随机后缀（9008CE → 9008CE5265），所以要支持前缀匹配。
        if (string.IsNullOrWhiteSpace(code))
        {
            static bool NameMatches(string candidate, string target) =>
                !string.IsNullOrWhiteSpace(candidate)
                && !string.IsNullOrWhiteSpace(target)
                && (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));

            var current = AccountService.Current;
            if (current != null)
            {
                if (NameMatches(current.Name, name)) code = current.Code;

                if (string.IsNullOrWhiteSpace(code) && current.IsLoggedIn)
                {
                    var friend = current.Friends
                        .Where(f => NameMatches(f.Name, name))
                        .OrderByDescending(f => f.Name.Length)
                        .FirstOrDefault();
                    if (friend != null) code = friend.Code;
                }
            }
        }

        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = "用户信息",
            Width = 330,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16)
        };
        card.SetResourceReference(Border.BackgroundProperty, "DialogCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var root = new StackPanel();

        var avatar = BuildChatAvatar(name, _chat);
        avatar.Width = 64;
        avatar.Height = 64;
        avatar.CornerRadius = new CornerRadius(10);
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(avatar);

        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 4)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        root.Children.Add(nameText);

        var info = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(code) ? "对方未登录账号（没有编码）" : $"编码 {code}",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        root.Children.Add(info);

        var account = _account;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var isFriend = code.Length > 0 && account != null
                       && account.Friends.Any(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

        if (account is { IsLoggedIn: true } && code.Length > 0 && !isFriend)
        {
            var add = new Button
            {
                Content = "加为好友", Height = 34, Padding = new Thickness(16, 2, 16, 2), Cursor = Cursors.Hand
            };
            add.SetResourceReference(FrameworkElement.StyleProperty, "BtnPrimary");
            add.Click += async (_, _) =>
            {
                add.IsEnabled = false;
                var error = await account.RequestFriendAsync(code);
                add.Content = error ?? "已申请";
                RenderFriends();
            };
            actions.Children.Add(add);
        }

        if (account is { IsLoggedIn: true } && isFriend)
        {
            var chat = new Button
            {
                Content = "私聊", Height = 34, Padding = new Thickness(16, 2, 16, 2), Cursor = Cursors.Hand
            };
            chat.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
            chat.Click += (_, _) =>
            {
                window.Close();
                OpenPrivateChat(code, name);
            };
            actions.Children.Add(chat);
        }

        root.Children.Add(actions);

        var close = new Button
        {
            Content = "关闭",
            Height = 34,
            Padding = new Thickness(16, 2, 16, 2),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        close.Click += (_, _) => window.Close();
        root.Children.Add(close);

        card.Child = root;
        window.Content = new Grid { Margin = new Thickness(24), Children = { card } };
        window.ShowDialog();
    }

    private Border BuildChatAvatar(string name, ChatClient? source = null)
    {
        source ??= _chat;
        var avatar = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        var data = source?.GetAvatar(name) ?? _chat?.GetAvatar(name);
        if (!string.IsNullOrWhiteSpace(data))
        {
            // 画刷按「名字 + 头像内容」缓存，避免每次刷新都重新解码 base64（这是卡顿主因）
            var key = name + "#" + data.Length + "#" + data.GetHashCode();
            if (AvatarBrushCache.TryGetValue(key, out var cached))
            {
                avatar.Background = cached;
            }
            else
            {
                try
                {
                    var bytes = Convert.FromBase64String(data);
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new System.IO.MemoryStream(bytes);
                    bitmap.DecodePixelWidth = 84;
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    var brush = new System.Windows.Media.ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                    RenderOptions.SetBitmapScalingMode(brush, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                    brush.Freeze();
                    AvatarBrushCache[key] = brush;
                    avatar.Background = brush;
                    if (AvatarBrushCache.Count > 300)
                        AvatarBrushCache.Clear();   // 简单上限：长时间挂机遇到大量玩家时不让内存只增不减
                }
                catch
                {
                    // 头像解码失败时退回字母头像
                }
            }
        }

        if (avatar.Background == null)
        {
            avatar.Background = (Brush)FindResource("PrimaryBrush");
            avatar.Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant(),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return avatar;
    }

    private UIElement? BuildChatImage(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new System.IO.MemoryStream(bytes);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            var image = new Image
            {
                Source = bitmap,
                MaxWidth = 320,
                MaxHeight = 260,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "单击查看大图"
            };
            image.MouseLeftButtonUp += (_, _) => ShowImagePreview(bitmap);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>密码框占位提示：空密码且未聚焦时显示。</summary>
    private static void WirePasswordWatermark(PasswordBox box, TextBlock watermark)
    {
        void Update() =>
            watermark.Visibility = box.Password.Length == 0 && !box.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;

        box.PasswordChanged += (_, _) => Update();
        box.GotFocus += (_, _) => Update();
        box.LostFocus += (_, _) => Update();
        box.Loaded += (_, _) => Update();
    }

    // ===== 好友（云端账号）=====

    private AccountService? _account;

    private async Task EnsureAccountAsync()
    {
        _account ??= AccountService.Ensure();
        WireInbox();   // 进好友页前也可能已经收到私聊/申请，尽早接线
        if (!_account.IsLoggedIn)
            await _account.TryRestoreAsync(CurrentPlayerName());
        else
            await _account.RefreshFriendsAsync();   // 每次进页面都同步一次，别人加你能及时看到
        RenderFriendAccount();
        RenderFriends();
    }

    private static string CurrentPlayerName()
    {
        var settings = App.Settings.Data;
        var name = settings.AuthMode == QuartzLauncher.Models.AuthModes.Offline
            ? settings.PlayerName
            : settings.AuthPlayerName;
        return string.IsNullOrWhiteSpace(name) ? "玩家" : name.Trim();
    }

    private void RenderFriendAccount()
    {
        var account = _account;
        var loggedIn = account is { IsLoggedIn: true };
        var needsReLogin = account is { NeedsReLogin: true };

        // 令牌失效但本地还记着编码：仍然显示编码（绝不能把编码藏起来），只要求重新输入密码
        FriendAuthPanel.Visibility = loggedIn || needsReLogin ? Visibility.Collapsed : Visibility.Visible;
        FriendAccountPanel.Visibility = loggedIn || needsReLogin ? Visibility.Visible : Visibility.Collapsed;
        ReLoginBtn.Visibility = needsReLogin ? Visibility.Visible : Visibility.Collapsed;

        if (account == null || (!loggedIn && !needsReLogin)) return;

        MyCodeText.Text = account.Code;
        MyAccountHint.Text = needsReLogin
            ? "登录状态已过期（中继重启会导致令牌失效），编码仍然有效：点「重新登录」输一次密码即可，好友不会丢。"
            : "编码是找回好友的唯一凭据，请抄下来保存；账号名会自动跟随你的游戏 ID。";
    }

    /// <summary>账户管理：把注销 / 退出 / 复制编码等操作收进一个弹窗，页面上只留一个按钮。</summary>
    private void AccountManage_Click(object sender, RoutedEventArgs e)
    {
        var account = _account;
        if (account is null) return;

        var choice = AccountDialogs.ShowAccountManage(Window.GetWindow(this), account.Code);
        switch (choice)
        {
            case "copy" when account is { IsLoggedIn: true }:
                CopyCode_Click(this, new RoutedEventArgs());
                break;
            case "email" when account is { IsLoggedIn: true }:
                BindEmail_Click(this, new RoutedEventArgs());
                break;
            // 令牌失效时复制编码/绑定邮箱没意义，先引导重新登录
            case "copy":
            case "email":
                ReLogin_Click(this, new RoutedEventArgs());
                break;
            case "relogin":
                ReLogin_Click(this, new RoutedEventArgs());
                break;
            case "logout":
                Logout_Click(this, new RoutedEventArgs());
                break;
            case "delete":
                DeleteAccount_Click(this, new RoutedEventArgs());
                break;
        }
    }

    /// <summary>找回编码（邮箱 + 密码，成功后自动复制到剪贴板）</summary>
    private void RecoverCode_Click(object sender, RoutedEventArgs e)
    {
        var code = AccountDialogs.ShowRecoverCode(Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(code))
        {
            FriendStatusText.Text = "已取消找回";
            return;
        }

        var copied = true;
        try
        {
            System.Windows.Clipboard.SetText(code);
        }
        catch
        {
            copied = false;
        }

        FriendStatusText.Text = copied ? $"你的编码是 {code}（已复制到剪贴板）" : $"你的编码是 {code}";


        AnimatedMessageBox.Show(
            $"你的编码是：{code}\n\n"
            + (copied ? "已自动复制到剪贴板。" : "请手动抄写下来。")
            + "\n\n建议立刻记到手机备忘录或纸上：编码是找回好友的唯一凭据，"
            + "忘了就只能靠绑定的邮箱 + 密码再查一次。",
            "找回成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>绑定邮箱（用于找回编码）</summary>
    private async void BindEmail_Click(object sender, RoutedEventArgs e)
    {
        var account = _account;
        if (account is not { IsLoggedIn: true }) return;

        var input = AccountDialogs.ShowBindEmail(Window.GetWindow(this), account.Email);
        if (input == null) return;

        FriendStatusText.Text = "正在绑定邮箱…";
        var error = await account.BindEmailAsync(input.Value.Email, input.Value.Password);
        FriendStatusText.Text = error ?? $"邮箱已绑定：{input.Value.Email}（忘记编码时可用它找回）";
    }

    /// <summary>好友申请区：谁申请加我，接受 / 拒绝。</summary>
    private void RenderFriendRequests()
    {
        foreach (var request in _account?.Requests ?? new List<AccountFriend>())
        {
            var row = new Grid { Margin = new Thickness(4, 6, 4, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = BuildChatAvatar(request.Name, _chat);
            avatar.Width = 36;
            avatar.Height = 36;
            Grid.SetColumn(avatar, 0);
            row.Children.Add(avatar);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            var title = new TextBlock
            {
                Text = $"{request.Name} 申请加你为好友", FontSize = 13, FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            info.Children.Add(title);
            var sub = new TextBlock { Text = $"编码 {request.Code}", FontSize = 11 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            info.Children.Add(sub);
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var accept = new Button
            {
                Content = "接受", Height = 28, Padding = new Thickness(12, 2, 12, 2), Cursor = Cursors.Hand,
                Tag = request.Code
            };
            accept.SetResourceReference(FrameworkElement.StyleProperty, "BtnPrimary");
            accept.Click += async (_, _) =>
            {
                if (accept.Tag is not string code) return;
                accept.IsEnabled = false;   // 防连点发出两次 friend_accept
                try
                {
                    FriendStatusText.Text = await _account!.AcceptRequestAsync(code) ?? "已添加为好友";
                    RenderFriendAccount();
                    RenderFriends();
                }
                finally
                {
                    accept.IsEnabled = true;
                }
            };

            var decline = new Button
            {
                Content = "拒绝", Height = 28, Padding = new Thickness(12, 2, 12, 2),
                Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Tag = request.Code
            };
            decline.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
            decline.Click += async (_, _) =>
            {
                if (decline.Tag is not string code) return;
                decline.IsEnabled = false;   // 防连点
                try
                {
                    FriendStatusText.Text = await _account!.DeclineRequestAsync(code) ?? "已拒绝";
                    RenderFriends();
                }
                finally
                {
                    decline.IsEnabled = true;
                }
            };

            buttons.Children.Add(accept);
            buttons.Children.Add(decline);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);

            FriendList.Children.Add(row);
        }
    }

    private async void ReLogin_Click(object sender, RoutedEventArgs e)
    {
        var account = _account;
        if (account == null || string.IsNullOrEmpty(account.Code)) return;

        var password = AccountDialogs.ShowReLogin(Window.GetWindow(this), account.Code);
        if (password == null) return;

        FriendStatusText.Text = "正在登录…";
        var error = await account.LoginAsync(account.Code, password, CurrentPlayerName());
        FriendStatusText.Text = error ?? "登录成功，好友已同步";
        RenderFriendAccount();
        RenderFriends();
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var account = AccountService.Ensure();
        _account = account;
        var password = AccountDialogs.ShowRegister(Window.GetWindow(this), CurrentPlayerName());
        if (password == null) return;

        FriendStatusText.Text = "正在注册…";
        var error = await account.RegisterAsync(password, CurrentPlayerName());
        FriendStatusText.Text = error ?? $"注册成功，你的编码是 {account.Code}（请抄下来保存）";
        RenderFriendAccount();
        RenderFriends();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var account = AccountService.Ensure();
        _account = account;
        var input = AccountDialogs.ShowLogin(Window.GetWindow(this), CurrentPlayerName());
        if (input == null) return;

        FriendStatusText.Text = "正在登录…";
        var error = await account.LoginAsync(input.Code, input.Password, CurrentPlayerName());
        FriendStatusText.Text = error ?? "登录成功，好友已同步";
        RenderFriendAccount();
        RenderFriends();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _account?.Logout();
        RenderFriendAccount();
        RenderFriends();
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_account is not { IsLoggedIn: true })
        {
            FriendStatusText.Text = "当前没有登录";
            return;
        }

        var password = AccountDialogs.ShowDeleteAccount(Window.GetWindow(this), _account!.Code);
        if (password == null) return;

        FriendStatusText.Text = "正在注销…";
        var error = await _account.DeleteAsync(password);
        FriendStatusText.Text = error ?? "账号已注销，编码已释放";
        RenderFriendAccount();
        RenderFriends();
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_account is not { IsLoggedIn: true }) return;
        try
        {
            System.Windows.Clipboard.SetText(_account.Code);
            FriendStatusText.Text = "编码已复制";
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_account is not { IsLoggedIn: true })
        {
            FriendStatusText.Text = "请先注册或登录账号";
            return;
        }

        var code = (FriendNameBox.Text ?? "").Trim();
        if (code.Length == 0)
        {
            FriendStatusText.Text = "请输入对方的 9 位编码";
            return;
        }

        FriendStatusText.Text = "正在发送申请…";
        var error = await _account.RequestFriendAsync(code);
        FriendStatusText.Text = error ?? "已发送好友申请，等对方同意后就会出现在列表里";
        if (error == null) FriendNameBox.Text = "";
        RenderFriends();
    }

    // ===== 私聊（点好友进入）=====

    private string _dmCode = "";
    private string _dmName = "";
    private readonly Dictionary<string, List<ChatMessage>> _dmLogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取某位好友的私聊记录（没有就建一个）。</summary>
    private List<ChatMessage> LogOf(string key)
    {
        key = string.IsNullOrWhiteSpace(key) ? "unknown" : key;
        if (!_dmLogs.TryGetValue(key, out var list))
        {
            list = new List<ChatMessage>();
            _dmLogs[key] = list;
        }
        return list;
    }

    private static int _unreadTotal;

    /// <summary>未读私聊数量变化（主窗口用它显示红点）。</summary>
    public static event Action<int>? UnreadChanged;

    /// <summary>按好友编码记录的未读私聊数（点开谁的聊天只清谁的红点）</summary>
    private readonly System.Collections.Generic.Dictionary<string, int> _unreadByPeer
        = new(StringComparer.OrdinalIgnoreCase);
    private int _unreadFriendRequests;
    private int _lastPendingRequests = -1;

    /// <summary>重算未读总数 = 新好友申请 + 未读私聊，并广播红点。</summary>
    private void RecomputeUnread()
    {
        _unreadTotal = _unreadFriendRequests + _unreadByPeer.Values.Sum();
        UnreadChanged?.Invoke(_unreadTotal);
    }

    private string DmChannel(string otherCode)
    {
        var pair = new[] { _account?.Code ?? "", otherCode }
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        return $"dm:{pair[0]}-{pair[1]}";
    }

    private bool _inboxWired;
    private bool _changedWired;

    /// <summary>头像画刷缓存（键包含头像内容，头像变了自动失效）</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Brush> AvatarBrushCache
        = new(StringComparer.OrdinalIgnoreCase);

    private void WireInbox()
    {
        var account = _account;
        if (account == null || _inboxWired) return;
        _inboxWired = true;

        if (!_changedWired)
        {
            _changedWired = true;
            // 好友/申请一有变化（含服务端实时推送）就刷新界面并提示。
            // 注意：Changed 也会被 45 秒在线心跳触发，所以只能按「新增的待处理申请数」累加，
            // 否则同一条申请会被反复计入红点、提示音每 45 秒响一次。
            account.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                var pending = account.Requests.Count;
                var added = _lastPendingRequests < 0 ? pending : Math.Max(0, pending - _lastPendingRequests);
                _lastPendingRequests = pending;

                RenderFriendAccount();
                RenderFriends();

                if (added > 0 && FriendsView.Visibility != Visibility.Visible)
                {
                    _unreadFriendRequests += added;
                    RecomputeUnread();
                    PlayNotifySound();
                }
            });
        }
        account.DirectMessage += message => Dispatcher.BeginInvoke(() => OnPrivateMessage(message));
        account.DirectError += text => Dispatcher.BeginInvoke(() => PrivateChatStatus.Text = text);
    }

    private void OpenPrivateChat(string code, string name)
    {
        _dmCode = code;
        _dmName = name;
        PrivateChatTitle.Text = string.IsNullOrWhiteSpace(name) ? code : $"{name}（{code}）";

        // 本地记录（48 小时内）先载入，避免重开启动器后聊天记录空白
        if (LogOf(code).Count == 0)
        {
            var stored = DirectMessageStore.Load(code);
            if (stored.Count > 0) _dmLogs[code] = stored;
        }

        ShowView(PrivateChatView);
        PrivateChatStatus.Text = "消息实时到达；对方不在线时，中继会保留最近 50 条";
        RenderPrivateChat();

        // 只清这位好友的未读，别把其他人发来的未读也一起抹掉
        if (_unreadByPeer.Remove(code)) RecomputeUnread();
    }

    private void RenderPrivateChat()
    {
        PrivateChatMessages.Children.Clear();
        var log = LogOf(_dmCode);
        for (var i = 0; i < log.Count; i++)
            PrivateChatMessages.Children.Add(BuildChatRow(log[i], _account?.Inbox, ShouldShowAvatar(log, i)));
        PrivateChatScroll.ScrollToEnd();
    }

    private void ClearUnread()
    {
        _unreadFriendRequests = 0;
        _unreadByPeer.Clear();
        RecomputeUnread();
    }

    /// <summary>收到私聊：正在和这位好友聊天就直接显示，否则提示音 + 红点。</summary>
    private void OnPrivateMessage(ChatMessage message)
    {
        // 优先按编码匹配（改名/重名都不会错位），退回按昵称
        var fromCode = message.SenderCode ?? "";
        var key = fromCode.Length > 0 ? fromCode : message.From;
        var log = LogOf(key);
        if (log.Count == 0)
        {
            // 先载入本地记录，下面的回放去重才有依据
            var stored = DirectMessageStore.Load(key);
            if (stored.Count > 0) _dmLogs[key] = log = stored;
        }

        // 中继重连时会回放频道最近 50 条：时间戳/发件人/内容完全相同的旧消息不再入库、不再响铃
        if (log.Any(existing => existing.Timestamp == message.Timestamp
                                && string.Equals(existing.From, message.From, StringComparison.OrdinalIgnoreCase)
                                && existing.Text == message.Text))
        {
            return;
        }

        DirectMessageStore.Append(key, message);   // 本地保留 48 小时
        log.Add(message);
        while (log.Count > 300) log.RemoveAt(0);

        var isCurrentChat = PrivateChatView.Visibility == Visibility.Visible
                            && (fromCode.Length > 0
                                ? string.Equals(_dmCode, fromCode, StringComparison.OrdinalIgnoreCase)
                                : string.Equals(_dmName, message.From, StringComparison.OrdinalIgnoreCase));
        if (isCurrentChat)
        {
            if (log.Count < 2 || message.Timestamp - log[^2].Timestamp > 5 * 60 * 1000)
                PrivateChatMessages.Children.Add(BuildTimeDivider(message.Timestamp));
            PrivateChatMessages.Children.Add(BuildChatRow(message, _account?.Inbox,
                log.Count < 2 || ShouldShowAvatar(log, log.Count - 1)));
            while (PrivateChatMessages.Children.Count > 300) PrivateChatMessages.Children.RemoveAt(0);
            PrivateChatScroll.ScrollToEnd();
            return;
        }

        _unreadByPeer[key] = _unreadByPeer.GetValueOrDefault(key) + 1;
        RecomputeUnread();
        PlayNotifySound();
    }

    /// <summary>内置提示音：现场生成一小段 WAV 播放，不依赖 Windows 声音方案。</summary>
    private static void PlayNotifySound()
    {
        try
        {
            const int rate = 44100;
            const int ms = 320;
            var samples = rate * ms / 1000;
            var data = new byte[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                // 柔和的双音：660Hz → 880Hz 渐变，音量低、淡出长
                var progress = (double)i / samples;
                var freq = 660 + 220 * progress;
                var fade = Math.Sin(Math.PI * progress);
                var value = (short)(Math.Sin(2 * Math.PI * freq * i / rate) * 3200 * fade * fade);
                data[i * 2] = (byte)(value & 0xFF);
                data[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + data.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(rate);
                writer.Write(rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(data.Length);
                writer.Write(data);
            }

            stream.Position = 0;
            using var player = new System.Media.SoundPlayer(stream);
            player.Play();
        }
        catch
        {
            // 没声音也不影响收消息
        }
    }

    private async void PrivateChatSend_Click(object sender, RoutedEventArgs e) => await SendPrivateAsync();

    private async void PrivateChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        await SendPrivateAsync();
    }

    private async Task SendPrivateAsync()
    {
        if (_sendingPrivate) return;
        var text = PrivateChatInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        var account = _account;
        if (account is null)
        {
            PrivateChatStatus.Text = "请先注册或登录账号";
            return;
        }

        _sendingPrivate = true;
        try
        {
            PrivateChatInput.Text = "";
            if (!await account.SendDirectAsync(_dmCode, text, "text", _quoteText))
            {
                PrivateChatInput.Text = text;   // 发送失败把内容还给用户，别丢
                PrivateChatStatus.Text = "发送失败，请检查网络";
                return;
            }
            ClearQuote();

            // 本地先显示自己发的那条
            var mine = new ChatMessage($"inbox:{_dmCode}", account.Inbox?.Name ?? account.Name, text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, "text", account.Code);
            DirectMessageStore.Append(_dmCode, mine);
            LogOf(_dmCode).Add(mine);
            RenderPrivateChat();
        }
        finally
        {
            _sendingPrivate = false;
        }
    }

    private async void PrivateChatImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送的图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
        };
        if (dialog.ShowDialog() != true) return;

        if (new System.IO.FileInfo(dialog.FileName).Length > 1024 * 1024)
        {
            PrivateChatStatus.Text = "图片太大（上限 1MB）";
            return;
        }

        var account = _account;
        if (account is null)
        {
            PrivateChatStatus.Text = "请先注册或登录账号";
            return;
        }

        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(dialog.FileName);
            var base64 = Convert.ToBase64String(bytes);
            var ok = await account.SendDirectAsync(_dmCode, base64, "image");
            PrivateChatStatus.Text = ok ? "图片已发送" : "图片发送失败";
            if (ok)
            {
                var sent = new ChatMessage($"inbox:{_dmCode}", account.Inbox?.Name ?? account.Name, base64,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, "image", account.Code);
                DirectMessageStore.Append(_dmCode, sent);
                LogOf(_dmCode).Add(sent);
                RenderPrivateChat();
            }
        }
        catch (Exception ex)
        {
            PrivateChatStatus.Text = "图片发送失败：" + ex.Message;
        }
    }

    private void PrivateChatBack_Click(object sender, RoutedEventArgs e)
    {
        ShowView(FriendsView);
        RenderFriendAccount();
        RenderFriends();
    }

    private DispatcherTimer? _friendRefreshTimer;
    private readonly HashSet<string> _knownFriendCodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>停留在好友页时每 15 秒自动同步一次，别人加你能自动出现。</summary>
    private void StartFriendAutoRefresh()
    {
        _friendRefreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _friendRefreshTimer.Tick -= FriendRefreshTick;
        _friendRefreshTimer.Tick += FriendRefreshTick;
        _friendRefreshTimer.Start();
    }

    private async void FriendRefreshTick(object? sender, EventArgs e)
    {
        if (FriendsView.Visibility != Visibility.Visible)
        {
            _friendRefreshTimer?.Stop();
            return;
        }

        if (_account is not { IsLoggedIn: true }) return;
        await _account.RefreshFriendsAsync();
        RenderFriendAccount();
        RenderFriends();
    }

    private async void RefreshFriends_Click(object sender, RoutedEventArgs e)
    {
        FriendStatusText.Text = "正在刷新…";
        await EnsureAccountAsync();
        FriendStatusText.Text = $"已刷新，共 {_account?.Friends.Count ?? 0} 位好友";
    }

    private void AddFriendHintText(string text)
    {
        var hint = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(4, 8, 4, 8)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        FriendList.Children.Add(hint);
    }

    private void RenderFriends()
    {
        FriendList.Children.Clear();

        // 已登录：先显示待处理的好友申请，再显示好友
        if (_account is { IsLoggedIn: true })
        {
            RenderFriendRequests();

            if (_account.Friends.Count == 0)
            {
                AddFriendHintText("还没有云端好友。\n\n让对方把「我的编码」发给你，填到上面点「添加好友」即可；编码永久有效、对方改名也不影响。");
                return;
            }

            // 自动同步后新出现的好友，给一行提示
            var currentCodes = _account.Friends.Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = currentCodes.Where(code => !_knownFriendCodes.Contains(code)).ToList();
            if (_knownFriendCodes.Count > 0 && added.Count > 0)
            {
                var names = string.Join("、", _account.Friends
                    .Where(f => added.Contains(f.Code))
                    .Select(f => string.IsNullOrWhiteSpace(f.Name) ? f.Code : f.Name));
                FriendStatusText.Text = "新增好友：" + names;
            }
            _knownFriendCodes.Clear();
            foreach (var code in currentCodes) _knownFriendCodes.Add(code);

            foreach (var friend in _account.Friends.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var online = friend.Online;   // 在线状态由中继按心跳判定

                var row = new Grid
                {
                    Margin = new Thickness(4, 6, 4, 6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "点击开始私聊"
                };
                var friendCode = friend.Code;
                var friendName = friend.Name;
                row.MouseLeftButtonUp += (_, _) => OpenPrivateChat(friendCode, friendName);
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var avatar = BuildChatAvatar(friend.Name);
                avatar.Width = 36;
                avatar.Height = 36;
                Grid.SetColumn(avatar, 0);
                row.Children.Add(avatar);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
                var nameText = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(friend.Name) ? "(未知玩家)" : friend.Name,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold
                };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                info.Children.Add(nameText);

                var state = new TextBlock
                {
                    Text = $"编码 {friend.Code} · {(online ? "在线" : "离线")}",
                    FontSize = 11
                };
                state.SetResourceReference(TextBlock.ForegroundProperty, online ? "SuccessBrush" : "TextMutedBrush");
                info.Children.Add(state);
                Grid.SetColumn(info, 1);
                row.Children.Add(info);

                var removeBtn = new Button
                {
                    Content = "删除",
                    Height = 28,
                    Padding = new Thickness(12, 2, 12, 2),
                    Tag = friend.Code,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
                };
                removeBtn.SetResourceReference(StyleProperty, "BtnBase");
                removeBtn.Click += async (_, _) =>
                {
                    if (removeBtn.Tag is string target && _account != null)
                    {
                        FriendStatusText.Text = await _account.RemoveFriendAsync(target) ?? "已删除好友";
                        RenderFriends();
                    }
                };
                Grid.SetColumn(removeBtn, 2);
                row.Children.Add(removeBtn);

                FriendList.Children.Add(row);
            }
            return;
        }

        var friends = FriendService.Load()
            .OrderByDescending(friend => _chat?.IsOnline(friend.Name) == true)
            .ThenBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (friends.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "还没有好友。\n\n在世界聊天里看到对方的名字后，把名字填到上面点「添加好友」就行；\n以后他只要在聊天里说过话，这里就会显示在线。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(4, 8, 4, 8)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            FriendList.Children.Add(empty);
            return;
        }

        foreach (var friend in friends)
        {
            var online = _chat?.IsOnline(friend.Name) == true;

            var row = new Grid { Margin = new Thickness(4, 6, 4, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = BuildChatAvatar(friend.Name);
            avatar.Width = 36;
            avatar.Height = 36;
            Grid.SetColumn(avatar, 0);
            row.Children.Add(avatar);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            var nameText = new TextBlock { Text = friend.Name, FontSize = 14, FontWeight = FontWeights.SemiBold };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            info.Children.Add(nameText);

                var state = new TextBlock
                {
                    Text = online
                        ? "在线（聊天活跃）"
                        : "未登录账号，无法显示在线状态；登录后按编码添加的好友才能实时看在线",
                    FontSize = 11
                };
                state.SetResourceReference(TextBlock.ForegroundProperty, online ? "SuccessBrush" : "TextMutedBrush");
            info.Children.Add(state);
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var removeBtn = new Button
            {
                Content = "删除",
                Height = 28,
                Padding = new Thickness(12, 2, 12, 2),
                Tag = friend.Name,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            removeBtn.SetResourceReference(StyleProperty, "BtnBase");
            removeBtn.Click += (_, _) =>
            {
                if (removeBtn.Tag is string target) FriendService.Remove(target);
                RenderFriends();
            };
            Grid.SetColumn(removeBtn, 2);
            row.Children.Add(removeBtn);

            FriendList.Children.Add(row);
        }
    }

    private DateTime _imagePreviewOpenedAt;

    /// <summary>双击聊天里的图片 → 全屏预览；点击预览层任意位置关闭。</summary>
    private void ShowImagePreview(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        _imagePreviewOpenedAt = DateTime.UtcNow;
        ChatImagePreviewImage.Source = bitmap;
        ChatImagePreview.Visibility = Visibility.Visible;
    }

    private void ChatImagePreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 双击的第二次抬起会落在刚弹出的预览层上，必须忽略，否则会被立刻关掉
        if ((DateTime.UtcNow - _imagePreviewOpenedAt).TotalMilliseconds < 350) return;
        ChatImagePreview.Visibility = Visibility.Collapsed;
    }

    /// <summary>发图片（≤1MB）。</summary>
    private async void ChatImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送的图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
        };
        if (dialog.ShowDialog() != true) return;

        var info = new System.IO.FileInfo(dialog.FileName);
        if (info.Length > 1024 * 1024)
        {
            ChatStatus.Text = "图片太大（上限 1MB）";
            return;
        }

        if (_chat is not { IsConnected: true })
        {
            await EnsureChatAsync();
            if (_chat is not { IsConnected: true }) return;
        }

        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(dialog.FileName);
            ChatStatus.Text = await _chat.SendImageAsync(bytes) ? "图片已发送" : "图片发送失败";
        }
        catch (Exception ex)
        {
            ChatStatus.Text = "图片发送失败：" + ex.Message;
        }
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        if (_sendingChat) return;
        var text = ChatInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        _sendingChat = true;
        try
        {
            if (_chat is not { IsConnected: true })
            {
                await EnsureChatAsync();
                if (_chat is not { IsConnected: true })
                {
                    ChatStatus.Text = "连接失败，请稍后再试";
                    return;
                }
            }

            ChatInput.Text = "";
            // 世界频道也带上账号编码（点头像才能显示资料/加好友）
            if (_account is { IsLoggedIn: true }) _chat.SenderCode = _account.Code;
            if (!await _chat.SendAsync(text, "text", null, _quoteText))
            {
                ChatStatus.Text = "发送失败，请检查网络";
                return;
            }
            ClearQuote();
        }
        finally
        {
            _sendingChat = false;
        }
    }

    private async Task LoadRoomsAsync(bool silent = false)
    {
        if (_busy || DetailCard.Visibility == Visibility.Visible) return;
        _busy = true;
        if (!silent) LobbyStatus.Text = "正在刷新...";
        try
        {
            _rooms = await _lobby.GetRoomsAsync();
            RenderRooms();
        }
        catch
        {
            // 自动刷新失败时只保留上一次列表，避免每 5 秒闪一次错误
            if (!silent) LobbyStatus.Text = "中继服务器不可达";
        }
        finally
        {
            _busy = false;
        }
    }

    // 按房间号搜索结果，重绘列表
    private void RenderRooms()
    {
        var keyword = SearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(keyword)
            ? _rooms
            : _rooms.Where(room => room.Room.Contains(keyword, StringComparison.Ordinal)).ToList();

        RoomList.Children.Clear();
        foreach (var room in filtered)
            RoomList.Children.Add(BuildRoomCard(room));

        EmptyHint.Text = _rooms.Count == 0
            ? "联机大厅暂无联机房间"
            : "没有找到该房间号";
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbyStatus.Text = _rooms.Count == 0 ? "" : $"{filtered.Count}/{_rooms.Count} 个房间";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || LobbyRoot.Visibility != Visibility.Visible) return;
        RenderRooms();
    }

    private Border BuildRoomCard(LobbyRoom room)
    {
        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.BorderThickness = new Thickness(1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var name = new TextBlock { Text = room.TitleText, FontSize = 14, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        left.Children.Add(name);

        var info = new TextBlock { FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        info.Inlines.Add(new System.Windows.Documents.Run(
            $"{room.KindText} · {room.KindDetail} · MC {room.Mc} · {room.PlayerText}"
            + (room.Locked ? " · 🔒需密码" : "")));
        left.Children.Add(info);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var code = new TextBlock
        {
            Text = room.Room,
            FontSize = 15,
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        code.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        right.Children.Add(code);

        var addr = new TextBlock
        {
            Text = room.AddrText,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        addr.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        right.Children.Add(addr);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        card.Child = grid;
        card.MouseLeftButtonUp += (_, _) => ShowDetail(room);
        return card;
    }

    private void ShowDetail(LobbyRoom room)
    {
        _selected = room;
        RoomList.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        LobbyStatus.Text = "";

        DetailCard.Visibility = Visibility.Visible;
        DetailName.Text = room.TitleText;

        // 第一行：房间版本 + 房主玩家 ID
        DetailMeta.Text = $"版本 {room.Mc}　玩家ID {room.OwnerText}\n"
                          + $"{room.KindText} · {room.KindDetail} · {room.PlayerText}";

        // 第二行：连接地址
        if (room.ConnectAddress is { Length: > 0 })
        {
            DetailAddrText.Text = $"连接地址 {room.ConnectAddress}";
            DetailAddrText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            CopyAddrBtn.Content = "复制 IP";
            CopyAddrBtn.Visibility = Visibility.Visible;
        }
        else
        {
            DetailAddrText.Text = "密码房：点「加入房间」由启动器自动进入";
            DetailAddrText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            CopyAddrBtn.Visibility = Visibility.Collapsed;
        }

        // 第三行：Mod 清单
        if (room.Mods.Count > 0)
        {
            var preview = string.Join("\n", room.Mods.Take(20).Select(mod => $"· {mod.Name}  ({mod.SizeText})"));
            if (room.Mods.Count > 20) preview += $"\n… 等共 {room.Mods.Count} 个";
            DetailMods.Text = $"Mod 清单（{room.Mods.Count}）：\n" + preview;
        }
        else
        {
            DetailMods.Text = room.Kind == "modded"
                ? $"加载器 {room.KindDetail}（房主未上报清单）"
                : "原版房间，无需同步 Mod";
        }

        PasswordLabel.Visibility = room.Locked ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Visibility = room.Locked ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Text = "";
        JoinHint.Text = "";
        JoinBtn.IsEnabled = true;
    }

    private void CopyAddr_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.ConnectAddress is not { Length: > 0 } address) return;
        try
        {
            Clipboard.SetText(address);
            CopyAddrBtn.Content = "已复制";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyAddrBtn.Content = "复制 IP";
            };
            timer.Start();
        }
        catch
        {
            JoinHint.Text = "复制失败，请手动选择地址";
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        DetailCard.Visibility = Visibility.Collapsed;
        RoomList.Visibility = Visibility.Visible;
        _ = LoadRoomsAsync();
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _joining) return;
        var room = _selected;
        if (room.Locked && string.IsNullOrWhiteSpace(PasswordBox.Text))
        {
            JoinHint.Text = "该房间需要联机密码";
            return;
        }

        _joining = true;
        JoinBtn.IsEnabled = false;
        JoinHint.Text = "正在加入...";
        try
        {
            var result = await _lobby.JoinAsync(room.Room, PasswordBox.Text.Trim());
            if (!result.Ok)
            {
                JoinHint.Text = result.Message;
                return;
            }

            if (!_lobby.StartProxy(result))
            {
                JoinHint.Text = "本地代理启动失败";
                return;
            }

            // Mod 自动同步：与房主清单比对并下载缺失项
            var syncText = "";
            if (room.Mods.Count > 0)
            {
                var instance = (Window.GetWindow(this) as MainWindow)?.HomePage?.SelectedInstance;
                var gameDir = instance != null
                    ? InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance)
                    : App.Paths.MinecraftDir;
                var progress = new Progress<string>(text => JoinHint.Text = text);
                syncText = await _lobby.SyncModsAsync(room.Mods, gameDir, result.Mc, result.Loader, progress);
            }

            // 一键启动并加入：公开房直接连中继地址；密码房走本地代理（令牌握手）
            var target = room.ConnectAddress is { Length: > 0 }
                ? room.ConnectAddress
                : $"127.0.0.1:{_lobby.LocalPort}";

            if (Window.GetWindow(this) is MainWindow mainWindow && mainWindow.StartQuickPlay())
            {
                QuickPlayRequest.Address = target;
                JoinHint.Text = $"{(string.IsNullOrEmpty(syncText) ? "" : syncText + "；")}正在启动游戏并加入房主的世界…";
            }
            else
            {
                JoinHint.Text = "游戏正在启动中，请等启动完成后再点「加入房间」";
            }
        }
        catch (Exception ex)
        {
            JoinHint.Text = "加入失败：" + ex.Message;
        }
        finally
        {
            _joining = false;
            JoinBtn.IsEnabled = true;
        }
    }
}
