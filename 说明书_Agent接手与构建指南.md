# ScreenboxLite v2 架构说明书与 Agent 接手开发指南

## 一、 核心架构演进（v1 → v2）

- **v1 历史痛点**：单文件 `4,946` 行，底层依赖 WPF `MediaElement`（WMP/EVR）。因 `MediaElement` 不支持精确逐帧、不支持原生 AV1、硬件层无法直接抓图，v1 被迫叠加了大量外挂 `ffmpeg.exe` 进程补丁（AV1 整片转码 H.264、3.6 秒 MJPEG 环形管道、单帧 MJPEG 抓图、双 `MediaElement` 悬浮窗），仍然存在逐帧慢、长 GOP 卡顿、AV1 二次有损编码等问题。
- **v2 彻底重构**：拆分为 `src/` 下 20 个职责单一的 C# 模块（共约 `3,990` 行，含自检与基准测试），底层直接通过 P/Invoke 嵌入 **`libmpv-2.dll`（Client API 2.6）**：
  - 主播放器 `MpvPlayer`：`vo=gpu-next` + `gpu-api=d3d11` + `hwdec=auto-safe` + `hr-seek=yes` + `hr-seek-framedrop=yes` + `256MiB` 前后向 demuxer 缓存；
  - 缩略图器 `MpvThumbnailer`：独立 headless 软件渲染上下文（`mpv_render_context` SW 模式），零外部进程生成 64 锚点与悬停缩略图；
  - 无损抓帧：`screenshot-raw video bgr0` 直接从 `libmpv` 内存读取当前视频原始分辨率像素并冻结为 `BitmapSource`，零编码损耗。

---

## 二、 严禁破坏的产品与交互铁律

1. **三行画框外固定布局（零画面位移 / 零比例变形）**
   - `Row 0`（固定 `48px`）：正常顶栏与截图模式保存路径栏在同一个 `48px` 行内原位切换；
   - `Row 1`（`*` 自适应）：纯视频舞台（`VideoHost` + 截图冻结图层 + 十字准星/选框画布）；
   - `Row 2`（固定 `96px`）：时间轴与播放控制栏；时间轴缩略图与截图完成预览卡片一律使用独立 `Popup` 承载，绝不挤压 `Row 1` 布局。
2. **音量默认彻底锁零（`🔒 锁0`）**
   - `Settings.VolumeZeroLocked` 默认 `true`，锁定时强制静音并拦截滑块、`↑/↓`、`M` 键，防止找帧时突发声音。
   - 配置文件 `%LocalAppData%\ScreenboxLite\settings.cfg` 与 v1 键值 100% 兼容（`Volume`、`IsMuted`、`VolumeZeroLocked`、`LastCrop`、`SaveFolderPath`）。
3. **快捷键与中文输入法物理扫描码直通**
   - `MainWindow.Input.cs` 通过 `WndProc` 直接解析物理扫描码（`~`=`0x29`、`A/S/D/E/R`、`1/2/3`），中文输入法激活时同样瞬时响应：
     - `~`：进入截图模式 / 模式内确认裁剪保存（无选框时保存整帧原画）；
     - `R`：截图模式内恢复上次归一化选框（`LastCrop`）；
     - 右键 / `Esc`：截图模式逐级「上一步」（撤销拖拽微调 → 取消选框/蓝图 → 退出截图模式）；
     - 滚轮：逐帧进退（`Ctrl`/`Shift`+滚轮 `±1s`）；
     - `A` / `D`：按住 5 倍速快退/快进；`S+A` / `S+D` 或 `,` / `.`：单帧后退/前进；
     - `1` / `2` / `3`：前进 `5s / 20s / 60s`（按住 `E` 为后退）。
4. **跨窗口/桌面边缘起笔，纯视频像素钳制**
   - 截图模式下支持从顶栏、底栏甚至窗口外侧桌面边缘移入/拖拽，所有坐标经 `CropSelection` 严格钳制在去除黑边后的真实视频显示矩形内，绝不裁入黑边或 UI。

---

## 三、 关键模块说明

| 模块路径 | 职责说明 |
| :--- | :--- |
| `src/Program.cs` | Per-Monitor V2 DPI 感知、`SetDllDirectory` 查找 `libmpv-2.dll`、DirectX 高性能独显注册表偏好、`--self-test` / `--bench` / `--verbose` 入口 |
| `src/Core/Log.cs` & `Perf.cs` | 异步无锁日志（`%LocalAppData%\ScreenboxLite\logs\yyyyMMdd.log`，保留 7 天）与 `p50/p95/max` 延迟统计 |
| `src/Core/Playlist.cs` | 优先解析 `DSH_GALLERY_SORT_SPEC`（与素材库联动），其次读取 Explorer 窗口排序，兜底 `StrCmpLogicalW` 自然序 |
| `src/Mpv/MpvPlayer.cs` | `libmpv` 主播放器封装、单调递增异步命令序号 `CommandReply`、`Turbo` 100× 静音极速 `frame-step`、无损 `GrabFrame` |
| `src/Mpv/MpvThumbnailer.cs` | 第二套轻量软解 `libmpv` 实例，异步提取 64 锚点预览图与鼠标悬停精确帧 |
| `src/Ui/VideoHost.cs` | 承载 `mpv` D3D11 渲染窗口的 `HwndHost`（`WM_NCHITTEST -> HTTRANSPARENT` + 禁用子窗口输入，使鼠标滚轮/拖拽全由 WPF 接管） |
| `src/Ui/FrameNavigator.cs` | 帧导航状态机：合并密集滚轮请求、后退连击在途取代、基于命令序号与 `IsPausedNow()` 确定性收敛 |
| `src/Ui/CropSelection.cs` | 纯几何选框状态机（拖拽定框、两次单击蓝图、8 控制柄缩放平移、归一化记忆与多级撤销），可独立单元测试 |
| `setup/ScreenboxSetup.cs` | 自包含安装/覆盖更新/卸载器，内嵌 `ScreenboxLite.exe`、`libmpv-2.dll.gz` 与 `libmpv.lock`（升级时若引擎未变则跳过解压，秒级完成更新） |

---

## 四、 编译约束与构建命令

- 编译器固定使用系统内置 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（**仅支持 C# 5 语法**：禁止使用 `$""` 字符串插值、`?.` 空条件、`nameof`、`out var`、表达式体成员）。
- 构建与验证命令：
  ```powershell
  # 快速编译主程序并执行自检
  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoSetup -Test

  # 完整构建主程序与 ScreenboxSetup.exe 安装包
  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
  ```
- 若需升级 `libmpv`，更新 `tools/fetch-libmpv.ps1` 或删除 `lib/libmpv.lock` 后重新运行 `tools/fetch-libmpv.ps1` 即可自动拉取并生成新 SHA256 锁文件。
