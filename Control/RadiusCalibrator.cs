using System;
using System.Globalization;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// r/l（摆臂轴到摆杆轴距离 / 等效摆长）的主动标定结果。一次标定只产生一个不可变快照。
    /// </summary>
    public sealed class RadiusCalibrationResult
    {
        /// <summary>估计值 r/l。无效时为 0。</summary>
        public double RadiusRatio;

        /// <summary>成功参与平均的**测量相数**（每对脉冲有 +a / −a 两相；每相独立给出一个 r/l 样本）。</summary>
        public int ValidSamples;

        /// <summary>每对估计值的标准差（总体标准差，与 r/l 同单位，绝对值）。</summary>
        public double StdDev;

        /// <summary>相对离散度 σ/|r/l|（无量纲）。判定有效时总是填；估计值本身无效时为 0。</summary>
        public double RelativeStdDev;

        /// <summary>本次判定使用的**绝对 σ 允许上限** = max(0.02, |r/l|×25%)，与 <see cref="StdDev"/> 同单位。</summary>
        public double AllowedStdDev;

        /// <summary>参与测量的平均 |φ̈| [rad/s²] 与平均 |θ̈| [rad/s²]，用于判断信噪比是否够。</summary>
        public double MeanAbsPhiAccelRadPerSec2;
        public double MeanAbsThetaAccelRadPerSec2;

        /// <summary>true = 估计有效且落在几何合理区间。</summary>
        public bool Valid;

        /// <summary>失败 / 拒绝原因；Valid=true 时为 null。</summary>
        public string Reason;

        /// <summary>结构稳定判据 (r/l)·Ka − g/l（&gt;0 才可能站住）。只做提示，不参与估计。</summary>
        public double StructuralMarginRadPerSec2;
    }

    /// <summary>
    /// r/l 主动标定：把摆杆停在下垂位附近，给摆臂一段**已知方向的恒定加速度脉冲**，
    /// 用同窗口内的 φ̈ / θ̈ 二次拟合反推 r/l。
    /// <para>
    /// 原理（引擎坐标，底部 cosθ≈+1）：θ̈ = −(g/l)·sinθ + C_cal·cosθ·φ̈。
    /// 每个测量相按实测 θ 均值扣重力项：
    /// <code>
    /// C_cal = (θ̈ + (g/l)·sinθ̄) / (cosθ̄ · φ̈)
    /// r/l   = −CalibrationSign · C_cal
    /// </code>
    /// 台架四次脉冲标定的原始 C_cal ≈ **−0.048（负）**；报告值 r/l 定义为**正的几何幅值**，
    /// 所以 CalibrationSign=+1 **且公式里的负号必须保留**（去掉负号会把同一批数据报成 −0.048，
    /// 标定会再次失败）。与平衡律的关系：近顶 cosθ≈−1 ⇒ C_engine = −C_cal = +r/l ≈ +0.048；
    /// 24 拍拟合的 C_engine=+0.057，与 r/l=+0.048 同号同量级（差 19%）。
    /// **平衡律用 +C_engine，标定用 r/l；两者不是同一个原始系数，但同号同量级。**
    /// </para>
    /// <para>
    /// 不使用「起摆数据回归 θ̈ 对 φ̈」：摆臂编码器量化噪声只会把斜率压小 1~2 个数量级（历史拟合值
    /// 0.015~0.021 vs 几何估计 ~0.4 就是它）。本方法的窗口是 60 ms 里的 40 ms 二次拟合，
    /// 摆臂 10000 count/转 ⇒ φ̈ 拟合噪声 ≈ 0.25 rad/s²，信噪比比 4 ms 二次差分高两个数量级。
    /// </para>
    /// <para>
    /// 安全：脉冲幅值默认 100 rad/s²、单相 60 ms、四相一对回到起点，摆臂峰值位移 ≈ 0.36 rad、
    /// 峰值速度 ≈ 6 rad/s，全部远小于软限位与起摆工作幅值。任何相位目标越界 / 状态离开 Enabled
    /// 都会立刻中止并由引擎回到正常 hold。
    /// </para>
    /// </summary>
    internal sealed class RadiusCalibrator
    {
        private const int WindowCapacity = 512;
        private const double MinAbsPhiAccel = 30.0;      // 低于它说明伺服没跟上，这一相作废
        private const double MinAbsThetaAccel = 1.0;     // 低于它信噪比不够
        private const double MaxAbsThetaAccel = 400.0;   // 物理上不可能，作废
        internal const double SettleOmega = 0.80;        // 静止判据 [rad/s]（重力项按实测 θ 扣除，不必等完全静止）
        internal const double SettleTheta = 0.25;        // 下垂位判据 [rad]
        internal const double SettleHoldSec = 0.20;      // 连续满足多久才算可用
        internal const double StartThetaLimit = 0.35;    // 启动前预检的下垂位门限 [rad]
        private const double SettleTimeoutSec = 6.0;
        private const double SkipStartSec = 0.015;       // 跳过伺服建立段
        private const double SkipEndSec = 0.005;

        // ---- r/l 有效性判据（唯一口径，评估函数与文档共用，不许各写一套）----
        // 估计值必须落在几何合理区间；上界用 SafetyLimits 的量级哨兵（10，不是物理定律），
        // 不再用历史小臂机构的 1.5 —— 否则 1.5…10 的真实机构会被整段判非法 / 静默丢弃。
        internal const double MinRatio = 0.005;
        internal const double MaxRatio = SafetyLimits.MaxReasonableRadiusRatio;
        // 逐相排除统计：让现场看到"测出来了但被规则挡掉"，而不是只看到"有效样本不足"。
        private int _candidateSamples;
        private int _rejectedBelow;
        private int _rejectedAbove;
        private int _rejectedInvalid;
        private int _rejectedGeometry;
        private readonly double[] _rejectedValues = new double[8];
        private int _rejectedValueCount;
        // 离散度判据：**绝对 σ** 与**绝对门限**比较。
        // 门限 = max(绝对噪声地板 0.02, |r/l|×25%)。
        // 相对离散度 σ/|r/l| 只用于展示与解释，绝不拿来跟绝对门限比（口径混用会让小 σ 被误拒）。
        internal const double MaxRelativeStdDev = 0.25;
        internal const double MinStdDevFloor = 0.02;
        // 连续坏样本（NaN / 非有限）上限：超过就判失败，绝不让标定永远卡在测量相。
        private const int MaxConsecutiveBadSamples = 1000;

        private enum Phase
        {
            Idle,
            Settle,
            AccelPlus,      // +a，测量
            StopMinus,      // −a，把臂刹停
            GapAfterPlus,
            AccelMinus,     // −a，测量
            StopPlus,       // +a，把臂刹停并回起点
            GapAfterMinus,
            Done,
            Failed
        }

        private Phase _phase = Phase.Idle;
        private double _phaseSec;
        private double _settleSec;
        private double _gapSec;
        private int _pairsDone;
        private double _accel;
        private double _pulseSec;
        private int _pairs;
        private double _gapDurSec;

        private double _refRad;
        private double _refDot;

        // 本相采样（周期线程独占，零分配）。
        private readonly double[] _t = new double[WindowCapacity];
        private readonly double[] _theta = new double[WindowCapacity];
        private readonly double[] _phi = new double[WindowCapacity];
        private int _n;

        // 每个有效测量相的直接估计（重力项已按实测 θ 扣除）。
        private int _validSamples;
        private double _sumRatio;
        private double _sumRatioSq;
        private double _sumAbsPhiAcc;
        private double _sumAbsThetaAcc;
        private double _gravityOverLength;
        private int _consecutiveBadSamples;

        private RadiusCalibrationResult _result;

        public bool Active
        {
            get { return _phase != Phase.Idle && _phase != Phase.Done && _phase != Phase.Failed; }
        }

        public bool Finished
        {
            get { return _phase == Phase.Done || _phase == Phase.Failed; }
        }

        /// <summary>本拍摆臂目标角 [rad]（绝对坐标，起始位置 = 0）。只在 Active 时有效。</summary>
        public double TargetRad
        {
            get { return _refRad; }
        }

        /// <summary>最近一次完成的结果（可能 Valid=false）。未跑过为 null。</summary>
        public RadiusCalibrationResult Result
        {
            get { return _result; }
        }

        public void Reset()
        {
            _phase = Phase.Idle;
            _phaseSec = 0;
            _settleSec = 0;
            _gapSec = 0;
            _pairsDone = 0;
            _n = 0;
            _validSamples = 0;
            _sumRatio = 0;
            _sumRatioSq = 0;
            _sumAbsPhiAcc = 0;
            _sumAbsThetaAcc = 0;
            _candidateSamples = 0;
            _rejectedBelow = 0;
            _rejectedAbove = 0;
            _rejectedInvalid = 0;
            _rejectedGeometry = 0;
            _rejectedValueCount = 0;
            _consecutiveBadSamples = 0;
            _refRad = 0;
            _refDot = 0;
            _result = null;
        }

        /// <summary>
        /// 开始标定。startPhi 是当前摆臂角（脉冲围绕它往返）；
        /// gravityOverLength 是 g/l [rad/s²]（= Mgl/JEq），用于按实测 θ 扣除重力项。
        /// </summary>
        public void Start(double accel, double pulseSec, int pairs, double gapSec, double startPhi,
            double gravityOverLength)
        {
            Reset();
            _accel = Math.Abs(accel);
            _pulseSec = pulseSec;
            _pairs = Math.Max(1, pairs);
            _gapDurSec = Math.Max(0.2, gapSec);
            _gravityOverLength = gravityOverLength;
            _refRad = startPhi;
            _refDot = 0;
            _phase = Phase.Settle;
            _phaseSec = 0;
            _settleSec = 0;
        }

        /// <summary>中止（故障 / 停机 / 状态离开 Enabled）。只记原因，不抛。</summary>
        public void Abort(string reason)
        {
            if (!Active) return;
            Fail(reason);
        }

        /// <summary>
        /// 推进一拍，返回 true = 本次标定已结束（结果见 <see cref="Result"/>）。
        /// theta 用下垂点为 0 的角（引擎传入 wrapPi(θ−π)+π 的等价值）。
        /// </summary>
        public bool Step(double dt, double theta, double omega, double phi, double phiDot,
            double phiMinRad, double phiMaxRad)
        {
            if (!Active)
                return Finished;

            if (!ControlMath.IsFinite(dt) || dt <= 0 ||
                !ControlMath.IsFinite(theta) || !ControlMath.IsFinite(omega) ||
                !ControlMath.IsFinite(phi) || !ControlMath.IsFinite(phiDot))
            {
                // 坏样本：本拍不推进，绝不用 NaN 污染拟合。但必须计数并最终失败，
                // 否则 φ/θ 持续 NaN 时相计时永不推进，标定会永远显示“进行中”且没有原因。
                if (++_consecutiveBadSamples >= MaxConsecutiveBadSamples)
                {
                    Fail("标定数据连续 " + MaxConsecutiveBadSamples.ToString(CultureInfo.InvariantCulture) +
                        " 拍非有限（θ/ω/φ/φ̇ 含 NaN 或 dt≤0）：先确认摆杆/摆臂遥测有效（status 看 θ、φ 是否数值），" +
                        "再重新 calibrate-radius。");
                    return true;
                }
                return false;
            }
            _consecutiveBadSamples = 0;

            _phaseSec += dt;

            switch (_phase)
            {
                case Phase.Settle:
                    if (Math.Abs(omega) <= SettleOmega && Math.Abs(theta) <= SettleTheta)
                        _settleSec += dt;
                    else
                        _settleSec = 0;
                    if (_settleSec >= SettleHoldSec)
                    {
                        BeginPulsePair();
                    }
                    else if (_phaseSec >= SettleTimeoutSec)
                    {
                        Fail(string.Format(CultureInfo.InvariantCulture,
                            "摆杆未在自然下垂位稳定（|ω|≤{0:F2} rad/s、|θ|≤{1:F2} rad 连续 {2:F2} s 未满足）。" +
                            "把摆杆放到自然下垂位并等它基本停住后再试。",
                            SettleOmega, SettleTheta, SettleHoldSec));
                    }
                    break;

                case Phase.AccelPlus:
                case Phase.AccelMinus:
                {
                    double a = _phase == Phase.AccelPlus ? _accel : -_accel;
                    _refDot += a * dt;
                    _refRad += _refDot * dt;
                    Sample(theta, phi);
                    if (_phaseSec >= _pulseSec)
                        EndMeasuredPhase(theta);
                    break;
                }

                case Phase.StopMinus:
                case Phase.StopPlus:
                {
                    double a = _phase == Phase.StopMinus ? -_accel : _accel;
                    _refDot += a * dt;
                    _refRad += _refDot * dt;
                    if (_phaseSec >= _pulseSec)
                    {
                        // 两相后速度应回到 0（允许伺服残差）；进入静置。
                        _refDot = 0;
                        _phase = _phase == Phase.StopMinus ? Phase.GapAfterPlus : Phase.GapAfterMinus;
                        _phaseSec = 0;
                        _gapSec = 0;
                    }
                    break;
                }

                case Phase.GapAfterPlus:
                case Phase.GapAfterMinus:
                    _gapSec += dt;
                    if (_gapSec >= _gapDurSec)
                    {
                        if (_phase == Phase.GapAfterPlus)
                        {
                            _phase = Phase.AccelMinus;
                            _phaseSec = 0;
                        }
                        else
                        {
                            _pairsDone++;
                            if (_pairsDone >= _pairs) Complete();
                            else
                            {
                                // 下一对之前重新静置：脉冲会带起一点摆动，等 |ω| 落到门限再测。
                                _phase = Phase.Settle;
                                _phaseSec = 0;
                                _settleSec = 0;
                            }
                        }
                    }
                    break;
            }

            if (_phase == Phase.Done || _phase == Phase.Failed)
                return true;

            // 目标行程自检：任何一相都不许把摆臂带出软限位（留 0.05 rad 余量）。
            if (!ControlMath.IsFinite(_refRad) ||
                _refRad < phiMinRad + 0.05 || _refRad > phiMaxRad - 0.05)
            {
                Fail(string.Format(CultureInfo.InvariantCulture,
                    "脉冲目标越界：φ_ref={0:F3} rad 不在软限位 [{1:F3}, {2:F3}] 内（留 0.05 余量）",
                    _refRad, phiMinRad, phiMaxRad));
                return true;
            }
            return false;
        }

        private void BeginPulsePair()
        {
            _n = 0;
            _phaseSec = 0;
            // 四相对称脉冲结束时摆臂应回到起点：参考位置保持当前值，只把速度归零。
            // 不重置 _refRad —— 重置会在伺服有残差时给 0x607A 一个阶跃。
            _refDot = 0;
            _phase = Phase.AccelPlus;
        }

        private void Sample(double theta, double phi)
        {
            // 跳过伺服建立段与相尾；窗口内的样本按相起点计时。
            if (_phaseSec < SkipStartSec || _phaseSec > _pulseSec - SkipEndSec)
                return;
            if (_n >= WindowCapacity) return;
            _t[_n] = _phaseSec;
            _theta[_n] = theta;
            _phi[_n] = phi;
            _n++;
        }

        private void EndMeasuredPhase(double theta)
        {
            bool plus = _phase == Phase.AccelPlus;
            double thetaAcc = double.NaN;
            double phiAcc = double.NaN;
            bool thetaFit = FitQuadratic(_t, _theta, _n, out _, out thetaAcc);
            bool phiFit = FitQuadratic(_t, _phi, _n, out _, out phiAcc);
            bool ok = thetaFit && phiFit;
            double expectedSign = plus ? 1.0 : -1.0;

            if (ok && ControlMath.IsFinite(thetaAcc) && ControlMath.IsFinite(phiAcc) &&
                Math.Abs(phiAcc) >= MinAbsPhiAccel &&
                Math.Abs(thetaAcc) >= MinAbsThetaAccel && Math.Abs(thetaAcc) <= MaxAbsThetaAccel &&
                Math.Sign(phiAcc) == expectedSign)
            {
                // 按窗口内实测 θ 的均值扣除重力项（与平衡律同一符号约定）：
                //   θ̈ = −(g/l)·sinθ + C·cosθ·φ̈，C = −CalibrationSign·(r/l)
                // ⇒ r/l = −CalibrationSign · (θ̈ + (g/l)·sinθ) / (cosθ·φ̈)。
                // 这样不必等摆杆完全静止；残余摆动（±0.04 rad）带来的重力加速度会被扣掉。
                double thetaSum = 0;
                for (int i = 0; i < _n; i++) thetaSum += _theta[i];
                double thetaMean = thetaSum / _n;
                double cosMean = Math.Cos(thetaMean);
                if (Math.Abs(cosMean) < 0.90)
                {
                    // 几何条件不满足（摆杆不在下垂位附近）：不是"比值不合格"，单独计数。
                    _rejectedGeometry++;
                }
                else
                {
                    double corrected = thetaAcc + _gravityOverLength * Math.Sin(thetaMean);
                    double ratio = -PendulumEngine.CalibrationSign * corrected / (cosMean * phiAcc);
                    _candidateSamples++;
                    if (ControlMath.IsFinite(ratio) && ratio > MinRatio && ratio < MaxRatio)
                    {
                        _validSamples++;
                        _sumRatio += ratio;
                        _sumRatioSq += ratio * ratio;
                        _sumAbsPhiAcc += Math.Abs(phiAcc);
                        _sumAbsThetaAcc += Math.Abs(thetaAcc);
                    }
                    else
                    {
                        // 被排除的样本必须留痕：现场要看得到"测出来了但被规则挡掉"，不能只看到"有效样本不足"。
                        if (!ControlMath.IsFinite(ratio)) _rejectedInvalid++;
                        else if (ratio <= MinRatio) _rejectedBelow++;
                        else if (ratio >= MaxRatio) _rejectedAbove++;
                        else _rejectedInvalid++;
                        if (_rejectedValueCount < _rejectedValues.Length)
                            _rejectedValues[_rejectedValueCount++] = ratio;
                    }
                }
            }

            _phase = plus ? Phase.StopMinus : Phase.StopPlus;
            _phaseSec = 0;
            _n = 0;
        }

        private void Complete()
        {
            RadiusCalibrationResult r = new RadiusCalibrationResult();
            int required = Math.Max(3, _pairs);
            if (_validSamples < required)
            {
                r.Valid = false;
                r.ValidSamples = _validSamples;
                r.Reason = string.Format(CultureInfo.InvariantCulture,
                    "有效测量相不足：{0}/{1}（{2} 对脉冲共 {3} 相，需要 ≥{4}）。" +
                    "先确认摆杆在自然下垂位并基本静止、伺服已使能且跟随正常、|φ̈| ≥ {5} rad/s²；" +
                    "满足后重新 calibrate-radius。",
                    _validSamples, _pairs * 2, _pairs, _pairs * 2, required, MinAbsPhiAccel)
                    + " " + DescribeRejections();
                _result = r;
                _phase = Phase.Failed;
                return;
            }

            double mean = _sumRatio / _validSamples;
            double variance = _sumRatioSq / _validSamples - mean * mean;
            double std = variance > 0 ? Math.Sqrt(variance) : 0;
            r.RadiusRatio = mean;
            r.ValidSamples = _validSamples;
            r.StdDev = std;
            r.MeanAbsPhiAccelRadPerSec2 = _sumAbsPhiAcc / _validSamples;
            r.MeanAbsThetaAccelRadPerSec2 = _sumAbsThetaAcc / _validSamples;
            double relativeStd;
            double allowedStd;
            string rejectReason;
            r.Valid = EvaluateDispersion(mean, std, out relativeStd, out allowedStd, out rejectReason);
            r.RelativeStdDev = relativeStd;
            r.AllowedStdDev = allowedStd;
            // 成功也要把被排除的样本说清楚：现场必须知道均值有没有被区间截断过。
            r.Reason = r.Valid
                ? ((_rejectedAbove + _rejectedBelow + _rejectedInvalid + _rejectedGeometry) > 0
                    ? DescribeRejections() : null)
                : rejectReason;
            _result = r;
            _phase = Phase.Done;
        }

        /// <summary>
        /// 本相排除情况的人话说明：候选数、有效数、低于下限 / 高于上限 / 非法 / 几何不符各多少，
        /// 以及被排除的前几个比值。失败消息与成功附注共用这一份，不许在两处各写一套。
        /// </summary>
        private string DescribeRejections()
        {
            if (_candidateSamples == 0 && _rejectedGeometry == 0)
                return "本次没有进入比值计算的测量相（二次拟合失败 / |φ̈| 或 |θ̈| 不足 / 脉冲方向不符）。";

            string text = "本次共得到 " + _candidateSamples.ToString(CultureInfo.InvariantCulture)
                + " 个候选比值：有效 " + _validSamples.ToString(CultureInfo.InvariantCulture)
                + "，低于下限 " + _rejectedBelow.ToString(CultureInfo.InvariantCulture)
                + "，高于上限 " + _rejectedAbove.ToString(CultureInfo.InvariantCulture)
                + "，非法 " + _rejectedInvalid.ToString(CultureInfo.InvariantCulture);
            if (_rejectedGeometry > 0)
                text += "；另有 " + _rejectedGeometry.ToString(CultureInfo.InvariantCulture)
                    + " 相因摆杆未在下垂位附近（|cosθ|<0.90）未计比值";
            text += "。";
            if (_rejectedValueCount > 0)
            {
                text += "被排除的比值：";
                for (int i = 0; i < _rejectedValueCount; i++)
                {
                    if (i > 0) text += "、";
                    text += double.IsNaN(_rejectedValues[i])
                        ? "NaN"
                        : _rejectedValues[i].ToString("F3", CultureInfo.InvariantCulture);
                }
                if (_candidateSamples > _rejectedValueCount) text += " …";
                text += "。";
            }
            if (_rejectedAbove > 0)
            {
                text += "高于上限 " + MaxRatio.ToString("0.###", CultureInfo.InvariantCulture)
                    + " 说明实测 r/l 可能确实很大：先核对臂轴到摆轴距离 r 与等效摆长 l（l = g/ω_n²）、"
                    + "摆杆/摆臂每转计数，确认机构几何后再决定，不要靠放宽上限硬凑。";
            }
            return text;
        }

        /// <summary>
        /// 唯一有效性判据：估计值区间的检查与**离散度**检查分开报原因。
        /// <para>
        /// 离散度用**同单位比较**：绝对 σ 对绝对门限 max(0.02, |r/l|×25%)。
        /// 相对离散度 σ/|r/l| 只用于展示，不能拿去跟绝对门限比较 ——
        /// 混用会把 σ=0.025（相对 10%）判成“超过 6.2%”而误拒（现场事故口径）。
        /// </para>
        /// </summary>
        /// <param name="relativeStd">σ/|r/l|；均值不可用时为 0。</param>
        /// <param name="allowedStd">本次使用的绝对 σ 允许上限；均值不可用时为 0。</param>
        /// <param name="reason">拒绝原因；有效时为 null。</param>
        internal static bool EvaluateDispersion(double mean, double std,
            out double relativeStd, out double allowedStd, out string reason)
        {
            relativeStd = 0;
            allowedStd = 0;
            reason = null;

            if (!ControlMath.IsFinite(mean) || mean <= MinRatio || mean >= MaxRatio)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "r/l 估计值 {0:F4} 不在几何合理区间 ({1:F4}, {2:F4}) 内（估计本身不可信，与离散度无关）。" +
                    "检查摆杆是否在自然下垂位、脉冲期间伺服是否跟随、软限位是否卡住。",
                    mean, MinRatio, MaxRatio);
                return false;
            }
            if (!ControlMath.IsFinite(std) || std < 0)
            {
                reason = "r/l 估计离散度 σ 非有限（拟合异常）：重新执行 calibrate-radius；若重复出现，保存当次日志再排查。";
                return false;
            }

            relativeStd = std / mean;
            allowedStd = Math.Max(MinStdDevFloor, mean * MaxRelativeStdDev);
            if (std <= allowedStd)
                return true;

            // 能走到这里必然 σ/|r/l| > 25%：绝对门限 ≥ 均值×25%，σ 超过它就不可能还在 25% 以内。
            reason = string.Format(CultureInfo.InvariantCulture,
                "r/l 估计离散度超出允许范围：σ={0:F4}，相对 σ/|r/l|={1:F1}%（允许 ≤{2:F0}%），" +
                "按均值 {3:F4} 折算的绝对门限={4:F4}。检查摆杆是否在自然下垂位、软限位是否卡住、" +
                "伺服是否报跟随误差；稳定后重试 calibrate-radius。",
                std, relativeStd * 100.0, MaxRelativeStdDev * 100.0, mean, allowedStd);
            return false;
        }

        private void Fail(string reason)
        {
            RadiusCalibrationResult r = new RadiusCalibrationResult();
            r.Valid = false;
            r.Reason = reason;
            r.ValidSamples = _validSamples;
            _result = r;
            _phase = Phase.Failed;
        }

        /// <summary>
        /// 最小二乘二次拟合 y = c0 + c1·u + c2·u²（u 为归一化时间，窗口内 ∈[−1,1]），
        /// 返回窗口中点值与 2·c2/tHalf²（= 加速度）。
        /// <para>
        /// **必须归一化时间**：直接用秒做 t 时，60 ms 窗口的法方程会出现 1e-6 量级的
        /// 「大数相减」抵消（s2·s4 − s3²），实测会把 100 rad/s² 拟合成 −10⁴ 量级。
        /// u∈[−1,1] 后 s2≈n/3、s4≈n/5，条件数正常。
        /// </para>
        /// 样本不足或法方程奇异返回 false。全部纯计算，无分配。
        /// </summary>
        private static bool FitQuadratic(double[] t, double[] y, int n,
            out double centerValue, out double secondDerivative)
        {
            centerValue = 0;
            secondDerivative = 0;
            if (n < 8) return false;

            double tMean = 0;
            for (int i = 0; i < n; i++) tMean += t[i];
            tMean /= n;
            double tHalf = (t[n - 1] - t[0]) * 0.5;
            if (!(tHalf > 1e-9) || !ControlMath.IsFinite(tHalf)) return false;

            double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            double sy = 0, suy = 0, su2y = 0;
            for (int i = 0; i < n; i++)
            {
                double u = (t[i] - tMean) / tHalf;
                double u2 = u * u;
                s1 += u;
                s2 += u2;
                s3 += u2 * u;
                s4 += u2 * u2;
                sy += y[i];
                suy += u * y[i];
                su2y += u2 * y[i];
            }

            // 3x3 法方程（对称），克拉默法则。
            double a11 = s0, a12 = s1, a13 = s2;
            double a22 = s2, a23 = s3, a33 = s4;
            double det = a11 * (a22 * a33 - a23 * a23)
                       - a12 * (a12 * a33 - a23 * a13)
                       + a13 * (a12 * a23 - a22 * a13);
            if (!ControlMath.IsFinite(det) || Math.Abs(det) < 1e-18) return false;

            double d0 = sy * (a22 * a33 - a23 * a23)
                      - a12 * (suy * a33 - a23 * su2y)
                      + a13 * (suy * a23 - a22 * su2y);
            // c2 的第三列替换展开（一般式；窗口样本时间均匀时 s1=s3=0 会退化，结果不变）。
            double d2 = a11 * (a22 * su2y - suy * a23)
                      - a12 * (a12 * su2y - suy * a13)
                      + sy * (a12 * a23 - a22 * a13);

            centerValue = d0 / det;
            double c2 = d2 / det;
            secondDerivative = 2.0 * c2 / (tHalf * tHalf);
            return ControlMath.IsFinite(centerValue) && ControlMath.IsFinite(secondDerivative);
        }
    }
}
