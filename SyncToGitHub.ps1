param(
    [switch]$Force,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "ScreenboxLite - 一键编译并推送到 GitHub（带防旧版本倒灌云端保护）"

$repoDir = $PSScriptRoot
$zipPath = "F:\QQ\ScreenboxLite_Release.zip"
$login = "Boccp"
$repoName = "ScreenboxLite"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   ScreenboxLite 版本防呆检测 + 编译 + 推送到 GitHub" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "当前仓库目录: $repoDir" -ForegroundColor Gray
Write-Host ""

function Pause-IfNeeded([int]$sec = 3) {
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

Set-Location $repoDir

$appVer = "1.4.0"
if (Test-Path "$repoDir\version.txt") {
    $appVer = (Get-Content "$repoDir\version.txt" -Raw -Encoding UTF8).Trim()
}
$localVer = Parse-SemVer $appVer
$tagName = "v$($localVer.ToString(3))"

$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) { $proxy = "http://127.0.0.1:7897" }
    $tcp.Close()
} catch {}

Write-Host "[1/4] 正在读取本机 GitHub ($login) 授权凭证并执行防倒灌版本校验 ..." -ForegroundColor Yellow
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
if (-not $token) {
    throw "未找到 GitHub 授权凭证，请先完成 GitHub 登录。"
}

$headers = @{
    Authorization = "Bearer $token"
    Accept = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent" = "ScreenboxLite-SyncButton"
}
$invokeParams = @{ Headers = $headers }
if ($proxy) { $invokeParams["Proxy"] = $proxy }

