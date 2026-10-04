using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using QuartzLauncher.Models;
using QuartzLauncher.Services;

namespace QuartzLauncher;

public partial class App : Application
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? pathName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SwRestore = 9;

    /// <summary>
    /// 单实例互斥体。必须活到进程结束，否则会被 GC 回收导致锁提前释放。
    /// </summary>
    private static Mutex? _singleInstanceMutex;

    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static ThemeManager Theme { get; private set; } = null!;

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "Launcher", "crash.log");

    /// <summary>
    /// 把已在运行的那个实例的窗口拉到前台。找不到就什么也不做。
    /// </summary>
    private static void ActivateRunningInstance()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                if (other.Id == self.Id || other.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(other.MainWindowHandle, SwRestore);
                SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
        catch
        {
            // 拿不到别的进程就静默退出，不影响本次启动
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (s, ex) =>
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now}] {ex.Exception}\n\n"); } catch { }
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            if (ex.ExceptionObject is Exception exObj)
                try { File.AppendAllText(LogFile, $"[{DateTime.Now}] FATAL: {exObj}\n\n"); } catch { }
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (e.Args.Length > 0 && e.Args[0] == "--apply-update")
            {
                UpdateService.ApplyFromArguments(e.Args);
                Shutdown();
                return;
            }

            if (e.Args.Length > 0 && e.Args[0] == "--update-server")
            {
                try
                {
                    UpdateService.RunLocalServer(e.Args);
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(LogFile, $"[{DateTime.Now}] UPDATE-SERVER: {ex.Message}\n\n"); } catch { }
                }
                Shutdown();
                return;
            }

            // 单实例保护：已经在跑就直接把那个窗口拉到前台，然后退出。
            // 放在参数分支之后 —— --apply-update / --update-server 是合法的辅助进程，不能拦。
            // 没有这道锁时连点图标会开出一堆窗口（实测同屏跑到 8 个）。
            _singleInstanceMutex = new Mutex(true, @"Local\QuartzLauncher.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                ActivateRunningInstance();
                Shutdown();
                return;
            }

            Paths = AppPaths.Default();
            Paths.EnsureConfiguration();
            SetDllDirectory(Paths.Root);
            var portableTemp = Paths.TempDir;
            Environment.SetEnvironmentVariable("TEMP", portableTemp);
            Environment.SetEnvironmentVariable("TMP", portableTemp);
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", portableTemp);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Paths.BrowserDataDir);
            Settings = SettingsService.Load(Paths);
            var instanceStore = new InstanceStore(Paths.InstancesDir);
            foreach (var instance in instanceStore.List())
            {
                var changed = false;
                if (instance.VersionIsolation == null)
                {
                    // Instances created before isolation settings existed must keep their old folder.
                    instance.VersionIsolation = true;
                    changed = true;
                }
                if (instance.UsesVersionDirectory == null)
                {
                    // New instances explicitly opt into versions/<id>; null means legacy storage.
                    instance.UsesVersionDirectory = false;
                    changed = true;
                }
                if (changed) instanceStore.Create(instance);
            }
            DownloadService.SetSourceMode(Settings.Data.DownloadSource);
            DownloadManager.Instance.MaxConcurrent = Settings.Data.ManualDownloadThreads;
            DownloadManager.Instance.ResourceMaxConcurrent = Settings.Data.ConcurrentDownloads;
            DownloadManager.Instance.AutoAdjustConcurrency = Settings.Data.AutoDownloadThreads;
            Theme = new ThemeManager(Settings);
            Theme.Apply();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now}] INIT: {ex}\n\n"); } catch { }
            MessageBox.Show($"启动器初始化失败：{ex.Message}\n\n详情已写入 Launcher\\crash.log。",
                "星落LaunCher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { UpdateService.StopLocalServer(); } catch { }
        try
        {
            if (Settings is not null) Settings.Save();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now}] EXIT: {ex}\n\n"); } catch { }
        }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
