using System;
using System.Threading;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 引擎状态机的七个状态。Disconnected=没连总线，Connected=止于 SafeOp，Enabled=OP 且已使能。
    /// </summary>
    public enum EngineState : byte
    {
        Disconnected = 0,
        Connected = 1,
        Enabled = 2,
        Swinging = 3,
        Balancing = 4,
        Stopped = 5,
        Fault = 6
    }

    /// <summary>
    /// 故障码。名字与 CLI 退出码映射表逐字对应，不要改名。
    /// </summary>
    public enum FaultCode : ushort
    {
        None = 0,
        ThetaOverrun,
        FollowingError,
        EnableLost,
        DriveFault,
        WkcInvalid,
        PdoFrameLoss,
        EtherCatState,
        CallbackException,
        TimingInvalid,
        EStopInput,
        SwingUpTimeout,
        BalanceLost,
        IdentityMismatch,
        ConfigInvalid,
        StopSequenceTimeout,
        TeardownTimeout,
        UserStop,
        /// <summary>摆臂**实测**角越过软限位 + 余量（目标钳位管不到实测越界 → 立即安全停）。</summary>
        SoftLimit
    }

    /// <summary>
    /// 一次故障的快照。数字字段在周期线程里填，Detail 只在事件投递时格式化（周期线程不拼字符串）。
    /// </summary>
    public struct FaultInfo
    {
        public FaultCode Code;
        public EngineState FromState;
        public long TimestampTicks;
        public double ThetaRad;
        public double OmegaRadPerSec;
        public double PhiRad;
        /// <summary>摆臂绝对位置 0x6064 计数（故障现场按 counts 记录，不折算角度）。</summary>
        public int ArmPositionCount;
        public ushort StatusWord;
        public ushort ErrorCode;
        public ushort ActualWkc;
        public ushort ExpectedWkc;
        /// <summary>
        /// 数字字段是否来自**本次连接**的有效遥测快照。false = 上面的 θ / 摆臂 / 状态字
        /// 不是本次会话的实测值，日志必须写 n/a，不能拿 0 冒充测量值。
        /// </summary>
        public bool HasTelemetry;

        /// <summary>
        /// 故障瞬间「连续无有效过程数据」的拍数（0 = 过程数据正常）。
        /// 单拍 STALE 去抖连续到阈值才判 PdoFrameLoss，故障行必须能看出到底连续了多少拍。
        /// </summary>
        public int StaleCycles;

        /// <summary>
        /// 周期回调异常的第一行（类型名 + Message，最长 300 字）。旧版只把异常对象当 bool 用，
        /// [SAFETY] FAULT 行看不出是「PDO 输入映像不可用」还是别的抛出点，现场只能猜。
        /// 完整堆栈仍由 PumpEvents 的 DiagText.Describe 另记。
        /// </summary>
        public string ExceptionText;

        public string Detail;
    }

    /// <summary>
    /// 抖动统计快照。照 HCFA_X3E_QuarterTurn/TimingDiagnostics.cs 的同名结构。
    /// </summary>
    public struct TimingSnapshot
    {
        public int Count;
        public double Average;
        public double Maximum;
        public double Rms;
        public double LifetimeMaximum;
        public long LifetimeAtTicks;
        public long Overflow;

        public TimingSnapshot(
            int count,
            double average,
            double maximum,
            double rms,
            double lifetimeMaximum,
            long lifetimeAtTicks,
            long overflow)
        {
            Count = count;
            Average = average;
            Maximum = maximum;
            Rms = rms;
            LifetimeMaximum = lifetimeMaximum;
            LifetimeAtTicks = lifetimeAtTicks;
            Overflow = overflow;
        }
    }

    /// <summary>
    /// 一拍遥测。ControlThread 每拍写一条，UI/CLI 按 10–20 Hz 读快照，画曲线从环里批取。
    /// 全是 double/int/bool/enum，拷贝即值拷贝，读写两侧都不分配。
    /// </summary>
    public struct PendulumTelemetry
    {
        public long Sequence;
        public long TimestampTicks;
        public EngineState EngineState;

        // 摆杆 θ
        public double ThetaRad;
        public double ThetaFromUprightRad;
        public double OmegaRadPerSec;

        // 摆臂 φ
        public double PhiRad;
        public double PhiDotRadPerSec;
        public double PhiRefRad;
        public double PhiTargetRad;
        // 起始位置（home）≡ 坐标 0（使能自动归零，不是锁存角）：PhiHomeRad 恒 0，PhiDevFromHomeRad ≡ PhiRad。
        // 两个字段保留只为 UI / CLI 显示兼容（名字逐字，前端已按此对接）。
        public double PhiHomeRad;
        public double PhiDevFromHomeRad;

        // 原始计数
        public int PendulumCount;
        public int ArmPositionCount;
        public int ArmTargetCount;

        // 控制输出
        public double ControlThetaOut;
        public double ControlPhiOut;
        public double PtermTheta;
        public double ItermTheta;
        public double DtermTheta;
        public double PtermPhi;
        public double ItermPhi;
        public double DtermPhi;

        // 起摆
        public double EnergyJ;
        public double EnergyTargetJ;
        public double EnergyDeltaJ;
        public double SwingUpPhase;
        public int SwitchCountdown;

        // 起摆控制器的**显示快照**（只进遥测给 UI / CLI dashboard，不参与任何控制判据）：
        // ω 的最小二乘估计、当前分支、本拍实际施加的能量/泵加速度。
        /// <summary>本拍用于切换判据与泵律的 ω 估计 [rad/s]（40 ms 窗最小二乘；样本不足时 = 实测 ω）。</summary>
        public double OmegaEstRadPerSec;

        /// <summary>起摆是否处在起动微扰段。</summary>
        public bool SwingBootstrapActive;

        /// <summary>起摆是否处在软中心带回中段（|φ| 超出中心带，暂停注入、满权限回中）。</summary>
        public bool SwingRecentering;

        /// <summary>本拍能量项实际生效的摆臂加速度 [rad/s²]（含符号/调度权重，未含回中与钳位）。</summary>
        public double SwingEnergyAccelRadPerSec2;

        /// <summary>本拍实际加上的往复泵加速度 [rad/s²]（泵关 / 被 |ω| 门关掉时为 0）。</summary>
        public double SwingPumpAccelRadPerSec2;

        /// <summary>本拍回中项（弹簧 / 限速刹车）的加速度 [rad/s²]（只读诊断）。</summary>
        public double SwingCenteringAccelRadPerSec2;

        /// <summary>本拍真正送进速度积分器的加速度 [rad/s²]（最终钳位后；只读诊断）。</summary>
        public double SwingCommandAccelRadPerSec2;

        /// <summary>
        /// 本拍位置参考速度 [rad/s]（速度积分器状态，不再被软件限速钳位；只读诊断）。
        /// 失败轮它恒贴着旧钳位、从不反向，是"交变刹车被半波整流"的直接证据。
        /// </summary>
        public double SwingVelocityRefRadPerSec;

        // 整圈捕获（等待抬升 / 释放后累计一整圈；等待 / 记录中实时更新，供 UI 显示进度）
        /// <summary>正在等待抬升（摆臂保持不动，等人把摆杆抬到顶部 180°）。</summary>
        public bool ReleaseWaitActive;

        /// <summary>等待中且已「上膛」（在顶部窗口保持够久，离开即判释放）。</summary>
        public bool ReleaseArmed;

        /// <summary>整圈记录进行中（已释放，正在累计角度）。</summary>
        public bool CaptureActive;

        /// <summary>释放瞬间摆杆绝对角 [deg]（未释放 = NaN）。</summary>
        public double CaptureReleaseThetaDeg;

        /// <summary>释放瞬间摆杆角速度 [rad/s]（未释放 = NaN）。</summary>
        public double CaptureReleaseOmegaRadPerSec;

        /// <summary>本次记录期间 |θ−180°| 最大值 [deg]。</summary>
        public double CaptureMaxDeviationDeg;

        /// <summary>释放后已记录的时长 [s]（未释放 = 0）。旧字段名保留给 UI 兼容，语义 = 整圈记录耗时。</summary>
        public double CaptureHoldSeconds;

        /// <summary>相对释放点的有符号累计角 [deg]（按释放方向为正；≥360 = 绕满一圈）。</summary>
        public double CaptureSweepDeg;

        /// <summary>释放方向上的最大累计角 [deg]（≥360 = 有效）。</summary>
        public double CaptureMaxSweepDeg;

        /// <summary>已记录的关键点数。</summary>
        public int CaptureKeyPoints;

        /// <summary>
        /// 摆杆 CH1 计数距最近一次变化的秒数（∞ = 本次连接后从没见过变化）。
        /// 只用于展示「当前是否静止」，<b>不是</b>捕获的前置拒绝条件：静止起步是正常流程；
        /// 「臂动了但计数不动」由 CaptureSession 在抬升段判失效（记录段另有 1.5 s 冻结判据）。
        /// </summary>
        public double PendulumFeedbackAgeSeconds;

        // 驱动器
        public ushort ControlWord;
        public ushort StatusWord;
        public ushort DriveErrorCode;
        public sbyte ModesDisplay;
        public int FollowingErrorCount;

        // 摆臂反馈可信度（GR-EC01 / EC1 是纯开环步进）
        /// <summary>
        /// 0x6064 是不是实测位置。**EC1 = true**（开环估计值）：驱动器内部指令位置的积分估计，
        /// 丢步 / 堵转 / 越障静默不可见，不能当真实机械角的唯一依据；软限位同样靠它判，不能当安全边界。
        /// </summary>
        public bool ArmFeedbackIsEstimate;

        /// <summary>
        /// 0x606C 实际速度换算的摆臂角速度 [rad/s] = Pulse/s ÷ 每转位置单位 × 2π（每转位置单位 = CPR_ARM，
        /// 由 0x2102 实读 × 减速比或 0x608F × 0x6091 定标）。只进遥测 / 交叉校验，不进控制律；
        /// 控制律里的 φ̇ 走 φ 差分。未定标（每转位置单位 = 0）时如实给 0。
        /// <para>
        /// ⚠ EC1 实测该对象回的是**幅值**（|速度|），不带符号：30 s 起摆里摆臂反复换向，0x606C 23k 拍
        /// 无一为负；且量化步长 1000 Pulse/s（≈0.628 rad/s），低速段会在 0 与该步长之间跳。
        /// 所以它只能当「速度表」与粗交叉校验用，**不能**代替带符号的 φ̇。
        /// </para>
        /// </summary>
        public double ArmVelocityFromDriveRadPerSec;

        /// <summary>
        /// 0x606C 原始值 [Pulse/s]（驱动位置单位/秒，与 0x6064 的计数同口径）。遥测/CSV 留着它做
        /// 「速度积分 vs 位置增量」的交叉校验，也便于现场核对驱动器到底报的是 Pulse/s 还是 rpm。
        /// </summary>
        public int ArmVelocityPulsePerSec;

        /// <summary>0x60FD 数字输入原值 [UDINT32]。bit0 负限位 / bit1 正限位 / bit2 原点 / bit23 急停 / bit26~27 探针。</summary>
        public uint ArmDigitalInputs;

        /// <summary>0x60FD bit23 急停输入是否已动作。S11 的证据之一（状态字判不了"驱动已去使能时急停仍闭合"）。</summary>
        public bool ArmEStopInputActive;

        // 总线
        public ushort ActualWkc;
        public ushort ExpectedWkc;

        // 自有从站健康度（真机多从站：自有 = GCAN + 摆臂步进，其余是旁观者）
        /// <summary>摆臂步进从站本拍是否在 OP。</summary>
        public bool ArmSlaveInOp;

        /// <summary>GCAN 耦合器本拍是否在 OP。</summary>
        public bool GcanSlaveInOp;

        /// <summary>自有两台本拍是否还在响应（per-slave WKC 贡献；两边 Unknown 时退回全局 WKC 比对兜底）。</summary>
        public bool OwnedSlavesResponding;

        /// <summary>GC1502 频率计换算的 ω 交叉校验值 [rad/s] = FreqCh1[Hz] × 2π / CPR_PENDULUM。只进诊断，不进控制律。</summary>
        public double OmegaFromFreqRadPerSec;

        /// <summary>频率计交叉校验是否可用（GC1502 在位才有频率计；GC1602 恒 false）。</summary>
        public bool FreqCrossCheckValid;

        // GCAN 计数切片原值（直接取自过程映像，不换算）：只做展示与交叉校验，不进控制律
        /// <summary>CH1 脉冲频率 [Hz]（GC1502 0x6003:2）。GC1602 无频率计 = 0。</summary>
        public uint PendulumFreqCh1Hz;

        /// <summary>CH2 计数原值（GC1502 0x6003:3 UDINT / GC1602 0x6003:2 DINT，按 32 位位型给）。</summary>
        public uint PendulumCountCh2;

        /// <summary>CH2 脉冲频率 [Hz]（只有 GC1502 有）。其余 = 0。</summary>
        public uint PendulumFreqCh2Hz;

        // 时序
        public long CycleDeltaTicks;
        public long ComputeTicks;

        // 安全
        public FaultCode FaultCode;
        public bool EStopLatch;
        public bool IsSaturated;
        public int SaturatedCycles;
    }

    /// <summary>
    /// 总线级状态快照（只读）。由 BusSession 实读、PendulumEngine 转出，UI 只展示、不参与任何安全判据。
    /// 拿不到的项留 null / 0 并在 Valid / OnlineCountKnown 上说明，前端显示"—"，不得用 0 或"正常"冒充真值。
    /// </summary>
    public struct BusStatusSnapshot
    {
        /// <summary>总线是否已打开且读到主站。false = 其余字段不具语义。</summary>
        public bool Valid;

        /// <summary>主站 EcState 名字（None / Init / PreOp / SafeOp / OP）。读不到 = null。</summary>
        public string MasterState;

        /// <summary>native 真 OP 裁决（必需站逐站干净 OP 且合并实测 WKC 无亏空）。</summary>
        public bool RealOp;

        /// <summary>
        /// 链路观测名字（EcLinkState：Connected / PrimaryOnly / SecondaryOnly / Disconnected / Unknown）。
        /// 只描述网口 link，不是拓扑判据。读不到 = null。
        /// </summary>
        public string LinkState;

        /// <summary>主网口 PHY 链路（Up / Down / Unknown）。读不到 = null。</summary>
        public string PrimaryPort;

        /// <summary>冗余网口 PHY 链路（Up / Down / Unknown）。未配副口或读不到 = null。</summary>
        public string SecondaryPort;

        /// <summary>RTcnt：RT 线程实测总线帧频率 [Hz]。0 = 未推进 / 未启动，前端显示"—"，不写 0 Hz。</summary>
        public int RtHz;

        /// <summary>控制层已完成的 PDO 周期序号（内核 CycleCount）。</summary>
        public long PdoCycleCount;

        /// <summary>周期序号是否有样本（控制线程在跑）。false = 前端显示"—"。</summary>
        public bool PdoCycleCountKnown;

        /// <summary>总线扫到的从站总数。</summary>
        public int SlaveCount;

        /// <summary>未掉线从站数。OnlineCountKnown=false 时本值不具语义。</summary>
        public int OnlineSlaveCount;

        /// <summary>在线数是否有实测样本。</summary>
        public bool OnlineCountKnown;
    }

    /// <summary>
    /// 周期与总线统计。NativeTimingValid/NativeWkcValid 为假时前端显示"不可用"，不要显示 0 冒充真值。
    /// </summary>
    public struct EngineDiagnostics
    {
        public TimingSnapshot CycleJitterUs;
        public TimingSnapshot ThetaLoopUs;
        public long CycleCount;
        public uint BusCycleHz;
        public double BusAvgJitterUs;
        public double BusMaxJitterUs;
        public double BusRoundtripUs;
        public double BusLoadPercent;
        public ushort ActualWkc;
        public ushort ExpectedWkc;
        public int WcDeficit;
        public uint PdoFrameLossTotal;
        public uint PdoFrameLossConsecutive;
        public int DroppedTelemetryPackets;

        /// <summary>
        /// 应用层「连续无有效过程数据」拍数（单拍 STALE 去抖的降级标记）。
        /// 0 = 正常；&gt;0 = 正在沿用上一拍过程数据继续控制，达到阈值即判 PdoFrameLoss。
        /// 与 <see cref="PdoFrameLossConsecutive"/>（SDK 丢帧事件计数）是两条独立证据链，不合并。
        /// </summary>
        public int PdoStaleCycles;

        /// <summary>本会话累计无有效过程数据的拍数（只增不减，诊断用）。</summary>
        public long PdoStaleTotal;

        // 多从站拓扑（真机 6 从站：自有两台 + Beckhoff 链等旁观者）
        /// <summary>总线上从站总数（含旁观者）。</summary>
        public int SlaveCount;

        /// <summary>摆臂步进从站的 0 基下标。未连接 = -1。</summary>
        public int ArmSlaveIndex;

        /// <summary>GCAN 耦合器的 0 基下标。未连接 = -1。</summary>
        public int GcanSlaveIndex;

        /// <summary>GCAN 上在位的计数切片（GC1502 / GC1602）。未识别 = Unknown。</summary>
        public GcanSliceKind CounterSlice;

        /// <summary>
        /// 旁观从站状态一览，格式 "下标 名称 State=.. WKC=..[ 已掉线]"，多台用中文分号分隔；
        /// 没有旁观从站时为空串。慢路径拼好，只如实展示、不参与任何安全判据。
        /// </summary>
        public string ObserverSlaveSummary;

        public bool NativeTimingValid;
        public bool NativeWkcValid;
    }

    /// <summary>
    /// 固定容量、按单调时间淘汰的滑动统计窗口。
    /// 周期线程是唯一写者，读侧走版本双读。写路径无锁、无分配。
    /// </summary>
    internal sealed class TimingDiagnostics
    {
        private readonly long[] _ticks;
        private readonly double[] _values;
        private readonly double[] _squares;
        private readonly long[] _maxSequence;
        private readonly double[] _maxValues;
        private readonly long _windowTicks;
        private int _head;
        private int _count;
        private long _sequence;
        private long _maxHead;
        private long _maxTail;
        private double _sum;
        private double _squareSum;
        private int _version;
        private long _overflow;
        private double _lifetimeMax;
        private long _lifetimeMaxAt;

        public TimingDiagnostics(int capacity, double windowSeconds)
        {
            _ticks = new long[capacity];
            _values = new double[capacity];
            _squares = new double[capacity];
            _maxSequence = new long[capacity + 1];
            _maxValues = new double[capacity + 1];
            _windowTicks = (long)(StopwatchFrequency * windowSeconds);
        }

        // Stopwatch.Frequency 不是 const，构造期算一次存起来。
        private static readonly long StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;

        // 只有周期线程调用。version 奇=正在写，读侧看到奇数就重试。
        public void Add(long now, double value, long sessionStart)
        {
            Interlocked.Increment(ref _version);
            Expire(now);
            if (_count == _ticks.Length)
            {
                RemoveHead();
                _overflow++;
            }
            int tail = (_head + _count) % _ticks.Length;
            double square = value * value;
            _ticks[tail] = now;
            _values[tail] = value;
            _squares[tail] = square;
            _sum += value;
            _squareSum += square;
            _count++;

            long sequence = ++_sequence;
            int capacity = _maxValues.Length;
            while (_maxTail > _maxHead && _maxValues[(int)((_maxTail - 1) % capacity)] <= value) _maxTail--;
            int maxTail = (int)(_maxTail % capacity);
            _maxValues[maxTail] = value;
            _maxSequence[maxTail] = sequence;
            _maxTail++;
            DropExpiredMax();
            if (value > _lifetimeMax)
            {
                _lifetimeMax = value;
                _lifetimeMaxAt = now - sessionStart;
            }
            Interlocked.Increment(ref _version);
        }

        private void Expire(long now)
        {
            long oldest = now - _windowTicks;
            while (_count > 0 && _ticks[_head] < oldest) RemoveHead();
            DropExpiredMax();
        }

        private void RemoveHead()
        {
            _sum -= _values[_head];
            _squareSum -= _squares[_head];
            _head = (_head + 1) % _ticks.Length;
            _count--;
        }

        private void DropExpiredMax()
        {
            long firstSequence = _sequence - _count + 1;
            int capacity = _maxValues.Length;
            while (_maxTail > _maxHead && _maxSequence[(int)(_maxHead % capacity)] < firstSequence) _maxHead++;
        }

        public void Reset()
        {
            Interlocked.Increment(ref _version);
            _head = 0;
            _count = 0;
            _sequence = 0;
            _maxHead = 0;
            _maxTail = 0;
            _sum = 0;
            _squareSum = 0;
            _overflow = 0;
            _lifetimeMax = 0;
            _lifetimeMaxAt = 0;
            Interlocked.Increment(ref _version);
        }

        public bool TryRead(out TimingSnapshot snapshot)
        {
            snapshot = default(TimingSnapshot);
            for (int retry = 0; retry < 8; retry++)
            {
                int begin = Volatile.Read(ref _version);
                if ((begin & 1) != 0) continue;
                int count = _count;
                double sum = _sum;
                double square = _squareSum;
                long maxHead = _maxHead;
                long maxTail = _maxTail;
                double maximum = maxTail > maxHead ? _maxValues[(int)(maxHead % _maxValues.Length)] : 0;
                double lifetime = _lifetimeMax;
                long lifetimeAt = _lifetimeMaxAt;
                long overflow = _overflow;
                Thread.MemoryBarrier();
                int end = Volatile.Read(ref _version);
                if (begin != end || (end & 1) != 0) continue;
                snapshot = default(TimingSnapshot);
                snapshot.Count = count;
                snapshot.Average = count == 0 ? 0 : sum / count;
                snapshot.Maximum = maximum;
                snapshot.Rms = count == 0 ? 0 : Math.Sqrt(Math.Max(0, square / count));
                snapshot.LifetimeMaximum = lifetime;
                snapshot.LifetimeAtTicks = lifetimeAt;
                snapshot.Overflow = overflow;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 最新快照的 seqlock 单槽。写侧只在周期线程，读侧任意线程。
    /// 写：版本 +1（奇）→ 写值 → 版本 +1（偶）。读：读版本（必须偶）→ 拷值 → 屏障 → 再读版本，两次相同才算一致。
    /// </summary>
    internal sealed class TelemetrySlot
    {
        private int _version;
        private PendulumTelemetry _value;

        /// <summary>seqlock 版本号。偶 = 有稳定快照，奇 = 正在写。周期侧拿它自检写有没有落地。</summary>
        public int Version
        {
            get { return Volatile.Read(ref _version); }
        }

        public void Write(in PendulumTelemetry value)
        {
            Interlocked.Increment(ref _version);
            _value = value;
            Interlocked.Increment(ref _version);
        }

        public bool TryRead(out PendulumTelemetry value)
        {
            for (int retry = 0; retry < 8; retry++)
            {
                int begin = Volatile.Read(ref _version);
                if ((begin & 1) != 0) continue;
                PendulumTelemetry copy = _value;
                Thread.MemoryBarrier();
                int end = Volatile.Read(ref _version);
                if (begin != end || (end & 1) != 0) continue;
                value = copy;
                return true;
            }
            value = default(PendulumTelemetry);
            return false;
        }
    }

    /// <summary>
    /// 遥测环（SPSC）。周期线程单写，UI/CLI 批取画曲线。构造期一次 new，之后零分配。
    /// 写满时丢新样本并计数，绝不覆盖读侧还没取走的槽，避免读到撕裂值。
    /// </summary>
    internal sealed class TelemetryRing
    {
        private const int Capacity = 4096;
        private const long Mask = Capacity - 1;
        private readonly PendulumTelemetry[] _items = new PendulumTelemetry[Capacity];
        private long _write;
        private long _read;
        private int _dropped;

        public int Dropped
        {
            get { return Volatile.Read(ref _dropped); }
        }

        public bool Write(in PendulumTelemetry value)
        {
            long write = _write;
            long read = Volatile.Read(ref _read);
            if (write - read >= Capacity)
            {
                // 读侧落后满一圈：丢掉本条，让读侧按顺序取到连贯的历史。
                // 返回 false 让周期线程能发现"遥测环写停"，这是 S10 的周期侧证据。
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _items[(int)(write & Mask)] = value;
            Volatile.Write(ref _write, write + 1);
            return true;
        }

        public int TryReadBatch(PendulumTelemetry[] dst, int max)
        {
            if (dst == null || max <= 0) return 0;
            int limit = max < dst.Length ? max : dst.Length;
            long read = _read;
            long write = Volatile.Read(ref _write);
            int n = 0;
            while (read < write && n < limit)
            {
                dst[n++] = _items[(int)(read & Mask)];
                read++;
            }
            Volatile.Write(ref _read, read);
            return n;
        }
    }

    /// <summary>
    /// 异常留痕的统一格式：类型名 + 消息 + 堆栈前 500 字。
    /// 失败路径不允许空吞异常，一律走这个格式记下来。
    /// </summary>
    internal static class DiagText
    {
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            string stack = ex.StackTrace ?? string.Empty;
            if (stack.Length > 500) stack = stack.Substring(0, 500);
            return string.Format("{0} {1} {2}", ex.GetType().Name, ex.Message, stack);
        }
    }

    /// <summary>
    /// 引擎事件的种类。状态迁移与故障是纯数字，诊断行才带字符串（慢路径拼好再入队）。
    /// </summary>
    internal enum EngineEventKind : byte
    {
        StateChanged = 1,
        Faulted = 2,
        Diagnostic = 3,
        /// <summary>一次整圈捕获结束（记录在 <see cref="EngineEvent.Capture"/>）。</summary>
        CaptureCompleted = 4
    }

    /// <summary>
    /// 引擎事件。周期线程只压数字，字符串只在慢路径或 1 Hz 诊断里填。
    /// </summary>
    internal struct EngineEvent
    {
        public EngineEventKind Kind;
        public EngineState FromState;
        public EngineState ToState;
        public FaultInfo Fault;
        public CaptureRecord Capture;
        public string Text;
    }

    /// <summary>
    /// 状态/故障/诊断事件的 SPSC 队列。周期线程入队，PendulumEngine.PumpEvents 出队并 raise。
    /// 满了就丢（周期线程绝不阻塞等前端）。
    /// </summary>
    internal sealed class EngineEventQueue
    {
        private const int Capacity = 256;
        private const long Mask = Capacity - 1;
        private readonly EngineEvent[] _items = new EngineEvent[Capacity];
        private long _write;
        private long _read;
        private int _dropped;

        public int Dropped
        {
            get { return Volatile.Read(ref _dropped); }
        }

        public void Enqueue(in EngineEvent item)
        {
            long write = _write;
            if (write - Volatile.Read(ref _read) >= Capacity)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _items[(int)(write & Mask)] = item;
            Volatile.Write(ref _write, write + 1);
        }

        public bool TryDequeue(out EngineEvent item)
        {
            long read = _read;
            if (read >= Volatile.Read(ref _write))
            {
                item = default(EngineEvent);
                return false;
            }
            item = _items[(int)(read & Mask)];
            Volatile.Write(ref _read, read + 1);
            return true;
        }
    }
}
