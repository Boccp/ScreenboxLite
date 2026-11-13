using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenboxLite.Core;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// 键盘与鼠标。快捷键表（与 v1 一致）：
    ///   ~            进入截图模式 / 模式内确认保存（物理扫描码直通，中文输入法下也有效）
    ///   R            截图模式内恢复上次选框
    ///   滚轮         逐帧（下=前进）；Ctrl/Shift+滚轮 ±1 秒
    ///   A / D        按住 5 倍速快退 / 快进；S+A / S+D 逐帧
    ///   1 / 2 / 3    前进 5s / 20s / 60s；按住 E 时为后退
    ///   , / .        逐帧；← / → ±5s（Shift ±1s）；↑ / ↓ 音量
    ///   Space / K    播放暂停；PageUp/PageDown（Shift+P/N）切换同目录视频
    ///   F / F11      全屏；M 静音；Ctrl+O 打开；Esc 截图模式上一步 / 退出全屏
    ///   右键         截图模式内逐级“上一步”
    /// </summary>
    internal sealed partial class MainWindow
    {
        private const double FastSeekRate = 5.0; // A/D 按住：每秒推进 5 秒

        private bool _keyA, _keyD, _keyS, _keyE, _tildeDown;
        private long _lastTildeTick, _lastRTick;
        private bool _fastForwarding;
        private bool _renderHooked;
        private readonly Stopwatch _holdClock = new Stopwatch();
        private double _reverseTarget = -1;
        private DispatcherTimer _outsideMouseTimer;

        private void InitInput()
        {
            Focusable = true;
            InputMethod.SetIsInputMethodEnabled(this, false);
            SourceInitialized += (s, e) =>
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src != null) src.AddHook(WndProc);
            };
            Loaded += (s, e) => { Activate(); FocusMain(); };
            Activated += (s, e) => { if (Keyboard.FocusedElement != _jumpBox) FocusMain(); };
            Deactivated += (s, e) => ReleaseAllHeldKeys();
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            PreviewMouseDown += OnPreviewMouseDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewMouseUp += OnPreviewMouseUp;
            MouseWheel += OnMouseWheel;
            Drop += OnDrop;
            DragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };

            _stage.MouseLeftButtonUp += (s, e) =>
            {
                if (!_shotActive && _player.CurrentPath != null) TogglePlayPause();
            };
            _stage.SizeChanged += (s, e) => OnStageSizeChanged();

            // 截图模式下鼠标移出窗口（到桌面）时，准星/选框仍需跟随
            _outsideMouseTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            _outsideMouseTimer.Tick += (s, e) =>
            {
                if (!_shotActive || _overlayRoot.IsMouseOver || _overlayRoot.IsMouseCaptured) return;
                try
                {
                    var sp = System.Windows.Forms.Control.MousePosition;
                    Point p = _overlayRoot.PointFromScreen(new Point(sp.X, sp.Y));
                    ShotMouseMove(p, (System.Windows.Forms.Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) != 0);
                }
                catch (InvalidOperationException)
                {
                    // 窗口尚未呈现
                }
            };
            _outsideMouseTimer.Start();
        }

        // ======================= 物理按键（WndProc，绕过输入法） =======================

        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int ScanTilde = 0x29, ScanA = 0x1E, ScanD = 0x20, ScanS = 0x1F, ScanE = 0x12, ScanR = 0x13, Scan1 = 0x02, Scan2 = 0x03, Scan3 = 0x04;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN && msg != WM_KEYUP && msg != WM_SYSKEYUP) return IntPtr.Zero;
            long lp = lParam.ToInt64();
            int scan = (int)((lp >> 16) & 0xFF);
            int vk = wParam.ToInt32() & 0xFFFF;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool repeat = (lp & (1L << 30)) != 0;
            bool typing = Keyboard.FocusedElement == _jumpBox;

            if (!down)
            {
                if (scan == ScanTilde || vk == 0xC0) _tildeDown = false;
                else if (scan == ScanA) { _keyA = false; OnHoldChanged(); }
                else if (scan == ScanD) { _keyD = false; OnHoldChanged(); }
                else if (scan == ScanS) _keyS = false;
                else if (scan == ScanE) _keyE = false;
                return IntPtr.Zero;
            }

            if (scan == ScanTilde || vk == 0xC0)
            {
                if (!repeat && !_tildeDown && Debounce(ref _lastTildeTick, 45))
                {
                    _tildeDown = true;
                    HandleTilde();
                }
                handled = true;
                return IntPtr.Zero;
            }
            if (typing) return IntPtr.Zero;

            handled = HandleLetterKey(scan, repeat);
            return IntPtr.Zero;
        }

        /// <summary>按扫描码处理字母/数字键（WndProc 与 PreviewKeyDown 共用，保证有无输入法行为一致）。</summary>
        private bool HandleLetterKey(int scan, bool repeat)
        {
            switch (scan)
            {
                case ScanS: _keyS = true; return true;
                case ScanE: _keyE = true; return true;
                case ScanA:
                    if (_keyS) _nav.StepFrames(-1);
                    else if (!_keyA) { _keyA = true; OnHoldChanged(); }
                    return true;
                case ScanD:
                    if (_keyS) _nav.StepFrames(1);
                    else if (!_keyD) { _keyD = true; OnHoldChanged(); }
                    return true;
                case Scan1: _nav.StepSeconds((_keyE ? -1 : 1) * 5.0); return true;
                case Scan2: _nav.StepSeconds((_keyE ? -1 : 1) * 20.0); return true;
                case Scan3: _nav.StepSeconds((_keyE ? -1 : 1) * 60.0); return true;
                case ScanR:
                    if (!_shotActive) return false;
                    if (!repeat && Debounce(ref _lastRTick, 120)) RestoreLastCrop();
                    return true;
            }
            return false;
        }

        private static bool Debounce(ref long last, int ms)
        {
            long now = Environment.TickCount;
            if (Math.Abs(now - last) < ms) return false;
            last = now;
            return true;
        }

        // ======================= 其余按键 =======================

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
            if (key == Key.Oem3) { e.Handled = true; return; } // 已由 WndProc 处理
            if (Keyboard.FocusedElement == _jumpBox && key != Key.Escape) return;

            // WndProc 已按扫描码处理过的字母/数字键，这里吞掉避免重复
            if (key == Key.A || key == Key.D || key == Key.S || key == Key.E || key == Key.D1 || key == Key.D2 || key == Key.D3 ||
                (key == Key.R && _shotActive))
            {
                e.Handled = true;
                return;
            }

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (key)
            {
                case Key.NumPad1: _nav.StepSeconds((_keyE ? -1 : 1) * 5.0); break;
                case Key.NumPad2: _nav.StepSeconds((_keyE ? -1 : 1) * 20.0); break;
                case Key.NumPad3: _nav.StepSeconds((_keyE ? -1 : 1) * 60.0); break;
                case Key.Escape:
                    if (_shotActive) ShotBack();
                    else if (WindowStyle == WindowStyle.None) ToggleFullscreen();
                    else return;
                    break;
                case Key.Space:
                case Key.K: TogglePlayPause(); break;
                case Key.PageUp: SwitchNeighbor(-1); break;
                case Key.PageDown: SwitchNeighbor(1); break;
                case Key.P: if (!shift) return; SwitchNeighbor(-1); break;
                case Key.N: if (!shift) return; SwitchNeighbor(1); break;
                case Key.Left: _nav.StepSeconds(shift ? -1.0 : -5.0); break;
                case Key.Right: _nav.StepSeconds(shift ? 1.0 : 5.0); break;
                case Key.Up: SetVolume(_settings.Volume + 0.05, true); break;
                case Key.Down: SetVolume(_settings.Volume - 0.05, true); break;
                case Key.M: if (Keyboard.Modifiers != ModifierKeys.None) return; ToggleMute(); break;
                case Key.OemComma: _nav.StepFrames(-1); break;
                case Key.OemPeriod: _nav.StepFrames(1); break;
                case Key.F:
                case Key.F11: ToggleFullscreen(); break;
                case Key.O: if (Keyboard.Modifiers != ModifierKeys.Control) return; OpenFilePicker(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
            if (key == Key.Oem3) _tildeDown = false;
            else if (key == Key.A) { _keyA = false; OnHoldChanged(); }
            else if (key == Key.D) { _keyD = false; OnHoldChanged(); }
            else if (key == Key.S) _keyS = false;
            else if (key == Key.E) _keyE = false;
        }

        private void ReleaseAllHeldKeys()
        {
            _keyA = _keyD = _keyS = _keyE = _tildeDown = false;
            OnHoldChanged();
        }

        // ======================= A/D 按住：5 倍速快进 / 快退 =======================

        private void OnHoldChanged()
        {
            bool forward = _keyD && !_keyA && !_keyS;
            bool backward = _keyA && !_keyD && !_keyS;

            // 快进：直接 5 倍速播放（mpv 连续解码，画面流畅）；松开即暂停在当前帧
            if (forward && !_fastForwarding && _player.HasVideo)
            {
                _fastForwarding = true;
                _player.SetPause(false);
                _player.SetSpeed(FastSeekRate);
            }
            else if (!forward && _fastForwarding)
            {
                _fastForwarding = false;
                _player.SetPause(true);
                _player.SetSpeed(_speedCombo.SelectedIndex >= 0 ? Speeds[_speedCombo.SelectedIndex] : 1.0);
            }

            // 快退：按渲染节拍推进目标时间，由导航器合并为“同时最多一个”的精确 seek
            if (backward && _player.HasVideo)
            {
                if (_reverseTarget < 0)
                {
                    _player.SetPause(true);
                    _reverseTarget = _nav.BaseTime();
                    _holdClock.Restart();
                }
                HookRender(true);
            }
            else
            {
                _reverseTarget = -1;
                HookRender(false);
            }
        }

        private void HookRender(bool on)
        {
            if (on == _renderHooked) return;
            _renderHooked = on;
            if (on) CompositionTarget.Rendering += OnRenderTick;
            else CompositionTarget.Rendering -= OnRenderTick;
        }

        private void OnRenderTick(object sender, EventArgs e)
        {
            if (_reverseTarget < 0) return;
            double dt = Math.Min(0.15, _holdClock.Elapsed.TotalSeconds);
            _holdClock.Restart();
            _reverseTarget = Math.Max(0, _reverseTarget - FastSeekRate * dt);
            _nav.SeekTo(_reverseTarget, true);
            UpdateTimeUi(true);
        }

        // ======================= 鼠标 =======================

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_player.HasVideo) return;
            int notches = -(e.Delta / 120);
            if (notches == 0) notches = e.Delta > 0 ? -1 : 1;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) _nav.StepSeconds(notches);
            else _nav.StepFrames(notches);
            UpdateTimeUi(true);
            e.Handled = true;
        }

        private static bool IsButtonSource(object source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is ButtonBase) return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_shotActive) return;
            if (e.ChangedButton == MouseButton.Right)
            {
                ShotBack();
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Left && !IsButtonSource(e.OriginalSource))
            {
                // 在顶栏/底栏边缘起笔也有效：坐标会被钳制到最近的画面边缘
                ShotMouseDown(e.GetPosition(_overlayRoot));
                e.Handled = true;
            }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_shotActive) return;
            ShotMouseMove(e.GetPosition(_overlayRoot), e.LeftButton == MouseButtonState.Pressed);
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_shotActive || e.ChangedButton != MouseButton.Left || _crop.Mode == CropMode.None) return;
            ShotMouseUp(e.GetPosition(_overlayRoot));
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) LoadVideo(files[0]);
        }
    }
}
