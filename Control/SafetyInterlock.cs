using System;
using System.Globalization;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 安全联锁阈值。收窄随时可以，放宽要打日志留痕。
    /// </summary>
    public struct SafetyLimits
    {
        /// <summary>双缓冲代号。SetSafetyLimits 时自增，周期线程在拍首顶换。</summary>
        public uint Generation;

        /// <summary>S1 硬限：|wrapPi(θ-π)| 超过它立即停。默认 1.05 rad ≈ 60°。</summary>
        public double ThetaTripRad;

        /// <summary>S13 软限：平衡中偏离直立超过它连续一定拍数判平衡丢失。默认 0.40 rad。</summary>
        public double ThetaLostRad;

        /// <summary>S2 跟随误差上限 |φ_target - φ| [rad]。默认 0.25。</summary>
        public double PhiErrMaxRad;

        /// <summary>
        /// 摆臂软限位下限 = **当前 0x6064 坐标系下的原始计数**（不是相对 0 点的偏移，不换算角度）。
        /// <para>
        /// 这是摆臂行程的唯一软件单位：用户只给过「摆臂一圈多少计数」，从没给过摆臂行程对应多少机械角度，
        /// 所以任何 deg / rad 口径的行程限位都是程序自己编的，一律不再存在。零点只是坐标原点，
        /// 改零点不搬限位；左右限位可以不对称，也不假设 0 点在中间。
        /// </para>
        /// <para>
        /// **「绝对计数」不等于「跨掉电仍对应同一物理位置」**：EC1 是开环步进，0x6064 只是驱动器内部
        /// 指令位置估计，手册明确每次上电要用 HM 或当前位置置零才能与机械零点对齐。若上电后 raw 坐标系
        /// 变了，本字段与零点/右限位一起作废，必须显式「规整化左中右」或重新采集；控制层绝不按
        /// 「这次从 0 开始」自动推算偏移。
        /// </para>
        /// <para>
        /// 默认值 = <see cref="DefaultArmMinCount"/>（全量占位 = 「还没采集」）：设备本身没有硬限位，
        /// 现场必须点动到实际左右边界后用「设置左 / 右限位」把真实计数写进来。
        /// 控制环在 counts 域把目标钳到这个闭区间（只对起摆 / 平衡等自动运动生效）。
        /// </para>
        /// </summary>
        public int ArmMinCount;

        /// <summary>摆臂软限位上限 = 当前 raw 坐标系下的 0x6064 计数，说明见 <see cref="ArmMinCount"/>。</summary>
        public int ArmMaxCount;

        /// <summary>
        /// 软限位计数的默认值 = int32 两端（= 「尚未采集」的占位）。合法判据只有 <c>min &lt; max</c>，
        /// 不设任何角度 / 行程 / 量程上限 —— 行程与位置计数由现场机械与驱动器决定。
        /// </summary>
        public const int DefaultArmMinCount = int.MinValue;

        /// <summary>软限位默认上限（= int32 上端，全量占位 = 尚未采集）。</summary>
        public const int DefaultArmMaxCount = int.MaxValue;

        /// <summary>
        /// 摆臂-摆杆耦合比 r/l 的**量级哨兵**上界（无量纲），**不是物理定律**。
        /// r 是臂轴到摆轴的距离、l 是摆杆等效摆长：两者都是独立几何量，r 完全可以大于 l
        /// （长臂 + 短摆）。典型机构在 0.05…5；超过 10 更可能是单位 / 每转计数填错（把毫米当米、
        /// CPR 差 10 倍），而不是真实机构。
        /// <para>
        /// 历史值 1.5 是照着早期一台 r/l≈0.24 的小臂机构定的，把「那台机器」当成了物理上限；
        /// 2026-11-20 台架数据回归的 |β| 在 0.65…2.8，会被旧上限整段判非法、连存都存不进去。
        /// 现在放宽到 10：1.5…10 的值**按真实值使用**（捕获门的动量耦合项按它算），
        /// 只有 >10 才当作量级错误拒绝并提示核对 r / l / CPR。
        /// </para>
        /// </summary>
        public const double MaxReasonableRadiusRatio = 10.0;

        /// <summary>
        /// 软限位是否仍是「尚未采集」的全量占位（= 两个默认端值）。日志 / 界面据此如实标注。
        /// 「清空位置标定」会把软限位恢复到这个状态；此时自动运动必须被上层拒绝，不能把全量区间
        /// 当成已标定的行程。
        /// </summary>
        public bool ArmLimitsArePlaceholder
        {
            get { return ArmMinCount == DefaultArmMinCount && ArmMaxCount == DefaultArmMaxCount; }
        }

        /// <summary>
        /// 软限位区间（counts 域）是否可用。只要求两端严格递增，**不设任何量程 / 角度上限**。
        /// 「左限位 &lt; 零点 &lt; 右限位」的三点关系由界面 / 参数文件层校验（那里才知道零点）。
        /// </summary>
        public bool ArmLimitsValid
        {
            get { return ArmMinCount < ArmMaxCount; }
        }

        /// <summary>
        /// 写进 0x607A 的逐拍 slew 天花板 [rad/s]（参数 / 界面契约字段）。
        /// <para>
        /// **控制路径不再读它当限速**：引擎按 <see cref="ArmDriveCeilingRadPerSec"/>（驱动器 0x6080 的
        /// 3000 rpm = 314.159 rad/s）执行，低于该值的配置一律抬到物理天花板。本字段只保留作
        /// 参数文件 / 参数对话框 / CLI 的既有契约（旧文件里有这个键，读进来不能报错）。
        /// </para>
        /// <para>
        /// 2026-11-21 移除旧口径（12 / 20 / 50 rad/s）：台架实测臂峰值 **98.7 rad/s**、驱动器零故障，
        /// 手册唯一的速度天花板是 0x6080 = 3000 rpm。旧出厂值把机器限死在驱动器能力的 4%~16%，
        /// 而且一旦速度积分被直流顶到钳位，交变的刹车指令就被**半波整流**成单向推力 ——
        /// 起摆一旦过能（E≈3.5 J）就再也抽不回来，90 s 超时、能量一分不掉。
        /// 旧出厂值在载入时按迁移处理（见 ParamStore 的 legacy 摆臂限速迁移），不会静默限速。
        /// </para>
        /// </summary>
        public double OmegaArmMaxRadPerSec;

        /// <summary>
        /// 摆臂角速度的物理天花板 [rad/s] = 驱动器 0x6080 的 3000 rpm。
        /// <para>
        /// 这是手册给出的**唯一**速度上限（3000 × 2π / 60 = 314.159 rad/s）。软件的任何 slew / 限速
        /// 都只能取它：再编一个更小的数就是无物理依据的软件限速（台架实测 98.7 rad/s 零故障）。
        /// </para>
        /// </summary>
        public const double ArmDriveCeilingRadPerSec = 314.15926535897932;

        /// <summary>
        /// 摆杆绕转轴等效转动惯量 [kg·m²]，只用于起摆的能量估计。
        /// <para>
        /// 能量法对**整体尺度**不敏感（E 与 E* 同乘一个系数，Δ 同步缩放，被 Ke 吸收），但对
        /// **J_eq 与 Mgl 的比值**极其敏感：比值决定等效摆长与固有角频率，
        /// 即 ω_n = √(mgl/J_eq)。出厂值按「等效点质量 m 落在重心距 l 处」给：
        /// J_eq = m·l²，mgl = m·g·l ⇒ ω_n² = g/l。
        /// </para>
        /// 默认 l = 0.12 m（台架实测自由摆动 f_n = 1.422 Hz ⇒ l = g/(2πf_n)² ≈ 0.123 m），
        /// 故 J_eq = Mgl/ω_n² = 1.0/81.75 = 0.0122324 kg·m²。换摆杆必须按 J_eq = m·l²、mgl = m·g·l 同步给。
        /// </summary>
        public double JEq;

        /// <summary>
        /// 摆杆重力矩系数 m·g·l [N·m]，起摆能量估计与目标能量 E* = 2·mgl·(1+ε) 都用它。
        /// <para>
        /// 默认 1.0 N·m 对应等效点质量 m ≈ 0.85 kg @ l = 0.12 m（1.0 / (9.81 × 0.12)）。
        /// 这个质量是**能量尺度约定**，不是称出来的：能量法对整体尺度不敏感，改质量时按
        /// J_eq = m·l² 与 Mgl = m·g·l 成对缩放，ω_n 与整组行为不变。
        /// </para>
        /// </summary>
        public double Mgl;

        /// <summary>
        /// 摆臂轴到摆杆轴的距离 r 与等效摆长 l 的比值（无量纲，r/l）。**0 = 未标定**。
        /// <para>
        /// 能量法本身不读它（φ̈ = −u_E·sign(θ̇·cosθ) 与 r 无关），但**能量注入的绝对量级**正比于
        /// m·l·r：每周期注入 ΔE = 4·(m·l·r)·u_E·G(θ)，G(θ) 在 θ=90° 处约等于 1。所以
        /// 「起摆爬到某个幅值就上不去」到底是权限不够还是摩擦吃掉，只有知道 r/l 才能算清：
        /// 注入率与 u_E 成正比、与 r/l 成正比，而损耗与幅值成正比，两者相等就是极限环幅值。
        /// </para>
        /// <para>
        /// 取值来源只有两种，日志里如实打印，不许用默认值冒充实测：
        /// ① 机构实测 —— 量出臂轴到摆轴的距离 r，再除以等效摆长 l = g/ω_n²（由自由摆动频率辨识）；
        /// ② 默认未标定 —— 本字段 0，日志打印「r/l 未标定」，起摆仍可跑，但注入量级无法预测。
        /// 几何上 r 必须小于 l 的量级（摆杆挂在臂上，r 是臂的半径，l 是摆杆重心距），
        /// 因此 0 &lt; r/l ≤ 1.5 之外的值一律按「明显不合理」拒收。
        /// </para>
        /// </summary>
        public double RadiusRatio;

        /// <summary>校验一组安全阈值。失败时 reason 写清哪一项不合法。</summary>
        public bool IsValid(out string reason)
        {
            if (!ControlMath.IsFinite(ThetaTripRad) || ThetaTripRad <= 0)
            {
                reason = "ThetaTripRad 必须是 >0 的有限数，单位 rad。";
                return false;
            }
            if (!ControlMath.IsFinite(ThetaLostRad) || ThetaLostRad <= 0)
            {
                reason = "ThetaLostRad 必须是 >0 的有限数，单位 rad。";
                return false;
            }
            if (ThetaLostRad > ThetaTripRad)
            {
                reason = "ThetaLostRad 不能大于 ThetaTripRad（软限先于硬限触发才有意义）。";
                return false;
            }
            if (!ControlMath.IsFinite(PhiErrMaxRad) || PhiErrMaxRad <= 0)
            {
                reason = "PhiErrMaxRad 必须是 >0 的有限数，单位 rad。";
                return false;
            }
            if (!ArmLimitsValid)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "摆臂软限位不合法：ArmMinCount={0} / ArmMaxCount={1}（单位 = 当前 raw 坐标系下的 0x6064 计数）。" +
                    "判据只有一条：ArmMinCount < ArmMaxCount；不设任何角度 / 量程上限 —— " +
                    "位置由现场机械与驱动器决定。请重新采集或输入左右限位。",
                    ArmMinCount, ArmMaxCount);
                return false;
            }
            if (!ControlMath.IsFinite(OmegaArmMaxRadPerSec) || OmegaArmMaxRadPerSec <= 0)
            {
                reason = "OmegaArmMaxRadPerSec 必须是 >0 的有限数，单位 rad/s。";
                return false;
            }
            if (!ControlMath.IsFinite(JEq) || JEq <= 0)
            {
                reason = "JEq 必须是 >0 的有限数，单位 kg·m²（起摆能量估计用）。";
                return false;
            }
            if (!ControlMath.IsFinite(Mgl) || Mgl <= 0)
            {
                reason = "Mgl 必须是 >0 的有限数，单位 N·m（起摆能量估计用）。";
                return false;
            }
            // 耦合比 r/l：0 = 未标定（合法，日志会如实打「未标定」）；配了就必须是物理上说得通的正数。
            // 上界 1.5 的依据：r 是臂轴到摆轴的距离、l 是摆杆重心距，摆杆挂在臂上，r 大于 l 就不成立。
            if (!ControlMath.IsFinite(RadiusRatio) || RadiusRatio < 0 || RadiusRatio > MaxReasonableRadiusRatio)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "摆臂-摆杆耦合比 r/l 必须在 [0,{0:F2}] 内且有限（0 = 未标定）：读到的值是 {1}。"
                    + "r 是臂轴到摆轴的距离、l 是摆杆重心距，摆杆挂在臂上时 r 不可能远大于 l。",
                    MaxReasonableRadiusRatio, RadiusRatio);
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>值拷贝一份，方便前端在改参数前留底。</summary>
        public SafetyLimits Clone()
        {
            return this;
        }

        /// <summary>起手值，照控制层契约 §2.3 与 §4.3。</summary>
        public static SafetyLimits CreateDefault()
        {
            SafetyLimits s = new SafetyLimits();
            s.ThetaTripRad = 1.05;
            s.ThetaLostRad = 0.40;
            s.PhiErrMaxRad = 0.25;
            // 摆臂软限位只存当前 raw 坐标系下的 0x6064 计数：默认 = 全量占位（尚未采集），不拿任何角度换算当成行程。
            // 掉电后 raw 坐标可能整体平移；本字段不会自动调整，必须由上层显式规整化或重采。
            s.ArmMinCount = DefaultArmMinCount;
            s.ArmMaxCount = DefaultArmMaxCount;
            // 参数 / 界面契约字段：控制路径按 ArmDriveCeilingRadPerSec（0x6080 = 3000 rpm）执行，
            // 这里给出厂值取 100（仍高于台架实测峰值 98.7 rad/s）只是为了不越过参数对话框的 0~100 量程。
            // 历史 12 / 20 / 50 是拍出来的软件限速，已删除并由 ParamStore 迁移。
            s.OmegaArmMaxRadPerSec = 100.0;
            // 能量法两件套必须成对给：比值决定 ω_n，单项改一个 = 把能量尺度算错。
            // 旧默认 1.0/1.0 隐含 ω_n = 1 rad/s（等效摆长 9.81 m），与台架实测 1.422 Hz（ω_n = 8.93 rad/s）
            // 差 8.9 倍：目标能量 2·mgl 会在摆杆只有 ω ≈ 2 rad/s 时被判"已到顶"，能量泵随即反向抽能，
            // 起摆永远上不去。默认按 l = 0.12 m（实测等效摆长）、Mgl = 1.0 N·m（等效质量 ≈0.85 kg）给。
            s.JEq = 0.0122324159;
            s.Mgl = 1.0;
            // 耦合比 r/l 出厂**不猜**：台架上从没标定过（历史拟合值 0.015~0.021 与几何估计 ~0.4 差 20 倍，
            // 那个矛盾本身说明拟合被臂加速度的量化噪声污染了）。0 = 未标定，起摆照跑，
            // 日志打印「r/l 未标定」，等机构实测后由参数文件 limits.radius_ratio 填进来。
            s.RadiusRatio = 0.0;
            return s;
        }
    }

    /// <summary>
    /// 一拍联锁所需的事实。全是标量，由周期线程填好再交给 SafetyInterlock，判据不散落在控制律里。
    /// </summary>
    internal struct SafetyInputs
    {
        public EngineState State;
        public double ThetaFromUprightRad;
        public double OmegaRadPerSec;
        public double PhiRad;
        public double PhiTargetRad;
        /// <summary>起始位置（home），坐标定义下恒为 0（使能自动归零）。只随帧带出，联锁判据不使用。</summary>
        public double PhiHomeRad;
        public ushort StatusWord;
        public ushort ErrorCode;

        /// <summary>0x6061 运行模式状态。必须 == 8（CSP），不是就按使能/模式丢失处理，不降级 PP/PV。</summary>
        public sbyte ModesDisplay;

        // 全局 WKC（expected/actual）照实进 Telemetry / Diagnostics / FaultInfo，但不进联锁判据：
        // S5 只看自有两台是否还在响应，旁观从站掉 WKC 不触发 Fault。
        /// <summary>
        /// 自有两台（GCAN + 摆臂步进）本拍是否还在响应。S5 只看这一条：
        /// 旁观从站掉 WKC 是诊断事件，不触发 Fault（总线共享，别人的设备波动不该让倒立摆停机）。
        /// </summary>
        public bool OwnedSlavesResponding;

        /// <summary>自有两台本拍是否都在 OP。S7 只看这一条，旁观从站掉出 OP 不触发 Fault。</summary>
        public bool OwnedSlavesInOp;

        /// <summary>
        /// **逐拍**连续无有效过程数据的拍数：本拍坏 +1、本拍好**必须清零**。
        /// <para>
        /// 这是 S6 的唯一输入契约。**它不是 SDK 的 PDO 事件计数**：SDK 侧只在「刚达阈值」和
        /// 之后每 +100 拍各发一次事件（实测 20 → 120 → 220），且 fault-reset 停总线时该计数
        /// 不会被清掉；拿事件值来判 S6 会在复位后第一拍用旧值立刻再跳 Fault，现场只能断线重连。
        /// SDK 的原始事件计数只进遥测 / 诊断（<c>BusStatusSnapshot.PdoFrameLossConsecutive</c>）。
        /// </para>
        /// </summary>
        public uint PdoBadCyclesConsecutive;
        public bool TelemetryWriteStalled;

        /// <summary>S11 证据一：0x6041 状态字侧的急停/STO 证据（掉 Quick Stop Active，或自称使能却把 bit5 拉低）。</summary>
        public bool EStopInputActive;

        /// <summary>
        /// S11 证据二：0x60FD bit23 急停输入（EC1 的 17B TxPDO 才拿得到）。
        /// 状态字判不了"驱动已去使能时急停仍闭合"，这条补那个盲区。
        /// ⚠ 该链路响应 ≥10 ms（输入脉宽 >10 ms + 0x2703 滤波 1000µs），只能当 10 ms 级保护，
        /// 不能当 1 ms 级保护用；真正的硬线保护不经过驱动器。
        /// </summary>
        public bool ArmEStopInputActive;

        public bool CallbackFault;
        public bool EnableSequenceActive;

        /// <summary>
        /// 受控停 QuickStop 路径进行中（0x0002 斜坡 / 0x0000 收尾未走完）。
        /// 这段窗口里驱动**被故意**带离 Operation Enabled（进 QuickStopActive）、目标=实际、运动质量
        /// 判据失去意义，所以 S1/S2/S3/S12/S13 这些"保运动"联锁暂停；S4/S5/S6/S9/S10/S11 照判。
        /// </summary>
        public bool ControlledStopActive;

        /// <summary>
        /// 我们自己送过 QuickStop 且驱动可能还挂着 QuickStopActive（受控停进行中 + 收尾后短暂余量）。
        /// 只用于 S11 状态字证据的让位——自己命令出来的 QuickStopActive 与急停/STO 同形，不能当急停证据；
        /// 0x60FD bit23 数字输入证据不受它影响。S4 的判据域也不受它影响（收尾余量里不判驱动报错，
        /// 保住"停完就是 Stopped"的既有落位）。
        /// </summary>
        public bool OwnQuickStopRecent;

        public double SwingUpElapsedSec;
        public double SwingUpTimeoutSec;
    }

    /// <summary>
    /// 安全联锁。命中即锁存 Fault，由 PendulumEngine 负责把 0x6040 清零。
    /// 每条联锁有自己的 arming 状态域：不在域里就不判。
    /// 例：S1 是"偏离直立超过 θ_trip"，摆杆自然下垂时偏离就是 π，如果在 Enabled/Stopped 也判，
    /// 第一拍就误跳。所以 S1/S13 只在 Balancing 判，S2/S3/S4/S7 只在驱动在跑的那几态判。
    /// <para>
    /// 这里**只判"驱动器 / 总线真的坏了"**（状态字、0x603F、WKC、帧、急停输入），
    /// 不判"运动学上应该怎样"——例如"摆臂动了摆杆也该动"这类推断在非平衡态必然假阳性：
    /// 手动点动摆臂时摆杆自然下垂不动本来就是正常现象。过程映像是否新鲜由 BusSession 的
    /// IsLost / PdoInputStale 负责（陈旧帧根本进不到控制律），不在这里重复判。
    /// </para>
    /// </summary>
    internal sealed class SafetyInterlock
    {
        // 拍数阈值照契约 §2.3：S2/S5 连续 200 拍，S13 连续 300 拍。
        private const int FollowingErrorTripCycles = 200;
        private const int WkcMismatchTripCycles = 200;
        private const int BalanceLostTripCycles = 300;

        // S6 的阈值契约没给具体数，这里定 20 帧连续丢才判，避免单帧抖动误跳。
        // internal：PendulumEngine 的「单拍 STALE 去抖」沿用同一个拍数阈值（同一故障口径，不另立数）。
        internal const uint PdoFrameLossTripConsecutive = 20;

        private int _followingErrorCycles;
        private int _wkcMismatchCycles;
        private int _balanceLostCycles;

        /// <summary>清空所有累计计数。使能、起摆、急停、断开时调用。</summary>
        public void ResetCounters()
        {
            _followingErrorCycles = 0;
            _wkcMismatchCycles = 0;
            _balanceLostCycles = 0;
        }

        /// <summary>
        /// 逐条判一拍。返回 true = 有联锁命中，code 里给故障码。
        /// 命中后由调用方锁存 Fault，这里只管判，不管动作。
        /// </summary>
        public bool Evaluate(in SafetyInputs input, SafetyLimits limits, out FaultCode code)
        {
            code = FaultCode.None;
            EngineState state = input.State;
            // driving = 正在驱动摆臂的那几态。受控停（QuickStop 路径）进行中时驱动被故意带离
            // Operation Enabled、目标钉实际，"保运动"的判据失效，driving / balancing 一并收起。
            bool driving = (state == EngineState.Enabled ||
                            state == EngineState.Swinging ||
                            state == EngineState.Balancing) && !input.ControlledStopActive;
            bool balancing = state == EngineState.Balancing && !input.ControlledStopActive;

            // S1 摆杆硬限：平衡中偏离直立超过 θ_trip 立即停。
            if (balancing && Math.Abs(input.ThetaFromUprightRad) > limits.ThetaTripRad)
            {
                code = FaultCode.ThetaOverrun;
                return true;
            }

            // S2 跟随误差：|φ_target - φ| 超限连续 200 拍。
            if (driving)
            {
                double err = Math.Abs(input.PhiTargetRad - input.PhiRad);
                if (ControlMath.IsFinite(err) && err > limits.PhiErrMaxRad) _followingErrorCycles++;
                else _followingErrorCycles = 0;
                if (_followingErrorCycles >= FollowingErrorTripCycles)
                {
                    code = FaultCode.FollowingError;
                    return true;
                }
            }
            else
            {
                _followingErrorCycles = 0;
            }

            // S3 使能/模式丢失：状态字不再 Operation Enabled，或 0x6061 回读不是 CSP（8）。
            // 后半条是 EC1 适配加的（04 §5E-6）：进 OP 后模式掉了就停 PLC、目标钉实际、日志报差异，
            // 绝不降级成 PP/PV 凑合跑摆臂。0x6060 每周期都写 8，回读对不上就是驱动器真没在 CSP。
            if (driving && (!CiA402.IsOperationEnabled(input.StatusWord) ||
                            input.ModesDisplay != CiA402.ModeCsp))
            {
                code = FaultCode.EnableLost;
                return true;
            }

            // S4 驱动报错：0x603F != 0。使能序列里也算——那时候驱动报错只会被 0x80 掩掉，不会进 Fault。
            // 开环丢步的唯一线索 0x8401「位置超差」（不可清除）也走这条：EC1 没有 0x6065 跟随误差阈值，
            // 失步保护只能靠它 + 主站侧自算偏差，命中即报 DriveFault 并保留 0x603F 原值进诊断。
            if ((driving || input.EnableSequenceActive || input.ControlledStopActive) && input.ErrorCode != 0)
            {
                code = FaultCode.DriveFault;
                return true;
            }

            // S5 自有从站（GCAN + 摆臂）掉 WKC 连续 200 拍。
            // WKC 口径一个字没改：expected 配置期固定不动，actual 如实读，禁止任何下调 / 扣除 / 加回。
            // 变的只是安全动作的**范围** —— 只看自有两台是否还在响应；旁观从站掉 WKC 是诊断事件 +
            // 遥测如实显示 + 日志报警，不触发 Fault（总线共享，别人的设备波动不该让倒立摆停机）。
            if (!input.OwnedSlavesResponding) _wkcMismatchCycles++;
            else _wkcMismatchCycles = 0;
            if (_wkcMismatchCycles >= WkcMismatchTripCycles)
            {
                code = FaultCode.WkcInvalid;
                return true;
            }

            // S6 PDO 丢帧：**逐拍连续坏拍数**到阈（第 20 拍触发，1 起算）。
            // 输入契约见 SafetyInputs.PdoBadCyclesConsecutive：好拍必须清零，不是 SDK 事件计数。
            if (input.PdoBadCyclesConsecutive >= PdoFrameLossTripConsecutive)
            {
                code = FaultCode.PdoFrameLoss;
                return true;
            }

            // S7 自有从站（GCAN + 摆臂）掉出 OP。只看自有两台：
            // 旁观从站（Beckhoff 链等）掉出 OP 只报诊断 + 遥测如实显示，不触发 Fault。
            if (driving && !input.OwnedSlavesInOp)
            {
                code = FaultCode.EtherCatState;
                return true;
            }

            // S9 周期回调抛异常（由 PendulumEngine 抓到后塞进来）。
            if (input.CallbackFault)
            {
                code = FaultCode.CallbackException;
                return true;
            }

            // S10 遥测环写停。周期侧这一半判的是"本拍遥测没落地"；
            // 周期线程整体停摆那一半由 PendulumEngine 在事件泵里按 200 ms 心跳判。
            if (input.TelemetryWriteStalled)
            {
                code = FaultCode.TimingInvalid;
                return true;
            }

            // S11 硬接线急停 / STO 动作。任何态都判：急停在停着的时候动作也是要如实报出来的。
            // 两条证据任一命中即判：① 0x6041 状态字（掉 Quick Stop Active / 自称使能却 bit5 拉低）；
            // ② 0x60FD bit23 急停输入（EC1 的 17B TxPDO 才拿得到，补"驱动已去使能时急停仍闭合"这个盲区）。
            // ⚠ 证据②的链路响应 ≥10 ms（输入脉宽 >10 ms + 0x2703 滤波 1000µs），只能当 10 ms 级保护，
            //   不能当 1 ms 级保护用；真正的硬线保护（STO / 继电器切 VDC）不经过驱动器。
            // 状态字证据在自己送过 QuickStop 的窗口里让位（0x0002 送驱动进 QuickStopActive 与急停/STO 同形）；
            // 0x60FD bit23 数字输入证据不受影响，受控停期间真急停照样锁存。
            if ((!input.OwnQuickStopRecent && input.EStopInputActive) || input.ArmEStopInputActive)
            {
                code = FaultCode.EStopInput;
                return true;
            }

            // S12 起摆超时。上限取当前生效的 SwingUpParams.TimeoutMs，由调用方换算成秒传进来。
            if (state == EngineState.Swinging && !input.ControlledStopActive &&
                input.SwingUpTimeoutSec > 0 &&
                input.SwingUpElapsedSec > input.SwingUpTimeoutSec)
            {
                code = FaultCode.SwingUpTimeout;
                return true;
            }

            // S13 平衡丢失：偏离直立超过 θ_lost 连续 300 拍。
            if (balancing)
            {
                if (Math.Abs(input.ThetaFromUprightRad) > limits.ThetaLostRad) _balanceLostCycles++;
                else _balanceLostCycles = 0;
                if (_balanceLostCycles >= BalanceLostTripCycles)
                {
                    code = FaultCode.BalanceLost;
                    return true;
                }
            }
            else
            {
                _balanceLostCycles = 0;
            }

            return false;
        }
    }
}
