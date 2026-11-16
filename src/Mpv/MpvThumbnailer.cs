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
    /// 时间轴悬停缩略图：独立的第二个 mpv 实例（多线程软解渲染到内存，不占用主画面的硬解与呈现）。
    ///  - 打开视频后在后台快速预取一批关键帧锚点，悬停首帧 0ms 立即可见；
    ///  - 悬停请求“最新者优先”，使用 hr-seek-framedrop 精确到帧（0.08s 时间桶，每秒约 12.5 帧高密度预览）；
    ///  - 工作线程独占该 mpv 句柄的事件，UI 线程只读缓存和接收 Ready 回调（需自行切回 UI 线程）。
    /// </summary>
    internal sealed class MpvThumbnailer : IDisposable
    {
        public const int ThumbWidth = 256;
        private const int AnchorCount = 64;
        private const int CacheLimit = 600;
        private const double ExactBucketSeconds = 0.08;

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
        private bool _hasValidFrame;
        private double _lastRenderedPts = -999;
        private BitmapSource _lastRenderedBmp;
        private IntPtr _buf, _bufAligned, _sizeParam, _strideParam, _formatParam;
        private readonly Queue<double> _anchors = new Queue<double>();

        // 缓存（加锁访问）：_cache 仅存精确帧，_exactTimeline + _anchorThumbs 供未命中时就近瞬时显示
        private readonly Dictionary<long, BitmapSource> _cache = new Dictionary<long, BitmapSource>();
        private readonly LinkedList<long> _lru = new LinkedList<long>();
        private readonly SortedList<double, BitmapSource> _exactTimeline = new SortedList<double, BitmapSource>();
        private readonly SortedList<double, BitmapSource> _anchorThumbs = new SortedList<double, BitmapSource>();
        private double _bucket = ExactBucketSeconds;

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
                _exactTimeline.Clear();
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

        /// <summary>取缓存中最合适的缩略图：同一精确时间桶(0.08s)命中则 exact=true，否则返回距离最近的已解码帧/锚点。</summary>
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
                double distExact = double.MaxValue, distAnchor = double.MaxValue;
                BitmapSource bestExact = FindClosest(_exactTimeline, seconds, out distExact);
                BitmapSource bestAnchor = FindClosest(_anchorThumbs, seconds, out distAnchor);
                if (bestExact == null) return bestAnchor;
                if (bestAnchor == null) return bestExact;
                return distExact <= distAnchor ? bestExact : bestAnchor;
            }
        }

        private static BitmapSource FindClosest(SortedList<double, BitmapSource> list, double seconds, out double dist)
        {
            dist = double.MaxValue;
            int n = list.Count;
            if (n == 0) return null;
            IList<double> keys = list.Keys;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (keys[mid] <= seconds) lo = mid; else hi = mid - 1;
            }
            int bestIdx = lo;
            dist = Math.Abs(keys[lo] - seconds);
            if (lo + 1 < n)
            {
                double dNext = Math.Abs(keys[lo + 1] - seconds);
                if (dNext < dist)
                {
                    dist = dNext;
                    bestIdx = lo + 1;
                }
            }
            return list.Values[bestIdx];
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
                        OpenFile(open, gen);
                        lock (_gate) { if (_request < 0 && req >= 0) _request = req; }
                        continue;
                    }
                    if (_duration <= 0 || _thumbH <= 0) continue;
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
                { "idle", "yes" }, { "keep-open", "yes" },
                { "hr-seek", "yes" }, { "hr-seek-framedrop", "yes" },
                { "hwdec", "no" }, { "vd-lavc-threads", "8" },
                { "vd-lavc-skiploopfilter", "all" }, { "vd-lavc-fast", "yes" },
                { "cache", "yes" }, { "demuxer-max-bytes", "64MiB" }, { "demuxer-max-back-bytes", "64MiB" },
                { "sws-fast", "yes" }
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
            AllocBuffer(144); // 预分配默认缓冲区，供 loadfile/reconfig 期间随时消费 vo_libmpv 渲染请求，避免 VO 阻塞 200ms
            return true;
        }

        private void OpenFile(string path, int gen)
        {
            _duration = 0;
            _hasValidFrame = false;
            _lastRenderedPts = -999;
            _lastRenderedBmp = null;
            _anchors.Clear();

            // 清理上一文件的残留渲染标志与旧事件
            DrainPendingRender();
            DrainPendingEvents();

            Command("loadfile", path, "replace");
            if (!WaitEvent(MpvEventId.FileLoaded, 6000, gen)) return;

            // 必须等到新文件的 PlaybackRestart（首帧真正解码完成），此时 dwidth/dheight 才是新文件的真实尺寸（而非上一文件的残留值），
            // 且期间持续调用 DrainPendingRender 消费 reconfig 空帧，避免 vo_libmpv 等待超时阻塞 200ms+。
            var swWait = Stopwatch.StartNew();
            bool sawPlaybackRestart = false;
            while (!_disposed && swWait.ElapsedMilliseconds < 5000)
            {
                if (IsStale(gen)) return;
                DrainPendingRender();

                IntPtr evp = MpvNative.mpv_wait_event(_h, 0.015);
                if (evp != IntPtr.Zero)
                {
                    var ev = (MpvEvent)Marshal.PtrToStructure(evp, typeof(MpvEvent));
                    if (ev.EventId == MpvEventId.Shutdown) return;
                    if (ev.EventId == MpvEventId.EndFile)
                    {
                        var ef = (MpvEventEndFile)Marshal.PtrToStructure(ev.Data, typeof(MpvEventEndFile));
                        if (ef.Reason == 4) return;
                    }
                    if (ev.EventId == MpvEventId.PlaybackRestart)
                    {
                        sawPlaybackRestart = true;
                        break;
                    }
                }
            }

            double dur = GetDouble("duration");
            double w = ReadDimension("video-params/dw", "dwidth", "width");
            double h = ReadDimension("video-params/dh", "dheight", "height");
            if (dur <= 0 || w <= 0 || h <= 0)
            {
                Log.Warn("缩略图未能获取视频尺寸或时长: " + path + " (dur=" + dur + ", w=" + w + ", h=" + h + ")");
                return;
            }

            _duration = dur;
            int th = (int)Math.Round(ThumbWidth * h / w);
            th = Math.Max(16, Math.Min(ThumbWidth * 2, th & ~1));
            AllocBuffer(th);
            lock (_gate) _bucket = ExactBucketSeconds;

            // 若首帧在 AllocBuffer 调整新比例前已被 DrainPendingRender 消费过，直接从当前 _rctx 渲染新尺寸首帧即可
            if (sawPlaybackRestart)
            {
                DrainPendingRender();
                BitmapSource firstBmp = Render();
                if (firstBmp != null && BufferHasVisiblePixels())
                {
                    _hasValidFrame = true;
                    _lastRenderedPts = GetDouble("time-pos");
                    _lastRenderedBmp = firstBmp;
                    StoreFrame(0.0, gen, false, firstBmp);
                    lock (_gate) { if (gen == _gen) _anchorThumbs[0.0] = firstBmp; }
                }
            }

            for (int i = 0; i < AnchorCount; i++) _anchors.Enqueue((i + 0.5) * _duration / AnchorCount);
        }

        private double ReadDimension(string p1, string p2, string p3)
        {
            double v = GetDouble(p1);
            if (v > 0) return v;
            v = GetDouble(p2);
            if (v > 0) return v;
            return GetDouble(p3);
        }

        private bool IsStale(int gen)
        {
            lock (_gate) return _gen != gen || _pendingOpen != null;
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
            if (_thumbH <= 0 || _bufAligned == IntPtr.Zero || _duration <= 0) return;
            var sw = Stopwatch.StartNew();

            // 每次 seek 前先清掉未消费的渲染标志位与残留事件，确保边沿触发的 _updateCb 正常工作
            DrainPendingRender();
            _frameSignal.Reset();
            DrainPendingEvents();

            // 后台锚点用 keyframes 秒速铺底；用户悬停用 exact + hr-seek-framedrop 精确到 0.08s 帧级密度
            string seekMode = isAnchor ? "absolute+keyframes" : "absolute+exact";
            Command("seek", t.ToString("0.###", CultureInfo.InvariantCulture), seekMode);
            if (!WaitEvent(MpvEventId.PlaybackRestart, 3000, gen)) return;

            double landedPts = GetDouble("time-pos");
            BitmapSource bmp = null;
            if (isAnchor && _lastRenderedBmp != null && _lastRenderedPts >= 0 && Math.Abs(landedPts - _lastRenderedPts) < 0.005)
            {
                DrainPendingRender();
                bmp = _lastRenderedBmp;
            }
            else if (!isAnchor && _lastRenderedBmp != null && _lastRenderedPts >= 0 && Math.Abs(landedPts - _lastRenderedPts) < 0.002 && Math.Abs(t - landedPts) < 0.05)
            {
                DrainPendingRender();
                bmp = _lastRenderedBmp;
            }
            else
            {
                bmp = TryHarvestFrame(600, _hasValidFrame, gen);
                if (bmp != null)
                {
                    _lastRenderedPts = landedPts;
                    _lastRenderedBmp = bmp;
                }
            }
            if (bmp == null) return;
            Perf.Record(isAnchor ? "thumb.anchor" : "thumb.hover", sw.Elapsed.TotalMilliseconds);

            StoreFrame(t, gen, isAnchor, bmp);
        }

        private void StoreFrame(double t, int gen, bool isAnchor, BitmapSource bmp)
        {
            lock (_gate)
            {
                if (gen != _gen) return;
                if (isAnchor)
                {
                    // 关键帧锚点仅存入粗粒度兜底表，不标记精确时间桶，确保鼠标悬停时仍会升级为精确帧
                    _anchorThumbs[t] = bmp;
                }
                else
                {
                    long key = BucketOf(t);
                    if (!_cache.ContainsKey(key))
                    {
                        _lru.AddLast(key);
                        if (_lru.Count > CacheLimit)
                        {
                            long evictKey = _lru.First.Value;
                            _cache.Remove(evictKey);
                            _exactTimeline.Remove(evictKey * _bucket);
                            _lru.RemoveFirst();
                        }
                    }
                    _cache[key] = bmp;
                    _exactTimeline[key * _bucket] = bmp;
                }
            }
            var h = Ready;
            if (h != null) h(gen, t, bmp);
        }

        private void DrainPendingRender()
        {
            if (_rctx == IntPtr.Zero) return;
            ulong flags = MpvNative.mpv_render_context_update(_rctx);
            if ((flags & 1) != 0 && _thumbH > 0 && _bufAligned != IntPtr.Zero && _sizeParam != IntPtr.Zero)
            {
                RenderRawToBuffer();
            }
        }

        private void DrainPendingEvents()
        {
            if (_h == IntPtr.Zero) return;
            while (!_disposed)
            {
                IntPtr evp = MpvNative.mpv_wait_event(_h, 0);
                if (evp == IntPtr.Zero) break;
                var ev = (MpvEvent)Marshal.PtrToStructure(evp, typeof(MpvEvent));
                if (ev.EventId == MpvEventId.None || ev.EventId == MpvEventId.Shutdown) break;
            }
        }

        private bool BufferHasVisiblePixels()
        {
            if (_bufAligned == IntPtr.Zero || _thumbH <= 0) return false;
            int totalBytes = ThumbWidth * 4 * _thumbH;
            int step = Math.Max(16, (totalBytes / 256) & ~3);
            for (int offset = 0; offset + 2 < totalBytes; offset += step)
            {
                byte b = Marshal.ReadByte(_bufAligned, offset);
                byte g = Marshal.ReadByte(_bufAligned, offset + 1);
                byte r = Marshal.ReadByte(_bufAligned, offset + 2);
                if (r > 3 || g > 3 || b > 3) return true;
            }
            return false;
        }

        private BitmapSource TryHarvestFrame(int timeoutMs, bool allowSameKeyframeFallback, int gen)
        {
            var deadline = Stopwatch.StartNew();
            while (!_disposed)
            {
                if (IsStale(gen)) return null;

                ulong flags = MpvNative.mpv_render_context_update(_rctx);
                if ((flags & 1) != 0)
                {
                    BitmapSource bmp = Render();
                    if (bmp != null)
                    {
                        if (BufferHasVisiblePixels()) _hasValidFrame = true;
                        return bmp;
                    }
                }
                else if (allowSameKeyframeFallback && deadline.ElapsedMilliseconds >= 20)
                {
                    return Render();
                }

                long remain = timeoutMs - deadline.ElapsedMilliseconds;
                if (remain <= 0) break;
                _frameSignal.WaitOne((int)Math.Min(15, remain));
            }
            return allowSameKeyframeFallback ? Render() : null;
        }

        private int RenderRawToBuffer()
        {
            var p = new[]
            {
                new MpvRenderParam(MpvRenderParamType.SwSize, _sizeParam),
                new MpvRenderParam(MpvRenderParamType.SwFormat, _formatParam),
                new MpvRenderParam(MpvRenderParamType.SwStride, _strideParam),
                new MpvRenderParam(MpvRenderParamType.SwPointer, _bufAligned),
                new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
            };
            return MpvNative.mpv_render_context_render(_rctx, p);
        }

        private BitmapSource Render()
        {
            if (_thumbH <= 0 || _bufAligned == IntPtr.Zero || _sizeParam == IntPtr.Zero) return null;
            if (RenderRawToBuffer() < 0) return null;
            int stride = ThumbWidth * 4;
            var bmp = BitmapSource.Create(ThumbWidth, _thumbH, 96, 96, PixelFormats.Bgr32, null, _bufAligned, stride * _thumbH, stride);
            bmp.Freeze();
            return bmp;
        }

        private bool WaitEvent(MpvEventId id, int timeoutMs, int gen)
        {
            var sw = Stopwatch.StartNew();
            while (!_disposed)
            {
                if (IsStale(gen)) return false;
                // 在等待 FileLoaded / PlaybackRestart 期间必须持续消费 vo_libmpv 的中间刷新请求，否则 VO 会阻塞 200ms
                DrainPendingRender();
                double left = (timeoutMs - sw.ElapsedMilliseconds) / 1000.0;
                if (left <= 0) return false;
                double step = Math.Min(left, 0.015);
                IntPtr evp = MpvNative.mpv_wait_event(_h, step);
                var ev = (MpvEvent)Marshal.PtrToStructure(evp, typeof(MpvEvent));
                if (ev.EventId == id) return true;
                if (ev.EventId == MpvEventId.Shutdown) return false;
                if (ev.EventId == MpvEventId.EndFile)
                {
                    var ef = (MpvEventEndFile)Marshal.PtrToStructure(ev.Data, typeof(MpvEventEndFile));
                    if (ef.Reason == 4) return false; // 仅在真正打开/解码报错(Reason=4)时退出；忽略切集时旧文件的 Reason=2(STOP)
                }
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
