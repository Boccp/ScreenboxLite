# ScreenboxLite 构建记录与验证台账

本文档记录每次重要版本的构建环境、依赖锁定、编译产物、自动化自检以及真实视频基准验证结果，便于后续维护与跨设备复现。

---

## v2.0.7 构建记录（2026-11-18）

### 1. 全局 `Tab` 水平镜像持久化与高频滚帧/截图后恢复播放卡顿乱序修复
- **全局 `Tab` 水平镜像生命周期升级**（`MpvPlayer.cs`、`MainWindow.Screenshot.cs`、`MainWindow.Input.cs`、`MainWindow.cs`）：
  1. 无需先进入 `~` 截图模式，在普通播放或暂停状态按 `Tab`（或点击顶栏新增的「镜像 (Tab)」指示按钮）即可通过 `vo=gpu-next` GLSL 着色器实现 **GPU 零拷贝即时水平翻转**（完整保留 `d3d11va` 硬解，零重解码延迟）。
  2. 进入 `~` 截图模式自动继承当前翻转状态，保存截图或退出 `~` 模式后**保持翻转不自动复原**，直到**切换视频文件或关闭软件**时才自动重置为原始方向。
- **彻底解决多次暂停/滚帧/截图后恢复播放卡顿与画面快速乱序跳变**：
  1. **硬解表面池扩容**（`MpvPlayer.cs`）：配置 `hwdec-extra-frames=48`（默认仅 `6`），杜绝反复 `frame-back-step` 缓存帧与 `screenshot-raw` 抓帧耗尽 `d3d11va` 硬解纹理切片池导致的解码阻塞与参考帧错乱。
  2. **消除 `100x` 倍速冲击与导航残留**（`MpvPlayer.cs`、`FrameNavigator.cs`、`MainWindow.Playback.cs`）：单帧步进不再改动播放 `speed`（避免音频重采样器与 `ao=wasapi` 时钟失步追帧），多帧突发连滚限速为温和的 `12x`；后退连滚改为单飞合并（杜绝向硬解器并发狂发数十次 flush/seek）；恢复正常播放前强制执行 `CancelForPlay()` 清空在途导航队列，并在回退步进/突发连滚后自动重对齐音视频 PTS、释放 `vo->backstep` 缓存表面。
  3. **截图抓帧严格单飞节流**（`MainWindow.Screenshot.cs`）：修复 `RefreshFrozenFrameAsync` 在连续滚轮时并发派发多个 `GrabFrame()` 线程及 `_grabInFlight` 偶发死锁的问题，确保同一时刻最多 `1` 个 GPU 回读在途。
- **自动化验证**（`SelfTest.cs`）：新增内存合成左红右蓝非对称帧的端到端测试，覆盖非 `~` 模式下 `Tab` 启用、进 `~` 模式像素方向校验、保存后保持翻转、落盘 PNG 像素校验、切文件自动重置，全部通过（`exit 0`）。
- **发布工具链加固**（`tools/RepoSync.ps1`）：`Publish-Release` 新增远端 Release 附件完整性校验（若已推 tag 但大体积安装包上传中途断连缺失，重跑 `push` 会自动清理残缺附件并补传）及 `System32\curl.exe` HTTP/1.1 多重重试流式上传（避免 `.NET 4` `Invoke-RestMethod` 上传 `45.5 MB` 安装包偶发 TLS 连接重置）。

---

## v2.0.6 构建记录（2026-11-17）

### 1. 关键改进：重构全界面视觉层级并彻底消除控制栏挤压遮挡
- **问题定位**：
  1. `v2.0.5` 新增底栏「历史截图」按钮后，底栏横向元素总宽达 `~1,469 px`，在 `1280 px` 默认窗口与 `960 px` 分屏宽度下超过容器宽度，导致中央「定点跳转框 / 跳转按钮」与左侧帧号、右侧历史截图按钮发生重叠遮挡；
  2. 倍速 `ComboBox`、按钮悬停高亮、历史队列滚动条沿用系统默认白框/浅蓝 Aero 样式，且 `🔇`、`⛶` 等 Unicode 字符存在字体回退乱码及滑块圆点越界压线，界面层级模糊。
