using System;
using System.Threading;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 扰动裕度分层。正常 = 回中可按原设计推进；预扰动 = 只告警、不冻结（现有 ψ/ω 让路仍生效）；
    /// 强扰动 = 冻结并重置到实测状态，把全部加速度权限交还平衡；恢复 = 保持冻结直到安静保持期满。
    /// </summary>
    public enum DisturbanceMarginState : byte
    {
        Normal = 0,
        PreDisturbance = 1,
        StrongDisturbance = 2,
        Recovering = 3
    }

    /// <summary>
    /// 外力扰动观测快照。周期线程写，UI / CLI 只读。所有量都是本拍控制状态的拷贝，
    /// 不反向参与任何控制判据。
    /// </summary>
    public struct BalanceDisturbanceSnapshot
    {
        /// <summary>观测器更新序号（每控制拍 +1）。</summary>
        public long Sequence;

        /// <summary>快照是否来自正在平衡的控制拍。false 时其余字段为上次平衡的冻结值。</summary>
        public bool BalanceActive;

        /// <summary>估计的外力角加速度 dHat [rad/s²]，正方向 = +ψ（thetaFromUpright 方向）。</summary>
        public double DisturbanceHatRadPerSec2;

        /// <summary>未滤波模型残差 α_meas − α_pred [rad/s²]，只用于诊断。</summary>
        public double ResidualRadPerSec2;

        /// <summary>0–1 置信度：模型有效、二次拟合残差小、摆臂未饱和、未丢失位置权限。</summary>
        public double Confidence;

        /// <summary>低通估计带宽 [Hz]（1/(2π·τ_lp)）。</summary>
        public double BandwidthHz;

        /// <summary>高通拐点 [Hz]（1/(2π·τ_hp)），用于隔离慢变模型偏置。</summary>
        public double HighPassHz;

        /// <summary>本拍注入平衡 aCmd 的扰动前馈 [rad/s²]。</summary>
        public double CompensationRadPerSec2;

        /// <summary>实测摆杆角加速度 [rad/s²]（40 ms 二次拟合的曲率）。</summary>
        public double MeasuredAccelRadPerSec2;

        /// <summary>模型预测的摆杆角加速度 [rad/s²]。</summary>
        public double PredictedAccelRadPerSec2;

        /// <summary>实测摆臂角加速度 [rad/s²]（40 ms 二次拟合曲率）。</summary>
        public double ArmAccelModelRadPerSec2;

        /// <summary>|φ_ref − φ| [rad]，平衡位置参考的跟踪误差。</summary>
        public double ArmReferenceErrorRad;

        /// <summary>|r − φ| [rad]，回中位置偏置相对实测的偏差；非回中时为 0。</summary>
        public double RecenterBiasErrorRad;

        /// <summary>|φ − 行程中点| [rad]，原 LQR 位置项的位置误差（非回中时也监控）。</summary>
        public double ArmPositionErrorRad;

        /// <summary>模型 g/l = Mgl/JEq [1/s²]。</summary>
        public double ModelGravityOverLength;

        /// <summary>配置的耦合比 r/l（0 = 未标定）。</summary>
        public double ModelCouplingRatio;

        /// <summary>前馈实际使用的分母 b_ff（不低于耦合下限）。</summary>
        public double FeedForwardCouplingRatio;

        /// <summary>当前裕度层。</summary>
        public DisturbanceMarginState MarginState;

        /// <summary>回中是否被本轮强扰动冻结（Strong/Recovering 为 true；Pre 只告警）。</summary>
        public bool RecenterFrozen;

        /// <summary>本拍扰动前馈是否真正启用。</summary>
        public bool FeedForwardEnabled;

        /// <summary>本次平衡会话进入强扰动的次数。</summary>
        public int StrongCount;

        /// <summary>本轮冻结已持续时间 [s]。</summary>
        public double FrozenSeconds;
    }

    /// <summary>
    /// 一拍观测输入。全部标量，按值传递，不分配。
    /// </summary>
    internal struct DisturbanceObserverInput
    {
        public double PsiRad;
        public double OmegaMeasuredRadPerSec;
        public double OmegaEstimateRadPerSec;
        public double PendulumAccelRadPerSec2;
        public bool PendulumAccelValid;
        public double ArmPhiRad;
        public double ArmPhiDotRadPerSec;
        /// <summary>实测摆臂角加速度（40 ms 二次拟合曲率）[rad/s²]。</summary>
        public double ArmAccelMeasuredRadPerSec2;
        public bool ArmAccelValid;
        /// <summary>上一拍真正下发的 aCmd [rad/s²]，只用于饱和反向保护判据。</summary>
        public double ArmCmdAccelPrevRadPerSec2;
        public bool ArmCmdSatPrev;
        public bool ArmCmdRateClippedPrev;
        public bool ArmAuthorityValid;
        public bool MarginGraceWindow;
        public double ArmReferenceErrorRad;
        public double RecenterBiasErrorRad;
        public double ArmPositionErrorRad;
        public double ArmAccMaxRadPerSec2;
        public double OmegaN2;
        public double RadiusRatio;
        public double OmegaResidualRmsRad;
        public double DtSec;
    }

    /// <summary>
    /// 外力扰动观测器（残差型 DOB，带宽 13.3 Hz / 高通 0.16 Hz）。
    /// <para>
    /// 模型：<c>ψ̈ = (Mgl/JEq)·sinψ + (r/l)·aArm + d</c>，单位 rad/s²，
    /// d 的正方向与 <c>thetaFromUpright</c> 相同（正 d 把摆杆往 +ψ 推）。
    /// <c>aArm</c> 取摆臂**实测**角加速度（与摆杆同一套 40 ms 二次拟合曲率）。
    /// 耦合符号由 R22 成功段 2 kHz 遥测回归确定：α 与 aArm 正相关（滞后 12 ms 时
    /// 系数约 +0.065），所以这里是加号；前馈抵消正 d 需要负 aArm。
    /// </para>
    /// <para>
    /// 角加速度用现有 <see cref="PendulumOmegaEstimator"/> 的 40 ms 二次最小二乘曲率
    /// <c>θ̈ = 2·c₂/halfSpan²</c>，不用未滤波的位置差分；再做 0.16 Hz 高通 + 13.3 Hz 低通。
    /// 高通只去掉慢变模型偏置（标定误差 / 静摩擦），手碰这种毫秒级脉冲完整通过。
    /// </para>
    /// <para>
    /// 前馈：<c>aCmd_ff = −k_ff·dHat/b_ff</c>。k_ff=0.5、权限 ≤ min(200, 0.20·accMax) rad/s²，
    /// 始终给 LQR 留 ≥80% 加速度权限。b_ff = max(r/l, 0.20)：历史回归 r/l 0.015~0.057 被编码器
    /// 量化压小 1~2 个数量级，直接拿它做分母会把前馈放大 4~12 倍；机构标定值 0.24 是当前可信口径。
    /// r/l=0（未标定）时前馈关闭，只保留裕度冻结。
    /// </para>
    /// <para>
    /// 线程约束：<see cref="Update"/> / <see cref="Reset"/> / <see cref="MarkInactive"/> 只由周期线程调用；
    /// UI / CLI 只走 <see cref="TryReadSnapshot"/>。Update 路径无分配、无字符串拼接。
    /// </para>
    /// </summary>
    internal sealed class DisturbanceObserver
    {
        // ---- 观测器带宽 / 滤波 ----
        /// <summary>角加速度估计低通时间常数 [s]：12 ms ⇒ 13.3 Hz。高于 LQR 带宽（≈1.4 Hz），低于编码器噪声主带。</summary>
        public const double LowPassTauSeconds = 0.012;

        /// <summary>高通时间常数 [s]：1.0 s ⇒ 0.16 Hz，只隔离慢变模型偏置。</summary>
        public const double HighPassTauSeconds = 1.0;

        // 摆臂实测角加速度的拟合窗口与摆杆一致（40 ms 二次拟合，见 PendulumOmegaEstimator）。

        /// <summary>估计带宽 [Hz] = 1/(2π·τ_lp)。</summary>
        public static double BandwidthHz
        {
            get { return 1.0 / (ControlMath.TwoPi * LowPassTauSeconds); }
        }

        /// <summary>高通拐点 [Hz]。只用于诊断。</summary>
        public static double HighPassHz
        {
            get { return 1.0 / (ControlMath.TwoPi * HighPassTauSeconds); }
        }

        // ---- 前馈 ----
        /// <summary>前馈比例增益（无量纲）。0.5 给 r/l 误差留 50% 增益裕量。</summary>
        public const double FeedForwardGain = 0.5;

        /// <summary>前馈分母下限：防止被量化压小的历史 r/l 把前馈放大。</summary>
        public const double CompensationCouplingFloor = 0.20;

        /// <summary>前馈权限占当前 accMax 的比例：始终给 LQR 留 ≥80% 权限。</summary>
        public const double CompensationAuthorityRatio = 0.20;

        /// <summary>前馈绝对权限上限 [rad/s²]。</summary>
        public const double MaxCompensationRadPerSec2 = 200.0;

        /// <summary>前馈一阶平滑时间常数 [s]。</summary>
        public const double CompensationFilterTauSeconds = 0.005;

        /// <summary>前馈变化率上限 [rad/s³]：20000 时 200 rad/s² 约 10 ms 建立，防阶跃。</summary>
        public const double CompensationSlewRadPerSec3 = 20000.0;

        /// <summary>dHat 死区下界 [rad/s²]：低于它不注入前馈，避免噪声来回打臂。</summary>
        public const double FeedForwardDeadbandLowRadPerSec2 = 1.0;

        /// <summary>dHat 死区上界 [rad/s²]：到它前馈权重升到 1（SmoothStep）。</summary>
        public const double FeedForwardDeadbandHighRadPerSec2 = 3.0;

        /// <summary>前馈所需最低置信度。</summary>
        public const double FeedForwardConfidenceMin = 0.25;

        // ---- 裕度分层门限（均低于捕获门 0.15 rad / 0.8 rad/s，远低于 ThetaLost 0.40） ----
        public const double PrePsiRad = 0.035;
        public const double PreOmegaRadPerSec = 0.35;
        public const double PreDisturbanceRadPerSec2 = 2.5;

        public const double StrongPsiRad = 0.075;
        public const double StrongOmegaRadPerSec = 0.75;
        public const double StrongDisturbanceRadPerSec2 = 7.0;
        public const double StrongSatPsiRad = 0.04;
        public const double StrongSatOmegaRadPerSec = 0.40;

        public const double RecoveryPsiRad = 0.025;
        public const double RecoveryOmegaRadPerSec = 0.20;
        public const double RecoveryDisturbanceRadPerSec2 = 1.2;
        public const double RecoveryHoldSeconds = 0.25;

        /// <summary>强扰动冻结的最长持续时间 [s]：超过它且 ψ/ω 已安静时按模型偏置处理，解除回中冻结。</summary>
        public const double StrongFreezeTimeoutSeconds = 1.0;

        /// <summary>二次拟合角加速度限幅 [rad/s²]：超过它按坏样本处理，绝不把数值打爆。</summary>
        public const double AccelerationClampRadPerSec2 = 2000.0;

        /// <summary>环境变量 FURUTA_DIST_FF=0 可关闭前馈（保留裕度冻结），用于台架 A/B。</summary>
        public static readonly bool FeedForwardEnvironmentEnabled = ReadEnvironmentFlag("FURUTA_DIST_FF", true);

        /// <summary>
        /// 观测器总开关，默认关：R25 台架 12:05–12:09 的两次实测比接入前更差，
        /// 先回到已验证的纯 PD/回中路径。台架显式设 FURUTA_DIST_OBS=1 才启用观测与前馈。
        /// </summary>
        public static readonly bool ObservationEnvironmentEnabled = ReadEnvironmentFlag("FURUTA_DIST_OBS", false);

        // ---- 周期线程独占状态 ----
        private double _armAccelModel;
        private bool _dcPrimed;
        private double _dcSlow;
        private double _disturbanceRaw;
        private double _disturbanceHat;

        private double _confidence;
        private double _compensation;
        private double _omegaN2;
        private double _radiusRatio;
        private double _feedForwardCoupling;
        private bool _modelValid;
        private bool _feedForwardEnabled;
        private double _measuredAccel;
        private double _predictedAccel;
        private double _armReferenceError;
        private double _recenterBiasError;
        private double _armPositionError;
        private double _omegaAbs;

        private DisturbanceMarginState _marginState = DisturbanceMarginState.Normal;
        private double _recoverySec;
        private double _frozenSec;
        private int _strongCount;
        private bool _recenterFrozen;

        private bool _balanceActive;
        private long _updateSequence;

        // ---- 快照 seqlock（周期线程写、UI/CLI 只读） ----
        private int _snapshotSeq;
        private BalanceDisturbanceSnapshot _snapshot;

        /// <summary>估计的外力角加速度 [rad/s²]（周期线程写，UI 请走快照）。</summary>
        public double DisturbanceHatRadPerSec2
        {
            get { return _disturbanceHat; }
        }

        /// <summary>当前裕度层。</summary>
        public DisturbanceMarginState MarginState
        {
            get { return _marginState; }
        }

        /// <summary>本拍回中是否应冻结。</summary>
        public bool RecenterFrozen
        {
            get { return _recenterFrozen; }
        }

        /// <summary>本拍前馈 [rad/s²]。</summary>
        public double CompensationRadPerSec2
        {
            get { return _compensation; }
        }

        /// <summary>0–1 置信度。</summary>
        public double Confidence
        {
            get { return _confidence; }
        }

        /// <summary>实测摆臂角加速度 [rad/s²]（40 ms 二次拟合）。</summary>
        public double ArmAccelModelRadPerSec2
        {
            get { return _armAccelModel; }
        }

        /// <summary>未滤波模型残差 [rad/s²]。</summary>
        public double ResidualRadPerSec2
        {
            get { return _disturbanceRaw; }
        }

        /// <summary>裕度层中文名。只返回字符串常量，不在热路径分配。</summary>
        public static string MarginStateName(DisturbanceMarginState state)
        {
            switch (state)
            {
                case DisturbanceMarginState.Normal: return "正常";
                case DisturbanceMarginState.PreDisturbance: return "预扰动";
                case DisturbanceMarginState.StrongDisturbance: return "强扰动";
                case DisturbanceMarginState.Recovering: return "恢复";
                default: return "未知";
            }
        }

        /// <summary>进入平衡时清零。只由周期线程调用。</summary>
        public void Reset(double omegaN2, double radiusRatio)
        {
            _armAccelModel = 0.0;
            _dcPrimed = false;
            _dcSlow = 0.0;
            _disturbanceRaw = 0.0;
            _disturbanceHat = 0.0;
            _confidence = 0.0;
            _compensation = 0.0;
            _omegaN2 = omegaN2;
            _radiusRatio = radiusRatio;
            _feedForwardCoupling = 0.0;
            _modelValid = false;
            _feedForwardEnabled = false;
            _measuredAccel = 0.0;
            _predictedAccel = 0.0;
            _armReferenceError = 0.0;
            _recenterBiasError = 0.0;
            _armPositionError = 0.0;
            _omegaAbs = 0.0;
            _marginState = DisturbanceMarginState.Normal;
            _recoverySec = 0.0;
            _frozenSec = 0.0;
            _strongCount = 0;
            _recenterFrozen = false;
            _balanceActive = false;
            _updateSequence = 0;
            PublishSnapshot();
        }

        /// <summary>离开平衡时把快照标成非活动；不重置估计器状态（下次 Reset 会清）。</summary>
        public void MarkInactive()
        {
            if (!_balanceActive)
                return;
            _balanceActive = false;
            _recenterFrozen = false;
            _compensation = 0.0;
            _feedForwardEnabled = false;
            PublishSnapshot();
        }

        /// <summary>
        /// 推进一拍观测。输入全部是标量，路径无分配；只允许周期线程调用。
        /// </summary>
        public void Update(DisturbanceObserverInput input)
        {
            double dt = input.DtSec;
            if (!ControlMath.IsFinite(dt) || dt <= 0.0 || dt > 0.05)
                return;

            _balanceActive = true;
            _updateSequence++;

            // 总开关关闭时只发布中性快照：不冻结回中、不注入前馈、不改参考。
            if (!ObservationEnvironmentEnabled)
            {
                _disturbanceHat = 0.0;
                _disturbanceRaw = 0.0;
                _confidence = 0.0;
                _compensation = 0.0;
                _marginState = DisturbanceMarginState.Normal;
                _recenterFrozen = false;
                _frozenSec = 0.0;
                _feedForwardEnabled = false;
                PublishSnapshot();
                return;
            }

            // 模型参数变化（运行中改参数）时清估计器，避免旧残差被当成新扰动。
            if (!NearlyEqual(input.OmegaN2, _omegaN2) || !NearlyEqual(input.RadiusRatio, _radiusRatio))
            {
                _omegaN2 = input.OmegaN2;
                _radiusRatio = input.RadiusRatio;
                _dcPrimed = false;
                _dcSlow = 0.0;
                _disturbanceHat = 0.0;
                _disturbanceRaw = 0.0;
                _compensation = 0.0;
            }

            bool modelValid =
                ControlMath.IsFinite(input.PsiRad) &&
                ControlMath.IsFinite(input.OmegaMeasuredRadPerSec) &&
                ControlMath.IsFinite(input.OmegaEstimateRadPerSec) &&
                ControlMath.IsFinite(input.ArmPhiRad) &&
                ControlMath.IsFinite(input.ArmPhiDotRadPerSec) &&
                ControlMath.IsFinite(input.ArmCmdAccelPrevRadPerSec2) &&
                input.ArmAccelValid && ControlMath.IsFinite(input.ArmAccelMeasuredRadPerSec2) &&
                ControlMath.IsFinite(input.OmegaN2) && input.OmegaN2 > 1.0 && input.OmegaN2 < 500.0 &&
                ControlMath.IsFinite(input.RadiusRatio) && input.RadiusRatio > 0.0 &&
                input.PendulumAccelValid && ControlMath.IsFinite(input.PendulumAccelRadPerSec2);
            _modelValid = modelValid;

            // 摆臂角加速度用**实测**（与摆杆同一套 40 ms 二次拟合）。下发 aCmd 在驱动器
            // 转矩/加速度上限外并不被执行，拿它预测会把未执行量当成外力。
            _armAccelModel = input.ArmAccelMeasuredRadPerSec2;
            if (!ControlMath.IsFinite(_armAccelModel))
                _armAccelModel = 0.0;

            double measuredAccel = 0.0;
            double predictedAccel = 0.0;
            double rawResidual = 0.0;
            if (modelValid)
            {
                measuredAccel = ControlMath.Clamp(
                    input.PendulumAccelRadPerSec2,
                    -AccelerationClampRadPerSec2, AccelerationClampRadPerSec2);
                predictedAccel = input.OmegaN2 * Math.Sin(input.PsiRad)
                    + input.RadiusRatio * _armAccelModel;
                if (!ControlMath.IsFinite(predictedAccel))
                    predictedAccel = 0.0;
                rawResidual = ControlMath.Clamp(
                    measuredAccel - predictedAccel,
                    -AccelerationClampRadPerSec2, AccelerationClampRadPerSec2);
            }

            // 置信度：拟合残差 + 摆臂饱和/限速 + 位置权限。
            double confidence = 0.0;
            if (modelValid)
            {
                double fitResidual = ControlMath.IsFinite(input.OmegaResidualRmsRad)
                    ? Math.Abs(input.OmegaResidualRmsRad)
                    : 0.03;
                double fitQuality = ControlMath.Clamp(1.0 - fitResidual / 0.03, 0.0, 1.0);
                double authority = 1.0;
                if (input.ArmCmdSatPrev) authority *= 0.5;
                if (input.ArmCmdRateClippedPrev) authority *= 0.5;
                if (!input.ArmAuthorityValid) authority = 0.0;
                if (input.MarginGraceWindow) authority *= 0.5;
                confidence = ControlMath.Clamp(fitQuality * authority, 0.0, 1.0);
            }
            _confidence = confidence;

            // 带通：先高通去慢偏置，再低通压噪声。
            if (modelValid)
            {
                if (!_dcPrimed)
                {
                    // 首个有效样本作为高通初值，避免接管瞬间的模型残差被当成扰动。
                    // 若手碰恰好发生在首个样本，dHat 会被这个初值吃掉；此时 margin 仍靠 ψ/ω 快速触发。
                    _dcSlow = rawResidual;
                    _dcPrimed = true;
                }
                else
                {
                    double dcAlpha = dt / (HighPassTauSeconds + dt);
                    _dcSlow += (rawResidual - _dcSlow) * dcAlpha;
                }
                double highPassed = rawResidual - _dcSlow;
                double lpAlpha = dt / (LowPassTauSeconds + dt);
                _disturbanceHat += (highPassed - _disturbanceHat) * lpAlpha;
                if (!ControlMath.IsFinite(_disturbanceHat))
                    _disturbanceHat = 0.0;
                _disturbanceRaw = rawResidual;
            }
            else
            {
                _disturbanceHat = 0.0;
                _disturbanceRaw = 0.0;
                _dcSlow = 0.0;
                _dcPrimed = false;
            }

            // 裕度分层：ω 同时看 2 ms 实测与 40 ms 估计，取较大者，避免估计滞后漏掉快碰。
            double psiAbs = ControlMath.IsFinite(input.PsiRad) ? Math.Abs(input.PsiRad) : 0.0;
            double omegaAbs = Math.Max(
                Math.Abs(ControlMath.IsFinite(input.OmegaMeasuredRadPerSec) ? input.OmegaMeasuredRadPerSec : 0.0),
                Math.Abs(ControlMath.IsFinite(input.OmegaEstimateRadPerSec) ? input.OmegaEstimateRadPerSec : 0.0));
            double disturbanceAbs = Math.Abs(_disturbanceHat);
            bool disturbanceUsable = _confidence >= FeedForwardConfidenceMin;
            // 参考跟踪误差（φ_ref−φ、r−φ）只作为诊断字段，**不参与**裕度分层：
            // r 是回中外环的慢速参考，正常回中时 r−φ 可以接近整个行程；把它当扰动证据
            // 会让回中自己的正常推进触发冻结，re-prime 又把参考打回实测，形成自锁振荡。
            bool saturated = input.ArmCmdSatPrev || input.ArmCmdRateClippedPrev;

            // margin 静默窗（接管后 0.50 s）内完全不动：R22 的 250→800 只在前 100 ms，
            // 捕获门 / K 增益 / topBrake 保持不变，由既有 LQR / ThetaLost / ThetaTrip 接管。
            // 静默窗后才允许 margin 冻结 / re-prime，避免把“接管瞬间本来就大的 ψ/ω”误判成外部扰动。
            bool grace = input.MarginGraceWindow;
            bool pre = !grace && (
                       psiAbs > PrePsiRad ||
                       omegaAbs > PreOmegaRadPerSec ||
                       (disturbanceUsable && disturbanceAbs > PreDisturbanceRadPerSec2) ||
                       saturated);
            bool strong = !grace && (
                          psiAbs > StrongPsiRad ||
                          omegaAbs > StrongOmegaRadPerSec ||
                          (disturbanceUsable && disturbanceAbs > StrongDisturbanceRadPerSec2) ||
                          (saturated && (psiAbs > StrongSatPsiRad || omegaAbs > StrongSatOmegaRadPerSec)));
            bool calm = psiAbs <= RecoveryPsiRad &&
                        omegaAbs <= RecoveryOmegaRadPerSec &&
                        !saturated &&
                        (!disturbanceUsable || grace ||
                         disturbanceAbs <= RecoveryDisturbanceRadPerSec2 ||
                         _frozenSec >= StrongFreezeTimeoutSeconds);

            switch (_marginState)
            {
                case DisturbanceMarginState.Normal:
                    if (strong)
                    {
                        EnterStrong();
                    }
                    else if (pre)
                    {
                        _marginState = DisturbanceMarginState.PreDisturbance;
                        _recoverySec = 0.0;
                    }
                    break;

                case DisturbanceMarginState.PreDisturbance:
                    if (strong)
                    {
                        EnterStrong();
                    }
                    else if (calm)
                    {
                        _recoverySec += dt;
                        if (_recoverySec >= RecoveryHoldSeconds)
                        {
                            _marginState = DisturbanceMarginState.Normal;
                            _recoverySec = 0.0;
                        }
                    }
                    else
                    {
                        _recoverySec = 0.0;
                    }
                    break;

                case DisturbanceMarginState.StrongDisturbance:
                    if (!strong)
                    {
                        _marginState = DisturbanceMarginState.Recovering;
                        _recoverySec = 0.0;
                    }
                    else if (_frozenSec >= StrongFreezeTimeoutSeconds &&
                             psiAbs <= RecoveryPsiRad &&
                             omegaAbs <= RecoveryOmegaRadPerSec &&
                             !saturated)
                    {
                        // 观测残差长期偏高但摆杆本身已安静 1 s：更像模型偏置而不是外力。
                        // 只解除回中冻结（前馈仍按 dHat 注入），避免一个符号/参数误差把回中永久锁死。
                        _marginState = DisturbanceMarginState.Recovering;
                        _recoverySec = 0.0;
                    }
                    break;

                case DisturbanceMarginState.Recovering:
                    if (strong)
                    {
                        EnterStrong();
                    }
                    else if (calm)
                    {
                        _recoverySec += dt;
                        if (_recoverySec >= RecoveryHoldSeconds)
                        {
                            _marginState = DisturbanceMarginState.Normal;
                            _recoverySec = 0.0;
                        }
                    }
                    else
                    {
                        _recoverySec = 0.0;
                    }
                    break;
            }

            _recenterFrozen = _marginState == DisturbanceMarginState.StrongDisturbance ||
                              _marginState == DisturbanceMarginState.Recovering;
            if (_recenterFrozen)
                _frozenSec += dt;
            else
                _frozenSec = 0.0;

            // 前馈：物理一致地注入加速度域，权限始终给 LQR 留 ≥80%。
            double feedForwardCoupling = input.RadiusRatio;
            if (feedForwardCoupling > 0.0 && feedForwardCoupling < CompensationCouplingFloor)
                feedForwardCoupling = CompensationCouplingFloor;
            _feedForwardCoupling = feedForwardCoupling;

            bool feedForwardEnabled = FeedForwardEnvironmentEnabled &&
                                      modelValid &&
                                      !input.MarginGraceWindow &&
                                      input.ArmAuthorityValid &&
                                      _confidence >= FeedForwardConfidenceMin &&
                                      feedForwardCoupling > 0.0;
            double feedForwardTarget = 0.0;
            if (feedForwardEnabled)
            {
                double weight = ControlMath.SmoothStep(
                    FeedForwardDeadbandLowRadPerSec2,
                    FeedForwardDeadbandHighRadPerSec2,
                    disturbanceAbs);
                // 抵消 d 需要 b·aArm = −d（耦合项是加号），所以前馈与 dHat 反号。
                feedForwardTarget = -_disturbanceHat / feedForwardCoupling * FeedForwardGain * weight;
                double cap = Math.Min(
                    MaxCompensationRadPerSec2,
                    CompensationAuthorityRatio * input.ArmAccMaxRadPerSec2);
                if (!(cap > 10.0) || !ControlMath.IsFinite(cap))
                    cap = 10.0;
                feedForwardTarget = ControlMath.Clamp(feedForwardTarget, -cap, cap);
                // 上一拍 PD 已饱和且前馈要反向抵消它：本拍不注入，保住唯一的纠偏权限。
                if (input.ArmCmdSatPrev &&
                    feedForwardTarget * input.ArmCmdAccelPrevRadPerSec2 < 0.0)
                {
                    feedForwardTarget = 0.0;
                }
            }

            double compAlpha = dt / (CompensationFilterTauSeconds + dt);
            double compFiltered = _compensation + (feedForwardTarget - _compensation) * compAlpha;
            _compensation = ControlMath.Slew(
                compFiltered, _compensation, CompensationSlewRadPerSec3 * dt);
            if (!ControlMath.IsFinite(_compensation))
                _compensation = 0.0;
            _feedForwardEnabled = feedForwardEnabled;

            _measuredAccel = measuredAccel;
            _predictedAccel = predictedAccel;
            _armReferenceError = ControlMath.IsFinite(input.ArmReferenceErrorRad)
                ? Math.Abs(input.ArmReferenceErrorRad) : 0.0;
            _recenterBiasError = ControlMath.IsFinite(input.RecenterBiasErrorRad)
                ? Math.Abs(input.RecenterBiasErrorRad) : 0.0;
            _armPositionError = ControlMath.IsFinite(input.ArmPositionErrorRad)
                ? Math.Abs(input.ArmPositionErrorRad) : 0.0;
            _omegaAbs = omegaAbs;

            PublishSnapshot();
        }

        /// <summary>读一拍快照；seqlock 正在写时返回 false，调用方本帧跳过。</summary>
        public bool TryReadSnapshot(out BalanceDisturbanceSnapshot snapshot)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int seq1 = Volatile.Read(ref _snapshotSeq);
                if ((seq1 & 1) != 0)
                    continue;
                Thread.MemoryBarrier();
                snapshot = _snapshot;
                Thread.MemoryBarrier();
                int seq2 = Volatile.Read(ref _snapshotSeq);
                if (seq1 == seq2)
                    return true;
            }
            snapshot = default(BalanceDisturbanceSnapshot);
            return false;
        }

        private void EnterStrong()
        {
            _marginState = DisturbanceMarginState.StrongDisturbance;
            _recoverySec = 0.0;
            _strongCount++;
        }

        private void PublishSnapshot()
        {
            int seq = _snapshotSeq;
            Volatile.Write(ref _snapshotSeq, seq + 1);
            Thread.MemoryBarrier();

            BalanceDisturbanceSnapshot s = new BalanceDisturbanceSnapshot();
            s.Sequence = _updateSequence;
            s.BalanceActive = _balanceActive;
            s.DisturbanceHatRadPerSec2 = _disturbanceHat;
            s.ResidualRadPerSec2 = _disturbanceRaw;
            s.Confidence = _confidence;
            s.BandwidthHz = BandwidthHz;
            s.HighPassHz = HighPassHz;
            s.CompensationRadPerSec2 = _compensation;
            s.MeasuredAccelRadPerSec2 = _measuredAccel;
            s.PredictedAccelRadPerSec2 = _predictedAccel;
            s.ArmAccelModelRadPerSec2 = _armAccelModel;
            s.ArmReferenceErrorRad = _armReferenceError;
            s.RecenterBiasErrorRad = _recenterBiasError;
            s.ArmPositionErrorRad = _armPositionError;
            s.ModelGravityOverLength = _omegaN2;
            s.ModelCouplingRatio = _radiusRatio;
            s.FeedForwardCouplingRatio = _feedForwardCoupling;
            s.MarginState = _marginState;
            s.RecenterFrozen = _recenterFrozen;
            s.FeedForwardEnabled = _feedForwardEnabled;
            s.StrongCount = _strongCount;
            s.FrozenSeconds = _frozenSec;
            _snapshot = s;

            Thread.MemoryBarrier();
            Volatile.Write(ref _snapshotSeq, seq + 2);
        }

        private static bool NearlyEqual(double a, double b)
        {
            if (double.IsNaN(a) && double.IsNaN(b))
                return true;
            return Math.Abs(a - b) <= 1e-12;
        }

        private static bool ReadEnvironmentFlag(string name, bool fallback)
        {
            string text = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(text))
                return fallback;
            if (text == "0" ||
                text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;
            if (text == "1" ||
                text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;
            return fallback;
        }
    }
}
