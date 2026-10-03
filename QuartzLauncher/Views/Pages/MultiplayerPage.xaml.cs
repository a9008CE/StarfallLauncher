using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
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
            _ = PrepareChatAsync();
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

        // 登录态就绪后立刻接上 IM，好友行才能内联显示「最后一条 + 未读」
        if (_account is { IsLoggedIn: true })
        {
            WireIm();
            _ = _account.RefreshImConversationsAsync();
        }
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
        ImChatView.Visibility = ReferenceEquals(view, ImChatView) ? Visibility.Visible : Visibility.Collapsed;

        if (ReferenceEquals(view, ChatView))
            ChatScroll.ScrollToEnd();
        if (ReferenceEquals(view, ImChatView))
            ImChatScroll.ScrollToEnd();
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

    /// <summary>
    /// 世界聊天要求账号登录。先恢复本地令牌，再建立聊天连接，避免
    /// 已保存账号因为尚未进入好友页而被当成未登录用户。
    /// </summary>
    private async Task PrepareChatAsync()
    {
        await EnsureAccountAsync();
        if (_account is not { IsLoggedIn: true })
        {
            ChatChannelText.Text = "";
            ChatLoginPrompt.Visibility = Visibility.Visible;
            ChatLoginPromptText.Text = _account?.NeedsReLogin == true
                ? "登录状态已失效，请重新登录后再进入世界频道。"
                : "登录后即可进入世界频道，与其他玩家聊天。";
            ChatStatus.Text = _account?.NeedsReLogin == true
                ? "登录状态已失效，请到好友页重新登录账号"
                : "世界聊天需要先登录账号，请到好友页完成登录";
            return;
        }

        ChatLoginPrompt.Visibility = Visibility.Collapsed;
        await EnsureChatAsync();
    }

    private void ChatLogin_Click(object sender, RoutedEventArgs e)
    {
        ShowSection("friends");
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
        ChatLoginPrompt.Visibility = !ok && client.LastError?.Contains("登录", StringComparison.OrdinalIgnoreCase) == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChatStatus.Text = ok
            ? "已连接 · 注意文明发言，违规内容会被过滤"
            : client.LastError ?? "连接失败，请稍后再试";
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
    private readonly List<ChatMessage> _chatHistory = new();

    /// <summary>发送成功后清掉引用与提示（状态栏写明「发送后自动清除」）。</summary>
    private void ClearQuote()
    {
        if (_quoteText.Length == 0) return;
        _quoteText = "";
        ChatStatus.Text = "";
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
                OpenFriendChat(code, name);
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
        var input = AccountDialogs.ShowRegister(Window.GetWindow(this), CurrentPlayerName());
        if (input == null) return;

        FriendStatusText.Text = "正在注册…";
        var error = await account.RegisterAsync(input.Email, input.Password, CurrentPlayerName());
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
        var error = await account.LoginAsync(input.Identifier, input.Password, CurrentPlayerName());
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

    // ===== IM 核心（好友单聊 / 离线消息 / 撤回 / BBCode 表情）=====

    private string _imPeer = "";
    private string _imPeerName = "";
    private readonly List<ImMessage> _imLog = new();
    private bool _sendingIm;
    private int _lastPendingImUnread = -1;

    /// <summary>按好友编码记录的 IM 未读数（与旧私聊的红点分开算，避免重复计数）。</summary>
    private readonly System.Collections.Generic.Dictionary<string, int> _imUnreadByPeer
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按好友编码缓存的会话摘要，好友行直接内联显示「最后一条 + 时间 + 未读」。</summary>
    private readonly System.Collections.Generic.Dictionary<string, ImConversation> _imConversations
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IM 相关事件只订阅一次。</summary>
    private bool _imWired;

    private void WireIm()
    {
        var account = _account;
        if (account == null || _imWired) return;
        _imWired = true;

        account.ImMessageReceived += message => Dispatcher.BeginInvoke(() => OnImMessage(message));
        account.ImMessageAcked += message => Dispatcher.BeginInvoke(() => OnImAcked(message));
        account.ImConversationsChanged += list => Dispatcher.BeginInvoke(() => OnImConversations(list));
        account.ImMessageRecalled += (conv, id) => Dispatcher.BeginInvoke(() => OnImRecalled(conv, id));
        account.ImHistoryLoaded += (conv, messages) => Dispatcher.BeginInvoke(() => OnImHistory(conv, messages));
        account.ImError += text => Dispatcher.BeginInvoke(() =>
        {
            // 报错时把没发出去的话还给用户，别让人以为自己发出去了
            if (_sendingIm && ImInput.Text.Length == 0) ImInput.Text = _imFailedText;
            ImChatStatus.Text = text;
        });
    }

    /// <summary>历史载入：只填当前打开的会话，不触发通知（否则一开窗口就响一串提示音）。</summary>
    private void OnImHistory(string conv, IReadOnlyList<ImMessage> messages)
    {
        if (ImChatView.Visibility != Visibility.Visible) return;
        if (!string.Equals(conv, ImClient.ConvId(_account?.Code ?? "", _imPeer), StringComparison.Ordinal)) return;

        _imLog.Clear();
        _imLog.AddRange(messages.Where(m => !m.Recalled || m.From == _account?.Code));
        while (_imLog.Count > 300) _imLog.RemoveAt(0);
        RenderImChat();
        ImChatStatus.Text = messages.Count > 0
            ? $"已载入最近 {messages.Count} 条（记录保存在服务器，重装启动器也还在）"
            : "还没有聊天记录";
    }

    /// <summary>
    /// 会话摘要到达：缓存起来并同步未读。
    /// 显示由好友行内联完成，这里只管数据，不再单独画一个会话列表页。
    /// </summary>
    private void OnImConversations(IReadOnlyList<ImConversation> sessions)
    {
        _imConversations.Clear();
        var pending = 0;
        foreach (var session in sessions)
        {
            if (string.IsNullOrEmpty(session.Peer)) continue;
            _imConversations[session.Peer] = session;
            pending += session.Unread;
        }

        if (pending != _lastPendingImUnread)
        {
            _lastPendingImUnread = pending;
            SyncImUnread(sessions);
        }

        // 好友行内联显示最后一条，摘要变了就得重画
        if (FriendsView.Visibility == Visibility.Visible) RenderFriends();
    }

    /// <summary>服务端给的未读数是权威值，同步到红点。</summary>
    private void SyncImUnread(IReadOnlyList<ImConversation> sessions)
    {
        _imUnreadByPeer.Clear();
        foreach (var session in sessions)
        {
            if (session.Unread > 0) _imUnreadByPeer[session.Peer] = session.Unread;
        }
        RecomputeUnread();
    }

    /// <summary>取某个好友的会话摘要；没聊过返回 null（好友行就退回显示编码和在线状态）。</summary>
    private ImConversation? ConversationOf(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        return _imConversations.TryGetValue(code, out var session) ? session : null;
    }

    /// <summary>好友行里那条「最后一条消息」：没消息时给一句说明，有消息时解析表情并省略。</summary>
    private static string ImPreviewText(ImConversation session)
    {
        if (string.IsNullOrEmpty(session.LastMessage))
            return session.Blocked ? "已解除好友关系" : "点此开始聊天";
        // 图片消息的正文是 data URL，绝不能当文字显示出来
        if (session.LastMessage.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "[图片]";
        return ImEmoji.Parse(session.LastMessage);
    }

    /// <summary>按编码在好友列表里查名字（查不到就显示编码本身）。</summary>
    private string FriendNameOf(string code)
    {
        var friend = _account?.Friends.FirstOrDefault(f =>
            string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        return friend?.Name ?? "";
    }

    /// <summary>
    /// 从好友列表或用户卡片点进来：统一走 IM 核心。
    /// 没登录时给明确提示（IM 必须是好友制，登录态拿不到就没法发）。
    /// </summary>
    private void OpenFriendChat(string code, string name)
    {
        var account = _account;
        if (account == null || !account.IsLoggedIn)
        {
            if (FriendsView.Visibility == Visibility.Visible)
                FriendStatusText.Text = "请先登录账号再聊天";
            return;
        }

        var isFriend = account.Friends.Any(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        if (!isFriend)
        {
            if (FriendsView.Visibility == Visibility.Visible)
                FriendStatusText.Text = "只能和好友聊天（IM 禁止陌生人私聊）";
            return;
        }

        WireIm();
        OpenImChat(code, name);
    }

    private void OpenImChat(string code, string name)    {
        var account = _account;
        if (account == null) return;

        _imPeer = code;
        // 名字可能没传（比如从用户卡片点进来），回好友列表里补一下
        _imPeerName = string.IsNullOrWhiteSpace(name) ? FriendNameOf(code) : name;
        if (string.IsNullOrWhiteSpace(_imPeerName)) _imPeerName = code;
        ImChatTitle.Text = _imPeerName;
        ImChatPeer.Text = code == _imPeerName ? "" : code;
        ImChatStatus.Text = "正在加载聊天记录…";

        ShowView(ImChatView);
        RenderImChat();

        _ = account.LoadImHistoryAsync(code, 50);

        // 打开就算已读：服务端落盘，重连不会重复补发
        var conv = ImClient.ConvId(account.Code, code);
        _ = account.MarkImReadAsync(conv);
        if (_imUnreadByPeer.Remove(code))
        {
            RecomputeUnread();
            // 好友行上的未读红点要跟着消失
            if (_imConversations.TryGetValue(code, out var opened))
                _imConversations[code] = opened with { Unread = 0 };
            if (FriendsView.Visibility == Visibility.Visible) RenderFriends();
        }
    }

    private void RenderImChat()
    {
        ImChatMessages.Children.Clear();
        for (var i = 0; i < _imLog.Count; i++)
        {
            var showHeader = i == 0 || _imLog[i].From != _imLog[i - 1].From
                             || _imLog[i].Timestamp - _imLog[i - 1].Timestamp > 5 * 60 * 1000;
            if (showHeader) ImChatMessages.Children.Add(BuildTimeDivider(_imLog[i].Timestamp));
            ImChatMessages.Children.Add(BuildImRow(_imLog[i]));
        }
        ImChatScroll.ScrollToEnd();
    }

    private UIElement BuildImRow(ImMessage message)
    {
        var myCode = _account?.Code ?? "";
        var isMine = string.Equals(message.From, myCode, StringComparison.Ordinal);

        var mineBubble = new SolidColorBrush(Color.FromRgb(0x9C, 0xDD, 0xFF));
        mineBubble.Freeze();
        var otherBubble = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xF8));
        otherBubble.Freeze();
        var otherBubbleBorder = new SolidColorBrush(Color.FromRgb(0xD8, 0xE0, 0xE8));
        otherBubbleBorder.Freeze();

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 9, 14, 9),
            MaxWidth = 520,
            Background = isMine ? mineBubble : (Brush)FindResource("DialogCardBrush")
        };
        if (!isMine)
        {
            bubble.Background = otherBubble;
            bubble.BorderBrush = otherBubbleBorder;
            bubble.BorderThickness = new Thickness(1);
        }

        // QQ 风格气泡尾巴：对方在左下角，我方在右下角。
        // 尾巴和气泡共用同一画刷，避免图片/长文本时出现断层。
        var bubbleWrap = new Grid { MaxWidth = 532 };
        bubbleWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bubbleWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tail = new Polygon
        {
            Points = isMine
                ? new PointCollection(new[] { new Point(0, 0), new Point(12, 9), new Point(0, 18) })
                : new PointCollection(new[] { new Point(12, 0), new Point(0, 9), new Point(12, 18) }),
            Width = 12,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
            Fill = isMine ? mineBubble : otherBubble
        };
        if (isMine)
        {
            Grid.SetColumn(bubble, 0);
            Grid.SetColumn(tail, 1);
        }
        else
        {
            Grid.SetColumn(tail, 0);
            Grid.SetColumn(bubble, 1);
        }
        Panel.SetZIndex(tail, 1);
        bubbleWrap.Children.Add(tail);
        bubbleWrap.Children.Add(bubble);

        var content = new StackPanel();

        if (message.Recalled)
        {
            var tip = new TextBlock { Text = isMine ? "你撤回了一条消息" : "对方撤回了一条消息", FontSize = 12, Opacity = 0.7 };
            tip.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            bubble.Child = tip;
            line.Children.Add(bubbleWrap);
            return line;
        }

        if (!isMine)
        {
            var header = new TextBlock
            {
                Text = $"{_imPeerName} · {message.TimeText}",
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 3)
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            content.Children.Add(header);
        }

        var body = message.IsImage ? BuildImImage(message.Text, isMine) : BuildImText(message.Text, isMine);
        if (body != null) content.Children.Add(body);

        if (isMine)
        {
            var time = new TextBlock
            {
                Text = message.TimeText,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Opacity = 0.7,
                Margin = new Thickness(0, 3, 0, 0)
            };
            time.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            content.Children.Add(time);
        }

        bubble.Child = content;

        // 右键：撤回（仅限自己 5 分钟内发的）/ 复制
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) => TryCopy(message.Text);
        menu.Items.Add(copy);
        if (message.CanRecall(myCode))
        {
            var recall = new MenuItem { Header = "撤回" };
            recall.Click += async (_, _) =>
            {
                var account = _account;
                if (account == null) return;
                ImChatStatus.Text = "正在撤回…";
                if (await account.RecallImAsync(message.Conv, message.Id))
                    ImChatStatus.Text = "已撤回";
                else
                    ImChatStatus.Text = "撤回失败：超过 5 分钟就不能撤回了";
            };
            menu.Items.Add(recall);
        }
        bubble.ContextMenu = menu;
        bubble.MouseRightButtonUp += (_, args) => args.Handled = true;

        line.Children.Add(bubbleWrap);
        return line;
    }

    private UIElement BuildImText(string text, bool isMine)
    {
        var body = new TextBlock
        {
            Text = ImEmoji.Parse(text),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24
        };
        if (isMine) body.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x3A));
        else body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return body;
    }

    /// <summary>渲染 IM 图片消息。服务端存的是 data URL，这里剥掉前缀后复用老私聊的解码逻辑。</summary>
    private UIElement? BuildImImage(string dataUrl, bool isMine)
    {
        var comma = dataUrl.IndexOf(',');
        var base64 = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
        var image = BuildChatImage(base64);
        if (image == null)
            return new TextBlock { Text = $"（图片无法显示：{base64.Length} 字符）", FontSize = 11 };
        if (isMine && image is FrameworkElement element) element.HorizontalAlignment = HorizontalAlignment.Right;
        return image;
    }

    private void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(ImEmoji.Parse(text));
            ImChatStatus.Text = "已复制到剪贴板";
        }
        catch
        {
            ImChatStatus.Text = "复制失败";
        }
    }

    /// <summary>收到 IM 消息：正在看的会话直接上屏，否则红点 + 提示音。</summary>
    private void OnImMessage(ImMessage message)
    {
        var account = _account;
        if (account == null) return;

        var peer = string.Equals(message.From, account.Code, StringComparison.Ordinal)
            ? message.To
            : message.From;
        if (string.IsNullOrEmpty(peer)) return;
        var conv = ImClient.ConvId(account.Code, peer);

        if (ImChatView.Visibility == Visibility.Visible
            && string.Equals(_imPeer, peer, StringComparison.OrdinalIgnoreCase))
        {
            if (_imLog.Any(existing => string.Equals(existing.Id, message.Id, StringComparison.Ordinal))) return;
            _imLog.Add(message);
            while (_imLog.Count > 300) _imLog.RemoveAt(0);
            RenderImChat();
            _ = account.MarkImReadAsync(conv);
            if (_imUnreadByPeer.Remove(peer)) RecomputeUnread();
            // 好友行内联显示最后一条，回到好友页要看到这条
            _ = account.RefreshImConversationsAsync();
            return;
        }

        // 不在当前会话：未读数交给服务端算，这里只要刷新列表 + 提醒
        _ = account.RefreshImConversationsAsync();
        PlayNotifySound();
    }

    /// <summary>
    /// 自己发出的消息被服务端确认：补上最终 ID 和时间戳后上屏。
    /// 服务端不会把消息回推给发送方，所以这一步是「自己也能看到自己发的」的唯一来源。
    /// </summary>
    private void OnImAcked(ImMessage message)
    {
        var peer = message.To;
        if (string.IsNullOrEmpty(peer)) return;

        if (ImChatView.Visibility == Visibility.Visible
            && string.Equals(_imPeer, peer, StringComparison.OrdinalIgnoreCase)
            && !_imLog.Any(existing => string.Equals(existing.Id, message.Id, StringComparison.Ordinal)))
        {
            _imLog.Add(message);
            while (_imLog.Count > 300) _imLog.RemoveAt(0);
            RenderImChat();
        }

        // 摘要（最后一条 + 时间）变了，会话列表要跟着动
        _ = _account?.RefreshImConversationsAsync();
    }

    private void OnImRecalled(string conv, string id)    {
        var index = _imLog.FindIndex(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (index < 0) return;
        _imLog[index] = _imLog[index] with { Recalled = true };
        if (ImChatView.Visibility == Visibility.Visible) RenderImChat();
        // 撤回后好友行的摘要也得跟着变（服务端会重新下发）
        _ = _account?.RefreshImConversationsAsync();
    }

    private string _imFailedText = "";

    private async void ImSend_Click(object sender, RoutedEventArgs e) => await SendImAsync();

    private async void ImInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Shift)
        {
            e.Handled = true;
            return;
        }
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        await SendImAsync();
    }

    private async Task SendImAsync()
    {
        var account = _account;
        if (account == null) return;

        var text = ImInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        if (string.IsNullOrEmpty(_imPeer))
        {
            ImChatStatus.Text = "还没选好友";
            return;
        }

        ImInput.Text = "";
        _imFailedText = text;
        _sendingIm = true;
        var ok = await account.SendImAsync(_imPeer, text);
        _sendingIm = false;
        if (ok) ImChatStatus.Text = "";
        else ImChatStatus.Text = "发送失败，请检查网络后重试";
    }

    private async void ImImage_Click(object sender, RoutedEventArgs e)
    {
        var account = _account;
        if (account == null || !account.IsLoggedIn)
        {
            ImChatStatus.Text = "请先注册或登录账号";
            return;
        }
        if (string.IsNullOrEmpty(_imPeer))
        {
            ImChatStatus.Text = "还没选好友";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送的图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp"
        };
        if (dialog.ShowDialog() != true) return;

        if (new System.IO.FileInfo(dialog.FileName).Length > 1024 * 1024)
        {
            ImChatStatus.Text = "图片太大（上限 1MB）";
            return;
        }

        try
        {
            ImChatStatus.Text = "正在发送图片…";
            var bytes = await System.IO.File.ReadAllBytesAsync(dialog.FileName);
            var mime = ImageMimeOf(dialog.FileName);
            var dataUrl = $"data:{mime};base64," + Convert.ToBase64String(bytes);
            if (await account.SendImAsync(_imPeer, dataUrl, "image"))
                ImChatStatus.Text = "图片已发送";
            else
                ImChatStatus.Text = "图片发送失败，请稍后再试";
        }
        catch (Exception ex)
        {
            ImChatStatus.Text = "图片发送失败：" + ex.Message;
        }
    }

    private static string ImageMimeOf(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };

    private void ImEmoji_Click(object sender, RoutedEventArgs e)
    {
        if (ImEmojiPopup.IsOpen)
        {
            ImEmojiPopup.IsOpen = false;
            return;
        }

        ImEmojiItems.Items.Clear();
        foreach (var tag in ImEmoji.AllTags.OrderBy(t => t, StringComparer.Ordinal))
        {
            var text = new TextBlock { Text = ImEmoji.Preview(tag), FontSize = 18, Margin = new Thickness(6, 3, 6, 3) };
            var button = new Button
            {
                Content = text,
                Style = TryFindResource("BtnBase") as Style,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(2),
                Tag = tag,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += (_, _) =>
            {
                InsertImText("[" + (string)button.Tag + "]");
                ImEmojiPopup.IsOpen = false;
            };
            ImEmojiItems.Items.Add(button);
        }
        ImEmojiPopup.IsOpen = true;
    }

    /// <summary>往输入框插入一段文字，并让光标停在它后面。</summary>
    private void InsertImText(string text)
    {
        ImInput.Text += text;
        ImInput.Focus();
        ImInput.CaretIndex = ImInput.Text.Length;
        ImInput.SelectionLength = 0;
    }

    private void ImChatBack_Click(object sender, RoutedEventArgs e)
    {
        _imPeer = "";
        ShowView(FriendsView);
        RenderFriends();
        _ = _account?.RefreshImConversationsAsync();
    }

    // ===== 未读红点 =====

    private static int _unreadTotal;

    /// <summary>未读总数变化（主窗口用它显示红点）。</summary>
    public static event Action<int>? UnreadChanged;

    private int _unreadFriendRequests;
    private int _lastPendingRequests = -1;

    /// <summary>重算未读总数 = 新好友申请 + 未读 IM，并广播红点。</summary>
    private void RecomputeUnread()
    {
        _unreadTotal = _unreadFriendRequests + _imUnreadByPeer.Values.Sum();
        UnreadChanged?.Invoke(_unreadTotal);
    }

    /// <summary>进入好友页时清掉好友申请红点（IM 未读保留，等用户点开会话）。</summary>
    private void ClearUnread()
    {
        _unreadFriendRequests = 0;
        RecomputeUnread();
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

        // 摘要和好友一起同步，好友行的最后一条消息才不会停在旧值
        WireIm();
        await _account.RefreshImConversationsAsync();
    }

    private async void RefreshFriends_Click(object sender, RoutedEventArgs e)
    {
        FriendStatusText.Text = "正在刷新…";
        await EnsureAccountAsync();
        FriendStatusText.Text = $"已刷新，共 {_account?.Friends.Count ?? 0} 位好友";
    }

    /// <summary>
    /// 好友行外面套一层带边框的卡片。
    /// Grid 自身没有背景，空白处点不到，所以整行可点必须由这个有背景的 Border 承担。
    /// 行内按钮自己会把手势吃掉，不会误触发进聊天。
    /// </summary>
    private Border BuildFriendCard(UIElement row, string friendCode, string friendName)
    {
        var card = new Border
        {
            Child = row,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(4, 3, 4, 3),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击任意位置开始私聊"
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.SetResourceReference(Border.CornerRadiusProperty, "CardCornerRadius");
        HoverAnimationBehavior.SetHoverScale(card, 1.006);

        card.MouseLeftButtonUp += (_, _) => OpenFriendChat(friendCode, friendName);
        return card;
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

                // 外边距和内边距由 BuildFriendCard 的卡片负责，这里不再重复留白
                var row = new Grid
                {
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var friendCode = friend.Code;
                var friendName = friend.Name;
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var avatar = BuildChatAvatar(friend.Name);
                avatar.Width = 36;
                avatar.Height = 36;
                Grid.SetColumn(avatar, 0);
                row.Children.Add(avatar);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };

                // 名字行：未读数直接挂在名字后面，不用再点进单独的会话列表
                var session = ConversationOf(friend.Code);
                var unread = session?.Unread ?? 0;
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                var nameText = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(friend.Name) ? "(未知玩家)" : friend.Name,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 320
                };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                nameRow.Children.Add(nameText);

                if (unread > 0)
                {
                    var badge = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(5, 0, 5, 1),
                        Margin = new Thickness(8, 1, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = (Brush)FindResource("DangerBrush"),
                        Child = new TextBlock
                        {
                            Text = unread > 99 ? "99+" : unread.ToString(),
                            FontSize = 10,
                            Foreground = Brushes.White
                        }
                    };
                    nameRow.Children.Add(badge);
                }
                info.Children.Add(nameRow);

                var preview = new TextBlock
                {
                    Text = session == null ? "点此开始聊天" : ImPreviewText(session),
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 420
                };
                preview.SetResourceReference(TextBlock.ForegroundProperty,
                    unread > 0 ? "TextBrush" : "TextMutedBrush");
                info.Children.Add(preview);

                var state = new TextBlock
                {
                    Text = $"编码 {friend.Code} · {(online ? "在线" : "离线")}",
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                state.SetResourceReference(TextBlock.ForegroundProperty, online ? "SuccessBrush" : "TextMutedBrush");
                info.Children.Add(state);

                // 好友正在开房：显示房间号（加密房标注）
                if (!string.IsNullOrWhiteSpace(friend.Room))
                {
                    var roomText = new TextBlock
                    {
                        Text = $"房间 {friend.Room}{(friend.RoomLocked ? "（加密）" : "")}",
                        FontSize = 11.5,
                        Margin = new Thickness(0, 3, 0, 0)
                    };
                    roomText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                    info.Children.Add(roomText);
                }
                Grid.SetColumn(info, 1);
                row.Children.Add(info);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (!string.IsNullOrWhiteSpace(friend.Room))
                {
                    var joinBtn = new Button
                    {
                        Content = "加入房间",
                        Height = 28,
                        Padding = new Thickness(12, 2, 12, 2),
                        Margin = new Thickness(0, 0, 8, 0),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    joinBtn.SetResourceReference(StyleProperty, "BtnPrimary");
                    joinBtn.Click += async (_, _) => await JoinFriendRoomAsync(friend, joinBtn);
                    buttons.Children.Add(joinBtn);
                }

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
                buttons.Children.Add(removeBtn);

                // 右列：最后一条的时间压在按钮上方
                var right = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (session != null && !string.IsNullOrEmpty(session.TimeText))
                {
                    var time = new TextBlock
                    {
                        Text = session.TimeText,
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    time.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                    right.Children.Add(time);
                }
                right.Children.Add(buttons);
                Grid.SetColumn(right, 2);
                row.Children.Add(right);

                FriendList.Children.Add(BuildFriendCard(row, friendCode, friendName));
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

            var row = new Grid { Cursor = System.Windows.Input.Cursors.Hand };
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

            // 未登录时点整行也会走 OpenFriendChat，由它提示先登录
            FriendList.Children.Add(BuildFriendCard(row, "", friend.Name));
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
            await PrepareChatAsync();
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
                await PrepareChatAsync();
                if (_chat is not { IsConnected: true })
                {
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
            await StartJoinRoomAsync(room, PasswordBox.Text.Trim(), text => JoinHint.Text = text);
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

    /// <summary>好友列表：一键加入好友正在开的房间（与联机大厅「加入房间」同一套流程）。</summary>
    private async Task JoinFriendRoomAsync(AccountFriend friend, Button joinBtn)
    {
        if (_joining) return;

        LobbyRoom? room;
        try
        {
            var rooms = await _lobby.GetRoomsAsync();
            room = rooms.FirstOrDefault(r => string.Equals(r.Room, friend.Room, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            FriendStatusText.Text = "中继服务器不可达";
            return;
        }
        if (room == null)
        {
            FriendStatusText.Text = "房间可能已关闭，稍后刷新再看";
            return;
        }

        var password = "";
        if (room.Locked)
        {
            var input = PromptRoomPassword(room.Room);
            if (input == null) return;
            password = input.Trim();
            if (password.Length == 0) return;
        }

        _joining = true;
        joinBtn.IsEnabled = false;
        FriendStatusText.Text = $"正在加入房间 {room.Room}…";
        try
        {
            await StartJoinRoomAsync(room, password, text => FriendStatusText.Text = text);
        }
        catch (Exception ex)
        {
            FriendStatusText.Text = "加入失败：" + ex.Message;
        }
        finally
        {
            _joining = false;
            joinBtn.IsEnabled = true;
        }
    }

    /// <summary>加入房间的共用流程：中继握手 → 本地代理 → Mod 同步 → 一键启动并加入。</summary>
    private async Task StartJoinRoomAsync(LobbyRoom room, string password, Action<string> setStatus)
    {
        var result = await _lobby.JoinAsync(room.Room, password);
        if (!result.Ok)
        {
            setStatus(result.Message);
            return;
        }

        if (!_lobby.StartProxy(result))
        {
            setStatus("本地代理启动失败");
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
            var progress = new Progress<string>(text => setStatus(text));
            syncText = await _lobby.SyncModsAsync(room.Mods, gameDir, result.Mc, result.Loader, progress);
        }

        // 一键启动并加入：公开房直接连中继地址；密码房走本地代理（令牌握手）
        var target = room.ConnectAddress is { Length: > 0 }
            ? room.ConnectAddress
            : $"127.0.0.1:{_lobby.LocalPort}";

        if (Window.GetWindow(this) is MainWindow mainWindow && mainWindow.StartQuickPlay())
        {
            QuickPlayRequest.Address = target;
            setStatus($"{(string.IsNullOrEmpty(syncText) ? "" : syncText + "；")}正在启动游戏并加入房主的世界…");
        }
        else
        {
            setStatus("游戏正在启动中，请等启动完成后再点「加入房间」");
        }
    }

    /// <summary>密码房：弹窗输入联机密码（取消返回 null）。</summary>
    private string? PromptRoomPassword(string roomCode)
    {
        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = "联机密码",
            Width = 320,
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
            Padding = new Thickness(20, 18, 20, 18)
        };
        card.SetResourceReference(Border.BackgroundProperty, "DialogCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var panel = new StackPanel();
        var title = new TextBlock
        {
            Text = $"房间 {roomCode} 需要联机密码",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(title);

        var box = new PasswordBox
        {
            Height = 34,
            Margin = new Thickness(0, 12, 0, 14),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(box);

        string? result = null;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "取消", Height = 30, Padding = new Thickness(14, 2, 14, 2) };
        cancel.SetResourceReference(StyleProperty, "BtnBase");
        cancel.Click += (_, _) => window.Close();

        var ok = new Button
        {
            Content = "加入",
            Height = 30,
            Padding = new Thickness(16, 2, 16, 2),
            Margin = new Thickness(8, 0, 0, 0)
        };
        ok.SetResourceReference(StyleProperty, "BtnPrimary");

        void Submit()
        {
            result = box.Password;
            window.Close();
        }

        ok.Click += (_, _) => Submit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            Submit();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        card.Child = panel;
        window.Content = card;
        box.Loaded += (_, _) => box.Focus();
        window.ShowDialog();
        return result;
    }
}
