using System;
using System.Collections.Generic;
using System.Globalization;

namespace FurutaPendulum.Control
{
    /// <summary>摆杆 0° 点的可用性分类（供上层决定能否起摆 / 平衡，不参与控制律）。</summary>
    public enum PendulumZeroIssue
    {
        /// <summary>0° 点已保存；（给了当前 raw 时）与当前 raw 一致。</summary>
        Ok,
        /// <summary>文件里没有摆杆 0° 点（从未采集，或只存了 360° 点）。</summary>
        NotCalibrated,
        /// <summary>当前 raw 与保存的 0° 点不一致（掉电 / 换机械后 raw 坐标系变了）。</summary>
        RawMismatch
    }

    /// <summary>摆杆 0° / 360° 两点标定的成对性分类（供上层判断能不能把这份标定当成「可运行标定」）。</summary>
    public enum PendulumPairIssue
    {
        /// <summary>0° 与 360° 两点都在，且 |360−0| 是合法的正计数：一圈的量可由两点派生。</summary>
        Complete,
        /// <summary>只有 360° 点，缺 0°：θ 没有零点参考。</summary>
        MissingZero,
        /// <summary>只有 0° 点，缺 360°：一圈的量没有第二点参考。</summary>
        Missing360,
        /// <summary>两点都没有。</summary>
        MissingBoth,
        /// <summary>两点计数相同（|360−0| = 0）：一圈的量无法派生。</summary>
        SameCount,
        /// <summary>两点计数差超出 int32（不可能是同一圈的两点）。</summary>
        SpanOverflow
    }

    /// <summary>
    /// 摆杆 0° / 360° 两点标定的一次裁决结果（只读视图：不改标定、不移动电机、不改引擎状态）。
    /// 上层（起摆前置校验 / 设备参数卡 / 参数载入提示）统一查这个，不要各自再写一遍成对判据。
    /// </summary>
    public sealed class PendulumPairCheck
    {
        /// <summary>成对性分类。</summary>
        public PendulumPairIssue Issue;

        /// <summary>文件里是否真的有 0° 点。</summary>
        public bool HasZero;

        /// <summary>文件里是否真的有 360° 点。</summary>
        public bool Has360;

        /// <summary>一圈的量 = |360° − 0°| [counts]；只有 <see cref="IsComplete"/> 时有效，否则为 0。</summary>
        public int SpanCounts;

        /// <summary>中文原因（Complete 时为 null）；缺哪一半必须点名。</summary>
        public string Reason;

        /// <summary>两点齐全且能派生出一圈的量 = 这份标定可以当作「可运行标定」。</summary>
        public bool IsComplete
        {
            get { return Issue == PendulumPairIssue.Complete; }
        }

