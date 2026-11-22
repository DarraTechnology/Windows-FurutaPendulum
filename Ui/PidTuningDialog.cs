using System;
using System.Drawing;
using System.Windows.Forms;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 控制参数对话框：内环 φ 三增益 + 输出限幅两项 + 起摆 / 切换 / 捕获七项 + 摆臂角速度限速一项。
    /// 平衡环已由整套状态反馈接管：外环 Ka / Kad / Kai / Kφ / Kφ̇ 只作历史参数显示，
    /// 平衡外环不再需要（也不会）调参；泵（k_e / 幅度 / 频率 / 相位）仍只在主界面「PID 参数」卡上改，
    /// 本对话框不重复、不覆盖。
    /// <para>
    /// 点「确定」把三组参数（PidGains / SwingUpParams / SafetyLimits）经控制层**同一个** generation 双缓冲通道
    /// 立即下发（下一拍顶换），不写文件；落盘仍由左栏「保存参数」负责。**起摆参数**按 CLI `setswingup` 的
    /// 状态门（<see cref="SwingParamGate"/>）：Disconnected / Enabled / Stopped 整组可改；Swinging 只放行
    /// CLI 热改项（捕获臂速上限 / 抽取增益 / 软中心带）；Connected / Balancing / Fault 整组不写入。
    /// 内环 / 输出限幅 / 安全阈值不属于起摆参数，任何状态都可改（与 CLI setpid / setlimits 一致）。
    /// </para>
    /// <para>
    /// 「自动调参…」是另一个对话框（<see cref="AutoTuneDialog"/>），它现在只整定起摆泵：
    /// 平衡外环已由固定状态反馈接管，外环增益不再整定。
    /// 自动调参一关闭本对话框就按取消退出，避免用打开时的旧值覆盖整定结果。
    /// </para>
    /// </summary>
    internal sealed class PidTuningDialog : Form
    {
        private readonly PidGains _pidSource;
        private readonly SwingUpParams _swingSource;
        private readonly SafetyLimits _limitsSource;
        private readonly IAutoTuneHost _host;
        private NumericUpDown _kpPhiBox;
        private NumericUpDown _kiPhiBox;
        private NumericUpDown _kdPhiBox;
        private NumericUpDown _kPhiBox;
        private NumericUpDown _kPhiDotBox;
        private NumericUpDown _accMaxBox;
        private NumericUpDown _armRateMaxBox;
        private NumericUpDown _energyGainBox;
        private NumericUpDown _aMaxBox;
        private NumericUpDown _thetaSwitchBox;
        private NumericUpDown _omegaSwitchBox;
        private NumericUpDown _captureArmRateBox;
        private NumericUpDown _swingKPhiBox;
        private NumericUpDown _swingKPhiDotBox;
        private NumericUpDown _omegaArmMaxBox;
        private NumericUpDown _extractGainBox;
        private NumericUpDown _centerBandRadBox;
        private Label _errorLabel;

        internal PidTuningDialog(PidGains gains, SwingUpParams swing, SafetyLimits limits,
            SwingParamAccess swingAccess, string swingAccessReason, IAutoTuneHost host)
        {
            _pidSource = gains;
            _swingSource = swing;
            _limitsSource = limits;
            _host = host;

            Text = "控制参数（内环 / 输出限幅 / 起摆切换 / 安全）";
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = UiStyles.FormBack;
            ClientSize = new Size(760, 450);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = UiStyles.FormBack
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 290f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            Controls.Add(root);

            TableLayoutPanel fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 5,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            for (int i = 0; i < 4; i++)
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            for (int i = 0; i < 5; i++)
                fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));

            _kpPhiBox = MakeBox(gains.KpPhi, 0m, 10000m, 5, "numPidKpPhi");
            _kiPhiBox = MakeBox(gains.KiPhi, 0m, 10000m, 5, "numPidKiPhi");
            _kdPhiBox = MakeBox(gains.KdPhi, 0m, 10000m, 5, "numPidKdPhi");
            _kPhiBox = MakeBox(gains.KPhi, 0.01m, 1000m, 3, "numPidKphi");
            _kPhiDotBox = MakeBox(gains.KPhiDot, 0.01m, 1000m, 3, "numPidKphiDot");
            // 平衡环由固定状态反馈接管后，平衡 Kφ / Kφ̇ 不再参与控制：保留只读显示，
            // 旧参数文件里的值仍有地方看，但不再给用户一个调了也不生效的输入口。
            _kPhiBox.Enabled = false;
            _kPhiDotBox.Enabled = false;
            _accMaxBox = MakeBox(gains.AccMax, 1m, 10000m, 1, "numPidAccMax");
            _armRateMaxBox = MakeBox(gains.ArmRateMax, 0.1m, 100m, 2, "numPidArmRateMax");
            _energyGainBox = MakeBox(swing.TargetEnergyGain,
                (decimal)SwingUpParams.MinTargetEnergyGain, (decimal)SwingUpParams.MaxTargetEnergyGain, 4,
                "numSwingEnergyGain");
            _aMaxBox = MakeBox(swing.AMax, 0.1m, 500m, 2, "numSwingAMax");
            // 切换角控制层用 rad，界面按度显示（与主界面角度卡同口径）；写回时再折回 rad。
            _thetaSwitchBox = MakeBox(swing.ThetaSwitchRad * 180.0 / Math.PI,
                0.5m, (decimal)(SwingUpParams.VerifiedRecoverableThetaRad * 180.0 / Math.PI), 3,
                "numSwingThetaSwitchDeg");
            _omegaSwitchBox = MakeBox(swing.OmegaSwitchRadPerSec, 0.1m, 20m, 2, "numSwingOmegaSwitch");
            _captureArmRateBox = MakeBox(swing.CaptureArmRateLimitRadPerSec, 0.5m, 20m, 2,
                "numSwingCaptureArmRate");
            // 起摆律自己的回中 / 阻尼增益（与平衡律的 Kφ / Kφ̇ 同名不同物，标签写清「起摆」）。
            _swingKPhiBox = MakeBox(swing.KPhi, 0m, 1000m, 3, "numSwingKphi");
            _swingKPhiDotBox = MakeBox(swing.KPhiDot, 0m, 1000m, 3, "numSwingKphiDot");
            _omegaArmMaxBox = MakeBox(limits.OmegaArmMaxRadPerSec, 0.5m, 100m, 2, "numSafetyOmegaArmMax");
            // 能量抽取（刹车）增益：与泵注入 k_e 解耦，0 = 控制层按内置 60 补齐。
            // 该通道决定「超过 E* 后按超调量退让」的力度，起摆中也允许热改（CLI 同一口径）。
            _extractGainBox = MakeBox(swing.ExtractGain, 0m, 10000m, 4, "numSwingExtractGain");
            // 软中心带半径 [rad]：|φ| 超出它就暂停注入、满 AMax 回中；出厂默认 20，起摆中允许热改。
            _centerBandRadBox = MakeBox(swing.CenterBandRad, 0.1m, 100m, 2, "numSwingCenterBandRad");

            // 起摆参数的状态门与 CLI `setswingup` 完全同一套（见 SwingParamGate，唯一来源
            // Cli/CliSession.Commands.SetSwingUpChangeRejectText）：
            //   Connected / Balancing / Fault → 整组锁定，确定时整组不写入；
            //   Swinging → 只放行 CLI 的热改项（捕获臂速上限 / 抽取增益 / 软中心带）；
            //   Disconnected / Enabled / Stopped → 整组可改。
            // 内环 / 输出限幅 / 安全阈值不属于 setswingup，任何状态都可改（与 CLI setpid / setlimits 一致）。
            bool swingEditable = swingAccess == SwingParamAccess.Editable;
            bool swingLocked = swingAccess == SwingParamAccess.Locked;
            _energyGainBox.Enabled = swingEditable;
            _aMaxBox.Enabled = swingEditable;
            _thetaSwitchBox.Enabled = swingEditable;
            _omegaSwitchBox.Enabled = swingEditable;
            _swingKPhiBox.Enabled = swingEditable;
            _swingKPhiDotBox.Enabled = swingEditable;
            // CLI 热改集合（--capture-arm-rate / --extract-gain / --center-band）：起摆中仍可改。
            _captureArmRateBox.Enabled = swingEditable || swingAccess == SwingParamAccess.HotEditOnly;
            _extractGainBox.Enabled = !swingLocked;
            _centerBandRadBox.Enabled = !swingLocked;

            fields.Controls.Add(UiStyles.MakeLabeledField("内环 Kpφ（关节角）", _kpPhiBox), 0, 0);
            fields.Controls.Add(UiStyles.MakeLabeledField("内环 Kiφ（积分）", _kiPhiBox), 1, 0);
            fields.Controls.Add(UiStyles.MakeLabeledField("内环 Kdφ（角速度）", _kdPhiBox), 2, 0);
            fields.Controls.Add(UiStyles.MakeLabeledField("平衡 Kφ（历史·不生效）", _kPhiBox), 3, 0);
            fields.Controls.Add(UiStyles.MakeLabeledField("平衡 Kφ̇（历史·不生效）", _kPhiDotBox), 0, 1);
            fields.Controls.Add(UiStyles.MakeLabeledField("加速度上限 AccMax（rad/s²）", _accMaxBox), 1, 1);
            fields.Controls.Add(UiStyles.MakeLabeledField("参考速度上限 ArmRateMax（rad/s）", _armRateMaxBox), 2, 1);
            fields.Controls.Add(UiStyles.MakeLabeledField("能量增益 ε", _energyGainBox), 3, 1);
            fields.Controls.Add(UiStyles.MakeLabeledField("起摆加速度 AMax（rad/s²）", _aMaxBox), 0, 2);
            fields.Controls.Add(UiStyles.MakeLabeledField("切换角 θ（°）", _thetaSwitchBox), 1, 2);
            fields.Controls.Add(UiStyles.MakeLabeledField("切换角速度 ω（rad/s）", _omegaSwitchBox), 2, 2);
            fields.Controls.Add(UiStyles.MakeLabeledField("捕获臂速上限（rad/s）", _captureArmRateBox), 3, 2);
            fields.Controls.Add(UiStyles.MakeLabeledField("摆臂角速度上限 ω_arm（rad/s）", _omegaArmMaxBox), 0, 3);
            fields.Controls.Add(UiStyles.MakeLabeledField("起摆回中 Kφ（1/s²）", _swingKPhiBox), 1, 3);
            fields.Controls.Add(UiStyles.MakeLabeledField("起摆阻尼 Kφ̇（1/s）", _swingKPhiDotBox), 2, 3);
            fields.Controls.Add(UiStyles.MakeLabeledField("抽取增益 ExtractGain（能量整形出力上限，默认 60）", _extractGainBox), 0, 4);
            fields.Controls.Add(UiStyles.MakeLabeledField("软中心带 CenterBandRad（rad，默认 20）", _centerBandRadBox), 1, 4);
            root.Controls.Add(fields, 0, 0);

            _errorLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = (swingAccess == SwingParamAccess.Editable
                    ? "点「确定」立即下发控制层（下一拍顶换），不写文件；落盘用左栏「保存参数」。"
                        + "切换角按度显示，写回控制层时折成 rad。"
                    : (swingAccessReason ?? string.Empty) +
                        (swingAccess == SwingParamAccess.HotEditOnly
                            ? " 点「确定」立即下发控制层（下一拍顶换），不写文件；内环 / 输出限幅 / 安全阈值仍可改。"
                            : " 本对话框的「确定」只下发内环 / 输出限幅 / 安全阈值；起摆参数整组不写入。"))
                    + " 平衡环由整套状态反馈接管：Ka / Kad / Kai / Kφ / Kφ̇ 只作历史参数显示，调整不生效。",
                Font = UiStyles.UiFont(8.5f),
                ForeColor = swingAccess == SwingParamAccess.Editable ? UiStyles.CaptionText : UiStyles.WarnOrange,
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false,
                AutoEllipsis = false,
                BackColor = Color.Transparent,
                Margin = new Padding(4, 6, 4, 0)
            };
            root.Controls.Add(_errorLabel, 0, 1);

            // 自动调参入口：整定起摆泵；平衡外环由固定状态反馈接管、不再整定，与下面三个内环手动框互不覆盖。
            Button autoTune = UiStyles.MakeButton("自动调参…", Color.Empty, AutoTuneClicked, Color.Empty);
            autoTune.AccessibleName = "btnAutoTune";
            autoTune.Margin = new Padding(80, 4, 80, 2);
            root.Controls.Add(autoTune, 0, 2);

            TableLayoutPanel buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Button ok = UiStyles.MakePrimaryButton("确定", OkClicked, 9f);
            ok.AccessibleName = "btnPidOk";
            ok.Margin = new Padding(80, 4, 8, 2);
            Button cancel = UiStyles.MakeButton("取消", Color.Empty, CancelClicked, Color.Empty, 8.5f);
            cancel.AccessibleName = "btnPidCancel";
            cancel.Margin = new Padding(8, 4, 80, 2);
            buttons.Controls.Add(ok, 0, 0);
            buttons.Controls.Add(cancel, 1, 0);
            root.Controls.Add(buttons, 0, 3);
        }

        private static NumericUpDown MakeBox(double value, decimal min, decimal max, int decimals, string accessibleName)
        {
            // Kdφ 默认 0.0009、能量裕量 ε 默认 -0.002：小数位必须按字段给足，
            // 否则框里会把值取整（0.0009 → 0.001、-0.002 → 0.000），点「确定」按取整值写回控制层。
            NumericUpDown box = UiStyles.MakeNumber((decimal)value, min, max, decimals);
            box.AccessibleName = accessibleName;
            return box;
        }

        /// <summary>
        /// 取三组参数并逐组校验。外环 Ka / Kad / Kai / Kφ / Kφ̇ 是历史参数，按构造时传入的原值原样沿用
        /// （对话框里没有可编辑入口，也不会清零）；泵四项与公共项（微分低通 / 输出限幅 / 积分限幅 / 死区）
        /// 同样沿用原值；切换角按度输入、写回控制层折成 rad。
        /// </summary>
        public bool TryGetParams(out PidGains gains, out SwingUpParams swing, out SafetyLimits limits,
            out string error)
        {
            gains = _pidSource;
            gains.KpPhi = (double)_kpPhiBox.Value;
            gains.KiPhi = (double)_kiPhiBox.Value;
            gains.KdPhi = (double)_kdPhiBox.Value;
            gains.KPhi = (double)_kPhiBox.Value;
            gains.KPhiDot = (double)_kPhiDotBox.Value;
            gains.AccMax = (double)_accMaxBox.Value;
            gains.ArmRateMax = (double)_armRateMaxBox.Value;

            swing = _swingSource;
            swing.TargetEnergyGain = (double)_energyGainBox.Value;
            swing.AMax = (double)_aMaxBox.Value;
            swing.ThetaSwitchRad = (double)_thetaSwitchBox.Value * Math.PI / 180.0;
            swing.OmegaSwitchRadPerSec = (double)_omegaSwitchBox.Value;
            swing.CaptureArmRateLimitRadPerSec = (double)_captureArmRateBox.Value;
            swing.KPhi = (double)_swingKPhiBox.Value;
            swing.KPhiDot = (double)_swingKPhiDotBox.Value;
            swing.ExtractGain = (double)_extractGainBox.Value;
            swing.CenterBandRad = (double)_centerBandRadBox.Value;

            limits = _limitsSource;
            limits.OmegaArmMaxRadPerSec = (double)_omegaArmMaxBox.Value;

            string reason;
            if (!gains.IsValid(out reason))
            {
                error = "平衡参数：" + reason;
                return false;
            }
            if (!swing.IsValid(out reason))
            {
                error = "起摆参数：" + reason;
                return false;
            }
            if (!limits.IsValid(out reason))
            {
                error = "安全阈值：" + reason;
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// 打开自动调参。整定完成后候选参数已经写进控制层与界面影子，而本对话框里的三个手动框还是
        /// 打开时的旧值 —— 继续「确定」会把旧的内环增益整组写回，覆盖自动调参结果。
        /// 因此自动调参一关闭，本对话框一律按取消退出，让外层不要回写任何手动值。
        /// </summary>
        private void AutoTuneClicked(object sender, EventArgs e)
        {
            if (_host == null)
            {
                _errorLabel.ForeColor = UiStyles.AlarmColor;
                _errorLabel.Text = "主界面没有提供自动调参入口，无法打开。";
                return;
            }

            try
            {
                using (AutoTuneDialog dialog = new AutoTuneDialog(_host.Engine, _host.CurrentPid, _host.CurrentSwing, _host))
                {
                    dialog.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                // 意外的构造 / 显示失败留在本对话框里给出中文原因，不把一个异常抛回主界面。
                _errorLabel.ForeColor = UiStyles.AlarmColor;
                _errorLabel.Text = "自动调参异常：" + ex.GetType().Name + " " + ex.Message;
                return;
            }

            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void OkClicked(object sender, EventArgs e)
        {
            PidGains gains;
            SwingUpParams swing;
            SafetyLimits limits;
            string error;
            if (!TryGetParams(out gains, out swing, out limits, out error))
            {
                _errorLabel.ForeColor = UiStyles.AlarmColor;
                _errorLabel.Text = "参数无效：" + error;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CancelClicked(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
