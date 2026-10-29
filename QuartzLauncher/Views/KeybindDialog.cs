using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views;

/// <summary>键位弹窗的结果：默认方案（只含原版键位）+ 是否启动时自动应用。</summary>
public sealed record KeybindEditResult(Dictionary<string, string> Binds, bool Enabled);

/// <summary>
/// 键位设置弹窗（默认方案）：
/// · 上方「原版键位」可编辑 —— 会存进启动器，启动游戏时自动写进该实例的 options.txt；
/// · 下方「Mod 键位」只读展示 —— 来自当前实例 options.txt，不纳入默认方案（不保存、不覆盖）；
/// · 改键方式：点一下再按实际按键（Esc 取消 / Backspace 清空）。
/// </summary>
public static class KeybindDialog
{
    public static KeybindEditResult? Show(
        Window? owner,
        string gameRoot,
        IReadOnlyDictionary<string, string> current,
        bool enabled)
    {
        var boxes = new Dictionary<string, KeyCaptureButton>(StringComparer.Ordinal);
        var result = (KeybindEditResult?)null;

        var dialog = new Window
        {
            Title = "默认键位",
            Width = 740,
            Height = 700,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
        };

        var card = new Border
        {
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "ReadablePanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.BorderThickness = new Thickness(1);

        var root = new DockPanel();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var saveBtn = new Button { Content = "保存默认键位", Padding = new Thickness(18, 7, 18, 7) };
        saveBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnPrimary");
        var cancelBtn = new Button { Content = "取消", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0) };
        cancelBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        var defaultBtn = new Button { Content = "恢复默认键位", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
        defaultBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        var reloadBtn = new Button { Content = "从本实例读取", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
        reloadBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        buttons.Children.Add(defaultBtn);
        buttons.Children.Add(reloadBtn);
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveBtn);

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock { Text = "默认键位方案", FontSize = 18, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        header.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "这套方案只保存原版键位，启动游戏时自动写进该实例的 options.txt"
                   + "（游戏运行中不写，否则退出会被覆盖）。"
                   + "\n点一下右边的按键，再按你要的键即可改键；Esc 取消本次录入，Backspace 清空绑定。"
                   + "\n⚠ Minecraft 原生键位只支持单键，Ctrl+X 这类组合键需要 Amecs 之类的 Mod。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        header.Children.Add(hint);

        var enableCheck = new CheckBox
        {
            Content = "启动游戏时自动应用这套键位（推荐）",
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            IsChecked = enabled,
        };
        enableCheck.SetResourceReference(CheckBox.ForegroundProperty, "TextBrush");
        header.Children.Add(enableCheck);

        var comboHint = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Orange,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        header.Children.Add(comboHint);
        DockPanel.SetDock(header, Dock.Top);

        var conflictText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.OrangeRed,
        };
        DockPanel.SetDock(conflictText, Dock.Bottom);

        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 6, 0, 0),
        };

