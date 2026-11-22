using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FurutaPendulum.Cli;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 主界面刷新与日志（partial）：遥测 → 右侧单页各卡 + 左栏操作提示，15Hz 卡片、50Hz 杆角度表盘、30Hz 按钮使能。
    /// 单页里实时 PDO 字段与角度 / 换算 / PID / 限位读数同频一起刷，不再分页签。
    /// 日志只留错误 / 警告与用户动作；1Hz 控制层心跳与例行诊断不入日志（只在 Debug 输出）。
    /// 例外：[SWING] 逐秒证据必须同时进界面与落盘文件，起摆超时要能直接定位能量 / 泵 / 摆臂哪一项没起来。
    /// UI 线程只读快照 / 缓存，不做任何总线 I/O、不阻塞控制层。
    /// </summary>
    public sealed partial class MainForm : Form
    {
        // ---------------------------------------------------------------- 诊断日志门
        // 周期遥测（1Hz beat）与例行信息不进日志区 / 落盘文件；只保留错误与警告。
        // 同一行 10 秒内重复只留第一条，避免故障抖动把日志刷满。
        private const double DIAGNOSTIC_REPEAT_SUPPRESS_SEC = 10.0;
        private readonly object _diagnosticGate = new object();
        private readonly Dictionary<string, long> _diagnosticLastTicks =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private int _dialErrors;

        // ---------------------------------------------------------------- 定时刷新

        private void OnUiTimerTick(object sender, EventArgs e)
        {
            try
            {
                _engine.PumpEvents();
                EngineState state = _engine.State;
                PendulumTelemetry telemetry;
                bool haveTelemetry = TryReadTelemetry(out telemetry);
                if (haveTelemetry)
                {
                    _lastThetaDeg = telemetry.ThetaFromUprightRad * UiStyles.RadToDeg;
                    _lastThetaAbsDeg = Wrap360(telemetry.ThetaRad * UiStyles.RadToDeg);
                    _lastOmegaDegPerSec = telemetry.OmegaRadPerSec * UiStyles.RadToDeg;
                    _lastUPercent = UPercent(telemetry.ControlThetaOut);
                    _lastEStopLatch = telemetry.EStopLatch;
                    // 连接后首帧就核对保存零点：差一行字也比等用户点起摆才发现强。
                    if (!_poseReferenceChecked && state != EngineState.Disconnected)
                        UpdatePoseReference(telemetry);
                }

                long nowTicks = Stopwatch.GetTimestamp();
                bool slow = nowTicks - _lastSlowTicks >= (long)(Stopwatch.Frequency * SLOW_REFRESH_INTERVAL_MS / 1000.0);
                if (slow)
                    _lastSlowTicks = nowTicks;

                if (nowTicks - _lastCardTicks >= (long)(Stopwatch.Frequency * CARD_REFRESH_INTERVAL_MS / 1000.0))
                {
                    _lastCardTicks = nowTicks;
                    RefreshCards(state, haveTelemetry, telemetry);
                }

                RefreshStatusPanel(slow, state, haveTelemetry, telemetry);
                RefreshButtons();
                TrackCaptureWindow(state);
                FlushLogs();
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _tickErrors) <= UI_LOG_ERROR_LIMIT)
                    Log(LogLevel.Error, "界面刷新异常：" + DescribeException(ex) + "  " + FirstLines(ex.StackTrace, 3));
            }
        }

        private bool TryReadTelemetry(out PendulumTelemetry telemetry)
        {
            telemetry = default(PendulumTelemetry);
            try
            {
                if (!_engine.TryReadTelemetry(out telemetry))
                    return false;
                // 控制线程写出第一帧之前槽里是零值（seqlock 版本 0）。时间戳 0 = 从未采过样，
                // 这时按「无遥测」显示 —，不把 0 counts / 0 状态字当成真实读数。
                if (telemetry.TimestampTicks <= 0)
                {
                    telemetry = default(PendulumTelemetry);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _readErrors) <= UI_LOG_ERROR_LIMIT)
                    Log(LogLevel.Warn, "读遥测快照失败：" + DescribeException(ex));
                return false;
            }
        }

        private double UPercent(double controlThetaOut)
        {
            double full = _uFullScaleRad > 1e-6 ? _uFullScaleRad : 1.0;
            return UiStyles.Clamp(controlThetaOut / full * 100.0, -100.0, 100.0);
        }

        // ---------------------------------------------------------------- 卡片刷新

        private void RefreshCards(EngineState state, bool haveTelemetry, PendulumTelemetry telemetry)
        {
            RefreshStepLiveCard(state, haveTelemetry, telemetry);
            RefreshCurrentCard(state, haveTelemetry, telemetry);
            RefreshPdoCard(state, haveTelemetry, telemetry);
            RefreshAngleCard(haveTelemetry, telemetry);
            RefreshDeviceCard(haveTelemetry, telemetry);
            RefreshCalibrationCard();
            UpdatePumpStatusLabel();
            RefreshLimitCountFields();
        }

        /// <summary>
        /// 设备参数卡的标定部分：零点 / 摆杆 0° 框跟随控制层，一圈的量只读显示，摆臂一圈计数只读显示。
        /// 只读遥测快照与引擎状态，不写参数。
        /// </summary>
        private void RefreshCalibrationCard()
        {
            if (_calibrationHintLabel == null)
                return;

            // 零点框跟随控制层实际值（使能自动归零 / 采集 / 载入参数都会改它）；正在编辑的框不覆盖。
            if (_armZeroCountBox != null && !_armZeroCountBox.Focused)
            {
                _suppressCalibrationEvents = true;
                try
                {
                    if (_engine.ArmZeroCalibrated && !_engine.CalibrationCleared)
                        ShowNumberCalibrated(_armZeroCountBox, _engine.ArmZeroCount);
                    else
                        ShowNumberUncalibrated(_armZeroCountBox);
                }
                finally
                {
                    _suppressCalibrationEvents = false;
                }
            }

            // 摆杆 0° 点框：跟随控制层（采集 / 载入参数 / 设置都会改它），正在编辑时不覆盖。
            if (_pendulumZeroCountBox != null && !_pendulumZeroCountBox.Focused)
            {
                _suppressCalibrationEvents = true;
                try
                {
                    if (_engine.PendulumZeroCalibrated && !_engine.CalibrationCleared)
                        ShowNumberCalibrated(_pendulumZeroCountBox, _engine.PendulumZeroRawCount);
                    else
                        ShowNumberUncalibrated(_pendulumZeroCountBox);
                }
                finally
                {
                    _suppressCalibrationEvents = false;
                }
            }

            // 摆杆 0°/360° 的成对状态常显：缺哪一半必须一眼看到（起摆 / 平衡前置校验按它拒绝），
            // 不能只写在载入提示或日志里。判据唯一来源 = CalibrationParameters.CheckPendulumPair。
            PendulumPairCheck pair = CalibrationParameters.CheckPendulumPair(
                _persistedExtras == null ? null : _persistedExtras.Calibration);
            string pairText = pair.IsComplete
                ? "已完成（|360°−0°| = " + pair.SpanCounts.ToString("N0", CultureInfo.InvariantCulture) + " counts）"
                : pair.MissingText + "（起摆 / 平衡会被拒）";

            // 摆臂一圈计数只有这一处展示，且是只读：权威 = 驱动器 0x2102 实读 × 减速比。
            double cpr = EffectiveArmCountsPerRev();
            UiStyles.SetLabel(_calibrationHintLabel,
                "摆杆 0°/360°：" + pairText + " · 摆臂一圈 " +
                (cpr > 0.0 ? cpr.ToString("N0") + " counts" : "未标定") +
                "（0x2102 × 减速比，只读）");
        }

        /// <summary>卡片⑤：当前 0x6064（绝对 + 相对零点）、当前摆杆计数，超限位时变红。全部按 counts。</summary>
        private void RefreshDeviceCard(bool haveTelemetry, PendulumTelemetry telemetry)
        {
            if (!haveTelemetry)
            {
                SetReadout(_limitCurrentLabel, null, Color.Empty);
                return;
            }
            int raw = telemetry.ArmPositionCount;
            int zero = ReadArmZero();
            int offset = unchecked(raw - zero);
            bool armCalibrated = _engine.ArmZeroCalibrated && !_engine.CalibrationCleared &&
                                 !_limitsShadow.ArmLimitsArePlaceholder;
            // 只报 counts：限位判定与显示同一单位，不折算摆臂角度。未标定时不拿 0 冒充「相对 0 点」。
            string text = armCalibrated
                ? raw.ToString("N0") + " · 相对 0 点 " + (offset >= 0 ? "+" : "") + offset.ToString("N0")
                : raw.ToString("N0") + " · 未标定（零点 / 左右限位未采集）";
            int minRaw = _limitsShadow.ArmMinCount;
            int maxRaw = _limitsShadow.ArmMaxCount;
            bool outside = armCalibrated && (raw < minRaw || raw > maxRaw);
            SetReadout(_limitCurrentLabel, text,
                !armCalibrated ? UiStyles.WarnOrange : (outside ? UiStyles.AlarmColor : UiStyles.ValueText));
        }

        /// <summary>
        /// 卡片 PDO 实时数据：目标 / 实际 / 偏差 / 速度 / 电流 / 状态字 / 错误码 / 使能 / 模式 / 反馈性质 / 从站。
        /// 全部取 PDO 过程映像字段（0x607A / 0x6064 / 0x606C / 0x6041 / 0x603F / 0x6061 + 从站 OP 状态），
        /// 不按角度反算；0x6077 未映射就显示「未映射」，不拿 0 冒充真值。
        /// </summary>
        private void RefreshStepLiveCard(EngineState state, bool haveTelemetry, PendulumTelemetry telemetry)
        {
            if (!haveTelemetry)
            {
                SetReadout(_pdoTargetPosLabel, null, Color.Empty);
                SetReadout(_pdoActualPosLabel, null, Color.Empty);
                SetReadout(_followErrorLabel, null, Color.Empty);
                SetReadout(_stepVelocityLabel, null, Color.Empty);
                SetReadout(_stepTorqueLabel, "未映射", UiStyles.CaptionText);
                SetReadout(_pdoStatusWordLabel, null, Color.Empty);
                SetReadout(_pdoErrorCodeLabel, null, Color.Empty);
                SetReadout(_stepEnableLabel, null, Color.Empty);
                SetReadout(_pdoModeLabel, null, Color.Empty);
                SetReadout(_stepFeedbackLabel, null, Color.Empty);
                SetReadout(_stepSlaveLabel, null, Color.Empty);
                return;
            }

            bool driving = state == EngineState.Enabled || state == EngineState.Swinging || state == EngineState.Balancing;

            // 位置类只给原始 counts，不追加度 / 圈换算（换算口径只在设备参数卡说明）。
            SetReadout(_pdoActualPosLabel, telemetry.ArmPositionCount.ToString("N0"), UiStyles.ValueText);
            SetReadout(_pdoTargetPosLabel, telemetry.ArmTargetCount.ToString("N0"), UiStyles.ValueText);

            int followError = telemetry.FollowingErrorCount;   // 控制层同拍算：目标 − 实际
            double cpr = EffectiveArmCountsPerRev();
            double followLimitCounts = cpr > 0.0
                ? _limitsShadow.PhiErrMaxRad * cpr / (2.0 * Math.PI)
                : double.MaxValue;
            SetReadout(_followErrorLabel, followError.ToString("N0"),
                Math.Abs(followError) > followLimitCounts ? UiStyles.AlarmColor : UiStyles.ValueText);

            // 0x606C 原值直显（Pulse/s，与 0x6064 同口径）；本台架 EC1 只回幅值，不做 rad/s 换算。
            SetReadout(_stepVelocityLabel, telemetry.ArmVelocityPulsePerSec.ToString("N0"), UiStyles.ValueText);

            // 0x6077 不在本台架的 PDO 映射里：如实标注，不显示 0 A。
            SetReadout(_stepTorqueLabel, "未映射", UiStyles.CaptionText);

            ushort statusWord = telemetry.StatusWord;
            bool statusBad = CiA402.IsFault(statusWord) || (driving && !CiA402.IsOperationEnabled(statusWord));
            SetReadout(_pdoStatusWordLabel,
                "0x" + statusWord.ToString("X4") + " · " + DescribeStatusWord(statusWord),
                statusBad ? UiStyles.AlarmColor : UiStyles.ValueText);

            SetReadout(_pdoErrorCodeLabel, telemetry.DriveErrorCode != 0
                    ? "0x" + telemetry.DriveErrorCode.ToString("X4") + " · 有错误"
                    : "0x0000 · 无错误",
                telemetry.DriveErrorCode != 0 ? UiStyles.AlarmColor : UiStyles.ValueText);

            bool enabled = CiA402.IsOperationEnabled(statusWord);
            SetReadout(_stepEnableLabel, enabled ? "已使能" : "未使能",
                enabled ? UiStyles.OkGreen : (statusBad ? UiStyles.AlarmColor : UiStyles.CaptionText));

            bool modeBad = telemetry.ModesDisplay != CiA402.ModeCsp;
            SetReadout(_pdoModeLabel, modeBad
                    ? telemetry.ModesDisplay + "（应为 8 CSP）"
                    : "CSP (8)",
                modeBad ? UiStyles.AlarmColor : UiStyles.ValueText);

            SetReadout(_stepFeedbackLabel, telemetry.ArmFeedbackIsEstimate ? "开环估计" : "实测",
                telemetry.ArmFeedbackIsEstimate ? UiStyles.WarnOrange : UiStyles.ValueText);

            SetReadout(_stepSlaveLabel, telemetry.ArmSlaveInOp ? "OP" : "未进 OP",
                telemetry.ArmSlaveInOp ? UiStyles.ValueText : UiStyles.AlarmColor);
        }

        /// <summary>
        /// 卡片 当前状态：关节绝对 / 相对零点计数、软限位状态（全部按原始计数）。
        /// 角度类读数不在这张卡里（见同页的角度读数卡）。
        /// </summary>
        private void RefreshCurrentCard(EngineState state, bool haveTelemetry, PendulumTelemetry telemetry)
        {
            if (!haveTelemetry)
            {
                SetReadout(_armPositionLabel, null, Color.Empty);
                SetReadout(_armOffsetLabel, null, Color.Empty);
                SetReadout(_armLimitStateLabel, null, Color.Empty);
                return;
            }

            int zero = ReadArmZero();
            int raw = telemetry.ArmPositionCount;
            int offset = unchecked(raw - zero);
            bool armCalibrated = _engine.ArmZeroCalibrated && !_engine.CalibrationCleared &&
                                 !_limitsShadow.ArmLimitsArePlaceholder;

            SetReadout(_armPositionLabel, raw.ToString("N0"), UiStyles.ValueText);
            SetReadout(_armOffsetLabel,
                armCalibrated
                    ? (offset >= 0 ? "+" : "") + offset.ToString("N0")
                    : "未标定",
                armCalibrated ? UiStyles.ValueText : UiStyles.WarnOrange);

            int minRaw = _limitsShadow.ArmMinCount;
            int maxRaw = _limitsShadow.ArmMaxCount;
            bool outside = armCalibrated && (raw < minRaw || raw > maxRaw);
            SetReadout(_armLimitStateLabel, armCalibrated
                    ? (outside
                        ? "超限 " + raw.ToString("N0") + "（" + minRaw.ToString("N0") + "~" + maxRaw.ToString("N0") + "）"
                        : "限位内 · " + minRaw.ToString("N0") + "~" + maxRaw.ToString("N0"))
                    : "未标定",
                armCalibrated ? (outside ? UiStyles.AlarmColor : UiStyles.ValueText) : UiStyles.WarnOrange);
        }

        /// <summary>卡片 总线与采样：WKC / PDO 周期 / 从站在线 / 主站实测状态 / 链路 / 端口 / RT 周期 / 采样时刻 / 配置来源。</summary>
        private void RefreshPdoCard(EngineState state, bool haveTelemetry, PendulumTelemetry telemetry)
        {
            BusStatusSnapshot bus = default(BusStatusSnapshot);
            bool haveBus = false;
            bool connected = state != EngineState.Disconnected;
            if (connected)
            {
                try
                {
                    haveBus = _engine.TryReadBusStatus(out bus) && bus.Valid;
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref _readErrors) <= UI_LOG_ERROR_LIMIT)
                        Log(LogLevel.Warn, "读总线状态快照失败：" + DescribeException(ex));
                }
            }

            // 配置来源：本案例只认 DENI，界面如实写来源文件，不谎报成 ESI；未连接显示"—"。
            SetReadout(_configSourceLabel,
                connected ? BusSession.DescribeConfigSource(_engine.DeniPath) : null,
                connected ? UiStyles.ValueText : Color.Empty);

            if (haveTelemetry)
            {
                ushort actual = telemetry.ActualWkc;
                ushort expected = telemetry.ExpectedWkc;
                bool wkcOk = expected > 0 && actual == expected;
                SetReadout(_busWkcLabel, actual + " / " + expected, wkcOk ? UiStyles.ValueText : UiStyles.AlarmColor);

                long ageTicks = Stopwatch.GetTimestamp() - telemetry.TimestampTicks;
                double ageSec = ageTicks > 0 ? ageTicks / (double)Stopwatch.Frequency : 0.0;
                DateTime sampleAt = DateTime.Now - TimeSpan.FromSeconds(Math.Min(ageSec, 3600.0));
                bool stale = ageSec > 1.0;
                SetReadout(_sampleTimeLabel,
                    sampleAt.ToString("HH:mm:ss.fff") + (stale ? " · 陈旧 " + (ageSec * 1000.0).ToString("F0") + " ms" : " · 新鲜"),
                    stale ? UiStyles.WarnOrange : UiStyles.ValueText);
            }
            else
            {
                SetReadout(_busWkcLabel, null, Color.Empty);
                SetReadout(_sampleTimeLabel, null, Color.Empty);
            }

            if (haveBus && bus.PdoCycleCountKnown && bus.PdoCycleCount > 0)
                SetReadout(_busCycleCountLabel, bus.PdoCycleCount.ToString("N0"), UiStyles.ValueText);
            else
                SetReadout(_busCycleCountLabel, null, Color.Empty);

            if (haveBus && bus.SlaveCount > 0)
            {
                string online = bus.OnlineCountKnown ? bus.OnlineSlaveCount.ToString() : "—";
                bool allOnline = !bus.OnlineCountKnown || bus.OnlineSlaveCount >= bus.SlaveCount;
                SetReadout(_busSlavesLabel, "在线 " + online + " / " + bus.SlaveCount,
                    allOnline ? UiStyles.ValueText : UiStyles.AlarmColor);
            }
            else
            {
                SetReadout(_busSlavesLabel, null, Color.Empty);
            }

            if (haveBus)
            {
                bool inOp = string.Equals(bus.MasterState, "OP", StringComparison.Ordinal);
                SetReadout(_busMasterLabel,
                    DescribeEcState(bus.MasterState) + (bus.RealOp ? " · 实测" : " · 未确认"),
                    inOp && bus.RealOp ? UiStyles.OkGreen : UiStyles.WarnOrange);
                SetReadout(_busLinkLabel, LinkStateText(bus.LinkState),
                    string.Equals(bus.LinkState, "Connected", StringComparison.Ordinal)
                        ? UiStyles.ValueText
                        : UiStyles.WarnOrange);
                SetReadout(_busPortLabel,
                    PortStateText(bus.PrimaryPort) + " / " + PortStateText(bus.SecondaryPort),
                    UiStyles.ValueText);
                SetReadout(_busRtLabel, bus.RtHz > 0 ? bus.RtHz + " Hz" : null, UiStyles.ValueText);
            }
            else
            {
                SetReadout(_busMasterLabel, null, Color.Empty);
                SetReadout(_busLinkLabel, null, Color.Empty);
                SetReadout(_busPortLabel, null, Color.Empty);
                SetReadout(_busRtLabel, null, Color.Empty);
            }
        }

        /// <summary>
        /// 卡片 角度读数（单页）：θ / ψ / ω / 摆臂 φ / 能量 E / 目标能量 E* / 接近倒立 / CH1·CH2 原始计数 / CH1 频率。
        /// 只读遥测快照；频率计仅在 GC1502 在位时有值，GC1602 如实显示「未提供」。
        /// </summary>
        private void RefreshAngleCard(bool haveTelemetry, PendulumTelemetry telemetry)
        {
            if (!haveTelemetry)
            {
                SetReadout(_thetaLabel, null, Color.Empty);
                SetReadout(_psiLabel, null, Color.Empty);
                SetReadout(_omegaLabel, null, Color.Empty);
                SetReadout(_phiLabel, null, Color.Empty);
                SetReadout(_energyLabel, null, Color.Empty);
                SetReadout(_energyTargetLabel, null, Color.Empty);
                SetReadout(_nearUprightLabel, null, Color.Empty);
                SetReadout(_pendulumCountLabel, null, Color.Empty);
                SetReadout(_pendulumCountCh2Label, null, Color.Empty);
                SetReadout(_pendulumFreqLabel, null, Color.Empty);
                return;
            }

            double thetaAbsDeg = _lastThetaAbsDeg;
            double thetaFromUprightDeg = _lastThetaDeg;
            double omegaDegPerSec = _lastOmegaDegPerSec;

            SetReadout(_thetaLabel, thetaAbsDeg.ToString("F1") + "° (" +
                (thetaAbsDeg * Math.PI / 180.0).ToString("F4") + " rad)", UiStyles.ValueText);
            SetReadout(_psiLabel, IsFinite(thetaFromUprightDeg)
                    ? thetaFromUprightDeg.ToString("F1") + "° (" +
                      telemetry.ThetaFromUprightRad.ToString("F3") + " rad)"
                    : null,
                IsFinite(thetaFromUprightDeg) ? UiStyles.ValueText : Color.Empty);
            SetReadout(_omegaLabel, IsFinite(omegaDegPerSec) ? omegaDegPerSec.ToString("F1") : null,
                IsFinite(omegaDegPerSec) ? UiStyles.ValueText : Color.Empty);
            double phiDeg = telemetry.PhiRad * UiStyles.RadToDeg;
            SetReadout(_phiLabel, IsFinite(phiDeg)
                    ? phiDeg.ToString("F1") + "° (" + telemetry.PhiRad.ToString("F3") + " rad)"
                    : null,
                IsFinite(phiDeg) ? UiStyles.ValueText : Color.Empty);

            // 能量口径与控制层同一帧：E = 0.5·J_eq·ω² + mgl·(1−cosθ)，E* = 2·mgl·(1+ε)。
            // 差值一起给出，起摆时能直接看出离目标还差多少；目标能量 <=0（未配置）时如实显示 —。
            double energy = telemetry.EnergyJ;
            double energyTarget = telemetry.EnergyTargetJ;
            SetReadout(_energyLabel,
                double.IsNaN(energy) || double.IsInfinity(energy) ? null : energy.ToString("F3") + " J",
                UiStyles.ValueText);
            bool targetKnown = !double.IsNaN(energyTarget) && !double.IsInfinity(energyTarget) && energyTarget > 0.0;
            SetReadout(_energyTargetLabel,
                targetKnown
                    ? energyTarget.ToString("F3") + " J（差 " + telemetry.EnergyDeltaJ.ToString("F3") + "）"
                    : null,
                targetKnown ? UiStyles.ValueText : Color.Empty);

            bool nearUpright = Math.Abs(thetaFromUprightDeg) < NEAR_UPRIGHT_DEG;
            SetReadout(_nearUprightLabel,
                (nearUpright ? "是" : "否") + " · Δ" + thetaFromUprightDeg.ToString("F1") + "°",
                nearUpright ? UiStyles.OkGreen : UiStyles.CaptionText);

            SetReadout(_pendulumCountLabel, telemetry.PendulumCount.ToString(), UiStyles.ValueText);
            SetReadout(_pendulumCountCh2Label, telemetry.PendulumCountCh2.ToString(), UiStyles.ValueText);

            if (telemetry.FreqCrossCheckValid)
                SetReadout(_pendulumFreqLabel, telemetry.PendulumFreqCh1Hz.ToString("N0"), UiStyles.ValueText);
            else
                SetReadout(_pendulumFreqLabel, "未提供（无频率计）", UiStyles.CaptionText);
        }

        /// <summary>
        /// 卡片③ 杆角度图：按控制层遥测 θ 画表盘（0 = 下垂，180 = 倒立）。
        /// 独立 50Hz 定时器调用：只读快照 + 存值 + Invalidate，不做字符串 / 总线 I/O。
        /// </summary>
        private void OnDialTimerTick(object sender, EventArgs e)
        {
            if (_pendulumDial == null || _pendulumDial.IsDisposed)
                return;
            try
            {
                PendulumTelemetry telemetry;
                bool haveTelemetry = TryReadTelemetry(out telemetry);
                EngineState state = _engine.State;

                double cpr = _engine.PendulumCountsPerRev;
                double thetaDeg = 0.0;
                if (haveTelemetry && !double.IsNaN(telemetry.ThetaRad) && !double.IsInfinity(telemetry.ThetaRad))
                    thetaDeg = telemetry.ThetaRad * UiStyles.RadToDeg;
                int wrappedCounts = haveTelemetry && cpr > 0.0
                    ? WrapPendulumCounts(thetaDeg, cpr)
                    : 0;
                bool alarm = state == EngineState.Fault ||
                             (haveTelemetry ? telemetry.EStopLatch : _lastEStopLatch);
                _pendulumDial.SetPose(thetaDeg, wrappedCounts, cpr,
                    state != EngineState.Disconnected, alarm, haveTelemetry);
                // 杆读数冻结（CH1 计数长时间不变）就在表盘上直说；
                // 上膛扶住 / 记录中由 ShouldWarnPendulumFeedback 抑制（那是正常操作）。
                _pendulumDial.SetFeedbackLive(!ShouldWarnPendulumFeedback(),
                    haveTelemetry ? telemetry.PendulumFeedbackAgeSeconds : double.PositiveInfinity);
                _pendulumDial.Invalidate();
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _dialErrors) <= UI_LOG_ERROR_LIMIT)
                    Log(LogLevel.Error, "杆角度图刷新异常：" + DescribeException(ex));
            }
        }

        /// <summary>θ 度 → ±半圈计数（2400 CPR 时 180° = ±1200，符号与杆方向一致）。</summary>
        private static int WrapPendulumCounts(double thetaDeg, double countsPerRev)
        {
            double counts = thetaDeg / 360.0 * countsPerRev;
            double half = countsPerRev * 0.5;
            counts = counts % countsPerRev;
            if (counts > half)
                counts -= countsPerRev;
            else if (counts < -half)
                counts += countsPerRev;
            return (int)Math.Round(counts);
        }

        private int ReadArmZero()
        {
            try
            {
                return _engine.ArmZeroCount;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _readErrors) <= UI_LOG_ERROR_LIMIT)
                    Log(LogLevel.Warn, "读关节零点失败：" + DescribeException(ex));
                return 0;
            }
        }

        /// <summary>0x6041 状态字中文解读（只用于显示，不参与任何判据）。</summary>
        private static string DescribeStatusWord(ushort statusWord)
        {
            if (CiA402.IsFault(statusWord))
                return "故障";
            if (CiA402.IsQuickStopActive(statusWord))
                return "快速停止";
            if (CiA402.IsSwitchOnDisabled(statusWord))
                return "未就绪（禁止合闸）";
            if (CiA402.IsOperationEnabled(statusWord))
                return "运行使能";
            if (CiA402.IsSwitchedOn(statusWord))
                return "已合闸";
            if (CiA402.IsReadyToSwitchOn(statusWord))
                return "就绪（待合闸）";
            return "未知状态";
        }

        private static double Wrap360(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg))
                return 0.0;
            double value = deg % 360.0;
            if (value < 0.0) value += 360.0;
            return value;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>读数格赋值：文本 / 颜色不变不重绘；null 或空 → "—" 用离线灰，不拿 0 冒充真值。</summary>
        private static void SetReadout(Label label, string text, Color color)
        {
            if (string.IsNullOrEmpty(text))
            {
                UiStyles.SetLabelColored(label, "—", UiStyles.OfflineGray);
                return;
            }
            UiStyles.SetLabelColored(label, text, color);
        }

        /// <summary>EcLinkState 名字转中文四态；不认识就原样带回，不臆造。</summary>
        private static string LinkStateText(string state)
        {
            if (string.IsNullOrEmpty(state))
                return null;
            switch (state)
            {
                case "Connected": return "两口已连接";
                case "PrimaryOnly": return "仅主口";
                case "SecondaryOnly": return "仅副口";
                case "Disconnected": return "断开";
                case "Unknown": return "未知";
                default: return state;
            }
        }

        /// <summary>PhyLinkState 名字转中文三态；Unknown（未标定）与 Down（确证断开）严格分开。</summary>
        private static string PortStateText(string state)
        {
            if (string.IsNullOrEmpty(state))
                return "—";
            switch (state)
            {
                case "Up": return "已连接";
                case "Down": return "断开";
                case "Unknown": return "未知";
                default: return state;
            }
        }

        /// <summary>
        /// 总线状态格：以主站实测状态为准，不按引擎状态猜。
        /// 直连进 OP 模式下去使能后总线仍留在 OP（不回退 SafeOp），这时引擎状态是 Connected，
        /// 若照旧写死 "SafeOp" 就是谎报。
        /// </summary>
        /// <summary>EtherCAT 状态名转中文并保留原名（排查时仍要能看到真值）。</summary>
        private static string DescribeEcState(string state)
        {
            if (string.IsNullOrEmpty(state))
                return "—";
            switch (state)
            {
                case "OP": return "运行（OP）";
                case "SafeOp": return "安全运行（SafeOp）";
                case "PreOp": return "预运行（PreOp）";
                case "Init": return "初始化（Init）";
                case "None": return "未配置";
                default: return state;
            }
        }

        private string BusStateText(EngineState state, FaultCode faultCode, out Color color)
        {
            if (state == EngineState.Disconnected)
            {
                color = UiStyles.OfflineGray;
                return "离线";
            }
            if (faultCode == FaultCode.EtherCatState)
            {
                color = UiStyles.AlarmColor;
                return "未进入 OP";
            }
            string actual = _engine.BusStateName;
            if (string.Equals(actual, "OP", StringComparison.Ordinal))
            {
                // 直连诊断模式常显标记：OP 是绕过 SafeOp 上去的，非标准路径。
                if (_engine.OpEntryMode == OpEntryMode.DirectFromPreOp)
                {
                    color = UiStyles.WarnOrange;
                    return "运行 OP（直连，非标准）";
                }
                color = UiStyles.OkGreen;
                return "运行（OP）";
            }
            if (state == EngineState.Connected)
            {
                color = UiStyles.CaptionText;
                return DescribeEcState(actual);
            }
            color = UiStyles.OkGreen;
            return "运行（OP）";
        }

        // ---------------------------------------------------------------- 当前状态（总线 / 引擎 / 运行 / 参数）
        //
        // 四行都挂在主视野「当前状态」卡上（原来的左栏状态卡已并入该卡，界面只保留一份当前状态）。
        // 刷新频率仍按原口径：总线 / 引擎 / 运行每拍刷，安全阈值与泵状态仍按 slow 节拍刷。

        private void RefreshStatusPanel(bool slow, EngineState state, bool haveTelemetry, PendulumTelemetry telemetry)
        {
            bool connected = state != EngineState.Disconnected;
            long now = Stopwatch.GetTimestamp();
            if (connected && !_sessionActive)
            {
                _sessionActive = true;
                _sessionStartTicks = now;
            }
            else if (!connected && _sessionActive)
            {
                _sessionActive = false;
            }

            Color busColor;
            FaultCode faultCode = haveTelemetry ? telemetry.FaultCode : FaultCode.None;
            UiStyles.SetLabelColored(_statusConnLabel, BusStateText(state, faultCode, out busColor), busColor);

            string chip = "[" + FormatState(state, _lastEStopLatch) + "]";
            Color chipColor;
            Color chipBack = Color.Transparent;
            if (state == EngineState.Fault)
            {
                chipColor = Color.White;
                chipBack = UiStyles.AlarmColor;
            }
            else if (state == EngineState.Swinging || state == EngineState.Balancing)
            {
                chipColor = UiStyles.TitleBlue;
            }
            else if (state == EngineState.Disconnected)
            {
                chipColor = UiStyles.OfflineGray;
            }
            else
            {
                chipColor = UiStyles.ValueText;
            }
            UiStyles.SetLabelColored(_statusStateLabel, chip, chipColor);
            if (_statusStateLabel.BackColor != chipBack)
                _statusStateLabel.BackColor = chipBack;

            if (connected && _sessionActive)
            {
                double seconds = (now - _sessionStartTicks) / (double)Stopwatch.Frequency;
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                    seconds = 0.0;
                TimeSpan span = TimeSpan.FromSeconds(seconds);
                UiStyles.SetLabelColored(_statusUptimeLabel,
                    string.Format("{0:00}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds),
                    UiStyles.ValueText);
            }
            else
            {
                UiStyles.SetLabelColored(_statusUptimeLabel, "—", UiStyles.OfflineGray);
            }

            if (slow)
            {
                RefreshSafetyText();
                UpdatePumpStatusLabel();
            }
        }

        // ---------------------------------------------------------------- 按钮使能

        internal static readonly string[] ButtonNames =
        {
            "btnConnect", "btnDisconnect", "btnEnable", "btnStop", "btnReset",
            "btnJogLeft", "btnJogRight", "btnSwingUp", "btnEStop",
            "btnSaveParams", "btnApplyParams", "btnLoadParams", "btnRestoreDefaults",
            "btnPidTuning", "btnSetArmZero", "btnSetLeftLimit", "btnSetRightLimit",
            "btnSetPendulumZero", "btnSetPendulum360",
            "btnReturnHome", "btnClearCalibration", "btnNormalizeArmPose",
            "btnNormalizePendulumPose"
        };

        /// <summary>按钮矩阵唯一输入。所有量都来自真实引擎 / 遥测快照，测试也走同一个判定入口。</summary>
        internal struct UiButtonStateSnapshot
        {
            public EngineState State;
            public bool LiveArm;
            public bool Busy;
            public bool StopActive;
            public bool JogHeld;
            public bool EStopLatch;
            public bool CaptureActive;
        }

        /// <summary>
        /// 23 按钮唯一可用性矩阵。标定 / 采集按钮按**引擎真实可写状态**启用：摆臂零点与左右限位
        /// 可在 Enabled（点动到边界采集）；摆杆 0°/360°、清空、规整化要求先停止；不可写时按钮禁用。
        /// </summary>
        internal bool IsButtonEnabledForAudit(string buttonName, UiButtonStateSnapshot s)
        {
            bool connected = s.State != EngineState.Disconnected;
            bool fault = s.State == EngineState.Fault || s.EStopLatch;
            bool idle = s.State == EngineState.Disconnected || s.State == EngineState.Connected ||
                        s.State == EngineState.Stopped;
            switch (buttonName)
            {
                case "btnConnect": return !connected && !s.Busy;
                case "btnDisconnect": return connected && !s.Busy;
                case "btnEnable": return idle && !fault && !s.Busy && !s.StopActive;
                case "btnStop": return connected; // 安全方向：故障 / 操作忙时也保留。
                case "btnReset": return connected && fault && !s.Busy;
                case "btnJogLeft":
                case "btnJogRight": return s.State == EngineState.Enabled && !fault && !s.Busy && !s.StopActive;
                // 标定 / 位置采集按钮：只在状态真的允许写入时启用。摆臂零点与左右限位
                // 允许在 Enabled（点动采边界）执行；摆杆 0°/360°、清空、规整化在闭环态先停止。
                case "btnSwingUp": return (s.State == EngineState.Enabled || s.State == EngineState.Stopped) &&
                    !fault && !s.Busy && !s.JogHeld && !s.StopActive;
                case "btnEStop": return true; // 急停不被任何状态、忙标志或遥测质量禁用。
                case "btnSaveParams": return true;  // 保存只写文件，安全方向；任何状态都允许落盘。
                case "btnApplyParams": return true; // 应用只改运行期影子参数，任何状态都允许。
                case "btnLoadParams": return idle && !s.Busy;
                case "btnRestoreDefaults": return idle && !s.Busy && !s.StopActive && !s.CaptureActive;
                case "btnPidTuning": return !s.Busy;
                case "btnSetArmZero":
                case "btnSetLeftLimit":
                case "btnSetRightLimit":
                    return s.State != EngineState.Swinging && s.State != EngineState.Balancing && !s.Busy;
                case "btnSetPendulumZero":
                case "btnSetPendulum360":
                    return s.State != EngineState.Enabled && s.State != EngineState.Swinging &&
                           s.State != EngineState.Balancing && !s.Busy;
                case "btnClearCalibration":
                case "btnNormalizeArmPose":
                case "btnNormalizePendulumPose":
                    return s.State != EngineState.Enabled && s.State != EngineState.Swinging &&
                           s.State != EngineState.Balancing && !s.Busy;
                case "btnReturnHome":
                    return (s.State == EngineState.Enabled || s.State == EngineState.Stopped) && !s.Busy;
                default: return false;
            }
        }

        private void RefreshButtons()
        {
            UiButtonStateSnapshot s = new UiButtonStateSnapshot();
            s.State = _engine.State;
            s.EStopLatch = _lastEStopLatch || _engine.IsEStopLatched;
            s.Busy = Volatile.Read(ref _opBusy) != 0;
            s.StopActive = _engine.IsControlledStopActive;
            s.JogHeld = _jogHeldDirection != 0;
            s.CaptureActive = _engine.IsReleaseWaitActive || _engine.IsCaptureAttemptActive;
            int ignoredRaw;
            s.LiveArm = TryReadLiveArmPosition(out ignoredRaw);

            ApplyButtonEnabled(_connectButton, "btnConnect", s);
            ApplyButtonEnabled(_disconnectButton, "btnDisconnect", s);
            ApplyButtonEnabled(_enableButton, "btnEnable", s);
            ApplyButtonEnabled(_stopButton, "btnStop", s);
            ApplyButtonEnabled(_resetButton, "btnReset", s);
            ApplyButtonEnabled(_jogLeftButton, "btnJogLeft", s);
            ApplyButtonEnabled(_jogRightButton, "btnJogRight", s);
            ApplyButtonEnabled(_swingButton, "btnSwingUp", s);
            ApplyButtonEnabled(_estopButton, "btnEStop", s);
            ApplyButtonEnabled(_saveParamsButton, "btnSaveParams", s);
            ApplyButtonEnabled(_applyParamsButton, "btnApplyParams", s);
            ApplyButtonEnabled(_loadParamsButton, "btnLoadParams", s);
            ApplyButtonEnabled(_restoreDefaultsButton, "btnRestoreDefaults", s);
            ApplyButtonEnabled(_pidDialogButton, "btnPidTuning", s);
            ApplyButtonEnabled(_setArmZeroButton, "btnSetArmZero", s);
            ApplyButtonEnabled(_setLeftLimitButton, "btnSetLeftLimit", s);
            ApplyButtonEnabled(_setRightLimitButton, "btnSetRightLimit", s);
            ApplyButtonEnabled(_setPendulumZeroButton, "btnSetPendulumZero", s);
            ApplyButtonEnabled(_setPendulum360Button, "btnSetPendulum360", s);
            ApplyButtonEnabled(_returnHomeButton, "btnReturnHome", s);
            ApplyButtonEnabled(_clearCalibrationButton, "btnClearCalibration", s);
            ApplyButtonEnabled(_normalizePoseButton, "btnNormalizeArmPose", s);
            ApplyButtonEnabled(_normalizePendulumButton, "btnNormalizePendulumPose", s);
            // 位姿待确认时回零必须真的禁用：开着让点只会把摆臂驱到错误零点。
            // 本次进程未确认持久标定同理 —— 旧零点可能不对应当前物理位置。
            if (_returnHomeButton != null && s.State != EngineState.Disconnected &&
                (_poseReferenceArmMismatch || _poseReferencePendulumMismatch ||
                 _poseSessionReviewRequired ||
                 _engine.CalibrationReviewRequired || _engine.CalibrationCleared ||
                 !_engine.CalibrationReviewChecked))
                _returnHomeButton.Enabled = false;

            bool idleEditable = (s.State == EngineState.Disconnected || s.State == EngineState.Connected ||
                s.State == EngineState.Stopped) && !s.Busy;
            _pendulumZeroCountBox.Enabled = idleEditable;
            _pendulum360CountBox.Enabled = idleEditable;
            _armZeroCountBox.Enabled = !s.Busy; // 0 点可在点动中设置（手输 / 采集都走同一条门）
            _limitMinCountsBox.Enabled = !s.Busy;
            _limitMaxCountsBox.Enabled = !s.Busy;
            // 平衡环已由整套状态反馈接管：Ka / Kad / Kai 只是历史参数，任何状态都不让改。
            // 输入框必须留着 —— 旧参数文件里仍有这三个值，删掉控件会让老用户找不到自己调过的数。
            _kpBox.Enabled = false;
            _kiBox.Enabled = false;
            _kdBox.Enabled = false;
            ApplyPidHistoricalNote();
            bool pumpEditable = !s.Busy && s.State != EngineState.Swinging;
            _keBox.Enabled = pumpEditable;
            _pumpAmpBox.Enabled = pumpEditable;
            _pumpFreqBox.Enabled = pumpEditable;
            _pumpPhaseBox.Enabled = pumpEditable;
        }

        private bool _pidHistoricalNoteApplied;

        /// <summary>
        /// PID 卡的 Ka / Kad / Kai 是历史参数：平衡环已由整套状态反馈接管，这三项不参与平衡。
        /// 卡片标题与输入框提示是在构建界面时写死的，这里在首次刷新时补一条只读说明，
        /// 并把鼠标提示改成同一口径 —— 否则旧提示会继续教用户去调一个调了也不生效的增益。
        /// 只在首次调用生效；提示行的固定前缀同时写回 <c>_pidHintBaseText</c>，
        /// 后续动态刷新（限幅 / 结构判据那几行）会把它保留在最上面。
        /// </summary>
        private void ApplyPidHistoricalNote()
        {
            if (_pidHistoricalNoteApplied)
                return;
            _pidHistoricalNoteApplied = true;

            const string note = "平衡环已由整套状态反馈接管：Ka / Kad / Kai 只作历史参数显示，调整它们不影响平衡。";
            if (_pidHintLabel == null)
                _pidHintLabel = UiControlFinder.FindByName(this, "lblPidHint") as Label;
            if (_pidHintLabel != null)
            {
                if (_pidHintBaseText == null)
                    _pidHintBaseText = _pidHintLabel.Text ?? string.Empty;
                if (_pidHintBaseText.IndexOf(note, StringComparison.Ordinal) < 0)
                    _pidHintBaseText = _pidHintBaseText.Length == 0
                        ? note
                        : note + "\r\n" + _pidHintBaseText;
                UpdatePidLawHint();
            }

            if (_hints != null)
            {
                // 历史值仍随参数文件读写：改不了，但不会丢。
                if (_kpBox != null) _hints.SetToolTip(_kpBox, note + " Ka 的历史值仍会原样写进参数文件。");
                if (_kiBox != null) _hints.SetToolTip(_kiBox, note + " Kad 的历史值仍会原样写进参数文件。");
                if (_kdBox != null) _hints.SetToolTip(_kdBox, note + " Kai 的历史值仍会原样写进参数文件。");
            }
        }

        private void ApplyButtonEnabled(Button button, string buttonName, UiButtonStateSnapshot s)
        {
            bool enabled = IsButtonEnabledForAudit(buttonName, s);
            if (button.Enabled != enabled)
                button.Enabled = enabled;
        }

        /// <summary>状态 / 操作 / 标定阶段变化共用这一条 UI 线程入口；后台线程只 BeginInvoke，不直碰控件。</summary>
        private void RequestButtonsRefresh()
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(RefreshButtons)); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }
            RefreshButtons();
        }

        private void TrackCaptureWindow(EngineState state)
        {
            if (state != EngineState.Swinging)
            {
                _captureLogged = false;
                return;
            }
            // 一次起摆只报一次「进入平衡切换窗口」；离开窗口不重置，避免杆来回穿窗刷屏。
            if (_captureLogged || Math.Abs(_lastThetaDeg) >= CAPTURE_WINDOW_DEG)
                return;
            _captureLogged = true;
            Log("进入捕获窗口：|θ−180°| = " + Math.Abs(_lastThetaDeg).ToString("F1") +
                "° < " + CAPTURE_WINDOW_DEG.ToString("F0") + "°，切换判据连续满足后自动进平衡。");
        }

        // ---------------------------------------------------------------- 引擎事件

        private void OnEngineStateChanged(object sender, EngineState state)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => OnEngineStateChanged(sender, state)));
                    return;
                }
                Log("[状态] 状态机 → [" + FormatState(state, _lastEStopLatch) + "]");
                if (state == EngineState.Disconnected)
                {
                    _poseReferenceChecked = false;
                    _poseReferenceArmMismatch = false;
                    _poseReferencePendulumMismatch = false;
                    _poseRecheckArmZero = false;
                    _poseRecheckArmMin = false;
                    _poseRecheckArmMax = false;
                    _poseRecheckPendulumZero = false;
                    _poseRecheckPendulum360 = false;
                }
                EngineState previous = _lastEngineState;
                _lastEngineState = state;
                RefreshIdleStatus();
                RefreshButtons();
                if (previous == EngineState.Swinging && state == EngineState.Balancing)
                    IdentifyAndApply("起摆结束");
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnEngineFaulted(object sender, FaultInfo info)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => OnEngineFaulted(sender, info)));
                    return;
                }
                string detail = string.IsNullOrEmpty(info.Detail) ? "" : "  " + info.Detail;
                // 无遥测快照时 FaultInfo 里的角度/速度是默认值（NaN），直接乘换算系数会把
                // "NaN°" 打到日志里，看起来像测量值。按 HasTelemetry 如实写 n/a。
                string thetaText = info.HasTelemetry ? (info.ThetaRad * UiStyles.RadToDeg).ToString("F1") + "°" : "n/a";
                string omegaText = info.HasTelemetry
                    ? (info.OmegaRadPerSec * UiStyles.RadToDeg).ToString("F1") + " °/s（原始 " +
                      info.OmegaRadPerSec.ToString("F2") + " rad/s）"
                    : "n/a";
                Log(LogLevel.Error, "[故障] 已锁存 " + DescribeFault(info.Code) +
                    "，来自 " + FormatState(info.FromState, _lastEStopLatch) + "，θ=" + thetaText + "，ω=" +
                    omegaText + "，6041=0x" + info.StatusWord.ToString("X4") +
                    "，603F=0x" + info.ErrorCode.ToString("X4") +
                    "，WKC " + info.ActualWkc + "/" + info.ExpectedWkc + detail);
                if (info.Code == FaultCode.SwingUpTimeout)
                {
                    PendulumTelemetry t;
                    if (_engine.TryReadTelemetry(out t))
                    {
                        Log(LogLevel.Error, "[起摆] 超时快照：θ=" + t.ThetaRad.ToString("F4") +
                            " rad，ψ=" + t.ThetaFromUprightRad.ToString("F4") + " rad，ω=" +
                            t.OmegaRadPerSec.ToString("F3") + " rad/s，能量=" + t.EnergyJ.ToString("F3") +
                            " / 目标 " + t.EnergyTargetJ.ToString("F3") + " J（差 " +
                            t.EnergyDeltaJ.ToString("F3") + "），φ=" + t.PhiRad.ToString("F3") +
                            "，φ_ref=" + t.PhiRefRad.ToString("F3") + "，φ_target=" + t.PhiTargetRad.ToString("F3") +
                            " rad，摆臂 0x6064=" + t.ArmPositionCount + "，摆杆 raw=" + t.PendulumCount +
                            "，阶段 " + t.SwingUpPhase.ToString("F2") + " s，切换倒计时 " + t.SwitchCountdown +
                            "，WKC " + t.ActualWkc + "/" + t.ExpectedWkc +
                            "。逐秒 [SWING]（含 φ̈能量 / φ̈泵实际输出）已写入日志文件。");
                    }
                    else
                    {
                        Log(LogLevel.Error, "[起摆] 超时快照不可读（遥测槽无帧）；逐秒 [SWING] 见日志文件。");
                    }
                }
                SetStatus("已锁存故障：" + info.Code + "。确认机械安全后点「复位」。", UiStyles.AlarmColor);
                RefreshButtons();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// 控制层诊断：<b>显式级别优先</b>（由产生方声明），只有未声明级别时才回落到既有的关键词分类
        /// （保持旧行为，不丢现有日志）。
        /// <para>
        /// 为什么不再只靠关键词：按"看起来像不像错误"决定是否落盘太脆弱 ——
        /// "连接完成，未发现异常" 会因为含"异常"被误判成错误，
        /// "步进 RxPDO 映射实读 = 56 bit" 不含任何关键词就被丢弃。
        /// 连接期一次性取证由产生方标 <see cref="DiagnosticSeverity.Audit"/>，这里必定落盘。
        /// </para>
        /// </summary>
        private void OnEngineDiagnostic(object sender, string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            // 1Hz 遥测心跳是状态镜像，不是事件：任何构建都不进日志。
            if (line.IndexOf("beat seq=", StringComparison.Ordinal) >= 0)
            {
                Debug.WriteLine("[诊断·周期] " + line);
                return;
            }

            // 起摆 1 Hz 证据必须落盘：能量 / 泵实际输出 / 摆臂行程 / θ / ω 都在这一行里。
            // 它是诊断证据，不做 Info 过滤，让文件日志在超时后能直接定位是哪一项没起来。
            if (line.StartsWith("[SWING] ", StringComparison.Ordinal))
            {
                Log(LogLevel.Info, line);
                return;
            }

            // 连接链计时汇总（[TIMING] … TOTAL=…）是台架硬指标取证行：只放行汇总，
            // 逐段打点仍按例行信息处理（只在 Debug 输出），免得每次连接刷十几行。
            if (line.StartsWith("[TIMING] ", StringComparison.Ordinal) &&
                line.IndexOf("TOTAL=", StringComparison.Ordinal) >= 0)
            {
                Log(LogLevel.Info, line);
                return;
            }

            // 2) 显式级别优先：级别由产生方声明，消费方不猜。
            DiagnosticDisposition disposition;
            if (DiagnosticPolicy.TryDecide(PendulumEngine.CurrentDiagnosticSeverity, out disposition))
            {
                switch (disposition)
                {
                    case DiagnosticDisposition.DebugOnly:
                        Debug.WriteLine("[诊断] " + line);
                        return;
                    case DiagnosticDisposition.PersistInfo:
                        // Audit（连接期取证）：一次连接只一两条，必落盘且不做 10 秒重复抑制 ——
                        // 连续重连时每一次的映射实读都要留下证据。
                        Log(LogLevel.Info, "[诊断] " + line);
                        return;
                    case DiagnosticDisposition.PersistWarn:
                        if (IsRepeatedDiagnostic(line)) return;
                        Log(LogLevel.Warn, "[诊断] " + line);
                        return;
                    case DiagnosticDisposition.PersistError:
                        if (IsRepeatedDiagnostic(line)) return;
                        Log(LogLevel.Error, "[诊断] " + line);
                        return;
                }
            }

            // 3) 未声明级别 → 既有兜底分类（行为与改前一致）。
            LogLevel level = ClassifyDiagnostic(line);
            if (level == LogLevel.Info)
            {
                // 例行连接 / 请求确认等一次性信息：界面按钮日志已覆盖同样的动作，
                // 这里只在 Debug 构建输出，Release 不产生日志行。
                Debug.WriteLine("[诊断] " + line);
                return;
            }

            if (IsRepeatedDiagnostic(line))
                return;
            Log(level, "[诊断] " + line);
        }

        /// <summary>诊断行分级：错误 / 警告保留，例行信息返回 Info 由调用方丢弃。</summary>
        private static LogLevel ClassifyDiagnostic(string line)
        {
            LogLevel level = GuessLevel(line);
            if (level != LogLevel.Info)
                return level;
            if (line.IndexOf("FAULT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("[SAFETY]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("锁存", StringComparison.Ordinal) >= 0)
                return LogLevel.Error;
            if (line.IndexOf("掉线", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("丢帧", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("未响应", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("未配置", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("没配置", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("旁观从站", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("IsLost=True", StringComparison.Ordinal) >= 0)
                return LogLevel.Warn;
            return LogLevel.Info;
        }

        /// <summary>同一诊断行 10 秒内重复返回 true（首条保留）。Connect 等工作线程也会回调，必须加锁。</summary>
        private bool IsRepeatedDiagnostic(string line)
        {
            long now = Stopwatch.GetTimestamp();
            long window = (long)(Stopwatch.Frequency * DIAGNOSTIC_REPEAT_SUPPRESS_SEC);
            lock (_diagnosticGate)
            {
                long last;
                if (_diagnosticLastTicks.TryGetValue(line, out last) && now - last < window)
                    return true;
                if (_diagnosticLastTicks.Count >= 512)
                    _diagnosticLastTicks.Clear();
                _diagnosticLastTicks[line] = now;
                return false;
            }
        }

        private void RefreshIdleStatus()
        {
            EngineState state = _engine.State;
            // 急停锁存以引擎实时字段为准：复位（Stop）清锁存后状态机立刻回「已停」，
            // 这时上一帧遥测里的 EStopLatch 可能还是 true，不能拿它把状态行永久钉在「已急停」。
            if (_engine.IsEStopLatched)
            {
                SetStatus("已急停：u=0，步进已断使能。确认机械安全后点「复位」或「断开」。", UiStyles.AlarmColor);
                return;
            }
            switch (state)
            {
                case EngineState.Disconnected:
                    SetStatus("未连接。");
                    break;
                case EngineState.Connected:
                    SetStatus(string.Equals(_engine.BusStateName, "OP", StringComparison.Ordinal)
                        ? (_engine.OpEntryMode == OpEntryMode.DirectFromPreOp
                            ? "已连接 OP(直连)（未使能；非标准进 OP 路径）。"
                            : "已连接 OP（未使能）。")
                        : "已连接 SafeOp（未进 OP）。");
                    break;
                case EngineState.Enabled:
                    SetStatus("已使能（就绪）。");
                    break;
                case EngineState.Swinging:
                    SetStatus("起摆中。");
                    break;
                case EngineState.Balancing:
                    SetStatus("平衡中。");
                    break;
                case EngineState.Stopped:
                    SetStatus("已受控停并去使能。");
                    break;
                case EngineState.Fault:
                    SetStatus("已锁存故障。确认机械安全后点「复位」或「断开」。", UiStyles.AlarmColor);
                    break;
                default:
                    SetStatus(FormatState(state, _lastEStopLatch));
                    break;
            }
        }

        internal static string FormatState(EngineState state, bool estopLatch)
        {
            if (state == EngineState.Fault && estopLatch)
                return "急停锁存";
            switch (state)
            {
                case EngineState.Disconnected: return "未连接";
                case EngineState.Connected: return "已连接";
                case EngineState.Enabled: return "就绪";
                case EngineState.Swinging: return "起摆中";
                case EngineState.Balancing: return "平衡中";
                case EngineState.Stopped: return "已停";
                case EngineState.Fault: return "故障";
                default: return "未知";
            }
        }

        /// <summary>故障码转中文短语并保留原名（排查要能看到原始码）。</summary>
        private static string DescribeFault(FaultCode code)
        {
            string name;
            switch (code)
            {
                case FaultCode.None: return "无故障";
                case FaultCode.ThetaOverrun: name = "杆角越界"; break;
                case FaultCode.FollowingError: name = "跟随误差超限"; break;
                case FaultCode.EnableLost: name = "使能丢失"; break;
                case FaultCode.DriveFault: name = "驱动器故障"; break;
                case FaultCode.WkcInvalid: name = "WKC 不符"; break;
                case FaultCode.PdoFrameLoss: name = "PDO 丢帧"; break;
                case FaultCode.EtherCatState: name = "EtherCAT 状态异常"; break;
                case FaultCode.CallbackException: name = "回调异常"; break;
                case FaultCode.TimingInvalid: name = "周期时序无效"; break;
                case FaultCode.EStopInput: name = "硬急停输入"; break;
                case FaultCode.SwingUpTimeout: name = "起摆超时"; break;
                case FaultCode.BalanceLost: name = "平衡失稳"; break;
                case FaultCode.IdentityMismatch: name = "设备身份不符"; break;
                case FaultCode.ConfigInvalid: name = "配置不合法"; break;
                case FaultCode.StopSequenceTimeout: name = "受控停超时"; break;
                case FaultCode.TeardownTimeout: name = "断开超时"; break;
                case FaultCode.UserStop: name = "用户停止"; break;
                case FaultCode.SoftLimit: name = "软限位越界"; break;
                default: name = "未知故障"; break;
            }
            return name + "（" + code + "）";
        }

        /// <summary>异常转一句中文 + 原始信息：界面日志不把英文异常类型名直接甩给操作者。</summary>
        private static string DescribeException(Exception ex)
        {
            if (ex == null)
                return "未知异常";
            string name;
            switch (ex.GetType().Name)
            {
                case "NullReferenceException": name = "空引用"; break;
                case "InvalidOperationException": name = "操作状态不合法"; break;
                case "ObjectDisposedException": name = "对象已释放"; break;
                case "TimeoutException": name = "等待超时"; break;
                case "IOException": name = "读写失败"; break;
                case "UnauthorizedAccessException": name = "权限不足"; break;
                case "FormatException": name = "格式错误"; break;
                case "ArgumentNullException": name = "缺少参数"; break;
                case "ArgumentException": name = "参数不合法"; break;
                case "InvalidCastException": name = "类型转换失败"; break;
                case "OutOfMemoryException": name = "内存不足"; break;
                default: name = "异常 " + ex.GetType().Name; break;
            }
            return string.IsNullOrEmpty(ex.Message) ? name : name + "：" + ex.Message;
        }

        private void SetStatus(string text, Color? color = null)
        {
            if (_statusMessageLabel == null || string.IsNullOrEmpty(text))
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SetStatus(text, color))); }
                catch (ObjectDisposedException) { }
                return;
            }
            if (string.Equals(_lastStatusText, text, StringComparison.Ordinal) && color == null)
                return;
            _lastStatusText = text;
            Color fore = color ?? UiStyles.StatusText;
            _statusMessageLabel.Text = text;
            _statusMessageLabel.ForeColor = fore;
            if (_statusMessageLabel.BackColor != Color.Transparent)
                _statusMessageLabel.BackColor = Color.Transparent;
        }

        // ---------------------------------------------------------------- 日志

        private static DiagnosticLogWriter CreateFileLog()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DarraPLC", "logs");
                string path = System.IO.Path.Combine(dir, "furuta_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                DiagnosticLogWriter writer = new DiagnosticLogWriter(path);
                if (string.IsNullOrEmpty(writer.CurrentFilePath))
                {
                    writer.Dispose();
                    return null;
                }
                return writer;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("日志落盘通道创建失败：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        private void LogReject(string action, string category, string detail, FaultCode? faultCode)
        {
            StringBuilder line = new StringBuilder();
            line.Append("[拒绝] 动作=").Append(action)
                .Append(" 引擎状态=").Append(FormatState(_engine.State, _lastEStopLatch))
                .Append(" 拒绝原因=").Append(category);
            if (faultCode.HasValue)
                line.Append(" 故障码=").Append(DescribeFault(faultCode.Value));
            if (!string.IsNullOrEmpty(detail))
                line.Append(" 详情=").Append(detail);
            Log(LogLevel.Warn, line.ToString());
        }

        private FaultCode CurrentFaultCode()
        {
            PendulumTelemetry telemetry;
            return TryReadTelemetry(out telemetry) ? telemetry.FaultCode : FaultCode.None;
        }

        private string ClassifyEngineReject(FaultCode code)
        {
            if (_lastEStopLatch || _engine.IsEStopLatched || _engine.IsControlledStopActive || code != FaultCode.None)
                return REJECT_INTERLOCK;
            return REJECT_OTHER;
        }

        private void Log(string message)
        {
            Log(GuessLevel(message), message);
        }

        private void Log(LogLevel level, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message;
            DiagnosticLogWriter fileLog = _fileLog;
            if (fileLog != null)
            {
                try
                {
                    // 落盘用统一行格式（与 CLI 同一个根、同一套命名、同一种行）：[时间] [kind] 正文。
                    // 界面/UI 队列仍用上面的 line，显示口径不变。
                    fileLog.Write(KindOf(level), message);
                }
                catch (ObjectDisposedException)
                {
                }
            }
            string uiLine = line.Length <= UI_LOG_LINE_LIMIT
                ? line
                : line.Substring(0, UI_LOG_LINE_LIMIT - UI_LOG_TRUNCATION_SUFFIX.Length) + UI_LOG_TRUNCATION_SUFFIX;
            LogEntry entry = new LogEntry();
            entry.Text = uiLine;
            entry.Level = level;
            if (Interlocked.Increment(ref _queuedUiLogLines) <= UI_LOG_QUEUE_LIMIT)
            {
                _logQueue.Enqueue(entry);
            }
            else
            {
                Interlocked.Decrement(ref _queuedUiLogLines);
                Interlocked.Increment(ref _droppedUiLogLines);
            }
        }

        /// <summary>
        /// 落盘行的 kind（与 CLI 记录里的 kind 同一字段语义）：GUI 侧按级别分 info / warn / error。
        /// 统一行格式由 <see cref="DiagnosticLogWriter.Write(string, string)"/> 唯一实现。
        /// </summary>
        private static string KindOf(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return "error";
                case LogLevel.Warn: return "warn";
                default: return "info";
            }
        }

        private static LogLevel GuessLevel(string message)
        {
            if (string.IsNullOrEmpty(message))
                return LogLevel.Info;
            if (message.IndexOf("故障", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("异常", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("失败", StringComparison.Ordinal) >= 0)
                return LogLevel.Error;
            if (message.IndexOf("不合法", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("无效", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("拒绝", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("被拒", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("未写入", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("超时", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("不可用", StringComparison.Ordinal) >= 0)
                return LogLevel.Warn;
            return LogLevel.Info;
        }

        private static Color LogColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return UiStyles.AlarmColor;
                case LogLevel.Warn: return UiStyles.WarnOrange;
                default: return UiStyles.CaptionText;
            }
        }

        private void FlushLogs()
        {
            if (_logQueue.IsEmpty || _logBox == null || _logBox.IsDisposed)
                return;
            List<LogEntry> entries = new List<LogEntry>(UI_LOG_BATCH_LIMIT + 1);
            int appendLength = 0;
            LogEntry entry;
            while (entries.Count < UI_LOG_BATCH_LIMIT && _logQueue.TryDequeue(out entry))
            {
                Interlocked.Decrement(ref _queuedUiLogLines);
                entries.Add(entry);
                appendLength += entry.Text.Length + 2;
            }
            if (_droppedUiLogLines > 0)
            {
                long dropped = Interlocked.Exchange(ref _droppedUiLogLines, 0);
                if (dropped > 0)
                {
                    LogEntry notice = new LogEntry();
                    notice.Text = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [系统] 日志队列已满，丢弃 " + dropped + " 行";
                    notice.Level = LogLevel.Warn;
                    entries.Add(notice);
                    appendLength += notice.Text.Length + 2;
                }
            }
            if (entries.Count == 0)
                return;

            int currentLength = _logBox.TextLength;
            int requiredRemoval = currentLength + appendLength - UI_LOG_TEXT_LIMIT;
            if (requiredRemoval > 0 && currentLength > 0)
            {
                int targetRemoval = Math.Max(requiredRemoval, currentLength - UI_LOG_TEXT_TARGET);
                targetRemoval = Math.Min(currentLength, targetRemoval);
                int lineIndex = _logBox.GetLineFromCharIndex(Math.Max(0, targetRemoval - 1));
                int cut = _logBox.GetFirstCharIndexFromLine(lineIndex + 1);
                if (cut <= 0 || cut > currentLength)
                    cut = targetRemoval;
                _logBox.Select(0, cut);
                _logBox.SelectedText = string.Empty;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.SelectionLength = 0;
                _logBox.SelectionColor = LogColor(entries[i].Level);
                _logBox.AppendText(entries[i].Text + "\r\n");
            }
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        // ---------------------------------------------------------------- 退出

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopUiAutomation();
            if (_paramApplyTimer != null)
            {
                _paramApplyTimer.Stop();
                _paramApplyTimer.Tick -= OnParamApplyTick;
                _paramApplyTimer.Dispose();
                _paramApplyTimer = null;
            }
            if (_dialTimer != null)
            {
                _dialTimer.Stop();
                _dialTimer.Tick -= OnDialTimerTick;
                _dialTimer.Dispose();
                _dialTimer = null;
            }
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Tick -= OnUiTimerTick;
                _uiTimer.Dispose();
                _uiTimer = null;
            }
            _engine.StateChanged -= OnEngineStateChanged;
            _engine.Faulted -= OnEngineFaulted;
            _engine.Diagnostic -= OnEngineDiagnostic;
            DiagnosticLogWriter fileLog = _fileLog;
            _fileLog = null;
            if (fileLog != null)
            {
                try
                {
                    fileLog.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("日志落盘通道关闭异常：" + ex.GetType().Name + " " + ex.Message);
                }
            }
            if (_hints != null)
            {
                _hints.Dispose();
                _hints = null;
            }
            base.OnFormClosed(e);
        }
    }
}
