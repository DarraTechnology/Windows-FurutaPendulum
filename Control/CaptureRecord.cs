using System;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 整圈捕获里关键点的类型。名字只用于显示与参数文件，不要用来做控制判据。
    /// </summary>
    public enum CaptureKeyPointKind : byte
    {
        /// <summary>点击「捕获」那一拍（零点参考）。</summary>
        Start = 0,
        /// <summary>抬升途中越过水平线（抬到 90° 附近）。</summary>
        Lift = 1,
        /// <summary>在顶部窗口内稳定住（已上膛，人可以松手了）。</summary>
        Armed = 2,
        /// <summary>释放瞬间（本次记录的时间锚点）。</summary>
        Release = 3,
        /// <summary>释放后累计转过 90°。</summary>
        Quarter = 4,
        /// <summary>累计 180°。</summary>
        Half = 5,
        /// <summary>累计 270°。</summary>
        ThreeQuarter = 6,
        /// <summary>累计 360° = 完整一圈（有效捕获的完成点）。</summary>
        FullTurn = 7,
        /// <summary>摆动极值点（摆杆角速度换向）。</summary>
        TurningPoint = 8,
        /// <summary>释放后 |ω| 最大点（一般在最低点附近）。</summary>
        MaxSpeed = 9,
        /// <summary>记录结束（有效 / 失效都写一条）。</summary>
        End = 10
    }

    /// <summary>
    /// 整圈捕获的一个关键点。时间是相对点击「捕获」的秒数（释放点 = <see cref="CaptureRecord.LiftSeconds"/>），
    /// 绝对时刻用 <see cref="CaptureRecord.StartTimestampUtc"/> 加这个秒数还原。
    /// 角速度 / 角加速度用 20 ms 窗口差分，不用 1 拍差分（1 拍差分被计数分辨率毁掉，大量相邻拍差为 0）。
    /// </summary>
    public struct CaptureKeyPoint
    {
        /// <summary>相对点击「捕获」的秒数。</summary>
        public double TimeSeconds;

        public CaptureKeyPointKind Kind;

        /// <summary>绝对摆杆角 [deg]：0 = 自然下垂，180 = 倒立，连续不折返（跨圈不归一）。</summary>
        public double ThetaDeg;

        /// <summary>相对释放点的累计单向角 [deg]；按释放方向为正，反向为负。释放前的关键点恒 0（无意义，看 <see cref="ThetaDeg"/>）。</summary>
        public double SweepDeg;

        /// <summary>窗口角速度 [rad/s]；<see cref="HasDerivative"/> = false 时无意义（写 0）。</summary>
        public double OmegaRadPerSec;

        /// <summary>窗口角加速度 [rad/s²]；<see cref="HasDerivative"/> = false 时无意义（写 0）。</summary>
        public double AlphaRadPerSec2;

        /// <summary>该时刻摆臂角 [deg]。</summary>
        public double ArmDeg;

        /// <summary>该时刻摆臂角速度 [rad/s]（窗口差分只用于摆杆；摆臂给控制层滤波值）。</summary>
        public double ArmOmegaRadPerSec;

        /// <summary>窗口差分是否有足够样本；false = 该点离起点太近，ω/α 不可用。</summary>
        public bool HasDerivative;
    }

    /// <summary>
    /// 一次「整圈捕获」的结果快照。
    /// 流程：摆杆在自然下垂（0°）静止 → 点捕获（记零点参考）→ 抬到顶部 180° → 释放 →
    /// 摆杆绕一整圈（释放方向累计 360°）为有效；「左侧来、左侧回」（方向反转 / 没绕满）为失效。
    ///
    /// 兼容字段（旧「顶部释放捕获 → 平衡保持」口径）继续保留并赋新语义，避免参数文件与界面断代：
    /// <see cref="Caught"/> = <see cref="Valid"/>；<see cref="HoldSeconds"/> = 整圈耗时；
    /// <see cref="Fell"/> 新记录恒 false（整圈捕获没有「跌倒」概念，失效原因看 <see cref="InvalidReason"/>）。
    /// </summary>
    public struct CaptureRecord
    {
        /// <summary>释放时刻（UTC）。记录按这个时间排序与显示；未释放就失效时 = 点击捕获时刻。</summary>
        public DateTime TimestampUtc;

        /// <summary>点击「捕获」的时刻（UTC），关键点相对时间的基准。</summary>
        public DateTime StartTimestampUtc;

        /// <summary>释放瞬间的摆杆绝对角 [deg]（0 = 自然下垂，180 = 倒立，不归一）。</summary>
        public double ReleaseThetaDeg;

        /// <summary>释放瞬间的摆杆角速度 [rad/s]（带符号）。</summary>
        public double ReleaseOmegaRadPerSec;

        /// <summary>本次捕获是否有效（= 朝释放方向累计绕满 360°）。</summary>
        public bool Caught;

        /// <summary>整圈耗时 [s]（释放 → 绕满 / 判失效）；未释放 = 0。</summary>
        public double HoldSeconds;

        /// <summary>本次记录期间 |θ−180°| 的最大值 [deg]。</summary>
        public double MaxDeviationDeg;

        /// <summary>释放 → 绕满一圈的用时 [s]；无效 = <see cref="double.NaN"/>。</summary>
        public double CaptureDelaySeconds;

        /// <summary>旧口径的「跌倒」标记。整圈捕获恒 false，失效看 <see cref="InvalidReason"/>。</summary>
        public bool Fell;

        // ==================== 整圈捕获专有字段 ====================

        /// <summary>有效 = 朝释放方向累计绕满 360°。恒等于 <see cref="Caught"/>（保留两名字给新旧调用方）。</summary>
        public bool Valid;

        /// <summary>失效原因（可读中文，带证据数字）；有效 = null。</summary>
        public string InvalidReason;

        /// <summary>点击捕获时的摆杆绝对角 [deg]（零点参考）。</summary>
        public double StartThetaDeg;

        /// <summary>点击捕获时的摆杆原始计数（零点参考；跨圈判定全靠它和之后的计数差）。</summary>
        public int StartPendulumCount;

        /// <summary>点击捕获 → 释放 的用时 [s]（抬升段）；未释放 = 0。</summary>
        public double LiftSeconds;

        /// <summary>释放 → 结束 的用时 [s]；未释放 = 0。</summary>
        public double RevolutionSeconds;

        /// <summary>结束时相对释放点的有符号累计角 [deg]（按释放方向为正）。</summary>
        public double SweepDeg;

        /// <summary>释放方向上的最大累计角 [deg]；≥360 才算有效。</summary>
        public double MaxSweepDeg;

        /// <summary>抬升方向：+1 / -1（释放后必须继续朝这个方向转才算绕圈）。0 = 判不出（按 +1 处理）。</summary>
        public int LiftSign;

        /// <summary>本次记录期间见过的最大 |ω| [rad/s]。</summary>
        public double MaxOmegaRadPerSec;

        /// <summary>关键点（按时间先后）。上限 <see cref="MaxKeyPoints"/> 条。</summary>
        public CaptureKeyPoint[] KeyPoints;

        /// <summary>单次记录最多保存的关键点数（防止参数文件被一次长摆撑大）。</summary>
        public const int MaxKeyPoints = 24;

        /// <summary>关键点条数（KeyPoints 为 null 时 0）。</summary>
        public int KeyPointCount
        {
            get { return KeyPoints == null ? 0 : KeyPoints.Length; }
        }
    }
}
