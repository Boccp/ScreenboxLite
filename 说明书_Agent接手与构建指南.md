# Screenbox (截图增强版 / ScreenboxLite) 架构说明书与 Agent 接手开发指南

## 一、 前身开源项目与协议声明
- **灵感与前身开源项目**：[Screenbox](https://github.com/huynhsontung/Screenbox)（基于 UWP / LibVLCSharp 的现代 Windows 媒体播放器，作者 Tung Huynh）。
- **开源许可协议**：沿用上游 **GPL-3.0 License**（及部分组件 MIT 许可）。
- **衍生版本定位**：由于上游 UWP 架构受限于 AppX 沙盒签名且无法免依赖编译为单文件 Win32 程序，本项目在保持视频播放与高频截图核心需求的基础上，采用 **纯原生 C# WPF (.NET Framework 4.x)** 重构了单文件轻量级版本 `ScreenboxLite.exe` 与自包含一键安装/覆盖更新程序 `ScreenboxSetup.exe`。

---

## 二、 核心产品设计与 UI 布局铁律（后续 Agent 修改时严禁破坏）

### 1. 三行式画框外固定布局（零画面位移 / 零比例变形）
主窗口（`MainWindow`）采用严格的 3 行 `Grid` 布局（见 `ScreenboxLite.cs` 构造函数）：
- **Row 0（固定高度 `48px`）**：顶部栏（在视频画框外部）。
  - 正常模式下显示：`打开视频` 按钮、`~键截图模式` 按钮、当前目录视频序号 `[i/N]`、视频文件名及完整路径。
  - 按下 `~` 进入截图模式时：在同一个 `48px` 容器内原位切换显示 `_topScreenshotSaveBarGrid`（默认截图保存位置栏目、更改目录、打开文件夹、操作提示），**绝对不改变 Row 0 高度**。
- **Row 1（`1*` 自适应）**：纯视频舞台 `_videoStageGrid`。
  - 内部叠加了主播放器 `_mediaElement` 与截图模式覆盖层 `_screenshotOverlayRoot`。
  - **核心铁律**：无论进入或退出 `~` 截图模式，还是弹出截图完成后的小图预览卡片，Row 1 的像素尺寸、视频长宽比（`Stretch.Uniform`）与屏幕位置**绝对不能发生哪怕 1 像素的跳动或缩放**。
- **Row 2（固定高度 `96px`）**：底部控制栏 `_bottomControlsRootGrid`（在视频画框外部）。
  - 包含高精度自定义时间轴 `_timelineInteractiveArea`、播放/暂停、上一个/下一个同目录视频、`-1帧`/`+1帧`、定点时间戳跳转、倍速选择、**`🔒 锁0` 音量彻底锁零按钮**、静音与音量条、全屏按钮。
  - 底部栏内含不占布局空间的 `_bottomOverlayCanvas`，用于悬浮显示**时间轴实时缩略图弹窗 `_timelineHoverPopup`** 以及**截图保存成功后的右下角小图预览卡片 `_screenshotPreviewBanner`**。

---

## 三、 核心功能模块构建思路与关键实现细节

### 1. 音量彻底锁零（`🔒 锁0`）与持久化记忆
- **构建思路**：用户主要使用本软件进行高频视频抽帧截图，不希望被突然播放的视频声音打扰。
- **状态变量**：
  - `_isVolumeZeroLocked`（默认 `true`）：开启时强制将音量归零并静音（`_persistedVolume = 0.0; _persistedIsMuted = true`），且锁死所有调节途径（滑块点击/拖拽、上下方向键、`M` 键静音切换全部拦截并给出 Toast 提示）。
  - 只有当用户手动点击 `🔒 锁0` 按钮切换为 `🔓 解锁` 状态后，音量才解除锁定，且从 `0%` 起点开始允许用户向上调节。
- **持久化存储**：保存在 `%LocalAppData%\ScreenboxLite\settings.cfg` 中（字段包括 `Volume`、`IsMuted`、`VolumeZeroLocked`、`LastCrop`、`SaveFolderPath`）。

### 2. 鼠标滚轮单帧进退与零延迟累加器（`_targetStepSeconds`）
- **构建思路**：鼠标滚轮已彻底解绑音量调节；滚动滚轮时**总是立即暂停画面**，并按单帧（`1/30s ≈ 0.0333s`）前后步进。
- **为什么之前会有滚轮延迟，以及如何解决的**：
  - WPF 的 `MediaElement.Position` 底层基于 WMP/EVR 异步 Seek，在 Seek 完成前读取 `_mediaElement.Position` 返回的仍是旧时间戳。如果连续滚动滚轮时每次都用 `_mediaElement.Position + delta` 计算下一帧，会导致多次滚轮事件叠加在同一个旧时间戳上被吞掉，体感严重滞后。
  - **解决方案**：引入 `_targetStepSeconds` 虚拟目标时间累加器（见 `StepFrame(int frames)` 与 `SeekToSeconds(double seconds)`）。每次滚轮或方向键步进都在 `_targetStepSeconds` 上精确累加 `1/30` 秒，并同步刷新进度条 UI 与时间戳文本，底层解码器异步追赶该目标时间，实现 0 延迟跟手体验。

### 3. 播放流畅度与时间轴缩略图无黑图设计
- **消除播放卡顿的两个关键点**：
  1. **动态切换 `ScrubbingEnabled`**：WPF `MediaElement.ScrubbingEnabled = true` 若在正常播放期间开启，会破坏 EVR 音视频时钟同步导致播放掉帧。因此代码中严格保证：**播放时 `_mediaElement.ScrubbingEnabled = false`，仅在暂停、滚轮单帧步进或按住时间轴拖动时设为 `true`**。
  2. **缩略图解码器按需延迟加载（Lazy-Init）**：时间轴悬浮预览框内的独立 `_timelineHoverMedia` 在刚打开视频时不立即启动解码，避免与主播放器争抢 GPU 硬件解码通道；仅当鼠标真正悬停到时间轴上时才按需初始化（见 `UpdateHoverThumbnailPopup`）。
- **消除时间轴缩略图黑图**：
  - WPF 的硬件视频层在 `Opacity < 0.01`、被其他不透明控件遮挡或处于 `Visibility.Collapsed` 时，显卡不会渲染纹理（用 `VisualBrush` + `RenderTargetBitmap` 抓图必出黑图）。
  - 因此 `_timelineHoverMedia` 直接作为可见子控件嵌在 `_timelineHoverPopup` 中；当悬浮框隐藏时，不使用 `Visibility.Collapsed`，而是通过 ` _timelineHoverPopup.Opacity = 0; Canvas.SetLeft(_timelineHoverPopup, -10000);` 移出屏幕视口，从而保证鼠标移上时间轴的瞬间缩略图立即可见且绝不黑屏。

### 4. `~` 键原生截图模式（跨 GUI/桌面信号接收 + 纯画面像素钳制 + 双模定框 + 边框拖拽 + `1` 键恢复）
- **物理按键直通（无视中文输入法拦截）**：
  - 在 `WndProc` 中直接监听 Win32 `WM_KEYDOWN` 的物理扫描码：`ScanCode == 0x29` 或 `VK_OEM_3 (0xC0)` 触发 `~` 键；截图模式下 `ScanCode == 0x02` 或 `VK_1 (0x31)` 触发 `1` 键。即便处于中文输入法状态（`Key.ImeProcessed`）也能 100% 瞬时响应。
- **信号范围覆盖 GUI 与桌面，但裁剪坐标严格钳制在画面像素内**：
  - 通过窗口级 `PreviewMouseDown/Move/Up` 以及 `_positionTimer` 中的 `System.Windows.Forms.Control.MousePosition` 屏幕坐标轮询，即使鼠标移到顶部/底部 GUI 栏甚至移出窗口到**桌面**，准星与选框也能持续接收信号（方便贴边起笔/收笔）。
  - 所有传入的鼠标坐标均通过 `GetDisplayedVideoRect()`（计算黑边 `Letterbox/Pillarbox` 后的纯视频显示矩形）进行 `Clamp`，确保十字准星、像素放大镜和最终 `CroppedBitmap` **绝不超出视频画面一分一毫，绝不裁进任何 GUI 或桌面像素**。
- **交互状态机（`CropSelectionMode`）**：
  1. **左键拖拽定框（`DraggingNew`）**：按住左键拖动距离 `>= 5px` 后松开，裁剪框落地（`_hasActiveSelection = true`），显示 8 个控制点手柄。
  2. **两次左键单击蓝图定框（`TwoClickBlueprint`）**：第一次左键单击（移动 `< 5px` 松开）定下起点，移动鼠标实时显示金色虚线预定框蓝图；第二次左键单击即刻落地裁剪框。
  3. **8 控制点拖动边框与内部平移（`ResizingOrMoving`）**：裁剪框落地后，可通过 `HitTestSelectionZone` 拖动四边、四角调整尺寸，或拖动内部整体平移。
  4. **按 `1` 键恢复上次裁剪框（`RestoreLastCropSelection`）**：读取 `_lastNormalizedCropRect`（归一化比例坐标 `0.0~1.0`），一键恢复上次确认裁剪的框选区域。
  5. **按 `~` 键确认裁剪保存**：无论是拖拽、两次单击还是按 `1` 键恢复的裁剪框，落地后都不会直接退出，而是等用户按 `~` 键时才执行裁剪保存并记录 `_lastNormalizedCropRect`；若当前没有任何框选直接按 `~`，则快速保存完整原画帧。
  6. **右键全流程「上一步」（`HandleRightClickBackInScreenshotMode`）**：
     - 正在拖动边框微调时按右键 → 撤销本次微调，恢复微调前的裁剪框；
     - 处于蓝图预览或已有落地裁剪框时按右键 → 取消选框，回到纯瞄准状态；
     - 处于无选框的纯瞄准状态时按右键 → 直接退出截图模式。

---

## 四、 编译构建与安装包打包方式（零外部依赖）

本项目不需要安装庞大的 Visual Studio IDE 或第三方打包工具，直接利用系统内置或 VS Build Tools 的 Roslyn `csc.exe` 即可在 1 秒内完成主程序与安装包的构建。

### 1. 代码文件清单
- `ScreenboxLite.cs`：主程序全部源码（单文件 WPF 架构）。
- `ScreenboxSetup.cs`：一键安装 / 自动识别已有安装路径覆盖更新 / 卸载程序源码。
- `ScreenboxLite.ico`：从原版 Screenbox 提取合成的多尺寸高清图标。

### 2. 一键重新编译命令（PowerShell）
后续 Agent 修改 `ScreenboxLite.cs` 或 `ScreenboxSetup.cs` 后，只需运行以下 PowerShell 脚本即可生成最新的 `ScreenboxLite.exe` 与 `ScreenboxSetup.exe`：

```powershell
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = "D:\VS2022BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "$netDir\csc.exe"
}

# 1. 编译主程序 ScreenboxLite.exe
& $csc /nologo /target:winexe /optimize+ /win32icon:"ScreenboxLite.ico" /out:"ScreenboxLite.exe" `
    /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" `
    /r:"$netDir\System.Xaml.dll" /r:"$netDir\System.Windows.Forms.dll" /r:"$netDir\System.Drawing.dll" `
    "ScreenboxLite.cs"

# 2. 将 ScreenboxLite.exe 作为嵌入式资源打包进可自选路径/自动识别覆盖更新的安装包 ScreenboxSetup.exe
& $csc /nologo /target:winexe /optimize+ /win32icon:"ScreenboxLite.ico" `
    /resource:"ScreenboxLite.exe,ScreenboxLite.exe" /out:"ScreenboxSetup.exe" `
    /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" `
    /r:"$netDir\System.Xaml.dll" /r:"$netDir\System.Windows.Forms.dll" /r:"$netDir\System.Drawing.dll" `
    "ScreenboxSetup.cs"
```

### 3. 安装包（`ScreenboxSetup.exe`）工作机制
- **自动识别已安装路径并覆盖更新**：启动时通过 `Program.DetectExistingInstallDir()` 自动检索注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenboxLite`、`Software\Classes\ScreenboxLite.Video` 以及默认安装路径 `%LocalAppData%\Programs\ScreenboxLite`。
- 若检测到已安装版本，界面自动切换为 **「🔄 立即覆盖更新」** 模式并自动填入原安装路径，点击后自动结束正在运行的旧版 `ScreenboxLite` 进程、覆盖写入新版主程序并保留用户的全部历史配置与截图文件。
- 支持命令行静默安装/静默覆盖更新：`ScreenboxSetup.exe /silent`。
- 支持命令行或系统应用列表卸载：`ScreenboxSetup.exe /uninstall`。
