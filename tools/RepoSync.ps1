<#
.SYNOPSIS
    两个软件的统一同步与本机部署工具（替代旧的 4 个同步脚本 + RepoSyncCenter）。

.DESCRIPTION
    使用标准 git 流程，不再强推、不再逐文件下载：
      status  查看两个仓库：本地版本、未提交改动、与 GitHub 的领先/落后提交数
      pull    拉取更新：有未提交改动时拒绝；只做快进合并（绝不覆盖本地提交），然后编译自检并部署到本机
      push    发布：编译 + 自检 → 提交 → 若云端有新提交先变基 → 普通推送（无 --force）
              → version.txt 是新版本时打 tag 并创建 GitHub Release 上传安装包/单文件程序
      deploy  仅编译自检并部署到本机（不涉及网络）

    防护靠 git 本身：pull 只快进、push 不强推，任何分叉都会停下来提示，而不是覆盖。

.EXAMPLE
    .\RepoSync.ps1                 # 交互菜单
    .\RepoSync.ps1 status          # 查看双仓库状态
    .\RepoSync.ps1 pull            # 两个都拉取并部署
    .\RepoSync.ps1 deploy          # 仅本机重新编译并部署更新
    .\RepoSync.ps1 push -Only DshGalleryExplorer -Message "更新说明"
#>
param(
    [ValidateSet('menu', 'status', 'pull', 'push', 'deploy')]
    [string]$Action = 'menu',
    [ValidateSet('All', 'ScreenboxLite', 'DshGalleryExplorer')]
    [string]$Only = 'All',
    [string]$Message,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = if ((Split-Path $PSScriptRoot -Leaf) -eq 'tools') {
    Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
} else {
    $PSScriptRoot
}
$Owner = 'Boccp'

function Resolve-RepoDir([string]$primaryName, [string]$legacyName) {
    $p1 = Join-Path $Root $primaryName
    if (Test-Path (Join-Path $p1 '.git')) { return $p1 }
    $p2 = Join-Path $Root $legacyName
    if (Test-Path (Join-Path $p2 '.git')) { return $p2 }
    return $p1
}

# ---------- 仓库定义：如何编译、产物在哪、如何部署 ----------
$Repos = @(
    @{
        Name = 'ScreenboxLite'; Title = '视频截图 ScreenboxLite'
        Dir = Resolve-RepoDir 'ScreenboxLite' 'ScreenboxLite_Release'
        Build = { param($d) & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $d 'build.ps1') -Test; if ($LASTEXITCODE -ne 0) { throw '编译/自检失败' } }
        Assets = @('out\ScreenboxSetup.exe')
        Deploy = { param($d)
            $setup = Join-Path $d 'out\ScreenboxSetup.exe'
            $p = Start-Process $setup -ArgumentList '/silent' -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "静默安装失败（退出码 $($p.ExitCode)）" }
        }
    },
    @{
        Name = 'DshGalleryExplorer'; Title = '素材库 DshGalleryExplorer'
        Dir = Resolve-RepoDir 'DshGalleryExplorer' 'DshGalleryExplorer_Release'
        Build = { param($d) & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $d 'build.ps1') -Test; if ($LASTEXITCODE -ne 0) { throw '编译/自检失败' } }
        Assets = @('out\dsh-gallery-explorer.exe')
        Deploy = { param($d)
            $exe = Join-Path $d 'out\dsh-gallery-explorer.exe'
            Get-Process -Name 'dsh-gallery-explorer' -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Milliseconds 300
            $toolsDir = Join-Path $env:USERPROFILE '.dsh\tools'
            $appDir = Join-Path $env:LOCALAPPDATA 'Programs\DshGalleryExplorer'
            foreach ($t in @($toolsDir, $appDir)) {
                New-Item -ItemType Directory -Force $t | Out-Null
                Copy-Item -Force $exe (Join-Path $t 'dsh-gallery-explorer.exe')
            }
            $desktop = [Environment]::GetFolderPath('Desktop')
            $lnk1 = Join-Path $desktop 'DSH 画师图库管理器.lnk'
            $lnk2 = Join-Path $desktop 'Files 文件管理器.lnk'
            if (-not (Test-Path $lnk1) -and -not (Test-Path $lnk2)) {
                $sh = New-Object -ComObject WScript.Shell
                $sc = $sh.CreateShortcut($lnk1)
                $sc.TargetPath = Join-Path $toolsDir 'dsh-gallery-explorer.exe'
                $sc.WorkingDirectory = $toolsDir
                $sc.Description = 'DSH 画师素材库与归档文件管理器 v2'
                $sc.Save()
            }
        }
    }
)
if ($Only -ne 'All') { $Repos = @($Repos | Where-Object { $_.Name -eq $Only }) }

function Write-Step([string]$text, [string]$color = 'Cyan') { Write-Host $text -ForegroundColor $color }

