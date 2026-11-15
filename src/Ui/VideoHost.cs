using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// mpv 渲染宿主窗口（WPF 中嵌入的原生子窗口）。
    /// 核心设计（彻底杜绝原生子窗口抢占键盘焦点与鼠标点击）：
    ///   1) 宿主 HWND 创建时直接带 WS_DISABLED + WS_EX_NOACTIVATE：
    ///      Win32 内核命中测试（WindowFromPoint / 鼠标消息路由）直接跳过整个子树（含 mpv 异步创建的 VO 子窗口），
    ///      同时不影响 Direct3D 11 / DXGI 交换链正常呈现视频画面；
    ///   2) 拦截 WM_PARENTNOTIFY(WM_CREATE)，在 mpv 子窗口诞生瞬间立即将其禁用（EnableWindow(false)），
    ///      并拦截 WM_ENABLE 防止外部重新启用宿主窗口；
    ///   3) 关闭 WPF HwndHost 默认的 Focusable / IKeyboardInputSink 焦点入口，并在 WM_SETFOCUS / WM_KEY* 上兜底归还主窗口。
    /// </summary>
    internal sealed class VideoHost : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_DISABLED = 0x08000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int GWL_EXSTYLE = -20;

        private const int WM_CREATE = 0x0001;
        private const int WM_SETFOCUS = 0x0007;
        private const int WM_ENABLE = 0x000A;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_PARENTNOTIFY = 0x0210;

        private const int HTTRANSPARENT = -1;
        private const int MA_NOACTIVATE = 3;
        private const uint GA_ROOT = 2;

        private static readonly WndProcDelegate ClassProc = StaticWndProc; // 防止被 GC
        private static bool _classRegistered;
        private const string ClassName = "ScreenboxLiteVideoHost";

        public IntPtr Hwnd { get; private set; }

        public VideoHost()
        {
            Focusable = false;
            KeyboardNavigation.SetIsTabStop(this, false);
            IsHitTestVisible = false;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            EnsureClass();
            Hwnd = CreateWindowEx(
                WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
                ClassName,
                "",
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_DISABLED,
                0, 0, 1, 1,
                hwndParent.Handle,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);
            if (Hwnd == IntPtr.Zero)
                throw new InvalidOperationException("创建视频宿主窗口失败: " + Marshal.GetLastWin32Error());
            return new HandleRef(this, Hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            DestroyWindow(hwnd.Handle);
            Hwnd = IntPtr.Zero;
        }

        // ---- 彻底关闭 HwndHost (IKeyboardInputSink) 的键盘焦点截留 ----

        protected override bool TabIntoCore(TraversalRequest request)
        {
            return false;
        }

        protected override bool HasFocusWithinCore()
        {
            return false;
        }

        protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
        {
            return false;
        }

        protected override bool TranslateCharCore(ref MSG msg, ModifierKeys modifiers)
        {
            return false;
        }

        protected override bool OnMnemonicCore(ref MSG msg, ModifierKeys modifiers)
        {
            return false;
        }

        private static void LockOutInput(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            long want = ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            if (ex != want)
            {
                SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(want));
            }
            if (IsWindowEnabled(h)) EnableWindow(h, false);
        }

        /// <summary>禁用宿主及 mpv 创建的所有子窗口并附加穿透样式，使一切输入稳定留在主窗口。返回扫描到的子窗口总数。</summary>
        public int DisableChildInput()
        {
            if (Hwnd == IntPtr.Zero) return 0;
            LockOutInput(Hwnd);
            int n = 0;
            EnumChildWindows(Hwnd, (h, l) =>
            {
                LockOutInput(h);
                n++;
                return true;
            }, IntPtr.Zero);
            return n;
        }

        private static void EnsureClass()
        {
            if (_classRegistered) return;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ClassProc),
                hInstance = GetModuleHandle(null),
                hbrBackground = GetStockObject(4), // BLACK_BRUSH
                lpszClassName = ClassName
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException("注册窗口类失败: " + Marshal.GetLastWin32Error());
            _classRegistered = true;
        }

        private static IntPtr StaticWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    return new IntPtr(HTTRANSPARENT);
                case WM_MOUSEACTIVATE:
                    return new IntPtr(MA_NOACTIVATE);
                case WM_ENABLE:
                    if (wParam != IntPtr.Zero)
                    {
                        EnableWindow(hwnd, false);
                        return IntPtr.Zero;
                    }
                    break;
                case WM_PARENTNOTIFY:
                    if (((int)wParam.ToInt64() & 0xFFFF) == WM_CREATE && lParam != IntPtr.Zero)
                    {
                        LockOutInput(lParam);
                    }
                    break;
                case WM_SETFOCUS:
                    {
                        IntPtr root = GetAncestor(hwnd, GA_ROOT);
                        if (root != IntPtr.Zero && root != hwnd) SetFocus(root);
                        return IntPtr.Zero;
                    }
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                    {
                        IntPtr root = GetAncestor(hwnd, GA_ROOT);
                        if (root != IntPtr.Zero && root != hwnd)
                            return SendMessage(root, msg, wParam, lParam);
                        break;
                    }
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // ---------------- Win32 ----------------
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hwnd, bool enable);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int obj);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
