# ScreenboxLite 构建记录与验证台账

本文档记录每次重要版本的构建环境、依赖锁定、编译产物、自动化自检以及真实视频基准验证结果，便于后续维护与跨设备复现。

---

## v2.0.1 构建记录（2026-11-16）

### 1. 关键修复：彻底解决原生子窗口与跳转框抢占 Win32 键盘焦点导致全快捷键失效
- **故障现象**：v2.0.0 在真实交互中出现 `~`、`Space`、`A/D`、`S+A/D`、`1/2/3`、`F`、`Esc` 等键盘快捷键整体失效，仅鼠标滚轮与进度条点击可用。
- **根因定位（四层耦合焦点漏洞）**：
  1. `libmpv`（`force-window=yes` + `vo=gpu-next`）在后台 VO 线程异步创建原生子 HWND；旧版 `DisableChildInput()` 在 `Initialize()` 后 `0.1 ms` 同步执行时子窗口尚未诞生，随后点击画面命中 `mpv` 子窗口并在其线程触发 `SetFocus(mpvHwnd)`，将所有 `WM_KEYDOWN` 导向 `mpv` 线程丢弃（而 `WM_MOUSEWHEEL` 仍经由 `DefWindowProc` 冒泡回父窗口）。
  2. WPF `HwndHost` 默认 `Focusable = true` 且实现 `IKeyboardInputSink`；进出 `~` 截图模式切换 `_videoHost.Visibility`（`ShowWindow(SW_HIDE)`）会导致 Win32 `GetFocus()` 置 `NULL`。
  3. 旧版 `FocusMain()` 仅调用 WPF `Focus(); Keyboard.Focus(this)`；当 WPF 逻辑焦点仍在 `MainWindow` 时属于空操作，不会调用 Win32 `user32!SetFocus(mainHwnd)` 收复原生焦点。
  4. 底栏 `_jumpBox`（定点跳转框）是视觉树中唯一可聚焦子控件（`IsTabStop = true`），WPF 自动焦点遍历或点选后点击无焦点按钮/时间轴无法释放输入态，导致快捷键被输入态屏蔽。
- **修复架构（纵深防御）**：
  - **`VideoHost.cs` + `app.manifest`**：宿主 HWND 创建即带 `WS_DISABLED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`，拦截 `WM_PARENTNOTIFY(WM_CREATE)` 在 `mpv` 子窗口创建瞬间禁用并附加穿透样式，关闭 `HwndHost` 全部 `IKeyboardInputSink` 入口，并在 `WM_SETFOCUS` / `WM_KEY*` 上兜底归还根窗口。
  - **`MainWindow.Input.cs`**：`FocusMain()` 同时收复 Win32 `SetFocus`（含跨线程 `GetGUIThreadInfo` + `AttachThreadInput` 恢复）、通过 `ImmAssociateContext(hwnd, 0)` 屏蔽中文输入法候选框干扰、挂载 `ComponentDispatcher.ThreadPreprocessMessage` + `WndProc` 统一按**物理扫描码 + 虚拟键码**处理全量快捷键（含 `Space/K`、`,/.`、`A/D`、`S+A/D`、`1/2/3`、`E+1/2/3`、`~`、`R`、`F/F11`、`Esc`、`Enter`），并在 `WndProc` 直接处理舞台单击暂停与双击无损全屏。
  - **`_jumpBox` 按需聚焦**：默认 `Focusable = false, IsTabStop = false`，仅在鼠标点入 `_jumpBox` 时开启编辑，按 `Enter`/`Esc` 或点击窗口其余任意位置（含视频画面、时间轴、按钮）立即解除并归还主窗口焦点，辅以 `200 ms` 前台焦点自愈守卫。

