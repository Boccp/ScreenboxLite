param(
    [switch]$Force,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "ScreenboxLite - 从 GitHub 拉取更新到本地（带防旧版本降级/防丢改动保护）"

$repoDir = $PSScriptRoot
$login = "Boccp"
$repoName = "ScreenboxLite"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   ScreenboxLite 远端版本检测与安全拉取安装器" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "本地仓库目录: $repoDir" -ForegroundColor Gray
Write-Host ""

function Pause-IfNeeded([int]$sec = 4) {
    if (-not $NoPause) { Start-Sleep -Seconds $sec }
}

function Parse-SemVer([string]$verText) {
    if ([string]::IsNullOrWhiteSpace($verText)) { return [version]"0.0.0.0" }
    $clean = $verText.Trim().TrimStart('v', 'V')
    if ($clean -match '(\d+\.\d+(?:\.\d+){0,2})') {
        $vStr = $matches[1]
        $parts = $vStr.Split('.')
        while ($parts.Count -lt 4) { $parts += "0" }
        try { return [version]($parts[0..3] -join '.') } catch {}
    }
    return [version]"0.0.0.0"
}

function Get-GitBlobSha([string]$filePath) {
    if (-not (Test-Path $filePath)) { return "" }
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $combined = New-Object byte[] ($header.Length + $bytes.Length)
    [System.Array]::Copy($header, 0, $combined, 0, $header.Length)
    [System.Array]::Copy($bytes, 0, $combined, $header.Length, $bytes.Length)
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    $hashBytes = $sha1.ComputeHash($combined)
    return ($hashBytes | ForEach-Object { $_.ToString("x2") }) -join ""
}

$localVer = [version]"1.4.0.0"
$verFile = Join-Path $repoDir "version.txt"
if (Test-Path $verFile) {
    $localVer = Parse-SemVer (Get-Content $verFile -Raw -Encoding UTF8)
}

$csPath = Join-Path $repoDir "ScreenboxLite.cs"
$localCsSha = Get-GitBlobSha $csPath
$localTimeUtc = if (Test-Path $csPath) { (Get-Item $csPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$localHeadSha = ""
$localHeadTimeUtc = [datetime]::MinValue
$isDirty = $false
if (Test-Path "$repoDir\.git") {
    try {
        $localHeadSha = (git -C $repoDir rev-parse HEAD 2>$null).Trim()
        $ct = (git -C $repoDir show -s --format=%cI HEAD 2>$null).Trim()
        if ($ct) { $localHeadTimeUtc = [datetime]::Parse($ct).ToUniversalTime() }
        $dirtyLines = git -C $repoDir status --porcelain -- "ScreenboxLite.cs" "ScreenboxSetup.cs" 2>$null
        if ($dirtyLines) { $isDirty = $true }
    } catch {}
}

$commitFile = Join-Path $repoDir ".last_synced_commit"
if (-not $localHeadSha -and (Test-Path $commitFile)) {
    $lines = Get-Content $commitFile -Encoding UTF8
    if ($lines.Count -ge 1) { $localHeadSha = $lines[0].Trim() }
    if ($lines.Count -ge 2) {
        try { $localHeadTimeUtc = [datetime]::Parse($lines[1].Trim()).ToUniversalTime() } catch {}
    }
}

$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) { $proxy = "http://127.0.0.1:7897" }
    $tcp.Close()
} catch {}

$token = $env:GITHUB_TOKEN
$tokenFile = Join-Path $repoDir ".github_token"
if (-not $token -and (Test-Path $tokenFile)) {
    $token = (Get-Content $tokenFile -Raw -Encoding UTF8).Trim()
}
if (-not $token) {
    $gcmCandidates = @(
        "C:\Users\Administrator\AppData\Local\hermes\tools\git-2.53.0+3-win32-x64\mingw64\bin\git-credential-manager.exe",
        "C:\Users\Admin\AppData\Local\hermes\tools\git-2.53.0+3-win32-x64\mingw64\bin\git-credential-manager.exe",
        "C:\Program Files\Git\mingw64\bin\git-credential-manager.exe"
    )
    foreach ($gcm in $gcmCandidates) {
        if (Test-Path $gcm) {
            try {
                $env:GCM_INTERACTIVE = "never"
                $credInput = "protocol=https`nhost=github.com`nusername=$login`n`n"
                $credLines = $credInput | & $gcm get 2>&1
                foreach ($line in $credLines) {
                    if ($line -match '^password=(.+)$') { $token = $matches[1].Trim(); break }
                }
            } catch {}
            if ($token) { break }
        }
    }
}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "ScreenboxLite-VersionChecker"
}
if ($token) { $headers["Authorization"] = "Bearer $token" }
$invokeParams = @{ Headers = $headers }
if ($proxy) { $invokeParams["Proxy"] = $proxy }

