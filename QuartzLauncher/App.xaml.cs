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

    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static ThemeManager Theme { get; private set; } = null!;

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "Launcher", "crash.log");

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
        base.OnExit(e);
    }
}
