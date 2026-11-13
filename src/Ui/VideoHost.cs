using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// mpv 渲染宿主窗口（WPF 中嵌入的原生子窗口）。
    /// 关键点：鼠标/键盘输入全部“穿透”回 WPF——
    ///   1) 本窗口对 WM_NCHITTEST 返回 HTTRANSPARENT（同线程的父窗口接收鼠标）；
    ///   2) mpv 在本窗口内创建的子窗口被禁用（被禁用的窗口不参与命中测试，也拿不到焦点）。
    /// 这样快捷键（含中文输入法下的物理键）、滚轮、点击都由 WPF 统一处理。
    /// </summary>
    internal sealed class VideoHost : HwndHost
    {
        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        private const int WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021, WM_ERASEBKGND = 0x0014;
        private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;

        private static readonly WndProcDelegate ClassProc = StaticWndProc; // 防止被 GC
        private static bool _classRegistered;
        private const string ClassName = "ScreenboxLiteVideoHost";

        public IntPtr Hwnd { get; private set; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            EnsureClass();
            Hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("创建视频宿主窗口失败: " + Marshal.GetLastWin32Error());
            return new HandleRef(this, Hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            DestroyWindow(hwnd.Handle);
            Hwnd = IntPtr.Zero;
        }

        /// <summary>禁用 mpv 创建的子窗口，使输入落回 WPF。返回禁用的窗口数。可重复调用。</summary>
        public int DisableChildInput()
        {
            if (Hwnd == IntPtr.Zero) return 0;
            int n = 0;
            EnumChildWindows(Hwnd, (h, l) =>
            {
                if (IsWindowEnabled(h)) EnableWindow(h, false);
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
            if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("注册窗口类失败: " + Marshal.GetLastWin32Error());
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

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hwnd, bool enable);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int obj);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
