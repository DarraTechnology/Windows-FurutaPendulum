using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 系统辨识结果。数值字段拿不到时一律 <see cref="double.NaN"/>（不编数），由调用方显示 n/a。
    /// </summary>
    public struct IdentificationResult
    {
        /// <summary>样本是否足够。false 时其余数值字段不具语义，Line 为 IDENTIFY insufficient_samples。</summary>
        public bool Sufficient;

        /// <summary>不足的原因（只用于日志，不参与控制）。</summary>
        public string InsufficientReason;

        /// <summary>自由摆动自然频率 [Hz]。</summary>
        public double NaturalFreqHz;

        /// <summary>阻尼比（对数衰减率换算）。</summary>
        public double DampingZeta;

        /// <summary>静止角偏置 [deg]（θ=0 的标定偏差）。</summary>
        public double ThetaOffsetDeg;

        /// <summary>推荐的往复泵频率 [Hz]（= 自然频率）。</summary>
        public double PumpHz;

        /// <summary>推荐的泵幅（等效摆臂角幅值）[deg]；不可辨识 = NaN。</summary>
        public double PumpAmpDeg;

        /// <summary>推荐的泵增益 k_e [rad/s² per J]。</summary>
        public double Ke;

        /// <summary>k_e 的来源说明。</summary>
        public string KeSource;

        /// <summary>可捕获的释放角速度上限 [deg/s]；样本不足 = NaN。</summary>
        public double CatchRateLimitDegPerSec;

        /// <summary>0–1 置信度（样本数 + 拟合残差 + 捕获分布）。</summary>
        public double Confidence;

        public int HalfPeriodSamples;
        public int ExtremaSamples;
        public int CaptureSamples;

        /// <summary>一行 IDENTIFY 文本（字段顺序固定，现场按词检索）。</summary>
        public string Line;
    }

    /// <summary>
    /// 在线系统辨识器。周期线程逐拍喂 θ/ω/φ（自由摆动）与泵能量样本；UI 线程按需取快照做最小二乘 /
    /// 过零统计。写路径无锁、无分配（固定环），读路径用版本双读拷一份再算。
    ///
    /// 估的东西：
    /// 1) 自然频率：θ 过零（带 ±0.015 rad 迟滞 + 线性插值）的半周期，f_n = 1/(2·mean(T_half))；
    /// 2) 阻尼：同侧相邻极值的对数衰减率 δ=ln(A_k/A_{k+1})，ζ=δ/√(4π²+δ²)；
    /// 3) 偏置：相邻异侧极值中点 (A_max+A_min)/2 的均值（静止角）；
    /// 4) 可捕获角速度上限：历史捕获记录里「成功 / 失败」的释放 |ω| 分布；
    /// 5) 泵幅：起摆中实测的能量注入速率 G [J/s per rad/s²]，按「把能量补到 E* 并压住阻尼损耗」反推。
    /// </summary>
    internal sealed class SystemIdentifier
    {
        private const int HalfPeriodCapacity = 96;
        private const int ExtremumCapacity = 64;
        private const int PumpCapacity = 4096;
        private const int CaptureCapacity = 20;

        // 过零迟滞：θ 要越过 ±0.015 rad 才换符号，避免在 0 附近抖成一片。
        private const double CrossingHysteresisRad = 0.015;

        private const double MinHalfPeriodSeconds = 0.05;
        private const double MaxHalfPeriodSeconds = 5.0;

        // 辨识门槛：至少 6 个半周期（3 个整周期）且累计时长 ≥ 2 s。
        private const int MinHalfPeriods = 6;
        private const double MinFreeSwingSeconds = 2.0;

        // 泵幅推导的设计摆动时长 [s]：从最低点补到 E* 的目标时间（工程取值，写进推导来源）。
        private const double PumpRiseTargetSeconds = 6.0;

        private readonly double[] _halfPeriods = new double[HalfPeriodCapacity];
        private int _halfCount;
        private int _halfHead;

        private readonly double[] _extTheta = new double[ExtremumCapacity];
        private int _extCount;
        private int _extHead;

        private readonly long[] _pumpTicks = new long[PumpCapacity];
        private readonly double[] _pumpEnergy = new double[PumpCapacity];
        private readonly double[] _pumpAmplitude = new double[PumpCapacity];
        private int _pumpCount;
        private int _pumpHead;

        private readonly object _captureGate = new object();
        private readonly List<CaptureRecord> _captures = new List<CaptureRecord>(CaptureCapacity);

        private int _version;

        // 自由摆动在线检测状态（只有周期线程读写）
        private bool _sampleValid;
        private double _lastTheta;
        private long _lastTicks;
        private int _crossSign;
        private bool _hasCross;
        private long _lastCrossTicks;
        private int _omegaSign;
        private bool _hasPrevExtremum;
        private double _prevExtremumTheta;

        /// <summary>清空自由摆动 / 泵样本（换机器或重新开始时调用）。捕获历史不清，由 <see cref="ImportCaptures"/> 管。</summary>
        public void ResetSession()
        {
            BeginWrite();
            _halfCount = 0;
            _halfHead = 0;
            _extCount = 0;
            _extHead = 0;
            _pumpCount = 0;
            _pumpHead = 0;
            _sampleValid = false;
            _crossSign = 0;
            _hasCross = false;
            _omegaSign = 0;
            EndWrite();
        }

        /// <summary>
        /// 周期线程逐拍喂一个自由摆动样本。freeSwing=false（起摆 / 平衡控制生效）时只断开差分链，
        /// 不污染过零与极值统计；历史环保留（同一台机器的旧数据仍然有效）。
        /// </summary>
        public void AddSample(long nowTicks, double thetaRad, double omegaRadPerSec, bool freeSwing)
        {
            if (!ControlMath.IsFinite(thetaRad) || !ControlMath.IsFinite(omegaRadPerSec))
            {
                _sampleValid = false;
                _crossSign = 0;
                _omegaSign = 0;
                return;
            }

            BeginWrite();
            try
            {
                if (!freeSwing)
                {
                    _sampleValid = false;
                    _crossSign = 0;
                    _omegaSign = 0;
                    _hasPrevExtremum = false;
                    return;
                }

                if (_sampleValid)
                {
                    double dtSec = (nowTicks - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (dtSec > 0 && dtSec <= 0.05)
                    {
                        DetectCrossing(nowTicks, dtSec, thetaRad);
                        DetectExtremum(nowTicks, thetaRad, omegaRadPerSec);
                    }
                    else
                    {
                        // 数据断档：差分链重来，别把跨越断档的半个周期算进去。
                        _crossSign = 0;
                        _hasCross = false;
                        _omegaSign = 0;
                        _hasPrevExtremum = false;
                    }
                }

                _sampleValid = true;
                _lastTheta = thetaRad;
                _lastTicks = nowTicks;
            }
            finally
            {
                EndWrite();
            }
        }

        /// <summary>周期线程在起摆中喂泵能量样本（泵幅 &gt; 0 才喂）。</summary>
        public void AddPumpSample(long nowTicks, double energyJ, double pumpAmplitudeRadPerSec2)
        {
            if (!ControlMath.IsFinite(energyJ) ||
                !ControlMath.IsFinite(pumpAmplitudeRadPerSec2) ||
                !(pumpAmplitudeRadPerSec2 > 0))
                return;

            BeginWrite();
            try
            {
                int tail = (_pumpHead + _pumpCount) % PumpCapacity;
                _pumpTicks[tail] = nowTicks;
                _pumpEnergy[tail] = energyJ;
                _pumpAmplitude[tail] = pumpAmplitudeRadPerSec2;
                if (_pumpCount == PumpCapacity)
                {
                    _pumpHead = (_pumpHead + 1) % PumpCapacity;
                }
                else
                {
                    _pumpCount++;
                }
            }
            finally
            {
                EndWrite();
            }
        }

        /// <summary>周期线程在捕获尝试结束时登记一条记录（只留最近 20 条）。</summary>
        public void AddCapture(CaptureRecord record)
        {
            lock (_captureGate)
            {
                _captures.Add(record);
                if (_captures.Count > CaptureCapacity)
                    _captures.RemoveRange(0, _captures.Count - CaptureCapacity);
            }
        }

        /// <summary>取最近捕获记录（按时间先后，最新的在最后）。</summary>
        public CaptureRecord[] SnapshotCaptures()
        {
            lock (_captureGate)
            {
                return _captures.ToArray();
            }
        }

        /// <summary>
        /// 导入持久化的捕获记录（启动自动载入时调用）。整体替换内存里的历史，只保留最近 20 条。
        /// 非法记录跳过，但「从未释放就失效」的记录（释放角 NaN + 有失效原因）要保留 ——
        /// 那条记录本身就是「CH1 无有效读数」的证据，不能因为释放点缺失就静默丢掉。
        /// </summary>
        public void ImportCaptures(IEnumerable<CaptureRecord> records)
        {
            lock (_captureGate)
            {
                _captures.Clear();
                if (records == null)
                    return;
                List<CaptureRecord> valid = new List<CaptureRecord>();
                foreach (CaptureRecord record in records)
                {
                    bool hasRelease = ControlMath.IsFinite(record.ReleaseThetaDeg);
                    if (hasRelease && !ControlMath.IsFinite(record.ReleaseOmegaRadPerSec))
                        continue;
                    if (!ControlMath.IsFinite(record.HoldSeconds) ||
                        !ControlMath.IsFinite(record.MaxDeviationDeg))
                        continue;
                    // 既没有释放点、又没有失效原因 = 无法解释的记录，跳过（不是正常产出的东西）。
                    if (!hasRelease && string.IsNullOrEmpty(record.InvalidReason))
                        continue;
                    valid.Add(record);
                }
                valid.Sort(delegate (CaptureRecord a, CaptureRecord b)
                {
                    return a.TimestampUtc.CompareTo(b.TimestampUtc);
                });
                int skip = valid.Count > CaptureCapacity ? valid.Count - CaptureCapacity : 0;
                for (int i = skip; i < valid.Count; i++)
                    _captures.Add(valid[i]);
            }
        }

        /// <summary>
        /// 做一次辨识。调用方（UI / CLI）在任意线程调用；内部拷一份快照再算，不阻塞周期线程。
        /// </summary>
        public IdentificationResult Identify(SafetyLimits limits, SwingUpParams swing)
        {
            IdentificationResult result = new IdentificationResult();
            result.Sufficient = false;
            result.NaturalFreqHz = double.NaN;
            result.DampingZeta = double.NaN;
            result.ThetaOffsetDeg = double.NaN;
            result.PumpHz = double.NaN;
            result.PumpAmpDeg = double.NaN;
            result.Ke = double.NaN;
            result.CatchRateLimitDegPerSec = double.NaN;
            result.Confidence = 0;
            result.KeSource = null;

            Snapshot snapshot = TakeSnapshot();
            result.HalfPeriodSamples = snapshot.HalfCount;
            result.ExtremaSamples = snapshot.ExtCount;
            CaptureRecord[] captures = SnapshotCaptures();
            result.CaptureSamples = captures.Length;

            // ---- 1. 自然频率（过零半周期）----
            double frequencyHz = double.NaN;
            double frequencyConfidence = 0;
            if (snapshot.HalfCount >= MinHalfPeriods)
            {
                double sum = 0;
                for (int i = 0; i < snapshot.HalfCount; i++)
                    sum += snapshot.HalfPeriods[i];
                double mean = sum / snapshot.HalfCount;
                double duration = sum;
                if (mean >= MinHalfPeriodSeconds && mean <= MaxHalfPeriodSeconds &&
                    duration >= MinFreeSwingSeconds)
                {
                    double variance = 0;
                    for (int i = 0; i < snapshot.HalfCount; i++)
                    {
                        double d = snapshot.HalfPeriods[i] - mean;
                        variance += d * d;
                    }
                    variance /= snapshot.HalfCount;
                    double std = Math.Sqrt(variance);
                    double candidate = 1.0 / (2.0 * mean);
                    if (candidate >= 0.05 && candidate <= 20.0)
                    {
                        frequencyHz = candidate;
                        double stability = 1.0 - (mean > 1e-9 ? std / mean * 4.0 : 1.0);
                        if (stability < 0) stability = 0;
                        double countFactor = snapshot.HalfCount >= 12 ? 1.0 : snapshot.HalfCount / 12.0;
                        frequencyConfidence = stability * countFactor;
                    }
                }
            }

            if (double.IsNaN(frequencyHz))
            {
                result.InsufficientReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "free_swing_half_periods={0}/{1} duration={2:F2}s；" +
                    "样本不足：让摆杆从较大角度自由摆动 ≥2 s（至少 3 个整周期 / 6 个半周期）后再识别。",
                    snapshot.HalfCount, MinHalfPeriods, snapshot.HalfPeriodSum);
                result.Line = "IDENTIFY insufficient_samples reason=" + result.InsufficientReason;
                return result;
            }

            result.Sufficient = true;
            result.NaturalFreqHz = frequencyHz;

            // ---- 2. 阻尼比 + 3. 静止角偏置（极值环）----
            double zeta = double.NaN;
            double zetaConfidence = 0;
            double offsetDeg = double.NaN;
            double offsetConfidence = 0;
            ComputeExtrema(snapshot, out zeta, out zetaConfidence, out offsetDeg, out offsetConfidence);
            result.DampingZeta = zeta;
            result.ThetaOffsetDeg = offsetDeg;

            // ---- 4. 可捕获角速度上限（历史捕获记录）----
            double catchLimitDegPerSec;
            double captureConfidence;
            ComputeCatchLimit(captures, out catchLimitDegPerSec, out captureConfidence);
            result.CatchRateLimitDegPerSec = catchLimitDegPerSec;

            // ---- 5. 泵参数与 k_e 初值 ----
            double targetEnergyJ = double.NaN;
            if (ControlMath.IsFinite(limits.Mgl) && limits.Mgl > 0 &&
                ControlMath.IsFinite(swing.TargetEnergyGain) && swing.TargetEnergyGain >= 0)
            {
                targetEnergyJ = 2.0 * limits.Mgl * (1.0 + swing.TargetEnergyGain);
            }

            double keSeed = double.NaN;
            if (ControlMath.IsFinite(targetEnergyJ) && targetEnergyJ > 1e-9 &&
                ControlMath.IsFinite(swing.AMax) && swing.AMax > 0)
            {
                keSeed = 0.5 * swing.AMax / targetEnergyJ;
            }
            result.Ke = keSeed;
            result.KeSource = "0.5·AMax/E*（Mgl 来自配置，不是辨识值）";

            double pumpGain = ComputePumpGain(snapshot);
            double pumpConfidence = 0;
            double pumpAmpDeg = double.NaN;
            double pumpHz = frequencyHz;
            if (ControlMath.IsFinite(pumpGain) && pumpGain > 1e-9 &&
                ControlMath.IsFinite(targetEnergyJ) && targetEnergyJ > 1e-9)
            {
                double omegaN = 2.0 * Math.PI * frequencyHz;
                double zetaForLoss = double.IsNaN(zeta) ? 0.0 : Math.Max(zeta, 0.0);
                // 能量平衡：E* 在 PumpRiseTargetSeconds 内补齐 + 目标能量处的阻尼损耗功率。
                double requiredPower = targetEnergyJ / PumpRiseTargetSeconds +
                                       2.0 * zetaForLoss * omegaN * targetEnergyJ;
                double amplitude = requiredPower / pumpGain;
                if (ControlMath.IsFinite(amplitude) && amplitude > 0)
                {
                    double cap = swing.AMax > 0 ? swing.AMax : amplitude;
                    if (amplitude > cap) amplitude = cap;
                    double omegaPump = 2.0 * Math.PI * pumpHz;
                    if (omegaPump > 1e-6)
                    {
                        double ampRad = amplitude / (omegaPump * omegaPump);
                        pumpAmpDeg = ampRad * (180.0 / Math.PI);
                    }
                }
                pumpConfidence = snapshot.PumpCount >= 2000 ? 1.0 : snapshot.PumpCount / 2000.0;
            }

            result.PumpHz = pumpHz;
            result.PumpAmpDeg = pumpAmpDeg;

            double confidence = 0.40 * frequencyConfidence +
                                0.20 * zetaConfidence +
                                0.10 * offsetConfidence +
                                0.15 * captureConfidence +
                                0.15 * pumpConfidence;
            if (confidence < 0) confidence = 0;
            if (confidence > 1) confidence = 1;
            result.Confidence = confidence;

            result.Line = BuildLine(result, snapshot, captures);
            return result;
        }

        // ==================== 在线检测 ====================

        private void DetectCrossing(long nowTicks, double dtSec, double thetaRad)
        {
            int sign = _crossSign;
            if (thetaRad > CrossingHysteresisRad) sign = 1;
            else if (thetaRad < -CrossingHysteresisRad) sign = -1;

            if (_crossSign != 0 && sign != 0 && sign != _crossSign)
            {
                double denominator = Math.Abs(_lastTheta) + Math.Abs(thetaRad);
                double fraction = denominator > 1e-12 ? Math.Abs(_lastTheta) / denominator : 0.5;
                long crossTicks = _lastTicks +
                    (long)((nowTicks - _lastTicks) * fraction);
                if (_hasCross)
                {
                    double halfPeriod = (crossTicks - _lastCrossTicks) /
                                        (double)System.Diagnostics.Stopwatch.Frequency;
                    if (halfPeriod >= MinHalfPeriodSeconds && halfPeriod <= MaxHalfPeriodSeconds)
                        PushHalfPeriod(halfPeriod);
                }
                _lastCrossTicks = crossTicks;
                _hasCross = true;
            }

            if (sign != 0)
                _crossSign = sign;
        }

        private void DetectExtremum(long nowTicks, double thetaRad, double omegaRadPerSec)
        {
            int sign = omegaRadPerSec > 0 ? 1 : (omegaRadPerSec < 0 ? -1 : 0);
            if (sign == 0)
                return;

            if (_omegaSign != 0 && sign != _omegaSign)
            {
                // 上一拍的 ω 符号是极值的方向：+ → 极大，− → 极小。
                PushExtremum(_lastTheta);
            }
            _omegaSign = sign;
        }

        private void PushHalfPeriod(double value)
        {
            int tail = (_halfHead + _halfCount) % HalfPeriodCapacity;
            _halfPeriods[tail] = value;
            if (_halfCount == HalfPeriodCapacity)
                _halfHead = (_halfHead + 1) % HalfPeriodCapacity;
            else
                _halfCount++;
        }

        private void PushExtremum(double thetaRad)
        {
            if (!ControlMath.IsFinite(thetaRad) || Math.Abs(thetaRad) < 0.02)
                return;
            // 相邻极值（异侧）摆幅太小 = 静止噪声，不是一次摆动：丢掉，免得污染偏置与阻尼。
            if (_hasPrevExtremum && Math.Abs(thetaRad - _prevExtremumTheta) < 0.03)
                return;
            _hasPrevExtremum = true;
            _prevExtremumTheta = thetaRad;
            int tail = (_extHead + _extCount) % ExtremumCapacity;
            _extTheta[tail] = thetaRad;
            if (_extCount == ExtremumCapacity)
                _extHead = (_extHead + 1) % ExtremumCapacity;
            else
                _extCount++;
        }

        // ==================== 快照与计算 ====================

        private struct Snapshot
        {
            public int HalfCount;
            public double[] HalfPeriods;
            public double HalfPeriodSum;
            public int ExtCount;
            public double[] ExtTheta;
            public int PumpCount;
            public long[] PumpTicks;
            public double[] PumpEnergy;
            public double[] PumpAmplitude;
        }

        private Snapshot TakeSnapshot()
        {
            Snapshot snapshot = new Snapshot();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int version = Volatile.Read(ref _version);
                if ((version & 1) != 0)
                    continue;

                snapshot.HalfCount = _halfCount;
                snapshot.HalfPeriods = new double[snapshot.HalfCount];
                double sum = 0;
                for (int i = 0; i < snapshot.HalfCount; i++)
                {
                    double value = _halfPeriods[(_halfHead + i) % HalfPeriodCapacity];
                    snapshot.HalfPeriods[i] = value;
                    sum += value;
                }
                snapshot.HalfPeriodSum = sum;

                snapshot.ExtCount = _extCount;
                snapshot.ExtTheta = new double[snapshot.ExtCount];
                for (int i = 0; i < snapshot.ExtCount; i++)
                {
                    int index = (_extHead + i) % ExtremumCapacity;
                    snapshot.ExtTheta[i] = _extTheta[index];
                }

                snapshot.PumpCount = _pumpCount;
                snapshot.PumpTicks = new long[snapshot.PumpCount];
                snapshot.PumpEnergy = new double[snapshot.PumpCount];
                snapshot.PumpAmplitude = new double[snapshot.PumpCount];
                for (int i = 0; i < snapshot.PumpCount; i++)
                {
                    int index = (_pumpHead + i) % PumpCapacity;
                    snapshot.PumpTicks[i] = _pumpTicks[index];
                    snapshot.PumpEnergy[i] = _pumpEnergy[index];
                    snapshot.PumpAmplitude[i] = _pumpAmplitude[index];
                }

                Thread.MemoryBarrier();
                if (version == Volatile.Read(ref _version))
                    return snapshot;
            }
            return snapshot;
        }

        /// <summary>极值 → 阻尼比与静止角偏置。</summary>
        private static void ComputeExtrema(
            Snapshot snapshot,
            out double zeta,
            out double zetaConfidence,
            out double offsetDeg,
            out double offsetConfidence)
        {
            zeta = double.NaN;
            zetaConfidence = 0;
            offsetDeg = double.NaN;
            offsetConfidence = 0;

            List<double> logDecrements = new List<double>();
            List<double> midpoints = new List<double>();
            for (int i = 1; i < snapshot.ExtCount; i++)
            {
                double previous = snapshot.ExtTheta[i - 1];
                double current = snapshot.ExtTheta[i];
                if (Math.Sign(previous) != Math.Sign(current))
                {
                    if (Math.Abs(previous - current) > 0.10)
                        midpoints.Add((previous + current) * 0.5);
                }
            }
            // 同侧相邻极值隔一个极值：用 i-2 与 i 比（max→max / min→min 才对应一整周期）。
            for (int i = 2; i < snapshot.ExtCount; i++)
            {
                double previous = snapshot.ExtTheta[i - 2];
                double current = snapshot.ExtTheta[i];
                if (Math.Sign(previous) != Math.Sign(current))
                    continue;
                double a1 = Math.Abs(previous);
                double a2 = Math.Abs(current);
                if (a1 > a2 && a2 > 0.03 && a1 > 0.03)
                {
                    double delta = Math.Log(a1 / a2);
                    if (delta >= 0 && delta <= 1.5)
                        logDecrements.Add(delta);
                }
            }

            if (logDecrements.Count >= 4)
            {
                double mean, std;
                MeanStd(logDecrements, out mean, out std);
                zeta = mean / Math.Sqrt(4.0 * Math.PI * Math.PI + mean * mean);
                double stability = 1.0 - std / (Math.Abs(mean) + 0.02);
                if (stability < 0) stability = 0;
                if (stability > 1) stability = 1;
                zetaConfidence = stability * (logDecrements.Count >= 8 ? 1.0 : logDecrements.Count / 8.0);
            }

            if (midpoints.Count >= 2)
            {
                double mean, std;
                MeanStd(midpoints, out mean, out std);
                offsetDeg = mean * (180.0 / Math.PI);
                offsetConfidence = midpoints.Count >= 4 ? 1.0 : midpoints.Count / 4.0;
            }
        }

        /// <summary>捕获记录 → 可捕获释放角速度上限 [deg/s]。</summary>
        private static void ComputeCatchLimit(
            CaptureRecord[] captures,
            out double limitDegPerSec,
            out double confidence)
        {
            limitDegPerSec = double.NaN;
            confidence = 0;
            if (captures == null || captures.Length == 0)
                return;

            List<double> caught = new List<double>();
            List<double> failed = new List<double>();
            for (int i = 0; i < captures.Length; i++)
            {
                double omega = Math.Abs(captures[i].ReleaseOmegaRadPerSec);
                if (!ControlMath.IsFinite(omega))
                    continue;
                if (captures[i].Caught) caught.Add(omega);
                else failed.Add(omega);
            }

            int total = caught.Count + failed.Count;
            if (total == 0)
                return;

            // 置信度先按样本数给，再看分离度。
            confidence = total >= 5 ? 0.7 : (total >= 3 ? 0.45 : 0.2);
            if (caught.Count == 0)
            {
                // 一次都没接住：不编上限，只报样本。
                confidence *= 0.3;
                return;
            }

            double maxCaught = 0;
            for (int i = 0; i < caught.Count; i++)
                if (caught[i] > maxCaught) maxCaught = caught[i];

            double minFailedAbove = double.MaxValue;
            for (int i = 0; i < failed.Count; i++)
                if (failed[i] > maxCaught && failed[i] < minFailedAbove) minFailedAbove = failed[i];

            double limitRad;
            if (minFailedAbove < double.MaxValue)
            {
                // 成功 / 失败在 |ω| 上分得开：取分界中点。
                limitRad = 0.5 * (maxCaught + minFailedAbove);
                confidence += 0.2;
            }
            else
            {
                // 没有更高的失败样本：只给「已验证上限 + 15% 裕量」，并如实降置信度。
                limitRad = maxCaught * 1.15;
                confidence *= 0.8;
            }

            if (confidence > 1) confidence = 1;
            limitDegPerSec = limitRad * (180.0 / Math.PI);
        }

        /// <summary>
        /// 起摆泵的能量注入速率 G [J/s per rad/s²]：5 拍平滑后的正功率除以当拍泵幅，取 90 分位。
        /// 没有泵样本（泵幅一直 0）返回 NaN —— 不拿默认值冒充辨识值。
        /// </summary>
        private static double ComputePumpGain(Snapshot snapshot)
        {
            if (snapshot.PumpCount < 200)
                return double.NaN;

            const int smooth = 5;
            List<double> gains = new List<double>(snapshot.PumpCount);
            for (int i = smooth; i < snapshot.PumpCount; i++)
            {
                double dt = (snapshot.PumpTicks[i] - snapshot.PumpTicks[i - smooth]) /
                            (double)System.Diagnostics.Stopwatch.Frequency;
                if (!(dt > 1e-5))
                    continue;
                double rate = (snapshot.PumpEnergy[i] - snapshot.PumpEnergy[i - smooth]) / dt;
                double amplitude = snapshot.PumpAmplitude[i - smooth];
                if (rate > 0 && amplitude > 1e-9)
                    gains.Add(rate / amplitude);
            }

            if (gains.Count < 100)
                return double.NaN;

            gains.Sort();
            int index = (int)Math.Round(0.90 * (gains.Count - 1));
            double gain = gains[index];
            return ControlMath.IsFinite(gain) && gain > 0 ? gain : double.NaN;
        }

        private static void MeanStd(List<double> values, out double mean, out double std)
        {
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            mean = sum / values.Count;
            double variance = 0;
            for (int i = 0; i < values.Count; i++)
            {
                double d = values[i] - mean;
                variance += d * d;
            }
            std = Math.Sqrt(variance / values.Count);
        }

        private static string BuildLine(
            IdentificationResult result,
            Snapshot snapshot,
            CaptureRecord[] captures)
        {
            StringBuilder builder = new StringBuilder(160);
            builder.Append("IDENTIFY");
            builder.Append(" f_n=").Append(FormatWithUnit(result.NaturalFreqHz, "F3", "Hz"));
            builder.Append(" zeta=").Append(Format(result.DampingZeta, "F4"));
            builder.Append(" offset=").Append(FormatWithUnit(result.ThetaOffsetDeg, "F2", "deg"));
            builder.Append(" pumpHz=").Append(Format(result.PumpHz, "F3"));
            builder.Append(" pumpAmp=").Append(FormatWithUnit(result.PumpAmpDeg, "F2", "deg"));
            builder.Append(" ke=").Append(ControlMath.Gain(result.Ke));
            builder.Append(" samples=").Append(snapshot.HalfCount.ToString(CultureInfo.InvariantCulture))
                   .Append('/').Append(snapshot.ExtCount.ToString(CultureInfo.InvariantCulture))
                   .Append('/').Append((captures == null ? 0 : captures.Length).ToString(CultureInfo.InvariantCulture));
            builder.Append(" conf=").Append(result.Confidence.ToString("F2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static string Format(double value, string format)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "n/a";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>带单位输出：不可辨识写 n/a（不写 n/aHz 这种拼接）。</summary>
        private static string FormatWithUnit(double value, string format, string unit)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "n/a";
            return value.ToString(format, CultureInfo.InvariantCulture) + unit;
        }

        private void BeginWrite()
        {
            Interlocked.Increment(ref _version);
        }

        private void EndWrite()
        {
            Interlocked.Increment(ref _version);
        }
    }
}
