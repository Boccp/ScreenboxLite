using System;
using System.Collections.Generic;
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
using ScreenboxLite.Core;

namespace ScreenboxLite.Ui
{
    internal sealed class ShotHistoryItem
    {
        public readonly SaveSync Sync = new SaveSync();
        public string FilePath;
        public string TrashPath;
        public string FileName;
        public string MetaText;
        public BitmapSource Preview;
        public bool IsDeleted;

        // 原位状态切换控件引用（删除/恢复时不改变卡片槽位，零跳动）
        public Border CardBorder;
        public Image ThumbImage;
        public Border DeletedBadge;
        public TextBlock TitleText;
        public TextBlock SubText;
        public Button DeleteBtn;
        public Button RestoreBtn;
        public Button LocateBtn;
    }

    /// <summary>
    /// 底部「历史截图队列」抽屉面板：
    ///  - 替代遮挡底栏的自动悬浮弹窗；
    ///  - 大尺寸高清卡片视图（368×276），支持一键单点删除；
    ///  - 删除后文件立即移入会话临时回收区，原卡片位置保留「恢复误删」按钮，一键无损原位恢复。
    /// </summary>
    internal sealed partial class MainWindow
    {
        private readonly List<ShotHistoryItem> _historyItems = new List<ShotHistoryItem>();
        private readonly string _sessionTrashDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScreenboxLite_UndoTrash_" + Process.GetCurrentProcess().Id);

        private Button _historyBtn;
        private System.Windows.Shapes.Path _historyIconPath;
        private TextBlock _historyBtnText;
        private Border _historyCountBadge;
        private TextBlock _historyCountText;

        private Popup _historyPopup;
        private Border _historyRootBorder;
        private WrapPanel _historyWrapPanel;
        private ScrollViewer _historyScroll;
        private TextBlock _historyHeaderSummary;
        private StackPanel _historyEmptyState;
        private bool _historyHookedNoActivate;

        internal bool IsHistoryPopupOpen { get { return _historyPopup != null && _historyPopup.IsOpen; } }
        internal IList<ShotHistoryItem> HistoryItemsForTest { get { return _historyItems; } }

