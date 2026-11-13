using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenboxLite.Core;

namespace ScreenboxLite.Mpv
{
    /// <summary>
    /// libmpv 播放器封装。
    /// - 视频直接渲染到宿主 HWND（d3d11 + 硬解，零拷贝），由 mpv 负责呈现节奏；
    /// - 事件在后台线程读取，统一投递到 UI 线程，对外只暴露 UI 线程上的状态与事件；
    /// - 所有 seek/step 命令异步发送，不阻塞 UI。
    /// </summary>
    internal sealed class MpvPlayer : IDisposable
    {
        private IntPtr _h;
        private readonly Dispatcher _ui;
        private Thread _eventThread;
        private volatile bool _disposed;
        private long _nextCmdId;

        // ---- UI 线程可读的状态 ----
        public double TimePos { get; private set; }
        public double Duration { get; private set; }
        public bool Paused { get; private set; }
        public double Fps { get; private set; }
        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }
        public bool EofReached { get; private set; }
        public string CurrentPath { get; private set; }
        public ulong LastAckedCmdId { get; private set; }
        public bool HasVideo { get { return VideoWidth > 0 && VideoHeight > 0; } }

        /// <summary>帧时长（秒），fps 未知时按 30fps 估计。</summary>
        public double FrameDuration { get { return Fps > 1 ? 1.0 / Fps : 1.0 / 30.0; } }

        public event Action FileLoaded;
        public event Action<int> EndFile;          // reason
        public event Action PlaybackRestart;       // seek 完成、新帧已就绪
        public event Action TimeChanged;
        public event Action PauseChanged;
        public event Action VideoChanged;          // 尺寸/帧率/时长
        public event Action VideoReconfig;         // VO 重新配置（窗口可能重建）
        public event Action<ulong, int> CommandReply; // (cmdId, error)

        private const ulong IdTime = 1, IdDuration = 2, IdPause = 3, IdWidth = 4, IdHeight = 5,
                            IdFps = 6, IdEof = 7, IdVfFps = 8;

        public MpvPlayer(Dispatcher ui)
        {
            _ui = ui;
        }

        public static string LibraryVersion()
        {
            ulong v = MpvNative.mpv_client_api_version();
            return (v >> 16) + "." + (v & 0xFFFF);
        }

        public void Initialize(IntPtr hostHwnd)
        {
            _h = MpvNative.mpv_create();
            if (_h == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败");

            var opts = new List<KeyValuePair<string, string>>
            {
                Opt("wid", hostHwnd.ToInt64().ToString(CultureInfo.InvariantCulture)),
                Opt("config", "no"),               // 不读取用户的 mpv.conf，行为可预期
                Opt("terminal", "no"),
                Opt("load-scripts", "no"),
                Opt("ytdl", "no"),
                Opt("osc", "no"),
                Opt("osd-level", "0"),
                Opt("input-default-bindings", "no"),
                Opt("input-vo-keyboard", "no"),    // 键盘一律由 WPF 处理（含输入法下的物理键）
                Opt("input-cursor", "no"),         // 鼠标一律由 WPF 处理
                Opt("cursor-autohide", "no"),
                Opt("force-window", "yes"),        // 窗口常驻，切换视频不重建
                Opt("idle", "yes"),
                Opt("keep-open", "yes"),           // 播完停在最后一帧
                Opt("vo", "gpu-next"),
                Opt("gpu-api", "d3d11"),
                Opt("hwdec", "auto-safe"),         // 硬解（含 AV1/HEVC），不再需要 ffmpeg 转码
                Opt("hr-seek", "yes"),             // 绝对定位一律精确到帧
                Opt("hr-seek-framedrop", "yes"),   // 实测：精确跳转快约 2 倍、4K 长 GOP 回退 p50 从 1180ms 降至 83ms，落点一致
                Opt("cache", "yes"),               // 本地文件也开 demuxer 缓存：回退/重复定位不再读盘
                Opt("demuxer-max-bytes", "256MiB"),
                Opt("demuxer-max-back-bytes", "256MiB"),
                Opt("screenshot-format", "png")
            };
            foreach (var kv in opts) SetOption(kv.Key, kv.Value);
            // 调优/对比实验用：SCREENBOX_MPV_OPTS="key=value;key2=value2"
            string extra = Environment.GetEnvironmentVariable("SCREENBOX_MPV_OPTS");
            if (!string.IsNullOrEmpty(extra))
            {
                foreach (string pair in extra.Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) SetOption(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim());
                }
            }

            int err = MpvNative.mpv_initialize(_h);
            if (err < 0) throw new InvalidOperationException("mpv_initialize 失败: " + MpvNative.ErrorString(err));

            MpvNative.mpv_request_log_messages(_h, MpvNative.Utf8("warn"));
            Observe(IdTime, "time-pos", MpvFormat.Double);
            Observe(IdDuration, "duration", MpvFormat.Double);
            Observe(IdPause, "pause", MpvFormat.Flag);
            Observe(IdWidth, "dwidth", MpvFormat.Int64);
            Observe(IdHeight, "dheight", MpvFormat.Int64);
            Observe(IdFps, "container-fps", MpvFormat.Double);
            Observe(IdVfFps, "estimated-vf-fps", MpvFormat.Double);
            Observe(IdEof, "eof-reached", MpvFormat.Flag);

            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
            _eventThread.Start();
            Log.Info("mpv 初始化完成，client API " + LibraryVersion());
        }

