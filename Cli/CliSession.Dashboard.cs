using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// CLI 实时面板（dashboard）：把 GUI 卡片上的同一份遥测 / 总线状态在终端里原地刷新。
    /// <para>
    /// 只做**显示层**：数据全部来自 <see cref="PendulumEngine.TryReadTelemetry"/> /
    /// <see cref="PendulumEngine.TryReadBusStatus"/> / 引擎公开参数属性，不另起读数通道、
    /// 不参与任何控制判据。Ctrl+C 只退出面板，不触发急停（见 CliSession.OnCancelKeyPress）。
    /// </para>
    /// </summary>
    internal sealed partial class CliSession
    {
        // 1 = dashboard 正在显示。OnCancelKeyPress 据此把 Ctrl+C 降级成「退出显示」而不是急停。
        private int _dashboardActive;

        // 标定 360° 点只存在参数文件里（引擎运行期只保留摆杆零点与一圈量）：进命令时读一次缓存。
        private string _dashPendulum360Text = "未采集";
        private string _dashPendulumPairText = "未采集";

        private struct DashboardData
        {
            public PendulumTelemetry T;
            public bool HaveTelemetry;
            public BusStatusSnapshot Bus;
            public bool HaveBus;
            public EngineDiagnostics Diag;
            public PidGains Pid;
            public SwingUpParams Swing;
            public SafetyLimits Limits;
            public EngineState State;
            public string BusName;
        }

        private CommandResult CmdDashboard(ParsedCommand command, long started)
        {
            double hz = 10.0;
            if (command.Has("hz"))
            {
                if (!command.TryDouble("hz", out hz))
                    return BadNumber("hz", command.Value("hz"), started);
                if (hz < 1.0 || hz > 20.0)
                {
                    _output.ErrorLine("invalid_arguments",
                        "dashboard --hz 必须在 [1, 20] 内（默认 10），收到 \"" + (command.Value("hz") ?? "") + "\"",
                        "invalid_arguments", ExitCodes.InvalidArguments);
                    return CommandResult.Fail(ExitCodes.InvalidArguments, "invalid_arguments");
                }
            }
            int intervalMs = Math.Max(20, (int)(1000.0 / hz + 0.5));

            // JSON 只出一帧（给脚本）：持续 JSON 流没有意义，也与既有「一条命令一个信封」冲突。
            bool once = command.Has("once") || _options.Json;
            LoadDashboardCalibration();

            Volatile.Write(ref _dashboardActive, 1);
            try
            {
                bool interactiveConsole = !Console.IsOutputRedirected;
                bool firstFrame = true;

                while (true)
                {
                    Pump();
                    if (_cancelRequested || _exitRequested)
                        return CommandResult.Fail(ExitCodes.Cancelled, "cancelled");
                    if (CommandExpired)
                        return CommandResult.Timeout();

                    DashboardData d = ReadDashboard();
                    if (_options.Json)
                    {
                        EmitDashboardJson(d, started);
                        return CommandResult.Success();
                    }

                    string frame = BuildDashboardFrame(d, hz, once);
                    if (once)
                    {
                        _output.Line(frame);
                        return CommandResult.Success();
                    }

                    if (interactiveConsole) DrawDashboardInPlace(frame, firstFrame);
                    else _output.Line(frame + Environment.NewLine);   // 重定向：每帧追加，不假装能做原地刷新

                    firstFrame = false;
                    Thread.Sleep(intervalMs);
                }
            }
            finally
            {
                Volatile.Write(ref _dashboardActive, 0);
            }
        }

        /// <summary>读一次数据源（全部走既有遥测 / 总线状态 / 公开参数属性）。</summary>
        private DashboardData ReadDashboard()
        {
            DashboardData d = new DashboardData();
            d.T = new PendulumTelemetry();
            d.HaveTelemetry = TrySnapshot(out d.T);
            d.State = _engine == null ? EngineState.Disconnected : _engine.State;
            d.BusName = _engine == null ? "None" : _engine.BusStateName;
            d.Diag = _engine == null ? new EngineDiagnostics() : _engine.Diagnostics;
            d.Pid = _engine == null ? PidGains.CreateDefault() : _engine.BalancePid;
            d.Swing = _engine == null ? SwingUpParams.CreateDefault() : _engine.SwingUpParams;
            d.Limits = _engine == null ? SafetyLimits.CreateDefault() : _engine.SafetyLimits;
            d.HaveBus = _engine != null && _engine.TryReadBusStatus(out d.Bus);
            return d;
        }

        /// <summary>
        /// 读参数文件里的摆杆 360° 点 / 配对状态。只做展示；读不到不报错，如实显示「未采集 / 读不到」。
        /// </summary>
        private void LoadDashboardCalibration()
        {
            _dashPendulum360Text = "未采集";
            _dashPendulumPairText = "未采集";
            PersistedExtras extras;
            string note;
            if (!ParamStore.TryReadExtras(ParamStore.DefaultPath, out extras, out note) ||
                extras == null || extras.Calibration == null)
            {
                _dashPendulum360Text = "读不到参数文件";
                _dashPendulumPairText = string.IsNullOrEmpty(note) ? "读不到参数文件" : "读不到参数文件（" + note + "）";
                return;
            }
            CalibrationParameters cal = extras.Calibration;
            if (cal.HasPendulum360)
                _dashPendulum360Text = cal.Pendulum360Count.ToString(CultureInfo.InvariantCulture);
            PendulumPairCheck pair = CalibrationParameters.CheckPendulumPair(cal);
            _dashPendulumPairText = pair.IsComplete
                ? "已配对（|360−0|=" + pair.SpanCounts.ToString(CultureInfo.InvariantCulture) + " counts）"
                : (pair.MissingText + (string.IsNullOrEmpty(pair.Reason) ? "" : "：" + pair.Reason));
        }

        /// <summary>
        /// 一帧文本面板。字段与 GUI 卡片一一对应；所有数值走 InvariantCulture。
        /// </summary>
        private string BuildDashboardFrame(DashboardData d, double hz, bool once)
        {
            PendulumTelemetry t = d.T;
            SwingUpParams s = d.Swing;
            SafetyLimits limits = d.Limits;
            bool have = d.HaveTelemetry;

            StringBuilder sb = new StringBuilder(2048);
            sb.Append("=== Darra Furuta Dashboard  ")
              .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append("  hz=").Append(hz.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(once ? "  （--once）" : "  （Ctrl+C 退出）").AppendLine(" ===");

            // 状态行
            string busState = d.HaveBus && !string.IsNullOrEmpty(d.Bus.MasterState)
                ? d.Bus.MasterState + (d.Bus.RealOp ? "(RealOp)" : "")
                : d.BusName;
            string link = d.HaveBus
                ? (d.Bus.LinkState ?? "—") + "  主口 " + (d.Bus.PrimaryPort ?? "—") +
                  "  副口 " + (d.Bus.SecondaryPort ?? "—")
                : "—";
            string rt = d.HaveBus && d.Bus.RtHz > 0
                ? d.Bus.RtHz.ToString(CultureInfo.InvariantCulture) + " Hz"
                : "—（未推进）";
            string slaves = d.HaveBus
                ? (d.Bus.OnlineCountKnown
                    ? d.Bus.OnlineSlaveCount.ToString(CultureInfo.InvariantCulture) + "/" +
                      d.Bus.SlaveCount.ToString(CultureInfo.InvariantCulture)
                    : "?/" + d.Bus.SlaveCount.ToString(CultureInfo.InvariantCulture))
                : "—";
            Line(sb, "状态", string.Format(CultureInfo.InvariantCulture,
                "{0} {1}  主站 {2}  WKC {3}/{4}  链路 {5}  RT {6}  从站 {7}",
                d.State, StateLabel(d.State), busState,
                t.ActualWkc, t.ExpectedWkc, link, rt, slaves));

            if (!have)
            {
                Line(sb, "遥测", "尚无快照（未连接 / 未起周期）。连上总线后这里会显示实时值。");
                if (d.State == EngineState.Disconnected || d.State == EngineState.Connected)
                    Line(sb, "下一步", "connect → enable（只看不使能也可以：connect 后就有总线 / WKC / 链路）。");
                sb.AppendLine();
                sb.AppendLine("提示：Ctrl+C 只退出面板，不影响主站；脚本用 dashboard --once。");
                return sb.ToString();
            }

            // 摆杆
            Line(sb, "摆杆", string.Format(CultureInfo.InvariantCulture,
                "θ={0,9:0.0000} rad  ψ={1,8:+0.0000;-0.0000; 0.0000} rad  ω={2,8:+0.000;-0.000; 0.000} rad/s  " +
                "ω_est={3,8:+0.000;-0.000; 0.000} rad/s",
                t.ThetaRad, t.ThetaFromUprightRad, t.OmegaRadPerSec, t.OmegaEstRadPerSec));

            // 摆臂
            Line(sb, "摆臂", string.Format(CultureInfo.InvariantCulture,
                "0x6064={0}  目标={1}  跟随={2:+0;-0;0}  φ={3,8:+0.0000;-0.0000; 0.0000} rad  " +
                "φ̇={4,8:+0.000;-0.000; 0.000} rad/s  软限位[{5},{6}]",
                t.ArmPositionCount, t.ArmTargetCount, t.FollowingErrorCount,
                t.PhiRad, t.PhiDotRadPerSec, limits.ArmMinCount, limits.ArmMaxCount));

            // 驱动器
            Line(sb, "驱动器", string.Format(CultureInfo.InvariantCulture,
                "0x6041=0x{0:X4}  0x603F=0x{1:X4}  0x6040=0x{2:X4}  模式={3}({4})  反馈={5}",
                t.StatusWord, t.DriveErrorCode, t.ControlWord, t.ModesDisplay,
                t.ModesDisplay == CiA402.ModeCsp ? "CSP" : "非 CSP",
                t.ArmFeedbackIsEstimate ? "开环估计" : "实测"));

            // 起摆
            string branch = d.State != EngineState.Swinging
                ? "—"
                : (t.SwingRecentering ? "回中" : (t.SwingBootstrapActive ? "起动" : "能量法"));
            Line(sb, "起摆", string.Format(CultureInfo.InvariantCulture,
                "分支={0,-4} t={1,6:0.00}s  E={2,6:0.000}/{3,6:0.000} J  ΔE={4:+0.000;-0.000; 0.000}  " +
                "φ̈能量={5,7:+0.0;-0.0; 0.0}  φ̈泵={6,6:+0.0;-0.0; 0.0} rad/s²",
                branch, t.SwingUpPhase, t.EnergyJ, t.EnergyTargetJ, t.EnergyDeltaJ,
                t.SwingEnergyAccelRadPerSec2, t.SwingPumpAccelRadPerSec2));

            // 加速度分解（只读）：回中 / 最终下发 / 速度参考；φ̇ref 持续单向 = 参考在跑飞。
            Line(sb, "分解", string.Format(CultureInfo.InvariantCulture,
                "φ̈回中={0,7:+0.0;-0.0; 0.0}  φ̈下发={1,7:+0.0;-0.0; 0.0} rad/s²  " +
                "φ̇ref={2,7:+0.00;-0.00; 0.00} rad/s",
                t.SwingCenteringAccelRadPerSec2,
                t.SwingCommandAccelRadPerSec2, t.SwingVelocityRefRadPerSec));

            // 泵配置
            Line(sb, "泵", string.Format(CultureInfo.InvariantCulture,
                "配置 幅={0:0.00} rad/s² @ {1:0.0000} Hz  本拍实际={2:+0.00;-0.00; 0.00} rad/s²{3}",
                s.PumpAmplitude, s.PumpFrequencyHz, t.SwingPumpAccelRadPerSec2,
                s.PumpAmplitude <= 0 ? "（幅 0 = 关泵）" : (t.SwingPumpAccelRadPerSec2 == 0 ? "（被 |ω| 门关掉 / 非起摆）" : "")));

            // 能量整形：抽取权限 / 死区 / 抽取增益 / 软中心带（都是起摆中可热改的项，现场一眼可查）
            Line(sb, "能量整形", string.Format(CultureInfo.InvariantCulture,
                "抽取权限={0:0.000}·AMax  死区={1:0.000}·E*  抽取增益={2:0.###} rad/s²/J  软中心带={3:0.0} rad",
                s.EnergyExtractRatio, s.EnergyDeadbandRatio, s.ExtractGain, s.CenterBandRad));

            // 捕获交棒：预捕获回中 / 快速提交臂速 + 摆杆角速度 / 漏桶（起摆中锁定；上限必须 ≤ 硬捕获门）
            Line(sb, "捕获交棒", string.Format(CultureInfo.InvariantCulture,
                "预回中≈{0:0.00} rad  能量余量={1:0.000}  提交臂速≤{2:0.00} rad/s（硬门 {3:0.00}）  " +
                "提交ω≤{4:0.00} rad/s（硬门 {5:0.00}）  提交净拍={6}  漏桶衰减={7}‰拍/拍（{8:0.000} 拍/拍）",
                s.CapturePrepareApproachRad, s.CapturePrepareEnergyMarginRatio,
                s.CaptureCommitArmRateRadPerSec, s.CaptureArmRateLimitRadPerSec,
                s.CaptureCommitOmegaRadPerSec, s.OmegaSwitchRadPerSec,
                s.CaptureCommitHoldCycles, s.SwitchHoldDecayPerCycle,
                s.SwitchHoldDecayPerCycle * 0.001));

            // 切换判据：逐条余量 + 模态幅值 + 双通道净拍目标。
            // SwitchCountdown 是**慢 / 快两通道剩余拍数的较小值**，慢通道已满但本拍不在提交带时
            // 会被钳成 1；它不等于「连续满足拍数」，所以这里只显示倒计时与两通道目标，不反推 held。
            if (d.State == EngineState.Swinging)
            {
                double psi = Math.Abs(t.ThetaFromUprightRad);
                double om = Math.Abs(t.OmegaEstRadPerSec);
                double phiAbs = Math.Abs(t.PhiRad);
                double phid = Math.Abs(t.PhiDotRadPerSec);
                double u = 0;
                double uMax = s.CaptureModeAmplitudeMaxRad > 0 ? s.CaptureModeAmplitudeMaxRad : 0.16;
                double wn = s.CapturePendulumRateRadPerSec > 0 ? s.CapturePendulumRateRadPerSec : 9.0416;
                if (s.CapturePendulumRateRadPerSec > 0 || limits.JEq > 0)
                {
                    double coupled = t.OmegaEstRadPerSec -
                        (limits.RadiusRatio > 0 ? limits.RadiusRatio * t.PhiDotRadPerSec : 0);
                    u = t.ThetaFromUprightRad + coupled / wn;
                }
                string tightest = TightestCriterion(psi, s.ThetaSwitchRad, om, s.OmegaSwitchRadPerSec,
                    phiAbs, s.PhiSwitchRad, phid, s.CaptureArmRateLimitRadPerSec,
                    Math.Abs(u), uMax);
                Line(sb, "切换判据", string.Format(CultureInfo.InvariantCulture,
                    "倒计时 {0} 拍（慢道净拍目标 {1} / 快道 {2}，漏桶 +1拍/−{3}×0.001 拍/拍；两通道取较紧，" +
                    "N=1 也可能是慢道已满但本拍不在提交带）  " +
                    "ψ {4:0.000}/{5:0.000}  ω {6:0.00}/{7:0.00}  φ {8:0.000}/{9:0.000}  φ̇ {10:0.00}/{11:0.00}  " +
                    "u≈{12:+0.000}/硬门 {13:0.000}  提交臂速上限 |φ̇|≤{14:0.00}  提交ω上限 |ω|≤{15:0.00}  最紧={16}",
                    t.SwitchCountdown, s.SwitchHoldCycles, s.CaptureCommitHoldCycles, s.SwitchHoldDecayPerCycle,
                    psi, s.ThetaSwitchRad, om, s.OmegaSwitchRadPerSec, phiAbs, s.PhiSwitchRad,
                    phid, s.CaptureArmRateLimitRadPerSec, u, uMax,
                    s.CaptureCommitArmRateRadPerSec, s.CaptureCommitOmegaRadPerSec, tightest));
            }
            else
            {
                Line(sb, "切换判据", "—（只有 Swinging 时才判）");
            }

            // 平衡增益：先列真正生效的限幅 / 内环 / 输出项，历史值单独一段并标明不参与控制。
            Line(sb, "平衡", string.Format(CultureInfo.InvariantCulture,
                "AccMax={0:0.#} rad/s²  ArmRateMax={1:0.##} rad/s  耦合符号(实测,只读)={2:+0;-0;0}",
                d.Pid.AccMax, d.Pid.ArmRateMax, d.Pid.Sign));
            Line(sb, "平衡内环", string.Format(CultureInfo.InvariantCulture,
                "Kp/Ki/Kd={0:0.###}/{1:0.###}/{2:0.###}  TdLp={3:0.#} ms  输出限幅=[{4:0.##},{5:0.##}]  " +
                "积分限幅={6:0.##}  死区={7:0.##}",
                d.Pid.KpPhi, d.Pid.KiPhi, d.Pid.KdPhi, d.Pid.TdLpMs,
                d.Pid.OutMin, d.Pid.OutMax, d.Pid.MaxIntegral, d.Pid.DeadBand));
            Line(sb, "平衡历史", string.Format(CultureInfo.InvariantCulture,
                "Ka={0:0.###} Kad={1:0.###} Kai={2:0.###} Kφ={3:0.###} Kφ̇={4:0.###}（历史·不参与控制，只随参数文件往返）",
                d.Pid.Ka, d.Pid.Kad, d.Pid.Kai, d.Pid.KPhi, d.Pid.KPhiDot));

            // 标定
            Line(sb, "标定", string.Format(CultureInfo.InvariantCulture,
                "摆杆0={0}  摆杆360={1}  一圈={2:0.###}  摆臂0={3}  摆臂一圈={4}  r/l={5}",
                _engine == null ? 0 : _engine.PendulumZeroRawCount,
                _dashPendulum360Text,
                _engine == null ? 0 : _engine.PendulumCountsPerRev,
                _engine == null ? 0 : _engine.ArmZeroCount,
                _engine == null ? 0 : _engine.ResolvedArmCountsPerRev,
                limits.RadiusRatio > 0
                    ? limits.RadiusRatio.ToString("0.0000", CultureInfo.InvariantCulture)
                    : "未标定（calibrate-radius）"));
            Line(sb, "标定文件", _dashPendulumPairText);

            // 循环 / 健康
            FaultCode dashFault = _engine == null ? t.FaultCode : _engine.FaultCodeNow;
            bool dashLatch = _engine == null ? t.EStopLatch : _engine.IsEStopLatched;
            double snapshotAge = _engine == null ? -1 : _engine.TelemetryAgeSeconds;
            string snapshotNote = double.IsPositiveInfinity(snapshotAge)
                ? "  [无本次会话遥测]"
                : snapshotAge > 0.5
                    ? "  [快照 " + snapshotAge.ToString("0.0", CultureInfo.InvariantCulture) + " s 前]"
                    : string.Empty;
            Line(sb, "循环", string.Format(CultureInfo.InvariantCulture,
                "周期 {0}  PDO丢帧 {1}/{2}  饱和={3}  故障={4}  急停={5}{6}",
                CycleUsText(t, d.Diag),
                d.Diag.PdoFrameLossTotal, d.Diag.PdoFrameLossConsecutive,
                t.IsSaturated ? "是" : "否",
                dashFault == FaultCode.None ? "无" : dashFault.ToString(),
                dashLatch ? "是" : "否",
                snapshotNote));

            sb.AppendLine();
            sb.Append("Ctrl+C 只退出面板，不影响主站；脚本用 dashboard --once。");
            return sb.ToString();
        }

        /// <summary>逐行加「标签 + 值」；标签按**显示宽度**（中文算 2 列）补齐到 10 列。</summary>
        private static void Line(StringBuilder sb, string label, string value)
        {
            int pad = Math.Max(0, 10 - DisplayWidth(label));
            sb.Append(label).Append(new string(' ', pad)).Append(": ").Append(value).Append('\n');
        }

        /// <summary>终端显示宽度：CJK / 全角字符按 2 列算，保证中文标签对齐。</summary>
        private static int DisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // 基本 CJK、全角标点、常用中文符号区段：按 2 列。
                width += (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) ||
                         (c >= 0xFF00 && c <= 0xFF60) ? 2 : 1;
            }
            return width;
        }

        /// <summary>找最紧的切换判据（归一化余量最小），给现场一个「现在卡在哪条」的提示。</summary>
        private static string TightestCriterion(
            double psi, double psiMax, double omega, double omegaMax,
            double phi, double phiMax, double phid, double phidMax,
            double u, double uMax)
        {
            string name = "—";
            double best = double.MaxValue;
            Consider("ψ", psi, psiMax);
            Consider("ω", omega, omegaMax);
            Consider("φ", phi, phiMax);
            Consider("φ̇", phid, phidMax);
            Consider("u", u, uMax);

            void Consider(string n, double value, double max)
            {
                if (!(max > 0) || !ControlMath.IsFinite(value)) return;
                double slack = (max - value) / max;   // 1 = 远离门限；0 = 贴线；负 = 已超
                if (slack < best) { best = slack; name = n; }
            }
            return name;
        }

        /// <summary>
        /// 原地刷新：回屏首覆盖每一行，行尾补空格擦掉旧内容。只在真控制台调用（重定向走逐帧追加）。
        /// 行数超过窗口高度时截断并给一行提示，避免滚动把「屏首」冲掉。
        /// </summary>
        private void DrawDashboardInPlace(string frame, bool firstFrame)
        {
            string[] raw = frame.Split('\n');
            int maxLines = 40;
            try
            {
                int height = Console.WindowHeight;
                if (height > 3) maxLines = height - 1;
            }
            catch (IOException ex) { Debug.WriteLine("读控制台窗口高度失败（不可用），按默认 40 行：" + ex.Message); }
            catch (PlatformNotSupportedException ex) { Debug.WriteLine("读控制台窗口高度失败（平台不支持），按默认 40 行：" + ex.Message); }
            catch (ArgumentOutOfRangeException ex) { Debug.WriteLine("读控制台窗口高度失败（取值越界），按默认 40 行：" + ex.Message); }

            int count = Math.Min(raw.Length, maxLines);
            int width = 120;
            try
            {
                int w = Console.WindowWidth;
                if (w > 20) width = Math.Min(140, w - 1);
            }
            catch (IOException ex) { Debug.WriteLine("读控制台窗口宽度失败（不可用），按默认 120 列：" + ex.Message); }
            catch (PlatformNotSupportedException ex) { Debug.WriteLine("读控制台窗口宽度失败（平台不支持），按默认 120 列：" + ex.Message); }
            catch (ArgumentOutOfRangeException ex) { Debug.WriteLine("读控制台窗口宽度失败（取值越界），按默认 120 列：" + ex.Message); }

            try
            {
                if (firstFrame) Console.Clear();
                Console.SetCursorPosition(0, 0);
                for (int i = 0; i < count; i++)
                {
                    string line = raw[i];
                    if (line.Length > width) line = line.Substring(0, width);
                    Console.Out.Write(line);
                    if (line.Length < width) Console.Out.Write(new string(' ', width - line.Length));
                    if (i < count - 1) Console.Out.Write('\n');
                }
                if (raw.Length > count)
                {
                    Console.Out.Write('\n');
                    string note = "...（窗口太矮，已截断；放大窗口或改 --once）";
                    Console.Out.Write(note);
                }
                Console.Out.Flush();
            }
            catch (IOException)
            {
                // 控制台不支持光标定位（某些重定向 / 终端）：退回逐帧追加，绝不因为显示失败中断命令。
                _output.Line(frame + Environment.NewLine);
            }
            catch (ArgumentOutOfRangeException)
            {
                _output.Line(frame + Environment.NewLine);
            }
        }

        /// <summary>JSON 一帧：字段名与文本面板一一对应，值同源。</summary>
        private void EmitDashboardJson(DashboardData d, long started)
        {
            PendulumTelemetry t = d.T;
            _output.AckWith("dashboard", true, ExitCodes.Ok, "ok",
                _clock.ElapsedMilliseconds - started, StateNow(), "dashboard",
                "state=" + d.State + "  swing=" + (d.State == EngineState.Swinging ? "yes" : "no"),
                e =>
                {
                    e.Str("engineState", d.State.ToString())
                     .Str("busState", d.BusName)
                     .Bool("telemetryAvailable", d.HaveTelemetry)
                     .Bool("busStatusAvailable", d.HaveBus);
                    if (d.HaveBus)
                    {
                        e.Str("masterState", d.Bus.MasterState).Bool("realOp", d.Bus.RealOp)
                         .Str("linkState", d.Bus.LinkState)
                         .Str("primaryPort", d.Bus.PrimaryPort)
                         .Str("secondaryPort", d.Bus.SecondaryPort)
                         .Int("rtHz", d.Bus.RtHz)
                         .Int("slaveCount", d.Bus.SlaveCount)
                         .Int("onlineSlaveCount", d.Bus.OnlineSlaveCount);
                    }
                    if (!d.HaveTelemetry) return;

                    e.Int("actualWkc", t.ActualWkc).Int("expectedWkc", t.ExpectedWkc)
                     .Num("thetaRad", t.ThetaRad).Num("thetaFromUprightRad", t.ThetaFromUprightRad)
                     .Num("omegaRadPerSec", t.OmegaRadPerSec).Num("omegaEstRadPerSec", t.OmegaEstRadPerSec)
                     .Int("armPositionCount", t.ArmPositionCount).Int("armTargetCount", t.ArmTargetCount)
                     .Int("followingErrorCount", t.FollowingErrorCount)
                     .Num("phiRad", t.PhiRad).Num("phiDotRadPerSec", t.PhiDotRadPerSec)
                     .Int("armMinCount", d.Limits.ArmMinCount).Int("armMaxCount", d.Limits.ArmMaxCount)
                     .Int("statusWord", t.StatusWord).Int("driveErrorCode", t.DriveErrorCode)
                     .Int("controlWord", t.ControlWord).Int("modesDisplay", t.ModesDisplay)
                     .Bool("armFeedbackIsEstimate", t.ArmFeedbackIsEstimate)
                     .Num("energyJ", t.EnergyJ).Num("energyTargetJ", t.EnergyTargetJ)
                     .Num("energyDeltaJ", t.EnergyDeltaJ)
                     .Int("switchCountdown", t.SwitchCountdown)
                     .Bool("swingBootstrapActive", t.SwingBootstrapActive)
                     .Bool("swingRecentering", t.SwingRecentering)
                     .Num("swingEnergyAccelRadPerSec2", t.SwingEnergyAccelRadPerSec2)
                     .Num("swingPumpAccelRadPerSec2", t.SwingPumpAccelRadPerSec2)
                     .Num("swingCenteringAccelRadPerSec2", t.SwingCenteringAccelRadPerSec2)
                     .Num("swingCommandAccelRadPerSec2", t.SwingCommandAccelRadPerSec2)
                     .Num("swingVelocityRefRadPerSec", t.SwingVelocityRefRadPerSec)
                     .Num("pumpAmplitudeRadPerSec2", d.Swing.PumpAmplitude)
                     .Num("pumpFrequencyHz", d.Swing.PumpFrequencyHz)
                     .Num("energyExtractRatio", d.Swing.EnergyExtractRatio)
                     .Num("energyDeadbandRatio", d.Swing.EnergyDeadbandRatio)
                     .Num("extractGain", d.Swing.ExtractGain)
                     .Num("centerBandRad", d.Swing.CenterBandRad)
                     .Num("capturePrepareApproachRad", d.Swing.CapturePrepareApproachRad)
                     .Num("capturePrepareEnergyMarginRatio", d.Swing.CapturePrepareEnergyMarginRatio)
                     .Num("captureCommitArmRateRadPerSec", d.Swing.CaptureCommitArmRateRadPerSec)
                     .Num("captureCommitOmegaRadPerSec", d.Swing.CaptureCommitOmegaRadPerSec)
                     .Int("captureCommitHoldCycles", d.Swing.CaptureCommitHoldCycles)
                     .Int("switchHoldDecayPerCycle", d.Swing.SwitchHoldDecayPerCycle)
                     .Num("switchHoldDecayCyclesPerCycle", d.Swing.SwitchHoldDecayPerCycle * 0.001)
                     .Int("switchHoldCycles", d.Swing.SwitchHoldCycles)
                     .Num("thetaSwitchRad", d.Swing.ThetaSwitchRad)
                     .Num("omegaSwitchRadPerSec", d.Swing.OmegaSwitchRadPerSec)
                     .Num("phiSwitchRad", d.Swing.PhiSwitchRad)
                     .Num("captureArmRateLimitRadPerSec", d.Swing.CaptureArmRateLimitRadPerSec)
                     .Num("captureModeAmplitudeMaxRad", d.Swing.CaptureModeAmplitudeMaxRad)
                     .Num("ka", d.Pid.Ka).Num("kad", d.Pid.Kad).Num("kai", d.Pid.Kai)
                     .Num("kPhi", d.Pid.KPhi).Num("kPhiDot", d.Pid.KPhiDot)
                     .Num("accMax", d.Pid.AccMax).Num("armRateMax", d.Pid.ArmRateMax)
                     .Int("pendulumZeroCount", _engine == null ? 0 : _engine.PendulumZeroRawCount)
                     .Num("pendulumCountsPerRev", _engine == null ? 0 : _engine.PendulumCountsPerRev)
                     .Int("armZeroCount", _engine == null ? 0 : _engine.ArmZeroCount)
                     .Int("armCountsPerRev", _engine == null ? 0 : _engine.ResolvedArmCountsPerRev)
                     .Num("radiusRatio", d.Limits.RadiusRatio)
                     .Str("pendulum360", _dashPendulum360Text)
                     .Str("pendulumPair", _dashPendulumPairText)
                     .Bool("saturated", t.IsSaturated)
                     .Str("faultCode", (_engine == null ? t.FaultCode : _engine.FaultCodeNow).ToString())
                     .Str("telemetryFaultCode", t.FaultCode.ToString())
                     .Bool("estopLatch", _engine == null ? t.EStopLatch : _engine.IsEStopLatched)
                     .Int("pdoFrameLossTotal", (int)d.Diag.PdoFrameLossTotal)
                     .Int("pdoFrameLossConsecutive", (int)d.Diag.PdoFrameLossConsecutive);
                });
        }
    }
}
