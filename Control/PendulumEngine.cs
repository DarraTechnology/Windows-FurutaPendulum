using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DarraEtherCAT_Master;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 连接期从 PreOp 升到 OP 的状态推进方式。默认 <see cref="Standard"/>（PreOp → SafeOp → OP），
    /// 与 EtherCAT 标准顺序一致。
    /// <para>
    /// 历史注记：2.13.7 曾有一个确定性缺陷 —— SafeOp 成功后 SDK 的 PDO 运行库把内核
    /// PD_OFFLOAD 武装，内核 piggyback 提交侧按旧判据扣光独立邮箱帧，OP 确认环读不到
    /// ALSTAT（全 0xFFFF），6.5 s 失败后再等 10 s。那 10 s 是 <b>SDK 自己的有界等待窗</b>
    /// （<c>OP_DEMOTE_GRACE_WAIT_MS</c>），不是驱动 grace。该缺陷已于 <b>2.13.9 修复
    /// （commit 20c6de1f）</b>，经过 SafeOp 的路径不再需要绕开。
    /// </para>
    /// <para>
    /// <see cref="DirectFromPreOp"/> 保留为<b>显式诊断开关</b>（<c>--op-entry direct</c>）：
    /// 只用于实测两条路径的差异，不是默认值、也不代表产品需要它。2.13.11 起 SDK 在
    /// <c>SetState(OP)</c> 内部固定先到 SafeOp 再推 OP（<c>RunOpSequenceWithSafeOpLrwBoundary</c>），
    /// <c>direct</c> 只剩"调用者不在 SafeOp 停留"这点差别。
    /// </para>
    /// </summary>
    public enum OpEntryMode
    {
        /// <summary>PreOp → SafeOp → OP（EtherCAT 标准顺序，PDO 映射在 SafeOp 后校验）。</summary>
        Standard = 0,

        /// <summary>PreOp → OP 直连，不经显式 SafeOp；过程映像字节数改在进 OP 后校验。</summary>
        DirectFromPreOp = 1,
    }

    /// <summary>
    /// 引擎入口参数。DENI 是唯一配置真相，不猜网卡、不猜映射、不猜标定。
    /// </summary>
    public sealed class PendulumEngineOptions
    {
        /// <summary>DENI 配置文件路径（现场 GUI 扫描导出的 .deni）。</summary>
        public string DeniPath;

        /// <summary>网卡接口名。必须与 DENI 里的 MAC 唯一对上，对不上就拒连。</summary>
        public string NicName;

        /// <summary>总线周期 [ns]，默认 500µs = 2000 Hz。实际以 DENI 里的周期为准。</summary>
        public uint CycleNs = 500000;

        /// <summary>
        /// 台架出厂默认的摆杆每转计数：600 线编码器 × 4（AB 四倍频）= <b>2400 count/rev</b>。
        /// 现场实测口径（GC1602 CH1，DINT32）：自然下垂 ≈ 0，倒立 ≈ 1200（= 半圈）。
        /// 旧的 600 是 GC1502 脉冲计数口径（不乘 4），用在 GC1602 上会让角度偏 4 倍。
        /// </summary>
        public const double DefaultPendulumCountsPerRev = 2400.0;

        /// <summary>
        /// 摆杆编码器每转计数 CPR_PENDULUM = 线数 × 4（AB 四倍频）× 传动比。
        /// 默认 2400（600 线 × 4，GC1602 台架实测口径）；显式给 0 = 未标定，此时拒绝起摆。不猜。
        /// <para>1 count = 0.15°；保持位 ±1200 count = ±180° 直立。</para>
        /// </summary>
        public double PendulumCountsPerRev = DefaultPendulumCountsPerRev;

        /// <summary>
        /// true = 启动命令行显式给了 <c>--pendulum-cpr</c>（含显式 0）。此时参数文件里的
        /// <c>calibration.pendulum_cpr</c> 不覆盖它；false = 用参数文件恢复，没有再退回默认 2400。
        /// </summary>
        public bool PendulumCountsPerRevExplicit;

        /// <summary>
        /// 摆臂每转计数 CPR_ARM（电机每转计数 P × 减速比）。**权威值 = 驱动器 0x2102 实读的
        /// 电机单圈脉冲数 P × <see cref="ArmGearRatio"/>**：细分是驱动器自己的硬件事实，改过细分
        /// 参数文件就过时了。本字段只在 0x2102 读不到时兜底；两者都没有 = 未标定，拒绝运动，不猜。
        /// 与 <see cref="PendulumCountsPerRev"/> 的"0 = 未标定拒动"语义一致。
        /// <para>
        /// EC1 全系没有 0x608F（PPR 读不到，手册 §5.3「手册未见」），所以这个值只能来自现场标定或
        /// 0x2102 × 减速比；0x608F 只在机型有这个对象时做交叉校验。
        /// </para>
        /// </summary>
        public int ArmCountsPerRev = 0;

        /// <summary>
        /// 摆臂减速比 = 电机转数 / 摆臂转数（无减速 = 1.0）。
        /// 0x2102 实读的电机单圈脉冲数要乘它才能得到摆臂每转计数 CPR_ARM；
        /// 位置换算（0x6064 → φ）与速度换算（0x606C 的 Pulse/s → φ̇ rad/s）都用这一个 CPR_ARM，必须 &gt; 0。
        /// </summary>
        public double ArmGearRatio = 1.0;

        /// <summary>
        /// 摆臂步进从站在 master.Slaves[] 里的 0 基下标。
        /// -1 = 自动（唯一的"非 GCAN 且支持 CSP"的 CiA402 从站）；
        /// 值越界、或自动识别出多个候选时一律拒绝连接并列出候选，不猜。
        /// 只在 Connect 期生效，连接后改一律拒（引擎的 ArmSlaveIndex 属性同此约束）。
        /// </summary>
        public int ArmSlaveIndex = -1;

        /// <summary>
        /// 进 OP 的状态推进方式。默认 <see cref="OpEntryMode.Standard"/>（PreOp → SafeOp → OP）。
        /// <see cref="OpEntryMode.DirectFromPreOp"/> 只作显式诊断开关（<c>--op-entry direct</c>）：
        /// 2.13.7 的 SafeOp 缺陷已于 2.13.9 修复（commit 20c6de1f），不需要靠它绕开。
        /// </summary>
        public OpEntryMode OpEntry = OpEntryMode.Standard;
    }

    /// <summary>
    /// 诊断行的显式重要级别。<b>由产生方在打日志时声明</b>，消费方不再靠中文关键词猜。
    /// <para>
    /// <see cref="Unspecified"/> = 未声明：消费方沿用既有的关键词兜底分类（保持旧行为不变）。
    /// 显式级别一旦声明，就以它为准（关键词一律不参与），这样
    /// "连接完成，未发现异常" 不会因为含"异常"两字被判成错误，
    /// "映射实读 = 56 bit" 也不会因为不含任何关键词而被丢弃。
    /// </para>
    /// </summary>
    public enum DiagnosticSeverity
    {
        /// <summary>未声明 —— 走消费方既有的兜底分类。</summary>
        Unspecified = 0,

        /// <summary>例行信息：只进调试输出，不落盘（与旧行为一致）。</summary>
        Info = 1,

        /// <summary>警告：必须落盘。</summary>
        Warning = 2,

        /// <summary>错误：必须落盘。</summary>
        Error = 3,

        /// <summary>
        /// 连接期一次性取证（映射实读 / 身份 / 进 OP 判据等）：一次连接只出现一两条，
        /// 体积可忽略，但现场事后排查离不开它 —— 必须落盘，且不做 10 秒重复抑制。
        /// </summary>
        Audit = 4
    }

    /// <summary>诊断行在 GUI 侧的落盘裁决（由 <see cref="DiagnosticPolicy"/> 统一给出）。</summary>
    public enum DiagnosticDisposition
    {
        /// <summary>只进调试输出，不写界面日志、不落盘。</summary>
        DebugOnly = 0,

        /// <summary>落盘，信息级。</summary>
        PersistInfo = 1,

        /// <summary>落盘，警告级。</summary>
        PersistWarn = 2,

        /// <summary>落盘，错误级。</summary>
        PersistError = 3
    }

    /// <summary>
    /// 诊断落盘裁决的唯一实现（GUI 与自检夹具共用，别在消费方另写一套）。
    /// 显式级别优先；<see cref="DiagnosticSeverity.Unspecified"/> 时由调用方走既有兜底分类。
    /// </summary>
    internal static class DiagnosticPolicy
    {
        /// <summary>
        /// 尝试按显式级别裁决。返回 false = 级别未声明，调用方应走既有兜底分类（保持旧行为）。
        /// </summary>
        public static bool TryDecide(DiagnosticSeverity severity, out DiagnosticDisposition disposition)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Audit:
                    disposition = DiagnosticDisposition.PersistInfo;
                    return true;
                case DiagnosticSeverity.Warning:
                    disposition = DiagnosticDisposition.PersistWarn;
                    return true;
                case DiagnosticSeverity.Error:
                    disposition = DiagnosticDisposition.PersistError;
                    return true;
                case DiagnosticSeverity.Info:
                    disposition = DiagnosticDisposition.DebugOnly;
                    return true;
                default:
                    disposition = DiagnosticDisposition.DebugOnly;
                    return false;
            }
        }
    }

    /// <summary>
    /// 摆臂位置规整化的结果快照（零点 / 左右限位在规整化前后的绝对 0x6064 counts）。
    /// 只用于日志与前端回显，不参与控制判据。
    /// </summary>
    public struct ArmPoseNormalization
    {
        public int OldZeroCount;
        public int NewZeroCount;
        public int OldMinCount;
        public int OldMaxCount;
        public int NewMinCount;
        public int NewMaxCount;
        public long DeltaCounts;

        public long OldSpanCounts { get { return (long)OldMaxCount - OldMinCount; } }
        public long NewSpanCounts { get { return (long)NewMaxCount - NewMinCount; } }
    }

    /// <summary>
    /// 旋转倒立摆控制层。整个工程里唯一碰 EtherCAT SDK 的地方。
    /// 前端（WinForms UI / CLI 无头）只调本类的公开方法、读遥测快照、订阅事件，不碰 DarraEtherCAT_Master。
    /// </summary>
    public sealed class PendulumEngine : IDisposable
    {
        // ==================== 起始位置 = 坐标 0（home 不再是锁存角）====================

        /// <summary>
        /// 摆臂起始位置（home）的坐标值 [rad]，**坐标系定义下恒为 0**。
        /// 使能成功时自动把 0x6064 基准建在当时位置上（使能即归零），所以"起始位置"永远就是坐标 0，
        /// 不是运行中锁存的任意角。电机在所有阶段都往这个 0 锚（回中让路给平衡的优先级不变，只是目标锚恒为 0）。
        /// </summary>
        public const double ArmHomeRad = 0.0;

        // 回中是加速度受限的慢速臂参考积分，只在摆杆稳定时启用；原四增益 PD 始终在线。
        // 触发 / 距离 / 完成都用同一个实测 φ 权威，目标是 counts 软限位的几何中点。
        private const double BalanceRecenterStablePsiMinRad = 0.02;
        private const double BalanceRecenterExitPsiRad = 0.15;
        private const double BalanceRecenterStableOmegaRadPerSec = 0.20;

        /// <summary>
        /// 回中**恢复**角速度阈值 [rad/s]（2026-11-21 r25 30 s 轮试验）：0.35。
        /// 触发回中仍用 0.20（<see cref="BalanceRecenterStableOmegaRadPerSec"/>）；
        /// 回中让路后，|ω_est| 降到 ≤0.35 就恢复推进，观察能否把臂从 3.77 rad 拉回。
        /// 若破坏平衡，回退到 0.20 并如实报数据。
        /// </summary>
        private const double BalanceRecenterYieldOmegaRadPerSec = 0.35;
        private const double BalanceRecenterExitOmegaRadPerSec = 1.0;
        private const double BalanceRecenterStableSeconds = 0.30;
        private const double BalanceRecenterStartRad = 0.05;
        private const double BalanceRecenterDoneRad = 0.006;
        private const int BalanceRecenterAbortCycles = 10;
        private const double BalanceRecenterDoneSeconds = 0.15;

        /// <summary>
        /// 回中参考速度的恢复斜率 [rad/s²]：强扰动冻结解除后从 0 平滑恢复，不做单拍阶跃。
        /// 只作用于外环参考 r 的速度，不是摆臂速度限幅，也不改变软限位 / ThetaTrip / 急停。
        /// </summary>
        private const double BalanceRecenterResumeRateRadPerSec2 = 50.0;

        /// <summary>
        /// 回中参考积分增益的标定系数：k_ref = 0.0288 / (r/l) [1/s]，夹在 [0.002, 1.0]。
        /// <para>
        /// 依据：把“四增益 LQR + 参考状态 r、ṙ=−k(φ−中点)”写成 5 状态线性系统，
        /// 用当前 K=[5015, 573, −8.51, −6.16]、g/l=81.75 做特征值扫描：
        /// r/l=0.24 时原 Kφ̇/Kφ=0.724 给出 +0.120±0.857j（右半平面，R24 发散根因）；
        /// k≈0.12 时最大实部 −0.13；k=0.0288/b 在 b=0.03~10 全范围最大实部为负。
        /// b&lt;0.03 时增广系统本身已接近结构边界（g/l÷Kψ=0.0163），回中参考保持不动，
        /// 只保留 PD 与 margin，宁可回中不动也不制造大权限偏置。
        /// 未标定（r/l=0）时同样保持不动。
        /// </para>
        /// </summary>
        private const double BalanceRecenterReferenceGainNumerator = 0.0288;
        private const double BalanceRecenterReferenceGainMin = 0.002;
        private const double BalanceRecenterReferenceGainMax = 1.0;
        private const double BalanceRecenterCouplingMin = 0.03;

        /// <summary>
        /// 回中期间**位置参考**相对实测 φ 的最大超前 [rad]。
        /// 2026-11-21 R3 台架：速度域双向钳位会把 PD 的反向权限一起夹死（aCmd=+319 时 v 被夹在 −2.0），
        /// 改成位置域单向钳位——只防「参考位置跑到执行器前面太远」，不阻止速度/加速度反向。
        /// 0.12 rad 是 S2 跟随误差预算 0.25 rad 的 48%，正常回中跟踪时这条钳位不生效。
        /// </summary>
        private const double BalanceRecenterPositionLeadRad = 0.12;

        // ==================== 两个不同的符号：平衡方向 vs 标定几何 ====================
        // 五轮排查的根因就是让一个常量同时表示这两件事。它们回答的是两个不同问题：
        //   平衡律问「引擎坐标下 ψ>0 时，aCmd 应该往哪边给」；
        //   标定问「几何耦合 r/l 是正幅值，底部脉冲响应里的 C_cal 该乘哪个符号还原成正的 r/l」。
        // 关键关系：C_engine（近顶，cosθ≈−1） = −C_cal（底部，cosθ≈+1）。两者同坐标系、不同侧，
        // 不是同一个原始系数；标定报告的 r/l = −C_cal，所以 r/l 与 C_engine 同号同量级。

        /// <summary>
        /// **平衡方向符号**（供 <c>PidGains.Sign</c> 与起摆捕获门的 <c>u_pred</c> 使用；本机 +1）。
        /// <para>
        /// 依据（2026-11-21 第六轮 24 拍 + 第五轮 19 拍）：
        /// ① 24 拍：aCmd=+800、0x6064 二次差分 φ̈≈+800、ψ̈≈+57；拟合
        ///    <c>ψ̈=(g/l)sinψ + C_engine·φ̈</c> 得 <c>C_engine=+0.0566±0.0184</c>（早期 5 点）；
        ///    固定 C=−0.048 拟合的 R²=−0.197，**负 C 被数据明确排除**。
        /// ② 后期 φ̈≈0、φ̇≈40 的 15 点残差均值 −0.68（95% CI [−10.1, +8.7]），φ̇² 系数 k≤0.0055，
        ///    40 rad/s 下离心项 ≤8.7 rad/s² —— “离心 +77”不成立，它是符号反了的线性项冒充的。
        /// ③ uTheta=−(Ka·ψ+Kad·ψ̇) 在 ψ>0 时为负；要 ψ̈&lt;0 需 C_engine·aCmd&lt;0 ⇒ aCmd 与 uTheta 同号，
        ///    所以 <c>BalanceLawSign = +1</c>：aCmd = uTheta + aArm = −Ka·ψ − …
        /// </para>
        /// <para>
        /// 稳定性：有效刚度 = <c>C_engine·Ka − g/l ≈ 0.057·Ka − 81.75</c>；结构下界
        /// Ka &gt; 81.75/0.057 ≈ 1434（按标定 0.048 则 1703）。默认 Ka=2500 留约 1.47 倍
        /// （5000 会把 ψ=0.05 的臂速需求推到 25 rad/s，超出平衡 22 的窗口，第十二轮已收口）。
        /// </para>
        /// <para>
        /// 摆臂回中项 **不乘** 这个符号（它作用在同一套摆臂实测坐标里；符号只编码它和摆杆稳定项
        /// 之间的相对极性）。PidGains.Sign 字段不参与控制律。
        /// </para>
        /// </summary>
        internal const double BalanceLawSign = 1.0;

        /// <summary>
        /// **标定几何符号**：脉冲标定与捕获门动量项用的几何耦合符号。
        /// <para>
        /// 标定器量的是**底部**原始系数 <c>C_cal = (θ̈+(g/l)sinθ)/(cosθ·φ̈)</c>（θ≈0，cosθ≈+1）。
        /// 台架四次脉冲标定给出的原始值约为 <c>−0.048</c>（负）；报告值 r/l 定义为**正的几何幅值**
        /// <c>r/l = −CalibrationSign·C_cal</c>，所以 <c>CalibrationSign=+1</c>，r/l=+0.048。
        /// **不要把公式里的负号去掉** —— 去掉后同一批数据会报 −0.048，标定会再次失败。
        /// </para>
        /// <para>
        /// 与平衡律的关系：近顶 cosθ≈−1，<c>C_engine = −C_cal = +r/l</c>；24 拍拟合 +0.057，
        /// 与 r/l=+0.048 同号同量级（差 19%）。这就是两条独立测量的一致性约束。
        /// 捕获门的 <c>coupledOmega</c> 动量项也属于几何耦合，使用本常量；**平衡律不要用它**。
        /// </para>
        /// </summary>
        internal const double CalibrationSign = 1.0;

        /// <summary>
        /// 历史名兼容别名，**等于 <see cref="CalibrationSign"/>（几何/标定符号，不是平衡方向）**。
        /// 仅因为 <c>SwingUpController.ComputeModeAmplitude</c> 仍引用它而保留；捕获侧迁到
        /// <see cref="CalibrationSign"/> 后必须删除，禁止再用它写平衡律。
        /// </summary>
        internal const double ArmCouplingSign = CalibrationSign;

        /// <summary>
        /// 失去控制权的确认拍数（50 拍，按当前控制周期）：三个条件同时成立才计数：
        /// ① 外环加速度指令顶在 AccMax（accSat）；② 参考速度被 ArmRateMax 钳死、或参考角顶住软限位；
        /// ③ 摆杆偏离 |ψ| 自计数起点仍在增长。离线细扫（模型 H，φ∈±1.8 rad，ArmRateMax=6）里
        /// ψ0 ≤ 0.14 rad 的可恢复初值这条**一次都不成立**（最长连续 0 拍），
        /// ψ0 ≥ 0.15 rad 的失控初值全部 ≥ 200 拍 —— 因此它只在真失去控制权时命中。
        /// </summary>
        private const int BalanceAuthorityLostTripCycles = 50;

        /// <summary>安全余量触边确认拍数（5 拍 @2 kHz = 10 ms）：目标/实测连续进入余量区即受控停机。</summary>
        private const int BalanceTravelGuardTripCycles = 5;

        /// <summary>确认失控所需的 |ψ| 最小增量 [rad]：小于它说明摆杆在往回走或只是抖，不判故障。</summary>
        private const double BalanceAuthorityLostGrowthRad = 0.01;

        /// <summary>
        /// 受控停 QuickStop 路径的确认预算 [s]：0x0002 后等 QuickStopActive 的有界等待，
        /// 到点仍没停稳也照走 0x0000 收尾，绝不卡死在中间态。
        /// </summary>
        public const double StopConfirmBudgetSeconds = 2.0;

        /// <summary>手动点动（JogStart）的摆臂目标推进速度 [rad/s]。远低于 ω_arm 限速，点动按住即动、松开即停。</summary>
        public const double JogSpeedRadPerSec = 1.0;

        /// <summary>
        /// 受控停收尾后 S11 状态字证据的抑制拍数：0x0000 发出后驱动可能还挂 1~2 拍 QuickStopActive，
        /// 那是我们自己送进去的，不能当硬接线急停误报。0x60FD bit23 数字输入证据全程不受影响。
        /// </summary>
        private const int StopFinishGraceCycles = 40;

        // ==================== 命令字（契约 §3.3C）====================

        private const int CmdNone = 0;
        private const int CmdEnable = 1;
        private const int CmdDisable = 2;
        private const int CmdSwingUp = 3;
        private const int CmdBalance = 4;
        private const int CmdStop = 5;
        private const int CmdEStop = 6;
        private const int CmdSetGains = 7;
        private const int CmdSetSwingUpParams = 8;
        private const int CmdZeroPendulum = 9;
        private const int CmdZeroArm = 10;
        private const int CmdReleaseCapture = 11;
        private const int CmdCancelRelease = 12;
        private const int CmdReturnHome = 13;
        private const int CmdRadiusCalibration = 14;

        // ==================== 使能子流程 ====================

        private const int PhaseIdle = 0;
        private const int PhaseNoMotionGate = 1;
        private const int PhaseDriveEnable = 2;

        /// <summary>无运动门连续多少个新鲜 PDO 周期才放行使能（照 HCFA 第一阶段）。</summary>
        private const int EnableGateCycles = 100;
        // 故障复位是上升沿。只发 1 拍 0x80 再回到 0x0000，从站采不到，故障位就清不掉。
        // 高电平保持 20 拍，低电平 10 拍，形成下一次上升沿。1 ms 周期下大约 30 ms 一次。
        private const int FaultResetHighCycles = 20;
        private const int FaultResetLowCycles = 10;

        /// <summary>无运动门允许的位置漂移 [count]。超出就是"位置一漂"，保持去使能。</summary>
        private const int EnableGateDriftCounts = 4;

        /// <summary>无运动门诊断日志节流 [s]。1 ms 周期下每拍一行会刷屏，按 100 ms 限频。</summary>
        private const double EnableGateLogIntervalSeconds = 0.1;

        /// <summary>整个使能子流程的期限 [s]。驱动一直不进 Operation Enabled 就收手，不挂着。</summary>
        private const double EnableTimeoutSeconds = 5.0;

        /// <summary>急停带外去使能后，等待驱动器退出 Operation Enabled 的回读上限 [s]。</summary>
        private const double EStopConfirmSeconds = 0.1;

        /// <summary>带外去使能回读确认的轮询间隔 [ms]。</summary>
        private const int EStopConfirmPollMs = 2;

        /// <summary>使能完成后要自动接起摆的标记。</summary>
        private const int PendingMotionSwingUp = 1;

        /// <summary>使能完成后要自动进「整圈捕获」等待的标记。</summary>
        private const int PendingMotionReleaseWait = 2;

        /// <summary>使能完成后自动接「回到零点」的标记。</summary>
        private const int PendingMotionReturnHome = 3;

        // ==================== 反馈可信度声明 ====================

        /// <summary>
        /// 摆臂反馈是不是实测值。**EC1 系列 = false 侧，恒 true：0x6064 是开环估计值。**
        /// 依据：手册 0x2100 说明栏原文「本产品只支持开环，需改此参数无效」、全书无"编码器"、
        /// 电机侧只有 A+/A-/B+/B- 四线。所以 0x6064 是驱动器内部指令位置的积分估计，
        /// **丢步 / 堵转 / 越障都静默不可见**，不能当真实机械角的唯一依据；
        /// 软限位判据也是 0x6064，真实角一旦发散软限位同样失效，**不能当安全边界**。
        /// 丢步的唯一线索是 0x603F = 0x8401 位置超差（不可清除），由 S4 兜住。
        /// </summary>
        public const bool ArmFeedbackIsEstimate = true;

        /// <summary>0x60FD bit23（急停输入）在运动态里恒 0 多久就提示一次"急停端子未配置" [s]。</summary>
        private const int ArmEStopHintAfterSeconds = 30;

        // ==================== 生命周期与总线 ====================

        private readonly PendulumEngineOptions _options;
        private readonly object _lifecycleGate = new object();
        private BusSession _bus;
        private ControlThread _control;
        private bool _disposed;

        private double _dtSec = CycleConstants.CycleSeconds;
        private readonly double _stopwatchToUs = 1000000.0 / Stopwatch.Frequency;
        private long _sessionStartTicks;

        // ==================== 标定 ====================

        private double _countsPerRevPendulum;

        // 摆臂每转计数 [count/rev]。权威 = 0x2102 实读 P × ArmGearRatio；显式 ArmCountsPerRev 只在读不到时兜底。
        // 用 double 是因为减速比可以不是整数，P × ratio 会带小数。
        private double _countsPerRevArm;

        // 0x60FD bit23 急停输入的观察窗：这个位恒 0 就提示"急停端子未配置"（急停功能码 8 默认未启用）。
        private bool _armEStopBitSeen;
        private long _armEStopWatchStartTicks;
        private bool _armEStopHintRaised;

        // ==================== 参数双缓冲（慢线程写影子，周期线程拍首顶换）====================
        // 两层语义，别混：
        //   *_Shadow = 配置真相（最近一次被 Set* 接受的那整组值）。慢线程（CLI / UI / 保存 / 诊断）只读这一份，
        //               所以「设完立刻读回 / 落盘」永远拿到用户最后设的值，与是否连着总线、周期线程有没有跑无关。
        //   *_Active = 控制拍上的生效副本（私有）。只由周期线程在拍首 SwapIfDirty 顶换，绝不对外暴露，
        //               避免读到顶换到一半的中间态，也保证慢线程的读不会干扰控制拍。

        private readonly object _paramGate = new object();
        private PidGains _pidShadow;
        private global::FurutaPendulum.Control.SwingUpParams _swingShadow;
        private SafetyLimits _limitsShadow;
        private int _paramVersion;
        private int _paramDirty;
        private bool _legacyGainsNoticeRaised;
        // 结构判据说明只发一次（耦合比未标定时很关键：别让现场以为判据已经按标定值生效）。
        private bool _balanceStructureNoteRaised;
        private PidGains _pidActive;
        private global::FurutaPendulum.Control.SwingUpParams _swingActive;
        private SafetyLimits _limitsActive;
        private double _feedbackAlpha = 0.8;

        // ==================== 命令口 ====================

        private int _pendingCommand;
        private int _eStopRequested;

        // 最近一条命令的拒绝原因（字符串引用赋值是原子的；时间戳走 Volatile）。
        // 用途：Enable() 是异步排队，CLI / UI 超时读到的是「状态还是 Connected」；
        // 旧版只能靠「诊断事件有没有被泵出来」碰运气，现场于是出现「12 s 什么原因都没有」。
        private string _lastRefusal;
        private long _lastRefusalTicks;

        // ==================== 状态与故障 ====================

        private int _state;
        private int _faultCode;
        private int _eStopLatch;
        private Exception _callbackException;

        // 周期回调异常的第一行留痕（类型名 + Message，最长 300 字）。
        // 旧版只把异常对象当 bool 用（safety.CallbackFault），[SAFETY] FAULT 行里没有消息，
        // 现场分不清是「PDO 输入映像不可用」还是别的抛出点。与 _callbackException 成对写入 / 消费。
        private string _callbackExceptionText;

        // ==================== 过程数据瞬时 STALE 去抖 ====================
        // 机制链（内核侧已闭合）：某一拍没合并到帧 → 过程映像判 STALE → InputsMapping 抛
        // InvalidOperationException("PDO 输入映像不可用")。旧版这一拍就锁 CallbackException 急停，
        // 但丢的只是**这一拍的过程数据**，物理世界没停：驱动侧位置环照跑、摆杆照动。
        // 所以单拍 STALE 沿用上一拍已解码的过程数据继续跑控制律，连续超过阈值才如实判 PdoFrameLoss。
        // 绝不下调 expected_wkc、绝不把 STALE 当成功 —— actual_wkc / 总线状态仍如实上报。
        /// <summary>
        /// 应用层连续多少拍无有效过程数据才判 PdoFrameLoss（20 拍）。
        /// <para>
        /// **端到端时间不是这 20 拍**，必须把原生 Core 的确认窗口算进去：
        /// <c>window_us = min(wd_pd_ms·1000/2, 50000)</c>，100 ms 看门狗 ⇒ 50 ms；
        /// 500 µs 周期 = 100 拍、1 ms 周期 = 50 拍。原生确认 STALE 之后，应用层才叠加这 20 拍
        /// （500 µs ⇒ +10 ms，1 ms ⇒ +20 ms）。所以真正的最坏时间 = **原生窗口 + 20 拍**
        /// ≈ **60 ms @500 µs / 70 ms @1 ms**。按现场实测 ω≈15 rad/s 估，摆杆在这段时间走
        /// **≈0.9~1.05 rad**，已经接近 S1/S2 的 θ_trip=1.05 rad。
        /// 旧注释按"20 拍 = 10 ms ⇒ 0.15 rad"算，漏掉原生窗口，**小了约 6 倍**，已更正。
        /// </para>
        /// <para>
        /// 为什么保留 20 拍而不设成 1：原生窗口已经负责 PDO 帧丢失确认；这 20 拍是**输出 iomap 争用**
        ///（SDK 侧 BUSY 实测只持续一拍）的本地去抖，设 1 会把单拍瞬态放大成急停。
        /// 输入侧真丢帧仍由原生窗口兜底；若将来改成 post-native（阈值 1），必须同步重算这段裕量
        /// 并复核 S1/S2 的 θ_trip。
        /// </para>
        /// <para>与 SafetyInterlock S6 同源：S6 消费逐拍连续坏拍数（好拍清零），不是 SDK 事件计数。</para>
        /// </summary>
        private const int PdoStaleTripConsecutive = (int)SafetyInterlock.PdoFrameLossTripConsecutive;

        /// <summary>STALE 限流诊断：首拍必报，之后每累计这么多拍再报一条（带累计数）。</summary>
        private const int PdoStaleDiagnosticEvery = 50;

        /// <summary>最近一次成功读到的摆臂 TxPDO 副本（瞬时 STALE 时顶成本拍工作值）。</summary>
        private StepperTxPdo _lastStepperIn;

        private uint _lastPendulumRaw;
        private uint _lastPendulumFreqHz;
        private uint _lastPendulumRawCh2;
        private uint _lastPendulumFreq2Hz;

        /// <summary>最近一次成功解码的过程状态（瞬时 STALE 时沿用；不做「同一份计数再差分」）。</summary>
        private double _lastThetaRad;
        private double _lastOmegaRadPerSec;
        private double _lastPhiRad;
        private double _lastPhiDotRadPerSec;
        private double _lastOmegaFromFreq;

        /// <summary>上一拍丢过帧：下一拍恢复时跳过跨丢帧差分（间隔不是 dt），避免 ω 被算成两倍。</summary>
        private bool _holdFeedbackAcrossGap;

        /// <summary>当前连续无有效过程数据的拍数（周期线程独占；慢路径只读展示 / 故障行）。</summary>
        private int _staleCycles;
        // 仅输入侧、逐拍连续坏拍数：本拍输入坏 +1，本拍输入好立刻清零（在安全联锁评估之前完成）。
        // S6 唯一消费这一条；与 _staleCycles（输入/输出合并、供引擎自身到阈值收口）不是同一条证据链。
        private int _staleInputCycles;
        // stop / fault-reset 的一次性清零请求，由周期线程在 ExecuteCycleCore 开头消费。
        private int _staleResetRequested;

        /// <summary>本会话累计无有效过程数据的拍数（只进限流诊断）。</summary>
        private long _staleTotal;

        /// <summary>本次连接是否成功读到过过程输入（0 = 从未映射成功，没有上一拍可沿用）。</summary>
        private int _processInputPrimed;

        /// <summary>本次连接是否成功写过过程输出（0 = 从未映射成功）。</summary>
        private int _processOutputPrimed;

        /// <summary>
        /// 周期路径取过程数据的三态结果。**未知 ≠ 失败**：
        /// <see cref="Fresh"/> = 本拍读到新数据；<see cref="Stale"/> = 本拍没合并到帧（可沿用上一拍）；
        /// <see cref="NotMapped"/> = 本会话从未映射成功（没有上一拍可沿用，必须立刻收口）。
        /// </summary>
        private enum PdoReadOutcome : byte
        {
            Fresh = 0,
            Stale = 1,
            NotMapped = 2
        }

        // ==================== 周期独占状态 ====================

        private readonly SwingUpController _swing = new SwingUpController();
        private readonly SafetyInterlock _interlock = new SafetyInterlock();

        // 整圈捕获状态机（仅周期线程推进）与在线辨识器（周期线程喂样本，UI 线程取快照算）。
        private readonly CaptureSession _capture = new CaptureSession();
        private readonly SystemIdentifier _identifier = new SystemIdentifier();

        // 摆杆编码器存活判据：CH1 计数最近一次变化的时刻。捕获前置门用它拒绝「编码器冻结还在录」。
        // 只在周期线程写，UI 线程只读时刻做年龄判断。
        private uint _pendulumRawPrev;
        private long _pendulumLastChangeTicks;
        private bool _pendulumLivenessPrimed;

        // 起摆诊断节流时刻（起摆中 1 s 一条：能量 / 目标 / 泵幅 / ψ / ω）。只进日志，不参与控制。
        private long _swingLogTicks;

        /// <summary>
        /// 起摆高频跟踪的时长 [s]：只在前 20 s 打，避免日志爆量。
        /// 环境变量 <c>FURUTA_SWING_TRACE_HZ</c>（Hz，默认 0=关闭）开启后，Swinging 状态下
        /// **无条件**按该频率打 [SWING]（不再受 |ψ|&lt;0.5 的 100 Hz / 其余 1 Hz 节流限制）。
        /// 用途：停滞/泵相位分析需要 50~100 Hz 的 φ/φ̇ 数据；大 |ψ| 段只有 1 Hz 时采 1.44 Hz 泵必然混叠。
        /// </summary>
        private const double SwingTraceSeconds = 20.0;
        private readonly double _swingTraceHz;

        private double _thetaPrev;
        private double _omegaFilt;
        private double _phiPrev;
        private double _phiDotFilt;
        private bool _feedbackPrimed;

        /// <summary>上一拍看到的摆臂每转计数。变了（使能时实读 0x2102 定标）= 本拍重置 φ 微分基准。</summary>
        private double _cycleArmCprSeen;

        /// <summary>「摆臂未标定」诊断只报一次（1 = 已报），避免未标定的每个周期刷屏。</summary>
        private int _armUncalibratedReported;

        /// <summary>「摆杆未标定」诊断只报一次（1 = 已报）。与摆臂同一口径，避免刷屏。</summary>
        private int _pendulumUncalibratedReported;

        // 摆杆计数零点的 32 位原值。GC1502 是 UDINT 无符号（会绕回 0xFFFFFFFF→0），
        // GC1602 是 DINT 有符号，两者都按 32 位位型存，θ 差分统一走 (uint)(now - zero)。
        private uint _countZeroRaw;
        private int _phiZeroCount;

        // 用户指定的摆臂零点（原始 0x6064 计数）。Set=1 时首次使能不再拿当前位置覆盖它，
        // 这样「设置 0 点」跨重启仍然有效；Set=0 保持原语义（首次使能把当前位置锁成 φ=0）。
        private int _armZeroOverride;
        private int _armZeroOverrideSet;

        // 用户指定的摆杆零点（自然下垂 / 最底部的原始 32 位计数位型）。Set=1 时首次使能不再
        // 拿当前位置覆盖它；Set=0 保持原语义（首次使能把当前位置锁成 θ=0）。
        private int _pendulumZeroOverride;
        private int _pendulumZeroOverrideSet;

        // 本会话里的零点是否真的可用：自动归零 / 用户设置都会置 1；「清空标定」置 0。
        // 与上面的 OverrideSet 分开：OverrideSet 只回答「用户是否显式设过、要不要落盘」，
        // 这里回答「当前坐标基准能不能用于自动运动」。清空后不能拿 0 冒充一个有效位置。
        private int _armZeroCalibrated;
        private int _pendulumZeroCalibrated;

        // 「清空标定」后禁止使能时自动重建零点：用户明确清掉位置点，必须由人重新采集 / 设置。
        private int _calibrationCleared;

        // 零点 / 标定相对上次落盘是否已变（1 = 脏）。置能自动归零、zero arm / zero pendulum、
        // 回中重锁零都会置位；调用方保存参数成功后调 ClearCalibrationDirty() 清零。
        // 用途：CLI 的 zero 命令自动落盘、状态行提示「标定未保存」，避免下次进程启动判「标定不一致」。
        private int _calibrationDirty;

        // 连接后首次拿到的 raw 与参数文件保存零点不一致时，置「位置标定待确认」：
        // 不自动加偏移、不自动清空，只如实记下旧 / 当前 / 差值并拒绝自动动作。
        private int _calibrationReviewChecked;
        private int _calibrationReviewRequired;
        private long _armPoseDeltaCounts;
        private long _pendulumPoseDeltaCounts;

        // 持久化位置标定是否已在**本次进程**确认（由界面在载入参数时置位、在规整化 / 重新采集齐全后清除）。
        // 与 _calibrationReviewRequired 分开：那个只表达「首帧 raw 与保存零点不一致」，由首帧核对就近
        // 置 / 清；本标记表达「增量编码器无法自证跨掉电位置」这一事实，必须由用户在本次会话里显式
        // 规整化或重采才能解除，绝不因为 raw 恰好对上就自动放行。
        private int _poseSessionReviewRequired;

        // 回到零点运动的完成判据 / 超时。只在 Enabled 态内推进，不新增状态机状态。
        private int _returnHomeActive;
        private int _returnHomeHoldCycles;
        private long _returnHomeStartTicks;
        // 回中目标（绝对 0x6064 counts）：默认 = 标定零点；「回到行程中点」时 = (左限位+右限位)/2。
        private int _returnHomeTargetCount;
        private int _pendingReturnHomeTarget;
        private int _pendingReturnHomeTargetValid;

        /// <summary>回零完成允许的位置误差 [count]（0x6064 与标定零点之差）。</summary>
        internal const int ReturnHomeToleranceCounts = 3;

        /// <summary>回零到位需要连续保持的拍数（0.1 s @ 2 kHz），防路过零点时误报完成。</summary>
        private const int ReturnHomeHoldCycles = 200;

        /// <summary>回零最长运动时间 [s]。到点只取消回零、冻结当前目标，不切故障。</summary>
        private const double ReturnHomeTimeoutSeconds = 20.0;

        // ---- 执行链：非平衡运动伺服滞后 / 加速度包线 ----

        /// <summary>
        /// 非平衡自动运动（recenter / return-home / normalize 居中 / 半径标定）的伺服跟踪滞后 [s]：
        /// 这些路径**没有**速度前馈。台架 [MOTION] 实测 32.57 rad/s → 0.306 rad、34.05 rad/s → 0.308 rad
        /// ⇒ ≈9 ms；与平衡路径同一物理量，但限速按无前馈口径单独算。
        /// </summary>
        private const double MotionTrackingLagSeconds = 0.009;

        /// <summary>
        /// 非平衡运动速度上限的跟踪误差裕量：速度限 = min(OmegaArmMax, 0.8·PhiErrMaxRad/9 ms)。
        /// 当前 OmegaArmMax=20 时实际限速 20；误差窗口单独允许 22.2，不抬高参数文件的安全限速。
        /// 取 0.8 而不是 0.9 是保守带：OmegaArmMax 调大后若仍按满速走，稳态滞后会超过 PhiErrMaxRad。
        /// </summary>
        private const double NonBalanceFollowingErrorMarginRatio = 0.8;

        /// <summary>
        /// recenter 速度前馈时间 [s]：与无前馈路径的实测滞后同值，目标加 φ̇_ref·T 的位置超前。
        /// 超前量随 _motionSlewSpeed 连续归零（启动 / 停止无阶跃）。
        /// </summary>
        private const double MotionVelocityFeedForwardSeconds = MotionTrackingLagSeconds;

        /// <summary>
        /// 平衡态臂加速度上限 [rad/s²]：**800**。台架实测该臂能做 2000 rad/s² 无故障，800 只占 40% 包线；
        /// 350 的旧包线救不回 ω≈±1.0 rad/s 的冲击（臂会被制动护栏卡住回不来）。
        /// 制动距离护栏的 vCap=√(2·accMax·d) 会随它自动放宽 —— 封的是「刹不住的速度」，不是固定速度。
        /// </summary>
        private const double BalanceAccMaxRadPerSec2 = 800.0;

        /// <summary>
        /// R22 交棒扭矩限幅窗口 [s]：交棒后前 100 ms 内把 aCmd 上限收到
        /// <see cref="BalanceHandoffAccelCapRadPerSec2"/>，之后恢复 accMax。
        /// 目的：避免交棒即饱和把臂直接推飞（R21：aCmdRaw=-823.8，28 ms φ̇_ref=-22、580 ms 臂=-9.96 rad）。
        /// 只限交棒瞬态，不改 K/捕获门/topBrake/回中/re-prime。
        /// </summary>
        private const double BalanceHandoffAccelWindowSeconds = 0.100;

        /// <summary>R22 交棒扭矩上限 [rad/s²]：前 100 ms 为 250，R15 的 289 也低于此帽。</summary>
        private const double BalanceHandoffAccelCapRadPerSec2 = 250.0;

        /// <summary>
        /// 接管后扰动 margin 的静默窗 [s]：0.50 s。R22 的 250→800 只限前 100 ms；这里额外让
        /// margin 在前 0.5 s 只看不动作，避免把“接管瞬间本来就大的 ψ/ω”误判成外部扰动而
        /// 干扰既有 LQR 的捕获瞬态。0.5 s 后强扰动冻结 / 前馈才生效。测试触碰安排在接管后
        /// 数秒，不受影响。
        /// </summary>
        private const double BalanceMarginEntryGraceSeconds = 0.50;

        // ---- 平衡律：归一化 LQR 四增益（K = [5015, 573, -8.51, -6.16]）----
        // aCmd = −(Kψ·ψ + Kω·ω_est + Kφ·clamp(φ−中点,±2π) + Kφ̇·φ̇)。
        // 2026-11-21 参数辨识（β=0.0486、ω_n=8.75）：旧 Kφ=-1.732 / Kφ̇=-4.057 的静态杠杆
        // Kψ/|Kφ|=3005，ψ 残差 5 mrad 就把臂参考顶到 15 rad、9 mrad 顶到 27.4 rad 软限位
        // （r19m/o/p 臂漂到 27.437 后失稳）。新组按 Q=diag(1,0.01,1,0.01)、R=0.0138 的归一化
        // LQR 解得，极点 -8.75±0.01j / -2.11±2.02j，静态杠杆 589。Bryson 备用组
        // [6361.2, 727.3, -20.0, -11.21]（杠杆 318）只在首轮仍出现臂慢漂/回中不足时切换。
        internal const double BalanceKPsi = 5015.0;
        internal const double BalanceKOmega = 573.0;
        internal const double BalanceKPhiDot = -6.16;
        internal const double BalanceKPhi = -8.51;

        /// <summary>平衡参考速度抗饱和上限 [rad/s]：S2 的 0.8×PhiErrMaxRad/0.009 ≈ 22.2，取 22。</summary>
        private const double BalancePhiDotRefMaxRadPerSec = 22.0;

        /// <summary>
        /// 平衡位置项误差钳位 [rad]：取机械全程 2π（软限位为 ±π）。
        /// 旧值 ±3.0 在 Kφ 从 −1.732 放大到 −8.51 后会把正常行程内的误差削平，
        /// 等于把位置环降回旧刚度；2π 覆盖整个可标定行程，不再截断有效误差。
        /// </summary>
        internal const double BalancePositionErrorClampRad = 6.283185307179586;

        /// <summary>平衡段显式增益 profile；进入平衡时激活，aCmdRaw 只读当前 profile。</summary>
        private struct BalanceGainProfile
        {
            public string Name;
            public double KPsi;
            public double KOmega;
            public double KPhi;
            public double KPhiDot;
        }

        private static readonly BalanceGainProfile HeadBalanceProfile = new BalanceGainProfile
        {
            Name = "HEAD-PD",
            KPsi = BalanceKPsi,
            KOmega = BalanceKOmega,
            KPhi = BalanceKPhi,
            KPhiDot = BalanceKPhiDot,
        };

        // 保留台架既有的加速度包线覆盖入口；默认值与 r16/r18 相同，不在本轮调整。
        private static readonly double BalanceAccMaxEnvOverride = ReadPositiveEnvDouble("FURUTA_BALANCE_ACC_MAX");

        // 台架 A/B：接管后前 N 秒只保留摆杆 Kψ/Kω，关闭臂位置/速度项（默认 0=关闭）。
        // 用于隔离 R25 失败段的 Kφ<0 位置项与 Kφdot=-6.16 正速度反馈。
        private static readonly double BalanceHandoffPureLqrSecondsOverride =
            ReadPositiveEnvDouble("FURUTA_HANDOFF_PURE_LQR");

        // 简化固定参数模式：接管后禁用回中外环，四增益 PD 固定，观测器仍默认关闭。
        private static readonly double BalanceSimpleModeEnv = ReadPositiveEnvDouble("FURUTA_BALANCE_SIMPLE");

        // 平衡反馈窗口覆盖（ms）：默认沿用起摆 40ms；台架可用 FURUTA_BALANCE_OMEGA_WINDOW_MS 调快反馈。
        private static readonly double BalanceOmegaWindowOverrideMs =
            ReadPositiveEnvDouble("FURUTA_BALANCE_OMEGA_WINDOW_MS");

        private static double ReadPositiveEnvDouble(string name)
        {
            string text = Environment.GetEnvironmentVariable(name);
            double value;
            if (!string.IsNullOrEmpty(text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                ControlMath.IsFinite(value) && value > 0)
            {
                return value;
            }
            return 0.0;
        }

        /// <summary>平衡态 φ_cmd / φ̇_cmd 状态。位置从实测 φ 起步，速度从 0 起步，由 aCmd 自积分。</summary>
        private double _balanceCmdRad;
        private double _balancePhiDotCmd;
        private bool _balanceFirstControlCycle;
        private BalanceGainProfile _balanceActiveProfile = HeadBalanceProfile;

        // 回中参考偏置与平衡 PD 的状态（仅周期线程读写）。
        // active=1/2 表示原四增益 PD 在线，慢速参考 r 向实际中点修正。
        private double _balanceStableSec;
        private double _balanceRecenterCenterRad;
        private double _balanceRecenterReferenceRad;
        private double _balanceRecenterRate;
        private double _balanceRecenterStartRad;
        private int _balanceRecenterActive;
        private int _balanceRecenterAbortCycles;
        private double _balanceRecenterDoneSec;
        private long _balanceRecenterLogTicks;

        // 外力扰动观测/前馈（仅周期线程写；UI / CLI 经 TryReadDisturbanceSnapshot 读快照）。
        // _balanceLast* 是上一拍真正下发的摆臂加速度与权限标志，供本拍观测器做模型预测。
        private readonly DisturbanceObserver _disturbanceObserver = new DisturbanceObserver();
        private double _balanceLastAppliedAccelRadPerSec2;
        private bool _balanceLastAccSat;
        private bool _balanceLastRateClipped;
        private bool _balanceLastAuthorityLost;
        private int _balanceRecenterFrozen;
        private DisturbanceMarginState _lastDisturbanceMarginState = DisturbanceMarginState.Normal;
        private long _disturbanceLogTicks;

        // 平衡与交棒门共用的低噪声 ω 估计（40 ms 最小二乘，同一实现见 PendulumOmegaEstimator）。
        private readonly PendulumOmegaEstimator _balanceOmegaEstimator = new PendulumOmegaEstimator();
        // 摆臂实测角加速度：同款 40 ms 二次拟合，喂扰动观测器。不能用下发的 aCmd 代替 ——
        // 驱动器有转矩/加速度上限，R25 12:01 段 aCmd 到 −654 rad/s² 时实测只有约 −18，
        // 拿指令做模型输入会把伺服没执行的部分全部误判成外力。
        private readonly PendulumOmegaEstimator _balanceArmAccelEstimator = new PendulumOmegaEstimator();
        private double _balanceOmegaEstimateRadPerSec;
        private double _balanceOmegaResidualRad;

        // 交棒 ω 混合：40 ms 最小二乘估计在交接瞬间滞后约 20 ms（R9 台架：真实 ω=−0.87、ω_est=−0.22），
        // Kω=573 会把 −0.65 rad/s 的估计误差放大成 −373 rad/s² 的错误反向加速度。
        // 前 25 拍（2 ms/拍 = 50 ms）用 2 ms 滤波实测 ω，随后 25 拍线性过渡到 40 ms ω_est（总 100 ms）。
        private const int BalanceOmegaHandoffDirectCycles = 25;
        private const int BalanceOmegaHandoffBlendCycles = 50;
        private int _balanceOmegaHandoffCycles;
        private double _balanceOmegaUsedRadPerSec;

        private double _motionSlewSpeed;
        private double _motionAccLimit;
        private long _motionLogTicks;
        private int _omegaArmLimitClampReported;

        /// <summary>
        /// 「伺服跟踪滞后 × v &lt; PhiErrMaxRad」这条物理约束的统一实现：
        /// 返回 marginRatio·PhiErrMaxRad / lagSeconds。两条路径的 lag 不同（平衡含前馈 9 ms，
        /// recenter 无前馈 9 ms），但公式只有这一份，调用点只传各自的实测 lag，不写两套。
        /// </summary>
        private static double TrackingSpeedLimit(double errWindowRad, double marginRatio, double lagSeconds)
        {
            double window = errWindowRad > 0 && ControlMath.IsFinite(errWindowRad)
                ? errWindowRad
                : SafetyLimits.CreateDefault().PhiErrMaxRad;
            double lag = lagSeconds > 0 && ControlMath.IsFinite(lagSeconds)
                ? lagSeconds
                : MotionTrackingLagSeconds;
            double limit = marginRatio * window / lag;
            return limit > 0 && ControlMath.IsFinite(limit) ? limit : 0.0;
        }

        /// <summary>
        /// 本次摆杆归零是不是用户显式动作（「设置摆杆零点」/ CLI zero）。只有显式动作才把落定的原始计数
        /// 记成「用户设定的摆杆零点」并允许落盘；使能自动归零是会话初始化，不冒充用户标定。
        /// </summary>
        private bool _zeroPendulumUserRequested;

        /// <summary>
        /// 整圈捕获的实测越界余量 [rad]（约 2.9°）。判据本身在 counts 域，引擎按摆臂一圈计数把它换成
        /// counts 后传给 CaptureSession —— 这样不同机型的容差量级一致，不引入第二个角度口径。
        /// </summary>
        private const double CaptureSoftLimitMarginRad = 0.05;

        // 点动中「设置 0 点」的挂起请求：闭环（Enabled / 手动点动）里改零点不能直接改换算基准，
        // 否则 0x607A 目标会在一拍内跳 ±当前角。慢线程只把请求排进来，周期线程在拍首统一换基准，
        // 并把所有以 φ 表达的锚点（目标 / 参考 / 微分基准）平移同样的角度，
        // 保证「物理目标计数」连续、φ̇ 不被打飞。PendingRaw 是请求值，Pending 为 1 表示尚未被周期线程消费。
        private int _pendingArmZeroRaw;
        private int _pendingArmZeroApply;

        // 起始位置（home）不再是锁存角：坐标系定义下恒为 0，见 ArmHomeRad。所有回中 / 收尾目标一律锚这个 0。
        private double _phiTargetPrev;
        private bool _phiTargetPrimed;
        private int _lastTargetCount;
        private bool _lastTargetValid;
        // 本拍 S2 实际使用的目标快照；遥测从同一快照读取，避免 counts 钳位/写入时序造成口径分裂。
        private double _safetyTargetRad;
        private long _safetyFollowingErrorCount;
        // 当前下发的参考角 [rad]：供遥测 / UI 显示。
        private double _phiRef;

        // 平衡执行链的参考角速度 [rad/s]（uCmdEff，已过制动距离护栏）；只用于遥测 / 诊断。
        private double _phiDotRef;

        // "失去控制权"判据的累计状态（仅周期线程读写）。参考速度被钳死 / 参考角顶到软限位之后，
        // 摆臂对摆杆的加速度输入变成 0；此时外环仍顶在 AccMax 且 |ψ| 还在增长 = 已经压不回来。
        // 判定在 RunControlLaw 里做，切安全态在控制律分派处做（那里才能把 0x6040 写成 0x0000）。
        private int _balanceAuthorityLostCycles;
        private double _balanceAuthorityLostPsiStart;
        private bool _balanceAuthorityLostTripped;

        // 安全余量触边：目标或实测 0x6064 进入软限位内侧 margin 区，连续 N 拍即受控停机。
        private int _balanceTravelGuardCycles;
        private bool _balanceTravelGuardTripped;

        // r/l 主动标定：脉冲状态机只在周期线程推进；参数由慢线程写入后经命令字交接。
        private readonly RadiusCalibrator _radiusCal = new RadiusCalibrator();
        private RadiusCalibrationResult _radiusCalResult;
        private int _radiusCalActive;
        private int _radiusCalAbortRequested;
        private double _pendingRadiusAccel;
        private double _pendingRadiusPulseSec;
        private double _pendingRadiusGapSec;
        private int _pendingRadiusPairs;

        // 平衡接管瞬间的状态快照（只用于诊断）：失稳时把「从什么状态接管的」一并打出来，
        // 现场才能区分「控制器不行」和「捕获门放行了一个本来就接不住的状态」。
        private double _balanceEntryPsi;
        private double _balanceEntryOmega;
        private double _balanceEntryPhi;
        private double _balanceEntryPhiDot;

        // 进入平衡后经过的时间 [s]，只用于遥测。
        private double _balanceEnterSec;

        // ---- 遥测 ----

        /// <summary>
        /// 进入 Balancing 后逐拍 [BALANCE] 遥测的时长 [s] 与间隔 [s]（100 Hz × 2 s）。
        /// 目的：接管后的动力学现在完全看不见 —— 只有 4 条事件（接管/首次超限/冻结结束/Fault），
        /// 必须能看到「aCmd 指令 vs 0x6064 实际 vs ψ 响应」逐拍对照，
        /// 才能分清是指令算错、臂没跟上、还是模型缺项。
        /// </summary>
        private const double BalanceTelemetrySeconds = 2.0;
        private const double BalanceTelemetryIntervalSeconds = 0.01;
        /// <summary>
        /// 头部窗口之后的**慢速尾窗**：接管后 2 s 到 30 s 仍按 10 Hz 继续记录。
        /// 目的：平衡失败往往发生在接管后几秒（台架出现过 7.78 s 才 ThetaOverrun 的一次），
        /// 只记前 2 s 的话失败原因完全看不见。30 s × 10 Hz = 300 行，可接受。
        /// </summary>
        private const double BalanceTelemetryTailSeconds = 30.0;
        private const double BalanceTelemetryTailIntervalSeconds = 0.1;
        private double _balanceTelemetryNextSec;

        private int _enablePhase;
        private int _enableGateTicks;
        private int _enableGateAnchor;
        private bool _enableGateArmed;
        // 无运动门诊断用：上一拍位置（0x606C 不可用时的位置差分兜底）与 100 ms 日志节流起点。
        private int _enableGatePrevPos;
        private bool _enableGatePrevPosValid;
        private long _enableGateLastLogTicks;
        private long _enableTimeoutTicks;
        private int _pendingMotion;
        private int _faultResetTicks;

        // 受控停 QuickStop 路径状态（仅周期线程推进）：0=无 1=发 0x0002 等 QuickStopActive 2=收尾 0x0000。
        private int _stopPhase;
        private int _stopGraceCycles;
        private bool _balanceRequest;

        // R11 捕获优先测试：Enabled 态手动把摆杆扶到直立附近，用与起摆交棒同一套捕获门
        // 连续满足 3 拍后自动接管。只允许人工近顶释放，不绕过软限位/急停/ThetaTrip。
        private const double ManualCaptureOmegaMaxRadPerSec = 0.8;
        private const double ManualCaptureArmRateMaxRadPerSec = 2.5;
        private const int ManualCaptureHoldCycles = 3;
        private int _manualBalanceRequest;
        private int _manualBalanceHoldCycles;
        private long _manualBalanceLogTicks;

        // 故障收尾写 0x6040=0 失败（从站掉线 / 过程映像不可用）的"本会话已报过一次"闸，
        // 防掉线期间每拍都在 2 kHz 热路径上拼字符串、刷诊断队列。
        private int _disableVoltageWriteFailureReported;
        private long _stopPhaseCyclesLeft;
        private int _zeroPulsePhase;

        // 零点是否已在本会话建立过。使能成功不再无条件重建零点：急停后摆杆停在任意角度，
        // 若每次使能都把它当垂位，θ 参考、起摆能量式、切换判据全部错位。连接时清、归零落定时置位。
        private bool _zeroEstablished;

        // 手动置零同时要建立摆臂零点：请求那一拍记下 0x6064，等摆杆锁零点那一拍一起落定，
        // 保证两路零点取的是同一拍的计数（分两拍取会让 φ 基准带上这两拍的摆臂位移）。
        private int _zeroArmCount;
        private bool _zeroArmCountPending;

        // 手动点动方向：-1 / 0 / +1。UI MouseDown 置方向、MouseUp 清零（松开即停），周期线程只读。
        private int _jogDirection;

        private long _sequence;
        private int _saturatedCycles;

        // ---- 一总线周期一步：控制律的推进节拍只能跟内核周期序号走，不能跟"新过程映像"通知次数走 ----
        // 内核每完成一拍把 PdoResponseReady 置位（用户态读后清零）。部分拓扑下同一份 PdIn 会被重复
        // 置位两次（主/副两口各自发布），ControlThread 的主等待 + 补拍各消费一次，于是每个 1 ms 总线
        // 周期里 ExecuteCycle 被调两次。而 dt 取的是 DENI 周期（1 ms）——每调一次推一步，控制相位就是
        // 墙钟的 2 倍：30 s 超时 15 s 触发，死点抖速被顶成 2 倍频（离摆杆固有频率更远，甩不起来）。
        // 判据用内核周期序号（每周期 +1）：序号没前进 = 还是同一份过程映像，本拍直接返回。
        private ulong _lastStepCycle;
        private bool _stepCycleValid;
        private long _duplicateCycleSkips;
        private int _duplicateCycleReported;
        private int _cycleSequenceReadFailed;

        // ==================== 遥测与事件 ====================

        private readonly TelemetrySlot _slot = new TelemetrySlot();
        private readonly TelemetryRing _ring = new TelemetryRing();
        private readonly EngineEventQueue _events = new EngineEventQueue();
        private readonly TimingDiagnostics _jitterStats = new TimingDiagnostics(8192, 3.0);
        private readonly TimingDiagnostics _computeStats = new TimingDiagnostics(8192, 3.0);
        private long _lastTickTimestamp;
        private bool _lastTickValid;
        private long _telemetryBeatTicks;
        private long _lastDiagTicks;

        // ==================== 总线统计 ====================

        private ushort _lastActualWkc;
        private ushort _lastExpectedWkc;

        /// <summary>
        /// 构造一个引擎。参数为空直接抛，不猜默认路径。
        /// </summary>
        /// <summary>
        /// 读取起摆高频跟踪频率（环境变量 FURUTA_SWING_TRACE_HZ）。默认 0=关闭；
        /// 空值 / 非数 / ≤0 / NaN 一律按关闭，上限 500 Hz，避免误配置把日志打爆。
        /// </summary>
        private static double ReadSwingTraceHz()
        {
            string text = Environment.GetEnvironmentVariable("FURUTA_SWING_TRACE_HZ");
            double hz;
            if (string.IsNullOrWhiteSpace(text) ||
                !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) ||
                !(hz > 0) || !ControlMath.IsFinite(hz))
                return 0.0;
            return Math.Min(hz, 500.0);
        }

        public PendulumEngine(PendulumEngineOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            _options = options;

            _pidShadow = PidGains.CreateDefault();
            _swingShadow = global::FurutaPendulum.Control.SwingUpParams.CreateDefault();
            _limitsShadow = SafetyLimits.CreateDefault();
            _pidActive = _pidShadow;
            _swingActive = _swingShadow;
            _limitsActive = _limitsShadow;
            _countsPerRevPendulum = options.PendulumCountsPerRev;
            _swingTraceHz = ReadSwingTraceHz();

            RefreshFeedbackAlpha();
        }

        // ==================== 生命周期 ====================

        /// <summary>
        /// 本次启动生效的 DENI 路径（构造时给定，只读）。前端保存调参文件时拿它做整定溯源记录，
        /// 只记录不切换：换 DENI 仍要换文件后重新 Connect，本属性不参与任何配置。
        /// </summary>
        public string DeniPath
        {
            get { return _options.DeniPath; }
        }

        /// <summary>
        /// 连总线。DENI 身份只读预检 → Build → PreOp 映射与字节数自检 → SafeOp。
        /// 止于 SafeOp，不进 OP、不发使能序列。慢操作，请在 Task/工作线程里调。
        /// </summary>
        public void Connect()
        {
            ThrowIfDisposed();
            // 冷启动的 native 加载 / 网卡枚举很贵（实测首次 GetNetworkAdapters 约 5.6 s）：
            // GUI 已在开窗时后台预热，这里等同一个 Task（通常已完成，瞬时返回）；CLI 一次性进程在这里同步预热。
            // 放在计时基准之前：② connect_total 不被这次性冷加载污染，① since_process_start 仍如实含它。
            SdkWarmup.WarmSdkAsync().GetAwaiter().GetResult();
            // 计时打点走现有诊断事件（不另造日志系统）；同进程只一个引擎，重复接线幂等。
            ConnectTiming.Sink = RaiseDiagnostic;
            // 同进程重连要能量出本次耗时：连接基准挪到本次入口，冷启动基准（process_start）保持不变。
            ConnectTiming.Reset();
            ConnectTiming.Mark("connect_begin");
            lock (_lifecycleGate)
            {
                if (State != EngineState.Disconnected)
                {
                    RaiseDiagnostic("已经连着了，先 Disconnect 再 Connect。");
                    return;
                }

                BusSession bus = new BusSession();
                bus.DiagnosticSink = RaiseDiagnostic;
                string error;
                FaultCode failureCode;
                if (!bus.Open(_options, out error, out failureCode))
                {
                    bus.Dispose();
                    RaiseFaultNow(failureCode, error);
                    return;
                }
                ConnectTiming.Mark("connect_open_ok");

                _bus = bus;
                _dtSec = bus.CycleNs / 1000000000.0;
                if (_dtSec <= 0) _dtSec = CycleConstants.CycleSeconds;
                RefreshFeedbackAlpha();

                _sessionStartTicks = Stopwatch.GetTimestamp();
                _lastTickValid = false;
                _telemetryBeatTicks = Stopwatch.GetTimestamp();
                _lastDiagTicks = 0;
                _interlock.ResetCounters();
                _swing.Reset();
                ResetRadiusCalibration();
                // 会话级事实一律从零开始：上一次连接的 r/l 标定结果、急停锁存、命令拒绝原因、
                // 0x60FD 观察窗与 WKC 缓存都不得冒到本次连接里（否则现场看到的“当前状态”是旧会话的）。
                _radiusCalResult = null;
                Volatile.Write(ref _eStopLatch, 0);
                _armEStopWatchStartTicks = 0;
                _armEStopBitSeen = false;
                _armEStopHintRaised = false;
                ClearCommandRefusal();
                _lastActualWkc = 0;
                _lastExpectedWkc = 0;
                _capture.ResetSession();
                ResetPendulumLiveness();
                // 新连接的摆动 / 泵能量样本从零开始；捕获历史（含启动导入的持久化记录）不清。
                _identifier.ResetSession();
                _feedbackPrimed = false;
                // 过程数据去抖是会话级事实：上一会话的缓存帧 / 丢拍计数绝不能冒到新连接里
                //（否则新连接第一拍 STALE 会拿上一次接线的旧数据当"上一拍"）。
                _staleCycles = 0;
                _staleInputCycles = 0;
                _holdFeedbackAcrossGap = false;
                Interlocked.Exchange(ref _processInputPrimed, 0);
                Interlocked.Exchange(ref _processOutputPrimed, 0);
                _lastStepperIn = default(StepperTxPdo);
                _lastPendulumRaw = 0;
                _lastPendulumFreqHz = 0;
                _lastPendulumRawCh2 = 0;
                _lastPendulumFreq2Hz = 0;
                _lastThetaRad = 0;
                _lastOmegaRadPerSec = 0;
                _lastPhiRad = 0;
                _lastPhiDotRadPerSec = 0;
                _lastOmegaFromFreq = 0;
                Interlocked.Exchange(ref _callbackException, null);
                Interlocked.Exchange(ref _callbackExceptionText, null);
                _cycleArmCprSeen = 0;
                Interlocked.Exchange(ref _armUncalibratedReported, 0);
                Interlocked.Exchange(ref _pendulumUncalibratedReported, 0);
                _phiTargetPrimed = false;
                _lastTargetValid = false;
                // 新会话没有「上一拍」，上一次会话没来得及消费的零点请求必须丢弃；否则重连后
                // 第一拍会拿旧请求覆盖本次使能刚建立的零点。
                Interlocked.Exchange(ref _pendingArmZeroApply, 0);
                // 新会话 = 零点未建立：下一次使能成功时才重新建立，会话内重复使能不再动零点。
                _zeroEstablished = false;
                // 零点 / 回零都是会话内运行时事实：连接后尚未使能，先一律标成不可用；
                // 「清空标定」的禁令保留，使能时会跳过自动归零。
                Volatile.Write(ref _armZeroCalibrated, 0);
                Volatile.Write(ref _pendulumZeroCalibrated, 0);
                Volatile.Write(ref _returnHomeActive, 0);
                Volatile.Write(ref _returnHomeHoldCycles, 0);
                _returnHomeStartTicks = 0;
                // 新连接必须重新核对一次「保存零点 ↔ 首帧 raw」；结果在第一帧周期后落定。
                Volatile.Write(ref _calibrationReviewChecked, 0);
                Volatile.Write(ref _calibrationReviewRequired, 0);
                Interlocked.Exchange(ref _armPoseDeltaCounts, 0L);
                Interlocked.Exchange(ref _pendulumPoseDeltaCounts, 0L);
                _zeroArmCountPending = false;
                _zeroPendulumUserRequested = false;
                _zeroPulsePhase = 0;
                _enablePhase = PhaseIdle;
                _pendingMotion = 0;
                // 上一次会话排队的命令绝不能在新会话第一拍被执行（典型：断线时按过 estop，
                // 重连第一拍消费到旧的 CmdEStop 会直接进 Fault）。新会话从“无命令”开始。
                Interlocked.Exchange(ref _pendingCommand, CmdNone);
                _stopPhase = 0;
                _stopPhaseCyclesLeft = 0;
                _stopGraceCycles = 0;
                Volatile.Write(ref _jogDirection, 0);
                _saturatedCycles = 0;
                _sequence = 0;
                // 新会话的内核周期序号从 0 起算，判重基准必须一起清，否则会拿上一次会话的序号误判重复。
                _stepCycleValid = false;
                Interlocked.Exchange(ref _duplicateCycleSkips, 0L);
                Volatile.Write(ref _duplicateCycleReported, 0);
                Volatile.Write(ref _cycleSequenceReadFailed, 0);
                Volatile.Write(ref _eStopRequested, 0);
                Volatile.Write(ref _faultCode, (int)FaultCode.None);
                Volatile.Write(ref _disableVoltageWriteFailureReported, 0);

                _control = new ControlThread(this, bus.MasterNumber, (uint)bus.CycleNs, "FurutaPendulum.Control");
                try
                {
                    _control.Start();
                }
                catch (Exception ex)
                {
                    _control = null;
                    bus.Dispose();
                    _bus = null;
                    RaiseFaultNow(FaultCode.ConfigInvalid, "周期执行器启动失败：" + DiagText.Describe(ex));
                    return;
                }

                SetState(EngineState.Connected);
                // 连接完成摘要 + 计时汇总都是"事后必须能查"的一次性取证：显式 Audit，GUI 必落盘。
                RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                    "总线已连到 {0}。周期 {1} ns，从站 {2} 台，网卡 {3}。",
                    bus.CurrentState, bus.CycleNs, bus.SlaveCount, ResolvedNicName));
                // 连接链计时汇总：读到从站、进 OP 的这段已经走完；第一拍 PDO 由周期线程再打一次点。
                RaiseDiagnostic(DiagnosticSeverity.Audit, ConnectTiming.Render());
            }
        }

        /// <summary>
        /// 断总线。固定 teardown 顺序，总期限 20 s（0x6040=0 上限 7 s）。幂等。
        /// </summary>
        public void Disconnect()
        {
            if (_disposed) return;
            lock (_lifecycleGate)
            {
                ControlThread control = _control;
                _control = null;
                if (control != null)
                {
                    control.RequestStop();
                    long until = Environment.TickCount + 3000;
                    while (!control.IsDrained && Environment.TickCount < until) Thread.Sleep(10);
                    control.ReleaseAfterDrain();
                    Exception first = control.FirstFault;
                    if (first != null)
                    {
                        RaiseDiagnostic("周期执行器带故障退出：" + DiagText.Describe(first));
                    }
                }

                BusSession bus = _bus;
                _bus = null;
                if (bus != null)
                {
                    string error;
                    bus.Teardown(out error);
                    bus.Dispose();
                    if (!string.IsNullOrEmpty(error))
                    {
                        RaiseFaultNow(FaultCode.TeardownTimeout, error);
                    }
                }

                _enablePhase = PhaseIdle;
                _pendingMotion = 0;
                _stopPhase = 0;
                _stopPhaseCyclesLeft = 0;
                _stopGraceCycles = 0;
                Volatile.Write(ref _jogDirection, 0);
                Volatile.Write(ref _returnHomeActive, 0);
                Volatile.Write(ref _returnHomeHoldCycles, 0);
                _returnHomeStartTicks = 0;
                // 断开后没有控制拍，挂起的零点请求不会再被消费；留着会在下次连接第一拍突然生效。
                Interlocked.Exchange(ref _pendingArmZeroApply, 0);
                // 命令槽同理：断线期间的 estop / stop 请求不能留到下次连接第一拍执行。
                Interlocked.Exchange(ref _pendingCommand, CmdNone);
                Volatile.Write(ref _eStopRequested, 0);
                // 断开后坐标基准不再代表现场姿态，自动运动资格随之作废。
                Volatile.Write(ref _armZeroCalibrated, 0);
                Volatile.Write(ref _pendulumZeroCalibrated, 0);
                // 本次会话的摆臂定标结论随会话作废：下次使能 / 起摆重新实读 0x2102 定标。
                // 不清的话 ResolvedArmCountsPerRev 会把上一次连接的定标值报成本次的实值。
                _countsPerRevArm = 0;
                _capture.ResetSession();
                ResetPendulumLiveness();
                // 会话结束：r/l 标定结果 / 命令拒绝原因 / WKC 缓存随会话作废，不再被 status 当“当前值”读出来。
                _radiusCalResult = null;
                ClearCommandRefusal();
                _lastActualWkc = 0;
                _lastExpectedWkc = 0;
                SetState(EngineState.Disconnected);
                RaiseDiagnostic("总线已断开。");
            }
        }

        public bool IsBusInOp
        {
            get
            {
                BusSession bus = _bus;
                return bus != null && bus.IsBusInOp;
            }
        }

        public string BusStateName
        {
            get
            {
                BusSession bus = _bus;
                return bus == null ? "None" : bus.CurrentState.ToString();
            }
        }

        /// <summary>
        /// 本次连接实际采用的进 OP 方式。未连接时返回构造参数的取值（= 下次连接会用的方式）。
        /// 默认是 <see cref="OpEntryMode.Standard"/>；<see cref="OpEntryMode.DirectFromPreOp"/>
        /// 是<b>仅诊断用的非标准路径</b>。2.13.7 的 SafeOp 缺陷已于 2.13.9 修复（commit 20c6de1f）。
        /// 界面上仍需常显方式标记，别让人把诊断模式当成默认路径。
        /// </summary>
        public OpEntryMode OpEntryMode
        {
            get
            {
                BusSession bus = _bus;
                return bus == null ? _options.OpEntry : bus.OpEntry;
            }
        }

        /// <summary>
        /// 进 OP 方式的一行中文标注（界面 / 日志 / CLI 共用同一口径，避免各处措辞不一致）。
        /// </summary>
        public string OpEntryText
        {
            get
            {
                return OpEntryMode == OpEntryMode.DirectFromPreOp
                    ? "直连模式（仅诊断用，非标准路径）"
                    : "标准模式（PreOp → SafeOp → OP）";
            }
        }

        /// <summary>SDO 直读 GC1502 四路计数/频率，供起摆诊断与 PDO 对照。</summary>
        public bool TryReadGcanCounterSnapshot(out uint[] values, out string error)
        {
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen)
            {
                values = null;
                error = "总线未连接：先 connect 并等主站进 SafeOp/OP 后再直读 GCAN 计数。";
                return false;
            }
            return bus.TryReadGcanCounterSnapshot(out values, out error);
        }

        /// <summary>= Disconnect()，幂等。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Disconnect();
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("Dispose 时断开出错：" + DiagText.Describe(ex));
            }
        }

        // ==================== 使能 ====================

        /// <summary>
        /// 进 OP 并走 CiA402 分步 0x06→0x07→0x0F。分步与无运动门都在周期里跑，
        /// 本方法只做慢操作（状态转换 + 摆臂标定定标与 0x8402 自检），不阻塞周期线程。
        /// </summary>
        public void Enable()
        {
            ThrowIfDisposed();
            EngineState state = State;
            if (state != EngineState.Connected && state != EngineState.Stopped)
            {
                RefuseCommandNow("enable", string.Format("当前状态 {0} 不能使能，只有 Connected / Stopped 可以", state));
                return;
            }
            if (IsEStopLatched)
            {
                RefuseCommandNow("enable", "急停锁存未清，先 Stop() 复位再使能");
                return;
            }
            if (Volatile.Read(ref _calibrationCleared) != 0)
            {
                RefuseCommandNow("enable", "位置标定已清空：先重新采集「零点 + 左限位 + 右限位」，"
                    + "或用「规整化」把当前位姿设为基准");
                return;
            }
            if (Volatile.Read(ref _calibrationReviewRequired) != 0)
            {
                RefuseCommandNow("enable", "位置标定待确认（连接后 raw 与保存的摆杆零点不一致）："
                    + "先在界面上选择「规整化」或重新采集 / 清空标定");
                return;
            }
            string poseSessionReason = PoseSessionReviewRejectText();
            if (poseSessionReason != null)
            {
                RefuseCommandNow("enable", poseSessionReason);
                return;
            }
            if (Volatile.Read(ref _calibrationReviewChecked) == 0)
            {
                RefuseCommandNow("enable", "首帧遥测还没到，不能核对保存零点：等状态行出现采样值后再试");
                return;
            }
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen)
            {
                RefuseCommandNow("enable", "总线未连接");
                return;
            }

            // 运动前先把标定事实读回来定标 + 自检（0x2102 / 0x2003 / 0x608F），禁止猜每转脉冲。
            if (!RefreshArmCalibration(bus)) return;

            string error;
            if (!bus.IsBusInOp && !bus.TransitionTo(EcState.OP, out error))
            {
                RaiseFaultNow(FaultCode.EtherCatState, error);
                return;
            }

            _pendingMotion = 0;
            Interlocked.Exchange(ref _pendingCommand, CmdEnable);
            RaiseDiagnostic("已请求使能：进 OP 后先过无运动门，再走 0x06→0x07→0x0F。");
        }

        /// <summary>退到 SafeOp，保留总线。想彻底断开用 Disconnect()。</summary>
        public void Disable()
        {
            ThrowIfDisposed();
            if (State == EngineState.Disconnected)
            {
                RaiseDiagnostic("本来就断着，不用 Disable。");
                return;
            }

            Interlocked.Exchange(ref _pendingCommand, CmdDisable);
            // 等周期线程把 0x6040 清掉并把状态收到 Connected，再由慢线程退 SafeOp。
            long until = Environment.TickCount + 1000;
            while (State != EngineState.Connected && Environment.TickCount < until) Thread.Sleep(5);

            BusSession bus = _bus;
            if (bus != null && bus.IsOpen)
            {
                // 直连（诊断）模式从不经过 SafeOp：Disable 也不回退 SafeOp，总线保持 OP。
                // 标准模式按标准语义回退 SafeOp。去使能只需要清 0x6040（周期线程已做）。
                if (bus.OpEntry == OpEntryMode.DirectFromPreOp)
                {
                    RaiseDiagnostic("已去使能，总线保持 OP（直连诊断模式不回退 SafeOp）。");
                }
                else
                {
                    string error;
                    if (!bus.TransitionTo(EcState.SafeOp, out error))
                    {
                        RaiseDiagnostic("退 SafeOp 失败：" + error);
                    }
                    RaiseDiagnostic("已退回 SafeOp，总线保留。");
                }
            }
        }

        // ==================== 运动 ====================

        /// <summary>能量起摆。只有 Enabled / Stopped 能进 Swinging。Stopped 会先重新使能再自动接起摆。</summary>
        public void SwingUp()
        {
            string reason;
            TrySwingUp(out reason);
        }

        /// <summary>
        /// 同 <see cref="SwingUp"/>，但把拒绝原因带出来。界面用它把「起摆被拒」的判据值 / 原因
        /// 直接写进日志与操作提示；引擎内部仍在同一处 RaiseDiagnostic，CLI 行为不变。
        /// </summary>
        public bool TrySwingUp(out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (Volatile.Read(ref _jogDirection) != 0)
            {
                reason = "手动点动中不能起摆（手动与启动互斥），先松开手动按钮。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_stopPhase != 0)
            {
                reason = "受控停进行中不能起摆，等停完再启动。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中，先取消等待 / 结束捕获再起摆。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _returnHomeActive) != 0)
            {
                reason = "回零进行中不能起摆：等回零走完或先「停止」。";
                RaiseDiagnostic(reason);
                return false;
            }
            EngineState state = State;
            if (state != EngineState.Enabled && state != EngineState.Stopped)
            {
                reason = string.Format("当前状态 {0} 不能起摆，只有 Enabled / Stopped 可以。", state);
                RaiseDiagnostic(reason);
                return false;
            }
            if (IsEStopLatched)
            {
                reason = "急停锁存未清，先 Stop() 复位再起摆。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _armZeroCalibrated) == 0)
            {
                reason = "驱动臂零点未标定（或已清空），起摆被拒：先在「设备参数」里设置 / 采集驱动臂零点。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _pendulumZeroCalibrated) == 0)
            {
                reason = "摆杆零点未标定（或已清空），起摆被拒：先停止并在自然下垂位采集「摆杆零点 (下垂 0°)」。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _calibrationReviewRequired) != 0)
            {
                reason = "位置标定待确认（连接后 raw 与保存零点不一致），起摆被拒："
                    + "先选择「规整化」或重新采集 / 清空标定。";
                RaiseDiagnostic(reason);
                return false;
            }
            string poseSessionReason = PoseSessionReviewRejectText();
            if (poseSessionReason != null)
            {
                reason = "起摆被拒：" + poseSessionReason;
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _calibrationReviewChecked) == 0)
            {
                reason = "首帧遥测还没到，不能核对保存零点，起摆被拒：等状态行出现采样值后再试。";
                RaiseDiagnostic(reason);
                return false;
            }
            string legacyDetail;
            if (IsLegacyFactorySwingDefaults(_swingShadow, out legacyDetail))
            {
                reason = "起摆参数异常，拒绝起摆：" + legacyDetail;
                RaiseDiagnostic(reason);
                return false;
            }
            if (_countsPerRevPendulum <= 0)
            {
                reason = "摆杆每转计数 CPR_PENDULUM 未标定（≤0），拒绝起摆。先在 PendulumEngineOptions.PendulumCountsPerRev 里给线数×4×传动比。";
                RaiseDiagnostic(reason);
                return false;
            }
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen)
            {
                reason = "总线未连接，不能起摆。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!RefreshArmCalibration(bus))
            {
                reason = "起摆被拒：摆臂每转计数无法定标（0x2102 实读不到，且没有显式 ArmCountsPerRev）。" +
                         "检查驱动器 0x2102 是否可读，或显式配置 ArmCountsPerRev / ArmGearRatio 后重试。";
                RaiseDiagnostic(reason);
                return false;
            }

            string limitReason;
            if (!CheckSoftLimitBeforeMotion("起摆", out limitReason))
            {
                reason = limitReason;
                return false;
            }

            Interlocked.Exchange(ref _pendingCommand, CmdSwingUp);
            RaiseDiagnostic("已请求起摆：能量法 + 摆臂回中阻尼 + 往复泵。");
            return true;
        }

        /// <summary>
        /// 回到标定零点：把摆臂目标设成**用户标定的零点原始 counts**，由现有 CFG 位置环按
        /// 回中速度斜坡走过去（速度 = S2 跟随误差窗口 / 伺服滞后换算，见 RunControlLaw）。
        /// 只移动，不重设零点、不改一圈量 / PID / 泵。
        /// <para>
        /// 目标必须严格落在软限位 [ArmMinCount, ArmMaxCount] 内；零点本身越界时拒绝并给出原因，
        /// 绝不把目标钳到边界后硬动。Enabled 直接起步；Stopped 先重新使能再自动接回零。
        /// </para>
        /// </summary>
        public bool TryReturnToZero(out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (Volatile.Read(ref _jogDirection) != 0)
                return RejectMotion("手动点动中不能回零，先松开手动按钮。", out reason);
            if (Volatile.Read(ref _returnHomeActive) != 0)
                return RejectMotion("回零已经在进行中，等它走完或先「停止」。", out reason);
            if (_stopPhase != 0 || IsControlledStopActive)
                return RejectMotion("受控停进行中不能回零，等停完再试。", out reason);
            if (IsEStopLatched)
                return RejectMotion("急停锁存未清，回零被拒：先「停止」复位再使能。", out reason);
            if (_capture.IsWaiting || _capture.IsCapturing)
                return RejectMotion("整圈捕获进行中不能回零：摆臂要保持不动，先取消捕获。", out reason);

            EngineState state = State;
            if (state == EngineState.Disconnected)
                return RejectMotion("总线未连接，不能回零。先「连接」并「使能」。", out reason);
            if (state == EngineState.Connected)
                return RejectMotion("当前只是已连接（未使能），不能回零。先「使能」。", out reason);
            if (state == EngineState.Swinging || state == EngineState.Balancing)
                return RejectMotion("起摆 / 平衡中不能回零，先「停止」再回零。", out reason);
            if (state == EngineState.Fault)
                return RejectMotion("故障锁存中不能回零，先「复位」再使能。", out reason);
            if (state != EngineState.Enabled && state != EngineState.Stopped)
                return RejectMotion("当前状态 " + state + " 不能回零，只有已使能 / 已停可以。", out reason);
            if (Volatile.Read(ref _calibrationReviewRequired) != 0)
                return RejectMotion("位置标定待确认（连接后 raw 与保存零点不一致），回零被拒：先选择「规整化」或重新采集 / 清空标定。", out reason);
            string poseSessionReason = PoseSessionReviewRejectText();
            if (poseSessionReason != null)
                return RejectMotion("回零被拒：" + poseSessionReason, out reason);
            if (Volatile.Read(ref _calibrationReviewChecked) == 0)
                return RejectMotion("首帧遥测还没到，不能核对保存零点，回零被拒：等状态行出现采样值后再试。", out reason);

            if (Volatile.Read(ref _armZeroCalibrated) == 0)
                return RejectMotion("驱动臂零点未标定（或已清空），回零被拒：先在「设备参数」里采集 / 设置驱动臂零点。", out reason);
            if (Volatile.Read(ref _pendingArmZeroApply) != 0)
                return RejectMotion("摆臂零点正在切换，等下一控制拍落定后再回零。", out reason);

            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen)
                return RejectMotion("总线未连接，不能回零。", out reason);
            if (!RefreshArmCalibration(bus))
            {
                reason = "回零被拒：摆臂每转计数无法定标（0x2102 实读不到，且没有显式 ArmCountsPerRev）。" +
                         "先确认驱动器 0x2102 可读，或显式配置 ArmCountsPerRev / ArmGearRatio 后重试。";
                RaiseDiagnostic(reason);
                return false;
            }

            SafetyLimits limits = SnapshotRequestedLimits();
            if (!limits.ArmLimitsValid)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "摆臂软限位不合法（绝对 counts [{0},{1}]，要求 min < max），回零被拒：先重新采集 / 输入左右限位。",
                    limits.ArmMinCount, limits.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }

            int target = Volatile.Read(ref _phiZeroCount);
            if (target < limits.ArmMinCount || target > limits.ArmMaxCount)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "标定零点 raw={0} 不在软限位 [{1},{2}] counts 内，回零被拒：先把限位改成包含零点，或重新标定零点。",
                    target, limits.ArmMinCount, limits.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }

            // 普通回零：先清掉可能残留的「行程中点」请求，再投命令字（顺序不能反，
            // 否则周期线程可能在两个写之间就消费到旧的中点请求）。
            Interlocked.Exchange(ref _pendingReturnHomeTargetValid, 0);
            Interlocked.Exchange(ref _pendingCommand, CmdReturnHome);
            RaiseDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "已请求回到零点：目标 raw={0} counts（标定零点，绝对位置；不改零点 / 一圈量 / PID / 泵）。",
                target));
            return true;
        }

        // ==================== 位置规整化 / 自动居中 ====================

        /// <summary>
        /// 位置规整化（文档 §5.5）：把**当前** 0x6064 定义为 φ=0，并把左右软限位按同一个 delta 平移，
        /// 保持行程跨度不变。用于「驱动器的 raw 坐标系整体平移了」的场景；调用前机械必须仍处在
        /// 限位标定时的姿态，否则平移后的边界不再对应真实机械行程。
        /// <para>
        /// 只在 Connected / Stopped 允许（闭环中零点会跳变）。零点 / 新旧限位 / 平移量全部落日志，
        /// 并标记位置标定待保存（CLI / 界面保存参数后持久化）。
        /// </para>
        /// </summary>
        public bool TryNormalizeArmPose(out ArmPoseNormalization result, out string reason)
        {
            ThrowIfDisposed();
            result = new ArmPoseNormalization();
            reason = null;

            EngineState state = State;
            if (state != EngineState.Connected && state != EngineState.Stopped)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "规整化只允许在 Connected / Stopped（当前 {0}）：闭环中零点平移会让位置指令跳变。", state);
                RaiseDiagnostic(reason);
                return false;
            }
            if (_enablePhase != PhaseIdle || IsControlledStopActive ||
                Volatile.Read(ref _returnHomeActive) != 0)
            {
                reason = "使能流程 / 受控停 / 回中进行中不能规整化：等动作结束。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中不能规整化：先取消捕获。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!TryReadFreshArmPosition(out int currentRaw, out reason))
            {
                RaiseDiagnostic("规整化被拒：" + reason);
                return false;
            }
            SafetyLimits limits = SnapshotRequestedLimits();
            if (!limits.ArmLimitsValid || limits.ArmLimitsArePlaceholder)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "软限位不可用（[{0},{1}]，占位={2}）：先采集左右限位再规整化。",
                    limits.ArmMinCount, limits.ArmMaxCount, limits.ArmLimitsArePlaceholder);
                RaiseDiagnostic(reason);
                return false;
            }

            int oldZero = Volatile.Read(ref _phiZeroCount);
            long delta = (long)currentRaw - oldZero;
            long newMin = (long)limits.ArmMinCount + delta;
            long newMax = (long)limits.ArmMaxCount + delta;
            if (newMin < int.MinValue || newMax > int.MaxValue ||
                (long)currentRaw <= newMin || (long)currentRaw >= newMax)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "平移后区间 [{0},{1}] 不能严格包含新零点 raw={2}（旧零点 {3}、Δ={4} counts）：" +
                    "先确认机械仍在标定姿态，或重新采集零点 / 左右限位。",
                    newMin, newMax, currentRaw, oldZero, delta);
                RaiseDiagnostic(reason);
                return false;
            }

            SafetyLimits shifted = limits;
            shifted.ArmMinCount = (int)newMin;
            shifted.ArmMaxCount = (int)newMax;
            if (!TryApplyArmCalibration(currentRaw, shifted, out reason))
            {
                RaiseDiagnostic("规整化未写入：" + reason);
                return false;
            }
            Volatile.Write(ref _calibrationDirty, 1);

            result.OldZeroCount = oldZero;
            result.NewZeroCount = currentRaw;
            result.OldMinCount = limits.ArmMinCount;
            result.OldMaxCount = limits.ArmMaxCount;
            result.NewMinCount = (int)newMin;
            result.NewMaxCount = (int)newMax;
            result.DeltaCounts = delta;
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(CultureInfo.InvariantCulture,
                "[CAL] 规整化完成：新零点 raw={0}（旧 {1}，Δ={2} counts）；限位 [{3},{4}] → [{5},{6}] counts，" +
                "行程跨度 {7} counts 保持不变。位置标定已标记待保存（save-config 落盘）。",
                currentRaw, oldZero, delta, limits.ArmMinCount, limits.ArmMaxCount,
                newMin, newMax, newMax - newMin));
            return true;
        }

        /// <summary>
        /// 以**当前实测位置**为中心，把左右软限位整体平移成以它为中点（行程跨度不变），
        /// 并把当前位置重新锁成 φ=0。用于「中心偏在区间一端、recenter 回旧零点也没用」的恢复。
        /// <para>
        /// 与 <see cref="TryNormalizeArmPose"/> 的区别：后者保持「零点在区间内的相对位置」，
        /// 本方法保持「当前点在区间正中间」。两者都保持跨度；是否可采用取决于机械姿态是否仍对应标定，
        /// 所以只由显式操作（CLI recenter / 界面按钮）触发，绝不自动执行。
        /// </para>
        /// 允许 Enabled（recenter 结束后就是 Enabled）：零点切换走 <see cref="TrySetArmZeroCount"/>
        /// 的锚点平移路径，目标计数不跳变；软限位在下一拍顶换。
        /// </summary>
        public bool TryNormalizeCenteredOnCurrent(out ArmPoseNormalization result, out string reason)
        {
            ThrowIfDisposed();
            result = new ArmPoseNormalization();
            reason = null;

            EngineState state = State;
            if (state != EngineState.Connected && state != EngineState.Enabled && state != EngineState.Stopped)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "居中规整化只允许 Connected / Enabled / Stopped（当前 {0}）。", state);
                RaiseDiagnostic(reason);
                return false;
            }
            if (_enablePhase != PhaseIdle || IsControlledStopActive)
            {
                reason = "使能流程 / 受控停进行中不能居中规整化：等动作结束。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中不能居中规整化：先取消捕获。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _returnHomeActive) != 0)
            {
                reason = "回中仍在进行：等它到位后再居中规整化。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!TryReadFreshArmPosition(out int currentRaw, out reason))
            {
                RaiseDiagnostic("居中规整化被拒：" + reason);
                return false;
            }
            SafetyLimits limits = SnapshotRequestedLimits();
            if (!limits.ArmLimitsValid || limits.ArmLimitsArePlaceholder)
            {
                reason = "软限位不可用（未采集 / 占位）：先采集左右限位。";
                RaiseDiagnostic(reason);
                return false;
            }

            int oldZero = Volatile.Read(ref _phiZeroCount);
            long span = (long)limits.ArmMaxCount - limits.ArmMinCount;
            long newMin = (long)currentRaw - span / 2;
            long newMax = newMin + span;
            if (span < 3 || newMin < int.MinValue || newMax > int.MaxValue ||
                (long)currentRaw <= newMin || (long)currentRaw >= newMax)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "居中后区间 [{0},{1}] 不能严格包含当前 raw={2}（行程 {3} counts）：检查限位 / 遥测。",
                    newMin, newMax, currentRaw, span);
                RaiseDiagnostic(reason);
                return false;
            }

            if (!TrySetArmZeroCount(currentRaw, out reason))
            {
                RaiseDiagnostic("居中规整化未写入零点：" + reason);
                return false;
            }
            SafetyLimits centered = limits;
            centered.ArmMinCount = (int)newMin;
            centered.ArmMaxCount = (int)newMax;
            SetSafetyLimits(centered);
            Volatile.Write(ref _calibrationCleared, 0);
            Interlocked.Exchange(ref _armPoseDeltaCounts, 0L);
            if (Interlocked.Read(ref _pendulumPoseDeltaCounts) == 0)
                ConfirmCalibrationReview();
            Volatile.Write(ref _calibrationDirty, 1);

            result.OldZeroCount = oldZero;
            result.NewZeroCount = currentRaw;
            result.OldMinCount = limits.ArmMinCount;
            result.OldMaxCount = limits.ArmMaxCount;
            result.NewMinCount = (int)newMin;
            result.NewMaxCount = (int)newMax;
            result.DeltaCounts = (long)currentRaw - oldZero;
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(CultureInfo.InvariantCulture,
                "[CAL] 居中规整化完成：当前 raw={0} 成为 φ=0 且为区间中点；限位 [{1},{2}] → [{3},{4}] counts，" +
                "行程 {5} counts 保持不变（新坐标下 ±{6} counts）。位置标定已标记待保存。",
                currentRaw, limits.ArmMinCount, limits.ArmMaxCount, newMin, newMax, span, span / 2));
            return true;
        }

        /// <summary>
        /// 把摆臂开到**当前软限位的行程中点**（不是旧零点），到位后由调用方接
        /// <see cref="TryNormalizeCenteredOnCurrent"/> 完成「居中 + 重新锁零」。
        /// 只在 Enabled / Stopped 允许；Stopped 会先重新使能再走。
        /// </summary>
        public bool TryRecenterToTravelCenter(out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (Volatile.Read(ref _jogDirection) != 0)
                return RejectMotion("手动点动中不能回中，先松开手动按钮。", out reason);
            if (Volatile.Read(ref _returnHomeActive) != 0)
                return RejectMotion("回中已经在进行中，等它走完。", out reason);
            if (_stopPhase != 0 || IsControlledStopActive)
                return RejectMotion("受控停进行中不能回中，等停完再试。", out reason);
            if (IsEStopLatched)
                return RejectMotion("急停锁存未清，回中被拒：先「停止」复位再使能。", out reason);
            if (_capture.IsWaiting || _capture.IsCapturing)
                return RejectMotion("整圈捕获进行中不能回中：先取消捕获。", out reason);

            EngineState state = State;
            if (state == EngineState.Disconnected)
                return RejectMotion("总线未连接，不能回中。", out reason);
            if (state == EngineState.Connected)
                return RejectMotion("当前只是已连接（未使能），不能回中：先「使能」。", out reason);
            if (state != EngineState.Enabled && state != EngineState.Stopped)
                return RejectMotion("当前状态 " + state + " 不能回中，只有已使能 / 已停可以。", out reason);
            if (Volatile.Read(ref _armZeroCalibrated) == 0)
                return RejectMotion("摆臂零点未标定（或已清空），回中被拒：先在「设备参数」采集 / 设置驱动臂零点，再使能后回中。", out reason);

            SafetyLimits limits = SnapshotRequestedLimits();
            if (!limits.ArmLimitsValid || limits.ArmLimitsArePlaceholder)
            {
                reason = "软限位不可用（未采集 / 占位），回中被拒：先用点动采集左右限位（setlimits 或界面采集），再试。";
                RaiseDiagnostic(reason);
                return false;
            }
            long mid = ((long)limits.ArmMinCount + limits.ArmMaxCount) / 2;
            if (mid <= limits.ArmMinCount || mid >= limits.ArmMaxCount ||
                mid < int.MinValue || mid > int.MaxValue)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "行程中点 raw={0} 不严格落在软限位 [{1},{2}] counts 内（要求 min < mid < max），回中被拒。" +
                    "重新采集左右限位，或改用「回到标定零点」。",
                    mid, limits.ArmMinCount, limits.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }

            Volatile.Write(ref _pendingReturnHomeTarget, (int)mid);
            Interlocked.Exchange(ref _pendingReturnHomeTargetValid, 1);
            Interlocked.Exchange(ref _pendingCommand, CmdReturnHome);
            RaiseDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "已请求回到行程中点：目标 raw={0} counts（限位 [{1},{2}] 的中点；到位后请接居中规整化）。",
                mid, limits.ArmMinCount, limits.ArmMaxCount));
            return true;
        }

        /// <summary>
        /// 读一拍**新鲜**遥测里的摆臂 raw counts。停更 / 未标定一律拒绝，绝不拿陈旧样本做规整化。
        /// 新鲜度口径见 <see cref="TryReadFreshTelemetry"/>：先等本次连接的第一帧（最多 500 ms）。
        /// </summary>
        private bool TryReadFreshArmPosition(out int armPos, out string reason)
        {
            armPos = 0;
            PendulumTelemetry t;
            if (!TryReadFreshTelemetry(out t, out reason))
                return false;
            if (!ControlMath.IsFinite(t.PhiRad))
            {
                reason = "摆臂未标定（φ 非有限）：先使能并让 0x2102 定标成功（status 看「摆臂标定」行），" +
                         "或显式配置 ArmCountsPerRev；标定成功后再重试。";
                return false;
            }
            armPos = t.ArmPositionCount;
            return true;
        }

        private const double FreshTelemetryMaxAgeSeconds = 0.5;
        private const int FreshTelemetryWaitBudgetMs = 500;

        /// <summary>
        /// 本次连接内最近一帧遥测的年龄 [s]。从未收到 / 只有上一次连接的残留 / 未连接 时返回 PositiveInfinity，
        /// 调用方不要拿这个值做“停更很久”的结论 —— 那只是旧会话时间戳。
        /// </summary>
        public double TelemetryAgeSeconds
        {
            get
            {
                PendulumTelemetry t;
                if (!_slot.TryRead(out t) || t.Sequence == 0 || t.TimestampTicks == 0)
                    return double.PositiveInfinity;
                long sessionStart = _sessionStartTicks;
                if (sessionStart != 0 && t.TimestampTicks < sessionStart)
                    return double.PositiveInfinity;
                double age = (Stopwatch.GetTimestamp() - t.TimestampTicks) / (double)Stopwatch.Frequency;
                return age >= 0 ? age : double.PositiveInfinity;
            }
        }

        /// <summary>
        /// 等一帧**本次连接**产生且不超过 0.5 s 的遥测，最多等 500 ms。
        /// <para>
        /// 连接后第一拍还没到时，seqlock 里仍留着上一次会话的快照；直接拿它的 TimestampTicks 算年龄
        /// 会得到“停更 10723 s”这类无意义数字。这里按「时间戳早于本次连接起点」识别旧会话残留，
        /// 只用可操作的中文说明当前该等什么 / 该查什么。
        /// </para>
        /// </summary>
        private bool TryReadFreshTelemetry(out PendulumTelemetry snapshot, out string reason)
        {
            snapshot = default(PendulumTelemetry);
            reason = null;
            long deadlineTicks = Stopwatch.GetTimestamp() +
                (long)(FreshTelemetryWaitBudgetMs / 1000.0 * Stopwatch.Frequency);
            bool sawCurrentSession = false;
            bool sawPreviousSession = false;
            double lastCurrentAgeSec = 0;

            while (true)
            {
                PendulumTelemetry t;
                if (_slot.TryRead(out t) && t.Sequence != 0 && t.TimestampTicks != 0)
                {
                    long sessionStart = _sessionStartTicks;
                    if (sessionStart != 0 && t.TimestampTicks < sessionStart)
                    {
                        // 上一会话的残留快照：不能当本次连接的遥测。
                        sawPreviousSession = true;
                    }
                    else
                    {
                        sawCurrentSession = true;
                        double age = (Stopwatch.GetTimestamp() - t.TimestampTicks) / (double)Stopwatch.Frequency;
                        if (age >= 0 && age <= FreshTelemetryMaxAgeSeconds)
                        {
                            snapshot = t;
                            return true;
                        }
                        if (age > lastCurrentAgeSec) lastCurrentAgeSec = age;
                    }
                }

                if (Stopwatch.GetTimestamp() >= deadlineTicks)
                    break;
                Thread.Sleep(5);
            }

            if (State == EngineState.Disconnected)
            {
                reason = "总线未连接：先 connect，等状态行出现遥测后再操作。";
            }
            else if (sawCurrentSession)
            {
                reason = "本次会话遥测已停更 " + DescribeTelemetryAge(lastCurrentAgeSec) +
                         "（>0.5 s）：检查主站是否仍在 OP、控制周期是否推进" +
                         "（status 看 WKC / 实测周期 / 最近故障），恢复后再操作。";
            }
            else if (sawPreviousSession)
            {
                reason = "本次连接尚未收到遥测（当前只有上一次连接的残留快照）：" +
                         "确认主站正在进 OP 且控制周期已启动（status 看 BusState 与 WKC），等状态行刷新后再操作。";
            }
            else
            {
                reason = "尚未收到任何遥测：确认已 connect 且控制周期在跑（status 看 BusState / WKC），再重试。";
            }
            return false;
        }

        /// <summary>遥测年龄显示：超过 60 s 只说“超过 60 s”，不打印跨会话残留算出来的一万多秒。</summary>
        private static string DescribeTelemetryAge(double ageSec)
        {
            if (!ControlMath.IsFinite(ageSec) || ageSec < 0) return "未知时长";
            if (ageSec >= 60.0) return "超过 60 s";
            return string.Format(CultureInfo.InvariantCulture, "{0:F2} s", ageSec);
        }

        // ==================== r/l 主动标定 ====================

        /// <summary>
        /// 启动 r/l 主动标定：摆杆必须自然下垂且基本静止；摆臂在中心附近、脉冲全程留在软限位内。
        /// 只允许 Enabled；结果经 <see cref="RadiusCalibrationResult"/> 读取，由调用方写入
        /// <see cref="SafetyLimits.RadiusRatio"/> 并保存。
        /// </summary>
        public bool TryStartRadiusCalibration(double accelRadPerSec2, double pulseSeconds, int pairs, out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (!ControlMath.IsFinite(accelRadPerSec2) || accelRadPerSec2 < 20 || accelRadPerSec2 > 200)
            {
                reason = "脉冲加速度必须在 [20, 200] rad/s² 内且有限（默认 100）。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!ControlMath.IsFinite(pulseSeconds) || pulseSeconds < 0.03 || pulseSeconds > 0.15)
            {
                reason = "单相脉冲时长必须在 [0.03, 0.15] s 内且有限（默认 0.06）。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (pairs < 1 || pairs > 8)
            {
                reason = "脉冲对数必须在 [1, 8] 内（默认 3）。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (State != EngineState.Enabled)
            {
                reason = "r/l 标定只能在已使能（Enabled）状态下运行：先 enable，确认摆杆下垂静止。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _returnHomeActive) != 0 || IsControlledStopActive ||
                _enablePhase != PhaseIdle || IsRadiusCalibrationActive)
            {
                // 指出到底是谁占着，别让人猜。
                string busy = IsRadiusCalibrationActive ? "上一次 r/l 标定尚未结束"
                    : IsControlledStopActive ? "受控停还在进行"
                    : Volatile.Read(ref _returnHomeActive) != 0 ? "回中运动还在进行"
                    : "使能序列还在进行";
                reason = "r/l 标定被拒：" + busy + "。等它结束（status 看 EngineState 稳定在 Enabled）后再试。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_countsPerRevPendulum <= 0 || _countsPerRevArm <= 0)
            {
                reason = "r/l 标定被拒：每转计数未标定（摆杆 CPR=" +
                    _countsPerRevPendulum.ToString(CultureInfo.InvariantCulture) + "、摆臂 CPR=" +
                    _countsPerRevArm.ToString(CultureInfo.InvariantCulture) + "）。" +
                    "先给摆杆 --pendulum-cpr，并让摆臂 0x2102 定标成功（或显式配置 ArmCountsPerRev），再试。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!TryReadFreshArmPosition(out int currentRaw, out reason))
            {
                RaiseDiagnostic("r/l 标定被拒：" + reason);
                return false;
            }
            SafetyLimits limits = SnapshotRequestedLimits();
            if (!limits.ArmLimitsValid || limits.ArmLimitsArePlaceholder)
            {
                reason = "r/l 标定被拒：软限位不可用（当前 [" +
                    limits.ArmMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                    limits.ArmMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts，占位=" +
                    limits.ArmLimitsArePlaceholder + "）。先用点动采集左右限位（setlimits 或界面采集），再试。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!(limits.JEq > 0) || !(limits.Mgl > 0))
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "r/l 标定被拒：JEq / Mgl 未配置有效值（JEq={0:F6}、Mgl={1:F4}），无法按重力项修正。" +
                    "先在参数 limits 段成对配置 JEq>0 与 Mgl>0 并 save-config，再试。",
                    limits.JEq, limits.Mgl);
                RaiseDiagnostic(reason);
                return false;
            }
            double phi = (double)((long)currentRaw - Volatile.Read(ref _phiZeroCount)) * ControlMath.TwoPi / _countsPerRevArm;
            double driftRad = accelRadPerSec2 * pulseSeconds * pulseSeconds;   // 四相脉冲的单向位移
            double margin = driftRad + 0.30;
            double minRad = ((double)limits.ArmMinCount - Volatile.Read(ref _phiZeroCount)) * ControlMath.TwoPi / _countsPerRevArm;
            double maxRad = ((double)limits.ArmMaxCount - Volatile.Read(ref _phiZeroCount)) * ControlMath.TwoPi / _countsPerRevArm;
            if (phi - margin < minRad || phi + margin > maxRad)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "摆臂当前位置 φ={0:F3} rad 距软限位太近（脉冲需要 ±{1:F3} rad 余量）：先 recenter / 点动到中心附近。",
                    phi, margin);
                RaiseDiagnostic(reason);
                return false;
            }
            PendulumTelemetry t;
            if (!TryReadFreshTelemetry(out t, out reason))
            {
                RaiseDiagnostic("r/l 标定被拒：" + reason);
                return false;
            }
            if (!ControlMath.IsFinite(t.ThetaFromUprightRad))
            {
                reason = "r/l 标定被拒：摆杆角 θ 非有限（未标定 / 反馈无效）。" +
                         "先让摆杆计数有效（status 看 θ 是否为数值），再试。";
                RaiseDiagnostic(reason);
                return false;
            }
            // ψ = 离直立角：**下垂位是 |ψ|≈π，不是 0**。这里先换回「以垂位为 0」的 θ 再判，
            // 否则会把正常下垂的摆杆当成「不在下半区」拒绝（旧判据写着 |ψ| < π−2 ≈ 1.14，
            // 实际含义是「离直立 65° 以内」，正好与下垂位相反；现场两次都看到 1.14，
            // 那只是这个硬编码门限，不是测量值）。
            double thetaBottom = ControlMath.WrapPi(t.ThetaFromUprightRad + Math.PI);
            if (Math.Abs(thetaBottom) > RadiusCalibrator.StartThetaLimit)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "摆杆不在自然下垂位：实测 θ(下垂起)={0:F3} rad、ψ(离直立)={1:F3} rad" +
                    "（要求 |θ(下垂起)| ≤ {2:F2} rad）。把摆杆放到自然下垂位并基本静止后再试。",
                    thetaBottom, t.ThetaFromUprightRad, RadiusCalibrator.StartThetaLimit);
                RaiseDiagnostic("r/l 标定被拒：" + reason);
                return false;
            }

            _pendingRadiusAccel = accelRadPerSec2;
            _pendingRadiusPulseSec = pulseSeconds;
            _pendingRadiusPairs = pairs;
            _pendingRadiusGapSec = Math.Max(0.6, 3.0 * pulseSeconds);
            Interlocked.Exchange(ref _radiusCalAbortRequested, 0);
            Interlocked.Exchange(ref _pendingCommand, CmdRadiusCalibration);
            RaiseDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "已请求 r/l 标定：脉冲 ±{0:F1} rad/s²、单相 {1:F0} ms、{2} 对；请保持摆杆下垂静止，不要触碰台架。",
                accelRadPerSec2, pulseSeconds * 1000.0, pairs));
            return true;
        }

        /// <summary>r/l 标定是否正在推进（脉冲 / 静置 / 测量）。</summary>
        public bool IsRadiusCalibrationActive
        {
            get { return Volatile.Read(ref _radiusCalActive) != 0; }
        }

        /// <summary>最近一次 r/l 标定结果；未跑过为 null。</summary>
        public RadiusCalibrationResult RadiusCalibrationResult
        {
            get { return Volatile.Read(ref _radiusCalResult); }
        }

        /// <summary>请求中止正在进行的 r/l 标定（下一拍生效，摆臂回到正常 hold）。</summary>
        public void AbortRadiusCalibration()
        {
            Interlocked.Exchange(ref _radiusCalAbortRequested, 1);
        }

        /// <summary>停机 / 断开 / 重新使能时清空 r/l 标定状态机（结果保留到下次标定开始）。</summary>
        private void ResetRadiusCalibration()
        {
            _radiusCal.Reset();
            Volatile.Write(ref _radiusCalActive, 0);
            Interlocked.Exchange(ref _radiusCalAbortRequested, 0);
        }


        /// <summary>回零运动是否正在推进（Enabled 态内）。UI 用它判断何时可以恢复「起摆 / 点动」。</summary>
        public bool IsReturnHomeActive
        {
            get { return Volatile.Read(ref _returnHomeActive) != 0; }
        }

        /// <summary>本次回零 / 回中目标的绝对 0x6064 counts；未启动时仍保留最近一次目标。</summary>
        public int ReturnHomeTargetCount
        {
            get { return Volatile.Read(ref _returnHomeTargetCount); }
        }

        /// <summary>摆臂零点当前是否可用于自动运动（用户设置 / 自动归零后为 true，清空标定后为 false）。</summary>
        public bool ArmZeroCalibrated
        {
            get { return Volatile.Read(ref _armZeroCalibrated) != 0; }
        }

        /// <summary>摆杆零点当前是否可用于角度换算 / 起摆（自动归零 / 用户设置后为 true，清空标定后为 false）。</summary>
        public bool PendulumZeroCalibrated
        {
            get { return Volatile.Read(ref _pendulumZeroCalibrated) != 0; }
        }

        /// <summary>
        /// 连接后首帧 raw 与参数文件保存零点不一致，需要用户先确认（规整化 / 重新采集 / 清空）。
        /// 置位期间 Enable / SwingUp / Balance / 自动回零一律拒绝，绝不自动加偏移。
        /// </summary>
        public bool CalibrationReviewRequired
        {
            get { return Volatile.Read(ref _calibrationReviewRequired) != 0; }
        }

        /// <summary>位置标定是否已被用户清空（清空后 Enable / 自动运动一律拒绝，直到三点重采齐全）。</summary>
        public bool CalibrationCleared
        {
            get { return Volatile.Read(ref _calibrationCleared) != 0; }
        }

        /// <summary>
        /// 零点 / 标定相对上次落盘是否已变（true = 需要保存参数才算持久化）。
        /// 使能自动归零、<see cref="ZeroArm"/> / <see cref="ZeroPendulum"/>、回中重锁零都会置位。
        /// </summary>
        public bool CalibrationDirty
        {
            get { return Volatile.Read(ref _calibrationDirty) != 0; }
        }

        /// <summary>保存参数成功后清零脏标记（CLI save-config / 界面保存参数调用）。</summary>
        public void ClearCalibrationDirty()
        {
            Volatile.Write(ref _calibrationDirty, 0);
        }

        /// <summary>
        /// 启动 / 载入参数时恢复跨重启的「标定已清空」标志。只设该标志，不写 / 不改零点与限位。
        /// true = 连自动归零也禁止，直到 UI 重新采集三点后显式 ConfirmCalibrationComplete。
        /// </summary>
        public void RestoreCalibrationClearedState(bool cleared)
        {
            if (!cleared)
            {
                Volatile.Write(ref _calibrationCleared, 0);
                return;
            }

            Volatile.Write(ref _calibrationCleared, 1);
            Volatile.Write(ref _armZeroCalibrated, 0);
            Volatile.Write(ref _pendulumZeroCalibrated, 0);
            Volatile.Write(ref _calibrationReviewChecked, 1);
            Volatile.Write(ref _calibrationReviewRequired, 0);
            Interlocked.Exchange(ref _armPoseDeltaCounts, 0L);
            Interlocked.Exchange(ref _pendulumPoseDeltaCounts, 0L);
            Volatile.Write(ref _phiZeroCount, 0);
            Volatile.Write(ref _countZeroRaw, 0u);
            Interlocked.Exchange(ref _pendingArmZeroApply, 0);
            _zeroArmCountPending = false;
            _zeroPendulumUserRequested = false;
            _zeroPulsePhase = 0;
            _zeroEstablished = true;
            _phiTargetPrimed = false;
            _lastTargetValid = false;
                        _phiRef = ArmHomeRad;
            RaiseDiagnostic("已恢复「标定已清空」状态：不会自动建立零点；重新采集零点 / 左右限位并确认后才允许使能。");
        }

        /// <summary>核对所需的样本是否已经拿到（false = 首帧遥测还没到，不能据此放行自动动作）。</summary>
        public bool CalibrationReviewChecked
        {
            get { return Volatile.Read(ref _calibrationReviewChecked) != 0; }
        }

        /// <summary>
        /// 持久化位置标定尚未在本次进程确认（置位期间 Enable / SwingUp / Balance / 回零一律拒绝）。
        /// 增量编码器（EC1 0x6064 / GC1602 AB 计数）没有可依赖的绝对多圈：程序无法证明文件里的
        /// counts 仍对应当前物理位置，只有用户在本会话里规整化或重新采集齐全后才解除。
        /// </summary>
        public bool PoseSessionReviewRequired
        {
            get { return Volatile.Read(ref _poseSessionReviewRequired) != 0; }
        }

        /// <summary>
        /// 置「位置标定未在本次进程确认」。只置标志 + 记一条诊断，不改任何标定值、不清首帧核对状态
        /// （首帧仍要算旧 / 当前 / 差值给界面看）。解除只走 <see cref="ConfirmPoseSessionReview"/>。
        /// </summary>
        public void RequirePoseSessionReview(string reason)
        {
            Volatile.Write(ref _poseSessionReviewRequired, 1);
            if (!string.IsNullOrEmpty(reason))
                EnqueueDiagnostic(reason);
        }

        /// <summary>
        /// 用户已在本次进程把两组位置点整体确认（规整化 / 重新采集齐全）：解除会话阻断。幂等；
        /// 只清这个标志，不动零点 / 限位（<see cref="ConfirmCalibrationReview"/> 那条链路各管各的）。
        /// </summary>
        public void ConfirmPoseSessionReview()
        {
            if (Interlocked.Exchange(ref _poseSessionReviewRequired, 0) == 0)
                return;
            EnqueueDiagnostic("位置标定已在本次进程确认：自动运动（使能 / 起摆 / 平衡 / 回零）恢复。");
        }

        /// <summary>会话阻断的中文拒绝原因（未阻断时 = null）。所有自动运动入口共用这一句，避免各处措辞分裂。</summary>
        private string PoseSessionReviewRejectText()
        {
            if (Volatile.Read(ref _poseSessionReviewRequired) == 0)
                return null;
            return "位置标定未在本次进程确认（增量编码器掉电后 raw 不保证与保存计数一致）："
                + "先按保存零点姿态「规整化」，或重新采集零点 / 左右限位与摆杆 0° / 360°；"
                + "不确定就「清空标定」重采。确认前不使能、不起摆。";
        }

        /// <summary>摆臂首帧 raw − 保存零点的差 [counts]；无需确认时为 0。</summary>
        public long ArmPoseDeltaCounts
        {
            get { return Interlocked.Read(ref _armPoseDeltaCounts); }
        }

        /// <summary>摆杆首帧 raw − 保存零点的差 [counts]；无需确认时为 0。</summary>
        public long PendulumPoseDeltaCounts
        {
            get { return Interlocked.Read(ref _pendulumPoseDeltaCounts); }
        }

        /// <summary>
        /// 用户已确认位置基准（规整化 / 重新采集完整三点 / 清空后重采）：解除自动动作阻断。
        /// 幂等；只清确认标志，不改零点 / 限位本身。
        /// </summary>
        public void ConfirmCalibrationReview()
        {
            Volatile.Write(ref _calibrationReviewChecked, 1);
            Volatile.Write(ref _calibrationReviewRequired, 0);
            Interlocked.Exchange(ref _armPoseDeltaCounts, 0L);
            Interlocked.Exchange(ref _pendulumPoseDeltaCounts, 0L);
        }

        /// <summary>
        /// 用户已重新采集完整的「零点 + 左限位 + 右限位」：解除清空状态，允许再次使能。
        /// 只改资格标志，不改任何标定值；失败返回原因（缺项 / 占位限位）。
        /// </summary>
        public bool ConfirmCalibrationComplete(out string reason)
        {
            reason = null;
            if (Volatile.Read(ref _armZeroCalibrated) == 0)
            {
                reason = "不能确认标定完成：摆臂零点仍未标定。先在「设备参数」采集 / 设置摆臂零点，再重新确认。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_limitsShadow.ArmLimitsArePlaceholder)
            {
                reason = "不能确认标定完成：左右软限位仍未采集（当前是占位全量）。先用点动采集左右限位（setlimits / 界面采集），再确认。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!_limitsShadow.ArmLimitsValid)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "不能确认标定完成：软限位不合法（[{0},{1}]，要求 min < max）。重新采集左右限位后再确认。",
                    _limitsShadow.ArmMinCount, _limitsShadow.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }
            int zero = Volatile.Read(ref _phiZeroCount);
            if (zero < _limitsShadow.ArmMinCount || zero > _limitsShadow.ArmMaxCount)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "不能确认标定完成：零点 raw={0} 不在软限位 [{1},{2}] 内。把零点改到限位内，或重新采集零点 / 限位后再确认。",
                    zero, _limitsShadow.ArmMinCount, _limitsShadow.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }
            Volatile.Write(ref _calibrationCleared, 0);
            ConfirmCalibrationReview();
            RaiseDiagnostic("标定确认完成：零点 + 左右限位齐全，自动运动资格已恢复。");
            return true;
        }

        /// <summary>
        /// 用一帧刚读到的 raw 核对保存零点。只做一次（连接后首帧），
        /// 不一致就把旧 / 当前 / 差值写进一条诊断，并保持自动动作阻断。
        /// </summary>
        private void UpdateCalibrationReview(int armPos, uint pendulumRaw)
        {
            if (Volatile.Read(ref _calibrationReviewChecked) != 0)
                return;
            bool haveArmReference = Volatile.Read(ref _armZeroOverrideSet) != 0;
            bool havePendulumReference = Volatile.Read(ref _pendulumZeroOverrideSet) != 0;
            if (!haveArmReference && !havePendulumReference)
            {
                Volatile.Write(ref _calibrationReviewChecked, 1);
                return;
            }

            long armDelta = haveArmReference
                ? (long)armPos - Volatile.Read(ref _armZeroOverride)
                : 0L;
            long pendulumDelta = havePendulumReference
                ? ControlMath.CountDelta(pendulumRaw, unchecked((uint)Volatile.Read(ref _pendulumZeroOverride)))
                : 0L;
            Interlocked.Exchange(ref _armPoseDeltaCounts, armDelta);
            Interlocked.Exchange(ref _pendulumPoseDeltaCounts, pendulumDelta);
            Volatile.Write(ref _calibrationReviewChecked, 1);

            if (armDelta == 0 && pendulumDelta == 0)
                return;

            // 摆臂零点偏差**不再阻断自动动作**（2026-11-20 用户裁定）：
            // 摆臂零点现在的语义是「本次使能位置 = 中心」，每次使能都会重新锁定；
            // 用户手动把摆臂挪回中心再使能是标准流程，此时 raw 与文件里上一次的零点不同是完全正常的。
            // 旧版据此判「标定不一致」并拒绝使能，现场看起来就是「怎么都不让使能、也看不到原因」。
            // 摆杆零点不同仍然阻断：θ 的垂位基准错了会让能量式 / 切换判据整体错位，必须人来确认。
            if (pendulumDelta == 0)
            {
                long savedArmZero = haveArmReference ? Volatile.Read(ref _armZeroOverride) : 0L;
                if (haveArmReference)
                {
                    Volatile.Write(ref _armZeroOverride, armPos);
                    Volatile.Write(ref _armZeroOverrideSet, 1);
                    Volatile.Write(ref _calibrationDirty, 1);
                }
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "摆臂 raw 与保存零点相差 {0} counts（保存 {1} → 当前 {2}）：按「每次使能重新锁零」自动采用当前位，" +
                    "不阻断使能。软限位是绝对 counts，不受零点平移影响。", armDelta,
                    savedArmZero, armPos));
                return;
            }

            Volatile.Write(ref _calibrationReviewRequired, 1);
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "位置标定异常：连接后首帧 raw 与保存的摆杆零点不一致，自动动作（使能 / 起摆 / 平衡 / 回零）已暂停。" +
                "摆臂：保存零点 raw={0}，当前 raw={1}，Δ={2} counts（不阻断，使能时重新锁零）；" +
                "摆杆：保存零点 raw={3}，当前 raw={4}，Δ={5} counts。" +
                "请确认机械是否在保存零点姿态：在就把当前位姿「规整化」，不确定就「清空标定」重新采集；"
                + "绝不自动加减偏移。",
                haveArmReference ? (long)Volatile.Read(ref _armZeroOverride) : 0L, armPos, armDelta,
                havePendulumReference ? (long)Volatile.Read(ref _pendulumZeroOverride) : 0L,
                unchecked((int)pendulumRaw), pendulumDelta));
        }

        /// <summary>
        /// 一次性写入「摆臂零点 + 左右软限位」。只允许未闭环 / 未捕获时调用；写入前先验证
        /// 零点落在新限位内，避免先改零点再被限位拒绝，留下半套标定。本方法不移动电机。
        /// </summary>
        public bool TryApplyArmCalibration(int armZeroCount, SafetyLimits limits, out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (IsClosedLoopRunning())
            {
                reason = "闭环运行中不能改写摆臂标定：请先「停止」去使能。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _returnHomeActive) != 0 || IsControlledStopActive)
            {
                reason = "回零 / 受控停进行中不能改写摆臂标定：等动作结束或先「停止」。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中不能改写摆臂标定：先取消捕获。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (!limits.ArmLimitsValid)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "摆臂软限位不合法（[{0},{1}]，要求 min < max），标定未写入。",
                    limits.ArmMinCount, limits.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }
            if (armZeroCount < limits.ArmMinCount || armZeroCount > limits.ArmMaxCount)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "零点 raw={0} 不在新软限位 [{1},{2}] counts 内，标定未写入。" +
                    "把零点改到限位内，或先重新采集左右限位；本次未写入任何值。",
                    armZeroCount, limits.ArmMinCount, limits.ArmMaxCount);
                RaiseDiagnostic(reason);
                return false;
            }
            if (!TrySetArmZeroCount(armZeroCount, out reason))
                return false;
            SetSafetyLimits(limits);
            Volatile.Write(ref _calibrationCleared, 0);
            // 规整化只确认摆臂这一路；摆杆若也有首帧偏移，必须等它重新采集 / 规整化后才解除整体阻断。
            Interlocked.Exchange(ref _armPoseDeltaCounts, 0L);
            if (Interlocked.Read(ref _pendulumPoseDeltaCounts) == 0)
                ConfirmCalibrationReview();
            else
                Volatile.Write(ref _calibrationReviewRequired, 1);
            // 零点 / 限位已改：标记待保存（CLI / 界面保存参数后 ClearCalibrationDirty）。
            Volatile.Write(ref _calibrationDirty, 1);
            return true;
        }

        /// <summary>
        /// 清空全部位置标定（摆臂零点 + 摆杆零点 + 左右限位）。换算尺度（摆杆一圈量 / 摆臂每转计数 /
        /// 减速比）与 PID / 泵参数保持不动。**不移动电机**；清空后自动运动必须被拒绝，直到重新标定。
        /// </summary>
        public bool ClearCalibration(out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (IsClosedLoopRunning())
            {
                reason = "闭环运行中不能清空标定：请先「停止」去使能。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (Volatile.Read(ref _returnHomeActive) != 0 || IsControlledStopActive)
            {
                reason = "回零 / 受控停进行中不能清空标定：等动作结束或先「停止」。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中不能清空标定：先取消捕获。";
                RaiseDiagnostic(reason);
                return false;
            }

            SafetyLimits limits = SnapshotRequestedLimits();
            limits.ArmMinCount = SafetyLimits.DefaultArmMinCount;
            limits.ArmMaxCount = SafetyLimits.DefaultArmMaxCount;
            SetSafetyLimits(limits);

            Volatile.Write(ref _armZeroOverride, 0);
            Volatile.Write(ref _armZeroOverrideSet, 0);
            Volatile.Write(ref _pendulumZeroOverride, 0);
            Volatile.Write(ref _pendulumZeroOverrideSet, 0);
            Volatile.Write(ref _armZeroCalibrated, 0);
            Volatile.Write(ref _pendulumZeroCalibrated, 0);
            Volatile.Write(ref _calibrationCleared, 1);
            // 清空后没有可比较的保存零点：解除「待确认」标记，但自动动作由 _calibrationCleared 继续拦。
            Volatile.Write(ref _calibrationReviewChecked, 1);
            Volatile.Write(ref _calibrationReviewRequired, 0);
            Interlocked.Exchange(ref _armPoseDeltaCounts, 0L);
            Interlocked.Exchange(ref _pendulumPoseDeltaCounts, 0L);
            Volatile.Write(ref _phiZeroCount, 0);
            Volatile.Write(ref _countZeroRaw, 0u);
            Interlocked.Exchange(ref _pendingArmZeroApply, 0);
            _zeroArmCountPending = false;
            _zeroPendulumUserRequested = false;
            _zeroPulsePhase = 0;
            _zeroEstablished = true;
            _thetaPrev = 0;
            _omegaFilt = 0;
            _phiPrev = 0;
            _phiDotFilt = 0;
            _phiTargetPrimed = false;
            _lastTargetValid = false;
                        _phiRef = ArmHomeRad;
            Volatile.Write(ref _returnHomeActive, 0);
            Volatile.Write(ref _returnHomeHoldCycles, 0);
            _returnHomeStartTicks = 0;

            RaiseDiagnostic("标定已清空：摆臂零点 / 摆杆零点 / 左右限位全部作废；"
                + "摆杆一圈量、PID、泵参数与减速比保持不动，电机未移动。重新采集前自动运动会被拒绝。");
            return true;
        }

        /// <summary>
        /// 检测并迁移旧版出厂起摆参数块（Ke=1 / AMax=8 / KPhi=6 / KPhiDot=1.5 / Timeout=30 s /
        /// 捕获门全 0）。这组值不是现场调出来的合理整定，旧版注释已实测它把起摆能力废掉；
        /// 只替换这一组旧默认，用户改过的能量裕量 / 角度阈值 / 泵幅原样保留。
        /// <para>纯函数，不碰引擎状态；界面载入参数后调用，CLI 读到旧块时由引擎拒绝起摆并给出原因。</para>
        /// </summary>
        public static bool TryMigrateLegacySwingDefaults(ref SwingUpParams p, out string note)
        {
            note = null;
            string detail;
            if (!IsLegacyFactorySwingDefaults(p, out detail))
                return false;

            SwingUpParams def = SwingUpParams.CreateDefault();
            double oldKe = p.Ke;
            double oldAMax = p.AMax;
            double oldKPhi = p.KPhi;
            double oldKPhiDot = p.KPhiDot;
            double oldPumpHz = p.PumpFrequencyHz;
            int oldTimeout = p.TimeoutMs;

            p.Ke = def.Ke;
            p.AMax = def.AMax;
            p.KPhi = def.KPhi;
            p.KPhiDot = def.KPhiDot;
            p.CenterAuthRatio = def.CenterAuthRatio;
            p.PumpFrequencyHz = def.PumpFrequencyHz;
            p.TimeoutMs = def.TimeoutMs;

            // 旧文件里没有这些键（0）或仍是旧默认时按当前默认补齐；用户显式给过的非零值保持不动。
            if (!(p.OmegaWindowMs > 0)) p.OmegaWindowMs = def.OmegaWindowMs;
            if (!(p.BootstrapAccelRadPerSec2 > 0)) p.BootstrapAccelRadPerSec2 = def.BootstrapAccelRadPerSec2;
            if (!(p.BootstrapSec > 0)) p.BootstrapSec = def.BootstrapSec;
            if (!(p.EnergySoftOmegaRadPerSec > 0)) p.EnergySoftOmegaRadPerSec = def.EnergySoftOmegaRadPerSec;
            if (!(p.ThetaFadeStartRad > 0) && !(p.ThetaFadeEndRad > 0) && !(p.ThetaFadeFloor > 0))
            {
                p.ThetaFadeStartRad = def.ThetaFadeStartRad;
                p.ThetaFadeEndRad = def.ThetaFadeEndRad;
                p.ThetaFadeFloor = def.ThetaFadeFloor;
            }
            if (p.CaptureModeAmplitudeMaxRad == 0 && p.CaptureStrokePerModeRad == 0 &&
                p.CaptureStrokeMarginRad == 0 && p.CaptureAdverseRateStrokeSec == 0 &&
                p.CaptureArmRateLimitRadPerSec == 0 && p.CapturePendulumRateRadPerSec == 0)
            {
                p.CaptureModeAmplitudeMaxRad = def.CaptureModeAmplitudeMaxRad;
                p.CaptureStrokePerModeRad = def.CaptureStrokePerModeRad;
                p.CaptureStrokeMarginRad = def.CaptureStrokeMarginRad;
                p.CaptureAdverseRateStrokeSec = def.CaptureAdverseRateStrokeSec;
                p.CaptureArmRateLimitRadPerSec = def.CaptureArmRateLimitRadPerSec;
                p.CapturePendulumRateRadPerSec = def.CapturePendulumRateRadPerSec;
            }

            note = string.Format(CultureInfo.InvariantCulture,
                "检测到旧版起摆出厂参数块（Ke={0:G} / AMax={1:G} / KPhi={2:G} / KPhiDot={3:G} / " +
                "Timeout={4} ms / 捕获门全 0），已迁移为当前出厂值 Ke={5:G} / AMax={6:G} / " +
                "KPhi={7:G} / KPhiDot={8:G} / Timeout={9} ms / 捕获门完整参数；PID 参数未改动。",
                oldKe, oldAMax, oldKPhi, oldKPhiDot, oldTimeout,
                p.Ke, p.AMax, p.KPhi, p.KPhiDot, p.TimeoutMs);
            return true;
        }

        /// <summary>旧版出厂起摆参数块的指纹。只用于迁移与起摆前拒绝，不参与控制律。</summary>
        public static bool IsLegacyFactorySwingDefaults(SwingUpParams p, out string detail)
        {
            detail = null;
            bool legacy =
                Math.Abs(p.Ke - 1.0) < 1e-9 &&
                Math.Abs(p.AMax - 8.0) < 1e-9 &&
                Math.Abs(p.KPhi - 6.0) < 1e-9 &&
                Math.Abs(p.KPhiDot - 1.5) < 1e-9 &&
                Math.Abs(p.PumpFrequencyHz - 1.0) < 1e-9 &&
                p.TimeoutMs > 0 && p.TimeoutMs <= 30000 &&
                p.CaptureModeAmplitudeMaxRad == 0 &&
                p.CaptureStrokePerModeRad == 0 &&
                p.CaptureStrokeMarginRad == 0 &&
                p.CaptureAdverseRateStrokeSec == 0 &&
                p.CaptureArmRateLimitRadPerSec == 0 &&
                p.CapturePendulumRateRadPerSec == 0;
            if (legacy)
            {
                detail = string.Format(CultureInfo.InvariantCulture,
                    "起摆参数仍是旧版出厂块：Ke={0:G}、AMax={1:G} rad/s²、KPhi={2:G}、KPhiDot={3:G}、" +
                    "Timeout={4} ms、捕获门全 0。这组值能量注入不足，30 s 内到不了捕获门（台架实测会以 " +
                    "SwingUpTimeout 结束）。请在参数卡「恢复默认」或重新载入参数后再起摆。",
                    p.Ke, p.AMax, p.KPhi, p.KPhiDot, p.TimeoutMs);
            }
            return legacy;
        }

        /// <summary>
        /// 请求切平衡。只在 Swinging 且切换判据已连续满足 N_c 拍时生效（人工抢接）；
        /// 在 Balancing 里是幂等确认：保持当前 PD 积分、回中调度与锚点，不重置控制器。
        /// Stopped / Fault 一律拒绝，必须重新 SwingUp()。
        /// </summary>
        public void Balance()
        {
            ThrowIfDisposed();
            EngineState state = State;
            if (state != EngineState.Enabled && state != EngineState.Swinging &&
                state != EngineState.Balancing)
            {
                RaiseDiagnostic(string.Format(
                    "当前状态 {0} 不能进平衡。急停/故障后摆杆早已偏离直立，直接进平衡会猛甩摆臂；"
                    + "先把摆杆扶到直立附近并 enable，或重新 SwingUp()。", state));
                return;
            }
            if (Volatile.Read(ref _calibrationReviewRequired) != 0)
            {
                RaiseDiagnostic("位置标定待确认（连接后 raw 与保存零点不一致），切平衡被拒：先确认标定。");
                return;
            }
            string poseSessionReason = PoseSessionReviewRejectText();
            if (poseSessionReason != null)
            {
                RaiseDiagnostic("切平衡被拒：" + poseSessionReason);
                return;
            }
            string limitReason;
            if (!CheckSoftLimitBeforeMotion("切平衡", out limitReason)) return;
            if (state == EngineState.Enabled)
            {
                // R11 捕获优先：Enabled 态只登记手动捕获请求，由周期线程用同一套捕获门逐拍判；
                // 不在这里直接切状态，避免 CLI 线程跨拍改控制状态。
                Interlocked.Exchange(ref _manualBalanceHoldCycles, 0);
                Volatile.Write(ref _manualBalanceRequest, 1);
                _manualBalanceLogTicks = 0;
                RaiseDiagnostic("已请求手动捕获平衡：把摆杆扶到直立附近并保持"
                    + "（|ψ|≤0.15、|ω_est|≤0.8、|φ̇|≤2.5、u 包线内）；连续 "
                    + ManualCaptureHoldCycles + " 拍满足后自动接管。");
                return;
            }
            Interlocked.Exchange(ref _pendingCommand, CmdBalance);
        }

        // ==================== 整圈捕获 ====================

        /// <summary>
        /// 「捕获」按钮：整圈捕获。已使能时进入等待抬升（摆臂目标钉在当前 0x6064，不主动驱动）：
        /// 摆杆从自然下垂 0° 起记零点参考 → 人把摆杆抬到顶部 180° → 释放 → 摆杆朝抬升方向转满一整圈
        /// （释放方向累计 360°）才算有效；「左侧来、左侧回」（方向反转 / 没绕满）判失效并写清原因。
        /// 释放后**不进平衡**：摆臂继续不动，摆杆自由摆完一整圈，数据全部落到记录里。
        /// 已在等待 / 记录中再点一次 = 取消（记录中取消会落一条「用户取消」记录）；其余状态拒绝并说明原因。
        /// </summary>
        public void ReleaseCapture()
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _jogDirection) != 0)
            {
                RaiseDiagnostic("手动点动中不能进入整圈捕获，先松开手动按钮。");
                return;
            }
            if (_stopPhase != 0)
            {
                RaiseDiagnostic("受控停进行中不能进入整圈捕获，等停完再试。");
                return;
            }
            if (IsEStopLatched)
            {
                RaiseDiagnostic("急停锁存未清，整圈捕获被拒绝：先「故障复位 / 停止」再使能。");
                return;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                Interlocked.Exchange(ref _pendingCommand, CmdCancelRelease);
                RaiseDiagnostic("已请求取消整圈捕获。");
                return;
            }
            EngineState state = State;
            if (state != EngineState.Enabled && state != EngineState.Stopped)
            {
                RaiseDiagnostic(string.Format(
                    "当前状态 {0} 不能进入整圈捕获：先「使能」（Stopped 会自动重新使能）。", state));
                return;
            }
            if (_countsPerRevPendulum <= 0)
            {
                RaiseDiagnostic("摆杆每转计数未标定（≤0），无法判定 θ，整圈捕获被拒。先设「摆杆每转计数」。");
                return;
            }
            string limitReason;
            if (!CheckSoftLimitBeforeMotion("整圈捕获", out limitReason)) return;
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen)
            {
                RaiseDiagnostic("总线未连接，不能进入整圈捕获。");
                return;
            }

            // ---- 整圈捕获的前置门（不满足就明确拒绝，绝不录一堆常数）----
            // 判据刻意分两层，不混在一起：
            //   ① 读数**有没有效**（有快照 / θ 有限 / 换算没到乱码量级）—— 无效才拒绝；
            //   ② 读数**当前动没动** —— 不作拒绝条件。用户流程就是「摆杆垂下静止 → 点捕获 → 抬起摆臂」，
            //      点捕获那一刻静止是正常的；「该动的时候没动」由 CaptureSession 在抬升段判
            //      （摆臂在走 且 CH1 计数严格不变），记录段另有独立的 1.5 s 冻结判据。
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry) || telemetry.Sequence == 0)
            {
                RaiseDiagnostic("没有可用的遥测快照（周期还没跑起来），整圈捕获被拒：等状态行刷新后再点。");
                return;
            }
            double startTheta = telemetry.ThetaRad;
            double feedbackAge = PendulumFeedbackAgeSeconds;
            string unusableReason;
            if (!IsPendulumReadingUsable(telemetry, out unusableReason))
            {
                RaiseDiagnostic(string.Format(
                    "摆杆读数无效（原始计数 {0}），整圈捕获被拒：{1}。" +
                    "检查编码器接线 / GC1602 计数通道后再点「捕获」。",
                    telemetry.PendulumCount, unusableReason));
                return;
            }
            if (feedbackAge > PendulumStaticNoticeSeconds)
            {
                // 静止本身不是错误：明确告诉操作员"现在可以点"，以及真正该查线的是下一步。
                string staticFor = ControlMath.IsFinite(feedbackAge)
                    ? string.Format("摆杆当前静止 {0:F1} s", feedbackAge)
                    : "摆杆当前静止（本次连接后 CH1 计数从未变化）";
                RaiseDiagnostic(string.Format(
                    "{0}（CH1 原始计数 {1}）：这是正常的起步状态，捕获可以开始。" +
                    "抬起摆臂后若计数仍无变化，才会判定编码器无响应。",
                    staticFor, telemetry.PendulumCount));
            }
            double startDeviationDeg = Math.Abs(ControlMath.WrapPi(startTheta)) * (180.0 / Math.PI);
            if (startDeviationDeg > CaptureStartMaxDeviationDeg)
            {
                RaiseDiagnostic(string.Format(
                    "整圈捕获要从自然下垂位（θ = 0°）开始：当前 θ = {0:F1}°，已偏离下垂位超过 {1:F0}°。" +
                    "先把摆杆放回自然下垂并静止，再点「捕获」。",
                    startTheta * (180.0 / Math.PI), CaptureStartMaxDeviationDeg));
                return;
            }
            double startOmega = telemetry.OmegaRadPerSec;
            if (Math.Abs(startOmega) > CaptureStartMaxOmegaRadPerSec)
            {
                RaiseDiagnostic(string.Format(
                    "摆杆还在摆动（ω = {0:F2} rad/s），整圈捕获被拒：先让它自然下垂静止（|ω| ≤ {1:F1} rad/s），再点「捕获」。",
                    startOmega, CaptureStartMaxOmegaRadPerSec));
                return;
            }

            Interlocked.Exchange(ref _pendingCommand, CmdReleaseCapture);
            RaiseDiagnostic(string.Format(
                "已请求整圈捕获：从当前 θ = {0:F1}° 记零点参考，摆臂保持不动。" +
                "请把摆杆抬到顶部（180°，±8° 会提示已上膛）后释放；摆杆要朝抬升方向转满一整圈才算有效，" +
                "「左侧来、左侧回」（方向反转 / 没绕满）会判失效并写清原因。",
                startTheta * (180.0 / Math.PI)));
        }

        /// <summary>正在等待释放（摆臂保持不动，等人松手）。</summary>
        public bool IsReleaseWaitActive
        {
            get { return _capture.IsWaiting; }
        }

        /// <summary>捕获尝试进行中（整圈记录中，摆臂保持不动）。</summary>
        public bool IsCaptureAttemptActive
        {
            get { return _capture.IsCapturing; }
        }

        /// <summary>捕获已在顶部窗口稳定住（可以松手了）。</summary>
        public bool IsCaptureArmed
        {
            get { return _capture.IsArmed; }
        }

        /// <summary>本次整圈捕获相对释放点的有符号累计角 [deg]（按释放方向为正）。</summary>
        public double CaptureSweepDeg
        {
            get { return _capture.SweepDeg; }
        }

        /// <summary>本次整圈捕获在释放方向上的最大累计角 [deg]（≥360 = 绕满一圈）。</summary>
        public double CaptureMaxSweepDeg
        {
            get { return _capture.MaxSweepDeg; }
        }

        /// <summary>整圈捕获已记下的关键点数。</summary>
        public int CaptureKeyPointCount
        {
            get { return _capture.KeyPointCount; }
        }

        /// <summary>整圈捕获的失效原因（进行中 / 有效 = null）。</summary>
        public string CaptureInvalidReason
        {
            get { return _capture.InvalidReason; }
        }

        /// <summary>摆杆 CH1 计数距最近一次变化的秒数；从未见过变化 = <see cref="double.PositiveInfinity"/>。</summary>
        public double PendulumFeedbackAgeSeconds
        {
            get
            {
                long ticks = Volatile.Read(ref _pendulumLastChangeTicks);
                if (ticks == 0) return double.PositiveInfinity;
                double age = (Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency;
                return age < 0 ? 0 : age;
            }
        }

        /// <summary>
        /// 摆杆读数是否有效（信号有效性）：有真实遥测快照、不是未连接态、θ 有限、累计圈数没到乱码量级。
        /// <b>不再要求「最近 0.5 s 变化过」</b> —— 用户流程是「摆杆自然下垂静止 → 点捕获 → 抬起摆臂」，
        /// 点捕获那一刻静止是正常状态。是否「该动的时候动了」由 <see cref="CaptureSession"/> 在抬升段判：
        /// 摆臂在走（行程达到阈值）而 CH1 计数严格不变连续 1.5 s，才判编码器无响应。
        /// </summary>
        public bool IsPendulumFeedbackLive
        {
            get
            {
                if (State == EngineState.Disconnected) return false;
                PendulumTelemetry snapshot;
                if (!TryReadTelemetry(out snapshot) || snapshot.Sequence == 0) return false;
                // 必须是**本次连接**产生且仍在新鲜窗口内的快照：重连首拍前 seqlock 里留的是上一会话的数据，
                // 旧 Sequence 很大，不判时间戳/会话就会被当成“当前有效”。
                if (_sessionStartTicks != 0 && snapshot.TimestampTicks < _sessionStartTicks) return false;
                double ageSec = (Stopwatch.GetTimestamp() - snapshot.TimestampTicks) / (double)Stopwatch.Frequency;
                if (ageSec < 0 || ageSec > FreshTelemetryMaxAgeSeconds) return false;
                string reason;
                return IsPendulumReadingUsable(snapshot, out reason);
            }
        }

        /// <summary>
        /// 点「捕获」时「摆杆当前静止」的提示阈值 [s]：超过它仍<b>允许</b>开始，只在诊断 / 日志里说明。
        /// 量级依据：人从点按钮到开始抬摆臂通常 &lt;2 s；2 s 足够区分「静止起步」和「操作员还没动」，
        /// 又不会把正常起步当故障。真正的失效判据不在这里，而在抬升段「摆臂动了、计数不动」。
        /// </summary>
        public const double PendulumStaticNoticeSeconds = 2.0;

        /// <summary>
        /// 捕获起点允许的最大累计角 [rad]（1000 圈）。θ 是**未折返的绝对角**（从使能零点累计，跨圈不归一）：
        /// 一次整圈捕获就 +360°，所以不能拿 |θ| &gt; 200° 这种单圈界去拒 —— 那会误拒第 2 次及以后的捕获。
        /// 1000 圈只用于抓「计数换算乱码」（32 位计数乱跳是几十万圈量级），远大于任何真实使用。
        /// </summary>
        private const double MaxPlausibleAccumulatedAngleRad = 1000.0 * 2.0 * Math.PI;

        /// <summary>捕获起点允许偏离自然下垂位的上限 [deg]：再大就不在「从 0 开始」的流程里。</summary>
        private const double CaptureStartMaxDeviationDeg = 90.0;

        /// <summary>捕获起点允许的摆杆角速度上限 [rad/s]：还在摆动就等它停。</summary>
        private const double CaptureStartMaxOmegaRadPerSec = 0.5;

        /// <summary>
        /// 读数是否可用 —— 只判信号本身（有限 / 量级可解释），<b>与当前是否在动无关</b>。
        /// 返回 false 时 <paramref name="reason"/> 给出可直接给操作员看的原因。
        /// </summary>
        private static bool IsPendulumReadingUsable(PendulumTelemetry snapshot, out string reason)
        {
            reason = null;
            if (!ControlMath.IsFinite(snapshot.ThetaRad))
            {
                reason = "θ 不是有限数（计数换算异常）";
                return false;
            }
            if (Math.Abs(snapshot.ThetaRad) > MaxPlausibleAccumulatedAngleRad)
            {
                reason = string.Format("累计角 |θ| = {0:F0}° 超出可解释范围（疑似计数换算乱码）",
                    Math.Abs(snapshot.ThetaRad) * (180.0 / Math.PI));
                return false;
            }
            return true;
        }

        /// <summary>周期线程调用：跟踪摆杆原始计数最近一次变化的时刻。</summary>
        private void UpdatePendulumLiveness(long nowTicks, uint pendulumRaw)
        {
            if (!_pendulumLivenessPrimed)
            {
                _pendulumLivenessPrimed = true;
                _pendulumRawPrev = pendulumRaw;
                Volatile.Write(ref _pendulumLastChangeTicks, nowTicks);
                return;
            }
            if (pendulumRaw != _pendulumRawPrev)
            {
                _pendulumRawPrev = pendulumRaw;
                Volatile.Write(ref _pendulumLastChangeTicks, nowTicks);
            }
        }

        /// <summary>连接 / 断开时清空存活判据，避免跨会话拿旧时刻当「刚变化过」。</summary>
        private void ResetPendulumLiveness()
        {
            _pendulumLivenessPrimed = false;
            _pendulumRawPrev = 0;
            Volatile.Write(ref _pendulumLastChangeTicks, 0);
        }

        /// <summary>
        /// 按当前样本做一次系统辨识（自然频率 / 阻尼 / 偏置 / 可捕获角速度上限 / 泵参数 / k_e 初值）。
        /// 纯计算，不改任何参数；是否应用由调用方（UI / CLI）决定。样本不足时 Sufficient=false，绝不编数。
        /// </summary>
        public IdentificationResult IdentifyNow()
        {
            ThrowIfDisposed();
            // 用「用户当前请求的参数」（影子）而不是周期顶换后的 active：断开时也按界面值算 k_e / 目标能量。
            return _identifier.Identify(_limitsShadow, _swingShadow);
        }

        /// <summary>取最近 20 条捕获记录（按时间先后，最新在最后）。</summary>
        public CaptureRecord[] CaptureRecordsSnapshot()
        {
            return _identifier.SnapshotCaptures();
        }

        /// <summary>导入持久化的捕获记录（启动自动载入时调用；整体替换内存历史）。</summary>
        public void ImportCaptureRecords(System.Collections.Generic.IEnumerable<CaptureRecord> records)
        {
            _identifier.ImportCaptures(records);
        }

        /// <summary>
        /// 受控停（QuickStop 路径）：0x0002 Quick Stop 按 0x6085 急停减速度斜坡减速 → 确认 QuickStopActive →
        /// 0x0000 去使能 → Stopped。不直接断力矩（断力矩 = 负载自由滑行），等待有界（2 s），
        /// 超时 / 故障 / 本就未使能直接收尾，不卡死在中间态。
        /// 同时是故障复位动作：清 EStopLatch。清之前只读复检 S11，硬接线急停没松开就保持 Fault 并拒绝。
        /// </summary>
        public void Stop()
        {
            ThrowIfDisposed();
            if (State == EngineState.Disconnected)
            {
                RaiseDiagnostic("总线没连，没什么可停。");
                return;
            }
            Interlocked.Exchange(ref _eStopRequested, 0);
            Interlocked.Exchange(ref _pendingCommand, CmdStop);
            // 用户停 / fault-reset：请求周期线程清零 S6 的逐拍计数。
            // 真正的自清仍由下一拍"输入有效"分支在安全联锁评估之前完成；
            // 这里保证即使下一拍就来评估，也不会拿上一次故障的旧计数再跳一次。
            Interlocked.Exchange(ref _staleResetRequested, 1);
            RaiseDiagnostic("已请求受控停。");
        }

        /// <summary>
        /// 任意态立即把 0x6040 清成 0x0000（Disable Voltage）→ Fault，并置 EStopLatch。
        /// 插队到所有命令前面。软件急停只是清 0x6040，不替代硬接线急停 / STO / 限位。
        /// <para>
        /// 硬路径不排队：除了置请求位，**调用线程还会直接带外写过程映像**。
        /// 周期线程卡在控制律 / 总线里时，请求位可能永远没人消费，带外这一笔照样下发到驱动器。
        /// </para>
        /// </summary>
        public void EmergencyStop()
        {
            ThrowIfDisposed();
            // 先立请求位：周期线程正常时会在下一拍再写一遍安全输出，并收口它私有的使能子流程状态。
            Volatile.Write(ref _eStopRequested, 1);
            Interlocked.Exchange(ref _pendingCommand, CmdEStop);
            Volatile.Write(ref _eStopLatch, 1);
            // 断线等没有控制拍的场景也必须能回显“为什么锁存”：把 UserStop 记进实时故障码
            // （已有其它故障码时保留第一条，不覆盖真正根因）。
            Interlocked.CompareExchange(ref _faultCode, (int)FaultCode.UserStop, (int)FaultCode.None);

            string error;
            int holdCount;
            if (!TryWriteDisableOutOfBand(out holdCount, out error))
            {
                RaiseDiagnostic("急停：请求位已置，但带外写 0x6040=0 未成功（" +
                    (string.IsNullOrEmpty(error) ? "总线会话不可写" : error) +
                    "）。立即按硬接线急停 / 切断驱动器 STO；软件侧只能等周期线程恢复后消费请求位。");
                return;
            }
            if (!string.IsNullOrEmpty(error))
            {
                RaiseDiagnostic("急停：已带外写 0x6040=0（目标钉在 " + holdCount + "），但" + error +
                    "。请目视确认驱动器已断使能；未确认就按硬接线急停。");
                return;
            }
            RaiseDiagnostic("急停已下发：调用线程已带外写 0x6040=0，目标钉在 " + holdCount + "，回读确认驱动器已退出 Operation Enabled。");
        }

        /// <summary>
        /// 带外去使能：在调用线程直接写过程映像，写序为
        /// ① 0x607A 目标钉到实测位置 → ② 0x6060 = CSP → ③ 0x6040 = 0x0000（Disable Voltage），
        /// 最后回读状态字确认驱动器真的退出了 Operation Enabled。
        /// <para>
        /// 先钉目标再断使能：中途若被总线取走一帧，最坏也只是"目标=实测位置 + 旧使能"，不会产生运动。
        /// 读不到实测位置时沿用上一拍已确认的目标，绝不写 0 制造位置跳变。
        /// </para>
        /// </summary>
        /// <param name="holdCount">目标被钉住的计数值。</param>
        /// <param name="error">失败 / 未确认的原因；全部正常时为 null。</param>
        /// <returns>false = 没有可写的会话或写不进去；true = 已写入过程映像。</returns>
        private bool TryWriteDisableOutOfBand(out int holdCount, out string error)
        {
            holdCount = 0;
            error = null;
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen) return false;

            try
            {
                ref readonly StepperTxPdo input = ref bus.StepperInput();
                holdCount = input.ActualPosition;
            }
            catch (Exception ex)
            {
                holdCount = _lastTargetValid ? _lastTargetCount : 0;
                error = "读实测位置失败（沿用上一拍目标 " + holdCount + "）：" + DiagText.Describe(ex);
            }

            try
            {
                ref StepperRxPdo output = ref bus.StepperOutput();
                output.TargetPosition = holdCount;
                output.ModesOfOperation = CiA402.ModeCsp;
                output.ControlWord = CiA402.CwDisableVoltage;
            }
            catch (Exception ex)
            {
                error = "写 0x6040=0 失败：" + DiagText.Describe(ex);
                return false;
            }

            _lastTargetCount = holdCount;
            _lastTargetValid = true;

            // 回读确认：判据取驱动器状态字，不看我们自己刚写的输出映像
            //（周期线程还活着时会照常消费急停请求再写一遍，输出映像的写入时序不能当唯一证据）。
            long startTicks = Stopwatch.GetTimestamp();
            long deadline = startTicks + (long)(EStopConfirmSeconds * Stopwatch.Frequency);
            while (true)
            {
                try
                {
                    ref readonly StepperTxPdo input = ref bus.StepperInput();
                    if (!CiA402.IsOperationEnabled(input.StatusWord)) return true;
                }
                catch (Exception ex)
                {
                    error = "回读状态字失败：" + DiagText.Describe(ex);
                    return true;
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    error = string.Format("{0:F0} ms 内驱动器状态字仍显示 Operation Enabled。",
                        EStopConfirmSeconds * 1000.0);
                    return true;
                }
                Thread.Sleep(EStopConfirmPollMs);
            }
        }

        // ==================== 手动点动 ====================

        /// <summary>点动中（JogStart 后、JogStop 前）。UI 用它做「手动中禁启动」互斥。</summary>
        public bool IsJogActive
        {
            get { return Volatile.Read(ref _jogDirection) != 0; }
        }

        /// <summary>
        /// 受控停序列进行中（QuickStop 路径 0x0002→0x0000 未走完），含收尾后驱动尚挂快停态的短暂余量。
        /// UI / CLI 用它挡并发操作、免把自己送的 QuickStopActive 误读成硬急停。
        /// </summary>
        public bool IsControlledStopActive
        {
            get { return Volatile.Read(ref _stopPhase) != 0 || Volatile.Read(ref _stopGraceCycles) > 0; }
        }

        /// <summary>
        /// 手动点动开始：摆臂目标按 <see cref="JogSpeedRadPerSec"/> 朝 direction（-1 左 / +1 右）推进，
        /// 只受点动速度与每拍增量护栏管。按住即动、松开即停（<see cref="JogStop"/>）。
        /// 只在 Enabled（Ready）态有效：未使能、自动运行（起摆 / 平衡）、受控停 / 急停锁存中一律拒绝并报原因。
        /// <b>点动不受软限位约束</b>：软限位是用户要靠点动去采集的行程边界，点动本身必须先能走到边界外，
        /// 否则「左限位 / 右限位 / 0 点」永远采不出来。软限位只在起摆 / 平衡等自动运动里生效。
        /// </summary>
        public void JogStart(int direction)
        {
            string reason;
            TryJogStart(direction, out reason);
        }

        /// <summary>
        /// 同 <see cref="JogStart"/>，但把拒绝原因带出来给前端显示（前端不该只看到一个泛化的「引擎侧拒绝」）。
        /// 返回 false = 未激活点动，<paramref name="reason"/> 是可读原因（同时已发诊断）。
        /// </summary>
        public bool TryJogStart(int direction, out string reason)
        {
            ThrowIfDisposed();
            if (direction != -1 && direction != 1)
                return RejectMotion("点动方向只能是 -1（左）或 +1（右），收到 " + direction + "。", out reason);
            if (_stopPhase != 0)
                return RejectMotion("受控停进行中，点动被拒。", out reason);
            if (Volatile.Read(ref _returnHomeActive) != 0)
                return RejectMotion("回零进行中，点动被拒：等回零走完或先「停止」。", out reason);
            if (IsEStopLatched)
                return RejectMotion("急停锁存未清，点动被拒：先「停止」复位再使能。", out reason);
            if (_capture.IsWaiting)
                return RejectMotion("整圈捕获等待中点动被拒：摆臂要保持不动，先取消等待。", out reason);
            if (_capture.IsCapturing)
                return RejectMotion("整圈捕获记录中点动被拒：摆臂要保持不动，先结束捕获。", out reason);
            EngineState state = State;
            if (state != EngineState.Enabled)
            {
                return RejectMotion(string.Format(
                    "点动只在已使能（Ready）态有效，当前 {0}，点动无效。先「使能」再点动。", state), out reason);
            }

            // 点动不读、不写、不校验软限位：限位是用户靠点动采集出来的结果，不是点动的前提。
            Volatile.Write(ref _jogDirection, direction);
            reason = null;
            return true;
        }

        /// <summary>拒绝一次运动入口：发诊断并返回 false，原因经 out 交给前端。</summary>
        private bool RejectMotion(string message, out string reason)
        {
            reason = message;
            RaiseDiagnostic(message);
            return false;
        }

        /// <summary>
        /// 用户当前请求的软限位快照（影子缓冲，加锁读；控制环下一拍就按它顶换）。
        /// 公开的 <see cref="SafetyLimits"/> 也走这里 —— 内外读同一份真相，不出现两个口径。
        /// </summary>
        private SafetyLimits SnapshotRequestedLimits()
        {
            lock (_paramGate)
            {
                return _limitsShadow;
            }
        }

        /// <summary>
        /// 自动运动（起摆 / 平衡 / 整圈捕获）的软限位前置校验。软限位在这里**不中止流程**：
        /// 起点（0 点计数）若不在 [ArmMinCount, ArmMaxCount] 内，控制环每拍本来就会把目标钳到最近的边界，
        /// 所以这里只记一条日志（如实记录越界事实）并放行，绝不因为限位把整个起摆 / 平衡拒掉。
        /// 唯一仍然拒绝的是「限位本身不合法」（min ≥ max 或超出 counts 合理区间）—— 那不是限位语义，
        /// 是配置错误，钳位区间都构造不出来，必须先把参数改对。
        /// </summary>
        private bool CheckSoftLimitBeforeMotion(string action, out string reason)
        {
            SafetyLimits limits = SnapshotRequestedLimits();
            if (!limits.ArmLimitsValid)
            {
                return RejectMotion(string.Format(CultureInfo.InvariantCulture,
                    "摆臂软限位不合法（绝对 counts [{0},{1}]，要求 min < max），" + action +
                    "被拒：先在「设备参数 / 软限位」里重新采集或手输左右限位。",
                    limits.ArmMinCount, limits.ArmMaxCount), out reason);
            }
            // 起始位置 = 坐标 0 = 当前零点计数。它是否落在绝对计数区间内，**直接按 counts 判**，不绕角度。
            int homeCount = Volatile.Read(ref _phiZeroCount);
            if (homeCount < limits.ArmMinCount || homeCount > limits.ArmMaxCount)
            {
                long clamped = homeCount < limits.ArmMinCount ? limits.ArmMinCount : limits.ArmMaxCount;
                RaiseDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[LIMIT] {0}：起点（0 点计数 {1}）不在软限位 [{2}, {3}] counts 内，" +
                    "按限位钳位放行（回中目标钳到 {4} counts），流程不中止。" +
                    "要恢复真回中请把软限位改成包含 0 点计数的区间。",
                    action, homeCount, limits.ArmMinCount, limits.ArmMaxCount, clamped));
            }
            reason = null;
            return true;
        }

        /// <summary>点动松开：目标冻结在当前位置（松开即停），摆臂保持不回中、不跳变。</summary>
        public void JogStop()
        {
            Volatile.Write(ref _jogDirection, 0);
        }

        // ==================== 参数 ====================

        /// <summary>写平衡级联增益的影子缓冲并自增 Generation，下一拍顶换，热生效。</summary>
        public void SetBalancePid(PidGains gains)
        {
            string reason;
            if (!gains.IsValid(out reason))
            {
                RaiseDiagnostic("平衡增益不合法，未写入：" + reason);
                return;
            }
            // 旧版参数文件里没有加速度律字段（读出来全 0）：按内置默认补齐，
            // 绝不能让 Ka=0 进控制律 —— 那比不稳还糟，外环会完全没有摆杆反馈。
            bool substituted = ApplyBalanceLawDefaults(ref gains, _limitsActive);
            lock (_paramGate)
            {
                gains.Generation = _pidShadow.Generation + 1;
                Interlocked.Increment(ref _paramVersion);
                _pidShadow = gains;
                Interlocked.Increment(ref _paramVersion);
            }
            Volatile.Write(ref _paramDirty, 1);
            Interlocked.Exchange(ref _pendingCommand, CmdSetGains);
            RaiseDiagnostic(string.Format(
                "平衡增益已排队：Ka={0} Kad={1} Kai={2} Kφ={3} Kφ̇={4} AccMax={5:F0} ArmRateMax={6:F2} " +
                "| 内环 KpPhi={7} KiPhi={8} KdPhi={9} gen={10}",
                ControlMath.Gain(gains.Ka), ControlMath.Gain(gains.Kad), ControlMath.Gain(gains.Kai),
                ControlMath.Gain(gains.KPhi), ControlMath.Gain(gains.KPhiDot),
                gains.AccMax, gains.ArmRateMax,
                ControlMath.Gain(gains.KpPhi), ControlMath.Gain(gains.KiPhi), ControlMath.Gain(gains.KdPhi),
                gains.Generation));
            NoticeLegacyGainsIfNeeded(substituted, false);
        }

        /// <summary>写起摆参数的影子缓冲并自增 Generation，下一拍顶换。</summary>
        public void SetSwingUpParams(global::FurutaPendulum.Control.SwingUpParams p)
        {
            string reason;
            if (!p.IsValid(out reason))
            {
                RaiseDiagnostic("起摆参数不合法，未写入：" + reason);
                return;
            }
            lock (_paramGate)
            {
                p.Generation = _swingShadow.Generation + 1;
                Interlocked.Increment(ref _paramVersion);
                _swingShadow = p;
                Interlocked.Increment(ref _paramVersion);
            }
            Volatile.Write(ref _paramDirty, 1);
            Interlocked.Exchange(ref _pendingCommand, CmdSetSwingUpParams);
            RaiseDiagnostic(string.Format(
                "起摆参数已排队：Ke={0} AMax={1:F3} KPhi={2} KPhiDot={3} 泵幅={4:F3}rad/s² 泵频={5:F2}Hz gen={6}",
                ControlMath.Gain(p.Ke), p.AMax, ControlMath.Gain(p.KPhi), ControlMath.Gain(p.KPhiDot),
                p.PumpAmplitude, p.PumpFrequencyHz, p.Generation));
        }

        /// <summary>写安全阈值的影子缓冲并自增 Generation，下一拍顶换。</summary>
        public void SetSafetyLimits(SafetyLimits s)
        {
            string reason;
            if (!s.IsValid(out reason))
            {
                RaiseDiagnostic("安全阈值不合法，未写入：" + reason);
                return;
            }
            lock (_paramGate)
            {
                s.Generation = _limitsShadow.Generation + 1;
                Interlocked.Increment(ref _paramVersion);
                _limitsShadow = s;
                Interlocked.Increment(ref _paramVersion);
            }
            Volatile.Write(ref _paramDirty, 1);
            RaiseDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "安全阈值已排队：θ_trip={0:F3} θ_lost={1:F3} φ_err={2:F3} 软限位=[{3},{4}] counts{5} ω_arm={6:F2} gen={7}",
                s.ThetaTripRad, s.ThetaLostRad, s.PhiErrMaxRad, s.ArmMinCount, s.ArmMaxCount,
                s.ArmLimitsArePlaceholder ? "（尚未采集：全量占位）" : "",
                s.OmegaArmMaxRadPerSec, s.Generation));
        }

        /// <summary>
        /// 当前配置的平衡级联增益（最近一次被 <see cref="SetBalancePid"/> 接受的那整组值）。
        /// 参数唯一真相源：CLI / UI / 保存 / 诊断读到的永远是用户最后设的值，
        /// 不因「周期线程还没顶换」「当前 Disconnected 没有周期线程」而读回默认值。
        /// 控制拍上真正生效的是私有的 <see cref="_pidActive"/>，只在周期线程拍首顶换。
        /// </summary>
        public PidGains BalancePid
        {
            // 结构体拷贝不是原子操作：与 SetBalancePid 的写入共用 _paramGate，避免读到半新半旧的一组值。
            get { lock (_paramGate) { return _pidShadow; } }
        }

        /// <summary>
        /// 当前配置的起摆参数（最近一次被 <see cref="SetSwingUpParams"/> 接受的那整组值）。
        /// 与控制拍上的生效副本 <see cref="_swingActive"/> 分离，理由同 <see cref="BalancePid"/>。
        /// </summary>
        public global::FurutaPendulum.Control.SwingUpParams SwingUpParams
        {
            get { lock (_paramGate) { return _swingShadow; } }
        }

        /// <summary>
        /// 当前配置的安全阈值（最近一次被 <see cref="SetSafetyLimits"/> 接受的那整组值）。
        /// 与控制拍上的生效副本 <see cref="_limitsActive"/> 分离，理由同 <see cref="BalancePid"/>。
        /// </summary>
        public SafetyLimits SafetyLimits
        {
            get { return SnapshotRequestedLimits(); }
        }

        /// <summary>
        /// 摆杆编码器每转计数 CPR_PENDULUM（线数 × 4 × 传动比）。现场标定值，0 = 未标定。
        /// 只允许在 Disconnected / Connected / Stopped 改：θ 靠它把计数换成角度，运行中改会让 θ 在一拍内跳变。
        /// 运行态写入直接拒绝并报诊断，值保持原样（读回旧值即可判定没生效）。
        /// </summary>
        public double PendulumCountsPerRev
        {
            get { return _countsPerRevPendulum; }
            set
            {
                EngineState state = State;
                if (state != EngineState.Disconnected && state != EngineState.Connected && state != EngineState.Stopped)
                {
                    RaiseDiagnostic(string.Format(
                        "摆杆每转计数只在 Disconnected / Connected / Stopped 可改，当前 {0}，写入被拒，仍是 {1}。",
                        state, _countsPerRevPendulum));
                    return;
                }
                _countsPerRevPendulum = value;
                RaiseDiagnostic(string.Format("摆杆每转计数已设为 {0}（线数 × 4 × 传动比）。", value));
            }
        }

        /// <summary>
        /// 启动命令行是否显式给了摆杆每转计数（含显式 0）。true = 参数文件的
        /// <c>calibration.pendulum_cpr</c> 不覆盖它；false = 参数文件值优先于默认 2400。
        /// </summary>
        public bool PendulumCountsPerRevExplicit
        {
            get { return _options.PendulumCountsPerRevExplicit; }
        }

        /// <summary>
        /// 摆臂减速比 = 电机转数 / 摆臂转数（构造期定值，运行期没有改写口）。
        /// 前端保存参数文件时把它记进 <c>calibration.arm_gear_ratio</c>，下次启动由入口恢复。
        /// </summary>
        public double ArmGearRatio
        {
            get { return _options.ArmGearRatio; }
        }

        /// <summary>
        /// 摆臂每转计数 CPR_ARM（电机每转计数 P × 减速比），摆臂标定的**权威值**。
        /// 0 = 未标定（此时若 0x2102 × ArmGearRatio 也推不出就拒绝运动，不猜）；给值就以它为准。
        /// 与 <see cref="PendulumCountsPerRev"/> 语义完全对称：只允许在 Disconnected / Connected / Stopped 改，
        /// 因为 φ 靠它把计数换成角度，运行中改会让 φ 在一拍内跳变。运行态写入直接拒绝并报诊断，值保持原样。
        /// <para>
        /// CLI 的 `--arm-cpr` 旗标已接线（写到这里，写完读回验证）；实值看
        /// <see cref="ResolvedArmCountsPerRev"/>。
        /// </para>
        /// </summary>
        public int ArmCountsPerRev
        {
            get { return _options.ArmCountsPerRev; }
            set
            {
                EngineState state = State;
                if (state != EngineState.Disconnected && state != EngineState.Connected && state != EngineState.Stopped)
                {
                    RaiseDiagnostic(string.Format(
                        "摆臂每转计数只在 Disconnected / Connected / Stopped 可改，当前 {0}，写入被拒，仍是 {1}。",
                        state, _options.ArmCountsPerRev));
                    return;
                }
                _options.ArmCountsPerRev = value;
                RaiseDiagnostic(string.Format(
                    "摆臂每转计数已设为 {0}（电机每转计数 × 减速比），下次使能 / 起摆时定标并自检。", value));
            }
        }

        /// <summary>
        /// 摆臂步进从站的 0 基下标（连接前的请求值，与 PendulumEngineOptions.ArmSlaveIndex 同源）。
        /// -1 = 自动识别。它只在 Connect 期生效：**只允许在 Disconnected 改**，
        /// 其余状态一律拒写并保持原值（读回旧值即可判定没生效）。
        /// </summary>
        public int ArmSlaveIndex
        {
            get { return _options.ArmSlaveIndex; }
            set
            {
                if (State != EngineState.Disconnected)
                {
                    RaiseDiagnostic(string.Format(
                        "摆臂从站下标只在 Disconnected 可改（它只在 Connect 期生效），当前 {0}，写入被拒，仍是 {1}。",
                        State, _options.ArmSlaveIndex));
                    return;
                }
                _options.ArmSlaveIndex = value;
                RaiseDiagnostic(string.Format(
                    "摆臂从站下标已设为 {0}（0 基，-1 = 自动识别），下次 Connect 生效。", value));
            }
        }

        /// <summary>
        /// 连接后实际选中的摆臂步进从站 0 基下标。未连接 = -1。
        /// 自动识别时它不等于请求值（请求值是 -1），前端展示用它，不要拿 ArmSlaveIndex 当结果。
        /// </summary>
        public int ResolvedArmSlaveIndex
        {
            get
            {
                BusSession bus = _bus;
                return bus == null ? -1 : bus.ArmSlaveIndex;
            }
        }

        /// <summary>
        /// 连接后实际用的网卡接口名（显式给名的匹配结果 / 空 NicName 的自动匹配结果）。未连接 = 空。
        /// 自动匹配时它不等于请求值（请求值是空串），前端展示与诊断用它，不要拿 NicName 当结果。
        /// </summary>
        public string ResolvedNicName
        {
            get
            {
                BusSession bus = _bus;
                return bus == null || bus.NicName == null ? string.Empty : bus.NicName;
            }
        }

        /// <summary>
        /// 摆臂标定的实值 CPR_ARM（电机每转计数 × 减速比）。已定标返回定标值：0x2102 实读 P ×
        /// 减速比的推导结果，或 0x2102 读不到时用的显式 <see cref="ArmCountsPerRev"/>
        /// （使能 / 起摆时 <c>RefreshArmCalibration</c> 定标落下来的那个数）；未定标返回 0。
        /// 前端展示与诊断用它，不要拿 <see cref="ArmCountsPerRev"/> 当结果 —— 那只是兜底请求值。
        /// </summary>
        public int ResolvedArmCountsPerRev
        {
            get
            {
                double resolved = _countsPerRevArm;
                if (resolved > 0) return (int)Math.Round(resolved, MidpointRounding.AwayFromZero);
                int explicitCpr = _options.ArmCountsPerRev;
                return explicitCpr > 0 ? explicitCpr : 0;
            }
        }

        /// <summary>
        /// 摆杆计数的置零基准（原始 32 位计数）。θ = (raw − 本值) × 2π / CPR_PENDULUM。
        /// 只读展示用：使能自动归零或「摆杆置零」后这里就是当时的原始计数。未连接时是上一次的值。
        /// </summary>
        public int PendulumZeroRawCount
        {
            get { return unchecked((int)Volatile.Read(ref _countZeroRaw)); }
        }

        /// <summary>
        /// 摆臂 0x6064 的零点计数（φ = 0 对应的原始位置）。使能自动归零或「设置 0 点」时写入。
        /// 点动中设 0 点会先挂起一拍（周期线程换基准并平移锚点），挂起期间这里读**请求值**，
        /// 保证界面「写入 → 立刻回读」不会看到还没换完的旧零点。
        /// </summary>
        public int ArmZeroCount
        {
            get
            {
                return Volatile.Read(ref _pendingArmZeroApply) != 0
                    ? Volatile.Read(ref _pendingArmZeroRaw)
                    : Volatile.Read(ref _phiZeroCount);
            }
        }

        /// <summary>
        /// 摆臂零点是否已被用户明确设定（<see cref="SetArmZeroCount"/> 或 <see cref="ZeroArm"/>）。
        /// true = 以后首次使能沿用这个零点；false = 首次使能仍把当前位置锁成 φ=0。
        /// </summary>
        public bool ArmZeroCountSet
        {
            get { return Volatile.Read(ref _armZeroOverrideSet) != 0; }
        }

        // ==================== 状态与遥测 ====================

        /// <summary>状态机当前状态。</summary>
        public EngineState State
        {
            get { return (EngineState)Volatile.Read(ref _state); }
        }

        /// <summary>急停锁存位。置位后 enable / swingup / balance 一律拒绝，只有 Stop() 能清。</summary>
        public bool IsEStopLatched
        {
            get { return Volatile.Read(ref _eStopLatch) != 0; }
        }

        /// <summary>
        /// 硬接线急停 / STO 是否已动作（S11 的只读证据，取自 0x6041 的 Quick Stop 位）。
        /// Stop() 清锁存前会先复检它，没松开就保持 Fault 并拒绝；前端也可以拿它做提示。
        /// </summary>
        public bool EmergencyStopInputActive
        {
            get
            {
                // 断开 / 只有上一会话快照时不能拿旧状态字报“急停仍在动作”。
                if (State == EngineState.Disconnected) return false;
                PendulumTelemetry t;
                if (!TryReadTelemetry(out t) || t.Sequence == 0) return false;
                if (_sessionStartTicks != 0 && t.TimestampTicks < _sessionStartTicks) return false;
                return CiA402.IsSafetyStopAsserted(t.StatusWord);
            }
        }

        /// <summary>
        /// 当前故障码（引擎实时字段，不依赖遥测快照）。CLI / UI 的回显用它做权威值；
        /// 遥测里的 FaultCode 只表示“那一帧记录到的故障码”。
        /// </summary>
        public FaultCode FaultCodeNow
        {
            get { return (FaultCode)Volatile.Read(ref _faultCode); }
        }

        /// <summary>seqlock 最新快照。读不到就返回 default（Sequence=0）；调用方按“无样本”处理。</summary>
        public PendulumTelemetry Telemetry
        {
            get
            {
                PendulumTelemetry t;
                return _slot.TryRead(out t) ? t : default(PendulumTelemetry);
            }
        }

        /// <summary>读一次 seqlock 快照。正在写时返回 false，本帧跳过，不阻塞。</summary>
        public bool TryReadTelemetry(out PendulumTelemetry snapshot)
        {
            return _slot.TryRead(out snapshot);
        }

        /// <summary>
        /// 读一拍外力扰动观测快照（dHat / 残差 / 置信度 / marginState / recenterFrozen / 估计带宽）。
        /// 周期线程写，UI / CLI 只读；不反向参与任何控制判据。
        /// </summary>
        public bool TryReadDisturbanceSnapshot(out BalanceDisturbanceSnapshot snapshot)
        {
            return _disturbanceObserver.TryReadSnapshot(out snapshot);
        }

        /// <summary>界面自检读取真实命令槽，不用第二套动作记录器证明按钮已接线。</summary>
        internal int PendingCommandForAudit
        {
            get { return Volatile.Read(ref _pendingCommand); }
        }

        /// <summary>
        /// --ui-audit 专用状态注入：仍写真实状态字段与遥测 seqlock，MainForm 的按钮矩阵和 Click
        /// 走生产同一路径。没有总线时不会创建控制线程，也不会产生任何 PDO / 电机输出。
        /// </summary>
        internal void InjectUiAuditState(EngineState state, int armRaw, bool liveTelemetry, bool estopLatched,
            int pendulumRaw = 0)
        {
            Volatile.Write(ref _state, (int)state);
            Volatile.Write(ref _eStopLatch, estopLatched ? 1 : 0);
            Volatile.Write(ref _faultCode, (int)(state == EngineState.Fault ? FaultCode.UserStop : FaultCode.None));
            Volatile.Write(ref _pendingCommand, CmdNone);
            Volatile.Write(ref _stopPhase, 0);
            Volatile.Write(ref _stopGraceCycles, 0);
            Volatile.Write(ref _jogDirection, 0);
            Volatile.Write(ref _returnHomeActive, 0);
            Volatile.Write(ref _returnHomeHoldCycles, 0);
            _enablePhase = PhaseIdle;
            _capture.ResetSession();

            PendulumTelemetry t = new PendulumTelemetry();
            t.Sequence = 1;
            t.TimestampTicks = liveTelemetry ? Stopwatch.GetTimestamp() : 0;
            t.EngineState = state;
            t.ArmPositionCount = armRaw;
            t.ArmTargetCount = armRaw;
            t.PendulumCount = pendulumRaw;
            t.ModesDisplay = CiA402.ModeCsp;
            t.ActualWkc = 4;
            t.ExpectedWkc = 4;
            t.ArmSlaveInOp = true;
            t.GcanSlaveInOp = true;
            t.OwnedSlavesResponding = true;
            t.EStopLatch = estopLatched;
            t.FaultCode = state == EngineState.Fault ? FaultCode.UserStop : FaultCode.None;
            _slot.Write(t);
        }

        /// <summary>从遥测环里批取历史样本，返回实取条数。画曲线用，落后太多就丢并计数。</summary>
        public int ReadTelemetryBatch(PendulumTelemetry[] dst, int max)
        {
            return _ring.TryReadBatch(dst, max);
        }

        /// <summary>
        /// 总线级状态快照（主站状态 / 真 OP / 链路与两口 / RTcnt / 从站在线数）。
        /// 只读控制层缓存的状态与诊断，<b>不发帧、不写 PDO</b>，UI 线程按 10–20 Hz 调用安全。
        /// 未连接返回 false，前端显示"—"而不是 0。
        /// </summary>
        public bool TryReadBusStatus(out BusStatusSnapshot status)
        {
            BusSession bus = _bus;
            if (bus == null)
            {
                status = default(BusStatusSnapshot);
                return false;
            }
            bool ok = bus.TryReadBusStatus(out status);
            // 周期序号由控制线程侧持有（内核 CycleCount），随同一份快照给出，UI 不必再读一遍诊断对象。
            ControlThread control = _control;
            status.PdoCycleCountKnown = control != null;
            status.PdoCycleCount = control == null ? 0L : control.CycleCount;
            return ok;
        }

        /// <summary>抖动 / WKC / 丢帧等统计。拿不到的项用 NativeXxxValid=false 表示"不可用"。</summary>
        public EngineDiagnostics Diagnostics
        {
            get
            {
                EngineDiagnostics d = new EngineDiagnostics();
                TimingSnapshot snap;
                if (_jitterStats.TryRead(out snap)) d.CycleJitterUs = snap;
                if (_computeStats.TryRead(out snap)) d.ThetaLoopUs = snap;
                ControlThread control = _control;
                d.CycleCount = control == null ? 0 : control.CycleCount;
                BusSession bus = _bus;
                long cycleNs = bus == null ? (long)CycleConstants.DefaultCycleNs : bus.CycleNs;
                d.BusCycleHz = (uint)(1000000000.0 / cycleNs);
                if (bus != null)
                {
                    double avg, max, round, load;
                    if (bus.TryReadBusTiming(out avg, out max, out round, out load))
                    {
                        d.BusAvgJitterUs = avg;
                        d.BusMaxJitterUs = max;
                        d.BusRoundtripUs = round;
                        d.BusLoadPercent = load;
                        d.NativeTimingValid = true;
                    }
                    ushort actual, expected;
                    if (bus.TryReadWkc(out actual, out expected))
                    {
                        d.ActualWkc = actual;
                        d.ExpectedWkc = expected;
                        d.NativeWkcValid = true;
                        int deficit = (int)expected - (int)actual;
                        d.WcDeficit = deficit > 0 ? deficit : 0;
                    }
                    d.PdoFrameLossTotal = bus.PdoFrameLossTotal;
                    d.PdoFrameLossConsecutive = bus.PdoFrameLossConsecutive;
                }
                else
                {
                    d.ActualWkc = _lastActualWkc;
                    d.ExpectedWkc = _lastExpectedWkc;
                }
                // 应用层 STALE 去抖的降级标记与累计数：不挂在 bus 分支里，掉总线时也如实可读。
                d.PdoStaleCycles = Volatile.Read(ref _staleCycles);
                d.PdoStaleTotal = Volatile.Read(ref _staleTotal);
                // 多从站拓扑：自有两台下标 + 旁观从站状态一览（只读只展示，不参与安全判据）。
                d.SlaveCount = bus == null ? 0 : bus.SlaveCount;
                d.ArmSlaveIndex = bus == null ? -1 : bus.ArmSlaveIndex;
                d.GcanSlaveIndex = bus == null ? -1 : bus.GcanSlaveIndex;
                d.CounterSlice = bus == null ? GcanSliceKind.Unknown : bus.CounterSlice;
                d.ObserverSlaveSummary = bus == null ? string.Empty : bus.BuildObserverSummary();
                d.DroppedTelemetryPackets = _ring.Dropped;
                return d;
            }
        }

        /// <summary>
        /// 取出状态迁移 / 故障 / 诊断并 raise。由 UI 的 200ms 定时器或 CLI 主循环调用。
        /// 事件不在周期线程里同步调前端，前端抛异常也不会回灌周期线程。
        /// </summary>
        public void PumpEvents()
        {
            if (_disposed) return;

            // S9 的另一半：worker 自己带故障退出时在这里判。
            ControlThread control = _control;
            if (control != null && control.FirstFault != null)
            {
                RecordCallbackException(control.FirstFault);
                LatchFault(FaultCode.CallbackException);
            }

            // S10 的另一半：周期线程整体停摆（心跳超 200 ms）。
            BusSession bus = _bus;
            if (bus != null && bus.IsOpen)
            {
                // 旁观从站掉站 / 掉出 OP / 掉 WKC 只报诊断事件，不触发 Fault（内部 1 Hz 节流）。
                bus.ScanObserverSlaves();

                EngineState state = State;
                bool shouldRun = state == EngineState.Connected || state == EngineState.Enabled ||
                                 state == EngineState.Swinging || state == EngineState.Balancing;
                long beat = Interlocked.Read(ref _telemetryBeatTicks);
                if (shouldRun && beat != 0 &&
                    Stopwatch.GetTimestamp() - beat > Stopwatch.Frequency / 5)
                {
                    LatchFault(FaultCode.TimingInvalid);
                }
            }

            EngineEvent item;
            while (_events.TryDequeue(out item))
            {
                if (item.Kind == EngineEventKind.StateChanged)
                {
                    RaiseStateChanged(item.FromState, item.ToState);
                }
                else if (item.Kind == EngineEventKind.Faulted)
                {
                    FaultInfo fault = item.Fault;
                    fault.Detail = FormatFaultDetail(fault);
                    // 现场日志必须有故障码：故障码枚举名 + 触发条件单独打一行（字符串只在慢路径拼）。
                    RaiseDiagnostic(FormatSafetyFaultLine(fault));
                    RaiseFaulted(fault);
                }
                else if (item.Kind == EngineEventKind.Diagnostic)
                {
                    RaiseDiagnostic(item.Text);
                }
                else if (item.Kind == EngineEventKind.CaptureCompleted)
                {
                    // 捕获记录：CAPTURE 行在这里拼（慢路径），周期线程只传结构体。
                    RaiseDiagnostic(FormatCaptureLine(item.Capture));
                    RaiseCaptureCompleted(item.Capture);
                }
            }

            // 周期回调异常要在事件里如实带上堆栈前 500 字；第一行文本一并取走，
            // 保证上一轮故障事件之后不会把旧消息带进下一轮（与 _callbackException 成对消费）。
            Exception pending = Interlocked.Exchange(ref _callbackException, null);
            string pendingText = Interlocked.Exchange(ref _callbackExceptionText, null);
            if (pending != null)
            {
                RaiseDiagnostic("周期回调异常：" + DiagText.Describe(pending));
            }
            else if (!string.IsNullOrEmpty(pendingText))
            {
                RaiseDiagnostic("周期回调异常：" + pendingText);
            }
        }

        // ==================== 零点 ====================

        /// <summary>
        /// 闭环是否在跑：已使能 / 起摆 / 平衡，或使能子流程还没走完。
        /// 这几个状态下改零点（换算基准）会让 θ、φ 与所有已锚定的位置指令同时跳变 ±当前角，
        /// 所以重标定入口一律先要求去使能。
        /// </summary>
        private bool IsClosedLoopRunning()
        {
            EngineState state = State;
            return state == EngineState.Enabled || state == EngineState.Swinging ||
                   state == EngineState.Balancing || _enablePhase != PhaseIdle;
        }

        /// <summary>
        /// 摆杆计数清零（**重标定**手段，使能成功时已自动做过一次）。
        /// 摆杆必须停在自然下垂位：θ 零点 = 垂位，平衡点 θ_eq = π 的物理约定就靠它。
        /// GC1602：发 Reset Counter CH1 一拍后清 0，再把当前计数锁成零点（有硬件复位命令）。
        /// GC1502：没有 RxPDO / 复位命令，只做软件置零 = 当前计数锁成零点（count_zero 软件基准），不发任何命令。
        /// </summary>
        public void ZeroPendulum()
        {
            ThrowIfDisposed();
            if (State == EngineState.Disconnected)
            {
                RaiseDiagnostic("总线没连，不能清摆杆计数。");
                return;
            }
            // 闭环运行中改换算基准，θ 会整体跳变、所有已锚定的目标同时错位，必须先去使能再置零。
            if (IsClosedLoopRunning())
            {
                RaiseDiagnostic("闭环运行中不能重标定零点：请先「停止」去使能，再重新置零。");
                return;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                RaiseDiagnostic("整圈捕获进行中不能重标定摆杆零点（θ 会跳变、累计角失效）：先取消捕获。");
                return;
            }
            Interlocked.Exchange(ref _pendingCommand, CmdZeroPendulum);
            RaiseDiagnostic("已请求重标定摆杆零点（摆杆须在自然下垂位，同时把摆臂当前位置锁成 φ=0）。");
        }

        /// <summary>
        /// 直接指定摆杆零点（自然下垂 / 最底部的**原始计数**，32 位位型，不做角度换算）。
        /// 用于界面手输 / 立即采集与启动时从参数文件恢复。与 <see cref="ZeroPendulum"/> 的区别：
        /// 本方法只改软件零点基准，**不发 GC1602 硬件复位**（复位会把计数器清零，与指定的绝对计数对不上）。
        /// <para>闭合环（Enabled / Swinging / Balancing / 使能流程）与整圈捕获中一律拒写：θ 会整体跳变。</para>
        /// </summary>
        public void SetPendulumZeroCount(int rawCount)
        {
            string reason;
            TrySetPendulumZeroCount(rawCount, out reason);
        }

        /// <summary>
        /// 同 <see cref="SetPendulumZeroCount"/>，但把拒绝原因带出来。**必须用返回值判成败**：
        /// 旧零点恰好等于请求值时，只看「读回值 == 请求值」会把一次被拒的写入误判成成功。
        /// </summary>
        public bool TrySetPendulumZeroCount(int rawCount, out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (IsClosedLoopRunning())
            {
                reason = "闭环运行中不能重标定摆杆零点：请先「停止」去使能再设置。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中不能改摆杆零点：先取消捕获。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            Volatile.Write(ref _pendulumZeroOverride, rawCount);
            Volatile.Write(ref _pendulumZeroOverrideSet, 1);
            Volatile.Write(ref _pendulumZeroCalibrated, 1);
            // 注意：这里不解除 _calibrationCleared。清空后单设一项不允许使能，
            // 必须等「零点 + 左限位 + 右限位」齐全后由 UI / CLI 显式 ConfirmCalibrationComplete。
            Volatile.Write(ref _countZeroRaw, unchecked((uint)rawCount));
            _thetaPrev = 0;
            _omegaFilt = 0;
            _zeroPulsePhase = 0;
            _zeroPendulumUserRequested = false;
            RaiseDiagnostic("摆杆零点已设为 raw=" + rawCount + "（原始计数；θ 从这一点起算 0）。");
            return true;
        }

        /// <summary>
        /// 摆杆零点是否已被用户明确设定（<see cref="SetPendulumZeroCount"/> 或 <see cref="ZeroPendulum"/>）。
        /// true = 以后首次使能沿用这个零点；false = 首次使能仍走「复位后锁当前计数」。
        /// </summary>
        public bool PendulumZeroCountSet
        {
            get { return Volatile.Read(ref _pendulumZeroOverrideSet) != 0; }
        }

        /// <summary>
        /// 摆臂重标定（使能成功时已自动做过一次）：把当前 0x6064 锁成 φ_zero_count，
        /// 此后 φ 读数从 0 起算。**不设 home**——起始位置恒等于坐标 0（<see cref="ArmHomeRad"/>），
        /// 回中 / 收尾目标也恒为 0，不存在"当前角被锁成 home"这回事。
        /// </summary>
        public void ZeroArm()
        {
            ThrowIfDisposed();
            if (State == EngineState.Disconnected)
            {
                RaiseDiagnostic("总线没连，不能清摆臂零点。");
                return;
            }
            // 同摆杆零点：闭环运行中改 φ 基准会让位置指令整体跳变。
            if (IsClosedLoopRunning())
            {
                RaiseDiagnostic("闭环运行中不能重标定零点：请先「停止」去使能，再重新置零。");
                return;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                RaiseDiagnostic("整圈捕获进行中不能重标定摆臂零点（φ 会跳变，捕获期间摆臂必须保持不动）：先取消捕获。");
                return;
            }
            Interlocked.Exchange(ref _pendingCommand, CmdZeroArm);
            RaiseDiagnostic("已请求重标定摆臂零点（当前位置 = 坐标 0 = 起始位置）。");
        }

        /// <summary>
        /// 直接指定摆臂零点（原始 0x6064 计数，不做角度换算）。用于界面「设置 0 点」手输 / 立即捕获，
        /// 以及启动时从参数文件恢复。与 <see cref="ZeroArm"/>（下一拍取当前位置）互补：本方法立刻改
        /// <see cref="ArmZeroCount"/> 并记下「用户已设定」，此后首次使能也沿用它，不再被当前位置覆盖。
        /// <para>
        /// <b>点动中（Enabled）也允许设置</b>：这是现场「点动到期望位置 → 设成 0 点」的标准用法。
        /// 零点请求会排进周期线程，在拍首换基准，并把目标 / 参考 / 微分基准同步平移
        /// 相同角度，所以 0x607A 的物理目标计数连续、φ̇ 不会被打飞。
        /// </para>
        /// <para>
        /// 仍拒写的只有：使能子流程进行中、起摆 / 平衡（φ 参与控制律与能量估计，改坐标会换掉控制相位）、
        /// 整圈捕获等待 / 记录中（摆臂必须保持不动）。拒写时保持旧值，读回 <see cref="ArmZeroCount"/> 即可判定。
        /// </para>
        /// </summary>
        public void SetArmZeroCount(int rawCount)
        {
            string reason;
            TrySetArmZeroCount(rawCount, out reason);
        }

        /// <summary>
        /// 同 <see cref="SetArmZeroCount"/>，但把拒绝原因带出来。**必须用返回值判成败**：
        /// 旧零点恰好等于请求值时，只看「读回值 == 请求值」会把一次被拒的写入误判成成功。
        /// </summary>
        public bool TrySetArmZeroCount(int rawCount, out string reason)
        {
            ThrowIfDisposed();
            reason = null;
            if (_enablePhase != PhaseIdle)
            {
                reason = "使能流程进行中不能改摆臂零点：等使能完成或先「停止」。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            EngineState state = State;
            if (state == EngineState.Swinging || state == EngineState.Balancing)
            {
                reason = "起摆 / 平衡中不能改摆臂零点（φ 参与控制律与能量估计）：先「停止」再设置 0 点。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                reason = "整圈捕获进行中不能改摆臂零点（φ 会跳变）：先取消捕获。写入被拒。";
                RaiseDiagnostic(reason);
                return false;
            }
            Volatile.Write(ref _armZeroOverride, rawCount);
            Volatile.Write(ref _armZeroOverrideSet, 1);
            Volatile.Write(ref _armZeroCalibrated, 1);
            // 注意：这里不解除 _calibrationCleared。清空后单设零点不允许使能，
            // 必须等三点齐全后由 UI / CLI 显式 ConfirmCalibrationComplete。
            // 周期线程在跑（总线上有控制拍）就排队，由拍首统一换基准并平移锚点；
            // 未连接 / 没有控制线程时没有「下一拍」，直接落定。
            bool cycleRunning = _control != null;
            if (cycleRunning)
            {
                Volatile.Write(ref _pendingArmZeroRaw, rawCount);
                Interlocked.Exchange(ref _pendingArmZeroApply, 1);
                RaiseDiagnostic("摆臂零点已设为 raw=" + rawCount +
                    "（原始 0x6064 计数；下一控制拍换基准，目标与参考角同步平移，位置指令不跳变）。");
                return true;
            }
            ApplyArmZeroNow(rawCount, false);
            RaiseDiagnostic("摆臂零点已设为 raw=" + rawCount + "（原始 0x6064 计数；后续首次使能沿用此零点）。");
            return true;
        }

        /// <summary>
        /// 周期线程拍首消费挂起的摆臂零点请求（在算 φ 之前调用）。
        /// 只由周期线程调用；慢线程的请求通过 <see cref="_pendingArmZeroApply"/> 交接。
        /// </summary>
        private void ApplyPendingArmZero()
        {
            if (Interlocked.Exchange(ref _pendingArmZeroApply, 0) == 0)
                return;
            int raw = Volatile.Read(ref _pendingArmZeroRaw);
            int oldZero = _phiZeroCount;
            ApplyArmZeroNow(raw, true);
            // 日志只报 counts（零点差是绝对计数差，不折算角度）。
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "[标定] 摆臂零点已切换为 raw={0}（原 {1}，差 {2} counts）：目标 / 参考 / 微分基准同步平移，物理位置指令保持连续。",
                raw, oldZero, (long)raw - oldZero));
        }

        /// <summary>
        /// 真正换零点：改 φ 换算基准，并把所有以 φ 表达的锚点平移同样的角度
        /// （零点从 old 变到 new 时，同一物理计数的新 φ = 旧 φ + (old−new)·2π/CPR）。
        /// 目标计数 = 零点 + φ_target·CPR/2π，因此零点与 φ_target 同步平移后计数不变；
        /// 微分基准也同步平移，φ̇ 不会因为坐标系跳变出现尖峰。
        /// </summary>
        private void ApplyArmZeroNow(int rawCount, bool fromCycle)
        {
            int oldZero = _phiZeroCount;
            double deltaRad = 0.0;
            bool shifted = false;
            if (_countsPerRevArm > 0)
            {
                deltaRad = (oldZero - rawCount) * ControlMath.TwoPi / _countsPerRevArm;
                shifted = ControlMath.IsFinite(deltaRad);
            }
            if (shifted)
            {
                _phiTargetPrev += deltaRad;
                _phiRef += deltaRad;
                _balanceCmdRad += deltaRad;
                _balanceRecenterCenterRad += deltaRad;
                _balanceRecenterReferenceRad += deltaRad;
                _balanceRecenterStartRad += deltaRad;
                // 微分基准只在控制拍内有意义；慢线程直接落定时它属于下一拍的初始化，平移同样安全。
                _phiPrev += deltaRad;
            }
            else
            {
                // 未标定 / 换算非法：没有可平移的角度口径。此时控制环本来就不输出有效目标，重置锚点即可。
                _phiPrev = 0;
                _phiDotFilt = 0;
                _phiTargetPrimed = false;
                _lastTargetValid = false;
                _phiRef = ArmHomeRad;
            }
            Volatile.Write(ref _phiZeroCount, rawCount);
            if (!fromCycle)
            {
                // 没有控制拍可平移时，下一拍的目标要从当前位置重新锚定，不能沿用旧目标。
                _phiTargetPrimed = false;
                _lastTargetValid = false;
            }
        }

        /// <summary>
        /// 直接指定摆臂每转计数 CPR_ARM（现场实测值，仅允许停机态写）。
        /// 立刻用于 counts→φ 与界面的度换算；下次使能 / 起摆仍走 <see cref="RefreshArmCalibration"/>，
        /// 以驱动器 0x2102 实读值复核（实读优先，不一致会在诊断里写明差多少）。
        /// </summary>
        public void SetArmCountsPerRev(int countsPerRev)
        {
            ThrowIfDisposed();
            if (IsClosedLoopRunning())
            {
                RaiseDiagnostic("闭环运行中不能改摆臂每转计数：请先「停止」去使能。写入被拒。");
                return;
            }
            if (countsPerRev <= 0)
            {
                RaiseDiagnostic("摆臂每转计数必须是 >0 的整数，收到 " + countsPerRev + "。写入被拒。");
                return;
            }
            _options.ArmCountsPerRev = countsPerRev;
            // 立刻生效：φ = (0x6064 − 零点) × 2π / CPR_ARM 与控制层速度换算都用它。
            // 使能 / 起摆时 RefreshArmCalibration 会按 0x2102 实读复核并覆盖（硬件事实优先）。
            _countsPerRevArm = countsPerRev;
            RaiseDiagnostic("摆臂每转计数已设为 " + countsPerRev + "（现场实测一圈；下次使能用 0x2102 复核）。");
        }

        /// <summary>
        /// 摆杆计数归零推进一拍，返回本拍要写进 GCAN 输出区的复位位（只有 GC1602 非零）。
        /// <para>
        /// <b>GC1602</b>（AB 正交，有复位命令）：先把 0x7000:1 Reset Counter CH1 置 1 打一拍，
        /// 下一拍把复位后的计数锁成零点（<c>_countZeroRaw</c>）。
        /// </para>
        /// <para>
        /// <b>GC1502</b>（脉冲计数，无 RxPDO / 无复位命令）：当场把当前计数锁成零点（软件置零），不发任何命令。
        /// </para>
        /// 使能完成的自动归零与手动 <see cref="ZeroPendulum()"/> 走同一条路径（<c>_zeroPulsePhase = 2</c> 排队）。
        /// </summary>
        internal ushort StepPendulumZero(GcanSliceKind slice, uint pendulumRaw, bool hardwareResetReachable)
        {
            if (_zeroPulsePhase <= 0) return 0;
            if (slice == GcanSliceKind.Gc1602Counter && hardwareResetReachable && _zeroPulsePhase == 2)
            {
                _zeroPulsePhase = 1;
                return 0x0001;
            }
            _zeroPulsePhase = 0;
            _countZeroRaw = pendulumRaw;
            _thetaPrev = 0;
            _omegaFilt = 0;
            Volatile.Write(ref _pendulumZeroCalibrated, 1);
            // 用户显式「设置摆杆零点」才把它记成可落盘的标定值；使能自动归零不算用户标定。
            if (_zeroPendulumUserRequested)
            {
                _zeroPendulumUserRequested = false;
                Volatile.Write(ref _pendulumZeroOverride, unchecked((int)pendulumRaw));
                Volatile.Write(ref _pendulumZeroOverrideSet, 1);
                Volatile.Write(ref _calibrationDirty, 1);
                // 用户显式重标定了摆杆垂位 = 保存零点不一致的问题已经被人工解决：
                // 解掉「位置标定待确认」，否则现场会遇到「被拒使能 → zero pendulum → 仍然被拒」的死锁。
                Volatile.Write(ref _calibrationReviewRequired, 0);
                Volatile.Write(ref _calibrationReviewChecked, 1);
            }
            // 手动置零带下来的摆臂零点：与摆杆零点在同一拍落定。
            // 分两拍取会让 φ 基准带上这两拍之间的摆臂位移，两点也就不再互为同一姿态的基准。
            if (_zeroArmCountPending)
            {
                _zeroArmCountPending = false;
                _phiZeroCount = _zeroArmCount;
                Volatile.Write(ref _armZeroCalibrated, 1);
                // 与 CmdZeroArm 同一口径：这两路归零同时把摆臂零点记成「用户设定」，保存参数时可落盘。
                Volatile.Write(ref _armZeroOverride, _zeroArmCount);
                Volatile.Write(ref _armZeroOverrideSet, 1);
                Volatile.Write(ref _calibrationDirty, 1);
                _phiPrev = 0;
                _phiDotFilt = 0;
                _phiTargetPrimed = false;
                _lastTargetValid = false;
                _phiRef = ArmHomeRad;
            }
            // 本会话零点已建立。注意：摆臂零点在**每次**使能成功时都会重新锁定（见 RunEnableSequence），
            // 这里的标志只表示「本会话已经有可用的摆杆基准」，不再意味着「后续使能沿用旧摆臂零点」。
            _zeroEstablished = true;
            EnqueueDiagnostic(slice == GcanSliceKind.Gc1602Counter
                ? (hardwareResetReachable
                    ? "摆杆计数已清零（GC1602 复位命令），θ=0 对应自然下垂位。"
                    : "摆杆计数已软件置零（GC1602 输出方向未配置，硬件复位不可达；当前计数锁成零点），θ=0 对应自然下垂位。")
                : "摆杆计数已软件置零（GC1502 无复位命令，当前计数锁成零点），θ=0 对应自然下垂位。");
            return 0;
        }

        // ==================== 周期路径（ControlThread 调用，热路径）====================

        internal void ExecuteCycle()
        {
            try
            {
                ExecuteCycleCore();
            }
            catch (PdoInputStaleException ex)
            {
                // IsLost 还来不及闩上的窗口里，过程映像也可能被判 stale（掉站前最后一帧）。
                // 按从站状态故障收口：根因如实记 EtherCatState，不让陈旧帧进控制律。
                RecordCallbackException(ex);
                try
                {
                    EmergencyStopInternal(FaultCode.EtherCatState);
                }
                catch (Exception inner)
                {
                    RecordCallbackException(inner);
                    Volatile.Write(ref _state, (int)EngineState.Fault);
                    Volatile.Write(ref _faultCode, (int)FaultCode.EtherCatState);
                }
            }
            catch (Exception ex)
            {
                // S9：周期回调抛异常。锁存异常对象（不在此处拼字符串），立刻进安全态。
                RecordCallbackException(ex);
                try
                {
                    EmergencyStopInternal(FaultCode.CallbackException);
                }
                catch (Exception inner)
                {
                    // 周期线程的最后兜底：再抛就逃进内核回调栈了，只能锁一个状态字出去。
                    RecordCallbackException(inner);
                    Volatile.Write(ref _state, (int)EngineState.Fault);
                    Volatile.Write(ref _faultCode, (int)FaultCode.CallbackException);
                }
            }
        }

        /// <summary>
        /// 锁存周期回调异常：异常对象留给 S9 判据（CallbackFault），第一行文本（类型 + Message）留给
        /// 故障诊断通道与 [SAFETY] FAULT 行。两者都只在第一条上写（CompareExchange），不覆盖真正的首因。
        /// </summary>
        private void RecordCallbackException(Exception ex)
        {
            if (ex == null) return;
            Interlocked.CompareExchange(ref _callbackException, ex, null);
            Interlocked.CompareExchange(ref _callbackExceptionText, FormatCallbackExceptionText(ex), null);
        }

        /// <summary>
        /// 周期回调异常的第一行：类型名 + Message，最长 300 字。
        /// 完整堆栈由 PumpEvents 的 <see cref="DiagText.Describe"/> 另记；
        /// 这里只保证故障行 / [SAFETY] 行能一眼分清是「PDO 输入映像不可用」还是别的抛出点。
        /// </summary>
        private static string FormatCallbackExceptionText(Exception ex)
        {
            if (ex == null) return null;
            string text = ex.GetType().Name + ": " + (ex.Message ?? string.Empty);
            return text.Length > 300 ? text.Substring(0, 300) : text;
        }

        /// <summary>
        /// 内核已完成的 PDO 周期序号（每个总线周期 +1）。读不到返回 0 = 本拍无法判重（照常推进），
        /// 绝不因为读序号失败停控制律；失败只在第一次记一条诊断。
        /// </summary>
        private ulong ReadBusCycleSequence()
        {
            ControlThread control = _control;
            if (control == null) return 0;

            try
            {
                long count = control.CycleCount;
                return count > 0 ? (ulong)count : 0UL;
            }
            catch (Exception ex)
            {
                // 周期路径不能把异常抛出去：ExecuteCycle 会把它锁成 CallbackException 急停。
                if (Interlocked.Exchange(ref _cycleSequenceReadFailed, 1) == 0)
                    EnqueueDiagnostic("读内核周期序号失败，本会话按「不判重」继续跑：" + DiagText.Describe(ex));
                return 0;
            }
        }

        private void ExecuteCycleCore()
        {
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen) return;

            // 用户 stop / fault-reset 请求的 S6 计数清零：**只由周期线程自己消费**。
            // 不在 Stop() 里直接写 _staleInputCycles —— 那是跨线程 RMW，可能与下面的 ++ 交错把
            // 旧值写回去。这里用一次性请求标志，保证下一次进入输入解码前计数一定是 0。
            if (Interlocked.Exchange(ref _staleResetRequested, 0) != 0)
                _staleInputCycles = 0;

            // 一个总线周期只推进一次控制律（判据 = 内核周期序号，见 _lastStepCycle 处的说明）。
            // 序号没前进 = 还是同一份过程映像：本拍不读输入、不推进积分器、不写 0x607A / 0x6040、
            // 不出遥测。dt 取的是 DENI 周期，只有"一次调用 = 一个总线周期"这个前提成立，时基才对。
            ulong busCycle = ReadBusCycleSequence();
            if (busCycle != 0)
            {
                if (_stepCycleValid && busCycle == _lastStepCycle)
                {
                    Interlocked.Increment(ref _duplicateCycleSkips);
                    if (Interlocked.Exchange(ref _duplicateCycleReported, 1) == 0)
                    {
                        EnqueueDiagnostic(
                            "内核同一周期重复通知（同一份 PdIn 被置位两次）：本拍按重复丢弃，不推进控制律。" +
                            "控制节拍只跟内核周期序号走（每周期 +1），不再按通知次数走。");
                    }
                    return;
                }
                _lastStepCycle = busCycle;
                _stepCycleValid = true;
            }

            // 掉线守卫：先问 SDK 在线真值，再碰过程映像。任一台自有从站掉线时
            // InputsMapping / InputsSliceMapping 会抛 PdoInputStaleException（交出掉站前最后一帧），
            // 这里直接锁故障并停写目标，绝不用陈旧帧跑控制律。
            if (bus.ArmSlaveLost || bus.GcanSlaveLost)
            {
                EmergencyStopInternal(FaultCode.EtherCatState);
                return;
            }

            long t0 = Stopwatch.GetTimestamp();

            // ---- 1. 读输入（单拍 STALE 去抖）----
            // 内核某一拍没把 PD 合并进过程映像时，GetIO 返回 STALE，InputsMapping 抛
            // InvalidOperationException("PDO 输入映像不可用")。那只是**这一拍**没有新过程数据：
            // 驱动侧位置环照跑、摆杆照动，旧版却一拍就锁 CallbackException 急停。
            // 现在：单拍沿用上一拍已解码的数据继续控制，连续超过阈值才按 PdoFrameLoss 如实收口。
            StepperTxPdo stepperIn;
            uint pendulumRaw;
            uint pendulumFreqHz;
            uint pendulumRawCh2;
            uint pendulumFreq2Hz;
            Exception staleReadError;
            PdoReadOutcome readOutcome = TryReadProcessInputs(
                out stepperIn, out pendulumRaw, out pendulumFreqHz,
                out pendulumRawCh2, out pendulumFreq2Hz, out staleReadError);
            if (readOutcome == PdoReadOutcome.NotMapped)
            {
                // 从未映射成功：没有上一拍过程数据可沿用，绝不拿默认 0 当测量值，立刻如实收口。
                _staleCycles = 0;
                _staleInputCycles = 0;
                EnqueueDiagnostic("PDO 输入映像从未映射成功（本会话没有可沿用的上一拍过程数据），立即切安全态："
                    + DiagText.Describe(staleReadError));
                EmergencyStopInternal(FaultCode.EtherCatState);
                return;
            }

            bool staleReadThisCycle = readOutcome == PdoReadOutcome.Stale;
            bool reuseHeldState = staleReadThisCycle;
            if (reuseHeldState)
            {
                _staleInputCycles++;
                bool trip = RegisterStaleCycle();
                ReuseLastProcessData(
                    out stepperIn, out pendulumRaw, out pendulumFreqHz,
                    out pendulumRawCh2, out pendulumFreq2Hz);
                ReportProcessDataStale("输入", staleReadError);
                if (trip)
                {
                    TripProcessDataStale("输入");
                    return;
                }
            }
            else
            {
                // 好拍必须清零 S6 的逐拍计数，且必须发生在下面的安全联锁评估之前：
                // fault-reset 后第一拍只要输入有效，S6 就看到 0，不会拿上一次故障的旧计数再跳一次。
                _staleInputCycles = 0;
                CacheProcessData(stepperIn, pendulumRaw, pendulumFreqHz, pendulumRawCh2, pendulumFreq2Hz);
            }

            // 摆杆计数一律按 32 位位型取：GC1502 是 UDINT（会绕回 0xFFFFFFFF→0），GC1602 是 DINT。
            int pendulumCount = unchecked((int)pendulumRaw);
            UpdatePendulumLiveness(t0, pendulumRaw);
            int armPos = stepperIn.ActualPosition;
            // 点动中「设置 0 点」的挂起请求必须在算 φ 之前消费：换基准 + 平移锚点，后面
            // 的 φ / φ̇ / 目标计数全部基于新坐标系，物理位置指令不发生跳变。
            ApplyPendingArmZero();
            // 连接后首帧核对保存零点 ↔ 当前 raw；不一致就阻断自动动作（绝不自动加偏移）。
            EngineState reviewState = State;
            if (_enablePhase == PhaseIdle &&
                (reviewState == EngineState.Connected || reviewState == EngineState.Disconnected))
            {
                UpdateCalibrationReview(armPos, pendulumRaw);
            }
            ushort statusWord = stepperIn.StatusWord;
            ushort driveError = stepperIn.ErrorCode;
            sbyte modeDisplay = stepperIn.ModesDisplay;
            uint armDigitalInputs = stepperIn.DigitalInputs;

            // ---- 2. 解码 θ / ω / φ / φ̇ ----
            // 单位换算只在这一处做（rad 单点）：φ = 2π × Pulse / CPR_ARM，CPR_ARM = 电机每转 P × 减速比。
            bool pendulumCalibrated = _countsPerRevPendulum > 0;
            // 未标定时**不猜**每转计数：旧版拿 1.0 兜底，把 arm_pos_count = −8334 算成
            // φ = −52364.07 rad（真值 −5.235 rad）—— 遥测里看着像「机器疯了」，实际只是没标定。
            // 现在未标定就把 φ / φ̇ 如实给 NaN（CSV 写 nan），并在诊断里明确标「摆臂未标定」。
            // 标定值一旦变（使能时实读 0x2102 定标），本拍重置 φ 的微分基准，避免 φ 跳变把 φ̇ 打飞。
            bool armCalibrated = _countsPerRevArm > 0;
            bool armCprChanged = _cycleArmCprSeen != _countsPerRevArm;
            _cycleArmCprSeen = _countsPerRevArm;
            // 频率计交叉校验：GC1502 的 FreqCh1 [Hz] 换算成 rad/s。只进遥测/诊断，绝不进控制律。
            bool freqCrossCheckValid = bus.CounterSlice == GcanSliceKind.Gc1502Counter &&
                                       _countsPerRevPendulum > 0;

            double theta;
            double omega;
            double phi;
            double phiDot;
            double omegaFromFreq;
            if (reuseHeldState)
            {
                // 本拍没有新过程数据：沿用上一拍**已解码**的状态继续跑控制律。
                // 不拿同一份计数再差分一次（那样 ω 会被算成 0、滤波器跟着衰减），
                // 也不动 _thetaPrev / _omegaFilt 这些微分基准 —— 恢复拍由 _holdFeedbackAcrossGap 接住。
                theta = _lastThetaRad;
                omega = _lastOmegaRadPerSec;
                phi = _lastPhiRad;
                phiDot = _lastPhiDotRadPerSec;
                omegaFromFreq = _lastOmegaFromFreq;
                _holdFeedbackAcrossGap = true;
            }
            else
            {
                // 未标定时**不给假值**：旧版回落 1.0，等于把编码器计数直接当弧度报出去
                // （遥测里看着像「摆杆在疯转」，实际只是没标定）。与摆臂未标定同一口径：
                // θ / ω 如实写 NaN，并打一条 INFO。
                double cprPendulum = pendulumCalibrated ? _countsPerRevPendulum : double.NaN;
                // 32 位回绕差分：无符号减法再转有符号，越过 0xFFFFFFFF→0 的过零也拿得到正确的小差值。
                int pendulumDelta = ControlMath.CountDelta(pendulumRaw, _countZeroRaw);
                theta = pendulumCalibrated ? pendulumDelta * ControlMath.TwoPi / cprPendulum : double.NaN;
                if (!pendulumCalibrated && Interlocked.Exchange(ref _pendulumUncalibratedReported, 1) == 0)
                {
                    EnqueueDiagnostic(
                        "摆杆未标定（CPR_PENDULUM = 0）：遥测 / CSV 里的 theta_rad / omega_rad_s 本段如实写 nan，" +
                        "不用假每转计数凑一个看起来正常的错值。用 --pendulum-cpr <线数×4×传动比> 给值后再起摆。");
                }
                omegaFromFreq = pendulumCalibrated ? pendulumFreqHz * ControlMath.TwoPi / cprPendulum : double.NaN;
                phi = armCalibrated
                    ? (armPos - _phiZeroCount) * ControlMath.TwoPi / _countsPerRevArm
                    : double.NaN;
                if (!armCalibrated && Interlocked.Exchange(ref _armUncalibratedReported, 1) == 0)
                {
                    EnqueueDiagnostic(
                        "摆臂未标定（CPR_ARM = 0）：遥测 / CSV 里的 phi_rad / phi_dot_rad_s 本段如实写 nan，" +
                        "不用假每转计数凑一个看起来正常的错值。使能 / 起摆时会实读 0x2102 定标。");
                }
                omega = 0;
                phiDot = armCalibrated ? 0.0 : double.NaN;
                if (_feedbackPrimed)
                {
                    if (_holdFeedbackAcrossGap)
                    {
                        // 上一拍丢过帧：不跨丢帧做差分（两次采样间隔不是 dt，差分会把 ω 打成两倍）。
                        // 沿用上一拍的 ω / φ̇，本拍只把微分基准对齐到新值，下一拍起恢复真微分。
                        _holdFeedbackAcrossGap = false;
                        omega = _omegaFilt;
                        phiDot = armCalibrated ? _phiDotFilt : double.NaN;
                    }
                    else
                    {
                        // 微分由编码器差分 + 同一个一阶低通，不引入第二个滤波器。dt 固定，不按墙钟。
                        double omegaRaw = (theta - _thetaPrev) / _dtSec;
                        if (ControlMath.IsFinite(omegaRaw))
                            omega = _omegaFilt * _feedbackAlpha + omegaRaw * (1 - _feedbackAlpha);
                        else
                            omega = _omegaFilt;

                        if (armCalibrated && !armCprChanged)
                        {
                            double phiDotRaw = (phi - _phiPrev) / _dtSec;
                            if (ControlMath.IsFinite(phiDotRaw))
                                phiDot = _phiDotFilt * _feedbackAlpha + phiDotRaw * (1 - _feedbackAlpha);
                            else
                                phiDot = _phiDotFilt;
                        }
                    }
                }
                else
                {
                    _feedbackPrimed = true;
                }
                _thetaPrev = theta;
                // 未标定 / 刚换标定：φ 的微分基准直接对齐当前拍，下一拍起才是真微分。
                _phiPrev = armCalibrated ? phi : 0.0;
                _omegaFilt = omega;
                // 未标定：对外（遥测 / 控制律 / 安全判据）如实给 NaN，与 φ / φ̇ 未标定同一口径；
                // 但滤波器内部仍保留有限值，标定恢复后 ω 立刻可用，不会被 NaN 永久污染。
                if (!pendulumCalibrated) omega = double.NaN;
                _phiDotFilt = ControlMath.IsFinite(phiDot) ? phiDot : 0.0;
            }

            // 缓存本拍已解码的过程状态：下一拍若瞬时 STALE 直接沿用这一份。
            // stale 分支写回的是同一组值（幂等），所以这里无条件更新。
            _lastThetaRad = theta;
            _lastOmegaRadPerSec = omega;
            _lastPhiRad = phi;
            _lastPhiDotRadPerSec = phiDot;
            _lastOmegaFromFreq = omegaFromFreq;

            double thetaFromUpright = ControlMath.WrapPi(theta - Math.PI);
            double omegaWindowMs = _swingActive.OmegaWindowMs > 0 && ControlMath.IsFinite(_swingActive.OmegaWindowMs)
                ? _swingActive.OmegaWindowMs
                : 40.0;
            if (BalanceOmegaWindowOverrideMs > 0 && ControlMath.IsFinite(BalanceOmegaWindowOverrideMs))
                omegaWindowMs = BalanceOmegaWindowOverrideMs;
            _balanceOmegaEstimateRadPerSec = _balanceOmegaEstimator.Update(theta, omega, _dtSec, omegaWindowMs);
            _balanceOmegaResidualRad = _balanceOmegaEstimator.ResidualRmsRad;
            _balanceArmAccelEstimator.Update(phi, phiDot, _dtSec, omegaWindowMs);

            // ---- 3. 拍首：参数顶换 + 命令消费 ----
            SwapIfDirty();
            if (_stopGraceCycles > 0) _stopGraceCycles--;
            bool estopRequested = Volatile.Read(ref _eStopRequested) != 0;
            int command = Interlocked.Exchange(ref _pendingCommand, CmdNone);
            if (estopRequested || command == CmdEStop)
            {
                EmergencyStopInternal(FaultCode.None);
                command = CmdNone;
            }
            else if (command != CmdNone)
            {
                ApplyCommand(command, theta, thetaFromUpright, omega, phi, armPos, pendulumCount);
            }

            // ---- 4. WKC 如实读取（expected 固定，只比较不下调）----
            ushort actualWkc, expectedWkc;
            if (bus.TryReadWkc(out actualWkc, out expectedWkc))
            {
                _lastActualWkc = actualWkc;
                _lastExpectedWkc = expectedWkc;
            }

            // ---- 5. 安全联锁 S1..S13 ----
            EngineState state = State;
            SafetyInputs safety = new SafetyInputs();
            safety.State = state;
            safety.ThetaFromUprightRad = thetaFromUpright;
            safety.OmegaRadPerSec = omega;
            safety.PhiRad = phi;
            // S2 / 遥测统一用最后一次真正写出的 0x607A 目标；不再用 counts 钳位前的 _phiTargetPrev。
            double lastWrittenTargetRad = phi;
            if (_lastTargetValid && _countsPerRevArm > 0)
            {
                double targetRad = (double)((long)_lastTargetCount - _phiZeroCount) *
                                   ControlMath.TwoPi / _countsPerRevArm;
                if (ControlMath.IsFinite(targetRad)) lastWrittenTargetRad = targetRad;
            }
            safety.PhiTargetRad = lastWrittenTargetRad;
            _safetyTargetRad = lastWrittenTargetRad;
            _safetyFollowingErrorCount = _lastTargetValid ? (long)_lastTargetCount - armPos : 0;
            safety.PhiHomeRad = ArmHomeRad;
            safety.StatusWord = statusWord;
            safety.ErrorCode = driveError;
            // ---- 4b. 自有两台（GCAN + 摆臂）的健康度。旁观从站不进安全判据，只进遥测与诊断。----
            bool armInOp, gcanInOp, ownedResponding;
            bus.ReadOwnedSlaveHealth(out armInOp, out gcanInOp, out ownedResponding);

            safety.OwnedSlavesResponding = ownedResponding;
            safety.OwnedSlavesInOp = armInOp && gcanInOp;
            safety.PdoBadCyclesConsecutive = (uint)_staleInputCycles;
            safety.TelemetryWriteStalled = false;
            // S11 证据一：0x6041 状态字（掉 Quick Stop Active / 自称使能却把 bit5 拉低）。
            safety.EStopInputActive = CiA402.IsSafetyStopAsserted(statusWord);
            // S11 证据二：0x60FD bit23 急停输入（17B TxPDO 才拿得到）。状态字判不了"驱动已去使能时急停仍闭合"，这条补那个盲区。
            safety.ArmEStopInputActive = CiA402.IsDigitalEStopAsserted(armDigitalInputs);
            safety.ModesDisplay = modeDisplay;
            safety.CallbackFault = _callbackException != null;
            safety.EnableSequenceActive = _enablePhase != PhaseIdle;
            safety.ControlledStopActive = _stopPhase != 0;
            safety.OwnQuickStopRecent = _stopPhase != 0 || _stopGraceCycles > 0;
            safety.SwingUpElapsedSec = _swing.PhaseSec;
            safety.SwingUpTimeoutSec = _swingActive.TimeoutMs / 1000.0;

            FaultCode interlockCode;
            if (_interlock.Evaluate(safety, _limitsActive, out interlockCode))
            {
                EmergencyStopInternal(interlockCode);
                state = State;
            }

            // ---- 5b. 整圈捕获推进（等待抬升 → 释放 → 累计一整圈）----
            // 放在控制律分派之前：释放那拍起就按整圈判据记录，不浪费一拍。
            // 摆臂全程钉在进入捕获时的位置，控制律在 Enabled 分支里保持，不因捕获切换状态机。
            // 软限位命中（实测摆臂越界）先停安全态，下面的分派看到 Fault 会写 0x0000。
            if (_capture.IsWaiting || _capture.IsCapturing)
            {
                if (state != EngineState.Enabled || _stopPhase != 0)
                {
                    // 故障 / 停止 / 断开：记录中的尝试先落盘（失效原因如实写），等待态直接作废。
                    FinishCaptureAttempt(t0);
                    _capture.ResetSession();
                }
                else
                {
                    CaptureEvent captureEvent = _capture.Step(
                        t0, theta, omega, phi, phiDot, pendulumCount, armPos,
                        CaptureSoftLimitMarginCount(), _limitsActive);
                    if (captureEvent == CaptureEvent.SoftLimitTrip)
                    {
                        // 先写真实失效原因再收记录：否则 EndAttempt 只会落“未能完成一整圈”，
                        // 把“实测越界”这个根因从记录里抹掉。
                        _capture.InvalidateAttempt(t0, string.Format(CultureInfo.InvariantCulture,
                            "摆臂实测越界：0x6064={0} counts，软限位 [{1},{2}]，余量 {3} counts；整圈数据作废。",
                            armPos, _limitsActive.ArmMinCount, _limitsActive.ArmMaxCount,
                            CaptureSoftLimitMarginCount()));
                        EmergencyStopInternal(FaultCode.SoftLimit);
                        state = State;
                        FinishCaptureAttempt(t0);
                        _capture.ResetSession();
                    }
                    else if (captureEvent == CaptureEvent.Armed)
                    {
                        EnqueueDiagnostic("整圈捕获：摆杆已在顶部窗口稳定（上膛），现在松手就开始计一整圈。");
                    }
                    else if (captureEvent == CaptureEvent.Released)
                    {
                        EnqueueDiagnostic(string.Format(
                            "整圈捕获：检测到释放（θ={0:F1}°，ω={1:F2} rad/s，抬升方向 {2}）。" +
                            "开始按累计角记录，朝这个方向转满 360° 才算有效。",
                            _capture.ReleaseThetaDeg, _capture.ReleaseOmegaRadPerSec,
                            _capture.LiftSign > 0 ? "正" : "反"));
                    }
                    else if (captureEvent == CaptureEvent.Completed)
                    {
                        FinishCaptureAttempt(t0);
                    }
                    else if (captureEvent == CaptureEvent.Invalidated)
                    {
                        // 失效原因由 FinishCaptureAttempt → 落记录 + 一条诊断，不在这里重复拼。
                        FinishCaptureAttempt(t0);
                    }
                    else if (captureEvent == CaptureEvent.LiftTimeout)
                    {
                        EnqueueDiagnostic(
                            "整圈捕获：120 s 内没检测到释放（摆杆没抬到顶部 180°），已退回普通待机。" +
                            "把摆杆抬到顶部附近（约 180°）再松手；若已抬起仍不判释放，检查 θ 零点与编码器方向。");
                    }
                    else if (captureEvent == CaptureEvent.LiftNotReachedTop)
                    {
                        EnqueueDiagnostic(string.Format(
                            "整圈捕获：这次抬升最高只到 {0:F0}°（离顶部 180° 还差），已回落；继续等待，" +
                            "请把摆杆抬到顶部附近再松手。", _capture.MaxLiftDeg));
                    }
                }
            }

            // ---- 5c. 在线辨识样本（自由摆动：Enabled；泵能量：Swinging 且泵幅 > 0）----
            _identifier.AddSample(t0, theta, omega, state == EngineState.Enabled);

            // ---- 6. 受控停（QuickStop 路径）+ 控制律 + 使能序列 ----
            ushort controlWord = CiA402.CwDisableVoltage;
            int targetCount = armPos;
            bool saturated = false;

            if (_stopPhase != 0)
            {
                // 受控停 QuickStop 路径：0x0002 斜坡 → 确认 QuickStopActive → 0x0000 收尾，全程目标=实际。
                controlWord = StepControlledStop(statusWord);
                targetCount = armPos;
            }
            else if (state == EngineState.Fault || state == EngineState.Disconnected ||
                (state == EngineState.Stopped && _enablePhase == PhaseIdle))
            {
                // 安全态：断使能，目标钉在实际位置上，绝不跳变。
                // Stopped 只有在没有使能子流程在跑时才短路（契约 §2.2 的 0x6040=0）。
                // StartEnable() 从 Stopped 发起后状态仍是 Stopped，这一支若先吃掉，
                // RunEnableSequence 一拍都跑不到，0x6040 会一直钉在 0x0000（现场 6.5 s 后 GUI 超时，sw 恒 0x0250）。
                controlWord = CiA402.CwDisableVoltage;
                targetCount = armPos;
            }
            else if (_enablePhase == PhaseNoMotionGate || _enablePhase == PhaseDriveEnable)
            {
                targetCount = armPos;
                controlWord = RunEnableSequence(statusWord, driveError, modeDisplay, armPos,
                    stepperIn.ActualVelocity, theta, phi, pendulumCount, ref targetCount);
            }
            else if (state == EngineState.Connected)
            {
                // SafeOp 里没有运动门，保持断电 + 目标=实际。
                controlWord = CiA402.CwDisableVoltage;
                targetCount = armPos;
            }
            else
            {
                // Enabled / Swinging / Balancing：跑控制律。
                controlWord = CiA402.CwEnableOperation;
                targetCount = RunControlLaw(
                    state, thetaFromUpright, omega, phi, phiDot,
                    armPos, ref saturated);
                if (_balanceAuthorityLostTripped || _balanceTravelGuardTripped)
                {
                    if (_balanceAuthorityLostTripped)
                    {
                        // 参考被钳死 + 指令仍饱和 + |ψ| 还在增长 = 摆臂失去对摆杆的控制权。
                        // 这是真实故障（离线细扫：ψ0 ≥ 0.15 rad 全部落进这个状态），如实切安全态，
                        // 绝不靠"参考冻结"或继续顶饱和把它糊过去。
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "平衡失控（PositionAuthorityLost）：参考已顶到软限位（φ̇_ref={0:F2} rad/s，φ_ref={1:F3} rad），"
                            + "加速度指令仍在 ±{2:F0} rad/s² 饱和，而 |ψ| 从 {3:F3} 增到 {4:F3} rad。"
                            + "接管时：ψ0={5:F3}、ω0={6:F2} rad/s、φ0={7:F3}、φ̇0={8:F2} rad/s。",
                            _phiDotRef, _phiRef,
                            BalanceAccMaxEnvOverride > 0 ? BalanceAccMaxEnvOverride : BalanceAccMaxRadPerSec2,
                            _balanceAuthorityLostPsiStart, Math.Abs(thetaFromUpright),
                            _balanceEntryPsi, _balanceEntryOmega, _balanceEntryPhi, _balanceEntryPhiDot));
                    }
                    _balanceAuthorityLostTripped = false;
                    _balanceAuthorityLostCycles = 0;
                    _balanceTravelGuardTripped = false;
                    _balanceTravelGuardCycles = 0;
                    EmergencyStopInternal(FaultCode.BalanceLost);
                    state = State;
                    controlWord = CiA402.CwDisableVoltage;
                    targetCount = armPos;
                }
            }

            // 起摆中的泵能量样本：辨识「能量注入速率 G」用。泵幅 0（关泵）时没有样本，不编数。
            if (state == EngineState.Swinging && _stopPhase == 0 && _swingActive.PumpAmplitude > 0)
                _identifier.AddPumpSample(t0, _swing.EnergyJ, _swingActive.PumpAmplitude);

            // 摆杆计数清零：GC1602 有复位命令（置 1 一拍，下一拍清 0 并锁零点），GC1502 只能软件置零。
            // 两型各自的语义都在 StepPendulumZero 里，使能完成的自动归零与手动 ZeroPendulum() 同一条路径。
            // 输出方向被按设备真实能力压成 0（无 RxPDO 映射）时，硬件复位不可达，只能软件置零并如实记日志。
            ushort gcanResetBits = StepPendulumZero(bus.CounterSlice, pendulumRaw, bus.GcanOutputsMapped);

            // ---- 7. 拍内算完即写（输出映像瞬时不可用同样去抖）----
            // 输出过程映像是主站自己的影子帧，不存在"输入陈旧"，瞬时失败只可能是映像句柄 /
            // iomap 争用（SDK 侧 BUSY 实测只持续一拍）。本拍不写，驱动保持上一拍控制字；
            // 连续到阈值才如实判 PdoFrameLoss。从未映射成功则立刻收口，绝不对着空气写。
            // 最后一道物理保护：无论目标来自控制律、使能序列还是故障/连接保持，
            // 写到 0x607A 的 counts 一律先过软限位内侧安全余量。
            long finalSafeMin, finalSafeMax;
            if (TryGetSafeTravelBounds(out finalSafeMin, out finalSafeMax, out _))
            {
                if (targetCount < finalSafeMin) targetCount = (int)finalSafeMin;
                else if (targetCount > finalSafeMax) targetCount = (int)finalSafeMax;
            }
            Exception staleWriteError;
            PdoReadOutcome writeOutcome = TryWriteProcessOutputs(
                controlWord, targetCount, gcanResetBits, out staleWriteError);
            if (writeOutcome == PdoReadOutcome.NotMapped)
            {
                _staleCycles = 0;
                _staleInputCycles = 0;
                EnqueueDiagnostic("PDO 输出映像从未映射成功（本会话没有可用的输出过程映像），立即切安全态："
                    + DiagText.Describe(staleWriteError));
                EmergencyStopInternal(FaultCode.EtherCatState);
                return;
            }
            bool staleWriteThisCycle = writeOutcome == PdoReadOutcome.Stale;
            if (staleWriteThisCycle)
            {
                // 同一拍输入已经计过数：一拍只算一拍，不按方向重复累加（否则阈值会变成 10 拍）。
                bool trip = staleReadThisCycle ? ShouldTripProcessDataStale() : RegisterStaleCycle();
                ReportProcessDataStale("输出", staleWriteError);
                if (trip)
                {
                    TripProcessDataStale("输出");
                    return;
                }
            }
            else
            {
                _lastTargetCount = targetCount;
                _lastTargetValid = true;
            }

            // 输入 / 输出都拿到新数据才是「本拍过程数据有效」：清零连续计数并记一条恢复。
            // 只报一次（_staleCycles > 0 才报），不跟每拍刷。
            if (!staleReadThisCycle && !staleWriteThisCycle)
            {
                MarkProcessDataFresh();
            }

            // ---- 8. 统计与遥测 ----
            long t1 = Stopwatch.GetTimestamp();
            long periodTicks = _lastTickValid ? t0 - _lastTickTimestamp : 0;
            _lastTickTimestamp = t0;
            _lastTickValid = true;
            long computeTicks = t1 - t0;

            if (periodTicks > 0)
            {
                double periodUs = periodTicks * _stopwatchToUs;
                double nominalUs = _dtSec * 1000000.0;
                _jitterStats.Add(t0, Math.Abs(periodUs - nominalUs), _sessionStartTicks);
            }
            _computeStats.Add(t0, computeTicks * _stopwatchToUs, _sessionStartTicks);

            _saturatedCycles = saturated ? _saturatedCycles + 1 : 0;

            PendulumTelemetry t = new PendulumTelemetry();
            t.Sequence = ++_sequence;
            t.TimestampTicks = t0;
            t.EngineState = State;
            t.ThetaRad = theta;
            t.ThetaFromUprightRad = thetaFromUpright;
            t.OmegaRadPerSec = omega;
            t.PhiRad = phi;
            t.PhiDotRadPerSec = phiDot;
            t.PhiRefRad = _phiRef;
            t.PhiTargetRad = _safetyTargetRad;
            // 起始位置（home）恒为坐标 0，偏差就是 φ 本身；两个字段保留是给 UI / CLI 显示兼容。
            t.PhiHomeRad = ArmHomeRad;
            t.PhiDevFromHomeRad = phi;
            t.PendulumCount = pendulumCount;
            t.ArmPositionCount = armPos;
            t.ArmTargetCount = targetCount;
            // PID 遥测字段（ControlThetaOut / Pterm / Iterm / Dterm）保留在结构体里供旧版 UI/CLI 兼容；
            // 平衡律不经过 PID，新建遥测结构时这些字段保持默认 0。
            t.EnergyJ = _swing.EnergyJ;
            t.EnergyTargetJ = _swing.EnergyTargetJ;
            t.EnergyDeltaJ = _swing.EnergyDeltaJ;
            t.SwingUpPhase = _swing.PhaseSec;
            t.SwitchCountdown = _swing.SwitchCountdown;
            // 起摆显示快照（只进遥测；控制律不读这些字段）。
            t.OmegaEstRadPerSec = State == EngineState.Balancing
                ? _balanceOmegaEstimateRadPerSec
                : _swing.OmegaEstRadPerSec;
            t.SwingBootstrapActive = _swing.BootstrapActive;
            t.SwingRecentering = _swing.Recentering;
            t.SwingEnergyAccelRadPerSec2 = _swing.EnergyAccelRadPerSec2;
            t.SwingPumpAccelRadPerSec2 = _swing.PumpAccelRadPerSec2;
            t.SwingCenteringAccelRadPerSec2 = _swing.CenteringAccelRadPerSec2;
            t.SwingCommandAccelRadPerSec2 = _swing.CommandAccelRadPerSec2;
            t.SwingVelocityRefRadPerSec = _swing.VelocityRefRadPerSec;
            t.ReleaseWaitActive = _capture.IsWaiting;
            t.ReleaseArmed = _capture.IsArmed;
            t.CaptureActive = _capture.IsCapturing;
            t.CaptureReleaseThetaDeg = _capture.ReleaseThetaDeg;
            t.CaptureReleaseOmegaRadPerSec = _capture.ReleaseOmegaRadPerSec;
            t.CaptureMaxDeviationDeg = _capture.MaxDeviationDeg;
            t.CaptureHoldSeconds = _capture.RecordingSeconds;
            t.CaptureSweepDeg = _capture.SweepDeg;
            t.CaptureMaxSweepDeg = _capture.MaxSweepDeg;
            t.CaptureKeyPoints = _capture.KeyPointCount;
            t.PendulumFeedbackAgeSeconds = PendulumFeedbackAgeSeconds;
            t.ControlWord = controlWord;
            t.StatusWord = statusWord;
            t.DriveErrorCode = driveError;
            t.ModesDisplay = modeDisplay;
            // 0x606C 单位 = Pulse/s（实机口径，见 ControlMath 的换算注释），rad/s 换算只在 ControlMath 这一处做；
            // 每转位置单位取标定链裁出的 CPR_ARM（0x2102 实读 × 减速比，或 0x608F × 0x6091），未标定时如实给 0。
            // 只进遥测 / 交叉校验，不进控制律。
            t.ArmVelocityPulsePerSec = stepperIn.ActualVelocity;
            t.ArmVelocityFromDriveRadPerSec =
                ControlMath.ArmRadPerSecFromDrivePulsePerSec(stepperIn.ActualVelocity, _countsPerRevArm);
            t.ArmDigitalInputs = armDigitalInputs;
            t.ArmEStopInputActive = CiA402.IsDigitalEStopAsserted(armDigitalInputs);
            t.ArmFeedbackIsEstimate = ArmFeedbackIsEstimate;
            t.FollowingErrorCount = (int)Math.Max(int.MinValue,
                Math.Min(int.MaxValue, _safetyFollowingErrorCount));
            t.ActualWkc = _lastActualWkc;
            t.ExpectedWkc = _lastExpectedWkc;
            t.ArmSlaveInOp = armInOp;
            t.GcanSlaveInOp = gcanInOp;
            t.OwnedSlavesResponding = ownedResponding;
            t.OmegaFromFreqRadPerSec = omegaFromFreq;
            t.FreqCrossCheckValid = freqCrossCheckValid;
            t.PendulumFreqCh1Hz = pendulumFreqHz;
            t.PendulumCountCh2 = pendulumRawCh2;
            t.PendulumFreqCh2Hz = pendulumFreq2Hz;
            t.CycleDeltaTicks = periodTicks;
            t.ComputeTicks = computeTicks;
            t.FaultCode = (FaultCode)Volatile.Read(ref _faultCode);
            t.EStopLatch = Volatile.Read(ref _eStopLatch) != 0;
            t.IsSaturated = saturated;
            t.SaturatedCycles = _saturatedCycles;

            _slot.Write(t);
            _ring.Write(t);
            Volatile.Write(ref _telemetryBeatTicks, Stopwatch.GetTimestamp());

            // S10 的周期侧证据：seqlock 写完必须落回偶数版本，奇数就是这次写没落地（写停）。
            // 环满不算写停——那只说明读侧落后，按 §3.3 丢样本 + 计数即可。
            if ((_slot.Version & 1) != 0)
            {
                EmergencyStopInternal(FaultCode.TimingInvalid);
            }

            // 1 Hz 诊断节流。周期路径里只有这一处允许拼字符串。
            if (_lastDiagTicks == 0) _lastDiagTicks = t0;
            if (t0 - _lastDiagTicks >= Stopwatch.Frequency)
            {
                _lastDiagTicks = t0;
                EnqueueDiagnostic(string.Format(
                    "beat seq={0} state={1} θ={2:F3} 0x6064={3} 目标={4} cw=0x{5:X4} sw=0x{6:X4} wkc={7}/{8} bus={9} own[arm={10},gcan={11},resp={12}] ωf={13:F3} evDrop={14} 跟随主站={15} 远程={16} 0x60FD=0x{17:X8} 急停输入={18} φ̇驱动={19:F3} dupSkip={20} stale={21}",
                    t.Sequence, t.EngineState, t.ThetaRad, t.ArmPositionCount, t.ArmTargetCount,
                    t.ControlWord, t.StatusWord, t.ActualWkc, t.ExpectedWkc,
                    bus.IsBusInOp ? "OP" : bus.CurrentState.ToString(),
                    armInOp, gcanInOp, ownedResponding,
                    t.FreqCrossCheckValid ? t.OmegaFromFreqRadPerSec : 0,
                    _events.Dropped,
                    CiA402.IsFollowingMaster(statusWord), CiA402.IsRemote(statusWord),
                    armDigitalInputs, t.ArmEStopInputActive, t.ArmVelocityFromDriveRadPerSec,
                    Interlocked.Read(ref _duplicateCycleSkips), _staleCycles));

                // 0x60FD bit23 急停输入的观察窗：跑起来一段时间仍恒 0，多半是急停端子没配
                //（0x2702 功能码 8 默认未启用，X0/X1/X2 被负限位/正限位/原点占着）。
                // 这是提示不是判据：位恒 0 分不清"没配"和"没按"，只能提示现场去核。
                bool driving = t.EngineState == EngineState.Enabled ||
                               t.EngineState == EngineState.Swinging ||
                               t.EngineState == EngineState.Balancing;
                if (driving)
                {
                    if (_armEStopWatchStartTicks == 0) _armEStopWatchStartTicks = t0;
                    if (t.ArmEStopInputActive) _armEStopBitSeen = true;
                    if (!_armEStopHintRaised && !_armEStopBitSeen &&
                        t0 - _armEStopWatchStartTicks > ArmEStopHintAfterSeconds * Stopwatch.Frequency)
                    {
                        _armEStopHintRaised = true;
                        EnqueueDiagnostic(
                            "0x60FD bit23（急停输入）从起动到现在恒为 0：急停端子多半没配置——把 0x2702 的某路输入设成功能码 8 " +
                            "（默认 X0=负限位 4 / X1=正限位 2 / X2=原点 1，要急停得牺牲一路）。" +
                            "该链路响应 ≥10 ms（输入脉宽 >10 ms + 0x2703 滤波默认 1000µs），只能当 10 ms 级保护，" +
                            "不能当 1 ms 级保护用；真正的硬线保护不经过驱动器。");
                    }
                }
            }
        }

        // ---- 过程数据瞬时 STALE 去抖（周期路径专用，见 §1 / §7 的说明）----

        /// <summary>
        /// 读本拍过程输入。三态返回：Fresh = 读到新数据；Stale = 本拍没合并到帧（可沿用上一拍）；
        /// NotMapped = 本会话从未映射成功（没有上一拍可沿用，必须立刻收口）。
        /// <para>
        /// **从站实测掉线（<see cref="PdoInputStaleException"/> / IsLost）不在这里吞**：那不是"这一拍
        /// 没合并到帧"，而是映像已经确定是掉站前最后一帧，原样抛给 ExecuteCycle 的 EtherCatState 口径。
        /// 只有 SDK 零拷贝入口报 "PDO 输入映像不可用" 才按瞬时 STALE 处理（未进 OP / 本拍 STALE / BUSY）。
        /// </para>
        /// </summary>
        private PdoReadOutcome TryReadProcessInputs(
            out StepperTxPdo stepperIn,
            out uint pendulumRaw,
            out uint pendulumFreqHz,
            out uint pendulumRawCh2,
            out uint pendulumFreq2Hz,
            out Exception staleError)
        {
            stepperIn = default(StepperTxPdo);
            pendulumRaw = 0;
            pendulumFreqHz = 0;
            pendulumRawCh2 = 0;
            pendulumFreq2Hz = 0;
            staleError = null;

            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen) return PdoReadOutcome.NotMapped;

            try
            {
                stepperIn = bus.StepperInput();
                // 摆杆计数一律按 32 位位型取：GC1502 是 UDINT（会绕回 0xFFFFFFFF→0），GC1602 是 DINT。
                pendulumRaw = bus.PendulumCountRaw();
                pendulumFreqHz = bus.PendulumFreqHz();
                // 第 2 路计数/频率：同一份映射内存，本拍一次性取走（只展示，不进控制律）。
                pendulumRawCh2 = bus.PendulumCountCh2Raw();
                pendulumFreq2Hz = bus.PendulumFreqCh2Hz();
                Volatile.Write(ref _processInputPrimed, 1);
                return PdoReadOutcome.Fresh;
            }
            catch (PdoInputStaleException)
            {
                // 实测离线：映像是掉站前最后一帧，不是单拍抖动。交给既有 EtherCatState 口径立刻收口。
                throw;
            }
            catch (InvalidOperationException ex)
            {
                if (!IsPdoImageUnavailable(ex)) throw;
                staleError = ex;
                return ClassifyPdoMappingFailure(Volatile.Read(ref _processInputPrimed));
            }
        }

        /// <summary>
        /// 软限位内侧的安全余量边界（counts 域）。返回 false = 软限位不合法（min ≥ max），
        /// 不虚构安全区间。margin = max(2000, 4%·span)，供最终 0x607A 钳位与平衡触边 guard 共用。
        /// </summary>
        private bool TryGetSafeTravelBounds(out long safeMin, out long safeMax, out long safeMargin)
        {
            long span = (long)_limitsActive.ArmMaxCount - _limitsActive.ArmMinCount;
            safeMin = (long)_limitsActive.ArmMinCount;
            safeMax = (long)_limitsActive.ArmMaxCount;
            safeMargin = 0L;
            if (span <= 0) return false;

            safeMargin = Math.Max(2000L, (long)(0.04 * span));
            safeMin = (long)_limitsActive.ArmMinCount + safeMargin;
            safeMax = (long)_limitsActive.ArmMaxCount - safeMargin;
            if (safeMin > safeMax)
            {
                long mid = ((long)_limitsActive.ArmMinCount + _limitsActive.ArmMaxCount) / 2;
                safeMin = safeMax = mid;
            }
            return true;
        }

        /// <summary>
        /// 写本拍过程输出。返回 Stale = 本拍没写成（驱动保持上一拍控制字）；NotMapped = 从未映射成功。
        /// 输出区是主站自己的影子帧，不存在"输入陈旧"，能落到这里的瞬时失败只有映像句柄 / iomap 争用。
        /// </summary>
        private PdoReadOutcome TryWriteProcessOutputs(
            ushort controlWord, int targetCount, ushort gcanResetBits, out Exception staleError)
        {
            staleError = null;
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen) return PdoReadOutcome.NotMapped;

            try
            {
                ref StepperRxPdo stepperOut = ref bus.StepperOutput();
                stepperOut.ControlWord = controlWord;
                stepperOut.ModesOfOperation = CiA402.ModeCsp;
                stepperOut.TargetPosition = targetCount;
                // GCAN 输出区：GC2008 的 8 路 DO 是别人的通道，恒写 0 不驱动；GC1602 的复位位按上面算的写。
                bus.WriteGcanOutputs(gcanResetBits);
                Volatile.Write(ref _processOutputPrimed, 1);
                return PdoReadOutcome.Fresh;
            }
            catch (PdoInputStaleException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                if (!IsPdoImageUnavailable(ex)) throw;
                staleError = ex;
                return ClassifyPdoMappingFailure(Volatile.Read(ref _processOutputPrimed));
            }
        }

        /// <summary>
        /// 把「过程映像不可用」二分成两种语义：
        /// 本会话从未映射成功（<paramref name="primed"/> = 0）= 没有上一拍可沿用，必须立刻收口；
        /// 成功过（= 1）= 单拍抖动，可沿用上一拍过程数据继续控制。
        /// </summary>
        private static PdoReadOutcome ClassifyPdoMappingFailure(int primed)
        {
            return primed == 0 ? PdoReadOutcome.NotMapped : PdoReadOutcome.Stale;
        }

        /// <summary>
        /// SDK 零拷贝映射入口在「未进 OP / 本拍 STALE / BUSY」时统一抛 InvalidOperationException，
        /// 消息是 "PDO 输入映像不可用" 或 "PDO 输出映像不可用"（见 DarraEtherCAT.Slave.InputsMapping）。
        /// 只认这两条：「结构体大于 PDO」「结构体含引用字段」是配置 / 代码错误，原样抛出，不进去抖。
        /// **实测掉线是 PdoInputStaleException（IsLost）**，调用方必须先按类型分开，不能一刀切。
        /// </summary>
        private static bool IsPdoImageUnavailable(Exception ex)
        {
            string message = ex == null ? null : ex.Message;
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("PDO 输入映像不可用", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("PDO 输出映像不可用", StringComparison.Ordinal) >= 0;
        }

        /// <summary>瞬时 STALE：把上一拍的成功样本顶成本拍工作值（只拷贝，不做任何换算）。</summary>
        private void ReuseLastProcessData(
            out StepperTxPdo stepperIn,
            out uint pendulumRaw,
            out uint pendulumFreqHz,
            out uint pendulumRawCh2,
            out uint pendulumFreq2Hz)
        {
            stepperIn = _lastStepperIn;
            pendulumRaw = _lastPendulumRaw;
            pendulumFreqHz = _lastPendulumFreqHz;
            pendulumRawCh2 = _lastPendulumRawCh2;
            pendulumFreq2Hz = _lastPendulumFreq2Hz;
        }

        /// <summary>本拍拿到新过程数据：缓存原始样本，供下一拍瞬时 STALE 时沿用。</summary>
        private void CacheProcessData(
            StepperTxPdo stepperIn,
            uint pendulumRaw,
            uint pendulumFreqHz,
            uint pendulumRawCh2,
            uint pendulumFreq2Hz)
        {
            _lastStepperIn = stepperIn;
            _lastPendulumRaw = pendulumRaw;
            _lastPendulumFreqHz = pendulumFreqHz;
            _lastPendulumRawCh2 = pendulumRawCh2;
            _lastPendulumFreq2Hz = pendulumFreq2Hz;
        }

        /// <summary>
        /// 记一拍无有效过程数据：连续计数与累计计数各 +1，返回是否已到故障阈值。
        /// 周期路径的输入 / 输出两侧共用这一处计数，判据只有一份。
        /// </summary>
        private bool RegisterStaleCycle()
        {
            _staleCycles++;
            _staleTotal++;
            return ShouldTripProcessDataStale();
        }

        /// <summary>
        /// 去抖判据（纯逻辑）：连续无有效过程数据是否已到故障阈值。
        /// 第 20 拍触发（1 起算），与 SafetyInterlock S6 共用 PdoFrameLossTripConsecutive = 20。
        /// </summary>
        private bool ShouldTripProcessDataStale()
        {
            return _staleCycles >= PdoStaleTripConsecutive;
        }

        /// <summary>
        /// 单拍 STALE 的限流诊断：首拍必报，之后每累计 <see cref="PdoStaleDiagnosticEvery"/> 拍一条。
        /// 异常消息照实写进去（一眼分清是输入端还是输出端、SDK 报的是哪一句），绝不每拍刷事件队列。
        /// </summary>
        private void ReportProcessDataStale(string side, Exception error)
        {
            if (_staleTotal == 1 || _staleTotal % PdoStaleDiagnosticEvery == 0)
            {
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[PDO] {0}过程数据本拍不可用（连续第 {1} 拍，累计 {2} 拍）：沿用上一拍过程数据继续控制，未停机；{3}",
                    side, _staleCycles, _staleTotal, DiagText.Describe(error)));
            }
        }

        /// <summary>过程数据恢复：只在连续丢拍数 > 0 时报一条，带本次连续拍数与累计数。</summary>
        private void ReportProcessDataRecovered()
        {
            if (_staleCycles <= 0) return;
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "[PDO] 过程数据已恢复：本次连续 {0} 拍无有效过程数据（累计 {1} 拍），控制律继续。",
                _staleCycles, _staleTotal));
        }

        /// <summary>
        /// 本拍输入 / 输出都有效：连续计数清零（累计数保留），恢复时记一条诊断。
        /// 周期路径只在「输入与输出都成功」的拍调用这一处，别在别处单独清计数器。
        /// </summary>
        private void MarkProcessDataFresh()
        {
            ReportProcessDataRecovered();
            _staleCycles = 0;
        }

        /// <summary>
        /// 连续无有效过程数据到阈值：按 PdoFrameLoss 如实收口。
        /// 这里**不**下调 expected_wkc、不把 STALE 记成成功 —— 只停止用陈旧数据继续控制。
        /// </summary>
        private void TripProcessDataStale(string side)
        {
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "[PDO] 连续 {0} 拍无有效过程数据（{1}方向，累计 {2} 拍）：按 PdoFrameLoss 切安全态。",
                _staleCycles, side, _staleTotal));
            EmergencyStopInternal(FaultCode.PdoFrameLoss);
        }

        // ==================== 控制律 ====================

        /// <summary>
        /// 软限位是否参与目标钳位。**只对自动运动生效**（起摆 / 平衡）：手动点动与停机态不钳位，
        /// 否则用户永远走不出出厂占位区间、采不出真实左右限位。审计直接调用本判据，
        /// 防止后续有人把无条件 Clamp 又加回去。
        /// </summary>
        internal static bool MotionClampsToSoftLimit(EngineState state)
        {
            return state == EngineState.Swinging || state == EngineState.Balancing;
        }

        /// <summary>
        /// 起摆开始前把系统辨识的固有频率回灌到泵频，并**如实写清来源**。
        /// <para>
        /// 规则：辨识样本充分（<see cref="IdentificationResult.Sufficient"/>）且 f_n 落在合理区间 ⇒
        /// 用实测值覆盖 <see cref="SwingUpParams.PumpFrequencyHz"/>；
        /// 否则保留配置值，并明确打一条「使用默认 ω_n（未辨识）」。
        /// </para>
        /// <para>
        /// 为什么必须回灌：参数文件里的 <c>natural_freq_hz</c> 只在「真的辨识过」时才非 0。
        /// 旧版全仓没有任何一处把辨识结果与 <see cref="SwingUpParams.PumpFrequencyHz"/> 比对或同步，
        /// 于是「辨识过但是没人用」和「从来没辨识」在日志里长得一模一样 —— 现场无法判断泵频到底是不是这台机器的。
        /// 只在起摆开始时算一次（一次辨识 + 一条日志），不进周期热路径。
        /// </para>
        /// </summary>
        private void ApplyIdentifiedPumpFrequency()
        {
            IdentificationResult id = _identifier.Identify(_limitsActive, _swingActive);
            bool identified = id.Sufficient &&
                              ControlMath.IsFinite(id.NaturalFreqHz) &&
                              id.NaturalFreqHz > 0.05 && id.NaturalFreqHz < 20.0;
            if (identified)
            {
                SwingUpParams p = _swingActive;
                p.PumpFrequencyHz = id.NaturalFreqHz;
                _swingActive = p;
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[SWING] 泵频 = 系统辨识实测 ω_n={0:F4} Hz（ζ={1:F4}，置信度 {2:F2}，半周期样本 {3} 个）：已覆盖配置值。"
                    + "注：能量注入项本身不依赖这个频率，它是给可选的往复泵用的；这条日志的作用是证明「这台机器的固有频率真被量过」。",
                    id.NaturalFreqHz, id.DampingZeta, id.Confidence, id.HalfPeriodSamples));
            }
            else
            {
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[SWING] 使用默认 ω_n={0:F4} Hz（未辨识：{1}）。要拿到实测值，先让摆杆自由摆动 ≥2 s（至少 3 个整周期）"
                    + "再起摆，辨识器才攒得够半周期样本。",
                    _swingActive.PumpFrequencyHz,
                    string.IsNullOrEmpty(id.InsufficientReason) ? "辨识样本不充分" : id.InsufficientReason));
            }
        }

        /// <summary>
        /// 起摆开始时自述一遍能量模型：J_eq / mgl、折算出的 ω_n 与等效摆长、目标能量、泵与增益。
        /// 现场换摆杆后第一眼就能看出「这组参数还是不是这台机器的」——等效摆长与实测自由摆动频率
        /// 对不上就是没标定；两项必须成对给（比值决定 ω_n，只改一项等于把能量尺度算错）。
        /// </summary>
        private void LogSwingUpModel()
        {
            double jEq = _limitsActive.JEq;
            double mgl = _limitsActive.Mgl;
            double omegaN = jEq > 0 && mgl > 0 ? Math.Sqrt(mgl / jEq) : double.NaN;
            double lengthM = ControlMath.IsFinite(omegaN) && omegaN > 0 ? 9.81 / (omegaN * omegaN) : double.NaN;
            double target = 2.0 * mgl * (1.0 + _swingActive.TargetEnergyGain);
            EnqueueDiagnostic(string.Format(
                CultureInfo.InvariantCulture,
                "[SWING] 起摆开始：J_eq={0:G6} kg·m²、mgl={1:G6} N·m ⇒ ω_n=√(mgl/J_eq)={2:F4} rad/s" +
                "（等效摆长 l=g/ω_n²={3:F4} m）；目标能量 E*=2·mgl·(1+ε)={4:F3} J；泵幅={5:F2} rad/s² @ {6:F4} Hz；" +
                "Ke={7:F3} rad/s²/J；AMax={8:F2} rad/s²。J_eq / mgl / 等效摆长须按实际摆杆标定" +
                "（按 J_eq=m·l²、mgl=m·g·l 成对给）。",
                jEq, mgl, omegaN, lengthM, target,
                _swingActive.PumpAmplitude, _swingActive.PumpFrequencyHz, _swingActive.Ke, _swingActive.AMax));

            // 耦合 r/l：能量法本身不读它，但**注入的绝对量级**正比于 m·l·r。
            // 现场「爬到某个幅值就上不去」到底是权限不够还是摩擦吃掉，只有这个数能算清，
            // 所以起摆开始时必须如实报出「用的是哪个值、来源是什么」——不许拿默认值冒充实测。
            double radiusRatio = _limitsActive.RadiusRatio;
            if (ControlMath.IsFinite(radiusRatio) && radiusRatio > 0)
            {
                // m·l·r = (r/l)·J_eq（J_eq = m·l²，与 mgl = m·g·l 成对定义）。
                // 旧式 (r/l)·mgl/g = (r/l)·m·l 漏了一个 r，量纲是 kg·m 而不是 kg·m²，
                // 会把 ΔE 高报 1/l 倍（l=0.12 m 时 8.33 倍），误导 Ke 整定。
                double mlr = radiusRatio * jEq;
                double perHalfCycle = 2.0 * mlr * _swingActive.AMax;   // θt=90° 时半个周期的最大注入
                EnqueueDiagnostic(string.Format(
                    CultureInfo.InvariantCulture,
                    "[SWING] 耦合 r/l={0:F4}（来源：参数文件 limits.radius_ratio 标定值，换机构后必须重标）⇒ m·l·r={1:G4} kg·m²；"
                    + "u_E 顶到 AMax={2:F1} rad/s² 时，摆幅 90° 处每半个周期最多注入 ΔE≈{3:F3} J。"
                    + "若实测爬升率明显低于这个数，差额就是摩擦 / 摆臂阻尼吃掉的（不是权限不够）。",
                    radiusRatio, mlr, _swingActive.AMax, perHalfCycle));
            }
            else
            {
                EnqueueDiagnostic(
                    "[SWING] 耦合 r/l 未标定（参数文件 limits.radius_ratio=0）：能量注入的绝对量级算不出来，"
                    + "以下任何基于 r/l 的数字都不可信。"
                    + "标定方法：量出臂轴到摆轴的距离 r [m]，除以等效摆长 l=g/ω_n²（上面那条日志里的 l），"
                    + "把比值写进 furuta_params.json 的 limits.radius_ratio；优先用 calibrate-radius 实测。"
                    + "不要用「拟合值」：臂加速度只能由 2400 count/转的编码器二次差分得到，量化噪声会把斜率压小一个量级。");
            }

            // 泵频为 0 的静默失效：旧版控制律直接拿 p.PumpFrequencyHz 推进相位，
            // 0 Hz 时 sin 恒为 0 —— 摆臂一动不动，而日志里泵幅又明明不是 0。现在控制律按内置默认补齐，
            // 这里必须如实说明来源，不能让现场以为「泵在工作」或「泵频是 1.439 就代表辨识过」。
            if (_swingActive.PumpAmplitude > 0 && !(_swingActive.PumpFrequencyHz > 0))
            {
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[SWING] 泵幅={0:F2} rad/s² > 0，但泵频配置为 0（参数文件缺 pump_frequency_hz / pump.pump_hz）："
                    + "控制层已按内置默认 {1:F4} Hz 推进相位，否则 sin(0)=0 会让泵完全不出力。"
                    + "要按本机固有频率注入，请先做自由摆动辨识或把实测值写进参数文件。",
                    _swingActive.PumpAmplitude, SwingUpParams.CreateDefault().PumpFrequencyHz));
            }

            // 起摆权限与近直立淡出下限（2026-11-21 第二十三轮：软件速度包线已删除）。
            // 现场排障看这一行：本拍真正生效的是「唯一的加速度权限 AMax」，速度不由软件封顶；
            // 0x607A 只剩驱动器 0x6080 的物理天花板。近直立淡出 floor 仍按 20/aPumpMax 收口。
            {
                double driveCeiling = SafetyLimits.ArmDriveCeilingRadPerSec;
                double configuredSlew = _limitsActive.OmegaArmMaxRadPerSec > 0 &&
                                        ControlMath.IsFinite(_limitsActive.OmegaArmMaxRadPerSec)
                    ? _limitsActive.OmegaArmMaxRadPerSec
                    : SafetyLimits.CreateDefault().OmegaArmMaxRadPerSec;
                double aPumpLimit = _swingActive.AMax > 0 && ControlMath.IsFinite(_swingActive.AMax)
                    ? _swingActive.AMax
                    : SwingUpParams.CreateDefault().AMax;
                double effectiveFloor = ControlMath.Clamp(_swingActive.ThetaFadeFloor,
                    SwingUpParams.MinThetaFadeFloor, 1.0);
                if (aPumpLimit > 0.0)
                {
                    double absoluteFloor = SwingUpParams.FadeFloorAccelRadPerSec2 / aPumpLimit;
                    if (absoluteFloor < effectiveFloor) effectiveFloor = absoluteFloor;
                }
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[SWING] 起摆权限：|φ̈|≤{0:F2} rad/s²（= AMax，唯一一道加速度权限）；"
                    + "臂速不由软件封顶（0x607A slew 天花板 {1:F1} rad/s = 驱动器 0x6080 3000 rpm，"
                    + "配置值 {2:F1}）；近直立淡出 floor 生效值={3:F2}（参数值 {4:F2}，结构性下限 {5:F2}）；"
                    + "能量环积分增益={6:F0}。",
                    aPumpLimit, driveCeiling, configuredSlew,
                    ControlMath.Clamp(effectiveFloor, 0.05, 1.0), _swingActive.ThetaFadeFloor,
                    SwingUpParams.MinThetaFadeFloor, SwingUpController.EnergyIntegralGain));
            }

            // ε > 0 的捕获窗口警告：E* 在「刚好到直立」的分界线上方，摆杆不会在 |ψ|≤θ_switch 处折返，
            // 只能以 ω=√(2·2mgl·ε/J) 掠过顶端。捕获门仍可能在**过顶前**的窄相位放行，但窗口比 ε<0 窄得多，
            // 这正是「同样参数有时 8 s、有时 180 s」的一个来源。不拒收（合法区间允许），但必须如实说明。
            if (_swingActive.TargetEnergyGain > 0)
            {
                double omegaAtTop = jEq > 0
                    ? Math.Sqrt(2.0 * 2.0 * mgl * _swingActive.TargetEnergyGain / jEq)
                    : double.NaN;
                // 「准备」段的三个真实判据（与 SwingUpController.Step 同一口径）：
                //   |ψ| ≤ CapturePrepareApproachRad 且 |ψ| > 0.02、ω_est < −0.05、能量落在捕获窗 ±margin。
                // 这里把生效值与换算出的能量边界一起打出来，现场照着三个条件逐条核对即可。
                SwingUpParams defSwing = SwingUpParams.CreateDefault();
                double approach = _swingActive.CapturePrepareApproachRad > 0 &&
                                  ControlMath.IsFinite(_swingActive.CapturePrepareApproachRad)
                    ? _swingActive.CapturePrepareApproachRad
                    : defSwing.CapturePrepareApproachRad;
                double marginRatio = _swingActive.CapturePrepareEnergyMarginRatio > 0 &&
                                     ControlMath.IsFinite(_swingActive.CapturePrepareEnergyMarginRatio)
                    ? _swingActive.CapturePrepareEnergyMarginRatio
                    : defSwing.CapturePrepareEnergyMarginRatio;
                double thetaSwitch = _swingActive.ThetaSwitchRad > 0 &&
                                     ControlMath.IsFinite(_swingActive.ThetaSwitchRad)
                    ? _swingActive.ThetaSwitchRad
                    : defSwing.ThetaSwitchRad;
                double captureModeMax = _swingActive.CaptureModeAmplitudeMaxRad > 0 &&
                                        ControlMath.IsFinite(_swingActive.CaptureModeAmplitudeMaxRad)
                    ? _swingActive.CaptureModeAmplitudeMaxRad
                    : defSwing.CaptureModeAmplitudeMaxRad;
                double pendRate = _swingActive.CapturePendulumRateRadPerSec > 0 &&
                                  ControlMath.IsFinite(_swingActive.CapturePendulumRateRadPerSec)
                    ? _swingActive.CapturePendulumRateRadPerSec
                    : defSwing.CapturePendulumRateRadPerSec;
                double gateLow = mgl * (1.0 + Math.Cos(thetaSwitch));
                double gateSpeed = captureModeMax * pendRate;
                double gateHigh = 2.0 * mgl + 0.5 * jEq * gateSpeed * gateSpeed;
                double prepareMarginJ = marginRatio * 2.0 * mgl;
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[SWING] 能量裕量 ε=+{0:F4} > 0：E* 在分界线上方，摆杆不会折返而是以 ω≈{1:F2} rad/s 掠过顶端。"
                    + "捕获窗口 = [mgl(1+cosθ_switch), 2mgl+½J(u_max·ω_n)²] = [{2:F3}, {3:F3}] J；"
                    + "控制层在以下三条同时成立时进「准备」段（停泵 + 摆臂按速度伺服回 0）："
                    + "① |ψ| ≤ {4:F2} rad（CapturePrepareApproachRad，且 |ψ| > 0.02）；"
                    + "② 下落侧：ω_est < −0.05 rad/s（上行侧不刹车）；"
                    + "③ 能量 ∈ [{5:F3}, {6:F3}] J（= 捕获窗 [{2:F3}, {3:F3}] 外扩 ±{7:F3} J，"
                    + "即 CapturePrepareEnergyMarginRatio={8:F3} × 2mgl）。"
                    + "出厂值 -0.002 会让摆杆在 |ψ|≈0.089 rad 处折返、停留更久，"
                    + "在窗口边界附近更稳；ε 取正时请确认日志里「准备」段确实出现。",
                    _swingActive.TargetEnergyGain, omegaAtTop, gateLow, gateHigh,
                    approach, gateLow - prepareMarginJ, gateHigh + prepareMarginJ,
                    prepareMarginJ, marginRatio));
            }
        }

        /// <summary>
        /// 把**权威的 counts 软限位**换算成控制律内部使用的弧度区间。
        /// <para>
        /// 这是全仓唯一允许把摆臂限位换成角度的地方，而且换算只用「摆臂一圈计数」这一个用户给过的量：
        /// φ = (count − 零点计数) × 2π / CPR_ARM。限位判定与目标钳位本身都在 counts 域
        /// （见 <see cref="RunControlLaw"/> 末尾的 target 钳位与 <see cref="CheckSoftLimitBeforeMotion"/>），
        /// 这里只是为了内部动力学 / 抗积分饱和需要一个等价的弧度边界。
        /// </para>
        /// 返回 false = 摆臂未标定（CPR ≤ 0）或换算结果非有限，此时不做行程门控（摆臂本来也不能动）。
        /// </summary>
        private bool TryGetActiveLimitRad(SafetyLimits limits, out double minRad, out double maxRad)
        {
            minRad = 0;
            maxRad = 0;
            if (_countsPerRevArm <= 0 || !limits.ArmLimitsValid)
                return false;
            minRad = (limits.ArmMinCount - _phiZeroCount) * ControlMath.TwoPi / _countsPerRevArm;
            maxRad = (limits.ArmMaxCount - _phiZeroCount) * ControlMath.TwoPi / _countsPerRevArm;
            return ControlMath.IsFinite(minRad) && ControlMath.IsFinite(maxRad) && minRad < maxRad;
        }

        /// <summary>
        /// 整圈捕获的实测越界余量（counts）。按摆臂一圈计数把 <see cref="CaptureSoftLimitMarginRad"/>
        /// 换成 counts，量级与机型无关；未标定时给 1 count（此时捕获本来就会因未标定被拒）。
        /// </summary>
        private int CaptureSoftLimitMarginCount()
        {
            if (_countsPerRevArm <= 0) return 1;
            double counts = CaptureSoftLimitMarginRad * _countsPerRevArm / ControlMath.TwoPi;
            if (!ControlMath.IsFinite(counts) || counts < 1) return 1;
            return (int)Math.Ceiling(counts);
        }

        /// <summary>
        /// 内部弧度 → 绝对 0x6064 计数（只用于日志显示，把摆臂量统一成 counts 口径）。
        /// 未标定 / 非有限值返回 0，不编造位置。
        /// </summary>
        private int ArmCountFromRad(double rad)
        {
            if (_countsPerRevArm <= 0 || !ControlMath.IsFinite(rad)) return 0;
            double counts = _phiZeroCount + rad * _countsPerRevArm / ControlMath.TwoPi;
            if (!ControlMath.IsFinite(counts)) return 0;
            if (counts >= int.MaxValue) return int.MaxValue;
            if (counts <= int.MinValue) return int.MinValue;
            return (int)Math.Round(counts, MidpointRounding.AwayFromZero);
        }

        private int RunControlLaw(
            EngineState state,
            double thetaFromUpright,
            double omega,
            double phi,
            double phiDot,
            int armPos,
            ref bool saturated)
        {
            // 软限位（绝对 counts）→ 内部弧度边界：只在缺省/未标定时退化为「不做行程门控」。
            double limitMinRad;
            double limitMaxRad;
            if (!TryGetActiveLimitRad(_limitsActive, out limitMinRad, out limitMaxRad))
            {
                limitMinRad = double.NegativeInfinity;
                limitMaxRad = double.PositiveInfinity;
            }

            // 离开平衡就把扰动快照标成非活动；MarkInactive 内部只在状态变化时写一次快照。
            if (state != EngineState.Balancing)
                _disturbanceObserver.MarkInactive();

            // 平衡分支内部量外提：最终 [BALANCE] 遥测要打「slew 与 counts 增量护栏后真正写进 0x607A 的目标」，
            // 必须等下面整段目标计算完成后再发射，所以这些量在本方法作用域声明、分支内只赋值。
            double aCmdRaw = 0.0, aCmd = 0.0, uCmdEff = 0.0;
            double accMax = 0.0, armRateMax = 0.0;
            double balancePosTerm = 0.0;
            double balanceReferenceRate = 0.0;
            double balanceTelemetrySec = 0.0;
            bool accSat = false, rateClipped = false, positionAuthorityLost = false;
            bool balanceRecentering = false;
            double phiTarget;
            if (state == EngineState.Swinging)
            {
                bool ready = _swing.Step(
                    thetaFromUpright + Math.PI, omega, phi, phiDot,
                    _limitsActive, limitMinRad, limitMaxRad, _swingActive, _dtSec, out phiTarget);
                _phiRef = phiTarget;

                // 起摆诊断（常规 1 s 一条；|ψ|<0.5 时 100 Hz；FURUTA_SWING_TRACE_HZ>0 时
                // 前 20 s 无条件按该频率，供停滞/泵相位分析）：
                // 现场排查「起摆超时」只看这一条。
                // **φ̈ 两项分开打，都是本拍真实施加的量，不是配置值**：
                //   φ̈_能量 = 能量项（已含软相位门与近直立淡出）；
                //   φ̈_泵   = 往复泵本拍实际分到的加速度，泵关闭 / 被 |ω| 门关掉时是 0.00。
                // 旧版这里打的是 _swingActive.PumpAmplitude（配置值），泵根本没参与也显示 2.00，
                // 排障时被误读成「泵在工作」—— 现在只认实际量。
                // 只进日志，不参与控制。
                long swingNow = Stopwatch.GetTimestamp();
                // 常规 1 s 一条；**接近直立（|ψ| < 0.5 rad）时加密到 100 Hz**。
                // 掠顶事件只有几十毫秒（|ψ|≤0.1 的窗口按 ω=3 算约 67 ms），1 s 采样永远看不到
                // 捕获门四条判据里到底哪一条没过 —— 现场因此只能靠猜。只在接近顶部才加密，日志量可控。
                long swingLogInterval = Math.Abs(thetaFromUpright) < 0.5
                    ? Math.Max(1L, Stopwatch.Frequency / 100L)
                    : Stopwatch.Frequency;
                // 高频跟踪（FURUTA_SWING_TRACE_HZ）：开启后 Swinging 状态**无条件**按该频率，
                // 不再看 |ψ|；只在前 SwingTraceSeconds 秒。默认关闭，行为与旧版一致。
                if (_swingTraceHz > 0 && _swing.PhaseSec <= SwingTraceSeconds)
                {
                    double traceInterval = Stopwatch.Frequency / _swingTraceHz;
                    if (traceInterval >= 1.0)
                        swingLogInterval = (long)traceInterval;
                }
                if (_swingLogTicks == 0 ||
                    swingNow - _swingLogTicks >= swingLogInterval)
                {
                    _swingLogTicks = swingNow;
                    EnqueueDiagnostic(string.Format(
                        CultureInfo.InvariantCulture,
                        "[SWING] t={0:F2}s 分支={1} 能量E={2:F3}J 目标={3:F3}J 差={4:F3}J "
                        + "φ̈能量={5:F2} φ̈泵={6:F2} rad/s² ψ(离直立)={7:F3}rad θ(下垂起)={12:F3}rad "
                        + "ω={8:F2}rad/s ω_est={9:F2} 0x6064={10}→{11} counts φ={14:F2}rad φ̇={15:F2}rad/s 门倒计时={13} stale={16}"
                        + " holdSlow={17} holdFast={18}"
                        + " sat={19} centerSat={20:F2} phiMean={21:F2}"
                        + " turn={22:F3}rad bias={23:F4}J turns={24} cosθ={25:F4} trace={26:F0}Hz"
                        // 只读加速度分解：φ̈和 = 能量 + 泵 + 回中(钳位后) + 起动。
                        + " φ̈回中={27:F2} φ̈起动={28:F2} φ̈和={29:F2} φ̈钳后={30:F2}"
                        + " φ̇ref={31:F2} φref−φ={32:F3} φ̈功率钳={33:F2} φ̈回中钳={34:F2}"
                        + " aCmdEntry={35:F1} curOk={36} headGate={37} ready={38}"
                        + " aPend={39:F1} aArm={40:F1} a1={41:F1} u={42:F3} topω={43:F2} topBrake={44}"
                        + " fastω={45:F2} slowω={46:F2} Efast={47:F3} Eest={48:F3}"
                        + " rev={49} revRate={50:F2} lim={51}",
                        _swing.PhaseSec,
                        _swing.Recentering ? "回中"
                            : (_swing.Preparing ? "准备"
                            : (_swing.BootstrapActive ? "起动" : "能量法")),
                        _swing.EnergyJ, _swing.EnergyTargetJ, _swing.EnergyDeltaJ,
                        _swing.EnergyAccelRadPerSec2, _swing.PumpAccelRadPerSec2,
                        thetaFromUpright, omega, _swing.OmegaEstRadPerSec,
                        ArmCountFromRad(phi), ArmCountFromRad(phiTarget),
                        // θ 与 Step 入参同源（ψ+π，范围 [0,2π)），让现场一眼区分「离直立」和「下垂起」。
                        // 旧日志只打 ψ，现场把它当成 θ 读，才出现「θ=−3.113 离直立 1.7°」的误判。
                        thetaFromUpright + Math.PI,
                        // 捕获门的连续满足拍数倒计时：到 0 就交棒。掠顶时它是最直接的"还差多少"。
                        _swing.SwitchCountdown,
                        // 捕获门另外两条判据（臂位置 / 臂速）的自变量，掠顶排障时缺了它们就只能猜。
                        phi, phiDot,
                        // 本拍连续无有效过程数据的拍数：STALE 去抖是否在兜、兜了几拍，一眼可见。
                        _staleCycles,
                        // 捕获门的两个净计数通道（只读诊断）：哪个通道在推进、有没有被 −2 衰减掉，
                        // 下一次台架失败时不用再靠猜。定义与衰减规则见 SwingUpController.Step。
                        _swing.SlowHold, _swing.FastHold,
                        // 回中饱和 / φ 均值（只读诊断，与 SwingUpController 的慢窗一致）：
                        // sat=本拍回中项钳位前超 centerLimit；centerSat≈1 s 饱和比例；
                        // phiMean≈1 s 的 φ 均值。台架成功轮 centerSat≈0、φ_mean≈+0.24；
                        // 失败轮 centerSat≈0.77~0.78、φ_mean≈−1.0 —— 一眼分辨失败轮特征。
                        _swing.CenterSaturated ? 1 : 0,
                        _swing.CenterSatRatio,
                        _swing.PhiMeanRad,
                        // 折返角只读诊断：turn = 最近一次折返时离直立的 |ψ|（第一次折返前为 0，
                        // 用 turns 区分）；bias = 折返偏置 J；turns = 已检测到的折返次数。
                        // 台架"折返角是否收敛"只看这三个数。
                        _swing.LastTurnAngleRad, _swing.TurnBiasJ, _swing.TurnCount,
                        // cosθ 与 θ(下垂起)=ψ+π 同源，供停滞/泵相位分析直接对能量项符号；
                        // trace=本段生效的跟踪频率（0=关闭，常规 1 s / 近顶 100 Hz）。
                        Math.Cos(thetaFromUpright + Math.PI), _swingTraceHz,
                        // 加速度分解（只读）：回中 / 起动 / 总和 / 最终钳位 / 速度参考 / 参考超前量。
                        _swing.CenteringAccelRadPerSec2,
                        _swing.BootstrapAccelRadPerSec2, _swing.CommandAccelRawRadPerSec2,
                        _swing.CommandAccelRadPerSec2, _swing.VelocityRefRadPerSec,
                        _swing.PositionRefRad - phi,
                        // 两道子钳位后的值：φ̈和 = φ̈功率钳 + φ̈回中钳（逐项可对账）。
                        _swing.PowerAccelRadPerSec2, _swing.CenteringTotalAccelRadPerSec2,
                        // 入口门只读快照：aCmdEntry / 当前实测包线 / HEAD 预测门 / 最终 ready。
                        _swing.CaptureEntryAccelRadPerSec2,
                        _swing.CaptureCurrentStateOk ? 1 : 0,
                        _swing.CaptureHeadGateOk ? 1 : 0,
                        _swing.CaptureReady ? 1 : 0,
                        _swing.CapturePendAccelRadPerSec2,
                        _swing.CaptureArmAccelRadPerSec2,
                        _swing.CaptureTotalAccelRadPerSec2,
                        _swing.CaptureModeAmpRad,
                        _swing.PredictedTopOmegaRadPerSec,
                        _swing.TopSpeedBrakeActive ? 1 : 0,
                        _swing.FastOmegaRadPerSec,
                        _swing.OmegaEstRadPerSec,
                        _swing.EnergyFastJ,
                        _swing.EnergyJ,
                        _swing.HighEnergyReverseActive ? 1 : 0,
                        _swing.HighEnergyReverseRateRadPerSec,
                        _swing.HighEnergyLimitCode));
                }

                bool manual = _balanceRequest;
                _balanceRequest = false;
                if (ready)
                {
                    EnterBalancing(thetaFromUpright, phi);
                    // 同拍覆盖起摆局部目标，避免公共 slew 把交接前一拍目标当平衡起点。
                    phiTarget = _balanceCmdRad;
                }
                else if (manual)
                {
                    EnqueueDiagnostic("切换判据还没连续满足，暂不切平衡。");
                }
            }
            else if (state == EngineState.Enabled)
            {
                if (Volatile.Read(ref _radiusCalActive) != 0)
                {
                    // r/l 主动标定：脉冲状态机独占摆臂目标。任何时刻请求中止 / 目标越界都会
                    // 由 RadiusCalibrator 自己收尾，下一拍回到正常 hold（目标 0），不锁存故障。
                    if (Volatile.Read(ref _radiusCalAbortRequested) != 0)
                    {
                        Interlocked.Exchange(ref _radiusCalAbortRequested, 0);
                        _radiusCal.Abort("用户取消");
                        Volatile.Write(ref _radiusCalActive, 0);
                        _radiusCalResult = _radiusCal.Result;
                        EnqueueDiagnostic("[CAL] r/l 标定已取消，摆臂回到正常 hold。");
                        phiTarget = _phiTargetPrimed ? _phiTargetPrev : phi;
                    }
                    else
                    {
                        bool done = _radiusCal.Step(
                            _dtSec, ControlMath.WrapPi(thetaFromUpright + Math.PI),
                            omega, phi, phiDot, limitMinRad, limitMaxRad);
                        phiTarget = _radiusCal.TargetRad;
                        if (done)
                        {
                            RadiusCalibrationResult r = _radiusCal.Result;
                            _radiusCalResult = r;
                            Volatile.Write(ref _radiusCalActive, 0);
                            if (r != null && r.Valid)
                            {
                                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                    "[CAL] r/l 标定完成：r/l={0:F4}（{1} 相有效，σ={2:F4}；平均 |φ̈|={3:F1} rad/s²、"
                                    + "|θ̈|={4:F1} rad/s²）。把该值写进 limits.radius_ratio 后，"
                                    + "平衡接管的 (r/l)·Ka>g/l 自检才会生效。",
                                    r.RadiusRatio, r.ValidSamples, r.StdDev,
                                    r.MeanAbsPhiAccelRadPerSec2, r.MeanAbsThetaAccelRadPerSec2));
                                // 成功但仍有被排除的样本时如实附注：均值可能被区间截断过，现场要能看到。
                                if (!string.IsNullOrEmpty(r.Reason))
                                    EnqueueDiagnostic("[CAL] r/l 标定附注：" + r.Reason);
                            }
                            else
                            {
                                EnqueueDiagnostic("[CAL] r/l 标定失败：" + (r == null ? "无结果" : r.Reason));
                            }
                        }
                    }
                }
                else
                {
                    // 待机 + 手动点动：点动按住时目标按 JogSpeedRadPerSec 推进（只受点动速度与增量护栏管），
                    // 松开即停 = 目标冻结在当前值（未点动过时就是起始位置 0）。不追实际角——
                    // 被外力挪开仍锚在显式目标上，绝不用"目标=实际"把任意角续成新起点。
                    // **点动不受软限位约束**：软限位是用户靠点动采集出来的边界，点动必须先能越过旧边界；
                    // 限位只在起摆 / 平衡等自动运动里做钳位（见下面的统一 Clamp）。
                    // 整圈捕获（等待抬升 / 记录中）期间：目标钉在进入捕获时的 φ（StartReleaseWait 已 prime），
                    // 不响应点动 —— 记录期间摆臂一动，整圈数据就不可比。
                    int jog = Volatile.Read(ref _jogDirection);
                    bool returningHome = Volatile.Read(ref _returnHomeActive) != 0;
                    if (_capture.IsWaiting || _capture.IsCapturing)
                    {
                        phiTarget = _phiTargetPrimed ? _phiTargetPrev : phi;
                    }
                    else if (returningHome)
                    {
                        // 回中目标可以是标定零点，也可以是「行程中点」（recenter 的自动居中路径）。
                        // 目标以 counts 存，换算成内部弧度；进入时 _phiTargetPrev 已锚在当前 φ，
                        // 后面统一走 slew 护栏，绝不跳变；限位在下面 counts 域再钳一次。
                        phiTarget = _countsPerRevArm > 0
                            ? (double)((long)_returnHomeTargetCount - _phiZeroCount) * ControlMath.TwoPi / _countsPerRevArm
                            : ArmHomeRad;
                    }
                    else if (jog != 0)
                    {
                        double from = _phiTargetPrimed ? _phiTargetPrev : phi;
                        phiTarget = from + jog * JogSpeedRadPerSec * _dtSec;
                    }
                    else
                    {
                        phiTarget = _phiTargetPrimed ? _phiTargetPrev : ArmHomeRad;
                    }
                }

                // ---- R11 手动捕获平衡（Enabled 态近顶释放）----
                // 只登记请求不直接切状态；周期线程逐拍用与起摆交棒同一套捕获门判，
                // 连续 ManualCaptureHoldCycles 拍满足才 EnterBalancing。不绕过软限位/急停/ThetaTrip。
                if (Volatile.Read(ref _manualBalanceRequest) != 0)
                {
                    double manualOmega = ControlMath.IsFinite(_balanceOmegaEstimateRadPerSec)
                        ? _balanceOmegaEstimateRadPerSec : omega;
                    double manualCenter = ControlMath.IsFinite(limitMinRad) &&
                                          ControlMath.IsFinite(limitMaxRad)
                        ? 0.5 * (limitMinRad + limitMaxRad) : 0.0;
                    double manualPosErr = ControlMath.Clamp(phi - manualCenter,
                        -BalancePositionErrorClampRad, BalancePositionErrorClampRad);
                    double manualAccel = -(BalanceKPsi * thetaFromUpright
                        + BalanceKOmega * manualOmega
                        + BalanceKPhi * manualPosErr
                        + BalanceKPhiDot * phiDot);
                    bool manualGate =
                        Volatile.Read(ref _returnHomeActive) == 0 &&
                        Volatile.Read(ref _jogDirection) == 0 &&
                        Volatile.Read(ref _radiusCalActive) == 0 &&
                        _stopPhase == 0 &&
                        !_capture.IsWaiting && !_capture.IsCapturing &&
                        Math.Abs(thetaFromUpright) <= SwingUpParams.VerifiedRecoverableThetaRad &&
                        Math.Abs(manualOmega) <= ManualCaptureOmegaMaxRadPerSec &&
                        Math.Abs(phiDot) <= ManualCaptureArmRateMaxRadPerSec &&
                        Math.Abs(manualAccel) <= BalanceAccMaxRadPerSec2 &&
                        SwingUpController.CaptureGateAllows(
                            thetaFromUpright, manualOmega, phiDot, _swingActive);
                    if (manualGate)
                    {
                        if (Interlocked.Increment(ref _manualBalanceHoldCycles) >= ManualCaptureHoldCycles)
                        {
                            Volatile.Write(ref _manualBalanceRequest, 0);
                            Interlocked.Exchange(ref _manualBalanceHoldCycles, 0);
                            EnterBalancing(thetaFromUpright, phi);
                            phiTarget = ControlMath.Clamp(_balanceCmdRad, limitMinRad, limitMaxRad);
                            EnqueueDiagnostic("[BALANCE] 手动捕获平衡接管。");
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _manualBalanceHoldCycles, 0);
                        long manualNow = Stopwatch.GetTimestamp();
                        if (_manualBalanceLogTicks == 0 ||
                            manualNow - _manualBalanceLogTicks >= Stopwatch.Frequency / 2)
                        {
                            _manualBalanceLogTicks = manualNow;
                            double manualPendRate = _swingActive.CapturePendulumRateRadPerSec;
                            if (!(manualPendRate > 0.0) || !ControlMath.IsFinite(manualPendRate))
                                manualPendRate = 9.0416;
                            double manualU = SwingUpController.ComputeModeAmplitude(
                                thetaFromUpright, manualOmega, phiDot,
                                SwingUpParams.VerifiedCaptureCouplingRatioRad, manualPendRate);
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[BALANCE] 手动捕获等待：ψ={0:F4}（≤0.15）、ω_est={1:F3}（≤0.8）、"
                                + "φ̇={2:F2}（≤2.5）、aEst={3:F0}（≤800）rad/s²、u={4:F3}（≤0.12）。",
                                thetaFromUpright, manualOmega, phiDot, manualAccel, manualU));
                        }
                    }
                }
                _phiRef = phiTarget;
            }
            else if (state == EngineState.Balancing)
            {
                // 回中是慢速参考积分，不替换任何 PD 项；触发 / 距离 / 完成都用同一个实测 φ 权威。
                // 稳定门带滞回和 ω 门，单拍抖动不清计时。
                bool haveCenter = ControlMath.IsFinite(limitMinRad) && ControlMath.IsFinite(limitMaxRad);
                double travelCenter = haveCenter ? 0.5 * (limitMinRad + limitMaxRad) : 0.0;
                double omegaEstimate = ControlMath.IsFinite(_balanceOmegaEstimateRadPerSec)
                    ? _balanceOmegaEstimateRadPerSec
                    : omega;
                double omegaMeasured = ControlMath.IsFinite(omega) ? omega : omegaEstimate;
                double omegaControl;
                if (_balanceOmegaHandoffCycles < BalanceOmegaHandoffBlendCycles)
                {
                    // 前 50 ms 纯用 2 ms 实测；随后 50 ms 线性过渡到 40 ms 估计。
                    double estWeight = 0.0;
                    if (_balanceOmegaHandoffCycles > BalanceOmegaHandoffDirectCycles)
                    {
                        estWeight = (_balanceOmegaHandoffCycles - BalanceOmegaHandoffDirectCycles) /
                            (double)(BalanceOmegaHandoffBlendCycles - BalanceOmegaHandoffDirectCycles);
                    }
                    omegaControl = omegaMeasured * (1.0 - estWeight) + omegaEstimate * estWeight;
                    _balanceOmegaHandoffCycles++;
                }
                else
                {
                    omegaControl = omegaEstimate;
                }
                _balanceOmegaUsedRadPerSec = omegaControl;
                double stablePsi = BalanceRecenterStablePsiMinRad;
                double doneRad = BalanceRecenterDoneRad;
                if (_countsPerRevArm > 0)
                {
                    double countRad = ControlMath.TwoPi / _countsPerRevArm;
                    stablePsi = Math.Max(stablePsi, 8.0 * countRad);
                    doneRad = Math.Max(doneRad, 2.0 * countRad);
                }
                double exitPsi = Math.Max(BalanceRecenterExitPsiRad, stablePsi * 1.5);
                bool recentering = _balanceRecenterActive != 0;

                // ---- 外力扰动观测 / 裕度分层 / 前馈（摆杆与摆臂都用实测角加速度） ----
                double modelOmegaN2 = _limitsActive.JEq > 0 && ControlMath.IsFinite(_limitsActive.JEq) &&
                                      ControlMath.IsFinite(_limitsActive.Mgl)
                    ? _limitsActive.Mgl / _limitsActive.JEq
                    : 0.0;
                double modelRadiusRatio = ControlMath.IsFinite(_limitsActive.RadiusRatio)
                    ? _limitsActive.RadiusRatio : 0.0;
                double armReferenceError = Math.Abs(_balanceCmdRad - phi);
                double recenterBiasError = recentering
                    ? Math.Abs(_balanceRecenterReferenceRad - phi) : 0.0;
                double armPositionError = haveCenter ? Math.Abs(phi - travelCenter) : 0.0;
                double omegaMarginAbs = Math.Max(
                    Math.Abs(ControlMath.IsFinite(omegaMeasured) ? omegaMeasured : 0.0),
                    Math.Abs(ControlMath.IsFinite(omegaEstimate) ? omegaEstimate : 0.0));

                // 平衡环的设计常量自己说了算：不再被起摆/手动模式的历史保守参数（150/15）压住。
                // 台架 A/B 可用 FURUTA_BALANCE_ACC_MAX 覆盖（350 是保守包线）。
                // 这里提前算，是因为扰动前馈权限按当前 accMax 的 20% 限幅；值与下方原口径完全一致。
                accMax = BalanceAccMaxEnvOverride > 0
                    ? BalanceAccMaxEnvOverride
                    : (BalanceAccMaxRadPerSec2 > 0 && ControlMath.IsFinite(BalanceAccMaxRadPerSec2)
                        ? BalanceAccMaxRadPerSec2
                        : 800.0);
                // R22 交棒扭矩限幅：前 100 ms 的 aCmd 上限 250 rad/s²，之后恢复 accMax。
                if (_balanceEnterSec < BalanceHandoffAccelWindowSeconds && accMax > BalanceHandoffAccelCapRadPerSec2)
                    accMax = BalanceHandoffAccelCapRadPerSec2;

                DisturbanceObserverInput disturbanceInput = new DisturbanceObserverInput();
                disturbanceInput.PsiRad = thetaFromUpright;
                disturbanceInput.OmegaMeasuredRadPerSec = omegaMeasured;
                disturbanceInput.OmegaEstimateRadPerSec = omegaEstimate;
                disturbanceInput.PendulumAccelRadPerSec2 = _balanceOmegaEstimator.AccelerationRadPerSec2;
                disturbanceInput.PendulumAccelValid = _balanceOmegaEstimator.AccelerationValid;
                disturbanceInput.ArmAccelMeasuredRadPerSec2 = _balanceArmAccelEstimator.AccelerationRadPerSec2;
                disturbanceInput.ArmAccelValid = _balanceArmAccelEstimator.AccelerationValid;
                disturbanceInput.ArmPhiRad = phi;
                disturbanceInput.ArmPhiDotRadPerSec = phiDot;
                disturbanceInput.ArmCmdAccelPrevRadPerSec2 = _balanceLastAppliedAccelRadPerSec2;
                disturbanceInput.ArmCmdSatPrev = _balanceLastAccSat;
                disturbanceInput.ArmCmdRateClippedPrev = _balanceLastRateClipped;
                disturbanceInput.ArmAuthorityValid = !_balanceLastAuthorityLost &&
                    !_balanceTravelGuardTripped && _balanceTravelGuardCycles == 0;
                disturbanceInput.MarginGraceWindow = _balanceEnterSec < BalanceMarginEntryGraceSeconds;
                disturbanceInput.ArmReferenceErrorRad = armReferenceError;
                disturbanceInput.RecenterBiasErrorRad = recenterBiasError;
                disturbanceInput.ArmPositionErrorRad = armPositionError;
                disturbanceInput.ArmAccMaxRadPerSec2 = accMax;
                disturbanceInput.OmegaN2 = modelOmegaN2;
                disturbanceInput.RadiusRatio = modelRadiusRatio;
                disturbanceInput.OmegaResidualRmsRad = _balanceOmegaResidualRad;
                disturbanceInput.DtSec = _dtSec;
                _disturbanceObserver.Update(disturbanceInput);

                DisturbanceMarginState disturbanceMargin = _disturbanceObserver.MarginState;
                if (disturbanceMargin == DisturbanceMarginState.StrongDisturbance &&
                    _lastDisturbanceMarginState != DisturbanceMarginState.StrongDisturbance)
                {
                    // 强扰动：立即冻结回中，把 PD 位置/速度参考 re-prime 到实测 φ/φ̇，
                    // 并把回中外环 r 锚到实测 φ。只冻结 r 不重置 PD 参考的话，R24 那种
                    // r=−3.9 / φ=−0.05 的大位置偏置会继续把 aCmd 拉走，等于没交还权限。
                    // 若此前不在回中，则强制进入回中 hold（active=1）：否则 balancePosTerm 仍按
                    // φ−行程中点 产生大偏置，re-prime 只改 _balanceCmdRad 解决不了位置项。
                    if (_balanceRecenterActive == 0)
                    {
                        _balanceRecenterActive = 1;
                        _balanceRecenterStartRad = phi;
                        _balanceRecenterCenterRad = haveCenter ? travelCenter : 0.0;
                        _balanceRecenterAbortCycles = 0;
                        recentering = true;
                    }
                    ReprimeBalanceReference(phi);
                    _balanceRecenterReferenceRad = phi;
                    _balanceRecenterRate = 0.0;
                    _balanceRecenterDoneSec = 0.0;
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "[BALANCE] 强扰动：立即冻结回中并 re-prime 到实测 φ={0:F3}/φ̇={1:F2} rad/s，回中 r 锚到实测 φ；"
                        + "|ψ|={2:F4} rad、|ω|={3:F3} rad/s（实测 {4:F3}/估计 {5:F3}）、"
                        + "dHat={6:F2} rad/s²（残差 {7:F2}、置信 {8:F2}）、参考误差={9:F3} rad、"
                        + "rcBias={10:F3} rad、posErr={11:F3} rad、sat={12}/{13}。",
                        phi, phiDot, Math.Abs(thetaFromUpright), omegaMarginAbs,
                        omegaMeasured, omegaEstimate,
                        _disturbanceObserver.DisturbanceHatRadPerSec2,
                        _disturbanceObserver.ResidualRadPerSec2,
                        _disturbanceObserver.Confidence,
                        armReferenceError, recenterBiasError, armPositionError,
                        _balanceLastAccSat ? 1 : 0, _balanceLastRateClipped ? 1 : 0));
                }
                else if (disturbanceMargin != _lastDisturbanceMarginState)
                {
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "[BALANCE] 扰动裕度 {0}→{1}：|ψ|={2:F4} rad、|ω|={3:F3} rad/s（实测 {4:F3}/估计 {5:F3}）、"
                        + "dHat={6:F2} rad/s²（残差 {7:F2}、置信 {8:F2}）、参考误差={9:F3} rad、"
                        + "rcBias={10:F3} rad、posErr={11:F3} rad、sat={12}/{13}、冻结={14}；"
                        + "r/l={15:F3}、g/l={16:F2}。",
                        DisturbanceObserver.MarginStateName(_lastDisturbanceMarginState),
                        DisturbanceObserver.MarginStateName(disturbanceMargin),
                        Math.Abs(thetaFromUpright), omegaMarginAbs, omegaMeasured, omegaEstimate,
                        _disturbanceObserver.DisturbanceHatRadPerSec2,
                        _disturbanceObserver.ResidualRadPerSec2,
                        _disturbanceObserver.Confidence,
                        armReferenceError, recenterBiasError, armPositionError,
                        _balanceLastAccSat ? 1 : 0, _balanceLastRateClipped ? 1 : 0,
                        _disturbanceObserver.RecenterFrozen ? 1 : 0,
                        modelRadiusRatio, modelOmegaN2));
                }
                _lastDisturbanceMarginState = disturbanceMargin;

                if (_disturbanceObserver.RecenterFrozen)
                {
                    if (_balanceRecenterFrozen == 0)
                    {
                        _balanceRecenterFrozen = 1;
                        if (recentering)
                        {
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[BALANCE] 回中冻结（{0}）：|ψ|={1:F4}、|ω|={2:F3} rad/s、"
                                + "dHat={3:F2}、参考误差={4:F3}、rcBias={5:F3}、posErr={6:F3}；"
                                + "PD 保持在线，r 停止推进。",
                                DisturbanceObserver.MarginStateName(disturbanceMargin),
                                Math.Abs(thetaFromUpright), omegaMarginAbs,
                                _disturbanceObserver.DisturbanceHatRadPerSec2,
                                armReferenceError, recenterBiasError, armPositionError));
                        }
                    }
                }
                else if (_balanceRecenterFrozen != 0)
                {
                    _balanceRecenterFrozen = 0;
                    if (recentering)
                    {
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "[BALANCE] 回中恢复：扰动裕度回到正常，r 从实测锚点按 ≤{0:F0} rad/s² 平滑恢复推进。",
                            BalanceRecenterResumeRateRadPerSec2));
                    }
                }

                double disturbanceCompensation = _disturbanceObserver.CompensationRadPerSec2;

                // 低频扰动诊断（2 Hz 限流，只在 margin 非正常或 dHat 超门限时打）：
                // dRaw / dHat / 置信度 / 前馈 / 9 ms 摆臂模型 / 实测与预测角加速度全部逐项可对账。
                long disturbanceNow = Stopwatch.GetTimestamp();
                if (disturbanceMargin != DisturbanceMarginState.Normal ||
                    Math.Abs(_disturbanceObserver.DisturbanceHatRadPerSec2) >
                    DisturbanceObserver.PreDisturbanceRadPerSec2)
                {
                    if (_disturbanceLogTicks == 0 ||
                        disturbanceNow - _disturbanceLogTicks >= Stopwatch.Frequency / 2)
                    {
                        _disturbanceLogTicks = disturbanceNow;
                        BalanceDisturbanceSnapshot distSnap;
                        if (_disturbanceObserver.TryReadSnapshot(out distSnap))
                        {
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[BALANCE] dist t={0:F2}s ψ={1:F4} ω={2:F3}/{3:F3} dRaw={4:F2} dHat={5:F2} "
                                + "conf={6:F2} margin={7} freeze={8} ff={9:F1} aArm={10:F1} "
                                + "α={11:F1} αPred={12:F1} refErr={13:F3} rcBias={14:F3} "
                                + "rl={15:F3} bff={16:F3} bw={17:F1}Hz strong#={18} frozen={19:F2}s "
                                + "posErr={20:F3}。",
                                _balanceEnterSec, thetaFromUpright, omegaMeasured, omegaEstimate,
                                distSnap.ResidualRadPerSec2, distSnap.DisturbanceHatRadPerSec2,
                                distSnap.Confidence, DisturbanceObserver.MarginStateName(distSnap.MarginState),
                                distSnap.RecenterFrozen ? 1 : 0, distSnap.CompensationRadPerSec2,
                                distSnap.ArmAccelModelRadPerSec2,
                                distSnap.MeasuredAccelRadPerSec2, distSnap.PredictedAccelRadPerSec2,
                                distSnap.ArmReferenceErrorRad, distSnap.RecenterBiasErrorRad,
                                distSnap.ModelCouplingRatio, distSnap.FeedForwardCouplingRatio,
                                distSnap.BandwidthHz, distSnap.StrongCount, distSnap.FrozenSeconds,
                                distSnap.ArmPositionErrorRad));
                        }
                    }
                }
                else
                {
                    _disturbanceLogTicks = 0;
                }

                if (recentering)
                {
                    bool unstable = !haveCenter ||
                        Math.Abs(thetaFromUpright) > exitPsi ||
                        Math.Abs(omegaControl) > BalanceRecenterExitOmegaRadPerSec;
                    if (_balanceRecenterFrozen != 0)
                    {
                        // 扰动冻结期间由 margin 接管：不把回中 abort 成旧的中心参考位置项。
                        // 旧 abort 会把 r 置回 0；φ 又远离中心时，位置项立刻重新变成大偏置，
                        // 等于把刚交给 PD 的权限又收回去。保持 active / r=实测 φ，恢复后再决定。
                        _balanceRecenterAbortCycles = 0;
                    }
                    else if (unstable)
                    {
                        _balanceRecenterAbortCycles++;
                        if (_balanceRecenterAbortCycles >= BalanceRecenterAbortCycles)
                        {
                            _balanceRecenterActive = 0;
                            _balanceRecenterRate = 0.0;
                            _balanceStableSec = 0.0;
                            _balanceRecenterLogTicks = 0;
                            recentering = false;
                            double preReprimeRefGap = _balanceCmdRad - phi;
                            // **同一拍**把 PD 参考 re-prime 到实测 φ / φ̇（与 EnterBalancing 同写法）。
                            // 不这样做的话，退出后 referenceRate 立刻切回已卷到 ±22 的积分器；
                            // r22 台架 `θ` 越界前的最后 80 ms 正是这个阶跃把摆臂动量烧光的。
                            ReprimeBalanceReference(phi);
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[BALANCE] 回中退出（连续 {0} 拍不稳定）：|ψ|={1:F4}（阈 {2:F3}）、"
                                + "|ω_est|={3:F3}（阈 {4:F3}）rad/s；实测位移={5:F3} rad，"
                                + "参考-实测差={6:F3} rad；已 re-prime 到实测 φ={7:F3}/φ̇={8:F2}。",
                                _balanceRecenterAbortCycles, Math.Abs(thetaFromUpright), exitPsi,
                                Math.Abs(omegaControl), BalanceRecenterExitOmegaRadPerSec,
                                phi - _balanceRecenterStartRad, preReprimeRefGap,
                                phi, _balancePhiDotCmd));
                        }
                    }
                    else
                    {
                        _balanceRecenterAbortCycles = 0;
                    }
                }
                if (!recentering && _balanceRecenterActive == 0)
                {
                    bool stableNow = Math.Abs(thetaFromUpright) <= stablePsi &&
                                     Math.Abs(omegaControl) <= BalanceRecenterStableOmegaRadPerSec;
                    bool outsideHysteresis = Math.Abs(thetaFromUpright) > exitPsi ||
                                             Math.Abs(omegaControl) > BalanceRecenterExitOmegaRadPerSec;
                    if (stableNow)
                    {
                        _balanceStableSec += _dtSec;
                    }
                    else if (outsideHysteresis)
                    {
                        if (_balanceStableSec >= 0.05)
                        {
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[BALANCE] 回中稳定计时清零：累计 {0:F2} s 后 |ψ|={1:F4} > {2:F3} 或 "
                                + "|ω_est|={3:F3} > {4:F3} rad/s。",
                                _balanceStableSec, Math.Abs(thetaFromUpright), exitPsi,
                                Math.Abs(omegaControl), BalanceRecenterExitOmegaRadPerSec));
                        }
                        _balanceStableSec = 0.0;
                    }

                    if (BalanceSimpleModeEnv <= 0 && _balanceRecenterFrozen == 0 && haveCenter &&
                        _balanceStableSec >= BalanceRecenterStableSeconds &&
                        Math.Abs(travelCenter - phi) > BalanceRecenterStartRad)
                    {
                        _balanceRecenterActive = 1;
                        // r 从**实测 φ** 起步，不直接跳到中点：位置项初始为 0，避免回中一开始就
                        // 产生大位置偏置（R24：φ=+1.274 时 r 直接置 0，位置项瞬间 −10.8 rad/s²，
                        // 随后 r 漂到 −3.9，扰动来临前已把 PD 权限压住）。之后 r 按外环慢速修正。
                        _balanceRecenterReferenceRad = phi;
                        _balanceRecenterRate = 0.0;
                        _balanceRecenterStartRad = phi;
                        _balanceRecenterCenterRad = travelCenter;
                        _balanceRecenterAbortCycles = 0;
                        _balanceRecenterDoneSec = 0.0;
                        recentering = true;
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "[BALANCE] 回中开始：实测 φ={0:F3} → 行程中点 {1:F3} rad（距离 {2:F3}），"
                            + "稳定 {3:F2} s、|ψ|={4:F4}、|ω_est|={5:F3} rad/s；"
                            + "参考-实测差={6:F3} rad，r 从实测 φ 起步。原四增益 PD 保持，慢速移动臂位置参考到中点。",
                            phi, travelCenter, Math.Abs(travelCenter - phi), _balanceStableSec,
                            Math.Abs(thetaFromUpright), Math.Abs(omegaControl), _balanceCmdRad - phi));
                    }
                }
                balanceRecentering = recentering;

                // 非回中仍是以几何中点为零参考的原四增益 LQR。
                balancePosTerm = _balanceActiveProfile.KPhi *
                    ControlMath.Clamp(phi - (haveCenter ? travelCenter : 0.0),
                        -BalancePositionErrorClampRad, BalancePositionErrorClampRad);
                double balancePhiDotControlTerm = _balanceActiveProfile.KPhiDot * phiDot;
                bool yieldToBalance = recentering &&
                    (_balanceRecenterFrozen != 0 ||
                     Math.Abs(thetaFromUpright) > BalanceRecenterStablePsiMinRad ||
                     Math.Abs(omegaControl) > BalanceRecenterYieldOmegaRadPerSec);
                if (recentering)
                {
                    // 回中不替换任何 PD 项，只给臂位置项一个慢速参考偏置 r。
                    // 实测 φ=3.7 rad 时原 LQR 已处于稳态，说明存在常值平衡偏置；必须移动参考而不是
                    // 把 Kφ<0 直接当位置误差反馈（那会产生右半平面极点）。
                    // ṙ = −k_ref·(φ−中点)，k_ref 由标定 r/l 导出（见 BalanceRecenterReferenceGainNumerator）；
                    // 5 状态特征值在 b=0.048~2.0 内最大实部为负，唯一平衡 φ=中点、r=0。
                    if (!yieldToBalance)
                    {
                        // 参考积分增益由标定耦合 r/l 导出：k_ref = 0.0288/(r/l)。
                        // 原 Kφ̇/Kφ=0.724 只在小耦合（0.048）下稳定；R24 的 r/l=0.24 会得到
                        // +0.120±0.857j 的右半平面根，回中自己就会把臂推走。这里用模型感知的
                        // 稳定增益，保留“慢速移动参考到中点”的原意图；margin 仍是独立的安全层。
                        // r/l<0.03 或未标定时 k_ref=0：回中参考保持不动，避免在结构边界附近
                        // 用不可信耦合值推进参考。
                        double coupling = ControlMath.IsFinite(_limitsActive.RadiusRatio)
                            ? _limitsActive.RadiusRatio : 0.0;
                        double refKi = coupling >= BalanceRecenterCouplingMin
                            ? ControlMath.Clamp(
                                BalanceRecenterReferenceGainNumerator / coupling,
                                BalanceRecenterReferenceGainMin,
                                BalanceRecenterReferenceGainMax)
                            : 0.0;
                        double refRateTarget = -refKi * (phi - _balanceRecenterCenterRad);
                        // 参考速度用 50 rad/s² 斜率恢复：强扰动冻结解除后从 0 平滑接上，
                        // 不做单拍阶跃。它只限外环 r 的速度，不是摆臂速度限幅。
                        _balanceRecenterRate = ControlMath.Slew(
                            refRateTarget, _balanceRecenterRate,
                            BalanceRecenterResumeRateRadPerSec2 * _dtSec);
                        double refNext = _balanceRecenterReferenceRad
                            + _balanceRecenterRate * _dtSec;
                        _balanceRecenterReferenceRad = ControlMath.Clamp(
                            refNext, limitMinRad, limitMaxRad);
                    }
                    else
                    {
                        // 冻结/让路是立即的：r 停止推进，PD 不受影响。
                        _balanceRecenterRate = 0.0;
                    }
                    balancePosTerm = _balanceActiveProfile.KPhi *
                        ControlMath.Clamp(phi - _balanceRecenterReferenceRad,
                            -BalancePositionErrorClampRad, BalancePositionErrorClampRad);
                }
                else
                {
                    _balanceRecenterRate = 0.0;
                }
                // 台架 A/B：接管后前 N 秒只保留摆杆 Kψ/Kω，先隔离 Kφ 位置项与 Kφdot 速度项。
                if (BalanceHandoffPureLqrSecondsOverride > 0 &&
                    _balanceEnterSec < BalanceHandoffPureLqrSecondsOverride)
                {
                    balancePosTerm = 0.0;
                    balancePhiDotControlTerm = 0.0;
                }
                aCmdRaw = -(_balanceActiveProfile.KPsi * thetaFromUpright
                    + _balanceActiveProfile.KOmega * omegaControl
                    + balancePosTerm
                    + balancePhiDotControlTerm);
                if (!ControlMath.IsFinite(aCmdRaw)) aCmdRaw = 0.0;

                // 扰动前馈在加速度域物理一致地注入：aCmd_ff = −kff·dHat/b_ff（耦合项为加号，抵消正 d 需负 aArm）。
                // 观测器已按当前 accMax 的 20% 限幅、5 ms 平滑、20 krad/s³ 斜率限幅，
                // 并在上一拍 PD 已饱和且前馈反向抵消时归零，因此不改变 LQR 的小信号稳定性。
                if (disturbanceCompensation != 0.0 && ControlMath.IsFinite(disturbanceCompensation))
                {
                    aCmdRaw += disturbanceCompensation;
                    if (!ControlMath.IsFinite(aCmdRaw)) aCmdRaw = 0.0;
                }

                // accMax / R22 交棒限幅已在本拍观测器之前算好（同一口径，供前馈权限使用）。
                // 单向速率削幅的上限 = 驱动器物理天花板（不再用软件自设的 40 rad/s）。
                // 它只禁止继续同向加速、绝不反向制动，所以放开到天花板不会制造任何额外风险。
                armRateMax = SafetyLimits.ArmDriveCeilingRadPerSec;

                aCmd = aCmdRaw;
                accSat = false;
                if (aCmd > accMax) { aCmd = accMax; accSat = true; }
                else if (aCmd < -accMax) { aCmd = -accMax; accSat = true; }

                // 单向速率削幅：只禁止继续同向加速，绝不反向制动。
                // 反向制动会把臂速锁死在 rateMax，aCmd 归零，控制权限再次消失 = 死锁换了个位置。
                rateClipped = false;
                if (phiDot >= armRateMax && aCmd > 0.0) { aCmd = 0.0; rateClipped = true; }
                else if (phiDot <= -armRateMax && aCmd < 0.0) { aCmd = 0.0; rateClipped = true; }

                // 参考速度是 aCmd 的自积分，不是「实测臂速 + 9 ms 前馈」。
                // 回中与平衡反馈先在加速度域仲裁，随后只经这一个积分器生成位置参考。
                if (_balanceFirstControlCycle)
                {
                    // 交棒第一拍保持入口 prime，不额外叠加 aCmd·dt，保证 φ̇_ref 与 φ̇_filt 连续。
                    _balanceFirstControlCycle = false;
                }
                else
                {
                    _balancePhiDotCmd += aCmd * _dtSec;
                }
                // 参考速度抗饱和：S2 预算 0.8×0.25/0.009 ≈ 22.2 rad/s，取 ±22。
                _balancePhiDotCmd = ControlMath.Clamp(_balancePhiDotCmd,
                    -BalancePhiDotRefMaxRadPerSec, BalancePhiDotRefMaxRadPerSec);
                // **不再做双向速度钳位**：2026-11-21 R3 台架实证，`|v−φ̇_meas|≤3` 在 PD 需要
                // 反向拉臂时把 v 夹在 φ̇_meas−3（aCmd=+319 却只能给到 −2.0），回中 −3.38 继续对抗，
                // 摆杆从 |ψ|=0.018 漂到 0.099 后失稳。速度参考必须允许反向；积分器抗饱和改在
                // 位置域做（下面 cmdNext 的 ±BalanceRecenterPositionLeadRad），不掐速度。
                uCmdEff = _balancePhiDotCmd;

                double referenceRate = uCmdEff;
                // rcRate 是慢速臂参考 r 的移动速度；原四增益 PD 始终在线，参考积分只改变稳态参考点。
                if (recentering)
                {
                    referenceRate = ControlMath.Clamp(referenceRate,
                        -BalancePhiDotRefMaxRadPerSec, BalancePhiDotRefMaxRadPerSec);

                    long nowTicks = Stopwatch.GetTimestamp();
                    if (_balanceRecenterLogTicks == 0 ||
                        nowTicks - _balanceRecenterLogTicks >= Stopwatch.Frequency / 2)
                    {
                        _balanceRecenterLogTicks = nowTicks;
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "[BALANCE] recenter-bias 回中推进：φ_ref={0:F3}、φ_act={1:F3}、中点={2:F3}、"
                            + "实际偏差={3:F3} rad、实际位移={4:F3} rad、rcRate={5:F2}、"
                            + "referenceRate={6:F2}、uCmdEff={7:F2} rad/s、"
                            + "|ψ|={8:F4}、|ω_est|={9:F3}、rcRef={10:F3}、yield={11}、"
                            + "frozen={12}、dHat={13:F2} rad/s²。",
                            _balanceCmdRad, phi, _balanceRecenterCenterRad,
                            _balanceRecenterCenterRad - phi, phi - _balanceRecenterStartRad,
                            _balanceRecenterRate, referenceRate, uCmdEff,
                            Math.Abs(thetaFromUpright), Math.Abs(omegaControl),
                            _balanceRecenterReferenceRad, yieldToBalance ? 1 : 0,
                            _balanceRecenterFrozen,
                            _disturbanceObserver.DisturbanceHatRadPerSec2));
                    }
                }
                else
                {
                    _balanceRecenterRate = 0.0;
                    _balanceRecenterLogTicks = 0;
                }

                double previousCmd = _balanceCmdRad;
                double requestedCmdNext = previousCmd + referenceRate * _dtSec;
                double cmdNext = requestedCmdNext;
                // 回中位置域 anti-windup：参考位置不得超前实测 φ 超过 0.12 rad。
                // 旧参考已落在窗外时，用既有 ±22 rad/s 预算逐拍拉回，绝不把“清旧 lead”的一次性
                // 位置差当成单拍速度冲量。最终 applied rate 会在所有位置钳位后统一回写。
                if (recentering)
                {
                    double leadMin = phi - BalanceRecenterPositionLeadRad;
                    double leadMax = phi + BalanceRecenterPositionLeadRad;
                    double leadMaxStep = BalancePhiDotRefMaxRadPerSec * _dtSec;
                    if (cmdNext > leadMax)
                        cmdNext = previousCmd +
                            ControlMath.Clamp(leadMax - previousCmd, -leadMaxStep, leadMaxStep);
                    else if (cmdNext < leadMin)
                        cmdNext = previousCmd +
                            ControlMath.Clamp(leadMin - previousCmd, -leadMaxStep, leadMaxStep);
                }
                positionAuthorityLost = false;
                if (cmdNext > limitMaxRad)
                {
                    positionAuthorityLost = referenceRate > 0.0;
                    cmdNext = limitMaxRad;
                    if (_balancePhiDotCmd > 0.0) _balancePhiDotCmd = 0.0;
                }
                else if (cmdNext < limitMinRad)
                {
                    positionAuthorityLost = referenceRate < 0.0;
                    cmdNext = limitMinRad;
                    if (_balancePhiDotCmd < 0.0) _balancePhiDotCmd = 0.0;
                }
                if (recentering && cmdNext != requestedCmdNext)
                {
                    // 位置参考以最终钳位后的增量为准；速度积分器与遥测同步到 applied rate，
                    // 并且仍受既有 ±22 rad/s 参考速度包线约束，不允许本拍冲量越界。
                    double appliedRate = ControlMath.Clamp(
                        (cmdNext - previousCmd) / _dtSec,
                        -BalancePhiDotRefMaxRadPerSec, BalancePhiDotRefMaxRadPerSec);
                    referenceRate = appliedRate;
                    _balancePhiDotCmd = appliedRate;
                    uCmdEff = appliedRate;
                }
                _balanceCmdRad = cmdNext;
                _phiRef = _balanceCmdRad;
                _phiDotRef = referenceRate;
                balanceReferenceRate = referenceRate;

                if (recentering)
                {
                    double centerError = _balanceRecenterCenterRad - phi;
                    if (!yieldToBalance && Math.Abs(centerError) <= doneRad &&
                        Math.Abs(omegaControl) <= BalanceRecenterExitOmegaRadPerSec)
                        _balanceRecenterDoneSec += _dtSec;
                    else
                        _balanceRecenterDoneSec = 0.0;

                    // 到达既有完成带后进入中点保持，而不是退出回中恢复 Kφ<0 的原臂位置项；
                    // 后者会再次把臂推离中点（R24 111137：到 0 后漂回 3.77 rad）。
                    // 保持仍由同一外环闭环实测 φ/φ̇，完成事件只发一次。
                    if (_balanceRecenterActive == 1 && _balanceRecenterDoneSec >= BalanceRecenterDoneSeconds)
                    {
                        _balanceRecenterActive = 2;
                        _balanceRecenterRate = 0.0;
                        _balanceRecenterDoneSec = 0.0;
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "[BALANCE] 回中完成：φ={0:F4} rad、中心={1:F4}、误差={2:F4} rad（带 ±{3:F4}），"
                            + "|ψ|={4:F4}、|ω_est|={5:F3} rad/s，已保持 {6:F2} s；外环继续闭环实测位置。",
                            phi, _balanceRecenterCenterRad, centerError, doneRad,
                            Math.Abs(thetaFromUpright), Math.Abs(omegaControl),
                            BalanceRecenterDoneSeconds));
                    }
                }

                // 失去控制权：加速度命令顶死且参考顶到 counts 软限位，|ψ| 还在长 = 真故障。
                // 单向速率削幅本身不直接判失权（它不是位置/加速度权限耗尽）。
                if (accSat && positionAuthorityLost)
                {
                    double psiAbs = Math.Abs(thetaFromUpright);
                    if (_balanceAuthorityLostCycles == 0)
                        _balanceAuthorityLostPsiStart = psiAbs;
                    _balanceAuthorityLostCycles++;
                    if (_balanceAuthorityLostCycles >= BalanceAuthorityLostTripCycles &&
                        psiAbs > _balanceAuthorityLostPsiStart + BalanceAuthorityLostGrowthRad)
                        _balanceAuthorityLostTripped = true;
                }
                else
                {
                    _balanceAuthorityLostCycles = 0;
                }

                phiTarget = ControlMath.Clamp(_balanceCmdRad, limitMinRad, limitMaxRad);
                saturated = accSat;

                // 遥测在最终 0x607A 目标算完后统一发射；这里先锁住本拍时间，避免下面的计时递增让 t 偏一拍。
                balanceTelemetrySec = _balanceEnterSec;

                // 本拍真正下发的摆臂加速度与权限标志，供下一拍扰动观测器做模型预测。
                // 必须在 aCmd 经过 accMax / 单向速率削幅之后取，不能拿 aCmdRaw。
                _balanceLastAppliedAccelRadPerSec2 = aCmd;
                _balanceLastAccSat = accSat;
                _balanceLastRateClipped = rateClipped;
                _balanceLastAuthorityLost = positionAuthorityLost;

                _balanceEnterSec += _dtSec;
            }

            else
            {
                // 兜底：Stopped / Fault / Disconnected 已经在分派那层拦住，走到这里就保持不动。
                phiTarget = phi;
                _phiRef = phi;
            }

            // 软限位只约束**自动运动**（起摆 / 平衡）；手动点动与停机态不钳位。
            // 现场必须先用点动把摆臂开到左 / 右边界，才能把边界采集进软限位参数 ——
            // 如果点动本身也被旧限位钳住，用户永远走不出出厂占位区间，也就采不出真实行程。
            // 自动运动仍按限位钳位：那是保护机构不被自动流程带出已标定行程的边界。
            // 这里的弧度钳位只是内部参考的等价边界；**权威钳位在下面 counts 域**（0x607A 目标）。
            bool nonBalanceAutomatic = state == EngineState.Enabled &&
                (Volatile.Read(ref _radiusCalActive) != 0 || Volatile.Read(ref _returnHomeActive) != 0);
            bool automaticMotion = MotionClampsToSoftLimit(state) || nonBalanceAutomatic;
            if (automaticMotion)
                phiTarget = ControlMath.Clamp(phiTarget, limitMinRad, limitMaxRad);

            // slew 阶跃护栏，堵死"目标阶跃过大 → 驱动报 0x7500"。
            // 驱动侧的 0x8403「PWM 周期内脉冲增量过大」也是同一件事，被这条护栏天然防住
            //（手册给的处理措施就是"降低加速度"，这里等价于 ω_arm_max·dt 的每拍增量上限）。
            // 这是**速度护栏**（保留），不是软限位：软限位只管行程边界，管不住阶跃速度。
            if (!_phiTargetPrimed)
            {
                _phiTargetPrimed = true;
                _phiTargetPrev = phiTarget;
            }
            // 护栏必须永远有有限值，否则 maxStep 变 NaN，Slew 会把目标算成 NaN 再写进 0x607A。
            // **下限 = 驱动器 0x6080 的物理天花板**（2026-11-21）：配置值低于它时按物理天花板执行 ——
            // 小于驱动器能力的软件限速没有物理依据（台架实测臂峰值 98.7 rad/s、驱动器零故障），
            // 而且起摆泵一旦被它钳住就会把交变刹车整流成单向推力。参数里的旧值由 ParamStore 迁移。
            // 配置只可作为更严的上限；任何配置都不得超过驱动器 0x6080 的物理天花板。
            double configuredArmRate = _limitsActive.OmegaArmMaxRadPerSec;
            if (!(configuredArmRate > 0.0) || !ControlMath.IsFinite(configuredArmRate))
                configuredArmRate = SafetyLimits.ArmDriveCeilingRadPerSec;
            double omegaArmLimit = Math.Min(configuredArmRate, SafetyLimits.ArmDriveCeilingRadPerSec);
            if (configuredArmRate > SafetyLimits.ArmDriveCeilingRadPerSec &&
                Interlocked.Exchange(ref _omegaArmLimitClampReported, 1) == 0)
            {
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[BALANCE] OmegaArmMax={0:F3} rad/s 超过驱动器 0x6080 天花板，按 {1:F3} rad/s 执行。",
                    configuredArmRate, SafetyLimits.ArmDriveCeilingRadPerSec));
            }

            // 平衡段不另设「误差窗÷滞后」速度上限：反向制动只由 aCmd 权限、软限位、
            // FollowingError 如实上报与驱动器 0x6080 天花板约束。

            // ---- 非平衡自动运动的限速 + 加速度斜坡 + 速度前馈（2026-11-21 第九/十四/二十三轮台架）----
            // 第九轮 recenter：实际约 48 rad/s 时稳态滞后≈0.3 rad > PhiErrMaxRad=0.25，按 FollowingError 切 Fault。
            // 第十四轮 [MOTION] 实测无前馈滞后：32.57 rad/s→0.306 rad、34.05 rad/s→0.308 rad ⇒ ≈9 ms。
            // **第二十三轮评估结论（为什么这一道保留）**：它不是软件自设的「最大速度」，而是
            // S2 跟随误差窗口的直接换算：v < 0.8·PhiErrMaxRad / 实测滞后 = 0.8×0.25/9 ms ≈ 22.2 rad/s。
            // S2 是保留的安全判据（实际跟不上要如实报 Fault），所以这道限速是它的前置预算，
            // 不能删；删了以后 recenter 只要超过 27.8 rad/s，400 ms 后必然按 FollowingError 停机。
            // 它只作用于回中 / 标定 / 点动（nonBalanceAutomatic），**不作用于起摆与平衡**：
            // 起摆的臂速由能量环自己决定；平衡由 aCmd 自积分 + 0x6080 天花板决定。
            // 同时按 AccMax 把目标速度从 0 斜坡拉起，并给目标叠 φ̇_ref·9 ms 的超前（速度前馈，
            // 与平衡路径同一思路），把 9 ms 的稳态滞后补掉；超前量随斜坡速度连续归零，启停无阶跃。
            double motionErrWindow = _limitsActive.PhiErrMaxRad > 0 && ControlMath.IsFinite(_limitsActive.PhiErrMaxRad)
                ? _limitsActive.PhiErrMaxRad
                : SafetyLimits.CreateDefault().PhiErrMaxRad;
            double motionSpeedLimit = omegaArmLimit;
            if (nonBalanceAutomatic)
            {
                double safeSpeed = TrackingSpeedLimit(
                    motionErrWindow, NonBalanceFollowingErrorMarginRatio, MotionTrackingLagSeconds);
                if (ControlMath.IsFinite(safeSpeed) && safeSpeed > 0 && safeSpeed < motionSpeedLimit)
                    motionSpeedLimit = safeSpeed;
            }
            // 斜坡本身保留（它不是速度上限）：① 0x607A 的逐拍阶跃必须受控，否则驱动报
            // 0x8403「PWM 周期内脉冲增量过大」；② 速度前馈需要一条连续的速度量来乘 9 ms。
            // 只作用于回中 / 标定 / 点动；起摆与平衡走上面的 omegaArmLimit 路径。
            double maxStep;
            _motionAccLimit = 0.0;
            if (nonBalanceAutomatic)
            {
                double dir = phiTarget > _phiTargetPrev ? 1.0 : (phiTarget < _phiTargetPrev ? -1.0 : 0.0);
                double desiredSpeed = dir * motionSpeedLimit;
                _motionAccLimit = _pidActive.AccMax > 0 && ControlMath.IsFinite(_pidActive.AccMax)
                    ? _pidActive.AccMax
                    : PidGains.CreateAccelerationLawDefaults().AccMax;
                _motionSlewSpeed = ControlMath.Slew(desiredSpeed, _motionSlewSpeed, _motionAccLimit * _dtSec);
                maxStep = Math.Abs(_motionSlewSpeed) * _dtSec;
            }
            else
            {
                _motionSlewSpeed = 0;
                _motionLogTicks = 0;
                maxStep = omegaArmLimit * _dtSec;
            }
            phiTarget = ControlMath.Slew(phiTarget, _phiTargetPrev, maxStep);
            _phiTargetPrev = phiTarget;

            // 速度前馈：目标 = 基准 + φ̇_ref·9 ms。lead 只跟随斜坡速度，速度归零时同步归零。
            double motionLead = nonBalanceAutomatic
                ? _motionSlewSpeed * MotionVelocityFeedForwardSeconds
                : 0.0;
            double commandedTarget = phiTarget + motionLead;
            if (nonBalanceAutomatic)
                commandedTarget = ControlMath.Clamp(commandedTarget, limitMinRad, limitMaxRad);

            if (_countsPerRevArm <= 0) return armPos;

            double counts = _phiZeroCount + commandedTarget * _countsPerRevArm / ControlMath.TwoPi;
            long target = (long)Math.Round(counts, MidpointRounding.AwayFromZero);
            if (target > int.MaxValue) target = int.MaxValue;
            if (target < int.MinValue) target = int.MinValue;

            // **软限位的权威钳位就在 counts 域**：写进 0x607A 的目标绝不超出 [ArmMinCount, ArmMaxCount]。
            // 判定单位与参数单位完全一致（绝对 0x6064 计数），不经过任何角度 / 比例换算，
            // 所以 -54000 / +54000 这种行程原样生效，左右不对称也照样成立。
            if (automaticMotion)
            {
                if (target < _limitsActive.ArmMinCount) target = _limitsActive.ArmMinCount;
                else if (target > _limitsActive.ArmMaxCount) target = _limitsActive.ArmMaxCount;
            }

            // 每拍目标增量护栏：非平衡运动用**斜坡速度 + 前馈变化率**（不是 OmegaArmMax），
            // 否则前馈的超前量会被当成阶跃剪掉，等于白加。前馈变化率上界 = AccMax·T_ff。
            double countSpeedLimit = nonBalanceAutomatic
                ? Math.Abs(_motionSlewSpeed) + Math.Abs(_motionAccLimit) * MotionVelocityFeedForwardSeconds
                : omegaArmLimit;
            long maxCountStep = (long)Math.Ceiling(
                countSpeedLimit * _dtSec * _countsPerRevArm / ControlMath.TwoPi);
            if (maxCountStep < 1) maxCountStep = 1;
            long baseline = _lastTargetValid ? _lastTargetCount : armPos;
            long delta = target - baseline;
            if (delta > maxCountStep) target = baseline + maxCountStep;
            else if (delta < -maxCountStep) target = baseline - maxCountStep;

            // ---- 最终安全余量：所有状态无条件生效，0x607A 绝不进入软限位内侧 margin ----
            long safeMin, safeMax, safeMargin;
            if (TryGetSafeTravelBounds(out safeMin, out safeMax, out safeMargin))
            {
                if (target < safeMin)
                {
                    target = safeMin;
                    if (_balancePhiDotCmd < 0.0) _balancePhiDotCmd = 0.0;
                }
                else if (target > safeMax)
                {
                    target = safeMax;
                    if (_balancePhiDotCmd > 0.0) _balancePhiDotCmd = 0.0;
                }

                if (state == EngineState.Balancing)
                {
                    // 方向性 guard：实测已在右余量区时，只有目标/参考仍继续向外才判触边；
                    // target 指向中心（target < 实测）时允许把臂拉回来，不因"身处余量区"直接断使能。
                    bool beyondRight = armPos > safeMax;
                    bool beyondLeft = armPos < safeMin;
                    bool outwardRight = balanceReferenceRate > 0.0;
                    bool outwardLeft = balanceReferenceRate < 0.0;
                    bool commandOutward;
                    if (beyondRight)
                        commandOutward = target >= armPos || (target >= safeMax && outwardRight);
                    else if (beyondLeft)
                        commandOutward = target <= armPos || (target <= safeMin && outwardLeft);
                    else
                        commandOutward = (target >= safeMax && outwardRight) ||
                                         (target <= safeMin && outwardLeft);

                    if (commandOutward)
                    {
                        _balanceTravelGuardCycles++;
                        if (_balanceTravelGuardCycles >= BalanceTravelGuardTripCycles &&
                            !_balanceTravelGuardTripped)
                        {
                            _balanceTravelGuardTripped = true;
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[BALANCE] travel-guard: actual={0} target={1} safe=[{2},{3}] counts"
                                + "（margin={4}，refRate={5:F2}）；目标/命令仍向外，按 BalanceLost 受控停机。",
                                armPos, target, safeMin, safeMax, safeMargin, balanceReferenceRate));
                            // 本拍不再向同向推进；调用方看到 trip 后会断使能并把目标钉到实测位置。
                            target = armPos;
                        }
                    }
                    else
                    {
                        _balanceTravelGuardCycles = 0;
                    }
                }
            }

            // ---- 接管后 2 s 逐拍遥测（100 Hz）----
            // φ_tgt 由最终 target 反算：包含弧度 slew、counts 取整、软限位与 counts 增量护栏，
            // 就是本拍写进 0x607A 的目标；rateMax 是本拍真正传给 Slew 的 omegaArmLimit，不再打配置常量 40。
            // 其余字段保持原口径：平衡律指令链 + 实测链 + 冻结与饱和标志；t 用分支内提前锁住的本拍时间。
            if (state == EngineState.Balancing &&
                balanceTelemetrySec < BalanceTelemetryTailSeconds &&
                balanceTelemetrySec >= _balanceTelemetryNextSec)
            {
                bool headWindow = balanceTelemetrySec < BalanceTelemetrySeconds;
                _balanceTelemetryNextSec = balanceTelemetrySec
                    + (headWindow ? BalanceTelemetryIntervalSeconds : BalanceTelemetryTailIntervalSeconds);
                double finalTargetRad = (target - _phiZeroCount) * ControlMath.TwoPi / _countsPerRevArm;
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[BALANCE] t={0:F3}s ψ={1:F4} ω={2:F2} ω_est={3:F2} φ={4:F3} φ̇={5:F2} 0x6064={6} "
                    + "aCmdRaw={7:F1} aCmd={8:F1} rc={9} φ_ref={10:F3} φ̇_ref={11:F2} φ_tgt={12:F3} "
                    + "satA={13} satRate={14} satPos={15} accMax={16:F0} rateMax={17:F1} "
                    + "rcCenter={18:F3} rcRate={19:F2} uCmdEff={20:F1} ωResid={21:F4} posTerm={22:F1} "
                    + "ω_used={23:F2} ho={24} dHat={25:F2} dRes={26:F2} dConf={27:F2} "
                    + "margin={28} freeze={29} ff={30:F1} dBw={31:F1} aArm={32:F1}",
                    balanceTelemetrySec, thetaFromUpright, omega,
                    ControlMath.IsFinite(_balanceOmegaEstimateRadPerSec) ? _balanceOmegaEstimateRadPerSec : omega,
                    phi, phiDot,
                    ArmCountFromRad(phi), aCmdRaw, aCmd,
                    balanceRecentering ? 1 : 0, _phiRef, _phiDotRef, finalTargetRad,
                    accSat ? 1 : 0, rateClipped ? 1 : 0, positionAuthorityLost ? 1 : 0,
                    accMax, omegaArmLimit, _balanceRecenterCenterRad, _balanceRecenterRate,
                    uCmdEff, _balanceOmegaResidualRad, balancePosTerm,
                    _balanceOmegaUsedRadPerSec,
                    _balanceOmegaHandoffCycles < BalanceOmegaHandoffBlendCycles ? 1 : 0,
                    _disturbanceObserver.DisturbanceHatRadPerSec2,
                    _disturbanceObserver.ResidualRadPerSec2,
                    _disturbanceObserver.Confidence,
                    (int)_disturbanceObserver.MarginState,
                    _balanceRecenterFrozen,
                    _disturbanceObserver.CompensationRadPerSec2,
                    DisturbanceObserver.BandwidthHz,
                    _disturbanceObserver.ArmAccelModelRadPerSec2));
            }

            // 非平衡运动诊断（20 Hz 限流）：φ/φ̇/0x6064/0x607A/剩余/速度限/斜坡速度/前馈/跟踪差，
            // 下一次超速或 FollowingError 时能直接看是哪一段。
            if (nonBalanceAutomatic)
            {
                long motionNow = Stopwatch.GetTimestamp();
                if (_motionLogTicks == 0 || motionNow - _motionLogTicks >= Stopwatch.Frequency / 20)
                {
                    _motionLogTicks = motionNow;
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "[MOTION] {0}：φ={1:F3} rad φ̇={2:F2} rad/s 0x6064={3} 0x607A目标={4} 剩余={5} counts；"
                        + "速度限={6:F1} rad/s（0x607A 天花板={7:F1}、误差窗口={8:F3} rad、伺服滞后={9:F1} ms）；"
                        + "斜坡速度={10:F1} rad/s 前馈={12:F3} rad 跟踪差={11:F3} rad。",
                        Volatile.Read(ref _radiusCalActive) != 0 ? "半径标定" : "回中",
                        phi, phiDot, armPos, target, target - (long)armPos,
                        motionSpeedLimit, omegaArmLimit, motionErrWindow,
                        MotionTrackingLagSeconds * 1000.0, _motionSlewSpeed,
                        commandedTarget - phi, motionLead));
                }
            }

            if (state == EngineState.Enabled && Volatile.Read(ref _returnHomeActive) != 0)
                UpdateReturnHomeProgress(armPos, target);
            return (int)target;
        }

        /// <summary>
        /// 回零完成 / 超时判定。完成 = 本拍目标与实际位置都落在标定零点 ±3 counts 并连续保持 0.1 s；
        /// 超时只取消回零、把目标冻结在当前已走到的位置，不切故障、不移动标定。
        /// </summary>
        private void UpdateReturnHomeProgress(int armPos, long targetCount)
        {
            if (Volatile.Read(ref _returnHomeActive) == 0)
                return;

            int zeroCount = Volatile.Read(ref _phiZeroCount);
            int targetNow = Volatile.Read(ref _returnHomeTargetCount);
            long targetError = Math.Abs(targetCount - targetNow);
            long actualError = Math.Abs((long)armPos - targetNow);
            if (targetError <= ReturnHomeToleranceCounts && actualError <= ReturnHomeToleranceCounts)
            {
                int held = Volatile.Read(ref _returnHomeHoldCycles) + 1;
                Volatile.Write(ref _returnHomeHoldCycles, held);
                if (held >= ReturnHomeHoldCycles)
                {
                    // 「回到中心并重新锁零」：到位后把实际位置重新锁成 φ=0（残差 ≤ 3 counts）。
                    // 这是用户点名的恢复入口：漂到边缘 → 一键回中 → 新中心就落在回来后的实际位置上。
                    // 软限位是绝对 counts，不随零点搬动；这里只平移内部 φ 坐标系与所有指令锚点。
                    int relockCount = armPos;
                    _phiZeroCount = relockCount;
                    Volatile.Write(ref _armZeroOverride, relockCount);
                    Volatile.Write(ref _armZeroOverrideSet, 1);
                    Volatile.Write(ref _armZeroCalibrated, 1);
                    Volatile.Write(ref _calibrationDirty, 1);
                    _phiPrev = 0;
                    _phiDotFilt = 0;
                    _phiTargetPrimed = true;
                    _phiTargetPrev = 0;
                    _lastTargetValid = false;
                                        _phiRef = ArmHomeRad;
                    Volatile.Write(ref _returnHomeActive, 0);
                    Volatile.Write(ref _returnHomeHoldCycles, 0);
                    _returnHomeStartTicks = 0;
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "回中完成并重新锁零：0x6064={0} counts 现在是 φ=0（原 0 点 {1}，回中目标 {2}，残差 {3} counts）。" +
                        "位置标定已标记为「待保存」：save-config 落盘后下次进程启动才不会判标定不一致。",
                        relockCount, zeroCount, targetNow, actualError));
                    return;
                }
            }
            else
            {
                Volatile.Write(ref _returnHomeHoldCycles, 0);
            }

            if (_returnHomeStartTicks != 0 &&
                Stopwatch.GetTimestamp() - _returnHomeStartTicks >
                (long)(ReturnHomeTimeoutSeconds * Stopwatch.Frequency))
            {
                Volatile.Write(ref _returnHomeActive, 0);
                Volatile.Write(ref _returnHomeHoldCycles, 0);
                _returnHomeStartTicks = 0;
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "回中超时（{0:F0} s）：当前位置 0x6064={1}，回中目标 raw={2}，还差 {3} counts。" +
                    "已冻结当前目标，不再继续驱动；请检查是否被机械挡住或零点 / 限位是否正确。",
                    ReturnHomeTimeoutSeconds, armPos, targetNow, actualError));
            }
        }

        /// <summary>
        /// 受控停 QuickStop 路径状态机（仅周期线程调用），返回本拍控制字。
        /// 0x0002 Quick Stop 按 0x6085 急停减速度斜坡减速 → 确认 QuickStopActive（0x6F == 0x07）→ 0x0000 去使能。
        /// 等待有界（StopConfirmBudgetSeconds，2 s）：超时 / 故障 / 未就绪直接收尾，绝不卡死在中间态。
        /// 全程目标钉在实际位置上。离开 QuickStopActive 只允许 0x0000（Disable Voltage）或 0x000F
        /// （Enable Operation）——0x0006 Shutdown 不是该态的合法迁移，绝不拿它收尾。
        /// 收尾那拍照发 0x0000，下一拍起仍由 Stopped 常态分支发 0x0000。
        /// </summary>
        private ushort StepControlledStop(ushort statusWord)
        {
            if (_stopPhase == 1)
            {
                if (CiA402.IsQuickStopActive(statusWord))
                {
                    _stopPhase = 2;
                }
                else if (CiA402.IsFault(statusWord) || (statusWord & CiA402.SwMaskFault) == 0x00)
                {
                    // 故障 / Not Ready To Switch On：斜坡无从谈起，直接收尾。
                    _stopPhase = 2;
                }
                else if (--_stopPhaseCyclesLeft <= 0)
                {
                    _stopPhase = 2;
                    EnqueueDiagnostic(string.Format(
                        "受控停等待 QuickStopActive 超时（{0:F0} s），收尾 0x0000 去使能。状态字=0x{1:X4}。",
                        StopConfirmBudgetSeconds, statusWord));
                }
                else
                {
                    return CiA402.CwQuickStop;
                }
            }

            // 收尾：0x0000 去使能（QuickStopActive 的合法出口），回 Stopped；本拍照发 0x0000，下一拍起常态 0x0000。
            _stopPhase = 0;
            _stopPhaseCyclesLeft = 0;
            FinishStop();
            EnqueueDiagnostic("受控停完成：QuickStopActive 路径走完，0x0000 去使能，回 Stopped。");
            return CiA402.CwDisableVoltage;
        }

        /// <summary>受控停 / 直接收尾共用的落位：清锁存、清控制器状态、回 Stopped。</summary>
        private void FinishStop()
        {
            // 停止 / 收尾时若有正在记录的整圈捕获，先落一条记录再清状态（不漏账）。
            FinishCaptureAttempt(Stopwatch.GetTimestamp());
            _enablePhase = PhaseIdle;
            _pendingMotion = 0;
            Volatile.Write(ref _jogDirection, 0);
            Volatile.Write(ref _returnHomeActive, 0);
            Volatile.Write(ref _returnHomeHoldCycles, 0);
            _returnHomeStartTicks = 0;
            // 收尾后驱动可能还挂 1~2 拍 QuickStopActive（我们自己送进去的），给 S11 一段抑制余量。
            // 只随时间衰减（周期线程每拍 -1），任何路径都不许提前清零——清零会让残余快停态被误读成硬急停。
            _stopGraceCycles = StopFinishGraceCycles;
            _swing.Reset();
            ResetRadiusCalibration();
            Interlocked.Exchange(ref _pendingReturnHomeTargetValid, 0);
            // 平衡失控判据的累计随停机清零：下次进平衡重新数，不继承上一段。
            _balanceAuthorityLostCycles = 0;
            _balanceAuthorityLostTripped = false;
            Volatile.Write(ref _eStopLatch, 0);
            Volatile.Write(ref _faultCode, (int)FaultCode.None);
            SetState(EngineState.Stopped);
        }

        /// <summary>
        /// 整圈捕获收尾：取记录 → 进辨识历史 → 入队 CaptureCompleted（字符串在 PumpEvents 慢路径拼）。
        /// 失效记录同时写一条带原因的诊断（有效不写，避免刷屏）。没有可落盘的记录时幂等返回 false。
        /// 仅周期线程调用。
        /// </summary>
        private bool FinishCaptureAttempt(long nowTicks)
        {
            CaptureRecord record;
            if (!_capture.EndAttempt(nowTicks, false, out record))
                return false;
            _identifier.AddCapture(record);
            EngineEvent captureEvent = new EngineEvent();
            captureEvent.Kind = EngineEventKind.CaptureCompleted;
            captureEvent.Capture = record;
            _events.Enqueue(captureEvent);
            if (!record.Valid && !string.IsNullOrEmpty(record.InvalidReason))
                EnqueueDiagnostic("整圈捕获无效：" + record.InvalidReason);
            return true;
        }

        /// <summary>
        /// 把平衡 PD 的位置/速度参考 re-prime 到**实测** φ / φ̇，并让下一拍跳过 aCmd·dt 累积。
        /// 入口交棒、回中完成、回中失稳退出三条路径共用这一份写法，保证「参考切回 PD」时无阶跃。
        /// <para>
        /// 必须与 <see cref="EnterBalancing(double, double, string)"/> 的 prime 口径一致：
        /// 速度积分器取 φ̇_filt（夹 ±22），位置参考取实测 φ，公共 slew 锚点同步刷新，
        /// 否则手轮 / 软限位护栏会拿上一起点的局部目标做基准而再产生一次阶跃。
        /// </para>
        /// </summary>
        private void ReprimeBalanceReference(double phi)
        {
            double prime = ControlMath.IsFinite(_phiDotFilt)
                ? ControlMath.Clamp(_phiDotFilt,
                    -BalancePhiDotRefMaxRadPerSec, BalancePhiDotRefMaxRadPerSec)
                : 0.0;
            _balancePhiDotCmd = prime;
            _balanceCmdRad = phi;
            // 回中外环 r 也锚到实测 φ：re-prime 之后位置项为 0，不会留下旧 r 偏置。
            // 入口交棒 / 回中 abort / 强扰动三条路径共用这一份写法，避免某条路径又把 r 置回 0。
            _balanceRecenterReferenceRad = phi;
            _balanceRecenterRate = 0.0;
            _phiRef = phi;
            _phiDotRef = prime;
            _phiTargetPrev = phi;
            _phiTargetPrimed = true;
            _lastTargetValid = false;
            // 下一拍不再叠加 aCmd·dt，保证 φ̇_ref 与实测 φ̇ 连续（与 EnterBalancing 同语义）。
            _balanceFirstControlCycle = true;
        }

        private void EnterBalancing(double thetaFromUpright, double phi)
        {
            EnterBalancing(thetaFromUpright, phi,
                "切换判据连续满足，已进平衡。纯摆杆 PD 立即接管；回中为连续位置参考，只在稳定时启用。");
        }

        private void EnterBalancing(double thetaFromUpright, double phi, string reason)
        {
            // 入口不锚最后目标：位置参考直接用实测 φ；速度 prime 也夹在 ±22 rad/s。
            // 与回中完成/退出的 re-prime 共用同一份写法（ReprimeBalanceReference），避免两处口径漂移。
            double balanceAnchorRad = phi;
            ReprimeBalanceReference(balanceAnchorRad);
            double balancePhiDotPrime = _balancePhiDotCmd;
            double balanceOmegaN2 = _limitsActive.JEq > 0 ? _limitsActive.Mgl / _limitsActive.JEq : 0;
            double balanceRadiusRatio = _limitsActive.RadiusRatio;
            _disturbanceObserver.Reset(balanceOmegaN2, balanceRadiusRatio);
            _balanceLastAppliedAccelRadPerSec2 = 0.0;
            _balanceLastAccSat = false;
            _balanceLastRateClipped = false;
            _balanceLastAuthorityLost = false;
            _balanceRecenterFrozen = 0;
            _lastDisturbanceMarginState = DisturbanceMarginState.Normal;
            _disturbanceLogTicks = 0;
            _balanceStableSec = 0.0;
            // 交棒 ω 混合只在本轮接管时启用；回中退出/完成的 re-prime 不再重置它。
            _balanceOmegaHandoffCycles = 0;
            _balanceRecenterActive = 0;
            _balanceRecenterRate = 0.0;
            _balanceRecenterStartRad = 0.0;
            _balanceRecenterCenterRad = 0.0;
            _balanceRecenterAbortCycles = 0;
            _balanceRecenterDoneSec = 0.0;
            _balanceEnterSec = 0;
            _balanceTelemetryNextSec = 0;
            // 失去控制权判据从零开始数，别把上一段平衡（或起摆）的累计带进来。
            _balanceAuthorityLostCycles = 0;
            _balanceAuthorityLostTripped = false;
            _balanceTravelGuardCycles = 0;
            _balanceTravelGuardTripped = false;
            // 接管瞬间快照：失稳诊断要能区分「控制器不行」与「捕获门放行了一个接不住的状态」。
            _balanceEntryPsi = thetaFromUpright;
            _balanceEntryOmega = ControlMath.IsFinite(_balanceOmegaEstimateRadPerSec)
                ? _balanceOmegaEstimateRadPerSec
                : (ControlMath.IsFinite(_omegaFilt) ? _omegaFilt : 0);
            _balanceEntryPhi = phi;
            _balanceEntryPhiDot = ControlMath.IsFinite(_phiDotFilt) ? _phiDotFilt : 0;
            // 倒立接管前立刻激活平衡 profile；aCmdRaw 只读当前 profile。
            _balanceActiveProfile = HeadBalanceProfile;
            // 起摆能量积分、折返偏置、相位与回中状态全部清零后再交棒。
            _swing.Reset();
            _interlock.ResetCounters();
            SetState(EngineState.Balancing);
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "[BALANCE] profile switch {0}: Ka={1:F1} Kω={2:F1} Kφ={3:F3} Kφdot={4:F2}",
                _balanceActiveProfile.Name,
                _balanceActiveProfile.KPsi,
                _balanceActiveProfile.KOmega,
                _balanceActiveProfile.KPhi,
                _balanceActiveProfile.KPhiDot));
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "[BALANCE] 纯 PD：aCmd=−(Kψψ+Kωω_est+Kφ·clamp(φ−中点,±3)+Kφ̇φ̇)；"
                + "φ̇_cmd 入口 prime={0:F2} rad/s（φ̇_filt），参考锚点={1:F3} rad（实测 φ），"
                + "此后只由 aCmd 自积分，accMax={2:F0} rad/s²。",
                balancePhiDotPrime, balanceAnchorRad,
                BalanceAccMaxEnvOverride > 0 ? BalanceAccMaxEnvOverride : BalanceAccMaxRadPerSec2));
            EnqueueDiagnostic(reason + string.Format(CultureInfo.InvariantCulture,
                " 接管状态：ψ0={0:F3} rad、ω_est0={1:F2} rad/s、φ0={2:F3} rad、φ̇0={3:F2} rad/s。",
                _balanceEntryPsi, _balanceEntryOmega, _balanceEntryPhi, _balanceEntryPhiDot));
            EnqueueDiagnostic(
                "[BALANCE] 接管后 2 s 逐拍遥测开启（100 Hz）：t ψ ω ω_est φ φ̇ 0x6064 "
                + "aCmdRaw aCmd rc φ_ref φ̇_ref φ_tgt satA satRate satPos accMax rateMax rcCenter rcRate uCmdEff ωResid；"
                + "φ̇_ref 由 aCmd 自积分；rc=1/2 表示原四增益 PD 在线，慢速臂参考向中点修正。");
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "[BALANCE] 扰动观测：ψ̈=(Mgl/JEq)·sinψ+(r/l)·aArm+d（aArm 用实测角加速度）；g/l={0:F2} s⁻²、r/l={1:F3}（{2}）；"
                + "带通 {3:F2}–{4:F1} Hz（高通 τ={5:F0} ms、低通 τ={6:F0} ms）；前馈 kff={7:F2}、"
                + "b_ff 下限={8:F2}、权限={9:F0} rad/s²、开关={10}；"
                + "margin 预扰动 |ψ|>{11:F3}/|ω|>{12:F2}/|dHat|>{13:F1}，"
                + "强扰动 |ψ|>{14:F3}/|ω|>{15:F2}/|dHat|>{16:F1}；"
                + "强扰动立即冻结回中并 re-prime 到实测 φ/φ̇，安静保持 {17:F2} s 后平滑恢复；"
                + "接管后 0.50 s 内 margin 只看不动作（R22 前 100 ms 250→800 不变）；"
                + "r/l<0.03 或未标定时回中参考保持不动。",
                balanceOmegaN2, balanceRadiusRatio,
                balanceRadiusRatio <= 0
                    ? "未标定，前馈关闭，仅保留裕度冻结"
                    : (balanceRadiusRatio < 0.10
                        ? "已标定但偏小，前馈分母按 0.20 保守"
                        : "已标定"),
                DisturbanceObserver.HighPassHz, DisturbanceObserver.BandwidthHz,
                DisturbanceObserver.HighPassTauSeconds * 1000.0,
                DisturbanceObserver.LowPassTauSeconds * 1000.0,
                DisturbanceObserver.FeedForwardGain,
                DisturbanceObserver.CompensationCouplingFloor,
                Math.Min(DisturbanceObserver.MaxCompensationRadPerSec2,
                    DisturbanceObserver.CompensationAuthorityRatio * BalanceAccMaxRadPerSec2),
                DisturbanceObserver.FeedForwardEnvironmentEnabled ? "开" : "关（FURUTA_DIST_FF）",
                DisturbanceObserver.PrePsiRad, DisturbanceObserver.PreOmegaRadPerSec,
                DisturbanceObserver.PreDisturbanceRadPerSec2,
                DisturbanceObserver.StrongPsiRad, DisturbanceObserver.StrongOmegaRadPerSec,
                DisturbanceObserver.StrongDisturbanceRadPerSec2,
                DisturbanceObserver.RecoveryHoldSeconds));
            if (!DisturbanceObserver.ObservationEnvironmentEnabled)
            {
                EnqueueDiagnostic(
                    "[BALANCE] 扰动观测总开关=关（未设 FURUTA_DIST_OBS=1）：不冻结回中、不注入前馈，"
                    + "回到已验证的纯 PD + 回中路径；观测器只发布中性快照。");
            }
            if (BalanceSimpleModeEnv > 0)
            {
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[BALANCE] 简化固定参数模式（FURUTA_BALANCE_SIMPLE）：禁用回中外环，"
                    + "四增益固定 K=[{0:F0},{1:F0},{2:F2},{3:F2}]，位置项参考行程中点。",
                    _balanceActiveProfile.KPsi, _balanceActiveProfile.KOmega,
                    _balanceActiveProfile.KPhi, _balanceActiveProfile.KPhiDot));
            }

            // 结构稳定性自检（r/l 已标定时）：ψ̈=(g/l)ψ+(r/l)φ̈，外环 aCmd=−Ka·ψ−Kad·ψ̇，
            // 闭环常数项 = (g/l) − (r/l)·Ka，必须 < 0。旧注释把判据写成「Ka > g/l」，漏了 r/l ——
            // 同一条 Ka 在 r 很小的机构上可能根本不够。这里按标定值如实报裕量；
            // r/l 未标定（0）时不猜，只提示无法判定。
            if (balanceRadiusRatio > 0 && balanceOmegaN2 > 0)
            {
                double closedLoopConstant = balanceOmegaN2 - balanceRadiusRatio * BalanceKPsi;
                if (closedLoopConstant >= 0)
                {
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "[BALANCE] 结构不稳定：r/l={0:F3}、Ka={1:F0} ⇒ (r/l)·Ka={2:F1} ≤ g/l={3:F1}，"
                        + "闭环常数项仍为正 —— 任何 Kad 都救不回来。Ka 至少要到 {4:F0}（= (g/l)/(r/l)）。",
                        balanceRadiusRatio, BalanceKPsi, balanceRadiusRatio * BalanceKPsi,
                        balanceOmegaN2, balanceOmegaN2 / balanceRadiusRatio));
                }
                else
                {
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "[BALANCE] 结构稳定裕量：(r/l)·Ka={0:F1} > g/l={1:F1}（Ka={2:F0}，r/l={3:F3}）。",
                        balanceRadiusRatio * BalanceKPsi, balanceOmegaN2, BalanceKPsi, balanceRadiusRatio));
                }
            }
            else
            {
                EnqueueDiagnostic(
                    "[BALANCE] r/l 未标定（limits.radius_ratio=0）：结构稳定判据 (r/l)·Ka > g/l 无法用实测值核对，"
                    + "以下任何 r/l 相关裕量数字都不可信；只能看接管后 |ψ| 是否收敛。"
                    + "量出臂轴到摆轴距离 r 后写进参数文件（优先 calibrate-radius 实测）即可恢复这条自检。");
            }
        }

        // ==================== 使能序列 ====================

        private ushort RunEnableSequence(ushort statusWord, ushort driveError, sbyte modeDisplay, int armPos,
            int armVelocityPulsePerSec, double theta, double phi, int pendulumCount, ref int targetCount)
        {
            targetCount = armPos;

            if (_enableTimeoutTicks > 0 && Stopwatch.GetTimestamp() - _enableTimeoutTicks >
                (long)(EnableTimeoutSeconds * Stopwatch.Frequency))
            {
                // 超时也要有话说：把门里实测的位置 / 漂移 / 速度 / 拍数全打出来，现场不用猜卡在哪。
                bool timedOutInGate = _enablePhase == PhaseNoMotionGate;
                int timeoutDrift = armPos - _enableGateAnchor;
                if (timeoutDrift < 0) timeoutDrift = -timeoutDrift;
                double timeoutSpeed = EnableGateSpeedCountsPerSec(armPos, armVelocityPulsePerSec);
                _enablePhase = PhaseIdle;
                _enableGateArmed = false;
                _pendingMotion = 0;
                SetState(EngineState.Connected);
                EnqueueDiagnostic(string.Format(
                    "使能子流程超时，驱动一直没进 Operation Enabled，保持去使能。状态字=0x{0:X4}，错误码=0x{1:X4}；" +
                    "实测 位置={2} 漂移={3}（阈值 {4}） 速度={5:F0} counts/s（0x606C={6} Pulse/s） ticks={7}/{8}，超时阶段={9}。",
                    statusWord, driveError, armPos, timeoutDrift, EnableGateDriftCounts,
                    timeoutSpeed, armVelocityPulsePerSec, _enableGateTicks, EnableGateCycles,
                    timedOutInGate ? "无运动门" : "CiA402"));
                return CiA402.CwDisableVoltage;
            }

            if (_enablePhase == PhaseNoMotionGate)
            {
                long gateNowTicks = Stopwatch.GetTimestamp();
                if (!_enableGateArmed)
                {
                    _enableGateArmed = true;
                    _enableGateTicks = 0;
                    _enableGateAnchor = armPos;
                    _enableGatePrevPos = armPos;
                    _enableGatePrevPosValid = false;
                    _enableGateLastLogTicks = gateNowTicks;
                }
                _enableGateTicks++;
                int drift = armPos - _enableGateAnchor;
                if (drift < 0) drift = -drift;
                // 位置差分速度是同一拍的独立证据（0x606C 按 Pulse/s 换算，应与 0x6064 的计数差分同量级；两路对照看）。
                double diffSpeed = _enableGatePrevPosValid ? (armPos - _enableGatePrevPos) / _dtSec : 0;
                double speed = EnableGateSpeedCountsPerSec(armPos, armVelocityPulsePerSec);
                _enableGatePrevPos = armPos;
                _enableGatePrevPosValid = true;

                // 100 ms 限频：现场能直接看出卡在哪（位置 / 漂移 / 速度 / 拍数）。
                if (gateNowTicks - _enableGateLastLogTicks >=
                    (long)(EnableGateLogIntervalSeconds * Stopwatch.Frequency))
                {
                    _enableGateLastLogTicks = gateNowTicks;
                    EnqueueDiagnostic(string.Format(
                        "无运动门: 位置={0} 漂移={1}（阈值 {2}） 速度={3:F0} counts/s（0x606C={4} Pulse/s） 位置差分速度={5:F0} counts/s ticks={6}/{7}",
                        armPos, drift, EnableGateDriftCounts, speed, armVelocityPulsePerSec, diffSpeed,
                        _enableGateTicks, EnableGateCycles));
                }

                if (drift > EnableGateDriftCounts)
                {
                    // 位置一漂就保持去使能，不硬上。
                    _enablePhase = PhaseIdle;
                    _enableGateArmed = false;
                    _pendingMotion = 0;
                    SetState(EngineState.Connected);
                    EnqueueDiagnostic(string.Format(
                        "无运动门失败：去使能期间摆臂漂了 {0} counts（允许 {1}），速度 {2:F0} counts/s（0x606C={3} Pulse/s），位置差分速度 {4:F0} counts/s，位置 {5}，ticks {6}/{7}，保持去使能。",
                        drift, EnableGateDriftCounts, speed, armVelocityPulsePerSec, diffSpeed, armPos,
                        _enableGateTicks, EnableGateCycles));
                    return CiA402.CwDisableVoltage;
                }
                if (_enableGateTicks >= EnableGateCycles)
                {
                    _enablePhase = PhaseDriveEnable;
                    _enableGateArmed = false;
                    EnqueueDiagnostic(string.Format(
                        "无运动门通过：位置={0} 漂移={1} 速度={2:F0} counts/s（0x606C={3} Pulse/s） 位置差分速度={4:F0} counts/s ticks={5}/{6}，开始 CiA402 使能序列 0x06→0x07→0x0F。",
                        armPos, drift, speed, armVelocityPulsePerSec, diffSpeed, _enableGateTicks, EnableGateCycles));
                }
                return CiA402.CwDisableVoltage;
            }

            // PhaseDriveEnable：按状态字一步步走，绝不一次写 0x0F。
            if (CiA402.IsFault(statusWord))
            {
                if (_faultResetTicks == 0)
                {
                    EnqueueDiagnostic(string.Format(
                        "驱动在故障态，按上升沿重复发故障复位。状态字=0x{0:X4}，错误码=0x{1:X4}。",
                        statusWord, driveError));
                }
                int period = FaultResetHighCycles + FaultResetLowCycles;
                int cycle = _faultResetTicks % period;
                _faultResetTicks++;
                if (cycle < FaultResetHighCycles)
                    return CiA402.CwFaultReset;
                return CiA402.CwDisableVoltage;
            }
            if (_faultResetTicks != 0)
            {
                _faultResetTicks = 0;
                _enableTimeoutTicks = Stopwatch.GetTimestamp();
                EnqueueDiagnostic(string.Format(
                    "故障位已清，重新计使能时限。状态字=0x{0:X4}。", statusWord));
            }

            if (CiA402.IsSwitchOnDisabled(statusWord)) return CiA402.CwShutdown;
            if (CiA402.IsReadyToSwitchOn(statusWord)) return CiA402.CwSwitchOn;
            if (CiA402.IsSwitchedOn(statusWord)) return CiA402.CwEnableOperation;
            if (CiA402.IsOperationEnabled(statusWord))
            {
                // 0x6061 回读必须 == 8（CSP）才允许进运动（04 §5E-6）：模式掉了就停 PLC、去使能、报差异，
                // 绝不降级 PP/PV 凑合跑。0x6060 每周期都写 8，回读对不上说明驱动器根本没进 CSP。
                if (modeDisplay != CiA402.ModeCsp)
                {
                    _enablePhase = PhaseIdle;
                    _enableGateArmed = false;
                    _pendingMotion = 0;
                    SetState(EngineState.Connected);
                    EnqueueDiagnostic(string.Format(
                        "0x6061 回读 = {0}，不是 CSP（{1}），拒绝完成使能并已去使能。请查驱动器运行模式与 ESI InitCmd（PS 迁移写 0x6060=08）。",
                        modeDisplay, CiA402.ModeCsp));
                    return CiA402.CwDisableVoltage;
                }
                _enablePhase = PhaseIdle;
                _interlock.ResetCounters();
                _swing.Reset();
                _capture.ResetSession();
                // 使能成功：只在**本会话第一次**使能时建立零点（两路基准同时归零，起始位置 ≡ 坐标 0）：
                // 摆杆走 ZeroPendulum() 同一条路径（GC1602 硬件复位位两拍 / GC1502 软件锁零点），
                // 摆臂把 0x6064 基准建在当前位置上。
                // 同一会话里再次使能（急停 / 停止后重新上电）**不再重建**：急停后摆杆停在任意角度，
                // 若每次都把当前位置定义成垂位，θ 参考、起摆能量式、切换判据全部错位。
                // GC1602 的硬件复位脉冲也一并跳过——否则 raw 被硬件清零而基准没跟着改，两者错配。
                bool calibrationCleared = Volatile.Read(ref _calibrationCleared) != 0;
                // 回中流程里的使能**必须保留旧中心**：`recenter` 从 Stopped 发起时先重新使能，
                // 如果这时按「每次使能重新锁零」把漂到的位置定义成中心，「回到中心」就会原地不动。
                bool keepArmZeroForReturnHome = _pendingMotion == PendingMotionReturnHome;
                if (!_zeroEstablished)
                {
                    if (calibrationCleared)
                    {
                        // 用户明确清空过位置标定：使能不替他把当前位置再定义成零点。
                        // 目标钉在当前 φ，保持不动；起摆 / 回零由各自入口拒绝，等人重新采集。
                        _phiTargetPrimed = true;
                        _phiTargetPrev = phi;
                                                _phiRef = phi;
                        _lastTargetValid = false;
                        Volatile.Write(ref _armZeroCalibrated, 0);
                        Volatile.Write(ref _pendulumZeroCalibrated, 0);
                        _zeroEstablished = true;
                        EnqueueDiagnostic("使能完成：标定已清空，本会话不自动建立零点，摆臂目标保持在当前 0x6064=" +
                            armPos + " counts。先停止并重新采集零点 / 左右限位（或规整化）后再起摆。");
                    }
                    else
                    {
                        // **每次使能都把当前 0x6064 锁成 φ=0**（2026-11-20 用户硬需求）：
                        // 现场的「中心」= 本次使能时摆臂所在的位置。旧版优先沿用用户设过的零点，
                        // 用户手动把摆臂挪回中心后再使能，坐标系仍锚在上一次的位置，新中心反而成了偏移。
                        // 软限位是绝对 counts，不随零点搬动，所以重新锁零不会移动行程边界。
                        // 例外：回中流程（keepArmZeroForReturnHome）必须保留旧中心，否则回中目标=当前位置。
                        bool keepSavedZero = keepArmZeroForReturnHome && Volatile.Read(ref _armZeroOverrideSet) != 0;
                        _phiZeroCount = keepSavedZero ? Volatile.Read(ref _armZeroOverride) : armPos;
                        Volatile.Write(ref _armZeroCalibrated, 1);
                        if (!keepSavedZero)
                        {
                            Volatile.Write(ref _armZeroOverride, armPos);
                            Volatile.Write(ref _armZeroOverrideSet, 1);
                            Volatile.Write(ref _calibrationDirty, 1);
                        }
                        // 摆杆零点同理：用户「设置摆杆零点（最底部）」指定过就直接采用那个绝对计数，
                        // **不发硬件复位脉冲**（GC1602 的复位会把计数器清掉，基准和计数就对不上）；
                        // 没设过才走原路径「复位一拍 → 把当前计数锁成 θ=0」。
                        bool pendulumZeroFromUser = Volatile.Read(ref _pendulumZeroOverrideSet) != 0;
                        if (pendulumZeroFromUser)
                        {
                            _countZeroRaw = unchecked((uint)Volatile.Read(ref _pendulumZeroOverride));
                            _zeroPulsePhase = 0;
                            _thetaPrev = 0;
                            _omegaFilt = 0;
                            Volatile.Write(ref _pendulumZeroCalibrated, 1);
                        }
                        else
                        {
                            _zeroPulsePhase = 2;
                        }
                        _phiPrev = 0;
                        _phiDotFilt = 0;
                        _phiTargetPrimed = false;
                                                _phiRef = ArmHomeRad;
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "使能完成，本会话首次使能建立零点：摆杆 θ=0 取{0}；摆臂 φ=0 取{1}。" +
                            "当前 0x6064={2}。摆杆零点应落在自然下垂位、摆臂零点应落在期望起始位——" +
                            "这两点软件无法验证，位置不对请重新「设置摆杆零点 / 设置 0 点」。",
                            pendulumZeroFromUser
                                ? "用户设定的摆杆零点 raw=" + Volatile.Read(ref _pendulumZeroOverride)
                                : "首次归零（复位后锁当前计数）",
                            keepSavedZero
                                ? "上次中心 raw=" + _phiZeroCount + "（回中流程保留旧中心）"
                                : "本次使能位置 " + armPos + "（每次使能重新锁零）",
                            armPos));
                    }
                }
                else
                {
                    // 零点沿用本会话已建立的基准：只重置指令锚点，不动换算基准。
                    // 位置按绝对 counts 报（φ 是内部坐标，日志不再引用摆臂角度）。
                    if (calibrationCleared)
                    {
                        // 清空后重新使能：目标仍钉在当前位，不往旧 raw=0 的方向动。
                        _phiTargetPrimed = true;
                        _phiTargetPrev = phi;
                        _lastTargetValid = false;
                                                _phiRef = phi;
                        EnqueueDiagnostic("使能完成：标定仍为空，摆臂目标保持在当前 0x6064=" + armPos +
                            " counts；重新采集 / 规整化前不自动运动。");
                    }
                    else if (keepArmZeroForReturnHome)
                    {
                        // 回中流程：保留旧中心（_phiZeroCount 不动），目标先锚在当前 φ，
                        // 使能完成后由 StartReturnHome 按限速朝旧中心走。这里若重新锁零，回中会原地不动。
                        _phiTargetPrimed = true;
                        _phiTargetPrev = phi;
                        _lastTargetValid = false;
                                                _phiRef = phi;
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "使能完成（回中流程）：保留摆臂中心 raw={0}，当前位置 0x6064={1}，接下来按限速回中。",
                            _phiZeroCount, armPos));
                    }
                    else
                    {
                        // **每次使能都重新锁定摆臂零点**（2026-11-20 用户硬需求：初始使能的地方就是中心）。
                        // 旧版沿用本会话第一次使能建立的零点：用户手动把摆臂挪回中心后再使能，
                        // 坐标系仍锚在上一次的位置，新中心被当成偏移，跨几轮运行就是「越来越偏离中心」。
                        // 摆杆基准不在这里重建：急停后摆杆可能停在任意角度，把它定义成垂位会让
                        // θ 参考 / 能量式 / 切换判据全部错位。
                        int previousArmZero = _phiZeroCount;
                        _phiZeroCount = armPos;
                        Volatile.Write(ref _armZeroOverride, armPos);
                        Volatile.Write(ref _armZeroOverrideSet, 1);
                        Volatile.Write(ref _armZeroCalibrated, 1);
                        Volatile.Write(ref _calibrationDirty, 1);
                        _phiPrev = 0;
                        _phiDotFilt = 0;
                        _phiTargetPrimed = false;
                        _lastTargetValid = false;
                                                _phiRef = ArmHomeRad;
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "使能完成：每次使能重新锁定摆臂零点 φ=0，本次 0x6064={0}（上次 0 点 {1}）；" +
                            "摆杆基准沿用本会话（θ={2:F3} rad），若不在自然下垂位请先「停止」再重新置零。",
                            armPos, previousArmZero, theta));
                        if (armPos < _limitsActive.ArmMinCount || armPos > _limitsActive.ArmMaxCount)
                        {
                            // 中心落在行程边界之外：自动运动会被限位钳住、起摆会被拒绝。
                            // 不阻断使能（人可能就在这个位置），但必须把恢复入口说清楚。
                            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                                "[LIMIT] 使能位置 0x6064={0} 在软限位 [{1},{2}] 之外：中心已锁在这里，" +
                                "起摆 / 回中会被限位拒绝。先点动到限位内，再 recenter（回到行程中点并居中）或 normalize。",
                                armPos, _limitsActive.ArmMinCount, _limitsActive.ArmMaxCount));
                        }
                    }
                }
                if (_pendingMotion == PendingMotionSwingUp)
                {
                    _pendingMotion = 0;
                    SetState(EngineState.Swinging);
                    ApplyIdentifiedPumpFrequency();
                    _swing.Begin(_swingActive);
                    _swingLogTicks = 0;
                    LogSwingUpModel();
                    EnqueueDiagnostic("使能完成，直接进起摆（全自动能量泵，从垂位甩到保持位，不需要人工扶摆）。");
                }
                else if (_pendingMotion == PendingMotionReturnHome)
                {
                    _pendingMotion = 0;
                    SetState(EngineState.Enabled);
                    StartReturnHome(phi, armPos);
                }
                else if (_pendingMotion == PendingMotionReleaseWait)
                {
                    _pendingMotion = 0;
                    SetState(EngineState.Enabled);
                    EnqueueDiagnostic("使能完成，直接进入整圈捕获等待（摆臂保持起始位置 0）。");
                    StartReleaseWait(Stopwatch.GetTimestamp(), theta, pendulumCount, ArmHomeRad, armPos);
                }
                else
                {
                    SetState(EngineState.Enabled);
                    EnqueueDiagnostic("使能完成，摆臂目标恒为起始位置 0，等待起摆指令。");
                }
                return CiA402.CwEnableOperation;
            }

            // Not Ready To Switch On：等驱动自己初始化完，不硬发命令字。
            return CiA402.CwDisableVoltage;
        }

        /// <summary>
        /// 无运动门诊断用的摆臂速度 [count/s]。0x606C 本身就是 Pulse/s = 驱动位置单位/秒，与 0x6064 的计数
        /// **同一口径**（CPR 只负责把计数换成角度，不改变计数单位），所以标定链给出每转位置单位后直接就是 counts/s：
        /// 经 <see cref="ControlMath.ArmRadPerSecFromDrivePulsePerSec"/> 折成 rad/s 再乘回来只是为了把换算链写在明处。
        /// 未标定时退回位置差分 / 固定周期 dt。只进日志，不参与放行判据。
        /// </summary>
        private double EnableGateSpeedCountsPerSec(int armPos, int armVelocityPulsePerSec)
        {
            if (_countsPerRevArm > 0)
            {
                double radPerSec = ControlMath.ArmRadPerSecFromDrivePulsePerSec(armVelocityPulsePerSec, _countsPerRevArm);
                return radPerSec * _countsPerRevArm / ControlMath.TwoPi;
            }
            if (_enableGatePrevPosValid) return (armPos - _enableGatePrevPos) / _dtSec;
            return 0;
        }

        // ==================== 命令应用 ====================

        private void ApplyCommand(
            int command, double theta, double thetaFromUpright, double omega,
            double phi, int armPos, int pendulumCount)
        {
            EngineState state = State;
            switch (command)
            {
                case CmdEnable:
                    if (IsEStopLatched)
                    {
                        RefuseCommand("enable", "急停锁存未清");
                        return;
                    }
                    if (Volatile.Read(ref _calibrationCleared) != 0)
                    {
                        RefuseCommand("enable", "位置标定已清空：先重新采集零点 / 左右限位或规整化");
                        return;
                    }
                    if (Volatile.Read(ref _calibrationReviewRequired) != 0)
                    {
                        RefuseCommand("enable", "位置标定待确认：先在界面上确认 / 规整化 / 重新采集");
                        return;
                    }
                    string poseSessionReason = PoseSessionReviewRejectText();
                    if (poseSessionReason != null)
                    {
                        RefuseCommand("enable", poseSessionReason);
                        return;
                    }
                    if (Volatile.Read(ref _calibrationReviewChecked) == 0)
                    {
                        RefuseCommand("enable", "首帧遥测未到，无法核对保存零点：等状态行出现采样值（或确认控制线程在跑）后重试");
                        return;
                    }
                    if (state != EngineState.Connected && state != EngineState.Stopped)
                    {
                        // 旧版这里直接 return，什么也不记：状态不是 Connected/Stopped 时现场只看到
                        // 「12 s 后仍是 X，期望 Enabled」。现在把实际状态写进拒绝原因。
                        RefuseCommand("enable", "当前状态 " + state + " 不能使能（只有 Connected / Stopped 可以）");
                        return;
                    }
                    _pendingMotion = 0;
                    StartEnable();
                    return;

                case CmdSwingUp:
                    if (IsEStopLatched)
                    {
                        RefuseCommand("swingup", "急停锁存未清，先 fault-reset 再起摆。");
                        return;
                    }
                    if (Volatile.Read(ref _calibrationReviewRequired) != 0)
                    {
                        RefuseCommand("swingup", "位置标定待确认：先确认 / 规整化 / 重新采集。");
                        return;
                    }
                    string swingPoseSessionReason = PoseSessionReviewRejectText();
                    if (swingPoseSessionReason != null)
                    {
                        EnqueueDiagnostic("起摆被拒绝：" + swingPoseSessionReason);
                        return;
                    }
                    if (state == EngineState.Enabled)
                    {
                        ApplyIdentifiedPumpFrequency();
                        _swing.Begin(_swingActive);
                        _interlock.ResetCounters();
                        _phiTargetPrimed = false;
                        SetState(EngineState.Swinging);
                        _swingLogTicks = 0;
                        LogSwingUpModel();
                        EnqueueDiagnostic("开始起摆（能量泵全自动，从垂位甩到保持位，不需要人工扶摆 / 助力）。");
                        return;
                    }
                    if (state == EngineState.Stopped)
                    {
                        // Stopped 里 0x6040 已经清零，必须先重新使能，再自动接起摆。
                        _pendingMotion = PendingMotionSwingUp;
                        StartEnable();
                        return;
                    }
                    RefuseCommand("swingup", "当前状态 " + state + " 不能起摆：先 connect 并 enable 到 Enabled（Stopped 会自动重新使能）；Fault 先 fault-reset。");
                    return;

                case CmdBalance:
                    if (state == EngineState.Swinging)
                    {
                        // 人工抢接只置请求位，判据统一在控制律里一拍判一次。
                        // 在这里再调一次 _swing.Step 会让起摆积分器一拍推进两遍。
                        _balanceRequest = true;
                        return;
                    }
                    if (state == EngineState.Balancing)
                    {
                        // 幂等确认：balance 命令在已平衡时只保持现状。
                        // 再调 EnterBalancing 会重置 PD 速度积分、回中调度与参考锚点，并在同一 2 ms 内
                        // 再打一条 [BALANCE] t=0；swingup 自动交棒后紧跟 balance --seconds 就会触发。
                        return;
                    }
                    RefuseCommand("balance", "当前状态 " + state + " 不能进平衡：先 swingup 起摆，或已使能后手动起摆到切换判据。");
                    return;

                case CmdReleaseCapture:
                    if (IsEStopLatched)
                    {
                        RefuseCommand("capture-release", "急停锁存未清，先 fault-reset 再捕获。");
                        return;
                    }
                    if (state == EngineState.Enabled)
                    {
                        StartReleaseWait(Stopwatch.GetTimestamp(), theta, pendulumCount, phi, armPos);
                        return;
                    }
                    if (state == EngineState.Stopped)
                    {
                        // Stopped 里 0x6040 已清零：先重新使能，使能完成后自动进等待释放。
                        _pendingMotion = PendingMotionReleaseWait;
                        StartEnable();
                        return;
                    }
                    RefuseCommand("capture-release", "当前状态 " + state + " 不能进入整圈捕获：先 enable 到 Enabled（Stopped 会自动重新使能）。");
                    return;

                case CmdCancelRelease:
                    if (_capture.IsCapturing)
                    {
                        CaptureRecord canceled;
                        if (_capture.EndAttempt(Stopwatch.GetTimestamp(), true, out canceled))
                        {
                            _identifier.AddCapture(canceled);
                            EngineEvent canceledEvent = new EngineEvent();
                            canceledEvent.Kind = EngineEventKind.CaptureCompleted;
                            canceledEvent.Capture = canceled;
                            _events.Enqueue(canceledEvent);
                            EnqueueDiagnostic("整圈捕获已取消，本次记录已按「用户取消」落盘。");
                        }
                    }
                    else
                    {
                        _capture.ResetSession();
                        if (state == EngineState.Enabled)
                            EnqueueDiagnostic("整圈捕获等待已取消，摆臂保持当前位置。");
                    }
                    return;

                case CmdStop:
                    HandleStopCommand(phi);
                    return;

                case CmdReturnHome:
                    if (IsEStopLatched)
                    {
                        RefuseCommand("return-home", "急停锁存未清，先 fault-reset 再回零。");
                        return;
                    }
                    // 「回到行程中点」是恢复入口：它只依赖软限位（绝对 counts），不依赖零点/摆杆基准，
                    // 所以标定待确认时也允许走（否则现场会被卡死在“被拒→无法回中→无法规整化”）。
                    bool centerTargetPending = Volatile.Read(ref _pendingReturnHomeTargetValid) != 0;
                    if (!centerTargetPending && Volatile.Read(ref _calibrationReviewRequired) != 0)
                    {
                        RefuseCommand("return-home", "位置标定待确认：先确认 / 规整化 / 重新采集。");
                        return;
                    }
                    string homePoseSessionReason = centerTargetPending ? null : PoseSessionReviewRejectText();
                    if (homePoseSessionReason != null)
                    {
                        RefuseCommand("return-home", homePoseSessionReason);
                        return;
                    }
                    if (Volatile.Read(ref _armZeroCalibrated) == 0)
                    {
                        RefuseCommand("return-home", "摆臂零点未标定（或已清空）：先在「设备参数」采集 / 设置摆臂零点，再使能后回零。");
                        return;
                    }
                    // 回中目标（零点或行程中点）必须在当前软限位内：控制拍再核一次，绝不把目标
                    // 钳到边界后硬动（慢路径也核，但那之后限位可能已经换过）。
                    int effTarget = Volatile.Read(ref _pendingReturnHomeTargetValid) != 0
                        ? Volatile.Read(ref _pendingReturnHomeTarget)
                        : Volatile.Read(ref _phiZeroCount);
                    if (effTarget < _limitsActive.ArmMinCount || effTarget > _limitsActive.ArmMaxCount)
                    {
                        Interlocked.Exchange(ref _pendingReturnHomeTargetValid, 0);
                        EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                            "回中被拒绝：目标 raw={0} 不在软限位 [{1},{2}] counts 内。",
                            effTarget, _limitsActive.ArmMinCount, _limitsActive.ArmMaxCount));
                        return;
                    }
                    if (state == EngineState.Enabled)
                    {
                        StartReturnHome(phi, armPos);
                        return;
                    }
                    if (state == EngineState.Stopped)
                    {
                        // Stopped 里 0x6040 已清零：先重新使能，完成后再自动接回零。
                        _pendingMotion = PendingMotionReturnHome;
                        StartEnable();
                        return;
                    }
                    RefuseCommand("return-home", "当前状态 " + state + " 不能回零：只有 Enabled / Stopped 可以（Stopped 会自动重新使能）。");
                    return;

                case CmdRadiusCalibration:
                    if (state != EngineState.Enabled)
                    {
                        RefuseCommand("calibrate-radius", "当前状态 " + state + "，只有 Enabled 可以。先 enable 并确认摆杆下垂静止。");
                        return;
                    }
                    if (_pendingRadiusAccel <= 0 || _pendingRadiusPairs <= 0 || _pendingRadiusPulseSec <= 0)
                    {
                        RefuseCommand("calibrate-radius", "脉冲参数未设置（加速度 / 相时长 / 对数），重新执行 calibrate-radius。");
                        return;
                    }
                    _radiusCalResult = null;
                    double radiusGravityOverLength = _limitsActive.JEq > 0
                        ? _limitsActive.Mgl / _limitsActive.JEq
                        : 0.0;
                    _radiusCal.Start(_pendingRadiusAccel, _pendingRadiusPulseSec, _pendingRadiusPairs,
                        _pendingRadiusGapSec, phi, radiusGravityOverLength);
                    Volatile.Write(ref _radiusCalActive, 1);
                    Interlocked.Exchange(ref _radiusCalAbortRequested, 0);
                    EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                        "[CAL] r/l 标定开始：先等摆杆在下垂位静止（|ω|≤{4:F2} rad/s、|θ|≤{5:F2} rad 连续 {6:F2} s，" +
                        "启动预检 |θ|≤{7:F2} rad），然后 ±{0:F1} rad/s² / {1:F0} ms × {2} 对；" +
                        "全程摆臂峰值位移约 {3:F2} rad。",
                        _pendingRadiusAccel, _pendingRadiusPulseSec * 1000.0, _pendingRadiusPairs,
                        _pendingRadiusAccel * _pendingRadiusPulseSec * _pendingRadiusPulseSec,
                        RadiusCalibrator.SettleOmega, RadiusCalibrator.SettleTheta,
                        RadiusCalibrator.SettleHoldSec, RadiusCalibrator.StartThetaLimit));
                    return;

                case CmdZeroPendulum:
                    // 手动置零同时建立摆杆 + 摆臂两个零点：先记下本拍 0x6064，
                    // 等摆杆锁零点那一拍一起落定（见 StepPendulumZero）。
                    _zeroArmCount = armPos;
                    _zeroArmCountPending = true;
                    _zeroPendulumUserRequested = true;
                    _zeroPulsePhase = 2;
                    return;

                case CmdZeroArm:
                    // 重标定：把当前 0x6064 锁成 φ_zero_count，φ 从 0 起算。不设 home（起始位置恒为坐标 0）。
                    _phiZeroCount = armPos;
                    Volatile.Write(ref _armZeroCalibrated, 1);
                    // 同步记成「用户设定的零点」：界面保存参数时能把它写进文件，下次启动勿被首次使能覆盖。
                    Volatile.Write(ref _armZeroOverride, armPos);
                    Volatile.Write(ref _armZeroOverrideSet, 1);
                    Volatile.Write(ref _calibrationDirty, 1);
                    _phiPrev = 0;
                    _phiDotFilt = 0;
                    _phiTargetPrimed = false;
                    _lastTargetValid = false;
                                        _phiRef = ArmHomeRad;
                    EnqueueDiagnostic("摆臂零点已重标定：当前位置 = 坐标 0 = 起始位置（home 恒为 0，不锁存任意角）。" +
                        "位置标定已标记为「待保存」：执行 save-config 落盘后下次进程启动才不会判标定不一致。");
                    return;

                case CmdDisable:
                    _enablePhase = PhaseIdle;
                    _pendingMotion = 0;
                    _stopPhase = 0;
                    _stopPhaseCyclesLeft = 0;
                    Volatile.Write(ref _jogDirection, 0);
                    Volatile.Write(ref _returnHomeActive, 0);
                    Volatile.Write(ref _returnHomeHoldCycles, 0);
                    FinishCaptureAttempt(Stopwatch.GetTimestamp());
                    _capture.ResetSession();
                    _swing.Reset();
                    ResetRadiusCalibration();
                    Interlocked.Exchange(ref _pendingReturnHomeTargetValid, 0);
                    SetState(EngineState.Connected);
                    return;

                case CmdSetGains:
                case CmdSetSwingUpParams:
                    // 参数走 generation 双缓冲，每拍首都会顶换，命令字只是契约里的唤醒位。
                    return;
            }
        }

        private void StartEnable()
        {
            // 使能命令已被接受：清掉上一条拒绝原因，避免旧原因盖住这次的新问题。
            ClearCommandRefusal();
            _enablePhase = PhaseNoMotionGate;
            _enableGateArmed = false;
            _enableGatePrevPos = 0;
            _enableGatePrevPosValid = false;
            _enableGateLastLogTicks = 0;
            _faultResetTicks = 0;
            Volatile.Write(ref _jogDirection, 0);
            _stopPhase = 0;
            _stopPhaseCyclesLeft = 0;
            _enableTimeoutTicks = Stopwatch.GetTimestamp();
            // 起始位置恒为 0，不再锁存当前角当 home。零点在使能完成那拍才建（见 RunEnableSequence）。
                        _phiRef = ArmHomeRad;
            _phiTargetPrimed = false;
            _lastTargetValid = false;
            _interlock.ResetCounters();
            _swing.Reset();
            ResetRadiusCalibration();
            EnqueueDiagnostic("使能序列启动：先过无运动门。");
        }

        /// <summary>
        /// 开始回零：只把目标锚到坐标 0（= 标定零点 raw），由现有斜率护栏逐拍 ramp，
        /// 不改零点 / 一圈量 / PID / 泵。进入前目标先锚在当前 φ，本拍不会产生阶跃。
        /// </summary>
        private void StartReturnHome(double phi, int armPos)
        {
            bool centerTarget = Interlocked.Exchange(ref _pendingReturnHomeTargetValid, 0) != 0;
            int target = centerTarget
                ? Volatile.Read(ref _pendingReturnHomeTarget)
                : Volatile.Read(ref _phiZeroCount);
            Volatile.Write(ref _returnHomeTargetCount, target);
            Volatile.Write(ref _jogDirection, 0);
            _phiTargetPrimed = true;
            _phiTargetPrev = phi;
            _phiRef = phi;
            _lastTargetValid = false;
            // 新的非平衡运动从 0 速重新做加速度斜坡，并从这一刻重新开始 20 Hz 诊断限流。
            _motionSlewSpeed = 0;
            _motionAccLimit = 0;
            _motionLogTicks = 0;
            Volatile.Write(ref _returnHomeActive, 1);
            Volatile.Write(ref _returnHomeHoldCycles, 0);
            _returnHomeStartTicks = Stopwatch.GetTimestamp();
            _interlock.ResetCounters();
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "开始回中：当前 0x6064={0} counts → 目标 raw={1} counts{2}（绝对位置；零点本身不改）。",
                armPos, target,
                centerTarget ? "（行程中点，到位后自动居中并重新锁零）" : "（标定零点）"));
        }

        /// <summary>
        /// <summary>
        /// 进入整圈捕获的等待抬升段：摆臂目标钉在当前 0x6064（phi 是当拍摆臂角），保持使能、不主动驱动。
        /// 等待期间由 <see cref="CaptureSession"/> 逐拍判顶部上膛 / 释放；释放后**不进平衡**，
        /// 摆臂继续不动，摆杆自由摆完一整圈（有效/失效都落记录）。
        /// </summary>
        private void StartReleaseWait(long nowTicks, double theta, int pendulumCount, double phi, int armPos)
        {
            _pendingMotion = 0;
            Volatile.Write(ref _jogDirection, 0);
            // 目标钉在当前值：primed=true + 目标=当前 φ，下一拍 RunControlLaw 不再回 0；
            // _lastTargetValid=false 让每拍增量护栏以实际位置为基线，避免把「已经在当前位置」误判成阶跃。
            _phiTargetPrimed = true;
            _phiTargetPrev = phi;
            _phiRef = phi;
            _lastTargetValid = false;
            _interlock.ResetCounters();
            _capture.Begin(nowTicks, theta, pendulumCount);
            // 越界判定按绝对 counts（与参数同一单位），不换算角度。
            if (armPos < _limitsActive.ArmMinCount || armPos > _limitsActive.ArmMaxCount)
            {
                EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                    "[LIMIT] 整圈捕获：摆臂当前 0x6064={0} 在软限位 [{1}, {2}] counts 外，" +
                    "捕获期间目标会被钳到限位边界（这是钳位，不是中止）。确认机械安全后再抬摆杆。",
                    armPos, _limitsActive.ArmMinCount, _limitsActive.ArmMaxCount));
            }
            EnqueueDiagnostic(string.Format(CultureInfo.InvariantCulture,
                "整圈捕获：进入等待抬升，摆臂目标钉在当前 0x6064={0}（不主动驱动）。" +
                "请把摆杆抬到顶部 180°（±8° 会提示上膛）后释放；摆杆要朝抬升方向转满一整圈才算有效。",
                armPos));
        }

        private void HandleStopCommand(double phi)
        {
            EngineState state = State;

            // 清锁存前先只读复检 S11：硬接线急停没松开就保持 Fault 并拒绝。
            // 两条证据都要看（0x6041 状态字 + 0x60FD bit23 急停输入）：只看状态字会漏掉
            // "驱动已去使能、急停端子仍闭合"那一类，清锁存就等于把仍在动作的急停放过去了。
            BusSession bus = _bus;
            if (bus != null && bus.IsOpen)
            {
                StepperTxPdo input;
                try
                {
                    input = bus.StepperInput();
                }
                catch (PdoInputStaleException)
                {
                    // 从站实测掉线：映像已是掉站前最后一帧，交给 ExecuteCycle 的 EtherCatState 口径。
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    if (!IsPdoImageUnavailable(ex) || Volatile.Read(ref _processInputPrimed) == 0) throw;
                    // 单拍过程数据不可用：与周期路径同一去抖口径，用上一拍已确认的过程数据做同一条 S11 判据。
                    // 判据只认"最近一次确认"的状态，不凭空放行；本会话从未读到过数据则立刻收口（上面的 throw）。
                    input = _lastStepperIn;
                }
                // 自己的受控停会把驱动送进 QuickStopActive（与急停/STO 的表现同形），
                // 所以受控停窗口（含收尾余量）里状态字那条证据不判，只留 0x60FD bit23 数字输入那条。
                bool statusEvidence = _stopPhase == 0 && _stopGraceCycles == 0 &&
                                      CiA402.IsSafetyStopAsserted(input.StatusWord);
                bool inputEvidence = CiA402.IsDigitalEStopAsserted(input.DigitalInputs);
                if (statusEvidence || inputEvidence)
                {
                    Volatile.Write(ref _eStopLatch, 1);
                    if (state != EngineState.Fault)
                    {
                        EmergencyStopInternal(FaultCode.EStopInput);
                    }
                    EnqueueDiagnostic(string.Format(
                        "硬接线急停 / STO 还闭合着（证据：0x6041 状态字={0}，0x60FD bit23 急停输入={1}），Stop() 被拒绝，保持 Fault。" +
                        "先松开急停按钮 / 恢复 STO，确认证据复位后再 fault-reset。",
                        statusEvidence ? "命中" : "未命中",
                        inputEvidence ? "已动作" : "未动作"));
                    return;
                }
            }

            if (state == EngineState.Enabled || state == EngineState.Swinging || state == EngineState.Balancing)
            {
                // 受控停 QuickStop 路径：0x0002 斜坡 → 确认 QuickStopActive → 0x0000 收尾。
                // 已经在受控停就别把预算拉回去，不然连敲 Stop 能把停机无限续期。
                Volatile.Write(ref _jogDirection, 0);
                Volatile.Write(ref _returnHomeActive, 0);
                Volatile.Write(ref _returnHomeHoldCycles, 0);
                _returnHomeStartTicks = 0;
                if (_capture.IsWaiting)
                    _capture.ResetSession();   // 等待态没有记录，停止时直接作废；捕获中留给 FinishStop 落记录
                if (_stopPhase == 0)
                {
                    _stopPhase = 1;
                    _stopPhaseCyclesLeft = (long)(StopConfirmBudgetSeconds / _dtSec);
                    if (_stopPhaseCyclesLeft < 2) _stopPhaseCyclesLeft = 2;
                    EnqueueDiagnostic("受控停：0x0002 Quick Stop 按 0x6085 急停减速度斜坡减速 → 确认 QuickStopActive → 0x0000 去使能。");
                }
                return;
            }

            // Fault / Stopped / Connected：直接收尾，清锁存。
            _stopPhase = 0;
            _stopPhaseCyclesLeft = 0;
            FinishStop();
            EnqueueDiagnostic("已停，急停锁存已清，可以重新「使能」。");
        }

        // ==================== 故障 ====================

        private void EmergencyStopInternal(FaultCode code)
        {
            // 故障码/状态先发布，断使能放最后：控制线程若卡在原生 GetIO/WaitForCycle，
            // 原来的「先 WriteDisableVoltage」会让 Fault 永远发不出来（R25 12:14/12:15 静默停止）。
            FaultCode reported = code == FaultCode.None ? FaultCode.UserStop : code;

            Volatile.Write(ref _eStopRequested, 0);
            _enablePhase = PhaseIdle;
            _pendingMotion = 0;
            _stopPhase = 0;
            _stopPhaseCyclesLeft = 0;
            Volatile.Write(ref _jogDirection, 0);
            Volatile.Write(ref _returnHomeActive, 0);
            Volatile.Write(ref _returnHomeHoldCycles, 0);
            _returnHomeStartTicks = 0;
            // 整圈捕获：记录中的尝试先落记录（失效原因如实写），等待态直接作废。
            FinishCaptureAttempt(Stopwatch.GetTimestamp());
            _capture.ResetSession();
            // 只有急停类故障才锁存，普通联锁故障由 Stop() 正常复位。
            if (reported == FaultCode.EStopInput || reported == FaultCode.UserStop)
            {
                Volatile.Write(ref _eStopLatch, 1);
            }

            EngineState from = State;
            if (from != EngineState.Fault)
            {
                Volatile.Write(ref _faultCode, (int)reported);
                SetState(EngineState.Fault);
                EnqueueFault(reported, from);
            }
            // 尽力断使能；即使过程映像卡死，也已经有 Fault 记录可查、CLI 可收口。
            WriteDisableVoltage();
        }

        /// <summary>把 0x6040 写成 0x0000 并把目标钉在实际位置上。故障与停机都走这一条。</summary>
        private void WriteDisableVoltage()
        {
            BusSession bus = _bus;
            if (bus == null || !bus.IsOpen) return;
            try
            {
                ref readonly StepperTxPdo inPdo = ref bus.StepperInput();
                ref StepperRxPdo outPdo = ref bus.StepperOutput();
                // 写序：先钉目标、再确认模式、最后断使能。中途若被总线取走一帧，
                // 最坏只是"目标=实测位置 + 旧使能"，不会产生运动；反过来写就会留一段位置阶跃。
                outPdo.TargetPosition = inPdo.ActualPosition;
                outPdo.ModesOfOperation = CiA402.ModeCsp;
                outPdo.ControlWord = CiA402.CwDisableVoltage;
                _lastTargetCount = inPdo.ActualPosition;
                _lastTargetValid = true;
            }
            catch (Exception ex)
            {
                // 摆臂从站掉线 / 过程映像不可用：写不进去就如实留痕，绝不用陈旧帧冒充实际位置；
                // 也不能让故障收尾路径自己再抛一次，那会把真正的根因埋掉。
                // 掉线期间这条路径每拍都会失败，只报第一次，别在 2 kHz 上刷诊断队列。
                if (Interlocked.Exchange(ref _disableVoltageWriteFailureReported, 1) == 0)
                    EnqueueDiagnostic("故障收尾写 0x6040=0 失败（过程映像不可用）：" + DiagText.Describe(ex));
            }
        }

        private void LatchFault(FaultCode code)
        {
            if (State == EngineState.Fault) return;
            // 先发布 Fault，再尽力断使能：周期线程停摆时，原生过程映像可能也卡住，
            // 不能把「写 0x6040」放在 Fault 之前（R25 12:14/12:15 就是这样丢掉 Fault 的）。
            Volatile.Write(ref _faultCode, (int)code);
            if (code == FaultCode.EStopInput) Volatile.Write(ref _eStopLatch, 1);
            EngineState from = State;
            SetState(EngineState.Fault);
            EnqueueFault(code, from);
            WriteDisableVoltage();
        }

        // ==================== 事件与状态 ====================

        private void SetState(EngineState next)
        {
            // 手动捕获请求只在 Enabled 态有效；离开 Enabled（含切平衡、停、故障、断开）立即清。
            if (next != EngineState.Enabled)
            {
                Volatile.Write(ref _manualBalanceRequest, 0);
                Interlocked.Exchange(ref _manualBalanceHoldCycles, 0);
            }
            EngineState prev = (EngineState)Interlocked.Exchange(ref _state, (int)next);
            if (prev == next) return;
            EngineEvent e = new EngineEvent();
            e.Kind = EngineEventKind.StateChanged;
            e.FromState = prev;
            e.ToState = next;
            _events.Enqueue(e);
        }

        private void EnqueueFault(FaultCode code, EngineState fromState)
        {
            // 只用**本次连接**产生过快照，绝不让上一会话的 θ / 状态字冒充故障现场。
            PendulumTelemetry t = default(PendulumTelemetry);
            bool hasTelemetry = _slot.TryRead(out t) && t.Sequence != 0 && t.TimestampTicks != 0 &&
                                (_sessionStartTicks == 0 || t.TimestampTicks >= _sessionStartTicks);
            FaultInfo f = new FaultInfo();
            f.Code = code;
            f.FromState = fromState;
            f.TimestampTicks = Stopwatch.GetTimestamp();
            f.HasTelemetry = hasTelemetry;
            f.ThetaRad = hasTelemetry ? t.ThetaRad : double.NaN;
            f.OmegaRadPerSec = hasTelemetry ? t.OmegaRadPerSec : double.NaN;
            f.PhiRad = hasTelemetry ? t.PhiRad : double.NaN;
            f.ArmPositionCount = hasTelemetry ? t.ArmPositionCount : int.MinValue;
            f.StatusWord = hasTelemetry ? t.StatusWord : (ushort)0;
            f.ErrorCode = hasTelemetry ? t.DriveErrorCode : (ushort)0;
            f.ActualWkc = hasTelemetry ? t.ActualWkc : (ushort)0;
            f.ExpectedWkc = hasTelemetry ? t.ExpectedWkc : (ushort)0;
            // 两条慢路径也要的证据：连续丢拍数（PdoFrameLoss 时故障行写"连续 N 拍"）
            // 与周期回调异常的第一行（旧版只当 bool 用，现场分不清抛出点）。
            f.StaleCycles = Volatile.Read(ref _staleCycles);
            f.ExceptionText = Volatile.Read(ref _callbackExceptionText);
            f.Detail = null;
            EngineEvent e = new EngineEvent();
            e.Kind = EngineEventKind.Faulted;
            e.FromState = fromState;
            e.ToState = EngineState.Fault;
            e.Fault = f;
            _events.Enqueue(e);
        }

        private void EnqueueDiagnostic(string text)
        {
            if (text == null) return;
            EngineEvent e = new EngineEvent();
            e.Kind = EngineEventKind.Diagnostic;
            e.Text = text;
            _events.Enqueue(e);
        }

        /// <summary>
        /// 最近一条命令拒绝原因（格式「命令：原因」）。从未拒绝 / 已清除时为 null。
        /// Enable() / Balance() 等都是异步排队，前端在等待超时时直接读这个属性拿根因，
        /// 不再依赖「诊断事件是否恰好被 Pump 出来」。
        /// </summary>
        public string LastCommandRefusal
        {
            get { return Volatile.Read(ref _lastRefusal); }
        }

        /// <summary>最近一条拒绝距今的秒数；没有记录时返回 0。</summary>
        public double LastCommandRefusalAgeSeconds
        {
            get
            {
                long ticks = Volatile.Read(ref _lastRefusalTicks);
                if (ticks == 0) return 0;
                return (Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency;
            }
        }

        /// <summary>清除拒绝原因（成功的命令入口调用，避免旧原因盖住新问题）。</summary>
        public void ClearCommandRefusal()
        {
            Volatile.Write(ref _lastRefusal, null);
            Volatile.Write(ref _lastRefusalTicks, 0L);
        }

        /// <summary>
        /// 记录一条命令拒绝：同时入诊断队列并写 <see cref="LastCommandRefusal"/>。
        /// 周期线程与慢线程都可调用（字符串引用赋值 + Volatile 时间戳，不持锁）。
        /// </summary>
        private void RefuseCommand(string command, string reason)
        {
            string text = string.IsNullOrEmpty(reason) ? command : command + "：" + reason;
            Volatile.Write(ref _lastRefusal, text);
            Volatile.Write(ref _lastRefusalTicks, Stopwatch.GetTimestamp());
            EnqueueDiagnostic("命令被拒绝（" + text + "）");
        }

        /// <summary>同上，但走同步 Raise：慢线程（UI / CLI 直接调用）的拒绝要立刻出现在前端日志里。</summary>
        private void RefuseCommandNow(string command, string reason)
        {
            string text = string.IsNullOrEmpty(reason) ? command : command + "：" + reason;
            Volatile.Write(ref _lastRefusal, text);
            Volatile.Write(ref _lastRefusalTicks, Stopwatch.GetTimestamp());
            RaiseDiagnostic("命令被拒绝（" + text + "）");
        }

        /// <summary>
        /// 周期线程的诊断出口：只入队，由 PumpEvents 统一出队 raise，
        /// 不在周期线程上同步回调前端（前端订阅方可能做落盘 / 排队，属于慢路径）。
        /// </summary>
        internal void ReportCycleDiagnostic(string text)
        {
            EnqueueDiagnostic(text);
        }

        private void RaiseStateChanged(EngineState fromState, EngineState toState)
        {
            EventHandler<EngineState> handler = StateChanged;
            if (handler == null) return;
            try { handler(this, toState); }
            catch (Exception ex)
            {
                RaiseDiagnostic("StateChanged 订阅方抛异常：" + DiagText.Describe(ex));
            }
        }

        private void RaiseFaulted(FaultInfo fault)
        {
            EventHandler<FaultInfo> handler = Faulted;
            if (handler == null) return;
            try { handler(this, fault); }
            catch (Exception ex)
            {
                RaiseDiagnostic("Faulted 订阅方抛异常：" + DiagText.Describe(ex));
            }
        }

        private void RaiseDiagnostic(string text)
        {
            RaiseDiagnostic(DiagnosticSeverity.Unspecified, text);
        }

        /// <summary>
        /// 当前正在同步派发的诊断行显式级别；只在 <see cref="Diagnostic"/> 处理器执行期间有效，
        /// 离开处理器即恢复成上一条（含重入场景）。
        /// <para>
        /// 事件签名保持 <c>EventHandler&lt;string&gt;</c>：CLI 订阅方不在本次改动范围，
        /// 显式级别经这条“同线程同步派发上下文”传给消费者。未声明级别时为
        /// <see cref="DiagnosticSeverity.Unspecified"/>，消费方照旧走关键词兜底。
        /// </para>
        /// </summary>
        public static DiagnosticSeverity CurrentDiagnosticSeverity
        {
            get { return _dispatchSeverity; }
        }

        [ThreadStatic]
        private static DiagnosticSeverity _dispatchSeverity;

        /// <summary>
        /// 带显式级别的诊断：级别由产生方声明（<see cref="DiagnosticSeverity.Audit"/> = 连接期必落盘取证）。
        /// 级别只在同步派发期间有效，绝不跨线程共享 —— 多个线程同时打日志互不串味。
        /// </summary>
        internal void RaiseDiagnostic(DiagnosticSeverity severity, string text)
        {
            EventHandler<string> handler = Diagnostic;
            if (handler == null) return;
            DiagnosticSeverity previous = _dispatchSeverity;
            _dispatchSeverity = severity;
            try { handler(this, text); }
            catch (Exception ex)
            {
                // 诊断订阅方抛异常绝不能回灌调用方，所以不再走 RaiseDiagnostic。
                // 但也不空吞：写到 Debug 输出，调试器里看得到。
                System.Diagnostics.Debug.WriteLine("诊断订阅方抛异常：" + DiagText.Describe(ex));
            }
            finally
            {
                _dispatchSeverity = previous;
            }
        }

        private void RaiseCaptureCompleted(CaptureRecord record)
        {
            EventHandler<CaptureRecord> handler = CaptureCompleted;
            if (handler == null) return;
            try { handler(this, record); }
            catch (Exception ex)
            {
                RaiseDiagnostic("CaptureCompleted 订阅方抛异常：" + DiagText.Describe(ex));
            }
        }

        private void RaiseFaultNow(FaultCode code, string detail)
        {
            Volatile.Write(ref _faultCode, (int)code);
            // 状态机跟故障码必须一致，否则前端按 FaultInfo 映射退出码时会自相矛盾。
            if (State != EngineState.Disconnected) SetState(EngineState.Fault);
            FaultInfo f = new FaultInfo();
            f.Code = code;
            f.FromState = State;
            f.TimestampTicks = Stopwatch.GetTimestamp();
            PendulumTelemetry snapshot = default(PendulumTelemetry);
            f.HasTelemetry = _slot.TryRead(out snapshot) && snapshot.Sequence != 0 && snapshot.TimestampTicks != 0 &&
                             (_sessionStartTicks == 0 || snapshot.TimestampTicks >= _sessionStartTicks);
            f.ArmPositionCount = f.HasTelemetry ? snapshot.ArmPositionCount : int.MinValue;
            f.Detail = detail;
            RaiseFaulted(f);
            if (!string.IsNullOrEmpty(detail)) RaiseDiagnostic(detail);
        }

        private static string FormatFaultDetail(FaultInfo f)
        {
            string core;
            if (f.HasTelemetry)
            {
                core = string.Format(CultureInfo.InvariantCulture,
                    "FaultCode={0} 从 {1} 进 Fault：θ={2:F4} rad，θ-π={3:F4} rad，ω={4:F3} rad/s，摆臂 0x6064={5} counts，0x6041=0x{6:X4}，0x603F=0x{7:X4}，WKC={8}/{9}",
                    f.Code, f.FromState, f.ThetaRad,
                    ControlMath.WrapPi(f.ThetaRad - Math.PI),
                    f.OmegaRadPerSec, f.ArmPositionCount,
                    f.StatusWord, f.ErrorCode, f.ActualWkc, f.ExpectedWkc);
            }
            else
            {
                // 没有本次会话的遥测时，θ / 摆臂 / 状态字 / WKC 全部未知：写 n/a，不拿 0 冒充测量值。
                core = string.Format(CultureInfo.InvariantCulture,
                    "FaultCode={0} 从 {1} 进 Fault：本次会话尚无遥测快照（θ / ω / 摆臂 0x6064 / 0x6041 / WKC = n/a）",
                    f.Code, f.FromState);
            }
            // 单拍 STALE 去抖连续到阈值才判 PdoFrameLoss：故障行必须写清"连续 N 拍无有效过程数据"，
            // 而不是只给一个丢帧码让现场猜到底丢了几拍。stale=0 的其他故障不写这一段。
            if (f.StaleCycles > 0)
            {
                core += string.Format(CultureInfo.InvariantCulture,
                    "；连续 {0} 拍无有效过程数据（stale={0}）", f.StaleCycles);
            }
            // 周期回调异常的第一行照实带出：一眼分清是「PDO 输入映像不可用」还是别的抛出点。
            if (!string.IsNullOrEmpty(f.ExceptionText))
            {
                core += "；周期回调异常=" + f.ExceptionText;
            }
            string guidance = FaultGuidance(f.Code);
            return guidance == null ? core : core + "；处置：" + guidance;
        }

        /// <summary>
        /// 每个故障码的现场处置建议。故障日志必须直接给出“下一步查什么”，
        /// 不允许只有一串数字让现场自己猜。
        /// </summary>
        private static string FaultGuidance(FaultCode code)
        {
            switch (code)
            {
                case FaultCode.ThetaOverrun:
                    return "摆杆越界：检查摆杆是否被外力撞出工作区、θ 零点是否漂移；确认机械安全后 Stop() 复位再使能。";
                case FaultCode.FollowingError:
                    return "跟随误差超限：检查 0x6064 与 0x607A 的差值、机械是否卡滞、AccMax / ArmRateMax 是否过大；排除后 Stop() 复位。";
                case FaultCode.EnableLost:
                    return "驱动器掉使能：读 0x6041 状态字与 0x603F 错误码，检查供电与 STO；恢复后 Stop() 复位再 enable。";
                case FaultCode.DriveFault:
                    return "驱动器故障：读 0x603F / 驱动器面板错误码；排除后断电重启或 Stop() 复位。";
                case FaultCode.WkcInvalid:
                    return "WKC 不足：检查摆臂与 GCAN 两站是否在 OP、网线与终端电阻；WKC 恢复后 Stop() 复位。";
                case FaultCode.PdoFrameLoss:
                    return "PDO 连续丢帧：检查网线 / 屏蔽 / 从站供电；连续丢帧归零后 Stop() 复位。";
                case FaultCode.EtherCatState:
                    return "EtherCAT 状态异常：查 BusState 与各从站 OP 状态、链路；恢复后重新 connect。";
                case FaultCode.CallbackException:
                    return "周期回调异常：看本条前后的回调异常详情与堆栈；修复后重启进程再连接。";
                case FaultCode.TimingInvalid:
                    return "周期 / 遥测停摆：确认控制线程存活与内核周期推进（status 看实测周期、seq 是否增长）；恢复后 Stop() 复位。";
                case FaultCode.EStopInput:
                    return "硬接线急停 / STO 动作：先松开急停按钮或恢复 STO，确认 0x60FD bit23 / 0x6041 复位后再 fault-reset。";
                case FaultCode.SwingUpTimeout:
                    return "起摆超时：检查摆杆是否能自由摆动、泵幅 / 能量参数是否足够；可加大 --timeout-ms 后重试。";
                case FaultCode.BalanceLost:
                    return "平衡失稳：检查切换时 ψ / ω 是否过大、Ka 是否高于 g/l；调整后重新起摆。"
                        + "（若为连续自动回起摆用满 6 次后报出，说明当前参数 / 机械状态下平衡确实建立不起来。）";
                case FaultCode.IdentityMismatch:
                    return "从站身份不符：核对 DENI 与现场驱动器型号 / 从站序号（--arm-slave）；重新导出 DENI 后重连。";
                case FaultCode.ConfigInvalid:
                    return "配置无效：按上一条具体字段原因修正参数文件或命令行，再重试。";
                case FaultCode.StopSequenceTimeout:
                    return "受控停超时：查 0x6041 是否进 QuickStopActive、驱动器是否响应；必要时按硬急停后再复位。";
                case FaultCode.TeardownTimeout:
                    return "断开收尾超时：确认总线 / 从站仍有响应；重启进程后重新 connect。";
                case FaultCode.UserStop:
                    return "软件急停已执行（0x6040=0）：现场确认机械已停稳，排除触发原因后 fault-reset。";
                case FaultCode.SoftLimit:
                    return "摆臂实测越过软限位：检查限位标定与机械行程，确认后 Stop() 复位并重新采集限位。";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 一次整圈捕获的日志行。字段顺序固定，现场按 CAPTURE 检索；数值缺失写 n/a，不写 0 冒充。
        /// 失效记录必带 reason（用户能在日志里看到「左侧来、左侧回 / 累计多少度」这种证据）。
        /// </summary>
        private static string FormatCaptureLine(CaptureRecord record)
        {
            string reason = string.IsNullOrEmpty(record.InvalidReason) ? "-" : record.InvalidReason;
            return string.Format(CultureInfo.InvariantCulture,
                "CAPTURE valid={0} release={1}deg omega={2}rad/s lift={3}s rev={4}s sweep={5}/{6}deg " +
                "maxOmega={7}rad/s keypoints={8} reason={9}",
                record.Valid ? "yes" : "no",
                FormatCaptureNumber(record.ReleaseThetaDeg, "F1"),
                FormatCaptureNumber(record.ReleaseOmegaRadPerSec, "F3"),
                FormatCaptureNumber(record.LiftSeconds, "F2"),
                FormatCaptureNumber(record.RevolutionSeconds, "F2"),
                FormatCaptureNumber(record.SweepDeg, "F1"),
                FormatCaptureNumber(record.MaxSweepDeg, "F1"),
                FormatCaptureNumber(record.MaxOmegaRadPerSec, "F3"),
                record.KeyPointCount.ToString(CultureInfo.InvariantCulture),
                reason);
        }

        /// <summary>捕获日志里的数值：非有限数写 n/a，不写 0 冒充。</summary>
        private static string FormatCaptureNumber(double value, string format)
        {
            if (!ControlMath.IsFinite(value)) return "n/a";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// [SAFETY] 现场日志行：故障码枚举名 + 触发条件。只在慢路径（PumpEvents）拼字符串。
        /// 带运动量与驱动状态，现场看日志就能定位是哪一条联锁、当时的总线状态如何。
        /// </summary>
        private string FormatSafetyFaultLine(FaultInfo f)
        {
            string core = f.HasTelemetry
                ? string.Format(CultureInfo.InvariantCulture,
                    "[SAFETY] FAULT: code={0} 触发: state={1}, θ={2:F4} rad, θ-π={3:F4} rad, ω={4:F3} rad/s, 摆臂 0x6064={5} counts, 0x6041=0x{6:X4}, 0x603F=0x{7:X4}, WKC={8}/{9}",
                    f.Code, f.FromState, f.ThetaRad, ControlMath.WrapPi(f.ThetaRad - Math.PI),
                    f.OmegaRadPerSec, f.ArmPositionCount, f.StatusWord, f.ErrorCode, f.ActualWkc, f.ExpectedWkc)
                : string.Format(CultureInfo.InvariantCulture,
                    "[SAFETY] FAULT: code={0} 触发: state={1}, 本次会话尚无遥测快照（θ / 摆臂 / 0x6041 / WKC = n/a）",
                    f.Code, f.FromState);
            // 现场日志固定带 stale 字段：去抖是否在兜、兜了几拍，grep 一行就够。
            core += string.Format(CultureInfo.InvariantCulture, ", stale={0}", f.StaleCycles);
            // 周期回调异常的第一行（类型 + 消息）也进 [SAFETY] 行，不再只剩一个 CallbackException 码。
            if (!string.IsNullOrEmpty(f.ExceptionText))
            {
                core += "，回调异常=" + f.ExceptionText;
            }
            string guidance = FaultGuidance(f.Code);
            return guidance == null ? core : core + "；处置：" + guidance;
        }

        // ==================== 参数顶换与标定 ====================

        private void SwapIfDirty()
        {
            if (Volatile.Read(ref _paramDirty) == 0) return;
            int v1 = Volatile.Read(ref _paramVersion);
            if ((v1 & 1) != 0) return;
            PidGains pid = _pidShadow;
            global::FurutaPendulum.Control.SwingUpParams swing = _swingShadow;
            SafetyLimits limits = _limitsShadow;
            Thread.MemoryBarrier();
            int v2 = Volatile.Read(ref _paramVersion);
            if (v1 != v2) return;

            // default(SafetyLimits) 之类的全零限位会让软限位区间塌成 [0,0]（min == max），
            // 自动运动的目标会被钳死在一个点上。入口处已校验过，这里再兜一道：
            // 无效就沿用上一组有效限位，绝不把臂锁死。
            string limitReason;
            if (!limits.IsValid(out limitReason))
            {
                limits = _limitsActive;
                EnqueueDiagnostic("安全阈值不合法，沿用上一组有效值：" + limitReason);
            }

            // 旧版参数文件的加速度律增益全是 0：按内置默认补齐，保证控制律永远不会拿 Ka=0 跑。
            bool substituted = ApplyBalanceLawDefaults(ref pid, limits);

            _pidActive = pid;
            _swingActive = swing;
            _limitsActive = limits;
            RefreshFeedbackAlpha();
            NoticeLegacyGainsIfNeeded(substituted, true);
            // 用 CAS 清 dirty。快照（上面读到 v2）之后、这条 CAS 之前若又落了新参数，
            // 这一次 CAS 会连带把它一起清掉 —— 所以清完再核一次版本：变了就把 dirty 重新立起来，
            // 下一拍按新影子再顶换一次，绝不吞更新（慢线程不重试也不会漏）。
            Interlocked.CompareExchange(ref _paramDirty, 0, 1);
            if (Volatile.Read(ref _paramVersion) != v2) Volatile.Write(ref _paramDirty, 1);
        }

        private void RefreshFeedbackAlpha()
        {
            // ω 与 φ̇ 用参数 TdLpMs 定义的一阶低通：α = TdLp/(TdLp+dt)，不引入第二个滤波常数。
            double tdSec = _pidActive.TdLpMs > 0 ? _pidActive.TdLpMs * 0.001 : 0;
            _feedbackAlpha = tdSec > 0 ? tdSec / (tdSec + _dtSec) : 0;
        }

        /// <summary>
        /// 旧版参数文件（位置映射律）没有加速度律字段，读出来全是 0；另有越界项也在这里回落。
        /// 规则本身只有一份 —— <see cref="PidGains.ResolveAccelerationLaw"/>，加载与运行期共用。
        /// 返回 true = 发生过替换，调用方据此提示用户按新律重新整定。
        /// </summary>
        private bool ApplyBalanceLawDefaults(ref PidGains g, SafetyLimits limits)
        {
            // 结构下界只能用**本机标定值**算：g/l = Mgl / JEq，耦合幅值 |β| = limits.radius_ratio。
            // 任一缺失（0 / 非有限）= 未标定：Resolve 不给假下限，并在 note 里说清楚。
            double gravityOverLength = 0.0;
            if (ControlMath.IsFinite(limits.Mgl) && ControlMath.IsFinite(limits.JEq) &&
                limits.Mgl > 0 && limits.JEq > 0)
            {
                gravityOverLength = limits.Mgl / limits.JEq;
            }
            double couplingAbs = Math.Abs(limits.RadiusRatio);
            string structuralNote;
            bool replaced = g.ResolveAccelerationLaw(gravityOverLength, couplingAbs, out structuralNote);
            if (!_balanceStructureNoteRaised && !string.IsNullOrEmpty(structuralNote))
            {
                _balanceStructureNoteRaised = true;
                EnqueueDiagnostic("[BALANCE] " + structuralNote);
            }
            return replaced;
        }

        /// <summary>
        /// 旧版增益被替换时的现场提示。每个会话只提示一次，免得每次改参数都刷一遍。
        /// fromCycleThread=true 时只入队（周期线程不得同步调前端回调）。
        /// </summary>
        private void NoticeLegacyGainsIfNeeded(bool substituted, bool fromCycleThread)
        {
            if (!substituted || _legacyGainsNoticeRaised) return;
            _legacyGainsNoticeRaised = true;
            string text =
                "检测到旧版「摆臂位置映射律」增益（kp / ki / kd）：它与当前加速度反馈律量纲不同、" +
                "没有正确换算关系，已改用默认 Ka=900 / Kad=55 / Kφ=4 / Kφ̇=4，旧 kp/kd 不再参与控制。" +
                "请在 PID 参数卡里按新律重新整定。";
            if (fromCycleThread) EnqueueDiagnostic(text);
            else RaiseDiagnostic(text);
        }

        /// <summary>
        /// 摆臂标定裁决（纯函数，可自检）：拿实读事实 + 显式标定 + 减速比算出权威 CPR_ARM，并做三道自检。
        /// 返回 false = 拒绝运动 / 拒进 CSP，reason 写清是哪一条、两边各是多少；true 时 source 说明 CPR_ARM 是怎么来的。
        /// <list type="number">
        /// <item>权威标定 = 0x2102 实读 P × <see cref="PendulumEngineOptions.ArmGearRatio"/>：细分是驱动器自己的
        /// 硬件事实，参数文件里的显式值可能已经过时。0x2102 读不到才用显式
        /// <see cref="PendulumEngineOptions.ArmCountsPerRev"/> 兜底；两者都没有就拒绝运动。
        /// 显式值与实读推导值不一致时按**实读值**走，并把差异写进 source —— 现场日志看得见，不静默覆盖。</item>
        /// <item>0x608F / 0x6091：EC1 全系没有这两个对象（手册 §5.3「手册未见」），读不到就按 0x2102 走；
        /// 能读到的机型在 0x2102 也读不到时用 0x608F:01 × (0x6091:01 ÷ 0x6091:02) 定标（驱动器自己的
        /// 位置单位/摆臂转）。0x2102 读得到时 0x608F:01 只做交叉校验，不一致拒动并报差异。</item>
        /// <item>0x8402 根因① 单圈脉冲数不对应（手册 6.3 章原文）：电机侧每转计数必须等于 0x2102 实读值，不等就拒进 CSP 报差异；
        /// 0x2102 读不到（按 OD 或显式值定标）就跳过这条自检并如实说明。</item>
        /// <item>0x8402 根因② 同步周期越窗：SYNC0 周期必须落在 0x2003:01..:02（EC1 = 1000~4000µs），越窗拒进 CSP 报差异；
        /// 0x2003 读不到 = 自检做不了，同样拒进 CSP，不静默放行。</item>
        /// </list>
        /// </summary>
        internal static bool ResolveArmCalibration(
            ArmDriveFacts facts,
            int explicitCountsPerRev,
            double gearRatio,
            uint sync0CycleNs,
            out double armCountsPerRev,
            out string source,
            out string reason)
        {
            armCountsPerRev = 0;
            source = null;
            reason = null;

            if (!(gearRatio > 0) || !ControlMath.IsFinite(gearRatio))
            {
                reason = string.Format("ArmGearRatio={0} 不合法（必须是 >0 的有限数），无法定标，拒绝运动。", gearRatio);
                return false;
            }

            double p = facts.MotorCountsPerRev;
            double odCpr = facts.OdCountsPerArmRev;
            double armCpr;
            bool cprFromOd = false;
            if (p > 0)
            {
                // 0x2102 实读优先：它才是驱动器当前细分的真实值。显式值只在读不到时兜底，
                // 否则参数文件里一个陈旧的数（例如 2400）会静默盖住实读的 10000，位置换算差 4 倍。
                armCpr = p * gearRatio;
                source = string.Format("0x2102 实读 P={0} × 减速比 {1}", p, gearRatio);
                if (explicitCountsPerRev > 0 && Math.Abs(explicitCountsPerRev - armCpr) > 0.5)
                {
                    source += string.Format(
                        "（注意：显式 ArmCountsPerRev={0} 与实读推导值 {1} 不一致，已按实读值；" +
                        "请把参数文件里的标定改成 {1}，否则界面上显示的数会与实跑不符）",
                        explicitCountsPerRev, armCpr);
                }
            }
            else if (odCpr > 0)
            {
                // 没有 0x2102 的机型：按驱动器自己的 0x608F:01 × (0x6091:01 ÷ 0x6091:02) 定标。
                // 0x6064 / 0x606C 用的就是这套位置单位，所以它同时是位置与速度换算的分母。
                armCpr = odCpr;
                cprFromOd = true;
                source = string.Format(
                    "0x2102 读不到，按 OD 定标 0x608F:01={0} × (0x6091:01 {1} ÷ 0x6091:02 {2}) = {3}",
                    facts.PprFromOd, facts.GearMotorRevs, facts.GearFeedRevs, armCpr);
                if (explicitCountsPerRev > 0 && Math.Abs(explicitCountsPerRev - armCpr) > 0.5)
                {
                    source += string.Format(
                        "（注意：显式 ArmCountsPerRev={0} 与 OD 推导值 {1} 不一致，已按 OD 实读值）",
                        explicitCountsPerRev, armCpr);
                }
            }
            else if (explicitCountsPerRev > 0)
            {
                armCpr = explicitCountsPerRev;
                source = string.Format("0x2102 读不到，使用显式 ArmCountsPerRev={0}", explicitCountsPerRev);
            }
            else
            {
                reason = string.Format(
                    "摆臂每转计数未标定：0x2102 实读拿不到电机单圈脉冲数，0x608F / 0x6091 未取得可用值" +
                    "（原因见原始诊断，不据此断言设备一定没有该对象），且未给显式 ArmCountsPerRev，" +
                    "推导不出 CPR_ARM，拒绝运动。{0}",
                    facts.Notes ?? string.Empty);
                return false;
            }
            if (!(armCpr > 0) || !ControlMath.IsFinite(armCpr))
            {
                reason = string.Format("摆臂每转计数算出来是 {0}，不合法，拒绝运动。", armCpr);
                return false;
            }

            double motorCpr = armCpr / gearRatio;

            // 0x8402 根因①：驱动器细分与主站的单圈脉冲数不对应（手册 6.3 章原文写死的根因）。
            if (p > 0)
            {
                double diffP = motorCpr - p;
                if (Math.Abs(diffP) > 0.5)
                {
                    reason = string.Format(
                        "拒绝进 CSP：单圈脉冲数不对应（0x8402 根因①）。主站电机侧每转 {0}，驱动器 0x2102 实读 P={1}，差 {2}。" +
                        "把 ArmCountsPerRev / ArmGearRatio 与驱动器细分对上再试。",
                        motorCpr, p, diffP);
                    return false;
                }
            }
            else if (cprFromOd)
            {
                // 按 OD 定标时 gearRatio 已含在 0x6091 里，不再参与推导；这条自检没有 0x2102 可比，如实说明跳过。
                source += "；0x2102 读不到，单圈脉冲数自检（0x8402 根因①）跳过";
            }
            else if (explicitCountsPerRev > 0)
            {
                // 0x2102 读不到但显式标定给了值：照显式值走，如实说明这条自检跳过了，不假装校验过。
                source += "；0x2102 读不到，单圈脉冲数自检（0x8402 根因①）跳过";
            }

            // 0x8402 根因②：主站同步周期不在驱动器允许窗口内。
            int sync0Us = (int)(sync0CycleNs / 1000u);
            int minUs = facts.MinSyncCycleUs;
            int maxUs = facts.MaxSyncCycleUs;
            if (minUs <= 0 || maxUs <= 0)
            {
                reason = string.Format(
                    "拒绝进 CSP：0x2003 同步周期窗口读不到（自检做不了，不静默放行）。本机 SYNC0={0}µs。{1}",
                    sync0Us, facts.Notes ?? string.Empty);
                return false;
            }
            if (sync0Us < minUs || sync0Us > maxUs)
            {
                reason = string.Format(
                    "拒绝进 CSP：同步周期越窗（0x8402 根因②）。本机 SYNC0={0}µs，驱动器 0x2003:01..:02 = {1}~{2}µs。",
                    sync0Us, minUs, maxUs);
                return false;
            }

            // 0x608F 交叉校验：EC1 读不到就跳过照常走（这是常态），读到了就必须一致。
            // 按 OD 定标时（cprFromOd）0x608F:01 本身就是标定的来源，再比一次是同义反复，跳过。
            if (facts.PprReadable && !cprFromOd)
            {
                double ppr = facts.PprFromOd;
                double diff = ppr - motorCpr;
                if (Math.Abs(diff) > 0.5)
                {
                    reason = string.Format(
                        "拒绝运动：0x608F:01 与标定不一致。OD 实读 {0}，标定电机侧每转 {1}（CPR_ARM={2} ÷ 减速比 {3}），差 {4}。",
                        ppr, motorCpr, armCpr, gearRatio, diff);
                    return false;
                }
            }

            armCountsPerRev = armCpr;
            return true;
        }

        /// <summary>
        /// 运动前的摆臂标定定标 + 0x8402 初始化自检（04 §D2 / 05 §12E-2）。
        /// 裁决全在 <see cref="ResolveArmCalibration"/>（纯函数，可自检），这里只负责实读事实 + 把结论落到状态与诊断。
        /// </summary>
        private bool RefreshArmCalibration(BusSession bus)
        {
            ArmDriveFacts facts;
            string error;
            if (!bus.TryReadArmDriveFacts(out facts, out error))
            {
                RaiseDiagnostic("摆臂标定事实读不到，禁止运动：" + error);
                return false;
            }

            double armCpr;
            string source;
            string reason;
            if (!ResolveArmCalibration(
                    facts, _options.ArmCountsPerRev, _options.ArmGearRatio, BusSession.ArmSync0CycleNs,
                    out armCpr, out source, out reason))
            {
                RaiseDiagnostic(reason);
                return false;
            }

            _countsPerRevArm = armCpr;
            int sync0Us = (int)(BusSession.ArmSync0CycleNs / 1000u);
            RaiseDiagnostic(string.Format(
                "摆臂标定定标完成：CPR_ARM={0}（{1}），电机侧每转={2}，0x2102={3}，0x2003 窗口={4}~{5}µs，SYNC0={6}µs，0x608F={7}。",
                armCpr, source, armCpr / (_options.ArmGearRatio > 0 ? _options.ArmGearRatio : 1.0),
                facts.MotorCountsPerRev > 0 ? facts.MotorCountsPerRev.ToString() : "读不到",
                facts.MinSyncCycleUs, facts.MaxSyncCycleUs, sync0Us,
                DescribePprReadState(facts)));
            return true;
        }

        /// <summary>
        /// 0x608F 交叉校验的**实际状态**说明。三种情况必须分开写，不能把「我们主动没探」
        /// 说成「设备没有这个对象」：
        /// ① 真读到 → 数值；
        /// ② 0x2102 已实读 → 按能力事实主动跳过 0x608F/0x6091 探测，未向设备查询；
        /// ③ 真的问了设备且拿到确定性 ObjectDoesNotExist（0x06020000 / 0x06090011）→ 设备报告不存在；
        /// ④ 问了但没拿到确定性结论（超时 / 其它 abort）→ 如实写探测未返回可用值 + 原始诊断。
        /// </summary>
        internal static string DescribePprReadState(ArmDriveFacts facts)
        {
            if (facts.PprReadable)
                return "已实读 " + facts.PprFromOd;

            if (facts.MotorCountsPerRev > 0)
            {
                return "按能力事实主动跳过探测（0x2102 已实读 P=" + facts.MotorCountsPerRev +
                       "；0x608F/0x6091 未向设备查询，交叉校验跳过）";
            }

            string notes = facts.Notes ?? string.Empty;
            const string abortDoesNotExist = "AbortCode=0x06020000";
            const string abortInvalidSub = "AbortCode=0x06090011";
            if (notes.IndexOf(abortDoesNotExist, StringComparison.OrdinalIgnoreCase) >= 0)
                return "设备报告该对象不存在（Abort=0x06020000，0x608F:01 探测已实际发出；交叉校验跳过）";
            if (notes.IndexOf(abortInvalidSub, StringComparison.OrdinalIgnoreCase) >= 0)
                return "设备报告该子索引不存在（Abort=0x06090011，0x608F:01 探测已实际发出；交叉校验跳过）";

            string raw = string.IsNullOrEmpty(notes) ? "无原始诊断" : notes;
            return "0x608F:01 探测未返回可用值，未得到「设备确定没有此对象」的结论（原始诊断：" + raw + "）";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException("PendulumEngine");
        }

        /// <summary>状态迁移。在 PumpEvents 里 raise，不在周期线程同步调前端。</summary>
        public event EventHandler<EngineState> StateChanged;

        /// <summary>任何安全联锁命中、驱动故障、反馈失效、丢使能都会抛这里。</summary>
        public event EventHandler<FaultInfo> Faulted;

        /// <summary>诊断行。控制层不自建日志文件，前端决定写哪。</summary>
        public event EventHandler<string> Diagnostic;

        /// <summary>一次整圈捕获结束。UI 用它落盘捕获记录并触发辨识。</summary>
        public event EventHandler<CaptureRecord> CaptureCompleted;
    }
}
