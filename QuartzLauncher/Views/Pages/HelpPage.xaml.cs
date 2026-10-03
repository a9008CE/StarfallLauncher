using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class HelpPage : Page
{
    private readonly HelpAiService _ai = new();
    private readonly LauncherMcpTools _mcp = new();
    private readonly McDoctorService _mcDoctor = new();
    private static readonly Random Rng = new();
    private bool _toolRunning;

    public HelpPage()
    {
        InitializeComponent();
        Loaded += HelpPage_Loaded;
    }

    private void HelpPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HelpPage_Loaded;
        AddMessage("星落助手", "你好，我是星落助手，关于启动器的任何用法都可以问我。也可以直接说出想安装的 Minecraft 版本、加载器、Mod 和语言。", fromUser: false);
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private async void AnalyzeLog_Click(object sender, RoutedEventArgs e)
    {
        var log = LogAnalyzer.GetRecentLogs(App.Paths.MinecraftDir, 15).FirstOrDefault();
        if (log == null)
        {
            await ReplyAsync("最近没有找到可分析的日志。请先启动一次游戏。");
            return;
        }

        await AnalyzeSelectedLogAsync(log);
    }

    private async void SelectLog_Click(object sender, RoutedEventArgs e)
    {
        var logs = LogAnalyzer.GetRecentLogs(App.Paths.MinecraftDir, 15);
        if (logs.Count == 0)
        {
            await ReplyAsync("最近没有找到可分析的日志。请先启动一次游戏。");
            return;
        }

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "指定日志分析",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            MinHeight = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (Brush)FindResource("BgBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var combo = new ComboBox
        {
            ItemsSource = logs,
            DisplayMemberPath = nameof(RecentLogFile.DisplayName),
            SelectedIndex = 0,
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        combo.SetResourceReference(Control.BackgroundProperty, "InputBgBrush");
        combo.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");

        var confirm = new Button
        {
            Content = "分析选中日志",
            IsDefault = true,
            Height = 36,
            Padding = new Thickness(14, 4, 14, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        confirm.SetResourceReference(StyleProperty, "BtnPrimary");
        confirm.Click += (_, _) => dialog.DialogResult = true;

        var panel = new StackPanel { Margin = new Thickness(18, 18, 18, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "仅显示最近 15 次日志",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(combo);
        panel.Children.Add(confirm);
        dialog.Content = panel;

        if (dialog.ShowDialog() == true && combo.SelectedItem is RecentLogFile selected)
            await AnalyzeSelectedLogAsync(selected);
    }

    private async Task AnalyzeSelectedLogAsync(RecentLogFile log)
    {
        EmptyHint.Visibility = Visibility.Collapsed;
        try
        {
            var content = await Task.Run(() => LogAnalyzer.ReadText(log.Path));
            AddMessage("", log.DisplayName, fromUser: true);
            
            var thinking = AddMessage("MCDoctor AI", "正在分析日志...", fromUser: false);
            var analysis = await _mcDoctor.AnalyzeLogAsync(content);
            await TypewriterAsync(thinking, analysis);
        }
        catch (Exception ex)
        {
            await TypewriterAsync(AddMessage("星落助手", "", fromUser: false),
                $"诶呀，这个文件好像读不了：{ex.Message}。换个文件试试？");
        }
    }

    private void QuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Send();
        }
    }

    private void QuestionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SendBtn != null)
            SendBtn.IsEnabled = !_toolRunning && !string.IsNullOrWhiteSpace(QuestionBox.Text);
        if (QuestionPlaceholder != null)
            QuestionPlaceholder.Visibility = string.IsNullOrEmpty(QuestionBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void Send()
    {
        var question = QuestionBox.Text.Trim();
        if (string.IsNullOrEmpty(question)) return;

        QuestionBox.Clear();
        EmptyHint.Visibility = Visibility.Collapsed;
        AddMessage("", question, fromUser: true);
        _ = HandleQuestionAsync(question);
    }

    private async Task HandleQuestionAsync(string question)
    {
        try
        {
            if (!LauncherMcpTools.TryParseInstallPlan(question, out var plan))
            {
                if (LauncherMcpTools.IsInstallRequest(question))
                {
                    await ShowInstallEditorAsync(question);
                    return;
                }
                await ReplyAsync(_ai.Ask(question));
                return;
            }
            await ShowInstallEditorAsync(question, plan);
        }
        catch (Exception ex)
        {
            WriteInstallLog("处理 AI 请求失败", ex);
            AddMessage("星落助手 · MCP", $"请求处理失败：{GetErrorMessage(ex)}", fromUser: false);
            AnimatedMessageBox.Show(GetErrorMessage(ex), "AI 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExecuteInstallPlanAsync(MinecraftInstallPlan plan)
    {
        AddMessage("星落助手 · MCP", LauncherMcpTools.DescribePlan(plan), fromUser: false);
        _toolRunning = true;
        SendBtn.IsEnabled = false;
        SendBtn.Content = "执行中";
        QuestionBox.IsEnabled = false;
        var status = AddMessage("星落助手 · MCP", "正在启动工具调用...", fromUser: false);
        try
        {
            var progress = new Progress<string>(text =>
            {
                status.Text = text;
                ChatScroll.ScrollToEnd();
            });
            var result = await _mcp.ExecuteInstallAsync(plan, progress);
            var skippedText = result.SkippedMods.Count == 0
                ? ""
                : $"\n未找到已跳过：{string.Join("、", result.SkippedMods)}";
            status.Text = $"工具调用完成。\n\n实例：{result.InstanceName}\nMinecraft：{result.MinecraftVersion}\n加载器：{result.Loader}\nMod：{(result.InstalledMods.Count == 0 ? "无" : string.Join("、", result.InstalledMods))}{skippedText}\n\n现在可以回到首页选择该实例启动。";
        }
        catch (Exception ex)
        {
            var error = GetErrorMessage(ex);
            status.Text = $"工具调用失败：{error}\n\n未完成的实例内容已回滚；下载中心仍保留任务记录，可在下载管理中重试。";
            ChatScroll.ScrollToEnd();
            WriteInstallLog($"安装 Minecraft {plan.MinecraftVersion} 失败", ex);
            AnimatedMessageBox.Show(
                $"AI 下载 Minecraft {plan.MinecraftVersion} 失败：\n\n{error}\n\n详细任务记录保留在下载管理中。",
                "AI 安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _toolRunning = false;
            SendBtn.Content = "发送";
            QuestionBox.IsEnabled = true;
            SendBtn.IsEnabled = !string.IsNullOrWhiteSpace(QuestionBox.Text);
            QuestionBox.Focus();
        }
    }

    private async Task ShowInstallEditorAsync(string sourceText, MinecraftInstallPlan? parsedPlan = null)
    {
        _toolRunning = true;
        SendBtn.IsEnabled = false;
        QuestionBox.IsEnabled = false;
        var editor = AddInstallEditor(sourceText, parsedPlan);
        try
        {
            editor.EditorStatus = "正在读取可用 Minecraft 版本...";
            var versions = await _mcp.GetMinecraftVersionsAsync();
            foreach (var version in versions)
                editor.Versions.Add(version);

            editor.SelectedVersion = parsedPlan?.MinecraftVersion;
            if (string.IsNullOrWhiteSpace(editor.SelectedVersion) || !editor.Versions.Contains(editor.SelectedVersion))
                editor.SelectedVersion = editor.Versions.FirstOrDefault();

            await LoadEditorLoadersAsync(editor, parsedPlan?.Loader);
        }
        catch (Exception ex)
        {
            editor.EditorStatus = $"读取版本列表失败：{GetErrorMessage(ex)}";
        }
        finally
        {
            editor.IsLoaderSelectionEnabled = true;
            editor.IsEditorLoading = false;
            _toolRunning = false;
            QuestionBox.IsEnabled = true;
            SendBtn.IsEnabled = !string.IsNullOrWhiteSpace(QuestionBox.Text);
            QuestionBox.Focus();
        }
    }

    private async void InstallVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not ChatMessage editor
            || !editor.IsInstallEditor || editor.IsEditorLoading || string.IsNullOrWhiteSpace(editor.SelectedVersion))
            return;
        await LoadEditorLoadersAsync(editor);
    }

    private async Task LoadEditorLoadersAsync(ChatMessage editor, string? preferredLoader = null)
    {
        if (string.IsNullOrWhiteSpace(editor.SelectedVersion)) return;
        var requestGeneration = ++editor.LoaderRequestGeneration;
        editor.IsEditorLoading = true;
        editor.IsLoaderSelectionEnabled = false;
        editor.Loaders.Clear();
        editor.SelectedLoader = null;
        editor.EditorStatus = "正在读取该版本支持的加载器...";
        try
        {
            var loaders = await _mcp.GetSupportedLoadersAsync(editor.SelectedVersion);
            if (requestGeneration != editor.LoaderRequestGeneration) return;
            foreach (var loader in loaders)
                editor.Loaders.Add(loader);
            editor.SelectedLoader = loaders.FirstOrDefault(loader =>
                                     loader.Id.Equals(preferredLoader, StringComparison.OrdinalIgnoreCase))
                                 ?? loaders.FirstOrDefault();
            editor.EditorStatus = "Mod 为可选项，留空则只安装游戏和加载器；找不到的 Mod 会自动跳过。";
        }
        catch (Exception ex)
        {
            editor.EditorStatus = $"读取加载器失败：{GetErrorMessage(ex)}";
        }
        finally
        {
            if (requestGeneration == editor.LoaderRequestGeneration)
            {
                editor.IsLoaderSelectionEnabled = true;
                editor.IsEditorLoading = false;
            }
        }
    }

    private void InstallEditorConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ChatMessage editor || !editor.IsInstallEditor)
            return;
        if (!editor.CanConfirmInstall || editor.SelectedLoader == null || string.IsNullOrWhiteSpace(editor.SelectedVersion))
        {
            editor.EditorStatus = "请选择 Minecraft 版本和加载器。";
            return;
        }

        editor.IsEditorCompleted = true;
        editor.IsEditorEnabled = false;
        var mods = editor.ModText
            .Split([',', '，', '、', ';', '；', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(mod => mod.Trim())
            .Where(mod => !string.IsNullOrWhiteSpace(mod))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var plan = new MinecraftInstallPlan(
            editor.SelectedVersion,
            editor.SelectedLoader.Id,
            editor.SelectedLoader.Version,
            mods,
            editor.Chinese,
            editor.SourceText);
        _ = ExecuteInstallPlanAsync(plan);
    }

    private void InstallEditorCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ChatMessage editor || !editor.IsInstallEditor)
            return;
        editor.IsEditorCompleted = true;
        editor.IsEditorEnabled = false;
        editor.EditorStatus = "操作已取消，没有修改实例或下载文件。";
    }

    private static string GetErrorMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && !messages.Contains(current.Message, StringComparer.Ordinal))
                messages.Add(current.Message.Trim());
        }
        return messages.Count == 0 ? "未知错误" : string.Join("\n", messages.Take(3));
    }

    private static void WriteInstallLog(string context, Exception exception)
    {
        try
        {
            var logFile = Path.Combine(App.Paths.Root, "ai-install.log");
            File.AppendAllText(logFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{exception}\n\n");
        }
        catch { }
    }

    private async Task ReplyAsync(string fullAnswer)
    {
        var thinking = AddMessage("星落助手", "…", fromUser: false);
        await Task.Delay(Rng.Next(150, 320));
        await TypewriterAsync(thinking, fullAnswer);
    }

    private async Task TypewriterAsync(ChatMessage message, string fullAnswer)
    {
        message.Text = "";
        var position = 0;
        var pausesLeft = 3;
        var nextTick = DateTime.UtcNow;

        while (position < fullAnswer.Length)
        {
            var now = DateTime.UtcNow;
            if (now < nextTick)
            {
                await Task.Delay(5);
                continue;
            }

            nextTick = now.AddMilliseconds(60);
            position += 2;

            if (position >= fullAnswer.Length)
            {
                message.Text = fullAnswer;
                break;
            }

            message.Text = fullAnswer[..position] + "▊";
            ChatScroll.ScrollToEnd();

            if (pausesLeft > 0 && Rng.NextDouble() < 0.05)
            {
                pausesLeft--;
                await Task.Delay(Rng.Next(20, 151));
            }
        }
    }

    private ChatMessage AddMessage(string who, string text, bool fromUser)
    {
        var bubble = fromUser
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("InputBgBrush");
        var textColor = fromUser
            ? Brushes.White
            : (Brush)FindResource("TextBrush");

        var message = new ChatMessage(
            who,
            text,
            fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            bubble,
            textColor);
        ChatList.Items.Add(message);

        // 只要有任意一条消息（包括开场欢迎语），空状态提示就必须收起，
        // 否则欢迎语会在气泡和提示里各显示一次。
        EmptyHint.Visibility = Visibility.Collapsed;

        Dispatcher.BeginInvoke(() =>
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
        });
        return message;
    }

    private ChatMessage AddInstallEditor(string sourceText, MinecraftInstallPlan? parsedPlan)
    {
        var editor = new ChatMessage(
            "星落助手 · MCP",
            "",
            HorizontalAlignment.Left,
            (Brush)FindResource("InputBgBrush"),
            (Brush)FindResource("TextBrush"),
            isInstallEditor: true,
            sourceText: sourceText,
            modText: parsedPlan == null ? "" : string.Join(", ", parsedPlan.Mods),
            chinese: parsedPlan?.Chinese ?? false);
        ChatList.Items.Add(editor);
        Dispatcher.BeginInvoke(() =>
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
        });
        return editor;
    }

    private sealed class ChatMessage : INotifyPropertyChanged
    {
        public ChatMessage(string who, string text, HorizontalAlignment alignment, Brush bubbleBrush, Brush textColor,
            bool isInstallEditor = false, string sourceText = "", string modText = "", bool chinese = false)
        {
            Who = who;
            _text = text;
            Alignment = alignment;
            BubbleBrush = bubbleBrush;
            TextColor = textColor;
            IsInstallEditor = isInstallEditor;
            SourceText = sourceText;
            _modText = modText;
            _chinese = chinese;
        }

        public string Who { get; }
        public HorizontalAlignment Alignment { get; }
        public Brush BubbleBrush { get; }
        public Brush TextColor { get; }
        public bool IsInstallEditor { get; }
        public Visibility TextVisibility => IsInstallEditor ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EditorVisibility => IsInstallEditor ? Visibility.Visible : Visibility.Collapsed;
        public string SourceText { get; }
        public ObservableCollection<string> Versions { get; } = new();
        public ObservableCollection<MinecraftLoaderChoice> Loaders { get; } = new();
        public int LoaderRequestGeneration { get; set; }

        private string? _selectedVersion;
        public string? SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                _selectedVersion = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }

        private MinecraftLoaderChoice? _selectedLoader;
        public MinecraftLoaderChoice? SelectedLoader
        {
            get => _selectedLoader;
            set
            {
                _selectedLoader = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }

        private string _modText;
        public string ModText
        {
            get => _modText;
            set
            {
                _modText = value;
                OnPropertyChanged();
            }
        }

        private bool _chinese;
        public bool Chinese
        {
            get => _chinese;
            set
            {
                _chinese = value;
                OnPropertyChanged();
            }
        }

        private bool _isEditorEnabled = true;
        public bool IsEditorEnabled
        {
            get => _isEditorEnabled;
            set
            {
                _isEditorEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }

        private bool _isEditorLoading = true;
        public bool IsEditorLoading
        {
            get => _isEditorLoading;
            set
            {
                _isEditorLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }

        private bool _isLoaderSelectionEnabled;
        public bool IsLoaderSelectionEnabled
        {
            get => _isLoaderSelectionEnabled;
            set
            {
                _isLoaderSelectionEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isEditorCompleted;
        public bool IsEditorCompleted
        {
            get => _isEditorCompleted;
            set
            {
                _isEditorCompleted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfirmInstall));
            }
        }

        private string _editorStatus = "正在读取...";
        public string EditorStatus
        {
            get => _editorStatus;
            set
            {
                _editorStatus = value;
                OnPropertyChanged();
            }
        }

        public bool CanConfirmInstall => IsInstallEditor && IsEditorEnabled && !IsEditorLoading
                                         && !IsEditorCompleted && !string.IsNullOrWhiteSpace(SelectedVersion)
                                         && SelectedLoader != null;

        private string _text;
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
