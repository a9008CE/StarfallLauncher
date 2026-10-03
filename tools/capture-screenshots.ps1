<#
.SYNOPSIS
    重新拍摄官网的实机截图（11 张），用于发版时更新 starfallmc.net 官网展示图。

.DESCRIPTION
    为什么需要这个脚本：官网首屏图和「界面」图库里的每张截图，左下角都会显示
    「星落 LaunCher x.y.z」。发版后如果不重拍，就会出现「文案写 1.1.6、配图还停在 1.1.1」
    的不一致（2026-10-03 就这样被用户抓到过一次）。

    推荐用法：发版前用**即将发布的那个 exe**跑一遍本脚本，再跑 optimize-screenshots.py 压体积。

.PARAMETER ExePath
    要用来截图的启动器 exe。默认取仓库外的 ..\..\dist\QuartzLauncher.exe（本地发布产物）。

.PARAMETER OutDir
    截图输出目录。默认直接写进 website\（这是官网的图片来源）。

.PARAMETER TargetPid
    已经开着的启动器进程号；给了就不再自己启动。

.NOTES
    - 本文件必须保存为 **UTF-8 with BOM**：Windows PowerShell 5.1 按 ANSI 读取 .ps1，
      没有 BOM 时脚本里的中文（UIA 元素名「返回」）会变成乱码并直接语法报错。
    - 截图时请勿移动鼠标或切换窗口。
    - 依赖已修正的 DPI 感知：不做 DPI 感知时 GetWindowRect 返回逻辑坐标，
      只会截到窗口左上角一块（本项目踩过）。
#>
param(
    [string]$ExePath = "",
    [string]$OutDir = "",
    [int]$TargetPid = 0,
    [int]$StartupWaitSeconds = 20
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot          # tools\ 的上一级 = source\mclauncher-csharp
if (-not $ExePath) { $ExePath = Join-Path (Split-Path -Parent $repoRoot) 'dist\QuartzLauncher.exe' }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'website' }
if (-not (Test-Path $ExePath)) { throw "找不到启动器：$ExePath" }
if (-not (Test-Path $OutDir)) { throw "找不到输出目录：$OutDir" }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ("ShotCap" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ShotCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@
}
[void][ShotCap]::SetProcessDPIAware()   # 必须在任何取坐标之前调用

# ---------- 启动 / 定位窗口 ----------
if ($TargetPid -eq 0) {
    Get-Process QuartzLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Host "启动：$ExePath"
    # 必须用 explorer 启动：从后台 shell 直接 Start-Process 起的 GUI 会被回收
    explorer.exe "$ExePath"
    $deadline = (Get-Date).AddSeconds($StartupWaitSeconds)
    do {
        Start-Sleep -Seconds 1
        $proc = Get-Process QuartzLauncher -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    } while ($proc -eq $null -and (Get-Date) -lt $deadline)
    if ($proc -eq $null) { throw "启动器没有在 $StartupWaitSeconds 秒内出现窗口" }
    $TargetPid = $proc.Id
}
$p = Get-Process -Id $TargetPid
$h = $p.MainWindowHandle
if ($h -eq 0) { throw "进程 $TargetPid 没有主窗口" }

# 抢前台：窗口在后台时，坐标点击会落到别的窗口上
[void][ShotCap]::ShowWindow($h, 9)
$fgT = [ShotCap]::GetWindowThreadProcessId([ShotCap]::GetForegroundWindow(), [IntPtr]::Zero)
[void][ShotCap]::AttachThreadInput($fgT, [ShotCap]::GetCurrentThreadId(), $true)
[void][ShotCap]::SetForegroundWindow($h)
[void][ShotCap]::AttachThreadInput($fgT, [ShotCap]::GetCurrentThreadId(), $false)
Start-Sleep -Milliseconds 900

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
$win = $null
foreach ($w in $AE::RootElement.FindAll($TS::Children, $cond)) {
    if ($w.Current.NativeWindowHandle -eq $h) { $win = $w; break }
}
if ($win -eq $null) { throw "找不到该进程的 UIA 窗口" }

function Shot([string]$name) {
    $r = New-Object 'ShotCap+RECT'; [void][ShotCap]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("  {0}  {1}x{2}" -f $name, $w, $ht)
}
function ClickAt([int]$x, [int]$y) {
    [void][ShotCap]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 150
    [ShotCap]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 70
    [ShotCap]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 140
}
function El([string]$n) {
    return $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)))
}
function ClickNamed([string]$n) {
    $e = El $n
    if ($e -eq $null) { return $false }
    $r = $e.Current.BoundingRectangle
    if ([double]::IsInfinity($r.X) -or $r.Width -le 0) { return $false }
    ClickAt ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    return $true
}
# 子页面会用自己的侧边栏替换主导航（返回/大厅/世界聊天…），所以每次跳转前先退回主导航
function GoMain {
    for ($i = 0; $i -lt 6; $i++) {
        if ((El '首页') -ne $null) { return $true }
        if (ClickNamed '返回') { Start-Sleep -Milliseconds 2400 } else { return $false }
    }
    return ((El '首页') -ne $null)
}

# 关掉更新公告弹窗，否则会盖住首页内容
$read = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'AnnouncementReadButton')))
if ($read -ne $null) {
    $r = $read.Current.BoundingRectangle
    ClickAt ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    Write-Host "已关闭更新公告弹窗"
    Start-Sleep -Milliseconds 2000
}

# ---------- 逐页截图 ----------
# 坐标是主侧边栏各行的位置（UIA 里这些导航项是 RadioButton + StackPanel，没有可访问名称，
# 只能按坐标点）。窗口尺寸变化时需要重新量。
Write-Host "开始截图 -> $OutDir"
$mainPages = @(
    @('shot-home.png',      407, 289, 3000),   # 首页
    @('shot-versions.png',  407, 351, 6000),   # 版本库（要等版本列表联网）
    @('shot-server.png',    407, 413, 9000),   # 服务器（要等服务器列表联网）
    @('shot-settings.png',  407, 599, 4000),   # 设置
    @('shot-help.png',      407, 661, 4000)    # 帮助
)
foreach ($pg in $mainPages) {
    if (-not (GoMain)) { Write-Host "  退回主导航失败，跳过 $($pg[0])"; continue }
    ClickAt $pg[1] $pg[2]
    Start-Sleep -Milliseconds $pg[3]
    Shot $pg[0]
}

# 联机大厅的三个子页
if (GoMain) {
    ClickAt 407 475; Start-Sleep -Milliseconds 3500; Shot 'shot-multiplayer.png'
    ClickAt 407 413; Start-Sleep -Milliseconds 3500; Shot 'shot-chat.png'
    ClickAt 407 537; Start-Sleep -Milliseconds 3500; Shot 'shot-friends.png'
}
# 资源中心（联网最慢）
if (GoMain) {
    ClickAt 407 537; Start-Sleep -Milliseconds 11000; Shot 'shot-mods.png'
}
# 更多功能 + 皮肤/预览
if (GoMain) {
    ClickAt 407 723; Start-Sleep -Milliseconds 4000; Shot 'shot-more.png'
    ClickAt 407 598; Start-Sleep -Milliseconds 5000; Shot 'shot-skin.png'
}

Write-Host ""
Write-Host "完成。建议接着跑： python tools\optimize-screenshots.py   （体积可减约 2/3）"
