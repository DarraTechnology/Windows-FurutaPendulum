using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 统一卡片容器：白底、8px 圆角、1px 浅灰边框、左上角主色短竖条 + 10pt 半粗标题。
    /// 所有卡片（右栏内容卡 + 左栏操作/提示卡）都用它，圆角与内边距只在这一处定义。
    /// 子控件放在 Padding 之内；标题条 28px 由本类自己绘制，子控件从 y=28 开始布局。
    /// </summary>
    internal sealed class CardPanel : Panel
    {
        private const int Radius = 8;
        /// <summary>标题条高度（规格 24–28 px）。</summary>
        public const int HeaderHeight = 28;
        /// <summary>卡片内边距（规格 10–12 px）。</summary>
        public const int Pad = 10;
        /// <summary>卡片外边距：相邻两张卡之间 = 6 + 6 = 12 px。</summary>
        public const int Gap = 6;

        private readonly string _title;
        private readonly Color _accent;

        public CardPanel(string title, string accessibleName)
            : this(title, accessibleName, UiStyles.TitleBlue)
        {
        }

        public CardPanel(string title, string accessibleName, Color accent)
        {
            _title = title ?? string.Empty;
            _accent = accent;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            BackColor = UiStyles.PanelBack;
            AccessibleName = accessibleName;
            AccessibleRole = AccessibleRole.Grouping;
            Padding = new Padding(Pad, HeaderHeight, Pad, Pad);
            Margin = new Padding(Gap);
        }

        /// <summary>卡片标题（自检报告与 AI 注入按它认卡片）。</summary>
        public string Title
        {
            get { return _title; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 先铺页面底色，再把圆角卡片画上去：四角露出的就是页面底色，圆角才看得出来。
            g.Clear(UiStyles.FormBack);
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiStyles.RoundRect(bounds, Radius))
            {
                using (SolidBrush fill = new SolidBrush(UiStyles.PanelBack))
                    g.FillPath(fill, path);
                using (Pen border = new Pen(UiStyles.GridLine, 1f))
                    g.DrawPath(border, path);
            }

            // 标题左侧主色竖条 + 标题文字；文字区留足内边距，不贴边、不裁切。
            using (SolidBrush accent = new SolidBrush(_accent))
                g.FillRectangle(accent, new Rectangle(13, 8, 3, 13));
            TextRenderer.DrawText(g, _title, UiStyles.CardTitleFont,
                new Rectangle(22, 4, Math.Max(1, Width - 34), 20),
                UiStyles.GroupText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            base.OnPaint(e);
        }
    }
}
