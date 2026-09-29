using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace QuartzLauncher.Views;

/// <summary>聚光灯引导的一步：聚焦到哪个元素、讲什么。</summary>
/// <param name="Title">标题。</param>
/// <param name="Body">正文。</param>
/// <param name="Tip">可选补充提示。</param>
/// <param name="Target">定位目标，null 表示不挖洞（只压暗）。</param>
/// <param name="BubbleSide">气泡相对高亮区域的位置。</param>
public sealed record GuideStep(
    string Title,
    string Body,
    string Tip,
    Func<FrameworkElement?>? Target,
    BubbleSide Side = BubbleSide.Right);

/// <summary>气泡相对高亮区域的摆放位置。</summary>
public enum BubbleSide
{
    Right,
    Left,
    Below,
    Above
}

/// <summary>
/// 首次启动时播放的聚光灯新手指南：整页压暗，只有当前步骤对应的元素保持正常亮度并高亮，
/// 旁边浮一张说明气泡。看完或跳过后由调用方把 OnboardingCompleted 置位，之后永不再播。
/// </summary>
public static class FirstRunGuide
{
    private const double HolePadding = 6;
    private const double BubbleWidth = 348;

    /// <summary>是否该播教程（只看设置，不改状态）。</summary>
    public static bool ShouldShow() => !App.Settings.Data.OnboardingCompleted;