        internal int ActiveHistoryCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _historyItems.Count; i++)
                {
                    if (!_historyItems[i].IsDeleted) n++;
                }
                return n;
            }
        }

        private Button BuildHistoryTriggerButton()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _historyIconPath = Theme.MakeIcon(Theme.GeoGallery, Theme.TextDim, 13);
            row.Children.Add(_historyIconPath);

            _historyBtnText = new TextBlock
            {
                Text = "历史截图",
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_historyBtnText);

            _historyCountBadge = new Border
            {
                Background = Theme.CapsuleBg,
                BorderBrush = Theme.CapsuleBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinWidth = 20,
                Height = 17,
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _historyCountText = new TextBlock
            {
                Text = "0",
                FontFamily = Theme.Mono,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _historyCountBadge.Child = _historyCountText;
            row.Children.Add(_historyCountBadge);

            var btn = Theme.MakeFlexButton(row, 32, 10, false);
            btn.ToolTip = "展开 / 收起本次历史截图队列（大图检视、单点删除、原位恢复误删）";
            btn.Click += (s, e) => ToggleHistoryPopup();
            return btn;
        }

        private void BuildHistoryPopup()
        {
            _historyRootBorder = new Border
            {
                Width = 1140,
                Height = 600,
                Background = Theme.PopupBg,
                BorderBrush = Theme.AccentBtnBorder,
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 16)
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 顶部标题栏
            var header = new Grid { Margin = new Thickness(2, 0, 2, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(Theme.MakeIcon(Theme.GeoGallery, Theme.Accent, 15));
            titleStack.Children.Add(new TextBlock
            {
                Text = "历史截图队列",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextMain,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            _historyHeaderSummary = new TextBlock
            {
                Text = "（本次会话共 0 张）",
                FontSize = 12,
                Foreground = Theme.TextDim,
                Margin = new Thickness(10, 1, 12, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(_historyHeaderSummary);
            header.Children.Add(titleStack);

            var headerBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var openDirBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoFolder, "打开保存目录", Theme.TextDim, 12.5, 12), 30, 11, false);
            openDirBtn.Click += (s, e) => OpenSaveFolder();
            headerBtns.Children.Add(openDirBtn);

            var closeBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoClose, "收起队列 (Esc)", Theme.TextDim, 11, 12), 30, 11, false);
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.Click += (s, e) => CloseHistoryPopup();
            headerBtns.Children.Add(closeBtn);
            Grid.SetColumn(headerBtns, 1);
            header.Children.Add(headerBtns);

            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            // 主体内容区（大尺寸卡片网格 + 空状态提示）
            var bodyGrid = new Grid();
            var innerBorder = new Border
            {
                Background = Theme.Frozen(Color.FromRgb(11, 14, 20)),
                BorderBrush = Theme.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8)
            };

            _historyWrapPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            _historyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = _historyWrapPanel
            };
            Theme.StyleDarkScrollViewer(_historyScroll);
            innerBorder.Child = _historyScroll;
            bodyGrid.Children.Add(innerBorder);

            _historyEmptyState = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            _historyEmptyState.Children.Add(new TextBlock
            {
                Text = "本次会话还没有保存截图",
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextDim,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _historyEmptyState.Children.Add(new TextBlock
            {
                Text = "在播放界面按 [~] 键框选并保存截图后，会自动排入此处（支持大图检视、单点删除与原位恢复误删）",
                FontSize = 12,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            bodyGrid.Children.Add(_historyEmptyState);

            Grid.SetRow(bodyGrid, 1);
            layout.Children.Add(bodyGrid);

            _historyRootBorder.Child = layout;
            _historyPopup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Relative,
                PlacementTarget = _bottomBar,
                StaysOpen = true,
                Focusable = false,
                Child = _historyRootBorder
            };
            _historyPopup.Opened += (s, e) =>
            {
                EnsureHistoryPopupNoActivate();
                FocusMain();
            };
            _historyPopup.Closed += (s, e) => UpdateHistoryButtonUi();
        }

        private void EnsureHistoryPopupNoActivate()
        {
            if (_historyHookedNoActivate) return;
            var src = PresentationSource.FromVisual(_historyRootBorder) as HwndSource;
            if (src == null) return;
            _historyHookedNoActivate = true;
            src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == 0x0021 /* WM_MOUSEACTIVATE */)
                {
                    handled = true;
                    return new IntPtr(3 /* MA_NOACTIVATE */);
                }
                if (msg == 0x0100 || msg == 0x0104 || msg == 0x0101 || msg == 0x0105)
                {
                    if (ProcessRawKeyMessage(_mainHwnd, msg, wParam, lParam))
                        handled = true;
                }
                return IntPtr.Zero;
            });
        }

        internal void ToggleHistoryPopup()
        {
            if (_historyPopup == null) return;
            if (_historyPopup.IsOpen)
            {
                CloseHistoryPopup();
            }
            else
            {
                if (_shotActive) ExitScreenshotMode();
                RepositionHistoryPopup();
                _historyPopup.IsOpen = true;
                UpdateHistoryButtonUi();
                FocusMain();
            }
        }

        internal void CloseHistoryPopup()
        {
            if (_historyPopup != null && _historyPopup.IsOpen)
            {
                _historyPopup.IsOpen = false;
                UpdateHistoryButtonUi();
            }
        }

        internal void RepositionHistoryPopup()
        {
            if (_historyPopup == null || _historyRootBorder == null) return;
            double availW = Math.Max(740, ActualWidth - 36);
            double availH = Math.Max(400, ActualHeight - 166);
            double pw = Clamp(availW, 740, 1480);
            double ph = Clamp(availH, 400, 820);
            _historyRootBorder.Width = pw;
            _historyRootBorder.Height = ph;
            double barW = _bottomBar.ActualWidth > 0 ? _bottomBar.ActualWidth : ActualWidth;
            _historyPopup.HorizontalOffset = Math.Max(8, (barW - pw) / 2.0);
            _historyPopup.VerticalOffset = -ph - 6;
        }

        internal ShotHistoryItem CreateAndSaveHistoryItem(BitmapSource bmp, string saveFolder)
        {
            var item = new ShotHistoryItem
            {
                MetaText = string.Format("{0} × {1}  ·  {2:HH:mm:ss}", bmp.PixelWidth, bmp.PixelHeight, DateTime.Now),
                Preview = ScreenshotWriter.MakePreviewThumb(bmp, 640),
                IsDeleted = false
            };
            string path = ScreenshotWriter.Save(bmp, saveFolder, ex =>
                Dispatcher.BeginInvoke(new Action(() => ShowToast("截图写入失败: " + ex.Message))), item.Sync);
            item.FilePath = path;
            item.FileName = System.IO.Path.GetFileName(path);

            UIElement card = BuildHistoryCard(item);
            _historyItems.Insert(0, item);
            if (_historyWrapPanel != null)
            {
                _historyWrapPanel.Children.Insert(0, card);
                if (_historyScroll != null) _historyScroll.ScrollToTop();
            }
            RefreshHistoryCounters();
            return item;
        }

        private UIElement BuildHistoryCard(ShotHistoryItem item)
        {
            var card = new Border
            {
                Width = 368,
                Height = 276,
                Margin = new Thickness(7),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1)
            };
            item.CardBorder = card;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(198) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 大尺寸高清缩略图舞台
            var stageBorder = new Border
            {
                Background = Theme.Frozen(Color.FromRgb(8, 10, 15)),
                BorderBrush = Theme.Frozen(Color.FromArgb(55, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                ToolTip = "点击在资源管理器中定位此文件"
            };
            var stageGrid = new Grid();
            var img = new Image
            {
                Source = item.Preview,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            item.ThumbImage = img;
            stageGrid.Children.Add(img);

            var deletedBadge = new Border
            {
                Background = Theme.Frozen(Color.FromArgb(238, 24, 20, 16)),
                BorderBrush = Theme.FlipActiveBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            deletedBadge.Child = new TextBlock
            {
                Text = "已从磁盘移出（可点击右下角恢复误删）",
                Foreground = Theme.FlipActiveText,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            item.DeletedBadge = deletedBadge;
            stageGrid.Children.Add(deletedBadge);

            stageBorder.Child = stageGrid;
            stageBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (!item.IsDeleted) RevealScreenshotFile(item.FilePath);
                FocusMain();
            };
            Grid.SetRow(stageBorder, 0);
            grid.Children.Add(stageBorder);

            // 卡片底栏：文件名 + 单点删除 / 原位恢复误删按钮
            var footer = new Grid { Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var titleText = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var subText = new TextBlock
            {
                FontSize = 11,
                FontFamily = Theme.Mono,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            item.TitleText = titleText;
            item.SubText = subText;
            infoStack.Children.Add(titleText);
            infoStack.Children.Add(subText);
            footer.Children.Add(infoStack);

            var actionStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var locateBtn = Theme.MakeButton("定位", 48, 28);
            locateBtn.FontSize = 11.5;
            locateBtn.ToolTip = "在文件夹中选中该截图";
            locateBtn.Click += (s, e) =>
            {
                RevealScreenshotFile(item.FilePath);
                FocusMain();
            };
            item.LocateBtn = locateBtn;
            actionStack.Children.Add(locateBtn);

            var delBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoTrash, "删除", Theme.DangerText, 11.5, 11.5), 28, 9, false);
            delBtn.Margin = new Thickness(6, 0, 0, 0);
            delBtn.FontWeight = FontWeights.SemiBold;
            delBtn.Background = Theme.DangerBg;
            delBtn.Foreground = Theme.DangerText;
            delBtn.BorderBrush = Theme.DangerBorder;
            delBtn.ToolTip = "单点立即删除此截图（删除后原卡片位置可一键恢复误删）";
            delBtn.Click += (s, e) =>
            {
                DeleteHistoryItem(item);
                FocusMain();
            };
            item.DeleteBtn = delBtn;
            actionStack.Children.Add(delBtn);

            var restoreBtn = Theme.MakeFlexButton(Theme.IconLabel(Theme.GeoRestore, "恢复误删", Theme.SuccessText, 12, 11.5), 28, 10, false);
            restoreBtn.FontWeight = FontWeights.Bold;
            restoreBtn.Background = Theme.SuccessBg;
            restoreBtn.Foreground = Theme.SuccessText;
            restoreBtn.BorderBrush = Theme.SuccessBorder;
            restoreBtn.Visibility = Visibility.Collapsed;
            restoreBtn.ToolTip = "将刚才删除的截图无损恢复回原目录原文件名";
            restoreBtn.Click += (s, e) =>
            {
                RestoreHistoryItem(item);
                FocusMain();
            };
            item.RestoreBtn = restoreBtn;
            actionStack.Children.Add(restoreBtn);

            Grid.SetColumn(actionStack, 1);
            footer.Children.Add(actionStack);
            Grid.SetRow(footer, 1);
            grid.Children.Add(footer);

            card.Child = grid;
            ApplyHistoryCardVisualState(item);
            return card;
        }

        private void ApplyHistoryCardVisualState(ShotHistoryItem item)
        {
            if (item.CardBorder == null) return;
            if (!item.IsDeleted)
            {
                item.CardBorder.Background = Theme.CapsuleBg;
                item.CardBorder.BorderBrush = Theme.CapsuleBorder;
                item.ThumbImage.Opacity = 1.0;
                item.DeletedBadge.Visibility = Visibility.Collapsed;
                item.TitleText.Text = item.FileName;
                item.TitleText.Foreground = Theme.TextMain;
                item.SubText.Text = item.MetaText;
                item.SubText.Foreground = Theme.TextDim;
                item.LocateBtn.Visibility = Visibility.Visible;
                item.DeleteBtn.Visibility = Visibility.Visible;
                item.RestoreBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                item.CardBorder.Background = Theme.Frozen(Color.FromRgb(20, 20, 24));
                item.CardBorder.BorderBrush = Theme.Frozen(Color.FromRgb(120, 90, 40));
                item.ThumbImage.Opacity = 0.24;
                item.DeletedBadge.Visibility = Visibility.Visible;
                item.TitleText.Text = "已删除 · " + item.FileName;
                item.TitleText.Foreground = Theme.TextFaint;
                item.SubText.Text = "原文件已移出，点击右侧可原位恢复";
                item.SubText.Foreground = Theme.FlipActiveText;
                item.LocateBtn.Visibility = Visibility.Collapsed;
                item.DeleteBtn.Visibility = Visibility.Collapsed;
                item.RestoreBtn.Visibility = Visibility.Visible;
            }
        }

        internal bool DeleteHistoryItem(ShotHistoryItem item)
        {
            if (item == null || item.IsDeleted) return false;
            try
            {
                item.Sync.WaitCompleted(3000);
                lock (item.Sync.Gate)
                {
                    if (File.Exists(item.FilePath))
                    {
                        Directory.CreateDirectory(_sessionTrashDir);
                        string trashFile = System.IO.Path.Combine(_sessionTrashDir, Guid.NewGuid().ToString("N") + "_" + item.FileName);
                        File.Copy(item.FilePath, trashFile, true);
                        File.Delete(item.FilePath);
                        item.TrashPath = trashFile;
                    }
                    item.IsDeleted = true;
                }
                ApplyHistoryCardVisualState(item);
                RefreshHistoryCounters();
                ShowToast("已删除截图: " + item.FileName + "（可在历史队列原位恢复）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "删除截图失败 " + item.FilePath);
                ShowToast("删除失败: " + ex.Message);
                return false;
            }
        }

        internal bool RestoreHistoryItem(ShotHistoryItem item)
        {
            if (item == null || !item.IsDeleted) return false;
            try
            {
                lock (item.Sync.Gate)
                {
                    if (string.IsNullOrEmpty(item.TrashPath) || !File.Exists(item.TrashPath))
                    {
                        ShowToast("恢复数据不存在，无法恢复");
                        return false;
                    }
                    string targetDir = System.IO.Path.GetDirectoryName(item.FilePath);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                    File.Copy(item.TrashPath, item.FilePath, true);
                    File.Delete(item.TrashPath);
                    item.TrashPath = null;
                    item.IsDeleted = false;
                }
                ApplyHistoryCardVisualState(item);
                RefreshHistoryCounters();
                ShowToast("已恢复误删截图: " + item.FileName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "恢复截图失败 " + item.FilePath);
                ShowToast("恢复失败: " + ex.Message);
                return false;
            }
        }

        private void RevealScreenshotFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ShowToast("该截图文件不存在或已被外部移除");
                return;
            }
            try
            {
                CloseHistoryPopup();
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                ShowToast("定位文件失败: " + ex.Message);
            }
        }

        private void RefreshHistoryCounters()
        {
            int active = ActiveHistoryCount;
            int deleted = _historyItems.Count - active;
            if (_historyEmptyState != null)
            {
                _historyEmptyState.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_historyHeaderSummary != null)
            {
                _historyHeaderSummary.Text = deleted > 0
                    ? string.Format("（有效 {0} 张 · 待恢复 {1} 张  |  目录: {2}）", active, deleted, _settings.SaveFolderPath)
                    : string.Format("（本次共 {0} 张  |  目录: {1}）", active, _settings.SaveFolderPath);
            }
            UpdateHistoryButtonUi();
        }

        private void UpdateHistoryButtonUi()
        {
            if (_historyBtn == null) return;
            int active = ActiveHistoryCount;
            bool open = IsHistoryPopupOpen;

            if (_historyCountText != null) _historyCountText.Text = active.ToString();

            if (open)
            {
                _historyBtn.Background = Theme.AccentBtnBg;
                _historyBtn.BorderBrush = Theme.Accent;
                _historyBtn.Foreground = Theme.Accent;
                if (_historyIconPath != null) _historyIconPath.Fill = Theme.Accent;
                if (_historyCountBadge != null)
                {
                    _historyCountBadge.Background = Theme.ActiveBadgeBg;
                    _historyCountBadge.BorderBrush = Theme.Accent;
                }
                if (_historyCountText != null) _historyCountText.Foreground = Theme.ActiveBadgeFg;
            }
            else if (active > 0)
            {
                _historyBtn.Background = Theme.Frozen(Color.FromRgb(20, 42, 54));
                _historyBtn.BorderBrush = Theme.AccentBtnBorder;
                _historyBtn.Foreground = Theme.TextMain;
                if (_historyIconPath != null) _historyIconPath.Fill = Theme.Accent;
                if (_historyCountBadge != null)
                {
                    _historyCountBadge.Background = Theme.ActiveBadgeBg;
                    _historyCountBadge.BorderBrush = Theme.Accent;
                }
                if (_historyCountText != null) _historyCountText.Foreground = Theme.ActiveBadgeFg;
            }
            else
            {
                _historyBtn.Background = Theme.ButtonBg;
                _historyBtn.BorderBrush = Theme.ButtonBorder;
                _historyBtn.Foreground = Theme.TextMain;
                if (_historyIconPath != null) _historyIconPath.Fill = Theme.TextDim;
                if (_historyCountBadge != null)
                {
                    _historyCountBadge.Background = Theme.InputBg;
                    _historyCountBadge.BorderBrush = Theme.CapsuleBorder;
                }
                if (_historyCountText != null) _historyCountText.Foreground = Theme.TextDim;
            }
        }

        private void CleanupHistoryTrash()
        {
            try
            {
                if (Directory.Exists(_sessionTrashDir))
                    Directory.Delete(_sessionTrashDir, true);
            }
            catch
            {
            }
        }
    }
}
