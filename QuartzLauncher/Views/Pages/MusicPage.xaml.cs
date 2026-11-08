using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace QuartzLauncher.Views.Pages;

/// <summary>
/// 「音乐」页：整页内嵌 Folia（自托管在服务器 8080）。
/// 没有标题栏/工具栏/边框，播放器铺满；连不上时只显示一句提示 + 重试。
/// </summary>
public partial class MusicPage : Page
{
    private const string ServiceUrl = "http://114.132.152.144:8080/";
    private const string HealthUrl = ServiceUrl + "api/healthz";

    private bool _webViewReady;
    private LocalForwarder? _forwarder;
    private bool _initialized;

    /// <summary>
    /// 这台机器是 Intel HD + 老驱动，Folia 的 backdrop-filter / 大范围模糊 / 粒子动画
    /// 全靠 CPU 光栅化，非常吃帧。这里注入一段样式把它们压掉，观感几乎不变。
    /// </summary>
    private const string PerfCssScript = """
        (() => {
            const apply = () => {
            if (document.getElementById('quartz-perf')) return;
            const style = document.createElement('style');
            style.id = 'quartz-perf';
            style.textContent = `
                * {
                    backdrop-filter: none !important;
                    -webkit-backdrop-filter: none !important;
                }
            `;
            (document.head || document.documentElement)?.appendChild(style);
            };
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', apply);
            else apply();
        })();
        """;

    /// <summary>
    /// Folia 播放时会调 requestFullscreen 进全屏歌词。WebView2 的全屏是把内容顶到
    /// 整个窗口上，会把左侧导航一起盖住，导致"其他按钮点不了"。
    /// 这里直接在页面里把全屏 API 换成空实现，再兜一层事件强制退出。
    /// </summary>
    private const string BlockFullscreenScript = """
        (() => {
            const noop = () => Promise.resolve();
            try {
                Object.defineProperty(Element.prototype, 'requestFullscreen', { value: noop, writable: false });
                Object.defineProperty(Element.prototype, 'webkitRequestFullscreen', { value: noop, writable: false });
                Object.defineProperty(Element.prototype, 'mozRequestFullScreen', { value: noop, writable: false });
                Object.defineProperty(Element.prototype, 'msRequestFullscreen', { value: noop, writable: false });
            } catch (e) {}
        })();
        """;

