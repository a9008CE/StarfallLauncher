using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views;

/// <summary>
/// 临时公告弹窗：后台发一条、启动器收到就弹一次。
/// 版式与其它弹窗一致（圆角卡片、主题笔刷），支持 info / warn / critical 三种级别。
/// </summary>
public static class NoticeDialog
{
    public static void Show(Window? owner, AnnouncementService.Notice notice)
    {
        var accent = notice.Level switch
        {
            "critical" => Color.FromRgb(0xF2, 0x6D, 0x6D),
            "warn" => Color.FromRgb(0xF0, 0xB3, 0x2E),
            _ => Color.FromRgb(0x4F, 0xD1, 0xC5),
        };

        var dialog = new Window
        {
            Title = notice.Title,
            Width = 520,
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
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "ReadablePanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.BorderThickness = new Thickness(1);

        var panel = new StackPanel();

        // 顶部：色条 + 标题
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(accent),
            Margin = new Thickness(0, 2, 12, 2),
        };
        Grid.SetColumn(bar, 0);
        head.Children.Add(bar);

        var titleBlock = new TextBlock
        {
            Text = notice.Title,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(titleBlock, 1);
        head.Children.Add(titleBlock);
        panel.Children.Add(head);

        // 级别小字
        var levelText = notice.Level switch
        {
            "critical" => "重要提醒",
            "warn" => "注意",
            _ => "公告",
        };
        var levelRow = new TextBlock
        {
            Text = levelText + " · " + DateTime.Now.ToString("yyyy-MM-dd"),
            FontSize = 11,
            Margin = new Thickness(16, 6, 0, 0),
            Foreground = new SolidColorBrush(accent),
        };
        panel.Children.Add(levelRow);

        // 正文
        var bodyBox = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(10),
        };
        bodyBox.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        bodyBox.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        bodyBox.BorderThickness = new Thickness(1);

        var body = new TextBlock
        {
            Text = notice.Body,
            FontSize = 13.5,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320,
        };
        bodyBox.Child = scroll;
        panel.Children.Add(bodyBox);

        // 底部按钮
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        if (!string.IsNullOrWhiteSpace(notice.Link))
        {
            var linkBtn = new Button
            {
                Content = "查看详情",
                Height = 34,
                MinWidth = 88,
                Margin = new Thickness(0, 0, 10, 0),
            };
            linkBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
            linkBtn.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(notice.Link) { UseShellExecute = true }); }
                catch { }
                dialog.Close();
            };
            footer.Children.Add(linkBtn);
        }

        var okBtn = new Button
        {
            Content = "知道了",
            Height = 34,
            MinWidth = 96,
            IsDefault = true,
        };
        okBtn.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        okBtn.Click += (_, _) => dialog.Close();
        footer.Children.Add(okBtn);

        panel.Children.Add(footer);
        card.Child = panel;
        dialog.Content = card;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter) dialog.Close();
        };

        dialog.ShowDialog();
    }
}
