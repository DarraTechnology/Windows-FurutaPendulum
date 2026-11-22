using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 整圈捕获单拍推进的结果。除 <see cref="None"/> 外都是一次性的（消费后回到 None）。
    /// </summary>
    internal enum CaptureEvent : byte
    {
        None = 0,
        /// <summary>摆杆已在顶部窗口稳定住（上膛），接下来离开即判释放。</summary>
        Armed = 1,
        /// <summary>检测到释放，已记下释放点，开始按整圈判据记录。</summary>
        Released = 2,
        /// <summary>朝释放方向累计绕满 360°，捕获有效；调用方取记录收尾。</summary>
        Completed = 3,
        /// <summary>捕获失效（方向反转 / 停滞 / 超时 / 编码器冻结）；原因在 <see cref="InvalidReason"/>。</summary>
        Invalidated = 4,
        /// <summary>等待抬升超时（人没把摆杆抬到顶部），不算一次捕获，不产生记录。</summary>
        LiftTimeout = 5,
        /// <summary>摆臂实测角越过软限位 + 余量，调用方应立即安全停机。</summary>
        SoftLimitTrip = 6,
        /// <summary>抬升没到顶部就回落了（仅提示，继续等待：可重试抬升）。</summary>
        LiftNotReachedTop = 7
    }

    /// <summary>
    /// 「整圈捕获」状态机（仅周期线程推进；前端只读属性/遥测）。
    ///
    /// 用户流程：
    ///   1. 摆杆自然下垂（θ = 0）静止 → 点「捕获」：记下点击时刻的绝对角与原始计数作为零点参考；
    ///   2. 把摆杆抬到顶部（θ = 180°，|θ−180°| ≤ 8° 才算稳定上膛）；
    ///   3. 释放：摆杆必须**继续朝抬升方向**转满一整圈（累计 360°）才算有效；
    ///      「左侧来、左侧回」（释放后方向反转、没绕过去）判失效并写清原因；
    ///   4. 有效/失效都记关键点（时间 + 摆杆角 + 累计角 + 窗口角速度/角加速度 + 摆臂角）。
    ///
    /// 判据细节：
    ///   - 累计角用**未折返的绝对角** θ（由 32 位计数差直接换算，跨圈不归一）；
    ///     不是「模 2π 的位置差」——那种差在半圈处会跳变，判不出到底绕没绕过去。
    ///   - 释放方向 = 释放点相对「最后一次低于水平线」样本的转向；释放后的累计角按它取正。
    ///   - 角速度/角加速度用 20 ms 窗口差分（1 拍差分被计数分辨率量化毁掉：2400 CPR 下大量相邻拍差为 0）。
    ///
    /// 安全：软限位在这里只判**实测**摆臂角越界（钳位只作用于目标，实测越界 = 机械已出界，仍立即停）。
    /// </summary>
    internal sealed class CaptureSession
    {
        // ==================== 判据常量 ====================

        /// <summary>「在顶部」窗口：|θ−180°| ≤ 8° 才算手扶到倒立附近 [rad]。</summary>
        public const double TopWindowRad = 8.0 * Math.PI / 180.0;

        /// <summary>在顶部连续保持这么久才「上膛」，避免路过顶部误判 [s]。</summary>
        private const double ArmHoldSeconds = 0.05;

        /// <summary>松手判据的角速度下界 [rad/s]。</summary>
        private const double ReleaseOmegaMinRadPerSec = 0.30;

        /// <summary>离开窗口且 |ω| 达标连续这么久才确认松手，压掉单拍噪声 [s]。</summary>
        private const double ReleaseConfirmSeconds = 0.003;

        /// <summary>跌出这么大偏角就无条件按松手处理 [rad]。</summary>
        public const double FallRad = 30.0 * Math.PI / 180.0;

        /// <summary>抬升到离顶部这么近（相对下垂角的绝对增量）就算「到过顶部」，之后离开顶部即判释放 [deg]。</summary>
        private const double ReachedTopDeg = 165.0;

        /// <summary>一整圈 [deg]。</summary>
        public const double FullTurnDeg = 360.0;

        /// <summary>方向反转判据：释放方向上的累计角从最大值回落这么多 = 「左侧来左侧回」[deg]。</summary>
        private const double ReversalVetoDeg = 15.0;

        /// <summary>释放后这么久没有新的前进量就判停滞 [s]。</summary>
        private const double StallSeconds = 3.0;

        /// <summary>释放后这么久还没绕满一圈就判超时 [s]。</summary>
        private const double RevolutionTimeoutSeconds = 30.0;

        /// <summary>等待抬升的总时限 [s]；超时退回普通待机，不算故障、不产生记录。</summary>
        private const double WaitTimeoutSeconds = 120.0;

        /// <summary>释放后摆杆计数连续这么久不变 = 编码器冻结，整圈数据不可信 [s]。</summary>
        private const double FeedbackFreezeSeconds = 1.5;

        /// <summary>
        /// 抬升段「臂动了但摆杆没动」的判据：摆杆计数连续严格不变这么久才判编码器无响应 [s]。
        /// 只判「连续时长」不够 —— 用户点捕获时摆杆本来就静止，必须同时要求**摆臂确实在走**；
        /// 摆臂走了而摆杆计数一位都不变，在机械上只可能是编码器 / 接线失效。
        /// </summary>
        private const double LiftFeedbackFreezeSeconds = 1.5;

        /// <summary>
        /// 抬升段判「摆臂确实在走」的最小行程 [count]（自摆杆计数上次变化以来累计的 max−min）。
        /// 量级依据：常见 CPR_ARM = 10000 count/rev（0x2102 P=10000 × 减速比 1），100 count ≈ 3.6°，
        /// 高于驱动器跟随 / 采样噪声（单拍抖动是个位 count），又远小于一次抬升（通常几千 count）。
        /// </summary>
        private const int LiftArmTravelCounts = 100;

        /// <summary>抬升超过这个高度又回落到水平线以下才提示「没到顶部」[deg]。</summary>
        private const double LiftHintDeg = 90.0;

        /// <summary>窗口差分窗口 [s]（20 ms；1 拍差分不可用）。</summary>
        private const double SpeedWindowSeconds = 0.020;

        /// <summary>角加速度差分锚点间隔 [s]。</summary>
        private const double AlphaAnchorSeconds = 0.020;

        /// <summary>极值点判据：窗口 ω 从这么大变号才算一次换向 [rad/s]。</summary>
        private const double TurningOmegaRadPerSec = 0.15;

        /// <summary>两次极值点之间至少隔这么久，压掉噪声 [s]。</summary>
        private const double TurningMinSpacingSeconds = 0.03;

        // ==================== 采样环（窗口差分用）====================

        private const int RingSize = 512;   // 512 × 0.5 ms = 256 ms

        private readonly long[] _ringTicks = new long[RingSize];
        private readonly double[] _ringThetaRad = new double[RingSize];
        private int _ringHead;
        private int _ringCount;

        // ==================== 状态 ====================

        private const int StateIdle = 0;
        private const int StateWaitingLift = 1;
        private const int StateRecording = 2;

        private int _state;
        private int _armed;

        private long _startTicks;
        private long _lastTicks;
        private long _releaseTicks;
        private DateTime _startTimestampUtc;
        private DateTime _releaseTimestampUtc;

        // 零点参考
        private double _startThetaRad;
        private int _startPendulumCount;

        // 抬升段
        private double _maxLiftDeg;
        private double _lastBelowHorizontalThetaRad;
        private bool _hasBelowHorizontal;
        private bool _liftCrossedHorizontal;
        private bool _liftHintRaised;
        private double _liftHintAtMaxDeg;
        private double _armHoldSec;
        private double _leaveSec;
        private bool _pendingReleaseValid;
        private double _pendingReleaseThetaRad;
        private double _pendingReleaseOmega;

        // 释放点
        private double _releaseThetaRad;
        private double _releaseOmega;
        private int _liftSign;

        // 整圈记录
        private double _sweepDeg;
        private double _forwardMaxDeg;
        private long _lastProgressTicks;
        private double _maxDeviationDeg;
        private double _maxOmegaAbs;
        private bool _crossedQuarter;
        private bool _crossedHalf;
        private bool _crossedThreeQuarter;
        private long _lastTurningTicks;
        private double _lastTurningOmega;
        private bool _hasTurningOmega;
        private bool _invalidated;
        private string _invalidReason;
        private bool _completed;
        private long _completeTicks;

        // 计数冻结
        private int _lastPendulumCount;
        private long _countLastChangeTicks;
        // 「臂动了但摆杆没动」：自摆杆计数上次变化以来的摆臂行程窗口（抬升段判据）。
        private bool _armTravelPrimed;
        private int _armTravelLow;
        private int _armTravelHigh;

        private readonly List<CaptureKeyPoint> _keyPoints = new List<CaptureKeyPoint>(32);

        /// <summary>构造即「无会话」（释放角为 NaN，不用 0 冒充）。</summary>
        public CaptureSession()
        {
            ResetSession();
        }

        // ==================== 只读面 ====================

        /// <summary>等待抬升中（摆臂保持不动，等人把摆杆抬到顶部）。</summary>
        public bool IsWaiting
        {
            get { return Volatile.Read(ref _state) == StateWaitingLift; }
        }

        /// <summary>整圈记录中（已释放，正在累计角度）。</summary>
        public bool IsCapturing
        {
            get { return Volatile.Read(ref _state) == StateRecording; }
        }

        /// <summary>等待中且已在顶部窗口稳定住（可以松手了）。</summary>
        public bool IsArmed
        {
            get { return Volatile.Read(ref _armed) != 0; }
        }

        /// <summary>释放瞬间摆杆绝对角 [deg]（未释放 = NaN）。</summary>
        public double ReleaseThetaDeg
        {
            get { return ControlMath.IsFinite(_releaseThetaRad) ? _releaseThetaRad * (180.0 / Math.PI) : double.NaN; }
        }

        /// <summary>释放瞬间摆杆角速度 [rad/s]（未释放 = NaN）。</summary>
        public double ReleaseOmegaRadPerSec
        {
            get { return _releaseOmega; }
        }

        /// <summary>本次记录期间 |θ−180°| 最大值 [deg]。</summary>
        public double MaxDeviationDeg
        {
            get { return _maxDeviationDeg; }
        }

        /// <summary>相对释放点的有符号累计角 [deg]（按释放方向为正）。</summary>
        public double SweepDeg
        {
            get { return _sweepDeg; }
        }

        /// <summary>释放方向上的最大累计角 [deg]（≥360 = 有效）。</summary>
        public double MaxSweepDeg
        {
            get { return _forwardMaxDeg; }
        }

        /// <summary>失效原因；有效或进行中 = null。</summary>
        public string InvalidReason
        {
            get { return _invalidReason; }
        }

        /// <summary>抬升方向：+1 / -1（判不出时按 +1 处理）。</summary>
        public int LiftSign
        {
            get { return _liftSign; }
        }

        /// <summary>释放后已经记录的时长 [s]（未释放 = 0）。</summary>
        public double RecordingSeconds
        {
            get
            {
                if (!ControlMath.IsFinite(_releaseThetaRad) || _releaseTicks == 0)
                    return 0;
                long now = Volatile.Read(ref _lastTicks);
                if (now <= _releaseTicks) return 0;
                return (now - _releaseTicks) / (double)Stopwatch.Frequency;
            }
        }

        /// <summary>本次等待期间抬升到的最高角（相对点击捕获时的摆杆角）[deg]。</summary>
        public double MaxLiftDeg
        {
            get { return _maxLiftDeg; }
        }

        /// <summary>已记录关键点数。</summary>
        public int KeyPointCount
        {
            get { return _keyPoints.Count; }
        }

        // ==================== 会话控制 ====================

        /// <summary>清空状态（连接、断开、急停、收尾时调用）。不产生记录。</summary>
        public void ResetSession()
        {
            Volatile.Write(ref _state, StateIdle);
            Volatile.Write(ref _armed, 0);
            _startTicks = 0;
            _lastTicks = 0;
            _releaseTicks = 0;
            _startTimestampUtc = DateTime.MinValue;
            _releaseTimestampUtc = DateTime.MinValue;
            _startThetaRad = 0;
            _startPendulumCount = 0;
            _maxLiftDeg = 0;
            _lastBelowHorizontalThetaRad = 0;
            _hasBelowHorizontal = false;
            _liftCrossedHorizontal = false;
            _liftHintRaised = false;
            _liftHintAtMaxDeg = 0;
            _armHoldSec = 0;
            _leaveSec = 0;
            _pendingReleaseValid = false;
            _pendingReleaseThetaRad = 0;
            _pendingReleaseOmega = 0;
            _releaseThetaRad = double.NaN;
            _releaseOmega = double.NaN;
            _liftSign = 1;
            _sweepDeg = 0;
            _forwardMaxDeg = 0;
            _lastProgressTicks = 0;
            _maxDeviationDeg = 0;
            _maxOmegaAbs = 0;
            _crossedQuarter = false;
            _crossedHalf = false;
            _crossedThreeQuarter = false;
            _lastTurningTicks = 0;
            _lastTurningOmega = 0;
            _hasTurningOmega = false;
            _invalidated = false;
            _invalidReason = null;
            _completed = false;
            _completeTicks = 0;
            _lastPendulumCount = 0;
            _countLastChangeTicks = 0;
            _armTravelPrimed = false;
            _armTravelLow = 0;
            _armTravelHigh = 0;
            _keyPoints.Clear();
            _ringHead = 0;
            _ringCount = 0;
        }

        /// <summary>
        /// 进入等待抬升。调用前摆臂目标应已钉在当前 0x6064 上（start 参数是当拍实测值），本类不碰控制字。
        /// </summary>
        public void Begin(long nowTicks, double thetaRad, int pendulumCount)
        {
            ResetSession();
            Volatile.Write(ref _state, StateWaitingLift);
            _startTicks = nowTicks;
            _lastTicks = nowTicks;
            _startTimestampUtc = DateTime.UtcNow;
            _startThetaRad = ControlMath.IsFinite(thetaRad) ? thetaRad : 0;
            LastThetaRad = _startThetaRad;
            _startPendulumCount = pendulumCount;
            _lastPendulumCount = pendulumCount;
            _countLastChangeTicks = nowTicks;
            AddKeyPoint(nowTicks, CaptureKeyPointKind.Start);
        }

        /// <summary>
        /// 推进一拍。仅周期线程调用；返回一次性事件（见 <see cref="CaptureEvent"/>）。
        /// <paramref name="armPositionCount"/> = 当拍摆臂实测位置（0x6064 原始 count），
        /// 抬升段用它判「臂动了但摆杆没动」；它不是控制量，只做证据。
        /// </summary>
        public CaptureEvent Step(
            long nowTicks,
            double thetaRad,
            double omegaRadPerSec,
            double phiRad,
            double phiDotRadPerSec,
            int pendulumCount,
            int armPositionCount,
            int armLimitMarginCount,
            SafetyLimits limits)
        {
            int state = Volatile.Read(ref _state);
            if (state == StateIdle)
                return CaptureEvent.None;

            // 实测摆臂越界优先于一切（已判失效也不例外）：钳位只作用于目标，实测越界就是机械出界。
            // 判定直接在 counts 域（绝对 0x6064），不绕角度。
            if (IsBeyondSoftLimit(armPositionCount, limits, armLimitMarginCount))
                return CaptureEvent.SoftLimitTrip;

            // 已判失效 / 已完成：等调用方 EndAttempt 取走记录，不再重复发事件（避免同一拍风暴）。
            if (_invalidated || _completed)
                return CaptureEvent.None;

            // 等待抬升超时先判（它比「计数冻结」更根本：人根本没开始抬，不算一次捕获）。
            if (state == StateWaitingLift &&
                nowTicks - _startTicks > (long)(WaitTimeoutSeconds * Stopwatch.Frequency))
            {
                ResetSession();
                return CaptureEvent.LiftTimeout;
            }

            // 计数冻结：抬升段只在「该动的时候没动」才判失效；上膛后允许静止扶住；记录段冻结 = 数据不可信。
            UpdateCountLiveness(nowTicks, pendulumCount, armPositionCount);
            double frozenSeconds = (nowTicks - _countLastChangeTicks) / (double)Stopwatch.Frequency;
            if (state == StateWaitingLift && Volatile.Read(ref _armed) == 0)
            {
                // 用户流程：摆杆自然下垂静止时点捕获 → 再抬起摆臂。所以「静止」本身不是证据，
                // 「摆臂已经走了 ≥ 阈值，而 CH1 计数一位都没变（严格相等）」才是编码器 / 接线失效的决定性证据。
                // 这里不误伤静止起步：人还没开始抬（臂行程 < 阈值）时无论冻结多久都不判。
                long armTravel = (long)_armTravelHigh - _armTravelLow;
                if (frozenSeconds >= LiftFeedbackFreezeSeconds && armTravel >= LiftArmTravelCounts)
                {
                    Invalidate(nowTicks, string.Format(
                        "编码器无响应：摆臂已移动 {0} counts，摆杆计数仍恒为 {1}（连续 {2:F1}s 未变化）。" +
                        "检查编码器接线 / GC1602 计数通道后再捕获。",
                        armTravel, pendulumCount, frozenSeconds));
                    return CaptureEvent.Invalidated;
                }
            }
            if (state == StateRecording && frozenSeconds >= FeedbackFreezeSeconds)
            {
                Invalidate(nowTicks, string.Format(
                    "摆杆计数冻结：CH1 原始计数连续 {0:F1}s 停在 {1}，整圈数据不可信。" +
                    "检查编码器接线 / GCAN 计数切片与摆杆是否真的停住，再重新捕获。",
                    frozenSeconds, pendulumCount));
                return CaptureEvent.Invalidated;
            }

            PushSample(nowTicks, thetaRad);
            LastPhiRad = ControlMath.IsFinite(phiRad) ? phiRad : LastPhiRad;
            LastPhiDotRadPerSec = ControlMath.IsFinite(phiDotRadPerSec) ? phiDotRadPerSec : 0;
            double dt = StepSeconds(nowTicks);

            if (state == StateWaitingLift)
                return StepWaitingLift(nowTicks, dt, thetaRad, omegaRadPerSec, phiRad, phiDotRadPerSec);

            return StepRecording(nowTicks, thetaRad, phiRad, phiDotRadPerSec);
        }

        /// <summary>
        /// 抬升段推进：等顶部稳定 → 判释放。
        /// </summary>
        private CaptureEvent StepWaitingLift(
            long nowTicks, double dt, double thetaRad, double omegaRadPerSec,
            double phiRad, double phiDotRadPerSec)
        {
            double deviation = Math.Abs(ControlMath.WrapPi(thetaRad - Math.PI));
            double liftDeg = Math.Abs(thetaRad - _startThetaRad) * (180.0 / Math.PI);
            if (liftDeg > _maxLiftDeg)
                _maxLiftDeg = liftDeg;

            // 「低于水平线」样本：判释放方向用（最后一次低于 90° 的位置 → 释放点）。
            if (deviation > Math.PI * 0.5)
            {
                _lastBelowHorizontalThetaRad = thetaRad;
                _hasBelowHorizontal = true;
            }
            if (!_liftCrossedHorizontal && _maxLiftDeg >= LiftHintDeg)
            {
                _liftCrossedHorizontal = true;
                AddKeyPoint(nowTicks, CaptureKeyPointKind.Lift);
            }

            // 到顶部（±8°）并保持 50 ms → 上膛。
            if (deviation <= TopWindowRad)
            {
                _armHoldSec += dt;
                if (Volatile.Read(ref _armed) == 0 && _armHoldSec >= ArmHoldSeconds)
                {
                    Volatile.Write(ref _armed, 1);
                    AddKeyPoint(nowTicks, CaptureKeyPointKind.Armed);
                    return CaptureEvent.Armed;
                }
            }
            else
            {
                _armHoldSec = 0;
            }

            bool reachedTop = _maxLiftDeg >= ReachedTopDeg;
            bool leaving = reachedTop &&
                           deviation > TopWindowRad &&
                           omegaRadPerSec * ControlMath.WrapPi(thetaRad - Math.PI) > 0 &&
                           Math.Abs(omegaRadPerSec) >= ReleaseOmegaMinRadPerSec;
            if (leaving)
            {
                if (!_pendingReleaseValid)
                {
                    // 记「刚离开顶部窗口」那一刻，确认窗口内人又扶回去就作废。
                    _pendingReleaseValid = true;
                    _pendingReleaseThetaRad = thetaRad;
                    _pendingReleaseOmega = omegaRadPerSec;
                }
                _leaveSec += dt;
                if (_leaveSec >= ReleaseConfirmSeconds)
                {
                    BeginRecording(nowTicks, thetaRad, omegaRadPerSec, phiRad, phiDotRadPerSec);
                    return CaptureEvent.Released;
                }
            }
            else
            {
                _leaveSec = 0;
                if (deviation <= TopWindowRad)
                    _pendingReleaseValid = false;
            }

            // 兜底：已经跌出 30°，不管角速度判据，按松手处理（记录里如实写释放角/角速度）。
            if (reachedTop && deviation > FallRad)
            {
                BeginRecording(nowTicks, thetaRad, omegaRadPerSec, phiRad, phiDotRadPerSec);
                return CaptureEvent.Released;
            }

            // 抬到过水平线以上又落回水平线以下、始终没进顶部窗口 → 提示「没到顶部」，继续等待重试。
            if (!reachedTop && _liftCrossedHorizontal && deviation > Math.PI * 0.5 &&
                !_liftHintRaised && _maxLiftDeg >= LiftHintDeg)
            {
                _liftHintRaised = true;
                _liftHintAtMaxDeg = _maxLiftDeg;
                return CaptureEvent.LiftNotReachedTop;
            }
            // 再次抬得明显更高时允许再提示一次（换了更接近顶部的一次抬升）。
            if (_liftHintRaised && _maxLiftDeg > _liftHintAtMaxDeg + 10.0)
                _liftHintRaised = false;

            return CaptureEvent.None;
        }

        /// <summary>确认释放：定释放点与释放方向，进入整圈记录。</summary>
        private void BeginRecording(
            long nowTicks, double thetaRad, double omegaRadPerSec,
            double phiRad, double phiDotRadPerSec)
        {
            bool usePending = _pendingReleaseValid;
            double releaseTheta = usePending ? _pendingReleaseThetaRad : thetaRad;
            double releaseOmega = usePending ? _pendingReleaseOmega : omegaRadPerSec;
            _pendingReleaseValid = false;

            _releaseTicks = nowTicks;
            _releaseThetaRad = ControlMath.IsFinite(releaseTheta) ? releaseTheta : thetaRad;
            _releaseOmega = releaseOmega;
            _releaseTimestampUtc = DateTime.UtcNow;
            _sweepDeg = 0;
            _forwardMaxDeg = 0;
            _lastProgressTicks = nowTicks;
            _lastTurningTicks = 0;
            _hasTurningOmega = false;
            _maxOmegaAbs = Math.Abs(releaseOmega);
            _invalidated = false;
            _invalidReason = null;
            _completed = false;

            // 释放方向 = 释放点相对「最后一次低于水平线」样本的转向；拿不到就用当拍角速度符号。
            _liftSign = 1;
            if (_hasBelowHorizontal)
            {
                double delta = _releaseThetaRad - _lastBelowHorizontalThetaRad;
                if (Math.Abs(delta) > 1e-6)
                    _liftSign = delta > 0 ? 1 : -1;
                else if (ControlMath.IsFinite(releaseOmega) && Math.Abs(releaseOmega) > 1e-6)
                    _liftSign = releaseOmega > 0 ? 1 : -1;
            }
            else if (ControlMath.IsFinite(releaseOmega) && Math.Abs(releaseOmega) > 1e-6)
            {
                _liftSign = releaseOmega > 0 ? 1 : -1;
            }

            Volatile.Write(ref _armed, 0);
            Volatile.Write(ref _state, StateRecording);
            AddKeyPoint(nowTicks, CaptureKeyPointKind.Release);
        }

        /// <summary>整圈记录推进：累计单向角、记关键点、判有效/失效。</summary>
        private CaptureEvent StepRecording(
            long nowTicks, double thetaRad, double phiRad, double phiDotRadPerSec)
        {
            double relDeg = (thetaRad - _releaseThetaRad) * (180.0 / Math.PI);
            _sweepDeg = _liftSign * relDeg;
            bool madeProgress = _sweepDeg > _forwardMaxDeg;
            if (madeProgress)
                _forwardMaxDeg = _sweepDeg;

            double deviationDeg = Math.Abs(ControlMath.WrapPi(thetaRad - Math.PI)) * (180.0 / Math.PI);
            if (deviationDeg > _maxDeviationDeg)
                _maxDeviationDeg = deviationDeg;

            double omegaW;
            bool hasOmega = TryWindowedOmega(nowTicks, SpeedWindowSeconds, out omegaW);
            double omegaAbs = hasOmega ? Math.Abs(omegaW) : 0;
            bool newSpeedPeak = omegaAbs > _maxOmegaAbs * 1.05;
            if (omegaAbs > _maxOmegaAbs) _maxOmegaAbs = omegaAbs;

            // 90° / 180° / 270° / 360° 跨越点。
            if (!_crossedQuarter && _sweepDeg >= 90.0)
            {
                _crossedQuarter = true;
                AddKeyPoint(nowTicks, CaptureKeyPointKind.Quarter);
            }
            if (!_crossedHalf && _sweepDeg >= 180.0)
            {
                _crossedHalf = true;
                AddKeyPoint(nowTicks, CaptureKeyPointKind.Half);
            }
            if (!_crossedThreeQuarter && _sweepDeg >= 270.0)
            {
                _crossedThreeQuarter = true;
                AddKeyPoint(nowTicks, CaptureKeyPointKind.ThreeQuarter);
            }

            // 极值点：窗口 ω 变号且幅度够大、间隔够久。
            if (hasOmega && _hasTurningOmega &&
                _lastTurningOmega * omegaW < 0 &&
                Math.Abs(_lastTurningOmega) >= TurningOmegaRadPerSec &&
                Math.Abs(omegaW) >= TurningOmegaRadPerSec &&
                (nowTicks - _lastTurningTicks) / (double)Stopwatch.Frequency >= TurningMinSpacingSeconds)
            {
                AddKeyPoint(nowTicks, CaptureKeyPointKind.TurningPoint);
                _lastTurningTicks = nowTicks;
            }
            if (hasOmega)
            {
                _lastTurningOmega = omegaW;
                _hasTurningOmega = true;
            }

            // 前进量刷新时间：判停滞用（只有真正刷新最大累计角才算「还在前进」）。
            if (madeProgress)
                _lastProgressTicks = nowTicks;

            double elapsedSec = (nowTicks - _releaseTicks) / (double)Stopwatch.Frequency;

            // 有效：朝释放方向累计满 360°。
            if (_sweepDeg >= FullTurnDeg)
            {
                _completed = true;
                _completeTicks = nowTicks;
                AddKeyPoint(nowTicks, CaptureKeyPointKind.FullTurn);
                return CaptureEvent.Completed;
            }

            // 失效：方向反转（左侧来、左侧回）。
            double retreat = _forwardMaxDeg - _sweepDeg;
            if (retreat >= ReversalVetoDeg)
            {
                string reason;
                if (_forwardMaxDeg < 1.0)
                {
                    reason = string.Format(
                        "释放后立刻朝反方向走（当前累计 {0:F1}°，t={1:F2}s，抬升方向为 {2}），" +
                        "「左侧来、左侧回」判失效，未绕满 360°。",
                        _sweepDeg, elapsedSec, _liftSign > 0 ? "正" : "反");
                }
                else
                {
                    reason = string.Format(
                        "释放后方向反转：最大累计 {0:F1}°（{1}），t={2:F2}s 回落到 {3:F1}°，反转量 {4:F1}° ≥ {5:F0}°；" +
                        "「左侧来、左侧回」判失效，未绕满 360°。",
                        _forwardMaxDeg, _liftSign > 0 ? "正向" : "反向", elapsedSec, _sweepDeg, retreat, ReversalVetoDeg);
                }
                Invalidate(nowTicks, reason);
                return CaptureEvent.Invalidated;
            }

            // 失效：停滞（不再前进）。
            if ((nowTicks - _lastProgressTicks) / (double)Stopwatch.Frequency >= StallSeconds)
            {
                Invalidate(nowTicks, string.Format(
                    "释放后 {0:F1}s 没有继续前进（累计 {1:F1}°，最大 {2:F1}°），判停滞失效，未绕满 360°。" +
                    "检查摆杆是否被挡、摩擦过大或释放力矩不足，再重新捕获。",
                    StallSeconds, _sweepDeg, _forwardMaxDeg));
                return CaptureEvent.Invalidated;
            }

            // 失效：超时。
            if (elapsedSec >= RevolutionTimeoutSeconds)
            {
                Invalidate(nowTicks, string.Format(
                    "释放后 {0:F0}s 仍未绕满一圈（累计 {1:F1}°，最大 {2:F1}°），判超时失效。" +
                    "确认摆杆释放方向正确、没有被线缆 / 限位阻挡，再重新捕获。",
                    RevolutionTimeoutSeconds, _sweepDeg, _forwardMaxDeg));
                return CaptureEvent.Invalidated;
            }

            // 最大速度点：只在刷新 5% 以上时记一个新点，避免每拍都记。
            if (hasOmega && newSpeedPeak && omegaAbs > 0.35)
                AddKeyPoint(nowTicks, CaptureKeyPointKind.MaxSpeed);

            return CaptureEvent.None;
        }

        /// <summary>
        /// 收走本次捕获的记录并清状态。返回 false = 没有可落盘的记录（从未释放且没有失效原因）。
        /// <paramref name="canceled"/> = 用户主动取消（记录里会写「用户取消」）。
        /// </summary>
        public bool EndAttempt(long nowTicks, bool canceled, out CaptureRecord record)
        {
            record = new CaptureRecord();
            int state = Volatile.Read(ref _state);
            bool wasRecording = state == StateRecording;
            if (!wasRecording && !_invalidated && !_completed)
            {
                ResetSession();
                return false;
            }

            bool released = wasRecording || ControlMath.IsFinite(_releaseThetaRad);
            bool valid = _completed && !_invalidated;
            string reason = _invalidReason;
            if (!valid && string.IsNullOrEmpty(reason))
                reason = canceled ? "用户取消（未绕满一圈）" : "未能完成一整圈";

            double endSeconds = released
                ? (nowTicks - _releaseTicks) / (double)Stopwatch.Frequency
                : 0;
            if (endSeconds < 0) endSeconds = 0;

            record.TimestampUtc = released ? _releaseTimestampUtc : _startTimestampUtc;
            record.StartTimestampUtc = _startTimestampUtc;
            record.ReleaseThetaDeg = released ? _releaseThetaRad * (180.0 / Math.PI) : double.NaN;
            record.ReleaseOmegaRadPerSec = released ? _releaseOmega : double.NaN;
            record.Valid = valid;
            record.Caught = valid;
            record.InvalidReason = valid ? null : reason;
            record.StartThetaDeg = _startThetaRad * (180.0 / Math.PI);
            record.StartPendulumCount = _startPendulumCount;
            record.LiftSeconds = released ? (_releaseTicks - _startTicks) / (double)Stopwatch.Frequency : 0;
            record.RevolutionSeconds = endSeconds;
            record.HoldSeconds = endSeconds;
            record.SweepDeg = _sweepDeg;
            record.MaxSweepDeg = _forwardMaxDeg;
            record.LiftSign = _liftSign;
            record.MaxOmegaRadPerSec = _maxOmegaAbs;
            record.MaxDeviationDeg = _maxDeviationDeg;
            record.CaptureDelaySeconds = valid
                ? (_completeTicks - _releaseTicks) / (double)Stopwatch.Frequency
                : double.NaN;
            record.Fell = false;

            // 结束点：失效时 Invalidate 已记过 End；有效时 FullTurn 就是结束点，不重复记同一拍。
            if (released)
            {
                CaptureKeyPointKind lastKind = _keyPoints.Count > 0
                    ? _keyPoints[_keyPoints.Count - 1].Kind
                    : CaptureKeyPointKind.End;
                if (lastKind != CaptureKeyPointKind.End && lastKind != CaptureKeyPointKind.FullTurn)
                    AddKeyPoint(nowTicks, CaptureKeyPointKind.End, allowOverflow: true);
            }

            record.KeyPoints = _keyPoints.Count == 0 ? null : _keyPoints.ToArray();
            ResetSession();
            return true;
        }

        /// <summary>判失效并记原因（只生效一次）。</summary>
        private void Invalidate(long nowTicks, string reason)
        {
            if (_invalidated) return;
            _invalidated = true;
            _invalidReason = reason;
            AddKeyPoint(nowTicks, CaptureKeyPointKind.End, allowOverflow: true);
        }

        /// <summary>
        /// 由引擎在捕获过程中发现实测越界等硬失效时写入**真实原因**
        /// （EndAttempt 的通用“未能完成一整圈”会掩盖越界，现场无法追溯）。
        /// 幂等：已有失效原因时保留第一条。
        /// </summary>
        internal void InvalidateAttempt(long nowTicks, string reason)
        {
            Invalidate(nowTicks, reason);
        }

        /// <summary>记一个关键点（超过上限的普通点直接丢，End / FullTurn 允许超一条）。</summary>
        private void AddKeyPoint(long nowTicks, CaptureKeyPointKind kind, bool allowOverflow = false)
        {
            if (!allowOverflow && _keyPoints.Count >= CaptureRecord.MaxKeyPoints)
                return;

            CaptureKeyPoint point = new CaptureKeyPoint();
            point.TimeSeconds = (nowTicks - _startTicks) / (double)Stopwatch.Frequency;
            point.Kind = kind;
            point.ThetaDeg = LastThetaRad * (180.0 / Math.PI);
            point.SweepDeg = _sweepDeg;
            point.ArmDeg = LastPhiRad * (180.0 / Math.PI);
            point.ArmOmegaRadPerSec = LastPhiDotRadPerSec;

            double omega;
            if (TryWindowedOmega(nowTicks, SpeedWindowSeconds, out omega))
            {
                point.HasDerivative = true;
                point.OmegaRadPerSec = omega;
                double omegaThen;
                if (TryWindowedOmega(nowTicks - (long)(AlphaAnchorSeconds * Stopwatch.Frequency),
                        SpeedWindowSeconds, out omegaThen))
                {
                    point.AlphaRadPerSec2 = (omega - omegaThen) / AlphaAnchorSeconds;
                }
            }

            _keyPoints.Add(point);
        }

        // ==================== 采样环 / 窗口差分 ====================

        private double LastThetaRad;
        private double LastPhiRad;
        private double LastPhiDotRadPerSec;

        /// <summary>把当拍样本压进环（Step 里已经调用；AddKeyPoint 依赖最近一次 Push）。</summary>
        private void PushSample(long nowTicks, double thetaRad)
        {
            LastThetaRad = ControlMath.IsFinite(thetaRad) ? thetaRad : LastThetaRad;
            _ringTicks[_ringHead] = nowTicks;
            _ringThetaRad[_ringHead] = LastThetaRad;
            _ringHead = (_ringHead + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }

        /// <summary>
        /// 以 anchorTicks 为终点、往前 windowSeconds 的窗口平均角速度 [rad/s]。
        /// 样本不够（少于窗口一半）返回 false —— 不拿 1 拍差分冒充。
        /// </summary>
        private bool TryWindowedOmega(long anchorTicks, double windowSeconds, out double omega)
        {
            omega = 0;
            if (_ringCount < 3) return false;

            int newest = (_ringHead - 1 + RingSize) % RingSize;
            int idx = newest;
            int steps = 0;
            while (_ringTicks[idx] > anchorTicks && steps < _ringCount - 1)
            {
                idx = (idx - 1 + RingSize) % RingSize;
                steps++;
            }
            if (_ringTicks[idx] > anchorTicks) return false;

            long target = _ringTicks[idx] - (long)(windowSeconds * Stopwatch.Frequency);
            int baseIdx = idx;
            for (int k = 1; k < _ringCount; k++)
            {
                int cand = (idx - k + RingSize * 2) % RingSize;
                baseIdx = cand;
                if (_ringTicks[cand] <= target) break;
            }
            if (baseIdx == idx) return false;

            double span = (_ringTicks[idx] - _ringTicks[baseIdx]) / (double)Stopwatch.Frequency;
            if (span < windowSeconds * 0.5 || !(span > 0)) return false;
            omega = (_ringThetaRad[idx] - _ringThetaRad[baseIdx]) / span;
            return ControlMath.IsFinite(omega);
        }

        // ==================== 小工具 ====================

        /// <summary>本拍推进用的秒数（首拍 = 0，异常大间隔按 50 ms 截断）。</summary>
        private double StepSeconds(long nowTicks)
        {
            if (_lastTicks == 0)
            {
                _lastTicks = nowTicks;
                return 0;
            }
            double dt = (nowTicks - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = nowTicks;
            if (!(dt > 0)) return 0;
            if (dt > 0.05) dt = 0.05;
            return dt;
        }

        /// <summary>
        /// 跟踪摆杆计数是否变化过（冻结判据），并同步维护「自上次计数变化以来的摆臂行程窗口」。
        /// 摆杆计数一变（严格不等）就重置窗口 —— 只要计数还会跟着动，就不是编码器失效。
        /// </summary>
        private void UpdateCountLiveness(long nowTicks, int pendulumCount, int armPositionCount)
        {
            if (!_armTravelPrimed)
            {
                _armTravelPrimed = true;
                _armTravelLow = armPositionCount;
                _armTravelHigh = armPositionCount;
            }

            if (pendulumCount != _lastPendulumCount)
            {
                _lastPendulumCount = pendulumCount;
                _countLastChangeTicks = nowTicks;
                _armTravelLow = armPositionCount;
                _armTravelHigh = armPositionCount;
            }
            else if (_countLastChangeTicks == 0)
            {
                _countLastChangeTicks = nowTicks;
                _armTravelLow = armPositionCount;
                _armTravelHigh = armPositionCount;
            }
            else
            {
                if (armPositionCount < _armTravelLow) _armTravelLow = armPositionCount;
                if (armPositionCount > _armTravelHigh) _armTravelHigh = armPositionCount;
            }
        }

        /// <summary>
        /// 实测摆臂计数越过软限位 + 余量 = 立即停（目标钳位管不到实测越界）。
        /// 判据只在 counts 域：<paramref name="armPositionCount"/> 与绝对限位直接比较，
        /// 余量由引擎按摆臂一圈计数换算成 counts 传入（保证不同机型的容差量级一致）。
        /// </summary>
        private static bool IsBeyondSoftLimit(int armPositionCount, SafetyLimits limits, int marginCount)
        {
            if (marginCount < 0) marginCount = 0;
            return armPositionCount < (long)limits.ArmMinCount - marginCount ||
                   armPositionCount > (long)limits.ArmMaxCount + marginCount;
        }
    }
}
