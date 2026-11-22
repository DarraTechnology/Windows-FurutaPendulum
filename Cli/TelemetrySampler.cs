using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// 只读遥测采样器（台架取证用）。CLI 是单线程顺序执行，<c>swingup</c> 等命令会阻塞命令循环，
    /// 期间没有第二条命令能取遥测；本采样器在独立线程里按 ~1 kHz 读 <see cref="PendulumEngine.TryReadTelemetry"/>
    /// 的 seqlock 快照写 CSV，<b>只读、不碰 PDO、不改控制律、不阻塞周期线程</b>。
    /// <para>
    /// 开关 = 环境变量 <c>FURUTA_TELEMETRY_CSV</c>（值 = 目标 CSV 路径）。不设 = 完全不启动线程。
    /// 正常命令行行为不受影响；写盘失败只记一行 stderr，不改变退出码。
    /// </para>
    /// </summary>
    internal static class TelemetrySampler
    {
        public const string EnvVarName = "FURUTA_TELEMETRY_CSV";

        /// <summary>启动采样；engine / path 不可用或线程启动失败时返回 null（不影响 CLI 主流程）。</summary>
        public static IDisposable Start(PendulumEngine engine, string path)
        {
            if (engine == null || string.IsNullOrWhiteSpace(path)) return null;
            Sampler sampler = null;
            try
            {
                sampler = new Sampler(engine, path);
                sampler.StartThread();
                return sampler;
            }
            catch (Exception ex)
            {
                if (sampler != null) sampler.Dispose();
                Console.Error.WriteLine("! warn telemetry_csv 采样器未启动：" + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private sealed class Sampler : IDisposable
        {
            private const int FlushEveryRows = 512;

            private readonly PendulumEngine _engine;
            private readonly string _path;
            private readonly object _gate = new object();
            private Thread _thread;
            private StreamWriter _writer;
            private volatile bool _stop;
            private long _lastSequence = long.MinValue;
            private long _rows;
            private string _lastError;

            public Sampler(PendulumEngine engine, string path)
            {
                _engine = engine;
                _path = path;
            }

            public void StartThread()
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan),
                    new UTF8Encoding(false));
                _writer.WriteLine("seq,wall_ms,state,theta_rad,theta_from_upright_rad,omega_rad_s,phi_rad,phi_dot_rad_s,"
                    + "phi_target_rad,swing_phase,switch_countdown,energy_j,u_theta_rad,u_phi_rad,"
                    + "wkc_actual,wkc_expected,fault,ctrl_word,status_word,cycle_delta_ticks,"
                    + "arm_vel_drive_rad_s,arm_vel_pulse_s,arm_pos_count,pendulum_count");
                _thread = new Thread(Loop) { IsBackground = true, Name = "telemetry-csv" };
                _thread.Priority = ThreadPriority.BelowNormal;
                _thread.Start();
            }

            private void Loop()
            {
                Stopwatch clock = Stopwatch.StartNew();
                while (!_stop)
                {
                    try
                    {
                        PendulumTelemetry t;
                        if (_engine.TryReadTelemetry(out t) && t.Sequence != _lastSequence)
                        {
                            _lastSequence = t.Sequence;
                            WriteRow(t, clock.ElapsedMilliseconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 读快照失败不该拖垮采样；记一次原因后继续（同一条错只记一次，避免刷屏）。
                        string message = ex.GetType().Name + ": " + ex.Message;
                        lock (_gate)
                        {
                            if (_lastError != message)
                            {
                                _lastError = message;
                                Console.Error.WriteLine("! warn telemetry_csv 采样失败：" + message);
                            }
                        }
                    }
                    Thread.Sleep(1);
                }
            }

            private void WriteRow(PendulumTelemetry t, long wallMs)
            {
                StringBuilder line = new StringBuilder(320);
                line.Append(t.Sequence.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(wallMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.EngineState).Append(',')
                    .Append(F6(t.ThetaRad)).Append(',')
                    .Append(F6(t.ThetaFromUprightRad)).Append(',')
                    .Append(F6(t.OmegaRadPerSec)).Append(',')
                    .Append(F6(t.PhiRad)).Append(',')
                    .Append(F6(t.PhiDotRadPerSec)).Append(',')
                    .Append(F6(t.PhiTargetRad)).Append(',')
                    .Append(F6(t.SwingUpPhase)).Append(',')
                    .Append(t.SwitchCountdown.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(F6(t.EnergyJ)).Append(',')
                    .Append(F6(t.ControlThetaOut)).Append(',')
                    .Append(F6(t.ControlPhiOut)).Append(',')
                    .Append(t.ActualWkc.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.ExpectedWkc.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.FaultCode).Append(',')
                    .Append(t.ControlWord.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.StatusWord.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.CycleDeltaTicks.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(F6(t.ArmVelocityFromDriveRadPerSec)).Append(',')
                    .Append(t.ArmVelocityPulsePerSec.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.ArmPositionCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.PendulumCount.ToString(CultureInfo.InvariantCulture));
                lock (_gate)
                {
                    if (_writer == null) return;
                    _writer.WriteLine(line.ToString());
                    _rows++;
                    if (_rows % FlushEveryRows == 0) _writer.Flush();
                }
            }

            private static string F6(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return "nan";
                return value.ToString("F6", CultureInfo.InvariantCulture);
            }

            public void Dispose()
            {
                _stop = true;
                Thread thread = _thread;
                if (thread != null && thread.IsAlive) thread.Join(3000);
                lock (_gate)
                {
                    if (_writer != null)
                    {
                        // 收尾失败必须留痕：CSV 尾部可能不完整，不能再往上抛（采样线程已在退出路径）。
                        try { _writer.Flush(); }
                        catch (Exception ex) { Debug.WriteLine("遥测 CSV 尾部 Flush 失败（" + _writer.GetType().Name + "）：" + ex.GetType().Name + ": " + ex.Message); }
                        try { _writer.Dispose(); }
                        catch (Exception ex) { Debug.WriteLine("遥测 CSV Dispose 失败（" + _writer.GetType().Name + "）：" + ex.GetType().Name + ": " + ex.Message); }
                        _writer = null;
                    }
                }
            }
        }
    }
}