        private static KeyValuePair<string, string> Opt(string k, string v)
        {
            return new KeyValuePair<string, string>(k, v);
        }

        private void SetOption(string name, string value)
        {
            int err = MpvNative.mpv_set_option_string(_h, MpvNative.Utf8(name), MpvNative.Utf8(value));
            if (err < 0) Log.Warn("mpv 选项 " + name + "=" + value + " 无效: " + MpvNative.ErrorString(err));
        }

        private void Observe(ulong id, string name, MpvFormat fmt)
        {
            MpvNative.mpv_observe_property(_h, id, MpvNative.Utf8(name), fmt);
        }

        // ================= 命令 =================

        public void Load(string path)
        {
            CurrentPath = path;
            TimePos = 0;
            Duration = 0;
            EofReached = false;
            CommandAsync("loadfile", path, "replace");
        }

        public void SetPause(bool pause)
        {
            if (!pause) EndStepping();
            SetFlag("pause", pause);
            Paused = pause; // 立即反映到 UI，mpv 回报后再校正
        }

        public void SetSpeed(double speed) { SetDouble("speed", speed); }
        public void SetVolume(double volume01) { SetDouble("volume", Math.Round(volume01 * 100.0, 1)); }
        public void SetMute(bool mute) { SetFlag("mute", mute); }

        /// <summary>精确定位（exact=false 时只到关键帧，用于拖动预览）。</summary>
        public ulong SeekAbsolute(double seconds, bool exact)
        {
            return CommandAsync("seek", seconds.ToString("0.######", CultureInfo.InvariantCulture), exact ? "absolute+exact" : "absolute+keyframes");
        }

        public enum StepMode { Play, Seek, Turbo }

        /// <summary>
        /// 逐帧前进方式。实测（1080p30/GOP250）：Play≈33ms（等 1/fps 播放节拍）、Seek≈95ms（回关键帧）、
        /// Turbo≈0.6ms 且 100% 落在预期帧，故默认 Turbo。可用环境变量 SCREENBOX_STEP_MODE 覆盖以便对比。
        /// </summary>
        public static StepMode ForwardStepMode = StepMode.Turbo;
        private double _speedBeforeTurbo = -1;

        public ulong FrameStep(int frames)
        {
            string n = frames.ToString(CultureInfo.InvariantCulture);
            switch (ForwardStepMode)
            {
                case StepMode.Seek:
                    return CommandAsync("frame-step", n, "seek");
                case StepMode.Turbo:
                    // 暂停中逐帧：临时把播放速度调到极高，帧一解码完就呈现，不必等待 1/fps 的播放节拍
                    if (_speedBeforeTurbo < 0)
                    {
                        _speedBeforeTurbo = GetDouble("speed", 1.0);
                        SetDouble("speed", 100.0);
                    }
                    return CommandAsync("frame-step", n, "mute");
                default:
                    return CommandAsync("frame-step", n);
            }
        }

        /// <summary>逐帧序列结束（由导航器调用），恢复正常播放速度。</summary>
        public void EndStepping()
        {
            if (_speedBeforeTurbo >= 0) { SetDouble("speed", _speedBeforeTurbo); _speedBeforeTurbo = -1; }
        }

        public ulong FrameBackStep() { return CommandAsync("frame-back-step"); }

