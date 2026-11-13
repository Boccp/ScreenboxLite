<#
.SYNOPSIS
    编译 ScreenboxLite（主程序 + 安装包），产物在 out\。

.PARAMETER NoSetup
    只编译主程序（开发调试时更快）。
.PARAMETER Test
    编译后运行无界面自检（--self-test）。
#>
param(
    [switch]$NoSetup,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'out'
$net  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf  = Join-Path $net 'WPF'
$csc  = Join-Path $net 'csc.exe'
New-Item -ItemType Directory -Force $out | Out-Null

& (Join-Path $root 'tools\fetch-libmpv.ps1')
if ($LASTEXITCODE -ne 0) { throw '获取 libmpv 失败' }

$refs = @(
    "/r:$wpf\PresentationCore.dll", "/r:$wpf\PresentationFramework.dll", "/r:$wpf\WindowsBase.dll",
    "/r:$net\System.Xaml.dll", "/r:System.dll", "/r:System.Core.dll",
    "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll"
)
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/codepage:65001',
            "/win32icon:$root\ScreenboxLite.ico")

function Invoke-Csc([string[]]$arguments, [string]$what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what 编译失败" }
    Write-Host ("  {0} 编译完成 ({1:0.0}s)" -f $what, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
}

# ---- 主程序 ----
$sources = Get-ChildItem -Recurse -Filter *.cs (Join-Path $root 'src') | ForEach-Object { $_.FullName }
$exe = Join-Path $out 'ScreenboxLite.exe'
Invoke-Csc ($common + $refs + @("/out:$exe") + $sources) 'ScreenboxLite.exe'

$dll = Join-Path $root 'lib\libmpv-2.dll'
$dllOut = Join-Path $out 'libmpv-2.dll'
if (-not (Test-Path $dllOut) -or (Get-Item $dllOut).Length -ne (Get-Item $dll).Length) {
    Copy-Item -Force $dll $dllOut
}

if ($Test) {
    $p = Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "自检失败（退出码 $($p.ExitCode)），详见 %LocalAppData%\ScreenboxLite\logs" }
    Write-Host '  自检通过' -ForegroundColor Green
}

if ($NoSetup) { return }

# ---- 安装包：内嵌主程序与压缩后的 libmpv ----
$gz = Join-Path $out 'libmpv-2.dll.gz'
$gzStamp = "$gz.sha256"
$dllHash = (Get-FileHash -Algorithm SHA256 $dll).Hash
if (-not (Test-Path $gz) -or -not (Test-Path $gzStamp) -or (Get-Content $gzStamp) -ne $dllHash) {
    Write-Host '  压缩 libmpv（仅在版本变化时执行）...' -ForegroundColor Cyan
    $in = [IO.File]::OpenRead($dll)
    $fs = [IO.File]::Create($gz)
    $z  = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionLevel]::Optimal)
    try { $in.CopyTo($z) } finally { $z.Dispose(); $fs.Dispose(); $in.Dispose() }
    Set-Content $gzStamp $dllHash
}
$setup = Join-Path $out 'ScreenboxSetup.exe'
Invoke-Csc ($common + $refs + @(
    "/out:$setup",
    "/resource:$exe,ScreenboxLite.exe",
    "/resource:$gz,libmpv-2.dll.gz",
    "/resource:$(Join-Path $root 'lib\libmpv.lock'),libmpv.lock",
    (Join-Path $root 'setup\ScreenboxSetup.cs'),
    (Join-Path $root 'src\AssemblyInfo.cs')
)) 'ScreenboxSetup.exe'

Write-Host ("产物: {0}" -f $out) -ForegroundColor Cyan
Get-ChildItem $out -Filter *.exe | ForEach-Object { Write-Host ("  {0,-22} {1,8:0.0} MB" -f $_.Name, ($_.Length / 1MB)) }
