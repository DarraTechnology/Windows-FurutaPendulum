using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 自动调参对话框与主界面之间的接线契约。由主界面实现：
    /// 写参数时同时更新控制层与界面影子，采用时负责落盘，失败 / 取消时把调参前的值写回去。
    /// 对话框只通过这四个回调改参数，自己不做任何持久化，也不直接读界面控件。
    /// </summary>
    public interface IAutoTuneHost
    {
        /// <summary>引擎实例（只调用公开 API）。</summary>
        PendulumEngine Engine { get; }

        /// <summary>当前生效的平衡增益（打开对话框时的基线）。</summary>
        PidGains CurrentPid { get; }

        /// <summary>当前生效的起摆参数（打开对话框时的基线）。</summary>
        SwingUpParams CurrentSwing { get; }

        /// <summary>把候选参数写进控制层与界面影子；失败返回 false 并给中文原因。</summary>
        bool ApplyTuningCandidate(PidGains pid, SwingUpParams swing, out string error);

        /// <summary>采用成功后保存参数文件（由主界面走 ParamStore.Save）。false = 保存失败，error 给中文原因。</summary>
        bool SaveTuningCandidate(out string error);

        /// <summary>回滚到调参前的值（写回控制层与界面）。</summary>
        void RestoreTuningBaseline(PidGains pid, SwingUpParams swing);
    }

    /// <summary>
    /// 自动调参对话框：前置检查 → 采集自由摆动 → 识别 → 计算候选增益 → 试跑验收 → 采用并保存。
    /// <para>
    /// 采集只依靠控制层已有的样本累计（仅在「已使能」且未起摆 / 未平衡时累计），界面不做第二套计算；
    /// 试跑是本对话框唯一的危险动作，必须二次确认，且结束一定受控停 + 等回停稳，失败一律回滚。
    /// </para>
    /// </summary>
    internal sealed class AutoTuneDialog : Form
    {
        private const int TickMs = 100;
        private const int CollectSeconds = 8;
        private const int EnableTimeoutMs = 10000;
        private const int StopTimeoutMs = 3000;

        private const double TrialHoldSeconds = 5.0;
        private const double TrialMaxDeviationDeg = 15.0;
        private const double TrialDivergeDeviationDeg = 30.0;
        /// <summary>试跑起摆超时的下限 [s]：低于它连一次慢起摆都跑不完，不能当验收窗口。</summary>
        private const double TrialSwingTimeoutFloorSeconds = 60.0;
        /// <summary>在控制层起摆超时（swingup.timeout_ms，默认 180 s）之上再留的余量 [s]。</summary>
        private const double TrialSwingTimeoutMarginSeconds = 20.0;

        /// <summary>
        /// 试跑允许的最长起摆时间：**跟控制层自己的 swingup.timeout_ms 走**（默认 180 s，本台架实测
        /// 从静止到捕获窗要 150 s 量级），再加 20 s 余量。写死 40 s 会让正常慢起摆全部误判超时。
        /// </summary>
        private double TrialSwingTimeoutSeconds
        {
            get
            {
                int configuredMs = _candidate != null
                    ? _candidate.Swing.TimeoutMs
                    : _baselineSwing.TimeoutMs;
                if (configuredMs <= 0)
                    configuredMs = SwingUpParams.CreateDefault().TimeoutMs;
                double seconds = configuredMs / 1000.0 + TrialSwingTimeoutMarginSeconds;
                return seconds < TrialSwingTimeoutFloorSeconds ? TrialSwingTimeoutFloorSeconds : seconds;
            }
        }

        private enum Phase
        {
            Idle,
            WaitingForEnable,
            Collecting,
            WaitingForStop,
            TrialRunning
        }

        private enum EnablePurpose
        {
            Collect,
            Trial
        }

        private enum StopPurpose
        {
            BeforeTrial,
            AfterTrial
        }

        private readonly PendulumEngine _engine;
        private readonly PidGains _baselinePid;
        private readonly SwingUpParams _baselineSwing;
        private readonly IAutoTuneHost _host;

        private readonly Timer _tick;
        private readonly TextBox _statusBox;
        private readonly StringBuilder _log = new StringBuilder();
        private string _progress = string.Empty;

        private Phase _phase = Phase.Idle;
        private EnablePurpose _enablePurpose = EnablePurpose.Collect;
        private StopPurpose _stopPurpose = StopPurpose.AfterTrial;
        private bool _precheckPassed;
        private IdentificationResult _identification;
        private AutoTuneCandidate _candidate;
        private long _deadlineUtcMs;
        private long _trialStartUtcMs;
        private bool _trialBalancing;
        private long _balanceStartUtcMs;
        private double _maxDeviationDeg;
        private int _deviationSamples;
        private bool _trialSucceeded;
        private bool _dirtyBaseline;
        private string _trialResult = string.Empty;
        private bool _closing;

        /// <summary>
        /// 构造对话框。baselinePid / baselineSwing 是打开时的当前值：整定以它为输入，回滚也回到它。
        /// </summary>
        public AutoTuneDialog(PendulumEngine engine, PidGains baselinePid, SwingUpParams baselineSwing, IAutoTuneHost host)
        {
            _engine = engine;
            _baselinePid = baselinePid;
            _baselineSwing = baselineSwing;
            _host = host;

            Text = "自动调参";
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = UiStyles.FormBack;
            ClientSize = new Size(660, 540);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = UiStyles.FormBack
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));
            Controls.Add(root);

            Label hint = UiStyles.MakeHint(
                "流程：前置检查 → 采集自由摆动（8 秒）→ 识别 → 计算候选增益 → 试跑验收 → 采用并保存。\r\n"
                + "平衡环由整套状态反馈接管：外环 Ka / Kad 不再参与平衡，本流程不写入它们，只写真正生效的起摆泵参数。\r\n"
                + "试跑会先写入候选参数并让设备自动起摆：确认台架周围无人、无遮挡，急停可随时按下。", 8.5f);
            hint.Margin = new Padding(4, 2, 4, 6);
            root.Controls.Add(hint, 0, 0);

            _statusBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = UiStyles.ValueText,
                Font = UiStyles.UiFont(9f),
                TabStop = false,
                Margin = new Padding(4, 2, 4, 8)
            };
            _statusBox.AccessibleName = "txtAutoTuneStatus";
            root.Controls.Add(_statusBox, 0, 1);

            TableLayoutPanel buttons = UiStyles.MakeGrid(4, 2);

            Button precheck = UiStyles.MakeButton("前置检查", Color.Empty, PrecheckClicked, Color.Empty);
            precheck.AccessibleName = "btnAutoPrecheck";
            Button collect = UiStyles.MakeButton("开始采集自由摆动", Color.Empty, CollectClicked, Color.Empty);
            collect.AccessibleName = "btnAutoCollect";
            Button identify = UiStyles.MakeButton("识别", Color.Empty, IdentifyClicked, Color.Empty);
            identify.AccessibleName = "btnAutoIdentify";
            Button compute = UiStyles.MakeButton("计算候选增益", Color.Empty, ComputeClicked, Color.Empty);
            compute.AccessibleName = "btnAutoCompute";
            Button trial = UiStyles.MakeButton("试跑验收", UiStyles.WarnOrange, TrialClicked, Color.White);
            trial.AccessibleName = "btnAutoTrial";
            Button adopt = UiStyles.MakePrimaryButton("采用并保存", AdoptClicked, 8.5f);
            adopt.AccessibleName = "btnAutoAdopt";
            Button rollback = UiStyles.MakeButton("回滚", Color.Empty, RollbackClicked, Color.Empty);
            rollback.AccessibleName = "btnAutoRollback";
            Button close = UiStyles.MakeButton("关闭", Color.Empty, CloseClicked, Color.Empty);
            close.AccessibleName = "btnAutoClose";

            buttons.Controls.Add(precheck, 0, 0);
            buttons.Controls.Add(collect, 1, 0);
            buttons.Controls.Add(identify, 2, 0);
            buttons.Controls.Add(compute, 3, 0);
            buttons.Controls.Add(trial, 0, 1);
            buttons.Controls.Add(adopt, 1, 1);
            buttons.Controls.Add(rollback, 2, 1);
            buttons.Controls.Add(close, 3, 1);
            root.Controls.Add(buttons, 0, 2);

            _tick = new Timer();
            _tick.Interval = TickMs;
            _tick.Tick += TickHandler;

            LogLine("自动调参：按「前置检查 → 采集自由摆动 → 识别 → 计算候选增益 → 试跑验收 → 采用并保存」依次执行。");
            LogLine("提示：采集与试跑期间请勿触碰关节；试跑结束后会自动受控停。");
        }

        // ================================================================ 步骤 1：前置检查

        private void PrecheckClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;

            try
            {
                StringBuilder problems = new StringBuilder();
                EngineState state = SafeState();
                if (state == EngineState.Disconnected)
                    problems.AppendLine("· 总线未连接：先在主界面「连接」设备，连接成功后再回来。");

                int armCountsPerRev = 0;
                try
                {
                    armCountsPerRev = _engine.ResolvedArmCountsPerRev;
                }
                catch (Exception ex)
                {
                    problems.AppendLine("· 读关节每圈计数失败：" + Describe(ex));
                }
                if (armCountsPerRev <= 0)
                    problems.AppendLine("· 关节每圈计数未标定（=0）：先在设备卡做一次「一圈标定」，或填入每转计数。");

                double pendulumCountsPerRev = 0;
                try
                {
                    pendulumCountsPerRev = _engine.PendulumCountsPerRev;
                }
                catch (Exception ex)
                {
                    problems.AppendLine("· 读杆每转计数失败：" + Describe(ex));
                }
                if (!(pendulumCountsPerRev > 0))
                    problems.AppendLine("· 杆每转计数未标定（=0）：先填杆每转计数（线数 × 4 × 传动比）。");

                bool limitsRead = false;
                SafetyLimits limits = default(SafetyLimits);
                try
                {
                    limits = _engine.SafetyLimits;
                    limitsRead = true;
                }
                catch (Exception ex)
                {
                    problems.AppendLine("· 读安全限值失败：" + Describe(ex));
                }
                if (limitsRead)
                {
                    string limitReason;
                    if (!limits.IsValid(out limitReason))
                    {
                        problems.AppendLine("· 安全限值不合法：" + limitReason);
                    }
                    // 软限位只认绝对 counts，没有「角度行程是否合理」这一层判断：
                    // 行程多大由现场机械决定，软件不假设比例。
                }

                bool eStopLatched = false;
                try
                {
                    eStopLatched = _engine.IsEStopLatched;
                }
                catch (Exception ex)
                {
                    problems.AppendLine("· 读急停锁存失败：" + Describe(ex));
                }
                if (eStopLatched)
                    problems.AppendLine("· 急停锁存未清：先确认急停已松开，再在主界面点「停止 / 故障复位」。");

                _precheckPassed = problems.Length == 0;
                if (_precheckPassed)
                {
                    LogLine("[前置检查] 通过：关节 " + armCountsPerRev.ToString("N0", CultureInfo.InvariantCulture)
                        + " count/圈，杆 " + F2(pendulumCountsPerRev) + " count/圈，状态「" + StateName(state) + "」。");
                    LogLine("            下一步：点「开始采集自由摆动」。");
                }
                else
                {
                    LogLine("[前置检查] 未通过：");
                    LogLine(problems.ToString().TrimEnd());
                    LogLine("            按上面逐项补齐后再点「前置检查」；补齐之前后续步骤不会执行。");
                }
            }
            catch (Exception ex)
            {
                _precheckPassed = false;
                LogLine("[前置检查] 异常：" + Describe(ex));
            }
        }

        // ================================================================ 步骤 2：采集自由摆动

        private void CollectClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;
            if (!_precheckPassed)
            {
                SetProgress("先点「前置检查」并通过，再开始采集。");
                return;
            }

            EngineState state = SafeState();
            if (state == EngineState.Enabled)
            {
                StartCollecting();
                return;
            }
            if (state == EngineState.Connected || state == EngineState.Stopped)
            {
                StartEnable(EnablePurpose.Collect);
                return;
            }
            if (state == EngineState.Swinging || state == EngineState.Balancing)
            {
                SetProgress("当前「" + StateName(state) + "」不能采集：先点「停止」回到「已使能」或「已停」，再开始采集。");
                return;
            }
            if (state == EngineState.Fault)
            {
                SetProgress("当前故障，不能采集：先点「停止 / 故障复位」并排除故障，再开始采集。");
                return;
            }
            SetProgress("总线未连接，不能采集：先连接设备。");
        }

        private void StartEnable(EnablePurpose purpose)
        {
            _enablePurpose = purpose;
            try
            {
                _engine.Enable();
            }
            catch (Exception ex)
            {
                FailForPurpose("使能请求异常：" + Describe(ex));
                return;
            }
            _phase = Phase.WaitingForEnable;
            _deadlineUtcMs = NowMs() + EnableTimeoutMs;
            StartTimer();
            SetProgress("已请求使能，等待进入「已使能」（最长 " + (EnableTimeoutMs / 1000) + " 秒）…");
        }

        private void StartCollecting()
        {
            _phase = Phase.Collecting;
            _deadlineUtcMs = NowMs() + CollectSeconds * 1000L;
            StartTimer();
            SetProgress("采集中… 剩余 " + CollectSeconds.ToString(CultureInfo.InvariantCulture)
                + " 秒。请让杆从任意角度自由摆动，不要碰关节。");
        }

        // ================================================================ 步骤 3：识别

        private void IdentifyClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;
            if (!_precheckPassed)
            {
                SetProgress("先点「前置检查」并通过，再做识别。");
                return;
            }
            RunIdentify();
        }

        private void RunIdentify()
        {
            _phase = Phase.Idle;
            StopTimer();
            ClearProgress();

            IdentificationResult result;
            try
            {
                result = _engine.IdentifyNow();
            }
            catch (Exception ex)
            {
                LogLine("[识别] 失败：" + Describe(ex));
                return;
            }

            _identification = result;
            _candidate = null;
            _trialSucceeded = false;

            if (!result.Sufficient)
            {
                string why = string.IsNullOrEmpty(result.InsufficientReason)
                    ? "控制层没有给出具体原因"
                    : result.InsufficientReason;
                LogLine("[识别] 样本不足：" + why);
                LogLine("        请延长自由摆动采集时间（可再点一次「开始采集自由摆动」）或加大初始摆动角度，然后重试。");
                return;
            }
            LogLine("[识别] 通过：f_n=" + F3(result.NaturalFreqHz) + " Hz，阻尼比=" + F4(result.DampingZeta)
                + "，置信度=" + F2(result.Confidence) + "。");
            LogLine("        样本：半周期 " + result.HalfPeriodSamples.ToString(CultureInfo.InvariantCulture)
                + " / 极值 " + result.ExtremaSamples.ToString(CultureInfo.InvariantCulture)
                + " / 捕获记录 " + result.CaptureSamples.ToString(CultureInfo.InvariantCulture) + "。");
            LogLine("        下一步：点「计算候选增益」。");
        }

        // ================================================================ 步骤 4：计算候选增益

        private void ComputeClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;
            if (!_identification.Sufficient)
            {
                SetProgress("先完成「识别」，样本不足时无法计算候选增益。");
                return;
            }

            AutoTuneCandidate candidate;
            string reason;
            // 耦合比取控制层当前的标定值：Ka 已是历史参数，候选里不再做结构下界校验，
            // 这个参数只为调用签名保持不变（旧调用方仍按原样传入）。
            double couplingAbs = Math.Abs(_engine.SafetyLimits.RadiusRatio);
            if (!AutoTuner.TryCompute(_identification, _baselinePid, _baselineSwing, couplingAbs,
                    out candidate, out reason))
            {
                _candidate = null;
                _trialSucceeded = false;
                LogLine("[计算] 未产出候选：" + reason);
                return;
            }

            // 平衡环已由整套状态反馈接管：Ka / Kad / Kai 不再参与平衡，整定它们没有可观察效果。
            // 候选里的这三项原样沿用打开对话框时的值，本次只把真正生效的起摆泵写下去。
            _candidate = candidate;
            _trialSucceeded = false;
            LogLine("[计算] 候选参数（可逐项复核）：");
            LogLine(candidate.Summary);
            LogLine("[计算] 平衡外环 Ka / Kad 已跳过：平衡环由整套状态反馈接管，这两项不参与平衡，"
                + "本次只写入起摆泵参数（候选里的外环值仅作记录）。");
            LogLine("        下一步：点「试跑验收」（会先写入候选，再自动起摆）。");
        }

        // ================================================================ 步骤 5：试跑验收

        private void TrialClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;
            if (_candidate == null)
            {
                SetProgress("先点「计算候选增益」得到候选参数，再试跑。");
                return;
            }
            if (!_precheckPassed)
            {
                SetProgress("先点「前置检查」并通过，再试跑。");
                return;
            }

            // 起摆从自然下垂位开始才有意义：杆若还停在顶部或大幅摆动，试跑结果不可解释。
            string poseDetail;
            if (!PendulumLooksHanging(out poseDetail))
            {
                SetProgress("试跑前请把杆放回自然下垂位并静止（当前" + poseDetail + "），再点「试跑验收」。");
                return;
            }
            // 起摆目标会被软限位钳位；关节已经在限位外时起摆会先把关节高速拖回边界，不能当试跑起点。
            if (!ArmInsideLimits(out poseDetail))
            {
                SetProgress("试跑前请先把关节点动回软限位内（当前" + poseDetail + "），再点「试跑验收」。");
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                "试跑会先把候选参数写入控制层，再让设备自动起摆，最长约 " + F0(TrialSwingTimeoutSeconds) + " 秒。\r\n"
                + "（上限跟控制层 swingup.timeout_ms 走；本台架从静止甩到保持位实测要 150 s 量级，慢是正常的。）\r\n\r\n"
                + "请先确认：\r\n"
                + "· 台架周围无人、无遮挡\r\n"
                + "· 急停可随时按下\r\n"
                + "· 关节行程范围内没有异物\r\n\r\n"
                + "仍要继续吗？",
                "试跑验收", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                SetProgress("已取消试跑：参数没有改动。");
                return;
            }

            string error;
            bool applied;
            try
            {
                applied = _host.ApplyTuningCandidate(_candidate.Pid, _candidate.Swing, out error);
            }
            catch (Exception ex)
            {
                applied = false;
                error = Describe(ex);
            }
            if (!applied)
            {
                LogLine("[试跑] 未写入候选参数：" + (string.IsNullOrEmpty(error) ? "主界面没有给出原因" : error)
                    + "；试跑中止（参数未改动）。");
                return;
            }

            _dirtyBaseline = true;
            _trialSucceeded = false;

            // 写进去的必须就是试跑要验证的那一组：读回控制层，不一致时中止并回滚，
            // 免得拿另一组参数跑完还宣称验证了候选。
            string verifyDetail;
            if (!VerifyAppliedCandidate(out verifyDetail))
            {
                bool restored = RestoreBaseline();
                LogLine("[试跑] 候选参数未按预期生效：" + verifyDetail + "；试跑中止"
                    + (restored ? "，已回滚到调参前的值。" : "，回滚失败，请手动恢复参数。"));
                return;
            }

            LogLine("[试跑] 候选参数已写入控制层并读回一致，准备起摆。");

            EngineState state = SafeState();
            if (state == EngineState.Enabled)
            {
                StartTrialMotion();
                return;
            }
            if (state == EngineState.Swinging || state == EngineState.Balancing)
            {
                BeginStopBeforeTrial();
                return;
            }
            if (state == EngineState.Connected || state == EngineState.Stopped)
            {
                StartEnable(EnablePurpose.Trial);
                return;
            }
            FailTrialAttempt("当前「" + StateName(state) + "」不能试跑：先回主界面处理连接 / 故障。");
        }

        private void BeginStopBeforeTrial()
        {
            _phase = Phase.WaitingForStop;
            _stopPurpose = StopPurpose.BeforeTrial;
            _deadlineUtcMs = NowMs() + StopTimeoutMs;
            try
            {
                _engine.Stop();
            }
            catch (Exception ex)
            {
                FailTrialAttempt("停止请求异常：" + Describe(ex));
                return;
            }
            StartTimer();
            SetProgress("正在停止当前运动，停稳后再起摆…");
        }

        private void StartTrialMotion()
        {
            if (_candidate == null)
            {
                FailTrialAttempt("没有候选参数，试跑中止。");
                return;
            }

            _trialStartUtcMs = NowMs();
            _trialBalancing = false;
            _balanceStartUtcMs = 0;
            _maxDeviationDeg = 0;
            _deviationSamples = 0;
            _trialResult = string.Empty;

            try
            {
                _engine.SwingUp();
            }
            catch (Exception ex)
            {
                BeginStopAfterTrial(false, "试跑失败：起摆请求异常 " + Describe(ex));
                return;
            }

            _phase = Phase.TrialRunning;
            StartTimer();
            SetProgress("已请求起摆，等待进入「起摆 / 平衡」…");
        }

        private void TickTrial()
        {
            EngineState state = SafeState();
            long elapsedMs = NowMs() - _trialStartUtcMs;

            if (state == EngineState.Fault)
            {
                BeginStopAfterTrial(false, "试跑失败：进入故障状态（" + DescribeFault() + "）。");
                return;
            }

            PendulumTelemetry telemetry;
            bool hasTelemetry = TryReadTelemetry(out telemetry);

            if (state == EngineState.Balancing)
            {
                if (!_trialBalancing)
                {
                    _trialBalancing = true;
                    _balanceStartUtcMs = NowMs();
                    _maxDeviationDeg = 0;
                    _deviationSamples = 0;
                }

                if (hasTelemetry && ControlMath.IsFinite(telemetry.ThetaFromUprightRad))
                {
                    double deviationDeg = Math.Abs(telemetry.ThetaFromUprightRad) * (180.0 / Math.PI);
                    _deviationSamples++;
                    if (deviationDeg > _maxDeviationDeg)
                        _maxDeviationDeg = deviationDeg;
                    if (deviationDeg > TrialDivergeDeviationDeg)
                    {
                        BeginStopAfterTrial(false, "试跑失败：平衡后偏差 " + F1(deviationDeg) + "° 超过 "
                            + F1(TrialDivergeDeviationDeg) + "°，判定发散。");
                        return;
                    }
                }

                double heldSeconds = (NowMs() - _balanceStartUtcMs) / 1000.0;
                if (heldSeconds >= TrialHoldSeconds)
                {
                    if (_deviationSamples == 0)
                    {
                        BeginStopAfterTrial(false, "试跑无法判定：平衡保持期间没有读到遥测快照。");
                        return;
                    }
                    if (_maxDeviationDeg <= TrialMaxDeviationDeg)
                    {
                        double riseSeconds = (_balanceStartUtcMs - _trialStartUtcMs) / 1000.0;
                        BeginStopAfterTrial(true, "试跑通过：进平衡用时 " + F1(riseSeconds) + " s，保持 "
                            + F1(heldSeconds) + " s，最大偏差 " + F1(_maxDeviationDeg) + "°。");
                        return;
                    }
                    BeginStopAfterTrial(false, "试跑失败：保持期内最大偏差 " + F1(_maxDeviationDeg)
                        + "° 超过 " + F1(TrialMaxDeviationDeg) + "°。");
                    return;
                }

                SetProgress("平衡保持中：已 " + F1(heldSeconds) + " s / 需 " + F1(TrialHoldSeconds)
                    + " s；最大偏差 " + F1(_maxDeviationDeg) + "° / 上限 " + F1(TrialMaxDeviationDeg) + "°。");
                return;
            }

            if (state == EngineState.Swinging)
            {
                if (_trialBalancing)
                {
                    BeginStopAfterTrial(false, "试跑失败：平衡丢失后回到起摆。");
                    return;
                }
                if (elapsedMs > (long)(TrialSwingTimeoutSeconds * 1000.0))
                {
                    BeginStopAfterTrial(false, "试跑超时：起摆 " + F1(TrialSwingTimeoutSeconds) + " 秒仍未进入平衡。");
                    return;
                }
                SetProgress("起摆中… 已 " + F1(elapsedMs / 1000.0) + " s（上限 " + F1(TrialSwingTimeoutSeconds) + " s）。");
                return;
            }

            // 其余状态（已使能 / 已停 / 已连接 / 未连接）：起摆请求没有生效。
            if (elapsedMs > 2000)
            {
                BeginStopAfterTrial(false, "试跑失败：起摆请求后状态停在「" + StateName(state) + "」，没有进入起摆。");
                return;
            }
            SetProgress("已请求起摆，等待状态切换…");
        }

        private void BeginStopAfterTrial(bool success, string message)
        {
            _trialSucceeded = success;
            _trialResult = message;
            _phase = Phase.WaitingForStop;
            _stopPurpose = StopPurpose.AfterTrial;
            _deadlineUtcMs = NowMs() + StopTimeoutMs;

            try
            {
                if (SafeState() != EngineState.Disconnected)
                    _engine.Stop();
            }
            catch (Exception ex)
            {
                _trialResult = message + "（停止请求异常：" + Describe(ex) + "）";
            }

            StartTimer();
            SetProgress("试跑结束，正在停止设备…");
        }

        private void FinishTrial(string stopNote)
        {
            _phase = Phase.Idle;
            StopTimer();
            ClearProgress();

            string note = string.IsNullOrEmpty(stopNote) ? string.Empty : "（" + stopNote + "）";
            if (_trialSucceeded)
            {
                LogLine("[试跑] " + _trialResult + note);
                LogLine("        候选参数保持生效：点「采用并保存」写进参数文件，或点「回滚」恢复调参前的值。");
                return;
            }

            bool restored = RestoreBaseline();
            LogLine("[试跑] " + _trialResult + note
                + (restored ? " 已回滚到调参前的值。" : " 回滚失败，请手动恢复参数。"));
            _trialSucceeded = false;
        }

        // ================================================================ 步骤 6 / 7：采用、回滚

        private void AdoptClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;
            if (!_trialSucceeded || _candidate == null)
            {
                SetProgress("先通过「试跑验收」，再采用并保存。");
                return;
            }

            try
            {
                string saveError;
                if (!_host.SaveTuningCandidate(out saveError))
                {
                    // 保存失败不能"假装已采用"：候选仍在运行期生效，让用户修好写入条件后重试或回滚。
                    LogLine("[采用] 保存失败：" + (string.IsNullOrEmpty(saveError)
                        ? "主界面没有给出原因，见运行日志。"
                        : saveError) + "；候选仍在运行期生效，可重试「采用并保存」或点「回滚」。");
                    return;
                }
            }
            catch (Exception ex)
            {
                LogLine("[采用] 保存异常：" + Describe(ex) + "；候选仍在运行期生效，可重试或回滚。");
                return;
            }

            // 已落盘：此后不再自动回滚，避免参数文件与运行期不一致。
            _dirtyBaseline = false;
            _trialSucceeded = false;
            LogLine("[采用] 已采用并保存：候选参数已写入参数文件（文件路径由主界面显示）。");
        }

        private void RollbackClicked(object sender, EventArgs e)
        {
            if (!GuardReady() || !GuardIdle())
                return;
            if (!_dirtyBaseline)
            {
                SetProgress("当前没有待回滚的候选参数。");
                return;
            }
            if (RestoreBaseline())
            {
                _trialSucceeded = false;
                LogLine("[回滚] 已回滚到调参前的值。");
            }
        }

        private bool RestoreBaseline()
        {
            if (!_dirtyBaseline)
                return true;
            try
            {
                _host.RestoreTuningBaseline(_baselinePid, _baselineSwing);
                _dirtyBaseline = false;
                return true;
            }
            catch (Exception ex)
            {
                LogLine("[回滚] 失败：" + Describe(ex));
                return false;
            }
        }

        /// <summary>
        /// 读回控制层，确认候选里真正生效的项确实写进去了：加速度限幅、参考角速度限幅、
        /// 内环 φ 三增益、微分低通、输出 / 积分限幅与死区，以及起摆泵三项。
        /// Ka / Kad / Kai 等历史字段不参与比对 —— 控制层会按内置默认校正历史值，
        /// 拿它们当验收条件会让试跑因为历史值不匹配而中止。
        /// 容差按界面显示精度放宽（泵频 2 位 / 泵幅 1 位），超出容差说明实际运行的不是候选，试跑必须中止。
        /// </summary>
        private bool VerifyAppliedCandidate(out string detail)
        {
            detail = null;
            if (_candidate == null)
            {
                detail = "候选参数已丢失";
                return false;
            }

            try
            {
                PidGains applied = _engine.BalancePid;
                // 只比对真正生效的项。历史字段（Ka / Kad / Kai / Kφ / Kφ̇ / Kp / Ki / Kd）
                // 由控制层按内置默认校正，不参与验收。
                if (!NearlyEqual(applied.AccMax, _candidate.Pid.AccMax, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.ArmRateMax, _candidate.Pid.ArmRateMax, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.KpPhi, _candidate.Pid.KpPhi, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.KiPhi, _candidate.Pid.KiPhi, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.KdPhi, _candidate.Pid.KdPhi, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.TdLpMs, _candidate.Pid.TdLpMs, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.OutMin, _candidate.Pid.OutMin, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.OutMax, _candidate.Pid.OutMax, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.OutRateLimit, _candidate.Pid.OutRateLimit, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.MaxIntegral, _candidate.Pid.MaxIntegral, 1e-6, 1e-9) ||
                    !NearlyEqual(applied.DeadBand, _candidate.Pid.DeadBand, 1e-6, 1e-9))
                {
                    detail = "控制层生效的限幅 / 内环 / 输出项与候选不一致（试跑中止）："
                        + "实际 AccMax=" + F1(applied.AccMax) + " 参考角速度限幅=" + F2(applied.ArmRateMax)
                        + " 内环 Kp/Ki/Kd=" + ControlMath.Gain(applied.KpPhi) + "/"
                        + ControlMath.Gain(applied.KiPhi) + "/" + ControlMath.Gain(applied.KdPhi)
                        + " TdLp=" + F1(applied.TdLpMs) + " ms 输出限幅=[" + F2(applied.OutMin) + ","
                        + F2(applied.OutMax) + "] 死区=" + F2(applied.DeadBand)
                        + "；候选 AccMax=" + F1(_candidate.Pid.AccMax) + " 参考角速度限幅="
                        + F2(_candidate.Pid.ArmRateMax) + " 内环 Kp/Ki/Kd=" + ControlMath.Gain(_candidate.Pid.KpPhi)
                        + "/" + ControlMath.Gain(_candidate.Pid.KiPhi) + "/" + ControlMath.Gain(_candidate.Pid.KdPhi)
                        + " TdLp=" + F1(_candidate.Pid.TdLpMs) + " ms 输出限幅=[" + F2(_candidate.Pid.OutMin)
                        + "," + F2(_candidate.Pid.OutMax) + "] 死区=" + F2(_candidate.Pid.DeadBand) + "。";
                    return false;
                }

                SwingUpParams appliedSwing = _engine.SwingUpParams;
                // 泵三项的容差按界面输入精度放宽：泵幅框 1 位小数、泵频框 2 位小数，
                // 主界面若经输入框回写会有对应的舍入；这里只拦「根本没写进去 / 写错组」。
                if (!NearlyEqual(appliedSwing.Ke, _candidate.Swing.Ke, 1e-4, 1e-9) ||
                    !NearlyEqual(appliedSwing.PumpFrequencyHz, _candidate.Swing.PumpFrequencyHz, 1e-2, 1e-6) ||
                    !NearlyEqual(appliedSwing.PumpAmplitude, _candidate.Swing.PumpAmplitude, 5e-2, 1e-3))
                {
                    detail = "控制层泵 k_e/泵频/泵幅=" + ControlMath.Gain(appliedSwing.Ke) + "/"
                        + F3(appliedSwing.PumpFrequencyHz) + "/" + F4(appliedSwing.PumpAmplitude)
                        + "，候选=" + ControlMath.Gain(_candidate.Swing.Ke) + "/"
                        + F3(_candidate.Swing.PumpFrequencyHz) + "/" + F4(_candidate.Swing.PumpAmplitude);
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = "读回控制层失败：" + Describe(ex);
                return false;
            }
            return true;
        }

        /// <summary>相对 / 绝对容差比较；完全相等直接通过，NaN / Infinity 一律判不等。</summary>
        private static bool NearlyEqual(double a, double b, double relativeTolerance, double absoluteTolerance)
        {
            if (a == b)
                return true;
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b))
                return false;
            double scale = Math.Max(Math.Abs(a), Math.Abs(b));
            return Math.Abs(a - b) <= Math.Max(absoluteTolerance, relativeTolerance * scale);
        }

        private void CloseClicked(object sender, EventArgs e)
        {
            Close();
        }

        // ================================================================ 定时器状态机

        private void TickHandler(object sender, EventArgs e)
        {
            if (_closing)
                return;
            try
            {
                switch (_phase)
                {
                    case Phase.WaitingForEnable:
                        TickWaitingForEnable();
                        break;
                    case Phase.Collecting:
                        TickCollecting();
                        break;
                    case Phase.WaitingForStop:
                        TickWaitingForStop();
                        break;
                    case Phase.TrialRunning:
                        TickTrial();
                        break;
                }
            }
            catch (Exception ex)
            {
                // 状态机里任何未预期异常都收口成可显示的中文，不留未处理异常。
                if (_dirtyBaseline)
                    FailTrialAttempt("自动调参流程异常：" + Describe(ex));
                else
                    FailStep("自动调参流程异常：" + Describe(ex));
            }
        }

        private void TickWaitingForEnable()
        {
            EngineState state = SafeState();
            if (state == EngineState.Enabled)
            {
                if (_enablePurpose == EnablePurpose.Collect)
                    StartCollecting();
                else
                    StartTrialMotion();
                return;
            }
            if (state == EngineState.Fault)
            {
                FailForPurpose("使能失败：设备进入故障状态（" + DescribeFault() + "）。");
                return;
            }
            if (state == EngineState.Disconnected)
            {
                FailForPurpose("使能失败：总线已断开。");
                return;
            }
            if (NowMs() >= _deadlineUtcMs)
            {
                FailForPurpose("使能超时：" + (EnableTimeoutMs / 1000) + " 秒内没有进入「已使能」（当前「"
                    + StateName(state) + "」）。");
                return;
            }
            SetProgress("等待进入「已使能」…（当前「" + StateName(state) + "」）");
        }

        private void TickCollecting()
        {
            EngineState state = SafeState();
            // 采集只承认「已使能」：起摆 / 平衡 / 故障 / 停止时控制层不再累计自由摆动样本。
            if (state != EngineState.Enabled)
            {
                FailStep("采集期间状态掉出「已使能」（当前「" + StateName(state) + "」）：本次采集作废，请排除后重新开始。");
                return;
            }

            long remainMs = _deadlineUtcMs - NowMs();
            if (remainMs <= 0)
            {
                ClearProgress();
                StopTimer();
                LogLine("[采集] " + CollectSeconds + " 秒采集结束。");
                RunIdentify();
                return;
            }
            SetProgress("采集中… 剩余 " + F1(remainMs / 1000.0)
                + " 秒。请让杆从任意角度自由摆动，不要碰关节。");
        }

        private void TickWaitingForStop()
        {
            EngineState state = SafeState();

            if (_stopPurpose == StopPurpose.BeforeTrial)
            {
                if (state == EngineState.Fault)
                {
                    FailTrialAttempt("试跑取消：停止后进入故障状态（" + DescribeFault() + "）。");
                    return;
                }
                if (state == EngineState.Enabled)
                {
                    StartTrialMotion();
                    return;
                }
                if (state == EngineState.Stopped || state == EngineState.Connected)
                {
                    StartEnable(EnablePurpose.Trial);
                    return;
                }
                if (NowMs() >= _deadlineUtcMs)
                {
                    FailTrialAttempt("试跑取消：" + (StopTimeoutMs / 1000) + " 秒内没有停稳（当前「"
                        + StateName(state) + "」）。");
                    return;
                }
                SetProgress("正在停止当前运动…（当前「" + StateName(state) + "」）");
                return;
            }

            bool stopped = state == EngineState.Stopped || state == EngineState.Connected ||
                           state == EngineState.Disconnected || state == EngineState.Fault;
            bool timedOut = NowMs() >= _deadlineUtcMs;
            if (stopped || timedOut)
            {
                string note = timedOut && !stopped
                    ? "停止未在 " + (StopTimeoutMs / 1000) + " 秒内完成（当前「" + StateName(state) + "」）"
                    : null;
                FinishTrial(note);
                return;
            }
            SetProgress("试跑结束，正在停止设备…（当前「" + StateName(state) + "」）");
        }

        private void FailForPurpose(string message)
        {
            if (_enablePurpose == EnablePurpose.Trial)
                FailTrialAttempt(message);
            else
                FailStep(message);
        }

        private void FailStep(string message)
        {
            _phase = Phase.Idle;
            StopTimer();
            ClearProgress();
            LogLine("[失败] " + message);
        }

        private void FailTrialAttempt(string message)
        {
            _phase = Phase.Idle;
            StopTimer();
            ClearProgress();

            bool wasDirty = _dirtyBaseline;
            bool restored = RestoreBaseline();
            string suffix = wasDirty
                ? (restored ? " 已回滚到调参前的值。" : " 回滚失败，请手动恢复参数。")
                : " 参数未改动。";
            _trialSucceeded = false;
            LogLine("[试跑] " + message + suffix);
        }

        // ================================================================ 关闭语义

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 试跑已通过但还没采用 = 关闭会丢结果：必须让人明确知道，不能静默回滚。
            if (e.CloseReason == CloseReason.UserClosing && _dirtyBaseline &&
                (_trialSucceeded || _phase == Phase.TrialRunning))
            {
                string question = _trialSucceeded
                    ? "试跑已通过，但还没有「采用并保存」。\r\n关闭会回滚到调参前的值。仍要关闭吗？"
                    : "试跑还在进行中。\r\n关闭会先受控停，并回滚到调参前的值。仍要关闭吗？";
                DialogResult answer = MessageBox.Show(this, question, "自动调参",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _closing = true;
            StopTimer();

            // 试跑进行中关窗 = 取消：先请求受控停（非阻塞），再把调参前的值写回去。
            try
            {
                bool trialInFlight = _phase == Phase.TrialRunning ||
                    (_phase == Phase.WaitingForEnable && _enablePurpose == EnablePurpose.Trial) ||
                    (_phase == Phase.WaitingForStop && _stopPurpose == StopPurpose.BeforeTrial);
                if (trialInFlight && SafeState() != EngineState.Disconnected)
                    _engine.Stop();
            }
            catch (Exception ex)
            {
                // 关窗路径不再弹窗：停止请求失败时参数回滚仍然照做，但必须留下调试痕迹。
                System.Diagnostics.Debug.WriteLine("自动调参关窗停止失败：" + Describe(ex));
            }

            if (_dirtyBaseline)
            {
                try
                {
                    _host.RestoreTuningBaseline(_baselinePid, _baselineSwing);
                    _dirtyBaseline = false;
                }
                catch (Exception ex)
                {
                    // 同上：关窗时没有可安全展示错误的位置，但不能空吞。
                    System.Diagnostics.Debug.WriteLine("自动调参关窗回滚失败：" + Describe(ex));
                }
            }

            _phase = Phase.Idle;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                _tick.Tick -= TickHandler;
                _tick.Dispose();
            }
            catch (Exception ex)
            {
                // 控件销毁阶段的异常没有可展示位置，留调试痕迹即可。
                System.Diagnostics.Debug.WriteLine("自动调参对话框销毁清理失败：" + Describe(ex));
            }
            base.OnFormClosed(e);
        }

        // ================================================================ 状态区与工具

        private void LogLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (_log.Length > 0)
                _log.Append("\r\n");
            _log.Append(text);
            Render();
        }

        private void SetProgress(string text)
        {
            _progress = text ?? string.Empty;
            Render();
        }

        private void ClearProgress()
        {
            _progress = string.Empty;
            Render();
        }

        private void Render()
        {
            if (_statusBox == null)
                return;
            string text = _log.ToString();
            if (_progress.Length > 0)
                text = (text.Length > 0 ? text + "\r\n\r\n" : string.Empty) + _progress;
            if (string.Equals(_statusBox.Text, text, StringComparison.Ordinal))
                return;
            _statusBox.Text = text;
            _statusBox.SelectionStart = _statusBox.TextLength;
            _statusBox.ScrollToCaret();
        }

        private bool GuardReady()
        {
            if (_engine == null)
            {
                SetProgress("没有可用的控制层实例，自动调参不可用。");
                return false;
            }
            if (_host == null)
            {
                SetProgress("主界面没有提供自动调参回调，自动调参不可用。");
                return false;
            }
            return true;
        }

        private bool GuardIdle()
        {
            if (_phase == Phase.Idle)
                return true;
            SetProgress("当前正在执行上一步，请等待完成…");
            return false;
        }

        private void StartTimer()
        {
            if (!_tick.Enabled)
                _tick.Start();
        }

        private void StopTimer()
        {
            if (_tick.Enabled)
                _tick.Stop();
        }

        private EngineState SafeState()
        {
            try
            {
                return _engine == null ? EngineState.Disconnected : _engine.State;
            }
            catch (Exception)
            {
                return EngineState.Disconnected;
            }
        }

        private bool TryReadTelemetry(out PendulumTelemetry telemetry)
        {
            telemetry = default(PendulumTelemetry);
            try
            {
                return _engine != null && _engine.TryReadTelemetry(out telemetry);
            }
            catch (Exception)
            {
                telemetry = default(PendulumTelemetry);
                return false;
            }
        }

        /// <summary>
        /// 试跑前确认杆在自然下垂位（离下垂 ≤30°）。遥测不可用时不阻断（无法判定），
        /// 只在能读到真实姿态时拒绝明显不在下垂位的起摆。detail 给出「离下垂 xx°」。
        /// </summary>
        private bool PendulumLooksHanging(out string detail)
        {
            detail = null;
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry) || !ControlMath.IsFinite(telemetry.ThetaFromUprightRad))
                return true;
            double deviationRad = Math.Abs(Math.Abs(telemetry.ThetaFromUprightRad) - Math.PI);
            if (deviationRad <= Math.PI / 6.0)
                return true;
            detail = "离下垂 " + F1(deviationRad * (180.0 / Math.PI)) + "°";
            return false;
        }

        /// <summary>
        /// 试跑前确认关节在软限位内。起摆目标会被软限位钳位，起点在限位外时试跑会先把关节
        /// 高速拖回边界，结果不可解释；遥测不可用时不阻断（无法判定）。
        /// </summary>
        private bool ArmInsideLimits(out string detail)
        {
            detail = null;
            SafetyLimits limits;
            try
            {
                limits = _engine.SafetyLimits;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("自动调参读软限位失败：" + Describe(ex));
                return true;
            }
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry))
                return true;
            // 判据在 counts 域：与参数同一单位，不换算角度。
            int raw = telemetry.ArmPositionCount;
            if (raw >= limits.ArmMinCount && raw <= limits.ArmMaxCount)
                return true;
            detail = "关节 0x6064=" + raw.ToString("N0") + " 不在软限位 ["
                + limits.ArmMinCount.ToString("N0") + ", " + limits.ArmMaxCount.ToString("N0") + "] counts 内";
            return false;
        }

        /// <summary>故障说明：能拿到的都写出来（故障码 / 姿态 / WKC / 旁观从站），拿不到就明说。</summary>
        private string DescribeFault()
        {
            StringBuilder sb = new StringBuilder(96);
            try
            {
                PendulumTelemetry telemetry;
                if (_engine != null && _engine.TryReadTelemetry(out telemetry))
                {
                    if (telemetry.FaultCode != FaultCode.None)
                        sb.Append("故障码 ").Append(telemetry.FaultCode.ToString());
                    if (ControlMath.IsFinite(telemetry.ThetaFromUprightRad))
                    {
                        if (sb.Length > 0)
                            sb.Append("，");
                        sb.Append("离直立 ").Append(F1(Math.Abs(telemetry.ThetaFromUprightRad) * (180.0 / Math.PI))).Append('°');
                    }
                }
            }
            catch (Exception ex)
            {
                // 遥测读不到就只写「故障」；留调试痕迹，不用猜测值冒充。
                System.Diagnostics.Debug.WriteLine("自动调参读故障遥测失败：" + Describe(ex));
            }

            try
            {
                if (_engine != null)
                {
                    EngineDiagnostics diagnostics = _engine.Diagnostics;
                    if (diagnostics.NativeWkcValid)
                        sb.Append("，WKC ").Append(diagnostics.ActualWkc.ToString(CultureInfo.InvariantCulture))
                          .Append('/').Append(diagnostics.ExpectedWkc.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(diagnostics.ObserverSlaveSummary))
                        sb.Append("，").Append(diagnostics.ObserverSlaveSummary);
                }
            }
            catch (Exception ex)
            {
                // 诊断读不到就不写，不用 0 冒充；留调试痕迹便于排查为什么读不到。
                System.Diagnostics.Debug.WriteLine("自动调参读总线诊断失败：" + Describe(ex));
            }

            if (sb.Length == 0)
                sb.Append("故障");
            return sb.ToString();
        }

        /// <summary>单调毫秒时钟（Stopwatch 时基，不受系统时间调整影响）。</summary>
        private static long NowMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private static string StateName(EngineState state)
        {
            switch (state)
            {
                case EngineState.Disconnected: return "未连接";
                case EngineState.Connected: return "已连接";
                case EngineState.Enabled: return "已使能";
                case EngineState.Swinging: return "起摆中";
                case EngineState.Balancing: return "平衡中";
                case EngineState.Stopped: return "已停";
                case EngineState.Fault: return "故障";
                default: return "未知";
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex == null)
                return "未知异常";
            string stack = ex.StackTrace ?? string.Empty;
            if (stack.Length > 300)
                stack = stack.Substring(0, 300);
            return ex.GetType().Name + "：" + ex.Message + (stack.Length > 0 ? "（" + stack + "）" : string.Empty);
        }

        private static string F0(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string F1(double value)
        {
            if (double.IsNaN(value)) return "n/a";
            if (double.IsInfinity(value)) return "∞";
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string F2(double value)
        {
            if (double.IsNaN(value)) return "n/a";
            if (double.IsInfinity(value)) return "∞";
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string F3(double value)
        {
            if (double.IsNaN(value)) return "n/a";
            if (double.IsInfinity(value)) return "∞";
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string F4(double value)
        {
            if (double.IsNaN(value)) return "n/a";
            if (double.IsInfinity(value)) return "∞";
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }
}
