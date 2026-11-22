using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 主界面的本机 AI 注入通道（127.0.0.1 TCP，一行命令 / 多行应答）。
    /// 只在显式指定端口时由 <see cref="MainForm"/> 启动；默认不监听，不改变生产行为。
    /// 线程模型：接受与读写都在后台线程，命令统一投递回 UI 线程执行（与鼠标点击同一条生产路径）。
    /// </summary>
    internal sealed class UiAutomationServer : IDisposable
    {
        private const int MaxCommandBytes = 4096;

        private readonly MainForm _form;
        private readonly TcpListener _listener;
        private readonly Thread _acceptThread;
        private volatile bool _stopping;

        private UiAutomationServer(MainForm form, TcpListener listener)
        {
            _form = form;
            _listener = listener;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "FurutaUiAutomation";
        }

        public static UiAutomationServer Start(MainForm form, int port)
        {
            if (form == null)
                throw new ArgumentNullException("form");
            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            UiAutomationServer server = new UiAutomationServer(form, listener);
            server._acceptThread.Start();
            return server;
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (_stopping)
                        return;
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                try
                {
                    HandleClient(client);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("UI 注入通道处理失败：" + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    try { client.Close(); }
                    catch (Exception) { }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                string line = reader.ReadLine();
                if (line == null)
                    return;
                if (line.Length > MaxCommandBytes)
                    line = line.Substring(0, MaxCommandBytes);
                string[] response;
                try
                {
                    response = Dispatch(line.Trim());
                }
                catch (Exception ex)
                {
                    response = new[]
                    {
                        "ERR " + ex.GetType().Name + ": " + ex.Message,
                        "STACK " + (ex.StackTrace ?? string.Empty).Replace('\r', ' ').Replace('\n', '|')
                    };
                }
                writer.NewLine = "\n";
                for (int i = 0; i < response.Length; i++)
                    writer.WriteLine(response[i]);
                writer.WriteLine("--END--");
                writer.Flush();
            }
        }

        private string[] Dispatch(string command)
        {
            if (_form == null || _form.IsDisposed || _form.Disposing)
                return new[] { "ERR form-disposed" };
            if (!_form.InvokeRequired)
                return _form.AutomationExecute(command);

            // 用 BeginInvoke(Action) + 事件等待：不用同步 Invoke 避免与模态对话框互相卡住；
            // Action 也是 WinForms 消息泵最稳的委托形态（同步 Invoke 的泛型委托曾触发参数计数异常）。
            string[] result = null;
            Exception failure = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                try
                {
                    _form.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            result = _form.AutomationExecute(command);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            try { done.Set(); }
                            catch (ObjectDisposedException) { }
                        }
                    }));
                }
                catch (ObjectDisposedException)
                {
                    return new[] { "ERR form-disposed" };
                }
                catch (InvalidOperationException ex)
                {
                    return new[] { "ERR begininvoke-invalid " + ex.Message };
                }
                if (!done.WaitOne(15000))
                    return new[] { "ERR invoke-timeout" };
            }
            if (failure != null)
            {
                return new[]
                {
                    "ERR " + failure.GetType().Name + ": " + failure.Message,
                    "STACK " + (failure.StackTrace ?? string.Empty).Replace('\r', ' ').Replace('\n', '|')
                };
            }
            return result ?? new[] { "ERR empty-response" };
        }

        public void Dispose()
        {
            if (_stopping)
                return;
            _stopping = true;
            try { _listener.Stop(); }
            catch (Exception) { }
            Thread thread = _acceptThread;
            if (thread != null && thread.IsAlive && !thread.Join(1500))
                Debug.WriteLine("UI 注入通道线程未在 1.5 s 内退出（后台线程，随进程结束）。");
        }
    }
}
