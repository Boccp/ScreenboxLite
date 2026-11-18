using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenboxLite.Core;
using ScreenboxLite.Mpv;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// 主窗口：三行固定布局（铁律，任何模式下视频区尺寸/位置不变）
    ///   Row 0  48px  顶栏（普通标题栏 / 截图模式工具栏，原位切换）
    ///   Row 1  *     视频舞台（mpv 宿主窗口 + 截图模式的冻结帧与选框覆盖层）
    ///   Row 2  96px  底栏（时间轴 + 模块化播放控制栏，自适应多档宽度零遮挡）
    /// </summary>
    internal sealed partial class MainWindow : Window
    {
        private readonly Settings _settings;
        private readonly string _startupFile;

        // ---- Row 0 ----
        private Grid _topNormalHeader;
        private Grid _topScreenshotBar;
        private StackPanel _topLeftPanel;
        private Grid _topTitleHost;
        private StackPanel _topRightPanel;
        private Border _playlistBadge;
        private TextBlock _playlistCounterText;
        private TextBlock _titleText;
        private Border _videoSpecBadge;
        private TextBlock _videoSpecText;
        private Button _topOpenFolderBtn;
        private Button _normalFlipBtn;
        private System.Windows.Shapes.Path _normalFlipIcon;
        private TextBlock _normalFlipText;

        private Button _shotFlipBtn;
        private System.Windows.Shapes.Path _shotFlipIcon;
        private TextBlock _shotFlipText;
        private Border _saveFolderPill;
        private TextBlock _saveLocationText;

        private Border _toastBorder;
        private TextBlock _toastText;
        private DispatcherTimer _toastTimer;

        // ---- Row 1 ----
        private Grid _stage;
        private VideoHost _videoHost;
        private Image _frozenImage;
        private Border _emptyCard;
        private Grid _overlayRoot;
        private Canvas _overlayCanvas;
        private Line _aimH, _aimV;
        private Grid _reticle;
        private Rectangle _selectionShape;
        private readonly Rectangle[] _handles = new Rectangle[8];
        private Border _sizeTag;
        private TextBlock _sizeText;
        private Border _magnifier;
        private Rectangle _magnifierView;
        private ImageBrush _magnifierBrush;
        private TextBlock _magnifierCoord;
        private Border _magnifierSwatch;
        private TextBlock _magnifierColor;

        // ---- Row 2 ----
        private Grid _bottomBar;
        private Border _bottomContainer;
        private Grid _timeline;
        private Border _timelineFill;
        private Border _timelineHoverMarker;
        private Ellipse _timelineThumb;

        private StackPanel _bottomLeftPanel;
        private Border _jumpCapsule;
        private StackPanel _bottomRightPanel;

        private Button _playBtn;
        private System.Windows.Shapes.Path _playIconPath;
        private Button _stepBackBtn;
        private TextBlock _stepBackText;
        private Button _stepFwdBtn;
        private TextBlock _stepFwdText;
        private StackPanel _readoutStack;
        private TextBlock _timeCurrentText;
        private TextBlock _timeTotalText;
        private TextBlock _frameText;

        private TextBlock _jumpLabel;
        private TextBox _jumpBox;
        private Button _jumpGoBtn;
        private Button _jumpShotBtn;

        private Border _rightDiv1;
        private Border _rightDiv2;
        private ComboBox _speedCombo;
        private Button _volumeLockBtn;
        private System.Windows.Shapes.Path _volumeLockIcon;
        private TextBlock _volumeLockText;
        private Button _muteBtn;
        private System.Windows.Shapes.Path _muteIconPath;
        private Grid _volumeArea;
        private Border _volumeTrackBg;
        private Border _volumeFill;
        private Ellipse _volumeThumb;
        private TextBlock _volumeText;
        private Button _fsBtn;
        private System.Windows.Shapes.Path _fsIconPath;
        private TextBlock _fsBtnText;

        // ---- 浮层（独立窗口，可盖在视频之上） ----
        private Popup _hoverPopup;
        private Image _hoverImage;
        private TextBlock _hoverTimeText;

        private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 };

        // ---- 供自检校验无遮挡布局的内部访问器 ----
        internal FrameworkElement BottomLeftForTest { get { return _bottomLeftPanel; } }
        internal FrameworkElement BottomCenterForTest { get { return _jumpCapsule; } }
        internal FrameworkElement BottomRightForTest { get { return _bottomRightPanel; } }
        internal FrameworkElement TopLeftForTest { get { return _topLeftPanel; } }
        internal FrameworkElement TopCenterForTest { get { return _topTitleHost; } }
        internal FrameworkElement TopRightForTest { get { return _topRightPanel; } }

        public MainWindow(string startupFile)
        {
            _startupFile = startupFile;
            Playlist.InitStartupContext(startupFile);
            _settings = Settings.Load();

            Title = "Screenbox";
            Width = 1280;
            Height = 820;
            MinWidth = 820;
            MinHeight = 540;
            FontFamily = Theme.UiFont;
            Background = Theme.WindowBg;
            Foreground = Theme.TextMain;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowDrop = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
            KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);
            LoadWindowIcon();

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });

            UIElement top = BuildTopBar();
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            UIElement stage = BuildStage();
            Grid.SetRow(stage, 1);
            root.Children.Add(stage);

            UIElement bottom = BuildBottomBar();
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            BuildPopups();
            Content = root;

            InitPlayback();
            InitInput();

            Loaded += (s, e) => UpdateResponsiveLayout();
            SizeChanged += (s, e) => UpdateResponsiveLayout();
            StateChanged += (s, e) =>
            {
                UpdateFullscreenButtonUi();
                UpdateResponsiveLayout();
            };
        }

        private void LoadWindowIcon()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (ico != null)
                        Icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch (Exception ex)
            {
                Log.Debug("读取窗口图标失败: " + ex.Message);
            }
        }

        // ======================= Row 0：顶栏（48px 固定高度） =======================

        private UIElement BuildTopBar()
        {
            var bar = new Border
            {
                Height = 48,
                Background = Theme.TopBarBg,
                BorderBrush = Theme.Divider,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var container = new Grid();

            // ---- 1) 普通播放模式顶栏：左(打开/截图) | 中(序号+标题+分辨率) | 右(截图目录) ----
            _topNormalHeader = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            _topNormalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _topNormalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _topNormalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _topLeftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var openBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoFolder, "打开视频", Theme.TextDim), 32, 11, false);
            openBtn.ToolTip = "打开本地视频文件 (Ctrl+O，或直接将视频拖入窗口)";
            openBtn.Click += (s, e) => OpenFilePicker();
            _topLeftPanel.Children.Add(openBtn);

            var shotBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoCrop, "截图模式 (~)", Theme.Accent), 32, 12, true);
            shotBtn.Margin = new Thickness(8, 0, 0, 0);
            shotBtn.ToolTip = "进入/确认无损原画框选截图（快捷键 ~，画面比例与位置零跳动）";
            shotBtn.Click += (s, e) => HandleTilde();
            _topLeftPanel.Children.Add(shotBtn);

            var normFlipContent = new StackPanel { Orientation = Orientation.Horizontal };
            _normalFlipIcon = Theme.MakeIcon(Theme.GeoFlip, Theme.TextDim, 12.5);
            _normalFlipText = new TextBlock { Text = "镜像 (Tab)", FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            normFlipContent.Children.Add(_normalFlipIcon);
            normFlipContent.Children.Add(_normalFlipText);
            _normalFlipBtn = Theme.MakeFlexButton(normFlipContent, 32, 10, false);
            _normalFlipBtn.Margin = new Thickness(8, 0, 0, 0);
            _normalFlipBtn.ToolTip = "水平镜像翻转视频画面与截图（快捷键 Tab，播放与截图模式通用，切换视频或关闭软件后自动重置）";
            _normalFlipBtn.Click += (s, e) => ToggleHorizontalMirror();
            _topLeftPanel.Children.Add(_normalFlipBtn);
            _topNormalHeader.Children.Add(_topLeftPanel);

            _topTitleHost = new Grid
            {
                Margin = new Thickness(16, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };
            _topTitleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _topTitleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _topTitleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _playlistBadge = new Border
            {
                Background = Theme.Frozen(Color.FromRgb(18, 46, 58)),
                BorderBrush = Theme.Frozen(Color.FromRgb(0, 138, 168)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _playlistCounterText = new TextBlock
            {
                Foreground = Theme.Accent,
                FontFamily = Theme.Mono,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _playlistBadge.Child = _playlistCounterText;
            Grid.SetColumn(_playlistBadge, 0);
            _topTitleHost.Children.Add(_playlistBadge);

            _titleText = new TextBlock
            {
                Text = "未打开视频 — 拖入视频或点击左上角「打开视频」开始",
                Foreground = Theme.TextDim,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_titleText, 1);
            _topTitleHost.Children.Add(_titleText);

            _videoSpecBadge = new Border
            {
                Background = Theme.CapsuleBg,
                BorderBrush = Theme.CapsuleBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _videoSpecText = new TextBlock
            {
                Foreground = Theme.TextDim,
                FontFamily = Theme.Mono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            _videoSpecBadge.Child = _videoSpecText;
            Grid.SetColumn(_videoSpecBadge, 2);
            _topTitleHost.Children.Add(_videoSpecBadge);

            Grid.SetColumn(_topTitleHost, 1);
            _topNormalHeader.Children.Add(_topTitleHost);

            _topRightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _topOpenFolderBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoFolder, "截图目录", Theme.TextDim, 12.5, 12), 30, 10, false);
            _topOpenFolderBtn.Background = Theme.SubtleBtnBg;
            _topOpenFolderBtn.BorderBrush = Theme.SubtleBtnBorder;
            _topOpenFolderBtn.Click += (s, e) => OpenSaveFolder();
            _topRightPanel.Children.Add(_topOpenFolderBtn);
            Grid.SetColumn(_topRightPanel, 2);
            _topNormalHeader.Children.Add(_topRightPanel);

            container.Children.Add(_topNormalHeader);

            // ---- 2) ~键截图模式顶栏：与普通顶栏共用同一 48px 行原位切换 ----
            _topScreenshotBar = new Grid
            {
                Background = Theme.ShotBarBg,
                Visibility = Visibility.Collapsed
            };
            var shotAccentLine = new Border
            {
                BorderBrush = Theme.ShotBarBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsHitTestVisible = false
            };
            _topScreenshotBar.Children.Add(shotAccentLine);

            var shotInner = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            shotInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            shotInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shotInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var shotLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var modeBadge = new Border
            {
                Background = Theme.ActiveBadgeBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal };
            modeRow.Children.Add(Theme.MakeIcon(Theme.GeoCrop, Theme.ActiveBadgeFg, 12));
            modeRow.Children.Add(new TextBlock
            {
                Text = "截图模式",
                Foreground = Theme.ActiveBadgeFg,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0)
            });
            modeBadge.Child = modeRow;
            shotLeft.Children.Add(modeBadge);

            var flipContent = new StackPanel { Orientation = Orientation.Horizontal };
            _shotFlipIcon = Theme.MakeIcon(Theme.GeoFlip, Theme.TextMain, 12.5);
            _shotFlipText = new TextBlock { Text = "镜像 (Tab)", FontSize = 11.5, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            flipContent.Children.Add(_shotFlipIcon);
            flipContent.Children.Add(_shotFlipText);
            _shotFlipBtn = Theme.MakeFlexButton(flipContent, 28, 9, false);
            _shotFlipBtn.Margin = new Thickness(10, 0, 0, 0);
            _shotFlipBtn.ToolTip = "水平镜像翻转画面与保存结果（快捷键 Tab，保存后保持翻转，切换视频或关闭软件后自动重置）";
            _shotFlipBtn.Click += (s, e) => ToggleHorizontalMirror();
            shotLeft.Children.Add(_shotFlipBtn);

            var restoreCropBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoRestore, "上次框 (R)", Theme.TextMain, 12, 11.5), 28, 9, false);
            restoreCropBtn.Margin = new Thickness(6, 0, 0, 0);
            restoreCropBtn.ToolTip = "一键恢复上一次保存时使用的裁剪框位置 (快捷键 R)";
            restoreCropBtn.Click += (s, e) => RestoreLastCrop();
            shotLeft.Children.Add(restoreCropBtn);
            shotInner.Children.Add(shotLeft);

            var shotTip = new TextBlock
            {
                Text = "左键拖拽或双点定框  ·  [~ / Enter] 保存  ·  [滚轮 / S+AD] 换帧  ·  [右键 / Esc] 上一步",
                Foreground = Theme.Frozen(Color.FromRgb(186, 224, 238)),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            Grid.SetColumn(shotTip, 1);
            shotInner.Children.Add(shotTip);

            var folderBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _saveFolderPill = new Border
            {
                Height = 28,
                MaxWidth = 180,
                Background = Theme.Frozen(Color.FromRgb(10, 24, 32)),
                BorderBrush = Theme.Frozen(Color.FromArgb(120, 0, 184, 217)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pillRow.Children.Add(Theme.MakeIcon(Theme.GeoFolder, Theme.Accent, 12));
            _saveLocationText = new TextBlock
            {
                Foreground = Theme.Frozen(Color.FromRgb(215, 238, 246)),
                FontSize = 11.5,
                Margin = new Thickness(6, 0, 0, 0),
                MaxWidth = 142,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            pillRow.Children.Add(_saveLocationText);
            _saveFolderPill.Child = pillRow;
            folderBtns.Children.Add(_saveFolderPill);

            var changeBtn = Theme.MakeButton("更改", 48, 28);
            changeBtn.FontSize = 11.5;
            changeBtn.ToolTip = "更改默认截图保存目录";
            changeBtn.Click += (s, e) => PickSaveFolder();
            folderBtns.Children.Add(changeBtn);

            var openFolderBtn = Theme.MakeButton("打开", 48, 28);
            openFolderBtn.FontSize = 11.5;
            openFolderBtn.Margin = new Thickness(5, 0, 0, 0);
            openFolderBtn.ToolTip = "在资源管理器中打开截图保存目录";
            openFolderBtn.Click += (s, e) => OpenSaveFolder();
            folderBtns.Children.Add(openFolderBtn);

            var exitShotBtn = Theme.MakeFlexButton(Theme.MakeIcon(Theme.GeoClose, Theme.TextDim, 11), 28, 8, false);
            exitShotBtn.Margin = new Thickness(6, 0, 0, 0);
            exitShotBtn.ToolTip = "退出截图模式返回播放 (Esc)";
            exitShotBtn.Click += (s, e) => ExitScreenshotMode();
            folderBtns.Children.Add(exitShotBtn);

            Grid.SetColumn(folderBtns, 2);
            shotInner.Children.Add(folderBtns);
            _topScreenshotBar.Children.Add(shotInner);
            container.Children.Add(_topScreenshotBar);

            // ---- 3) 居中悬浮轻提示（不遮挡左右两侧按钮） ----
            _toastBorder = new Border
            {
                Background = Theme.Frozen(Color.FromArgb(242, 14, 38, 52)),
                BorderBrush = Theme.AccentBtnBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 5, 14, 5),
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _toastText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _toastBorder.Child = _toastText;
            container.Children.Add(_toastBorder);
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); _toastBorder.Visibility = Visibility.Collapsed; };

            UpdateSaveFolderDisplay();
            bar.Child = container;
            return bar;
        }

        private void UpdateSaveFolderDisplay()
        {
            string full = _settings.SaveFolderPath ?? "";
            string leaf = full;
            try
            {
                string trimmed = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string name = System.IO.Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(name)) leaf = name;
            }
            catch
            {
            }
            if (_saveLocationText != null) _saveLocationText.Text = leaf;
            if (_saveFolderPill != null) _saveFolderPill.ToolTip = "默认截图保存目录：\n" + full;
            if (_topOpenFolderBtn != null) _topOpenFolderBtn.ToolTip = "打开截图保存目录：\n" + full;
        }

        // ======================= Row 1：视频舞台（* 零位移铁律） =======================

        private UIElement BuildStage()
        {
            _stage = new Grid { Background = Theme.StageBg, ClipToBounds = true };

            // 截图模式下显示的冻结帧（与 mpv 画面同为等比居中），位于宿主窗口之下
            _frozenImage = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_frozenImage, BitmapScalingMode.HighQuality);
            _stage.Children.Add(_frozenImage);

            _videoHost = new VideoHost { Visibility = Visibility.Hidden };
            _stage.Children.Add(_videoHost);

            // 空状态引导卡片：清晰展示核心找帧与截图按键
            _emptyCard = new Border
            {
                MaxWidth = 620,
                Background = Theme.Frozen(Color.FromRgb(16, 20, 28)),
                BorderBrush = Theme.CapsuleBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(34, 26, 34, 26),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var emptyStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            var emblem = new Border
            {
                Width = 46,
                Height = 46,
                CornerRadius = new CornerRadius(23),
                Background = Theme.AccentBtnBg,
                BorderBrush = Theme.AccentBtnBorder,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
                Child = Theme.MakeIcon(Theme.GeoCrop, Theme.Accent, 20)
            };
            emptyStack.Children.Add(emblem);

            emptyStack.Children.Add(new TextBlock
            {
                Text = "拖入视频文件，或点击下方按钮开始",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextMain,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = "专为边看视频边精准逐帧定位与原画无损裁剪而设计",
                FontSize = 12.5,
                Foreground = Theme.TextDim,
                Margin = new Thickness(0, 6, 0, 18),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var keyGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < 3; r++) keyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddShortcutHint(keyGrid, 0, 0, "~ 波浪键", "进入框选截图 / 再次按保存");
            AddShortcutHint(keyGrid, 0, 1, "滚轮 / , .", "零延迟逐帧后退 / 逐帧前进");
            AddShortcutHint(keyGrid, 1, 0, "按住 A / D", "5 倍速快退 / 快进 (S+AD 单帧)");
            AddShortcutHint(keyGrid, 1, 1, "1 / 2 / 3", "跳 5s / 20s / 60s (按 E 后退)");
            AddShortcutHint(keyGrid, 2, 0, "Tab / R", "随时水平镜像 / 恢复上次选框");
            AddShortcutHint(keyGrid, 2, 1, "右键 / Esc", "逐级撤销调框与退出截图模式");
            emptyStack.Children.Add(keyGrid);

            var emptyOpen = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoFolder, "选择视频文件打开 (Ctrl+O)", Theme.Accent, 14, 13), 38, 22, true);
            emptyOpen.HorizontalAlignment = HorizontalAlignment.Center;
            emptyOpen.Click += (s, e) => OpenFilePicker();
            emptyStack.Children.Add(emptyOpen);

            _emptyCard.Child = emptyStack;
            _stage.Children.Add(_emptyCard);

            _stage.Children.Add(BuildScreenshotOverlay());
            return _stage;
        }

        private static void AddShortcutHint(Grid g, int row, int col, string keyText, string descText)
        {
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(col == 0 ? 0 : 10, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var kbd = new Border
            {
                Background = Theme.CapsuleBg,
                BorderBrush = Theme.ButtonBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                MinWidth = 68
            };
            kbd.Child = new TextBlock
            {
                Text = keyText,
                FontFamily = Theme.Mono,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            item.Children.Add(kbd);
            item.Children.Add(new TextBlock
            {
                Text = descText,
                FontSize = 11.5,
                Foreground = Theme.TextDim,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(item, row);
            Grid.SetColumn(item, col);
            g.Children.Add(item);
        }

        private UIElement BuildScreenshotOverlay()
        {
            _overlayRoot = new Grid { Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Cursor = Cursors.None };
            _overlayCanvas = new Canvas { IsHitTestVisible = false };

            var dash = new DoubleCollection(new double[] { 4, 4 });
            dash.Freeze();
            var aimBrush = Theme.Frozen(Color.FromArgb(180, 0, 229, 255));
            _aimH = new Line { Stroke = aimBrush, StrokeThickness = 1, StrokeDashArray = dash };
            _aimV = new Line { Stroke = aimBrush, StrokeThickness = 1, StrokeDashArray = dash };
            _overlayCanvas.Children.Add(_aimH);
            _overlayCanvas.Children.Add(_aimV);

            _selectionShape = new Rectangle { Stroke = Theme.Accent, StrokeThickness = 2, Fill = Theme.SelectionFill, Visibility = Visibility.Collapsed };
            _overlayCanvas.Children.Add(_selectionShape);
            for (int i = 0; i < 8; i++)
            {
                _handles[i] = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = Theme.Accent, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
                _overlayCanvas.Children.Add(_handles[i]);
            }
            _sizeTag = new Border
            {
                Background = Theme.Frozen(Color.FromArgb(238, 14, 18, 26)),
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                Visibility = Visibility.Collapsed
            };
            _sizeText = new TextBlock { Foreground = Brushes.White, FontFamily = Theme.Mono, FontSize = 12 };
            _sizeTag.Child = _sizeText;
            _overlayCanvas.Children.Add(_sizeTag);

            _reticle = new Grid { Width = 26, Height = 26 };
            _reticle.Children.Add(new Rectangle { Width = 26, Height = 26, Stroke = Theme.Accent, StrokeThickness = 1.5 });
            _reticle.Children.Add(new Line { X1 = 13, Y1 = 0, X2 = 13, Y2 = 26, Stroke = Theme.Reticle, StrokeThickness = 1.2 });
            _reticle.Children.Add(new Line { X1 = 0, Y1 = 13, X2 = 26, Y2 = 13, Stroke = Theme.Reticle, StrokeThickness = 1.2 });
            _overlayCanvas.Children.Add(_reticle);

            // 像素放大镜：直接放大冻结帧的真实像素（最近邻），并显示该像素的颜色值
            _magnifier = new Border
            {
                Width = 160,
                Padding = new Thickness(7),
                Background = Theme.PopupBg,
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(7)
            };
            var magStack = new StackPanel();
            var magGrid = new Grid { Width = 142, Height = 142, ClipToBounds = true, Background = Brushes.Black };
            _magnifierBrush = new ImageBrush { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
            _magnifierView = new Rectangle { Width = 142, Height = 142, Fill = _magnifierBrush };
            RenderOptions.SetBitmapScalingMode(_magnifierView, BitmapScalingMode.NearestNeighbor);
            magGrid.Children.Add(_magnifierView);
            var crossBrush = Theme.Frozen(Color.FromArgb(170, 0, 229, 255));
            magGrid.Children.Add(new Line { X1 = 71, Y1 = 0, X2 = 71, Y2 = 142, Stroke = crossBrush, StrokeThickness = 1 });
            magGrid.Children.Add(new Line { X1 = 0, Y1 = 71, X2 = 142, Y2 = 71, Stroke = crossBrush, StrokeThickness = 1 });
            magGrid.Children.Add(new Rectangle { Width = 10, Height = 10, Stroke = Theme.Reticle, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            magStack.Children.Add(new Border { BorderBrush = Theme.Frozen(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = magGrid });
            _magnifierCoord = new TextBlock { Text = "X: 0, Y: 0", FontFamily = Theme.Mono, FontSize = 11.5, Foreground = Theme.TextMain, Margin = new Thickness(0, 6, 0, 3) };
            magStack.Children.Add(_magnifierCoord);
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
            _magnifierSwatch = new Border { Width = 14, Height = 14, Background = Brushes.Black, BorderBrush = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0) };
            _magnifierColor = new TextBlock { Text = "#000000", FontFamily = Theme.Mono, FontSize = 11.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            colorRow.Children.Add(_magnifierSwatch);
            colorRow.Children.Add(_magnifierColor);
            magStack.Children.Add(colorRow);
            _magnifier.Child = magStack;
            _overlayCanvas.Children.Add(_magnifier);

            _overlayRoot.Children.Add(_overlayCanvas);
            return _overlayRoot;
        }

        // ======================= Row 2：底栏（96px 固定高度，模块化分组 + 自适应防遮挡） =======================

        private UIElement BuildBottomBar()
        {
            _bottomBar = new Grid { Height = 96, Background = Theme.BottomBarBg };
            _bottomContainer = new Border
            {
                BorderBrush = Theme.Divider,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 7, 14, 9)
            };
            var rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ---- 上层：高精度时间轴 ----
            _timeline = new Grid { Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            _timeline.Children.Add(new Border { Height = 6, Background = Theme.Track, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center });
            _timelineFill = new Border { Height = 6, Width = 0, Background = Theme.AccentFill, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            _timeline.Children.Add(_timelineFill);
            _timelineHoverMarker = new Border { Width = 2, Height = 14, Background = Theme.HoverMarker, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            _timeline.Children.Add(_timelineHoverMarker);
            _timelineThumb = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.White,
                Stroke = Theme.AccentFill,
                StrokeThickness = 2.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-7, 0, 0, 0),
                IsHitTestVisible = false
            };
            _timeline.Children.Add(_timelineThumb);
            _timeline.MouseEnter += (s, e) => { _timelineThumb.Width = 16; _timelineThumb.Height = 16; UpdateTimeUi(true); };
            _timeline.MouseLeave += (s, e) => { _timelineThumb.Width = 14; _timelineThumb.Height = 14; UpdateTimeUi(true); };
            Grid.SetRow(_timeline, 0);
            rows.Children.Add(_timeline);

            // ---- 下层：三栏控制区（左：播放+逐帧+双行时间帧号 | 中：定点跳转胶囊 | 右：历史队列+倍速+锁0音量+全屏） ----
            var transport = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // === 左区 ===
            _bottomLeftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 1) 播放与切集胶囊
            var transportCapsule = Theme.MakeCapsule(new Thickness(3, 2, 3, 2));
            var transportRow = new StackPanel { Orientation = Orientation.Horizontal };
            var prevBtn = Theme.MakeCapsuleButton(Theme.MakeIcon(Theme.GeoPrev, Theme.TextMain, 12), 30, 28);
            prevBtn.ToolTip = "切换同目录上一个视频 (PageUp / Shift+P)";
            prevBtn.Click += (s, e) => SwitchNeighbor(-1);
            transportRow.Children.Add(prevBtn);

            _playIconPath = Theme.MakeIcon(Theme.GeoPlay, Theme.Accent, 13.5);
            _playBtn = Theme.MakeCapsuleButton(_playIconPath, 38, 28);
            _playBtn.Margin = new Thickness(2, 0, 2, 0);
            _playBtn.Background = Theme.AccentBtnBg;
            _playBtn.BorderBrush = Theme.Frozen(Color.FromArgb(90, 0, 200, 240));
            _playBtn.ToolTip = "播放 / 暂停 (Space / K / 单击画面)";
            _playBtn.Click += (s, e) => TogglePlayPause();
            transportRow.Children.Add(_playBtn);

            var nextBtn = Theme.MakeCapsuleButton(Theme.MakeIcon(Theme.GeoNext, Theme.TextMain, 12), 30, 28);
            nextBtn.ToolTip = "切换同目录下一个视频 (PageDown / Shift+N)";
            nextBtn.Click += (s, e) => SwitchNeighbor(1);
            transportRow.Children.Add(nextBtn);
            transportCapsule.Child = transportRow;
            _bottomLeftPanel.Children.Add(transportCapsule);

            // 2) 单帧步进胶囊
            var stepCapsule = Theme.MakeCapsule(new Thickness(3, 2, 3, 2));
            stepCapsule.Margin = new Thickness(8, 0, 0, 0);
            var stepRow = new StackPanel { Orientation = Orientation.Horizontal };

            var backContent = new StackPanel { Orientation = Orientation.Horizontal };
            backContent.Children.Add(Theme.MakeIcon(Theme.GeoStepBack, Theme.TextDim, 10.5));
            _stepBackText = new TextBlock { Text = "-1帧", FontSize = 11.5, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            backContent.Children.Add(_stepBackText);
            _stepBackBtn = Theme.MakeCapsuleButton(backContent, 52, 28);
            _stepBackBtn.ToolTip = "后退 1 帧 (快捷键 , 逗号 或 S+A)";
            _stepBackBtn.Click += (s, e) => { _nav.StepFrames(-1); UpdateTimeUi(true); };
            stepRow.Children.Add(_stepBackBtn);

            stepRow.Children.Add(Theme.MakeDivider(14, 2));

            var fwdContent = new StackPanel { Orientation = Orientation.Horizontal };
            _stepFwdText = new TextBlock { Text = "+1帧", FontSize = 11.5, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            fwdContent.Children.Add(_stepFwdText);
            fwdContent.Children.Add(Theme.MakeIcon(Theme.GeoStepFwd, Theme.TextDim, 10.5));
            _stepFwdBtn = Theme.MakeCapsuleButton(fwdContent, 52, 28);
            _stepFwdBtn.ToolTip = "前进 1 帧 (快捷键 . 句号 或 S+D)";
            _stepFwdBtn.Click += (s, e) => { _nav.StepFrames(1); UpdateTimeUi(true); };
            stepRow.Children.Add(_stepFwdBtn);

            stepCapsule.Child = stepRow;
            _bottomLeftPanel.Children.Add(stepCapsule);

            // 3) 双行高对比度时间码 + 帧号读数（纵向排布，彻底消除横向挤压且帧号常驻清晰可见）
            _readoutStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                MinWidth = 178
            };
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
            _timeCurrentText = new TextBlock
            {
                Text = "00:00:00.000",
                FontFamily = Theme.Mono,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextMain
            };
            var timeSlash = new TextBlock
            {
                Text = " / ",
                FontFamily = Theme.Mono,
                FontSize = 12,
                Foreground = Theme.TextFaint
            };
            _timeTotalText = new TextBlock
            {
                Text = "00:00:00.000",
                FontFamily = Theme.Mono,
                FontSize = 12.5,
                Foreground = Theme.TextDim
            };
            timeRow.Children.Add(_timeCurrentText);
            timeRow.Children.Add(timeSlash);
            timeRow.Children.Add(_timeTotalText);
            _readoutStack.Children.Add(timeRow);

            _frameText = new TextBlock
            {
                Text = "未加载视频",
                FontFamily = Theme.Mono,
                FontSize = 11,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(0, 1, 0, 0),
                ToolTip = "当前精确帧号与视频帧率"
            };
            _readoutStack.Children.Add(_frameText);
            _bottomLeftPanel.Children.Add(_readoutStack);

            Grid.SetColumn(_bottomLeftPanel, 0);
            transport.Children.Add(_bottomLeftPanel);

            // === 中区：定点跳转胶囊（置于 ClipToBounds 容器内，窄窗自动折叠次要项，绝不越界遮挡） ===
            var centerHost = new Grid
            {
                Margin = new Thickness(8, 0, 8, 0),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            _jumpCapsule = Theme.MakeCapsule(new Thickness(7, 3, 3, 3));
            _jumpCapsule.HorizontalAlignment = HorizontalAlignment.Center;
            var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _jumpLabel = Theme.Label("定点", Theme.TextDim, 11.5);
            _jumpLabel.Margin = new Thickness(1, 0, 6, 0);
            jumpRow.Children.Add(_jumpLabel);

            _jumpBox = new TextBox
            {
                Width = 78,
                Height = 26,
                Text = "00:00:05",
                Focusable = false,
                IsTabStop = false,
                FontFamily = Theme.Mono,
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0),
                ToolTip = "输入秒数(如 85.5)或时间(如 01:25 或 00:01:25.500)，按回车精准定位"
            };
            Theme.StyleDarkTextBox(_jumpBox);
            InputMethod.SetIsInputMethodEnabled(_jumpBox, false);
            _jumpBox.PreviewMouseDown += (s, e) =>
            {
                if (!_jumpBox.Focusable) _jumpBox.Focusable = true;
            };
            _jumpBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { JumpToInput(false); e.Handled = true; }
                else if (e.Key == Key.Escape) { FocusMain(); e.Handled = true; }
            };
            jumpRow.Children.Add(_jumpBox);

            _jumpGoBtn = Theme.MakeButton("跳转", 44, 26);
            _jumpGoBtn.FontSize = 11.5;
            _jumpGoBtn.Margin = new Thickness(4, 0, 0, 0);
            _jumpGoBtn.ToolTip = "精准定位到该时间点 (Enter)";
            _jumpGoBtn.Click += (s, e) => JumpToInput(false);
            jumpRow.Children.Add(_jumpGoBtn);

            _jumpShotBtn = Theme.MakeFlexButton("截全帧", 26, 8, true);
            _jumpShotBtn.FontSize = 11.5;
            _jumpShotBtn.Margin = new Thickness(4, 0, 0, 0);
            _jumpShotBtn.ToolTip = "精准定位到该时间点并直接保存整帧原画截图";
            _jumpShotBtn.Click += (s, e) => JumpToInput(true);
            jumpRow.Children.Add(_jumpShotBtn);

            _jumpCapsule.Child = jumpRow;
            centerHost.Children.Add(_jumpCapsule);
            Grid.SetColumn(centerHost, 1);
            transport.Children.Add(centerHost);

            // === 右区：历史截图队列 + 暗色倍速下拉 + 音量锁0胶囊 + 全屏 ===
            _bottomRightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 1) 历史截图队列按钮（带独立计数胶囊角标）
            _historyBtn = BuildHistoryTriggerButton();
            _bottomRightPanel.Children.Add(_historyBtn);

            _rightDiv1 = Theme.MakeDivider(18, 8);
            _bottomRightPanel.Children.Add(_rightDiv1);

            // 2) 纯暗色倍速选择器（向上弹出，告别刺眼白框）
            _speedCombo = new ComboBox
            {
                Width = 68,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "切换播放倍速"
            };
            Theme.StyleDarkComboBox(_speedCombo);
            foreach (double sp in Speeds) _speedCombo.Items.Add(sp.ToString("0.##") + "x");
            _speedCombo.SelectedIndex = 3;
            _speedCombo.SelectionChanged += (s, e) =>
            {
                if (_speedCombo.SelectedIndex >= 0 && _player != null) _player.SetSpeed(Speeds[_speedCombo.SelectedIndex]);
                FocusMain();
            };
            _speedCombo.DropDownClosed += (s, e) => FocusMain();
            _bottomRightPanel.Children.Add(_speedCombo);

            _rightDiv2 = Theme.MakeDivider(18, 8);
            _bottomRightPanel.Children.Add(_rightDiv2);

            // 3) 音量与「锁0」一体胶囊
            var audioCapsule = Theme.MakeCapsule(new Thickness(3, 2, 5, 2));
            var audioRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var lockContent = new StackPanel { Orientation = Orientation.Horizontal };
            _volumeLockIcon = Theme.MakeIcon(Theme.GeoLock, Theme.LockedText, 11.5);
            _volumeLockText = new TextBlock { Text = "锁0", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lockContent.Children.Add(_volumeLockIcon);
            lockContent.Children.Add(_volumeLockText);
            _volumeLockBtn = Theme.MakeFlexButton(lockContent, 28, 8, false);
            _volumeLockBtn.ToolTip = "彻底锁定音量为 0（默认开启防打扰；点击解锁后可从 0% 重新调节音量）";
            _volumeLockBtn.Click += (s, e) => ToggleVolumeLock();
            audioRow.Children.Add(_volumeLockBtn);

            _muteIconPath = Theme.MakeIcon(Theme.GeoVolumeMute, Theme.TextDim, 13);
            _muteBtn = Theme.MakeCapsuleButton(_muteIconPath, 28, 28);
            _muteBtn.Margin = new Thickness(3, 0, 0, 0);
            _muteBtn.ToolTip = "静音 / 取消静音 (快捷键 M)";
            _muteBtn.Click += (s, e) => ToggleMute();
            audioRow.Children.Add(_muteBtn);

            // 音量滑块：内部预留 6px 圆点半径边距，使 0% 时圆点绝不越界压住左侧静音按钮边框
            _volumeArea = new Grid
            {
                Width = 68,
                Height = 26,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = "单击或拖动调节音量（需先点击左侧「锁0」解锁）"
            };
            _volumeTrackBg = new Border
            {
                Height = 5,
                Margin = new Thickness(6, 0, 6, 0),
                Background = Theme.Track,
                CornerRadius = new CornerRadius(2.5),
                VerticalAlignment = VerticalAlignment.Center
            };
            _volumeArea.Children.Add(_volumeTrackBg);
            _volumeFill = new Border
            {
                Height = 5,
                Width = 0,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Theme.AccentFill,
                CornerRadius = new CornerRadius(2.5),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _volumeArea.Children.Add(_volumeFill);
            _volumeThumb = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                Stroke = Theme.AccentFill,
                StrokeThickness = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 0),
                IsHitTestVisible = false
            };
            _volumeArea.Children.Add(_volumeThumb);
            _volumeArea.SizeChanged += (s, e) => UpdateVolumeUi();
            audioRow.Children.Add(_volumeArea);

            _volumeText = new TextBlock
            {
                Width = 36,
                FontFamily = Theme.Mono,
                FontSize = 11,
                TextAlignment = TextAlignment.Right,
                Foreground = Theme.TextDim,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0)
            };
            audioRow.Children.Add(_volumeText);

            audioCapsule.Child = audioRow;
            _bottomRightPanel.Children.Add(audioCapsule);

            // 4) 全屏切换按钮（矢量四角图标，不乱码）
            var fsContent = new StackPanel { Orientation = Orientation.Horizontal };
            _fsIconPath = Theme.MakeIcon(Theme.GeoFullscreen, Theme.TextMain, 12.5);
            _fsBtnText = new TextBlock { Text = "全屏", FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            fsContent.Children.Add(_fsIconPath);
            fsContent.Children.Add(_fsBtnText);
            _fsBtn = Theme.MakeFlexButton(fsContent, 32, 10, false);
            _fsBtn.Margin = new Thickness(8, 0, 0, 0);
            _fsBtn.ToolTip = "全屏 / 窗口切换 (快捷键 F / F11，或双击视频画面)";
            _fsBtn.Click += (s, e) => ToggleFullscreen();
            _bottomRightPanel.Children.Add(_fsBtn);

            Grid.SetColumn(_bottomRightPanel, 2);
            transport.Children.Add(_bottomRightPanel);

            Grid.SetRow(transport, 1);
            rows.Children.Add(transport);
            _bottomContainer.Child = rows;
            _bottomBar.Children.Add(_bottomContainer);
            return _bottomBar;
        }

        internal void UpdateFullscreenButtonUi()
        {
            if (_fsIconPath == null) return;
            bool fs = WindowStyle == WindowStyle.None;
            _fsIconPath.Data = fs ? Theme.GeoExitFullscreen : Theme.GeoFullscreen;
            _fsIconPath.Fill = fs ? Theme.Accent : Theme.TextMain;
            if (_fsBtnText != null) _fsBtnText.Text = fs ? "退全屏" : "全屏";
        }

        /// <summary>按当前窗口宽度平滑折叠次要元素，确保从 820px 最小宽度到 4K 全屏均无任何控件遮挡。</summary>
        internal void UpdateResponsiveLayout()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            bool wide = w >= 1150;
            bool medium = w >= 980;
            bool compact = w < 930;

            if (_bottomContainer != null)
                _bottomContainer.Padding = new Thickness(compact ? 10 : 14, 7, compact ? 10 : 14, 9);

            if (_stepBackText != null) _stepBackText.Text = compact ? "-1" : "-1帧";
            if (_stepBackBtn != null) _stepBackBtn.Width = compact ? 38 : 52;
            if (_stepFwdText != null) _stepFwdText.Text = compact ? "+1" : "+1帧";
            if (_stepFwdBtn != null) _stepFwdBtn.Width = compact ? 38 : 52;
            if (_readoutStack != null)
            {
                _readoutStack.MinWidth = compact ? 152 : 178;
                _readoutStack.Margin = new Thickness(compact ? 7 : 12, 0, 0, 0);
            }

            if (_jumpLabel != null) _jumpLabel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            if (_jumpShotBtn != null) _jumpShotBtn.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            if (_jumpBox != null) _jumpBox.Width = compact ? 66 : 78;
            if (_jumpGoBtn != null) _jumpGoBtn.Width = compact ? 38 : 44;

            if (_rightDiv1 != null) _rightDiv1.Margin = new Thickness(compact ? 4 : 8, 0, compact ? 4 : 8, 0);
            if (_rightDiv2 != null) _rightDiv2.Margin = new Thickness(compact ? 4 : 8, 0, compact ? 4 : 8, 0);
            if (_speedCombo != null) _speedCombo.Width = compact ? 58 : 68;
            if (_volumeLockText != null) _volumeLockText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            if (_fsBtn != null) _fsBtn.Margin = new Thickness(compact ? 5 : 8, 0, 0, 0);

            if (_volumeText != null) _volumeText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            if (_volumeArea != null)
            {
                bool showSlider = wide || (medium && !_settings.VolumeZeroLocked);
                _volumeArea.Visibility = showSlider ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_fsBtnText != null) _fsBtnText.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
            if (_normalFlipText != null)
            {
                _normalFlipText.Text = compact ? (_videoMirrored ? "已镜像" : "镜像") : (_videoMirrored ? "已镜像 (Tab)" : "镜像 (Tab)");
            }
            if (_historyBtnText != null)
            {
                _historyBtnText.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
                _historyBtnText.Text = wide ? "历史截图" : "历史";
            }
            if (_videoSpecBadge != null)
            {
                bool hasRealVideo = _player != null && _player.CurrentPath != null && _player.HasVideo;
                _videoSpecBadge.Visibility = (hasRealVideo && w >= 960)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        internal void PopulateSampleReadoutForTest()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            if (_playlistBadge != null) _playlistBadge.Visibility = Visibility.Visible;
            if (_playlistCounterText != null) _playlistCounterText.Text = "12 / 128";
            if (_titleText != null)
            {
                _titleText.Text = "示例高精度分镜参考视频_4K60fps_HEVC_Main10_超长文件名防挤压测试样本.mp4";
                _titleText.Foreground = Theme.TextMain;
                _titleText.FontWeight = FontWeights.SemiBold;
            }
            if (_videoSpecText != null) _videoSpecText.Text = "3840×2160";
            if (_videoSpecBadge != null) _videoSpecBadge.Visibility = w >= 960 ? Visibility.Visible : Visibility.Collapsed;
            if (_timeCurrentText != null) _timeCurrentText.Text = "00:26:23.116";
            if (_timeTotalText != null) _timeTotalText.Text = "00:54:34.048";
            if (_frameText != null)
            {
                _frameText.Text = "帧 #94892 · 59.94 fps";
                _frameText.Foreground = Theme.Frozen(Color.FromRgb(110, 192, 222));
            }
            if (_timeline != null && _timeline.ActualWidth > 0)
            {
                double px = 0.484 * _timeline.ActualWidth;
                _timelineFill.Width = px;
                _timelineThumb.Margin = new Thickness(px - 7, 0, 0, 0);
            }
        }

        internal void SetMockScreenshotBarForTest(bool active, bool flipped)
        {
            _videoMirrored = flipped;
            UpdateShotFlipUi();
            _topScreenshotBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            _bottomBar.Opacity = active ? 0.52 : 1.0;
        }

        // ======================= 浮层 =======================

        private void BuildPopups()
        {
            // 时间轴悬停缩略图：用 Popup（独立顶层窗口），可以盖在原生视频窗口之上
            var hoverBorder = new Border
            {
                Width = 198,
                Padding = new Thickness(6),
                Background = Theme.PopupBg,
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(7)
            };
            var hs = new StackPanel();
            var imgBorder = new Border
            {
                Width = 184,
                Height = 104,
                Background = Brushes.Black,
                BorderBrush = Theme.Frozen(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true
            };
            _hoverImage = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_hoverImage, BitmapScalingMode.HighQuality);
            imgBorder.Child = _hoverImage;
            hs.Children.Add(imgBorder);
            _hoverTimeText = new TextBlock
            {
                FontFamily = Theme.Mono,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            };
            hs.Children.Add(_hoverTimeText);
            hoverBorder.Child = hs;
            _hoverPopup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Relative,
                PlacementTarget = _timeline,
                StaysOpen = true,
                Focusable = false,
                IsHitTestVisible = false,
                Child = hoverBorder
            };

            BuildHistoryPopup();
        }

        // ======================= 通用小工具 =======================

        private void ShowToast(string message)
        {
            _toastText.Text = message;
            _toastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void PickSaveFolder()
        {
            _inModalDialog = true;
            UpdateShotShield();
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择截图默认保存目录";
                    dlg.SelectedPath = _settings.SaveFolderPath;
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                    {
                        _settings.SaveFolderPath = dlg.SelectedPath;
                        UpdateSaveFolderDisplay();
                        RefreshHistoryCounters();
                        _settings.Save();
                    }
                }
            }
            finally
            {
                _inModalDialog = false;
                FocusMain();
                UpdateShotShield();
            }
        }

        private void OpenSaveFolder()
        {
            try
            {
                HideShotShield();
                Directory.CreateDirectory(_settings.SaveFolderPath);
                Process.Start("explorer.exe", "\"" + _settings.SaveFolderPath + "\"");
            }
            catch (Exception ex)
            {
                ShowToast("无法打开文件夹: " + ex.Message);
            }
        }
    }
}
