# ScreenboxLite v2

专注于**边看视频边精准、舒适地找帧与框选截图**的轻量级 Windows 视频播放器与裁剪截图工具。

v2 起彻底移除 WPF `MediaElement` + 外挂 `ffmpeg` 补丁方案，改为原生嵌入 **`libmpv`（`d3d11va` 硬解 + `gpu-next` 渲染）**，无损直解 H.264 / HEVC / AV1 / VP9 全格式视频，逐帧前进平均 **0.4–0.6 ms**，原画无损截帧 **19–44 ms**，零外部 FFmpeg 依赖。

## 核心特性

- **三行固定画框外布局（48px / `*` / 96px）**：进出 `~` 截图模式、弹出缩略图与保存横幅时，视频画面 **0 像素位移、0 比例变形**。
- **亚毫秒级精准逐帧导航**：
  - 滚轮解绑音量，直接逐帧进退（`Ctrl`/`Shift` + 滚轮按秒进退）；
  - `A` / `D` 按住 5 倍速快退/快进，`S+A` / `S+D` 或 `,` / `.` 单帧进退；
  - `1` / `2` / `3` 前进 `5s / 20s / 60s`（按住 `E` 为后退）；
  - 基于 `libmpv` 异步命令序号（`CommandReply` + `PlaybackRestart` / `Pause`）确定性收敛，连滚合并、不排队滞后。
- **`~` 键原生无损截图模式**：
  - Win32 `WndProc` 物理扫描码（`0x29`）直通，无视中文输入法拦截；
  - 进入瞬间通过 `screenshot-raw video bgr0` 抓取当前帧原始分辨率无损位图冻结在舞台上，配套 **5× 像素级放大镜 + 中心像素 HEX 色值**；
  - 支持**左键拖拽定框**与**两次单击金色蓝图定框**双模式、8 控制柄微调与整体平移、`R` 键一键恢复上次归一化裁剪框、右键多级「上一步」，再按 `~` 异步保存无损 PNG（无选框时保存完整原画帧）。
- **独立 `libmpv` 软解缩略图上下文**：后台异步生成 64 锚点关键帧与悬停精确缩略图，不争抢主播放器硬解上下文、不派生任何外部子进程。
- **音量默认彻底锁零（`🔒 锁0`）**：防止高频找帧截图时突发声音打扰，解锁前拦截一切调音操作。
- **与 DshGalleryExplorer 无缝联动**：自动读取 `DSH_GALLERY_SORT_SPEC` 环境变量或 Explorer 活动窗口排序，`PageUp` / `PageDown` 按素材库一致顺序切换同目录视频。

## 目录结构

```text
ScreenboxLite/
├── build.ps1                  # 一键构建主程序 + 自包含安装包（含 --self-test）
├── tools/fetch-libmpv.ps1     # 自动下载并按 SHA256 校验锁定 libmpv-2.dll
├── lib/libmpv.lock            # libmpv 版本与 SHA256 锁文件
├── setup/ScreenboxSetup.cs    # 安装 / 覆盖升级 / 卸载向导（内嵌 GZip 压缩引擎）
├── docs/perf-baseline.md      # v1 vs v2 实测性能基线与优化报告
└── src/
    ├── Program.cs             # Per-Monitor V2 DPI、独显偏好、CLI（--self-test / --bench / --verbose）
    ├── SelfTest.cs            # 选框几何状态机、时间解析、排序协议自检
    ├── Core/                  # Log（7天滚动异步日志）、Perf（p50/p95统计）、Settings、Playlist、ScreenshotWriter
    ├── Mpv/                   # MpvNative（P/Invoke）、MpvPlayer（硬解播放与无损抓帧）、MpvThumbnailer（后台缩略图）
    └── Ui/                    # VideoHost、FrameNavigator、CropSelection、MainWindow.*.cs、Theme
```

## 构建与验证

无需安装 Visual Studio，直接调用系统内置 `.NET Framework 4.x` `csc.exe`：

```powershell
# 编译主程序 + 运行无界面自检（开发调试最快，约 0.3s）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoSetup -Test

# 完整构建 out\ScreenboxLite.exe 与自包含安装包 out\ScreenboxSetup.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test

# 对任意视频运行自动化逐帧/跳转/抓帧基准测试
.\out\ScreenboxLite.exe --bench "D:\sample.mp4" ".\bench_result.txt"
```

## 开源协议

沿用上游 [Screenbox](https://github.com/huynhsontung/Screenbox) 开源协议（GPL-3.0 License）。
