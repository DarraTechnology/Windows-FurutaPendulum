using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// 实时整定：tune 观察视图 + setpid 增益对照。只读遥测、只调 BalancePid，
    /// 不写控制算法、不碰安全判定（Ctrl+C 急停语义保持 CliSession 原样）。
    /// </summary>
    internal sealed partial class CliSession
    {
        /// <summary>tune 只看哪一环。both=双环全列。</summary>
        private enum TuneLoop
        {
            Theta,
            Phi,
            Both
        }

        // ---------- tune：实时整定视图 ----------

        private CommandResult CmdTune(ParsedCommand command, long started)
        {
            double hz;
            int seconds;
            TuneLoop loop;
            string error;
            if (!TryParseTuneOptions(command, out hz, out seconds, out loop, out error))
                return FailEcho(command, started, ExitCodes.InvalidArguments, "invalid_arguments", error);
            return RunTuneLoop(command, hz, seconds, loop, started);
        }

        /// <summary>
        /// tune 参数硬校验（用法错先报 2，再谈状态机）。UsageError 与 CmdTune 共用这一个解析，
        /// 判定只有一处，不会两处口径漂。
        /// </summary>
        private static bool TryParseTuneOptions(ParsedCommand command, out double hz, out int seconds,
            out TuneLoop loop, out string error)
        {
            hz = 10.0;
            seconds = -1;
            loop = TuneLoop.Both;
            error = null;

            if (command.Has("hz"))
            {
                double raw;
                if (!command.TryDouble("hz", out raw) || raw < 5.0 || raw > 20.0)
                {
                    error = "--hz 取值范围 5..20（整定视图刷新率）";
                    return false;
                }
                hz = raw;
            }

            if (command.Has("seconds"))
            {
                if (!command.TryInt("seconds", out seconds) || seconds <= 0)
                {
                    error = "--seconds 需要正整数秒";
                    return false;
                }
            }

            if (command.Has("loop"))
            {
                string raw = command.Value("loop") ?? string.Empty;
                if (string.Equals(raw, "theta", StringComparison.OrdinalIgnoreCase)) loop = TuneLoop.Theta;
                else if (string.Equals(raw, "phi", StringComparison.OrdinalIgnoreCase)) loop = TuneLoop.Phi;
                else if (string.Equals(raw, "both", StringComparison.OrdinalIgnoreCase)) loop = TuneLoop.Both;
                else
                {
                    error = "--loop 只接受 theta | phi | both，收到 \"" + raw + "\"";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 整定行流式输出。停止语义：回车 = 停止观察回提示符（不急停）；Ctrl+C 仍走 CliSession 的急停；
        /// --seconds 到点自动停。只在 tune 加回车停止，telemetry / watch 的现有行为不动。
        /// </summary>
        private CommandResult RunTuneLoop(ParsedCommand command, double hz, int seconds, TuneLoop loop, long started)
        {
            PidGains gains = _engine == null ? new PidGains() : _engine.BalancePid;
            if (_options.Json)
                _output.Event("tune_header", "tune", e => e.Str("view", "tune").Str("loop", loop.ToString().ToLowerInvariant())
                    .Num("hz", hz).Int("seconds", seconds).Str("columns", TuneLegend()));
            else
                _output.Line(TuneHeader(gains));

            StartTuneConsoleStopReader();

            long begin = _clock.ElapsedMilliseconds;
            long deadline = seconds > 0 ? begin + seconds * 1000L : long.MaxValue;
            int intervalMs = Math.Max(1, (int)(1000.0 / hz));
            bool stoppedByEnter = false;
            bool warnedNoSnapshot = false;

            while (_clock.ElapsedMilliseconds < deadline)
            {
                Pump();
                if (_cancelRequested || _exitRequested)
                    return CommandResult.Fail(ExitCodes.Cancelled, "cancelled");
                // --timeout 到点：只中断观察等待并按超时收口，不急停（急停仍只归 Ctrl+C）。
                if (CommandExpired) return CommandResult.Timeout();

                PendulumTelemetry t;
                bool have = TrySnapshot(out t);
                if (have)
                {
                    WriteTuneLine(t, _clock.ElapsedMilliseconds - begin, loop, gains);
                }
                else if (!warnedNoSnapshot && !_options.Json && !_options.Quiet)
                {
                    warnedNoSnapshot = true;
                    _output.Line("（还没有遥测快照，等周期线程出数）");
                }

                if (have && t.FaultCode != FaultCode.None && t.FaultCode != FaultCode.UserStop)
                {
                    int code = ExitCodes.ForFault(t.FaultCode);
                    _output.ErrorLine("interlock", "整定观察中命中故障 " + t.FaultCode + "，停止流式输出；先 status 看「最近故障」并排除，再 fault-reset",
                        "FaultCode:" + t.FaultCode, code);
                    return CommandResult.Fail(code == ExitCodes.Ok ? ExitCodes.BusFault : code, "FaultCode:" + t.FaultCode);
                }

                // 停止信号放在出数之后判：一进观察至少能看到一行，回车在下一拍生效。
                string pushback;
                if (TryTuneStop(out pushback))
                {
                    stoppedByEnter = true;
                    if (pushback != null)
                        _pushbackLine = pushback;
                    break;
                }

                Thread.Sleep(intervalMs);
            }

            string detail = stoppedByEnter
                ? "已停止观察（回车，不急停）  观察 " + CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - begin)
                : "观察到点自动停止  " + CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - begin);
            _output.AckWith("tune", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                stoppedByEnter ? "stopped" : "elapsed",
                detail + "  state=" + StateNow(),
                e => e.Str("view", "tune").Str("loop", loop.ToString().ToLowerInvariant())
                     .Bool("stoppedByEnter", stoppedByEnter)
                     .Int("observedMs", _clock.ElapsedMilliseconds - begin));
            return CommandResult.Success();
        }

        private void WriteTuneLine(PendulumTelemetry t, long elapsedMs, TuneLoop loop, PidGains gains)
        {
            if (_options.Json)
            {
                _output.TelemetrySample(e =>
                {
                    e.Str("view", "tune").Str("loop", loop.ToString().ToLowerInvariant());
                    e.Int("elapsedMs", elapsedMs);
                    e.Int("Sequence", t.Sequence).Str("EngineState", t.EngineState.ToString());
                    e.Num("ThetaFromUprightRad", t.ThetaFromUprightRad);
                    e.Num("OmegaRadPerSec", t.OmegaRadPerSec);
                    // 频率计换算的 ω 交叉校验：字段名与主遥测信封逐字一致（只进诊断，不进控制律）。
                    e.Num("OmegaFromFreqRadPerSec", t.OmegaFromFreqRadPerSec);
                    e.Bool("FreqCrossCheckValid", t.FreqCrossCheckValid);
                    e.Num("PtermTheta", t.PtermTheta).Num("ItermTheta", t.ItermTheta).Num("DtermTheta", t.DtermTheta);
                    e.Num("ControlThetaOut", t.ControlThetaOut);
                    e.Num("PhiRad", t.PhiRad).Num("PhiRefRad", t.PhiRefRad).Num("PhiDevFromHomeRad", t.PhiDevFromHomeRad);
                    e.Num("PtermPhi", t.PtermPhi).Num("ItermPhi", t.ItermPhi).Num("DtermPhi", t.DtermPhi);
                    e.Num("ControlPhiOut", t.ControlPhiOut);
                    e.Int("SaturatedCycles", t.SaturatedCycles).Bool("IsSaturated", t.IsSaturated);
                    e.Int("FollowingErrorCount", t.FollowingErrorCount);
                    e.Num("kp", gains.Kp).Num("kd", gains.Kd).Num("kpPhi", gains.KpPhi).Num("kdPhi", gains.KdPhi);
                });
                return;
            }
            if (_options.Quiet) return;
            _output.Line(TuneRow(t, elapsedMs, loop));
        }

        /// <summary>
        /// 一行表头：先列真正生效的限幅 / 内环增益，历史增益单独标注不参与平衡；
        /// 再给各列含义与停止约定。
        /// </summary>
        private static string TuneHeader(PidGains g)
        {
            return "整定视图  生效: AccMax=" + F1(g.AccMax) + " rad/s²  ArmRateMax=" + F2(g.ArmRateMax) + " rad/s" +
                   "  φ内环 Kp=" + F3(g.KpPhi) + " Ki=" + F3(g.KiPhi) + " Kd=" + F3(g.KdPhi) +
                   "  TdLp=" + F1(g.TdLpMs) + " ms" +
                   " | 历史·不参与平衡 Ka=" + F3(g.Ka) + " Kad=" + F3(g.Kad) + " Kai=" + F3(g.Kai) +
                   " | 列: " + TuneLegend() +
                   " | 回车=停止观察(不急停) Ctrl+C=急停";
        }

        /// <summary>各列含义（tune 表头与 help 共用，一处定义）。</summary>
        internal static string TuneLegend()
        {
            return "t=观察秒  eθ=偏离直立角(°)  ω=摆杆角速度(rad/s)  Pθ/Iθ/Dθ=θ环三项(rad)  uθ=θ环输出(rad)" +
                   "  φ=摆臂 0x6064(counts)  φref=摆臂目标(counts)  dev=跟随误差(counts)  Pφ/Iφ/Dφ=φ环三项(rad)  uφ=φ环输出(rad)" +
                   "  sat=输出饱和拍数  miss=跟随误差计数";
        }

        /// <summary>整定行。角度成度，增益/输出用 rad 原值，列名固定。</summary>
        private static string TuneRow(PendulumTelemetry t, long elapsedMs, TuneLoop loop)
        {
            var sb = new System.Text.StringBuilder(220);
            sb.Append("t=").Append((elapsedMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)).Append('s');

            if (loop != TuneLoop.Phi)
            {
                sb.Append(" eθ=").Append(Deg1(t.ThetaFromUprightRad))
                  .Append(" ω=").Append(Rad2(t.OmegaRadPerSec))
                  .Append(" | Pθ=").Append(Rad3(t.PtermTheta))
                  .Append(" Iθ=").Append(Rad3(t.ItermTheta))
                  .Append(" Dθ=").Append(Rad3(t.DtermTheta))
                  .Append(" uθ=").Append(Rad3(t.ControlThetaOut));
            }

            if (loop != TuneLoop.Theta)
            {
                sb.Append(" | φ=").Append(t.ArmPositionCount.ToString(CultureInfo.InvariantCulture))
                  .Append(" φref=").Append(t.ArmTargetCount.ToString(CultureInfo.InvariantCulture))
                  .Append(" dev=").Append(t.FollowingErrorCount.ToString(CultureInfo.InvariantCulture))
                  .Append(" | Pφ=").Append(Rad3(t.PtermPhi))
                  .Append(" Iφ=").Append(Rad3(t.ItermPhi))
                  .Append(" Dφ=").Append(Rad3(t.DtermPhi))
                  .Append(" uφ=").Append(Rad3(t.ControlPhiOut));
            }

            sb.Append(" | sat=").Append(t.SaturatedCycles.ToString(CultureInfo.InvariantCulture))
              .Append(" miss=").Append(t.FollowingErrorCount.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ---------- 回车 = 停止观察 ----------

        /// <summary>
        /// 观察停止信号。REPL 里从输入队列取（取到非空行就带回提示符队列继续执行），
        /// 一次性交互会话里起一个一次性控制台读行线程。脚本 / 管道没有回车可听，只靠 --seconds 与 Ctrl+C。
        /// </summary>
        private bool TryTuneStop(out string pushback)
        {
            pushback = null;
            if (_pendingInput != null)
            {
                string line;
                if (!_pendingInput.TryTake(out line, 0)) return false;
                pushback = string.IsNullOrWhiteSpace(line) ? null : line;
                return true;
            }
            if (Interlocked.Exchange(ref _tuneStopFired, 0) != 0)
            {
                pushback = _tuneStopLine;
                _tuneStopLine = null;
                return true;
            }
            return false;
        }

        /// <summary>一次性会话才起控制台读行线程；REPL 已有 stdin 读线程，脚本模式没有控制台输入。</summary>
        private void StartTuneConsoleStopReader()
        {
            if (_pendingInput != null) return;
            if (Console.IsInputRedirected) return;
            var thread = new Thread(() =>
            {
                try
                {
                    string line = Console.ReadLine();
                    _tuneStopLine = line;
                    Volatile.Write(ref _tuneStopFired, 1);
                }
                catch (IOException ex)
                {
                    Debug.WriteLine("tune 回车停止读线程中断（IOException）：" + ex.Message + "；用 --seconds 或 Ctrl+C 停止观察。");
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine("tune 回车停止读线程中断（InvalidOperationException）：" + ex.Message + "；用 --seconds 或 Ctrl+C 停止观察。");
                }
            }) { IsBackground = true, Name = "furuta-cli-tune-stop" };
            thread.Start();
        }

        // ---------- setpid：增益对照与相对调整 ----------

        /// <summary>
        /// 取值带符号（+ / - 开头）= 相对现值增减，不带符号 = 绝对设置。
        /// 限幅 / 滤波类旗标（td-lp-ms / out-* / max-i / deadband）一律绝对设置，负号就是取值本身。
        /// </summary>
        private static bool TryRelativeValue(string raw, double current, out double next, out bool relative)
        {
            next = 0;
            relative = false;
            if (string.IsNullOrEmpty(raw)) return false;
            string text = raw.Trim();
            if (text.Length == 0) return false;
            relative = text[0] == '+' || text[0] == '-';
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out next)) return false;
            if (relative) next = current + next;
            return true;
        }

        /// <summary>
        /// 套用一个增益旗标：没给就保持现值；给了带符号的值 = 相对现值增减，不带符号 = 绝对设置。
        /// 返回 false = 取值解析不出数值，调用方按参数错拒收。deltas 记 old→new 对照行。
        /// </summary>
        private static bool ApplyGainFlag(ParsedCommand command, string flag, ref double field, List<string> deltas)
        {
            string raw = command.Value(flag);
            if (raw == null) return true;
            double next;
            bool relative;
            if (!TryRelativeValue(raw, field, out next, out relative)) return false;
            deltas.Add(GainDeltaLine(flag, field, next, relative));
            field = next;
            return true;
        }

        /// <summary>
        /// 无参数 setpid 的全表（含单位与 IsValid 状态）。
        /// 先列真正生效的限幅 / 内环 / 输出项，历史增益单独一段并标明不参与平衡。
        /// </summary>
        private static string PidGainTable(PidGains g)
        {
            string reason;
            bool valid = g.IsValid(out reason);
            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine("平衡级联 PID 当前增益（IsValid=" + (valid ? "通过" : "不通过") +
                          (valid ? string.Empty : "：" + reason) + "）");
            sb.AppendLine("  生效·限幅   AccMax=" + F1(g.AccMax) + " [rad/s²]   ArmRateMax=" + F2(g.ArmRateMax) + " [rad/s]");
            sb.AppendLine("  生效·内环 φ（摆臂位置修正环，eφ=φref-φ）  Kp=" + F3(g.KpPhi) + " [rad/rad]" +
                          "  Ki=" + F3(g.KiPhi) + " [1/s]  Kd=" + F3(g.KdPhi) + " [s]");
            sb.AppendLine("  生效·两环共用   TdLp=" + F1(g.TdLpMs) + " [ms]  输出限幅=[" + F2(g.OutMin) + "," + F2(g.OutMax) + "] [rad]" +
                          "  输出限速=" + F2(g.OutRateLimit) + " [rad/s]  积分限幅=" + F2(g.MaxIntegral) + " [rad]" +
                          "  死区=" + F2(g.DeadBand) + " [rad]");
            sb.AppendLine("  历史·不参与平衡   Ka=" + F3(g.Ka) + " [rad/s²/rad]" +
                          "  Kad=" + F3(g.Kad) + " [rad/s²/(rad/s)]  Kai=" + F3(g.Kai) + " [rad/s²/(rad·s)]" +
                          "（只随参数文件往返，调了不影响平衡）");
            sb.AppendLine("  generation=" + g.Generation.ToString(CultureInfo.InvariantCulture) +
                          "  相对调整: setpid --kp-phi +0.1（带符号=相对增减，不带=绝对设置；限幅 / 滤波类旗标一律绝对设置；" +
                          "泵加速度幅在 setswingup --pump-amp，单位 rad/s² 不变）");
            return sb.ToString().TrimEnd();
        }

        /// <summary>old→new 对照行（只列本次给过的旗标）。</summary>
        private static string GainDeltaLine(string flag, double before, double after, bool relative)
        {
            return "  --" + flag.PadRight(9) + F3(before) + " → " + F3(after) +
                   "   (Δ" + (after - before >= 0 ? "+" : string.Empty) + F3(after - before) +
                   (relative ? "，相对" : "，绝对") + ")";
        }

        private static string Rad3(double v)
        {
            return v.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }

        private static string Rad2(double v)
        {
            return v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        }

        private static string Deg1(double rad)
        {
            return (rad * 180.0 / Math.PI).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "°";
        }
    }
}
