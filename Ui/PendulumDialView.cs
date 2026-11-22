using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 杆角度图：单表盘自绘。6 点钟 = 自然下垂 0°，12 点钟 = 倒立 180°，
    /// 指针指向杆的实际方位；顶部绿弧 = 倒立目标带。
    /// 杆 CH1 计数冻结（无有效读数）时状态行直接写明，不把冻结值当实时值展示。
    /// 只负责画，姿态由 MainForm 按控制层遥测喂进来；不碰 SDK、不碰控制层类型。
    /// 角度换算：θ = 杆编码器 count × 360 / 每转计数（默认 2400 count/圈，1200 count = 180°）。
    /// 全部 GDI+ 抗锯齿绘制，不用任何默认控件拼。
    /// </summary>
    internal sealed class PendulumDialView : Panel
    {
        private const double UprightDeg = 180.0;
        private const double UprightBandHalfDeg = 8.0;   // 顶部倒立目标带半宽（±8°）

        private static readonly Font AngleFont = new Font("Consolas", 17f, FontStyle.Bold);
        private static readonly Font CaptionFont = new Font("Segoe UI", 8.5f);
        private static readonly Font SmallFont = new Font("Consolas", 8.5f);
        private static readonly Font TickFont = new Font("Segoe UI", 8.5f);

        /// <summary>表盘自绘用到的字号（界面自检统计「全窗体字号种类」时并入）。</summary>
        internal static float[] RenderedFontSizes()
        {
            return new[] { AngleFont.Size, CaptionFont.Size, SmallFont.Size, TickFont.Size };
        }

        private double _thetaDeg;
        private double _countsPerRev = 2400.0;
        private int _wrappedCounts;
        private bool _connected;
        private bool _alarm;
        private bool _hasData;
        private bool _feedbackLive = true;
        private double _feedbackAgeSeconds;

        public PendulumDialView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = UiStyles.PanelBack;
            AccessibleName = "viewPendulumDial";
        }

        /// <summary>喂一帧姿态。thetaDeg 是未卷绕的真实摆角（0 = 下垂，180 = 倒立）；counts 为 ±半圈内计数。</summary>
        public void SetPose(double thetaDeg, int wrappedCounts, double countsPerRev,
            bool connected, bool alarm, bool hasData)
        {
            _thetaDeg = thetaDeg;
            _wrappedCounts = wrappedCounts;
            _countsPerRev = countsPerRev > 0 && !double.IsNaN(countsPerRev) && !double.IsInfinity(countsPerRev)
                ? countsPerRev
                : 0.0;
            _connected = connected;
            _alarm = alarm;
            _hasData = hasData;
        }

        /// <summary>
        /// 喂一帧杆读数存活状态：false = CH1 计数已冻结。
        /// 冻结时状态行写「杆读数无效」，指针转警示色，避免把最后一帧当成实时值。
        /// </summary>
        public void SetFeedbackLive(bool live, double ageSeconds)
        {
            _feedbackLive = live;
            _feedbackAgeSeconds = ageSeconds;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(UiStyles.PanelBack);

            float w = ClientSize.Width;
            float h = ClientSize.Height;
            if (w < 80f || h < 80f)
                return;

            // 卡片形状决定排法。卡片宽高比 > 1.9（宽而矮）时，上下排会把 17pt 读数压进圆里、
            // 刻度标签挤在一起，所以改成表盘在左、读数在右；竖高卡片（表盘卡跨两行）仍用上下排。
            bool sideBySide = w > h * 1.9f;

            // 刻度标签画在圆环外（见 DrawTickLabel），半径外侧要留出标签自身的实测尺寸：
            // 左右两个标签（90° / 270°）沿半径外推占满一个标签宽，上下两个标签（0° / 180°）占满一个标签高。
            // 尺寸一律量出来，不拿字号估 —— 估小了标签会被卡片边缘裁掉。
            const float RingGap = 4f;
            float sideLabelWidth = MaxTextWidth("90°", "270°");
            float verticalLabelHeight = Math.Max(MeasureTickText("下垂 0°").Height, MeasureTickText("倒立 180°").Height);
            // 上下排时先把读数块的高度预留出来（三行：17pt 大字 + 两行 8.5pt），
            // 剩下的才给表盘 —— 先算直径再算起点会让字号一变大就压到圆周上。
            const float ReadoutReserve = 76f;
            RectangleF dialArea = sideBySide
                ? new RectangleF(2f, 2f, Math.Min(h * 1.35f, w * 0.45f), h - 4f)
                : new RectangleF(2f, 2f, w - 4f, Math.Max(90f, h - ReadoutReserve));

            float r = Math.Min(dialArea.Width * 0.5f - sideLabelWidth - RingGap,
                               dialArea.Height * 0.5f - verticalLabelHeight - RingGap);
            if (r < 22f)
                return;

            float cx = dialArea.X + dialArea.Width * 0.5f;
            float cy = dialArea.Y + dialArea.Height * 0.5f;

            Color needleColor = !_connected
                ? UiStyles.OfflineGray
                : (_alarm ? UiStyles.AlarmColor : (_feedbackLive ? UiStyles.ThetaColor : UiStyles.WarnOrange));
            Color ringColor = _alarm ? UiStyles.AlarmColor : UiStyles.GridLine;

            DrawDialFace(g, cx, cy, r, ringColor);
            DrawTargetBand(g, cx, cy, r);
            DrawTicks(g, cx, cy, r);
            DrawTargetMark(g, cx, cy, r);
            DrawNeedle(g, cx, cy, r, Wrap360(_thetaDeg), needleColor);

            RectangleF readoutBlock = sideBySide
                ? new RectangleF(dialArea.Right + 6f, 0f, w - dialArea.Right - 8f, h)
                : new RectangleF(0f, dialArea.Bottom + 2f, w, h - dialArea.Bottom - 2f);
            DrawReadout(g, readoutBlock);
        }

        /// <summary>表盘底：外圈 + 内侧浅底。外圈颜色随报警变红。</summary>
        private static void DrawDialFace(Graphics g, float cx, float cy, float r, Color ringColor)
        {
            using (SolidBrush face = new SolidBrush(Color.FromArgb(251, 253, 255)))
                g.FillEllipse(face, cx - r + 2f, cy - r + 2f, 2f * r - 4f, 2f * r - 4f);
            using (Pen outer = new Pen(ringColor, 1.6f))
                g.DrawEllipse(outer, cx - r, cy - r, 2f * r, 2f * r);
            using (Pen inner = new Pen(UiStyles.GridLine, 1f))
                g.DrawEllipse(inner, cx - r * 0.86f, cy - r * 0.86f, 2f * r * 0.86f, 2f * r * 0.86f);
        }

        /// <summary>目标带：顶部 180° 的倒立目标短弧 + ±8° 浅绿带。</summary>
        private static void DrawTargetBand(Graphics g, float cx, float cy, float r)
        {
            float ringR = r * 0.94f;
            RectangleF ring = new RectangleF(cx - ringR, cy - ringR, 2f * ringR, 2f * ringR);
            float band = Math.Max(6f, r * 0.07f);

            // GDI+ 角度以 3 点钟为 0°、顺时针为正；本表盘 0° 在 6 点钟，换算 = 90° − 显示角。
            float bandStart = (float)(90.0 - (UprightDeg + UprightBandHalfDeg));
            using (Pen capture = new Pen(Color.FromArgb(70, UiStyles.OkGreen), band))
            {
                capture.StartCap = LineCap.Flat;
                capture.EndCap = LineCap.Flat;
                g.DrawArc(capture, ring, bandStart, (float)(2.0 * UprightBandHalfDeg));
            }

            using (Pen target = new Pen(UiStyles.OkGreen, Math.Max(2.5f, r * 0.02f)))
            {
                target.StartCap = LineCap.Round;
                target.EndCap = LineCap.Round;
                g.DrawArc(target, ring, (float)(90.0 - (UprightDeg + 1.0)), 2f);
            }
        }

        /// <summary>刻度：每 30° 短、每 90° 长；底「下垂 0°」、顶「倒立 180°」、左右 90° / 270°。</summary>
        private static void DrawTicks(Graphics g, float cx, float cy, float r)
        {
            using (Pen minor = new Pen(Color.FromArgb(200, 210, 222), 1f))
            using (Pen major = new Pen(UiStyles.ZeroLine, 1.6f))
            {
                for (int deg = 0; deg < 360; deg += 30)
                {
                    bool isMajor = deg % 90 == 0;
                    double a = deg * Math.PI / 180.0;
                    float sx = (float)Math.Sin(a);
                    float sy = (float)Math.Cos(a);
                    float outer = r * 0.985f;
                    float len = isMajor ? r * 0.10f : r * 0.05f;
                    g.DrawLine(isMajor ? major : minor,
                        cx + sx * (outer - len), cy + sy * (outer - len),
                        cx + sx * outer, cy + sy * outer);
                }
            }

            DrawTickLabel(g, cx, cy, r, 0.0, "下垂 0°");
            DrawTickLabel(g, cx, cy, r, 90.0, "90°");
            DrawTickLabel(g, cx, cy, r, 180.0, "倒立 180°");
            DrawTickLabel(g, cx, cy, r, 270.0, "270°");
        }

        /// <summary>
        /// 刻度标签：沿**它自己的角方向**（sin/cos 与指针同一套换算）推到圆环外侧，标签框在半径方向上的
        /// 投影半宽刚好落在环外 gap 处。
        /// <para>
        /// 旧版把标签放在 r*0.78 处 —— 那正是指针尖的半径，再叠加写死的 -24 / +24 / ±12 像素偏移，
        /// 于是「下垂 0°」偏在左下、「倒立 180°」偏在右上、「90° / 270°」上下错位：
        /// 标签根本不在各自的刻度上，换个窗口大小偏移还不跟着缩放。改成径向定位后，
        /// 四个标签各自压在自己的角位上，任何半径/宽高比都对得齐。
        /// </para>
        /// </summary>
        private static void DrawTickLabel(Graphics g, float cx, float cy, float r, double deg, string text)
        {
            const float RingGap = 4f;
            double a = deg * Math.PI / 180.0;
            float ux = (float)Math.Sin(a);
            float uy = (float)Math.Cos(a);
            SizeF size = MeasureTickText(text);
            float halfW = size.Width * 0.5f;
            float halfH = size.Height * 0.5f;
            // 标签框在半径方向上的投影半宽：沿 x 的投影用 halfW、沿 y 的投影用 halfH。
            float radialHalf = Math.Abs(ux) * halfW + Math.Abs(uy) * halfH;
            float dist = r + RingGap + radialHalf;
            float x = cx + ux * dist - halfW;
            float y = cy + uy * dist - halfH;
            TextRenderer.DrawText(g, text, TickFont, new Point((int)Math.Round(x), (int)Math.Round(y)),
                UiStyles.CaptionText, TextFormatFlags.NoPadding);
        }

        /// <summary>刻度标签按绘制同款 NoPadding 口径量尺寸，量出来和画出来一样宽（旧版量与画口径不一致）。</summary>
        private static SizeF MeasureTickText(string text)
        {
            // 提议尺寸给一个足够大的固定框，不用 int.MaxValue：过大尺寸在 TextRenderer 里会退化成估算值。
            Size size = TextRenderer.MeasureText(text, TickFont, new Size(1000, 100),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return new SizeF(size.Width, size.Height);
        }

        private static float MaxTextWidth(params string[] texts)
        {
            float max = 0f;
            for (int i = 0; i < texts.Length; i++)
            {
                SizeF size = MeasureTickText(texts[i]);
                if (size.Width > max)
                    max = size.Width;
            }
            return max;
        }

        /// <summary>180° 倒立目标位标记：顶部向内的绿色小三角。</summary>
        private static void DrawTargetMark(Graphics g, float cx, float cy, float r)
        {
            float y = cy - r;
            using (SolidBrush brush = new SolidBrush(UiStyles.OkGreen))
            {
                PointF[] triangle =
                {
                    new PointF(cx, y + 8f),
                    new PointF(cx - 5f, y - 1f),
                    new PointF(cx + 5f, y - 1f)
                };
                g.FillPolygon(brush, triangle);
            }
        }

        /// <summary>指针：圆心到半径 0.78，圆头，针尖小圆点；中心轴白底描边。</summary>
        private static void DrawNeedle(Graphics g, float cx, float cy, float r, double angleDeg, Color color)
        {
            double a = angleDeg * Math.PI / 180.0;
            float sx = (float)Math.Sin(a);
            float sy = (float)Math.Cos(a);
            float tipX = cx + sx * (r * 0.78f);
            float tipY = cy + sy * (r * 0.78f);

            using (Pen needle = new Pen(color, 3.2f))
            {
                needle.StartCap = LineCap.Round;
                needle.EndCap = LineCap.Round;
                g.DrawLine(needle, cx, cy, tipX, tipY);
            }
            using (SolidBrush tip = new SolidBrush(color))
                g.FillEllipse(tip, tipX - 3.5f, tipY - 3.5f, 7f, 7f);
            using (SolidBrush hub = new SolidBrush(UiStyles.PanelBack))
                g.FillEllipse(hub, cx - 6f, cy - 6f, 12f, 12f);
            using (Pen hubPen = new Pen(UiStyles.ZeroLine, 1.5f))
                g.DrawEllipse(hubPen, cx - 6f, cy - 6f, 12f, 12f);
        }

        /// <summary>
        /// 读数块：θ 大字、±半圈 count / 每转计数小字、下垂/倒立方位说明，三行整体居中在给定区域。
        /// 区域放不下就自上而下少画一行（先省方位说明，再省计数），绝不把字叠到表盘上 ——
        /// 旧版用 h*0.42 + r 算起点，卡片一矮（单页第三行只有 23% 高）大字号就压进圆里。
        /// </summary>
        private void DrawReadout(Graphics g, RectangleF block)
        {
            string angleText = _hasData
                ? Wrap360(_thetaDeg).ToString("0.0") + "°"
                : (_connected ? "—" : "未连接");
            Color angleColor = !_hasData
                ? UiStyles.OfflineGray
                : (_alarm ? UiStyles.AlarmColor : UiStyles.ThetaLargeColor);

            string countsText;
            if (!_hasData)
            {
                countsText = "—";
            }
            else if (_countsPerRev <= 0.0)
            {
                countsText = "每圈计数未标定";
            }
            else
            {
                countsText = string.Format("{0:N0} / {1:N0} counts（半圈 {2:N0} = 180°）",
                    _wrappedCounts, _countsPerRev, _countsPerRev * 0.5);
            }

            string stateText;
            Color stateColor;
            if (!_connected)
            {
                stateText = "未连接";
                stateColor = UiStyles.OfflineGray;
            }
            else if (!_hasData)
            {
                stateText = "无遥测";
                stateColor = UiStyles.OfflineGray;
            }
            else if (_alarm)
            {
                stateText = "故障 / 急停锁存";
                stateColor = UiStyles.AlarmColor;
            }
            else if (!_feedbackLive)
            {
                stateText = "杆读数无效 · CH1 " + FormatFeedbackAge(_feedbackAgeSeconds);
                stateColor = UiStyles.WarnOrange;
            }
            else
            {
                stateText = "下垂 0° · 倒立 180°";
                stateColor = UiStyles.CaptionText;
            }

            const float GapAfterAngle = 2f;
            const float GapAfterCounts = 4f;
            SizeF angleSize = g.MeasureString(angleText, AngleFont);
            SizeF countsSize = g.MeasureString(countsText, SmallFont);
            SizeF stateSize = g.MeasureString(stateText, CaptionFont);

            float needAngle = angleSize.Height;
            float needCounts = needAngle + GapAfterAngle + countsSize.Height;
            float needState = needCounts + GapAfterCounts + stateSize.Height;
            bool showCounts = block.Height >= needCounts;
            bool showState = block.Height >= needState;
            float height = showState ? needState : (showCounts ? needCounts : needAngle);

            float y = block.Y + Math.Max(0f, (block.Height - height) * 0.5f);
            using (SolidBrush brush = new SolidBrush(angleColor))
                g.DrawString(angleText, AngleFont, brush, block.X + (block.Width - angleSize.Width) * 0.5f, y);
            y += angleSize.Height + GapAfterAngle;
            if (!showCounts)
                return;
            using (SolidBrush brush = new SolidBrush(UiStyles.CaptionText))
                g.DrawString(countsText, SmallFont, brush, block.X + (block.Width - countsSize.Width) * 0.5f, y);
            y += countsSize.Height + GapAfterCounts;
            if (!showState)
                return;
            using (SolidBrush brush = new SolidBrush(stateColor))
                g.DrawString(stateText, CaptionFont, brush, block.X + (block.Width - stateSize.Width) * 0.5f, y);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>杆计数多久没变化（∞ = 从未变化）。全程序唯一实现，主界面无有效读数提示也调用它。</summary>
        internal static string FormatFeedbackAge(double ageSeconds)
        {
            if (!IsFinite(ageSeconds))
                return "从未变化";
            return ageSeconds.ToString("F1") + " s 未变化";
        }

        /// <summary>角度收拢到 [0,360)：0 = 6 点钟（下垂）、180 = 12 点钟（倒立）。</summary>
        private static double Wrap360(double deg)
        {
            if (!IsFinite(deg))
                return 0.0;
            deg = deg % 360.0;
            if (deg < 0.0)
                deg += 360.0;
            return deg;
        }
    }
}
