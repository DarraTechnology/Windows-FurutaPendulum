using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DarraEtherCAT_Master;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// SDK 预热：把 Darra.Core 的 native 加载 + 网卡枚举提前做掉，别落在用户点「连接」的那一刻
    /// （实测冷启动首次 GetNetworkAdapters 约 5.6 s，几乎全花在 native 加载上）。
    /// <para>
    /// 只枚举不活扫（includeSlaveCount = false）：带从站数的活扫会留 temp owner / 隔离，
    /// 不收口就让随后的 SetENI + Build 扫出 0 从站。
    /// 预热失败绝不影响连接：异常在内部吞掉，只写 Debug，不写用户可见日志。
    /// </para>
    /// </summary>
    internal static class SdkWarmup
    {
        private static readonly object Gate = new object();
        private static Task _warmTask;

        /// <summary>
        /// 幂等预热：整个进程只跑一次，后续调用返回同一个 Task（已完成就立刻返回）。
        /// GUI 在创建窗口之前调用且不等待；<see cref="PendulumEngine.Connect"/> 入口再等一次同一个 Task。
        /// </summary>
        public static Task WarmSdkAsync()
        {
            lock (Gate)
            {
                if (_warmTask == null) _warmTask = Task.Run(new Action(WarmCore));
                return _warmTask;
            }
        }

        private static void WarmCore()
        {
            try
            {
                DarraEtherCAT.GetNetworkAdapters(false);
            }
            catch (Exception ex)
            {
                // 预热只是省时间，失败不改变任何连接语义；不写用户可见日志。
                Debug.WriteLine("SDK 预热失败（不影响连接）：" + ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
