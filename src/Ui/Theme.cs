using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// 现代暗色工作室视觉主题与纯代码自定义控件模板（零默认白色系统边框、零 Emoji 乱码）。
    /// </summary>
    internal static class Theme
    {
        // ---- 核心表面层级 ----
        public static readonly Brush WindowBg = Frozen(Color.FromRgb(10, 12, 16));
        public static readonly Brush StageBg = Frozen(Color.FromRgb(8, 10, 14));
        public static readonly Brush TopBarBg = Frozen(Color.FromRgb(19, 24, 32));
        public static readonly Brush BottomBarBg = Frozen(Color.FromRgb(18, 22, 30));
        public static readonly Brush ShotBarBg = Frozen(Color.FromRgb(14, 34, 44));
        public static readonly Brush ShotBarBorder = Frozen(Color.FromRgb(0, 172, 204));
        public static readonly Brush CapsuleBg = Frozen(Color.FromRgb(26, 32, 43));
        public static readonly Brush CapsuleBorder = Frozen(Color.FromRgb(42, 52, 68));
        public static readonly Brush InputBg = Frozen(Color.FromRgb(13, 17, 24));
        public static readonly Brush Divider = Frozen(Color.FromRgb(38, 47, 62));

        // ---- 强调色与语义状态色 ----
        public static readonly Brush Accent = Frozen(Color.FromRgb(0, 216, 246));
        public static readonly Brush AccentFill = Frozen(Color.FromRgb(0, 198, 235));
        public static readonly Brush AccentBtnBg = Frozen(Color.FromRgb(15, 56, 70));
        public static readonly Brush AccentBtnBorder = Frozen(Color.FromRgb(0, 176, 208));
        public static readonly Brush ActiveBadgeBg = Frozen(Color.FromRgb(0, 184, 217));
        public static readonly Brush ActiveBadgeFg = Frozen(Color.FromRgb(8, 18, 24));

        public static readonly Brush Track = Frozen(Color.FromRgb(45, 55, 72));
        public static readonly Brush TextMain = Frozen(Color.FromRgb(241, 245, 249));
        public static readonly Brush TextDim = Frozen(Color.FromRgb(148, 163, 184));
        public static readonly Brush TextFaint = Frozen(Color.FromRgb(100, 116, 139));

        public static readonly Brush ButtonBg = Frozen(Color.FromRgb(33, 41, 54));
        public static readonly Brush ButtonBorder = Frozen(Color.FromRgb(54, 66, 86));
        public static readonly Brush SubtleBtnBg = Frozen(Color.FromRgb(26, 32, 43));
        public static readonly Brush SubtleBtnBorder = Frozen(Color.FromRgb(42, 52, 68));

        public static readonly Brush LockedBg = Frozen(Color.FromRgb(64, 26, 35));
        public static readonly Brush LockedBorder = Frozen(Color.FromRgb(210, 74, 94));
        public static readonly Brush LockedText = Frozen(Color.FromRgb(255, 148, 162));

        public static readonly Brush DangerBg = Frozen(Color.FromRgb(66, 26, 34));
        public static readonly Brush DangerBorder = Frozen(Color.FromRgb(208, 72, 88));
        public static readonly Brush DangerText = Frozen(Color.FromRgb(255, 168, 178));

        public static readonly Brush SuccessBg = Frozen(Color.FromRgb(18, 62, 48));
        public static readonly Brush SuccessBorder = Frozen(Color.FromRgb(40, 208, 142));
        public static readonly Brush SuccessText = Frozen(Color.FromRgb(126, 255, 204));

        public static readonly Brush FlipActiveBg = Frozen(Color.FromRgb(68, 50, 16));
        public static readonly Brush FlipActiveBorder = Frozen(Color.FromRgb(245, 180, 45));
        public static readonly Brush FlipActiveText = Frozen(Color.FromRgb(255, 220, 120));

        public static readonly Brush Blueprint = Frozen(Color.FromRgb(255, 215, 0));
        public static readonly Brush BlueprintFill = Frozen(Color.FromArgb(28, 255, 215, 0));
        public static readonly Brush SelectionFill = Frozen(Color.FromArgb(35, 0, 229, 255));
        public static readonly Brush Reticle = Frozen(Color.FromRgb(255, 51, 102));
        public static readonly Brush PopupBg = Frozen(Color.FromArgb(250, 18, 22, 30));
        public static readonly Brush HoverMarker = Frozen(Color.FromRgb(255, 214, 70));

        public static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI, Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Consolas, Microsoft YaHei UI");

        // ---- 16x16 矢量几何图标（像素对齐，彻底告别字体缺字/乱码） ----
        public static readonly Geometry GeoPlay = Geo("M 4.2,2.4 L 13.8,8 L 4.2,13.6 Z");
        public static readonly Geometry GeoPause = Geo("M 3.6,2.5 H 6.8 V 13.5 H 3.6 Z M 9.2,2.5 H 12.4 V 13.5 H 9.2 Z");
        public static readonly Geometry GeoPrev = Geo("M 2.8,3 H 4.6 V 13 H 2.8 Z M 13.2,3 L 5.8,8 L 13.2,13 Z");
        public static readonly Geometry GeoNext = Geo("M 11.4,3 H 13.2 V 13 H 11.4 Z M 2.8,3 L 10.2,8 L 2.8,13 Z");
        public static readonly Geometry GeoStepBack = Geo("M 3,3.5 H 4.8 V 12.5 H 3 Z M 12.8,3.5 L 6.4,8 L 12.8,12.5 Z");
        public static readonly Geometry GeoStepFwd = Geo("M 11.2,3.5 H 13 V 12.5 H 11.2 Z M 3.2,3.5 L 9.6,8 L 3.2,12.5 Z");
        public static readonly Geometry GeoFolder = Geo("M 1.5,3.2 H 6.2 L 7.8,5 H 14.5 V 13 H 1.5 Z");
        public static readonly Geometry GeoCrop = Geo("M 4,1 H 5.8 V 10.2 H 15 V 12 H 4 Z M 1,4 H 12 V 15 H 10.2 V 5.8 H 1 Z");
        public static readonly Geometry GeoGallery = Geo("M 1.5,2.5 H 14.5 V 13.5 H 1.5 Z M 3.1,4.1 V 10.3 L 6.1,7.4 L 8.7,9.8 L 10.9,7.8 L 12.9,9.9 V 4.1 Z");
        public static readonly Geometry GeoLock = Geo("M 4.8,7 V 5 A 3.2,3.2 0 0 1 11.2,5 V 7 H 12.6 V 14 H 3.4 V 7 Z M 6.5,7 H 9.5 V 5 A 1.5,1.5 0 0 0 6.5,5 Z");
        public static readonly Geometry GeoUnlock = Geo("M 9.6,7 V 4.6 A 2.6,2.6 0 0 1 14.4,4.6 H 12.8 A 1,1 0 0 0 11.2,4.6 V 7 H 12.6 V 14 H 3.4 V 7 Z");
        public static readonly Geometry GeoVolumeMute = Geo("M 1.8,5.8 H 5 L 9,2.4 V 13.6 L 5,10.2 H 1.8 Z M 11,5.6 L 12.4,7 L 13.8,5.6 L 14.9,6.7 L 13.5,8.1 L 14.9,9.5 L 13.8,10.6 L 12.4,9.2 L 11,10.6 L 9.9,9.5 L 11.3,8.1 L 9.9,6.7 Z");
        public static readonly Geometry GeoVolumeLow = Geo("M 1.8,5.8 H 5 L 9,2.4 V 13.6 L 5,10.2 H 1.8 Z M 11.2,5.2 A 4,4 0 0 1 11.2,10.8 L 10.1,9.7 A 2.4,2.4 0 0 0 10.1,6.3 Z");
        public static readonly Geometry GeoVolumeHigh = Geo("M 1.8,5.8 H 5 L 9,2.4 V 13.6 L 5,10.2 H 1.8 Z M 10.9,5.2 A 3.8,3.8 0 0 1 10.9,10.8 L 9.9,9.8 A 2.3,2.3 0 0 0 9.9,6.2 Z M 12.7,3.2 A 6.6,6.6 0 0 1 12.7,12.8 L 11.6,11.7 A 5,5 0 0 0 11.6,4.3 Z");
        public static readonly Geometry GeoFullscreen = Geo("M 2,6.2 V 2 H 6.2 V 3.8 H 3.8 V 6.2 Z M 9.8,2 H 14 V 6.2 H 12.2 V 3.8 H 9.8 Z M 3.8,9.8 V 12.2 H 6.2 V 14 H 2 V 9.8 Z M 12.2,9.8 H 14 V 14 H 9.8 V 12.2 H 12.2 Z");
        public static readonly Geometry GeoExitFullscreen = Geo("M 2,4.4 H 4.4 V 2 H 6.2 V 6.2 H 2 Z M 9.8,2 H 11.6 V 4.4 H 14 V 6.2 H 9.8 Z M 2,9.8 H 6.2 V 14 H 4.4 V 11.6 H 2 Z M 9.8,9.8 H 14 V 11.6 H 11.6 V 14 H 9.8 Z");
        public static readonly Geometry GeoFlip = Geo("M 7.3,1.8 H 8.7 V 14.2 H 7.3 Z M 1.4,8 L 5.8,3.8 V 12.2 Z M 14.6,8 L 10.2,3.8 V 12.2 Z");
        public static readonly Geometry GeoRestore = Geo("M 4.6,5.4 H 10.4 A 3.4,3.4 0 0 1 10.4,12.2 H 6 V 10.5 H 10.4 A 1.7,1.7 0 0 0 10.4,7.1 H 4.6 L 6.8,9.3 L 5.6,10.5 L 1.5,6.3 L 5.6,2.1 L 6.8,3.3 Z");
        public static readonly Geometry GeoTrash = Geo("M 5.5,1.8 H 10.5 L 11.3,3 H 14 V 4.6 H 2 V 3 H 4.7 Z M 3.5,5.6 H 12.5 L 11.8,14.2 H 4.2 Z");
        public static readonly Geometry GeoClose = Geo("M 3.5,2.3 L 8,6.8 L 12.5,2.3 L 13.7,3.5 L 9.2,8 L 13.7,12.5 L 12.5,13.7 L 8,9.2 L 3.5,13.7 L 2.3,12.5 L 6.8,8 L 2.3,3.5 Z");
        public static readonly Geometry GeoChevronDown = Geo("M 3.2,5.8 L 8,10.6 L 12.8,5.8 L 11.6,4.6 L 8,8.2 L 4.4,4.6 Z");

        private static ControlTemplate _roundedButtonTemplate;
        private static ControlTemplate _darkTextBoxTemplate;
        private static ControlTemplate _darkComboTemplate;
        private static Style _darkComboItemStyle;
        private static Style _darkScrollBarStyle;

        public static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Geometry Geo(string data)
        {
            Geometry g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        public static Path MakeIcon(Geometry geo, Brush fill, double size = 13)
        {
            var p = new Path
            {
                Data = geo,
                Fill = fill,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true
            };
            return p;
        }

        public static StackPanel IconLabel(Geometry geo, string text, Brush iconBrush = null, double iconSize = 13, double fontSize = 12)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(MakeIcon(geo, iconBrush ?? TextMain, iconSize));
            if (!string.IsNullOrEmpty(text))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = fontSize,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            return sp;
        }

        public static Border MakeDivider(double height = 18, double marginX = 8)
        {
            return new Border
            {
                Width = 1,
                Height = height,
                Background = Divider,
                Margin = new Thickness(marginX, 0, marginX, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
        }

        public static Border MakeCapsule(Thickness padding)
        {
            return new Border
            {
                Background = CapsuleBg,
                BorderBrush = CapsuleBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = padding,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
        }

        public static TextBlock Label(string text, Brush fg, double size = 12)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = fg,
                FontFamily = UiFont,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // ======================= 自定义暗色圆角按钮模板 =======================

        public static ControlTemplate GetButtonTemplate()
        {
            if (_roundedButtonTemplate != null) return _roundedButtonTemplate;

            var root = new FrameworkElementFactory(typeof(Grid));

            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            root.AppendChild(bd);

            var hl = new FrameworkElementFactory(typeof(Border), "hl");
            hl.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            hl.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            hl.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            hl.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            hl.SetValue(UIElement.IsHitTestVisibleProperty, false);
            hl.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            root.AppendChild(hl);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter), "cp");
            cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
            root.AppendChild(cp);

            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = root };

            var hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(26, 255, 255, 255)), "hl"));
            hoverTrig.Setters.Add(new Setter(Border.BorderBrushProperty, Frozen(Color.FromArgb(72, 255, 255, 255)), "hl"));
            tpl.Triggers.Add(hoverTrig);

            var pressTrig = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressTrig.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(52, 0, 0, 0)), "hl"));
            pressTrig.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "hl"));
            tpl.Triggers.Add(pressTrig);

            var disableTrig = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disableTrig.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "bd"));
            disableTrig.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "cp"));
            tpl.Triggers.Add(disableTrig);

            tpl.Seal();
            _roundedButtonTemplate = tpl;
            return tpl;
        }

        public static Button MakeButton(string text, double width, double height = 30)
        {
            var b = new Button
            {
                Content = text,
                Height = height,
                Padding = new Thickness(width > 0 ? 6 : 10, 0, width > 0 ? 6 : 10, 0),
                FontFamily = UiFont,
                FontSize = 12,
                Background = ButtonBg,
                Foreground = TextMain,
                BorderBrush = ButtonBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = false,
                IsTabStop = false,
                Template = GetButtonTemplate()
            };
            if (width > 0) b.Width = width;
            return b;
        }

        public static Button MakeFlexButton(object content, double height = 30, double padX = 10, bool accent = false)
        {
            return new Button
            {
                Content = content,
                Height = height,
                Padding = new Thickness(padX, 0, padX, 0),
                FontFamily = UiFont,
                FontSize = 12,
                FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
                Background = accent ? AccentBtnBg : ButtonBg,
                Foreground = accent ? Accent : TextMain,
                BorderBrush = accent ? AccentBtnBorder : ButtonBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = false,
                IsTabStop = false,
                Template = GetButtonTemplate()
            };
        }

        public static Button MakeCapsuleButton(object content, double width, double height = 28)
        {
            var b = new Button
            {
                Content = content,
                Height = height,
                Padding = new Thickness(width > 0 ? 4 : 8, 0, width > 0 ? 4 : 8, 0),
                FontFamily = UiFont,
                FontSize = 12,
                Background = Brushes.Transparent,
                Foreground = TextMain,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = false,
                IsTabStop = false,
                Template = GetButtonTemplate()
            };
            if (width > 0) b.Width = width;
            return b;
        }

        // ======================= 自定义暗色输入框模板 =======================

        public static void StyleDarkTextBox(TextBox tb)
        {
            if (_darkTextBoxTemplate == null)
            {
                var bd = new FrameworkElementFactory(typeof(Border), "bd");
                bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
                bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
                bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

                var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
                host.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
                host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                host.SetValue(UIElement.FocusableProperty, false);
                bd.AppendChild(host);

                var tpl = new ControlTemplate(typeof(TextBox)) { VisualTree = bd };

                var hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrig.Setters.Add(new Setter(Border.BorderBrushProperty, Frozen(Color.FromRgb(86, 104, 130)), "bd"));
                tpl.Triggers.Add(hoverTrig);

                var focusTrig = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
                focusTrig.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "bd"));
                focusTrig.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(8, 14, 22)), "bd"));
                tpl.Triggers.Add(focusTrig);

                tpl.Seal();
                _darkTextBoxTemplate = tpl;
            }

            tb.Background = InputBg;
            tb.Foreground = TextMain;
            tb.CaretBrush = Accent;
            tb.SelectionBrush = Frozen(Color.FromArgb(120, 0, 200, 240));
            tb.BorderBrush = Frozen(Color.FromRgb(52, 65, 84));
            tb.BorderThickness = new Thickness(1);
            tb.Template = _darkTextBoxTemplate;
        }

        // ======================= 自定义暗色倍速下拉框模板（向上弹出、零白边、零抢焦点） =======================

        public static void StyleDarkComboBox(ComboBox cb)
        {
            if (_darkComboItemStyle == null)
            {
                var itemBd = new FrameworkElementFactory(typeof(Border), "ibd");
                itemBd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                itemBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                itemBd.SetValue(Border.PaddingProperty, new Thickness(10, 0, 10, 0));

                var itemCp = new FrameworkElementFactory(typeof(ContentPresenter));
                itemCp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                itemCp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                itemBd.AppendChild(itemCp);

                var itemTpl = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = itemBd };

                var hiTrig = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
                hiTrig.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(42, 54, 72)), "ibd"));
                itemTpl.Triggers.Add(hiTrig);

                var overTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                overTrig.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(42, 54, 72)), "ibd"));
                itemTpl.Triggers.Add(overTrig);

                var selTrig = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
                selTrig.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(16, 62, 78)), "ibd"));
                selTrig.Setters.Add(new Setter(Control.ForegroundProperty, Accent));
                selTrig.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
                itemTpl.Triggers.Add(selTrig);

                itemTpl.Seal();

                var st = new Style(typeof(ComboBoxItem));
                st.Setters.Add(new Setter(UIElement.FocusableProperty, false));
                st.Setters.Add(new Setter(FrameworkElement.HeightProperty, 28.0));
                st.Setters.Add(new Setter(Control.ForegroundProperty, TextMain));
                st.Setters.Add(new Setter(Control.FontFamilyProperty, Mono));
                st.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
                st.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
                st.Setters.Add(new Setter(Control.TemplateProperty, itemTpl));
                st.Seal();
                _darkComboItemStyle = st;
            }

            if (_darkComboTemplate == null)
            {
                // ToggleButton 子模板
                var togRoot = new FrameworkElementFactory(typeof(Border), "tbd");
                togRoot.SetValue(Border.BackgroundProperty, ButtonBg);
                togRoot.SetValue(Border.BorderBrushProperty, ButtonBorder);
                togRoot.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                togRoot.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
                togRoot.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

                var togTpl = new ControlTemplate(typeof(ToggleButton)) { VisualTree = togRoot };
                var togOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                togOver.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(44, 54, 70)), "tbd"));
                togOver.Setters.Add(new Setter(Border.BorderBrushProperty, Frozen(Color.FromRgb(82, 98, 122)), "tbd"));
                togTpl.Triggers.Add(togOver);
                var togChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                togChecked.Setters.Add(new Setter(Border.BackgroundProperty, AccentBtnBg, "tbd"));
                togChecked.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "tbd"));
                togTpl.Triggers.Add(togChecked);
                togTpl.Seal();

                // ComboBox 主树
                var grid = new FrameworkElementFactory(typeof(Grid));

                var toggle = new FrameworkElementFactory(typeof(ToggleButton), "ToggleButton");
                toggle.SetValue(Control.TemplateProperty, togTpl);
                toggle.SetValue(UIElement.FocusableProperty, false);
                toggle.SetValue(Control.IsTabStopProperty, false);
                toggle.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
                toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
                toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
                {
                    Mode = BindingMode.TwoWay,
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });
                grid.AppendChild(toggle);

                var content = new FrameworkElementFactory(typeof(ContentPresenter), "ContentSite");
                content.SetValue(UIElement.IsHitTestVisibleProperty, false);
                content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
                content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
                content.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 22, 0));
                content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                grid.AppendChild(content);

                var arrow = new FrameworkElementFactory(typeof(Path));
                arrow.SetValue(Path.DataProperty, GeoChevronDown);
                arrow.SetValue(Shape.FillProperty, TextDim);
                arrow.SetValue(FrameworkElement.WidthProperty, 10.0);
                arrow.SetValue(FrameworkElement.HeightProperty, 10.0);
                arrow.SetValue(Shape.StretchProperty, Stretch.Uniform);
                arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
                arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 8, 0));
                arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
                grid.AppendChild(arrow);

                var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
                popup.SetValue(Popup.AllowsTransparencyProperty, true);
                popup.SetValue(Popup.PlacementProperty, PlacementMode.Top);
                popup.SetValue(Popup.VerticalOffsetProperty, -4.0);
                popup.SetValue(UIElement.FocusableProperty, false);
                popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });

                var popBorder = new FrameworkElementFactory(typeof(Border));
                popBorder.SetValue(Border.BackgroundProperty, PopupBg);
                popBorder.SetValue(Border.BorderBrushProperty, AccentBtnBorder);
                popBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                popBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                popBorder.SetValue(Border.PaddingProperty, new Thickness(4));
                popBorder.SetValue(FrameworkElement.MinWidthProperty, 74.0);

                var itemsPanel = new FrameworkElementFactory(typeof(StackPanel));
                itemsPanel.SetValue(Panel.IsItemsHostProperty, true);
                itemsPanel.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Contained);
                popBorder.AppendChild(itemsPanel);
                popup.AppendChild(popBorder);
                grid.AppendChild(popup);

                var cbTpl = new ControlTemplate(typeof(ComboBox)) { VisualTree = grid };
                cbTpl.Seal();
                _darkComboTemplate = cbTpl;
            }

            cb.Foreground = TextMain;
            cb.FontFamily = Mono;
            cb.FontSize = 12;
            cb.Focusable = false;
            cb.IsTabStop = false;
            cb.ItemContainerStyle = _darkComboItemStyle;
            cb.Template = _darkComboTemplate;
        }

        // ======================= 暗色细滚动条样式（用于历史截图抽屉） =======================

        public static void StyleDarkScrollViewer(ScrollViewer sv)
        {
            if (_darkScrollBarStyle == null)
            {
                const string xaml =
                    "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
                    "  <Setter Property='Width' Value='10'/>" +
                    "  <Setter Property='Background' Value='Transparent'/>" +
                    "  <Setter Property='Template'>" +
                    "    <Setter.Value>" +
                    "      <ControlTemplate TargetType='ScrollBar'>" +
                    "        <Border Background='#550E121A' CornerRadius='4' Width='8' Margin='2,2,0,2'>" +
                    "          <Track x:Name='PART_Track' IsDirectionReversed='True'>" +
                    "            <Track.Thumb>" +
                    "              <Thumb>" +
                    "                <Thumb.Template>" +
                    "                  <ControlTemplate TargetType='Thumb'>" +
                    "                    <Border x:Name='tbd' Background='#3A485E' CornerRadius='4'/>" +
                    "                    <ControlTemplate.Triggers>" +
                    "                      <Trigger Property='IsMouseOver' Value='True'>" +
                    "                        <Setter TargetName='tbd' Property='Background' Value='#566C8C'/>" +
                    "                      </Trigger>" +
                    "                    </ControlTemplate.Triggers>" +
                    "                  </ControlTemplate>" +
                    "                </Thumb.Template>" +
                    "              </Thumb>" +
                    "            </Track.Thumb>" +
                    "          </Track>" +
                    "        </Border>" +
                    "      </ControlTemplate>" +
                    "    </Setter.Value>" +
                    "  </Setter>" +
                    "</Style>";
                var st = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                st.Seal();
                _darkScrollBarStyle = st;
            }

            sv.Resources[typeof(ScrollBar)] = _darkScrollBarStyle;
        }
    }
}
