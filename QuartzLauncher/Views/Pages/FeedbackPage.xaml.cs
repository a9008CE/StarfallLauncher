using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher.Views.Pages;

public partial class FeedbackPage : Page
{
    private string _diagnostics = "";
    private string? _instanceRoot;

    public FeedbackPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshContext();
    }

    /// <summary>读取当前选中的实例，刷新环境信息预览。</summary>
    private void RefreshContext()
    {
        try
        {
            var main = Window.GetWindow(this) as MainWindow;
            var instance = main?.HomePage?.SelectedInstance;
            if (instance != null)
            {
                _instanceRoot = InstancePathService.GetGameDirectory(App.Paths, App.Settings.Data, instance);
                _diagnostics = FeedbackService.CollectDiagnostics(_instanceRoot, instance.Name);
            }
            else
            {
                _diagnostics = FeedbackService.CollectDiagnostics(null, null);
            }
        }
        catch
        {
            _diagnostics = "（环境信息读取失败）";
        }
        DiagBox.Text = _diagnostics;
    }

    private string CurrentKind => CrashRadio.IsChecked == true ? "crash"
        : IdeaRadio.IsChecked == true ? "idea"
        : "bug";

    private string CurrentKindText => CurrentKind switch
    {
        "crash" => "游戏报错 / 崩溃",
        "idea" => "建议 / 想要的功能",
        _ => "功能异常 / BUG",
    };

    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var length = MessageBox.Text.Trim().Length;
        MessageHint.Text = length == 0
            ? "至少写 4 个字"
            : length < 4 ? $"还差 {4 - length} 个字" : $"{length} 个字（够清楚了）";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var message = MessageBox.Text.Trim();
        if (message.Length < 4)
        {
            StatusText.Text = "先写清楚发生了什么（至少 4 个字）再提交。";
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusText.Text = IncludeLogsCheck.IsChecked == true ? "正在收集日志并提交…" : "正在提交…";
        try
        {
            var logs = IncludeLogsCheck.IsChecked == true
                ? FeedbackService.CollectLogs(_instanceRoot)
                : "";
            var (ok, reply) = await FeedbackService.SubmitAsync(
                CurrentKind, message, ContactBox.Text.Trim(), _diagnostics, logs);

            if (ok)
            {
                StatusText.Text = $"✅ {reply}。已经收到了，谢谢你！";
                MessageBox.Text = "";
            }
            else
            {
                // 提交失败也别让玩家白写：直接存一份到桌面
                var path = FeedbackService.SaveBundle(
                    FeedbackService.BuildBundle(CurrentKindText, message, ContactBox.Text.Trim(), _diagnostics, logs));
                StatusText.Text = $"❌ 没提交成功（{reply}）。" +
                    (path == null ? "可以点「复制诊断信息」发给管理员。" : $"已保存到桌面：{path}");
            }
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logs = IncludeLogsCheck.IsChecked == true ? FeedbackService.CollectLogs(_instanceRoot) : "";
            Clipboard.SetText(FeedbackService.BuildBundle(
                CurrentKindText, MessageBox.Text.Trim(), ContactBox.Text.Trim(), _diagnostics, logs));
            StatusText.Text = "已复制到剪贴板（含描述、环境信息" + (logs.Length > 0 ? "和日志" : "") + "）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var logs = IncludeLogsCheck.IsChecked == true ? FeedbackService.CollectLogs(_instanceRoot) : "";
        var path = FeedbackService.SaveBundle(FeedbackService.BuildBundle(
            CurrentKindText, MessageBox.Text.Trim(), ContactBox.Text.Trim(), _diagnostics, logs));
        StatusText.Text = path == null ? "保存失败，换个方式（复制诊断信息）。" : "已保存到：" + path;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instanceRoot)) return;
        var logsDir = Path.Combine(_instanceRoot, "logs");
        if (!Directory.Exists(logsDir))
        {
            StatusText.Text = "这个实例还没有 logs 目录（没启动过游戏？）。";
            return;
        }
        FeedbackService.OpenFolder(logsDir);
    }

    private void OpenCrashReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instanceRoot)) return;
        var crashDir = Path.Combine(_instanceRoot, "crash-reports");
        if (!Directory.Exists(crashDir))
        {
            StatusText.Text = "这个实例没有 crash-reports 目录（没崩过是好事）。";
            return;
        }
        FeedbackService.OpenFolder(crashDir);
    }
}
