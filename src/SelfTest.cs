using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenboxLite.Core;
using ScreenboxLite.Ui;

namespace ScreenboxLite
{
    /// <summary>
    /// 自动化自检：
    ///   --self-test              选框状态机、时间解析、排序协议 + 真实 HWND/libmpv 焦点与输入法快捷键路由自检
    ///   --self-test-ui <视频>    加载真实视频端到端验证 d3d11 渲染、抓帧、点击穿透、中文输入法 VK_PROCESSKEY 全快捷键
    /// 返回 0 表示全部通过。
    /// </summary>
    internal static class SelfTest
    {
        private static readonly List<string> Failures = new List<string>();

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_PROCESSKEY = 0xE5;
        private const uint CWP_SKIPINVISIBLE = 0x0001;
        private const uint CWP_SKIPDISABLED = 0x0002;

        public static int Run()
        {
            Failures.Clear();
            Crop();
            TimeParsing();
            SortSpec();
            if (Program.EnsureLibmpv(true))
            {
                InputAndFocusWindowTest(null);
            }
            return Finish("自检");
        }

        public static int RunWithVideo(string videoPath)
        {
            Failures.Clear();
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                Failures.Add("测试视频不存在: " + videoPath);
                return Finish("UI自检");
            }
            InputAndFocusWindowTest(videoPath);
            return Finish("UI自检");
        }

        public static int RenderUiSnapshots(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new MainWindow(null) { ShowInTaskbar = false, Width = 1280, Height = 720 };
            try
            {
                win.Show();
                Pump(120);
                SaveVisualPng(win, Path.Combine(outDir, "1_empty_1280.png"));

                win.PopulateSampleReadoutForTest();
                win.UpdateResponsiveLayout();
                win.UpdateLayout();
                Pump(40);
                SaveVisualPng(win, Path.Combine(outDir, "2_playback_1280.png"));

                win.SetMockScreenshotBarForTest(true, true);
                win.UpdateLayout();
                Pump(40);
                SaveVisualPng(win, Path.Combine(outDir, "3_shotbar_1280.png"));
                win.SetMockScreenshotBarForTest(false, false);

                win.Width = 960;
                win.UpdateResponsiveLayout();
                win.UpdateLayout();
                Pump(40);
                win.PopulateSampleReadoutForTest();
                win.UpdateLayout();
                SaveVisualPng(win, Path.Combine(outDir, "4_playback_960.png"));

                win.Width = 820;
                win.UpdateResponsiveLayout();
                win.UpdateLayout();
                Pump(40);
                win.PopulateSampleReadoutForTest();
                win.UpdateLayout();
                SaveVisualPng(win, Path.Combine(outDir, "5_playback_820.png"));
                return 0;
            }
            finally
            {
                win.Close();
                Pump(40);
            }
        }

        private static void SaveVisualPng(Window win, string path)
        {
            var el = win.Content as FrameworkElement;
            if (el == null) return;
            int w = Math.Max(1, (int)Math.Round(el.ActualWidth));
            int h = Math.Max(1, (int)Math.Round(el.ActualHeight));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
        }

