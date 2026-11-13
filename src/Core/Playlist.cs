using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ScreenboxLite.Core
{
    /// <summary>
    /// 同目录视频列表：顺序与资源管理器当前视图一致（若该目录正在资源管理器中打开），
    /// 否则用素材库传入的 DSH_GALLERY_SORT_SPEC，最后回退到自然文件名升序。
    /// </summary>
    internal static class Playlist
    {
        public static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp",
            ".m2ts", ".mts", ".vob", ".ogv", ".rm", ".rmvb", ".asf", ".f4v", ".3g2", ".mxf", ".divx", ".dv"
        };

        public static string OpenDialogFilter
        {
            get
            {
                string pats = string.Join(";", VideoExtensions.Select(e => "*" + e).ToArray());
                return "视频文件|" + pats + "|所有文件|*.*";
            }
        }

        public static bool IsVideo(string path)
        {
            return VideoExtensions.Contains(Path.GetExtension(path) ?? "");
        }

        private static string _cachedDir;
        private static string _cachedSortSpec;

        public static List<string> GetSortedFolderVideos(string dir)
        {
            List<FileInfo> files = new DirectoryInfo(dir).GetFiles().Where(f => VideoExtensions.Contains(f.Extension)).ToList();
            string normDir = NormalizeDir(dir);

            string spec = TryDetectExplorerSortColumns(normDir);
            if (string.IsNullOrWhiteSpace(spec))
            {
                string env = Environment.GetEnvironmentVariable("DSH_GALLERY_SORT_SPEC");
                if (!string.IsNullOrWhiteSpace(env)) spec = env.Trim();
            }
            if (!string.IsNullOrWhiteSpace(spec))
            {
                _cachedDir = normDir;
                _cachedSortSpec = spec;
            }
            else if (string.Equals(_cachedDir, normDir, StringComparison.OrdinalIgnoreCase))
            {
                spec = _cachedSortSpec;
            }

            string prop;
            bool desc;
            ParseSortSpec(spec, out prop, out desc);
            return Order(files, prop, desc).Select(f => f.FullName).ToList();
        }

        /// <summary>解析 "prop:-System.DateCreated;System.ItemNameDisplay;" 形式的排序描述。</summary>
        public static void ParseSortSpec(string spec, out string propName, out bool descending)
        {
            propName = "System.ItemNameDisplay";
            descending = false;
            if (string.IsNullOrWhiteSpace(spec)) return;
            string s = spec.Trim();
            if (s.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            int semi = s.IndexOf(';');
            string primary = (semi >= 0 ? s.Substring(0, semi) : s).Trim();
            if (primary.Length == 0) return;
            if (primary[0] == '-' || primary[0] == '+')
            {
                descending = primary[0] == '-';
                primary = primary.Substring(1).Trim();
            }
            propName = primary;
        }

        private static IEnumerable<FileInfo> Order(List<FileInfo> files, string prop, bool desc)
        {
            Func<FileInfo, IComparable> key;
            if (Has(prop, "DateCreated")) key = f => f.CreationTimeUtc;
            else if (Has(prop, "DateModified") || Has(prop, "System.Date") || Has(prop, "DateAccessed")) key = f => f.LastWriteTimeUtc;
            else if (Has(prop, "Size")) key = f => f.Length;
            else if (Has(prop, "ItemType") || Has(prop, "FileExtension")) key = f => f.Extension.ToLowerInvariant();
            else key = null;

            if (key == null)
            {
                return desc ? files.OrderByDescending(f => f.Name, NaturalComparer.Instance)
                            : files.OrderBy(f => f.Name, NaturalComparer.Instance);
            }
            var primary = desc ? files.OrderByDescending(key) : files.OrderBy(key);
            return primary.ThenBy(f => f.Name, NaturalComparer.Instance);
        }

        private static bool Has(string s, string part)
        {
            return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (ArgumentException) { return path.Trim().TrimEnd('\\', '/'); }
        }

        /// <summary>通过 Shell.Application 读取资源管理器窗口当前的排序列（目录未打开则返回 null）。</summary>
        private static string TryDetectExplorerSortColumns(string normTarget)
        {
            if (string.IsNullOrEmpty(normTarget)) return null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object windows = Invoke(shell, "Windows", BindingFlags.InvokeMethod);
                if (windows == null) return null;
                int count = Convert.ToInt32(Invoke(windows, "Count", BindingFlags.GetProperty));
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object win = Invoke(windows, "Item", BindingFlags.InvokeMethod, i);
                        object doc = win == null ? null : Invoke(win, "Document", BindingFlags.GetProperty);
                        object folder = doc == null ? null : Invoke(doc, "Folder", BindingFlags.GetProperty);
                        object self = folder == null ? null : Invoke(folder, "Self", BindingFlags.GetProperty);
                        string path = self == null ? null : Invoke(self, "Path", BindingFlags.GetProperty) as string;
                        if (!string.Equals(NormalizeDir(path), normTarget, StringComparison.OrdinalIgnoreCase)) continue;
                        string cols = Invoke(doc, "SortColumns", BindingFlags.GetProperty) as string;
                        if (!string.IsNullOrWhiteSpace(cols)) return cols.Trim();
                    }
                    catch (Exception)
                    {
                        // 非资源管理器窗口（如 IE 控件）或窗口忙：跳过即可
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("读取资源管理器排序失败: " + ex.Message);
            }
            return null;
        }

        private static object Invoke(object target, string name, BindingFlags flags, params object[] args)
        {
            return target.GetType().InvokeMember(name, flags, null, target, args.Length == 0 ? null : args);
        }
    }

    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        public int Compare(string x, string y)
        {
            return StrCmpLogicalW(x ?? "", y ?? "");
        }
    }
}
