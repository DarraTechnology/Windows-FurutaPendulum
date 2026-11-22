using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// 异步 UTF-8 诊断日志。<b>GUI 与 CLI 共用同一个根、同一套命名、同一种行格式</b>：
    /// 落点 <c>%AppData%\DarraPLC\logs\furuta_&lt;yyyyMMdd&gt;.log</c>（与产品既有惯例一致），
    /// 行格式 <c>[yyyy-MM-dd HH:mm:ss.fff] [kind] message</c>（kind 承载原 CLI JSON 记录里的记录类别）。
    /// 控制层周期回调禁止调用（入队也有锁/分配，会把 500µs 拍拉长）；队列满则丢行计数，不阻塞写者。
    /// 单文件约 20MB 翻页（溢出页 <c>furuta_&lt;日期&gt;_NNN.log</c>），超 30 天或超 100 个文件才清。
    /// <para>
    /// <b>历史遗留（只读，不写）</b>：旧 CLI 路径 <c>%LocalAppData%\DarraEtherCAT\FurutaPendulum\logs\</c>
    /// 仅用于查历史日志，新日志一律写上面的 AppData 根；旧目录不迁移、不删除。
    /// </para>
    /// 可用 <c>--log</c> 显式改写落点。
    /// </summary>
    internal sealed class DiagnosticLogWriter : IDisposable
    {
        private const long MAX_FILE_BYTES = 20L * 1024 * 1024;

        /// <summary>文件名前缀（与 GUI 的 furuta_&lt;日期&gt;.log 同一套命名）。</summary>
        private const string FILE_PREFIX = "furuta_";

        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(8192);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _worker;
        private readonly object _fileGate = new object();
        private StreamWriter _writer;
        private string _date;
        private int _part;
        private long _droppedCount;
        private int _isHealthy = 1;
        private string _lastError;

        /// <summary>当前日志目录（--log 指到具体文件时为其所在目录）。</summary>
        public string DirectoryPath { get; }

        /// <summary>
        /// 历史遗留的 CLI 日志根（<b>只读</b>）：旧版本 CLI 写在这里，新日志统一写
        /// <see cref="DirectoryPath"/>。旧目录不迁移、不删除；查历史时与新版目录并存查看。
        /// </summary>
        public static string LegacyDirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DarraEtherCAT", "FurutaPendulum", "logs");
            }
        }

        /// <summary>当前正在写的日志文件绝对路径；尚未开写时为 null。</summary>
        public string CurrentFilePath { get; private set; }

        /// <summary>--log 显式指定的落点；为 null 时按日期翻页。</summary>
        public string FixedFilePath { get; }

        public long DroppedCount { get { return Interlocked.Read(ref _droppedCount); } }
        public bool IsHealthy { get { return Volatile.Read(ref _isHealthy) != 0; } }
        public string LastError { get { return Volatile.Read(ref _lastError); } }

        public DiagnosticLogWriter()
            : this(null)
        {
        }

        /// <summary>fixedFilePath 非空时日志固定写该文件（不翻页、不按前缀清理）。</summary>
        public DiagnosticLogWriter(string fixedFilePath)
        {
            if (!string.IsNullOrWhiteSpace(fixedFilePath))
            {
                FixedFilePath = Path.GetFullPath(fixedFilePath);
                DirectoryPath = Path.GetDirectoryName(FixedFilePath) ?? string.Empty;
            }
            else
            {
                // 唯一日志根（与 GUI 完全一致）：%AppData%\DarraPLC\logs\。
                // 旧 CLI 根 %LocalAppData%\DarraEtherCAT\FurutaPendulum\logs\ 仅历史遗留，新日志不再写那里。
                DirectoryPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DarraPLC", "logs");
            }
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (FixedFilePath == null) Cleanup();
                // 构造期就把文件建好：log-path 要能立刻报出真实路径，不必等第一条日志。
                EnsureWriter();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PublishError(ex);
            }
            _worker = Task.Run((Action)Work);
        }

        /// <summary>入队一行。队列满丢行计数、不阻塞。调用方必须在非控制周期线程。</summary>
        public void Write(string line)
        {
            if (!_queue.TryAdd(line)) Interlocked.Increment(ref _droppedCount);
        }

        /// <summary>
        /// 统一行格式（GUI 与 CLI 共用）：<c>[yyyy-MM-dd HH:mm:ss.fff] [kind] message</c>。
        /// kind 由调用方给（ui / diagnostic / ack / error / event / config_loaded 等），
        /// 不做关键词推断，也不改写成别的格式 —— 一个根、一种命名、一种行格式。
        /// </summary>
        public void Write(string kind, string message)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(kind)) kind = "log";
            Write("[" + stamp + "] [" + kind + "] " + (message ?? string.Empty));
        }

        private void Work()
        {
            try
            {
                foreach (string line in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    bool written = false;
                    for (int retry = 0; retry < 3 && !written; retry++)
                    {
                        try
                        {
                            EnsureWriter();
                            _writer.WriteLine(line);
                            _writer.Flush();
                            Volatile.Write(ref _isHealthy, 1);
                            written = true;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            PublishError(ex);
                            lock (_fileGate)
                            {
                                if (_writer != null)
                                {
                                    try { _writer.Dispose(); }
                                    catch (IOException disposeEx) { Debug.WriteLine("日志文件切换时 Dispose 失败（" + (CurrentFilePath ?? "?") + "）：" + disposeEx.Message); }
                                    _writer = null;
                                    CurrentFilePath = null;
                                }
                            }
                            if (retry < 2) Thread.Sleep(100);
                        }
                    }
                    if (!written) Interlocked.Increment(ref _droppedCount);
                }
            }
            catch (OperationCanceledException ex) { Debug.WriteLine("日志写线程按取消收尾：" + ex.Message); }
            finally
            {
                lock (_fileGate)
                {
                    if (_writer != null) _writer.Dispose();
                    _writer = null;
                }
            }
        }

        private void EnsureWriter()
        {
            lock (_fileGate)
            {
                if (FixedFilePath != null)
                {
                    if (_writer != null) return;
                    Directory.CreateDirectory(DirectoryPath);
                    _writer = Open(FixedFilePath);
                    CurrentFilePath = FixedFilePath;
                    return;
                }

                Directory.CreateDirectory(DirectoryPath);
                string date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_writer != null && _date == date && _writer.BaseStream.Length < MAX_FILE_BYTES) return;
                if (_writer != null) _writer.Dispose();
                if (_date != date) { _date = date; _part = 0; }
                string path;
                do
                {
                    // 首页 = GUI 同一套命名 furuta_<日期>.log；超 20MB 才翻到 furuta_<日期>_001.log。
                    path = _part == 0
                        ? Path.Combine(DirectoryPath, FILE_PREFIX + date + ".log")
                        : Path.Combine(DirectoryPath, FILE_PREFIX + date + "_" + _part.ToString("D3") + ".log");
                    _part++;
                }
                while (File.Exists(path) && new FileInfo(path).Length >= MAX_FILE_BYTES);
                _writer = Open(path);
                CurrentFilePath = path;
            }
        }

        private static StreamWriter Open(string path)
        {
            // FileShare.ReadWrite：GUI 与 CLI 现在共用一个日志根（甚至同一天同名文件），
            // 两个进程必须能同时追加；共享读保留，排查时可以用别的工具实时看。
            var writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(true));
            writer.AutoFlush = true;
            return writer;
        }

        private void PublishError(Exception ex)
        {
            Volatile.Write(ref _isHealthy, 0);
            Volatile.Write(ref _lastError, ex.GetType().Name + ": " + ex.Message);
        }

        private void Cleanup()
        {
            FileInfo[] files = new DirectoryInfo(DirectoryPath).GetFiles(FILE_PREFIX + "*.log");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            DateTime cutoff = DateTime.UtcNow.AddDays(-30);
            for (int i = 0; i < files.Length; i++)
                if (i >= 100 || files[i].LastWriteTimeUtc < cutoff)
                {
                    try { files[i].Delete(); }
                    catch (IOException ex) { Debug.WriteLine("清理旧日志失败（" + files[i].FullName + "）：" + ex.Message); }
                }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (!_worker.Wait(3000)) _cts.Cancel();
            _queue.Dispose();
            _cts.Dispose();
        }
    }
}
