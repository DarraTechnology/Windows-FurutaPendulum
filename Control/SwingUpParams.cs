using System;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 起摆（能量法）参数。换一台摆（换 J_eq / mgl）只调 Ke 与 AMax，其余基本不动。
    /// </summary>
    public struct SwingUpParams
    {
        /// <summary>双缓冲代号。SetSwingUpParams 时自增，周期线程在拍首顶换。</summary>
        public uint Generation;

        /// <summary>
        /// 目标能量裕量 ε（无量纲）。E* = 2·mgl·(1 + ε)，2·mgl 就是"刚好能到直立"的分界能量。默认 **-0.002**。
        /// <para>
        /// 必须是**极小且略负**：这个数是起摆与平衡的交接点，它不是"多给点余量更保险"，而是必须落在
        /// 切换判据能接住的那条能量带上。推导（θ 自下垂点量起，ψ = π − θ 为离直立角）：
        /// <code>
        /// E = 0.5·J·ω² + mgl·(1 + cosψ)            // 注意直立处 (1+cosψ)=2
        /// 摆杆在 ψ_turn 处折返（ω=0）⇒ E = mgl·(1 + cosψ_turn)
        /// 切换判据要求 |ψ| ≤ θ_switch（默认 0.10 rad）
        /// ⇒ 可交接能量带 E ∈ [mgl·(1+cos0.10), 2mgl) = [1.9950, 2.0)（mgl=1 时）
        /// </code>
        /// 也就是说 **E* 必须落在分界线下方 0.25% 以内**（ε ∈ [−0.0025, 0)）；
        /// 一旦 ε&gt;0（旧默认 +0.05），摆杆到顶时必然还带着
        /// ω = √(2·0.05·2mgl/J) ≈ 4.05 rad/s，既过不了 |ω| ≤ 0.5，也过不了捕获门的
        /// 模态幅值预算 ——**这时的"起摆成功"在数学上不可能**。
        /// 台架实测正是这样：E 冲到 2.094 J（E* 的 99.7%），但摆杆以 9 rad/s 掠过顶端，永远抓不住。
        /// 取 −0.002（E* = 1.996 J ⇒ ψ_turn ≈ 0.089 rad）：旧 120-pass 离线在 0.06~0.09 带内
        /// 10/10、中位 0.069；新平衡包线 0.162(ω=0)/0.123(ω=0.3)/0.096(ω=0.5) 覆盖该折返。
        /// </para>
        /// </summary>
        public double TargetEnergyGain;

        /// <summary>
        /// 能量误差 → 摆臂加速度增益 [rad/s² per J]，**爬升段**用。默认 60.0（台架整定 700）。
        /// <para>
        /// 量级自检：能量误差从 ΔE = E* ≈ 2.0 J（Mgl=1.0）起算，Ke=60 ⇒ u_E=120 rad/s²，
        /// 顶在 AMax=40 上饱和；要到 ΔE &lt; 0.67 J（E &lt; 1.33 J）才退出饱和，也就是**大半程都用满权限**。
        /// 改 Mgl 的整体尺度后要按同比例改 Ke（u_E = Ke·ΔE 的物理量级 = Ke·Mgl）。
        /// </para>
        /// <para>
        /// **2026-11-21 第十轮：本值只用于爬升段（|ΔE| ≥ 0.30 J）。**
        /// 目标附近改用 <see cref="ConvergenceGainRadPerSec2PerJ"/>=150，并在 0.15~0.30 J 之间平滑过渡。
        /// 原因：收敛段的每半周期能量增量 ∝ C·Ke（C≈0.0033 J per rad/s²），Ke=700 时 C·Ke≈2.3 > 1，
        /// 每个半周期过冲 129% → 目标附近形成 ±0.1 J 的极限环，永远压不进捕获所需的 ±0.005 J。
        /// 用户把 Ke 配得比收敛增益更小（例如 60）时，全程按 Ke 执行（不再插入收敛段），行为与旧版一致。
        /// </para>
        /// </summary>
        public double Ke;

        /// <summary>
        /// 摆臂加速度限幅 [rad/s²]。默认 **100.0**（第八轮折中值），合法
        /// [<see cref="MinSwingAMaxRadPerSec2"/>, <see cref="MaxSwingAMaxRadPerSec2"/>]。
        /// <para>
        /// 它决定能量法的权限上限：dE/dt = −m·l·r·θ̇·cosθ·φ̈，注入率与 φ̈ 成正比。
        /// 台架臂实测可做 100 rad/s、2000 rad/s² 无失步；再叠加旧的 0.66·ω_n 包线
        /// （ω_n=9.0416 ⇒ 实际只有 54 rad/s²），能量爬升率被压在 0.03~0.07 J/s。
        /// </para>
        /// <para>
        /// **2026-11-21 第八轮回退到 100**：150 让起摆 1/7（掠顶太快，|φ̇|≤3 与 |ω_est|≤0.5
        /// 更难同时满足）；80 是 r6 的 5/8 但总时长 30 s 量级。100 是折中：
        /// 注入率约为 54 的 1.85 倍、峰值臂速 100/ω_n≈11.1 rad/s，比 150 低 33%。
        /// 下一轮台架若仍偏慢再往上加；**不要直接跳回 80**。
        /// </para>
        /// <para>
        /// **这是起摆唯一的加速度权限**（2026-11-21 第二十三轮起）：旧版在它之外还乘一道
        /// 「臂速包线 vPumpMax = ratio·ω_n」并硬钳速度积分，等于第二次削幅，而且在钳位点上
        /// 把交变刹车整流成单向推力。现在速度不再由软件封顶，只有 AMax 这一道。
        /// </para>
        /// <para>
        /// 历史：旧值 8.0 与平衡律的 AccMax 差 7.5 倍，起摆最需要权限的区间反而被限得最死；
        /// 20 实测只能爬到 68% E*，40 才够（2026-11-16）；80 用于 11-20~11-21 的台架。
        /// 现场若仍看到能量爬升慢，先看 `[SWING]` 里本拍 φ̈ 有没有顶在 aPumpMax 上。
        /// </para>
        /// </summary>
        public double AMax;

        /// <summary>
        /// 起摆泵加速度权限的合法下限 [rad/s²] = **40**（2026-11-16 已验证 40 能爬到 E*）。
        /// 80 是 r6 用过的值，必须保持可选，便于下一轮 A/B 回退；低于 40 没有实测依据。
        /// </summary>
        public const double MinSwingAMaxRadPerSec2 = 40.0;

        /// <summary>起摆泵加速度权限的合法上限 [rad/s²]。台架臂实测 2000 rad/s² 无失步，超过它没有物理依据。</summary>
        public const double MaxSwingAMaxRadPerSec2 = 2000.0;

        /// <summary>摆臂回中增益 [1/s²]。作用在 φ 上（起始位置 ≡ 坐标 0，没有第二个锚点）。默认 3.0。</summary>
        public double KPhi;

        /// <summary>摆臂阻尼增益 [1/s]。作用在 φ̇ 上。默认 0.8。</summary>
        public double KPhiDot;

        /// <summary>
        /// 回中 / 阻尼项允许占用的加速度预算比例（无量纲，0~1）。默认 0.20。
        /// <para>
        /// 起摆的摆臂加速度预算只有 AMax 那么多，能量项和回中项在抢它。旧版回中项没有单独限额，
        /// 实测 φ̇=9.6 rad/s 时 `-KPhiDot·φ̇ = -14.4 rad/s²`（旧 KPhiDot=1.5），在 AMax=40 里占 36%、
        /// 在 AMax=20 里占 72%，把摆臂的往复幅值压到 **0.85 rad 峰峰**（软限位 3.6 rad）——
        /// 现场看到的就是「摆臂就转那么点距离、甩不起来」。
        /// 钳到 20% 之后，无论 φ / φ̇ 多大，能量项都至少还有 80% 的权限；回中仍然生效（防摆臂漂走）。
        /// 0 = 未配置（旧参数文件没有这个键），由 <see cref="SwingUpController"/> 按默认 0.20 补齐。
        /// </para>
        /// </summary>
        public double CenterAuthRatio;

        /// <summary>
        /// 能量超过目标时允许的**抽取**权限占 AMax 的比例（无量纲，0~1）。默认 **1.0**（第九轮）。
        /// <para>
        /// 只注入不抽取会在能量冲过 E* 后留下一个**高能极限环**：ΔE &lt; 0 时 u_E = 0，摆杆再没有阻力，
        /// 于是带着 E &gt; 2mgl 一直整圈转（台架实测 40 s 内 CH1 从 2000 涨到 181903 ≈ 75 圈）。
        /// 本字段给抽取一个大小的上限：`u_E = clamp(ExtractGain·ΔE, -EnergyExtractRatio·AMax, 0)`。
        /// </para>
        /// <para>
        /// **2026-11-21 第九轮从 0.20 放到 1.0**：r9 实测泵把能量灌到 3.0 J（目标 1.86 J）时，
        /// 0.2·AMax=20 rad/s² 的抽取权限（顶部再被淡出压到 4）根本追不上注入用掉的 100 rad/s²，
        /// 摆杆在顶部以 9~16 rad/s 高速循环、90 s 抓不住。
        /// 历史上 "对称抽取把摆幅打回底部" 的灾难是 **抽取与注入共用 Ke** 造成的：那时一点超调就
        /// `Ke·ΔE` 顶满抽取上限，摆臂被反推到底。现在 `ExtractGain=60` 独立且是比例律，
        /// 超调越大抽得越多、超调消失自动归零，不会 bang-bang；权限放到 1.0 只是把上限解开。
        /// 0 = 未配置（旧文件缺键），按 1.0 补齐；要退回弱抽取请显式填小值（如 0.2）。
        /// 载入时旧出厂值 0.2/0.3 会被一次性迁移到 1.0（见 TryResolveInvalidFields）。
        /// </para>
        /// </summary>
        public double EnergyExtractRatio;

        /// <summary>
        /// 抽取增益 [rad/s² per J]。与注入用的 <see cref="Ke"/> **解耦**。默认 **90**（第十轮）。
        /// <para>
        /// 为什么要解耦（2026-11-20 台架实测）：注入和抽取共用 Ke 时，Ke 为了让摆臂在整个爬升区间
        /// 顶在 AMax 上必须取得很大（实测 700），于是**一点点超调**就会让 `Ke·ΔE` 瞬间顶满抽取上限。
        /// 单独给抽取一个增益后，刹车量与实际超调成正比。
        /// </para>
        /// <para>
        /// **2026-11-21 第十轮从 60 提到 90**：目标附近的能量极限环是**双向**的 ——
        /// 注入侧过冲、抽取侧回得慢，两个半周期不对称就会留下 ±0.1 J 的残差。
        /// 90 与 <see cref="ConvergenceGainRadPerSec2PerJ"/> 取同一个值，使收敛段对称：
        /// 对 C=0.0112 是 deadbeat、对 C=0.005 是每半周期修正 45%（见 ConvergenceGain 的推导）。
        /// </para>
        /// 0 = 未配置，由控制层按默认 90 补齐。
        /// </summary>
        public double ExtractGain;

        /// <summary>
        /// 能量目标的死区（无量纲，相对 E*）。默认 **0.0025**（E* 的 0.25%，本机 ≈0.005 J）。
        /// <para>
        /// |ΔE| 小于 `EnergyDeadbandRatio·E*` 时 u_E = 0，注入与抽取都不动作：避免在 E* 附近
        /// 因 ω_est 噪声逐拍正负翻转，把摆臂抖成 2 kHz 的方波。
        /// **2026-11-21 第十轮从 0.005 缩到 0.0025**：捕获需要能量落在目标 ±0.005 J 内
        ///（掠顶 |ω|≤0.9 对应 E−2mgl ≤ 0.005 J），而 0.005 的死区本身就是 ±0.010 J，
        /// 比需求大一倍。缩小后 u_E 的抖幅只有 150×0.005=0.75 rad/s²（相对 AMax=100 可忽略）。
        /// 注意：能量**估计**噪声仍有 ±0.04~0.1 J（ω_est 决定），死区管的是控制律动作，
        /// 不是测量分辨率 —— 捕获判据用的是 u/ω，不依赖 E 的绝对精度。
        /// </para>
        /// 0 = 未配置（旧文件缺键），按 0.0025 补齐。
        /// </summary>
        public double EnergyDeadbandRatio;

        /// <summary>
        /// 收敛段注入增益 [rad/s² per J] = **90**（第十轮）。
        /// <para>
        /// 半周期能量增量 <c>ΔE_half = C·u_E</c>，其中
        /// <c>C = m·l·r·∫|ω·cosθ|dt</c>（沿半周期积分）。本机 m·l·r=0.002936，
        /// E=1.995 J 时数值积分得 ∫|ωcosθ|dt=3.80 rad·s、T_half=0.97 s
        /// ⇒ **C=0.0112 J per (rad/s²)**；由 r10 实测极限环反推的有效 C≈0.005（伺服相位滞后/
        /// 跟踪损耗把理想值打对折）。两者给出 deadbeat 增益 90~190。
        /// </para>
        /// <para>
        /// 取 **90**：对 C=0.0112 是 deadbeat（一个半周期消掉误差），对 C=0.005 是 45% 修正/半周期
        /// （0.15 J→0.005 J 约 5~6 个半周期，≈5 s）。**不超过 90**：C·K>1 就会每半周期过冲，
        /// 形成极限环；旧 Ke=700 的 C·K≈7.8，正是 r10 目标附近 E 在 2.12~2.36 来回的原因。
        /// </para>
        /// </summary>
        public const double ConvergenceGainRadPerSec2PerJ = 90.0;

        /// <summary>收敛段上限 [J]：|ΔE| ≤ 本值时用收敛增益（默认 0.15 J）。</summary>
        public const double ConvergenceBandLowJ = 0.15;

        /// <summary>爬升段起点 [J]：|ΔE| ≥ 本值时用配置的 Ke（默认 0.30 J，之间平滑过渡，防抖）。</summary>
        public const double ConvergenceBandHighJ = 0.30;

        /// <summary>
        /// 能量环积分的权限占 AMax 的比例 = **0.06**（AMax=100 时 6 rad/s²）。
        /// <para>
        /// 积分只补摩擦缺口：本机能量损耗约 0.03 J/s，半周期 0.97 s ⇒ 0.029 J/half，
        /// 按 C=0.0112 折算需要 **u_E≈2.6 rad/s²** 的稳态注入。取 6 rad/s²（2.3 倍裕量）。
        /// 旧值 `0.5·aPumpMax`（=50 rad/s²）在目标附近是一股不随 ΔE 收敛的常值注入
        /// （半个周期就能灌 0.56 J），是 ±0.1 J 极限环的第二个来源。
        /// </para>
        /// </summary>
        public const double IntegralAuthorityRatio = 0.06;

        /// <summary>
        /// 软中心带半径 [rad]。出厂默认 **20.0**（用户 2026-11-20 裁定）。|φ| 超出它时**暂停能量注入与泵**，
        /// 用满 AMax 权限主动回中；回到带内 50% 且 |φ̇| &lt; 2 rad/s 才恢复起摆。
        /// <para>
        /// 这是漂移的**第二道防线**：直流扣除（SwingUpController 里的 `_energyBias`）负责让摆臂不漂，
        /// 本字段负责「万一还是漂了，在撞软限位之前先收手」。判据不依赖软限位有多远：
        /// 用户现场的 counts 行程可能很大（本机 -55272..32061），等 |φ| 接近限位才收手等于没有保护。
        /// </para>
        /// <para>
        /// 取值依据：本机软限位换算约 [-17.4, 10.1] rad，3.6 rad 会把出厂摆臂行程提前锁死
        /// （实测 3.6 时起摆 58 s，20 时 13 s）。20 rad 仍明显小于两侧真实行程，保留回中保护，
        /// 又不限制正常的起摆往复。**0 = 未配置**（旧文件缺键）时按 20 补齐；
        /// 用户显式写入 3.6 必须原样保留，不当成「旧出厂值」替换（那是参数层对历史文件的一次性迁移语义）。
        /// </para>
        /// </summary>
        public double CenterBandRad;

        /// <summary>
        /// 往复泵加速度幅值 [rad/s²]，不是角度。0 = 关泵（**出厂默认就是关**）。
        /// <para>
        /// 泵是一条**开环**支路：固定频率的 sin 与摆杆相位没有任何锁定关系，只在 |ω| 很小时才放行。
        /// 修正后的能量项已经是闭环、自找相位的，泵再叠上去只会跟它抢拍，所以出厂关掉；
        /// 要留作对照实验就打开，日志/遥测会打**本拍实际施加量**（<c>PumpAccelRadPerSec2</c>），
        /// 不是这里的配置值。
        /// </para>
        /// <para>
        /// 历史教训：旧版把这一项当成「起摆驱动力」，日志恒打配置值 2.00，实测却从来没参与过
        /// （旧分支在 |ω| &lt; 0.30 时根本不计算泵项），排障时被误读成「泵在工作」。
        /// </para>
        /// </summary>
        public double PumpAmplitude;

        /// <summary>
        /// 往复泵频率 [Hz]。泵关着时这个值不起作用。
        /// 默认 1.4390 = ω_n/2π（等效摆长 0.12 m；设计值，不是台架实测）。
        /// 泵必须落在摆杆固有频率上才能持续注入能量，偏离太多等于白推。
        /// <para>
        /// **起摆开始时会用系统辨识的实测值覆盖它**：<see cref="SystemIdentifier.Identify"/> 给出
        /// <c>NaturalFreqHz</c> 且样本充分时，<c>PendulumEngine</c> 把本字段换成实测值并打日志说明来源；
        /// 辨识不可用则明确打「使用默认 ω_n（未辨识）」。这样参数文件里 <c>natural_freq_hz = 0</c>
        /// （从没辨识过）与「辨识过但没人回灌」两种状态在日志里一眼可辨。
        /// </para>
        /// </summary>
        public double PumpFrequencyHz;

        /// <summary>
        /// 往复泵相位偏移 [rad]。0 = 正弦从 0 起振；换泵序 / 现场微调相位时用。
        /// 只影响泵项 sin(2πft + φ0)，不改变能量注入项的符号门。
        /// </summary>
        public double PumpPhaseOffsetRad;

        /// <summary>
        /// 切换判据（= 到位判据）：|wrapPi(θ-π)| 上限 [rad]。默认 0.10 ≈ 5.7°。
        /// count 视角：摆杆 2400 count/rev 时 0.10 rad ≈ 38 count（0.15°/count），连续 <see cref="SwitchHoldCycles"/> 拍才算到位。
        /// <para>
        /// 取值依据：β=+0.0499（第五轮 19 拍遥测 + 脉冲标定 +0.048）；2026-11-21 权限放开后
        ///（AccMax=800、Ka=5000、Kad=300）离线三层验收：最大可救 ψ0=0.162(ω=0) / 0.123(ω=0.3) /
        /// 0.096(ω=0.5)；历史 19 步初值（旧权限下 0.205 s 崩）peak 0.0942、2 s 末 −0.0088。
        /// 捕获门取 **0.10 rad**：旧 120-pass 离线在 0.06~0.09 带内 10/10、中位 0.069，
        /// 0.069~0.089 的折返/ω≈0 被 0.162/0.123/0.096 的新包线完全覆盖。
        /// 0.05 是旧平衡权限（AccMax=150/Ka=900）下的保守值，且泵的单 pass 能量量子 ~0.017 J
        /// 根本产不出 0.03~0.05 的折返带；不要再收紧回去。
        /// </para>
        /// </summary>
        public double ThetaSwitchRad;

        /// <summary>
        /// 平衡可恢复包线上限 [rad]。2026-11-21 权限放开后（AccMax=800、Ka=5000、Kad=300）的
        /// 离线三层验收：最大可救 ψ0 = 0.162(ω=0) / 0.123(ω=0.3) / 0.096(ω=0.5)；
        /// 历史 19 步初值（旧权限下 0.205 s 崩）也救回（peak 0.0942、2 s 末 −0.0088）。
        /// 取 0.15 作包线，与 0.10 捕获门留约 1.5 倍余量。
        /// 参数文件里的旧值（0.28）由 IsValid 按本值拒收/回退，避免老文件把门重新开大。
        /// </summary>
        public const double VerifiedRecoverableThetaRad = 0.15;

        /// <summary>切换判据：|ω| 上限 [rad/s]。默认 3.0。</summary>
        public double OmegaSwitchRadPerSec;

        /// <summary>切换判据：|φ| 上限 [rad]（起始位置 ≡ 坐标 0）。默认 0.6，摆臂别在极限位。</summary>
        public double PhiSwitchRad;

        // ---- 捕获门联合判据（默认 0 = 未配置，由 SwingUpController 按 CreateDefault 的标定值补齐） ----
        // 2026-11-21 权限放开后（AccMax=800、Ka=5000、Kad=300）离线三层验收：历史 19 步初值
        //（旧权限下 0.205 s 崩）peak 0.0942、2 s 末 −0.0088；最大可救 ψ0=0.162(ω=0)/
        // 0.123(ω=0.3)/0.096(ω=0.5)。捕获门取 0.10（旧 120-pass 10/10 的 0.06~0.09 带），
        // 对 0.162 的包线留约 1.6 倍余量。
        // 可恢复集不是矩形，下面这组把「摆杆状态」和「摆臂还剩多少行程」放在同一个预算里判。

        /// <summary>
        /// 捕获门：不稳定模态幅值上限 [rad]。默认 **0.12**，硬上限
        /// <see cref="VerifiedMaxCaptureModeAmplitudeRad"/>（同值，调不过去）。
        /// <para>
        /// **2026-11-21 第二十一轮裁定**：直接取平衡包线本身 **|u| ≤ 0.12 rad**，不再留余量。
        /// 交棒失败由真实平衡与 S2 跟随误差如实裁决，不再自动回起摆或改能量目标；
        /// 放进去救不回来就如实报警，不再用重试掩盖。
        /// </para>
        /// <para>
        /// 该判据用 <c>u = ψ + (ω − CalibrationSign·(r/l)·φ̇)/ω_n</c>，**r/l 唯一来源 =
        /// <see cref="VerifiedCaptureCouplingRatioRad"/>（0.048，标定值）**；48 ms 交棒预测里的
        /// 耦合项必须用同一个值。`limits.radius_ratio`（台架 0.24）只归平衡律的 plant 模型，
        /// **不要**再拿它算捕获门（两处口径不一致会让「门」和「被比较的包线」对不上）。
        /// </para>
        /// </summary>
        public double CaptureModeAmplitudeMaxRad;

        /// <summary>
        /// 捕获门（u 判据）与 48 ms 交棒预测使用的几何耦合比 r/l = **0.048**（常量）。
        /// <para>
        /// **2026-11-21 第十一轮实证裁定**：r11 第一次交棒状态 ψ=+0.115、ω=+0.27、φ̇=+3.50，
        /// 用 0.24 算 u=0.052（放行），用 0.048 算 u=0.126（超 0.12 包线，本该拒绝）——
        /// 放进去 0.26 s 就 FollowingError（臂 0.24 s 内从 +0.77 甩到 −6.49）。
        /// 0.24 让门对「臂带着大 φ̇ 进门」视而不见，这比任何样本统计都硬。
        /// 来源：脉冲标定 +0.048 / 24 拍拟合 +0.057（见 PendulumEngine.CalibrationSign）。
        /// </para>
        /// <para>
        /// **不要再从 `limits.radius_ratio` 取**：那个值（0.24）仍被平衡律的 plant 模型使用，
        /// 两条线各自标定；捕获侧只认本常量。48 ms 预测的 plant 项也必须用它 ——
        /// 用 0.24 会把平衡律的修正效果放大 5 倍，预测出 u_pred=0.03 的「看起来会收敛」，
        /// 正是 r11 那个坏状态被放行的原因。
        /// </para>
        /// </summary>
        public const double VerifiedCaptureCouplingRatioRad = 0.048;

        /// <summary>
        /// 交棒时摆杆角速度的**硬上限** [rad/s] = **1.2**（常量，不可调）。
        /// <para>
        /// 平衡包线在 ω 方向约 ±1 rad/s（ψ=0 时 |u|≤0.12 ⇒ |ω|≤1.09）。第二十一轮把本上限
        /// 从 1.0 放到 1.2：略超包线，让「到了顶就能切」；交棒失败由真实平衡与 S2 裁决，
        /// 不再自动回起摆。
        /// **2026-11-21 第九轮**：它只作为 <see cref="CaptureCommitOmegaRadPerSec"/> 的上限，
        /// 不再单独出现在硬门里 —— 预测状态上的 ψ/ω 由 u 包线联合约束（u 已编码两者），
        /// 再单加 `|ψ_pred|≤0.10`、`|ω_pred|≤1.0` 等于同一个约束数三遍（r8 饿死的主因）。
        /// </para>
        /// </summary>
        public const double CaptureHandoffOmegaMaxRadPerSec = 1.2;

        /// <summary>
        /// commit 判据的预测外推拍数（按控制周期 dt）。默认 **24 拍**（2 ms 周期 = 48 ms）。
        /// 0 = 关闭预测（旧行为）。门在 (ψ_pred, ω_pred) 上算 u，而不是用决策拍状态。
        /// <para>
        /// 为什么需要预测：交棒决策与平衡律第一拍之间还有 1~5 个控制周期，且 `[SWING]` 日志
        /// 只在 |ψ|&lt;0.5 时 100 Hz（10 ms）采样，最后一次日志与真正的 decision 拍可差 5 拍。
        /// 用 48 ms 外推判 u，等于要求「按平衡律收敛 48 ms 后仍在包线内」，把这段时序不确定性
        /// 收进判据；实测（2026-11-21）决策拍 |u| 0.002~0.10，外推后单调减小。
        /// </para>
        /// </summary>
        public double CapturePredictCycles;

        /// <summary>
        /// 旧参数文件的兼容字段，**不参与交棒预测与平衡控制**。
        /// 平衡增益唯一权威是 <see cref="PendulumEngine"/> 的 BalanceKPsi / BalanceKOmega / BalanceKPhiDot；
        /// 保留字段只为旧 JSON / GUI 能继续读取，保存后不再影响控制。
        /// </summary>
        public double BalanceKa;
        public double BalanceKad;

        /// <summary>旧参数兼容字段；平衡预测不读臂位置增益，臂速阻尼统一读 PendulumEngine.BalanceKPhiDot。</summary>
        public double BalanceKPhi;
        public double BalanceKPhiDot;

        /// <summary>
        /// 捕获门：每 1 rad 模态幅值需要多少摆臂行程去吸收 [rad/rad]。默认 12.3（离线标定）。
        /// 实测摆臂参考行程与 |u| 近似线性（0.06→0.74 rad、0.10→1.23 rad、0.14→1.76 rad）。
        /// </summary>
        public double CaptureStrokePerModeRad;

        /// <summary>捕获门：行程预算的固定裕量 [rad]。默认 0.05，用来吸收模型误差与 ω 估计滞后。</summary>
        public double CaptureStrokeMarginRad;

        /// <summary>
        /// 捕获门：摆臂已经在朝限位方向运动时，每 1 rad/s 额外占用的行程 [rad/(rad/s)]。默认 0.5。
        /// 反向速度要先把臂刹住，实测每 rad/s 多走 0.3~0.5 rad（越靠近边界越陡）；
        /// 按线性折算比 v²/2a 在大速度时更保守。
        /// </summary>
        public double CaptureAdverseRateStrokeSec;

        /// <summary>
        /// 摆臂速度上限 [rad/s]，两个方向都算。默认 4.0，**硬上限 6.0**。
        /// 臂速过大时 aArm=Kφ̇·φ̇ 项会压过摆杆的加速度需求（实测 |u| 很小、|φ̇|=6 时必然失控）。
        /// <para>
        /// **2026-11-21 第二十一轮：本项已退出交棒硬门**，只作为
        /// <see cref="CaptureCommitArmRateRadPerSec"/>（预捕获限速刹车）的上限与参数合法性上界。
        /// 交棒改由 u 包线单独判定：臂在泵的驱动下天然是快的，再单列臂速门就等于「到了顶也不让切」。
        /// </para>
        /// <para>
        /// 历史（判据时代）：2026-11-20 台架把本项从 4.0 抬到 15 是「起摆从 180 s 降到 8.64 s」
        /// 的直接原因，但同一组参数下平衡接管后 0.4 s 就掉（ψ 从 ≤0.1 涨到 0.479）；4.0 是离线
        /// 细扫标定的安全值，6.0 已实测失控，所以本项只允许收在 [0.5, 6.0]。
        /// 想加快起摆请改能量律（直流扣除 / 抽取 / 中心带），**不要**再抬这个门。
        /// </para>
        /// </summary>
        public double CaptureArmRateLimitRadPerSec;

        /// <summary>
        /// 捕获门：摆杆固有角频率 ω_n = √(mgl/J_eq) [rad/s]，把角速度折算成等价摆角。
        /// 默认 9.0416（等效摆长 0.12 m 时 √(9.81/0.12)；台架实测自由摆动 1.422 Hz ⇒ 8.93 rad/s，差 1.2%）。
        /// 换摆杆必须同步改它，并与 <see cref="SafetyLimits.JEq"/> / <see cref="SafetyLimits.Mgl"/> 的比值一致：
        /// 那两项的出厂默认已按 ω_n² = 81.75 配好，三者对不上时先把 J_eq / mgl 配成 m·l² 与 m·g·l。
        /// </summary>
        public double CapturePendulumRateRadPerSec;

        /// <summary>
        /// 预捕获回中窗口 [rad]：|ψ| 进入该窗口、能量接近捕获窗且**本拍刹车会抽取能量**时，
        /// **不等捕获门满足**，先停能量泵、把摆臂速度压回 <see cref="CaptureCommitArmRateRadPerSec"/>
        /// 以内（**限速刹车，不回中**：够低就停止出力，不把臂刹到 0）。默认 0.60（约 34°）。
        /// <para>
        /// 2026-11-20 台架失败样本（2 ms 周期）的直接原因：能量恰在掠顶那一拍才进入捕获窗，
        /// 旧 `Preparing` 只从窗口内部才开始回中，此时摆臂仍以 −3 rad/s 往家外跑；
        /// 按 aPumpMax=40 rad/s² 刹停要 75 ms，而捕获门只连续保持了 13 拍（26 ms）就被臂速项打断。
        /// 把回中窗口提前到 |ψ| ≤ 0.60 后，摆杆从 0.60 rad 走到 0.10 rad（ω≈2~3 rad/s 时约
        /// 0.15~0.25 s）足够先把臂速刹到 0 并开始回中，捕获门不再与被要求刹车的臂速互相等待。
        /// </para>
        /// <para>
        /// **刹车相位必须符号感知，不能写死 ω&lt;0**：制动对摆杆的能量作用
        /// <c>dE/dt = −ArmCouplingSign·m·l·r·θ̇·cosθ·φ̈</c>；制动取 φ̈=−sign(φ̇)·A 后，
        /// **抽取（dE/dt&lt;0）当且仅当 ArmCouplingSign·ω·cosθ·sign(φ̇) &lt; 0**。
        /// 本机 ArmCouplingSign=+1（2026-11-21 台架反解 β≈+0.20；11-16 独立测量 β≈+0.17），
        /// 外跑时抽取相位是**下行侧**（ω&lt;0）。旧的 S=−1/上行侧结论作废；2026-11-20 记录的
        ///「下落侧刹车 dE/dt=+1.10 J/s」是在旧判据下测的，新符号下必须重测，不能再拿来推相位。
        /// 判定为注入的半周期**完全不刹车**（不做半刹车，半刹车仍是注入）。
        /// 上限 1.5 rad：再大就会整个半周期停泵，起摆能量爬不上去；下限 0.05 rad 保证窗口
        /// 至少覆盖捕获角 θ_switch(0.10) 之外的接近段。0 = 未配置（按 0.60 补齐）。
        /// </para>
        /// </summary>
        public double CapturePrepareApproachRad;

        /// <summary>
        /// 预捕获能量带宽（无量纲，相对 2·mgl）。默认 0.015（mgl=1 时约 ±0.03 J）。
        /// <para>
        /// 回中窗口的能量判据取 E ∈ [gateLow − ratio·2mgl, gateHigh + ratio·2mgl]，
        /// 其中 gateLow = mgl(1+cos θ_switch)、gateHigh = 2mgl + ½J(ω_n·u_max)²。
        /// 台架能量在 E* 附近有 ±0.05 J 的波动；只认窗口内部会出现「能量从上方跌回窗口的那一拍
        /// 才开始回中」，而那时摆杆已经越顶、摆臂来不及刹（11-20 日志 t=17.00 正是这种时序）。
        /// 留 ±0.03 J 的预窗口后，能量接近目标（|E−E*| ≲ 1.5%）时就会提前收臂。
        /// </para>
        /// <para>上限 0.1：再大等于长期停泵，能量到不了 E*。0 = 未配置（按 0.015 补齐）。</para>
        /// </summary>
        public double CapturePrepareEnergyMarginRatio;

        /// <summary>
        /// 捕获准备段的限速刹车门限 [rad/s]。默认 **3.0**，运行时自动收紧到
        /// <see cref="CaptureArmRateLimitRadPerSec"/> 以内。
        /// <para>
        /// **2026-11-21 第二十一轮：本项已退出交棒判据。** 交棒只认 u 包线 + |ω_est|≤commitOmega；
        /// 臂在泵的驱动下天然是快的，再拿 |φ̇|≤3.0 当门就是「到了顶也不让切」的直接原因。
        /// 本值现在只服务 <c>Preparing</c>（预捕获限速刹车：把超过它的臂速压回门限，不回中），
        /// 以及参数层与 <see cref="CaptureArmRateLimitRadPerSec"/> 的一致性校验。
        /// </para>
        /// <para>
        /// 历史（判据时代）：旧网格（S=−1、β=−2.58 以及 β=0.24）已作废。2026-11-21 第五轮实测
        /// β=+0.0499（脉冲标定 +0.048）；权限放开后（AccMax=800、Ka=5000）离线验收最大可救 ψ0 为
        /// 0.162(ω=0)/0.123(ω=0.3)/0.096(ω=0.5)。
        /// </para>
        /// <para>
        /// 取 3.0 而不是 4.0：第一个失败族出现在 |φ̇|=4.0，留 25% 裕量；换摆杆 / 重装摆臂后
        /// 重跑同一网格再定。0 = 未配置（按 3.0 补齐）；合法区间 (0, 6.0] rad/s。
        /// </para>
        /// <para>
        /// **与 radius_ratio 的关系**：若参数文件已把 `limits.radius_ratio` 配成实测 |β|≈2.58，
        /// 硬门的 u≤0.16 本身就会拒绝 |φ̇|=4 的贴边状态（2865 个臂速网格状态 0 失败），本上限
        /// 只是冗余保险；若 radius_ratio 仍停在旧的 0.24 或 0（未标定），硬门会低估臂速动量，
        /// 这时本上限是**唯一**挡住该失败族的东西。两种情况都必须保留。
        /// </para>
        /// </summary>
        public double CaptureCommitArmRateRadPerSec;

        /// <summary>
        /// 快速提交的摆杆角速度绝对值上限 [rad/s]。默认 **1.2**（2026-11-21 第二十一轮），
        /// 运行时自动收紧到 <see cref="OmegaSwitchRadPerSec"/> 与
        /// <see cref="CaptureHandoffOmegaMaxRadPerSec"/> 以内。
        /// <para>
        /// **为什么是 1.2**：平衡包线 |u|≤0.12 在 ψ=0 时对应 |ω| ≤ 0.12·ω_n = **1.08 rad/s**；
        /// 1.2 略超包线，是有意为之 —— 交棒后由真实平衡与 S2 裁决；不再用自动回起摆或重试掩盖。
        /// 旧的 0.5 是凭空多收一倍（第九轮 0.5 → 1.0），本项现在只兜「ψ 小、ω 大」的穿越状态。
        /// </para>
        /// <para>本项是**当前拍、ω_est（40 ms 最小二乘）**的物理上限，不是 u 的重复项：
        /// 预测状态上的 ψ/ω 联合约束交给 u 包线（见 CaptureGateAllows）。</para>
        /// <para>0 = 未配置（按 1.2 补齐）；合法区间 (0, 10] rad/s。</para>
        /// </summary>
        public double CaptureCommitOmegaRadPerSec;

        /// <summary>
        /// 快速提交需要累计的净满足拍数 N_cc。默认 **5 拍**（2026-11-21 第二十一轮；
        /// 控制周期 1 ms 时约 5 ms，2 ms 时约 10 ms）。
        /// <para>
        /// 快速通道只要求「硬捕获门(u 包线) + |ω_est|≤1.2」的近顶端驻留净满足 5 拍
        ///（用千分之一拍累加、远离顶端冻结），只用来滤掉单拍边界抖动。旧值 20 拍会把
        /// 「掠顶窗口只有 14~48 ms」的台架窗口再削掉一截，属于「为了凑拍数错过窗口」。
        /// 慢通道（<see cref="SwitchHoldCycles"/>=60 净拍）仍然保留，负责「持续接近可恢复集」的语义。
        /// 两条通道的提交条件都必须落在提交带内。
        /// </para>
        /// <para>上限 200 拍：再大等于退回慢通道。</para>
        /// </summary>
        public int CaptureCommitHoldCycles;

        /// <summary>
        /// 近顶端驻留计数的漏桶衰减量，单位 **千分之一拍/拍（milli-cycle per cycle）**。默认 **2**（= 0.002 拍/拍）；
        /// 0 = 未配置（按 2 补齐）。合法区间 [0, 10]（即 0~0.010 拍/拍）。
        /// <para>
        /// **为什么必须是千分之一拍而不是整数拍**（2026-11-20 台架实测）：
        /// 能量窗宽 gateHigh−gateLow ≈ 0.0178 J，E 变化率 1.1~1.3 J/s
        /// ⇒ 单次掠顶门只开 0.0178/1.2 ≈ **14~16 ms**（1 ms 周期 = 14~16 拍）。
        /// 两次掠顶的间隔是 1.5~3 s = **1500~3000 拍**。要跨周期累积，每拍衰减必须
        /// &lt; 窗口/间隔 ≈ 14/3000 = **0.0047 拍**；整数「−1 拍/拍」一次间隔就扣掉
        /// 1500~3000 拍（是窗口的 100~200 倍），数学上不可能攒到 60。旧实现正是因此
        /// 卡在 holdSlowMax=48/60、倒计时 20→12→20 反复。
        /// </para>
        /// <para>
        /// 现在 k=2 milli = 0.002 拍/拍，比 0.0047 上限留 2.3 倍裕量；同时
        /// |ψ| &gt; CapturePrepareApproachRad（0.6）时计数**冻结**，摆动间隔期不扣——
        /// 跨掠顶累积与摆动周期长短无关，扣减只用于同一掠顶内的边界抖动。
        /// 台架实测有效窗口 14~48 ms/次 ⇒ 60 拍目标需 2~5 个掠顶周期。
        /// </para>
        /// </summary>
        public int SwitchHoldDecayPerCycle;

        /// <summary>漏桶衰减量的合法上界（千分之一拍/拍）。超过它等于取消跨掠顶累积语义。</summary>
        public const int MaxSwitchHoldDecayPerCycle = 10;

        /// <summary>快速提交拍数的合法上界。超过它等于退回慢通道，窗口内不可能提交。</summary>
        public const int MaxCaptureCommitHoldCycles = 200;

        /// <summary>预捕获回中窗口的合法上界 [rad]。再大就会整个下落半周期停泵。</summary>
        public const double MaxCapturePrepareApproachRad = 1.5;

        /// <summary>快速提交摆杆角速度上限的绝对上界 [rad/s]。超过它粗筛早已失效，提交带没有意义。</summary>
        public const double MaxCaptureCommitOmegaRadPerSec = 10.0;

        /// <summary>
        /// 捕获门模态幅值的**硬上限** [rad] = **0.12**（不可调，参数超过它一律由
        /// <see cref="IsValid"/> 拒收、载入时由 <see cref="TryResolveInvalidFields"/> 回落默认值）。
        /// <para>
        /// 依据：平衡恢复包线实测 **|u| ≤ 0.12 rad**，本上限直接取包线本身、不再留余量
        /// （第二十一轮裁定）。留余量的代价是「到了顶也不让切」——旧值 0.10 把本来能接住的
        /// 掠顶状态挡在门外，起摆只能继续泵、再掠一次顶。交棒失败由真实平衡与 S2 如实裁决，
        /// 不再自动回起摆。细扫里 |u|=0.163 已实测失控，所以 0.12 仍是绝对上界，
        /// **不要再放宽**（第六轮 0.08 → 台架 1/5、第九轮 0.10 → 本机实测可用，均记录在案）。
        /// </para>
        /// </summary>
        public const double VerifiedMaxCaptureModeAmplitudeRad = 0.12;

        /// <summary>
        /// 平衡包的模态幅值包线 [rad] = 0.12（台架实测，仅作判据依据的文档常量，不参与控制）。
        /// 第二十一轮起捕获门 <see cref="VerifiedMaxCaptureModeAmplitudeRad"/> 直接等于包线本身。
        /// </summary>
        public const double VerifiedBalanceEnvelopeModeAmplitudeRad = 0.12;

        /// <summary>
        /// 近直立淡出的**绝对加速度下限** [rad/s²] = 20.0。
        /// <para>
        /// 淡出权重 `weight` 的结构下限 0.35 是「泵权限 54 rad/s²」时代定的：0.35×54≈19 rad/s²。
        /// 泵权限提到 150 后，若仍用 0.35，顶部注入会跟着 2.8 倍化，摆臂掠顶速度会冲出捕获门
        /// |φ̇|≤3（台架历史把门抬到 15 时平衡 0.4 s 掉摆就是这条通道）。
        /// 因此生效下限取 <c>min(ThetaFadeFloor, 20/aPumpMax)</c>：
        /// 现在（aPumpMax=54）仍是 0.37≈0.35，行为不变；泵变大时顶部绝对权限稳定在约 20 rad/s²，
        /// 中下部（weight=1）才享受全部新权限。
        /// </para>
        /// </summary>
        public const double FadeFloorAccelRadPerSec2 = 20.0;

        /// <summary>顶部条件助推（FadeBoost）的绝对加速度上限 [rad/s²] = 40.0（= 2×淡出下限）。</summary>
        public const double FadeBoostAccelCeilingRadPerSec2 = 40.0;

        // ---- 已知历史出厂起摆整定块（2026-11-20 ~ 11-21 台架一直在用）----
        // Ke=700 / AMax=80 / KPhi=8 / KPhiDot=1.5 + 泵关。这组值不是随便填的：它是 11-20 起摆
        // 可靠性修复后的整定结果，但 AMax=80 在当时的 0.66·ω_n 包线下实际只生效 54 rad/s²，
        // 导致能量爬升 0.03~0.07 J/s、从静止到 E*≈2 J 要 30~90 s。2026-11-21 提速裁定把
        // AMax 的出厂值提到 150（臂实测 2000 rad/s²）。命中该指纹时一次性迁移，写进加载报告；
        // 用户只要动过其中任何一项（或泵幅非 0），就不算这组旧出厂值，原样保留。
        /// <summary>历史出厂块指纹：能量增益。</summary>
        public const double LegacySlowPumpKe = 700.0;
        /// <summary>历史出厂块指纹：泵加速度权限 [rad/s²]。</summary>
        public const double LegacySlowPumpAMaxRadPerSec2 = 80.0;
        /// <summary>历史出厂块指纹：起点回中增益。</summary>
        public const double LegacySlowPumpKPhi = 8.0;
        /// <summary>历史出厂块指纹：起点阻尼增益。</summary>
        public const double LegacySlowPumpKPhiDot = 1.5;

        /// <summary>历史出厂提交带 ω 上限 [rad/s]：0.5（2026-11-20~21），第九轮迁移到 1.0。</summary>
        public const double LegacyCommitOmegaRadPerSec = 0.5;

        /// <summary>
        /// 第九轮出厂提交带 ω 上限 [rad/s]：1.0（2026-11-21 第九轮 ~ 第二十轮）。
        /// 第二十一轮放到 1.2，命中该值时一次性迁移（用户自己填的 1.0 与旧出厂值不可区分，同口径处理）。
        /// </summary>
        public const double LegacyCommitOmegaRadPerSecV2 = 1.0;

        /// <summary>
        /// 历史出厂捕获门幅值上限 [rad]：0.10（第七轮 ~ 第二十轮，留 20% 余量）。
        /// 第二十一轮改为 0.12（= 平衡包线本身），命中该值时一次性迁移。
        /// </summary>
        public const double LegacyCaptureModeAmplitudeMaxRad = 0.10;

        /// <summary>
        /// 历史出厂快速提交净拍数：20（第五轮 ~ 第二十轮）。第二十一轮降到 5 拍，命中该值时一次性迁移。
        /// </summary>
        public const int LegacyCaptureCommitHoldCycles = 20;

        /// <summary>
        /// 捕获门臂速的实测安全上界 [rad/s] = 6.0。4.0 是离线细扫标定值，6.0 的样本已实测失控；
        /// 2026-11-20 台架把门抬到 15 直接导致平衡接管 0.4 s 掉摆（推导见
        /// <see cref="CaptureArmRateLimitRadPerSec"/> 的字段注释），故本项不再允许超过 6.0。
        /// </summary>
        public const double VerifiedMaxCaptureArmRateRadPerSec = 6.0;

        /// <summary>
        /// 慢通道（硬捕获门）需要累计的净满足拍数 N_c。默认 60 拍 = 30 ms @2 kHz（2 ms 周期时 120 ms）。
        /// <para>
        /// 计数是**近顶端驻留**的净拍数：满足 +1 拍、不满足 −k 千分之一拍（见
        /// <see cref="SwitchHoldDecayPerCycle"/>），|ψ| &gt; CapturePrepareApproachRad 时冻结。
        /// 所以它是「净拍数」而不是严格连续拍数。慢通道**只在提交带
        ///（<see cref="CaptureCommitOmegaRadPerSec"/> 的 |ω_est| 上限）内才放行**；
        /// 掠顶短窗内的交棒由 <see cref="CaptureCommitHoldCycles"/> 的快速通道负责，
        /// 本项保留「持续接近可恢复集」的语义。
        /// </para>
        /// </summary>
        public int SwitchHoldCycles;

        /// <summary>
        /// 起摆超时 [ms]。超时进 Fault(SwingUpTimeout)。默认 180000（3 min）。
        /// <para>
        /// 这个数只决定「多久算失败」，不改变控制律。本机实测从静止爬到捕获窗要 150 s 量级
        /// （2026-11-16：147 s 到 E=1.86 J，E* = 2.1 J），旧的 30 s 必然误判。
        /// 台架换摆杆 / 换耦合后按 <c>[SWING]</c> 里 E(t) 的斜率重估，不要靠放宽切换判据凑成功。
        /// </para>
        /// </summary>
        public int TimeoutMs;

        // ---- 起动微扰 / ω 估计 ----
        // 旧参数文件里没有这些键，读出来是 0；0 一律视为「未配置」，由 SwingUpController 按
        // CreateDefault 的默认值补齐。现场不需要为了跑起来手工填它们。

        /// <summary>
        /// 起动微扰的摆臂加速度幅值 [rad/s²]。默认 5.0。
        /// <para>
        /// 能量律 dE/dt ∝ −θ̇·cosθ·φ̈ 在 ω ≡ 0 处恒为 0，是数学上的鞍点：严格静止时任何符号门都给不出方向。
        /// 台架噪声虽然迟早会把它推离死点，但方向随机、起步慢；这里在起摆最开始的
        /// <see cref="BootstrapSec"/> 秒里给一段**确定方向**的小加速度，把对称打破后立刻交给能量律。
        /// 它不依赖摆杆固有频率，也不需要相位，因此换摆杆不用重标。
        /// 取值要够把摆杆推出编码器噪声（2400 count/rev 时 1 count ≈ 0.15°），又不能大到把摆臂甩出窗口。
        /// </para>
        /// </summary>
        public double BootstrapAccelRadPerSec2;

        /// <summary>起动微扰持续时长 [s]。默认 0.25。到时自动退出，之后完全是闭环能量律。</summary>
        public double BootstrapSec;

        /// <summary>
        /// 能量律相位门的软符号宽度 [rad/s]。默认 0.10。
        /// <para>
        /// 相位门取 x/√(x²+ε²)，x = ω·cosθ：|ω| ≫ ε 时近似 ±1（等于经典 bang-bang 能量法），
        /// |ω| ≪ ε 时按比例收小，避免 ω≈0 时把采样噪声放大成摆臂抖动。
        /// **它不是起摆闸门**：这里没有「|ω| 不够就不算能量」的分支，任何非零 ω 都会被同向放大。
        /// 旧版的 0.30 rad/s 硬闸门就是死锁根源 —— 抖速把摆幅顶到刚好 0.30 附近，能量法又更弱，永远交不出去。
        /// </para>
        /// </summary>
        public double EnergySoftOmegaRadPerSec;

        /// <summary>ω 最小二乘估计窗 [ms]。默认 40。窗越长噪声越小、滞后越大。</summary>
        public double OmegaWindowMs;

        /// <summary>增益调度：离直立近于它时泵注入开始淡出 [rad]。默认 1.00。</summary>
        public double ThetaFadeStartRad;

        /// <summary>增益调度：离直立近于它时泵注入降到 <see cref="ThetaFadeFloor"/> [rad]。默认 0.45。</summary>
        public double ThetaFadeEndRad;

        /// <summary>
        /// 贴合直立时的注入下限（0~1）。默认 **0.35**，合法下界 <see cref="MinThetaFadeFloor"/>。
        /// <para>
        /// 2026-11-20 可靠性修正：旧默认 0.15 让摆杆在 |ψ| &lt; 0.45 的最后一段只剩 8 rad/s² 的注入权限，
        /// 泵在整个周期上的平均注入被砍到与摩擦打平 —— 能量停在 E*−0.02 J，折返点 ψ≈0.16 rad，
        /// 捕获门 |ψ| ≤ 0.10 永远差几毫弧度（台架表现为 E 停在 1.5~1.6 J、60 s 超时）。
        /// 下限取 0.35：顶部仍有约 19 rad/s²（aPumpMax≈54 时）能把能量推过摩擦，
        /// 同时摆臂在顶部的速度仍在捕获门 |φ̇| ≤ 4 rad/s 的余量内（离线 20 组初始条件 20/20）。
        /// 低于 0.35 时控制层会按 <see cref="MinThetaFadeFloor"/> 收紧，并在参数载入时如实报告替换。
        /// </para>
        /// <para>
        /// 控制层另有**条件式顶部助推**（不是本参数的可调值）：|ΔE|&gt;0.05 J 且 |ψ|&gt;0.30 rad 时
        /// 临时把淡出 floor 抬到 0.70，|ΔE|≤0.05 或 |ψ|≤0.30 立即恢复本参数值；0.06~0.09 折返带
        /// 不触发。用于救低能量净功停滞（折返 0.41 rad 时有效权限仅 0.35×aPump），不影响顶部速度。
        /// </para>
        /// </summary>
        public double ThetaFadeFloor;

        /// <summary>
        /// 近直立淡出下限的合法下界。低于它时泵在顶部推不过摩擦，能量会在捕获窗前形成平台
        /// （离线与台架都能复现）。这是控制律的结构性下限，不是可调余量。
        /// </summary>
        public const double MinThetaFadeFloor = 0.35;

        /// <summary>
        /// 能量裕量 ε 的合法下界。见 <see cref="TargetEnergyGain"/>：ε 太小 ⇒ 摆杆折返点离直立太远，
        /// |ψ| 永远进不了 θ_switch；−0.02 对应折返点 ψ ≈ 0.28 rad，已明显超出捕获包线，再小就是无谓空转。
        /// </summary>
        public const double MinTargetEnergyGain = -0.02;

        /// <summary>
        /// 能量裕量 ε 的合法上界。ε = 0.02 时 E* 仍低于分界线 0.02·2mgl，到顶速度 ≈ 2.6 rad/s；
        /// 再大就必然过不了 |ω| ≤ 3.0 与捕获门的模态幅值预算，属于"明知抓不住还往上冲"。
        /// </summary>
        public const double MaxTargetEnergyGain = 0.02;

        /// <summary>
        /// 能量项 Ke 的归一化软饱和强度系数 k（无量纲，机制常量，默认 **1.0**）。
        /// <para>
        /// 软化律（归一化到执行权限，而不是绑一个 ΔE_c）：
        /// <code>
        /// x  = ke·ΔE / aPumpMax          // 归一化到饱和点
        /// uE = aPumpMax · x / (1 + k·|x|) // k=1 时渐近 aPumpMax；k&lt;1 更接近线性
        /// </code>
        /// 为什么用这个形式（2026-11-20 定）：
        /// ① **小信号增益完全不变**：ΔE→0 时 uE ≈ ke·ΔE，近目标精调行为与旧律一致；
        /// ② **渐近值永远是 aPumpMax**，不再是 ke·ΔE_c 这种与配置强相关的数——
        ///    旧 ΔE_c 形式在 ke=60/AMax=40 时渐近只有 3 rad/s²，会把默认配置的起摆拖死；
        /// ③ 去掉的是硬钳位的**拐点**：旧律 ΔE &gt; aPumpMax/ke（台架 0.077 J）就撞死，
        ///    只剩 bang-bang；软饱和无拐点，E* 附近不再靠满推/关断来回打。
        /// </para>
        /// <para>
        /// k 是强度旋钮：k=1 为标准软饱和；离线 MC 若显示成功率下降，减小 k（k&lt;1 更接近线性、
        /// 软化更弱），不要回去改 ΔE_c。默认 1.0。
        /// </para>
        /// </summary>
        public const double EnergySofteningStrength = 1.0;

        /// <summary>校验一组起摆参数。失败时 reason 写清哪一项不合法。</summary>
        public bool IsValid(out string reason)
        {
            // 泵是可选的：PumpAmplitude = 0 就是「关泵」，是合法的出厂配置。
            // （旧版把「泵幅 0」当成未初始化整组拒收，导致只想关泵的现场连其它起摆参数一起丢。）
            if (!ControlMath.IsFinite(PumpAmplitude) || PumpAmplitude < 0)
            {
                reason = "PumpAmplitude 必须是 ≥0 的有限数，单位 rad/s²（往复泵加速度幅值，不是角度）。0 = 关泵。";
                return false;
            }
            if (!ControlMath.IsFinite(TargetEnergyGain) ||
                TargetEnergyGain < MinTargetEnergyGain || TargetEnergyGain > MaxTargetEnergyGain)
            {
                reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "TargetEnergyGain（能量裕量 ε）必须在 [{0:F4}, {1:F2}] 内且有限：读到的值是 {2:G6}。"
                    + "E* = 2·mgl·(1+ε)，ε 太大 ⇒ 摆杆到顶还带着速度（ε=0.05 时 ω≈4 rad/s），"
                    + "既过不了 |ω| ≤ ω_switch 也过不了捕获门；ε 太小 ⇒ 永远爬不到 |ψ| ≤ θ_switch。"
                    + "出厂 -0.002（见字段注释里的推导）。",
                    MinTargetEnergyGain, MaxTargetEnergyGain, TargetEnergyGain);
                return false;
            }
            if (!ControlMath.IsFinite(Ke) || Ke <= 0)
            {
                reason = "Ke 必须是 >0 的有限数，单位 rad/s² per J。";
                return false;
            }
            if (!ControlMath.IsFinite(AMax) ||
                AMax < MinSwingAMaxRadPerSec2 || AMax > MaxSwingAMaxRadPerSec2)
            {
                reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "AMax（起摆泵加速度权限）必须在 [{0:F0}, {1:F0}] rad/s² 内且有限：读到的值是 {2:G6}。"
                    + "台架臂实测 2000 rad/s² 无失步；第八轮折中值 100（150 起摆 1/7，80 慢但成功率高），"
                    + "低于 {0:F0} 没有实测依据。",
                    MinSwingAMaxRadPerSec2, MaxSwingAMaxRadPerSec2, AMax);
                return false;
            }
            if (!ControlMath.IsFinite(KPhi) || KPhi < 0)
            {
                reason = "KPhi 必须是 ≥0 的有限数，单位 1/s²。";
                return false;
            }
            if (!ControlMath.IsFinite(KPhiDot) || KPhiDot < 0)
            {
                reason = "KPhiDot 必须是 ≥0 的有限数，单位 1/s。";
                return false;
            }
            if (!ControlMath.IsFinite(PumpFrequencyHz) || PumpFrequencyHz < 0)
            {
                reason = "PumpFrequencyHz 必须是 ≥0 的有限数，单位 Hz。";
                return false;
            }
            if (!ControlMath.IsFinite(PumpPhaseOffsetRad))
            {
                reason = "PumpPhaseOffsetRad 必须是有限数，单位 rad。";
                return false;
            }
            if (!ControlMath.IsFinite(ThetaSwitchRad) || ThetaSwitchRad <= 0)
            {
                reason = "ThetaSwitchRad 必须是 >0 的有限数，单位 rad。";
                return false;
            }
            // 捕获门限不许超过离线实测的平衡可恢复包线：门开得比能力大 = 明知会失控还进平衡。
            // 旧参数文件存的是 0.28 rad，载入 / 保存时都会被这条拒收（由 ParamStore 回落默认起摆参数）。
            if (ThetaSwitchRad > VerifiedRecoverableThetaRad)
            {
                reason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "ThetaSwitchRad={0:F3} rad 超过离线实测的平衡可恢复包线 {1:F2} rad：ψ0 再大时摆臂会撞软限位、"
                    + "失去对摆杆的加速度输入，进平衡必然失控。请收窄到 {1:F2} rad 以内。",
                    ThetaSwitchRad, VerifiedRecoverableThetaRad);
                return false;
            }
            if (!ControlMath.IsFinite(OmegaSwitchRadPerSec) || OmegaSwitchRadPerSec <= 0)
            {
                reason = "OmegaSwitchRadPerSec 必须是 >0 的有限数，单位 rad/s。";
                return false;
            }
            if (!ControlMath.IsFinite(PhiSwitchRad) || PhiSwitchRad <= 0)
            {
                reason = "PhiSwitchRad 必须是 >0 的有限数，单位 rad。";
                return false;
            }
            // 联合判据参数：旧参数文件没有这些键，全 0 = 未配置（由控制器按标定默认值补齐），
            // 不能因此拒载整个文件。只要配了任意一项，这一组就必须完整合法 —— 半套参数会让行程预算失真。
            bool hasCaptureEnvelope = CaptureModeAmplitudeMaxRad != 0 || CaptureStrokePerModeRad != 0 ||
                                      CaptureStrokeMarginRad != 0 || CaptureAdverseRateStrokeSec != 0 ||
                                      CaptureArmRateLimitRadPerSec != 0 || CapturePendulumRateRadPerSec != 0;
            if (hasCaptureEnvelope)
            {
                if (!ControlMath.IsFinite(CaptureModeAmplitudeMaxRad) || CaptureModeAmplitudeMaxRad <= 0)
                {
                    reason = "CaptureModeAmplitudeMaxRad 必须是 >0 的有限数，单位 rad（不稳定模态幅值上限）。";
                    return false;
                }
                if (CaptureModeAmplitudeMaxRad > VerifiedMaxCaptureModeAmplitudeRad)
                {
                    reason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "CaptureModeAmplitudeMaxRad={0:F3} rad 超过捕获门硬上限 {1:F2} rad："
                        + "捕获门直接取平衡恢复包线 |u|≤{2:F2} rad（第二十一轮裁定，不再留余量）；"
                        + "|u|=0.163 的格点已实测失控。请收窄到 {1:F2} rad 以内。",
                        CaptureModeAmplitudeMaxRad, VerifiedMaxCaptureModeAmplitudeRad,
                        VerifiedBalanceEnvelopeModeAmplitudeRad);
                    return false;
                }
                if (!ControlMath.IsFinite(CaptureStrokePerModeRad) ||
                    CaptureStrokePerModeRad < 1.0 || CaptureStrokePerModeRad > 40.0)
                {
                    reason = "CaptureStrokePerModeRad 必须在 [1, 40] rad/rad 内且有限（离线标定值 12.3）。"
                        + "小于 1 等于不判行程，大于 40 会让判据永远不成立。";
                    return false;
                }
                if (!ControlMath.IsFinite(CaptureStrokeMarginRad) ||
                    CaptureStrokeMarginRad <= 0 || CaptureStrokeMarginRad > 0.5)
                {
                    reason = "CaptureStrokeMarginRad 必须在 (0, 0.5] rad 内且有限（默认 0.05）。"
                        + "填 0 会被运行期当作「未配置」而补回默认值，所以这里直接拒收，不做静默回落。";
                    return false;
                }
                if (!ControlMath.IsFinite(CaptureAdverseRateStrokeSec) ||
                    CaptureAdverseRateStrokeSec <= 0 || CaptureAdverseRateStrokeSec > 5.0)
                {
                    reason = "CaptureAdverseRateStrokeSec 必须在 (0, 5] rad/(rad/s) 内且有限（默认 0.5）。";
                    return false;
                }
                if (!ControlMath.IsFinite(CaptureArmRateLimitRadPerSec) ||
                    CaptureArmRateLimitRadPerSec < 0.5 ||
                    CaptureArmRateLimitRadPerSec > VerifiedMaxCaptureArmRateRadPerSec)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CaptureArmRateLimitRadPerSec 必须在 [0.5, {0:F1}] rad/s 内且有限（默认 4.0）。"
                        + "4.0 是离线细扫标定值，6.0 的样本已实测失控；2026-11-20 台架把它抬到 15，"
                        + "起摆确实变快，但平衡接管 0.4 s 就掉摆。这是捕获门的安全上界，不是可调余量。",
                        VerifiedMaxCaptureArmRateRadPerSec);
                    return false;
                }
                if (!ControlMath.IsFinite(CapturePendulumRateRadPerSec) ||
                    CapturePendulumRateRadPerSec < 1.0 || CapturePendulumRateRadPerSec > 60.0)
                {
                    reason = "CapturePendulumRateRadPerSec 必须在 [1, 60] rad/s 内且有限（样机默认 9.0416）。"
                        + "它等于 √(mgl/J_eq)，填错会把 ω 折算成错误的等价摆角。";
                    return false;
                }
                // 一致性：判据最严处（|u| 取上限）需要的行程必须落在默认软限位的全行程以内，
                // 否则这条判据永远不可能满足，起摆只会一路走到超时。
                double worstStroke = CaptureStrokePerModeRad * CaptureModeAmplitudeMaxRad + CaptureStrokeMarginRad;
                if (!(worstStroke > 0) || worstStroke >= 3.0)
                {
                    reason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "联合判据在 |u| 取上限时要求 {0:F2} rad 摆臂行程，已接近/超过软限位全行程 3.6 rad："
                        + "这样的组合永远等不到满足，起摆会直接超时。请减小 CaptureStrokePerModeRad、"
                        + "CaptureModeAmplitudeMaxRad 或 CaptureStrokeMarginRad。", worstStroke);
                    return false;
                }
            }
            // 预捕获回中 / 快速提交 / 漏桶（2026-11-20 新增键）：旧文件缺键 = 0 = 未配置，
            // 由 SwingUpController 按内置默认补齐；只要配了任意一项，整组就必须合法。
            bool hasCaptureHandoff = CapturePrepareApproachRad != 0 || CapturePrepareEnergyMarginRatio != 0 ||
                                     CaptureCommitArmRateRadPerSec != 0 || CaptureCommitOmegaRadPerSec != 0 ||
                                     CaptureCommitHoldCycles != 0 || SwitchHoldDecayPerCycle != 0;
            if (hasCaptureHandoff)
            {
                if (!ControlMath.IsFinite(CapturePrepareApproachRad) ||
                    CapturePrepareApproachRad <= 0 ||
                    CapturePrepareApproachRad > MaxCapturePrepareApproachRad)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CapturePrepareApproachRad 必须在 (0, {0:F1}] rad 内且有限（预捕获回中窗口，默认 0.60）："
                        + "再大就会在整个下落半周期停泵，起摆能量爬不上去。",
                        MaxCapturePrepareApproachRad);
                    return false;
                }
                if (!ControlMath.IsFinite(CapturePrepareEnergyMarginRatio) ||
                    CapturePrepareEnergyMarginRatio <= 0 || CapturePrepareEnergyMarginRatio > 0.1)
                {
                    reason = "CapturePrepareEnergyMarginRatio 必须在 (0, 0.1] 内且有限（相对 2·mgl 的预捕获能量带宽，默认 0.015）。"
                        + "0 = 未配置（按 0.015 补齐）；再大等于长期停泵。";
                    return false;
                }
                if (!ControlMath.IsFinite(CaptureCommitArmRateRadPerSec) ||
                    CaptureCommitArmRateRadPerSec <= 0 ||
                    CaptureCommitArmRateRadPerSec > VerifiedMaxCaptureArmRateRadPerSec)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CaptureCommitArmRateRadPerSec 必须在 (0, {0:F1}] rad/s 内且有限（快速提交臂速软上限，默认 3.0）。",
                        VerifiedMaxCaptureArmRateRadPerSec);
                    return false;
                }
                if (!ControlMath.IsFinite(CaptureCommitOmegaRadPerSec) ||
                    CaptureCommitOmegaRadPerSec <= 0 ||
                    CaptureCommitOmegaRadPerSec > MaxCaptureCommitOmegaRadPerSec)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CaptureCommitOmegaRadPerSec 必须在 (0, {0:F0}] rad/s 内且有限（快速提交摆杆角速度上限，默认 1.2；"
                        + "台架实测 ψ0=−0.068、ω0=+1.10 交棒后 0.31 s ThetaOverrun，硬门对小幅高速穿越是盲的）。",
                        MaxCaptureCommitOmegaRadPerSec);
                    return false;
                }
                if (CaptureCommitHoldCycles <= 0 || CaptureCommitHoldCycles > MaxCaptureCommitHoldCycles)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CaptureCommitHoldCycles 必须在 [1, {0}] 内（快速提交净拍数，默认 20）。"
                        + "再大等于退回慢通道，掠顶窗口内不可能提交。", MaxCaptureCommitHoldCycles);
                    return false;
                }
                if (SwitchHoldDecayPerCycle < 0 ||
                    SwitchHoldDecayPerCycle > MaxSwitchHoldDecayPerCycle)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "SwitchHoldDecayPerCycle 必须在 [0, {0}] 内（单位千分之一拍/拍，默认 2；"
                        + "0 = 未配置按 2 补齐；再大等于取消跨掠顶累积语义）。", MaxSwitchHoldDecayPerCycle);
                    return false;
                }
            }
            // 能量整形（抽取 / 死区 / 软中心带）：新键，旧文件缺键读出来是 0 = 未配置，
            // 由 ParamStore.FillUnconfiguredSwingFields 按默认标注后再走这里；只要配了任意一项，
            // 整组就必须合法 —— 半套参数会让抽取权限与中心带判据失真。
            bool hasEnergyShaping = EnergyExtractRatio != 0 || EnergyDeadbandRatio != 0 || CenterBandRad != 0;
            if (hasEnergyShaping)
            {
                if (!ControlMath.IsFinite(EnergyExtractRatio) ||
                    EnergyExtractRatio < 0 || EnergyExtractRatio > 1.0)
                {
                    reason = "EnergyExtractRatio 必须在 [0, 1] 内且有限（相对 AMax 的抽取权限比例，默认 0.20）。"
                        + "0 = 未配置（按 0.20 补齐）；要退回「只注入」请填一个极小正数。";
                    return false;
                }
                if (!ControlMath.IsFinite(EnergyDeadbandRatio) ||
                    EnergyDeadbandRatio < 0 || EnergyDeadbandRatio > 0.2)
                {
                    reason = "EnergyDeadbandRatio 必须在 [0, 0.2] 内且有限（相对 E* 的死区，默认 0.005）。"
                        + "0 = 未配置（按 0.005 补齐）。";
                    return false;
                }
                if (!ControlMath.IsFinite(CenterBandRad) || CenterBandRad <= 0 || CenterBandRad > 100.0)
                {
                    reason = "CenterBandRad 必须在 (0, 100] rad 内且有限（软中心带半径，默认 20）。"
                        + "0 = 未配置（按 20 补齐）；它必须明显小于软限位行程，否则等于没有保护。";
                    return false;
                }
            }
            // 抽取增益（与 Ke 解耦，默认 60）：0 = 未配置（控制层按默认补齐），负值 / 非有限值一律拒收 ——
            // 负增益会把「抽取」变成同向注入，能量在 E* 上方越抽越高。
            if (!ControlMath.IsFinite(ExtractGain) || ExtractGain < 0)
            {
                reason = "ExtractGain 必须是 ≥0 的有限数，单位 rad/s² per J（抽取增益，默认 60；0 = 未配置按 60 补齐）。";
                return false;
            }
            if (SwitchHoldCycles <= 0)
            {
                reason = "SwitchHoldCycles 必须 >0（慢通道累计的净满足拍数 N_c；漏桶计数见 SwitchHoldDecayPerCycle）。";
                return false;
            }
            if (TimeoutMs <= 0)
            {
                reason = "TimeoutMs 必须 >0，单位 ms。";
                return false;
            }
            // 起动微扰 / ω 估计窗：旧参数文件没有这些键，全 0 = 未配置（由控制器补默认），
            // 不能因此拒载整个文件。只有真配了值时才要求都是有限数。
            bool hasScheduling = OmegaWindowMs != 0 || BootstrapAccelRadPerSec2 != 0 ||
                                 BootstrapSec != 0 || EnergySoftOmegaRadPerSec != 0 ||
                                 ThetaFadeStartRad != 0 || ThetaFadeFloor != 0;
            if (hasScheduling)
            {
                if (!ControlMath.IsFinite(OmegaWindowMs) ||
                    !ControlMath.IsFinite(BootstrapAccelRadPerSec2) ||
                    !ControlMath.IsFinite(BootstrapSec) ||
                    !ControlMath.IsFinite(EnergySoftOmegaRadPerSec) ||
                    !ControlMath.IsFinite(ThetaFadeStartRad) ||
                    !ControlMath.IsFinite(ThetaFadeEndRad) ||
                    !ControlMath.IsFinite(ThetaFadeFloor))
                {
                    reason = "起动微扰 / 相位软门 / ω 估计窗参数必须是有限数。";
                    return false;
                }
                // 近直立淡出下限低于 MinThetaFadeFloor 时，泵在顶部推不过摩擦，能量会在捕获窗前
                // 形成平台（台架 E 停在 1.5~1.6 J）。这是结构性下限，不是可调余量。
                if (ThetaFadeFloor < MinThetaFadeFloor || ThetaFadeFloor > 1.0)
                {
                    reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "ThetaFadeFloor={0:G6} 必须在 [{1:F2}, 1] 内：再低泵在顶部推不过摩擦，"
                        + "能量会卡在捕获窗前（离线与台架均复现）。", ThetaFadeFloor, MinThetaFadeFloor);
                    return false;
                }
            }
            reason = null;
            return true;
        }

        /// <summary>值拷贝一份，方便前端在改参数前留底。</summary>
        public SwingUpParams Clone()
        {
            return this;
        }

        /// <summary>
        /// 逐字段修复：只把**不合格的那一项**换成出厂默认值，合格的字段原样保留，
        /// 每一处替换都写进 report（字段名 / 读到的值 / 生效的默认值 / 原因），绝不静默回落。
        /// <para>
        /// 除越界修复外，还负责把**已知的历史出厂整定块**做一次性迁移：命中
        /// 「Ke=700 / AMax=80 / KPhi=8 / KPhiDot=1.5 / 泵关」时按新出厂提速值（AMax=150）生效，
        /// 同样写进 report。只认整组指纹，用户动过任意一项就不迁移。
        /// </para>
        /// <para>
        /// 与 <see cref="IsValid"/> 的分工：
        /// IsValid 是「整组提交」的严格门 —— 操作者在界面上按确定时，一组不合法的值必须整组拒绝并告诉他错在哪；
        /// 本方法只在**从参数文件读回**时用 —— 参数文件里一个历史字段越界（例如旧版留下的
        /// theta_switch_rad=0.28、pump_amplitude=0）不应该把用户调好的其它字段一起丢掉。
        /// </para>
        /// </summary>
        /// <param name="resolved">修复后的参数（this 不被修改）。</param>
        /// <param name="report">逐条替换说明；无替换时为 null。</param>
        /// <returns>是否发生过替换。</returns>
        public bool TryResolveInvalidFields(out SwingUpParams resolved, out string report)
        {
            SwingUpParams p = this;
            SwingUpParams def = CreateDefault();
            System.Collections.Generic.List<string> notes = new System.Collections.Generic.List<string>();

            // ---- 能量项 ----
            if (!ControlMath.IsFinite(p.TargetEnergyGain) ||
                p.TargetEnergyGain < MinTargetEnergyGain || p.TargetEnergyGain > MaxTargetEnergyGain)
            {
                notes.Add(Replace("TargetEnergyGain", p.TargetEnergyGain, def.TargetEnergyGain,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 [{0:F4}, {1:F2}] 内（ε 太大抓不住、太小爬不到）", MinTargetEnergyGain, MaxTargetEnergyGain)));
                p.TargetEnergyGain = def.TargetEnergyGain;
            }
            if (!ControlMath.IsFinite(p.Ke) || p.Ke <= 0)
            {
                notes.Add(Replace("Ke", p.Ke, def.Ke, "必须是 >0 的有限数 [rad/s² per J]"));
                p.Ke = def.Ke;
            }
            // AMax：一是不在合法区间 / 非有限 → 回落默认；二是命中 2026-11-20~21 台架那组
            // 「Ke=700 / AMax=80 / KPhi=8 / KPhiDot=1.5 / 泵关」的旧出厂整定块 → 一次性迁移到 150。
            // 迁移只认整组指纹（任一字段被动过就不算），并在 report 里写明读到的值与新值。
            bool legacySlowPump =
                Math.Abs(p.Ke - LegacySlowPumpKe) < 1e-9 &&
                Math.Abs(p.AMax - LegacySlowPumpAMaxRadPerSec2) < 1e-9 &&
                Math.Abs(p.KPhi - LegacySlowPumpKPhi) < 1e-9 &&
                Math.Abs(p.KPhiDot - LegacySlowPumpKPhiDot) < 1e-9 &&
                p.PumpAmplitude == 0.0;
            if (legacySlowPump)
            {
                notes.Add(Replace("AMax", p.AMax, def.AMax,
                    "命中旧出厂整定块（泵权限被 0.66·ω_n 包线截在 54 rad/s²，起摆 30~90 s）；"
                    + "按第八轮折中值 100 生效（要测 80 请同时改本文件任一字段后重存）"));
                p.AMax = def.AMax;
            }
            else if (!ControlMath.IsFinite(p.AMax) ||
                     p.AMax < MinSwingAMaxRadPerSec2 || p.AMax > MaxSwingAMaxRadPerSec2)
            {
                notes.Add(Replace("AMax", p.AMax, def.AMax,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 [{0:F0}, {1:F0}] rad/s² 内（低于 {0:F0} 起摆必超时，高于 {1:F0} 无物理依据）",
                        MinSwingAMaxRadPerSec2, MaxSwingAMaxRadPerSec2)));
                p.AMax = def.AMax;
            }
            if (!ControlMath.IsFinite(p.KPhi) || p.KPhi < 0)
            {
                notes.Add(Replace("KPhi", p.KPhi, def.KPhi, "必须是 ≥0 的有限数 [1/s²]"));
                p.KPhi = def.KPhi;
            }
            if (!ControlMath.IsFinite(p.KPhiDot) || p.KPhiDot < 0)
            {
                notes.Add(Replace("KPhiDot", p.KPhiDot, def.KPhiDot, "必须是 ≥0 的有限数 [1/s]"));
                p.KPhiDot = def.KPhiDot;
            }
            if (!ControlMath.IsFinite(p.PumpAmplitude) || p.PumpAmplitude < 0)
            {
                notes.Add(Replace("PumpAmplitude", p.PumpAmplitude, def.PumpAmplitude, "必须是 ≥0 的有限数 [rad/s²]，0 = 关泵"));
                p.PumpAmplitude = def.PumpAmplitude;
            }
            if (!ControlMath.IsFinite(p.PumpFrequencyHz) || p.PumpFrequencyHz < 0)
            {
                notes.Add(Replace("PumpFrequencyHz", p.PumpFrequencyHz, def.PumpFrequencyHz, "必须是 ≥0 的有限数 [Hz]"));
                p.PumpFrequencyHz = def.PumpFrequencyHz;
            }
            if (!ControlMath.IsFinite(p.PumpPhaseOffsetRad))
            {
                notes.Add(Replace("PumpPhaseOffsetRad", p.PumpPhaseOffsetRad, def.PumpPhaseOffsetRad, "必须是有限数 [rad]"));
                p.PumpPhaseOffsetRad = def.PumpPhaseOffsetRad;
            }

            // ---- 切换判据 ----
            if (!ControlMath.IsFinite(p.ThetaSwitchRad) || p.ThetaSwitchRad <= 0)
            {
                notes.Add(Replace("ThetaSwitchRad", p.ThetaSwitchRad, def.ThetaSwitchRad, "必须是 >0 的有限数 [rad]"));
                p.ThetaSwitchRad = def.ThetaSwitchRad;
            }
            else if (p.ThetaSwitchRad > VerifiedRecoverableThetaRad)
            {
                notes.Add(Replace("ThetaSwitchRad", p.ThetaSwitchRad, def.ThetaSwitchRad, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "超过离线实测的平衡可恢复包线 {0:F2} rad，门开得比能力大 = 明知会失控还进平衡",
                    VerifiedRecoverableThetaRad)));
                p.ThetaSwitchRad = def.ThetaSwitchRad;
            }
            if (!ControlMath.IsFinite(p.OmegaSwitchRadPerSec) || p.OmegaSwitchRadPerSec <= 0)
            {
                notes.Add(Replace("OmegaSwitchRadPerSec", p.OmegaSwitchRadPerSec, def.OmegaSwitchRadPerSec, "必须是 >0 的有限数 [rad/s]"));
                p.OmegaSwitchRadPerSec = def.OmegaSwitchRadPerSec;
            }
            if (!ControlMath.IsFinite(p.PhiSwitchRad) || p.PhiSwitchRad <= 0)
            {
                notes.Add(Replace("PhiSwitchRad", p.PhiSwitchRad, def.PhiSwitchRad, "必须是 >0 的有限数 [rad]"));
                p.PhiSwitchRad = def.PhiSwitchRad;
            }

            // ---- 捕获门联合判据：旧文件缺键 = 0 = 未配置，不算错误；配了但越界才修 ----
            bool captureConfigured = p.CaptureModeAmplitudeMaxRad != 0 || p.CaptureStrokePerModeRad != 0 ||
                                     p.CaptureStrokeMarginRad != 0 || p.CaptureAdverseRateStrokeSec != 0 ||
                                     p.CaptureArmRateLimitRadPerSec != 0 || p.CapturePendulumRateRadPerSec != 0;
            if (captureConfigured)
            {
                // 第二十一轮：捕获门直接取平衡包线 0.12。旧出厂值 0.10（留 20% 余量）一次性迁移，
                // 否则参数文件里的 0.10 会盖掉新默认值，「到了顶也不让切」的现象原样保留。
                if (Math.Abs(p.CaptureModeAmplitudeMaxRad - LegacyCaptureModeAmplitudeMaxRad) < 1e-9)
                {
                    notes.Add(Replace("CaptureModeAmplitudeMaxRad", p.CaptureModeAmplitudeMaxRad,
                        def.CaptureModeAmplitudeMaxRad, "旧出厂值 0.10 一次性迁移（包线本身 = 0.12）"));
                    p.CaptureModeAmplitudeMaxRad = def.CaptureModeAmplitudeMaxRad;
                }
                if (!ControlMath.IsFinite(p.CaptureModeAmplitudeMaxRad) || p.CaptureModeAmplitudeMaxRad <= 0 ||
                    p.CaptureModeAmplitudeMaxRad > VerifiedMaxCaptureModeAmplitudeRad)
                {
                    notes.Add(Replace("CaptureModeAmplitudeMaxRad", p.CaptureModeAmplitudeMaxRad, def.CaptureModeAmplitudeMaxRad,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "必须在 (0, {0:F2}] rad 内（= 平衡包线 |u|≤{1:F2}，不再留余量）",
                            VerifiedMaxCaptureModeAmplitudeRad, VerifiedBalanceEnvelopeModeAmplitudeRad)));
                    p.CaptureModeAmplitudeMaxRad = def.CaptureModeAmplitudeMaxRad;
                }
                if (!ControlMath.IsFinite(p.CaptureStrokePerModeRad) ||
                    p.CaptureStrokePerModeRad < 1.0 || p.CaptureStrokePerModeRad > 40.0)
                {
                    notes.Add(Replace("CaptureStrokePerModeRad", p.CaptureStrokePerModeRad, def.CaptureStrokePerModeRad,
                        "必须在 [1, 40] rad/rad 内"));
                    p.CaptureStrokePerModeRad = def.CaptureStrokePerModeRad;
                }
                if (!ControlMath.IsFinite(p.CaptureStrokeMarginRad) ||
                    p.CaptureStrokeMarginRad <= 0 || p.CaptureStrokeMarginRad > 0.5)
                {
                    notes.Add(Replace("CaptureStrokeMarginRad", p.CaptureStrokeMarginRad, def.CaptureStrokeMarginRad,
                        "必须在 (0, 0.5] rad 内"));
                    p.CaptureStrokeMarginRad = def.CaptureStrokeMarginRad;
                }
                if (!ControlMath.IsFinite(p.CaptureAdverseRateStrokeSec) ||
                    p.CaptureAdverseRateStrokeSec <= 0 || p.CaptureAdverseRateStrokeSec > 5.0)
                {
                    notes.Add(Replace("CaptureAdverseRateStrokeSec", p.CaptureAdverseRateStrokeSec, def.CaptureAdverseRateStrokeSec,
                        "必须在 (0, 5] rad/(rad/s) 内"));
                    p.CaptureAdverseRateStrokeSec = def.CaptureAdverseRateStrokeSec;
                }
                if (!ControlMath.IsFinite(p.CaptureArmRateLimitRadPerSec) ||
                    p.CaptureArmRateLimitRadPerSec < 0.5 ||
                    p.CaptureArmRateLimitRadPerSec > VerifiedMaxCaptureArmRateRadPerSec)
                {
                    notes.Add(Replace("CaptureArmRateLimitRadPerSec", p.CaptureArmRateLimitRadPerSec, def.CaptureArmRateLimitRadPerSec,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "必须在 [0.5, {0:F1}] rad/s 内（4.0 标定值，6.0 已实测失控；台架 15 导致平衡接管 0.4 s 掉摆）",
                            VerifiedMaxCaptureArmRateRadPerSec)));
                    p.CaptureArmRateLimitRadPerSec = def.CaptureArmRateLimitRadPerSec;
                }
                if (!ControlMath.IsFinite(p.CapturePendulumRateRadPerSec) ||
                    p.CapturePendulumRateRadPerSec < 1.0 || p.CapturePendulumRateRadPerSec > 60.0)
                {
                    notes.Add(Replace("CapturePendulumRateRadPerSec", p.CapturePendulumRateRadPerSec, def.CapturePendulumRateRadPerSec,
                        "必须在 [1, 60] rad/s 内（= √(mgl/J_eq)）"));
                    p.CapturePendulumRateRadPerSec = def.CapturePendulumRateRadPerSec;
                }
            }

            // ---- 预捕获回中 / 快速提交 / 漏桶：新键，0 = 未配置，不算错误；配了但越界才修 ----
            if (p.CapturePrepareApproachRad != 0 &&
                (!ControlMath.IsFinite(p.CapturePrepareApproachRad) ||
                 p.CapturePrepareApproachRad <= 0 ||
                 p.CapturePrepareApproachRad > MaxCapturePrepareApproachRad))
            {
                notes.Add(Replace("CapturePrepareApproachRad", p.CapturePrepareApproachRad, def.CapturePrepareApproachRad,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 (0, {0:F1}] rad 内（预捕获回中窗口）", MaxCapturePrepareApproachRad)));
                p.CapturePrepareApproachRad = def.CapturePrepareApproachRad;
            }
            if (p.CapturePrepareEnergyMarginRatio != 0 &&
                (!ControlMath.IsFinite(p.CapturePrepareEnergyMarginRatio) ||
                 p.CapturePrepareEnergyMarginRatio <= 0 ||
                 p.CapturePrepareEnergyMarginRatio > 0.1))
            {
                notes.Add(Replace("CapturePrepareEnergyMarginRatio", p.CapturePrepareEnergyMarginRatio,
                    def.CapturePrepareEnergyMarginRatio,
                    "必须在 (0, 0.1] 内（相对 2·mgl 的预捕获能量带宽）"));
                p.CapturePrepareEnergyMarginRatio = def.CapturePrepareEnergyMarginRatio;
            }
            if (p.CaptureCommitArmRateRadPerSec != 0 &&
                (!ControlMath.IsFinite(p.CaptureCommitArmRateRadPerSec) ||
                 p.CaptureCommitArmRateRadPerSec <= 0 ||
                 p.CaptureCommitArmRateRadPerSec > VerifiedMaxCaptureArmRateRadPerSec))
            {
                notes.Add(Replace("CaptureCommitArmRateRadPerSec", p.CaptureCommitArmRateRadPerSec,
                    def.CaptureCommitArmRateRadPerSec,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 (0, {0:F1}] rad/s 内（快速提交臂速上限）", VerifiedMaxCaptureArmRateRadPerSec)));
                p.CaptureCommitArmRateRadPerSec = def.CaptureCommitArmRateRadPerSec;
            }
            // 旧出厂值 0.5（2026-11-20~21 参数文件里一直是它）一次性迁移到当前默认：
            // 0.5 不是台架整定出来的，是第五轮在没有噪声估计时为了压 2 ms 差分噪声给的保守值；
            // 现在提交带用 ω_est（40 ms 最小二乘），0.5 属于凭空多收。
            if (Math.Abs(p.CaptureCommitOmegaRadPerSec - LegacyCommitOmegaRadPerSec) < 1e-9)
            {
                notes.Add(Replace("CaptureCommitOmegaRadPerSec", p.CaptureCommitOmegaRadPerSec,
                    def.CaptureCommitOmegaRadPerSec, "旧出厂值 0.5 一次性迁移（包线允许 1.08）"));
                p.CaptureCommitOmegaRadPerSec = def.CaptureCommitOmegaRadPerSec;
            }
            // 第九轮出厂值 1.0 一次性迁移到 1.2：略超包线，用自动回起摆兜底换掠顶窗口。
            else if (Math.Abs(p.CaptureCommitOmegaRadPerSec - LegacyCommitOmegaRadPerSecV2) < 1e-9)
            {
                notes.Add(Replace("CaptureCommitOmegaRadPerSec", p.CaptureCommitOmegaRadPerSec,
                    def.CaptureCommitOmegaRadPerSec, "旧出厂值 1.0 一次性迁移（第二十一轮：略超包线，靠回退兜底）"));
                p.CaptureCommitOmegaRadPerSec = def.CaptureCommitOmegaRadPerSec;
            }
            else if (!ControlMath.IsFinite(p.CaptureCommitOmegaRadPerSec) ||
                     p.CaptureCommitOmegaRadPerSec <= 0 ||
                     p.CaptureCommitOmegaRadPerSec > MaxCaptureCommitOmegaRadPerSec)
            {
                notes.Add(Replace("CaptureCommitOmegaRadPerSec", p.CaptureCommitOmegaRadPerSec,
                    def.CaptureCommitOmegaRadPerSec,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 (0, {0:F0}] rad/s 内（快速提交角速度上限）", MaxCaptureCommitOmegaRadPerSec)));
                p.CaptureCommitOmegaRadPerSec = def.CaptureCommitOmegaRadPerSec;
            }
            // 第二十一轮：快速提交净拍 20 → 5。旧出厂值一次性迁移，否则参数文件里的 20
            // 会让新门白放宽（掠顶窗口只有 14~48 ms，20 拍本身就吃掉一截）。
            if (p.CaptureCommitHoldCycles == LegacyCaptureCommitHoldCycles)
            {
                notes.Add(Replace("CaptureCommitHoldCycles", p.CaptureCommitHoldCycles,
                    def.CaptureCommitHoldCycles, "旧出厂值 20 拍一次性迁移（第二十一轮：5 拍，别为凑拍数错过掠顶窗口）"));
                p.CaptureCommitHoldCycles = def.CaptureCommitHoldCycles;
            }
            else if (p.CaptureCommitHoldCycles != 0 &&
                (p.CaptureCommitHoldCycles < 0 || p.CaptureCommitHoldCycles > MaxCaptureCommitHoldCycles))
            {
                notes.Add(Replace("CaptureCommitHoldCycles", p.CaptureCommitHoldCycles,
                    def.CaptureCommitHoldCycles,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 [1, {0}] 内（快速提交净拍数；0 = 未配置按默认补齐）", MaxCaptureCommitHoldCycles)));
                p.CaptureCommitHoldCycles = def.CaptureCommitHoldCycles;
            }
            if (p.SwitchHoldDecayPerCycle < 0 || p.SwitchHoldDecayPerCycle > MaxSwitchHoldDecayPerCycle)
            {
                notes.Add(Replace("SwitchHoldDecayPerCycle", p.SwitchHoldDecayPerCycle,
                    def.SwitchHoldDecayPerCycle,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "必须在 [0, {0}] 内（单位千分之一拍/拍；0 = 未配置按默认补齐）", MaxSwitchHoldDecayPerCycle)));
                p.SwitchHoldDecayPerCycle = def.SwitchHoldDecayPerCycle;
            }

            // ---- 能量抽取 / 死区 / 软中心带：新键，0 = 未配置 ----
            // 与 Capture* 同一口径：只要配了任意一项就整组校验；缺键（全 0）留给
            // ParamStore.FillUnconfiguredSwingFields 按默认标注，不在这里制造「越界」噪声。
            bool energyShapingConfigured = p.EnergyExtractRatio != 0 || p.EnergyDeadbandRatio != 0 ||
                                           p.CenterBandRad != 0;
            if (energyShapingConfigured)
            {
                // 旧出厂抽取权限 0.2 / 0.3（2026-11-20~21 参数文件里用过的值）一次性迁移到 1.0：
                // 这两个值只给 20~30 rad/s² 的抽取权限，r9 在 3 J 过冲下追不上 100 rad/s² 的注入，
                // 摆杆在顶部 9~16 rad/s 高速循环、90 s 抓不住。比例律由 ExtractGain=60 负责，不会 bang-bang。
                if (Math.Abs(p.EnergyExtractRatio - 0.20) < 1e-9 ||
                    Math.Abs(p.EnergyExtractRatio - 0.30) < 1e-9)
                {
                    notes.Add(Replace("EnergyExtractRatio", p.EnergyExtractRatio, def.EnergyExtractRatio,
                        "旧出厂值 0.2/0.3 一次性迁移（抽取权限 = AMax；超调越大抽得越多由 ExtractGain 控制）"));
                    p.EnergyExtractRatio = def.EnergyExtractRatio;
                }
                if (!ControlMath.IsFinite(p.EnergyExtractRatio) ||
                    p.EnergyExtractRatio < 0 || p.EnergyExtractRatio > 1.0)
                {
                    notes.Add(Replace("EnergyExtractRatio", p.EnergyExtractRatio, def.EnergyExtractRatio,
                        "必须在 [0, 1] 内（相对 AMax 的抽取权限比例）"));
                    p.EnergyExtractRatio = def.EnergyExtractRatio;
                }
                // 旧出厂死区 0.005（=±0.01 J）一次性迁到 0.0025（=±0.005 J）：捕获要求目标 ±0.005 J，
                // 旧死区本身比需求大一倍，律在目标附近直接不动作。
                if (Math.Abs(p.EnergyDeadbandRatio - 0.005) < 1e-9)
                {
                    notes.Add(Replace("EnergyDeadbandRatio", p.EnergyDeadbandRatio, def.EnergyDeadbandRatio,
                        "旧出厂死区 0.005（±0.01 J）一次性迁移到 0.0025（±0.005 J）"));
                    p.EnergyDeadbandRatio = def.EnergyDeadbandRatio;
                }
                if (!ControlMath.IsFinite(p.EnergyDeadbandRatio) ||
                    p.EnergyDeadbandRatio < 0 || p.EnergyDeadbandRatio > 0.2)
                {
                    notes.Add(Replace("EnergyDeadbandRatio", p.EnergyDeadbandRatio, def.EnergyDeadbandRatio,
                        "必须在 [0, 0.2] 内（相对 E* 的死区）"));
                    p.EnergyDeadbandRatio = def.EnergyDeadbandRatio;
                }
                if (!ControlMath.IsFinite(p.CenterBandRad) || p.CenterBandRad <= 0 || p.CenterBandRad > 100.0)
                {
                    notes.Add(Replace("CenterBandRad", p.CenterBandRad, def.CenterBandRad,
                        "必须在 (0, 100] rad 内（软中心带半径，必须明显小于软限位行程）"));
                    p.CenterBandRad = def.CenterBandRad;
                }
            }
            // 抽取增益：0 = 未配置（控制层按默认 60 补齐）；负值 / 非有限值必须替换成默认，
            // 否则「抽取」会变成同向注入。与 ParamStore 的拒收口径一致，这里是唯一权威。
            if (!ControlMath.IsFinite(p.ExtractGain) || p.ExtractGain < 0)
            {
                notes.Add(Replace("ExtractGain", p.ExtractGain, def.ExtractGain,
                    "必须是 ≥0 的有限数 [rad/s² per J]（抽取增益；0 = 未配置按 60 补齐）"));
                p.ExtractGain = def.ExtractGain;
            }

            if (p.SwitchHoldCycles <= 0)
            {
                notes.Add(Replace("SwitchHoldCycles", p.SwitchHoldCycles, def.SwitchHoldCycles, "必须 >0 拍"));
                p.SwitchHoldCycles = def.SwitchHoldCycles;
            }
            if (p.TimeoutMs <= 0)
            {
                notes.Add(Replace("TimeoutMs", p.TimeoutMs, def.TimeoutMs, "必须 >0 ms"));
                p.TimeoutMs = def.TimeoutMs;
            }

            // ---- 起动微扰 / 相位软门 / ω 估计窗：0 = 未配置（由控制器补默认），非零就必须有限 ----
            if (!ControlMath.IsFinite(p.OmegaWindowMs) || p.OmegaWindowMs < 0)
            {
                notes.Add(Replace("OmegaWindowMs", p.OmegaWindowMs, def.OmegaWindowMs, "必须是 ≥0 的有限数 [ms]"));
                p.OmegaWindowMs = def.OmegaWindowMs;
            }
            if (!ControlMath.IsFinite(p.BootstrapAccelRadPerSec2) || p.BootstrapAccelRadPerSec2 < 0)
            {
                notes.Add(Replace("BootstrapAccelRadPerSec2", p.BootstrapAccelRadPerSec2, def.BootstrapAccelRadPerSec2,
                    "必须是 ≥0 的有限数 [rad/s²]"));
                p.BootstrapAccelRadPerSec2 = def.BootstrapAccelRadPerSec2;
            }
            if (!ControlMath.IsFinite(p.BootstrapSec) || p.BootstrapSec < 0)
            {
                notes.Add(Replace("BootstrapSec", p.BootstrapSec, def.BootstrapSec, "必须是 ≥0 的有限数 [s]"));
                p.BootstrapSec = def.BootstrapSec;
            }
            if (!ControlMath.IsFinite(p.EnergySoftOmegaRadPerSec) || p.EnergySoftOmegaRadPerSec <= 0)
            {
                notes.Add(Replace("EnergySoftOmegaRadPerSec", p.EnergySoftOmegaRadPerSec, def.EnergySoftOmegaRadPerSec,
                    "必须是 >0 的有限数 [rad/s]"));
                p.EnergySoftOmegaRadPerSec = def.EnergySoftOmegaRadPerSec;
            }
            if (!ControlMath.IsFinite(p.ThetaFadeStartRad) || !ControlMath.IsFinite(p.ThetaFadeEndRad) ||
                !ControlMath.IsFinite(p.ThetaFadeFloor) ||
                p.ThetaFadeStartRad < 0 || p.ThetaFadeEndRad < 0 ||
                p.ThetaFadeFloor < MinThetaFadeFloor || p.ThetaFadeFloor > 1)
            {
                notes.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "近直立淡出组（ThetaFadeStartRad={0} / ThetaFadeEndRad={1} / ThetaFadeFloor={2}）→ 默认 {3}/{4}/{5}（角度需 ≥0，下限需在 [{6},1]：再低泵在顶部推不过摩擦，能量会卡在捕获窗前）",
                    Describe(p.ThetaFadeStartRad), Describe(p.ThetaFadeEndRad), Describe(p.ThetaFadeFloor),
                    Describe(def.ThetaFadeStartRad), Describe(def.ThetaFadeEndRad), Describe(def.ThetaFadeFloor),
                    Describe(MinThetaFadeFloor)));
                p.ThetaFadeStartRad = def.ThetaFadeStartRad;
                p.ThetaFadeEndRad = def.ThetaFadeEndRad;
                p.ThetaFadeFloor = def.ThetaFadeFloor;
            }

            resolved = p;
            report = notes.Count == 0 ? null : string.Join("；", notes.ToArray());
            return notes.Count > 0;
        }

        /// <summary>一条替换说明：字段=旧值 → 默认值（原因）。</summary>
        private static string Replace(string name, double value, double fallback, string rule)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}={1} → {2}（{3}）", name, Describe(value), Describe(fallback), rule);
        }

        private static string Replace(string name, int value, int fallback, string rule)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}={1} → {2}（{3}）", name, value, fallback, rule);
        }

        /// <summary>数值可读化：NaN / Infinity 直接写出来，别显示成 "-2147483648" 之类。</summary>
        private static string Describe(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "+Inf";
            if (double.IsNegativeInfinity(value)) return "-Inf";
            return value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>起手值，照控制层契约 §4.3 的增益量级表。</summary>
        public static SwingUpParams CreateDefault()
        {
            SwingUpParams p = new SwingUpParams();
            // ε 必须略负且极小：E* = 2mgl(1+ε) 要落在 [1.9950, 2.0) 这条"可交接能量带"里
            //（θ_switch=0.10；推导见字段注释）。取 −0.002 ⇒ ψ_turn≈0.089 rad，落在折返带 0.06~0.09。
            p.TargetEnergyGain = -0.002;
            // Ke 必须让 u_E = Ke·ΔE 在整个爬升区间都顶到 aPumpMax。注意现在是**软饱和**：
            //   x = Ke·ΔE/aPumpMax，u_E = aPumpMax·x/(1+|x|)；
            //   要拿到 ≥90% 权限需要 x ≥ 9 ⇒ Ke ≥ 9·aPumpMax/(2·mgl)。
            // 本机 aPumpMax=150、2·mgl=2 J ⇒ Ke ≥ 675，取 700（与 11-20 台架整定一致）。
            // 换摆按 Ke ∝ aPumpMax/mgl 同比例改。
            // 历史（AMax=40 时代）Ke=60 就够，是因为 aPumpMax 只有 40：60·2/40=3，x=3 给到 75%。
            p.Ke = 700.0;
            // 摆臂加速度权限：第八轮折中 100（150 时掠顶太快、起摆 1/7；80 是 r6 的 5/8 但慢）。
            // 注入率约为旧包线 54 的 1.85 倍，峰值臂速 100/ω_n≈11.1 rad/s。
            p.AMax = 100.0;
            p.KPhi = 3.0;
            p.KPhiDot = 0.8;
            // 回中项最多占 20% 的加速度预算，剩下 80% 留给能量注入（理由见字段注释）。
            p.CenterAuthRatio = 0.20;
            // 能量抽取 / 死区 / 软中心带（2026-11-20 台架：只注入不抽取会让摆杆冲过 E* 后整圈转）。
            // 第九轮：抽取权限放到 1.0·AMax —— 泵把能量灌到 3 J 时 0.2 的权限根本抽不回来
            //（r9 顶部 9~16 rad/s 高速循环）。ExtractGain=60 独立，比例律不会 bang-bang。
            p.EnergyExtractRatio = 1.0;
            // 抽取增益与收敛增益对齐（90）：目标附近两个半周期对称修正，避免单向残差。
            p.ExtractGain = 90.0;
            // 死区 0.0025（≈0.005 J）：捕获要求 E 落在目标 ±0.005 J 内，0.005 的死区本身就有 ±0.01 J。
            p.EnergyDeadbandRatio = 0.0025;
            // 软中心带 20 rad（用户 2026-11-20 裁定）：本机软限位约 [-17.4, 10.1] rad，
            // 3.6 会把出厂摆臂行程提前锁死（实测 3.6 起摆 58 s、20 时 13 s）。
            // 用户显式写入的值原样保留，只有缺键 / 越界才回落到这里。
            p.CenterBandRad = 20.0;
            // 泵默认关：修正后的闭环能量项自己找相位，开环正弦只会跟它抢拍。
            // 需要时在界面上把泵幅打开做对照实验，日志会如实打本拍实际施加量。
            p.PumpAmplitude = 0.0;
            p.PumpFrequencyHz = 1.4390;
            p.PumpPhaseOffsetRad = 0.0;
            p.ThetaSwitchRad = 0.10;
            p.OmegaSwitchRadPerSec = 3.0;
            p.PhiSwitchRad = 0.6;
            // 捕获门 |u| 上限 0.12 = 平衡包线本身（第二十一轮：不再留余量）。
            // 第六轮的 0.08 在台架 r7 把起摆成功率打到 1/5（掠顶窗口被乘小），第七轮放开到 0.10。
            p.CaptureModeAmplitudeMaxRad = 0.12;
            p.CapturePredictCycles = 24.0;
            // 兼容字段：交棒预测已改为直接读取 PendulumEngine.BalanceKPsi/BalanceKOmega/BalanceKPhiDot。
            p.BalanceKa = 900.0;
            p.BalanceKad = 55.0;
            p.BalanceKPhi = 1.0;
            p.BalanceKPhiDot = 1.0;
            p.CaptureStrokePerModeRad = 12.3;
            p.CaptureStrokeMarginRad = 0.05;
            p.CaptureAdverseRateStrokeSec = 0.5;
            p.CaptureArmRateLimitRadPerSec = 4.0;
            p.CapturePendulumRateRadPerSec = 9.0416;
            // 预捕获限速刹车：|ψ| ≤ 0.60 rad、能量在预窗口内、本拍刹车抽取能量、且 |φ̇| 超过
            // CaptureCommitArmRateRadPerSec 时才把臂速压回门限（不回中、不刹到 0）。
            // 判据：ArmCouplingSign·ω·cosθ·sign(φ̇) < 0（推导见 CapturePrepareApproachRad 字段注释）。
            // 2026-11-21 反解 S=+1（β≈+0.20）；旧的 S=−1/上行侧结论作废，外跑抽取相位为下行侧。
            // 2026-11-20 记录的旧下落侧 +1.10 J/s 是在旧判据下测的，新符号下必须重测，不作相位依据。
            // 判定为注入的半周期完全不刹车（不做半刹车，半刹车仍是注入）。
            p.CapturePrepareApproachRad = 0.60;
            p.CapturePrepareEnergyMarginRatio = 0.015;
            // 快速提交带（2026-11-21 第五轮）：β 实测 +0.0499（脉冲标定 +0.048）；权限放开后
            //（AccMax=800、Ka=5000）离线验收最大可救 ψ0=0.162(ω=0)/0.123(ω=0.3)/0.096(ω=0.5)。
            // θ_switch=0.10、|ω|=0.5（本轮定稿）；|φ̇|≤3.0 暂留，待新臂速网格。
            p.CaptureCommitArmRateRadPerSec = 3.0;
            // |ω_est| 上限 = 1.2：略超平衡包线（ψ=0 时 1.08），用自动回起摆兜底换掠顶窗口。
            // 旧的 0.5 → 1.0（第九轮）→ 1.2（第二十一轮）。
            p.CaptureCommitOmegaRadPerSec = 1.2;
            // 交棒判据收敛为一句：「硬门(u 包线 ≤0.12) + |ω_est| ≤ 1.2，净 5 拍即交棒」。
            // 5 拍 @1 ms ≈ 5 ms，只用来滤单拍边界抖动；慢通道 60 拍仍保留。
            // 旧值 20 拍会把掠顶窗口（台架 14~48 ms）再削一截，等于为了凑拍数错过窗口。
            p.CaptureCommitHoldCycles = 5;
            // 漏桶：满足 +1、不满足 −2，允许单拍边界抖动，占空比 > 2/3 才净增长。
            p.SwitchHoldDecayPerCycle = 2;
            p.SwitchHoldCycles = 60;
            // 起摆截止。旧默认 30 s 是按「权限充足、一两个来回就上去」的想象给的，台架实测不成立：
            // 本机耦合弱，从静止爬到捕获窗要 150 s 量级（147 s 才到 E=1.86 J），30 s 必然误判失败。
            // 180 s 留一倍余量；它只决定「多久算失败」，到点进 Fault(SwingUpTimeout) 后摆臂照常受软限位与
            // 摆动看门狗管，不会因为超时长就变成危险动作。无头模式的运动看门狗 300 s 仍在它之后兜底。
            p.TimeoutMs = 180000;
            p.BootstrapAccelRadPerSec2 = 5.0;
            p.BootstrapSec = 0.25;
            p.EnergySoftOmegaRadPerSec = 0.10;
            p.OmegaWindowMs = 40.0;
            p.ThetaFadeStartRad = 1.00;
            p.ThetaFadeEndRad = 0.45;
            // 0.35：顶部仍留有足够权限把能量推过摩擦，同时摆臂掠顶速度在捕获门以内
            // （0.15 的旧值会把能量卡在 E*−0.02 J，见字段注释）。
            p.ThetaFadeFloor = MinThetaFadeFloor;
            return p;
        }
    }
}
