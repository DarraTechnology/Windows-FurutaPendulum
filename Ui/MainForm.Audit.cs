using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 程序化自检（partial）：布局 / 界面契约 / 按钮矩阵 / 参数 / 按钮动作 / 控制参数对话框 / 标定规则七套。
    /// 只读界面树与内存数据，不需要截图、不连总线；由 Program 的 --ui-audit 在窗体 Show 之后调用。
    /// 每套最后一行固定 RESULT: PASS 或 RESULT: FAIL。
    /// </summary>
    public sealed partial class MainForm : Form
    {
        private static readonly string[] CardNames =
        {
            "operationConnect", "operationManual", "operationRun", "operationParams", "operationCalibration",
            "messagePanel",
            "cardStepLive", "cardPdo", "cardCurrent", "cardLog",
            "cardDial", "cardAngle", "cardPid", "cardDevice"
        };

        /// <summary>
        /// 布局与参数自检。判据：
        /// ⓪ 主视野单页：8 张卡片挂在同一张 mainPage 上全部常驻，全树无 TabControl 页签容器，
        ///    角度 / PID / 换算 / 限位与 PDO 实时数据读数全部可读且尺寸非零；
        /// ⓪b 视野比例：杆角度表盘占 2 格且面积 ≥ 单格参考卡的 1.9 倍（约为改动前单格表盘的 2.3 倍）；
        ///    PDO 实时数据收回 1 格且面积 ≤ 单格参考卡的 1.15 倍，任何一处被改回去立即红；
        /// ① 每个视觉容器（13 块 CardPanel）宽:高、高:宽都 ≤2.0；每张卡片宽高比在 0.4–2.0；
        /// ② 每个按钮的父链上有 Panel / GroupBox（绝不裸挂窗体），按钮高 24–28px、宽 ≤160；
        /// ③ 每转计数 = 200 × 细分数 的双向联动成立；
        /// ④ PID 参数只出现在 cardPid（numPid*），设备参数卡与 PID 卡零交叉字段；
        /// ⑤ 日志可见 ≥3 行、左栏 200–220px、卡片内子控件不越界。
        /// </summary>
        public string RunLayoutAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 界面布局自检 ===");
            int failures = 0;

            // ⓪ 主视野单页：八张卡片全部常驻，页签容器必须不存在；读数控件直接可读，不用切页。
            report.AppendLine("[0] 主视野单页（无页签，8 张卡片全部常驻）");
            if (!AuditMainPage(report))
                failures++;

            // ⓪b 视野比例：杆角度表盘放大到两格、PDO 实时数据收回单格；被改回去 / 被压小都立即红。
            report.AppendLine("[0b] 视野比例：杆角度表盘占 2 格放大 / PDO 实时数据 1 格收缩");
            if (!AuditDialPdoRatio(report))
                failures++;

            // ① 视觉容器宽高比
            List<System.Windows.Forms.Control> containers = new List<System.Windows.Forms.Control>();
            CollectVisualContainers(this, containers);
            containers.Sort(delegate (System.Windows.Forms.Control a, System.Windows.Forms.Control b)
            {
                return string.CompareOrdinal(ContainerName(a), ContainerName(b));
            });
            report.AppendLine("[1] 视觉容器宽高比（宽:高 与 高:宽 都必须 ≤2.0）");
            for (int i = 0; i < containers.Count; i++)
            {
                System.Windows.Forms.Control container = containers[i];
                int w = container.Width;
                int h = container.Height;
                bool ok = w > 0 && h > 0 && w <= h * 2 && h <= w * 2;
                double ratio = h > 0 ? (double)w / h : 0.0;
                double worst = ratio > 1.0 ? ratio : (ratio > 0.0 ? 1.0 / ratio : 0.0);
                if (!ok)
                    failures++;
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0,-20} {1,4} × {2,-4}  宽/高 {3,5:F2}  最大 {4,5:F2}   {5}",
                    ContainerName(container), w, h, ratio, worst, ok ? "OK" : "FAIL"));
            }
            report.AppendLine("    容器数 " + containers.Count);

            // ①b 每张卡片的宽高比（规格 0.4–2.0，宽/高；最坏方向单独打印）
            report.AppendLine("[1b] 每张卡片宽高比（规格 0.4–2.0，宽/高）");
            for (int i = 0; i < CardNames.Length; i++)
            {
                CardPanel card = UiControlFinder.FindByName(this, CardNames[i]) as CardPanel;
                if (card == null)
                {
                    report.AppendLine("    " + CardNames[i] + " 未找到：FAIL");
                    failures++;
                    continue;
                }
                double ratio = card.Height > 0 ? (double)card.Width / card.Height : 0.0;
                double worst = ratio > 1.0 ? ratio : (ratio > 0.0 ? 1.0 / ratio : 0.0);
                bool ok = ratio >= 0.4 && ratio <= 2.0 && card.Controls.Count > 0;
                if (!ok)
                    failures++;
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0,-18} {1,4} × {2,-4}  宽/高 {3:F3}  最坏 {4:F3}  子控件 {5}  {6}",
                    CardNames[i], card.Width, card.Height, ratio, worst, card.Controls.Count, ok ? "OK" : "FAIL"));
            }

            // ② 按钮：高度集合 / 宽度 / 最小点击区（数值断言，不靠目测）
            List<Button> buttons = new List<Button>();
            UiControlFinder.Collect(this, buttons);
            List<CardPanel> cards = new List<CardPanel>();
            UiControlFinder.Collect(this, cards);

            int buttonMinHeight = int.MaxValue;
            int buttonMaxHeight = 0;
            int buttonMinWidth = int.MaxValue;
            int buttonMaxWidth = 0;
            HashSet<int> heightKinds = new HashSet<int>();
            double heightSum = 0.0;
            double heightSumSq = 0.0;
            for (int i = 0; i < buttons.Count; i++)
            {
                Button button = buttons[i];
                heightKinds.Add(button.Height);
                heightSum += button.Height;
                heightSumSq += (double)button.Height * button.Height;
                if (button.Height < buttonMinHeight) buttonMinHeight = button.Height;
                if (button.Height > buttonMaxHeight) buttonMaxHeight = button.Height;
                if (button.Width < buttonMinWidth) buttonMinWidth = button.Width;
                if (button.Width > buttonMaxWidth) buttonMaxWidth = button.Width;
            }
            double heightMean = buttons.Count > 0 ? heightSum / buttons.Count : 0.0;
            double heightVariance = buttons.Count > 0
                ? Math.Max(0.0, heightSumSq / buttons.Count - heightMean * heightMean)
                : 0.0;
            bool heightOk = buttons.Count > 0 && heightKinds.Count == 1 &&
                            buttonMinHeight >= 24 && buttonMaxHeight <= 28 && heightVariance < 1e-9;
            bool clickOk = buttons.Count > 0 && buttonMinHeight >= 24 && buttonMinWidth >= 80;
            bool widthCapOk = buttonMaxWidth <= 160;
            report.AppendLine("[2] 按钮：高 24–28 / 宽 ≤160 / 点击区 ≥80×24");
            report.AppendLine("    按钮数 " + buttons.Count + "；高度 min/max = " + buttonMinHeight + "/" +
                buttonMaxHeight + " px（种类 " + heightKinds.Count + "，方差 " +
                heightVariance.ToString("F3") + "）；宽度 min/max = " + buttonMinWidth + "/" +
                buttonMaxWidth + " px");
            report.AppendLine("    高度集合方差=0 " + (heightVariance < 1e-9) + "；最小点击区 " +
                buttonMinWidth + "×" + buttonMinHeight + " px；宽度上限 ≤160 " + widthCapOk + "：" +
                (heightOk && clickOk && widthCapOk ? "OK" : "FAIL"));
            if (!(heightOk && clickOk && widthCapOk))
                failures++;

            // ②b 每卡：同宽 / 间距 ≥6 / 按钮区占比 ≤2/3 / 内边距 10–12 / 卡距 12
            report.AppendLine("[2b] 每卡按钮宽度一致性 / 间距 / 按钮区占比 / 内边距 / 卡距");
            for (int c = 0; c < cards.Count; c++)
            {
                CardPanel card = cards[c];
                List<Button> cardButtons = new List<Button>();
                for (int b = 0; b < buttons.Count; b++)
                {
                    if (ReferenceEquals(FindCardAncestor(buttons[b]), card))
                        cardButtons.Add(buttons[b]);
                }

                int contentHeight = Math.Max(1, card.ClientSize.Height - CardPanel.HeaderHeight - CardPanel.Pad);
                string widthsText = "无按钮";
                bool sameWidth = true;
                int minGap = int.MaxValue;
                double areaRatio = 0.0;
                if (cardButtons.Count > 0)
                {
                    HashSet<int> widthKinds = new HashSet<int>();
                    int minW = int.MaxValue;
                    int maxW = 0;
                    double wSum = 0.0;
                    double wSumSq = 0.0;
                    int top = int.MaxValue;
                    int bottom = int.MinValue;
                    List<Rectangle> rects = new List<Rectangle>(cardButtons.Count);
                    for (int b = 0; b < cardButtons.Count; b++)
                    {
                        Button button = cardButtons[b];
                        widthKinds.Add(button.Width);
                        wSum += button.Width;
                        wSumSq += (double)button.Width * button.Width;
                        if (button.Width < minW) minW = button.Width;
                        if (button.Width > maxW) maxW = button.Width;
                        Rectangle rect = CardRect(card, button);
                        rects.Add(rect);
                        if (rect.Top < top) top = rect.Top;
                        if (rect.Bottom > bottom) bottom = rect.Bottom;
                    }
                    double meanW = wSum / cardButtons.Count;
                    double varW = Math.Max(0.0, wSumSq / cardButtons.Count - meanW * meanW);
                    sameWidth = widthKinds.Count == 1 && varW < 1e-9;
                    widthsText = minW + "–" + maxW + " px（种类 " + widthKinds.Count +
                                 "，方差 " + varW.ToString("F2") + "）";
                    areaRatio = (bottom - top) / (double)contentHeight;

                    for (int a = 0; a < rects.Count; a++)
                    {
                        for (int b = a + 1; b < rects.Count; b++)
                        {
                            Rectangle ra = rects[a];
                            Rectangle rb = rects[b];
                            if (ra.Right <= rb.Left || rb.Right <= ra.Left)
                            {
                                int gap = Math.Max(ra.Left - rb.Right, rb.Left - ra.Right);
                                if (gap >= 0 && gap < minGap) minGap = gap;
                            }
                            if (ra.Bottom <= rb.Top || rb.Bottom <= ra.Top)
                            {
                                int gap = Math.Max(ra.Top - rb.Bottom, rb.Top - ra.Bottom);
                                if (gap >= 0 && gap < minGap) minGap = gap;
                            }
                        }
                    }
                }
                bool padOk = card.Padding.Left >= 10 && card.Padding.Left <= 12 &&
                             card.Padding.Right >= 10 && card.Padding.Right <= 12 &&
                             card.Padding.Bottom >= 10 && card.Padding.Bottom <= 12;
                bool gapOk = card.Margin.Left == CardPanel.Gap && card.Margin.Right == CardPanel.Gap &&
                             card.Margin.Top == CardPanel.Gap && card.Margin.Bottom == CardPanel.Gap;
                bool spacingOk = minGap == int.MaxValue || minGap >= 6;
                bool areaOk = areaRatio <= (2.0 / 3.0) + 1e-9;
                bool cardOk = sameWidth && spacingOk && areaOk && padOk && gapOk;
                if (!cardOk)
                    failures++;
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0,-18} 按钮 {1,2} 个  宽度 {2}  最小间距 {3} px  按钮区 {4:P1} / 内容高 {5} px  内边距 {6}  卡距 {7}  {8}",
                    ContainerName(card), cardButtons.Count, widthsText,
                    minGap == int.MaxValue ? -1 : minGap, areaRatio, contentHeight,
                    padOk ? "OK" : "FAIL", gapOk ? "OK" : "FAIL", cardOk ? "OK" : "FAIL"));
            }

            // ②c 每卡交互控件数（按钮 + 数字输入；规格 ≤12）
            report.AppendLine("[2c] 每卡交互控件数（按钮 + 数字输入，规格 ≤12）");
            for (int c = 0; c < cards.Count; c++)
            {
                CardPanel card = cards[c];
                int buttonsInCard = 0;
                int numericsInCard = 0;
                CountInteractive(card, ref buttonsInCard, ref numericsInCard);
                int interactive = buttonsInCard + numericsInCard;
                int total = CountDescendants(card);
                bool ok = interactive <= 12;
                if (!ok)
                    failures++;
                report.AppendLine("    " + ContainerName(card) + " 按钮 " + buttonsInCard +
                    " + 数字输入 " + numericsInCard + " = " + interactive +
                    "（子控件总数 " + total + "）：" + (ok ? "OK" : "FAIL（>12）"));
            }

            // ②d 字号种类（控件字体 + 卡片标题 + 表盘自绘；规格 ≤3）
            report.AppendLine("[2d] 全窗体字号种类（规格 ≤3）");
            HashSet<float> fontSizes = new HashSet<float>();
            CollectTextFontSizes(this, fontSizes);
            fontSizes.Add(UiStyles.CardTitleFont.Size);
            float[] dialSizes = PendulumDialView.RenderedFontSizes();
            for (int i = 0; i < dialSizes.Length; i++)
                fontSizes.Add(dialSizes[i]);
            List<float> sortedSizes = new List<float>(fontSizes);
            sortedSizes.Sort();
            string sizeText = "";
            for (int i = 0; i < sortedSizes.Count; i++)
                sizeText += (i == 0 ? "" : " / ") + sortedSizes[i].ToString("0.#");
            bool fontOk = sortedSizes.Count <= 3;
            report.AppendLine("    字号集合：" + sizeText + "（种类 " + sortedSizes.Count + "）：" +
                (fontOk ? "OK" : "FAIL（>3）"));
            if (!fontOk)
                failures++;

            // ②e 浮放控件（按钮 / 数字输入必须有 Panel 或 GroupBox 祖先；规格 0）
            report.AppendLine("[2e] 浮放控件数（规格 0）");
            int floating = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (!HasPanelAncestor(buttons[i]))
                    floating++;
            }
            List<NumericUpDown> numerics = new List<NumericUpDown>();
            UiControlFinder.Collect(this, numerics);
            for (int i = 0; i < numerics.Count; i++)
            {
                if (!HasPanelAncestor(numerics[i]))
                    floating++;
            }
            report.AppendLine("    按钮 " + buttons.Count + " + 数字输入 " + numerics.Count +
                "，浮放 " + floating + " 个：" + (floating == 0 ? "OK" : "FAIL"));
            if (floating > 0)
                failures++;

            // ③ 现场参数面：设备卡只有 5 个绝对 counts 输入，没有重复 / 角度 / 比例类输入
            report.AppendLine("[3] 设备参数面 = 5 个 counts 参数（无重复输入）");
            System.Windows.Forms.Control deviceParams = UiControlFinder.FindByName(this, "cardDevice");
            List<NumericUpDown> deviceNumerics = new List<NumericUpDown>();
            if (deviceParams != null)
                UiControlFinder.Collect(deviceParams, deviceNumerics);
            string[] requiredParams =
            {
                "numPendulumZeroCount", "numPendulum360Count", "numArmZeroCount",
                "numLimitMinCounts", "numLimitMaxCounts"
            };
            int presentParams = 0;
            for (int i = 0; i < requiredParams.Length; i++)
            {
                if (deviceParams != null && UiControlFinder.FindByName(deviceParams, requiredParams[i]) != null)
                    presentParams++;
            }
            // 旧的多余输入必须真的不在树上：细分 / 每圈计数（200 × 细分）与它们互相反推的联动。
            bool duplicatesGone =
                UiControlFinder.FindByName(this, "numMicroSteps") == null &&
                UiControlFinder.FindByName(this, "numArmCountsPerRev") == null;
            bool fiveOnly = presentParams == requiredParams.Length && deviceNumerics.Count == requiredParams.Length;
            report.AppendLine("    5 个参数输入 " + presentParams + "/5，设备卡数字输入共 " + deviceNumerics.Count +
                "（期望 5）；细分 / 每圈计数输入已删除=" + duplicatesGone +
                "：" + ((fiveOnly && duplicatesGone) ? "OK" : "FAIL"));
            if (!fiveOnly || !duplicatesGone)
                failures++;

            // 数字框量程必须原样接受 -54000 / +54000（不设角度 / 行程上限的界面证据）。
            bool rangeOk = _limitMinCountsBox.Minimum <= -54000m && _limitMaxCountsBox.Maximum >= 54000m;
            decimal savedMin = _limitMinCountsBox.Value;
            decimal savedMax = _limitMaxCountsBox.Value;
            _suppressCalibrationEvents = true;
            try
            {
                _limitMinCountsBox.Value = -54000m;
                _limitMaxCountsBox.Value = 54000m;
                rangeOk = rangeOk && _limitMinCountsBox.Value == -54000m && _limitMaxCountsBox.Value == 54000m;
            }
            finally
            {
                _limitMinCountsBox.Value = savedMin;
                _limitMaxCountsBox.Value = savedMax;
                _suppressCalibrationEvents = false;
            }
            report.AppendLine("    限位框可原样输入 -54000 / +54000：" + (rangeOk ? "OK" : "FAIL"));
            if (!rangeOk)
                failures++;

            // ④ PID 唯一性 / 两块参数零交叉
            report.AppendLine("[4] PID 参数只在一个卡片 / 参数块零交叉");
            List<NumericUpDown> pidBoxes = new List<NumericUpDown>();
            UiControlFinder.Collect(this, pidBoxes);
            int pidCount = 0;
            int pidOutside = 0;
            System.Windows.Forms.Control pidCard = UiControlFinder.FindByName(this, "cardPid");
            System.Windows.Forms.Control deviceCard = UiControlFinder.FindByName(this, "cardDevice");
            for (int i = 0; i < pidBoxes.Count; i++)
            {
                string name = pidBoxes[i].AccessibleName ?? "";
                if (name.StartsWith("numPid", StringComparison.Ordinal))
                {
                    pidCount++;
                    if (pidCard == null || !IsDescendant(pidBoxes[i], pidCard))
                        pidOutside++;
                }
            }
            bool pidOk = pidCount == 3 && pidOutside == 0 && pidCard != null;
            report.AppendLine("    平衡 PID 输入框 " + pidCount + " 个（期望 3：numPidKp/Ki/Kd），卡外 " +
                pidOutside + " 个：" + (pidOk ? "OK" : "FAIL"));
            if (!pidOk)
                failures++;

            bool crossPid = deviceCard != null && ContainsNamePrefix(deviceCard, "numPid");
            bool crossDevice = pidCard != null && (ContainsNamePrefix(pidCard, "numArmCountsPerRev") ||
                ContainsNamePrefix(pidCard, "numLimit") || ContainsNamePrefix(pidCard, "numMicroSteps") ||
                ContainsNamePrefix(pidCard, "numPendulumCpr"));
            report.AppendLine("    PID 卡含设备字段 " + crossDevice + "；设备卡含 PID 字段 " + crossPid +
                "：" + (!crossDevice && !crossPid ? "OK" : "FAIL"));
            if (crossDevice || crossPid)
                failures++;

            // ⑤ 日志 / 左栏 / 越界
            int lineHeight = _logBox == null ? 0 : Math.Max(1, _logBox.Font.Height);
            int visibleLines = _logBox == null || lineHeight <= 0 ? 0 : _logBox.ClientSize.Height / lineHeight;
            bool logOk = visibleLines >= 3;
            report.AppendLine("[5] 日志可见行数 / 左栏宽度 / 越界");
            report.AppendLine("    日志卡可见 " + visibleLines + " 行（判据 ≥3）：" + (logOk ? "OK" : "FAIL"));
            if (!logOk)
                failures++;

            System.Windows.Forms.Control rail = UiControlFinder.FindByName(this, "leftColumn");
            int railWidth = rail == null ? -1 : rail.Width;
            bool railOk = railWidth >= 200 && railWidth <= 220;
            report.AppendLine("    左栏宽度 " + railWidth + " px（判据 200–220）：" + (railOk ? "OK" : "FAIL"));
            if (!railOk)
                failures++;

            int overflow = 0;
            string firstOverflow = null;
            for (int i = 0; i < containers.Count; i++)
            {
                System.Windows.Forms.Control card = containers[i];
                overflow += CountOverflow(card, card, ref firstOverflow);
            }
            report.AppendLine("    卡片内子控件越界 " + overflow + " 个：" + (overflow == 0 ? "OK" : "FAIL" +
                (firstOverflow == null ? "" : "（首个：" + firstOverflow + "）")));
            if (overflow > 0)
                failures++;

            // ⑥ 小屏 / 极端宽高比：程序化把客户区依次设为常见笔记本尺寸，逐尺寸读控件内存矩形。
            report.AppendLine("[6] 小屏 / 极端宽高比布局（程序化改客户区，读内存矩形，不截图）");
            if (!AuditResponsiveSizes(report))
                failures++;

            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        /// <summary>响应式布局自检的客户区尺寸清单：基线 + 常见笔记本 + 两个极端宽高比。</summary>
        private static readonly Size[] ResponsiveClientSizes =
        {
            new Size(1280, 1000),   // 设计稿基线：必须仍与改造前一致（表盘跨 2 格、PDO 单格）
            new Size(1152, 864),    // 4:3 工程笔记本
            new Size(1024, 768),    // 老式 4:3 / 投影
            new Size(1366, 768),    // 最常见的 16:9 笔记本
            new Size(2560, 1080),   // 超宽屏：验证表盘不挤扁邻卡、自身不出容器
            new Size(1024, 1400),   // 竖屏：验证表盘按高度放大时仍受宽度约束
            new Size(960, 640)      // 窗口最小客户区（MinimumSize）：可用性下限的实证
        };

        /// <summary>
        /// 逐尺寸响应式自检：每个尺寸下统计具名控件（按钮 / 数字框 / 卡片 / 带名标签与容器）的
        /// 裁切数、同父重叠对数、0 尺寸数、23 个按钮的视口可见 / 滚动容纳数，以及表盘跨格与面积比。
        /// 判定用控件内存矩形与父容器 ClientSize，不截图；判完恢复原客户区。
        /// </summary>
        private bool AuditResponsiveSizes(StringBuilder report)
        {
            bool allOk = true;
            Size savedClient = ClientSize;
            try
            {
                for (int i = 0; i < ResponsiveClientSizes.Length; i++)
                {
                    Size size = ResponsiveClientSizes[i];
                    ClientSize = size;
                    PerformLayout();
                    Application.DoEvents();
                    // 滚动条出现会改变可视区宽度：把两个宿主的内容尺寸再同步一次，读的是稳定态。
                    // 程序化改 ClientSize 只保证消息同步处理，根布局的列宽生效要再走完整布局循环；
                    // 跑两轮（宿主尺寸 ↔ 列宽互相影响）后取稳定值。
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Panel pageHostNow = UiControlFinder.FindByName(this, "mainPageScroll") as Panel;
                        Panel railHostNow = UiControlFinder.FindByName(this, "leftRailScroll") as Panel;
                        System.Windows.Forms.Control pageNow = UiControlFinder.FindByName(this, "mainPage");
                        System.Windows.Forms.Control railNow = UiControlFinder.FindByName(this, "leftColumn");
                        if (pageHostNow != null && pageNow != null)
                            SyncScrollContent(pageHostNow, pageNow, MIN_MAIN_PAGE_WIDTH, MIN_MAIN_PAGE_HEIGHT);
                        if (railHostNow != null && railNow != null)
                            SyncScrollContent(railHostNow, railNow, 0, MIN_LEFT_COLUMN_HEIGHT);
                        UpdateLeftRailWidth();
                        PerformLayout();
                        if (_rootLayout != null)
                            _rootLayout.PerformLayout();
                        Application.DoEvents();
                    }
                    if (!AuditResponsiveSize(size, report))
                        allOk = false;
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                report.AppendLine("    响应式自检异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                ClientSize = savedClient;
                PerformLayout();
                Application.DoEvents();
            }
            return allOk;
        }

        /// <summary>单个尺寸的响应式判定；返回 false = 该尺寸有裁切 / 重叠 / 0 尺寸 / 按钮出容器 / 表盘异常。</summary>
        private bool AuditResponsiveSize(Size requested, StringBuilder report)
        {
            System.Windows.Forms.Control page = UiControlFinder.FindByName(this, "mainPage");
            System.Windows.Forms.Control leftColumn = UiControlFinder.FindByName(this, "leftColumn");
            Panel pageHost = UiControlFinder.FindByName(this, "mainPageScroll") as Panel;
            Panel railHost = UiControlFinder.FindByName(this, "leftRailScroll") as Panel;

            List<System.Windows.Forms.Control> named = new List<System.Windows.Forms.Control>();
            CollectNamedControls(this, named);

            int zeroSize = 0;
            int clipped = 0;
            string firstClip = null;
            for (int i = 0; i < named.Count; i++)
            {
                System.Windows.Forms.Control control = named[i];
                if (!control.Visible)
                    continue;
                if (control.Width <= 0 || control.Height <= 0)
                    zeroSize++;
                // 滚动内容根（整页 / 左栏）本来就允许大于滚动视口；它内部控件仍逐一核对。
                if (ReferenceEquals(control, page) || ReferenceEquals(control, leftColumn))
                    continue;
                if (control.Parent == null)
                    continue;
                Rectangle local = control.Parent.RectangleToClient(
                    control.RectangleToScreen(control.ClientRectangle));
                if (local.Left < -2 || local.Top < -2 ||
                    local.Right > control.Parent.ClientSize.Width + 2 ||
                    local.Bottom > control.Parent.ClientSize.Height + 2)
                {
                    clipped++;
                    if (firstClip == null)
                        firstClip = (control.AccessibleName ?? control.Name ?? control.GetType().Name) + " " + local;
                }
            }

            // 同父控件两两重叠：TableLayoutPanel 的正常排布不应产生任何交集。
            Dictionary<System.Windows.Forms.Control, List<System.Windows.Forms.Control>> byParent =
                new Dictionary<System.Windows.Forms.Control, List<System.Windows.Forms.Control>>();
            for (int i = 0; i < named.Count; i++)
            {
                System.Windows.Forms.Control control = named[i];
                if (!control.Visible || control.Parent == null)
                    continue;
                List<System.Windows.Forms.Control> siblings;
                if (!byParent.TryGetValue(control.Parent, out siblings))
                {
                    siblings = new List<System.Windows.Forms.Control>();
                    byParent.Add(control.Parent, siblings);
                }
                siblings.Add(control);
            }
            int overlaps = 0;
            string firstOverlap = null;
            foreach (KeyValuePair<System.Windows.Forms.Control, List<System.Windows.Forms.Control>> pair in byParent)
            {
                List<System.Windows.Forms.Control> siblings = pair.Value;
                for (int a = 0; a < siblings.Count; a++)
                {
                    for (int b = a + 1; b < siblings.Count; b++)
                    {
                        Rectangle ra = siblings[a].Bounds;
                        Rectangle rb = siblings[b].Bounds;
                        Rectangle hit = Rectangle.Intersect(ra, rb);
                        if (hit.Width > 0 && hit.Height > 0)
                        {
                            overlaps++;
                            if (firstOverlap == null)
                                firstOverlap = (siblings[a].AccessibleName ?? siblings[a].Name ?? "?") + " ↔ " +
                                               (siblings[b].AccessibleName ?? siblings[b].Name ?? "?");
                        }
                    }
                }
            }

            // 23 个按钮：要么在当前视口内完整可见，要么被滚动容器完整容纳（可以滚出来）。
            List<Button> buttons = new List<Button>();
            UiControlFinder.Collect(this, buttons);
            int visibleButtons = 0;
            int containedButtons = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                Button button = buttons[i];
                Rectangle buttonOnForm = RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                bool inPage = page != null && IsDescendant(button, page);
                Panel host = inPage ? pageHost : railHost;
                if (host != null)
                {
                    Rectangle viewport = RectangleToClient(host.RectangleToScreen(host.ClientRectangle));
                    Rectangle shown = Rectangle.Intersect(viewport, buttonOnForm);
                    if (shown.Width >= button.Width - 1 && shown.Height >= button.Height - 1)
                        visibleButtons++;
                }
                System.Windows.Forms.Control scrollRoot = inPage ? page : leftColumn;
                if (scrollRoot != null)
                {
                    Rectangle inRoot = scrollRoot.RectangleToClient(
                        button.RectangleToScreen(button.ClientRectangle));
                    if (inRoot.Left >= -2 && inRoot.Top >= -2 &&
                        inRoot.Right <= scrollRoot.ClientSize.Width + 2 &&
                        inRoot.Bottom <= scrollRoot.ClientSize.Height + 2)
                        containedButtons++;
                }
            }
            bool buttonsOk = buttons.Count > 0 && containedButtons == buttons.Count;

            // 表盘：跨 2 格、面积 ≥ 单格参考卡 1.9 倍、自身在单页内且不出容器；相邻卡不得被挤成 0 宽。
            TableLayoutPanel pageGrid = page as TableLayoutPanel;
            CardPanel dial = UiControlFinder.FindByName(this, "cardDial") as CardPanel;
            CardPanel live = UiControlFinder.FindByName(this, "cardStepLive") as CardPanel;
            CardPanel reference = UiControlFinder.FindByName(this, "cardPdo") as CardPanel;
            CardPanel pid = UiControlFinder.FindByName(this, "cardPid") as CardPanel;
            CardPanel device = UiControlFinder.FindByName(this, "cardDevice") as CardPanel;
            CardPanel angle = UiControlFinder.FindByName(this, "cardAngle") as CardPanel;
            CardPanel log = UiControlFinder.FindByName(this, "cardLog") as CardPanel;
            PendulumDialView dialView = dial == null ? null : UiControlFinder.FindByName(dial, "viewPendulumDial") as PendulumDialView;

            int dialCells = 0;
            double dialRatio = 0.0;
            bool dialInside = false;
            bool liveSingle = false;
            if (pageGrid != null && dial != null && live != null && reference != null &&
                ReferenceEquals(dial.Parent, pageGrid) && ReferenceEquals(live.Parent, pageGrid))
            {
                dialCells = pageGrid.GetColumnSpan(dial) * pageGrid.GetRowSpan(dial);
                liveSingle = pageGrid.GetColumnSpan(live) * pageGrid.GetRowSpan(live) == 1;
                double referenceArea = Math.Max(1.0, (double)reference.Width * reference.Height);
                dialRatio = (double)dial.Width * dial.Height / referenceArea;
                dialInside = dial.Left >= -2 && dial.Top >= -2 &&
                             dial.Right <= pageGrid.ClientSize.Width + 2 &&
                             dial.Bottom <= pageGrid.ClientSize.Height + 2;
            }
            bool neighborsOk = pid != null && device != null && angle != null && log != null &&
                               pid.Width > 0 && device.Width > 0 && angle.Width > 0 && log.Width > 0;
            bool dialOk = dialCells == 2 && dialRatio >= 1.9 && dialInside && liveSingle && neighborsOk &&
                          dialView != null && dialView.Width > 0 && dialView.Height > 0;

            int visibleLines = _logBox == null ? 0 :
                _logBox.ClientSize.Height / Math.Max(1, _logBox.Font.Height);
            // 左栏内容宽度必须保持 200–220（按钮不因滚动条变窄）；内容超出视口时滚动必须真的可用，
            // 否则“滚动容器容纳”只是把控件藏到了够不着的地方。
            bool railContentOk = leftColumn != null && leftColumn.Width >= 200 && leftColumn.Width <= 220;
            bool pageScrollOk = page == null || pageHost == null ||
                                page.Height <= pageHost.ClientSize.Height || pageHost.VerticalScroll.Visible;
            bool railScrollOk = leftColumn == null || railHost == null ||
                                leftColumn.Height <= railHost.ClientSize.Height || railHost.VerticalScroll.Visible;
            bool pageHorizontalOk = page == null || pageHost == null ||
                                     page.Width <= pageHost.ClientSize.Width || pageHost.HorizontalScroll.Visible;
            bool ok = clipped == 0 && overlaps == 0 && zeroSize == 0 && buttonsOk && dialOk &&
                      railContentOk && pageScrollOk && railScrollOk && pageHorizontalOk;

            string pageViewport = pageHost == null ? "?" : pageHost.ClientSize.Width + "×" + pageHost.ClientSize.Height;
            string railViewport = railHost == null ? "?" : railHost.ClientSize.Width + "×" + railHost.ClientSize.Height;
            string pageScroll = pageHost == null ? "?" :
                (pageHost.VerticalScroll.Visible ? "纵向滚动可用" : "无纵向滚动") +
                "，显示区 " + pageHost.DisplayRectangle.Width + "×" + pageHost.DisplayRectangle.Height;
            string railScroll = railHost == null ? "?" :
                (railHost.VerticalScroll.Visible ? "纵向滚动可用" : "无纵向滚动") +
                "，显示区 " + railHost.DisplayRectangle.Width + "×" + railHost.DisplayRectangle.Height;
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "    {0}×{1}：具名控件 {2}，被裁切 {3}，重叠 {4} 对，0 尺寸 {5}，按钮 {6} 个（视口内完整 {7} / 滚动容器容纳 {8}）；" +
                "表盘跨 {9} 格 面积比 {10:F2}，相邻卡宽 {11}/{12}/{13}/{14}；单页 {15}×{16}（视口 {17}，{18}），左栏 {19}×{20}（视口 {21}，{22}）；日志可见 {23} 行；实际客户区 {25}×{26}，根左列 {27}：{24}",
                requested.Width, requested.Height, named.Count, clipped, overlaps, zeroSize,
                buttons.Count, visibleButtons, containedButtons,
                dialCells, dialRatio,
                pid == null ? 0 : pid.Width, device == null ? 0 : device.Width,
                angle == null ? 0 : angle.Width, log == null ? 0 : log.Width,
                page == null ? 0 : page.Width, page == null ? 0 : page.Height, pageViewport, pageScroll,
                leftColumn == null ? 0 : leftColumn.Width, leftColumn == null ? 0 : leftColumn.Height,
                railViewport, railScroll,
                visibleLines, ok ? "OK" : "FAIL",
                ClientSize.Width, ClientSize.Height,
                _rootLayout == null ? 0 : (int)_rootLayout.ColumnStyles[0].Width));

            if (clipped > 0)
                report.AppendLine("      首个裁切：" + firstClip);
            if (overlaps > 0)
                report.AppendLine("      首对重叠：" + firstOverlap);
            return ok;
        }

        /// <summary>收集全部具名控件（按钮 / 数字框 / 卡片 / 任何带 Name 或 AccessibleName 的控件）；滚动宿主也计入。</summary>
        private static void CollectNamedControls(System.Windows.Forms.Control root,
            List<System.Windows.Forms.Control> sink)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            for (int i = 1; i < all.Count; i++)   // 不含 root 本身（与原递归口径一致）
            {
                System.Windows.Forms.Control child = all[i];
                bool interesting = child is Button || child is NumericUpDown || child is CardPanel ||
                                   !string.IsNullOrEmpty(child.Name) || !string.IsNullOrEmpty(child.AccessibleName);
                if (interesting)
                    sink.Add(child);
            }
        }

        /// <summary>单页上必须常驻的 8 张卡片（左栏 4 块操作卡 + 操作提示卡不在此列）。</summary>
        private static readonly string[] MainPageCardNames =
        {
            "cardStepLive", "cardPdo", "cardPid", "cardDevice",
            "cardCurrent", "cardDial", "cardAngle", "cardLog"
        };

        /// <summary>
        /// 主视野单页自检：8 张卡片都挂在同一张 mainPage 上且全部可见、尺寸非零，全树无 TabControl；
        /// 角度 / PID / 换算 / 限位与 PDO 实时数据 11 个读数逐项打印（控件名 + 文案 + 当前文本）。
        /// </summary>
        private bool AuditMainPage(StringBuilder report)
        {
            // 窗口尺寸契约：默认设计稿 1280×1000 一屏全见；最小客户区 960×640，小于设计稿时
            // 左右两栏各自滚动（[6] 逐尺寸验证）。不设上限、可自由缩放。
            Size nonClient = new Size(Math.Max(0, Width - ClientSize.Width), Math.Max(0, Height - ClientSize.Height));
            bool clientOk = ClientSize.Width >= MIN_CLIENT_WIDTH && ClientSize.Height >= MIN_CLIENT_HEIGHT;
            bool minOk = MinimumSize.Width - nonClient.Width >= MIN_CLIENT_WIDTH &&
                         MinimumSize.Height - nonClient.Height >= MIN_CLIENT_HEIGHT;
            bool noMax = MaximumSize.IsEmpty;
            bool resizable = FormBorderStyle == FormBorderStyle.Sizable && MaximizeBox;
            bool sizeOk = clientOk && minOk && noMax && resizable;
            report.AppendLine("    窗口：客户区 " + ClientSize.Width + " × " + ClientSize.Height +
                "（默认设计稿 " + DpiLock.DesignWidth + " × " + DpiLock.DesignHeight +
                "，可滚动下限 " + MIN_CLIENT_WIDTH + " × " + MIN_CLIENT_HEIGHT + "）；最小外框 " +
                MinimumSize.Width + " × " + MinimumSize.Height + "；上限 " +
                (noMax ? "未设" : MaximumSize.Width + " × " + MaximumSize.Height) +
                "；可缩放 " + resizable + "：" + (sizeOk ? "OK" : "FAIL"));

            System.Windows.Forms.Control page = UiControlFinder.FindByName(this, "mainPage");
            bool pageOk = page != null && page.Visible && page.Width > 0 && page.Height > 0;
            report.AppendLine("    单页容器 mainPage：" +
                (pageOk ? "OK（" + page.Width + " × " + page.Height + "）" : "FAIL（未找到 / 零尺寸）"));

            int onPage = 0;
            string firstBad = null;
            for (int i = 0; i < MainPageCardNames.Length; i++)
            {
                System.Windows.Forms.Control card = UiControlFinder.FindByName(this, MainPageCardNames[i]);
                bool okCard = card != null && page != null && IsDescendant(card, page) &&
                              card.Visible && card.Width > 0 && card.Height > 0;
                if (okCard)
                    onPage++;
                else if (firstBad == null)
                    firstBad = MainPageCardNames[i];
            }
            bool cardsOk = onPage == MainPageCardNames.Length;
            report.AppendLine("    mainPage 上的卡片 " + onPage + " / " + MainPageCardNames.Length +
                (firstBad == null ? "" : "（首个缺失 / 零尺寸：" + firstBad + "）") +
                "（判据：单页常驻、不用切页）：" + (cardsOk ? "OK" : "FAIL"));
            bool ok = sizeOk && pageOk && cardsOk;

            // 单页布局下不允许再出现页签容器：「参数与换算」这类折叠视图已并入同一页。
            int tabCount = 0;
            CollectTabs(this, ref tabCount);
            bool noTabs = tabCount == 0;
            report.AppendLine("    页签容器 TabControl " + tabCount + " 个（判据 0）：" + (noTabs ? "OK" : "FAIL"));
            ok &= noTabs;

            string[] pageControls =
            {
                "lblTheta", "lblPsi", "lblOmega", "lblPhi", "lblEnergy", "lblEnergyTarget",
                "lblNearUpright", "lblPendulumCount", "lblPendulumCountCh2",
                "lblPendulumFreqCh1", "numPidKp", "numPendulumZeroCount", "numPendulum360Count",
                "numArmZeroCount", "numLimitMinCounts", "numLimitMaxCounts",
                "btnSetPendulumZero", "btnSetPendulum360", "btnPidTuning"
            };
            int readable = 0;
            for (int i = 0; i < pageControls.Length; i++)
            {
                System.Windows.Forms.Control control = UiControlFinder.FindByName(this, pageControls[i]);
                if (control != null && control.Width > 0 && control.Height > 0)
                    readable++;
            }
            bool paramsOk = readable == pageControls.Length;
            report.AppendLine("    角度 / PID / 5 项 counts 参数 / 限位控件可读 " + readable + " / " +
                pageControls.Length + "（单页常驻）：" + (paramsOk ? "OK" : "FAIL"));
            ok &= paramsOk;

            string[] realtimeControls =
            {
                "lblPdoTargetPosition", "lblPdoActualPosition", "lblFollowError", "lblStepVelocity",
                "lblStepTorque", "lblPdoStatusWord", "lblPdoErrorCode", "lblStepEnable", "lblPdoMode",
                "lblStepFeedback", "lblStepSlave"
            };
            report.AppendLine("    PDO 实时数据读数清单（控件名「文案」= 当前文本）");
            int liveOk = 0;
            for (int i = 0; i < realtimeControls.Length; i++)
            {
                System.Windows.Forms.Control control = UiControlFinder.FindByName(this, realtimeControls[i]);
                bool present = control != null && control.Width > 0 && control.Height > 0;
                if (present) liveOk++;
                report.AppendLine("      " + ControlDump(realtimeControls[i], control) +
                    (present ? "" : "（缺失 / 零尺寸）"));
            }
            bool liveOkAll = liveOk == realtimeControls.Length;
            report.AppendLine("    PDO 实时数据读数可读 " + liveOk + " / " + realtimeControls.Length + "：" +
                (liveOkAll ? "OK" : "FAIL"));
            ok &= liveOkAll;

            // 读数绑定自检：注入一帧 live 遥测跑一次生产刷新，确认读数真的从遥测字段取值，
            // 而不是「控件在、值永远 —」。检查完立即恢复未连接，后续自检各自准备状态。
            string actualPos = "—";
            string targetPos = "—";
            string modeText = "—";
            string slaveText = "—";
            string feedbackText = "—";
            string torqueText = "—";
            try
            {
                _engine.InjectUiAuditState(EngineState.Enabled, 12345, true, false);
                Application.DoEvents();
                PendulumTelemetry injected;
                bool injectedLive = TryReadTelemetry(out injected);
                RefreshCards(_engine.State, injectedLive, injected);
                actualPos = _pdoActualPosLabel.Text;
                targetPos = _pdoTargetPosLabel.Text;
                modeText = _pdoModeLabel.Text;
                slaveText = _stepSlaveLabel.Text;
                feedbackText = _stepFeedbackLabel.Text;
                torqueText = _stepTorqueLabel.Text;
            }
            catch (Exception ex)
            {
                report.AppendLine("    读数绑定注入异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                _engine.InjectUiAuditState(EngineState.Disconnected, 0, false, false);
                RefreshCards(EngineState.Disconnected, false, default(PendulumTelemetry));
            }

            bool bindingOk = ContainsNumber(actualPos, 12345) && ContainsNumber(targetPos, 12345) &&
                             modeText.IndexOf("CSP", StringComparison.Ordinal) >= 0 &&
                             string.Equals(slaveText, "OP", StringComparison.Ordinal) &&
                             string.Equals(feedbackText, "实测", StringComparison.Ordinal) &&
                             string.Equals(torqueText, "未映射", StringComparison.Ordinal);
            report.AppendLine("    读数绑定（注入 live 快照 12345 counts 后）：实际位置=" + actualPos +
                " · 目标位置=" + targetPos + " · 模式=" + modeText + " · 从站=" + slaveText +
                " · 反馈=" + feedbackText + " · 力矩=" + torqueText + "：" + (bindingOk ? "OK" : "FAIL"));
            ok &= bindingOk;
            return ok;
        }

        /// <summary>
        /// 视野比例自检：杆角度表盘必须占 2 格、面积 ≥ 单格参考卡（同页「总线与采样」卡，恒为 1 格）的 1.9 倍；
        /// PDO 实时数据必须只占 1 格、面积 ≤ 单格参考卡的 1.15 倍。
        /// <para>
        /// 用「同一页的单格卡」当尺子，窗口被放大 / 缩小（含 WinRM 小屏）判据都成立，不写死设计稿像素。
        /// 改动前（设计稿 1280×1000）：表盘卡 ≈ 337×257 px = 0.82 张单格卡；PDO 实时数据跨两列 ≈ 685×353 px = 2.28 张。
        /// 因此「表盘 ≥ 1.9 张单格卡」即满足「≥ 改动前 2 倍面积」，「PDO ≤ 1.15 张」即「单格且明显小于原值」。
        /// 表盘跨回一格、把两格压扁到不足两张单格卡、或 PDO 又跨回两列，都会红。
        /// </para>
        /// </summary>
        private bool AuditDialPdoRatio(StringBuilder report)
        {
            TableLayoutPanel page = UiControlFinder.FindByName(this, "mainPage") as TableLayoutPanel;
            CardPanel dial = UiControlFinder.FindByName(this, "cardDial") as CardPanel;
            CardPanel live = UiControlFinder.FindByName(this, "cardStepLive") as CardPanel;
            CardPanel reference = UiControlFinder.FindByName(this, "cardPdo") as CardPanel;
            if (page == null || dial == null || live == null || reference == null ||
                !ReferenceEquals(reference.Parent, page))
            {
                report.AppendLine("    mainPage / cardDial / cardStepLive / cardPdo 缺失或参考卡不在单页上：FAIL");
                return false;
            }

            bool dialOnPage = ReferenceEquals(dial.Parent, page);
            bool liveOnPage = ReferenceEquals(live.Parent, page);
            int dialCells = dialOnPage ? page.GetColumnSpan(dial) * page.GetRowSpan(dial) : 0;
            int liveCells = liveOnPage ? page.GetColumnSpan(live) * page.GetRowSpan(live) : 0;
            int refCells = page.GetColumnSpan(reference) * page.GetRowSpan(reference);

            double referenceArea = Math.Max(1.0, (double)reference.Width * reference.Height);
            double dialArea = (double)dial.Width * dial.Height;
            double liveArea = (double)live.Width * live.Height;
            double dialRatio = dialArea / referenceArea;
            double liveRatio = liveArea / referenceArea;

            bool dialOk = dialOnPage && dialCells == 2 && dialRatio >= 1.9;
            bool liveOk = liveOnPage && liveCells == 1 && refCells == 1 && liveRatio <= 1.15;
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "    杆角度 cardDial {0} × {1} px，跨 {2} 格，面积 = 单格参考卡的 {3:F2} 倍（要求 = 2 格且 ≥ 1.90）：{4}",
                dial.Width, dial.Height, dialCells, dialRatio, dialOk ? "OK" : "FAIL"));
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "    PDO 实时数据 cardStepLive {0} × {1} px，占 {2} 格，面积 = 单格参考卡的 {3:F2} 倍（要求 = 1 格且 ≤ 1.15）：{4}",
                live.Width, live.Height, liveCells, liveRatio, liveOk ? "OK" : "FAIL"));
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "    单格参考卡 cardPdo {0} × {1} px（{2} 格）；改动前表盘 = 0.82 张单格卡、PDO 实时数据 = 2.28 张",
                reference.Width, reference.Height, refCells));
            return dialOk && liveOk;
        }

        /// <summary>文本里是否含指定整数（忽略千分位 / 小数点分隔符，避免受区域设置影响）。</summary>
        private static bool ContainsNumber(string text, int value)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            string digits = text.Replace(",", "").Replace(".", "").Replace(" ", "");
            return digits.IndexOf(value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0;
        }

        /// <summary>统计界面树里的页签容器（单页布局判据 = 0）。</summary>
        private static void CollectTabs(System.Windows.Forms.Control root, ref int count)
        {
            List<TabControl> tabs = new List<TabControl>();
            UiControlFinder.Collect(root, tabs);
            count += tabs.Count;
        }

        /// <summary>读数格打印：「控件名「文案」= 当前文本」；文案取自同格左侧的说明标签。</summary>
        private static string ControlDump(string accessibleName, System.Windows.Forms.Control control)
        {
            if (control == null)
                return accessibleName + " 缺失";
            string caption = "";
            if (control.Parent != null && control.Parent.Controls.Count >= 2)
            {
                Label first = control.Parent.Controls[0] as Label;
                if (first != null && !ReferenceEquals(first, control))
                    caption = first.Text;
            }
            string text = control.Text;
            return accessibleName + "「" + caption + "」= " + (string.IsNullOrEmpty(text) ? "（空）" : text);
        }

        /// <summary>
        /// 界面契约自检（收紧）：按钮清单 / 所属卡片 / 单杆措辞。
        /// 判据：
        /// ① 必需按钮各出现且仅出现一次；已删按钮（平衡 / 系统辨识 / 测量流程 / 整圈捕获）不存在；
        /// ② 连接卡 = 急停 / 连接 / 断开 / 复位，运动卡 = 起摆 / 停止 / 使能，参数卡 = 保存 / 应用（不保存）/ 载入 / 恢复默认，
        ///    PID 卡有「PID 调参…」，设备卡 = 五项标定；
        /// ③ 按钮与标签文本不得出现「整圈捕获」「系统辨识」「测量流程」及操作顺序类备注；
        /// ④ 每张操作卡最多 1 个主操作（蓝底）按钮：蓝底只标「把系统推进一步」的那一个动作，
        ///    同级动作必须同色（「驱动臂零点」曾经在一排白底采集按钮里单独用蓝底，就是这条要防的）。
        /// 比旧版「整圈捕获文案」判据更严：旧判据只查一段已删除功能的文案，本判据查整套交付界面。
        /// </summary>
        public string RunContractAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 界面契约自检 ===");
            int failures = 0;

            string[] required = ButtonNames;
            string[] removed = { "btnBalance", "btnIdentify", "btnMeasure", "btnReleaseCapture" };
            report.AppendLine("[1] 按钮清单（必需 23 个各一次；已删 4 个 0 次）");
            for (int i = 0; i < required.Length; i++)
            {
                int count = CountByAccessibleName(this, required[i]);
                bool ok = count == 1;
                if (!ok) failures++;
                report.AppendLine("    " + required[i] + " × " + count + "：" + (ok ? "OK" : "FAIL"));
            }
            for (int i = 0; i < removed.Length; i++)
            {
                int count = CountByAccessibleName(this, removed[i]);
                bool ok = count == 0;
                if (!ok) failures++;
                report.AppendLine("    " + removed[i] + " × " + count + "（必须 0）：" + (ok ? "OK" : "FAIL"));
            }

            report.AppendLine("[2] 按钮所属卡片");
            bool placementOk =
                CheckButtonCard("btnSwingUp", "operationRun") &&
                CheckButtonCard("btnStop", "operationRun") &&
                CheckButtonCard("btnEnable", "operationRun") &&
                CheckButtonCard("btnReturnHome", "operationRun") &&
                CheckButtonCard("btnEStop", "operationConnect") &&
                CheckButtonCard("btnConnect", "operationConnect") &&
                CheckButtonCard("btnDisconnect", "operationConnect") &&
                CheckButtonCard("btnReset", "operationConnect") &&
                CheckButtonCard("btnSaveParams", "operationParams") &&
                CheckButtonCard("btnApplyParams", "operationParams") &&
                CheckButtonCard("btnLoadParams", "operationParams") &&
                CheckButtonCard("btnRestoreDefaults", "operationParams") &&
                CheckButtonCard("btnPidTuning", "cardPid") &&
                CheckButtonCard("btnSetArmZero", "cardDevice") &&
                CheckButtonCard("btnSetLeftLimit", "cardDevice") &&
                CheckButtonCard("btnSetRightLimit", "cardDevice") &&
                CheckButtonCard("btnSetPendulumZero", "cardDevice") &&
                CheckButtonCard("btnSetPendulum360", "cardDevice") &&
                CheckButtonCard("btnNormalizePendulumPose", "cardDevice") &&
                CheckButtonCard("btnClearCalibration", "operationCalibration") &&
                CheckButtonCard("btnNormalizeArmPose", "operationCalibration");
            if (!placementOk) failures++;
            report.AppendLine("    连接 / 运动 / 参数 / PID / 设备卡内按钮位置：" + (placementOk ? "OK" : "FAIL"));

            bool countsOk =
                CountButtonsInCard("operationConnect") == 4 &&
                CountButtonsInCard("operationRun") == 4 &&
                CountButtonsInCard("operationParams") == 4 &&
                CountButtonsInCard("operationCalibration") == 2 &&
                CountButtonsInCard("cardPid") == 1 &&
                CountButtonsInCard("cardDevice") == 6;
            if (!countsOk) failures++;
            report.AppendLine("    连接卡 4（含急停）/ 运动卡 4（起摆+停止+使能+回到零点）/ 参数卡 4 / 标定维护卡 2 / PID 卡 1 / 设备卡 6（5 个位置采集 + 规整化摆杆）：" + (countsOk ? "OK" : "FAIL"));

            // ②b 主操作分级：每张操作卡最多 1 个蓝底（主操作）按钮；蓝色只属于「把系统推进一步」的那个动作。
            //     同级动作必须同色 —— 设备卡 5 个采集按钮全体白底，任何一个回蓝都算 FAIL。
            string[] operationCards = { "operationConnect", "operationManual", "operationRun", "operationParams", "operationCalibration", "cardDevice" };
            bool hierarchyOk = true;
            for (int i = 0; i < operationCards.Length; i++)
            {
                int primaries = CountPrimaryButtonsInCard(operationCards[i]);
                bool ok = primaries >= 0 && primaries <= 1;
                if (!ok) hierarchyOk = false;
                report.AppendLine("    " + operationCards[i] + " 蓝底主操作 " + primaries +
                    "（上限 1）：" + (ok ? "OK" : "FAIL"));
            }
            if (!hierarchyOk) failures++;

            // 急停必须永远置顶：它得是左栏里 y 坐标最小的那张卡，且高于「停止」按钮。
            System.Windows.Forms.Control estop = UiControlFinder.FindByName(this, "btnEStop");
            System.Windows.Forms.Control stopBtn = UiControlFinder.FindByName(this, "btnStop");
            bool estopOnTop = estop != null && stopBtn != null &&
                              estop.PointToScreen(Point.Empty).Y < stopBtn.PointToScreen(Point.Empty).Y;
            if (!estopOnTop) failures++;
            report.AppendLine("    急停位于停止之上：" + (estopOnTop ? "OK" : "FAIL"));

            report.AppendLine("[3] 可见文本措辞（单杆 + 无操作顺序备注）");
            string[] banned =
            {
                // 「摆臂 / 摆杆」不再禁用：这 5 个现场参数本身就是用户指定的这两个词
                //（左限位 / 右限位 / 摆臂 0 点 / 摆杆零点 / 摆杆一圈的量）。
                "整圈捕获", "系统辨识", "测量流程",
                "先连接再使能", "按住才动", "松开即停", "急停随时可点",
                "未连接：按钮需要实时", "改零点 / 标定需先停止", "确认无误点",
                "手动把摆臂转满一圈", "点「系统辨识」", "点「整圈捕获」"
            };
            List<Button> buttons = new List<Button>();
            UiControlFinder.Collect(this, buttons);
            List<Label> labels = new List<Label>();
            UiControlFinder.Collect(this, labels);
            int hits = 0;
            for (int i = 0; i < buttons.Count; i++)
                hits += ScanBanned(buttons[i].Text, banned, "按钮 " + buttons[i].AccessibleName, report);
            for (int i = 0; i < labels.Count; i++)
                hits += ScanBanned(labels[i].Text, banned, "标签 " + labels[i].AccessibleName, report);
            if (hits > 0) failures++;
            report.AppendLine("    命中 " + hits + " 条：" + (hits == 0 ? "OK" : "FAIL"));

            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        /// <summary>23 按钮 × 七态矩阵；期望表独立写在审计侧，避免拿生产判据自己证明自己。</summary>
        public string RunButtonMatrixAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 按钮状态矩阵自检 ===");
            EngineState[] states =
            {
                EngineState.Disconnected, EngineState.Connected, EngineState.Enabled,
                EngineState.Swinging, EngineState.Balancing, EngineState.Stopped, EngineState.Fault
            };
            int failures = 0;
            report.AppendLine("状态顺序：未连接 / 已连接 / 已使能 / 起摆 / 平衡 / 已停 / 故障（1=可用，0=禁用）");
            for (int b = 0; b < ButtonNames.Length; b++)
            {
                string name = ButtonNames[b];
                string actual = "";
                string expected = "";
                for (int i = 0; i < states.Length; i++)
                {
                    UiButtonStateSnapshot s = new UiButtonStateSnapshot();
                    s.State = states[i];
                    s.LiveArm = true;
                    s.EStopLatch = states[i] == EngineState.Fault;
                    bool a = IsButtonEnabledForAudit(name, s);
                    bool e = ExpectedButtonEnabled(name, states[i]);
                    actual += a ? "1" : "0";
                    expected += e ? "1" : "0";
                    if (a != e) failures++;
                }
                bool ok = actual == expected;
                report.AppendLine("    " + name + " 实际 " + actual + " 期望 " + expected + "：" +
                    (ok ? "OK" : "FAIL"));
            }

            // 采集按钮不靠「禁用」表达前提：没有新鲜计数时按钮仍然可点，点击后生产入口给中文原因。
            UiButtonStateSnapshot noLive = new UiButtonStateSnapshot();
            noLive.State = EngineState.Connected;
            noLive.LiveArm = false;
            string[] captureButtons =
            {
                "btnSetArmZero", "btnSetLeftLimit", "btnSetRightLimit",
                "btnSetPendulumZero", "btnSetPendulum360"
            };
            for (int i = 0; i < captureButtons.Length; i++)
            {
                bool enabled = IsButtonEnabledForAudit(captureButtons[i], noLive);
                if (!enabled) failures++;
                report.AppendLine("    无新鲜计数 " + captureButtons[i] + "=" + enabled + "（期望 True：可点并提示原因）");
            }

            UiButtonStateSnapshot busyFault = new UiButtonStateSnapshot();
            busyFault.State = EngineState.Fault;
            busyFault.LiveArm = true;
            busyFault.Busy = true;
            busyFault.EStopLatch = true;
            bool stopSafety = IsButtonEnabledForAudit("btnStop", busyFault);
            bool estopSafety = IsButtonEnabledForAudit("btnEStop", busyFault);
            if (!stopSafety || !estopSafety) failures++;
            report.AppendLine("    故障 + 操作忙：停止=" + stopSafety + " 急停=" + estopSafety + "（两者必须 True）");

            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        private static bool ExpectedButtonEnabled(string name, EngineState state)
        {
            bool connected = state != EngineState.Disconnected;
            bool idle = state == EngineState.Disconnected || state == EngineState.Connected ||
                        state == EngineState.Stopped;
            switch (name)
            {
                case "btnConnect": return state == EngineState.Disconnected;
                case "btnDisconnect": return connected;
                case "btnEnable": return idle;
                case "btnStop": return connected;
                case "btnReset": return state == EngineState.Fault;
                case "btnJogLeft":
                case "btnJogRight": return state == EngineState.Enabled;
                // 采集入口永不禁用：状态不对时点击由生产入口给中文原因。
                case "btnSwingUp": return state == EngineState.Enabled || state == EngineState.Stopped;
                case "btnEStop":
                case "btnSaveParams":
                case "btnApplyParams":
                case "btnPidTuning": return true;
                case "btnSetPendulumZero":
                case "btnSetPendulum360":
                    return state != EngineState.Enabled && state != EngineState.Swinging &&
                           state != EngineState.Balancing;
                case "btnSetArmZero":
                case "btnSetLeftLimit":
                case "btnSetRightLimit":
                    return state != EngineState.Swinging && state != EngineState.Balancing;
                case "btnClearCalibration":
                case "btnNormalizeArmPose":
                case "btnNormalizePendulumPose":
                    return state != EngineState.Enabled && state != EngineState.Swinging &&
                           state != EngineState.Balancing;
                case "btnReturnHome":
                    return state == EngineState.Enabled || state == EngineState.Stopped;
                case "btnLoadParams":
                case "btnRestoreDefaults": return idle;
                default: return false;
            }
        }

        private bool CheckButtonCard(string buttonName, string cardName)
        {
            System.Windows.Forms.Control button = UiControlFinder.FindByName(this, buttonName);
            if (button == null)
                return false;
            CardPanel card = FindCardAncestor(button);
            return card != null && string.Equals(card.AccessibleName, cardName, StringComparison.Ordinal);
        }

        private int CountButtonsInCard(string cardName)
        {
            System.Windows.Forms.Control card = UiControlFinder.FindByName(this, cardName);
            if (card == null)
                return -1;
            return CountButtonsOnly(card);
        }

        private static int CountButtonsOnly(System.Windows.Forms.Control root)
        {
            List<Button> buttons = new List<Button>();
            UiControlFinder.Collect(root, buttons);
            return buttons.Count;
        }

        /// <summary>卡片内蓝底（主操作）按钮数；卡片不存在返回 -1（当作失败）。</summary>
        private int CountPrimaryButtonsInCard(string cardName)
        {
            System.Windows.Forms.Control card = UiControlFinder.FindByName(this, cardName);
            if (card == null)
                return -1;
            return CountPrimaryButtonsOnly(card);
        }

        private static int CountPrimaryButtonsOnly(System.Windows.Forms.Control root)
        {
            List<Button> buttons = new List<Button>();
            UiControlFinder.Collect(root, buttons);
            int count = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].BackColor == UiStyles.TitleBlue)
                    count++;
            }
            return count;
        }

        private static int CountByAccessibleName(System.Windows.Forms.Control root, string name)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            int count = 0;
            for (int i = 1; i < all.Count; i++)   // 不含 root 本身（与原递归口径一致）
            {
                if (string.Equals(all[i].AccessibleName, name, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static int ScanBanned(string text, string[] banned, string owner, StringBuilder report)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            int hits = 0;
            for (int i = 0; i < banned.Length; i++)
            {
                if (text.IndexOf(banned[i], StringComparison.Ordinal) >= 0)
                {
                    hits++;
                    report.AppendLine("    命中「" + banned[i] + "」：" + owner + " —— " +
                        text.Replace("\r\n", " / "));
                }
            }
            return hits;
        }

        /// <summary>
        /// 23 按钮动作审计：真实 PerformClick / MouseDown → 生产 Click → 真实引擎或参数文件。
        /// 无 DENI / 无控制线程，因此不会产生 PDO 或电机运动；运动按钮只验证其真实入口与安全拒绝。
        /// </summary>
        public string RunButtonActionAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 按钮动作自检（无总线，不输出运动）===");
            int failures = 0;
            bool uiWasRunning = _uiTimer != null && _uiTimer.Enabled;
            bool dialWasRunning = _dialTimer != null && _dialTimer.Enabled;
            if (_uiTimer != null) _uiTimer.Stop();
            if (_dialTimer != null) _dialTimer.Stop();
            try
            {
                // 单页布局：所有卡片常驻可见，PerformClick 直接生效，不需要切页。
                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                AuditCheck(ClickButton("btnConnect") && WaitButtonAudit(delegate
                {
                    FlushLogs();
                    return Volatile.Read(ref _opBusy) == 0 &&
                        _logBox.Text.IndexOf("DeniPath 为空", StringComparison.Ordinal) >= 0;
                }, 3000), "btnConnect", "真实 Connect 入口在缺 DENI 时如实失败", report, ref failures);

                PrepareButtonAction(EngineState.Connected, 0, true, false);
                AuditCheck(ClickButton("btnDisconnect") && WaitButtonAudit(delegate { return _engine.State == EngineState.Disconnected; }, 3000),
                    "btnDisconnect", "真实 Disconnect 回到未连接", report, ref failures);

                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                AuditCheck(ClickButton("btnEnable") && WaitButtonAudit(delegate
                {
                    FlushLogs();
                    return Volatile.Read(ref _opBusy) == 0 &&
                        _logBox.Text.IndexOf("DeniPath 为空", StringComparison.Ordinal) >= 0;
                }, 3000), "btnEnable", "未连接使能先走真实 Connect，缺 DENI 不假装成功", report, ref failures);

                PrepareButtonAction(EngineState.Stopped, 0, true, false);
                AuditCheck(ClickButton("btnStop") && WaitButtonAudit(delegate { return _engine.PendingCommandForAudit == 5; }, 2000),
                    "btnStop", "故障矩阵保留停止；真实 Click 下发 CmdStop", report, ref failures);

                PrepareButtonAction(EngineState.Fault, 0, true, true);
                AuditCheck(ClickButton("btnReset") && WaitButtonAudit(delegate { return _engine.PendingCommandForAudit == 5; }, 2000),
                    "btnReset", "复位复用真实 Stop 安全路径", report, ref failures);

                PrepareButtonAction(EngineState.Enabled, 0, true, false);
                RaiseJog(_jogLeftButton, true);
                bool leftHeld = _engine.IsJogActive && _jogHeldDirection == -1;
                RaiseJog(_jogLeftButton, false);
                AuditCheck(leftHeld && !_engine.IsJogActive && _jogHeldDirection == 0,
                    "btnJogLeft", "MouseDown 开始、MouseUp 停止", report, ref failures);

                PrepareButtonAction(EngineState.Enabled, 0, true, false);
                RaiseJog(_jogRightButton, true);
                bool rightHeld = _engine.IsJogActive && _jogHeldDirection == 1;
                RaiseJog(_jogRightButton, false);
                AuditCheck(rightHeld && !_engine.IsJogActive && _jogHeldDirection == 0,
                    "btnJogRight", "MouseDown 开始、MouseUp 停止", report, ref failures);

                // 点动不受软限位约束：把限位收窄到当前角之外，点动入口仍必须放行（现场靠点动采边界）。
                PrepareButtonAction(EngineState.Enabled, 0, true, false);
                SafetyLimits narrow = _engine.SafetyLimits;
                narrow.ArmMinCount = 1;
                narrow.ArmMaxCount = 2;
                _engine.SetSafetyLimits(narrow);
                string jogReason;
                bool jogOutsideLimits = _engine.TryJogStart(1, out jogReason);
                _engine.JogStop();
                SafetyLimits wide = _engine.SafetyLimits;
                wide.ArmMinCount = SafetyLimits.CreateDefault().ArmMinCount;
                wide.ArmMaxCount = SafetyLimits.CreateDefault().ArmMaxCount;
                _engine.SetSafetyLimits(wide);
                AuditCheck(jogOutsideLimits, "JogStart(不受软限位)",
                    "限位收窄到 [1,2] counts 时点动仍放行（点动只受速度护栏）", report, ref failures);

                bool clampPolicy =
                    !PendulumEngine.MotionClampsToSoftLimit(EngineState.Enabled) &&
                    !PendulumEngine.MotionClampsToSoftLimit(EngineState.Stopped) &&
                    PendulumEngine.MotionClampsToSoftLimit(EngineState.Swinging) &&
                    PendulumEngine.MotionClampsToSoftLimit(EngineState.Balancing);
                AuditCheck(clampPolicy, "SoftLimit(钳位域)",
                    "软限位只钳自动运动；Enabled/Stopped 不钳位", report, ref failures);

                // 点动中（Enabled）可以设 0 点：现场标准流程「点动到位 → 设 0 点」。
                PrepareButtonAction(EngineState.Enabled, 2345, true, false);
                _engine.SetArmZeroCount(0);
                bool zeroWhileEnabled = ClickButton("btnSetArmZero") && _engine.ArmZeroCount == 2345 && _paramsDirty;
                AuditCheck(zeroWhileEnabled, "btnSetArmZero(点动中)",
                    "Enabled 下当前 raw 立即设零点（目标由控制层平移，不跳变）", report, ref failures);

                PrepareButtonAction(EngineState.Stopped, 0, true, false);
                AuditCheck(ClickButton("btnSwingUp") && WaitButtonAudit(delegate
                {
                    FlushLogs();
                    return _logBox.Text.IndexOf("动作=SwingUp(起摆)", StringComparison.Ordinal) >= 0 &&
                        _logBox.Text.IndexOf("总线不在 OP", StringComparison.Ordinal) >= 0;
                }, 2000), "btnSwingUp", "真实起摆门在无 OP 时拒绝且不输出运动", report, ref failures);

                PrepareButtonAction(EngineState.Enabled, 0, true, false);
                AuditCheck(ClickButton("btnEStop") && WaitButtonAudit(delegate
                {
                    return _engine.IsEStopLatched && _engine.PendingCommandForAudit == 6;
                }, 2000), "btnEStop", "真实急停锁存 + CmdEStop", report, ref failures);

                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                AuditCheck(ClickButton("btnSaveParams") && WaitButtonAudit(delegate
                {
                    return File.Exists(ParamStore.DefaultPath) && _paramSavedLabel.Text.IndexOf("参数已保存", StringComparison.Ordinal) >= 0;
                }, 2000), "btnSaveParams", "真实参数文件落盘", report, ref failures);

                // 「应用（不保存）」：历史增益框只读；程序化写进一个远低于旧结构下界的值也必须照常应用
                // （旧实现会因此整批拒收）。应用后勾上未保存标记，参数文件此时还不该被改写。
                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                bool historicalBoxesReadOnly = !_kpBox.Enabled && !_kiBox.Enabled && !_kdBox.Enabled;
                _kpBox.Value = 50m;
                bool applyClicked = ClickButton("btnApplyParams");
                bool applyNotRejected = applyClicked && !_pidApplyRejected;
                bool applyMarkedDirty = _paramsDirty &&
                    _saveParamsButton.Text.IndexOf("●", StringComparison.Ordinal) >= 0;
                AuditCheck(historicalBoxesReadOnly && applyNotRejected && applyMarkedDirty,
                    "btnApplyParams",
                    "历史增益框只读；低历史值不拦应用；应用后标记未保存", report, ref failures);
                report.AppendLine("      历史框只读=" + historicalBoxesReadOnly + "；应用未被拒=" + applyNotRejected +
                    "；未保存标记=" + applyMarkedDirty);

                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                AuditCheck(ClickButton("btnLoadParams") && WaitButtonAudit(delegate
                {
                    FlushLogs();
                    return _logBox.Text.IndexOf("参数已载入并应用", StringComparison.Ordinal) >= 0;
                }, 3000), "btnLoadParams", "真实参数文件读回并应用", report, ref failures);

                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                PidGains changedPid = _pidShadow;
                changedPid.Ka = DefaultGains.Ka + 123.0;
                _pidShadow = changedPid;
                AuditCheck(ClickButton("btnRestoreDefaults") && WaitButtonAudit(delegate
                {
                    return Math.Abs(_pidShadow.Ka - DefaultGains.Ka) < 1e-9 && Volatile.Read(ref _opBusy) == 0;
                }, 3000), "btnRestoreDefaults", "真实恢复默认 PID", report, ref failures);

                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                bool dialogSeen = false;
                bool autoDialogSeen = false;
                bool autoClicked = false;
                bool autoPrecheckClicked = false;
                System.Windows.Forms.Timer closeDialog = new System.Windows.Forms.Timer();
                closeDialog.Interval = 100;
                closeDialog.Tick += delegate
                {
                    AutoTuneDialog auto = null;
                    PidTuningDialog pidDialog = null;
                    foreach (Form open in Application.OpenForms)
                    {
                        if (auto == null) auto = open as AutoTuneDialog;
                        if (pidDialog == null) pidDialog = open as PidTuningDialog;
                    }
                    if (pidDialog != null)
                    {
                        dialogSeen = true;
                        if (!autoClicked)
                        {
                            Button autoButton = UiControlFinder.FindByName(pidDialog, "btnAutoTune") as Button;
                            if (autoButton != null && autoButton.Enabled)
                            {
                                autoClicked = true;
                                // 模态打开自动调参；对话框自己的消息循环会让本 Timer 继续跑，
                                // 下一次 tick 找到 AutoTuneDialog 后关闭它。
                                UiControlFinder.Press(autoButton);
                                return;
                            }
                        }
                    }
                    if (auto != null)
                    {
                        autoDialogSeen = true;
                        if (!autoPrecheckClicked)
                        {
                            Button precheck = UiControlFinder.FindByName(auto, "btnAutoPrecheck") as Button;
                            if (precheck != null && precheck.Enabled)
                            {
                                autoPrecheckClicked = true;
                                // 走真实生产入口：断开态必须逐项列出缺什么，不能抛异常。
                                UiControlFinder.Press(precheck);
                                return;
                            }
                        }
                        auto.Close();
                        return;
                    }
                    if (pidDialog != null && autoClicked)
                        pidDialog.Close();
                };
                closeDialog.Start();
                bool pidClicked = ClickButton("btnPidTuning");
                closeDialog.Stop();
                closeDialog.Dispose();
                AuditCheck(pidClicked && dialogSeen && autoClicked && autoDialogSeen && autoPrecheckClicked,
                    "btnPidTuning",
                    "真实打开内环调参对话框 + 「自动调参…」入口可打开 + 前置检查可执行", report, ref failures);

                PrepareButtonAction(EngineState.Connected, 1234, true, false);
                _engine.SetArmZeroCount(0);
                bool zeroClicked = ClickButton("btnSetArmZero") && _engine.ArmZeroCount == 1234 && _paramsDirty;
                bool zeroSaved = ClickButton("btnSaveParams") && AuditPersistedZero(1234);
                AuditCheck(zeroClicked && zeroSaved, "btnSetArmZero",
                    "当前 raw 立即设零点 → 待保存 → 保存后落盘", report, ref failures);

                PrepareButtonAction(EngineState.Connected, -1000, true, false);
                _engine.SetArmZeroCount(0);
                const int expectedMin = -1000;
                bool leftClicked = ClickButton("btnSetLeftLimit") &&
                    _limitsShadow.ArmMinCount == expectedMin && _paramsDirty;
                bool leftSaved = ClickButton("btnSaveParams") && AuditPersistedLimits(true, expectedMin);
                AuditCheck(leftClicked && leftSaved, "btnSetLeftLimit",
                    "当前 raw 写左限位 → 待保存 → 保存后落盘", report, ref failures);

                PrepareButtonAction(EngineState.Connected, 1000, true, false);
                _engine.SetArmZeroCount(0);
                const int expectedMax = 1000;
                bool rightClicked = ClickButton("btnSetRightLimit") &&
                    _limitsShadow.ArmMaxCount == expectedMax && _paramsDirty;
                bool rightSaved = ClickButton("btnSaveParams") && AuditPersistedLimits(false, expectedMax);
                AuditCheck(rightClicked && rightSaved, "btnSetRightLimit",
                    "当前 raw 写右限位 → 待保存 → 保存后落盘", report, ref failures);

                // 摆杆 0°/360° 两点标定：采集 → 一圈的量 = |360° − 0°| → 保存落盘。
                PrepareButtonAction(EngineState.Connected, 0, true, false);
                _engine.InjectUiAuditState(EngineState.Connected, 0, true, false, 1120);
                RefreshButtons();
                bool zeroCaptured = ClickButton("btnSetPendulumZero") && _engine.PendulumZeroRawCount == 1120;
                _engine.InjectUiAuditState(EngineState.Connected, 0, true, false, 3320);
                RefreshButtons();
                bool fullCaptured = ClickButton("btnSetPendulum360") &&
                                    (int)_pendulum360CountBox.Value == 3320;
                bool cprDerived = Math.Abs(_engine.PendulumCountsPerRev - 2200.0) < 1e-6;
                bool calSaved = ClickButton("btnSaveParams") &&
                                AuditPersistedPendulumCalibration(1120, 3320, 2200);
                AuditCheck(zeroCaptured && fullCaptured && cprDerived && calSaved,
                    "btnSetPendulumZero/360",
                    "采集 0°/360° → 一圈的量 = |3320−1120| = 2200 → 保存后落盘", report, ref failures);

                // 回到零点：真实入口在「已使能但无总线」时拒绝，不产生任何运动。
                PrepareButtonAction(EngineState.Enabled, 1234, true, false);
                _engine.SetArmZeroCount(0);
                bool returnClicked = ClickButton("btnReturnHome");
                bool returnRejected = WaitButtonAudit(delegate
                {
                    FlushLogs();
                    return _logBox.Text.IndexOf("总线未连接，不能回零", StringComparison.Ordinal) >= 0;
                }, 1500);
                AuditCheck(returnClicked && returnRejected, "btnReturnHome",
                    "真实入口在无总线时给中文原因并拒绝，不输出运动", report, ref failures);

                // 清空标定：五项位置点全清、保留一圈量，电机不动；界面显示未标定。
                PrepareButtonAction(EngineState.Connected, 1234, true, false);
                _engine.SetArmZeroCount(1234);
                SafetyLimits clearLimits = SafetyLimits.CreateDefault();
                clearLimits.ArmMinCount = 0;
                clearLimits.ArmMaxCount = 3000;
                _engine.SetSafetyLimits(clearLimits);
                if (_persistedExtras == null)
                    _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
                _persistedExtras.Calibration.HasArmZeroCount = true;
                _persistedExtras.Calibration.ArmZeroCount = 1234;
                _persistedExtras.SoftLimits.MinCounts = 0;
                _persistedExtras.SoftLimits.MaxCounts = 3000;
                double cprBeforeClear = _engine.PendulumCountsPerRev;
                bool clearClicked = ClickButton("btnClearCalibration");
                bool clearOk = clearClicked &&
                               !_engine.ArmZeroCalibrated &&
                               _engine.CalibrationCleared &&
                               _engine.SafetyLimits.ArmLimitsArePlaceholder &&
                               !_persistedExtras.Calibration.HasArmZeroCount &&
                               !_persistedExtras.Calibration.HasPendulumZero &&
                               !_persistedExtras.Calibration.HasPendulum360 &&
                               Math.Abs(_engine.PendulumCountsPerRev - cprBeforeClear) < 1e-9 &&
                               string.IsNullOrEmpty(_armZeroCountBox.Text) &&
                               string.IsNullOrEmpty(_limitMinCountsBox.Text);
                AuditCheck(clearOk, "btnClearCalibration",
                    "清空五个位置点 → 标志清零 / 限位回占位 / 一圈量保留 / 输入框留空 / 不移动电机", report, ref failures);

                // 规整化左中右：以当前 raw=2000 为新零点，旧限位 [0,3000]、旧零点 1000 同 delta 平移。
                SafetyLimits normalizeSeed = SafetyLimits.CreateDefault();
                normalizeSeed.ArmMinCount = 0;
                normalizeSeed.ArmMaxCount = 3000;
                _engine.SetArmZeroCount(1000);
                _engine.SetSafetyLimits(normalizeSeed);
                string completeReason;
                _engine.ConfirmCalibrationComplete(out completeReason);
                _persistedExtras.Calibration.HasArmZeroCount = true;
                _persistedExtras.Calibration.ArmZeroCount = 1000;
                _persistedExtras.SoftLimits.MinCounts = 0;
                _persistedExtras.SoftLimits.MaxCounts = 3000;
                PrepareButtonAction(EngineState.Connected, 2000, true, false);
                bool normalizeClicked = ClickButton("btnNormalizeArmPose") &&
                    _engine.ArmZeroCount == 2000 &&
                    _engine.SafetyLimits.ArmMinCount == 1000 &&
                    _engine.SafetyLimits.ArmMaxCount == 4000 &&
                    _persistedExtras.Calibration.ArmZeroCount == 2000 &&
                    _persistedExtras.SoftLimits.MinCounts == 1000 &&
                    _persistedExtras.SoftLimits.MaxCounts == 4000 &&
                    _armZeroCountBox.Value == 2000m;
                AuditCheck(normalizeClicked, "btnNormalizeArmPose",
                    "current raw=2000 / 旧零点 1000 / 旧限位[0,3000] → 新零 2000、限位[1000,4000]（行程保留）", report, ref failures);

                // 规整化摆杆：当前 CH1=1500 认作新 0°，已有 360°=2200 按同 delta 平移为 2700。
                PrepareButtonAction(EngineState.Connected, 2000, true, false);
                _engine.InjectUiAuditState(EngineState.Connected, 2000, true, false, 1500);
                _engine.SetPendulumZeroCount(1000);
                if (_persistedExtras == null)
                    _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
                if (_persistedExtras.Calibration == null)
                    _persistedExtras.Calibration = CalibrationParameters.CreateDefault();
                _persistedExtras.Calibration.HasPendulumZero = true;
                _persistedExtras.Calibration.PendulumZeroCount = 1000;
                _persistedExtras.Calibration.HasPendulum360 = true;
                _persistedExtras.Calibration.Pendulum360Count = 2200;
                RefreshButtons();
                Application.DoEvents();
                bool pendulumNormalizeClicked = ClickButton("btnNormalizePendulumPose") &&
                    _engine.PendulumZeroRawCount == 1500 &&
                    _persistedExtras.Calibration.PendulumZeroCount == 1500 &&
                    _persistedExtras.Calibration.Pendulum360Count == 2700 &&
                    _persistedExtras.Calibration.PendulumCpr == 1200;
                AuditCheck(pendulumNormalizeClicked, "btnNormalizePendulumPose",
                    "current CH1=1500 / 旧0°=1000 / 旧360°=2200 → 新0°=1500、新360°=2700（一圈量保持 1200）", report, ref failures);

                // ---- 状态行回归：三处“改过但没真机复测”的界面修复，逐条触发前 → 触发后留原始文本 ----

                // ① 规整化只完成摆臂一组：状态行必须橙色并如实列出剩余待确认的摆杆原因，
                //    不能被绿色的「规整化完成」盖掉（触发 = 真实 btnNormalizeArmPose PerformClick）。
                SafetyLimits statusLimits = SafetyLimits.CreateDefault();
                statusLimits.ArmMinCount = 0;
                statusLimits.ArmMaxCount = 3000;
                _engine.SetArmZeroCount(1000);
                _engine.SetSafetyLimits(statusLimits);
                PrepareButtonAction(EngineState.Connected, 2000, true, false);
                if (_persistedExtras == null)
                    _persistedExtras = PersistedExtras.CreateDefault((double)DEFAULT_ARM_COUNTS_PER_REV);
                if (_persistedExtras.Calibration == null)
                    _persistedExtras.Calibration = CalibrationParameters.CreateDefault();
                _persistedExtras.Calibration.HasArmZeroCount = true;
                _persistedExtras.Calibration.ArmZeroCount = 1000;
                _persistedExtras.SoftLimits.MinCounts = 0;
                _persistedExtras.SoftLimits.MaxCounts = 3000;
                _persistedExtras.Calibration.HasPendulumZero = true;
                _persistedExtras.Calibration.PendulumZeroCount = 1000;
                _persistedExtras.Calibration.HasPendulum360 = true;
                _persistedExtras.Calibration.Pendulum360Count = 3400;
                _persistedExtras.Calibration.ClearPendulumPoseSessionMark();
                _poseSessionReviewRequired = true;
                RefreshButtons();
                Application.DoEvents();
                string normalizeBefore = _statusMessageLabel.Text;
                bool normalizeClicked2 = ClickButton("btnNormalizeArmPose");
                string normalizeAfter = _statusMessageLabel.Text;
                bool normalizeStatusOk = normalizeClicked2 && _engine.ArmZeroCount == 2000 &&
                    normalizeAfter.IndexOf("待确认", StringComparison.Ordinal) >= 0 &&
                    normalizeAfter.IndexOf("摆杆", StringComparison.Ordinal) >= 0 &&
                    _statusMessageLabel.ForeColor.ToArgb() == UiStyles.WarnOrange.ToArgb();
                AuditCheck(normalizeStatusOk, "状态行(规整化只完成摆臂一组)",
                    "橙色 + 如实列出剩余摆杆待确认原因", report, ref failures);
                report.AppendLine("      触发前：" + normalizeBefore);
                report.AppendLine("      触发后：" + normalizeAfter);

                // ② 复位后状态行回显引擎权威状态：引擎锁存已清、上一帧遥测锁存仍是旧 true 时，
                //    状态行不能卡在「已急停」（触发 = 真实 btnReset PerformClick + 受控停完成后的引擎字段）。
                PrepareButtonAction(EngineState.Fault, 0, true, true);
                RefreshIdleStatus();
                string resetBefore = _statusMessageLabel.Text;
                bool resetClicked2 = ClickButton("btnReset");
                WaitButtonAudit(delegate { return Volatile.Read(ref _opBusy) == 0; }, 3000);
                _engine.InjectUiAuditState(EngineState.Stopped, 0, true, false);
                _lastEStopLatch = true;   // 上一帧遥测的旧锁存位：旧实现会据此把状态行永久钉在已急停
                RefreshIdleStatus();
                string resetAfter = _statusMessageLabel.Text;
                bool resetStatusOk = resetClicked2 &&
                    resetBefore.IndexOf("已急停", StringComparison.Ordinal) >= 0 &&
                    resetAfter.IndexOf("已受控停并去使能", StringComparison.Ordinal) >= 0 &&
                    resetAfter.IndexOf("已急停", StringComparison.Ordinal) < 0;
                AuditCheck(resetStatusOk, "状态行(复位后)",
                    "回显引擎权威状态（已受控停并去使能），不被旧遥测锁存钉在已急停", report, ref failures);
                report.AppendLine("      触发前：" + resetBefore);
                report.AppendLine("      触发后：" + resetAfter);

                // ③a 摆杆零点回执（360° 点可跟随）：状态行必须写明 360° 点被平移及新计数。
                PrepareButtonAction(EngineState.Connected, 1000, true, false);
                _engine.InjectUiAuditState(EngineState.Connected, 1000, true, false, 1200);
                _engine.SetPendulumZeroCount(1000);
                if (_persistedExtras.Calibration == null)
                    _persistedExtras.Calibration = CalibrationParameters.CreateDefault();
                _persistedExtras.Calibration.HasPendulumZero = true;
                _persistedExtras.Calibration.PendulumZeroCount = 1000;
                _persistedExtras.Calibration.HasPendulum360 = true;
                _persistedExtras.Calibration.Pendulum360Count = 3400;
                RefreshButtons();
                Application.DoEvents();
                string zeroBefore = _statusMessageLabel.Text;
                bool zeroReceiptClicked = ClickButton("btnSetPendulumZero");
                string zeroAfter = _statusMessageLabel.Text;
                bool zeroReceiptOk = zeroReceiptClicked &&
                    _engine.PendulumZeroRawCount == 1200 &&
                    _persistedExtras.Calibration.Pendulum360Count == 3600 &&
                    zeroAfter.IndexOf("平移", StringComparison.Ordinal) >= 0 &&
                    zeroAfter.IndexOf("360°", StringComparison.Ordinal) >= 0 &&
                    (zeroAfter.IndexOf("3,600", StringComparison.Ordinal) >= 0 ||
                     zeroAfter.IndexOf("3600", StringComparison.Ordinal) >= 0);
                AuditCheck(zeroReceiptOk, "状态行(摆杆零点回执·360°平移)",
                    "写明 360° 点随 0° 平移（3400 → 3600）且一圈量不变", report, ref failures);
                report.AppendLine("      触发前：" + zeroBefore);
                report.AppendLine("      触发后：" + zeroAfter);

                // ③b 摆杆零点回执（360° 点作废）：只有 360° 没有旧 0° 时必须写「作废 + 原因」。
                PrepareButtonAction(EngineState.Connected, 1000, true, false);
                _engine.InjectUiAuditState(EngineState.Connected, 1000, true, false, 1200);
                _persistedExtras.Calibration.HasPendulumZero = false;
                _persistedExtras.Calibration.PendulumZeroCount = 0;
                _persistedExtras.Calibration.HasPendulum360 = true;
                _persistedExtras.Calibration.Pendulum360Count = 3400;
                RefreshButtons();
                Application.DoEvents();
                string obsoleteBefore = _statusMessageLabel.Text;
                bool obsoleteClicked = ClickButton("btnSetPendulumZero");
                string obsoleteAfter = _statusMessageLabel.Text;
                bool obsoleteReceiptOk = obsoleteClicked &&
                    !_persistedExtras.Calibration.HasPendulum360 &&
                    obsoleteAfter.IndexOf("作废", StringComparison.Ordinal) >= 0 &&
                    obsoleteAfter.IndexOf("没有参考", StringComparison.Ordinal) >= 0;
                AuditCheck(obsoleteReceiptOk, "状态行(摆杆零点回执·360°作废)",
                    "写明 360° 点作废及原因（没有参考零点）", report, ref failures);
                report.AppendLine("      触发前：" + obsoleteBefore);
                report.AppendLine("      触发后：" + obsoleteAfter);

                // ④ 历史增益现状（Ka / Kad / Kai 是历史参数，平衡环已由整套状态反馈接管）：
                //    界面只读；程序化写一个低于旧结构下界的值也必须照常应用，不连累真正生效的限幅 / 内环；
                //    提示行只展示实测耦合符号并明确标注历史项，不能再出现教用户调外环的「结构判据」。
                PrepareButtonAction(EngineState.Disconnected, 0, true, false);
                bool kaHistoricalReadOnly = !_kpBox.Enabled && !_kiBox.Enabled && !_kdBox.Enabled;
                PidGains beforeHistoricalApply = _engine.BalancePid;
                _kpBox.Value = 50m;
                bool kaLowClicked = ClickButton("btnApplyParams");
                string kaLowStatus = _statusMessageLabel.Text;
                bool kaLowAccepted = kaLowClicked && !_pidApplyRejected &&
                    SameEffectiveGains(_engine.BalancePid, beforeHistoricalApply);
                AuditCheck(kaHistoricalReadOnly && kaLowAccepted, "历史增益 Ka/Kad/Kai(只读)",
                    "界面只读；低于旧结构下界也照常应用，真正生效的项保持不变", report, ref failures);
                report.AppendLine("      触发后：" + kaLowStatus + "（控制层历史 Ka=" +
                    UiStyles.Gain(_engine.BalancePid.Ka) + "）");

                bool pidHintOk = _pidHintLabel != null &&
                    _pidHintLabel.Text.IndexOf("耦合符号（实测）", StringComparison.Ordinal) >= 0 &&
                    _pidHintLabel.Text.IndexOf("历史项", StringComparison.Ordinal) >= 0 &&
                    _pidHintLabel.Text.IndexOf("结构判据", StringComparison.Ordinal) < 0;
                AuditCheck(pidHintOk, "PID 提示行",
                    "耦合符号（实测）只读展示 + 历史增益明确标注、不再出现结构判据", report, ref failures);
                report.AppendLine("      提示行：" +
                    (_pidHintLabel == null ? "（缺失）" : _pidHintLabel.Text.Replace("\r\n", " / ")));

                // ⑤ 关窗自动保存门：参数文件载入被拒后，兜底保存必须跳过（否则出厂值整份覆盖原文件）；
                //    提示文案必须明确写出“不会自动保存”。这里只验门与文案，不真写文件。
                bool saveAllowedNormal = ShouldAutoSaveOnClose();
                _paramLoadRejected = true;
                bool saveSkippedWhenRejected = !ShouldAutoSaveOnClose();
                // 隐式保存（关窗 / 规整化自动落盘 / 调参采用）也必须被同一道门挡住；
                // 这里走到写盘之前就返回 false，不会创建或改写默认参数文件。
                bool implicitSaveBlocked = !SaveParametersTo(ParamStore.DefaultPath, "自检(载入被拒)", false);
                _paramLoadRejected = false;
                bool saveRestoredAfterExplicitSave = ShouldAutoSaveOnClose();
                bool rejectNoticeOk = PARAM_LOAD_REJECTED_NOTICE.IndexOf("不会自动保存", StringComparison.Ordinal) >= 0 &&
                    PARAM_LOAD_REJECTED_NOTICE.IndexOf("覆盖原文件", StringComparison.Ordinal) >= 0;
                AuditCheck(saveAllowedNormal && saveSkippedWhenRejected && implicitSaveBlocked &&
                    saveRestoredAfterExplicitSave && rejectNoticeOk,
                    "关窗自动保存门",
                    "载入被拒时跳过兜底与隐式保存并给出“不会自动保存”提示；正常情形仍保留兜底", report, ref failures);
                report.AppendLine("      提示文案：" + PARAM_LOAD_REJECTED_NOTICE + "。");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("    动作审计异常：" + ex.GetType().Name + " " + ex.Message);
                report.AppendLine(ex.StackTrace);
            }
            finally
            {
                if (_uiTimer != null && uiWasRunning) _uiTimer.Start();
                if (_dialTimer != null && dialWasRunning) _dialTimer.Start();
            }
            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        private void PrepareButtonAction(EngineState state, int raw, bool live, bool estop)
        {
            WaitButtonAudit(delegate { return Volatile.Read(ref _opBusy) == 0; }, 3000);
            Interlocked.Exchange(ref _opBusy, 0);
            _opLabel = null;
            _jogHeldDirection = 0;
            _lastEStopLatch = estop;
            // 界面动作自检不依赖现场参数文件里的旧 raw：位姿核对按「已确认」准备，
            // 生产入口仍然真跑，遇到总线 / 零点缺失时照常给中文拒绝。
            _poseReferenceChecked = true;
            _poseReferenceArmMismatch = false;
            _poseReferencePendulumMismatch = false;
            _poseRecheckArmZero = false;
            _poseRecheckArmMin = false;
            _poseRecheckArmMax = false;
            _poseRecheckPendulumZero = false;
            _poseRecheckPendulum360 = false;
            // 动作自检的基线是「本次进程已确认过的机器」：会话阻断由本助手清掉，各用例才对得上它们
            // 真正要验证的那一项（例如起摆门在无 OP 时的拒绝）。会话判据本身由纯规则自检覆盖。
            _poseSessionReviewRequired = false;
            _engine.ConfirmCalibrationReview();
            _engine.ConfirmPoseSessionReview();
            _engine.InjectUiAuditState(state, raw, live, estop);
            RefreshButtons();
            Application.DoEvents();
        }

        private bool ClickButton(string name)
        {
            Button button = UiControlFinder.FindByName(this, name) as Button;
            if (button == null || !button.Enabled) return false;
            UiControlFinder.Press(button);
            Application.DoEvents();
            return true;
        }

        private static bool WaitButtonAudit(Func<bool> predicate, int timeoutMs)
        {
            long until = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < until)
            {
                if (predicate()) return true;
                Application.DoEvents();
                Thread.Sleep(10);
            }
            return predicate();
        }

        private static void RaiseJog(Button button, bool down)
        {
            if (!UiControlFinder.TryRaiseMouse(button, down))
                throw new MissingMethodException("Button", down ? "OnMouseDown" : "OnMouseUp");
        }

        private static void AuditCheck(bool ok, string name, string action, StringBuilder report, ref int failures)
        {
            if (!ok) failures++;
            report.AppendLine("    " + name + " " + action + "：" + (ok ? "OK" : "FAIL"));
        }

        private bool AuditPersistedZero(int expected)
        {
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            PersistedExtras extras;
            string deni, note, error;
            return ParamStore.TryLoad(ParamStore.DefaultPath, null, out pid, out swing, out limits,
                out extras, out deni, out note, out error) && extras != null && extras.Calibration != null &&
                extras.Calibration.HasArmZeroCount && extras.Calibration.ArmZeroCount == expected;
        }

        private bool AuditPersistedLimits(bool minimum, int expected)
        {
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            PersistedExtras extras;
            string deni, note, error;
            if (!ParamStore.TryLoad(ParamStore.DefaultPath, null, out pid, out swing, out limits,
                    out extras, out deni, out note, out error)) return false;
            int actual = minimum ? limits.ArmMinCount : limits.ArmMaxCount;
            return actual == expected;
        }

        private bool AuditPersistedPendulumCalibration(int zeroCount, int p360Count, int expectedCpr)
        {
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            PersistedExtras extras;
            string deni, note, error;
            return ParamStore.TryLoad(ParamStore.DefaultPath, null, out pid, out swing, out limits,
                out extras, out deni, out note, out error) && extras != null && extras.Calibration != null &&
                extras.Calibration.HasPendulumZero && extras.Calibration.PendulumZeroCount == zeroCount &&
                extras.Calibration.HasPendulum360 && extras.Calibration.Pendulum360Count == p360Count &&
                Math.Abs(extras.Calibration.PendulumCpr - expectedCpr) < 1e-9;
        }

        /// <summary>
        /// 参数自测审计（替代已删除的测量向导自检）：用合成自由摆动样本跑引擎同一套辨识算法，
        /// 校验 f_n ≈ 1.0 Hz；再把结果按 ParamStore 写进临时文件并读回逐字段比对；
        /// 最后按起摆结束时自动应用的生产路径验证泵参数换算与校验。不依赖任何向导窗口。
        /// </summary>
        public string RunParameterAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 参数自测审计 ===");
            int failures = 0;

            // [1] 合成自由摆动样本 → SystemIdentifier（与 PendulumEngine 同源）
            report.AppendLine("[1] 合成自由摆动样本 → f_n / ζ（1 kHz，30 s，f_n=1.0 Hz，ζ=0.05）");
            const int count = 30000;
            long[] ticks = new long[count];
            double[] theta = new double[count];
            double[] omega = new double[count];
            double fn = 1.0;
            double zeta = 0.05;
            double amplitude = 0.6;
            double wn = 2.0 * Math.PI * fn;
            double wd = wn * Math.Sqrt(1.0 - zeta * zeta);
            long step = Math.Max(1L, Stopwatch.Frequency / 1000L);
            for (int i = 0; i < count; i++)
            {
                double t = i / 1000.0;
                double decay = amplitude * Math.Exp(-zeta * wn * t);
                ticks[i] = i * step;
                theta[i] = decay * Math.Sin(wd * t);
                omega[i] = decay * (wd * Math.Cos(wd * t) - zeta * wn * Math.Sin(wd * t));
            }
            SystemIdentifier identifier = new SystemIdentifier();
            for (int i = 0; i < count; i++)
                identifier.AddSample(ticks[i], theta[i], omega[i], true);
            IdentificationResult result = identifier.Identify(_limitsShadow, _swingShadow);
            bool sufficient = result.Sufficient;
            double errPercent = sufficient ? Math.Abs(result.NaturalFreqHz - fn) / fn * 100.0 : 999.0;
            bool fnOk = sufficient && errPercent <= 10.0;
            if (!fnOk) failures++;
            report.AppendLine("    " + (result.Line ?? "(no line)"));
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "    f_n 误差 {0:F2}%（判据 ≤10%）：{1}", errPercent, fnOk ? "OK" : "FAIL"));

            string tempPath = Path.Combine(Path.GetTempPath(),
                "furuta_param_audit_" + Guid.NewGuid().ToString("N") + ".json");
            string error = null;
            try
            {
                // [2] ParamStore 保存 / 读回逐字段
                report.AppendLine("[2] ParamStore 保存 / 读回逐字段");
                PersistedExtras extras = BuildPersistedExtras();
                extras.Identify = IdentifyParameters.From(result);
                bool saved = ParamStore.Save(tempPath, _pidShadow, _swingShadow, _limitsShadow,
                    _engine.DeniPath, extras, out error);
                bool contentOk = false;
                if (saved && File.Exists(tempPath))
                {
                    string content = File.ReadAllText(tempPath);
                    contentOk = content.IndexOf("\"identify\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                content.IndexOf("\"calibration\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                content.IndexOf("\"soft_limits\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                content.IndexOf("\"arm_gear_ratio\"", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                if (!saved || !contentOk) failures++;
                report.AppendLine("    ParamStore.Save " + (saved ? "OK" : "FAIL：" + error));
                report.AppendLine("    JSON 含 identify/calibration/soft_limits/arm_gear_ratio：" + (contentOk ? "OK" : "FAIL"));

                PidGains loadedPid = PidGains.CreateDefault();
                SwingUpParams loadedSwing = SwingUpParams.CreateDefault();
                SafetyLimits loadedLimits = SafetyLimits.CreateDefault();
                PersistedExtras loadedExtras = null;
                string loadedDeni = null;
                string loadedNote = null;
                string loadError = null;
                bool loaded = saved && ParamStore.TryLoad(tempPath, null, out loadedPid, out loadedSwing,
                    out loadedLimits, out loadedExtras, out loadedDeni, out loadedNote, out loadError);
                bool roundTripOk = false;
                string roundTripDetail = null;
                if (!loaded)
                {
                    roundTripDetail = "读回失败：" + (saved ? loadError : "保存未成功");
                }
                else
                {
                    PumpParameters pumpNow = PumpParameters.From(_swingShadow);
                    PumpParameters pumpBack = PumpParameters.From(loadedSwing);
                    double pendulumCpr = extras.Calibration == null ? 0 : extras.Calibration.PendulumCpr;
                    double pendulumCprBack = loadedExtras.Calibration == null ? 0 : loadedExtras.Calibration.PendulumCpr;
                    int armCpr = extras.Calibration == null ? 0 : extras.Calibration.ArmCountsPerRev;
                    int armCprBack = loadedExtras.Calibration == null ? 0 : loadedExtras.Calibration.ArmCountsPerRev;
                    double gear = extras.Calibration == null ? 0 : extras.Calibration.ArmGearRatio;
                    double gearBack = loadedExtras.Calibration == null ? 0 : loadedExtras.Calibration.ArmGearRatio;
                    int captures = extras.Captures == null ? 0 : extras.Captures.Count;
                    int capturesBack = loadedExtras.Captures == null ? 0 : loadedExtras.Captures.Count;
                    // 耦合符号不是用户整定项：运行期由 PendulumEngine.ArmCouplingSign 钉死（台架实测）。
                    // 读回校验只确认参数文件 / 界面影子 / 控制层三处与该实测值一致，不把它当可调参数比较差异。
                    bool signPinned = Math.Abs(loadedPid.Sign - PendulumEngine.ArmCouplingSign) < 1e-9 &&
                                      Math.Abs(_pidShadow.Sign - PendulumEngine.ArmCouplingSign) < 1e-9;
                    bool pidOk = Math.Abs(loadedPid.Ka - _pidShadow.Ka) < 1e-9 &&
                                 Math.Abs(loadedPid.Kad - _pidShadow.Kad) < 1e-9 &&
                                 Math.Abs(loadedPid.Kai - _pidShadow.Kai) < 1e-9 &&
                                 Math.Abs(loadedPid.KPhi - _pidShadow.KPhi) < 1e-9 &&
                                 Math.Abs(loadedPid.KPhiDot - _pidShadow.KPhiDot) < 1e-9 &&
                                 Math.Abs(loadedPid.AccMax - _pidShadow.AccMax) < 1e-9 &&
                                 Math.Abs(loadedPid.ArmRateMax - _pidShadow.ArmRateMax) < 1e-9 &&
                                 signPinned;
                    bool pumpOk = Math.Abs(pumpBack.Ke - pumpNow.Ke) < 1e-9 &&
                                  Math.Abs(pumpBack.PumpAmpDeg - pumpNow.PumpAmpDeg) < 1e-6 &&
                                  Math.Abs(pumpBack.PumpHz - pumpNow.PumpHz) < 1e-9 &&
                                  Math.Abs(pumpBack.PhaseOffsetDeg - pumpNow.PhaseOffsetDeg) < 1e-6;
                    bool limitsOk = loadedLimits.ArmMinCount == _limitsShadow.ArmMinCount &&
                                    loadedLimits.ArmMaxCount == _limitsShadow.ArmMaxCount;
                    bool calibrationOk = Math.Abs(pendulumCprBack - pendulumCpr) < 1e-9 &&
                                         armCprBack == armCpr && Math.Abs(gearBack - gear) < 1e-9;
                    bool capturesOk = capturesBack == captures;
                    bool identifyOk = loadedExtras != null && loadedExtras.Identify != null &&
                                      loadedExtras.Identify.HasResult &&
                                      Math.Abs(loadedExtras.Identify.NaturalFreqHz - result.NaturalFreqHz) < 1e-6;
                    roundTripOk = pidOk && pumpOk && limitsOk && calibrationOk && capturesOk && identifyOk;
                    roundTripDetail = string.Format(CultureInfo.InvariantCulture,
                        "pid={0} pump={1} limits={2} calibration(pendulumCpr={3},armCpr={4},gear={5:0.###})={6} captures({7})={8} identify={9}",
                        pidOk, pumpOk, limitsOk, pendulumCprBack, armCprBack, gearBack, calibrationOk,
                        capturesBack, capturesOk, identifyOk);
                }
                if (!roundTripOk) failures++;
                report.AppendLine("[2b] 读回逐字段：" + (roundTripOk ? "OK" : "FAIL"));
                report.AppendLine("    " + roundTripDetail);

                // [3] 起摆结束时自动应用的生产换算路径（PumpParameters.ApplyTo + SwingUpParams.IsValid）
                report.AppendLine("[3] 泵参数换算 / 校验（起摆结束后的自动应用路径）");
                PumpParameters pump = PumpParameters.From(_swingShadow);
                if (IsFinitePositive(result.PumpHz))
                    pump.PumpHz = result.PumpHz;
                if (!double.IsNaN(result.PumpAmpDeg) && !double.IsInfinity(result.PumpAmpDeg) &&
                    result.PumpAmpDeg >= 0)
                {
                    pump.PumpAmpDeg = Math.Min(result.PumpAmpDeg, 180.0);
                }
                if (IsFinitePositive(result.Ke))
                    pump.Ke = result.Ke;
                string reason;
                bool pumpValid = pump.IsValid(out reason);
                SwingUpParams candidate = _swingShadow;
                pump.ApplyTo(ref candidate);
                bool swingValid = pumpValid && candidate.IsValid(out reason);
                PumpParameters pumpBack2 = PumpParameters.From(candidate);
                bool valuesOk = pumpValid && swingValid &&
                                Math.Abs(pumpBack2.PumpHz - pump.PumpHz) < 1e-9 &&
                                Math.Abs(pumpBack2.PumpAmpDeg - pump.PumpAmpDeg) < 1e-6 &&
                                Math.Abs(pumpBack2.Ke - pump.Ke) < 1e-9;
                if (!valuesOk) failures++;
                report.AppendLine("    泵参数合法 " + pumpValid + "；换算后控制层参数合法 " + swingValid +
                    "；往返一致 " + valuesOk + "：" + (valuesOk ? "OK" : "FAIL"));
                report.AppendLine("    " + (pumpValid
                    ? "泵频=" + pump.PumpHz.ToString("F3", CultureInfo.InvariantCulture) + " Hz，泵幅=" +
                      pump.PumpAmpDeg.ToString("F2", CultureInfo.InvariantCulture) + "°，k_e=" + UiStyles.Gain(pump.Ke)
                    : reason));

                // [4] 自动调参候选：同一套 AutoTuner 纯计算（可用性入口会真的跑它）
                //     Ka/Kad/Kai 是历史参数：候选必须原样沿用输入值；即使这根历史值远低于旧结构下界，
                //     也必须照常产出候选（旧实现会整份拒收，连泵推荐一起丢）。
                report.AppendLine("[4] 自动调参候选（AutoTuner 纯计算，f_n=1.422 Hz）");
                IdentificationResult tuneId = result;
                tuneId.Sufficient = true;
                tuneId.NaturalFreqHz = 1.422;
                tuneId.Confidence = 0.9;
                AutoTuneCandidate tuneCandidate;
                string tuneReason;
                double tuneCouplingAbs = Math.Abs(_limitsShadow.RadiusRatio);
                bool tuneComputed = AutoTuner.TryCompute(tuneId, _pidShadow, _swingShadow, tuneCouplingAbs,
                    out tuneCandidate, out tuneReason);
                bool tuneKeepsHistory = tuneComputed && tuneCandidate != null &&
                    Math.Abs(tuneCandidate.Pid.Ka - _pidShadow.Ka) < 1e-9 &&
                    Math.Abs(tuneCandidate.Pid.Kad - _pidShadow.Kad) < 1e-9 &&
                    Math.Abs(tuneCandidate.Pid.Kai - _pidShadow.Kai) < 1e-9;
                PidGains lowHistoryPid = _pidShadow;
                lowHistoryPid.Ka = 1.0;                  // 远低于任何结构下界的历史值
                lowHistoryPid.Kad = 0.01;
                AutoTuneCandidate lowHistoryCandidate;
                string lowHistoryReason;
                // 合成一份本机耦合比（0.05）：旧的结构下界判据在这里一定会命中并整份拒收，
                // 新实现必须照常产出候选 —— 这条是「历史值不拦整份候选」的回归门。
                bool tuneIgnoresLowHistory = AutoTuner.TryCompute(tuneId, lowHistoryPid, _swingShadow,
                    0.05, out lowHistoryCandidate, out lowHistoryReason) && lowHistoryCandidate != null;
                PumpParameters tunePump = tuneComputed && tuneCandidate != null
                    ? PumpParameters.From(tuneCandidate.Swing)
                    : null;
                // 候选泵值必须等于「有推荐用推荐、没有就沿用当前」的结果（与 AutoTuner 同一取舍）。
                PumpParameters tuneBasePump = PumpParameters.From(_swingShadow);
                double expectedPumpAmpDeg = IsFinitePositive(tuneId.PumpAmpDeg)
                    ? Math.Min(tuneId.PumpAmpDeg, AutoTuner.MaxPumpAmplitudeDeg)
                    : tuneBasePump.PumpAmpDeg;
                bool tunePumpOk = tunePump != null &&
                    Math.Abs(tunePump.Ke - (IsFinitePositive(tuneId.Ke) ? tuneId.Ke : tuneBasePump.Ke)) < 1e-9 &&
                    Math.Abs(tunePump.PumpHz - (IsFinitePositive(tuneId.PumpHz) ? tuneId.PumpHz : tuneBasePump.PumpHz)) < 1e-9 &&
                    Math.Abs(tunePump.PumpAmpDeg - expectedPumpAmpDeg) < 1e-6;
                IdentificationResult tuneInsufficient = tuneId;
                tuneInsufficient.Sufficient = false;
                tuneInsufficient.InsufficientReason = "audit synthetic";
                AutoTuneCandidate rejectedCandidate;
                string rejectedReason;
                bool tuneRejectsInsufficient = !AutoTuner.TryCompute(
                    tuneInsufficient, _pidShadow, _swingShadow, tuneCouplingAbs,
                    out rejectedCandidate, out rejectedReason);
                bool tuneOk = tuneKeepsHistory && tuneIgnoresLowHistory && tunePumpOk && tuneRejectsInsufficient;
                if (!tuneOk) failures++;
                report.AppendLine("    候选计算 " + tuneComputed + "；历史值原样沿用 " + tuneKeepsHistory +
                    "；低历史值照常产出 " + tuneIgnoresLowHistory + "；泵推荐写入 " + tunePumpOk +
                    "；样本不足被拒 " + tuneRejectsInsufficient + "：" + (tuneOk ? "OK" : "FAIL"));
                report.AppendLine("    " + (tuneComputed && tuneCandidate != null
                    ? "历史 Ka/Kad/Kai=" + UiStyles.Gain(tuneCandidate.Pid.Ka) + "/" +
                      UiStyles.Gain(tuneCandidate.Pid.Kad) + "/" + UiStyles.Gain(tuneCandidate.Pid.Kai) +
                      "；泵 k_e=" + UiStyles.Gain(tuneCandidate.Swing.Ke) +
                      " 泵频=" + tuneCandidate.Swing.PumpFrequencyHz.ToString("F2", CultureInfo.InvariantCulture) + " Hz" +
                      " 泵幅=" + (tunePump == null ? "n/a" :
                          tunePump.PumpAmpDeg.ToString("F2", CultureInfo.InvariantCulture) + "°")
                    : tuneReason));

                // [5] 旧版起摆出厂块迁移：这是台架 30 s 超时的实际参数块，必须被识别并一次替换。
                report.AppendLine("[5] 旧版起摆参数块迁移（Ke=1 / AMax=8 / Timeout=30s / 捕获门全0）");
                SwingUpParams legacySwing = SwingUpParams.CreateDefault();
                legacySwing.Ke = 1.0;
                legacySwing.AMax = 8.0;
                legacySwing.KPhi = 6.0;
                legacySwing.KPhiDot = 1.5;
                legacySwing.PumpFrequencyHz = 1.0;
                legacySwing.TimeoutMs = 30000;
                legacySwing.CaptureModeAmplitudeMaxRad = 0;
                legacySwing.CaptureStrokePerModeRad = 0;
                legacySwing.CaptureStrokeMarginRad = 0;
                legacySwing.CaptureAdverseRateStrokeSec = 0;
                legacySwing.CaptureArmRateLimitRadPerSec = 0;
                legacySwing.CapturePendulumRateRadPerSec = 0;
                string legacyDetail;
                bool legacyDetected = PendulumEngine.IsLegacyFactorySwingDefaults(legacySwing, out legacyDetail);
                string migrateNote;
                bool migrated = PendulumEngine.TryMigrateLegacySwingDefaults(ref legacySwing, out migrateNote);
                bool migratedValues =
                    Math.Abs(legacySwing.Ke - SwingUpParams.CreateDefault().Ke) < 1e-9 &&
                    Math.Abs(legacySwing.AMax - SwingUpParams.CreateDefault().AMax) < 1e-9 &&
                    legacySwing.TimeoutMs == SwingUpParams.CreateDefault().TimeoutMs &&
                    legacySwing.CaptureModeAmplitudeMaxRad > 0;
                bool legacyGone = !PendulumEngine.IsLegacyFactorySwingDefaults(legacySwing, out legacyDetail);
                bool legacyOk = legacyDetected && migrated && migratedValues && legacyGone;
                if (!legacyOk) failures++;
                report.AppendLine("    识别 " + legacyDetected + "；迁移 " + migrated +
                    "；值=" + UiStyles.Gain(legacySwing.Ke) + "/" + legacySwing.AMax.ToString("F0") +
                    " rad/s²，Timeout=" + legacySwing.TimeoutMs + " ms；迁移后不再命中 " + legacyGone +
                    "：" + (legacyOk ? "OK" : "FAIL"));

                // [6] 0x608F 诊断必须区分「主动跳过」与「设备明确回答不存在」，不能一句话混说。
                report.AppendLine("[6] 0x608F 交叉校验诊断措辞（主动跳过 / 设备不存在 / 未测出）");
                ArmDriveFacts pprSkipped = new ArmDriveFacts();
                pprSkipped.MotorCountsPerRev = 10000;
                pprSkipped.PprReadable = false;
                string skippedLine = PendulumEngine.DescribePprReadState(pprSkipped);
                ArmDriveFacts pprMissing = new ArmDriveFacts();
                pprMissing.PprReadable = false;
                pprMissing.Notes = "0x608F:01（每转脉冲）读不到：读 0x608F:1 失败（AbortCode=0x06020000，返回 0 字节）。";
                string missingLine = PendulumEngine.DescribePprReadState(pprMissing);
                ArmDriveFacts pprUnknown = new ArmDriveFacts();
                pprUnknown.PprReadable = false;
                pprUnknown.Notes = "0x608F:01（每转脉冲）读不到：读 0x608F:1 失败（AbortCode=0x12345678，返回 0 字节）。";
                string unknownLine = PendulumEngine.DescribePprReadState(pprUnknown);
                bool pprWordingOk =
                    skippedLine.IndexOf("主动跳过", StringComparison.Ordinal) >= 0 &&
                    skippedLine.IndexOf("未向设备查询", StringComparison.Ordinal) >= 0 &&
                    missingLine.IndexOf("设备报告该对象不存在", StringComparison.Ordinal) >= 0 &&
                    missingLine.IndexOf("0x06020000", StringComparison.Ordinal) >= 0 &&
                    unknownLine.IndexOf("未返回可用值", StringComparison.Ordinal) >= 0 &&
                    unknownLine.IndexOf("设备报告该对象不存在", StringComparison.Ordinal) < 0;
                if (!pprWordingOk) failures++;
                report.AppendLine("    主动跳过：" + skippedLine);
                report.AppendLine("    设备不存在：" + missingLine);
                report.AppendLine("    未测出：" + unknownLine);
                report.AppendLine("    三种原因可区分：" + (pprWordingOk ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("    自检异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("参数自测审计临时文件删除失败：" + ex.GetType().Name + " " + ex.Message);
                }
            }

            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        // ---------------------------------------------------------------- 收集器
        //
        // 控件树遍历只有 UiControlFinder 一份实现；这里的「收集器」只按审计需要的类型做过滤，
        // 不再各自递归。

        /// <summary>视觉容器 = CardPanel（统一圆角卡片）+ 其它 GroupBox；匿名布局网格不算容器。</summary>
        private static void CollectVisualContainers(System.Windows.Forms.Control root,
            List<System.Windows.Forms.Control> sink)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            for (int i = 0; i < all.Count; i++)
            {
                System.Windows.Forms.Control control = all[i];
                if (control is CardPanel || control is GroupBox)
                    sink.Add(control);
            }
        }

        private static string ContainerName(System.Windows.Forms.Control control)
        {
            if (!string.IsNullOrEmpty(control.AccessibleName))
                return control.AccessibleName;
            if (!string.IsNullOrEmpty(control.Text))
                return control.Text.Trim();
            return control.GetType().Name;
        }

        /// <summary>父链上必须有 Panel / GroupBox（按钮绝不裸挂窗体；TableLayoutPanel 也是 Panel）。</summary>
        private static bool HasPanelAncestor(System.Windows.Forms.Control control)
        {
            for (System.Windows.Forms.Control parent = control.Parent; parent != null; parent = parent.Parent)
            {
                if (parent is Form)
                    return false;
                if (parent is Panel || parent is GroupBox)
                    return true;
            }
            return false;
        }

        private static bool IsDescendant(System.Windows.Forms.Control child, System.Windows.Forms.Control ancestor)
        {
            for (System.Windows.Forms.Control parent = child.Parent; parent != null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, ancestor))
                    return true;
            }
            return false;
        }

        private static bool ContainsNamePrefix(System.Windows.Forms.Control root, string prefix)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            for (int i = 1; i < all.Count; i++)   // 不含 root 本身（与原递归口径一致）
            {
                string name = all[i].AccessibleName;
                if (!string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>统计超出卡片可视区的后代控件（坐标先换算到卡片客户区）。</summary>
        private static int CountOverflow(System.Windows.Forms.Control card, System.Windows.Forms.Control root,
            ref string firstOverflow)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            int count = 0;
            for (int i = 1; i < all.Count; i++)   // 不含 root 本身（与原递归口径一致）
            {
                System.Windows.Forms.Control child = all[i];
                if (child.Visible && child.Parent != null)
                {
                    Rectangle screen = child.Parent.RectangleToScreen(child.Bounds);
                    Rectangle local = card.RectangleToClient(screen);
                    if (local.Left < -2 || local.Top < -2 ||
                        local.Right > card.ClientSize.Width + 2 || local.Bottom > card.ClientSize.Height + 2)
                    {
                        count++;
                        if (firstOverflow == null)
                        {
                            firstOverflow = (child.AccessibleName ?? child.GetType().Name) + " " + local;
                        }
                    }
                }
            }
            return count;
        }

        private static CardPanel FindCardAncestor(System.Windows.Forms.Control control)
        {
            for (System.Windows.Forms.Control parent = control.Parent; parent != null; parent = parent.Parent)
            {
                CardPanel card = parent as CardPanel;
                if (card != null)
                    return card;
            }
            return null;
        }

        /// <summary>控件在卡片客户区里的矩形（跨多层 TableLayoutPanel 取真实位置）。</summary>
        private static Rectangle CardRect(System.Windows.Forms.Control card, System.Windows.Forms.Control child)
        {
            return card.RectangleToClient(child.Parent.RectangleToScreen(child.Bounds));
        }

        private static void CountInteractive(System.Windows.Forms.Control root, ref int buttons, ref int numerics)
        {
            List<Button> buttonList = new List<Button>();
            UiControlFinder.Collect(root, buttonList);
            List<NumericUpDown> numericList = new List<NumericUpDown>();
            UiControlFinder.Collect(root, numericList);
            buttons += buttonList.Count;
            numerics += numericList.Count;
        }

        private static int CountDescendants(System.Windows.Forms.Control root)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            return all.Count - 1;   // 不含 root 本身
        }

        /// <summary>统计会渲染文字的控件字号（容器继承的默认字体不算）。</summary>
        private static void CollectTextFontSizes(System.Windows.Forms.Control root, HashSet<float> sizes)
        {
            List<System.Windows.Forms.Control> all = new List<System.Windows.Forms.Control>();
            UiControlFinder.CollectAll(root, all);
            for (int i = 0; i < all.Count; i++)
            {
                System.Windows.Forms.Control control = all[i];
                if (control is Label || control is Button || control is NumericUpDown || control is RichTextBox)
                    sizes.Add(control.Font.Size);
            }
        }

        // ================================================================ 控制参数对话框自检（纯对象）

        /// <summary>
        /// 控制参数对话框自检：构造 <see cref="PidTuningDialog"/>（不 Show、不连总线、不写文件），
        /// 验证 17 个参数入口齐全，且给定默认值原样读回（Kpφ/Kiφ/Kdφ、Kφ/Kφ̇/AccMax/ArmRateMax、
        /// 能量增益 ε / 起摆 AMax / 切换 θ·ω / 捕获臂速上限 / 起摆回中·阻尼 / 抽取增益 / 软中心带 / ω_arm 上限）。
        /// 少一个入口、入口被清零、或写错结构体字段，这一套立即红。
        /// </summary>
        public static string RunControlParamUiAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 控制参数对话框自检（无总线 / 不显示窗口） ===");
            int failures = 0;

            string[] names =
            {
                "numPidKpPhi", "numPidKiPhi", "numPidKdPhi",
                "numPidKphi", "numPidKphiDot", "numPidAccMax", "numPidArmRateMax",
                "numSwingEnergyGain", "numSwingAMax", "numSwingThetaSwitchDeg",
                "numSwingOmegaSwitch", "numSwingCaptureArmRate", "numSwingKphi", "numSwingKphiDot",
                "numSwingExtractGain", "numSwingCenterBandRad", "numSafetyOmegaArmMax"
            };

            PidGains pid = PidGains.CreateDefault();
            SwingUpParams swing = SwingUpParams.CreateDefault();
            SafetyLimits limits = SafetyLimits.CreateDefault();

            int present = 0;
            string firstMissing = null;
            PidGains gotPid = pid;
            SwingUpParams gotSwing = swing;
            SafetyLimits gotLimits = limits;
            string error = null;
            bool readOk = false;
            using (PidTuningDialog dialog = new PidTuningDialog(pid, swing, limits,
                SwingParamAccess.Editable, null, null))
            {
                dialog.CreateControl();
                dialog.PerformLayout();
                for (int i = 0; i < names.Length; i++)
                {
                    System.Windows.Forms.Control box = UiControlFinder.FindByName(dialog, names[i]);
                    bool ok = box is NumericUpDown;
                    if (ok)
                        present++;
                    else if (firstMissing == null)
                        firstMissing = names[i];
                    report.AppendLine("    " + names[i] + " " +
                        (box == null ? "缺失" : box.Width + " × " + box.Height) + "：" + (ok ? "OK" : "FAIL"));
                }
                readOk = dialog.TryGetParams(out gotPid, out gotSwing, out gotLimits, out error);
            }

            bool presentOk = present == names.Length;
            report.AppendLine("    参数入口 " + present + " / " + names.Length +
                (firstMissing == null ? "" : "（首个缺失：" + firstMissing + "）") + "：" + (presentOk ? "OK" : "FAIL"));
            if (!presentOk)
                failures++;

            // 读回必须逐字段等于源值：入口在、但映射到别的字段 / 被清零，这一条会红。
            // 切换角按度显示，3 位小数 → 折回 rad 的误差上限约 1.8e-5 rad，给 1e-4 容差。
            bool same = readOk &&
                Near(gotPid.Ka, pid.Ka) && Near(gotPid.Kad, pid.Kad) && Near(gotPid.Kai, pid.Kai) &&
                Near(gotPid.KPhi, pid.KPhi) && Near(gotPid.KPhiDot, pid.KPhiDot) &&
                Near(gotPid.AccMax, pid.AccMax) && Near(gotPid.ArmRateMax, pid.ArmRateMax) &&
                Near(gotPid.KpPhi, pid.KpPhi) && Near(gotPid.KiPhi, pid.KiPhi) && Near(gotPid.KdPhi, pid.KdPhi) &&
                Near(gotSwing.TargetEnergyGain, swing.TargetEnergyGain) && Near(gotSwing.AMax, swing.AMax) &&
                Near(gotSwing.OmegaSwitchRadPerSec, swing.OmegaSwitchRadPerSec) &&
                Near(gotSwing.CaptureArmRateLimitRadPerSec, swing.CaptureArmRateLimitRadPerSec) &&
                Near(gotSwing.KPhi, swing.KPhi) && Near(gotSwing.KPhiDot, swing.KPhiDot) &&
                Near(gotSwing.ExtractGain, swing.ExtractGain) &&
                Near(gotSwing.CenterBandRad, swing.CenterBandRad) &&
                Near(gotSwing.Ke, swing.Ke) && Near(gotSwing.PumpAmplitude, swing.PumpAmplitude) &&
                Near(gotSwing.PumpFrequencyHz, swing.PumpFrequencyHz) &&
                Near(gotSwing.PumpPhaseOffsetRad, swing.PumpPhaseOffsetRad) &&
                Math.Abs(gotSwing.ThetaSwitchRad - swing.ThetaSwitchRad) < 1e-4 &&
                Near(gotLimits.OmegaArmMaxRadPerSec, limits.OmegaArmMaxRadPerSec) &&
                gotLimits.ArmMinCount == limits.ArmMinCount && gotLimits.ArmMaxCount == limits.ArmMaxCount;
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "    读回校验：Kφ={0:F3} Kφ̇={1:F3} AccMax={2:F1} ArmRateMax={3:F2}；ε={4:F4} AMax={5:F2} " +
                "θ切={6:F3}° ω切={7:F2} 捕获臂速={8:F2}；ω_arm上限={9:F2}：{10}",
                gotPid.KPhi, gotPid.KPhiDot, gotPid.AccMax, gotPid.ArmRateMax,
                gotSwing.TargetEnergyGain, gotSwing.AMax,
                gotSwing.ThetaSwitchRad * 180.0 / Math.PI, gotSwing.OmegaSwitchRadPerSec,
                gotSwing.CaptureArmRateLimitRadPerSec, gotLimits.OmegaArmMaxRadPerSec,
                (readOk && same) ? "OK" : "FAIL" + (error == null ? "" : "（" + error + "）")));
            if (!readOk || !same)
                failures++;

            // 起摆参数状态门：必须与 CLI setswingup 同一套（见 SwingParamGate）。纯规则 + 对话框
            // Enabled 状态程序化验证，不连总线、不显示窗口、不写文件。
            string gateReason;
            bool gateRules =
                SwingParamGate.Resolve(EngineState.Connected, out gateReason) == SwingParamAccess.Locked &&
                !string.IsNullOrEmpty(gateReason) &&
                SwingParamGate.Resolve(EngineState.Balancing, out gateReason) == SwingParamAccess.Locked &&
                !string.IsNullOrEmpty(gateReason) &&
                SwingParamGate.Resolve(EngineState.Fault, out gateReason) == SwingParamAccess.Locked &&
                !string.IsNullOrEmpty(gateReason) &&
                SwingParamGate.Resolve(EngineState.Swinging, out gateReason) == SwingParamAccess.HotEditOnly &&
                !string.IsNullOrEmpty(gateReason) &&
                SwingParamGate.Resolve(EngineState.Stopped, out gateReason) == SwingParamAccess.Editable &&
                gateReason == null &&
                SwingParamGate.Resolve(EngineState.Disconnected, out gateReason) == SwingParamAccess.Editable &&
                gateReason == null &&
                SwingParamGate.Resolve(EngineState.Enabled, out gateReason) == SwingParamAccess.Editable &&
                gateReason == null;

            // 起摆中：CLI 热改集合 = 捕获臂速 / 抽取增益 / 软中心带（对话框暴露的三项）可改，
            // 其余起摆字段锁定；内环 / 加速度律 / 安全阈值不属于 setswingup，任何状态都可改。
            string hotReason;
            SwingParamAccess hotAccess = SwingParamGate.Resolve(EngineState.Swinging, out hotReason);
            bool hotFieldsOk;
            using (PidTuningDialog hotDialog = new PidTuningDialog(pid, swing, limits, hotAccess, hotReason, null))
            {
                hotDialog.CreateControl();
                hotFieldsOk =
                    IsBoxEnabled(hotDialog, "numSwingCaptureArmRate", true) &&
                    IsBoxEnabled(hotDialog, "numSwingExtractGain", true) &&
                    IsBoxEnabled(hotDialog, "numSwingCenterBandRad", true) &&
                    IsBoxEnabled(hotDialog, "numSwingEnergyGain", false) &&
                    IsBoxEnabled(hotDialog, "numSwingAMax", false) &&
                    IsBoxEnabled(hotDialog, "numSwingThetaSwitchDeg", false) &&
                    IsBoxEnabled(hotDialog, "numSwingOmegaSwitch", false) &&
                    IsBoxEnabled(hotDialog, "numSwingKphi", false) &&
                    IsBoxEnabled(hotDialog, "numSwingKphiDot", false) &&
                    IsBoxEnabled(hotDialog, "numPidKpPhi", true) &&
                    IsBoxEnabled(hotDialog, "numSafetyOmegaArmMax", true);
            }
            string lockedReason;
            SwingParamAccess lockedAccess = SwingParamGate.Resolve(EngineState.Connected, out lockedReason);
            bool lockedFieldsOk;
            using (PidTuningDialog lockedDialog = new PidTuningDialog(pid, swing, limits, lockedAccess, lockedReason, null))
            {
                lockedDialog.CreateControl();
                lockedFieldsOk =
                    IsBoxEnabled(lockedDialog, "numSwingEnergyGain", false) &&
                    IsBoxEnabled(lockedDialog, "numSwingCaptureArmRate", false) &&
                    IsBoxEnabled(lockedDialog, "numSwingExtractGain", false) &&
                    IsBoxEnabled(lockedDialog, "numSwingCenterBandRad", false) &&
                    IsBoxEnabled(lockedDialog, "numPidKpPhi", true) &&
                    IsBoxEnabled(lockedDialog, "numSafetyOmegaArmMax", true);
            }

            // 打开后状态变了（例如途中起摆）：被锁字段的改动必须整组拦住；只改热改项才放行。
            SwingUpParams lockedChange = swing;
            lockedChange.AMax += 1.0;
            bool lockedChangeBlocked = SwingParamGate.DescribeLockedChanges(swing, lockedChange) != null;
            SwingUpParams hotChange = swing;
            hotChange.CenterBandRad += 1.0;
            hotChange.ExtractGain += 1.0;
            hotChange.CaptureArmRateLimitRadPerSec += 0.5;
            bool hotChangeAllowed = SwingParamGate.DescribeLockedChanges(swing, hotChange) == null;
            SwingUpParams merged = SwingParamGate.MergeHotEditFields(swing, hotChange);
            bool mergeOk = Near(merged.CenterBandRad, hotChange.CenterBandRad) &&
                           Near(merged.ExtractGain, hotChange.ExtractGain) &&
                           Near(merged.CaptureArmRateLimitRadPerSec, hotChange.CaptureArmRateLimitRadPerSec) &&
                           Near(merged.AMax, swing.AMax);

            bool gateOk = gateRules && hotFieldsOk && lockedFieldsOk && lockedChangeBlocked &&
                          hotChangeAllowed && mergeOk;
            report.AppendLine("    起摆状态门（Connected / Balancing / Fault 整组锁定；Swinging 仅 CLI 热改项）：" +
                (gateOk ? "OK" : "FAIL"));
            if (!gateOk)
                failures++;

            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        /// <summary>数字输入框存在且 Enabled 等于期望值（对话框状态门自检用）。</summary>
        private static bool IsBoxEnabled(System.Windows.Forms.Control root, string name, bool expectedEnabled)
        {
            NumericUpDown box = UiControlFinder.FindByName(root, name) as NumericUpDown;
            return box != null && box.Enabled == expectedEnabled;
        }

        // ================================================================ 标定规则自检（纯对象）

        /// <summary>
        /// 标定规则自检：只构造 <see cref="CalibrationParameters"/> / <see cref="SoftLimitParameters"/> 纯对象，
        /// 不连总线、不开窗、不动电机、不写文件。判据独立写在这里（生产侧只提供被测方法），
        /// 覆盖：0°/360° 成对性、一圈量派生、掉电后的会话确认语义、驱动臂三点整体平移。
        /// </summary>
        public static string RunCalibrationRuleAudit()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== Furuta 标定规则自检（纯对象，无总线 / 无界面） ===");
            int failures = 0;

            // ---- [1] 0°/360° 成对性 ----
            report.AppendLine("[1] 摆杆 0°/360° 成对性（只有一半 = 不是可运行标定，文案必须点名缺哪一半）");
            CalibrationParameters only360 = CalibrationParameters.CreateDefault();
            only360.HasPendulum360 = true;
            only360.Pendulum360Count = 2400;
            PendulumPairCheck pair360 = only360.CheckPendulumPair();
            AuditRule(pair360.Issue == PendulumPairIssue.MissingZero && !pair360.IsComplete &&
                      pair360.Reason != null && pair360.Reason.IndexOf("缺 0°", StringComparison.Ordinal) >= 0,
                "只有 360°", "判 MissingZero、Reason 点名「缺 0°」", report, ref failures);

            CalibrationParameters onlyZero = CalibrationParameters.CreateDefault();
            onlyZero.HasPendulumZero = true;
            onlyZero.PendulumZeroCount = 100;
            PendulumPairCheck pairZero = onlyZero.CheckPendulumPair();
            AuditRule(pairZero.Issue == PendulumPairIssue.Missing360 &&
                      pairZero.Reason != null && pairZero.Reason.IndexOf("缺 360°", StringComparison.Ordinal) >= 0,
                "只有 0°", "判 Missing360、Reason 点名「缺 360°」", report, ref failures);

            CalibrationParameters both = CalibrationParameters.CreateDefault();
            both.HasPendulumZero = true;
            both.PendulumZeroCount = 1120;
            both.HasPendulum360 = true;
            both.Pendulum360Count = 3320;
            PendulumPairCheck pairBoth = both.CheckPendulumPair();
            AuditRule(pairBoth.IsComplete && pairBoth.SpanCounts == 2200 && pairBoth.Reason == null,
                "0°/360° 齐全", "Complete 且 |360−0| = 2200 counts", report, ref failures);

            CalibrationParameters same = CalibrationParameters.CreateDefault();
            same.HasPendulumZero = true;
            same.HasPendulum360 = true;
            same.PendulumZeroCount = 500;
            same.Pendulum360Count = 500;
            AuditRule(same.CheckPendulumPair().Issue == PendulumPairIssue.SameCount,
                "0° 与 360° 同值", "判 SameCount（不能派生一圈的量）", report, ref failures);

            CalibrationParameters none = CalibrationParameters.CreateDefault();
            AuditRule(none.CheckPendulumPair().Issue == PendulumPairIssue.MissingBoth &&
                      CalibrationParameters.CheckPendulumPair((CalibrationParameters)null).Issue == PendulumPairIssue.MissingBoth,
                "两点都没有 / 无 calibration 段", "判 MissingBoth（null 安全）", report, ref failures);

            // ---- [2] 一圈量派生 ----
            report.AppendLine("[2] 一圈的量 = |360° − 0°|（缺一半时不得改写已保存值）");
            CalibrationParameters cprHalf = CalibrationParameters.CreateDefault();
            cprHalf.PendulumCpr = 2400;
            cprHalf.HasPendulum360 = true;
            cprHalf.Pendulum360Count = 2400;
            int derived;
            bool halfDerived = cprHalf.TryCalibratePendulumCpr(out derived);
            AuditRule(!halfDerived && derived == 0 && Math.Abs(cprHalf.PendulumCpr - 2400) < 1e-9,
                "只有 360° 时标定一圈量", "返回 false、不改 PendulumCpr（2400 保留）", report, ref failures);

            CalibrationParameters cprBoth = CalibrationParameters.CreateDefault();
            cprBoth.HasPendulumZero = true;
            cprBoth.PendulumZeroCount = 1200;
            cprBoth.HasPendulum360 = true;
            cprBoth.Pendulum360Count = 3600;
            bool fullDerived = cprBoth.TryCalibratePendulumCpr(out derived);
            AuditRule(fullDerived && derived == 2400 && Math.Abs(cprBoth.PendulumCpr - 2400) < 1e-9,
                "两点齐全时标定一圈量", "true 且 PendulumCpr = 2400", report, ref failures);

            // ---- [3] 掉电语义：持久 counts 只有「本次进程确认过」才算可用 ----
            report.AppendLine("[3] 掉电语义（别的进程写的标记 = 未确认，不得拿旧值直接使能）");
            CalibrationParameters stale = CalibrationParameters.CreateDefault();
            stale.HasArmZeroCount = true;
            stale.ArmZeroCount = -8776;
            stale.ArmPoseSessionTag = "boot=2026-11-17T01:00:00.000Z;proc=2026-11-17T01:05:00.000Z;pid=4242";
            stale.HasPendulumZero = true;
            stale.PendulumZeroCount = 100;
            stale.HasPendulum360 = true;
            stale.Pendulum360Count = 2500;
            stale.PendulumPoseSessionTag = "boot=2026-11-17T01:00:00.000Z;proc=2026-11-17T01:05:00.000Z;pid=4242";
            AuditRule(!stale.ArmPoseSessionVerified && !stale.PendulumPoseSessionVerified,
                "旧进程的会话标记", "两组都判未确认", report, ref failures);

            stale.MarkArmPoseCapturedInSession();
            stale.MarkPendulumPoseCapturedInSession();
            AuditRule(stale.ArmPoseSessionVerified && stale.PendulumPoseSessionVerified &&
                      stale.ArmPoseCapturedAtUtc.HasValue && stale.PendulumPoseCapturedAtUtc.HasValue,
                "本次进程整体确认后", "两组都判已确认（并记下 UTC 时间）", report, ref failures);

            CalibrationParameters singleHalf = CalibrationParameters.CreateDefault();
            singleHalf.HasPendulumZero = true;
            singleHalf.PendulumZeroCount = 100;
            singleHalf.MarkPendulumPoseCapturedInSession();
            AuditRule(!singleHalf.PendulumPoseSessionVerified,
                "只采到 0° 就盖会话戳", "仍判未确认（两点必须整体建立）", report, ref failures);

            stale.ClearPosePoints();
            AuditRule(!stale.HasArmZeroCount && !stale.HasPendulumZero && !stale.HasPendulum360 &&
                      !stale.ArmPoseSessionVerified && !stale.PendulumPoseSessionVerified &&
                      stale.CalibrationCleared,
                "清空位置点", "点位、会话标记一起清，Cleared=true", report, ref failures);

            // ---- [4] 整体平移（规整化） ----
            report.AppendLine("[4] 规整化 = 按当前 raw 整体平移（行程 / 一圈量不变）");
            CalibrationParameters armCal = CalibrationParameters.CreateDefault();
            armCal.HasArmZeroCount = true;
            armCal.ArmZeroCount = 1000;
            SoftLimitParameters limits = new SoftLimitParameters();
            limits.MinCounts = 0;
            limits.MaxCounts = 3000;
            int newMin, newZero, newMax;
            string normalizeReason;
            bool armShifted = armCal.TryNormalizeArmPose(2000, limits,
                out newMin, out newZero, out newMax, out normalizeReason);
            AuditRule(armShifted && newZero == 2000 && newMin == 1000 && newMax == 4000 &&
                      (newMax - newMin) == (limits.MaxCounts - limits.MinCounts),
                "摆臂三点平移 raw=2000", "零 1000→2000、限位 [0,3000]→[1000,4000]，行程不变", report, ref failures);

            SoftLimitParameters placeholder = new SoftLimitParameters();
            AuditRule(!armCal.TryNormalizeArmPose(2000, placeholder, out newMin, out newZero, out newMax, out normalizeReason),
                "占位限位（未采集）", "拒绝平移并给中文原因", report, ref failures);

            CalibrationParameters pendCal = CalibrationParameters.CreateDefault();
            pendCal.HasPendulumZero = true;
            pendCal.PendulumZeroCount = 1000;
            pendCal.HasPendulum360 = true;
            pendCal.Pendulum360Count = 2200;
            int newZeroP, new360P;
            string pendReason;
            bool pendShifted = pendCal.TryNormalizePendulumPose(1500, out newZeroP, out new360P, out pendReason);
            int spanBefore = 1200;
            AuditRule(pendShifted && newZeroP == 1500 && new360P == 2700 && (new360P - newZeroP) == spanBefore,
                "摆杆两点平移 raw=1500", "0° 1000→1500、360° 2200→2700，一圈量仍 1200", report, ref failures);

            // ---- [5] 0° 重标时 360° 点的同一份处理（UI 与 CLI `zero pendulum` 共用实现） ----
            report.AppendLine("[5] 摆杆 0° 重标 → 360° 点按同一圈量平移 / 作废（PersistedParams 唯一实现）");
            CalibrationParameters rebase = CalibrationParameters.CreateDefault();
            rebase.HasPendulumZero = true;
            rebase.PendulumZeroCount = 1000;
            rebase.HasPendulum360 = true;
            rebase.Pendulum360Count = 3400;
            string rebaseNote;
            bool shifted = rebase.RebasePendulum360AfterZero(1200, out rebaseNote);
            AuditRule(shifted && rebase.PendulumZeroCount == 1200 && rebase.HasPendulum360 &&
                      rebase.Pendulum360Count == 3600 && Math.Abs(rebase.PendulumCpr - 2400) < 1e-9 &&
                      !string.IsNullOrEmpty(rebaseNote) && rebaseNote.IndexOf("平移", StringComparison.Ordinal) >= 0,
                "旧 0°=1000 / 360°=3400，新 0°=1200", "360° 平移为 3600，一圈量仍 2400，回执说明已平移",
                report, ref failures);

            CalibrationParameters orphan360 = CalibrationParameters.CreateDefault();
            orphan360.HasPendulumZero = false;
            orphan360.HasPendulum360 = true;
            orphan360.Pendulum360Count = 2400;
            string orphanNote;
            bool orphanShifted = orphan360.RebasePendulum360AfterZero(50, out orphanNote);
            AuditRule(!orphanShifted && !orphan360.HasPendulum360 && orphan360.HasPendulumZero &&
                      orphan360.PendulumZeroCount == 50 &&
                      !string.IsNullOrEmpty(orphanNote) && orphanNote.IndexOf("作废", StringComparison.Ordinal) >= 0,
                "只有 360° 没有旧 0°", "360° 作废并给出原因，新 0° 写入", report, ref failures);

            CalibrationParameters sameSpan = CalibrationParameters.CreateDefault();
            sameSpan.HasPendulumZero = true;
            sameSpan.PendulumZeroCount = 100;
            sameSpan.HasPendulum360 = true;
            sameSpan.Pendulum360Count = 100;
            string sameNote;
            bool sameShifted = sameSpan.RebasePendulum360AfterZero(200, out sameNote);
            AuditRule(!sameShifted && !sameSpan.HasPendulum360 &&
                      !string.IsNullOrEmpty(sameNote) && sameNote.IndexOf("作废", StringComparison.Ordinal) >= 0,
                "旧 0°/360° 同值", "无法派生一圈量 → 360° 作废并给出原因", report, ref failures);

            report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL（" + failures + " 项）");
            return report.ToString();
        }

        private static void AuditRule(bool ok, string what, string expect, StringBuilder report, ref int failures)
        {
            if (!ok) failures++;
            report.AppendLine("    " + what + " → " + expect + "：" + (ok ? "OK" : "FAIL"));
        }
    }
}
