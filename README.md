# ScreenboxLite v2.0

专注于**边看视频边精准、舒适地找帧与框选截图**的轻量级 Windows 视频播放器与无损裁剪截图工具。

v2.0 彻底移除旧版 WPF `MediaElement` + 外挂 `ffmpeg` 进程补丁方案，改为原生嵌入 **`libmpv`（`d3d11va` 硬解 + `gpu-next` 渲染）**，无损直解 H.264 / HEVC / AV1 / VP9 全格式视频，逐帧前进平均 **0.4–0.6 ms**，原画无损截帧 **19–44 ms**，零外部 FFmpeg 依赖。

- 构建与验证记录：[docs/BUILD_LOG.md](docs/BUILD_LOG.md)
- 性能基线与实测报告：[docs/perf-baseline.md](docs/perf-baseline.md)
- 架构与开发规范：[说明书_Agent接手与构建指南.md](说明书_Agent接手与构建指南.md)

---

## 一、 安装与升级方法

### 方式 A：直接运行安装包（推荐）
1. 在 GitHub Releases 下载 `ScreenboxSetup.exe`（已内嵌主程序与压缩的 `libmpv-2.dll` 引擎）。
2. 双击运行安装向导（或命令行执行 `ScreenboxSetup.exe /silent` 静默安装/覆盖升级）。
3. 程序默认安装到 `%LocalAppData%\Programs\ScreenboxLite\ScreenboxLite.exe`，自动创建桌面快捷方式 `Screenbox (截图增强版)` 并关联常见视频格式（`.mp4 / .mkv / .webm / .mov / .avi` 等）。

### 方式 B：从源码一键编译部署
```powershell
# 仅编译主程序与安装包并运行自检
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test

# 编译并静默部署到本机（使用统一同步脚本）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 deploy -Only ScreenboxLite -NoPause
```

---

## 二、 完整使用方法与快捷键速查

### 1. 播放与精准找帧快捷键

> 所有核心按键通过 Win32 物理扫描码直通，**即使处于中文输入法状态也能零延迟响应**。

| 操作 / 快捷键 | 功能说明 |
| :--- | :--- |
| **`Space`（空格）** / 单击画面 | 播放 / 暂停切换 |
| **鼠标滚轮上 / 下** | **逐帧后退 / 逐帧前进**（已与音量彻底解绑，高速连滚自动合并，绝不滞后） |
| **`Ctrl` 或 `Shift` + 滚轮** | 按秒快速后退 / 前进（`±1s`） |
| **`S + A` / `S + D`**（或 **`,` / `.`**） | 单帧后退 1 帧 / 单帧前进 1 帧 |
| **按住 `A` / 按住 `D`** | 长按期间 **5 倍速** 流畅快退 / 快进，松开即恢复原状 |
| **`1` / `2` / `3`** | 向前跳转 **`+5s` / `+20s` / `+60s`** |
| **按住 `E` + `1` / `2` / `3`** | 向后跳转 **`-5s` / `-20s` / `-60s`** |
| **`←` / `→` 方向键** | 微幅后退 / 前进（`±2s`） |
| **`PageUp` / `PageDown`** | 切换同文件夹上/下一个视频（自动继承 `DshGalleryExplorer` 或资源管理器排序） |
| **双击视频画面 / `F11`** | 全屏 / 窗口切换（三行画框外固定布局，进出截图模式画面 **0 位移**） |
| **底部进度条悬停 / 拖拽** | 实时显示独立软解引擎提取的时间点缩略图并精准定位 |

### 2. `~` 键原生无损框选截图工作流

1. **进入截图模式**：播放或暂停任意时刻按左上角 **`~` 键**（`Esc` 下方波浪号键），瞬间抓取当前视频帧**原始分辨率无损位图**冻结在画面上，右上角跟随鼠标显示 **5× 像素级放大镜 + 中心像素 HEX 色值**，顶栏原位切换为截图保存目录栏（可点击修改保存路径）。
2. **两种定框方式（自由混用）**：
   - **拖拽框选**：按住鼠标左键拖出裁剪框（支持从画面外黑边/顶底栏甚至桌面边缘起笔，自动钳制到纯视频有效区域内）。
   - **两次单击蓝图定框**：单击第 1 下定左上/起始锚点（显示金色十字蓝图预览），移动鼠标对齐边缘后再单击第 2 下精确锁定对角点。