    /// <summary>
    /// 在主窗口上就地播放聚光灯引导。非阻塞：立即返回，播放期间用户仍可操作主窗口。
    /// 引导结束（看完 / 跳过 / 关窗）时由本方法自行把 OnboardingCompleted 落盘。
    /// </summary>
    public static void Show(Window owner)
    {
        var root = MainWindow.Current?.WindowRoot;
        if (root is null) return;

        var index = 0;
        var host = new Canvas { IsHitTestVisible = true };
        host.SetValue(Panel.ZIndexProperty, 9999);
        host.VerticalAlignment = VerticalAlignment.Stretch;
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(host, 0);
        Grid.SetRowSpan(host, 3);
        Grid.SetColumn(host, 0);
        Grid.SetColumnSpan(host, 2);
        // Canvas 不会自动拉伸，必须显式跟随根窗口尺寸，否则压暗层没有面积
        host.Width = root.ActualWidth;
        host.Height = root.ActualHeight;
        var onSize = new SizeChangedEventHandler((_, _) =>
        {
            host.Width = root.ActualWidth;
            host.Height = root.ActualHeight;
        });
        root.SizeChanged += onSize;

        // 压暗层：直接用画刷属性挂 GuideDimBrush（主题切换时跟随），不再给 Data 留空
        var dim = new Path();
        dim.SetResourceReference(Shape.FillProperty, "GuideDimBrush");

        // 高亮描边 + 呼吸光晕
        var glow = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Colors.White),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Opacity = 0
        };
        glow.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");

        // 高亮脉冲：给挖洞边缘一圈微光，强调「看这里」
        var pulse = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(6),
            BorderBrush = new SolidColorBrush(Colors.White),
            IsHitTestVisible = false,
            Opacity = 0
        };
        pulse.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");

        // —— 气泡卡片 ——
        var card = new Border
        {
            Width = BubbleWidth,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 16, 20, 14),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 30,
                ShadowDepth = 5,
                Opacity = 0.5,
                Color = Colors.Black
            }
        };
        card.SetResourceReference(Border.BackgroundProperty, "ReadablePanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.BorderThickness = new Thickness(1);

        var counter = new TextBlock { FontSize = 11 };
        counter.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");

        var title = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var body = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 8, 0, 0)
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var tipBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 12, 0, 0)
        };
        tipBorder.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
        var tip = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        tipBorder.Child = tip;

        // 进度点
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var dotList = new List<Ellipse>();
        for (var i = 0; i < Steps.Count; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0) };
            dot.SetResourceReference(Shape.FillProperty, "BorderBrush");
            dotList.Add(dot);
            dots.Children.Add(dot);
        }

        // 底部按钮
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var skipButton = new Button
        {
            Content = "跳过",
            Height = 34,
            Padding = new Thickness(14, 3, 14, 3),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        skipButton.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        Grid.SetColumn(skipButton, 0);
        footer.Children.Add(skipButton);

        var prevButton = new Button
        {
            Content = "上一步",
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 3, 14, 3),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        prevButton.SetResourceReference(FrameworkElement.StyleProperty, "BtnBase");
        Grid.SetColumn(prevButton, 1);
        footer.Children.Add(prevButton);

        var nextButton = new Button
        {
            Height = 34,
            MinWidth = 96,
            Padding = new Thickness(18, 3, 18, 3),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        nextButton.SetResourceReference(FrameworkElement.StyleProperty, "BtnPrimary");
        Grid.SetColumn(nextButton, 2);
        footer.Children.Add(nextButton);

        var cardContent = new StackPanel();
        cardContent.Children.Add(counter);
        cardContent.Children.Add(title);
        cardContent.Children.Add(body);
        cardContent.Children.Add(tipBorder);
        cardContent.Children.Add(dots);
        cardContent.Children.Add(footer);
        card.Child = cardContent;

        host.Children.Add(dim);
        host.Children.Add(pulse);
        host.Children.Add(glow);
        host.Children.Add(card);
        root.Children.Add(host);

        // 事件在所有控件都建好之后再挂，避免局部函数捕获到未赋值的变量
        skipButton.Click += (_, _) => Close();
        prevButton.Click += (_, _) => Go(-1);
        nextButton.Click += (_, _) => Go(1);

        void Go(int delta)
        {
            var next = index + delta;
            if (next < 0) return;
            if (next >= Steps.Count)
            {
                Close();
                return;
            }
            index = next;
            Render();
        }

        void Close()
        {
            host.IsHitTestVisible = false;
            root.SizeChanged -= onSize; // 解绑尺寸跟随
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) =>
            {
                root.Children.Remove(host);
                owner.Activate();
                // 看完、跳过、关窗都算「不再触发」，立刻落盘
                App.Settings.Data.OnboardingCompleted = true;
                App.Settings.Save();
            };
            host.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void Render()
        {
            var step = Steps[index];
            counter.Text = $"{index + 1} / {Steps.Count}";
            title.Text = step.Title;
            body.Text = step.Body;
            if (string.IsNullOrWhiteSpace(step.Tip))
            {
                tipBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                tipBorder.Visibility = Visibility.Visible;
                tip.Text = step.Tip;
            }
            prevButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
            nextButton.Content = index == Steps.Count - 1 ? "开始使用" : "下一步";
            for (var i = 0; i < dotList.Count; i++)
                dotList[i].SetResourceReference(Shape.FillProperty, i == index ? "PrimaryBrush" : "BorderBrush");

            // 等布局稳定后再量坐标，否则第一次拿到的会是旧位置
            host.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => Layout(step)));
        }

        void Layout(GuideStep step)
        {
            var area = new Size(root.ActualWidth, root.ActualHeight);
            if (area.Width <= 0 || area.Height <= 0)
            {
                // 窗口还没量出尺寸，下一帧再试
                host.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    new Action(() => Layout(step)));
                return;
            }

            var target = step.Target?.Invoke();
            Rect hole = new();
            var hasHole = false;
            if (target is not null && target.IsVisible && target.ActualWidth > 0)
            {
                try
                {
                    // 目标在 WindowRoot 坐标系里的位置
                    var origin = target.TransformToAncestor(root).Transform(new Point(0, 0));
                    hole = new Rect(origin, target.RenderSize);
                    hasHole = hole.Width > 4 && hole.Height > 4;
                }
                catch
                {
                    // 元素不在同一棵树里（刚被换页替换），这步就不挖洞
                }
            }

            // 压暗层铺满整个窗口（Canvas 里必须给死尺寸）
            Canvas.SetLeft(dim, 0);
            Canvas.SetTop(dim, 0);
            dim.Width = area.Width;
            dim.Height = area.Height;

            if (hasHole)
            {
                var pad = HolePadding;
                var box = new Rect(
                    Math.Max(0, hole.Left - pad),
                    Math.Max(0, hole.Top - pad),
                    Math.Min(area.Width - Math.Max(0, hole.Left - pad), hole.Width + pad * 2),
                    Math.Min(area.Height - Math.Max(0, hole.Top - pad), hole.Height + pad * 2));

                var r = 10d;
                var figure = new GeometryGroup { FillRule = FillRule.EvenOdd };
                figure.Children.Add(new RectangleGeometry(new Rect(0, 0, area.Width, area.Height)));
                figure.Children.Add(BuildRoundedRect(box, r));
                dim.Data = figure;

                Canvas.SetLeft(glow, box.Left);
                Canvas.SetTop(glow, box.Top);
                glow.Width = box.Width;
                glow.Height = box.Height;
                glow.Visibility = Visibility.Visible;

                Canvas.SetLeft(pulse, box.Left - 4);
                Canvas.SetTop(pulse, box.Top - 4);
                pulse.Width = box.Width + 8;
                pulse.Height = box.Height + 8;
                pulse.Visibility = Visibility.Visible;
                pulse.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames =
                    {
                        new LinearDoubleKeyFrame(0.18, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))),
                        new LinearDoubleKeyFrame(0.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800)))
                    }
                });

                PlaceBubble(box, step.Side, area);
            }
            else
            {
                dim.Data = new RectangleGeometry(new Rect(0, 0, area.Width, area.Height));
                glow.Visibility = Visibility.Collapsed;
                pulse.Visibility = Visibility.Collapsed;

                card.Height = double.NaN;
                host.UpdateLayout();
                card.Measure(new Size(BubbleWidth, double.PositiveInfinity));
                card.UpdateLayout();
                Canvas.SetLeft(card, Math.Max(16, (area.Width - BubbleWidth) / 2));
                Canvas.SetTop(card, Math.Max(16, (area.Height - card.DesiredSize.Height) / 2));
                host.UpdateLayout();
            }

            // 气泡与描边淡入
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            if (hasHole)
            {
                glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd
                });
                glow.Opacity = 1;
            }
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = ease
            });

            // 关键：Canvas 子元素的最终位置在更新布局后才生效
            host.UpdateLayout();
        }

        void PlaceBubble(Rect box, BubbleSide side, Size area)
        {
            const double gap = 16;
            const double margin = 18;

            // 先让卡片按内容自然撑开：清掉上一次可能写死的高度，再量一次
            card.Height = double.NaN;
            host.UpdateLayout();
            card.Measure(new Size(BubbleWidth, double.PositiveInfinity));
            card.UpdateLayout();
            var h = card.DesiredSize.Height;

            double left, top = 0;
            switch (side)
            {
                case BubbleSide.Right:
                    left = box.Right + gap;
                    if (left + BubbleWidth > area.Width - margin) left = box.Left - gap - BubbleWidth;
                    break;
                case BubbleSide.Left:
                    left = box.Left - gap - BubbleWidth;
                    if (left < margin) left = box.Right + gap;
                    break;
                case BubbleSide.Below:
                    left = box.Left + box.Width / 2 - BubbleWidth / 2;
                    top = box.Bottom + gap;
                    break;
                default: // Above
                    left = box.Left + box.Width / 2 - BubbleWidth / 2;
                    top = box.Top - gap - h;
                    break;
            }

            // 左右两侧默认与高亮区域垂直居中
            if (side is BubbleSide.Right or BubbleSide.Left)
                top = box.Top + box.Height / 2 - h / 2;

            // 越界回弹：气泡必须完整留在可视区内，底部按钮才不会被裁掉
            left = Math.Min(Math.Max(margin, left), Math.Max(margin, area.Width - BubbleWidth - margin));
            top = Math.Min(Math.Max(margin, top), Math.Max(margin, area.Height - h - margin));
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            host.UpdateLayout();
        }

        static Geometry BuildRoundedRect(Rect rect, double radius)
        {
            var g = new RectangleGeometry(rect, radius, radius);
            g.Freeze();
            return g;
        }

        Render();

        owner.Activate();
        owner.Focus();
    }

    private static FrameworkElement? NavAt(int index)
    {
        // 侧栏入口是 NavStack 里按顺序排的匿名 RadioButton
        var items = MainWindow.NavStackItems;
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    private static FrameworkElement? LaunchButton()
    {
        var home = MainWindow.Current?.HomePage;
        if (home is null) return null;
        return home.LaunchBtn.IsVisible ? home.LaunchBtn : null;
    }

    /// <summary>
    /// 引导步骤。高亮的都是主界面上真实存在的控件，引导结束后用户就知道这些是能点的。
    /// </summary>
    public static readonly IReadOnlyList<GuideStep> Steps =
    [
        new("欢迎使用星落 LaunCher",
            "这是一个 Minecraft 启动器。\n\n接下来几步，我会把界面压暗、只留下要讲的那块亮着，带你过一遍最常用的入口。",
            "随时可以点「跳过」，跳过之后不会再自动出现。",
            null),
        new("这里开始游戏",
            "选好 Minecraft 版本后，点这个按钮就能启动。\n\n右边是启动日志，每一步下载和加载的进度都会显示在这里。",
            "没装 Java 也没关系，启动器会自己准备好运行环境。",
            () => LaunchButton(),
            BubbleSide.Right),
        new("版本都在「版本库」",
            "装 Minecraft、装 Mod 整合包、切加载器都在这里。\n\n点进去选版本和加载器，再点「快速安装」就会自动下载。",
            "整合包、Mod、光影可以在「资源中心」里直接装，依赖会自动补全。",
            () => NavAt(1)),
        new("「资源中心」装 Mod 和整合包",
            "搜索整合包、Mod、光影、皮肤，点一下就能装进当前版本。\n\n模组之间的依赖关系会自动处理，不用手动下前置。",
            "皮肤库支持 3D 预览，装之前能先看看效果。",
            () => NavAt(4)),
        new("「联机大厅」和朋友联机",
            "开房：在游戏里开单人世界 → 开放到局域网 → 启动器会自动把房间发到大厅。\n\n加入：在这里点「加入房间」，或者直接用朋友发来的房间码。",
            "加了好友之后，可以从好友列表一键进对方的房间。",
            () => NavAt(3)),
        new("主题和设置都在「设置」",
            "换主题、调内存、改下载源、开关动画效果都在「设置」里。\n\n遇到问题先看「帮助」页的日志分析，它会直接告诉你原因和怎么解决。",
            "就这些，点「开始使用」自己去玩吧。",
            () => NavAt(5))
    ];
}
