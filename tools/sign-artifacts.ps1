<#
.SYNOPSIS
    给星落 LaunCher 的发布产物做 Authenticode 代码签名。

.DESCRIPTION
    支持两种方式（二选一）：
      1) 证书已装进本机证书存储（USB Token / 云签名客户端）→ 用 -Thumbprint
      2) PFX 文件                                        → 用 -PfxPath（+ -PfxPassword）
    签名一律带 RFC3161 时间戳；没有时间戳的话，证书一过期签名就作废。

.PARAMETER Path
    要签名的文件，可以给多个。

.EXAMPLE
    .\sign-artifacts.ps1 -Path ..\..\dist\QuartzLauncher.exe -Thumbprint 0A1B2C...

.EXAMPLE
    .\sign-artifacts.ps1 -Path ..\..\dist\QuartzLauncher.exe -PfxPath .\starfall.pfx -PfxPassword '***'
#>
[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    [Parameter(Mandatory = $true, ParameterSetName = 'Thumbprint')]
    [string]$Thumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Pfx')]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword = '',

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [string]$Description = '星落 LaunCher',
    [string]$ProductUrl = 'https://starfallmc.top/'
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }
    foreach ($root in $roots) {
        $hit = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "找不到 signtool.exe。请安装 Windows SDK（勾选『Windows SDK Signing Tools for Desktop Apps』），或把 signtool 加进 PATH。"
}

$signTool = Find-SignTool
Write-Host "signtool: $signTool"

if ($PSCmdlet.ParameterSetName -eq 'Pfx') {
    $PfxPath = (Resolve-Path $PfxPath).Path
}

foreach ($item in $Path) {
    $file = (Resolve-Path $item).Path
    Write-Host "`n=== 签名 $file ==="

    $args = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl,
              '/d', $Description, '/du', $ProductUrl)

    if ($PSCmdlet.ParameterSetName -eq 'Thumbprint') {
        $args += @('/sha1', $Thumbprint, '/sm')   # /sm = 机器存储；证书在用户存储时删掉它
    }
    else {
        $args += @('/f', $PfxPath)
        if ($PfxPassword) { $args += @('/p', $PfxPassword) }
    }
    $args += $file

    & $signTool @args
    if ($LASTEXITCODE -ne 0) { throw "签名失败（signtool 退出码 $LASTEXITCODE）：$file" }

    $sig = Get-AuthenticodeSignature $file
    Write-Host ("验证: {0}  签名者: {1}" -f $sig.Status, $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '（无）' }))
    if ($sig.Status -ne 'Valid') { throw "签名验证未通过：$($sig.StatusMessage)" }
}

Write-Host "`n全部签名完成。"
Write-Host "提示：签名后不要再改动文件（改一个字节签名即失效）。"
