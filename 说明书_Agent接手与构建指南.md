# ScreenboxLite v2 架构说明书与 Agent 接手开发指南

## 一、 核心架构演进（v1 → v2.0.7）

- **v1 历史痛点**：单文件 `4,946` 行，底层依赖 WPF `MediaElement`（WMP/EVR）。因 `MediaElement` 不支持精确逐帧、不支持原生 AV1、硬件层无法直接抓图，v1 被迫叠加了大量外挂 `ffmpeg.exe` 进程补丁（AV1 整片转码 H.264、3.6 秒 MJPEG 环形管道、单帧 MJPEG 抓图、双 `MediaElement` 悬浮窗），仍然存在逐帧慢、长 GOP 卡顿、AV1 二次有损编码等问题。
- **v2 彻底重构与演进**：拆分为 `src/` 下 22 个职责单一的模块，底层直接通过 P/Invoke 嵌入 **`libmpv-2.dll`（Client API 2.6）**：
  - **主播放器 `MpvPlayer`**：`vo=gpu-next` + `gpu-api=d3d11` + `hwdec=auto-safe` + `hwdec-extra-frames=48`（防高频回退/抓帧表面耗尽）+ `hr-seek=yes` + `hr-seek-framedrop=yes` + `256MiB` 前后向 demuxer 缓存 + GLSL `HOOK MAIN` 零拷贝水平镜像着色器；
  - **缩略图器 `MpvThumbnailer`**：独立 headless 软件渲染上下文（`mpv_render_context` SW 模式），`0.08s` 时间桶高密度按需提取 + 64 关键帧锚点瞬时兜底；
  - **无损抓帧与单飞节流**：`screenshot-raw video bgr0` 直接从 `libmpv` 内存读取当前视频原始分辨率像素并冻结为 `BitmapSource`，配合单飞合并机制避免连续滚帧时并发 GPU 回读阻塞；
  - **现代暗色工作室界面 `Theme`**：纯 C# 代码构建圆角按钮、向上弹出暗色 `ComboBox`、暗色细滚动条与全套 `16×16` 像素对齐矢量几何图标，配合双行时间/帧号读数与多档宽度自适应折叠，彻底消除控件挤压遮挡与字体乱码。

---

## 二、 严禁破坏的产品与交互铁律

1. **三行画框外固定布局（零画面位移 / 零比例变形）**
   - `Row 0`（固定 `48px`）：正常顶栏与截图模式工具栏在同一个 `48px` 行内原位切换；
   - `Row 1`（`*` 自适应）：纯视频舞台（`VideoHost` + 截图冻结图层 + 十字准星/选框画布）；
   - `Row 2`（固定 `96px`）：时间轴与模块化控制栏；时间轴悬停缩略图与「历史截图队列」抽屉均使用独立无焦点 `Popup` 承载，绝不改变 `Row 1` 高度与位置；从 `820px` 最小宽度到 `4K` 全屏严禁出现控件重叠遮挡（由 `--self-test` 包围盒测试守卫）。
2. **原生子窗口零抢焦点 + 全快捷键中文输入法物理扫描码直通**
   - `VideoHost` 创建即携带 `WS_DISABLED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` 并拦截 `WM_PARENTNOTIFY(WM_CREATE)` 即时锁死 `mpv` 子窗口输入；`_jumpBox` 默认 `Focusable = false`，仅鼠标点入时开启编辑，点击外部或按 `Esc`/`Enter` 立即调用 `FocusMain()` 同步收复 Win32 `SetFocus` 与 WPF 焦点。
   - `MainWindow.Input.cs` 通过 `ThreadPreprocessMessage` + `WndProc` 统一解析物理扫描码 + 虚拟键码（并通过 `ImmAssociateContext` 屏蔽中文输入法候选条干扰）：
     - `~`：进入截图模式 / 模式内确认裁剪保存（无选框时保存整帧原画）；
     - `Tab`：**全局水平镜像翻转**（普通播放/暂停与 `~` 截图模式均可即时启用，**保存截图或退出 `~` 模式后保持翻转不复原**，切换视频文件或关闭软件时自动重置）；
     - `R`：截图模式内恢复上次归一化选框（`LastCrop`）；
     - 右键 / `Esc`：截图模式逐级「上一步」（撤销拖拽微调 → 取消选框/蓝图 → 退出截图模式）及收起历史截图抽屉；
     - 滚轮：逐帧进退（`Ctrl`/`Shift`+滚轮 `±1s`）；
     - `A` / `D`：按住 5 倍速快退/快进；`S+A` / `S+D` 或 `,` / `.`：单帧后退/前进；
     - `1` / `2` / `3`：前进 `5s / 20s / 60s`（按住 `E` 为后退）。