function Invoke-GitHubApi([string]$Uri) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            if ($attempt -eq 3 -and $invokeParams.ContainsKey("Proxy")) {
                $noProxy = @{ Headers = $headers }
                return Invoke-RestMethod -Method Get -Uri $Uri @noProxy -TimeoutSec 20
            }
            return Invoke-RestMethod -Method Get -Uri $Uri @invokeParams -TimeoutSec 20
        } catch {
            if ($attempt -eq 4) { throw }
            Start-Sleep -Milliseconds (400 * $attempt)
        }
    }
}

Write-Host "[1/2] 正在检测本地版本与 GitHub ($login/$repoName) 远端版本 ..." -ForegroundColor Yellow

$remoteVer = [version]"1.4.0.0"
try {
    $latestRel = Invoke-GitHubApi "https://api.github.com/repos/$login/$repoName/releases/latest"
    if ($latestRel -and $latestRel.tag_name) {
        $remoteVer = Parse-SemVer $latestRel.tag_name
    }
} catch {}

$commits = Invoke-GitHubApi "https://api.github.com/repos/$login/$repoName/commits?per_page=1"
$headCommit = $commits[0]
$remoteCommitSha = $headCommit.sha
$remoteCommitShort = $remoteCommitSha.Substring(0, 7)
$remoteCommitTimeUtc = [datetime]::Parse($headCommit.commit.committer.date).ToUniversalTime()
$remoteCommitMsg = ($headCommit.commit.message -split "`n")[0].Trim()

$tree = Invoke-GitHubApi "https://api.github.com/repos/$login/$repoName/git/trees/$remoteCommitSha`?recursive=1"
$remoteCsBlobSha = ""
foreach ($item in $tree.tree) {
    if ($item.path -eq "ScreenboxLite.cs") {
        $remoteCsBlobSha = $item.sha
    }
    if ($item.path -eq "version.txt") {
        $blob = Invoke-GitHubApi $item.url
        $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($blob.content))
        $v = Parse-SemVer $text
        if ($v -gt $remoteVer) { $remoteVer = $v }
    }
}

$localShort = if ($localHeadSha.Length -ge 7) { $localHeadSha.Substring(0, 7) } else { "unknown" }
Write-Host ("      本地当前版本 : v{0} (Commit: {1}, 源码SHA: {2})" -f $localVer.ToString(3), $localShort, $(if ($localCsSha) { $localCsSha.Substring(0, 7) } else { "none" })) -ForegroundColor White
Write-Host ("      远端最新版本 : v{0} (Commit: {1} @ {2} UTC, 源码SHA: {3})" -f $remoteVer.ToString(3), $remoteCommitShort, $remoteCommitTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"), $(if ($remoteCsBlobSha) { $remoteCsBlobSha.Substring(0, 7) } else { "none" })) -ForegroundColor Cyan
Write-Host ("      远端提交说明 : {0}" -f $remoteCommitMsg) -ForegroundColor Gray
Write-Host ""

if (-not $Force) {
    if ($isDirty) {
        Write-Host "⛔ [防呆拦截：本地有未推送的新改动] 检测到本地 ScreenboxLite 源码相比上次提交有未保存/未推送的修改！" -ForegroundColor Red
        Write-Host "   为防止云端旧文件覆盖冲掉你刚修改的代码，已自动中止拉取。请先执行「推送到 GitHub」或传入 -Force。" -ForegroundColor Yellow
        Pause-IfNeeded 4
        exit 0
    }
    if ($remoteVer -lt $localVer) {
        Write-Host "⛔ [防降级拦截] 云端版本号 (v$($remoteVer.ToString(3))) 低于本地版本号 (v$($localVer.ToString(3)))，禁止覆盖！" -ForegroundColor Red
        Pause-IfNeeded 3
        exit 0
    }
    if ($localCsSha -and $remoteCsBlobSha -and ($localCsSha -eq $remoteCsBlobSha)) {
        $repoExe = Join-Path $repoDir "ScreenboxLite.exe"
        $instExe = "$env:LOCALAPPDATA\Programs\ScreenboxLite\ScreenboxLite.exe"
        if ((Test-Path $repoExe) -and ((Get-GitBlobSha $instExe) -ne (Get-GitBlobSha $repoExe))) {
            Write-Host "🔄 本地仓库已是最新，检测到安装目录未对齐，正在执行静默覆盖安装 ..." -ForegroundColor Yellow
            Start-Process -FilePath "$repoDir\ScreenboxSetup.exe" -ArgumentList "/silent" -Wait
            Write-Host "✅ 本机安装目录已对齐到最新 v$($localVer.ToString(3)) ($remoteCommitShort)。" -ForegroundColor Green
            Pause-IfNeeded 2
            exit 0
        }
        Write-Host "✅ [已是最新 · 防重复拦截] 本地与云端完全一致 (v$($remoteVer.ToString(3)), Commit $remoteCommitShort)，无需重复拉取。" -ForegroundColor Green
        Pause-IfNeeded 2
        exit 0
    }
    if (Test-Path "$repoDir\.git") {
        try {
            git -C $repoDir cat-file -e "$remoteCommitSha^{commit}" 2>$null
            if ($LASTEXITCODE -eq 0 -and $localHeadSha -ne $remoteCommitSha) {
                git -C $repoDir merge-base --is-ancestor $remoteCommitSha HEAD 2>$null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "⛔ [防回退拦截] 本地仓库 ($localShort) 领先于云端 ($remoteCommitShort)，禁止用云端旧提交覆盖本地！" -ForegroundColor Red
                    Pause-IfNeeded 4
                    exit 0
                }
            }
        } catch {}
    }
    if ($localHeadSha -eq $remoteCommitSha -and $localTimeUtc -gt $remoteCommitTimeUtc) {
        Write-Host "⛔ [防降级拦截] 本地包含基于 $remoteCommitShort 的更新修改，云端未高于本地，禁止覆盖！" -ForegroundColor Red
        Pause-IfNeeded 4
        exit 0
    }
}

