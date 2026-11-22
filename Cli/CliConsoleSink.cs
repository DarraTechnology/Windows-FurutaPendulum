using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// 非阻塞控制台输出。CLI 模式下把 <see cref="Console.Out"/> / <see cref="Console.Error"/> 换成
    /// 先入队、由后台线程写出的 <see cref="QueueTextWriter"/>。
    /// <para>
    /// 为什么必须这样：标准输出被父进程重定向成管道、但父进程要等子进程退出才开始读（
    /// <c>WaitForExit()</c> 之后才 <c>ReadToEnd()</c>）时，匿名管道只有约 4 KB 缓冲。
    /// 同步写一旦写满就永远阻塞在 WriteFile 上 —— 命令其实早就跑完了，进程却退不出去，
    /// 退出码也永远交不回来。「命令越长 / --json 信封越大」越容易撞上，与命令本身无关。
    /// </para>
    /// <para>
    /// 换成队列后：主线程只入队（永不阻塞），写线程是后台线程，管道写不进去也只卡住它自己；
    /// 主线程跑完 <c>Main</c> 就返回退出码。父进程正常读取时，<see cref="Flush"/> 会在预算内
    /// 等队列排空，输出完整不丢。写线程遇到管道已关（EPIPE / 对象已释放）只记账并继续出队，
    /// 绝不把异常回灌给命令线程。
    /// </para>
    /// </summary>
    internal static class CliConsoleSink
    {
        private static QueueTextWriter _stdout;
        private static QueueTextWriter _stderr;

        /// <summary>是否已安装（只有 CLI / --help / --ui-audit 这些挂了控制台的模式才装）。</summary>
        public static bool Installed
        {
            get { return _stdout != null; }
        }

        /// <summary>待写出的行数（`Flush` 超时诊断用）。</summary>
        public static int PendingCount
        {
            get
            {
                QueueTextWriter outWriter = _stdout;
                QueueTextWriter errWriter = _stderr;
                return (outWriter == null ? 0 : outWriter.PendingCount) +
                       (errWriter == null ? 0 : errWriter.PendingCount);
            }
        }

        /// <summary>把 Console.Out / Console.Error 接到队列写线程上。可重复调用（幂等）。</summary>
        public static void Install(TextWriter stdout, TextWriter stderr)
        {
            if (_stdout != null) return;
            if (stdout == null || stderr == null) return;

            var outWriter = new QueueTextWriter(stdout, "furuta-cli-stdout");
            var errWriter = new QueueTextWriter(stderr, "furuta-cli-stderr");
            _stdout = outWriter;
            _stderr = errWriter;
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
        }

        /// <summary>
        /// 退出前等队列排空，最多等 <paramref name="timeoutMs"/> 毫秒。
        /// 返回 true = 全部写出；false = 超预算（调用方负责留一条落盘证据，然后照常退出）。
        /// 未安装时直接返回 true。
        /// </summary>
        public static bool Flush(int timeoutMs)
        {
            QueueTextWriter outWriter = _stdout;
            QueueTextWriter errWriter = _stderr;
            if (outWriter == null && errWriter == null) return true;

            long deadline = Environment.TickCount + Math.Max(0, timeoutMs);
            bool outDone = outWriter == null || outWriter.WaitForDrain(RemainingMs(deadline));
            bool errDone = errWriter == null || errWriter.WaitForDrain(RemainingMs(deadline));

            // 收尾：不再接受新行，让写线程把剩下的写完就退出（都是后台线程，不拖住进程）。
            if (outWriter != null) outWriter.Complete();
            if (errWriter != null) errWriter.Complete();
            return outDone && errDone;
        }

        private static int RemainingMs(long deadline)
        {
            int left = (int)(deadline - Environment.TickCount);
            return left > 0 ? left : 0;
        }

        // ---------- JSON 模式：把 native 层直写 stdout 改道 stderr ----------

        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private static IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        /// <summary>
        /// JSON 模式专用：把进程的 STDOUT 标准句柄改指到 STDERR。
        /// <para>
        /// 原因：Darra.Core 的 native 层在连接期会直接 <c>printf</c> 几行
        /// （<c>DarraRT: 设备句柄已打开…</c>），那几行走的是 CRT 的 stdout，绕不开 C# 的 Console.Out。
        /// 不改道，<c>--json</c> 的 stdout 就不是纯 JSON Lines（机器解析会撞上非 JSON 行）。
        /// </para>
        /// <para>
        /// 本程序自己的输出在 <see cref="Install"/> 时已经拿住真实 stdout 的句柄（StreamWriter 已包好），
        /// 之后只往那个句柄写，所以这里改标准句柄不影响 JSON 输出本身；native 那几行会落到 stderr，
        /// 人仍然看得到，只是不再污染 stdout。必须在 SDK 首次加载之前调用（CLI 在解析完参数、跑命令之前）。
        /// 返回值 = 是否真的改道成功（句柄不可用时不改，如实返回 false）。
        /// </para>
        /// </summary>
        public static bool RouteNativeStdoutToStderr()
        {
            try
            {
                IntPtr err = GetStdHandle(StdErrorHandle);
                if (err == IntPtr.Zero || err == InvalidHandleValue)
                    return false;
                if (!SetStdHandle(StdOutputHandle, err))
                {
                    Debug.WriteLine("native stdout 改道失败：SetStdHandle 返回 false，error=" + Marshal.GetLastWin32Error());
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("native stdout 改道抛异常：" + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 入队式 <see cref="TextWriter"/>：所有写入只入队，真正的写由单个后台线程做。
        /// <see cref="Flush"/> 故意是非阻塞空操作 —— 若在这里等写线程，管道写满时主线程又会被拖死，
        /// 那就等于没修（REPL 提示符 / 危险命令确认前都会调 Flush）。
        /// </summary>
        internal sealed class QueueTextWriter : TextWriter
        {
            private readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
            private readonly TextWriter _target;
            private readonly Thread _worker;
            private int _pending;
            private int _droppedAfterBrokenPipe;
            private volatile bool _broken;

            public QueueTextWriter(TextWriter target, string threadName)
            {
                if (target == null) throw new ArgumentNullException("target");
                _target = target;
                _worker = new Thread(Work)
                {
                    IsBackground = true,
                    Name = threadName ?? "furuta-cli-console",
                };
                _worker.Start();
            }

            public override Encoding Encoding
            {
                get { return _target.Encoding; }
            }

            /// <summary>还没写出的块数。写线程被管道卡住时它会一直涨，Flush 据此判超时。</summary>
            public int PendingCount
            {
                get { return Volatile.Read(ref _pending); }
            }

            /// <summary>管道已关导致丢弃的块数（尽力而为，只留作诊断）。</summary>
            public int DroppedAfterBrokenPipe
            {
                get { return Volatile.Read(ref _droppedAfterBrokenPipe); }
            }

            public bool IsBroken
            {
                get { return _broken; }
            }

            public override void Write(char value)
            {
                Write(value.ToString());
            }

            public override void Write(char[] buffer, int index, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (index < 0 || count < 0 || index + count > buffer.Length)
                    throw new ArgumentOutOfRangeException(index < 0 || count < 0 ? "index" : "count");
                if (count == 0) return;
                Write(new string(buffer, index, count));
            }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                if (_queue.IsAddingCompleted) return;
                try
                {
                    _queue.Add(value);
                    Interlocked.Increment(ref _pending);
                }
                catch (InvalidOperationException ex)
                {
                    // 已经 CompleteAdding（进程收尾阶段）：这一块丢弃，不再接。
                    Debug.WriteLine("控制台输出队列已关闭，丢弃一块输出（" + ex.GetType().Name + "）：" + ex.Message);
                }
            }

            public override void WriteLine(string value)
            {
                Write((value ?? string.Empty) + Environment.NewLine);
            }

            public override void WriteLine()
            {
                Write(Environment.NewLine);
            }

            /// <summary>非阻塞空操作。真正的冲刷由写线程每块写完立即做。</summary>
            public override void Flush()
            {
            }

            /// <summary>等队列排空（最多 timeoutMs）。返回 false = 写线程被下游卡住或管道已断。</summary>
            public bool WaitForDrain(int timeoutMs)
            {
                long deadline = Environment.TickCount + Math.Max(0, timeoutMs);
                while (Volatile.Read(ref _pending) > 0)
                {
                    if (RemainingMs(deadline) <= 0) return false;
                    Thread.Sleep(5);
                }
                return true;
            }

            /// <summary>不再接受新行；写线程把队列里剩下的写完自然退出。</summary>
            public void Complete()
            {
                try { _queue.CompleteAdding(); }
                catch (ObjectDisposedException ex) { Debug.WriteLine("控制台输出队列已释放，CompleteAdding 跳过：" + ex.Message); }
            }

            private void Work()
            {
                try
                {
                    foreach (string chunk in _queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            if (!_broken)
                            {
                                _target.Write(chunk);
                                _target.Flush();
                            }
                            else
                            {
                                Interlocked.Increment(ref _droppedAfterBrokenPipe);
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                        {
                            // 管道被父进程关掉 / 控制台已释放：后续输出已无处可写，但必须继续出队，
                            // 否则队列无限长、内存跟着涨。这里绝不把异常抛回命令线程。
                            _broken = true;
                            Interlocked.Increment(ref _droppedAfterBrokenPipe);
                            Debug.WriteLine("控制台写线程中止写出（" + ex.GetType().Name + "）：" + ex.Message);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _pending);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 消费循环本身出错（理论上只有 CompleteAdding 并发），留痕但不重抛。
                    Debug.WriteLine("控制台写线程退出：" + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }
}