    public MusicPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await ConnectAsync();
        };
    }

    private async Task ConnectAsync()
    {
        RetryBtn.Visibility = Visibility.Collapsed;
        OfflinePanel.Visibility = Visibility.Visible;
        OfflineTitle.Text = "正在连接音乐服务…";
        OfflineHint.Text = ServiceUrl;

        if (!await PingAsync())
        {
            OfflineTitle.Text = "音乐服务连不上";
            OfflineHint.Text = $"Folia 跑在 {ServiceUrl}（服务器 8080 端口）。检查服务器是否在跑，或点「重试」。";
            RetryBtn.Visibility = Visibility.Visible;
            return;
        }

        OfflinePanel.Visibility = Visibility.Collapsed;

        try
        {
            if (!_webViewReady)
            {
                var dataDir = Path.Combine(AppContext.BaseDirectory, "Launcher", "WebView2-music6");
                Directory.CreateDirectory(dataDir);

                var options = new CoreWebView2EnvironmentOptions
                {
                    // 之前为排查黑屏强制过 --disable-gpu，那是软件渲染，会卡到没法用；
                    // 黑屏真正原因是 crypto.randomUUID（已用回环解决），所以这里恢复 GPU，
                    // 只保留"切窗口不降频"这一条。
                    AdditionalBrowserArguments =
                        "--disable-background-timer-throttling " +
                        "--disable-renderer-backgrounding"
                };
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
                await Player.EnsureCoreWebView2Async(env);
                await Player.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BlockFullscreenScript);
                await Player.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PerfCssScript);
                Player.CoreWebView2.ContainsFullScreenElementChanged += OnFullscreenChanged;
                _webViewReady = true;
            }

            _forwarder ??= new LocalForwarder("114.132.152.144", 8080);
            var localUrl = $"http://127.0.0.1:{_forwarder.Port}/";

            Player.Visibility = Visibility.Visible;
            Player.Source = new Uri(localUrl);
            Player.ZoomFactor = 0.85;   // 少渲染 28% 的像素，老核显上最直接的一刀
            Player.CoreWebView2.ProcessFailed += (_, e) => LogWebView("[ProcessFailed] " + e.ProcessFailedKind);
            Player.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    OfflinePanel.Visibility = Visibility.Visible;
                    OfflineTitle.Text = "页面加载失败";
                    OfflineHint.Text = args.WebErrorStatus.ToString();
                    RetryBtn.Visibility = Visibility.Visible;
                }
            };
            try
            {
                var exc = Player.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
                exc.DevToolsProtocolEventReceived += (_, e) => LogWebView("EXC " + e.ParameterObjectAsJson);
                await Player.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");

                var log = Player.CoreWebView2.GetDevToolsProtocolEventReceiver("Log.entryAdded");
                log.DevToolsProtocolEventReceived += (_, e) => LogWebView("LOG " + e.ParameterObjectAsJson);
                await Player.CoreWebView2.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            }
            catch (Exception ex)
            {
                LogWebView("CDP 启动失败: " + ex.Message);
            }
            Player.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Player.CoreWebView2.Settings.IsStatusBarEnabled = false;
        }
        catch (Exception ex)
        {
            OfflinePanel.Visibility = Visibility.Visible;
            OfflineTitle.Text = "WebView2 初始化失败";
            OfflineHint.Text = ex.Message;
            RetryBtn.Visibility = Visibility.Visible;
        }
    }

    /// <summary>万一还有漏网的全屏（比如浏览器默认快捷键），进来就退掉。</summary>
    private async void OnFullscreenChanged(object? sender, object e)
    {
        try
        {
            if (Player.CoreWebView2?.ContainsFullScreenElement == true)
                await Player.CoreWebView2.ExecuteScriptAsync(
                    "document.exitFullscreen && document.exitFullscreen();");
        }
        catch { }
    }

    /// <summary>把 WebView2 的控制台/进程失败写进日志，黑屏时能查到原因。</summary>
    private static void LogWebView(string message)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Launcher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "music-webview.log"),
                DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static async Task<bool> PingAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var res = await client.GetAsync(HealthUrl);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    ~MusicPage()
    {
        try { _forwarder?.Dispose(); } catch { }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryBtn.IsEnabled = false;
        try { await ConnectAsync(); }
        finally { RetryBtn.IsEnabled = true; }
    }
}


/// <summary>
/// 把 127.0.0.1:随机端口 转发到服务器 8080。
/// 为什么需要：浏览器只在「安全上下文」（https 或 localhost）里提供 crypto.randomUUID，
/// 而 Folia 启动时用它生成 ID；直接访问 http://公网IP 会抛 TypeError 导致页面黑屏。
/// 走本机回环就没这个问题，且不用折腾证书。
/// </summary>
internal sealed class LocalForwarder : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _host;
    private readonly int _port;

    public int Port { get; }

    public LocalForwarder(string host, int port)
    {
        _host = host;
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch { break; }

            _ = HandleAsync(client, token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            TcpClient? upstream = null;
            try
            {
                upstream = new TcpClient();
                await upstream.ConnectAsync(_host, _port, token);

                var clientStream = client.GetStream();
                var upstreamStream = upstream.GetStream();

                var up = clientStream.CopyToAsync(upstreamStream, token);
                var down = upstreamStream.CopyToAsync(clientStream, token);
                await Task.WhenAny(up, down);
            }
            catch { }
            finally { try { upstream?.Dispose(); } catch { } }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}
