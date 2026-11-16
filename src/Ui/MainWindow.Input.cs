using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ScreenboxLite.Core;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// 键盘与鼠标焦点/快捷键中枢。快捷键表（与 v1 一致）：
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

        private IntPtr _mainHwnd;
        private bool _inModalDialog;
        private bool _keyA, _keyD, _keyS, _keyE, _tildeDown;
        private long _lastTildeTick, _lastRTick, _lastTabTick;
        private bool _stageClickArmed;
        private POINT _stageDownPt;
        private long _lastStageClickUpTick;
        private POINT _lastStageClickUpPt;
        private long _lastRawHandledTick;
        private int _lastRawHandledVk;
        private bool _fastForwarding;
        private bool _renderHooked;
        private readonly Stopwatch _holdClock = new Stopwatch();
        private double _reverseTarget = -1;
        private DispatcherTimer _outsideMouseTimer;
        private DispatcherTimer _focusGuardTimer;
        private HwndSource _shieldSource;

        // ---- 供自动化自检读取的内部访问器 ----
        internal IntPtr MainHwnd { get { return _mainHwnd; } }
        internal IntPtr ShotShieldHwnd { get { return _shieldSource != null ? _shieldSource.Handle : IntPtr.Zero; } }
        internal VideoHost VideoHostControl { get { return _videoHost; } }
        internal Grid StageControl { get { return _stage; } }
        internal TextBox JumpBoxControl { get { return _jumpBox; } }
        internal bool IsShotActive { get { return _shotActive; } }
        internal Mpv.MpvPlayer PlayerControl { get { return _player; } }
        internal FrameNavigator NavControl { get { return _nav; } }

        private void InitInput()
        {
            Focusable = true;
            InputMethod.SetIsInputMethodEnabled(this, false);

            SourceInitialized += (s, e) =>
            {
                _mainHwnd = new WindowInteropHelper(this).Handle;
                if (_mainHwnd != IntPtr.Zero)
                    ImmAssociateContext(_mainHwnd, IntPtr.Zero);
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src != null) src.AddHook(WndProc);
                ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
            };

            Loaded += (s, e) =>
            {
                Activate();
                FocusMain();
            };

            Activated += (s, e) =>
            {
                if (!IsTypingInJumpBox()) FocusMain();
                if (_shotActive) UpdateShotShield();
            };

            Deactivated += (s, e) =>
            {
                ReleaseAllHeldKeys();
                HideShotShield();
                CloseHistoryPopup();
            };

            LocationChanged += (s, e) =>
            {
                if (_shotActive && IsActive) UpdateShotShield();
                if (IsHistoryPopupOpen) RepositionHistoryPopup();
            };
            SizeChanged += (s, e) =>
            {
                if (IsHistoryPopupOpen) RepositionHistoryPopup();
            };
            StateChanged += (s, e) =>
            {
                if (_shotActive && IsActive) UpdateShotShield();
                if (IsHistoryPopupOpen) RepositionHistoryPopup();
            };

            Closed += (s, e) =>
            {
                ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
                if (_outsideMouseTimer != null) _outsideMouseTimer.Stop();
                if (_focusGuardTimer != null) _focusGuardTimer.Stop();
                DisposeShotShield();
            };

            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            PreviewMouseDown += OnPreviewMouseDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewMouseUp += OnPreviewMouseUp;
            MouseWheel += OnMouseWheel;
            Drop += OnDrop;
            DragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };

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

            // 焦点自愈守卫：当主窗口处于前台且未在定点跳转框输入时，确保 Win32 键盘焦点与 WPF 焦点始终在主窗口
            _focusGuardTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            _focusGuardTimer.Tick += (s, e) =>
            {
                if (_inModalDialog || !IsActive || _mainHwnd == IntPtr.Zero) return;
                if (_videoHost != null) _videoHost.DisableChildInput();
                if (IsTypingInJumpBox()) return;
                if (_speedCombo != null && _speedCombo.IsDropDownOpen) return;
                if (GetForegroundWindow() == _mainHwnd && (GetFocus() != _mainHwnd || Keyboard.FocusedElement != this))
                {
                    FocusMain();
                }
            };
            _focusGuardTimer.Start();
        }

        internal bool IsTypingInJumpBox()
        {
            return _jumpBox != null && _jumpBox.Focusable &&
                   (Keyboard.FocusedElement == _jumpBox || _jumpBox.IsKeyboardFocusWithin);
        }

        /// <summary>
        /// 同时恢复 Win32 真实键盘焦点（user32!SetFocus）与 WPF 逻辑焦点，并释放定点跳转框焦点锁定。
        /// </summary>
        internal void FocusMain()
        {
            if (_jumpBox != null && _jumpBox.Focusable)
            {
                _jumpBox.Focusable = false;
            }

            IntPtr hwnd = _mainHwnd != IntPtr.Zero ? _mainHwnd : new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                _mainHwnd = hwnd;
                ImmAssociateContext(hwnd, IntPtr.Zero);

                IntPtr currentFocus = GetFocus();
                if (currentFocus != hwnd)
                {
                    uint otherThread = 0;
                    uint curThread = GetCurrentThreadId();
                    if (currentFocus == IntPtr.Zero && GetForegroundWindow() == hwnd)
                    {
                        var gti = new GUITHREADINFO();
                        gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                        if (GetGUIThreadInfo(0, ref gti) && gti.hwndFocus != IntPtr.Zero && gti.hwndFocus != hwnd)
                        {
                            uint pid;
                            otherThread = GetWindowThreadProcessId(gti.hwndFocus, out pid);
                        }
                    }

                    bool attached = false;
                    if (otherThread != 0 && otherThread != curThread)
                    {
                        attached = AttachThreadInput(otherThread, curThread, true);
                    }
                    try
                    {
                        SetFocus(hwnd);
                    }
                    finally
                    {
                        if (attached) AttachThreadInput(otherThread, curThread, false);
                    }
                }
            }

            FocusManager.SetFocusedElement(this, this);
            Focus();
            Keyboard.Focus(this);
        }

        // ======================= 物理按键与消息级快捷键（绕过输入法与子窗口） =======================

        private const int WM_SETFOCUS = 0x0007;
        private const int WM_SETCURSOR = 0x0020;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_NCRBUTTONDOWN = 0x00A4;
        private const int WM_NCRBUTTONDBLCLK = 0x00A6;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_RBUTTONDBLCLK = 0x0206;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        // 标准 PC Set-1 物理扫描码（与输入法状态无关）
        private const int ScanEsc = 0x01;
        private const int Scan1 = 0x02;
        private const int Scan2 = 0x03;
        private const int Scan3 = 0x04;
        private const int ScanTab = 0x0F;
        private const int ScanE = 0x12;
        private const int ScanR = 0x13;
        private const int ScanO = 0x18;
        private const int ScanP = 0x19;
        private const int ScanA = 0x1E;
        private const int ScanS = 0x1F;
        private const int ScanD = 0x20;
        private const int ScanF = 0x21;
        private const int ScanK = 0x25;
        private const int ScanTilde = 0x29;
        private const int ScanN = 0x31;
        private const int ScanM = 0x32;
        private const int ScanComma = 0x33;
        private const int ScanPeriod = 0x34;
        private const int ScanSpace = 0x39;
        private const int ScanF11 = 0x57;

        // Win32 虚拟键码
        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_PRIOR = 0x21; // PageUp
        private const int VK_NEXT = 0x22;  // PageDown
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_NUMPAD1 = 0x61;
        private const int VK_NUMPAD2 = 0x62;
        private const int VK_NUMPAD3 = 0x63;
        private const int VK_F11 = 0x7A;
        private const int VK_OEM_COMMA = 0xBC;
        private const int VK_OEM_PERIOD = 0xBE;
        private const int VK_OEM_3 = 0xC0;
        private const int VK_PROCESSKEY = 0xE5;

        private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (handled || _inModalDialog || _mainHwnd == IntPtr.Zero) return;
            int m = msg.message;
            if (m != WM_KEYDOWN && m != WM_SYSKEYDOWN && m != WM_KEYUP && m != WM_SYSKEYUP) return;
            if (msg.hwnd != _mainHwnd && !IsChild(_mainHwnd, msg.hwnd)) return;
            if (ProcessRawKeyMessage(msg.hwnd, m, msg.wParam, msg.lParam))
            {
                handled = true;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SETFOCUS)
            {
                if (!IsTypingInJumpBox() && hwnd != IntPtr.Zero)
                    ImmAssociateContext(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            }
            // 截图模式下，防止贴边选框时误点窗口 8px 缩放边框导致拖拽窗口大小
            if (_shotActive && !_inModalDialog &&
                (msg == WM_NCLBUTTONDOWN || msg == WM_NCLBUTTONDBLCLK || msg == WM_NCRBUTTONDOWN || msg == WM_NCRBUTTONDBLCLK))
            {
                int ht = wParam.ToInt32();
                if (ht >= 10 && ht <= 17) // HTLEFT(10)..HTBOTTOMRIGHT(17)
                {
                    FocusMain();
                    if (msg == WM_NCRBUTTONDOWN || msg == WM_NCRBUTTONDBLCLK)
                    {
                        ShotBack();
                    }
                    else
                    {
                        var sp = System.Windows.Forms.Control.MousePosition;
                        ShotMouseDown(_overlayRoot.PointFromScreen(new Point(sp.X, sp.Y)));
                    }
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                if (!_inModalDialog && (_speedCombo == null || !_speedCombo.IsDropDownOpen))
                {
                    if ((IsTypingInJumpBox() || GetFocus() != hwnd || !ReferenceEquals(Keyboard.FocusedElement, this)) &&
                        !IsClientPointInsideControl(hwnd, lParam, _jumpBox) &&
                        !IsClientPointInsideControl(hwnd, lParam, _speedCombo))
                    {
                        FocusMain();
                    }
                }
                if ((msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK) &&
                    !_shotActive && _player != null && _player.CurrentPath != null &&
                    IsClientPointInsideControl(hwnd, lParam, _stage))
                {
                    POINT pt = ClientPointFromLParam(lParam);
                    long now = Environment.TickCount;
                    bool isDbl = msg == WM_LBUTTONDBLCLK ||
                                 (Math.Abs(now - _lastStageClickUpTick) <= System.Windows.Forms.SystemInformation.DoubleClickTime &&
                                  Math.Abs(pt.X - _lastStageClickUpPt.X) <= 6 &&
                                  Math.Abs(pt.Y - _lastStageClickUpPt.Y) <= 6);
                    if (isDbl)
                    {
                        _stageClickArmed = false;
                        _lastStageClickUpTick = 0;
                        TogglePlayPause(); // 还原第一次单击改变的播放状态，使双击全屏不打断播放
                        ToggleFullscreen();
                    }
                    else
                    {
                        _stageClickArmed = true;
                        _stageDownPt = pt;
                    }
                }
                else if (msg == WM_LBUTTONDOWN)
                {
                    _stageClickArmed = false;
                }
                return IntPtr.Zero;
            }
            if (msg == WM_LBUTTONUP)
            {
                if (_stageClickArmed)
                {
                    _stageClickArmed = false;
                    if (!_shotActive && _player != null && _player.CurrentPath != null &&
                        IsClientPointInsideControl(hwnd, lParam, _stage))
                    {
                        POINT pt = ClientPointFromLParam(lParam);
                        if (Math.Abs(pt.X - _stageDownPt.X) <= 6 && Math.Abs(pt.Y - _stageDownPt.Y) <= 6)
                        {
                            _lastStageClickUpTick = Environment.TickCount;
                            _lastStageClickUpPt = pt;
                            TogglePlayPause();
                        }
                    }
                }
                return IntPtr.Zero;
            }
            if (handled || _inModalDialog) return IntPtr.Zero;
            if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN && msg != WM_KEYUP && msg != WM_SYSKEYUP)
                return IntPtr.Zero;
            if (ProcessRawKeyMessage(hwnd, msg, wParam, lParam))
            {
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static POINT ClientPointFromLParam(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            return new POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
        }

        private static bool IsClientPointInsideControl(IntPtr hwnd, IntPtr lParam, FrameworkElement el)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0) return false;
            try
            {
                POINT pt = ClientPointFromLParam(lParam);
                if (!ClientToScreen(hwnd, ref pt)) return false;
                Point local = el.PointFromScreen(new Point(pt.X, pt.Y));
                return local.X >= 0 && local.X <= el.ActualWidth && local.Y >= 0 && local.Y <= el.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 统一处理所有物理扫描码与虚拟键码快捷键（消息循环预处理与 WndProc 共用）。
        /// 即使中文输入法将 wParam 改写为 VK_PROCESSKEY(0xE5)，仍通过硬件扫描码 + ImmGetVirtualKey 100% 识别。
        /// </summary>
        internal bool ProcessRawKeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            int scan = (int)((lp >> 16) & 0xFF);
            bool extended = (lp & (1L << 24)) != 0;
            int vk = wParam.ToInt32() & 0xFFFF;
            if (vk == VK_PROCESSKEY && scan == 0 && hwnd != IntPtr.Zero)
            {
                int realVk = ImmGetVirtualKey(hwnd);
                if (realVk != 0 && realVk != VK_PROCESSKEY) vk = realVk;
            }

            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool repeat = (lp & (1L << 30)) != 0;
            bool typing = IsTypingInJumpBox();

            bool isTilde = (!extended && scan == ScanTilde) || vk == VK_OEM_3;
            bool isA = (!extended && scan == ScanA) || vk == 'A';
            bool isD = (!extended && scan == ScanD) || vk == 'D';
            bool isS = (!extended && scan == ScanS) || vk == 'S';
            bool isE = (!extended && scan == ScanE) || vk == 'E';
            bool isR = (!extended && scan == ScanR) || vk == 'R';
            bool isTab = (!extended && scan == ScanTab) || vk == VK_TAB;
            bool is1 = (!extended && scan == Scan1) || vk == '1' || vk == VK_NUMPAD1;
            bool is2 = (!extended && scan == Scan2) || vk == '2' || vk == VK_NUMPAD2;
            bool is3 = (!extended && scan == Scan3) || vk == '3' || vk == VK_NUMPAD3;

            if (!down)
            {
                if (isTilde) { _tildeDown = false; return true; }
                if (isA) { _keyA = false; OnHoldChanged(); return !typing; }
                if (isD) { _keyD = false; OnHoldChanged(); return !typing; }
                if (isS) { _keyS = false; return !typing; }
                if (isE) { _keyE = false; return !typing; }
                return false;
            }

            bool ctrl = IsVkDown(VK_CONTROL) || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = IsVkDown(VK_SHIFT) || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            // Win32 WM_SYSKEYDOWN 在焦点窗口为 NULL 时也会发给活动窗口（此时 bit 29 = 0，并非按下 Alt）
            bool alt = IsVkDown(VK_MENU) || (lp & (1L << 29)) != 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            if (alt) return false;

            // ~ 键在任何非 Ctrl 状态下直通（包括输入时间时可一键进截图模式）
            if (isTilde && !ctrl)
            {
                if (!repeat && !_tildeDown && Debounce(ref _lastTildeTick, 45))
                {
                    _tildeDown = true;
                    HandleTilde();
                }
                MarkRawHandled(vk);
                return true;
            }

            // 正在定点跳转框内编辑：只接管回车与 Esc，其余字符交还文本框
            if (typing)
            {
                if (vk == VK_ESCAPE || (!extended && scan == ScanEsc))
                {
                    if (!repeat) FocusMain();
                    MarkRawHandled(VK_ESCAPE);
                    return true;
                }
                if (vk == VK_RETURN)
                {
                    if (!repeat) JumpToInput(false);
                    MarkRawHandled(VK_RETURN);
                    return true;
                }
                return false;
            }

            // 若因极端情况 Win32 焦点不在主窗口而消息落到此处，顺手收复焦点
            if (_mainHwnd != IntPtr.Zero && GetFocus() != _mainHwnd)
            {
                FocusMain();
            }

            if (ctrl)
            {
                if (!shift && ((!extended && scan == ScanO) || vk == 'O'))
                {
                    if (!repeat) OpenFilePicker();
                    MarkRawHandled('O');
                    return true;
                }
                return false;
            }

            if (isS) { _keyS = true; MarkRawHandled('S'); return true; }
            if (isE) { _keyE = true; MarkRawHandled('E'); return true; }
            if (isA)
            {
                if (_keyS) { _nav.StepFrames(-1); UpdateTimeUi(true); }
                else if (!_keyA) { _keyA = true; OnHoldChanged(); }
                MarkRawHandled('A');
                return true;
            }
            if (isD)
            {
                if (_keyS) { _nav.StepFrames(1); UpdateTimeUi(true); }
                else if (!_keyD) { _keyD = true; OnHoldChanged(); }
                MarkRawHandled('D');
                return true;
            }
            if (is1) { _nav.StepSeconds((_keyE ? -1 : 1) * 5.0); UpdateTimeUi(true); MarkRawHandled('1'); return true; }
            if (is2) { _nav.StepSeconds((_keyE ? -1 : 1) * 20.0); UpdateTimeUi(true); MarkRawHandled('2'); return true; }
            if (is3) { _nav.StepSeconds((_keyE ? -1 : 1) * 60.0); UpdateTimeUi(true); MarkRawHandled('3'); return true; }
            if (isR)
            {
                if (!_shotActive) return false;
                if (!repeat && Debounce(ref _lastRTick, 120)) RestoreLastCrop();
                MarkRawHandled('R');
                return true;
            }
            if (isTab)
            {
                if (!_shotActive) return false;
                if (!repeat && Debounce(ref _lastTabTick, 60)) ToggleShotHorizontalFlip();
                MarkRawHandled(VK_TAB);
                return true;
            }

            if ((!extended && scan == ScanComma) || vk == VK_OEM_COMMA)
            {
                _nav.StepFrames(-1);
                UpdateTimeUi(true);
                MarkRawHandled(VK_OEM_COMMA);
                return true;
            }
            if ((!extended && scan == ScanPeriod) || vk == VK_OEM_PERIOD)
            {
                _nav.StepFrames(1);
                UpdateTimeUi(true);
                MarkRawHandled(VK_OEM_PERIOD);
                return true;
            }
            if ((!extended && scan == ScanSpace) || vk == VK_SPACE || (!extended && scan == ScanK) || vk == 'K')
            {
                if (!repeat) TogglePlayPause();
                MarkRawHandled(VK_SPACE);
                return true;
            }
            if ((!extended && scan == ScanF) || vk == 'F' || (!extended && scan == ScanF11) || vk == VK_F11)
            {
                if (!repeat) ToggleFullscreen();
                MarkRawHandled(VK_F11);
                return true;
            }
            if (!shift && ((!extended && scan == ScanM) || vk == 'M'))
            {
                if (!repeat) ToggleMute();
                MarkRawHandled('M');
                return true;
            }
            if (vk == VK_PRIOR || (shift && ((!extended && scan == ScanP) || vk == 'P')))
            {
                if (!repeat) SwitchNeighbor(-1);
                MarkRawHandled(VK_PRIOR);
                return true;
            }
            if (vk == VK_NEXT || (shift && ((!extended && scan == ScanN) || vk == 'N')))
            {
                if (!repeat) SwitchNeighbor(1);
                MarkRawHandled(VK_NEXT);
                return true;
            }
            if (vk == VK_LEFT)
            {
                _nav.StepSeconds(shift ? -1.0 : -5.0);
                UpdateTimeUi(true);
                MarkRawHandled(VK_LEFT);
                return true;
            }
            if (vk == VK_RIGHT)
            {
                _nav.StepSeconds(shift ? 1.0 : 5.0);
                UpdateTimeUi(true);
                MarkRawHandled(VK_RIGHT);
                return true;
            }
            if (vk == VK_UP)
            {
                SetVolume(_settings.Volume + 0.05, true);
                MarkRawHandled(VK_UP);
                return true;
            }
            if (vk == VK_DOWN)
            {
                SetVolume(_settings.Volume - 0.05, true);
                MarkRawHandled(VK_DOWN);
                return true;
            }
            if (vk == VK_RETURN && _shotActive)
            {
                if (!repeat && Debounce(ref _lastTildeTick, 45)) HandleTilde();
                MarkRawHandled(VK_RETURN);
                return true;
            }
            if ((!extended && scan == ScanEsc) || vk == VK_ESCAPE)
            {
                if (_shotActive)
                {
                    if (!repeat) ShotBack();
                    MarkRawHandled(VK_ESCAPE);
                    return true;
                }
                if (IsHistoryPopupOpen)
                {
                    if (!repeat) CloseHistoryPopup();
                    MarkRawHandled(VK_ESCAPE);
                    return true;
                }
                if (WindowStyle == WindowStyle.None)
                {
                    if (!repeat) ToggleFullscreen();
                    MarkRawHandled(VK_ESCAPE);
                    return true;
                }
                return false;
            }

            return false;
        }

        private void MarkRawHandled(int vk)
        {
            _lastRawHandledVk = vk;
            _lastRawHandledTick = Environment.TickCount;
        }

        private bool WasJustRawHandled(int vk)
        {
            return _lastRawHandledVk == vk && Math.Abs(Environment.TickCount - _lastRawHandledTick) < 25;
        }

        private static bool IsVkDown(int vk)
        {
            return (GetKeyState(vk) & 0x8000) != 0;
        }

        private static bool Debounce(ref long last, int ms)
        {
            long now = Environment.TickCount;
            if (Math.Abs(now - last) < ms) return false;
            last = now;
            return true;
        }

        // ======================= WPF PreviewKey 兜底（防止非消息渠道漏网） =======================

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (WasJustRawHandled(vk))
            {
                e.Handled = true;
                return;
            }
            if (IsTypingInJumpBox() && key != Key.Escape && key != Key.Oem3) return;
            if (ProcessRawKeyMessage(_mainHwnd, WM_KEYDOWN, new IntPtr(vk), e.IsRepeat ? new IntPtr(1L << 30) : IntPtr.Zero))
            {
                e.Handled = true;
            }
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

        private static DependencyObject SafeParent(DependencyObject d)
        {
            if (d == null) return null;
            if (d is Visual || d is Visual3D) return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        private static bool IsDescendantOf(DependencyObject d, DependencyObject ancestor)
        {
            if (ancestor == null) return false;
            while (d != null)
            {
                if (ReferenceEquals(d, ancestor)) return true;
                d = SafeParent(d);
            }
            return false;
        }

        private static bool IsButtonSource(object source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is ButtonBase) return true;
                d = SafeParent(d);
            }
            return false;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            // 点击定点跳转框与倍速下拉框以外的任何区域（视频画面、时间轴、按钮、顶底栏），立即收回主窗口键盘焦点
            if ((IsTypingInJumpBox() || (_mainHwnd != IntPtr.Zero && GetFocus() != _mainHwnd) || !ReferenceEquals(Keyboard.FocusedElement, this)) &&
                !IsDescendantOf(src, _jumpBox) && !IsDescendantOf(src, _speedCombo))
            {
                FocusMain();
            }

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

        // ======================= Win32 焦点与输入法 P/Invoke =======================

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        // ======================= 截图模式全屏防误点护盾层（紧贴主窗口正下方 Z 序，拦截窗外点击且绝不夺取焦点） =======================

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int SW_HIDE = 0;
        private const int MA_NOACTIVATE = 3;
        private const int IDC_CROSS = 32515;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private void EnsureShotShield()
        {
            if (_shieldSource != null) return;
            var p = new HwndSourceParameters("ScreenboxShotShield")
            {
                ParentWindow = IntPtr.Zero, // 独立顶层无主窗口，以便精确置于 _mainHwnd 正后方 Z 序
                WindowStyle = WS_POPUP,
                ExtendedWindowStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
                PositionX = 0,
                PositionY = 0,
                Width = 1,
                Height = 1
            };
            _shieldSource = new HwndSource(p);
            _shieldSource.AddHook(ShieldWndProc);
            // Alpha=1（肉眼完全透明，但内核命中测试 100% 实体，彻底挡住桌面与后台窗口误点）
            SetLayeredWindowAttributes(_shieldSource.Handle, 0, 1, LWA_ALPHA);
        }

        internal void UpdateShotShield()
        {
            if (!_shotActive || _inModalDialog || _mainHwnd == IntPtr.Zero || !IsVisible)
            {
                HideShotShield();
                return;
            }
            if (!IsActive && GetForegroundWindow() != _mainHwnd && GetActiveWindow() != _mainHwnd)
            {
                HideShotShield();
                return;
            }

            EnsureShotShield();
            IntPtr sh = _shieldSource.Handle;
            if (sh == IntPtr.Zero) return;

            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (Topmost)
            {
                SetWindowPos(sh, HWND_TOPMOST, vs.Left, vs.Top, vs.Width, vs.Height, SWP_NOACTIVATE);
                SetWindowPos(_mainHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            // 将护盾置于 _mainHwnd 紧邻的下一层：主窗口内部按钮与画面正常交互，窗口外全屏幕区域均由护盾无焦点接管
            SetWindowPos(sh, _mainHwnd, vs.Left, vs.Top, vs.Width, vs.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        internal void HideShotShield()
        {
            if (_shieldSource == null || _shieldSource.Handle == IntPtr.Zero) return;
            if (GetCapture() == _shieldSource.Handle) ReleaseCapture();
            ShowWindow(_shieldSource.Handle, SW_HIDE);
        }

        private void DisposeShotShield()
        {
            if (_shieldSource == null) return;
            try
            {
                HideShotShield();
                _shieldSource.RemoveHook(ShieldWndProc);
                _shieldSource.Dispose();
            }
            catch
            {
            }
            _shieldSource = null;
        }

        private Point OverlayPointFromScreenCursor()
        {
            var sp = System.Windows.Forms.Control.MousePosition;
            return _overlayRoot.PointFromScreen(new Point(sp.X, sp.Y));
        }

        private IntPtr ShieldWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            if (msg == WM_SETCURSOR)
            {
                SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IDC_CROSS)));
                handled = true;
                return new IntPtr(1);
            }
            if (!_shotActive || _inModalDialog)
            {
                HideShotShield();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_LBUTTONDOWN:
                case WM_LBUTTONDBLCLK:
                    FocusMain();
                    ShotMouseDown(OverlayPointFromScreenCursor());
                    if (!_overlayRoot.IsMouseCaptured && _crop.WantsCapture) SetCapture(hwnd);
                    handled = true;
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    ShotMouseMove(OverlayPointFromScreenCursor(), (wParam.ToInt64() & 1) != 0);
                    handled = true;
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    if (GetCapture() == hwnd) ReleaseCapture();
                    ShotMouseUp(OverlayPointFromScreenCursor());
                    handled = true;
                    return IntPtr.Zero;

                case WM_RBUTTONDOWN:
                case WM_RBUTTONDBLCLK:
                    if (GetCapture() == hwnd) ReleaseCapture();
                    FocusMain();
                    ShotBack();
                    handled = true;
                    return IntPtr.Zero;

                case WM_MOUSEWHEEL:
                    if (_player != null && _player.HasVideo)
                    {
                        short wheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        int notches = -(wheelDelta / 120);
                        if (notches == 0) notches = wheelDelta > 0 ? -1 : 1;
                        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) _nav.StepSeconds(notches);
                        else _nav.StepFrames(notches);
                        UpdateTimeUi(true);
                    }
                    handled = true;
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    FocusMain();
                    handled = true;
                    return IntPtr.Zero;

                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    if (ProcessRawKeyMessage(_mainHwnd, msg, wParam, lParam))
                        handled = true;
                    return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        private static extern int ImmGetVirtualKey(IntPtr hWnd);
    }
}
