<#
.SYNOPSIS
    重拍官网的实机截图（11 张），发版时更新 starfallmc.top 的展示图。

.DESCRIPTION
    为什么需要：官网首屏图和「界面」图库的每张截图里都带着界面本身。
    发版后不重拍就会出现「文案写 1.1.7、配图还停在上一版」的不一致
    （2026-10-03 被用户抓到过一次）。

    推荐用法：发版前用**即将发布的那个 exe**跑本脚本，再跑 optimize-screenshots.py 压体积。

    ⚠️ 1.1.7 重写过：侧栏从 8 项改成 4 项（首页/联机/资源/设置），
    版本库、帮助、更多功能都不在侧栏了，所以**导航逻辑整个换掉**，
    不再是「按坐标点第 N 项」。现在一律按 UIA 名称找元素再点它的中心 ——
    导航项本身没有可访问名称，但里面的 TextBlock 有，能搜到，点击会冒泡到父级。
    窗口尺寸变了也不用重新量坐标。

.PARAMETER ExePath
    用来截图的 exe。默认取仓库外的 ..\..\dist\QuartzLauncher.exe（本地发布产物）。

.PARAMETER OutDir
    截图输出目录。默认直接写进 website\（官网的图片来源）。

.PARAMETER TargetPid
    已经开着的启动器进程号；给了就不再自己启动。

.NOTES
    - **本文件必须保存为 UTF-8 with BOM**：Windows PowerShell 5.1 按 ANSI 读 .ps1，
      没有 BOM 时脚本里的中文（UIA 元素名「返回」等）会变乱码并直接语法报错。
      改完记得确认 BOM 还在（前三个字节应为 EF BB BF）。
    - 截图时请勿移动鼠标或切换窗口。
    - 依赖 DPI 感知：不做的话 GetWindowRect 返回逻辑坐标，只会截到窗口左上角一块。
#>
param(
    [string]$ExePath = "",
    [string]$OutDir = "",
    [int]$TargetPid = 0,
    [int]$StartupWaitSeconds = 25
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
    if ($proc -eq $null) { throw "启动器没有在 $StartupWaitSeconds 秒内出现窗口（SmartScreen 拦了？或者它没起来）" }
    $TargetPid = $proc.Id
}
$p = Get-Process -Id $TargetPid
$h = $p.MainWindowHandle
if ($h -eq 0) { throw "进程 $TargetPid 没有主窗口" }

# 把窗口弄到前台。**故意不用 ShowWindow + AttachThreadInput**（见 FocusWin 处的说明）。
[void][ShotCap]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 900

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
$win = $null
foreach ($w in $AE::RootElement.FindAll($TS::Children, $cond)) {
    if ($w.Current.NativeWindowHandle -eq $h) { $win = $w; break }
}
if ($win -eq $null) { throw "找不到该进程的 UIA 窗口" }

# ---------- 基础动作 ----------
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
# 把启动器窗口弄到前台。
# ⚠️ 这里**故意只用 SetForegroundWindow**，不用 ShowWindow(9) + AttachThreadInput 那套。
# 实测教训：用「暴力抢前台」的版本时，点侧栏「联机」怎么都跳不过去；
# 而完全不做前台操作的独立探针，同样的坐标一点就中。AttachThreadInput 会把本进程
# 和前台线程的输入队列挂在一起，之后 mouse_event 的去向就不可控了。
# 窗口是刚用 explorer 启动/刚激活的，本来就在前台，温和地确认一下就够。
function FocusWin {
    [void][ShotCap]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 200
}
function El([string]$n) {
    return $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)))
}
# 等某个元素出现（页面是异步加载的，网络慢时要多等）
function WaitEl([string]$n, [int]$seconds = 8) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $e = El $n
        if ($e -ne $null) { return $e }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $null
}
# 两种「点」的手段，互为兜底：
#   ① 合成鼠标（需要窗口在前台、命中测试能落到目标上）
#   ② UIA 模式调用（不经过命中测试）
# ⚠️ 实测教训：**UIA 调用会「假装成功」** —— SelectionItem.Select() 在 WPF RadioButton
# 上不报错，但不会触发导航用的 Checked 事件。如果只试 UIA 并相信它的返回值，
# 就会静默拍出错页面的图。所以现在鼠标优先、UIA 兜底，并且**一律用「页面真的换了没」
# 来判定成败，不信任何一种手段自己的返回值**。
function TryInvoke($e) {
    if ($e -eq $null) { return $false }
    $pat = $null
    try { if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true } } catch {}
    try { if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true } } catch {}
    try { if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle(); return $true } } catch {}
    return $false
}
# 找到的往往是导航项内部的 TextBlock，真正可点的是外面的 RadioButton —— 往上找
function InvokeUia([string]$n) {
    $e = El $n
    if ($e -eq $null) { return $false }
    $cur = $e
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($i = 0; $i -lt 6 -and $cur -ne $null; $i++) {
        if (TryInvoke $cur) { return $true }
        $cur = $walker.GetParent($cur)
    }
    return $false
}
# 合成鼠标点击（先抢前台）
function ClickMouse([string]$n, [int]$waitFirst = 8, [int]$after = 1200) {
    $e = WaitEl $n $waitFirst
    if ($e -eq $null) { return $false }
    FocusWin
    $r = $e.Current.BoundingRectangle
    if ([double]::IsInfinity($r.X) -or $r.Width -le 0) { return $false }
    ClickAt ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds $after
    return $true
}
function ClickText([string]$n, [int]$waitFirst = 8, [int]$after = 1200) {
    if ((WaitEl $n $waitFirst) -eq $null) { Write-Host "  !! 找不到「$n」，跳过"; return $false }
    # 不验证的场合（子导航切换）：先鼠标，鼠标找不到或不可点再走 UIA
    if (ClickMouse $n 1 $after) { return $true }
    if (InvokeUia $n) { Start-Sleep -Milliseconds $after; return $true }
    Write-Host "  !! 「$n」两种方式都点不动"
    return $false
}
# 点导航并**验证页面真的换了**（出现 $expect 才算成功），鼠标 / UIA 交替重试。
# 不验证的话，「点了没反应」会静默产出一张拍错页面的图 —— 比报错更糟。
function ClickNav([string]$n, [string]$expect, [int]$tries = 4, [int]$expectSec = 12) {
    for ($i = 1; $i -le $tries; $i++) {
        if ((WaitEl $n 6) -eq $null) { Write-Host "  !! 找不到「$n」"; return $false }
        if ($i % 2 -eq 1) { [void](ClickMouse $n 2 1400) } else { [void](InvokeUia $n); Start-Sleep -Milliseconds 1400 }
        if (-not $expect) { return $true }
        if ((WaitEl $expect $expectSec) -ne $null) { return $true }
        Write-Host "  ...第 $i 次点「$n」没等到「$expect」"
    }
    Write-Host "  !! 跳转「$n」失败（等不到「$expect」）"
    return $false
}
# 子页面（联机大厅、更多功能、资源中心…）会用自己的导航替换或隐藏主导航，
# 所以每次跳转前先退回主导航
function GoMain {
    for ($i = 0; $i -lt 8; $i++) {
        if ((El '首页') -ne $null) { return $true }
        if (ClickText '返回' 4 2000) { continue } else { return $false }
    }
    return ((El '首页') -ne $null)
}

