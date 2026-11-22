using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
// 本仓有命名空间 FurutaPendulum.Control（Control/ 目录），裸 Control 会被解析成命名空间，
// 界面控件类型必须走别名。
using WinControl = System.Windows.Forms.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 界面控件原语唯一实现：遍历 / 按名查找 / 点击 / 按住（鼠标按下 · 抬起）。
    /// <para>
    /// 自动化通道（<c>MainForm.Automation.cs</c>，实时协议）与审计通道（<c>MainForm.Audit.cs</c>，
    /// 批量断言）是两条独立消费方，但底下这四件事只允许有这一份实现：两条通道各自只保留
    /// 自己的状态门与应答 / 报表格式，不再复制控件树操作。
    /// </para>
    /// <para>
    /// 只用本机进程内控件树与 WinForms 公开 / 受保护入口，不截图、不依赖 UIA、不猜坐标。
    /// </para>
    /// </summary>
    internal static class UiControlFinder
    {
        // 点动这类「按住才动」的按钮必须走真实 OnMouseDown / OnMouseUp（与鼠标同一条生产路径），
        // 直接调 Click 不会进入 MouseDown 处理器。WinForms 没有公开这两个入口，只能反射受保护虚方法。
        private static readonly MethodInfo MouseDownMethod =
            typeof(WinControl).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo MouseUpMethod =
            typeof(WinControl).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>按下 / 抬起入口在当前运行时是否可用（正常 WinForms 恒为 true）。</summary>
        internal static bool MouseEventsAvailable
        {
            get { return MouseDownMethod != null && MouseUpMethod != null; }
        }

        /// <summary>先序遍历整棵控件树（含 <paramref name="root"/> 本身），顺序与 WinForms 子控件顺序一致。</summary>
        internal static void CollectAll(WinControl root, List<WinControl> sink)
        {
            if (root == null)
                return;
            sink.Add(root);
            foreach (WinControl child in root.Controls)
                CollectAll(child, sink);
        }

        /// <summary>收集指定类型的后代控件（不含 <paramref name="root"/> 本身），先序。</summary>
        internal static void Collect<T>(WinControl root, List<T> sink) where T : WinControl
        {
            foreach (WinControl child in root.Controls)
            {
                T item = child as T;
                if (item != null)
                    sink.Add(item);
                Collect(child, sink);
            }
        }

        /// <summary>
        /// 按名称查找：匹配 <see cref="WinControl.Name"/> 或 <see cref="WinControl.AccessibleName"/>
        /// （Ordinal），<paramref name="root"/> 本身也参与匹配。找不到返回 null。
        /// </summary>
        internal static WinControl FindByName(WinControl root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;
            if (string.Equals(root.Name, name, StringComparison.Ordinal) ||
                string.Equals(root.AccessibleName, name, StringComparison.Ordinal))
                return root;
            foreach (WinControl child in root.Controls)
            {
                WinControl found = FindByName(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>控件所属窗体（沿 Parent 链上溯）；不在窗体里返回 null。</summary>
        internal static Form FindForm(WinControl control)
        {
            WinControl cursor = control;
            while (cursor != null)
            {
                Form form = cursor as Form;
                if (form != null)
                    return form;
                cursor = cursor.Parent;
            }
            return null;
        }

        /// <summary>
        /// 真实点击（<see cref="Button.PerformClick"/>，与鼠标点击同一条生产路径）。
        /// Enabled 门与应答文本由调用方决定：审计要求「不可用就别点」，自动化要回 enabled=False
        /// 而不是静默跳过，所以这里不收门。
        /// </summary>
        internal static void Press(Button button)
        {
            button.PerformClick();
        }

        /// <summary>
        /// 真实按下 / 抬起：反射调用非公开的 <c>Control.OnMouseDown</c> / <c>OnMouseUp</c>，
        /// 虚派发到实际按钮的处理器（与鼠标事件同一条路径）。
        /// 返回 false = 运行时取不到该方法或控件为空，调用方按自身格式报错。
        /// </summary>
        internal static bool TryRaiseMouse(WinControl control, bool down)
        {
            MethodInfo method = down ? MouseDownMethod : MouseUpMethod;
            if (control == null || method == null)
                return false;
            method.Invoke(control, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 1, 1, 0) });
            return true;
        }
    }
}