        void AddGroupTitle(string text, string sub)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
            var line = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold };
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            stack.Children.Add(line);
            if (!string.IsNullOrEmpty(sub))
            {
                var subLine = new TextBlock { Text = sub, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                subLine.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                stack.Children.Add(subLine);
            }
            list.Children.Add(stack);
        }

        // ── 原版键位（可编辑、会保存）──
        AddGroupTitle("原版键位", "这些会存进启动器，并在每次启动游戏前写入 options.txt。");
        foreach (var action in KeybindService.Actions)
        {
            var value = current.TryGetValue(action.Id, out var existing) ? existing : action.Default;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = action.Display,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var box = new KeyCaptureButton { Margin = new Thickness(8, 0, 0, 0), DefaultValue = action.Default };
            box.SetValue(value);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            boxes[action.Id] = box;

            list.Children.Add(row);
        }

        // ── Mod 键位（只读、不保存）──
        var liveKeys = KeybindService.Read(gameRoot);
        var modKeys = liveKeys
            .Where(pair => !KeybindService.IsVanillaId(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddGroupTitle("Mod 键位（只读）",
            modKeys.Count == 0
                ? "当前实例没有检测到 Mod 键位。装了带键位的 Mod 后，这里会列出来，但不会纳入默认方案。"
                : $"来自当前实例 options.txt 的 {modKeys.Count} 个 Mod 键位；只做展示，不保存、也不会被覆盖。");

        foreach (var (id, value) in modKeys)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = id,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = id,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var valueText = new TextBlock
            {
                Text = KeybindService.DescribeValue(value),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);

            list.Children.Add(row);
        }

        void RefreshConflicts()
        {
            var currentValues = boxes.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
            var conflicts = KeybindService.DetectConflicts(currentValues);
            conflictText.Text = conflicts.Count == 0
                ? ""
                : "⚠ 冲突：" + string.Join("；", conflicts.Select(item =>
                    $"{KeybindService.DescribeValue(item.Value)} 被 "
                    + string.Join("、", item.Actions.Select(id => KeybindService.DisplayName(id)))
                    + " 同时占用"));
        }

        foreach (var box in boxes.Values)
        {
            box.ValueChanged += RefreshConflicts;
            box.Hint += message =>
            {
                comboHint.Text = message;
                comboHint.Visibility = string.IsNullOrEmpty(message)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };
        }
        RefreshConflicts();

        defaultBtn.Click += (_, _) =>
        {
            foreach (var box in boxes.Values) box.ResetToDefault();
            RefreshConflicts();
        };

        reloadBtn.Click += (_, _) =>
        {
            foreach (var (id, box) in boxes)
            {
                if (liveKeys.TryGetValue(id, out var value)) box.SetValue(value);
            }
            RefreshConflicts();
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        saveBtn.Click += (_, _) =>
        {
            var conflicts = KeybindService.DetectConflicts(
                boxes.ToDictionary(p => p.Key, p => p.Value.Value));
            if (conflicts.Count > 0)
            {
                var go = AnimatedMessageBox.Show(
                    "检测到按键冲突：\n" + string.Join("\n", conflicts.Select(item =>
                        $"· {KeybindService.DescribeValue(item.Value)} → "
                        + string.Join("、", item.Actions.Select(id => KeybindService.DisplayName(id)))))
                    + "\n\n仍然保存吗？（游戏里同一个键会触发多个动作）",
                    "按键冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes) return;
            }

            // 只保存原版键位：Mod 键位一行都不写进方案
            var binds = boxes
                .Where(pair => KeybindService.IsVanillaId(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
            result = new KeybindEditResult(binds, enableCheck.IsChecked == true);
            dialog.Close();
        };

        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(conflictText);
        root.Children.Add(scroll);
        card.Child = root;
        dialog.Content = card;
        dialog.ShowDialog();
        return result;
    }
}

/// <summary>「点一下再按键」的键位录入按钮。</summary>
internal sealed class KeyCaptureButton : Button
{
    private static KeyCaptureButton? _active;
    private bool _capturing;
    private Window? _window;

    /// <summary>按下的修饰键（Ctrl/Shift/Alt/Win）；等 700ms 没等到主键就绑定它本身。</summary>
    private string? _pendingModifier;
    private System.Windows.Threading.DispatcherTimer? _modifierTimer;

    public event Action<string>? Hint;

    public string Value { get; private set; } = "";
    public string DefaultValue { get; init; } = "";
    public event Action? ValueChanged;

    public KeyCaptureButton()
    {
        Height = 30;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Padding = new Thickness(10, 0, 10, 0);
        FontSize = 12;
        Cursor = Cursors.Hand;
        SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        Click += (_, _) =>
        {
            if (_capturing)
            {
                Commit("key.mouse.left");
                return;
            }
            BeginCapture();
        };
    }

    public void SetValue(string value)
    {
        Value = value ?? "";
        Content = KeybindService.DescribeValue(Value);
    }

    public void ResetToDefault()
    {
        SetValue(DefaultValue);
        ValueChanged?.Invoke();
    }

    private void BeginCapture()
    {
        if (_active != null && !ReferenceEquals(_active, this)) _active.EndCapture();
        _window = Window.GetWindow(this);
        if (_window == null) return;
        _capturing = true;
        _active = this;
        Content = "按下按键…（Esc 取消 / Backspace 清空）";
        _window.PreviewKeyDown += OnPreviewKeyDown;
        _window.PreviewMouseDown += OnPreviewMouseDown;
        Focus();
    }

    private void EndCapture()
    {
        _modifierTimer?.Stop();
        _modifierTimer = null;
        _pendingModifier = null;
        _capturing = false;
        if (_window != null)
        {
            _window.PreviewKeyDown -= OnPreviewKeyDown;
            _window.PreviewMouseDown -= OnPreviewMouseDown;
        }
        _window = null;
        if (ReferenceEquals(_active, this)) _active = null;
        Content = KeybindService.DescribeValue(Value);
    }

    private void Commit(string value)
    {
        EndCapture();
        SetValue(value);
        ValueChanged?.Invoke();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndCapture();
            return;
        }
        if (e.Key is Key.Back or Key.Delete)
        {
            e.Handled = true;
            Commit("key.keyboard.unknown");
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        var name = KeybindService.FromVirtualKey(vk);
        if (string.IsNullOrEmpty(name)) return;
        e.Handled = true;

        var isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

        if (isModifier)
        {
            // 不立刻绑定：等 700ms，看玩家是不是要按「Ctrl + X」这种组合
            _pendingModifier = name;
            Content = KeybindService.DescribeValue(name) + " + …（再按主键，或等待只绑这个修饰键）";
            _modifierTimer?.Stop();
            _modifierTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700),
            };
            _modifierTimer.Tick += (_, _) =>
            {
                _modifierTimer?.Stop();
                if (_pendingModifier != null)
                {
                    Hint?.Invoke("已绑定修饰键本身（" + KeybindService.DescribeValue(_pendingModifier) + "）。");
                    Commit(_pendingModifier);
                }
            };
            _modifierTimer.Start();
            return;
        }

        if (_pendingModifier != null)
        {
            // Minecraft 的键位只能存「一个键」，组合键存不进去：
            // 这里绑定主键，并明确告诉玩家修饰键部分需要 Mod（例如 Amecs）。
            var modifier = _pendingModifier;
            _pendingModifier = null;
            _modifierTimer?.Stop();
            Hint?.Invoke(
                "Minecraft 原生键位只支持单键，已绑定「" + KeybindService.DescribeValue(name) +
                "」；「" + KeybindService.DescribeValue(modifier) + " + " +
                KeybindService.DescribeValue(name) + "」这种组合键需要安装支持修饰键的 Mod（例如 Amecs）。");
            Commit(name);
            return;
        }

        Hint?.Invoke("");
        Commit(name);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点在任意「录入框」上时不当作鼠标键提交，交给它的 Click 去切换录入目标
        if (IsInsideCaptureButton(e.OriginalSource as DependencyObject)) return;

        var name = e.ChangedButton switch
        {
            MouseButton.Left => "key.mouse.left",
            MouseButton.Right => "key.mouse.right",
            MouseButton.Middle => "key.mouse.middle",
            MouseButton.XButton1 => "key.mouse.4",
            MouseButton.XButton2 => "key.mouse.5",
            _ => "",
        };
        if (string.IsNullOrEmpty(name)) return;
        e.Handled = true;
        Commit(name);
    }

    private static bool IsInsideCaptureButton(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is KeyCaptureButton) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
