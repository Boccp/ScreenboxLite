using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ScreenboxLite.Core
{
    /// <summary>
    /// 同目录视频列表排序优先级：
    ///   1. 若从 DSH 素材库管理器打开：优先遵循素材库传入的播放列表 / DSH_GALLERY_SORT_SPEC；
    ///   2. 否则若该目录正在 Windows 资源管理器中打开：跟随资源管理器当前视图排序；
    ///   3. 最后兜底回退到自然文件名升序。
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

        private static string _dshStartupDir;
        private static List<string> _dshExplicitPlaylist;
        private static string _cachedDir;
        private static string _cachedSortSpec;

        /// <summary>记录启动时的初始视频上下文（用于绑定素材库传入的排序或显式列表）。</summary>
        public static void InitStartupContext(string startupFile)
        {
            _dshStartupDir = null;
            _dshExplicitPlaylist = null;

            if (!string.IsNullOrWhiteSpace(startupFile))
            {
                try
                {
                    string full = Path.GetFullPath(startupFile);
                    _dshStartupDir = NormalizeDir(Path.GetDirectoryName(full));
                }
                catch
                {
                }
            }

            string listFile = Environment.GetEnvironmentVariable("DSH_GALLERY_PLAYLIST_FILE");
            if (!string.IsNullOrWhiteSpace(listFile))
            {
                try
                {
                    string cleanPath = listFile.Trim();
                    if (File.Exists(cleanPath))
                    {
                        var list = new List<string>();
                        foreach (string raw in File.ReadAllLines(cleanPath, System.Text.Encoding.UTF8))
                        {
                            string p = (raw ?? "").Trim();
                            if (p.Length > 0 && File.Exists(p)) list.Add(Path.GetFullPath(p));
                        }
                        if (list.Count > 0) _dshExplicitPlaylist = list;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("读取素材库播放列表失败: " + ex.Message);
                }
            }
        }

        /// <summary>获取给定视频文件所属的上下集列表（优先使用素材库传入的显式列表或排序）。</summary>
        public static List<string> GetPlaylistForVideo(string fullVideoPath)
        {
            if (_dshExplicitPlaylist != null && _dshExplicitPlaylist.Count > 0 && !string.IsNullOrEmpty(fullVideoPath))
            {
                int idx = _dshExplicitPlaylist.FindIndex(p => string.Equals(p, fullVideoPath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    List<string> alive = _dshExplicitPlaylist.Where(File.Exists).ToList();
                    if (alive.Count > 0) return alive;
                }
            }

            string dir = Path.GetDirectoryName(fullVideoPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                return GetSortedFolderVideos(dir);
            }
            return new List<string> { fullVideoPath };
        }

        public static List<string> GetSortedFolderVideos(string dir)
        {
            List<FileInfo> files = new DirectoryInfo(dir).GetFiles().Where(f => VideoExtensions.Contains(f.Extension)).ToList();
            string normDir = NormalizeDir(dir);

            string spec = null;

            // 优先级 1：如果从 DSH 素材库管理器打开（且处于启动目录或未限定启动目录），优先使用素材库排序
            string dshEnv = Environment.GetEnvironmentVariable("DSH_GALLERY_SORT_SPEC");
            if (!string.IsNullOrWhiteSpace(dshEnv) &&
                (_dshStartupDir == null || string.Equals(normDir, _dshStartupDir, StringComparison.OrdinalIgnoreCase)))
            {
                spec = dshEnv.Trim();
            }

            // 优先级 2：未命中素材库排序时，尝试跟随当前已打开的 Windows 资源管理器窗口排序
            if (string.IsNullOrWhiteSpace(spec))
            {
                spec = TryDetectExplorerSortColumns(normDir);
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

            // 优先级 3：兜底使用自然文件名升序
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
