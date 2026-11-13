using System;
using System.Windows;

namespace ScreenboxLite.Ui
{
    [Flags]
    internal enum CropHitZone
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        Inside = 16
    }

    internal enum CropMode
    {
        None,               // 瞄准中（可能已有落地的框）
        DraggingNew,        // 按住左键拖出新框
        TwoClickBlueprint,  // 第一次单击定起点，移动预览，第二次单击落地
        ResizingOrMoving    // 拖动已落地框的边/角/内部
    }

    internal enum CropBackResult
    {
        RevertedAdjust,     // 撤销了本次边框调整
        Cleared,            // 取消了选框，回到瞄准
        ExitMode            // 已无可撤销内容，应退出截图模式
    }

    /// <summary>
    /// 截图选框的纯逻辑状态机（不依赖任何控件，便于自检）。
    /// 坐标均为覆盖层坐标；bounds 为视频画面实际显示区域，所有结果都钳制在其中。
    /// </summary>
    internal sealed class CropSelection
    {
        public const double MinSize = 4.0;
        private const double ClickTolerance = 5.0;
        private const double EdgeTolerance = 8.0;

        public CropMode Mode { get; private set; }
        public bool HasSelection { get; private set; }
        public Rect Current { get; private set; }

        private Point _start;
        private Point _mouseDown;
        private Point _resizeStartMouse;
        private Rect _resizeStartRect;
        private CropHitZone _zone;

        /// <summary>是否正在显示金色虚线“蓝图”（尚未落地）。</summary>
        public bool IsBlueprint { get { return Mode == CropMode.DraggingNew || Mode == CropMode.TwoClickBlueprint; } }

        /// <summary>是否有可见的框（落地或蓝图）。</summary>
        public bool HasBox
        {
            get { return (HasSelection || IsBlueprint) && Current.Width >= MinSize && Current.Height >= MinSize; }
        }

        public bool ShowHandles { get { return HasSelection && !IsBlueprint && Mode != CropMode.DraggingNew; } }

        /// <summary>需要捕获鼠标（拖动/蓝图期间，鼠标移出窗口也要继续跟踪）。</summary>
        public bool WantsCapture { get { return Mode != CropMode.None; } }

        public void Reset()
        {
            Mode = CropMode.None;
            HasSelection = false;
            Current = Rect.Empty;
            _zone = CropHitZone.None;
        }

        /// <summary>左键按下。返回需要提示的消息（可为 null）。</summary>
        public string MouseDown(Point raw, Rect bounds)
        {
            Point p = ClampPoint(raw, bounds);
            _mouseDown = p;

            if (Mode == CropMode.TwoClickBlueprint)
            {
                Mode = CropMode.None;
                Rect r = Normalize(_start, p);
                if (r.Width >= MinSize && r.Height >= MinSize)
                {
                    Current = ClampRect(r, bounds);
                    HasSelection = true;
                    return "裁剪框已落地：可拖动边框微调，或按 ~ 键确认裁剪保存";
                }
                Reset();
                return "已返回上一步（取消当前框选），可重新选框或再按右键退出截图模式";
            }

            if (HasSelection && Mode == CropMode.None)
            {
                CropHitZone zone = HitTest(p);
                if (zone != CropHitZone.None)
                {
                    Mode = CropMode.ResizingOrMoving;
                    _zone = zone;
                    _resizeStartMouse = p;
                    _resizeStartRect = Current;
                    return null;
                }
            }

            Mode = CropMode.DraggingNew;
            HasSelection = false;
            _start = p;
            Current = new Rect(p, p);
            return null;
        }

        public void MouseMove(Point raw, bool leftPressed, Rect bounds)
        {
            Point p = ClampPoint(raw, bounds);
            if (Mode == CropMode.DraggingNew || Mode == CropMode.TwoClickBlueprint)
            {
                Current = Normalize(ClampPoint(_start, bounds), p);
                return;
            }
            if (Mode != CropMode.ResizingOrMoving || !leftPressed) return;

            double dx = p.X - _resizeStartMouse.X;
            double dy = p.Y - _resizeStartMouse.Y;
            Rect r = _resizeStartRect;
            if (_zone == CropHitZone.Inside)
            {
                double left = Clamp(r.Left + dx, bounds.Left, Math.Max(bounds.Left, bounds.Right - r.Width));
                double top = Clamp(r.Top + dy, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - r.Height));
                Current = new Rect(left, top, r.Width, r.Height);
                return;
            }
            double l = r.Left, rt = r.Right, t = r.Top, b = r.Bottom;
            if ((_zone & CropHitZone.Left) != 0) l = Clamp(r.Left + dx, bounds.Left, rt - MinSize);
            if ((_zone & CropHitZone.Right) != 0) rt = Clamp(r.Right + dx, l + MinSize, bounds.Right);
            if ((_zone & CropHitZone.Top) != 0) t = Clamp(r.Top + dy, bounds.Top, b - MinSize);
            if ((_zone & CropHitZone.Bottom) != 0) b = Clamp(r.Bottom + dy, t + MinSize, bounds.Bottom);
            Current = new Rect(l, t, Math.Max(MinSize, rt - l), Math.Max(MinSize, b - t));
        }

        public string MouseUp(Point raw, Rect bounds)
        {
            Point p = ClampPoint(raw, bounds);
            if (Mode == CropMode.ResizingOrMoving)
            {
                Mode = CropMode.None;
                _zone = CropHitZone.None;
                HasSelection = Current.Width >= MinSize && Current.Height >= MinSize;
                return null;
            }
            if (Mode != CropMode.DraggingNew) return null;

            double dist = Math.Sqrt(Math.Pow(p.X - _mouseDown.X, 2) + Math.Pow(p.Y - _mouseDown.Y, 2));
            if (dist < ClickTolerance)
            {
                Mode = CropMode.TwoClickBlueprint;
                HasSelection = false;
                Current = Normalize(_start, p);
                return "已定第一个起点：移动鼠标预览蓝图，再次单击左键落地裁剪框";
            }
            Mode = CropMode.None;
            if (Current.Width >= MinSize && Current.Height >= MinSize)
            {
                HasSelection = true;
                return "裁剪框已就绪：可拖动边框微调，按 ~ 键确认裁剪保存";
            }
            Reset();
            return null;
        }

        /// <summary>右键/Esc：逐级“上一步”。</summary>
        public CropBackResult Back()
        {
            if (Mode == CropMode.ResizingOrMoving)
            {
                Mode = CropMode.None;
                _zone = CropHitZone.None;
                Current = _resizeStartRect;
                HasSelection = Current.Width >= MinSize && Current.Height >= MinSize;
                if (HasSelection) return CropBackResult.RevertedAdjust;
                Reset();
                return CropBackResult.Cleared;
            }
            if (Mode != CropMode.None || HasSelection)
            {
                Reset();
                return CropBackResult.Cleared;
            }
            return CropBackResult.ExitMode;
        }

        /// <summary>按归一化矩形恢复上次的框（R 键）。</summary>
        public bool Restore(Rect normalized, Rect bounds)
        {
            var r = new Rect(
                bounds.X + normalized.X * bounds.Width,
                bounds.Y + normalized.Y * bounds.Height,
                normalized.Width * bounds.Width,
                normalized.Height * bounds.Height);
            r = ClampRect(r, bounds);
            if (r.Width < MinSize || r.Height < MinSize) return false;
            Mode = CropMode.None;
            _zone = CropHitZone.None;
            HasSelection = true;
            Current = r;
            return true;
        }

        /// <summary>当前框相对画面的归一化矩形（0..1）；无框时返回 null。</summary>
        public Rect? Normalized(Rect bounds)
        {
            if (!HasBox || bounds.Width <= 0 || bounds.Height <= 0) return null;
            Rect c = ClampRect(Current, bounds);
            double x = Clamp((c.X - bounds.X) / bounds.Width, 0, 1);
            double y = Clamp((c.Y - bounds.Y) / bounds.Height, 0, 1);
            double w = Clamp(c.Width / bounds.Width, 0.001, 1 - x);
            double h = Clamp(c.Height / bounds.Height, 0.001, 1 - y);
            return new Rect(x, y, w, h);
        }

        /// <summary>窗口尺寸变化后，按归一化坐标把框映射到新的画面区域。</summary>
        public void Rescale(Rect oldBounds, Rect newBounds)
        {
            if (Current.IsEmpty || oldBounds.Width <= 0 || oldBounds.Height <= 0) return;
            double sx = newBounds.Width / oldBounds.Width, sy = newBounds.Height / oldBounds.Height;
            Func<Point, Point> map = pt => new Point(newBounds.X + (pt.X - oldBounds.X) * sx, newBounds.Y + (pt.Y - oldBounds.Y) * sy);
            Current = new Rect(map(Current.TopLeft), map(Current.BottomRight));
            _start = map(_start);
        }

        public CropHitZone HitTest(Point pt)
        {
            if (!HasSelection || Current.Width < MinSize || Current.Height < MinSize) return CropHitZone.None;
            Rect r = Current;
            bool inY = pt.Y >= r.Top - EdgeTolerance && pt.Y <= r.Bottom + EdgeTolerance;
            bool inX = pt.X >= r.Left - EdgeTolerance && pt.X <= r.Right + EdgeTolerance;
            CropHitZone z = CropHitZone.None;
            if (Math.Abs(pt.X - r.Left) <= EdgeTolerance && inY) z |= CropHitZone.Left;
            else if (Math.Abs(pt.X - r.Right) <= EdgeTolerance && inY) z |= CropHitZone.Right;
            if (Math.Abs(pt.Y - r.Top) <= EdgeTolerance && inX) z |= CropHitZone.Top;
            else if (Math.Abs(pt.Y - r.Bottom) <= EdgeTolerance && inX) z |= CropHitZone.Bottom;
            if (z != CropHitZone.None) return z;
            return r.Contains(pt) ? CropHitZone.Inside : CropHitZone.None;
        }

        public static Point ClampPoint(Point p, Rect b)
        {
            return new Point(Clamp(p.X, b.Left, b.Right), Clamp(p.Y, b.Top, b.Bottom));
        }

        public static Rect ClampRect(Rect r, Rect b)
        {
            double x1 = Clamp(r.Left, b.Left, b.Right), y1 = Clamp(r.Top, b.Top, b.Bottom);
            double x2 = Clamp(r.Right, b.Left, b.Right), y2 = Clamp(r.Bottom, b.Top, b.Bottom);
            return new Rect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        private static Rect Normalize(Point a, Point b)
        {
            return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (hi < lo) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
