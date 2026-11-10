$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "ScreenboxLite - 检查并从 GitHub 下载更新（防旧版本降级保护）"

$repoDir = $PSScriptRoot
$login = "Boccp"
$repoName = "ScreenboxLite"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   ScreenboxLite 远端版本检测与安全更新下载器" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "本地目标目录: $repoDir" -ForegroundColor Gray
Write-Host ""

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

function Get-LocalState {
    $verCandidates = @()

    # 1. version.txt
    $verFile = Join-Path $repoDir "version.txt"
    if (Test-Path $verFile) {
        $verCandidates += Parse-SemVer (Get-Content $verFile -Raw -Encoding UTF8)
    }

    # 2. ScreenboxSetup.cs AppVersion
    $setupCs = Join-Path $repoDir "ScreenboxSetup.cs"
    if (Test-Path $setupCs) {
        $csContent = Get-Content $setupCs -Raw -Encoding UTF8
        if ($csContent -match 'AppVersion\s*=\s*"([^"]+)"') {
            $verCandidates += Parse-SemVer $matches[1]
        }
    }

    # 3. ScreenboxLite.exe FileVersion
    $exePath = Join-Path $repoDir "ScreenboxLite.exe"
    if (Test-Path $exePath) {
        try {
            $fv = (Get-Item $exePath).VersionInfo.FileVersion
            if ($fv) { $verCandidates += Parse-SemVer $fv }
        } catch {}
    }

    # 4. Registry DisplayVersion
    try {
        $regKey = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenboxLite" -ErrorAction SilentlyContinue
        if ($regKey -and $regKey.DisplayVersion) {
            $verCandidates += Parse-SemVer $regKey.DisplayVersion
        }
    } catch {}

    $maxVer = [version]"0.0.0.0"
    foreach ($v in $verCandidates) {
        if ($v -gt $maxVer) { $maxVer = $v }
    }

    # Local newest code/exe timestamp (UTC) and SHA1 of ScreenboxLite.cs
    $localTimeUtc = [datetime]::MinValue
    $localCsSha = ""
    $csPath = Join-Path $repoDir "ScreenboxLite.cs"
    if (Test-Path $csPath) {
        $localTimeUtc = (Get-Item $csPath).LastWriteTimeUtc
        # Calculate Git blob SHA1 of local ScreenboxLite.cs to compare with GitHub tree SHA
        $bytes = [System.IO.File]::ReadAllBytes($csPath)
        $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
        $combined = New-Object byte[] ($header.Length + $bytes.Length)
        [System.Array]::Copy($header, 0, $combined, 0, $header.Length)
        [System.Array]::Copy($bytes, 0, $combined, $header.Length, $bytes.Length)
        $sha1 = [System.Security.Cryptography.SHA1]::Create()
        $hashBytes = $sha1.ComputeHash($combined)
        $localCsSha = ($hashBytes | ForEach-Object { $_.ToString("x2") }) -join ""
    }

    $commitFile = Join-Path $repoDir ".last_synced_commit"
    $lastCommitSha = ""
    $lastCommitTimeUtc = [datetime]::MinValue
    if (Test-Path $commitFile) {
        $lines = Get-Content $commitFile -Encoding UTF8
        if ($lines.Count -ge 1) { $lastCommitSha = $lines[0].Trim() }
        if ($lines.Count -ge 2) {
            try { $lastCommitTimeUtc = [datetime]::Parse($lines[1].Trim()).ToUniversalTime() } catch {}
        }
    }

    return @{
        Version = $maxVer
        CsBlobSha = $localCsSha
        CsWriteTimeUtc = $localTimeUtc
        LastCommitSha = $lastCommitSha
        LastCommitTimeUtc = $lastCommitTimeUtc
    }
}

# 检测本机代理端口 7897 是否开启
$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) {
        $proxy = "http://127.0.0.1:7897"
    }
    $tcp.Close()
} catch {}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "ScreenboxLite-VersionChecker"
}

