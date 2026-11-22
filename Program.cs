using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FurutaPendulum.Cli;
using FurutaPendulum.Control;
using FurutaPendulum.Ui;

namespace FurutaPendulum
{
    /// <summary>
    /// 旋转倒立摆 Furuta 开源案例启动入口。
    /// 本文件只做引导：DPI 锁定、启动异常兜底、DENI 候选路径定位、UI / CLI 模式分流。
    /// 控制算法在 FurutaPendulum.Control，界面在 FurutaPendulum.Ui，命令行在 FurutaPendulum.Cli。
    /// </summary>
    internal static class Program
    {
        // 退出码与 FurutaPendulum.Cli 的码表同口径：1=一般运行失败，2=参数错误，6=配置错误。
        private const int INTERNAL_ERROR_EXIT_CODE = 1;
        private const int ARGUMENT_ERROR_EXIT_CODE = 2;
        private const int CONFIG_ERROR_EXIT_CODE = 6;

        /// <summary>
        /// 克隆部署网卡名回退开关（布尔旗标，默认关闭）。见 <see cref="BusSession.AllowNicNameFallback"/>。
        /// 只解决"克隆镜像换了机器、DENI 里的网卡 MAC 在本机不存在"这一种情形；
        /// 正常同机部署永远只按 DENI 的 MAC 认卡。
        /// </summary>
        private const string NicNameFallbackFlag = "--allow-nic-name-fallback";

        /// <summary>退出前等待标准输出队列排空的预算 [ms]。父进程正常读时瞬间排空；被管道卡住时按此上限收尾。</summary>
        private const int ConsoleFlushBudgetMs = 5000;

        [STAThread]
        internal static int Main(string[] args)
        {
            // 连接链计时的进程启动基准：保持为入口第一条语句。
            ConnectTiming.Mark("process_start");
            SoftRealTime.ApplyProcess();
            SoftRealTime.EnableTimerResolution();
            try
            {
                int exitCode = Run(args);
                FlushConsoleOutput();
                return exitCode;
            }
            finally
            {
                SoftRealTime.RestoreTimerResolution();
            }
        }

