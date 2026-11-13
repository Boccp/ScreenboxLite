using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ScreenboxLite.Core
{
    /// <summary>
    /// 极简异步日志：写入 %LocalAppData%\ScreenboxLite\logs\yyyyMMdd.log，保留 7 天。
    /// 替代原来的空 catch{}——出错不弹窗，但留下可追查的记录。
    /// </summary>
    internal static class Log
    {
        private static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(4096);
        private static readonly string Dir = Path.Combine(AppPaths.DataDir, "logs");
        private static Thread _writer;
        public static bool Verbose = false;

        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                foreach (string f in Directory.GetFiles(Dir, "*.log"))
                {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
                }
            }
            catch
            {
                // 日志目录不可用时静默降级（日志本身不能成为故障源）
            }
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "LogWriter", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }

        public static void Info(string msg) { Enqueue("INFO", msg); }
        public static void Debug(string msg) { if (Verbose) Enqueue("DEBUG", msg); }
        public static void Warn(string msg) { Enqueue("WARN", msg); }

        public static void Warn(Exception ex, string context)
        {
            Enqueue("WARN", context + ": " + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message));
        }

        public static void Error(Exception ex, string context)
        {
            Enqueue("ERROR", context + ": " + (ex == null ? "" : ex.ToString()));
        }

        private static void Enqueue(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + level + "] " + msg;
            Queue.TryAdd(line);
        }

        public static void Flush()
        {
            for (int i = 0; i < 50 && Queue.Count > 0; i++) Thread.Sleep(10);
        }

        private static void WriterLoop()
        {
            foreach (string line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    string file = Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    var sb = new StringBuilder(line).Append("\r\n");
                    string next;
                    while (sb.Length < 65536 && Queue.TryTake(out next)) sb.Append(next).Append("\r\n");
                    File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // 写日志失败只能丢弃
                }
            }
        }
    }
}
