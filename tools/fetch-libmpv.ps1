<#
.SYNOPSIS
    下载 libmpv-2.dll（播放引擎）到 ScreenboxLite\lib\。

.DESCRIPTION
    libmpv 体积约 100MB，不进 Git 仓库（GitHub 单文件上限 100MB），由本脚本按需拉取。
    来源：shinchiro/mpv-winbuild-cmake 的每日构建（mpv 官方 Windows 推荐构建源之一）。
    - 已存在且 SHA256 与 lib\libmpv.lock 一致时直接跳过；
    - -Update 强制拉取最新构建并刷新 lock 文件。
#>
param(
    [switch]$Update
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # PowerShell 5 的进度条会让大文件下载慢几十倍
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root    = Split-Path -Parent $PSScriptRoot
$libDir  = Join-Path $root 'lib'
$dllPath = Join-Path $libDir 'libmpv-2.dll'
$lockPath = Join-Path $libDir 'libmpv.lock'
New-Item -ItemType Directory -Force $libDir | Out-Null

function Get-Sha256([string]$p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }

$lock = $null
if (Test-Path $lockPath) {
    $lock = @{}
    foreach ($l in Get-Content $lockPath) { if ($l -match '^(\w+)=(.*)$') { $lock[$matches[1]] = $matches[2].Trim() } }
}

if (-not $Update -and $lock -and (Test-Path $dllPath) -and ((Get-Sha256 $dllPath) -eq $lock['sha256'])) {
    Write-Host "libmpv 已就绪: $($lock['asset'])" -ForegroundColor Green
    exit 0
}

# 本机常用代理（Clash 默认端口），可用则走代理
$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect('127.0.0.1', 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) { $proxy = 'http://127.0.0.1:7897' }
    $tcp.Close()
} catch {}
$web = @{ Headers = @{ 'User-Agent' = 'ScreenboxLite-fetch-libmpv' } }
if ($proxy) { $web['Proxy'] = $proxy }

$assetUrl = $null; $assetName = $null
if (-not $Update -and $lock -and $lock['url']) {
    $assetUrl = $lock['url']; $assetName = $lock['asset']
} else {
    Write-Host '查询 shinchiro/mpv-winbuild-cmake 最新构建 ...' -ForegroundColor Cyan
    $repo = 'https://github.com/shinchiro/mpv-winbuild-cmake'
    $pattern = 'mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z'
    try {
        $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' @web
        $asset = $rel.assets | Where-Object { $_.name -match "^$pattern$" } | Select-Object -First 1
        if ($asset) { $assetUrl = $asset.browser_download_url; $assetName = $asset.name }
    } catch {
        Write-Host "  GitHub API 不可用（$($_.Exception.Message.Split([char]10)[0])），改用网页解析" -ForegroundColor DarkYellow
    }
    if (-not $assetUrl) {
        # 不依赖 API（匿名 API 有频率限制）：latest 跳转拿到 tag，再读 expanded_assets 列表
        $resp = Invoke-WebRequest -Uri "$repo/releases/latest" -UseBasicParsing -MaximumRedirection 5 @web
        $tag = ($resp.BaseResponse.ResponseUri.AbsoluteUri -split '/')[-1]
        $html = (Invoke-WebRequest -Uri "$repo/releases/expanded_assets/$tag" -UseBasicParsing @web).Content
        $m = [regex]::Match($html, $pattern)
        if (-not $m.Success) { throw "未在 Release $tag 中找到 mpv-dev-x86_64-*.7z" }
        $assetName = $m.Value
        $assetUrl = "$repo/releases/download/$tag/$assetName"
    }
}

$tmp = Join-Path $env:TEMP ("libmpv_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $archive = Join-Path $tmp $assetName
    Write-Host "下载 $assetName ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $assetUrl -OutFile $archive -UseBasicParsing @web
    tar -xf $archive -C $tmp libmpv-2.dll
    if (-not (Test-Path (Join-Path $tmp 'libmpv-2.dll'))) { throw '压缩包内未找到 libmpv-2.dll' }
    $sha = Get-Sha256 (Join-Path $tmp 'libmpv-2.dll')
    if (-not $Update -and $lock -and $lock['sha256'] -and $sha -ne $lock['sha256']) {
        throw "SHA256 与 lib\libmpv.lock 不一致（期望 $($lock['sha256'])，实际 $sha）。如需升级请加 -Update。"
    }
    Copy-Item -Force (Join-Path $tmp 'libmpv-2.dll') $dllPath
    Set-Content -Path $lockPath -Encoding UTF8 -Value @("asset=$assetName", "url=$assetUrl", "sha256=$sha")
    Write-Host "libmpv 已就绪: $assetName ($([math]::Round((Get-Item $dllPath).Length / 1MB, 1)) MB)" -ForegroundColor Green
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
