using System;
using System.Collections.Generic;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>控制参数对话框里「起摆参数」组的可编辑档位，与 CLI <c>setswingup</c> 状态门一一对应。</summary>
    internal enum SwingParamAccess
    {
        /// <summary>Disconnected / Enabled / Stopped：整组可改（离线配置 / 使能后起摆前整定 / 停机整定）。</summary>
        Editable,

        /// <summary>Swinging：只允许 CLI 的热改项，其余起摆字段锁定到起摆结束。</summary>
        HotEditOnly,

        /// <summary>Connected / Balancing / Fault：整组拒绝，原因与 CLI 同一段中文文案。</summary>
        Locked
    }

    /// <summary>
    /// 起摆参数写入门（UI 侧唯一实现）。
    /// <para>
    /// 规则唯一来源 = <c>Cli/CliSession.Commands.cs</c> 的 <c>SetSwingUpChangeRejectText</c> /
    /// <c>SwingHotEditFlags</c>：UI 不能引用 CLI 会话内部方法，这里按同一套状态、同一段中文原因镜像。
    /// 两处不一致时以 CLI 为准；改状态门必须同时改这两处。
    /// </para>
    /// <list type="bullet">
    /// <item>Connected：拒绝 —— 伺服未使能，写进去没有任何运行反馈，不能算「已生效」。</item>
    /// <item>Balancing：拒绝 —— 起摆参数已不在当前控制律里，先 stop。</item>
    /// <item>Fault：拒绝 —— 先 fault-reset。</item>
    /// <item>Swinging：只放行热改项；捕获交棒 / 慢漂移等机制参数一律锁定。</item>
    /// <item>Disconnected / Enabled / Stopped：允许。</item>
    /// </list>
    /// </summary>
    internal static class SwingParamGate
    {
        /// <summary>界面里可热改的起摆项（CLI 的 --extract-gain / --center-band / --capture-arm-rate 对应项）。</summary>
        internal const string HotEditFieldsText = "「捕获臂速上限」「抽取增益 ExtractGain」「软中心带 CenterBandRad」";

        /// <summary>
        /// 解析当前状态下的起摆参数访问档位。
        /// <paramref name="reason"/>：Locked = CLI 同口径的整组拒绝原因；HotEditOnly = 热改范围说明；Editable = null。
        /// </summary>
        internal static SwingParamAccess Resolve(EngineState state, out string reason)
        {
            switch (state)
            {
                case EngineState.Connected:
                    reason = "当前 Connected（总线已连接、伺服未使能）：起摆参数只在起摆 / 平衡闭环里生效，"
                        + "此时写入会被接受却没有任何运行反馈，不能算「已生效」。先 enable 再改，"
                        + "或断开后离线配置，或停到 Stopped 再改。";
                    return SwingParamAccess.Locked;
                case EngineState.Balancing:
                    reason = "当前 Balancing（正在平衡）：起摆参数已不在当前控制律里，改它会让下一次起摆 / 切换的节奏"
                        + "与当前现场不一致。先 stop 再改。";
                    return SwingParamAccess.Locked;
                case EngineState.Fault:
                    reason = "当前 Fault：先 fault-reset 清除故障，再改起摆参数。";
                    return SwingParamAccess.Locked;
                case EngineState.Swinging:
                    // 与 CLI 的 Swinging 热改集合一致：--energy-extract / --energy-deadband / --extract-gain /
                    // --center-band / --capture-arm-rate。对话框只暴露后三项，前两项没有输入框、不会变化。
                    reason = "当前 Swinging（起摆中）：只允许热改 " + HotEditFieldsText +
                        "（能量抽取比例 / 死区没有输入框）；其余起摆参数等起摆结束再改。";
                    return SwingParamAccess.HotEditOnly;
                default:
                    reason = null;
                    return SwingParamAccess.Editable;
            }
        }

        /// <summary>
        /// 起摆中复核：对话框打开后状态可能变化（例如途中起摆），此时只能应用热改项。
        /// 返回非 null = 有被锁定的起摆字段被改动，调用方必须整组不写并展示本原因。
        /// </summary>
        internal static string DescribeLockedChanges(SwingUpParams before, SwingUpParams after)
        {
            List<string> changed = new List<string>();
            if (!Same(before.TargetEnergyGain, after.TargetEnergyGain)) changed.Add("能量增益 ε");
            if (!Same(before.AMax, after.AMax)) changed.Add("起摆加速度 AMax");
            if (!Same(before.ThetaSwitchRad, after.ThetaSwitchRad)) changed.Add("切换角 θ");
            if (!Same(before.OmegaSwitchRadPerSec, after.OmegaSwitchRadPerSec)) changed.Add("切换角速度 ω");
            if (!Same(before.KPhi, after.KPhi)) changed.Add("起摆回中 Kφ");
            if (!Same(before.KPhiDot, after.KPhiDot)) changed.Add("起摆阻尼 Kφ̇");
            if (changed.Count == 0)
                return null;
            return "当前 Swinging（起摆中）：只允许热改 " + HotEditFieldsText + "；本次还改了 " +
                string.Join("、", changed.ToArray()) +
                "，起摆参数整组未写入（不做半套生效）。等起摆结束再改这些字段。";
        }

        /// <summary>只把热改字段从 <paramref name="after"/> 合进 <paramref name="before"/>，其余字段保持原值。</summary>
        internal static SwingUpParams MergeHotEditFields(SwingUpParams before, SwingUpParams after)
        {
            before.CaptureArmRateLimitRadPerSec = after.CaptureArmRateLimitRadPerSec;
            before.ExtractGain = after.ExtractGain;
            before.CenterBandRad = after.CenterBandRad;
            before.EnergyExtractRatio = after.EnergyExtractRatio;
            before.EnergyDeadbandRatio = after.EnergyDeadbandRatio;
            return before;
        }

        private static bool Same(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b))
                return double.IsNaN(a) && double.IsNaN(b);
            return a == b;
        }
    }
}