function Invoke-Git([string]$dir, [string[]]$gitArgs, [switch]$Quiet) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $dir @gitArgs 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($code -ne 0) { throw ("git {0} 失败 (exit {1}):`n{2}" -f ($gitArgs -join ' '), $code, ($out | Out-String)) }
    if (-not $Quiet) { return ($out | Out-String).Trim() }
}

function Get-Version([string]$dir) {
    $f = Join-Path $dir 'version.txt'
    if (Test-Path $f) { return (Get-Content $f -Raw).Trim() } else { return '0.0.0' }
}

function Get-RepoState($r, [switch]$Fetch) {
    $d = $r.Dir
    $s = [ordered]@{ Name = $r.Title; Version = Get-Version $d; Dirty = 0; Ahead = 0; Behind = 0; Online = $false; Head = '' }
    $s.Head = Invoke-Git $d @('rev-parse', '--short', 'HEAD')
    $s.Dirty = @((Invoke-Git $d @('status', '--porcelain')) -split "`n" | Where-Object { $_.Trim() }).Count
    if ($Fetch) {
        try { Invoke-Git $d @('fetch', '--quiet', 'origin') -Quiet; $s.Online = $true } catch { Write-Step "  [$($r.Name)] 无法连接 GitHub：$($_.Exception.Message.Split("`n")[0])" 'DarkYellow' }
    }
    $counts = Invoke-Git $d @('rev-list', '--left-right', '--count', 'HEAD...origin/main')
    $parts = $counts -split '\s+'
    $s.Ahead = [int]$parts[0]; $s.Behind = [int]$parts[1]
    return [pscustomobject]$s
}

function Show-Status {
    foreach ($r in $Repos) {
        $s = Get-RepoState $r -Fetch
        $state = if ($s.Dirty -gt 0) { "有 $($s.Dirty) 个未提交改动" }
                 elseif ($s.Ahead -gt 0 -and $s.Behind -gt 0) { "与云端分叉（本地领先 $($s.Ahead)，落后 $($s.Behind)）" }
                 elseif ($s.Behind -gt 0) { "云端有 $($s.Behind) 个新提交，可拉取" }
                 elseif ($s.Ahead -gt 0) { "本地有 $($s.Ahead) 个提交未推送" }
                 else { '与云端一致' }
        $color = if ($state -eq '与云端一致') { 'Green' } elseif ($s.Dirty -gt 0 -or $s.Ahead -gt 0) { 'Yellow' } else { 'Cyan' }
        Write-Host ("  {0,-28} v{1,-8} {2}  {3}" -f $s.Name, $s.Version, $s.Head, $state) -ForegroundColor $color
    }
}

function Invoke-Pull($r) {
    Write-Step ">>> 拉取 $($r.Title)"
    $s = Get-RepoState $r -Fetch
    if (-not $s.Online) { throw '无法连接 GitHub' }
    if ($s.Dirty -gt 0) { throw "本地有 $($s.Dirty) 个未提交改动，为避免覆盖已停止。请先推送（push）或自行处理。" }
    if ($s.Behind -eq 0) { Write-Step '  已是最新' 'Green' }
    elseif ($s.Ahead -gt 0) { throw "本地与云端已分叉（领先 $($s.Ahead)，落后 $($s.Behind)）。请先 push（会自动变基）。" }
    else {
        Invoke-Git $r.Dir @('merge', '--ff-only', 'origin/main') -Quiet
        Write-Step "  已快进到 $(Invoke-Git $r.Dir @('rev-parse','--short','HEAD'))" 'Green'
    }
    Invoke-Deploy $r
}

function Invoke-Deploy($r) {
    Write-Step "  编译 $($r.Title) ..."
    & $r.Build $r.Dir
    Write-Step '  部署到本机 ...'
    & $r.Deploy $r.Dir
    Write-Step "  ✅ $($r.Title) v$(Get-Version $r.Dir) 已部署" 'Green'
}

function Invoke-Push($r) {
    Write-Step ">>> 发布 $($r.Title)"
    $d = $r.Dir
    & $r.Build $d   # 编译/自检不过不允许发布

    $dirty = Invoke-Git $d @('status', '--porcelain')
    if ($dirty) {
        $msg = $Message
        if (-not $msg) { $msg = Read-Host '  提交说明（直接回车使用默认）' }
        if (-not $msg) { $msg = "更新 v$(Get-Version $d) ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))" }
        Invoke-Git $d @('add', '-A') -Quiet
        Invoke-Git $d @('commit', '-q', '-m', $msg) -Quiet
    }

    $s = Get-RepoState $r -Fetch
    if (-not $s.Online) { throw '无法连接 GitHub' }
    if ($s.Behind -gt 0) {
        Write-Step "  云端有 $($s.Behind) 个新提交，先变基到其后 ..."
        try { Invoke-Git $d @('rebase', 'origin/main') -Quiet }
        catch { & git -C $d rebase --abort 2>$null; throw '变基冲突，已还原。请手动合并后再发布。' }
        & $r.Build $d
    }
    if ($s.Ahead -eq 0 -and $s.Behind -eq 0 -and -not $dirty) { Write-Step '  没有需要推送的内容' 'Green' }
    else {
        Invoke-Git $d @('push', 'origin', 'HEAD:main') -Quiet
        Write-Step '  已推送 main 分支到 GitHub' 'Green'
    }
    Publish-Release $r
    & $r.Deploy $d
    Write-Step "  ✅ $($r.Title) v$(Get-Version $d) 发布并部署完成" 'Green'
}

# version.txt 对应的 tag 不存在时：打 tag、建 Release、上传安装包
function Publish-Release($r) {
    $d = $r.Dir
    $tag = 'v' + (Get-Version $d)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $remoteTag = & git -C $d ls-remote --tags origin "refs/tags/$tag" 2>$null
        $localTag = & git -C $d tag -l $tag 2>$null
        $notes = (& git -C $d log --pretty='- %s' -10 2>$null | Out-String)
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($remoteTag) { Write-Step "  $tag 已发布过（改动只进入代码仓库；要发新版请先修改 version.txt）" 'DarkGray'; return }

    if ($localTag) { Invoke-Git $d @('tag', '-d', $tag) -Quiet }
    Invoke-Git $d @('tag', $tag) -Quiet
    Invoke-Git $d @('push', 'origin', $tag) -Quiet

    $token = Get-GitHubToken
    if (-not $token) { Write-Step "  已推送 tag $tag；未取得 GitHub 凭证，请在网页上为该 tag 创建 Release 并上传 $($r.Assets -join ', ')" 'Yellow'; return }

    $h = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'RepoSync' }
    $body = @{ tag_name = $tag; name = "$($r.Name) $tag"; body = $notes } | ConvertTo-Json
    $rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Owner/$($r.Name)/releases" -Headers $h -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json' -TimeoutSec 60
    foreach ($a in $r.Assets) {
        $file = Join-Path $d $a
        $name = [IO.Path]::GetFileName($file)
        $url = ($rel.upload_url -replace '\{.*\}', '') + "?name=$name"
        Write-Step "  正在上传 Release 附件 $name ($([math]::Round((Get-Item $file).Length/1MB, 2)) MB) ..."
        Invoke-RestMethod -Method Post -Uri $url -Headers $h -InFile $file -ContentType 'application/octet-stream' -TimeoutSec 600 | Out-Null
        Write-Step "  已上传 Release 附件 $name" 'Green'
    }
}

# 复用本机 Git 凭据管理器或 gh CLI 中保存的 GitHub 登录（不在磁盘上另存 token）
function Get-GitHubToken {
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        try {
            $out = "protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null
            foreach ($l in $out) { if ($l -match '^password=(.+)$') { return $matches[1] } }
        } catch {}
        try {
            $gh = (& gh auth token 2>$null)
            if ($gh -and $gh.Trim()) { return $gh.Trim() }
        } catch {}
    } finally {
        $ErrorActionPreference = $prevEap
    }
    return $null
}

function Run([string]$act) {
    foreach ($r in $Repos) {
        try {
            switch ($act) {
                'pull'   { Invoke-Pull $r }
                'push'   { Invoke-Push $r }
                'deploy' { Invoke-Deploy $r }
            }
        } catch {
            Write-Step "  ⛔ $($r.Title)：$($_.Exception.Message)" 'Red'
            exit 1
        }
        Write-Host ''
    }
}

if ($Action -eq 'menu') {
    while ($true) {
        Clear-Host
        Write-Step '==================== 双软件仓库同步 ===================='
        Show-Status
        Write-Host ''
        Write-Host '  [1] 拉取并部署两个软件     [2] 发布两个软件到 GitHub'
        Write-Host '  [3] 仅拉取截图软件         [4] 仅拉取素材库'
        Write-Host '  [5] 仅发布截图软件         [6] 仅发布素材库'
        Write-Host '  [7] 仅本机编译部署（不联网）   [Q] 退出'
        $c = (Read-Host '选择').Trim().ToUpperInvariant()
        $all = $Repos
        switch ($c) {
            '1' { Run 'pull' }
            '2' { Run 'push' }
            '3' { $Repos = @($all | Where-Object Name -eq 'ScreenboxLite'); Run 'pull'; $Repos = $all }
            '4' { $Repos = @($all | Where-Object Name -eq 'DshGalleryExplorer'); Run 'pull'; $Repos = $all }
            '5' { $Repos = @($all | Where-Object Name -eq 'ScreenboxLite'); Run 'push'; $Repos = $all }
            '6' { $Repos = @($all | Where-Object Name -eq 'DshGalleryExplorer'); Run 'push'; $Repos = $all }
            '7' { Run 'deploy' }
            'Q' { exit 0 }
        }
        Read-Host '回车返回菜单' | Out-Null
    }
}
elseif ($Action -eq 'status') { Show-Status }
else { Run $Action }
if (-not $NoPause -and $Action -ne 'status') { Read-Host '完成，回车关闭' | Out-Null }