3. **微调与记忆恢复**：
   - 拖动选框内部整体平移，拖动四周 **8 个控制柄** 像素级微调边界；
   - 按 **`R` 键** 一键恢复上一次使用过的归一化裁剪框（适合连截同一视频固定区域/字幕/立绘）；
   - 按 **鼠标右键** 或 **`Esc`** 执行多级「上一步」（撤销刚做的拖拽调整 → 取消当前选框 → 退出截图模式）。
4. **保存导出**：
   - 再次按 **`~` 键**（或回车）：若已画框则按原始视频像素比例无损裁剪保存 PNG 并复制到剪贴板；**若未画选框直接再按 `~`，则直接保存整张完整原画帧**。
   - 右下角浮现不遮挡画面的缩略图通知卡片，可单击直接打开截图或所在目录。

### 3. 音量「锁0」防打扰机制
- 顶栏默认开启 **`🔒 锁0`**（状态持久化在配置文件中）：锁定时强制静音 `Volume = 0`，并屏蔽 `↑/↓`、`M` 键与音量条误触，避免深夜或高频找帧时突然发出声音。需要听声音时点击顶栏按钮切换为解锁即可。

### 4. 配置、缓存与日志目录
- 配置文件：`%LocalAppData%\ScreenboxLite\settings.cfg`（记录默认保存目录、上次归一化选框 `LastCrop`、音量与 `VolumeZeroLocked` 状态）。
- 运行日志：`%LocalAppData%\ScreenboxLite\logs\yyyyMMdd.log`（异步无锁写入，自动保留最近 7 天；启动加 `--verbose` 开启调试日志）。

---

## 三、 双机 / GitHub 同步工具（`tools/RepoSync.ps1`）

```powershell
# 查看本地版本、未提交改动及与 GitHub 的同步状态
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 status

# 从 GitHub 快进拉取最新代码并自动编译、自检、部署到本机
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 pull -Only ScreenboxLite

# 编译 + 自检通过后推送到 GitHub（若 version.txt 升级则自动打 Tag 并上传 ScreenboxSetup.exe 到 Release）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 push -Only ScreenboxLite -Message "更新说明"
```

---

## 四、 编译命令与目录结构

无需安装 Visual Studio，仅使用 Windows 自带 `.NET Framework 4.x` 的 `csc.exe`（C# 5）：

```powershell
# 1. 快速编译主程序并执行无界面自动化自检（约 0.3s）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoSetup -Test

# 2. 完整编译主程序 + ScreenboxSetup.exe 单文件安装包 + 自检（约 0.8s）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test

# 3. 运行自动化性能基准测试（测试打开、逐帧、跳转与无损抓帧延迟）
.\out\ScreenboxLite.exe --bench "D:\test.mp4" ".\bench_report.txt"
```

```text
ScreenboxLite/
├── build.ps1                  # 一键构建主程序 + 自包含安装包（含 -Test 自检）
├── version.txt                # 当前语义化版本号 (2.0.0)
├── tools/
│   ├── fetch-libmpv.ps1       # 自动下载并按 SHA256 校验锁定 libmpv-2.dll
│   └── RepoSync.ps1           # 统一 Git 同步、Release 发布与本机部署工具
├── lib/libmpv.lock            # libmpv 引擎版本与 SHA256 锁文件
├── setup/ScreenboxSetup.cs    # 安装 / 覆盖升级 / 卸载向导源码
├── docs/
│   ├── BUILD_LOG.md           # v2.0.0 构建记录与自检/基准验证台账
│   └── perf-baseline.md       # v1 vs v2 实测性能对比报告
└── src/
    ├── Program.cs             # Per-Monitor V2 DPI、独显偏好、CLI 分发
    ├── Core/                  # Log、Perf、Settings、Playlist、ScreenshotWriter、SelfTest
    ├── Mpv/                   # MpvNative、MpvPlayer（硬解与无损抓帧）、MpvThumbnailer（软解缩略图）
    └── Ui/                    # VideoHost、FrameNavigator、CropSelection、MainWindow.*.cs、Theme
```

## 开源协议

沿用上游 [Screenbox](https://github.com/huynhsontung/Screenbox) 开源协议（GPL-3.0 License）。
