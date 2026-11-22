using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 界面配色、字体与控件工厂。按钮、卡片、字段框、指标格统一从这里出，改样式只改这一处。
    /// 配色沿用本仓 ETH 案例浅底风格：底 #F5F8FC、主色 DodgerBlue，杆角度橙、报警红、离线灰。
    /// </summary>
    internal static class UiStyles
    {
        // ---- 配色 ----
        public static readonly Color FormBack = Color.FromArgb(245, 248, 252);
        public static readonly Color TitleBlue = Color.FromArgb(30, 144, 255);
        public static readonly Color TitleBlueDark = Color.FromArgb(20, 116, 214);
        public static readonly Color TitleBlueSoft = Color.FromArgb(234, 244, 255);
        public static readonly Color GroupText = Color.FromArgb(50, 60, 72);
        public static readonly Color CaptionText = Color.FromArgb(104, 114, 126);
        public static readonly Color ValueText = Color.FromArgb(20, 24, 30);
        public static readonly Color StatusText = Color.FromArgb(20, 70, 140);
        public static readonly Color StatusBack = Color.FromArgb(235, 244, 255);
        public static readonly Color AlarmText = Color.Firebrick;
        public static readonly Color AlarmBack = Color.FromArgb(255, 236, 236);
        public static readonly Color PanelBack = Color.White;
        public static readonly Color GridLine = Color.FromArgb(227, 233, 239);
        public static readonly Color FieldBorder = Color.FromArgb(206, 216, 228);
        public static readonly Color ZeroLine = Color.FromArgb(144, 160, 176);
        public static readonly Color ArmColor = Color.FromArgb(30, 144, 255);
        public static readonly Color ThetaColor = Color.FromArgb(230, 126, 34);
        public static readonly Color ThetaLargeColor = Color.FromArgb(255, 140, 0);
        public static readonly Color ControlColor = Color.FromArgb(46, 139, 87);
        public static readonly Color AlarmColor = Color.FromArgb(217, 83, 79);
        public static readonly Color OfflineGray = Color.FromArgb(176, 184, 192);
        public static readonly Color OkGreen = Color.ForestGreen;
        public static readonly Color WarnOrange = Color.FromArgb(230, 126, 34);
        public static readonly Color HoverBack = Color.FromArgb(236, 245, 255);

        /// <summary>卡片标题字体：10pt 半粗。GDI 绘制，不走控件 Font，避免被 DpiLock 二次换算。</summary>
        public static readonly Font CardTitleFont = new Font("Segoe UI Semibold", 10f);

        /// <summary>界面字体。点阵单位换算交给 DpiLock 的 FreezeToPixelFonts，这里只按 96DPI 设计稿给点值。</summary>
        public static Font UiFont(float size)
        {
            return new Font("Segoe UI", size);
        }

        public static Font UiFont(float size, FontStyle style)
        {
            return new Font("Segoe UI", size, style);
        }

        /// <summary>日志框字体：等宽，方便对齐时间戳与数值（全窗体字号 8.5 / 10，此处用 10）。</summary>
        public static Font LogFont()
        {
            return new Font("Consolas", 10f);
        }

        /// <summary>等宽数值字体：数字 / 十六进制按位对齐，中文说明仍用 UiFont。</summary>
        public static Font MonoFont(float size)
        {
            return new Font("Consolas", size);
        }

        public static Font MonoFont(float size, FontStyle style)
        {
            return new Font("Consolas", size, style);
        }

        // ---------------------------------------------------------------- 卡片与网格

        /// <summary>统一卡片容器（白底圆角 + 主色标题条）。accessibleName 供布局自检与 AI 注入定位。</summary>
        public static CardPanel MakeCard(string title, string accessibleName)
        {
            return new CardPanel(title, accessibleName);
        }

        /// <summary>布局网格：透明底、零边距，不参与视觉容器统计。</summary>
        public static TableLayoutPanel MakeGrid(int columns, int rows)
        {
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            for (int i = 0; i < columns; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < rows; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            return grid;
        }

        /// <summary>固定行高的布局网格（参数区用）：行高由调用方给，传 0 的行用百分比吸收余量。</summary>
        public static TableLayoutPanel MakeFixedGrid(int columns, params int[] rowHeights)
        {
            TableLayoutPanel grid = MakeGrid(columns, rowHeights == null ? 0 : rowHeights.Length);
            if (rowHeights == null)
                return grid;
            int flexible = 0;
            for (int i = 0; i < rowHeights.Length; i++)
                if (rowHeights[i] <= 0) flexible++;
            for (int i = 0; i < rowHeights.Length; i++)
            {
                grid.RowStyles[i] = rowHeights[i] > 0
                    ? new RowStyle(SizeType.Absolute, rowHeights[i])
                    : new RowStyle(SizeType.Percent, flexible > 0 ? 100f / flexible : 100f);
            }
            return grid;
        }

        /// <summary>圆角矩形路径（卡片 / 提示块用）。</summary>
        public static GraphicsPath RoundRect(Rectangle bounds, int radius)
        {
            int d = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180f, 90f);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270f, 90f);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // ---------------------------------------------------------------- 按钮

        /// <summary>按钮。back / fore 传 Color.Empty 表示白底描边；主操作传 TitleBlue/白字。</summary>
        public static Button MakeButton(string text, Color back, EventHandler handler, Color fore)
        {
            return MakeButton(text, back, handler, fore, 8.5f);
        }

        /// <summary>按钮（指定字号）。统一 Flat 描边、悬停浅蓝、按下深一档，高度由容器行高决定。</summary>
        public static Button MakeButton(string text, Color back, EventHandler handler, Color fore, float fontSize)
        {
            Button button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 2, 3, 2),
                Padding = new Padding(0),
                Text = text,
                Font = UiFont(fontSize),
                FlatStyle = FlatStyle.Flat,
                AccessibleRole = AccessibleRole.PushButton,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                TabStop = true
            };
            button.FlatAppearance.BorderSize = 1;
            if (back == Color.Empty)
            {
                button.BackColor = PanelBack;
                button.ForeColor = fore == Color.Empty ? GroupText : fore;
                button.FlatAppearance.BorderColor = FieldBorder;
                button.FlatAppearance.MouseOverBackColor = HoverBack;
                button.FlatAppearance.MouseDownBackColor = TitleBlueSoft;
            }
            else
            {
                button.BackColor = back;
                button.ForeColor = fore == Color.Empty ? Color.White : fore;
                button.FlatAppearance.BorderColor = back;
                button.FlatAppearance.MouseOverBackColor = Darken(back, 0.08f);
                button.FlatAppearance.MouseDownBackColor = Darken(back, 0.16f);
            }
            if (handler != null)
                button.Click += handler;
            return button;
        }

        /// <summary>主操作按钮：DodgerBlue 实底白字。</summary>
        public static Button MakePrimaryButton(string text, EventHandler handler, float fontSize)
        {
            return MakeButton(text, TitleBlue, handler, Color.White, fontSize);
        }

        /// <summary>危险按钮：报警红实底白字（急停），高度仍由行高决定，不做高大按钮。</summary>
        public static Button MakeDangerButton(string text, EventHandler handler, float fontSize)
        {
            return MakeButton(text, AlarmColor, handler, Color.White, fontSize);
        }

        private static Color Darken(Color color, float amount)
        {
            int r = (int)Math.Round(color.R * (1.0 - amount));
            int g = (int)Math.Round(color.G * (1.0 - amount));
            int b = (int)Math.Round(color.B * (1.0 - amount));
            return Color.FromArgb(color.A, ClampToByte(r), ClampToByte(g), ClampToByte(b));
        }

        private static int ClampToByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }

        // ---------------------------------------------------------------- 字段与读数

        /// <summary>
        /// 上行小标签 + 下行编辑器的两行式字段框。标签 8.5pt 灰、编辑器高 27px，左右内边距一致。
        /// Control 写全名：FurutaPendulum.Control 是控制层命名空间，会遮住 Forms 的 Control。
        /// </summary>
        public static System.Windows.Forms.Control MakeLabeledField(string caption, System.Windows.Forms.Control editor)
        {
            TableLayoutPanel box = MakeGrid(1, 2);
            box.RowStyles[0] = new RowStyle(SizeType.Absolute, 15f);
            box.RowStyles[1] = new RowStyle(SizeType.Absolute, 28f);
            box.Margin = new Padding(4, 3, 4, 3);

            Label label = FieldCaption(caption);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Padding = new Padding(0, 0, 0, 2);
            label.Margin = new Padding(0);

            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0);
            editor.MaximumSize = new Size(4096, 27);
            box.Controls.Add(label, 0, 0);
            box.Controls.Add(editor, 0, 1);
            return box;
        }

        /// <summary>字段小标签：8.5pt 灰、单行、不贴边。</summary>
        public static Label FieldCaption(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                Font = UiFont(8.5f),
                ForeColor = CaptionText,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(1, 0, 0, 0)
            };
        }

        /// <summary>数值框。整数位宽由调用方给范围，小数位按参数精度定。</summary>
        public static NumericUpDown MakeNumber(decimal value, decimal min, decimal max)
        {
            return MakeNumber(value, min, max, 0);
        }

        public static NumericUpDown MakeNumber(decimal value, decimal min, decimal max, int decimals)
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = min,
                Maximum = max,
                Value = value,
                DecimalPlaces = decimals,
                ThousandsSeparator = false,
                TextAlign = HorizontalAlignment.Right,
                Font = MonoFont(10f),
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0)
            };
        }

        /// <summary>指标格的灰色说明字（单行）。</summary>
        public static Label MakeCaption(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                AutoEllipsis = true,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = UiFont(8.5f),
                ForeColor = CaptionText,
                BackColor = Color.Transparent,
                Margin = new Padding(6, 2, 4, 2)
            };
        }

        /// <summary>指标格的加粗值字。文本未变则不赋值，避免无谓重绘。</summary>
        public static Label MakeValue()
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = "—",
                AutoEllipsis = true,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = UiFont(10f, FontStyle.Bold),
                ForeColor = ValueText,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 2, 8, 2)
            };
        }

        /// <summary>
        /// 单行读数格（内容卡统一 22px 行高）：左 8.5pt 灰标签、右 10pt 等宽粗值。
        /// 标签左对齐 / 数值右对齐 / 等宽字体，是内容卡唯一的读数排版。标签列默认 36%。
        /// </summary>
        public static TableLayoutPanel MakeStat(string caption, out Label value)
        {
            return MakeStat(caption, out value, 36f);
        }

        /// <summary>
        /// 单行读数格（指定标签列宽百分比）。窄卡要传大一点：单格宽的 PDO 实时数据卡用 52%，
        /// 标签列才放得下「实际速度 0x606C (counts/s)」这类寄存器名；值列 48% 仍足够显示 10 位计数与十六进制状态字。
        /// </summary>
        public static TableLayoutPanel MakeStat(string caption, out Label value, float captionPercent)
        {
            TableLayoutPanel cell = MakeGrid(2, 1);
            cell.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, captionPercent);
            cell.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100f - captionPercent);
            cell.Margin = new Padding(2, 0, 2, 0);
            cell.RowStyles[0] = new RowStyle(SizeType.Percent, 100f);

            Label captionLabel = FieldCaption(caption);
            captionLabel.TextAlign = ContentAlignment.MiddleLeft;
            captionLabel.Margin = new Padding(1, 0, 0, 0);

            value = new Label
            {
                Dock = DockStyle.Fill,
                Text = "—",
                Font = MonoFont(10f, FontStyle.Bold),
                ForeColor = ValueText,
                TextAlign = ContentAlignment.MiddleRight,
                UseMnemonic = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 2, 0)
            };
            cell.Controls.Add(captionLabel, 0, 0);
            cell.Controls.Add(value, 1, 0);
            return cell;
        }

        /// <summary>单行读数格：左灰说明字（固定宽度）+ 右等宽值（设备参数卡的只读读数行用）。</summary>
        public static TableLayoutPanel MakeReadout(string caption, int captionWidth, out Label value)
        {
            TableLayoutPanel cell = MakeGrid(2, 1);
            cell.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, captionWidth);
            cell.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100f);
            cell.Margin = new Padding(0);

            Label captionLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = caption,
                Font = UiFont(8.5f),
                ForeColor = CaptionText,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(1, 0, 0, 0)
            };
            value = new Label
            {
                Dock = DockStyle.Fill,
                Text = "—",
                Font = MonoFont(10f, FontStyle.Bold),
                ForeColor = ValueText,
                TextAlign = ContentAlignment.MiddleRight,
                UseMnemonic = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 2, 0)
            };
            cell.Controls.Add(captionLabel, 0, 0);
            cell.Controls.Add(value, 1, 0);
            return cell;
        }

        /// <summary>只读的等宽文本块（限位 counts、换算结果、单位说明）。</summary>
        public static Label MakeMonoLabel(string text, float size)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text ?? string.Empty,
                Font = MonoFont(size),
                ForeColor = CaptionText,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Margin = new Padding(3, 0, 0, 0)
            };
        }

        /// <summary>说明 / 提示小字：8pt 灰、可换行、左上对齐。</summary>
        public static Label MakeHint(string text, float size)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text ?? string.Empty,
                Font = UiFont(size),
                ForeColor = CaptionText,
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false,
                AutoSize = false,
                AutoEllipsis = false,
                BackColor = Color.Transparent,
                Margin = new Padding(3, 1, 3, 1)
            };
        }

        /// <summary>兼容旧调用：带标题的分组框（新界面卡片用 CardPanel，此方法保留给对话框）。</summary>
        public static GroupBox MakeGroup(string title)
        {
            return new GroupBox
            {
                Dock = DockStyle.Fill,
                Text = "  " + title + "  ",
                Font = UiFont(8.5f),
                ForeColor = GroupText,
                Padding = new Padding(12, 22, 12, 12),
                Margin = new Padding(0),
                BackColor = PanelBack
            };
        }

        /// <summary>自绘面板：开双缓冲，动画与曲线都用它，避免重绘闪烁。</summary>
        public static Panel MakeDoubleBufferedPanel()
        {
            return new DoubleBufferedPanel();
        }

        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = PanelBack;
            }
        }

        /// <summary>文本未变则不赋值。</summary>
        public static void SetLabel(Label label, string text)
        {
            if (label == null)
                return;
            if (!string.Equals(label.Text, text, StringComparison.Ordinal))
                label.Text = text;
        }

        /// <summary>文本与颜色都不变则不重绘。</summary>
        public static void SetLabelColored(Label label, string text, Color color)
        {
            if (label == null)
                return;
            if (!string.Equals(label.Text, text, StringComparison.Ordinal))
                label.Text = text;
            if (label.ForeColor != color)
                label.ForeColor = color;
        }

        /// <summary>
        /// 控制增益的统一显示精度：5 位小数。
        /// <para>
        /// 内环 Kdφ 出厂 0.0009、Kai 与辨识出的 k_e 都可能落在千分位以下；按 3 位小数显示会变成
        /// 0.001 / 0.00，现场照显示值回写就会吃掉稳定裕量。增益一律走这里，不要在调用点散落格式串。
        /// 角度、速度、能量、计数、限幅等物理量不要用这个，按各自的既有精度显示。
        /// </para>
        /// </summary>
        public static string Gain(double value)
        {
            return value.ToString("0.00000", CultureInfo.InvariantCulture);
        }

        /// <summary>角度弧度换算。</summary>
        public const double RadToDeg = 180.0 / Math.PI;

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
