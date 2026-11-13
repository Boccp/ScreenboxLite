using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenboxLite.Ui
{
    /// <summary>统一的配色与控件样式（与 v1 外观一致）。</summary>
    internal static class Theme
    {
        public static readonly Brush WindowBg = Frozen(Color.FromRgb(18, 18, 18));
        public static readonly Brush TopBarBg = Frozen(Color.FromRgb(26, 26, 26));
        public static readonly Brush BottomBarBg = Frozen(Color.FromRgb(24, 24, 24));
        public static readonly Brush Divider = Frozen(Color.FromRgb(45, 45, 45));
        public static readonly Brush Accent = Frozen(Color.FromRgb(0, 229, 255));
        public static readonly Brush AccentFill = Frozen(Color.FromRgb(0, 200, 255));
        public static readonly Brush Track = Frozen(Color.FromRgb(65, 65, 65));
        public static readonly Brush TextMain = Frozen(Color.FromRgb(220, 220, 220));
        public static readonly Brush TextDim = Frozen(Color.FromRgb(170, 170, 170));
        public static readonly Brush TextFaint = Frozen(Color.FromRgb(110, 110, 110));
        public static readonly Brush ButtonBg = Frozen(Color.FromRgb(42, 42, 42));
        public static readonly Brush ButtonBorder = Frozen(Color.FromRgb(72, 72, 72));
        public static readonly Brush LockedBg = Frozen(Color.FromRgb(165, 45, 55));
        public static readonly Brush LockedBorder = Frozen(Color.FromRgb(255, 95, 105));
        public static readonly Brush LockedText = Frozen(Color.FromRgb(255, 120, 120));
        public static readonly Brush Blueprint = Frozen(Color.FromRgb(255, 215, 0));
        public static readonly Brush BlueprintFill = Frozen(Color.FromArgb(28, 255, 215, 0));
        public static readonly Brush SelectionFill = Frozen(Color.FromArgb(35, 0, 229, 255));
        public static readonly Brush Reticle = Frozen(Color.FromRgb(255, 51, 102));
        public static readonly Brush PopupBg = Frozen(Color.FromArgb(248, 20, 20, 20));
        public static readonly Brush HoverMarker = Frozen(Color.FromRgb(255, 220, 0));
        public static readonly FontFamily Mono = new FontFamily("Consolas");

        public static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Button MakeButton(string text, double width, double height = 30)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = height,
                Background = ButtonBg,
                Foreground = Brushes.White,
                BorderBrush = ButtonBorder,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false // 焦点始终留在主窗口，保证快捷键可用
            };
        }

        public static TextBlock Label(string text, Brush fg, double size = 12.5)
        {
            return new TextBlock { Text = text, Foreground = fg, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        }
    }
}
