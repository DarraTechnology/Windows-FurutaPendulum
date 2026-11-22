using System;
using System.Drawing;
using System.Windows.Forms;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 主界面布局（partial）：左栏 210px（五张操作卡 + 操作提示），右侧单页三行八张卡片
    /// （PDO 实时数据 / 总线与采样 / 当前状态 / 杆角度（左列跨两行）/ PID 参数 / 设备参数·软限位 / 角度读数 / 运行日志）。
    /// 全用百分比行列：窗口拉大时各区块等比跟着长；一行代码都不碰控制层，控件只由 MainForm 的刷新方法喂值。
    /// </summary>
    public sealed partial class MainForm : Form
    {
        private const int LEFT_RAIL_WIDTH = 210;

        // 卡片外边距 6px（相邻卡之间 12px）、按钮高度 26px、行距 6px，全部由设计规格钉死。
        private const int BUTTON_ROW_HEIGHT = 32;   // 26px 按钮 + 上下各 3px 间距

        // ---- 窗口尺寸契约 ----
        // 设计稿客户区 1280×1000 是“一屏全见”的默认尺寸；窗口可以拉到更小（工程笔记本常见
        // 1366×768 / 1024×768），此时左右两栏各自在滚动容器里保持设计高度，按钮 / 输入框不裁切。
        // 960×640 是仍能正常阅读与点击的下限；再小没有工程意义，不做支持承诺。
        internal const int MIN_CLIENT_WIDTH = 960;
        internal const int MIN_CLIENT_HEIGHT = 640;

        // 左栏五张操作卡 + 操作提示在设计稿下的总高（168+174+196+188+116+134）。
        // 可视高度小于它时左栏出现纵向滚动条，卡片本身不压缩、按钮保持 26px 高。
        private const int MIN_LEFT_COLUMN_HEIGHT = 976;

        // 右侧单页在设计稿下的最小内容尺寸：行高由各卡固定行数决定，小于它就滚动而不是把
        // 卡片压扁（压扁会把 PDO / PID / 设备卡的固定行裁掉）。680 = 960 最小客户区减去
        // 左栏 227（含滚动条抵消）与根内边距 24 后，扣掉纵向滚动条仍放得下三列。
        private const int MIN_MAIN_PAGE_WIDTH = 680;
        private const int MIN_MAIN_PAGE_HEIGHT = 976;

        private TableLayoutPanel _rootLayout;
        private Panel _leftRailHost;
        private System.Windows.Forms.Control _leftColumnContent;

        // ---------------------------------------------------------------- 根布局

        private void BuildRoot()
        {
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12, 12, 12, 12),
                BackColor = UiStyles.FormBack
            };
            root.AccessibleName = "rootLayout";
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LEFT_RAIL_WIDTH));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);
            _rootLayout = root;

            // 两栏都放进 AutoScroll 宿主：窗口小于设计稿时滚动，而不是把卡片压扁 / 裁切。
            // 1280×1000 下内容高度正好等于可视高度，不出现滚动条，外观与原来的 Dock=Fill 完全一致。
            System.Windows.Forms.Control leftColumn = BuildLeftColumn();
            _leftColumnContent = leftColumn;
            _leftRailHost = MakeScrollHost("leftRailScroll", leftColumn, 0, MIN_LEFT_COLUMN_HEIGHT);
            root.Controls.Add(_leftRailHost, 0, 0);

            System.Windows.Forms.Control mainPage = BuildMainPage();
            root.Controls.Add(MakeScrollHost("mainPageScroll", mainPage, MIN_MAIN_PAGE_WIDTH, MIN_MAIN_PAGE_HEIGHT), 1, 0);
        }

        /// <summary>
        /// 通用纵向滚动宿主：内容 Dock=Top，高度 = max(设计高度, 可视高度)。
        /// 内容宽度跟随宿主显示区（已扣除纵向滚动条），主视野另有最小宽度，窗口被压得极窄时横向滚动。
        /// 只负责容器尺寸，不碰卡片内部布局与配色。
        /// 实例方法：尺寸同步要回写本窗体的左栏列宽（见 <see cref="UpdateLeftRailWidth"/>）。
        /// </summary>
        private Panel MakeScrollHost(string accessibleName, System.Windows.Forms.Control content,
            int minWidth, int minHeight)
        {
            Panel host = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = UiStyles.FormBack,
                AutoScroll = true
            };
            host.AccessibleName = accessibleName;
            host.Name = accessibleName;
            content.Dock = DockStyle.Top;
            content.Margin = new Padding(0);
            host.Controls.Add(content);
            host.ClientSizeChanged += delegate { SyncScrollContent(host, content, minWidth, minHeight); };
            SyncScrollContent(host, content, minWidth, minHeight);
            return host;
        }

        /// <summary>
        /// 同步滚动宿主与内容尺寸：内容至少保持设计高度，宿主更大时跟随变高（表盘 / 日志随之等比变大）。
        /// 内容宽度用 DisplayRectangle（已扣掉纵向滚动条），避免“纵向滚动条出现后横向又冒一条”。
        /// </summary>
        private void SyncScrollContent(Panel host, System.Windows.Forms.Control content, int minWidth, int minHeight)
        {
            if (host == null || content == null || host.IsDisposed || content.IsDisposed)
                return;
            int width = Math.Max(minWidth, host.DisplayRectangle.Width);
            int height = Math.Max(minHeight, host.ClientSize.Height);
            if (content.Width != width)
                content.Width = width;
            if (content.Height != height)
                content.Height = height;
            if (ReferenceEquals(content, _leftColumnContent))
                UpdateLeftRailWidth();
        }

        /// <summary>
        /// 左栏需要纵向滚动条时把根部左列加宽 17px，抵消滚动条占掉的宽度：
        /// 左栏内容始终保持 210px（按钮不低于 80px 点击区），主视野相应让出这一条。
        /// 设计稿尺寸下不出现滚动条，左列仍是 210px。
        /// <para>
        /// 判据直接用窗体客户区高度推算（可视高度 = 客户区 − 根布局上下内边距），不依赖滚动条
        /// 是否已经完成布局 —— 否则“滚动条出现 → 客户区变窄 → 再同步”的先后顺序会让列宽来回抖。
        /// </para>
        /// </summary>
        private void UpdateLeftRailWidth()
        {
            if (_rootLayout == null || _leftRailHost == null || _leftRailHost.IsDisposed ||
                ClientSize.Height <= 0)
                return;
            int railViewportHeight = ClientSize.Height - _rootLayout.Padding.Vertical;
            bool scrollBar = railViewportHeight < MIN_LEFT_COLUMN_HEIGHT;
            int rail = LEFT_RAIL_WIDTH + (scrollBar ? SystemInformation.VerticalScrollBarWidth : 0);
            // 就地改 ColumnStyle（不要替换集合元素）：替换不会通知 TableLayoutPanel 重算列宽，
            // 结果是列宽偶尔生效、偶尔卡在上一尺寸。
            ColumnStyle style = _rootLayout.ColumnStyles[0];
            if (style.SizeType == SizeType.Absolute && Math.Abs(style.Width - rail) < 0.5)
                return;
            style.SizeType = SizeType.Absolute;
            style.Width = rail;
            _rootLayout.PerformLayout();
        }

        /// <summary>窗体尺寸变化时重算左栏是否需要滚动条（列宽随之调整）。</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateLeftRailWidth();
            // WM_SIZE 处理期间布局被挂起：列宽改完要等这次消息处理结束再强制一轮根布局，
            // 否则用户拖完窗口的最后一次 resize 可能停在上一个列宽（差 17px 滚动条宽度）。
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(ReconcileLayoutAfterResize));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
        }

        /// <summary>resize 消息收尾后的第二轮布局：只补列宽与根布局，幂等且无新事件。</summary>
        private void ReconcileLayoutAfterResize()
        {
            if (IsDisposed)
                return;
            UpdateLeftRailWidth();
            if (_rootLayout != null && !_rootLayout.IsDisposed)
                _rootLayout.PerformLayout();
        }

        // ---------------------------------------------------------------- 左栏

        private System.Windows.Forms.Control BuildLeftColumn()
        {
            TableLayoutPanel column = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Margin = new Padding(0),
                BackColor = UiStyles.FormBack
            };
            column.AccessibleName = "leftColumn";
            column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // 五张操作卡按功能域自上而下；标定维护单独一张卡，避免把设备卡挤到超过 12 个交互控件。
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 168f));   // 连接（2 行，含急停）
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 174f));   // 手动（2 行）
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 196f));   // 运动（2 行：起摆 / 停止 / 使能 / 回到零点）
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 188f));   // 参数（2 行）
            column.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));   // 标定维护（清空 / 规整化左中右）
            column.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));    // 操作提示

            column.Controls.Add(BuildConnectCard(), 0, 0);
            column.Controls.Add(BuildManualCard(), 0, 1);
            column.Controls.Add(BuildRunCard(), 0, 2);
            column.Controls.Add(BuildParamsCard(), 0, 3);
            column.Controls.Add(BuildCalibrationMaintenanceCard(), 0, 4);
            column.Controls.Add(BuildMessagePanel(), 0, 5);
            return column;
        }

        /// <summary>标定维护卡：清空五个位置点 / 把当前位姿规整化为左中右零点；都不移动电机。</summary>
        private System.Windows.Forms.Control BuildCalibrationMaintenanceCard()
        {
            _clearCalibrationButton = MakeRailButton("清空标定", "btnClearCalibration", ClearCalibrationClicked, false);
            _hints.SetToolTip(_clearCalibrationButton,
                "清空左限位 / 零点 / 右限位 / 摆杆 0° / 360° 五个位置点；保留一圈量、PID、泵参数。不移动电机。");
            _normalizePoseButton = MakeRailButton("规整化左中右", "btnNormalizeArmPose", NormalizeArmPoseClicked, false);
            _hints.SetToolTip(_normalizePoseButton,
                "把当前 0x6064 作为新零点，按同一位移平移左右限位（保留行程）；只用于机械确实停在零点姿态时。");
            return BuildOperationCard("标定维护", "operationCalibration", 1, null,
                _clearCalibrationButton, _normalizePoseButton);
        }

        /// <summary>
        /// 五张操作卡，一卡一个功能域：连接 / 手动 / 运动 / 参数 / 标定维护。
        /// 每张卡固定两列，按钮 26px 高、同卡同宽；按钮区不超过卡片内容高 2/3。
        /// </summary>
        private System.Windows.Forms.Control BuildConnectCard()
        {
            // 急停放在这张卡的第一格：与连接类按钮同区、位置最靠上，不单独占一张卡。
            _estopButton = UiStyles.MakeDangerButton("急停", EStopClicked, 10f);
            _estopButton.AccessibleName = "btnEStop";
            _estopButton.Name = "btnEStop";
            _estopButton.Margin = new Padding(3, 3, 3, 3);
            _hints.SetToolTip(_estopButton, "立即安全态：u=0、断使能、报警锁存。");

            _connectButton = MakeRailButton("连接", "btnConnect", ConnectClicked, true);
            _disconnectButton = MakeRailButton("断开", "btnDisconnect", DisconnectClicked, false);
            _resetButton = MakeRailButton("复位", "btnReset", ResetClicked, false);

            // 「使能」已随运动动作归位到「运动」卡：它和 起摆 / 停止 是同一条操作链，不放连接卡。
            return BuildOperationCard("连接", "operationConnect", 2, null,
                _estopButton, _connectButton, _disconnectButton, _resetButton);
        }

        private System.Windows.Forms.Control BuildManualCard()
        {
            _jogLeftButton = MakeRailButton("◀ 点动", "btnJogLeft", null, false);
            _jogRightButton = MakeRailButton("点动 ▶", "btnJogRight", null, false);
            JogButtonEvents(_jogLeftButton, -1);
            JogButtonEvents(_jogRightButton, 1);

            // 摆杆零点 / 360° 两个采集按钮在「设备参数 / 软限位」卡里，与它们的输入框同排（不在这里重复）。
            return BuildOperationCard("手动", "operationManual", 1, null,
                _jogLeftButton, _jogRightButton);
        }

        /// <summary>
        /// 运动卡：起摆 / 停止 / 使能 三个运动类动作放同一张卡，一眼看出是一套。
        /// 起摆与停止是一对反向动作，排在同一行相邻两格；使能是它们的前置条件，排在下一行。
        /// 停止原先在「连接与使能」卡（现「连接」卡）的第三行，与运动动作隔了两张卡，起摆时找不到 —— 这里归位。
        /// </summary>
        private System.Windows.Forms.Control BuildRunCard()
        {
            _swingButton = MakeRailButton("起摆", "btnSwingUp", StartClicked, true);
            _stopButton = MakeRailButton("停止", "btnStop", StopClicked, false);
            _hints.SetToolTip(_stopButton,
                "受控停：取消点动 → 目标钉住实测位置 → 0x0002 QuickStop 按 0x6085 斜坡减速 → 确认 QuickStopActive → 0x0000 去使能。重复点不报错。");
            _enableButton = MakeRailButton("使能", "btnEnable", EnableClicked, false);
            _hints.SetToolTip(_enableButton, "上伺服并进 OP；起摆前需要先使能（「起摆」也会自动补使能）。");
            _returnHomeButton = MakeRailButton("回到零点", "btnReturnHome", ReturnHomeClicked, false);
            _hints.SetToolTip(_returnHomeButton,
                "把摆臂运动回已标定的零点（绝对 0x6064 counts）；只移动，不重设零点、不改一圈量 / PID / 泵。");

            return BuildOperationCard("运动", "operationRun", 2, null,
                _swingButton, _stopButton, _enableButton, _returnHomeButton);
        }

        private System.Windows.Forms.Control BuildParamsCard()
        {
            // 保存 = 落盘（含完整路径回执）；应用 = 只把当前值下发控制层、不写文件（改了还没决定存不存时用）。
            _saveParamsButton = MakeRailButton(SAVE_BUTTON_TEXT, "btnSaveParams", SaveParamsClicked, false);
            _applyParamsButton = MakeRailButton("应用（不保存）", "btnApplyParams", ApplyParamsClicked, false);
            _loadParamsButton = MakeRailButton("载入参数", "btnLoadParams", LoadParamsClicked, false);
            _restoreDefaultsButton = MakeRailButton("恢复默认", "btnRestoreDefaults", RestoreDefaultsClicked, false);

            return BuildOperationCard("参数", "operationParams", 2, null,
                _saveParamsButton, _applyParamsButton, _loadParamsButton, _restoreDefaultsButton);
        }

        /// <summary>
        /// 操作卡统一骨架：两列等宽、行高 32（26px 按钮 + 6px 间距），最后一行放说明 / 摘要。
        /// 按钮绝不跨列（同卡同宽），也不纵向 Dock 撑满（卡片高度由行数决定）。
        /// </summary>
        private System.Windows.Forms.Control BuildOperationCard(string title, string accessibleName,
            int rows, System.Windows.Forms.Control footer, params Button[] buttons)
        {
            CardPanel card = UiStyles.MakeCard(title, accessibleName);
            TableLayoutPanel body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows + 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
            body.AccessibleName = accessibleName + "Body";
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            for (int i = 0; i < rows; i++)
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            for (int i = 0; i < buttons.Length; i++)
                body.Controls.Add(buttons[i], i % 2, i / 2);
            if (footer != null)
            {
                body.Controls.Add(footer, 0, rows);
                body.SetColumnSpan(footer, 2);
            }
            card.Controls.Add(body);
            return card;
        }

        /// <summary>统一左栏按钮：主操作蓝底、次操作白底描边，高 26px、字号 8.5pt，同卡等宽。</summary>
        private Button MakeRailButton(string text, string accessibleName, EventHandler handler, bool primary)
        {
            Button button = primary
                ? UiStyles.MakePrimaryButton(text, handler, 8.5f)
                : UiStyles.MakeButton(text, Color.Empty, handler, Color.Empty, 8.5f);
            button.AccessibleName = accessibleName;
            button.Name = accessibleName; // UIA AutomationId 固定为 btnXxx，不暴露瞬时 HWND。
            // 窄屏（1024 级）卡片变窄时，长按钮文案给省略号而不是硬切边；完整含义仍由同一控件的
            // ToolTip 与可编程名称承载，不改变按钮尺寸 / 配色。
            button.AutoEllipsis = true;
            button.Margin = new Padding(3, 3, 3, 3);
            button.Padding = new Padding(0);
            // 右栏卡片比左栏宽，按钮仍按布局规格封顶 160 px（宽高比自检只认 ≤160）。
            button.MaximumSize = new Size(160, 0);
            return button;
        }

        /// <summary>
        /// 「操作提示」卡片：只放操作回执 —— 起摆被拒 / 使能未完成 / 停止完成 / 参数落盘 等一句话结果。
        /// <para>
        /// 这里**不是**状态卡。原来的「总线 / 引擎 / 运行 / 参数」四行是引擎快照，
        /// 与主视野「当前状态」卡里的关节位置三项同属「当前状态」，两处各画一份会被读成两份状态。
        /// 现在四行并入主视野「当前状态」卡，左栏只留消息，全界面只有一份当前状态。
        /// </para>
        /// </summary>
        private System.Windows.Forms.Control BuildMessagePanel()
        {
            CardPanel card = UiStyles.MakeCard("操作提示", "messagePanel");

            TableLayoutPanel body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 1,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            body.AccessibleName = "messageBody";
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _statusMessageLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "—",
                Font = UiStyles.UiFont(8.5f),
                ForeColor = UiStyles.StatusText,
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false,
                AutoSize = false,
                BackColor = Color.Transparent,
                Margin = new Padding(3, 3, 3, 3)
            };
            _statusMessageLabel.AccessibleName = "lblStatusMessage";
            _statusMessageLabel.Name = "lblStatusMessage";
            body.Controls.Add(_statusMessageLabel, 0, 0);

            card.Controls.Add(body);
            return card;
        }

        // ---------------------------------------------------------------- 右侧主视野（单页）

        /// <summary>
        /// 主视野单页：八张卡片全部常驻，按「运行 → 参数 → 标定 / 日志」三行排布。
        /// 行 1：PDO 实时数据 + 总线与采样 + 当前状态；行 2：杆角度表盘（跨行 2、行 3）+ PID 参数 + 设备参数·软限位；
        /// 行 3：表盘续 + 角度读数 + 运行日志。
        /// 行列全用百分比：窗口拉大时各区块等比跟着长，卡片内部控件与绑定一概不动。
        /// </summary>
        private System.Windows.Forms.Control BuildMainPage()
        {
            TableLayoutPanel page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = UiStyles.FormBack
            };
            page.AccessibleName = "mainPage";
            page.Name = "mainPage";

            // 三列等宽；三行比例按各行的**最小可用高度**定，不平均分：
            //   行 1 的 PDO 实时数据已收回单格：11 行 22px 读数 + 说明折成两行，33.5% 是下限；
            //   行 2 的 PID 卡 4 行 58px 输入 + 调参入口、设备卡 6 行采集，取 34.0%；
            //   行 3 只剩角度读数 / 运行日志，取余下的 32.5%。
            // 杆角度表盘左列跨行 2、行 3（合计 66.5% 高，设计稿约 337×637 px）：
            // 占两格、面积约等于两张单格卡，表盘半径由 52px 放大到 130px —— 图看的是姿态，不再挤在一格里。
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 33.5f));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 34.0f));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 32.5f));

            System.Windows.Forms.Control dial = BuildDialCard();
            page.Controls.Add(BuildStepLiveCard(), 0, 0);
            page.Controls.Add(BuildPdoCard(), 1, 0);
            page.Controls.Add(BuildCurrentStateCard(), 2, 0);
            page.Controls.Add(dial, 0, 1);
            page.SetRowSpan(dial, 2);
            page.Controls.Add(BuildPidCard(), 1, 1);
            page.Controls.Add(BuildDeviceCard(), 2, 1);
            page.Controls.Add(BuildAngleCard(), 1, 2);
            page.Controls.Add(BuildLogCard(), 2, 2);
            return page;
        }

        /// <summary>
        /// 卡片 PDO 实时数据（单页第一行左列，单格）：目标 / 实际 / 偏差 / 速度 / 电流 / 状态字 / 错误码 / 使能 /
        /// 模式 / 反馈性质 / 从站。全部直接取 PDO 过程映像字段，不做角度反算；
        /// 没映射进 PDO 的量如实写「未映射」，不拿 0 冒充真值。
        /// 卡宽只有单格，标签列放到 52%（默认 36% 会把「目标位置 0x607A (counts)」这类寄存器名省略成「…」）。
        /// </summary>
        private System.Windows.Forms.Control BuildStepLiveCard()
        {
            CardPanel card = UiStyles.MakeCard("PDO 实时数据", "cardStepLive");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 12,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            grid.AccessibleName = "stepLiveGrid";
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 11; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // 单格宽卡：标签列 52%（默认 36% 会把「目标位置 0x607A (counts)」这类寄存器名省略）。
            const float CaptionPercent = 52f;
            grid.Controls.Add(UiStyles.MakeStat("目标位置 0x607A (counts)", out _pdoTargetPosLabel, CaptionPercent), 0, 0);
            _pdoTargetPosLabel.AccessibleName = "lblPdoTargetPosition";
            grid.Controls.Add(UiStyles.MakeStat("实际位置 0x6064 (counts)", out _pdoActualPosLabel, CaptionPercent), 0, 1);
            _pdoActualPosLabel.AccessibleName = "lblPdoActualPosition";
            grid.Controls.Add(UiStyles.MakeStat("位置偏差 (counts)", out _followErrorLabel, CaptionPercent), 0, 2);
            _followErrorLabel.AccessibleName = "lblFollowError";
            grid.Controls.Add(UiStyles.MakeStat("实际速度 0x606C (counts/s)", out _stepVelocityLabel, CaptionPercent), 0, 3);
            _stepVelocityLabel.AccessibleName = "lblStepVelocity";
            grid.Controls.Add(UiStyles.MakeStat("电流 / 力矩 0x6077", out _stepTorqueLabel, CaptionPercent), 0, 4);
            _stepTorqueLabel.AccessibleName = "lblStepTorque";
            grid.Controls.Add(UiStyles.MakeStat("状态字 0x6041 (hex)", out _pdoStatusWordLabel, CaptionPercent), 0, 5);
            _pdoStatusWordLabel.AccessibleName = "lblPdoStatusWord";
            grid.Controls.Add(UiStyles.MakeStat("错误码 0x603F (hex)", out _pdoErrorCodeLabel, CaptionPercent), 0, 6);
            _pdoErrorCodeLabel.AccessibleName = "lblPdoErrorCode";
            grid.Controls.Add(UiStyles.MakeStat("使能状态", out _stepEnableLabel, CaptionPercent), 0, 7);
            _stepEnableLabel.AccessibleName = "lblStepEnable";
            grid.Controls.Add(UiStyles.MakeStat("运行模式 0x6061", out _pdoModeLabel, CaptionPercent), 0, 8);
            _pdoModeLabel.AccessibleName = "lblPdoMode";
            grid.Controls.Add(UiStyles.MakeStat("位置反馈", out _stepFeedbackLabel, CaptionPercent), 0, 9);
            _stepFeedbackLabel.AccessibleName = "lblStepFeedback";
            grid.Controls.Add(UiStyles.MakeStat("步进从站", out _stepSlaveLabel, CaptionPercent), 0, 10);
            _stepSlaveLabel.AccessibleName = "lblStepSlave";

            Label hint = UiStyles.MakeHint(
                "位置偏差 = 目标 − 实际；0x6077 未映射进 PDO，电流/力矩无真值；0x606C 为幅值口径（不带符号）。", 8.5f);
            hint.AccessibleName = "lblStepLiveHint";
            hint.Name = "lblStepLiveHint";
            grid.Controls.Add(hint, 0, 11);

            card.Controls.Add(grid);
            return card;
        }

        /// <summary>
        /// 卡片 当前状态（单页第一行右列）：总线 / 引擎 / 运行 / 参数 + 关节绝对位置 / 相对零点 / 软限位。
        /// <para>
        /// 全界面**唯一**一份「当前状态」：左栏原来那张同名状态卡的四行已并入本卡。
        /// 前四行取自引擎 / 主站实测快照，后三行是 0x6064 原始计数；角度类读数与换算系数放在同页的角度读数卡与设备参数卡。
        /// </para>
        /// </summary>
        private System.Windows.Forms.Control BuildCurrentStateCard()
        {
            CardPanel card = UiStyles.MakeCard("当前状态", "cardCurrent");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            grid.AccessibleName = "currentGrid";
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 7; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            grid.Controls.Add(UiStyles.MakeStat("总线", out _statusConnLabel), 0, 0);
            _statusConnLabel.AccessibleName = "lblStatusConnection";
            grid.Controls.Add(UiStyles.MakeStat("引擎", out _statusStateLabel), 0, 1);
            _statusStateLabel.AccessibleName = "lblStatusEngine";
            _statusStateLabel.Name = "lblStatusEngine";
            grid.Controls.Add(UiStyles.MakeStat("运行", out _statusUptimeLabel), 0, 2);
            _statusUptimeLabel.AccessibleName = "lblStatusUptime";
            grid.Controls.Add(UiStyles.MakeStat("参数", out _paramSavedLabel), 0, 3);
            _paramSavedLabel.AccessibleName = "lblStatusParamSaved";

            grid.Controls.Add(UiStyles.MakeStat("关节位置 (counts)", out _armPositionLabel), 0, 4);
            _armPositionLabel.AccessibleName = "lblArmPosition";
            grid.Controls.Add(UiStyles.MakeStat("相对零点 (counts)", out _armOffsetLabel), 0, 5);
            _armOffsetLabel.AccessibleName = "lblArmOffset";
            grid.Controls.Add(UiStyles.MakeStat("软限位", out _armLimitStateLabel), 0, 6);
            _armLimitStateLabel.AccessibleName = "lblArmLimitState";

            Label hint = UiStyles.MakeHint(
                "总线 / 引擎 / 运行 / 参数取引擎与主站实测快照；位置三项是 0x6064 原始计数，不做角度换算。角度读数与换算系数在同页其它卡片。", 8.5f);
            hint.AccessibleName = "lblCurrentHint";
            hint.Name = "lblCurrentHint";
            grid.Controls.Add(hint, 0, 7);

            card.Controls.Add(grid);
            return card;
        }

        /// <summary>
        /// 卡片 总线与采样：WKC / PDO 周期 / 从站在线 / 主站实测状态 / 链路 / 端口 / RT 周期 / 采样时刻 / 配置来源。
        /// 全部取主站实测快照与遥测，总线量不再和步进量混在一张卡里。
        /// </summary>
        private System.Windows.Forms.Control BuildPdoCard()
        {
            CardPanel card = UiStyles.MakeCard("总线与采样", "cardPdo");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 10,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            grid.AccessibleName = "pdoGrid";
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 9; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            grid.Controls.Add(UiStyles.MakeStat("WKC 实际 / 期望", out _busWkcLabel), 0, 0);
            _busWkcLabel.AccessibleName = "lblBusWkc";
            grid.Controls.Add(UiStyles.MakeStat("PDO 周期计数", out _busCycleCountLabel), 0, 1);
            _busCycleCountLabel.AccessibleName = "lblBusCycleCount";
            grid.Controls.Add(UiStyles.MakeStat("从站在线", out _busSlavesLabel), 0, 2);
            _busSlavesLabel.AccessibleName = "lblBusSlaves";
            grid.Controls.Add(UiStyles.MakeStat("主站状态", out _busMasterLabel), 0, 3);
            _busMasterLabel.AccessibleName = "lblBusMaster";
            grid.Controls.Add(UiStyles.MakeStat("链路状态", out _busLinkLabel), 0, 4);
            _busLinkLabel.AccessibleName = "lblBusLink";
            grid.Controls.Add(UiStyles.MakeStat("端口 主 / 副", out _busPortLabel), 0, 5);
            _busPortLabel.AccessibleName = "lblBusPort";
            grid.Controls.Add(UiStyles.MakeStat("RT 周期", out _busRtLabel), 0, 6);
            _busRtLabel.AccessibleName = "lblBusRt";
            grid.Controls.Add(UiStyles.MakeStat("采样时刻", out _sampleTimeLabel), 0, 7);
            _sampleTimeLabel.AccessibleName = "lblSampleTime";
            grid.Controls.Add(UiStyles.MakeStat("配置来源", out _configSourceLabel), 0, 8);
            _configSourceLabel.AccessibleName = "lblConfigSource";

            Label hint = UiStyles.MakeHint(
                "主站状态 / 链路 / 端口取主站实测快照，不按引擎状态猜；采样陈旧 >1 s 标橙。", 8.5f);
            hint.AccessibleName = "lblBusHint";
            hint.Name = "lblBusHint";
            grid.Controls.Add(hint, 0, 9);

            card.Controls.Add(grid);
            return card;
        }

        /// <summary>卡片 杆角度图（单页第二行左列，跨行 2、行 3 共两格）：单表盘（6 点钟下垂 0°、12 点钟倒立 180°，顶部绿弧 = 倒立目标）。</summary>
        private System.Windows.Forms.Control BuildDialCard()
        {
            CardPanel card = UiStyles.MakeCard("杆角度", "cardDial");
            _pendulumDial = new PendulumDialView { Dock = DockStyle.Fill, Margin = new Padding(0) };
            card.Controls.Add(_pendulumDial);
            return card;
        }

        /// <summary>
        /// 卡片 角度读数（单页第三行中列）：θ / ψ / ω / 摆臂 φ / 能量 E / 目标能量 E* / 接近倒立 /
        /// CH1·CH2 原始计数 / CH1 频率。控制层每拍遥测同一帧喂进来，界面不二次换算、不猜。
        /// </summary>
        private System.Windows.Forms.Control BuildAngleCard()
        {
            CardPanel card = UiStyles.MakeCard("角度读数", "cardAngle");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 11,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            grid.AccessibleName = "angleGrid";
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 10; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            grid.Controls.Add(UiStyles.MakeStat("摆杆角 θ (°)", out _thetaLabel), 0, 0);
            _thetaLabel.AccessibleName = "lblTheta";
            grid.Controls.Add(UiStyles.MakeStat("离直立 ψ (°)", out _psiLabel), 0, 1);
            _psiLabel.AccessibleName = "lblPsi";
            grid.Controls.Add(UiStyles.MakeStat("摆杆角速度 ω (°/s)", out _omegaLabel), 0, 2);
            _omegaLabel.AccessibleName = "lblOmega";
            grid.Controls.Add(UiStyles.MakeStat("摆臂角 φ (°)", out _phiLabel), 0, 3);
            _phiLabel.AccessibleName = "lblPhi";
            grid.Controls.Add(UiStyles.MakeStat("能量 E (J)", out _energyLabel), 0, 4);
            _energyLabel.AccessibleName = "lblEnergy";
            grid.Controls.Add(UiStyles.MakeStat("目标能量 E* (J)", out _energyTargetLabel), 0, 5);
            _energyTargetLabel.AccessibleName = "lblEnergyTarget";
            grid.Controls.Add(UiStyles.MakeStat("接近倒立", out _nearUprightLabel), 0, 6);
            _nearUprightLabel.AccessibleName = "lblNearUpright";
            grid.Controls.Add(UiStyles.MakeStat("CH1 原始计数", out _pendulumCountLabel), 0, 7);
            _pendulumCountLabel.AccessibleName = "lblPendulumCount";
            grid.Controls.Add(UiStyles.MakeStat("CH2 原始计数", out _pendulumCountCh2Label), 0, 8);
            _pendulumCountCh2Label.AccessibleName = "lblPendulumCountCh2";
            grid.Controls.Add(UiStyles.MakeStat("CH1 频率 (Hz)", out _pendulumFreqLabel), 0, 9);
            _pendulumFreqLabel.AccessibleName = "lblPendulumFreqCh1";

            Label hint = UiStyles.MakeHint(
                "ψ = θ−180°（离直立）；φ 是摆臂绝对角；E/E* 取控制层同拍能量口径。CH1/CH2 是 GCAN 计数切片原值，"
                + "不换算；频率计只有 GC1502 提供，GC1602 如实显示未提供。", 8.5f);
            hint.AccessibleName = "lblAngleHint";
            hint.Name = "lblAngleHint";
            grid.Controls.Add(hint, 0, 10);

            card.Controls.Add(grid);
            return card;
        }

        /// <summary>
        /// 卡片④ PID 参数：历史增益 Ka / Kad / Kai + 能量泵 k_e / 幅度 / 频率 / 相位 + 内环调参入口。
        /// 平衡环已由整套状态反馈接管，Ka / Kad / Kai 只作历史值显示（随参数文件读写，不参与平衡）；
        /// 真正能改的是泵参数与内环 Kpφ / Kiφ / Kdφ（点本卡「PID 调参…」）。
        /// </summary>
        private System.Windows.Forms.Control BuildPidCard()
        {
            CardPanel card = UiStyles.MakeCard("PID 参数", "cardPid");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            grid.AccessibleName = "pidGrid";
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            for (int i = 0; i < 4; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            PumpParameters defaultPump = PumpParameters.CreateDefault();
            PidGains defaultGains = PidGains.CreateDefault();

            // 增益框统一 5 位小数（与内环调参对话框、CLI 增益表一致）：Ka=5000.00000 / Kad=300.00000 /
            // Kai=0.00000 / k_e=1.00000。3 位以下会把 0.0009 显示成 0.001，SetNumber 回填时还会按
            // DecimalPlaces 取整，用户改同卡其它字段就把取整值写回控制层。
            _kpBox = UiStyles.MakeNumber((decimal)defaultGains.Ka, 0m, 100000m, 5);
            _kpBox.AccessibleName = "numPidKp";
            _kiBox = UiStyles.MakeNumber((decimal)defaultGains.Kad, 0m, 100000m, 5);
            _kiBox.AccessibleName = "numPidKi";
            _kdBox = UiStyles.MakeNumber((decimal)defaultGains.Kai, 0m, 100000m, 5);
            _kdBox.AccessibleName = "numPidKd";
            _keBox = UiStyles.MakeNumber((decimal)defaultPump.Ke, 0.001m, 1000m, 5);
            _keBox.AccessibleName = "numPumpKe";
            _pumpAmpBox = UiStyles.MakeNumber((decimal)defaultPump.PumpAmpDeg, 0m, 180m, 1);
            _pumpAmpBox.AccessibleName = "numPumpAmp";
            _pumpFreqBox = UiStyles.MakeNumber((decimal)defaultPump.PumpHz, 0.1m, 10m, 2);
            _pumpFreqBox.AccessibleName = "numPumpFreq";
            _pumpPhaseBox = UiStyles.MakeNumber((decimal)defaultPump.PhaseOffsetDeg, 0m, 360m, 1);
            _pumpPhaseBox.AccessibleName = "numPumpPhase";

            _kpBox.ValueChanged += PidParamChanged;
            _kiBox.ValueChanged += PidParamChanged;
            _kdBox.ValueChanged += PidParamChanged;
            _keBox.ValueChanged += PumpParamChanged;
            _pumpAmpBox.ValueChanged += PumpParamChanged;
            _pumpFreqBox.ValueChanged += PumpParamChanged;
            _pumpPhaseBox.ValueChanged += PumpParamChanged;

            // 平衡环已由整套状态反馈接管：Ka / Kad / Kai 是历史值，不再参与平衡。
            // 输入框保留只为旧参数文件里的值仍能原样读写；静态提示先按运行时同一口径写一遍，
            // 首次刷新时 MainForm.Refresh 会用同一句话再覆盖一次。
            // 标签只写符号 + （历史）：左栏每格约 90 px，带「杆角速度」这类描述再加后缀会被省略号截掉。
            const string historicalHint = "平衡环已由整套状态反馈接管：Ka / Kad / Kai 只作历史参数显示，调整它们不影响平衡。";
            _hints.SetToolTip(_kpBox, historicalHint + " Ka 是上一代摆杆角误差增益的历史值，仍会原样写进参数文件。");
            _hints.SetToolTip(_kiBox, historicalHint + " Kad 是上一代摆杆角速度增益的历史值，仍会原样写进参数文件。");
            _hints.SetToolTip(_kdBox, historicalHint + " Kai 是上一代摆杆角积分增益的历史值，仍会原样写进参数文件。");

            grid.Controls.Add(UiStyles.MakeLabeledField("Ka（历史）", _kpBox), 0, 0);
            grid.Controls.Add(UiStyles.MakeLabeledField("Kad（历史）", _kiBox), 1, 0);
            grid.Controls.Add(UiStyles.MakeLabeledField("Kai（历史）", _kdBox), 0, 1);
            grid.Controls.Add(UiStyles.MakeLabeledField("泵增益 k_e", _keBox), 1, 1);
            grid.Controls.Add(UiStyles.MakeLabeledField("泵幅度 (°)", _pumpAmpBox), 0, 2);
            grid.Controls.Add(UiStyles.MakeLabeledField("泵频 (Hz)", _pumpFreqBox), 1, 2);
            grid.Controls.Add(UiStyles.MakeLabeledField("相位偏移 (°)", _pumpPhaseBox), 0, 3);

            _pumpStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "已下发 —",
                Font = UiStyles.UiFont(8.5f),
                ForeColor = UiStyles.CaptionText,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                AutoSize = false,
                BackColor = Color.Transparent,
                Margin = new Padding(4, 20, 4, 2)
            };
            _pumpStatusLabel.AccessibleName = "lblPumpStatus";
            grid.Controls.Add(_pumpStatusLabel, 1, 3);

            _pidDialogButton = MakeRailButton("PID 调参…", "btnPidTuning", PidTuningClicked, false);
            grid.Controls.Add(_pidDialogButton, 0, 4);

            Label hint = UiStyles.MakeHint("", 8.5f);
            hint.AccessibleName = "lblPidHint";
            grid.Controls.Add(hint, 0, 5);
            grid.SetColumnSpan(hint, 2);
            card.Controls.Add(grid);
            return card;
        }

        /// <summary>
        /// 卡片⑤ 设备参数 / 软限位。现场只有 **5 个位置**，每个位置一个采集按钮 + 一个绝对 counts 输入框：
        /// 左限位 / 驱动臂零点 / 右限位（驱动臂三点，要求 左 &lt; 驱动臂零点 &lt; 右）与 摆杆零点（下垂 0°）/ 摆杆 360°（摆杆两点）。
        /// **摆杆一圈的量 = |360° count − 0° count|**，由两点标定算出，界面只显示结果，没有输入框。
        /// 另加一个「规整化摆杆」按钮：只平移摆杆 0°/360° 基准，不移动电机，也不改一圈量。
        /// 不存在角度 / 度 / 比例 / 细分 / 每圈计数 / 「绝对定位」这类重复输入。
        /// </summary>
        private System.Windows.Forms.Control BuildDeviceCard()
        {
            CardPanel card = UiStyles.MakeCard("设备参数 / 软限位", "cardDevice");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            grid.AccessibleName = "deviceGrid";
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));   // 左限位
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));   // 驱动臂零点
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));   // 右限位
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));   // 摆杆零点（下垂 0°）
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));   // 摆杆 360°
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, BUTTON_ROW_HEIGHT));   // 摆杆 0° / 360° 规整化
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));                 // 一圈的量（只读）
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));                 // 当前 0x6064
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));                 // 状态 + 安全阈值

            // 输入框只用 int32 全域，不设任何角度 / 行程量程；采集按钮取当前实时计数。
            // 五个采集按钮是同一级动作，一律白底描边：「驱动臂零点」原来误用了主操作蓝底，看着像本卡唯一主按钮，
            // 与它并排的 左限位 / 右限位 / 摆杆零点 / 摆杆 360° 却是白底 —— 同级不同色，操作者会以为它更重要。
            _setLeftLimitButton = MakeRailButton("左限位", "btnSetLeftLimit", SetLeftLimitClicked, false);
            _setArmZeroButton = MakeRailButton("驱动臂零点", "btnSetArmZero", SetArmZeroClicked, false);
            _setRightLimitButton = MakeRailButton("右限位", "btnSetRightLimit", SetRightLimitClicked, false);
            _setPendulumZeroButton = MakeRailButton("摆杆零点 (下垂 0°)", "btnSetPendulumZero", SetPendulumZeroClicked, false);
            _hints.SetToolTip(_setPendulumZeroButton,
                "把摆杆自然下垂位采为 0°。闭环运行中按钮禁用：先「停止」去使能再采集。");
            _setPendulum360Button = MakeRailButton("摆杆 360°", "btnSetPendulum360", SetPendulum360Clicked, false);
            _hints.SetToolTip(_setPendulum360Button,
                "把摆杆整转一圈后的计数采为 360°。闭环运行中按钮禁用：先「停止」再采集。");
            _normalizePendulumButton = MakeRailButton("规整化摆杆", "btnNormalizePendulumPose", NormalizePendulumPoseClicked, false);
            _hints.SetToolTip(_normalizePendulumButton,
                "把当前 CH1 计数认作摆杆 0°；已有 360° 时按同一位移平移。只用于摆杆确实在自然下垂位时。");

            _limitMinCountsBox = UiStyles.MakeNumber(0m, int.MinValue, int.MaxValue, 0);
            _limitMinCountsBox.AccessibleName = "numLimitMinCounts";
            _limitMinCountsBox.Margin = new Padding(0, 4, 0, 4);
            _limitMinCountsBox.ValueChanged += LimitCountsChanged;
            _hints.SetToolTip(_limitMinCountsBox,
                "左限位 = 绝对 0x6064 计数。与驱动臂零点 / 右限位一起要求：左限位 < 驱动臂零点 < 右限位。");

            _armZeroCountBox = UiStyles.MakeNumber(0m, int.MinValue, int.MaxValue, 0);
            _armZeroCountBox.AccessibleName = "numArmZeroCount";
            _armZeroCountBox.Margin = new Padding(0, 4, 0, 4);
            _armZeroCountBox.ValueChanged += ArmZeroCountChanged;
            _hints.SetToolTip(_armZeroCountBox,
                "驱动臂零点 = 驱动臂坐标 0 的绝对 0x6064 计数。点动中也可直接改，下一控制拍生效且位置指令不跳变。");

            _limitMaxCountsBox = UiStyles.MakeNumber(0m, int.MinValue, int.MaxValue, 0);
            _limitMaxCountsBox.AccessibleName = "numLimitMaxCounts";
            _limitMaxCountsBox.Margin = new Padding(0, 4, 0, 4);
            _limitMaxCountsBox.ValueChanged += LimitCountsChanged;
            _hints.SetToolTip(_limitMaxCountsBox,
                "右限位 = 绝对 0x6064 计数。左右可以不对称，零点也不必在中间。");

            _pendulumZeroCountBox = UiStyles.MakeNumber(0m, int.MinValue, int.MaxValue, 0);
            _pendulumZeroCountBox.AccessibleName = "numPendulumZeroCount";
            _pendulumZeroCountBox.Margin = new Padding(0, 4, 0, 4);
            _pendulumZeroCountBox.ValueChanged += PendulumZeroCountChanged;
            _hints.SetToolTip(_pendulumZeroCountBox,
                "摆杆零点（下垂 0°）= 摆杆自然下垂 / 最底部那一点的原始计数。与 360° 点一起标定出一圈的量。");

            _pendulum360CountBox = UiStyles.MakeNumber(0m, int.MinValue, int.MaxValue, 0);
            _pendulum360CountBox.AccessibleName = "numPendulum360Count";
            _pendulum360CountBox.Margin = new Padding(0, 4, 0, 4);
            _pendulum360CountBox.ValueChanged += Pendulum360CountChanged;
            _hints.SetToolTip(_pendulum360CountBox,
                "摆杆 360° = 从下垂 0° 点整转一圈后那一点的原始计数。一圈的量 = |360° − 0°|，只显示不输入。");

            grid.Controls.Add(_setLeftLimitButton, 0, 0);
            grid.Controls.Add(_limitMinCountsBox, 1, 0);
            grid.Controls.Add(_setArmZeroButton, 0, 1);
            grid.Controls.Add(_armZeroCountBox, 1, 1);
            grid.Controls.Add(_setRightLimitButton, 0, 2);
            grid.Controls.Add(_limitMaxCountsBox, 1, 2);
            grid.Controls.Add(_setPendulumZeroButton, 0, 3);
            grid.Controls.Add(_pendulumZeroCountBox, 1, 3);
            grid.Controls.Add(_setPendulum360Button, 0, 4);
            grid.Controls.Add(_pendulum360CountBox, 1, 4);
            grid.Controls.Add(_normalizePendulumButton, 0, 5);
            Label pendulumNormalizeHint = UiStyles.MakeHint("摆杆 0° / 360° 按同一位移平移", 8.5f);
            pendulumNormalizeHint.AccessibleName = "lblPendulumNormalizeHint";
            pendulumNormalizeHint.Name = "lblPendulumNormalizeHint";
            grid.Controls.Add(pendulumNormalizeHint, 1, 5);

            grid.Controls.Add(UiStyles.MakeReadout("一圈的量（0°→360°）", 120, out _pendulumCprLabel), 0, 6);
            _pendulumCprLabel.AccessibleName = "lblPendulumCprDerived";
            grid.SetColumnSpan(grid.GetControlFromPosition(0, 6), 2);

            grid.Controls.Add(UiStyles.MakeReadout("当前 0x6064", 66, out _limitCurrentLabel), 0, 7);
            _limitCurrentLabel.AccessibleName = "lblLimitCurrent";
            _limitCurrentLabel.Name = "lblLimitCurrent";
            grid.SetColumnSpan(grid.GetControlFromPosition(0, 7), 2);

            TableLayoutPanel footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 2, 0, 0),
                BackColor = Color.Transparent
            };
            footer.AccessibleName = "deviceFooter";
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _calibrationHintLabel = UiStyles.MakeHint("", 8.5f);
            _calibrationHintLabel.AccessibleName = "lblCalibrationHint";
            _calibrationHintLabel.Name = "lblCalibrationHint";
            footer.Controls.Add(_calibrationHintLabel, 0, 0);

            _safetyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "安全阈值 —",
                Font = UiStyles.UiFont(8.5f),
                ForeColor = UiStyles.CaptionText,
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false,
                AutoSize = false,
                BackColor = Color.Transparent,
                Margin = new Padding(3, 2, 3, 0)
            };
            _safetyLabel.AccessibleName = "lblSafetySummary";
            footer.Controls.Add(_safetyLabel, 0, 1);

            grid.Controls.Add(footer, 0, 8);
            grid.SetColumnSpan(footer, 2);
            card.Controls.Add(grid);
            return card;
        }

        /// <summary>卡片⑥ 运行日志：圆角卡片内嵌 RichTextBox，横向滚动不折行，按级别逐行着色。</summary>
        private System.Windows.Forms.Control BuildLogCard()
        {
            CardPanel card = UiStyles.MakeCard("运行日志", "cardLog");
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = UiStyles.GroupText,
                Font = UiStyles.MonoFont(10f),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false,
                HideSelection = false,
                AccessibleName = "logBox"
            };
            card.Controls.Add(_logBox);
            return card;
        }
    }
}
