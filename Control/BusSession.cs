using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using DarraEtherCAT_Master;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 从 DENI 里只读解析出来的身份信息。DENI 是唯一配置真相，这里只读不写、不用 ESI 冒充。
    /// </summary>
    internal sealed class DeniIdentity
    {
        /// <summary>主站网卡 MAC，来自 DENI 的 Master/Info/Source，12 位十六进制。</summary>
        public string MasterMac;

        /// <summary>
        /// DENI 的 Master/MasterConfig/deni:Interface —— 导出时选中的网卡标识
        /// （pcap 设备名 <c>\Device\NPF_{GUID}</c>，也见过直接写 Windows 友好名）。
        /// 只给「克隆部署按名字回退」用：正常连接只认 <see cref="MasterMac"/>。缺了就是空串。
        /// </summary>
        public string InterfaceName;

        /// <summary>DENI 的 Master/MasterConfig/deni:InterfaceDescription —— 导出时的网卡描述。缺了就是空串。</summary>
        public string InterfaceDescription;

        /// <summary>DENI 的 Master/MasterConfig/deni:InterfaceMAC —— 导出时的网卡 MAC（与 Source 同源，独立留一份做交叉核对）。</summary>
        public string InterfaceMac;

        /// <summary>从站身份表，顺序与总线扫描顺序一致。</summary>
        public List<DeniSlaveIdentity> Slaves = new List<DeniSlaveIdentity>();
    }

    /// <summary>一个从站的 DENI 身份。</summary>
    internal struct DeniSlaveIdentity
    {
        public string Name;
        public uint VendorId;
        public uint ProductId;
        public uint RevisionNo;

        /// <summary>
        /// DENI 里该从站是否导出了 <c>&lt;ProcessData&gt;</c>（PDO 分配）。缺了它主站不会给该站映射过程映像，
        /// 实测 Outputs/Inputs 会全是 0B —— 这时如实报「DENI 缺该站 PDO 分配」，不要把 0B 当成"设备没 PDO"。
        /// </summary>
        public bool HasProcessData;
    }

    /// <summary>
    /// DENI 只读身份解析。文件可能是 UTF-8 带 BOM，也可能声明 utf-16 —— 先看字节头定编码再解析，
    /// 不信 XML 声明里的 encoding（实测导出的 DENI 声明跟字节不一致）。
    /// </summary>
    internal static class DeniIdentityReader
    {
        public static bool TryRead(string path, out DeniIdentity identity, out string error)
        {
            identity = null;
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "DENI 路径为空。";
                return false;
            }
            if (!System.IO.File.Exists(path))
            {
                error = string.Format("DENI 文件不存在：{0}", path);
                return false;
            }

            string text;
            try
            {
                byte[] raw = System.IO.File.ReadAllBytes(path);
                text = Decode(raw);
            }
            catch (Exception ex)
            {
                error = "读 DENI 失败：" + DiagText.Describe(ex);
                return false;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch (Exception ex)
            {
                error = "DENI 不是合法 XML：" + DiagText.Describe(ex);
                return false;
            }

            XElement root = doc.Root;
            if (root == null || root.Name.LocalName != "EtherCATConfig")
            {
                error = "DENI 根节点不是 EtherCATConfig。";
                return false;
            }
            XElement config = root.Element("Config");
            XElement master = config == null ? null : config.Element("Master");
            XElement masterInfo = master == null ? null : master.Element("Info");
            XElement source = masterInfo == null ? null : masterInfo.Element("Source");
            if (source == null || string.IsNullOrWhiteSpace(source.Value))
            {
                error = "DENI 里没有 Master/Info/Source（网卡 MAC），无法做身份预检。";
                return false;
            }

            identity = new DeniIdentity();
            identity.MasterMac = NormalizeMac(source.Value);

            // 网卡名 / 描述 / InterfaceMAC：只有「克隆部署按名字回退」这一个消费者，
            // 正常连接（按 MAC 认卡）完全不看它们。缺了留空，不猜。
            XElement masterConfig = FindChildByLocalName(master, "MasterConfig");
            if (masterConfig != null)
            {
                identity.InterfaceName = ReadTextAnyNamespace(masterConfig, "Interface");
                identity.InterfaceDescription = ReadTextAnyNamespace(masterConfig, "InterfaceDescription");
                identity.InterfaceMac = NormalizeMac(ReadTextAnyNamespace(masterConfig, "InterfaceMAC"));
            }

            if (config == null)
            {
                error = "DENI 里没有 Config 节点。";
                return false;
            }
            foreach (XElement slave in config.Elements("Slave"))
            {
                XElement info = slave.Element("Info");
                if (info == null) continue;
                DeniSlaveIdentity item = new DeniSlaveIdentity();
                item.Name = ReadText(info, "Name");
                item.HasProcessData = slave.Element("ProcessData") != null;
                if (!TryReadHex(info, "VendorId", out item.VendorId) ||
                    !TryReadHex(info, "ProductCode", out item.ProductId) ||
                    !TryReadHex(info, "RevisionNo", out item.RevisionNo))
                {
                    error = string.Format(
                        "DENI 从站 {0} 的 VendorId/ProductCode/RevisionNo 缺失或不是十六进制。",
                        item.Name);
                    identity = null;
                    return false;
                }
                identity.Slaves.Add(item);
            }
            if (identity.Slaves.Count == 0)
            {
                error = "DENI 里没有从站身份条目。";
                identity = null;
                return false;
            }
            return true;
        }

        private static string Decode(byte[] raw)
        {
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                return System.Text.Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
                return System.Text.Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
            return System.Text.Encoding.UTF8.GetString(raw);
        }

        private static string ReadText(XElement parent, string name)
        {
            XElement e = parent.Element(name);
            return e == null ? string.Empty : (e.Value ?? string.Empty).Trim();
        }

        /// <summary>
        /// 按 localName 找直接子元素，不看命名空间前缀。
        /// DENI 里 from站段是无前缀元素，而 Master/MasterConfig 下的网卡字段带 <c>deni:</c> 前缀
        /// （&lt;deni:Interface&gt;），只按前缀名找会漏，所以这两处统一走本方法。
        /// </summary>
        private static XElement FindChildByLocalName(XElement parent, string localName)
        {
            if (parent == null) return null;
            foreach (XElement child in parent.Elements())
            {
                if (string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
                    return child;
            }
            return null;
        }

        /// <summary>按 localName 读直接子元素的文本（忽略命名空间前缀），没找到返回空串。</summary>
        private static string ReadTextAnyNamespace(XElement parent, string localName)
        {
            XElement e = FindChildByLocalName(parent, localName);
            return e == null ? string.Empty : (e.Value ?? string.Empty).Trim();
        }

        private static bool TryReadHex(XElement parent, string name, out uint value)
        {
            value = 0;
            XElement e = parent.Element(name);
            if (e == null) return false;
            return TryParseHex(e.Value, out value);
        }

        /// <summary>DENI 里的十六进制有两种写法：#x00000168 与 0x00001164，都得认。</summary>
        internal static bool TryParseHex(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        internal static string NormalizeMac(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>把 MAC 排成 aa:bb:cc:dd:ee:ff，诊断 / 报文好读。不是 6 字节就原样带回，不造数。</summary>
        internal static string FormatMac(string text)
        {
            string mac = NormalizeMac(text);
            if (mac.Length != 12) return text ?? string.Empty;
            var sb = new System.Text.StringBuilder(17);
            for (int i = 0; i < 12; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append(mac, i, 2);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 摆臂驱动器的标定事实，PreOp/慢路径实读。全部如实带回，读不到就是读不到，不猜、不写死默认值。
    /// </summary>
    internal struct ArmDriveFacts
    {
        /// <summary>0x2102 开闭环细分设置 = 电机单圈脉冲数 P（手册范围 200~60000）。0 = 读不到。</summary>
        public int MotorCountsPerRev;

        /// <summary>0x2003:01 最小同步周期 [µs]。0 = 读不到。</summary>
        public int MinSyncCycleUs;

        /// <summary>0x2003:02 最大同步周期 [µs]。0 = 读不到。</summary>
        public int MaxSyncCycleUs;

        /// <summary>0x608F:01 每转脉冲。EC1 全系无此对象，只有别的机型才有值。</summary>
        public int PprFromOd;

        /// <summary>0x608F:02 负载侧（feed）每转增量。0 = 读不到（EC1 全系无此对象）。</summary>
        public int FeedPprFromOd;

        /// <summary>0x6091:01 齿轮比·电机转数。0 = 读不到（EC1 全系无此对象）。</summary>
        public uint GearMotorRevs;

        /// <summary>0x6091:02 齿轮比·负载转数。0 = 读不到（EC1 全系无此对象）。</summary>
        public uint GearFeedRevs;

        /// <summary>0x608F 是否真读到了。false = 机型没有或读失败，交叉校验跳过（EC1 全系无 0x608F，这是常态不是错误）。</summary>
        public bool PprReadable;

        /// <summary>
        /// 按驱动器自己的 OD 算「摆臂转一圈 = 多少个位置单位」：0x608F:01 × (0x6091:01 ÷ 0x6091:02)。
        /// 0x6064 / 0x606C 用的就是这套位置单位，所以它同时是位置换算和速度换算的分母。
        /// 三件缺一（EC1 全系没有 0x608F / 0x6091）或比值为 0 返回 0 —— 由调用方回落 0x2102 标定，不猜。
        /// </summary>
        public double OdCountsPerArmRev
        {
            get
            {
                if (PprFromOd <= 0 || GearMotorRevs == 0 || GearFeedRevs == 0) return 0;
                return (double)PprFromOd * GearMotorRevs / GearFeedRevs;
            }
        }

        /// <summary>各对象读不到时的实况（含 AbortCode），诊断用。全读到 = null。</summary>
        public string Notes;
    }

    /// <summary>
    /// 总线会话：唯一碰 EtherCAT SDK 的地方。
    /// 慢操作（DENI 预检、Build、状态转换、SDO、teardown）全在这里，周期路径只做 PDO 读写与 WKC 读取。
    /// </summary>
    internal sealed class BusSession : IDisposable
    {
        // 广成 GCAN-8200 的 Vendor Id，来自 其他依赖文件/ESI/GCAN-8200.xml（#x00000E0D）。
        // 用它把耦合器从站跟步进从站分开，不猜顺序。
        private const uint GcanVendorId = 0x00000E0D;

        // GR-EC01 / EC1 系列的 Vendor Id（Green IOT 格睿物联，ESI L4 = #x0AB5；手册 4.1.1 的 0x1018:01 同值）。
        // 摆臂步进的 0x1018:1 实读对不上就拒连，不做"可能是别的牌子也行"的猜测。
        private const uint GreenIotVendorId = 0x0AB5;

        // EC1 这台从站的 SYNC0 周期 [ns]。手册 0x2003:01 最小同步周期 1000µs（越窗报 0x8402 指令超速），
        // 所以这台从站固定 1 ms —— 主站 LoopCycle 仍是 500µs（θ 环 2 kHz 不降），同步周期按从站区分。
        // 引擎做 0x2003 窗口自检时也拿这个值比，别在两处各写一份。
        internal const uint ArmSync0CycleNs = 1000000;

        // teardown 固定期限：总 20 s，其中 0x6040=0 上限 7 s。
        private const int TeardownTotalMs = 20000;
        private const int DisableVoltageTimeoutMs = 7000;

        /// <summary>
        /// 连接期 PDO 输入映像就绪门的等待上限 [ms]。native <c>Start()</c> 已经在
        /// <c>pdo_publish_primed</c>（会话 priming）与 iomap 可写之前不放行（预算 5000 ms），
        /// 这里是在 OP 之后再做一个**应用侧**的显式确认：控制线程第一拍之前，两个自有从站的
        /// 输入映像必须连续可读。正常情况下等待接近 0 ms；到点仍不可读按配置错误拒连，
        /// 绝不把「映像还没就绪」留给控制线程去撞。
        /// </summary>
        private const int PdoReadyTimeoutMs = 2000;

        /// <summary>就绪判据要求连续可读的采样次数（2 次 ≈ 2 个周期，跨过单拍抖动）。</summary>
        private const int PdoReadyConsecutiveSamples = 2;

        private DarraEtherCAT _master;
        private DarraEtherCAT.Slave _stepper;
        private DarraEtherCAT.Slave _gcan;
        private DarraEtherCAT.SlavePdo _stepperPdo;
        private DarraEtherCAT.SlavePdo _gcanPdo;
        private int _stepperIndex = -1;
        private int _gcanIndex = -1;

        // 连接时实际用的网卡接口名（显式给名的匹配结果 / 空 NicName 的自动匹配结果）。没连过 = 空。
        private string _nicName;

        // 本次连接用的 DENI 路径（Open 时留底，只供诊断 / 界面显示配置来源，不参与任何配置决策）。
        private string _deniPath;

        // 进程内网卡解析缓存：DENI Source MAC（规范化 12 位小写）→ 已验证的解析结果。
        // 同一个进程里 MAC 与网卡名不会变（DENI 也按路径区分），重连时直接复用，
        // 跳过 GetAllNetworkInterfaces + GetNetworkAdapters 两次慢枚举（实测约 1.08 s）。解析失败不写。
        private struct NicResolution
        {
            public string NicName;
            public string DeniPath;
        }

        private static readonly object NicCacheGate = new object();
        private static readonly Dictionary<string, NicResolution> NicCache =
            new Dictionary<string, NicResolution>(StringComparer.Ordinal);

        // GCAN 模块 RxPdo 映射对象的 SDO 可读性「已实测能力事实」缓存（进程内 static）：
        //   键 = VendorId:ProductId:RevId:ModuleIdent:RxPdo（唯一拼装点 = GcanRxMappingFactKey）；
        //   值 = 0（可读）或确定性 abort（当前只收 0x06020000）。
        // 生命周期：跟随进程，跨 Connect/Disconnect 保留 —— 能力是「型号 + 修订级」的设备事实，不随一次连接失效；
        //   换型号 / 换修订（RevId 变）自动换键 → 重新实测；进程重启自然清空。
        // 为什么可以跨连接保留：它只记录"设备明确回答过的事实"，不是某次通讯的成败 ——
        //   超时 / 传输失败 / 状态不允许这类动态故障不入缓存，下次仍如实重试。
        // 目的：GC1602 的 0x1600:00 在 GCAN-8200 上不可 SDO 读（实测 abort 0x06020000），
        //   同型号+同修订只探一次，后续连接按这条事实判定，不再每次连都发一条必失败的 SDO。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> GcanRxMappingAbortCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, uint>();

        /// <summary>0x06020000 = Object does not exist（CiA 301 Table 22），设备明确"没有这个对象"。</summary>
        private const uint SdoAbortObjectDoesNotExist = 0x06020000u;

        /// <summary>
        /// 克隆部署回退开关（默认 false = fail closed）：DENI 记录的网卡 MAC 在本机不存在时，
        /// 允许按 DENI 记录的网卡名 / 描述在本机认唯一一张网卡。认错卡会把主站绑到另一张连着
        /// 别的设备的网卡上，所以它只能由命令行显式开关 <c>--allow-nic-name-fallback</c> 打开，
        /// 绝不默认放宽；回退发生时会走诊断大声留痕（DENI MAC → 实际网卡名 + MAC）。
        /// 由 Program.cs 在启动期按命令行设置一次（GUI 也可用 <c>--gui --allow-nic-name-fallback</c>）。
        /// </summary>
        internal static bool AllowNicNameFallback;

        /// <summary>
        /// 摆臂从站 DC（Distributed Clocks / SYNC0）开关。**默认关闭**（<c>null</c> = 未显式指定）。
        /// <para>
        /// 关闭的理由（2026-11-20 台架实测）：DENI 的 <c>deni:DistributedClocks enabled=false</c>、
        /// 步进 <c>HasDC=false</c>、<c>Sync0CycleNs=0</c> 时总线照样进 OP，使能耗时从 166 ms 降到 87 ms。
        /// 「无 DC 不能进 SafeOp/OP」不是事实；把 DC 武装写成无条件步骤，会让每一台现场设备都强制
        /// 依赖 DC 能力，反而把不支持 DC 的从站挡在门外。因此默认不武装，DC 的开关交给这里的参数
        /// （CLI <c>--dc-sync</c> / 环境变量 <see cref="DcSyncEnvVar"/>），DENI 仍是拓扑与周期的唯一真相。
        /// </para>
        /// <para>
        /// 需要按从站 SYNC0 同步运行（例如对同步抖动有硬要求的现场）时显式打开；打开后仍按原硬门
        /// 校验 <c>HasDC</c> 与 <c>Sync0Armed</c>，读不到结论就拒绝升态，绝不冒充「已武装」。
        /// </para>
        /// </summary>
        internal static bool? DcSyncOverride;

        /// <summary>DC 开关的环境变量名（GUI 无命令行参数时用它；CLI 命令行优先）。</summary>
        internal const string DcSyncEnvVar = "FURUTA_DC_SYNC";

        /// <summary>
        /// 解析本次连接是否武装 DC。优先级：显式覆盖（CLI 旗标）→ 环境变量 → 默认关闭。
        /// 环境变量只认 1/0、true/false、on/off、yes/no；无法识别的值按默认关闭处理并如实写出来源。
        /// </summary>
        internal static bool ResolveDcSyncEnabled(out string source)
        {
            if (DcSyncOverride.HasValue)
            {
                source = "命令行 --dc-sync / --no-dc-sync";
                return DcSyncOverride.Value;
            }
            string raw = Environment.GetEnvironmentVariable(DcSyncEnvVar);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string value = raw.Trim().ToLowerInvariant();
                if (value == "1" || value == "true" || value == "on" || value == "yes")
                {
                    source = "环境变量 " + DcSyncEnvVar + "=" + raw.Trim();
                    return true;
                }
                if (value == "0" || value == "false" || value == "off" || value == "no")
                {
                    source = "环境变量 " + DcSyncEnvVar + "=" + raw.Trim();
                    return false;
                }
                source = "环境变量 " + DcSyncEnvVar + "=" + raw.Trim() +
                         "（无法识别，按默认关闭；只认 1/0、true/false、on/off、yes/no）";
                return false;
            }
            source = "默认关闭（未给 --dc-sync，也没有 " + DcSyncEnvVar + "）";
            return false;
        }

        // 0xF050 实读识别出的 GCAN 过程映像布局。默认全是"未识别"，识别成功才填。
        private GcanImageLayout _gcanImage;

        // DENI 里两台自有从站有没有导出 <ProcessData>（PDO 分配）。只用于把"实测 0B"的报错说准，
        // 不参与识别与放行：真正的放行判据永远是过程映像实测字节数与推导值相等。
        private bool _deniArmHasProcessData;
        private bool _deniGcanHasProcessData;

        // 旁观从站（除自有两台外的全部从站）的 0 基下标与状态留底，只读不控。
        private int[] _observerIndices = new int[0];
        private byte[] _observerStateCache = new byte[0];
        private long _observerScanTicks;
        private long _cycleNs = CycleConstants.DefaultCycleNs;
        private bool _open;
        private bool _disposed;

        // 本次连接实际采用的进 OP 方式（Open 时按 PendulumEngineOptions.OpEntry 定）。
        // Disable() 靠它决定要不要回退 SafeOp：直连诊断模式不经过 SafeOp，也就不回退。
        private OpEntryMode _opEntry = OpEntryMode.Standard;
        // 本次连接实际采用的 DC 策略（Open 时解析，失败信息里如实带上；未连接时是默认 false）。
        private bool _dcSyncEnabled;
        private int _frameLossConsecutive;
        private int _frameLossTotal;

        public bool IsOpen
        {
            get { return _open; }
        }

        /// <summary>本次连接实际采用的进 OP 方式（未连接时是默认值，不代表已生效）。</summary>
        public OpEntryMode OpEntry
        {
            get { return _opEntry; }
        }

        public ushort MasterNumber
        {
            get { return _master == null ? (ushort)0 : _master.MasterNumber; }
        }

        public long CycleNs
        {
            get { return _cycleNs; }
        }

        /// <summary>总线上扫到的从站数。</summary>
        public int SlaveCount
        {
            get { return _master == null || _master.Slaves == null ? 0 : _master.Slaves.Count; }
        }

        /// <summary>
        /// 摆臂步进从站当前是否掉线（SDK 公开真值 <c>Slave.IsLost</c>，native 存活判定）。
        /// 周期路径取 PDO 前先问它：掉线时 <c>InputsMapping</c> / <c>InputsSliceMapping</c> 会抛
        /// <c>PdoInputStaleException</c>，交出的是掉站前最后一帧，绝不能当当前值用。
        /// </summary>
        public bool ArmSlaveLost
        {
            get { return _stepper != null && _stepper.IsLost; }
        }

        /// <summary>GCAN 计数从站当前是否掉线。判据与 <see cref="ArmSlaveLost"/> 同源。</summary>
        public bool GcanSlaveLost
        {
            get { return _gcan != null && _gcan.IsLost; }
        }

        /// <summary>
        /// 连接时实际用的网卡接口名：显式给名走"名字匹配 + 与 DENI MAC 校验"，留空走 DENI Source MAC 自动匹配。
        /// 这是解析结果，不是请求值；还没连过 = 空。
        /// </summary>
        public string NicName
        {
            get { return _nicName; }
        }

        /// <summary>当前连接用的配置来源（本案例只认 DENI；配置缺失时不谎报成 ESI）。</summary>
        public string ConfigSourceText
        {
            get
            {
                if (!_open || string.IsNullOrWhiteSpace(_deniPath)) return "未配置 DENI";
                return DescribeConfigSource(_deniPath);
            }
        }

        /// <summary>DENI 路径 → 状态格那一行来源文本：只取文件名，不把整条路径塞进界面。</summary>
        internal static string DescribeConfigSource(string deniPath)
        {
            if (string.IsNullOrWhiteSpace(deniPath)) return "未配置 DENI";
            string name = null;
            try
            {
                name = System.IO.Path.GetFileName(deniPath);
            }
            catch (ArgumentException)
            {
                // 路径含非法字符时退回原路径，不把已给的配置来源丢成"未配置"。
            }
            if (string.IsNullOrEmpty(name)) name = deniPath;
            return "DENI：" + name;
        }

        public uint PdoFrameLossConsecutive
        {
            get { return (uint)Volatile.Read(ref _frameLossConsecutive); }
        }

        public uint PdoFrameLossTotal
        {
            get { return (uint)Volatile.Read(ref _frameLossTotal); }
        }

        /// <summary>摆臂步进从站在 master.Slaves 里的 0 基下标。未连接 = -1。</summary>
        public int ArmSlaveIndex
        {
            get { return _stepperIndex; }
        }

        /// <summary>GCAN 耦合器在 master.Slaves 里的 0 基下标。未连接 = -1。</summary>
        public int GcanSlaveIndex
        {
            get { return _gcanIndex; }
        }

        /// <summary>GCAN 上在位的计数切片种类。未连接 = Unknown。</summary>
        public GcanSliceKind CounterSlice
        {
            get { return _gcanImage.CounterSlice; }
        }

        /// <summary>0xF050 识别出的 GCAN 过程映像布局（含各切片字节偏移）。诊断展示用。</summary>
        public GcanImageLayout GcanLayout
        {
            get { return _gcanImage; }
        }

        // ============ 自有从站 / 旁观从站 ============

        /// <summary>
        /// 一次性取自有两台（GCAN + 摆臂步进）本拍的健康度。周期路径每拍调一次，热路径零分配零锁。
        /// <para>
        /// <paramref name="responding"/>（是否还在响应）的判据来源优先级：
        /// ① SDK 的 per-slave WKC 贡献（Contributed = 在响应，NotContributed = 明确掉线）；
        /// ② 两边都是 Unknown（内核还没填充逐站贡献）时退回全局 WKC 比对兜底——
        /// 全局 WKC 仍是如实读的 expected/actual 两个数，口径一个字没改，只是判据来源换了。
        /// </para>
        /// 旁观从站的 WKC / 状态一律不进这条判据：总线共享，别人的设备波动不该让倒立摆停机。
        /// </summary>
        public void ReadOwnedSlaveHealth(out bool armInOp, out bool gcanInOp, out bool responding)
        {
            DarraEtherCAT.Slave arm = _stepper;
            DarraEtherCAT.Slave gcan = _gcan;
            armInOp = arm != null && arm.State == EcState.OP;
            gcanInOp = gcan != null && gcan.State == EcState.OP;
            responding = false;
            if (arm == null || gcan == null) return;

            WcContribution a = arm.WcContributed;
            WcContribution g = gcan.WcContributed;
            if (a == WcContribution.NotContributed || g == WcContribution.NotContributed) return;
            if (a == WcContribution.Contributed && g == WcContribution.Contributed)
            {
                responding = true;
                return;
            }

            // Unknown：内核尚未填充逐站 WKC 贡献，退回全局 WKC 如实比对兜底。
            ushort actual, expected;
            if (!TryReadWkc(out actual, out expected)) return;
            responding = expected > 0 && actual == expected;
        }

        // ============ 连接 ============

        /// <summary>
        /// 只读身份预检 + Build + PreOp 身份/CSP 硬门 + 映射校验 + DC Sync0 武装 + SafeOp。
        /// 止于 SafeOp，不进 OP、不发 0x6040。失败时 failureCode 区分"配置不对"与"身份对不上"，前端按它映射退出码。
        /// </summary>
        public bool Open(PendulumEngineOptions options, out string error, out FaultCode failureCode)
        {
            ConnectTiming.Mark("open_begin");
            failureCode = FaultCode.ConfigInvalid;
            error = null;
            if (_open)
            {
                error = "总线会话已打开，请先 Disconnect。";
                ConnectTiming.Mark("failed:already_open");
                return false;
            }
            if (options == null)
            {
                error = "PendulumEngineOptions 为空。";
                ConnectTiming.Mark("failed:null_options");
                return false;
            }
            if (string.IsNullOrWhiteSpace(options.DeniPath))
            {
                error = "DeniPath 为空。DENI 是唯一配置真相，不猜配置。";
                ConnectTiming.Mark("failed:empty_deni");
                return false;
            }
            // 留底给诊断 / 界面显示配置来源；连接没成功时 IsOpen 为假，不会拿它冒充已生效配置。
            _deniPath = options.DeniPath;

            DeniIdentity deni;
            if (!DeniIdentityReader.TryRead(options.DeniPath, out deni, out error))
            {
                ConnectTiming.Mark("failed:deni_parse");
                return false;
            }
            ConnectTiming.Mark("deni_parse");

            // 网卡两条路：显式 NicName 照旧走"名字匹配 + 与 DENI MAC 校验"；
            // 留空 = 按 DENI Source MAC 自动匹配唯一一张 —— DENI 是唯一配置真相，
            // Master/Info/Source 精确指认网卡，按 MAC 找是照配置找卡，不是猜。
            // 这两步只是只读预检（fail-closed 身份门，不碰总线）：实际连接走 DENI-only 快速路径，
            // Build 自己按 DENI Source MAC 找到那张卡，不重新枚举 / 重配网卡。
            // 同进程重连时先查缓存：命中且 DENI 未变就完全跳过这两次慢枚举（实测约 1.08 s）。
            string nicName = options.NicName;
            bool nameFallbackUsed = false;
            if (string.IsNullOrWhiteSpace(nicName))
            {
                string cachedNic;
                if (TryGetCachedNic(deni.MasterMac, options.DeniPath, out cachedNic))
                {
                    nicName = cachedNic;
                    RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                        "网卡解析命中进程内缓存：MAC={0} → {1}（跳过枚举）",
                        DeniIdentityReader.FormatMac(deni.MasterMac), nicName));
                }
                else if (!TryAutoResolveNic(deni.MasterMac, out nicName, out error))
                {
                    // MAC 在本机不存在 = 典型克隆部署（镜像搬到新机器，网卡 MAC 变了）。
                    // 默认 fail closed；只有显式开关才按 DENI 记录的网卡名回退。
                    if (!AllowNicNameFallback)
                    {
                        error = error +
                            " 克隆部署（换了机器 / MAC 变了）请重新导出 DENI，或用 --nic 显式指定网卡；" +
                            "确认要按 DENI 记录的网卡名回退时，加 --allow-nic-name-fallback。";
                        failureCode = FaultCode.IdentityMismatch;
                        ConnectTiming.Mark("failed:nic_auto");
                        return false;
                    }
                    string fallbackMac;
                    string fallbackDetail;
                    if (!TryResolveNicByNameFallback(deni, out nicName, out fallbackMac, out fallbackDetail, out error))
                    {
                        failureCode = FaultCode.IdentityMismatch;
                        ConnectTiming.Mark("failed:nic_fallback");
                        return false;
                    }
                    nameFallbackUsed = true;
                    LogNameFallback(deni, nicName, fallbackMac, fallbackDetail, false);
                }
            }
            if (!nameFallbackUsed)
            {
                if (!MatchNic(nicName, deni.MasterMac, options.DeniPath, out error))
                {
                    if (!AllowNicNameFallback)
                    {
                        error = error +
                            " 克隆部署请重新导出 DENI，或确认按网卡名回退后再加 --allow-nic-name-fallback。";
                        failureCode = FaultCode.IdentityMismatch;
                        ConnectTiming.Mark("failed:nic_match");
                        return false;
                    }
                    // 显式 --nic + 显式开关：用户已经两次表明"就用这张卡"，按名字采纳并大声留痕。
                    string explicitMac;
                    string explicitDetail;
                    if (!TryResolveNicByNameOnly(nicName, out explicitMac, out explicitDetail, out error))
                    {
                        failureCode = FaultCode.IdentityMismatch;
                        ConnectTiming.Mark("failed:nic_match");
                        return false;
                    }
                    nameFallbackUsed = true;
                    LogNameFallback(deni, nicName, explicitMac, explicitDetail, true);
                }
                else
                {
                    // 回退结果不进缓存：缓存键只有 DENI MAC，缓存了就等于把"放宽"固化到进程生命周期，
                    // 后续没有开关的连接也会被放过。
                    StoreNicResolution(deni.MasterMac, options.DeniPath, nicName);
                }
            }
            // 名字已过 MAC 复核（或显式开关下的网卡名回退），留底给诊断 / 前端展示。
            _nicName = nicName;
            ConnectTiming.Mark("nic_resolve");

            DarraEtherCAT master = null;
            try
            {
                // 只给 DENI：网卡由 DENI 的 Master/Info/Source 指认，Build 自己认卡。
                // 不要再加回 SetNetwork：它按网卡名重新枚举 / 重配网卡，是慢路径（实测还会报会话创建失败）。
                ConnectTiming.Mark("sdk_load_begin");
                master = new DarraEtherCAT()
                    .SetENI(options.DeniPath)
                    .DisableAutoStartup();
                ConnectTiming.Mark("sdk_load");
                var result = master.Build();
                ConnectTiming.Mark("build");
                if (result == null || !result.Success)
                {
                    error = "Build 失败：" + (result == null ? "无结果" : result.Message);
                    // 这时还没把句柄交给会话（_master 未赋值），必须当场收口，否则这次实例没人释放。
                    CloseQuietly(master);
                    ConnectTiming.Mark("failed:build");
                    return false;
                }
                if (result.Master != null && !ReferenceEquals(result.Master, master))
                {
                    master = result.Master;
                }
            }
            catch (Exception ex)
            {
                error = "连接总线抛异常：" + DiagText.Describe(ex);
                CloseQuietly(master);
                ConnectTiming.Mark("failed:exception");
                return false;
            }

            _master = master;
            if (master.Slaves == null || master.Slaves.Count == 0)
            {
                error = "总线上没扫到从站：检查网线 / 从站供电、确认从站接在 DENI 对应的网口上，再重试 connect。";
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:no_slaves");
                return false;
            }
            if (master.Slaves.Count != deni.Slaves.Count)
            {
                error = string.Format(
                    "从站数量不符：总线扫到 {0} 个，DENI 声明 {1} 个。核对现场拓扑是否与 DENI 一致（增删从站后需重新导出 DENI）。",
                    master.Slaves.Count, deni.Slaves.Count);
                failureCode = FaultCode.IdentityMismatch;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:slave_count");
                return false;
            }

            // 身份逐条比对：顺序、VID/PID/Rev 都要对上，对不上就 fail closed。
            for (int i = 0; i < master.Slaves.Count; i++)
            {
                DarraEtherCAT.Slave s = master.Slaves[i];
                DeniSlaveIdentity expect = deni.Slaves[i];
                if (s.VendorId != expect.VendorId ||
                    s.ProductId != expect.ProductId ||
                    s.RevId != expect.RevisionNo)
                {
                    error = string.Format(
                        "从站 {0} 身份不符：总线 VID=0x{1:X8} PID=0x{2:X8} REV=0x{3:X8}，DENI 声明 VID=0x{4:X8} PID=0x{5:X8} REV=0x{6:X8}（{7}）。",
                        i, s.VendorId, s.ProductId, s.RevId,
                        expect.VendorId, expect.ProductId, expect.RevisionNo, expect.Name);
                    failureCode = FaultCode.IdentityMismatch;
                    TeardownAfterOpenFailure(ref error);
                    ConnectTiming.Mark("failed:identity");
                    return false;
                }
            }

            // 分从站：GCAN 耦合器按 VendorId 认，摆臂步进按 ArmSlaveIndex 显式指定或按 CSP 能力自动唯一识别。
            // 总线上其余从站（Beckhoff EK/EL 链等）是旁观者：枚举 / 身份对账 / 日志照实带出，
            // 但不写它们的 PDO、不做它们的 SDO 配置、也不因为它们拒连或掉站让倒立摆停机。
            for (int i = 0; i < master.Slaves.Count; i++)
            {
                if (master.Slaves[i].VendorId == GcanVendorId)
                {
                    if (_gcanIndex >= 0)
                    {
                        error = "总线上有不止一台广成 GCAN 从站，摆杆计数该接哪台不明确，拒绝连接。";
                        failureCode = FaultCode.IdentityMismatch;
                        TeardownAfterOpenFailure(ref error);
                        ConnectTiming.Mark("failed:dup_gcan");
                        return false;
                    }
                    _gcanIndex = i;
                }
            }
            if (_gcanIndex < 0)
            {
                error = "没找到 VendorId=0x00000E0D 的广成 GCAN 从站（摆杆计数模块）：确认 GCAN-8200 已上电并接在总线中，或重新导出包含该从站的 DENI。";
                failureCode = FaultCode.IdentityMismatch;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:no_gcan");
                return false;
            }
            if (!SelectArmSlave(master, options, out error))
            {
                failureCode = FaultCode.IdentityMismatch;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:arm_select");
                return false;
            }
            _stepper = master.Slaves[_stepperIndex];
            _gcan = master.Slaves[_gcanIndex];
            // DENI 顺序与总线扫描顺序已逐条核对过身份，这里按下标取同名条目是同一台站。
            _deniArmHasProcessData = _stepperIndex < deni.Slaves.Count && deni.Slaves[_stepperIndex].HasProcessData;
            _deniGcanHasProcessData = _gcanIndex < deni.Slaves.Count && deni.Slaves[_gcanIndex].HasProcessData;
            _stepperPdo = _stepper.PDO;
            _gcanPdo = _gcan.PDO;
            BuildObserverList(master);
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                "自有从站：GCAN=#{0}（{1}），摆臂步进=#{2}（{3}）；旁观从站 {4} 台（只读不控）。",
                _gcanIndex + 1, _gcan.Name, _stepperIndex + 1, _stepper.Name, _observerIndices.Length));

            // 周期时间以 DENI 为准。改周期必须重新导出 DENI，不在这儿偷偷改。
            long deniCycleNs = master.LoopCycle;
            if (deniCycleNs <= 0) deniCycleNs = CycleConstants.DefaultCycleNs;
            _cycleNs = deniCycleNs;
            if (options.CycleNs != (uint)deniCycleNs)
            {
                // 请求值与 DENI 不一致时如实报，不静默改配置。
                RaiseDiagnostic(DiagnosticSeverity.Warning, string.Format(
                    "周期以 DENI 为准：DENI={0} ns，请求 CycleNs={1} ns，本次按 DENI 走。",
                    deniCycleNs, options.CycleNs));
            }

            if (!TransitionTo(EcState.PreOp, out error))
            {
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:preop");
                return false;
            }
            ConnectTiming.Mark("preop");
            // 摆臂从站身份与能力硬门（04-EC1-ESI分析 §5E-1..3）：
            // 0x1018:1 必须是 Green IOT #x0AB5；0x6502 必须读得到且 bit7（0x80）置位 = 支持 CSP。
            // 任一不满足一律拒连，不降级 PP/PV 凑合跑摆臂，也不写死 0x6502 的期望值。
            if (!CheckArmIdentity(_stepper, out error))
            {
                failureCode = FaultCode.IdentityMismatch;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:arm_identity");
                return false;
            }
            if (!RequireCspMode(_stepper, out error))
            {
                failureCode = FaultCode.ConfigInvalid;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:arm_csp");
                return false;
            }
            // 先读 GCAN 的 0xF050 在位模块表识别切片（设备能力事实，供 SafeOp 之后的映射核对用）。
            // 识别不出 / 不认识的切片一律 fail closed 拒绝连接，禁止猜。
            if (!DetectGcanSlices(_gcan, out _gcanImage, out error))
            {
                failureCode = FaultCode.ConfigInvalid;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:gcan_slices");
                return false;
            }
            // PDO 映射核对（CheckPdoAssignment）不能在 PreOp 里做：DENI 的 PDO 分配启动参数这时批次
            // 还没落定，读到的会是出厂映射。核对移到 SafeOp 转换之后，见下方 SafeOp 段。
            // 顺序硬约束（ESI Info/StateMachine `StartToSafeopNoSync=false`）：
            // INIT → PREOP → 配 DC/Sync0 + 配 PDO 映射 → SAFEOP → OP。
            // DC 配置本身按本次参数决定（默认关闭，理由见 DcSyncOverride）；关闭时显式把摆臂从站
            // 置为 FreeRun，防止 DENI / SDK 自动策略把它又武装回来 —— 不允许「说了关但实际还开着」。
            string dcSource;
            _dcSyncEnabled = ResolveDcSyncEnabled(out dcSource);
            if (!ApplyStepperDcPolicy(out error))
            {
                failureCode = FaultCode.ConfigInvalid;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:dc_sync0");
                return false;
            }
            RaiseDiagnostic(DiagnosticSeverity.Audit, _dcSyncEnabled
                ? "摆臂从站 DC Sync0：本次按参数启用（来源：" + dcSource + "）。"
                : "摆臂从站 DC Sync0：本次关闭（来源：" + dcSource + "），按 FreeRun 运行；" +
                  "总线进 OP 不依赖 DC，DENI 仍是拓扑与周期的唯一真相。");
            ConnectTiming.Mark("preop_gate");
            _opEntry = options.OpEntry;
            if (_opEntry == OpEntryMode.Standard)
            {
                if (!TransitionTo(EcState.SafeOp, out error))
                {
                    // 同步没建立就硬切 SAFEOP 会失败，这里如实带上 DC 武装证据与本次 DC 策略，不重试硬切（04 §5E-8）。
                    error = string.Format("切 SafeOp 失败：{0}。DC 武装状态：{1}{2}", error, DescribeDcState(_stepper),
                        _dcSyncEnabled
                            ? ""
                            : "（本次按默认关闭 DC；若 DENI 声明了 DistributedClocks，请加 --dc-sync 重连后再试）");
                    TeardownAfterOpenFailure(ref error);
                    ConnectTiming.Mark("failed:safeop");
                    return false;
                }
                ConnectTiming.Mark("safeop");
                // 映射核对必须等 DENI 启动参数落定之后（2026-11-18 台架实测的竞态）：
                // 步进的 0x1C12 / 0x1C13 / 0x1600 / 0x1A00 是 DENI 里的 IP-AfterTransition 启动参数，
                // 由 native 在 INIT→PreOp 转换之后按批次下发（highlevel.c 的「[高级API] ... 全部从站启动参数
                // 完成 (并行)」即出自那里）；GCAN 的 0xF030 模块表则是 PS-Before，在 PreOp→SafeOp 才写。
                // 复现条件：冷启动 + 步进处于出厂 96 bit 映射 + DENI 会重写映射（本台架重写为 56 bit）。
                // 在 PreOp 里读，IP-After 批次可能还没写完，读到出厂 96 bit → 首连必失败；
                // 参数落定后再读才是 56 bit → 第二次连接才成功。这是校验时机错，不是配置错，
                // 不许用「失败重试一次就放过」掩盖：这里仍逐条如实比对，不一致照旧拒绝连接。
                if (!CheckPdoAssignment(out error))
                {
                    TeardownAfterOpenFailure(ref error);
                    ConnectTiming.Mark("failed:pdo_assign");
                    return false;
                }
                // 过程映像字节数在未映射时是 0。PreOp 里核对会把「还没映射」误报成结构体不符。
                // SafeOp 之后映像已按 DENI 落地，这时再比。
                if (!CheckPdoSizes(out error))
                {
                    TeardownAfterOpenFailure(ref error);
                    ConnectTiming.Mark("failed:pdo_sizes");
                    return false;
                }
            }
            // 倒立摆要进 OP。这里只切总线状态，不写 0x6040，不使能电机。
            // Standard：PreOp → SafeOp → OP；DirectFromPreOp：PreOp 一步升 OP，不经显式 SafeOp
            // （仅诊断用；2.13.7 的 SafeOp 缺陷已于 2.13.9 修复，不再需要绕开）。
            if (!TransitionTo(EcState.OP, out error))
            {
                error = string.Format("切 OP 失败（进 OP 方式 {0}）：{1}。DC 武装状态：{2}{3}",
                    _opEntry, error, DescribeDcState(_stepper),
                    _dcSyncEnabled
                        ? ""
                        : "（本次按默认关闭 DC；若 DENI 声明了 DistributedClocks，请加 --dc-sync 重连后再试）");
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:op");
                return false;
            }
            ConnectTiming.Mark("op");
            if (_opEntry == OpEntryMode.DirectFromPreOp)
            {
                // 直连路径没有显式 SafeOp 这一站：DENI 的 PS-Before / SO-Before 启动参数在 PreOp→OP
                // 这一趟升态里才落定（含 0x1C12 / 0x1C13 分配），所以映射核对与字节数自检都补在这里。
                // 仍逐条如实比对，不一致照旧拒绝连接。
                if (!CheckPdoAssignment(out error))
                {
                    TeardownAfterOpenFailure(ref error);
                    ConnectTiming.Mark("failed:pdo_assign_direct");
                    return false;
                }
                // 直连路径没有 SafeOp 这一站，过程映像要到进 OP 后才按 DENI 落地，因此把字节数自检补在这里。
                if (!CheckPdoSizes(out error))
                {
                    TeardownAfterOpenFailure(ref error);
                    ConnectTiming.Mark("failed:pdo_sizes_direct");
                    return false;
                }
            }
            // 进 OP 判据（走的是哪条路径）是连接期一次性取证：显式 Audit，GUI 必落盘。
            RaiseDiagnostic(DiagnosticSeverity.Audit, _opEntry == OpEntryMode.DirectFromPreOp
                ? "总线进 OP 完成：PreOp → OP【直连诊断模式：绕过 SafeOp，非标准路径】。"
                : "总线进 OP 完成：PreOp → SafeOp → OP（标准路径）。");

            // 应用侧 PDO 就绪门（在控制线程创建之前）：native Start 已等会话 priming 与 iomap 可写，
            // 这里再确认两个自有从站的输入映像**连续可读**，把「控制线程第一拍撞上映像未就绪」
            // 变成确定性等待并留证，而不是靠周期路径的单拍去抖兜启动时序。
            long pdoReadyWaitMs;
            string pdoReadyDetail;
            if (!WaitForProcessImageReady(PdoReadyTimeoutMs, out pdoReadyWaitMs, out pdoReadyDetail))
            {
                error = "PDO 输入映像就绪确认失败：" + (pdoReadyDetail ?? "未返回原因") +
                        "。不在映像未就绪时启动控制线程；请检查链路 / 从站状态后重连。";
                failureCode = FaultCode.ConfigInvalid;
                TeardownAfterOpenFailure(ref error);
                ConnectTiming.Mark("failed:pdo_ready");
                return false;
            }
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                "PDO 输入映像就绪确认：等待 {0} ms（{1}）。控制线程在确认之后才启动。",
                pdoReadyWaitMs, pdoReadyDetail));
            ConnectTiming.Mark("pdo_ready");

            master.Events.PDOFrameLoss += OnPdoFrameLoss;
            _open = true;
            ConnectTiming.Mark("open_done");
            return true;
        }

        /// <summary>
        /// Open 中途失败时收口：Close 抛异常也不外抛，只记一条诊断。
        /// 成功路径的收口只走 <see cref="FinishTeardown"/>，两条路都不留挂住的 native 句柄。
        /// </summary>
        private void CloseQuietly(DarraEtherCAT master)
        {
            if (master == null) return;
            try { master.Close(); }
            catch (Exception closeEx)
            {
                RaiseDiagnostic(DiagnosticSeverity.Error, "连接失败后收尾 Close 也失败：" + DiagText.Describe(closeEx));
            }
        }

        /// <summary>
        /// 按 DENI Source MAC 查缓存：同一 MAC 但换了 DENI 文件就不复用（配置真相变了要重新解析）。
        /// 命中 = 拿到上一轮已验证过的网卡名，调用方可以完全跳过两次慢枚举。
        /// </summary>
        private static bool TryGetCachedNic(string deniMac, string deniPath, out string nicName)
        {
            nicName = null;
            string key = DeniIdentityReader.NormalizeMac(deniMac);
            if (key.Length != 12) return false;
            lock (NicCacheGate)
            {
                NicResolution hit;
                if (!NicCache.TryGetValue(key, out hit)) return false;
                if (!string.Equals(hit.DeniPath, deniPath, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.IsNullOrWhiteSpace(hit.NicName)) return false;
                nicName = hit.NicName;
                return true;
            }
        }

        /// <summary>解析 + 校验成功后写缓存；失败路径一律不写，免得把错结果固化到整个进程生命周期。</summary>
        private static void StoreNicResolution(string deniMac, string deniPath, string nicName)
        {
            string key = DeniIdentityReader.NormalizeMac(deniMac);
            if (key.Length != 12 || string.IsNullOrWhiteSpace(nicName)) return;
            NicResolution entry;
            entry.NicName = nicName;
            entry.DeniPath = deniPath;
            lock (NicCacheGate) { NicCache[key] = entry; }
        }

        /// <summary>
        /// 网卡接口名 + 6 字节 MAC 唯一匹配。名字对上但 MAC 对不上也要拒，不猜网卡。
        /// 同进程内已按 (MAC, DENI) 验证过的同一个名字直接命中缓存，跳过 GetAllNetworkInterfaces 慢枚举。
        /// </summary>
        private static bool MatchNic(string nicName, string deniMac, string deniPath, out string error)
        {
            error = null;
            string cachedName;
            if (TryGetCachedNic(deniMac, deniPath, out cachedName) &&
                string.Equals(cachedName, nicName, StringComparison.OrdinalIgnoreCase))
                return true;
            NetworkInterface[] all;
            try
            {
                all = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception ex)
            {
                error = "枚举网卡失败：" + DiagText.Describe(ex);
                return false;
            }
            NetworkInterface matched = null;
            int matchedCount = 0;
            foreach (NetworkInterface nic in all)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                bool nameHit = string.Equals(nic.Name, nicName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(nic.Description, nicName, StringComparison.OrdinalIgnoreCase);
                if (!nameHit) continue;
                matchedCount++;
                matched = nic;
            }
            if (matchedCount != 1)
            {
                error = string.Format(
                    "网卡名 {0} 匹配到 {1} 个接口，必须唯一。未找到 DENI 对应网卡就不猜网卡。",
                    nicName, matchedCount);
                return false;
            }
            string actualMac = DeniIdentityReader.NormalizeMac(matched.GetPhysicalAddress().ToString());
            if (!string.Equals(actualMac, deniMac, StringComparison.OrdinalIgnoreCase))
            {
                error = string.Format(
                    "网卡 {0} 的 MAC={1} 与 DENI 的 Source={2} 不一致，拒绝连接。",
                    nicName, actualMac, deniMac);
                return false;
            }
            return true;
        }

        // ============ 克隆部署：按 DENI 网卡名回退（显式开关才走） ============

        /// <summary>本机一张网卡的候选快照。克隆回退裁决只看 DENI 里写下的身份字段，不按从站数猜。</summary>
        private struct NicCandidate
        {
            public string Name;         // Windows 友好名（"以太网"），SDK / MatchNic 认的就是它
            public string Description;  // 驱动描述（"Intel(R) 82575EB ..."）
            public string Id;           // NDIS 接口 GUID（"{C240A437-...}"）
            public string Mac;          // 规范化 12 位十六进制，拿不到 = 空
        }

        /// <summary>
        /// 枚举本机网卡候选（跳过 loopback / tunnel）。单卡取 MAC 失败只记诊断，不拖垮整次枚举。
        /// </summary>
        private List<NicCandidate> EnumerateNicCandidates(out string error)
        {
            error = null;
            var list = new List<NicCandidate>();
            NetworkInterface[] all;
            try
            {
                all = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception ex)
            {
                error = "枚举网卡失败，没法按 DENI 网卡名回退：" + DiagText.Describe(ex);
                return null;
            }
            foreach (NetworkInterface nic in all)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;
                string mac = string.Empty;
                try
                {
                    mac = DeniIdentityReader.NormalizeMac(nic.GetPhysicalAddress().ToString());
                }
                catch (Exception ex)
                {
                    RaiseDiagnostic(DiagnosticSeverity.Warning, string.Format(
                        "克隆回退枚举：网卡 {0} 取 MAC 失败（{1}），该卡只按名字参与匹配。",
                        nic.Name, DiagText.Describe(ex)));
                }
                var candidate = new NicCandidate
                {
                    Name = nic.Name ?? string.Empty,
                    Description = nic.Description ?? string.Empty,
                    Id = nic.Id ?? string.Empty,
                    Mac = mac
                };
                list.Add(candidate);
            }
            return list;
        }

        /// <summary>
        /// 按 DENI 记录的网卡身份在本机找唯一一张网卡（克隆回退，仅 <see cref="AllowNicNameFallback"/> 下调用）。
        /// 只认 DENI 明确写下的 <c>deni:Interface / deni:InterfaceDescription</c>：
        /// 名字 / 描述 / NPF 设备名里的 GUID 任一精确命中即算候选；0 张或多张一律拒绝，不按从站数猜。
        /// </summary>
        private bool TryResolveNicByNameFallback(
            DeniIdentity deni, out string nicName, out string actualMac, out string detail, out string error)
        {
            nicName = null;
            actualMac = null;
            detail = null;
            error = null;

            string wantName = (deni.InterfaceName ?? string.Empty).Trim();
            string wantDesc = (deni.InterfaceDescription ?? string.Empty).Trim();
            if (wantName.Length == 0 && wantDesc.Length == 0)
            {
                error = "DENI 里没有网卡名（deni:Interface / deni:InterfaceDescription），没法按名字回退。" +
                        "请重新导出 DENI，或用 --nic 显式指定网卡。";
                return false;
            }
            string wantGuid = ExtractBracedGuid(wantName);

            List<NicCandidate> all = EnumerateNicCandidates(out error);
            if (all == null) return false;
            if (all.Count == 0)
            {
                error = "本机没枚举到任何物理网卡，没法按 DENI 网卡名回退。";
                return false;
            }

            var hits = new List<NicCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var listing = new System.Text.StringBuilder();
            foreach (NicCandidate c in all)
            {
                if (listing.Length > 0) listing.Append("；");
                listing.AppendFormat("{0}（{1}，MAC={2}）",
                    c.Name,
                    string.IsNullOrWhiteSpace(c.Description) ? "无描述" : c.Description,
                    c.Mac.Length == 12 ? DeniIdentityReader.FormatMac(c.Mac) : "拿不到");

                bool hit = false;
                if (wantName.Length > 0)
                {
                    if (NameEquals(c.Name, wantName)) hit = true;
                    else if (NameEquals(c.Description, wantName)) hit = true;
                    else if (wantGuid != null && NameEquals(c.Id, wantGuid)) hit = true;
                }
                if (!hit && wantDesc.Length > 0)
                {
                    if (NameEquals(c.Description, wantDesc)) hit = true;
                    else if (NameEquals(c.Name, wantDesc)) hit = true;
                }
                if (!hit) continue;
                // 同一张卡可能因为别名重复出现，按 GUID（拿不到再用名字）去重。
                string key = c.Id.Length > 0 ? c.Id : (c.Mac.Length == 12 ? c.Mac : c.Name);
                if (seen.Add(key)) hits.Add(c);
            }

            string source = wantName.Length > 0
                ? "deni:Interface=" + wantName + (wantDesc.Length > 0 ? " / deni:InterfaceDescription=" + wantDesc : string.Empty)
                : "deni:InterfaceDescription=" + wantDesc;

            if (hits.Count == 1)
            {
                NicCandidate hit = hits[0];
                if (hit.Mac.Length != 12 || string.IsNullOrWhiteSpace(hit.Name))
                {
                    error = string.Format(
                        "DENI 网卡名（{0}）在本机命中一张卡，但给不出可用接口名 / MAC，拒绝按名字回退。本机网卡实况：{1}",
                        source, listing);
                    return false;
                }
                nicName = hit.Name;
                actualMac = hit.Mac;
                detail = (string.IsNullOrWhiteSpace(hit.Description) ? "无描述" : hit.Description) +
                         "，GUID=" + (string.IsNullOrWhiteSpace(hit.Id) ? "拿不到" : hit.Id);
                return true;
            }

            if (hits.Count == 0)
            {
                error = string.Format(
                    "DENI 网卡名（{0}）在本机没有对应网卡，拒绝按名字回退（不猜网卡）。本机网卡实况：{1}",
                    source, listing);
                return false;
            }

            var hitNames = new System.Text.StringBuilder();
            foreach (NicCandidate c in hits)
            {
                if (hitNames.Length > 0) hitNames.Append("；");
                hitNames.AppendFormat("{0}（MAC={1}）", c.Name,
                    c.Mac.Length == 12 ? DeniIdentityReader.FormatMac(c.Mac) : "拿不到");
            }
            error = string.Format(
                "DENI 网卡名（{0}）在本机命中 {1} 张网卡，不唯一，拒绝按名字回退：{2}。请用 --nic 显式指定。",
                source, hits.Count, hitNames);
            return false;
        }

        /// <summary>
        /// 显式 <c>--nic</c> + 显式回退开关时按名字认卡：只验证这个名字在本机唯一存在，不比对 MAC。
        /// 名字对不上 / 命中多张一律拒绝 —— 显式意图不等于可以猜。
        /// </summary>
        private bool TryResolveNicByNameOnly(
            string nicName, out string actualMac, out string detail, out string error)
        {
            actualMac = null;
            detail = null;
            error = null;
            List<NicCandidate> all = EnumerateNicCandidates(out error);
            if (all == null) return false;

            var hits = new List<NicCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var listing = new System.Text.StringBuilder();
            foreach (NicCandidate c in all)
            {
                if (listing.Length > 0) listing.Append("；");
                listing.AppendFormat("{0}（{1}，MAC={2}）", c.Name,
                    string.IsNullOrWhiteSpace(c.Description) ? "无描述" : c.Description,
                    c.Mac.Length == 12 ? DeniIdentityReader.FormatMac(c.Mac) : "拿不到");
                if (!NameEquals(c.Name, nicName) && !NameEquals(c.Description, nicName)) continue;
                string key = c.Id.Length > 0 ? c.Id : (c.Mac.Length == 12 ? c.Mac : c.Name);
                if (seen.Add(key)) hits.Add(c);
            }
            if (hits.Count == 1 && hits[0].Mac.Length == 12 && !string.IsNullOrWhiteSpace(hits[0].Name))
            {
                actualMac = hits[0].Mac;
                detail = (string.IsNullOrWhiteSpace(hits[0].Description) ? "无描述" : hits[0].Description) +
                         "，GUID=" + (string.IsNullOrWhiteSpace(hits[0].Id) ? "拿不到" : hits[0].Id);
                return true;
            }
            if (hits.Count == 0)
            {
                error = string.Format(
                    "显式 --nic {0} 在本机找不到对应网卡，拒绝连接（不猜网卡）。本机网卡实况：{1}",
                    nicName, listing);
                return false;
            }
            error = hits.Count == 1
                ? string.Format("显式 --nic {0} 命中的网卡给不出 MAC / 接口名，拒绝连接。本机网卡实况：{1}", nicName, listing)
                : string.Format("显式 --nic {0} 命中 {1} 张网卡，必须唯一，拒绝连接。本机网卡实况：{2}", nicName, hits.Count, listing);
            return false;
        }

        /// <summary>
        /// 回退发生时的取证行（大声留痕）：DENI 原本记的 MAC、本机实际用了哪张卡（名字 + MAC + 描述 + GUID）。
        /// 这条必须能被现场日志 / 诊断界面直接看到 —— 克隆部署认错卡是安全事件，不允许静默。
        /// </summary>
        private void LogNameFallback(
            DeniIdentity deni, string nicName, string actualMac, string detail, bool explicitNic)
        {
            string deniNote = string.Empty;
            if (!string.IsNullOrEmpty(deni.InterfaceMac) &&
                !string.Equals(deni.InterfaceMac, deni.MasterMac, StringComparison.OrdinalIgnoreCase))
            {
                deniNote = string.Format("（注意：DENI 自相矛盾，Master/Info/Source={0} 与 deni:InterfaceMAC={1} 不一致）",
                    DeniIdentityReader.FormatMac(deni.MasterMac),
                    DeniIdentityReader.FormatMac(deni.InterfaceMac));
            }
            RaiseDiagnostic(DiagnosticSeverity.Warning, string.Format(
                "克隆部署网卡名回退已启用（{0}）：DENI 记录的网卡 MAC={1}{2} 在本机不存在；" +
                "改用本机网卡 \"{3}\"（MAC={4}，{5}）。" +
                "这是显式开关 --allow-nic-name-fallback 下的放宽行为；请确认这张卡就是本 DENI 对应的 EtherCAT 总线，" +
                "克隆部署建议重新导出 DENI 或改用 --nic 显式指定。",
                explicitNic ? "显式 --nic + 开关" : "DENI 网卡名 + 开关",
                DeniIdentityReader.FormatMac(deni.MasterMac),
                deniNote,
                nicName,
                DeniIdentityReader.FormatMac(actualMac),
                detail));
        }

        /// <summary>名字比较：去空白 + 忽略大小写（网卡名和描述都是人写的，不因空格/大小写拒认）。</summary>
        private static bool NameEquals(string a, string b)
        {
            return string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从 <c>\Device\NPF_{GUID}</c> 这类文本里取 {GUID}；不是 38 字符的花括号 GUID 返回 null。</summary>
        private static string ExtractBracedGuid(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int open = text.IndexOf('{');
            if (open < 0) return null;
            int close = text.IndexOf('}', open + 1);
            if (close < 0) return null;
            string guid = text.Substring(open, close - open + 1);
            return guid.Length == 38 ? guid : null;
        }

        /// <summary>
        /// 空 NicName 时按 DENI Source MAC 自动匹配唯一一张网卡。
        /// DENI 是唯一配置真相，<c>Master/Info/Source</c> 精确指认网卡 —— 按 MAC 找是照配置找卡，不是猜。
        /// 恰好 1 张命中 = 自动采用并继续走 <see cref="MatchNic"/> 复核；
        /// 0 张 / 多张一律 fail closed 拒连，报文列出候选 / 实况，不猜不夹。
        /// </summary>
        private bool TryAutoResolveNic(string deniMac, out string nicName, out string error)
        {
            nicName = null;
            error = null;

            // MAC → Windows 友好名一次建好。名字是 MatchNic 预检的入口，不猜。
            string osNotes;
            Dictionary<string, string> osNameByMac = BuildOsNicNameMap(out osNotes);
            if (!string.IsNullOrEmpty(osNotes))
            {
                RaiseDiagnostic(DiagnosticSeverity.Warning,
                    "网卡自动匹配：Windows 网卡名反查有缺口，拿不到的按驱动描述 / SDK 名降级。" + osNotes);
            }

            List<DarraEtherCAT.NetworkAdapterInfo> nets;
            try
            {
                // 物理网卡枚举走公开选卡清单 GetNetworkAdapters(includeSlaveCount:false)：一次给全
                // （含物理判据 EtherCatCapable），再按 EtherCatCapable 过滤，
                // 与旧的两步枚举 + 物理过滤同一判据、行为等价。
                // 只枚举不活扫：带从站数的活扫会留下 temp owner / 隔离，
                // 不收口就让随后的 SetENI + Build 扫出 0 从站。
                nets = new List<DarraEtherCAT.NetworkAdapterInfo>();
                foreach (DarraEtherCAT.NetworkAdapterInfo nic in DarraEtherCAT.GetNetworkAdapters(includeSlaveCount: false))
                {
                    if (nic != null && nic.EtherCatCapable) nets.Add(nic);
                }
            }
            catch (Exception ex)
            {
                error = "枚举物理网卡失败，没法按 DENI Source MAC 自动匹配：" + DiagText.Describe(ex);
                return false;
            }
            if (nets == null) nets = new List<DarraEtherCAT.NetworkAdapterInfo>();

            // 同一块物理卡在 SDK 列表里可能出两行（绑定行 + 原生枚举行），按 MAC 去重，
            // 免得把同一张卡数成"多候选"误拒。拿不到 MAC 的行只能进实况清单，命中不了。
            var names = new List<string>();
            var macs = new List<string>();
            var seenMac = new HashSet<string>();
            System.Text.StringBuilder list = new System.Text.StringBuilder();
            foreach (DarraEtherCAT.NetworkAdapterInfo net in nets)
            {
                if (net == null) continue;
                string mac = DeniIdentityReader.NormalizeMac(net.Mac);
                if (mac.Length == 12 && !seenMac.Add(mac)) continue;
                string name = null;
                if (mac.Length == 12) osNameByMac.TryGetValue(mac, out name);
                if (string.IsNullOrWhiteSpace(name))
                    name = string.IsNullOrWhiteSpace(net.Description) ? net.Name : net.Description;
                if (string.IsNullOrWhiteSpace(name)) name = "（未命名网卡）";
                names.Add(name);
                macs.Add(mac);
                if (list.Length > 0) list.Append("；");
                list.AppendFormat("{0}（{1}，MAC={2}，{3}）",
                    name,
                    string.IsNullOrWhiteSpace(net.Description) ? "无描述" : net.Description,
                    mac.Length == 12 ? DeniIdentityReader.FormatMac(mac) : "拿不到",
                    net.BoundProtocol ? "已绑 EtherCAT" : "未绑 EtherCAT");
            }

            return TryAdoptNicByMac(deniMac, names.ToArray(), macs.ToArray(), list.ToString(),
                out nicName, out error);
        }

        /// <summary>
        /// 自动匹配收口（可自检）：拿候选三件套裁决，命中就打「网卡自动匹配」诊断。
        /// <see cref="TryAutoResolveNic"/> 只管枚举物理网卡，本方法管裁决 + 诊断；
        /// 拆开是为了让「DENI Source 命中唯一 → 放行 + 打自动匹配日志」这一路不动真网卡也能自检到。
        /// </summary>
        internal bool TryAdoptNicByMac(
            string deniMac,
            string[] candidateName,
            string[] candidateMac,
            string candidateList,
            out string nicName,
            out string error)
        {
            nicName = null;
            if (!ResolveNicByMac(deniMac, candidateName, candidateMac, candidateList, out nicName, out error))
                return false;
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format("网卡自动匹配：DENI Source={0} → {1}",
                DeniIdentityReader.FormatMac(deniMac), nicName));
            return true;
        }

        /// <summary>
        /// 一次枚举本机网卡建 MAC → Windows 友好名映射（<see cref="MatchNic"/> 复核用的就是这个名字）。
        /// 枚举失败 / 单卡取 MAC 失败如实写进 notes 交上层打诊断，不空吞。
        /// </summary>
        private static Dictionary<string, string> BuildOsNicNameMap(out string notes)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var gaps = new System.Text.StringBuilder();
            NetworkInterface[] all;
            try
            {
                all = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception ex)
            {
                notes = "枚举 Windows 网卡失败：" + DiagText.Describe(ex);
                return map;
            }
            foreach (NetworkInterface nic in all)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    string mac = DeniIdentityReader.NormalizeMac(nic.GetPhysicalAddress().ToString());
                    if (mac.Length != 12 || string.IsNullOrWhiteSpace(nic.Name)) continue;
                    if (!map.ContainsKey(mac)) map.Add(mac, nic.Name);
                }
                catch (Exception ex)
                {
                    if (gaps.Length > 0) gaps.Append("；");
                    gaps.Append(nic.Name).Append(" 取 MAC 失败：").Append(DiagText.Describe(ex));
                }
            }
            notes = gaps.ToString();
            return map;
        }

        /// <summary>
        /// 网卡自动裁决（纯函数，可自检）：按 DENI Source MAC 在候选里找唯一一张。
        /// <paramref name="candidateName"/> 与 <paramref name="candidateMac"/> 同序；
        /// <paramref name="candidateMac"/> 是规范化 12 位十六进制，空 = 该卡拿不到 MAC。
        /// <paramref name="candidateList"/> 是本机实况清单，拒连时原样写进 error。
        /// 命中恰好 1 个 = 采用；0 个 / 多个一律拒绝并把实况写进 error，绝不猜。
        /// </summary>
        internal static bool ResolveNicByMac(
            string deniMac,
            string[] candidateName,
            string[] candidateMac,
            string candidateList,
            out string chosenName,
            out string error)
        {
            chosenName = null;
            error = null;
            string mac = DeniIdentityReader.NormalizeMac(deniMac);
            if (mac.Length != 12)
            {
                error = string.Format(
                    "DENI Source MAC {0} 不是 6 字节 MAC，没法指认网卡，拒绝连接。",
                    string.IsNullOrEmpty(deniMac) ? "（空）" : deniMac);
                return false;
            }

            int hits = 0;
            int total = candidateMac == null ? 0 : candidateMac.Length;
            for (int i = 0; i < total; i++)
            {
                if (string.IsNullOrEmpty(candidateMac[i])) continue;
                if (!string.Equals(candidateMac[i], mac, StringComparison.OrdinalIgnoreCase)) continue;
                hits++;
                chosenName = candidateName == null || i >= candidateName.Length ? null : candidateName[i];
            }

            string shown = DeniIdentityReader.FormatMac(mac);
            if (hits == 1 && !string.IsNullOrWhiteSpace(chosenName)) return true;

            chosenName = null;
            if (hits == 0)
            {
                error = string.Format(
                    "DENI Source MAC {0} 在本机无对应网卡，拒绝连接。本机物理网卡实况：{1}",
                    shown, string.IsNullOrEmpty(candidateList) ? "（没枚举到任何物理网卡）" : candidateList);
                return false;
            }
            if (hits > 1)
            {
                error = string.Format(
                    "DENI Source MAC {0} 在本机命中 {1} 张网卡，不唯一，拒绝连接。请用 --nic 显式指定。本机物理网卡实况：{2}",
                    shown, hits, candidateList);
                return false;
            }
            // 命中 1 张但这张卡给不出可用接口名，仍拒：名字是 MatchNic 预检的入口，不猜。
            error = string.Format(
                "DENI Source MAC {0} 命中的网卡给不出接口名，拒绝连接。本机物理网卡实况：{1}",
                shown, candidateList);
            return false;
        }

        // ============ 从站角色分配（自有两台 + 旁观者）============

        /// <summary>
        /// 选摆臂步进从站。ArmSlaveIndex >= 0 = 显式指定（越界 / 指到 GCAN 就拒）；
        /// -1 = 自动，判据是"非 GCAN 且 0x6502 声明支持 CSP"。自动找出 0 个或多个候选一律拒绝连接并列出清单，
        /// 歧义绝不猜（真机上 EL7041 这类端子也自称 CSP，必须显式指定）。
        /// </summary>
        private bool SelectArmSlave(DarraEtherCAT master, PendulumEngineOptions options, out string error)
        {
            error = null;
            int requested = options.ArmSlaveIndex;
            int total = master.Slaves.Count;

            // 逐台探 CSP 能力，顺带拼一份候选清单（拒连时要列得出来）。
            bool[] probed = new bool[total];
            bool[] supports = new bool[total];
            List<int> candidates = new List<int>();
            System.Text.StringBuilder list = new System.Text.StringBuilder();
            for (int i = 0; i < total; i++)
            {
                if (i == _gcanIndex) continue;
                DarraEtherCAT.Slave s = master.Slaves[i];
                string probe;
                probed[i] = TryProbeCsp(s, out supports[i], out probe);
                if (list.Length > 0) list.Append("；");
                list.AppendFormat("#{0} {1} VID=0x{2:X8} PID=0x{3:X8} CSP={4}（{5}）",
                    i + 1, s.Name, s.VendorId, s.ProductId,
                    probed[i] ? (supports[i] ? "是" : "否") : "未知", probe);
                if (probed[i] && supports[i]) candidates.Add(i);
            }

            int chosen;
            if (!ResolveArmSlaveIndex(requested, _gcanIndex, total,
                    candidates.ToArray(), list.ToString(), out chosen, out error))
            {
                return false;
            }
            _stepperIndex = chosen;

            if (requested >= 0)
            {
                // 显式指定优先于自动识别；CSP 能力读得到就复检，读不到如实报一条诊断。
                // 这里只做选站，不做能力放行：PreOp 后的 0x6502 硬门（RequireCspMode）才是判据，
                // 读不到或 bit7 未置位一律拒连，不因"显式指定过"就降级放行。
                if (!probed[chosen])
                {
                    RaiseDiagnostic(DiagnosticSeverity.Warning, string.Format(
                        "摆臂 #{0} 的 CSP 能力探不到，按显式指定选站；PreOp 后仍要过 0x6502 硬门，过不了就拒连。候选排查：{1}",
                        chosen + 1, list));
                }
                else if (!supports[chosen])
                {
                    error = string.Format(
                        "ArmSlaveIndex={0} 指定的从站 {1} 不支持 CSP，拒绝连接。候选排查：{2}",
                        chosen, master.Slaves[chosen].Name, list);
                    return false;
                }
                else
                {
                    RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                        "摆臂 #{0}（{1}）按 ArmSlaveIndex={2} 显式指定，CSP 能力已复检。",
                        chosen + 1, master.Slaves[chosen].Name, requested));
                }
            }
            else
            {
                RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                    "摆臂 #{0}（{1}）已按 CSP 能力自动识别（唯一候选）。", chosen + 1, master.Slaves[chosen].Name));
            }
            return true;
        }

        /// <summary>
        /// 摆臂下标最终裁决（纯函数，可自检）。
        /// <paramref name="requested"/> ≥ 0 = 显式指定：越界、指到 GCAN 一律拒；
        /// = -1 = 自动：候选必须恰好 1 个，0 个 / 多个一律拒绝并把候选清单写进 error，绝不猜。
        /// </summary>
        internal static bool ResolveArmSlaveIndex(
            int requested,
            int gcanIndex,
            int slaveCount,
            int[] candidates,
            string candidateList,
            out int chosen,
            out string error)
        {
            chosen = -1;
            error = null;

            if (requested >= 0)
            {
                if (requested >= slaveCount)
                {
                    error = string.Format(
                        "ArmSlaveIndex={0} 越界：总线只有 {1} 个从站（0 基下标 0..{2}）。候选排查：{3}",
                        requested, slaveCount, slaveCount - 1, candidateList);
                    return false;
                }
                if (requested == gcanIndex)
                {
                    error = string.Format(
                        "ArmSlaveIndex={0} 指到 GCAN 耦合器本身，摆臂步进不能是计数模块，拒绝连接。候选排查：{1}",
                        requested, candidateList);
                    return false;
                }
                chosen = requested;
                return true;
            }

            if (candidates == null || candidates.Length == 0)
            {
                error = string.Format(
                    "没找到支持 CSP 的 CiA402 从站（摆臂步进），拒绝连接。请用 ArmSlaveIndex 显式指定（0 基下标）。候选排查：{0}",
                    candidateList);
                return false;
            }
            if (candidates.Length > 1)
            {
                System.Text.StringBuilder names = new System.Text.StringBuilder();
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (names.Length > 0) names.Append("、");
                    names.Append(candidates[i]);
                }
                error = string.Format(
                    "支持 CSP 的从站有 {0} 个（0 基下标 {1}），摆臂步进是哪台不明确，拒绝连接。请用 ArmSlaveIndex 显式指定。候选排查：{2}",
                    candidates.Length, names, candidateList);
                return false;
            }
            chosen = candidates[0];
            return true;
        }

        /// <summary>
        /// 探一台从站是否支持 CSP（读 0x6502 的 bit7）。
        /// 返回 false = 探不到（不是 CiA402 / SDO 读失败），note 里写清是哪一种；探到了则 supportsCsp 给真值。
        /// </summary>
        private static bool TryProbeCsp(DarraEtherCAT.Slave slave, out bool supportsCsp, out string note)
        {
            supportsCsp = false;
            if (slave.CoE == null)
            {
                note = "从站没有 CoE 实例";
                return false;
            }
            var cia = slave.CoE.CiA402;
            if (cia == null)
            {
                note = "不是 CiA402 设备（0x1000 设备档案不是 402）";
                return false;
            }
            CiA402ReadResult<uint> r = cia.TryReadSupportedDriveModes();
            if (!r.Ok)
            {
                note = string.Format("0x6502 读不到（Status={0} AbortCode=0x{1:X8}）", r.Status, r.AbortCode);
                return false;
            }
            // 0x6502 位定义：bit7 = CSP（ETG.6010）。
            supportsCsp = (r.Value & 0x80u) != 0;
            note = string.Format("0x6502=0x{0:X8}", r.Value);
            return true;
        }

        /// <summary>
        /// 摆臂从站身份实读（04 §5E-1）：0x1018 拆 4 次 expedited 读（:1 VendorID / :2 ProductCode /
        /// :3 Revision / :4 Serial）。该系列 CoE 不支持 SdoInfo / 分段 / CompleteAccess（04 §4），
        /// 所以禁止一次读整个 0x1018，只能逐子索引 ≤4B。
        /// 0x1018:1 ≠ 0x0AB5（Green IOT）→ 非本系列设备，拒连。
        /// </summary>
        private bool CheckArmIdentity(DarraEtherCAT.Slave slave, out string error)
        {
            error = null;
            if (slave == null || slave.CoE == null)
            {
                error = "摆臂从站句柄或 CoE 未就绪，读不到 0x1018 身份，拒绝连接。";
                return false;
            }
            uint[] id = new uint[4];
            string[] label = { "Vendor ID", "Product code", "Revision", "Serial number" };
            for (byte sub = 1; sub <= 4; sub++)
            {
                uint value;
                if (!TryReadOdUInt(slave.CoE, 0x1018, sub, out value, out error))
                {
                    error = string.Format("读 0x1018:{0}（{1}）失败：{2}", sub, label[sub - 1], error);
                    return false;
                }
                id[sub - 1] = value;
            }
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                "摆臂身份实读：0x1018 = VID=0x{0:X8} PID=0x{1:X8} REV=0x{2:X8} SN=0x{3:X8}。",
                id[0], id[1], id[2], id[3]));
            if (id[0] != GreenIotVendorId)
            {
                error = string.Format(
                    "摆臂从站 0x1018:1 Vendor ID=0x{0:X8}，不是 Green IOT（#x{1:X8}），拒绝连接。请换回 Green IOT EC1 系列驱动器，或重新生成对应 DENI。",
                    id[0], GreenIotVendorId);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 摆臂从站 CSP 能力硬门（04 §5E-2/3）：PreOp 实读 0x6502（ESI 不给默认值，必须实读）。
        /// 读失败 → 报「0x6502 读不到 + AbortCode」拒连；bit7（掩码 0x80）未置位 → 不支持 CSP，拒连。
        /// **不许降级成 PP/PV 凑合跑摆臂**，也不写死 0x6502 的期望值。
        /// </summary>
        private static bool RequireCspMode(DarraEtherCAT.Slave slave, out string error)
        {
            error = null;
            if (slave == null || slave.CoE == null)
            {
                error = "摆臂从站句柄或 CoE 未就绪，读不到 0x6502，拒绝连接。";
                return false;
            }
            var cia = slave.CoE.CiA402;
            if (cia == null)
            {
                error = "摆臂从站不是 CiA402 设备（0x1000 设备档案不是 402），读不到 0x6502，拒绝连接。";
                return false;
            }
            CiA402ReadResult<uint> r = cia.TryReadSupportedDriveModes();
            if (!r.Ok)
            {
                error = string.Format(
                    "0x6502 读不到（Status={0} AbortCode=0x{1:X8}），无法确认支持 CSP，拒绝连接。确认驱动器型号支持 CSP，并检查 0x6502 是否被 ESI/DENI 正确导出。",
                    r.Status, r.AbortCode);
                return false;
            }
            if ((r.Value & 0x80u) == 0)
            {
                error = string.Format(
                    "0x6502=0x{0:X8}，bit7（CSP，掩码 0x80）未置位，该从站不支持 CSP，拒绝连接。不降级 PP/PV：换支持 CSP 的驱动器，或按实际型号重新导出 DENI。",
                    r.Value);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 按本次解析出的 DC 策略处理摆臂从站：
        /// <list type="bullet">
        /// <item>启用 → 走 <see cref="ArmStepperDcSync0"/> 的原硬门（HasDC / ConfigureDC / Sync0Armed 回读）；</item>
        /// <item>关闭 → 调 <see cref="DarraEtherCAT.Slave.DisableDC"/> 显式置 FreeRun（它会记下用户意图，
        /// 阻止 SDK 的自动启动策略再把 SYNC0 武装回来），并回读当前 DC 状态留证。
        /// 关闭不是「什么都不做」：只跳过 ConfigureDC 而让 DENI 自动配置继续生效，等于说了关却没关。</item>
        /// </list>
        /// DC 关闭时如果从站明确报告了 DC 能力（HasDC=true），<c>DisableDC</c> 失败按配置错误拒连 ——
        /// 因为这时「关闭」这个请求没有被执行，继续升态就是静默违背用户的配置。
        /// </summary>
        private bool ApplyStepperDcPolicy(out string error)
        {
            error = null;
            if (_dcSyncEnabled)
                return ArmStepperDcSync0(out error);

            DarraEtherCAT.Slave s = _stepper;
            if (s == null)
            {
                error = "摆臂从站句柄未就绪，无法应用 DC 关闭策略。";
                return false;
            }
            if (!s.HasDC)
            {
                RaiseDiagnostic(DiagnosticSeverity.Audit,
                    "摆臂从站无 DC 能力（HasDC=false），本次无需关闭；按 FreeRun 运行。");
                return true;
            }

            try
            {
                s.DisableDC();
            }
            catch (Exception ex)
            {
                error = "摆臂从站 DC 关闭失败（DisableDC 抛异常）：" + DiagText.Describe(ex) +
                        "。本次按默认要求关闭 DC，无法确认关闭就不升态；确认现场需要 DC Sync0 时加 --dc-sync 重连。";
                return false;
            }

            RaiseDiagnostic(DiagnosticSeverity.Audit,
                "摆臂从站 DC 已按参数关闭（FreeRun 意图，防止自动策略重新武装）；当前 " + DescribeDcState(s) + "。");
            return true;
        }

        /// <summary>
        /// PreOp 之后、切 SAFEOP 之前给摆臂步进武装 DC Sync0。
        /// 仅当本次连接显式启用 DC（CLI <c>--dc-sync</c> / 环境变量 <see cref="DcSyncEnvVar"/>）时调用；
        /// 默认路径是 <see cref="ApplyStepperDcPolicy"/> 的关闭分支（FreeRun）。
        /// <para>
        /// EC1 的 ESI `&lt;Dc&gt;&lt;OpMode&gt; DC` = AssignActivate `#x300`（SYNC0），CycleTimeSync0=0
        /// （周期不由 ESI 钉死，由主站下发）；手册 0x2003:01 最小同步周期 1000µs → 这台从站
        /// SYNC0 固定 1 ms（<see cref="ArmSync0CycleNs"/>），主站 LoopCycle 仍是 500µs，θ 环不降。
        /// GCAN 等其余从站照旧不动，同步周期按从站区分。
        /// </para>
        /// <para>
        /// 武装必须回读确认：d1090 返 void，不校验就升态 = 允许"这台没武装也进 OP"。
        /// 判据唯一权威 = SDK 公开成员 <c>Slave.Sync0Armed</c>（内部走 native DcSync0Armed / D_1803），
        /// 托管侧不再自算 (DCActive &amp; 0x0300) == 0x0300 &amp;&amp; DCCycle0 &gt; 0；
        /// 该成员为 null（native 答不出来）时按"结论未知"拒绝升态并如实报出，绝不冒充"未武装"。
        /// EC1 的 0x1C32/0x1C33 是 RO（手册 4.1.1），所以不写同步类型，只武装 SYNC0。
        /// </para>
        /// </summary>
        private bool ArmStepperDcSync0(out string error)
        {
            error = null;
            DarraEtherCAT.Slave s = _stepper;
            if (s == null)
            {
                error = "摆臂从站句柄未就绪，无法配 DC 同步。";
                return false;
            }
            if (!s.HasDC)
            {
                error = "本次显式启用了 DC Sync0（--dc-sync / " + DcSyncEnvVar + "），但摆臂步进无 DC 能力（HasDC=false）。" +
                        "要么确认该从站 / 固件支持分布式时钟，要么去掉 --dc-sync 按默认（关闭 DC、FreeRun）运行；" +
                        "不降级冒充「已武装」。";
                return false;
            }

            bool configured;
            try
            {
                configured = s.ConfigureDC(ArmSync0CycleNs, 0, null);
            }
            catch (Exception ex)
            {
                error = "同步未配：摆臂步进 ConfigureDC 抛异常：" + DiagText.Describe(ex);
                return false;
            }

            string dcState = DescribeDcState(s);
            bool armed;
            try
            {
                // [roundSYNC0PUB1] 武装判据唯一权威 = SDK 公开成员 Slave.Sync0Armed
                //   (内部走 native DcSync0Armed / D_1803)。案例不再直连 SDK 内部 DLL 声明,
                //   也不自算 (DCActive & 0x0300) == 0x0300 && DCCycle0 > 0。
                //   null = native 答不出来 (结论未知), 绝不折成"未武装"。
                bool? armedState = s.Sync0Armed;
                if (armedState.HasValue)
                {
                    armed = armedState.Value;
                }
                else
                {
                    // 结论未知: 不冒充"未武装", 如实写"未知"并拒绝切 SAFEOP。
                    armed = false;
                    dcState = "DC SYNC0 武装结论未知（Sync0Armed 为 null，native 未给判据），" + dcState;
                }
            }
            catch (Exception ex)
            {
                armed = false;
                dcState = "读 DC 武装标志失败：" + DiagText.Describe(ex);
            }
            if (!configured || !armed)
            {
                error = string.Format(
                    "同步未配：摆臂步进 DC Sync0 未获确认武装（ConfigureDC={0}，{1}，SYNC0 周期 {2} ns）。" +
                    "本次显式启用了 DC，未获确认就不升态；不需要 DC 时去掉 --dc-sync（默认关闭）。",
                    configured, dcState, ArmSync0CycleNs);
                return false;
            }
            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                "摆臂步进 DC Sync0 已武装：SYNC0 周期 {0} ns（1 ms，落在 0x2003 的 1000~4000µs 窗口），{1}，AssignActivate=#x300。",
                ArmSync0CycleNs, dcState));
            return true;
        }

        /// <summary>摆臂从站的 DC 武装标志实况串，报错 / 诊断用。读不到就如实说读不到。</summary>
        private static string DescribeDcState(DarraEtherCAT.Slave s)
        {
            if (s == null) return "从站句柄为空";
            try
            {
                return string.Format("HasDC={0} DCActive=0x{1:X4} Cycle0={2}ns",
                    s.HasDC, s.DCActive, s.DCCycle0);
            }
            catch (Exception ex)
            {
                return "DC 状态读不到：" + DiagText.Describe(ex);
            }
        }

        /// <summary>
        /// expedited 逐子索引读一个小对象（1~4 字节）。该系列 CoE 只支持这种读法
        /// （SdoInfo=0 / SegmentedSdo=0 / CompleteAccess=0，04 §4），所以不许一次读整个结构体对象。
        /// 读失败 / 空数据 / 超 4 字节一律 false，error 里带 AbortCode。
        /// </summary>
        private static bool TryReadOdUInt(
            DarraEtherCAT.Slave.CoEInstance coe, ushort index, byte sub, out uint value, out string error)
        {
            value = 0;
            error = null;
            if (coe == null)
            {
                error = "从站没有 CoE 实例。";
                return false;
            }
            coe.SDORead(index, sub, out SdoReadResult r, false);
            if (!r.Ok || r.Data == null || r.Data.Length == 0)
            {
                error = string.Format("读 0x{0:X4}:{1} 失败（AbortCode=0x{2:X8}，返回 {3} 字节）。",
                    index, sub, r.AbortCode, r.Data == null ? 0 : r.Data.Length);
                return false;
            }
            if (r.Data.Length > 4)
            {
                error = string.Format("读 0x{0:X4}:{1} 返回 {2} 字节，超过 expedited 上限 4 字节。",
                    index, sub, r.Data.Length);
                return false;
            }
            uint v = 0;
            for (int i = 0; i < r.Data.Length; i++) v |= (uint)r.Data[i] << (8 * i);
            value = v;
            return true;
        }

        /// <summary>把除自有两台外的从站记成旁观者清单，只读不控。</summary>
        private void BuildObserverList(DarraEtherCAT master)
        {
            List<int> observers = new List<int>();
            for (int i = 0; i < master.Slaves.Count; i++)
            {
                if (i == _stepperIndex || i == _gcanIndex) continue;
                observers.Add(i);
            }
            _observerIndices = observers.ToArray();
            _observerStateCache = new byte[_observerIndices.Length];
            _observerScanTicks = 0;
        }

        /// <summary>
        /// 旁观从站状态巡检。只读、只报警：旁观站掉站 / 掉出 OP / 掉 WKC 记诊断事件并如实展示，
        /// 不触发 Fault（总线共享，别人的设备波动不该让倒立摆停机）。
        /// 1 Hz 节流，状态有变化才报一条。慢路径调用（PumpEvents），不进周期路径。
        /// </summary>
        public void ScanObserverSlaves()
        {
            DarraEtherCAT m = _master;
            if (m == null || m.Slaves == null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_observerScanTicks != 0 && now - _observerScanTicks < System.Diagnostics.Stopwatch.Frequency) return;
            _observerScanTicks = now;

            for (int k = 0; k < _observerIndices.Length; k++)
            {
                int i = _observerIndices[k];
                if (i < 0 || i >= m.Slaves.Count) continue;
                DarraEtherCAT.Slave s = m.Slaves[i];
                byte state = (byte)s.State;
                bool lost = s.IsLost;
                byte key = (byte)(state | (lost ? 0x80 : 0));
                if (key == _observerStateCache[k]) continue;
                _observerStateCache[k] = key;
                RaiseDiagnostic(DiagnosticSeverity.Warning, string.Format(
                    "旁观从站 #{0}（{1}）状态变化：State={2}，WKC贡献={3}，IsLost={4}。只报警不停机。",
                    i + 1, s.Name, s.State, s.WcContributed, lost));
            }
        }

        /// <summary>
        /// 旁观从站状态一览，一行一台，如实带上状态 / WKC 贡献 / 是否掉线。
        /// 没有旁观从站时返回空串。慢路径用，不进周期路径。
        /// </summary>
        public string BuildObserverSummary()
        {
            DarraEtherCAT m = _master;
            if (m == null || m.Slaves == null || _observerIndices.Length == 0) return string.Empty;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int k = 0; k < _observerIndices.Length; k++)
            {
                int i = _observerIndices[k];
                if (i < 0 || i >= m.Slaves.Count) continue;
                DarraEtherCAT.Slave s = m.Slaves[i];
                if (sb.Length > 0) sb.Append("；");
                sb.AppendFormat("#{0} {1} State={2} WKC={3}{4}",
                    i + 1, s.Name, s.State, s.WcContributed, s.IsLost ? " 已掉线" : string.Empty);
            }
            return sb.ToString();
        }

        // ============ GCAN 切片识别 ============

        /// <summary>
        /// PreOp 阶段读 GCAN 的 0xF050 在位模块表，识别每个槽位是什么切片。
        /// 0xF050:00 = **表项总数（含结尾字符）**，0xF050:01..:N = 各表项（UDINT），
        /// 与 ESI 其他依赖文件/ESI/GCAN-8200.xml 的 "Module detected list" 逐字对应。
        /// 末项 0x00000001 是结尾字符而非模块，真实模块 = sub0 - 1，由
        /// <see cref="PdoStructs.StripIdentTerminator"/> 按值 + 位置剔掉。
        /// 认不出的 ident、计数切片不是恰好一个、读不到 0xF050 —— 一律 fail closed 拒绝连接，禁止猜。
        /// </summary>
        private bool DetectGcanSlices(DarraEtherCAT.Slave gcan, out GcanImageLayout layout, out string error)
        {
            layout = default(GcanImageLayout);
            error = null;

            DarraEtherCAT.Slave.CoEInstance coe = gcan.CoE;
            if (coe == null)
            {
                error = "GCAN 从站没有 CoE 实例，读不到 0xF050 在位模块表，拒绝连接。";
                return false;
            }
            coe.SDORead(0xF050, 0, out SdoReadResult count, false);
            if (!count.Ok || count.Data == null || count.Data.Length < 1)
            {
                error = string.Format(
                    "读 GCAN 0xF050:00（在位模块表项数）失败（AbortCode=0x{0:X8}），切片识别不出，拒绝连接。",
                    count.AbortCode);
                return false;
            }
            int slots = count.Data[0];
            if (slots < 1 || slots > 32)
            {
                error = string.Format("GCAN 0xF050:00 报表项数 {0}，超出 1..32，拒绝连接。", slots);
                return false;
            }

            uint[] idents = new uint[slots];
            for (int slot = 0; slot < slots; slot++)
            {
                coe.SDORead(0xF050, (byte)(slot + 1), out SdoReadResult r, false);
                if (!r.Ok || r.Data == null || r.Data.Length < 4)
                {
                    error = string.Format(
                        "读 GCAN 0xF050:{0}（槽位 {1} Module Ident）失败（AbortCode=0x{2:X8}），切片识别不出，拒绝连接。",
                        slot + 1, slot + 1, r.AbortCode);
                    return false;
                }
                idents[slot] = BitConverter.ToUInt32(r.Data, 0);
            }

            string summary;
            if (!PdoStructs.BuildGcanLayout(idents, out layout, out summary, out error)) return false;
            RaiseDiagnostic(DiagnosticSeverity.Audit, "GCAN 0xF050 在表项：" + summary +
                "，计数切片=" + PdoStructs.DescribeSlice(layout.CounterSlice) + "。");
            return true;
        }

        /// <summary>
        /// SafeOp 之后回读 0x1C12 / 0x1C13，逐条核对映射条目顺序与结构体字段顺序一致。
        /// 分配列表允许多个 PDO（GCAN 每块在位模块各有一个 TxPdo），也允许 0 个（该侧按 ESI 就是 0 字节），
        /// 判据是"按分配顺序拼接后的条目序 + 总位宽"，不是"恰好一个 PDO"。
        /// 旁观从站不核对映射（不写它们的 PDO、不做它们的 SDO 配置）。
        /// GCAN 的期望条目按 0xF050 识别出的在位切片现拼，不写死某一种切片组合。
        /// <para>
        /// <b>调用时机是硬约束</b>：必须在 DENI 启动参数落定之后 —— Standard 路径 = PreOp→SafeOp 转换完成，
        /// DirectFromPreOp 路径 = PreOp→OP 转换完成。冷启动首连时步进处于出厂 96 bit 映射，
        /// 在 PreOp 提前读会撞上 IP-After 启动参数批次尚未写完，把"参数还没落地"误报成"配置不合法"。
        /// </para>
        /// </summary>
        public bool CheckPdoAssignment(out string error)
        {
            error = null;
            int stepperRxBytes = Marshal.SizeOf(typeof(StepperRxPdo));
            int stepperTxBytes = Marshal.SizeOf(typeof(StepperTxPdo));
            // 实读的分配字原样落诊断（与 GCAN 那两条同口径）：现场要能一眼看到"这次到底读到多少 bit"，
            // 而不是只看到"对不上"的结论。
            string observed;
            if (!CheckAssignment(_stepper, 0x1C12, "步进 RxPDO", StepperRxEntries,
                    stepperRxBytes * 8, out error, out observed)) return false;
            RaiseDiagnostic(DiagnosticSeverity.Audit, "步进 RxPDO 映射实读：" + observed);
            if (!CheckAssignment(_stepper, 0x1C13, "步进 TxPDO", StepperTxEntries,
                    stepperTxBytes * 8, out error, out observed)) return false;
            RaiseDiagnostic(DiagnosticSeverity.Audit, "步进 TxPDO 映射实读：" + observed);
            return CheckGcanAssignment(out error);
        }

        /// <summary>一条 PDO 映射条目的期望值（index:subindex / 位宽 / 所属槽位）。</summary>
        internal struct PdoEntry
        {
            public ushort Index;
            public byte SubIndex;
            public byte BitLen;

            /// <summary>该条目所属切片在 0xF050 里的 0 基槽位。-1 = 不参与槽位索引换算（步进从站）。</summary>
            public int Slot;

            public PdoEntry(ushort index, byte subIndex, byte bitLen)
                : this(index, subIndex, bitLen, -1)
            {
            }

            public PdoEntry(ushort index, byte subIndex, byte bitLen, int slot)
            {
                Index = index;
                SubIndex = subIndex;
                BitLen = bitLen;
                Slot = slot;
            }
        }

        // 条目顺序逐字对照结构体字段顺序，与 GR-EC01 / EC1 系列 ESI 的默认 CSP PDO 一致
        //（04-EC1-ESI分析 §2.3 = 0x1600 行 L2873/L2880/L2887；§2.4 = 0x1A00 行 L3056/L3063/L3070/L3077/L3084/L3091）。
        // 7 个型号（EC-57A-7 / EC1-510 / EC1-530 / EC1-570 / EC1-872 / EC1-57E / ECA1-86E）映射逐条相同，不按型号分支。
        // internal 是给自检用的：结构体字段序 ↔ 条目表 ↔ ESI 三者必须同源互核，任何一处漂了都当场红。
        internal static readonly PdoEntry[] StepperRxEntries =
        {
            new PdoEntry(0x6040, 0, 16),
            new PdoEntry(0x607A, 0, 32),
            new PdoEntry(0x6060, 0, 8)
        };

        internal static readonly PdoEntry[] StepperTxEntries =
        {
            new PdoEntry(0x603F, 0, 16),
            new PdoEntry(0x6041, 0, 16),
            new PdoEntry(0x6064, 0, 32),
            new PdoEntry(0x606C, 0, 32),
            new PdoEntry(0x60FD, 0, 32),
            new PdoEntry(0x6061, 0, 8)
        };

        // 下面四张表逐字照 ESI 其他依赖文件/ESI/GCAN-8200.xml 的各切片 PDO 条目序抄录：
        //   GC2008 RxPdo  = 8×(0x7000:1..8, BOOL, 1 bit) + (0x0000:9, 8 bit 填充)   → 2 B
        //   GC1502 TxPdo  = (0x6003:1..4, UDINT, 32 bit)                            → 16 B，无 RxPdo
        //   GC1602 TxPdo  = (0x6003:1/2, DINT, 32 bit)                              → 8 B
        //   GC1602 RxPdo  = (0x7000:1/2, BOOL, 1 bit) + (0x0000:5, 14 bit 填充)      → 2 B
        // Index 0x0000 的条目是 ESI 填充位，只计入总位宽、不参与 index:subindex 比对。
        // 0xF050 末尾的 0x00000001 是结尾字符（见 PdoStructs.StripIdentTerminator），不在这里建表。
        private static PdoEntry[] BuildGc2008RxEntries(int slot)
        {
            PdoEntry[] e = new PdoEntry[9];
            for (int i = 0; i < 8; i++) e[i] = new PdoEntry(0x7000, (byte)(i + 1), 1, slot);
            e[8] = new PdoEntry(0x0000, 9, 8, slot);
            return e;
        }

        private static PdoEntry[] BuildGc1502TxEntries(int slot)
        {
            PdoEntry[] e = new PdoEntry[4];
            for (int i = 0; i < 4; i++) e[i] = new PdoEntry(0x6003, (byte)(i + 1), 32, slot);
            return e;
        }

        private static PdoEntry[] BuildGc1602TxEntries(int slot)
        {
            return new PdoEntry[]
            {
                new PdoEntry(0x6003, 1, 32, slot),
                new PdoEntry(0x6003, 2, 32, slot)
            };
        }

        private static PdoEntry[] BuildGc1602RxEntries(int slot)
        {
            return new PdoEntry[]
            {
                new PdoEntry(0x7000, 1, 1, slot),
                new PdoEntry(0x7000, 2, 1, slot),
                new PdoEntry(0x0000, 5, 14, slot)
            };
        }

        /// <summary>
        /// ESI 计划里该槽位的 RxPdo 索引（GCAN 模块索引按 0x10 递进，槽位 1 = 0x1600）。
        /// 只用于运行期能力探测，不参与写配置。
        /// </summary>
        private static ushort PlannedRxPdoIndex(int slot)
        {
            return (ushort)(0x1600 + slot * 0x10);
        }

        /// <summary>
        /// GCAN RxPdo 映射对象能力事实的缓存键：本次连接实读到的从站身份 + 模块型号 + RxPdo 索引。
        /// 键的<b>唯一拼装点</b>（别在别处再拼一次），格式 "VendorId:ProductId:RevId:ModuleIdent:RxPdo"。
        /// 身份字段取自扫描期的 0x1018 实读（<c>Slave.VendorId/ProductId/RevId</c>），
        /// 不用 DENI / ESI 的期望值 —— 能力是「型号 + 修订级」事实，换型号或换修订必然换键，自动重新实测。
        /// </summary>
        private static string GcanRxMappingFactKey(DarraEtherCAT.Slave gcan, uint moduleIdent, ushort rxPdoIndex)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:X8}:{1:X8}:{2:X8}:{3:X8}:{4:X4}",
                gcan.VendorId, gcan.ProductId, gcan.RevId, moduleIdent, rxPdoIndex);
        }

        /// <summary>
        /// 按 0xF050 识别出的在位切片（槽位顺序）现拼 GCAN 的期望映射条目，再回读 0x1C12 / 0x1C13 核对。
        /// 总位宽必须等于该侧过程映像字节数 × 8，真实条目（Index != 0）的 index:subindex:位宽 必须逐条对上。
        /// </summary>
        private bool CheckGcanAssignment(out string error)
        {
            error = null;
            uint[] idents = _gcanImage.SlotIdents;
            if (idents == null || idents.Length == 0)
            {
                error = "GCAN 在位切片表为空，无法核对映射。";
                return false;
            }

            List<PdoEntry> rx = new List<PdoEntry>();
            List<PdoEntry> tx = new List<PdoEntry>();
            bool anyRxZeroed = false;
            for (int slot = 0; slot < idents.Length; slot++)
            {
                GcanSliceKind kind = PdoStructs.MapSliceKind(idents[slot]);
                PdoEntry[] sliceRx = null;
                PdoEntry[] sliceTx = null;
                if (kind == GcanSliceKind.Gc2008Do) sliceRx = BuildGc2008RxEntries(slot);
                else if (kind == GcanSliceKind.Gc1502Counter) sliceTx = BuildGc1502TxEntries(slot);
                else if (kind == GcanSliceKind.Gc1602Counter)
                {
                    sliceRx = BuildGc1602RxEntries(slot);
                    sliceTx = BuildGc1602TxEntries(slot);
                }
                else
                {
                    error = string.Format(
                        "GCAN 槽位 {0} 的切片 ident=0x{1:X8} 不认识，无法核对映射，拒绝连接。",
                        slot + 1, idents[slot]);
                    return false;
                }
                if (sliceRx != null)
                {
                    // 按设备真实能力收敛输出期望：运行期确认该槽位 ESI 计划里的 RxPdo :00 是否可 SDO 读。
                    // 只有 abort=0x06020000（对象字典无此对象）才把该方向期望置空并压 0 输出；
                    // 0x05040000（无应答/超时）或其它 abort 一律维持原期望，由 CheckAssignment fail-closed。
                    // 能力事实优先：同一模块型号 + 同一 RxPdo 索引的设备事实只实测一次（进程内缓存），
                    // 后续连接不再发这条必失败的 SDO；跳过探测时留 Debug 说明。
                    ushort plannedRxPdo = PlannedRxPdoIndex(slot);
                    bool zeroed = false;
                    if (_gcan != null && _gcan.CoE != null)
                    {
                        // 键只认本次连接实读到的身份（VendorId/ProductId/RevId 来自扫描期 0x1018 实读），
                        // 不拿 DENI / ESI 的期望值去拼 —— 否则就是拿配置冒充设备事实。
                        string factKey = GcanRxMappingFactKey(_gcan, idents[slot], plannedRxPdo);
                        uint knownAbort;
                        bool fromCache = GcanRxMappingAbortCache.TryGetValue(factKey, out knownAbort);
                        if (fromCache)
                        {
                            // 命中缓存必须留痕（Debug）：写清键内容 + 用的是哪条事实 + 因此跳过了哪一步校验，
                            // 这是现场查"为什么这次没探"的唯一线索。
                            string factText = knownAbort == 0
                                ? "可读"
                                : (knownAbort == SdoAbortObjectDoesNotExist ? "对象不存在" : "其它 abort");
                            string skipped = knownAbort == SdoAbortObjectDoesNotExist
                                ? "跳过本次 SDO 探测，并按该事实把输出方向压 0、跳过 GCAN RxPDO(0x1C12) 内容核对"
                                : "跳过本次 SDO 探测，随后的 0x1C12 映射核对照常执行";
                            DebugNote(string.Format(
                                "GCAN RxPdo 能力事实命中缓存：键={0}（VendorId=0x{1:X8} ProductId=0x{2:X8} " +
                                "RevId=0x{3:X8} ModuleIdent=0x{4:X8} RxPdo=0x{5:X4}），事实={6}（abort=0x{7:X8}）→ {8}。",
                                factKey, _gcan.VendorId, _gcan.ProductId, _gcan.RevId,
                                idents[slot], plannedRxPdo, factText, knownAbort, skipped));
                        }
                        else
                        {
                            SdoReadResult probe;
                            _gcan.CoE.SDORead(plannedRxPdo, 0, out probe, false);
                            if (probe.Ok)
                            {
                                knownAbort = 0;
                                GcanRxMappingAbortCache[factKey] = 0;
                            }
                            else if (probe.AbortCode == SdoAbortObjectDoesNotExist)
                            {
                                knownAbort = probe.AbortCode;
                                GcanRxMappingAbortCache[factKey] = knownAbort;
                            }
                            else
                            {
                                // 传输层失败（无应答 / 超时 / 状态不允许）不是能力事实：不入缓存、不降级，
                                // 由 CheckAssignment 按原期望 fail-closed，下次仍如实重试。
                                knownAbort = probe.AbortCode;
                                DebugNote(string.Format(
                                    "GCAN RxPdo 能力事实未入缓存：键={0} 探测失败 abort=0x{1:X8}（不是「对象不存在」），" +
                                    "期望维持 ESI 表并由 CheckAssignment 如实判失败，下次连接仍会重试。",
                                    factKey, probe.AbortCode));
                            }
                        }
                        if (knownAbort == SdoAbortObjectDoesNotExist)
                        {
                            zeroed = true;
                            anyRxZeroed = true;
                            RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                                "GCAN 从站 #{0} 槽位 {1} RxPDO 0x{2:X4}:00 {3}（abort=0x{4:X8}）→ 该方向按 0 输出配置；输入侧 0x1A03 / {5}B 不变。",
                                _gcan.SlaveNum, slot + 1, plannedRxPdo,
                                fromCache ? "按已实测能力事实判定对象不存在，本次未发 SDO 探测" : "实测对象不存在，已记入能力事实缓存",
                                knownAbort, _gcanImage.InputBytes));
                        }
                    }
                    if (!zeroed) rx.AddRange(sliceRx);
                }
                if (sliceTx != null) tx.AddRange(sliceTx);
            }
            if (anyRxZeroed && rx.Count == 0)
            {
                _gcanImage.OutputBytes = 0;
                _gcanImage.ResetOutputOffset = -1;
            }

            string observed;
            if (anyRxZeroed)
            {
                // 输出方向已按设备真实能力压 0：设备侧 0x1C12 可能是历史悬空赋值，校验它没有意义；
                // 主站侧也不会为 0 输出建 FMMU/SM2。这里只留证据，输入侧照旧严格核对。
                RaiseDiagnostic(DiagnosticSeverity.Warning,
                    "GCAN RxPDO 校验跳过：RxPdo 映射对象不存在，输出方向按 0 配置（不写、不建 FMMU）。");
            }
            else if (!CheckAssignment(_gcan, 0x1C12, "GCAN RxPDO", rx.ToArray(),
                    _gcanImage.OutputBytes * 8, out error, out observed)) return false;
            else RaiseDiagnostic(DiagnosticSeverity.Audit, "GCAN RxPDO 映射实读：" + observed);
            if (!CheckAssignment(_gcan, 0x1C13, "GCAN TxPDO", tx.ToArray(),
                    _gcanImage.InputBytes * 8, out error, out observed)) return false;
            RaiseDiagnostic(DiagnosticSeverity.Audit, "GCAN TxPDO 映射实读：" + observed);
            return true;
        }

        /// <summary>
        /// SafeOp 之后回读 PDO 分配并核对映射条目（时机约束见 <see cref="CheckPdoAssignment"/>）。
        /// <para>
        /// 分配列表按 0x1C1x 的顺序拼接成过程映像，所以这里**不能假定只有一个 PDO**：GCAN-8200 每个在位模块
        /// 各有自己的 TxPdo（SlotPdoIncrement=16），两块模块在位时 0x1C13 就是两条；本侧 0 字节输出时
        /// 0x1C12 则可能一条都没有。两种情况都是合法现实，只有"拼起来的条目序 + 总位宽"才是真判据。
        /// </para>
        /// 真实条目（Index != 0）按拼接顺序逐条核对 index:subindex:位宽；ESI 填充条目（Index == 0）
        /// 只计入总位宽。总位宽必须与 expectedTotalBits 严格相等，对不上就如实报错并拒绝连接。
        /// </summary>
        private static bool CheckAssignment(
            DarraEtherCAT.Slave slave,
            ushort assignmentIndex,
            string label,
            PdoEntry[] expected,
            int expectedTotalBits,
            out string error,
            out string observed)
        {
            error = null;
            observed = null;
            DarraEtherCAT.Slave.CoEInstance coe = slave.CoE;
            if (coe == null)
            {
                error = label + "：从站没有 CoE 实例，无法回读 PDO 分配。";
                return false;
            }
            bool expectsNothing = expectedTotalBits == 0 && (expected == null || expected.Length == 0);
            coe.SDORead(assignmentIndex, 0, out SdoReadResult count, false);
            if (!count.Ok || count.Data == null || count.Data.Length < 1)
            {
                // 本侧按在位模块推导就是 0 字节，且设备明确回"对象/子索引不存在"——
                // 那就是这台从站根本没有该侧的 PDO 分配对象（无 RxPdo 的模块组合），与推导一致，如实放行。
                // 其它失败（超时 / 通讯错）仍 fail closed，不当成"没有"。
                if (expectsNothing &&
                    (count.AbortCode == 0x06020000u || count.AbortCode == 0x06090011u))
                {
                    observed = string.Format(
                        "0x{0:X4} 不存在（AbortCode=0x{1:X8}）：该侧无 PDO 分配对象，本侧按在位模块推导 0B，一致。",
                        assignmentIndex, count.AbortCode);
                    return true;
                }
                error = string.Format("{0}：读 0x{1:X4}:0 失败（AbortCode=0x{2:X8}）。", label, assignmentIndex, count.AbortCode);
                return false;
            }
            int assigned = count.Data[0];
            if (assigned == 0)
            {
                // 一条 PDO 都没分配：只有"本侧按 ESI 推出来就是 0 字节"（例如 GCAN 只有无 RxPdo 的模块）才成立。
                if (expectsNothing)
                {
                    observed = string.Format("0x{0:X4} 未分配任何 PDO（本侧按在位模块推导输出 0B，一致）", assignmentIndex);
                    return true;
                }
                error = string.Format(
                    "{0}：0x{1:X4} 没有分配任何 PDO，但本侧按 ESI 期望 {2} bit（{3} B），映射缺失，拒绝连接。",
                    label, assignmentIndex, expectedTotalBits, expectedTotalBits / 8);
                return false;
            }
            if (assigned > 32)
            {
                error = string.Format("{0}：0x{1:X4} 分配了 {2} 个 PDO，超出合理上限 32，拒绝连接。", label, assignmentIndex, assigned);
                return false;
            }

            // 分配顺序 = 过程映像拼接顺序：按此顺序把所有 PDO 的条目平铺进同一张表再统一比对。
            var idxBuf = new List<ushort>();
            var subBuf = new List<byte>();
            var bitBuf = new List<byte>();
            int totalBits = 0;
            System.Text.StringBuilder dump = new System.Text.StringBuilder();
            for (int p = 0; p < assigned; p++)
            {
                coe.SDORead(assignmentIndex, (byte)(p + 1), out SdoReadResult first, false);
                if (!first.Ok || first.Data == null || first.Data.Length < 2)
                {
                    error = string.Format("{0}：读 0x{1:X4}:{2}（第 {2} 个 PDO 索引）失败（AbortCode=0x{3:X8}）。原始字节 len={4} hex={5}",
                        label, assignmentIndex, p + 1, first.AbortCode,
                        first.Data == null ? 0 : first.Data.Length, FormatSdoBytes(first.Data));
                    return false;
                }
                ushort pdoIndex = BitConverter.ToUInt16(first.Data, 0);
                dump.AppendLine(string.Format(
                    "PDO原始字节 {0} 0x{1:X4}:{2} len={3} hex={4}",
                    label, assignmentIndex, p + 1, first.Data.Length, FormatSdoBytes(first.Data)));

                coe.SDORead(pdoIndex, 0, out SdoReadResult entryCount, false);
                if (!entryCount.Ok || entryCount.Data == null || entryCount.Data.Length < 1)
                {
                    int expectedBits = 0;
                    if (expected != null)
                    {
                        for (int bi = 0; bi < expected.Length; bi++) expectedBits += expected[bi].BitLen;
                    }
                    // GCAN-8200 对未实现 SDO 的对象静默，不回 abort（0xF980/0x9001 同症）；
                    // 另有模块（如 GC1602）的 0x1600 在设备上不可 SDO 读，返回 0x06020000
                    // 「对象不存在」—— 这两种都是"该 PDO 的映射表无法按 SDO 读"，不是映射错误。
                    // 注意：GC1602 的 ESI 里 RxPdo/TxPdo 是 Fixed="0"（GCAN-8200.xml:6905/6925），
                    // 其 abort 不能按"固定 PDO"解释。
                    // 0x1A03/0x1600 已由 0x1C12/0x1C13 分配读到，条目表来自 ESI 模块，位宽与在位模块推导一致。
                    // 多 PDO 分不清归属时仍拒绝。
                    if ((entryCount.AbortCode == 0x05040000u || entryCount.AbortCode == 0x06020000u) &&
                        assigned == 1 &&
                        expected != null &&
                        expected.Length > 0 &&
                        expectedBits == expectedTotalBits)
                    {
                        dump.AppendFormat(
                            "0x{0:X4}:0 映射表读不到（AbortCode=0x{1:X8}）：固定 PDO 或无 SDO 映射对象。分配已读到该 PDO，条目按 ESI 模块表 {2} 条 / {3} bit 核对，不把读不到当成映射错误。",
                            pdoIndex, entryCount.AbortCode, expected.Length, expectedTotalBits);
                        observed = dump.ToString();
                        return true;
                    }
                    error = string.Format("{0}：读 PDO 0x{1:X4}:0 条目数失败（AbortCode=0x{2:X8}）。原始字节 len={3} hex={4}；已读分配：{5}",
                    label, pdoIndex, entryCount.AbortCode,
                    entryCount.Data == null ? 0 : entryCount.Data.Length, FormatSdoBytes(entryCount.Data), dump);
                    return false;
                }
                int entries = entryCount.Data[0];
                if (entries > 255)
                {
                    error = string.Format("{0}：PDO 0x{1:X4} 条目数 {2} 不合理。", label, pdoIndex, entries);
                    return false;
                }
                if (idxBuf.Count + entries > 1024)
                {
                    error = string.Format("{0}：分配列表累计条目数超过 1024，映射明显不对，拒绝连接。", label);
                    return false;
                }

                if (p > 0) dump.Append(" ＋ ");
                dump.AppendFormat("0x{0:X4}[", pdoIndex);
                for (int i = 0; i < entries; i++)
                {
                    coe.SDORead(pdoIndex, (byte)(i + 1), out SdoReadResult e, false);
                    if (!e.Ok || e.Data == null || e.Data.Length < 4)
                    {
                        error = string.Format("{0}：读 0x{1:X4}:{2} 条目失败（AbortCode=0x{3:X8}）。原始字节 len={4} hex={5}",
                        label, pdoIndex, i + 1, e.AbortCode,
                        e.Data == null ? 0 : e.Data.Length, FormatSdoBytes(e.Data));
                        return false;
                    }
                    dump.AppendLine(string.Format(
                        "PDO原始字节 {0} 0x{1:X4}:{2} len={3} hex={4}",
                        label, pdoIndex, i + 1, e.Data.Length, FormatSdoBytes(e.Data)));
                    byte bit = e.Data[0];
                    byte sub = e.Data[1];
                    ushort idx = (ushort)((e.Data[3] << 8) | e.Data[2]);
                    idxBuf.Add(idx);
                    subBuf.Add(sub);
                    bitBuf.Add(bit);
                    totalBits += bit;
                    if (i > 0) dump.Append(" → ");
                    dump.AppendFormat("{0:X4}:{1}/{2}bit", idx, sub, bit);
                }
                dump.Append("]");
            }
            observed = string.Format("0x{0:X4} 分配 {1} 个 PDO / 共 {2} bit：{3}",
                assignmentIndex, assigned, totalBits, dump);

            if (totalBits != expectedTotalBits)
            {
                error = string.Format(
                    "{0}：分配列表 0x{1:X4} 映射总位宽 {2} bit，结构体 / 在位模块推导要求 {3} bit（{4} B），字节布局对不上，拒绝连接。实读：{5}；原始字节：{6}",
                    label, assignmentIndex, totalBits, expectedTotalBits, expectedTotalBits / 8, observed, dump);
                return false;
            }

            // 真实条目（Index != 0）按拼接顺序逐条比对；填充条目不参与序比对，只占位宽。
            int want = 0;
            for (int i = 0; i < idxBuf.Count && want < (expected == null ? 0 : expected.Length); i++)
            {
                if (idxBuf[i] == 0) continue;
                if (!IndexMatches(idxBuf[i], expected[want]) ||
                    subBuf[i] != expected[want].SubIndex ||
                    bitBuf[i] != expected[want].BitLen)
                {
                    error = string.Format(
                        "{0}：分配列表 0x{1:X4} 第 {2} 条真实条目是 {3:X4}:{4} / {5} bit，与结构体字段 / 在位模块顺序要求的 {6:X4}:{7} / {8} bit 不一致，拒绝连接。实读：{9}",
                        label, assignmentIndex, i + 1, idxBuf[i], subBuf[i], bitBuf[i],
                        expected[want].Index, expected[want].SubIndex, expected[want].BitLen, observed);
                    return false;
                }
                want++;
            }
            int wantTotal = expected == null ? 0 : expected.Length;
            if (want != wantTotal)
            {
                error = string.Format(
                    "{0}：分配列表 0x{1:X4} 只核对上 {2} 条真实条目，结构体 / 在位模块要求 {3} 条，拒绝连接。实读：{4}",
                    label, assignmentIndex, want, wantTotal, observed);
                return false;
            }
            return true;
        }

        /// <summary>
        /// ESI 的模块条目带 DependOnSlot，实机既可能照字面索引（0x6003 / 0x7000）报，
        /// 也可能按 SlotIndexIncrement=16 平移。两种位型都认，命中哪种由实读决定、不做臆测；
        /// 子索引与位宽一律严格对。两种都不中就是映射真不对。
        /// </summary>
        private static string FormatSdoBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return "<empty>";
            var hex = new System.Text.StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) hex.Append(' ');
                hex.Append(data[i].ToString("X2"));
            }
            return hex.ToString();
        }

        private static bool IndexMatches(ushort actual, in PdoEntry expected)
        {
            if (actual == expected.Index) return true;
            if (expected.Slot >= 0 && actual == (ushort)(expected.Index + expected.Slot * 0x10)) return true;
            return false;
        }

        /// <summary>
        /// PDO 字节数自检：Marshal.SizeOf 与总线实测的 Outputs/Inputs 字节数逐段比对，
        /// GCAN 那两段按 0xF050 在位模块逐个累加推期望值。对不上如实报错并拒绝连接，绝不猜、绝不迁就。
        /// 实测 0B 时补一句 DENI 侧实况：DENI 缺 &lt;ProcessData&gt; 的话主站根本不会为该站映射过程映像，
        /// 这时根因在 DENI（配置真相）而不是设备，不让报错把人引去查硬件。
        /// </summary>
        public bool CheckPdoSizes(out string error)
        {
            error = null;
            if (_stepper == null || _gcan == null)
            {
                error = "从站句柄未就绪，无法做 PDO 字节数自检。";
                return false;
            }
            if (PdoStructs.VerifySizes(
                    _stepper.OutputsByteCount,
                    _stepper.InputsByteCount,
                    _gcanImage,
                    _gcan.OutputsByteCount,
                    _gcan.InputsByteCount,
                    out error))
            {
                return true;
            }

            System.Text.StringBuilder hint = new System.Text.StringBuilder();
            if (!_deniArmHasProcessData)
            {
                hint.Append("摆臂步进从站在 DENI 里没有 <ProcessData> 节点（PDO 分配未导出），主站不会给它映射过程映像——先修 DENI 再看字节数；");
            }
            if (!_deniGcanHasProcessData)
            {
                hint.Append("GCAN 从站在 DENI 里没有 <ProcessData> 节点（PDO 分配未导出），主站不会给它映射过程映像——先修 DENI 再看字节数；");
            }
            if (hint.Length > 0) error = error + "另：" + hint;
            return false;
        }

        // ============ 状态转换 ============

        /// <summary>
        /// 等两个自有从站的**输入过程映像**连续可读，确认 PDO 会话与应用侧映射都已就绪。
        /// 调用点在 <see cref="Open"/> 进 OP 之后、返回给控制层之前 —— 控制线程是在 Open 返回
        /// 后才创建的，所以本确认一定早于它的第一拍。
        /// <para>
        /// 判据用 SDK 非抛异常的 <c>SlavePdo.TryGetInputsPtr</c>（与 <c>InputsMapping</c> 同一份
        /// 指针 / 同一套 GetIO 状态；返回 false = 未映射 / 本拍 Stale / Busy / 掉线），连续
        /// <see cref="PdoReadyConsecutiveSamples"/> 次成功才放行。GCAN 输入字节为 0 时该侧不参与判据
        /// （按 ESI 本来就没有输入）。
        /// </para>
        /// <para>
        /// 为什么不用去抖兜这一拍：去抖是给**运行中**偶发丢帧用的；启动时序应该是确定性的。
        /// 这里等不到就按配置错误拒连并如实报等待时长与两侧字节数，不静默接受。
        /// </para>
        /// </summary>
        public bool WaitForProcessImageReady(int timeoutMs, out long waitedMs, out string detail)
        {
            waitedMs = 0;
            detail = null;
            DarraEtherCAT.Slave stepper = _stepper;
            DarraEtherCAT.Slave gcan = _gcan;
            if (stepper == null || gcan == null)
            {
                detail = "摆臂 / GCAN 从站句柄未就绪";
                return false;
            }

            int stepperExpected;
            try
            {
                stepperExpected = Marshal.SizeOf(typeof(StepperTxPdo));
            }
            catch (Exception ex)
            {
                detail = "摆臂 TxPDO 结构体大小读取失败：" + DiagText.Describe(ex);
                return false;
            }
            int gcanExpected = _gcanImage.InputBytes;

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int streak = 0;
            int lastStepperBytes = 0;
            int lastGcanBytes = 0;
            while (true)
            {
                bool stepperReady = false;
                bool gcanReady = gcanExpected <= 0;
                try
                {
                    IntPtr pointer;
                    int bytes;
                    stepperReady = stepper.TryGetInputsPtr(out pointer, out bytes) &&
                                   bytes >= stepperExpected;
                    lastStepperBytes = bytes;
                    if (gcanExpected > 0)
                    {
                        gcanReady = gcan.TryGetInputsPtr(out pointer, out bytes) &&
                                    bytes >= gcanExpected;
                        lastGcanBytes = bytes;
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    detail = "PDO 句柄已释放：" + DiagText.Describe(ex);
                    waitedMs = sw.ElapsedMilliseconds;
                    return false;
                }
                catch (Exception ex)
                {
                    detail = "探测 PDO 输入映像抛异常：" + DiagText.Describe(ex);
                    waitedMs = sw.ElapsedMilliseconds;
                    return false;
                }

                if (stepperReady && gcanReady)
                {
                    streak++;
                    if (streak >= PdoReadyConsecutiveSamples)
                    {
                        waitedMs = sw.ElapsedMilliseconds;
                        detail = string.Format(CultureInfo.InvariantCulture,
                            "摆臂输入 {0} B / GCAN 输入 {1} B，连续 {2} 次可读",
                            lastStepperBytes, gcanExpected > 0 ? lastGcanBytes : 0, streak);
                        return true;
                    }
                }
                else
                {
                    streak = 0;
                }

                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    waitedMs = sw.ElapsedMilliseconds;
                    detail = string.Format(CultureInfo.InvariantCulture,
                        "等待 {0} ms 后仍不可读（摆臂输入 {1}/{2} B，GCAN 输入 {3}/{4} B；判据 = 连续 {5} 次可读）",
                        waitedMs, lastStepperBytes, stepperExpected,
                        gcanExpected > 0 ? lastGcanBytes : 0, gcanExpected,
                        PdoReadyConsecutiveSamples);
                    return false;
                }
                Thread.Sleep(1);
            }
        }

        public bool TransitionTo(EcState state, out string error)
        {
            error = null;
            DarraEtherCAT m = _master;
            if (m == null)
            {
                error = "总线会话未打开。";
                return false;
            }
            string stateError;
            bool ok = m.SetState(state, out stateError);
            if (!ok)
            {
                error = string.Format("切到 {0} 失败：{1}（SDK 未给原因）。检查从站 OP 前置条件、DC/Sync0 与过程映像映射。",
                    state, stateError ?? "无说明");
                return false;
            }
            return true;
        }

        /// <summary>主站当前 EtherCAT 状态。S7 用它判有没有掉出 OP。</summary>
        public EcState CurrentState
        {
            get
            {
                DarraEtherCAT m = _master;
                return m == null ? EcState.None : m.State;
            }
        }

        public bool IsBusInOp
        {
            get { return CurrentState == EcState.OP; }
        }

        /// <summary>
        /// 总线级状态快照（只读）：主站 EcState / 真 OP / 链路与两口 PHY / RTcnt / 从站在线数。
        /// <para>
        /// 只读主站缓存的状态与诊断指针，<b>不发帧、不做状态转换、不写 PDO</b>，UI 线程可以按 10–20 Hz 调。
        /// 未打开（<see cref="IsOpen"/>=false）或主站句柄已释放 → false，调用方显示"—"而不是 0。
        /// </para>
        /// 单项读失败不整体失败：对应字段留 null / 0，并打诊断，不拿 0 冒充真值。
        /// </summary>
        public bool TryReadBusStatus(out BusStatusSnapshot status)
        {
            status = default(BusStatusSnapshot);
            DarraEtherCAT m = _master;
            if (m == null || !_open) return false;
            status.Valid = true;

            try
            {
                status.MasterState = m.State.ToString();
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("读主站状态失败：" + DiagText.Describe(ex));
            }

            try
            {
                status.RealOp = m.IsRealOp;
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("读 native 真 OP 裁决失败：" + DiagText.Describe(ex));
            }

            try
            {
                status.LinkState = m.LinkState.ToString();
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("读链路状态失败：" + DiagText.Describe(ex));
            }

            try
            {
                MasterDiagnosticsInfo diag = m.Diagnostics;
                if (diag != null)
                {
                    status.RtHz = diag.RTcnt;
                    status.PrimaryPort = diag.PrimaryPhyLinkState.ToString();
                    status.SecondaryPort = diag.SecondaryPhyLinkState.ToString();
                }
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("读总线链路 / 频率诊断失败：" + DiagText.Describe(ex));
            }

            try
            {
                List<DarraEtherCAT.Slave> slaves = m.Slaves;
                if (slaves != null)
                {
                    status.SlaveCount = slaves.Count;
                    int online = 0;
                    for (int i = 0; i < slaves.Count; i++)
                    {
                        if (!slaves[i].IsLost) online++;
                    }
                    status.OnlineSlaveCount = online;
                    status.OnlineCountKnown = true;
                }
            }
            catch (Exception ex)
            {
                // 在线数读不到时保留 SlaveCount（若已读到），在线侧留"未知"，不把总数冒充成在线数。
                RaiseDiagnostic("读从站在线数失败：" + DiagText.Describe(ex));
            }
            return true;
        }

        // ============ SDO ============

        /// <summary>
        /// 实读摆臂驱动器的标定事实（慢路径专用，绝不进周期路径）。
        /// 该系列 CoE 只支持 expedited ≤4 字节逐子索引（SdoInfo=0 / SegmentedSdo=0 / CompleteAccess=0，04 §4），
        /// 所以一个对象一次读、不拆大对象、不做 Complete Access：
        /// <list type="bullet">
        /// <item>0x2102:0 开闭环细分设置 = 电机单圈脉冲数 P（手册范围 200~60000，禁硬编码 10000）；</item>
        /// <item>0x2003:01/:02 同步周期窗口 [µs]（0x8402 指令超速的根因② 自检用）；</item>
        /// <item>0x608F:01/:02 每转增量（电机侧 / 负载侧）；EC1 全系没有这个对象，读不到是常态：PprReadable=false。
        /// 只有 0x2102 读不到、必须按 OD 定标时才探测（能力事实优先，避免每次连接都问设备没有的对象）；</item>
        /// <item>0x6091:01/:02 齿轮比（电机转数 / 负载转数）；EC1 全系同样没有，读不到就回落 0x2102 标定。</item>
        /// </list>
        /// 0x608F × 0x6091 这两组只影响「摆臂转一圈等于多少位置单位」的来源判定（<see cref="ArmDriveFacts.OdCountsPerArmRev"/>）：
        /// 有它们的机型按 OD 实读合成，没有的（本案例的 EC1）就按 0x2102 × 减速比 —— 两条路都不写死经验系数。
        /// </summary>
        /// 每一项读不到就把该项留 0 并把 AbortCode 记进 <see cref="ArmDriveFacts.Notes"/>，由调用方决定是拒动还是跳过；
        /// 只有句柄 / CoE 不可用才整体 false。
        /// </summary>
        public bool TryReadArmDriveFacts(out ArmDriveFacts facts, out string error)
        {
            facts = new ArmDriveFacts();
            error = null;
            DarraEtherCAT.Slave s = _stepper;
            if (s == null || s.CoE == null)
            {
                error = "摆臂从站句柄或 CoE 未就绪，读不到标定事实。";
                return false;
            }
            DarraEtherCAT.Slave.CoEInstance coe = s.CoE;
            System.Text.StringBuilder notes = new System.Text.StringBuilder();
            uint value;
            string readError;

            if (TryReadOdUInt(coe, 0x2102, 0, out value, out readError))
            {
                facts.MotorCountsPerRev = (int)value;
            }
            else
            {
                notes.Append("0x2102（单圈脉冲数）读不到：").Append(readError).Append("；");
            }

            if (TryReadOdUInt(coe, 0x2003, 1, out value, out readError))
            {
                facts.MinSyncCycleUs = (int)value;
            }
            else
            {
                notes.Append("0x2003:01（最小同步周期）读不到：").Append(readError).Append("；");
            }

            if (TryReadOdUInt(coe, 0x2003, 2, out value, out readError))
            {
                facts.MaxSyncCycleUs = (int)value;
            }
            else
            {
                notes.Append("0x2003:02（最大同步周期）读不到：").Append(readError).Append("；");
            }

            // 能力事实优先：0x2102 已经给出「电机单圈脉冲数」的权威实读时，不再去猜 0x608F / 0x6091。
            // 这两个对象只服务于「0x2102 读不到、必须按 OD 定标」的机型；EC1 全系没有它们，
            // 每次连接都问一遍只会换回 4 条 ObjectDoesNotExist（设备明确"无此对象"，不是故障）。
            // 跳过时留 Debug 说明（Release 编出），便于查「为什么这次没探」。
            if (facts.MotorCountsPerRev > 0)
            {
                // 这条走诊断通道（Info）而不是只写调试器：现场要能在日志里看到"为什么这次没探"，
                // 也正好把「设备无此对象不是故障」讲清楚，避免再出现"为什么少了一堆报错"的疑问。
                RaiseDiagnostic(DiagnosticSeverity.Audit, string.Format(
                    "摆臂标定：0x2102 已实读电机单圈脉冲数 {0}，按能力事实优先跳过可选 OD 探测 " +
                    "0x608F:01/:02、0x6091:01/:02（按 OD 定标用不到；EC1 全系无此对象，不是故障；" +
                    "0x608F 对 0x2102 的交叉校验一并跳过）。",
                    facts.MotorCountsPerRev));
            }
            else
            {
                if (TryReadOdUInt(coe, 0x608F, 1, out value, out readError))
                {
                    facts.PprFromOd = (int)value;
                    facts.PprReadable = true;
                }
                else
                {
                    facts.PprReadable = false;
                    notes.Append("0x608F:01（每转脉冲）读不到：").Append(readError).Append("；");
                }

                // 0x608F:02 / 0x6091:01..02 只给「有齿轮比 + 编码器分辨率定义」的机型用；
                // EC1 全系没有这些对象，读不到就是常态，不进 Notes（否则每台 EC1 都刷三条无意义告警）。
                if (TryReadOdUInt(coe, 0x608F, 2, out value, out readError)) facts.FeedPprFromOd = (int)value;
                if (TryReadOdUInt(coe, 0x6091, 1, out value, out readError)) facts.GearMotorRevs = value;
                if (TryReadOdUInt(coe, 0x6091, 2, out value, out readError)) facts.GearFeedRevs = value;
            }

            facts.Notes = notes.Length == 0 ? null : notes.ToString();
            return true;
        }

        // ============ WKC ============

        /// <summary>
        /// 读本拍 WKC。expected 是配置期固定值，只如实比较，绝不下调 expected 去迁就总线。
        /// </summary>
        public bool TryReadWkc(out ushort actual, out ushort expected)
        {
            actual = 0;
            expected = 0;
            DarraEtherCAT m = _master;
            if (m == null) return false;
            expected = DarraEtherCAT.GetGroupExpectedWKC(m.MasterNumber, 1);
            actual = DarraEtherCAT.GetGroupActualWKC(m.MasterNumber, 1);
            return true;
        }

        /// <summary>
        /// 用 SDO 直读 GC1502 的 0x6003:1..4，和 PDO 遥测对照。
        /// 只读，不使能、不清零；用于区分过程映像映射错误与现场确实没有脉冲。
        /// </summary>
        public bool TryReadGcanCounterSnapshot(out uint[] values, out string error)
        {
            values = new uint[4];
            error = null;
            if (_gcan == null || _gcan.CoE == null)
            {
                error = "GCAN 从站没有 CoE 实例。";
                return false;
            }
            for (byte sub = 1; sub <= 4; sub++)
            {
                if (!TryReadOdUInt(_gcan.CoE, 0x6003, sub, out values[sub - 1], out error))
                    return false;
            }
            return true;
        }

        /// <summary>总线层的原生时序诊断。拿不到就返回 false，前端显示"不可用"而不是 0。</summary>
        public bool TryReadBusTiming(out double avgJitterUs, out double maxJitterUs, out double roundtripUs, out double loadPercent)        {
            avgJitterUs = 0;
            maxJitterUs = 0;
            roundtripUs = 0;
            loadPercent = 0;
            DarraEtherCAT m = _master;
            if (m == null) return false;
            try
            {
                MasterDiagnosticsInfo d = m.Diagnostics;
                if (d == null) return false;
                avgJitterUs = d.BusAvgJitterUs;
                maxJitterUs = d.BusMaxJitterUs;
                roundtripUs = d.BusRoundtripUs;
                loadPercent = d.BusLoadPercent;
                return ControlMath.IsFinite(avgJitterUs) && ControlMath.IsFinite(maxJitterUs);
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("读原生时序诊断失败：" + DiagText.Describe(ex));
                return false;
            }
        }

        // ============ PDO（周期线程独占）============

        public ref readonly StepperTxPdo StepperInput()
        {
            return ref _stepperPdo.InputsMapping<StepperTxPdo>();
        }

        public ref StepperRxPdo StepperOutput()
        {
            return ref _stepperPdo.OutputsMapping<StepperRxPdo>();
        }

        public ref readonly Gcan1602TxPdo Gcan1602Input()
        {
            return ref _gcanPdo.InputsSliceMapping<Gcan1602TxPdo>(_gcanImage.CounterInputOffset);
        }

        public ref readonly Gcan1502TxPdo Gcan1502Input()
        {
            return ref _gcanPdo.InputsSliceMapping<Gcan1502TxPdo>(_gcanImage.CounterInputOffset);
        }

        /// <summary>
        /// 摆杆计数原值（32 位位型）。GC1502 是 UDINT 无符号、GC1602 是 DINT 有符号，
        /// 这里一律按 32 位位型取，回绕差分由 PendulumEngine 用 (uint)(now - prev) 统一做。
        /// </summary>
        public uint PendulumCountRaw()
        {
            if (_gcanImage.CounterSlice == GcanSliceKind.Gc1502Counter)
                return _gcanPdo.InputsSliceMapping<Gcan1502TxPdo>(_gcanImage.CounterInputOffset).CountCh1;
            return unchecked((uint)_gcanPdo
                .InputsSliceMapping<Gcan1602TxPdo>(_gcanImage.CounterInputOffset).CountCh1);
        }

        /// <summary>
        /// 摆杆脉冲频率 [Hz]（GC1502 的 0x6003:2 FreqCh1）。GC1602 没有频率计，恒返回 0。
        /// 只进遥测 / 诊断做 ω 交叉校验，不进控制律（频率计是平均量，不当微分用）。
        /// </summary>
        public uint PendulumFreqHz()
        {
            if (_gcanImage.CounterSlice != GcanSliceKind.Gc1502Counter) return 0;
            return _gcanPdo.InputsSliceMapping<Gcan1502TxPdo>(_gcanImage.CounterInputOffset).FreqCh1;
        }

        /// <summary>
        /// 摆杆第 2 路计数原值（GC1502 0x6003:3 / GC1602 0x6003:2）。本案例不用它算 θ，只如实展示做交叉校验。
        /// 计数切片未识别 = 0。
        /// </summary>
        public uint PendulumCountCh2Raw()
        {
            if (_gcanImage.CounterSlice == GcanSliceKind.Gc1502Counter)
                return _gcanPdo.InputsSliceMapping<Gcan1502TxPdo>(_gcanImage.CounterInputOffset).CountCh2;
            if (_gcanImage.CounterSlice == GcanSliceKind.Gc1602Counter)
                return unchecked((uint)_gcanPdo
                    .InputsSliceMapping<Gcan1602TxPdo>(_gcanImage.CounterInputOffset).CountCh2);
            return 0;
        }

        /// <summary>摆杆第 2 路脉冲频率 [Hz]（只有 GC1502 有频率计）。其余切片恒 0。</summary>
        public uint PendulumFreqCh2Hz()
        {
            if (_gcanImage.CounterSlice != GcanSliceKind.Gc1502Counter) return 0;
            return _gcanPdo.InputsSliceMapping<Gcan1502TxPdo>(_gcanImage.CounterInputOffset).FreqCh2;
        }

        /// <summary>
        /// GCAN 输出方向是否有过程映像。GC1602 正常时 true；按设备真实能力探测到 RxPDO 映射对象
        /// 不存在时（输出方向按 0 配置）为 false —— 此时硬件复位命令不可达，只能软件置零。
        /// </summary>
        public bool GcanOutputsMapped
        {
            get { return _gcanImage.OutputBytes > 0 && _gcanImage.ResetOutputOffset >= 0; }
        }

        /// <summary>
        /// 写 GCAN 输出区。GC2008 的 8 路 DO 是别人的通道，恒写 0 不驱动它；
        /// GC1602 在位时才写计数复位位（bit0=CH1，bit1=CH2）；GC1502 没有 RxPDO，本调用只清 DO。
        /// </summary>
        public void WriteGcanOutputs(ushort resetBits)
        {
            if (_gcanImage.HasDoSlice)
            {
                ref Gcan2008DoPdo doOut = ref _gcanPdo.OutputsSliceMapping<Gcan2008DoPdo>(_gcanImage.DoOutputOffset);
                doOut.DoBits = 0;
            }
            if (_gcanImage.ResetOutputOffset >= 0)
            {
                ref Gcan1602ResetPdo resetOut =
                    ref _gcanPdo.OutputsSliceMapping<Gcan1602ResetPdo>(_gcanImage.ResetOutputOffset);
                resetOut.ResetBits = resetBits;
            }
        }

        // ============ 事件 ============

        // 只记两个计数，不拼字符串、不分配。判据交给 SafetyInterlock 的 S6。
        private void OnPdoFrameLoss(ushort masterIndex, byte group, uint consecutive, uint total)
        {
            Volatile.Write(ref _frameLossConsecutive, (int)consecutive);
            Volatile.Write(ref _frameLossTotal, (int)total);
        }

        /// <summary>
        /// 慢路径诊断出口，由 PendulumEngine 接到 Diagnostic 事件。
        /// 带显式级别：产生方自己声明重要级别，消费方不再靠关键词猜
        /// （连接期取证用 <see cref="DiagnosticSeverity.Audit"/>：GUI 必落盘）。
        /// </summary>
        internal Action<DiagnosticSeverity, string> DiagnosticSink;

        /// <summary>
        /// Debug 级说明（Release 编出）：记录"这次为什么没做某步探测"这类可解释事实。
        /// 不是错误、不占诊断通道；只有调试器 / DebugView 能看到，现场日志保持干净。
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void DebugNote(string text)
        {
            System.Diagnostics.Debug.WriteLine("[BusSession] " + text);
        }

        /// <summary>未声明级别的诊断：消费方照旧走关键词兜底（保持旧行为）。</summary>
        private void RaiseDiagnostic(string text)
        {
            RaiseDiagnostic(DiagnosticSeverity.Unspecified, text);
        }

        /// <summary>带显式级别的诊断；级别由本方法（产生方）声明，不交给消费方猜。</summary>
        private void RaiseDiagnostic(DiagnosticSeverity severity, string text)
        {
            Action<DiagnosticSeverity, string> sink = DiagnosticSink;
            if (sink == null) return;
            try { sink(severity, text); }
            catch (Exception ex)
            {
                // 诊断 sink 失败不得影响周期语义，但要留一条痕迹，不空吞。
                System.Diagnostics.Debug.WriteLine("BusSession 诊断 sink 抛异常：" + DiagText.Describe(ex));
            }
        }

        // ============ 收尾 ============

        /// <summary>
        /// Open 失败时的收尾：把 teardown 自身的错误附加进原始失败原因。
        /// 只报原始错误会把“总线没收干净”吞掉，下一次连接可能踩到半开状态。
        /// </summary>
        private void TeardownAfterOpenFailure(ref string error)
        {
            string teardownError;
            Teardown(out teardownError);
            if (!string.IsNullOrEmpty(teardownError))
                error = (error ?? string.Empty) + "（收尾未完成：" + teardownError + "）";
        }

        /// <summary>
        /// 固定 teardown 顺序：0x6040=0（上限 7 s）→ SafeOp → PreOp → INIT 回读 → 解绑事件 → Abort → Close → Dispose。
        /// INIT 未确认也照样收口（记一条错误后仍走 Abort → Close → Dispose，不把总线句柄挂住），
        /// 该错误由调用方如实上报。总期限 20 s。可重复调用，幂等。
        /// </summary>
        public void Teardown(out string error)
        {
            error = null;
            DarraEtherCAT m = _master;
            _open = false;
            if (m == null) return;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TeardownTotalMs);

            try { m.Events.PDOFrameLoss -= OnPdoFrameLoss; }
            catch (Exception ex)
            {
                error = "解绑 PDOFrameLoss 失败：" + DiagText.Describe(ex);
            }

            // 1. 0x6040=0，把驱动断到 Disable Voltage，上限 7 s。
            try
            {
                if (_stepperPdo != null)
                {
                    DateTime disableDeadline = DateTime.UtcNow.AddMilliseconds(DisableVoltageTimeoutMs);
                    do
                    {
                        ref StepperRxPdo output = ref _stepperPdo.OutputsMapping<StepperRxPdo>();
                        output.ControlWord = CiA402.CwDisableVoltage;
                        output.ModesOfOperation = CiA402.ModeCsp;
                        ref readonly StepperTxPdo input = ref _stepperPdo.InputsMapping<StepperTxPdo>();
                        if (!CiA402.IsOperationEnabled(input.StatusWord)) break;
                        System.Threading.Thread.Sleep(20);
                    } while (DateTime.UtcNow < disableDeadline);
                }
            }
            catch (Exception ex)
            {
                error = "写 0x6040=0 失败：" + DiagText.Describe(ex);
            }

            // 2/3/4. SafeOp → PreOp → INIT，一级一级确认，INIT 没确认就不 Close。
            if (!TeardownState(m, EcState.SafeOp, deadline, ref error)) { FinishTeardown(m, ref error); return; }
            if (!TeardownState(m, EcState.PreOp, deadline, ref error)) { FinishTeardown(m, ref error); return; }
            if (!TeardownState(m, EcState.Init, deadline, ref error)) { FinishTeardown(m, ref error); return; }

            FinishTeardown(m, ref error);
        }

        private bool TeardownState(DarraEtherCAT m, EcState state, DateTime deadline, ref string error)
        {
            try
            {
                string stateError;
                m.SetState(state, out stateError);
                long waitDeadline = (long)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (waitDeadline < 200) waitDeadline = 200;
                long until = Environment.TickCount + waitDeadline;
                while (Environment.TickCount < until)
                {
                    if (m.State == state) return true;
                    System.Threading.Thread.Sleep(10);
                }
                if (m.State == state) return true;
                error = string.Format("teardown 停在 {0} 超时，实测状态 {1}，将强制收口（Abort/Close/Dispose）并如实上报。", state, m.State);
                return false;
            }
            catch (Exception ex)
            {
                error = string.Format("teardown 切 {0} 抛异常：{1}", state, DiagText.Describe(ex));
                return false;
            }
        }

        private void FinishTeardown(DarraEtherCAT m, ref string error)
        {
            try { DarraEtherCAT.Abort(); }
            catch (Exception ex)
            {
                if (error == null) error = "Abort 失败：" + DiagText.Describe(ex);
            }
            try { m.Close(); }
            catch (Exception ex)
            {
                if (error == null) error = "Close 失败：" + DiagText.Describe(ex);
            }
            try { m.Dispose(); }
            catch (Exception ex)
            {
                if (error == null) error = "Dispose 失败：" + DiagText.Describe(ex);
            }
            // 收口只走这一处：句柄一经 Close/Dispose 就不再归本会话所有，
            // 清掉引用让随后的 Dispose() / Teardown() 幂等返回，不重复 Abort/Close/Dispose。
            _master = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            string ignored;
            Teardown(out ignored);
        }
    }
}
