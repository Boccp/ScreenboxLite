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
    ///   Row 0  48px  顶栏（普通标题 / 截图模式保存栏，原位切换）
    ///   Row 1  *     视频舞台（mpv 宿主窗口 + 截图模式的冻结帧与选框覆盖层）
    ///   Row 2  96px  底栏（时间轴 + 播放控制）
    /// 本文件只负责搭建界面；播放、截图、输入分别在同名 partial 文件中。
    /// </summary>
    internal sealed partial class MainWindow : Window
    {
        private readonly Settings _settings;
        private readonly string _startupFile;

        // ---- Row 0 ----
        private Grid _topNormalHeader;
        private Grid _topScreenshotBar;
        private TextBlock _titleText;
        private TextBlock _playlistCounterText;
        private TextBlock _saveLocationText;
        private Border _toastBorder;
        private TextBlock _toastText;
        private DispatcherTimer _toastTimer;

        // ---- Row 1 ----
        private Grid _stage;
        private VideoHost _videoHost;
        private Image _frozenImage;
        private StackPanel _emptyPanel;
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
        private Grid _timeline;
        private Border _timelineFill;
        private Border _timelineHoverMarker;
        private Ellipse _timelineThumb;
        private TextBlock _playIcon;
        private TextBlock _timeText;
        private TextBlock _frameText;
        private TextBox _jumpBox;
        private ComboBox _speedCombo;
        private Button _volumeLockBtn;
        private Button _muteBtn;
        private Grid _volumeArea;
        private Border _volumeFill;
        private Ellipse _volumeThumb;
        private TextBlock _volumeText;

        // ---- 浮层（独立窗口，可盖在视频之上） ----
        private Popup _hoverPopup;
        private Image _hoverImage;
        private TextBlock _hoverTimeText;
        private Popup _shotPreviewPopup;
        private Image _shotPreviewImage;
        private TextBlock _shotPreviewName;
        private TextBlock _shotPreviewPath;
        private DispatcherTimer _shotPreviewTimer;

        private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 };

        public MainWindow(string startupFile)
        {
            _startupFile = startupFile;
            _settings = Settings.Load();

            Title = "Screenbox (画框外UI + ~键原生截图 + 时间轴缩略图版)";
            Width = 1280;
            Height = 820;
            MinWidth = 820;
            MinHeight = 540;
            Background = Theme.WindowBg;
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowDrop = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
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

        // ======================= Row 0 =======================

        private UIElement BuildTopBar()
        {
            var bar = new Border
            {
                Height = 48,
                Background = Theme.TopBarBg,
                BorderBrush = Theme.Divider,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 0, 14, 0)
            };
            var container = new Grid();

            _topNormalHeader = new Grid();
            _topNormalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _topNormalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var openBtn = Theme.MakeButton("📂 打开视频 (Ctrl+O)", 135);
            openBtn.Click += (s, e) => OpenFilePicker();
            left.Children.Add(openBtn);
            var shotBtn = Theme.MakeButton("✂ ~键截图模式", 120);
            shotBtn.Margin = new Thickness(8, 0, 0, 0);
            shotBtn.ToolTip = "快捷键 ~ (波浪号键)：进入/快速保存截图（不改变视频画面比例和位置）";
            shotBtn.Click += (s, e) => HandleTilde();
            left.Children.Add(shotBtn);
            _topNormalHeader.Children.Add(left);

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
            _playlistCounterText = new TextBlock { Foreground = Theme.Accent, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _titleText = new TextBlock
            {
                Text = "未打开视频 — 可直接拖入视频文件、点击左上角打开，或用本程序关联视频直接双击打开",
                Foreground = Theme.TextMain,
                FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(_playlistCounterText);
            titleStack.Children.Add(_titleText);
            Grid.SetColumn(titleStack, 1);
            _topNormalHeader.Children.Add(titleStack);
            container.Children.Add(_topNormalHeader);

            // 截图模式保存栏：与普通标题共用同一 48px 行，原位切换
            _topScreenshotBar = new Grid { Background = Theme.Frozen(Color.FromRgb(22, 42, 48)), Margin = new Thickness(-14, 0, -14, 0), Visibility = Visibility.Collapsed };
            var inner = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var saveLabel = Theme.Label("📷 默认截图保存位置：", Theme.Accent, 12.5);
            saveLabel.FontWeight = FontWeights.SemiBold;
            inner.Children.Add(saveLabel);
            var saveBox = new Border
            {
                Height = 24,
                Background = Theme.Frozen(Color.FromRgb(18, 28, 34)),
                BorderBrush = Theme.Frozen(Color.FromArgb(110, 0, 180, 210)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(6, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _saveLocationText = new TextBlock { Text = _settings.SaveFolderPath, Foreground = Theme.Frozen(Color.FromRgb(215, 235, 242)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            saveBox.Child = _saveLocationText;
            Grid.SetColumn(saveBox, 1);
            inner.Children.Add(saveBox);
            var tip = Theme.Label("[~截图模式] 拖拽/双点定框 | [R]恢复上次框 | [~]保存 | 滚轮:帧(Ctrl:1s) | A/D:5x快退进(S+A/D:帧) | 1/2/3(E倒):5s/20s/1m",
                Theme.Frozen(Color.FromRgb(190, 225, 240)), 11.5);
            tip.TextTrimming = TextTrimming.CharacterEllipsis;
            tip.Margin = new Thickness(4, 0, 12, 0);
            Grid.SetColumn(tip, 2);
            inner.Children.Add(tip);
            var folderBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var changeBtn = Theme.MakeButton("更改目录", 64, 22);
            changeBtn.FontSize = 11;
            changeBtn.Click += (s, e) => PickSaveFolder();
            var openFolderBtn = Theme.MakeButton("打开文件夹", 72, 22);
            openFolderBtn.FontSize = 11;
            openFolderBtn.Margin = new Thickness(6, 0, 0, 0);
            openFolderBtn.Click += (s, e) => OpenSaveFolder();
            folderBtns.Children.Add(changeBtn);
            folderBtns.Children.Add(openFolderBtn);
            Grid.SetColumn(folderBtns, 3);
            inner.Children.Add(folderBtns);
            _topScreenshotBar.Children.Add(inner);
            container.Children.Add(_topScreenshotBar);

            _toastBorder = new Border
            {
                Background = Theme.Frozen(Color.FromArgb(235, 0, 120, 215)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _toastText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
            _toastBorder.Child = _toastText;
            container.Children.Add(_toastBorder);
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); _toastBorder.Visibility = Visibility.Collapsed; };

            bar.Child = container;
            return bar;
        }

        // ======================= Row 1 =======================

        private UIElement BuildStage()
        {
            _stage = new Grid { Background = Brushes.Black, ClipToBounds = true };

            // 截图模式下显示的冻结帧（与 mpv 画面同为等比居中），位于宿主窗口之下
            _frozenImage = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_frozenImage, BitmapScalingMode.HighQuality);
            _stage.Children.Add(_frozenImage);

            _videoHost = new VideoHost { Visibility = Visibility.Hidden };
            _stage.Children.Add(_videoHost);

            _emptyPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _emptyPanel.Children.Add(new TextBlock { Text = "拖入视频文件 或 点击「打开视频」开始播放", FontSize = 18, Foreground = Theme.TextDim, HorizontalAlignment = HorizontalAlignment.Center });
            _emptyPanel.Children.Add(new TextBlock
            {
                Text = "滚轮:单帧(Ctrl/Shift:1s) | A/D:5x快退进(S+A/D:单帧) | 1/2/3(E倒):5s/20s/1m | [~]截图([R]恢复选框)",
                FontSize = 13,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(0, 10, 0, 18),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var emptyOpen = Theme.MakeButton("📂 选择视频文件打开...", 180, 36);
            emptyOpen.HorizontalAlignment = HorizontalAlignment.Center;
            emptyOpen.Click += (s, e) => OpenFilePicker();
            _emptyPanel.Children.Add(emptyOpen);
            _stage.Children.Add(_emptyPanel);

            _stage.Children.Add(BuildScreenshotOverlay());
            return _stage;
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
                Background = Theme.Frozen(Color.FromArgb(230, 20, 20, 20)),
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
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
                Width = 158,
                Padding = new Thickness(6),
                Background = Theme.Frozen(Color.FromArgb(242, 22, 22, 22)),
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6)
            };
            var magStack = new StackPanel();
            var magGrid = new Grid { Width = 140, Height = 140, ClipToBounds = true, Background = Brushes.Black };
            _magnifierBrush = new ImageBrush { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
            _magnifierView = new Rectangle { Width = 140, Height = 140, Fill = _magnifierBrush };
            RenderOptions.SetBitmapScalingMode(_magnifierView, BitmapScalingMode.NearestNeighbor);
            magGrid.Children.Add(_magnifierView);
            var crossBrush = Theme.Frozen(Color.FromArgb(170, 0, 229, 255));
            magGrid.Children.Add(new Line { X1 = 70, Y1 = 0, X2 = 70, Y2 = 140, Stroke = crossBrush, StrokeThickness = 1 });
            magGrid.Children.Add(new Line { X1 = 0, Y1 = 70, X2 = 140, Y2 = 70, Stroke = crossBrush, StrokeThickness = 1 });
            magGrid.Children.Add(new Rectangle { Width = 10, Height = 10, Stroke = Theme.Reticle, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            magStack.Children.Add(new Border { BorderBrush = Theme.Frozen(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1), Child = magGrid });
            _magnifierCoord = new TextBlock { Text = "X: 0, Y: 0", FontFamily = Theme.Mono, FontSize = 11.5, Foreground = Theme.TextMain, Margin = new Thickness(0, 5, 0, 2) };
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

        // ======================= Row 2 =======================

        private UIElement BuildBottomBar()
        {
            _bottomBar = new Grid { Height = 96, Background = Theme.BottomBarBg };
            var container = new Border { BorderBrush = Theme.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 8, 16, 10) };
            var rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 时间轴
            _timeline = new Grid { Height = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            _timeline.Children.Add(new Border { Height = 6, Background = Theme.Track, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center });
            _timelineFill = new Border { Height = 6, Width = 0, Background = Theme.AccentFill, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            _timeline.Children.Add(_timelineFill);
            _timelineHoverMarker = new Border { Width = 2, Height = 14, Background = Theme.HoverMarker, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            _timeline.Children.Add(_timelineHoverMarker);
            _timelineThumb = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, Stroke = Theme.AccentFill, StrokeThickness = 2.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-7, 0, 0, 0), IsHitTestVisible = false };
            _timeline.Children.Add(_timelineThumb);
            Grid.SetRow(_timeline, 0);
            rows.Children.Add(_timeline);

            var transport = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左：播放/切换/逐帧/时间
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var playBtn = Theme.MakeButton("", 46, 34);
            playBtn.ToolTip = "播放 / 暂停 (Space 或 K)";
            _playIcon = new TextBlock { Text = "▶", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
            playBtn.Content = _playIcon;
            playBtn.Click += (s, e) => TogglePlayPause();
            left.Children.Add(playBtn);
            left.Children.Add(Spaced(Theme.MakeButton("⏮ 上一个", 82, 34), 6, "切换同目录上一个视频 (PageUp / Shift+P)", () => SwitchNeighbor(-1)));
            left.Children.Add(Spaced(Theme.MakeButton("⏭ 下一个", 82, 34), 6, "切换同目录下一个视频 (PageDown / Shift+N)", () => SwitchNeighbor(1)));
            left.Children.Add(Spaced(Theme.MakeButton("⏪ -1帧", 66, 34), 10, "上一帧 (快捷键 , 逗号)", () => _nav.StepFrames(-1)));
            left.Children.Add(Spaced(Theme.MakeButton("+1帧 ⏩", 66, 34), 4, "下一帧 (快捷键 . 句号)", () => _nav.StepFrames(1)));
            _timeText = new TextBlock { Text = "00:00:00.000 / 00:00:00.000", FontFamily = Theme.Mono, FontSize = 13.5, Foreground = Theme.Frozen(Color.FromRgb(230, 230, 230)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            left.Children.Add(_timeText);
            _frameText = new TextBlock { Text = "", FontFamily = Theme.Mono, FontSize = 12, Foreground = Theme.TextFaint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = "当前帧号 / 帧率" };
            left.Children.Add(_frameText);
            transport.Children.Add(left);

            // 中：定点跳转
            var center = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var jumpLabel = Theme.Label("定点跳转:", Theme.TextDim, 12.5);
            jumpLabel.Margin = new Thickness(0, 0, 6, 0);
            center.Children.Add(jumpLabel);
            _jumpBox = new TextBox
            {
                Width = 92,
                Height = 28,
                Text = "00:00:05",
                Background = Theme.Frozen(Color.FromRgb(34, 34, 34)),
                Foreground = Brushes.White,
                BorderBrush = Theme.ButtonBorder,
                FontFamily = Theme.Mono,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0),
                ToolTip = "输入秒数(如 85.5)或时间(如 01:25 或 00:01:25.500)，按回车直接跳转"
            };
            InputMethod.SetIsInputMethodEnabled(_jumpBox, true);
            _jumpBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { JumpToInput(false); e.Handled = true; }
                else if (e.Key == Key.Escape) { FocusMain(); e.Handled = true; }
            };
            center.Children.Add(_jumpBox);
            center.Children.Add(Spaced(Theme.MakeButton("跳转", 52, 28), 4, null, () => JumpToInput(false)));
            center.Children.Add(Spaced(Theme.MakeButton("跳转并全屏截图", 112, 28), 6, null, () => JumpToInput(true)));
            Grid.SetColumn(center, 1);
            transport.Children.Add(center);

            // 右：倍速/音量/全屏
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var speedLabel = Theme.Label("倍速:", Theme.TextDim, 12);
            speedLabel.Margin = new Thickness(0, 0, 4, 0);
            right.Children.Add(speedLabel);
            _speedCombo = new ComboBox { Width = 68, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Focusable = false };
            foreach (double sp in Speeds) _speedCombo.Items.Add(sp.ToString("0.##") + "x");
            _speedCombo.SelectedIndex = 3;
            _speedCombo.SelectionChanged += (s, e) =>
            {
                if (_speedCombo.SelectedIndex >= 0) _player.SetSpeed(Speeds[_speedCombo.SelectedIndex]);
                FocusMain();
            };
            right.Children.Add(_speedCombo);

            _volumeLockBtn = Theme.MakeButton("🔒 锁0", 62, 28);
            _volumeLockBtn.Margin = new Thickness(0, 0, 6, 0);
            _volumeLockBtn.ToolTip = "彻底锁定音量为 0（默认开启；开启时强制归零并锁定，点击关闭后可从 0 重新调节音量）";
            _volumeLockBtn.Click += (s, e) => ToggleVolumeLock();
            right.Children.Add(_volumeLockBtn);
            _muteBtn = Theme.MakeButton("🔇", 34, 28);
            _muteBtn.Margin = new Thickness(0, 0, 6, 0);
            _muteBtn.ToolTip = "静音 / 取消静音 (快捷键 M)";
            _muteBtn.Click += (s, e) => ToggleMute();
            right.Children.Add(_muteBtn);

            _volumeArea = new Grid { Width = 92, Height = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "单击或拖动调节音量（需先关闭旁边的「锁0」按钮）" };
            _volumeArea.Children.Add(new Border { Height = 6, Background = Theme.Track, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center });
            _volumeFill = new Border { Height = 6, Width = 0, Background = Theme.AccentFill, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            _volumeArea.Children.Add(_volumeFill);
            _volumeThumb = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Stroke = Theme.AccentFill, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 0), IsHitTestVisible = false };
            _volumeArea.Children.Add(_volumeThumb);
            _volumeArea.SizeChanged += (s, e) => UpdateVolumeUi();
            right.Children.Add(_volumeArea);
            _volumeText = new TextBlock { Width = 38, FontFamily = Theme.Mono, FontSize = 11.5, Foreground = Theme.Frozen(Color.FromRgb(210, 210, 210)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            right.Children.Add(_volumeText);
            var fsBtn = Theme.MakeButton("⛶ 全屏 (F)", 82, 34);
            fsBtn.Click += (s, e) => ToggleFullscreen();
            right.Children.Add(fsBtn);
            Grid.SetColumn(right, 2);
            transport.Children.Add(right);

            Grid.SetRow(transport, 1);
            rows.Children.Add(transport);
            container.Child = rows;
            _bottomBar.Children.Add(container);
            return _bottomBar;
        }

        private static Button Spaced(Button b, double leftMargin, string tip, Action onClick)
        {
            b.Margin = new Thickness(leftMargin, 0, 0, 0);
            if (tip != null) b.ToolTip = tip;
            b.Click += (s, e) => onClick();
            return b;
        }

        // ======================= 浮层 =======================

        private void BuildPopups()
        {
            // 时间轴悬停缩略图：用 Popup（独立顶层窗口），可以盖在原生视频窗口之上
            var hoverBorder = new Border
            {
                Width = 196,
                Padding = new Thickness(5),
                Background = Theme.PopupBg,
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6)
            };
            var hs = new StackPanel();
            var imgBorder = new Border { Width = 182, Height = 102, Background = Brushes.Black, BorderBrush = Theme.Frozen(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ClipToBounds = true };
            _hoverImage = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_hoverImage, BitmapScalingMode.HighQuality);
            imgBorder.Child = _hoverImage;
            hs.Children.Add(imgBorder);
            _hoverTimeText = new TextBlock { FontFamily = Theme.Mono, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
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

            // 截图保存成功的小卡片：显示在屏幕左下角，不遮挡画面
            var card = new Border
            {
                Width = 370,
                Height = 64,
                Padding = new Thickness(8, 5, 10, 5),
                Background = Theme.Frozen(Color.FromArgb(248, 28, 32, 36)),
                BorderBrush = Theme.Accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                ToolTip = "点击在资源管理器中定位该截图"
            };
            card.MouseLeftButtonUp += (s, e) => RevealLastScreenshot();
            var cs = new StackPanel { Orientation = Orientation.Horizontal };
            var thumbBorder = new Border { Width = 92, Height = 52, Background = Brushes.Black, BorderBrush = Theme.Frozen(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0) };
            _shotPreviewImage = new Image { Stretch = Stretch.Uniform };
            thumbBorder.Child = _shotPreviewImage;
            cs.Children.Add(thumbBorder);
            var ts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 246 };
            ts.Children.Add(new TextBlock { Text = "✔ 已自动保存截图（点击定位文件）", Foreground = Theme.Accent, FontWeight = FontWeights.SemiBold, FontSize = 12 });
            _shotPreviewName = new TextBlock { Foreground = Brushes.White, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1) };
            _shotPreviewPath = new TextBlock { Foreground = Theme.Frozen(Color.FromRgb(165, 165, 165)), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
            ts.Children.Add(_shotPreviewName);
            ts.Children.Add(_shotPreviewPath);
            cs.Children.Add(ts);
            card.Child = cs;
            _shotPreviewPopup = new Popup { AllowsTransparency = true, Placement = PlacementMode.AbsolutePoint, StaysOpen = true, Focusable = false, Child = card };
            _shotPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _shotPreviewTimer.Tick += (s, e) => { _shotPreviewTimer.Stop(); _shotPreviewPopup.IsOpen = false; };
        }

        // ======================= 通用小工具 =======================

        private void ShowToast(string message)
        {
            _toastText.Text = message;
            _toastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void FocusMain()
        {
            Focus();
            Keyboard.Focus(this);
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
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择截图默认保存目录";
                dlg.SelectedPath = _settings.SaveFolderPath;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    _settings.SaveFolderPath = dlg.SelectedPath;
                    _saveLocationText.Text = dlg.SelectedPath;
                    _settings.Save();
                }
            }
            FocusMain();
        }

        private void OpenSaveFolder()
        {
            try
            {
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
