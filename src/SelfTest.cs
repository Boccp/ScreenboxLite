using System;
using System.Collections.Generic;
using System.Windows;
using ScreenboxLite.Core;
using ScreenboxLite.Ui;

namespace ScreenboxLite
{
    /// <summary>无界面自检（--self-test）：选框状态机、时间解析、排序描述解析。返回 0 表示全部通过。</summary>
    internal static class SelfTest
    {
        private static readonly List<string> Failures = new List<string>();

        public static int Run()
        {
            Crop();
            TimeParsing();
            SortSpec();
            string summary = Failures.Count == 0 ? "自检通过" : "自检失败:\r\n  " + string.Join("\r\n  ", Failures.ToArray());
            Log.Info(summary);
            Console.WriteLine(summary);
            return Failures.Count == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string name)
        {
            if (!ok) Failures.Add(name);
        }

        private static void Crop()
        {
            var b = new Rect(100, 50, 800, 450);
            var c = new CropSelection();

            // 拖拽定框
            c.MouseDown(new Point(200, 100), b);
            c.MouseMove(new Point(400, 300), true, b);
            c.MouseUp(new Point(400, 300), b);
            Check(c.HasSelection && c.Current == new Rect(200, 100, 200, 200), "拖拽定框");

            // 越界钳制：拖到画面外
            c.Reset();
            c.MouseDown(new Point(50, 10), b);
            c.MouseMove(new Point(2000, 2000), true, b);
            c.MouseUp(new Point(2000, 2000), b);
            Check(c.Current == b, "选框钳制在画面内");

            // 两次单击蓝图
            c.Reset();
            c.MouseDown(new Point(300, 200), b);
            c.MouseUp(new Point(301, 201), b);
            Check(c.Mode == CropMode.TwoClickBlueprint && !c.HasSelection, "单击进入蓝图");
            c.MouseMove(new Point(500, 400), false, b);
            c.MouseDown(new Point(500, 400), b);
            Check(c.HasSelection && c.Current == new Rect(300, 200, 200, 200) && c.Mode == CropMode.None, "二次单击落地");

            // 拖动右下角调整、右键撤销调整
            c.MouseDown(new Point(500, 400), b);
            Check(c.Mode == CropMode.ResizingOrMoving, "命中右下角");
            c.MouseMove(new Point(600, 450), true, b);
            Check(c.Current == new Rect(300, 200, 300, 250), "调整尺寸");
            Check(c.Back() == CropBackResult.RevertedAdjust && c.Current == new Rect(300, 200, 200, 200), "右键撤销本次调整");

            // 内部平移不越界
            c.MouseDown(new Point(400, 300), b);
            c.MouseMove(new Point(2000, 300), true, b);
            c.MouseUp(new Point(2000, 300), b);
            Check(Math.Abs(c.Current.Right - b.Right) < 1e-9 && Math.Abs(c.Current.Width - 200) < 1e-9, "平移钳制");

            // 逐级上一步
            Check(c.Back() == CropBackResult.Cleared && !c.HasBox, "右键取消选框");
            Check(c.Back() == CropBackResult.ExitMode, "再右键退出");

            // 归一化与恢复往返
            c.MouseDown(new Point(300, 200), b);
            c.MouseMove(new Point(500, 350), true, b);
            c.MouseUp(new Point(500, 350), b);
            Rect? n = c.Normalized(b);
            var c2 = new CropSelection();
            var b2 = new Rect(0, 0, 1600, 900);
            Check(n.HasValue && c2.Restore(n.Value, b2) && Near(c2.Current, new Rect(400, 300, 400, 300)), "归一化恢复(R 键)");

            // 太小的框不落地
            c.Reset();
            c.MouseDown(new Point(300, 200), b);
            c.MouseMove(new Point(302, 260), true, b);
            c.MouseUp(new Point(302, 260), b);
            Check(!c.HasSelection, "过窄的框不落地");
        }

        private static bool Near(Rect a, Rect b)
        {
            return Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;
        }

        private static void TimeParsing()
        {
            double s;
            Check(MainWindow.TryParseTime("85.5", out s) && s == 85.5, "秒数");
            Check(MainWindow.TryParseTime("01:25", out s) && s == 85, "分:秒");
            Check(MainWindow.TryParseTime("00:01:25.500", out s) && s == 85.5, "时:分:秒.毫秒");
            Check(MainWindow.TryParseTime("1：02", out s) && s == 62, "全角冒号");
            Check(!MainWindow.TryParseTime("abc", out s), "非法输入");
            Check(!MainWindow.TryParseTime("", out s), "空输入");
        }

        private static void SortSpec()
        {
            string p;
            bool d;
            Playlist.ParseSortSpec("prop:-System.DateCreated;System.ItemNameDisplay;", out p, out d);
            Check(p == "System.DateCreated" && d, "降序创建时间");
            Playlist.ParseSortSpec("prop:System.Size;", out p, out d);
            Check(p == "System.Size" && !d, "升序大小");
            Playlist.ParseSortSpec(null, out p, out d);
            Check(p == "System.ItemNameDisplay" && !d, "默认名称");
        }
    }
}
