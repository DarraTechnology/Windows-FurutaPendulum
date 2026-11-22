using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 应用侧软实时设置：进程优先级、当前控制线程优先级与单核亲和性、系统计时分辨率。
    /// 所有入口都只在初始化或退出时调用，不进入周期控制路径；任何失败只留日志，不打断控制。
    /// </summary>
    internal static class SoftRealTime
    {
        private const uint TimerPeriodMilliseconds = 1;
        private const uint TimerNoError = 0;
        private static bool _timerResolutionEnabled;

        /// <summary>对应 SetPriorityClass(REALTIME_PRIORITY_CLASS)，只调整当前进程。</summary>
        public static void ApplyProcess()
        {
            try
            {
                Process process = Process.GetCurrentProcess();
                process.PriorityClass = ProcessPriorityClass.RealTime;
                ProcessPriorityClass actual = process.PriorityClass;
                if (actual != ProcessPriorityClass.RealTime)
                {
                    Log("进程优先级设置未生效：期望 RealTime，实际 " + actual + "。");
                    return;
                }
                Log("SetPriorityClass(REALTIME_PRIORITY_CLASS) 已应用，实际进程优先级=" + actual + "。");
            }
            catch (Exception ex)
            {
                LogFailure("SetPriorityClass(REALTIME_PRIORITY_CLASS) 失败（可能缺少权限）", ex);
            }
        }

        /// <summary>
        /// 把当前本地线程设为 Highest，并对应 SetThreadAffinityMask 固定到指定 CPU。
        /// 索引先校验再改动；亲和性失败时恢复原线程优先级，避免留下半套设置。
        /// </summary>
        public static bool ApplyCurrentThread(int cpuIndex)
        {
            int processorCount = Environment.ProcessorCount;
            int maskWidth = IntPtr.Size * 8;
            if (cpuIndex < 0 || cpuIndex >= processorCount || cpuIndex >= maskWidth)
            {
                Log(string.Format(CultureInfo.InvariantCulture,
                    "SetThreadAffinityMask 拒绝越界 CPU={0}：有效索引为 0..{1}，当前掩码宽度={2} 位；线程设置保持不变。",
                    cpuIndex, Math.Max(0, Math.Min(processorCount, maskWidth) - 1), maskWidth));
                return false;
            }

            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority;
            ThreadPriority actualPriority;
            try
            {
                originalPriority = thread.Priority;
                // CLR 经线程基础优先级接口落实该设置，对应文档的 ThreadBasePriority 应用侧配置。
                thread.Priority = ThreadPriority.Highest;
                actualPriority = thread.Priority;
            }
            catch (Exception ex)
            {
                LogFailure("设置当前控制线程 ThreadPriority.Highest 失败", ex);
                return false;
            }

            UIntPtr mask = new UIntPtr(1UL << cpuIndex);
            UIntPtr previousMask;
            try
            {
                previousMask = NativeMethods.SetThreadAffinityMask(
                    NativeMethods.GetCurrentThread(), mask);
            }
            catch (Exception ex)
            {
                RestoreThreadPriority(thread, originalPriority);
                LogFailure("SetThreadAffinityMask 调用异常，CPU=" + cpuIndex.ToString(CultureInfo.InvariantCulture), ex);
                return false;
            }

            if (previousMask == UIntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                RestoreThreadPriority(thread, originalPriority);
                LogFailure("SetThreadAffinityMask 失败，CPU=" + cpuIndex.ToString(CultureInfo.InvariantCulture) +
                    "，Win32Error=" + errorCode.ToString(CultureInfo.InvariantCulture),
                    new Win32Exception(errorCode));
                return false;
            }

            Log(string.Format(CultureInfo.InvariantCulture,
                "控制线程软实时设置已应用：CPU={0}，实际线程优先级={1}，实际亲和性掩码=0x{2:X}，原亲和性掩码=0x{3:X}。",
                cpuIndex, actualPriority, mask.ToUInt64(), previousMask.ToUInt64()));
            return true;
        }

        /// <summary>对应 timeBeginPeriod(1)；只有成功后才允许成对恢复。</summary>
        public static void EnableTimerResolution()
        {
            if (_timerResolutionEnabled) return;
            try
            {
                uint result = NativeMethods.timeBeginPeriod(TimerPeriodMilliseconds);
                if (result != TimerNoError)
                {
                    Log("timeBeginPeriod(1) 失败，MMRESULT=" + result.ToString(CultureInfo.InvariantCulture) + "。");
                    return;
                }
                _timerResolutionEnabled = true;
                Log("timeBeginPeriod(1) 已应用，系统计时分辨率请求为 1 ms。");
            }
            catch (Exception ex)
            {
                LogFailure("timeBeginPeriod(1) 调用异常", ex);
            }
        }

        /// <summary>与成功的 timeBeginPeriod(1) 成对调用 timeEndPeriod(1)。</summary>
        public static void RestoreTimerResolution()
        {
            if (!_timerResolutionEnabled) return;
            try
            {
                uint result = NativeMethods.timeEndPeriod(TimerPeriodMilliseconds);
                if (result != TimerNoError)
                {
                    Log("timeEndPeriod(1) 失败，MMRESULT=" + result.ToString(CultureInfo.InvariantCulture) + "。");
                    return;
                }
                _timerResolutionEnabled = false;
                Log("timeEndPeriod(1) 已恢复本次计时分辨率请求。");
            }
            catch (Exception ex)
            {
                LogFailure("timeEndPeriod(1) 调用异常", ex);
            }
        }

        private static void RestoreThreadPriority(Thread thread, ThreadPriority priority)
        {
            try
            {
                thread.Priority = priority;
            }
            catch (Exception ex)
            {
                LogFailure("亲和性设置失败后恢复原线程优先级时出错", ex);
            }
        }

        private static void LogFailure(string operation, Exception ex)
        {
            Log(operation + "：" + DescribeException(ex));
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null) return "未知错误";
            string stack = ex.StackTrace ?? string.Empty;
            if (stack.Length > 500) stack = stack.Substring(0, 500);
            return ex.GetType().Name + ": " + ex.Message + "；StackTrace=" + stack;
        }

        /// <summary>
        /// 初始化与退出路径的同步日志：沿用统一日志根，不创建后台线程，也不在控制周期内调用。
        /// 日志自身失败只留 Debug 痕迹，绝不反向影响启动、退出或控制线程。
        /// </summary>
        private static void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                "] [soft_real_time] " + (message ?? string.Empty);
            Debug.WriteLine(line);
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DarraPLC", "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory,
                    "furuta_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("软实时日志落盘失败：" + DescribeException(ex));
            }
        }

        /// <summary>本功能全部 Win32 / 多媒体计时声明的唯一位置。</summary>
        private static class NativeMethods
        {
            /// <summary>取得当前线程伪句柄，只用于当前线程自身的原生调用。</summary>
            [DllImport("kernel32.dll")]
            internal static extern IntPtr GetCurrentThread();

            /// <summary>设置线程亲和性掩码；成功返回原掩码，失败返回零并保留 Win32 错误码。</summary>
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

            /// <summary>请求系统计时周期，单位毫秒。</summary>
            [DllImport("winmm.dll")]
            internal static extern uint timeBeginPeriod(uint uPeriod);

            /// <summary>撤销此前成功请求的系统计时周期，单位毫秒。</summary>
            [DllImport("winmm.dll")]
            internal static extern uint timeEndPeriod(uint uPeriod);
        }
    }
}