# 自动读取本机 GitHub Token（避免共享代理 IP 触发匿名 API 60次/小时 403 限流）
$token = $env:GITHUB_TOKEN
$tokenFile = Join-Path $repoDir ".github_token"
if (-not $token -and (Test-Path $tokenFile)) {
    $token = (Get-Content $tokenFile -Raw -Encoding UTF8).Trim()
}
if (-not $token) {
    $gcmCandidates = @(
        "C:\Program Files\Git\mingw64\bin\git-credential-manager.exe",
        "C:\Users\Admin\AppData\Local\hermes\tools\git-2.53.0+3-win32-x64\mingw64\bin\git-credential-manager.exe",
        "C:\Users\Administrator\AppData\Local\hermes\tools\git-2.53.0+3-win32-x64\mingw64\bin\git-credential-manager.exe"
    )
    foreach ($gcm in $gcmCandidates) {
        if (Test-Path $gcm) {
            try {
                $env:GCM_INTERACTIVE = "never"
                $credLines = "protocol=https`nhost=github.com`nusername=$login`n`n" | & $gcm get 2>&1
                foreach ($line in $credLines) {
                    if ($line -match '^password=(.+)$') { $token = $matches[1].Trim(); break }
                }
            } catch {}
            if ($token) { break }
        }
    }
}
if ($token) {
    $headers["Authorization"] = "Bearer $token"
}

function Invoke-GitHubApi([string]$Uri) {
    if ($proxy) {
        try {
            return Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers -Proxy $proxy
        } catch {
            return Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers
        }
    }
    return Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers
}

Write-Host "[1/3] 正在检测本地版本与 GitHub ($login/$repoName) 远端版本 ..." -ForegroundColor Yellow
$local = Get-LocalState
Write-Host ("      本地当前版本 : v{0}" -f $local.Version.ToString(3)) -ForegroundColor White

# 获取远端最新 Release 与最新 Commit
$remoteReleaseVer = [version]"0.0.0.0"
try {
    $latestRel = Invoke-GitHubApi "https://api.github.com/repos/$login/$repoName/releases/latest"
    if ($latestRel -and $latestRel.tag_name) {
        $remoteReleaseVer = Parse-SemVer $latestRel.tag_name
    }
} catch {}

$commits = Invoke-GitHubApi "https://api.github.com/repos/$login/$repoName/commits?per_page=1"
if (-not $commits -or $commits.Count -eq 0) {
    throw "无法获取 GitHub 远端提交记录。"
}
$headCommit = $commits[0]
$remoteCommitSha = $headCommit.sha
$remoteCommitShort = $remoteCommitSha.Substring(0, 7)
$remoteCommitTimeUtc = [datetime]::Parse($headCommit.commit.committer.date).ToUniversalTime()
$remoteCommitMsg = ($headCommit.commit.message -split "`n")[0].Trim()

# 读取远端树结构以获取远端 ScreenboxSetup.cs / version.txt 中的精确版本号
$tree = Invoke-GitHubApi "https://api.github.com/repos/$login/$repoName/git/trees/$remoteCommitSha`?recursive=1"
$remoteTreeVer = $remoteReleaseVer
$remoteCsBlobSha = ""

foreach ($item in $tree.tree) {
    if ($item.path -eq "ScreenboxLite.cs") {
        $remoteCsBlobSha = $item.sha
    }
    if ($item.path -eq "version.txt" -or $item.path -eq "ScreenboxSetup.cs") {
        $blob = Invoke-GitHubApi $item.url
        $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($blob.content))
        if ($item.path -eq "version.txt") {
            $v = Parse-SemVer $text
            if ($v -gt $remoteTreeVer) { $remoteTreeVer = $v }
        } elseif ($text -match 'AppVersion\s*=\s*"([^"]+)"') {
            $v = Parse-SemVer $matches[1]
            if ($v -gt $remoteTreeVer) { $remoteTreeVer = $v }
        }
    }
}