### 2. 构建产物与自动化真窗自检（`--self-test` & `--self-test-ui`）
- **构建命令**：
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
  .\out\ScreenboxLite.exe --self-test-ui "<测试视频.mp4>"
  ```
- **验证结果（全部 `exit 0`）**：
  - `VideoHost` 与 `mpv` 异步渲染子窗口 `100%` 处于禁用输入态，`WindowFromPoint` 与 `ChildWindowFromPointEx` 在播放画面中心 `100%` 命中 `MainWindow` HWND；
  - `d3d11` 硬解画面在 `WS_DISABLED | WS_EX_TRANSPARENT` 宿主下正常呈现（屏幕采样非黑屏）且 `GrabFrame()` 原画抓帧正常；
  - 模拟点入 `_jumpBox` 后点击播放中的视频画面：立即解除输入态、收复 Win32 `GetFocus()` 并触发单击暂停；
  - 模拟消息队列链路及中文输入法 `VK_PROCESSKEY (0xE5)` + 硬件扫描码链路：`Space`、`D` 长按快进、`.` / `,` 逐帧、`S+D` 逐帧、`1` / `E+1` 秒跳、`F` 全屏、`~` 进退截图模式及 `Esc` 返回全部 `100%` 通过。

---

## v2.0.0 构建记录（2026-11-14）

### 1. 构建环境与工具链
- **操作系统**：Windows x64（24 逻辑 CPU）
- **编译器**：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（C# 5，`/optimize+ /warn:4 /platform:x64`）
- **原生解码引擎锁（`lib/libmpv.lock`）**：
  - 包名：`mpv-dev-x86_64-20261111-git-bcde6b93cc.7z`（Client API `2.6`）
  - SHA256：`88504ee5560942a955405e99dfb5971368e4cf851321f36565bf2a667271db1b`
- **构建命令**：
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
  ```

### 2. 构建产物与自检结果
| 产物路径 | 大小 | 编译耗时 | 编译警告 | `--self-test` 结果 |
| :--- | ---: | ---: | ---: | :--- |
| `out\ScreenboxLite.exe` | `149.0 KB` | `~0.3 s` | `0` | **PASS (`exit 0`)** |
| `out\ScreenboxSetup.exe` | `45.4 MB`（内嵌 `ScreenboxLite.exe` + GZip 压缩 `libmpv-2.dll` + 锁文件） | `~0.8 s`（总计） | `0` | 安装器 `/silent` 幂等覆盖验证通过 |

**`--self-test` 覆盖项**：
1. `CropSelection` 选框几何状态机（左键拖拽定框、两次单击蓝图定框、越界钳制、8 控制柄缩放、归一化 `LastCrop` 恢复、多级右键撤销）。
2. 时间戳格式化与解析边界测试。
3. `DSH_GALLERY_SORT_SPEC` 素材库排序协议解析与自然序播放列表一致性验证。
4. `Settings` 配置文件读写往返兼容性（含默认 `VolumeZeroLocked = true`）。

### 3. 四种编码规格 `--bench` 实测汇总（v1 基线 vs v2.0.0）
测试样本覆盖：`480p24 H.264`、`1080p30 H.264 (GOP=250)`、`1080p60 AV1 (GOP=600)`、`4K60 HEVC (GOP=600)`。详细分位数见 [perf-baseline.md](perf-baseline.md)。

- **文件打开首帧就绪**：
  - `1080p60 AV1`：从 v1 `9,754.8 ms`（整片有损转码 H.264）降至 **`9.5 ms`**（原生 `d3d11va` 硬解直开，**提速 1026×**，零画质损失）。
  - `4K60 HEVC`：**`12.6 ms`**。
- **`~` 键全分辨率无损抓帧 (`GrabFrame` `bgr0`)**：
  - `1080p30 H.264`：`224.0 ms` → **`19.0 ms`**（**11.8×**）
  - `1080p60 AV1`：`375.7 ms` → **`19.7 ms`**（**19.1×**）
  - `4K60 HEVC`：`1,273.7 ms` → **`44.1 ms`**（**28.9×**）
- **单帧步进与滚轮连滚精度**：
  - 前进单帧（`Turbo` 模式）：平均 **`0.4–0.6 ms`**（`p50 = 0.3–0.4 ms`），`480p H.264` / `1080p60 AV1` / `4K60 HEVC` 连续 100 步命中率 `100/100`，零超时卡死。
  - 滚轮突发连滚（`+8` 帧合并步进）：基于 `mpv_command_async` 单调序号与 `IsPausedNow()` 确定性收敛，8 帧精准落位不丢帧。
