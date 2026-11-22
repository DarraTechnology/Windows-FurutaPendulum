using System;
using System.Runtime.InteropServices;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 步进电机主站→从站（RxPDO）。字段顺序必须与摆臂步进的实际 ESI 逐字节一致，换机型按实机 ESI 重排，勿臆测。
    /// <para>
    /// 当前基准 = GR-EC01 / EC1 系列（Vendor #x0AB5）默认 CSP RxPDO <c>#x1600</c>，7 型号条目序完全相同
    /// （04-EC1-ESI分析 §2.3，EC-57A-7 行 L2873/L2880/L2887）：
    /// `0x6040:16bit@0 → 0x607A:32bit@2 → 0x6060:8bit@6`，合计 56 bit = 7 B。
    /// </para>
    /// <para>
    /// 旧顺序（0x6040 → 0x6060 → 0x607A，DENI/Ezi 机型）已作废：0x6060 那 1 字节若落在偏移 2，
    /// 会被驱动当成目标位置 0x607A 的第 3 字节，每周期写 8 = 给目标掺 +524288 count 固定偏置，电机直接跑飞。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StepperRxPdo
    {
        /// <summary>0x6040 控制字 [UINT16]，字节偏移 0。</summary>
        public ushort ControlWord;

        /// <summary>0x607A 目标位置 [INT32，单位 Pulse]，字节偏移 2。CSP 下直接周期刷新 0x607A，无"新设定点"握手。</summary>
        public int TargetPosition;

        /// <summary>
        /// 0x6060 运行模式 [SINT8]，字节偏移 6。CSP = 8。
        /// ESI 的 InitCmd 在 PS 迁移时已自动写 0x6060=08（04 §6.4），这里照常发不冲突；
        /// 但绝不假定"默认就是 CSP"——0x6060 表格里的「默认值 0」是手册排版缺陷，一律以 0x6061 回读为准。
        /// </summary>
        public sbyte ModesOfOperation;
    }

    /// <summary>
    /// 步进电机从站→主站（TxPDO）。基准 = GR-EC01 / EC1 系列默认 CSP TxPDO <c>#x1A00</c>
    /// （04-EC1-ESI分析 §2.4，EC-57A-7 行 L3056/L3063/L3070/L3077/L3084/L3091）：
    /// `0x603F:16bit@0 → 0x6041:16bit@2 → 0x6064:32bit@4 → 0x606C:32bit@8 → 0x60FD:32bit@12 → 0x6061:8bit@16`，
    /// 合计 136 bit = 17 B。旧结构体 13 B 且条目序全错，改前务必整表重排（漏一条 = 整段错位）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StepperTxPdo
    {
        /// <summary>0x603F 最新错误码 [UINT16]，字节偏移 0。运行中 0x7500 多半是目标更新过快；0x8401 位置超差（不可清除）是开环丢步的唯一线索。</summary>
        public ushort ErrorCode;

        /// <summary>0x6041 状态字 [UINT16]，字节偏移 2。按掩码解析，不要拿原始整数当状态。</summary>
        public ushort StatusWord;

        /// <summary>
        /// 0x6064 当前实际位置 [INT32，单位 Pulse]，字节偏移 4。
        /// ⚠ EC1 是纯开环步进（0x2100 明写"本产品只支持开环"），本值是驱动器内部指令位置的积分估计，
        /// 不是电机轴实测位置——丢步 / 堵转 / 越障静默不可见，不能当真实机械角的唯一依据。
        /// </summary>
        public int ActualPosition;

        /// <summary>
        /// 0x606C 当前实际速度 [INT32]，字节偏移 8。**单位 = Pulse/s（驱动器位置单位/秒）**，与 0x6064 的计数同口径。
        /// 手册 4.1.3 与 V1.0.1 修订历史写 rpm、5.3 表写 Pulse/s，两处互相矛盾；实机实测站得住的是 Pulse/s：
        /// 本台架 EC1 回报 0x606C = 0/1000/2000/3000（量化步长 1000），按 Pulse/s ÷ CPR_ARM(10000) × 2π 得
        /// 0/0.628/1.257/1.885 rad/s，与同拍 φ 差分速度上限 1.66 rad/s 一致；按 rpm 换算得 0~314 rad/s，
        /// 比 Ω_arm_max = 12 rad/s 大 20 倍以上，物理上不可能。换算只在 `ControlMath.ArmRadPerSecFromDrivePulsePerSec`
        /// 一处做（rad 单点）。0x606B / 0x6081 也是 Pulse/s，与它同口径；别再把某一个当 rpm 单独换算。
        /// </summary>
        public int ActualVelocity;

        /// <summary>
        /// 0x60FD 数字输入 [UDINT32]，字节偏移 12。bit0 负限位 / bit1 正限位 / bit2 原点 / **bit23 急停** / bit26 探针1 / bit27 探针2。
        /// bit23 是 S11 急停证据源（急停须先把 0x2702 某路设成功能码 8，默认三路被限位/原点占着）。
        /// </summary>
        public uint DigitalInputs;

        /// <summary>0x6061 运行模式状态 [SINT8]，字节偏移 16。必须 == 8（CSP），不是就按模式丢失停机。</summary>
        public sbyte ModesDisplay;
    }

    /// <summary>
    /// GCAN-8200 上 GC1602 双通道 AB 正交计数模块 从站→主站（TxPdo 0x1A03 / Sm3，8 字节）。
    /// 字段顺序照 ESI GCAN-8200.xml 的 GC1602 TxPdo 条目序（0x6003:1/2，DataType=DINT 有符号）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Gcan1602TxPdo
    {
        /// <summary>0x6003:1 CH1 计数值 [counts]，摆杆编码器。DINT 有符号。</summary>
        public int CountCh1;

        /// <summary>0x6003:2 CH2 计数值 [counts]，备用（第二个编码器 / 交叉校验）。DINT 有符号。</summary>
        public int CountCh2;
    }

    /// <summary>
    /// GCAN-8200 上 GC1502 高速计数模块 从站→主站（TxPdo 0x1A03 / Sm3，16 字节）。
    /// 字段顺序照 ESI GCAN-8200.xml 的 GC1502 TxPdo 条目序（0x6003:1..4，DataType=UDINT 无符号）。
    /// GC1502 只有 TxPdo、没有 RxPdo（ESI 的 GC1502 Module 块里无 RxPdo 元素），所以不给它造空输出结构体。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Gcan1502TxPdo
    {
        /// <summary>0x6003:1 CH1 计数值 [counts]，摆杆编码器。UDINT 无符号，32 位回绕。</summary>
        public uint CountCh1;

        /// <summary>0x6003:2 CH1 脉冲频率 [Hz]。只进遥测/诊断做 ω 交叉校验，不进控制律（频率计是平均量）。</summary>
        public uint FreqCh1;

        /// <summary>0x6003:3 CH2 计数值 [counts]，备用（第二个编码器 / 交叉校验）。UDINT 无符号。</summary>
        public uint CountCh2;

        /// <summary>0x6003:4 CH2 脉冲频率 [Hz]，备用。UDINT 无符号。</summary>
        public uint FreqCh2;
    }

    /// <summary>
    /// GC2008 8 路数字量输出切片（RxPdo 0x1600 / Sm2，2 字节）。
    /// ESI 条目 = 8×(0x7000:1..8, BOOL, 1 bit) + 1×(0x0000:9, 8 bit 填充)，合计 16 bit = 2 字节。
    /// 本案例不驱动别人的 DO 通道，这段输出区恒写 0，注释见 PendulumEngine 的写路径。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Gcan2008DoPdo
    {
        /// <summary>bit0..7 = CH1..CH8（0x7000:1..8），bit8..15 为 ESI 填充位。恒写 0。</summary>
        public ushort DoBits;
    }

    /// <summary>
    /// GC1602 计数切片的复位位（RxPdo 0x1600 / Sm2，2 字节）。
    /// ESI 条目 = (0x7000:1, BOOL, 1 bit) + (0x7000:2, BOOL, 1 bit) + (0x0000:5, 14 bit 填充)。
    /// GC1502 没有 RxPdo，这段复位位只在 GC1602 在位时才存在。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Gcan1602ResetPdo
    {
        /// <summary>复位位。bit0 = Reset Counter CH1，bit1 = Reset Counter CH2，bit2..15 保留写 0。</summary>
        public ushort ResetBits;
    }

    /// <summary>
    /// GCAN-8200 在位切片的种类。由 PreOp 读 0xF050 在位模块表实读识别，识别不出就拒绝连接。
    /// 「切片」= 插在 GCAN-8200 背板上的一个模块，种类与 ESI 的 ModuleIdent 一一对应。
    /// <para>
    /// ⚠ 0x00000001 是 0xF050 列表的**结尾字符（terminator）**，不是模块：sub0 是表项总数（含结尾字符），
    /// 真实模块 = sub0 - 1。它不进本枚举，也不参与过程映像字节累加。
    /// </para>
    /// </summary>
    public enum GcanSliceKind : byte
    {
        /// <summary>未识别。出现它就是识别失败，必须拒绝连接。</summary>
        Unknown = 0,

        /// <summary>GC2008 8 路数字量输出（ModuleIdent 2008）。</summary>
        Gc2008Do = 1,

        /// <summary>GC1502 高速计数（ModuleIdent 1502）：4×UDINT，含频率计，无 Rx 复位。</summary>
        Gc1502Counter = 2,

        /// <summary>GC1602 AB 正交计数（ModuleIdent 1602）：2×DINT，带 Rx 复位位。</summary>
        Gc1602Counter = 3
    }

    /// <summary>
    /// GCAN-8200 过程映像期望布局。全部由 0xF050 在位模块表实读 + ESI 条目推得，不是猜的。
    /// 字节偏移按槽位顺序累加：每个模块占自己在 ESI 里声明的输入/输出字节。
    /// 总字节数是**推导值**（各槽位相加），必须与主站实测的过程映像字节数交叉核对，任何一侧写死都不行。
    /// </summary>
    public struct GcanImageLayout
    {
        /// <summary>在位计数切片种类（全机只允许一个计数切片）。</summary>
        public GcanSliceKind CounterSlice;

        /// <summary>是否有 GC2008 8 路 DO 切片。</summary>
        public bool HasDoSlice;

        /// <summary>计数切片在 Inputs 过程映像里的字节偏移。</summary>
        public int CounterInputOffset;

        /// <summary>GC2008 DO 在 Outputs 过程映像里的字节偏移，-1 = 无 DO 切片。</summary>
        public int DoOutputOffset;

        /// <summary>GC1602 复位位在 Outputs 过程映像里的字节偏移，-1 = 无复位（GC1502 无 Rx）。</summary>
        public int ResetOutputOffset;

        /// <summary>Inputs 过程映像总字节（各真实模块输入之和，推导值）。</summary>
        public int InputBytes;

        /// <summary>Outputs 过程映像总字节（各切片输出之和，推导值）。</summary>
        public int OutputBytes;

        /// <summary>0xF050 里各槽位的 Module Ident 原值，按槽位顺序。诊断展示用。</summary>
        public uint[] SlotIdents;

        /// <summary>各槽位推算明细（槽位 → 种类 → 输入/输出字节与偏移）。字节数对不上时原文带进报错。</summary>
        public string SlotDump;
    }

    /// <summary>
    /// CiA402 控制字 / 状态字常量。状态字一律按掩码解析，不要拿原始整数当状态。
    /// </summary>
    public static class CiA402
    {
        /// <summary>控制字：Disable Voltage，直接断使能。故障与急停都写这个。</summary>
        public const ushort CwDisableVoltage = 0x0000;

        /// <summary>
        /// 控制字：Quick Stop（bit2 = 0 低有效，EC1 手册 4.1.3）。受控停第一跳：
        /// 驱动按 0x6085 急停减速度斜坡减速并停在 Quick Stop Active（0x6F == 0x07），
        /// 不直接断力矩（断力矩 = 负载自由滑行）。确认到位后才走 <see cref="CwDisableVoltage"/> 收尾
        /// （离开 QuickStopActive 只允许 0x0000 / 0x000F，0x0006 不是合法迁移）。
        /// </summary>
        public const ushort CwQuickStop = 0x0002;

        /// <summary>控制字：Shutdown。使能序列第一步；受控停最后一跳（QuickStopActive 确认后收尾去使能）。</summary>
        public const ushort CwShutdown = 0x0006;

        /// <summary>控制字：Switch On。使能序列第二步。</summary>
        public const ushort CwSwitchOn = 0x0007;

        /// <summary>
        /// 控制字：Enable Operation。使能序列第三步，禁止跳步一次写它。
        /// CSP 下 0x6040 的 bit4/5/6 全部无效（手册 4.1.3 逐位标注；bit4「新位置点」、bit5「立即触发上升沿」、
        /// bit6「绝对/相对」都是 PP/HM 的语义）——所以使能链只写 0x06 → 0x07 → 0x0F，**不置 bit4**，
        /// 不做 PP 那套 0x2F→0x3F 的 bit5 上升沿握手，直接周期刷 0x607A。
        /// 手册 5.4 里的第 5 步 1Fh（=0Fh+bit4）在 CSP 下与 0Fh 等价，本案例不写 1Fh。
        /// </summary>
        public const ushort CwEnableOperation = 0x000F;

        /// <summary>控制字：Fault Reset，上升沿才生效。</summary>
        public const ushort CwFaultReset = 0x0080;

        /// <summary>运行态状态字掩码 0x6F。</summary>
        public const ushort SwMaskRun = 0x006F;

        /// <summary>故障态状态字掩码 0x4F。</summary>
        public const ushort SwMaskFault = 0x004F;

        /// <summary>
        /// Switch On Disabled 判定掩码 0x4F。按 CiA402 标准该态只看 bit0..3 与 bit6，
        /// bit5（Quick Stop）不参与——用 0x6F 判会把 bit5=0 的 Switch On Disabled 漏成「未识别」。
        /// </summary>
        public const ushort SwMaskSwitchOnDisabled = 0x004F;

        /// <summary>状态字：Switch On Disabled（0x6F == 0x40）。</summary>
        public const ushort SwSwitchOnDisabled = 0x0040;

        /// <summary>状态字：Ready To Switch On（0x6F == 0x21）。</summary>
        public const ushort SwReadyToSwitchOn = 0x0021;

        /// <summary>状态字：Switched On（0x6F == 0x23）。</summary>
        public const ushort SwSwitchedOn = 0x0023;

        /// <summary>状态字：Operation Enabled（0x6F == 0x27）。S3 就盯这个。</summary>
        public const ushort SwOperationEnabled = 0x0027;

        /// <summary>状态字：Fault Reaction Active（0x4F == 0x0F）。</summary>
        public const ushort SwFaultReactionActive = 0x000F;

        /// <summary>状态字：Fault（0x4F == 0x08）。</summary>
        public const ushort SwFault = 0x0008;

        /// <summary>状态字 bit5：Quick Stop 输入位，0 = 急停/STO 已动作。S11 盯这个。</summary>
        public const ushort SwQuickStopBit = 0x0020;

        /// <summary>状态字 bit12：跟随主站。CSP 下驱动器未使能、或不再跟随主站指令时置 0（手册 5.2.2）。</summary>
        public const ushort SwFollowingMasterBit = 0x1000;

        /// <summary>状态字 bit9：远程。ProOP 以下为 0，此时 0x6040 命令不被执行（手册 5.2.2）。</summary>
        public const ushort SwRemoteBit = 0x0200;

        /// <summary>
        /// 0x60FD 数字输入 bit23：急停输入（0x2702 可编程输入功能码 8）。
        /// ⚠ 默认三路输入被负限位 / 正限位 / 原点占着（0x2702:01..03 = 4/2/1），**急停默认未启用**；
        /// 要接 S11 得牺牲一路（通常把 X2 原点改成 8，回零改用 HM 方式 35/37）。
        /// 响应量级 ≥10 ms（输入脉宽 >10 ms + 0x2703 滤波默认 1000 µs）——只能当 10 ms 级保护，
        /// 不能当 1 ms 级保护用，真正的硬线保护不经过驱动器。
        /// </summary>
        public const uint DiEStopInputBit = 0x00800000u;

        /// <summary>
        /// CSP 模式号 0x6060/0x6061。
        /// 0x6060 表格的「范围=8 / 默认值=0」两列是手册排版缺陷（05-EC1-手册要点 §2.1 / §10#9），
        /// 禁止硬编码"默认就是 CSP"：一律写 0x6060=8 并以 0x6061 回读确认。
        /// </summary>
        public const sbyte ModeCsp = 8;

        /// <summary>状态字是否已到 Operation Enabled。</summary>
        public static bool IsOperationEnabled(ushort statusWord)
        {
            return (statusWord & SwMaskRun) == SwOperationEnabled;
        }

        /// <summary>状态字是否在 Switch On Disabled（可以从 0x06 起步）。</summary>
        public static bool IsSwitchOnDisabled(ushort statusWord)
        {
            return (statusWord & SwMaskSwitchOnDisabled) == SwSwitchOnDisabled;
        }

        /// <summary>状态字是否在 Ready To Switch On（该发 0x07）。</summary>
        public static bool IsReadyToSwitchOn(ushort statusWord)
        {
            return (statusWord & SwMaskRun) == SwReadyToSwitchOn;
        }

        /// <summary>状态字是否在 Switched On（该发 0x0F）。</summary>
        public static bool IsSwitchedOn(ushort statusWord)
        {
            return (statusWord & SwMaskRun) == SwSwitchedOn;
        }

        /// <summary>状态字是否报故障（含 Fault Reaction Active）。</summary>
        public static bool IsFault(ushort statusWord)
        {
            return (statusWord & SwMaskFault) == SwFault ||
                   (statusWord & SwMaskFault) == SwFaultReactionActive;
        }

        /// <summary>状态字：Quick Stop Active（0x6F == 0x07）。</summary>
        public const ushort SwQuickStopActive = 0x0007;

        /// <summary>
        /// 状态字是否已停在 Quick Stop Active（受控停斜坡已停）。
        /// 离开该态只允许 Disable Voltage（0x0000）或 Enable Operation（0x000F）；
        /// 0x0006 Shutdown 不是 Quick Stop Active 的合法迁移。
        /// </summary>
        public static bool IsQuickStopActive(ushort statusWord)
        {
            return (statusWord & SwMaskRun) == SwQuickStopActive;
        }

        /// <summary>
        /// 硬接线急停 / STO 已动作的证据之一（S11 · 状态字侧）。
        /// 判据：
        /// ① 驱动掉进 Quick Stop Active（0x6F == 0x07）——急停/STO 动作的典型表现；
        /// ② 驱动自称已使能但 Quick Stop 位(bit5) 拉低——状态字自相矛盾，按动作处理。
        /// 其余状态（Switch On Disabled / Not Ready）里 bit5 本来就没定义，不作证据，避免误判。
        /// </summary>
        public static bool IsSafetyStopAsserted(ushort statusWord)
        {
            ushort run = (ushort)(statusWord & SwMaskRun);
            if (run == SwQuickStopActive) return true;
            if (run == SwReadyToSwitchOn || run == SwSwitchedOn || run == SwOperationEnabled)
                return (statusWord & SwQuickStopBit) == 0;
            return false;
        }

        /// <summary>
        /// 硬接线急停输入的证据之二（S11 · 数字输入侧，EC1 的 0x60FD Bit23）。
        /// 位 = 1 = 急停功能已动作（0x2701 极性配置决定有效电平，接常闭开关时要按现场核对极性）。
        /// 状态字那条判不了"驱动已去使能时急停仍闭合"，本条是那个盲区的直接证据。
        /// </summary>
        public static bool IsDigitalEStopAsserted(uint digitalInputs)
        {
            return (digitalInputs & DiEStopInputBit) != 0;
        }

        /// <summary>状态字 bit12「跟随主站」是否置位。CSP 健康度的直接证据，进诊断。</summary>
        public static bool IsFollowingMaster(ushort statusWord)
        {
            return (statusWord & SwFollowingMasterBit) != 0;
        }

        /// <summary>状态字 bit9「远程」是否置位。置 0 说明还在 ProOP 以下，0x6040 命令不会被执行，进诊断。</summary>
        public static bool IsRemote(ushort statusWord)
        {
            return (statusWord & SwRemoteBit) != 0;
        }
    }

    /// <summary>
    /// PDO 结构体字节数自检。字节数一律不写死：拿 Marshal.SizeOf 跟总线实测的字节数对，
    /// 对不上就如实报错并拒绝连接，绝不猜、绝不迁就。
    /// </summary>
    public static class PdoStructs
    {
        /// <summary>GC2008 8 路 DO 切片在过程映像里占的输出字节。</summary>
        public const int Gc2008OutputBytes = 2;

        /// <summary>GC1502 高速计数切片的输入字节（4×UDINT）。</summary>
        public const int Gc1502InputBytes = 16;

        /// <summary>GC1602 AB 正交计数切片的输入字节（2×DINT）。</summary>
        public const int Gc1602InputBytes = 8;

        /// <summary>GC1602 计数切片的输出字节（2 字节复位位）。GC1502 无 Rx，输出 0 字节。</summary>
        public const int Gc1602OutputBytes = 2;

        /// <summary>0xF050 在位模块表里 GC2008 的 Module Ident。</summary>
        public const uint IdentGc2008 = 2008;

        /// <summary>0xF050 在位模块表里 GC1502 的 Module Ident。</summary>
        public const uint IdentGc1502 = 1502;

        /// <summary>0xF050 在位模块表里 GC1602 的 Module Ident。</summary>
        public const uint IdentGc1602 = 1602;

        /// <summary>
        /// 0xF050 列表的**结尾字符（terminator）**原值。只有出现在列表末尾时才按结尾字符处理，
        /// 出现在中间就是一台不认识的模块（照 Unknown fail closed）。
        /// ESI 的 <c>ModuleIdent="0001"</c> 与它同值，但工控现场的 0xF050 用它收尾 ——
        /// 判据是"位置 + 值"，不是"看到 1 就当模块"。
        /// </summary>
        public const uint IdentGc0001 = 0x0001;

        /// <summary>
        /// 把 0xF050 读回来的 Module Ident 原值映射成切片种类。
        /// ESI 的 ModuleIdent 写作十进制字面量（1502/1602/2008），但实机回读的编码形式不预设，
        /// 十进制与十六进制两种位型都认；认不出返回 Unknown，由调用方 fail closed 拒绝连接。
        /// **结尾字符（<see cref="IdentGc0001"/>）不是模块**，它必须先被调用方按位置剔掉，
        /// 绝不在这里被映射成切片，否则会凭空多累加字节数。
        /// 识别结果还要跟 Inputs/Outputs 字节数交叉验证，两边对不上照样拒。
        /// </summary>
        public static GcanSliceKind MapSliceKind(uint ident)
        {
            if (ident == IdentGc2008 || ident == 0x2008) return GcanSliceKind.Gc2008Do;
            if (ident == IdentGc1502 || ident == 0x1502) return GcanSliceKind.Gc1502Counter;
            if (ident == IdentGc1602 || ident == 0x1602) return GcanSliceKind.Gc1602Counter;
            return GcanSliceKind.Unknown;
        }

        /// <summary>切片种类的可读名，诊断与报错用。</summary>
        public static string DescribeSlice(GcanSliceKind kind)
        {
            switch (kind)
            {
                case GcanSliceKind.Gc2008Do: return "GC2008 8 路 DO";
                case GcanSliceKind.Gc1502Counter: return "GC1502 高速计数";
                case GcanSliceKind.Gc1602Counter: return "GC1602 AB 正交计数";
                default: return "未知切片";
            }
        }

        /// <summary>
        /// 切片在过程映像里占的输入字节，按 ESI 各模块 TxPdo 的位宽推得（填充位也计入）。
        /// Unknown / GC2008（只有输出）返回 0。偏移累加与结构体自检都走这里，不另写一份字节表。
        /// </summary>
        public static int SliceInputBytes(GcanSliceKind kind)
        {
            switch (kind)
            {
                case GcanSliceKind.Gc1502Counter: return Gc1502InputBytes;
                case GcanSliceKind.Gc1602Counter: return Gc1602InputBytes;
                default: return 0;
            }
        }

        /// <summary>切片在过程映像里占的输出字节，按 ESI 各模块 RxPdo 的位宽推得。只有 GC2008 与 GC1602 有 RxPdo。</summary>
        public static int SliceOutputBytes(GcanSliceKind kind)
        {
            switch (kind)
            {
                case GcanSliceKind.Gc2008Do: return Gc2008OutputBytes;
                case GcanSliceKind.Gc1602Counter: return Gc1602OutputBytes;
                default: return 0;
            }
        }

        /// <summary>
        /// 切片输入结构体的字节数（自检用，应等于 <see cref="SliceInputBytes"/>）。
        /// 结构体一旦与 ESI 漂了，这里当场暴露，不让偏移静默错位。
        /// </summary>
        public static int SliceInputStructBytes(GcanSliceKind kind)
        {
            switch (kind)
            {
                case GcanSliceKind.Gc1502Counter: return Marshal.SizeOf(typeof(Gcan1502TxPdo));
                case GcanSliceKind.Gc1602Counter: return Marshal.SizeOf(typeof(Gcan1602TxPdo));
                default: return 0;
            }
        }

        /// <summary>
        /// 0xF050 原始表项 → 真实模块 ident 列表：**按位置剔掉末尾结尾字符**。
        /// <para>
        /// 判据（用户 2026-11-10 现场裁定）：<c>0xF050:00</c> 是**表项总数（含结尾字符）**，真实模块 = sub0 - 1；
        /// 末尾的 <c>0x00000001</c> 是结尾字符，不是模块。因此**只当末项的值恰好等于
        /// <see cref="IdentGc0001"/> 时丢弃最后一项**；末项是 2008 / 1502 / 1602 等真实 ident 时原样保留
        /// （不能按"数少一个"硬砍，那样会把一台真实模块当成结尾字符扔掉）。
        /// </para>
        /// 现场实测：<c>0xF050 = [2, 1502, 1]</c> → 真实模块 GC1502 一个 → In=16B / Out=0B，与设备 0x1C13 只映射 0x1A03 吻合。
        /// </summary>
        public static uint[] StripIdentTerminator(uint[] slotIdents)
        {
            if (slotIdents == null || slotIdents.Length == 0) return new uint[0];
            if (slotIdents[slotIdents.Length - 1] != IdentGc0001) return slotIdents;
            uint[] real = new uint[slotIdents.Length - 1];
            Array.Copy(slotIdents, real, real.Length);
            return real;
        }

        /// <summary>
        /// 按 0xF050 回读的 Module Ident 序列（槽位顺序）推 GCAN 过程映像布局。
        /// 先按位置剔掉末尾结尾字符（见 <see cref="StripIdentTerminator"/>），再让每个真实模块
        /// 占自己在 ESI 里声明的输入 / 输出字节，偏移按槽位顺序累加 —— 布局完全由在位模块推导，
        /// 不写死任何组合的总字节数（现场 GC1502 单模块 = In 16B / Out 0B）。
        /// 认不出的 ident、结尾字符以外的 0x00000001、计数切片不是恰好 1 个、结构体字节数与 ESI 不符 —— 一律 fail closed，禁止猜。
        /// summary / layout.SlotDump 里如实写出每个槽位识别成了什么、字节数怎么累加出来的。
        /// </summary>
        public static bool BuildGcanLayout(
            uint[] slotIdents,
            out GcanImageLayout layout,
            out string summary,
            out string error)
        {
            layout = default(GcanImageLayout);
            layout.DoOutputOffset = -1;
            layout.ResetOutputOffset = -1;
            summary = null;
            error = null;
            if (slotIdents == null || slotIdents.Length == 0)
            {
                error = "GCAN 在位模块表为空，切片识别不出，拒绝连接。";
                return false;
            }

            // 0xF050:00 是表项总数（含结尾字符）：末项 0x00000001 是结尾字符，不是模块，先剔掉再算字节。
            // 判据按值与位置（见 StripIdentTerminator），不按"固定少一个"硬砍。
            bool hadTerminator = slotIdents[slotIdents.Length - 1] == IdentGc0001;
            uint[] realIdents = StripIdentTerminator(slotIdents);
            if (realIdents.Length == 0)
            {
                error = string.Format(
                    "GCAN 0xF050 只有结尾字符 0x{0:X8}，没有任何真实模块（sub0={1} ⇒ 真实模块 0 个），拒绝连接。",
                    IdentGc0001, slotIdents.Length);
                return false;
            }

            System.Text.StringBuilder dump = new System.Text.StringBuilder();
            int inOffset = 0;
            int outOffset = 0;
            int counterCount = 0;
            GcanSliceKind counterSlice = GcanSliceKind.Unknown;

            for (int slot = 0; slot < realIdents.Length; slot++)
            {
                uint ident = realIdents[slot];
                GcanSliceKind kind = MapSliceKind(ident);
                if (kind == GcanSliceKind.Unknown)
                {
                    error = string.Format(
                        "GCAN 槽位 {0} 的 Module Ident=0x{1:X8}（{1}）不认识，拒绝连接。已知：GC2008={2} / GC1502={3} / GC1602={4}；" +
                        "0x{5:X4} 只允许出现在列表末尾并作为结尾字符（出现在这里是中间项，不是结尾字符）。",
                        slot + 1, ident, IdentGc2008, IdentGc1502, IdentGc1602, IdentGc0001);
                    return false;
                }

                int inBytes = SliceInputBytes(kind);
                int outBytes = SliceOutputBytes(kind);
                int inAt = inOffset;
                int outAt = outOffset;

                if (dump.Length > 0) dump.Append("；");
                dump.AppendFormat("槽位{0}(0xF050:{1})={2} ident=0x{3:X8}：输入 {4}B{5} / 输出 {6}B{7}",
                    slot + 1, slot + 1, DescribeSlice(kind), ident,
                    inBytes, inBytes > 0 ? "@" + inAt : string.Empty,
                    outBytes, outBytes > 0 ? "@" + outAt : string.Empty);

                if (kind == GcanSliceKind.Gc2008Do)
                {
                    layout.HasDoSlice = true;
                    layout.DoOutputOffset = outAt;
                }
                else
                {
                    counterCount++;
                    counterSlice = kind;
                    layout.CounterSlice = kind;
                    layout.CounterInputOffset = inAt;
                    if (outBytes > 0)
                    {
                        layout.ResetOutputOffset = outAt;
                    }
                }

                inOffset += inBytes;
                outOffset += outBytes;
            }

            if (counterCount != 1)
            {
                error = string.Format(
                    "GCAN 上的计数切片有 {0} 个（要求恰好 1 个，摆杆计数该接哪个不明确），拒绝连接。实读：{1}",
                    counterCount, dump);
                return false;
            }
            if (SliceInputStructBytes(counterSlice) != SliceInputBytes(counterSlice))
            {
                error = string.Format(
                    "计数切片 {0} 的输入结构体 {1}B 与 ESI 声明的 {2}B 不一致，拒绝连接。",
                    DescribeSlice(counterSlice), SliceInputStructBytes(counterSlice), SliceInputBytes(counterSlice));
                return false;
            }

            if (hadTerminator)
            {
                dump.AppendFormat("（0xF050 末项 0x{0:X8} 为结尾字符，已剔除；真实模块 {1} 个）",
                    IdentGc0001, realIdents.Length);
            }
            layout.SlotIdents = realIdents;
            layout.InputBytes = inOffset;
            layout.OutputBytes = outOffset;
            layout.SlotDump = dump.ToString();
            summary = string.Format("{0} → Inputs={1}B / Outputs={2}B", layout.SlotDump, inOffset, outOffset);
            return true;
        }

        /// <summary>
        /// 两段步进 PDO 的实测字节数与结构体逐一比对，再按 0xF050 识别出的切片布局比对 GCAN 的输入/输出字节数。
        /// stepperOutBytes/stepperInBytes 是摆臂步进从站的 Outputs/Inputs 字节数（Rx 侧 0x6040 起 7B / Tx 侧 0x603F 起 17B）；
        /// gcanOutBytes/gcanInBytes 是 GCAN 从站的实测 Outputs/Inputs 字节数，gcan 是由 0xF050 在位模块**逐个累加**推得的期望布局。
        /// 任一项不等即 false，reason 里写清是哪一段、推导值 / 实测值各是多少、各槽位明细是什么。
        /// </summary>
        public static bool VerifySizes(
            int stepperOutBytes,
            int stepperInBytes,
            in GcanImageLayout gcan,
            int gcanOutBytes,
            int gcanInBytes,
            out string reason)
        {
            int stepperRx = Marshal.SizeOf(typeof(StepperRxPdo));
            int stepperTx = Marshal.SizeOf(typeof(StepperTxPdo));

            if (stepperRx != stepperOutBytes)
            {
                reason = string.Format(
                    "步进 RxPDO 字节数不符：结构体 {0}B，总线实测 Outputs {1}B。GR-EC01 ESI 的 0x1600（Sm2，7B）与 StepperRxPdo 字段顺序不一致。",
                    stepperRx, stepperOutBytes);
                return false;
            }
            if (stepperTx != stepperInBytes)
            {
                reason = string.Format(
                    "步进 TxPDO 字节数不符：结构体 {0}B，总线实测 Inputs {1}B。GR-EC01 ESI 的 0x1A00（Sm3，17B=136bit）与 StepperTxPdo 字段顺序不一致。",
                    stepperTx, stepperInBytes);
                return false;
            }
            if (gcan.CounterSlice == GcanSliceKind.Unknown)
            {
                reason = "GCAN 计数切片未识别，过程映像字节数无从核对。";
                return false;
            }
            string slotDump = string.IsNullOrEmpty(gcan.SlotDump) ? "（无槽位明细）" : gcan.SlotDump;
            if (gcan.OutputBytes != gcanOutBytes)
            {
                reason = string.Format(
                    "GCAN Outputs 字节数不符：按 0xF050 在位模块逐个累加推导 {0}B，主站过程映像实测 {1}B。各槽位明细：{2}。切片识别与过程映像对不上，拒绝连接。",
                    gcan.OutputBytes, gcanOutBytes, slotDump);
                return false;
            }
            if (gcan.InputBytes != gcanInBytes)
            {
                reason = string.Format(
                    "GCAN Inputs 字节数不符：按 0xF050 在位模块逐个累加推导 {0}B，主站过程映像实测 {1}B。各槽位明细：{2}。切片识别与过程映像对不上，拒绝连接。",
                    gcan.InputBytes, gcanInBytes, slotDump);
                return false;
            }
            int counterStruct = SliceInputStructBytes(gcan.CounterSlice);
            if (counterStruct != SliceInputBytes(gcan.CounterSlice))
            {
                reason = string.Format(
                    "计数切片 {0} 的输入结构体 {1}B 与 ESI 声明的 {2}B 不一致。",
                    DescribeSlice(gcan.CounterSlice), counterStruct, SliceInputBytes(gcan.CounterSlice));
                return false;
            }
            int doStruct = gcan.HasDoSlice ? Marshal.SizeOf(typeof(Gcan2008DoPdo)) : 0;
            if (gcan.HasDoSlice && doStruct != Gc2008OutputBytes)
            {
                reason = string.Format(
                    "GC2008 DO 输出结构体 {0}B 与 ESI 声明的 {1}B 不一致。", doStruct, Gc2008OutputBytes);
                return false;
            }
            int resetStruct = gcan.ResetOutputOffset >= 0 ? Marshal.SizeOf(typeof(Gcan1602ResetPdo)) : 0;
            if (gcan.ResetOutputOffset >= 0 && resetStruct != Gc1602OutputBytes)
            {
                reason = string.Format(
                    "GC1602 复位结构体 {0}B 与 ESI 声明的 {1}B 不一致。", resetStruct, Gc1602OutputBytes);
                return false;
            }
            reason = null;
            return true;
        }
    }
}
