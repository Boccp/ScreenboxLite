using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenboxLite.Core;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// ~ 键截图模式。
    /// 进入时从 mpv 取当前帧的原始像素（视频原始分辨率），显示为冻结帧并在其上选框——
    /// 所见即所得：保存的就是这张原始帧的裁剪，不经过屏幕缩放，也不会截到黑图。
    /// 模式内滚轮/快捷键照常逐帧，帧落定后自动刷新冻结帧，选框保持不动。
    /// </summary>
    internal sealed partial class MainWindow
    {
        private bool _shotActive;
        private bool _videoMirrored;
        private static bool _mpvGrabIncludesMirrorShader; // 由真实像素探测或默认确定
        private static bool _mpvGrabMirrorProbed;
        private readonly CropSelection _crop = new CropSelection();
        private BitmapSource _rawFrozen;
        private bool _rawFrozenMirrored;
        private BitmapSource _flippedFrozen;
        private BitmapSource _frozen;
        private int _grabGeneration;
        private bool _grabInFlight;
        private bool _grabPending;
        private bool _saveWhenReady;
        private string _lastSavedPath;
        private Rect _lastBounds = Rect.Empty;

        internal bool IsShotFlipped { get { return _videoMirrored; } }
        internal bool IsVideoMirrored { get { return _videoMirrored; } }
        internal BitmapSource FrozenFrameForTest { get { return _frozen; } }
        internal string TempSaveFolderOverride;

        private void HandleTilde()
        {
            if (_player.CurrentPath == null || !_player.HasVideo)
            {
                ShowToast("请先打开一个视频文件再按 ~ 截图");
                return;
            }
            if (!_shotActive)
            {
                EnterScreenshotMode();
                return;
            }
            // 模式内再按 ~：确认保存（若帧还在落定/刷新，等它完成后自动保存）
            if (_nav.Busy || _grabInFlight)
            {
                _saveWhenReady = true;
                return;
            }
            SaveCurrentSelection();
        }

        private void EnterScreenshotMode()
        {
            if (!_player.Paused) _player.SetPause(true);
            bool grabMirrored;
            BitmapSource frame;
            using (Perf.Measure("shot.grab.enter"))
            {
                frame = GrabFrameTrackingMirror(out grabMirrored);
            }
            if (frame == null)
            {
                ShowToast("截取画面失败，请稍后重试");
                return;
            }
            // 继承当前播放态已有的水平翻转状态，进出截图模式与保存后均保持不重置
            SetRawFrozen(frame, grabMirrored);
            UpdateShotFlipUi();

            _shotActive = true;
            _grabPending = false;
            _saveWhenReady = false;
            _crop.Reset();
            CloseHistoryPopup();
            HideHoverPopup();
            _frozenImage.Visibility = Visibility.Visible;
            _overlayRoot.Visibility = Visibility.Visible;
            _topScreenshotBar.Visibility = Visibility.Visible;
            _bottomBar.IsHitTestVisible = false;
            _bottomBar.Opacity = 0.52;
            UpdateVideoVisibility(); // 隐藏原生视频窗口，露出冻结帧
            _lastBounds = VideoRect();
            RenderSelection();
            FocusMain();
            UpdateShotShield();
            UpdateAim(Mouse.GetPosition(_overlayRoot));
            // 正在导航（如滚轮刚滚过）时，落定后会再刷新一次
            if (_nav.Busy) _grabGeneration++;
        }

        private void ExitScreenshotMode()
        {
            if (_overlayRoot.IsMouseCaptured) _overlayRoot.ReleaseMouseCapture();
            _shotActive = false;
            _grabPending = false;
            _saveWhenReady = false;
            HideShotShield();
            _crop.Reset();
            HideSelectionVisuals();
            _overlayRoot.Visibility = Visibility.Collapsed;
            _topScreenshotBar.Visibility = Visibility.Collapsed;
            _bottomBar.IsHitTestVisible = true;
            _bottomBar.Opacity = 1.0;
            UpdateShotFlipUi();
            UpdateVideoVisibility();
            _frozenImage.Visibility = Visibility.Collapsed;
            _frozenImage.Source = null;
            _magnifierBrush.ImageSource = null;
            _rawFrozen = null;
            _flippedFrozen = null;
            _frozen = null;
            FocusMain();
        }

        /// <summary>抓帧并精确记录该原始帧是否已包含 GPU 镜像着色器效果（首次镜像抓帧时自动比对探测一次，零猜测）。</summary>
        private BitmapSource GrabFrameTrackingMirror(out bool alreadyMirrored)
        {
            bool playerMirrored = _player != null && _player.IsHorizontalMirrored;
            if (!playerMirrored)
            {
                alreadyMirrored = false;
                return _player.GrabFrame();
            }
            if (!_mpvGrabMirrorProbed)
            {
                BitmapSource withShader = _player.GrabFrame();
                _player.SetHorizontalMirror(false);
                BitmapSource withoutShader = _player.GrabFrame();
                _player.SetHorizontalMirror(true);
                if (!IsCenterRowSymmetric(withoutShader))
                {
                    _mpvGrabIncludesMirrorShader = !AreBitmapsIdenticalSample(withShader, withoutShader);
                    _mpvGrabMirrorProbed = true;
                }
                alreadyMirrored = _mpvGrabIncludesMirrorShader;
                return withShader;
            }
            alreadyMirrored = _mpvGrabIncludesMirrorShader;
            return _player.GrabFrame();
        }

        private static bool IsCenterRowSymmetric(BitmapSource bmp)
        {
            if (bmp == null || bmp.PixelWidth < 4 || bmp.PixelHeight < 1) return true;
            int w = bmp.PixelWidth;
            int[] row = new int[w];
            bmp.CopyPixels(new Int32Rect(0, bmp.PixelHeight / 2, w, 1), row, w * 4, 0);
            for (int l = 0, r = w - 1; l < r; l++, r--)
            {
                if ((row[l] & 0xFFFFFF) != (row[r] & 0xFFFFFF)) return false;
            }
            return true;
        }

        private static bool AreBitmapsIdenticalSample(BitmapSource a, BitmapSource b)
        {
            if (a == null || b == null) return true;
            if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight || a.PixelWidth < 2) return true;
            int w = a.PixelWidth, h = a.PixelHeight;
            int y = h / 2;
            byte[] rowA = new byte[w * 4];
            byte[] rowB = new byte[w * 4];
            a.CopyPixels(new Int32Rect(0, y, w, 1), rowA, w * 4, 0);
            b.CopyPixels(new Int32Rect(0, y, w, 1), rowB, w * 4, 0);
            for (int i = 0; i < rowA.Length; i++)
            {
                if (rowA[i] != rowB[i]) return false;
            }
            return true;
        }

        private void SetRawFrozen(BitmapSource rawFrame, bool rawIsAlreadyMirrored)
        {
            _rawFrozen = rawFrame;
            _rawFrozenMirrored = rawIsAlreadyMirrored;
            _flippedFrozen = null;
            ApplyFrozenView();
        }

        private void ApplyFrozenView()
        {
            if (_rawFrozen == null)
            {
                _frozen = null;
                _frozenImage.Source = null;
                _magnifierBrush.ImageSource = null;
                return;
            }
            if (_videoMirrored != _rawFrozenMirrored)
            {
                if (_flippedFrozen == null) _flippedFrozen = ScreenshotWriter.FlipHorizontal(_rawFrozen);
                _frozen = _flippedFrozen;
            }
            else
            {
                _frozen = _rawFrozen;
            }
            _frozenImage.Source = _frozen;
            _magnifierBrush.ImageSource = _frozen;
        }

        /// <summary>切换视频文件时重置水平翻转状态（保存截图与退出截图模式不重置）。</summary>
        private void ResetMirrorOnFileChange()
        {
            if (!_videoMirrored && (_player == null || !_player.IsHorizontalMirrored)) return;
            _videoMirrored = false;
            if (_player != null) _player.SetHorizontalMirror(false);
            UpdateShotFlipUi();
        }

        /// <summary>全局 Tab 水平镜像切换：无论处于普通播放/暂停态还是 ~截图模式均可即时启用，且截图后保持不复原。</summary>
        internal void ToggleHorizontalMirror()
        {
            if (_player == null || _player.CurrentPath == null || !_player.HasVideo)
            {
                ShowToast("请先打开一个视频文件再按 Tab 水平镜像");
                return;
            }
            _videoMirrored = !_videoMirrored;
            _player.SetHorizontalMirror(_videoMirrored);
            UpdateShotFlipUi();

            if (_shotActive && _rawFrozen != null)
            {
                ApplyFrozenView();
                _crop.MirrorHorizontal(VideoRect());
                RenderSelection();
                UpdateAim(Mouse.GetPosition(_overlayRoot));
            }
            ShowToast(_videoMirrored
                ? "↔ 已开启水平镜像（播放与截图同步保持，切换视频或关闭软件后自动还原）"
                : "↔ 已关闭水平镜像，恢复原始画面方向");
        }

        private void UpdateShotFlipUi()
        {
            bool compact = (ActualWidth > 0 ? ActualWidth : Width) < 930;
            if (_shotFlipBtn != null)
            {
                _shotFlipBtn.Background = _videoMirrored ? Theme.FlipActiveBg : Theme.ButtonBg;
                _shotFlipBtn.BorderBrush = _videoMirrored ? Theme.FlipActiveBorder : Theme.ButtonBorder;
                _shotFlipBtn.Foreground = _videoMirrored ? Theme.FlipActiveText : Theme.TextMain;
                if (_shotFlipIcon != null) _shotFlipIcon.Fill = _videoMirrored ? Theme.FlipActiveText : Theme.TextMain;
                if (_shotFlipText != null) _shotFlipText.Text = _videoMirrored ? "已镜像 (Tab)" : "镜像 (Tab)";
            }
            if (_normalFlipBtn != null)
            {
                _normalFlipBtn.Background = _videoMirrored ? Theme.FlipActiveBg : Theme.ButtonBg;
                _normalFlipBtn.BorderBrush = _videoMirrored ? Theme.FlipActiveBorder : Theme.ButtonBorder;
                _normalFlipBtn.Foreground = _videoMirrored ? Theme.FlipActiveText : Theme.TextMain;
                if (_normalFlipIcon != null) _normalFlipIcon.Fill = _videoMirrored ? Theme.FlipActiveText : Theme.TextDim;
                if (_normalFlipText != null)
                    _normalFlipText.Text = compact ? (_videoMirrored ? "已镜像" : "镜像") : (_videoMirrored ? "已镜像 (Tab)" : "镜像 (Tab)");
            }
        }

        /// <summary>
        /// 模式内帧落定后，以严格“单飞合并”方式在后台重新取帧（同一时刻最多 1 个 screenshot-raw 在途，
        /// 彻底杜绝快速滚轮时数十个并发 GPU 回读冲垮 vo_gpu_next 导致卡顿）。
        /// </summary>
        private void RefreshFrozenFrameAsync()
        {
            _grabGeneration++;
            if (!_shotActive) return;
            if (_grabInFlight)
            {
                _grabPending = true;
                return;
            }
            StartSingleGrab();
        }

        private void StartSingleGrab()
        {
            if (!_shotActive) return;
            _grabInFlight = true;
            _grabPending = false;
            int gen = _grabGeneration;
            bool grabMirrored = _mpvGrabIncludesMirrorShader && _player != null && _player.IsHorizontalMirrored;
            var sw = Stopwatch.StartNew();
            Task.Factory.StartNew(() => _player.GrabFrame()).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    _grabInFlight = false;
                    Perf.Record("shot.grab.refresh", sw.Elapsed.TotalMilliseconds);
                    if (!_shotActive)
                    {
                        _grabPending = false;
                        return;
                    }
                    if (_grabPending || gen != _grabGeneration)
                    {
                        StartSingleGrab();
                        return;
                    }
                    if (t.Exception == null && t.Result != null)
                    {
                        Rect before = VideoRect();
                        SetRawFrozen(t.Result, grabMirrored);
                        Rect after = VideoRect();
                        if (before != after) _crop.Rescale(before, after);
                        RenderSelection();
                        UpdateAim(Mouse.GetPosition(_overlayRoot));
                    }
                    if (_saveWhenReady && !_nav.Busy)
                    {
                        _saveWhenReady = false;
                        SaveCurrentSelection();
                    }
                }));
            }, TaskScheduler.Default);
        }

        private void SaveCurrentSelection()
        {
            Rect? norm = _crop.Normalized(VideoRect());
            if (norm.HasValue)
            {
                _settings.LastCrop = norm.Value;
                _settings.Save();
            }
            SaveBitmap(ScreenshotWriter.Crop(_frozen, norm));
            ExitScreenshotMode();
        }

        /// <summary>“跳转并全屏截图”：直接保存整帧（自动遵循当前水平镜像状态），不进入截图模式。</summary>
        private void CaptureFullFrameNow()
        {
            bool grabMirrored;
            BitmapSource frame = GrabFrameTrackingMirror(out grabMirrored);
            if (frame == null)
            {
                ShowToast("截取画面失败");
                return;
            }
            if (_videoMirrored != grabMirrored)
                frame = ScreenshotWriter.FlipHorizontal(frame);
            SaveBitmap(frame);
        }

        private void SaveBitmap(BitmapSource bmp)
        {
            if (bmp == null) return;
            try
            {
                string folder = !string.IsNullOrEmpty(TempSaveFolderOverride) ? TempSaveFolderOverride : _settings.SaveFolderPath;
                ShotHistoryItem item = CreateAndSaveHistoryItem(bmp, folder);
                _lastSavedPath = item.FilePath;
                ShowToast("✔ 已保存截图: " + item.FileName + "（已加入底栏历史截图队列）");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "截图保存失败");
                ShowToast("截图保存失败: " + ex.Message);
            }
        }

        // ======================= 选框交互 =======================

        /// <summary>冻结帧在舞台中的实际显示区域（等比居中）。</summary>
        private Rect VideoRect()
        {
            double cw = _stage.ActualWidth, ch = _stage.ActualHeight;
            int fw = _frozen != null ? _frozen.PixelWidth : _player.VideoWidth;
            int fh = _frozen != null ? _frozen.PixelHeight : _player.VideoHeight;
            if (cw <= 0 || ch <= 0 || fw <= 0 || fh <= 0) return new Rect(0, 0, Math.Max(1, cw), Math.Max(1, ch));
            double s = Math.Min(cw / fw, ch / fh);
            double w = fw * s, h = fh * s;
            return new Rect((cw - w) / 2, (ch - h) / 2, w, h);
        }

        private void ShotMouseDown(Point overlayPos)
        {
            string msg = _crop.MouseDown(overlayPos, VideoRect());
            if (_crop.WantsCapture) _overlayRoot.CaptureMouse();
            else if (_overlayRoot.IsMouseCaptured) _overlayRoot.ReleaseMouseCapture();
            if (msg != null) ShowToast(msg);
            RenderSelection();
            UpdateAim(overlayPos);
        }

        private void ShotMouseMove(Point overlayPos, bool leftPressed)
        {
            UpdateAim(overlayPos);
            Rect b = VideoRect();
            _crop.MouseMove(overlayPos, leftPressed, b);
            if (_crop.Mode != CropMode.None) RenderSelection();
            else UpdateOverlayCursor(_crop.HitTest(CropSelection.ClampPoint(overlayPos, b)));
        }

        private void ShotMouseUp(Point overlayPos)
        {
            string msg = _crop.MouseUp(overlayPos, VideoRect());
            if (!_crop.WantsCapture && _overlayRoot.IsMouseCaptured) _overlayRoot.ReleaseMouseCapture();
            if (msg != null) ShowToast(msg);
            RenderSelection();
        }

        private void ShotBack()
        {
            if (_overlayRoot.IsMouseCaptured) _overlayRoot.ReleaseMouseCapture();
            switch (_crop.Back())
            {
                case CropBackResult.RevertedAdjust:
                    ShowToast("已取消本次边框调整，恢复调整前裁剪框");
                    break;
                case CropBackResult.Cleared:
                    ShowToast("已返回上一步（取消当前框选），可重新选框或再按右键退出截图模式");
                    break;
                default:
                    ExitScreenshotMode();
                    ShowToast("已退出截图模式");
                    return;
            }
            _overlayRoot.Cursor = Cursors.None;
            RenderSelection();
        }

        private void RestoreLastCrop()
        {
            if (!_shotActive) return;
            if (!_settings.LastCrop.HasValue)
            {
                ShowToast("暂无上次裁剪框记录，请先框选一次");
                return;
            }
            if (_crop.Restore(_settings.LastCrop.Value, VideoRect()))
            {
                RenderSelection();
                ShowToast("已恢复上次裁剪框范围（可拖动边框/内部微调，按 ~ 确认裁剪保存）");
            }
        }

        private void HideSelectionVisuals()
        {
            _selectionShape.Visibility = Visibility.Collapsed;
            _sizeTag.Visibility = Visibility.Collapsed;
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        }

        private void RenderSelection()
        {
            if (!_shotActive || !_crop.HasBox)
            {
                HideSelectionVisuals();
                return;
            }
            Rect b = VideoRect();
            Rect r = CropSelection.ClampRect(_crop.Current, b);
            bool bp = _crop.IsBlueprint;

            Canvas.SetLeft(_selectionShape, r.Left);
            Canvas.SetTop(_selectionShape, r.Top);
            _selectionShape.Width = r.Width;
            _selectionShape.Height = r.Height;
            _selectionShape.Stroke = bp ? Theme.Blueprint : Theme.Accent;
            _selectionShape.Fill = bp ? Theme.BlueprintFill : Theme.SelectionFill;
            _selectionShape.StrokeDashArray = bp ? new DoubleCollection(new double[] { 5, 3 }) : null;
            _selectionShape.Visibility = Visibility.Visible;

            int fw = _frozen != null ? _frozen.PixelWidth : 0, fh = _frozen != null ? _frozen.PixelHeight : 0;
            int pw = b.Width > 0 ? (int)Math.Round(r.Width / b.Width * fw) : 0;
            int ph = b.Height > 0 ? (int)Math.Round(r.Height / b.Height * fh) : 0;
            _sizeText.Text = bp ? string.Format("蓝图: {0} × {1} px (再点左键落地)", pw, ph) : string.Format("{0} × {1} px (按 ~ 确认裁剪)", pw, ph);
            Canvas.SetLeft(_sizeTag, Clamp(r.Left, b.Left, Math.Max(b.Left, b.Right - 190)));
            Canvas.SetTop(_sizeTag, r.Top >= 28 ? r.Top - 26 : Math.Min(b.Bottom - 24, r.Bottom + 4));
            _sizeTag.Visibility = Visibility.Visible;

            bool handles = _crop.ShowHandles;
            double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            Point[] pts =
            {
                new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
                new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy)
            };
            for (int i = 0; i < 8; i++)
            {
                _handles[i].Visibility = handles ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(_handles[i], pts[i].X - 4);
                Canvas.SetTop(_handles[i], pts[i].Y - 4);
            }
        }

        private void UpdateOverlayCursor(CropHitZone z)
        {
            Cursor c = Cursors.None;
            if (z == (CropHitZone.Left | CropHitZone.Top) || z == (CropHitZone.Right | CropHitZone.Bottom)) c = Cursors.SizeNWSE;
            else if (z == (CropHitZone.Right | CropHitZone.Top) || z == (CropHitZone.Left | CropHitZone.Bottom)) c = Cursors.SizeNESW;
            else if ((z & (CropHitZone.Left | CropHitZone.Right)) != 0) c = Cursors.SizeWE;
            else if ((z & (CropHitZone.Top | CropHitZone.Bottom)) != 0) c = Cursors.SizeNS;
            else if (z == CropHitZone.Inside) c = Cursors.SizeAll;
            if (_overlayRoot.Cursor != c) _overlayRoot.Cursor = c;
        }

        /// <summary>准星、十字线与像素放大镜（坐标钳制在画面内）。</summary>
        private void UpdateAim(Point pos)
        {
            double ow = _overlayRoot.ActualWidth, oh = _overlayRoot.ActualHeight;
            if (!_shotActive || _frozen == null || ow <= 0 || oh <= 0) return;
            Rect b = VideoRect();
            double x = Clamp(pos.X, b.Left, b.Right), y = Clamp(pos.Y, b.Top, b.Bottom);

            _aimH.X1 = b.Left; _aimH.X2 = b.Right; _aimH.Y1 = y; _aimH.Y2 = y;
            _aimV.X1 = x; _aimV.X2 = x; _aimV.Y1 = b.Top; _aimV.Y2 = b.Bottom;
            Canvas.SetLeft(_reticle, x - 13);
            Canvas.SetTop(_reticle, y - 13);

            const double lw = 158, lh = 198, off = 20;
            Canvas.SetLeft(_magnifier, x + off + lw > ow ? Math.Max(4, x - off - lw) : x + off);
            Canvas.SetTop(_magnifier, y + off + lh > oh ? Math.Max(4, y - off - lh) : y + off);

            int fw = _frozen.PixelWidth, fh = _frozen.PixelHeight;
            int sx = (int)Clamp(Math.Floor((x - b.X) / b.Width * fw), 0, fw - 1);
            int sy = (int)Clamp(Math.Floor((y - b.Y) / b.Height * fh), 0, fh - 1);
            // 放大 5 倍：取以光标像素为中心的 28×28 像素区域（冻结帧 96dpi，DIP = 像素）
            _magnifierBrush.Viewbox = new Rect(sx + 0.5 - 14, sy + 0.5 - 14, 28, 28);
            _magnifierCoord.Text = string.Format("X: {0}, Y: {1}  ({2}×{3})", sx, sy, fw, fh);

            var px = new byte[4];
            try
            {
                _frozen.CopyPixels(new Int32Rect(sx, sy, 1, 1), px, 4, 0);
                Color c = Color.FromRgb(px[2], px[1], px[0]);
                _magnifierSwatch.Background = new SolidColorBrush(c);
                _magnifierColor.Text = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            }
            catch (ArgumentException)
            {
                // 坐标越界（窗口尺寸变化瞬间），下一次移动会恢复
            }
        }

        private void OnStageSizeChanged()
        {
            if (!_shotActive) return;
            Rect now = VideoRect();
            if (!_lastBounds.IsEmpty && now != _lastBounds) _crop.Rescale(_lastBounds, now);
            _lastBounds = now;
            RenderSelection();
        }
    }
}