Write-Host ("      远端最新版本 : v{0} (Commit: {1} @ {2} UTC)" -f $remoteTreeVer.ToString(3), $remoteCommitShort, $remoteCommitTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")) -ForegroundColor Cyan
Write-Host ("      远端提交说明 : {0}" -f $remoteCommitMsg) -ForegroundColor Gray
Write-Host ""

# 核心防降级判定逻辑：
# 1. 若远端版本号 < 本地版本号 -> 严禁覆盖！
if ($remoteTreeVer -lt $local.Version) {
    Write-Host "⛔ [防降级拦截] 远端版本 (v$($remoteTreeVer.ToString(3))) 低于本地版本 (v$($local.Version.ToString(3)))！" -ForegroundColor Red
    Write-Host "   已自动阻止下载，保护本地较新的版本不被旧版覆盖。" -ForegroundColor Yellow
    Start-Sleep -Seconds 3
    exit 0
}

# 2. 若远端版本号 == 本地版本号，进一步比对源码 SHA 与修改时间
if ($remoteTreeVer -eq $local.Version) {
    if ($local.CsBlobSha -and $remoteCsBlobSha -and ($local.CsBlobSha -eq $remoteCsBlobSha)) {
        Write-Host "✅ 本地版本 (v$($local.Version.ToString(3))) 与 GitHub 远端最新源码 ($remoteCommitShort) 完全一致，无需重复下载。" -ForegroundColor Green
        Start-Sleep -Seconds 3
        exit 0
    }
    if ($local.LastCommitSha -eq $remoteCommitSha -and $local.CsWriteTimeUtc -gt $remoteCommitTimeUtc) {
        Write-Host "⛔ [防覆盖保护] 检测到您在本地基于最新提交 ($remoteCommitShort) 进行了更新的未上传修改！" -ForegroundColor Yellow
        Write-Host "   为防止远端旧快照覆盖您的本地新改动，已跳过下载（请先使用「上传」按钮同步到 GitHub）。" -ForegroundColor Yellow
        Start-Sleep -Seconds 4
        exit 0
    }
    if ($local.LastCommitTimeUtc -gt $remoteCommitTimeUtc) {
        Write-Host "⛔ [防降级拦截] 远端提交时间早于本地已同步版本，拒绝覆盖更旧的版本到本地！" -ForegroundColor Red
        Start-Sleep -Seconds 3
        exit 0
    }
}

Write-Host "[2/3] 检测到远端有更新版本 ($remoteCommitShort)，正在安全下载所有文件到本地 ..." -ForegroundColor Yellow
Get-Process -Name "ScreenboxLite" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

foreach ($item in $tree.tree) {
    if ($item.type -eq "blob") {
        # 保留本机带 Token 鉴权与防限流修复的更新器与上传脚本，防止被远端旧脚本回写覆盖
        if ($item.path -in @("DownloadFromGitHub.ps1", "SyncToGitHub.ps1")) { continue }
        $blob = Invoke-GitHubApi $item.url
        $bytes = [System.Convert]::FromBase64String($blob.content)
        $outPath = Join-Path $repoDir $item.path
        $outDir = Split-Path -Parent $outPath
        if ($outDir -and -not (Test-Path $outDir)) {
            New-Item -Path $outDir -ItemType Directory -Force | Out-Null
        }
        if ($item.path -match '\.exe$' -and (Test-Path $outPath)) {
            try {
                [System.IO.File]::WriteAllBytes($outPath, $bytes)
            } catch {
                $oldTmp = "$outPath.old"
                Remove-Item -LiteralPath $oldTmp -Force -ErrorAction SilentlyContinue
                Move-Item -LiteralPath $outPath -Destination $oldTmp -Force
                [System.IO.File]::WriteAllBytes($outPath, $bytes)
            }
        } else {
            [System.IO.File]::WriteAllBytes($outPath, $bytes)
        }
        Write-Host "      -> 已更新文件: $($item.path) ($($bytes.Length) bytes)" -ForegroundColor Green
    }
}

# 解除网络下载锁定标记，避免 SmartScreen 弹窗
Get-ChildItem -LiteralPath $repoDir -File -ErrorAction SilentlyContinue | ForEach-Object {
    Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
}

Set-Content -Path (Join-Path $repoDir "version.txt") -Value $remoteTreeVer.ToString(3) -Encoding UTF8
Set-Content -Path (Join-Path $repoDir ".last_synced_commit") -Value @($remoteCommitSha, $remoteCommitTimeUtc.ToString("o")) -Encoding UTF8

Write-Host "[3/3] 正在静默同步更新本机已安装的 ScreenboxLite ..." -ForegroundColor Yellow
$instDir = "$env:LOCALAPPDATA\Programs\ScreenboxLite"
if (-not (Test-Path $instDir)) { New-Item -Path $instDir -ItemType Directory -Force | Out-Null }
try {
    Copy-Item -LiteralPath (Join-Path $repoDir "ScreenboxLite.exe") -Destination (Join-Path $instDir "ScreenboxLite.exe") -Force
} catch {
    $setupExe = Join-Path $repoDir "ScreenboxSetup.exe"
    if (Test-Path $setupExe) {
        Start-Process -FilePath $setupExe -ArgumentList "/silent" -Wait
    }
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ 下载并更新完成！当前已是最新 v$($remoteTreeVer.ToString(3)) ($remoteCommitShort)" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Green
Start-Sleep -Seconds 3
