$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "ScreenboxLite - 一键编译并同步到 GitHub"

$repoDir = $PSScriptRoot
$zipPath = Join-Path $repoDir "ScreenboxLite_Release.zip"
$proxy = "http://127.0.0.1:7897"
$login = "Boccp"
$repoName = "ScreenboxLite"
$tagName = "v1.4.0"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   ScreenboxLite 一键编译 + 本机更新 + GitHub 仓库同步" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host ""

Set-Location $repoDir

# 1. 检查源码是否变动，自动重新编译并更新本机与 Release.zip
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$netDir\csc.exe"

Write-Host "[1/4] 正在重新编译 ScreenboxLite.exe 与 ScreenboxSetup.exe ..." -ForegroundColor Yellow
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

$instDir = "$env:LOCALAPPDATA\Programs\ScreenboxLite"
if (-not (Test-Path $instDir)) { New-Item -Path $instDir -ItemType Directory -Force | Out-Null }
try {
    Copy-Item -LiteralPath "$repoDir\ScreenboxLite.exe" -Destination "$instDir\ScreenboxLite.exe" -Force
} catch {
    Start-Process -FilePath "$repoDir\ScreenboxSetup.exe" -ArgumentList "/silent" -Wait
}
Write-Host "      -> 已编译并静默同步更新至本机安装目录。" -ForegroundColor Green

# 2. 从 Git Credential Manager 提取 GitHub Token
Write-Host "[2/4] 正在读取本机 GitHub (Boccp) 授权凭证 ..." -ForegroundColor Yellow
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
if (-not $token) {
    throw "未找到 GitHub 授权凭证，请先完成 GitHub 登录。"
}

# 3. Git Commit & Push
Write-Host "[3/4] 正在提交并推送到 GitHub 仓库 ($login/$repoName) ..." -ForegroundColor Yellow
if (-not (Test-Path "$repoDir\.git")) {
    git init -b main | Out-Null
    git remote add origin "https://github.com/$login/$repoName.git" 2>$null
}
git config user.name $login
git config user.email "$login@users.noreply.github.com"
git add -A
$status = git status --porcelain
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
if ($status) {
    git commit -m "Sync update ($timestamp)"
} else {
    Write-Host "      -> 工作区无新增文件差异，强制校验远端同步状态..." -ForegroundColor Gray
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
Write-Host "      -> 代码与 Tag ($tagName) 已推送到 GitHub！" -ForegroundColor Green

# 4. 重新打包 zip 并同步更新 GitHub Release 附件
Write-Host "[4/4] 正在同步更新 GitHub Release ($tagName) 安装包附件 ..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempZip = "$env:TEMP\ScreenboxLite_Release_temp.zip"
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
$zipArchive = [System.IO.Compression.ZipFile]::Open($tempZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $filesToZip = @("README.md", "ScreenboxLite.cs", "ScreenboxLite.exe", "ScreenboxLite.ico", "ScreenboxSetup.cs", "ScreenboxSetup.exe", "说明书_Agent接手与构建指南.md")
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

$headers = @{
    Authorization = "Bearer $token"
    Accept = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent" = "ScreenboxLite-SyncButton"
}

try {
    $release = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/releases/tags/$tagName" -Headers $headers -Proxy $proxy
    $releaseId = $release.id
    $assetsToUpload = @(
        @{ Path = "$repoDir\ScreenboxSetup.exe"; Name = "ScreenboxSetup.exe"; ContentType = "application/vnd.microsoft.portable-executable" },
        @{ Path = "$repoDir\ScreenboxLite.exe"; Name = "ScreenboxLite.exe"; ContentType = "application/vnd.microsoft.portable-executable" },
        @{ Path = $zipPath; Name = "ScreenboxLite_Release.zip"; ContentType = "application/zip" }
    )

    foreach ($item in $assetsToUpload) {
        $existing = $release.assets | Where-Object { $_.name -eq $item.Name }
        if ($existing) {
            Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$login/$repoName/releases/assets/$($existing.id)" -Headers $headers -Proxy $proxy | Out-Null
        }
        $uploadUri = "https://uploads.github.com/repos/$login/$repoName/releases/$releaseId/assets?name=$($item.Name)"
        Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers -ContentType $item.ContentType -InFile $item.Path -Proxy $proxy | Out-Null
        Write-Host "      -> 已上传 Release 附件: $($item.Name)" -ForegroundColor Green
    }
} catch {
    Write-Host "      -> Release 附件更新跳过: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ 全部同步完成！GitHub 仓库与 Release 已是最新状态：" -ForegroundColor Green
Write-Host "    https://github.com/Boccp/ScreenboxLite" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Green
Start-Sleep -Seconds 3