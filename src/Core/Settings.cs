using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;

namespace ScreenboxLite.Core
{
    internal static class AppPaths
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenboxLite");

        public static readonly string SettingsFile = Path.Combine(DataDir, "settings.cfg");

        public static string DefaultScreenshotDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenbox"); }
        }
    }

    /// <summary>
    /// 用户设置。文件格式与 v1 完全兼容（key=value 文本，%LocalAppData%\ScreenboxLite\settings.cfg）。
    /// </summary>
    internal sealed class Settings
    {
        public double Volume;            // 0..1
        public bool IsMuted = true;
        public bool VolumeZeroLocked = true;
        public Rect? LastCrop;           // 归一化 0..1
        public string SaveFolderPath = AppPaths.DefaultScreenshotDir;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return s;
                foreach (string raw in File.ReadAllLines(AppPaths.SettingsFile))
                {
                    int sep = raw.IndexOf('=');
                    if (sep <= 0) continue;
                    string key = raw.Substring(0, sep).Trim();
                    string val = raw.Substring(sep + 1).Trim();
                    double d;
                    bool b;
                    switch (key.ToLowerInvariant())
                    {
                        case "volume":
                            if (double.TryParse(val, NumberStyles.Float, Inv, out d)) s.Volume = Clamp01(d);
                            break;
                        case "ismuted":
                            if (bool.TryParse(val, out b)) s.IsMuted = b;
                            break;
                        case "volumezerolocked":
                            if (bool.TryParse(val, out b)) s.VolumeZeroLocked = b;
                            break;
                        case "lastcrop":
                            s.LastCrop = ParseRect(val);
                            break;
                        case "savefolderpath":
                            if (!string.IsNullOrWhiteSpace(val)) s.SaveFolderPath = val;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "读取设置失败，使用默认值");
            }
            if (s.VolumeZeroLocked)
            {
                s.Volume = 0;
                s.IsMuted = true;
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string crop = "";
                if (LastCrop.HasValue)
                {
                    Rect c = LastCrop.Value;
                    crop = string.Format(Inv, "{0:0.######},{1:0.######},{2:0.######},{3:0.######}", c.X, c.Y, c.Width, c.Height);
                }
                var lines = new List<string>
                {
                    "Volume=" + Volume.ToString("0.####", Inv),
                    "IsMuted=" + (IsMuted ? "true" : "false"),
                    "VolumeZeroLocked=" + (VolumeZeroLocked ? "true" : "false"),
                    "LastCrop=" + crop,
                    "SaveFolderPath=" + (SaveFolderPath ?? "")
                };
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(AppPaths.SettingsFile)) File.Replace(tmp, AppPaths.SettingsFile, null);
                else File.Move(tmp, AppPaths.SettingsFile);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "保存设置失败");
            }
        }

        private static Rect? ParseRect(string val)
        {
            string[] p = val.Split(',');
            double x, y, w, h;
            if (p.Length == 4 &&
                double.TryParse(p[0], NumberStyles.Float, Inv, out x) &&
                double.TryParse(p[1], NumberStyles.Float, Inv, out y) &&
                double.TryParse(p[2], NumberStyles.Float, Inv, out w) &&
                double.TryParse(p[3], NumberStyles.Float, Inv, out h) &&
                w > 0.001 && h > 0.001)
            {
                return new Rect(Clamp01(x), Clamp01(y), Math.Min(1, w), Math.Min(1, h));
            }
            return null;
        }

        private static double Clamp01(double v)
        {
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }
    }
}
