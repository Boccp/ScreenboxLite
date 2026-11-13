using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenboxLite.Core;
using ScreenboxLite.Mpv;

namespace ScreenboxLite.Ui
{
    /// <summary>播放、时间轴、音量、播放列表。</summary>
    internal sealed partial class MainWindow
    {
        private MpvPlayer _player;
        private FrameNavigator _nav;
        private MpvThumbnailer _thumbs;

        private List<string> _folderVideos = new List<string>();
        private int _videoIndex = -1;

        private bool _scrubbing;
        private bool _wasPlayingBeforeScrub;
        private double _scrubDownX;
        private double _hoverSeconds = -1;
        private long _lastTimeUiTick;
        private Action _afterSettled; // 导航完成后执行一次（如“跳转并截图”）
        private readonly Stopwatch _loadClock = new Stopwatch();

        private void InitPlayback()
        {
            _player = new MpvPlayer(Dispatcher);
            _nav = new FrameNavigator(_player, Dispatcher);
            _thumbs = new MpvThumbnailer();
            _thumbs.Ready += OnThumbReady;

            _player.TimeChanged += OnTimeChanged;
            _player.PauseChanged += () => _playIcon.Text = _player.Paused ? "▶" : "⏸";
            _player.VideoChanged += () => { UpdateTimeUi(true); UpdateVideoVisibility(); };
            _player.FileLoaded += OnFileLoaded;
            _player.VideoReconfig += () => { _videoHost.DisableChildInput(); UpdateVideoVisibility(); };
            _player.EndFile += reason =>
            {
                if (reason == 4) ShowToast("无法播放该文件（格式损坏或不受支持）");
            };
            _nav.Settled += OnNavigationSettled;

            _timeline.MouseLeftButtonDown += Timeline_MouseDown;
            _timeline.MouseMove += Timeline_MouseMove;
            _timeline.MouseLeftButtonUp += Timeline_MouseUp;
            _timeline.MouseLeave += (s, e) => { if (!_scrubbing) HideHoverPopup(); };
            _timeline.SizeChanged += (s, e) => UpdateTimeUi(true);

            _volumeArea.MouseLeftButtonDown += VolumeArea_MouseDown;
            _volumeArea.MouseMove += VolumeArea_MouseMove;
            _volumeArea.MouseLeftButtonUp += (s, e) => { if (_volumeArea.IsMouseCaptured) _volumeArea.ReleaseMouseCapture(); };

            // 宿主 HWND 创建后才能初始化 mpv
            Loaded += (s, e) =>
            {
                try
                {
                    _player.Initialize(_videoHost.Hwnd);
                    ApplyVolumeToPlayer();
                    _videoHost.DisableChildInput();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "播放引擎初始化失败");
                    MessageBox.Show(this, "播放引擎初始化失败：\n" + ex.Message, "Screenbox", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                UpdateVolumeUi();
                if (!string.IsNullOrEmpty(_startupFile)) LoadVideo(_startupFile);
            };
            Closing += (s, e) =>
            {
                _settings.Save();
                _shotPreviewPopup.IsOpen = false;
                _hoverPopup.IsOpen = false;
            };
            Closed += (s, e) =>
            {
                _thumbs.Dispose();
                _player.Dispose();
                if (Perf.HasSamples) Log.Info("本次运行性能统计:\r\n" + Perf.Report());
            };
        }

        // ======================= 打开 / 切换 =======================

        private void OpenFilePicker()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Title = "选择要打开的视频文件";
                dlg.Filter = Playlist.OpenDialogFilter;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) LoadVideo(dlg.FileName);
            }
            FocusMain();
        }

        public void LoadVideo(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (_shotActive) ExitScreenshotMode();
            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _folderVideos = Playlist.GetSortedFolderVideos(dir);
                    _videoIndex = _folderVideos.FindIndex(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
                    if (_videoIndex < 0)
                    {
                        _folderVideos.Insert(0, full); // 扩展名不在列表中的文件也允许播放
                        _videoIndex = 0;
                    }
                }
                else
                {
                    _folderVideos = new List<string> { full };
                    _videoIndex = 0;
                }

                _nav.Reset();
                _hoverSeconds = -1;
                _loadClock.Restart();
                _player.Load(full);
                _player.SetPause(false);
                _thumbs.Open(full);

                string name = Path.GetFileName(full);
                _playlistCounterText.Text = string.Format("[{0}/{1}]", _videoIndex + 1, _folderVideos.Count);
                _titleText.Text = name + "  (" + full + ")";
                Title = name + " - Screenbox";
                _emptyPanel.Visibility = Visibility.Collapsed;
                Activate();
                FocusMain();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "打开视频失败 " + path);
                ShowToast("打开失败: " + ex.Message);
            }
        }

        private void SwitchNeighbor(int delta)
        {
            if (_folderVideos.Count <= 1 || _videoIndex < 0)
            {
                ShowToast("当前文件夹没有更多视频");
                return;
            }
            int next = (_videoIndex + delta + _folderVideos.Count) % _folderVideos.Count;
            string path = _folderVideos[next];
            LoadVideo(path);
            ShowToast((delta > 0 ? "⏭ 下一个视频: " : "⏮ 上一个视频: ") + Path.GetFileName(path));
        }

        private void OnFileLoaded()
        {
            Perf.Record("file.open", _loadClock.Elapsed.TotalMilliseconds);
            if (_speedCombo.SelectedIndex >= 0) _player.SetSpeed(Speeds[_speedCombo.SelectedIndex]);
            ApplyVolumeToPlayer();
            _videoHost.DisableChildInput();
            UpdateVideoVisibility();
            UpdateTimeUi(true);
        }

        private void UpdateVideoVisibility()
        {
            bool loaded = _player.CurrentPath != null;
            _emptyPanel.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            // 截图模式下隐藏原生窗口，露出 WPF 的冻结帧与选框（原生窗口之上无法绘制 WPF 内容）
            _videoHost.Visibility = loaded && !_shotActive ? Visibility.Visible : Visibility.Hidden;
        }

        // ======================= 播放控制 =======================

        private void TogglePlayPause()
        {
            if (_player.CurrentPath == null) return;
            if (_player.Paused)
            {
                if (_shotActive) ExitScreenshotMode();
                if (_player.EofReached) _player.SeekAbsolute(0, true);
                _player.SetPause(false);
            }
            else
            {
                _player.SetPause(true);
            }
        }

        private void ToggleFullscreen()
        {
            if (WindowStyle == WindowStyle.None)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // 先还原才能盖住任务栏
                WindowState = WindowState.Maximized;
            }
        }

        private bool JumpToInput(bool thenCapture)
        {
            if (_player.CurrentPath == null) return false;
            double sec;
            if (!TryParseTime(_jumpBox.Text, out sec))
            {
                ShowToast("时间格式无效，示例: 01:25 或 85.5");
                return false;
            }
            _player.SetPause(true);
            _nav.SeekTo(sec, true);
            FocusMain();
            ShowToast("已精准定位至 " + FormatTime(sec));
            if (thenCapture) _afterSettled = CaptureFullFrameNow;
            return true;
        }

        /// <summary>支持 "85.5"、"01:25"、"00:01:25.500"。</summary>
        internal static bool TryParseTime(string raw, out double seconds)
        {
            seconds = 0;
            raw = (raw ?? "").Trim().Replace('：', ':');
            if (raw.Length == 0) return false;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return seconds >= 0;
            string[] parts = raw.Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            double total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0) return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }

        private void OnNavigationSettled()
        {
            UpdateTimeUi(true);
            if (_shotActive) RefreshFrozenFrameAsync();
            Action after = _afterSettled;
            _afterSettled = null;
            if (after != null) after();
        }

        // ======================= 时间显示 =======================

        private void OnTimeChanged()
        {
            // 播放中最多 ~30Hz 刷新文字，进度条每帧刷新
            long now = Environment.TickCount;
            bool force = _player.Paused;
            if (!force && Math.Abs(now - _lastTimeUiTick) < 33) return;
            _lastTimeUiTick = now;
            UpdateTimeUi(force);
        }

        private double DisplayTime()
        {
            if (_scrubbing) return _nav.TargetTime;
            return _nav.Busy ? _nav.TargetTime : _player.TimePos;
        }

        private void UpdateTimeUi(bool force)
        {
            double dur = _player.Duration;
            double t = DisplayTime();
            _timeText.Text = FormatTime(t) + " / " + FormatTime(dur);
            if (_player.Fps > 1)
            {
                long frame = (long)Math.Round(t * _player.Fps);
                _frameText.Text = "#" + frame + "  " + _player.Fps.ToString("0.###", CultureInfo.InvariantCulture) + "fps";
            }
            else
            {
                _frameText.Text = "";
            }
            double w = _timeline.ActualWidth;
            double px = (dur > 0 && w > 0) ? Clamp(t / dur, 0, 1) * w : 0;
            _timelineFill.Width = px;
            _timelineThumb.Margin = new Thickness(px - 7, 0, 0, 0);
        }

        // ======================= 时间轴：点击 / 拖动 / 悬停 =======================

        private double SecondsAtX(double x)
        {
            double w = _timeline.ActualWidth;
            if (w <= 0 || _player.Duration <= 0) return 0;
            return Clamp(x / w, 0, 1) * _player.Duration;
        }

        private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_player.Duration <= 0) return;
            _scrubbing = true;
            _wasPlayingBeforeScrub = !_player.Paused;
            _timeline.CaptureMouse();
            Point p = e.GetPosition(_timeline);
            _scrubDownX = p.X;
            double t = SecondsAtX(p.X);
            _nav.SeekTo(t, true);
            UpdateTimeUi(true);
            ShowHoverPopup(p.X, t);
            e.Handled = true;
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            if (_player.Duration <= 0) return;
            Point p = e.GetPosition(_timeline);
            double t = SecondsAtX(p.X);
            if (_scrubbing && e.LeftButton == MouseButtonState.Pressed)
            {
                // 真正开始拖动（≥4px）才暂停，单击不打断播放
                if (!_player.Paused && Math.Abs(p.X - _scrubDownX) >= 4) _player.SetPause(true);
                _nav.SeekTo(t, false); // 拖动中只到关键帧，跟手
                UpdateTimeUi(true);
            }
            ShowHoverPopup(p.X, t);
        }

        private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scrubbing) return;
            _scrubbing = false;
            _timeline.ReleaseMouseCapture();
            Point p = e.GetPosition(_timeline);
            double t = SecondsAtX(p.X);
            if (Math.Abs(p.X - _scrubDownX) >= 2) _nav.SeekTo(t, true); // 松手落到精确帧
            if (_wasPlayingBeforeScrub && _player.Paused) _player.SetPause(false);
            if (!_timeline.IsMouseOver) HideHoverPopup();
            e.Handled = true;
        }

        private void ShowHoverPopup(double x, double seconds)
        {
            double w = _timeline.ActualWidth;
            if (w <= 0 || _player.Duration <= 0) return;
            double cx = Clamp(x, 0, w);
            _timelineHoverMarker.Margin = new Thickness(cx - 1, 0, 0, 0);
            _timelineHoverMarker.Visibility = Visibility.Visible;
            _hoverTimeText.Text = FormatTime(seconds);
            _hoverSeconds = seconds;

            const double popupW = 196, popupH = 134;
            _hoverPopup.HorizontalOffset = Clamp(cx - popupW / 2, -8, Math.Max(-8, w - popupW + 8));
            _hoverPopup.VerticalOffset = -popupH - 8;
            if (!_hoverPopup.IsOpen) _hoverPopup.IsOpen = true;

            bool exact;
            var img = _thumbs.TryGet(seconds, out exact);
            if (img != null) _hoverImage.Source = img;
            if (!exact) _thumbs.Request(seconds);
        }

        private void HideHoverPopup()
        {
            _hoverSeconds = -1;
            _hoverPopup.IsOpen = false;
            _timelineHoverMarker.Visibility = Visibility.Collapsed;
        }

        private void OnThumbReady(int gen, double t, System.Windows.Media.Imaging.BitmapSource bmp)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (_hoverSeconds < 0 || gen != _thumbs.Generation) return;
                bool exact;
                var best = _thumbs.TryGet(_hoverSeconds, out exact);
                if (best != null) _hoverImage.Source = best;
            }));
        }

        // ======================= 音量（锁 0 / 静音 / 调节） =======================

        private void ApplyVolumeToPlayer()
        {
            bool locked = _settings.VolumeZeroLocked;
            _player.SetVolume(locked ? 0 : _settings.Volume);
            _player.SetMute(locked || _settings.IsMuted);
        }

        private void ToggleVolumeLock()
        {
            _settings.VolumeZeroLocked = !_settings.VolumeZeroLocked;
            _settings.Volume = 0;
            _settings.IsMuted = _settings.VolumeZeroLocked;
            ApplyVolumeToPlayer();
            UpdateVolumeUi();
            _settings.Save();
            ShowToast(_settings.VolumeZeroLocked ? "🔒 已开启音量彻底锁零（音量强制归 0 并锁定）" : "🔓 已解除音量锁零，现在可从 0% 重新调节音量");
        }

        private void SetVolume(double v, bool toast)
        {
            if (_settings.VolumeZeroLocked)
            {
                if (toast) ShowToast("🔒 音量已彻底锁定为 0，请先点击「🔒 锁0」解锁后再调整");
                return;
            }
            _settings.Volume = Clamp(v, 0, 1);
            if (_settings.Volume > 0.001) _settings.IsMuted = false;
            ApplyVolumeToPlayer();
            UpdateVolumeUi();
            _settings.Save();
            if (toast) ShowToast(_settings.IsMuted ? "🔇 已静音" : "🔊 音量: " + (int)Math.Round(_settings.Volume * 100) + "%（已自动记忆）");
        }

        private void ToggleMute()
        {
            if (_settings.VolumeZeroLocked)
            {
                ShowToast("🔒 音量已彻底锁定为 0，请先点击「🔒 锁0」按钮解锁");
                return;
            }
            _settings.IsMuted = !_settings.IsMuted;
            ApplyVolumeToPlayer();
            UpdateVolumeUi();
            _settings.Save();
            ShowToast(_settings.IsMuted ? "🔇 已静音（已自动记忆）" : "🔊 取消静音: " + (int)Math.Round(_settings.Volume * 100) + "%");
        }

        private void VolumeArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_settings.VolumeZeroLocked)
            {
                ShowToast("🔒 音量已彻底锁定为 0，请先点击「🔒 锁0」按钮解锁后再从 0 调节");
                return;
            }
            _volumeArea.CaptureMouse();
            SetVolume(e.GetPosition(_volumeArea).X / Math.Max(1, _volumeArea.ActualWidth), true);
        }

        private void VolumeArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_volumeArea.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            SetVolume(e.GetPosition(_volumeArea).X / Math.Max(1, _volumeArea.ActualWidth), true);
        }

        private void UpdateVolumeUi()
        {
            bool locked = _settings.VolumeZeroLocked;
            _volumeLockBtn.Content = locked ? "🔒 锁0" : "🔓 解锁";
            _volumeLockBtn.Background = locked ? Theme.LockedBg : Theme.ButtonBg;
            _volumeLockBtn.BorderBrush = locked ? Theme.LockedBorder : Theme.ButtonBorder;
            int pct = locked ? 0 : (int)Math.Round(_settings.Volume * 100);
            _muteBtn.Content = (locked || _settings.IsMuted || pct == 0) ? "🔇" : (pct < 50 ? "🔉" : "🔊");
            _muteBtn.Opacity = locked ? 0.5 : 1.0;
            _volumeText.Text = locked ? "锁0%" : (_settings.IsMuted ? "静音" : pct + "%");
            _volumeText.Foreground = locked ? Theme.LockedText : Theme.Frozen(Color.FromRgb(210, 210, 210));
            _volumeArea.Opacity = locked ? 0.45 : 1.0;
            double w = _volumeArea.ActualWidth > 0 ? _volumeArea.ActualWidth : 92;
            double px = (locked || _settings.IsMuted ? 0 : _settings.Volume) * w;
            _volumeFill.Width = px;
            _volumeThumb.Margin = new Thickness(px - 6, 0, 0, 0);
        }
    }
}
