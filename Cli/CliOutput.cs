using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>退出码表（设计稿 10.1）。脚本判据，不可改。</summary>
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int General = 1;
        public const int InvalidArguments = 2;
        public const int SdkActivation = 3;
        public const int ConnectFailed = 4;
        public const int BusFault = 5;
        public const int ConfigError = 6;
        public const int Interlock = 7;
        public const int StateRejected = 8;
        public const int DeviceFault = 9;
        public const int NeedsConfirmation = 10;
        public const int Cancelled = 130;

        /// <summary>FaultCode → 退出码（设计稿 8.4 映射表，脚本判据）。</summary>
        public static int ForFault(FaultCode code)
        {
            switch (code)
            {
                case FaultCode.None:
                case FaultCode.UserStop:
                    return Ok;
                case FaultCode.ThetaOverrun:
                case FaultCode.BalanceLost:
                case FaultCode.FollowingError:
                case FaultCode.EStopInput:
                case FaultCode.SoftLimit:
                    return Interlock;
                case FaultCode.TimingInvalid:
                case FaultCode.PdoFrameLoss:
                case FaultCode.WkcInvalid:
                case FaultCode.EtherCatState:
                case FaultCode.SwingUpTimeout:
                    return BusFault;
                case FaultCode.DriveFault:
                case FaultCode.EnableLost:
                case FaultCode.StopSequenceTimeout:
                case FaultCode.TeardownTimeout:
                    return DeviceFault;
                case FaultCode.CallbackException:
                    return General;
                case FaultCode.IdentityMismatch:
                    return ConnectFailed;
                case FaultCode.ConfigInvalid:
                    return ConfigError;
                default:
                    return General;
            }
        }
    }

    /// <summary>全局旗标（设计稿 2.1）。</summary>
    internal sealed class CliOptions
    {
        public bool Json;
        public bool Quiet;
        public bool Color;
        public bool Yes;
        public bool Help;
        public bool Version;
        public string ConfigPath;
        public string LogPath;
        public string ResultPath;
        public string ScriptPath;
        /// <summary>
        /// 单条命令执行超时 [s]（--timeout）。null = 没给：各命令照自己既有默认超时走，CLI 不再套总闸。
        /// 到点 = 中断该条命令的等待、按超时失败收口，不触发急停（急停只归 Ctrl+C / estop / 联锁）。
        /// </summary>
        public int? TimeoutSeconds;
        /// <summary>摆杆每转计数 CPR_PENDULUM（线数×4×传动比）。机器标定值，不进 furuta_params.json。</summary>
        public double? PendulumCpr;
        /// <summary>
        /// 摆臂每转计数 CPR_ARM = 电机每转计数 × 减速比（<b>正整数</b>）。机器标定值，不进 furuta_params.json。
        /// null = 不给，交给控制层用 0x2102（开闭环细分设置）× <see cref="ArmGearRatio"/> 推导。
        /// </summary>
        public int? ArmCpr;
        /// <summary>
        /// 摆臂减速比 = 电机转数 / 摆臂转数（正浮点）。null = 不给 = 1.0（无减速）。
        /// <b>只在构造 PendulumEngine 时生效</b>（控制层没有运行期改减速比的口），运行中给值按参数错拒绝。
        /// </summary>
        public double? ArmGearRatio;
        // --arm-slave 走 ArmSlaveNumber（1 基序号，见下），不要另设 0 基字段。
        /// <summary>
        /// 摆臂从站序号，<b>1 基</b>（与 status / 枚举显示的 "Slave n" 一致）。
        /// 落到 PendulumEngineOptions.ArmSlaveIndex 时减 1 转 0 基。null = 不给，交给控制层自动判定。
        /// </summary>
        public int? ArmSlaveNumber;
        /// <summary>网卡接口名（Name 或 Description，唯一匹配 DENI 里的 Source MAC）。不给 = 空，控制层按 DENI Source MAC 自动匹配唯一网卡（0/多候选拒连）。</summary>
        public string NicName;
        /// <summary>
        /// 进 OP 方式（--op-entry standard|direct）。构造 PendulumEngine 前已由入口生效，
        /// 这里只做取值校验与生效值回显。null = 没给，走默认 standard（PreOp → SafeOp → OP）。
        /// </summary>
        public OpEntryMode? OpEntry;
    }

    /// <summary>JSON Lines 信封构建器。字段值只写字符串或数字，绝不写 null 键。</summary>
    internal sealed class JsonEnvelope
    {
        private readonly List<KeyValuePair<string, string>> _fields = new List<KeyValuePair<string, string>>(16);

        public JsonEnvelope(string kind, string msg)
        {
            Add("ts", Quote(IsoUtcNow()));
            Add("kind", Quote(kind));
            Add("msg", Quote(msg ?? string.Empty));
        }

        private void Add(string key, string renderedValue)
        {
            _fields.Add(new KeyValuePair<string, string>(key, renderedValue));
        }

        public JsonEnvelope Str(string key, string value)
        {
            Add(key, Quote(value ?? string.Empty));
            return this;
        }

        public JsonEnvelope Int(string key, long value)
        {
            Add(key, value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonEnvelope Num(string key, double value)
        {
            Add(key, double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonEnvelope Bool(string key, bool value)
        {
            Add(key, value ? "true" : "false");
            return this;
        }

        /// <summary>渲染成一行 JSON；extraKey/extraValue 供 last_result 追加 exitCode。</summary>
        public string Render(string extraKey, string extraValue)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            for (int i = 0; i < _fields.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(_fields[i].Key).Append("\":").Append(_fields[i].Value);
            }
            if (extraKey != null)
                sb.Append(",\"").Append(extraKey).Append("\":").Append(extraValue);
            sb.Append('}');
            return sb.ToString();
        }

        public string Render()
        {
            return Render(null, null);
        }

        public static string IsoUtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static string Quote(string value)
        {
            if (value == null) value = string.Empty;
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// 输出层：人读表格 / JSON Lines 双格式 + 诊断日志落盘 + last_result.json。
    /// 不判安全、不调控制算法，只负责渲染与落盘。
    /// </summary>
    internal sealed class CliOutput
    {
        private readonly CliOptions _options;
        private readonly DiagnosticLogWriter _log;
        private JsonEnvelope _lastEnvelope;
        private long _telemetryDropped;
        /// <summary>被丢弃的命令线程（超时收口后仍在后台跑）：它的迟到输出一律不再落。</summary>
        private volatile Thread _mutedThread;

        public CliOptions Options { get { return _options; } }
        public DiagnosticLogWriter Log { get { return _log; } }
        public long TelemetryDropped { get { return _telemetryDropped; } }

        public CliOutput(CliOptions options, DiagnosticLogWriter log)
        {
            _options = options ?? new CliOptions();
            _log = log;
        }

        public void CountDropped()
        {
            _telemetryDropped++;
        }

        /// <summary>
        /// 把某个命令线程的输出整条丢掉。命令执行超时（--timeout 到点）收口后，被弃的后台线程
        /// 可能还要跑一会儿才回，它的迟到 ack / error 不许再落进控制台和日志 —— 那条命令的唯一
        /// 回执已经在超时收口时如实打过了，再冒一行就是自相矛盾。
        /// </summary>
        public void MuteThread(Thread thread)
        {
            _mutedThread = thread;
        }

        private bool IsMuted()
        {
            return ReferenceEquals(Thread.CurrentThread, _mutedThread);
        }

        public void Info(string line)
        {
            if (IsMuted() || _options.Quiet) return;
            Console.Out.WriteLine(line);
        }

        public void Line(string line)
        {
            if (IsMuted()) return;
            Console.Out.WriteLine(line);
        }

        public void Warn(string line)
        {
            if (IsMuted()) return;
            if (_options.Color)
            {
                ConsoleColor previous = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine(line);
                }
                finally
                {
                    Console.ForegroundColor = previous;
                }
            }
            else
            {
                Console.Error.WriteLine(line);
            }
        }

        // ---------- 信封 ----------

        public void Ack(string cmd, bool ok, int code, string errorCode, long elapsedMs, string state, string msg)
        {
            AckWith(cmd, ok, code, errorCode, elapsedMs, state, msg, null, null);
        }

        /// <summary>ack 附带自由字段（JSON 逐个进信封，人读拼进同一行）。</summary>
        public void AckWith(string cmd, bool ok, int code, string errorCode, long elapsedMs, string state, string msg,
            string humanDetail, Action<JsonEnvelope> extra)
        {
            if (IsMuted()) return;
            var envelope = new JsonEnvelope("ack", msg);
            envelope.Str("cmd", cmd).Bool("ok", ok).Int("code", code).Str("errorCode", errorCode)
                    .Int("elapsedMs", elapsedMs).Str("state", state);
            if (extra != null) extra(envelope);
            _lastEnvelope = envelope;

            if (_options.Json)
            {
                Console.Out.WriteLine(envelope.Render());
            }
            else
            {
                string tail = string.IsNullOrEmpty(humanDetail) ? string.Empty : "  " + humanDetail;
                if (ok)
                    Console.Out.WriteLine("ok " + cmd.PadRight(12) + tail + "  (" + FormatSeconds(elapsedMs) + ")");
                else
                    Console.Error.WriteLine("! error  " + errorCode + "  " + (string.IsNullOrEmpty(msg) ? humanDetail : msg));
            }
            WriteLogRecord(ok ? "ack" : "error", msg, cmd, ok, code, errorCode);
        }

        public void Event(string eventName, string msg, Action<JsonEnvelope> extra)
        {
            Emit("event", msg, e =>
            {
                e.Str("event", eventName);
                if (extra != null) extra(e);
            });
        }

        /// <summary>结构化错误（设计稿 8.2 kind=error）。</summary>
        public void Error(string errorCode, string message, string stage, string stack)
        {
            if (IsMuted()) return;
            var envelope = new JsonEnvelope("error", message);
            envelope.Str("status", "error").Str("code", errorCode).Str("message", message)
                    .Str("stage", stage).Str("stack", stack ?? string.Empty);
            _lastEnvelope = envelope;

            if (_options.Json)
                Console.Out.WriteLine(envelope.Render());
            else
                Console.Error.WriteLine("! error  " + errorCode + "  " + message + (string.IsNullOrEmpty(stage) ? string.Empty : "  [" + stage + "]"));
            WriteLogRecord("error", message, null, false, ExitCodes.General, errorCode);
        }

        /// <summary>人读模式下错误行，带上下文（设计稿 8.1 的 "! error  <类>  <详情>"）。</summary>
        public void ErrorLine(string category, string message, string errorCode, int code)
        {
            if (IsMuted()) return;
            var envelope = new JsonEnvelope("error", message);
            envelope.Str("status", "error").Str("code", errorCode).Str("message", message)
                    .Str("stage", category).Str("stack", string.Empty);
            _lastEnvelope = envelope;

            if (_options.Json)
                Console.Out.WriteLine(envelope.Render());
            else
                Console.Error.WriteLine("! error  " + category + "  " + message);
            WriteLogRecord("error", message, category, false, code, errorCode);
        }

        public void LogForward(string kind, string msg, Action<JsonEnvelope> extra)
        {
            if (_options.Json)
            {
                Emit("log", msg, e =>
                {
                    e.Str("logKind", kind);
                    if (extra != null) extra(e);
                });
            }
            else if (!_options.Quiet)
            {
                Console.Out.WriteLine("  · " + kind + "  " + msg);
            }
            WriteLogRecord(kind, msg, null, true, 0, "ok");
        }

        public void Summary(string msg, Action<JsonEnvelope> extra)
        {
            Emit("summary", msg, extra);
        }

        /// <summary>遥测帧（kind=telemetry，字段名与 PendulumTelemetry 一致）。</summary>
        public void TelemetrySample(Action<JsonEnvelope> extra)
        {
            Emit("telemetry", "sample", extra);
        }

        private void Emit(string kind, string msg, Action<JsonEnvelope> decorate)
        {
            if (IsMuted()) return;
            var envelope = new JsonEnvelope(kind, msg);
            if (decorate != null) decorate(envelope);
            if (_options.Json)
                Console.Out.WriteLine(envelope.Render());
            WriteLogRecord(kind, msg, null, kind != "error", 0, kind == "error" ? "error" : "ok");
        }

        // ---------- 日志落盘 ----------

        /// <summary>诊断日志行 = StructuredLog JSON Lines。</summary>
        public void WriteLogRecord(string kind, string msg, string cmd, bool ok, int code, string errorCode)
        {
            if (IsMuted() || _log == null) return;
            try
            {
                // 统一行格式（与 GUI 同一个根 / 命名 / 行）：[时间] [kind] 正文。
                // 原 JSON 记录里的语义保留成 key=value 追加在正文后（cmd / ok / code / errorCode），
                // 不再写 JSON 行 —— 一个日志根只允许一种行格式。
                var sb = new StringBuilder(msg ?? string.Empty);
                if (!string.IsNullOrEmpty(cmd)) sb.Append(" cmd=").Append(cmd);
                sb.Append(" ok=").Append(ok ? "true" : "false");
                sb.Append(" code=").Append(code.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(errorCode)) sb.Append(" errorCode=").Append(errorCode);
                _log.Write(kind, sb.ToString());
            }
            catch (Exception ex)
            {
                // 日志写入自身失败绝不能影响命令语义，只在控制台留痕。
                Console.Error.WriteLine("! log_write_failed  " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>last_result.json：最后一条 ack / error + exitCode（设计稿 9）。</summary>
        public void WriteResultFile(int exitCode, string resultPath)
        {
            try
            {
                string path = string.IsNullOrWhiteSpace(resultPath)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DarraEtherCAT", "FurutaPendulum", "last_result.json")
                    : Path.GetFullPath(resultPath);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string json;
                if (_lastEnvelope != null)
                    json = _lastEnvelope.Render("exitCode", exitCode.ToString(CultureInfo.InvariantCulture));
                else
                    json = "{\"ts\":\"" + JsonEnvelope.IsoUtcNow() + "\",\"kind\":\"summary\",\"msg\":\"session end\",\"exitCode\":"
                           + exitCode.ToString(CultureInfo.InvariantCulture) + "}";
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("! result_write_failed  " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------- 人读格式 ----------

        public static string FormatSeconds(long elapsedMs)
        {
            return (elapsedMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";
        }

        public static string Rad(double rad)
        {
            return rad.ToString("0.000", CultureInfo.InvariantCulture);
        }

        public static string Deg(double rad)
        {
            return (rad * 180.0 / Math.PI).ToString("0.00", CultureInfo.InvariantCulture) + "°";
        }

        /// <summary>摆杆角**显示用**：归一化到 [0,360)，避免负角 / 超过 360 让现场困惑。内部 θ 仍是累计角。</summary>
        public static string Deg360(double rad)
        {
            if (double.IsNaN(rad) || double.IsInfinity(rad)) return "n/a";
            double deg = rad * 180.0 / Math.PI;
            deg = deg % 360.0;
            if (deg < 0) deg += 360.0;
            return deg.ToString("0.00", CultureInfo.InvariantCulture) + "°";
        }

        /// <summary>着色写一行到 stdout。--color 未开时原样写，管道下自动不着色。</summary>
        public void WriteColored(string text, ConsoleColor color)
        {
            if (!_options.Color)
            {
                Console.Out.WriteLine(text);
                return;
            }
            ConsoleColor previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.Out.WriteLine(text);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }
}
