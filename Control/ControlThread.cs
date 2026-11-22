using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Threading;
using DarraEtherCAT_Master;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 2 kHz 周期执行器。内核 PDO 线程只响门铃，本线程才是真正跑控制律、写 0x607A / 0x6040 的地方。
    /// 五条规矩（照 HCFA_X3E_QuarterTurn/CyclePdoWorker.cs）：
    /// ① Wait 超时必须 0；② 控制线程入口一次性设 Highest 并绑 housekeeping 核；③ 漏拍 hold 不补跳；
    /// ④ 拍内算完即写；⑤ 回调里禁止日志。软实时初始化失败只记日志，不退出控制线程。
    /// 停机先排空再释放，绝不 Join 强杀、绝不释放仍在飞的拍。
    /// <para>
    /// 另加一条：0 超时轮询不允许变成无限满速空转。内核周期一直不就绪时按连续未就绪拍数逐档退避，
    /// 2 秒打警告并降成每 1 毫秒检查一次，10 秒判致命，停使能请求后退出循环让上层能正常 Disconnect。
    /// 未就绪期间一拍都不跑，所以不会写 0x607A / 0x6040。
    /// </para>
    /// </summary>
    internal sealed class ControlThread
    {
        // 超时必须 0：Wait 1ms 会把 500µs 的内核拍合并成 ~2ms，伺服跟丢 SYNC。
        private const uint WaitTimeoutMilliseconds = 0;
        private const int MinCycleNanoseconds = 125000;

        // 用户态控制线程默认固定到 housekeeping 核；30/31 是内核 RT lane，严禁经环境变量占用。
        private const int DefaultRtCpuIndex = 27;
        private const int FirstKernelRtLaneCpuIndex = 30;
        private const int SecondKernelRtLaneCpuIndex = 31;

        // ---- 连续未就绪的自救档位（只降 CPU 占用，不停止轮询）----
        // 拍数档：到档就换更省的等待指令（2 kHz 下 200 拍 ≈ 100 ms，1000 拍 ≈ 500 ms）。
        private const int StallYieldAfterCycles = 200;
        private const int StallSleepZeroAfterCycles = 1000;
        // 时间档：连续 2 秒仍不就绪就降成每 1 毫秒检查一次；连续 10 秒判致命，停循环。
        private const double StallWarnSeconds = 2.0;
        private const double StallFatalSeconds = 10.0;
        private const int StallDegradedSleepMilliseconds = 1;

        private readonly PendulumEngine _engine;
        private readonly CycleEventReceiver _receiver;
        private readonly PdoConsistentReader _tape;
        private readonly string _threadName;
        private readonly long _cycleTicks;
        private readonly object _lifecycleGate = new object();
        private readonly CancellationTokenSource _stopSource = new CancellationTokenSource();

        private Thread _thread;
        private Exception _firstFault;
        private int _started;
        private int _admissionOpen;
        private int _inFlight;
        private int _completed;
        private int _stopRequested;
        private int _released;
        private int _releaseSucceeded;
        private ulong _lastTapeCycle;
        private int _firstPdoMarked;

        // 连续未就绪跟踪：_stallStartedTicks = 0 表示当前没有未就绪累积。
        private long _stallStartedTicks;
        private int _stallIterations;
        private int _stallWarned;

        public ControlThread(PendulumEngine engine, ushort masterIndex, uint cycleNanoseconds, string threadName)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            if (masterIndex == 0) throw new ArgumentOutOfRangeException("masterIndex");
            if (string.IsNullOrWhiteSpace(threadName))
                throw new ArgumentException("周期线程名不能为空。", "threadName");

            uint cycleNs = cycleNanoseconds < MinCycleNanoseconds
                ? (uint)MinCycleNanoseconds
                : cycleNanoseconds;

            _engine = engine;
            _threadName = threadName;
            _receiver = new CycleEventReceiver(masterIndex);
            _tape = new PdoConsistentReader(masterIndex);
            _cycleTicks = Math.Max(1L, Stopwatch.Frequency * cycleNs / 1000000000L);
        }

        /// <summary>内核已完成的周期序号。诊断里显示，也用来判补拍。</summary>
        public long CycleCount
        {
            get { return (long)_tape.CycleCount; }
        }

        public Exception FirstFault
        {
            get { return Volatile.Read(ref _firstFault); }
        }

        /// <summary>最后一拍已经跑完且没有在飞的拍。</summary>
        public bool IsDrained
        {
            get
            {
                if (Volatile.Read(ref _released) != 0) return true;
                return Volatile.Read(ref _completed) != 0 && Volatile.Read(ref _inFlight) == 0;
            }
        }

        public void Start()
        {
            lock (_lifecycleGate)
            {
                if (_released != 0)
                    throw new ObjectDisposedException("ControlThread");
                if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                    throw new InvalidOperationException("同一个周期执行器只能启动一次。");

                Volatile.Write(ref _admissionOpen, 1);
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = _threadName
                };
                try
                {
                    _thread.Start();
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _admissionOpen, 0);
                    RecordFirstFault(ex);
                    Volatile.Write(ref _completed, 1);
                    throw;
                }
            }
        }

        /// <summary>关门不再接纳新拍，等最后一拍跑完。不强杀线程。</summary>
        public void RequestStop()
        {
            Volatile.Write(ref _admissionOpen, 0);
            Thread.MemoryBarrier();
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
            try
            {
                _stopSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经 release 过，重复停止保持幂等。
            }
        }

        /// <summary>只在 IsDrained 之后释放托管 waiter。绝不释放仍在飞的 owner。</summary>
        public bool ReleaseAfterDrain()
        {
            lock (_lifecycleGate)
            {
                if (_released != 0) return _releaseSucceeded != 0;
                if (Volatile.Read(ref _completed) == 0 || Volatile.Read(ref _inFlight) != 0) return false;

                bool released = true;
                try
                {
                    _receiver.Dispose();
                }
                catch (Exception ex)
                {
                    released = false;
                    RecordFirstFault(ex);
                }
                try
                {
                    _stopSource.Dispose();
                }
                catch (Exception ex)
                {
                    released = false;
                    RecordFirstFault(ex);
                }
                _thread = null;
                Volatile.Write(ref _releaseSucceeded, released ? 1 : 0);
                Volatile.Write(ref _released, 1);
                return released;
            }
        }

        private void RecordFirstFault(Exception ex)
        {
            Interlocked.CompareExchange(ref _firstFault, ex, null);
        }

        /// <summary>
        /// 第一拍 PDO 已读到、0x607A 已写之后打一次点，并把连接链计时汇总送诊断通道。
        /// 只做一次：之后每拍只多一次 Interlocked 读取，不拼字符串、不阻塞。
        /// 汇总只入队（与 <see cref="ReportStall"/> 同一条慢路径出口），不在周期线程上同步回调前端。
        /// </summary>
        private void MarkFirstPdoOnce()
        {
            if (Interlocked.Exchange(ref _firstPdoMarked, 1) != 0) return;
            ConnectTiming.Mark("first_pdo");
            _engine.ReportCycleDiagnostic(ConnectTiming.Render());
        }

        /// <summary>
        /// 门铃与内核周期序号都没动时的等待。保留 0 超时轮询，但按连续未就绪拍数换更省的等待指令，
        /// 连续 2 秒仍不就绪降到每 1 毫秒检查一次（仍在轮询，CPU 立刻降下来），连续 10 秒判致命。
        /// 返回 true = 等到本拍周期；false = 本拍没有周期（调用方继续循环）。
        /// </summary>
        private bool WaitThroughStall(long waitStarted)
        {
            // 正常停机排队中的空转不算未就绪，别把用户的 Disconnect 报成致命故障。
            if (Volatile.Read(ref _stopRequested) != 0 || _stopSource.IsCancellationRequested) return false;

            if (_stallIterations < StallSleepZeroAfterCycles) _stallIterations++;
            if (_stallStartedTicks == 0) _stallStartedTicks = waitStarted;

            double waitedSeconds =
                (double)(Stopwatch.GetTimestamp() - _stallStartedTicks) / Stopwatch.Frequency;

            if (waitedSeconds >= StallFatalSeconds)
            {
                AbortStalledControl(waitedSeconds);
                return false;
            }

            if (waitedSeconds >= StallWarnSeconds)
            {
                if (Interlocked.Exchange(ref _stallWarned, 1) == 0)
                {
                    ReportStall("警告", waitedSeconds,
                        "周期等待超时，已降为每 1 毫秒检查一次，这一拍没有控制输出（不写目标位置）");
                }
                // 退化轮询：睡 1 ms 再看。CPU 立刻降下来，但仍然在轮询，内核一恢复就能接上。
                Thread.Sleep(StallDegradedSleepMilliseconds);
                return TryTakeCycle();
            }

            // 不能空转满一拍再看：空转期间内核序号已经 +1，转完才看会把 1ms 拍合并成约 2ms。
            // 序号一变就立刻跑，所以这里始终按序号取拍，只是等待指令随未就绪拍数退避。
            long deadline = waitStarted + _cycleTicks;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (Volatile.Read(ref _stopRequested) != 0 ||
                    _stopSource.IsCancellationRequested) return false;
                if (TryTakeTape()) return true;
                BackoffWait();
            }

            if (Volatile.Read(ref _stopRequested) != 0 || _stopSource.IsCancellationRequested) return false;
            return TryTakeCycle();
        }

        /// <summary>未就绪时逐档降 CPU：满速空转 → Yield → Sleep(0)。只换等待指令，不停止轮询。</summary>
        private void BackoffWait()
        {
            if (_stallIterations < StallYieldAfterCycles)
            {
                Thread.SpinWait(32);
                return;
            }
            if (_stallIterations < StallSleepZeroAfterCycles)
            {
                Thread.Yield();
                return;
            }
            Thread.Sleep(0);
        }

        /// <summary>看一次门铃（0 超时，设计约束），没响就看内核周期序号。会推进 _lastTapeCycle。</summary>
        private bool TryTakeCycle()
        {
            bool ready;
            try
            {
                ready = _receiver.WaitForCycle(WaitTimeoutMilliseconds);
            }
            catch (AccessViolationException ex)
            {
                // 不空吞：周期线程会因此静默停摆，必须把根因带进 FirstFault（Disconnect 时如实上报）。
                RecordFirstFault(new InvalidOperationException(
                    "WaitForCycle 触发 AccessViolation（周期放行判断）：" + ex.Message, ex));
                return false;
            }
            if (ready)
            {
                ulong tape = _tape.CycleCount;
                if (tape != 0) _lastTapeCycle = tape;
                return true;
            }
            return TryTakeTape();
        }

        /// <summary>内核周期序号前进了就算这一拍到了（内核跳过 merge 时门铃不响，序号每拍仍 +1）。</summary>
        private bool TryTakeTape()
        {
            ulong tape = _tape.CycleCount;
            if (tape == 0 || tape == _lastTapeCycle) return false;
            _lastTapeCycle = tape;
            return true;
        }

        /// <summary>收到周期就把退避与计时清零，下一次未就绪重新从最保守档开始。</summary>
        private void ResetStallTracking()
        {
            _stallStartedTicks = 0;
            _stallIterations = 0;
            Volatile.Write(ref _stallWarned, 0);
        }

        /// <summary>
        /// 连续 10 秒收不到内核周期 = 内核周期已停。继续空转只会占满一个核并把整机拖死，
        /// 所以这里停使能请求 + 关门退出：线程退出后 IsDrained 为真，上层可以正常 Disconnect 释放总线。
        /// </summary>
        private void AbortStalledControl(double waitedSeconds)
        {
            ReportStall("致命故障", waitedSeconds,
                "判定内核周期已停，停止控制循环，这一拍没有写目标位置");
            try
            {
                // 停使能请求排进引擎命令队列：内核若恢复，下一拍按受控停执行。
                _engine.Stop();
            }
            catch (Exception ex)
            {
                RecordFirstFault(ex);
            }
            RecordFirstFault(new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                "周期线程连续 {0:F1} 秒未收到内核周期，已停止控制循环。",
                waitedSeconds)));
            Volatile.Write(ref _admissionOpen, 0);
            RequestStop();
        }

        /// <summary>
        /// 未就绪的自救日志。只入队（PumpEvents 统一出队 raise），不在周期线程上回调前端。
        /// 取值全部做失败兜底：诊断本身出错绝不能把周期线程带崩。
        /// </summary>
        private void ReportStall(string kind, double waitedSeconds, string consequence)
        {
            int rtHz = 0;
            string link = "未知";
            string wkc = "未读到";
            try
            {
                PendulumTelemetry t;
                if (_engine.TryReadTelemetry(out t))
                {
                    wkc = t.ActualWkc.ToString(CultureInfo.InvariantCulture) + "/" +
                          t.ExpectedWkc.ToString(CultureInfo.InvariantCulture);
                }
                BusStatusSnapshot status;
                if (_engine.TryReadBusStatus(out status))
                {
                    rtHz = status.RtHz;
                    if (!string.IsNullOrEmpty(status.LinkState)) link = status.LinkState;
                }
            }
            catch (Exception ex)
            {
                link = "未知（读取失败 " + ex.GetType().Name + "）";
            }
            // RTcnt=0 的语义是"未推进 / 未启动"，按实情标出来，不用 0 冒充真值。
            string rtText = rtHz > 0
                ? rtHz.ToString(CultureInfo.InvariantCulture)
                : "0（未推进）";
            _engine.ReportCycleDiagnostic(string.Format(
                CultureInfo.InvariantCulture,
                "{0}：周期线程连续 {1:F1} 秒未收到内核周期，{2}。RTcnt={3} Hz，CycleCount={4}，WKC={5}，链路={6}。",
                kind, waitedSeconds, consequence, rtText, CycleCount, wkc, link));
        }

        /// <summary>
        /// 控制线程入口的一次性软实时设置。环境变量只负责选 housekeeping 核；
        /// 非法值回退默认 27，CPU30/31 明确拒绝，任何诊断失败都不得结束控制线程。
        /// </summary>
        private void ApplySoftRealTimeSettings()
        {
            int cpu = DefaultRtCpuIndex;
            try
            {
                string configured = Environment.GetEnvironmentVariable("FURUTA_RT_CPU");
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    int parsed;
                    if (!int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        ReportSoftRealTimeDiagnostic("FURUTA_RT_CPU=\"" + configured +
                            "\" 不是整数，回退默认 housekeeping CPU27。");
                    }
                    else if (parsed == FirstKernelRtLaneCpuIndex || parsed == SecondKernelRtLaneCpuIndex)
                    {
                        ReportSoftRealTimeDiagnostic("FURUTA_RT_CPU=" + parsed.ToString(CultureInfo.InvariantCulture) +
                            " 属于内核 RT lane（CPU30/31），拒绝绑定并回退 housekeeping CPU27，避免同核饿死。");
                    }
                    else
                    {
                        cpu = parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                cpu = DefaultRtCpuIndex;
                ReportSoftRealTimeDiagnostic("读取 FURUTA_RT_CPU 失败，回退默认 housekeeping CPU27：" +
                    ex.GetType().Name + ": " + ex.Message);
            }

            bool applied = SoftRealTime.ApplyCurrentThread(cpu);
            ReportSoftRealTimeDiagnostic("控制线程软实时初始化" + (applied ? "完成" : "未完整应用（控制循环照常运行）") +
                "，请求 CPU=" + cpu.ToString(CultureInfo.InvariantCulture) +
                "；实际线程优先级与亲和性掩码见 [soft_real_time] 统一日志。");
        }

        private void ReportSoftRealTimeDiagnostic(string text)
        {
            try
            {
                // 入口只入队一次；不会进入每拍日志路径。
                _engine.ReportCycleDiagnostic(text);
            }
            catch (Exception ex)
            {
                string stack = ex.StackTrace ?? string.Empty;
                if (stack.Length > 500) stack = stack.Substring(0, 500);
                Debug.WriteLine("软实时诊断入队失败：" + ex.GetType().Name + ": " + ex.Message +
                    "；StackTrace=" + stack);
            }
        }

        // 停机后 WaitForCycle 可能 AccessViolation。net472 的 catch(Exception) 抓不到损坏状态异常，
        // 必须标 HandleProcessCorruptedStateExceptions。
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private void Run()
        {
            try
            {
                ApplySoftRealTimeSettings();

                while (!_stopSource.IsCancellationRequested)
                {
                    if (Volatile.Read(ref _stopRequested) != 0) break;

                    long waitStarted = Stopwatch.GetTimestamp();
                    bool cycleReady;
                    try
                    {
                        // 0ms：门铃响没响立刻看。1ms Wait 会把 500µs 的内核拍合并。
                        cycleReady = _receiver.WaitForCycle(WaitTimeoutMilliseconds);
                    }
                    catch (AccessViolationException ex)
                    {
                        RecordFirstFault(new InvalidOperationException(
                            "WaitForCycle 触发 AccessViolation（周期主循环）：" + ex.Message, ex));
                        break;
                    }

                    // 门铃没响时用内核周期序号补拍：内核跳过 merge 时门铃不响，但序号每拍仍 +1。
                    if (!cycleReady)
                    {
                        ulong tape = _tape.CycleCount;
                        if (tape != 0 && tape != _lastTapeCycle)
                        {
                            cycleReady = true;
                            _lastTapeCycle = tape;
                        }
                    }
                    else
                    {
                        ulong tape = _tape.CycleCount;
                        if (tape != 0) _lastTapeCycle = tape;
                    }

                    // 门铃和内核周期序号都没动才叫未就绪。这一支绝不无限满速空转：
                    // 按连续未就绪拍数退避 → 2 秒降成 1 ms 轮询 → 10 秒判致命停循环。
                    // 未就绪期间不跑 ExecuteCycle，所以一拍都不写 0x607A / 0x6040。
                    if (!cycleReady && !WaitThroughStall(waitStarted)) continue;

                    // 本拍就绪：退避与计时清零，下一次未就绪重新从最保守的档位开始。
                    ResetStallTracking();

                    if (Volatile.Read(ref _admissionOpen) == 0) continue;

                    Interlocked.Increment(ref _inFlight);
                    try
                    {
                        if (Volatile.Read(ref _admissionOpen) == 0) continue;
                        // 本拍内算完并写出 0x607A / 0x6040，禁止抛回 UI 线程、禁止跨拍缓存待写。
                        _engine.ExecuteCycle();
                        MarkFirstPdoOnce();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }

                    // 本拍若跨过内核下一门铃，立刻再跑一拍把目标补上。timeout=0，没有新周期立刻回 Wait。
                    if (Volatile.Read(ref _admissionOpen) != 0 && Volatile.Read(ref _stopRequested) == 0)
                    {
                        bool extraReady = false;
                        try
                        {
                            extraReady = _receiver.WaitForCycle(WaitTimeoutMilliseconds);
                        }
                        catch (AccessViolationException ex)
                        {
                            RecordFirstFault(new InvalidOperationException(
                                "WaitForCycle 触发 AccessViolation（补拍判断）：" + ex.Message, ex));
                            extraReady = false;
                        }
                        if (extraReady)
                        {
                            Interlocked.Increment(ref _inFlight);
                            try
                            {
                                if (Volatile.Read(ref _admissionOpen) != 0)
                                    _engine.ExecuteCycle();
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _inFlight);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 首次异常锁存，之后关门不再接纳周期。
                RecordFirstFault(ex);
                Volatile.Write(ref _admissionOpen, 0);
                try
                {
                    _stopSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // owner 已完成 release。
                }
            }
            finally
            {
                Volatile.Write(ref _admissionOpen, 0);
                Volatile.Write(ref _completed, 1);
            }
        }
    }
}
