using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace QuartzLauncher.Services;

/// <summary>
/// 问题反馈：自动收集环境信息 + 游戏日志，提交到中继的 <c>/feedback</c>
/// （https://lobby.starfallmc.top/feedback → nginx → sf-relay:7002）。
///
/// 设计原则：
/// 1. **先本机留档** —— 提交失败也能「复制 / 保存诊断包」，不让玩家白写一遍；
/// 2. **日志有上限** —— 只带 latest.log 末尾 400 行 + 最新一份 crash-report 的头部/尾部，
///    避免几百 MB 的日志把请求撑爆（服务端硬上限 512 KB）；
/// 3. **不碰玩家文件** —— 只读，不移动、不删除任何日志。
/// </summary>
public static class FeedbackService
{
    /// <summary>提交地址（走 Cloudflare → nginx → 中继 7002）。</summary>
    // 走官网域名（Cloudflare Pages Function 转发到服务器 8080）。
    // 不能用 lobby.starfallmc.top：那个域名未备案，腾讯云会拦成 ICP 页面。
    // 直连服务器 8080（nginx → sf-relay:7002）。
    // 为什么不用域名：① lobby.starfallmc.top 未备案，腾讯云按 Host 拦成 ICP 页面（8080 端口也拦）；
    //              ② Cloudflare Functions 回源到「裸 IP」会被 CF 直接拒（error code: 1003，已实测）。
    public const string Endpoint = "http://114.132.152.144:8080/feedback";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>环境信息（不含日志），给玩家看、也随反馈一起提交。</summary>
    public static string CollectDiagnostics(string? instanceRoot = null, string? instanceName = null)
    {
        var sb = new StringBuilder();
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString(3) ?? "?";
        sb.AppendLine($"[启动器] QuartzLauncher {version}");
        sb.AppendLine($"[系统] {Environment.OSVersion.VersionString} / {(Environment.Is64BitProcess ? "x64" : "x86")} / 语言 {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        try
        {
            sb.AppendLine($"[运行时] .NET {Environment.Version} / WPF 渲染层级 {RenderCapability.Tier >> 16} / 屏幕 {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");
        }
        catch
        {
            sb.AppendLine("[运行时] 读取失败");
        }

        try
        {
            var s = App.Settings.Data;
            sb.AppendLine($"[外观] {s.ThemeMode} / {s.UiStyle} / {s.ThemeName}" + (string.IsNullOrWhiteSpace(s.ThemePrimary) ? "" : $" / 主色 {s.ThemePrimary}"));
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(instanceName))
            sb.AppendLine($"[当前实例] {instanceName}");

        if (!string.IsNullOrWhiteSpace(instanceRoot) && Directory.Exists(instanceRoot))
        {
            sb.AppendLine($"[游戏目录] {instanceRoot}");
            try
            {
                var (enabled, disabled) = ContentService.CountMods(instanceRoot);
                sb.AppendLine($"[Mod] 启用 {enabled} 个 / 禁用 {disabled} 个");
            }
            catch { }

            var latestLog = Path.Combine(instanceRoot, "logs", "latest.log");
            if (File.Exists(latestLog))
            {
                var info = new FileInfo(latestLog);
                sb.AppendLine($"[游戏日志] logs/latest.log {info.Length / 1024} KB，最后写入 {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                sb.AppendLine("[游戏日志] 没有 logs/latest.log（可能还没启动过这个实例）");
            }

            var crashDir = Path.Combine(instanceRoot, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                var newest = new DirectoryInfo(crashDir).GetFiles("*.txt")
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                sb.AppendLine(newest == null
                    ? "[崩溃报告] crash-reports 目录为空"
                    : $"[崩溃报告] {newest.Name}（{newest.Length / 1024} KB，{newest.LastWriteTime:yyyy-MM-dd HH:mm}）");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>收集日志：latest.log 末尾 + 最新 crash-report 的头尾 + 启动器 crash.log。</summary>
    public static string CollectLogs(string? instanceRoot)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(instanceRoot))
        {
            var latestLog = Path.Combine(instanceRoot, "logs", "latest.log");
            if (File.Exists(latestLog))
            {
                sb.AppendLine("===== logs/latest.log（末尾 400 行）=====");
                sb.AppendLine(TailLines(latestLog, 400));
                sb.AppendLine();
            }

            var crashDir = Path.Combine(instanceRoot, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                var newest = new DirectoryInfo(crashDir).GetFiles("*.txt")
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (newest != null)
                {
                    sb.AppendLine($"===== crash-reports/{newest.Name}（头 120 行 + 尾 60 行）=====");
                    sb.AppendLine(HeadAndTail(newest.FullName, 120, 60));
                    sb.AppendLine();
                }
            }
        }

        // 启动器自己的崩溃记录（数据目录里的 crash.log）
        try
        {
            var launcherCrash = Path.Combine(App.Paths.Root, "crash.log");
            if (File.Exists(launcherCrash))
            {
                sb.AppendLine("===== 启动器 crash.log（末尾 120 行）=====");
                sb.AppendLine(TailLines(launcherCrash, 120));
            }
        }
        catch { }

        return sb.ToString().Trim();
    }

    /// <summary>提交一条反馈。</summary>
    public static async Task<(bool Ok, string Message)> SubmitAsync(
        string kind, string message, string contact, string diagnostics, string logs)
    {
        var payload = new
        {
            kind,
            message,
            contact,
            client = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?",
            diagnostics,
            logs,
            website = "", // 蜜罐：真人始终留空
        };

        try
        {
            using var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Endpoint, content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // 服务端会返回 {"ok":false,"msg":"..."}
                var msg = ExtractJson(body, "msg");
                return (false, string.IsNullOrWhiteSpace(msg) ? $"服务器返回 {(int)response.StatusCode}" : msg);
            }
            var id = ExtractJson(body, "id");
            return (true, string.IsNullOrWhiteSpace(id) ? "已提交" : $"已提交，编号 {id}");
        }
        catch (Exception ex)
        {
            return (false, "网络不通：" + ex.Message);
        }
    }

    /// <summary>把「环境信息 + 描述 + 日志」打包成一份纯文本，供复制/另存。</summary>
    public static string BuildBundle(string kindText, string message, string contact, string diagnostics, string logs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==== 星落 Launcher 反馈 ====");
        sb.AppendLine("类型：" + kindText);
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (!string.IsNullOrWhiteSpace(contact)) sb.AppendLine("联系方式：" + contact);
        sb.AppendLine();
        sb.AppendLine("---- 描述 ----");
        sb.AppendLine(message);
        sb.AppendLine();
        sb.AppendLine("---- 环境 ----");
        sb.AppendLine(diagnostics);
        if (!string.IsNullOrWhiteSpace(logs))
        {
            sb.AppendLine();
            sb.AppendLine("---- 日志 ----");
            sb.AppendLine(logs);
        }
        return sb.ToString();
    }

    /// <summary>保存到桌面，返回文件路径（失败返回 null）。</summary>
    public static string? SaveBundle(string text)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, $"星落反馈-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开目录（日志目录、崩溃报告目录都靠它）。</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    private static string TailLines(string path, int count)
    {
        try
        {
            var lines = File.ReadLines(path);
            var queue = new Queue<string>(count + 1);
            foreach (var line in lines)
            {
                queue.Enqueue(line);
                if (queue.Count > count) queue.Dequeue();
            }
            return string.Join(Environment.NewLine, queue);
        }
        catch (Exception ex)
        {
            return "（读取失败：" + ex.Message + "）";
        }
    }

    private static string HeadAndTail(string path, int head, int tail)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length <= head + tail) return string.Join(Environment.NewLine, lines);
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(Environment.NewLine, lines.Take(head)));
            sb.AppendLine($"……（中间省略 {lines.Length - head - tail} 行）……");
            sb.AppendLine(string.Join(Environment.NewLine, lines.Skip(lines.Length - tail)));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "（读取失败：" + ex.Message + "）";
        }
    }

    private static string ExtractJson(string body, string key)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }
}