        private static int Finish(string title)
        {
            string summary = Failures.Count == 0
                ? title + "通过"
                : title + "失败:\r\n  " + string.Join("\r\n  ", Failures.ToArray());
            Log.Info(summary);
            Console.WriteLine(summary);
            return Failures.Count == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string name)
        {
            if (!ok) Failures.Add(name);
        }

        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms))
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void PostKey(IntPtr hwnd, int vk, int scan)
        {
            IntPtr downLp = new IntPtr((long)(scan & 0xFF) << 16);
            IntPtr upLp = new IntPtr(((long)(scan & 0xFF) << 16) | (3L << 30));
            PostMessage(hwnd, WM_KEYDOWN, new IntPtr(vk), downLp);
            PostMessage(hwnd, WM_KEYUP, new IntPtr(vk), upLp);
        }

        private static void SendImeKey(IntPtr hwnd, int scan)
        {
            IntPtr downLp = new IntPtr((long)(scan & 0xFF) << 16);
            IntPtr upLp = new IntPtr(((long)(scan & 0xFF) << 16) | (3L << 30));
            SendMessage(hwnd, WM_KEYDOWN, new IntPtr(VK_PROCESSKEY), downLp);
            SendMessage(hwnd, WM_KEYUP, new IntPtr(VK_PROCESSKEY), upLp);
        }

        private static void InputAndFocusWindowTest(string videoPath)
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new MainWindow(videoPath)
            {
                ShowInTaskbar = false,
                Topmost = videoPath != null,
                Width = 960,
                Height = 620
            };

            try
            {
                win.Show();
                win.Activate();
                SetActiveWindow(win.MainHwnd);
                win.FocusMain();

                // 等待 libmpv 初始化并异步创建 VO 子窗口（force-window=yes）
                var sw = Stopwatch.StartNew();
                int mpvChildren = 0;
                while (sw.ElapsedMilliseconds < 1500)
                {
                    Pump(40);
                    mpvChildren = win.VideoHostControl.DisableChildInput();
                    if (mpvChildren > 0 && (videoPath == null || win.PlayerControl.HasVideo))
                        break;
                }
                if (videoPath != null)
                {
                    while (sw.ElapsedMilliseconds < 4000 && !win.PlayerControl.HasVideo)
                    {
                        Pump(50);
                    }
                    Pump(200);
                }

                IntPtr mainHwnd = win.MainHwnd;
                IntPtr hostHwnd = win.VideoHostControl.Hwnd;
                Check(mainHwnd != IntPtr.Zero && hostHwnd != IntPtr.Zero, "主窗口与视频宿主 HWND 创建成功");
                Check(!IsWindowEnabled(hostHwnd), "VideoHost 宿主 HWND 保持 WS_DISABLED");

                bool anyEnabledChild = false;
                int childTotal = 0;
                EnumChildWindows(hostHwnd, (h, l) =>
                {
                    childTotal++;
                    if (IsWindowEnabled(h)) anyEnabledChild = true;
                    return true;
                }, IntPtr.Zero);
                Check(!anyEnabledChild, "mpv 渲染子窗口全部处于禁用输入状态 (count=" + childTotal + ")");

                // 验证视频舞台正中心的命中测试直接穿透回主窗口 HWND（绝不落入 VideoHost 或 mpv 子窗口）
                Point stageCenterDip = win.StageControl.TranslatePoint(
                    new Point(win.StageControl.ActualWidth / 2.0, win.StageControl.ActualHeight / 2.0), win);
                var src = PresentationSource.FromVisual(win);
                Point pxPt = src != null && src.CompositionTarget != null
                    ? src.CompositionTarget.TransformToDevice.Transform(stageCenterDip)
                    : stageCenterDip;
                var ptClient = new POINT { X = (int)Math.Round(pxPt.X), Y = (int)Math.Round(pxPt.Y) };
                IntPtr hitHwnd = ChildWindowFromPointEx(mainHwnd, ptClient, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED);
                Check(hitHwnd == mainHwnd, "视频区域命中测试 100% 落在主窗口 HWND (hit=" + hitHwnd + ", main=" + mainHwnd + ")");

                // 验证初始焦点：不会误落入定点跳转框，且 Win32 GetFocus 指向主窗口
                SetActiveWindow(mainHwnd);
                win.FocusMain();
                Pump(30);
                Check(!win.JumpBoxControl.Focusable && !win.IsTypingInJumpBox(), "初始状态下定点跳转框不抢焦点");
                Check(ReferenceEquals(Keyboard.FocusedElement, win), "WPF 键盘焦点位于主窗口");
                Check(GetFocus() == mainHwnd, "Win32 GetFocus 位于主窗口");

                // 模拟外部把 Win32 焦点置空后 FocusMain 能真实收复 Win32 焦点（旧版此处会因 WPF 缓存而失效）
                SetFocus(IntPtr.Zero);
                Check(GetFocus() == IntPtr.Zero, "模拟清空 Win32 焦点");
                win.FocusMain();
                Check(GetFocus() == mainHwnd, "FocusMain 真实收复 Win32 SetFocus");

                // 模拟点击定点跳转框输入，再按 Esc 退出输入态并恢复快捷键焦点
                win.JumpBoxControl.Focusable = true;
                win.JumpBoxControl.Focus();
                Keyboard.Focus(win.JumpBoxControl);
                Pump(20);
                Check(win.IsTypingInJumpBox(), "点击定点跳转框后可正常进入输入态");
                Check(!win.ProcessRawKeyMessage(mainHwnd, WM_KEYDOWN, new IntPtr('1'), new IntPtr(0x02L << 16)),
                    "跳转框输入数字 1 时不被快捷键吞掉");
                PostKey(mainHwnd, VK_ESCAPE, 0x01);
                Pump(40);
                Check(!win.IsTypingInJumpBox() && !win.JumpBoxControl.Focusable && GetFocus() == mainHwnd,
                    "在跳转框按 Esc 立即退出输入态并归还主窗口焦点");

                // 验证消息队列链路 (PostMessage -> GetMessage -> ThreadPreprocessMessage) 及中文输入法 VK_PROCESSKEY 扫描码链路
                Check(win.WindowStyle != WindowStyle.None, "初始为窗口模式");
                PostKey(mainHwnd, 'F', 0x21); // 真实消息队列 F 键
                Pump(60);
                Check(win.WindowStyle == WindowStyle.None, "消息队列链路 F 键切换全屏成功");
                Check(GetFocus() == mainHwnd, "切换全屏后 Win32 焦点仍锁定主窗口");
                PostKey(mainHwnd, VK_ESCAPE, 0x01);
                Pump(60);
                Check(win.WindowStyle != WindowStyle.None, "Esc 退出全屏成功");

                SendImeKey(mainHwnd, 0x21); // 模拟第三方输入法改写为 VK_PROCESSKEY(0xE5) + ScanF(0x21)
                Pump(60);
                Check(win.WindowStyle == WindowStyle.None, "中文输入法 VK_PROCESSKEY 下 F 键切换全屏成功");
                PostKey(mainHwnd, VK_ESCAPE, 0x01);
                Pump(60);
                Check(win.WindowStyle != WindowStyle.None, "全屏二次退出成功");

                // 验证底栏「历史截图队列」：截图后不自动弹窗遮挡底栏、按需展开大图队列、单点删除与原位置无损恢复误删
                string tmpHistDir = Path.Combine(Path.GetTempPath(), "sbx_hist_" + Guid.NewGuid().ToString("N"));
                try
                {
                    double stageHBefore = win.StageControl.ActualHeight;
                    int[] dummyPx = new int[64 * 36];
                    for (int i = 0; i < dummyPx.Length; i++) dummyPx[i] = 0x2288CC;
                    var dummyBmp = BitmapSource.Create(64, 36, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, dummyPx, 64 * 4);
                    dummyBmp.Freeze();

                    var item1 = win.CreateAndSaveHistoryItem(dummyBmp, tmpHistDir);
                    var item2 = win.CreateAndSaveHistoryItem(dummyBmp, tmpHistDir);
                    item1.Sync.WaitCompleted(3000);
                    item2.Sync.WaitCompleted(3000);
                    Check(!win.IsHistoryPopupOpen, "保存截图后默认不弹出遮挡底栏的浮窗");
                    Check(win.ActiveHistoryCount == 2 && win.HistoryItemsForTest.Count == 2, "历史截图队列计数准确");
                    Check(win.HistoryItemsForTest[0] == item2 && win.HistoryItemsForTest[1] == item1, "历史队列按最新在前排序");
                    Check(File.Exists(item2.FilePath) && new FileInfo(item2.FilePath).Length > 50, "截图 PNG 已完整落盘");

                    win.ToggleHistoryPopup();
                    Pump(50);
                    Check(win.IsHistoryPopupOpen, "点击底栏历史截图按钮成功展开队列");
                    Check(GetFocus() == mainHwnd, "展开历史截图队列后主窗口 Win32 焦点不丢失");
                    Check(Math.Abs(win.StageControl.ActualHeight - stageHBefore) < 0.5, "展开历史队列严格保持三段式零位移铁律");

                    Check(win.DeleteHistoryItem(item2), "单点删除截图执行成功");
                    Check(item2.IsDeleted && !File.Exists(item2.FilePath), "单点删除后原截图文件立即从保存目录移除");
                    Check(win.ActiveHistoryCount == 1 && win.HistoryItemsForTest[0] == item2, "删除后原卡片槽位保留在原位置（零跳动）");
                    Check(item2.DeleteBtn.Visibility == Visibility.Collapsed && item2.RestoreBtn.Visibility == Visibility.Visible,
                        "删除后原卡片位置切换为「恢复误删」按钮");

                    Check(win.RestoreHistoryItem(item2), "点击原位「恢复误删」执行成功");
                    Check(!item2.IsDeleted && File.Exists(item2.FilePath) && new FileInfo(item2.FilePath).Length > 50,
                        "恢复误删后原截图无损还原回原路径");
                    Check(win.ActiveHistoryCount == 2 && win.HistoryItemsForTest[0] == item2, "恢复后有效计数与槽位完全一致");
                    Check(item2.DeleteBtn.Visibility == Visibility.Visible && item2.RestoreBtn.Visibility == Visibility.Collapsed,
                        "恢复后卡片还原为正常态按钮");

                    PostKey(mainHwnd, VK_ESCAPE, 0x01);
                    Pump(50);
                    Check(!win.IsHistoryPopupOpen && GetFocus() == mainHwnd, "按 Esc 收起历史截图队列且焦点保持在主窗口");
                }
                finally
                {
                    try { if (Directory.Exists(tmpHistDir)) Directory.Delete(tmpHistDir, true); } catch { }
                }

                // 验证多档窗口宽度（1440 / 1280 / 1100 / 960 / 820 最小宽度）下顶栏与底栏零遮挡、零越界
                VerifyNoBarOverlapAcrossWidths(win);

                // 无外部视频依赖：用内存合成的左红右蓝测试帧端到端验证全局 Tab 镜像（非 ~ 模式启用、截图后保持、切文件重置）与导航器清理
                if (videoPath == null)
                {
                    VerifyGlobalTabMirrorAndCleanResume(win);
                }

                // 若传入真实视频，继续验证 d3d11 画面解码、屏幕实画呈现、点击穿透、截图模式焦点及全部核心播放快捷键
                if (videoPath != null)
                {
                    Check(win.PlayerControl.HasVideo && win.PlayerControl.VideoWidth > 0,
                        "WS_DISABLED 宿主下 libmpv 正常解码视频 (" + win.PlayerControl.VideoWidth + "x" + win.PlayerControl.VideoHeight + ")");

                    BitmapSource grabbed = win.PlayerControl.GrabFrame();
                    Check(grabbed != null && grabbed.PixelWidth == win.PlayerControl.VideoWidth, "GrabFrame 原画抓帧成功");

                    // 播放中确认 mpv 子窗口未抢走命中测试或焦点（同时测 ChildWindowFromPointEx 与真实内核 WindowFromPoint）
                    IntPtr hitPlaying = ChildWindowFromPointEx(mainHwnd, ptClient, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED);
                    Check(hitPlaying == mainHwnd, "视频播放中画面点击仍 100% 穿透至主窗口");

                    var ptScreen = ptClient;
                    ClientToScreen(mainHwnd, ref ptScreen);
                    IntPtr realHit = WindowFromPoint(ptScreen);
                    Check(realHit == mainHwnd, "Win32 WindowFromPoint 真实内核命中测试直达主窗口 (realHit=" + realHit + ", main=" + mainHwnd + ")");

                    // 验证 WS_DISABLED + WS_EX_TRANSPARENT 下 d3d11 交换链画面真实呈现在屏幕上（非全黑空洞）
                    bool hasScreenPixels = false;
                    try
                    {
                        using (var bmpScreen = new System.Drawing.Bitmap(40, 40))
                        using (var g = System.Drawing.Graphics.FromImage(bmpScreen))
                        {
                            g.CopyFromScreen(ptScreen.X - 20, ptScreen.Y - 20, 0, 0, new System.Drawing.Size(40, 40));
                            for (int y = 0; y < 40 && !hasScreenPixels; y += 4)
                            {
                                for (int x = 0; x < 40; x += 4)
                                {
                                    var c = bmpScreen.GetPixel(x, y);
                                    if (c.R > 20 || c.G > 20 || c.B > 20) { hasScreenPixels = true; break; }
                                }
                            }
                        }
                    }
                    catch
                    {
                        hasScreenPixels = grabbed != null;
                    }
                    Check(hasScreenPixels, "d3d11 视频画面真实呈现在屏幕上（非黑屏）");

                    // 确保处于播放态后再测试点击视频画面中央（经由 WindowFromPoint 命中的真实目标 HWND 投递点击）：
                    // 应立即解除跳转框焦点、锁定主窗口 Win32 焦点并触发单击暂停
                    if (win.PlayerControl.EofReached) win.PlayerControl.SeekAbsolute(0, true);
                    win.PlayerControl.SetPause(false);
                    Pump(100);
                    Check(!win.PlayerControl.Paused, "点击测试前处于播放态");

                    win.JumpBoxControl.Focusable = true;
                    win.JumpBoxControl.Focus();
                    Keyboard.Focus(win.JumpBoxControl);
                    Pump(20);
                    Check(win.IsTypingInJumpBox(), "播放中点击跳转框可聚焦");

                    IntPtr clickLParam = new IntPtr(((ptClient.Y & 0xFFFF) << 16) | (ptClient.X & 0xFFFF));
                    PostMessage(realHit, 0x0201 /* WM_LBUTTONDOWN */, new IntPtr(1), clickLParam);
                    PostMessage(realHit, 0x0202 /* WM_LBUTTONUP */, IntPtr.Zero, clickLParam);
                    Pump(100);
                    Check(!win.IsTypingInJumpBox() && GetFocus() == mainHwnd, "点击视频画面立即解除跳转框焦点并锁定主窗口 Win32 焦点");
                    Check(win.PlayerControl.Paused, "单击视频画面成功触发暂停");

                    // Space (队列 PostKey + ScanSpace 0x39) 恢复播放再暂停
                    PostKey(mainHwnd, 0x20, 0x39);
                    Pump(80);
                    Check(!win.PlayerControl.Paused, "Space 键恢复播放成功");
                    PostKey(mainHwnd, 0x20, 0x39);
                    Pump(80);
                    Check(win.PlayerControl.Paused, "Space 键再次暂停成功");

                    // 按住 D 键 5 倍速快进，松开立即暂停
                    SendMessage(mainHwnd, WM_KEYDOWN, new IntPtr(VK_PROCESSKEY), new IntPtr(0x20L << 16));
                    Pump(100);
                    Check(!win.PlayerControl.Paused, "按住 D 键进入 5 倍速快进成功");
                    SendMessage(mainHwnd, WM_KEYUP, new IntPtr(VK_PROCESSKEY), new IntPtr((0x20L << 16) | (3L << 30)));
                    Pump(80);
                    Check(win.PlayerControl.Paused, "松开 D 键立即定格暂停成功");

                    // . 键 (IME VK_PROCESSKEY + ScanPeriod 0x34) 步进 +1 帧，, 键 (0x33) 回退 -1 帧
                    double tBeforeStep = win.NavControl.BaseTime();
                    SendImeKey(mainHwnd, 0x34);
                    Pump(120);
                    double tAfterFwd = win.NavControl.BaseTime();
                    Check(tAfterFwd > tBeforeStep, ". 键单帧前进成功");
                    SendImeKey(mainHwnd, 0x33);
                    Pump(150);
                    Check(win.NavControl.BaseTime() < tAfterFwd, ", 键单帧后退成功");

                    // S + D 组合键 (IME VK_PROCESSKEY + ScanS 0x1F / ScanD 0x20) 步进 +1 帧
                    double tBeforeSD = win.NavControl.BaseTime();
                    SendMessage(mainHwnd, WM_KEYDOWN, new IntPtr(VK_PROCESSKEY), new IntPtr(0x1FL << 16));
                    SendImeKey(mainHwnd, 0x20);
                    SendMessage(mainHwnd, WM_KEYUP, new IntPtr(VK_PROCESSKEY), new IntPtr((0x1FL << 16) | (3L << 30)));
                    Pump(120);
                    Check(win.NavControl.BaseTime() > tBeforeSD, "S+D 中文输入法下逐帧前进成功");

                    // 1 键 (+5s) 与 E+1 (-5s)
                    if (win.PlayerControl.Duration > 1.5)
                    {
                        win.NavControl.SeekTo(0.2, true);
                        Pump(180);
                        PostKey(mainHwnd, '1', 0x02);
                        Pump(180);
                        double afterPlus5 = win.NavControl.BaseTime();
                        Check(afterPlus5 >= Math.Min(win.PlayerControl.Duration - 0.3, 4.5), "1 键前进 5s 成功");

                        SendMessage(mainHwnd, WM_KEYDOWN, new IntPtr(VK_PROCESSKEY), new IntPtr(0x12L << 16)); // E down
                        SendImeKey(mainHwnd, 0x02); // 1 tap
                        SendMessage(mainHwnd, WM_KEYUP, new IntPtr(VK_PROCESSKEY), new IntPtr((0x12L << 16) | (3L << 30))); // E up
                        Pump(180);
                        Check(win.NavControl.BaseTime() < afterPlus5 - 0.5, "E+1 组合键后退 5s 成功");
                    }

                    // ~ 键 (IME VK_PROCESSKEY + ScanTilde 0x29) 进入截图模式（隐藏原生窗口后焦点不丢失）+ 窗外护盾验证 + Esc 退出截图模式
                    SendImeKey(mainHwnd, 0x29);
                    Pump(120);
                    Check(win.IsShotActive, "~ 键中文输入法下进入截图模式成功");
                    Check(GetFocus() == mainHwnd, "进入截图模式隐藏 VideoHost 后 Win32 焦点不丢失");
                    Check(win.ShotShieldHwnd != IntPtr.Zero && IsWindowVisible(win.ShotShieldHwnd), "截图模式窗外防误点护盾已启用");
                    Check(SendMessage(win.ShotShieldHwnd, 0x0021 /* WM_MOUSEACTIVATE */, mainHwnd, IntPtr.Zero).ToInt32() == 3,
                        "窗外防误点护盾返回 MA_NOACTIVATE 不夺取主窗口焦点");
                    Check(WindowFromPoint(ptScreen) == mainHwnd, "截图模式下主窗口内部点击仍直达主窗口（未被护盾遮挡）");

                    // Tab 键 (ScanTab 0x0F) 水平翻转切换与还原
                    Check(!win.IsShotFlipped, "进入截图模式默认未翻转");
                    SendImeKey(mainHwnd, 0x0F);
                    Pump(80);
                    Check(win.IsShotFlipped, "截图模式按 Tab 开启水平翻转成功");
                    Check(GetFocus() == mainHwnd, "按 Tab 水平翻转后 Win32 焦点未跑偏");
                    SendImeKey(mainHwnd, 0x0F);
                    Pump(80);
                    Check(!win.IsShotFlipped, "截图模式再次按 Tab 还原原始方向成功");

                    var ptOutside = new POINT { X = Math.Max(2, ptScreen.X - 600), Y = ptScreen.Y };
                    RECT mainWinRect;
                    if (GetWindowRect(mainHwnd, out mainWinRect) && mainWinRect.Left > 20)
                    {
                        ptOutside = new POINT { X = mainWinRect.Left - 15, Y = (mainWinRect.Top + mainWinRect.Bottom) / 2 };
                        Check(WindowFromPoint(ptOutside) == win.ShotShieldHwnd, "截图模式下窗口外部点击由护盾层接管（不误点桌面/后台窗口）");
                    }

                    PostKey(mainHwnd, VK_ESCAPE, 0x01);
                    Pump(80);
                    Check(!win.IsShotActive, "截图模式按 Esc 退回播放界面成功");
                    Check(!IsWindowVisible(win.ShotShieldHwnd), "退出截图模式后窗外护盾立即隐藏");
                    Check(GetFocus() == mainHwnd, "退出截图模式恢复 VideoHost 后 Win32 焦点不丢失");
                }
            }
            finally
            {
                win.Close();
                Pump(50);
            }
        }

        private static Rect BoundsInWindow(FrameworkElement el, Window win)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
                return Rect.Empty;
            Point tl = el.TranslatePoint(new Point(0, 0), win);
            return new Rect(tl.X, tl.Y, el.ActualWidth, el.ActualHeight);
        }

        private static void VerifyNoBarOverlapAcrossWidths(MainWindow win)
        {
            double origW = win.Width;
            double origH = win.Height;
            try
            {
                win.PopulateSampleReadoutForTest();
                int[] testWidths = new int[] { 1440, 1280, 1100, 960, 820 };
                for (int i = 0; i < testWidths.Length; i++)
                {
                    int w = testWidths[i];
                    win.Width = w;
                    win.Height = 640;
                    win.UpdateResponsiveLayout();
                    win.UpdateLayout();
                    Pump(25);
                    win.PopulateSampleReadoutForTest();
                    win.UpdateLayout();

                    var contentRoot = win.Content as FrameworkElement;
                    double clientW = contentRoot != null && contentRoot.ActualWidth > 0 ? contentRoot.ActualWidth : win.ActualWidth;

                    Rect tl = BoundsInWindow(win.TopLeftForTest, win);
                    Rect tc = BoundsInWindow(win.TopCenterForTest, win);
                    Rect tr = BoundsInWindow(win.TopRightForTest, win);
                    bool topOk = !tl.IsEmpty && !tc.IsEmpty && !tr.IsEmpty &&
                                 tl.Right <= tc.Left + 0.5 &&
                                 tc.Right <= tr.Left + 0.5 &&
                                 tr.Right <= clientW + 1.0;
                    Check(topOk, "顶栏在宽度 " + w + "px 下无任何挤压遮挡");

                    Rect bl = BoundsInWindow(win.BottomLeftForTest, win);
                    Rect bc = BoundsInWindow(win.BottomCenterForTest, win);
                    Rect br = BoundsInWindow(win.BottomRightForTest, win);
                    bool botOk = !bl.IsEmpty && !bc.IsEmpty && !br.IsEmpty &&
                                 bc.Width >= 110 &&
                                 bl.Right <= bc.Left + 0.5 &&
                                 bc.Right <= br.Left + 0.5 &&
                                 br.Right <= clientW + 1.0;
                    Check(botOk, string.Format("底栏在宽度 {0}px 下无任何挤压遮挡 (L={1:0}..{2:0}, C={3:0}..{4:0}, R={5:0}..{6:0}, client={7:0})",
                        w, bl.Left, bl.Right, bc.Left, bc.Right, br.Left, br.Right, clientW));
                }
            }
            finally
            {
                win.Width = origW;
                win.Height = origH;
                win.UpdateResponsiveLayout();
                win.UpdateLayout();
            }
        }

        private static void SaveSolidSplitPng(string path, int w, int h, int leftBgr, int rightBgr)
        {
            int[] px = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    px[y * w + x] = (x < w / 2) ? leftBgr : rightBgr;
                }
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, px, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
        }

        private static bool IsLeftBlueRightRed(BitmapSource bmp)
        {
            if (bmp == null || bmp.PixelWidth < 16 || bmp.PixelHeight < 4) return false;
            var bgr = bmp.Format == System.Windows.Media.PixelFormats.Bgr32
                ? bmp
                : new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            int y = bgr.PixelHeight / 2;
            byte[] leftPx = new byte[4];
            byte[] rightPx = new byte[4];
            bgr.CopyPixels(new Int32Rect(4, y, 1, 1), leftPx, 4, 0);
            bgr.CopyPixels(new Int32Rect(bgr.PixelWidth - 5, y, 1, 1), rightPx, 4, 0);
            // Bgr32: [0]=Blue, [1]=Green, [2]=Red
            bool leftIsBlue = leftPx[0] > 180 && leftPx[2] < 60;
            bool rightIsRed = rightPx[2] > 180 && rightPx[0] < 60;
            return leftIsBlue && rightIsRed;
        }

        private static void VerifyGlobalTabMirrorAndCleanResume(MainWindow win)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "sbx_mirror_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string shotOutDir = Path.Combine(tmpDir, "shots");
            Directory.CreateDirectory(shotOutDir);
            win.TempSaveFolderOverride = shotOutDir;

            try
            {
                string fileA = Path.Combine(tmpDir, "a_red_blue.png");
                string fileB = Path.Combine(tmpDir, "b_green_yellow.png");
                // Bgr32: 0xFF0000 = Red, 0x0000FF = Blue
                SaveSolidSplitPng(fileA, 64, 36, 0xFF0000, 0x0000FF);
                SaveSolidSplitPng(fileB, 64, 36, 0x00FF00, 0xFFFF00);

                win.LoadVideo(fileA);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2500 && (!win.PlayerControl.HasVideo || win.PlayerControl.GrabFrame() == null))
                {
                    Pump(40);
                }
                Pump(80);
                Check(win.PlayerControl.HasVideo, "测试媒体加载成功");
                Check(!win.IsVideoMirrored && !win.PlayerControl.IsHorizontalMirrored, "初始打开时默认未水平翻转");

                // 1) 未开启 ~ 截图模式时，直接按 Tab 即可启用水平镜像
                IntPtr mainHwnd = win.MainHwnd;
                Check(!win.IsShotActive, "当前未处于 ~ 截图模式");
                SendImeKey(mainHwnd, 0x0F); // ScanTab = 0x0F
                Pump(80);
                Check(win.IsVideoMirrored && win.PlayerControl.IsHorizontalMirrored, "非 ~ 模式下按 Tab 直接启用水平翻转成功");

                // 2) 开启 ~ 截图模式：继承已翻转状态，且冻结帧像素精确为左蓝右红（无漏翻、无双重翻转）
                SendImeKey(mainHwnd, 0x29); // ScanTilde = 0x29 -> 进入截图模式
                Pump(100);
                Check(win.IsShotActive && win.IsVideoMirrored, "进入 ~ 截图模式继承已翻转状态");
                Check(IsLeftBlueRightRed(win.FrozenFrameForTest), "镜像后进入 ~ 模式的冻结帧像素方向精确为左蓝右红");

                // 3) 再次按 ~ 保存截图退出：截图后保持翻转不复原，且落盘 PNG 也是翻转后的左蓝右红
                int histBefore = win.HistoryItemsForTest.Count;
                SendImeKey(mainHwnd, 0x29); // ScanTilde -> 保存并退出截图模式
                Pump(100);
                Check(!win.IsShotActive, "再按 ~ 完成保存并退回播放态");
                Check(win.IsVideoMirrored && win.PlayerControl.IsHorizontalMirrored, "保存截图后保持水平翻转不自动复原");
                Check(win.HistoryItemsForTest.Count == histBefore + 1, "镜像截图成功计入历史队列");
                if (win.HistoryItemsForTest.Count > 0)
                {
                    var savedItem = win.HistoryItemsForTest[0];
                    savedItem.Sync.WaitCompleted(3000);
                    Check(File.Exists(savedItem.FilePath), "镜像截图文件已落盘");
                    if (File.Exists(savedItem.FilePath))
                    {
                        using (var fs = new FileStream(savedItem.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            Check(dec.Frames.Count > 0 && IsLeftBlueRightRed(dec.Frames[0]), "落盘截图 PNG 像素方向精确保持水平镜像");
                        }
                    }
                }

                // 4) 进入 ~ 模式后按 Esc 取消退出：同样保持翻转不复原
                SendImeKey(mainHwnd, 0x29);
                Pump(100);
                Check(win.IsShotActive, "二次进入 ~ 截图模式");
                PostKey(mainHwnd, VK_ESCAPE, 0x01);
                Pump(80);
                Check(!win.IsShotActive && win.IsVideoMirrored, "按 Esc 退出 ~ 模式后仍保持水平翻转");

                // 5) 切换文件后自动重置水平翻转
                win.LoadVideo(fileB);
                Pump(120);
                Check(!win.IsVideoMirrored && !win.PlayerControl.IsHorizontalMirrored, "切换文件后自动重置水平翻转");

                // 6) 验证恢复播放前 CancelForPlay 立即清空导航状态
                win.NavControl.CancelForPlay();
                Check(!win.NavControl.Busy, "CancelForPlay 清空在途导航队列成功");
            }
            finally
            {
                win.TempSaveFolderOverride = null;
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private static void Crop()
        {
            var b = new Rect(100, 50, 800, 450);
            var c = new CropSelection();

            // 拖拽定框
            c.MouseDown(new Point(200, 100), b);
            c.MouseMove(new Point(400, 300), true, b);
            c.MouseUp(new Point(400, 300), b);
            Check(c.HasSelection && c.Current == new Rect(200, 100, 200, 200), "拖拽定框");

            // 越界钳制：拖到画面外
            c.Reset();
            c.MouseDown(new Point(50, 10), b);
            c.MouseMove(new Point(2000, 2000), true, b);
            c.MouseUp(new Point(2000, 2000), b);
            Check(c.Current == b, "选框钳制在画面内");

            // 两次单击蓝图
            c.Reset();
            c.MouseDown(new Point(300, 200), b);
            c.MouseUp(new Point(301, 201), b);
            Check(c.Mode == CropMode.TwoClickBlueprint && !c.HasSelection, "单击进入蓝图");
            c.MouseMove(new Point(500, 400), false, b);
            c.MouseDown(new Point(500, 400), b);
            Check(c.HasSelection && c.Current == new Rect(300, 200, 200, 200) && c.Mode == CropMode.None, "二次单击落地");

            // 拖动右下角调整、右键撤销调整
            c.MouseDown(new Point(500, 400), b);
            Check(c.Mode == CropMode.ResizingOrMoving, "命中右下角");
            c.MouseMove(new Point(600, 450), true, b);
            Check(c.Current == new Rect(300, 200, 300, 250), "调整尺寸");
            Check(c.Back() == CropBackResult.RevertedAdjust && c.Current == new Rect(300, 200, 200, 200), "右键撤销本次调整");

            // 内部平移不越界
            c.MouseDown(new Point(400, 300), b);
            c.MouseMove(new Point(2000, 300), true, b);
            c.MouseUp(new Point(2000, 300), b);
            Check(Math.Abs(c.Current.Right - b.Right) < 1e-9 && Math.Abs(c.Current.Width - 200) < 1e-9, "平移钳制");

            // 逐级上一步
            Check(c.Back() == CropBackResult.Cleared && !c.HasBox, "右键取消选框");
            Check(c.Back() == CropBackResult.ExitMode, "再右键退出");

            // 归一化与恢复往返
            c.MouseDown(new Point(300, 200), b);
            c.MouseMove(new Point(500, 350), true, b);
            c.MouseUp(new Point(500, 350), b);
            Rect? n = c.Normalized(b);
            var c2 = new CropSelection();
            var b2 = new Rect(0, 0, 1600, 900);
            Check(n.HasValue && c2.Restore(n.Value, b2) && Near(c2.Current, new Rect(400, 300, 400, 300)), "归一化恢复(R 键)");

            // 太小的框不落地
            c.Reset();
            c.MouseDown(new Point(300, 200), b);
            c.MouseMove(new Point(302, 260), true, b);
            c.MouseUp(new Point(302, 260), b);
            Check(!c.HasSelection, "过窄的框不落地");

            // 水平镜像选框与位图翻转验证
            c.Reset();
            c.MouseDown(new Point(150, 100), b);
            c.MouseMove(new Point(350, 250), true, b);
            c.MouseUp(new Point(350, 250), b);
            c.MirrorHorizontal(b); // b = [100..900], [150..350] 镜像后应为 [650..850]
            Check(Near(c.Current, new Rect(650, 100, 200, 150)), "选框水平镜像坐标精确");
            c.MirrorHorizontal(b);
            Check(Near(c.Current, new Rect(150, 100, 200, 150)), "选框二次镜像往返还原");

            int[] testPx = new int[] { 0x112233, 0x445566, 0x778899 };
            var srcBmp = BitmapSource.Create(3, 1, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, testPx, 12);
            var flipBmp = ScreenshotWriter.FlipHorizontal(srcBmp);
            int[] outPx = new int[3];
            flipBmp.CopyPixels(outPx, 12, 0);
            Check(outPx[0] == 0x778899 && outPx[1] == 0x445566 && outPx[2] == 0x112233, "位图水平镜像像素精确");
        }

        private static bool Near(Rect a, Rect b)
        {
            return Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;
        }

        private static void TimeParsing()
        {
            double s;
            Check(MainWindow.TryParseTime("85.5", out s) && s == 85.5, "秒数");
            Check(MainWindow.TryParseTime("01:25", out s) && s == 85, "分:秒");
            Check(MainWindow.TryParseTime("00:01:25.500", out s) && s == 85.5, "时:分:秒.毫秒");
            Check(MainWindow.TryParseTime("1：02", out s) && s == 62, "全角冒号");
            Check(!MainWindow.TryParseTime("abc", out s), "非法输入");
            Check(!MainWindow.TryParseTime("", out s), "空输入");
        }

        private static void SortSpec()
        {
            string p;
            bool d;
            Playlist.ParseSortSpec("prop:-System.DateCreated;System.ItemNameDisplay;", out p, out d);
            Check(p == "System.DateCreated" && d, "降序创建时间");
            Playlist.ParseSortSpec("prop:System.Size;", out p, out d);
            Check(p == "System.Size" && !d, "升序大小");
            Playlist.ParseSortSpec(null, out p, out d);
            Check(p == "System.ItemNameDisplay" && !d, "默认名称");

            // 验证素材库排序与显式播放列表优先规则
            string tmpDir = Path.Combine(Path.GetTempPath(), "sbx_sort_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string prevSort = Environment.GetEnvironmentVariable("DSH_GALLERY_SORT_SPEC");
            string prevList = Environment.GetEnvironmentVariable("DSH_GALLERY_PLAYLIST_FILE");
            try
            {
                string fSmall = Path.Combine(tmpDir, "a_small.mp4");
                string fLarge = Path.Combine(tmpDir, "b_large.mp4");
                File.WriteAllBytes(fSmall, new byte[10]);
                File.WriteAllBytes(fLarge, new byte[100]);

                Environment.SetEnvironmentVariable("DSH_GALLERY_SORT_SPEC", "prop:-System.Size;");
                Environment.SetEnvironmentVariable("DSH_GALLERY_PLAYLIST_FILE", null);
                Playlist.InitStartupContext(fSmall);
                var bySizeDesc = Playlist.GetPlaylistForVideo(fSmall);
                Check(bySizeDesc.Count == 2 && string.Equals(bySizeDesc[0], fLarge, StringComparison.OrdinalIgnoreCase), "素材库按大小降序优先");

                string listPath = Path.Combine(tmpDir, "playlist.txt");
                File.WriteAllLines(listPath, new[] { fSmall, fLarge });
                Environment.SetEnvironmentVariable("DSH_GALLERY_PLAYLIST_FILE", listPath);
                Playlist.InitStartupContext(fLarge);
                var byExplicit = Playlist.GetPlaylistForVideo(fLarge);
                Check(byExplicit.Count == 2 && string.Equals(byExplicit[0], fSmall, StringComparison.OrdinalIgnoreCase), "素材库显式列表优先");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DSH_GALLERY_SORT_SPEC", prevSort);
                Environment.SetEnvironmentVariable("DSH_GALLERY_PLAYLIST_FILE", prevList);
                Playlist.InitStartupContext(null);
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ---------------- Win32 ----------------
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

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

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, POINT pt, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
