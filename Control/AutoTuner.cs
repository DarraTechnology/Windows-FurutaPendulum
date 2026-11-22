using System;
using System.Globalization;
using System.Text;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 自动调参产出的候选参数包。只描述一组可以写进控制层的值，不持有引擎 / 界面引用，也不自己写参数。
    /// </summary>
    public sealed class AutoTuneCandidate
    {
        /// <summary>候选平衡增益：Ka / Kad / Kai 是历史参数，直接沿用输入那一组；其余字段也一并沿用。</summary>
        public PidGains Pid;

        /// <summary>候选起摆参数：泵的 k_e / 泵频 / 泵幅按辨识推荐更新，其余字段沿用输入的那一组。</summary>
        public SwingUpParams Swing;

        /// <summary>人能读的中文说明：辨识事实、每一项的旧值 → 新值，以及没有采用的推荐。</summary>
        public string Summary;
    }

    /// <summary>
    /// 起摆泵的纯计算整定器：把一次自由摆动辨识结果折算成一组候选参数，供界面「试跑验收」使用。
    /// <para>
    /// 全部计算只读 <see cref="IdentificationResult"/> 与调用方给的两组当前参数，不碰引擎、不碰界面、
    /// 不写任何全局状态；因此同一输入必然得到同一输出，试跑前后都能按 Summary 逐项复核。
    /// </para>
    /// <para>
    /// 口径：平衡环已由整套状态反馈接管，上一代加速度反馈律的 Ka / Kad / Kai 只作历史值随参数文件读写，
    /// 本整定器<b>不计算也不校验外环</b> —— 一个调了也没有效果的数，不该成为整份候选（连同泵推荐）的否决条件。
    /// 本整定器只按辨识结果推荐起摆泵：k_e / 泵频 / 泵幅；其余起摆项与全部平衡增益原样沿用当前值。
    /// Sign 不是可调项：方向由控制层的平衡方向固定，界面与参数文件只如实展示。
    /// </para>
    /// </summary>
    public static class AutoTuner
    {
        /// <summary>自然频率下界 [Hz]。低于它半周期统计没有意义（一次摆动超过 10 s）。</summary>
        public const double MinNaturalFreqHz = 0.05;

        /// <summary>自然频率上界 [Hz]。高于它更可能是计数噪声被误判成摆动。</summary>
        public const double MaxNaturalFreqHz = 20.0;

        /// <summary>泵幅的界面量程上限 [deg]：与主界面泵幅输入框一致，超过就按上限写入并在 Summary 说明。</summary>
        public const double MaxPumpAmplitudeDeg = 180.0;

        /// <summary>
        /// 由辨识结果算出一组候选参数（无耦合比入口）。Ka 已是历史参数，本入口与带耦合比的重载结果相同。
        /// </summary>
        public static bool TryCompute(
            IdentificationResult id,
            PidGains current,
            SwingUpParams currentSwing,
            out AutoTuneCandidate candidate,
            out string reason)
        {
            return TryCompute(id, current, currentSwing, 0.0, out candidate, out reason);
        }

        /// <summary>
        /// 由辨识结果算出一组候选参数。全程不抛异常，失败一律写进 <paramref name="reason"/>。
        /// </summary>
        /// <param name="id">引擎给的自由摆动辨识结果（样本不足时 Sufficient=false）。</param>
        /// <param name="current">当前生效的平衡增益；候选原样沿用这一组（Ka / Kad / Kai 是历史值）。</param>
        /// <param name="currentSwing">当前生效的起摆参数；泵以外的字段全部沿用这一组。</param>
        /// <param name="couplingAbs">使用中的 |r/l|（limits.radius_ratio）；0 = 未标定。
        /// 保留这个参数只为调用方签名不变：Ka 已不参与平衡，结构下界不再参与候选判定。</param>
        /// <param name="candidate">成功时给出候选；失败时为 null。</param>
        /// <param name="reason">失败时的中文原因（说清缺什么 / 哪里不合法 / 怎么补）；成功时为 null。</param>
        /// <returns>true = 候选可用且通过全部校验；false = 未产出候选。</returns>
        public static bool TryCompute(
            IdentificationResult id,
            PidGains current,
            SwingUpParams currentSwing,
            double couplingAbs,
            out AutoTuneCandidate candidate,
            out string reason)
        {
            candidate = null;
            reason = null;
            try
            {
                return TryComputeCore(id, current, currentSwing, couplingAbs, out candidate, out reason);
            }
            catch (Exception ex)
            {
                // 纯计算也不留未处理异常：调用方（界面）只需要一条可直接显示的中文原因。
                candidate = null;
                reason = "自动调参计算异常：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        private static bool TryComputeCore(
            IdentificationResult id,
            PidGains current,
            SwingUpParams currentSwing,
            double couplingAbs,
            out AutoTuneCandidate candidate,
            out string reason)
        {
            candidate = null;
            reason = null;

            // ---- 1. 前置：样本足够 + 自然频率落在可信区间 ----
            if (!id.Sufficient)
            {
                string detail = string.IsNullOrEmpty(id.InsufficientReason)
                    ? "控制层没有给出具体原因"
                    : id.InsufficientReason;
                reason = "辨识样本不足，无法计算候选增益（" + detail + "）。"
                    + "请让杆从较大角度自由摆动并延长采集时间后重试。";
                return false;
            }
            if (!ControlMath.IsFinite(id.NaturalFreqHz) ||
                id.NaturalFreqHz <= MinNaturalFreqHz ||
                id.NaturalFreqHz >= MaxNaturalFreqHz)
            {
                reason = "辨识自然频率 " + Num(id.NaturalFreqHz, "0.######") + " Hz 不在 ("
                    + Num(MinNaturalFreqHz, "0.######") + ", " + Num(MaxNaturalFreqHz, "0.######")
                    + ") Hz 内，折不出可信的 ω_n。请确认自由摆动段没有碰关节，并重新采集。";
                return false;
            }

            double wn = 2.0 * Math.PI * id.NaturalFreqHz;
            double gravityOverLength = wn * wn;
            if (!ControlMath.IsFinite(wn) || !ControlMath.IsFinite(gravityOverLength))
            {
                reason = "辨识自然频率换算出的 ω_n 不是有限数，拒绝写入。";
                return false;
            }

            // ---- 2. 平衡增益：Ka / Kad / Kai 是历史参数，不整定、不校验 ----
            // 平衡环已由整套状态反馈接管，这三项只随参数文件读写，候选原样沿用当前值。
            // 这里不做结构下界校验：那会把一个调了也没有效果的数当成整份候选的否决条件，
            // 连同一轮算出的泵推荐一起丢掉。
            PidGains pid = current;

            // ---- 3. 起摆泵：k_e / 泵频 / 泵幅按辨识推荐，其余沿用 ----
            PumpParameters pump = PumpParameters.From(currentSwing);
            double oldKe = pump.Ke;
            double oldPumpHz = pump.PumpHz;
            double oldPumpAmpDeg = pump.PumpAmpDeg;

            bool keApplied = false;
            bool pumpHzApplied = false;
            bool pumpAmpApplied = false;
            bool pumpAmpClamped = false;

            // 辨识结果里的 Ke / PumpHz / PumpAmpDeg 就是推荐值（持久化时落到 recommended_* 字段）；
            // 不可辨识时分别是 NaN / 0，一律不采用，保留当前值。
            if (ControlMath.IsFinite(id.Ke) && id.Ke > 0)
            {
                pump.Ke = id.Ke;
                keApplied = true;
            }
            if (ControlMath.IsFinite(id.PumpHz) && id.PumpHz > 0)
            {
                pump.PumpHz = id.PumpHz;
                pumpHzApplied = true;
            }
            if (ControlMath.IsFinite(id.PumpAmpDeg) && id.PumpAmpDeg > 0)
            {
                double amplitude = id.PumpAmpDeg;
                if (amplitude > MaxPumpAmplitudeDeg)
                {
                    amplitude = MaxPumpAmplitudeDeg;
                    pumpAmpClamped = true;
                }
                pump.PumpAmpDeg = amplitude;
                pumpAmpApplied = true;
            }

            string pumpReason;
            if (!pump.IsValid(out pumpReason))
            {
                reason = "候选泵参数不合法（未写入）：" + pumpReason;
                return false;
            }

            SwingUpParams swing = currentSwing;
            pump.ApplyTo(ref swing);
            string swingReason;
            if (!swing.IsValid(out swingReason))
            {
                reason = "换算后的起摆参数不合法（未写入）：" + swingReason;
                return false;
            }

            // ---- 4. 组装候选与可复核的说明 ----
            AutoTuneCandidate result = new AutoTuneCandidate();
            result.Pid = pid;
            result.Swing = swing;
            result.Summary = BuildSummary(id, pid, oldKe, oldPumpHz, oldPumpAmpDeg,
                pump, pumpAmpApplied, pumpAmpClamped, keApplied, pumpHzApplied);
            candidate = result;
            return true;
        }

        /// <summary>
        /// 人读说明：辨识事实 → 平衡增益沿用情况 → 每一项的旧值 → 新值 → 未采用的推荐。
        /// 现场照这一页就能复核，不需要回看代码。
        /// </summary>
        private static string BuildSummary(
            IdentificationResult id,
            PidGains candidate,
            double oldKe,
            double oldPumpHz,
            double oldPumpAmpDeg,
            PumpParameters pump,
            bool pumpAmpApplied,
            bool pumpAmpClamped,
            bool keApplied,
            bool pumpHzApplied)
        {
            double wn = 2.0 * Math.PI * id.NaturalFreqHz;
            double gravityOverLength = wn * wn;

            StringBuilder sb = new StringBuilder(512);
            sb.Append("辨识：f_n=").Append(Num(id.NaturalFreqHz, "0.######")).Append(" Hz");
            sb.Append("（ω_n=").Append(Num(wn, "0.####")).Append(" rad/s，g/l=").Append(Num(gravityOverLength, "0.####")).Append("）");
            sb.Append("；实测阻尼比=").Append(Num(id.DampingZeta, "0.0000"));
            sb.Append("；置信度=").Append(Num(id.Confidence, "0.00"));
            sb.Append("；样本 半周期/极值/捕获=").Append(id.HalfPeriodSamples.ToString(CultureInfo.InvariantCulture))
              .Append('/').Append(id.ExtremaSamples.ToString(CultureInfo.InvariantCulture))
              .Append('/').Append(id.CaptureSamples.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.Append("平衡增益：Ka / Kad / Kai 是历史参数，平衡环已由整套状态反馈接管，本轮不整定也不校验；")
              .Append("候选沿用 Ka=").Append(ControlMath.Gain(candidate.Ka))
              .Append(" Kad=").Append(ControlMath.Gain(candidate.Kad))
              .Append(" Kai=").Append(ControlMath.Gain(candidate.Kai)).Append("。");
            sb.AppendLine();
            sb.Append("泵：k_e ").Append(ControlMath.Gain(oldKe)).Append(" → ").Append(ControlMath.Gain(pump.Ke));
            sb.Append("，泵频 ").Append(Num(oldPumpHz, "0.###")).Append(" → ").Append(Num(pump.PumpHz, "0.###")).Append(" Hz");
            sb.Append("，泵幅 ").Append(Num(oldPumpAmpDeg, "0.##")).Append(" → ").Append(Num(pump.PumpAmpDeg, "0.##")).Append("°");
            sb.AppendLine();
            sb.Append("本轮只推荐起摆泵（k_e / 泵频 / 泵幅）；AccMax / ArmRateMax / Kφ / Kφ̇ / 内环 Kpφ·Kiφ·Kdφ / "
                + "微分低通 / 输出与积分限幅 / 死区 / 起摆其它项一律沿用当前值。");
            sb.AppendLine();
            sb.Append("历史说明：上一代外环整定口径已退场，结构下界不再参与候选判定；"
                + "方向由平衡方向固定，不是可调项。");
            if (pumpAmpClamped)
            {
                sb.AppendLine();
                sb.Append("备注：辨识泵幅 ").Append(Num(id.PumpAmpDeg, "0.##"))
                  .Append("° 超过界面量程 ").Append(Num(MaxPumpAmplitudeDeg, "0.#"))
                  .Append("°，已按上限写入。");
            }
            if (!pumpAmpApplied)
            {
                sb.AppendLine();
                sb.Append("备注：本次没有可用的泵幅推荐，泵幅沿用当前值。");
            }
            if (!keApplied)
            {
                sb.AppendLine();
                sb.Append("备注：本次没有可用的 k_e 推荐，k_e 沿用当前值。");
            }
            if (!pumpHzApplied)
            {
                sb.AppendLine();
                sb.Append("备注：本次没有可用的泵频推荐，泵频沿用当前值。");
            }
            return sb.ToString();
        }

        /// <summary>数值可读化：NaN / Infinity 直说，不显示成 -2147483648 之类。</summary>
        private static string Num(double value, string format)
        {
            if (double.IsNaN(value)) return "n/a";
            if (double.IsPositiveInfinity(value)) return "+∞";
            if (double.IsNegativeInfinity(value)) return "-∞";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
