using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ScreenboxLite.Core
{
    /// <summary>
    /// 关键路径计时：记录每类操作的耗时样本，退出时把统计写入日志。
    /// 用法：Perf.Record("step.forward", ms) 或 using (Perf.Measure("grab")) { ... }
    /// </summary>
    internal static class Perf
    {
        private static readonly Dictionary<string, List<double>> Samples = new Dictionary<string, List<double>>();
        private static readonly object Gate = new object();

        public static void Record(string name, double ms)
        {
            lock (Gate)
            {
                List<double> list;
                if (!Samples.TryGetValue(name, out list))
                {
                    list = new List<double>();
                    Samples[name] = list;
                }
                if (list.Count < 100000) list.Add(ms);
            }
        }

        public static IDisposable Measure(string name)
        {
            return new Scope(name);
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("{0,-28}{1,8}{2,10}{3,10}{4,10}{5,10}", "操作", "次数", "平均ms", "p50", "p95", "最大"));
            lock (Gate)
            {
                foreach (var kv in Samples.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (kv.Value.Count == 0) continue;
                    double[] s = kv.Value.OrderBy(v => v).ToArray();
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-28}{1,8}{2,10:0.0}{3,10:0.0}{4,10:0.0}{5,10:0.0}",
                        kv.Key, s.Length, s.Average(), Pct(s, 0.5), Pct(s, 0.95), s[s.Length - 1]));
                }
            }
            return sb.ToString();
        }

        public static bool HasSamples
        {
            get { lock (Gate) { return Samples.Count > 0; } }
        }

        private static double Pct(double[] sorted, double p)
        {
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            public Scope(string name) { _name = name; }
            public void Dispose() { Record(_name, _sw.Elapsed.TotalMilliseconds); }
        }
    }
}