        /// <summary>
        /// 主流程。入口只做两件事：跑完它、按预算把控制台输出排空后再交出退出码。
        /// 输出走队列 + 后台写线程（见 <see cref="CliConsoleSink"/>）：父进程把 stdout 接成管道却不读时，
        /// 同步写会写满约 4 KB 管道缓冲后永久阻塞，命令早就跑完了进程却退不出去、退出码也交不回来。
        /// </summary>
        private static int Run(string[] args)
        {
            DpiLock.Apply();
            InstallStartupExceptionHandlers();
            Thread.CurrentThread.Priority = ThreadPriority.Normal;

            // --help / --version 必须在 DENI 定位与创建引擎之前短路：没有 config.deni 也要能打帮助和版本。
            // --ui-audit 同理：只读界面树做程序化自检，不需要总线 / DENI，供开发与测试机复跑。
            bool wantsHelp = ContainsFlag(args, "--help") || ContainsFlag(args, "--version");
            bool cliMode = IsFirstArg(args, "--cli");
            bool auditMode = ContainsFlag(args, "--ui-audit");
            if (cliMode || wantsHelp || auditMode)
                EnsureCliConsole();

            if (wantsHelp)
                return CliRunner.Run(args, null);

            // DC Sync0 开关（GUI / CLI / 界面自检共用）：--dc-sync / --no-dc-sync 在两种模式下都必须
            // 被解析，不能只在 CliRunner 里生效 —— 否则 `--gui --dc-sync` 会被静默忽略（用户明确给了
            // 旗标却没有生效）。判定与 CLI 完全同一套（CliCommandParser.TryApplyDcSyncFlags →
            // ApplyGlobalBool），这里不写第二份解析。未给旗标时 BusSession 按环境变量
            // FURUTA_DC_SYNC / 默认关闭解析，连接日志里会如实写出来源。
            string dcFlagError;
            if (!CliCommandParser.TryApplyDcSyncFlags(args, out dcFlagError))
            {
                WriteStructuredError("invalid_arguments", dcFlagError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", dcFlagError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            BusSession.DcSyncOverride = CliCommandParser.DcSyncRequested;

            if (auditMode)
            {
                // 审计会真实触发按钮动作（含「连接」），走的是同一条冷启动路径：
                // 首次 GetNetworkAdapters 约 5.6 s，不预热会让按钮动作审计因 3 s 超时预算
                // 误判成「真实 Connect 入口失败」。这里同步预热一次，让审计的耗时与预算匹配。
                try { SdkWarmup.WarmSdkAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { WriteStructuredError("sdk_warmup_failed", ex.Message, "ui_audit", ex); }
                return RunUiAudit();
            }

            bool guiMode = args == null || args.Length == 0 || IsFirstArg(args, "--gui");
            if (!guiMode && !cliMode)
            {
                // 参数错走脚本可判的退出码，不弹窗拦自动化。
                string invalid = "只支持无参数（UI）、--gui、--cli 或 --ui-audit。";
                WriteStructuredError("invalid_arguments", invalid, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", invalid);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // GUI 路径：窗口起来之前后台预热 SDK（native 加载 + 只枚举不活扫的网卡枚举），
            // 不等待、不阻塞 UI 启动；点「连接」时就不用再付这次冷加载。
            // CLI 是一次性进程，不在这里预热：Connect 入口会同步等同一个 Task，总耗时不变。
            if (guiMode)
            {
                SdkWarmup.WarmSdkAsync();
                // GUI 没有 CliRunner 的会话日志：用同一个 DiagnosticLogWriter（同一个日志根
                // %AppData%\DarraPLC\logs\、同一种行格式）记一条 DC 生效来源，与 CLI 的
                // dc_sync 启动记录同口径。
                string dcSource;
                bool dcEnabled = BusSession.ResolveDcSyncEnabled(out dcSource);
                WriteGuiStartupLog("dc_sync",
                    "DC Sync0 " + (dcEnabled ? "启用" : "关闭") + "（来源：" + dcSource + "）" +
                    (dcEnabled
                        ? "；连接时按 HasDC / ConfigureDC / Sync0Armed 硬门武装，未获确认就拒绝升态。"
                        : "；按 FreeRun 运行，连接时显式 DisableDC 防止自动策略重新武装。默认关闭是现场裁定：DENI 关 DC 后总线仍可进 OP，且使能更快。"));
            }

            // 显式 --deni 的取值先校验：给了但不是 .deni 就按参数错退出，绝不静默回退去扫目录
            // （--config 指向 .json 是参数文件，另有语义，不在这里管）。
            string deniArg;
            string deniArgError;
            if (!TryReadOptionValue(args, "--deni", out deniArg, out deniArgError))
            {
                WriteStructuredError("invalid_arguments", deniArgError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", deniArgError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            if (!string.IsNullOrEmpty(deniArg) && !HasDeniExtension(deniArg))
            {
                string invalidDeni = "参数 --deni 只接受 .deni 文件路径，收到 \"" + deniArg + "\"。";
                WriteStructuredError("invalid_arguments", invalidDeni, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", invalidDeni);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // DENI 定位：只产出候选路径，文件本身是否可用由 Connect() 校验。
            string locateError;
            string deniPath = ResolveDeniCandidate(args, out locateError);            if (string.IsNullOrEmpty(deniPath))
            {
                WriteStructuredError("deni_not_found", locateError, "deni_locate", null);
                WriteStartupCrashLog("deni_not_found", "deni_locate", locateError);
                if (guiMode)
                    return FailStartup(true, "Furuta DENI 定位失败", locateError, CONFIG_ERROR_EXIT_CODE);
                // CLI 侧照常交给 CliRunner：status 之类不碰总线的命令仍可用，connect 会明确报配置错。
            }

            // 参数文件实际路径：默认 exe 同级；只有 exe 目录不可写才回退 %AppData%，并明确留痕。
            // 这里不弹窗（启动期弹窗会挡自动化），但把实际路径与回退原因写进日志目录，
            // 界面在「保存参数 / 载入参数」的日志与状态行里显示的也是同一条实际路径。
            string paramPathNote = ParamStore.EffectivePathNote;
            if (!string.IsNullOrEmpty(paramPathNote))
            {
                Debug.WriteLine("参数文件路径提示：" + paramPathNote);
                WriteStartupNotice("params_path", paramPathNote);
            }

            string nicName;
            string nicError;
            if (!TryReadOptionValue(args, "--nic", out nicName, out nicError))
            {
                WriteStructuredError("invalid_arguments", nicError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", nicError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // 摆杆每转计数：现场标定值（线数 × 4 × 传动比）。不给 = 台架默认 2400（600 线 × AB 四倍频）；
            // 显式给 0 = 未标定，控制层会拒绝起摆。给了就标记「显式」，参数文件里的标定不覆盖它。
            string cprText;
            string cprError;
            if (!TryReadOptionValue(args, "--pendulum-cpr", out cprText, out cprError))
            {
                WriteStructuredError("invalid_arguments", cprError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", cprError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            double pendulumCpr;
            if (!TryReadNonNegative(cprText, PendulumEngineOptions.DefaultPendulumCountsPerRev, out pendulumCpr, out cprError))
            {
                WriteStructuredError("invalid_arguments", cprError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", cprError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // 摆臂每转计数：现场标定值（电机每转计数 × 减速比）。命令行 --arm-cpr 优先；
            // 命令行没给就从参数文件恢复 calibration.arm_counts_per_rev（CLI 默认文件或 --config），
            // 文件也没有 = 0，控制层用 0x2102（细分设置）× 减速比 推导。
            // 运行时控制层仍以 0x2102 实读为权威，文件值只在实读不到时兜底（见 PendulumEngine.ResolveArmCalibration）。
            string armCprText;
            string armCprError;
            if (!TryReadOptionValue(args, "--arm-cpr", out armCprText, out armCprError))
            {
                WriteStructuredError("invalid_arguments", armCprError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", armCprError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            int armCpr;
            if (!TryReadArmCountsPerRev(armCprText, out armCpr, out armCprError))
            {
                WriteStructuredError("invalid_arguments", armCprError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", armCprError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // 摆臂减速比：电机转数 / 摆臂转数。不给 = 先用参数文件里的 calibration.arm_gear_ratio，
            // 再没有 = 1.0（无减速）。只在构造引擎时生效，所以必须在 new PendulumEngine 之前定下来。
            string armGearText;
            string armGearError;
            if (!TryReadOptionValue(args, "--arm-gear", out armGearText, out armGearError))
            {
                WriteStructuredError("invalid_arguments", armGearError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", armGearError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            double armGear;
            if (!TryReadArmGearRatio(armGearText, out armGear, out armGearError))
            {
                WriteStructuredError("invalid_arguments", armGearError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", armGearError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            // 摆臂标定的两项构造期参数（arm_cpr / arm_gear）从同一份参数文件恢复：
            // 命令行给了哪一项就以哪一项为准，没给的才从文件取。
            // 摆臂每转计数只对 CLI 恢复 —— GUI 的同一项由 MainForm 按「参数文件 + 界面值」自行恢复，
            // 入口不往控制层写它，GUI 行为保持不变。
            if ((cliMode && string.IsNullOrEmpty(armCprText)) || string.IsNullOrEmpty(armGearText))
            {
                // CLI 若显式给了 --config，就按那份文件恢复；GUI / 无 --config 用默认参数文件。
                string configPath;
                string configError;
                string paramsPath = ParamStore.DefaultPath;
                if (TryReadOptionValue(args, "--config", out configPath, out configError) &&
                    !string.IsNullOrEmpty(configPath))
                {
                    paramsPath = configPath;
                }
                PersistedArmCalibration persisted = ReadPersistedArmCalibration(paramsPath);
                if (string.IsNullOrEmpty(armGearText))
                    armGear = persisted.ArmGearRatio;
                if (cliMode && string.IsNullOrEmpty(armCprText))
                    armCpr = persisted.ArmCountsPerRev;
            }

            // 摆臂从站序号：命令行给的是 1 基（与 status 显示的 "Slave n" 一致），这里减 1 转 0 基下标。
            // 不给 = -1，交给控制层自动识别；歧义 / 越界由 Connect() 拒绝并列候选，不猜。
            string armSlaveText;
            string armSlaveError;
            if (!TryReadOptionValue(args, "--arm-slave", out armSlaveText, out armSlaveError))
            {
                WriteStructuredError("invalid_arguments", armSlaveError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", armSlaveError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            int armSlaveIndex;
            if (!TryReadArmSlaveIndex(armSlaveText, out armSlaveIndex, out armSlaveError))
            {
                WriteStructuredError("invalid_arguments", armSlaveError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", armSlaveError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // 进 OP 方式：standard = PreOp → SafeOp → OP（默认标准顺序）；direct = PreOp → OP 直连
            // （仅诊断用的非标准路径）。2.13.7 的 SafeOp 缺陷已于 2.13.9 修复（commit 20c6de1f），
            // 默认不再绕开 SafeOp；10 s 是 SDK 自己的有界等待窗，不是驱动 grace。
            // 取值不认按参数错退出（退出码 2），不静默退回默认。
            string opEntryText;
            string opEntryError;
            if (!TryReadOptionValue(args, "--op-entry", out opEntryText, out opEntryError))
            {
                WriteStructuredError("invalid_arguments", opEntryError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", opEntryError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }
            OpEntryMode opEntry;
            if (!TryReadOpEntryMode(opEntryText, out opEntry, out opEntryError))
            {
                WriteStructuredError("invalid_arguments", opEntryError, "argument_validation", null);
                WriteStartupCrashLog("invalid_arguments", "argument_validation", opEntryError);
                return ARGUMENT_ERROR_EXIT_CODE;
            }

            // 克隆部署回退开关（默认关闭 = fail closed）：DENI 记录的网卡 MAC 在本机不存在时，
            // 允许按 DENI 记录的网卡名/描述认唯一一张网卡。认错卡会把主站绑到别的网段上，
            // 所以它只由命令行显式开关打开，且回退发生时由 BusSession 走诊断大声留痕。
            // CLI 解析器不认识这个旗标（改解析器不在本案例的改动范围），进 CliRunner 前先剥掉；
            // GUI 用 `FurutaPendulum.exe --gui --allow-nic-name-fallback` 同样生效。
            bool allowNicNameFallback = ContainsFlag(args, NicNameFallbackFlag);
            BusSession.AllowNicNameFallback = allowNicNameFallback;

            // 网卡留空 = 按 DENI Source MAC 自动匹配唯一网卡（DENI 是唯一配置真相，照配置找卡不是猜）；
            // 显式 --nic 仍是覆盖入口，照旧与 DENI MAC 校验。CycleNs 取 PendulumEngineOptions 默认 500000，引导层不覆盖。
            PendulumEngineOptions options = new PendulumEngineOptions
            {
                DeniPath = deniPath,
                NicName = nicName,
                PendulumCountsPerRev = pendulumCpr,
                PendulumCountsPerRevExplicit = !string.IsNullOrEmpty(cprText),
                ArmCountsPerRev = armCpr,
                ArmGearRatio = armGear,
                ArmSlaveIndex = armSlaveIndex,
                OpEntry = opEntry
            };

            using (PendulumEngine engine = new PendulumEngine(options))
            {
                if (cliMode)
                    return CliRunner.Run(StripFlag(args, NicNameFallbackFlag), engine);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(engine));
                return 0;
            }
        }

        private static bool IsFirstArg(string[] args, string flag)
        {
            return args != null && args.Length > 0 &&
                string.Equals(args[0], flag, StringComparison.Ordinal);
        }

        private static bool ContainsFlag(string[] args, string flag)
        {
            if (args == null)
                return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, flag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 去掉指定布尔旗标后的参数数组（引导层自用的旗标不能落到 CliCommandParser 的未知旗标检查里）。
        /// 没命中时原样返回，不复制、不改顺序。
        /// </summary>
        private static string[] StripFlag(string[] args, string flag)
        {
            if (args == null || !ContainsFlag(args, flag))
                return args;
            var kept = new List<string>(args.Length);
            foreach (string arg in args)
            {
                if (!string.Equals(arg, flag, StringComparison.Ordinal))
                    kept.Add(arg);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// 定位 DENI 候选路径：显式参数优先，其次 EtherCATRestore 目录，最后 exe 同级目录。
        /// 只给候选，不做文件校验（校验在 Connect()）；找不到或找到多个都如实报，不猜、不生成。
        /// </summary>
        private static string ResolveDeniCandidate(string[] args, out string error)
        {
            error = string.Empty;

            string explicitPath = FindExplicitDeniPath(args);
            if (!string.IsNullOrEmpty(explicitPath))
                return GetFullPathSafe(explicitPath, out error);

            List<string> found = new List<string>();
            string baseDir;
            try
            {
                baseDir = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch (Exception ex)
            {
                error = "无法解析程序目录: " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            try
            {
                string restore = Path.Combine(baseDir, "EtherCATRestore");
                if (Directory.Exists(restore))
                    found.AddRange(Directory.GetFiles(restore, "*.deni"));
                if (found.Count == 0 && Directory.Exists(baseDir))
                    found.AddRange(Directory.GetFiles(baseDir, "*.deni"));
            }
            catch (Exception ex)
            {
                error = "扫描 .deni 失败: " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            if (found.Count == 0)
            {
                error = "EtherCATRestore 与程序目录都没有 .deni。请把 GUI 导出的 config.deni 放进 EtherCATRestore。";
                return null;
            }
            if (found.Count > 1)
            {
                error = "找到 " + found.Count.ToString(CultureInfo.InvariantCulture) +
                    " 个 .deni，拒绝猜测，请只留一个：" + string.Join(" | ", found.ToArray());
                return null;
            }
            return GetFullPathSafe(found[0], out error);
        }

        /// <summary>
        /// 显式 DENI 参数：--deni &lt;path&gt;，或 --config &lt;path&gt; 指向 .deni（HCFA 惯例）。
        /// --config 指向 .json 时是参数文件，这里不认，留给 CliRunner 解析。
        /// </summary>
        private static string FindExplicitDeniPath(string[] args)
        {
            if (args == null)
                return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--deni", StringComparison.Ordinal) &&
                    !string.Equals(args[i], "--config", StringComparison.Ordinal))
                    continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    continue;
                if (HasDeniExtension(args[i + 1]))
                    return args[i + 1];
            }
            return null;
        }

        private static bool HasDeniExtension(string path)
        {
            try
            {
                return string.Equals(Path.GetExtension(path), ".deni", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DENI 后缀判定失败: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string GetFullPathSafe(string path, out string error)
        {
            error = string.Empty;
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                error = "DENI 路径无效: " + path + " (" + ex.GetType().Name + ": " + ex.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// 读单值参数，照 HCFA 的参数读取惯例：缺值或值是下一个旗标都判参数错。
        /// </summary>
        private static bool TryReadOptionValue(string[] args, string optionName, out string value, out string error)
        {
            value = null;
            error = string.Empty;
            if (args == null)
                return true;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], optionName, StringComparison.Ordinal))
                    continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "参数 " + optionName + " 缺少值。";
                    return false;
                }
                value = args[i + 1];
                return true;
            }
            return true;
        }

        /// <summary>
        /// 解析可选的非负数值参数。没给这个旗标时 value = defaultValue、返回 true；
        /// 给了但不是数值或为负数，如实报参数错，不猜、不夹到 0。
        /// </summary>
        private static bool TryReadNonNegative(string text, double defaultValue, out double value, out string error)
        {
            value = defaultValue;
            error = string.Empty;
            if (string.IsNullOrEmpty(text))
                return true;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0 ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "参数 --pendulum-cpr 需要非负的有限数值（线数 × 4 × 传动比），收到 \"" + text + "\"。";
                value = defaultValue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 解析可选的摆臂从站序号。没给这个旗标时 index = -1（交给控制层自动识别）；
        /// 给了就必须是正整数 —— 命令行口径是 <b>1 基</b>（与 status 显示的 "Slave n" 一致），
        /// 这里减 1 转成 PendulumEngineOptions.ArmSlaveIndex 的 0 基下标。
        /// 0 / 负数 / 非整数如实报参数错（退出码 2），不猜、不夹到边界。
        /// </summary>
        private static bool TryReadArmSlaveIndex(string text, out int index, out string error)
        {
            index = -1;
            error = string.Empty;
            if (string.IsNullOrEmpty(text))
                return true;
            int number;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1)
            {
                error = "参数 --arm-slave 需要正整数从站序号（1 基，与 status 的 Slave n 一致），收到 \"" + text + "\"。";
                return false;
            }
            index = number - 1;
            return true;
        }

        /// <summary>
        /// 解析可选的进 OP 方式 <c>--op-entry</c>：<c>standard</c>（默认，PreOp → SafeOp → OP）
        /// 或 <c>direct</c>（PreOp → OP 直连，仅诊断用）。没给旗标 = 默认 standard；
        /// 给了别的值如实报参数错（退出码 2），不静默退回默认。
        /// </summary>
        private static bool TryReadOpEntryMode(string text, out OpEntryMode mode, out string error)
        {
            mode = OpEntryMode.Standard;
            error = string.Empty;
            if (string.IsNullOrEmpty(text))
                return true;
            if (string.Equals(text, "direct", StringComparison.OrdinalIgnoreCase))
            {
                mode = OpEntryMode.DirectFromPreOp;
                return true;
            }
            if (string.Equals(text, "standard", StringComparison.OrdinalIgnoreCase))
            {
                mode = OpEntryMode.Standard;
                return true;
            }
            error = "参数 --op-entry 只认 direct（PreOp → OP 直连）或 standard（PreOp → SafeOp → OP），收到 \"" + text + "\"。";
            return false;
        }

        /// <summary>
        /// 解析可选的摆臂每转计数 CPR_ARM（= 电机每转计数 × 减速比）。没给这个旗标时 countsPerRev = 0，
        /// 之后由 <see cref="ReadPersistedArmCalibration"/> 从参数文件补（仅 CLI）；补不到才是 0
        /// （交给控制层用 0x2102 细分设置 × 减速比 推导，推导不出拒绝运动）。给了就必须是<b>正整数</b>。
        /// 0 / 负数 / 非整数如实报参数错（退出码 2），不猜、不夹到边界。
        /// </summary>
        private static bool TryReadArmCountsPerRev(string text, out int countsPerRev, out string error)
        {
            countsPerRev = 0;
            error = string.Empty;
            if (string.IsNullOrEmpty(text))
                return true;
            int number;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1)
            {
                error = "参数 --arm-cpr 需要正整数摆臂每转计数（电机每转计数 × 减速比），收到 \"" + text + "\"。";
                return false;
            }
            countsPerRev = number;
            return true;
        }

        /// <summary>
        /// 解析可选的摆臂减速比（= 电机转数 / 摆臂转数）。没给这个旗标时 gearRatio = 1.0（无减速）；
        /// 给了就必须是 &gt; 0 的有限浮点。≤0 / 非数如实报参数错（退出码 2），不猜、不夹到 1。
        /// </summary>
        private static bool TryReadArmGearRatio(string text, out double gearRatio, out string error)
        {
            gearRatio = 1.0;
            error = string.Empty;
            if (string.IsNullOrEmpty(text))
                return true;
            double number;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
                !(number > 0) || double.IsNaN(number) || double.IsInfinity(number))
            {
                error = "参数 --arm-gear 需要正数减速比（电机转数 / 摆臂转数），收到 \"" + text + "\"。";
                gearRatio = 1.0;
                return false;
            }
            gearRatio = number;
            return true;
        }

        /// <summary>
        /// 参数文件的「构造期摆臂标定」两项：减速比供 PendulumEngineOptions.ArmGearRatio，
        /// 每转计数供 PendulumEngineOptions.ArmCountsPerRev（控制层仍以 0x2102 实读为权威，它只在实读不到时兜底）。
        /// </summary>
        private struct PersistedArmCalibration
        {
            /// <summary>摆臂减速比 = 电机转数 / 摆臂转数；恢复不到 = 1.0（无减速）。</summary>
            public double ArmGearRatio;

            /// <summary>摆臂每转计数；文件里没有 / 未定标（0）/ 段被回退 = 0（交给 0x2102 × 减速比 推导）。</summary>
            public int ArmCountsPerRev;
        }

        /// <summary>
        /// 从参数文件（默认 furuta_params.json / CLI --config）的 calibration 段读摆臂减速比与摆臂每转计数
        /// （两项都只在入口构造期生效）。文件不存在 / 读不出 / 段缺失 / 段被整段回退一律不恢复
        /// （减速比 1.0 无减速、每转计数 0 未定标），并留 Debug 痕迹，不猜。
        /// calibration.arm_counts_per_rev 缺键（旧文件）= 0 = 未定标，不拿程序默认的 10000 冒充文件值。
        /// 本次读取就是「默认参数文件的自动载入」：结局必须记进 ParamStore，让 CLI 的 status 行与
        /// 会话日志能说出「整份拒载、已回落默认」及中文原因，不再只有 Debug 输出（静默失败）。
        /// </summary>
        private static PersistedArmCalibration ReadPersistedArmCalibration(string path)
        {
            PersistedArmCalibration result = new PersistedArmCalibration();
            result.ArmGearRatio = 1.0;
            try
            {
                PidGains pid;
                SwingUpParams swing;
                SafetyLimits limits;
                PersistedExtras extras;
                string deniPath;
                string deniNote;
                string error;
                if (!ParamStore.TryLoad(path, null, out pid, out swing, out limits,
                        out extras, out deniPath, out deniNote, out error))
                {
                    ParamStore.ReportDefaultFileLoad(path, false, error, null);
                    Debug.WriteLine("未读取参数文件的摆臂标定（" + path + "）：" + error);
                    return result;
                }
                if (extras == null || extras.Calibration == null || !extras.CalibrationFromFile)
                {
                    // 旧文件没有 calibration 段、或该段不合法被整段回退：两项标定都不恢复。
                    // 回退出来的对象带的是程序默认值（arm_counts_per_rev=10000 / 减速比 1.0），
                    // 那是「未标定」的默认口径，不是文件里的现场标定，不能拿来冒充。
                    ParamStore.ReportDefaultFileLoad(path, true, null, deniNote);
                    return result;
                }

                List<string> notes = new List<string>();
                if (!string.IsNullOrEmpty(deniNote))
                    notes.Add(deniNote);

                double gear = extras.Calibration.ArmGearRatio;
                if (!(gear > 0) || double.IsNaN(gear) || double.IsInfinity(gear))
                {
                    // 可选段里这一个字段不合法也回落了 1.0，同样要让人看见，不能只写 Debug。
                    notes.Add("calibration.arm_gear_ratio 不合法（" + gear + "），按 1.0（无减速）处理。");
                    Debug.WriteLine("参数文件里的 arm_gear_ratio 不合法（" + gear + "），按 1.0 处理。");
                }
                else
                {
                    result.ArmGearRatio = gear;
                }

                int armCpr = extras.Calibration.ArmCountsPerRev;
                if (armCpr < 0 || armCpr > 100000000)
                {
                    // CalibrationParameters.IsValid 已挡住越界值；这里再守一次，绝不把越界数写进控制层。
                    notes.Add("calibration.arm_counts_per_rev 不合法（" + armCpr + "），按未定标（0）处理。");
                    Debug.WriteLine("参数文件里的 arm_counts_per_rev 不合法（" + armCpr + "），按 0 处理。");
                }
                else
                {
                    // 0 = 文件里没这个键 / 未定标：保持 0，交给控制层用 0x2102 × 减速比 推导。
                    result.ArmCountsPerRev = armCpr;
                }

                ParamStore.ReportDefaultFileLoad(path, true, null,
                    notes.Count == 0 ? null : string.Join("；", notes.ToArray()));
                return result;
            }
            catch (Exception ex)
            {
                ParamStore.ReportDefaultFileLoad(path, false,
                    "读取参数文件异常：" + ex.GetType().Name + ": " + ex.Message, null);
                Debug.WriteLine("读取参数文件的摆臂标定异常：" + ex.GetType().Name + ": " + ex.Message);
                return result;
            }
        }

        /// <summary>
        /// 程序化界面自检（无总线 / 无 DENI）：构造主窗体 → 跑布局 / 界面契约 / 按钮矩阵 / 参数 / 按钮动作 /
        /// 标定规则 / 控制参数对话框七套自检 → 打印报告。只读界面树和内存数据，不截图；退出码 0 = 七套都 PASS。
        /// </summary>
        private static int RunUiAudit()
        {
            string paramsPath = ParamStore.DefaultPath;
            string backupPath = paramsPath + ".audit-bak";
            bool hadParams = false;
            try
            {
                if (File.Exists(paramsPath))
                {
                    File.Copy(paramsPath, backupPath, true);
                    hadParams = true;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (PendulumEngine engine = new PendulumEngine(new PendulumEngineOptions()))
                using (MainForm form = new MainForm(engine))
                {
                    form.CreateControl();
                    form.Show();
                    Application.DoEvents();
                    form.PerformLayout();
                    Application.DoEvents();

                    string layout = form.RunLayoutAudit();
                    string contract = form.RunContractAudit();
                    string buttonMatrix = form.RunButtonMatrixAudit();
                    string parameter = form.RunParameterAudit();
                    string buttonActions = form.RunButtonActionAudit();
                    // 标定规则 / 控制参数对话框自检是纯对象判定，与界面 / 总线无关；放在这里统一出口，台架一条命令跑全套。
                    string calibrationRules = MainForm.RunCalibrationRuleAudit();
                    string controlParams = MainForm.RunControlParamUiAudit();
                    Console.WriteLine(layout);
                    Console.WriteLine(contract);
                    Console.WriteLine(buttonMatrix);
                    Console.WriteLine(parameter);
                    Console.WriteLine(buttonActions);
                    Console.WriteLine(calibrationRules);
                    Console.WriteLine(controlParams);

                    bool layoutPass = layout.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool contractPass = contract.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool buttonMatrixPass = buttonMatrix.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool parameterPass = parameter.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool buttonActionsPass = buttonActions.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool calibrationRulesPass = calibrationRules.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool controlParamsPass = controlParams.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0;
                    bool pass = layoutPass && contractPass && buttonMatrixPass && parameterPass &&
                                buttonActionsPass && calibrationRulesPass && controlParamsPass;
                    Console.WriteLine(pass ? "AUDIT RESULT: PASS" : "AUDIT RESULT: FAIL");
                    return pass ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("AUDIT RESULT: FAIL（" + ex.GetType().Name + ": " + ex.Message + "）");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                try
                {
                    if (hadParams)
                    {
                        if (File.Exists(backupPath)) File.Copy(backupPath, paramsPath, true);
                        if (File.Exists(backupPath)) File.Delete(backupPath);
                    }
                    else if (File.Exists(paramsPath))
                    {
                        File.Delete(paramsPath);
                    }
                }
                catch (Exception cleanup)
                {
                    Debug.WriteLine("界面自检参数文件恢复失败：" + cleanup.GetType().Name + ": " + cleanup.Message);
                }
            }
        }

        private static void InstallStartupExceptionHandlers()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine("未处理异常模式设置失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            Application.ThreadException += (sender, e) =>
            {
                Exception ex = e != null ? e.Exception : null;
                string detail = ex == null ? "ThreadException" : ex.GetType().Name + ": " + ex.Message;
                WriteStartupCrashLog("ui_thread_exception", "ui", detail);
                WriteStructuredError("ui_thread_exception", detail, "ui", ex);
                ShowStartupError("Furuta 界面异常", detail);
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Exception ex = e != null ? e.ExceptionObject as Exception : null;
                string detail = ex == null ? "UnhandledException" : ex.GetType().Name + ": " + ex.Message;
                WriteStartupCrashLog("unhandled_exception", "runtime", detail);
                WriteStructuredError("unhandled_exception", detail, "runtime", ex);
            };
        }

        /// <summary>
        /// 启动失败收口：GUI 弹窗说明原因（双击绝不静默闪退），CLI 靠 stderr + 结构化错误文件。
        /// </summary>
        private static int FailStartup(bool guiMode, string title, string body, int exitCode)
        {
            if (guiMode)
                ShowStartupError(title, body);
            return exitCode;
        }

        private static void ShowStartupError(string title, string body)
        {
            try
            {
                MessageBox.Show(
                    body ?? string.Empty,
                    title ?? "Furuta",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("启动错误弹窗失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 启动期「非错误但必须让人看见」的提示落盘（例如参数文件回退到 %AppData%、发现旧路径参数文件）。
        /// 落点与启动错误同目录：%LocalAppData%\DarraEtherCAT\FurutaPendulum\logs\params_path_notice.txt。
        /// </summary>
        /// <summary>
        /// GUI 启动期往统一日志根写一条记录：用与 CLI / GUI 完全相同的 <see cref="DiagnosticLogWriter"/>
        /// （同一落点 %AppData%\DarraPLC\logs\、同一行格式），不另造日志文件。写失败只留 Debug 痕迹，
        /// 不阻断启动（DC 记录是取证信息，不是启动前提）。
        /// </summary>
        private static void WriteGuiStartupLog(string kind, string message)
        {
            try
            {
                using (DiagnosticLogWriter log = new DiagnosticLogWriter())
                {
                    log.Write(kind, message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GUI 启动日志写入失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void WriteStartupNotice(string code, string message)
        {
            try
            {
                string dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "params_path_notice.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\r\n" +
                    "code=" + (code ?? string.Empty) + "\r\n" +
                    "实际参数文件：" + ParamStore.DefaultPath + "\r\n" +
                    (message ?? string.Empty) + "\r\n",
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("启动提示落盘失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 启动期可读错误落盘：%LocalAppData%\DarraEtherCAT\FurutaPendulum\logs\startup_last_error.txt。
        /// </summary>
        private static void WriteStartupCrashLog(string code, string stage, string error)
        {
            try
            {
                string dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "startup_last_error.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\r\n" +
                    "code=" + (code ?? string.Empty) + "\r\n" +
                    "stage=" + (stage ?? string.Empty) + "\r\n" +
                    (error ?? string.Empty) + "\r\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("启动错误落盘失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 结构化错误：stderr 一行 JSON + startup_last_error.json 机器可读副本。
        /// </summary>
        private static void WriteStructuredError(string code, string message, string stage, Exception exception)
        {
            string stackTrace = exception != null ? (exception.StackTrace ?? string.Empty) : string.Empty;
            if (stackTrace.Length > 500)
                stackTrace = stackTrace.Substring(0, 500);
            string json =
                "{\"status\":\"error\",\"code\":\"" + EscapeJson(code) +
                "\",\"message\":\"" + EscapeJson(message) +
                "\",\"stage\":\"" + EscapeJson(stage) +
                "\",\"stack\":\"" + EscapeJson(stackTrace) + "\"}";
            try
            {
                Console.Error.WriteLine(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("结构化错误写 stderr 失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            try
            {
                string dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "startup_last_error.json"), json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("结构化错误落盘失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string GetLogDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DarraEtherCAT",
                "FurutaPendulum",
                "logs");
        }

        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        /// <summary>
        /// exe 是 WinExe，CLI 模式不挂控制台就没有 stdout，管道重定向也抓不到。
        /// </summary>
        private static void EnsureCliConsole()
        {
            try
            {
                if (!AttachConsole(AttachParentProcess))
                    AllocConsole();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CLI 控制台挂接失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            ConfigureConsoleStreams();
        }

        private static void ConfigureConsoleStreams()
        {
            try
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                // 换成非阻塞队列写线程（内部会 SetOut/SetError）：命令线程只入队，
                // 下游管道写不进去时卡住的是后台写线程，不会让进程退不出去。
                CliConsoleSink.Install(stdout, stderr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CLI 标准流初始化失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 退出前把控制台输出队列排空。超预算（通常 = 父进程没有读走 stdout，写线程被管道卡住）
        /// 说明输出没法完整交付 —— 落一条可查的证据，然后照常退出：退出码比输出更重要，
        /// 后台写线程是 IsBackground，进程退出即被回收，不会再拖住调用方。
        /// </summary>
        private static void FlushConsoleOutput()
        {
            if (!CliConsoleSink.Installed)
                return;
            try
            {
                int pending = CliConsoleSink.PendingCount;
                if (CliConsoleSink.Flush(ConsoleFlushBudgetMs))
                    return;
                WriteConsoleFlushTimeoutNotice(pending);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("控制台输出排空失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 输出排空超时证据：落 %LocalAppData%\DarraEtherCAT\FurutaPendulum\logs\console_flush_timeout.txt。
        /// 典型原因是调用方把 stdout 接成管道却等进程退出后才读 —— 那不是命令失败，退出码照常返回。
        /// </summary>
        private static void WriteConsoleFlushTimeoutNotice(int pendingBlocks)
        {
            try
            {
                string dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "console_flush_timeout.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\r\n" +
                    "code=console_flush_timeout\r\n" +
                    "detail=标准输出在 " + ConsoleFlushBudgetMs.ToString(CultureInfo.InvariantCulture) +
                    " ms 内没有排空，仍有 " + pendingBlocks.ToString(CultureInfo.InvariantCulture) +
                    " 块未写出。常见原因：调用方把 stdout/stderr 接成管道但等进程退出后才读取，" +
                    "管道缓冲（约 4 KB）写满后写线程被阻塞。命令本身已执行完毕，退出码照常返回。\r\n",
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("控制台排空超时证据落盘失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