        /// <summary>缺哪一半的短语（完成时空串），供 UI 一行显示。</summary>
        public string MissingText
        {
            get
            {
                switch (Issue)
                {
                    case PendulumPairIssue.MissingZero: return "缺 0°";
                    case PendulumPairIssue.Missing360: return "缺 360°";
                    case PendulumPairIssue.MissingBoth: return "0° / 360° 都未采集";
                    case PendulumPairIssue.SameCount: return "0° 与 360° 计数相同";
                    case PendulumPairIssue.SpanOverflow: return "两点计数差超出 int32";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>写进 furuta_params.json 的机器可读标记（完成时空串，不写这个键）。</summary>
        public string MissingToken
        {
            get
            {
                switch (Issue)
                {
                    case PendulumPairIssue.MissingZero: return "zero";
                    case PendulumPairIssue.Missing360: return "360";
                    case PendulumPairIssue.MissingBoth: return "zero+360";
                    case PendulumPairIssue.SameCount: return "same_count";
                    case PendulumPairIssue.SpanOverflow: return "span_overflow";
                    default: return null;
                }
            }
        }
    }

    /// <summary>
    /// 摆杆 0° 点的一次裁决结果（只读视图：不改标定、不移动电机、不改引擎状态）。
    /// 上层在起摆 / 平衡前查 <see cref="CanStartSwing"/>；false 时把 <see cref="Reason"/> 原样提示用户。
    /// </summary>
    public sealed class PendulumZeroCheck
    {
        /// <summary>裁决分类。</summary>
        public PendulumZeroIssue Issue;

        /// <summary>文件里保存的 0° 原始计数（NotCalibrated 时无意义）。</summary>
        public int SavedZeroCount;

        /// <summary>调用方给的当前 raw（摆杆原始计数）；没给 = null。</summary>
        public int? CurrentRawCount;

        /// <summary>当前 raw − 保存零点 [counts]（32 位计数差，与引擎 UpdateCalibrationReview 同口径）；没比过 = 0。</summary>
        public long DeltaCounts;

        /// <summary>中文原因（Ok 时为 null）。</summary>
        public string Reason;

        /// <summary>是否可以起摆 / 平衡（= <see cref="PendulumZeroIssue.Ok"/>）。</summary>
        public bool CanStartSwing
        {
            get { return Issue == PendulumZeroIssue.Ok; }
        }
    }

    /// <summary>
    /// 能量泵参数的「工程口径」持久化视图。控制层里泵幅是加速度幅 [rad/s²]；
    /// 现场更好理解的是「等效摆臂角幅值」[deg]，两者换算：A_rad = A_acc / (2πf)²。
    /// 本类只做换算与校验，不参与控制律。
    /// </summary>
    public sealed class PumpParameters
    {
        /// <summary>能量误差 → 摆臂加速度增益 [rad/s² per J]。</summary>
        public double Ke;

        /// <summary>泵幅的等效摆臂角幅值 [deg]。</summary>
        public double PumpAmpDeg;

        /// <summary>泵频 [Hz]。</summary>
        public double PumpHz;

        /// <summary>泵相位偏移 [deg]。</summary>
        public double PhaseOffsetDeg;

        /// <summary>校验一组泵参数。失败时 reason 写清哪一项不合法。</summary>
        public bool IsValid(out string reason)
        {
            if (!ControlMath.IsFinite(Ke) || Ke <= 0)
            {
                reason = "k_e 必须是 >0 的有限数，单位 rad/s² per J。";
                return false;
            }
            if (!ControlMath.IsFinite(PumpHz) || PumpHz <= 0 || PumpHz > 100)
            {
                reason = "泵频必须是 (0,100] Hz 的有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(PumpAmpDeg) || PumpAmpDeg < 0 || PumpAmpDeg > 3600)
            {
                reason = "泵幅（等效摆臂角幅值）必须是 [0,3600] deg 的有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(PhaseOffsetDeg))
            {
                reason = "相位偏移必须是有限数，单位 deg。";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>把工程口径写回控制层的 SwingUpParams（加速度幅、泵频、相位弧度）。</summary>
        public void ApplyTo(ref SwingUpParams swing)
        {
            double omega = 2.0 * Math.PI * PumpHz;
            swing.Ke = Ke;
            swing.PumpFrequencyHz = PumpHz;
            swing.PumpAmplitude = DegToRad(PumpAmpDeg) * omega * omega;
            swing.PumpPhaseOffsetRad = DegToRad(NormalizeDegrees(PhaseOffsetDeg));
        }

        /// <summary>从控制层当前参数生成工程口径。泵频 ≤0 时泵幅记 0（无法换算，不猜）。</summary>
        public static PumpParameters From(SwingUpParams swing)
        {
            PumpParameters p = new PumpParameters();
            p.Ke = swing.Ke;
            p.PumpHz = swing.PumpFrequencyHz;
            p.PhaseOffsetDeg = NormalizeDegrees(swing.PumpPhaseOffsetRad * (180.0 / Math.PI));
            double omega = 2.0 * Math.PI * swing.PumpFrequencyHz;
            if (omega > 1e-9 && ControlMath.IsFinite(swing.PumpAmplitude))
                p.PumpAmpDeg = swing.PumpAmplitude / (omega * omega) * (180.0 / Math.PI);
            else
                p.PumpAmpDeg = 0;
            return p;
        }

        /// <summary>起手值：与控制层 SwingUpParams.CreateDefault() 对齐（泵幅 0 = 关泵，泵频 1 Hz）。</summary>
        public static PumpParameters CreateDefault()
        {
            SwingUpParams swing = SwingUpParams.CreateDefault();
            return From(swing);
        }

        internal static double DegToRad(double deg)
        {
            return deg * Math.PI / 180.0;
        }

        internal static double NormalizeDegrees(double deg)
        {
            if (!ControlMath.IsFinite(deg)) return 0;
            double d = deg % 360.0;
            if (d < 0) d += 360.0;
            return d;
        }
    }

    /// <summary>
    /// 系统辨识结果（持久化视图）。没有结果时 <see cref="NaturalFreqHz"/> = 0 / <see cref="Confidence"/> = 0，
    /// 读回后按「无结果」处理，不拿旧值冒充本次辨识。
    /// </summary>
    public sealed class IdentifyParameters
    {
        /// <summary>自然频率 [Hz]。</summary>
        public double NaturalFreqHz;

        /// <summary>阻尼比；不可辨识 = NaN。</summary>
        public double DampingZeta = double.NaN;

        /// <summary>静止角偏置 [deg]；不可辨识 = NaN。</summary>
        public double ThetaOffsetDeg = double.NaN;

        /// <summary>可捕获释放角速度上限 [deg/s]；不可辨识 = NaN。</summary>
        public double CatchRateLimitDegPerSec = double.NaN;

        /// <summary>0–1 置信度。</summary>
        public double Confidence;

        /// <summary>辨识时间（UTC）。</summary>
        public DateTime? IdentifiedAtUtc;

        /// <summary>k_e 初值的来源说明。</summary>
        public string KeSource;

        public int HalfPeriodSamples;
        public int ExtremaSamples;
        public int CaptureSamples;

        /// <summary>辨识推荐的泵频 [Hz]；0 = 未知。</summary>
        public double RecommendedPumpHz;

        /// <summary>辨识推荐的泵幅（等效摆臂角）[deg]。</summary>
        public double RecommendedPumpAmpDeg;

        /// <summary>推荐泵幅是否可辨识（没有泵能量样本时为 false，不编数）。</summary>
        public bool HasRecommendedPumpAmp;

        /// <summary>辨识推荐的 k_e 初值 [rad/s² per J]；0 = 未知。</summary>
        public double RecommendedKe;

        /// <summary>是否有可用结果。</summary>
        public bool HasResult
        {
            get { return NaturalFreqHz > 0 && Confidence > 0; }
        }

        /// <summary>校验。空结果（全部 0 / NaN）也算合法，表示「还没辨识过」。</summary>
        public bool IsValid(out string reason)
        {
            if ((NaturalFreqHz == 0 || double.IsNaN(NaturalFreqHz)) && Confidence == 0)
            {
                reason = null;
                return true;
            }
            if (!ControlMath.IsFinite(NaturalFreqHz) || NaturalFreqHz <= 0 || NaturalFreqHz > 100)
            {
                reason = "naturalFreqHz 必须是 (0,100] Hz 的有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(Confidence) || Confidence < 0 || Confidence > 1)
            {
                reason = "confidence 必须在 [0,1]。";
                return false;
            }
            if (!IsFiniteOrNaN(DampingZeta) || (!double.IsNaN(DampingZeta) && DampingZeta < 0))
            {
                reason = "dampingZeta 必须是 ≥0 的有限数或 NaN。";
                return false;
            }
            if (!IsFiniteOrNaN(ThetaOffsetDeg))
            {
                reason = "thetaOffsetDeg 必须是有限数或 NaN。";
                return false;
            }
            if (!IsFiniteOrNaN(CatchRateLimitDegPerSec) ||
                (!double.IsNaN(CatchRateLimitDegPerSec) && CatchRateLimitDegPerSec < 0))
            {
                reason = "catchRateLimitDegPerSec 必须是 ≥0 的有限数或 NaN。";
                return false;
            }
            if (!ControlMath.IsFinite(RecommendedPumpHz) || RecommendedPumpHz < 0 || RecommendedPumpHz > 100)
            {
                reason = "recommendedPumpHz 必须是 [0,100] Hz 的有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(RecommendedPumpAmpDeg) ||
                RecommendedPumpAmpDeg < 0 || RecommendedPumpAmpDeg > 3600)
            {
                reason = "recommendedPumpAmpDeg 必须是 [0,3600] deg 的有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(RecommendedKe) || RecommendedKe < 0)
            {
                reason = "recommendedKe 必须是 ≥0 的有限数。";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool IsFiniteOrNaN(double value)
        {
            return double.IsNaN(value) || ControlMath.IsFinite(value);
        }

        /// <summary>从辨识结果生成持久化视图。样本不足时按「无结果」落盘（不把 NaN 写进文件）。</summary>
        public static IdentifyParameters From(IdentificationResult result)
        {
            IdentifyParameters p = new IdentifyParameters();
            p.HalfPeriodSamples = result.HalfPeriodSamples;
            p.ExtremaSamples = result.ExtremaSamples;
            p.CaptureSamples = result.CaptureSamples;
            p.KeSource = result.KeSource;
            if (!result.Sufficient)
            {
                // 保留样本数 / 来源便于溯源，但主结果归零：读回就是「还没辨识过」。
                p.NaturalFreqHz = 0;
                p.DampingZeta = double.NaN;
                p.ThetaOffsetDeg = double.NaN;
                p.CatchRateLimitDegPerSec = double.NaN;
                p.Confidence = 0;
                p.IdentifiedAtUtc = null;
                return p;
            }
            p.NaturalFreqHz = result.NaturalFreqHz;
            p.DampingZeta = result.DampingZeta;
            p.ThetaOffsetDeg = result.ThetaOffsetDeg;
            p.CatchRateLimitDegPerSec = result.CatchRateLimitDegPerSec;
            p.Confidence = result.Confidence;
            p.IdentifiedAtUtc = DateTime.UtcNow;
            p.RecommendedPumpHz = ControlMath.IsFinite(result.PumpHz) && result.PumpHz > 0 ? result.PumpHz : 0;
            p.HasRecommendedPumpAmp = !double.IsNaN(result.PumpAmpDeg) && ControlMath.IsFinite(result.PumpAmpDeg);
            p.RecommendedPumpAmpDeg = p.HasRecommendedPumpAmp ? result.PumpAmpDeg : 0;
            p.RecommendedKe = ControlMath.IsFinite(result.Ke) && result.Ke > 0 ? result.Ke : 0;
            return p;
        }
    }

    /// <summary>
    /// 摆臂软限位（持久化视图）。**唯一单位 = 当前 raw 坐标系下的 0x6064 计数**：不存角度、不按摆臂每转计数换算。
    /// 用户只给过「摆臂一圈多少计数」，从没给过行程对应的机械角度；任何 deg / rad 的行程口径都是自己编的，
    /// 所以这里只有 <see cref="MinCounts"/> / <see cref="MaxCounts"/>，且它们就是控制层的权威值。
    /// 左右可以不对称、零点可以不在中间，都照原样保存。
    /// <para>「绝对计数」不等于跨掉电的物理绝对位置：EC1 开环步进的 0x6064 每次上电都要重新与机械零点
    /// 对齐；raw 坐标一变，这里的一对数必须与零点一起规整化或重新采集。</para>
    /// </summary>
    public sealed class SoftLimitParameters
    {
        /// <summary>左限位 = 当前 raw 坐标系下的 0x6064 计数（不是相对 0 点的偏移，也不保证跨掉电同物理位置）。</summary>
        public int MinCounts = SafetyLimits.DefaultArmMinCount;

        /// <summary>右限位 = 当前 raw 坐标系下的 0x6064 计数。</summary>
        public int MaxCounts = SafetyLimits.DefaultArmMaxCount;

        /// <summary>校验：只要求严格 min &lt; max（int32 全域，不设任何角度 / 量程上限）。</summary>
        public bool IsValid(out string reason)
        {
            if (!(MinCounts < MaxCounts))
            {
                reason = string.Format(
                    "软限位必须是 min_counts < max_counts（当前 {0} / {1}，单位 = 当前 raw 坐标系下的 0x6064 计数）。",
                    MinCounts, MaxCounts);
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 清空左右限位，回到「尚未采集」占位（int32 两端）。**只清限位，不碰零点/每圈量/减速比**。
        /// 清空后自动运动必须由上层拒绝，直到重新采集左/零/右三点。
        /// </summary>
        public void Clear()
        {
            MinCounts = SafetyLimits.DefaultArmMinCount;
            MaxCounts = SafetyLimits.DefaultArmMaxCount;
        }

        /// <summary>从控制层安全阈值直接复制 counts（同一份真相，无换算）。</summary>
        public static SoftLimitParameters From(SafetyLimits limits)
        {
            SoftLimitParameters p = new SoftLimitParameters();
            p.MinCounts = limits.ArmMinCount;
            p.MaxCounts = limits.ArmMaxCount;
            return p;
        }

        /// <summary>默认软限位 = 控制层默认（全量占位 = 尚未采集）。</summary>
        public static SoftLimitParameters CreateDefault()
        {
            return new SoftLimitParameters();
        }
    }

    /// <summary>
    /// 本次进程的标定会话标记。增量编码器（EC1 开环步进的 0x6064 / GC1602 的 AB 计数）都没有可依赖的
    /// 绝对多圈存储：程序无法仅凭「文件里的 counts」证明它仍对应当前物理位置。因此持久化标定必须带上
    /// 「它是在哪一个进程里被采集 / 规整化 / 确认的」这一事实，读回时只认**本次进程**写下的标记；
    /// 别的进程留下的标记一律视为「未在本次进程确认」，由上层要求重新标定或平移（规整化），
    /// 绝不拿旧值直接使能。标记串本身可读（开机时刻 / 进程启动时刻 / pid），便于现场对日志。
    /// </summary>
    public static class CalibrationSession
    {
        private static readonly string Tag = BuildTag();

        /// <summary>本次进程的会话标记（进程内恒定；进程重启即不同）。</summary>
        public static string CurrentTag
        {
            get { return Tag; }
        }

        /// <summary>标记是否由本次进程写下。空 / null 一律不算（旧文件没有这个键 = 未确认）。</summary>
        public static bool MatchesCurrent(string tag)
        {
            return !string.IsNullOrEmpty(tag) && string.Equals(tag, Tag, StringComparison.Ordinal);
        }

        /// <summary>供日志 / 界面说明「本次进程」是哪一次启动。</summary>
        public static string DescribeCurrent()
        {
            return "本次进程 " + Tag;
        }

        private static string BuildTag()
        {
            int pid;
            DateTime processStartUtc;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    pid = process.Id;
                    processStartUtc = process.StartTime.ToUniversalTime();
                }
            }
            catch (Exception ex)
            {
                // 拿不到进程启动时间不影响判据（同一进程内仍然恒定），只影响标记的可读性；
                // 用「现在」当启动时刻，并留一条 Debug 痕迹，不编造 pid。
                System.Diagnostics.Debug.WriteLine("读取进程启动时间失败，会话标记退回当前时刻：" + DiagText.Describe(ex));
                pid = 0;
                processStartUtc = DateTime.UtcNow;
            }
            // 开机时刻由 TickCount 反推：TickCount 约 24.9 天回绕，回绕后这里会算错 —— 但标记的
            // 正确性只依赖「进程内恒定 + 跨进程不同」，开机时刻只作可读溯源，错了也不会放行旧标定。
            DateTime bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount);
            return "boot=" + FormatIso(bootUtc) + ";proc=" + FormatIso(processStartUtc) +
                   ";pid=" + pid.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatIso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 机器标定（持久化视图）。pendulumCpr 是控制层真正用的 θ 标定；armCountsPerRev 是界面换算
    /// 与工程假设值，控制层的摆臂权威标定仍是 --arm-cpr / 0x2102 × 减速比；armGearRatio 只在下次
    /// 启动构造控制层时生效（入口 Program 在未给 --arm-gear 时读回）。
    /// <para>
    /// 这里的位置点（摆臂零点、摆杆 0°/360°）全部存**当前 0x6064 / 计数通道坐标系下的原始计数**。
    /// 「绝对计数」只表示计算方式，不等于「掉电后仍是同一物理位置」：EC1 是开环步进、GC1602 是 AB
    /// 增量计数，两者都没有可依赖的绝对多圈存储。上电后发现 raw 坐标变了，只能由用户明确
    /// 「规整化 / 重新采集」，绝不自动猜偏移（见 <see cref="TryNormalizeArmPose"/> /
    /// <see cref="TryNormalizePendulumPose"/>）。
    /// </para>
    /// </summary>
    public sealed class CalibrationParameters
    {
        /// <summary>摆杆每转计数（用户指定出厂 2400）。0 = 未标定。新口径由 0°/360° 两点标定派生。</summary>
        public double PendulumCpr = 2400.0;

        /// <summary>用 0°/360° 两个标定点做一圈标定：成功返回 true，并把 <see cref="PendulumCpr"/> 更新为 |360−0|。</summary>
        public bool TryCalibratePendulumCpr(out int countsPerRev)
        {
            string reason;
            return TryCalibratePendulumCpr(out countsPerRev, out reason);
        }

        /// <summary>
        /// 带原因的版本：缺哪一半 / 为什么不能派生一圈计数必须能直接告诉用户，
        /// 不能只返回 false 让调用方自己猜。
        /// </summary>
        public bool TryCalibratePendulumCpr(out int countsPerRev, out string reason)
        {
            countsPerRev = 0;
            PendulumPairCheck pair = CheckPendulumPair();
            if (!pair.IsComplete)
            {
                reason = "摆杆 0°/360° 两点标定不完整：" + pair.MissingText +
                         (string.IsNullOrEmpty(pair.Reason) ? "。" : "（" + pair.Reason + "）。") +
                         "先把摆杆分别停到 0° 与 360° 两个点并采集，再派生一圈计数。";
                return false;
            }
            reason = null;
            countsPerRev = pair.SpanCounts;
            PendulumCpr = countsPerRev;
            return true;
        }

        /// <summary>
        /// 摆杆 0° / 360° 两点是否成对（唯一判据，别在上层另写一份）。
        /// <para>
        /// 只有一半时**不是**可运行标定：缺 0° = θ 没有零点参考（整圈偏差会被当成 0 附近的小角度）；
        /// 缺 360° = 一圈的量没有第二点参考（沿用文件里的 pendulum_cpr 只是兼容值，不是现场标定结果）。
        /// 两点计数相同 / 差超出 int32 同样不能派生一圈的量。现场允许先只采一半（采集动作不被阻止），
        /// 但起摆 / 平衡前置校验必须拒绝，并把缺的是哪一半原样告诉用户。
        /// </para>
        /// </summary>
        public PendulumPairCheck CheckPendulumPair()
        {
            return CheckPendulumPair(this);
        }

        /// <summary>null 安全入口：没有 calibration 段（calibration 传 null）= 两点都未标定。</summary>
        public static PendulumPairCheck CheckPendulumPair(CalibrationParameters calibration)
        {
            PendulumPairCheck check = new PendulumPairCheck();
            check.HasZero = calibration != null && calibration.HasPendulumZero;
            check.Has360 = calibration != null && calibration.HasPendulum360;
            if (!check.HasZero && !check.Has360)
            {
                check.Issue = PendulumPairIssue.MissingBoth;
                check.Reason = "摆杆 0° / 360° 两点都未标定：θ 没有零点参考（整圈偏差会被当成 0 附近的小角度），"
                    + "一圈的量也没有标定参考；起摆 / 平衡前必须先在自然下垂位采集 0°，再把摆杆整转一圈采集 360°。";
                return check;
            }
            if (!check.HasZero)
            {
                check.Issue = PendulumPairIssue.MissingZero;
                check.Reason = "摆杆标定缺 0° 点（只有 360°）：θ 没有零点参考（整圈偏差会被当成 0 附近的小角度），"
                    + "起摆 / 平衡前必须先在自然下垂位采集 0°；360° 点不用重采。";
                return check;
            }
            if (!check.Has360)
            {
                check.Issue = PendulumPairIssue.Missing360;
                check.Reason = "摆杆标定缺 360° 点（只有 0°）：一圈的量没有第二点参考，"
                    + "沿用文件里的 pendulum_cpr 只是兼容值、不是现场标定结果；"
                    + "把摆杆从 0° 整转一圈后采集 360°，两点齐全才算可运行标定。";
                return check;
            }

            long span = (long)calibration.Pendulum360Count - calibration.PendulumZeroCount;
            if (span < 0) span = -span;
            if (span == 0)
            {
                check.Issue = PendulumPairIssue.SameCount;
                check.Reason = "摆杆 0° 与 360° 的计数相同（|360−0| = 0）：一圈的量无法从两点派生，θ 换算会退化；"
                    + "确认 360° 确实转过一整圈后重新采集。";
                return check;
            }
            if (span > int.MaxValue)
            {
                check.Issue = PendulumPairIssue.SpanOverflow;
                check.Reason = string.Format(CultureInfo.InvariantCulture,
                    "摆杆 0° 与 360° 的计数差 {0} 超出 int32：两点不可能来自同一圈，重新采集 0° / 360°。", span);
                return check;
            }
            check.Issue = PendulumPairIssue.Complete;
            check.SpanCounts = (int)span;
            return check;
        }

        /// <summary>
        /// 清空全部「位置点」：摆臂零点 + 摆杆 0°/360°。**不清换算尺度** ——
        /// <see cref="PendulumCpr"/>（一圈的量）、<see cref="ArmCountsPerRev"/>、<see cref="ArmGearRatio"/>
        /// 保持原值，避免用户辛苦标的一圈量被顺手清掉。左右限位由 <see cref="SoftLimitParameters.Clear"/> 清。
        /// 同时清掉两组的「本次进程确认」标记：清空后必须重新采集，旧标记不能继续代表任何东西。
        /// 本方法只改内存视图，不移动电机，也不写文件。
        /// </summary>
        public void ClearPosePoints()
        {
            ArmZeroCount = 0;
            HasArmZeroCount = false;
            PendulumZeroCount = 0;
            HasPendulumZero = false;
            Pendulum360Count = 0;
            HasPendulum360 = false;
            ClearArmPoseSessionMark();
            ClearPendulumPoseSessionMark();
            CalibrationCleared = true;
        }

        /// <summary>清掉摆臂三点的「本次进程确认」标记（点位被清空 / 判定失效时用）。</summary>
        public void ClearArmPoseSessionMark()
        {
            ArmPoseSessionTag = null;
            ArmPoseCapturedAtUtc = null;
        }

        /// <summary>清掉摆杆两点的「本次进程确认」标记（点位被清空 / 判定失效时用）。</summary>
        public void ClearPendulumPoseSessionMark()
        {
            PendulumPoseSessionTag = null;
            PendulumPoseCapturedAtUtc = null;
        }

        /// <summary>
        /// 记录「摆臂三点（零点 + 左限位 + 右限位）是在本次进程里整体建立 / 确认的」。
        /// 只在三点同时被写成一套时调用（规整化 / 重新采集齐全并确认），不要单点采集就盖这个戳。
        /// </summary>
        public void MarkArmPoseCapturedInSession()
        {
            ArmPoseSessionTag = CalibrationSession.CurrentTag;
            ArmPoseCapturedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 记录「摆杆 0° / 360° 两点是在本次进程里整体建立 / 确认的」。只在两点成对时调用：
        /// 单采 0° 而保留上次开机留下的 360°，两点差（一圈的量）并不可信，不能盖这个戳。
        /// </summary>
        public void MarkPendulumPoseCapturedInSession()
        {
            PendulumPoseSessionTag = CalibrationSession.CurrentTag;
            PendulumPoseCapturedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 摆臂三点是否已在**本次进程**确认（会话标记匹配 + 三点齐全）。false = 文件里的旧 counts
        /// 无法证明对应当前物理位置，自动运动（使能 / 起摆 / 回零）必须先规整化或重新采集。
        /// </summary>
        public bool ArmPoseSessionVerified
        {
            get { return HasArmZeroCount && CalibrationSession.MatchesCurrent(ArmPoseSessionTag); }
        }

        /// <summary>
        /// 摆杆两点是否已在**本次进程**确认（会话标记匹配 + 0°/360° 成对）。只匹配 0° 不够：
        /// 上次开机留下的 360° 与这次采的 0° 不是同一坐标系，|360−0| 不可信。
        /// </summary>
        public bool PendulumPoseSessionVerified
        {
            get
            {
                return HasPendulumZero && HasPendulum360 &&
                       CalibrationSession.MatchesCurrent(PendulumPoseSessionTag);
            }
        }

        /// <summary>
        /// 规整化摆臂左/中/右：把**当前 0x6064** 作为新的零点，旧左/右限位按同一个
        /// <c>delta = currentRaw − 旧零点</c> 平移，因此左侧行程、右侧行程原样保留。
        /// <para>
        /// 这是**坐标平移**，不是重新采集机械限位：调用前必须把摆臂停在「应当成为零点」的
        /// 同一机械姿态；停在任意位置调用会把整个坐标系搬过去，三点虽然仍有序，但不再对应
        /// 真实机械边界。旧三点不完整/越序、或平移后超出 int32 时整体拒绝，不产生半套值。
        /// </para>
        /// <para>本方法只计算新值，不修改对象；调用方确认后一次性写入零点与左右限位。</para>
        /// </summary>
        public bool TryNormalizeArmPose(int currentRawCount, SoftLimitParameters limits,
            out int newMinCounts, out int newZeroCount, out int newMaxCounts, out string reason)
        {
            newMinCounts = 0;
            newZeroCount = 0;
            newMaxCounts = 0;
            if (!HasArmZeroCount)
            {
                reason = "尚未保存驱动臂零点（has_arm_zero_count=false）：先把驱动臂停到期望零点并采集「驱动臂零点」，再规整化。";
                return false;
            }
            if (limits == null)
            {
                reason = "没有软限位段，无法规整化左/中/右三点：先重新采集左限位、零点、右限位。";
                return false;
            }
            if (limits.MinCounts == SafetyLimits.DefaultArmMinCount ||
                limits.MaxCounts == SafetyLimits.DefaultArmMaxCount)
            {
                reason = "左右限位仍是「尚未采集」占位（int32 两端），不能规整化：先点动到实际左右边界并采集左/右限位。";
                return false;
            }
            if (!(limits.MinCounts < ArmZeroCount && ArmZeroCount < limits.MaxCounts))
            {
                reason = string.Format(
                    "摆臂三点不完整或越序（左 {0} / 零 {1} / 右 {2}，要求 左 < 零 < 右）："
                    + "先重新采集左/零/右，不能按每圈计数或固定角度推算。",
                    limits.MinCounts, ArmZeroCount, limits.MaxCounts);
                return false;
            }

            long delta = (long)currentRawCount - ArmZeroCount;
            long shiftedMin = (long)limits.MinCounts + delta;
            long shiftedMax = (long)limits.MaxCounts + delta;
            if (shiftedMin < int.MinValue || shiftedMin > int.MaxValue ||
                shiftedMax < int.MinValue || shiftedMax > int.MaxValue)
            {
                reason = string.Format(
                    "规整化后限位超出 int32 计数范围（当前位置 {0}，平移量 {1}）：保持原值，请重新采集三点。",
                    currentRawCount, delta);
                return false;
            }

            newMinCounts = (int)shiftedMin;
            newZeroCount = currentRawCount;
            newMaxCounts = (int)shiftedMax;
            reason = null;
            return true;
        }

        /// <summary>
        /// 规整化摆杆两点：把**当前计数**作为新的 0° 点；若已保存 360° 点，则按同一个
        /// <c>delta = currentRaw − 旧 0°点</c> 平移。两点差不变，所以 <see cref="PendulumCpr"/>
        /// （一圈的量）不需要改，也不会被清掉。
        /// <para>
        /// 调用前摆杆必须停在自然下垂位；停在任意角度调用等于把 θ=0 定在错误位置。
        /// 没有保存过 0° 点时拒绝（无法知道旧坐标系）；360° 未标定时只返回新 0°，由调用方
        /// 保留 has_pendulum_360=false。本方法只计算，不修改对象、不移动电机。
        /// </para>
        /// </summary>
        public bool TryNormalizePendulumPose(int currentRawCount,
            out int newZeroCount, out int new360Count, out string reason)
        {
            newZeroCount = 0;
            new360Count = 0;
            if (!HasPendulumZero)
            {
                reason = "尚未保存摆杆 0° 点（has_pendulum_zero=false）：先把摆杆放到自然下垂位并采集 0°，再规整化。";
                return false;
            }

            long delta = (long)currentRawCount - PendulumZeroCount;
            if (HasPendulum360)
            {
                long shifted360 = (long)Pendulum360Count + delta;
                if (shifted360 < int.MinValue || shifted360 > int.MaxValue)
                {
                    reason = string.Format(
                        "规整化后摆杆 360° 点超出 int32 计数范围（当前位置 {0}，平移量 {1}）：保持原值，请重新采集 0°/360°。",
                        currentRawCount, delta);
                    return false;
                }
                new360Count = (int)shifted360;
            }

            newZeroCount = currentRawCount;
            reason = null;
            return true;
        }

        /// <summary>
        /// 把「当前 raw 相对基准零点的角度」折到一圈以内再换算成等效角 [rad]，取值范围 (−π, π]。
        /// <para>
        /// 用途 = 「摆杆是否在自然下垂位」这一类**位置判定**。摆杆是增量计数：同一物理位置可以读成
        /// 相差整数圈的不同 raw（例如保存零点 raw=1000，上电后同一垂位读到 raw=3400，2400 counts/圈）。
        /// 直接拿累计角比较会把这种一圈整数倍的偏差当成「不在垂位」，从而拒绝一次正确的重标定；
        /// 反过来，拿累计角当判据也会在零点附近被整圈偏移骗过。因此判定一律在 counts 域按一圈取模，
        /// 再把等价残差换成角度 —— 与「计数等价性」口径一致（见调参规范 §2.3）。
        /// </para>
        /// <para>
        /// 分支点落在半圈处（零点 + 半圈 = 直立点）：重标定时的工作区在垂位附近，分支点不在工作区内，
        /// 不会出现「清零后角度跳到另一侧」。<paramref name="countsPerRev"/> ≤ 0 或非有限时返回 NaN，
        /// 调用方据此如实说明「未标定，无法程序化判定」。
        /// </para>
        /// </summary>
        public static double WrappedAngleRad(int rawCount, int zeroCount, double countsPerRev)
        {
            if (!ControlMath.IsFinite(countsPerRev) || countsPerRev <= 0)
                return double.NaN;
            int delta = ControlMath.CountDelta(unchecked((uint)rawCount), unchecked((uint)zeroCount));
            double turns = delta / countsPerRev;
            double fraction = turns - Math.Floor(turns);   // [0, 1)
            if (fraction > 0.5) fraction -= 1.0;           // (−0.5, 0.5]
            return fraction * ControlMath.TwoPi;
        }

        /// <summary>
        /// 摆杆 0° 点被重标定后，把 360° 点按**同一圈量**跟到新零点上：
        /// <c>new360 = newZero + (old360 − oldZero)</c>。两点差（一圈的量）原样保留，
        /// 因此 <see cref="PendulumCpr"/> 不需要用户重算；这同时处理了 raw 计数跨 0 / 跨整圈的情形。
        /// <para>
        /// 为什么必须跟随：0° 点一挪，文件里旧 360° 点就不再是「离新零点一圈」的位置；下次载入按
        /// |360−0| 派生的是一圈量，会让摆杆角度换算整体偏掉，表现为「清零后角度跳到另一侧 / 整圈算错」。
        /// </para>
        /// <para>
        /// 没有旧 0° 点（旧 360° 点没有参考对象）→ 作废 360° 点并要求重采；平移后越 int32 / 两点重合
        /// → 同样作废并说明原因。本方法只改内存视图，不移动电机、不写文件；调用方随后自行落盘。
        /// </para>
        /// </summary>
        /// <returns>重标定后 0°/360° 两点是否仍然成对可用。</returns>
        public bool RebasePendulum360AfterZero(int newZeroCount, out string note)
        {
            note = null;
            if (!HasPendulumZero)
            {
                // 没有旧 0° 点：旧 360° 点失去参考，不能猜它在新坐标系里的位置。
                if (HasPendulum360)
                {
                    note = "旧参数文件里有 360° 点但没有 0° 点（没有参考零点）：该 360° 点已作废，"
                        + "重新把摆杆停到 0° 与 360° 两点再采集。";
                    HasPendulum360 = false;
                    Pendulum360Count = 0;
                }
                HasPendulumZero = true;
                PendulumZeroCount = newZeroCount;
                return false;
            }

            long oldSpan = (long)Pendulum360Count - PendulumZeroCount;
            PendulumZeroCount = newZeroCount;
            HasPendulumZero = true;
            if (!HasPendulum360)
                return false;

            if (oldSpan == 0)
            {
                note = "旧 0°/360° 两点计数相同（|360−0|=0），一圈的量无法派生：360° 点已作废，请重采两点。";
                HasPendulum360 = false;
                Pendulum360Count = 0;
                return false;
            }

            long new360 = (long)newZeroCount + oldSpan;
            if (new360 < int.MinValue || new360 > int.MaxValue)
            {
                note = string.Format(CultureInfo.InvariantCulture,
                    "0° 点重标后按同一圈量（{0} counts）平移 360° 点会超出 int32 计数范围：360° 点已作废，请重采两点。",
                    oldSpan);
                HasPendulum360 = false;
                Pendulum360Count = 0;
                return false;
            }

            Pendulum360Count = (int)new360;
            PendulumCpr = Math.Abs(oldSpan);
            // 不盖「本次进程已确认」的会话戳：360° 点是按旧圈量平移出来的**标称点**，不是本次在
            // 机械上重新采到的位置。两点差（一圈的量）可信，但「360° 对应哪个物理姿态」没有重新确认；
            // 按本类既有规则（单采 0° 而保留上次开机留下的 360° 不得盖戳），这里保持原会话标记不动，
            // 上层该提示重新采集时仍然会提示。
            note = string.Format(CultureInfo.InvariantCulture,
                "360° 点已随 0° 点按同一圈量平移：新 360° raw={0}，一圈的量保持 {1} counts（{2:0.###}）；" +
                "如需把 360° 点重新认定为现场实测位置，请重采 0°/360° 两点。",
                Pendulum360Count, oldSpan, PendulumCpr);
            return true;
        }
        /// <para>
        /// 两种不可用：① 文件里没有 0° 点（<see cref="HasPendulumZero"/> = false —— 只存了 360° 点也算缺）；
        /// ② 调用方给了当前 raw（首帧遥测的摆杆计数）且与保存的 0° 点不一致 —— 掉电 / 换机械后 raw 坐标变了，
        /// 带旧零点起摆会把整圈偏差当成 0 附近的小角度。
        /// </para>
        /// <para>
        /// 比较走 <see cref="ControlMath.CountDelta"/>（32 位计数差，与引擎连接后首帧的位姿核对同一口径）：
        /// 只有 Δ = 0 才算一致。做不到「按一圈取模归一」—— EC1 / GC1602 都没有可依赖的绝对多圈，
        /// 差一整圈同样意味着旧零点不能再用，必须规整化或重采，不能把它当成「同一位置」放行。
        /// 上层据此拦起摆，不要自己再写一遍比对逻辑。
        /// </para>
        /// </summary>
        /// <param name="currentRawCount">当前摆杆原始计数；只查文件状态时传 null。</param>
        public PendulumZeroCheck CheckPendulumZero(int? currentRawCount)
        {
            return CheckPendulumZero(this, currentRawCount);
        }

        /// <summary>null 安全入口：没有 calibration 段（calibration 传 null）= 未标定。</summary>
        public static PendulumZeroCheck CheckPendulumZero(CalibrationParameters calibration, int? currentRawCount)
        {
            PendulumZeroCheck check = new PendulumZeroCheck();
            check.CurrentRawCount = currentRawCount;
            if (calibration == null || !calibration.HasPendulumZero)
            {
                check.Issue = PendulumZeroIssue.NotCalibrated;
                check.SavedZeroCount = calibration == null ? 0 : calibration.PendulumZeroCount;
                check.Reason = "参数文件里没有摆杆 0° 点（has_pendulum_zero=false）：θ 没有零点参考，"
                    + "整圈偏差会被当成 0 附近的小角度；起摆 / 平衡前必须先在自然下垂位采集摆杆 0°。";
                return check;
            }

            check.SavedZeroCount = calibration.PendulumZeroCount;
            check.Issue = PendulumZeroIssue.Ok;
            if (currentRawCount.HasValue)
            {
                int delta = ControlMath.CountDelta(
                    unchecked((uint)currentRawCount.Value),
                    unchecked((uint)calibration.PendulumZeroCount));
                check.DeltaCounts = delta;
                if (delta != 0)
                {
                    check.Issue = PendulumZeroIssue.RawMismatch;
                    check.Reason = string.Format(CultureInfo.InvariantCulture,
                        "摆杆当前 raw={0} 与保存的 0° 点 raw={1} 不一致（Δ={2} counts，一圈 {3:0.###} counts）："
                        + "先确认机械是否就在 0° 姿态 —— 在就「规整化」，不确定就重新采集 0°/360°；不要带旧零点起摆。",
                        currentRawCount.Value, calibration.PendulumZeroCount, delta, calibration.PendulumCpr);
                }
            }
            return check;
        }

        /// <summary>摆臂每转计数（用户指定当前假设 10000）。0 = 未定。</summary>
        public int ArmCountsPerRev = 10000;

        /// <summary>
        /// 摆臂减速比 = 电机转数 / 摆臂转数（1 = 无减速）。只用于下次启动构造控制层 ——
        /// 控制层运行期没有改写口，所以文件值在入口（Program）读进 PendulumEngineOptions。
        /// </summary>
        public double ArmGearRatio = 1.0;

        /// <summary>
        /// 摆臂零点：φ = 0 对应的原始 0x6064 计数（32 位有符号原样存，不做角度换算）。
        /// 只有 <see cref="HasArmZeroCount"/> = true 时生效；false = 用户没设过，使能首次归零仍取当前位置。
        /// <para>
        /// 该计数只在「0x6064 坐标系没有变」时有意义。EC1 是开环步进，掉电后 0x6064 不是绝对机械位置；
        /// 若上电发现 raw 起点变了，不能拿这条旧零点硬套，必须按
        /// <see cref="TryNormalizeArmPose"/> 在当前机械零点姿态平移，或重新采集三点。
        /// </para>
        /// </summary>
        public int ArmZeroCount;

        /// <summary>文件里是否真的写过摆臂零点；false = 未设定（旧文件 / 从未点过「设置 0 点」）。</summary>
        public bool HasArmZeroCount;

        /// <summary>
        /// 摆杆零点 = 摆杆自然下垂（最底部）那一点的**原始计数**（32 位位型原样存，不做角度换算）。
        /// 只有 <see cref="HasPendulumZero"/> = true 时生效；false = 用户没设过，使能首次归零仍取当前位置。
        /// GC1602 是 AB 增量计数，ESI 没有绝对多圈/掉电保持语义；raw 坐标一变就必须规整化或重采，
        /// 详见 <see cref="TryNormalizePendulumPose"/>。
        /// </summary>
        public int PendulumZeroCount;

        /// <summary>文件里是否真的写过摆杆零点；false = 未设定（旧文件 / 从未点过「摆杆零点」）。</summary>
        public bool HasPendulumZero;

        /// <summary>
        /// 摆杆一整圈（360°）那一点的原始计数。与 <see cref="PendulumZeroCount"/> 一起标定出
        /// **一圈的量 = |360° − 0°|**；<see cref="PendulumCpr"/> 只作为派生结果 / 旧文件兼容，不再由用户直接输入。
        /// </summary>
        public int Pendulum360Count;

        /// <summary>文件里是否真的写过 360° 标定点。</summary>
        public bool HasPendulum360;

        /// <summary>
        /// 摆臂三点（零点 + 左限位 + 右限位）最后一次被**整体**采集 / 规整化 / 确认时的进程会话标记。
        /// 不是本次进程写下的 = 无法证明这组 counts 仍对应当前物理位置，上层必须先规整化或重新采集。
        /// 只由 <see cref="MarkArmPoseCapturedInSession"/> 写入，旧文件没有这个键 = 未确认。
        /// </summary>
        public string ArmPoseSessionTag;

        /// <summary>摆臂三点最后一次整体确认的 UTC 时间（只作溯源，不参与判据）。</summary>
        public DateTime? ArmPoseCapturedAtUtc;

        /// <summary>
        /// 摆杆两点（0° + 360°）最后一次被**整体**采集 / 规整化 / 确认时的进程会话标记。
        /// 只有一半时不得标记（单采 0° 而保留旧 360° 的两点差不可信）。
        /// </summary>
        public string PendulumPoseSessionTag;

        /// <summary>摆杆两点最后一次整体确认的 UTC 时间（只作溯源，不参与判据）。</summary>
        public DateTime? PendulumPoseCapturedAtUtc;

        /// <summary>
        /// 用户明确清空过位置标定，且尚未重新采集完整三点 / 两点。**跨重启保留**，用于让控制层启动后
        /// 恢复同一个 <c>CalibrationCleared</c> 状态：true 时不得自动建立零点、不得使能自动运动。
        /// 旧文件缺这个键 = false（兼容“从未标定”，由引擎按首次使能规则处理）。
        /// </summary>
        public bool CalibrationCleared;

        /// <summary>
        /// 用户已重新采集完整位置标定（至少摆臂零点 + 左/右限位；摆杆 0° 按现有起摆门另行检查）。
        /// 只清跨重启的 <see cref="CalibrationCleared"/>，不改任何数值；控制层仍需调用自己的
        /// <c>ConfirmCalibrationComplete</c> 清运行态。调用方负责先验证三点齐全。
        /// </summary>
        public void MarkCalibrationComplete()
        {
            CalibrationCleared = false;
        }

        public bool IsValid(out string reason)
        {
            if (!ControlMath.IsFinite(PendulumCpr) || PendulumCpr < 0 || PendulumCpr > 100000000)
            {
                reason = "pendulumCpr 必须是 [0,1e8] 的有限数。";
                return false;
            }
            if (ArmCountsPerRev < 0 || ArmCountsPerRev > 100000000)
            {
                reason = "armCountsPerRev 必须是 [0,1e8] 的整数。";
                return false;
            }
            if (!ControlMath.IsFinite(ArmGearRatio) || !(ArmGearRatio > 0) || ArmGearRatio > 1000000)
            {
                reason = "armGearRatio 必须是 (0,1e6] 的有限数（电机转数 / 摆臂转数）。";
                return false;
            }
            reason = null;
            return true;
        }

        public static CalibrationParameters CreateDefault()
        {
            return new CalibrationParameters();
        }
    }

    /// <summary>
    /// 起摆段的一次载入裁决（只描述「文件里读到什么、最后用了什么」，控制律不读它）。
    /// 目前只有一种裁决：文件里的 swingup 段是**旧版出厂块** —— 这组值不是现场整定，
    /// 按它起摆会能量注入不足直到超时（SwingUpTimeout），所以载入时拒绝并改用当前出厂块。
    /// </summary>
    public sealed class SwingLoadDecision
    {
        /// <summary>文件里的 swingup 段是旧版出厂块，已被拒绝（本次按当前出厂起摆块载入）。</summary>
        public bool LegacyFactoryBlockRejected;

        /// <summary>中文原因：原始指纹值 + 拒绝后实际使用的出厂值；未拒绝时为 null。</summary>
        public string Reason;

        /// <summary>被拒绝的文件原始块（供界面显示「文件里原来是什么」）；未拒绝时无意义。</summary>
        public SwingUpParams RejectedBlock;
    }

    /// <summary>
    /// furuta_params.json 里主参数（pid / swingup / limits）之外的全部持久化内容。
    /// 同一个文件、同一套序列化，不另造第二个配置文件。
    /// </summary>
    public sealed class PersistedExtras
    {
        public PumpParameters Pump;
        public IdentifyParameters Identify;
        public SoftLimitParameters SoftLimits;
        public CalibrationParameters Calibration;

        /// <summary>
        /// 起摆段的载入裁决（旧版出厂块被拒时 <see cref="SwingLoadDecision.LegacyFactoryBlockRejected"/> = true）。
        /// 界面 / 上层据此提示「起摆参数仍是旧版出厂块」；只由 ParamStore 载入填写，控制层不读。
        /// </summary>
        public SwingLoadDecision SwingLoad;

        /// <summary>
        /// 摆杆 0° 点是否可用于起摆（null 安全：没有 calibration 段 = 未标定）。起摆前查这个，
        /// 不要自己比对 counts。<paramref name="currentRawCount"/> 传首帧遥测的摆杆 raw 才能同时
        /// 检查「坐标是否变了」；只查文件状态传 null。
        /// </summary>
        public PendulumZeroCheck CheckPendulumZero(int? currentRawCount)
        {
            return CalibrationParameters.CheckPendulumZero(Calibration, currentRawCount);
        }

        /// <summary>最近 N=20 次捕获记录（按时间先后，最新的在最后）。</summary>
        public List<CaptureRecord> Captures;

        /// <summary>本文件最后一次成功保存的时间（UTC）。只作显示与溯源。</summary>
        public DateTime? SavedAtUtc;

        /// <summary>
        /// 读盘时文件里是否真的出现过可选段。false = 旧格式文件 / 全默认合成的对象；
        /// 保存时据此决定「extras 传 null」要不要补写默认可选段（旧文件保持原样，不凭空升级）。
        /// 不序列化（JSON 载荷在 ParamStore 的 Doc 类里）。
        /// </summary>
        public bool SectionsPresent;

        /// <summary>
        /// 读盘时 calibration 段是否真的存在且校验通过。false = 旧格式文件没有这一段 / 这一段不合法被
        /// 「整段回退默认值」——此时 <see cref="Calibration"/> 里的数（armCountsPerRev = 10000、
        /// armGearRatio = 1.0）是程序默认值，不是文件里的现场标定，入口不得拿它冒充恢复出来的标定。
        /// 不序列化（JSON 载荷在 ParamStore 的 Doc 类里）。
        /// </summary>
        public bool CalibrationFromFile;

        /// <summary>
        /// 本对象由「只读可选段」入口（<see cref="ParamStore.TryReadExtras"/>）返回。
        /// 该入口的服务对象是 zero / normalize / recenter / calibrate-radius 这类**增量标定落盘**：
        /// 只允许改标定 / 几何量，主参数段（pid / swingup / limits）必须按文件原值保留 ——
        /// CLI 完全可能没带 --config 启动（引擎里是出厂默认），此时拿引擎值写盘会把用户的
        /// Ke / radius_ratio / 软限位整段冲掉（2026-11-20 台架事故）。
        /// 不序列化。
        /// </summary>
        internal bool IncrementalRead;

        /// <summary>
        /// 载入来源快照（来源路径 / 原始 JSON / 各段载入后的语义值）。保存时用它做
        /// 「用户没改过的字段原样保留」——只有语义值确实变了才覆盖文件里的旧值。
        /// 由 <see cref="ParamStore.TryLoad(string,string,out PidGains,out SwingUpParams,out SafetyLimits,out PersistedExtras,out string,out string,out string)"/>
        /// 填写；不序列化。
        /// </summary>
        internal ParamStore.SaveProvenance Provenance;

        public const int CaptureRecordLimit = 20;

        /// <summary>整体校验：任一段不合法就不写入（避免半套生效）。</summary>
        public bool IsValid(out string reason)
        {
            reason = null;
            string part;
            if (Pump != null && !Pump.IsValid(out part))
            {
                reason = "泵参数段不合法：" + part;
                return false;
            }
            if (Identify != null && !Identify.IsValid(out part))
            {
                reason = "辨识结果段不合法：" + part;
                return false;
            }
            if (SoftLimits != null && !SoftLimits.IsValid(out part))
            {
                reason = "软限位段不合法：" + part;
                return false;
            }
            if (Calibration != null && !Calibration.IsValid(out part))
            {
                reason = "标定段不合法：" + part;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 清空全部位置标定（摆臂左/零/右 + 摆杆 0°/360°），只改内存，不移动电机、不写文件。
        /// **保留换算尺度**：摆杆一圈的量 <see cref="CalibrationParameters.PendulumCpr"/>、
        /// 摆臂每转计数 <see cref="CalibrationParameters.ArmCountsPerRev"/>、减速比
        /// <see cref="CalibrationParameters.ArmGearRatio"/> 不动。同时把
        /// <see cref="CalibrationParameters.CalibrationCleared"/> 置 true，保存后跨重启仍拒绝自动运动。
        /// 调用方随后必须让自动运动被拒绝，直到重新采集完整三点 / 两点。
        /// </summary>
        public void ClearPositionCalibration()
        {
            if (Calibration == null) Calibration = CalibrationParameters.CreateDefault();
            Calibration.ClearPosePoints();
            if (SoftLimits == null) SoftLimits = SoftLimitParameters.CreateDefault();
            SoftLimits.Clear();
        }

        /// <summary>
        /// 标记位置标定已重新采集完成，清跨重启的 <see cref="CalibrationParameters.CalibrationCleared"/>。
        /// **不改数值、不移动电机**；调用前必须先验证三点 / 两点齐全，控制层另行调用
        /// <c>ConfirmCalibrationComplete</c> 清运行态。
        /// </summary>
        public void MarkCalibrationComplete()
        {
            if (Calibration != null)
                Calibration.MarkCalibrationComplete();
        }

        /// <summary>只保留最近 20 条捕获记录（按时间排序后截尾）。</summary>
        public void ClampCaptures()
        {
            if (Captures == null)
            {
                Captures = new List<CaptureRecord>();
                return;
            }
            if (Captures.Count > CaptureRecordLimit)
                Captures.RemoveRange(0, Captures.Count - CaptureRecordLimit);
        }

        /// <summary>起手值：控制层默认参数 + 用户指定的标定 / 软限位默认。</summary>
        public static PersistedExtras CreateDefault(double armCountsPerRev)
        {
            PersistedExtras extras = new PersistedExtras();
            extras.Pump = PumpParameters.CreateDefault();
            extras.Identify = new IdentifyParameters();
            extras.SoftLimits = SoftLimitParameters.CreateDefault();
            extras.Calibration = CalibrationParameters.CreateDefault();
            if (armCountsPerRev > 0 && ControlMath.IsFinite(armCountsPerRev))
                extras.Calibration.ArmCountsPerRev = (int)Math.Round(armCountsPerRev, MidpointRounding.AwayFromZero);
            extras.Captures = new List<CaptureRecord>();
            extras.SavedAtUtc = null;
            return extras;
        }
    }
}