$remoteCommitSha = ""
$remoteCsBlobSha = ""
$remoteVer = [version]"1.4.0.0"
try {
    $commits = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/commits?per_page=1" @invokeParams
    $remoteCommitSha = $commits[0].sha
    $tree = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/git/trees/$remoteCommitSha`?recursive=1" @invokeParams
    foreach ($item in $tree.tree) {
        if ($item.path -eq "ScreenboxLite.cs") { $remoteCsBlobSha = $item.sha }
        if ($item.path -eq "version.txt") {
            $blob = Invoke-RestMethod -Method Get -Uri $item.url @invokeParams
            $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($blob.content))
            $v = Parse-SemVer $text
            if ($v -gt $remoteVer) { $remoteVer = $v }
        }
    }
} catch {}

$localCsSha = Get-GitBlobSha "$repoDir\ScreenboxLite.cs"
$gitStatus = git status --porcelain -- "ScreenboxLite.cs" "ScreenboxSetup.cs" "README.md" "version.txt" "SyncToGitHub.ps1" "DownloadFromGitHub.ps1" "说明书_Agent接手与构建指南.md" 2>$null

if (-not $Force -and $remoteCommitSha) {
    if ($remoteVer -gt $localVer) {
        Write-Host "⛔ [防倒灌拦截] 云端版本 (v$($remoteVer.ToString(3))) 高于本地版本 (v$($localVer.ToString(3)))！禁止用旧版本强推覆盖云端！" -ForegroundColor Red
        Write-Host "   请先执行「从 GitHub 拉取更新到本地」。" -ForegroundColor Yellow
        Pause-IfNeeded 4
        exit 0
    }
    if ($localCsSha -eq $remoteCsBlobSha -and -not $gitStatus) {
        $localHead = (git rev-parse HEAD 2>$null).Trim()
        if ($localHead -eq $remoteCommitSha) {
            Write-Host "✅ [防重复推送拦截] 本地源码与 GitHub 云端 ($($remoteCommitSha.Substring(0,7))) 完全一致，无任何新改动需要推送。" -ForegroundColor Green
            Pause-IfNeeded 2
            exit 0
        }
    }
    try {
        $localHead = (git rev-parse HEAD 2>$null).Trim()
        if ($localHead -and $localHead -ne $remoteCommitSha) {
            git cat-file -e "$remoteCommitSha^{commit}" 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "⛔ [防倒灌拦截] GitHub 云端存在本地尚未拉取的新提交 ($($remoteCommitSha.Substring(0,7)))！" -ForegroundColor Red
                Write-Host "   为防止强推抹掉云端新记录，请先运行「从 GitHub 拉取更新到本地」或使用 -Force。" -ForegroundColor Yellow
                Pause-IfNeeded 4
                exit 0
            }
            git merge-base --is-ancestor $remoteCommitSha HEAD 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "⛔ [防倒灌拦截] GitHub 云端存在本地尚未合并的新提交 ($($remoteCommitSha.Substring(0,7)))！" -ForegroundColor Red
                Write-Host "   为防止强推抹掉云端新记录，请先运行「从 GitHub 拉取更新到本地」或使用 -Force。" -ForegroundColor Yellow
                Pause-IfNeeded 4
                exit 0
            }
        }
    } catch {}
}

$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$netDir\csc.exe"

Write-Host "[2/4] 正在重新编译 ScreenboxLite.exe 与 ScreenboxSetup.exe ($tagName) ..." -ForegroundColor Yellow
Get-Process -Name "ScreenboxLite" -ErrorAction SilentlyContinue | Stop-Process -Force

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
Write-Host "      -> 已编译并静默同步更新至本机安装目录。" -ForegroundColor Green

Write-Host "[3/4] 正在提交并推送到 GitHub 仓库 ($login/$repoName) ..." -ForegroundColor Yellow
git add -A
$status = git status --porcelain
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
if ($status) {
    git commit -m "Sync update ($timestamp)"
}
git tag -f $tagName

$remoteUrlWithAuth = "https://${login}:${token}@github.com/${login}/${repoName}.git"
$cleanRemoteUrl = "https://github.com/${login}/${repoName}.git"
git remote set-url origin $remoteUrlWithAuth
try {
    git -c credential.helper= -c http.sslBackend=openssl push -u origin main --force
    git -c credential.helper= -c http.sslBackend=openssl push origin $tagName --force
} finally {
    git remote set-url origin $cleanRemoteUrl
}

$newHeadSha = (git rev-parse HEAD).Trim()
Set-Content -Path (Join-Path $repoDir ".last_synced_commit") -Value @($newHeadSha, (Get-Date).ToUniversalTime().ToString("o")) -Encoding UTF8
Write-Host "      -> 代码与 Tag ($tagName) 已推送到 GitHub！" -ForegroundColor Green

Write-Host "[4/4] 正在同步更新 GitHub Release ($tagName) 安装包附件 ..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempZip = "$env:TEMP\ScreenboxLite_Release_temp.zip"
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
$zipArchive = [System.IO.Compression.ZipFile]::Open($tempZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $filesToZip = @("README.md", "version.txt", "ScreenboxLite.cs", "ScreenboxLite.exe", "ScreenboxLite.ico", "ScreenboxSetup.cs", "ScreenboxSetup.exe", "SyncToGitHub.ps1", "DownloadFromGitHub.ps1", "说明书_Agent接手与构建指南.md")
    foreach ($f in $filesToZip) {
        $fullP = Join-Path $repoDir $f
        if (Test-Path $fullP) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $fullP, $f) | Out-Null
        }
    }
} finally {
    $zipArchive.Dispose()
}
Move-Item -Path $tempZip -Destination $zipPath -Force

$release = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/releases/tags/$tagName" @invokeParams
$releaseId = $release.id
$assetsToUpload = @(
    @{ Path = "$repoDir\ScreenboxSetup.exe"; Name = "ScreenboxSetup.exe"; ContentType = "application/vnd.microsoft.portable-executable" },
    @{ Path = "$repoDir\ScreenboxLite.exe"; Name = "ScreenboxLite.exe"; ContentType = "application/vnd.microsoft.portable-executable" },
    @{ Path = $zipPath; Name = "ScreenboxLite_Release.zip"; ContentType = "application/zip" }
)

foreach ($item in $assetsToUpload) {
    $existing = $release.assets | Where-Object { $_.name -eq $item.Name }
    if ($existing) {
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$login/$repoName/releases/assets/$($existing.id)" @invokeParams | Out-Null
    }
    $uploadUri = "https://uploads.github.com/repos/$login/$repoName/releases/$releaseId/assets?name=$($item.Name)"
    Invoke-RestMethod -Method Post -Uri $uploadUri -ContentType $item.ContentType -InFile $item.Path @invokeParams | Out-Null
    Write-Host "      -> 已上传 Release 附件: $($item.Name)" -ForegroundColor Green
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ ScreenboxLite 同步上传完成！Commit: $($newHeadSha.Substring(0,7))" -ForegroundColor Green
Write-Host "    https://github.com/Boccp/ScreenboxLite" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Green
Pause-IfNeeded 3
