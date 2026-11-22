using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FurutaPendulum.Cli;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 旋转倒立摆 Furuta 开源案例主界面：左栏 210px 操作 / 操作提示，右侧单页三行八张卡片。
    /// 八卡固定为：PDO 实时数据 / 总线与采样 / PID 参数 / 设备参数·软限位与标定 / 当前状态 / 杆角度 / 角度读数 / 运行日志。
    /// 只做显示与命令下发，控制算法、安全联锁、SDK 全在 PendulumEngine；本窗体不碰 SDK。
    /// 布局在 MainForm.Layout.cs，刷新与日志在 MainForm.Refresh.cs，自检在 MainForm.Audit.cs。
    /// </summary>
    public sealed partial class MainForm : Form, IAutoTuneHost
    {
        private const string WindowTitle = "旋转倒立摆 Furuta · 单杆";

        private const int UI_REFRESH_INTERVAL_MS = 33;        // ≈30Hz：日志 / 按钮使能 / 状态
        private const int DIAL_REFRESH_INTERVAL_MS = 20;      // ≈50Hz：杆角度表盘（控制层 1kHz 遥测，UI 只取最新帧）
        private const int SLOW_REFRESH_INTERVAL_MS = 500;     // 慢列节拍
        private const int CARD_REFRESH_INTERVAL_MS = 66;      // ≈15Hz：卡片数值刷新
        private const int PARAM_APPLY_DEBOUNCE_MS = 250;      // 参数输入去抖
        private const double CAPTURE_WINDOW_DEG = 6.0;        // 自动进平衡的切换窗口（|θ−180°| < 6°）
        private const double NEAR_UPRIGHT_DEG = 15.0;         // 「接近倒立」判据（用户指定 15°）
        private const int LIVE_ARM_FRESH_MS = 500;            // 0x6064 快照超过这个年龄就不算「正在跑」

        // 设备换算：42 步进 1.8°/步 → 200 整步/圈；一圈计数 = 200 × 细分（默认 50 → 10000）。
        // 摆臂默认一圈计数只作参数文件兜底（控制层权威 = 驱动器 0x2102 实读 × 减速比），界面没有对应输入框。
        private const decimal DEFAULT_ARM_COUNTS_PER_REV = 10000m;

        private const int UI_LOG_QUEUE_LIMIT = 2000;
        private const int UI_LOG_BATCH_LIMIT = 50;
        private const int UI_LOG_TEXT_LIMIT = 40000;
        private const int UI_LOG_TEXT_TARGET = 20000;
        private const int UI_LOG_LINE_LIMIT = 2048;
        private const string UI_LOG_TRUNCATION_SUFFIX = "...[已截断]";
        private const int UI_LOG_ERROR_LIMIT = 5;

        // 被拒日志的拒绝原因分类（固定四类，现场按词检索；字面量不要改）。
        private const string REJECT_NOT_ENABLED = "未使能";
        private const string REJECT_NOT_CALIBRATED = "未标定";
        private const string REJECT_INTERLOCK = "联锁";
        private const string REJECT_OTHER = "其它";

        // 保存按钮的两种文本：有未保存改动时带实心圆点，提示「还差一次落盘」。
        private const string SAVE_BUTTON_TEXT = "保存参数";
        private const string SAVE_BUTTON_DIRTY_TEXT = "保存参数 ●";

        // 参数文件载入被拒时的统一提示：关窗兜底保存必须让路，避免拿出厂值覆盖用户原文件。
        private const string PARAM_LOAD_REJECTED_NOTICE =
            "本次未能载入参数文件，关闭时不会自动保存，以免覆盖原文件；请先修复文件或另存";

        private static readonly PidGains DefaultGains = PidGains.CreateDefault();
        private static readonly SwingUpParams DefaultSwing = SwingUpParams.CreateDefault();

        private enum LogLevel
        {
            Info = 0,
            Warn = 1,
            Error = 2
        }

        private struct LogEntry
        {
            public string Text;
            public LogLevel Level;
        }

        private readonly PendulumEngine _engine;
        private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
        private DiagnosticLogWriter _fileLog;

        // ---- 左栏「操作」----
        private Button _connectButton;
        private Button _disconnectButton;
        private Button _enableButton;
        private Button _stopButton;
        private Button _resetButton;
        private Button _jogLeftButton;
        private Button _jogRightButton;
        private Button _swingButton;
        private Button _returnHomeButton;
        private Button _estopButton;
        private Button _saveParamsButton;
        private Button _applyParamsButton;
        private Button _loadParamsButton;
        private Button _pidDialogButton;
        private Button _restoreDefaultsButton;

        // ---- 主视野单页：八张卡片全部常驻（布局见 MainForm.Layout.cs 的 BuildMainPage）----

        // ---- 卡片① PDO 实时数据（主视野常驻，全部取 PDO 过程映像字段）----
        private Label _pdoTargetPosLabel;
        private Label _pdoActualPosLabel;
        private Label _followErrorLabel;
        private Label _stepVelocityLabel;
        private Label _stepTorqueLabel;
        private Label _pdoStatusWordLabel;
        private Label _pdoErrorCodeLabel;
        private Label _stepEnableLabel;
        private Label _pdoModeLabel;
        private Label _stepFeedbackLabel;
        private Label _stepSlaveLabel;

        // ---- 卡片② 当前状态（关节位置 / 零点 / 软限位，全部原始计数）----
        private Label _armPositionLabel;
        private Label _armOffsetLabel;
        private Label _armLimitStateLabel;

        // ---- 卡片③ 总线与采样 ----
        private Label _busWkcLabel;
        private Label _busCycleCountLabel;
        private Label _busSlavesLabel;
        private Label _busMasterLabel;
        private Label _busLinkLabel;
        private Label _busPortLabel;
        private Label _busRtLabel;
        private Label _sampleTimeLabel;
        private Label _configSourceLabel;

        // ---- 卡片 杆角度图 / 卡片 角度读数（单页第三行）----
        private PendulumDialView _pendulumDial;
        private Label _thetaLabel;
        private Label _omegaLabel;
        private Label _psiLabel;
        private Label _phiLabel;
        private Label _energyLabel;
        private Label _energyTargetLabel;
        private Label _nearUprightLabel;
        private Label _pendulumCountLabel;
        private Label _pendulumCountCh2Label;
        private Label _pendulumFreqLabel;

        // ---- 卡片④ PID 参数 ----
        // 控件名沿用历史 _kpBox/_kiBox/_kdBox；它们只显示上一代外环增益 Ka / Kad / Kai 的历史值
        // （平衡环已由整套状态反馈接管，这三项不参与平衡，见 ApplyPidParams）。
        private NumericUpDown _kpBox;
        private NumericUpDown _kiBox;
        private NumericUpDown _kdBox;
        private NumericUpDown _keBox;
        private NumericUpDown _pumpAmpBox;
        private NumericUpDown _pumpFreqBox;
        private NumericUpDown _pumpPhaseBox;
        private Label _pumpStatusLabel;

        // ---- 卡片⑤ 设备参数 / 限位与标定（5 个位置，全部绝对 counts）----
        private NumericUpDown _pendulumZeroCountBox;
        private NumericUpDown _pendulum360CountBox;
        private Label _pendulumCprLabel;
        private Label _limitCurrentLabel;
        private Label _safetyLabel;

        // ---- 卡片⑤ 标定与限位（原始计数）----
        // 5 个位置 = 5 个采集按钮 + 5 个计数输入框：左限位 / 驱动臂零点 / 右限位 / 摆杆零点(下垂 0°) / 摆杆 360°。
        // 摆杆一圈的量 = |360° − 0°|，只显示不输入。
        private Button _setArmZeroButton;
        private NumericUpDown _armZeroCountBox;
        private Button _setLeftLimitButton;
        private Button _setRightLimitButton;
        private Button _setPendulumZeroButton;
        private Button _setPendulum360Button;
        private Button _clearCalibrationButton;
        private Button _normalizePoseButton;
        private Button _normalizePendulumButton;
        private NumericUpDown _limitMinCountsBox;
        private NumericUpDown _limitMaxCountsBox;
        private Label _calibrationHintLabel;
        private bool _suppressCalibrationEvents;
        private bool _armZeroDirty;
        private bool _limitCountsDirty;
        private bool _pendulumZeroDirty;
        private bool _pendulum360Dirty;

        // ---- 启动位姿核对（连接后首帧 raw 与保存零点不一致时阻断自动动作）----
        private bool _poseReferenceChecked;
        private bool _poseReferenceArmMismatch;
        private bool _poseReferencePendulumMismatch;
        private long _poseReferenceArmDelta;
        private long _poseReferencePendulumDelta;
        private int _poseReferenceSavedArmZero;
        private int _poseReferenceSavedPendulumZero;
        private int _poseReferenceCurrentArmRaw;
        private int _poseReferenceCurrentPendulumRaw;
        private bool _poseReferenceHasArm;
        private bool _poseReferenceHasPendulum;
        private bool _poseRecheckArmZero;
        private bool _poseRecheckArmMin;
        private bool _poseRecheckArmMax;
        private bool _poseRecheckPendulumZero;
        private bool _poseRecheckPendulum360;
        private bool _poseReferenceNeedsPendulum360;
        // 本次进程还没有确认持久标定（增量编码器无法自证跨掉电位置）：载入参数时按会话标记置位，
        // 规整化 / 重新采集齐全后清除。它是「使能 / 起摆 / 平衡 / 回零」的前置阻断之一。
        // 拒绝原因按当前点位现算（BuildPoseSessionReason），不缓存：用户补采一半后文案要跟着变。
        private bool _poseSessionReviewRequired;
        // ---- 卡片⑥ 运行日志 ----
        private RichTextBox _logBox;

        // ---- 左栏「状态」----
        private Label _statusConnLabel;
        private Label _statusStateLabel;
        private Label _statusUptimeLabel;
        private Label _paramSavedLabel;
        private Label _statusMessageLabel;

        private System.Windows.Forms.Timer _uiTimer;
        private System.Windows.Forms.Timer _dialTimer;
        private System.Windows.Forms.Timer _paramApplyTimer;
        private ToolTip _hints;

        // ---- 参数影子 ----
        private PidGains _pidShadow;
        private Label _pidHintLabel;
        private string _pidHintBaseText;
        private SwingUpParams _swingShadow;
        private SafetyLimits _limitsShadow;
        private bool _suppressParamEvents;
        private bool _pumpDirty;
        private bool _pidDirty;
        // 本次「应用」是否被真正生效项的合法性判据拒绝（历史增益 Ka/Kad/Kai 不参与）：拒绝时状态行必须保留原因，
        // 不能随后被一句绿色的「参数已应用」盖掉（否则用户会以为无效参数已经生效）。
        private bool _pidApplyRejected;

        // ---- 持久化 ----
        private PersistedExtras _persistedExtras;
        private int _persistedArmCountsPerRev;
        private double _lastAppliedIdentifyKe;
        private bool _savingParams;
        // 本次进程曾载入参数文件失败（自动 / 手动）。置位期间关窗兜底保存跳过：
        // 引擎里只是出厂值，写回去会整份覆盖用户那个「格式有问题」的文件。
        private bool _paramLoadRejected;
        // 参数已改动但还没写进 furuta_params.json：所有改动都已对本次运行生效，
        // 这里只负责把「未保存」如实显示出来（保存按钮高亮 + 当前状态卡的「参数」行 + 操作提示）。
        private bool _paramsDirty;

        // ---- 运行态 ----
        private int _opBusy;
        private string _opLabel;
        private int _jogHeldDirection;
        private int _queuedUiLogLines;
        private long _droppedUiLogLines;
        private int _tickErrors;
        private int _readErrors;
        private long _lastSlowTicks;
        private long _lastCardTicks;
        private long _sessionStartTicks;
        private bool _sessionActive;
        private double _lastThetaDeg;
        private double _lastThetaAbsDeg;
        private double _lastOmegaDegPerSec;
        private double _lastUPercent;
        private bool _lastEStopLatch;
        private bool _captureLogged;
        private double _uFullScaleRad = 0.35;
        private string _lastStatusText;
        private EngineState _lastEngineState = EngineState.Disconnected;

        public MainForm(PendulumEngine engine)
        {
            if (engine == null)
                throw new ArgumentNullException("engine");
            _engine = engine;

            _fileLog = CreateFileLog();

            Text = WindowTitle;
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = false;
            BackColor = UiStyles.FormBack;
            StartPosition = FormStartPosition.CenterScreen;
            // 可缩放：只钉最小客户区（八张卡片都放得下的尺寸），不设上限、不改 ResizeMode。
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            ShowIcon = false;
            ClientSize = new Size(DpiLock.DesignWidth, DpiLock.DesignHeight);

            _hints = new ToolTip();
            BuildRoot();
            LoadInitialState();
            AutoLoadParams();
            RefreshSafetyText();
            RefreshUFullScale();
            RefreshButtons();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = UI_REFRESH_INTERVAL_MS;
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            // 表盘单独 50Hz：只读最新遥测快照 + 重绘一个小面板，不跟卡片 15Hz 节流。
            _dialTimer = new System.Windows.Forms.Timer();
            _dialTimer.Interval = DIAL_REFRESH_INTERVAL_MS;
            _dialTimer.Tick += OnDialTimerTick;
            _dialTimer.Start();

            _paramApplyTimer = new System.Windows.Forms.Timer();
            _paramApplyTimer.Interval = PARAM_APPLY_DEBOUNCE_MS;
            _paramApplyTimer.Tick += OnParamApplyTick;
            _paramApplyTimer.Stop();

            AttachParamCommitHandlers();

            _engine.StateChanged += OnEngineStateChanged;
            _engine.Faulted += OnEngineFaulted;
            _engine.Diagnostic += OnEngineDiagnostic;
            FormClosing += OnFormClosing;

            SetStatus("未连接。");
            // 启动自动载入被拒时，构造函数后面的通用状态会盖掉失败提示；这里最后写一次，
            // 让用户一进来就看到「本次不自动保存」的后果说明。
            if (_paramLoadRejected)
                SetStatus(PARAM_LOAD_REJECTED_NOTICE, UiStyles.WarnOrange);
            Log("[系统] 界面就绪。");
            Log("[系统] 布局：左栏五张操作卡 + 操作提示；右侧单页三行（PDO 实时数据 / 总线与采样 / 当前状态；杆角度表盘跨两行 / PID 参数 / 设备参数·软限位 / 角度读数 / 运行日志），窗口可自由拉大。");
            if (_fileLog != null && !string.IsNullOrEmpty(_fileLog.CurrentFilePath))
                Log("[系统] 日志落盘：" + _fileLog.CurrentFilePath);
            else
                Log("[系统] 日志落盘通道未建立（目录不可写），本次只有界面日志。");
        }

        /// <summary>
        /// 窗口尺寸契约：默认 1280×1000 设计稿；Load 时按真实非客户区差值钉最小外框。
        /// 最小客户区 960×640 —— 低于设计稿时左右两栏各自滚动（见 MainForm.Layout.cs），
        /// 不再把“最小 1280×1000”硬塞给用户。不设上限、不改 ResizeMode。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            DpiLock.ApplyMinimumClientSize(this, MIN_CLIENT_WIDTH, MIN_CLIENT_HEIGHT);
            // AI 注入接口默认关闭；只带 --ui-automation-port / 环境变量时在本机监听（见 MainForm.Automation.cs）。
            StartUiAutomationIfRequested();
        }

        // ================================================================ 按钮事件

        private void ConnectClicked(object sender, EventArgs e)
        {
            RunOp("连接", delegate
            {
                _engine.Connect();
                // 总线状态如实取自实测：直连进 OP 模式下连接完成即已在 OP（未使能）。
                Log("连接完成：" + _engine.BusStateName + "（未使能）。进 OP 方式：" + _engine.OpEntryText + "。" + DescribeBus() + "。");
            });
        }

        private void EnableClicked(object sender, EventArgs e)
        {
            // 未连接时由本按钮自动补连接；连接完成后在同一个操作里再核对位姿。
            if (_engine.State != EngineState.Disconnected)
            {
                string poseReason;
                if (!CheckPoseReferenceForMotion(out poseReason))
                {
                    LogReject("Enable(使能)", REJECT_INTERLOCK, poseReason, null);
                    SetStatus("使能被拒：" + poseReason, UiStyles.AlarmColor);
                    return;
                }
            }
            RunOp("使能", null, delegate
            {
                if (_engine.State == EngineState.Disconnected)
                {
                    _engine.Connect();
                    Log("连接完成：" + _engine.BusStateName + "（未使能）。进 OP 方式：" + _engine.OpEntryText + "。" + DescribeBus() + "。");
                }
                string poseReason;
                if (!CheckPoseReferenceForMotion(out poseReason))
                {
                    LogReject("Enable(使能)", REJECT_INTERLOCK, poseReason, null);
                    SetStatus("使能被拒：" + poseReason, UiStyles.AlarmColor);
                    return;
                }
                _engine.Enable();
                EngineState reached = WaitForState(EngineState.Enabled, 6500);
                if (reached == EngineState.Enabled)
                {
                    Log("使能完成：进 OP，CiA402 06→07→0F，θ/φ 已自动归零。" + DescribeDrive());
                    SetStatus("已使能（就绪）。");
                }
                else
                {
                    FaultCode code = CurrentFaultCode();
                    LogReject("Enable(使能)", ClassifyEngineReject(code),
                        "使能未完成（" + FormatState(reached, _lastEStopLatch) + "）：" + DescribeDrive() + "，可重试", code);
                    SetStatus("使能未完成：" + DescribeDrive() + "，可重试。", Color.Firebrick);
                }
            });
        }

        private void StartClicked(object sender, EventArgs e)
        {
            string reason;
            string category;
            if (!TryPassStartGate(out reason, out category))
            {
                FaultCode? code = category == REJECT_INTERLOCK ? CurrentFaultCode() : (FaultCode?)null;
                LogReject("SwingUp(起摆)", category, reason, code);
                SetStatus("起摆被拒：" + reason, UiStyles.AlarmColor);
                return;
            }
            double amp = _swingShadow.PumpAmplitude;
            double hz = _swingShadow.PumpFrequencyHz;
            RunOp("起摆", null, delegate
            {
                string engineReason;
                if (!_engine.TrySwingUp(out engineReason))
                {
                    LogReject("SwingUp(起摆)", ClassifyEngineReject(CurrentFaultCode()),
                        "引擎侧门拒绝：" + engineReason, CurrentFaultCode());
                    SetStatus("起摆被拒：" + engineReason, UiStyles.WarnOrange);
                    return;
                }
                // Stopped 会先自动重新使能再接起摆，等待窗口必须覆盖完整使能子流程。
                EngineState reached = WaitForState(EngineState.Swinging, 6500);
                if (reached == EngineState.Swinging || reached == EngineState.Balancing)
                {
                    Log("[起摆] 泵已投入（泵加速度幅值 " + amp.ToString("F2") + " rad/s²，泵频 " + hz.ToString("F2") +
                        " Hz），切换判据满足后自动切入平衡。");
                }
                else
                {
                    FaultCode code = CurrentFaultCode();
                    LogReject("SwingUp(起摆)", ClassifyEngineReject(code),
                        "未进入起摆（" + FormatState(reached, _lastEStopLatch) + "）：" + DescribeDrive() + "，可重试", code);
                    SetStatus("起摆未完成：" + DescribeDrive() + "，可重试。", UiStyles.AlarmColor);
                }
            });
        }

        /// <summary>
        /// 「回到零点」：把摆臂运动回**已标定的零点 counts**。只移动，不改零点 / 限位 / 一圈量 / PID / 泵。
        /// 位姿核对未通过时按钮禁用；点击入口仍再查一次，状态不满足时给中文原因。
        /// </summary>
        private void ReturnHomeClicked(object sender, EventArgs e)
        {
            string reason;
            if (_engine.State != EngineState.Disconnected && !CheckPoseReferenceForMotion(out reason))
            {
                LogReject("ReturnHome(回到零点)", REJECT_INTERLOCK, reason, null);
                SetStatus("回零被拒：" + reason, UiStyles.AlarmColor);
                return;
            }
            if (!_engine.TryReturnToZero(out reason))
            {
                LogReject("ReturnHome(回到零点)", ClassifyEngineReject(CurrentFaultCode()), reason, null);
                SetStatus("回零被拒：" + reason, UiStyles.WarnOrange);
                return;
            }

            int target = _engine.ReturnHomeTargetCount;
            Log("[运动] 回零已受理：目标 = 标定零点 raw=" + target.ToString("N0") +
                " counts（绝对位置；不重设零点）。");
            SetStatus("回零中：目标 raw=" + target.ToString("N0") + " counts，界面不阻塞，随时可急停。");
            RunOp("回到零点", null, delegate
            {
                EngineState reached = WaitForReturnHome(target, 30000);
                if (reached == EngineState.Enabled && !_engine.IsReturnHomeActive)
                {
                    PendulumTelemetry t;
                    int actual = _engine.TryReadTelemetry(out t) ? t.ArmPositionCount : target;
                    Log("回零完成：0x6064=" + actual.ToString("N0") + " counts，标定零点 raw=" +
                        target.ToString("N0") + " counts。零点 / 限位 / PID 均未改动。");
                    SetStatus("回零完成：0x6064=" + actual.ToString("N0") + " counts。", UiStyles.OkGreen);
                }
                else
                {
                    string why = _engine.IsReturnHomeActive
                        ? "30 s 内未确认到位"
                        : "回零已提前结束（状态 " + FormatState(reached, _lastEStopLatch) + "）";
                    Log(LogLevel.Warn, "[运动] 回零未确认完成：" + why + "；当前 0x6064 / 目标见「当前状态」卡。");
                    SetStatus("回零未确认完成：" + why + "，见运行日志。", UiStyles.WarnOrange);
                }
            });
        }

        /// <summary>等待回零走完：先看到 active，再等它落下；Fault / Disconnected 立即返回。</summary>
        private EngineState WaitForReturnHome(int target, int timeoutMs)
        {
            long until = Environment.TickCount + timeoutMs;
            bool sawActive = false;
            while (Environment.TickCount < until)
            {
                EngineState state = _engine.State;
                if (state == EngineState.Fault || state == EngineState.Disconnected)
                    return state;
                if (_engine.IsReturnHomeActive)
                {
                    sawActive = true;
                }
                else if (sawActive || state == EngineState.Enabled)
                {
                    PendulumTelemetry t;
                    if (_engine.TryReadTelemetry(out t) &&
                        Math.Abs((long)t.ArmPositionCount - target) <= 3)
                        return state;
                }
                Thread.Sleep(50);
            }
            return _engine.State;
        }

        /// <summary>
        /// 「清空标定」：清掉五个位置点（左 / 零 / 右 / 摆杆 0° / 360°），保留一圈量 / PID / 泵 / 减速比。
        /// 只改内存与未保存状态，不移动电机；清完自动运动由引擎的 CalibrationCleared 拒绝。
        /// </summary>
        private void ClearCalibrationClicked(object sender, EventArgs e)
        {
            string reason;
            if (!_engine.ClearCalibration(out reason))
            {
                LogReject("ClearCalibration(清空标定)", REJECT_INTERLOCK, reason, null);
                SetStatus("清空标定被拒：" + reason, UiStyles.WarnOrange);
                return;
            }

            if (_persistedExtras == null)
                _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
            _persistedExtras.ClearPositionCalibration();
            _limitsShadow = _engine.SafetyLimits;
            _engine.ConfirmCalibrationReview();

            _suppressCalibrationEvents = true;
            try
            {
                ShowNumberUncalibrated(_armZeroCountBox);
                ShowNumberUncalibrated(_limitMinCountsBox);
                ShowNumberUncalibrated(_limitMaxCountsBox);
                ShowNumberUncalibrated(_pendulumZeroCountBox);
                ShowNumberUncalibrated(_pendulum360CountBox);
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
            _armZeroDirty = false;
            _limitCountsDirty = false;
            _pendulumZeroDirty = false;
            _pendulum360Dirty = false;
            _poseReferenceChecked = true;
            _poseReferenceArmMismatch = false;
            _poseReferencePendulumMismatch = false;
            _poseRecheckArmZero = false;
            _poseRecheckArmMin = false;
            _poseRecheckArmMax = false;
            _poseRecheckPendulumZero = false;
            _poseRecheckPendulum360 = false;
            // 清空后没有「未确认的持久标定」这回事：标定已经不存在了，阻断由 CalibrationCleared 负责。
            _poseSessionReviewRequired = false;
            _engine.ConfirmPoseSessionReview();

            double keptCpr = _engine.PendulumCountsPerRev;
            SetPendulumCprReadout(keptCpr > 0
                    ? keptCpr.ToString("0.###", CultureInfo.InvariantCulture) + " counts/圈（保留；0°/360° 未标定）"
                    : "未标定（标定 0° 与 360° 两点）",
                keptCpr > 0 ? UiStyles.WarnOrange : UiStyles.WarnOrange);
            RefreshCalibrationCard();
            RefreshLimitCountFields();
            RefreshSafetyText();
            MarkParamsDirty("清空位置标定（左 / 驱动臂零点 / 右 / 0° / 360°）");
            Log(LogLevel.Warn, "[标定] 已清空五个位置点：左限位 / 驱动臂零点 / 右限位 / 摆杆 0° / 360°；" +
                "一圈量 " + keptCpr.ToString("0.###", CultureInfo.InvariantCulture) +
                " counts/圈、PID、泵参数保持不动，电机未移动。重新采集 / 规整化前自动运动会被拒绝。");
            SetStatus("标定已清空：五个位置点未标定，电机未移动；一圈量 / PID / 泵已保留。保存参数后持久化。",
                UiStyles.WarnOrange);
            RefreshButtons();
        }

        /// <summary>
        /// 「规整化左中右」：以当前实测 0x6064 为新零点，把旧左 / 右限位按同一个 delta 平移，
        /// 保留左右行程。只改位置基准，不动摆杆两点 / 一圈量 / PID / 泵，也不移动电机。
        /// </summary>
        private void NormalizeArmPoseClicked(object sender, EventArgs e)
        {
            string reason;
            int currentRaw;
            if (!TryReadLiveArmPosition(out currentRaw))
            {
                reason = "0x6064 没有正在跑的实时值（未连接 / 未进 OP / 遥测停更），无法规整化。";
                LogReject("NormalizeArmPose(规整化左中右)", REJECT_NOT_ENABLED, reason, null);
                SetStatus("规整化被拒：" + reason, UiStyles.AlarmColor);
                return;
            }
            if (_persistedExtras == null || _persistedExtras.Calibration == null ||
                !_persistedExtras.Calibration.HasArmZeroCount)
            {
                reason = "尚未保存驱动臂零点，不能规整化：先采集「驱动臂零点 + 左限位 + 右限位」。";
                LogReject("NormalizeArmPose(规整化左中右)", REJECT_NOT_CALIBRATED, reason, null);
                SetStatus("规整化被拒：" + reason, UiStyles.WarnOrange);
                return;
            }

            int newMin, newZero, newMax;
            // 以**当前生效**的软限位为准平移：控制层的 SafetyLimits 是唯一权威（用户刚改过限位
            // 还没保存时，_persistedExtras.SoftLimits 仍是旧的；_limitsShadow 也可能还没同步）。
            SoftLimitParameters liveLimits = SoftLimitParameters.From(_engine.SafetyLimits);
            if (!_persistedExtras.Calibration.TryNormalizeArmPose(
                    currentRaw, liveLimits,
                    out newMin, out newZero, out newMax, out reason))
            {
                LogReject("NormalizeArmPose(规整化左中右)", REJECT_OTHER, reason, null);
                SetStatus("规整化被拒：" + reason, UiStyles.WarnOrange);
                return;
            }

            SafetyLimits limits = _limitsShadow;
            limits.ArmMinCount = newMin;
            limits.ArmMaxCount = newMax;
            if (!_engine.TryApplyArmCalibration(newZero, limits, out reason))
            {
                LogReject("NormalizeArmPose(规整化左中右)", REJECT_INTERLOCK, reason, null);
                SetStatus("规整化未写入：" + reason, UiStyles.WarnOrange);
                return;
            }

            _limitsShadow = limits;
            _persistedExtras.Calibration.ArmZeroCount = newZero;
            _persistedExtras.Calibration.HasArmZeroCount = true;
            _persistedExtras.SoftLimits.MinCounts = newMin;
            _persistedExtras.SoftLimits.MaxCounts = newMax;
            _persistedExtras.MarkCalibrationComplete();
            _poseRecheckArmZero = true;
            _poseRecheckArmMin = true;
            _poseRecheckArmMax = true;
            MaybeResolvePoseReference();
            RefreshCalibrationCard();
            RefreshLimitCountFields();
            RefreshSafetyText();
            MarkParamsDirty("规整化左中右");
            Log(LogLevel.Warn, "[标定] 已规整化左中右：当前 0x6064=" + currentRaw.ToString("N0") +
                " 设为新零点；左限位 " + newMin.ToString("N0") + " → 右限位 " + newMax.ToString("N0") +
                " counts（同 delta 平移，左右行程保留）。摆杆 0°/360°、一圈量、PID、泵未改动，电机未移动。");
            // 规整化只解决摆臂这一组；摆杆那组仍可能未确认。此时必须如实说「仍待确认」，
            // 不能拿一句绿色的「规整化完成」把后面的使能阻断盖掉。
            bool stillPending = _poseReferenceArmMismatch || _poseReferencePendulumMismatch ||
                                _poseSessionReviewRequired || _engine.CalibrationReviewRequired;
            if (stillPending)
            {
                string pendingReason = _poseSessionReviewRequired ? BuildPoseSessionReason() : BuildPoseMismatchReason();
                Log(LogLevel.Warn, "[标定] 规整化只完成摆臂一组，自动动作仍待确认：" + pendingReason);
                SetStatus("规整化完成（新零点 raw=" + newZero.ToString("N0") +
                    "），但自动动作仍待确认：" + pendingReason, UiStyles.WarnOrange);
            }
            else
            {
                SetStatus("规整化完成：新零点 raw=" + newZero.ToString("N0") +
                    "，限位 " + newMin.ToString("N0") + " ~ " + newMax.ToString("N0") +
                    " counts（行程保留）。保存参数后持久化。", UiStyles.OkGreen);
            }
            RefreshButtons();
        }

        /// <summary>
        /// 「规整化摆杆」：把当前 CH1 计数认作新的摆杆 0°；若已有 360° 点，按同一位移平移 360°。
        /// 一圈量（|360−0|）保持不变；不移动电机，只改摆杆零点基准并落盘。
        /// </summary>
        private void NormalizePendulumPoseClicked(object sender, EventArgs e)
        {
            string reason = null;
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry) || telemetry.TimestampTicks <= 0)
            {
                reason = "摆杆计数没有实时值（未连接 / 未进 OP / 遥测停更），无法规整化。";
                LogReject("NormalizePendulumPose(规整化摆杆)", REJECT_NOT_ENABLED, reason, null);
                SetStatus("规整化摆杆被拒：" + reason, UiStyles.AlarmColor);
                return;
            }
            if (_persistedExtras == null)
                _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
            if (_persistedExtras.Calibration == null)
                _persistedExtras.Calibration = CalibrationParameters.CreateDefault();
            if (!_persistedExtras.Calibration.HasPendulumZero)
            {
                reason = "尚未保存摆杆 0° 点，不能规整化：先把摆杆放到自然下垂位并采集「摆杆零点 (下垂 0°)」。";
                LogReject("NormalizePendulumPose(规整化摆杆)", REJECT_NOT_CALIBRATED, reason, null);
                SetStatus("规整化摆杆被拒：" + reason, UiStyles.WarnOrange);
                return;
            }

            int currentRaw = telemetry.PendulumCount;
            int newZero;
            // 本方法只负责「把当前点作为新零点」的合法性校验（含 360° 越 int32 整体拒绝）；
            // 360° 的新值由下面的提交函数按控制层同一套规则派生。
            if (!_persistedExtras.Calibration.TryNormalizePendulumPose(
                    currentRaw, out newZero, out _, out reason))
            {
                LogReject("NormalizePendulumPose(规整化摆杆)", REJECT_OTHER, reason, null);
                SetStatus("规整化摆杆被拒：" + reason, UiStyles.WarnOrange);
                return;
            }
            // 零点提交 + 360° 同步走唯一实现（与「摆杆零点」采集、CLI zero/normalize/recenter 同一套
            // span=0 作废 / 零点未变短路规则）。new360 只由控制层 Rebase 派生，UI 不自己写第二个值。
            PendulumZeroCommit.Result commit = PendulumZeroCommit.Commit(
                _engine, _persistedExtras.Calibration, newZero);
            if (!commit.Accepted)
            {
                LogReject("NormalizePendulumPose(规整化摆杆)", REJECT_INTERLOCK,
                    "规整化摆杆未写入：" + commit.RejectReason + "（请求 raw=" + newZero + "）", null);
                SetStatus("规整化摆杆未写入：" + commit.RejectReason, UiStyles.WarnOrange);
                return;
            }

            bool has360 = commit.HasPendulum360;
            _poseRecheckPendulumZero = true;
            if (has360) _poseRecheckPendulum360 = true;
            MaybeResolvePoseReference();

            _suppressCalibrationEvents = true;
            try
            {
                ShowNumberCalibrated(_pendulumZeroCountBox, commit.AppliedZero);
                if (has360)
                    ShowNumberCalibrated(_pendulum360CountBox, commit.Pendulum360Count);
                else
                    ShowNumberUncalibrated(_pendulum360CountBox);
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
            RefreshCalibrationCard();
            UpdateDerivedPendulumCpr();
            if (!string.IsNullOrEmpty(commit.SyncError))
            {
                LogReject("NormalizePendulumPose(规整化摆杆)", REJECT_OTHER,
                    "0° 已写入控制层，但 360° 点重标失败：" + commit.SyncError, null);
            }
            if (!string.IsNullOrEmpty(commit.Note))
                Log(commit.Rebased ? LogLevel.Info : LogLevel.Warn, "[标定] " + commit.Note);
            // 零点 / 360° 都没变（同一点重复规整化）时不再弄脏参数：没有要保存的变化。
            if (!commit.ZeroUnchanged)
                MarkParamsDirty("规整化摆杆 0° / 360°");
            bool saved = SaveParametersNow("规整化摆杆");
            Log(LogLevel.Warn, "[标定] 已规整化摆杆：当前 CH1=" + currentRaw.ToString("N0") +
                " 设为新 0°" + (has360 ? "，360° = " + commit.Pendulum360Count.ToString("N0") + "（同 delta 平移，一圈量不变）" : "（未标定 360°）") +
                (commit.ZeroUnchanged ? "；零点与 360° 点原本就是该值，未产生变更" : "") +
                "；电机未移动。" + (saved ? "参数已保存。" : "参数保存失败，见日志。"));
            SetStatus("规整化摆杆完成：新 0° raw=" + commit.AppliedZero.ToString("N0") +
                (has360 ? "，360° raw=" + commit.Pendulum360Count.ToString("N0") : "") +
                "；一圈量保持不变。" + (saved ? "已保存。" : "保存失败，见日志。"),
                saved ? UiStyles.OkGreen : UiStyles.WarnOrange);
            RefreshButtons();
        }

        /// <summary>
        /// 连接后首次使能前的位姿核对：当前 raw 与保存零点不一致时，显示 old/current/delta 并阻断
        /// 使能 / 起摆 / 平衡 / 自动回零。返回 false = 不允许继续自动动作。
        /// </summary>
        private bool CheckPoseReferenceForMotion(out string reason)
        {
            reason = null;
            PendulumTelemetry telemetry;
            if (!_poseReferenceChecked)
            {
                if (!TryReadTelemetry(out telemetry) || telemetry.TimestampTicks <= 0)
                {
                    reason = "首帧遥测还没到，无法核对保存零点；等状态行出现采样值后再试。";
                    return false;
                }
                UpdatePoseReference(telemetry);
            }

            if (_engine.CalibrationReviewRequired)
            {
                _poseReferenceArmMismatch = _poseReferenceArmMismatch || _engine.ArmPoseDeltaCounts != 0;
                _poseReferencePendulumMismatch = _poseReferencePendulumMismatch || _engine.PendulumPoseDeltaCounts != 0;
            }
            if (_poseReferenceArmMismatch || _poseReferencePendulumMismatch)
            {
                reason = BuildPoseMismatchReason();
                return false;
            }
            if (_poseSessionReviewRequired)
            {
                reason = BuildPoseSessionReason();
                return false;
            }
            return true;
        }

        /// <summary>
        /// 「本次进程还没确认持久标定」的中文原因：点名哪一组待确认、摆杆缺哪一半，并给出三条出口
        /// （按保存姿态规整化 / 重新采集 / 清空后重采）。文案随当前点位实时生成，补采一半后会变。
        /// </summary>
        private string BuildPoseSessionReason()
        {
            CalibrationParameters calibration = _persistedExtras == null ? null : _persistedExtras.Calibration;
            StringBuilder sb = new StringBuilder();
            sb.Append("本次进程还没有确认持久标定的位置基准（增量编码器掉电后 raw 不保证仍对应保存的计数），");
            List<string> pending = new List<string>();
            if (calibration != null && calibration.HasArmZeroCount && !calibration.ArmPoseSessionVerified)
                pending.Add("摆臂三点（零点 + 左 / 右限位）");
            if (calibration != null && (calibration.HasPendulumZero || calibration.HasPendulum360) &&
                !calibration.PendulumPoseSessionVerified)
            {
                PendulumPairCheck pair = calibration.CheckPendulumPair();
                pending.Add(pair.IsComplete ? "摆杆两点（0° / 360°）" : "摆杆两点（" + pair.MissingText + "）");
            }
            sb.Append(pending.Count == 0 ? "位置点位未确认" : "待确认：" + string.Join("、", pending.ToArray()));
            sb.Append("。机械仍在保存的零点姿态就点「规整化左中右」/「规整化摆杆」；"
                + "不确定就按当前位姿重新采集，或「清空标定」后重采；确认前不使能、不起摆。");
            return sb.ToString();
        }

        private void UpdatePoseReference(PendulumTelemetry telemetry)
        {
            if (_poseReferenceChecked)
                return;
            _poseReferenceChecked = true;
            CalibrationParameters calibration = _persistedExtras == null ? null : _persistedExtras.Calibration;
            _poseReferenceHasArm = calibration != null && calibration.HasArmZeroCount;
            _poseReferenceHasPendulum = calibration != null && calibration.HasPendulumZero;
            _poseReferenceNeedsPendulum360 = calibration != null && calibration.HasPendulum360;
            _poseReferenceCurrentArmRaw = telemetry.ArmPositionCount;
            _poseReferenceCurrentPendulumRaw = telemetry.PendulumCount;

            if (!_poseReferenceHasArm && !_poseReferenceHasPendulum)
            {
                Log("[标定] 没有保存的位置零点，跳过连接后位姿核对；本次首次使能会自动归零。");
                return;
            }

            _poseReferenceSavedArmZero = _poseReferenceHasArm ? calibration.ArmZeroCount : 0;
            _poseReferenceSavedPendulumZero = _poseReferenceHasPendulum ? calibration.PendulumZeroCount : 0;
            _poseReferenceArmDelta = _poseReferenceHasArm
                ? (long)_poseReferenceCurrentArmRaw - _poseReferenceSavedArmZero
                : 0L;
            _poseReferencePendulumDelta = _poseReferenceHasPendulum
                ? (long)_poseReferenceCurrentPendulumRaw - _poseReferenceSavedPendulumZero
                : 0L;
            _poseReferenceArmMismatch = _poseReferenceHasArm && _poseReferenceArmDelta != 0;
            _poseReferencePendulumMismatch = _poseReferenceHasPendulum && _poseReferencePendulumDelta != 0;
            _poseRecheckArmZero = false;
            _poseRecheckArmMin = false;
            _poseRecheckArmMax = false;
            _poseRecheckPendulumZero = false;
            _poseRecheckPendulum360 = false;

            if (_poseReferenceArmMismatch || _poseReferencePendulumMismatch)
            {
                string message = BuildPoseMismatchReason();
                Log(LogLevel.Warn, "[标定] " + message);
                SetStatus("位置标定待确认：" + message, UiStyles.WarnOrange);
            }
            else
            {
                Log("[标定] 连接后位姿核对通过：当前 raw 与保存零点一致。");
            }
        }

        private string BuildPoseMismatchReason()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("连接后 raw 与保存零点不一致，自动动作（使能 / 起摆 / 平衡 / 回零）已暂停。");
            if (_poseReferenceHasArm)
            {
                sb.Append(" 摆臂：保存零点 raw=").Append(_poseReferenceSavedArmZero.ToString("N0"))
                  .Append("，当前 raw=").Append(_poseReferenceCurrentArmRaw.ToString("N0"))
                  .Append("，Δ=").Append(_poseReferenceArmDelta.ToString("N0")).Append(" counts；");
            }
            if (_poseReferenceHasPendulum)
            {
                sb.Append(" 摆杆：保存零点 raw=").Append(_poseReferenceSavedPendulumZero.ToString("N0"))
                  .Append("，当前 raw=").Append(_poseReferenceCurrentPendulumRaw.ToString("N0"))
                  .Append("，Δ=").Append(_poseReferencePendulumDelta.ToString("N0")).Append(" counts；");
            }
            sb.Append(" 机械在保存零点姿态就点「规整化左中右」，不确定就「清空标定」重新采集；不自动加偏移。");
            return sb.ToString();
        }

        /// <summary>重新采集 / 规整化完成一部分后调用：两点或三点凑齐才解除整体阻断。</summary>
        private void MaybeResolvePoseReference()
        {
            bool sessionPending = _poseSessionReviewRequired;
            bool reviewPending = _poseReferenceArmMismatch || _poseReferencePendulumMismatch ||
                                 _engine.CalibrationReviewRequired || sessionPending;
            bool completePending = _engine.CalibrationCleared &&
                                   _poseRecheckArmZero && _poseRecheckArmMin && _poseRecheckArmMax;
            if (!reviewPending && !completePending)
                return;

            bool armSettled = _poseRecheckArmZero && _poseRecheckArmMin && _poseRecheckArmMax;
            bool pendulumSettled = _poseRecheckPendulumZero &&
                                   (!_poseReferenceNeedsPendulum360 || _poseRecheckPendulum360);

            if (_poseReferenceArmMismatch && armSettled)
                _poseReferenceArmMismatch = false;

            if (_poseReferencePendulumMismatch && pendulumSettled)
                _poseReferencePendulumMismatch = false;

            // 会话标记的唯一写入口：只有「整组点位」在本次进程里被重新建立（规整化 / 全部重采）才盖戳。
            // 单采一个点不盖戳 —— 摆杆必须 0°/360° 成对，摆臂必须三点齐全且限位不是占位。
            CalibrationParameters calibration = _persistedExtras == null ? null : _persistedExtras.Calibration;
            if (calibration != null && armSettled && calibration.HasArmZeroCount &&
                !_limitsShadow.ArmLimitsArePlaceholder)
                calibration.MarkArmPoseCapturedInSession();
            if (calibration != null && pendulumSettled && calibration.HasPendulumZero &&
                calibration.HasPendulum360)
                calibration.MarkPendulumPoseCapturedInSession();

            if (completePending)
            {
                string completeReason;
                if (_engine.ConfirmCalibrationComplete(out completeReason))
                {
                    if (_persistedExtras != null)
                        _persistedExtras.MarkCalibrationComplete();
                    MarkParamsDirty("标定确认完成（驱动臂零点 + 左右限位）");
                    Log(LogLevel.Warn, "[标定] 已重新采集驱动臂零点 + 左 / 右限位，标定确认完成，使能资格恢复；"
                        + "参数文件里的 calibration_cleared 已置 false（保存参数后持久化）。");
                }
                else
                {
                    SetStatus("标定仍未完整：" + completeReason, UiStyles.WarnOrange);
                }
            }

            if (sessionPending)
            {
                // 两组都已在本次进程确认（文件里没有的那一组不算待确认）→ 解除会话阻断。
                bool armOk = calibration == null || !calibration.HasArmZeroCount || calibration.ArmPoseSessionVerified;
                bool pendOk = calibration == null ||
                              (!calibration.HasPendulumZero && !calibration.HasPendulum360) ||
                              calibration.PendulumPoseSessionVerified;
                if (armOk && pendOk)
                {
                    _poseSessionReviewRequired = false;
                    _engine.ConfirmPoseSessionReview();
                    Log(LogLevel.Warn, "[标定] 位置标定已在本次进程确认（会话标记已更新为 " +
                        CalibrationSession.DescribeCurrent() + "）：保存参数后该确认状态跨进程才作废。");
                }
            }

            if (_poseReferenceArmMismatch || _poseReferencePendulumMismatch || _poseSessionReviewRequired)
            {
                List<string> pendingParts = new List<string>();
                if (_poseReferenceArmMismatch) pendingParts.Add("摆臂左 / 零 / 右三点");
                if (_poseReferencePendulumMismatch) pendingParts.Add("摆杆 0° / 360° 两点");
                if (_poseSessionReviewRequired) pendingParts.Add("本次进程的位姿确认");
                SetStatus("位置标定仍待确认：还差 " + string.Join("、", pendingParts.ToArray()) +
                    "，补齐后自动动作才恢复。", UiStyles.WarnOrange);
                return;
            }

            _engine.ConfirmCalibrationReview();
            Log(LogLevel.Warn, "[标定] 位置基准已重新采集 / 规整化，连接后位姿阻断已解除。");
            SetStatus(_engine.CalibrationCleared
                    ? "位置标定待确认已解除，但标定仍为空：采集驱动臂零点 + 左右限位后才能使能。"
                    : "位置标定已确认，自动动作恢复。",
                _engine.CalibrationCleared ? UiStyles.WarnOrange : UiStyles.OkGreen);
        }

        private bool TryPassStartGate(out string reason, out string category)
        {
            string poseReason;
            if (!CheckPoseReferenceForMotion(out poseReason))
            {
                category = REJECT_INTERLOCK;
                reason = poseReason;
                return false;
            }
            if (_jogHeldDirection != 0)
            {
                category = REJECT_INTERLOCK;
                reason = "手动点动中（手动与启动互斥），松开手动按钮再启动。";
                return false;
            }
            if (_engine.IsControlledStopActive)
            {
                category = REJECT_INTERLOCK;
                reason = "受控停进行中，等停完再启动。";
                return false;
            }
            EngineState state = _engine.State;
            if (state != EngineState.Enabled && state != EngineState.Stopped)
            {
                category = REJECT_NOT_ENABLED;
                reason = "不在已使能（就绪）/ 已停态，当前 " + FormatState(state, _lastEStopLatch) + "。先「使能」。";
                return false;
            }
            if (_engine.PendulumCountsPerRev <= 0)
            {
                category = REJECT_NOT_CALIBRATED;
                reason = "杆每转计数未标定（≤0），起摆会被拒。先在「设备参数 / 软限位」填杆每转计数。";
                return false;
            }
            if (!_engine.IsBusInOp)
            {
                category = REJECT_INTERLOCK;
                reason = "总线不在 OP（当前 " + _engine.BusStateName + "）。";
                return false;
            }
            PendulumTelemetry t;
            if (!_engine.TryReadTelemetry(out t))
            {
                category = REJECT_OTHER;
                reason = "遥测快照读不到，总线状态不可判定。";
                return false;
            }
            if (!t.OwnedSlavesResponding)
            {
                category = REJECT_INTERLOCK;
                reason = "自有从站（GCAN + 步进驱动）未在响应，WKC " + t.ActualWkc + "/" + t.ExpectedWkc + "。";
                return false;
            }
            if (!t.ArmSlaveInOp || !t.GcanSlaveInOp)
            {
                category = REJECT_INTERLOCK;
                reason = "自有从站不在 OP（步进=" + t.ArmSlaveInOp + "，GCAN=" + t.GcanSlaveInOp + "）。";
                return false;
            }
            if (t.ExpectedWkc == 0 || t.ActualWkc != t.ExpectedWkc)
            {
                category = REJECT_INTERLOCK;
                reason = "WKC 不满（" + t.ActualWkc + "/" + t.ExpectedWkc + "）。";
                return false;
            }
            if (_engine.CalibrationCleared)
            {
                category = REJECT_NOT_CALIBRATED;
                reason = "位置标定已清空（跨重启标志生效），起摆被拒：先重新采集「驱动臂零点 + 左限位 + 右限位」并确认；"
                    + "摆杆还要采集 0° 点。";
                return false;
            }
            if (!_engine.PendulumZeroCountSet)
            {
                category = REJECT_NOT_CALIBRATED;
                reason = "摆杆 0° 零点未标定（参数文件 / 本次会话没有用户保存的 0° 点），起摆会被拒：" +
                    "先「停止」，把摆杆放到自然下垂位，点「摆杆零点 (下垂 0°)」采集后再起摆。";
                return false;
            }
            // 0°/360° 必须成对：只有一半不是「可运行标定」。判据唯一来源 = CheckPendulumPair，
            // 文案由它给出（点名缺哪一半），这里不另写一份。
            PendulumPairCheck pair = CalibrationParameters.CheckPendulumPair(
                _persistedExtras == null ? null : _persistedExtras.Calibration);
            if (!pair.IsComplete)
            {
                category = REJECT_NOT_CALIBRATED;
                reason = "摆杆 0°/360° 标定不完整，起摆被拒：" + pair.Reason;
                return false;
            }
            reason = null;
            category = null;
            return true;
        }

        private void JogButtonEvents(Button button, int direction)
        {
            button.MouseDown += delegate { JogHeld(direction); };
            button.MouseUp += delegate { JogReleased(); };
            button.MouseLeave += delegate { JogReleased(); };
        }

        private void JogHeld(int direction)
        {
            _jogHeldDirection = direction;
            RefreshButtons();
            EngineState state = _engine.State;
            if (state != EngineState.Enabled)
            {
                string text = "点动无效：只在已使能（就绪）态可点动，当前 " + FormatState(state, _lastEStopLatch) + "。";
                LogReject("Jog(手动点动)", REJECT_NOT_ENABLED,
                    "只在已使能（就绪）态可点动，当前 " + FormatState(state, _lastEStopLatch), null);
                SetStatus(text, Color.Firebrick);
                return;
            }
            string rejectReason;
            if (_engine.TryJogStart(direction, out rejectReason) && _engine.IsJogActive)
            {
                Log(direction < 0 ? "手动点动 ◀（-φ）中，松开即停。" : "手动点动 ▶（+φ）中，松开即停。");
                SetStatus(direction < 0 ? "手动点动 ◀（-φ）中，松开即停。" : "手动点动 ▶（+φ）中，松开即停。");
                return;
            }
            FaultCode code = CurrentFaultCode();
            LogReject("Jog(手动点动)", ClassifyEngineReject(code),
                rejectReason ?? "JogStart 后点动未激活（引擎侧门拒绝）", code);
            SetStatus("点动被拒：" + (rejectReason ?? "引擎侧门拒绝，见日志。"), UiStyles.WarnOrange);
        }

        private void JogReleased()
        {
            if (_jogHeldDirection == 0)
                return;
            _jogHeldDirection = 0;
            _engine.JogStop();
            RefreshButtons();
            if (_engine.State == EngineState.Enabled && !_engine.IsControlledStopActive)
            {
                Log("手动点动已松开：关节保持在当前位置。");
                SetStatus("手动点动已松开。");
            }
        }

        private EngineState WaitForState(EngineState target, int timeoutMs)
        {
            long until = Environment.TickCount + timeoutMs;
            while (true)
            {
                EngineState now = _engine.State;
                if (now == target || now == EngineState.Fault || now == EngineState.Disconnected)
                    return now;
                if (Environment.TickCount >= until)
                    return now;
                Thread.Sleep(50);
            }
        }

        private string DescribeDrive()        {
            PendulumTelemetry t;
            if (!_engine.TryReadTelemetry(out t))
                return "状态字 / 错误码不可用";
            return "状态字 0x" + t.StatusWord.ToString("X4") + "，错误码 0x" + t.DriveErrorCode.ToString("X4");
        }

        /// <summary>
        /// 「停止」：让摆立刻停止左右晃动并落到「已停（去使能）」。
        /// <para>
        /// 现场安全顺序（控制层 HandleStopCommand 执行，这里只发请求）：
        /// ① 先取消点动 / 作废等待态捕获，把逻辑上的运动请求清掉；
        /// ② 目标位置钉在**实测** 0x6064 上，避免任何一拍指令跳变；
        /// ③ 0x0002 Quick Stop，按 0x6085 急停减速度斜坡减速，确认驱动器进 QuickStopActive；
        /// ④ 最后 0x0000 Disable Voltage 去使能，状态机回 Stopped。
        /// </para>
        /// <para>
        /// 为什么不是「先断使能」：0x0000 会直接切掉力矩，摆还在摆、摆臂就会自由滑行，
        /// 既停不住也保不住位置；先斜坡减速再断使能，才是能停在原地且不甩负载的顺序。
        /// 幂等：连点只是重复下发同一个 CmdStop，引擎侧 `_stopPhase` 已经在跑就不再重置预算，
        /// 重复点击既不报错也不会把停机时间无限延长；本来就停了则直接收尾回 Stopped。
        /// </para>
        /// </summary>
        private void StopClicked(object sender, EventArgs e)
        {
            // 停止是安全方向：不走普通操作互斥锁，故障或另一项慢操作进行中也能下发。
            Log("[操作] 停止 开始");
            SetStatus("停止执行中…界面不阻塞，随时可急停。");
            Task.Run(delegate
            {
                try
                {
                    _engine.Stop();
                    EngineState reached = WaitForState(EngineState.Stopped, 5000);
                    if (reached == EngineState.Stopped)
                    {
                        Log("停止完成：受控停（QuickStop 路径）走完，已去使能，摆不再被驱动。");
                        SetStatus("已停止：受控停走完、已去使能，摆不再被驱动。");
                    }
                    else
                    {
                        Log("停止未落 Stopped（" + FormatState(reached, _lastEStopLatch) + "）：" + DescribeDrive());
                        SetStatus("停止未完成：" + DescribeDrive(), Color.Firebrick);
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, "[操作] 停止 失败：" + DescribeException(ex) +
                        (string.IsNullOrEmpty(ex.StackTrace) ? "" : "  " + FirstLines(ex.StackTrace, 3)));
                    SetStatus("停止失败：" + ex.Message, UiStyles.AlarmColor);
                }
                finally
                {
                    RequestButtonsRefresh();
                }
            });
        }

        private void EStopClicked(object sender, EventArgs e)
        {
            Log("[急停] 请求立即安全态：θ " + _lastThetaAbsDeg.ToString("F1") + "°，u " + _lastUPercent.ToString("F1") +
                "%，u=0、步进断使能");
            SetStatus("急停已下发：带外写 0x6040=0。确认机械安全后「复位」或「断开」。", Color.Firebrick);
            PendulumEngine engine = _engine;
            Task.Run(delegate
            {
                try
                {
                    engine.EmergencyStop();
                    Log("[急停] 已下发：0x6040=0 断使能（成败见引擎诊断行）。");
                }
                catch (Exception ex)
                {
                    Log("[急停] 下发异常：" + DescribeException(ex));
                }
            });
        }

        private void ResetClicked(object sender, EventArgs e)
        {
            RunOp("故障复位", "已受理", delegate
            {
                _engine.Stop();
                Log("[复位] 受控停进行中，清锁存后回到已停（已去使能）。");
                SetStatus("故障复位已受理：受控停进行中，等状态回到「已停」再确认机械安全、重新「使能」。");
            });
        }

        private void DisconnectClicked(object sender, EventArgs e)
        {
            RunOp("断开", delegate
            {
                _engine.Disconnect();
                Log("断开完成：停 PDO/计数采集 → 断 EtherCAT → 关计数通道。");
            });
        }

        private string DescribeBus()
        {
            try
            {
                EngineDiagnostics diag = _engine.Diagnostics;
                string cycle = diag.NativeTimingValid && diag.BusCycleHz > 0
                    ? (1000.0 / diag.BusCycleHz).ToString("F2") + " ms（" + diag.BusCycleHz.ToString("F0") + " Hz）"
                    : "不可用";
                return "WKC " + diag.ActualWkc + "/" + diag.ExpectedWkc + "，周期 " + cycle;
            }
            catch (Exception ex)
            {
                return "总线诊断读取失败：" + DescribeException(ex);
            }
        }

        // ================================================================ 标定与限位（原始计数）
        //
        // 现场只有 5 个位置，**全部是原始计数**：左限位 / 驱动臂零点 / 右限位（驱动臂三点，左 < 驱动臂零点 < 右）
        // 与 摆杆零点（下垂 0°）/ 摆杆 360°（摆杆两点，一圈的量 = |360° − 0°|，只显示不输入）。
        // 界面不做任何「计数 ↔ 角度」的限位换算：限位判定与钳位都在控制层的 counts 域，
        // 摆杆角度只由 counts 推导**显示**（(raw − 0° 点) 归一化到一圈），不是输入项。

        private void ArmZeroCountChanged(object sender, EventArgs e)
        {
            if (_suppressCalibrationEvents)
                return;
            _armZeroDirty = true;
            ScheduleParamApply();
        }

        private void LimitCountsChanged(object sender, EventArgs e)
        {
            if (_suppressCalibrationEvents)
                return;
            _limitCountsDirty = true;
            ScheduleParamApply();
        }

        /// <summary>「设置 0 点」：取当前 0x6064 → 填进原始计数框 → 立刻写进控制层 → 落盘。</summary>
        private void SetArmZeroClicked(object sender, EventArgs e)
        {
            int raw;
            if (!TryReadLiveArmPosition(out raw))
            {
                LogReject("SetArmZero(设置 0 点)", REJECT_NOT_ENABLED,
                    "0x6064 没有正在跑的实时值（未连接 / 未进 OP / 遥测停更），0 点未设置", null);
                SetStatus("0 点未设置：0x6064 没有实时值。", UiStyles.AlarmColor);
                return;
            }
            _suppressCalibrationEvents = true;
            try
            {
                SetNumber(_armZeroCountBox, raw);
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
            _armZeroDirty = false;
            if (!ApplyArmZero(raw, "捕获当前位置"))
                return;
            ShowArmPositionStatus("0 点已设置", raw);
        }

        /// <summary>
        /// 把摆臂 0 点写进控制层并读回验证；失败时保持旧值并把输入框还原（不假装成功）。
        /// <para>
        /// 软限位是**绝对物理计数**，与零点无关：改 0 点只换坐标原点，左右限位一个字都不用动，
        /// 所以这里既没有角度换算，也没有「按新零点保住旧物理限位」的重算与回滚。
        /// </para>
        /// </summary>
        private bool ApplyArmZero(int raw, string source)
        {
            EngineState state = _engine.State;
            // 点动中（Enabled）允许设 0 点：现场标准用法就是「点动到期望位置 → 设成 0 点」。
            // 控制层会把零点请求放到下一控制拍，并把目标 / 参考角同步平移，位置指令不跳变。
            // 起摆 / 平衡中仍拒绝：φ 参与控制律与能量估计，换坐标等于换控制相位。
            if (state == EngineState.Swinging || state == EngineState.Balancing)
            {
                LogReject("SetArmZero(设置 0 点)", REJECT_INTERLOCK,
                    "当前 " + FormatState(state, _lastEStopLatch) + " 不能改 0 点（φ 参与控制律），写入被拒", null);
                SetStatus("0 点未写入：先「停止」或结束起摆 / 平衡，再设置 0 点。", UiStyles.WarnOrange);
                RestoreArmZeroBox();
                return false;
            }

            int oldZero = _engine.ArmZeroCount;
            string writeReason;
            if (!_engine.TrySetArmZeroCount(raw, out writeReason))
            {
                LogReject("SetArmZero(设置 0 点)", REJECT_OTHER,
                    "0 点未写入：" + writeReason + "（请求 raw=" + raw + "）", null);
                SetStatus("0 点未写入：" + writeReason, UiStyles.WarnOrange);
                RestoreArmZeroBox();
                return false;
            }
            int applied = _engine.ArmZeroCount;
            if (applied != raw)
            {
                LogReject("SetArmZero(设置 0 点)", REJECT_OTHER,
                    "0 点未写入：控制层仍是 raw=" + applied + "（请求 " + raw + "），检查是否在使能流程中", null);
                SetStatus("0 点未写入：控制层仍是 raw=" + applied + "。", UiStyles.WarnOrange);
                RestoreArmZeroBox();
                return false;
            }
            // 三点关系校验：左限位 < 驱动臂零点 < 右限位。越序 = 参数错误，写入回滚。
            if (!(_limitsShadow.ArmMinCount < raw && raw < _limitsShadow.ArmMaxCount))
            {
                _engine.SetArmZeroCount(oldZero);
                LogReject("SetArmZero(设置 0 点)", REJECT_OTHER,
                    "0 点未写入：raw=" + raw + " 必须严格在左限位 " + _limitsShadow.ArmMinCount +
                    " 与右限位 " + _limitsShadow.ArmMaxCount + " 之间（左 < 驱动臂零点 < 右），写入已回滚", null);
                SetStatus("0 点未写入：驱动臂零点必须严格在左右限位之间。", UiStyles.WarnOrange);
                RestoreArmZeroBox();
                return false;
            }

            _poseRecheckArmZero = true;
            MaybeResolvePoseReference();
            Log("[标定] 摆臂 0 点已设为 raw=" + raw + "（" + source + "）。左右限位是绝对 counts，保持不动。");
            SetStatus("摆臂 0 点已设为 raw=" + raw + "。");
            MarkParamsDirty("摆臂 0 点 raw=" + raw);
            RefreshLimitCountFields();
            RefreshCalibrationCard();
            RefreshSafetyText();
            return true;
        }

        private void RestoreArmZeroBox()
        {
            _suppressCalibrationEvents = true;
            try
            {
                SetNumber(_armZeroCountBox, _engine.ArmZeroCount);
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
        }

        /// <summary>「设置左 / 右限位」：取当前 0x6064，按原始计数写进对应限位并立刻下发。</summary>
        private void SetLeftLimitClicked(object sender, EventArgs e)
        {
            SetLimitFromCurrent(false);
        }

        private void SetRightLimitClicked(object sender, EventArgs e)
        {
            SetLimitFromCurrent(true);
        }

        private void SetLimitFromCurrent(bool upper)
        {
            int raw;
            if (!TryReadLiveArmPosition(out raw))
            {
                LogReject("SetLimit(限位采集)", REJECT_NOT_ENABLED,
                    "0x6064 没有正在跑的实时值（未连接 / 未进 OP / 遥测停更），限位未采集", null);
                SetStatus("限位未采集：0x6064 没有实时值，先「连接」。", UiStyles.AlarmColor);
                return;
            }
            if (!ApplyLimitFromRaw(raw, upper, "捕获当前位置"))
                return;
            ShowArmPositionStatus(upper ? "右限位已设置" : "左限位已设置", raw);
        }

        /// <summary>
        /// 原始计数直接写进 SafetyLimits（counts 域，无角度换算）。另一侧沿用当前生效值，
        /// 避免拿可能正在编辑的旧框值互相打架。成功返回 true。
        /// </summary>
        private bool ApplyLimitFromRaw(int raw, bool upper, string source)
        {
            int minCount = upper ? _limitsShadow.ArmMinCount : raw;
            int maxCount = upper ? raw : _limitsShadow.ArmMaxCount;
            if (!ApplyLimitsCounts(minCount, maxCount, true, source + " raw=" + raw))
                return false;
            _limitCountsDirty = false;
            return true;
        }

        /// <summary>
        /// 把一次位置采集（摆杆零点 / 摆臂 0 点 / 左限位 / 右限位）的口径显示出来：
        /// 绝对 0x6064 + 相对 0 点的计数偏移。**不再显示任何摆臂角度**（角度不是本机参数）。
        /// </summary>
        private void ShowArmPositionStatus(string action, int raw)
        {
            int zero = _engine.ArmZeroCount;
            int offset = unchecked(raw - zero);
            string text = action + "：0x6064=" + raw.ToString("N0") +
                "，相对 0 点 " + (offset >= 0 ? "+" : "") + offset.ToString("N0") +
                " counts。尚未保存。";
            SetStatus(text, UiStyles.WarnOrange);
            Log("[标定] " + text);
        }

        // ---------------------------------------------------------------- 摆杆 0°/360° 两点标定
        //
        // 用户模型：一圈的量是**标定出来的**，不是输入进去的。两个采集点 = 摆杆零点（下垂 0°）与摆杆 360°，
        // 一圈的量 = |360° count − 0° count|（见 UpdateDerivedPendulumCpr）。没有单独的一圈标定按钮。

        /// <summary>「摆杆零点（下垂 0°）」采集：当前 CH1 原始计数 → 填框 + 写成摆杆零点。</summary>
        private void SetPendulumZeroClicked(object sender, EventArgs e)
        {
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry) || telemetry.TimestampTicks <= 0)
            {
                LogReject("SetPendulumZero(摆杆零点 0°)", REJECT_NOT_ENABLED,
                    "摆杆计数没有实时值（未连接 / 未进 OP / 遥测停更），0° 点未采集", null);
                SetStatus("摆杆零点未采集：没有实时计数。", UiStyles.AlarmColor);
                return;
            }
            int raw = telemetry.PendulumCount;
            _suppressCalibrationEvents = true;
            try { SetNumber(_pendulumZeroCountBox, raw); }
            finally { _suppressCalibrationEvents = false; }
            _pendulumZeroDirty = false;
            if (!ApplyPendulumZeroCount(raw, "采集 0° 点"))
                return;
            // 状态行 / 日志 / 一圈量已由 ApplyPendulumZeroCount 写全（含 360° 点平移 / 作废回执），
            // 这里不再重复派生一圈量 —— 重复调用会把刚写的回执盖成「摆杆一圈的量 = …」。
        }

        /// <summary>「摆杆 360°」采集：当前 CH1 原始计数 → 填框，并重算一圈的量。</summary>
        private void SetPendulum360Clicked(object sender, EventArgs e)
        {
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry) || telemetry.TimestampTicks <= 0)
            {
                LogReject("SetPendulum360(摆杆 360°)", REJECT_NOT_ENABLED,
                    "摆杆计数没有实时值（未连接 / 未进 OP / 遥测停更），360° 点未采集", null);
                SetStatus("摆杆 360° 未采集：没有实时计数。", UiStyles.AlarmColor);
                return;
            }
            int raw = telemetry.PendulumCount;
            _suppressCalibrationEvents = true;
            try { SetNumber(_pendulum360CountBox, raw); }
            finally { _suppressCalibrationEvents = false; }
            _pendulum360Dirty = false;
            ApplyPendulum360Count(raw, "采集 360° 点");
        }

        private bool TryReadLiveArmPosition(out int raw)
        {
            raw = 0;
            PendulumTelemetry telemetry;
            if (!TryReadTelemetry(out telemetry) || telemetry.TimestampTicks <= 0)
                return false;
            long age = Stopwatch.GetTimestamp() - telemetry.TimestampTicks;
            if (age < 0 || age > (long)(Stopwatch.Frequency * LIVE_ARM_FRESH_MS / 1000.0))
                return false;
            raw = telemetry.ArmPositionCount;
            return true;
        }

        // ================================================================ 参数（写进控制层）

        private void PidParamChanged(object sender, EventArgs e)
        {
            if (_suppressParamEvents)
                return;
            _pidDirty = true;
            ScheduleParamApply();
        }

        private void PumpParamChanged(object sender, EventArgs e)
        {
            if (_suppressParamEvents)
                return;
            _pumpDirty = true;
            ScheduleParamApply();
        }

        private void PendulumZeroCountChanged(object sender, EventArgs e)
        {
            if (_suppressParamEvents)
                return;
            _pendulumZeroDirty = true;
            ScheduleParamApply();
        }

        private void Pendulum360CountChanged(object sender, EventArgs e)
        {
            if (_suppressParamEvents)
                return;
            _pendulum360Dirty = true;
            ScheduleParamApply();
        }

        private void ScheduleParamApply()
        {
            if (_paramApplyTimer == null)
                return;
            _paramApplyTimer.Stop();
            _paramApplyTimer.Start();
        }

        private void OnParamApplyTick(object sender, EventArgs e)
        {
            _paramApplyTimer.Stop();
            if (_pidDirty)
            {
                _pidDirty = false;
                ApplyPidParams();
            }
            if (_pumpDirty)
            {
                _pumpDirty = false;
                ApplyPumpParams();
            }
            // 标定卡：手输原始计数同样 250 ms 去抖后立刻生效（点击即用，不再要二次确认）。
            if (_armZeroDirty)
            {
                _armZeroDirty = false;
                ApplyArmZero((int)_armZeroCountBox.Value, "手输原始计数");
            }
            if (_pendulumZeroDirty)
            {
                _pendulumZeroDirty = false;
                ApplyPendulumZeroCount((int)_pendulumZeroCountBox.Value, "手输原始计数");
            }
            if (_pendulum360Dirty)
            {
                _pendulum360Dirty = false;
                ApplyPendulum360Count((int)_pendulum360CountBox.Value, "手输原始计数");
            }
            if (_limitCountsDirty)
            {
                _limitCountsDirty = false;
                ApplyLimitsFromCountBoxes();
            }
        }

        /// <summary>手输左 / 右限位的绝对原始计数 → SafetyLimits（counts 域，不换算角度）。左右一起取。</summary>
        private void ApplyLimitsFromCountBoxes()
        {
            int minRaw = (int)_limitMinCountsBox.Value;
            int maxRaw = (int)_limitMaxCountsBox.Value;
            if (!ApplyLimitsCounts(minRaw, maxRaw, true, "手输原始计数 " + minRaw + " / " + maxRaw))
                RefreshLimitCountFields();
        }

        /// <summary>
        /// 卡片④：把界面上这组参数写进 BalancePid（控制层双缓冲，下一拍顶换）。
        /// Ka / Kad / Kai 是历史参数（平衡环已由整套状态反馈接管），只随参数文件读写，不参与平衡，
        /// 也不再决定这一批能不能写 —— 旧实现按「Ka 必须高于结构下界」整批拒收，会让同一次应用里
        /// 真正生效的限幅 / 内环 / 输出项被一个调了也没有效果的数一起挡掉。
        /// </summary>
        private void ApplyPidParams()
        {
            _pidApplyRejected = false;
            PidGains gains = _pidShadow;
            gains.Ka = (double)_kpBox.Value;
            gains.Kad = (double)_kiBox.Value;
            gains.Kai = (double)_kdBox.Value;
            // 合法性复核只针对真正生效的项（AccMax / ArmRateMax / 内环 φ / 微分低通 / 输出与积分限幅 / 死区）。
            // 复核用的副本先把三个历史字段换回已生效的影子值，历史框里的数不会连累整批。
            PidGains probe = gains;
            probe.Ka = _pidShadow.Ka;
            probe.Kad = _pidShadow.Kad;
            probe.Kai = _pidShadow.Kai;
            string reason;
            if (!probe.IsValid(out reason))
            {
                _pidApplyRejected = true;
                LogReject("SetBalancePid(PID 参数)", REJECT_OTHER, "PID 参数无效，未写入：" + reason, null);
                SetStatus("PID 参数无效，未写入：" + reason, UiStyles.WarnOrange);
                return;
            }
            _pidShadow = gains;
            _engine.SetBalancePid(gains);
            UpdatePidLawHint();
            RefreshUFullScale();
            MarkParamsDirty("PID 参数（Ka/Kad/Kai 为历史值）");
        }

        /// <summary>卡片④：把泵增益 / 目标幅度 / 泵频 / 相位写进 SwingUpParams。</summary>
        private void ApplyPumpParams()
        {
            PumpParameters pump = PumpParameters.From(_swingShadow);
            pump.Ke = (double)_keBox.Value;
            pump.PumpAmpDeg = (double)_pumpAmpBox.Value;
            pump.PumpHz = (double)_pumpFreqBox.Value;
            pump.PhaseOffsetDeg = (double)_pumpPhaseBox.Value;
            string reason;
            if (!pump.IsValid(out reason))
            {
                LogReject("SetSwingUp(能量泵)", REJECT_OTHER, "泵参数无效，未写入：" + reason, null);
                SetStatus("能量泵参数无效，未写入：" + reason, UiStyles.WarnOrange);
                UpdatePumpStatusLabel();
                return;
            }
            if (_engine.State == EngineState.Swinging)
            {
                LogReject("SetSwingUp(能量泵)", REJECT_INTERLOCK, "起摆中，泵参数未改", null);
                SetStatus("起摆中：泵参数暂不修改（等起摆结束再改）。", UiStyles.WarnOrange);
                UpdatePumpStatusLabel();
                return;
            }
            SwingUpParams p = _swingShadow;
            pump.ApplyTo(ref p);
            if (!p.IsValid(out reason))
            {
                LogReject("SetSwingUp(能量泵)", REJECT_OTHER, "换算后的控制层泵参数无效，未写入：" + reason, null);
                SetStatus("能量泵参数换算失败，未写入：" + reason, UiStyles.WarnOrange);
                UpdatePumpStatusLabel();
                return;
            }
            _swingShadow = p;
            _engine.SetSwingUpParams(p);
            if (_persistedExtras != null)
                _persistedExtras.Pump = pump;
            UpdatePumpStatusLabel();
            MarkParamsDirty("能量泵参数");
        }

        /// <summary>
        /// 把软限位写进 SafetyLimits（控制环在 counts 域钳目标）。参数就是绝对 0x6064 计数，
        /// 这里不换算、不猜比例：唯一校验是 min &lt; max 与 ±2e9 的合理区间。
        /// </summary>
        private bool ApplyLimitsCounts(int minCount, int maxCount, bool logWidening, string source)
        {
            if (!(minCount < maxCount))
            {
                LogReject("SetSafetyLimits(软限位)", REJECT_OTHER,
                    "左限位计数必须小于右限位（当前 " + minCount + " / " + maxCount + " counts），未写入", null);
                SetStatus("限位未写入：左限位计数必须小于右限位。", UiStyles.WarnOrange);
                return false;
            }
            // 三点关系校验：左限位 < 驱动臂零点 < 右限位（用户模型）。驱动臂零点越序 = 参数错误，整对限位不写。
            int zeroCount = _engine.ArmZeroCount;
            if (!(minCount < zeroCount && zeroCount < maxCount))
            {
                LogReject("SetSafetyLimits(软限位)", REJECT_OTHER,
                    "限位未写入：驱动臂零点 raw=" + zeroCount + " 必须严格在左 / 右限位之间（" +
                    minCount + " < 驱动臂零点 < " + maxCount + "）", null);
                SetStatus("限位未写入：驱动臂零点必须严格在左右限位之间（左 < 驱动臂零点 < 右）。", UiStyles.WarnOrange);
                return false;
            }
            SafetyLimits s = _limitsShadow;
            bool widened = minCount < s.ArmMinCount || maxCount > s.ArmMaxCount;
            int oldMinCount = s.ArmMinCount;
            int oldMaxCount = s.ArmMaxCount;
            s.ArmMinCount = minCount;
            s.ArmMaxCount = maxCount;
            string reason;
            if (!s.IsValid(out reason))
            {
                LogReject("SetSafetyLimits(软限位)", REJECT_OTHER, "软限位无效，未写入：" + reason, null);
                SetStatus("限位未写入：" + reason, UiStyles.WarnOrange);
                return false;
            }
            _limitsShadow = s;
            _engine.SetSafetyLimits(s);
            _poseRecheckArmMin = true;
            _poseRecheckArmMax = true;
            MaybeResolvePoseReference();
            string text = minCount.ToString("N0") + " ~ " + maxCount.ToString("N0") + " counts";
            string pending = _engine.State == EngineState.Disconnected ? "（未连接：连接后生效）" : "";
            Log("限位已下发：" + text + pending + "（" + source + "）。");
            if (widened && logWidening)
            {
                Log(LogLevel.Warn, "限位被放宽：原 " + oldMinCount.ToString("N0") + " ~ " +
                    oldMaxCount.ToString("N0") + " counts，现 " + text + "（放宽已留痕）。");
            }
            SetStatus("限位已下发：" + text + pending + "。");
            RefreshLimitCountFields();
            RefreshSafetyText();
            MarkParamsDirty("软限位 " + text);
            return true;
        }

        private void UpdatePumpStatusLabel()
        {
            if (_pumpStatusLabel == null)
                return;
            PumpParameters pump = PumpParameters.From(_swingShadow);
            string text = "已下发 k_e=" + UiStyles.Gain(pump.Ke) + "\r\n" +
                pump.PumpAmpDeg.ToString("F1") + "° · " + pump.PumpHz.ToString("F2") + " Hz · 相位 " + pump.PhaseOffsetDeg.ToString("F1") + "°";
            bool swinging = _engine.State == EngineState.Swinging;
            UiStyles.SetLabelColored(_pumpStatusLabel, text, swinging ? UiStyles.WarnOrange : UiStyles.CaptionText);
        }

        /// <summary>
        /// 把当前生效的限位回填到标定卡的两个计数框（直接就是 counts，无换算）。
        /// 正在编辑的框不覆盖（避免和用户输入打架）。
        /// </summary>
        private void RefreshLimitCountFields()
        {
            if (_limitMinCountsBox == null || _limitMaxCountsBox == null)
                return;
            _suppressCalibrationEvents = true;
            try
            {
                if (!_limitMinCountsBox.Focused)
                {
                    if (_limitsShadow.ArmLimitsArePlaceholder)
                        ShowNumberUncalibrated(_limitMinCountsBox);
                    else
                        ShowNumberCalibrated(_limitMinCountsBox, _limitsShadow.ArmMinCount);
                }
                if (!_limitMaxCountsBox.Focused)
                {
                    if (_limitsShadow.ArmLimitsArePlaceholder)
                        ShowNumberUncalibrated(_limitMaxCountsBox);
                    else
                        ShowNumberCalibrated(_limitMaxCountsBox, _limitsShadow.ArmMaxCount);
                }
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
        }

        private double EffectiveArmCountsPerRev()
        {
            try
            {
                int resolved = _engine.ResolvedArmCountsPerRev;
                if (resolved > 0)
                    return resolved;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _readErrors) <= UI_LOG_ERROR_LIMIT)
                    Log(LogLevel.Warn, "读一圈标定失败，换算用界面值：" + DescribeException(ex));
            }
            // 界面不再维护「细分 / 每圈计数」第二条换算：用控制层实读值，读不到才用参数文件恢复的兜底值。
            double ui = _persistedArmCountsPerRev > 0
                ? (double)_persistedArmCountsPerRev
                : (double)DEFAULT_ARM_COUNTS_PER_REV;
            return ui > 0.0 ? ui : (double)DEFAULT_ARM_COUNTS_PER_REV;
        }

        /// <summary>
        /// 控制参数对话框：内环 Kpφ / Kiφ / Kdφ + 真正生效的两个限幅 AccMax / ArmRateMax
        /// + 起摆与切换（能量增益 ε / 起摆 AMax / 切换 θ·ω / 捕获臂速上限 / 起摆回中 Kφ / 起摆阻尼 Kφ̇）
        /// + 摆臂角速度上限 ω_arm。
        /// Ka / Kad / Kai 与平衡 Kφ / Kφ̇ 是历史参数（平衡环已由整套状态反馈接管），只随参数文件读写、不影响平衡；
        /// 泵四项仍以卡片「PID 参数」为准，对话框不重复、不覆盖。
        /// 点「确定」后三组参数经控制层同一个 generation 双缓冲通道下发（下一拍顶换），不写文件；
        /// 起摆参数先过 CLI 同口径状态门（Connected / Balancing / Fault 整组不写入，Swinging 只放行热改项），
        /// 内环 / 限幅 / 安全阈值照常下发。落盘仍由左栏「保存参数」。对话框内的「自动调参…」走
        /// <see cref="IAutoTuneHost"/>（本窗体实现），可完成前置检查 → 自由摆动采集 → 识别 → 算增益 →
        /// 试跑 → 采用保存 / 回滚整条流程。
        /// </summary>
        private void PidTuningClicked(object sender, EventArgs e)
        {
            // 起摆参数写入门与 CLI setswingup 同一套（SwingParamGate 是 UI 唯一实现：
            // Connected / Balancing / Fault 整组锁定，Swinging 只放行 CLI 热改项）。
            string swingAccessReason;
            SwingParamAccess swingAccess = SwingParamGate.Resolve(_engine.State, out swingAccessReason);
            using (PidTuningDialog dialog = new PidTuningDialog(_pidShadow, _swingShadow, _limitsShadow,
                swingAccess, swingAccessReason, this))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                PidGains gains;
                SwingUpParams swing;
                SafetyLimits limits;
                string error;
                if (!dialog.TryGetParams(out gains, out swing, out limits, out error))
                {
                    LogReject("SetGains/SetSwingUp(控制参数)", REJECT_OTHER, "控制参数无效，未写入：" + error, null);
                    SetStatus("控制参数无效，未写入：" + error, UiStyles.WarnOrange);
                    return;
                }

                // 模态期间状态可能变化：按「确定」这一刻重新过门（CLI 也是按命令到达时的状态判）。
                SwingParamAccess accessNow = SwingParamGate.Resolve(_engine.State, out string swingReject);
                bool applySwing = accessNow != SwingParamAccess.Locked;
                if (applySwing && accessNow == SwingParamAccess.HotEditOnly)
                {
                    string lockedChanges = SwingParamGate.DescribeLockedChanges(_swingShadow, swing);
                    if (lockedChanges != null)
                    {
                        // 打开时还可编辑，途中进了起摆：被锁字段的改动整组不写（不做半套生效）。
                        applySwing = false;
                        swingReject = lockedChanges;
                    }
                    else
                    {
                        swing = SwingParamGate.MergeHotEditFields(_swingShadow, swing);
                    }
                }

                _pidShadow = gains;
                _engine.SetBalancePid(gains);
                if (applySwing)
                {
                    _swingShadow = swing;
                    _engine.SetSwingUpParams(swing);
                }
                _limitsShadow = limits;
                _engine.SetSafetyLimits(limits);
                RefreshParamBoxesFromShadows();
                UpdatePidLawHint();
                RefreshSafetyText();
                if (!applySwing)
                {
                    LogReject("SetSwingUp(控制参数)", REJECT_INTERLOCK,
                        "起摆参数未写入：" + swingReject, null);
                    SetStatus("控制参数已下发（内环 / 限幅 / 安全阈值）；起摆参数未写入 —— " +
                        swingReject, UiStyles.WarnOrange);
                    MarkParamsDirty("控制参数（内环 / 限幅 / 安全）");
                    return;
                }
                Log("控制参数已应用（下一拍顶换）：加速度限幅=" + gains.AccMax.ToString("0.#", CultureInfo.InvariantCulture) +
                    " 参考角速度限幅=" + gains.ArmRateMax.ToString("F2", CultureInfo.InvariantCulture) +
                    " 内环 Kpφ=" + UiStyles.Gain(gains.KpPhi) +
                    " Kiφ=" + UiStyles.Gain(gains.KiPhi) +
                    " Kdφ=" + UiStyles.Gain(gains.KdPhi) +
                    "；历史项 Kφ=" + UiStyles.Gain(gains.KPhi) +
                    " Kφ̇=" + UiStyles.Gain(gains.KPhiDot) + "（随参数文件保存，不参与平衡）" +
                    "；起摆 ε=" + swing.TargetEnergyGain.ToString("F4", CultureInfo.InvariantCulture) +
                    " AMax=" + swing.AMax.ToString("F1", CultureInfo.InvariantCulture) +
                    " θ切=" + (swing.ThetaSwitchRad * 180.0 / Math.PI).ToString("F2", CultureInfo.InvariantCulture) + "°" +
                    " ω切=" + swing.OmegaSwitchRadPerSec.ToString("F2", CultureInfo.InvariantCulture) +
                    " 捕获臂速=" + swing.CaptureArmRateLimitRadPerSec.ToString("F2", CultureInfo.InvariantCulture) +
                    " 抽取增益=" + swing.ExtractGain.ToString("F2", CultureInfo.InvariantCulture) +
                    " 软中心带=" + swing.CenterBandRad.ToString("F2", CultureInfo.InvariantCulture) + " rad" +
                    " 起摆Kφ=" + UiStyles.Gain(swing.KPhi) + " 起摆Kφ̇=" + UiStyles.Gain(swing.KPhiDot) +
                    "；ω_arm上限=" + limits.OmegaArmMaxRadPerSec.ToString("F2", CultureInfo.InvariantCulture) + "。");
                SetStatus("控制参数已下发（下一拍顶换），未保存。");
                MarkParamsDirty("控制参数（内环 / 限幅 / 起摆切换 / 安全）");
            }
        }

        // ================================================================ 自动调参接线 + 参数落盘状态

        // ---- IAutoTuneHost：把自动调参对话框需要的引擎与「采用 / 保存 / 回滚」动作接到主界面 ----

        PendulumEngine IAutoTuneHost.Engine { get { return _engine; } }

        PidGains IAutoTuneHost.CurrentPid { get { return _pidShadow; } }

        SwingUpParams IAutoTuneHost.CurrentSwing { get { return _swingShadow; } }

        /// <summary>
        /// 把候选参数写进控制层与界面影子。只写参数、不保存；试跑失败时由对话框调
        /// <see cref="RestoreTuningBaseline"/> 回滚。写完后读回控制层核对，拒绝时给可读原因。
        /// <para>
        /// Ka / Kad / Kai 是历史参数（不参与平衡）：候选里它们就是当前值，这里既不因它们拒收，
        /// 读回也只核对真正生效的限幅 / 内环 / 输出项 —— 否则一个历史值会把整份候选连同泵推荐一起拒掉。
        /// </para>
        /// </summary>
        public bool ApplyTuningCandidate(PidGains pid, SwingUpParams swing, out string error)
        {
            error = null;
            string reason;
            if (!swing.IsValid(out reason))
            {
                error = "候选起摆参数无效：" + reason;
                return false;
            }
            _engine.SetBalancePid(pid);
            _engine.SetSwingUpParams(swing);
            PidGains appliedPid = _engine.BalancePid;
            if (!SameEffectiveGains(appliedPid, pid))
            {
                error = "控制层没有接受候选参数：请求加速度限幅 " + UiStyles.Gain(pid.AccMax) +
                    " rad/s² / 参考角速度限幅 " + UiStyles.Gain(pid.ArmRateMax) +
                    " rad/s，控制层当前 " + UiStyles.Gain(appliedPid.AccMax) +
                    " rad/s² / " + UiStyles.Gain(appliedPid.ArmRateMax) + " rad/s。";
                return false;
            }
            _pidShadow = appliedPid;
            _swingShadow = _engine.SwingUpParams;
            RefreshParamBoxesFromShadows();
            MarkParamsDirty("自动调参候选（试跑中）");
            Log("[自动调参] 候选已写入控制层（未保存）：加速度限幅=" +
                _pidShadow.AccMax.ToString("F0", CultureInfo.InvariantCulture) + " rad/s²  参考角速度限幅=" +
                _pidShadow.ArmRateMax.ToString("F1", CultureInfo.InvariantCulture) + " rad/s  内环 Kpφ/Kiφ/Kdφ=" +
                UiStyles.Gain(_pidShadow.KpPhi) + "/" + UiStyles.Gain(_pidShadow.KiPhi) + "/" +
                UiStyles.Gain(_pidShadow.KdPhi) +
                "；泵 " + PumpParameters.From(_swingShadow).PumpAmpDeg.ToString("F1") + "° / " +
                PumpParameters.From(_swingShadow).PumpHz.ToString("F2") + " Hz；" +
                "Ka/Kad/Kai 是历史值，按当前值原样保留。");
            return true;
        }

        /// <summary>
        /// 真正生效的那一组参数是否一致：限幅 + 内环 φ + 微分低通 + 输出 / 积分限幅 + 死区。
        /// Ka / Kad / Kai 等历史字段不参与比较 —— 控制层可能按内置默认补齐它们，但那不影响平衡。
        /// </summary>
        private static bool SameEffectiveGains(PidGains a, PidGains b)
        {
            return Near(a.AccMax, b.AccMax) && Near(a.ArmRateMax, b.ArmRateMax) &&
                   Near(a.KpPhi, b.KpPhi) && Near(a.KiPhi, b.KiPhi) && Near(a.KdPhi, b.KdPhi) &&
                   Near(a.TdLpMs, b.TdLpMs) &&
                   Near(a.OutMin, b.OutMin) && Near(a.OutMax, b.OutMax) &&
                   Near(a.OutRateLimit, b.OutRateLimit) && Near(a.MaxIntegral, b.MaxIntegral) &&
                   Near(a.DeadBand, b.DeadBand);
        }

        /// <summary>自动调参试跑通过后由对话框调用：采用并保存。失败返回 false + 中文原因，绝不假装成功。</summary>
        public bool SaveTuningCandidate(out string error)
        {
            error = null;
            string path = ParamStore.DefaultPath;
            if (SaveParametersNow("自动调参采用"))
            {
                SetStatus("自动调参结果已采用并保存：" + path, UiStyles.OkGreen);
                Log("[自动调参] 候选参数已采用并保存：" + path);
                return true;
            }
            error = "参数文件保存失败（目标 " + path + "），原因见运行日志；候选仍在运行期生效。";
            SetStatus(error, UiStyles.AlarmColor);
            return false;
        }

        /// <summary>自动调参取消 / 试跑失败后由对话框调用：回到打开对话框前的参数（只对本次运行生效）。</summary>
        public void RestoreTuningBaseline(PidGains pid, SwingUpParams swing)
        {
            _pidShadow = pid;
            _swingShadow = swing;
            _engine.SetBalancePid(pid);
            _engine.SetSwingUpParams(swing);
            RefreshParamBoxesFromShadows();
            MarkParamsDirty("自动调参回滚");
            Log("[自动调参] 已回滚到调参前的值（已下发控制层，未保存）。");
        }

        private static bool Near(double a, double b)
        {
            double scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
            return Math.Abs(a - b) <= 1e-6 * scale;
        }

        private void RefreshParamBoxesFromShadows()
        {
            _suppressParamEvents = true;
            try
            {
                FillPidBoxes();
                FillPumpBoxes();
            }
            finally
            {
                _suppressParamEvents = false;
            }
            UpdatePumpStatusLabel();
            UpdatePidLawHint();
            RefreshUFullScale();
        }

        // ---- 未保存状态：任何参数改动都是「立即生效、待落盘」，UI 必须一直看得见 ----

        private void MarkParamsDirty(string change)
        {
            bool wasDirty = _paramsDirty;
            _paramsDirty = true;
            if (_saveParamsButton != null)
            {
                if (!string.Equals(_saveParamsButton.Text, SAVE_BUTTON_DIRTY_TEXT, StringComparison.Ordinal))
                    _saveParamsButton.Text = SAVE_BUTTON_DIRTY_TEXT;
                if (_saveParamsButton.ForeColor != UiStyles.WarnOrange)
                    _saveParamsButton.ForeColor = UiStyles.WarnOrange;
            }
            if (_paramSavedLabel != null)
                UiStyles.SetLabelColored(_paramSavedLabel, "未保存（已生效）", UiStyles.WarnOrange);
            if (!wasDirty)
            {
                SetStatus("参数已改动并对本次运行生效，尚未保存：" + change +
                    "。点「保存参数」写入参数文件。", UiStyles.WarnOrange);
            }
        }

        private void ClearParamsDirty()
        {
            _paramsDirty = false;
            if (_saveParamsButton != null)
            {
                if (!string.Equals(_saveParamsButton.Text, SAVE_BUTTON_TEXT, StringComparison.Ordinal))
                    _saveParamsButton.Text = SAVE_BUTTON_TEXT;
                if (_saveParamsButton.ForeColor != UiStyles.GroupText)
                    _saveParamsButton.ForeColor = UiStyles.GroupText;
            }
        }

        /// <summary>
        /// 「应用（不保存）」：把当前影子参数再下发一次控制层，只影响本次运行。
        /// 文本框的 250 ms 去抖没跑完时先强制提交，保证按钮语义是「现在就用这套值」。
        /// </summary>
        private void ApplyParamsClicked(object sender, EventArgs e)
        {
            try
            {
                _pidApplyRejected = false;
                FlushPendingParamEdits();
                // 真正生效的项被合法性判据拒绝时保留拒绝原因（ApplyPidParams 已写状态行 / 日志），
                // 不再下发整组、也不写「参数已应用」——无效参数不能看起来已经生效。
                if (_pidApplyRejected)
                {
                    Log(LogLevel.Warn, "[参数] 应用（不保存）被拒：PID 参数未下发，状态行保留拒绝原因；其余参数本次未下发表。");
                    return;
                }
                ApplyAllParametersNow();
                MarkParamsDirty("应用（不保存）");
                string path = ParamStore.DefaultPath;
                SetStatus("参数已应用到本次运行（未写文件）。要持久化请点「保存参数」→ " + path,
                    UiStyles.WarnOrange);
                Log("[参数] 已应用（未保存）：PID / 泵 / 软限位已下发控制层；参数文件仍是上次内容：" + path);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "应用参数失败：" + DescribeException(ex));
                SetStatus("应用参数失败：" + ex.Message, UiStyles.AlarmColor);
            }
        }

        /// <summary>把三组影子参数统一下发控制层（不写文件）。启动 / 载入之外的「应用」都走这里。</summary>
        private void ApplyAllParametersNow()
        {
            _engine.SetBalancePid(_pidShadow);
            _engine.SetSwingUpParams(_swingShadow);
            _engine.SetSafetyLimits(_limitsShadow);
            _pidShadow = _engine.BalancePid;
            _swingShadow = _engine.SwingUpParams;
            RefreshParamBoxesFromShadows();
            RefreshSafetyText();
        }

        // ================================================================ 持久化

        private void AutoLoadParams()
        {
            string path = ParamStore.DefaultPath;
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            PersistedExtras extras;
            string deniPath;
            string note;
            string error;
            bool ok = ParamStore.TryLoad(path, _engine.DeniPath,
                out pid, out swing, out limits, out extras, out deniPath, out note, out error);
            if (!ok)
            {
                _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
                // 结局记进共享状态（CLI status 行同源），并让界面「参数」读数也说出来：
                // 只写日志的话，用户看到的还是出厂值，不知道自己的参数整份被拒。
                ParamStore.ReportDefaultFileLoad(path, false, error, null);
                if (File.Exists(path))
                {
                    _paramLoadRejected = true;
                    Log(LogLevel.Error, "[参数] 启动自动载入参数失败（已回退控制层默认值）：" + error +
                        "。" + PARAM_LOAD_REJECTED_NOTICE + "。");
                    if (_paramSavedLabel != null)
                        UiStyles.SetLabelColored(_paramSavedLabel, "载入失败（默认值）", UiStyles.WarnOrange);
                }
                else
                {
                    Log("[参数] 未找到参数文件（首次运行），使用控制层默认值：" + path);
                }
                return;
            }
            ParamStore.ReportDefaultFileLoad(path, true, null, note);

            // 旧参数文件缺加速度律字段时按唯一规则补齐（PidGains.ResolveAccelerationLaw），
            // 界面与后续保存必须用补齐后的值，不能把 0 显示/写回。
            bool legacyPidGains = !pid.HasAccelerationLawGains();
            if (pid.ResolveAccelerationLaw() && legacyPidGains)
                Log(LogLevel.Warn, "旧参数文件的平衡增益字段不完整，已按内置默认补齐 " +
                    DescribeAccelerationLawDefaults() + "；Ka/Kad/Kai 是历史值（不参与平衡），" +
                    "加速度 / 参考角速度限幅与内环请按台架重新确认");
            string swingMigrationNote;
            bool swingMigrated = PendulumEngine.TryMigrateLegacySwingDefaults(ref swing, out swingMigrationNote);
            if (swingMigrated)
                Log(LogLevel.Warn, "[参数] " + swingMigrationNote);
            _engine.SetBalancePid(pid);
            _engine.SetSwingUpParams(swing);
            _engine.SetSafetyLimits(limits);
            _pidShadow = pid;
            _swingShadow = swing;
            _limitsShadow = limits;
            _persistedExtras = extras ?? PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);

            ApplyCalibrationFromFile(_persistedExtras.Calibration, false);
            if (!_engine.PendulumZeroCountSet)
            {
                Log(LogLevel.Warn, "[标定] 参数文件没有摆杆 0° 零点（has_pendulum_zero=false）：θ 基准不可信，"
                    + "起摆会被拒绝。先停止，把摆杆放到自然下垂位并采集「摆杆零点 (下垂 0°)」。");
            }
            if (_persistedExtras.Captures != null)
                _engine.ImportCaptureRecords(_persistedExtras.Captures);

            _suppressParamEvents = true;
            FillPidBoxes();
            FillPumpBoxes();
            _suppressParamEvents = false;
            UpdatePumpStatusLabel();
            FillCalibrationFields();
            RefreshLimitCountFields();
            RefreshUFullScale();
            RefreshSafetyText();
            LoadInitialCpr();

            if (_persistedExtras.Identify != null && _persistedExtras.Identify.HasResult)
            {
                Log("已载入上次参数自测：f_n=" + _persistedExtras.Identify.NaturalFreqHz.ToString("F3") +
                    " Hz，置信度 " + _persistedExtras.Identify.Confidence.ToString("F2"));
            }
            Log("[参数] 参数已自动载入：" + path +
                (_persistedExtras.SavedAtUtc.HasValue
                    ? "（保存于 " + _persistedExtras.SavedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "）"
                    : ""));
            UpdateParamSavedLabel("参数已载入", _persistedExtras.SavedAtUtc.HasValue
                ? _persistedExtras.SavedAtUtc.Value.ToLocalTime()
                : DateTime.Now);
            ClearParamsDirty();
            if (swingMigrated)
                MarkParamsDirty("旧版起摆参数已迁移");
            if (!string.IsNullOrEmpty(note))
                Log(LogLevel.Warn, note);
        }

        private void ApplyCalibrationFromFile(CalibrationParameters calibration, bool force)
        {
            if (calibration == null)
                return;
            // 位置基准换了来源（自动载入 / 手动载入）：连接后首帧核对结论作废，下一次自动动作前重算。
            _poseReferenceChecked = false;
            // 持久标定是否已在**本次进程**确认：增量编码器不能自证跨掉电位置，别的进程写下的标记
            // 一律视为未确认，使能 / 起摆 / 平衡 / 回零必须先规整化或重新采集（不拿旧值直接使能）。
            EvaluatePoseSessionReview(calibration);
            // 跨重启的「标定已清空」必须先恢复引擎同一个标志，再谈零点数值：
            // 否则 has_*=false 会被首次使能当成「从未标定」而自动重建零点。
            _engine.RestoreCalibrationClearedState(calibration.CalibrationCleared);
            if (calibration.CalibrationCleared)
            {
                Log(LogLevel.Warn, "[标定] 参数文件标记 calibration_cleared=true：已恢复清空状态，"
                    + "本次启动不会自动建立零点；重新采集零点 / 左右限位并确认后才允许使能。");
            }
            if (calibration.ArmCountsPerRev > 0)
                _persistedArmCountsPerRev = calibration.ArmCountsPerRev;
            // 关节 0 点（原始计数）：只有文件里明确 set 过才恢复；旧文件没这个键就保持「首次使能取当前位置」。
            if (calibration.HasArmZeroCount)
            {
                string rejectReason;
                bool written = _engine.TrySetArmZeroCount(calibration.ArmZeroCount, out rejectReason);
                if (!written)
                {
                    Log(LogLevel.Warn, "参数文件里的关节 0 点 raw=" + calibration.ArmZeroCount +
                        " 未写入（" + rejectReason + "），控制层仍是 raw=" +
                        _engine.ArmZeroCount + "。");
                }
                else
                {
                    Log("标定已按参数文件恢复：关节 0 点 raw=" + calibration.ArmZeroCount + "（原始 0x6064 计数）。");
                }
            }
            if (!(calibration.PendulumCpr > 0))
                return;

            // 摆杆零点（自然下垂 / 最底部的原始计数）：只有文件里明确 set 过才恢复。
            // 用户显式标定过的零点跨越重启沿用，不再被使能自动归零覆盖。
            if (calibration.HasPendulumZero)
            {
                string rejectReason;
                if (!_engine.TrySetPendulumZeroCount(calibration.PendulumZeroCount, out rejectReason))
                {
                    Log(LogLevel.Warn, "参数文件里的摆杆零点 raw=" + calibration.PendulumZeroCount +
                        " 未写入（" + rejectReason + "），控制层仍是 raw=" +
                        _engine.PendulumZeroRawCount + "。");
                }
                else
                {
                    Log("标定已按参数文件恢复：摆杆零点 raw=" + calibration.PendulumZeroCount + "（原始计数）。");
                }
            }

            // 摆杆 360° 标定点：与 0° 一起决定一圈的量。两点齐全时**以两点标定为准**，
            // 覆盖文件里的 pendulum_cpr（用户模型：一圈是标定出来的，不是输入进去的）。
            int derivedCpr;
            bool derivedFromTwoPoints = calibration.TryCalibratePendulumCpr(out derivedCpr);
            if (derivedFromTwoPoints)
            {
                Log("标定已按参数文件恢复：摆杆一圈的量 = |360° − 0°| = " +
                    derivedCpr.ToString("N0") + " counts。");
            }

            double fileCpr = calibration.PendulumCpr;
            // 旧参数文件里的 600 是 GC1502 脉冲计数口径（不乘 4）；GC1602 是 AB 四倍频，同一机械
            // 每转 = 600 线 × 4 = 2400。直接沿用 600 会让 θ 偏 4 倍，这里按台架新口径迁移并留痕。
            // 两点标定派生出来的值不参与这条迁移（那是现场真标定的结果）。
            double legacyGc1502Cpr = 600.0;
            if (!derivedFromTwoPoints && Math.Abs(fileCpr - legacyGc1502Cpr) < 1e-9)
            {
                double migrated = CalibrationParameters.CreateDefault().PendulumCpr;
                Log(LogLevel.Warn, "参数文件里的杆每转计数 600 是 GC1502 脉冲口径（旧值），" +
                    "已按 GC1602 AB 四倍频口径迁移为 " + migrated.ToString("0.###") + "（600 线 × 4）。");
                fileCpr = migrated;
            }

            double current = _engine.PendulumCountsPerRev;
            bool apply = force || !_engine.PendulumCountsPerRevExplicit;
            if (!apply)
            {
                if (Math.Abs(current - fileCpr) > 1e-6)
                {
                    Log(LogLevel.Warn, "参数文件里的杆每转计数 " + fileCpr.ToString("0.###") +
                        " 与本次显式 --pendulum-cpr " + current.ToString("0.###") + " 不同：以显式启动值为准。");
                }
                return;
            }
            if (Math.Abs(current - fileCpr) < 1e-6)
                return;

            _engine.PendulumCountsPerRev = fileCpr;
            double applied = _engine.PendulumCountsPerRev;
            if (Math.Abs(applied - fileCpr) > 1e-6)
            {
                LogReject("Calibrate(参数文件)", REJECT_OTHER,
                    "杆每转计数未写入（当前 " + FormatState(_engine.State, _lastEStopLatch) + " 不允许改标定），仍是 " +
                    applied.ToString("0.###") + "，文件值 " + fileCpr.ToString("0.###"), null);
                return;
            }
            Log("标定已按参数文件恢复：杆每转计数 = " + applied.ToString("0.###") + "。");
        }

        /// <summary>
        /// 判定「文件里的位置标定是否已在本次进程确认」并同步给控制层。
        /// <para>
        /// 判据 = 该组的会话标记是否由本次进程写下（<see cref="CalibrationParameters.ArmPoseSessionVerified"/> /
        /// <see cref="CalibrationParameters.PendulumPoseSessionVerified"/>）。EC1 的 0x6064 与 GC1602 的 AB 计数
        /// 都没有绝对多圈，程序**无法证明**文件里的 counts 仍对应当前物理位置；raw 恰好相等也可能是掉电后的巧合，
        /// 所以不拿旧值直接使能，而是要求现场二选一：确认机械仍在保存姿态 → 规整化；否则重新采集 / 清空重采。
        /// </para>
        /// <para>
        /// 已经 <c>calibration_cleared</c> 或本来就没有位置点的，不置这个标志（前者有自己的阻断，后者是首次运行）。
        /// </para>
        /// </summary>
        private void EvaluatePoseSessionReview(CalibrationParameters calibration)
        {
            _poseSessionReviewRequired = false;
            if (calibration == null || calibration.CalibrationCleared)
            {
                _engine.ConfirmPoseSessionReview();
                return;
            }
            bool needArm = calibration.HasArmZeroCount && !calibration.ArmPoseSessionVerified;
            bool needPendulum = (calibration.HasPendulumZero || calibration.HasPendulum360) &&
                                !calibration.PendulumPoseSessionVerified;
            if (!needArm && !needPendulum)
            {
                _engine.ConfirmPoseSessionReview();
                return;
            }

            _poseSessionReviewRequired = true;
            string reason = BuildPoseSessionReason();
            _engine.RequirePoseSessionReview("[标定] " + reason);
            Log(LogLevel.Warn, "[标定] " + reason);
            SetStatus("位置标定待确认：" + reason, UiStyles.WarnOrange);
        }

        private PersistedExtras BuildPersistedExtras()
        {
            PersistedExtras extras = _persistedExtras ??
                PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);

            extras.Pump = PumpParameters.From(_swingShadow);
            if (extras.Identify == null)
                extras.Identify = new IdentifyParameters();
            extras.SoftLimits = SoftLimitParameters.From(_limitsShadow);

            if (extras.Calibration == null)
                extras.Calibration = CalibrationParameters.CreateDefault();
            double pendulumCpr = _engine.PendulumCountsPerRev;
            if (pendulumCpr > 0)
                extras.Calibration.PendulumCpr = pendulumCpr;
            else if (!(extras.Calibration.PendulumCpr > 0))
                extras.Calibration.PendulumCpr = CalibrationParameters.CreateDefault().PendulumCpr;
            if (_persistedArmCountsPerRev > 0)
                extras.Calibration.ArmCountsPerRev = _persistedArmCountsPerRev;
            else if (extras.Calibration.ArmCountsPerRev <= 0)
                extras.Calibration.ArmCountsPerRev = (int)DEFAULT_ARM_COUNTS_PER_REV;
            // 关节减速比：控制层只在构造期生效，这里记录当前生效值，下次入口（Program）读回并应用。
            double gear = _engine.ArmGearRatio;
            if (gear > 0 && !double.IsNaN(gear) && !double.IsInfinity(gear))
                extras.Calibration.ArmGearRatio = gear;
            // 关节零点是原始 0x6064 计数，原样落盘；没设过（ArmZeroCountSet=false）就不写，
            // 保持「使能首次归零取当前位置」的老语义，不凭空造一个零点。
            if (_engine.ArmZeroCountSet)
            {
                extras.Calibration.HasArmZeroCount = true;
                extras.Calibration.ArmZeroCount = _engine.ArmZeroCount;
            }
            else
            {
                extras.Calibration.HasArmZeroCount = false;
                extras.Calibration.ClearArmPoseSessionMark();
            }
            // 摆杆 360° 标定点只写**界面真正标定过**的值；清空标定后 has=false，不拿 0 冒充。
            if (_persistedExtras.Calibration != null && _persistedExtras.Calibration.HasPendulum360)
            {
                extras.Calibration.HasPendulum360 = true;
                extras.Calibration.Pendulum360Count = _persistedExtras.Calibration.Pendulum360Count;
            }
            else
            {
                extras.Calibration.HasPendulum360 = false;
                extras.Calibration.Pendulum360Count = 0;
            }
            // 摆杆 0° 点只写**控制层真正接受过**的值（用户显式设过才写），避免把被拒的输入冒充标定。
            if (_engine.PendulumZeroCountSet)
            {
                extras.Calibration.HasPendulumZero = true;
                extras.Calibration.PendulumZeroCount = _engine.PendulumZeroRawCount;
            }
            else
            {
                extras.Calibration.HasPendulumZero = false;
                extras.Calibration.ClearPendulumPoseSessionMark();
            }

            extras.Captures = new List<CaptureRecord>(_engine.CaptureRecordsSnapshot());
            extras.ClampCaptures();
            return extras;
        }

        private void FlushPendingParamEdits()
        {
            if (_paramApplyTimer != null)
                _paramApplyTimer.Stop();
            if (_pidDirty)
            {
                _pidDirty = false;
                ApplyPidParams();
            }
            if (_pumpDirty)
            {
                _pumpDirty = false;
                ApplyPumpParams();
            }
            if (_armZeroDirty)
            {
                _armZeroDirty = false;
                ApplyArmZero((int)_armZeroCountBox.Value, "保存前提交");
            }
            if (_pendulumZeroDirty)
            {
                _pendulumZeroDirty = false;
                ApplyPendulumZeroCount((int)_pendulumZeroCountBox.Value, "保存前提交");
            }
            if (_pendulum360Dirty)
            {
                _pendulum360Dirty = false;
                ApplyPendulum360Count((int)_pendulum360CountBox.Value, "保存前提交");
            }
            if (_limitCountsDirty)
            {
                _limitCountsDirty = false;
                ApplyLimitsFromCountBoxes();
            }
        }

        private bool SaveParametersNow(string reason)
        {
            return SaveParametersTo(ParamStore.DefaultPath, reason, false);
        }

        /// <summary>
        /// 原子保存到指定路径（默认 furuta_params.json）。抽成带 path 的入口，自检程序可在临时目录验证落盘，
        /// 不碰现场参数文件。失败路径都写可读日志，不空吞、不假装成功。
        /// <para>
        /// userInitiated = 用户明确点「保存参数」：只有这种保存允许在“载入曾被拒”后覆盖默认文件；
        /// 关窗兜底 / 规整化后的自动落盘 / 自动调参采用都属于隐式保存，载入被拒时一律跳过，
        /// 避免拿出厂值或半套参数覆盖用户那个只是格式有问题的文件。
        /// </para>
        /// </summary>
        internal bool SaveParametersTo(string path, string reason, bool userInitiated)
        {
            if (_savingParams)
                return false;
            if (!userInitiated && _paramLoadRejected &&
                string.Equals(path, ParamStore.DefaultPath, StringComparison.OrdinalIgnoreCase))
            {
                Log(LogLevel.Warn, "[参数] 自动保存已跳过（" + reason + "）：" + PARAM_LOAD_REJECTED_NOTICE + "。");
                SetStatus("本次未自动保存（" + reason + "）：" + PARAM_LOAD_REJECTED_NOTICE + "。",
                    UiStyles.WarnOrange);
                return false;
            }
            _savingParams = true;
            try
            {
                FlushPendingParamEdits();
                PersistedExtras extras = BuildPersistedExtras();
                string error;
                if (!ParamStore.Save(path, _pidShadow, _swingShadow, _limitsShadow,
                        _engine.DeniPath, extras, out error))
                {
                    Log(LogLevel.Error, "[参数] 参数保存失败（" + reason + "）：" + error + "（目标 " + path + "）");
                    SetStatus("参数保存失败：" + error + "（目标 " + path + "）", UiStyles.AlarmColor);
                    return false;
                }
                _persistedExtras = extras;
                // 用户显式保存成功 = 参数文件已经是一份完整可读的权威值，关窗兜底不再需要避让。
                _paramLoadRejected = false;
                ClearParamsDirty();
                UpdateParamSavedLabel("参数已保存", DateTime.Now);
                Log("[参数] 参数已保存（" + reason + "）：" + path +
                    " · 捕获记录 " + (extras.Captures == null ? 0 : extras.Captures.Count) + " 条");
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "参数保存异常（" + reason + "）：" + DescribeException(ex));
                SetStatus("参数保存异常：" + ex.Message, UiStyles.AlarmColor);
                return false;
            }
            finally
            {
                _savingParams = false;
            }
        }

        private void UpdateParamSavedLabel(string prefix, DateTime time)
        {
            if (_paramSavedLabel == null)
                return;
            UiStyles.SetLabelColored(_paramSavedLabel, prefix + " " + time.ToString("HH:mm:ss"), UiStyles.OkGreen);
        }

        private void AttachParamCommitHandlers()
        {
            NumericUpDown[] boxes =
            {
                _kpBox, _kiBox, _kdBox,
                _keBox, _pumpAmpBox, _pumpFreqBox, _pumpPhaseBox,
                _limitMinCountsBox, _limitMaxCountsBox, _pendulumZeroCountBox, _pendulum360CountBox,
                _armZeroCountBox
            };
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] == null) continue;
                boxes[i].Leave += ParamBoxCommitted;
                boxes[i].KeyDown += ParamBoxKeyDown;
            }
        }

        private void ParamBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            ParamBoxCommitted(sender, EventArgs.Empty);
        }

        private void ParamBoxCommitted(object sender, EventArgs e)
        {
            try
            {
                NumericUpDown box = sender as NumericUpDown;
                FlushPendingParamEdits();
                if (box == _kpBox || box == _kiBox || box == _kdBox)
                {
                    // Ka / Kad / Kai 是历史值：这里只报这一批真正生效的项，不再把历史增益说成"外环已应用"。
                    Log("PID 参数已应用：加速度限幅=" + _pidShadow.AccMax.ToString("F0", CultureInfo.InvariantCulture) +
                        " rad/s² 参考角速度限幅=" + _pidShadow.ArmRateMax.ToString("F1", CultureInfo.InvariantCulture) +
                        " rad/s 内环 Kpφ/Kiφ/Kdφ=" + UiStyles.Gain(_pidShadow.KpPhi) + "/" +
                        UiStyles.Gain(_pidShadow.KiPhi) + "/" + UiStyles.Gain(_pidShadow.KdPhi) +
                        "（下一拍顶换）；Ka/Kad/Kai 只作历史值随参数文件保存，不参与平衡。");
                }
                if (box == _keBox || box == _pumpAmpBox || box == _pumpFreqBox || box == _pumpPhaseBox)
                {
                    PumpParameters pump = PumpParameters.From(_swingShadow);
                    Log("能量泵参数已应用：k_e=" + UiStyles.Gain(pump.Ke) +
                        " 幅度=" + pump.PumpAmpDeg.ToString("F1") + "°" +
                        " 频率=" + pump.PumpHz.ToString("F2") + " Hz" +
                        " 相位=" + pump.PhaseOffsetDeg.ToString("F1") + "°（下一拍顶换）。");
                }
                if (box == _armZeroCountBox)
                {
                    Log("标定：0 点原始计数已提交 raw=" + _engine.ArmZeroCount + "。");
                }
                if (box == _pendulumZeroCountBox)
                {
                    Log("标定：摆杆零点原始计数已提交 raw=" + _engine.PendulumZeroRawCount + "。");
                }
                if (box == _limitMinCountsBox || box == _limitMaxCountsBox)
                {
                    Log("标定：限位原始计数已提交 " + _limitMinCountsBox.Value.ToString("N0") +
                        " ~ " + _limitMaxCountsBox.Value.ToString("N0") + " counts。");
                }
                // 不做隐式落盘：改动已经热生效，是否写进参数文件由用户点「保存参数」决定；
                // 有未保存改动时保存按钮和当前状态卡会一直显示「未保存」。
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "参数提交失败：" + DescribeException(ex));
            }
        }

        /// <summary>把摆杆一圈的量写进控制层，并读回验证是否真的生效。</summary>
        private bool ApplyPendulumCpr(double cpr, string source)
        {
            EngineState state = _engine.State;
            if (state != EngineState.Disconnected && state != EngineState.Connected && state != EngineState.Stopped)
            {
                LogReject("Calibrate(摆杆一圈)", REJECT_OTHER,
                    "一圈的量未写入：当前 " + FormatState(state, _lastEStopLatch) +
                    " 不允许改标定（运行中 θ 会跳变），请先「停止」再标定", null);
                SetStatus("一圈的量未写入：运行态不可改，先「停止」再标定。", UiStyles.WarnOrange);
                return false;
            }
            _engine.PendulumCountsPerRev = cpr;
            double applied = _engine.PendulumCountsPerRev;
            if (Math.Abs(applied - cpr) > 1e-6)
            {
                LogReject("Calibrate(摆杆一圈)", REJECT_OTHER,
                    "一圈的量未写入：当前 " + FormatState(state, _lastEStopLatch) + " 不允许改标定，仍是 " +
                    applied.ToString("0.###") + "，停机后再设", null);
                SetStatus("一圈的量未写入：运行态不可改，先「停止」再标定。", UiStyles.AlarmColor);
                return false;
            }
            Log("摆杆一圈的量 = " + cpr.ToString("0.###") + " counts（" + source + "）。");
            SetStatus("摆杆一圈的量 = " + cpr.ToString("0.###") + " counts。");
            MarkParamsDirty("摆杆一圈的量 " + cpr.ToString("0.###"));
            return true;
        }

        /// <summary>手输 / 采集摆杆 360° 标定点（纯标定数据，不改控制层状态），并重算一圈的量。</summary>
        private void ApplyPendulum360Count(int raw, string source)
        {
            _pendulum360Dirty = false;
            if (_persistedExtras == null)
                _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
            if (_persistedExtras.Calibration == null)
                _persistedExtras.Calibration = CalibrationParameters.CreateDefault();
            _persistedExtras.Calibration.Pendulum360Count = raw;
            _persistedExtras.Calibration.HasPendulum360 = true;
            _poseRecheckPendulum360 = true;
            MaybeResolvePoseReference();
            Log("[标定] 摆杆 360° 标定点 = raw=" + raw + "（" + source + "）。");
            MarkParamsDirty("摆杆 360° raw=" + raw);
            UpdateDerivedPendulumCpr();
        }

        /// <summary>
        /// 一圈的量 = |360° − 0°|（用户模型：标定出来的，不是输入进去的）。
        /// 两点不相等才更新控制层的 PendulumCountsPerRev；同值 / 缺一点就如实显示「未标定」。
        /// </summary>
        private void UpdateDerivedPendulumCpr()
        {
            if (_pendulumZeroCountBox == null || _pendulum360CountBox == null)
                return;

            bool hasZero = _engine.PendulumZeroCalibrated;
            bool has360 = _persistedExtras != null && _persistedExtras.Calibration != null &&
                          _persistedExtras.Calibration.HasPendulum360;
            if (!hasZero || !has360)
            {
                double kept = _engine.PendulumCountsPerRev;
                SetPendulumCprReadout(kept > 0
                        ? kept.ToString("0.###", CultureInfo.InvariantCulture) + " counts/圈（保留；0°/360° 未标定）"
                        : "未标定（标定 0° 与 360° 两点）",
                    UiStyles.WarnOrange);
                return;
            }

            int zero = (int)_pendulumZeroCountBox.Value;
            int p360 = (int)_pendulum360CountBox.Value;
            long diff = (long)p360 - zero;
            if (diff < 0) diff = -diff;
            if (diff <= 0 || diff > int.MaxValue)
            {
                SetPendulumCprReadout("未标定（0° 与 360° 不能同值）", UiStyles.WarnOrange);
                return;
            }
            int cpr = (int)diff;
            bool applied = ApplyPendulumCpr(cpr, "0°/360° 两点标定");
            SetPendulumCprReadout(applied
                    ? cpr.ToString("N0", CultureInfo.InvariantCulture) + " counts/圈"
                    : cpr.ToString("N0", CultureInfo.InvariantCulture) +
                      " counts/圈（已算出，但控制层未写入：先「停止」再应用）",
                applied ? UiStyles.ValueText : UiStyles.WarnOrange);
        }

        private void SetPendulumCprReadout(string text, Color color)
        {
            if (_pendulumCprLabel == null) return;
            UiStyles.SetLabelColored(_pendulumCprLabel, text ?? string.Empty, color);
        }

        /// <summary>手输摆杆零点原始计数 → 控制层（只改软件零点基准）。状态门由控制层把关（闭环拒写）。</summary>
        private bool ApplyPendulumZeroCount(int raw, string source)
        {
            // 持久化视图先就位：提交函数要拿它判断「零点是否真的变了」并同步 360°。
            if (_persistedExtras == null)
                _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
            if (_persistedExtras.Calibration == null)
                _persistedExtras.Calibration = CalibrationParameters.CreateDefault();
            // 「提交零点 + 同步 360°」唯一实现（与规整化路径、CLI zero pendulum / normalize / recenter
            // 同一套规则）：控制层接受 → 零点未变则两点原样保留 → 变了才按同一圈量平移 / 按 span=0 作废。
            PendulumZeroCommit.Result commit = PendulumZeroCommit.Commit(
                _engine, _persistedExtras.Calibration, raw);
            if (!commit.Accepted)
            {
                LogReject("Calibrate(摆杆零点)", REJECT_OTHER,
                    "摆杆零点未写入：" + commit.RejectReason + "（请求 raw=" + raw + "）", null);
                SetStatus(commit.AppliedMismatch
                        ? "摆杆零点未写入：先「停止」再设。"
                        : "摆杆零点未写入：" + commit.RejectReason,
                    UiStyles.WarnOrange);
                return false;
            }
            _poseRecheckPendulumZero = true;
            _suppressCalibrationEvents = true;
            try
            {
                if (commit.HasPendulum360)
                    ShowNumberCalibrated(_pendulum360CountBox, commit.Pendulum360Count);
                else
                    ShowNumberUncalibrated(_pendulum360CountBox);
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
            _pendulum360Dirty = false;
            MaybeResolvePoseReference();
            if (!string.IsNullOrEmpty(commit.SyncError))
            {
                LogReject("Calibrate(摆杆零点)", REJECT_OTHER,
                    "0° 已写入控制层，但 360° 点重标失败：" + commit.SyncError, null);
            }
            string rebaseText = commit.ZeroUnchanged
                ? "；零点未变，360° 点保持原样"
                : (string.IsNullOrEmpty(commit.Note)
                    ? (commit.HasPendulum360 ? "" : "；360° 点未标定")
                    : "；" + commit.Note);
            if (!string.IsNullOrEmpty(commit.Note))
                Log(commit.Rebased ? LogLevel.Info : LogLevel.Warn, "[标定] " + commit.Note);
            Log("摆杆零点已设为 raw=" + commit.AppliedZero + "（" + source + "）" + rebaseText +
                (commit.ZeroUnchanged ? "。" : "（尚未保存）。"));
            // 同一点重复清零没有产生任何变化：不标「未保存」，也不写「360° 点同步平移」的假回执。
            if (!commit.ZeroUnchanged)
                MarkParamsDirty("摆杆零点 raw=" + commit.AppliedZero +
                    (commit.Rebased ? "（360° 点同步平移）" : ""));
            // 先派生一圈量（它会把状态行写成「摆杆一圈的量 = …」），再写零点回执：
            // 回执必须是对本次操作的最后一句 —— 否则 360° 点平移 / 作废的说明会被一串通用读数盖掉。
            UpdateDerivedPendulumCpr();
            SetStatus(commit.ZeroUnchanged
                    ? "摆杆零点未变（raw=" + commit.AppliedZero + "）：0° / 360° 两点保持原样。"
                    : "摆杆零点已设为 raw=" + commit.AppliedZero + rebaseText + "（尚未保存）。",
                commit.ZeroUnchanged ? UiStyles.OkGreen : UiStyles.WarnOrange);
            RefreshCalibrationCard();
            return true;
        }

        // ================================================================ 起摆内自动参数自测

        private void IdentifyAndApply(string source)
        {
            try
            {
                IdentificationResult result = _engine.IdentifyNow();
                Log("参数自测（" + source + "）：" + result.Line);

                if (_persistedExtras == null)
                    _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
                _persistedExtras.Identify = IdentifyParameters.From(result);

                if (!result.Sufficient)
                {
                    SetStatus("参数自测：样本不足（" + result.InsufficientReason + "），未改参数。", UiStyles.WarnOrange);
                    MarkParamsDirty("参数自测记录（样本不足）");
                    return;
                }

                bool swingRunning = _engine.State == EngineState.Swinging;
                bool canApply = !swingRunning;
                bool applied = false;
                if (canApply && result.Confidence >= 0.5)
                {
                    PumpParameters pump = PumpParameters.From(_swingShadow);
                    if (IsFinitePositive(result.PumpHz))
                        pump.PumpHz = result.PumpHz;
                    if (!double.IsNaN(result.PumpAmpDeg) && !double.IsInfinity(result.PumpAmpDeg) &&
                        result.PumpAmpDeg >= 0)
                    {
                        pump.PumpAmpDeg = Math.Min(result.PumpAmpDeg, 180.0);
                    }
                    double factoryKe = SwingUpParams.CreateDefault().Ke;
                    double keReference = _lastAppliedIdentifyKe > 0 ? _lastAppliedIdentifyKe : factoryKe;
                    bool keUntouched = Math.Abs(_swingShadow.Ke - keReference) < 1e-9;
                    if (IsFinitePositive(result.Ke) && keUntouched)
                        pump.Ke = result.Ke;

                    string reason;
                    if (!pump.IsValid(out reason))
                    {
                        Log(LogLevel.Warn, "自测结果未应用：泵参数不合法 —— " + reason);
                    }
                    else
                    {
                        SwingUpParams candidate = _swingShadow;
                        pump.ApplyTo(ref candidate);
                        if (!candidate.IsValid(out reason))
                        {
                            Log(LogLevel.Warn, "自测结果未应用：换算后的控制层参数不合法 —— " + reason);
                        }
                        else
                        {
                            _swingShadow = candidate;
                            _engine.SetSwingUpParams(candidate);
                            _suppressParamEvents = true;
                            FillPumpBoxes();
                            _suppressParamEvents = false;
                            UpdatePumpStatusLabel();
                            _persistedExtras.Pump = pump;
                            applied = true;
                            _lastAppliedIdentifyKe = keUntouched ? pump.Ke : 0;
                            Log("自测结果已自动应用：泵频=" + pump.PumpHz.ToString("F3") + " Hz，泵幅=" +
                                pump.PumpAmpDeg.ToString("F2") + "°，k_e=" + UiStyles.Gain(pump.Ke) +
                                (keUntouched ? "" : "（k_e 保留用户值）") + "，置信度 " + result.Confidence.ToString("F2") + "。");
                        }
                    }
                }
                if (!applied)
                {
                    string why = !canApply
                        ? "起摆进行中（泵参数暂不修改）"
                        : "置信度 " + result.Confidence.ToString("F2") + " < 0.50";
                    Log("自测结果未自动应用：" + why + "；参数文件已记录推荐值。");
                    SetStatus("参数自测完成（未自动应用：" + why + "）。", UiStyles.WarnOrange);
                }
                else
                {
                    SetStatus("参数自测完成：泵参数已按自测结果更新（置信度 " +
                        result.Confidence.ToString("F2") + "）。");
                }

                MarkParamsDirty("参数自测结果");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "参数自测失败：" + DescribeException(ex));
                SetStatus("参数自测失败：" + ex.Message, UiStyles.AlarmColor);
            }
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
        }

        /// <summary>杆无有效读数（表盘反馈指示灯用）。</summary>
        private bool ShouldWarnPendulumFeedback()
        {
            if (_engine.State == EngineState.Disconnected)
                return false;
            return !_engine.IsPendulumFeedbackLive;
        }

        private void RestoreDefaultsClicked(object sender, EventArgs e)
        {
            RunOp("恢复默认", delegate
            {
                EngineState state = _engine.State;
                if (state != EngineState.Disconnected && state != EngineState.Connected && state != EngineState.Stopped)
                {
                    LogReject("RestoreDefaults(恢复默认)", REJECT_INTERLOCK,
                        "当前 " + FormatState(state, _lastEStopLatch) + "，只在未连接 / 已连接 / 已停可恢复默认", null);
                    SetStatus("恢复默认被拒：当前 " + FormatState(state, _lastEStopLatch) + "。", UiStyles.WarnOrange);
                    return;
                }
                PidGains pid = PidGains.CreateDefault();
                SwingUpParams swing = SwingUpParams.CreateDefault();
                _engine.SetBalancePid(pid);
                _engine.SetSwingUpParams(swing);
                _pidShadow = pid;
                _swingShadow = swing;
                if (_persistedExtras != null)
                {
                    _persistedExtras.Identify = new IdentifyParameters();
                    _persistedExtras.Pump = PumpParameters.CreateDefault();
                }
                _lastAppliedIdentifyKe = 0;
                _suppressParamEvents = true;
                FillPidBoxes();
                FillPumpBoxes();
                _suppressParamEvents = false;
                UpdatePumpStatusLabel();
                RefreshUFullScale();
                // 软限位与标定是实测出来的位置，恢复默认不碰它们。
                Log(LogLevel.Warn, "已恢复默认：平衡 PID / 能量泵；自测结果清空，标定 / 软限位 / 历史记录保留。");
                SetStatus("已恢复默认参数（标定与限位保持不动，尚未保存）。", UiStyles.WarnOrange);
                MarkParamsDirty("恢复默认 PID / 能量泵");
            });
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // 载入被拒时引擎里只是出厂值 / 旧值，兜底保存会把用户那个“格式有问题”的文件整份覆盖。
                // 只跳过自动保存；用户仍可先点「保存参数」显式落盘（成功即恢复正常兜底）。
                if (!ShouldAutoSaveOnClose())
                {
                    Log(LogLevel.Warn, "[参数] 关窗兜底保存已跳过：" + PARAM_LOAD_REJECTED_NOTICE + "。");
                    return;
                }
                FlushPendingParamEdits();
                SaveParametersNow("关窗兜底");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("关窗保存异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// 关窗时是否允许自动保存。载入参数被拒（自动或手动）后为 false，直到一次显式保存成功；
        /// 自检直接翻转 <c>_paramLoadRejected</c> 验证这道门，不真写文件。
        /// </summary>
        internal bool ShouldAutoSaveOnClose()
        {
            return !_paramLoadRejected;
        }

        private void SaveParamsClicked(object sender, EventArgs e)
        {
            string path = ParamStore.DefaultPath;
            // 这是用户明确要求落盘：载入曾被拒也允许，成功后自动恢复关窗兜底。
            if (!SaveParametersTo(path, "手动保存", true))
            {
                // SaveParametersTo 已把「原因 + 目标路径」写进状态行与日志，这里不再用泛化文案覆盖。
                return;
            }
            string note = ParamStore.EffectivePathNote;
            SetStatus("参数已保存：" + path + (string.IsNullOrEmpty(note) ? "" : "（" + note + "）"),
                UiStyles.OkGreen);
        }

        private async void LoadParamsClicked(object sender, EventArgs e)
        {
            EngineState state = _engine.State;
            if (state != EngineState.Disconnected && state != EngineState.Connected && state != EngineState.Stopped)
            {
                LogReject("LoadConfig(载入参数)", REJECT_INTERLOCK,
                    "当前 " + FormatState(state, _lastEStopLatch) + "，只在未连接 / 已连接 / 已停可载入", null);
                SetStatus("载入被拒：当前 " + FormatState(state, _lastEStopLatch) + "。", UiStyles.WarnOrange);
                return;
            }
            string path = ParamStore.DefaultPath;
            PidGains pid = default(PidGains);
            SwingUpParams swing = default(SwingUpParams);
            SafetyLimits limits = default(SafetyLimits);
            PersistedExtras extras = null;
            string deniPath = null;
            string note = null;
            string error = null;
            bool ok = await Task.Run(delegate
            {
                return ParamStore.TryLoad(path, _engine.DeniPath,
                    out pid, out swing, out limits, out extras, out deniPath, out note, out error);
            });
            if (!ok)
            {
                // 手动载入失败也写回共享结局，并让「参数」读数改口：否则开机自动载入成功过的
                // 「参数已载入」会一直挂着，看起来像这次也读成功。
                // 同时关掉关窗兜底保存：被拒文件里的内容还没读进引擎，写回去等于拿当前值覆盖它。
                _paramLoadRejected = true;
                ParamStore.ReportDefaultFileLoad(path, false, error, null);
                Log(LogLevel.Error, "载入参数失败：" + error + "。" + PARAM_LOAD_REJECTED_NOTICE + "。");
                SetStatus("载入参数失败：" + error + "。" + PARAM_LOAD_REJECTED_NOTICE + "。", UiStyles.AlarmColor);
                if (_paramSavedLabel != null)
                    UiStyles.SetLabelColored(_paramSavedLabel, "载入失败", UiStyles.WarnOrange);
                return;
            }
            // 旧参数文件缺加速度律字段时按唯一规则补齐（PidGains.ResolveAccelerationLaw），
            // 界面与后续保存必须用补齐后的值，不能把 0 显示/写回。
            bool legacyPidGains = !pid.HasAccelerationLawGains();
            if (pid.ResolveAccelerationLaw() && legacyPidGains)
                Log(LogLevel.Warn, "旧参数文件的平衡增益字段不完整，已按内置默认补齐 " +
                    DescribeAccelerationLawDefaults() + "；Ka/Kad/Kai 是历史值（不参与平衡），" +
                    "加速度 / 参考角速度限幅与内环请按台架重新确认");
            string swingMigrationNote;
            bool swingMigrated = PendulumEngine.TryMigrateLegacySwingDefaults(ref swing, out swingMigrationNote);
            if (swingMigrated)
                Log(LogLevel.Warn, "[参数] " + swingMigrationNote);
            _engine.SetBalancePid(pid);
            _engine.SetSwingUpParams(swing);
            _engine.SetSafetyLimits(limits);
            _pidShadow = pid;
            _swingShadow = swing;
            _limitsShadow = limits;
            _persistedExtras = extras ?? PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
            ApplyCalibrationFromFile(_persistedExtras.Calibration, true);
            if (!_engine.PendulumZeroCountSet)
            {
                Log(LogLevel.Warn, "[标定] 参数文件没有摆杆 0° 零点（has_pendulum_zero=false）：θ 基准不可信，"
                    + "起摆会被拒绝。先停止，把摆杆放到自然下垂位并采集「摆杆零点 (下垂 0°)」。");
            }
            if (_persistedExtras.Captures != null)
                _engine.ImportCaptureRecords(_persistedExtras.Captures);
            _suppressParamEvents = true;
            FillPidBoxes();
            FillPumpBoxes();
            _suppressParamEvents = false;
            UpdatePumpStatusLabel();
            FillCalibrationFields();
            RefreshLimitCountFields();
            RefreshSafetyText();
            RefreshUFullScale();
            LoadInitialCpr();
            Log("[参数] 参数已载入并应用：" + path);
            if (!string.IsNullOrEmpty(note))
                Log(LogLevel.Warn, note);
            SetStatus("参数已载入并应用。");
            _paramLoadRejected = false;
            ParamStore.ReportDefaultFileLoad(path, true, null, note);
            UpdateParamSavedLabel("参数已载入", DateTime.Now);
            ClearParamsDirty();
            if (swingMigrated)
                MarkParamsDirty("旧版起摆参数已迁移");
        }

        private void LoadInitialState()
        {
            string reason;
            bool pidOk = false;
            bool swingOk = false;
            bool limitsOk = false;
            try
            {
                _pidShadow = _engine.BalancePid;
                pidOk = _pidShadow.IsValid(out reason);
                _swingShadow = _engine.SwingUpParams;
                swingOk = _swingShadow.IsValid(out reason);
                _limitsShadow = _engine.SafetyLimits;
                limitsOk = _limitsShadow.IsValid(out reason);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "读控制层当前参数失败：" + DescribeException(ex));
            }
            if (!pidOk)
                _pidShadow = DefaultGains;
            if (!swingOk)
                _swingShadow = DefaultSwing;
            if (!limitsOk)
                _limitsShadow = SafetyLimits.CreateDefault();

            _suppressParamEvents = true;
            FillPidBoxes();
            FillPumpBoxes();
            _suppressParamEvents = false;
            UpdatePumpStatusLabel();
            FillCalibrationFields();
            RefreshLimitCountFields();
            LoadInitialCpr();
            if (!pidOk || !swingOk || !limitsOk)
                Log(LogLevel.Warn, "控制层参数未初始化，PID / 设备参数卡先按 CreateDefault() 起手值显示。");
            else
                Log("PID / 设备参数卡已按控制层当前参数回填。");
        }

        private void FillPidBoxes()
        {
            SetNumber(_kpBox, _pidShadow.Ka);
            SetNumber(_kiBox, _pidShadow.Kad);
            SetNumber(_kdBox, _pidShadow.Kai);
            UpdatePidLawHint();
        }

        /// <summary>
        /// 加速度律出厂默认的中文描述（旧参数迁移日志用）。数字一律取自
        /// <see cref="PidGains.CreateAccelerationLawDefaults"/>，界面不手抄默认值 ——
        /// 出厂 Ka 从 500 调到 900 时，写死的日志会让现场按错值整定。
        /// </summary>
        private static string DescribeAccelerationLawDefaults()
        {
            PidGains law = PidGains.CreateAccelerationLawDefaults();
            return "Ka=" + law.Ka.ToString("0", CultureInfo.InvariantCulture) +
                "/Kad=" + law.Kad.ToString("0.#", CultureInfo.InvariantCulture) +
                "/Kai=" + law.Kai.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// PID 卡提示行：只列真正生效的关节加速度 / 参考角速度限幅与实测耦合符号，
        /// 并把 Ka / Kad / Kai 明确标成历史值 —— 旧提示里的「结构判据」已经不成立，不能再教用户去调历史增益。
        /// </summary>
        private void UpdatePidLawHint()
        {
            if (_pidHintLabel == null)
                _pidHintLabel = UiControlFinder.FindByName(this, "lblPidHint") as Label;
            if (_pidHintLabel == null)
                return;
            if (_pidHintBaseText == null)
                _pidHintBaseText = _pidHintLabel.Text ?? string.Empty;
            // 耦合符号是台架实测值、运行期由 PendulumEngine.ArmCouplingSign 固定，界面只如实展示，不给可编辑入口。
            string line = "关节加速度限幅 " + _pidShadow.AccMax.ToString("F0", CultureInfo.InvariantCulture) +
                " rad/s² · 参考角速度限幅 " + _pidShadow.ArmRateMax.ToString("F1", CultureInfo.InvariantCulture) +
                " rad/s · 耦合符号（实测） " + (_pidShadow.Sign < 0 ? "-1" : "+1") + "（固定，不可手改）";
            string body = (string.IsNullOrEmpty(_pidHintBaseText) ? line : _pidHintBaseText + "\r\n" + line) +
                "\r\n历史项：Ka / Kad / Kai 不参与平衡，只随参数文件读写在界面上显示。";
            UiStyles.SetLabel(_pidHintLabel, body);
        }

        private void FillPumpBoxes()
        {
            PumpParameters pump = PumpParameters.From(_swingShadow);
            SetNumber(_keBox, pump.Ke);
            SetNumber(_pumpAmpBox, pump.PumpAmpDeg);
            SetNumber(_pumpFreqBox, pump.PumpHz);
            SetNumber(_pumpPhaseBox, pump.PhaseOffsetDeg);
        }

        /// <summary>
        /// 载入参数 / 恢复默认后回填标定卡的计数框：0 点与左右限位都直接取控制层生效的绝对 counts，
        /// 不做任何角度反算（内部只有 counts 一份权威）。
        /// </summary>
        private void FillCalibrationFields()
        {
            if (_armZeroCountBox == null || _pendulumZeroCountBox == null || _pendulum360CountBox == null)
                return;
            int zero = _engine.ArmZeroCount;
            int pendulumZero = _engine.PendulumZeroRawCount;
            bool hasPendulum360 = _persistedExtras != null && _persistedExtras.Calibration != null &&
                                  _persistedExtras.Calibration.HasPendulum360;
            int pendulum360 = hasPendulum360 ? _persistedExtras.Calibration.Pendulum360Count : 0;
            _suppressCalibrationEvents = true;
            try
            {
                if (_engine.ArmZeroCalibrated && !_engine.CalibrationCleared)
                    ShowNumberCalibrated(_armZeroCountBox, zero);
                else
                    ShowNumberUncalibrated(_armZeroCountBox);
                if (_limitsShadow.ArmLimitsArePlaceholder)
                {
                    ShowNumberUncalibrated(_limitMinCountsBox);
                    ShowNumberUncalibrated(_limitMaxCountsBox);
                }
                else
                {
                    ShowNumberCalibrated(_limitMinCountsBox, _limitsShadow.ArmMinCount);
                    ShowNumberCalibrated(_limitMaxCountsBox, _limitsShadow.ArmMaxCount);
                }
                if (_engine.PendulumZeroCalibrated && !_engine.CalibrationCleared)
                    ShowNumberCalibrated(_pendulumZeroCountBox, pendulumZero);
                else
                    ShowNumberUncalibrated(_pendulumZeroCountBox);
                if (hasPendulum360)
                    ShowNumberCalibrated(_pendulum360CountBox, pendulum360);
                else
                    ShowNumberUncalibrated(_pendulum360CountBox);
            }
            finally
            {
                _suppressCalibrationEvents = false;
            }
            _armZeroDirty = false;
            _limitCountsDirty = false;
            _pendulumZeroDirty = false;
            _pendulum360Dirty = false;
            // 两点齐全时立即显示派生的一圈的量；只有旧 pendulum_cpr 时由 LoadInitialCpr 显示兼容值。
            long diff = (long)pendulum360 - pendulumZero;
            if (diff < 0) diff = -diff;
            if (diff > 0 && diff <= int.MaxValue && _engine.PendulumZeroCalibrated &&
                !_engine.CalibrationCleared && hasPendulum360)
            {
                SetPendulumCprReadout(diff.ToString("N0", CultureInfo.InvariantCulture) + " counts/圈",
                    UiStyles.ValueText);
            }
        }

        /// <summary>启动时把控制层当前的一圈的量显示出来（只读；新口径由 0°/360° 两点标定派生）。</summary>
        private void LoadInitialCpr()
        {
            double cpr;
            try
            {
                cpr = _engine.PendulumCountsPerRev;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "读摆杆一圈的量失败：" + DescribeException(ex));
                return;
            }
            // 两点标定齐全时 FillCalibrationFields 已经显示派生值，这里只补旧文件 / 未标定的情形。
            if (_pendulumCprLabel != null && _pendulumCprLabel.Text.StartsWith("未标定", StringComparison.Ordinal))
            {
                SetPendulumCprReadout(
                    cpr > 0
                        ? cpr.ToString("0.###", CultureInfo.InvariantCulture) + " counts/圈（旧文件值，建议用 0°/360° 重标）"
                        : "未标定（标定 0° 与 360° 两点）",
                    cpr > 0 ? UiStyles.ValueText : UiStyles.WarnOrange);
            }
            if (cpr <= 0)
                Log(LogLevel.Warn, "摆杆一圈的量未标定：标定「摆杆零点（下垂 0°）」与「摆杆 360°」两点后自动得出。");
        }

        private static void SetNumber(NumericUpDown box, double value)
        {
            if (box == null || double.IsNaN(value) || double.IsInfinity(value))
                return;
            decimal dec = (decimal)value;
            if (dec < box.Minimum) dec = box.Minimum;
            if (dec > box.Maximum) dec = box.Maximum;
            dec = Math.Round(dec, box.DecimalPlaces);
            if (box.Value != dec)
                box.Value = dec;
        }

        /// <summary>未标定显示：Value 回 0（框仍可编辑），文本留空；不把 0 冒充真值。</summary>
        private static void ShowNumberUncalibrated(NumericUpDown box)
        {
            if (box == null) return;
            decimal zero = 0m;
            if (zero < box.Minimum) zero = box.Minimum;
            if (zero > box.Maximum) zero = box.Maximum;
            if (box.Value != zero) box.Value = zero;
            box.Text = string.Empty;
            if (box.ForeColor != UiStyles.WarnOrange)
                box.ForeColor = UiStyles.WarnOrange;
        }

        /// <summary>已标定显示：恢复数值文本与正常前景色。</summary>
        private static void ShowNumberCalibrated(NumericUpDown box, int value)
        {
            if (box == null) return;
            SetNumber(box, value);
            box.Text = box.Value.ToString("N0", CultureInfo.InvariantCulture);
            if (box.ForeColor != UiStyles.ValueText)
                box.ForeColor = UiStyles.ValueText;
        }

        private void RefreshUFullScale()
        {
            try
            {
                PidGains pid = _engine.BalancePid;
                double limit = Math.Abs(pid.OutMax);
                if (limit > 1e-6)
                    _uFullScaleRad = limit;
            }
            catch (Exception ex)
            {
                Log("读平衡 PID 输出限幅失败，沿用 " + _uFullScaleRad.ToString("F2") + " rad：" +
                    DescribeException(ex));
            }
        }

        private void RefreshSafetyText()
        {
            if (_safetyLabel == null)
                return;
            try
            {
                SafetyLimits limits = _limitsShadow;
                // S2 跟随误差是摆臂量：有「摆臂一圈计数」就按 counts 显示（与位置读数同一单位），
                // 没有就不折算角度、如实写「未标定」。
                double cpr = EffectiveArmCountsPerRev();
                string phiErr = cpr > 0.0
                    ? Math.Round(limits.PhiErrMaxRad * cpr / (2.0 * Math.PI), MidpointRounding.AwayFromZero)
                        .ToString("N0", CultureInfo.InvariantCulture) + " counts"
                    : "未标定";
                string text = string.Format(CultureInfo.InvariantCulture,
                    "安全阈值（只读）\r\nθ跳闸 {0:F1}° · θ失稳 {1:F1}° · φ误差 {2} · ω {3:F1}°/s\r\n" +
                    "软限位 {4} ~ {5} counts{6}",
                    limits.ThetaTripRad * UiStyles.RadToDeg,
                    limits.ThetaLostRad * UiStyles.RadToDeg,
                    phiErr,
                    limits.OmegaArmMaxRadPerSec * UiStyles.RadToDeg,
                    limits.ArmMinCount.ToString("N0", CultureInfo.InvariantCulture),
                    limits.ArmMaxCount.ToString("N0", CultureInfo.InvariantCulture),
                    limits.ArmLimitsArePlaceholder ? "（尚未采集）" : "");
                UiStyles.SetLabel(_safetyLabel, text);
            }
            catch (Exception ex)
            {
                UiStyles.SetLabel(_safetyLabel, "安全阈值 读取失败：" + ex.Message);
            }
        }

        // ================================================================ 操作闸门

        private Task RunOp(string label, Action action)
        {
            return RunOp(label, "完成", action);
        }

        /// <summary>
        /// doneWord：动作收尾时写进日志的词。纯排队命令（只写 _pendingCommand、真正动作在周期线程里跑）
        /// 必须传「已受理」，不能写「完成」，否则界面会把"命令已排队"说成"已经做完"。
        /// 传 null = 动作自己已经写了验收后的成功 / 失败行，闸门不再补一句可能自相矛盾的收尾。
        /// </summary>
        private Task RunOp(string label, string doneWord, Action action)
        {
            if (Interlocked.CompareExchange(ref _opBusy, 1, 0) != 0)
            {
                LogReject(label, REJECT_OTHER,
                    "上一项操作还在进行（" + (_opLabel ?? "…") + "），本次点击未执行，急停不受此限制", null);
                SetStatus("上一项操作还在进行（" + (_opLabel ?? "…") + "），等它结束再点。", UiStyles.WarnOrange);
                return Task.FromResult(0);
            }
            _opLabel = label;
            RefreshButtons();
            SetStatus(label + " 执行中…界面不阻塞，随时可急停。");
            Log("[操作] " + label + " 开始");
            return Task.Run(delegate
            {
                try
                {
                    action();
                    if (!string.IsNullOrEmpty(doneWord))
                        Log("[操作] " + label + " " + doneWord);
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, "[操作] " + label + " 失败：" + DescribeException(ex) +
                        (string.IsNullOrEmpty(ex.StackTrace) ? "" : "  " + FirstLines(ex.StackTrace, 3)));
                    SetStatus(label + " 失败：" + ex.Message, UiStyles.AlarmColor);
                }
                finally
                {
                    Interlocked.Exchange(ref _opBusy, 0);
                    _opLabel = null;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            RefreshIdleStatus();
                            RefreshButtons();
                        }));
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            });
        }

        private static string FirstLines(string text, int lines)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            string[] parts = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int take = Math.Min(lines, parts.Length);
            string result = "";
            for (int i = 0; i < take; i++)
                result += (i > 0 ? " | " : "") + parts[i].Trim();
            return result;
        }
    }
}