Write-Host "[2/2] 检测到云端有新版本 ($remoteCommitShort)，正在拉取并编译安装到本地 ..." -ForegroundColor Yellow
Get-Process -Name "ScreenboxLite" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

foreach ($item in $tree.tree) {
    if ($item.type -eq "blob") {
        if ($item.path -eq "DownloadFromGitHub.ps1" -or $item.path -eq "SyncToGitHub.ps1") { continue }
        $blob = Invoke-GitHubApi $item.url
        $bytes = [System.Convert]::FromBase64String($blob.content)
        $outPath = Join-Path $repoDir $item.path
        $outDir = Split-Path -Parent $outPath
        if ($outDir -and -not (Test-Path $outDir)) {
            New-Item -Path $outDir -ItemType Directory -Force | Out-Null
        }
        [System.IO.File]::WriteAllBytes($outPath, $bytes)
        Write-Host "      -> 已拉取文件: $($item.path) ($($bytes.Length) bytes)" -ForegroundColor Green
    }
}

$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$netDir\csc.exe"

Set-Location $repoDir
& $csc /nologo /target:winexe /optimize+ /win32icon:"ScreenboxLite.ico" /out:"ScreenboxLite.exe" `
    /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" `
    /r:"$netDir\System.Xaml.dll" /r:"$netDir\System.Windows.Forms.dll" /r:"$netDir\System.Drawing.dll" `
    "ScreenboxLite.cs"
if ($LASTEXITCODE -ne 0) { throw "ScreenboxLite.exe 编译失败！" }

& $csc /nologo /target:winexe /optimize+ /win32icon:"ScreenboxLite.ico" `
    /resource:"ScreenboxLite.exe,ScreenboxLite.exe" /out:"ScreenboxSetup.exe" `
    /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" `
    /r:"$netDir\System.Xaml.dll" /r:"$netDir\System.Windows.Forms.dll" /r:"$netDir\System.Drawing.dll" `
    "ScreenboxSetup.cs"
if ($LASTEXITCODE -ne 0) { throw "ScreenboxSetup.exe 编译失败！" }

Start-Process -FilePath "$repoDir\ScreenboxSetup.exe" -ArgumentList "/silent" -Wait

Set-Content -Path (Join-Path $repoDir "version.txt") -Value $remoteVer.ToString(3) -Encoding UTF8 -NoNewline
Set-Content -Path (Join-Path $repoDir ".last_synced_commit") -Value @($remoteCommitSha, $remoteCommitTimeUtc.ToString("o")) -Encoding UTF8

if (Test-Path "$repoDir\.git") {
    try {
        if ($token) {
            git -C $repoDir -c credential.helper= -c http.sslBackend=openssl fetch "https://${login}:${token}@github.com/${login}/${repoName}.git" main --quiet 2>$null
            git -C $repoDir reset --mixed FETCH_HEAD 2>$null
        }
    } catch {}
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ ScreenboxLite 拉取并安装完成！当前版本: v$($remoteVer.ToString(3)) ($remoteCommitShort)" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Green
Pause-IfNeeded 3
