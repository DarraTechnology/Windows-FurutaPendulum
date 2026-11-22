using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>23 条命令的实现。每条 = 调 PendulumEngine 一个方法 + 格式化输出，不含控制算法与安全判定。</summary>
    internal sealed partial class CliSession
    {
        /// <summary>零点校准前摆杆须在自然下垂位的容差 [rad]。</summary>
        private const double ZERO_PENDULUM_TOLERANCE_RAD = 0.2;

        /// <summary>最近一次 AutoSaveCalibration 的标定说明（0°/360° 平移结果等）；无变化 = null。</summary>
        private string _lastCalibrationNote;

        /// <summary>最近一次 AutoSaveCalibration 是否把 360° 点随 0° 点平移后保持了成对。</summary>
        private bool _lastPendulum360Rebased;

        private double MotionElapsedMs()
        {
            long start = Interlocked.Read(ref _motionStartTicks);
            return start < 0 ? 0 : _clock.ElapsedMilliseconds - start;
        }

        // ---------- 1. status ----------

        private CommandResult CmdStatus(ParsedCommand command, long started)
        {
            PendulumTelemetry t = new PendulumTelemetry();
            bool haveTelemetry = TrySnapshot(out t);

            if (_options.Json)
            {
                SwingUpParams swing = _engine == null ? SwingUpParams.CreateDefault() : _engine.SwingUpParams;
                _output.AckWith("status", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "status", "state=" + StateNow(), e =>
                    {
                        WriteTelemetryFields(e, t, haveTelemetry);
                        WriteSwingFields(e, swing);
                        WriteArmCalibrationFields(e);
                        WriteParamFileFields(e);
                        WriteResourceIndicatorFields(e);
                    });
            }
            else
            {
                _output.Line(BuildStatusTable(t, haveTelemetry));
            }
            _output.WriteLogRecord("status", "state=" + StateNow(), "status", true, 0, "ok");

            if (command.Has("watch"))
                return RunWatchLoop(command, 1.0, -1, started);
            return CommandResult.Success();
        }

        private string BuildStatusTable(PendulumTelemetry t, bool haveTelemetry)
        {
            EngineState state = _engine == null ? EngineState.Disconnected : _engine.State;
            PidGains pid = _engine == null ? new PidGains() : _engine.BalancePid;
            SwingUpParams swing = _engine == null ? SwingUpParams.CreateDefault() : _engine.SwingUpParams;
            SafetyLimits limits = _engine == null ? new SafetyLimits() : _engine.SafetyLimits;
            EngineDiagnostics diag = _engine == null ? new EngineDiagnostics() : _engine.Diagnostics;
            // 故障码 / 急停锁存以引擎实时字段为准；遥测里的值只代表那一帧，不能在无快照时冒充当前。
            FaultCode liveFault = _engine == null ? t.FaultCode : _engine.FaultCodeNow;
            bool liveLatch = _engine == null ? t.EStopLatch : _engine.IsEStopLatched;

            var rows = new List<KeyValuePair<string, string>>(24);
            string busName = _engine == null ? "None" : _engine.BusStateName;
            rows.Add(new KeyValuePair<string, string>("状态", state.ToString() + "  " + StateLabel(state)));
            rows.Add(new KeyValuePair<string, string>("总线", busName + " (WKC " + t.ActualWkc.ToString(CultureInfo.InvariantCulture) + "/" + t.ExpectedWkc.ToString(CultureInfo.InvariantCulture) + ")"));
            double telemetryAge = _engine == null ? -1 : _engine.TelemetryAgeSeconds;
            string telemetryFreshness = !haveTelemetry || double.IsPositiveInfinity(telemetryAge)
                ? "无本次会话遥测（未连接 / 未起周期）"
                : (telemetryAge <= 0.5 ? "新鲜（" : "陈旧（") + F2(telemetryAge) + " s 前）";
            rows.Add(new KeyValuePair<string, string>("遥测", telemetryFreshness));
            rows.Add(new KeyValuePair<string, string>("驱动控制字",
                "0x6040=0x" + t.ControlWord.ToString("X4", CultureInfo.InvariantCulture) +
                "  0x6041=0x" + t.StatusWord.ToString("X4", CultureInfo.InvariantCulture) +
                "  FaultCode=" + liveFault + "  EStopLatch=" + (liveLatch ? "是" : "否")));
            rows.Add(new KeyValuePair<string, string>("进 OP 方式",
                _engine == null ? "—" : _engine.OpEntryText));
            rows.Add(new KeyValuePair<string, string>("摆杆 θ", CliOutput.Deg360(t.ThetaRad) + "  (偏离直立 " + Signed(t.ThetaFromUprightRad) + " rad)  " + PendulumHoldTargetText()));
            rows.Add(new KeyValuePair<string, string>("摆杆角速度 ω", t.OmegaRadPerSec.ToString("0.000", CultureInfo.InvariantCulture) + " rad/s"));
            rows.Add(new KeyValuePair<string, string>("摆杆 ω 交叉校验",
                (t.FreqCrossCheckValid
                     ? t.OmegaFromFreqRadPerSec.ToString("0.000", CultureInfo.InvariantCulture) + " rad/s"
                     : "不可用（GC1502 频率计才有 FreqCh1；GC1602 无频率计）") +
                "  (GC1502 频率计平均量，只做 ω 交叉校验，不进控制律、不能当 dθ/dt)"));
            rows.Add(new KeyValuePair<string, string>("摆臂 0x6064",
                t.ArmPositionCount.ToString("N0", CultureInfo.InvariantCulture) + " counts  (目标 " +
                t.ArmTargetCount.ToString("N0", CultureInfo.InvariantCulture) + "，跟随误差 " +
                t.FollowingErrorCount.ToString("N0", CultureInfo.InvariantCulture) + " counts)"));
            rows.Add(new KeyValuePair<string, string>("摆臂角速度 φ̇", t.PhiDotRadPerSec.ToString("0.000", CultureInfo.InvariantCulture) + " rad/s"));
            rows.Add(new KeyValuePair<string, string>("摆臂 0 点",
                (_engine == null ? "—" : _engine.ArmZeroCount.ToString("N0", CultureInfo.InvariantCulture)) +
                " counts（坐标 0：使能自动归零 / 「设置 0 点」；重标定用 zero arm；改零点不搬限位）"));
            rows.Add(new KeyValuePair<string, string>("控制输出",
                "uθ=" + Rad(t.ControlThetaOut) + " rad  uφ=" + Rad(t.ControlPhiOut) + " rad  饱和=" + (t.IsSaturated ? "是" : "否")));
            rows.Add(new KeyValuePair<string, string>("平衡 PID", PidSummary(pid)));
            rows.Add(new KeyValuePair<string, string>("摆杆每转计数",
                CprText()));
            rows.Add(new KeyValuePair<string, string>("GCAN 计数切片",
                GcanCounterText(t, diag)));
            rows.Add(new KeyValuePair<string, string>("摆臂标定",
                ArmCprText()));
            rows.Add(new KeyValuePair<string, string>("起摆参数",
                "pumpAmp=" + F2(swing.PumpAmplitude) + " rad/s²  pumpHz=" + F2(swing.PumpFrequencyHz) +
                "  ke=" + F3(swing.Ke) +
                "  extract=" + F3(swing.EnergyExtractRatio) + "  extractGain=" + F3(swing.ExtractGain) +
                "  centerBand=" + F2(swing.CenterBandRad) + " rad" +
                "  timeoutMs=" + swing.TimeoutMs.ToString(CultureInfo.InvariantCulture) +
                "  全自动甩摆(无需人工扶摆)  到位判据 " + SwitchCriterionText(swing)));
            rows.Add(new KeyValuePair<string, string>("捕获交棒",
                "prepApproach=" + F2(swing.CapturePrepareApproachRad) + " rad  prepMargin=" + F3(swing.CapturePrepareEnergyMarginRatio) +
                "  commitArmRate=" + F2(swing.CaptureCommitArmRateRadPerSec) + " rad/s" +
                "  commitOmega=" + F2(swing.CaptureCommitOmegaRadPerSec) + " rad/s" +
                "  commitHold=" +
                swing.CaptureCommitHoldCycles.ToString(CultureInfo.InvariantCulture) + " 拍  holdDecay=" +
                swing.SwitchHoldDecayPerCycle.ToString(CultureInfo.InvariantCulture) + " 千分之一拍/拍（" +
                (swing.SwitchHoldDecayPerCycle * 0.001).ToString("0.000", CultureInfo.InvariantCulture) + " 拍/拍）" +
                "  (臂速 / ω 上限必须 ≤ 硬捕获门；起摆中锁定)"));
            rows.Add(new KeyValuePair<string, string>("安全阈值",
                "θ_trip=" + F2(limits.ThetaTripRad) + " rad  θ_lost=" + F2(limits.ThetaLostRad) +
                "  φ_err=" + F2(limits.PhiErrMaxRad) +
                "  软限位=[" + limits.ArmMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                limits.ArmMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts"));
            rows.Add(new KeyValuePair<string, string>("整圈捕获", CaptureSummaryText(t)));
            rows.Add(new KeyValuePair<string, string>("参数文件", ParamFileText()));
            rows.Add(new KeyValuePair<string, string>("能量 (起摆用)",
                "E=" + F3(t.EnergyJ) + " J  E*=" + F3(t.EnergyTargetJ) + " J  Δ=" + F3(t.EnergyDeltaJ) + " J"));
            rows.Add(new KeyValuePair<string, string>("实测周期",
                CycleUsText(t, diag)));
            rows.Add(new KeyValuePair<string, string>("WKC",
                "实测 " + t.ActualWkc.ToString(CultureInfo.InvariantCulture) +
                " / 期望 " + t.ExpectedWkc.ToString(CultureInfo.InvariantCulture) +
                "  (累计亏 " + diag.WcDeficit.ToString(CultureInfo.InvariantCulture) + ")"));
            rows.Add(new KeyValuePair<string, string>("PDO",
                "丢帧累计 " + diag.PdoFrameLossTotal.ToString(CultureInfo.InvariantCulture) +
                "  连续 " + diag.PdoFrameLossConsecutive.ToString(CultureInfo.InvariantCulture) +
                "  STALE 连续 " + diag.PdoStaleCycles.ToString(CultureInfo.InvariantCulture) +
                " 累计 " + diag.PdoStaleTotal.ToString(CultureInfo.InvariantCulture) +
                (diag.PdoStaleCycles > 0 ? "（降级中：沿用上一拍过程数据）" : "")));
            rows.Add(new KeyValuePair<string, string>("最近故障",
                (liveFault == FaultCode.None ? "无 (None)" : liveFault.ToString())));
            rows.Add(new KeyValuePair<string, string>("急停锁存", liveLatch ? "是" : "否"));
            rows.Add(new KeyValuePair<string, string>("日志丢行",
                (_output.Log != null ? _output.Log.DroppedCount : 0).ToString(CultureInfo.InvariantCulture)));
            rows.Add(new KeyValuePair<string, string>("遥测丢行", _output.TelemetryDropped.ToString(CultureInfo.InvariantCulture)));
            rows.Add(new KeyValuePair<string, string>("资源指示", ResourceIndicator()));

            var text = new System.Text.StringBuilder(2048);
            text.Append("项目".PadRight(22)).Append("  值").Append(Environment.NewLine);
            text.Append(new string('-', 22)).Append("  ").Append(new string('-', 44)).Append(Environment.NewLine);
            for (int i = 0; i < rows.Count; i++)
                text.Append(rows[i].Key.PadRight(PadWidth(rows[i].Key, 22))).Append("  ").Append(rows[i].Value).Append(Environment.NewLine);
            if (!haveTelemetry)
                text.Append("（尚无遥测快照，未连接或未起周期）").Append(Environment.NewLine);
            return text.ToString();
        }

        /// <summary>
        /// 「参数文件」一行：路径 + 实际路径回退说明 + 本次读默认文件的结局。
        /// 整份拒载时必须写清「已回落默认值」和中文原因（可截断，但必须指向日志）——
        /// 只显示一个路径会让人以为文件里的参数已经生效（存量 theta_switch_rad=0.28 就是实例）。
        /// </summary>
        private string ParamFileText()
        {
            string text = ParamStore.DefaultPath;
            if (!string.IsNullOrEmpty(ParamStore.EffectivePathNote))
                text += "  ⇒ " + ParamStore.EffectivePathNote;

            ParamStore.ParamFileLoadReport report = ParamStore.LastDefaultFileLoad;
            if (report == null)
                return text;

            switch (report.Outcome)
            {
                case ParamStore.ParamFileLoadOutcome.Loaded:
                    // 「读到」不等于「用上」：CLI 启动的自动读取只恢复构造期摆臂标定
                    // （calibration.arm_gear_ratio / calibration.arm_counts_per_rev），
                    // PID / 起摆 / 安全阈值仍要 --config 显式载入。状态行必须把这件事说清楚，
                    // 否则用户会以为文件里的整定值已经在跑。
                    return text + (string.IsNullOrEmpty(report.Note)
                        ? "（已读取并校验通过，启动只恢复摆臂标定（减速比 / 摆臂每转计数）；PID / 起摆 / 安全阈值用 --config 显式载入）"
                        : "（已读取并校验通过，启动只恢复摆臂标定（减速比 / 摆臂每转计数）；部分段已回退：" + BriefParamLoadText(report.Note) + "）");
                case ParamStore.ParamFileLoadOutcome.Missing:
                    return text + "（文件不存在，按控制层默认值启动）";
                default:
                    return text + "（整份拒载，已回落控制层默认值：" + BriefParamLoadText(report.Error) +
                           "；完整原因见日志 " + ParamLogPathText() + "）";
            }
        }

        /// <summary>
        /// status 的 --json 信封补参数文件结局（与人读表格的「参数文件」行同源）：
        /// 整份拒载时 paramFileOutcome=rejected 并带中文原因，不让人只看一个路径。
        /// </summary>
        private void WriteParamFileFields(JsonEnvelope e)
        {
            e.Str("paramFile", ParamStore.DefaultPath);
            ParamStore.ParamFileLoadReport report = ParamStore.LastDefaultFileLoad;
            if (report == null)
            {
                e.Str("paramFileOutcome", "not_read");
                return;
            }
            e.Str("paramFileOutcome", ParamFileOutcomeToken(report.Outcome));
            if (!string.IsNullOrEmpty(report.Error)) e.Str("paramFileError", report.Error);
            if (!string.IsNullOrEmpty(report.Note)) e.Str("paramFileNote", report.Note);
        }

        private static string ParamFileOutcomeToken(ParamStore.ParamFileLoadOutcome outcome)
        {
            switch (outcome)
            {
                case ParamStore.ParamFileLoadOutcome.Loaded: return "loaded";
                case ParamStore.ParamFileLoadOutcome.Missing: return "missing";
                default: return "rejected";
            }
        }

        /// <summary>长原因压成 status 行能读的一段（到第一个分句 + 上限）；完整原文仍在日志里。</summary>
        private static string BriefParamLoadText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "加载失败但调用方未返回原因：检查参数文件内容与路径权限，详见会话日志";
            int cut = text.Length;
            int clause = text.IndexOf('；');
            if (clause > 0 && clause < cut) cut = clause;
            const int MaxChars = 96;
            if (cut > MaxChars) cut = MaxChars;
            string brief = text.Substring(0, cut);
            return cut < text.Length ? brief + "…" : brief;
        }

        /// <summary>status 行指向的日志落点；日志没开成时给目录，绝不空着。</summary>
        private string ParamLogPathText()
        {
            if (_output.Log == null)
                return "(日志未启用)";
            return string.IsNullOrEmpty(_output.Log.CurrentFilePath)
                ? _output.Log.DirectoryPath
                : _output.Log.CurrentFilePath;
        }

        /// <summary>
        /// 整圈捕获的一行状态：等待抬升 / 记录中 / 未进行；记录中带累计角与关键点数，
        /// 并把「摆杆读数距最近一次变化多久」一起给出 —— CH1 冻结时这一行就能直接看出问题。
        /// </summary>
        private string CaptureSummaryText(PendulumTelemetry t)
        {
            string ch1;
            if (_engine == null)
            {
                ch1 = "CH1 无引擎";
            }
            else
            {
                double age = _engine.PendulumFeedbackAgeSeconds;
                if (double.IsInfinity(age))
                    ch1 = "CH1 从未见变化（未连接 / 未使能）";
                else if (_engine.IsPendulumFeedbackLive)
                    ch1 = "CH1 变化距今 " + F2(age) + " s";
                else
                    ch1 = "CH1 连续 " + F2(age) + " s 无变化（无有效读数）";
            }
            if (t.CaptureActive)
            {
                return "记录中：累计 " + F2(t.CaptureSweepDeg) + "° / 360°（最大 " + F2(t.CaptureMaxSweepDeg) +
                       "°）  关键点 " + t.CaptureKeyPoints.ToString(CultureInfo.InvariantCulture) +
                       "  " + F2(t.CaptureHoldSeconds) + " s  " + ch1;
            }
            if (t.ReleaseWaitActive)
            {
                return "等待抬升" + (t.ReleaseArmed ? "（已上膛，松手即开始计圈）" : "（把摆杆抬到顶部 180°）") + "  " + ch1;
            }
            string reason = _engine == null ? null : _engine.CaptureInvalidReason;
            return "未进行（摆杆自然下垂静止后点「捕获」）" +
                   (string.IsNullOrEmpty(reason) ? "" : "  上次失效：" + reason) + "  " + ch1;
        }

        private static int PadWidth(string text, int width)
        {
            int display = 0;
            for (int i = 0; i < text.Length; i++) display += text[i] > 0x2E80 ? 2 : 1;
            return Math.Max(1, width - display + text.Length);
        }

        private static string StateLabel(EngineState state)
        {
            switch (state)
            {
                case EngineState.Disconnected: return "[未连接]";
                case EngineState.Connected: return "[已连接]";
                case EngineState.Enabled: return "[已使能]";
                case EngineState.Swinging: return "[起摆中]";
                case EngineState.Balancing: return "[平衡中]";
                case EngineState.Stopped: return "[已停]";
                case EngineState.Fault: return "[故障]";
                default: return string.Empty;
            }
        }

        private static string CycleUsText(PendulumTelemetry t, EngineDiagnostics diag)
        {
            double us = t.CycleDeltaTicks * 1000000.0 / Stopwatch.Frequency;
            return us.ToString("0.0", CultureInfo.InvariantCulture) + " µs  抖动 RMS=" +
                   diag.CycleJitterUs.Rms.ToString("0.0", CultureInfo.InvariantCulture) + " µs  Max=" +
                   diag.CycleJitterUs.Maximum.ToString("0.0", CultureInfo.InvariantCulture) + " µs";
        }

        // ---------- 资源真假指示（profile 定档） ----------

        /// <summary>本次会话读到的资源指示。定档只读一次并缓存：档位是启动期事实，运行中切换必须重启（PLC 同口径）。</summary>
        private volatile ResourceIndicatorReading _resourceIndicator;
        private readonly object _resourceIndicatorGate = new object();

        /// <summary>
        /// 取资源指示（首次调用读 profile.json 并缓存；回落时记一条日志，原因同时进状态行）。
        /// 只读 —— 本程序永不写 profile.json，改档唯一通道是 PLC 的 setup-profile.ps1。
        /// </summary>
        private ResourceIndicatorReading ResourceIndicatorNow()
        {
            ResourceIndicatorReading reading = _resourceIndicator;
            if (reading != null) return reading;

            lock (_resourceIndicatorGate)
            {
                if (_resourceIndicator != null) return _resourceIndicator;

                reading = ResourceProfileProbe.Read();
                _resourceIndicator = reading;
                if (!reading.Resolved)
                {
                    // 回落必须留痕：状态行只给短原因，日志给完整原因（含堆栈），售后可直接核对本机到底配没配档位。
                    // 路径已经写在原因里（未配置 / 读取失败 / 文件异常三种都带），这里不再重复一遍。
                    _output.WriteLogRecord(
                        "profile_fallback",
                        "[DEV/SIM] 回落：" + reading.Note +
                        (reading.LogDetail == null ? "" : "；明细=" + reading.LogDetail),
                        "status", true, 0, "ok");
                }
                return reading;
            }
        }

        /// <summary>
        /// 资源真假指示的显示文本：读到合法档位就直接给标签，回落则「标签 + 回落原因」。
        /// 永远显示，不许隐藏（设计稿 13.3 / 20.4）；缺文件 / 读不到 / JSON 坏 / 档位非法
        /// 一律回落 [DEV/SIM] 并明写原因，绝不冒充 [TEST/RIG] 或 [PROD]。
        /// </summary>
        private string ResourceIndicator()
        {
            ResourceIndicatorReading reading = ResourceIndicatorNow();
            return reading.Note == null ? reading.Label : reading.Label + "  回落：" + reading.Note;
        }

        /// <summary>
        /// status 的 --json 信封补资源指示（信封不写 null 键：未解析时档位字段给 unresolved，原因另给一列）。
        /// 人读表格与 JSON 两条出口都带它，避免 --json 下把真假指示藏掉。
        /// </summary>
        private void WriteResourceIndicatorFields(JsonEnvelope e)
        {
            ResourceIndicatorReading reading = ResourceIndicatorNow();
            e.Str("resourceIndicator", reading.Label)
             .Str("resourceProfile", reading.Resolved ? ResourceProfileProbe.ToToken(reading.Tier) : "unresolved");
            if (!reading.Resolved)
                e.Str("resourceProfileNote", reading.Note);
        }

        /// <summary>摆杆每转计数 CPR_PENDULUM（线数×4×传动比）。0 = 未标定，起摆会被控制层拒绝。</summary>
        private string CprText()
        {
            double cpr = _engine == null ? 0 : _engine.PendulumCountsPerRev;
            if (cpr <= 0)
                return "未标定（=0）→ SwingUp 会拒绝。用 --pendulum-cpr <线数×4×传动比> 给值（台架默认 2400 = 600 线 × 4，GC1602 AB 四倍频）";
            return cpr.ToString("0.###", CultureInfo.InvariantCulture) + "  (线数×4×传动比)";
        }

        /// <summary>
        /// GCAN 计数切片的各通道原值（CH1/CH2 计数 + GC1502 才有的两路频率计）。
        /// 全部是过程映像直读原值：计数是 encoder counts，频率单位 Hz。只展示，不参与控制律。
        /// 计数切片未识别（未连接 / 0xF050 读不到）就明说，不拿 0 冒充读数。
        /// </summary>
        private static string GcanCounterText(PendulumTelemetry t, EngineDiagnostics diag)
        {
            switch (diag.CounterSlice)
            {
                case GcanSliceKind.Gc1502Counter:
                    return "GC1502  CH1=" + t.PendulumCount.ToString(CultureInfo.InvariantCulture) + " count / " +
                           t.PendulumFreqCh1Hz.ToString(CultureInfo.InvariantCulture) + " Hz  " +
                           "CH2=" + unchecked((int)t.PendulumCountCh2).ToString(CultureInfo.InvariantCulture) + " count / " +
                           t.PendulumFreqCh2Hz.ToString(CultureInfo.InvariantCulture) + " Hz";
                case GcanSliceKind.Gc1602Counter:
                    return "GC1602  CH1=" + t.PendulumCount.ToString(CultureInfo.InvariantCulture) + " count  " +
                           "CH2=" + unchecked((int)t.PendulumCountCh2).ToString(CultureInfo.InvariantCulture) +
                           " count  (AB 正交，无频率计)";
                default:
                    return "未识别（未连接 / GCAN 0xF050 读不到计数切片）";
            }
        }

        /// <summary>
        /// 摆杆保持位（直立点）的 count 视角文本：±CPR/2 count (180°)。
        /// wrap 语义下 +CPR/2 与 −CPR/2 是同一个直立点（+π ≡ −π），从哪侧甩上算哪侧。
        /// </summary>
        private string PendulumHoldTargetText()
        {
            double cpr = _engine == null ? 0 : _engine.PendulumCountsPerRev;
            if (cpr <= 0) return "保持目标 ±(CPR/2) count (180°)，CPR 未标定";
            return "保持目标 ±" + (cpr / 2.0).ToString("0.###", CultureInfo.InvariantCulture) + " count (180°)";
        }

        /// <summary>
        /// 到位判据（起摆 → 平衡切换）的文本：粗筛三阈值 |ψ| &lt; θ_switch、|ω| &lt; ω_switch、
        /// |φ| &lt; φ_switch；两条计数通道都走**近顶端漏桶**（满足 +1 拍、不满足
        /// −SwitchHoldDecayPerCycle×0.001 拍/拍，单位千分之一拍/拍；|ψ| 超出预捕获窗口时冻结）：
        /// 慢通道净拍达到 SwitchHoldCycles 且本拍在提交带内才放行，提交通道净拍达到
        /// CaptureCommitHoldCycles 即交棒。每一拍还必须过硬捕获门（不稳定模态幅值 + 摆臂行程预算）
        /// 与提交带（臂速 + 摆杆角速度）。三条独立阈值单独判会在失稳区放行，必须叠加后几道。
        /// </summary>
        private string SwitchCriterionText(SwingUpParams swing)
        {
            double cpr = _engine == null ? 0 : _engine.PendulumCountsPerRev;
            string counts = cpr > 0
                ? "≈" + Math.Round(swing.ThetaSwitchRad * cpr / (2.0 * Math.PI)).ToString(CultureInfo.InvariantCulture) +
                  " count @" + cpr.ToString("0.###", CultureInfo.InvariantCulture)
                : "count 视角待标定 CPR";
            // 联合捕获门的 6 个字段在旧参数文件里没有（读出 0 = 未配置），控制层按内置默认补齐；
            // 状态行按同一口径显示生效值，否则会把「0 = 未配置」当成真实门槛（0 = 永远不满足）。
            SwingUpParams def = SwingUpParams.CreateDefault();
            double modeMax = EffectiveSwingValue(swing.CaptureModeAmplitudeMaxRad, def.CaptureModeAmplitudeMaxRad);
            double strokePerMode = EffectiveSwingValue(swing.CaptureStrokePerModeRad, def.CaptureStrokePerModeRad);
            double margin = EffectiveSwingValue(swing.CaptureStrokeMarginRad, def.CaptureStrokeMarginRad);
            double adverseStroke = EffectiveSwingValue(swing.CaptureAdverseRateStrokeSec, def.CaptureAdverseRateStrokeSec);
            double rateLimit = EffectiveSwingValue(swing.CaptureArmRateLimitRadPerSec, def.CaptureArmRateLimitRadPerSec);
            double pendulumRate = EffectiveSwingValue(swing.CapturePendulumRateRadPerSec, def.CapturePendulumRateRadPerSec);
            int holdDecay = swing.SwitchHoldDecayPerCycle > 0 ? swing.SwitchHoldDecayPerCycle : def.SwitchHoldDecayPerCycle;
            int commitHold = swing.CaptureCommitHoldCycles > 0 ? swing.CaptureCommitHoldCycles : def.CaptureCommitHoldCycles;
            return "粗筛 |ψ| < " + F2(swing.ThetaSwitchRad) + " rad (" + counts + ") 且 |ω| < " +
                   F2(swing.OmegaSwitchRadPerSec) + " rad/s 且 |φ| < " + F2(swing.PhiSwitchRad) + " rad；" +
                   "慢通道净拍 ≥ " + swing.SwitchHoldCycles.ToString(CultureInfo.InvariantCulture) +
                   "（近顶端漏桶：满足 +1 拍 / 不满足 −" + holdDecay.ToString(CultureInfo.InvariantCulture) +
                   "×0.001 拍/拍，远离顶端冻结）且本拍在提交带内（|φ̇| ≤ " + F2(swing.CaptureCommitArmRateRadPerSec) + " rad/s 且 |ω| ≤ " +
                   F2(swing.CaptureCommitOmegaRadPerSec) + " rad/s），或提交通道净拍 ≥ " +
                   commitHold.ToString(CultureInfo.InvariantCulture) + " 即交棒；" +
                   "逐拍硬门 |u=ψ+ω/" + F2(pendulumRate) + "| ≤ " + F2(modeMax) +
                   " rad 且 |φ̇| ≤ " + F2(rateLimit) + " rad/s 且需求行程 " + F2(strokePerMode) + "·|u|+" +
                   F2(adverseStroke) + "·|φ̇|+" + F2(margin) + " rad ≤ 剩余行程";
        }

        /// <summary>
        /// 起摆参数的生效值：&gt;0 且有限就用它，否则按控制层同一口径回落内置默认
        /// （旧参数文件缺 Capture* 键时读出来是 0 = 未配置）。</summary>
        private static double EffectiveSwingValue(double value, double fallback)
        {
            return value > 0 && ControlMath.IsFinite(value) ? value : fallback;
        }

        // ---------- 摆臂标定链（01 §11.4）：显式值 = 权威 / 0 → 0x2102×减速比 推导 / 0x608F 仅交叉校验 ----------

        /// <summary>摆臂标定来源。--json 的 armCalibrationSource 就是它的枚举名。</summary>
        private enum ArmCalibrationSource
        {
            /// <summary>`--arm-cpr` 显式给值，它就是本次的启动标定。</summary>
            Explicit,
            /// <summary>命令行没给：入口从参数文件 calibration.arm_counts_per_rev 恢复的标定（使能 / 起摆时 0x2102 实读仍优先）。</summary>
            PersistedFile,
            /// <summary>没给显式值：走 EC1 标定链 0x2102（开闭环细分设置）实读 × 减速比，控制层在使能 / 起摆时定标。</summary>
            Derived0x2102,
            /// <summary>显式值与推导都拿不到（没连总线就读不到 0x2102），未标定。</summary>
            Uncalibrated
        }

        /// <summary>
        /// 摆臂减速比 = 电机转数 / 摆臂转数。以控制层构造期生效值为准（入口可能从参数文件恢复），
        /// 没有引擎时退回命令行值（不给 = 1.0）。
        /// </summary>
        internal double ArmGearRatioValue()
        {
            if (_engine != null)
                return _engine.ArmGearRatio;
            return _options.ArmGearRatio.HasValue ? _options.ArmGearRatio.Value : 1.0;
        }

        /// <summary>减速比显示文本（至少一位小数：1 → 1.0，2.5 → 2.5）。显示格式统一走这里。</summary>
        internal static string ArmGearText(double gear)
        {
            return gear.ToString("0.0##", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 摆臂标定来源：<see cref="ArmCalibrationSource.Explicit"/> = `--arm-cpr` 显式给值；
        /// 命令行没给但控制层里有值 = 入口从参数文件 <c>calibration.arm_counts_per_rev</c> 恢复的标定；
        /// 都没有就走 0x2102 × 减速比 推导（0x2102 由控制层在使能 / 起摆时实读，CLI 不猜数值）；
        /// 还没连总线 = 0x2102 也读不到，未标定。
        /// </summary>
        private ArmCalibrationSource ArmCalibrationSourceNow()
        {
            int configuredCpr = _engine == null ? 0 : _engine.ArmCountsPerRev;
            if (configuredCpr > 0)
            {
                // 同一个数有两种来路：命令行旗标 / 参数文件恢复。显示与 --json 必须说清是哪一种。
                bool cliExplicit = _options != null && _options.ArmCpr.HasValue;
                return cliExplicit ? ArmCalibrationSource.Explicit : ArmCalibrationSource.PersistedFile;
            }
            EngineState state = _engine == null ? EngineState.Disconnected : _engine.State;
            return state == EngineState.Disconnected ? ArmCalibrationSource.Uncalibrated : ArmCalibrationSource.Derived0x2102;
        }

        /// <summary>
        /// 摆臂标定实值（控制层定标结果，与 <see cref="PendulumEngine.ResolvedArmCountsPerRev"/> 同义）：
        /// 显式值或 0x2102×减速比的推导结果，未定标 = 0。人读行与 --json 的 resolvedArmCountsPerRev 都取它。
        /// </summary>
        private int ArmCprResolved()
        {
            return _engine == null ? 0 : _engine.ResolvedArmCountsPerRev;
        }

        /// <summary>
        /// 摆臂每转计数 CPR_ARM（= 电机每转计数 × 减速比）的标定链一行。命令行显式值与参数文件恢复值都给数
        /// （并标明来路）；推导分支给控制层定标出来的实值（CLI 侧没有 OD 读口，不猜这个数），还没定标就明说；
        /// 三条都拿不到 = 未标定。
        /// </summary>
        private string ArmCprText()
        {
            string gearText = ArmGearText(ArmGearRatioValue());
            switch (ArmCalibrationSourceNow())
            {
                case ArmCalibrationSource.Explicit:
                    return _engine.ArmCountsPerRev.ToString(CultureInfo.InvariantCulture) +
                        " count/rev  (来源: --arm-cpr 显式)";
                case ArmCalibrationSource.PersistedFile:
                    return _engine.ArmCountsPerRev.ToString(CultureInfo.InvariantCulture) +
                        " count/rev  (来源: 参数文件 calibration.arm_counts_per_rev；使能 / 起摆时 0x2102 实读仍优先)";
                case ArmCalibrationSource.Derived0x2102:
                {
                    int resolved = ArmCprResolved();
                    return resolved > 0
                        ? resolved.ToString(CultureInfo.InvariantCulture) +
                          " count/rev  (来源: 0x2102×减速比 " + gearText + ")"
                        : "尚未定标  (来源: 0x2102×减速比 " + gearText + "，使能 / 起摆时实读 0x2102 定标)";
                }
                default:
                    return "未标定, 起摆将被拒（不给 --arm-cpr 时按 0x2102×减速比 " + gearText + " 推导，连上总线后使能 / 起摆时定标）";
            }
        }

        /// <summary>
        /// status 的 --json 信封补摆臂标定四件套（信封不写 null 键：未定标时 resolvedArmCountsPerRev 给 0）。
        /// armCountsPerRev = 控制层当前配置的兜底值（命令行显式值或参数文件恢复值；纯 0x2102 推导分支给 0）。
        /// </summary>
        private void WriteArmCalibrationFields(JsonEnvelope e)
        {
            ArmCalibrationSource source = ArmCalibrationSourceNow();
            bool configured = source == ArmCalibrationSource.Explicit || source == ArmCalibrationSource.PersistedFile;
            e.Int("armCountsPerRev", configured ? _engine.ArmCountsPerRev : 0)
             .Num("armGearRatio", ArmGearRatioValue())
             .Str("armCalibrationSource", source.ToString())
             .Int("resolvedArmCountsPerRev", ArmCprResolved());
        }

        // ---------- 2. connect ----------


        /// <summary>
        /// 连接后实际选中的摆臂从站 0 基下标（真解析结果）。未连接 / 未解析 = -1。
        /// 回执必须报它，不能报请求值 ArmSlaveIndex —— 自动识别时请求值是 -1、两者不等。
        /// </summary>
        private int ArmSlaveResolved()
        {
            return _engine == null ? -1 : _engine.ResolvedArmSlaveIndex;
        }

        /// <summary>摆臂从站的回显文本，用 1 基序号（与 --arm-slave、status 的 "Slave n" 同口径）。</summary>
        private string ArmSlaveText()
        {
            int resolved = ArmSlaveResolved();
            return resolved < 0
                ? "摆臂=未解析"
                : "摆臂=从站#" + (resolved + 1).ToString(CultureInfo.InvariantCulture);
        }

        private CommandResult CmdConnect(ParsedCommand command, StateGate gate, long started)
        {
            if (gate == StateGate.Idempotent)
            {
                _output.AckWith("connect", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "already connected", "bus=" + BusStateUpper() + "  wkc=" + WkcText() + "  " + ArmSlaveText() + "  (幂等跳过)",
                    e => e.Str("bus", BusStateUpper()).Str("idempotent", "true")
                         .Int("resolvedArmSlaveIndex", ArmSlaveResolved()));
                return CommandResult.Success();
            }

            string configPath = command.Value("config");
            if (!string.IsNullOrEmpty(configPath))
            {
                CommandResult loaded = ApplyConfigFile(configPath, started);
                if (!loaded.Ok) return loaded;
            }

            // Connect() 不抛异常：拒绝连接走 Faulted 事件，原因在 FaultInfo.Detail 里
            // （摆臂候选清单那类排查信息就在里面）。这里抓住本次 connect 的那一次故障、原文透传，
            // 并按状态如实收口 —— 绝不把没连上 ack 成 "connected"（事件在人读模式不打印，不补这行原因就丢了）。
            FaultInfo fault = default(FaultInfo);
            bool faulted = false;
            EventHandler<FaultInfo> catchFault = (sender, info) => { fault = info; faulted = true; };
            _engine.Faulted += catchFault;
            try
            {
                _engine.Connect();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:IdentityMismatch", ex.GetType().Name + ": " + ex.Message, "connect", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.ConnectFailed, "FaultCode:IdentityMismatch");
            }
            finally
            {
                _engine.Faulted -= catchFault;
            }

            if (_engine.State != EngineState.Connected)
            {
                int code = ExitCodes.ForFault(faulted ? fault.Code : FaultCode.None);
                if (code == ExitCodes.Ok) code = ExitCodes.ConnectFailed;
                string errorCode = faulted && fault.Code != FaultCode.None
                    ? "FaultCode:" + fault.Code
                    : "connect_failed";
                string detail = faulted && !string.IsNullOrEmpty(fault.Detail)
                    ? fault.Detail
                    : "控制层没给故障详情。";
                _output.ErrorLine("connect_failed",
                    "connect 未连上，当前 " + StateNow() + "。" + detail,
                    errorCode, code);
                return CommandResult.Fail(code, errorCode);
            }

            PendulumTelemetry t;
            bool have = TrySnapshot(out t);
            // 摆臂回执打 ResolvedArmSlaveIndex（真解析结果）而不是请求值 ArmSlaveIndex：
            // 自动识别时请求值是 -1、两者不等，报请求值就是骗人。显示用 1 基序号，与 --arm-slave 同口径。
            _output.AckWith("connect", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "connected", "bus=" + (_engine == null ? "None" : _engine.BusStateName) + "  op=" + OpEntryShort() + "  wkc=" + WkcText() + "  " + ArmSlaveText() + "  (" + CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started) + ")",
                e =>
                {
                    e.Str("bus", BusStateUpper()).Int("actualWkc", have ? t.ActualWkc : 0).Int("expectedWkc", have ? t.ExpectedWkc : 0)
                     .Str("opEntry", _engine == null ? "" : _engine.OpEntryText)
                     .Int("resolvedArmSlaveIndex", ArmSlaveResolved());
                });
            _output.WriteLogRecord("connect",
                "DENI 校验 + Build 完成，总线实测状态 " + BusStateUpper() + "（进 OP 方式见启动日志 op_entry）",
                "connect", true, 0, "ok");
            return CommandResult.Success();
        }

        /// <summary>主站实测总线状态的大写名（OP / SAFEOP / PREOP / INIT / NONE），不按引擎状态猜。</summary>
        private string BusStateUpper()
        {
            return (_engine == null ? "None" : _engine.BusStateName).ToUpperInvariant();
        }

        /// <summary>进 OP 方式的短标注：direct 是仅诊断用的非标准路径，必须让脚本和人一眼看出来。</summary>
        private string OpEntryShort()
        {
            return _engine != null && _engine.OpEntryMode == OpEntryMode.DirectFromPreOp
                ? "direct(仅诊断,非标准路径)"
                : "standard";
        }

        private string WkcText()
        {
            PendulumTelemetry t;
            if (!TrySnapshot(out t)) return "—/—";
            return t.ActualWkc.ToString(CultureInfo.InvariantCulture) + "/" + t.ExpectedWkc.ToString(CultureInfo.InvariantCulture);
        }

        // ---------- 3. disconnect ----------

        private CommandResult CmdDisconnect(ParsedCommand command, StateGate gate, long started)
        {
            if (gate == StateGate.Idempotent)
            {
                _output.AckWith("disconnect", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "already disconnected", "teardown=clean", e => e.Str("teardown", "clean"));
                return CommandResult.Success();
            }

            bool force = command.Has("force");
            if (force) _output.Warn("! warning  --force 跳过等待直接拆，仅在设备已失联时使用");

            if (!force && (_engine.State == EngineState.Swinging || _engine.State == EngineState.Balancing))
                SafeEmergencyStop("disconnect");

            int code = Teardown(false);
            if (code != ExitCodes.Ok)
            {
                _output.AckWith("disconnect", false, code, "teardown_failed", _clock.ElapsedMilliseconds - started, StateNow(),
                    "teardown failed", "teardown=failed", null);
                return CommandResult.Fail(code, "teardown_failed");
            }
            _output.AckWith("disconnect", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "disconnected", "teardown=clean  (" + CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started) + ")",
                e => e.Str("teardown", "clean"));
            return CommandResult.Success();
        }

        // ---------- 4. enable ----------

        private CommandResult CmdEnable(ParsedCommand command, StateGate gate, long started)
        {
            if (gate == StateGate.Idempotent)
            {
                _output.AckWith("enable", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "already enabled", "noMotionGate=pass  (幂等跳过)", e => e.Str("noMotionGate", "pass"));
                return CommandResult.Success();
            }

            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            // 结构化拒绝：先清掉上一条命令留下的记录，本次只认引擎在 Enable 之后新写入的
            // LastCommandRefusal，不再用诊断文本里的「未标定 / 拒绝 / 超时」关键词猜控制流。
            _engine.ClearCommandRefusal();
            try
            {
                _engine.Enable();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:EnableLost", ex.GetType().Name + ": " + ex.Message, "enable", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.DeviceFault, "FaultCode:EnableLost");
            }

            // Enable() 只把命令交给周期线程。故障复位可能先占几秒，引擎超时 5 秒，
            // 故障清除后时限会重计。这里留 12 秒，把诊断泵出来。立刻检查会把还没完成的使能判失败。
            EngineState enabledState = WaitForEnableResult(12000);
            if (enabledState != EngineState.Enabled)
            {
                string why = string.IsNullOrEmpty(_lastDiagnostic) ? "没有诊断" : _lastDiagnostic;
                // 拒绝原因只认本次 Enable 写入的结构化记录（上一条已在上方清除），
                // 文本关键词不参与判定。
                string refusal = _engine.LastCommandRefusal;
                PendulumTelemetry snap;
                bool haveSnap = TrySnapshot(out snap);
                string cw = haveSnap ? "0x" + snap.ControlWord.ToString("X4", CultureInfo.InvariantCulture) : "n/a";
                string detail = !string.IsNullOrEmpty(refusal)
                    ? "拒绝原因：" + refusal + "（控制字=" + cw + "）"
                    : "没有记录到本次使能的拒绝原因；控制字=" + cw +
                      " 说明使能命令可能未被周期线程消费（检查内核周期是否推进 / 总线是否在 OP）。最后一条诊断：" + why;
                return FailEcho(command, started, ExitCodes.DeviceFault, "FaultCode:EnableLost",
                    "使能后状态为 " + enabledState + "，期望 Enabled。" + detail);
            }

            int seconds;
            if (command.TryInt("seconds", out seconds) && seconds > 0)
            {
                CommandResult hold = HoldMotion(command, "enable", seconds, started);
                if (!hold.Ok) return hold;
                return CommandResult.Success();
            }
            if (command.Has("seconds"))
                _output.Warn("! warning  enable --seconds 0 表示不限时保持使能，靠 Ctrl+C 或联锁停机");

            _output.AckWith("enable", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "enabled", "noMotionGate=pass  (" + CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started) + ")",
                e => e.Str("noMotionGate", "pass"));
            return CommandResult.Success();
        }

        /// <summary>
        /// 等周期线程把使能跑完。等待中必须 Pump，否则诊断和状态事件出不来。
        /// 拒绝判据只看引擎的**结构化** <see cref="PendulumEngine.LastCommandRefusal"/>
        /// （调用前已清空旧记录），不匹配诊断文本 —— 文案一改就会静默失效。
        /// </summary>
        private EngineState WaitForEnableResult(int timeoutMs)
        {
            long start = _clock.ElapsedMilliseconds;
            while (_clock.ElapsedMilliseconds - start < timeoutMs)
            {
                Pump();
                EngineState state = _engine.State;
                if (state == EngineState.Enabled || state == EngineState.Fault ||
                    state == EngineState.Swinging || state == EngineState.Balancing)
                    return state;
                if (!string.IsNullOrEmpty(_engine.LastCommandRefusal))
                    return state;
                Thread.Sleep(20);
            }
            Pump();
            return _engine.State;
        }

        private CommandResult EchoConfirmFail(ParsedCommand command, long started, CommandResult confirm)
        {
            _output.AckWith(command.Name, false, confirm.Code, confirm.ErrorCode,
                _clock.ElapsedMilliseconds - started, StateNow(),
                "危险命令需要 --yes（非交互模式）", "需要 --yes", null);
            return confirm;
        }

        // ---------- 5. disable ----------

        /// <summary>
        /// 等控制层把状态落到目标集合，并取一帧**新鲜**遥测做回读证据。
        /// <paramref name="expectedControlWord"/> 给了就要求 0x6040 同时等于它（停机 / 去使能必须回读到 0）。
        /// 只用现有遥测 / 状态属性，不新增读数通道。超时返回 false，调用方按最后一次实测状态说明原因。
        /// </summary>
        private bool WaitForEngineState(long timeoutMs, out EngineState state, out PendulumTelemetry snapshot,
            int? expectedControlWord, params EngineState[] targets)
        {
            long deadline = _clock.ElapsedMilliseconds + timeoutMs;
            snapshot = new PendulumTelemetry();
            while (true)
            {
                Pump();
                state = _engine == null ? EngineState.Disconnected : _engine.State;
                bool matched = false;
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] == state) { matched = true; break; }
                }
                if (matched && TrySnapshot(out snapshot))
                {
                    double age = _engine == null ? double.PositiveInfinity : _engine.TelemetryAgeSeconds;
                    bool fresh = !double.IsPositiveInfinity(age) && age <= 0.5;
                    bool controlWordOk = !expectedControlWord.HasValue ||
                                         snapshot.ControlWord == expectedControlWord.Value;
                    if (fresh && controlWordOk) return true;
                }
                if (_clock.ElapsedMilliseconds >= deadline) return false;
                Thread.Sleep(10);
            }
        }

        private CommandResult CmdDisable(ParsedCommand command, StateGate gate, long started)
        {
            try
            {
                _engine.Disable();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:DriveFault", ex.GetType().Name + ": " + ex.Message, "disable", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.DeviceFault, "FaultCode:DriveFault");
            }
            // 去使能必须回读：状态要到 Connected，0x6040 必须为 0x0000；做不到就如实报“未确认”。
            EngineState state;
            PendulumTelemetry t;
            bool confirmed = WaitForEngineState(5000, out state, out t, 0, EngineState.Connected);
            if (!confirmed)
            {
                EngineState now = _engine == null ? EngineState.Disconnected : _engine.State;
                PendulumTelemetry last;
                bool have = TrySnapshot(out last);
                return FailEcho(command, started, ExitCodes.StateRejected, "state_rejected",
                    "去使能未在 5 s 内确认：当前状态 " + now + "，0x6040=" +
                    (have ? "0x" + last.ControlWord.ToString("X4", CultureInfo.InvariantCulture) : "无本次会话遥测") +
                    "（期望 Connected 且 0x6040=0x0000）。检查主站是否仍在 OP / 驱动器是否响应" +
                    "（status 看 BusState、0x6041）；必要时执行 stop 或按硬急停。");
            }
            if (t.ControlWord != 0)
            {
                return FailEcho(command, started, ExitCodes.StateRejected, "state_rejected",
                    "去使能已回到 Connected，但 0x6040 回读为 0x" + t.ControlWord.ToString("X4", CultureInfo.InvariantCulture) +
                    "（期望 0x0000）。控制字未清干净，先执行 stop；仍不清零则检查驱动器中是否被其它主站/软件接管。");
            }
            _output.AckWith("disable", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "disabled", "state=" + state + "  0x6040=0x0000（已回读）  WKC " + t.ActualWkc + "/" + t.ExpectedWkc +
                "  (" + CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started) + ")",
                e => e.Int("statusWord", t.StatusWord).Int("controlWord", t.ControlWord)
                     .Str("engineState", state.ToString()));
            return CommandResult.Success();
        }

        // ---------- 6. swingup ----------

        private CommandResult CmdSwingUp(ParsedCommand command, StateGate gate, long started)
        {
            if (gate == StateGate.Idempotent)
            {
                _output.AckWith("swingup", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "already swinging", "phase=Swinging", e => e.Str("phase", "Swinging"));
                return CommandResult.Success();
            }

            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            // 标定前置：CPR 未给值时控制层会拒绝起摆，这里先报配置错，省掉一轮起摆超时。
            // 真正的拒绝仍由控制层做（S12/诊断），CLI 不写第二套判定。
            if (_engine.PendulumCountsPerRev <= 0)
            {
                _output.ErrorLine("config_invalid",
                    "摆杆每转计数 CPR_PENDULUM 未标定（=0），换算不出 θ，控制层会拒绝起摆。" +
                    "用 --pendulum-cpr <线数×4×传动比> 给值（机器标定，不进 furuta_params.json）。",
                    "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            // 起摆截止只有一个数：--timeout-ms（别名 --timeout，单位 ms）给了就用它，没给就用当前起摆参数的
            // TimeoutMs（默认 30000）。这个数**同时**是控制层 S12 联锁的截止与 CLI 的等待上限 ——
            // 两边永远同值，不会出现「引擎按参数里的 30 s 自己跳 SwingUpTimeout、CLI 还等 60 s」的分裂。
            int timeoutMs;
            bool deadlineGiven = command.Has("timeout-ms") || command.Has("timeout");
            if (deadlineGiven)
            {
                string rawTimeout = command.Value("timeout-ms") ?? command.Value("timeout");
                if (!command.TryInt("timeout-ms", out timeoutMs) && !command.TryInt("timeout", out timeoutMs))
                    return BadNumber("timeout-ms", rawTimeout, started);
                if (timeoutMs <= 0)
                {
                    _output.ErrorLine("invalid_arguments",
                        "旗标 --timeout-ms 必须是正整数毫秒（本次起摆截止），收到 \"" + (rawTimeout ?? string.Empty) + "\"",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
            }
            else
            {
                timeoutMs = _engine.SwingUpParams.TimeoutMs;
                if (timeoutMs <= 0) timeoutMs = 30000;   // 起摆截止兜底，默认 30 s（S12 口径）
            }

            // 全局 --timeout（秒）是 CLI 自己的单命令看门狗，与起摆截止各管一层：它更短就先中断等待、
            // 按 FaultCode:SwingUpTimeout 收口，但**不改**控制层截止。提前说一声，免得分不清是谁到点。
            long cliWatchdogMs = CommandTimeoutBudgetMs(started);
            if (cliWatchdogMs > 0 && cliWatchdogMs < timeoutMs)
            {
                _output.Warn("--timeout 单命令执行上限 " + cliWatchdogMs.ToString(CultureInfo.InvariantCulture) +
                    " ms 短于本次起摆截止 " + timeoutMs.ToString(CultureInfo.InvariantCulture) +
                    " ms：到点由 CLI 中断等待收口，控制层截止不变。");
            }

            // 结构化拒绝：TrySwingUp 直接返回「是否受理 + 真实原因」。不再用诊断文本里的
            // 「未标定 / 不能起摆」等关键词判断控制流 —— 提示性诊断（例如耦合比 r/l 未标定）
            // 从来不是拒绝理由，字符串匹配会把它误当成失败。先清旧记录，排队后的拒绝只看本次。
            _engine.ClearCommandRefusal();
            try
            {
                // 命令行给了截止 = 本次起摆的截止：克隆当前起摆参数只改 TimeoutMs，走既有 SetSwingUpParams 的
                // generation 双缓冲提交路径（帧首 SwapIfDirty 先于 pending 命令顶换，见 PendulumEngine），
                // 再请求起摆。不绕过它直接写 _swingActive，也不新增第二套超时变量。
                if (deadlineGiven && _engine.SwingUpParams.TimeoutMs != timeoutMs)
                {
                    SwingUpParams swing = _engine.SwingUpParams.Clone();
                    swing.TimeoutMs = timeoutMs;
                    _engine.SetSwingUpParams(swing);
                }
                string refusal;
                if (!_engine.TrySwingUp(out refusal))
                {
                    string reasonText = string.IsNullOrEmpty(refusal) ? "控制层未返回原因" : refusal;
                    return FailEcho(command, started, ExitCodes.StateRejected, "state_rejected",
                        "起摆被拒绝：" + reasonText);
                }
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:SwingUpTimeout", ex.GetType().Name + ": " + ex.Message, "swingup", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.BusFault, "FaultCode:SwingUpTimeout");
            }

            // SwingUp() 只把命令交给周期线程。第一拍还是 Enabled 不能当成起摆失败。
            long swingSeen = _clock.ElapsedMilliseconds;
            long deadline = swingSeen + timeoutMs;
            bool gcanProbeDone = false;
            long motionProbeLog = 0;
            while (_clock.ElapsedMilliseconds < deadline)
            {
                Pump();
                if (_cancelRequested || _exitRequested)
                    return CommandResult.Fail(ExitCodes.Cancelled, "cancelled");
                // --timeout 到点：只中断起摆等待并按超时收口（归它既有的起摆超时码），不急停。
                if (CommandExpired) return CommandResult.Timeout();

                PendulumTelemetry t;
                bool have = TrySnapshot(out t);
                EngineState state = _engine.State;
                long swingElapsed = _clock.ElapsedMilliseconds - swingSeen;
                if (have && swingElapsed <= 2000 && swingElapsed - motionProbeLog >= 100)
                {
                    motionProbeLog = swingElapsed;
                    _output.Warn("motion t=" + swingElapsed + "ms arm=" + t.ArmPositionCount +
                        " phi=" + F2(t.PhiRad) + " CH1=" + t.PendulumCount + "/" + t.PendulumFreqCh1Hz +
                        "Hz CH2=" + t.PendulumCountCh2 + "/" + t.PendulumFreqCh2Hz + "Hz");
                }

                if (state == EngineState.Balancing)
                {
                    _output.AckWith("swingup", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                        "swingup", "phase=swing→balance  omega=" + (have ? F2(t.OmegaRadPerSec) : "0.00") + " rad/s  elapsed=" +
                        CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started),
                        e => e.Str("phase", "swing→balance").Num("omegaRadPerSec", have ? t.OmegaRadPerSec : 0)
                             .Num("elapsedSec", (_clock.ElapsedMilliseconds - started) / 1000.0));
                    return CommandResult.Success();
                }

                if (state != EngineState.Swinging)
                {
                    if (state == EngineState.Enabled &&
                        _clock.ElapsedMilliseconds - swingSeen < 1500)
                    {
                        Thread.Sleep(20);
                        continue;
                    }
                    FaultCode fault = have ? t.FaultCode : FaultCode.None;
                    int code = ExitCodes.ForFault(fault);
                    if (code == ExitCodes.Ok) code = ExitCodes.BusFault;
                    // 排队后的拒绝原因只认引擎的结构化记录（本次起摆前已清空旧记录）；
                    // 没有就如实说没有，不拿最后一条提示性诊断冒充故障原因。
                    string refusal = _engine.LastCommandRefusal;
                    bool freshRefusal = !string.IsNullOrEmpty(refusal);
                    string detail = "起摆中断，状态 " + state + "，故障 " + fault +
                        (freshRefusal
                            ? "；控制层拒绝原因：" + refusal
                            : "；没有记录到本次起摆的拒绝原因，先按 status 的「最近故障」排除，必要时 fault-reset 后重试") +
                        "。";
                    return FailEcho(command, started, code, "FaultCode:" + fault, detail);
                }

                if (!gcanProbeDone && have && t.SwingUpPhase > 0.4)
                {
                    gcanProbeDone = true;
                    uint[] raw;
                    string probeError;
                    if (_engine.TryReadGcanCounterSnapshot(out raw, out probeError))
                    {
                        _output.Warn("GC1502 SDO 0x6003: " + string.Join(",", raw) +
                            "；PDO: CH1=" + t.PendulumCount + "/" + t.PendulumFreqCh1Hz +
                            "Hz CH2=" + t.PendulumCountCh2 + "/" + t.PendulumFreqCh2Hz + "Hz");
                    }
                    else
                    {
                        _output.Warn("GC1502 SDO 对照读取失败：" + probeError);
                    }
                }

                // 切换判据已满足 = SwitchCountdown 归 0（01 §7）。起步那几拍判据必不满足，加 0.1s 下限防初值误判。
                if (have && t.SwingUpPhase > 0.1 && t.SwitchCountdown == 0)
                {
                    _output.AckWith("swingup", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                        "swingup", "phase=swing→balance-ready  omega=" + F2(t.OmegaRadPerSec) + " rad/s  elapsed=" +
                        CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started),
                        e => e.Str("phase", "swing→balance-ready").Num("omegaRadPerSec", t.OmegaRadPerSec)
                             .Num("elapsedSec", (_clock.ElapsedMilliseconds - started) / 1000.0));
                    return CommandResult.Success();
                }

                Thread.Sleep(20);
            }

            return FailEcho(command, started, ExitCodes.BusFault, "FaultCode:SwingUpTimeout",
                "起摆 " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms 未达切换判据");
        }

        // ---------- 7. balance ----------

        private CommandResult CmdBalance(ParsedCommand command, StateGate gate, long started)
        {
            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            try
            {
                _engine.Balance();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:BalanceLost", ex.GetType().Name + ": " + ex.Message, "balance", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.Interlock, "FaultCode:BalanceLost");
            }

            PendulumTelemetry t;
            bool have = TrySnapshot(out t);

            if (_engine.State == EngineState.Enabled)
            {
                _output.Warn("! warning  balance：已登记手动捕获平衡请求；把摆杆扶到直立附近并保持，"
                    + "捕获门连续满足后自动接管（|ψ|≤0.15、|ω_est|≤0.8、|φ̇|≤2.5、u 包线内）。");
            }

            int seconds;
            if (command.TryInt("seconds", out seconds) && seconds > 0)
            {
                CommandResult hold = HoldMotion(command, "balance", seconds, started);
                if (!hold.Ok) return hold;
                return CommandResult.Success();
            }

            if (command.Has("seconds"))
            {
                _output.Warn("! warning  balance --seconds 0 表示不限时运行，靠 Ctrl+C 或联锁停机");
            }

            _output.AckWith("balance", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "balance", "state=Balancing  偏离直立 " + Signed(have ? t.ThetaFromUprightRad : 0) + " rad  (" +
                CliOutput.FormatSeconds(_clock.ElapsedMilliseconds - started) + ")",
                e => e.Num("thetaFromUprightRad", have ? t.ThetaFromUprightRad : 0));
            return CommandResult.Success();
        }

        // ---------- 8. stop / 10. fault-reset ----------

        /// <summary>
        /// 受控停（QuickStop 路径）：0x0002 按 0x6085 斜坡减速 → 确认 QuickStopActive → 0x0000 去使能（等待有界 2s）。
        /// 清 EStopLatch 前只读复检 S11 硬接线急停，未松开保持 Fault 并拒绝（裁定 5）。
        /// </summary>
        private CommandResult CmdStop(ParsedCommand command, StateGate gate, long started)
        {
            if (gate == StateGate.Idempotent)
            {
                // 已是 Stopped：仍然回读 0x6040 / FaultCode / EStopLatch，不凭状态名假设它们已清零。
                PendulumTelemetry t;
                bool have = TrySnapshot(out t);
                string evidence = have
                    ? "state=Stopped  0x6040=0x" + t.ControlWord.ToString("X4", CultureInfo.InvariantCulture) +
                      "  FaultCode=" + t.FaultCode + "  EStopLatch=" + (t.EStopLatch ? "true" : "false")
                    : "state=Stopped  无本次会话遥测（0x6040 未回读）";
                _output.AckWith("stop", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "already stopped", evidence,
                    e => e.Str("engineState", "Stopped")
                         .Int("controlWord", have ? t.ControlWord : 0)
                         .Str("faultCode", have ? t.FaultCode.ToString() : "Unknown")
                         .Bool("estopLatch", _engine != null && _engine.IsEStopLatched));
                return CommandResult.Success();
            }
            return RunStopSequence("stop", started);
        }

        private CommandResult CmdFaultReset(ParsedCommand command, StateGate gate, long started)
        {
            return RunStopSequence("fault-reset", started);
        }

        private CommandResult RunStopSequence(string commandName, long started)
        {
            CommandResult precheck = PrecheckHardwareEStop(commandName, started);
            if (!precheck.Ok) return precheck;

            try
            {
                _engine.Stop();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:StopSequenceTimeout", ex.GetType().Name + ": " + ex.Message, commandName, Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.DeviceFault, "FaultCode:StopSequenceTimeout");
            }

            // 受控停是异步的（QuickStop 斜坡 + 有界 2 s 收尾）：必须等到 Stopped 再回读，
            // 不能命令刚排队就回 “state=X” 把“还没停”说成“已停”。
            EngineState state;
            PendulumTelemetry t;
            bool confirmed = WaitForEngineState(4500, out state, out t, 0, EngineState.Stopped);
            if (!confirmed)
            {
                EngineState now = _engine == null ? EngineState.Disconnected : _engine.State;
                PendulumTelemetry last;
                bool have = TrySnapshot(out last);
                string evidence = have
                    ? "状态=" + now + "，0x6040=0x" + last.ControlWord.ToString("X4", CultureInfo.InvariantCulture) +
                      "，FaultCode=" + last.FaultCode + "，EStopLatch=" + (last.EStopLatch ? "true" : "false")
                    : "状态=" + now + "，无本次会话遥测";
                string action = now == EngineState.Fault
                    ? "先按 status 的「最近故障」排除原因；硬急停未松开时先松开急停 / 恢复 STO，再 fault-reset。"
                    : "检查驱动器是否响应 QuickStop（status 看 0x6041）、主站是否仍在 OP；必要时按硬急停。";
                return FailEcho(commandName, started, ExitCodes.DeviceFault, "FaultCode:StopSequenceTimeout",
                    commandName + " 未在 4.5 s 内确认到 Stopped（" + evidence + "）。" + action);
            }

            // 停机后三个必须回读的事实：0x6040=0、FaultCode=None、EStopLatch=false。
            if (t.ControlWord != 0)
            {
                return FailEcho(commandName, started, ExitCodes.DeviceFault, "FaultCode:StopSequenceTimeout",
                    commandName + " 已到 Stopped，但 0x6040 回读为 0x" +
                    t.ControlWord.ToString("X4", CultureInfo.InvariantCulture) + "（期望 0x0000）。" +
                    "控制字未清干净：检查驱动器 / 其它主站是否接管了控制字，必要时按硬急停。");
            }
            if (t.FaultCode != FaultCode.None)
            {
                return FailEcho(commandName, started, ExitCodes.Interlock, "FaultCode:" + t.FaultCode,
                    commandName + " 已到 Stopped，但 FaultCode 仍为 " + t.FaultCode +
                    "（期望 None）。故障未被清除：按 status 的「最近故障」排除原因；硬急停未松开时先松开再 fault-reset。");
            }
            if (t.EStopLatch)
            {
                return FailEcho(commandName, started, ExitCodes.Interlock, "FaultCode:EStopInput",
                    commandName + " 已到 Stopped，但 EStopLatch 仍为 true（期望 false）。" +
                    "先松开硬接线急停 / 恢复 STO，确认 0x60FD bit23 与 0x6041 复位后再 fault-reset。");
            }

            double phiDev = t.PhiDevFromHomeRad;
            bool latched = _engine.IsEStopLatched;
            _output.AckWith(commandName, true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                commandName, "state=" + state + "  0x6040=0x0000（已回读）  FaultCode=None  EStopLatch=false" +
                "  φ偏差(起始位0)=" + Signed(phiDev) + " rad" +
                (commandName == "fault-reset" ? "  interlocks=clear" : string.Empty),
                e => e.Str("engineState", state.ToString())
                     .Int("controlWord", t.ControlWord)
                     .Str("faultCode", t.FaultCode.ToString())
                     .Bool("estopLatch", latched)
                     .Num("phiDevFromHomeRad", phiDev)
                     .Str("interlocks", commandName == "fault-reset" ? "clear" : "checked"));
            _pendingFaultExit = 0;
            return CommandResult.Success();
        }

        /// <summary>
        /// 只读复检 S11（硬接线急停 / STO）。仍闭合就拒绝并保持 Fault（裁定 5）。
        /// 证据取控制层 EmergencyStopInputActive（0x6041 的 Quick Stop 位）+ FaultCode=EStopInput。
        /// 本案例 PDO 无独立 DI 通道，硬急停只能从状态字取证据：驱动已下电时「仍闭合」与「已松开」
        /// 在状态字上不可区分，所以文案必须写明需现场确认，不能说成软件百分百能判。
        /// </summary>
        private CommandResult PrecheckHardwareEStop(string commandName, long started)
        {
            if (_engine == null) return CommandResult.Success();

            PendulumTelemetry t = new PendulumTelemetry();
            bool have = TrySnapshot(out t);
            // 自己的受控停（QuickStop 路径）会把驱动送进 QuickStopActive，与急停/STO 在状态字上同形，
            // 受控停进行中状态字证据不判，免得连敲 stop 把自己的受控停误报成硬急停。
            bool inputActive = !_engine.IsControlledStopActive && _engine.EmergencyStopInputActive;
            bool faultSaysEStop = have && t.FaultCode == FaultCode.EStopInput;
            if (!inputActive && !faultSaysEStop) return CommandResult.Success();

            _output.ErrorLine("interlock",
                "硬接线急停 / STO 证据仍在（S11），保持 Fault 并拒绝 " + commandName + "。" +
                "证据：状态字 0x6041 QuickStop 位=" + (inputActive ? "动作" : "未动作") +
                "，FaultCode=" + (have ? t.FaultCode.ToString() : "None") +
                "，EStopLatch=" + (_engine.IsEStopLatched ? "true" : "false") +
                "，θ=" + Rad(t.ThetaRad) + " rad。" +
                "注意：硬急停只能从状态字取证据，驱动已下电时「仍闭合」与「已松开」在状态字上不可区分，" +
                "请现场确认急停按钮 / STO 已松开后再复位。",
                "FaultCode:EStopInput", ExitCodes.Interlock);
            _pendingFaultExit = ExitCodes.Interlock;
            return CommandResult.Fail(ExitCodes.Interlock, "FaultCode:EStopInput");
        }

        // ---------- 9. estop ----------

        private CommandResult CmdEStop(ParsedCommand command, long started)
        {
            string reason = command.Value("reason") ?? "manual";
            bool disconnected = _engine == null || _engine.State == EngineState.Disconnected;
            try
            {
                if (_engine != null) _engine.EmergencyStop();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:DriveFault", "急停清零动作失败：" + ex.GetType().Name + ": " + ex.Message, "estop", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.DeviceFault, "FaultCode:DriveFault");
            }

            if (disconnected)
            {
                // 没有总线会话时软件急停写不到 0x6040；本地锁存已置，但绝不能回 “ok=已下发”。
                return FailEcho(command, started, ExitCodes.StateRejected, "state_rejected",
                    "总线未连接：软件急停无法写 0x6040=0（本地 EStopLatch 已置）。" +
                    "先 connect 并确认驱动器已去使能；机械有风险时立即按硬接线急停 / 切断 STO。");
            }

            // 回读 0x6040：带外写通常立即生效，留 1.5 s 让周期线程 / 遥测落地。
            PendulumTelemetry t = new PendulumTelemetry();
            bool have = false;
            bool zeroConfirmed = false;
            long deadline = _clock.ElapsedMilliseconds + 1500;
            while (_clock.ElapsedMilliseconds < deadline)
            {
                Pump();
                if (TrySnapshot(out t))
                {
                    have = true;
                    if (t.ControlWord == 0) { zeroConfirmed = true; break; }
                }
                Thread.Sleep(10);
            }
            if (!zeroConfirmed)
            {
                return FailEcho(command, started, ExitCodes.DeviceFault, "FaultCode:DriveFault",
                    "急停已请求，但 1.5 s 内未回读到 0x6040=0x0000（" +
                    (have ? "当前 0x" + t.ControlWord.ToString("X4", CultureInfo.InvariantCulture) : "无本次会话遥测") +
                    "）。立即按硬接线急停 / 切断 STO；再检查总线会话与驱动器状态字（0x6041）。");
            }

            _output.Event("estop", "estop", e => e.Str("reason", reason).Bool("estopLatch", true));
            _output.AckWith("estop", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "estop", "state=" + StateNow() + "  0x6040=0x0000（已回读）  FaultCode=" + t.FaultCode +
                "  EStopLatch=" + (t.EStopLatch ? "true" : "false") + "  reason=" + reason,
                e => e.Str("reason", reason).Bool("estopLatch", t.EStopLatch)
                     .Str("engineState", StateNow())
                     .Int("controlWord", t.ControlWord)
                     .Str("faultCode", t.FaultCode.ToString())
                     .Num("phiDevFromHomeRad", t.PhiDevFromHomeRad));
            _output.Warn("软件 EmergencyStop() 只把 0x6040 清零，不是安全回路，不替代硬接线急停 / STO / 限位。");
            return CommandResult.Success();
        }

        // ---------- 11/12/20. setpid / setswingup / setlimits ----------

        private CommandResult CmdSetPid(ParsedCommand command, long started)
        {
            PidGains before = _engine == null ? new PidGains() : _engine.BalancePid;
            if (!command.AnyFlag())
            {
                if (!_options.Json && !_options.Quiet) _output.Line(PidGainTable(before));
                if (!_options.Json && !_options.Quiet) _output.Line(AccelerationLawSummary(before));
                return EchoParams("setpid", started, PidSummary(before), e => WritePidFields(e, before));
            }

            // clone 现值再覆盖：没给的字段（含另一环、TdLp / 限幅 / 死区）一律保留，不许清零。
            PidGains gains = before;
            var deltas = new List<string>();

            // 历史增益 ka/kad/kai（上一代外环）与 kp/ki/kd（更早的位置映射律）：带符号 = 相对现值增减，
            // 不带符号 = 绝对设置。两组都只为旧参数文件往返保留，当前控制律不读取，下面会单独警告。
            if (!ApplyGainFlag(command, "ka", ref gains.Ka, deltas)) return BadNumber("ka", command.Value("ka"), started);
            if (!ApplyGainFlag(command, "kad", ref gains.Kad, deltas)) return BadNumber("kad", command.Value("kad"), started);
            if (!ApplyGainFlag(command, "kai", ref gains.Kai, deltas)) return BadNumber("kai", command.Value("kai"), started);
            if (!ApplyGainFlag(command, "kp", ref gains.Kp, deltas)) return BadNumber("kp", command.Value("kp"), started);
            if (!ApplyGainFlag(command, "ki", ref gains.Ki, deltas)) return BadNumber("ki", command.Value("ki"), started);
            if (!ApplyGainFlag(command, "kd", ref gains.Kd, deltas)) return BadNumber("kd", command.Value("kd"), started);
            // 内环 φ 三增益是真正生效的项：带符号 = 相对增减，不带符号 = 绝对设置。
            if (!ApplyGainFlag(command, "kp-phi", ref gains.KpPhi, deltas)) return BadNumber("kp-phi", command.Value("kp-phi"), started);
            if (!ApplyGainFlag(command, "ki-phi", ref gains.KiPhi, deltas)) return BadNumber("ki-phi", command.Value("ki-phi"), started);
            if (!ApplyGainFlag(command, "kd-phi", ref gains.KdPhi, deltas)) return BadNumber("kd-phi", command.Value("kd-phi"), started);

            // 历史字段「看着能调、实际无效」的旋钮必须当场说清：值照收（旧参数文件 / 老脚本要能往返），
            // 但平衡环已由整套状态反馈接管，当前控制律不读 Ka/Kad/Kai 与 Kp/Ki/Kd，设了不会改变控制输出。
            var historicalFlags = new List<string>();
            if (command.Has("ka")) historicalFlags.Add("ka");
            if (command.Has("kad")) historicalFlags.Add("kad");
            if (command.Has("kai")) historicalFlags.Add("kai");
            if (command.Has("kp")) historicalFlags.Add("kp");
            if (command.Has("ki")) historicalFlags.Add("ki");
            if (command.Has("kd")) historicalFlags.Add("kd");
            string historicalNote = historicalFlags.Count == 0
                ? null
                : string.Join("、", historicalFlags.ToArray()) +
                  " 是历史增益字段：平衡环已由整套状态反馈接管，当前控制律不读取，只用于旧参数文件往返；" +
                  "真正生效的平衡项是 --acc-max / --arm-rate-max / --kp-phi / --ki-phi / --kd-phi。" +
                  "本次已接受，但不会改变控制输出。";

            // 滤波 / 限幅类：一律绝对设置（负号就是取值本身，如 out-min=-0.35）。
            if (command.Has("td-lp-ms") && !command.TryDouble("td-lp-ms", out gains.TdLpMs)) return BadNumber("td-lp-ms", command.Value("td-lp-ms"), started);
            if (command.Has("out-min") && !command.TryDouble("out-min", out gains.OutMin)) return BadNumber("out-min", command.Value("out-min"), started);
            if (command.Has("out-max") && !command.TryDouble("out-max", out gains.OutMax)) return BadNumber("out-max", command.Value("out-max"), started);
            if (command.Has("out-rate") && !command.TryDouble("out-rate", out gains.OutRateLimit)) return BadNumber("out-rate", command.Value("out-rate"), started);
            if (command.Has("max-i") && !command.TryDouble("max-i", out gains.MaxIntegral)) return BadNumber("max-i", command.Value("max-i"), started);
            if (command.Has("deadband") && !command.TryDouble("deadband", out gains.DeadBand)) return BadNumber("deadband", command.Value("deadband"), started);

            // 加速度律限幅：绝对设置。耦合符号不在这里：它由台架实测固定
            // （PendulumEngine.ArmCouplingSign），运行期钉死，CLI 不再提供可写开关。
            if (command.Has("acc-max") && !command.TryDouble("acc-max", out gains.AccMax)) return BadNumber("acc-max", command.Value("acc-max"), started);
            if (command.Has("arm-rate-max") && !command.TryDouble("arm-rate-max", out gains.ArmRateMax)) return BadNumber("arm-rate-max", command.Value("arm-rate-max"), started);

            string reason;
            // Ka / Kad / Kai 是历史参数：平衡环已由整套状态反馈接管，它们不参与平衡，
            // 所以这里不再做结构下界 / 阻尼比校验 —— 那些判据只对已经退场的外环才有意义，
            // 留着只会让一个调了也没效果的数把整条命令拒掉。历史值照收，只为旧参数文件往返。
            if (!(gains.AccMax > 0))
            {
                _output.ErrorLine("invalid_arguments", "AccMax 必须大于 0 [rad/s²]", "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }
            if (!(gains.ArmRateMax > 0))
            {
                _output.ErrorLine("invalid_arguments", "ArmRateMax 必须大于 0 [rad/s]", "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }
            if (!gains.IsValid(out reason))
            {
                _output.ErrorLine("invalid_arguments", "PidGains 不合法，整体未写入：" + reason, "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }

            _engine.SetBalancePid(gains);
            PidGains after = _engine.BalancePid;

            // 警告与日志都在写成功之后：值确实进了参数结构（供旧文件往返），只是控制律不读取。
            if (historicalNote != null)
            {
                _output.Warn("! warning  setpid 已接受 --" + string.Join(" / --", historicalFlags.ToArray()) +
                             "：" + historicalNote);
                _output.WriteLogRecord("pid_historical_flags", historicalNote, "setpid", true, 0,
                    "accepted_not_effective");
            }

            if (!_options.Json && !_options.Quiet)
            {
                _output.Line("setpid 增益对照（带符号=相对增减，不带=绝对设置）:");
                for (int i = 0; i < deltas.Count; i++) _output.Line(deltas[i]);
                _output.Line("已生效（generation " + before.Generation.ToString(CultureInfo.InvariantCulture) +
                             "→" + after.Generation.ToString(CultureInfo.InvariantCulture) +
                             "，双缓冲下个周期拍首顶换）");
                if (historicalNote != null)
                    _output.Line("  注意: 上面历史旗标（--ka/--kad/--kai 与 --kp/--ki/--kd）的对照行只是写回参数结构" +
                                 "（供旧参数文件往返），控制输出不变；控制层可能按内置默认校正历史值，以回执为准。" +
                                 "真正生效的平衡项是 --acc-max / --arm-rate-max / --kp-phi / --ki-phi / --kd-phi。");
            }
            _output.WriteLogRecord("pid_changed",
                string.Join("  ", deltas.ToArray()) + "  gen " + before.Generation.ToString(CultureInfo.InvariantCulture) +
                "→" + after.Generation.ToString(CultureInfo.InvariantCulture),
                "setpid", true, 0, "ok");
            return EchoParams("setpid", started, PidSummary(after), e =>
            {
                WritePidFields(e, after);
                e.Bool("applied", true).Str("changes", string.Join("  ", deltas.ToArray()))
                 .Int("genBefore", before.Generation).Int("genAfter", after.Generation);
                if (historicalFlags.Count > 0)
                {
                    // 机器可读的「已接受但未生效」：脚本不该因为 ok=true 就以为控制输出变了。
                    e.Bool("acceptedButInactive", true)
                     .Bool("historicalFlagsEffective", false)
                     .Str("historicalFlags", string.Join(",", historicalFlags.ToArray()))
                     .Str("historicalFlagsNote", historicalNote);
                }
            });
        }

        private CommandResult CmdSetSwingUp(ParsedCommand command, long started)
        {
            SwingUpParams swing = _engine == null ? SwingUpParams.CreateDefault() : _engine.SwingUpParams;
            if (!command.AnyFlag())
                return EchoParams("setswingup", started, SwingSummary(swing), e => WriteSwingFields(e, swing));

            // 状态门（读取已在上面放行）：起摆中只放行热改项；平衡 / 已连接未使能 / 故障态整条拒绝。
            // 参数走 generation 双缓冲，但「能写进去」不等于「此时改它有意义且安全」，所以恢复这个门，
            // 拒绝时把当前状态、允许的状态与允许热改的旗标一次说清，不做半套写入。
            string stateReject = SetSwingUpChangeRejectText(command);
            if (stateReject != null)
                return FailEcho(command, started, ExitCodes.StateRejected, "state_rejected", stateReject);

            if (command.Has("energy-gain") && !command.TryDouble("energy-gain", out swing.TargetEnergyGain)) return BadNumber("energy-gain", command.Value("energy-gain"), started);
            if (command.Has("ke") && !command.TryDouble("ke", out swing.Ke)) return BadNumber("ke", command.Value("ke"), started);
            if (command.Has("amax") && !command.TryDouble("amax", out swing.AMax)) return BadNumber("amax", command.Value("amax"), started);
            if (command.Has("kphi") && !command.TryDouble("kphi", out swing.KPhi)) return BadNumber("kphi", command.Value("kphi"), started);
            if (command.Has("kphidot") && !command.TryDouble("kphidot", out swing.KPhiDot)) return BadNumber("kphidot", command.Value("kphidot"), started);
            if (command.Has("pump-amp") && !command.TryDouble("pump-amp", out swing.PumpAmplitude)) return BadNumber("pump-amp", command.Value("pump-amp"), started);
            if (command.Has("pump-hz") && !command.TryDouble("pump-hz", out swing.PumpFrequencyHz)) return BadNumber("pump-hz", command.Value("pump-hz"), started);
            if (command.Has("theta-switch") && !command.TryDouble("theta-switch", out swing.ThetaSwitchRad)) return BadNumber("theta-switch", command.Value("theta-switch"), started);
            if (command.Has("omega-switch") && !command.TryDouble("omega-switch", out swing.OmegaSwitchRadPerSec)) return BadNumber("omega-switch", command.Value("omega-switch"), started);
            if (command.Has("phi-switch") && !command.TryDouble("phi-switch", out swing.PhiSwitchRad)) return BadNumber("phi-switch", command.Value("phi-switch"), started);
            if (command.Has("hold-cycles") && !command.TryInt("hold-cycles", out swing.SwitchHoldCycles)) return BadNumber("hold-cycles", command.Value("hold-cycles"), started);
            if (command.Has("energy-extract") && !command.TryDouble("energy-extract", out swing.EnergyExtractRatio)) return BadNumber("energy-extract", command.Value("energy-extract"), started);
            if (command.Has("energy-deadband") && !command.TryDouble("energy-deadband", out swing.EnergyDeadbandRatio)) return BadNumber("energy-deadband", command.Value("energy-deadband"), started);
            // 抽取增益与注入 Ke 解耦：0 = 未配置（控制层按出厂 60 生效），负值 / 非有限值直接拒收，
            // 不留「写进去但被 Pick 静默回落」的假接受。
            if (command.Has("extract-gain"))
            {
                if (!command.TryDouble("extract-gain", out swing.ExtractGain))
                    return BadNumber("extract-gain", command.Value("extract-gain"), started);
                if (!ControlMath.IsFinite(swing.ExtractGain) || swing.ExtractGain < 0)
                {
                    _output.ErrorLine("invalid_arguments",
                        "--extract-gain 必须是 ≥0 的有限数，单位 rad/s² per J（0 = 未配置，按出厂 " +
                        ParamStore.FactoryExtractGain.ToString("0.###", CultureInfo.InvariantCulture) + " 生效）",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
            }
            if (command.Has("center-band") && !command.TryDouble("center-band", out swing.CenterBandRad)) return BadNumber("center-band", command.Value("center-band"), started);
            if (command.Has("capture-arm-rate") && !command.TryDouble("capture-arm-rate", out swing.CaptureArmRateLimitRadPerSec)) return BadNumber("capture-arm-rate", command.Value("capture-arm-rate"), started);
            // 捕获交棒六项（预捕获回中 / 快速提交臂速 / 摆杆角速度 / 漏桶）：起摆中全部锁定（见 SetSwingUpChangeRejectText），
            // 单项范围由 SwingUpParams.IsValid 校验，组合约束（提交带 ⊂ 硬捕获门）由 ParamStore 统一校验。
            if (command.Has("prepare-approach") && !command.TryDouble("prepare-approach", out swing.CapturePrepareApproachRad)) return BadNumber("prepare-approach", command.Value("prepare-approach"), started);
            if (command.Has("prepare-energy-margin") && !command.TryDouble("prepare-energy-margin", out swing.CapturePrepareEnergyMarginRatio)) return BadNumber("prepare-energy-margin", command.Value("prepare-energy-margin"), started);
            if (command.Has("commit-arm-rate") && !command.TryDouble("commit-arm-rate", out swing.CaptureCommitArmRateRadPerSec)) return BadNumber("commit-arm-rate", command.Value("commit-arm-rate"), started);
            if (command.Has("commit-omega") && !command.TryDouble("commit-omega", out swing.CaptureCommitOmegaRadPerSec)) return BadNumber("commit-omega", command.Value("commit-omega"), started);
            if (command.Has("commit-hold-cycles") && !command.TryInt("commit-hold-cycles", out swing.CaptureCommitHoldCycles)) return BadNumber("commit-hold-cycles", command.Value("commit-hold-cycles"), started);
            if (command.Has("hold-decay") && !command.TryInt("hold-decay", out swing.SwitchHoldDecayPerCycle)) return BadNumber("hold-decay", command.Value("hold-decay"), started);
            int timeoutMs;
            if (command.Has("timeout-ms") || command.Has("timeout"))
            {
                if (!command.TryInt("timeout-ms", out timeoutMs) && !command.TryInt("timeout", out timeoutMs))
                    return BadNumber("timeout-ms", command.Value("timeout-ms") ?? command.Value("timeout"), started);
                swing.TimeoutMs = timeoutMs;
            }

            string reason;
            if (!swing.IsValid(out reason))
            {
                _output.ErrorLine("invalid_arguments", "SwingUpParams 不合法，整体未写入：" + reason, "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }
            // 组合约束：快速提交带必须是硬捕获门的子集（参数层拒收；控制层 Step 还会再收紧一次）。
            if (!ParamStore.TryValidateCaptureHandoffGates(swing, out reason))
            {
                _output.ErrorLine("invalid_arguments", "起摆参数组合不合法，整体未写入：" + reason,
                    "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }
            _engine.SetSwingUpParams(swing);
            SwingUpParams after = _engine.SwingUpParams;
            return EchoParams("setswingup", started, SwingSummary(after), e => WriteSwingFields(e, after));
        }

        /// <summary>起摆中允许热改的旗标（只改本拍之后的注入 / 刹车权重，不改变已积累的能量状态）。</summary>
        private static readonly string[] SwingHotEditFlags =
        {
            "energy-extract", "energy-deadband", "extract-gain", "center-band", "capture-arm-rate"
        };

        private static bool IsSwingHotEditFlag(string flag)
        {
            for (int i = 0; i < SwingHotEditFlags.Length; i++)
                if (string.Equals(SwingHotEditFlags[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 捕获交棒参数（预捕获回中 / 快速提交 / 漏桶）。起摆中一律锁定：
        /// 它们直接决定「当前这一趟掠顶窗口」的收臂时机与提交判据 —— 中途改窗口会让本已开始的
        /// 回中动作与新的能量判据错位；中途放宽提交带 / 漏桶衰减 / 提交拍数，等于让当前这一趟
        /// 在未经验证的条件下提交给平衡环。与能量整形类热改项（只改本拍之后的权重）语义不同。
        /// </summary>
        private static readonly string[] SwingCaptureHandoffFlags =
        {
            "prepare-approach", "prepare-energy-margin", "commit-arm-rate", "commit-omega",
            "commit-hold-cycles", "hold-decay"
        };

        private static bool IsSwingCaptureHandoffFlag(string flag)
        {
            for (int i = 0; i < SwingCaptureHandoffFlags.Length; i++)
                if (string.Equals(SwingCaptureHandoffFlags[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 起摆参数改写的状态门（恢复旧版被删掉的门）。返回 null = 允许；否则是给现场的完整中文原因。
        /// <list type="bullet">
        /// <item>Connected（总线已连接、伺服未使能）：拒绝 —— 此时写入只会「静默接受」但没有任何闭环反馈，
        /// 现场很容易以为参数已经在起作用；先 enable，或断开 / 停止后再配置。</item>
        /// <item>Balancing：拒绝 —— 起摆参数已不在当前控制律里，改它会让下一次起摆 / 切换的节奏
        /// 与当前现场不一致；先 stop。</item>
        /// <item>Fault：拒绝 —— 先 fault-reset 清故障。</item>
        /// <item>Swinging：只放行热改项；捕获交棒六项（预捕获回中 / 快速提交臂速 / 摆杆角速度 / 漏桶）一律锁定；
        /// 带任何其它起摆字段整条拒绝，不做半套写入。</item>
        /// <item>Disconnected / Enabled / Stopped：允许（离线配置、使能后起摆前整定、停机整定）。</item>
        /// </list>
        /// </summary>
        private string SetSwingUpChangeRejectText(ParsedCommand command)
        {
            if (_engine == null)
                return "控制层未初始化，起摆参数无法写入。";

            EngineState state = _engine.State;
            switch (state)
            {
                case EngineState.Connected:
                    return "当前 Connected（总线已连接、伺服未使能）：起摆参数只在起摆 / 平衡闭环里生效，"
                        + "此时写入会被接受却没有任何运行反馈，不能算「已生效」。先 enable 再改，"
                        + "或断开后离线配置，或停到 Stopped 再改。";
                case EngineState.Balancing:
                    return "当前 Balancing（正在平衡）：起摆参数已不在当前控制律里，改它会让下一次起摆 / 切换的节奏"
                        + "与当前现场不一致。先 stop 再改。";
                case EngineState.Fault:
                    return "当前 Fault：先 fault-reset 清除故障，再改起摆参数。";
                case EngineState.Swinging:
                {
                    List<string> locked = new List<string>();
                    bool captureHandoffLocked = false;
                    string[] flags = command.Spec == null ? new string[0] : command.Spec.ValueFlags;
                    for (int i = 0; i < flags.Length; i++)
                    {
                        if (!command.Has(flags[i]) || IsSwingHotEditFlag(flags[i])) continue;
                        locked.Add("--" + flags[i]);
                        if (IsSwingCaptureHandoffFlag(flags[i])) captureHandoffLocked = true;
                    }
                    if (locked.Count == 0) return null;
                    return "当前 Swinging（起摆中）：只允许热改 --energy-extract / --energy-deadband / --extract-gain / "
                        + "--center-band / --capture-arm-rate；本次还带了 " + string.Join("、", locked.ToArray())
                        + "，整条命令未写入（不做半套生效）。等起摆结束再改这些字段。"
                        + (captureHandoffLocked
                            ? "其中捕获交棒参数（预捕获回中 / 快速提交 / 漏桶）在起摆中会改变当前掠顶窗口的收臂时机与提交判据，"
                              + "中途放宽会让这一趟在未经验证的条件下提交给平衡环，必须等起摆结束再改。"
                            : "");
                }
                default:
                    return null;
            }
        }

        private CommandResult CmdSetLimits(ParsedCommand command, long started)
        {
            SafetyLimits limits = _engine == null ? SafetyLimits.CreateDefault() : _engine.SafetyLimits;
            if (!command.AnyFlag())
                return EchoParams("setlimits", started, LimitsSummary(limits), e => WriteLimitFields(e, limits));

            // 旧旗标 --phi-min / --phi-max 是角度行程口径，已废除：不给换算、直接拒绝并说明。
            if (command.Has("phi-min") || command.Has("phi-max"))
            {
                _output.ErrorLine("invalid_arguments",
                    "软限位只接受绝对 counts：--phi-min / --phi-max 已废除（摆臂行程没有角度比例），" +
                    "改用 --arm-min-count / --arm-max-count（例如 -54000 / +54000）。",
                    "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }

            if (command.Has("theta-trip") && !command.TryDouble("theta-trip", out limits.ThetaTripRad)) return BadNumber("theta-trip", command.Value("theta-trip"), started);
            if (command.Has("theta-lost") && !command.TryDouble("theta-lost", out limits.ThetaLostRad)) return BadNumber("theta-lost", command.Value("theta-lost"), started);
            if (command.Has("phi-err-max") && !command.TryDouble("phi-err-max", out limits.PhiErrMaxRad)) return BadNumber("phi-err-max", command.Value("phi-err-max"), started);
            if (command.Has("arm-min-count") && !command.TryInt("arm-min-count", out limits.ArmMinCount)) return BadNumber("arm-min-count", command.Value("arm-min-count"), started);
            if (command.Has("arm-max-count") && !command.TryInt("arm-max-count", out limits.ArmMaxCount)) return BadNumber("arm-max-count", command.Value("arm-max-count"), started);
            if (command.Has("omega-arm-max") && !command.TryDouble("omega-arm-max", out limits.OmegaArmMaxRadPerSec)) return BadNumber("omega-arm-max", command.Value("omega-arm-max"), started);
            if (command.Has("jeq") && !command.TryDouble("jeq", out limits.JEq)) return BadNumber("jeq", command.Value("jeq"), started);
            if (command.Has("mgl") && !command.TryDouble("mgl", out limits.Mgl)) return BadNumber("mgl", command.Value("mgl"), started);

            string reason;
            if (!limits.IsValid(out reason))
            {
                _output.ErrorLine("invalid_arguments", "SafetyLimits 不合法，整体未写入：" + reason, "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }
            SafetyLimits before = _engine.SafetyLimits;
            _engine.SetSafetyLimits(limits);
            SafetyLimits after = _engine.SafetyLimits;
            _output.WriteLogRecord("limits_changed",
                "θ_trip " + F2(before.ThetaTripRad) + "→" + F2(after.ThetaTripRad) +
                "  θ_lost " + F2(before.ThetaLostRad) + "→" + F2(after.ThetaLostRad) +
                "  φ_err " + F2(before.PhiErrMaxRad) + "→" + F2(after.PhiErrMaxRad) +
                "  软限位 [" + before.ArmMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                before.ArmMaxCount.ToString(CultureInfo.InvariantCulture) + "]→[" +
                after.ArmMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                after.ArmMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts",
                "setlimits", true, 0, "ok");
            return EchoParams("setlimits", started, LimitsSummary(after), e => WriteLimitFields(e, after));
        }

        private CommandResult EchoParams(string commandName, long started, string human, Action<JsonEnvelope> extra)
        {
            _output.AckWith(commandName, true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                commandName, human, extra);
            return CommandResult.Success();
        }

        private CommandResult BadNumber(string flag, string raw, long started)
        {
            string message = "旗标 --" + flag + " 的取值 \"" + (raw ?? string.Empty) + "\" 不是数值";
            _output.ErrorLine("invalid_arguments", message, "invalid_arguments", ExitCodes.InvalidArguments);
            return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
        }

        // ---------- 13/14. save-config / load-config ----------

        private CommandResult CmdSaveConfig(ParsedCommand command, long started)
        {
            string path = command.Value("path");
            if (string.IsNullOrWhiteSpace(path)) path = ParamStore.DefaultPath;
            // 第一道防线：本进程没成功载入过参数文件时，引擎里是出厂值（pid / swingup / limits）。
            // 覆盖已有文件会把用户的整份参数改成出厂值 —— 直接拒绝并说明；目标文件不存在时允许
            // 首次创建（没有用户数据可丢），但明确告知内容是当前引擎值。
            bool targetExists;
            try { targetExists = File.Exists(path); }
            catch (Exception ex)
            {
                targetExists = false;
                _output.WriteLogRecord("path_check_failed", ex.GetType().Name + ": " + ex.Message, "save-config", false, ExitCodes.ConfigError, "config_invalid");
            }
            if (!_paramsLoadedFromFile && targetExists)
            {
                _output.ErrorLine("config_invalid",
                    "当前进程没有成功载入过参数文件（引擎里是出厂值），保存会把出厂值写入已有文件，已拒绝：" + path +
                    "。请先 load-config（或重启 CLI 让默认参数文件自动载入）后再保存。",
                    "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }
            if (!_paramsLoadedFromFile)
                _output.Warn("! warning  当前进程没有载入过参数文件，正在新建 " + path + "（内容 = 当前引擎值，不是任何已有文件的覆盖）。");
            string error;
            PidGains pid = _engine.BalancePid;
            SwingUpParams swing = _engine.SwingUpParams;
            SafetyLimits limits = _engine.SafetyLimits;
            // deniPath 只作整定溯源记录写进文件，不拿它切拓扑。
            if (!ParamStore.Save(path, pid, swing, limits, _engine.DeniPath, out error))
            {
                _output.ErrorLine("config_invalid", "写参数文件失败：" + error, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }
            long bytes = 0;
            try
            {
                bytes = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 文件长度只用于显示，取不到不影响保存结果，但必须留痕。
                _output.WriteLogRecord("size_unavailable", ex.GetType().Name + ": " + ex.Message, "save-config", false,
                    ExitCodes.Ok, "ok");
            }
            // 回退到 %AppData% / 发现旧路径参数文件：明说，不静默。
            if (!string.IsNullOrEmpty(ParamStore.EffectivePathNote))
                _output.Warn("! warning  参数文件路径：" + ParamStore.EffectivePathNote);
            _output.AckWith("save-config", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "saved", "path=" + path + "  bytes=" + bytes.ToString(CultureInfo.InvariantCulture) +
                "  gen=" + pid.Generation.ToString(CultureInfo.InvariantCulture) + "/" +
                swing.Generation.ToString(CultureInfo.InvariantCulture) + "/" +
                limits.Generation.ToString(CultureInfo.InvariantCulture),
                e => e.Str("path", path).Int("bytes", bytes)
                     .Int("pidGen", pid.Generation).Int("swingGen", swing.Generation).Int("limitsGen", limits.Generation));
            return CommandResult.Success();
        }

        private CommandResult CmdLoadConfig(ParsedCommand command, StateGate gate, long started)
        {
            string path = command.Value("path");
            if (string.IsNullOrWhiteSpace(path)) path = ParamStore.DefaultPath;
            return ApplyConfigFile(path, started);
        }

        private CommandResult ApplyConfigFile(string path, long started)
        {
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            string deniPath;
            string deniNote;
            string error;
            if (!ParamStore.TryLoad(path, _engine.DeniPath, out pid, out swing, out limits, out deniPath, out deniNote, out error))
            {
                // 载入的是默认参数文件时，status 行要跟着改口：否则前面「整份拒载」的旧结论会一直挂着。
                ParamStore.ReportDefaultFileLoad(path, false, error, null);
                _output.ErrorLine("config_invalid", "读参数文件失败：" + error, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            string reason;
            if (!pid.IsValid(out reason) || !swing.IsValid(out reason) || !limits.IsValid(out reason))
            {
                ParamStore.ReportDefaultFileLoad(path, false, "参数文件字段不合法，整体未生效：" + reason, null);
                _output.ErrorLine("config_invalid", "参数文件字段不合法，整体未生效：" + reason, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            ParamStore.ReportDefaultFileLoad(path, true, null, deniNote);
            _engine.SetBalancePid(pid);
            _engine.SetSwingUpParams(swing);
            _engine.SetSafetyLimits(limits);
            // 本次进程已成功载入过参数文件：后续 save-config / zero 自动落盘可以安全覆盖同一文件。
            MarkParamsLoadedFromFile();
            // 文件里记的 DENI 与当前生效的不同：只提示，不切拓扑、不换 DENI。
            if (!string.IsNullOrEmpty(deniNote))
                _output.Warn("! warning  " + deniNote);
            if (!string.IsNullOrEmpty(ParamStore.EffectivePathNote))
                _output.Warn("! warning  参数文件路径：" + ParamStore.EffectivePathNote);
            _output.AckWith("load-config", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "loaded", "path=" + path + "  Ka=" + F3(pid.Ka) + " Kad=" + F3(pid.Kad) + " Kai=" + F3(pid.Kai) +
                "  历史kp=" + F3(pid.Kp) + "  pumpHz=" + F2(swing.PumpFrequencyHz) +
                "  thetaTripRad=" + F2(limits.ThetaTripRad) +
                // 联合捕获门的 6 个值原样回显文件里读到的数（0 = 文件未配置，控制层按内置默认补齐）：
                // 参数持久化往返核对就靠这一行，不需要再去翻 JSON。
                "  captureGate(uMax=" + F2(swing.CaptureModeAmplitudeMaxRad) + " rad, strokePerU=" +
                F2(swing.CaptureStrokePerModeRad) + " rad/rad, margin=" + F2(swing.CaptureStrokeMarginRad) +
                " rad, adverseRate=" + F2(swing.CaptureAdverseRateStrokeSec) + " s, armRateMax=" +
                F2(swing.CaptureArmRateLimitRadPerSec) + " rad/s, omegaN=" +
                F2(swing.CapturePendulumRateRadPerSec) + " rad/s)",
                e => e.Str("path", path).Num("ka", pid.Ka).Num("kad", pid.Kad).Num("kai", pid.Kai)
                     .Num("kp", pid.Kp).Num("pumpHz", swing.PumpFrequencyHz)
                     .Num("thetaTripRad", limits.ThetaTripRad).Num("phiHomeRad", ReadPhiHome())
                     .Num("captureModeAmplitudeMaxRad", swing.CaptureModeAmplitudeMaxRad)
                     .Num("captureStrokePerModeRad", swing.CaptureStrokePerModeRad)
                     .Num("captureStrokeMarginRad", swing.CaptureStrokeMarginRad)
                     .Num("captureAdverseRateStrokeSec", swing.CaptureAdverseRateStrokeSec)
                     .Num("captureArmRateLimitRadPerSec", swing.CaptureArmRateLimitRadPerSec)
                     .Num("capturePendulumRateRadPerSec", swing.CapturePendulumRateRadPerSec));
            return CommandResult.Success();
        }

        private double ReadPhiHome()
        {
            PendulumTelemetry t;
            return TrySnapshot(out t) ? t.PhiHomeRad : 0;
        }

        // ---------- 15/16. telemetry / watch ----------

        private CommandResult CmdTelemetry(ParsedCommand command, long started)
        {
            double hz = 1.0;
            if (command.Has("hz"))
            {
                double raw;
                if (!command.TryDouble("hz", out raw) || raw < 1.0 || raw > 20.0)
                {
                    _output.ErrorLine("invalid_arguments", "--hz 取值范围 1..20", "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
                hz = raw;
            }
            int seconds = -1;
            if (command.Has("seconds") && !command.TryInt("seconds", out seconds))
                return BadNumber("seconds", command.Value("seconds"), started);

            if (command.Has("once"))
            {
                PendulumTelemetry t = new PendulumTelemetry();
                bool have = TrySnapshot(out t);
                if (!_options.Json && !_options.Quiet) _output.Line(TelemetryHeader());
                WriteTelemetryLine(t, have);
                return CommandResult.Success();
            }
            return RunTelemetryLoop(command, hz, seconds, started);
        }

        private CommandResult CmdWatch(ParsedCommand command, long started)
        {
            double interval = 1.0;
            if (command.Has("interval"))
            {
                double raw;
                if (!command.TryDouble("interval", out raw) || raw <= 0)
                    return BadNumber("interval", command.Value("interval"), started);
                interval = raw;
            }
            int seconds = -1;
            if (command.Has("seconds") && !command.TryInt("seconds", out seconds))
                return BadNumber("seconds", command.Value("seconds"), started);
            return RunWatchLoop(command, interval, seconds, started);
        }

        private CommandResult RunTelemetryLoop(ParsedCommand command, double hz, int seconds, long started)
        {
            bool headerWritten = false;
            long deadline = seconds > 0 ? _clock.ElapsedMilliseconds + seconds * 1000L : long.MaxValue;
            int intervalMs = Math.Max(1, (int)(1000.0 / hz));

            while (_clock.ElapsedMilliseconds < deadline)
            {
                Pump();
                if (_cancelRequested || _exitRequested) return CommandResult.Fail(ExitCodes.Cancelled, "cancelled");
                // --timeout 到点：只中断流式输出等待并按超时收口，不急停。
                if (CommandExpired) return CommandResult.Timeout();
                PendulumTelemetry t;
                bool have = TrySnapshot(out t);
                if (!_options.Json && !headerWritten)
                {
                    _output.Line(TelemetryHeader());
                    headerWritten = true;
                }
                WriteTelemetryLine(t, have);

                if (have && t.FaultCode != FaultCode.None && t.FaultCode != FaultCode.UserStop)
                {
                    int code = ExitCodes.ForFault(t.FaultCode);
                    _output.ErrorLine("interlock", "遥测显示故障 " + t.FaultCode + "，停止流式输出；先 status 看「最近故障」并排除，再 fault-reset",
                        "FaultCode:" + t.FaultCode, code);
                    return CommandResult.Fail(code == ExitCodes.Ok ? ExitCodes.BusFault : code, "FaultCode:" + t.FaultCode);
                }
                Thread.Sleep(intervalMs);
            }
            return CommandResult.Success();
        }

        private CommandResult RunWatchLoop(ParsedCommand command, double intervalSeconds, int seconds, long started)
        {
            long deadline = seconds > 0 ? _clock.ElapsedMilliseconds + seconds * 1000L : long.MaxValue;
            int intervalMs = Math.Max(100, (int)(intervalSeconds * 1000.0));
            long begin = _clock.ElapsedMilliseconds;

            while (_clock.ElapsedMilliseconds < deadline)
            {
                Pump();
                if (_cancelRequested || _exitRequested) return CommandResult.Fail(ExitCodes.Cancelled, "cancelled");
                // --timeout 到点：只中断流式输出等待并按超时收口，不急停。
                if (CommandExpired) return CommandResult.Timeout();
                PendulumTelemetry t;
                bool have = TrySnapshot(out t);
                WriteWatchLine(t, have, _clock.ElapsedMilliseconds - begin);

                if (have && t.FaultCode != FaultCode.None && t.FaultCode != FaultCode.UserStop)
                {
                    int code = ExitCodes.ForFault(t.FaultCode);
                    _output.ErrorLine("interlock", "监视中命中故障 " + t.FaultCode + "：先 status 看「最近故障」并排除，再 fault-reset", "FaultCode:" + t.FaultCode, code);
                    return CommandResult.Fail(code == ExitCodes.Ok ? ExitCodes.BusFault : code, "FaultCode:" + t.FaultCode);
                }
                Thread.Sleep(intervalMs);
            }
            return CommandResult.Success();
        }

        // ---------- 17/18/19/22. quit / help / version / log-path ----------

        private CommandResult CmdQuit(ParsedCommand command, long started)
        {
            _exitRequested = true;
            int code = Teardown(true);
            _output.AckWith("quit", code == ExitCodes.Ok, code, code == ExitCodes.Ok ? "ok" : "teardown_failed",
                _clock.ElapsedMilliseconds - started, StateNow(), "quit",
                "teardown=" + (code == ExitCodes.Ok ? "clean" : "failed"), e => e.Str("teardown", code == ExitCodes.Ok ? "clean" : "failed"));
            return code == ExitCodes.Ok ? CommandResult.Success() : CommandResult.Fail(code, "teardown_failed");
        }

        private CommandResult CmdHelp(ParsedCommand command, long started)
        {
            if (command.Positional.Count > 0)
            {
                CommandSpec spec = CommandTable.Find(command.Positional[0]);
                if (spec == null)
                {
                    _output.ErrorLine("invalid_arguments", "未知命令 \"" + command.Positional[0] + "\"", "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
                _output.Line(CliCommandParser.BuildCommandHelp(spec));
                return CommandResult.Success();
            }
            _output.Line(CliCommandParser.BuildHelpText());
            return CommandResult.Success();
        }

        private CommandResult CmdVersion(ParsedCommand command, long started)
        {
            string text = CliCommandParser.BuildVersionText();
            if (_options.Json)
                _output.AckWith("version", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "version", text, e => e.Str("version", text));
            else
                _output.Line(text);
            return CommandResult.Success();
        }

        private CommandResult CmdLogPath(ParsedCommand command, long started)
        {
            string path = _output.Log == null ? string.Empty : _output.Log.CurrentFilePath;
            if (string.IsNullOrEmpty(path))
                path = _output.Log == null ? string.Empty : System.IO.Path.Combine(_output.Log.DirectoryPath, "(尚未开写)");
            // GUI 与 CLI 现在共用一个日志根；旧 CLI 目录只作历史查询，写入不再落到那里。
            string legacyDir = DiagnosticLogWriter.LegacyDirectoryPath;
            if (_options.Json)
                _output.AckWith("log-path", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                    "log path", path, e => e.Str("logPath", path).Str("logDir", _output.Log == null ? string.Empty : _output.Log.DirectoryPath)
                        .Str("legacyLogDir", legacyDir));
            else
            {
                _output.Line(path);
                _output.Line("历史遗留（只读，新日志不再写这里）：" + legacyDir);
            }
            return CommandResult.Success();
        }

        // ---------- 21. zero ----------

        private CommandResult CmdZero(ParsedCommand command, StateGate gate, long started)
        {
            if (command.Positional.Count == 0)
            {
                _output.ErrorLine("invalid_arguments", "用法: zero pendulum | zero arm", "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }
            string what = command.Positional[0].ToLowerInvariant();
            if (what != "pendulum" && what != "arm")
            {
                _output.ErrorLine("invalid_arguments", "用法: zero pendulum | zero arm，收到 \"" + command.Positional[0] + "\"",
                    "invalid_arguments", ExitCodes.InvalidArguments);
                return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
            }

            PendulumTelemetry t;
            bool have = TrySnapshot(out t);

            if (what == "pendulum" && have)
            {
                // 下垂位判定必须用「可信参考零点 + counts 域按一圈取模」，不能直接拿累计 θ 比绝对值：
                // 摆杆是增量计数，同一垂位允许读成相差整数圈的 raw（2400 counts/圈时 ±2400 等价），
                // 累计角比较会把这种等价位置误判成「不在垂位」，也会被整圈偏移骗过。
                // 参考零点只在两种情况下可信：① 参数文件里的 0°/360° 两点已在**本次进程**整体确认；
                // ② 控制层里有本次进程设过的摆杆零点。两者都没有 = 参考已过期/未确认，此时不做
                // 程序化判定，只提示现场人工确认 —— 绝不拿一个不可信的零点把正确位置拒掉。
                string referenceNote;
                int referenceZero;
                bool trusted = TryGetTrustedPendulumZeroReference(out referenceZero, out referenceNote);
                double equivalentAngle;
                if (trusted && _engine.PendulumCountsPerRev > 0)
                {
                    equivalentAngle = CalibrationParameters.WrappedAngleRad(
                        t.PendulumCount, referenceZero, _engine.PendulumCountsPerRev);
                }
                else
                {
                    equivalentAngle = t.ThetaRad;
                }

                if (!trusted)
                {
                    _output.Warn("! warning  没有可信的摆杆 0° 参考（" + referenceNote +
                                 "）：无法程序化判定是否在自然下垂位，请现场确认摆杆已松手下垂后再执行。");
                }
                else if (!ControlMath.IsFinite(equivalentAngle))
                {
                    _output.Warn("! warning  摆杆每转计数（CPR_PENDULUM）未标定，无法把计数折成角度判定下垂位，请现场确认摆杆已松手下垂。");
                }
                else if (Math.Abs(equivalentAngle) > ZERO_PENDULUM_TOLERANCE_RAD)
                {
                    _output.Line("摆杆须松手自然下垂。当前等效角 θ=" + Rad(equivalentAngle) +
                                 " rad（" + referenceNote + "，已按一圈取模，整圈偏差不算偏离），看起来不在下垂位。");
                    _output.ErrorLine("config_invalid",
                        "请把摆杆放到自然下垂位再执行；若机械确实在垂位，说明保存的 0° 参考已过期：" +
                        "先清空 / 重采摆杆 0° 与 360° 两点（两条一起平移会保留一圈的量），不要强行清零。",
                        "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                    return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
                }
            }
            else if (what == "pendulum" && _engine.PendulumCountsPerRev <= 0)
            {
                // CPR 未标定时 θ 算不出来，没法程序化判下垂位 —— 如实说明，不假装判过。
                _output.Warn("! warning  CPR_PENDULUM 未标定，无法程序化判摆杆是否在自然下垂位，请现场确认摆杆已松手下垂。");
            }

            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            try
            {
                if (what == "pendulum") _engine.ZeroPendulum();
                else _engine.ZeroArm();
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:ConfigInvalid", ex.GetType().Name + ": " + ex.Message, "zero", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            // 零点命令是在下一控制拍落定的（CmdZero* 只是排队）。等它落定再读回 / 落盘，
            // 否则会把「还没换基准」的旧值当成新零点写进文件。
            WaitForCalibrationLanded();

            // **自动落盘**：旧版 CLI 的 zero 只改内存，日志里写「尚未持久化」，
            // 换个进程使能就因「保存零点与当前 raw 不一致」被拒。现在 zero 成功即增量写回参数文件，
            // 保留其它可选段，只更新标定零点。写失败不掩盖成功事实，但必须明确报原因。
            string saveMessage;
            bool saved = AutoSaveCalibration(out saveMessage);

            PendulumTelemetry after;
            bool haveAfter = TrySnapshot(out after);
            string human = what == "pendulum"
                ? "theta=" + Rad(haveAfter ? after.ThetaRad : 0) + " rad  (摆杆零点已重标定，θ 零点 = 自然下垂位)"
                : "phi=" + Rad(haveAfter ? after.PhiRad : 0) + " rad  φ_zero_count=" +
                  (haveAfter ? after.ArmPositionCount : 0).ToString(CultureInfo.InvariantCulture) +
                  "  (摆臂零点已重标定：当前位置 = 坐标 0 = 起始位置，home 恒为 0，不锁存任意角)";
            human += saved
                ? "  [已自动保存：" + saveMessage + "]"
                : "  [自动保存失败：" + (saveMessage ?? "未返回原因，见会话日志") + "；请执行 save-config]";
            if (!string.IsNullOrEmpty(_lastCalibrationNote))
                human += "  [标定：" + _lastCalibrationNote + "]";
            _output.AckWith("zero " + what, true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "zero " + what, human,
                e => e.Str("target", what)
                     .Int("armPositionCount", haveAfter ? after.ArmPositionCount : 0)
                     .Num("thetaRad", haveAfter ? after.ThetaRad : 0)
                     .Num("phiRad", haveAfter ? after.PhiRad : 0)
                     .Num("phiHomeRad", haveAfter ? after.PhiHomeRad : 0)
                     .Bool("persisted", saved)
                     .Bool("pendulum360Rebased", _lastPendulum360Rebased)
                     .Str("calibrationNote", _lastCalibrationNote)
                     .Str("saveError", saved ? null : saveMessage));
            return CommandResult.Success();
        }

        /// <summary>
        /// 取一个**可信**的摆杆 0° 参考零点，用于 zero pendulum 前的下垂位判定。
        /// 可信 = 参数文件里的 0°/360° 两点已在本次进程整体确认（会话标记匹配），
        /// 或控制层里有本次进程设过的摆杆零点。其它情况（旧进程留下的文件、没有标定段）
        /// 一律返回 false，由调用方降级为「现场人工确认」，不拿过期零点拒掉正确操作。
        /// </summary>
        private bool TryGetTrustedPendulumZeroReference(out int zeroCount, out string note)
        {
            zeroCount = 0;
            note = null;

            PersistedExtras extras;
            string readNote;
            bool haveExtras = ParamStore.TryReadExtras(ParamStore.DefaultPath, out extras, out readNote) &&
                              extras != null && extras.Calibration != null;
            CalibrationParameters cal = haveExtras ? extras.Calibration : null;
            if (cal != null && cal.HasPendulumZero && cal.PendulumPoseSessionVerified)
            {
                zeroCount = cal.PendulumZeroCount;
                note = "参数文件 0°/360° 两点已在本次进程确认（0° raw=" + zeroCount.ToString(CultureInfo.InvariantCulture) + "）";
                return true;
            }
            if (_engine != null && _engine.PendulumZeroCountSet)
            {
                zeroCount = _engine.PendulumZeroRawCount;
                note = "控制层本次进程设定的摆杆零点 raw=" + zeroCount.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (!haveExtras)
            {
                note = "参数文件读不到或没有标定段" + (string.IsNullOrEmpty(readNote) ? "" : "（" + readNote + "）");
            }
            else if (!cal.HasPendulumZero)
            {
                note = "参数文件里没有摆杆 0° 点";
            }
            else
            {
                note = "参数文件里的 0° 点不是本次进程确认的（" + CalibrationSession.DescribeCurrent() + "）";
            }
            return false;
        }

        /// <summary>
        /// 等零点在控制拍上落定：命令是下一控制拍才消费的，先无条件等 100 ms（200 拍），
        /// 之后再看 CalibrationDirty（最多到 500 ms）。不能一看到「之前就脏」立刻返回 ——
        /// 那会在命令还没落定时就把旧零点写进文件。
        /// </summary>
        private void WaitForCalibrationLanded()
        {
            long begin = _clock.ElapsedMilliseconds;
            while (_clock.ElapsedMilliseconds - begin < 500)
            {
                Pump();
                if (_clock.ElapsedMilliseconds - begin >= 100 && _engine.CalibrationDirty) return;
                Thread.Sleep(10);
            }
            Pump();
        }

        /// <summary>
        /// 增量保存位置标定：读回文件里的可选段（泵 / 捕获 / 辨识 / 软限位 / 原标定），
        /// 只把零点 / CPR / 减速比换成当前生效值，再整份写回。参数文件不存在时按默认段新建。
        /// 返回 false 时 message 是失败原因（调用方必须如实展示，不能吞掉）。
        /// </summary>
        private bool AutoSaveCalibration(out string message)
        {
            message = null;
            _lastCalibrationNote = null;
            _lastPendulum360Rebased = false;
            string path = ParamStore.DefaultPath;
            // 第一道防线：本进程没成功载入过参数文件时，引擎里是出厂值（主参数三段）。
            // 这时自动落盘绝不能覆盖已有文件 —— 台架事故就是一次 zero arm 把整份参数写成出厂值。
            bool targetExists;
            try { targetExists = File.Exists(path); }
            catch (Exception ex) { targetExists = false; System.Diagnostics.Debug.WriteLine("检查参数文件是否存在失败：" + DiagText.Describe(ex)); }
            if (!_paramsLoadedFromFile && targetExists)
            {
                message = "当前进程没有成功载入过参数文件（引擎里是出厂值），拒绝自动写回已有参数文件：" + path +
                          "；零点已生效但未落盘。请先 load-config（或重启 CLI 让默认参数文件自动载入）后再执行 zero / save-config。";
                _output.ErrorLine("config_invalid", message, "FaultCode:ConfigInvalid", ExitCodes.ConfigError);
                return false;
            }
            PersistedExtras extras;
            string note;
            if (!ParamStore.TryReadExtras(path, out extras, out note) || extras == null)
            {
                extras = PersistedExtras.CreateDefault(
                    _engine.ResolvedArmCountsPerRev > 0 ? _engine.ResolvedArmCountsPerRev : 10000.0);
            }
            if (extras.Calibration == null)
                extras.Calibration = CalibrationParameters.CreateDefault();
            CalibrationParameters cal = extras.Calibration;
            if (_engine.PendulumCountsPerRev > 0) cal.PendulumCpr = _engine.PendulumCountsPerRev;
            if (_engine.ResolvedArmCountsPerRev > 0) cal.ArmCountsPerRev = _engine.ResolvedArmCountsPerRev;
            double gear = _engine.ArmGearRatio;
            if (gear > 0 && !double.IsNaN(gear) && !double.IsInfinity(gear)) cal.ArmGearRatio = gear;
            if (_engine.ArmZeroCountSet)
            {
                cal.HasArmZeroCount = true;
                cal.ArmZeroCount = _engine.ArmZeroCount;
            }
            else
            {
                cal.HasArmZeroCount = false;
                cal.ClearArmPoseSessionMark();
            }
            if (_engine.PendulumZeroCountSet)
            {
                int newZero = _engine.PendulumZeroRawCount;
                if (cal.HasPendulumZero && cal.PendulumZeroCount == newZero)
                {
                    // 零点没变（zero arm / recenter / normalize / 半径标定这些路径）：0°/360° 原样保留。
                }
                else
                {
                    // 0° 点变了：360° 点必须随同一圈量平移，否则文件里 |360−0| 会变，
                    // 下次载入按它派生一圈的量 → 摆杆角度整圈算错（表现为清零后角度跳到另一侧）。
                    // 平移失败/没有旧零点时由 RebasePendulum360AfterZero 作废 360° 点并给出原因。
                    string rebaseNote;
                    _lastPendulum360Rebased = cal.RebasePendulum360AfterZero(newZero, out rebaseNote);
                    _lastCalibrationNote = rebaseNote;
                }
            }
            extras.Pump = PumpParameters.From(_engine.SwingUpParams);
            extras.SoftLimits = SoftLimitParameters.From(_engine.SafetyLimits);

            string error;
            if (!ParamStore.Save(path, _engine.BalancePid, _engine.SwingUpParams, _engine.SafetyLimits,
                    _engine.DeniPath, extras, out error))
            {
                message = error;
                return false;
            }
            _engine.ClearCalibrationDirty();
            message = path;
            if (!string.IsNullOrEmpty(ParamStore.EffectivePathNote))
                message += "（" + ParamStore.EffectivePathNote + "）";
            return true;
        }

        // ---------- 22. recenter ----------

        /// <summary>
        /// 把摆臂开到**行程中点**，到位后自动做居中规整化（当前点 = φ=0 = 区间中点，行程跨度不变）。
        /// 漂到区间一端后的恢复入口：只回旧零点没用，必须把中心挪到行程几何中点。
        /// 控制层负责运动与零点，CLI 只等待、调用居中规整化并落盘。
        /// </summary>
        private CommandResult CmdRecenter(ParsedCommand command, StateGate gate, long started)
        {
            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            int budgetMs = 25000;
            int seconds;
            if (command.TryInt("seconds", out seconds) && seconds > 0) budgetMs = seconds * 1000;

            string reason;
            try
            {
                if (!_engine.TryRecenterToTravelCenter(out reason))
                {
                    _output.ErrorLine("state_rejected", "回中被拒绝：" + (string.IsNullOrEmpty(reason) ? "控制层未返回原因（先看 status 的「最近故障」与会话日志）" : reason),
                        "state_rejected", ExitCodes.StateRejected);
                    return CommandResult.Fail(ExitCodes.StateRejected, "state_rejected");
                }
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:ConfigInvalid", ex.GetType().Name + ": " + ex.Message, "recenter", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            // 命令下一控制拍才被消费：先等它真的开始（IsReturnHomeActive=true），再等它结束。
            long begin = _clock.ElapsedMilliseconds;
            bool sawActive = false;
            while (_clock.ElapsedMilliseconds - begin < budgetMs)
            {
                Pump();
                if (_engine.IsReturnHomeActive)
                {
                    sawActive = true;
                }
                else if (sawActive)
                {
                    break;
                }
                Thread.Sleep(20);
            }
            Pump();

            if (_engine.IsReturnHomeActive)
            {
                return FailEcho(command, started, ExitCodes.Interlock, "timeout",
                    "回中未在 " + budgetMs.ToString(CultureInfo.InvariantCulture) + " ms 内完成；控制层会按自己的 20 s 预算收尾。");
            }
            if (!sawActive)
            {
                // 已在目标位置（或命令未被周期线程消费）：如实回报，不假装走完了一段运动。
                _output.Warn("! warning  回中命令已接受，但没有观察到运动（可能已在中点，或周期线程尚未消费）。");
            }

            // 超时 / 命令未消费 / 机械挡住时绝不能把当前位置误居中：只有实测真的到达行程中点才允许重锁零。
            PendulumTelemetry arrival;
            bool haveArrival = TrySnapshot(out arrival);
            int centerTarget = _engine.ReturnHomeTargetCount;
            if (!haveArrival ||
                Math.Abs((long)arrival.ArmPositionCount - centerTarget) > PendulumEngine.ReturnHomeToleranceCounts)
            {
                return FailEcho(command, started, ExitCodes.Interlock, "not_at_center",
                    "回中结束位置不在行程中点：目标 raw=" + centerTarget.ToString(CultureInfo.InvariantCulture) +
                    "，当前 raw=" + (haveArrival ? arrival.ArmPositionCount.ToString(CultureInfo.InvariantCulture) : "未知") +
                    "，允许误差 ±" + PendulumEngine.ReturnHomeToleranceCounts.ToString(CultureInfo.InvariantCulture) +
                    " counts。未执行居中规整化；请检查机械阻挡、限位与 [MOTION] 日志后重试。");
            }

            // 到位后自动居中规整化：零点 = 当前（≈行程中点），左右限位按同一跨度平移到以它为中点。
            ArmPoseNormalization norm = new ArmPoseNormalization();
            string normReason;
            bool normalized;
            try
            {
                normalized = _engine.TryNormalizeCenteredOnCurrent(out norm, out normReason);
            }
            catch (Exception ex)
            {
                normalized = false;
                normReason = ex.GetType().Name + ": " + ex.Message;
            }
            if (!normalized)
            {
                return FailEcho(command, started, ExitCodes.ConfigError, "normalize_failed",
                    "回中已到位，但居中规整化失败：" + (normReason ?? "控制层未返回原因（先看 status 的「最近故障」与会话日志）") +
                    "。零点仍是回中后的位置；可执行 normalize 手动重试。");
            }

            // 零点 / 限位已改：增量落盘，让「回中 → 居中 → 重新锁零」跨进程成立。
            string saveMessage;
            bool saved = AutoSaveCalibration(out saveMessage);
            PendulumTelemetry t;
            bool have = TrySnapshot(out t);
            string human = "0x6064=" + (have ? t.ArmPositionCount : 0).ToString(CultureInfo.InvariantCulture) +
                           "  φ_zero_count=" + _engine.ArmZeroCount.ToString(CultureInfo.InvariantCulture) +
                           "  限位=[" + norm.NewMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                           norm.NewMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts（跨度 " +
                           norm.NewSpanCounts.ToString(CultureInfo.InvariantCulture) + "，原零点 " +
                           norm.OldZeroCount.ToString(CultureInfo.InvariantCulture) + "、Δ=" +
                           norm.DeltaCounts.ToString(CultureInfo.InvariantCulture) + " counts）" +
                           (saved ? "  [已自动保存：" + saveMessage + "]"
                                  : "  [自动保存失败：" + (saveMessage ?? "未返回原因，见会话日志") + "；请执行 save-config]");
            _output.AckWith("recenter", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "recentered", human,
                e => e.Int("armPositionCount", have ? t.ArmPositionCount : 0)
                     .Int("armZeroCount", _engine.ArmZeroCount)
                     .Int("oldZeroCount", norm.OldZeroCount)
                     .Int("newMinCount", norm.NewMinCount)
                     .Int("newMaxCount", norm.NewMaxCount)
                     .Num("spanCounts", (double)norm.NewSpanCounts)
                     .Int("deltaCounts", (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, norm.DeltaCounts)))
                     .Num("phiRad", have ? t.PhiRad : 0)
                     .Bool("calibrationDirty", _engine.CalibrationDirty)
                     .Bool("persisted", saved)
                     .Str("saveError", saved ? null : saveMessage));
            return CommandResult.Success();
        }

        // ---------- 23. normalize ----------

        /// <summary>
        /// 位置规整化（文档 §5.5）：当前 0x6064 = φ=0，左右限位按同一 delta 平移，行程跨度不变。
        /// 只在 Connected / Stopped 允许；新旧零点 / 限位 / 平移量全部回显并自动落盘。
        /// </summary>
        private CommandResult CmdNormalize(ParsedCommand command, long started)
        {
            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            ArmPoseNormalization norm = new ArmPoseNormalization();
            string reason;
            try
            {
                if (!_engine.TryNormalizeArmPose(out norm, out reason))
                {
                    _output.ErrorLine("state_rejected", "规整化被拒绝：" + (string.IsNullOrEmpty(reason) ? "控制层未返回原因（先看 status 的「最近故障」与会话日志）" : reason),
                        "state_rejected", ExitCodes.StateRejected);
                    return CommandResult.Fail(ExitCodes.StateRejected, "state_rejected");
                }
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:ConfigInvalid", ex.GetType().Name + ": " + ex.Message, "normalize", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            string saveMessage;
            bool saved = AutoSaveCalibration(out saveMessage);
            string human = "新零点 raw=" + norm.NewZeroCount.ToString(CultureInfo.InvariantCulture) +
                           "（旧 " + norm.OldZeroCount.ToString(CultureInfo.InvariantCulture) + "，Δ=" +
                           norm.DeltaCounts.ToString(CultureInfo.InvariantCulture) + " counts）；限位 [" +
                           norm.OldMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                           norm.OldMaxCount.ToString(CultureInfo.InvariantCulture) + "] → [" +
                           norm.NewMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                           norm.NewMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts（跨度 " +
                           norm.NewSpanCounts.ToString(CultureInfo.InvariantCulture) + " 保持不变）" +
                           (saved ? "  [已自动保存：" + saveMessage + "]"
                                  : "  [自动保存失败：" + (saveMessage ?? "未返回原因，见会话日志") + "；请执行 save-config]");
            _output.AckWith("normalize", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "normalized", human,
                e => e.Int("oldZeroCount", norm.OldZeroCount).Int("newZeroCount", norm.NewZeroCount)
                     .Int("oldMinCount", norm.OldMinCount).Int("oldMaxCount", norm.OldMaxCount)
                     .Int("newMinCount", norm.NewMinCount).Int("newMaxCount", norm.NewMaxCount)
                     .Num("spanCounts", (double)norm.NewSpanCounts)
                     .Int("deltaCounts", (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, norm.DeltaCounts)))
                     .Bool("persisted", saved).Str("saveError", saved ? null : saveMessage));
            return CommandResult.Success();
        }

        // ---------- 24. calibrate-radius ----------

        /// <summary>
        /// r/l 主动标定：摆杆自然下垂静止时，摆臂给 ±已知加速度脉冲，用同窗口 φ̈/θ̈ 二次拟合反推 r/l。
        /// 成功后写入 limits.radius_ratio 并落盘；失败原样报原因（不写入、不猜）。
        /// </summary>
        private CommandResult CmdCalibrateRadius(ParsedCommand command, StateGate gate, long started)
        {
            CommandResult confirm;
            if (!ConfirmIfNeeded(command, out confirm)) return EchoConfirmFail(command, started, confirm);

            double accel = 100.0;
            double pulseMs = 60.0;
            int pairs = 3;
            if (command.Has("accel") && !command.TryDouble("accel", out accel)) return BadNumber("accel", command.Value("accel"), started);
            if (command.Has("pulse-ms") && !command.TryDouble("pulse-ms", out pulseMs)) return BadNumber("pulse-ms", command.Value("pulse-ms"), started);
            if (command.Has("pairs") && !command.TryInt("pairs", out pairs)) return BadNumber("pairs", command.Value("pairs"), started);

            string reason;
            try
            {
                if (!_engine.TryStartRadiusCalibration(accel, pulseMs / 1000.0, pairs, out reason))
                {
                    _output.ErrorLine("state_rejected", "r/l 标定被拒绝：" + (string.IsNullOrEmpty(reason) ? "控制层未返回原因（先看 status 的「最近故障」与会话日志）" : reason),
                        "state_rejected", ExitCodes.StateRejected);
                    return CommandResult.Fail(ExitCodes.StateRejected, "state_rejected");
                }
            }
            catch (Exception ex)
            {
                _output.Error("FaultCode:ConfigInvalid", ex.GetType().Name + ": " + ex.Message, "calibrate-radius", Truncate(ex.StackTrace));
                return CommandResult.Fail(ExitCodes.ConfigError, "FaultCode:ConfigInvalid");
            }

            // 预算：静置 6 s + 每对 2×(2 相 + 静置) ≈ 3 s，再留 10 s 余量。
            int budgetMs = 10000 + pairs * 4000;
            long begin = _clock.ElapsedMilliseconds;
            bool sawActive = false;
            while (_clock.ElapsedMilliseconds - begin < budgetMs)
            {
                Pump();
                if (_engine.IsRadiusCalibrationActive)
                {
                    sawActive = true;
                }
                else if (sawActive || _clock.ElapsedMilliseconds - begin > 1000)
                {
                    break;
                }
                Thread.Sleep(20);
            }
            Pump();

            RadiusCalibrationResult result = _engine.RadiusCalibrationResult;
            if (_engine.IsRadiusCalibrationActive)
            {
                _engine.AbortRadiusCalibration();
                return FailEcho(command, started, ExitCodes.Interlock, "timeout",
                    "r/l 标定未在 " + budgetMs.ToString(CultureInfo.InvariantCulture) + " ms 内完成，已请求中止。");
            }
            if (result == null)
            {
                return FailEcho(command, started, ExitCodes.DeviceFault, "no_result",
                    "r/l 标定没有产生结果（" + (sawActive ? "标定器已结束但结果为空" : "命令可能未被周期线程消费") + "）。");
            }
            if (!result.Valid)
            {
                return FailEcho(command, started, ExitCodes.DeviceFault, "invalid_estimate",
                    "r/l 标定失败：" + (result.Reason ?? "控制层未返回原因（先看 status 的「最近故障」与会话日志）"));
            }

            // 写入安全阈值并落盘。等一拍让 generation 双缓冲顶换后再保存。
            SafetyLimits limits = _engine.SafetyLimits;
            limits.RadiusRatio = result.RadiusRatio;
            _engine.SetSafetyLimits(limits);
            Thread.Sleep(50);
            Pump();
            string saveMessage;
            bool saved = AutoSaveCalibration(out saveMessage);

            double ka = _engine.BalancePid.Ka;
            double omegaN2 = limits.JEq > 0 ? limits.Mgl / limits.JEq : 0;
            double margin = result.RadiusRatio * ka - omegaN2;
            string human = "r/l=" + F3(result.RadiusRatio) + "（" + result.ValidSamples.ToString(CultureInfo.InvariantCulture) +
                           " 相有效，σ=" + F3(result.StdDev) + "，相对 σ/|r/l|=" +
                           F1(result.RelativeStdDev * 100.0) + "%（允许 ≤25%），绝对门限=" +
                           F3(result.AllowedStdDev) + "；平均 |φ̈|=" + F2(result.MeanAbsPhiAccelRadPerSec2) +
                           " rad/s²、|θ̈|=" + F2(result.MeanAbsThetaAccelRadPerSec2) + " rad/s²）；结构裕量 (r/l)·Ka−g/l=" +
                           F1(margin) + " rad/s²（>0 才可能站住）" +
                           (saved ? "  [已自动保存：" + saveMessage + "]"
                                  : "  [自动保存失败：" + (saveMessage ?? "未返回原因，见会话日志") + "；请执行 save-config]");
            _output.AckWith("calibrate-radius", true, ExitCodes.Ok, "ok", _clock.ElapsedMilliseconds - started, StateNow(),
                "calibrated", human,
                e => e.Num("radiusRatio", result.RadiusRatio).Int("validSamples", result.ValidSamples)
                     .Num("stdDev", result.StdDev)
                     .Num("relativeStdDev", result.RelativeStdDev)
                     .Num("allowedStdDev", result.AllowedStdDev)
                     .Num("meanAbsPhiAccel", result.MeanAbsPhiAccelRadPerSec2)
                     .Num("meanAbsThetaAccel", result.MeanAbsThetaAccelRadPerSec2)
                     .Num("structuralMarginRadPerSec2", margin)
                     .Bool("persisted", saved).Str("saveError", saved ? null : saveMessage));
            return CommandResult.Success();
        }

        // ---------- 格式化辅助 ----------

        private void WriteTelemetryFields(JsonEnvelope e, PendulumTelemetry t, bool have)
        {
            // 状态字段以**引擎实时状态**为准（与 status / dashboard 同一口径）；
            // 快照里的状态只作为“这一帧遥测记录的状态”单列，并给出年龄与是否新鲜，
            // 不拿上一拍/上一会话的 EngineState 冒充当前状态。
            double ageSec = _engine == null ? -1 : _engine.TelemetryAgeSeconds;
            if (double.IsPositiveInfinity(ageSec)) ageSec = -1;
            bool fresh = ageSec >= 0 && ageSec <= 0.5 && have;
            BusStatusSnapshot bus = default(BusStatusSnapshot);
            bool haveBus = _engine != null && _engine.TryReadBusStatus(out bus);

            e.Int("Sequence", t.Sequence);
            e.Int("TimestampTicks", t.TimestampTicks);
            e.Str("EngineState", StateNow());
            e.Str("TelemetryEngineState", have ? t.EngineState.ToString() : "None");
            e.Bool("TelemetryAvailable", have);
            e.Bool("TelemetryFresh", fresh);
            e.Num("TelemetryAgeSeconds", ageSec);
            e.Bool("busStatusAvailable", haveBus);
            e.Str("BusState", haveBus ? bus.MasterState : (_engine == null ? "None" : _engine.BusStateName));
            e.Bool("BusInOp", _engine != null && _engine.IsBusInOp);
            e.Num("ThetaRad", t.ThetaRad);
            e.Num("ThetaFromUprightRad", t.ThetaFromUprightRad);
            e.Num("OmegaRadPerSec", t.OmegaRadPerSec);
            // 频率计换算的 ω：GC1502 FreqCh1 是平均量，只做 ω 交叉校验，不进控制律、不能当 dθ/dt。
            e.Num("OmegaFromFreqRadPerSec", t.OmegaFromFreqRadPerSec);
            e.Bool("FreqCrossCheckValid", t.FreqCrossCheckValid);
            // 摆杆保持位（直立）的 count 视角：±CPR/2 count (180°)。wrap 语义下 +CPR/2 ≡ -CPR/2，两侧是同一点。
            e.Num("pendulumHoldTargetCount", _engine == null || _engine.PendulumCountsPerRev <= 0
                     ? 0 : _engine.PendulumCountsPerRev / 2.0)
             .Str("pendulumHoldTarget", PendulumHoldTargetText());
            e.Num("PhiRad", t.PhiRad);
            e.Num("PhiDotRadPerSec", t.PhiDotRadPerSec);
            e.Num("PhiRefRad", t.PhiRefRad);
            e.Num("PhiTargetRad", t.PhiTargetRad);
            e.Num("PhiHomeRad", t.PhiHomeRad);
            e.Num("PhiDevFromHomeRad", t.PhiDevFromHomeRad);
            e.Int("PendulumCount", t.PendulumCount);
            // GCAN 计数切片原值：CH1 计数已在 PendulumCount，这里补两路频率与 CH2 计数。
            e.Int("pendulumFreqCh1Hz", t.PendulumFreqCh1Hz);
            e.Int("PendulumCountCh2", unchecked((int)t.PendulumCountCh2));
            e.Int("pendulumFreqCh2Hz", t.PendulumFreqCh2Hz);
            e.Int("ArmPositionCount", t.ArmPositionCount);
            e.Int("ArmTargetCount", t.ArmTargetCount);
            e.Num("ControlThetaOut", t.ControlThetaOut);
            e.Num("ControlPhiOut", t.ControlPhiOut);
            e.Num("PtermTheta", t.PtermTheta);
            e.Num("ItermTheta", t.ItermTheta);
            e.Num("DtermTheta", t.DtermTheta);
            e.Num("PtermPhi", t.PtermPhi);
            e.Num("ItermPhi", t.ItermPhi);
            e.Num("DtermPhi", t.DtermPhi);
            e.Num("EnergyJ", t.EnergyJ);
            e.Num("EnergyTargetJ", t.EnergyTargetJ);
            e.Num("EnergyDeltaJ", t.EnergyDeltaJ);
            e.Num("SwingUpPhase", t.SwingUpPhase);
            e.Int("SwitchCountdown", t.SwitchCountdown);
            // 整圈捕获：进度 + 释放点；没释放时 CaptureRelease* 写 0 但 hasCaptureRelease=false（不拿 0 冒充）。
            e.Bool("ReleaseWaitActive", t.ReleaseWaitActive)
             .Bool("ReleaseArmed", t.ReleaseArmed)
             .Bool("CaptureActive", t.CaptureActive)
             .Bool("hasCaptureRelease", have && !double.IsNaN(t.CaptureReleaseThetaDeg) &&
                                        !double.IsInfinity(t.CaptureReleaseThetaDeg))
             .Num("CaptureReleaseThetaDeg", t.CaptureReleaseThetaDeg)
             .Num("CaptureReleaseOmegaRadPerSec", t.CaptureReleaseOmegaRadPerSec)
             .Num("CaptureSweepDeg", t.CaptureSweepDeg)
             .Num("CaptureMaxSweepDeg", t.CaptureMaxSweepDeg)
             .Num("CaptureMaxDeviationDeg", t.CaptureMaxDeviationDeg)
             .Num("CaptureRecordSeconds", t.CaptureHoldSeconds)
             .Int("CaptureKeyPoints", t.CaptureKeyPoints)
             .Bool("pendulumFeedbackLive", _engine != null && _engine.IsPendulumFeedbackLive)
             .Num("pendulumFeedbackAgeSeconds",
                 _engine == null ? -1 : (double.IsInfinity(_engine.PendulumFeedbackAgeSeconds)
                     ? -1
                     : _engine.PendulumFeedbackAgeSeconds));
            e.Int("ControlWord", t.ControlWord);
            e.Int("StatusWord", t.StatusWord);
            e.Int("DriveErrorCode", t.DriveErrorCode);
            e.Int("ModesDisplay", t.ModesDisplay);
            e.Int("FollowingErrorCount", t.FollowingErrorCount);
            e.Int("ActualWkc", t.ActualWkc);
            e.Int("ExpectedWkc", t.ExpectedWkc);
            e.Int("CycleDeltaTicks", t.CycleDeltaTicks);
            e.Int("ComputeTicks", t.ComputeTicks);
            // 故障码 / 急停锁存用引擎实时字段（权威值）；快照里的值单列，不混用作“当前”。
            e.Str("FaultCode", (_engine == null ? t.FaultCode : _engine.FaultCodeNow).ToString());
            e.Str("TelemetryFaultCode", have ? t.FaultCode.ToString() : "None");
            e.Bool("EStopLatch", _engine == null ? t.EStopLatch : _engine.IsEStopLatched);
            e.Bool("IsSaturated", t.IsSaturated);
            e.Int("SaturatedCycles", t.SaturatedCycles);
            e.Int("elapsedMs", (long)MotionElapsedMs());
            e.Int("dropped", _output.TelemetryDropped);
            if (!have) e.Str("note", "no-snapshot");
        }

        private void WritePidFields(JsonEnvelope e, PidGains g)
        {
            e.Num("ka", g.Ka).Num("kad", g.Kad).Num("kai", g.Kai)
             .Num("kPhi", g.KPhi).Num("kPhiDot", g.KPhiDot)
             .Num("accMax", g.AccMax).Num("armRateMax", g.ArmRateMax).Num("sign", g.Sign)
             .Num("armCouplingSign", PendulumEngine.ArmCouplingSign)
             .Num("kp", g.Kp).Num("ki", g.Ki).Num("kd", g.Kd)
             .Num("kpPhi", g.KpPhi).Num("kiPhi", g.KiPhi).Num("kdPhi", g.KdPhi)
             .Num("tdLpMs", g.TdLpMs)
             .Num("outMin", g.OutMin).Num("outMax", g.OutMax).Num("outRateLimit", g.OutRateLimit)
             .Num("maxIntegral", g.MaxIntegral).Num("deadBand", g.DeadBand).Int("gen", g.Generation);
        }

        private void WriteSwingFields(JsonEnvelope e, SwingUpParams s)
        {
            e.Num("targetEnergyGain", s.TargetEnergyGain).Num("ke", s.Ke).Num("aMax", s.AMax)
             .Num("kPhi", s.KPhi).Num("kPhiDot", s.KPhiDot)
             .Num("pumpAmpRadPerSec2", s.PumpAmplitude).Num("pumpHz", s.PumpFrequencyHz)
             .Num("thetaSwitchRad", s.ThetaSwitchRad).Num("omegaSwitchRadPerSec", s.OmegaSwitchRadPerSec)
             .Num("phiSwitchRad", s.PhiSwitchRad).Int("switchHoldCycles", s.SwitchHoldCycles)
             .Num("energyExtractRatio", s.EnergyExtractRatio).Num("energyDeadbandRatio", s.EnergyDeadbandRatio)
             .Num("extractGain", s.ExtractGain)
             .Num("centerBandRad", s.CenterBandRad).Num("captureArmRateLimitRadPerSec", s.CaptureArmRateLimitRadPerSec)
             .Num("capturePrepareApproachRad", s.CapturePrepareApproachRad)
             .Num("capturePrepareEnergyMarginRatio", s.CapturePrepareEnergyMarginRatio)
             .Num("captureCommitArmRateRadPerSec", s.CaptureCommitArmRateRadPerSec)
             .Num("captureCommitOmegaRadPerSec", s.CaptureCommitOmegaRadPerSec)
             .Int("captureCommitHoldCycles", s.CaptureCommitHoldCycles)
             .Int("switchHoldDecayPerCycle", s.SwitchHoldDecayPerCycle)
             .Num("switchHoldDecayCyclesPerCycle", s.SwitchHoldDecayPerCycle * 0.001)
             .Int("timeoutMs", s.TimeoutMs).Int("gen", s.Generation);
        }

        private void WriteLimitFields(JsonEnvelope e, SafetyLimits s)
        {
            e.Num("thetaTripRad", s.ThetaTripRad).Num("thetaLostRad", s.ThetaLostRad)
             .Num("phiErrMaxRad", s.PhiErrMaxRad)
             .Int("armMinCount", s.ArmMinCount).Int("armMaxCount", s.ArmMaxCount)
             .Num("omegaArmMaxRadPerSec", s.OmegaArmMaxRadPerSec).Num("jeq", s.JEq).Num("mgl", s.Mgl)
             .Int("gen", s.Generation);
        }

        /// <summary>当前真正生效的平衡参数；历史增益 Ka/Kad/Kai、Kφ/Kφ̇、Kp/Ki/Kd 单独标注，避免被当成整定入口。</summary>
        private static string AccelerationLawSummary(PidGains g)
        {
            return "当前生效：AccMax=" + F1(g.AccMax) + " rad/s²  ArmRateMax=" + F2(g.ArmRateMax) + " rad/s" +
                   "  内环 Kpφ/Kiφ/Kdφ=" + F3(g.KpPhi) + "/" + F3(g.KiPhi) + "/" + F3(g.KdPhi) +
                   "  TdLp=" + F1(g.TdLpMs) + " ms" +
                   "  耦合符号(实测,只读)=" + (g.Sign < 0 ? "-1" : "+1") +
                   "  | 历史字段（控制律不读取）Ka=" + F3(g.Ka) + " Kad=" + F3(g.Kad) + " Kai=" + F3(g.Kai) +
                   " Kφ=" + F3(g.KPhi) + " Kφ̇=" + F3(g.KPhiDot) +
                   " Kp=" + F3(g.Kp) + " Ki=" + F3(g.Ki) + " Kd=" + F3(g.Kd);
        }

        private static string PidSummary(PidGains g)
        {
            return "当前生效 AccMax=" + F1(g.AccMax) + " ArmRateMax=" + F2(g.ArmRateMax) +
                   "  内环 Kpφ/Kiφ/Kdφ=" + F3(g.KpPhi) + "/" + F3(g.KiPhi) + "/" + F3(g.KdPhi) +
                   "  tdLpMs=" + F1(g.TdLpMs) +
                   "  out=[" + F2(g.OutMin) + "," + F2(g.OutMax) + "]" +
                   "  耦合符号(实测)=" + (g.Sign < 0 ? "-1" : "+1") +
                   "  | 历史(不读取) Ka=" + F3(g.Ka) + " Kad=" + F3(g.Kad) + " Kai=" + F3(g.Kai) +
                   " Kφ=" + F3(g.KPhi) + " Kφ̇=" + F3(g.KPhiDot) +
                   " Kp=" + F3(g.Kp) + " Ki=" + F3(g.Ki) + " Kd=" + F3(g.Kd) +
                   "  gen=" + g.Generation.ToString(CultureInfo.InvariantCulture);
        }

        private static string SwingSummary(SwingUpParams s)
        {
            return "pumpAmp=" + F2(s.PumpAmplitude) + " rad/s²  pumpHz=" + F2(s.PumpFrequencyHz) +
                   "  ke=" + F3(s.Ke) + "  aMax=" + F2(s.AMax) +
                   "  extract=" + F3(s.EnergyExtractRatio) + "  deadband=" + F3(s.EnergyDeadbandRatio) +
                   "  extractGain=" + F3(s.ExtractGain) +
                   "  centerBand=" + F2(s.CenterBandRad) + " rad  captureArmRate=" + F2(s.CaptureArmRateLimitRadPerSec) + " rad/s" +
                   "  prepApproach=" + F2(s.CapturePrepareApproachRad) + " rad  prepMargin=" + F3(s.CapturePrepareEnergyMarginRatio) +
                   "  commitArmRate=" + F2(s.CaptureCommitArmRateRadPerSec) + " rad/s" +
                   "  commitOmega=" + F2(s.CaptureCommitOmegaRadPerSec) + " rad/s" +
                   "  commitHold=" + s.CaptureCommitHoldCycles.ToString(CultureInfo.InvariantCulture) +
                   "  holdDecay=" + s.SwitchHoldDecayPerCycle.ToString(CultureInfo.InvariantCulture) + "‰拍/拍" +
                   "  timeoutMs=" + s.TimeoutMs.ToString(CultureInfo.InvariantCulture) +
                   "  gen=" + s.Generation.ToString(CultureInfo.InvariantCulture);
        }

        private static string LimitsSummary(SafetyLimits s)
        {
            return "thetaTripRad=" + F2(s.ThetaTripRad) + "  thetaLostRad=" + F2(s.ThetaLostRad) +
                   "  phiErrMaxRad=" + F2(s.PhiErrMaxRad) +
                   "  软限位=[" + s.ArmMinCount.ToString(CultureInfo.InvariantCulture) + "," +
                   s.ArmMaxCount.ToString(CultureInfo.InvariantCulture) + "] counts" +
                   "  gen=" + s.Generation.ToString(CultureInfo.InvariantCulture);
        }

        private void WriteTelemetryLine(PendulumTelemetry t, bool have)
        {
            if (_options.Json)
            {
                _output.TelemetrySample(e => WriteTelemetryFields(e, t, have));
                return;
            }
            if (_options.Quiet) return;
            _output.Line(TelemetryRow(t, have));
        }

        private static string TelemetryHeader()
        {
            return "  时间      状态       θ(°)     偏离直立   ω(rad/s)  φ(counts)  φ̇(rad/s)  跟随误差(counts)  uθ      uφ     周期(µs)  WKC";
        }

        private string TelemetryRow(PendulumTelemetry t, bool have)
        {
            double cycleUs = t.CycleDeltaTicks * 1000000.0 / Stopwatch.Frequency;
            // 状态列用引擎实时状态；快照年龄单独标注，陈旧时不冒充当前值。
            double ageSec = _engine == null ? -1 : _engine.TelemetryAgeSeconds;
            string staleness;
            if (double.IsPositiveInfinity(ageSec)) staleness = "无本次会话遥测";
            else if (ageSec > 0.5) staleness = "快照 " + ageSec.ToString("0.0", CultureInfo.InvariantCulture) + " s 前";
            else staleness = null;
            return "  " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                   "  " + StateNow().PadRight(10) +
                   "  " + Deg360Value(t.ThetaRad) +
                   "  " + Signed(t.ThetaFromUprightRad).PadLeft(9) +
                   "  " + t.OmegaRadPerSec.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9) +
                   "  " + t.ArmPositionCount.ToString("N0", CultureInfo.InvariantCulture).PadLeft(10) +
                   "  " + t.PhiDotRadPerSec.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9) +
                   "  " + t.FollowingErrorCount.ToString("N0", CultureInfo.InvariantCulture).PadLeft(15) +
                   "  " + t.ControlThetaOut.ToString("0.000", CultureInfo.InvariantCulture) +
                   "  " + t.ControlPhiOut.ToString("0.000", CultureInfo.InvariantCulture) +
                   "  " + cycleUs.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8) +
                   "  " + t.ActualWkc.ToString(CultureInfo.InvariantCulture) + "/" + t.ExpectedWkc.ToString(CultureInfo.InvariantCulture) +
                   "  fault=" + (_engine == null ? (have ? t.FaultCode.ToString() : "None") : _engine.FaultCodeNow.ToString()) +
                   "  cw=0x" + t.ControlWord.ToString("X4", CultureInfo.InvariantCulture) +
                   (staleness == null ? string.Empty : "  [" + staleness + "]");
        }

        private void WriteWatchLine(PendulumTelemetry t, bool have, long elapsedMs)
        {
            long totalSeconds = elapsedMs / 1000;
            string stamp = "[" + (totalSeconds / 3600).ToString("00", CultureInfo.InvariantCulture) + ":" +
                           ((totalSeconds / 60) % 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                           (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture) + "]";
            double cycleUs = t.CycleDeltaTicks * 1000000.0 / Stopwatch.Frequency;
            Console.Out.WriteLine(
                stamp + " " + StateNow().PadRight(10) +
                "  θ=" + Deg360Value(t.ThetaRad) + "(" + Signed(t.ThetaFromUprightRad) + "rad)" +
                "  ω=" + t.OmegaRadPerSec.ToString("0.00", CultureInfo.InvariantCulture) +
                "  φ=" + t.ArmPositionCount.ToString("N0", CultureInfo.InvariantCulture) + "counts" +
                "  φ̇=" + t.PhiDotRadPerSec.ToString("0.00", CultureInfo.InvariantCulture) +
                "  φ误差=" + t.FollowingErrorCount.ToString("N0", CultureInfo.InvariantCulture) + "counts" +
                "  uθ=" + t.ControlThetaOut.ToString("0.000", CultureInfo.InvariantCulture) +
                "  uφ=" + t.ControlPhiOut.ToString("0.000", CultureInfo.InvariantCulture) +
                "  cyc=" + cycleUs.ToString("0.0", CultureInfo.InvariantCulture) + "µs" +
                "  wkc=" + t.ActualWkc.ToString(CultureInfo.InvariantCulture) + "/" + t.ExpectedWkc.ToString(CultureInfo.InvariantCulture) +
                "  fault=" + (_engine == null ? t.FaultCode.ToString() : _engine.FaultCodeNow.ToString()) +
                "  drop=" + _output.TelemetryDropped.ToString(CultureInfo.InvariantCulture));
        }

        private static string Rad(double v)
        {
            return v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>摆杆角显示值：归一化到 [0,360)（内部 θ 仍是累计角，仅显示归一化）。</summary>
        private static string Deg360Value(double rad)
        {
            if (double.IsNaN(rad) || double.IsInfinity(rad)) return "n/a".PadLeft(7);
            double deg = rad * 180.0 / Math.PI % 360.0;
            if (deg < 0) deg += 360.0;
            return deg.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(7);
        }

        private static string Signed(double v)
        {
            return (v >= 0 ? "+" : string.Empty) + v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        // 增益显示统一 5 位小数：Kdφ 默认 0.0009，3 位小数会被显示成 0.001（照抄回写会吃掉裕量）。
        // 名字沿用 F3（CliSession.Tune.cs 的表头 / 增益对照也调它），实际精度是 5 位。
        // 口径：Ka/Kad/Kai/Kφ/Kφ̇/Kp/Ki/Kd/内环三项/起摆 ke 一律走它；TdLp、限幅、死区、角度、速度、
        // 能量、计数、泵幅、泵频这些不是增益，按各自单位用 F1/F2。
        private static string F3(double v) { return v.ToString("0.00000", CultureInfo.InvariantCulture); }
    }
}
