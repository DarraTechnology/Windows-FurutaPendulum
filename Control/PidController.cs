using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 控制律里反复用到的数值小工具。全部纯函数，无状态、无分配。
    /// </summary>
    internal static class ControlMath
    {
        public const double TwoPi = 6.283185307179586476925286766559;

        /// <summary>net472 没有 double.IsFinite，这里自己判。NaN 与 ±∞ 一律当非有限。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// 把角折到 [-π, π]。手写 ±2π 归一，不用 % —— C# 对负数取模的符号跟数学上的余数不一样。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double WrapPi(double x)
        {
            if (!IsFinite(x)) return 0;
            const double pi = 3.1415926535897932384626433832795;
            double twoPi = TwoPi;
            // 先粗归一，防止调用方传进来一个绕了很多圈的角。
            double y = x;
            if (y > twoPi || y < -twoPi)
            {
                y = y - twoPi * Math.Floor((y + pi) / twoPi);
            }
            while (y > pi) y -= twoPi;
            while (y < -pi) y += twoPi;
            return y;
        }

        /// <summary>
        /// Hermite 平滑阶跃：x ≤ e0 返回 0，x ≥ e1 返回 1，中间按 3t²−2t³ 过渡。
        /// 增益调度用它，避免在调度区间两端出现一阶不可导的折角。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SmoothStep(double e0, double e1, double x)
        {
            if (!IsFinite(e0) || !IsFinite(e1) || !IsFinite(x)) return 0;
            if (!(e1 > e0)) return x >= e1 ? 1.0 : 0.0;
            double t = (x - e0) / (e1 - e0);
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
        /// 从 previous 往 target 走，单步最多 maxDelta。就是阶跃护栏 / 速度限幅。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Slew(double target, double previous, double maxDelta)
        {
            if (!(maxDelta > 0) || !IsFinite(maxDelta)) return target;
            double delta = target - previous;
            if (delta > maxDelta) return previous + maxDelta;
            if (delta < -maxDelta) return previous - maxDelta;
            return target;
        }

        /// <summary>
        /// 驱动器 0x606C 实际速度（**单位 Pulse/s**，即驱动位置单位/秒）→ 摆臂角速度 [rad/s]。
        /// 换算链只有一条：`rad/s = Pulse/s ÷ 每转位置单位 × 2π`。
        /// <para>
        /// 每转位置单位 = 「摆臂转一圈等于多少个 0x6064 / 0x606C 位置单位」，由标定链给出，
        /// 不是经验系数：有 0x608F / 0x6091 的机型按 OD 实读合成（0x608F:01 × 0x6091:01/0x6091:02），
        /// EC1 这类没有这两个对象的按 0x2102 细分实读 P × 减速比（CPR_ARM）。两条路都在
        /// <see cref="PendulumEngine.ResolveArmCalibration"/> 里裁决成同一个数，本函数只认结果。
        /// </para>
        /// <para>
        /// 为什么不是 rpm：手册 4.1.3 与 V1.0.1 修订历史写 rpm，但 5.3 表写 Pulse/s，实机实测站得住的是
        /// Pulse/s —— 本台架 EC1 回 0x606C = 2000/3000（量化步长 1000），按 Pulse/s ÷ 10000 × 2π 得
        /// 1.257/1.885 rad/s，与同拍 φ 差分速度上限 1.66 rad/s 一致；按 rpm 换算得 209/314 rad/s，
        /// 比 Ω_arm_max=12 rad/s 大 20 倍以上。详见 README「0x606C 单位」一节。
        /// </para>
        /// <para>
        /// 0x606B / 0x6081 也是 Pulse/s，与 0x606C 同口径；位置差分（0x6064）同样是 Pulse，
        /// 三者的计数口径一致，别再把某一个当 rpm 单独换算。
        /// </para>
        /// <para>
        /// ⚠ 本台架 EC1 回的是**幅值**（|速度|）：30 s 起摆里摆臂反复换向，0x606C 23k 拍无一为负；
        /// 且量化步长 1000 Pulse/s（≈0.628 rad/s），低速段在 0 与该步长之间跳。所以换算结果是 |φ̇|，
        /// 只能当速度表 / 粗交叉校验，**不能**给出带符号的 φ̇（那一路只走 φ 差分）。
        /// </para>
        /// </summary>
        public static double ArmRadPerSecFromDrivePulsePerSec(double pulsePerSec, double countsPerRevArm)
        {
            if (!IsFinite(pulsePerSec) || !(countsPerRevArm > 0) || !IsFinite(countsPerRevArm)) return 0;
            return pulsePerSec * TwoPi / countsPerRevArm;
        }

        /// <summary>
        /// 控制增益的统一显示精度：5 位小数，固定宽度，InvariantCulture（与界面 UiStyles.Gain 同口径）。
        /// <para>
        /// 增益（Ka / Kad / Kai / Kφ / Kφ̇ / KpΦ / KiΦ / KdΦ / Ke）一律走这里，不要在调用点散落格式串：
        /// 内环 KdΦ 出厂 0.0009、Kai 与辨识出的 k_e 都可能落在千分位以下，2~3 位小数会把它们显示成
        /// 0.001 / 0.00，现场照显示值回写就吃掉稳定裕量。角度、速度、能量、限幅、滤波常数等物理量
        /// 不要用这个，按各自既有精度显示（3 位足够，扩精度只会破坏既有输出）。
        /// </para>
        /// <para>非有限值写 n/a（防止 NaN 混进日志）。只用于诊断/日志拼串，不进周期路径。</para>
        /// </summary>
        public static string Gain(double value)
        {
            if (!IsFinite(value)) return "n/a";
            return value.ToString("0.00000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 32 位计数差分（处理回绕）：now - zero 先按无符号减法，再按位转有符号。
        /// GC1502 的计数是 UDINT，越过 0xFFFFFFFF→0 会绕回；这样算能从"0xFFFFFFFE → 0x00000001"
        /// 拿到正确的 +3 而不是 -4294967293。GC1602 的 DINT 不绕回时结果与直接相减完全一致。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CountDelta(uint now, uint zero)
        {
            return unchecked((int)(now - zero));
        }
    }

    /// <summary>周期常量。周期由 DENI 定死，控制律一律按固定 dt 推进，不拿墙钟当 dt。</summary>
    internal static class CycleConstants
    {
        /// <summary>默认总线周期 500000 ns = 500µs = 2000 Hz。</summary>
        public const uint DefaultCycleNs = 500000;

        /// <summary>固定控制周期 [s]。与 PendulumEngineOptions.CycleNs 换算结果保持一致。</summary>
        public const double CycleSeconds = 0.0005;

    }
}
