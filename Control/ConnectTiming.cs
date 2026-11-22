using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 连接链分阶段打点：只读计时，不参与任何业务判断，也不改连接顺序。
    /// <para>
    /// 两个口径分开报，别混：
    /// ① <c>since_process_start</c> = 进程启动 → 现在（含 SDK 冷加载），冷启动总时长；
    /// ② <c>connect_total</c> = 本次 Connect 入口 → 现在（不含进程启动），"3 秒连上"按这个判。
    /// </para>
    /// <para>
    /// 进程启动基准取 <see cref="Program"/> 的 Main 第一行 <c>Mark("process_start")</c> 触发静态初始化时的
    /// <see cref="Stopwatch"/> 时间戳（<c>Process.StartTime</c> 只有 ~15.6 ms 粒度，太粗），此后永不变；
    /// 每次 <see cref="Reset"/> 把连接基准移到当时的 Connect 入口。同一个阶段名只记第一次；重复 Mark 直接忽略。
    /// </para>
    /// </summary>
    internal static class ConnectTiming
    {
        /// <summary>连接链预算 [ms]：本次 Connect → 第一拍 PDO 可读写。</summary>
        public const int BudgetMs = 3000;

        /// <summary>进程启动那一条的固定阶段名（与 Program.Main 第一行的字面量同名）。</summary>
        private const string ProcessStageName = "process_start";

        // 两个基准：进程启动（永不变）与本次 Connect 入口（Reset 重设）。
        private static readonly long ProcessTicks = Stopwatch.GetTimestamp();
        private static long _connectTicks = ProcessTicks;

        private static readonly object Gate = new object();
        private static readonly List<string> Order = new List<string>();
        private static readonly Dictionary<string, double> MsByName =
            new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>
        /// 现有诊断通道出口（由 PendulumEngine.Connect 接到引擎的 Diagnostic 事件），不另造日志系统。
        /// 未接线时（例如 Main 刚开始打点）只走 Debug 输出。
        /// </summary>
        internal static Action<string> Sink;

        /// <summary>进程启动的 Stopwatch 时间戳（Main 第一行 Mark 时取准）。</summary>
        public static long ProcessStartTicks
        {
            get { return ProcessTicks; }
        }

        /// <summary>记一个阶段。线程安全；同名阶段只保留第一次。</summary>
        public static void Mark(string stage)
        {
            if (string.IsNullOrEmpty(stage)) return;

            double ms;
            double delta;
            lock (Gate)
            {
                if (MsByName.ContainsKey(stage)) return;
                ms = ConnectMsLocked();
                double previous = Order.Count == 0 ? 0.0 : MsByName[Order[Order.Count - 1]];
                MsByName.Add(stage, ms);
                Order.Add(stage);
                delta = ms - previous;
            }

            // 打点行在锁外发：诊断订阅方做什么都不该占住计时簿。
            string line = "[TIMING] " + stage + "=" +
                          ms.ToString("F0", CultureInfo.InvariantCulture) + "ms(+" +
                          delta.ToString("F0", CultureInfo.InvariantCulture) + ")";
            Debug.WriteLine(line);
            Action<string> sink = Sink;
            if (sink == null) return;
            try
            {
                sink(line);
            }
            catch (Exception ex)
            {
                // 诊断出口失败绝不能影响连接流程，但要留痕，不空吞。
                Debug.WriteLine("ConnectTiming 诊断 sink 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>当前时间相对本次 Connect 基准的毫秒数（调用方须持有 <see cref="Gate"/>）。</summary>
        private static double ConnectMsLocked()
        {
            return (Stopwatch.GetTimestamp() - _connectTicks) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// 重新起算本次 Connect：清空阶段列表、把连接基准移到当前时刻，只保留 process_start 那一条。
        /// 进程启动基准（<see cref="ProcessStartTicks"/>）不变，所以冷启动总时长仍能单独算出来。
        /// 在 <see cref="PendulumEngine.Connect"/> 入口、<c>Mark("connect_begin")</c> 之前调用。
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                double processStartMs;
                bool keepProcessStart = MsByName.TryGetValue(ProcessStageName, out processStartMs);
                Order.Clear();
                MsByName.Clear();
                if (keepProcessStart)
                {
                    MsByName.Add(ProcessStageName, processStartMs);
                    Order.Add(ProcessStageName);
                }
                _connectTicks = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// 一行可读汇总，两个口径并列：
        /// <c>[TIMING] since_process_start=5920ms | connect_total=2100ms | sdk_load=0ms(+0) build=472ms(+472) | TOTAL=2100ms WITHIN_BUDGET</c>。
        /// 阶段链按「本次 Connect」基准给绝对毫秒 + 相对上一段增量；TOTAL 与预算裁决都按 connect_total
        /// （用户要的"3 秒连上"不含进程启动那段）。超预算时行尾给 <c>OVER_BUDGET(最慢段名)</c>。
        /// 只读渲染，可重复调用，不改变已记录的数据。
        /// </summary>
        public static string Render()
        {
            string[] names;
            double[] offsets;
            double sinceProcessStartMs;
            double connectTotalMs;
            lock (Gate)
            {
                var stages = new List<string>(Order.Count);
                var values = new List<double>(Order.Count);
                for (int i = 0; i < Order.Count; i++)
                {
                    // process_start 只当绝对基准行输出，不进本次 Connect 的阶段链。
                    if (string.Equals(Order[i], ProcessStageName, StringComparison.Ordinal)) continue;
                    stages.Add(Order[i]);
                    values.Add(MsByName[Order[i]]);
                }
                names = stages.ToArray();
                offsets = values.ToArray();
                sinceProcessStartMs = (Stopwatch.GetTimestamp() - ProcessTicks) * 1000.0 / Stopwatch.Frequency;
                connectTotalMs = ConnectMsLocked();
            }

            StringBuilder sb = new StringBuilder("[TIMING] since_process_start=");
            sb.Append(sinceProcessStartMs.ToString("F0", CultureInfo.InvariantCulture))
              .Append("ms | connect_total=")
              .Append(connectTotalMs.ToString("F0", CultureInfo.InvariantCulture))
              .Append("ms | ");

            double previous = 0.0;
            double slowestDelta = double.MinValue;
            string slowestName = "n/a";
            for (int i = 0; i < names.Length; i++)
            {
                double delta = offsets[i] - previous;
                previous = offsets[i];
                if (delta > slowestDelta)
                {
                    slowestDelta = delta;
                    slowestName = names[i];
                }
                if (i > 0) sb.Append(' ');
                sb.Append(names[i]).Append('=')
                  .Append(offsets[i].ToString("F0", CultureInfo.InvariantCulture))
                  .Append("ms(+")
                  .Append(delta.ToString("F0", CultureInfo.InvariantCulture))
                  .Append(')');
            }
            if (names.Length == 0) sb.Append("（本次 Connect 还没有阶段打点）");

            sb.Append(" | TOTAL=").Append(connectTotalMs.ToString("F0", CultureInfo.InvariantCulture)).Append("ms ");
            sb.Append(connectTotalMs > BudgetMs ? "OVER_BUDGET(" + slowestName + ")" : "WITHIN_BUDGET");
            return sb.ToString();
        }
    }
}
