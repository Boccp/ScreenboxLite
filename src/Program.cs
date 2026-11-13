using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using ScreenboxLite.Core;
using ScreenboxLite.Ui;

namespace ScreenboxLite
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string path);

        [STAThread]
        public static int Main(string[] args)
        {
            // 每显示器 DPI 感知 v2：视频与界面按物理像素 1:1 呈现，不被系统缩放模糊
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { /* Win10 1703 以前的系统 */ }

            Log.Init();
            foreach (string a in args)
            {
                if (a == "--verbose") Log.Verbose = true;
            }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log.Error(e.ExceptionObject as Exception, "未处理异常"); Log.Flush(); };

            if (args.Length > 0 && args[0] == "--self-test")
            {
                int code = SelfTest.Run();
                Log.Flush();
                return code;
            }

            if (!EnsureLibmpv()) return 2;
            PreferHighPerformanceGpu();
            string stepMode = Environment.GetEnvironmentVariable("SCREENBOX_STEP_MODE");
            Mpv.MpvPlayer.StepMode sm;
            if (!string.IsNullOrEmpty(stepMode) && Enum.TryParse(stepMode, true, out sm)) Mpv.MpvPlayer.ForwardStepMode = sm;

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error(e.Exception, "界面线程未处理异常");
                e.Handled = true; // 记录后继续运行，避免一次小错误导致丢失工作
            };

            MainWindow win;
            if (args.Length >= 2 && args[0] == "--bench")
            {
                win = new MainWindow(null);
                win.StartBench(args[1], args.Length >= 3 ? args[2] : Path.Combine(AppPaths.DataDir, "bench.txt"));
            }
            else
            {
                string file = null;
                foreach (string a in args)
                {
                    if (!a.StartsWith("--") && File.Exists(a)) { file = a; break; }
                }
                win = new MainWindow(file);
            }
            Log.Info("启动 v" + Assembly.GetExecutingAssembly().GetName().Version);
            app.Run(win);
            Log.Flush();
            return 0;
        }

        /// <summary>libmpv-2.dll 放在 exe 同目录（或 lib\ 子目录）。缺失时给出明确提示。</summary>
        private static bool EnsureLibmpv()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "libmpv-2.dll"))) return true;
            string lib = Path.Combine(dir, "lib");
            if (File.Exists(Path.Combine(lib, "libmpv-2.dll")))
            {
                SetDllDirectory(lib);
                return true;
            }
            MessageBox.Show("缺少播放引擎 libmpv-2.dll。\n请重新运行安装包 ScreenboxSetup.exe 修复安装。\n\n查找位置：" + dir,
                "Screenbox", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        /// <summary>混合显卡笔记本/台式机：让本程序使用独立显卡（与“图形设置→高性能”等效）。</summary>
        private static void PreferHighPerformanceGpu()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                {
                    if (key != null && !string.Equals(key.GetValue(exe) as string, "GpuPreference=2;", StringComparison.Ordinal))
                        key.SetValue(exe, "GpuPreference=2;", RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "设置高性能显卡偏好失败");
            }
        }
    }
}
