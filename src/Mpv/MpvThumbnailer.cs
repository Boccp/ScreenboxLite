using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenboxLite.Core;

namespace ScreenboxLite.Mpv
{
    /// <summary>
    /// 时间轴悬停缩略图：独立的第二个 mpv 实例（软件渲染到内存，不占用主画面的解码与呈现）。
    ///  - 打开视频后在后台均匀预取一批“锚点”缩略图，悬停时立即有图可显示；
    ///  - 悬停请求“最新者优先”，只到关键帧（快），结果按时间桶缓存；
    ///  - 工作线程独占该 mpv 句柄的事件，UI 线程只读缓存和接收 Ready 回调（需自行切回 UI 线程）。
    /// </summary>
    internal sealed class MpvThumbnailer : IDisposable
    {
        public const int ThumbWidth = 256;
        private const int AnchorCount = 64;
        private const int CacheLimit = 400;

        private IntPtr _h, _rctx;
        private Thread _worker;
        private volatile bool _disposed;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly AutoResetEvent _frameSignal = new AutoResetEvent(false);
        private MpvRenderUpdateFn _updateCb;

        private readonly object _gate = new object();
        private string _pendingOpen;
        private double _request = -1;
        private int _gen;

        // 以下仅工作线程写
        private double _duration;
        private int _thumbH;
        private IntPtr _buf, _bufAligned, _sizeParam, _strideParam, _formatParam;
        private readonly Queue<double> _anchors = new Queue<double>();

        // 缓存（加锁访问）
        private readonly Dictionary<long, BitmapSource> _cache = new Dictionary<long, BitmapSource>();
        private readonly LinkedList<long> _lru = new LinkedList<long>();
        private readonly SortedList<double, BitmapSource> _anchorThumbs = new SortedList<double, BitmapSource>();
        private double _bucket = 0.5;

        /// <summary>缩略图就绪（工作线程触发）。参数：代号、时间、图像。</summary>
        public event Action<int, double, BitmapSource> Ready;

        public int Generation { get { lock (_gate) return _gen; } }

        public void Open(string path)
        {
            lock (_gate)
            {
                _pendingOpen = path;
                _request = -1;
                _gen++;
                _cache.Clear();
                _lru.Clear();
                _anchorThumbs.Clear();
            }
            EnsureWorker();
            _wake.Set();
        }

        public void Request(double seconds)
        {
            lock (_gate) _request = seconds;
            _wake.Set();
        }

        /// <summary>取缓存中最合适的缩略图：同一时间桶优先，否则最近的锚点。</summary>
        public BitmapSource TryGet(double seconds, out bool exact)
        {
            lock (_gate)
            {
                BitmapSource b;
                if (_cache.TryGetValue(BucketOf(seconds), out b))
                {
                    exact = true;
                    return b;
                }
                exact = false;
                if (_anchorThumbs.Count == 0) return null;
                IList<double> keys = _anchorThumbs.Keys;
                int lo = 0, hi = keys.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (keys[mid] <= seconds) lo = mid; else hi = mid - 1;
                }
                return _anchorThumbs.Values[lo];
            }
        }

        private long BucketOf(double t)
        {
            return (long)Math.Floor(t / _bucket);
        }