- **改进方案**：
  1. **模块化分组与双行读数布局**（`MainWindow.cs`、`MainWindow.Playback.cs`）：播放切集、单帧步进、定点跳转、音量锁零分别收拢为等高胶囊模块；时间码与帧号改为纵向双行高对比度读数（节省 `~135 px` 横向空间且帧号常驻可读）；新增 `1150 px / 980 px / 930 px` 多档自适应折叠与 `ClipToBounds` 边界保护。
  2. **纯代码暗色控件模板与全矢量图标体系**（`Theme.cs`、`MainWindow.History.cs`、`MainWindow.Screenshot.cs`）：全面替换 Emoji 字符为 `16×16` 像素对齐矢量几何图标；定制暗色圆角按钮、向上弹出的暗色倍速下拉框、暗色细滚动条与内缩防越界音量滑轨；截图模式顶栏新增实时「镜像 (Tab)」状态指示按钮与「上次框 (R)」快捷按钮，并联动降低底栏透明度以明确交互焦点。
  3. **多宽度零遮挡自动化自检**（`SelfTest.cs`）：在 `--self-test` 中新增 `1440 / 1280 / 1100 / 960 / 820 px` 五档窗口宽度的真实包围盒碰撞断言，确保顶栏与底栏在任何宽度下 `0 px` 遮挡。

---

## v2.0.5 构建记录（2026-11-16，提交 `b260412`）

### 1. 底栏历史截图队列抽屉与原位误删恢复
- **背景与目标**：移除原先保存截图后自动弹出、易遮挡底栏「上一集/下一集」按钮的悬浮卡片弹窗，改为按需展开的大尺寸高清历史队列抽屉。
- **核心变更**：
  1. **历史截图队列（`MainWindow.History.cs`）**：新增 `368 × 276` 大尺寸卡片网格抽屉（`MA_NOACTIVATE` 无焦点夺取 + 按键直通主窗口），按最新在前排列本次会话全部截图，支持单击定位资源管理器文件与 `Esc` 一键收起。
  2. **单点删除与原位无损恢复（`ScreenshotWriter.cs`、`SaveSync`）**：新增异步落盘同步锁 `SaveSync`；点击「删除」立即将磁盘文件移入会话隔离临时回收目录，**保留原卡片槽位零跳动**并原位切换为「恢复误删」按钮，支持一键无损还原回原目录原文件名。
  3. **自动化自检（`SelfTest.cs`）**：新增历史队列计数、不自动弹窗、展开保持三段式零位移、单点删除与原位恢复往返验证。

---

## v2.0.4 构建记录（2026-11-16，提交 `c12e59f`）

### 1. 截图模式水平镜像翻转与同步脚本代理接入
- **核心变更**：
  1. **水平镜像翻转（`ScreenshotWriter.FlipHorizontal`、`CropSelection.MirrorHorizontal`、`MainWindow.Screenshot.cs`）**：在 `~` 截图模式内支持按 `Tab` 键对冻结帧及已画选框执行像素级对称水平翻转，保存结果同步翻转。
  2. **双机同步增强（`tools/RepoSync.ps1`）**：自动读取本机环境变量、Git 配置与 Windows 系统代理（`ProxyEnable` / `ProxyServer`），并复用 Git Credential Manager / `gh` 登录凭据，解决代理环境下推送与上传 GitHub Release 超时问题。
  3. **自动化自检（`SelfTest.cs`）**：新增 `CropSelection.MirrorHorizontal` 几何对称往返与 `FlipHorizontal` 逐像素翻转校验。

---

## v2.0.3 构建记录（2026-11-16，提交 `7301d9d`）

### 1. 素材库播放列表优先、高密度悬停缩略图与窗外防误点护盾
- **核心变更**：
  1. **素材库排序与显式列表优先（`Playlist.cs`）**：支持解析 `DshGalleryExplorer` 传入的 `DSH_GALLERY_PLAYLIST_FILE`（四色标签筛选/排序列表）与 `DSH_GALLERY_SORT_SPEC`，优先级高于资源管理器窗口排序与自然序兜底。
  2. **0.08s 高密度精确悬停预览与切集防串帧（`MpvThumbnailer.cs`）**：将精确时间桶细化至 `0.08s`（约 `12.5 fps` 预览密度），区分关键帧粗锚点与悬停精确帧缓存；切集加载时等待新文件 `PlaybackRestart` 并持续消费 `vo_libmpv` 渲染请求，消除切集残留旧尺寸与 `200 ms` 阻塞。
  3. **截图模式全屏防误点护盾（`MainWindow.Input.cs`）**：进入 `~` 截图模式时在主窗口紧邻下一层 Z 序挂载透明 `WS_EX_NOACTIVATE` 护盾窗口（`Alpha=1`）并拦截窗口 `8px` 缩放边框命中，支持从屏幕边缘安心起笔框选而绝不误点桌面或后台窗口、不丢失主窗口焦点。
  4. **构建安全策略兼容（`build.ps1`、`tools/RepoSync.ps1`）**：将自检与静默安装复刻至 `%TEMP%` 执行，兼容 Windows 安全策略对受保护目录的拦截。

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