3. **音量默认彻底锁零（`🔒 锁0`）**
   - `Settings.VolumeZeroLocked` 默认 `true`，锁定时强制静音并拦截滑块、`↑/↓`、`M` 键，防止找帧时突发声音。
4. **全屏防误点护盾与跨边缘纯视频像素钳制**
   - 截图模式下挂载紧贴主窗口正后方 Z 序的 `WS_EX_NOACTIVATE` 全屏透明护盾层并拦截窗口缩放边框，支持从顶底栏或屏幕外部桌面安心起笔拖拽，所有坐标经 `CropSelection` 严格钳制在纯视频有效区域内。

---

## 三、 关键模块说明

| 模块路径 | 职责说明 |
| :--- | :--- |
| `src/Program.cs` & `app.manifest` | Per-Monitor V2 DPI、Win10/11 穿透兼容清单、独显偏好、`--self-test` / `--self-test-ui` / `--ui-snapshot` / `--bench` 入口 |
| `src/SelfTest.cs` | 自动化回归套件：选框状态机、排序协议、真窗 Win32 焦点/输入法路由、历史队列删除恢复、五档宽度零遮挡、全局 `Tab` 镜像像素闭环 |
| `src/Core/Playlist.cs` | 优先解析素材库 `DSH_GALLERY_PLAYLIST_FILE` 与 `DSH_GALLERY_SORT_SPEC`，其次跟随 Explorer 窗口排序，兜底 `StrCmpLogicalW` 自然序 |
| `src/Core/ScreenshotWriter.cs` | 归一化无损裁剪、逐行水平像素镜像、`640px` 预览缩略图生成、异步 PNG 落盘与 `SaveSync` 同步锁 |
| `src/Mpv/MpvPlayer.cs` | `libmpv` 主播放器封装、`hwdec-extra-frames=48`、GLSL 镜像着色器、单帧零改速与多帧 `12x` 突发步进、回退后恢复播放 PTS 自愈对齐 |
| `src/Mpv/MpvThumbnailer.cs` | 第二套轻量软解 `libmpv` 实例，异步提取 64 关键帧锚点与 `0.08s` 密度悬停精确帧 |
| `src/Ui/VideoHost.cs` | 承载 `mpv` D3D11 渲染窗口的禁焦穿透 `HwndHost`，确保点击、双击与快捷键 100% 落在主窗口 |
| `src/Ui/FrameNavigator.cs` | 帧导航状态机：后退/跳转单飞合并、`CancelForPlay()` 播放前队列清空、基于命令序号确定性收敛 |
| `src/Ui/Theme.cs` | 暗色工作室调色板、纯代码圆角按钮/暗色下拉框/细滚动条 `ControlTemplate` 与 `16×16` 矢量几何图标库 |
| `src/Ui/MainWindow.*.cs` | 三段式主界面、输入焦点中枢、播放控制、无损截图覆盖层（单飞抓帧刷新）与历史截图队列抽屉（单点删除与原位恢复） |
| `setup/ScreenboxSetup.cs` | 自包含安装/覆盖更新/卸载器，内嵌 `ScreenboxLite.exe`、`libmpv-2.dll.gz` 与 `libmpv.lock` |

---

## 四、 编译约束与构建同步命令

- 编译器固定使用系统内置 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（**仅支持 C# 5 语法**：禁止使用 `$""`、`?.`、`nameof`、`out var`、表达式体成员）。
- 完整演进构建记录见 [docs/BUILD_LOG.md](docs/BUILD_LOG.md)。
- 常用命令：
  ```powershell
  # 快速编译主程序并执行全套自动化自检
  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoSetup -Test

  # 完整构建主程序与安装包 + 本机部署 / 推送发布到 GitHub
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 deploy -Only ScreenboxLite -NoPause
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RepoSync.ps1 push -Only ScreenboxLite -Message "更新说明" -NoPause
  ```
