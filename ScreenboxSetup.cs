using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenboxSetup
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string arg0 = args[0].Trim().ToLowerInvariant();
                if (arg0 == "/uninstall" || arg0 == "-uninstall" || arg0 == "--uninstall")
                {
                    RunUninstaller(silent: false);
                    return 0;
                }
                if (arg0 == "/silent" || arg0 == "/s" || arg0 == "-s")
                {
                    string targetDir = DetectExistingInstallDir() ?? GetDefaultInstallDir();
                    PerformInstall(targetDir, true, true, true, true, false);
                    return 0;
                }
            }

            var app = new Application();
            var win = new SetupWindow();
            app.Run(win);
            return 0;
        }

        public static readonly string[] VideoExtensions = new string[]
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp"
        };

        public const string AppId = "ScreenboxLite";
        public const string AppDisplayName = "Screenbox (截图增强版)";
        public const string AppVersion = "1.4.0";
        public const string ProgId = "ScreenboxLite.Video";

        public static string GetDefaultInstallDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "ScreenboxLite");
        }

        public static string DetectExistingInstallDir()
        {
            try
            {
                using (RegistryKey uninstKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId))
                {
                    if (uninstKey != null)
                    {
                        string loc = uninstKey.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc))
                        {
                            string cleanLoc = loc.Trim().Trim('"');
                            if (Directory.Exists(cleanLoc) || File.Exists(Path.Combine(cleanLoc, "ScreenboxLite.exe")))
                            {
                                return cleanLoc;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                using (RegistryKey cmdKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
                {
                    if (cmdKey != null)
                    {
                        string cmd = cmdKey.GetValue("") as string;
                        if (!string.IsNullOrWhiteSpace(cmd) && cmd.StartsWith("\""))
                        {
                            int endQuote = cmd.IndexOf('"', 1);
                            if (endQuote > 1)
                            {
                                string exePath = cmd.Substring(1, endQuote - 1);
                                string dir = Path.GetDirectoryName(exePath);
                                if (!string.IsNullOrWhiteSpace(dir) && File.Exists(exePath))
                                {
                                    return dir;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            string defDir = GetDefaultInstallDir();
            if (File.Exists(Path.Combine(defDir, "ScreenboxLite.exe")))
            {
                return defDir;
            }

            return null;
        }

        public static string DetectExistingVersion()
        {
            try
            {
                using (RegistryKey uninstKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId))
                {
                    if (uninstKey != null)
                    {
                        return uninstKey.GetValue("DisplayVersion") as string;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public static void RefreshShellAssociations()
        {
            try
            {
                const int SHCNE_ASSOCCHANGED = 0x08000000;
                const uint SHCNF_IDLIST = 0x0000;
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
            }
        }

        public static void CreateShortcut(string shortcutPath, string targetExePath, string workingDir, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExePath });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetExePath + ",0" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch
            {
            }
        }

        public static string PerformInstall(
            string installDir,
            bool desktopShortcut,
            bool startMenuShortcut,
            bool contextMenu,
            bool fileAssoc,
            bool setAsDefaultPlayer)
        {
            // Close running instances of ScreenboxLite before overwriting
            try
            {
                foreach (var proc in Process.GetProcessesByName("ScreenboxLite"))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(1500);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            Directory.CreateDirectory(installDir);
            string targetExe = Path.Combine(installDir, "ScreenboxLite.exe");
            string uninstallExe = Path.Combine(installDir, "Uninstall.exe");

            // Extract embedded ScreenboxLite.exe payload
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream resStream = asm.GetManifestResourceStream("ScreenboxLite.exe"))
            {
                if (resStream == null)
                {
                    throw new InvalidOperationException("未找到内置安装包资源 ScreenboxLite.exe");
                }
                using (FileStream fs = new FileStream(targetExe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    resStream.CopyTo(fs);
                }
            }

            // Copy current setup executable as Uninstall.exe
            string selfExe = Process.GetCurrentProcess().MainModule.FileName;
            if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstallExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selfExe, uninstallExe, true);
            }

            // 1. Desktop shortcut
            if (desktopShortcut)
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(
                    Path.Combine(desktopDir, "Screenbox (截图增强版).lnk"),
                    targetExe,
                    installDir,
                    "Screenbox 视频播放器 (画框外UI + ~键原生截图 + 时间轴缩略图版)");
            }

            // 2. Start Menu shortcut
            if (startMenuShortcut)
            {
                string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string folderDir = Path.Combine(programsDir, "Screenbox");
                Directory.CreateDirectory(folderDir);
                CreateShortcut(
                    Path.Combine(folderDir, "Screenbox (截图增强版).lnk"),
                    targetExe,
                    installDir,
                    "Screenbox 视频播放器 (画框外UI + ~键原生截图 + 时间轴缩略图版)");
                CreateShortcut(
                    Path.Combine(folderDir, "卸载 Screenbox.lnk"),
                    uninstallExe,
                    installDir,
                    "卸载 Screenbox (截图增强版)");
            }

            // 3. Register ProgID and Applications entry in HKCU\Software\Classes
            if (fileAssoc || setAsDefaultPlayer)
            {
                using (RegistryKey progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
                {
                    if (progIdKey != null)
                    {
                        progIdKey.SetValue("", "Screenbox 视频文件");
                        progIdKey.SetValue("FriendlyTypeName", "Screenbox 视频文件");
                        using (RegistryKey iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                        {
                            if (iconKey != null) iconKey.SetValue("", "\"" + targetExe + "\",0");
                        }
                        using (RegistryKey cmdKey = progIdKey.CreateSubKey(@"shell\open\command"))
                        {
                            if (cmdKey != null) cmdKey.SetValue("", "\"" + targetExe + "\" \"%1\"");
                        }
                    }
                }

                using (RegistryKey appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\ScreenboxLite.exe"))
                {
                    if (appKey != null)
                    {
                        appKey.SetValue("FriendlyAppName", AppDisplayName);
                        using (RegistryKey iconKey = appKey.CreateSubKey("DefaultIcon"))
                        {
                            if (iconKey != null) iconKey.SetValue("", "\"" + targetExe + "\",0");
                        }
                        using (RegistryKey cmdKey = appKey.CreateSubKey(@"shell\open\command"))
                        {
                            if (cmdKey != null) cmdKey.SetValue("", "\"" + targetExe + "\" \"%1\"");
                        }
                        using (RegistryKey supKey = appKey.CreateSubKey("SupportedTypes"))
                        {
                            if (supKey != null)
                            {
                                foreach (string ext in VideoExtensions)
                                {
                                    supKey.SetValue(ext, "");
                                }
                            }
                        }
                    }
                }

                foreach (string ext in VideoExtensions)
                {
                    using (RegistryKey extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext))
                    {
                        if (extKey != null)
                        {
                            if (setAsDefaultPlayer)
                            {
                                extKey.SetValue("", ProgId);
                            }
                            using (RegistryKey owlKey = extKey.CreateSubKey("OpenWithList\\ScreenboxLite.exe"))
                            {
                            }
                            using (RegistryKey owpKey = extKey.CreateSubKey("OpenWithProgids"))
                            {
                                if (owpKey != null)
                                {
                                    owpKey.SetValue(ProgId, new byte[0], RegistryValueKind.None);
                                }
                            }
                        }
                    }
                }
            }

            // 4. Right-click Context Menu ("用 Screenbox 打开") on video extensions
            if (contextMenu)
            {
                using (RegistryKey sysAssocs = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations"))
                {
                    if (sysAssocs != null)
                    {
                        foreach (string ext in VideoExtensions)
                        {
                            using (RegistryKey menuKey = sysAssocs.CreateSubKey(ext + @"\shell\ScreenboxLite"))
                            {
                                if (menuKey != null)
                                {
                                    menuKey.SetValue("", "用 Screenbox 打开并截图");
                                    menuKey.SetValue("Icon", "\"" + targetExe + "\",0");
                                    using (RegistryKey cmdKey = menuKey.CreateSubKey("command"))
                                    {
                                        if (cmdKey != null)
                                        {
                                            cmdKey.SetValue("", "\"" + targetExe + "\" \"%1\"");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 5. Register in Windows "Apps & Features" / "Programs and Features" (Uninstall registry)
            using (RegistryKey uninstKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId))
            {
                if (uninstKey != null)
                {
                    uninstKey.SetValue("DisplayName", AppDisplayName);
                    uninstKey.SetValue("DisplayIcon", "\"" + targetExe + "\",0");
                    uninstKey.SetValue("DisplayVersion", AppVersion);
                    uninstKey.SetValue("Publisher", "Screenbox Custom Edition");
                    uninstKey.SetValue("InstallLocation", installDir);
                    uninstKey.SetValue("UninstallString", "\"" + uninstallExe + "\" /uninstall");
                    uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    uninstKey.SetValue("EstimatedSize", 120, RegistryValueKind.DWord);
                }
            }

            RefreshShellAssociations();
            return targetExe;
        }

        public static void RunUninstaller(bool silent)
        {
            if (!silent)
            {
                MessageBoxResult res = MessageBox.Show(
                    "确定要卸载「Screenbox (截图增强版)」并移除桌面快捷方式与右键菜单关联吗？\n（您已保存的视频截图不会被删除）",
                    "卸载 Screenbox",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            try
            {
                foreach (var proc in Process.GetProcessesByName("ScreenboxLite"))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(1500);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            string installDir = null;
            try
            {
                using (RegistryKey uninstKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId))
                {
                    if (uninstKey != null)
                    {
                        installDir = uninstKey.GetValue("InstallLocation") as string;
                    }
                }
            }
            catch
            {
            }

            // Remove shortcuts
            try
            {
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Screenbox (截图增强版).lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screenbox");
                if (Directory.Exists(startMenuFolder)) Directory.Delete(startMenuFolder, true);
            }
            catch
            {
            }

            // Clean registry keys
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\ScreenboxLite.exe", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId, false);

                foreach (string ext in VideoExtensions)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + ext + @"\shell\ScreenboxLite", false);
                    using (RegistryKey owpKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", true))
                    {
                        if (owpKey != null) owpKey.DeleteValue(ProgId, false);
                    }
                }
            }
            catch
            {
            }

            RefreshShellAssociations();

            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
            {
                try
                {
                    string targetExe = Path.Combine(installDir, "ScreenboxLite.exe");
                    if (File.Exists(targetExe)) File.Delete(targetExe);
                }
                catch
                {
                }

                // Schedule self-deletion of Uninstall.exe and folder via cmd
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/C ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"" + installDir + "\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    });
                }
                catch
                {
                }
            }

            if (!silent)
            {
                MessageBox.Show("Screenbox (截图增强版) 已成功卸载。", "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    public sealed class SetupWindow : Window
    {
        private readonly TextBox _installPathBox;
        private readonly TextBlock _detectedNoticeText;
        private readonly CheckBox _chkDesktopShortcut;
        private readonly CheckBox _chkStartMenuShortcut;
        private readonly CheckBox _chkContextMenu;
        private readonly CheckBox _chkFileAssoc;
        private readonly CheckBox _chkDefaultPlayer;
        private readonly CheckBox _chkLaunchNow;
        private readonly Button _installBtn;
        private readonly TextBlock _statusText;
        private readonly bool _isUpdateMode;

        public SetupWindow()
        {
            string existingDir = Program.DetectExistingInstallDir();
            string existingVer = Program.DetectExistingVersion();
            _isUpdateMode = !string.IsNullOrEmpty(existingDir);

            Title = _isUpdateMode
                ? "Screenbox (截图增强版) 覆盖更新 / 安装向导"
                : "Screenbox (截图增强版) 安装向导";
            Width = 570;
            Height = 495;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(22, 24, 28));
            Foreground = Brushes.White;

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                if (File.Exists(exePath))
                {
                    using (var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (sysIcon != null)
                        {
                            Icon = Imaging.CreateBitmapSourceFromHIcon(
                                sysIcon.Handle,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                        }
                    }
                }
            }
            catch
            {
            }

            var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = _isUpdateMode
                    ? "覆盖更新 Screenbox (截图增强版) v" + Program.AppVersion
                    : "安装 Screenbox (截图增强版) v" + Program.AppVersion,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255))
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "画框外独立控制栏 · ~键双模像素级放大框选 · 1键恢复上次框 · 滚轮0延迟单帧 · 音量锁0",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(175, 182, 192)),
                Margin = new Thickness(0, 5, 0, 0)
            });
            Grid.SetRow(headerPanel, 0);
            root.Children.Add(headerPanel);

            // Body
            var bodyStack = new StackPanel();

            bodyStack.Children.Add(new TextBlock
            {
                Text = _isUpdateMode ? "已自动识别现有安装位置（可直接覆盖更新或更改路径）：" : "安装位置（可自由选择路径）：",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 215, 222)),
                Margin = new Thickness(0, 0, 0, 6)
            });

            var pathGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string initialDir = existingDir ?? Program.GetDefaultInstallDir();

            _installPathBox = new TextBox
            {
                Text = initialDir,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(34, 38, 44)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(_isUpdateMode ? Color.FromRgb(0, 200, 150) : Color.FromRgb(70, 78, 90)),
                BorderThickness = new Thickness(1)
            };
            _installPathBox.TextChanged += (_, __) => UpdatePathModeFeedback();
            Grid.SetColumn(_installPathBox, 0);
            pathGrid.Children.Add(_installPathBox);

            var browseBtn = new Button
            {
                Content = "浏览...",
                Width = 76,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(48, 54, 62)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 95, 108)),
                Cursor = Cursors.Hand
            };
            browseBtn.Click += (_, __) =>
            {
                using (var dlg = new Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择 Screenbox 安装目录";
                    dlg.SelectedPath = _installPathBox.Text;
                    if (dlg.ShowDialog() == Forms.DialogResult.OK)
                    {
                        string sel = dlg.SelectedPath;
                        if (!sel.EndsWith("ScreenboxLite", StringComparison.OrdinalIgnoreCase) &&
                            !File.Exists(Path.Combine(sel, "ScreenboxLite.exe")))
                        {
                            sel = Path.Combine(sel, "ScreenboxLite");
                        }
                        _installPathBox.Text = sel;
                    }
                }
            };
            Grid.SetColumn(browseBtn, 1);
            pathGrid.Children.Add(browseBtn);
            bodyStack.Children.Add(pathGrid);

            _detectedNoticeText = new TextBlock
            {
                Text = _isUpdateMode
                    ? "✔ 检测到已安装版本" + (!string.IsNullOrEmpty(existingVer) ? (" (v" + existingVer + ")") : "") + "，将保留您的截图目录与配置并直接覆盖升级至 v" + Program.AppVersion
                    : "提示：若目标目录已存在旧版本，安装程序会自动关闭运行中的旧进程并平滑覆盖更新。",
                FontSize = 11.5,
                Foreground = _isUpdateMode
                    ? new SolidColorBrush(Color.FromRgb(80, 235, 165))
                    : new SolidColorBrush(Color.FromRgb(140, 150, 165)),
                Margin = new Thickness(2, 0, 0, 12)
            };
            bodyStack.Children.Add(_detectedNoticeText);

            // Checkboxes
            var optionsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 34, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(52, 58, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var optStack = new StackPanel();

            _chkDesktopShortcut = CreateOptionCheckBox("创建/更新桌面快捷方式（Screenbox 截图增强版）", true);
            _chkStartMenuShortcut = CreateOptionCheckBox("添加到「开始」菜单程序列表与系统应用卸载列表", true);
            _chkContextMenu = CreateOptionCheckBox("添加视频文件右键菜单「用 Screenbox 打开并截图」", true);
            _chkFileAssoc = CreateOptionCheckBox("注册到系统视频「打开方式」列表 (.mp4 / .mkv / .avi / .mov 等)", true);
            _chkDefaultPlayer = CreateOptionCheckBox("设为常见视频格式关联打开程序", true);
            _chkLaunchNow = CreateOptionCheckBox("安装/更新完成后立即启动 Screenbox", true);

            optStack.Children.Add(_chkDesktopShortcut);
            optStack.Children.Add(_chkStartMenuShortcut);
            optStack.Children.Add(_chkContextMenu);
            optStack.Children.Add(_chkFileAssoc);
            optStack.Children.Add(_chkDefaultPlayer);
            optStack.Children.Add(_chkLaunchNow);
            optionsBorder.Child = optStack;
            bodyStack.Children.Add(optionsBorder);

            _statusText = new TextBlock
            {
                Text = _isUpdateMode
                    ? "已就绪：点击下方按钮即可原位覆盖更新（无需先卸载旧版）。"
                    : "准备就绪，点击「立即一键安装」即可完成部署。",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 175)),
                Margin = new Thickness(2, 10, 0, 0)
            };
            bodyStack.Children.Add(_statusText);

            Grid.SetRow(bodyStack, 1);
            root.Children.Add(bodyStack);

            // Footer Buttons
            var footerGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var uninstBtn = new Button
            {
                Content = "卸载已有版本",
                Width = 110,
                Height = 38,
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 54)),
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(75, 80, 90)),
                Cursor = Cursors.Hand
            };
            uninstBtn.Click += (_, __) => Program.RunUninstaller(silent: false);
            Grid.SetColumn(uninstBtn, 0);
            footerGrid.Children.Add(uninstBtn);

            var rightBtnStack = new StackPanel { Orientation = Orientation.Horizontal };
            var cancelBtn = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 38,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 54)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(75, 80, 90)),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (_, __) => Close();
            rightBtnStack.Children.Add(cancelBtn);

            _installBtn = new Button
            {
                Content = _isUpdateMode ? "🔄 立即覆盖更新" : "🚀 立即一键安装",
                Width = 162,
                Height = 38,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Background = new SolidColorBrush(Color.FromRgb(0, 195, 225)),
                Foreground = new SolidColorBrush(Color.FromRgb(12, 16, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                Cursor = Cursors.Hand
            };
            _installBtn.Click += InstallBtn_OnClick;
            rightBtnStack.Children.Add(_installBtn);

            Grid.SetColumn(rightBtnStack, 2);
            footerGrid.Children.Add(rightBtnStack);

            Grid.SetRow(footerGrid, 2);
            root.Children.Add(footerGrid);

            Content = root;
        }

        private void UpdatePathModeFeedback()
        {
            if (_installPathBox == null || _installBtn == null || _detectedNoticeText == null)
            {
                return;
            }
            string path = (_installPathBox.Text ?? "").Trim();
            bool hasExistingFile = !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "ScreenboxLite.exe"));
            if (hasExistingFile)
            {
                _installBtn.Content = "🔄 立即覆盖更新";
                _detectedNoticeText.Text = "✔ 该路径下已存在 ScreenboxLite.exe，点击按钮将直接覆盖更新并保留用户配置。";
                _detectedNoticeText.Foreground = new SolidColorBrush(Color.FromRgb(80, 235, 165));
            }
            else if (!_isUpdateMode)
            {
                _installBtn.Content = "🚀 立即一键安装";
                _detectedNoticeText.Text = "提示：将在所选路径下全新安装 Screenbox (截图增强版)。";
                _detectedNoticeText.Foreground = new SolidColorBrush(Color.FromRgb(140, 150, 165));
            }
        }

        private static CheckBox CreateOptionCheckBox(string label, bool isChecked)
        {
            return new CheckBox
            {
                Content = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Color.FromRgb(230, 235, 240)),
                    FontSize = 13
                },
                IsChecked = isChecked,
                Margin = new Thickness(0, 4, 0, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
        }

        private void InstallBtn_OnClick(object sender, RoutedEventArgs e)
        {
            string dir = (_installPathBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir))
            {
                MessageBox.Show("请选择有效的安装目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _installBtn.IsEnabled = false;
                _statusText.Text = "正在安装并配置快捷方式与文件关联...";
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255));

                string installedExe = Program.PerformInstall(
                    dir,
                    _chkDesktopShortcut.IsChecked == true,
                    _chkStartMenuShortcut.IsChecked == true,
                    _chkContextMenu.IsChecked == true,
                    _chkFileAssoc.IsChecked == true,
                    _chkDefaultPlayer.IsChecked == true);

                _statusText.Text = "✔ 安装成功！已部署至: " + installedExe;

                if (_chkLaunchNow.IsChecked == true && File.Exists(installedExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installedExe,
                        WorkingDirectory = dir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show(
                        "Screenbox (截图增强版) 已安装完成！\n\n安装位置：\n" + installedExe,
                        "安装成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                Close();
            }
            catch (Exception ex)
            {
                _installBtn.IsEnabled = true;
                _statusText.Text = "安装出错: " + ex.Message;
                _statusText.Foreground = Brushes.Tomato;
                MessageBox.Show("安装过程中发生错误：\n" + ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