# 关掉更新公告，否则窄条会占首页版面（也顺带验证它是可点的）
$read = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'AnnouncementReadButton')))
if ($read -ne $null) {
    $r = $read.Current.BoundingRectangle
    ClickAt ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    Write-Host "已标记公告为已读（收起窄条）"
    Start-Sleep -Milliseconds 1800
}

# ---------- 逐页截图 ----------
# 每个主跳转都用 ClickNav 带上「跳过去之后应该出现什么」做验证，
# 不然点了没反应会静默产出拍错页面的图。
Write-Host "开始截图 -> $OutDir"

# 1. 首页（新版式）
if (GoMain) { Start-Sleep -Milliseconds 2500; Shot 'shot-home.png' }

# 2-5. 联机：进主页面之后，子导航（返回 / 大厅 / 服务器 / 世界聊天 / 我的房间 / 好友）才存在
if (GoMain -and (ClickNav '联机' '大厅' 3 15)) {
    Shot 'shot-multiplayer.png'
    # 子页之间切换没有独立标记，靠「点完等一下」+ 人工看图确认
    if (ClickText '世界聊天' 6 4000) { Shot 'shot-chat.png' }
    if (ClickText '好友' 6 4000)     { Shot 'shot-friends.png' }
    if (ClickText '服务器' 6 12000)  { Shot 'shot-server.png' }   # 服务器列表要联网
}

# 6. 资源中心（联网最慢）
if (GoMain -and (ClickNav '资源' '返回' 3 20)) { Shot 'shot-mods.png' }

# 7. 版本列表 —— 侧栏没这一项了，从首页的「版本列表」卡进
if (GoMain) {
    Start-Sleep -Milliseconds 1500
    if (ClickNav '版本列表' '返回' 3 15) { Shot 'shot-versions.png' }
}

# 8. 设置（用「打开更多功能」当标记，它在设置页顶部）
if (GoMain -and (ClickNav '设置' '打开更多功能' 3 12)) { Shot 'shot-settings.png' }

# 9-10. 更多功能里的主题 + 皮肤/预览（更多功能现在收在设置页顶部）
if (GoMain -and (ClickNav '设置' '打开更多功能' 3 12)) {
    if (ClickNav '打开更多功能' '主题' 3 15) {
        Shot 'shot-more.png'                                          # 落在「主题」上
        if (ClickText '皮肤/预览' 6 6000) { Shot 'shot-skin.png' }
    }
}

# 11. 登录切换窗（卡片式，1.1.7 新做的）
if (GoMain) {
    FocusWin
    if (ClickText '账号栏' 5 2500) {
        if ((WaitEl '取消' 6) -ne $null) { Shot 'shot-login.png' } else { Write-Host "  !! 登录窗没弹出来" }
        [void](ClickText '取消' 3 1500)
    }
}

Write-Host ""
Write-Host "完成。建议接着跑： python tools\optimize-screenshots.py   （体积可减约 2/3）"
