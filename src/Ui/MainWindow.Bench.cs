using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ScreenboxLite.Core;

namespace ScreenboxLite.Ui
{
    /// <summary>
    /// 自动化性能测试（--bench 视频路径 [结果文件]）。
    /// 打开视频后按固定脚本执行逐帧、回退、跳转、截帧、缩略图，统计每类操作“发出→新帧就绪”的耗时。
    /// </summary>
    internal sealed partial class MainWindow
    {
        private readonly Queue<Action> _benchSteps = new Queue<Action>();
        private string _benchOutput;
        private readonly Stopwatch _benchStepClock = new Stopwatch();
        private string _benchStepName;
        private DispatcherTimer _benchTimer;

        public void StartBench(string video, string output)
        {
            _benchOutput = output;
            var rnd = new Random(42);
            Loaded += (s, e) =>
            {
                LoadVideo(video);
                _player.FileLoaded += () =>
                {
                    if (_benchSteps.Count > 0 || _benchTimer != null) return;
                    // 先播放 1.5 秒让解码器与缓存进入稳态
                    _benchSteps.Enqueue(() => { _player.SetPause(true); _nav.SeekTo(Math.Min(10, _player.Duration * 0.3), true); BenchWait("warmup.seek"); });
                    for (int i = 0; i < 40; i++) _benchSteps.Enqueue(() => { BenchExpect(1); _nav.StepFrames(1); BenchWait("bench.step+1"); });
                    for (int i = 0; i < 40; i++) _benchSteps.Enqueue(() => { BenchExpect(-1); _nav.StepFrames(-1); BenchWait("bench.step-1"); });
                    for (int i = 0; i < 10; i++) _benchSteps.Enqueue(() => { BenchExpect(-8); for (int k = 0; k < 8; k++) _nav.StepFrames(-1); BenchWait("bench.wheel-8burst"); });
                    for (int i = 0; i < 10; i++) _benchSteps.Enqueue(() => { BenchExpect(8); for (int k = 0; k < 8; k++) _nav.StepFrames(1); BenchWait("bench.wheel+8burst"); });
                    for (int i = 0; i < 12; i++) _benchSteps.Enqueue(() => { _nav.SeekTo(rnd.NextDouble() * _player.Duration * 0.95, true); BenchWait("bench.seek.exact"); });
                    for (int i = 0; i < 8; i++) _benchSteps.Enqueue(() =>
                    {
                        var sw = Stopwatch.StartNew();
                        var bmp = _player.GrabFrame();
                        Perf.Record("bench.grab(" + (bmp == null ? "fail" : bmp.PixelWidth + "x" + bmp.PixelHeight) + ")", sw.Elapsed.TotalMilliseconds);
                        BenchNext();
                    });
                    _benchSteps.Enqueue(FinishBench);
                    _benchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (s2, e2) =>
                    {
                        ((DispatcherTimer)s2).Stop();
                        BenchNext();
                    }, Dispatcher);
                };
                _nav.Settled += () =>
                {
                    if (_benchStepName == null) return;
                    Perf.Record(_benchStepName, _benchStepClock.Elapsed.TotalMilliseconds);
                    if (_benchExpectFrames != 0)
                    {
                        double now = _player.GetDouble("time-pos", 0);
                        int got = (int)Math.Round((now - _benchStartPos) * _player.Fps);
                        _benchChecked++;
                        if (got != _benchExpectFrames)
                        {
                            _benchWrong++;
                            Log.Info(string.Format("BENCH 帧偏差: {0} 期望 {1} 实际 {2} (t={3:0.0000})", _benchStepName, _benchExpectFrames, got, now));
                        }
                        _benchExpectFrames = 0;
                    }
                    _benchStepName = null;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BenchNext));
                };
            };
        }

        private int _benchExpectFrames, _benchChecked, _benchWrong;
        private double _benchStartPos;

        private void BenchExpect(int frames)
        {
            _benchExpectFrames = frames;
            _benchStartPos = _player.GetDouble("time-pos", 0);
        }

        private void BenchWait(string name)
        {
            _benchStepName = name;
            _benchStepClock.Restart();
            if (!_nav.Busy)
            {
                // 无需等待（如已在目标处）
                _benchStepName = null;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BenchNext));
            }
        }

        private void BenchNext()
        {
            if (_benchSteps.Count > 0) _benchSteps.Dequeue()();
        }

        private void FinishBench()
        {
            string info = string.Format("视频: {0}\r\n分辨率: {1}x{2}  帧率: {3:0.###}  时长: {4:0.0}s  hwdec: {5}  vo: {6}\r\nlibmpv: {7}  逐帧方式: {8}\r\n帧精度: {9}/{10} 次逐帧落在预期帧\r\n\r\n",
                _player.CurrentPath, _player.VideoWidth, _player.VideoHeight, _player.Fps, _player.Duration,
                _player.GetString("hwdec-current"), _player.GetString("current-vo"), Mpv.MpvPlayer.LibraryVersion(),
                Mpv.MpvPlayer.ForwardStepMode, _benchChecked - _benchWrong, _benchChecked);
            string report = info + Perf.Report();
            Log.Info("BENCH\r\n" + report);
            try
            {
                if (!string.IsNullOrEmpty(_benchOutput)) File.WriteAllText(_benchOutput, report, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "写入 bench 结果失败");
            }
            Close();
        }
    }
}
