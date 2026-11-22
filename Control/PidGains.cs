using System;
using System.Globalization;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 平衡控制律的增益包。
    /// <para>
    /// <b>平衡环当前由 PendulumEngine 内固定的状态反馈驱动</b>（默认 HYBRID：4 状态 LQR 刚度 + ∫φ 回中；
    /// <c>FURUTA_BALANCE_LAW=lqi</c> 切到 LQI）：增益是编译期常量，
    /// 运行期不可调。因此本结构体里的 <see cref="Ka"/> / <see cref="Kad"/> / <see cref="Kai"/> /
    /// <see cref="KPhi"/> / <see cref="KPhiDot"/> 是上一代加速度反馈律的历史字段 —— 控制律**不再读取**，
    /// 保留只为旧参数文件的读写往返与界面只读显示（界面上调它们不生效）。
    /// </para>
    /// <para>
    /// <b>仍参与运行期判据的字段</b>：<see cref="AccMax"/>（回中 / 标定 / 点动斜坡的加速度上限）、
    /// 内环 <see cref="KpPhi"/> / <see cref="KiPhi"/> / <see cref="KdPhi"/> 与 <see cref="TdLpMs"/>
    /// （参数合法性校验：闭环极点必须落在单位圆内）。
    /// <see cref="ArmRateMax"/> 自 2026-11-21 起**不再参与控制**（平衡参考速度只由「S2 误差窗口 / 伺服滞后」
    /// 现算），保留只为旧参数文件往返与界面显示。
    /// <see cref="OutMin"/> / <see cref="OutMax"/> / <see cref="OutRateLimit"/> / <see cref="MaxIntegral"/> /
    /// <see cref="DeadBand"/> 只保留参数文件读写与界面显示，不再进入控制律。
    /// </para>
    /// <para>
    /// <b>Kp/Ki/Kd 也是历史字段</b>：旧「摆臂位置映射」律的增益（量纲 rad臂/rad杆），
    /// 与加速度律的增益不是一回事，也没有正确换算关系，控制律同样**不再读取**。
    /// </para>
    /// <para>
    /// 加速度律字段在旧参数文件里读出来是 0（文件里没有这些键），0 一律视为「未配置」，
    /// 由 PendulumEngine 按 <see cref="CreateAccelerationLawDefaults"/> 补齐；补齐值只用于历史显示。
    /// </para>
    /// </summary>
    public struct PidGains
    {
        /// <summary>双缓冲代号。UI 写影子缓冲时自增，周期线程在拍首顶换后看到新代号。</summary>
        public uint Generation;

        // ---- 加速度反馈律字段：Ka / Kad / Kai / Kφ / Kφ̇ / ArmRateMax 为历史值；AccMax 仍生效 ----
        // 平衡环现由 PendulumEngine 内固定的状态反馈驱动：上面这些不再进入控制律，
        // AccMax 仍作为回中 / 标定 / 点动斜坡的加速度上限；ArmRateMax 自 2026-11-21 起只作显示。

        /// <summary>
        /// **历史字段**：上一代加速度反馈律的摆杆角误差增益 Ka [rad/s² per rad]，平衡环已由固定状态
        /// 状态反馈接管，控制律不再读取它。以下取值与结构下界只用于旧参数文件与兼容校验。默认 **5000**。
        /// 结构下界是 g/l ÷ |β|：本台架 g/l = 81.75、脉冲标定 |β| = 0.048（19 拍 0x6064 反算 +0.0499）
        /// ⇒ 下界 ≈ **1703**；5000 留约 2.9 倍结构裕量。
        /// 第十三轮曾把默认降到 2500/180（理由：ψ=0.05 时 P 项 250 rad/s² 会积到 25 rad/s）：
        /// 线性 4 状态极点仍全 LHP（ζ≈0.70），但**非线性限速回放层 1 全部失败**——低增益把制动冲量
        /// 推后，参考在摆杆过零前就撞上臂速限幅，uTheta 一直为负，臂再也反不回来。
        /// 5000/300 在 24 rad/s 下通过全部门内状态；已测的 2500~4500（Kad=0.06·Ka）全部失败（见 CreateAccelerationLawDefaults）。
        /// 平衡包线 800 rad/s² 下 P 项饱和点 800/5000=0.16 rad，
        /// 捕获门 0.05 远在包线内。Kad=300 给 ψ 环 ζ≈0.57（C=0.048），4 状态极点全 LHP。
        /// <see cref="GravityOverPendulumLength"/> 的 196.2 是 l=0.05 m 的保守结构下限，不是本台架 g/l。
        /// </summary>
        public double Ka;

        /// <summary>**历史字段**：上一代加速度反馈律的摆杆角速度增益 Kad [rad/s² per rad/s]，控制律不再读取。默认 300（β=0.048、Ka=5000 时 ψ 环 ζ≈0.57；4 状态极点全 LHP）。</summary>
        public double Kad;

        /// <summary>**历史字段**：上一代加速度反馈律的摆杆角误差积分增益 [rad/s² per (rad·s)]，控制律不再读取。默认 0（PD 足够；大扰动时再开）。</summary>
        public double Kai;

        /// <summary>**历史字段**：上一代加速度反馈律的摆臂偏置回中增益 Kφ [rad/s² per rad]，控制律不再读取。默认 **1.0**（β=0.048 时 4.0 会给 4 状态模型引入右半平面复极点）。</summary>
        public double KPhi;

        /// <summary>**历史字段**：上一代加速度反馈律的摆臂阻尼增益 Kφ̇ [rad/s² per rad/s]，控制律不再读取。默认 **1.0**（同上；回中项在弱耦合下本身是发散源）。</summary>
        public double KPhiDot;

        /// <summary>外环加速度指令限幅 AccMax [rad/s²]。默认 **800**（台架实测 2000 rad/s² 无故障，取 40% 保守）。</summary>
        public double AccMax;

        /// <summary>
        /// **历史字段**：旧参考角速度限幅 ArmRateMax [rad/s]，默认 40。
        /// 自 2026-11-21 起控制律**不再读取**：平衡参考速度只由「S2 跟随误差窗口 / 实测伺服滞后」现算
        ///（0.85×0.25/9 ms ≈ 23.6 rad/s），起摆 / 回中各有自己的加速度权限。
        /// 保留只为旧参数文件读写往返与界面显示。
        /// </summary>
        public double ArmRateMax;

        /// <summary>符号 Sign。**不是可调项**：Resolve 时固定为 <see cref="PendulumEngine.BalanceLawSign"/>（本机 +1，平衡方向）。</summary>
        public double Sign;

        // ---- 内环 φ 参数（当前只用于参数合法性校验与界面显示）----
        // 误差 eφ = φ_ref - φ，输出 uφ 单位 rad（叠加到 φ_ref 上的修正量）。

        /// <summary>内环比例增益 [rad/rad]。默认 0.5。</summary>
        public double KpPhi;

        /// <summary>内环积分增益 [1/s]。默认 0。</summary>
        public double KiPhi;

        /// <summary>
        /// 内环微分增益 [s]，作用在 φ̇ 上，D-on-PV。默认 0.0009。
        /// <para>
        /// 它和 <see cref="TdLpMs"/> 一起决定内环的<b>离散</b>稳定性：低通把微分放大成
        /// 等效速度增益 KdΦ·(1−α)/dt（α = TdLp/(TdLp+dt)），2 kHz 下一拍延迟就能把闭环极点顶出单位圆。
        /// 上限见 <see cref="IsValid"/> 的离散稳定性校验，不要超过它。
        /// </para>
        /// </summary>
        public double KdPhi;

        // ---- 历史字段：旧「摆臂位置映射」律的增益，控制律不再读取 ----
        // 保留仅为旧参数文件往返与既有界面绑定；平衡环现由 PendulumEngine 内固定的状态反馈驱动，
        // 不再需要本结构体提供外环增益。

        /// <summary>历史字段（位置映射律外环 P，[rad臂/rad杆]），当前控制律不读取。</summary>
        public double Kp;

        /// <summary>历史字段（位置映射律外环 I，[1/s]），当前控制律不读取。</summary>
        public double Ki;

        /// <summary>历史字段（位置映射律外环 D，[s]），当前控制律不读取。</summary>
        public double Kd;

        // ---- 两环共用 ----

        /// <summary>
        /// 微分一阶低通时间常数 [ms]。默认 2 ms，500µs 周期下 α=0.8。同时用作 ω / φ̇ 反馈的低通。
        /// <para>
        /// 提醒：提高它能压低微分带来的离散增益，但内环闭环极点会随之变慢（极点趋近单位圆），
        /// 外环看到的相位滞后也变大。KdΦ=0.005 时单靠加大 TdLp 最好也只能把极点模压到 0.965，
        /// 到不了 0.95 —— 要压极点应当先降 <see cref="KdPhi"/>。
        /// </para>
        /// </summary>
        public double TdLpMs;

        /// <summary>旧 PID 内环的输出下限 [rad]；当前控制律不读取，只保留参数文件读写与界面显示（校验仍要求 OutMin &lt; OutMax）。</summary>
        public double OutMin;

        /// <summary>旧 PID 内环的输出上限 [rad]；当前控制律不读取，只保留参数文件读写与界面显示。</summary>
        public double OutMax;

        /// <summary>旧 PID 的输出变化率限幅 [rad/s]；当前控制律不读取，只保留参数兼容。</summary>
        public double OutRateLimit;

        /// <summary>旧 PID 的积分项输出上限 [rad]；当前控制律不读取，只保留参数兼容。</summary>
        public double MaxIntegral;

        /// <summary>旧 PID 的误差死区 [rad]；当前控制律不读取，只保留参数兼容。</summary>
        public double DeadBand;

        /// <summary>
        /// 旧参数文件里没有加速度律字段（读出来全 0）时返回 false。
        /// 调用方据此判断"要不要按内置默认补齐并提示用户重新整定"。
        /// </summary>
        public bool HasAccelerationLawGains()
        {
            return Ka != 0 || Kad != 0 || KPhi != 0 || KPhiDot != 0;
        }

        /// <summary>
        /// 加速度律的<b>历史保守下限</b> [1/s²] = 9.81 / 0.05，对应等效摆长 0.05 m 的短摆。
        /// <para>
        /// **它不是本台架的 g/l，也不该再当结构判据用**。真正的结构下界是
        /// <c>g/l ÷ |β|</c>（β = 摆臂角加速度→摆杆角加速度的耦合系数，本机 2026-11-20 实测 |β|≈2.6）：
        /// 本台架 g/l = 81.75、|β| = 0.24…2.8 ⇒ 下界约 29…341，随机构的 r/l 变化。
        /// 该常数保留只为旧参数文件 / 旧提示的兼容引用；<see cref="ResolveAccelerationLaw(double,double,out string)"/>
        /// 已改用标定值，耦合比未标定时如实给"无法给出下界"，不再拿这个数假装精确。
        /// </para>
        /// </summary>
        public const double GravityOverPendulumLength = 196.2;

        /// <summary>
        /// 加速度反馈律增益的补全与校核（无标定信息的旧入口）。
        /// <para>
        /// 加载（ParamStore）与运行期（PendulumEngine）共用这一份规则，不要在别处再写一遍 ——
        /// 两套规则会演变成「读进来合法、跑起来被改」。
        /// </para>
        /// <para>
        /// 本重载拿不到 g/l 与耦合比，因此**不施加结构下限**（不编造 196.2 这类假数）；
        /// 运行期由引擎按 SafetyLimits 的标定值调用三参数重载。缺项（0）与越界项仍回落内置默认。
        /// </para>
        /// </summary>
        /// <returns>true = 至少替换过一项（调用方据此提示现场重新整定）。</returns>
        public bool ResolveAccelerationLaw()
        {
            string unused;
            return ResolveAccelerationLaw(0.0, 0.0, out unused);
        }

        /// <summary>
        /// 加速度反馈律增益的补全与校核（带标定值）。<b>这是唯一的结构判据实现。</b>
        /// <para>
        /// 结构下界 = <c>g/l ÷ |β|</c>：闭环线性化 ψ̈ = (g/l)ψ + β·φ̈，P 反馈把 ψ̈ 的系数压成
        /// <c>(g/l) − |β|·Ka</c>，要落在左半平面必须 |β|·Ka &gt; g/l。β 的符号由
        /// <c>PendulumEngine.BalanceLawSign</c>（平衡方向）与 <c>CalibrationSign</c>（标定几何）分开约定；
        /// 本判据只用幅值，幅值来自 limits.radius_ratio 的标定值。
        /// </para>
        /// <para>
        /// <paramref name="gravityOverLength"/> 或 <paramref name="couplingAbs"/> 不可用（≤0 / 非有限）
        /// = 耦合比未标定：此时**不给结构下限**，只做有限性检查，并在
        /// <paramref name="structuralNote"/> 里明确说明"请先标定"，绝不拿历史常数冒充精确判据。
        /// </para>
        /// </summary>
        /// <param name="gravityOverLength">g/l = Mgl / JEq [1/s²]；未标定传 0。</param>
        /// <param name="couplingAbs">|r/l|（或实测 |β|）；未标定传 0。</param>
        /// <param name="structuralNote">结构判据的人话说明（含实际下界值，或"未标定无法给出"）。</param>
        public bool ResolveAccelerationLaw(double gravityOverLength, double couplingAbs, out string structuralNote)
        {
            PidGains def = CreateAccelerationLawDefaults();
            bool replaced = false;
            bool calibrated = ControlMath.IsFinite(gravityOverLength) && gravityOverLength > 0 &&
                              ControlMath.IsFinite(couplingAbs) && couplingAbs > 0;
            double structureFloor = calibrated ? gravityOverLength / couplingAbs : 0.0;
            structuralNote = calibrated
                ? string.Format(CultureInfo.InvariantCulture,
                    "结构下界 Ka > g/l÷|β| = {0:F2} ÷ {1:F4} = {2:F1}（g/l 与 |β| 均有标定值）。",
                    gravityOverLength, couplingAbs, structureFloor)
                : "耦合比 r/l 未标定：结构下界无法给出（不拿历史常数冒充），请先做 calibrate-radius 或在参数里填 limits.radius_ratio。";
            // Ka 的判据是结构性的：低于下界时闭环极点仍在右半平面。未标定时只要求有限且 >0。
            bool kaBad = !ControlMath.IsFinite(Ka) || Ka <= 0 || (calibrated && !(Ka > structureFloor));
            if (kaBad) { Ka = def.Ka; replaced = true; }
            // Kad 的下界按阻尼比给：ζ = Kad / (2·√(Ka − g/l))。取 ζ 下限 0.05（很松，只挡"几乎没有阻尼"）。
            // 未标定时没有 g/l，只要求 Kad 有限且非负 —— 不给假阻尼下限。
            double kadFloor = calibrated ? 0.1 * Math.Sqrt(Math.Max(0.0, Ka - structureFloor)) : 0.0;
            if (!(Kad >= kadFloor) || !ControlMath.IsFinite(Kad)) { Kad = def.Kad; replaced = true; }
            if (!ControlMath.IsFinite(Kai)) { Kai = def.Kai; replaced = true; }
            if (!(KPhi > 0) || !ControlMath.IsFinite(KPhi)) { KPhi = def.KPhi; replaced = true; }
            if (!(KPhiDot > 0) || !ControlMath.IsFinite(KPhiDot)) { KPhiDot = def.KPhiDot; replaced = true; }
            if (!(AccMax > 0) || !ControlMath.IsFinite(AccMax)) { AccMax = def.AccMax; replaced = true; }
            if (!(ArmRateMax > 0) || !ControlMath.IsFinite(ArmRateMax)) { ArmRateMax = def.ArmRateMax; replaced = true; }
            // NaN 对两个比较都成立，会落到这里被替换掉。
            if (Sign != 1.0 && Sign != -1.0) { Sign = def.Sign; replaced = true; }
            // 方向唯一来源 = PendulumEngine.BalanceLawSign（当前 +1，依据见该常量注释）。Sign 不再作为
            // 手改开关：这里钉成同一个值，保证参数文件写回 / 界面显示 / 运行期三者一致，
            // 不留下"看起来能调、实际无效"的旋钮。它**不**算整定项，不触发重新整定提示。
            Sign = PendulumEngine.BalanceLawSign;
            // 内环 φ 的离散闭环极点：跑出单位圆就整组回落内置默认（判据与 IsValid 共用一份）。
            if (!(InnerLoopMaxPole(KpPhi, KiPhi, KdPhi, TdLpMs, CycleConstants.CycleSeconds) < 1.0))
            {
                PidGains full = CreateDefault();
                KpPhi = full.KpPhi;
                KiPhi = full.KiPhi;
                KdPhi = full.KdPhi;
                TdLpMs = full.TdLpMs;
                replaced = true;
            }
            return replaced;
        }

        /// <summary>
        /// 校验一组增益是否可用。失败时 reason 里写清是哪一项不合法，供前端直接显示。
        /// </summary>
        public bool IsValid(out string reason)
        {
            // 全零增益一律当"未初始化"，不是合法增益。UI/CLI 靠这条区分"没配过"和"配成 0"。
            // 加速度律字段配了值时就以它们为准：历史 Kp/Ki/Kd 已不参与控制，
            // 不该因为它们恰好是 0 就把一组合法的加速度律增益判成"没配过"。
            if (!HasAccelerationLawGains() && Kp == 0 && Ki == 0 && Kd == 0)
            {
                reason = "增益全零视为未初始化，不是合法增益（加速度律 Ka/Kad，或旧版 Kp/Ki/Kd）。";
                return false;
            }
            if (!ControlMath.IsFinite(Kp) ||
                !ControlMath.IsFinite(Ki) ||
                !ControlMath.IsFinite(Kd))
            {
                reason = "Kp/Ki/Kd（外环 θ）必须是有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(KpPhi) ||
                !ControlMath.IsFinite(KiPhi) ||
                !ControlMath.IsFinite(KdPhi))
            {
                reason = "KpPhi/KiPhi/KdPhi（内环 φ）必须是有限数。";
                return false;
            }
            // 加速度律字段：旧参数文件里没有这些键，读出来全 0 —— 那是「未配置」，
            // 由 PendulumEngine 按内置默认补齐，不是非法值，不能因此拒载整个文件。
            // 只有真的配了值（非全零）时才要求每一项都是有限数。
            if (HasAccelerationLawGains())
            {
                if (!ControlMath.IsFinite(Ka) || !ControlMath.IsFinite(Kad) || !ControlMath.IsFinite(Kai) ||
                    !ControlMath.IsFinite(KPhi) || !ControlMath.IsFinite(KPhiDot) ||
                    !ControlMath.IsFinite(AccMax) || !ControlMath.IsFinite(ArmRateMax) ||
                    !ControlMath.IsFinite(Sign))
                {
                    reason = "加速度律增益（Ka/Kad/Kai/KPhi/KPhiDot/AccMax/ArmRateMax/Sign）必须是有限数。";
                    return false;
                }
            }
            if (!ControlMath.IsFinite(TdLpMs) || TdLpMs < 0)
            {
                reason = "TdLpMs 必须是 ≥0 的有限数，单位 ms。";
                return false;
            }
            // 内环 φ 的离散闭环稳定性：极点必须全部落在单位圆内。
            // 这一条挡的是“参数看着都在合理范围、按 2 kHz 推一拍却是发散的”那种组态
            // （例：KdΦ=0.005 在 dt=0.5 ms / TdLp=2 ms 下 |z|max=2.617，每拍放大）。
            // 判据与 ResolveAccelerationLaw 的回落共用同一份 InnerLoopMaxPole，不要在别处再写一遍。
            {
                double dtStab = CycleConstants.CycleSeconds;
                double maxPole = InnerLoopMaxPole(KpPhi, KiPhi, KdPhi, TdLpMs, dtStab);
                if (!(maxPole < 1.0))
                {
                    reason = DescribeInnerLoopInstability(maxPole, dtStab);
                    return false;
                }
            }
            if (!ControlMath.IsFinite(OutMin) || !ControlMath.IsFinite(OutMax) || !(OutMin < OutMax))
            {
                reason = "OutMin 必须小于 OutMax，且两者都是有限数。";
                return false;
            }
            if (!ControlMath.IsFinite(OutRateLimit))
            {
                reason = "OutRateLimit 必须是有限数，≤0 表示关闭限速。";
                return false;
            }
            if (!ControlMath.IsFinite(MaxIntegral))
            {
                reason = "MaxIntegral 必须是有限数，≤0 表示自动取 OutMax-OutMin。";
                return false;
            }
            if (!ControlMath.IsFinite(DeadBand))
            {
                reason = "DeadBand 必须是有限数，≤0 表示关闭死区。";
                return false;
            }
            reason = null;
            return true;
        }

        // ============ 内环 φ 的离散闭环稳定性（IsValid 与 Resolve 共用）============
        //
        // 被控对象取最坏情况：单拍延迟的位置跟随（指令下一拍才到位）
        //     φ(k+1) = φ_ref(k) + u(k)
        // 按内环跟踪环的形式（Ki=0 时 u = Kpφ·e + Kdφ·f）推导：
        //     f(k) = α·f(k-1) − g·(φ(k) − φ(k-1))
        //     α = TdLp/(TdLp+dt)，g = (1−α)/dt
        // z 域上 F(z) = −g·(z−1)/(z−α)·Φ(z)，闭环特征式（Ki=0）为
        //     z² + (Kpφ − α + Kdφ·g)·z − (Kpφ·α + Kdφ·g) = 0
        // 另有一个 z=0 的死拍极点，不影响稳定性。Kiφ≠0 时梯形积分再引入一个 (z−1) 极点，
        // 特征式升到三阶：
        //     z³ + (Kpφ + Kdφ·g − 1 − α + Kiφ·dt/2)·z²
        //        + (α − Kpφ·(1+α) − 2·Kdφ·g + Kiφ·dt/2·(1−α))·z
        //        + (Kpφ·α + Kdφ·g − Kiφ·dt/2·α) = 0
        // 判据 = 全部极点 |z| < 1。dt 取项目固定控制周期 CycleConstants.CycleSeconds（500µs）。

        /// <summary>微分一阶低通的离散系数 α = TdLp/(TdLp+dt)（内环增益校验用）。</summary>
        private static double InnerLoopAlpha(double tdLpMs, double dt)
        {
            double td = tdLpMs > 0 ? tdLpMs * 0.001 : 0.0;
            return td > 0 ? td / (td + dt) : 0.0;
        }

        /// <summary>
        /// 内环 φ 闭环极点的最大模。&lt;1 稳定；≥1（含 NaN）即该组增益不可用。
        /// </summary>
        private static double InnerLoopMaxPole(double kp, double ki, double kd, double tdLpMs, double dt)
        {
            double alpha = InnerLoopAlpha(tdLpMs, dt);
            double bg = kd * (1.0 - alpha) / dt;
            if (ki == 0.0)
            {
                double p = kp - alpha + bg;
                double q = -(kp * alpha + bg);
                double disc = p * p - 4.0 * q;
                if (disc >= 0.0)
                {
                    double s = Math.Sqrt(disc);
                    return Math.Max(Math.Abs((-p + s) * 0.5), Math.Abs((-p - s) * 0.5));
                }
                return Math.Sqrt(q);            // 共轭复根，模 = √q
            }
            // 三阶特征式，系数按上面推导搬过来
            return CubicMaxRoot(
                kp + bg - 1.0 - alpha + ki * dt * 0.5,
                alpha - kp * (1.0 + alpha) - 2.0 * bg + ki * dt * 0.5 * (1.0 - alpha),
                kp * alpha + bg - ki * dt * 0.5 * alpha);
        }

        /// <summary>z³ + a2·z² + a1·z + a0 的最大根模（闭式，无迭代）。</summary>
        private static double CubicMaxRoot(double a2, double a1, double a0)
        {
            // 先压成 y³ + p·y + q = 0，z = y − a2/3
            double shift = a2 / 3.0;
            double p = a1 - a2 * a2 / 3.0;
            double q = 2.0 * a2 * a2 * a2 / 27.0 - a2 * a1 / 3.0 + a0;
            if (p == 0.0 && q == 0.0) return Math.Abs(shift);   // 三重根
            double disc = q * q * 0.25 + p * p * p / 27.0;
            double m = 0.0;
            if (disc > 0.0)
            {
                // 一实根 + 一对共轭复根
                double s = Math.Sqrt(disc);
                double u = Cbrt(-q * 0.5 + s);
                double v = Cbrt(-q * 0.5 - s);
                m = Math.Abs(u + v - shift);
                double re = -0.5 * (u + v) - shift;
                double im = Math.Sqrt(3.0) * 0.5 * (u - v);
                double mag = Math.Sqrt(re * re + im * im);
                if (mag > m) m = mag;
            }
            else
            {
                // 三个实根：y = 2·√(−p/3)·cos(θ/3 − 2πk/3)
                double r = Math.Sqrt(-p / 3.0);
                double arg = -q / (2.0 * r * r * r);
                if (arg > 1.0) arg = 1.0;
                else if (arg < -1.0) arg = -1.0;
                double theta = Math.Acos(arg);
                for (int k = 0; k < 3; k++)
                {
                    double mag = Math.Abs(2.0 * r * Math.Cos((theta + 2.0 * Math.PI * k) / 3.0) - shift);
                    if (mag > m) m = mag;
                }
            }
            return m;
        }

        private static double Cbrt(double x)
        {
            return x >= 0 ? Math.Pow(x, 1.0 / 3.0) : -Math.Pow(-x, 1.0 / 3.0);
        }

        /// <summary>单调谓词：求 ok(x) 为真的最大 x，lo 必须为真；hi 也为真时直接返回 hi。</summary>
        private static double LargestOk(double lo, double hi, Func<double, bool> ok)
        {
            if (!ok(lo)) return double.NaN;
            if (ok(hi)) return hi;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ok(mid)) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>上区间谓词：求 ok(x) 为真的最小 x；hi 都不成立时返回 NaN。</summary>
        private static double SmallestOk(double lo, double hi, Func<double, bool> ok)
        {
            if (ok(lo)) return lo;
            if (!ok(hi)) return double.NaN;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ok(mid)) hi = mid; else lo = mid;
            }
            return hi;
        }

        /// <summary>
        /// 内环不稳定的中文原因：说清是哪组增益、极点模多大、临界值在哪、怎么改。
        /// 只在不稳定分支调用（含一次粗扫 + 三次二分），不进周期路径。
        /// </summary>
        private string DescribeInnerLoopInstability(double maxPole, double dt)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            // 结构体内的 lambda 不能捕获 this，先把要用到的字段拷成局部量（CS1673）。
            double kp = KpPhi, ki = KiPhi, kd = KdPhi, tdLp = TdLpMs;
            double alpha = InnerLoopAlpha(tdLp, dt);
            double kdLimit = LargestOk(0.0, 0.05,
                x => InnerLoopMaxPole(kp, ki, x, tdLp, dt) < 1.0);
            double kdComfort = LargestOk(0.0, 0.05,
                x => InnerLoopMaxPole(kp, ki, x, tdLp, dt) <= 0.95);
            double tdLimit = SmallestOk(0.05, 500.0,
                x => InnerLoopMaxPole(kp, ki, kd, x, dt) < 1.0);
            double kpLimit = LargestOk(0.0, 10.0,
                x => InnerLoopMaxPole(x, ki, kd, tdLp, dt) < 1.0);
            double kiLimit = LargestOk(0.0, 1e6,
                x => InnerLoopMaxPole(kp, x, kd, tdLp, dt) < 1.0);

            string head = "内环 φ 的离散闭环不稳定：KpΦ=" + ControlMath.Gain(kp)
                + "、KiΦ=" + ControlMath.Gain(ki)
                + "、KdΦ=" + ControlMath.Gain(kd)
                + "、TdLp=" + tdLp.ToString("0.###", inv)
                + " ms、dt=" + (dt * 1000.0).ToString("0.###", inv) + " ms 时，"
                + "闭环极点 |z|max=" + maxPole.ToString("0.0000", inv)
                + " ≥ 1（临界值 1）。"
                + "有效微分增益 KdΦ·(1−α)/dt=" + (kd * (1.0 - alpha) / dt).ToString("0.000", inv)
                + "（α=" + alpha.ToString("0.000", inv) + "）";

            // 诊断顺序：先看降 KdΦ 能不能救（这是已知的病根），再看 KpΦ，最后看 KiΦ。
            // 每一条只在"确实要改这个参数"时才给建议 —— 否则提示没有可操作性。
            string fix;
            bool tdHelps = !double.IsNaN(tdLimit) && tdLimit > tdLp;
            // 建议值必须"照着改一定站得住"：KdΦ 向下取整、TdLp 向上取整，
            // 否则 0.001125 会被四舍五入成 0.00113，用户照填反而落在不稳定侧。
            if (!double.IsNaN(kdLimit) && kdLimit < kd)
            {
                fix = "；KdΦ 需 ≤ " + (Math.Floor(kdLimit * 1e6) / 1e6).ToString("0.######", inv);
                if (!double.IsNaN(kdComfort) && kdComfort < kdLimit)
                    fix += "（留裕量建议 ≤ " + (Math.Floor(kdComfort * 1e6) / 1e6).ToString("0.######", inv)
                        + "，对应 |z| ≤ 0.95）";
                if (tdHelps)
                    fix += "；或 TdLp ≥ " + (Math.Ceiling(tdLimit * 100.0) / 100.0).ToString("0.00", inv)
                        + " ms（保留当前 KdΦ）";
            }
            else if (!double.IsNaN(kpLimit) && kpLimit < kp)
            {
                fix = "；KdΦ 降到 0 也不够，KpΦ 需 ≤ " + (Math.Floor(kpLimit * 1e4) / 1e4).ToString("0.####", inv);
                if (tdHelps)
                    fix += "；或 TdLp ≥ " + (Math.Ceiling(tdLimit * 100.0) / 100.0).ToString("0.00", inv) + " ms";
            }
            else if (!double.IsNaN(kiLimit) && kiLimit < Math.Abs(ki))
            {
                fix = "；KdΦ/KpΦ 都不是病根，是积分项：KiΦ 需 ≤ "
                    + (Math.Floor(kiLimit * 1e4) / 1e4).ToString("0.####", inv);
            }
            else
            {
                fix = "；这组增益怎么调都站不住，请回到内置默认（KpΦ=0.5 / KiΦ=0 / KdΦ=0.0009 / TdLp=2 ms）重新整定";
            }
            return head + fix + "。";
        }

        /// <summary>值拷贝一份，方便前端在改参数前留底。</summary>
        public PidGains Clone()
        {
            return this;
        }

        /// <summary>
        /// 起手值（平衡外环四项 Ka / Kad / Kφ / Kφ̇ 与 Kai 自平衡环改由固定状态反馈接管后只作历史值）：
        /// Ka=5000 / Kad=300 / Kai=0 / Kφ=1.0 / Kφ̇=1.0，AccMax=800 rad/s²，
        /// ArmRateMax=40 rad/s（**历史字段，不再参与控制**），Sign 由 <c>PendulumEngine.BalanceLawSign</c> 固定（本机 +1，不可手改）；
        /// 平衡态专用包线只有一条物理约束（AccMax=800，再按 0.85·PhiErrMaxRad/9 ms 收紧到 23.6 rad/s，见 PendulumEngine）；
        /// 内环 0.5 / 0 / 0.0009；TdLp 2 ms；内环输出限幅 ±0.35 rad。
        /// </summary>
        public static PidGains CreateDefault()
        {
            PidGains g = CreateAccelerationLawDefaults();
            g.KpPhi = 0.5;
            g.KiPhi = 0;
            // 0.0009 而不是老的 0.005：2 kHz + 单拍延迟下 0.005 的闭环极点模是 2.617（每拍放大），
            // 0.0009 是 0.902（实测 0.896），仍在 0.95 目标线以内且留了裕量。上限 0.001125。
            g.KdPhi = 0.0009;
            g.TdLpMs = 2.0;
            // OutMin/OutMax 只保留参数兼容；当前控制律的加速度限幅是 AccMax [rad/s²]。
            g.OutMin = -0.35;
            g.OutMax = 0.35;
            g.OutRateLimit = 0;
            g.MaxIntegral = 0;
            g.DeadBand = 0;
            // 历史字段保持旧起手值：旧参数文件读进来再存出去时不会无故改动这几项。
            g.Kp = 4.0;
            g.Ki = 0;
            g.Kd = 0.06;
            return g;
        }

        /// <summary>
        /// 加速度反馈律的内置默认值。旧参数文件没有对应字段（读出来全 0）时按它补齐。
        /// <para>
        /// 注意：平衡环已由固定状态反馈接管，这组值现在只用于旧参数文件兼容与界面历史显示，
        /// 不再进入平衡控制律；AccMax / ArmRateMax 两项例外，它们仍是状态反馈输出的限幅。
        /// </para>
        /// <para>
        /// 依据（2026-11-21 第五轮 19 拍台架实测 + 脉冲标定 + 第十二/十三轮臂速夹缝收口）：
        /// 物理耦合 |C_phys| = 0.048（19 拍 0x6064 反算 +0.0499），g/l = 81.75。
        /// 结构下界 Ka &gt; g/l/|β| ≈ 1703；Ka=5000 留约 2.9 倍结构裕量。
        /// 第十三轮验证过把 Ka 降到 2500/180 的想法：线性 4 状态极点确实全 LHP（ζ≈0.70），
        /// 但非线性限速回放（层 1 两组 × C=0.048/0.0566/0.058）全部失败。机理：低增益的初始
        /// 制动加速度更小，参考在摆杆过零前就撞到 24 rad/s 限幅；此后实际臂加速度≈0，uTheta
        /// 仍为负（ψ&gt;0），aCmd 无法反号，臂停在限幅上不再反向。5000 的初始冲量足够让摆杆在
        /// 臂饱和前过零，uTheta 反号后臂能及时刹停/反向；同样 24 rad/s 下 5000/300 通过全部
        /// 门内状态，且 32 拍/run3 的峰值臂速反而更低（13.4/4.6 vs 低增益的更高峰值）。
        /// 执行器包线只有一条仍在生效的：AccMax=800（台架实测 2000 无故障，取 40%）；
        /// 平衡参考速度由 PendulumEngine 按 0.85·PhiErrMaxRad/9 ms 收紧到 23.6 rad/s。
        /// ArmRateMax=40 自 2026-11-21 起只作历史显示（不再参与控制）。
        /// Kad=300 ⇒ C=0.048 时 C·Kad=14.4、ω_n=√158.25=12.58、ζ≈0.57；
        /// 4 状态极点 −6.451±10.424j / −0.249±0.694j（C=0.058 时 −8.013±11.689j / −0.187±0.610j），全 LHP。
        /// Kφ/Kφ̇=1：与 5000/300 组合的 4 状态极点全左半平面，因此保留 1.0。
        /// </para>
        /// </summary>
        public static PidGains CreateAccelerationLawDefaults()
        {
            PidGains g = new PidGains();
            // Ka=5000：结构下界 1703，留 ~2.9 倍。第十三轮 2500/180 的非线性回放层 1 全败（机理见上）；
            // 5000 在 24 rad/s 臂速下通过全部门内状态；已测的 2500~4500（Kad=0.06·Ka）全部失败。
            g.Ka = 5000.0;
            // Kad=300：C=0.048 时 C·Kad=14.4、ω_n=12.58、ζ≈0.57；4 状态极点全 LHP。
            g.Kad = 300.0;
            g.Kai = 0.0;
            // Kφ/Kφ̇=1.0：β=0.048 时 4.0 会在 4 状态模型产生右半平面复极点（+1.1~+2.4）。
            g.KPhi = 1.0;
            g.KPhiDot = 1.0;
            // 执行器包线：台架实测 2000 rad/s² 无丢步，AccMax 取 40% 保守 = 800。
            // ArmRateMax 保留旧值 40 只为参数文件往返 / 界面显示，控制律不读（见字段注释）。
            g.AccMax = 800.0;
            g.ArmRateMax = 40.0;
            // 方向不是可调项：唯一来源是 PendulumEngine.BalanceLawSign（平衡方向，本机 +1）。
            g.Sign = PendulumEngine.BalanceLawSign;
            return g;
        }
    }
}
