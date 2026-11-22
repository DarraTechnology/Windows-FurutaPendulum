using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FurutaPendulum
{
    /// <summary>
    /// 与 PnS / HCFA DpiLock 同一口径：永远按 100%/96DPI 设计稿，禁止 Windows 150% 再放大。
    /// PerMonitorV2 清单只挡 DWM 糊图；运行期切 UNAWARE，字体用像素单位，窗体 AutoScale=None。
    /// 尺寸只钉最小客户区（八张卡片都放得下的单页设计稿），窗口本身可自由拉大，不设上限。
    /// </summary>
    internal static class DpiLock
    {
        /// <summary>单页布局的最小客户区宽度。</summary>
        public const int DesignWidth = 1280;

        /// <summary>单页布局的最小客户区高度：三行卡片都放得下。</summary>
        public const int DesignHeight = 1000;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static void Apply()
        {
            try
            {
                // -1 = DPI_AWARENESS_CONTEXT_UNAWARE。必须在出第一个窗体前调用。
                SetProcessDpiAwarenessContext(new IntPtr(-1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DPI 锁定失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 应用设计稿：客户区按设计稿，最小外框在 Load 时按真实非客户区差值钉一次。
        /// </summary>
        public static void ApplyDesignSize(Form form)
        {
            if (form == null)
                return;
            // 禁止 AutoScale：Windows 150% 会把设计稿再放大一次。
            form.AutoScaleMode = AutoScaleMode.None;
            form.AutoSize = false;
            FreezeToPixelFonts(form);
            form.ClientSize = new Size(DesignWidth, DesignHeight);
            form.Load += delegate { ApplyMinimumClientSize(form, DesignWidth, DesignHeight); };
        }

        /// <summary>
        /// 只钉最小客户区：客户区小于设计稿时先放大回设计稿，再按真实非客户区（标题栏 / 边框）
        /// 换算成 MinimumSize。**不写 MaximumSize、不改 ResizeMode** —— 窗口可以自由拉大。
        /// 句柄创建前 Width-ClientSize 不可靠，所以必须在 Load（或更晚）调用。
        /// </summary>
        public static void ApplyMinimumClientSize(Form form, int clientWidth, int clientHeight)
        {
            if (form == null || clientWidth <= 0 || clientHeight <= 0)
                return;
            if (form.ClientSize.Width < clientWidth || form.ClientSize.Height < clientHeight)
            {
                form.ClientSize = new Size(
                    Math.Max(form.ClientSize.Width, clientWidth),
                    Math.Max(form.ClientSize.Height, clientHeight));
            }
            int nonClientWidth = Math.Max(0, form.Width - form.ClientSize.Width);
            int nonClientHeight = Math.Max(0, form.Height - form.ClientSize.Height);
            form.MinimumSize = new Size(clientWidth + nonClientWidth, clientHeight + nonClientHeight);
        }

        // 本项目有 FurutaPendulum.Control 命名空间，会遮蔽 System.Windows.Forms.Control，
        // 所以控件基类必须写全名，不能只写 Control。

        /// <summary>
        /// 已做过"点→像素"换算的控件。换算必须幂等: 外观字体可能保留数值、把单位改回 Point,
        /// 同一控件被换算第二次就会再乘一次 96/72 (实测 11pt→14.67→19.56, 字体越换越大)。
        /// </summary>
        private static readonly ConditionalWeakTable<System.Windows.Forms.Control, object> convertedFonts =
            new ConditionalWeakTable<System.Windows.Forms.Control, object>();

        private static readonly object convertedMark = new object();

        private static void FreezeToPixelFonts(System.Windows.Forms.Control root)
        {
            try
            {
                Font current = root.Font;
                if (current != null && !convertedFonts.TryGetValue(root, out _))
                {
                    convertedFonts.Add(root, convertedMark);
                    if (current.Unit != GraphicsUnit.Pixel)
                    {
                        // Point→像素按 96DPI。不按当前系统缩放，否则和 UNAWARE 打架。
                        float px = current.SizeInPoints * 96f / 72f;
                        if (px < 8f)
                            px = 12f;
                        root.Font = new Font(current.FontFamily, px, current.Style, GraphicsUnit.Pixel);

                        // 写完回读: 外观字体把像素数值当点数收下时 (单位被改回 Point),
                        // 用设计期点值写回, 否则控件会比设计稿大 33% (且下次换算再放大一次)。
                        Font applied = root.Font;
                        if (applied != null && applied.Unit != GraphicsUnit.Pixel)
                        {
                            root.Font = new Font(current.FontFamily, current.SizeInPoints, current.Style, GraphicsUnit.Point);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DPI 字体像素化失败: " + root.Name + " " + ex.GetType().Name + ": " + ex.Message);
            }

            foreach (System.Windows.Forms.Control child in root.Controls)
                FreezeToPixelFonts(child);
        }
    }
}
