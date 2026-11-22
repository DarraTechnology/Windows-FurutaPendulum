using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// CLI 无头模式入口。控制台挂接由 Program.cs 负责（EnsureCliConsole），此处不重复 AttachConsole。
    /// 只做：解析参数 → 调 PendulumEngine → 格式化输出 → 收尾退出码，不写控制算法、不写第二套安全判定。
    /// </summary>
    public static class CliRunner
    {
        /// <summary>CLI 入口。engine 可为 null（仅 --help / --version / log-path 可用）；不做隐式 connect。</summary>
        public static int Run(string[] args, FurutaPendulum.Control.PendulumEngine engine)
        {
            string[] cliArgs = StripModePrefix(args);

            ParseOutcome outcome;
            try
            {
                outcome = CliCommandParser.Parse(cliArgs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("! error  invalid_arguments  " + ex.GetType().Name + ": " + ex.Message);
                return ExitCodes.InvalidArguments;
            }

            CliOptions options = outcome.Options;

            // DC Sync0 开关：命令行旗标在解析层留底，这里落到总线会话（Open 时读取）。
            // 不给旗标时 BusSession 按环境变量 FURUTA_DC_SYNC / 默认关闭解析 —— 默认不武装 DC。
            BusSession.DcSyncOverride = CliCommandParser.DcSyncRequested;

            // --help / --version：只打文本就退，不要求 engine。
            if (options.Help)
            {
                Console.Out.WriteLine(CliCommandParser.BuildHelpText());
                return ExitCodes.Ok;
            }
            if (options.Version)
            {
                Console.Out.WriteLine(CliCommandParser.BuildVersionText());
                return ExitCodes.Ok;
            }

            if (!string.IsNullOrEmpty(outcome.Error))
            {
                WritePlainError("invalid_arguments", outcome.Error, "argument_validation");
                return outcome.ErrorCode;
            }

            // --json 的 stdout 必须是纯 JSON Lines。Darra.Core 的 native 层连接期会直接 printf 几行
            // （DarraRT: ...），那几行绕不开 Console.Out —— 在 SDK 首次加载之前把标准输出句柄改道 stderr，
            // 本程序自己的 JSON 写线程不受影响（它已经拿住真实 stdout）。
            bool nativeStdoutRouted = options.Json && CliConsoleSink.RouteNativeStdoutToStderr();

            DiagnosticLogWriter log = null;
            CliOutput output = null;
            CliSession session = null;
            IDisposable telemetrySampler = null;
            int exitCode = ExitCodes.Ok;

            try
            {
                log = new DiagnosticLogWriter(options.LogPath);
                output = new CliOutput(options, log);
                session = new CliSession(engine, output, options);

                if (options.Json)
                {
                    // 让售后 / 脚本一眼看出这份 stdout 是不是纯 JSON Lines，以及为什么。
                    output.WriteLogRecord("json_stream",
                        nativeStdoutRouted
                            ? "stdout 只写 JSON Lines：Darra.Core native 层直写 stdout 已改道 stderr（人仍可见）"
                            : "stdout 是 JSON Lines，但 native 层 stdout 改道未生效：SDK 若打印 DarraRT: 行会混入本流",
                        "startup", nativeStdoutRouted, 0, nativeStdoutRouted ? "ok" : "native_stdout_not_routed");
                }

                if (engine == null)
                {
                    // 入口在 DENI 之前短路传 null：只允许不碰引擎的命令。
                    foreach (ParsedCommand command in outcome.Commands)
                    {
                        if (command.Name == "log-path" || command.Name == "help" || command.Name == "version")
                            continue;
                        output.ErrorLine("internal_error",
                            "控制层未初始化，命令 " + command.Name + " 需要 PendulumEngine（入口未提供）",
                            "internal_error", ExitCodes.General);
                        exitCode = ExitCodes.General;
                        return exitCode;
                    }
                    if (outcome.Commands.Count == 0)
                    {
                        output.ErrorLine("internal_error", "控制层未初始化，无头会话不可用（只有 --help / --version 可用）",
                            "internal_error", ExitCodes.General);
                        exitCode = ExitCodes.General;
                    }
                    else
                    {
                        exitCode = session.RunCommands(outcome.Commands);
                    }
                    return exitCode;
                }

                // Ctrl+C = 急停，不是退出。在跑任何命令之前挂上。
                ConsoleCancelEventHandler cancelHandler = (sender, e) =>
                {
                    e.Cancel = true;
                    session.OnCancelKeyPress();
                };
                EventHandler processExitHandler = (sender, e) => session.OnProcessExit();
                Console.CancelKeyPress += cancelHandler;
                AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                try
                {
                    // 台架取证：环境变量给了 CSV 路径才启动只读采样线程（见 TelemetrySampler）。
                    telemetrySampler = TelemetrySampler.Start(engine, Environment.GetEnvironmentVariable(TelemetrySampler.EnvVarName));
                    session.Attach();
                    exitCode = RunSession(session, engine, output, options, outcome);
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                    AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                }
            }
            catch (Exception ex)
            {
                if (output != null)
                    output.Error("internal_error", ex.GetType().Name + ": " + ex.Message, "cli_host", Truncate(ex.StackTrace));
                else
                    Console.Error.WriteLine("! error  internal_error  " + ex.GetType().Name + ": " + ex.Message);
                exitCode = ExitCodes.General;
            }
            finally
            {
                if (telemetrySampler != null)
                {
                    telemetrySampler.Dispose();
                    telemetrySampler = null;
                }
                if (session != null)
                {
                    int teardownCode = session.Teardown(false);
                    if (exitCode == ExitCodes.Ok && teardownCode != ExitCodes.Ok) exitCode = teardownCode;
                    session.Dispose();
                }
                if (output != null)
                {
                    output.WriteResultFile(exitCode, options.ResultPath);
                    if (log != null)
                        output.WriteLogRecord("session_end", "exitCode=" + exitCode.ToString(CultureInfo.InvariantCulture),
                            null, exitCode == ExitCodes.Ok, exitCode, exitCode == ExitCodes.Ok ? "ok" : "error");
                }
                if (log != null) log.Dispose();
            }
            return exitCode;
        }

        /// <summary>去掉入口模式前缀（--cli），并兼容直接传 --help / --version 的短路调用。</summary>
        private static string[] StripModePrefix(string[] args)
        {
            if (args == null || args.Length == 0) return new string[0];
            int start = 0;
            if (string.Equals(args[0], "--cli", StringComparison.Ordinal) ||
                string.Equals(args[0], "--gui", StringComparison.Ordinal))
                start = 1;
            var list = new List<string>(args.Length - start);
            for (int i = start; i < args.Length; i++) list.Add(args[i]);
            return list.ToArray();
        }

        private static int RunSession(CliSession session, FurutaPendulum.Control.PendulumEngine engine,
            CliOutput output, CliOptions options, ParseOutcome outcome)
        {
            // 启动自动载入默认参数文件：pid / swingup / limits 三段与 GUI 的 AutoLoadParams 同口径，
            // **不再由 --config 决定载不载**。默认文件先灌入引擎，--config 只作覆盖。
            // 这一步同时是第一道防线：引擎里必须是用户参数，而不是出厂值（否则一次 zero arm 的
            // 自动落盘会把整份文件改成出厂值 —— 2026-11-20 台架事故根因）。
            ApplyStartupDefaultParams(session, engine, output);

            // 载入结局先落日志：整份拒载时用户改过的参数会悄悄回落控制层默认值，必须能看见。
            // 放在 --config 之前，这样即使 --config 随后失败，这次自动读取的记录也不会丢。
            ReportStartupParamFile(output);

            // 启动配置：--config 指定的参数文件覆盖默认文件的 pid / swingup / limits。
            // 默认文件不存在 / 整份拒载时引擎保持控制层出厂值，这里不再做任何「旧出厂值迁移」：
            // 旧版 3.6 起摆块的识别与替换唯一在 ParamStore.TryLoad（PendulumEngine.IsLegacyFactorySwingDefaults
            // 整块指纹 → 按当前出厂块载入，并把原因写进 note）。CLI 侧再补一套只会制造第二个真相源。
            if (!string.IsNullOrWhiteSpace(options.ConfigPath))
            {
                CommandResult loaded = ApplyStartupConfig(session, engine, output, options.ConfigPath);
                if (!loaded.Ok) return loaded.Code;
            }

            // DC 策略留一条启动记录：连接日志里还会再报一次实际生效来源，两边同值。
            string dcSource;
            bool dcEnabled = BusSession.ResolveDcSyncEnabled(out dcSource);
            output.WriteLogRecord("dc_sync",
                "DC Sync0 " + (dcEnabled ? "启用" : "关闭") + "（来源：" + dcSource + "）" +
                (dcEnabled
                    ? "；连接时按 HasDC / ConfigureDC / Sync0Armed 硬门武装，未获确认就拒绝升态。"
                    : "；按 FreeRun 运行，连接时显式 DisableDC 防止自动策略重新武装。默认关闭是现场裁定：DENI 关 DC 后总线仍可进 OP，且使能更快。"),
                "startup", true, 0, "ok");

            // 机器标定：摆杆每转计数。它是标定量不是控制参数，不进 furuta_params.json。
            // 写入必须读回验证：控制层只在 Disconnected/Connected/Stopped 接受改写。
            if (options.PendulumCpr.HasValue && !session.ApplyPendulumCpr(options.PendulumCpr.Value, "startup"))
            {
                output.ErrorLine("state_rejected",
                    "摆杆每转计数写入被拒（当前 " + engine.State + "，控制层只在 Disconnected/Connected/Stopped 允许），值未变。",
                    "state_rejected", ExitCodes.StateRejected);
                return ExitCodes.StateRejected;
            }

            // 机器标定：摆臂每转计数 CPR_ARM（= 电机每转计数 × 减速比）。与摆杆同口径必须读回验证：
            // 控制层只在 Disconnected/Connected/Stopped 接受改写，写不进就如实报 state_rejected。
            if (options.ArmCpr.HasValue && !session.ApplyArmCpr(options.ArmCpr.Value, "startup"))
            {
                output.ErrorLine("state_rejected",
                    "摆臂每转计数写入被拒（当前 " + engine.State + "，控制层只在 Disconnected/Connected/Stopped 允许），值未变。",
                    "state_rejected", ExitCodes.StateRejected);
                return ExitCodes.StateRejected;
            }

            // 摆臂减速比只在构造控制层时生效（PendulumEngineOptions.ArmGearRatio，运行期没有改写口），
            // 这里只如实记一笔生效值，不做写入 —— 没有可写回验证的口，谎报"已写入"是不允许的。
            output.WriteLogRecord("arm_gear",
                "ArmGearRatio = " + CliSession.ArmGearText(session.ArmGearRatioValue()) +
                "（构造期生效，运行期不可改；不给 --arm-cpr 时 CPR_ARM = 0x2102 实读 × 此减速比）",
                "startup", true, 0, "ok");

            // 进 OP 方式同样只在构造控制层时生效，这里如实记一笔实际取值（不给旗标 = 默认 standard）。
            if (options.OpEntry.HasValue)
            {
                output.WriteLogRecord("op_entry",
                    "OpEntry = " + options.OpEntry.Value +
                    (options.OpEntry.Value == OpEntryMode.DirectFromPreOp
                        ? "（PreOp → OP 直连，不经显式 SafeOp；仅诊断用，非标准路径）"
                        : "（PreOp → SafeOp → OP 标准顺序）") +
                    "，构造期生效",
                    "startup", true, 0, "ok");
            }

            // 摆臂从站：只在 Connect 期生效，因此只在 Disconnected 可写。命令行口径是 1 基序号
            // （与 status 显示的 "Slave n" 一致），这里减 1 转 0 基下标再写。写入必须读回验证 ——
            // 写不进就如实报状态拒绝，不许宣称"已按指定从站连"。不给就保持 -1，交给控制层自动识别。
            if (options.ArmSlaveNumber.HasValue)
            {
                int armSlaveIndex = options.ArmSlaveNumber.Value - 1;
                if (!session.ApplyArmSlaveIndex(armSlaveIndex, "startup"))
                {
                    output.ErrorLine("state_rejected",
                        "摆臂从站下标写入被拒（当前 " + engine.State + "，只在 Disconnected 可改，它只在 Connect 期生效），值未变。",
                        "state_rejected", ExitCodes.StateRejected);
                    return ExitCodes.StateRejected;
                }
            }

            bool interactiveStdin = !Console.IsInputRedirected;

            // 一次性 / 序列
            if (outcome.Commands.Count > 0)
            {
                if (!interactiveStdin) session.StartMotionWatchdog(CliSession.DefaultMotionCapSeconds);
                int code = session.RunCommands(outcome.Commands);
                return FinishCode(session, code);
            }

            // 脚本文件
            if (!string.IsNullOrEmpty(options.ScriptPath))
            {
                List<string> lines = ReadLines(options.ScriptPath, output);
                if (lines == null) return ExitCodes.ConfigError;
                session.StartMotionWatchdog(CliSession.DefaultMotionCapSeconds);
                int code = session.RunScript(lines);
                return FinishCode(session, code);
            }

            // stdin 管道
            if (!interactiveStdin)
            {
                session.StartMotionWatchdog(CliSession.DefaultMotionCapSeconds);
                int code = session.RunScript(ReadStdinLines());
                return FinishCode(session, code);
            }

            // REPL
            int replCode = session.RunRepl();
            return FinishCode(session, replCode);
        }

        private static int FinishCode(CliSession session, int code)
        {
            if (session.CancelRequested) return ExitCodes.Cancelled;
            if (session.MotionCapHit && code == ExitCodes.Ok) return ExitCodes.Ok;
            if (code != ExitCodes.Ok) return code;
            return session.SessionExitCode;
        }

        /// <summary>
        /// 把「本次启动读默认参数文件」的结局写进会话日志（成功也留一条，便于与状态行对照）。
        /// 整份拒载 / 部分段回退用 Warn/Error 级别并往 stderr 打一行；只落日志与状态行，不弹窗
        /// （项目铁律：不做「操作失败」这类弹窗噪声）。没读过（例如显式给了 --arm-gear）就不写。
        /// </summary>
        private static void ReportStartupParamFile(CliOutput output)
        {
            ParamStore.ParamFileLoadReport report = ParamStore.LastDefaultFileLoad;
            if (report == null)
                return;

            string message;
            bool trouble;
            string errorCode;
            switch (report.Outcome)
            {
                case ParamStore.ParamFileLoadOutcome.Loaded:
                    trouble = !string.IsNullOrEmpty(report.Note);
                    // 默认文件现在与 GUI 同口径，pid / swingup / limits 三段都灌进引擎；
                    // --config 只是随后覆盖。别再写「只恢复摆臂标定」的旧口径。
                    message = "默认参数文件已读取并载入 pid / swingup / limits（--config 只作覆盖）：" +
                              report.Path + (trouble ? "；部分段已回退：" + report.Note : string.Empty);
                    errorCode = trouble ? "param_file_partial_fallback" : "ok";
                    break;
                case ParamStore.ParamFileLoadOutcome.Missing:
                    // 首次运行没有参数文件是正常态：留一条记录，但不按故障级别刷屏。
                    trouble = false;
                    message = "默认参数文件不存在（首次运行），按控制层默认值启动：" + report.Path +
                              "（save-config 与 zero 自动落盘不会覆盖已有文件；需要落盘时先 load-config）";
                    errorCode = "param_file_missing";
                    break;
                default:
                    trouble = true;
                    message = "默认参数文件整份拒载，已回落控制层默认值；为防把出厂值写回，save-config 与 zero 自动落盘会拒绝覆盖已有文件：" +
                              report.Error;
                    errorCode = "param_file_rejected";
                    break;
            }

            output.WriteLogRecord("param_load", message, "startup", !trouble, 0, errorCode);
            if (trouble)
                output.Warn("! warning  参数文件：" + message);
        }

        /// <summary>
        /// 启动时自动载入默认参数文件（<see cref="ParamStore.DefaultPath"/>）的 pid / swingup / limits
        /// 三段，与 GUI <c>MainForm.AutoLoadParams</c> 同口径；<c>--config</c> 随后只作覆盖。
        /// <para>
        /// 这是参数丢失事故的第一道防线：CLI 以前只有带 <c>--config</c> 才灌主参数，不带时引擎是
        /// 出厂值（radius_ratio=0 / 软限位 int32 极值 / Ke=60），一次 <c>zero arm</c> 的自动落盘
        /// 就会把整份参数文件覆盖成出厂值。现在默认文件先载入，引擎里的值就是用户参数。
        /// </para>
        /// <para>
        /// 文件不存在 / 整份拒载 / 字段不合法：不阻断 CLI 启动（status 等命令仍可用），
        /// 保持出厂值并返回 false；此时 <see cref="CliSession.ParamsLoadedFromFile"/> 保持 false，
        /// save-config 与 zero 自动落盘会拒绝覆盖已有文件。可选段（标定 / 泵 / 辨识 / 捕获）不在这里
        /// 应用，仍由各自入口读取。
        /// </para>
        /// </summary>
        private static bool ApplyStartupDefaultParams(CliSession session,
            FurutaPendulum.Control.PendulumEngine engine, CliOutput output)
        {
            string path = ParamStore.DefaultPath;
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            PersistedExtras extras;
            string deniPath;
            string deniNote;
            string error;
            bool ok = ParamStore.TryLoad(path, engine.DeniPath, out pid, out swing, out limits, out extras,
                out deniPath, out deniNote, out error);
            ParamStore.ReportDefaultFileLoad(path, ok, ok ? null : error, ok ? deniNote : null);
            if (!ok)
            {
                output.WriteLogRecord("param_load",
                    "默认参数文件未载入，引擎保持控制层出厂值：" + error +
                    "（save-config 与 zero 自动落盘会拒绝覆盖已有文件，先 load-config 再保存）",
                    "startup", false, 0, "param_file_rejected");
                return false;
            }

            string reason;
            if (!pid.IsValid(out reason) || !swing.IsValid(out reason) || !limits.IsValid(out reason))
            {
                output.WriteLogRecord("param_load",
                    "默认参数文件的 pid/swingup/limits 不合法，未灌入引擎（保持出厂值；save-config 与 zero 自动落盘会拒绝覆盖已有文件）：" + reason,
                    "startup", false, 0, "param_file_rejected");
                output.Warn("! warning  默认参数文件字段不合法，未载入：" + reason + "（文件：" + path + "）");
                return false;
            }

            // 与 GUI AutoLoadParams 同一顺序：旧版位置映射律增益按唯一规则补齐；旧版出厂起摆块迁移。
            bool legacyPidGains = !pid.HasAccelerationLawGains();
            if (pid.ResolveAccelerationLaw() && legacyPidGains)
                output.Warn("! warning  外环增益是旧版位置映射律参数（不可换算），已用默认加速度律增益；请重新整定。");
            string swingMigrationNote;
            if (PendulumEngine.TryMigrateLegacySwingDefaults(ref swing, out swingMigrationNote))
                output.Warn("! warning  " + swingMigrationNote);

            engine.SetBalancePid(pid);
            engine.SetSwingUpParams(swing);
            engine.SetSafetyLimits(limits);
            session.MarkParamsLoadedFromFile();

            output.WriteLogRecord("param_load",
                "默认参数文件已载入 pid / swingup / limits：" + path +
                "  Ke=" + swing.Ke.ToString("G6", CultureInfo.InvariantCulture) +
                "  radius_ratio=" + limits.RadiusRatio.ToString("G6", CultureInfo.InvariantCulture) +
                "  软限位=[" + limits.ArmMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                limits.ArmMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts" +
                (string.IsNullOrEmpty(deniNote) ? string.Empty : "；" + deniNote),
                "startup", true, 0, "ok");
            if (!string.IsNullOrEmpty(deniNote))
                output.Warn("! warning  参数文件：" + deniNote);
            return true;
        }

        private static CommandResult ApplyStartupConfig(CliSession session,
            FurutaPendulum.Control.PendulumEngine engine, CliOutput output, string path)
        {
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            string deniPath;
            string deniNote;
            string error;
            // 必须用带 deniNote 的重载：5 参重载把段级回退全部丢掉（旧版出厂起摆块被拒、标定段回退、
            // DENI 溯源不一致）。--config 指到旧块文件时那样看起来「载入成功」，实际起摆参数已被换成
            // 出厂值 —— 静默失败，用户在任何出口都查不到原因。
            if (!ParamStore.TryLoad(path, engine.DeniPath, out pid, out swing, out limits,
                    out deniPath, out deniNote, out error))
            {
                output.ErrorLine("config_invalid", "--config 读取失败：" + error, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }
            string reason;
            if (!pid.IsValid(out reason) || !swing.IsValid(out reason) || !limits.IsValid(out reason))
            {
                output.ErrorLine("config_invalid", "--config 字段不合法，整体未生效：" + reason, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }
            engine.SetBalancePid(pid);
            engine.SetSwingUpParams(swing);
            engine.SetSafetyLimits(limits);
            // --config 只是覆盖默认文件；本进程从此算「已成功载入过参数文件」，
            // save-config / zero 自动落盘可以安全覆盖同一文件。
            session.MarkParamsLoadedFromFile();

            // 段级回退必须让用户看见：一条 config_loaded 日志记录（--log-path 可查，含拒绝原因）+
            // stderr 一行警告。口径与默认参数文件的 ReportStartupParamFile 一致：
            // ok=false + param_file_partial_fallback，不冒充「全部生效」。
            // 摘要只列真正生效的项：历史增益（Ka/Kad/Kai、Kp/Ki/Kd）不写进摘要，避免看起来已生效。
            bool partialFallback = !string.IsNullOrEmpty(deniNote);
            output.WriteLogRecord("config_loaded", "path=" + path +
                "  accMax=" + pid.AccMax.ToString(CultureInfo.InvariantCulture) +
                "  armRateMax=" + pid.ArmRateMax.ToString(CultureInfo.InvariantCulture) +
                "  kpPhi=" + pid.KpPhi.ToString(CultureInfo.InvariantCulture) +
                "  kiPhi=" + pid.KiPhi.ToString(CultureInfo.InvariantCulture) +
                "  kdPhi=" + pid.KdPhi.ToString(CultureInfo.InvariantCulture) +
                "  tdLpMs=" + pid.TdLpMs.ToString(CultureInfo.InvariantCulture) +
                (partialFallback ? "；部分段已拒绝/回退：" + deniNote : string.Empty),
                null, !partialFallback, 0, partialFallback ? "param_file_partial_fallback" : "ok");
            if (partialFallback)
                output.Warn("! warning  --config 参数文件（" + path + "）：" + deniNote);
            return CommandResult.Success();
        }

        private static List<string> ReadLines(string path, CliOutput output)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!File.Exists(full))
                {
                    output.ErrorLine("config_invalid", "脚本文件不存在：" + full, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                    return null;
                }
                return new List<string>(File.ReadAllLines(full));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                output.ErrorLine("config_invalid", "脚本文件读失败：" + ex.GetType().Name + ": " + ex.Message,
                    "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return null;
            }
        }

        private static IEnumerable<string> ReadStdinLines()
        {
            string line;
            while ((line = Console.ReadLine()) != null) yield return line;
        }

        private static void WritePlainError(string code, string message, string stage)
        {
            Console.Error.WriteLine("{\"ts\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) +
                "\",\"kind\":\"error\",\"msg\":" + JsonEnvelope.Quote(message) +
                ",\"status\":\"error\",\"code\":" + JsonEnvelope.Quote(code) +
                ",\"message\":" + JsonEnvelope.Quote(message) +
                ",\"stage\":" + JsonEnvelope.Quote(stage) + ",\"stack\":\"\"}");
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 500 ? text : text.Substring(0, 500);
        }
    }
}
