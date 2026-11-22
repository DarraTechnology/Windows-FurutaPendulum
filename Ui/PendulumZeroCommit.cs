using System;
using FurutaPendulum.Control;

namespace FurutaPendulum.Ui
{
    /// <summary>
    /// 摆杆 0° 点提交（唯一实现）：把新零点交给控制层，并同步文件视图里的 360° 标定点。
    /// <para>
    /// 规则与 CLI <c>zero pendulum</c> / <c>normalize</c> / <c>recenter</c> 落盘时同一套
    /// （<c>CliSession.Commands.AutoSaveCalibration</c>）：
    /// </para>
    /// <list type="number">
    /// <item>控制层拒绝（闭环 / 捕获中）或回读不是请求值 → 文件视图一个字段都不写，原样返回原因。</item>
    /// <item>0° 与文件视图里的旧 0° 相同 → 0° / 360° 原样保留。重复清同一个零点不再误报
    /// 「360° 已平移」，也不产生无意义的「未保存」。</item>
    /// <item>0° 变了 → 调 <see cref="CalibrationParameters.RebasePendulum360AfterZero"/>：
    /// 平移算术（<c>new360 = newZero + (old360 − oldZero)</c>）与 span=0 / 无旧零点 / 越 int32
    /// 的作废规则只在控制层这一处，UI 不复制第二套。</item>
    /// </list>
    /// <para>
    /// 只改控制层与传入的 calibration 内存视图，不写文件、不动电机；UI 回填与落盘由调用方负责。
    /// </para>
    /// </summary>
    internal static class PendulumZeroCommit
    {
        /// <summary>一次提交的结果；<see cref="Accepted"/> = false 时除 <see cref="RejectReason"/> 外均无意义。</summary>
        internal sealed class Result
        {
            /// <summary>控制层接受并回读等于请求值。</summary>
            internal bool Accepted;

            /// <summary>控制层实际生效的 0° 原始计数（= 请求值）。</summary>
            internal int AppliedZero;

            /// <summary>控制层拒绝原因（Accepted = false）。</summary>
            internal string RejectReason;

            /// <summary>拒绝原因属于「控制层仍是旧值」而不是引擎拒绝（调用方文案不同）。</summary>
            internal bool AppliedMismatch;

            /// <summary>0° 与文件视图旧值相同：360° 未动。</summary>
            internal bool ZeroUnchanged;

            /// <summary>360° 点按同一圈量平移成功（且两点仍成对可用）。</summary>
            internal bool Rebased;

            /// <summary>360° 平移 / 作废的可读回执；没有 360° 点时可能为 null。</summary>
            internal string Note;

            /// <summary>提交后文件视图是否仍有成对可用的 360° 点。</summary>
            internal bool HasPendulum360;

            /// <summary>提交后文件视图里的 360° 计数。</summary>
            internal int Pendulum360Count;

            /// <summary>同步 360° 时异常（已保留原值），null = 正常。</summary>
            internal string SyncError;
        }

        /// <summary>
        /// 提交新 0° 点并同步 360° 点。调用方必须先确保 <paramref name="calibration"/> 非空。
        /// </summary>
        internal static Result Commit(PendulumEngine engine, CalibrationParameters calibration, int newZero)
        {
            if (engine == null)
                throw new ArgumentNullException("engine");
            if (calibration == null)
                throw new ArgumentNullException("calibration");

            Result result = new Result();
            string reason;
            if (!engine.TrySetPendulumZeroCount(newZero, out reason))
            {
                result.RejectReason = reason;
                return result;
            }
            int applied = engine.PendulumZeroRawCount;
            if (applied != newZero)
            {
                result.AppliedMismatch = true;
                result.RejectReason = "控制层仍是 raw=" + applied + "（请求 " + newZero + "），先「停止」再设";
                return result;
            }

            result.Accepted = true;
            result.AppliedZero = applied;
            result.HasPendulum360 = calibration.HasPendulum360;
            result.Pendulum360Count = calibration.Pendulum360Count;

            // 与 CLI 同一条短路：零点没变时 0°/360° 原样保留，不进 Rebase（否则会把同一对点
            // 再“平移”一次并给出误导回执；span=0 的旧点对被误当成本次操作作废）。
            if (calibration.HasPendulumZero && calibration.PendulumZeroCount == applied)
            {
                result.ZeroUnchanged = true;
                return result;
            }

            try
            {
                result.Rebased = calibration.RebasePendulum360AfterZero(applied, out string note);
                result.Note = note;
            }
            catch (Exception ex)
            {
                result.SyncError = ex.GetType().Name + ": " + ex.Message;
                result.Note = "360° 点重标异常，已保留原值，请人工核对 0° / 360° 两点。";
            }
            result.HasPendulum360 = calibration.HasPendulum360;
            result.Pendulum360Count = calibration.Pendulum360Count;
            return result;
        }
    }
}
