using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views;

/// <summary>
/// 服务器详情弹窗：大图标 + 完整描述 + 版本/人数/延迟/国家 + 打开官网条目。
/// 说明：找服玩的数据里**没有服务器 IP**（只有详情页链接），所以这里不提供「一键加入」。
/// </summary>
public static class ServerDetailDialog
{
    public static void Show(Window? owner, ServerDirectoryItem server, string? version)
    {
        var dialog = new Window
        {
            Title = server.Name,
            Width = 560,
            SizeToContent = SizeToContent.Height,
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
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "ReadablePanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.BorderThickness = new Thickness(1);

        var panel = new StackPanel();

        // 顶部：图标 + 名称 + 标签
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x30)),
        };
        if (Uri.TryCreate(server.IconUrl, UriKind.Absolute, out var iconUri))
        {
            icon.Child = new Image
            {
                Source = new BitmapImage(iconUri),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4),
            };
        }
        Grid.SetColumn(icon, 0);
        head.Children.Add(icon);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = server.Name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        titleStack.Children.Add(name);

        var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        void AddTag(string text, Brush? foreground = null)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(1),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = foreground ?? (Brush)Application.Current.TryFindResource("TextMutedBrush") ?? Brushes.Gray,
            };
            border.Child = textBlock;
            tags.Children.Add(border);
        }

        if (!string.IsNullOrWhiteSpace(version)) AddTag("MC " + version);
        if (!string.IsNullOrWhiteSpace(server.Players)) AddTag(server.Players.Trim() + " 人");
        if (!string.IsNullOrWhiteSpace(server.Ping)) AddTag(server.Ping + " ms", PingBrush(server.Ping));
        if (!string.IsNullOrWhiteSpace(server.CountryCode)) AddTag(server.CountryCode);
        titleStack.Children.Add(tags);

        Grid.SetColumn(titleStack, 1);
        head.Children.Add(titleStack);
        panel.Children.Add(head);

        // 完整描述
        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.Description) ? "（该服务器没有填写简介）" : server.Description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 16, 0, 0),
        };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(desc);

        var note = new TextBlock
        {
            Text = "数据来自 MC百科找服玩。这里没有服务器 IP（数据源只提供详情页），"
                   + "要进服请在官网条目里复制地址。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(note);

        // 按钮
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var openBtn = new Button { Content = "在浏览器打开条目", Padding = new Thickness(16, 7, 16, 7) };
        openBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnPrimary");
        openBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(server.PageUrl)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(server.PageUrl) { UseShellExecute = true }); }
            catch { /* 打不开就算了 */ }
        };
        var copyBtn = new Button { Content = "复制名称", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
        copyBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(server.Name); } catch { }
        };
        var closeBtn = new Button { Content = "关闭", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
        closeBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        closeBtn.Click += (_, _) => dialog.Close();
        buttons.Children.Add(copyBtn);
        buttons.Children.Add(openBtn);
        buttons.Children.Add(closeBtn);
        panel.Children.Add(buttons);

        card.Child = panel;
        dialog.Content = card;
        dialog.ShowDialog();
    }

    /// <summary>Ping 颜色分级：&lt;60 绿 / &lt;150 橙 / 其余红。</summary>
    public static Brush PingBrush(string ping)
    {
        if (int.TryParse(new string(ping.Where(char.IsDigit).ToArray()), out var ms))
        {
            if (ms < 60) return new SolidColorBrush(Color.FromRgb(0x3D, 0xD5, 0x6A));
            if (ms < 150) return new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x2E));
            return new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
        return Brushes.Gray;
    }
}