        private void EnsureWorker()
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "mpv-thumbs", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }

        private void WorkerLoop()
        {
            try
            {
                if (!InitMpv()) return;
                while (!_disposed)
                {
                    _wake.WaitOne(_anchors.Count > 0 ? 0 : 200);
                    if (_disposed) break;

                    string open;
                    double req;
                    int gen;
                    lock (_gate)
                    {
                        open = _pendingOpen;
                        _pendingOpen = null;
                        req = _request;
                        _request = -1;
                        gen = _gen;
                    }
                    if (open != null)
                    {
                        OpenFile(open);
                        lock (_gate) { if (_request < 0 && req >= 0) _request = req; }
                        continue;
                    }
                    if (_duration <= 0) continue;
                    if (req >= 0)
                    {
                        Produce(req, gen, false);
                    }
                    else if (_anchors.Count > 0)
                    {
                        Produce(_anchors.Dequeue(), gen, true);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "缩略图线程异常退出");
            }
        }

        private bool InitMpv()
        {
            _h = MpvNative.mpv_create();
            if (_h == IntPtr.Zero) return false;
            string[,] opts =
            {
                { "config", "no" }, { "terminal", "no" }, { "load-scripts", "no" }, { "ytdl", "no" },
                { "vo", "libmpv" }, { "aid", "no" }, { "sid", "no" }, { "pause", "yes" },
                { "idle", "yes" }, { "keep-open", "yes" }, { "hr-seek", "no" },
                { "hwdec", "auto-copy-safe" }, { "vd-lavc-threads", "2" },
                { "vd-lavc-skiploopfilter", "all" }, { "vd-lavc-fast", "yes" },
                { "cache", "no" }, { "demuxer-readahead-secs", "0" }, { "sws-fast", "yes" }
            };
            for (int i = 0; i < opts.GetLength(0); i++)
            {
                MpvNative.mpv_set_option_string(_h, MpvNative.Utf8(opts[i, 0]), MpvNative.Utf8(opts[i, 1]));
            }
            int err = MpvNative.mpv_initialize(_h);
            if (err < 0)
            {
                Log.Warn("缩略图 mpv 初始化失败: " + MpvNative.ErrorString(err));
                return false;
            }

            IntPtr apiType = Marshal.StringToHGlobalAnsi("sw");
            var p = new[]
            {
                new MpvRenderParam(MpvRenderParamType.ApiType, apiType),
                new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
            };
            err = MpvNative.mpv_render_context_create(out _rctx, _h, p);
            Marshal.FreeHGlobal(apiType);
            if (err < 0)
            {
                Log.Warn("缩略图渲染上下文创建失败: " + MpvNative.ErrorString(err));
                return false;
            }
            _updateCb = ctx => _frameSignal.Set();
            MpvNative.mpv_render_context_set_update_callback(_rctx, _updateCb, IntPtr.Zero);
            _formatParam = Marshal.StringToHGlobalAnsi("bgr0");
            return true;
        }

        private void OpenFile(string path)
        {
            _duration = 0;
            _anchors.Clear();
            Command("loadfile", path, "replace");
            if (!WaitEvent(MpvEventId.FileLoaded, 5000)) return;

            _duration = GetDouble("duration");
            double w = GetDouble("dwidth"), h = GetDouble("dheight");
            if (_duration <= 0 || w <= 0 || h <= 0) return;
            int th = (int)Math.Round(ThumbWidth * h / w);
            th = Math.Max(16, Math.Min(ThumbWidth * 2, th & ~1));
            AllocBuffer(th);
            lock (_gate) _bucket = Math.Max(0.2, _duration / 800.0);

            for (int i = 0; i < AnchorCount; i++) _anchors.Enqueue((i + 0.5) * _duration / AnchorCount);
        }

        private void AllocBuffer(int h)
        {
            if (_buf != IntPtr.Zero && h == _thumbH) return;
            if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
            if (_sizeParam == IntPtr.Zero) _sizeParam = Marshal.AllocHGlobal(8);
            if (_strideParam == IntPtr.Zero) _strideParam = Marshal.AllocHGlobal(IntPtr.Size);
            _thumbH = h;
            int stride = ThumbWidth * 4; // 256*4=1024，满足 64 字节对齐
            _buf = Marshal.AllocHGlobal(stride * h + 64);
            _bufAligned = new IntPtr((_buf.ToInt64() + 63) & ~63L);
            Marshal.WriteInt32(_sizeParam, 0, ThumbWidth);
            Marshal.WriteInt32(_sizeParam, 4, h);
            Marshal.WriteIntPtr(_strideParam, new IntPtr(stride));
        }

        private void Produce(double t, int gen, bool isAnchor)
        {
            var sw = Stopwatch.StartNew();
            _frameSignal.Reset();
            Command("seek", t.ToString("0.###", CultureInfo.InvariantCulture), "absolute+keyframes");
            if (!WaitEvent(MpvEventId.PlaybackRestart, 3000)) return;

            BitmapSource bmp = null;
            var deadline = Stopwatch.StartNew();
            while (bmp == null && deadline.ElapsedMilliseconds < 600)
            {
                if (!_frameSignal.WaitOne(100)) continue;
                ulong flags = MpvNative.mpv_render_context_update(_rctx);
                if ((flags & 1) == 0) continue; // MPV_RENDER_UPDATE_FRAME
                bmp = Render();
            }
            if (bmp == null) return;
            Perf.Record(isAnchor ? "thumb.anchor" : "thumb.hover", sw.Elapsed.TotalMilliseconds);

            lock (_gate)
            {
                if (gen != _gen) return;
                long key = BucketOf(t);
                if (!_cache.ContainsKey(key))
                {
                    _lru.AddLast(key);
                    if (_lru.Count > CacheLimit)
                    {
                        _cache.Remove(_lru.First.Value);
                        _lru.RemoveFirst();
                    }
                }
                _cache[key] = bmp;
                if (isAnchor) _anchorThumbs[t] = bmp;
            }
            var h = Ready;
            if (h != null) h(gen, t, bmp);
        }

        private BitmapSource Render()
        {
            var p = new[]
            {
                new MpvRenderParam(MpvRenderParamType.SwSize, _sizeParam),
                new MpvRenderParam(MpvRenderParamType.SwFormat, _formatParam),
                new MpvRenderParam(MpvRenderParamType.SwStride, _strideParam),
                new MpvRenderParam(MpvRenderParamType.SwPointer, _bufAligned),
                new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
            };
            int err = MpvNative.mpv_render_context_render(_rctx, p);
            if (err < 0) return null;
            int stride = ThumbWidth * 4;
            var bmp = BitmapSource.Create(ThumbWidth, _thumbH, 96, 96, PixelFormats.Bgr32, null, _bufAligned, stride * _thumbH, stride);
            bmp.Freeze();
            return bmp;
        }

        private bool WaitEvent(MpvEventId id, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (!_disposed)
            {
                double left = (timeoutMs - sw.ElapsedMilliseconds) / 1000.0;
                if (left <= 0) return false;
                IntPtr evp = MpvNative.mpv_wait_event(_h, left);
                var ev = (MpvEvent)Marshal.PtrToStructure(evp, typeof(MpvEvent));
                if (ev.EventId == id) return true;
                if (ev.EventId == MpvEventId.Shutdown) return false;
                if (ev.EventId == MpvEventId.EndFile && id == MpvEventId.FileLoaded)
                {
                    var ef = (MpvEventEndFile)Marshal.PtrToStructure(ev.Data, typeof(MpvEventEndFile));
                    if (ef.Reason == 4) return false; // 打开失败
                }
                // 新请求到来时不必等旧 seek 完成：mpv 会自行合并
            }
            return false;
        }

        private void Command(params string[] args)
        {
            IntPtr[] a = MpvNative.AllocArgs(args);
            try { MpvNative.mpv_command(_h, a); }
            finally { MpvNative.FreeArgs(a); }
        }

        private double GetDouble(string name)
        {
            double v;
            return MpvNative.mpv_get_property(_h, MpvNative.Utf8(name), MpvFormat.Double, out v) >= 0 ? v : 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wake.Set();
            _frameSignal.Set();
            if (_h != IntPtr.Zero) MpvNative.mpv_wakeup(_h);
            if (_worker != null && !_worker.Join(3000))
            {
                Log.Warn("缩略图线程未及时退出，跳过释放以免崩溃");
                return;
            }
            if (_rctx != IntPtr.Zero) { MpvNative.mpv_render_context_free(_rctx); _rctx = IntPtr.Zero; }
            if (_h != IntPtr.Zero) { MpvNative.mpv_terminate_destroy(_h); _h = IntPtr.Zero; }
            if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
            if (_sizeParam != IntPtr.Zero) Marshal.FreeHGlobal(_sizeParam);
            if (_strideParam != IntPtr.Zero) Marshal.FreeHGlobal(_strideParam);
            if (_formatParam != IntPtr.Zero) Marshal.FreeHGlobal(_formatParam);
        }
    }
}
