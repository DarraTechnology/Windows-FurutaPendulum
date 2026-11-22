using System;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 能量法起摆控制器。单律：能量注入/抽取 + 摆臂回中阻尼 + 可选往复泵 + 软中心带。
    /// 不需要精确建模，J_eq 与 mgl 只要相对量对就行，绝对值不敏感。
    /// 周期线程独占一个实例，逐拍推进，零分配。
    /// <para>
    /// 与旧版的差别（2026-11-16 修正）：旧版把「抖速」当独立分支，|ω| 不过闸门就永远出不去，
    /// 而能量项连符号都写成抽能方向，实测 30 s 只到 0.0008 J（目标的 0.04%）。
    /// 现版按 dE/dt = −m·l·r·θ̇·cosθ·φ̈ 直接取 φ̈ = −u_E·sign(θ̇·cosθ)，
    /// 不再设 |ω| 闸门 —— 标准 Furuta 能量法没有最小角速度要求，从静止（靠起动微扰打破对称）即可起振。
    /// </para>
    /// <para>
    /// 2026-11-20 台架补充：① 能量项扣除一阶低通直流分量（波形不对称会让摆臂单向漂）；
    /// ② ΔE 为负时按 20% AMax 的权限抽取（只注入不抽取会让摆杆带着 E&gt;2mgl 整圈转、起摆时间 8~180 s 乱跳）；
    /// ③ 软中心带：|φ| 预测刹车距离超出带半径时暂停注入、满权限回中（撞软限位前的主动保护）。
    /// </para>
    /// </summary>
    internal sealed class SwingUpController
    {
        // 往复泵的门限：|ω| 小于这个值才开泵，随 |ω| 增大线性关掉。
        // 泵**默认关闭**（PumpAmplitude = 0）：闭环能量项自己会找相位，开环正弦只会跟它抢拍。
        private const double PumpGateOmegaRadPerSec = 1.0;

        /// <summary>
        /// 顶部速度抽取制动的目标 [rad/s]：预测顶部 |ω| 超过它就追加抽取，把顶部速度压回捕获带。
        /// 0.8 与捕获判据的物理边界 |ψ+ω/ω_n|≤0.088（ψ≈0 时 |ω|≤0.8）一致；
        /// 不用 commitOmega=1.2，因为 1.2 已接近平衡包线边缘，交棒后容易被真实状态裁决掉。
        /// </summary>
        private const double TopSpeedBrakeOmegaTargetRadPerSec = 0.8;

        /// <summary>
        /// 顶部速度抽取制动的增益 [rad/s² per (rad/s)]：每超出目标 1 rad/s，追加 100 rad/s² 抽取。
        /// 仍受 <c>extractMax = AMax·EnergyExtractRatio</c> 钳位（默认 100 rad/s²），
        /// 不新增速度限幅；只是把「E 超目标」造成的顶部速度提前抽掉。
        /// </summary>
        private const double TopSpeedBrakeGainRadPerSec2PerOmega = 100.0;

        /// <summary>
        /// 顶部制动用的 fastω 短滤波时间常数 [s]：8 ms。输入是 2 ms 实测 ω（已低通），
        /// 再 8 ms 抑制量化噪声；仍远快于 40 ms 最小二乘估计。R12 失败：40 ms ω_est 在高加速度
        /// 下把 E 高估到 2.30 J（真实约 2.05 J），顶部制动按虚假高能一直满抽，形成过抽/再注入极限环。
        /// </summary>
        private const double FastOmegaFilterTauSeconds = 0.008;
        private double _fastOmega;
        private bool _fastOmegaInit;

        /// <summary>
        /// 起摆能量环的积分增益 [rad/s² per (J·s)]。纯比例律 ke·ΔE 在目标附近推不过摩擦：
        /// 台架 E 停在 1.5~1.6 J、离线下停在 E*−0.02 J，折返点永远差几毫弧度进不了捕获门。
        /// 积分项把「摩擦造成的固定能量缺口」补到 0，让能量真正收敛到 E*；
        /// 泄放（<see cref="EnergyIntegralLeakSeconds"/>）保证超目标后不会带着一大坨存量再次猛推。
        /// </summary>
        internal const double EnergyIntegralGain = 300.0;

        /// <summary>能量积分在死区 / 超目标后的泄放时间常数 [s]。只泄放不反向，避免积分变成抽能。</summary>
        private const double EnergyIntegralLeakSeconds = 1.5;

        /// <summary>内置默认参数。旧参数文件缺抖速 / 调度 / ω 窗口这些键时按它补齐（周期路径只读）。</summary>
        private static readonly SwingUpParams DefaultParams = SwingUpParams.CreateDefault();

        // 与平衡共用同一个 40 ms 最小二乘估计实现。
        private readonly PendulumOmegaEstimator _omegaEstimator = new PendulumOmegaEstimator();

        private double _phiCmd;
        private double _phiDotCmd;
        private double _phaseSec;
        private double _pumpPhase;
        // 慢/快通道的近顶端驻留计数，单位 = 千分之一拍（milli-cycle）。
        // 用有理数累加器而不是整数拍：单次掠顶门只开 ~14 拍，两次掠顶间隔 1500~3000 拍，
        // 每拍衰减必须 < 14/3000 ≈ 0.0047 拍才能跨周期累积；整数"−1 拍/拍"在数学上不可能。
        private long _switchHoldMilli;
        private long _commitHoldMilli;
        // 回中饱和率与 φ 均值的滚动诊断（τ=1 s 一阶指数平均，零额外分配）。
        // 台架成功轮 φ_mean=+0.24、回中饱和 0%；失败轮 φ_mean≈−1.0、饱和 77~78%——
        // 这两个数比事后反推 φ 更直接，交棒前就能判断是不是掉进失败轮状态。
        private double _centerSatRatio;
        private double _phiMean;
        private const double CenterSatTauSeconds = 1.0;

        // ---- 折返角反馈（A/B-4）：把能量目标偏置按每个 pass 的折返角修正 ----
        // 精度来自角度（θ 量化 0.0026 rad），不来自能量（摆动中 E 噪声 0.04~0.10 J）。
        // 方向（物理）：E=mgl(1+cosψ_turn) ⇒ 折返角越大 = 能量越低。
        //   ψ_turn > 0.09 → 能量偏低 → 目标抬高一档（抬高能量，折返角变小）；
        //   ψ_turn < 0.06 → 能量偏高 → 目标降低一档（降低能量，折返角变大）；
        //   0.06~0.09 → 保持不动（死区，防抖）。
        // 依据：旧 120-pass 离线在 0.06~0.09 带内 10/10、中位 0.069；新平衡包线
        // 0.162(ω=0)/0.123(ω=0.3)/0.096(ω=0.5) 完全覆盖 0.069~0.089 的折返/ω≈0。
        // 0.03~0.05 带对应的能量带只有 0.0008 J，小于泵单 pass 量子（~0.017 J），不可达。
        // 每 pass 最多一档；dψ/dδ≈11~16 rad/J，0.002 J 对应约 0.02~0.03 rad。
        private double _turnBiasJ;
        private bool _turnVisitActive;
        private double _turnMinAbsPsi;
        private int _turnOmegaSign;
        // 偏置权限（2026-11-21 第十六轮）：实测过冲 0.07~0.15 J、停滞缺口 0.5~1.0 J，
        // 旧的 ±0.02 只有需求的 1/4~1/50，永远够不着。现在目标可在 2mgl±0.15 J 内调。
        private const double TurnBiasStepJ = 0.002;          // 误差小：精细步
        private const double TurnBiasFastStepJ = 0.01;       // 误差中等：快速步
        private const double TurnBiasExtremeStepJ = 0.03;    // 过冲 ≥0.10 J：≤5 pass 拉回
        private const double TurnFastEnergyErrorJ = 0.03;
        private const double TurnExtremeEnergyErrorJ = 0.10;
        private const double TurnBiasMaxJ = 0.15;            // 目标物理上下界 2mgl±0.15 J
        private const double TurnTargetMarginJ = 0.15;
        private const double TurnAngleLowRad = 0.06;
        private const double TurnAngleHighRad = 0.09;
        private const double TurnTrackZoneRad = 0.5;
        private const double TurnHysteresisRadPerSec = 0.05;
        // 能量停滞：连续 3 s 没有新高、且最近离直立仍 >0.30 rad ⇒ 按 +0.01 J 抬目标。
        private const double StallSeconds = 3.0;
        private const double StallBumpJ = 0.01;
        private const double StallRiseEpsilonJ = 0.003;
        private const double StallFarPsiRad = 0.30;
        // 条件式顶部助推：只在大能量缺口且远离顶部时抬高淡出 floor，不碰 0.06~0.09 折返带。
        private const double FadeBoostDeltaJ = 0.05;
        private const double FadeBoostPsiRad = 0.30;
        private const double FadeBoostFloor = 0.70;
        private double _maxEnergySeen;
        private double _stallTimerSec;
        private double _stallMinUprightRad;
        private bool _running;
        private uint _paramsGeneration;

        // 能量注入项的直流分量估计 [rad/s²]。
        // 为什么要估: -uE·softSign(ω·cosθ) 虽然随 ω 翻符号，但摆杆在一侧停留时间比对侧长，
        // 波形本身不对称 → 一个摆动周期下来净推力恒指向同一侧 → 摆臂一路单向漂走。
        // 台架实测 (2026-11-20, 同一套增益): 1 ms 周期 40 s 漂到 -40290 counts、
        // 500 µs 周期 40 s 漂到 -41891 counts，提频率和开正弦泵都压不住，说明偏置来自这项本身。
        // 做法: 一阶低通估计它的直流分量，本拍减掉，只保留与摆动同步的交变推力。
        private double _energyBias;
        private const double EnergyBiasTauSeconds = 1.0;
        /// <summary>
        /// 能量项不动作（uE=0）时直流估计的泄漏时间常数 [s]。只做慢泄漏、不按零样本学习：
        /// 按零样本学等于每次进出死区 / 回中 / 准备都把刚学到的直流抹掉，回来那 1 s 净直流重新作用。
        /// </summary>
        private const double EnergyBiasHoldSeconds = 10.0;

        // 能量环积分 [J·s]：补偿摩擦等未建模损耗造成的稳态能量缺口。只由周期线程读写。
        private double _energyIntegral;

        // 本拍诊断。只由周期线程写，遥测拷走，读侧不改。
        public double EnergyJ;
        public double EnergyTargetJ;
        public double EnergyDeltaJ;
        public double PhaseSec;
        public int SwitchCountdown;

        /// <summary>本拍入口平衡命令 aCmdEntry [rad/s²]（HEAD 增益，只读诊断）。</summary>
        public double CaptureEntryAccelRadPerSec2;

        /// <summary>本拍是否通过当前实测入口包线（只读诊断）。</summary>
        public bool CaptureCurrentStateOk;

        /// <summary>本拍 CaptureGateAllows 的返回值（只读诊断）。</summary>
        public bool CaptureHeadGateOk;

        /// <summary>本拍最终 ready（当前包线 + HEAD 预测门，只读诊断）。</summary>
        public bool CaptureReady;

        /// <summary>本拍入口分组预算：摆杆项 aPend [rad/s²]（只读诊断）。</summary>
        public double CapturePendAccelRadPerSec2;

        /// <summary>本拍入口分组预算：摆臂项 aArm [rad/s²]（只读诊断）。</summary>
        public double CaptureArmAccelRadPerSec2;

        /// <summary>本拍入口分组预算：总项 a1 [rad/s²]（只读诊断）。</summary>
        public double CaptureTotalAccelRadPerSec2;

        /// <summary>本拍预测不稳定模态幅值 u（只读诊断；硬门限 0.12）。</summary>
        public double CaptureModeAmpRad;

        /// <summary>本拍慢通道净拍数（硬捕获门 &amp; 漏桶计数，封顶 SwitchHoldCycles）。只读诊断。</summary>
        public int SlowHold;

        /// <summary>本拍快速提交通道净拍数（提交带 &amp; 漏桶计数，封顶 CaptureCommitHoldCycles）。只读诊断。</summary>
        public int FastHold;

        /// <summary>本拍回中项是否被 centerLimit 钳位（饱和）。只读诊断。</summary>
        public bool CenterSaturated;

        /// <summary>最近约 1 s 的回中饱和比例（0~1，一阶指数平均）。只读诊断。</summary>
        public double CenterSatRatio;

        /// <summary>最近约 1 s 的摆臂平均角 φ [rad]（一阶指数平均）。只读诊断。</summary>
        public double PhiMeanRad;

        /// <summary>本拍能量环积分值 [J·s]（只读诊断）。</summary>
        public double EnergyIntegralJ;

        /// <summary>本拍能量环积分实际贡献的加速度 [rad/s²]（已含死区 taper；只读诊断）。</summary>
        public double IntegralAccelRadPerSec2;

        /// <summary>本拍折返角反馈的能量目标偏置 [J]（只读诊断；正 = 抬高目标）。</summary>
        public double TurnBiasJ;

        /// <summary>最近一次折返的折返角 |ψ| [rad]（无折返时为 0；只读诊断）。</summary>
        public double LastTurnAngleRad;

        /// <summary>累计检测到的折返次数（只读诊断）。</summary>
        public int TurnCount;

        /// <summary>能量停滞触发的目标抬高次数（只读诊断；每次 +0.01 J）。</summary>
        public int StallBumpCount;

        /// <summary>当前能量停滞计时 [s]（只读诊断；到 StallSeconds 且远离直立时抬目标）。</summary>
        public double EnergyStallSeconds;

        /// <summary>本拍是否触发条件式顶部助推（|ΔE|&gt;0.05 J 且 |ψ|&gt;0.30 rad）。只读诊断。</summary>
        public bool FadeBoostActive;

        /// <summary>本拍用于判据与泵律的 ω 估计 [rad/s]（窗内最小二乘；样本不足时为实测值）。</summary>
        public double OmegaEstRadPerSec;

        /// <summary>本拍 2 ms 实测 ω 的 8 ms 短滤波值 [rad/s]（只读诊断；顶部制动用它算 E_fast）。</summary>
        public double FastOmegaRadPerSec;

        /// <summary>本拍用 fastω 算出的能量 [J]（只读诊断；顶部制动用，能量环仍用 40 ms E_est）。</summary>
        public double EnergyFastJ;

        /// <summary>
        /// 本拍**预测顶部角速度** [rad/s]：<c>sqrt(max(2·(E − 2·mgl)/J_eq, 0))</c>。
        /// 含义：把当前能量全部换成 ψ=0 处的动能时摆杆会有多快。E ≤ 2·mgl 时为 0（到不了顶）。
        /// 只读诊断；顶部速度制动用它判断是否还需要抽取能量。
        /// </summary>
        public double PredictedTopOmegaRadPerSec;

        /// <summary>
        /// 本拍顶部速度抽取制动是否生效（只读诊断）。
        /// 判据：|ψ| ≤ CapturePrepareApproachRad、预测顶部 |ω| &gt; 0.8 rad/s、且不在软中心带回中。
        /// 生效时把 uE 压到负值（抽取），仍走原有 extractMax / AMax 钳位，不新增速度限幅。
        /// </summary>
        public bool TopSpeedBrakeActive;

        /// <summary>
        /// 本拍是否进入**高能段反向抽取**（R14）：近顶且 Efast&gt;target 或 predictedTopω&gt;0.8。
        /// 生效时回中项不再是普通弹簧，而是把臂速参考反向（抽取方向），并禁止普通回中的正向注入。
        /// 只读诊断；判据在 Step 内，保留 K/捕获门/topBrake 0.8/软限位/急停/ThetaTrip。
        /// </summary>
        public bool HighEnergyReverseActive;

        /// <summary>高能段反向抽取的目标臂速 [rad/s]（只读诊断；未进入该段时为 0）。</summary>
        public double HighEnergyReverseRateRadPerSec;

        /// <summary>高能段软限位状态（只读诊断）：+1 = 贴 φMax，-1 = 贴 φMin，0 = 未贴。</summary>
        public int HighEnergyLimitCode;

        /// <summary>
        /// 高能反向的软限位余量 [rad]：φ 进入 limit±0.05 就认为贴限位，
        /// 反向目标若指向限位则改取 sign(energyAcc) 或置 0，绝不把臂顶在限位上（R15）。
        /// </summary>
        private const double HighEnergyLimitMarginRad = 0.05;

        /// <summary>本拍是否处在「起动微扰」段（起摆最开始那一小段，用来打破 ω≡0 的对称死点）。</summary>
        public bool BootstrapActive;

        /// <summary>
        /// 本拍**实际**加到摆臂加速度上的往复泵分量 [rad/s²]（不是配置值）。
        /// 泵关着、或泵门被 |ω| 关掉时是 0；日志/遥测只能认这一个数，别拿配置的 PumpAmplitude 冒充。
        /// </summary>
        public double PumpAccelRadPerSec2;

        /// <summary>本拍能量项实际生效的摆臂加速度 [rad/s²]（含符号、含调度权重、未含回中与钳位）。</summary>
        public double EnergyAccelRadPerSec2;

        /// <summary>
        /// 本拍回中项（弹簧 −Kφ·φ − Kφ̇·φ̇；Preparing 时是限速刹车）的加速度 [rad/s²]
        ///（只读诊断；未经过 centerLimit 钳位）。
        /// </summary>
        public double CenteringAccelRadPerSec2;

        /// <summary>本拍起动微扰实际施加的加速度 [rad/s²]（只读诊断；未起动时为 0）。</summary>
        public double BootstrapAccelRadPerSec2;

        /// <summary>本拍四项之和（能量 + 泵 + 回中钳位 + 起动）在最终钳位**之前**的值 [rad/s²]（只读诊断）。</summary>
        public double CommandAccelRawRadPerSec2;

        /// <summary>
        /// 本拍「能量 + 泵 + 起动」在叠加回中项之前被 AMax 钳位后的值 [rad/s²]（只读诊断；
        /// 让日志里的分解能逐项对上加：φ̈和 = φ̈功率钳 + φ̈回中钳）。
        /// </summary>
        public double PowerAccelRadPerSec2;

        /// <summary>
        /// 本拍回中项被 centerLimit 钳位后的值 [rad/s²]（只读诊断；
        /// 与 <see cref="CenteringAccelRadPerSec2"/> 的差就是钳位量）。
        /// </summary>
        public double CenteringTotalAccelRadPerSec2;

        /// <summary>本拍真正送进速度积分器的加速度（最终钳位之后）[rad/s²]（只读诊断）。</summary>
        public double CommandAccelRadPerSec2;

        /// <summary>
        /// 本拍位置参考的速度 [rad/s]（= 速度积分器状态，**不再被软件限速钳位**；只读诊断）。
        /// 台架失败轮这个值恒为 +19.9 贴着旧钳位、从不反向，是"交变刹车被半波整流"的直接证据。
        /// </summary>
        public double VelocityRefRadPerSec;

        /// <summary>本拍位置参考角 [rad]（只读诊断；与实测 φ 的差就是参考超前量）。</summary>
        public double PositionRefRad;

        /// <summary>
        /// 本拍是否处在「软中心带回中」段：|φ| 超出 <see cref="SwingUpParams.CenterBandRad"/> 后
        /// 暂停能量注入与泵、按满 AMax 权限回中，直到回到带内 50% 且 |φ̇| 足够小才恢复。
        /// 只用于日志 / 遥测，判据在 <see cref="Step"/> 内。
        /// </summary>
        public bool Recentering;

        /// <summary>
        /// 本拍是否处在「捕获准备」段：摆杆进入预捕获接近窗（|ψ| ≤ CapturePrepareApproachRad）、
        /// 能量已接近捕获窗（含 CapturePrepareEnergyMarginRatio 的预窗口），且**本拍刹车会抽取能量**
        /// （CalibrationSign·ω·cosθ·sign(φ̇) &lt; 0；2026-11-21 台架反解 S=+1、外跑时即下行侧 ω&lt;0）时，
        /// 停掉能量泵与往复泵，并把超过 CaptureCommitArmRateRadPerSec 的臂速压回门限
        /// （限速刹车，不回中）；判定为注入或臂速已够低的半周期完全不刹车。
        /// 接近顶端时目标速度按 |ψ| 比例收小，让交接瞬间臂速接近 0。只用于日志 / 遥测，判据在 <see cref="Step"/> 内。
        /// </summary>
        public bool Preparing;

        /// <summary>清空起摆状态。开始起摆、急停、断开时调用。</summary>
        public void Reset()
        {
            _phiCmd = 0;
            _phiDotCmd = 0;
            _phaseSec = 0;
            _pumpPhase = 0;
            _switchHoldMilli = 0;
            _commitHoldMilli = 0;
            _running = false;
            _energyBias = 0;
            _energyIntegral = 0;
            EnergyIntegralJ = 0;
            IntegralAccelRadPerSec2 = 0;
            ClearOmegaWindow();
            _fastOmega = 0;
            _fastOmegaInit = false;
            FastOmegaRadPerSec = 0;
            EnergyFastJ = 0;
            EnergyJ = 0;
            EnergyTargetJ = 0;
            EnergyDeltaJ = 0;
            PhaseSec = 0;
            SwitchCountdown = 0;
            SlowHold = 0;
            FastHold = 0;
            CenterSaturated = false;
            CenterSatRatio = 0;
            PhiMeanRad = 0;
            _centerSatRatio = 0;
            _phiMean = 0;
            _turnBiasJ = 0;
            _turnVisitActive = false;
            _turnMinAbsPsi = 0;
            _turnOmegaSign = 0;
            TurnBiasJ = 0;
            LastTurnAngleRad = 0;
            TurnCount = 0;
            StallBumpCount = 0;
            EnergyStallSeconds = 0;
            _maxEnergySeen = 0;
            _stallTimerSec = 0;
            _stallMinUprightRad = Math.PI;
            OmegaEstRadPerSec = 0;
            BootstrapActive = false;
            PumpAccelRadPerSec2 = 0;
            EnergyAccelRadPerSec2 = 0;
            CenteringAccelRadPerSec2 = 0;
            BootstrapAccelRadPerSec2 = 0;
            CommandAccelRawRadPerSec2 = 0;
            PowerAccelRadPerSec2 = 0;
            CenteringTotalAccelRadPerSec2 = 0;
            CommandAccelRadPerSec2 = 0;
            VelocityRefRadPerSec = 0;
            PositionRefRad = 0;
            Recentering = false;
            Preparing = false;
            FadeBoostActive = false;
        }

        /// <summary>清空 ω 估计窗。每次开始起摆重新积累，不把上一段的样本带进来。</summary>
        private void ClearOmegaWindow()
        {
            _omegaEstimator.Reset();
        }

        /// <summary>
        /// 起摆开跑：摆臂目标从起始位置 0 起步（坐标定义下 home ≡ 0，不锁存当前角），速度清零，
        /// 泵相位按参数里的相位偏移起振（换泵序 / 现场微调相位走这一个入口）。
        /// </summary>
        public void Begin(SwingUpParams p)
        {
            _phiCmd = 0;
            _phiDotCmd = 0;
            _phaseSec = 0;
            _pumpPhase = NormalizePhase(p.PumpPhaseOffsetRad);
            _paramsGeneration = p.Generation;
            _switchHoldMilli = 0;
            _commitHoldMilli = 0;
            _running = true;
            ClearOmegaWindow();
            // 每次起摆从零开始估计直流分量与摩擦缺口：上一段的积分存量不能带进新的一次起摆。
            _energyBias = 0;
            _energyIntegral = 0;
            EnergyIntegralJ = 0;
            IntegralAccelRadPerSec2 = 0;
            EnergyJ = 0;
            EnergyTargetJ = 0;
            EnergyDeltaJ = 0;
            PhaseSec = 0;
            SwitchCountdown = 0;
            SlowHold = 0;
            FastHold = 0;
            CenterSaturated = false;
            CenterSatRatio = 0;
            PhiMeanRad = 0;
            _centerSatRatio = 0;
            _phiMean = 0;
            _turnBiasJ = 0;
            _turnVisitActive = false;
            _turnMinAbsPsi = 0;
            _turnOmegaSign = 0;
            TurnBiasJ = 0;
            LastTurnAngleRad = 0;
            TurnCount = 0;
            StallBumpCount = 0;
            EnergyStallSeconds = 0;
            _maxEnergySeen = 0;
            _stallTimerSec = 0;
            _stallMinUprightRad = Math.PI;
            OmegaEstRadPerSec = 0;
            BootstrapActive = false;
            PumpAccelRadPerSec2 = 0;
            EnergyAccelRadPerSec2 = 0;
            CenteringAccelRadPerSec2 = 0;
            BootstrapAccelRadPerSec2 = 0;
            CommandAccelRawRadPerSec2 = 0;
            PowerAccelRadPerSec2 = 0;
            CenteringTotalAccelRadPerSec2 = 0;
            CommandAccelRadPerSec2 = 0;
            VelocityRefRadPerSec = 0;
            PositionRefRad = 0;
            Recentering = false;
            Preparing = false;
            FadeBoostActive = false;
        }

        /// <summary>
        /// 推进一拍，算出本拍摆臂目标角 [rad]。
        /// 返回 true = 切换判据已连续满足 N_c 拍，可以交棒给平衡级联。
        /// 判据任一条破了就清零计数重来，不满足就绝不返回 true。
        /// </summary>
        public bool Step(
            double thetaRad,
            double omegaRadPerSec,
            double phiRad,
            double phiDotRadPerSec,
            SafetyLimits limits,
            double phiMinRad,
            double phiMaxRad,
            SwingUpParams p,
            double dt,
            out double phiTargetRad)
        {
            phiTargetRad = _phiCmd;

            // 非有限输入 / 非正步长：一次坏样本（NaN、掉站残留）不能污染积分器与 ω 窗口。
            // 不推进任何内部状态，本拍也不宣告可切换。
            if (!ControlMath.IsFinite(dt) || dt <= 0 ||
                !ControlMath.IsFinite(thetaRad) || !ControlMath.IsFinite(omegaRadPerSec) ||
                !ControlMath.IsFinite(phiRad) || !ControlMath.IsFinite(phiDotRadPerSec))
            {
                return false;
            }

            if (!_running)
            {
                _running = true;
            }

            _phaseSec += dt;
            PhaseSec = _phaseSec;

            // 旧参数文件里没有这些键，读出来是 0：按内置默认补齐。
            SwingUpParams def = DefaultParams;
            double windowMs = Pick(p.OmegaWindowMs, def.OmegaWindowMs);
            double softOmega = Pick(p.EnergySoftOmegaRadPerSec, def.EnergySoftOmegaRadPerSec);
            double thetaFloor = ControlMath.Clamp(Pick(p.ThetaFadeFloor, def.ThetaFadeFloor),
                SwingUpParams.MinThetaFadeFloor, 1.0);
            double thetaFadeStart = Pick(p.ThetaFadeStartRad, def.ThetaFadeStartRad);
            double thetaFadeEnd = Pick(p.ThetaFadeEndRad, def.ThetaFadeEndRad);
            double aMax = Pick(p.AMax, def.AMax);
            double ke = Pick(p.Ke, def.Ke);
            double bootAcc = Pick(p.BootstrapAccelRadPerSec2, def.BootstrapAccelRadPerSec2);
            double bootSec = Pick(p.BootstrapSec, def.BootstrapSec);
            // 能量整形 / 中心带 / 泵频：新键，0 = 未配置（Pick 按内置默认补齐）。
            // 泵频必须补：PumpFrequencyHz=0 时 _pumpPhase 永不前进，sin(0)=0 会让「泵幅>0 但完全没动」，
            // 现场只能看到摆臂一动不动、日志里泵幅又明明不是 0（2026-11-20 台架这一轮就是这个组合）。
            double extractRatio = Pick(p.EnergyExtractRatio, def.EnergyExtractRatio);
            double deadbandRatio = Pick(p.EnergyDeadbandRatio, def.EnergyDeadbandRatio);
            double centerBand = Pick(p.CenterBandRad, def.CenterBandRad);
            double pumpHz = Pick(p.PumpFrequencyHz, def.PumpFrequencyHz);

            // 软限位：唯一权威是引擎的绝对 counts 区间，这里只接收它按「摆臂一圈计数」换算出的内部弧度。
            // 换算不出来（摆臂未标定）时不做行程门控：此时摆臂本来就不能动，且 CaptureGateAllows
            // 会因非有限值直接拒绝切换，绝不会拿编出来的角度当成行程。
            double phiMin = phiMinRad;
            double phiMax = phiMaxRad;
            if (!ControlMath.IsFinite(phiMin) || !ControlMath.IsFinite(phiMax) || !(phiMin < phiMax))
            {
                phiMin = double.NegativeInfinity;
                phiMax = double.PositiveInfinity;
            }
            // 摆臂速度不再由控制层封顶：SafetyLimits.OmegaArmMaxRadPerSec 只用于引擎输出端的
            // 0x607A 逐拍 slew（= 驱动器 0x6080 的物理天花板），起摆泵不读它。

            // ---- 起摆泵的权限（2026-11-21 第二十三轮：删掉速度包线，只留一道加速度权限）----
            // 旧版在加速度权限之外又乘一道「臂速包线」vPumpMax = PumpRateEnvelopeRatio·ω_n
            //（默认 2.0·ω_n ≈ 18.1 rad/s，再被 SafetyLimits 的软件限速压到 20），并以它为上限
            // 硬钳 _phiDotCmd。台架后果：速度积分一旦被直流顶到钳位，交变的刹车指令就被
            // **半波整流**成净单向推力 —— 目标位置恒以 19.9 rad/s 前进、从不反向，E=3.5 J 卡 90 s
            // 一分不掉。实测臂峰值 98.7 rad/s、驱动器零故障；手册唯一的物理天花板是 0x6080 = 3000 rpm
            //（314 rad/s）。所以这里只保留**一个**加速度权限：aPumpMax = AMax（配置值），
            // 速度不再由软件封顶 —— 交变刹车能不能真的反向，交给积分器、软限位与跟随误差如实裁决。
            // 摆臂是开环步进，0x6064 是指令镜像，看不见丢步；能做的加速度由 AMax 与台架实测给出。
            double pendRate = Pick(p.CapturePendulumRateRadPerSec, def.CapturePendulumRateRadPerSec);
            double aPumpMax = aMax;

            double theta = thetaRad;
            double cosTheta = Math.Cos(theta);
            // ε 允许**负值**（出厂 -0.002，理由见 SwingUpParams.TargetEnergyGain 的推导）：
            // 判据是「有限数且落在 [MinTargetEnergyGain, MaxTargetEnergyGain]」，不能再用 >=0 挡掉它，
            // 否则现场填的负 ε 会被静默换成默认值，日志里还看不出是谁改的。
            double eps = ControlMath.IsFinite(p.TargetEnergyGain) &&
                         p.TargetEnergyGain >= SwingUpParams.MinTargetEnergyGain &&
                         p.TargetEnergyGain <= SwingUpParams.MaxTargetEnergyGain
                ? p.TargetEnergyGain
                : def.TargetEnergyGain;

            // 稳健 ω：窗内最小二乘斜率。样本不足时回退到实测 ω —— 绝不返回 0 / NaN，
            // 那会让切换门与失衡门同时失效，比噪声本身危险得多。
            double omegaEst = EstimateOmega(theta, omegaRadPerSec, dt, windowMs);
            OmegaEstRadPerSec = omegaEst;

            // 摆杆能量：以最低点为零势能。θ=0 下垂势能 0，θ=π 直立势能 2·mgl。
            double mgl = limits.Mgl > 0 && ControlMath.IsFinite(limits.Mgl) ? limits.Mgl : 1.0;
            double jEq = limits.JEq > 0 && ControlMath.IsFinite(limits.JEq) ? limits.JEq : 1.0;
            double energy = 0.5 * jEq * omegaEst * omegaEst
                          + mgl * (1.0 - cosTheta);

            // 顶部制动专用的 E_fast：用 2 ms 实测 ω（输入已低通）再 8 ms 短滤波，不用 40 ms ω_est。
            // R12 台架：高加速度下 ω_est 滞后/高估，E_est 冲到 2.30 J 而真实约 2.05 J；
            // 顶部制动按虚假高能一直满抽（φ̈钳后=-100），真实能量被抽空后又注入，形成极限环。
            double fastOmegaInput = ControlMath.IsFinite(omegaRadPerSec) ? omegaRadPerSec : omegaEst;
            if (!_fastOmegaInit)
            {
                _fastOmega = fastOmegaInput;
                _fastOmegaInit = true;
            }
            else
            {
                double fastAlpha = dt / (FastOmegaFilterTauSeconds + dt);
                _fastOmega += (fastOmegaInput - _fastOmega) * fastAlpha;
            }
            double fastOmega = ControlMath.IsFinite(_fastOmega) ? _fastOmega : fastOmegaInput;
            FastOmegaRadPerSec = fastOmega;
            double energyFast = 0.5 * jEq * fastOmega * fastOmega + mgl * (1.0 - cosTheta);
            EnergyFastJ = energyFast;
            // ---- 捕获窗口的能量边界（物理推导，不是百分比经验值）----
            // 下边界：折返点刚好落在 θ_switch 的能量（ω=0 时 E = mgl(1−cosθ) = mgl(1+cosψ)）。
            // 上边界：过顶速度刚好把不稳定模态幅值顶到 u_max 的能量（E = 2mgl + ½J(ω_n·u_max)²）。
            // 这两个数同时给三处用：能量目标的物理下限、近直立淡出、捕获准备、以及日志。
            // **必须先算**：目标的下限要用 gateEnergyLow（见下）。
            double thetaSwitch = Pick(p.ThetaSwitchRad, def.ThetaSwitchRad);
            // 用**生效**上限（含 0.12 硬夹）算能量上边界：参数文件里可能残留 0.16，
            // 若拿它算 gateEnergyHigh，预捕获能量窗会被撑大 4 倍，和捕获门判的不是同一件事。
            double captureModeMax = CaptureModeAmplitudeMax(p);
            double gateEnergyLow = mgl * (1.0 + Math.Cos(thetaSwitch));
            double gateSpeed = captureModeMax * pendRate;
            double gateEnergyHigh = 2.0 * mgl + 0.5 * jEq * gateSpeed * gateSpeed;

            // 折返角反馈的目标偏置（A/B-4）：只改能量目标，不改控制律形状。
            //
            // **2026-11-21 第九轮修正（r9 超时的直接原因）**：
            // 旧下限是 `2mgl − 0.15`（mgl=1 时 1.85 J），而 "折返点落在 θ_switch=0.10" 需要的能量是
            // `gateEnergyLow = mgl(1+cos0.10) = 1.995 J`。偏置在摆杆高速掠过顶部时会被折返检测
            // 一路下调（每次越顶都算一次 "turn angle < 0.06"），r9 实测 bias 顶到 −0.15 J、
            // 目标 1.858 J < 1.995 J —— **摆杆永远到不了 |ψ|≤0.10，捕获在数学上不可能**。
            // 折返角与能量是同一个物理量的两种表示：ψ_turn 从 0.06→0.09 只对应 0.002 J，
            // 所以负向偏置的合法权限本来就极小（约 −0.009 J），不是 −0.15 J。
            // 现在：目标下限 = gateEnergyLow（物理可达性的硬边界），偏置本身也按这个下限收口。

            double nominalTarget = 2.0 * mgl * (1.0 + eps);
            double turnBiasLow = gateEnergyLow - nominalTarget;   // ≤0，本机约 −0.009 J
            double targetLow = Math.Max(2.0 * mgl - TurnTargetMarginJ, gateEnergyLow);
            double target = ControlMath.Clamp(
                nominalTarget + _turnBiasJ,
                targetLow,
                2.0 * mgl + TurnTargetMarginJ);
            double delta = target - energy;

            // 预捕获回中 / 快速提交 / 漏桶（2026-11-20 新增，默认 0 = 未配置，由 CreateDefault 补齐）。
            double prepareApproach = Pick(p.CapturePrepareApproachRad, def.CapturePrepareApproachRad);
            double prepareMarginRatio = ControlMath.Clamp(
                Pick(p.CapturePrepareEnergyMarginRatio, def.CapturePrepareEnergyMarginRatio), 0.0, 0.1);
            double prepareMarginJ = prepareMarginRatio * 2.0 * mgl;
            double commitArmRate = Pick(p.CaptureCommitArmRateRadPerSec, def.CaptureCommitArmRateRadPerSec);
            double commitOmega = Pick(p.CaptureCommitOmegaRadPerSec, def.CaptureCommitOmegaRadPerSec);
            // 粗筛 |ω| 上限（历史挡位，默认 3.0）—— 第九轮起**不再出现在硬门/提交带的放行逻辑里**，
            // 只保留「提交带 ⊂ 粗筛」的一致性钳位（ParamStore 的组合校验也读它）。
            // 真正放行的是 u 包线 + 提交带的 |ω_est|≤1.2。
            double omegaSwitchMax = Pick(p.OmegaSwitchRadPerSec, def.OmegaSwitchRadPerSec);
            // 提交带必须永远是硬捕获门/粗筛的**子集**：现场把硬门收紧到比提交带默认值更小时
            //（例如 capture_arm_rate_limit=2.0），这里按硬门自动收紧，而不是让整组参数非法。
            double captureArmRateLimit = Pick(p.CaptureArmRateLimitRadPerSec, def.CaptureArmRateLimitRadPerSec);
            if (commitArmRate > captureArmRateLimit) commitArmRate = captureArmRateLimit;
            if (commitOmega > omegaSwitchMax) commitOmega = omegaSwitchMax;
            // **2026-11-21 交棒硬上限**：平衡包线在 ω 方向只有约 ±1 rad/s（ψ=0 时 |u|≤0.12），
            // 无论参数文件把提交带/粗筛填多大，交棒都不允许超过 1.0 rad/s。
            if (commitOmega > SwingUpParams.CaptureHandoffOmegaMaxRadPerSec)
                commitOmega = SwingUpParams.CaptureHandoffOmegaMaxRadPerSec;
            int commitHoldCycles = p.CaptureCommitHoldCycles > 0
                ? p.CaptureCommitHoldCycles
                : def.CaptureCommitHoldCycles;
            if (commitHoldCycles > SwingUpParams.MaxCaptureCommitHoldCycles)
                commitHoldCycles = SwingUpParams.MaxCaptureCommitHoldCycles;
            // holdDecay 的单位是**千分之一拍/拍**（milli-cycle per cycle），默认 2 = 0.002。
            // 0 = 未配置（旧参数文件没有这个键）→ 用内置默认；负值 / 超界在参数层已被拒收，
            // 这里再夹一次，保证周期路径永远不会拿到一个会让计数器“永不复位”的值。
            int holdDecay = p.SwitchHoldDecayPerCycle > 0
                ? p.SwitchHoldDecayPerCycle
                : def.SwitchHoldDecayPerCycle;
            if (holdDecay > SwingUpParams.MaxSwitchHoldDecayPerCycle)
                holdDecay = SwingUpParams.MaxSwitchHoldDecayPerCycle;
            long targetSlowMilli = (long)p.SwitchHoldCycles * 1000L;
            long targetFastMilli = (long)commitHoldCycles * 1000L;

            EnergyJ = energy;
            EnergyTargetJ = target;
            EnergyDeltaJ = delta;

            double upright = Math.Abs(ControlMath.WrapPi(theta - Math.PI));

            // ---- 能量停滞检测（2026-11-21 第十六轮）----
            // 折返反馈只在有折返时动作；能量停在 1.0~1.5 J 时 turns=0，必须有不依赖折返的爬升通道。
            // 连续 StallSeconds 没有新高、且最近离直立仍 > StallFarPsiRad，就 +0.01 J 抬目标
            //（目标本身钳在 2mgl±0.15 J）。
            if (energy > _maxEnergySeen + StallRiseEpsilonJ)
            {
                _maxEnergySeen = energy;
                _stallTimerSec = 0;
                _stallMinUprightRad = upright;
            }
            else
            {
                _stallTimerSec += dt;
                if (upright < _stallMinUprightRad) _stallMinUprightRad = upright;
                if (_stallTimerSec >= StallSeconds && _stallMinUprightRad > StallFarPsiRad && delta > 0.02)
                {
                    _turnBiasJ += StallBumpJ;
                    if (_turnBiasJ > TurnBiasMaxJ) _turnBiasJ = TurnBiasMaxJ;
                    StallBumpCount++;
                    _stallTimerSec = 0;
                    _stallMinUprightRad = upright;
                    _maxEnergySeen = energy;
                }
            }
            EnergyStallSeconds = _stallTimerSec;

            // 近直立淡出：离直立越近越少推，把最后一段交给平衡级联，避免把摆杆甩过头。
            // **不再按 |ω| 淡出** —— 能量误差 ΔE 本身就把注入量收得很干净，
            // 再叠一层「|ω| 大就少推」等于在摆得最快、最需要推力的时候砍掉 80% 的权限，
            // 实测把这一项去掉后同样的能量误差注入率高 4~5 倍。
            // 条件式顶部助推（2026-11-21 第十六轮）：只在 |ΔE|>0.05 J 且 |ψ|>0.30 rad 时，
            // 把淡出 floor 从 thetaFloor(0.35) 临时抬到 0.70；|ΔE|≤0.05 或 |ψ|≤0.30 立即恢复 0.35。
            // 目的：救 1.95 J / 折返 0.41 rad 的净功停滞（那里 0.35 只给 54×0.35≈19 rad/s²）；
            // 0.06~0.09 折返带（|ψ|≈0.07、|ΔE|≈0.005）不触发，顶部速度/命中率不受影响。
            // 离线（0.66·ω_n）：带内命中 52/97=53.6%（原 48%），末 10 抖动 0.022 rad（原 0.062），
            // 初始 maxE 2.201（与原来相同）；停滞 tTo2 0.22 s（原 0.24，模型 β 偏大，仅参考）。
            double fadeFloor = thetaFloor;
            FadeBoostActive = Math.Abs(delta) > FadeBoostDeltaJ && upright > FadeBoostPsiRad;
            // ---- 近顶端权限的绝对上限（2026-11-21 提速配套）----
            // 淡出权重结构下限 0.35 是「泵权限 54 rad/s²」时代定的（0.35×54≈19 rad/s²）。
            // 泵权限提到 150 后若仍按 0.35，顶部注入会 2.8 倍化，摆臂掠顶速度会冲出捕获门 |φ̇|≤3
            // （历史教训：把臂速门抬到 15 后平衡接管 0.4 s 就掉摆）。
            // 生效下限取 min(配置下限, 20/aPumpMax)：aPumpMax=54 时仍是 0.35~0.37（行为不变），
            // aPumpMax=150 时顶部绝对权限稳定在约 20 rad/s²，中下部（weight=1）才吃满新权限。
            // 条件助推（FadeBoost）同理按 40 rad/s² 封顶，保住「救停滞」而不会把摆杆甩过顶。
            if (aPumpMax > 0.0)
            {
                double absoluteFloor = SwingUpParams.FadeFloorAccelRadPerSec2 / aPumpMax;
                if (absoluteFloor < fadeFloor) fadeFloor = absoluteFloor;
            }
            if (FadeBoostActive)
            {
                double boostFloor = FadeBoostFloor;
                if (aPumpMax > 0.0)
                {
                    double absoluteBoost = SwingUpParams.FadeBoostAccelCeilingRadPerSec2 / aPumpMax;
                    if (absoluteBoost < boostFloor) boostFloor = absoluteBoost;
                }
                fadeFloor = Math.Max(fadeFloor, boostFloor);
            }
            fadeFloor = ControlMath.Clamp(fadeFloor, 0.05, 1.0);
            double weight = ControlMath.Clamp(
                fadeFloor + (1.0 - fadeFloor) * ControlMath.SmoothStep(thetaFadeEnd, thetaFadeStart, upright),
                0.0, 1.0);
            // 近直立淡出**恒生效**（2026-11-20 可靠性修正后的定论）。
            // 它的作用是把摆臂在顶部的速度压进捕获门 |φ̇| ≤ 4 rad/s：泵在 |ψ| < 0.45 区间若仍按
            // 全权限推，摆臂会带着 5~6 rad/s 掠顶，捕获门每拍都破在臂速上（离线对照 8/20）。
            // 淡出压掉的平均注入由能量环积分补回来；floor 的结构性下限是 0.35
            //（再低泵在顶部推不过摩擦，能量会卡在 E*−0.02 J，见 SwingUpParams.MinThetaFadeFloor）。

            // ---- 软中心带：漂移的第二道防线 ----
            // 进入：|φ| 的预测刹车距离超出带半径；退出：|φ| < 50% 带半径 且 |φ̇| < 2 rad/s（滞回，避免在带边来回切）。
            // 回中期间暂停能量注入与泵、按满泵包线 aPumpMax 回中：先保机构行程，再谈起摆。
            // 判据只认**实测** φ / φ̇，不认指令量 —— 指令漂了而实际没漂时不该停注入；
            // 实际漂了而指令没漂（被外力拖走）时必须停。
            const double RecenterExitRatio = 0.5;
            const double RecenterExitRateRadPerSec = 2.0;
            if (!Recentering)
            {
                // 用**预测刹车距离**判进入，而不是等 |φ| 本身越过带半径：
                // 满权限反向刹车的滑行距离 = φ̇²/(2·AMax)。高速时提前收手，带才是真正的包络
                //（离线仿真：只看 |φ| 时 3.6 rad 的带会被冲到 5.2 rad；加预测后峰值≈3.7 rad）。
                double predictedStop = Math.Abs(phiRad) + phiDotRadPerSec * phiDotRadPerSec / (2.0 * aPumpMax);
                if (predictedStop > centerBand) Recentering = true;
            }
            else if (Math.Abs(phiRad) < centerBand * RecenterExitRatio &&
                     Math.Abs(phiDotRadPerSec) < RecenterExitRateRadPerSec)
            {
                Recentering = false;
            }

            // 起摆中换参数（Generation 变）：泵相位按新偏移重新起振，避免旧相位与新频率错拍。
            if (p.Generation != _paramsGeneration)
            {
                _paramsGeneration = p.Generation;
                _pumpPhase = NormalizePhase(p.PumpPhaseOffsetRad);
            }

            _pumpPhase += ControlMath.TwoPi * pumpHz * dt;
            // 泵频高时一拍可能越过好几个 2π，用倍数归一，别只减一次让相位越攒越大。
            if (_pumpPhase >= ControlMath.TwoPi)
                _pumpPhase -= ControlMath.TwoPi * Math.Floor(_pumpPhase / ControlMath.TwoPi);

            // ---- 能量注入 / 抽取项（单律，没有 |ω| 闸门）----
            // 推导（θ 自下垂点量起）：θ̈ = −(g/l)·sinθ − (r/l)·cosθ·φ̈，于是
            //     dE/dt = J·θ̇·θ̈ + mgl·sinθ·θ̇ = −m·l·r·θ̇·cosθ·φ̈
            // 要让 E 单调涨到 E*，必须取 φ̈ = −u_E·sign(θ̇·cosθ)；取正号是**抽能**方向。
            // 旧版（以及 01-控制层契约 §4.1 的公式）漏了这个负号，实测「一进能量法就掉回抖速」就是它。
            //
            // **2026-11-20 修正：ΔE < 0 时不再一律给 0。**
            // 只注入不抽取的后果不是「让摩擦自然衰减」，而是**高能极限环**：能量冲过 E* 后 u_E=0，
            // 摆杆带着 E>2mgl 整圈转（台架实测 CH1 2000→181903 ≈ 75 圈），只有摩擦能慢慢耗掉；
            // 起摆时间因此在 8~180 s 之间抖，捕获门永远等不到 |ω| ≤ 3。
            // 现在取钳到 [−EnergyExtractRatio·AMax, +AMax] 的 u_E：抽取权限只有注入的 20%，
            // 够把多出来的那一点放掉，又不会重演历史上「对称抽取 2 s 把 1.82 J 打到 0.07 J」的灾难。
            // 死区（EnergyDeadbandRatio·E*）内不动作，避免 ΔE 在 0 附近逐拍翻符号把摆臂抖成方波。
            // 死区保持默认 0.01 J（EnergyDeadbandRatio=0.005）：**不要缩到 0.0015**。
            // 摆动中 E 的估计误差是 0.04~0.10 J（ω_est 噪声），0.003 J 死区会被噪声淹没、
            // 只让能量环抖动；折返点捕获的精度来自折返角（θ 量化 0.0026 rad），不来自能量死区。
            double deadband = ControlMath.Clamp(deadbandRatio, 0.0, 0.2) * Math.Abs(target);

            // ---- 能量环积分（2026-11-20 可靠性修正）----
            // 只在下垂差方向累积；进入死区 / 超目标 / 中心带回中后按 1.5 s 泄放，且不反向
            //（积分只补摩擦，不抽能）。回中期间必须泄放：那时 uE 被强制为 0，继续累积会在回中结束后
            // 一次性变成一大坨额外推力。
            // 上限 = 积分项最多贡献 90% 的泵权限，防止长时间爬升把积分灌爆后一脚冲过目标。
            // 积分项的抗过冲（第三轮，与 Ke 软化同轮但可单独对照）：只在 ΔE > 2×死区时累积，
            // 死区附近只泄漏；权限上限从 0.9·aPumpMax 降到 0.5·aPumpMax。
            // 原因：软饱和只作用在 P 项，积分项不受它约束；台架/离线都看到 ΔE 接近目标时
            // 积分仍在推，是能量过冲（maxE 2.25 J、顶部 |ω|~5）的主要嫌疑。
            // 第十轮：积分的作用门限从 2×死区收到 1×死区。摩擦损耗需要 u_E≈2.6 rad/s² 的稳态注入，
            // 纯 P 项（收敛增益 90）要 ΔE≈0.03 J 才能给出 —— 比捕获要求的 ±0.005 J 大 4 倍。
            // 让积分在 |ΔE|>死区（0.005 J）就开始补，稳态误差才压得进 0.005 J。
            double integralDeadband = deadband;
            if (delta > integralDeadband && !Recentering)
            {
                _energyIntegral += delta * dt;
                // **2026-11-21 第十轮：积分权限从 0.5·aPumpMax 收到 0.06·aPumpMax（=6 rad/s²）。**
                // 积分只补摩擦缺口（本机约需 2.6 rad/s²）；旧的 50 rad/s² 在目标附近是一股
                // 不随 ΔE 收敛的常值注入，是 ±0.1 J 极限环的第二个来源。
                double integralMax = SwingUpParams.IntegralAuthorityRatio * aPumpMax / EnergyIntegralGain;
                if (_energyIntegral > integralMax) _energyIntegral = integralMax;
            }
            else
            {
                _energyIntegral -= _energyIntegral * (dt / EnergyIntegralLeakSeconds);
            }
            if (_energyIntegral < 0.0) _energyIntegral = 0.0;

            double uE;
            if (delta > deadband)
            {
                // 比例项决定「差得多就推得狠」，积分项补掉摩擦造成的稳态缺口。
                // 没有积分时，能量会停在 ke·ΔE 与摩擦打平的地方（台架 1.5~1.6 J、离线 1.987 J），
                // 看起来像「参数不够狠」，其实是控制律缺一个积分自由度。
                // Ke 归一化软饱和（第三轮）：x = ke·ΔE/aPumpMax；u_E = aPumpMax·x/(1+k·|x|)。
                // 小信号 u_E ≈ ke·ΔE；渐近值 = aPumpMax（与 ke/AMax 配置无关）；无硬拐点。
                //
                // **2026-11-21 第十轮：增益两段化（爬升段大步走、收敛段小步走）。**
                // 目标附近的半周期能量增量 = C·u_E，C = m·l·r·∫|ωcosθ|dt。
                // 本机 E=1.995 J 时数值积分 ∫|ωcosθ|dt=3.80 rad·s ⇒ C=0.0112 J per (rad/s²)；
                // 由 r10 极限环反推的有效 C≈0.005（伺服相位/跟踪损耗）。deadbeat 增益 90~190。
                // 旧 Ke=700 ⇒ C·K≈3.5~7.8 > 1，每个半周期过冲，形成 ±0.1 J 极限环
                //（r10 实测 E 在 2.12~2.36 来回），而捕获需要 E 落在目标 ±0.005 J。
                // 取收敛增益 90：对 C=0.0112 是 deadbeat、对 C=0.005 每半周期修正 45%，永不过冲。
                // |ΔE| ≥ 0.30 J 仍用配置的 Ke（爬升要快）；0.15~0.30 J 平滑过渡；≤0.15 J 用 90。
                // 用户把 Ke 配得比收敛增益还小时，全程按 Ke 执行（保持旧行为，不反向加陡）。
                double absDelta = Math.Abs(delta);
                double keEffective = ke;
                if (ke > SwingUpParams.ConvergenceGainRadPerSec2PerJ)
                {
                    double convergenceBlend = ControlMath.SmoothStep(
                        SwingUpParams.ConvergenceBandLowJ, SwingUpParams.ConvergenceBandHighJ, absDelta);
                    keEffective = SwingUpParams.ConvergenceGainRadPerSec2PerJ
                                + (ke - SwingUpParams.ConvergenceGainRadPerSec2PerJ) * convergenceBlend;
                }
                double softX = aPumpMax > 0.0 ? keEffective * delta / aPumpMax : 0.0;
                double softDenom = 1.0 + SwingUpParams.EnergySofteningStrength * Math.Abs(softX);
                double softUe = softDenom > 0.0 ? aPumpMax * softX / softDenom : 0.0;
                // 积分项在 [deadband, 2×deadband] 内从 0 线性放开到满值：接近目标时不再顶能量。
                double integralScale = ControlMath.Clamp((delta - deadband) / deadband, 0.0, 1.0);
                double integralAcc = EnergyIntegralGain * _energyIntegral * integralScale;
                EnergyIntegralJ = _energyIntegral;
                IntegralAccelRadPerSec2 = integralAcc;
                double injection = softUe + integralAcc;
                uE = ControlMath.Clamp(injection, 0.0, aPumpMax);
            }
            else if (delta < -deadband)
            {
                // **2026-11-21 第九轮：抽取权限 = AMax（EnergyExtractRatio 默认 1.0）。**
                // r9 实测：泵把能量灌到 3.0 J（目标 1.86 J）时，旧抽取权限只有 0.2·AMax=20 rad/s²，
                // 顶部再被淡出压到 4 rad/s²，而注入曾用到 100 rad/s² —— 灌进去的能量抽不出来，
                // 摆杆就一直在顶部以 9~16 rad/s 高速循环（掠顶），捕获门 |ω_est|≤1 永远不满足。
                // 旧注释担心的 "对称抽取把摆幅打回底部" 是 **抽取与注入共用 Ke** 时代的灾难；
                // 现在 ExtractGain=90 独立，uE=90·ΔE 是比例律，超调越大抽得越多，不会 bang-bang。
                double extractMax = aMax * ControlMath.Clamp(extractRatio, 0.0, 1.0);
                double extractGain = Pick(p.ExtractGain, def.ExtractGain);
                uE = ControlMath.Clamp(extractGain * delta, -extractMax, 0.0);
            }
            else
            {
                uE = 0.0;
            }
            // 软中心带回中期间暂停注入 / 抽取：摆臂先回家，能量交给摩擦。
            if (Recentering) uE = 0.0;

            // ---- 捕获准备：近顶、能量在捕获窗、臂速 >3 rad/s 时提前反向制动 ----
            // 不再按 ω·cosθ 相位挑半周期；到顶前只要臂速超出捕获窗就刹回 3 rad/s。
            // 制动项仍走原有 centerLimit / aPumpMax 包线，不另加固定速度限速。
            // |ψ| > 0.6 或 |φ̇| ≤ 3 时自动退出；Recentering 不参与。
            bool prepareEnergyWindow = energy >= gateEnergyLow - prepareMarginJ &&
                                       energy <= gateEnergyHigh + prepareMarginJ;
            // prepare-brake：只要近顶、能量在捕获窗、臂速 >3 rad/s 就反向制动，
            // 不再按能量抽取相位挑半周期。制动量进入原有 centerLimit/aPumpMax 包线，
            // 不另加速度限速；|φ̇|≤3 或离开近顶立即退出。
            bool armTooFast = Math.Abs(phiDotRadPerSec) > commitArmRate;
            Preparing = !Recentering &&
                        upright <= prepareApproach &&
                        prepareEnergyWindow &&
                        armTooFast;
            if (Preparing) uE = 0.0;

            // ---- 顶部速度抽取制动（2026-11-21 第二十五轮）----
            // 问题：R1 的能量目标已经是下限 1.995 J（turn bias 把它压到 targetLow），但 E 仍停在
            // 2.006~2.008 J；能量环的抽取只有 ExtractGain·ΔE≈0.8 rad/s²，顶部速度 1.6~2.0 rad/s，
            // 捕获提交带 |ω_est|≤1.2 一直不满足，40 s 超时。只把目标再降 0.01 不会生效（已在下限）。
            // 现在按「预测顶部速度」追加抽取：E 全部换成 ψ=0 处动能时的 |ω| 超过 0.8 rad/s 就刹车。
            // **R13（2026-11-22）：这里的 E 用 E_fast（2 ms 实测 ω + 8 ms 短滤波），不再用 40 ms ω_est。**
            // uE 取负值即抽取（dE/dt = −m·l·r·|ωcosθ|·|φ̈|，与相位无关），仍受 extractMax/AMax 钳位。
            double predictedTopOmega = jEq > 0.0
                ? Math.Sqrt(Math.Max(2.0 * (energyFast - 2.0 * mgl) / jEq, 0.0))
                : 0.0;
            PredictedTopOmegaRadPerSec = predictedTopOmega;
            bool topSpeedBrake = !Recentering &&
                                 upright <= prepareApproach &&
                                 predictedTopOmega > TopSpeedBrakeOmegaTargetRadPerSec;
            if (topSpeedBrake)
            {
                double topExcess = predictedTopOmega - TopSpeedBrakeOmegaTargetRadPerSec;
                double brake = ControlMath.Clamp(
                    TopSpeedBrakeGainRadPerSec2PerOmega * topExcess,
                    0.0, aMax * ControlMath.Clamp(extractRatio, 0.0, 1.0));
                // 只允许把 uE 压得更负（抽取更强），不削弱本拍已有的抽取。
                if (-brake < uE) uE = -brake;
            }
            TopSpeedBrakeActive = topSpeedBrake;

            // R14 高能段反向抽取：Efast>target 或 predictedTopω>0.8（近顶）时，允许臂速参考反向；
            // 该段回中让路，禁止普通回中的正向注入。幅度按超量比例，最大 commitArmRate。
            double highExcessJ = energyFast - target;
            double highTopExcess = predictedTopOmega - TopSpeedBrakeOmegaTargetRadPerSec;
            bool highEnergyExtraction = !Recentering &&
                                        upright <= prepareApproach &&
                                        (highExcessJ > 0.0 || highTopExcess > 0.0);
            HighEnergyReverseActive = highEnergyExtraction;

            // 相位门用**软符号** x/√(x²+ε²)：|ω| ≫ ε 时近似 ±1（等于 bang-bang），
            // |ω| ≪ ε 时按比例收小，避免在 ω≈0 处把 2 kHz 的采样噪声放大成摆臂抖动。
            // 它只是数值正则化，**不是**起摆闸门：任何非零 ω 都会被同向放大。
            double phaseSignal = omegaEst * cosTheta;
            double softSign = phaseSignal / Math.Sqrt(
                phaseSignal * phaseSignal + softOmega * softOmega);

            // **能量律符号不要动（2026-11-20 定案）**：把 plant 耦合整体反号的对照里，
            // 同一份控制器的 maxE 只到 0.03~0.08 J，并报 energy_never_reached；
            // 台架同一套代码能爬到 2 J，说明当前相位是正确的。近顶端"φ̈能量只有 ±5 rad/s²"
            // 只说明能量法在近顶端是配角，不是符号错——近顶端真正做功的是回中项
            //（分解：近顶 68~100% 回中，中段/底部 80~91% 能量法）。不要把这条当符号证据。
            // **2026-11-21 第九轮：近直立淡出只作用于注入，不作用于抽取。**
            // 淡出的本来目的是"别把摆杆甩过顶"（限制顶部**注入**的权限）。旧代码把它也乘到抽取上，
            // 于是顶部抽取从 0.2·AMax 再乘 0.2 ≈ 4 rad/s² —— 而顶部正是高速循环最需要泄能的相位。
            // r9 日志（E 从 2.53 涨到 3.00、ΔE=−0.68→−1.14、bias 顶到 −0.15）就是这个组合的结果：
            // 能量灌超后既抽不动、目标又被折返反馈压到 1.858 J，最终 90 s 超时。
            // 抽取方向本身是对的：dE/dt = −m·l·r·ω·cosθ·φ̈，代入 φ̈=−uE·sign(ωcosθ)（uE<0）得
            // dE/dt = −m·l·r·|ωcosθ|·|uE| < 0，与 ω 的符号无关，高速循环下也始终在泄能。
            double energyAcc = -uE * softSign * (uE > 0.0 ? weight : 1.0);

            // 去掉能量注入项的直流分量: 只留与摆动同步的交变推力。
            // 不做这一步时，波形不对称会让摆臂在一个摆动周期里净位移不为零，几十秒就漂出十几圈，
            // 而回中项被 center_auth_ratio 钳住，拽不回来（放大回中权限只会抢走注入权限、拖慢起摆）。
            // 低通带宽取 1 s：远低于摆动周期 (~0.7 s) 的漂移会被吃掉，交变成分几乎不受影响。
            //
            // **2026-11-21 第二十三轮：uE=0 时冻结估计，不再按零样本衰减。**
            // 旧版无论 uE 是否为 0 都按 tau=1 s 学习：uE=0 的拍（死区 / 软中心带回中 / 预捕获准备）
            // 喂进来的是 0，等于把刚学到的直流估计一点点抹掉。反复进出这些段 = 每次回到能量法时
            // 前 1 s 的直流完全没被扣除 —— 一台单向棘轮，正是"直流把速度积分顶到钳位"的来源之一。
            // 现在：只有能量项真的在动作时才学习；不动作时只做 10 s 的极慢泄漏（适应慢变工况）。
            if (uE != 0.0)
            {
                double biasAlpha = dt / (EnergyBiasTauSeconds + dt);
                _energyBias += (energyAcc - _energyBias) * biasAlpha;
                energyAcc -= _energyBias;
            }
            else
            {
                _energyBias -= _energyBias * (dt / EnergyBiasHoldSeconds);
            }

            EnergyAccelRadPerSec2 = energyAcc;

            // ---- 起动微扰 ----
            // 能量律在 ω ≡ 0 处是鞍点（dE/dt ∝ ω），数学上从严格静止起不来。
            // 真实台架总有噪声，但方向随机、起步慢；这里给一段**确定方向、有时限**的小加速度把对称打破，
            // 一旦摆杆动起来（ω ≠ 0）能量律立刻接管。它不依赖摆杆固有频率，也不需要认相位。
            //
            // **2026-11-20 可靠性修正：不再死守固定 0.25 s。**
            // 台架的失败样本里有「t=24 s 能量还在 1.5 J」和「起动段没把摆杆推出死区」两种；固定时长的
            // 微扰在摩擦大的机器上可能刚结束、摆杆又停回 ω≈0，能量律没有方向可用。现在改成：
            // 固定段照旧；只要能量还没起来（< 1%·E*）且摆杆仍近乎静止（|ω| < 0.5），就继续微扰，
            // 最长 8 倍 BootstrapSec（默认 2 s）。能量一旦开始爬升或摆杆动起来，立刻交还能量律。
            double bootStallEnergy = 0.01 * Math.Abs(target);
            bool bootByStall = _phaseSec <= bootSec * 8.0 &&
                               energy < bootStallEnergy && Math.Abs(omegaEst) < 0.5;
            BootstrapActive = (_phaseSec <= bootSec || bootByStall) && !Recentering && !Preparing;
            double bootAccApplied = BootstrapActive ? bootAcc : 0.0;
            BootstrapAccelRadPerSec2 = bootAccApplied;
            double acc = energyAcc + bootAccApplied;

            // ---- 可选往复泵（默认关）----
            // 只按 |ω| 开门，且**只在本拍真的加上去时才记账**：日志/遥测读 PumpAccelRadPerSec2，
            // 不再拿配置的 PumpAmplitude 冒充实际施加量（旧版这里一直显示 2.00，而泵根本没参与）。
            // 中心带回中期间泵也停：它和能量项一样会把摆臂往外推，回中优先。
            PumpAccelRadPerSec2 = 0;
            if (p.PumpAmplitude > 0 && !Recentering && !Preparing)
            {
                double pumpGate = 1.0 - Math.Abs(omegaEst) / PumpGateOmegaRadPerSec;
                if (pumpGate < 0) pumpGate = 0;
                else if (pumpGate > 1) pumpGate = 1;
                double pump = p.PumpAmplitude * Math.Sin(_pumpPhase) * pumpGate;
                acc += pump;
                PumpAccelRadPerSec2 = pump;
            }

            acc = ControlMath.Clamp(acc, -aPumpMax, aPumpMax);
            // 诊断：能量+泵+起动这一路的第一道钳位（AMax）后的值。
            PowerAccelRadPerSec2 = acc;

            // 回中 / 阻尼叠在能量项之后，最后再钳一次。
            // 起始位置 ≡ 坐标 0（home 不是锁存角），回中项直接对 φ 本身，没有第二个锚点。
            //
            // **回中项不许吃掉能量注入的权限**（2026-11-16 台架实测的直接原因）：
            // 阻尼项 -KPhiDot·φ̇ 在 φ̇=9.6 rad/s 时是 -14.4 rad/s²（旧 KPhiDot=1.5），
            // 占 AMax=40 的 36%、占 AMax=20 的 72%；再叠 -KPhi·φ。结果是摆臂在一次泵动里
            // 只有 0.85 rad 峰峰（软限位 3.6 rad）——用户看到的「就转那么点距离、摆都甩不起来」就是它。
            // 现在把回中项**单独**钳到 CenterAuthRatio·AMax（默认 20%）：剩下的 80% 永远留给能量项，
            // 回中仍然生效（防摆臂漂走），但不再和注入抢权限。
            //
            // 阻尼改用**实测** φ̇（旧版用 _phiDotCmd 指令速度）：指令速度是积分器自己的状态，
            // 伺服跟不上时它不会减小，阻尼项形同虚设；中心带回中期间用实测速度 + 满权限反向刹车。
            double centerRatio = Pick(p.CenterAuthRatio, def.CenterAuthRatio);
            double centerLimit = (Recentering || Preparing || highEnergyExtraction)
                ? aPumpMax
                : aPumpMax * ControlMath.Clamp(centerRatio, 0.0, 1.0);
            double centering;
            if (highEnergyExtraction)
            {
                // R14：高能段回中让路，禁止普通弹簧的正向注入；把臂速参考反向（抽取方向）。
                // 幅度按超量比例（0.05 J 或 0.5 rad/s 满幅），最大 commitArmRate；
                // 仍走 aPumpMax 加速度钳位与驱动器速度天花板，不新增限速。
                double revFrac = Math.Max(highExcessJ / 0.05, highTopExcess / 0.5);
                revFrac = ControlMath.Clamp(revFrac, 0.0, 1.0);
                double reverseDir = Math.Abs(phiDotRadPerSec) > 0.5
                    ? -Math.Sign(phiDotRadPerSec)
                    : Math.Sign(energyAcc);
                if (reverseDir == 0.0) reverseDir = 1.0;
                double reverseRate = reverseDir * commitArmRate * revFrac;
                // R15 软限位感知：反向目标若指向 φMax/φMin，改取 sign(energyAcc)（真正去能方向）；
                // 两个方向都没有空间就置 0，只制动，绝不把臂顶在限位上。
                bool atPosLimit = ControlMath.IsFinite(phiMax) &&
                                  phiRad >= phiMax - HighEnergyLimitMarginRad;
                bool atNegLimit = ControlMath.IsFinite(phiMin) &&
                                  phiRad <= phiMin + HighEnergyLimitMarginRad;
                HighEnergyLimitCode = atPosLimit ? 1 : (atNegLimit ? -1 : 0);
                if ((reverseRate > 0.0 && atPosLimit) || (reverseRate < 0.0 && atNegLimit))
                {
                    double energyDir = Math.Sign(energyAcc);
                    bool energyBlocked = (energyDir > 0.0 && atPosLimit) ||
                                         (energyDir < 0.0 && atNegLimit);
                    if (energyDir == 0.0 || energyBlocked)
                    {
                        reverseRate = 0.0;
                    }
                    else
                    {
                        reverseRate = energyDir * commitArmRate * revFrac;
                    }
                }
                HighEnergyReverseRateRadPerSec = reverseRate;
                centering = 20.0 * (reverseRate - phiDotRadPerSec);
            }
            else
            {
                HighEnergyReverseRateRadPerSec = 0.0;
                HighEnergyLimitCode = 0;
                centering = -p.KPhi * phiRad - p.KPhiDot * phiDotRadPerSec;
                if (Preparing)
                {
                    // 捕获准备：**限速刹车**，不是回中刹车。目标速度 = sign(φ̇)·CaptureCommitArmRate
                    // （默认 3.0），只把超出部分的臂速压回门限，不反向、不回 0。
                    // 理由：旧版按 vHome→0 刹到静止，多抽走的能量在只有 ~2 J 的系统里足以把起摆抽干
                    //（台架重放：全刹车 −3.62 J/run）。**第二十一轮起本门限不再是交棒判据**，
                    // 只是让掠顶时臂速别无限涨的保守刹车；刹车判为注入的半周期不出力（见上）。
                    // 速度低于门限时 Preparing 本身为假（armTooFast），这里不再出力。
                    double targetRate = phiDotRadPerSec > 0.0 ? commitArmRate : -commitArmRate;
                    centering = 20.0 * (targetRate - phiDotRadPerSec);
                }
                else if (Recentering && Math.Abs(phiDotRadPerSec) > RecenterExitRateRadPerSec)
                {
                    // 还带着明显速度：叠加满权限反向刹车（取更“刹车”的那个方向），
                    // 不允许只靠 KPhiDot 慢慢收 —— 软限位可能只剩几度。
                    double brake = -Math.Sign(phiDotRadPerSec) * aPumpMax;
                    centering = brake > 0 ? Math.Max(centering, brake) : Math.Min(centering, brake);
                }
            }
            CenteringAccelRadPerSec2 = centering;

            // ---- 回中饱和 / φ 均值滚动诊断（只读，不参与控制） ----
            // 饱和 = 本拍回中项在钳位前就超出 centerLimit；台架失败轮的饱和率 77~78%，
            // 成功轮 0%。τ=1 s 的一阶指数平均即"最近约 1 s 的饱和占比"。
            bool centerSat = Math.Abs(centering) > centerLimit;
            CenterSaturated = centerSat;
            double satAlpha = dt / (CenterSatTauSeconds + dt);
            _centerSatRatio += ((centerSat ? 1.0 : 0.0) - _centerSatRatio) * satAlpha;
            CenterSatRatio = _centerSatRatio;
            _phiMean += (phiRad - _phiMean) * satAlpha;
            PhiMeanRad = _phiMean;

            centering = ControlMath.Clamp(centering, -centerLimit, centerLimit);
            CenteringTotalAccelRadPerSec2 = centering;
            acc += centering;
            // 诊断：四项之和（能量 + 泵 + 回中钳位 + 起动）与最终钳位后的实际值分开记账。
            CommandAccelRawRadPerSec2 = acc;
            acc = ControlMath.Clamp(acc, -aPumpMax, aPumpMax);

            // 非有限加速度按 0 处理并保持可运行：不抛异常、不锁存故障。必须在积分前做。
            if (!ControlMath.IsFinite(acc)) acc = 0;
            CommandAccelRadPerSec2 = acc;

            // ---- 速度参考积分（2026-11-21 第二十三轮：删除软件限速钳位）----
            // 旧版这里是 clamp(_phiDotCmd + acc·dt, ±vPumpMax)。一旦直流把积分顶到钳位上，
            // 反向的刹车加速度**无法让速度回到负值**：交变指令被半波整流成净单向推力，
            // 目标位置恒以 19.9 rad/s 前进、从不反向，能量永远抽不回来（台架 E=3.5 J 卡 90 s）。
            // 现在只保留一个数值护栏 = 驱动器 0x6080 的物理天花板（314 rad/s）：它高于实测 98.7 rad/s，
            // 不参与任何控制律的权限分配，只防止坏输入把积分器灌成天文数字。
            _phiDotCmd += acc * dt;
            if (_phiDotCmd > SafetyLimits.ArmDriveCeilingRadPerSec)
                _phiDotCmd = SafetyLimits.ArmDriveCeilingRadPerSec;
            else if (_phiDotCmd < -SafetyLimits.ArmDriveCeilingRadPerSec)
                _phiDotCmd = -SafetyLimits.ArmDriveCeilingRadPerSec;

            // 指令位置撞软限位时把朝外的指令速度清零：旧版只钳位置、速度继续积压，
            // 一旦符号翻转要先卸掉这段「速度债」，摆臂会贴着限位多停很久。
            double phiNext = _phiCmd + _phiDotCmd * dt;
            if (phiNext > phiMax)
            {
                phiNext = phiMax;
                if (_phiDotCmd > 0) _phiDotCmd = 0;
            }
            else if (phiNext < phiMin)
            {
                phiNext = phiMin;
                if (_phiDotCmd < 0) _phiDotCmd = 0;
            }
            _phiCmd = phiNext;
            phiTargetRad = _phiCmd;
            VelocityRefRadPerSec = _phiDotCmd;
            PositionRefRad = _phiCmd;

            // 切换判据（= 捕获门）：第九轮起**只认不稳定模态幅值 u 与摆臂行程预算**。
            // u = ψ + (ω − (r/l)φ̇)/ω_n 已把位置与角速度合成一条物理包线，
            // 再单列 |ψ|/|ω| 阈值等于同一约束重复计数（r8 饿死的主因）。
            // ω 用最小二乘估计：2 ms 低通后的差分量噪声约为它的 100 倍，拿噪声判"ω 小"会抖成随机。
            // **中心带回中期间绝不切换**：摆臂还没回家就交给平衡，等于把回中问题塞进平衡环。
            double thetaFromUpright = ControlMath.WrapPi(theta - Math.PI);
            // ---- 交棒预测外推（2026-11-21 第二十轮；同日修正采样口径）----
            // `[SWING]` 在 |ψ|<0.5 时按 100 Hz 记录，控制是 2 ms（500 Hz）：最后一次日志与真正的
            // decision 拍可差 1~5 拍（2~10 ms），而 EnterBalancing 与 decision 同拍执行。
            // 因此用平衡律外推 T=24 拍（48 ms）后的 (ψ_pred, ω_pred) 判硬门 —— 等于要求
            // 「按平衡律收敛 48 ms 后仍在包线内」，把决策拍与平衡律接管之间的时序不确定性收进判据。
            //
            // **ω 一律用 ω_est（40 ms 最小二乘）判门，不用 2 ms 差分后的实测 ω**：
            // 摆杆 2400 count/rev、2 ms 周期时 1 个 count = 1.31 rad/s，实测 ω 的逐拍跳变
            // sd 达 0.4~1.4 rad/s（round20 三条日志实测）；拿它判 |ω|≤1.0 会被量化噪声
            // 顶成随机拒绝/放行。ω_est 的 sd 只有 0.16 rad/s，滞后约 20 ms，
            // 相对不稳定时间常数 1/ω_n≈110 ms 可以接受（判据本来就已外推 48 ms 补偿）。
            // 归一化 LQR 预测固定 48 ms（48 拍 @1 ms，与 β=0.048 同一口径）。
            double predT = 0.048;
            // 增益单一权威：预测、入口与 RunControlLaw 读同一组 PendulumEngine 常量。
            double kaP = PendulumEngine.BalanceKPsi;
            double kadP = PendulumEngine.BalanceKOmega;
            double kphP = PendulumEngine.BalanceKPhi;
            double kphidP = PendulumEngine.BalanceKPhiDot;
            double balanceSign = PendulumEngine.BalanceLawSign;
            double omegaGate = omegaEst;
            System.Diagnostics.Debug.Assert(
                PendulumEngine.BalanceKPsi == 5015.0 &&
                PendulumEngine.BalanceKOmega == 573.0 &&
                PendulumEngine.BalanceKPhi == -8.51 &&
                PendulumEngine.BalanceKPhiDot == -6.16,
                "平衡增益权威被改动：预测、入口与平衡律必须同步。");
            double captureCenter = ControlMath.IsFinite(phiMinRad) &&
                                   ControlMath.IsFinite(phiMaxRad)
                ? 0.5 * (phiMinRad + phiMaxRad)
                : 0.0;
            double positionError = ControlMath.Clamp(phiRad - captureCenter,
                -PendulumEngine.BalancePositionErrorClampRad,
                PendulumEngine.BalancePositionErrorClampRad);
            double aPend = kaP * thetaFromUpright + kadP * omegaGate;
            double aArm = kphP * positionError + kphidP * phiDotRadPerSec;
            double a1 = aPend + aArm;
            double aCmdEntry = -a1;
            // 预测和入口用同一 K、同一 Kφdot、同一 β。
            double aPred = pendRate * pendRate * Math.Sin(thetaFromUpright)
                         + balanceSign * SwingUpParams.VerifiedCaptureCouplingRatioRad
                           * Math.Cos(thetaFromUpright) * aCmdEntry;
            double psiPred = thetaFromUpright + omegaGate * predT + 0.5 * aPred * predT * predT;
            double omegaPred = omegaGate + aPred * predT;
            double modeAmpPred = ComputeModeAmplitude(psiPred, omegaPred, phiDotRadPerSec,
                SwingUpParams.VerifiedCaptureCouplingRatioRad, pendRate);
            // 捕获门：u≤0.12、|φ̇|≤2.5、|ψ|≤0.25；|ψ|<0.12 时 ω≤0.8；三组预算分别限幅。
            // 2026-11-21 R7/R8 台架：两道交棒都在 |φ̇|≈2.92（臂已负向）时放行，PD 给出
            // −435 / −606 rad/s²，0.6 s 内 ThetaOverrun。|φ̇|≤2.5 只是收紧现有臂速预算，
            // 不放宽任何角度/角速度门；参数层 CaptureArmRateLimit=4.0 / CommitArmRate=3.0 不变。
            bool lowPsiOmegaOk =
                Math.Abs(thetaFromUpright) >= 0.12 ||
                Math.Abs(omegaEst) <= 0.8;
            // 交棒入口硬门：|ψ|≤0.15、|ω_est|≤1.2。R25 12:16 段在 |ψ|=0.23、
            // ω=-1.02 时放行（旧门允许到 0.25），0.36 s 内 ThetaOverrun；
            // R22 成功段 ψ0=-0.144、ω0=1.02 仍在此门内。
            bool currentStateOk =
                Math.Abs(thetaFromUpright) <= 0.15 &&
                Math.Abs(omegaEst) <= 1.2 &&
                Math.Abs(phiDotRadPerSec) <= 2.5 &&
                lowPsiOmegaOk &&
                Math.Abs(aPend) <= 960.0 &&
                Math.Abs(aArm) <= 160.0 &&
                // 入口必须不饱和：R22 成功接管 |aCmdRaw|=170<250；
                // R25 失败段 |aCmdRaw|>700，首拍就顶死并卷参考，随后只剩饱和没有权限。
                Math.Abs(a1) <= 220.0;
            bool headGate = CaptureGateAllows(psiPred, omegaPred, phiDotRadPerSec, p);
            bool ready =
                !Recentering &&
                currentStateOk &&
                headGate;
            // 只读诊断快照：让 [SWING] 日志能逐拍看到入口门到底卡在哪一条。
            CaptureEntryAccelRadPerSec2 = aCmdEntry;
            CaptureCurrentStateOk = currentStateOk;
            CaptureHeadGateOk = headGate;
            CaptureReady = ready;
            CapturePendAccelRadPerSec2 = aPend;
            CaptureArmAccelRadPerSec2 = aArm;
            CaptureTotalAccelRadPerSec2 = a1;
            CaptureModeAmpRad = modeAmpPred;

            // 快速提交带 = u 包线硬门 ∩ |ω_est| ≤ commitOmega ∩ |φ̇| ≤ 3.0。
            bool tight = ready && Math.Abs(omegaGate) <= commitOmega &&
                         Math.Abs(phiDotRadPerSec) <= 3.0;

            // ---- 近顶端驻留计数（漏桶，单位 = 千分之一拍）----
            // 台架物理约束（2026-11-20 实测）：
            //   能量窗宽 gateHigh−gateLow ≈ 0.0178 J；E 变化率 1.1~1.3 J/s
            //   ⇒ 单次掠顶门只开 0.0178/1.2 ≈ **14~16 ms**（1 ms 周期 = 14~16 拍）。
            //   两次掠顶间隔 1.5~3 s = **1500~3000 拍**，是窗口的 100~200 倍。
            // 要跨周期累积，每拍衰减必须 < 窗口/间隔 ≈ 14/3000 = **0.0047 拍**；
            // 旧的「每拍 −1 拍」（甚至 −0.25 拍）一次间隔就扣 1500~3000 拍，
            // 所以 holdSlowMax 卡在 48/60、倒计时 20→12→20 反复——不是门不开，是计数器数学上攒不起来。
            // 现在：
            //   ① 单位改成千分之一拍（milli-cycle），默认 k=2 ⇒ 0.002 拍/拍，比 0.0047 上限留 2.3 倍裕量；
            //   ② |ψ| > CapturePrepareApproachRad（0.6）时**冻结**计数：摆动间隔再长也不扣，
            //      跨掠顶累积与摆动周期无关；扣减只用于同一掠顶内的边界抖动；
            //   ③ Recentering 是硬违规，计数清零。
            // 台架窗口 14~48 ms/次（holdSlowMax=48 说明有效窗可达 48 拍）⇒ 60 拍目标需 2~5 个掠顶周期。
            bool nearTop = upright <= prepareApproach;
            if (Recentering)
            {
                _switchHoldMilli = 0;
                _commitHoldMilli = 0;
            }
            else if (nearTop)
            {
                if (ready)
                    _switchHoldMilli = Math.Min(_switchHoldMilli + 1000L, targetSlowMilli);
                else
                    _switchHoldMilli = Math.Max(_switchHoldMilli - holdDecay, 0L);

                if (tight)
                    _commitHoldMilli = Math.Min(_commitHoldMilli + 1000L, targetFastMilli);
                else
                    _commitHoldMilli = Math.Max(_commitHoldMilli - holdDecay, 0L);
            }
            // 远离顶端：冻结。计数只在顶端附近增长/衰减，间隔期不清零。

            bool slowCommit = _switchHoldMilli >= targetSlowMilli && tight;
            bool fastCommit = _commitHoldMilli >= targetFastMilli && tight;
            SlowHold = (int)(_switchHoldMilli / 1000L);
            FastHold = (int)(_commitHoldMilli / 1000L);

            int slowRemaining = (int)((targetSlowMilli - _switchHoldMilli + 999L) / 1000L);
            if (slowRemaining < 0) slowRemaining = 0;
            // 慢通道计数已满但本拍不在提交带内：还差「回到提交带」这一下，别把倒计时打成 0 误导现场。
            if (slowRemaining == 0 && !tight) slowRemaining = 1;
            int fastRemaining = (int)((targetFastMilli - _commitHoldMilli + 999L) / 1000L);
            if (fastRemaining < 0) fastRemaining = 0;
            int remaining = slowRemaining < fastRemaining ? slowRemaining : fastRemaining;
            SwitchCountdown = remaining > 0 ? remaining : 0;

            // ---- 折返角反馈（A/B-4）：检测折返、按折返角调能量目标偏置 ----
            // 折返角 = 本次接近顶端的最小 |ψ|（|ψ|→0 表示越过顶端，能量过高）。
            // 检测：ω_est 带 0.05 rad/s 迟滞的符号翻转；跟踪区 |ψ|≤0.5。
            // **跟踪区为什么不是 0.10**：偏置一旦把能量目标压低，折返角会先落到 0.1~0.3 rad；
            // 若跟踪区只到 0.10，反馈在需要回升时反而失明，偏置卡在低档、能量回不到捕获窗。
            // 0.5 只影响「检测」，交棒仍由 gate 的 |ψ|≤0.10 硬拦，所以域外折返不会被交棒。
            // 每 pass 最多调一档；步长自适应只看 |ΔE|：<0.03 → 0.002 J，≥0.03 → 0.01 J，
            // ≥0.10 → 0.03 J（过冲 0.15 J 时 5 pass 内拉回）。偏置钳 ±0.15 J，目标再钳 2mgl±0.15 J。
            // 0.06~0.09 内不动（死区）。
            bool turnDetected = false;
            if (upright <= TurnTrackZoneRad)
            {
                if (!_turnVisitActive)
                {
                    _turnVisitActive = true;
                    _turnMinAbsPsi = upright;
                    // 进入跟踪区先取一次符号基准；真实折返时 ω_est 是逐拍穿过 ±0.05 的，
                    // 拿「上一拍的值」和「本拍的值」同时跨两侧判会永远漏掉（离线复现：
                    // 偏置降档后折返角 0.14~0.23 rad，反馈整段失明）。
                    _turnOmegaSign = omegaEst > TurnHysteresisRadPerSec ? 1
                        : (omegaEst < -TurnHysteresisRadPerSec ? -1 : 0);
                }
                else if (upright < _turnMinAbsPsi)
                {
                    _turnMinAbsPsi = upright;
                }

                // 交叉检测看**符号状态**：折返 = 跟踪区内的 ω_est 符号翻转（带 0.05 迟滞）。
                int omegaSign = omegaEst > TurnHysteresisRadPerSec ? 1
                    : (omegaEst < -TurnHysteresisRadPerSec ? -1 : 0);
                if (omegaSign != 0)
                {
                    if (_turnOmegaSign != 0 && omegaSign != _turnOmegaSign) turnDetected = true;
                    _turnOmegaSign = omegaSign;
                }
            }
            else if (_turnVisitActive)
            {
                // 离开跟踪区但没有变号 = 越过顶端（|ψ| 到过 ~0）：能量偏高，降一档。
                if (_turnMinAbsPsi < TurnAngleLowRad)
                {
                    _turnBiasJ -= TurnBiasStepFor(delta);
                    if (_turnBiasJ < -TurnBiasMaxJ) _turnBiasJ = -TurnBiasMaxJ;
                    LastTurnAngleRad = _turnMinAbsPsi;
                    TurnCount++;
                }
                _turnVisitActive = false;
                _turnOmegaSign = 0;
            }

            if (turnDetected)
            {
                double turnAbs = _turnMinAbsPsi;
                LastTurnAngleRad = turnAbs;
                TurnCount++;
                // 物理方向：折返角越大 = 能量越低 ⇒ 抬高目标；折返角越小 = 能量越高 ⇒ 降低目标。
                double step = TurnBiasStepFor(delta);
                if (turnAbs > TurnAngleHighRad) _turnBiasJ += step;
                else if (turnAbs < TurnAngleLowRad) _turnBiasJ -= step;
                if (_turnBiasJ > TurnBiasMaxJ) _turnBiasJ = TurnBiasMaxJ;
                else if (_turnBiasJ < -TurnBiasMaxJ) _turnBiasJ = -TurnBiasMaxJ;
                _turnVisitActive = false;
                _turnOmegaSign = 0;
            }
            // 负向偏置的合法权限由**物理可达性**给出（见上面 turnBiasLow 的推导）：
            // 目标不能低于 gateEnergyLow，否则摆杆到不了 |ψ|≤θ_switch，捕获数学上不可能。
            // r9 实测 bias 顶到 −0.15 J 就是这条缺失的收口；正常物理带宽只有约 −0.009 J。
            if (_turnBiasJ < turnBiasLow) _turnBiasJ = turnBiasLow;
            TurnBiasJ = _turnBiasJ;

            return slowCommit || fastCommit;
        }

        /// <summary>
        /// 折返偏置的自适应步长：|ΔE| &lt; 0.03 J 用 0.002 J，≥0.03 J 用 0.01 J，
        /// ≥0.10 J 用 0.03 J（过冲 0.15 J 时 5 pass 内拉回）。
        /// **量级只看能量误差，不看折返角偏差**：折返角对能量非常敏感（dψ/dδ≈11~16 rad/J），
        /// 过顶 pass 的能量误差可能只有 0.001 J 而折返角是 0；拿角度定步长会在 0/0.13 rad 之间
        /// 打 0.01 J 的乒乓（离线复现），只有 |ΔE| 才是控制误差。
        /// </summary>
        private static double TurnBiasStepFor(double delta)
        {
            double absDelta = Math.Abs(delta);
            if (absDelta >= TurnExtremeEnergyErrorJ) return TurnBiasExtremeStepJ;
            if (absDelta >= TurnFastEnergyErrorJ) return TurnBiasFastStepJ;
            return TurnBiasStepJ;
        }

        /// <summary>
        /// 捕获门：**不稳定模态幅值 u** + **摆臂角速度硬上限**（2026-11-21 第二十二轮）。
        /// 返回 true 才允许把摆杆交给平衡级联。
        /// <para>
        /// <c>u = ψ + (ω − CalibrationSign·(r/l)·φ̇)/ω_n</c> 已经把位置与角速度合成一条物理包线，
        /// 它就是「摆杆现在有多难拉住」的完整度量。第二十一轮把旧的
        /// 「摆臂行程预算 / |φ|≤phiCap / 距限位储备」三道门全部退出交棒判据
        ///（台架现象：摆杆到顶、ω_est 已经很小，臂还在 6~10 rad/s，门不放行）；
        /// 摆臂行程与软限位仍由执行链（<c>RunControlLaw</c> 的弧度/counts 钳位与 slew 护栏）负责。
        /// </para>
        /// <para>
        /// 交棒不再设臂速阈值：u 已包含 φ̇ 的动量耦合，额外阈值只会挡住「摆杆已到顶但臂仍在运动」
        /// 的可恢复状态。接不住由真实平衡与 S2 跟随误差如实裁决。
        /// </para>
        /// </summary>
        /// <param name="thetaFromUprightRad">ψ = wrapPi(θ-π) [rad]，正 = 摆杆偏向一侧（交棒判据传 48 ms 外推后的 ψ_pred）。</param>
        /// <param name="omegaRadPerSec">ω 估计 [rad/s]（交棒判据传外推后的 ω_pred）。</param>
        /// <param name="phiDotRadPerSec">摆臂实际角速度 [rad/s]，只进 u 的动量耦合。</param>
        /// <param name="p">起摆参数（含 u 包线上限与 ω_n）。</param>
        internal static bool CaptureGateAllows(
            double thetaFromUprightRad,
            double omegaRadPerSec,
            double phiDotRadPerSec,
            SwingUpParams p)
        {
            if (!ControlMath.IsFinite(thetaFromUprightRad) || !ControlMath.IsFinite(omegaRadPerSec) ||
                !ControlMath.IsFinite(phiDotRadPerSec))
            {
                return false;
            }

            SwingUpParams def = DefaultParams;
            double modeMax = CaptureModeAmplitudeMax(p);
            double pendulumRate = Pick(p.CapturePendulumRateRadPerSec, def.CapturePendulumRateRadPerSec);
            double modeAmp = ComputeModeAmplitude(thetaFromUprightRad, omegaRadPerSec, phiDotRadPerSec,
                SwingUpParams.VerifiedCaptureCouplingRatioRad, pendulumRate);
            return Math.Abs(modeAmp) <= modeMax;
        }

        /// <summary>
        /// 捕获门模态幅值的**生效上限** [rad]：<c>min(参数值, VerifiedMaxCaptureModeAmplitudeRad=0.12)</c>。
        /// <para>
        /// 硬门（<see cref="CaptureGateAllows"/>，在预测状态上）与 gateEnergyHigh（预捕获能量窗）
        /// 必须用同一个上限，否则两处判的不是同一件事。参数层已把 &gt;0.12 的值回落，
        /// 这里再夹一次是运行期兜底（GUI 直接调用 / 旧参数对象也走这条路径）。
        /// 提交带不再单独判实测 u（第七轮拿掉），所以这里只服务硬门。
        /// </para>
        /// </summary>
        internal static double CaptureModeAmplitudeMax(SwingUpParams p)
        {
            double configured = Pick(p.CaptureModeAmplitudeMaxRad, DefaultParams.CaptureModeAmplitudeMaxRad);
            return configured < SwingUpParams.VerifiedMaxCaptureModeAmplitudeRad
                ? configured
                : SwingUpParams.VerifiedMaxCaptureModeAmplitudeRad;
        }

        /// <summary>
        /// 不稳定模态幅值 u = ψ + (ω − CalibrationSign·(r/l)·φ̇)/ω_n [rad]。
        /// <para>
        /// 2026-11-21 更正：`calibrate-radius` 历史全部失败的原因是候选 r/l **全为负**
        ///（−0.112/−0.059/−0.032/−0.072/−0.064/−0.054），被正值下限拒掉；S 翻正后同一批数据
        /// 变成 +0.03~+0.11，可以通过。
        /// </para>
        /// <para>
        /// **2026-11-21 第十一轮裁定：r/l = `SwingUpParams.VerifiedCaptureCouplingRatioRad`（0.048）。**
        /// 捕获门与 48 ms 预测共用这一个值；`limits.radius_ratio`（0.24）只归平衡律的 plant 模型。
        /// 依据是 r11 第一次交棒的实证反例（0.24 放行 / 0.048 拒绝，放行后 0.26 s FollowingError）。
        /// </para>
        /// 捕获门、快速提交带、日志/遥测共用这一个定义，避免两处各写一份符号与钳位。
        /// 耦合符号只认 <see cref="PendulumEngine.CalibrationSign"/>（2026-11-21 台架反解 = +1），
        /// 本方法不再自行假设 +r/l。r/l ≤0 时退化为 u = ψ + ω/ω_n。
        /// </summary>
        /// <param name="thetaFromUprightRad">ψ = wrapPi(θ−π) [rad]。</param>
        /// <param name="omegaRadPerSec">ω 估计 [rad/s]。</param>
        /// <param name="phiDotRadPerSec">摆臂实际角速度 [rad/s]，按 −CalibrationSign·(r/l)·φ̇ 折进动量耦合。</param>
        /// <param name="radiusRatio">机构耦合比 |r/l|（0 = 未标定）。符号由 CalibrationSign 统一提供。</param>
        /// <param name="pendulumRate">摆杆固有角频率 ω_n [rad/s]。</param>
        internal static double ComputeModeAmplitude(
            double thetaFromUprightRad,
            double omegaRadPerSec,
            double phiDotRadPerSec,
            double radiusRatio,
            double pendulumRate)
        {
            double coupledOmega = omegaRadPerSec;
            if (radiusRatio > 0 && ControlMath.IsFinite(radiusRatio))
            {
                double ratio = Math.Min(radiusRatio, SafetyLimits.MaxReasonableRadiusRatio);
                coupledOmega -= PendulumEngine.CalibrationSign * ratio * phiDotRadPerSec;
            }
            return thetaFromUprightRad + coupledOmega / pendulumRate;
        }

        /// <summary>参数取值：&gt;0 且有限就用它，否则用内置默认（旧参数文件缺这些键时读出来全是 0）。</summary>
        private static double Pick(double value, double fallback)
        {
            return value > 0 && ControlMath.IsFinite(value) ? value : fallback;
        }

        /// <summary>起摆与平衡共用的低噪声 ω 估计（40 ms 最小二乘，见 <see cref="PendulumOmegaEstimator"/>）。</summary>
        private double EstimateOmega(double thetaRad, double fallbackOmega, double dt, double windowMs)
        {
            return _omegaEstimator.Update(thetaRad, fallbackOmega, dt, windowMs);
        }

        /// <summary>把相位归一到 [0, 2π)，避免负角 / 多圈相位参与 sin 时符号难读。</summary>
        private static double NormalizePhase(double phase)
        {
            if (!ControlMath.IsFinite(phase)) return 0;
            double value = phase % ControlMath.TwoPi;
            if (value < 0) value += ControlMath.TwoPi;
            return value;
        }
    }

    /// <summary>
    /// 起摆捕获门与平衡律共用的 ω 最小二乘估计。二次拟合取窗口末端斜率，
    /// 40 ms 窗在 2 kHz 下把 1 count 的差分量噪声压到可判据的量级。
    /// 同一份实现同时服务 SwingUpController 与 PendulumEngine，避免两套估计口径。
    /// </summary>
    internal sealed class PendulumOmegaEstimator
    {
        private const int Capacity = 256;
        private const int MinSamples = 8;

        private readonly double[] _timeSec = new double[Capacity];
        private readonly double[] _thetaUnwrapped = new double[Capacity];
        private int _head;
        private int _count;
        private double _clockSec;
        private double _lastRawTheta;
        private double _unwrappedTheta;

        /// <summary>本拍估计值 [rad/s]。</summary>
        public double OmegaRadPerSec { get; private set; }

        /// <summary>窗口二次拟合的角残差 RMS [rad]，只用于噪声诊断。</summary>
        public double ResidualRmsRad { get; private set; }

        /// <summary>
        /// 本拍二次拟合的角加速度 [rad/s²]：θ̈ = 2·c₂/halfSpan²，c₂ 是 40 ms 窗口二次项的曲率。
        /// 用整窗最小二乘而不是位置二次差分，噪声被样本数和拟合抑制；只给扰动观测器读，不进入 ω 控制律。
        /// </summary>
        public double AccelerationRadPerSec2 { get; private set; }

        /// <summary>角加速度是否可用（样本足够、拟合非退化、曲率有限）。</summary>
        public bool AccelerationValid { get; private set; }

        public void Reset()
        {
            _head = 0;
            _count = 0;
            _clockSec = 0;
            _lastRawTheta = 0;
            _unwrappedTheta = 0;
            OmegaRadPerSec = 0;
            ResidualRmsRad = 0;
            AccelerationRadPerSec2 = 0;
            AccelerationValid = false;
        }

        public double Update(double thetaRad, double fallbackOmega, double dt, double windowMs)
        {
            if (!ControlMath.IsFinite(thetaRad) || !ControlMath.IsFinite(dt) || dt <= 0)
            {
                AccelerationRadPerSec2 = 0;
                AccelerationValid = false;
                return ControlMath.IsFinite(fallbackOmega) ? fallbackOmega : OmegaRadPerSec;
            }

            double windowSec = windowMs > 0 && ControlMath.IsFinite(windowMs) ? windowMs * 0.001 : 0.040;
            double targetSamples = windowSec / dt;
            if (targetSamples < MinSamples) targetSamples = MinSamples;
            if (targetSamples > Capacity) targetSamples = Capacity;
            int maxSamples = (int)targetSamples;

            if (_count == 0)
            {
                _lastRawTheta = thetaRad;
                _unwrappedTheta = thetaRad;
            }
            else
            {
                double d = thetaRad - _lastRawTheta;
                if (d > Math.PI) d -= ControlMath.TwoPi;
                else if (d < -Math.PI) d += ControlMath.TwoPi;
                _lastRawTheta = thetaRad;
                _unwrappedTheta += d;
            }

            _clockSec += dt;
            _timeSec[_head] = _clockSec;
            _thetaUnwrapped[_head] = _unwrappedTheta;
            _head = (_head + 1) % Capacity;
            if (_count < maxSamples) _count++;

            int n = _count;
            if (n < MinSamples)
            {
                OmegaRadPerSec = ControlMath.IsFinite(fallbackOmega) ? fallbackOmega : 0.0;
                ResidualRmsRad = 0;
                AccelerationRadPerSec2 = 0;
                AccelerationValid = false;
                return OmegaRadPerSec;
            }

            double meanT = 0, meanY = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = _head - 1 - i;
                if (idx < 0) idx += Capacity;
                meanT += _timeSec[idx];
                meanY += _thetaUnwrapped[idx];
            }
            meanT /= n;
            meanY /= n;

            int newest = _head - 1;
            if (newest < 0) newest += Capacity;
            int oldest = _head - n;
            while (oldest < 0) oldest += Capacity;
            double halfSpan = (_timeSec[newest] - _timeSec[oldest]) * 0.5;
            if (!(halfSpan > 1e-9) || !ControlMath.IsFinite(halfSpan))
            {
                OmegaRadPerSec = ControlMath.IsFinite(fallbackOmega) ? fallbackOmega : 0.0;
                ResidualRmsRad = 0;
                AccelerationRadPerSec2 = 0;
                AccelerationValid = false;
                return OmegaRadPerSec;
            }

            double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            double sy = 0, suy = 0, su2y = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = _head - 1 - i;
                if (idx < 0) idx += Capacity;
                double u = (_timeSec[idx] - meanT) / halfSpan;
                double u2 = u * u;
                s1 += u;
                s2 += u2;
                s3 += u2 * u;
                s4 += u2 * u2;
                double y = _thetaUnwrapped[idx] - meanY;
                sy += y;
                suy += u * y;
                su2y += u2 * y;
            }

            double a11 = s0, a12 = s1, a13 = s2;
            double a22 = s2, a23 = s3, a33 = s4;
            double det = a11 * (a22 * a33 - a23 * a23)
                       - a12 * (a12 * a33 - a23 * a13)
                       + a13 * (a12 * a23 - a22 * a13);
            if (!ControlMath.IsFinite(det) || Math.Abs(det) < 1e-18)
            {
                OmegaRadPerSec = ControlMath.IsFinite(fallbackOmega) ? fallbackOmega : 0.0;
                ResidualRmsRad = 0;
                AccelerationRadPerSec2 = 0;
                AccelerationValid = false;
                return OmegaRadPerSec;
            }

            double d1 = a11 * (suy * a33 - a23 * su2y)
                      - sy * (a12 * a33 - a23 * a13)
                      + a13 * (a12 * su2y - suy * a13);
            double d2 = a11 * (a22 * su2y - suy * a23)
                      - a12 * (a12 * su2y - suy * a13)
                      + sy * (a12 * a23 - a22 * a13);
            double c1 = d1 / det;
            double c2 = d2 / det;
            double c0 = (sy - c1 * s1 - c2 * s2) / n;
            double uNewest = (_timeSec[newest] - meanT) / halfSpan;
            double slope = (c1 + 2.0 * c2 * uNewest) / halfSpan;

            // 二次项曲率 = θ̈。halfSpan 是窗半宽 [s]，d²y/dt² = 2·c₂/halfSpan²。
            // 至少 16 个样本（500 µs 周期 = 8 ms）才把曲率交给扰动观测器：样本太少的
            // 二次拟合曲率噪声会被 1/halfSpan² 放大，低通也压不住。
            // 限幅只挡坏拟合，不把真实冲击截成 0；超限说明窗口已被坏样本污染，宁可不给观测器用。
            double acceleration = 2.0 * c2 / (halfSpan * halfSpan);
            if (n >= 16 && ControlMath.IsFinite(acceleration) && Math.Abs(acceleration) <= 2000.0)
            {
                AccelerationRadPerSec2 = acceleration;
                AccelerationValid = true;
            }
            else
            {
                AccelerationRadPerSec2 = 0;
                AccelerationValid = false;
            }

            double sse = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = _head - 1 - i;
                if (idx < 0) idx += Capacity;
                double u = (_timeSec[idx] - meanT) / halfSpan;
                double predicted = c0 + c1 * u + c2 * u * u;
                double residual = (_thetaUnwrapped[idx] - meanY) - predicted;
                sse += residual * residual;
            }
            ResidualRmsRad = Math.Sqrt(sse / n);

            double estimate = slope;
            if (!ControlMath.IsFinite(estimate) || Math.Abs(estimate) > 200.0 ||
                (ControlMath.IsFinite(fallbackOmega) && Math.Abs(estimate - fallbackOmega) > 6.0))
                estimate = ControlMath.IsFinite(fallbackOmega) ? fallbackOmega : 0.0;
            OmegaRadPerSec = estimate;
            return estimate;
        }
    }
}
