using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace QuartzLauncher.Services;

public static class AnimatedMessageBox
{
    public static MessageBoxResult Show(string message) =>
        Show(message, "提示", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current.MainWindow;
        var dialog = new AnimatedDialog(message, title, buttons, image)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true ? dialog.Result : dialog.Result;
    }

    /// <summary>可自定义按钮文字的消息框（顺序：是 / 否 / 取消）。</summary>
    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        (string Yes, string No, string Cancel) labels)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current.MainWindow;
        var dialog = new AnimatedDialog(message, title, buttons, image, true, labels)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true ? dialog.Result : dialog.Result;
    }

    public static void ShowTimed(
        string message,
        string title,
        TimeSpan duration,
        MessageBoxImage image = MessageBoxImage.Information)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current.MainWindow;
        var dialog = new AnimatedDialog(message, title, MessageBoxButton.OK, image, false)
        {
            Owner = owner
        };
        dialog.Show();
        dialog.CloseAfter(duration);
    }

    public static void ShowCopyable(string text, string title = "查看路径")
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current.MainWindow;
        var dialog = new CopyableDialog(text, title)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private sealed class AnimatedDialog : Window
    {
        private readonly Border _card;
        private readonly ScaleTransform _scale;
        private readonly bool _modal;
        private bool _closing;

        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private readonly (string Yes, string No, string Cancel)? _labels;

        public AnimatedDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image, bool modal = true,
            (string Yes, string No, string Cancel)? labels = null)
        {
            _labels = labels;
            _modal = modal;
            Title = title;
            Width = 420;
            MinHeight = 190;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.Transparent;
            AllowsTransparency = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _scale = new ScaleTransform(0.94, 0.94);
            _card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(22, 18, 22, 18),
                RenderTransform = _scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.3,
                    Color = Colors.Black
                }
            };
            _card.SetResourceReference(BackgroundProperty, "DialogCardBrush");
            _card.SetResourceReference(BorderBrushProperty, "BorderBrush");
            _card.Child = CreateContent(message, title, buttons, image);
            Content = new Grid { Margin = new Thickness(24), Children = { _card } };

            Opacity = 0;
            ContentRendered += (_, _) => AnimateIn();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
        }

        private UIElement CreateContent(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = GetIcon(image),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, GetAccentResource(image));

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(titleText, 1);

            var closeIcon = new Path
            {
                Data = Geometry.Parse("M 2,2 L 12,12 M 12,2 L 2,12"),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            };
            closeIcon.SetResourceReference(Shape.StrokeProperty, "TextMutedBrush");
            var closeButton = CreateButton(closeIcon, "BtnBase", 36);
            closeButton.Padding = new Thickness(6);
            closeButton.Click += (_, _) => BeginClose(DefaultCancelResult(buttons));
            Grid.SetColumn(closeButton, 2);

            header.Children.Add(icon);
            header.Children.Add(titleText);
            header.Children.Add(closeButton);

            var messageText = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                MaxWidth = 340,
                Margin = new Thickness(30, 0, 0, 20)
            };
            messageText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetRow(messageText, 1);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            AddButtons(actions, buttons);
            Grid.SetRow(actions, 2);

            root.Children.Add(header);
            root.Children.Add(messageText);
            root.Children.Add(actions);
            return root;
        }

        private void AddButtons(Panel actions, MessageBoxButton buttons)
        {
            if (buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
            {
                var yes = CreateButton(_labels?.Yes ?? "确定", "BtnPrimary", double.NaN);
                yes.Click += (_, _) => BeginClose(MessageBoxResult.Yes);
                actions.Children.Add(yes);

                var no = CreateButton(_labels?.No ?? "取消", "BtnBase", double.NaN);
                no.Click += (_, _) => BeginClose(MessageBoxResult.No);
                actions.Children.Add(no);

                if (buttons == MessageBoxButton.YesNoCancel)
                {
                    var cancel = CreateButton(_labels?.Cancel ?? "关闭", "BtnBase", double.NaN);
                    cancel.Click += (_, _) => BeginClose(MessageBoxResult.Cancel);
                    actions.Children.Add(cancel);
                }
                return;
            }

            if (buttons == MessageBoxButton.OKCancel)
            {
                var ok = CreateButton("确定", "BtnPrimary", 86);
                ok.Click += (_, _) => BeginClose(MessageBoxResult.OK);
                actions.Children.Add(ok);

                var cancel = CreateButton("取消", "BtnBase", 86);
                cancel.Click += (_, _) => BeginClose(MessageBoxResult.Cancel);
                actions.Children.Add(cancel);
                return;
            }

            var only = CreateButton("确定", "BtnPrimary", 86);
            only.Click += (_, _) => BeginClose(MessageBoxResult.OK);
            actions.Children.Add(only);
        }

        private Button CreateButton(object content, string styleKey, double width)
        {
            var button = new Button
            {
                Content = content,
                Width = width,
                Height = 40,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(StyleProperty, styleKey);
            return button;
        }

        private void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        private async void BeginClose(MessageBoxResult result)
        {
            if (_closing) return;
            _closing = true;
            Result = result;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
            BeginAnimation(OpacityProperty, fade);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            await Task.Delay(150);
            if (!IsVisible) return;
            if (_modal)
                DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
            Close();
        }

        public async void CloseAfter(TimeSpan duration)
        {
            await Task.Delay(duration);
            BeginClose(MessageBoxResult.OK);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                BeginClose(MessageBoxResult.Cancel);
                e.Handled = true;
            }
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (!_closing)
            {
                e.Cancel = true;
                BeginClose(MessageBoxResult.Cancel);
            }
        }

        private static string GetIcon(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error => "\uE783",
            MessageBoxImage.Warning => "\uE7BA",
            MessageBoxImage.Question => "\uE897",
            _ => "\uE946"
        };

        private static string GetAccentResource(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error => "DangerBrush",
            MessageBoxImage.Warning => "DangerBrush",
            MessageBoxImage.Information => "SuccessBrush",
            _ => "PrimaryBrush"
        };

        private static MessageBoxResult DefaultCancelResult(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel
        };
    }

    private sealed class CopyableDialog : Window
    {
        private readonly TextBox _textBox;
        private readonly Button _copyButton;

        public CopyableDialog(string text, string title)
        {
            Title = title;
            Width = 680;
            MinHeight = 230;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.Transparent;
            AllowsTransparency = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(22, 18, 22, 18),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.3,
                    Color = Colors.Black
                }
            };
            card.SetResourceReference(BackgroundProperty, "DialogCardBrush");
            card.SetResourceReference(BorderBrushProperty, "BorderBrush");

            var root = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var closeButton = CreateButton("关闭", "BtnBase", 86);
            closeButton.Click += (_, _) => Close();
            Grid.SetColumn(closeButton, 1);
            header.Children.Add(titleText);
            header.Children.Add(closeButton);

            _textBox = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 76,
                MaxHeight = 170,
                Padding = new Thickness(8),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _textBox.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            _textBox.SetResourceReference(TextBox.BackgroundProperty, "InputBgBrush");
            _textBox.SetResourceReference(TextBox.BorderBrushProperty, "BorderBrush");

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _copyButton = CreateButton("复制路径", "BtnPrimary", 100);
            _copyButton.Click += Copy_Click;
            actions.Children.Add(_copyButton);

            root.Children.Add(header);
            root.Children.Add(_textBox);
            root.Children.Add(actions);
            card.Child = root;
            Content = new Grid { Margin = new Thickness(24), Children = { card } };

            Loaded += (_, _) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
            PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape) Close();
            };
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_textBox.Text);
                _copyButton.Content = "已复制";
            }
            catch
            {
                _copyButton.Content = "复制失败";
            }
        }

        private static Button CreateButton(object content, string styleKey, double width)
        {
            var button = new Button
            {
                Content = content,
                Width = width,
                Height = 40,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(StyleProperty, styleKey);
            return button;
        }
    }
}
