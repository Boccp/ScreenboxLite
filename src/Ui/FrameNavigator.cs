using System;
using System.Diagnostics;
using System.Windows.Threading;
using ScreenboxLite.Core;
using ScreenboxLite.Mpv;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// 帧导航器：把滚轮/方向键/拖动产生的密集请求合并，保证画面始终朝“最新目标”走、不排队滞后、不丢帧。
    ///  - 逐帧前进：frame-step N（极速播放 N 帧：只解码后续帧，无需回关键帧）
    ///  - 逐帧后退：frame-back-step / 精确 seek（必须从前一个关键帧解码，代价取决于 GOP 长度）
    ///  - 后退连击：在途的后退 seek 直接被新目标取代（mpv 会中止旧的解码），连滚多格只付一次代价
    ///  - 按秒跳转/时间轴：精确 seek；拖动中只到关键帧，松手再精确
    /// 完成判定基于 mpv 命令序号（CommandReply + PlaybackRestart / Pause 归位），不依赖浮点时间戳猜测。
    /// 所有方法只在 UI 线程调用。
    /// </summary>
    internal sealed class FrameNavigator
    {
        private enum Op { None, StepForward, StepBack, Seek }

        private const int MaxForwardPlaySteps = 60;   // 前进超过该帧数改用 seek

        private readonly MpvPlayer _player;
        private readonly DispatcherTimer _watchdog;
        private Op _inFlight = Op.None;
        private ulong _inFlightCmdId;
        private bool _inFlightFromSteps;              // 由逐帧请求产生（后退方向可被后续请求取代）
        private bool _inFlightExact = true;
        private double _expect = -1;                  // 在途操作的预期落点（秒）
        private readonly Stopwatch _opClock = new Stopwatch();

        private int _pendingFrames;                   // 待执行的逐帧步数（正=前进）
        private double _pendingSeek = -1;             // 待执行的目标时间（秒），-1 表示无
        private bool _pendingSeekExact;

        /// <summary>导航序列的最终目标时间（UI 可立即显示，不必等解码）。</summary>
        public double TargetTime { get; private set; }

        /// <summary>是否有尚未完成的导航。</summary>
        public bool Busy { get { return _inFlight != Op.None || _pendingFrames != 0 || _pendingSeek >= 0; } }

        /// <summary>一次导航序列全部完成（新帧已显示）。</summary>
        public event Action Settled;

        public FrameNavigator(MpvPlayer player, Dispatcher ui)
        {
            _player = player;
            _player.CommandReply += OnCommandReply;
            _player.PlaybackRestart += OnPlaybackRestart;
            _player.TimeChanged += OnTimeChanged;
            _player.PauseChanged += OnPauseChanged;
            // 安全兜底（4K60 10s 超长 GOP 冷回退硬解约需 1.25s，留足 5s 仅防损坏流卡死）
            _watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(5000), DispatcherPriority.Background, (s, e) =>
            {
                ((DispatcherTimer)s).Stop();
                if (_inFlight == Op.None) return;
                Log.Warn("导航操作超时兜底: " + _inFlight + " cmd=#" + _inFlightCmdId + " 预期 " + _expect);
                Finish();
            }, ui);
            _watchdog.Stop();
        }

        public void Reset()
        {
            _inFlight = Op.None;
            _inFlightCmdId = 0;
            _pendingFrames = 0;
            _pendingSeek = -1;
            _expect = -1;
            _watchdog.Stop();
            TargetTime = 0;
            _player.EndStepping();
        }

        /// <summary>逐帧（正=前进）。</summary>
        public void StepFrames(int frames)
        {
            if (frames == 0 || !_player.HasVideo) return;
            EnsurePaused();
            double fd = _player.FrameDuration;
            TargetTime = Clamp(BaseTime() + frames * fd);

            // 在途的后退 seek：直接以新目标取代（目标仍在旧落点之前时），避免两次回关键帧解码
            if (_inFlightFromSteps && (_inFlight == Op.StepBack || _inFlight == Op.Seek) && TargetTime < _expect + fd * 0.5)
            {
                _pendingFrames = 0;
                IssueSeek(TargetTime, true, true);
                return;
            }

            if (_pendingSeek >= 0)
            {
                _pendingSeek = TargetTime;
                _pendingSeekExact = true;
            }
            else
            {
                _pendingFrames += frames;
            }
            Pump();
        }

        /// <summary>相对跳转（秒），精确到帧。</summary>
        public void StepSeconds(double delta)
        {
            if (!_player.HasVideo) return;
            EnsurePaused();
            SeekTo(BaseTime() + delta, true);
        }

        /// <summary>绝对定位；exact=false 只到关键帧（拖动时最跟手）。</summary>
        public void SeekTo(double seconds, bool exact)
        {
            if (_player.Duration <= 0) return;
            TargetTime = Clamp(seconds);
            _pendingFrames = 0;
            _pendingSeek = TargetTime;
            _pendingSeekExact = exact;
            Pump();
        }

        /// <summary>当前“逻辑位置”：有在途/待执行操作时取目标时间，否则取 mpv 实际时间。</summary>
        public double BaseTime()
        {
            return Busy ? TargetTime : _player.GetDouble("time-pos", _player.TimePos);
        }

        private void EnsurePaused()
        {
            if (!_player.Paused) _player.SetPause(true);
        }

        private void Pump()
        {
            if (_inFlight != Op.None) return;

            if (_pendingSeek >= 0)
            {
                double t = _pendingSeek;
                bool exact = _pendingSeekExact;
                _pendingSeek = -1;
                IssueSeek(t, exact, false);
                return;
            }

            int n = _pendingFrames;
            if (n == 0) return;
            _pendingFrames = 0;
            double now = _player.GetDouble("time-pos", _player.TimePos);
            double fd = _player.FrameDuration;

            if (n < 0 && now <= fd * 0.25)
            {
                // 已在片头第一帧，无需再后退
                TargetTime = 0;
                return;
            }
            if (n > 0 && _player.EofReached)
            {
                return;
            }

            if (n > 0 && n <= MaxForwardPlaySteps)
            {
                ulong cid = _player.FrameStep(n);
                Begin(Op.StepForward, now + n * fd, true, true, cid);
            }
            else if (n == -1)
            {
                ulong cid = _player.FrameBackStep();
                Begin(Op.StepBack, now - fd, true, true, cid);
            }
            else
            {
                IssueSeek(now + n * fd, true, true);
            }
        }

        /// <summary>精确 seek 的目标取“目标帧 pts 前 1/4 帧”，保证落在目标帧本身而不是下一帧。</summary>
        private void IssueSeek(double target, bool exact, bool fromSteps)
        {
            double t = Clamp(target);
            ulong cid = _player.SeekAbsolute(exact ? Math.Max(0, t - _player.FrameDuration * 0.25) : t, exact);
            Begin(Op.Seek, t, fromSteps, exact, cid);
        }

        private void Begin(Op op, double expect, bool fromSteps, bool exact, ulong cmdId)
        {
            if (cmdId == 0)
            {
                _inFlight = Op.None;
                return;
            }
            if (_inFlight == Op.None) _opClock.Restart(); // 被取代时沿用首次发出的时间（即用户感知延迟）
            _inFlight = op;
            _inFlightCmdId = cmdId;
            _inFlightFromSteps = fromSteps;
            _inFlightExact = exact;
            _expect = Clamp(expect);
            _watchdog.Stop();
            _watchdog.Start();
        }

        private void OnCommandReply(ulong cid, int err)
        {
            if (_inFlight == Op.None || cid < _inFlightCmdId) return;
            if (err < 0)
            {
                Finish();
                return;
            }
            if (_inFlight == Op.StepForward) CheckStepForwardDone();
        }

        private void OnPlaybackRestart()
        {
            if (_inFlight == Op.None || _inFlight == Op.StepForward) return;
            // 仅接受 mpv 已确认收到最新命令之后的 PlaybackRestart，确定性过滤被取代的旧 seek 回报
            if (_player.LastAckedCmdId >= _inFlightCmdId) Finish();
        }

        private void OnTimeChanged()
        {
            if (_inFlight == Op.StepForward) CheckStepForwardDone();
        }

        private void OnPauseChanged()
        {
            if (_inFlight == Op.StepForward) CheckStepForwardDone();
        }

        private void CheckStepForwardDone()
        {
            if (_inFlight != Op.StepForward || _player.LastAckedCmdId < _inFlightCmdId) return;
            if (_player.IsPausedNow() || _player.EofReached) Finish();
        }

        private void Finish()
        {
            string name = _inFlight == Op.StepForward ? "nav.step+" : _inFlight == Op.StepBack ? "nav.step-1"
                        : (_inFlightFromSteps ? "nav.step-burst" : (_inFlightExact ? "nav.seek" : "nav.seek.kf"));
            Perf.Record(name, _opClock.Elapsed.TotalMilliseconds);
            _inFlight = Op.None;
            _inFlightCmdId = 0;
            _expect = -1;
            _watchdog.Stop();
            Pump();
            if (!Busy)
            {
                _player.EndStepping();
                TargetTime = _player.GetDouble("time-pos", _player.TimePos);
                var h = Settled;
                if (h != null) h();
            }
        }

        private double Clamp(double t)
        {
            double d = _player.Duration;
            if (t < 0) return 0;
            if (d > 0 && t > d) return d;
            return t;
        }
    }
}
