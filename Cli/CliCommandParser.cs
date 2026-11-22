using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>一条命令的静态描述（命令总表的唯一真相，help 直接从这里渲染）。</summary>
    internal sealed class CommandSpec
    {
        public string Name;
        public string Summary;
        public string Syntax;
        public string[] Aliases;
        public bool Dangerous;
        public string ExitCodes;
        /// <summary>取值旗标（不含前导 --）。</summary>
        public string[] ValueFlags;
        /// <summary>布尔旗标（不含前导 --）。</summary>
        public string[] BoolFlags;
        /// <summary>
        /// 本命令的 --timeout 是不是毫秒别名（= --timeout-ms，归它自己的起摆截止）：只有 swingup / setswingup。
        /// 其余命令的 --timeout 一律是单命令执行超时（秒）。
        /// </summary>
        public bool TimeoutAliasMs;

        public bool Matches(string token)
        {
            if (string.Equals(Name, token, StringComparison.OrdinalIgnoreCase)) return true;
            if (Aliases == null) return false;
            for (int i = 0; i < Aliases.Length; i++)
                if (string.Equals(Aliases[i], token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool IsValueFlag(string name)
        {
            return Contains(ValueFlags, name);
        }

        public bool IsBoolFlag(string name)
        {
            return Contains(BoolFlags, name);
        }

        private static bool Contains(string[] set, string name)
        {
            if (set == null) return false;
            for (int i = 0; i < set.Length; i++)
                if (string.Equals(set[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>命令总表（设计稿 5 / 6）：23 条，含全部别名。</summary>
    internal static class CommandTable
    {
        public static readonly CommandSpec[] All = new CommandSpec[]
        {
            new CommandSpec {
                Name = "status", Aliases = new[] { "st" }, Dangerous = false,
                Syntax = "status [--watch]",
                Summary = "当前状态 + 总线 + 关键量一次快照",
                ExitCodes = "0 / 6",
                BoolFlags = new[] { "watch" }, ValueFlags = new string[0] },
            new CommandSpec {
                Name = "connect", Aliases = new[] { "con" }, Dangerous = false,
                Syntax = "connect [--timeout <sec>] [--config <path>]",
                Summary = "建总线，进 OP（不使能；默认 standard = PreOp → SafeOp → OP，--op-entry direct 才是 PreOp 直连）",
                ExitCodes = "0 / 4 / 5 / 6 / 9",
                ValueFlags = new[] { "timeout", "config" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "disconnect", Aliases = new[] { "disc" }, Dangerous = false,
                Syntax = "disconnect [--force]",
                Summary = "安全收尾后断开",
                ExitCodes = "0 / 5 / 9",
                ValueFlags = new string[0], BoolFlags = new[] { "force" } },
            new CommandSpec {
                Name = "enable", Aliases = new[] { "en" }, Dangerous = true,
                Syntax = "enable [--seconds <n>]",
                Summary = "使能摆臂电机（输出上电，目标保持静止）",
                ExitCodes = "0 / 7 / 8 / 9 / 10",
                ValueFlags = new[] { "seconds" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "disable", Aliases = new[] { "dis2", "off" }, Dangerous = false,
                Syntax = "disable",
                Summary = "去使能（输出下电，保留总线）",
                ExitCodes = "0 / 9",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "swingup", Aliases = new[] { "su" }, Dangerous = true,
                Syntax = "swingup [--timeout-ms <n>]  (别名 --timeout，单位 ms)",
                Summary = "起摆（能量注入 + 摆臂回中 + 往复泵）",
                ExitCodes = "0 / 5 / 7 / 8 / 10",
                TimeoutAliasMs = true,
                ValueFlags = new[] { "timeout-ms", "timeout" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "balance", Aliases = new[] { "bal" }, Dangerous = true,
                Syntax = "balance [--seconds <n>]",
                Summary = "转平衡闭环（Enabled 手动近顶捕获 / Swinging / Balancing）",
                ExitCodes = "0 / 7 / 8 / 10",
                ValueFlags = new[] { "seconds" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "stop", Aliases = new string[0], Dangerous = false,
                Syntax = "stop",
                Summary = "受控停（QuickStop 路径）：0x0002 斜坡 → 确认 QuickStopActive → 0x0000 去使能 → Stopped，清 EStopLatch",
                ExitCodes = "0 / 7 / 9",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "estop", Aliases = new[] { "es", "e-stop" }, Dangerous = false,
                Syntax = "estop [--reason <文本>]",
                Summary = "急停（最高优先级，清零 + 断使能 + 锁存）",
                ExitCodes = "0 / 9",
                ValueFlags = new[] { "reason" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "fault-reset", Aliases = new[] { "fr", "reset" }, Dangerous = false,
                Syntax = "fault-reset",
                Summary = "清故障与急停锁存（= 控制层 Stop()）",
                ExitCodes = "0 / 7 / 8",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "setpid", Aliases = new[] { "pid" }, Dangerous = false,
                Syntax = "setpid [--ka <f>] [--kad <f>] [--kai <f>] [--acc-max <rad/s²>] [--arm-rate-max <rad/s>] [--kp <f>] [--ki <f>] [--kd <f>] [--kp-phi <f>] [--ki-phi <f>] [--kd-phi <f>] [--td-lp-ms <f>] [--out-min <f>] [--out-max <f>] [--out-rate <f>] [--max-i <f>] [--deadband <f>]   (增益取值带符号=相对增减；--ka/--kad/--kai 与 --kp/--ki/--kd 都是历史字段，不影响平衡，接受只为旧参数文件往返)",
                Summary = "设平衡参数（真正生效：加速度 / 参考角速度限幅、内环 φ 三增益、微分低通、输出与积分限幅、死区；--ka/--kad/--kai 是历史参数，不改平衡，仍接受以便旧参数文件往返；耦合符号为台架实测只读值）",
                ExitCodes = "0 / 2",
                ValueFlags = new[] { "ka", "kad", "kai", "acc-max", "arm-rate-max", "kp", "ki", "kd", "kp-phi", "ki-phi", "kd-phi", "td-lp-ms", "out-min", "out-max", "out-rate", "max-i", "deadband" },
                BoolFlags = new string[0] },
            new CommandSpec {
                Name = "setswingup", Aliases = new[] { "su-set" }, Dangerous = false,
                Syntax = "setswingup [--energy-gain <f>] [--ke <f>] [--amax <f>] [--kphi <f>] [--kphidot <f>] [--pump-amp <rad/s²>] [--pump-hz <Hz>] [--theta-switch <rad>] [--omega-switch <rad/s>] [--phi-switch <rad>] [--hold-cycles <n>] [--hold-decay <0..10>] [--timeout-ms <n>] [--energy-extract <0..1>] [--energy-deadband <0..0.2>] [--extract-gain <rad/s²/J>] [--center-band <rad>] [--capture-arm-rate <0.5..6 rad/s>] [--prepare-approach <0..1.5 rad>] [--prepare-energy-margin <0..0.1>] [--commit-arm-rate <rad/s>] [--commit-omega <0..10 rad/s>] [--commit-hold-cycles <1..200>]",
                Summary = "设起摆参数（泵幅单位 rad/s²，泵频 Hz；能量抽取/死区/抽取增益/软中心带/捕获臂速上限可在起摆中热改；捕获交棒六项在起摆中锁定；缺键 = 0 按出厂默认生效）",
                ExitCodes = "0 / 2 / 8",
                TimeoutAliasMs = true,
                ValueFlags = new[] { "energy-gain", "ke", "amax", "kphi", "kphidot", "pump-amp", "pump-hz", "theta-switch", "omega-switch", "phi-switch", "hold-cycles", "timeout-ms", "timeout",
                    "energy-extract", "energy-deadband", "extract-gain", "center-band", "capture-arm-rate",
                    "prepare-approach", "prepare-energy-margin", "commit-arm-rate", "commit-omega",
                    "commit-hold-cycles", "hold-decay" },
                BoolFlags = new string[0] },
            new CommandSpec {
                Name = "save-config", Aliases = new[] { "save" }, Dangerous = false,
                Syntax = "save-config [--path <f>]",
                Summary = "当前参数落盘（默认 exe 同级 furuta_params.json；exe 目录不可写才回退 %AppData% 并打警告）",
                ExitCodes = "0 / 6",
                ValueFlags = new[] { "path" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "load-config", Aliases = new[] { "load" }, Dangerous = false,
                Syntax = "load-config [--path <f>]",
                Summary = "从盘读参数（只在 Disconnected/Connected/Stopped 允许）",
                ExitCodes = "0 / 6 / 8",
                ValueFlags = new[] { "path" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "telemetry", Aliases = new[] { "tm" }, Dangerous = false,
                Syntax = "telemetry [--hz <1..20>] [--once] [--seconds <n>]",
                Summary = "遥测流式输出（1 Hz 默认，上限 20 Hz）",
                ExitCodes = "0 / 2 / 7 / 130",
                ValueFlags = new[] { "hz", "seconds" }, BoolFlags = new[] { "once" } },
            new CommandSpec {
                Name = "watch", Aliases = new[] { "w" }, Dangerous = false,
                Syntax = "watch [--interval <sec>] [--seconds <n>]",
                Summary = "周期单行摘要（长跑监视）",
                ExitCodes = "0 / 2 / 7 / 130",
                ValueFlags = new[] { "interval", "seconds" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "dashboard", Aliases = new[] { "dash", "d" }, Dangerous = false,
                Syntax = "dashboard [--hz <1..20>] [--once]",
                Summary = "终端实时面板（状态/摆杆/摆臂/驱动器/起摆/平衡/标定），原地刷新；--once 打一帧给脚本",
                ExitCodes = "0 / 2 / 5 / 130",
                ValueFlags = new[] { "hz" }, BoolFlags = new[] { "once" } },
            new CommandSpec {
                Name = "tune", Aliases = new[] { "t" }, Dangerous = false,
                Syntax = "tune [--hz <5..20>] [--seconds <n>] [--loop theta|phi|both]",
                Summary = "实时整定视图（双环 P/I/D 分项 + 输出 + 饱和/跟随误差；回车=停止观察）",
                ExitCodes = "0 / 2 / 7 / 8 / 130",
                ValueFlags = new[] { "hz", "seconds", "loop" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "quit", Aliases = new[] { "q", "exit", "x" }, Dangerous = false,
                Syntax = "quit",
                Summary = "安全收尾后退出（仅 REPL 有意义）",
                ExitCodes = "0 / 1 / 5 / 7 / 9 / 130",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "help", Aliases = new[] { "?", "h" }, Dangerous = false,
                Syntax = "help [<命令>]",
                Summary = "命令表 / 单条命令语法与退出码",
                ExitCodes = "0 / 2",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "version", Aliases = new[] { "ver" }, Dangerous = false,
                Syntax = "version",
                Summary = "版本 + 构建戳 + 绑定件 SHA256（只读显示）",
                ExitCodes = "0",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "setlimits", Aliases = new[] { "il" }, Dangerous = false,
                Syntax = "setlimits [--theta-trip <rad>] [--theta-lost <rad>] [--phi-err-max <rad>] [--arm-min-count <count>] [--arm-max-count <count>] [--omega-arm-max <rad/s>] [--jeq <f>] [--mgl <f>]",
                Summary = "设安全联锁阈值（判定仍在控制层 S1–S13）；摆臂软限位只接受绝对 0x6064 counts",
                ExitCodes = "0 / 2",
                ValueFlags = new[] { "theta-trip", "theta-lost", "phi-err-max", "arm-min-count", "arm-max-count", "omega-arm-max", "jeq", "mgl",
                    // 已废除的旧角度口径旗标：保留在解析表里只为给出「改用 counts」的明确拒绝原因，不写进 Syntax。
                    "phi-min", "phi-max" },
                BoolFlags = new string[0] },
            new CommandSpec {
                Name = "zero", Aliases = new[] { "z" }, Dangerous = true,
                Syntax = "zero pendulum | zero arm",
                Summary = "零点校准（重标定）：pendulum=摆杆重标定（下垂位=θ 0）；arm=摆臂重标定（当前点设为坐标 0，起始位置恒为 0，使能即自动归零）",
                ExitCodes = "0 / 6 / 8 / 10",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "recenter", Aliases = new[] { "rc", "recenter-arm" }, Dangerous = true,
                Syntax = "recenter [--seconds <n>]",
                Summary = "把摆臂开到行程中点并在到位后居中规整化（新零点 = 区间中点，行程跨度不变）",
                ExitCodes = "0 / 5 / 7 / 8 / 10",
                ValueFlags = new[] { "seconds" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "normalize", Aliases = new[] { "norm" }, Dangerous = true,
                Syntax = "normalize",
                Summary = "位置规整化：当前 0x6064 设为 φ=0，左右限位按同一 delta 平移（行程跨度不变）；只在 Connected/Stopped",
                ExitCodes = "0 / 6 / 8 / 10",
                ValueFlags = new string[0], BoolFlags = new string[0] },
            new CommandSpec {
                Name = "calibrate-radius", Aliases = new[] { "cal-radius", "cr" }, Dangerous = true,
                Syntax = "calibrate-radius [--accel <20..200 rad/s²>] [--pulse-ms <30..150>] [--pairs <1..8>]",
                Summary = "r/l 主动标定：摆杆下垂静止时给 ±加速度脉冲，实测 φ̈/θ̈ 反推 r/l 并写入 limits.radius_ratio",
                ExitCodes = "0 / 5 / 7 / 8 / 10",
                ValueFlags = new[] { "accel", "pulse-ms", "pairs" }, BoolFlags = new string[0] },
            new CommandSpec {
                Name = "log-path", Aliases = new[] { "lp" }, Dangerous = false,
                Syntax = "log-path",
                Summary = "打印当前日志文件路径",
                ExitCodes = "0",
                ValueFlags = new string[0], BoolFlags = new string[0] },
        };

        public static CommandSpec Find(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            for (int i = 0; i < All.Length; i++)
                if (All[i].Matches(token)) return All[i];
            return null;
        }

        /// <summary>危险命令（需要 --yes 或 REPL 确认）：enable / swingup / balance / zero。</summary>
        public static bool IsDangerous(string canonicalName)
        {
            CommandSpec spec = Find(canonicalName);
            return spec != null && spec.Dangerous;
        }
    }

    /// <summary>解析后的一条命令（规范名 + 旗标 + 位置参数）。</summary>
    internal sealed class ParsedCommand
    {
        public CommandSpec Spec;
        public string Raw;
        public readonly Dictionary<string, string> Flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Positional = new List<string>();

        public string Name { get { return Spec != null ? Spec.Name : string.Empty; } }

        public bool Has(string flag)
        {
            return Flags.ContainsKey(flag);
        }

        public string Value(string flag)
        {
            string v;
            return Flags.TryGetValue(flag, out v) ? v : null;
        }

        public bool TryDouble(string flag, out double value)
        {
            value = 0;
            string raw = Value(flag);
            if (raw == null) return false;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public bool TryInt(string flag, out int value)
        {
            value = 0;
            string raw = Value(flag);
            if (raw == null) return false;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>写了哪些 set* 旗标（不含 --timeout 这类别名，由调用方归并）。</summary>
        public bool AnyFlag()
        {
            return Flags.Count > 0;
        }
    }

    /// <summary>解析结果。</summary>
    internal sealed class ParseOutcome
    {
        public CliOptions Options = new CliOptions();
        public readonly List<ParsedCommand> Commands = new List<ParsedCommand>();
        public string Error;
        public int ErrorCode = ExitCodes.InvalidArguments;
        /// <summary>stdin 为交互控制台且未给命令 / --script → 进 REPL。</summary>
        public bool WantRepl;
    }

    /// <summary>全局旗标 + 命令行解析（含 ";" 序列与 --script）。</summary>
    internal static class CliCommandParser
    {
        /// <summary>布尔全局旗标。</summary>
        private static readonly string[] GlobalBool = { "json", "quiet", "color", "yes", "help", "version", "dc-sync", "no-dc-sync" };

        /// <summary>
        /// 本次进程的 DC Sync0 选择：null = 没给旗标（交 BusSession 按环境变量 / 默认关闭解析）。
        /// 只做「值留底」，连接时由 <see cref="Control.BusSession.DcSyncOverride"/> 统一生效。
        /// </summary>
        internal static bool? DcSyncRequested;

        /// <summary>取值全局旗标。</summary>
        private static readonly string[] GlobalValue = { "config", "log", "result", "timeout", "script", "pendulum-cpr", "arm-cpr", "arm-gear", "arm-slave", "nic", "op-entry" };

        /// <summary>解析 --cli 之后的全部参数。args 已去掉 --cli 前缀。</summary>
        public static ParseOutcome Parse(string[] args)
        {
            var outcome = new ParseOutcome();
            CliOptions options = outcome.Options;

            bool sawCommand = false;
            var pending = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i];
                if (string.IsNullOrEmpty(token)) continue;

                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    string name;
                    string inline;
                    if (!TrySplitFlag(token, out name, out inline))
                    {
                        outcome.Error = "空旗标";
                        return outcome;
                    }
                    bool isBool = Contains(GlobalBool, name);
                    bool isValue = Contains(GlobalValue, name);

                    // --arm-gear / --op-entry 是启动期旗标（构造控制层时给定，运行期没有改写口）：独立 token 无论写在
                    // 命令前还是命令后都按全局取值收下，与入口 Program 的取值口径一致；藏在命令串里的
                    // （如 "connect --arm-gear 5"）留给命令级核对，由 Execute 如实报"未生效"。
                    bool startupOnly = isValue &&
                        (string.Equals(name, "arm-gear", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, "op-entry", StringComparison.OrdinalIgnoreCase));

                    // 命令名之后的旗标一律归该命令（--timeout 在命令名之后 = 只管这一条：
                    // swingup / setswingup 是毫秒别名，其余命令是这条的执行超时秒数）。
                    if ((sawCommand && !startupOnly) || !(isBool || isValue))
                    {
                        pending.Add(token);
                        continue;
                    }

                    if (isBool)
                    {
                        if (inline != null)
                        {
                            outcome.Error = "布尔旗标 --" + name + " 不接受 = 取值";
                            return outcome;
                        }
                        if (!ApplyGlobalBool(options, name))
                        {
                            outcome.Error = "未知旗标 --" + name;
                            return outcome;
                        }
                        continue;
                    }

                    string value = inline;
                    if (value == null)
                    {
                        if (i + 1 >= args.Length)
                        {
                            outcome.Error = "旗标 --" + name + " 缺少取值";
                            return outcome;
                        }
                        value = args[++i];
                    }
                    string valueError;
                    if (!ApplyGlobalValue(options, name, value, out valueError))
                    {
                        outcome.Error = valueError;
                        return outcome;
                    }
                    continue;
                }

                // 第一个非旗标 token = 命令名；之后的全部进命令解析。
                sawCommand = true;
                pending.Add(token);
            }

            if (pending.Count == 0)
            {
                // 无命令：给了 --script 就是脚本模式，否则交由调用方按 stdin 是否交互决定 REPL。
                outcome.WantRepl = string.IsNullOrEmpty(options.ScriptPath);
                return outcome;
            }

            // 命令序列：单个参数里用 ";" 分隔，或已拆开的多个 token 串成一条。
            string commandText = JoinForSequence(pending);
            List<string> segments = SplitSequence(commandText);
            for (int s = 0; s < segments.Count; s++)
            {
                ParsedCommand command;
                string error;
                int code;
                if (!TryParseCommand(segments[s], out command, out error, out code))
                {
                    outcome.Error = error;
                    outcome.ErrorCode = code;
                    return outcome;
                }
                outcome.Commands.Add(command);
            }

            // 全局旗标在 --cli 之后任意位置都能放（设计稿 2.1）：命令名之后出现的同样要生效。
            // --timeout 例外：写在命令名之后只管那一条命令（swingup / setswingup 还是毫秒别名），
            // 不回灌全局 —— 全局 --timeout 只认写在命令名之前的那份。
            ApplyGlobalsFromCommands(outcome);
            return outcome;
        }

        /// <summary>把命令行里出现的全局旗标回灌进 CliOptions（--timeout 除外，它按位置分全局 / 单命令两种口径）。</summary>
        private static void ApplyGlobalsFromCommands(ParseOutcome outcome)
        {
            for (int i = 0; i < outcome.Commands.Count; i++)
            {
                foreach (KeyValuePair<string, string> flag in outcome.Commands[i].Flags)
                {
                    string name = flag.Key.ToLowerInvariant();
                    if (string.Equals(name, "timeout", StringComparison.Ordinal)) continue;
                    // --arm-gear 只在启动期生效：命令串里给的那份不回灌全局，免得把"没生效的值"当生效值显示。
                    // 它怎么处置由 CliSession.Execute 就地核对并如实报。
                    if (string.Equals(name, "arm-gear", StringComparison.Ordinal)) continue;
                    if (Contains(GlobalBool, name)) ApplyGlobalBool(outcome.Options, name);
                    else if (Contains(GlobalValue, name) && flag.Value != null && flag.Value != "1")
                        ApplyGlobalValue(outcome.Options, name, flag.Value, out string ignored);
                }
            }
        }

        /// <summary>把 pending token 串回命令文本（保留原有空格语义）。</summary>
        private static string JoinForSequence(List<string> tokens)
        {
            if (tokens.Count == 1) return tokens[0];
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(tokens[i]);
            }
            return sb.ToString();
        }

        /// <summary>按 ";" 分段（命令序列）。空段忽略。</summary>
        public static List<string> SplitSequence(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            string[] parts = text.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length > 0) list.Add(part);
            }
            return list;
        }

        /// <summary>解析一条命令行：命令名 + 旗标 + 位置参数。</summary>
        public static bool TryParseCommand(string line, out ParsedCommand command, out string error, out int errorCode)
        {
            command = null;
            error = null;
            errorCode = ExitCodes.InvalidArguments;

            List<string> tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                error = "空命令";
                return false;
            }

            CommandSpec spec = CommandTable.Find(tokens[0]);
            if (spec == null)
            {
                error = "未知命令 \"" + tokens[0] + "\"，用 help 看命令表";
                return false;
            }

            command = new ParsedCommand { Spec = spec, Raw = line };
            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    string name;
                    string inline;
                    if (!TrySplitFlag(token, out name, out inline))
                    {
                        error = "空旗标";
                        return false;
                    }
                    bool isValue = spec.IsValueFlag(name) || Contains(GlobalValue, name);
                    bool isBool = spec.IsBoolFlag(name) || Contains(GlobalBool, name);
                    if (!isValue && !isBool)
                    {
                        error = "命令 " + spec.Name + " 不认识旗标 --" + name;
                        return false;
                    }
                    if (isBool)
                    {
                        if (inline != null)
                        {
                            error = "布尔旗标 --" + name + " 不接受 = 取值";
                            return false;
                        }
                        command.Flags[name] = "1";
                        continue;
                    }
                    // --name=value：负数取值不被当成旗标（--kp=-0.25）。
                    if (inline != null)
                    {
                        command.Flags[name] = inline;
                        continue;
                    }
                    if (i + 1 >= tokens.Count)
                    {
                        error = "旗标 --" + name + " 缺少取值";
                        return false;
                    }
                    command.Flags[name] = tokens[++i];
                    continue;
                }
                command.Positional.Add(token);
            }
            return true;
        }

        /// <summary>空白切分，双引号内保持整体。</summary>
        public static List<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(line)) return tokens;
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Length = 0; }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        /// <summary>
        /// 拆 --name 与 --name=value 两种写法。等号形式让负数取值不被当成新旗标（setpid --kp=-0.25）。
        /// 返回 false = 旗标名为空。
        /// </summary>
        private static bool TrySplitFlag(string token, out string name, out string inlineValue)
        {
            name = token.Substring(2);
            inlineValue = null;
            int eq = name.IndexOf('=');
            if (eq >= 0)
            {
                inlineValue = name.Substring(eq + 1);
                name = name.Substring(0, eq);
            }
            return name.Length > 0;
        }

        private static bool ApplyGlobalBool(CliOptions options, string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "json": options.Json = true; return true;
                case "quiet": options.Quiet = true; return true;
                case "color": options.Color = true; return true;
                case "yes": options.Yes = true; return true;
                case "help": options.Help = true; return true;
                case "version": options.Version = true; return true;
                // DC Sync0 开关：只影响本次进程后续连接（BusSession 在 Open 时读取）。默认关闭；
                // --no-dc-sync 用于覆盖环境变量 FURUTA_DC_SYNC 的启用值。
                case "dc-sync":
                case "no-dc-sync":
                    ApplyDcSyncFlag(name);
                    return true;
                default: return false;
            }
        }

        /// <summary>是否是 DC Sync0 开关旗标（命令名 + 语义的唯一判据，CLI / GUI 共用）。</summary>
        internal static bool IsDcSyncFlag(string name)
        {
            return string.Equals(name, "dc-sync", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "no-dc-sync", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>应用一个 DC 开关旗标：--dc-sync = 启用，--no-dc-sync = 关闭（后出现的覆盖先出现的）。</summary>
        private static void ApplyDcSyncFlag(string name)
        {
            DcSyncRequested = string.Equals(name, "dc-sync", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// GUI 入口的 DC 开关扫描。<b>复用 CLI 同一套旗标判定</b>（<see cref="IsDcSyncFlag"/> +
        /// <see cref="ApplyGlobalBool"/>），不复制第二份解析：GUI 模式不经过 <see cref="Parse"/>，
        /// 若不在这里扫一遍，<c>--gui --dc-sync</c> 会被静默忽略 —— 用户明确给了旗标却没有生效，
        /// 属于静默吞掉意图。布尔旗标不接受 <c>=取值</c>，与 CLI 解析同一条规则。
        /// 返回 false 时 error 是可直接展示的中文参数错（调用方按退出码 2 收口）。
        /// </summary>
        internal static bool TryApplyDcSyncFlags(string[] args, out string error)
        {
            error = null;
            if (args == null) return true;
            CliOptions scratch = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i];
                if (string.IsNullOrEmpty(token) || !token.StartsWith("--", StringComparison.Ordinal))
                    continue;
                string name;
                string inline;
                if (!TrySplitFlag(token, out name, out inline))
                {
                    error = "空旗标";
                    return false;
                }
                if (!IsDcSyncFlag(name)) continue;
                if (inline != null)
                {
                    error = "布尔旗标 --" + name + " 不接受 = 取值";
                    return false;
                }
                ApplyGlobalBool(scratch, name);
            }
            return true;
        }

        private static bool ApplyGlobalValue(CliOptions options, string name, string value, out string error)
        {
            error = null;
            switch (name.ToLowerInvariant())
            {
                case "config":
                    options.ConfigPath = value;
                    return true;
                case "log":
                    options.LogPath = value;
                    return true;
                case "result":
                    options.ResultPath = value;
                    return true;
                case "script":
                    options.ScriptPath = value;
                    return true;
                case "nic":
                    options.NicName = value;
                    return true;
                case "pendulum-cpr":
                    double cpr;
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out cpr) || cpr < 0)
                    {
                        error = "--pendulum-cpr 需要非负数值（线数×4×传动比），收到 \"" + value + "\"";
                        return false;
                    }
                    options.PendulumCpr = cpr;
                    return true;
                case "arm-cpr":
                    // 摆臂每转计数 = 电机每转计数 × 减速比。正整数（1 起），0 / 负数 / 非数一律参数错，
                    // 不猜、不夹到 0 —— 想让控制层自己推导就别给这个旗标。
                    int armCpr;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out armCpr) || armCpr < 1)
                    {
                        error = "--arm-cpr 需要正整数摆臂每转计数（电机每转计数 × 减速比），收到 \"" + value + "\"";
                        return false;
                    }
                    options.ArmCpr = armCpr;
                    return true;
                case "arm-gear":
                    // 摆臂减速比 = 电机转数 / 摆臂转数。正的有限浮点，≤0 / 非数一律参数错。
                    double armGear;
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out armGear) ||
                        !(armGear > 0) || double.IsNaN(armGear) || double.IsInfinity(armGear))
                    {
                        error = "--arm-gear 需要正数减速比（电机转数 / 摆臂转数），收到 \"" + value + "\"";
                        return false;
                    }
                    // 减速比在构造控制层时定死（入口按第一个取值），这里同口径取第一个，
                    // 免得后给的值被显示成生效值而控制层实际用的是前一个。
                    if (options.ArmGearRatio.HasValue) return true;
                    options.ArmGearRatio = armGear;
                    return true;
                case "arm-slave":
                    // 1 基从站序号（与 status 的 "Slave n" 一致）。0 / 负数 / 非整数一律参数错，不猜、不夹。
                    int armSlave;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out armSlave) || armSlave < 1)
                    {
                        error = "--arm-slave 需要正整数从站序号（1 基），收到 \"" + value + "\"";
                        return false;
                    }
                    options.ArmSlaveNumber = armSlave;
                    return true;
                case "timeout":
                    int seconds;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                    {
                        error = "--timeout 需要正整数秒，收到 \"" + value + "\"";
                        return false;
                    }
                    options.TimeoutSeconds = seconds;
                    return true;
                case "op-entry":
                    // 进 OP 方式。控制层已在进程入口按同一取值构造完毕，这里只做取值校验并留底回显。
                    OpEntryMode entry;
                    if (string.Equals(value, "direct", StringComparison.OrdinalIgnoreCase))
                        entry = OpEntryMode.DirectFromPreOp;
                    else if (string.Equals(value, "standard", StringComparison.OrdinalIgnoreCase))
                        entry = OpEntryMode.Standard;
                    else
                    {
                        error = "--op-entry 只认 direct（PreOp → OP 直连）或 standard（PreOp → SafeOp → OP），收到 \"" + value + "\"";
                        return false;
                    }
                    options.OpEntry = entry;
                    return true;
                default:
                    error = "未知旗标 --" + name;
                    return false;
            }
        }

        /// --arm-slave 的实现见 ApplyGlobalValue 的 case "arm-slave"（1 基序号 → ArmSlaveNumber）。

        private static bool Contains(string[] set, string name)
        {
            for (int i = 0; i < set.Length; i++)
                if (string.Equals(set[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------- 帮助文本 ----------

        public static string BuildHelpText()
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("FurutaPendulum CLI 无头模式");
            sb.AppendLine();
            sb.AppendLine("用法:");
            sb.AppendLine("  FurutaPendulum.exe --cli                         交互 REPL");
            sb.AppendLine("  FurutaPendulum.exe --cli <命令> [参数]           一次性单命令");
            sb.AppendLine("  FurutaPendulum.exe --cli \"A; B; C\"              命令序列（同一进程，状态保持）");
            sb.AppendLine("  FurutaPendulum.exe --cli --script f.txt         脚本文件（等价 stdin 管道）");
            sb.AppendLine("  FurutaPendulum.exe --ui-audit                   界面程序化自检（布局 + 界面契约 + 参数自测，无需 DENI / 总线）");
            sb.AppendLine();
            sb.AppendLine("全局旗标:");
            sb.AppendLine("  --json              JSON Lines 输出（机器可读）");
            sb.AppendLine("  --quiet             只输出 ack / error");
            sb.AppendLine("  --color             终端着色（管道下自动关）");
            sb.AppendLine("  --yes               全程免确认（enable/swingup/balance/zero）");
            sb.AppendLine("  --config <path>     控制配置 JSON（启动即加载）");
            sb.AppendLine("  --log <path>        日志落点（默认 %AppData%\\DarraPLC\\logs\\；旧路径 %LocalAppData%\\DarraEtherCAT\\FurutaPendulum\\logs\\ 只读，仅用于查历史）");
            sb.AppendLine("  --result <path>     结构化结果 JSON 落点（默认同目录 last_result.json）");
            sb.AppendLine("  --timeout <sec>     单条命令执行超时（秒）：到点中断该条命令的等待、按超时失败收口，不急停；不给 = 各命令既有默认超时不变");
            sb.AppendLine("  --script <path>     从文件读命令");
            sb.AppendLine("  --pendulum-cpr <n>  摆杆每转计数 = 线数×4×传动比（机器标定；不给 = 台架默认 2400 = 600 线 × AB 四倍频；给 0 = 未标定则拒绝起摆）。GUI 参数文件会记进 calibration.pendulum_cpr 并在未显式给本旗标时恢复，显式值优先");
            sb.AppendLine("  --arm-cpr <n>       摆臂每转计数 = 电机每转计数 × 减速比（机器标定；正整数，只在未连接/已连接/已停可改）。优先本旗标；不给 = CLI 启动先从参数文件 calibration.arm_counts_per_rev 恢复（运行时 0x2102 实读仍优先，文件值只在实读不到时兜底），文件也没有才由 0x2102（开闭环细分设置）× 减速比 自动推导，推导不出就拒绝运动并报差异");
            sb.AppendLine("  --arm-gear <x>      摆臂减速比 = 电机转数 / 摆臂转数（正浮点；不给 = 先读参数文件 calibration.arm_gear_ratio，再没有 = 1.0 无减速）。只在启动期生效（φ̇ 与 0x606C rpm 的换算也走它），运行中给值按参数错拒绝");
            sb.AppendLine("  --arm-slave <n>     摆臂从站序号，1 基（与 status 的 \"Slave n\" 一致，内部转 0 基）。不给=自动（唯一非 GCAN CSP CiA402 从站）；歧义/越界=拒绝连接并列候选");
            sb.AppendLine("  --nic <name>        网卡接口名（Name 或 Description）。不给 = 按 DENI Source MAC 自动匹配唯一网卡；显式给则与 DENI MAC 校验。多网卡 / 自动匹配失败时建议显式指定");
            sb.AppendLine("  --op-entry <mode>   进 OP 方式（只接受 standard / direct，其它值按参数错拒绝；不给 = standard 默认）：standard = PreOp → SafeOp → OP（默认标准顺序）；direct = PreOp → OP 直连（仅诊断用，非标准路径）。只在构造控制层时生效，运行中给值按参数错拒绝");
            sb.AppendLine("  --dc-sync           本次连接启用摆臂从站 DC Sync0（默认关闭：按 FreeRun 运行，DENI 仍是拓扑与周期的唯一真相；关闭 DC 时总线实测仍可进 OP）。GUI 无命令行时可用环境变量 FURUTA_DC_SYNC=1 启用");
            sb.AppendLine("  --no-dc-sync        显式关闭 DC Sync0（用于覆盖环境变量 FURUTA_DC_SYNC 的启用值；默认本来就是关闭）");
            sb.AppendLine("  --help / --version  打命令总表 / 版本后退");
            sb.AppendLine();
            sb.AppendLine("三层超时（各管一层，互不替代）:");
            sb.AppendLine("  --timeout <sec>     单命令执行超时：只管这一条命令跑多久，写在命令名之前 = 全局每条都管，");
            sb.AppendLine("                      写在命令名之后 = 只管这一条（优先于全局）。到点只中断等待 + 失败收口，");
            sb.AppendLine("                      不急停；退出码 1（errorCode=timeout），swingup 归它既有的 5 / FaultCode:SwingUpTimeout。");
            sb.AppendLine("                      swingup / setswingup 的 --timeout 是毫秒别名（= --timeout-ms），归它们自己的起摆截止。");
            sb.AppendLine("  命令看门狗 60 s     REPL 无人值守：60 秒无输入且仍在 Swinging/Balancing → Stop() + 急停，退出码 7。");
            sb.AppendLine("  运动看门狗 300 s    无头连续运动上限：到点自动 Stop()，算正常完成，退出码 0。");
            sb.AppendLine("  另有各命令自身截止（connect / enable / stop 的内部预算、swingup 的起摆截止）。");
            sb.AppendLine("                      swingup 的起摆截止只用 --timeout-ms 给：它同时是控制层 S12 联锁与 CLI 等待的上限，");
            sb.AppendLine("                      全局 --timeout <秒> 只封顶 CLI 等待、不改控制层截止（更短时先中断等待并如实说明）。");
            sb.AppendLine();
            sb.AppendLine("命令总表（* = 危险命令，需要 --yes 或 REPL 确认）:");
            sb.AppendLine();
            sb.AppendLine("  命令            别名                    说明");
            sb.AppendLine("  --------------  ----------------------  ------------------------------------------");
            for (int i = 0; i < CommandTable.All.Length; i++)
            {
                CommandSpec spec = CommandTable.All[i];
                string alias = spec.Aliases != null && spec.Aliases.Length > 0 ? string.Join(" / ", spec.Aliases) : "—";
                string prefix = (spec.Dangerous ? "*" : " ") + spec.Name;
                sb.Append("  ").Append(prefix).Append(' ', Math.Max(1, 16 - prefix.Length));
                sb.Append(alias).Append(' ', Math.Max(1, 24 - alias.Length));
                sb.AppendLine(spec.Summary);
            }
            sb.AppendLine();
            sb.AppendLine("退出码: 0 成功 / 1 一般失败 / 2 参数错 / 3 SDK 激活失败 / 4 连接失败 / 5 总线反馈故障 /");
            sb.AppendLine("        6 配置参数错 / 7 安全联锁 / 8 状态机拒绝 / 9 设备驱动故障 / 10 需要确认 / 130 取消");
            sb.AppendLine();
            sb.AppendLine("安全约定:");
            sb.AppendLine("  Ctrl+C = 急停（不是退出）。REPL 里 5 秒内再按一次才退出，退出码 130。");
            sb.AppendLine("  软件 EmergencyStop() 只是把 0x6040 清零，不是安全回路，不替代硬接线急停 / STO / 限位。");
            sb.AppendLine("  不做隐式 connect；未连接发 enable 一律状态机拒绝（退出码 8）。");
            sb.AppendLine("  tune 观察：回车 = 停止观察回提示符（不急停），Ctrl+C 仍是急停；telemetry / watch 不吃回车，按 --seconds 或 Ctrl+C 收。");
            return sb.ToString();
        }

        public static string BuildCommandHelp(CommandSpec spec)
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("语法: " + spec.Syntax);
            if (spec.Aliases != null && spec.Aliases.Length > 0)
                sb.AppendLine("别名: " + string.Join(" / ", spec.Aliases));
            sb.AppendLine("说明: " + spec.Summary);
            sb.AppendLine("退出码: " + spec.ExitCodes);
            if (spec.Dangerous)
                sb.AppendLine("危险命令: 需要 --yes，REPL 里会要 [y/N] 确认。");
            if (spec.Name == "swingup")
                sb.AppendLine("单位: --pump-amp 用在 setswingup 里是 rad/s²（泵加速度幅），泵频 --pump-hz 是 Hz；本命令的 --timeout / --timeout-ms 单位是毫秒（本次起摆截止，写在命令名之后）。这个数同时是控制层 S12 联锁的截止与 CLI 的等待上限，两边永远同值，不会出现「引擎按 30 s 跳 Fault、CLI 还等 60 s」。写在命令名之前的 --timeout <秒> 只是单命令执行看门狗：更短时先中断等待并按 5 / FaultCode:SwingUpTimeout 收口，但**不改**控制层截止。");
            if (spec.Name == "setswingup")
            {
                sb.AppendLine("单位: pump-amp = 泵加速度幅 [rad/s²]；pump-hz = 泵频 [Hz]；theta-switch / phi-switch / center-band = rad；omega-switch / capture-arm-rate = rad/s；timeout-ms = ms（--timeout 是它的毫秒别名，不参与单命令执行超时）；extract-gain = 抽取增益 [rad/s² per J]（与注入增益 ke 解耦，缺键/0 按出厂 60 生效）。");
                sb.AppendLine("状态: 不带旗标读取当前值在任何状态都可用。改参数只在未连接 / 已使能 / 已停生效；已连接但未使能、平衡中、故障态一律拒绝并说明原因（先 enable / stop / fault-reset，或断开后离线配置）。起摆中只允许热改 --energy-extract / --energy-deadband / --extract-gain / --center-band / --capture-arm-rate；捕获交棒六项（--prepare-approach / --prepare-energy-margin / --commit-arm-rate / --commit-omega / --commit-hold-cycles / --hold-decay）在起摆中一律锁定，其余起摆字段同样锁定到起摆结束。");
                sb.AppendLine("约束: 快速提交臂速必须 ≤ capture_arm_rate_limit_rad_per_s，摆杆角速度上限必须 ≤ omega_switch_rad_per_s；参数层直接拒收越界组合，控制层运行期还会收紧一次。");
                sb.AppendLine("捕获交棒（单位 / 默认 / 范围；缺键或 0 = 按默认生效）：--prepare-approach 预捕获回中窗口 [rad]，默认 0.60，范围 (0, 1.5]；" +
                    "--prepare-energy-margin 预捕获能量带宽（相对 2·mgl，无量纲），默认 0.015，范围 (0, 0.1]；" +
                    "--commit-arm-rate 快速提交臂速软上限 [rad/s]，默认 3.0，范围 (0, 6.0] 且 ≤ capture_arm_rate_limit_rad_per_s（硬门 4.0）；" +
                    "--commit-omega 快速提交摆杆角速度上限 [rad/s]，默认 0.8，范围 (0, 10.0] 且 ≤ omega_switch_rad_per_s（运行期自动收紧）；" +
                    "--commit-hold-cycles 快速提交通道净拍数，默认 20 拍，范围 [1, 200]；" +
                    "--hold-decay 近顶端驻留计数的漏桶衰减量，单位 **千分之一拍/拍（0.001 拍/拍）**：默认 2 = 0.002 拍/拍，" +
                    "范围 [0, 10] 对应 0~0.010 拍/拍；0 = 未配置（按 2 生效，**不是不衰减**）。" +
                    "只有离直立 |ψ| ≤ 预捕获窗口时才计数，远离顶端冻结；该量必须远小于「单次掠顶窗口/两次掠顶间隔」≈0.0047 拍/拍，" +
                    "否则计数会在两次掠顶之间被扣光。");
                sb.AppendLine("默认: energy-extract / energy-deadband / extract-gain / center-band 填 0 = 未配置，按出厂 0.20 / 0.005 / 60 / 20 生效；用户显式写入的其它值（含 3.6）原样保留，不做运行时钳位。");
            }
            if (spec.Name == "setpid")
            {
                sb.AppendLine("语义: 平衡环已由整套状态反馈接管。当前真正生效的是 —— --acc-max / --arm-rate-max 关节加速度与参考角速度限幅（绝对设置），--kp-phi / --ki-phi / --kd-phi 内环 φ 跟踪环，以及 --td-lp-ms / --out-min / --out-max / --out-rate / --max-i / --deadband 这些滤波 / 限幅 / 死区项。只覆盖给出的旗标，其余字段保留现值，不会把没给的增益清零。耦合符号由台架实测固定（当前 −1），不是可调旗标，只在 status / dashboard 里以只读字段显示。");
                sb.AppendLine("历史（接受但无效）: --ka / --kad / --kai 是上一代加速度反馈律的外环增益，--kp / --ki / --kd 是更早的「摆臂位置映射律」增益 —— 当前控制律都不读取。保留这两组旗标只为旧参数文件和老脚本能原样往返（删了老脚本会报错），设置时不改变任何控制量，JSON 回执带 acceptedButInactive=true / historicalFlagsEffective=false。整定平衡行为请用 --acc-max / --arm-rate-max / --kp-phi / --ki-phi / --kd-phi；整定起摆请用 setswingup。");
                sb.AppendLine("相对: ka/kad/kai 与 kp/ki/kd、kp-phi/ki-phi/kd-phi 取值带符号 = 相对现值增减（--ka +50 / --kad -5），不带符号 = 绝对设置（--ka 500）；等号写法 --ka=500 同义。--acc-max / --arm-rate-max 与 td-lp-ms / out-min / out-max / out-rate / max-i / deadband 一律绝对设置。");
                sb.AppendLine("范围: 历史旗标只要求是有限数，不再做结构下界 / 阻尼比校验（写进去也不影响平衡）；--acc-max > 0 [rad/s²]；--arm-rate-max > 0 [rad/s]；内环三增益必须让离散闭环极点落在单位圆内，判据由 PidGains.IsValid 给出，不在 CLI 写死数字。");
                sb.AppendLine("单位: AccMax [rad/s²]、ArmRateMax [rad/s]；内环 Kpφ [rad/rad]、Kiφ [1/s]、Kdφ [s]；TdLp [ms]；out-min/out-max/max-i/deadband [rad]；out-rate [rad/s]。历史项 Ka [rad/s² per rad]、Kad [rad/s² per rad/s]、Kai [rad/s² per (rad·s)] 只作参数文件记录。");
                sb.AppendLine("回显: 不带参数 = 打印当前增益全表（标注哪些是历史值）；改完打印 old→new 对照并提示已生效（generation 双缓冲下个周期拍首顶换），历史旗标单独标注为「只写参数结构，控制输出不变」。");
            }
            if (spec.Name == "tune")
            {
                sb.AppendLine("列: " + CliSession.TuneLegend());
                sb.AppendLine("停止: 回车 = 停止观察回提示符（不急停）；Ctrl+C = 急停（安全裁定全程序不变）；--seconds 到点自动停。脚本 / stdin 管道没有回车可听，只靠 --seconds 与 Ctrl+C。");
                sb.AppendLine("范围: --hz 5..20（默认 10），--loop 只看一环可少刷一半列。只读命令，不改任何参数；改增益用 setpid。");
            }
            if (spec.Name == "zero")
            {
                sb.AppendLine("语义: zero pendulum 把摆杆计数置零 = 当前计数锁成零点（count_zero），只是重标定手段。按在位的计数切片分两种 —— GC1602（AB 正交）只有在输出方向映射到 RxPDO 时才发 0x7000:1 Reset Counter CH1 硬件复位；当前固件 0x1600:00 恒 abort 0x06020000，主站把输出方向按 0 配置（GcanOutputsMapped=false），实际按软件置零处理。GC1502（脉冲计数 4×UDINT）没有 RxPDO / 复位命令，只能软件置零，不发任何命令。使能时摆杆自然下垂即自动归零，zero pendulum 仅用于重标定（把当前点重新定为坐标 0），位置不对重标出来的 θ 全错。");
                sb.AppendLine("换算: θ = Δcount × 2π / CPR_PENDULUM，Δcount 走 32 位回绕差分（摆杆往复摆不满 2³¹）。zero arm = 摆臂重标定（把当前点设为坐标 0），起始位置恒为 0，平衡回中目标也恒为 0。只在 Connected（未使能）合法。");
            }
            if (spec.Name == "stop" || spec.Name == "fault-reset")
                sb.AppendLine("语义: 受控停走 QuickStop 路径——0x0002 按 0x6085 斜坡减速，确认 QuickStopActive 后 0x0000 去使能（离开该态只允许 0x0000 / 0x000F；等待有界 2s，超时/故障直接收尾）；清 EStopLatch 前只读复检 S11 硬接线急停，未松开保持 Fault 并拒绝。");
            return sb.ToString();
        }

        public static string BuildVersionText()
        {
            var asm = typeof(CliCommandParser).Assembly;
            Version v = asm.GetName().Version;
            string buildStamp;
            try
            {
                buildStamp = File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                buildStamp = "unknown";
            }
            var sb = new StringBuilder(256);
            sb.Append("FurutaPendulum CLI ").Append(v != null ? v.ToString() : "1.0.0");
            sb.Append(" | build ").Append(buildStamp);
            sb.Append(" | Darra.Core.dll=").Append(FileSha256("Darra.Core.dll"));
            sb.Append(" | DarraRT_Eth.sys=").Append(FileSha256("DarraRT_Eth.sys"));
            return sb.ToString();
        }

        /// <summary>只读显示绑定件 SHA256，不做预检（预检只在 connect 做）。找不到就如实打未找到。</summary>
        private static string FileSha256(string fileName)
        {
            try
            {
                string baseDir = Path.GetDirectoryName(typeof(CliCommandParser).Assembly.Location);
                string path = Path.Combine(baseDir ?? string.Empty, fileName);
                if (!File.Exists(path)) return "not-found";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                        sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "unavailable(" + ex.GetType().Name + ")";
            }
        }
    }
}