        /// <summary>
        /// 截取当前帧的原始像素（视频原始分辨率，无字幕/OSD）。线程安全，可在后台线程调用。
        /// </summary>
        public BitmapSource GrabFrame()
        {
            if (_h == IntPtr.Zero) return null;
            IntPtr[] args = MpvNative.AllocArgs(new[] { "screenshot-raw", "video", "bgr0" });
            try
            {
                MpvNode result;
                int err = MpvNative.mpv_command_ret(_h, args, out result);
                if (err < 0)
                {
                    Log.Warn("screenshot-raw 失败: " + MpvNative.ErrorString(err));
                    return null;
                }
                try
                {
                    return NodeToBitmap(result);
                }
                finally
                {
                    MpvNative.mpv_free_node_contents(ref result);
                }
            }
            finally
            {
                MpvNative.FreeArgs(args);
            }
        }

        private static BitmapSource NodeToBitmap(MpvNode node)
        {
            if (node.Format != MpvFormat.NodeMap || node.U == IntPtr.Zero) return null;
            var list = (MpvNodeList)Marshal.PtrToStructure(node.U, typeof(MpvNodeList));
            long w = 0, h = 0, stride = 0;
            IntPtr data = IntPtr.Zero;
            long size = 0;
            int nodeSize = Marshal.SizeOf(typeof(MpvNode));
            for (int i = 0; i < list.Num; i++)
            {
                string key = MpvNative.FromUtf8(Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size));
                IntPtr valPtr = new IntPtr(list.Values.ToInt64() + i * nodeSize);
                var val = (MpvNode)Marshal.PtrToStructure(valPtr, typeof(MpvNode));
                switch (key)
                {
                    case "w": w = val.U.ToInt64(); break;
                    case "h": h = val.U.ToInt64(); break;
                    case "stride": stride = val.U.ToInt64(); break;
                    case "data":
                        if (val.Format == MpvFormat.ByteArray && val.U != IntPtr.Zero)
                        {
                            var ba = (MpvByteArray)Marshal.PtrToStructure(val.U, typeof(MpvByteArray));
                            data = ba.Data;
                            size = (long)ba.Size.ToUInt64();
                        }
                        break;
                }
            }
            if (w <= 0 || h <= 0 || stride <= 0 || data == IntPtr.Zero || size < stride * h) return null;
            var bmp = BitmapSource.Create((int)w, (int)h, 96, 96, PixelFormats.Bgr32, null, data, (int)(stride * h), (int)stride);
            bmp.Freeze();
            return bmp;
        }

        public ulong CommandAsync(params string[] args)
        {
            if (_h == IntPtr.Zero) return 0;
            ulong id = (ulong)Interlocked.Increment(ref _nextCmdId);
            IntPtr[] a = MpvNative.AllocArgs(args);
            try
            {
                int err = MpvNative.mpv_command_async(_h, id, a);
                if (err < 0)
                {
                    Log.Warn("mpv 命令失败 " + string.Join(" ", args) + ": " + MpvNative.ErrorString(err));
                    return 0;
                }
                return id;
            }
            finally
            {
                MpvNative.FreeArgs(a); // mpv 在调用返回前已复制参数
            }
        }

        /// <summary>同步查询 mpv 当前是否处于暂停态（用于精确判定 frame-step 全部帧呈现完毕）。</summary>
        public bool IsPausedNow()
        {
            if (_h == IntPtr.Zero) return true;
            int v;
            return MpvNative.mpv_get_property(_h, MpvNative.Utf8("pause"), MpvFormat.Flag, out v) < 0 || v != 0;
        }

        public string GetString(string name)
        {
            IntPtr p = MpvNative.mpv_get_property_string(_h, MpvNative.Utf8(name));
            if (p == IntPtr.Zero) return null;
            try { return MpvNative.FromUtf8(p); }
            finally { MpvNative.mpv_free(p); }
        }

        public double GetDouble(string name, double fallback)
        {
            double v;
            return MpvNative.mpv_get_property(_h, MpvNative.Utf8(name), MpvFormat.Double, out v) >= 0 ? v : fallback;
        }

        private void SetFlag(string name, bool value)
        {
            if (_h == IntPtr.Zero) return;
            int v = value ? 1 : 0;
            MpvNative.mpv_set_property(_h, MpvNative.Utf8(name), MpvFormat.Flag, ref v);
        }

        private void SetDouble(string name, double value)
        {
            if (_h == IntPtr.Zero) return;
            MpvNative.mpv_set_property(_h, MpvNative.Utf8(name), MpvFormat.Double, ref value);
        }

        // ================= 事件循环（后台线程） =================

        private void EventLoop()
        {
            while (!_disposed)
            {
                IntPtr evPtr = MpvNative.mpv_wait_event(_h, -1);
                if (evPtr == IntPtr.Zero) continue;
                var ev = (MpvEvent)Marshal.PtrToStructure(evPtr, typeof(MpvEvent));
                try
                {
                    switch (ev.EventId)
                    {
                        case MpvEventId.Shutdown:
                            return;
                        case MpvEventId.None:
                            break;
                        case MpvEventId.PropertyChange:
                            HandleProperty(ev);
                            break;
                        case MpvEventId.CommandReply:
                            {
                                ulong cid = ev.ReplyUserdata;
                                int errCode = ev.Error;
                                if (errCode < 0) Log.Warn("mpv 异步命令错误 #" + cid + ": " + MpvNative.ErrorString(errCode));
                                Post(() =>
                                {
                                    if (cid > LastAckedCmdId) LastAckedCmdId = cid;
                                    var h = CommandReply;
                                    if (h != null) h(cid, errCode);
                                });
                            }
                            break;
                        case MpvEventId.FileLoaded:
                            Post(() => Raise(FileLoaded));
                            break;
                        case MpvEventId.EndFile:
                            {
                                var ef = (MpvEventEndFile)Marshal.PtrToStructure(ev.Data, typeof(MpvEventEndFile));
                                int reason = ef.Reason;
                                if (reason == 4) Log.Warn("播放出错: " + MpvNative.ErrorString(ef.Error));
                                Post(() => { var h = EndFile; if (h != null) h(reason); });
                            }
                            break;
                        case MpvEventId.PlaybackRestart:
                            Post(() => Raise(PlaybackRestart));
                            break;
                        case MpvEventId.VideoReconfig:
                            Post(() => Raise(VideoReconfig));
                            break;
                        case MpvEventId.LogMessage:
                            {
                                var lm = (MpvEventLogMessage)Marshal.PtrToStructure(ev.Data, typeof(MpvEventLogMessage));
                                Log.Warn("mpv[" + MpvNative.FromUtf8(lm.Prefix) + "] " + (MpvNative.FromUtf8(lm.Text) ?? "").TrimEnd());
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "处理 mpv 事件 " + ev.EventId);
                }
            }
        }

        private void HandleProperty(MpvEvent ev)
        {
            var p = (MpvEventProperty)Marshal.PtrToStructure(ev.Data, typeof(MpvEventProperty));
            bool has = p.Format != MpvFormat.None && p.Data != IntPtr.Zero;
            double d = 0;
            long l = 0;
            if (has)
            {
                if (p.Format == MpvFormat.Double) d = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p.Data));
                else if (p.Format == MpvFormat.Int64) l = Marshal.ReadInt64(p.Data);
                else if (p.Format == MpvFormat.Flag) l = Marshal.ReadInt32(p.Data);
            }
            switch (ev.ReplyUserdata)
            {
                case IdTime:
                    Post(() => { if (has) TimePos = d; Raise(TimeChanged); });
                    break;
                case IdDuration:
                    Post(() => { Duration = has ? d : 0; Raise(VideoChanged); });
                    break;
                case IdPause:
                    Post(() => { Paused = has && l != 0; Raise(PauseChanged); });
                    break;
                case IdWidth:
                    Post(() => { VideoWidth = has ? (int)l : 0; Raise(VideoChanged); });
                    break;
                case IdHeight:
                    Post(() => { VideoHeight = has ? (int)l : 0; Raise(VideoChanged); });
                    break;
                case IdFps:
                case IdVfFps:
                    if (has && d > 1 && d < 1000) Post(() => { Fps = d; });
                    break;
                case IdEof:
                    Post(() => { EofReached = has && l != 0; });
                    break;
            }
        }

        private void Post(Action a)
        {
            if (_disposed) return;
            _ui.BeginInvoke(DispatcherPriority.Send, a);
        }

        private static void Raise(Action a)
        {
            if (a != null) a();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IntPtr h = _h;
            if (h == IntPtr.Zero) return;
            // 先让事件线程收到 Shutdown 自然退出，再销毁句柄（不能在 wait_event 期间销毁）
            IntPtr[] a = MpvNative.AllocArgs(new[] { "quit" });
            try { MpvNative.mpv_command(h, a); }
            finally { MpvNative.FreeArgs(a); }
            if (_eventThread != null && !_eventThread.Join(3000)) Log.Warn("mpv 事件线程未在 3 秒内退出");
            _h = IntPtr.Zero;
            MpvNative.mpv_terminate_destroy(h);
        }
    }
}
