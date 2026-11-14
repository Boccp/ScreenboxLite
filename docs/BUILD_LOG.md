# ScreenboxLite 构建记录与验证台账

本文档记录每次重要版本的构建环境、依赖锁定、编译产物、自动化自检以及真实视频基准验证结果，便于后续维护与跨设备复现。

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
