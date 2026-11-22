using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>命令执行结果。</summary>
    internal struct CommandResult
    {
        public int Code;
        public bool Ok;
        public string ErrorCode;
        /// <summary>单命令执行超时（--timeout 到点）收口。回执由 Execute 统一打，命令本体不再补一行。</summary>
        public bool TimedOut;

        public static CommandResult Success()
        {
            return new CommandResult { Code = ExitCodes.Ok, Ok = true, ErrorCode = "ok" };
        }

        public static CommandResult Fail(int code, string errorCode)
        {
            return new CommandResult { Code = code, Ok = false, ErrorCode = errorCode };
        }

        /// <summary>超时收口（不急停）：只把等待中断掉并如实失败，最终码由 Execute 按命令既有超时码归口。</summary>
        public static CommandResult Timeout()
        {
            return Timeout(ExitCodes.General, "timeout");
        }

        /// <summary>超时收口，带最终失败码（不造新码，取该命令既有的超时码）。</summary>
        public static CommandResult Timeout(int code, string errorCode)
        {
            return new CommandResult { Code = code, Ok = false, ErrorCode = errorCode, TimedOut = true };
        }
    }

    /// <summary>命令 × 状态机合法性判定结果（设计稿 12）。</summary>
    internal enum StateGate
    {
        Allow,
        Idempotent,
        Reject
    }

    /// <summary>
    /// CLI 会话：命令实现 + REPL / 序列 / 脚本三种编排 + Ctrl+C 急停 + 收尾。
    /// 只调 PendulumEngine 公开方法，不写一行控制算法、不写第二套安全判定。
    /// 超时三层各管一层、互不替代：--timeout = 单命令执行超时（到点中断等待、失败收口，不急停）；
    /// 60 秒 REPL 命令看门狗 = 无人值守兜底（到点 Stop + 急停）；300 秒运动看门狗 = 无头连续运动上限（到点自动 Stop，算正常完成）。
    /// </summary>
    internal sealed partial class CliSession : IDisposable
    {
        /// <summary>命令看门狗：REPL 无输入上限（ms），只管「人走开了」，不管单条命令跑多久。</summary>
        private const int WATCHDOG_IDLE_MS = 60000;
        /// <summary>无头模式连续运动态上限（秒），到点自动停机，算正常完成。</summary>
        public const int DefaultMotionCapSeconds = 300;
        private const int DOUBLE_CTRL_C_WINDOW_MS = 5000;

        private readonly PendulumEngine _engine;
        private readonly CliOutput _output;
        private readonly CliOptions _options;

        /// <summary>
        /// 本进程是否**成功载入过**参数文件（默认文件或 --config / load-config）。
        /// false 时引擎里是出厂值：save-config 与 zero 的自动落盘都不得覆盖已有文件，
        /// 否则一次操作就会把用户的整份参数改成出厂值（2026-11-20 台架事故根因）。
        /// </summary>
        private bool _paramsLoadedFromFile;

        /// <summary>标记本进程已成功载入过参数文件（只由载入成功路径调用）。</summary>
        public void MarkParamsLoadedFromFile()
        {
            _paramsLoadedFromFile = true;
        }

        /// <summary>本进程是否成功载入过参数文件（供保存前的安全门判断）。</summary>
        public bool ParamsLoadedFromFile
        {
            get { return _paramsLoadedFromFile; }
        }

        private string _lastDiagnostic;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly object _stateGateLock = new object();
        private EngineState _lastState = EngineState.Disconnected;
        private long _motionStartTicks = -1;
        private int _watchdogSuppressed;
        private volatile bool _motionCapHit;
        /// <summary>单命令执行截止（_clock 毫秒）。long.MaxValue = 这条命令没给 --timeout，不套总闸。</summary>
        private long _commandDeadlineTicks = long.MaxValue;

        private volatile bool _cancelRequested;
        private volatile bool _exitRequested;
        private long _cancelTicks;
        private int _pendingFaultExit;
        private int _sessionExit;
        private bool _interactive;

        // tune 的"回车=停止观察"：REPL 输入队列 / 一次性会话的控制台读行线程，二选一。
        private BlockingCollection<string> _pendingInput;
        private string _pushbackLine;
        private string _tuneStopLine;
        private int _tuneStopFired;

        public CliSession(PendulumEngine engine, CliOutput output, CliOptions options)
        {
            _engine = engine;
            _output = output;
            _options = options;
        }

        public bool Interactive
        {
            get { return _interactive; }
            set { _interactive = value; }
        }

        /// <summary>会话最终退出码（REPL 用：取还没清掉的最高严重度）。</summary>
        public int SessionExitCode
        {
            get { return _sessionExit != 0 ? _sessionExit : (_pendingFaultExit != 0 ? _pendingFaultExit : 0); }
        }

        // ---------- 事件订阅 ----------

        public void Attach()
        {
            if (_engine == null) return;
            _engine.StateChanged += OnStateChanged;
            _engine.Faulted += OnFaulted;
            _engine.Diagnostic += OnDiagnostic;
            lock (_stateGateLock) { _lastState = _engine.State; }
        }

        public void Detach()
        {
            if (_engine == null) return;
            _engine.StateChanged -= OnStateChanged;
            _engine.Faulted -= OnFaulted;
            _engine.Diagnostic -= OnDiagnostic;
        }

        private void OnStateChanged(object sender, EngineState state)
        {
            string from;
            lock (_stateGateLock)
            {
                from = _lastState.ToString();
                bool wasMotion = IsMotion(_lastState);
                bool nowMotion = IsMotion(state);
                if (!wasMotion && nowMotion) Volatile.Write(ref _motionStartTicks, _clock.ElapsedMilliseconds);
                else if (wasMotion && !nowMotion) Volatile.Write(ref _motionStartTicks, -1);
                _lastState = state;
            }
            _output.Event("state_changed", "state changed", e => e.Str("from", from).Str("to", state.ToString()));
            _output.WriteLogRecord("state_changed", from + " -> " + state.ToString(), null, true, 0, "ok");
        }

        private static bool IsMotion(EngineState state)
        {
            return state == EngineState.Enabled || state == EngineState.Swinging || state == EngineState.Balancing;
        }

        private void OnFaulted(object sender, FaultInfo info)
        {
            int code = ExitCodes.ForFault(info.Code);
            _output.Event("faulted", "faulted", e =>
            {
                e.Str("faultCode", info.Code.ToString());
                e.Str("faultMsg", info.Detail ?? string.Empty);
                e.Str("fromState", info.FromState.ToString());
                e.Bool("hasTelemetry", info.HasTelemetry);
                if (info.HasTelemetry)
                {
                    e.Num("thetaRad", info.ThetaRad);
                    e.Num("omegaRadPerSec", info.OmegaRadPerSec);
                    e.Num("phiRad", info.PhiRad);
                    e.Int("armPositionCount", info.ArmPositionCount);
                    e.Int("statusWord", info.StatusWord);
                    e.Int("errorCode", info.ErrorCode);
                    e.Int("actualWkc", info.ActualWkc);
                    e.Int("expectedWkc", info.ExpectedWkc);
                }
                else
                {
                    // 本次会话尚无快照：θ / ω / 状态字 / WKC 全部未知，写 n/a，不拿 0 冒充。
                    e.Str("telemetryNote", "本次会话尚无遥测快照，θ / ω / φ / 0x6041 / 0x603F / WKC 未知");
                }
                e.Bool("estopLatch", true);
            });
            _output.WriteLogRecord("faulted", info.Code + ": " + (info.Detail ?? string.Empty), null, false, code, "FaultCode:" + info.Code);
            if (code != ExitCodes.Ok)
                _pendingFaultExit = code;
        }

        private void OnDiagnostic(object sender, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _lastDiagnostic = line;
            _output.LogForward("diagnostic", line, null);
        }

        /// <summary>
        /// 把控制层排队的状态迁移 / 故障 / 诊断取出来 raise（01 §3.4：事件不在周期线程同步抛，由前端主循环泵）。
        /// CLI 主循环每个节拍都要调一次，否则 StateChanged / Faulted 永远不触发。
        /// </summary>
        private void Pump()
        {
            try
            {
                if (_engine != null) _engine.PumpEvents();
            }
            catch (Exception ex)
            {
                _output.WriteLogRecord("pump_failed", ex.GetType().Name + ": " + ex.Message + " " + Truncate(ex.StackTrace),
                    null, false, ExitCodes.General, "internal_error");
            }
        }

        /// <summary>
        /// 读一次稳定遥测快照。控制层在没有稳定快照时给 default（Sequence==0），
        /// 这里统一按「还没有数据」处理，绝不把全零当成真测量值。
        /// <para>
        /// 也要挡掉**上一次连接**残留的快照：时间戳早于本次连接起点（引擎用 PositiveInfinity 表示），
        /// 否则 status / normalize 会把旧会话的 θ / EngineState 当成当前值（现场“停更 10723 s”的解来源）。
        /// </para>
        /// </summary>
        private bool TrySnapshot(out PendulumTelemetry snapshot)
        {
            snapshot = new PendulumTelemetry();
            if (_engine == null) return false;
            PendulumTelemetry read;
            if (!_engine.TryReadTelemetry(out read)) return false;
            if (read.Sequence == 0) return false;
            if (double.IsPositiveInfinity(_engine.TelemetryAgeSeconds)) return false;
            snapshot = read;
            return true;
        }

        /// <summary>
        /// 写摆杆每转计数并**读回验证**。控制层只在 Disconnected/Connected/Stopped 接受改写，
        /// 其它状态拒绝并保持原值 —— 不读回就宣称标定成功等于谎报，这里必须如实。
        /// </summary>
        public bool ApplyPendulumCpr(double cpr, string commandName)
        {
            if (_engine == null) return true;
            double before = _engine.PendulumCountsPerRev;
            if (before == cpr) return true;

            _engine.PendulumCountsPerRev = cpr;
            double after = _engine.PendulumCountsPerRev;
            if (after != cpr)
            {
                _output.WriteLogRecord("calibration_rejected",
                    "PendulumCountsPerRev 写入被拒：状态 " + _engine.State + "，期望 " +
                    cpr.ToString("0.###", CultureInfo.InvariantCulture) + "，实际保持 " +
                    after.ToString("0.###", CultureInfo.InvariantCulture),
                    commandName, false, ExitCodes.StateRejected, "state_rejected");
                return false;
            }

            _output.WriteLogRecord("calibration",
                "PendulumCountsPerRev " + before.ToString("0.###", CultureInfo.InvariantCulture) +
                " → " + after.ToString("0.###", CultureInfo.InvariantCulture),
                commandName, true, 0, "ok");
            return true;
        }

        /// <summary>
        /// 写摆臂步进从站下标并**读回验证**。它只在 Connect 期生效，控制层只在 Disconnected 接受改写，
        /// 其余状态拒写并保持原值 —— 不读回就宣称"已按指定从站连"等于谎报，这里必须如实。
        /// 入参是 <b>0 基</b>下标（命令行 --arm-slave 给的是 1 基序号，由调用方减 1），-1 = 交回自动识别。
        /// </summary>
        public bool ApplyArmSlaveIndex(int zeroBasedIndex, string commandName)
        {
            if (_engine == null) return true;
            int before = _engine.ArmSlaveIndex;
            if (before == zeroBasedIndex) return true;

            _engine.ArmSlaveIndex = zeroBasedIndex;
            int after = _engine.ArmSlaveIndex;
            if (after != zeroBasedIndex)
            {
                _output.WriteLogRecord("arm_slave_rejected",
                    "ArmSlaveIndex 写入被拒：状态 " + _engine.State + "，期望 " +
                    zeroBasedIndex.ToString(CultureInfo.InvariantCulture) + "，实际保持 " +
                    after.ToString(CultureInfo.InvariantCulture),
                    commandName, false, ExitCodes.StateRejected, "state_rejected");
                return false;
            }

            _output.WriteLogRecord("arm_slave",
                "ArmSlaveIndex " + before.ToString(CultureInfo.InvariantCulture) +
                " → " + after.ToString(CultureInfo.InvariantCulture) + "（0 基下标；-1 = 自动识别）",
                commandName, true, 0, "ok");
            return true;
        }

        /// <summary>
        /// 写摆臂每转计数 CPR_ARM（= 电机每转计数 × 减速比）并**读回验证**。与 <see cref="ApplyPendulumCpr"/> 同口径：
        /// 控制层只在 Disconnected/Connected/Stopped 接受改写，其它状态拒绝并保持原值 ——
        /// 不读回就宣称标定成功等于谎报，这里必须如实。
        /// </summary>
        public bool ApplyArmCpr(int cpr, string commandName)
        {
            if (_engine == null) return true;
            int before = _engine.ArmCountsPerRev;
            if (before == cpr) return true;

            _engine.ArmCountsPerRev = cpr;
            int after = _engine.ArmCountsPerRev;
            if (after != cpr)
            {
                _output.WriteLogRecord("arm_cpr_rejected",
                    "ArmCountsPerRev 写入被拒：状态 " + _engine.State + "，期望 " +
                    cpr.ToString(CultureInfo.InvariantCulture) + "，实际保持 " +
                    after.ToString(CultureInfo.InvariantCulture),
                    commandName, false, ExitCodes.StateRejected, "state_rejected");
                return false;
            }

            _output.WriteLogRecord("arm_cpr",
                "ArmCountsPerRev " + before.ToString(CultureInfo.InvariantCulture) +
                " → " + after.ToString(CultureInfo.InvariantCulture) + "（电机每转计数 × 减速比）",
                commandName, true, 0, "ok");
            return true;
        }

        // ---------- Ctrl+C / 进程退出 ----------

        /// <summary>Ctrl+C：第一次急停；REPL 里 5 秒内第二次才要求退出。handler 不做重活。</summary>
        public void OnCancelKeyPress()
        {
            long now = _clock.ElapsedMilliseconds;
            long previous = Interlocked.Read(ref _cancelTicks);
            Volatile.Write(ref _cancelTicks, now);
            _cancelRequested = true;

            // dashboard 是只读显示：Ctrl+C 只退出面板，不触发急停、不改变控制状态（用户 2026-11-20 要求）。
            // 非交互（一次性 / 脚本）也按 Ctrl+C 的通用语义退出进程。
            if (Volatile.Read(ref _dashboardActive) != 0)
            {
                if (!_interactive) _exitRequested = true;
                return;
            }

            if (_interactive && previous > 0 && now - previous <= DOUBLE_CTRL_C_WINDOW_MS)
            {
                _exitRequested = true;
                return;
            }

            try
            {
                if (_engine != null) _engine.EmergencyStop();
                _output.Event("estop", "estop", e => e.Str("reason", "ctrl+c").Bool("estopLatch", true));
            }
            catch (Exception ex)
            {
                _output.WriteLogRecord("estop_failed", ex.GetType().Name + ": " + ex.Message, null, false, ExitCodes.DeviceFault, "internal_error");
            }

            if (!_interactive) _exitRequested = true;
        }

        /// <summary>控制台被关 / 进程退出：2 秒预算尽力急停。</summary>
        public void OnProcessExit()
        {
            try
            {
                if (_engine == null) return;
                var done = new ManualResetEventSlim(false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _engine.EmergencyStop(); }
                    catch (Exception ex) { _output.WriteLogRecord("estop_failed", ex.GetType().Name + ": " + ex.Message, null, false, ExitCodes.DeviceFault, "internal_error"); }
                    finally { done.Set(); }
                });
                if (!done.Wait(2000))
                    _output.WriteLogRecord("estop_timeout", "退出急停超出 2 秒预算", null, false, ExitCodes.DeviceFault, "timeout");
            }
            catch (Exception ex)
            {
                _output.WriteLogRecord("estop_failed", ex.GetType().Name + ": " + ex.Message, null, false, ExitCodes.DeviceFault, "internal_error");
            }
        }

        public bool ExitRequested { get { return _exitRequested; } }
        public bool CancelRequested { get { return _cancelRequested; } }

        // ---------- 状态机合法性（设计稿 12） ----------

        public static StateGate Gate(string command, EngineState state)
        {
            switch (command)
            {
                case "status":
                case "telemetry":
                case "watch":
                case "dashboard":
                case "help":
                case "version":
                case "log-path":
                case "setpid":
                case "setlimits":
                case "save-config":
                case "estop":
                case "quit":
                    return StateGate.Allow;

                case "tune":
                    // 整定观察只读，但没连总线就没有遥测可看：照门表惯例给 8（状态机拒绝）。
                    return state == EngineState.Disconnected ? StateGate.Reject : StateGate.Allow;

                case "connect":
                    return state == EngineState.Disconnected ? StateGate.Allow : StateGate.Idempotent;

                case "disconnect":
                    return state == EngineState.Disconnected ? StateGate.Idempotent : StateGate.Allow;

                case "enable":
                    if (state == EngineState.Enabled) return StateGate.Idempotent;
                    return (state == EngineState.Connected || state == EngineState.Stopped) ? StateGate.Allow : StateGate.Reject;

                case "disable":
                    return (state == EngineState.Connected || state == EngineState.Enabled || state == EngineState.Stopped)
                        ? StateGate.Allow : StateGate.Reject;

                case "swingup":
                    if (state == EngineState.Swinging) return StateGate.Idempotent;
                    return (state == EngineState.Enabled || state == EngineState.Stopped) ? StateGate.Allow : StateGate.Reject;

                case "balance":
                    // R11 捕获优先：Enabled 态允许手动捕获平衡请求（引擎 Balance() 只登记请求，
                    // 周期线程用与起摆交棒同一套捕获门连续满足后才 EnterBalancing）。
                    // Swinging/Balancing 的原有路径不变；Stopped/Fault 仍拒绝。
                    return (state == EngineState.Enabled ||
                            state == EngineState.Swinging ||
                            state == EngineState.Balancing) ? StateGate.Allow : StateGate.Reject;

                case "stop":
                    return state == EngineState.Stopped ? StateGate.Idempotent :
                        (state == EngineState.Disconnected ? StateGate.Reject : StateGate.Allow);

                case "fault-reset":
                    return (state == EngineState.Stopped || state == EngineState.Fault) ? StateGate.Allow : StateGate.Reject;

                case "setswingup":
                    // 读（不带旗标）任何状态都允许；写参数的状态门在 CmdSetSwingUp 里按旗标逐条判：
                    // 起摆中只放行热改项，平衡 / 已连接未使能 / 故障态整条拒绝并给原因。
                    // 这里返回 Allow 是为了让「不带旗标的读取」不被状态门提前挡掉。
                    return StateGate.Allow;

                case "load-config":
                    return (state == EngineState.Disconnected || state == EngineState.Connected || state == EngineState.Stopped)
                        ? StateGate.Allow : StateGate.Reject;

                case "zero":
                    return state == EngineState.Connected ? StateGate.Allow : StateGate.Reject;

                case "recenter":
                    // 回中：Enabled 直接走位置环；Stopped 先重新使能再回中（使能保留旧中心，见控制层）。
                    return (state == EngineState.Enabled || state == EngineState.Stopped)
                        ? StateGate.Allow : StateGate.Reject;

                case "normalize":
                    // 规整化会平移零点 + 左右限位：闭环中会跳变，只在 Connected / Stopped 允许。
                    return (state == EngineState.Connected || state == EngineState.Stopped)
                        ? StateGate.Allow : StateGate.Reject;

                case "calibrate-radius":
                    // 脉冲标定必须在已使能、且没有其它自动运动时做。
                    return state == EngineState.Enabled ? StateGate.Allow : StateGate.Reject;

                default:
                    return StateGate.Reject;
            }
        }

        /// <summary>用法硬校验（必填位置参数 + tune 的取值旗标），错就报退出码 2，先于状态机判定。</summary>
        private static string UsageError(ParsedCommand command)
        {
            if (command.Name == "zero")
            {
                if (command.Positional.Count == 0)
                    return "用法: zero pendulum | zero arm";
                string what = command.Positional[0].ToLowerInvariant();
                if (what != "pendulum" && what != "arm")
                    return "用法: zero pendulum | zero arm，收到 \"" + command.Positional[0] + "\"";
                return null;
            }
            if (command.Name == "tune")
            {
                double hz;
                int seconds;
                TuneLoop loop;
                string error;
                return TryParseTuneOptions(command, out hz, out seconds, out loop, out error) ? null : error;
            }
            return null;
        }

        private string StateNow()
        {
            return _engine == null ? "Disconnected" : _engine.State.ToString();
        }

        /// <summary>危险命令确认门（裁定 7）：只拦 enable/swingup/balance/zero。</summary>
        private bool ConfirmIfNeeded(ParsedCommand command, out CommandResult result)
        {
            result = CommandResult.Success();
            if (!CommandTable.IsDangerous(command.Name)) return true;
            if (_options.Yes) return true;

            if (_interactive)
            {
                Console.Out.Write("  " + command.Name + " 会驱动机构运动，确认执行？[y/N] ");
                Console.Out.Flush();
                string answer;
                try { answer = Console.ReadLine(); }
                catch (IOException) { answer = null; }
                if (!string.Equals(answer != null ? answer.Trim() : string.Empty, "y", StringComparison.OrdinalIgnoreCase))
                {
                    result = CommandResult.Fail(ExitCodes.NeedsConfirmation, "needs_confirmation");
                    return false;
                }
                return true;
            }

            result = CommandResult.Fail(ExitCodes.NeedsConfirmation, "needs_confirmation");
            return false;
        }

        // ---------- 命令分发 ----------

        public CommandResult Execute(ParsedCommand command)
        {
            long started = _clock.ElapsedMilliseconds;
            Pump();

            // --pendulum-cpr 可跟任意命令走（REPL 逐行解析时全局旗标回灌只在启动做一次）。
            // 控制层只在 Disconnected/Connected/Stopped 接受改写，其它状态拒绝并保持原值 ——
            // 所以必须读回验证，不许写完就宣称标定成功。
            string cprRaw = command.Value("pendulum-cpr");
            if (cprRaw != null)
            {
                double cpr;
                if (!double.TryParse(cprRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out cpr) || cpr < 0)
                    return BadNumber("pendulum-cpr", cprRaw, started);
                if (_engine != null && !ApplyPendulumCpr(cpr, command.Name))
                {
                    _output.ErrorLine("state_rejected",
                        "当前 " + StateNow() + " 不接受改写摆杆每转计数（控制层只在 Disconnected/Connected/Stopped 允许），值未变。",
                        "state_rejected", ExitCodes.StateRejected);
                    return CommandResult.Fail(ExitCodes.StateRejected, "state_rejected");
                }
            }

            // --arm-slave 同样可跟任意命令走（脚本 / REPL 逐行给的全局旗标不会被启动回灌覆盖）。
            // 它只在 Connect 期生效，控制层只在 Disconnected 接受改写 —— 同样必须读回验证。
            // 命令行口径是 1 基序号（与 status 的 "Slave n" 一致），这里减 1 转 0 基下标。
            string armSlaveRaw = command.Value("arm-slave");
            if (armSlaveRaw != null)
            {
                int armSlaveNumber;
                if (!int.TryParse(armSlaveRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out armSlaveNumber) ||
                    armSlaveNumber < 1)
                {
                    _output.ErrorLine("invalid_arguments",
                        "--arm-slave 需要正整数从站序号（1 基），收到 \"" + armSlaveRaw + "\"",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
                if (_engine != null && !ApplyArmSlaveIndex(armSlaveNumber - 1, command.Name))
                {
                    _output.ErrorLine("state_rejected",
                        "当前 " + StateNow() + " 不接受改写摆臂从站下标（只在 Disconnected 可改，它只在 Connect 期生效），值未变。",
                        "state_rejected", ExitCodes.StateRejected);
                    return CommandResult.Fail(ExitCodes.StateRejected, "state_rejected");
                }
            }

            // --arm-cpr 与 --pendulum-cpr 同口径：可跟任意命令走，写完必须读回验证。
            // 控制层只在 Disconnected/Connected/Stopped 接受改写，其它状态拒绝并保持原值。
            string armCprRaw = command.Value("arm-cpr");
            if (armCprRaw != null)
            {
                int armCpr;
                if (!int.TryParse(armCprRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out armCpr) || armCpr < 1)
                {
                    _output.ErrorLine("invalid_arguments",
                        "--arm-cpr 需要正整数摆臂每转计数（电机每转计数 × 减速比），收到 \"" + armCprRaw + "\"",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
                if (_engine != null && !ApplyArmCpr(armCpr, command.Name))
                {
                    _output.ErrorLine("state_rejected",
                        "当前 " + StateNow() + " 不接受改写摆臂每转计数（控制层只在 Disconnected/Connected/Stopped 允许），值未变。",
                        "state_rejected", ExitCodes.StateRejected);
                    return CommandResult.Fail(ExitCodes.StateRejected, "state_rejected");
                }
            }

            // --arm-gear 只在启动期生效（构造控制层时给定，运行期没有改写口）。独立 token 已在解析层
            // 收成全局值，走到这里的只有写在命令串里的那份：与生效值一致就当重复声明放行，不一致一律
            // 如实报未生效，绝不谎报"已改"。
            string armGearRaw = command.Value("arm-gear");
            if (armGearRaw != null)
            {
                double armGear;
                if (!double.TryParse(armGearRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out armGear) ||
                    !(armGear > 0) || double.IsNaN(armGear) || double.IsInfinity(armGear))
                {
                    _output.ErrorLine("invalid_arguments",
                        "--arm-gear 需要正数减速比（电机转数 / 摆臂转数），收到 \"" + armGearRaw + "\"",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
                if (armGear != ArmGearRatioValue())
                {
                    _output.ErrorLine("invalid_arguments",
                        "--arm-gear 只在启动期生效（构造控制层时给定，运行期没有改写口），本次给值 " +
                        ArmGearText(armGear) + " 未生效，当前生效值 " + ArmGearText(ArmGearRatioValue()) +
                        "。要换减速比请带 --arm-gear 重新启动。",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
            }

            // 用法错先报（2），再谈危险确认与状态机。
            string usageError = UsageError(command);
            if (usageError != null)
                return FailEcho(command, started, ExitCodes.InvalidArguments, "invalid_arguments", usageError);

            // 单命令执行超时（--timeout）在这里定：给了就给这条命令套总闸，没给走各命令既有默认超时。
            string timeoutError;
            int? timeoutSeconds = CommandTimeoutSeconds(command, out timeoutError);
            if (timeoutError != null)
                return FailEcho(command, started, ExitCodes.InvalidArguments, "invalid_arguments", timeoutError);

            // 危险命令确认门先于状态判定：脚本缺 --yes 时拿到 10（needs_confirmation），
            // 不会被状态错误的 8 淹没。REPL 里的 [y/N] 提示在前置只读检查之后（Cmd* 内部）。
            if (CommandTable.IsDangerous(command.Name) && !_options.Yes && !_interactive)
            {
                _output.AckWith(command.Name, false, ExitCodes.NeedsConfirmation, "needs_confirmation",
                    _clock.ElapsedMilliseconds - started, StateNow(),
                    "危险命令需要 --yes（非交互模式）",
                    "需要 --yes", e => e.Str("detail", "危险命令需要 --yes"));
                return CommandResult.Fail(ExitCodes.NeedsConfirmation, "needs_confirmation");
            }

            EngineState before = _engine == null ? EngineState.Disconnected : _engine.State;
            StateGate gate = Gate(command.Name, before);

            if (gate == StateGate.Reject)
            {
                return FailEcho(command, started, ExitCodes.StateRejected, "state_rejected",
                    "当前 " + before + "，" + command.Name + " 不允许（" + AllowedStatesText(command.Name) + "）");
            }

            CommandResult result;
            try
            {
                result = DispatchCapped(command, gate, started, timeoutSeconds);
            }
            catch (Exception ex)
            {
                _output.Error("internal_error", ex.GetType().Name + ": " + ex.Message, command.Name, Truncate(ex.StackTrace));
                result = CommandResult.Fail(ExitCodes.General, "internal_error");
            }

            return result;
        }

        /// <summary>
        /// 这条命令的单命令执行超时 [s]（--timeout）。null = 没给，各命令既有默认超时原样不变。
        /// 口径：--timeout 写在命令名之前 = 全局，每条命令都管；写在命令名之后 = 只管这一条，优先于全局。
        /// swingup / setswingup 的 --timeout 是毫秒别名（= --timeout-ms），归它们自己的起摆截止，不走这里。
        /// </summary>
        private int? CommandTimeoutSeconds(ParsedCommand command, out string error)
        {
            error = null;
            if (command.Spec != null && command.Spec.TimeoutAliasMs) return _options.TimeoutSeconds;

            string raw = command.Value("timeout");
            if (raw == null) return _options.TimeoutSeconds;

            int seconds;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
            {
                error = "--timeout 需要正整数秒（单条命令执行超时），收到 \"" + raw + "\"";
                return null;
            }
            return seconds;
        }

        /// <summary>单命令执行超时（--timeout）到点没到。到点 = 等待立刻中断、按超时收口，绝不顺手急停。</summary>
        private bool CommandExpired
        {
            get { return _clock.ElapsedMilliseconds >= Volatile.Read(ref _commandDeadlineTicks); }
        }

        /// <summary>这条命令的 --timeout 总预算 [ms]。0 = 没给，调用方走各命令既有默认超时。</summary>
        private long CommandTimeoutBudgetMs(long started)
        {
            long deadline = Volatile.Read(ref _commandDeadlineTicks);
            return deadline == long.MaxValue ? 0 : Math.Max(0, deadline - started);
        }

        /// <summary>
        /// 命令分发 + 单命令执行超时（--timeout）。
        /// 没给超时 = 就在本线程跑，行为与既有完全一致；给了超时 = 命令本体跑在后台线程，到点只中断等待、
        /// 按超时失败收口。<b>超时不是急停</b>：本路径绝不调 EmergencyStop（急停只归 Ctrl+C / estop / 联锁）。
        /// </summary>
        private CommandResult DispatchCapped(ParsedCommand command, StateGate gate, long started, int? timeoutSeconds)
        {
            if (timeoutSeconds == null)
            {
                Volatile.Write(ref _commandDeadlineTicks, long.MaxValue);
                return Dispatch(command, gate, started);
            }

            Volatile.Write(ref _commandDeadlineTicks, started + timeoutSeconds.Value * 1000L);

            CommandResult result = CommandResult.Success();
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { result = Dispatch(command, gate, started); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true, Name = "furuta-cli-command" };
            worker.Start();

            // 等待分 20 ms 小步：到点那一下先问「跑完没有」，跑完了就算完成，还没跑完才判超时。
            // 判定粒度由此是 20 ms —— 切断点落在到点后 0~20 ms，宁可让它多跑一拍，也不把刚好跑完的命令冤枉成超时。
            bool joined = false;
            while (!joined)
            {
                if (worker.Join(20)) { joined = true; break; }
                if (_clock.ElapsedMilliseconds >= Volatile.Read(ref _commandDeadlineTicks)) break;
            }

            if (!joined)
            {
                // 到点：只中断等待、失败收口。被弃线程的迟到输出整条丢掉，这条命令的回执只有下面这一条。
                _output.MuteThread(worker);
                return TimeoutEcho(command, started, timeoutSeconds.Value);
            }

            if (failure != null)
            {
                _output.Error("internal_error", failure.GetType().Name + ": " + failure.Message, command.Name, Truncate(failure.StackTrace));
                return CommandResult.Fail(ExitCodes.General, "internal_error");
            }

            // 命令自己的等待环是协作式退出的：它先看见截止就交 TimedOut，回执同样只打一条。
            if (result.TimedOut) return TimeoutEcho(command, started, timeoutSeconds.Value);
            return result;
        }

        /// <summary>
        /// 单命令执行超时的唯一回执 + 失败码归口。不造新码：默认归 1（errorCode=timeout）；
        /// swingup 归它自带起摆截止的既有超时码 5 / FaultCode:SwingUpTimeout —— --timeout 覆盖的就是那个截止，同口径。
        /// </summary>
        private CommandResult TimeoutEcho(ParsedCommand command, long started, int timeoutSeconds)
        {
            CommandResult result = TimeoutResult(command);
            string message = "命令 " + command.Name + " 超过 " + timeoutSeconds.ToString(CultureInfo.InvariantCulture) +
                             " s（--timeout 单命令执行超时）没跑完，已中断等待并按超时失败收口；" +
                             "未触发急停（急停只归 Ctrl+C / estop / 联锁），机构保持 " + StateNow() + "。";
            return FailEcho(command, started, result.Code, result.ErrorCode, message);
        }

        /// <summary>超时失败码：默认 1 / timeout，swingup 用它既有的起摆超时码。</summary>
        private static CommandResult TimeoutResult(ParsedCommand command)
        {
            return command != null && command.Name == "swingup"
                ? CommandResult.Timeout(ExitCodes.BusFault, "FaultCode:SwingUpTimeout")
                : CommandResult.Timeout();
        }

        private CommandResult FailEcho(ParsedCommand command, long started, int code, string errorCode, string message)
        {
            return FailEcho(command.Name, started, code, errorCode, message);
        }

        /// <summary>按命令名回执失败（供没有 ParsedCommand 的收尾路径复用同一格式）。</summary>
        private CommandResult FailEcho(string commandName, long started, int code, string errorCode, string message)
        {
            _output.AckWith(commandName, false, code, errorCode, _clock.ElapsedMilliseconds - started, StateNow(),
                message, message, e => e.Str("detail", message));
            _output.WriteLogRecord("error", message, commandName, false, code, errorCode);
            return CommandResult.Fail(code, errorCode);
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 500 ? text : text.Substring(0, 500);
        }

        private static string AllowedStatesText(string command)
        {
            switch (command)
            {
                case "connect": return "需要 Disconnected";
                case "enable": return "需要 Connected 或 Stopped";
                case "disable": return "需要 Connected / Enabled / Stopped";
                case "swingup": return "需要 Enabled 或 Stopped，Fault 须先 fault-reset";
                case "balance": return "需要 Enabled（手动近顶捕获）/ Swinging / Balancing；Stopped/Fault 须先 enable 或重新 swingup";
                case "stop": return "需要已连接";
                case "fault-reset": return "需要 Stopped 或 Fault";
                case "setswingup": return "可读取任意状态；改参数需要 Disconnected / Enabled / Stopped（起摆中仅限热改项）";
                case "normalize": return "需要 Connected / Stopped（闭环中零点会跳变）";
                case "calibrate-radius": return "需要已使能（Enabled）且摆杆下垂静止";
                case "load-config": return "需要 Disconnected / Connected / Stopped";
                case "zero": return "需要 Connected（未使能）";
                case "tune": return "需要已连接（Disconnected 看不到遥测）";
                default: return "见 help " + command;
            }
        }

        private CommandResult Dispatch(ParsedCommand command, StateGate gate, long started)
        {
            switch (command.Name)
            {
                case "status": return CmdStatus(command, started);
                case "connect": return CmdConnect(command, gate, started);
                case "disconnect": return CmdDisconnect(command, gate, started);
                case "enable": return CmdEnable(command, gate, started);
                case "disable": return CmdDisable(command, gate, started);
                case "swingup": return CmdSwingUp(command, gate, started);
                case "balance": return CmdBalance(command, gate, started);
                case "stop": return CmdStop(command, gate, started);
                case "estop": return CmdEStop(command, started);
                case "fault-reset": return CmdFaultReset(command, gate, started);
                case "setpid": return CmdSetPid(command, started);
                case "setswingup": return CmdSetSwingUp(command, started);
                case "save-config": return CmdSaveConfig(command, started);
                case "load-config": return CmdLoadConfig(command, gate, started);
                case "telemetry": return CmdTelemetry(command, started);
                case "watch": return CmdWatch(command, started);
                case "dashboard": return CmdDashboard(command, started);
                case "tune": return CmdTune(command, started);
                case "quit": return CmdQuit(command, started);
                case "help": return CmdHelp(command, started);
                case "version": return CmdVersion(command, started);
                case "setlimits": return CmdSetLimits(command, started);
                case "zero": return CmdZero(command, gate, started);
                case "recenter": return CmdRecenter(command, gate, started);
                case "normalize": return CmdNormalize(command, started);
                case "calibrate-radius": return CmdCalibrateRadius(command, gate, started);
                case "log-path": return CmdLogPath(command, started);
                default: return FailEcho(command, started, ExitCodes.InvalidArguments, "invalid_arguments", "未实现的命令 " + command.Name);
            }
        }

        // ---------- 编排：一次性 / 序列 / 脚本 ----------

        /// <summary>顺序执行一组命令。任一条失败即停止后续，退出码取失败那条。</summary>
        public int RunCommands(List<ParsedCommand> commands)
        {
            int exitCode = ExitCodes.Ok;
            for (int i = 0; i < commands.Count; i++)
            {
                if (_exitRequested || _cancelRequested) break;
                CommandResult result = Execute(commands[i]);
                if (!result.Ok)
                {
                    exitCode = result.Code;
                    break;
                }
            }
            return exitCode;
        }

        /// <summary>脚本 / stdin 管道：逐行当命令执行。</summary>
        public int RunScript(IEnumerable<string> lines)
        {
            int exitCode = ExitCodes.Ok;
            foreach (string raw in lines)
            {
                if (_exitRequested || _cancelRequested) break;
                Pump();
                string line = raw == null ? string.Empty : raw.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                foreach (string segment in CliCommandParser.SplitSequence(line))
                {
                    ParsedCommand command;
                    string error;
                    int code;
                    if (!CliCommandParser.TryParseCommand(segment, out command, out error, out code))
                    {
                        _output.ErrorLine("invalid_arguments", error, "invalid_arguments", code);
                        return code;
                    }
                    CommandResult result = Execute(command);
                    if (!result.Ok)
                    {
                        exitCode = result.Code;
                        return exitCode;
                    }
                }
            }
            return exitCode;
        }

        // ---------- 编排：REPL ----------

        /// <summary>交互 REPL：提示符带状态，Ctrl+C=急停，60 秒无输入且在运动态自动停。</summary>
        public int RunRepl()
        {
            _interactive = true;
            Console.Out.WriteLine("FurutaPendulum CLI " + VersionText() + " | " +
                                  DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                  " | Ctrl+C=急停  quit=退出");
            Console.Out.WriteLine();
            Console.Out.WriteLine("软件急停只清 0x6040，不是安全回路，不替代硬接线急停 / STO / 限位。");
            Console.Out.WriteLine();

            var lines = new BlockingCollection<string>();
            // tune 的"回车=停止观察"复用这条输入队列，避免与 REPL 抢 Console.ReadLine。
            _pendingInput = lines;
            var reader = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = Console.ReadLine()) != null)
                    {
                        if (!lines.TryAdd(line)) break;
                    }
                }
                catch (IOException ex)
                {
                    Debug.WriteLine("REPL 标准输入中断（IOException）：" + ex.Message + "；后续收不到新命令，可用 --script 或重启会话。");
                }
                catch (ObjectDisposedException ex)
                {
                    Debug.WriteLine("REPL 标准输入已释放（ObjectDisposedException）：" + ex.Message + "；输入队列关闭。");
                }
                finally
                {
                    try { lines.CompleteAdding(); }
                    catch (InvalidOperationException ex)
                    {
                        Debug.WriteLine("REPL 输入队列 CompleteAdding 失败（InvalidOperationException）：" + ex.Message + "；队列可能已关闭。");
                    }
                }
            }) { IsBackground = true, Name = "furuta-cli-stdin" };
            reader.Start();

            long lastInputMs = _clock.ElapsedMilliseconds;
            int exitCode = ExitCodes.Ok;

            while (!_exitRequested)
            {
                Pump();
                string line;
                bool haveLine;
                if (_pushbackLine != null)
                {
                    // tune 停止观察时带回的命令行：接着执行，不丢。
                    line = _pushbackLine;
                    _pushbackLine = null;
                    haveLine = true;
                }
                else
                {
                    haveLine = lines.TryTake(out line, 250);
                }
                if (haveLine)
                {
                    lastInputMs = _clock.ElapsedMilliseconds;
                    string trimmed = line == null ? string.Empty : line.Trim();
                    if (trimmed.Length == 0) { PrintPrompt(); continue; }

                    foreach (string segment in CliCommandParser.SplitSequence(trimmed))
                    {
                        ParsedCommand command;
                        string error;
                        int code;
                        if (!CliCommandParser.TryParseCommand(segment, out command, out error, out code))
                        {
                            _output.ErrorLine("invalid_arguments", error, "invalid_arguments", code);
                            exitCode = code;
                            continue;
                        }
                        if (command.Name == "quit")
                        {
                            Execute(command);
                            _exitRequested = true;
                            break;
                        }
                        CommandResult result = Execute(command);
                        if (!result.Ok) exitCode = result.Code;
                        if (_exitRequested) break;
                    }
                    if (_exitRequested) break;
                    PrintPrompt();
                    continue;
                }

                // 无人值守看门狗：60 秒无输入且仍在运动态 → 停 + 急停。
                EngineState state = _engine == null ? EngineState.Disconnected : _engine.State;
                if ((state == EngineState.Swinging || state == EngineState.Balancing) &&
                    _clock.ElapsedMilliseconds - lastInputMs > WATCHDOG_IDLE_MS)
                {
                    _output.ErrorLine("interlock", "REPL 60 秒无输入且仍在 " + state + "，命令看门狗已停机并急停",
                        "cancelled", ExitCodes.Interlock);
                    SafeEmergencyStop("command_watchdog");
                    _sessionExit = ExitCodes.Interlock;
                    exitCode = ExitCodes.Interlock;
                    break;
                }
            }

            _pendingInput = null;
            if (_cancelRequested) exitCode = ExitCodes.Cancelled;
            return exitCode;
        }

        private void PrintPrompt()
        {
            Console.Out.Write("furuta [" + StateNow() + "]> ");
            Console.Out.Flush();
        }

        private static string VersionText()
        {
            Version v = typeof(CliSession).Assembly.GetName().Version;
            return v != null ? v.ToString() : "1.0.0";
        }

        // ---------- 运动看门狗 ----------

        /// <summary>
        /// 显式时长保持（--seconds N）：保持 N 秒后自动 stop，算正常完成。
        /// 保持期间暂停会话级运动看门狗，尊重用户显式给出的时长。
        /// </summary>
        private CommandResult HoldMotion(ParsedCommand command, string label, int seconds, long started)
        {
            if (seconds == 0)
            {
                _output.Warn("! warning  " + label + " 不限时运行，靠 Ctrl+C 或联锁停机");
                return CommandResult.Success();
            }

            Interlocked.Increment(ref _watchdogSuppressed);
            try
            {
                long deadline = _clock.ElapsedMilliseconds + seconds * 1000L;
                while (_clock.ElapsedMilliseconds < deadline)
                {
                    Pump();
                    if (_cancelRequested || _exitRequested) return CommandResult.Fail(ExitCodes.Cancelled, "cancelled");
                    // --timeout 到点：只中断这段保持等待并按超时收口，不 Stop 也不急停。
                    if (CommandExpired) return CommandResult.Timeout();
                    EngineState state = _engine == null ? EngineState.Disconnected : _engine.State;
                    if (state != EngineState.Swinging && state != EngineState.Balancing && state != EngineState.Enabled)
                    {
                        PendulumTelemetry fault = new PendulumTelemetry();
                        bool have = TrySnapshot(out fault);
                        int code = have ? ExitCodes.ForFault(fault.FaultCode) : ExitCodes.Ok;
                        return CommandResult.Fail(code == ExitCodes.Ok ? ExitCodes.BusFault : code, "FaultCode:" + (have ? fault.FaultCode.ToString() : "None"));
                    }
                    Thread.Sleep(50);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _watchdogSuppressed);
            }

            _engine.Stop();
            _output.AckWith(command.Name, true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                label + " held " + seconds.ToString(CultureInfo.InvariantCulture) + "s",
                "held=" + seconds.ToString(CultureInfo.InvariantCulture) + "s  state=" + StateNow() +
                "  φ偏差(起始位0)=" + SignedText(ReadPhiDevFromHome()) + " rad",
                e => e.Int("heldSec", seconds).Num("phiDevFromHomeRad", ReadPhiDevFromHome()));
            return CommandResult.Success();
        }

        private static string SignedText(double v)
        {
            return (v >= 0 ? "+" : string.Empty) + v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 无头看门狗（设计稿 11.4）：非交互模式下连续处于运动态超过上限就自动 Stop()。
        /// 到点自动停机算正常完成（退出码 0），不是失败。
        /// </summary>
        public void StartMotionWatchdog(int capSeconds)
        {
            if (capSeconds <= 0) return;
            var thread = new Thread(() =>
            {
                while (!_exitRequested)
                {
                    Thread.Sleep(500);
                    if (Volatile.Read(ref _watchdogSuppressed) != 0) continue;
                    long start = Volatile.Read(ref _motionStartTicks);
                    if (start < 0) continue;
                    if (_clock.ElapsedMilliseconds - start < capSeconds * 1000L) continue;

                    _output.WriteLogRecord("motion_watchdog",
                        "运动态持续 " + capSeconds.ToString(CultureInfo.InvariantCulture) + "s，看门狗自动停机",
                        null, true, 0, "ok");
                    try { _engine.Stop(); }
                    catch (Exception ex)
                    {
                        _output.WriteLogRecord("estop_failed", ex.GetType().Name + ": " + ex.Message, null, false,
                            ExitCodes.DeviceFault, "internal_error");
                    }
                    _motionCapHit = true;
                    _exitRequested = true;
                    break;
                }
            }) { IsBackground = true, Name = "furuta-cli-motion-watchdog" };
            thread.Start();
        }

        public bool MotionCapHit { get { return _motionCapHit; } }

        private double ReadPhiDevFromHome()
        {
            PendulumTelemetry t;
            return TrySnapshot(out t) ? t.PhiDevFromHomeRad : 0;
        }

        // ---------- 急停 / 收尾 ----------

        private void SafeEmergencyStop(string reason)
        {
            try
            {
                if (_engine == null) return;
                _engine.EmergencyStop();
                _output.Event("estop", "estop", e => e.Str("reason", reason).Bool("estopLatch", true));
            }
            catch (Exception ex)
            {
                _output.WriteLogRecord("estop_failed", ex.GetType().Name + ": " + ex.Message, null, false,
                    ExitCodes.DeviceFault, "internal_error");
            }
        }

        /// <summary>
        /// 固定收尾顺序（设计稿 11.5）：仍在运动先急停，再走控制层 Disconnect() 的固定 teardown。
        /// CLI 不自造顺序，不做「跳过退出急停」的后门。
        /// </summary>
        public int Teardown(bool report)
        {
            if (_engine == null) return ExitCodes.Ok;
            try
            {
                EngineState state = _engine.State;
                if (state == EngineState.Disconnected) return ExitCodes.Ok;
                if (state == EngineState.Swinging || state == EngineState.Balancing)
                    SafeEmergencyStop("teardown");

                _engine.Disconnect();
                if (report)
                    _output.WriteLogRecord("teardown", "teardown=clean", null, true, 0, "ok");
                return ExitCodes.Ok;
            }
            catch (Exception ex)
            {
                _output.WriteLogRecord("teardown_failed",
                    ex.GetType().Name + ": " + ex.Message + " " + Truncate(ex.StackTrace), null, false,
                    ExitCodes.DeviceFault, "teardown_failed");
                return ExitCodes.DeviceFault;
            }
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
