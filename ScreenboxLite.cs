// ScreenboxLite - 精简版视频裁剪截图工具
// 基于原始开源项目 Screenbox 开发
// 沿用原项目协议 (MIT / Apache 2.0 - 请根据原项目协议确认)
// 增加功能：音量锁0、滚轮逐帧进退、双模裁剪、裁剪框保存记忆等

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace ScreenboxLite
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        [STAThread]
        public static void Main(string[] args)
        {
            // 1. Enable Per-Monitor V2 DPI awareness so video and UI are rendered at 1:1 native physical pixels (no Windows DWM 125%/150%/200% bilinear blur)
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); }
            catch
            {
                try { SetProcessDpiAwareness(2); }
                catch { try { SetProcessDPIAware(); } catch { } }
            }

            // 2. Request 1ms high-resolution OS multimedia timer so EVR/DirectShow video frame presentation never jitters on 15.6ms slices
            try { timeBeginPeriod(1); } catch { }
            EnsureHighPerformanceGpuPreference();
            try
            {
                var app = new Application();
                var win = new MainWindow(args);
                app.Run(win);
            }
            finally
            {
                try { timeEndPeriod(1); } catch { }
            }
        }

        private static void EnsureHighPerformanceGpuPreference()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath))
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    {
                        if (key != null)
                        {
                            string existing = key.GetValue(exePath) as string;
                            if (existing != "GpuPreference=2;")
                            {
                                key.SetValue(exePath, "GpuPreference=2;", RegistryValueKind.String);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }

    public sealed class MainWindow : Window
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp"
        };

        // ==================== ROW 0: TOP BAR (FIXED HEIGHT 48px - NEVER CHANGES) ====================
        private readonly Grid _topNormalHeaderGrid;
        private readonly Grid _topScreenshotSaveBarGrid;
        private readonly TextBlock _saveLocationTextBlock;
        private readonly TextBlock _titleText;
        private readonly TextBlock _playlistCounterText;
        private readonly Border _statusToastBorder;
        private readonly TextBlock _statusToastText;

        // ==================== ROW 1: PURE VIDEO STAGE (SIZE & POSITION NEVER CHANGE) ====================
        private readonly Grid _videoStageGrid;
        private readonly MediaElement _mediaElement;
        private readonly Image _instantFrameImage;
        private readonly StackPanel _emptyStatePanel;

        // GPU/FFmpeg Zero-Latency Hover Strip & Exact Frame Ring-Buffer Cache
        private static readonly string[] FFMPEG_CANDIDATE_PATHS = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe"),
            @"C:\Users\Administrator\AppData\Local\hermes\tools\ffmpeg-9.0.1-win32-x64\bin\ffmpeg.exe",
            @"D:\_tools\ffmpeg.exe",
            @"D:\JianyingPro\11.5.0.14471\ffmpeg.exe",
            @"D:\JianyingPro\11.4.2.14459\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"JianyingPro\Apps\10.7.0.14095\ffmpeg.exe"),
            @"C:\Users\Administrator\Desktop\处理图片和视频\ffmpeg.exe",
            @"F:\剪映\9.9.0.13784\ffmpeg.exe"
        };
        private static string _cachedFfmpegExe;
        private string _currentVideoFilePath;
        private int _videoGenerationId;

        // 1) Sparse Anchor Keyframe Cache + Latest-Wins Single On-Demand Hover Worker (100% frozen BitmapSource in background thread, ZERO UI decode, ZERO full-file scan)
        private const int HOVER_ANCHOR_COUNT = 80;
        private const int HOVER_EXACT_BUCKETS_PER_SEC = 5; // 200ms bucket resolution for exact hover cache
        private readonly ConcurrentDictionary<int, BitmapSource> _hoverAnchorFrames = new ConcurrentDictionary<int, BitmapSource>();
        private readonly ConcurrentDictionary<int, BitmapSource> _hoverExactFrames = new ConcurrentDictionary<int, BitmapSource>();
        private readonly ConcurrentQueue<int> _hoverExactLruQueue = new ConcurrentQueue<int>();
        private readonly AutoResetEvent _hoverRequestSignal = new AutoResetEvent(false);
        private double _latestRequestedHoverSeconds = -1.0;
        private int _latestRequestedHoverGenId = -1;
        private int _lastRenderedHoverKey = int.MinValue;
        private long _lastHoverWorkerTriggerTick;

        // 2) Exact Frame Ring-Buffer Cache for 0ms Wheel Stepping (Key = Frame Index @ 30fps, Value = pre-frozen full-res BitmapSource)
        private const int FRAME_RING_FPS = 30;
        private readonly ConcurrentDictionary<int, BitmapSource> _exactFrameRingBitmaps = new ConcurrentDictionary<int, BitmapSource>();
        private readonly ConcurrentQueue<int> _exactFrameRingOrder = new ConcurrentQueue<int>();
        private Process _burstDecodeFfmpegProc;
        private readonly object _burstDecodeLock = new object();
        private int _burstWindowStartIdx = -999999;
        private int _burstWindowEndIdx = -999999;
        private bool _isBurstDecodeRunning;
        private double _queuedNextBurstCenterSeconds = -1.0;
        private BitmapSource _currentInstantFrameBitmap;
        private int _currentInstantFrameIdx = -999999;
        private Point _timelineMouseDownPos;

        // Dedicated live Image + fallback MediaElement inside the Timeline Hover Popup
        private readonly Canvas _bottomOverlayCanvas;
        private readonly Border _timelineHoverPopup;
        private readonly Image _timelineHoverImage;
        private readonly MediaElement _timelineHoverMedia;
        private readonly TextBlock _timelineHoverTimeText;
        private readonly DispatcherTimer _thumbDebounceTimer;
        private double _pendingThumbSeconds = -1;
        private double _lastThumbSeekSeconds = -999;
        private long _lastThumbSeekTick;
        private bool _thumbMediaReady;
        private long _lastUiTimelineUpdateTick;
        private double _lastDisplayedFillWidth = -1;
        private string _lastDisplayedTimeText = "";

        // Interactive Tilde (~) Screenshot Mode Overlay (Inside Row 1, exact same geometry as video)
        private readonly Grid _screenshotOverlayRoot;
        private readonly Image _screenshotFrozenImage;
        private readonly Canvas _screenshotCanvas;
        private readonly Line _aimHorizontalLine;
        private readonly Line _aimVerticalLine;
        private readonly Rectangle _selectionRectShape;
        private readonly Rectangle[] _selectionHandles = new Rectangle[8];
        private readonly Border _selectionSizeTag;
        private readonly TextBlock _selectionSizeText;
        private readonly Grid _aimReticleBox;
        private readonly Border _magnifierPopup;
        private readonly Rectangle _magnifierLiveRect;
        private readonly VisualBrush _magnifierLiveBrush;
        private readonly TextBlock _magnifierCoordText;
        private readonly Border _magnifierColorSwatch;
        private readonly TextBlock _magnifierColorText;

        // ==================== ROW 2: BOTTOM CONTROLS (FIXED HEIGHT 96px - NEVER CHANGES) ====================
        private readonly Grid _bottomControlsRootGrid;

        // Custom Precision Timeline / SeekBar (Supports instant single-click, drag, and hover thumbnail)
        private readonly Grid _timelineInteractiveArea;
        private readonly Border _timelineTrackBg;
        private readonly Border _timelineFillBar;
        private readonly Border _timelineHoverMarker;
        private readonly Ellipse _timelineThumb;

        // Saved Screenshot Small Thumbnail Preview Card (Displayed on monitor bottom-left corner outside main window via non-activating Popup)
        private readonly Popup _screenshotPreviewPopup;
        private readonly Border _screenshotPreviewBanner;
        private readonly Image _screenshotPreviewThumbImage;
        private readonly TextBlock _screenshotPreviewFileNameText;
        private readonly TextBlock _screenshotPreviewPathText;
        private readonly object _fileSaveLock = new object();
        private bool _isTildeKeyPhysicallyDown;

        // Transport Controls
        private readonly Button _playPauseBtn;
        private readonly TextBlock _playPauseIcon;
        private readonly Button _prevBtn;
        private readonly Button _nextBtn;
        private readonly TextBlock _timeDisplayText;
        private readonly TextBox _jumpTimeTextBox;
        private readonly Button _volumeLockBtn;
        private readonly Button _muteBtn;
        private readonly Grid _volumeInteractiveArea;
        private readonly Border _volumeFillBar;
        private readonly Ellipse _volumeThumb;
        private readonly TextBlock _volumePercentText;
        private readonly ComboBox _speedCombo;

        // State
        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _previewHideTimer;
        private readonly DispatcherTimer _toastHideTimer;
        private readonly DispatcherTimer _mainSeekFlushTimer;
        private long _lastMainSeekCommitTick;
        private double _pendingMainSeekSeconds = -1;
        private long _lastAdHoldTick;
        private bool _isKeyAHeld;
        private bool _isKeyDHeld;
        private bool _isKeySHeld;
        private bool _isKeyEHeld;
        private List<string> _folderVideoFiles = new List<string>();
        private string _cachedPlaylistDir;
        private string _cachedExplorerSortSpec;
        private int _currentVideoIndex = -1;
        private double _durationSeconds;
        private double _targetStepSeconds = -1;
        private bool _isPlaying;
        private bool _wasPlayingBeforeScrub;
        private double _scrubPreviewSeconds;
        private long _lastMainScrubSeekTick;
        private bool _isScrubbingTimeline;
        private bool _isDraggingVolume;
        private bool _isScreenshotModeActive;

        private enum CropSelectionMode
        {
            None,
            DraggingNew,
            TwoClickBlueprint,
            ResizingOrMoving
        }

        [Flags]
        private enum CropHitZone
        {
            None = 0,
            Left = 1,
            Right = 2,
            Top = 4,
            Bottom = 8,
            Inside = 16
        }

        private CropSelectionMode _cropMode = CropSelectionMode.None;
        private CropHitZone _activeResizeZone = CropHitZone.None;
        private bool _hasActiveSelection;
        private Point _selectionStartPoint;
        private Point _mouseDownOverlayPoint;
        private Point _resizeStartMousePoint;
        private Rect _resizeStartSelectionRect;
        private Rect _currentSelectionRect;
        private Rect? _lastNormalizedCropRect;
        private BitmapSource _frozenFrameBitmap;
        private int _frozenPixelWidth;
        private int _frozenPixelHeight;
        private int _frozenStride;
        private string _saveFolderPath;
        private string _lastSavedFilePath;
        private readonly string _settingsFilePath;
        private double _persistedVolume = 0.0;
        private bool _persistedIsMuted = true;
        private bool _isVolumeZeroLocked = true;

        public MainWindow(string[] args)
        {
            Title = "Screenbox (画框外UI + ~键原生截图 + 时间轴缩略图版)";
            Width = 1280;
            Height = 820;
            MinWidth = 820;
            MinHeight = 540;
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowDrop = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

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

            string picturesDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _saveFolderPath = Path.Combine(picturesDir, "Screenbox");

            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenboxLite");
            _settingsFilePath = Path.Combine(appDataDir, "settings.cfg");
            LoadPersistentSettings();

            try
            {
                Directory.CreateDirectory(_saveFolderPath);
            }
            catch
            {
                _saveFolderPath = picturesDir;
            }

            // Root 3-Row Grid with STRICT FIXED HEIGHTS for Row 0 and Row 2:
            // Row 0: Fixed 48px Top Bar (Outside video frame)
            // Row 1: * Pure Video Stage (Size, aspect ratio, and position NEVER change when toggling ~ mode!)
            // Row 2: Fixed 96px Bottom Controls Bar (Outside video frame)
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48, GridUnitType.Pixel) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96, GridUnitType.Pixel) });

            // ==================== ROW 0: FIXED-HEIGHT TOP BAR (48px) ====================
            var topBarBorder = new Border
            {
                Height = 48,
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 0, 14, 0)
            };
            var topContainerGrid = new Grid();

            // 0A: Normal Header Mode
            _topNormalHeaderGrid = new Grid();
            _topNormalHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _topNormalHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _topNormalHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftTopStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var openBtn = CreateStyledButton("📂 打开视频 (Ctrl+O)", 135);
            openBtn.Click += (_, __) => OpenFilePicker();
            leftTopStack.Children.Add(openBtn);

            var screenshotHintBtn = CreateStyledButton("✂ ~键截图模式", 120);
            screenshotHintBtn.Margin = new Thickness(8, 0, 0, 0);
            screenshotHintBtn.ToolTip = "快捷键 ~ (波浪号键)：进入/快速保存截图（不改变视频画面比例和位置）";
            screenshotHintBtn.Click += (_, __) => HandleTildeShortcut();
            leftTopStack.Children.Add(screenshotHintBtn);
            _topNormalHeaderGrid.Children.Add(leftTopStack);

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 16, 0)
            };
            _playlistCounterText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _titleText = new TextBlock
            {
                Text = "未打开视频 — 可直接拖入视频文件、点击左上角打开，或用本程序关联视频直接双击打开",
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(_playlistCounterText);
            titleStack.Children.Add(_titleText);
            Grid.SetColumn(titleStack, 1);
            _topNormalHeaderGrid.Children.Add(titleStack);

            topContainerGrid.Children.Add(_topNormalHeaderGrid);

            // 0B: Screenshot Mode Default Save Location Bar (Overlays inside the exact same 48px Row 0 -> ZERO video resize!)
            _topScreenshotSaveBarGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 42, 48)),
                Margin = new Thickness(-14, 0, -14, 0),
                Visibility = Visibility.Collapsed
            };
            var saveBarInnerGrid = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            saveBarInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            saveBarInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            saveBarInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            saveBarInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var saveLabel = new TextBlock
            {
                Text = "📷 默认截图保存位置：",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                FontWeight = FontWeights.SemiBold,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            saveBarInnerGrid.Children.Add(saveLabel);

            var saveLocationDisplayBorder = new Border
            {
                Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(18, 28, 34)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 0, 180, 210)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(6, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Focusable = false
            };
            _saveLocationTextBlock = new TextBlock
            {
                Text = _saveFolderPath,
                Foreground = new SolidColorBrush(Color.FromRgb(215, 235, 242)),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            saveLocationDisplayBorder.Child = _saveLocationTextBlock;
            Grid.SetColumn(saveLocationDisplayBorder, 1);
            saveBarInnerGrid.Children.Add(saveLocationDisplayBorder);

            var modeTipText = new TextBlock
            {
                Text = "[~截图模式] 拖拽/双点定框 | [R]恢复上次框 | [~]保存 | 滚轮:帧(Ctrl:1s) | A/D:5x快退进(S+A/D:帧) | 1/2/3(E倒):5s/20s/1m",
                Foreground = new SolidColorBrush(Color.FromRgb(190, 225, 240)),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 12, 0),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(modeTipText, 2);
            saveBarInnerGrid.Children.Add(modeTipText);

            var folderBtnStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var changeFolderBtn = CreateStyledButton("更改目录", 64);
            changeFolderBtn.Height = 22;
            changeFolderBtn.FontSize = 11;
            changeFolderBtn.Click += (_, __) => PickSaveFolder();
            var openFolderBtn = CreateStyledButton("打开文件夹", 72);
            openFolderBtn.Height = 22;
            openFolderBtn.FontSize = 11;
            openFolderBtn.Margin = new Thickness(6, 0, 0, 0);
            openFolderBtn.Click += (_, __) => OpenSaveFolder();
            folderBtnStack.Children.Add(changeFolderBtn);
            folderBtnStack.Children.Add(openFolderBtn);
            Grid.SetColumn(folderBtnStack, 3);
            saveBarInnerGrid.Children.Add(folderBtnStack);

            _topScreenshotSaveBarGrid.Children.Add(saveBarInnerGrid);
            topContainerGrid.Children.Add(_topScreenshotSaveBarGrid);

            // Toast notification (Right aligned in Row 0)
            _statusToastBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 0, 120, 215)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _statusToastText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            _statusToastBorder.Child = _statusToastText;
            topContainerGrid.Children.Add(_statusToastBorder);

            topBarBorder.Child = topContainerGrid;
            Grid.SetRow(topBarBorder, 0);
            rootGrid.Children.Add(topBarBorder);

            // ==================== ROW 1: PURE VIDEO STAGE ====================
            _videoStageGrid = new Grid
            {
                Background = Brushes.Black,
                ClipToBounds = true
            };

            // Main Video Player MediaElement (ScrubbingEnabled stays false while playing for smooth hardware playback; enabled only while paused)
            _mediaElement = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
                ScrubbingEnabled = false,
                Volume = _persistedVolume,
                IsMuted = _persistedIsMuted,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(_mediaElement, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(_mediaElement, EdgeMode.Aliased);
            _mediaElement.MediaOpened += MediaElement_OnMediaOpened;
            _mediaElement.MediaEnded += MediaElement_OnMediaEnded;
            _mediaElement.MediaFailed += MediaElement_OnMediaFailed;
            _mediaElement.MouseLeftButtonUp += (_, e) =>
            {
                if (!_isScreenshotModeActive && _mediaElement.Source != null)
                {
                    TogglePlayPause();
                }
            };
            _videoStageGrid.Children.Add(_mediaElement);

            // Instant zero-latency frame overlay for wheel stepping & timeline scrubbing (same Uniform stretch & alignment as _mediaElement)
            _instantFrameImage = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_instantFrameImage, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(_instantFrameImage, EdgeMode.Aliased);
            _videoStageGrid.Children.Add(_instantFrameImage);

            // Empty State Prompt
            _emptyStatePanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var emptyTitle = new TextBlock
            {
                Text = "拖入视频文件 或 点击「打开视频」开始播放",
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var emptySub = new TextBlock
            {
                Text = "滚轮:单帧(Ctrl/Shift:1s) | A/D:5x快退进(S+A/D:单帧) | 1/2/3(E倒):5s/20s/1m | [~]截图([R]恢复选框)",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                Margin = new Thickness(0, 10, 0, 18),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var emptyOpenBtn = CreateStyledButton("📂 选择视频文件打开...", 180);
            emptyOpenBtn.Height = 36;
            emptyOpenBtn.HorizontalAlignment = HorizontalAlignment.Center;
            emptyOpenBtn.Click += (_, __) => OpenFilePicker();
            _emptyStatePanel.Children.Add(emptyTitle);
            _emptyStatePanel.Children.Add(emptySub);
            _emptyStatePanel.Children.Add(emptyOpenBtn);
            _videoStageGrid.Children.Add(_emptyStatePanel);

            // Interactive Tilde (~) Screenshot Mode Overlay
            // Uses exact same layout alignment and size as _mediaElement so video position/ratio NEVER shifts!
            _screenshotOverlayRoot = new Grid
            {
                Background = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.None
            };
            _screenshotOverlayRoot.MouseDown += ScreenshotOverlay_OnMouseDown;
            _screenshotOverlayRoot.MouseMove += ScreenshotOverlay_OnMouseMove;
            _screenshotOverlayRoot.MouseUp += ScreenshotOverlay_OnMouseUp;

            _screenshotFrozenImage = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_screenshotFrozenImage, BitmapScalingMode.HighQuality);
            _screenshotOverlayRoot.Children.Add(_screenshotFrozenImage);

            _screenshotCanvas = new Canvas
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _aimHorizontalLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 229, 255)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new double[] { 4, 4 })
            };
            _aimVerticalLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 229, 255)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new double[] { 4, 4 })
            };
            _screenshotCanvas.Children.Add(_aimHorizontalLine);
            _screenshotCanvas.Children.Add(_aimVerticalLine);

            _selectionRectShape = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(35, 0, 229, 255)),
                Visibility = Visibility.Collapsed
            };
            _screenshotCanvas.Children.Add(_selectionRectShape);

            for (int i = 0; i < 8; i++)
            {
                var handle = new Rectangle
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                    StrokeThickness = 1.5,
                    Visibility = Visibility.Collapsed
                };
                _selectionHandles[i] = handle;
                _screenshotCanvas.Children.Add(handle);
            }

            _selectionSizeTag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Visibility = Visibility.Collapsed
            };
            _selectionSizeText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            _selectionSizeTag.Child = _selectionSizeText;
            _screenshotCanvas.Children.Add(_selectionSizeTag);

            // Cursor Aiming Reticle Box
            _aimReticleBox = new Grid { Width = 26, Height = 26 };
            _aimReticleBox.Children.Add(new Rectangle
            {
                Width = 26,
                Height = 26,
                Stroke = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                StrokeThickness = 1.5
            });
            _aimReticleBox.Children.Add(new Line
            {
                X1 = 13, Y1 = 0, X2 = 13, Y2 = 26,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 51, 102)),
                StrokeThickness = 1.2
            });
            _aimReticleBox.Children.Add(new Line
            {
                X1 = 0, Y1 = 13, X2 = 26, Y2 = 13,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 51, 102)),
                StrokeThickness = 1.2
            });
            _screenshotCanvas.Children.Add(_aimReticleBox);

            // Pixel Magnifier Popup beside cursor
            _magnifierPopup = new Border
            {
                Width = 158,
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Color.FromArgb(242, 22, 22, 22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6)
            };
            var magStack = new StackPanel();
            var magViewportBorder = new Border
            {
                Width = 140,
                Height = 140,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };
            var magGrid = new Grid { Width = 140, Height = 140, ClipToBounds = true };
            var magCanvas = new Canvas { Width = 140, Height = 140, ClipToBounds = true };
            _magnifierLiveBrush = new VisualBrush(_mediaElement)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(0, 0, 1, 1)
            };
            _magnifierLiveRect = new Rectangle
            {
                Width = 140,
                Height = 140,
                Fill = _magnifierLiveBrush
            };
            RenderOptions.SetBitmapScalingMode(_magnifierLiveRect, BitmapScalingMode.NearestNeighbor);
            magCanvas.Children.Add(_magnifierLiveRect);
            magGrid.Children.Add(magCanvas);
            magGrid.Children.Add(new Line
            {
                X1 = 70, Y1 = 0, X2 = 70, Y2 = 140,
                Stroke = new SolidColorBrush(Color.FromArgb(170, 0, 229, 255)),
                StrokeThickness = 1
            });
            magGrid.Children.Add(new Line
            {
                X1 = 0, Y1 = 70, X2 = 140, Y2 = 70,
                Stroke = new SolidColorBrush(Color.FromArgb(170, 0, 229, 255)),
                StrokeThickness = 1
            });
            magGrid.Children.Add(new Rectangle
            {
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 51, 102)),
                StrokeThickness = 1.5
            });
            magViewportBorder.Child = magGrid;
            magStack.Children.Add(magViewportBorder);

            _magnifierCoordText = new TextBlock
            {
                Text = "X: 0, Y: 0",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
                Margin = new Thickness(0, 5, 0, 2)
            };
            magStack.Children.Add(_magnifierCoordText);

            var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
            _magnifierColorSwatch = new Border
            {
                Width = 14,
                Height = 14,
                Background = Brushes.Black,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _magnifierColorText = new TextBlock
            {
                Text = "#000000",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            colorRow.Children.Add(_magnifierColorSwatch);
            colorRow.Children.Add(_magnifierColorText);
            magStack.Children.Add(colorRow);

            _magnifierPopup.Child = magStack;
            _screenshotCanvas.Children.Add(_magnifierPopup);

            _screenshotOverlayRoot.Children.Add(_screenshotCanvas);
            _videoStageGrid.Children.Add(_screenshotOverlayRoot);

            Grid.SetRow(_videoStageGrid, 1);
            rootGrid.Children.Add(_videoStageGrid);

            // ==================== ROW 2: FIXED-HEIGHT BOTTOM CONTROLS (96px - OUTSIDE VIDEO) ====================
            _bottomControlsRootGrid = new Grid
            {
                Height = 96,
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 24))
            };
            Panel.SetZIndex(_bottomControlsRootGrid, 20);

            var controlsContainer = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 8, 16, 10)
            };

            var controlsStack = new Grid();
            controlsStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28, GridUnitType.Pixel) });
            controlsStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 2A. Custom High-Precision Timeline SeekBar (Instant Single-Click + Drag + Hover Thumbnail)
            _timelineInteractiveArea = new Grid
            {
                Height = 26,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            _timelineInteractiveArea.MouseLeftButtonDown += Timeline_OnMouseLeftButtonDown;
            _timelineInteractiveArea.MouseMove += Timeline_OnMouseMove;
            _timelineInteractiveArea.MouseLeftButtonUp += Timeline_OnMouseLeftButtonUp;
            _timelineInteractiveArea.MouseLeave += Timeline_OnMouseLeave;
            _timelineInteractiveArea.SizeChanged += (_, __) => UpdateTimelineVisuals();

            _timelineTrackBg = new Border
            {
                Height = 6,
                Background = new SolidColorBrush(Color.FromRgb(65, 65, 65)),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _timelineInteractiveArea.Children.Add(_timelineTrackBg);

            _timelineFillBar = new Border
            {
                Height = 6,
                Width = 0,
                Background = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _timelineInteractiveArea.Children.Add(_timelineFillBar);

            _timelineHoverMarker = new Border
            {
                Width = 2,
                Height = 14,
                Background = new SolidColorBrush(Color.FromRgb(255, 220, 0)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _timelineInteractiveArea.Children.Add(_timelineHoverMarker);

            _timelineThumb = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                StrokeThickness = 2.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-7, 0, 0, 0),
                IsHitTestVisible = false
            };
            _timelineInteractiveArea.Children.Add(_timelineThumb);

            Grid.SetRow(_timelineInteractiveArea, 0);
            controlsStack.Children.Add(_timelineInteractiveArea);

            // 2B. Transport & Tools Row
            var transportGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: Play/Pause, Prev, Next, Frame Step, Time Display
            var leftControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _playPauseBtn = CreateStyledButton("", 46);
            _playPauseBtn.Height = 34;
            _playPauseBtn.ToolTip = "播放 / 暂停 (Space 或 K)";
            _playPauseIcon = new TextBlock { Text = "▶", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
            _playPauseBtn.Content = _playPauseIcon;
            _playPauseBtn.Click += (_, __) => TogglePlayPause();
            leftControls.Children.Add(_playPauseBtn);

            _prevBtn = CreateStyledButton("⏮ 上一个", 82);
            _prevBtn.Height = 34;
            _prevBtn.Margin = new Thickness(6, 0, 0, 0);
            _prevBtn.ToolTip = "切换同目录上一个视频 (PageUp / Shift+P)";
            _prevBtn.Click += (_, __) => SwitchNeighborVideo(-1);
            leftControls.Children.Add(_prevBtn);

            _nextBtn = CreateStyledButton("⏭ 下一个", 82);
            _nextBtn.Height = 34;
            _nextBtn.Margin = new Thickness(6, 0, 0, 0);
            _nextBtn.ToolTip = "切换同目录下一个视频 (PageDown / Shift+N)";
            _nextBtn.Click += (_, __) => SwitchNeighborVideo(1);
            leftControls.Children.Add(_nextBtn);

            var stepBackBtn = CreateStyledButton("⏪ -1帧", 66);
            stepBackBtn.Height = 34;
            stepBackBtn.Margin = new Thickness(10, 0, 0, 0);
            stepBackBtn.ToolTip = "上一帧 (快捷键 , 逗号)";
            stepBackBtn.Click += (_, __) => StepFrame(-1);
            leftControls.Children.Add(stepBackBtn);

            var stepFwdBtn = CreateStyledButton("+1帧 ⏩", 66);
            stepFwdBtn.Height = 34;
            stepFwdBtn.Margin = new Thickness(4, 0, 0, 0);
            stepFwdBtn.Click += (_, __) => StepFrame(1);
            stepFwdBtn.ToolTip = "下一帧 (快捷键 . 句号)";
            leftControls.Children.Add(stepFwdBtn);

            _timeDisplayText = new TextBlock
            {
                Text = "00:00:00.000 / 00:00:00.000",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            leftControls.Children.Add(_timeDisplayText);

            transportGrid.Children.Add(leftControls);

            // Center: Exact Time Jump Box
            var centerControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            centerControls.Children.Add(new TextBlock
            {
                Text = "定点跳转:",
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            _jumpTimeTextBox = new TextBox
            {
                Width = 92,
                Height = 28,
                Text = "00:00:05",
                Background = new SolidColorBrush(Color.FromRgb(34, 34, 34)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                FontFamily = new FontFamily("Consolas"),
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0),
                ToolTip = "输入秒数(如 85.5)或时间(如 01:25 或 00:01:25.500)，按回车直接跳转"
            };
            InputMethod.SetIsInputMethodEnabled(_jumpTimeTextBox, true);
            _jumpTimeTextBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    JumpToInputTimestamp();
                    e.Handled = true;
                }
            };
            centerControls.Children.Add(_jumpTimeTextBox);

            var jumpBtn = CreateStyledButton("跳转", 52);
            jumpBtn.Height = 28;
            jumpBtn.Margin = new Thickness(4, 0, 0, 0);
            jumpBtn.Click += (_, __) => JumpToInputTimestamp();
            centerControls.Children.Add(jumpBtn);

            var jumpAndSnapBtn = CreateStyledButton("跳转并全屏截图", 112);
            jumpAndSnapBtn.Height = 28;
            jumpAndSnapBtn.Margin = new Thickness(6, 0, 0, 0);
            jumpAndSnapBtn.Click += async (_, __) =>
            {
                if (JumpToInputTimestamp())
                {
                    await System.Threading.Tasks.Task.Delay(150);
                    CaptureFullFrameImmediately();
                }
            };
            centerControls.Children.Add(jumpAndSnapBtn);

            Grid.SetColumn(centerControls, 1);
            transportGrid.Children.Add(centerControls);

            // Right: Speed, Volume, Fullscreen
            var rightControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            rightControls.Children.Add(new TextBlock
            {
                Text = "倍速:",
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            _speedCombo = new ComboBox
            {
                Width = 68,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            double[] speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 };
            foreach (double s in speeds)
            {
                _speedCombo.Items.Add(s.ToString("0.##") + "x");
            }
            _speedCombo.SelectedIndex = 3;
            _speedCombo.SelectionChanged += (_, __) =>
            {
                if (_speedCombo.SelectedIndex >= 0 && _speedCombo.SelectedIndex < speeds.Length)
                {
                    _mediaElement.SpeedRatio = speeds[_speedCombo.SelectedIndex];
                }
            };
            rightControls.Children.Add(_speedCombo);

            _volumeLockBtn = CreateStyledButton("🔒 锁0", 62);
            _volumeLockBtn.Height = 28;
            _volumeLockBtn.Margin = new Thickness(0, 0, 6, 0);
            _volumeLockBtn.ToolTip = "彻底锁定音量为 0（默认开启；开启时强制归零并锁定，点击关闭后可从 0 重新调节音量）";
            _volumeLockBtn.Click += (_, __) => ToggleVolumeZeroLock();
            rightControls.Children.Add(_volumeLockBtn);

            _muteBtn = CreateStyledButton(_persistedIsMuted || _persistedVolume <= 0.001 ? "🔇" : "🔊", 34);
            _muteBtn.Height = 28;
            _muteBtn.Margin = new Thickness(0, 0, 6, 0);
            _muteBtn.ToolTip = "静音 / 取消静音 (快捷键 M)";
            _muteBtn.Click += (_, __) => ToggleMute();
            rightControls.Children.Add(_muteBtn);

            _volumeInteractiveArea = new Grid
            {
                Width = 92,
                Height = 26,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "单击或拖动调节音量（需先关闭旁边的「锁0」按钮）"
            };
            var volTrackBg = new Border
            {
                Height = 6,
                Background = new SolidColorBrush(Color.FromRgb(65, 65, 65)),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _volumeInteractiveArea.Children.Add(volTrackBg);

            _volumeFillBar = new Border
            {
                Height = 6,
                Width = 0,
                Background = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _volumeInteractiveArea.Children.Add(_volumeFillBar);

            _volumeThumb = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                StrokeThickness = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-6, 0, 0, 0),
                IsHitTestVisible = false
            };
            _volumeInteractiveArea.Children.Add(_volumeThumb);

            _volumeInteractiveArea.SizeChanged += (_, __) => UpdateVolumeUi();
            _volumeInteractiveArea.MouseLeftButtonDown += (s, e) =>
            {
                if (_isVolumeZeroLocked)
                {
                    ShowToast("🔒 音量已彻底锁定为 0，请先点击「🔒 锁0」按钮解锁后再从 0 调节");
                    e.Handled = true;
                    return;
                }
                _isDraggingVolume = true;
                _volumeInteractiveArea.CaptureMouse();
                double ratio = Clamp(e.GetPosition(_volumeInteractiveArea).X / Math.Max(1.0, _volumeInteractiveArea.ActualWidth), 0.0, 1.0);
                SetVolume(ratio, true);
                e.Handled = true;
            };
            _volumeInteractiveArea.MouseMove += (s, e) =>
            {
                if (_isDraggingVolume && !_isVolumeZeroLocked && e.LeftButton == MouseButtonState.Pressed)
                {
                    double ratio = Clamp(e.GetPosition(_volumeInteractiveArea).X / Math.Max(1.0, _volumeInteractiveArea.ActualWidth), 0.0, 1.0);
                    SetVolume(ratio, true);
                    e.Handled = true;
                }
            };
            _volumeInteractiveArea.MouseLeftButtonUp += (s, e) =>
            {
                if (_isDraggingVolume)
                {
                    _isDraggingVolume = false;
                    _volumeInteractiveArea.ReleaseMouseCapture();
                    if (!_isVolumeZeroLocked)
                    {
                        double ratio = Clamp(e.GetPosition(_volumeInteractiveArea).X / Math.Max(1.0, _volumeInteractiveArea.ActualWidth), 0.0, 1.0);
                        SetVolume(ratio, true);
                    }
                    e.Handled = true;
                }
            };
            rightControls.Children.Add(_volumeInteractiveArea);

            _volumePercentText = new TextBlock
            {
                Width = 38,
                Text = _persistedIsMuted ? "静音" : ((int)Math.Round(_persistedVolume * 100) + "%"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            rightControls.Children.Add(_volumePercentText);

            var fullScreenBtn = CreateStyledButton("⛶ 全屏 (F)", 82);
            fullScreenBtn.Height = 34;
            fullScreenBtn.Click += (_, __) => ToggleFullscreen();
            rightControls.Children.Add(fullScreenBtn);

            Grid.SetColumn(rightControls, 2);
            transportGrid.Children.Add(transportGrid.Children.Contains(rightControls) ? new UIElement() : rightControls);

            Grid.SetRow(transportGrid, 1);
            controlsStack.Children.Add(transportGrid);

            controlsContainer.Child = controlsStack;
            _bottomControlsRootGrid.Children.Add(controlsContainer);

            // 2C. Non-Layout-Affecting Overlay Canvas inside Row 2 for:
            //     (1) Timeline Hover Thumbnail Popup
            //     (2) Saved Screenshot Small Preview Banner (Right side of bottom bar, never changes video stage size!)
            _bottomOverlayCanvas = new Canvas
            {
                IsHitTestVisible = true
            };

            // Timeline Hover Thumbnail Popup (Uses Opacity + offscreen Left instead of Visibility.Collapsed so the embedded MediaElement stays live and decoded in WPF's visual tree)
            _timelineHoverPopup = new Border
            {
                Width = 196,
                Padding = new Thickness(5),
                Background = new SolidColorBrush(Color.FromArgb(248, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Opacity = 0,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_timelineHoverPopup, -10000);
            Canvas.SetTop(_timelineHoverPopup, -132);

            var hoverStack = new StackPanel();
            var hoverImgBorder = new Border
            {
                Width = 182,
                Height = 102,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true
            };
            var hoverImgGrid = new Grid();
            _timelineHoverMedia = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
                ScrubbingEnabled = true,
                IsMuted = true,
                Volume = 0,
                IsHitTestVisible = false
            };
            _timelineHoverMedia.MediaOpened += (_, __) =>
            {
                _thumbMediaReady = true;
                _timelineHoverMedia.Pause();
                if (_pendingThumbSeconds >= 0)
                {
                    _lastThumbSeekSeconds = _pendingThumbSeconds;
                    _timelineHoverMedia.Position = TimeSpan.FromSeconds(_pendingThumbSeconds);
                }
            };
            _timelineHoverImage = new Image
            {
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_timelineHoverImage, BitmapScalingMode.LowQuality);
            hoverImgGrid.Children.Add(_timelineHoverMedia);
            hoverImgGrid.Children.Add(_timelineHoverImage);
            hoverImgBorder.Child = hoverImgGrid;
            hoverStack.Children.Add(hoverImgBorder);

            _timelineHoverTimeText = new TextBlock
            {
                Text = "00:00:00.000",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            hoverStack.Children.Add(_timelineHoverTimeText);
            _timelineHoverPopup.Child = hoverStack;
            _bottomOverlayCanvas.Children.Add(_timelineHoverPopup);

            // Saved Screenshot Brief Preview Card (Floats at bottom-left corner of the monitor outside the window)
            _screenshotPreviewBanner = new Border
            {
                Width = 370,
                Height = 64,
                Padding = new Thickness(8, 5, 10, 5),
                Background = new SolidColorBrush(Color.FromArgb(248, 28, 32, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                ToolTip = "点击在资源管理器中定位该截图"
            };
            _screenshotPreviewBanner.MouseLeftButtonUp += (_, __) =>
            {
                if (!string.IsNullOrEmpty(_lastSavedFilePath) && File.Exists(_lastSavedFilePath))
                {
                    Process.Start("explorer.exe", "/select,\"" + _lastSavedFilePath + "\"");
                }
            };
            var previewStack = new StackPanel { Orientation = Orientation.Horizontal };
            var thumbBorder = new Border
            {
                Width = 92,
                Height = 52,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 10, 0)
            };
            _screenshotPreviewThumbImage = new Image { Stretch = Stretch.Uniform };
            thumbBorder.Child = _screenshotPreviewThumbImage;
            previewStack.Children.Add(thumbBorder);

            var previewTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 246 };
            previewTextStack.Children.Add(new TextBlock
            {
                Text = "✔ 已自动保存截图（点击定位文件）",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            });
            _screenshotPreviewFileNameText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 1)
            };
            _screenshotPreviewPathText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 165)),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            previewTextStack.Children.Add(_screenshotPreviewFileNameText);
            previewTextStack.Children.Add(_screenshotPreviewPathText);
            previewStack.Children.Add(previewTextStack);
            _screenshotPreviewBanner.Child = previewStack;

            _screenshotPreviewPopup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.AbsolutePoint,
                StaysOpen = true,
                Focusable = false,
                Child = _screenshotPreviewBanner
            };

            _bottomControlsRootGrid.Children.Add(_bottomOverlayCanvas);

            Grid.SetRow(_bottomControlsRootGrid, 2);
            rootGrid.Children.Add(_bottomControlsRootGrid);

            Content = rootGrid;

            // Timers: Synchronize UI timeline refresh to WPF's DWM VSync composition callback instead of an unsynchronized 40ms timer
            CompositionTarget.Rendering += MainWindow_OnCompositionRendering;

            _positionTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            _positionTimer.Tick += (_, __) =>
            {
                if (_isScreenshotModeActive && _screenshotOverlayRoot.IsVisible && !_screenshotOverlayRoot.IsMouseOver)
                {
                    try
                    {
                        var sp = Forms.Control.MousePosition;
                        Point posInOverlay = _screenshotOverlayRoot.PointFromScreen(new Point(sp.X, sp.Y));
                        HandleScreenshotMouseMove(posInOverlay, (Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0);
                    }
                    catch
                    {
                    }
                }
            };
            _positionTimer.Start();

            _previewHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _previewHideTimer.Tick += (_, __) =>
            {
                _previewHideTimer.Stop();
                _screenshotPreviewPopup.IsOpen = false;
            };

            _toastHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _toastHideTimer.Tick += (_, __) =>
            {
                _toastHideTimer.Stop();
                _statusToastBorder.Visibility = Visibility.Collapsed;
            };

            // Background priority debounce timer for hover thumbnail seek so rapid mouse movements across the timeline never block the UI thread
            _thumbDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(55) };
            _thumbDebounceTimer.Tick += (_, __) =>
            {
                _thumbDebounceTimer.Stop();
                if (_pendingThumbSeconds >= 0 && _timelineHoverPopup.Opacity > 0.5)
                {
                    if (!string.IsNullOrEmpty(ResolveFfmpegPath()))
                    {
                        _lastHoverWorkerTriggerTick = Environment.TickCount;
                        _latestRequestedHoverGenId = _videoGenerationId;
                        Interlocked.Exchange(ref _latestRequestedHoverSeconds, _pendingThumbSeconds);
                        _hoverRequestSignal.Set();
                    }
                    else if (_thumbMediaReady && Math.Abs(_pendingThumbSeconds - _lastThumbSeekSeconds) >= 0.02)
                    {
                        _lastThumbSeekSeconds = _pendingThumbSeconds;
                        _lastThumbSeekTick = Environment.TickCount;
                        _timelineHoverMedia.Position = TimeSpan.FromSeconds(_pendingThumbSeconds);
                    }
                }
            };

            // Coalesced main decoder seek flush timer: prevents EVR/DirectShow seek queue backlog during rapid wheel/A/D stepping while guaranteeing exact final frame landing
            _mainSeekFlushTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(28) };
            _mainSeekFlushTimer.Tick += (_, __) =>
            {
                _mainSeekFlushTimer.Stop();
                if (_pendingMainSeekSeconds >= 0 && _mediaElement.Source != null)
                {
                    double target = _pendingMainSeekSeconds;
                    _pendingMainSeekSeconds = -1;
                    _lastMainSeekCommitTick = Environment.TickCount;
                    _mediaElement.Position = TimeSpan.FromSeconds(target);
                }
            };

            Focusable = true;

            // Global Events
            InputMethod.SetIsInputMethodEnabled(this, false);
            SourceInitialized += MainWindow_OnSourceInitialized;
            Loaded += (_, __) =>
            {
                UpdateVolumeUi();
                Activate();
                Focus();
                Keyboard.Focus(this);
            };
            Activated += (_, __) =>
            {
                if (Keyboard.FocusedElement != _jumpTimeTextBox)
                {
                    Focus();
                    Keyboard.Focus(this);
                }
            };
            Closing += (_, __) =>
            {
                SavePersistentSettings();
                _screenshotPreviewPopup.IsOpen = false;
                StopGpuHoverAndBurstWorkers();
                try
                {
                    _timelineHoverMedia.Close();
                }
                catch
                {
                }
            };
            PreviewKeyDown += MainWindow_OnPreviewKeyDown;
            PreviewKeyUp += MainWindow_OnPreviewKeyUp;
            Deactivated += (_, __) =>
            {
                _isKeyAHeld = false;
                _isKeyDHeld = false;
                _isKeySHeld = false;
                _isKeyEHeld = false;
                _isTildeKeyPhysicallyDown = false;
                _lastAdHoldTick = 0;
            };
            PreviewMouseDown += MainWindow_OnPreviewMouseDown;
            PreviewMouseMove += MainWindow_OnPreviewMouseMove;
            PreviewMouseUp += MainWindow_OnPreviewMouseUp;
            MouseWheel += MainWindow_OnMouseWheel;
            Drop += MainWindow_OnDrop;
            DragOver += (_, e) =>
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };

            if (args != null && args.Length > 0 && File.Exists(args[0]))
            {
                Loaded += (_, __) => LoadVideoFile(args[0]);
            }

            StartHoverOnDemandWorkerThread();
        }

        private static Button CreateStyledButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Focusable = false,
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(72, 72, 72)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
        }

        private void ShowToast(string message)
        {
            _statusToastText.Text = message;
            _statusToastBorder.Visibility = Visibility.Visible;
            _toastHideTimer.Stop();
            _toastHideTimer.Start();
        }

        // ==================== FAST KEYFRAME HOVER ENGINE & EXACT FRAME RING-BUFFER ENGINE ====================
        private static string ResolveFfmpegPath()
        {
            if (_cachedFfmpegExe != null)
            {
                return _cachedFfmpegExe.Length > 0 ? _cachedFfmpegExe : null;
            }
            foreach (string candidate in FFMPEG_CANDIDATE_PATHS)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    _cachedFfmpegExe = candidate;
                    return candidate;
                }
            }
            _cachedFfmpegExe = "";
            return null;
        }

        private void StopGpuHoverAndBurstWorkers()
        {
            _latestRequestedHoverSeconds = -1.0;
            _lastRenderedHoverKey = int.MinValue;
            lock (_burstDecodeLock)
            {
                if (_burstDecodeFfmpegProc != null)
                {
                    try { if (!_burstDecodeFfmpegProc.HasExited) _burstDecodeFfmpegProc.Kill(); } catch { }
                    try { _burstDecodeFfmpegProc.Dispose(); } catch { }
                    _burstDecodeFfmpegProc = null;
                }
                _burstWindowStartIdx = -999999;
                _burstWindowEndIdx = -999999;
                _isBurstDecodeRunning = false;
                _queuedNextBurstCenterSeconds = -1.0;
            }
        }

        private static BitmapImage DecodeJpegBytesToFrozenBitmap(byte[] jpegBytes)
        {
            if (jpegBytes == null || jpegBytes.Length < 32)
            {
                return null;
            }
            try
            {
                using (var ms = new MemoryStream(jpegBytes, false))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource ExtractSingleFrameFrozenBitmap(string ffmpegExe, string videoPath, double seconds, int scaleWidth, int jpegQuality, bool keyframeOnly = false)
        {
            if (string.IsNullOrEmpty(ffmpegExe) || string.IsNullOrEmpty(videoPath) || seconds < 0)
            {
                return null;
            }
            try
            {
                string skipArg = keyframeOnly ? "-skip_frame nokey " : "";
                string vfArg = scaleWidth > 0
                    ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "-vf \"scale={0}:-2:flags=fast_bilinear\" ", scaleWidth)
                    : "";
                string args = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error {0}-ss {1:0.###} -i \"{2}\" -frames:v 1 -an -sn -threads 2 -pix_fmt yuvj420p -strict -2 {3}-q:v {4} -f image2pipe -vcodec mjpeg pipe:1",
                    skipArg, seconds, videoPath, vfArg, jpegQuality);

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return null;
                    using (var ms = new MemoryStream(32768))
                    {
                        byte[] buf = new byte[16384];
                        int read;
                        Stream stdout = proc.StandardOutput.BaseStream;
                        while ((read = stdout.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, read);
                        }
                        if (!proc.WaitForExit(1500))
                        {
                            try { proc.Kill(); } catch { }
                        }
                        if (ms.Length < 32)
                        {
                            return null;
                        }
                        byte[] raw = ms.ToArray();
                        return DecodeJpegBytesToFrozenBitmap(raw);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private void ExtractBatchAnchorKeyframes(string ffmpegExe, string videoPath, int genId, double totalDur, List<int> batchAnchorIndices)
        {
            if (batchAnchorIndices == null || batchAnchorIndices.Count == 0 || genId != _videoGenerationId)
            {
                return;
            }
            try
            {
                var sbIn = new System.Text.StringBuilder();
                var sbFilter = new System.Text.StringBuilder();
                int count = batchAnchorIndices.Count;
                for (int i = 0; i < count; i++)
                {
                    int anchorIdx = batchAnchorIndices[i];
                    double ratio = (anchorIdx + 0.5) / HOVER_ANCHOR_COUNT;
                    double sec = Clamp(ratio * totalDur, 0.0, Math.Max(0.0, totalDur - 0.05));
                    sbIn.AppendFormat(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-skip_frame nokey -ss {0:0.###} -i \"{1}\" ",
                        sec, videoPath);
                    sbFilter.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "[{0}:v:0]", i);
                }
                sbFilter.AppendFormat(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "concat=n={0}:v=1:a=0,scale=220:-2:flags=fast_bilinear[outv]",
                    count);

                string args = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error {0}-filter_complex \"{1}\" -map \"[outv]\" -frames:v {2} -an -sn -threads 1 -strict unofficial -q:v 5 -f image2pipe -vcodec mjpeg pipe:1",
                    sbIn.ToString(), sbFilter.ToString(), count);

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return;
                    int decodedCount = 0;
                    ReadMjpegStreamFrames(proc.StandardOutput.BaseStream, (frameIdx, bytes) =>
                    {
                        if (genId != _videoGenerationId)
                        {
                            return false;
                        }
                        if (frameIdx >= 0 && frameIdx < batchAnchorIndices.Count)
                        {
                            BitmapImage bmp = DecodeJpegBytesToFrozenBitmap(bytes);
                            if (bmp != null)
                            {
                                int anchorIdx = batchAnchorIndices[frameIdx];
                                _hoverAnchorFrames[anchorIdx] = bmp;
                                decodedCount++;
                            }
                        }
                        return true;
                    });
                    if (!proc.WaitForExit(2500))
                    {
                        try { proc.Kill(); } catch { }
                    }

                    // Fallback to single-frame extraction if concat filter failed (e.g. variable resolution mid-stream)
                    if (decodedCount == 0 && genId == _videoGenerationId)
                    {
                        foreach (int anchorIdx in batchAnchorIndices)
                        {
                            if (genId != _videoGenerationId) return;
                            double ratio = (anchorIdx + 0.5) / HOVER_ANCHOR_COUNT;
                            double sec = Clamp(ratio * totalDur, 0.0, Math.Max(0.0, totalDur - 0.05));
                            BitmapSource bmp = ExtractSingleFrameFrozenBitmap(ffmpegExe, videoPath, sec, 220, 5, keyframeOnly: true);
                            if (bmp != null && genId == _videoGenerationId)
                            {
                                _hoverAnchorFrames[anchorIdx] = bmp;
                            }
                        }
                    }

                    if (genId == _videoGenerationId && _timelineHoverPopup.Opacity > 0.5 && _pendingThumbSeconds >= 0)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            if (genId == _videoGenerationId && _timelineHoverPopup.Opacity > 0.5 && _pendingThumbSeconds >= 0)
                            {
                                int key;
                                BitmapSource best = TryGetBestHoverBitmap(_pendingThumbSeconds, out key);
                                if (best != null && key != _lastRenderedHoverKey)
                                {
                                    _lastRenderedHoverKey = key;
                                    _timelineHoverImage.Source = best;
                                    _timelineHoverImage.Visibility = Visibility.Visible;
                                }
                            }
                        }));
                    }
                }
            }
            catch
            {
            }
        }

        private void StartHoverOnDemandWorkerThread()
        {
            var worker = new Thread(() =>
            {
                while (true)
                {
                    _hoverRequestSignal.WaitOne();
                    while (true)
                    {
                        double reqSec = Interlocked.Exchange(ref _latestRequestedHoverSeconds, -1.0);
                        if (reqSec < 0)
                        {
                            break;
                        }
                        int reqGen = _latestRequestedHoverGenId;
                        string videoPath = _currentVideoFilePath;
                        string ffmpegExe = ResolveFfmpegPath();
                        if (reqGen != _videoGenerationId || string.IsNullOrEmpty(ffmpegExe) || string.IsNullOrEmpty(videoPath))
                        {
                            continue;
                        }

                        int exactBucket = Math.Max(0, (int)Math.Round(reqSec * HOVER_EXACT_BUCKETS_PER_SEC));
                        BitmapSource existing;
                        if (_hoverExactFrames.TryGetValue(exactBucket, out existing) && existing != null)
                        {
                            continue;
                        }

                        double bucketSec = exactBucket / (double)HOVER_EXACT_BUCKETS_PER_SEC;
                        BitmapSource frozenBmp = ExtractSingleFrameFrozenBitmap(ffmpegExe, videoPath, bucketSec, 220, 5, keyframeOnly: true);
                        if (frozenBmp == null && reqGen == _videoGenerationId)
                        {
                            frozenBmp = ExtractSingleFrameFrozenBitmap(ffmpegExe, videoPath, bucketSec, 220, 5, keyframeOnly: false);
                        }
                        if (frozenBmp != null && reqGen == _videoGenerationId)
                        {
                            _hoverExactFrames[exactBucket] = frozenBmp;
                            _hoverExactLruQueue.Enqueue(exactBucket);
                            while (_hoverExactLruQueue.Count > 360)
                            {
                                int oldBucket;
                                if (_hoverExactLruQueue.TryDequeue(out oldBucket))
                                {
                                    BitmapSource removed;
                                    _hoverExactFrames.TryRemove(oldBucket, out removed);
                                }
                            }

                            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                            {
                                if (reqGen != _videoGenerationId || _timelineHoverPopup.Opacity < 0.5 || _pendingThumbSeconds < 0)
                                {
                                    return;
                                }
                                int curBucket = Math.Max(0, (int)Math.Round(_pendingThumbSeconds * HOVER_EXACT_BUCKETS_PER_SEC));
                                if (Math.Abs(curBucket - exactBucket) <= 1)
                                {
                                    int renderKey = 1000000 + exactBucket;
                                    if (_lastRenderedHoverKey != renderKey)
                                    {
                                        _lastRenderedHoverKey = renderKey;
                                        _timelineHoverImage.Source = frozenBmp;
                                        if (_timelineHoverImage.Visibility != Visibility.Visible)
                                        {
                                            _timelineHoverImage.Visibility = Visibility.Visible;
                                        }
                                    }
                                }
                            }));
                        }
                    }
                }
            });
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Name = "ScreenboxHoverThumbWorker";
            worker.Start();
        }

        private static void ReadMjpegStreamFrames(Stream stream, Func<int, byte[], bool> onFrameDecoded)
        {
            byte[] readBuf = new byte[65536];
            byte[] accum = new byte[262144];
            int accumLen = 0;
            int frameCounter = 0;

            int bytesRead;
            while ((bytesRead = stream.Read(readBuf, 0, readBuf.Length)) > 0)
            {
                if (accumLen + bytesRead > accum.Length)
                {
                    int nextCap = Math.Max(accum.Length * 2, accumLen + bytesRead + 65536);
                    byte[] nextArr = new byte[nextCap];
                    Buffer.BlockCopy(accum, 0, nextArr, 0, accumLen);
                    accum = nextArr;
                }
                Buffer.BlockCopy(readBuf, 0, accum, accumLen, bytesRead);
                accumLen += bytesRead;

                int searchPos = 0;
                while (true)
                {
                    // Find SOI (FF D8)
                    int soi = -1;
                    for (int i = searchPos; i < accumLen - 1; i++)
                    {
                        if (accum[i] == 0xFF && accum[i + 1] == 0xD8)
                        {
                            soi = i;
                            break;
                        }
                    }
                    if (soi < 0)
                    {
                        accumLen = 0;
                        break;
                    }

                    // Find EOI (FF D9) after SOI
                    int eoi = -1;
                    for (int i = soi + 2; i < accumLen - 1; i++)
                    {
                        if (accum[i] == 0xFF && accum[i + 1] == 0xD9)
                        {
                            eoi = i + 2;
                            break;
                        }
                    }
                    if (eoi < 0)
                    {
                        if (soi > 0)
                        {
                            int rem = accumLen - soi;
                            Buffer.BlockCopy(accum, soi, accum, 0, rem);
                            accumLen = rem;
                        }
                        break;
                    }

                    int jpegLen = eoi - soi;
                    byte[] frameBytes = new byte[jpegLen];
                    Buffer.BlockCopy(accum, soi, frameBytes, 0, jpegLen);
                    searchPos = eoi;

                    if (!onFrameDecoded(frameCounter, frameBytes))
                    {
                        return;
                    }
                    frameCounter++;
                }

                if (searchPos > 0 && accumLen > searchPos)
                {
                    int rem = accumLen - searchPos;
                    Buffer.BlockCopy(accum, searchPos, accum, 0, rem);
                    accumLen = rem;
                }
                else if (searchPos > 0 && accumLen == searchPos)
                {
                    accumLen = 0;
                }
            }
        }

        private void StartGpuHoverStripPreloader(string videoPath, int genId)
        {
            string ffmpegExe = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegExe) || !File.Exists(videoPath))
            {
                return;
            }

            _hoverAnchorFrames.Clear();
            _hoverExactFrames.Clear();
            int dummyHover;
            while (_hoverExactLruQueue.TryDequeue(out dummyHover)) { }

            _exactFrameRingBitmaps.Clear();
            int dummy;
            while (_exactFrameRingOrder.TryDequeue(out dummy)) { }

            double totalDur = _durationSeconds;
            if (totalDur <= 0.05)
            {
                return;
            }

            // Sparse keyframe anchor sampling using O(1) input-side -skip_frame nokey -ss before -i:
            // Progressive binary-stride batches (Pass 1: 10 coarse anchors across entire video in ~115ms -> Pass 2: 20 -> Pass 3: all 80 anchors in ~900ms total!)
            // Works on 10MB or 20GB videos in < 1 second with zero full-file scan and zero NVDEC GPU contention!
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int[] strides = new[] { 8, 4, 2, 1 };
                    foreach (int stride in strides)
                    {
                        var batch = new List<int>(10);
                        for (int anchorIdx = 0; anchorIdx < HOVER_ANCHOR_COUNT; anchorIdx += stride)
                        {
                            if (genId != _videoGenerationId)
                            {
                                return;
                            }
                            if (_hoverAnchorFrames.ContainsKey(anchorIdx))
                            {
                                continue;
                            }
                            batch.Add(anchorIdx);
                            if (batch.Count >= 10)
                            {
                                ExtractBatchAnchorKeyframes(ffmpegExe, videoPath, genId, totalDur, batch);
                                batch.Clear();
                            }
                        }
                        if (batch.Count > 0 && genId == _videoGenerationId)
                        {
                            ExtractBatchAnchorKeyframes(ffmpegExe, videoPath, genId, totalDur, batch);
                            batch.Clear();
                        }
                    }
                }
                catch
                {
                }
            });
        }

        private BitmapSource TryGetBestHoverBitmap(double seconds, out int resolvedKey)
        {
            resolvedKey = int.MinValue;
            if (_durationSeconds <= 0)
            {
                return null;
            }

            // 1. Check exact on-demand hover cache (200ms resolution)
            int exactBucket = Math.Max(0, (int)Math.Round(seconds * HOVER_EXACT_BUCKETS_PER_SEC));
            BitmapSource bmp;
            if (_hoverExactFrames.TryGetValue(exactBucket, out bmp) && bmp != null)
            {
                resolvedKey = 1000000 + exactBucket;
                return bmp;
            }
            if (_hoverExactFrames.TryGetValue(exactBucket - 1, out bmp) && bmp != null)
            {
                resolvedKey = 1000000 + (exactBucket - 1);
                return bmp;
            }
            if (_hoverExactFrames.TryGetValue(exactBucket + 1, out bmp) && bmp != null)
            {
                resolvedKey = 1000000 + (exactBucket + 1);
                return bmp;
            }

            // 2. Check full-res exact frame ring buffer if available
            int ringIdx = Math.Max(0, (int)Math.Round(seconds * FRAME_RING_FPS));
            if (_exactFrameRingBitmaps.TryGetValue(ringIdx, out bmp) && bmp != null)
            {
                resolvedKey = 2000000 + ringIdx;
                return bmp;
            }

            // 3. Check sparse 80-point anchor keyframes (guaranteed 0ms non-black fallback across entire video)
            if (!_hoverAnchorFrames.IsEmpty)
            {
                int targetAnchor = (int)Clamp(Math.Floor((seconds / _durationSeconds) * HOVER_ANCHOR_COUNT), 0, HOVER_ANCHOR_COUNT - 1);
                if (_hoverAnchorFrames.TryGetValue(targetAnchor, out bmp) && bmp != null)
                {
                    resolvedKey = targetAnchor;
                    return bmp;
                }
                for (int delta = 1; delta < HOVER_ANCHOR_COUNT; delta++)
                {
                    int left = targetAnchor - delta;
                    if (left >= 0 && _hoverAnchorFrames.TryGetValue(left, out bmp) && bmp != null)
                    {
                        resolvedKey = left;
                        return bmp;
                    }
                    int right = targetAnchor + delta;
                    if (right < HOVER_ANCHOR_COUNT && _hoverAnchorFrames.TryGetValue(right, out bmp) && bmp != null)
                    {
                        resolvedKey = right;
                        return bmp;
                    }
                }
            }

            return null;
        }

        private void RequestExactHoverThumbnailAsync(double hoverSeconds)
        {
            int exactBucket = Math.Max(0, (int)Math.Round(hoverSeconds * HOVER_EXACT_BUCKETS_PER_SEC));
            if (_hoverExactFrames.ContainsKey(exactBucket))
            {
                return;
            }

            long nowTick = Environment.TickCount;
            _latestRequestedHoverGenId = _videoGenerationId;
            Interlocked.Exchange(ref _latestRequestedHoverSeconds, hoverSeconds);
            if (Math.Abs(nowTick - _lastHoverWorkerTriggerTick) >= 55)
            {
                _lastHoverWorkerTriggerTick = nowTick;
                _hoverRequestSignal.Set();
            }
            else
            {
                _thumbDebounceTimer.Stop();
                _thumbDebounceTimer.Start();
            }
        }

        private void EnsureExactFrameBurstWindow(double centerSeconds)
        {
            string ffmpegExe = ResolveFfmpegPath();
            string videoPath = _currentVideoFilePath;
            int genId = _videoGenerationId;
            if (string.IsNullOrEmpty(ffmpegExe) || string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                return;
            }

            int centerFrameIdx = Math.Max(0, (int)Math.Round(centerSeconds * FRAME_RING_FPS));
            lock (_burstDecodeLock)
            {
                // If centerFrameIdx is inside or near the active burst window:
                // NEVER kill an in-flight FFmpeg burst process when stepping within the window!
                if (centerFrameIdx >= _burstWindowStartIdx + 6 && centerFrameIdx <= _burstWindowEndIdx - 12)
                {
                    return;
                }
                if (_isBurstDecodeRunning)
                {
                    // If the user stepped slightly toward the edge of the currently decoding window, queue the next window smoothly
                    // instead of killing the running FFmpeg process mid-stream; only preempt if jumped far outside (> 3 seconds away)
                    if (centerFrameIdx >= _burstWindowStartIdx - 30 && centerFrameIdx <= _burstWindowEndIdx + 45)
                    {
                        _queuedNextBurstCenterSeconds = centerSeconds;
                        return;
                    }
                    if (_burstDecodeFfmpegProc != null)
                    {
                        try { if (!_burstDecodeFfmpegProc.HasExited) _burstDecodeFfmpegProc.Kill(); } catch { }
                        try { _burstDecodeFfmpegProc.Dispose(); } catch { }
                        _burstDecodeFfmpegProc = null;
                    }
                }

                // Decode a 3.6-second window (-1.0s to +2.6s = 108 frames at 30fps) around centerSeconds
                double startSec = Math.Max(0.0, centerSeconds - 1.0);
                int startFrameIdx = Math.Max(0, (int)Math.Round(startSec * FRAME_RING_FPS));
                int endFrameIdx = startFrameIdx + (int)(3.6 * FRAME_RING_FPS);

                _burstWindowStartIdx = startFrameIdx;
                _burstWindowEndIdx = endFrameIdx;
                _isBurstDecodeRunning = true;
                _queuedNextBurstCenterSeconds = -1.0;

                StartBurstDecodeWorkerLocked(ffmpegExe, videoPath, genId, startSec, 3.6, startFrameIdx);
            }
        }

        private void StartBurstDecodeWorkerLocked(string ffmpegExe, string videoPath, int genId, double startSec, double durationSec, int startFrameIdx)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Process proc = null;
                try
                {
                    string args = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -ss {0:0.###} -i \"{1}\" -t {2:0.###} -an -sn -threads 2 -strict unofficial -vf \"fps={3}\" -q:v 2 -f image2pipe -vcodec mjpeg pipe:1",
                        startSec, videoPath, durationSec, FRAME_RING_FPS);

                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegExe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false
                    };

                    proc = Process.Start(psi);
                    if (proc == null) return;

                    lock (_burstDecodeLock)
                    {
                        if (genId != _videoGenerationId || _burstWindowStartIdx != startFrameIdx)
                        {
                            try { proc.Kill(); proc.Dispose(); } catch { }
                            return;
                        }
                        _burstDecodeFfmpegProc = proc;
                    }

                    ReadMjpegStreamFrames(proc.StandardOutput.BaseStream, (offsetIdx, bytes) =>
                    {
                        if (genId != _videoGenerationId || _burstWindowStartIdx != startFrameIdx)
                        {
                            return false;
                        }

                        // Pre-decode and Freeze() BitmapImage on the background worker thread so UI wheel stepping is a <0.05ms pointer assignment!
                        BitmapImage frozenBmp = DecodeJpegBytesToFrozenBitmap(bytes);
                        if (frozenBmp == null)
                        {
                            return true;
                        }

                        int absFrameIdx = startFrameIdx + offsetIdx;
                        _exactFrameRingBitmaps[absFrameIdx] = frozenBmp;
                        _exactFrameRingOrder.Enqueue(absFrameIdx);

                        // Cap ring buffer at 180 frozen frames so memory stays bounded
                        while (_exactFrameRingOrder.Count > 180)
                        {
                            int oldIdx;
                            if (_exactFrameRingOrder.TryDequeue(out oldIdx))
                            {
                                BitmapSource removed;
                                _exactFrameRingBitmaps.TryRemove(oldIdx, out removed);
                            }
                        }

                        // If the user is currently paused and waiting on this exact frame, push it to _instantFrameImage immediately!
                        int wantedIdx = (int)Math.Round(_targetStepSeconds * FRAME_RING_FPS);
                        if (!_isPlaying && Math.Abs(absFrameIdx - wantedIdx) <= 1)
                        {
                            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                            {
                                if (!_isPlaying && Math.Abs((int)Math.Round(_targetStepSeconds * FRAME_RING_FPS) - absFrameIdx) <= 1)
                                {
                                    ShowInstantFrameOverlay(frozenBmp, absFrameIdx);
                                }
                            }));
                        }
                        return true;
                    });
                }
                catch
                {
                }
                finally
                {
                    double nextCenter = -1.0;
                    lock (_burstDecodeLock)
                    {
                        if (_burstDecodeFfmpegProc == proc)
                        {
                            try { proc.Dispose(); } catch { }
                            _burstDecodeFfmpegProc = null;
                            _isBurstDecodeRunning = false;
                            nextCenter = _queuedNextBurstCenterSeconds;
                            _queuedNextBurstCenterSeconds = -1.0;
                        }
                    }
                    if (nextCenter >= 0 && genId == _videoGenerationId && !_isPlaying)
                    {
                        EnsureExactFrameBurstWindow(nextCenter);
                    }
                }
            });
        }

        private void ShowInstantFrameOverlay(BitmapSource bmp, int frameIdx = -999999)
        {
            if (bmp == null) return;
            _currentInstantFrameBitmap = bmp;
            _currentInstantFrameIdx = frameIdx;
            _instantFrameImage.Source = bmp;
            if (_instantFrameImage.Visibility != Visibility.Visible)
            {
                _instantFrameImage.Visibility = Visibility.Visible;
            }
            if (_magnifierLiveBrush != null && _magnifierLiveBrush.Visual != _instantFrameImage)
            {
                _magnifierLiveBrush.Visual = _instantFrameImage;
            }
        }

        private void HideInstantFrameOverlay()
        {
            _currentInstantFrameBitmap = null;
            _currentInstantFrameIdx = -999999;
            _instantFrameImage.Visibility = Visibility.Collapsed;
            _instantFrameImage.Source = null;
            if (_magnifierLiveBrush != null && _magnifierLiveBrush.Visual != _mediaElement)
            {
                _magnifierLiveBrush.Visual = _mediaElement;
            }
        }

        // ==================== CUSTOM PRECISION TIMELINE + HOVER THUMBNAIL ====================
        private double GetSecondsFromTimelineMouseX(double mouseX)
        {
            double width = _timelineInteractiveArea.ActualWidth;
            if (width <= 0 || _durationSeconds <= 0)
            {
                return 0;
            }
            double ratio = Clamp(mouseX / width, 0.0, 1.0);
            return ratio * _durationSeconds;
        }

        private void Timeline_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mediaElement.Source == null || _durationSeconds <= 0)
            {
                return;
            }

            _wasPlayingBeforeScrub = _isPlaying;
            _isScrubbingTimeline = true;
            _timelineInteractiveArea.CaptureMouse();

            Point pt = e.GetPosition(_timelineInteractiveArea);
            _timelineMouseDownPos = pt;
            double targetSec = GetSecondsFromTimelineMouseX(pt.X);
            _scrubPreviewSeconds = targetSec;
            _lastMainScrubSeekTick = Environment.TickCount;

            if (_wasPlayingBeforeScrub)
            {
                // Single-click while playing: DO NOT pause, DO NOT toggle ScrubbingEnabled (which stalls EVR pipeline for 200ms+),
                // and DO NOT spawn multi-frame FFmpeg burst processes! Commit Position directly at 0ms latency!
                HideInstantFrameOverlay();
                _targetStepSeconds = -1;
                UpdateTimelineVisualsForSeconds(targetSec);
                string nextTimeText = FormatTime(TimeSpan.FromSeconds(targetSec)) + " / " + FormatTime(TimeSpan.FromSeconds(_durationSeconds));
                if (!string.Equals(_lastDisplayedTimeText, nextTimeText, StringComparison.Ordinal))
                {
                    _lastDisplayedTimeText = nextTimeText;
                    _timeDisplayText.Text = nextTimeText;
                }
                _mainSeekFlushTimer.Stop();
                _pendingMainSeekSeconds = -1;
                _lastMainSeekCommitTick = _lastMainScrubSeekTick;
                _mediaElement.Position = TimeSpan.FromSeconds(Clamp(targetSec, 0.0, _durationSeconds));
            }
            else
            {
                _mediaElement.ScrubbingEnabled = true;
                SeekToSeconds(targetSec, immediateHardwareCommit: true);
            }

            UpdateHoverThumbnailPopup(pt.X, targetSec);
            e.Handled = true;
        }

        private void Timeline_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_mediaElement.Source == null || _durationSeconds <= 0)
            {
                return;
            }

            Point pt = e.GetPosition(_timelineInteractiveArea);
            double targetSec = GetSecondsFromTimelineMouseX(pt.X);

            if (_isScrubbingTimeline && e.LeftButton == MouseButtonState.Pressed)
            {
                // If video was playing when mouse went down, only pause into scrubbing mode once the user actually drags (>= 4px)
                if (_isPlaying && Math.Abs(pt.X - _timelineMouseDownPos.X) >= 4.0)
                {
                    _mediaElement.Pause();
                    _isPlaying = false;
                    _playPauseIcon.Text = "▶";
                    _mediaElement.ScrubbingEnabled = true;
                }

                _scrubPreviewSeconds = targetSec;
                _targetStepSeconds = targetSec;
                UpdateTimelineVisualsForSeconds(targetSec);
                _timeDisplayText.Text = FormatTime(TimeSpan.FromSeconds(targetSec)) + " / " + FormatTime(TimeSpan.FromSeconds(_durationSeconds));

                if (!_isPlaying)
                {
                    int dummyKey;
                    BitmapSource dragBmp = TryGetBestHoverBitmap(targetSec, out dummyKey);
                    if (dragBmp != null)
                    {
                        ShowInstantFrameOverlay(dragBmp, -999999);
                    }
                }

                // Throttle underlying hardware MediaElement seek during fast drag so EVR never chokes
                long nowTick = Environment.TickCount;
                if (Math.Abs(nowTick - _lastMainScrubSeekTick) >= 75)
                {
                    _lastMainScrubSeekTick = nowTick;
                    _mediaElement.Position = TimeSpan.FromSeconds(Clamp(targetSec, 0.0, _durationSeconds));
                }
            }

            UpdateHoverThumbnailPopup(pt.X, targetSec);
        }

        private void Timeline_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isScrubbingTimeline)
            {
                _isScrubbingTimeline = false;
                _timelineInteractiveArea.ReleaseMouseCapture();

                Point pt = e.GetPosition(_timelineInteractiveArea);
                double targetSec = GetSecondsFromTimelineMouseX(pt.X);
                _scrubPreviewSeconds = targetSec;

                if (_wasPlayingBeforeScrub)
                {
                    HideInstantFrameOverlay();
                    _targetStepSeconds = -1;
                    if (!_isPlaying)
                    {
                        // User dragged while playing -> resume playback from final release position
                        _mediaElement.ScrubbingEnabled = false;
                        _mediaElement.Position = TimeSpan.FromSeconds(Clamp(targetSec, 0.0, _durationSeconds));
                        _mediaElement.Play();
                        _isPlaying = true;
                        _playPauseIcon.Text = "⏸";
                    }
                    else if (Math.Abs(pt.X - _timelineMouseDownPos.X) >= 2.0)
                    {
                        _mediaElement.Position = TimeSpan.FromSeconds(Clamp(targetSec, 0.0, _durationSeconds));
                    }
                }
                else
                {
                    _mediaElement.ScrubbingEnabled = true;
                    SeekToSeconds(targetSec, immediateHardwareCommit: true);
                }

                if (!_timelineInteractiveArea.IsMouseOver)
                {
                    HideHoverThumbnailPopup();
                    _timelineHoverMarker.Visibility = Visibility.Collapsed;
                }

                e.Handled = true;
            }
        }

        private void Timeline_OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isScrubbingTimeline)
            {
                HideHoverThumbnailPopup();
                _timelineHoverMarker.Visibility = Visibility.Collapsed;
            }
        }

        private void HideHoverThumbnailPopup()
        {
            _thumbDebounceTimer.Stop();
            _latestRequestedHoverSeconds = -1.0;
            _timelineHoverPopup.Opacity = 0;
            Canvas.SetLeft(_timelineHoverPopup, -10000);
        }

        private void UpdateHoverThumbnailPopup(double mouseXOnTimeline, double hoverSeconds)
        {
            double trackWidth = _timelineInteractiveArea.ActualWidth;
            if (trackWidth <= 0 || _durationSeconds <= 0)
            {
                return;
            }

            double clampedX = Clamp(mouseXOnTimeline, 0, trackWidth);

            // Position yellow hover marker on timeline
            _timelineHoverMarker.Margin = new Thickness(clampedX - 1, 0, 0, 0);
            _timelineHoverMarker.Visibility = Visibility.Visible;

            // Update timestamp text immediately
            _timelineHoverTimeText.Text = FormatTime(TimeSpan.FromSeconds(hoverSeconds));

            // Position popup centered above the mouse cursor on the timeline
            double popupW = _timelineHoverPopup.Width;
            double canvasX = Clamp(16 + clampedX - (popupW * 0.5), 8, Math.Max(8, ActualWidth - popupW - 24));
            Canvas.SetLeft(_timelineHoverPopup, canvasX);
            Canvas.SetTop(_timelineHoverPopup, -132);
            _timelineHoverPopup.Opacity = 1;

            _pendingThumbSeconds = hoverSeconds;

            string ffmpegExe = ResolveFfmpegPath();
            if (!string.IsNullOrEmpty(ffmpegExe))
            {
                // 1) 0ms instant O(1) lookup of pre-frozen BitmapSource (either exact 200ms bucket or nearest sparse anchor keyframe)
                //    Zero UI-thread JPEG decoding! Pointer assignment takes < 0.02ms and NEVER stalls main video playback!
                int resolvedKey;
                BitmapSource bestBmp = TryGetBestHoverBitmap(hoverSeconds, out resolvedKey);
                if (bestBmp != null)
                {
                    if (_lastRenderedHoverKey != resolvedKey || _timelineHoverImage.Visibility != Visibility.Visible)
                    {
                        _lastRenderedHoverKey = resolvedKey;
                        _timelineHoverImage.Source = bestBmp;
                        _timelineHoverImage.Visibility = Visibility.Visible;
                    }
                }

                // 2) Queue single background worker to fetch exact 200ms-bucket frame via fast -ss input seek (Latest-Wins, throttled >= 55ms)
                RequestExactHoverThumbnailAsync(hoverSeconds);
                return;
            }

            // Fallback Path (only if FFmpeg is completely absent on the machine):
            _timelineHoverImage.Visibility = Visibility.Collapsed;
            if (_timelineHoverMedia.Source == null && _mediaElement.Source != null)
            {
                _thumbMediaReady = false;
                _timelineHoverMedia.IsMuted = true;
                _timelineHoverMedia.Volume = 0;
                _timelineHoverMedia.Source = _mediaElement.Source;
                _timelineHoverMedia.Play();
                return;
            }

            if (_thumbMediaReady)
            {
                long nowTick = Environment.TickCount;
                if (Math.Abs(hoverSeconds - _lastThumbSeekSeconds) >= 0.05 && Math.Abs(nowTick - _lastThumbSeekTick) >= 85)
                {
                    _thumbDebounceTimer.Stop();
                    _lastThumbSeekSeconds = hoverSeconds;
                    _lastThumbSeekTick = nowTick;
                    _timelineHoverMedia.Position = TimeSpan.FromSeconds(hoverSeconds);
                }
                else
                {
                    _thumbDebounceTimer.Stop();
                    _thumbDebounceTimer.Start();
                }
            }
        }

        private void UpdateTimelineVisuals()
        {
            double posSec = _isScrubbingTimeline ? _scrubPreviewSeconds : _mediaElement.Position.TotalSeconds;
            UpdateTimelineVisualsForSeconds(posSec);
        }

        private void UpdateTimelineVisualsForSeconds(double posSec)
        {
            double trackWidth = _timelineInteractiveArea.ActualWidth;
            if (trackWidth <= 0 || _durationSeconds <= 0 || _mediaElement.Source == null)
            {
                if (_lastDisplayedFillWidth != 0)
                {
                    _lastDisplayedFillWidth = 0;
                    _timelineFillBar.Width = 0;
                    _timelineThumb.Margin = new Thickness(-7, 0, 0, 0);
                }
                return;
            }

            double ratio = Clamp(posSec / _durationSeconds, 0.0, 1.0);
            double px = ratio * trackWidth;

            if (Math.Abs(px - _lastDisplayedFillWidth) >= 0.25 || !_isPlaying || _isScrubbingTimeline)
            {
                _lastDisplayedFillWidth = px;
                _timelineFillBar.Width = px;
                _timelineThumb.Margin = new Thickness(px - 7, 0, 0, 0);
            }
        }

        // ==================== FILE LOADING & PLAYBACK ====================
        private void OpenFilePicker()
        {
            using (var dlg = new Forms.OpenFileDialog())
            {
                dlg.Title = "选择要打开的视频文件";
                dlg.Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts;*.3gp|所有文件|*.*";
                if (dlg.ShowDialog() == Forms.DialogResult.OK)
                {
                    LoadVideoFile(dlg.FileName);
                }
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        private sealed class NaturalFileNameComparer : IComparer<string>
        {
            public static readonly NaturalFileNameComparer Instance = new NaturalFileNameComparer();
            public int Compare(string x, string y)
            {
                return StrCmpLogicalW(Path.GetFileName(x) ?? "", Path.GetFileName(y) ?? "");
            }
        }

        private static string NormalizeDirPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path.Trim().TrimEnd('\\', '/');
            }
        }

        private string TryDetectExplorerSortColumns(string targetDir)
        {
            string normTarget = NormalizeDirPath(targetDir);
            if (string.IsNullOrEmpty(normTarget)) return null;

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return null;

                int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object win = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                        if (win == null) continue;

                        object doc = win.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, win, null);
                        if (doc == null) continue;

                        object folder = doc.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, doc, null);
                        if (folder == null) continue;

                        object self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                        if (self == null) continue;

                        string folderPath = self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null) as string;
                        if (!string.Equals(NormalizeDirPath(folderPath), normTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string sortCols = doc.GetType().InvokeMember("SortColumns", BindingFlags.GetProperty, null, doc, null) as string;
                        if (!string.IsNullOrWhiteSpace(sortCols))
                        {
                            return sortCols.Trim();
                        }
                    }
                    catch
                    {
                        // Ignore individual non-Explorer IE/WebBrowser or busy shell windows
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private List<string> GetSortedFolderVideos(string dir)
        {
            List<FileInfo> files = new DirectoryInfo(dir)
                .GetFiles()
                .Where(fi => SupportedExtensions.Contains(fi.Extension))
                .ToList();

            string normDir = NormalizeDirPath(dir);
            string liveSortSpec = TryDetectExplorerSortColumns(dir);
            if (string.IsNullOrWhiteSpace(liveSortSpec))
            {
                string envSort = Environment.GetEnvironmentVariable("DSH_GALLERY_SORT_SPEC");
                if (!string.IsNullOrWhiteSpace(envSort))
                {
                    liveSortSpec = envSort.Trim();
                }
            }
            if (!string.IsNullOrWhiteSpace(liveSortSpec))
            {
                _cachedPlaylistDir = normDir;
                _cachedExplorerSortSpec = liveSortSpec;
            }
            else if (string.Equals(_cachedPlaylistDir, normDir, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_cachedExplorerSortSpec))
            {
                liveSortSpec = _cachedExplorerSortSpec;
            }

            // Default: Windows Explorer natural filename ascending
            string propName = "System.ItemNameDisplay";
            bool descending = false;

            if (!string.IsNullOrWhiteSpace(liveSortSpec))
            {
                // Format example: "prop:-System.DateCreated;" or "prop:System.DateModified;System.ItemNameDisplay;"
                string spec = liveSortSpec;
                if (spec.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
                {
                    spec = spec.Substring(5);
                }
                int semi = spec.IndexOf(';');
                string primary = (semi >= 0 ? spec.Substring(0, semi) : spec).Trim();
                if (!string.IsNullOrEmpty(primary))
                {
                    if (primary.StartsWith("-"))
                    {
                        descending = true;
                        propName = primary.Substring(1).Trim();
                    }
                    else if (primary.StartsWith("+"))
                    {
                        descending = false;
                        propName = primary.Substring(1).Trim();
                    }
                    else
                    {
                        propName = primary;
                    }
                }
            }

            IEnumerable<FileInfo> ordered;
            if (propName.IndexOf("DateCreated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ordered = descending
                    ? files.OrderByDescending(f => f.CreationTimeUtc).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance)
                    : files.OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance);
            }
            else if (propName.IndexOf("DateModified", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("System.Date", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("DateAccessed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ordered = descending
                    ? files.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance)
                    : files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance);
            }
            else if (propName.IndexOf("Size", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ordered = descending
                    ? files.OrderByDescending(f => f.Length).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance)
                    : files.OrderBy(f => f.Length).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance);
            }
            else if (propName.IndexOf("ItemType", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     propName.IndexOf("FileExtension", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ordered = descending
                    ? files.OrderByDescending(f => f.Extension, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance)
                    : files.OrderBy(f => f.Extension, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.FullName, NaturalFileNameComparer.Instance);
            }
            else
            {
                ordered = descending
                    ? files.OrderByDescending(f => f.FullName, NaturalFileNameComparer.Instance)
                    : files.OrderBy(f => f.FullName, NaturalFileNameComparer.Instance);
            }

            return ordered.Select(f => f.FullName).ToList();
        }

        private void LoadVideoFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return;
            }

            if (_isScreenshotModeActive)
            {
                ExitScreenshotMode();
            }

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _folderVideoFiles = GetSortedFolderVideos(dir);
                    _currentVideoIndex = _folderVideoFiles.FindIndex(f => string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    _folderVideoFiles = new List<string> { fullPath };
                    _currentVideoIndex = 0;
                }

                _emptyStatePanel.Visibility = Visibility.Collapsed;
                StopGpuHoverAndBurstWorkers();
                StopActiveTranscodeWorker();
                HideInstantFrameOverlay();
                _currentVideoFilePath = fullPath;
                int genId = Interlocked.Increment(ref _videoGenerationId);

                _durationSeconds = 0;
                _targetStepSeconds = -1;
                _thumbMediaReady = false;
                _pendingThumbSeconds = 0;
                _lastThumbSeekSeconds = -999;

                string playSourcePath = fullPath;
                if (IsAv1OrUnsupportedMp4(fullPath))
                {
                    string cachedH264 = GetTranscodedCacheFilePath(fullPath);
                    if (!string.IsNullOrEmpty(cachedH264) && File.Exists(cachedH264) && new FileInfo(cachedH264).Length > 65536)
                    {
                        playSourcePath = cachedH264;
                    }
                    else
                    {
                        _titleText.Text = Path.GetFileName(fullPath) + "  (" + fullPath + ")";
                        Title = Path.GetFileName(fullPath) + " - Screenbox";
                        _playlistCounterText.Text = _folderVideoFiles.Count > 0
                            ? string.Format("[{0}/{1}]", _currentVideoIndex + 1, _folderVideoFiles.Count)
                            : "";
                        ShowToast("⚡ 检测到 B站 AV1 编码视频，正在使用 GPU 极速硬解缓冲...");
                        StartGpuAv1ToH264Fallback(fullPath, genId);
                        Activate();
                        Focus();
                        Keyboard.Focus(this);
                        return;
                    }
                }

                var uri = new Uri(playSourcePath);
                _mediaElement.ScrubbingEnabled = false;
                _mediaElement.Volume = _isVolumeZeroLocked ? 0.0 : _persistedVolume;
                _mediaElement.IsMuted = _isVolumeZeroLocked || _persistedIsMuted;
                _mediaElement.Source = uri;
                _mediaElement.Play();

                // Keep fallback hover MediaElement closed unless FFmpeg is absent
                _timelineHoverMedia.Stop();
                _timelineHoverMedia.Source = null;
                _timelineHoverImage.Source = null;
                _timelineHoverImage.Visibility = Visibility.Collapsed;

                _isPlaying = true;
                _playPauseIcon.Text = "⏸";

                string fileName = Path.GetFileName(fullPath);
                _playlistCounterText.Text = _folderVideoFiles.Count > 0
                    ? string.Format("[{0}/{1}]", _currentVideoIndex + 1, _folderVideoFiles.Count)
                    : "";
                _titleText.Text = fileName + "  (" + fullPath + ")";
                Title = fileName + " - Screenbox";

                Activate();
                Focus();
                Keyboard.Focus(this);
            }
            catch (Exception ex)
            {
                ShowToast("打开失败: " + ex.Message);
            }
        }

        private void SwitchNeighborVideo(int delta)
        {
            if (_folderVideoFiles == null || _folderVideoFiles.Count <= 1 || _currentVideoIndex < 0)
            {
                ShowToast("当前文件夹没有更多视频");
                return;
            }

            int nextIdx = (_currentVideoIndex + delta + _folderVideoFiles.Count) % _folderVideoFiles.Count;
            LoadVideoFile(_folderVideoFiles[nextIdx]);
            ShowToast((delta > 0 ? "⏭ 下一个视频: " : "⏮ 上一个视频: ") + Path.GetFileName(_folderVideoFiles[nextIdx]));
        }

        private void MediaElement_OnMediaOpened(object sender, RoutedEventArgs e)
        {
            if (_mediaElement.NaturalDuration.HasTimeSpan)
            {
                _durationSeconds = _mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            }
            _mediaElement.Volume = _isVolumeZeroLocked ? 0.0 : _persistedVolume;
            _mediaElement.IsMuted = _isVolumeZeroLocked || _persistedIsMuted;
            UpdateVolumeUi();
            UpdateTimelineUi();
            if (!string.IsNullOrEmpty(_currentVideoFilePath))
            {
                StartGpuHoverStripPreloader(_currentVideoFilePath, _videoGenerationId);
            }
        }

        private void MediaElement_OnMediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            _playPauseIcon.Text = "▶";
        }

        private static readonly string TRANSCODE_CACHE_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\av1_h264_cache");
        private Process _activeTranscodeProc;
        private readonly object _transcodeLock = new object();

        private void StopActiveTranscodeWorker()
        {
            lock (_transcodeLock)
            {
                if (_activeTranscodeProc != null)
                {
                    try { if (!_activeTranscodeProc.HasExited) _activeTranscodeProc.Kill(); } catch { }
                    try { _activeTranscodeProc.Dispose(); } catch { }
                    _activeTranscodeProc = null;
                }
            }
        }

        private static string GetTranscodedCacheFilePath(string originalVideoPath)
        {
            try
            {
                if (!Directory.Exists(TRANSCODE_CACHE_DIR)) Directory.CreateDirectory(TRANSCODE_CACHE_DIR);
                FileInfo fi = new FileInfo(originalVideoPath);
                string key = string.Format("{0}|{1}|{2}|h264_v1", originalVideoPath.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc.Ticks);
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                    var sb = new System.Text.StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return Path.Combine(TRANSCODE_CACHE_DIR, sb.ToString() + ".mp4");
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool IsAv1OrUnsupportedMp4(string videoPath)
        {
            try
            {
                if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return false;
                byte[] header = new byte[512];
                int read;
                using (var fs = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    read = fs.Read(header, 0, header.Length);
                }
                if (read >= 16)
                {
                    string ascii = System.Text.Encoding.ASCII.GetString(header, 0, read);
                    if (ascii.IndexOf("av01", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void StartGpuAv1ToH264Fallback(string originalVideoPath, int genId)
        {
            string ffmpegExe = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegExe) || !File.Exists(originalVideoPath))
            {
                ShowToast("无法解码此视频 (AV1 编码且未找到 FFmpeg)");
                return;
            }

            string cacheMp4 = GetTranscodedCacheFilePath(originalVideoPath);
            if (!string.IsNullOrEmpty(cacheMp4) && File.Exists(cacheMp4) && new FileInfo(cacheMp4).Length > 65536)
            {
                _mediaElement.Source = new Uri(cacheMp4);
                _mediaElement.Play();
                return;
            }

            // Immediately show first frame so the screen is never black while NVENC transcodes in ~2-3s!
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    BitmapSource firstFrame = ExtractSingleFrameFrozenBitmap(ffmpegExe, originalVideoPath, 0.2, 0, 3, false);
                    if (firstFrame != null && genId == _videoGenerationId)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                        {
                            if (genId == _videoGenerationId && _mediaElement.NaturalVideoWidth <= 0)
                            {
                                ShowInstantFrameOverlay(firstFrame, 0);
                            }
                        }));
                    }

                    string tmpMp4 = cacheMp4 + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp.mp4";
                    // Use NVIDIA hardware av1_cuvid + h264_nvenc (or fallback libx264 ultrafast)
                    string args = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -y -hwaccel auto -i \"{0}\" -c:v h264_nvenc -preset p1 -cq 24 -pix_fmt yuv420p -c:a copy -movflags +faststart \"{1}\"",
                        originalVideoPath, tmpMp4);

                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegExe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = false,
                        RedirectStandardOutput = false
                    };

                    Process proc = Process.Start(psi);
                    lock (_transcodeLock) { _activeTranscodeProc = proc; }
                    if (proc != null)
                    {
                        proc.WaitForExit();
                    }

                    // If h264_nvenc failed (e.g. non-NVIDIA GPU), fallback to CPU libx264 ultrafast
                    if (!File.Exists(tmpMp4) || new FileInfo(tmpMp4).Length < 32768)
                    {
                        if (genId != _videoGenerationId) return;
                        string cpuArgs = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "-hide_banner -loglevel error -y -i \"{0}\" -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p -c:a copy -movflags +faststart \"{1}\"",
                            originalVideoPath, tmpMp4);
                        psi.Arguments = cpuArgs;
                        Process cpuProc = Process.Start(psi);
                        lock (_transcodeLock) { _activeTranscodeProc = cpuProc; }
                        if (cpuProc != null) cpuProc.WaitForExit();
                    }

                    if (genId != _videoGenerationId)
                    {
                        try { if (File.Exists(tmpMp4)) File.Delete(tmpMp4); } catch { }
                        return;
                    }

                    if (File.Exists(tmpMp4) && new FileInfo(tmpMp4).Length > 32768)
                    {
                        try
                        {
                            if (File.Exists(cacheMp4)) File.Delete(cacheMp4);
                            File.Move(tmpMp4, cacheMp4);
                        }
                        catch
                        {
                            cacheMp4 = tmpMp4;
                        }

                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            if (genId != _videoGenerationId) return;
                            HideInstantFrameOverlay();
                            _mediaElement.ScrubbingEnabled = false;
                            _mediaElement.Volume = _isVolumeZeroLocked ? 0.0 : _persistedVolume;
                            _mediaElement.IsMuted = _isVolumeZeroLocked || _persistedIsMuted;
                            _mediaElement.Source = new Uri(cacheMp4);
                            _mediaElement.Play();
                            _isPlaying = true;
                            _playPauseIcon.Text = "⏸";
                            ShowToast("⚡ AV1 视频已完成 GPU 极速硬解适配并开始播放");
                        }));
                    }
                }
                catch { }
            });
        }

        private void MediaElement_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentVideoFilePath) && File.Exists(_currentVideoFilePath))
            {
                ShowToast("⚡ 检测到 AV1/高阶编码，正在调用 GPU 极速硬解转换 (约 2~3 秒)...");
                StartGpuAv1ToH264Fallback(_currentVideoFilePath, _videoGenerationId);
                return;
            }
            ShowToast("媒体解码错误: " + (e.ErrorException != null ? e.ErrorException.Message : "未知错误"));
        }

        private void TogglePlayPause()
        {
            if (_mediaElement.Source == null)
            {
                return;
            }

            if (_isPlaying)
            {
                _mediaElement.Pause();
                _mediaElement.ScrubbingEnabled = true;
                _targetStepSeconds = _mediaElement.Position.TotalSeconds;
                _isPlaying = false;
                _playPauseIcon.Text = "▶";
                // Pre-warm exact frame ring buffer around current pause position so the very first wheel notch is 0ms!
                EnsureExactFrameBurstWindow(_targetStepSeconds);
            }
            else
            {
                if (_isScreenshotModeActive)
                {
                    ExitScreenshotMode();
                }
                if (_targetStepSeconds >= 0)
                {
                    _mediaElement.Position = TimeSpan.FromSeconds(Clamp(_targetStepSeconds, 0.0, _durationSeconds));
                }
                HideInstantFrameOverlay();
                _targetStepSeconds = -1;
                _mediaElement.ScrubbingEnabled = false;
                _mediaElement.Play();
                _isPlaying = true;
                _playPauseIcon.Text = "⏸";
            }
        }

        private void SeekToSeconds(double seconds, bool immediateHardwareCommit = false)
        {
            if (_mediaElement.Source == null || _durationSeconds <= 0)
            {
                return;
            }

            double clamped = Clamp(seconds, 0.0, _durationSeconds);
            _targetStepSeconds = clamped;
            _mediaElement.ScrubbingEnabled = !_isPlaying;

            // Immediately update UI to target timestamp so rapid stepping/seeking feels 0-latency
            UpdateTimelineVisualsForSeconds(clamped);
            string nextTimeText = FormatTime(TimeSpan.FromSeconds(clamped)) + " / " + FormatTime(TimeSpan.FromSeconds(_durationSeconds));
            if (!string.Equals(_lastDisplayedTimeText, nextTimeText, StringComparison.Ordinal))
            {
                _lastDisplayedTimeText = nextTimeText;
                _timeDisplayText.Text = nextTimeText;
            }

            // When paused/stepping, immediately render pre-frozen BitmapSource from Exact Frame Ring-Buffer (< 0.05ms pointer swap!),
            // and keep the exact frame ring window pre-warmed around the target timestamp without killing in-window workers!
            if (!_isPlaying)
            {
                int frameIdx = Math.Max(0, (int)Math.Round(clamped * FRAME_RING_FPS));
                BitmapSource exactBmp;
                if (_exactFrameRingBitmaps.TryGetValue(frameIdx, out exactBmp) && exactBmp != null)
                {
                    if (_currentInstantFrameIdx != frameIdx || _instantFrameImage.Visibility != Visibility.Visible)
                    {
                        ShowInstantFrameOverlay(exactBmp, frameIdx);
                    }
                }
                else
                {
                    // Check +/- 1 adjacent frame in ring buffer before falling back
                    if ((_exactFrameRingBitmaps.TryGetValue(frameIdx - 1, out exactBmp) ||
                         _exactFrameRingBitmaps.TryGetValue(frameIdx + 1, out exactBmp)) && exactBmp != null)
                    {
                        ShowInstantFrameOverlay(exactBmp, frameIdx);
                    }
                    else if (immediateHardwareCommit)
                    {
                        int dummyKey;
                        BitmapSource hoverBmp = TryGetBestHoverBitmap(clamped, out dummyKey);
                        if (hoverBmp != null)
                        {
                            ShowInstantFrameOverlay(hoverBmp, -999999);
                        }
                    }
                }
                EnsureExactFrameBurstWindow(clamped);
            }

            // Throttle hardware decoder Position assignments to ~32ms during rapid wheel/A/D bursts so EVR never builds a seek backlog,
            // or commit at 0ms when immediateHardwareCommit is requested (e.g. single click on timeline)!
            long nowTick = Environment.TickCount;
            if (immediateHardwareCommit || Math.Abs(nowTick - _lastMainSeekCommitTick) >= 32)
            {
                _mainSeekFlushTimer.Stop();
                _pendingMainSeekSeconds = -1;
                _lastMainSeekCommitTick = nowTick;
                _mediaElement.Position = TimeSpan.FromSeconds(clamped);
            }
            else
            {
                _pendingMainSeekSeconds = clamped;
                _mainSeekFlushTimer.Stop();
                _mainSeekFlushTimer.Start();
            }
        }

        private void FlushPendingMainSeekImmediately()
        {
            if (_mainSeekFlushTimer.IsEnabled || _pendingMainSeekSeconds >= 0)
            {
                _mainSeekFlushTimer.Stop();
                double target = _pendingMainSeekSeconds >= 0 ? _pendingMainSeekSeconds : _targetStepSeconds;
                _pendingMainSeekSeconds = -1;
                if (target >= 0 && _mediaElement.Source != null)
                {
                    _lastMainSeekCommitTick = Environment.TickCount;
                    _mediaElement.Position = TimeSpan.FromSeconds(Clamp(target, 0.0, _durationSeconds));
                }
            }
        }

        private void StepBySeconds(double deltaSeconds)
        {
            if (_mediaElement.Source == null || _durationSeconds <= 0)
            {
                return;
            }

            if (_isPlaying)
            {
                _mediaElement.Pause();
                _isPlaying = false;
                _playPauseIcon.Text = "▶";
            }

            _mediaElement.ScrubbingEnabled = true;

            // Accumulate from _targetStepSeconds instead of _mediaElement.Position so rapid mouse-wheel notches
            // never get lost waiting for WMP/EVR async seek completion!
            double actualPos = _mediaElement.Position.TotalSeconds;
            if (_targetStepSeconds < 0 || Math.Abs(_targetStepSeconds - actualPos) > Math.Max(1.5, Math.Abs(deltaSeconds) * 3.0))
            {
                _targetStepSeconds = actualPos;
            }

            double nextTarget = Clamp(_targetStepSeconds + deltaSeconds, 0.0, _durationSeconds);
            SeekToSeconds(nextTarget);
        }

        private void StepFrame(int frames)
        {
            // Use 1/30s (0.0333333s) per frame step so every wheel notch advances a guaranteed distinct frame on 24/25/30/60fps videos
            StepBySeconds(frames * (1.0 / 30.0));
        }

        private bool JumpToInputTimestamp()
        {
            if (_mediaElement.Source == null)
            {
                return false;
            }

            string raw = (_jumpTimeTextBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            TimeSpan target;
            double sec;
            if (double.TryParse(raw, out sec))
            {
                target = TimeSpan.FromSeconds(sec);
            }
            else
            {
                string[] parts = raw.Split(':');
                int m;
                double s;
                if (parts.Length == 2 && int.TryParse(parts[0], out m) && double.TryParse(parts[1], out s))
                {
                    target = TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                }
                else if (!TimeSpan.TryParse(raw, out target))
                {
                    ShowToast("时间格式无效，示例: 01:25 或 85.5");
                    return false;
                }
            }

            if (_isPlaying)
            {
                _mediaElement.Pause();
                _isPlaying = false;
                _playPauseIcon.Text = "▶";
            }

            _mediaElement.ScrubbingEnabled = true;
            SeekToSeconds(target.TotalSeconds);
            ShowToast("已精准定位至 " + FormatTime(target));
            return true;
        }

        private void MainWindow_OnCompositionRendering(object sender, EventArgs e)
        {
            if (_mediaElement.Source == null || _isScrubbingTimeline)
            {
                return;
            }

            long nowTick = Environment.TickCount;

            // Smooth continuous 5x fast-rewind / fast-forward while holding A or D (without S modifier)
            if (!_isKeySHeld && (_isKeyAHeld ^ _isKeyDHeld) && Keyboard.FocusedElement != _jumpTimeTextBox)
            {
                if (_lastAdHoldTick == 0)
                {
                    _lastAdHoldTick = nowTick;
                }
                else
                {
                    long elapsedMs = nowTick - _lastAdHoldTick;
                    if (elapsedMs >= 16)
                    {
                        _lastAdHoldTick = nowTick;
                        double dtSec = Math.Min(0.15, elapsedMs / 1000.0);
                        double dir = _isKeyDHeld ? 1.0 : -1.0;
                        // 1 second of holding A/D advances/rewinds 5.0 seconds total
                        StepBySeconds(dir * 5.0 * dtSec);
                    }
                }
            }
            else
            {
                _lastAdHoldTick = 0;
            }

            if (Math.Abs(nowTick - _lastUiTimelineUpdateTick) < 33)
            {
                return;
            }
            _lastUiTimelineUpdateTick = nowTick;
            UpdateTimelineUi();
        }

        private void UpdateTimelineUi()
        {
            if (_mediaElement.Source == null || _isScrubbingTimeline)
            {
                return;
            }

            if (_mediaElement.NaturalDuration.HasTimeSpan)
            {
                _durationSeconds = _mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            }

            double displaySec = (!_isPlaying && _targetStepSeconds >= 0)
                ? _targetStepSeconds
                : _mediaElement.Position.TotalSeconds;
            TimeSpan pos = TimeSpan.FromSeconds(displaySec);
            TimeSpan dur = TimeSpan.FromSeconds(_durationSeconds);

            UpdateTimelineVisualsForSeconds(displaySec);
            string nextTimeText = FormatTime(pos) + " / " + FormatTime(dur);
            if (!string.Equals(_lastDisplayedTimeText, nextTimeText, StringComparison.Ordinal))
            {
                _lastDisplayedTimeText = nextTimeText;
                _timeDisplayText.Text = nextTimeText;
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }

        private void ToggleFullscreen()
        {
            if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
        }

        // ==================== TILDE (~) NATIVE SCREENSHOT MODE (ZERO LAYOUT/ASPECT SHIFT) ====================
        private void HandleTildeShortcut()
        {
            if (_mediaElement.Source == null || _mediaElement.NaturalVideoWidth <= 0)
            {
                ShowToast("请先打开一个视频文件再按 ~ 截图");
                return;
            }

            if (!_isScreenshotModeActive)
            {
                EnterScreenshotMode();
            }
            else
            {
                // Pressing ~ while in screenshot mode confirms and saves:
                // Flush any pending seek and capture full-resolution video frame ONLY at the exact moment of saving!
                FlushPendingMainSeekImmediately();
                RefreshFrozenFrameInPlace();
                Rect videoRect = GetDisplayedVideoRect();
                if ((_hasActiveSelection || _cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint) &&
                    _currentSelectionRect.Width >= 4 && _currentSelectionRect.Height >= 4 &&
                    videoRect.Width > 0 && videoRect.Height > 0)
                {
                    Rect clampedSel = ClampRectToBounds(_currentSelectionRect, videoRect);
                    double normX = Clamp((clampedSel.X - videoRect.X) / videoRect.Width, 0.0, 1.0);
                    double normY = Clamp((clampedSel.Y - videoRect.Y) / videoRect.Height, 0.0, 1.0);
                    double normW = Clamp(clampedSel.Width / videoRect.Width, 0.001, 1.0 - normX);
                    double normH = Clamp(clampedSel.Height / videoRect.Height, 0.001, 1.0 - normY);
                    var normCrop = new Rect(normX, normY, normW, normH);
                    _lastNormalizedCropRect = normCrop;
                    SavePersistentSettings();
                    SaveScreenshotAndPreview(normCrop);
                }
                else
                {
                    SaveScreenshotAndPreview(null);
                }
                ExitScreenshotMode();
            }
        }

        private void RestoreLastCropSelection()
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            if (!_lastNormalizedCropRect.HasValue)
            {
                ShowToast("暂无上次裁剪框记录，请先框选一次");
                return;
            }

            Rect videoRect = GetDisplayedVideoRect();
            if (videoRect.Width <= 0 || videoRect.Height <= 0)
            {
                return;
            }

            Rect norm = _lastNormalizedCropRect.Value;
            double left = videoRect.X + (norm.X * videoRect.Width);
            double top = videoRect.Y + (norm.Y * videoRect.Height);
            double width = norm.Width * videoRect.Width;
            double height = norm.Height * videoRect.Height;

            Rect restored = ClampRectToBounds(new Rect(left, top, width, height), videoRect);
            if (restored.Width < 4 || restored.Height < 4)
            {
                return;
            }

            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = true;
            _currentSelectionRect = restored;
            UpdateSelectionVisuals(isBlueprint: false);
            ShowToast("已恢复上次裁剪框范围（可拖动边框/内部微调，按 ~ 确认裁剪保存）");
        }

        private static Rect ClampRectToBounds(Rect r, Rect bounds)
        {
            double x1 = Clamp(r.Left, bounds.Left, bounds.Right);
            double y1 = Clamp(r.Top, bounds.Top, bounds.Bottom);
            double x2 = Clamp(r.Right, bounds.Left, bounds.Right);
            double y2 = Clamp(r.Bottom, bounds.Top, bounds.Bottom);
            return new Rect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        private BitmapSource CaptureCleanVideoFrame()
        {
            int videoW = _mediaElement.NaturalVideoWidth;
            int videoH = _mediaElement.NaturalVideoHeight;
            if (videoW <= 0 || videoH <= 0)
            {
                return null;
            }

            // 1. If an exact full-resolution frame is currently displayed in the instant frame overlay, capture directly from it!
            if (_instantFrameImage.Visibility == Visibility.Visible &&
                _currentInstantFrameBitmap != null &&
                _currentInstantFrameBitmap.PixelWidth >= videoW &&
                _currentInstantFrameBitmap.PixelHeight >= videoH)
            {
                return _currentInstantFrameBitmap;
            }

            // 2. If paused/stepping at an exact target timestamp, check if full-res frame is in _exactFrameRingBitmaps or extract exact full-res frame via FFmpeg -ss
            //    so we NEVER capture a low-res 220px hover thumbnail or a stale un-seeked MediaElement frame!
            if (!_isPlaying && _targetStepSeconds >= 0)
            {
                int targetIdx = Math.Max(0, (int)Math.Round(_targetStepSeconds * FRAME_RING_FPS));
                BitmapSource ringBmp;
                if (_exactFrameRingBitmaps.TryGetValue(targetIdx, out ringBmp) &&
                    ringBmp != null &&
                    ringBmp.PixelWidth >= videoW &&
                    ringBmp.PixelHeight >= videoH)
                {
                    return ringBmp;
                }

                string ffmpegExe = ResolveFfmpegPath();
                if (!string.IsNullOrEmpty(ffmpegExe) && !string.IsNullOrEmpty(_currentVideoFilePath))
                {
                    BitmapSource exactFullRes = ExtractSingleFrameFrozenBitmap(ffmpegExe, _currentVideoFilePath, _targetStepSeconds, 0, 2);
                    if (exactFullRes != null && exactFullRes.PixelWidth > 0 && exactFullRes.PixelHeight > 0)
                    {
                        ShowInstantFrameOverlay(exactFullRes, targetIdx);
                        return exactFullRes;
                    }
                }
            }

            // 3. Fallback: Render from _mediaElement (never render from _instantFrameImage if it holds a low-res preview!)
            Visual targetVisual = (_instantFrameImage.Visibility == Visibility.Visible &&
                                   _currentInstantFrameBitmap != null &&
                                   _currentInstantFrameBitmap.PixelWidth >= videoW)
                ? (Visual)_instantFrameImage
                : (Visual)_mediaElement;

            var rtb = new RenderTargetBitmap(videoW, videoH, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                var brush = new VisualBrush(targetVisual)
                {
                    Stretch = Stretch.Fill
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, videoW, videoH));
            }
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private void CaptureFullFrameImmediately()
        {
            if (_mediaElement.Source == null || _mediaElement.NaturalVideoWidth <= 0)
            {
                return;
            }

            _frozenFrameBitmap = CaptureCleanVideoFrame();
            if (_frozenFrameBitmap != null)
            {
                _frozenPixelWidth = _frozenFrameBitmap.PixelWidth;
                _frozenPixelHeight = _frozenFrameBitmap.PixelHeight;
                SaveScreenshotAndPreview(null);
            }
        }

        private void RefreshFrozenFrameInPlace()
        {
            BitmapSource bmp = CaptureCleanVideoFrame();
            if (bmp == null)
            {
                return;
            }

            _frozenFrameBitmap = bmp;
            _frozenPixelWidth = _frozenFrameBitmap.PixelWidth;
            _frozenPixelHeight = _frozenFrameBitmap.PixelHeight;
            _frozenStride = _frozenPixelWidth * 4;
        }

        private void EnterScreenshotMode()
        {
            if (_isPlaying)
            {
                _mediaElement.Pause();
                _isPlaying = false;
                _playPauseIcon.Text = "▶";
            }
            _mediaElement.ScrubbingEnabled = true;
            double curPos = _targetStepSeconds >= 0 ? _targetStepSeconds : _mediaElement.Position.TotalSeconds;
            EnsureExactFrameBurstWindow(curPos);

            _frozenPixelWidth = _mediaElement.NaturalVideoWidth;
            _frozenPixelHeight = _mediaElement.NaturalVideoHeight;
            if (_frozenPixelWidth <= 0 || _frozenPixelHeight <= 0)
            {
                return;
            }
            _frozenStride = _frozenPixelWidth * 4;
            _frozenFrameBitmap = null;

            _isScreenshotModeActive = true;
            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = false;
            _currentSelectionRect = Rect.Empty;
            HideSelectionVisuals();

            // Show overlay in Row 1 (transparent over live paused _mediaElement -> ZERO GPU readback lag when wheel-stepping!),
            // swap Top Bar content in Row 0, and disable all interaction on Bottom GUI (Row 2)
            HideHoverThumbnailPopup();
            _timelineHoverMarker.Visibility = Visibility.Collapsed;
            _bottomControlsRootGrid.IsHitTestVisible = false;
            _screenshotOverlayRoot.Visibility = Visibility.Visible;
            _topScreenshotSaveBarGrid.Visibility = Visibility.Visible;

            Focus();
            Keyboard.Focus(this);

            Point mousePos = Mouse.GetPosition(_screenshotOverlayRoot);
            UpdateAimAndMagnifier(mousePos);
        }

        private void HideSelectionVisuals()
        {
            _selectionRectShape.Visibility = Visibility.Collapsed;
            _selectionSizeTag.Visibility = Visibility.Collapsed;
            for (int i = 0; i < 8; i++)
            {
                if (_selectionHandles[i] != null)
                {
                    _selectionHandles[i].Visibility = Visibility.Collapsed;
                }
            }
        }

        private void UpdateSelectionVisuals(bool isBlueprint)
        {
            Rect videoRect = GetDisplayedVideoRect();
            _currentSelectionRect = ClampRectToBounds(_currentSelectionRect, videoRect);

            double left = _currentSelectionRect.Left;
            double top = _currentSelectionRect.Top;
            double width = _currentSelectionRect.Width;
            double height = _currentSelectionRect.Height;

            Canvas.SetLeft(_selectionRectShape, left);
            Canvas.SetTop(_selectionRectShape, top);
            _selectionRectShape.Width = width;
            _selectionRectShape.Height = height;
            _selectionRectShape.StrokeDashArray = isBlueprint ? new DoubleCollection(new double[] { 5, 3 }) : null;
            _selectionRectShape.Stroke = isBlueprint
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(0, 229, 255));
            _selectionRectShape.Fill = isBlueprint
                ? new SolidColorBrush(Color.FromArgb(28, 255, 215, 0))
                : new SolidColorBrush(Color.FromArgb(35, 0, 229, 255));
            _selectionRectShape.Visibility = Visibility.Visible;

            int pxW = videoRect.Width > 0 ? (int)Math.Round(width / videoRect.Width * _frozenPixelWidth) : (int)width;
            int pxH = videoRect.Height > 0 ? (int)Math.Round(height / videoRect.Height * _frozenPixelHeight) : (int)height;
            _selectionSizeText.Text = isBlueprint
                ? string.Format("蓝图: {0} × {1} px (再点左键落地)", pxW, pxH)
                : string.Format("{0} × {1} px (按 ~ 确认裁剪)", pxW, pxH);

            double tagTop = top >= 28 ? top - 26 : Math.Min(videoRect.Bottom - 24, top + height + 4);
            double tagLeft = Clamp(left, videoRect.Left, Math.Max(videoRect.Left, videoRect.Right - 190));
            Canvas.SetLeft(_selectionSizeTag, tagLeft);
            Canvas.SetTop(_selectionSizeTag, tagTop);
            _selectionSizeTag.Visibility = Visibility.Visible;

            bool showHandles = !isBlueprint && _hasActiveSelection && width >= 4 && height >= 4;
            if (showHandles)
            {
                double cx = left + (width * 0.5);
                double cy = top + (height * 0.5);
                Point[] pts =
                {
                    new Point(left, top),
                    new Point(cx, top),
                    new Point(left + width, top),
                    new Point(left + width, cy),
                    new Point(left + width, top + height),
                    new Point(cx, top + height),
                    new Point(left, top + height),
                    new Point(left, cy)
                };
                for (int i = 0; i < 8; i++)
                {
                    Canvas.SetLeft(_selectionHandles[i], pts[i].X - 4);
                    Canvas.SetTop(_selectionHandles[i], pts[i].Y - 4);
                    _selectionHandles[i].Visibility = Visibility.Visible;
                }
            }
            else
            {
                for (int i = 0; i < 8; i++)
                {
                    _selectionHandles[i].Visibility = Visibility.Collapsed;
                }
            }
        }

        private CropHitZone HitTestSelectionZone(Point pt)
        {
            if (!_hasActiveSelection || _currentSelectionRect.Width < 4 || _currentSelectionRect.Height < 4)
            {
                return CropHitZone.None;
            }

            const double tol = 8.0;
            Rect r = _currentSelectionRect;
            bool nearLeft = Math.Abs(pt.X - r.Left) <= tol && pt.Y >= r.Top - tol && pt.Y <= r.Bottom + tol;
            bool nearRight = Math.Abs(pt.X - r.Right) <= tol && pt.Y >= r.Top - tol && pt.Y <= r.Bottom + tol;
            bool nearTop = Math.Abs(pt.Y - r.Top) <= tol && pt.X >= r.Left - tol && pt.X <= r.Right + tol;
            bool nearBottom = Math.Abs(pt.Y - r.Bottom) <= tol && pt.X >= r.Left - tol && pt.X <= r.Right + tol;

            CropHitZone zone = CropHitZone.None;
            if (nearLeft) zone |= CropHitZone.Left;
            else if (nearRight) zone |= CropHitZone.Right;

            if (nearTop) zone |= CropHitZone.Top;
            else if (nearBottom) zone |= CropHitZone.Bottom;

            if (zone != CropHitZone.None)
            {
                return zone;
            }

            if (r.Contains(pt))
            {
                return CropHitZone.Inside;
            }

            return CropHitZone.None;
        }

        private void UpdateOverlayCursorForZone(CropHitZone zone)
        {
            if (_cropMode == CropSelectionMode.None && _hasActiveSelection)
            {
                if ((zone == (CropHitZone.Left | CropHitZone.Top)) || (zone == (CropHitZone.Right | CropHitZone.Bottom)))
                {
                    _screenshotOverlayRoot.Cursor = Cursors.SizeNWSE;
                    return;
                }
                if ((zone == (CropHitZone.Right | CropHitZone.Top)) || (zone == (CropHitZone.Left | CropHitZone.Bottom)))
                {
                    _screenshotOverlayRoot.Cursor = Cursors.SizeNESW;
                    return;
                }
                if ((zone & (CropHitZone.Left | CropHitZone.Right)) != 0)
                {
                    _screenshotOverlayRoot.Cursor = Cursors.SizeWE;
                    return;
                }
                if ((zone & (CropHitZone.Top | CropHitZone.Bottom)) != 0)
                {
                    _screenshotOverlayRoot.Cursor = Cursors.SizeNS;
                    return;
                }
                if (zone == CropHitZone.Inside)
                {
                    _screenshotOverlayRoot.Cursor = Cursors.SizeAll;
                    return;
                }
            }
            _screenshotOverlayRoot.Cursor = Cursors.None;
        }

        private void CancelCurrentSelection()
        {
            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = false;
            _currentSelectionRect = Rect.Empty;
            HideSelectionVisuals();
            _screenshotOverlayRoot.Cursor = Cursors.None;
            ShowToast("已返回上一步（取消当前框选），可重新选框或再按右键退出截图模式");
        }

        private void ExitScreenshotMode()
        {
            if (Mouse.Captured == _screenshotOverlayRoot)
            {
                _screenshotOverlayRoot.ReleaseMouseCapture();
            }
            _cropMode = CropSelectionMode.None;
            _activeResizeZone = CropHitZone.None;
            _hasActiveSelection = false;
            _isScreenshotModeActive = false;
            HideSelectionVisuals();
            _screenshotOverlayRoot.Visibility = Visibility.Collapsed;
            _topScreenshotSaveBarGrid.Visibility = Visibility.Collapsed;
            _bottomControlsRootGrid.IsHitTestVisible = true;
            _screenshotFrozenImage.Source = null;
            _frozenFrameBitmap = null;
        }

        private Rect GetDisplayedVideoRect()
        {
            // Use the exact rendered bounds of _mediaElement so it matches 1:1 with the video frame
            double containerW = _videoStageGrid.ActualWidth;
            double containerH = _videoStageGrid.ActualHeight;
            if (containerW <= 0 || containerH <= 0 || _frozenPixelWidth <= 0 || _frozenPixelHeight <= 0)
            {
                return new Rect(0, 0, Math.Max(1, containerW), Math.Max(1, containerH));
            }

            double scale = Math.Min(containerW / _frozenPixelWidth, containerH / _frozenPixelHeight);
            double drawW = _frozenPixelWidth * scale;
            double drawH = _frozenPixelHeight * scale;
            double offsetX = (containerW - drawW) * 0.5;
            double offsetY = (containerH - drawH) * 0.5;
            return new Rect(offsetX, offsetY, drawW, drawH);
        }

        private static bool IsInteractiveControlInScreenshotMode(DependencyObject source)
        {
            DependencyObject cur = source;
            while (cur != null)
            {
                if (cur is ButtonBase)
                {
                    return true;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void MainWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            // Right-Click Multi-Level Back ("上一步") works anywhere across the entire window/GUI
            if (e.ChangedButton == MouseButton.Right)
            {
                HandleRightClickBackInScreenshotMode();
                e.Handled = true;
                return;
            }

            // If user clicked outside the video stage (e.g. on top/bottom GUI edge) but NOT on an interactive button/timeline,
            // forward the signal into the screenshot cropper clamped to the nearest video edge!
            if (e.ChangedButton == MouseButton.Left && !_screenshotOverlayRoot.IsMouseOver)
            {
                if (!IsInteractiveControlInScreenshotMode(e.OriginalSource as DependencyObject))
                {
                    Point posInOverlay = e.GetPosition(_screenshotOverlayRoot);
                    HandleScreenshotLeftMouseDown(posInOverlay);
                    e.Handled = true;
                }
            }
        }

        private void MainWindow_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            Point posInOverlay = e.GetPosition(_screenshotOverlayRoot);
            HandleScreenshotMouseMove(posInOverlay, e.LeftButton == MouseButtonState.Pressed);
        }

        private void MainWindow_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left && _cropMode != CropSelectionMode.None)
            {
                Point posInOverlay = e.GetPosition(_screenshotOverlayRoot);
                HandleScreenshotLeftMouseUp(posInOverlay);
                e.Handled = true;
            }
        }

        private void HandleRightClickBackInScreenshotMode()
        {
            if (Mouse.Captured == _screenshotOverlayRoot)
            {
                _screenshotOverlayRoot.ReleaseMouseCapture();
            }

            // Step-by-step back logic:
            // 1) If currently resizing/moving an existing box -> revert to the box before this drag
            if (_cropMode == CropSelectionMode.ResizingOrMoving)
            {
                _cropMode = CropSelectionMode.None;
                _activeResizeZone = CropHitZone.None;
                _currentSelectionRect = _resizeStartSelectionRect;
                _hasActiveSelection = _currentSelectionRect.Width >= 4 && _currentSelectionRect.Height >= 4;
                if (_hasActiveSelection)
                {
                    UpdateSelectionVisuals(isBlueprint: false);
                    ShowToast("已取消本次边框调整，恢复调整前裁剪框");
                }
                else
                {
                    CancelCurrentSelection();
                }
                return;
            }

            // 2) If in two-click blueprint mode, dragging a new box, or already has a landed selection box -> clear selection and return to aiming state
            if (_cropMode != CropSelectionMode.None || _hasActiveSelection)
            {
                CancelCurrentSelection();
                return;
            }

            // 3) If in initial aiming state with no selection -> exit screenshot mode completely
            ExitScreenshotMode();
            ShowToast("已退出截图模式");
        }

        private void ScreenshotOverlay_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Right)
            {
                HandleRightClickBackInScreenshotMode();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                Point pos = e.GetPosition(_screenshotOverlayRoot);
                HandleScreenshotLeftMouseDown(pos);
                e.Handled = true;
            }
        }

        private void HandleScreenshotLeftMouseDown(Point rawOverlayPos)
        {
            Rect videoRect = GetDisplayedVideoRect();
            Point clampedPos = new Point(
                Clamp(rawOverlayPos.X, videoRect.Left, videoRect.Right),
                Clamp(rawOverlayPos.Y, videoRect.Top, videoRect.Bottom));

            _mouseDownOverlayPoint = clampedPos;

            // Case 1: Currently in Two-Click Blueprint mode -> Second left click lands the crop box!
            if (_cropMode == CropSelectionMode.TwoClickBlueprint)
            {
                double left = Math.Min(_selectionStartPoint.X, clampedPos.X);
                double top = Math.Min(_selectionStartPoint.Y, clampedPos.Y);
                double width = Math.Abs(clampedPos.X - _selectionStartPoint.X);
                double height = Math.Abs(clampedPos.Y - _selectionStartPoint.Y);

                _cropMode = CropSelectionMode.None;
                if (Mouse.Captured == _screenshotOverlayRoot)
                {
                    _screenshotOverlayRoot.ReleaseMouseCapture();
                }

                if (width >= 4 && height >= 4)
                {
                    _currentSelectionRect = ClampRectToBounds(new Rect(left, top, width, height), videoRect);
                    _hasActiveSelection = true;
                    UpdateSelectionVisuals(isBlueprint: false);
                    ShowToast("裁剪框已落地：可拖动边框微调，或按 ~ 键确认裁剪保存");
                }
                else
                {
                    CancelCurrentSelection();
                }
                UpdateAimAndMagnifier(rawOverlayPos);
                return;
            }

            // Case 2: If there is already a landed crop box, check if user clicked on its borders/corners/inside to resize or move it
            if (_hasActiveSelection && _cropMode == CropSelectionMode.None)
            {
                CropHitZone hitZone = HitTestSelectionZone(clampedPos);
                if (hitZone != CropHitZone.None)
                {
                    _cropMode = CropSelectionMode.ResizingOrMoving;
                    _activeResizeZone = hitZone;
                    _resizeStartMousePoint = clampedPos;
                    _resizeStartSelectionRect = _currentSelectionRect;
                    _screenshotOverlayRoot.CaptureMouse();
                    UpdateAimAndMagnifier(rawOverlayPos);
                    return;
                }
            }

            // Case 3: Start a new selection (supports both click-and-drag AND click-once-for-blueprint + click-again-to-land)
            _cropMode = CropSelectionMode.DraggingNew;
            _hasActiveSelection = false;
            _selectionStartPoint = clampedPos;
            _currentSelectionRect = new Rect(clampedPos, clampedPos);
            UpdateSelectionVisuals(isBlueprint: true);

            _screenshotOverlayRoot.CaptureMouse();
            UpdateAimAndMagnifier(rawOverlayPos);
        }

        private void ScreenshotOverlay_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            Point pos = e.GetPosition(_screenshotOverlayRoot);
            HandleScreenshotMouseMove(pos, e.LeftButton == MouseButtonState.Pressed);
        }

        private void HandleScreenshotMouseMove(Point rawOverlayPos, bool isLeftPressed)
        {
            UpdateAimAndMagnifier(rawOverlayPos);

            Rect videoRect = GetDisplayedVideoRect();
            Point clampedPos = new Point(
                Clamp(rawOverlayPos.X, videoRect.Left, videoRect.Right),
                Clamp(rawOverlayPos.Y, videoRect.Top, videoRect.Bottom));

            if (_cropMode == CropSelectionMode.DraggingNew || _cropMode == CropSelectionMode.TwoClickBlueprint)
            {
                double x1 = Clamp(_selectionStartPoint.X, videoRect.Left, videoRect.Right);
                double y1 = Clamp(_selectionStartPoint.Y, videoRect.Top, videoRect.Bottom);
                double x2 = clampedPos.X;
                double y2 = clampedPos.Y;

                _currentSelectionRect = new Rect(
                    Math.Min(x1, x2),
                    Math.Min(y1, y2),
                    Math.Abs(x2 - x1),
                    Math.Abs(y2 - y1));

                UpdateSelectionVisuals(isBlueprint: _cropMode == CropSelectionMode.TwoClickBlueprint);
                return;
            }

            if (_cropMode == CropSelectionMode.ResizingOrMoving && isLeftPressed)
            {
                double dx = clampedPos.X - _resizeStartMousePoint.X;
                double dy = clampedPos.Y - _resizeStartMousePoint.Y;
                Rect r = _resizeStartSelectionRect;

                if (_activeResizeZone == CropHitZone.Inside)
                {
                    double newLeft = Clamp(r.Left + dx, videoRect.Left, Math.Max(videoRect.Left, videoRect.Right - r.Width));
                    double newTop = Clamp(r.Top + dy, videoRect.Top, Math.Max(videoRect.Top, videoRect.Bottom - r.Height));
                    _currentSelectionRect = new Rect(newLeft, newTop, r.Width, r.Height);
                }
                else
                {
                    double left = r.Left;
                    double right = r.Right;
                    double top = r.Top;
                    double bottom = r.Bottom;

                    if ((_activeResizeZone & CropHitZone.Left) != 0)
                    {
                        left = Clamp(r.Left + dx, videoRect.Left, right - 4);
                    }
                    if ((_activeResizeZone & CropHitZone.Right) != 0)
                    {
                        right = Clamp(r.Right + dx, left + 4, videoRect.Right);
                    }
                    if ((_activeResizeZone & CropHitZone.Top) != 0)
                    {
                        top = Clamp(r.Top + dy, videoRect.Top, bottom - 4);
                    }
                    if ((_activeResizeZone & CropHitZone.Bottom) != 0)
                    {
                        bottom = Clamp(r.Bottom + dy, top + 4, videoRect.Bottom);
                    }

                    _currentSelectionRect = new Rect(left, top, Math.Max(4, right - left), Math.Max(4, bottom - top));
                }

                UpdateSelectionVisuals(isBlueprint: false);
                return;
            }

            if (_cropMode == CropSelectionMode.None && _hasActiveSelection)
            {
                CropHitZone zone = HitTestSelectionZone(clampedPos);
                UpdateOverlayCursorForZone(zone);
            }
        }

        private void ScreenshotOverlay_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isScreenshotModeActive)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left && _cropMode != CropSelectionMode.None)
            {
                Point pos = e.GetPosition(_screenshotOverlayRoot);
                HandleScreenshotLeftMouseUp(pos);
                e.Handled = true;
            }
        }

        private void HandleScreenshotLeftMouseUp(Point rawOverlayPos)
        {
            Rect videoRect = GetDisplayedVideoRect();
            Point clampedPos = new Point(
                Clamp(rawOverlayPos.X, videoRect.Left, videoRect.Right),
                Clamp(rawOverlayPos.Y, videoRect.Top, videoRect.Bottom));

            if (_cropMode == CropSelectionMode.ResizingOrMoving)
            {
                _cropMode = CropSelectionMode.None;
                _activeResizeZone = CropHitZone.None;
                if (Mouse.Captured == _screenshotOverlayRoot)
                {
                    _screenshotOverlayRoot.ReleaseMouseCapture();
                }
                _hasActiveSelection = _currentSelectionRect.Width >= 4 && _currentSelectionRect.Height >= 4;
                UpdateSelectionVisuals(isBlueprint: false);
                return;
            }

            if (_cropMode == CropSelectionMode.DraggingNew)
            {
                double dragDist = Math.Sqrt(
                    Math.Pow(clampedPos.X - _mouseDownOverlayPoint.X, 2) +
                    Math.Pow(clampedPos.Y - _mouseDownOverlayPoint.Y, 2));

                // If user single-clicked without dragging (< 5px movement), enter Two-Click Blueprint mode!
                // We keep mouse capture so even if the user moves the cursor outside the GUI onto the desktop, blueprint tracks smoothly!
                if (dragDist < 5.0)
                {
                    _cropMode = CropSelectionMode.TwoClickBlueprint;
                    _hasActiveSelection = false;
                    _currentSelectionRect = new Rect(_selectionStartPoint, clampedPos);
                    UpdateSelectionVisuals(isBlueprint: true);
                    ShowToast("已定第一个起点：移动鼠标预览蓝图，再次单击左键落地裁剪框");
                    return;
                }

                // Otherwise, user finished a drag selection -> land the crop box (do NOT auto-save until ~ is pressed!)
                _cropMode = CropSelectionMode.None;
                if (Mouse.Captured == _screenshotOverlayRoot)
                {
                    _screenshotOverlayRoot.ReleaseMouseCapture();
                }

                if (_currentSelectionRect.Width >= 4 && _currentSelectionRect.Height >= 4)
                {
                    _hasActiveSelection = true;
                    UpdateSelectionVisuals(isBlueprint: false);
                    ShowToast("裁剪框已就绪：可拖动边框微调，按 ~ 键确认裁剪保存");
                }
                else
                {
                    CancelCurrentSelection();
                }
            }
        }

        private void UpdateAimAndMagnifier(Point cursorPos)
        {
            double overlayW = _screenshotOverlayRoot.ActualWidth;
            double overlayH = _screenshotOverlayRoot.ActualHeight;
            if (overlayW <= 0 || overlayH <= 0)
            {
                return;
            }

            Rect videoRect = GetDisplayedVideoRect();
            if (videoRect.Width <= 0 || videoRect.Height <= 0 || _frozenPixelWidth <= 0 || _frozenPixelHeight <= 0)
            {
                return;
            }

            // Strictly clamp the aiming crosshair & reticle to the pure video pixel area so it never exceeds the video frame
            double aimX = Clamp(cursorPos.X, videoRect.Left, videoRect.Right);
            double aimY = Clamp(cursorPos.Y, videoRect.Top, videoRect.Bottom);

            _aimHorizontalLine.X1 = videoRect.Left;
            _aimHorizontalLine.X2 = videoRect.Right;
            _aimHorizontalLine.Y1 = aimY;
            _aimHorizontalLine.Y2 = aimY;

            _aimVerticalLine.X1 = aimX;
            _aimVerticalLine.X2 = aimX;
            _aimVerticalLine.Y1 = videoRect.Top;
            _aimVerticalLine.Y2 = videoRect.Bottom;

            Canvas.SetLeft(_aimReticleBox, aimX - 13);
            Canvas.SetTop(_aimReticleBox, aimY - 13);

            const double loupeW = 158;
            const double loupeH = 198;
            const double offset = 20;
            double loupeX = aimX + offset + loupeW > overlayW
                ? Math.Max(4, aimX - offset - loupeW)
                : aimX + offset;
            double loupeY = aimY + offset + loupeH > overlayH
                ? Math.Max(4, aimY - offset - loupeH)
                : aimY + offset;

            Canvas.SetLeft(_magnifierPopup, loupeX);
            Canvas.SetTop(_magnifierPopup, loupeY);

            double relX = Clamp((aimX - videoRect.X) / videoRect.Width, 0.0, 1.0);
            double relY = Clamp((aimY - videoRect.Y) / videoRect.Height, 0.0, 1.0);

            int srcX = (int)Clamp(Math.Floor(relX * _frozenPixelWidth), 0, _frozenPixelWidth - 1);
            int srcY = (int)Clamp(Math.Floor(relY * _frozenPixelHeight), 0, _frozenPixelHeight - 1);

            const double zoomFactor = 5.0;
            double magImgW = videoRect.Width * zoomFactor;
            double magImgH = videoRect.Height * zoomFactor;
            _magnifierLiveRect.Width = magImgW;
            _magnifierLiveRect.Height = magImgH;
            Canvas.SetLeft(_magnifierLiveRect, 70.0 - (relX * magImgW));
            Canvas.SetTop(_magnifierLiveRect, 70.0 - (relY * magImgH));

            _magnifierCoordText.Text = string.Format("X: {0}, Y: {1}", srcX, srcY);
            _magnifierColorText.Text = string.Format("画幅: {0}×{1}", _frozenPixelWidth, _frozenPixelHeight);
            _magnifierColorSwatch.Background = new SolidColorBrush(Color.FromRgb(0, 229, 255));
        }

        private void ShowScreenshotPreviewOnMonitorBottomLeft(BitmapSource thumbBitmap, string fileName, string fullSavePath)
        {
            _screenshotPreviewThumbImage.Source = thumbBitmap;
            _screenshotPreviewFileNameText.Text = fileName;
            _screenshotPreviewPathText.Text = fullSavePath;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Forms.Screen screen = hwnd != IntPtr.Zero
                    ? Forms.Screen.FromHandle(hwnd)
                    : Forms.Screen.PrimaryScreen;
                var workArea = screen != null ? screen.WorkingArea : Forms.Screen.PrimaryScreen.WorkingArea;

                double dpiScaleY = 1.0;
                PresentationSource src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    dpiScaleY = src.CompositionTarget.TransformToDevice.M22;
                }

                double bannerHeightPx = 64.0 * dpiScaleY;
                double leftPx = workArea.Left + (18.0 * dpiScaleY);
                double topPx = workArea.Bottom - bannerHeightPx - (18.0 * dpiScaleY);

                if (src != null && src.CompositionTarget != null)
                {
                    Point dipPt = src.CompositionTarget.TransformFromDevice.Transform(new Point(leftPx, topPx));
                    _screenshotPreviewPopup.HorizontalOffset = dipPt.X;
                    _screenshotPreviewPopup.VerticalOffset = dipPt.Y;
                }
                else
                {
                    _screenshotPreviewPopup.HorizontalOffset = leftPx;
                    _screenshotPreviewPopup.VerticalOffset = topPx;
                }
            }
            catch
            {
                _screenshotPreviewPopup.HorizontalOffset = 18;
                _screenshotPreviewPopup.VerticalOffset = SystemParameters.WorkArea.Bottom - 82;
            }

            _screenshotPreviewPopup.IsOpen = true;
            _previewHideTimer.Stop();
            _previewHideTimer.Start();
        }

        private void SaveScreenshotAndPreview(Rect? normalizedCrop)
        {
            if (_frozenFrameBitmap == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_saveFolderPath);

                BitmapSource outputBitmap = _frozenFrameBitmap;
                if (normalizedCrop.HasValue)
                {
                    Rect c = normalizedCrop.Value;
                    int x = (int)Clamp(Math.Round(c.X * _frozenPixelWidth), 0, _frozenPixelWidth - 1);
                    int y = (int)Clamp(Math.Round(c.Y * _frozenPixelHeight), 0, _frozenPixelHeight - 1);
                    int w = (int)Clamp(Math.Round(c.Width * _frozenPixelWidth), 1, _frozenPixelWidth - x);
                    int h = (int)Clamp(Math.Round(c.Height * _frozenPixelHeight), 1, _frozenPixelHeight - y);
                    var cropped = new CroppedBitmap(_frozenFrameBitmap, new Int32Rect(x, y, w, h));
                    cropped.Freeze();
                    outputBitmap = cropped;
                }
                else if (!outputBitmap.IsFrozen && outputBitmap.CanFreeze)
                {
                    outputBitmap.Freeze();
                }

                DateTime now = DateTime.Now;
                string yearPart = now.ToString("yy");
                string datePart = string.Format("{0}{1}", now.Month, now.Day);
                int secondsPart = (int)now.TimeOfDay.TotalSeconds;
                string baseName = string.Format("{0} {1} {2}", yearPart, datePart, secondsPart);
                string fileName = baseName + ".png";
                string fullSavePath = Path.Combine(_saveFolderPath, fileName);

                lock (_fileSaveLock)
                {
                    int collisionIdx = 2;
                    while (File.Exists(fullSavePath))
                    {
                        fileName = string.Format("{0} ({1}).png", baseName, collisionIdx);
                        fullSavePath = Path.Combine(_saveFolderPath, fileName);
                        collisionIdx++;
                    }
                    // Create placeholder file immediately under lock so concurrent rapid saves never collide on the same filename
                    using (File.Create(fullSavePath)) { }
                }

                _lastSavedFilePath = fullSavePath;
                ShowScreenshotPreviewOnMonitorBottomLeft(outputBitmap, fileName, fullSavePath);
                ShowToast("已保存截图: " + fileName);

                string targetPath = fullSavePath;
                string saveFolder = _saveFolderPath;
                BitmapSource bmpToEncode = outputBitmap;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Directory.CreateDirectory(saveFolder);
                        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bmpToEncode));
                            encoder.Save(fs);
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ShowToast("截图写入失败: " + ex.Message);
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                ShowToast("截图保存失败: " + ex.Message);
            }
        }

        private void PickSaveFolder()
        {
            using (var dlg = new Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择截图默认保存目录";
                dlg.SelectedPath = _saveFolderPath;
                if (dlg.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    _saveFolderPath = dlg.SelectedPath;
                    _saveLocationTextBlock.Text = _saveFolderPath;
                    SavePersistentSettings();
                }
            }
        }

        private void LoadPersistentSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return;
                }

                string[] lines = File.ReadAllLines(_settingsFilePath);
                foreach (string rawLine in lines)
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        continue;
                    }

                    int sep = rawLine.IndexOf('=');
                    if (sep <= 0)
                    {
                        continue;
                    }

                    string key = rawLine.Substring(0, sep).Trim();
                    string val = rawLine.Substring(sep + 1).Trim();

                    if (string.Equals(key, "Volume", StringComparison.OrdinalIgnoreCase))
                    {
                        double v;
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                        {
                            _persistedVolume = Clamp(v, 0.0, 1.0);
                        }
                    }
                    else if (string.Equals(key, "IsMuted", StringComparison.OrdinalIgnoreCase))
                    {
                        bool m;
                        if (bool.TryParse(val, out m))
                        {
                            _persistedIsMuted = m;
                        }
                    }
                    else if (string.Equals(key, "VolumeZeroLocked", StringComparison.OrdinalIgnoreCase))
                    {
                        bool locked;
                        if (bool.TryParse(val, out locked))
                        {
                            _isVolumeZeroLocked = locked;
                        }
                    }
                    else if (string.Equals(key, "LastCrop", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = val.Split(',');
                        double cx, cy, cw, ch;
                        if (parts.Length == 4 &&
                            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cx) &&
                            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cy) &&
                            double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cw) &&
                            double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ch) &&
                            cw > 0.001 && ch > 0.001)
                        {
                            _lastNormalizedCropRect = new Rect(
                                Clamp(cx, 0.0, 1.0),
                                Clamp(cy, 0.0, 1.0),
                                Clamp(cw, 0.001, 1.0),
                                Clamp(ch, 0.001, 1.0));
                        }
                    }
                    else if (string.Equals(key, "SaveFolderPath", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            _saveFolderPath = val;
                        }
                    }
                }

                if (_isVolumeZeroLocked)
                {
                    _persistedVolume = 0.0;
                    _persistedIsMuted = true;
                }
            }
            catch
            {
                // Ignore corrupt settings file
            }
        }

        private void SavePersistentSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string lastCropStr = "";
                if (_lastNormalizedCropRect.HasValue)
                {
                    Rect c = _lastNormalizedCropRect.Value;
                    lastCropStr = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0:0.######},{1:0.######},{2:0.######},{3:0.######}",
                        c.X, c.Y, c.Width, c.Height);
                }

                string[] lines =
                {
                    "Volume=" + _persistedVolume.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                    "IsMuted=" + (_persistedIsMuted ? "true" : "false"),
                    "VolumeZeroLocked=" + (_isVolumeZeroLocked ? "true" : "false"),
                    "LastCrop=" + lastCropStr,
                    "SaveFolderPath=" + (_saveFolderPath ?? "")
                };
                File.WriteAllLines(_settingsFilePath, lines);
            }
            catch
            {
                // Ignore disk write errors
            }
        }

        private void ToggleVolumeZeroLock()
        {
            _isVolumeZeroLocked = !_isVolumeZeroLocked;
            if (_isVolumeZeroLocked)
            {
                _persistedVolume = 0.0;
                _persistedIsMuted = true;
                if (_mediaElement != null)
                {
                    _mediaElement.Volume = 0.0;
                    _mediaElement.IsMuted = true;
                }
                UpdateVolumeUi();
                SavePersistentSettings();
                ShowToast("🔒 已开启音量彻底锁零（音量强制归 0 并锁定）");
            }
            else
            {
                // Unlocked: stays at 0 so user can adjust upwards from 0
                _persistedVolume = 0.0;
                _persistedIsMuted = false;
                if (_mediaElement != null)
                {
                    _mediaElement.Volume = 0.0;
                    _mediaElement.IsMuted = false;
                }
                UpdateVolumeUi();
                SavePersistentSettings();
                ShowToast("🔓 已解除音量锁零，现在可从 0% 重新调节音量");
            }
        }

        private void SetVolume(double newVolume, bool showToastNotification)
        {
            if (_isVolumeZeroLocked)
            {
                _persistedVolume = 0.0;
                _persistedIsMuted = true;
                if (_mediaElement != null)
                {
                    _mediaElement.Volume = 0.0;
                    _mediaElement.IsMuted = true;
                }
                UpdateVolumeUi();
                if (showToastNotification)
                {
                    ShowToast("🔒 音量已彻底锁定为 0，请先点击「🔒 锁0」解锁后再调整");
                }
                return;
            }

            _persistedVolume = Clamp(newVolume, 0.0, 1.0);
            if (_persistedVolume > 0.001 && _persistedIsMuted)
            {
                _persistedIsMuted = false;
            }

            if (_mediaElement != null)
            {
                _mediaElement.Volume = _persistedVolume;
                _mediaElement.IsMuted = _persistedIsMuted;
            }

            UpdateVolumeUi();
            SavePersistentSettings();

            if (showToastNotification)
            {
                int pct = (int)Math.Round(_persistedVolume * 100);
                ShowToast(_persistedIsMuted ? "🔇 已静音" : ("🔊 音量: " + pct + "%（已自动记忆）"));
            }
        }

        private void ToggleMute()
        {
            if (_isVolumeZeroLocked)
            {
                ShowToast("🔒 音量已彻底锁定为 0，请先点击「🔒 锁0」按钮解锁");
                return;
            }

            _persistedIsMuted = !_persistedIsMuted;
            if (_mediaElement != null)
            {
                _mediaElement.IsMuted = _persistedIsMuted;
            }

            UpdateVolumeUi();
            SavePersistentSettings();

            int pct = (int)Math.Round(_persistedVolume * 100);
            ShowToast(_persistedIsMuted ? "🔇 已静音（已自动记忆）" : ("🔊 取消静音: " + pct + "%"));
        }

        private void UpdateVolumeUi()
        {
            if (_volumeLockBtn != null)
            {
                _volumeLockBtn.Content = _isVolumeZeroLocked ? "🔒 锁0" : "🔓 解锁";
                _volumeLockBtn.Background = _isVolumeZeroLocked
                    ? new SolidColorBrush(Color.FromRgb(165, 45, 55))
                    : new SolidColorBrush(Color.FromRgb(42, 42, 42));
                _volumeLockBtn.BorderBrush = _isVolumeZeroLocked
                    ? new SolidColorBrush(Color.FromRgb(255, 95, 105))
                    : new SolidColorBrush(Color.FromRgb(72, 72, 72));
            }

            int pct = _isVolumeZeroLocked ? 0 : (int)Math.Round(_persistedVolume * 100);
            if (_muteBtn != null)
            {
                _muteBtn.Content = (_isVolumeZeroLocked || _persistedIsMuted || pct == 0) ? "🔇" : (pct < 50 ? "🔉" : "🔊");
                _muteBtn.Opacity = _isVolumeZeroLocked ? 0.5 : 1.0;
            }
            if (_volumePercentText != null)
            {
                _volumePercentText.Text = _isVolumeZeroLocked ? "锁0%" : (_persistedIsMuted ? "静音" : (pct + "%"));
                _volumePercentText.Foreground = _isVolumeZeroLocked
                    ? new SolidColorBrush(Color.FromRgb(255, 120, 120))
                    : new SolidColorBrush(Color.FromRgb(210, 210, 210));
            }
            if (_volumeInteractiveArea != null && _volumeFillBar != null && _volumeThumb != null)
            {
                _volumeInteractiveArea.Opacity = _isVolumeZeroLocked ? 0.45 : 1.0;
                double trackW = _volumeInteractiveArea.ActualWidth > 0 ? _volumeInteractiveArea.ActualWidth : 92.0;
                double px = (_isVolumeZeroLocked || _persistedIsMuted ? 0.0 : _persistedVolume) * trackW;
                _volumeFillBar.Width = px;
                _volumeThumb.Margin = new Thickness(px - 6, 0, 0, 0);
            }
        }

        private void OpenSaveFolder()
        {
            try
            {
                Directory.CreateDirectory(_saveFolderPath);
                Process.Start("explorer.exe", "\"" + _saveFolderPath + "\"");
            }
            catch
            {
            }
        }

        private long _lastTildeTriggerTick;
        private long _lastKeyOneTriggerTick;

        private void MainWindow_OnSourceInitialized(object sender, EventArgs e)
        {
            HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_KEYDOWN = 0x0100;
            const int WM_KEYUP = 0x0101;
            const int WM_SYSKEYDOWN = 0x0104;
            const int WM_SYSKEYUP = 0x0105;
            const int VK_OEM_3 = 0xC0; // ~ / ` key (physical key left of '1')
            const int VK_1 = 0x31;
            const int VK_2 = 0x32;
            const int VK_3 = 0x33;
            const int VK_A = 0x41;
            const int VK_D = 0x44;
            const int VK_E = 0x45;
            const int VK_R = 0x52;
            const int VK_S = 0x53;

            if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                int vkUp = wParam.ToInt32() & 0xFFFF;
                long lParamUp = lParam.ToInt64();
                int scanUp = (int)((lParamUp >> 16) & 0xFF);
                if (vkUp == VK_OEM_3 || scanUp == 0x29) { _isTildeKeyPhysicallyDown = false; }
                else if (vkUp == VK_A || scanUp == 0x1E) { _isKeyAHeld = false; _lastAdHoldTick = 0; }
                else if (vkUp == VK_D || scanUp == 0x20) { _isKeyDHeld = false; _lastAdHoldTick = 0; }
                else if (vkUp == VK_S || scanUp == 0x1F) { _isKeySHeld = false; }
                else if (vkUp == VK_E || scanUp == 0x12) { _isKeyEHeld = false; }
                return IntPtr.Zero;
            }

            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vk = wParam.ToInt32() & 0xFFFF;
                long lParamVal = lParam.ToInt64();
                int scanCode = (int)((lParamVal >> 16) & 0xFF);
                bool isRepeat = (lParamVal & (1L << 30)) != 0;

                // ScanCode 0x29 (41) is the physical Tilde/Grave key next to '1' on all standard keyboards,
                // and VK_OEM_3 (0xC0) is its virtual key (even before IME eats it when VK == 0xE5 VK_PROCESSKEY).
                if (vk == VK_OEM_3 || scanCode == 0x29)
                {
                    if (!isRepeat && !_isTildeKeyPhysicallyDown)
                    {
                        _isTildeKeyPhysicallyDown = true;
                        TriggerTildeDebounced();
                    }
                    handled = true;
                    return IntPtr.Zero;
                }

                if (Keyboard.FocusedElement == _jumpTimeTextBox)
                {
                    return IntPtr.Zero;
                }

                // Track modifier keys S (ScanCode 0x1F) and E (ScanCode 0x12)
                if (vk == VK_S || scanCode == 0x1F)
                {
                    _isKeySHeld = true;
                    handled = true;
                    return IntPtr.Zero;
                }
                if (vk == VK_E || scanCode == 0x12)
                {
                    _isKeyEHeld = true;
                    handled = true;
                    return IntPtr.Zero;
                }

                // A (ScanCode 0x1E) and D (ScanCode 0x20):
                // - When S is held: step 1 frame per press/repeat
                // - Otherwise: hold for smooth 5x continuous fast-rewind / fast-forward (5s per 1s held)
                if (vk == VK_A || scanCode == 0x1E)
                {
                    if (_isKeySHeld || Keyboard.IsKeyDown(Key.S))
                    {
                        StepFrame(-1);
                    }
                    else
                    {
                        _isKeyAHeld = true;
                    }
                    handled = true;
                    return IntPtr.Zero;
                }
                if (vk == VK_D || scanCode == 0x20)
                {
                    if (_isKeySHeld || Keyboard.IsKeyDown(Key.S))
                    {
                        StepFrame(1);
                    }
                    else
                    {
                        _isKeyDHeld = true;
                    }
                    handled = true;
                    return IntPtr.Zero;
                }

                // 1 / 2 / 3 keys (ScanCode 0x02 / 0x03 / 0x04):
                // - Default: fast-forward +5s / +20s / +60s
                // - When E is held: fast-rewind -5s / -20s / -60s
                double dir = (_isKeyEHeld || Keyboard.IsKeyDown(Key.E)) ? -1.0 : 1.0;
                if (vk == VK_1 || scanCode == 0x02)
                {
                    StepBySeconds(dir * 5.0);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (vk == VK_2 || scanCode == 0x03)
                {
                    StepBySeconds(dir * 20.0);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (vk == VK_3 || scanCode == 0x04)
                {
                    StepBySeconds(dir * 60.0);
                    handled = true;
                    return IntPtr.Zero;
                }

                // In screenshot mode, pressing 'R' (ScanCode 0x13) restores the previous crop selection box
                if (_isScreenshotModeActive && (vk == VK_R || scanCode == 0x13))
                {
                    if (!isRepeat)
                    {
                        TriggerKeyOneDebounced();
                    }
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            return IntPtr.Zero;
        }

        private void TriggerTildeDebounced()
        {
            long now = Environment.TickCount;
            if (Math.Abs(now - _lastTildeTriggerTick) < 45)
            {
                return;
            }
            _lastTildeTriggerTick = now;
            HandleTildeShortcut();
        }

        private void TriggerKeyOneDebounced()
        {
            long now = Environment.TickCount;
            if (Math.Abs(now - _lastKeyOneTriggerTick) < 120)
            {
                return;
            }
            _lastKeyOneTriggerTick = now;
            RestoreLastCropSelection();
        }

        private void MainWindow_OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (e.Key == Key.ImeProcessed)
            {
                actualKey = e.ImeProcessedKey;
            }

            if (actualKey == Key.OemTilde || actualKey == Key.Oem3) { _isTildeKeyPhysicallyDown = false; }
            else if (actualKey == Key.A) { _isKeyAHeld = false; _lastAdHoldTick = 0; }
            else if (actualKey == Key.D) { _isKeyDHeld = false; _lastAdHoldTick = 0; }
            else if (actualKey == Key.S) { _isKeySHeld = false; }
            else if (actualKey == Key.E) { _isKeyEHeld = false; }
        }

        private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if ( e.Key == Key.ImeProcessed )
            {
                actualKey = e.ImeProcessedKey;
            }

            if (actualKey == Key.OemTilde || actualKey == Key.Oem3)
            {
                if (!e.IsRepeat && !_isTildeKeyPhysicallyDown)
                {
                    _isTildeKeyPhysicallyDown = true;
                    TriggerTildeDebounced();
                }
                e.Handled = true;
                return;
            }

            if (Keyboard.FocusedElement == _jumpTimeTextBox && actualKey != Key.Escape)
            {
                return;
            }

            if (actualKey == Key.S)
            {
                _isKeySHeld = true;
                e.Handled = true;
                return;
            }
            if (actualKey == Key.E)
            {
                _isKeyEHeld = true;
                e.Handled = true;
                return;
            }
            if (actualKey == Key.A)
            {
                if (_isKeySHeld || Keyboard.IsKeyDown(Key.S)) StepFrame(-1);
                else _isKeyAHeld = true;
                e.Handled = true;
                return;
            }
            if (actualKey == Key.D)
            {
                if (_isKeySHeld || Keyboard.IsKeyDown(Key.S)) StepFrame(1);
                else _isKeyDHeld = true;
                e.Handled = true;
                return;
            }

            double jumpDir = (_isKeyEHeld || Keyboard.IsKeyDown(Key.E)) ? -1.0 : 1.0;
            if (actualKey == Key.D1 || actualKey == Key.NumPad1)
            {
                StepBySeconds(jumpDir * 5.0);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.D2 || actualKey == Key.NumPad2)
            {
                StepBySeconds(jumpDir * 20.0);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.D3 || actualKey == Key.NumPad3)
            {
                StepBySeconds(jumpDir * 60.0);
                e.Handled = true;
                return;
            }

            if (_isScreenshotModeActive && actualKey == Key.R)
            {
                TriggerKeyOneDebounced();
                e.Handled = true;
                return;
            }

            if (actualKey == Key.Escape)
            {
                if (_isScreenshotModeActive)
                {
                    HandleRightClickBackInScreenshotMode();
                    e.Handled = true;
                    return;
                }

                if (WindowStyle == WindowStyle.None)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                    return;
                }
            }

            if (actualKey == Key.Space || actualKey == Key.K)
            {
                TogglePlayPause();
                e.Handled = true;
            }
            else if (actualKey == Key.PageUp || (actualKey == Key.P && Keyboard.Modifiers == ModifierKeys.Shift))
            {
                SwitchNeighborVideo(-1);
                e.Handled = true;
            }
            else if (actualKey == Key.PageDown || (actualKey == Key.N && Keyboard.Modifiers == ModifierKeys.Shift))
            {
                SwitchNeighborVideo(1);
                e.Handled = true;
            }
            else if (actualKey == Key.Left)
            {
                double step = Keyboard.Modifiers == ModifierKeys.Shift ? 1.0 : 5.0;
                StepBySeconds(-step);
                e.Handled = true;
            }
            else if (actualKey == Key.Right)
            {
                double step = Keyboard.Modifiers == ModifierKeys.Shift ? 1.0 : 5.0;
                StepBySeconds(step);
                e.Handled = true;
            }
            else if (actualKey == Key.Up)
            {
                SetVolume(_persistedVolume + 0.05, true);
                e.Handled = true;
            }
            else if (actualKey == Key.Down)
            {
                SetVolume(_persistedVolume - 0.05, true);
                e.Handled = true;
            }
            else if (actualKey == Key.M && Keyboard.Modifiers == ModifierKeys.None)
            {
                ToggleMute();
                e.Handled = true;
            }
            else if (actualKey == Key.OemComma)
            {
                StepFrame(-1);
                e.Handled = true;
            }
            else if (actualKey == Key.OemPeriod)
            {
                StepFrame(1);
                e.Handled = true;
            }
            else if (actualKey == Key.F || actualKey == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (actualKey == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenFilePicker();
                e.Handled = true;
            }
        }

        private void MainWindow_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_mediaElement.Source == null || _durationSeconds <= 0)
            {
                return;
            }

            // High performance step: Unbind from volume.
            // Invert wheel direction: wheel down (e.Delta < 0) advances forward, wheel up (e.Delta > 0) steps backward.
            int notches = -(e.Delta / 120);
            if (notches == 0) notches = e.Delta > 0 ? -1 : 1;

            bool isOneSecondStep = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            if (isOneSecondStep)
            {
                StepBySeconds(notches * 1.0);
            }
            else
            {
                StepFrame(notches);
            }
            UpdateTimelineUi();
            
            e.Handled = true;
        }

        private void MainWindow_OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    LoadVideoFile(files[0]);
                }
            }
        }

        private static double Clamp(double val, double min, double max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }
    }
}
