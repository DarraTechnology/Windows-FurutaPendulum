using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using FurutaPendulum.Control;
// 本仓有命名空间 FurutaPendulum.Control（Control/ 目录），裸 Control 会被解析成命名空间，
// 界面控件类型必须走别名。
using WinControl = System.Windows.Forms.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 界面 AI 注入接口（partial，测试与外部自动化专用）：本机 127.0.0.1 单行命令 + 多行应答。
    /// <para>
    /// 只在启动参数带 <c>--ui-automation-port &lt;port&gt;</c> 或环境变量
    /// <c>FURUTA_UI_AUTOMATION_PORT</c> 时开启；默认不监听任何端口，不影响生产使用。
    /// 命令行必须写成 <c>FurutaPendulum.exe --gui --ui-automation-port &lt;port&gt;</c>：
    /// 端口参数要跟在 <c>--gui</c> 之后（裸 <c>--ui-automation-port &lt;port&gt;</c> 会被参数校验拒绝，
    /// 因为启动器只接受无参数 / <c>--gui</c> / <c>--cli</c> / <c>--ui-audit</c> 四种首参数）。
    /// 命令经 <see cref="System.Windows.Forms.Control.Invoke(Delegate)"/> 回到 UI 线程执行，与鼠标点击同一条生产路径：
    /// 按钮走 <see cref="Button.PerformClick"/>，数字框写 <see cref="NumericUpDown.Value"/>，
    /// 读值直接取控件内存与引擎参数快照，不截图、不猜。
    /// </para>
    /// <para>
    /// 命令：<c>ping</c> / <c>snapshot</c> / <c>click &lt;按钮名&gt;</c> / <c>hold &lt;按钮名&gt; [毫秒]</c>（按住类按钮：
    /// 真实 OnMouseDown，到时自动 OnMouseUp，默认 500 ms）/ <c>setnum &lt;输入框名&gt; &lt;值&gt;</c> /
    /// <c>getnum &lt;输入框名&gt;</c> / <c>flush</c> / <c>dialogs</c> / <c>logtail &lt;行数&gt;</c>。
    /// 每个应答以 <c>--END--</c> 收尾；<c>click btnPidTuning</c> 异步打开模态对话框后立即返回。
    /// </para>
    /// </summary>
    public sealed partial class MainForm : Form
    {
        private UiAutomationServer _automationServer;

        /// <summary>启动期读取注入端口：命令行优先，其次环境变量。0 / 缺失 = 不开启。</summary>
        private static int ResolveAutomationPort()
        {
            string text = null;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--ui-automation-port", StringComparison.OrdinalIgnoreCase))
                    {
                        text = args[i + 1];
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("读取 --ui-automation-port 失败：" + ex.GetType().Name + " " + ex.Message);
            }
            if (string.IsNullOrEmpty(text))
                text = Environment.GetEnvironmentVariable("FURUTA_UI_AUTOMATION_PORT");
            int port;
            if (!string.IsNullOrEmpty(text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
                port > 0 && port <= 65535)
                return port;
            return 0;
        }

        /// <summary>OnLoad 末尾调用；端口未配置时什么也不做。</summary>
        private void StartUiAutomationIfRequested()
        {
            if (_automationServer != null)
                return;
            int port = ResolveAutomationPort();
            if (port <= 0)
                return;
            try
            {
                _automationServer = UiAutomationServer.Start(this, port);
                Log("[自动化] AI 注入接口已开启：127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) +
                    "（本机命令；默认关闭，仅带 --ui-automation-port 时启用）。");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "AI 注入接口启动失败（端口 " + port + "）：" + DescribeException(ex));
            }
        }

        private void StopUiAutomation()
        {
            UiAutomationServer server = _automationServer;
            _automationServer = null;
            if (server != null)
            {
                try { server.Dispose(); }
                catch (Exception ex) { Debug.WriteLine("AI 注入接口关闭异常：" + ex.GetType().Name + " " + ex.Message); }
            }
        }

        /// <summary>在 UI 线程执行一条注入命令，返回应答行（不含结束标记）。</summary>
        internal string[] AutomationExecute(string command)
        {
            if (string.IsNullOrEmpty(command))
                return new[] { "ERR empty-command" };
            string[] parts = command.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            switch (verb)
            {
                case "ping":
                    return new[] { "PONG pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                        " state=" + _engine.State + " interactive=" + Environment.UserInteractive };
                case "snapshot":
                    return BuildAutomationSnapshot();
                case "click":
                    return AutomationClick(parts.Length > 1 ? parts[1] : null);
                case "hold":
                    return AutomationHold(parts.Length > 1 ? parts[1] : null,
                        parts.Length > 2 ? parts[2] : null);
                case "setnum":
                    return AutomationSetNumber(parts.Length > 1 ? parts[1] : null,
                        parts.Length > 2 ? parts[2] : null);
                case "getnum":
                    return AutomationGetNumber(parts.Length > 1 ? parts[1] : null);
                case "flush":
                    FlushPendingParamEdits();
                    return new[] { "OK flushed" };
                case "dialogs":
                    return AutomationDialogs();
                case "logtail":
                    return AutomationLogTail(parts.Length > 1 ? parts[1] : "40");
                default:
                    return new[] { "ERR unknown-command " + Esc(verb) };
            }
        }

        private string[] AutomationClick(string name)
        {
            if (string.IsNullOrEmpty(name))
                return new[] { "ERR click 需要按钮名" };
            Button button = UiControlFinder.FindByName(this, name) as Button;
            Form ownerForm = null;
            if (button == null)
            {
                // 模态对话框里的按钮：Application.OpenForms 里逐个找（主窗体已含在列表里，但上面找过）。
                foreach (Form open in Application.OpenForms)
                {
                    if (ReferenceEquals(open, this) || open == null)
                        continue;
                    button = UiControlFinder.FindByName(open, name) as Button;
                    if (button != null)
                    {
                        ownerForm = open;
                        break;
                    }
                }
            }
            else
            {
                ownerForm = UiControlFinder.FindForm(button);
            }
            if (button == null)
                return new[] { "ERR button-not-found " + Esc(name) };

            bool wasEnabled = button.Enabled;
            string text = button.Text;
            // 内环调参按钮会打开模态对话框：在 UI 线程同步 PerformClick 会把注入线程一并卡在对话框循环里，
            // 改为投递到消息队列后立即返回；后续 setnum / click 命令经 UI 消息泵照样能进对话框。
            if (string.Equals(name, "btnPidTuning", StringComparison.Ordinal) ||
                string.Equals(name, "btnAutoTune", StringComparison.Ordinal))
            {
                if (!button.Enabled)
                    return new[] { "OK click name=" + Esc(name) + " enabled=False text=" + Esc(text) +
                        " posted=False reason=disabled" };
                button.BeginInvoke(new Action(delegate { try { UiControlFinder.Press(button); } catch (Exception) { } }));
                return new[] { "OK click name=" + Esc(name) + " enabled=True text=" + Esc(text) + " posted=True" };
            }

            UiControlFinder.Press(button);
            string formText = ownerForm == null ? string.Empty : ownerForm.Text;
            return new[] { "OK click name=" + Esc(name) + " wasEnabled=" + wasEnabled +
                " text=" + Esc(text) + " form=" + Esc(formText) };
        }

        /// <summary>
        /// 按住按钮：真实调用 Control.OnMouseDown / OnMouseUp（与鼠标一致），
        /// 用于点动这类 MouseDown 起、MouseUp 停的按钮。到时自动松开，绝不让按钮卡在按住态。
        /// </summary>
        private string[] AutomationHold(string name, string holdMsText)
        {
            if (string.IsNullOrEmpty(name))
                return new[] { "ERR hold 需要按钮名" };
            int holdMs;
            if (!int.TryParse(holdMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out holdMs) ||
                holdMs <= 0 || holdMs > 15000)
                holdMs = 500;
            Button button = UiControlFinder.FindByName(this, name) as Button;
            if (button == null)
                return new[] { "ERR button-not-found " + Esc(name) };
            if (!button.Enabled)
                return new[] { "OK hold name=" + Esc(name) + " enabled=False text=" + Esc(button.Text) + " pressed=False" };

            if (!UiControlFinder.MouseEventsAvailable)
                return new[] { "ERR mouse-events-unavailable" };
            UiControlFinder.TryRaiseMouse(button, true);

            Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(holdMs).ConfigureAwait(false);
                    if (!button.IsDisposed)
                    {
                        button.BeginInvoke(new Action(delegate
                        {
                            try { UiControlFinder.TryRaiseMouse(button, false); }
                            catch (Exception) { }
                        }));
                    }
                }
                catch (Exception) { }
            });
            return new[] { "OK hold name=" + Esc(name) + " enabled=True text=" + Esc(button.Text) +
                " pressed=True ms=" + holdMs.ToString(CultureInfo.InvariantCulture) };
        }

        private string[] AutomationSetNumber(string name, string valueText)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(valueText))
                return new[] { "ERR setnum 需要 控件名 和 数值" };
            decimal requested;
            if (!decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out requested))
                return new[] { "ERR setnum 数值不合法 " + Esc(valueText) };

            NumericUpDown box = UiControlFinder.FindByName(this, name) as NumericUpDown;
            Form ownerForm = null;
            if (box == null)
            {
                foreach (Form open in Application.OpenForms)
                {
                    if (ReferenceEquals(open, this) || open == null)
                        continue;
                    box = UiControlFinder.FindByName(open, name) as NumericUpDown;
                    if (box != null)
                    {
                        ownerForm = open;
                        break;
                    }
                }
            }
            else
            {
                ownerForm = UiControlFinder.FindForm(box);
            }
            if (box == null)
                return new[] { "ERR numeric-not-found " + Esc(name) };

            decimal oldValue = box.Value;
            decimal clamped = requested;
            if (clamped < box.Minimum) clamped = box.Minimum;
            if (clamped > box.Maximum) clamped = box.Maximum;
            bool enabled = box.Enabled;
            if (!enabled)
            {
                return new[] { "OK setnum name=" + Esc(name) + " enabled=False old=" + F(oldValue) +
                    " requested=" + F(requested) + " applied=False" };
            }
            // 与真人编辑同一路径：先取得焦点，15Hz 的标定回填才会跳过这个框（未聚焦框会被权威值覆盖）。
            bool focused = false;
            try
            {
                box.Focus();
                focused = box.Focused;
            }
            catch (Exception) { }
            box.Value = clamped;
            // 生产路径的 250 ms 去抖由 _paramApplyTimer 跑；测试要「改完立刻读控制层」时由调用方先 flush 或等待。
            return new[] { "OK setnum name=" + Esc(name) + " enabled=True old=" + F(oldValue) +
                " requested=" + F(requested) + " applied=" + F(box.Value) +
                " clamped=" + (clamped != requested) + " focused=" + focused +
                " form=" + Esc(ownerForm == null ? string.Empty : ownerForm.Text) };
        }

        private string[] AutomationGetNumber(string name)
        {
            if (string.IsNullOrEmpty(name))
                return new[] { "ERR getnum 需要控件名" };
            NumericUpDown box = UiControlFinder.FindByName(this, name) as NumericUpDown;
            if (box == null)
            {
                foreach (Form open in Application.OpenForms)
                {
                    if (ReferenceEquals(open, this) || open == null)
                        continue;
                    box = UiControlFinder.FindByName(open, name) as NumericUpDown;
                    if (box != null)
                        break;
                }
            }
            if (box == null)
                return new[] { "ERR numeric-not-found " + Esc(name) };
            return new[] { "OK getnum name=" + Esc(name) + " value=" + F(box.Value) +
                " enabled=" + box.Enabled + " min=" + F(box.Minimum) + " max=" + F(box.Maximum) +
                " decimals=" + box.DecimalPlaces };
        }

        private string[] AutomationDialogs()
        {
            List<string> lines = new List<string>();
            lines.Add("OK dialogs");
            foreach (Form open in Application.OpenForms)
            {
                if (open == null)
                    continue;
                lines.Add("dialog|name=" + Esc(open.Name) + "|title=" + Esc(open.Text) +
                    "|type=" + open.GetType().Name + "|visible=" + open.Visible +
                    "|modal=" + open.Modal + "|size=" + open.Width + "x" + open.Height);
                List<WinControl> controls = new List<WinControl>();
                UiControlFinder.CollectAll(open, controls);
                for (int i = 0; i < controls.Count; i++)
                {
                    WinControl control = controls[i];
                    string controlName = DisplayName(control);
                    if (string.IsNullOrEmpty(controlName))
                        continue;
                    lines.Add("dlgctl|form=" + Esc(open.Text) + "|name=" + Esc(controlName) +
                        "|type=" + control.GetType().Name + "|enabled=" + control.Enabled +
                        "|text=" + Esc(control.Text));
                }
            }
            return lines.ToArray();
        }

        private string[] AutomationLogTail(string countText)
        {
            int count;
            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count <= 0)
                count = 40;
            if (count > 1000)
                count = 1000;
            List<string> lines = new List<string>();
            lines.Add("OK logtail n=" + count);
            if (_logBox != null && !_logBox.IsDisposed)
            {
                string[] all = _logBox.Text.Replace("\r\n", "\n").Split('\n');
                int start = Math.Max(0, all.Length - 1 - count);
                for (int i = start; i < all.Length; i++)
                {
                    if (i == all.Length - 1 && string.IsNullOrEmpty(all[i]))
                        continue;
                    lines.Add("log|" + Esc(all[i]));
                }
            }
            return lines.ToArray();
        }

        /// <summary>
        /// 程序化验收快照：布局矩形 / 控件内存文本 / 按钮可用性 / 引擎状态与遥测 / 参数快照 / 内部错误计数。
        /// 全部取本进程内存，不做截图；由 MainFormAutomation.ps1 解析成表格与判据。
        /// </summary>
        private string[] BuildAutomationSnapshot()
        {
            List<string> lines = new List<string>();
            lines.Add("OK snapshot");
            lines.Add("opid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            lines.Add("title=" + Esc(Text));
            lines.Add("userinteractive=" + Environment.UserInteractive);
            lines.Add("form.visible=" + Visible + " form.client=" + ClientSize.Width + "x" + ClientSize.Height +
                " form.size=" + Width + "x" + Height +
                " form.minimum=" + MinimumSize.Width + "x" + MinimumSize.Height +
                " form.maximum=" + (MaximumSize.IsEmpty ? "none" : MaximumSize.Width + "x" + MaximumSize.Height) +
                " form.resizable=" + (FormBorderStyle == FormBorderStyle.Sizable));
            try
            {
                System.Drawing.Rectangle screen = Screen.FromControl(this).WorkingArea;
                lines.Add("screen.working=" + screen.Width + "x" + screen.Height);
                lines.Add("screen.bounds=" + Screen.FromControl(this).Bounds.Width + "x" +
                    Screen.FromControl(this).Bounds.Height);
            }
            catch (Exception ex)
            {
                lines.Add("screen.error=" + Esc(ex.GetType().Name));
            }
            lines.Add("state=" + _engine.State);
            lines.Add("bus.state=" + Esc(_engine.BusStateName));
            lines.Add("bus.opentry=" + _engine.OpEntryMode + ";" + Esc(_engine.OpEntryText));
            lines.Add("bus.inop=" + _engine.IsBusInOp);
            lines.Add("bus.deni=" + Esc(_engine.DeniPath));
            lines.Add("engine.estop=" + _engine.IsEStopLatched);
            lines.Add("engine.fault=" + _engine.FaultCodeNow);
            lines.Add("engine.telemetryAgeSec=" + F(_engine.TelemetryAgeSeconds));
            lines.Add("engine.pendulumCpr=" + F(_engine.PendulumCountsPerRev));
            lines.Add("engine.armCpr=" + _engine.ArmCountsPerRev);
            lines.Add("engine.armGear=" + F(_engine.ArmGearRatio));
            lines.Add("engine.armZero=" + _engine.ArmZeroCount + " set=" + _engine.ArmZeroCountSet +
                " calibrated=" + _engine.ArmZeroCalibrated);
            lines.Add("engine.pendulumZero=" + _engine.PendulumZeroRawCount + " set=" + _engine.PendulumZeroCountSet +
                " calibrated=" + _engine.PendulumZeroCalibrated);
            lines.Add("engine.calibrationReview=" + _engine.CalibrationReviewRequired +
                " checked=" + _engine.CalibrationReviewChecked +
                " cleared=" + _engine.CalibrationCleared +
                " poseSession=" + _poseSessionReviewRequired);

            // ---- 遥测（同一帧）----
            PendulumTelemetry t = _engine.Telemetry;
            bool hasTelemetry = t.TimestampTicks > 0 && t.Sequence > 0;
            lines.Add("telemetry.valid=" + hasTelemetry + " telemetry.seq=" + t.Sequence);
            if (hasTelemetry)
            {
                double ageSec = (Stopwatch.GetTimestamp() - t.TimestampTicks) / (double)Stopwatch.Frequency;
                lines.Add("telemetry.ageMs=" + F(ageSec * 1000.0));
                lines.Add("t.thetaDeg=" + F(t.ThetaRad * UiStyles.RadToDeg));
                lines.Add("t.psiDeg=" + F(t.ThetaFromUprightRad * UiStyles.RadToDeg));
                lines.Add("t.omegaDps=" + F(t.OmegaRadPerSec * UiStyles.RadToDeg));
                lines.Add("t.phiDeg=" + F(t.PhiRad * UiStyles.RadToDeg));
                lines.Add("t.phiDotDps=" + F(t.PhiDotRadPerSec * UiStyles.RadToDeg));
                lines.Add("t.phiRefDeg=" + F(t.PhiRefRad * UiStyles.RadToDeg));
                lines.Add("t.phiTargetDeg=" + F(t.PhiTargetRad * UiStyles.RadToDeg));
                lines.Add("t.energyJ=" + F(t.EnergyJ));
                lines.Add("t.energyTargetJ=" + F(t.EnergyTargetJ));
                lines.Add("t.energyDeltaJ=" + F(t.EnergyDeltaJ));
                lines.Add("t.swingPhase=" + F(t.SwingUpPhase) + " t.switchCountdown=" + t.SwitchCountdown);
                lines.Add("t.armPos=" + t.ArmPositionCount + " t.armTarget=" + t.ArmTargetCount +
                    " t.followErr=" + t.FollowingErrorCount +
                    " t.armVelPulse=" + t.ArmVelocityPulsePerSec);
                lines.Add("t.statusWord=0x" + t.StatusWord.ToString("X4") +
                    " t.errorCode=0x" + t.DriveErrorCode.ToString("X4") +
                    " t.mode=" + t.ModesDisplay.ToString(CultureInfo.InvariantCulture));
                lines.Add("t.armInOp=" + t.ArmSlaveInOp + " t.gcanInOp=" + t.GcanSlaveInOp);
                lines.Add("t.wkc=" + t.ActualWkc + "/" + t.ExpectedWkc);
                lines.Add("t.pendulumCount=" + t.PendulumCount + " t.pendulumCountCh2=" + t.PendulumCountCh2 +
                    " t.freqCh1Hz=" + t.PendulumFreqCh1Hz + " t.freqValid=" + t.FreqCrossCheckValid);
                lines.Add("t.controlOut=" + F(t.ControlThetaOut) + " t.phiOut=" + F(t.ControlPhiOut) +
                    " t.estopLatch=" + t.EStopLatch + " t.fault=" + t.FaultCode);
                lines.Add("t.saturated=" + t.IsSaturated + " t.saturatedCycles=" + t.SaturatedCycles);
            }

            // ---- 参数快照（控制层 shadow，界面显示的唯一来源）----
            PidGains pid = _engine.BalancePid;
            lines.Add("param.pid.ka=" + F(pid.Ka) + " param.pid.kad=" + F(pid.Kad) + " param.pid.kai=" + F(pid.Kai));
            lines.Add("param.pid.kphi=" + F(pid.KPhi) + " param.pid.kphidot=" + F(pid.KPhiDot) +
                " param.pid.accmax=" + F(pid.AccMax) + " param.pid.armratemax=" + F(pid.ArmRateMax));
            lines.Add("param.pid.kpphi=" + F(pid.KpPhi) + " param.pid.kiphi=" + F(pid.KiPhi) +
                " param.pid.kdphi=" + F(pid.KdPhi));
            SwingUpParams swing = _engine.SwingUpParams;
            lines.Add("param.swing.ke=" + F(swing.Ke));
            lines.Add("param.swing.extractGain=" + F(swing.ExtractGain));
            lines.Add("param.swing.amax=" + F(swing.AMax));
            lines.Add("param.swing.energyGain=" + F(swing.TargetEnergyGain));
            lines.Add("param.swing.thetaSwitchRad=" + F(swing.ThetaSwitchRad) +
                " param.swing.thetaSwitchDeg=" + F(swing.ThetaSwitchRad * 180.0 / Math.PI));
            lines.Add("param.swing.omegaSwitch=" + F(swing.OmegaSwitchRadPerSec));
            lines.Add("param.swing.captureArmRate=" + F(swing.CaptureArmRateLimitRadPerSec));
            lines.Add("param.swing.capturePendulumRate=" + F(swing.CapturePendulumRateRadPerSec));
            lines.Add("param.swing.captureModeAmpMax=" + F(swing.CaptureModeAmplitudeMaxRad));
            lines.Add("param.swing.captureStrokePerMode=" + F(swing.CaptureStrokePerModeRad));
            lines.Add("param.swing.captureStrokeMargin=" + F(swing.CaptureStrokeMarginRad));
            lines.Add("param.swing.captureAdverseRateStrokeSec=" + F(swing.CaptureAdverseRateStrokeSec));
            lines.Add("param.swing.centerBandRad=" + F(swing.CenterBandRad) +
                " param.swing.energyExtractRatio=" + F(swing.EnergyExtractRatio) +
                " param.swing.energyDeadbandRatio=" + F(swing.EnergyDeadbandRatio));
            lines.Add("param.swing.kphi=" + F(swing.KPhi) + " param.swing.kphidot=" + F(swing.KPhiDot));
            lines.Add("param.swing.pumpAmp=" + F(swing.PumpAmplitude) +
                " param.swing.pumpHz=" + F(swing.PumpFrequencyHz) +
                " param.swing.pumpPhaseRad=" + F(swing.PumpPhaseOffsetRad));
            SafetyLimits limits = _engine.SafetyLimits;
            lines.Add("param.limits.armMin=" + limits.ArmMinCount + " param.limits.armMax=" + limits.ArmMaxCount +
                " param.limits.omegaArmMax=" + F(limits.OmegaArmMaxRadPerSec) +
                " param.limits.phiErrMaxRad=" + F(limits.PhiErrMaxRad) +
                " param.limits.jeq=" + F(limits.JEq) + " param.limits.mgl=" + F(limits.Mgl) +
                " param.limits.radiusRatio=" + F(limits.RadiusRatio));

            // ---- 内部错误计数（异常路径证据）----
            lines.Add("errors.tick=" + _tickErrors + " errors.read=" + _readErrors + " errors.dial=" + _dialErrors +
                " log.dropped=" + _droppedUiLogLines);
            lines.Add("status.message=" + Esc(_statusMessageLabel == null ? string.Empty : _statusMessageLabel.Text));
            lines.Add("status.paramSaved=" + Esc(_paramSavedLabel == null ? string.Empty : _paramSavedLabel.Text));
            lines.Add("params.dirty=" + _paramsDirty + " saveButton=" + Esc(_saveParamsButton == null ? string.Empty : _saveParamsButton.Text));

            // ---- 控件树：矩形 / 文本 / 可用性（程序化判重叠与零尺寸的唯一来源）----
            List<WinControl> controls = new List<WinControl>();
            UiControlFinder.CollectAll(this, controls);
            for (int i = 0; i < controls.Count; i++)
            {
                WinControl control = controls[i];
                if (!IsAutomationInteresting(control))
                    continue;
                string name = DisplayName(control);
                string parent = control.Parent == null ? string.Empty : DisplayName(control.Parent);
                System.Drawing.Rectangle rect;
                try
                {
                    rect = RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                }
                catch (Exception)
                {
                    rect = System.Drawing.Rectangle.Empty;
                }
                lines.Add("ctl|" + Esc(name) + "|" + Esc(parent) + "|" + control.GetType().Name +
                    "|" + rect.X + "|" + rect.Y + "|" + rect.Width + "|" + rect.Height +
                    "|" + control.Visible + "|" + control.Enabled + "|" + Esc(ControlValue(control)));
            }

            // ---- 卡片与关键容器（含跨行跨列），布局裁定的直接证据 ----
            TableLayoutPanel page = UiControlFinder.FindByName(this, "mainPage") as TableLayoutPanel;
            string[] cards =
            {
                "cardStepLive", "cardPdo", "cardCurrent", "cardDial", "cardPid", "cardDevice",
                "cardAngle", "cardLog", "operationConnect", "operationManual", "operationRun",
                "operationParams", "operationCalibration", "messagePanel"
            };
            for (int i = 0; i < cards.Length; i++)
            {
                WinControl card = UiControlFinder.FindByName(this, cards[i]);
                if (card == null)
                {
                    lines.Add("card|" + cards[i] + "|missing");
                    continue;
                }
                System.Drawing.Rectangle rect = RectangleToClient(card.RectangleToScreen(card.ClientRectangle));
                int colSpan = page != null && ReferenceEquals(card.Parent, page) ? page.GetColumnSpan(card) : 0;
                int rowSpan = page != null && ReferenceEquals(card.Parent, page) ? page.GetRowSpan(card) : 0;
                int col = page != null && ReferenceEquals(card.Parent, page) ? page.GetColumn(card) : -1;
                int row = page != null && ReferenceEquals(card.Parent, page) ? page.GetRow(card) : -1;
                lines.Add("card|" + cards[i] + "|" + rect.X + "|" + rect.Y + "|" + rect.Width + "|" +
                    rect.Height + "|visible=" + card.Visible + "|col=" + col + "|row=" + row +
                    "|colSpan=" + colSpan + "|rowSpan=" + rowSpan);
            }
            return lines.ToArray();
        }

        private static bool IsAutomationInteresting(WinControl control)
        {
            if (!string.IsNullOrEmpty(control.Name) || !string.IsNullOrEmpty(control.AccessibleName))
                return true;
            return control is NumericUpDown || control is Button || control is CardPanel ||
                   control is TableLayoutPanel || control is RichTextBox;
        }

        private static string DisplayName(WinControl control)
        {
            if (!string.IsNullOrEmpty(control.AccessibleName))
                return control.AccessibleName;
            if (!string.IsNullOrEmpty(control.Name))
                return control.Name;
            return control.GetType().Name;
        }

        private static string ControlValue(WinControl control)
        {
            NumericUpDown numeric = control as NumericUpDown;
            if (numeric != null)
                return numeric.Value.ToString(CultureInfo.InvariantCulture);
            Button button = control as Button;
            if (button != null)
                return button.Text;
            RichTextBox rich = control as RichTextBox;
            if (rich != null)
                return rich.TextLength.ToString(CultureInfo.InvariantCulture) + " chars";
            return control.Text;
        }

        private static string F(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return value.ToString(CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string F(decimal value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Esc(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Replace("\r\n", " / ").Replace('\r', ' ').Replace('\n', ' ')
                .Replace('|', '/').Trim();
        }
    }
}
