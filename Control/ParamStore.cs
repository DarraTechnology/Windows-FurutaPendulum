using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FurutaPendulum.Control
{
    /// <summary>
    /// 调参文件的存取。UI 与 CLI 共用这一套，禁止两边各写一份序列化。
    /// net472 不引任何新 NuGet，走框架自带的 DataContractJsonSerializer。
    ///
    /// 落点（<see cref="DefaultPath"/>）：默认 **exe 同级 furuta_params.json**。
    /// 只有 exe 目录确实不可写（例如装在 Program Files 且没给写权限）才回退到
    /// %AppData%\DarraEtherCAT\FurutaPendulum\furuta_params.json，并通过 <see cref="EffectivePathNote"/>
    /// 明确说明「实际路径是哪个、为什么回退」——绝不静默换位置。旧位置的同名文件只提示、不删。
    ///
    /// 读回对 BOM 容错：UTF-8 BOM（含被脚本写重了的多个 BOM）、UTF-16 LE/BE BOM、前导空白都能读；
    /// 这些「打开就报意外字符 'ï'」的情况实测出现过（文件被别的工具带 BOM 另存）。
    /// 读回一律走各参数类的 IsValid 校验，必需段（pid / swingup / limits）任一不合法就整体拒载；
    /// 可选段（pump / identify / soft_limits / calibration / captures）逐段校验，坏了就用默认值并写 note，
    /// 不让一段辨识历史拖垮整份调参文件。绝不半套生效。
    /// </summary>
    public static class ParamStore
    {
        private const string FileFormat = "furuta_params.v1";

        /// <summary>参数文件名（exe 同级与回退目录同名，便于手工搬迁）。</summary>
        public const string FileName = "furuta_params.json";

        /// <summary>
        /// 软中心带半径的本版出厂默认值 [rad]。现场左右限位行程远大于旧值 3.6 rad，
        /// 3.6 会把摆臂的往复行程提前判成「漂出中心带」并反复掐断能量注入。
        /// 参数文件里的缺键和显式 0 无法区分（DataContract 缺键 = 0），两者都按本值生效。
        /// <para>
        /// 权威出厂默认仍在控制层 <c>SwingUpParams.CreateDefault()</c>；本常量只负责参数文件载入补齐，
        /// 必须与它保持一致（当前两者同为 20）。用户显式写进文件的其它值（包括 3.6）原样保留，不钳位。
        /// 旧版 3.6 出厂块的迁移只由 <c>PendulumEngine.IsLegacyFactorySwingDefaults</c> 的整块指纹负责
        /// （见 <see cref="TryLoad(string, string, out PidGains, out SwingUpParams, out SafetyLimits,
        /// out PersistedExtras, out string, out string, out string)"/> 里「旧版出厂起摆块」分支），
        /// 不在本常量上另建一套识别逻辑。
        /// </para>
        /// </summary>
        public const double FactoryCenterBandRad = 20.0;

        /// <summary>
        /// 抽取增益的本版出厂默认值 [rad/s² per J]。0 = 未配置（旧文件缺键）时按它补齐；
        /// 与 <c>SwingUpParams.CreateDefault()</c> 的 ExtractGain 默认值保持一致。
        /// </summary>
        public const double FactoryExtractGain = 60.0;

        private const string FallbackFolder = "DarraEtherCAT";

        private static readonly object PathGate = new object();
        private static string _effectivePath;
        private static string _effectiveNote;

        /// <summary>
        /// JSON 里的单位约定：角度 rad、角速度 rad/s、角加速度 rad/s²、时间 ms。
        /// **摆臂软限位是唯一例外，只存当前 raw 坐标系下的 counts**（不存 deg / rad 行程口径）；可选段另有 deg 字段
        /// （泵幅等效角、辨识偏置），键名自带单位后缀，不会混。
        /// </summary>
        private const string UnitConvention = "角度=rad，角速度=rad/s，角加速度=rad/s²，时间=ms；摆臂软限位/位置点=当前 raw 坐标系下的 0x6064 counts（可选段键名自带 deg / counts 单位后缀；掉电后 raw 起点变化时必须规整化或重采）";

        /// <summary>exe 同级参数文件的完整路径（不论该目录是否可写）。</summary>
        public static string ExeDirectoryPath
        {
            get { return Path.Combine(ResolveExeDirectory(), FileName); }
        }

        /// <summary>
        /// exe 所在目录：优先用入口程序集的位置（真正「exe 旁边」），拿不到才退回 AppDomain.BaseDirectory。
        /// GUI / CLI 两种模式下都等于 FurutaPendulum.exe 所在目录。
        /// </summary>
        private static string ResolveExeDirectory()
        {
            try
            {
                System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
                if (entry != null)
                {
                    string location = entry.Location;
                    if (!string.IsNullOrEmpty(location))
                    {
                        string dir = Path.GetDirectoryName(location);
                        if (!string.IsNullOrEmpty(dir)) return dir;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("解析 exe 目录失败，退回 BaseDirectory：" + DiagText.Describe(ex));
            }
            return AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
        }

        /// <summary>回退路径：%AppData%\DarraEtherCAT\FurutaPendulum\furuta_params.json。</summary>
        public static string FallbackPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData))
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(appData ?? string.Empty, FallbackFolder, "FurutaPendulum", FileName);
            }
        }

        /// <summary>
        /// 实际生效的参数文件路径：默认 exe 同级；exe 目录不可写时回退到 <see cref="FallbackPath"/>。
        /// 结果缓存（写探针只在第一次解析时做一次）。
        /// </summary>
        public static string DefaultPath
        {
            get { return ResolveEffectivePath(); }
        }

        /// <summary>
        /// 关于实际路径的说明（回退原因 / 发现旧路径文件）；一切正常时 = null。
        /// UI 状态行、CLI 回执与日志用它明确告诉用户「文件到底在哪」，不静默回退。
        /// </summary>
        public static string EffectivePathNote
        {
            get
            {
                ResolveEffectivePath();
                return _effectiveNote;
            }
        }

        /// <summary>旧位置候选（只用于提示「可从这里导入」，不自动读、不删除）。</summary>
        public static string[] LegacyPaths()
        {
            List<string> list = new List<string>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                list.Add(Path.Combine(appData, "DarraEtherCAT", "FurutaPendulum", FileName));
                list.Add(Path.Combine(appData, FileName));
            }
            if (!string.IsNullOrEmpty(local) &&
                !string.Equals(local, appData, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(Path.Combine(local, "DarraEtherCAT", "FurutaPendulum", FileName));
            }
            return list.ToArray();
        }

        // ---- 默认参数文件的载入结局（只用于「让人看见」，不参与任何控制逻辑）----

        private static readonly object LoadReportGate = new object();
        private static ParamFileLoadReport _lastDefaultFileLoad;

        /// <summary>
        /// 最近一次读「默认参数文件」（<see cref="DefaultPath"/>）的结局；null = 本进程还没读过。
        /// CLI 的 status「参数文件」行与启动日志靠它把「整份拒载、已回落默认」说清楚 ——
        /// 只显示一个路径会让人以为文件里的参数已经生效（存量 theta_switch_rad=0.28 就是实例）。
        /// </summary>
        public static ParamFileLoadReport LastDefaultFileLoad
        {
            get { lock (LoadReportGate) { return _lastDefaultFileLoad; } }
        }

        /// <summary>
        /// 记一笔参数载入结局。只有 path 指向默认参数文件时才记：其它路径（--config 指定的文件）
        /// 本来就不该冒充「status 行显示的那份文件」的状态。失败时 error 必须是完整中文原因。
        /// </summary>
        public static void ReportDefaultFileLoad(string path, bool ok, string error, string note)
        {
            string target = string.IsNullOrEmpty(path) ? DefaultPath : path;
            if (!IsDefaultFile(target))
                return;

            ParamFileLoadOutcome outcome;
            if (ok)
                outcome = ParamFileLoadOutcome.Loaded;
            else
                outcome = File.Exists(target) ? ParamFileLoadOutcome.Rejected : ParamFileLoadOutcome.Missing;

            ParamFileLoadReport report = new ParamFileLoadReport(
                target, outcome,
                ok ? null : (string.IsNullOrEmpty(error) ? "加载失败但调用方未返回原因：检查参数文件内容与路径权限，详见会话日志" : error),
                ok ? note : null,
                DateTime.UtcNow);
            lock (LoadReportGate) { _lastDefaultFileLoad = report; }
        }

        /// <summary>path 是否就是默认参数文件（全路径、忽略大小写比对；比对失败一律当不是）。</summary>
        private static bool IsDefaultFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(path), Path.GetFullPath(DefaultPath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("参数文件路径比对失败：" + DiagText.Describe(ex));
                return false;
            }
        }

        /// <summary>一次参数文件载入的结局分类（供 status 行 / 启动日志显示，不参与控制逻辑）。</summary>
        public enum ParamFileLoadOutcome
        {
            /// <summary>读出来了；可选段可能有回退（见 <see cref="ParamFileLoadReport.Note"/>）。</summary>
            Loaded,
            /// <summary>文件存在但整份被拒（格式 / 必需段 / 越界），控制层按默认值跑。</summary>
            Rejected,
            /// <summary>文件不存在（首次运行 / 路径变了）。</summary>
            Missing
        }

        /// <summary>默认参数文件的一次载入结局快照，构造后只读。</summary>
        public sealed class ParamFileLoadReport
        {
            public ParamFileLoadReport(string path, ParamFileLoadOutcome outcome, string error, string note,
                DateTime attemptedAtUtc)
            {
                Path = path;
                Outcome = outcome;
                Error = error;
                Note = note;
                AttemptedAtUtc = attemptedAtUtc;
            }

            /// <summary>读的是哪个文件（默认参数文件的完整路径）。</summary>
            public string Path { get; private set; }
            /// <summary>结局：已载入 / 整份拒载 / 文件不存在。</summary>
            public ParamFileLoadOutcome Outcome { get; private set; }
            /// <summary>整份拒载或读失败的中文原因（可能很长）；Loaded 时为 null。</summary>
            public string Error { get; private set; }
            /// <summary>可选段回退 / DENI 不一致等提示；没有则 null。</summary>
            public string Note { get; private set; }
            /// <summary>这次尝试的 UTC 时间。</summary>
            public DateTime AttemptedAtUtc { get; private set; }
        }

        /// <summary>解析并缓存实际路径（含回退说明与旧路径提示）。</summary>
        private static string ResolveEffectivePath()
        {
            lock (PathGate)
            {
                if (!string.IsNullOrEmpty(_effectivePath))
                    return _effectivePath;

                string exePath = ExeDirectoryPath;
                string note = null;
                string chosen;
                string exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir) && IsDirectoryWritable(exeDir))
                {
                    chosen = exePath;
                }
                else
                {
                    chosen = FallbackPath;
                    note = string.Format(
                        "exe 目录不可写（{0}），参数文件改存 {1}（实际路径就是这条，写盘核验后再显示「已保存」）。",
                        exePath, chosen);
                }

                string legacy = FindLegacyFile(chosen);
                if (!string.IsNullOrEmpty(legacy))
                {
                    string legacyNote = string.Format(
                        "发现旧路径参数文件 {0}：可点「载入参数」从旧路径导入（不会自动删除旧文件）。", legacy);
                    note = string.IsNullOrEmpty(note) ? legacyNote : note + " " + legacyNote;
                }

                _effectiveNote = note;
                _effectivePath = chosen;
                return chosen;
            }
        }

        /// <summary>找第一个存在的旧路径文件（跳过当前生效路径）。</summary>
        private static string FindLegacyFile(string currentPath)
        {
            string[] candidates;
            try
            {
                candidates = LegacyPaths();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("枚举旧参数路径失败：" + DiagText.Describe(ex));
                return null;
            }
            for (int i = 0; i < candidates.Length; i++)
            {
                if (string.Equals(candidates[i], currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (File.Exists(candidates[i])) return candidates[i];
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("检查旧参数文件失败：" + DiagText.Describe(ex));
                }
            }
            return null;
        }

        /// <summary>
        /// 目录可写探针：在目标目录建一个临时文件再删掉。任何异常都算不可写（不猜）。
        /// 只在首次解析路径时做一次。
        /// </summary>
        public static bool IsDirectoryWritable(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return false;
            string probe = Path.Combine(directory, ".furuta_write_probe_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0x20);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("参数目录写探针失败：" + directory + " " + DiagText.Describe(ex));
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe)) File.Delete(probe);
                }
                catch (Exception cleanup)
                {
                    System.Diagnostics.Debug.WriteLine("参数写探针清理失败：" + DiagText.Describe(cleanup));
                }
            }
        }

        /// <summary>
        /// 存三组参数到 path（兼容旧调用）。可选段不会丢：extras 传 null 时保留文件里已有的
        /// pump / identify / soft_limits / calibration / captures，CLI 的 save-config 不会抹掉辨识与捕获历史。
        /// path 传 null / 空 时用 DefaultPath。
        /// deniPath 是保存当时生效的 DENI 路径，只作整定溯源记录写进文件，不参与任何配置。
        /// </summary>
        public static bool Save(string path, PidGains pid, SwingUpParams swing, SafetyLimits limits, string deniPath, out string error)
        {
            return Save(path, pid, swing, limits, deniPath, null, out error);
        }

        /// <summary>
        /// 存三组必需参数 + 可选段（泵 / 辨识 / 软限位 / 标定 / 捕获记录）。
        /// 任一段不合法就整体不写，reason 原样给出。extras 传 null = 保留文件里已有的可选段。
        /// </summary>
        public static bool Save(string path, PidGains pid, SwingUpParams swing, SafetyLimits limits,
            string deniPath, PersistedExtras extras, out string error)
        {
            error = null;
            string reason;
            // 落盘前先按同一份规则补全/校核加速度律增益：调用方传进来的可能是旧结构（新字段全 0），
            // 直接把 0 写进文件，下次加载又得当「未配置」再补一遍。这里写回的始终是运行期生效值。
            // pid 是值参数，这里的修改只影响本函数要写入的那份文档，不改调用方的变量。
            pid.ResolveAccelerationLaw();
            if (!pid.IsValid(out reason))
            {
                error = "平衡增益不合法，未保存：" + reason;
                return false;
            }
            // 旧版出厂起摆块不许写盘：它不是整定结果，写回去只会让下一次载入继续踩同一个坑
            // （现场参数文件就是这样一代代保存下来的）。判据与载入 / 起摆前拒绝同源。
            string legacySwingDetail;
            if (PendulumEngine.IsLegacyFactorySwingDefaults(swing, out legacySwingDetail))
            {
                error = "起摆参数仍是旧版出厂块，未保存：" + legacySwingDetail;
                return false;
            }
            if (!swing.IsValid(out reason))
            {
                error = "起摆参数不合法，未保存：" + reason;
                return false;
            }
            // 快速提交带必须是硬捕获门的子集：保存入口按拒收口径，不把越界值写进文件
            // （控制层运行期还会收紧一次，那是第二层保护，不是允许写越界值的理由）。
            if (!TryValidateCaptureHandoffGates(swing, out reason))
            {
                error = "起摆参数不合法，未保存：" + reason;
                return false;
            }
            if (!limits.IsValid(out reason))
            {
                error = "安全阈值不合法，未保存：" + reason;
                return false;
            }
            if (extras != null && !extras.IsValid(out reason))
            {
                error = "可选参数段不合法，未保存：" + reason;
                return false;
            }

            string target = string.IsNullOrEmpty(path) ? DefaultPath : path;
            // 可选段合并：新传进来的段覆盖旧文件，缺的段保留旧文件里的（不因一次只改 PID 就抹掉辨识/捕获历史）。
            PersistedExtras existing = null;
            if (File.Exists(target))
            {
                string existingNote;
                if (!TryReadExtrasOnly(target, out existing, out existingNote))
                {
                    // 旧文件读不出可选段：不影响主参数保存，但绝不把「读不出」当成「原来是空的」。
                    // 这里不阻断保存（主参数仍要落盘），只是本次无法保留旧的可选段。
                    System.Diagnostics.Debug.WriteLine("保存时读取旧可选段失败：" + existingNote);
                }
            }
            PersistedExtras extrasToWrite = MergeExtras(existing, extras);
            // soft_limits 与必需段 limits 是同一组摆臂限位的两种写法，counts 是唯一权威：
            // 调用方没给这一段（例如 CLI save-config 只传 limits）时必须按本次要写的 limits 同步，
            // 否则文件里会留下上一次的 soft_limits，下次加载把刚改的限位悄悄改回旧值。
            if (extrasToWrite != null && (extras == null || extras.SoftLimits == null))
            {
                extrasToWrite.SoftLimits = SoftLimitParameters.From(limits);
            }

            string temp = target + ".tmp-" + Environment.TickCount.ToString("X8") + "-" +
                          Guid.NewGuid().ToString("N");
            try
            {
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                ParamDocument doc = new ParamDocument();
                doc.Format = FileFormat;
                doc.UnitConvention = UnitConvention;
                // 溯源记录：写保存当时生效的 DENI 路径；拿不到就不写这个键，不写空串冒充。
                doc.DeniPath = string.IsNullOrEmpty(deniPath) ? null : deniPath;
                doc.Pid = PidDoc.From(pid);
                doc.Swing = SwingDoc.From(swing);
                doc.Limits = LimitsDoc.From(limits);
                DateTime savedAtUtc = DateTime.UtcNow;
                if (extrasToWrite != null)
                {
                    if (extrasToWrite.Pump != null) doc.Pump = PumpDoc.From(extrasToWrite.Pump);
                    if (extrasToWrite.Identify != null) doc.Identify = IdentifyDoc.From(extrasToWrite.Identify);
                    if (extrasToWrite.SoftLimits != null) doc.SoftLimits = SoftLimitDoc.From(extrasToWrite.SoftLimits);
                    if (extrasToWrite.Calibration != null) doc.Calibration = CalibrationDoc.From(extrasToWrite.Calibration);
                    if (extrasToWrite.Captures != null && extrasToWrite.Captures.Count > 0)
                        doc.Captures = CaptureDoc.FromList(extrasToWrite.Captures);
                    extrasToWrite.SavedAtUtc = savedAtUtc;
                }
                doc.SavedAt = FormatTimestamp(savedAtUtc);

                // 与旧文件做字段级合并：只有语义值真的变了的键才覆盖；缺键补齐 / 旧写法 / 未知键
                // 原样保留（明确退场的配置键除外，详见 BuildDocumentText / MergeDocument）。合并失败自动退回完整文档，不阻断保存。
                string documentText = BuildDocumentText(target, doc, extras);
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] payload = new UTF8Encoding(false).GetBytes(documentText);
                    stream.Write(payload, 0, payload.Length);
                }
                // 原子落位：临时文件写完整再改名，中途崩溃不留半截 JSON，旧文件也还在。
                // 目标已存在时用 File.Replace；网络盘 / 非 NTFS / 被别的进程占着时它会一直失败，
                // 这时退化成「整文件覆盖」（临时文件已完整，覆盖失败也只会保留旧文件，不会写半截）。
                ReplaceWithRetry(temp, target);
                if (extras != null)
                {
                    extras.SavedAtUtc = savedAtUtc;
                    extras.SectionsPresent = true;
                    // 刷新溯源到刚写出的这份文件：同一进程里连续保存（改一次存一次）时，
                    // 下一次比较的基准必须是「上一次真正写到盘上的语义值」，不能停留在最初载入那一刻。
                    // 文件已经写成功，溯源刷新失败只丢优化能力，绝不能把保存结果改报成失败。
                    try
                    {
                        extras.Provenance = SaveProvenance.Capture(
                            Path.GetFullPath(target), documentText, pid, swing, limits, extrasToWrite,
                            string.IsNullOrEmpty(deniPath) ? null : deniPath);
                    }
                    catch (Exception provenanceError)
                    {
                        System.Diagnostics.Debug.WriteLine("参数保存后刷新溯源失败（文件已保存）：" +
                                                           DiagText.Describe(provenanceError));
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // 失败也要把半截临时文件收走，别在 exe 同级留垃圾。收不掉就留痕，不空吞。
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception cleanup)
                {
                    System.Diagnostics.Debug.WriteLine("参数临时文件清理失败：" + DiagText.Describe(cleanup));
                }
                error = string.Format("写 {0} 失败：{1}", target, DiagText.Describe(ex));
                return false;
            }
        }

        /// <summary>
        /// 把写完整的临时文件落到目标路径。先试 File.Replace（原子、保留旧文件语义），
        /// 失败重试 3 次（杀软 / 另一个实例短暂占用）；仍失败就整文件覆盖 ——
        /// File.Replace 在网络盘 / 非 NTFS 上会直接抛 IOException，不能让它把保存永远卡死。
        /// 所有失败都保留临时文件直到成功，最后统一清理。
        /// </summary>
        private static void ReplaceWithRetry(string temp, string target)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is NotSupportedException || ex is PlatformNotSupportedException)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(20 * (attempt + 1));
                }
            }

            try
            {
                File.Copy(temp, target, true);
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanup)
                {
                    System.Diagnostics.Debug.WriteLine("参数临时文件清理失败：" + DiagText.Describe(cleanup));
                }
            }
            catch (Exception copyError)
            {
                throw new IOException(string.Format(
                    "目标 {0} 替换失败（{1}: {2}），整文件覆盖也失败（{3}: {4}）。",
                    target, last == null ? "?" : last.GetType().Name, last == null ? "?" : last.Message,
                    copyError.GetType().Name, copyError.Message), copyError);
            }
        }

        /// <summary>
        /// 从 path 读三组参数（兼容旧调用，不要可选段）。path 传 null / 空 时用 DefaultPath。
        /// 校验按**段**隔离：哪一段不合法只回退那一段，其余段照常生效；起摆段内部逐字段修复，
        /// 只换越界的那一项。每一处回退都写进 deniNote，绝不静默回落。
        /// currentDeniPath 是当前生效的 DENI 路径，只拿来跟文件里的 deniPath 记录比对：
        /// 不一致就把提示放进 deniNote 交给上层显示，绝不拿文件里的值去换 DENI / 切拓扑。
        /// </summary>
        public static bool TryLoad(string path, string currentDeniPath,
            out PidGains pid, out SwingUpParams swing, out SafetyLimits limits,
            out string deniPath, out string deniNote, out string error)
        {
            PersistedExtras extras;
            return TryLoad(path, currentDeniPath, out pid, out swing, out limits, out extras,
                out deniPath, out deniNote, out error);
        }

        /// <summary>
        /// 从 path 读三组必需参数 + 可选段（泵 / 辨识 / 软限位 / 标定 / 捕获记录）。
        /// 段缺失 = 整份不认（格式契约破了，猜下去只会更糟）；段存在但不合法 = **只回退那一段**，
        /// 起摆段内部再逐字段修复（只换越界项），全部写进 note，不让一段历史拖垮整份文件。
        /// 可选段里的 pump / soft_limits 会覆盖 swing / limits 的对应字段（工程口径优先），并在下方注释说明。
        /// <para>
        /// 旧版出厂起摆块（指纹 = <see cref="PendulumEngine.IsLegacyFactorySwingDefaults"/>）按**拒绝**处理：
        /// 不用文件里的值起摆，改按当前出厂起摆块载入；原因写进 note 并放进
        /// <see cref="PersistedExtras.SwingLoad"/>，上层据此提示用户恢复默认 / 重新整定。
        /// </para>
        /// </summary>
        public static bool TryLoad(string path, string currentDeniPath,
            out PidGains pid, out SwingUpParams swing, out SafetyLimits limits, out PersistedExtras extras,
            out string deniPath, out string deniNote, out string error)
        {
            pid = PidGains.CreateDefault();
            swing = SwingUpParams.CreateDefault();
            limits = SafetyLimits.CreateDefault();
            extras = null;
            deniPath = null;
            deniNote = null;
            error = null;

            string target = string.IsNullOrEmpty(path) ? DefaultPath : path;
            if (!File.Exists(target))
            {
                error = string.Format(
                    "参数文件不存在：{0}；本次按控制层默认值启动（平衡 PID / 起摆 / 安全阈值 / 软限位 / 标定全部取默认，捕获记录为空）。",
                    target);
                return false;
            }

            ParamDocument doc;
            string rawJson = null;
            try
            {
                // DataContractJsonSerializer 不认 BOM（记事本 / 脚本另存会带 BOM，实测还见过 BOM 被写重）。
                // 先按 BOM / 前导空白归一成 UTF-8 文本再喂序列化器，参数文件手改过也能读回。
                byte[] payload = ReadJsonPayload(target);
                rawJson = Encoding.UTF8.GetString(payload);
                using (MemoryStream stream = new MemoryStream(payload.Length))
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Position = 0;
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ParamDocument));
                    doc = serializer.ReadObject(stream) as ParamDocument;
                }
            }
            catch (Exception ex)
            {
                error = string.Format(
                    "读 {0} 失败：{1}；本次按控制层默认值启动（文件里的参数一项都没生效）。",
                    target, DiagText.Describe(ex));
                return false;
            }

            if (doc == null)
            {
                error = string.Format(
                    "{0} 里没有可识别的参数内容；本次按控制层默认值启动（文件里的参数一项都没生效）。", target);
                return false;
            }
            if (doc.Format != FileFormat)
            {
                // 格式标记不符 = 整份不认，绝不猜旧字段含义。但必须把「丢了什么、回落到什么」写清楚：
                // 现场只看控件的话，看到的是出厂值，看不出自己调过的参数被整份跳过。
                error = string.Format(
                    "{0} 的格式标记是 {1}，本版只认 {2}：整份参数拒载，文件里出现的段（{3}）全部丢弃，"
                    + "本次按控制层默认值启动（平衡 PID / 起摆 / 安全阈值 / 软限位 / 标定取默认，捕获记录不载入）。",
                    target, doc.Format ?? "(空)", FileFormat, DescribePresentSections(doc));
                return false;
            }
            if (doc.Pid == null || doc.Swing == null || doc.Limits == null)
            {
                List<string> missing = new List<string>();
                if (doc.Pid == null) missing.Add("pid");
                if (doc.Swing == null) missing.Add("swingup");
                if (doc.Limits == null) missing.Add("limits");
                error = string.Format(
                    "{0} 缺必需段（缺：{1}）；文件里出现的段是（{2}）；整份拒载，本次按控制层默认值启动，其余段也未生效。",
                    target, string.Join("、", missing.ToArray()), DescribePresentSections(doc));
                return false;
            }

            PidGains loadedPid = doc.Pid.ToGains();
            // 内环 φ 四个键：缺键 ≠ 显式 0。缺键按 PidGains 出厂默认补齐（KpPhi=0.5 / KiPhi=0 /
            // KdPhi=0.0009 / TdLpMs=2），否则内环跟踪被静默关断、回显却仍"合法"。
            // 显式写 0 原样保留；若三项全 0，note 明确告警「内环已关闭，摆臂不跟踪位置指令」。
            // presence 由 has_* 标志 + raw JSON 值键双重判定（旧文件没有 has_* 键）。
            MarkPidKeyPresence(doc.Pid, rawJson);
            PidGains pidFactory = PidGains.CreateDefault();
            List<string> pidMissingInnerKeys = new List<string>();
            if (!doc.Pid.HasKpPhi) { loadedPid.KpPhi = pidFactory.KpPhi; pidMissingInnerKeys.Add("kp_phi"); }
            if (!doc.Pid.HasKiPhi) { loadedPid.KiPhi = pidFactory.KiPhi; pidMissingInnerKeys.Add("ki_phi"); }
            if (!doc.Pid.HasKdPhi) { loadedPid.KdPhi = pidFactory.KdPhi; pidMissingInnerKeys.Add("kd_phi"); }
            if (!doc.Pid.HasTdLpMs) { loadedPid.TdLpMs = pidFactory.TdLpMs; pidMissingInnerKeys.Add("td_lp_ms"); }
            string pidInnerKeyNote = pidMissingInnerKeys.Count == 0
                ? null
                : "pid 段缺键（" + string.Join("、", pidMissingInnerKeys.ToArray()) +
                  "）已按出厂默认补齐：KpPhi=" + FormatValue(loadedPid.KpPhi) +
                  " / KiPhi=" + FormatValue(loadedPid.KiPhi) +
                  " / KdPhi=" + FormatValue(loadedPid.KdPhi) +
                  " / TdLpMs=" + FormatValue(loadedPid.TdLpMs) +
                  "（缺键不能当成显式 0，否则内环 φ 跟踪会被静默关断）。";
            string pidInnerLoopOffNote = loadedPid.KpPhi == 0 && loadedPid.KiPhi == 0 && loadedPid.KdPhi == 0
                ? "内环 φ 跟踪已显式关闭（kp_phi/ki_phi/kd_phi 全为 0），摆臂将不跟踪位置指令；"
                  + "确认这是有意配置再保存，否则把 kp_phi 设为非 0。"
                : null;
            // 加速度反馈律增益：旧文件没有这些键（全 0）或值越界 → 按内置默认补齐。
            // 补齐规则只有一份（PidGains.ResolveAccelerationLaw，运行期用的也是它），
            // 但**绝不静默**：下面把「为什么补、补成了什么」写成一条提示交出去。
            bool legacyAccelerationGains = !loadedPid.HasAccelerationLawGains();
            string accelerationGainNote = null;
            if (loadedPid.ResolveAccelerationLaw())
            {
                accelerationGainNote = legacyAccelerationGains
                    ? string.Format(CultureInfo.InvariantCulture,
                        "外环增益是旧版位置映射律参数（kp={0:G} kd={1:G}），与加速度反馈律量纲不同、不可换算，" +
                        "已用默认 Ka={2:G}/Kad={3:G}/Kai={4:G}（Kφ={5:G}、Kφ̇={6:G}、AccMax={7:G} rad/s²、" +
                        "ArmRateMax={8:G} rad/s、Sign={9:0}）；请在 PID 卡里按新律重新整定。",
                        doc.Pid.Kp, doc.Pid.Kd, loadedPid.Ka, loadedPid.Kad, loadedPid.Kai,
                        loadedPid.KPhi, loadedPid.KPhiDot, loadedPid.AccMax, loadedPid.ArmRateMax, loadedPid.Sign)
                    : string.Format(CultureInfo.InvariantCulture,
                        "pid 段的加速度律增益有缺项或越界（Ka 必须 > g/l={0:G}；" +
                        "Kad 需满足阻尼比下限 Kad ≥ 0.1·√(Ka−g/l)（Ka=500 时约 1.7）；" +
                        "Kφ / Kφ̇ / AccMax / ArmRateMax 必须为正；Sign 只接受 ±1），" +
                        "已回落默认 Ka={1:G}/Kad={2:G}/Kai={3:G}/Kφ={4:G}/Kφ̇={5:G}/AccMax={6:G}/" +
                        "ArmRateMax={7:G}/Sign={8:0}；请核对后重新保存。",
                        PidGains.GravityOverPendulumLength, loadedPid.Ka, loadedPid.Kad, loadedPid.Kai,
                        loadedPid.KPhi, loadedPid.KPhiDot, loadedPid.AccMax, loadedPid.ArmRateMax, loadedPid.Sign);
            }
            SwingUpParams loadedSwing = doc.Swing.ToParams();
            SafetyLimits loadedLimits = doc.Limits.ToLimits();

            // 旧版出厂的 J_eq / mgl = 1.0 / 1.0 隐含 ω_n = 1 rad/s（等效摆长 9.81 m），与任何真实摆杆都差
            // 一个数量级：目标能量 2·mgl 会在摆杆几乎没动时就被判"已到顶"，能量泵反向抽能，起摆上不去。
            // 这一对是旧版**默认值**的指纹（不是用户调出来的合理值），照旧值跑等于把起摆整条支路废掉，
            // 所以读到就当未标定处理，换成按等效摆长配好的一对，并如实写进提示里。
            string legacyEnergyNote = null;
            if (loadedLimits.JEq == 1.0 && loadedLimits.Mgl == 1.0)
            {
                SafetyLimits energyDefaults = SafetyLimits.CreateDefault();
                loadedLimits.JEq = energyDefaults.JEq;
                loadedLimits.Mgl = energyDefaults.Mgl;
                legacyEnergyNote = string.Format(
                    CultureInfo.InvariantCulture,
                    "安全段里的能量参数是旧版出厂占位值 J_eq=1/mgl=1 kg·m²·N·m（比值隐含 ω_n=1 rad/s，等效摆长 9.81 m），"
                    + "已换成按等效摆长 l=0.12 m 配好的 J_eq={0:G}/mgl={1:G}（ω_n=√(mgl/J_eq)={2:F4} rad/s）。"
                    + "实测摆杆质量 / 重心距不同时，按 J_eq=m·l²、mgl=m·g·l 成对给，别只改一项。",
                    loadedLimits.JEq, loadedLimits.Mgl, Math.Sqrt(loadedLimits.Mgl / loadedLimits.JEq));
            }

            // 旧版出厂的「摆臂最大角速度」12 / 20 / 50 rad/s 是软件自设的限速指纹（不是现场整定值）：
            // 台架实测臂峰值 98.7 rad/s、驱动器零故障，手册唯一的速度天花板是 0x6080 = 3000 rpm。
            // 按旧值跑时，速度积分一旦被直流顶到钳位，交变刹车就被半波整流成单向推力，起摆过能后
            // 能量永远抽不回来。读到这三个历史出厂值就迁移到当前出厂值，并如实写进提示。
            // 注意：控制路径本身已按物理天花板执行（SafetyLimits.ArmDriveCeilingRadPerSec），
            // 这里迁移的是参数文件 / 界面里显示的那个旧值。
            string legacyArmSpeedNote = null;
            if (loadedLimits.OmegaArmMaxRadPerSec == 12.0 ||
                loadedLimits.OmegaArmMaxRadPerSec == 20.0 ||
                loadedLimits.OmegaArmMaxRadPerSec == 50.0)
            {
                SafetyLimits speedDefaults = SafetyLimits.CreateDefault();
                legacyArmSpeedNote = string.Format(CultureInfo.InvariantCulture,
                    "安全段的摆臂最大角速度是旧版出厂值 {0:G} rad/s（软件自设的限速，不是机构能力）："
                    + "已迁移到出厂值 {1:F2} rad/s；控制路径按驱动器 0x6080 = 3000 rpm 的物理天花板 "
                    + "{2:F2} rad/s 执行（该项不再限制起摆泵 / 平衡外环的控制权限）。"
                    + "确需更小的值请显式改成其它数后重新保存。",
                    loadedLimits.OmegaArmMaxRadPerSec, speedDefaults.OmegaArmMaxRadPerSec,
                    SafetyLimits.ArmDriveCeilingRadPerSec);
                loadedLimits.OmegaArmMaxRadPerSec = speedDefaults.OmegaArmMaxRadPerSec;
            }

            // 校验失败只回退**出问题的那一段**，绝不因为一段历史字段越界就把整份文件丢掉。
            // 旧口径是「任一组必需参数不合法 = 整份拒载」：参数文件里 theta_switch_rad=0.28（超包线）
            // 与 pump_amplitude_rad_per_s2=0（旧版把它当「未初始化」）两处就足以让用户调好的
            // PID / 软限位 / 标定全部回落出厂值，而提示只有一句「整份拒载」——现场看到的是参数「自己变回去了」。
            // 现在：起摆段**逐字段**修复（只换越界那一项），PID / 安全阈值**逐段**回退，全部写进 notes。
            List<string> notes = new List<string>();
            string reason;
            if (pidInnerKeyNote != null) notes.Add(pidInnerKeyNote);
            if (pidInnerLoopOffNote != null) notes.Add(pidInnerLoopOffNote);

            // 旧文件的 limits 段只有 phi_min_rad / phi_max_rad，没有 arm_min_count / arm_max_count：
            // **绝不**按摆臂每转计数把角度换算成行程（用户从没给过这个比例），按「尚未采集」占位并要求重设。
            if (doc.Limits.ArmLimitCountsMissing)
            {
                notes.Add("参数文件的 limits 段没有摆臂 counts 限位（旧格式只有 phi_min_rad / phi_max_rad）："
                    + "角度行程一律不做换算，已按「尚未采集」占位（全量 counts 区间）。"
                    + "请在「设备参数 / 软限位」里重新采集或手输当前 raw 坐标系下的 0x6064 左右限位后再保存。");
            }

            if (!loadedPid.IsValid(out reason))
            {
                notes.Add("pid 段的平衡增益不合法，已整段回退控制层默认值（其它段照常生效）：" + reason +
                          "；文件里的 pid 段 " + DescribeSection(doc.Pid) +
                          "；回落到 " + DescribeSection(PidDoc.From(PidGains.CreateDefault())));
                loadedPid = PidGains.CreateDefault();
            }

            // ---- 旧版出厂起摆块：拒绝，不按文件值静默跑 ----
            // 指纹只有一份（控制层 PendulumEngine.IsLegacyFactorySwingDefaults），这里不复制判据。
            // 那组值（Ke=1 / AMax=8 / KPhi=6 / KPhiDot=1.5 / PumpHz=1 / Timeout ≤ 30 s / 捕获门全 0）
            // 不是现场整定，是旧版出厂默认；按它起摆能量注入不足，台架实测必然以 SwingUpTimeout 结束。
            // 做法 = 拒绝这一段并按当前出厂起摆块载入（不把旧块当"迁移"偷偷改写），原因与原始值交给上层
            // （PersistedExtras.SwingLoad）并写进 note，界面据此提示「请恢复默认或重新整定」。
            SwingLoadDecision swingLoad = new SwingLoadDecision();
            string legacySwingDetail;
            if (PendulumEngine.IsLegacyFactorySwingDefaults(loadedSwing, out legacySwingDetail))
            {
                SwingUpParams factorySwing = SwingUpParams.CreateDefault();
                swingLoad.LegacyFactoryBlockRejected = true;
                swingLoad.RejectedBlock = loadedSwing;
                swingLoad.Reason = string.Format(CultureInfo.InvariantCulture,
                    "起摆参数仍是旧版出厂块，已拒绝按文件值起摆：{0} 本次按当前出厂起摆参数载入"
                    + "（Ke={1:G} / AMax={2:G} rad/s² / KPhi={3:G} / KPhiDot={4:G} / Timeout={5} ms，其余按控制层默认）；"
                    + "请在参数卡核对后重新保存，要保留自定义整定请重新整定 Ke / AMax。文件里的 swingup 段 {6}",
                    legacySwingDetail, factorySwing.Ke, factorySwing.AMax, factorySwing.KPhi, factorySwing.KPhiDot,
                    factorySwing.TimeoutMs, DescribeSection(doc.Swing));
                loadedSwing = factorySwing;
                // 旧块被整块替换成当前出厂值时，软中心带按当前出厂 20 rad 生效（与控制层
                // SwingUpParams.CreateDefault 同一口径；避免「换了新出厂块却还把摆臂行程掐在 3.6」）。
                loadedSwing.CenterBandRad = FactoryCenterBandRad;
                notes.Add(swingLoad.Reason);
            }
            else
            {
                // 目标能量裕量 ε：缺键 ≠ 显式 0。缺键读 0，而 0 落在合法区间 [−0.02, 0.02] 内，
                // 会被 IsValid 当成「用户显式设了 ε=0」放行 —— 目标能量恰在分界线上，摆杆会按设计
                // 飞越顶端而不是折返（2026-11-20 台架：捕获率 3/3 → 0/1）。presence 与 pid 四件套同一套规则：
                // has_* 标志 + raw JSON 值键双重判定；缺键按**当前出厂默认**补齐（不硬编码数值）。
                MarkSwingKeyPresence(doc.Swing, rawJson);
                SwingUpParams swingFactory = SwingUpParams.CreateDefault();
                if (!doc.Swing.HasTargetEnergyGain)
                {
                    loadedSwing.TargetEnergyGain = swingFactory.TargetEnergyGain;
                    notes.Add("swingup 段缺键 target_energy_gain，已按当前出厂默认补齐 "
                              + FormatValue(loadedSwing.TargetEnergyGain)
                              + "（缺键不能当成显式 0：ε=0 表示目标能量恰在分界线上，摆杆会飞越顶端而不是折返）。");
                }
                else if (loadedSwing.TargetEnergyGain == 0)
                {
                    notes.Add("target_energy_gain=0 表示目标能量恰在分界线上，摆杆不会折返也不会稳定超越，"
                              + "通常不是想要的值；确认这是有意配置再保存（出厂默认 "
                              + FormatValue(swingFactory.TargetEnergyGain) + "）。");
                }
                string assumedSwingNote;
                if (FillUnconfiguredSwingFields(ref loadedSwing, out assumedSwingNote))
                    notes.Add(assumedSwingNote);
            }

            SwingUpParams resolvedSwing;
            string swingRepair;
            if (loadedSwing.TryResolveInvalidFields(out resolvedSwing, out swingRepair))
            {
                notes.Add("swingup 段有字段越界，已**逐字段**回退出厂值（只换那一项，其它字段照常生效）："
                          + swingRepair + "；文件里的 swingup 段 " + DescribeSection(doc.Swing));
                loadedSwing = resolvedSwing;
            }
            // 交叉约束：快速提交带必须是硬捕获门的子集（逐字段修复后所有单项都已合法，这里再判组合）。
            // 文件里写越界时不整段拒载，按硬门收紧并留 note；保存 / CLI 入口走 TryValidateCaptureHandoffGates
            // 的拒收口径，不产生半套生效。
            string handoffGateNote;
            if (ApplyCaptureHandoffGates(ref loadedSwing, out handoffGateNote))
                notes.Add(handoffGateNote);
            // 逐字段修复 / 收紧之后仍不合法 = 组内组合约束（或本版新增字段在旧文件里缺键 = 0）。
            // **绝不因为一个键整段回退**：整段替换会把用户调好的 Ke / AMax / 中心带等全部换成出厂值
            // （2026-11-20 台架事故：新增 capture_commit_omega_rad_per_s 后旧文件缺这个键 → 0 →
            //  交棒组 IsValid 失败 → 整段下落，Ke 700 变成 60、radius/中心带一同丢失）。
            // 这里只修越界的组 / 项，其余字段原样保留；每一处修改都写进 note。
            string salvageNote;
            int salvageRounds = 0;
            while (!loadedSwing.IsValid(out reason) && salvageRounds < 8 &&
                   TrySalvageSwingParams(ref loadedSwing, out salvageNote))
            {
                salvageRounds++;
                notes.Add(salvageNote);
                if (loadedSwing.TryResolveInvalidFields(out resolvedSwing, out swingRepair))
                {
                    notes.Add("swingup 段修正后有单项越界，已逐字段回退出厂值（其它字段照常生效）：" + swingRepair);
                    loadedSwing = resolvedSwing;
                }
            }
            if (!loadedSwing.IsValid(out reason))
            {
                // 兜底只回退**捕获门两组**（本版新增字段都落在这一组）：能量 / 中心带 / 切换判据
                // 这些真正现场整定出来的值必须保住。把「为什么、回落成什么」摊开给现场看。
                SwingUpParams defSwing = SwingUpParams.CreateDefault();
                loadedSwing.CaptureModeAmplitudeMaxRad = defSwing.CaptureModeAmplitudeMaxRad;
                loadedSwing.CaptureStrokePerModeRad = defSwing.CaptureStrokePerModeRad;
                loadedSwing.CaptureStrokeMarginRad = defSwing.CaptureStrokeMarginRad;
                loadedSwing.CaptureAdverseRateStrokeSec = defSwing.CaptureAdverseRateStrokeSec;
                loadedSwing.CaptureArmRateLimitRadPerSec = defSwing.CaptureArmRateLimitRadPerSec;
                loadedSwing.CapturePendulumRateRadPerSec = defSwing.CapturePendulumRateRadPerSec;
                loadedSwing.CapturePrepareApproachRad = defSwing.CapturePrepareApproachRad;
                loadedSwing.CapturePrepareEnergyMarginRatio = defSwing.CapturePrepareEnergyMarginRatio;
                loadedSwing.CaptureCommitArmRateRadPerSec = defSwing.CaptureCommitArmRateRadPerSec;
                loadedSwing.CaptureCommitOmegaRadPerSec = defSwing.CaptureCommitOmegaRadPerSec;
                loadedSwing.CaptureCommitHoldCycles = defSwing.CaptureCommitHoldCycles;
                loadedSwing.SwitchHoldDecayPerCycle = defSwing.SwitchHoldDecayPerCycle;
                notes.Add("swingup 段的捕获门组合约束仍算不出可用解（" + reason +
                          "）：只把捕获门两组恢复出厂值，能量 / 中心带 / 切换判据按文件值保留；"
                          + "文件里的 swingup 段 " + DescribeSection(doc.Swing));
            }
            if (!loadedSwing.IsValid(out reason))
            {
                // 真正不可用（各项都被上面修过仍不合法）：才整段回落，并明确写出「文件值 → 生效值」。
                notes.Add("swingup 段已无可修复的合法组合，整段回退控制层默认起摆参数（其它段照常生效）：" + reason +
                          "；文件里的 swingup 段 " + DescribeSection(doc.Swing) +
                          "；回落到 " + DescribeSection(SwingDoc.From(SwingUpParams.CreateDefault())));
                loadedSwing = SwingUpParams.CreateDefault();
                loadedSwing.CenterBandRad = FactoryCenterBandRad;
            }

            // 摆臂软限位的旧「单侧行程不超过 3π」判据已取消：软限位只认当前 raw 坐标系 counts，
            // 行程多大由现场机械决定，软件不假设任何角度比例，也就不存在「超宽 = 不合理」的角度上界。

            // limits 段同样只修越界项，不整段回退：一个无关字段（例如 theta 两项互相矛盾）不配
            // 把用户标定好的 radius_ratio / 摆臂 counts / 能量参数一起清掉（2026-11-20 事故同源）。
            string limitsRepair;
            if (!loadedLimits.IsValid(out reason) && TrySalvageLimits(ref loadedLimits, out limitsRepair))
            {
                notes.Add("limits 段有字段越界，已只修正越界项（其余字段原样保留）：" + limitsRepair +
                          "；文件里的 limits 段 " + DescribeSection(doc.Limits));
            }
            if (!loadedLimits.IsValid(out reason))
            {
                notes.Add("limits 段已无可修复的合法组合，整段回退控制层默认值（其它段照常生效）：" + reason +
                          "；文件里的 limits 段 " + DescribeSection(doc.Limits) +
                          "；回落到 " + DescribeSection(LimitsDoc.From(SafetyLimits.CreateDefault())));
                loadedLimits = SafetyLimits.CreateDefault();
            }

            if (!string.IsNullOrEmpty(accelerationGainNote)) notes.Add(accelerationGainNote);
            if (!string.IsNullOrEmpty(legacyEnergyNote)) notes.Add(legacyEnergyNote);
            if (!string.IsNullOrEmpty(legacyArmSpeedNote)) notes.Add(legacyArmSpeedNote);
            PersistedExtras loadedExtras = new PersistedExtras();
            loadedExtras.SwingLoad = swingLoad;

            // ---- 标定（先读，软限位的 counts 反推要用它）----
            if (doc.Calibration != null)
            {
                CalibrationParameters calibration = doc.Calibration.ToParameters();
                if (calibration.IsValid(out reason))
                {
                    loadedExtras.Calibration = calibration;
                    // 文件里真有这一段且通过校验：入口恢复构造期标定时只认这个来源。
                    loadedExtras.CalibrationFromFile = true;
                }
                else
                {
                    CalibrationParameters fallbackCalibration = CalibrationParameters.CreateDefault();
                    // 段不合法也不能把「用户明确清空」洗成 false：否则坏文件 + 重启会让引擎重新自动建零点。
                    // 只保留 cleared 这一个安全标志；其它数值仍按默认回退。
                    fallbackCalibration.CalibrationCleared = doc.Calibration.CalibrationCleared;
                    loadedExtras.Calibration = fallbackCalibration;
                    notes.Add("标定段不合法，已整段回退默认值：" + reason +
                              "；文件里的标定段 " + DescribeSection(doc.Calibration) +
                              "；回落到 " + DescribeSection(CalibrationDoc.From(fallbackCalibration)));
                }
            }
            else
            {
                loadedExtras.Calibration = CalibrationParameters.CreateDefault();
            }

            if (loadedExtras.Calibration != null && loadedExtras.Calibration.CalibrationCleared)
            {
                notes.Add("参数文件标记位置标定已被用户清空（calibration.calibration_cleared=true）："
                    + "启动后必须恢复控制层同一个 CalibrationCleared 状态，拒绝自动建立零点 / 使能自动运动；"
                    + "重新采集完整三点（或规整化）并确认后，调用 MarkCalibrationComplete / ConfirmCalibrationComplete 清除。");
            }

            // 摆杆一圈的量 = |360° − 0°|（两点标定派生，不再由用户直接输入）。
            // 旧文件只有 pendulum_cpr 时保留该值作兼容并提示重新标定，不按任何比例推算。
            if (loadedExtras.Calibration != null)
            {
                int derivedCpr;
                if (loadedExtras.Calibration.TryCalibratePendulumCpr(out derivedCpr))
                {
                    if (Math.Abs(loadedExtras.Calibration.PendulumCpr - derivedCpr) > 1e-9)
                    {
                        notes.Add(string.Format(CultureInfo.InvariantCulture,
                            "摆杆一圈的量按 0°/360° 两点重新算成 {0} counts（文件里 pendulum_cpr 写的是 {1}）：以两点标定为准。",
                            derivedCpr, loadedExtras.Calibration.PendulumCpr));
                    }
                    loadedExtras.Calibration.PendulumCpr = derivedCpr;
                }
                else
                {
                    // 只有一半 = 不是可运行标定（判据唯一来源 = CalibrationParameters.CheckPendulumPair）。
                    // 这里只负责把「缺哪一半」说清楚；真正的拦截在起摆 / 平衡前置校验里。
                    // MissingBoth 不刷提示：从未标定过是首次运行正常态，只由界面常显（设备参数卡）说明。
                    PendulumPairCheck pair = loadedExtras.Calibration.CheckPendulumPair();
                    if (pair.Issue != PendulumPairIssue.MissingBoth)
                    {
                        notes.Add(pair.Reason + " 保留文件里的 pendulum_cpr=" +
                                  loadedExtras.Calibration.PendulumCpr.ToString("0.###", CultureInfo.InvariantCulture) +
                                  " 作兼容值；请在「设备参数」里补齐 0° 与 360° 两点。");
                    }
                }
            }

            // ---- 泵参数（工程口径：k_e / 泵幅 deg / 泵频 / 相位 deg）----
            if (doc.Pump != null)
            {
                PumpParameters pump = doc.Pump.ToParameters();
                if (pump.IsValid(out reason))
                {
                    SwingUpParams candidate = loadedSwing;
                    double swingKe = loadedSwing.Ke;
                    double swingPumpAmp = loadedSwing.PumpAmplitude;
                    pump.ApplyTo(ref candidate);
                    // 泵幅 0（pump_amp_deg=0）在旧文件里同时可能是「关泵」和「没配过」：
                    // 不能让它静默盖掉 swingup 段刚设的非零泵幅。判定 = 泵段为 0 而 swingup 段非 0
                    // → 以 swingup 段为准并写 note。（2026-11-20 台架：setswingup --pump-amp 设了
                    // 40 / 100，摆臂 40 s 一动不动；根因就是旧 pump 段把它清零了。）
                    if (!(pump.PumpAmpDeg > 0) && ControlMath.IsFinite(swingPumpAmp) && swingPumpAmp > 0)
                    {
                        candidate.PumpAmplitude = swingPumpAmp;
                        notes.Add(string.Format(CultureInfo.InvariantCulture,
                            "泵段 pump_amp_deg=0 但 swingup 段泵幅={0:G6} rad/s²：按 swingup 段生效"
                            + "（旧文件里 0 既表示关泵也表示没配过，不能静默盖掉现场刚设的值）。",
                            swingPumpAmp));
                    }
                    // 泵段是**旧能量泵**口径的工程视图：它的 k_e 与 swingup 段是同一个控制层字段。
                    // 但能量法现在归 swingup 段管，泵段只该负责泵幅 / 泵频 / 相位 —— 否则旧文件里
                    // 那个历史的 pump.ke（台架存的是 1.0）会在载入时**静默盖掉**现场刚调好的 ke
                    // （例如 30），起摆又退回「权限只有一点点」的老状态，而且日志里看不出是谁改的。
                    // 所以：ke 一律以 swingup 段为准，两段不一致就写一条 note 说明忽略了泵段的值。
                    if (ControlMath.IsFinite(swingKe) && swingKe > 0 &&
                        ControlMath.IsFinite(pump.Ke) && pump.Ke > 0 &&
                        Math.Abs(pump.Ke - swingKe) > 1e-9)
                    {
                        candidate.Ke = swingKe;
                        notes.Add(string.Format(CultureInfo.InvariantCulture,
                            "泵段 ke_rad_per_s2_per_j={0:G6} 与 swingup 段的 ke={1:G6} 不一致：按 swingup 段生效"
                            + "（泵段只管泵幅 / 泵频 / 相位，能量增益归 swingup 段）。要改能量增益请改 swingup.ke。",
                            pump.Ke, swingKe));
                    }
                    if (candidate.IsValid(out reason))
                    {
                        loadedSwing = candidate;
                        // 落盘口径也同步成生效值，避免下次保存又把旧的 pump.ke / 泵幅写回去。
                        PumpParameters effective = PumpParameters.From(candidate);
                        pump.Ke = candidate.Ke;
                        pump.PumpAmpDeg = effective.PumpAmpDeg;
                        pump.PumpHz = effective.PumpHz;
                        loadedExtras.Pump = pump;
                    }
                    else
                    {
                        PumpParameters fallbackPump = PumpParameters.From(loadedSwing);
                        loadedExtras.Pump = fallbackPump;
                        notes.Add("泵参数段换算到控制层后不合法，已整段回退当前 swingup 段的泵字段：" + reason +
                                  "；文件里的泵段 " + DescribeSection(doc.Pump) +
                                  "；回落到 " + DescribeSection(PumpDoc.From(fallbackPump)));
                    }
                }
                else
                {
                    PumpParameters fallbackPump = PumpParameters.From(loadedSwing);
                    loadedExtras.Pump = fallbackPump;
                    notes.Add("泵参数段不合法，已整段回退当前 swingup 段的泵字段：" + reason +
                              "；文件里的泵段 " + DescribeSection(doc.Pump) +
                              "；回落到 " + DescribeSection(PumpDoc.From(fallbackPump)));
                }
            }
            else
            {
                loadedExtras.Pump = PumpParameters.From(loadedSwing);
            }

            // ---- 软限位（唯一单位 = 当前 raw 坐标系下的 0x6064 counts；旧 deg 字段忽略、不换算）----
            if (doc.SoftLimits != null)
            {
                SoftLimitParameters softLimits = doc.SoftLimits.ToParameters();
                string softReason;
                if (softLimits.IsValid(out softReason))
                {
                    // 正常保存时两段同源；手改文件让两段对不上时先留一条提示再按 soft_limits 生效，不静默覆盖。
                    if (softLimits.MinCounts != loadedLimits.ArmMinCount ||
                        softLimits.MaxCounts != loadedLimits.ArmMaxCount)
                    {
                        notes.Add(string.Format(CultureInfo.InvariantCulture,
                            "soft_limits 与 limits 段的摆臂限位不一致，按 soft_limits 生效：" +
                            "当前 raw 坐标系 counts [{0},{1}]（limits 段写的是 [{2},{3}]）。",
                            softLimits.MinCounts, softLimits.MaxCounts,
                            loadedLimits.ArmMinCount, loadedLimits.ArmMaxCount));
                    }
                    loadedLimits.ArmMinCount = softLimits.MinCounts;
                    loadedLimits.ArmMaxCount = softLimits.MaxCounts;
                    loadedExtras.SoftLimits = softLimits;
                }
                else
                {
                    // 旧文件只有 min_deg / max_deg（缺 counts）或 counts 本身不合法：不拿比例硬算，要求重设。
                    SoftLimitParameters fallbackSoft = SoftLimitParameters.From(loadedLimits);
                    loadedExtras.SoftLimits = fallbackSoft;
                    notes.Add("soft_limits 段没有可用的 counts 限位（" + softReason + "）："
                        + "旧文件的角度限位不做任何换算，已保留 limits 段 / 当前生效值；"
                        + "请重新采集或手输当前 raw 坐标系下的 0x6064 左右限位。文件里的 soft_limits 段 " +
                        DescribeSection(doc.SoftLimits));
                }
            }
            else
            {
                loadedExtras.SoftLimits = SoftLimitParameters.From(loadedLimits);
            }

            // 「左限位 < 零点 < 右限位」三点校验（现场参数模型）：三者都在且越序 = 配置错误，
            // 整对软限位按「尚未采集」占位并要求重采，绝不按比例推算。
            if (loadedExtras.Calibration != null && loadedExtras.Calibration.HasArmZeroCount &&
                !loadedLimits.ArmLimitsArePlaceholder)
            {
                int zero = loadedExtras.Calibration.ArmZeroCount;
                if (!(loadedLimits.ArmMinCount < zero && zero < loadedLimits.ArmMaxCount))
                {
                    notes.Add(string.Format(CultureInfo.InvariantCulture,
                        "摆臂三点关系不合法：左限位 {0} / 零点 {1} / 右限位 {2}（要求 左 < 零点 < 右）。" +
                        "软限位已按「尚未采集」占位，请在「设备参数 / 软限位」里重新采集或手输。",
                        loadedLimits.ArmMinCount, zero, loadedLimits.ArmMaxCount));
                    loadedLimits.ArmMinCount = SafetyLimits.DefaultArmMinCount;
                    loadedLimits.ArmMaxCount = SafetyLimits.DefaultArmMaxCount;
                    loadedExtras.SoftLimits = SoftLimitParameters.From(loadedLimits);
                }
            }

            // ---- 辨识结果 ----
            if (doc.Identify != null)
            {
                IdentifyParameters identify = doc.Identify.ToParameters();
                if (identify.IsValid(out reason))
                    loadedExtras.Identify = identify;
                else
                {
                    loadedExtras.Identify = new IdentifyParameters();
                    notes.Add("辨识结果段不合法，已按「未辨识」处理（自然频率 0 / 置信度 0 / 无辨识时间）：" + reason +
                              "；文件里的 identify 段 " + DescribeSection(doc.Identify));
                }
            }
            else
            {
                loadedExtras.Identify = new IdentifyParameters();
            }

            // ---- 捕获记录（逐条校验，坏条目跳过但留痕）----
            loadedExtras.Captures = new List<CaptureRecord>();
            if (doc.Captures != null)
            {
                int skipped = 0;
                List<string> skippedDetails = new List<string>();
                for (int i = 0; i < doc.Captures.Count; i++)
                {
                    CaptureRecord record;
                    string captureError;
                    if (doc.Captures[i].TryToRecord(out record, out captureError))
                        loadedExtras.Captures.Add(record);
                    else
                    {
                        skipped++;
                        // 留前 3 条的具体原因（时间戳 + 哪个字段坏了），再多只报总数，避免刷屏。
                        if (skippedDetails.Count < 3)
                        {
                            string stamp = string.IsNullOrEmpty(doc.Captures[i].Timestamp)
                                ? "无时间戳"
                                : doc.Captures[i].Timestamp;
                            skippedDetails.Add("第 " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                                               " 条[" + stamp + "]：" + captureError);
                        }
                    }
                }
                loadedExtras.Captures.Sort(delegate (CaptureRecord a, CaptureRecord b)
                {
                    return a.TimestampUtc.CompareTo(b.TimestampUtc);
                });
                loadedExtras.ClampCaptures();
                if (skipped > 0)
                {
                    string detail = skippedDetails.Count == 0
                        ? ""
                        : "（" + string.Join("；", skippedDetails.ToArray()) +
                          (skipped > skippedDetails.Count ? "；其余不再列出" : "") + "）";
                    notes.Add("捕获记录有 " + skipped.ToString(CultureInfo.InvariantCulture) +
                              " 条不合法，已跳过" + detail + "。");
                }
            }
            loadedExtras.SavedAtUtc = ParseTimestamp(doc.SavedAt);
            // saved_at 每次保存都会写，不能拿它判断「可选段是否存在」；只看五个真正的可选段。
            loadedExtras.SectionsPresent = doc.Pump != null || doc.Identify != null ||
                                           doc.SoftLimits != null || doc.Calibration != null ||
                                           doc.Captures != null;

            // ---- 本版已删除 / 未知的键：忽略但不丢（保存时原样保留），并明确告知 ----
            string unknownKeyNote = DescribeUnknownKeys(rawJson);
            if (!string.IsNullOrEmpty(unknownKeyNote))
                notes.Add(unknownKeyNote);

            // ---- 保存溯源：记下「这份文件读成了什么」+ 原始 JSON，保存时据此只覆盖真正改过的字段 ----
            // 没有这份快照，Save 无法区分「用户改了这个字段」与「载入时按缺键补了默认值」，
            // 只能整段重写 —— 缺键补齐 / 旧键废弃都会变成数据丢失。
            // 溯源只是优化：任何意外都不能把「文件已经读成功」变成加载失败。
            try
            {
                loadedExtras.Provenance = SaveProvenance.Capture(
                    Path.GetFullPath(target), rawJson, loadedPid, loadedSwing, loadedLimits, loadedExtras, doc.DeniPath);
            }
            catch (Exception provenanceError)
            {
                System.Diagnostics.Debug.WriteLine("参数载入溯源快照失败（不影响本次载入）：" +
                                                   DiagText.Describe(provenanceError));
            }

            pid = loadedPid;
            swing = loadedSwing;
            limits = loadedLimits;
            extras = loadedExtras;
            deniPath = doc.DeniPath;
            string deniNoteText = DeniPathNote(doc.DeniPath, currentDeniPath);
            if (notes.Count > 0)
            {
                string optionalNote = string.Join("；", notes.ToArray());
                deniNoteText = string.IsNullOrEmpty(deniNoteText) ? optionalNote : deniNoteText + "；" + optionalNote;
            }
            deniNote = deniNoteText;
            return true;
        }

        /// <summary>
        /// 参数文件里「缺键 = 0」或没有其它校验口的起摆字段，在进入
        /// <see cref="SwingUpParams.TryResolveInvalidFields"/> 之前按出厂默认补齐/修正。
        /// 控制层对这类字段的口径是「0 / 非有限 / 明显越界 = 未配置，按内置默认补齐」
        /// （<c>SwingUpController.Pick</c>），载入时用同一份默认标注，避免把它们当「越界」重复刷记录。
        /// <para>
        /// 实际处理范围（与实现逐条一致）：
        /// ① 以下字段值为 0（= 文件里没这个键）→ 取 <see cref="SwingUpParams.CreateDefault"/> 的值：
        ///    BootstrapAccelRadPerSec2 / BootstrapSec / EnergySoftOmegaRadPerSec / OmegaWindowMs /
        ///    ThetaFadeStartRad / ThetaFadeEndRad / ThetaFadeFloor / EnergyExtractRatio / EnergyDeadbandRatio。
        /// ② CenterAuthRatio：非有限数或 ≤0 → 出厂默认（它没有别的校验口）。
        /// ③ CenterBandRad：**单一口径 = 0 表示未配置**（参数文件这一层无法区分「缺键」与「显式写 0」），
        ///    0 / 负值 / 非有限数 → 本版出厂 20 rad，与控制层 <c>SwingUpController.Pick</c> 的
        ///    「≤0 / 非有限 → 默认」一致；&gt;100 rad 属区间越界，参数层**不重复** (0,100] 判据，
        ///    交给 <see cref="SwingUpParams.TryResolveInvalidFields"/> 按唯一权威修正；合法正数原样保留。
        ///    CLI 的 <c>setswingup --center-band 0</c> 是显式用户输入，<see cref="SwingUpParams.IsValid"/>
        ///    应统一成同一口径（0 = 未配置，入口先按默认补齐再校验），不能「参数文件接受、CLI 拒绝」。
        /// ④ ExtractGain：0（未配置）→ 本版出厂 60；负值 / 非有限值的修正由
        ///    <see cref="SwingUpParams.TryResolveInvalidFields"/> 负责（唯一权威），这里不重复。
        /// ⑤ PumpFrequencyHz：值为 0 且泵幅 &gt;0 → 出厂频率（否则 sin(0)=0，泵完全不出力）。
        /// ⑥ 捕获交棒六项（CapturePrepareApproachRad / CapturePrepareEnergyMarginRatio /
        ///    CaptureCommitArmRateRadPerSec / CaptureCommitOmegaRadPerSec / CaptureCommitHoldCycles /
        ///    SwitchHoldDecayPerCycle（千分之一拍/拍，2 = 0.002 拍/拍））值为 0（= 旧文件缺键）→ <see cref="SwingUpParams.CreateDefault"/> 的默认值。
        ///    **新增交棒字段必须同步加在这里**：它们属于同一个「配了任意一项就整组校验」的组，
        ///    少补一个 0 就会让整组 IsValid 失败 —— 而历史实现遇到组失败会把整段 swingup 换成出厂值，
        ///    用户的 Ke / AMax / 中心带全部丢失（2026-11-20 台架：新增 capture_commit_omega 后 Ke 700→60）。
        ///    （旧的 |u| / |ω| 提交带两项已被上游撤销，参数层不持久化、不复活。）
        /// 其余非 0 的非法值不在这里处理，原样留给逐字段修复并如实报越界；
        /// 「提交带 ⊂ 硬捕获门」的交叉约束由 <see cref="ApplyCaptureHandoffGates"/> 在逐字段修复后收紧。
        /// 每一处替换都写进 note；填好的值下次保存即写入文件。
        /// </para>
        /// </summary>
        private static bool FillUnconfiguredSwingFields(ref SwingUpParams p, out string note)
        {
            note = null;
            SwingUpParams def = SwingUpParams.CreateDefault();
            List<string> assumed = new List<string>();

            // 回中项预算比例：0 = 未配置；负数 / NaN 没有别的校验口，同样按默认补齐（控制层 Pick 也是这么做的）。
            if (!ControlMath.IsFinite(p.CenterAuthRatio) || p.CenterAuthRatio <= 0)
            {
                assumed.Add("CenterAuthRatio=" + FormatValue(p.CenterAuthRatio) + " → " +
                            FormatValue(def.CenterAuthRatio));
                p.CenterAuthRatio = def.CenterAuthRatio;
            }
            AssumeUnset("BootstrapAccelRadPerSec2", ref p.BootstrapAccelRadPerSec2, def.BootstrapAccelRadPerSec2, assumed);
            AssumeUnset("BootstrapSec", ref p.BootstrapSec, def.BootstrapSec, assumed);
            AssumeUnset("EnergySoftOmegaRadPerSec", ref p.EnergySoftOmegaRadPerSec, def.EnergySoftOmegaRadPerSec, assumed);
            AssumeUnset("OmegaWindowMs", ref p.OmegaWindowMs, def.OmegaWindowMs, assumed);
            AssumeUnset("ThetaFadeStartRad", ref p.ThetaFadeStartRad, def.ThetaFadeStartRad, assumed);
            AssumeUnset("ThetaFadeEndRad", ref p.ThetaFadeEndRad, def.ThetaFadeEndRad, assumed);
            AssumeUnset("ThetaFadeFloor", ref p.ThetaFadeFloor, def.ThetaFadeFloor, assumed);
            AssumeUnset("EnergyExtractRatio", ref p.EnergyExtractRatio, def.EnergyExtractRatio, assumed);
            AssumeUnset("EnergyDeadbandRatio", ref p.EnergyDeadbandRatio, def.EnergyDeadbandRatio, assumed);
            // 软中心带：单一口径 = 0 表示未配置（参数文件无法区分缺键与显式 0）。
            // 0 / 负值 / 非有限数 → 本版出厂 20 rad，与控制层 SwingUpController.Pick 的「≤0 / 非有限 → 默认」一致。
            // >100 rad 是区间越界，参数层不重复 (0,100] 判据，交给 SwingUpParams.TryResolveInvalidFields 修正。
            // 合法正数（含用户显式写的 3.6）原样保留：这是出厂默认的变更，不是运行时钳位。
            if (!ControlMath.IsFinite(p.CenterBandRad) || p.CenterBandRad <= 0)
            {
                assumed.Add("CenterBandRad=" + FormatValue(p.CenterBandRad) + " → " +
                            FormatValue(FactoryCenterBandRad) +
                            "（未配置：0 / 负值 / 非有限数均表示未配置；参数文件里 0 与缺键无法区分）");
                p.CenterBandRad = FactoryCenterBandRad;
            }
            // 抽取增益：缺键（0）按本版出厂 60 补齐；负值 / 非有限值的修正由
            // SwingUpParams.TryResolveInvalidFields（唯一权威）负责，不在这里重复一套口径。
            if (p.ExtractGain == 0)
            {
                assumed.Add("ExtractGain=" + FormatValue(0) + " → " + FormatValue(FactoryExtractGain) + "（未配置）");
                p.ExtractGain = FactoryExtractGain;
            }

            // 预捕获回中 / 快速提交 / 漏桶（2026-11-20）：旧文件缺键 = 0 = 未配置，按 CreateDefault 补齐。
            // 合法范围与越界修正由 SwingUpParams.TryResolveInvalidFields 负责；这里只处理「没有值」。
            AssumeUnset("CapturePrepareApproachRad", ref p.CapturePrepareApproachRad, def.CapturePrepareApproachRad, assumed);
            AssumeUnset("CapturePrepareEnergyMarginRatio", ref p.CapturePrepareEnergyMarginRatio, def.CapturePrepareEnergyMarginRatio, assumed);
            AssumeUnset("CaptureCommitArmRateRadPerSec", ref p.CaptureCommitArmRateRadPerSec, def.CaptureCommitArmRateRadPerSec, assumed);
            AssumeUnset("CaptureCommitOmegaRadPerSec", ref p.CaptureCommitOmegaRadPerSec, def.CaptureCommitOmegaRadPerSec, assumed);
            AssumeUnset("CaptureCommitHoldCycles", ref p.CaptureCommitHoldCycles, def.CaptureCommitHoldCycles, assumed);
            AssumeUnset("SwitchHoldDecayPerCycle", ref p.SwitchHoldDecayPerCycle, def.SwitchHoldDecayPerCycle, assumed);

            // 泵幅 > 0 而泵频 = 0：控制层会用内置默认频率（否则 sin(0)=0，泵完全不出力），
            // 这里按同一口径标注并写明原因，保存一次即写入文件。
            if (p.PumpAmplitude > 0 && !(p.PumpFrequencyHz > 0))
            {
                assumed.Add("PumpFrequencyHz=" + FormatValue(p.PumpFrequencyHz) + " → " +
                            FormatValue(def.PumpFrequencyHz) + "（泵幅 > 0 时 0 Hz 等于关泵）");
                p.PumpFrequencyHz = def.PumpFrequencyHz;
            }

            if (assumed.Count == 0)
                return false;
            note = "swingup 段这些字段没有配置值（旧文件缺键 = 0），已按当前出厂默认标注后再校验："
                + string.Join("、", assumed.ToArray()) +
                "；只在运行期按此生效（保存时是否写回取决于入口：带载入溯源的 GUI / 增量保存保留缺键，CLI save-config 按当前生效值重写）。";
            return true;
        }

        /// <summary>缺键（= 0）的字段按出厂默认标注；非 0 的值原样留给逐字段修复去判越界。</summary>
        private static void AssumeUnset(string name, ref double field, double fallback, List<string> assumed)
        {
            if (field != 0)
                return;
            field = fallback;
            assumed.Add(name + "=" + FormatValue(fallback));
        }

        /// <summary>缺键（= 0）的整数字段按出厂默认标注；非 0 的值原样留给逐字段修复去判越界。</summary>
        private static void AssumeUnset(string name, ref int field, int fallback, List<string> assumed)
        {
            if (field != 0)
                return;
            field = fallback;
            assumed.Add(name + "=" + fallback.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 捕获交棒参数的「提交保护 ⊂ 硬捕获门」交叉校验（**拒收**口径，供保存 / CLI 使用）。
        /// 控制层 <c>SwingUpController.Step</c> 运行时会再收紧一次（双层保护），但参数层若允许
        /// 把越界的提交保护写进文件 / 生效，现场看到的配置值就会与真正生效的值不一致。
        /// 当前仍生效的两条：
        /// <list type="bullet">
        /// <item>CaptureCommitArmRateRadPerSec ≤ CaptureArmRateLimitRadPerSec；</item>
        /// <item>CaptureCommitOmegaRadPerSec ≤ OmegaSwitchRadPerSec（台架 R1-1：ψ 小 + ω 大的高速穿越
        /// 能绕过硬门的 |u| 判据，ω 上限必须独立保留）。</item>
        /// </list>
        /// 值为 0 = 未配置（按 CreateDefault 补齐）；此时不拒收，运行期按硬门收紧。
        /// </summary>
        public static bool TryValidateCaptureHandoffGates(SwingUpParams p, out string error)
        {
            error = null;
            SwingUpParams def = SwingUpParams.CreateDefault();

            double armGate = p.CaptureArmRateLimitRadPerSec > 0 && ControlMath.IsFinite(p.CaptureArmRateLimitRadPerSec)
                ? p.CaptureArmRateLimitRadPerSec : def.CaptureArmRateLimitRadPerSec;
            if (ControlMath.IsFinite(p.CaptureCommitArmRateRadPerSec) && p.CaptureCommitArmRateRadPerSec > 0 &&
                p.CaptureCommitArmRateRadPerSec > armGate)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "capture_commit_arm_rate_rad_per_s={0:G6} rad/s 大于硬捕获门的 capture_arm_rate_limit_rad_per_s={1:G6} rad/s："
                    + "提交瞬间的臂速上限不能比硬门更宽。请收到 {1:G6} rad/s 以内。",
                    p.CaptureCommitArmRateRadPerSec, armGate);
                return false;
            }

            double omegaGate = p.OmegaSwitchRadPerSec > 0 && ControlMath.IsFinite(p.OmegaSwitchRadPerSec)
                ? p.OmegaSwitchRadPerSec : def.OmegaSwitchRadPerSec;
            if (ControlMath.IsFinite(p.CaptureCommitOmegaRadPerSec) && p.CaptureCommitOmegaRadPerSec > 0 &&
                p.CaptureCommitOmegaRadPerSec > omegaGate)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "capture_commit_omega_rad_per_s={0:G6} rad/s 大于硬捕获门的 omega_switch_rad_per_s={1:G6} rad/s："
                    + "提交瞬间的摆杆角速度上限不能比硬门更宽。请收到 {1:G6} rad/s 以内。",
                    p.CaptureCommitOmegaRadPerSec, omegaGate);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 参数文件读回时的同一条交叉约束：越界的提交臂速**收紧到硬门并留 note**（与其它字段的
        /// 「逐字段修复」口径一致），不整段拒载。只在硬门本身合法（&gt;0 且有限）时收紧；
        /// 硬门自己越界时交给 <see cref="SwingUpParams.IsValid"/> 整组拒收。
        /// </summary>
        private static bool ApplyCaptureHandoffGates(ref SwingUpParams p, out string note)
        {
            note = null;
            List<string> fixes = new List<string>();

            if (p.CaptureArmRateLimitRadPerSec > 0 && ControlMath.IsFinite(p.CaptureArmRateLimitRadPerSec) &&
                ControlMath.IsFinite(p.CaptureCommitArmRateRadPerSec) && p.CaptureCommitArmRateRadPerSec > 0 &&
                p.CaptureCommitArmRateRadPerSec > p.CaptureArmRateLimitRadPerSec)
            {
                fixes.Add("CaptureCommitArmRateRadPerSec=" + FormatValue(p.CaptureCommitArmRateRadPerSec) +
                          " → " + FormatValue(p.CaptureArmRateLimitRadPerSec) +
                          "（收紧到硬捕获门 CaptureArmRateLimitRadPerSec）");
                p.CaptureCommitArmRateRadPerSec = p.CaptureArmRateLimitRadPerSec;
            }

            if (p.OmegaSwitchRadPerSec > 0 && ControlMath.IsFinite(p.OmegaSwitchRadPerSec) &&
                ControlMath.IsFinite(p.CaptureCommitOmegaRadPerSec) && p.CaptureCommitOmegaRadPerSec > 0 &&
                p.CaptureCommitOmegaRadPerSec > p.OmegaSwitchRadPerSec)
            {
                fixes.Add("CaptureCommitOmegaRadPerSec=" + FormatValue(p.CaptureCommitOmegaRadPerSec) +
                          " → " + FormatValue(p.OmegaSwitchRadPerSec) +
                          "（收紧到硬捕获门 OmegaSwitchRadPerSec）");
                p.CaptureCommitOmegaRadPerSec = p.OmegaSwitchRadPerSec;
            }

            if (fixes.Count == 0)
                return false;
            note = "swingup 段的快速提交保护超出硬捕获门，已按同一约束收紧（提交保护必须是硬门的子集）："
                + string.Join("、", fixes.ToArray()) + "；只在运行期按此生效（保存时不会把补齐值强行写回文件）。";
            return true;
        }

        /// <summary>
        /// 载入时的「最小修复」：逐字段修复之后仍被组约束挡住时，只动越界的组 / 项，
        /// 绝不像旧实现那样把整段 swingup 换成出厂值。
        /// <para>
        /// 覆盖的两类真实故障：
        /// ① **组内缺键**：本版新增的交棒 / 捕获门字段在旧文件里不存在（DataContract 缺键 = 0），
        ///    而组校验只要任意一项配过就要求全组合法 —— 0 会被判越界。这里把组内为 0 的成员按
        ///    出厂默认补齐（0 的语义本来就是「未配置」，控制层运行期也按同一默认跑）。
        /// ② **交叉约束**：捕获门「|u| 取上限时所需摆臂行程」必须 &lt; 3.0 rad。单项都在各自范围里、
        ///    组合却越界时，只收紧 CaptureStrokePerModeRad（保留用户填的模态幅值上限与裕量）。
        /// </para>
        /// 返回 true = 本轮真的有修改（调用方重新走逐字段修复 / 校验）；false = 已无可修项。
        /// </summary>
        private static bool TrySalvageSwingParams(ref SwingUpParams p, out string note)
        {
            note = null;
            SwingUpParams def = SwingUpParams.CreateDefault();
            List<string> fixes = new List<string>();

            // ---- ① 组内为 0 的成员按出厂默认补齐（组已配置时才补）----
            bool captureEnvelopeConfigured = p.CaptureModeAmplitudeMaxRad != 0 || p.CaptureStrokePerModeRad != 0 ||
                                             p.CaptureStrokeMarginRad != 0 || p.CaptureAdverseRateStrokeSec != 0 ||
                                             p.CaptureArmRateLimitRadPerSec != 0 || p.CapturePendulumRateRadPerSec != 0;
            if (captureEnvelopeConfigured)
            {
                FillZeroMember("CaptureModeAmplitudeMaxRad", ref p.CaptureModeAmplitudeMaxRad, def.CaptureModeAmplitudeMaxRad, fixes);
                FillZeroMember("CaptureStrokePerModeRad", ref p.CaptureStrokePerModeRad, def.CaptureStrokePerModeRad, fixes);
                FillZeroMember("CaptureStrokeMarginRad", ref p.CaptureStrokeMarginRad, def.CaptureStrokeMarginRad, fixes);
                FillZeroMember("CaptureAdverseRateStrokeSec", ref p.CaptureAdverseRateStrokeSec, def.CaptureAdverseRateStrokeSec, fixes);
                FillZeroMember("CaptureArmRateLimitRadPerSec", ref p.CaptureArmRateLimitRadPerSec, def.CaptureArmRateLimitRadPerSec, fixes);
                FillZeroMember("CapturePendulumRateRadPerSec", ref p.CapturePendulumRateRadPerSec, def.CapturePendulumRateRadPerSec, fixes);
            }

            bool handoffConfigured = p.CapturePrepareApproachRad != 0 || p.CapturePrepareEnergyMarginRatio != 0 ||
                                     p.CaptureCommitArmRateRadPerSec != 0 || p.CaptureCommitOmegaRadPerSec != 0 ||
                                     p.CaptureCommitHoldCycles != 0 || p.SwitchHoldDecayPerCycle != 0;
            if (handoffConfigured)
            {
                FillZeroMember("CapturePrepareApproachRad", ref p.CapturePrepareApproachRad, def.CapturePrepareApproachRad, fixes);
                FillZeroMember("CapturePrepareEnergyMarginRatio", ref p.CapturePrepareEnergyMarginRatio, def.CapturePrepareEnergyMarginRatio, fixes);
                FillZeroMember("CaptureCommitArmRateRadPerSec", ref p.CaptureCommitArmRateRadPerSec, def.CaptureCommitArmRateRadPerSec, fixes);
                FillZeroMember("CaptureCommitOmegaRadPerSec", ref p.CaptureCommitOmegaRadPerSec, def.CaptureCommitOmegaRadPerSec, fixes);
                FillZeroMemberInt("CaptureCommitHoldCycles", ref p.CaptureCommitHoldCycles, def.CaptureCommitHoldCycles, fixes);
                FillZeroMemberInt("SwitchHoldDecayPerCycle", ref p.SwitchHoldDecayPerCycle, def.SwitchHoldDecayPerCycle, fixes);
            }

            bool energyShapingConfigured = p.EnergyExtractRatio != 0 || p.EnergyDeadbandRatio != 0 || p.CenterBandRad != 0;
            if (energyShapingConfigured)
            {
                FillZeroMember("EnergyExtractRatio", ref p.EnergyExtractRatio, def.EnergyExtractRatio, fixes);
                FillZeroMember("EnergyDeadbandRatio", ref p.EnergyDeadbandRatio, def.EnergyDeadbandRatio, fixes);
                FillZeroMember("CenterBandRad", ref p.CenterBandRad, def.CenterBandRad, fixes);
            }

            // 未来防呆：捕获门 / 交棒组以后新增的字段只要缺键（0）也自动按出厂默认补齐，
            // 这样「加字段忘了同步 ParamStore」不会再演变成整段回退（本次事故的直接成因）。
            FillZeroCaptureGroupMembers(ref p, def, fixes);

            // ---- ② 交叉约束：|u| 取上限时所需摆臂行程必须 < 3.0 rad ----
            // 只收「每 1 rad 模态幅值所需行程」这一项：模态幅值上限与固定裕量是现场按实测填的，
            // 动它们等于改判据；改行程系数只是把预算压回可用包线内。
            if (captureEnvelopeConfigured && ControlMath.IsFinite(p.CaptureStrokePerModeRad) &&
                ControlMath.IsFinite(p.CaptureModeAmplitudeMaxRad) && ControlMath.IsFinite(p.CaptureStrokeMarginRad) &&
                p.CaptureStrokePerModeRad > 0 && p.CaptureModeAmplitudeMaxRad > 0)
            {
                double worstStroke = p.CaptureStrokePerModeRad * p.CaptureModeAmplitudeMaxRad + p.CaptureStrokeMarginRad;
                if (worstStroke >= 3.0)
                {
                    double allowed = (2.95 - p.CaptureStrokeMarginRad) / p.CaptureModeAmplitudeMaxRad;
                    if (allowed < 1.0) allowed = 1.0;   // IsValid 的合法下界
                    if (allowed < p.CaptureStrokePerModeRad)
                    {
                        fixes.Add(string.Format(CultureInfo.InvariantCulture,
                            "CaptureStrokePerModeRad={0:G6} → {1:G6}（|u| 取上限时所需行程 {2:F2} rad 已达软限位全行程，"
                            + "只收紧行程系数；模态幅值上限 / 裕量保持文件值）",
                            p.CaptureStrokePerModeRad, allowed, worstStroke));
                        p.CaptureStrokePerModeRad = allowed;
                    }
                }
            }

            if (fixes.Count == 0)
                return false;
            note = "swingup 段组合约束越界，已只修正越界项（其余字段原样保留，不再整段回退）：" +
                   string.Join("、", fixes.ToArray()) + "；只在运行期按此生效（保存时不会把补齐值强行写回文件）。";
            return true;
        }

        /// <summary>
        /// limits 段的「最小修复」：只把越界的那一项换回出厂值，其余字段（尤其 radius_ratio、
        /// 摆臂 counts、J_eq / mgl）原样保留。旧实现一段不合法就整段回退 —— 一个 theta 字段写错
        /// 就会把现场标定的耦合比 / 软限位一起清零，属同一类数据丢失。
        /// </summary>
        private static bool TrySalvageLimits(ref SafetyLimits s, out string note)
        {
            note = null;
            SafetyLimits def = SafetyLimits.CreateDefault();
            List<string> fixes = new List<string>();

            if (!ControlMath.IsFinite(s.ThetaTripRad) || s.ThetaTripRad <= 0)
            {
                fixes.Add("ThetaTripRad=" + FormatValue(s.ThetaTripRad) + " → " + FormatValue(def.ThetaTripRad));
                s.ThetaTripRad = def.ThetaTripRad;
            }
            if (!ControlMath.IsFinite(s.ThetaLostRad) || s.ThetaLostRad <= 0)
            {
                fixes.Add("ThetaLostRad=" + FormatValue(s.ThetaLostRad) + " → " + FormatValue(def.ThetaLostRad));
                s.ThetaLostRad = def.ThetaLostRad;
            }
            if (s.ThetaLostRad > s.ThetaTripRad)
            {
                // 软限先于硬限才有意义：只把软限收到硬限以内，不动硬限（硬限通常按机构包线填）。
                double capped = Math.Min(def.ThetaLostRad, s.ThetaTripRad);
                fixes.Add("ThetaLostRad=" + FormatValue(s.ThetaLostRad) + " → " + FormatValue(capped) +
                          "（不得大于 ThetaTripRad）");
                s.ThetaLostRad = capped;
            }
            if (!ControlMath.IsFinite(s.PhiErrMaxRad) || s.PhiErrMaxRad <= 0)
            {
                fixes.Add("PhiErrMaxRad=" + FormatValue(s.PhiErrMaxRad) + " → " + FormatValue(def.PhiErrMaxRad));
                s.PhiErrMaxRad = def.PhiErrMaxRad;
            }
            if (!s.ArmLimitsValid)
            {
                // 左右限位一对数没法从单个值推回来：只能成对回到「尚未采集」占位。
                fixes.Add("ArmMinCount/ArmMaxCount=[" + s.ArmMinCount + "," + s.ArmMaxCount +
                          "] → 尚未采集占位（min ≥ max，无法从单项修复）");
                s.ArmMinCount = def.ArmMinCount;
                s.ArmMaxCount = def.ArmMaxCount;
            }
            if (!ControlMath.IsFinite(s.OmegaArmMaxRadPerSec) || s.OmegaArmMaxRadPerSec <= 0)
            {
                fixes.Add("OmegaArmMaxRadPerSec=" + FormatValue(s.OmegaArmMaxRadPerSec) + " → " +
                          FormatValue(def.OmegaArmMaxRadPerSec));
                s.OmegaArmMaxRadPerSec = def.OmegaArmMaxRadPerSec;
            }
            if (!ControlMath.IsFinite(s.JEq) || s.JEq <= 0)
            {
                fixes.Add("JEq=" + FormatValue(s.JEq) + " → " + FormatValue(def.JEq));
                s.JEq = def.JEq;
            }
            if (!ControlMath.IsFinite(s.Mgl) || s.Mgl <= 0)
            {
                fixes.Add("Mgl=" + FormatValue(s.Mgl) + " → " + FormatValue(def.Mgl));
                s.Mgl = def.Mgl;
            }
            if (!ControlMath.IsFinite(s.RadiusRatio) || s.RadiusRatio < 0 ||
                s.RadiusRatio > SafetyLimits.MaxReasonableRadiusRatio)
            {
                fixes.Add("RadiusRatio=" + FormatValue(s.RadiusRatio) + " → " + FormatValue(def.RadiusRatio) +
                          "（超出 [0," + FormatValue(SafetyLimits.MaxReasonableRadiusRatio) + "]）");
                s.RadiusRatio = def.RadiusRatio;
            }

            if (fixes.Count == 0)
                return false;
            note = string.Join("、", fixes.ToArray()) +
                   "；只在运行期按此生效（保存时是否写回取决于入口：带载入溯源的 GUI / 增量保存保留文件原写法，CLI save-config 按当前生效值重写）。";
            return true;
        }

        /// <summary>组内为 0（= 旧文件缺这个键）的成员按出厂默认补齐，并记一条修改说明。</summary>
        private static void FillZeroMember(string name, ref double field, double fallback, List<string> fixes)
        {
            if (field != 0 && ControlMath.IsFinite(field))
                return;
            if (field == 0)
            {
                field = fallback;
                fixes.Add(name + "=0 → " + FormatValue(fallback) + "（旧文件缺键 / 组内未配置）");
            }
        }

        /// <summary>组内为 0 的整型成员按出厂默认补齐。</summary>
        private static void FillZeroMemberInt(string name, ref int field, int fallback, List<string> fixes)
        {
            if (field != 0)
                return;
            field = fallback;
            fixes.Add(name + "=0 → " + fallback.ToString(CultureInfo.InvariantCulture) + "（旧文件缺键 / 组内未配置）");
        }

        /// <summary>
        /// 未来防呆：捕获门 / 交棒组（字段名以 Capture* / SwitchHold* 开头）里任何为 0 的成员，
        /// 只要组实际已配置，就按 <see cref="SwingUpParams.CreateDefault"/> 补齐。
        /// <para>
        /// 为什么用反射而不是逐项列：这组字段「配了任意一项就整组校验」，新增一个字段而载入层
        /// 忘了补 0，整组 IsValid 就会失败 —— 历史实现遇到组失败会整段回退（Ke 700→60 的来源）。
        /// 用命名约定兜底，新增字段不再依赖「记得同步 ParamStore」这一人工步骤。
        /// 0 在这组里的语义统一是「未配置」（控制层运行期同样按默认补齐），不会覆盖用户的合法值。
        /// </para>
        /// </summary>
        private static void FillZeroCaptureGroupMembers(ref SwingUpParams p, SwingUpParams def, List<string> fixes)
        {
            FieldInfo[] fields = typeof(SwingUpParams).GetFields(BindingFlags.Instance | BindingFlags.Public);
            bool groupConfigured = false;
            for (int i = 0; i < fields.Length; i++)
            {
                if (!IsCaptureGroupField(fields[i].Name)) continue;
                object value = fields[i].GetValue(p);
                if (value is double && (double)value != 0) { groupConfigured = true; break; }
                if (value is int && (int)value != 0) { groupConfigured = true; break; }
            }
            if (!groupConfigured)
                return;

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!IsCaptureGroupField(field.Name)) continue;
                object current = field.GetValue(p);
                object fallback = field.GetValue(def);
                if (current is double)
                {
                    if ((double)current != 0 || (double)fallback == 0)
                        continue;
                    field.SetValue(p, fallback);
                    fixes.Add(field.Name + "=0 → " + FormatValue((double)fallback) +
                              "（旧文件缺键 / 捕获门组内未配置）");
                }
                else if (current is int)
                {
                    if ((int)current != 0 || (int)fallback == 0)
                        continue;
                    field.SetValue(p, fallback);
                    fixes.Add(field.Name + "=0 → " + ((int)fallback).ToString(CultureInfo.InvariantCulture) +
                              "（旧文件缺键 / 捕获门组内未配置）");
                }
            }
        }

        /// <summary>字段是否属于「捕获门 / 交棒」组（按命名约定：Capture* / SwitchHold*）。</summary>
        private static bool IsCaptureGroupField(string name)
        {
            return name.StartsWith("Capture", StringComparison.Ordinal) ||
                   name.StartsWith("SwitchHold", StringComparison.Ordinal);
        }

        /// <summary>提示里的数值统一 G6 + invariant，避免中文区域出现逗号小数点。</summary>
        private static string FormatValue(double value)
        {
            return value.ToString("G6", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把一段参数序列化成一行 JSON 文本，供加载失败时在日志里写清「哪个字段、什么值」。
        /// 走和参数文件同一个序列化器，键名与文件一致；诊断本身失败只留一句说明，不再抛。
        /// </summary>
        private static string DescribeSection(object section)
        {
            if (section == null)
                return "(缺失)";
            try
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(section.GetType());
                    serializer.WriteObject(stream, section);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception ex)
            {
                return "(段内容读出失败：" + DiagText.Describe(ex) + ")";
            }
        }

        /// <summary>列出文件里实际出现的段名，用于整份拒载时说明哪些内容被丢弃。</summary>
        private static string DescribePresentSections(ParamDocument doc)
        {
            List<string> present = new List<string>();
            if (doc.Pid != null) present.Add("pid");
            if (doc.Swing != null) present.Add("swingup");
            if (doc.Limits != null) present.Add("limits");
            if (doc.Pump != null) present.Add("pump");
            if (doc.Identify != null) present.Add("identify");
            if (doc.SoftLimits != null) present.Add("soft_limits");
            if (doc.Calibration != null) present.Add("calibration");
            if (doc.Captures != null && doc.Captures.Count > 0)
                present.Add("captures(" + doc.Captures.Count.ToString(CultureInfo.InvariantCulture) + " 条)");
            return present.Count == 0 ? "无可识别段" : string.Join("、", present.ToArray());
        }

        /// <summary>
        /// 读三组参数但不做 DENI 比对（启动期 --config 载入用，那时连接还没建，没有可比的当前路径）。
        /// </summary>
        public static bool TryLoad(string path, out PidGains pid, out SwingUpParams swing, out SafetyLimits limits, out string error)
        {
            string deniPath;
            string deniNote;
            return TryLoad(path, null, out pid, out swing, out limits, out deniPath, out deniNote, out error);
        }

        /// <summary>
        /// 读参数文件并归一成「无 BOM 的 UTF-8 字节」：
        ///   - UTF-16 LE / BE BOM：按对应编码解码后再转 UTF-8；
        ///   - UTF-8 BOM：可以出现多次（被脚本 / 编辑器重复写过），逐个剥掉；
        ///   - 前导空白：一起剥掉（手改文件常见）。
        /// 实测故障原文：`反序列化对象 ... 时出现错误。遇到意外字符“ï”` —— 就是 BOM 首字节 0xEF 被当正文。
        /// </summary>
        private static byte[] ReadJsonPayload(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length >= 2 &&
                ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)))
            {
                Encoding encoding = raw[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
                string text = encoding.GetString(raw, 2, raw.Length - 2);
                text = text.TrimStart('\uFEFF');
                return new UTF8Encoding(false).GetBytes(text);
            }

            int offset = 0;
            while (offset + 2 < raw.Length &&
                   raw[offset] == 0xEF && raw[offset + 1] == 0xBB && raw[offset + 2] == 0xBF)
            {
                offset += 3;
            }
            while (offset < raw.Length &&
                   (raw[offset] == 0x20 || raw[offset] == 0x09 || raw[offset] == 0x0A || raw[offset] == 0x0D))
            {
                offset++;
            }
            byte[] normalized = new byte[raw.Length - offset];
            Buffer.BlockCopy(raw, offset, normalized, 0, normalized.Length);
            return normalized;
        }

        /// <summary>
        /// 文件里记的 DENI 与当前生效的不是同一个才给提示。两边任一为空就不吭声（没得比，不硬报）。
        /// 只是溯源提醒，不改配置、不切拓扑。
        /// </summary>
        private static string DeniPathNote(string savedDeniPath, string currentDeniPath)
        {
            if (string.IsNullOrEmpty(savedDeniPath) || string.IsNullOrEmpty(currentDeniPath))
                return null;
            if (string.Equals(savedDeniPath, currentDeniPath, StringComparison.OrdinalIgnoreCase))
                return null;
            return string.Format(
                "参数文件记的 DENI 是 {0}，当前生效的是 {1}：这是上次整定的溯源记录，本次仍按当前 DENI 跑，未切换拓扑。",
                savedDeniPath, currentDeniPath);
        }

        /// <summary>
        /// 只读文件里的可选段（保存时用来保留旧段）。走完整 TryLoad，必需段不合法时返回 false，
        /// 由调用方决定是否继续保存主参数；读不出就如实留痕，不假装旧段是空的。
        /// </summary>
        private static bool TryReadExtrasOnly(string path, out PersistedExtras extras, out string note)
        {
            extras = null;
            note = null;
            if (string.IsNullOrEmpty(path))
            {
                note = "参数文件路径为空：读不到可选段（标定 / 捕获 / 泵 / 辨识）。确认 --config 或默认参数文件路径后再试。";
                return false;
            }
            if (!File.Exists(path))
            {
                note = "参数文件不存在：读不到可选段（标定 / 捕获 / 泵 / 辨识）。先 save-config 生成文件：" + path;
                return false;
            }
            PidGains pid;
            SwingUpParams swing;
            SafetyLimits limits;
            string deniPath;
            string error;
            bool ok = TryLoad(path, null, out pid, out swing, out limits, out extras,
                out deniPath, out note, out error);
            if (!ok)
            {
                note = error;
                extras = null;
            }
            else if (extras != null)
            {
                // 本入口只服务「增量标定落盘」（zero / normalize / recenter / calibrate-radius）：
                // 调用方的 pid / swing / limits 可能根本是引擎出厂值（CLI 没带 --config），
                // 保存时不允许拿它们覆盖文件里的主参数段。见 ParamStore.Save 的增量保护。
                extras.IncrementalRead = true;
            }
            return ok;

        }

        /// <summary>
        /// 只读参数文件的可选段（标定 / 捕获 / 泵 / 辨识 / 软限位），供 CLI 在 zero 后做增量落盘：
        /// 保留文件里原有的其它段，只把校准零点换成当前生效值。读不出时返回 false 并给 note，
        /// 调用方据此决定是「放弃自动保存并明确报原因」而不是写一份丢掉旧段的文件。
        /// </summary>
        public static bool TryReadExtras(string path, out PersistedExtras extras, out string note)
        {
            return TryReadExtrasOnly(path, out extras, out note);
        }

        /// <summary>
        /// 合并可选段：新传入的非空段覆盖旧段，缺的段保留旧段，捕获列表优先用新传入的。
        /// 两边都没有 = null（文件里不写可选段）。
        /// </summary>
        private static PersistedExtras MergeExtras(PersistedExtras existing, PersistedExtras incoming)
        {
            if (incoming == null && (existing == null || !existing.SectionsPresent))
            {
                // 旧格式文件 + 调用方不传可选段（CLI save-config）：保持旧文件原样，不凭空补写默认段。
                return null;
            }
            PersistedExtras merged = new PersistedExtras();
            merged.Pump = incoming != null && incoming.Pump != null
                ? incoming.Pump
                : (existing != null ? existing.Pump : null);
            merged.Identify = incoming != null && incoming.Identify != null
                ? incoming.Identify
                : (existing != null ? existing.Identify : null);
            merged.SoftLimits = incoming != null && incoming.SoftLimits != null
                ? incoming.SoftLimits
                : (existing != null ? existing.SoftLimits : null);
            merged.Calibration = incoming != null && incoming.Calibration != null
                ? incoming.Calibration
                : (existing != null ? existing.Calibration : null);
            if (incoming != null && incoming.Captures != null)
                merged.Captures = new List<CaptureRecord>(incoming.Captures);
            else if (existing != null && existing.Captures != null)
                merged.Captures = new List<CaptureRecord>(existing.Captures);
            else
                merged.Captures = new List<CaptureRecord>();
            merged.ClampCaptures();
            return merged;
        }

        /// <summary>UTC 时间戳统一走 ISO-8601（"o" 往返格式），不用 /Date(ms)/ 这种难读格式。</summary>
        private static string FormatTimestamp(DateTime? value)
        {
            if (!value.HasValue) return null;
            return value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime? ParseTimestamp(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                return parsed;
            return null;
        }

        // ==================== 保存溯源 / 字段级合并 ====================

        /// <summary>
        /// 一次载入的溯源快照：来源路径、原始 JSON 文本、各段**载入后实际语义值**。
        /// <para>
        /// 保存时用它判断「这个字段用户到底改没改」：
        /// 新值与载入后语义值相同 = 用户没改 → 保留文件里原来的写法（包括缺键、旧值、旧格式），
        /// 绝不因为载入时补过默认值就整段重写；只有语义值确实变了才覆盖那一个键。
        /// 这样「缺键补默认」只影响运行期内存，不会反向污染用户文件。
        /// </para>
        /// </summary>
        internal sealed class SaveProvenance
        {
            internal string _sourcePath;
            internal string _rawJson;
            internal PidDoc _pid;
            internal SwingDoc _swing;
            internal LimitsDoc _limits;
            internal PumpDoc _pump;
            internal IdentifyDoc _identify;
            internal SoftLimitDoc _softLimits;
            internal CalibrationDoc _calibration;
            internal List<CaptureDoc> _captures;
            internal string _deniPath;

            private SaveProvenance()
            {
            }

            internal static SaveProvenance Capture(string path, string rawJson, PidGains pid, SwingUpParams swing,
                SafetyLimits limits, PersistedExtras extras, string deniPath)
            {
                SaveProvenance p = new SaveProvenance();
                p._sourcePath = path;
                p._rawJson = rawJson;
                p._pid = PidDoc.From(pid);
                p._swing = SwingDoc.From(swing);
                p._limits = LimitsDoc.From(limits);
                p._deniPath = deniPath;
                if (extras != null)
                {
                    p._pump = extras.Pump != null ? PumpDoc.From(extras.Pump) : null;
                    p._identify = extras.Identify != null ? IdentifyDoc.From(extras.Identify) : null;
                    p._softLimits = extras.SoftLimits != null ? SoftLimitDoc.From(extras.SoftLimits) : null;
                    p._calibration = extras.Calibration != null ? CalibrationDoc.From(extras.Calibration) : null;
                    p._captures = extras.Captures != null ? CaptureDoc.FromList(extras.Captures) : null;
                }
                return p;
            }

            /// <summary>快照是否来自同一个目标文件（全路径、忽略大小写）。</summary>
            internal bool MatchesPath(string path)
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(_sourcePath))
                    return false;
                try
                {
                    return string.Equals(Path.GetFullPath(path), Path.GetFullPath(_sourcePath),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("参数溯源路径比对失败：" + DiagText.Describe(ex));
                    return false;
                }
            }
        }

        /// <summary>Doc 类的一个 [DataMember] 字段（键名 / 字段反射）。</summary>
        private sealed class DocMember
        {
            public string Key;
            public FieldInfo Field;
        }

        private static readonly object DocMemberGate = new object();
        private static readonly Dictionary<Type, DocMember[]> DocMemberCache = new Dictionary<Type, DocMember[]>();

        private static DocMember[] GetDocMembers(Type docType)
        {
            lock (DocMemberGate)
            {
                DocMember[] cached;
                if (DocMemberCache.TryGetValue(docType, out cached))
                    return cached;
                List<DocMember> list = new List<DocMember>();
                FieldInfo[] fields = docType.GetFields(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < fields.Length; i++)
                {
                    object[] attrs = fields[i].GetCustomAttributes(typeof(DataMemberAttribute), true);
                    if (attrs == null || attrs.Length == 0)
                        continue;
                    DataMemberAttribute attr = (DataMemberAttribute)attrs[0];
                    DocMember m = new DocMember();
                    m.Key = string.IsNullOrEmpty(attr.Name) ? fields[i].Name : attr.Name;
                    m.Field = fields[i];
                    list.Add(m);
                }
                // 按 Order 稳定排序（没有 Order 的排后面）；保证缺键补齐 / 新键追加顺序与文件契约一致。
                list.Sort(delegate (DocMember a, DocMember b)
                {
                    DataMemberAttribute da = (DataMemberAttribute)a.Field.GetCustomAttributes(
                        typeof(DataMemberAttribute), true)[0];
                    DataMemberAttribute db = (DataMemberAttribute)b.Field.GetCustomAttributes(
                        typeof(DataMemberAttribute), true)[0];
                    int orderA = da.Order >= 0 ? da.Order : int.MaxValue;
                    int orderB = db.Order >= 0 ? db.Order : int.MaxValue;
                    return orderA.CompareTo(orderB);
                });
                cached = list.ToArray();
                DocMemberCache[docType] = cached;
                return cached;
            }
        }

        /// <summary>
        /// 生成最终写盘文本：
        /// ① 有同文件溯源 → 与旧 JSON 做字段级合并，只覆盖语义值真的变了的键；
        ///    unknown 键原样保留，明确退场的 drift 配置键在下一次保存剔除；
        /// ② 无溯源但文件存在（例如 CLI save-config）→ 保留旧文件里的未知键，已知键按本次值覆盖；
        /// ③ 都没有 → 直接写本次文档。
        /// 增量标定（zero / normalize / …）额外拒绝「用出厂默认值覆盖文件里的非默认值」，
        /// 防止 CLI 未载入 --config 时把用户的 Ke / radius_ratio / 软限位冲成出厂值。
        /// </summary>
        private static string BuildDocumentText(string target, ParamDocument doc, PersistedExtras extras)
        {
            string compact = SerializeCompact(doc);
            SaveProvenance prov = extras != null ? extras.Provenance : null;
            bool incremental = extras != null && extras.IncrementalRead;
            try
            {
                if (prov != null && prov.MatchesPath(target) && !string.IsNullOrEmpty(prov._rawJson))
                    return MergeDocument(prov._rawJson, compact, doc, prov, incremental);
                if (File.Exists(target))
                {
                    string existing = File.ReadAllText(target, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(existing))
                        return MergeDocument(existing, compact, doc, null, false);
                }
                return JsonDom.Format(JsonDom.Parse(compact));
            }
            catch (Exception ex)
            {
                // 合并失败绝不阻断保存：退回完整文档写盘（字段值仍以本次调用为准）。
                System.Diagnostics.Debug.WriteLine("参数保存合并旧文件失败，按完整文档写出：" + DiagText.Describe(ex));
                return JsonDom.Format(JsonDom.Parse(compact));
            }
        }

        private static string SerializeCompact(object value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(value.GetType());
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>把本次文档合并到旧 JSON 文本上：只改语义值变化的键，旧键 / 未知键保留（明确退场键除外）。</summary>
        private static string MergeDocument(string oldJson, string compactNew, ParamDocument doc,
            SaveProvenance prov, bool incremental)
        {
            JsonObjectNode oldRoot = JsonDom.Parse(oldJson) as JsonObjectNode;
            JsonObjectNode newRoot = JsonDom.Parse(compactNew) as JsonObjectNode;
            if (oldRoot == null || newRoot == null)
                return JsonDom.Format(JsonDom.Parse(compactNew));

            MergeSection(oldRoot, newRoot, "pid", typeof(PidDoc), doc.Pid,
                prov != null ? prov._pid : null, PidDoc.From(PidGains.CreateDefault()), incremental);
            MergeSection(oldRoot, newRoot, "swingup", typeof(SwingDoc), doc.Swing,
                prov != null ? prov._swing : null, SwingDoc.From(SwingUpParams.CreateDefault()), incremental);
            MergeSection(oldRoot, newRoot, "limits", typeof(LimitsDoc), doc.Limits,
                prov != null ? prov._limits : null, LimitsDoc.From(SafetyLimits.CreateDefault()), incremental);
            MergeSection(oldRoot, newRoot, "pump", typeof(PumpDoc), doc.Pump,
                prov != null ? prov._pump : null, PumpDoc.From(PumpParameters.CreateDefault()), incremental);
            MergeSection(oldRoot, newRoot, "identify", typeof(IdentifyDoc), doc.Identify,
                prov != null ? prov._identify : null, null, false);
            MergeSection(oldRoot, newRoot, "soft_limits", typeof(SoftLimitDoc), doc.SoftLimits,
                prov != null ? prov._softLimits : null, SoftLimitDoc.From(SoftLimitParameters.CreateDefault()), incremental);
            // 标定段是增量保存的唯一目标：不做出厂值保护（引擎/文件实际值该写就写），
            // 但「新值与载入值相同」的字段仍保留旧写法。
            MergeSection(oldRoot, newRoot, "calibration", typeof(CalibrationDoc), doc.Calibration,
                prov != null ? prov._calibration : null, null, false);

            // captures：整组替换。载入后未变 → 保留旧数组原文（含坏条目原样，不静默删除用户数据）。
            JsonNode newCaptures;
            if (newRoot.TryGet("captures", out newCaptures))
            {
                bool unchanged = prov != null && ListsEqual(doc.Captures, prov._captures);
                if (!unchanged)
                    oldRoot.Set("captures", newCaptures.Clone());
            }

            // deniPath：未变则保留旧写法；变了（含清空）按新值写。
            MergeScalarMember(oldRoot, newRoot, "deniPath",
                doc.DeniPath, prov != null ? prov._deniPath : null, prov != null);

            // format / unit_convention / saved_at：版本标记与时间戳始终以本次为准。
            JsonNode versionNode;
            if (newRoot.TryGet("format", out versionNode)) oldRoot.Set("format", versionNode.Clone());
            if (newRoot.TryGet("unit_convention", out versionNode)) oldRoot.Set("unit_convention", versionNode.Clone());
            JsonNode savedAtNode;
            if (newRoot.TryGet("saved_at", out savedAtNode)) oldRoot.Set("saved_at", savedAtNode.Clone());

            string newline = oldJson.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string formatted = JsonDom.Format(oldRoot, newline);
            // 旧文件末尾有换行的，合并结果也保留同样结尾（不因一次保存改掉文件收尾习惯）。
            if (oldJson.EndsWith("\n", StringComparison.Ordinal))
                formatted += newline;
            return formatted;
        }

        /// <summary>逐字段合并一段。旧段里的未知键不动（明确退场键除外）；只有语义值变了的已知键才覆盖。</summary>
        private static void MergeSection(JsonObjectNode oldRoot, JsonObjectNode newRoot, string sectionName,
            Type docType, object newDocObj, object loadedDocObj, object defaultDocObj, bool incremental)
        {
            JsonObjectNode newSection = newRoot.GetObject(sectionName);
            if (newDocObj == null || newSection == null)
                return; // 本次不写这一段：旧段原样保留（Save 明确要清空时由调用方传 null 文档）
            JsonObjectNode oldSection = oldRoot.GetObject(sectionName);
            if (oldSection == null)
            {
                oldRoot.Set(sectionName, newSection.Clone());
                return;
            }

            DocMember[] members = GetDocMembers(docType);
            for (int i = 0; i < members.Length; i++)
            {
                DocMember m = members[i];
                JsonNode newNode;
                JsonNode oldNode;
                bool newHas = newSection.TryGet(m.Key, out newNode);
                bool oldHas = oldSection.TryGet(m.Key, out oldNode);
                object newValue = m.Field.GetValue(newDocObj);
                object loadedValue = loadedDocObj != null ? m.Field.GetValue(loadedDocObj) : null;
                bool sameAsLoaded = loadedDocObj != null && ValuesEqual(newValue, loadedValue);
                bool sameAsFactory = incremental && defaultDocObj != null &&
                                     ValuesEqual(newValue, m.Field.GetValue(defaultDocObj));

                if (!newHas)
                {
                    // 新文档没写这个键（EmitDefaultValue=false 的 null 字符串等）：
                    // 未改动 / 出厂默认 → 保留旧写法；真的改成空 → 删键。
                    if (!oldHas) continue;
                    if (sameAsLoaded || sameAsFactory) continue;
                    oldSection.Remove(m.Key);
                    continue;
                }

                if (oldHas)
                {
                    if (sameAsLoaded) continue;   // 用户没改：保留旧值（哪怕是旧写法的 0 / 缺省值）
                    if (sameAsFactory) continue;  // 增量保存：绝不用出厂默认覆盖文件里的非默认用户值
                    oldSection.Set(m.Key, newNode.Clone());
                    continue;
                }

                // 旧文件没有这个键：用户没改过（= 载入时补的默认值）就保持缺键；
                // 只有真的填了新值才新增，避免「载入即补键」反向污染用户文件。
                if (sameAsLoaded) continue;
                if (sameAsFactory && loadedDocObj != null) continue;
                oldSection.Set(m.Key, newNode.Clone());
            }

            // 已退场的 drift 配置不属于「未知键原样保留」：旧文件读入时忽略，下一次保存从旧段剔除。
            for (int i = oldSection.Members.Count - 1; i >= 0; i--)
            {
                if (IsRetiredSwingMember(sectionName, oldSection.Members[i].Key))
                    oldSection.Members.RemoveAt(i);
            }
        }

        private static bool IsRetiredSwingMember(string sectionName, string key)
        {
            return string.Equals(sectionName, "swingup", StringComparison.Ordinal) &&
                   (string.Equals(key, "drift_gain_rad_per_s2_per_rad_s", StringComparison.Ordinal) ||
                    string.Equals(key, "drift_auth_max_rad_per_s2", StringComparison.Ordinal));
        }

        /// <summary>顶层标量键（deniPath 等）的「未变保留旧写法」合并。</summary>
        private static void MergeScalarMember(JsonObjectNode oldRoot, JsonObjectNode newRoot, string key,
            object newValue, object loadedValue, bool hasProvenance)
        {
            JsonNode newNode;
            bool newHas = newRoot.TryGet(key, out newNode);
            JsonNode oldNode;
            bool oldHas = oldRoot.TryGet(key, out oldNode);
            bool sameAsLoaded = hasProvenance && ValuesEqual(newValue, loadedValue);
            if (!newHas)
            {
                if (!oldHas || sameAsLoaded) return;
                oldRoot.Remove(key);
                return;
            }
            if (sameAsLoaded && oldHas) return;
            if (sameAsLoaded) return; // 旧文件缺键、载入也没值：保持缺键
            oldRoot.Set(key, newNode.Clone());
        }

        /// <summary>两个值语义是否相同。double 用 Equals（NaN 与 NaN 视为相同）；列表走规范化 JSON。</summary>
        private static bool ValuesEqual(object a, object b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            Type ta = a.GetType();
            if (ta != b.GetType())
                return false;
            if (a is double)
                return double.Equals((double)a, (double)b);
            if (a is int || a is bool || a is string)
                return a.Equals(b);
            return string.Equals(SerializeCompact(a), SerializeCompact(b), StringComparison.Ordinal);
        }

        private static bool ListsEqual(List<CaptureDoc> a, List<CaptureDoc> b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ValuesEqual(a[i], b[i])) return false;
            return true;
        }

        /// <summary>
        /// 列出文件里本版不认识的键（已删除字段 / 更新版本写的键）。
        /// 只做提示：这些键既不影响控制，也不会被保存丢掉（写盘时按旧 JSON 原样保留）。
        /// </summary>
        private static string DescribeUnknownKeys(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
                return null;
            JsonObjectNode root;
            try
            {
                root = JsonDom.Parse(rawJson) as JsonObjectNode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("参数文件未知键扫描失败：" + DiagText.Describe(ex));
                return null;
            }
            if (root == null)
                return null;

            List<string> unknown = new List<string>();
            CollectUnknownKeys(root, "pid", typeof(PidDoc), unknown);
            CollectUnknownKeys(root, "swingup", typeof(SwingDoc), unknown);
            CollectUnknownKeys(root, "limits", typeof(LimitsDoc), unknown);
            CollectUnknownKeys(root, "pump", typeof(PumpDoc), unknown);
            CollectUnknownKeys(root, "identify", typeof(IdentifyDoc), unknown);
            CollectUnknownKeys(root, "soft_limits", typeof(SoftLimitDoc), unknown);
            CollectUnknownKeys(root, "calibration", typeof(CalibrationDoc), unknown);

            List<string> knownTop = new List<string>();
            knownTop.Add("format");
            knownTop.Add("unit_convention");
            knownTop.Add("deniPath");
            knownTop.Add("pid");
            knownTop.Add("swingup");
            knownTop.Add("limits");
            knownTop.Add("pump");
            knownTop.Add("identify");
            knownTop.Add("soft_limits");
            knownTop.Add("calibration");
            knownTop.Add("captures");
            knownTop.Add("saved_at");
            for (int i = 0; i < root.Members.Count; i++)
            {
                if (!knownTop.Contains(root.Members[i].Key))
                    unknown.Add(root.Members[i].Key + "（顶层未知段）");
            }

            if (unknown.Count == 0)
                return null;
            return "参数文件含本版已删除 / 不认识的键，已忽略、不参与控制，保存时原样保留：" +
                   string.Join("、", unknown.ToArray()) + "。";
        }

        private static void CollectUnknownKeys(JsonObjectNode root, string sectionName, Type docType, List<string> unknown)
        {
            JsonObjectNode section = root.GetObject(sectionName);
            if (section == null)
                return;
            DocMember[] members = GetDocMembers(docType);
            List<string> known = new List<string>(members.Length);
            for (int i = 0; i < members.Length; i++)
                known.Add(members[i].Key);
            for (int i = 0; i < section.Members.Count; i++)
            {
                string key = section.Members[i].Key;
                if (!known.Contains(key) && !IsRetiredSwingMember(sectionName, key))
                    unknown.Add(sectionName + "." + key);
            }
        }

        // ==================== 极简 JSON DOM（保序、保留原始 token 写法） ====================

        /// <summary>
        /// 只服务参数文件合并的 JSON DOM：对象保序、标量保留原始 token（数字写法 / 转义都不变），
        /// 这样「没改的字段」可以逐字节沿用旧文件，而不是被反序列化再序列化一遍。
        /// </summary>
        private abstract class JsonNode
        {
            public abstract JsonNode Clone();
        }

        private sealed class JsonScalarNode : JsonNode
        {
            public string Raw;

            public override JsonNode Clone()
            {
                JsonScalarNode copy = new JsonScalarNode();
                copy.Raw = Raw;
                return copy;
            }
        }

        private sealed class JsonMemberNode
        {
            public string Key;
            public JsonNode Value;
        }

        private sealed class JsonObjectNode : JsonNode
        {
            public readonly List<JsonMemberNode> Members = new List<JsonMemberNode>();

            public override JsonNode Clone()
            {
                JsonObjectNode copy = new JsonObjectNode();
                for (int i = 0; i < Members.Count; i++)
                {
                    JsonMemberNode m = new JsonMemberNode();
                    m.Key = Members[i].Key;
                    m.Value = Members[i].Value.Clone();
                    copy.Members.Add(m);
                }
                return copy;
            }

            public bool TryGet(string key, out JsonNode value)
            {
                for (int i = 0; i < Members.Count; i++)
                {
                    if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
                    {
                        value = Members[i].Value;
                        return true;
                    }
                }
                value = null;
                return false;
            }

            public JsonObjectNode GetObject(string key)
            {
                JsonNode node;
                return TryGet(key, out node) ? node as JsonObjectNode : null;
            }

            public void Set(string key, JsonNode value)
            {
                for (int i = 0; i < Members.Count; i++)
                {
                    if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
                    {
                        Members[i].Value = value;
                        return;
                    }
                }
                JsonMemberNode m = new JsonMemberNode();
                m.Key = key;
                m.Value = value;
                Members.Add(m);
            }

            public void Remove(string key)
            {
                for (int i = 0; i < Members.Count; i++)
                {
                    if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
                    {
                        Members.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        private sealed class JsonArrayNode : JsonNode
        {
            public readonly List<JsonNode> Items = new List<JsonNode>();

            public override JsonNode Clone()
            {
                JsonArrayNode copy = new JsonArrayNode();
                for (int i = 0; i < Items.Count; i++)
                    copy.Items.Add(Items[i].Clone());
                return copy;
            }
        }

        private static class JsonDom
        {
            public static JsonNode Parse(string text)
            {
                int index = 0;
                JsonNode node = ParseValue(text, ref index);
                SkipWhite(text, ref index);
                return node;
            }

            public static string Format(JsonNode node)
            {
                return Format(node, "\r\n");
            }

            /// <summary>按指定换行风格输出：旧文件用 LF 保存的，合并后仍用 LF，不改用户的文件风格。</summary>
            public static string Format(JsonNode node, string newline)
            {
                StringBuilder sb = new StringBuilder();
                WriteValue(node, sb, 0, string.IsNullOrEmpty(newline) ? "\r\n" : newline);
                return sb.ToString();
            }

            private static JsonNode ParseValue(string text, ref int index)
            {
                SkipWhite(text, ref index);
                if (index >= text.Length)
                    throw new FormatException("JSON 提前结束");
                char c = text[index];
                if (c == '{') return ParseObject(text, ref index);
                if (c == '[') return ParseArray(text, ref index);
                if (c == '"')
                {
                    int start = index;
                    ScanString(text, ref index);
                    JsonScalarNode s = new JsonScalarNode();
                    s.Raw = text.Substring(start, index - start);
                    return s;
                }
                int tokenStart = index;
                while (index < text.Length && text[index] != ',' && text[index] != '}' && text[index] != ']' &&
                       !char.IsWhiteSpace(text[index]))
                    index++;
                JsonScalarNode scalar = new JsonScalarNode();
                scalar.Raw = text.Substring(tokenStart, index - tokenStart);
                return scalar;
            }

            private static JsonObjectNode ParseObject(string text, ref int index)
            {
                JsonObjectNode obj = new JsonObjectNode();
                index++; // {
                SkipWhite(text, ref index);
                if (index < text.Length && text[index] == '}')
                {
                    index++;
                    return obj;
                }
                while (true)
                {
                    SkipWhite(text, ref index);
                    int keyStart = index;
                    ScanString(text, ref index);
                    JsonMemberNode m = new JsonMemberNode();
                    m.Key = UnescapeString(text.Substring(keyStart, index - keyStart));
                    SkipWhite(text, ref index);
                    if (index >= text.Length || text[index] != ':')
                        throw new FormatException("缺少 ':' @" + index);
                    index++;
                    m.Value = ParseValue(text, ref index);
                    obj.Members.Add(m);
                    SkipWhite(text, ref index);
                    if (index < text.Length && text[index] == ',') { index++; continue; }
                    if (index < text.Length && text[index] == '}') { index++; return obj; }
                    throw new FormatException("对象格式错误 @" + index);
                }
            }

            private static JsonArrayNode ParseArray(string text, ref int index)
            {
                JsonArrayNode arr = new JsonArrayNode();
                index++; // [
                SkipWhite(text, ref index);
                if (index < text.Length && text[index] == ']')
                {
                    index++;
                    return arr;
                }
                while (true)
                {
                    arr.Items.Add(ParseValue(text, ref index));
                    SkipWhite(text, ref index);
                    if (index < text.Length && text[index] == ',') { index++; continue; }
                    if (index < text.Length && text[index] == ']') { index++; return arr; }
                    throw new FormatException("数组格式错误 @" + index);
                }
            }

            private static void ScanString(string text, ref int index)
            {
                if (text[index] != '"')
                    throw new FormatException("缺少字符串引号 @" + index);
                index++;
                while (index < text.Length)
                {
                    char c = text[index++];
                    if (c == '"') return;
                    if (c == '\\' && index < text.Length) index++;
                }
                throw new FormatException("字符串未闭合");
            }

            private static string UnescapeString(string quoted)
            {
                if (quoted.Length < 2) return string.Empty;
                StringBuilder sb = new StringBuilder(quoted.Length);
                for (int i = 1; i < quoted.Length - 1; i++)
                {
                    char c = quoted[i];
                    if (c != '\\') { sb.Append(c); continue; }
                    i++;
                    if (i >= quoted.Length - 1) break;
                    char esc = quoted[i];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < quoted.Length)
                            {
                                sb.Append((char)Convert.ToInt32(quoted.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                return sb.ToString();
            }

            private static void WriteValue(JsonNode node, StringBuilder sb, int indent, string newline)
            {
                JsonScalarNode scalar = node as JsonScalarNode;
                if (scalar != null)
                {
                    sb.Append(string.IsNullOrEmpty(scalar.Raw) ? "null" : scalar.Raw);
                    return;
                }
                JsonArrayNode array = node as JsonArrayNode;
                if (array != null)
                {
                    if (array.Items.Count == 0) { sb.Append("[]"); return; }
                    sb.Append("[");
                    for (int i = 0; i < array.Items.Count; i++)
                    {
                        sb.Append(i == 0 ? newline : "," + newline);
                        Indent(sb, indent + 1);
                        WriteValue(array.Items[i], sb, indent + 1, newline);
                    }
                    sb.Append(newline);
                    Indent(sb, indent);
                    sb.Append("]");
                    return;
                }
                JsonObjectNode obj = (JsonObjectNode)node;
                if (obj.Members.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{");
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    sb.Append(i == 0 ? newline : "," + newline);
                    Indent(sb, indent + 1);
                    sb.Append(QuoteString(obj.Members[i].Key));
                    sb.Append(": ");
                    WriteValue(obj.Members[i].Value, sb, indent + 1, newline);
                }
                sb.Append(newline);
                Indent(sb, indent);
                sb.Append("}");
            }

            private static void Indent(StringBuilder sb, int indent)
            {
                for (int i = 0; i < indent; i++)
                    sb.Append("  ");
            }

            private static string QuoteString(string value)
            {
                StringBuilder sb = new StringBuilder(value.Length + 2);
                sb.Append('"');
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
                return sb.ToString();
            }

            private static void SkipWhite(string text, ref int index)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            }
        }

        // ==================== JSON 载荷 ====================

        [DataContract]
        internal sealed class ParamDocument
        {
            [DataMember(Name = "format", Order = 0)]
            public string Format;

            [DataMember(Name = "unit_convention", Order = 1)]
            public string UnitConvention;

            /// <summary>整定溯源记录：保存当时生效的 DENI 路径。只记录，读回也不拿它切拓扑。</summary>
            [DataMember(Name = "deniPath", Order = 2, EmitDefaultValue = false)]
            public string DeniPath;

            [DataMember(Name = "pid", Order = 3)]
            public PidDoc Pid;

            [DataMember(Name = "swingup", Order = 4)]
            public SwingDoc Swing;

            [DataMember(Name = "limits", Order = 5)]
            public LimitsDoc Limits;

            /// <summary>能量泵工程口径（k_e / 泵幅 deg / 泵频 / 相位 deg）。读回时覆盖 swingup 段的泵字段。</summary>
            [DataMember(Name = "pump", Order = 6, EmitDefaultValue = false)]
            public PumpDoc Pump;

            /// <summary>系统辨识结果。没有结果时不写这一段。</summary>
            [DataMember(Name = "identify", Order = 7, EmitDefaultValue = false)]
            public IdentifyDoc Identify;

            /// <summary>软限位（deg 权威 + counts 派生）。读回时覆盖 limits 段的 PhiMin/PhiMax。</summary>
            [DataMember(Name = "soft_limits", Order = 8, EmitDefaultValue = false)]
            public SoftLimitDoc SoftLimits;

            /// <summary>机器标定（摆杆每转计数 / 摆臂每转计数）。</summary>
            [DataMember(Name = "calibration", Order = 9, EmitDefaultValue = false)]
            public CalibrationDoc Calibration;

            /// <summary>最近 20 次整圈捕获记录（按时间先后，含关键点）。</summary>
            [DataMember(Name = "captures", Order = 10, EmitDefaultValue = false)]
            public List<CaptureDoc> Captures;

            /// <summary>最后一次成功保存的时间（ISO-8601 UTC）。只作显示与溯源。</summary>
            [DataMember(Name = "saved_at", Order = 11, EmitDefaultValue = false)]
            public string SavedAt;
        }

        /// <summary>
        /// 用 raw JSON 的实际键存在性补强 pid 段的 has_* presence 标志。
        /// 旧参数文件没有 has_* 键，但值键（kp_phi 等）只要存在就说明是显式配置；
        /// 值键缺失 = 未配置，必须按出厂默认补齐，不能把缺键当成显式 0。
        /// 新写出的文件两者都有；此方法只做 OR，绝不把已有值键判成缺失。
        /// </summary>
        private static void MarkPidKeyPresence(PidDoc pid, string rawJson)
        {
            if (pid == null || string.IsNullOrEmpty(rawJson)) return;
            try
            {
                JsonObjectNode root = JsonDom.Parse(rawJson) as JsonObjectNode;
                JsonObjectNode pidObj = root == null ? null : root.GetObject("pid");
                if (pidObj == null) return;
                JsonNode ignored;
                pid.HasKpPhi = pid.HasKpPhi || pidObj.TryGet("kp_phi", out ignored);
                pid.HasKiPhi = pid.HasKiPhi || pidObj.TryGet("ki_phi", out ignored);
                pid.HasKdPhi = pid.HasKdPhi || pidObj.TryGet("kd_phi", out ignored);
                pid.HasTdLpMs = pid.HasTdLpMs || pidObj.TryGet("td_lp_ms", out ignored);
            }
            catch (Exception ex)
            {
                // presence 探测失败不能拖垮整份参数载入：退回 has_* 标志（新文件仍然正确），
                // 旧文件则可能被当成缺键并按默认补齐 —— 这是偏安全的方向（不会静默关断内环）。
                System.Diagnostics.Debug.WriteLine("pid 段键存在性探测失败：" + DiagText.Describe(ex));
            }
        }

        /// <summary>
        /// 用 raw JSON 的实际键存在性补强 swingup 段需要区分「缺键」与「显式 0」的 presence 标志。
        /// 旧参数文件没有 has_* 键，但值键只要存在就说明是显式配置；值键缺失 = 未配置，
        /// 必须按当前出厂默认补齐（target_energy_gain 的 0 恰在合法区间内，不能靠数值判断）。
        /// 新写出的文件两者都有；此方法只做 OR，绝不把已有值键判成缺失。
        /// </summary>
        private static void MarkSwingKeyPresence(SwingDoc swing, string rawJson)
        {
            if (swing == null || string.IsNullOrEmpty(rawJson)) return;
            try
            {
                JsonObjectNode root = JsonDom.Parse(rawJson) as JsonObjectNode;
                JsonObjectNode swingObj = root == null ? null : root.GetObject("swingup");
                if (swingObj == null) return;
                JsonNode ignored;
                swing.HasTargetEnergyGain =
                    swing.HasTargetEnergyGain || swingObj.TryGet("target_energy_gain", out ignored);
            }
            catch (Exception ex)
            {
                // presence 探测失败不能拖垮整份参数载入：退回 has_* 标志（新文件仍然正确），
                // 旧文件则可能被当成缺键并按默认补齐 —— 这是偏安全的方向（不会静默用 ε=0 飞越顶端）。
                System.Diagnostics.Debug.WriteLine("swingup 段键存在性探测失败：" + DiagText.Describe(ex));
            }
        }

        /// <summary>
        /// 平衡控制律增益。字段单位：
        /// 加速度律 Ka/Kad/Kai [rad/s² per rad 或 rad/s]、Kφ/Kφ̇ [rad/s² per rad 或 rad/s]、
        /// AccMax [rad/s²]、ArmRateMax [rad/s]、Sign 无量纲（±1）；
        /// 内环 KpPhi/KiPhi/KdPhi [rad/rad]、[1/s]、[s]；TdLpMs [ms]，Out/MaxIntegral [rad]，OutRateLimit [rad/s]，DeadBand [rad]。
        /// <para>
        /// kp/ki/kd 是**历史字段**（旧摆臂位置映射律），只为旧文件往返保留，控制律不读取。
        /// </para>
        /// </summary>
        [DataContract]
        internal sealed class PidDoc
        {
            [DataMember(Name = "kp", Order = 0)] public double Kp;
            [DataMember(Name = "ki", Order = 1)] public double Ki;
            [DataMember(Name = "kd", Order = 2)] public double Kd;
            [DataMember(Name = "kp_phi", Order = 3)] public double KpPhi;
            [DataMember(Name = "ki_phi", Order = 4)] public double KiPhi;
            [DataMember(Name = "kd_phi", Order = 5)] public double KdPhi;
            [DataMember(Name = "td_lp_ms", Order = 6)] public double TdLpMs;
            [DataMember(Name = "out_min_rad", Order = 7)] public double OutMin;
            [DataMember(Name = "out_max_rad", Order = 8)] public double OutMax;
            [DataMember(Name = "out_rate_rad_per_s", Order = 9)] public double OutRateLimit;
            [DataMember(Name = "max_integral_rad", Order = 10)] public double MaxIntegral;
            [DataMember(Name = "deadband_rad", Order = 11)] public double DeadBand;
            // 加速度反馈律（当前生效）。旧文件没有这 8 个键，读出来是 0 = 未配置，由加载裁决补齐。
            [DataMember(Name = "ka", Order = 12)] public double Ka;
            [DataMember(Name = "kad", Order = 13)] public double Kad;
            [DataMember(Name = "kai", Order = 14)] public double Kai;
            [DataMember(Name = "k_phi_acc", Order = 15)] public double KPhiAcc;
            [DataMember(Name = "k_phi_dot_acc", Order = 16)] public double KPhiDotAcc;
            [DataMember(Name = "acc_max_rad_per_s2", Order = 17)] public double AccMax;
            [DataMember(Name = "arm_rate_max_rad_per_s", Order = 18)] public double ArmRateMax;
            [DataMember(Name = "sign", Order = 19)] public double Sign;
            // 内环 φ 四个键的 presence 标志（CalibrationDoc 的 has_* 同一模式）：
            // 缺键 ≠ 显式 0。缺键按出厂默认补（否则内环被静默关断），显式 0 原样保留并告警。
            // 旧文件没有这些 has_* 键时，ParamStore 会再看 raw JSON 里值键是否存在（老文件兼容）。
            [DataMember(Name = "has_kp_phi", Order = 20)] public bool HasKpPhi;
            [DataMember(Name = "has_ki_phi", Order = 21)] public bool HasKiPhi;
            [DataMember(Name = "has_kd_phi", Order = 22)] public bool HasKdPhi;
            [DataMember(Name = "has_td_lp_ms", Order = 23)] public bool HasTdLpMs;

            public static PidDoc From(PidGains g)
            {
                PidDoc d = new PidDoc();
                d.Kp = g.Kp;
                d.Ki = g.Ki;
                d.Kd = g.Kd;
                d.KpPhi = g.KpPhi;
                d.KiPhi = g.KiPhi;
                d.KdPhi = g.KdPhi;
                d.TdLpMs = g.TdLpMs;
                // 新写出的文件四个值键一定在；has_* 只是显式的 presence 元数据，
                // 让「显式 0」与「缺键」在后续手改 / 合并时仍然可区分。
                d.HasKpPhi = true;
                d.HasKiPhi = true;
                d.HasKdPhi = true;
                d.HasTdLpMs = true;
                d.OutMin = g.OutMin;
                d.OutMax = g.OutMax;
                d.OutRateLimit = g.OutRateLimit;
                d.MaxIntegral = g.MaxIntegral;
                d.DeadBand = g.DeadBand;
                d.Ka = g.Ka;
                d.Kad = g.Kad;
                d.Kai = g.Kai;
                d.KPhiAcc = g.KPhi;
                d.KPhiDotAcc = g.KPhiDot;
                d.AccMax = g.AccMax;
                d.ArmRateMax = g.ArmRateMax;
                d.Sign = g.Sign;
                return d;
            }

            public PidGains ToGains()
            {
                PidGains g = new PidGains();
                g.Kp = Kp;
                g.Ki = Ki;
                g.Kd = Kd;
                g.KpPhi = KpPhi;
                g.KiPhi = KiPhi;
                g.KdPhi = KdPhi;
                g.TdLpMs = TdLpMs;
                g.OutMin = OutMin;
                g.OutMax = OutMax;
                g.OutRateLimit = OutRateLimit;
                g.MaxIntegral = MaxIntegral;
                g.DeadBand = DeadBand;
                g.Ka = Ka;
                g.Kad = Kad;
                g.Kai = Kai;
                g.KPhi = KPhiAcc;
                g.KPhiDot = KPhiDotAcc;
                g.AccMax = AccMax;
                g.ArmRateMax = ArmRateMax;
                g.Sign = Sign;
                return g;
            }
        }

        /// <summary>
        /// 起摆参数。字段单位：TargetEnergyGain 无量纲，Ke [rad/s² per J]，AMax/PumpAmplitude [rad/s²]，
        /// KPhi [1/s²]，KPhiDot [1/s]，PumpFrequencyHz [Hz]，Theta/PhiSwitch [rad]，OmegaSwitch [rad/s]，TimeoutMs [ms]。
        /// Capture* 是捕获门联合判据：模幅值上限 [rad]、每模幅值所需摆臂行程 [rad/rad]、行程裕量 [rad]、
        /// 逆速行程折算 [s]、臂速上限 [rad/s]、摆杆固有角频率 [rad/s]。
        /// CenterAuthRatio 无量纲（回中项可占的加速度预算比例），Bootstrap*/EnergySoftOmega/OmegaWindow 见字段注释，
        /// ThetaFade* 是近直立淡出组 [rad]/[rad]/无量纲。这些「起动微扰 / 相位软门 / 估计窗 / 淡出 / 回中预算」
        /// 字段以前不落盘，每次载入都按 0 处理并被逐字段修复当「越界」报一条记录；现在按生效值写盘。
        /// </summary>
        [DataContract]
        internal sealed class SwingDoc
        {
            [DataMember(Name = "target_energy_gain", Order = 0)] public double TargetEnergyGain;
            [DataMember(Name = "ke_rad_per_s2_per_j", Order = 1)] public double Ke;
            [DataMember(Name = "amax_rad_per_s2", Order = 2)] public double AMax;
            [DataMember(Name = "kphi_per_s2", Order = 3)] public double KPhi;
            [DataMember(Name = "kphidot_per_s", Order = 4)] public double KPhiDot;
            [DataMember(Name = "pump_amplitude_rad_per_s2", Order = 5)] public double PumpAmplitude;
            [DataMember(Name = "pump_frequency_hz", Order = 6)] public double PumpFrequencyHz;
            [DataMember(Name = "theta_switch_rad", Order = 7)] public double ThetaSwitchRad;
            [DataMember(Name = "omega_switch_rad_per_s", Order = 8)] public double OmegaSwitchRadPerSec;
            [DataMember(Name = "phi_switch_rad", Order = 9)] public double PhiSwitchRad;
            [DataMember(Name = "switch_hold_cycles", Order = 10)] public int SwitchHoldCycles;
            [DataMember(Name = "timeout_ms", Order = 11)] public int TimeoutMs;
            [DataMember(Name = "pump_phase_offset_rad", Order = 12)] public double PumpPhaseOffsetRad;
            // 捕获门联合判据（不稳定模态幅值 + 摆臂行程预算）。旧参数文件没有这 6 个键，读出来是 0 = 未配置，
            // 由控制层按内置默认补齐（SwingUpParams.CreateDefault），不能因为缺键拒载整份文件。
            [DataMember(Name = "capture_mode_amplitude_max_rad", Order = 13)] public double CaptureModeAmplitudeMaxRad;
            [DataMember(Name = "capture_stroke_per_mode_rad", Order = 14)] public double CaptureStrokePerModeRad;
            [DataMember(Name = "capture_stroke_margin_rad", Order = 15)] public double CaptureStrokeMarginRad;
            [DataMember(Name = "capture_adverse_rate_stroke_sec", Order = 16)] public double CaptureAdverseRateStrokeSec;
            [DataMember(Name = "capture_arm_rate_limit_rad_per_s", Order = 17)] public double CaptureArmRateLimitRadPerSec;
            [DataMember(Name = "capture_pendulum_rate_rad_per_s", Order = 18)] public double CapturePendulumRateRadPerSec;
            // 起动微扰 / 相位软门 / ω 估计窗 / 近直立淡出 / 回中预算：当前生效值，必须落盘。
            // 旧文件没有这些键（DataContract 缺键 = 0）；0 一律是「未配置」，ToParams 保持 0 不改写，
            // 由 ParamStore.TryLoad 按控制层默认标注（同一规则见 SwingUpController 的 Pick）。
            [DataMember(Name = "center_auth_ratio", Order = 19)] public double CenterAuthRatio;
            [DataMember(Name = "bootstrap_accel_rad_per_s2", Order = 20)] public double BootstrapAccelRadPerSec2;
            [DataMember(Name = "bootstrap_sec", Order = 21)] public double BootstrapSec;
            [DataMember(Name = "energy_soft_omega_rad_per_s", Order = 22)] public double EnergySoftOmegaRadPerSec;
            [DataMember(Name = "omega_window_ms", Order = 23)] public double OmegaWindowMs;
            [DataMember(Name = "theta_fade_start_rad", Order = 24)] public double ThetaFadeStartRad;
            [DataMember(Name = "theta_fade_end_rad", Order = 25)] public double ThetaFadeEndRad;
            [DataMember(Name = "theta_fade_floor", Order = 26)] public double ThetaFadeFloor;
            // 能量整形 / 软中心带（2026-11-20）。旧文件缺键 = 0 = 未配置，由 FillUnconfiguredSwingFields 补默认。
            [DataMember(Name = "energy_extract_ratio", Order = 27)] public double EnergyExtractRatio;
            [DataMember(Name = "energy_deadband_ratio", Order = 28)] public double EnergyDeadbandRatio;
            [DataMember(Name = "center_band_rad", Order = 29)] public double CenterBandRad;
            // 抽取增益与注入增益 Ke 解耦（2026-11-20 台架）。旧文件缺键 = 0 = 未配置，按出厂 60 补齐；
            // 缺了它控制层也能按内置默认跑，但现场改过的值必须能落盘、能读回，否则换进程就丢。
            [DataMember(Name = "extract_gain", Order = 30)] public double ExtractGain;
            // 预捕获回中 / 快速提交 / 漏桶（2026-11-20 捕获交棒重构）。旧文件缺键 = 0 = 未配置，
            // 由 FillUnconfiguredSwingFields 按 CreateDefault 补齐；单位与范围见 SwingUpParams 字段注释。
            // 提交保护 = 硬捕获门 + 臂速软上限 + 摆杆角速度上限（|ω|≤0.8）：台架 R1-1 证明
            // 「ψ 很小 + ω 很大」的高速穿越能把 u 拉低、绕过硬门，所以 ω 这一条必须保留。
            [DataMember(Name = "capture_prepare_approach_rad", Order = 31)] public double CapturePrepareApproachRad;
            [DataMember(Name = "capture_prepare_energy_margin_ratio", Order = 32)] public double CapturePrepareEnergyMarginRatio;
            [DataMember(Name = "capture_commit_arm_rate_rad_per_s", Order = 33)] public double CaptureCommitArmRateRadPerSec;
            [DataMember(Name = "capture_commit_hold_cycles", Order = 34)] public int CaptureCommitHoldCycles;
            // 单位 = 千分之一拍/拍（milli-cycle per cycle）：2 = 每拍扣 0.002 拍，不是旧的「每拍扣 2 拍」。
            // 键名与类型保持不变，旧文件继续可读（语义按新单位解释）。
            [DataMember(Name = "switch_hold_decay_per_cycle", Order = 35)] public int SwitchHoldDecayPerCycle;
            [DataMember(Name = "capture_commit_omega_rad_per_s", Order = 36)] public double CaptureCommitOmegaRadPerSec;
            // 目标能量裕量 ε 的 presence：0 是合法值（恰在分界线上），DataContract 无法用数值区分
            // 「缺键」与「显式写 0」；新文件写 true，旧文件由 raw JSON 值键存在性兜底（MarkSwingKeyPresence）。
            [DataMember(Name = "has_target_energy_gain", Order = 39)] public bool HasTargetEnergyGain;

            public static SwingDoc From(SwingUpParams p)
            {
                SwingDoc d = new SwingDoc();
                d.TargetEnergyGain = p.TargetEnergyGain;
                d.Ke = p.Ke;
                d.AMax = p.AMax;
                d.KPhi = p.KPhi;
                d.KPhiDot = p.KPhiDot;
                d.PumpAmplitude = p.PumpAmplitude;
                d.PumpFrequencyHz = p.PumpFrequencyHz;
                d.ThetaSwitchRad = p.ThetaSwitchRad;
                d.OmegaSwitchRadPerSec = p.OmegaSwitchRadPerSec;
                d.PhiSwitchRad = p.PhiSwitchRad;
                d.SwitchHoldCycles = p.SwitchHoldCycles;
                d.TimeoutMs = p.TimeoutMs;
                d.PumpPhaseOffsetRad = p.PumpPhaseOffsetRad;
                d.CaptureModeAmplitudeMaxRad = p.CaptureModeAmplitudeMaxRad;
                d.CaptureStrokePerModeRad = p.CaptureStrokePerModeRad;
                d.CaptureStrokeMarginRad = p.CaptureStrokeMarginRad;
                d.CaptureAdverseRateStrokeSec = p.CaptureAdverseRateStrokeSec;
                d.CaptureArmRateLimitRadPerSec = p.CaptureArmRateLimitRadPerSec;
                d.CapturePendulumRateRadPerSec = p.CapturePendulumRateRadPerSec;
                d.CenterAuthRatio = p.CenterAuthRatio;
                d.BootstrapAccelRadPerSec2 = p.BootstrapAccelRadPerSec2;
                d.BootstrapSec = p.BootstrapSec;
                d.EnergySoftOmegaRadPerSec = p.EnergySoftOmegaRadPerSec;
                d.OmegaWindowMs = p.OmegaWindowMs;
                d.ThetaFadeStartRad = p.ThetaFadeStartRad;
                d.ThetaFadeEndRad = p.ThetaFadeEndRad;
                d.ThetaFadeFloor = p.ThetaFadeFloor;
                d.EnergyExtractRatio = p.EnergyExtractRatio;
                d.EnergyDeadbandRatio = p.EnergyDeadbandRatio;
                d.CenterBandRad = p.CenterBandRad;
                d.ExtractGain = p.ExtractGain;
                d.CapturePrepareApproachRad = p.CapturePrepareApproachRad;
                d.CapturePrepareEnergyMarginRatio = p.CapturePrepareEnergyMarginRatio;
                d.CaptureCommitArmRateRadPerSec = p.CaptureCommitArmRateRadPerSec;
                d.CaptureCommitHoldCycles = p.CaptureCommitHoldCycles;
                d.SwitchHoldDecayPerCycle = p.SwitchHoldDecayPerCycle;
                d.CaptureCommitOmegaRadPerSec = p.CaptureCommitOmegaRadPerSec;
                d.HasTargetEnergyGain = true;
                return d;
            }

            public SwingUpParams ToParams()
            {
                SwingUpParams p = new SwingUpParams();
                p.TargetEnergyGain = TargetEnergyGain;
                p.Ke = Ke;
                p.AMax = AMax;
                p.KPhi = KPhi;
                p.KPhiDot = KPhiDot;
                p.PumpAmplitude = PumpAmplitude;
                p.PumpFrequencyHz = PumpFrequencyHz;
                p.ThetaSwitchRad = ThetaSwitchRad;
                p.OmegaSwitchRadPerSec = OmegaSwitchRadPerSec;
                p.PhiSwitchRad = PhiSwitchRad;
                p.SwitchHoldCycles = SwitchHoldCycles;
                p.TimeoutMs = TimeoutMs;
                p.PumpPhaseOffsetRad = PumpPhaseOffsetRad;
                // 缺失（旧文件）= 0 = 未配置，保持 0 交给控制层按内置默认补齐，这里不擅自填默认值。
                p.CaptureModeAmplitudeMaxRad = CaptureModeAmplitudeMaxRad;
                p.CaptureStrokePerModeRad = CaptureStrokePerModeRad;
                p.CaptureStrokeMarginRad = CaptureStrokeMarginRad;
                p.CaptureAdverseRateStrokeSec = CaptureAdverseRateStrokeSec;
                p.CaptureArmRateLimitRadPerSec = CaptureArmRateLimitRadPerSec;
                p.CapturePendulumRateRadPerSec = CapturePendulumRateRadPerSec;
                // 同上：缺键 = 0 = 未配置，由 ParamStore.TryLoad 统一按默认标注并写进提示，不在这里分散处理。
                p.CenterAuthRatio = CenterAuthRatio;
                p.BootstrapAccelRadPerSec2 = BootstrapAccelRadPerSec2;
                p.BootstrapSec = BootstrapSec;
                p.EnergySoftOmegaRadPerSec = EnergySoftOmegaRadPerSec;
                p.OmegaWindowMs = OmegaWindowMs;
                p.ThetaFadeStartRad = ThetaFadeStartRad;
                p.ThetaFadeEndRad = ThetaFadeEndRad;
                p.ThetaFadeFloor = ThetaFadeFloor;
                // 缺键 = 0 = 未配置，保持 0 交给 FillUnconfiguredSwingFields 按默认标注。
                p.EnergyExtractRatio = EnergyExtractRatio;
                p.EnergyDeadbandRatio = EnergyDeadbandRatio;
                p.CenterBandRad = CenterBandRad;
                p.ExtractGain = ExtractGain;
                // 缺键 = 0 = 未配置，保持 0 交给 FillUnconfiguredSwingFields 按默认标注。
                p.CapturePrepareApproachRad = CapturePrepareApproachRad;
                p.CapturePrepareEnergyMarginRatio = CapturePrepareEnergyMarginRatio;
                p.CaptureCommitArmRateRadPerSec = CaptureCommitArmRateRadPerSec;
                p.CaptureCommitHoldCycles = CaptureCommitHoldCycles;
                p.SwitchHoldDecayPerCycle = SwitchHoldDecayPerCycle;
                p.CaptureCommitOmegaRadPerSec = CaptureCommitOmegaRadPerSec;
                return p;
            }
        }

        /// <summary>
        /// 安全阈值。字段单位：ThetaTrip/ThetaLost/PhiErr [rad]，**ArmMin/ArmMaxCount = 当前 raw 坐标系下的 0x6064 计数**，
        /// OmegaArmMax [rad/s]，JEq [kg·m²]，Mgl [N·m]。摆臂软限位没有 deg / rad 口径的键。
        /// </summary>
        [DataContract]
        internal sealed class LimitsDoc
        {
            [DataMember(Name = "theta_trip_rad", Order = 0)] public double ThetaTripRad;
            [DataMember(Name = "theta_lost_rad", Order = 1)] public double ThetaLostRad;
            [DataMember(Name = "phi_err_max_rad", Order = 2)] public double PhiErrMaxRad;
            [DataMember(Name = "arm_min_count", Order = 3)] public int ArmMinCount;
            [DataMember(Name = "arm_max_count", Order = 4)] public int ArmMaxCount;
            [DataMember(Name = "omega_arm_max_rad_per_s", Order = 5)] public double OmegaArmMaxRadPerSec;
            [DataMember(Name = "jeq_kg_m2", Order = 6)] public double JEq;
            [DataMember(Name = "mgl_n_m", Order = 7)] public double Mgl;
            // 耦合比 r/l（无量纲）。缺这一段 / 缺这个键 = 未标定（0），旧参数文件照读，不报错。
            [DataMember(Name = "radius_ratio", Order = 8)] public double RadiusRatio;

            /// <summary>
            /// 读到的文件里是否缺摆臂 counts 限位（旧格式只有 phi_min_rad / phi_max_rad，缺键 = 0/0）。
            /// 不序列化，只用于给上层写「请重新采集」的提示；旧角度字段一律忽略、不换算。
            /// </summary>
            public bool ArmLimitCountsMissing;

            public static LimitsDoc From(SafetyLimits s)
            {
                LimitsDoc d = new LimitsDoc();
                d.ThetaTripRad = s.ThetaTripRad;
                d.ThetaLostRad = s.ThetaLostRad;
                d.PhiErrMaxRad = s.PhiErrMaxRad;
                d.ArmMinCount = s.ArmMinCount;
                d.ArmMaxCount = s.ArmMaxCount;
                d.OmegaArmMaxRadPerSec = s.OmegaArmMaxRadPerSec;
                d.JEq = s.JEq;
                d.Mgl = s.Mgl;
                d.RadiusRatio = s.RadiusRatio;
                return d;
            }

            public SafetyLimits ToLimits()
            {
                SafetyLimits s = new SafetyLimits();
                s.ThetaTripRad = ThetaTripRad;
                s.ThetaLostRad = ThetaLostRad;
                s.PhiErrMaxRad = PhiErrMaxRad;
                s.ArmMinCount = ArmMinCount;
                s.ArmMaxCount = ArmMaxCount;
                if (ArmMinCount == 0 && ArmMaxCount == 0)
                {
                    // 旧文件没有 counts 限位（DataContract 缺键 = 0）。**不拿 phi_min_rad/phi_max_rad 换算**：
                    // 用户没给过行程比例，换算出来的就是编的。按「尚未采集」占位，并让上层明确要求重设。
                    ArmLimitCountsMissing = true;
                    s.ArmMinCount = SafetyLimits.DefaultArmMinCount;
                    s.ArmMaxCount = SafetyLimits.DefaultArmMaxCount;
                }
                s.OmegaArmMaxRadPerSec = OmegaArmMaxRadPerSec;
                s.JEq = JEq;
                s.Mgl = Mgl;
                s.RadiusRatio = RadiusRatio;
                return s;
            }
        }

        /// <summary>
        /// 能量泵工程口径。泵幅用「等效摆臂角幅值 [deg]」保存，避免把加速度幅当成角度看；
        /// 换算关系 A_rad = A_acc / (2πf)²，读回时按 pump_hz 还原成控制层的 rad/s²。
        /// </summary>
        [DataContract]
        internal sealed class PumpDoc
        {
            [DataMember(Name = "ke_rad_per_s2_per_j", Order = 0)] public double Ke;
            [DataMember(Name = "pump_amp_deg", Order = 1)] public double PumpAmpDeg;
            [DataMember(Name = "pump_hz", Order = 2)] public double PumpHz;
            [DataMember(Name = "phase_offset_deg", Order = 3)] public double PhaseOffsetDeg;

            public static PumpDoc From(PumpParameters p)
            {
                PumpDoc d = new PumpDoc();
                d.Ke = p.Ke;
                d.PumpAmpDeg = p.PumpAmpDeg;
                d.PumpHz = p.PumpHz;
                d.PhaseOffsetDeg = p.PhaseOffsetDeg;
                return d;
            }

            public PumpParameters ToParameters()
            {
                PumpParameters p = new PumpParameters();
                p.Ke = Ke;
                p.PumpAmpDeg = PumpAmpDeg;
                p.PumpHz = PumpHz;
                p.PhaseOffsetDeg = PhaseOffsetDeg;
                return p;
            }
        }

        /// <summary>
        /// 系统辨识结果。NaN 不直接进 JSON：用 has_* 标志 + 0 值表达「不可辨识」，
        /// 读回时再还原成 NaN，避免不同 JSON 序列化器对 NaN 的写法不一致。
        /// </summary>
        [DataContract]
        internal sealed class IdentifyDoc
        {
            [DataMember(Name = "natural_freq_hz", Order = 0)] public double NaturalFreqHz;
            [DataMember(Name = "damping_zeta", Order = 1)] public double DampingZeta;
            [DataMember(Name = "has_damping_zeta", Order = 2)] public bool HasDampingZeta;
            [DataMember(Name = "theta_offset_deg", Order = 3)] public double ThetaOffsetDeg;
            [DataMember(Name = "has_theta_offset", Order = 4)] public bool HasThetaOffset;
            [DataMember(Name = "catch_rate_limit_deg_per_s", Order = 5)] public double CatchRateLimitDegPerSec;
            [DataMember(Name = "has_catch_rate_limit", Order = 6)] public bool HasCatchRateLimit;
            [DataMember(Name = "confidence", Order = 7)] public double Confidence;
            [DataMember(Name = "identified_at", Order = 8, EmitDefaultValue = false)] public string IdentifiedAt;
            [DataMember(Name = "ke_source", Order = 9, EmitDefaultValue = false)] public string KeSource;
            [DataMember(Name = "half_period_samples", Order = 10)] public int HalfPeriodSamples;
            [DataMember(Name = "extrema_samples", Order = 11)] public int ExtremaSamples;
            [DataMember(Name = "capture_samples", Order = 12)] public int CaptureSamples;
            [DataMember(Name = "recommended_pump_hz", Order = 13)] public double RecommendedPumpHz;
            [DataMember(Name = "recommended_pump_amp_deg", Order = 14)] public double RecommendedPumpAmpDeg;
            [DataMember(Name = "has_recommended_pump_amp", Order = 15)] public bool HasRecommendedPumpAmp;
            [DataMember(Name = "recommended_ke", Order = 16)] public double RecommendedKe;

            public static IdentifyDoc From(IdentifyParameters p)
            {
                IdentifyDoc d = new IdentifyDoc();
                d.NaturalFreqHz = p.NaturalFreqHz;
                d.HasDampingZeta = !double.IsNaN(p.DampingZeta) && ControlMath.IsFinite(p.DampingZeta);
                d.DampingZeta = d.HasDampingZeta ? p.DampingZeta : 0;
                d.HasThetaOffset = !double.IsNaN(p.ThetaOffsetDeg) && ControlMath.IsFinite(p.ThetaOffsetDeg);
                d.ThetaOffsetDeg = d.HasThetaOffset ? p.ThetaOffsetDeg : 0;
                d.HasCatchRateLimit = !double.IsNaN(p.CatchRateLimitDegPerSec) &&
                                      ControlMath.IsFinite(p.CatchRateLimitDegPerSec);
                d.CatchRateLimitDegPerSec = d.HasCatchRateLimit ? p.CatchRateLimitDegPerSec : 0;
                d.Confidence = p.Confidence;
                d.IdentifiedAt = FormatTimestamp(p.IdentifiedAtUtc);
                d.KeSource = string.IsNullOrEmpty(p.KeSource) ? null : p.KeSource;
                d.HalfPeriodSamples = p.HalfPeriodSamples;
                d.ExtremaSamples = p.ExtremaSamples;
                d.CaptureSamples = p.CaptureSamples;
                d.RecommendedPumpHz = p.RecommendedPumpHz;
                d.RecommendedPumpAmpDeg = p.RecommendedPumpAmpDeg;
                d.HasRecommendedPumpAmp = p.HasRecommendedPumpAmp;
                d.RecommendedKe = p.RecommendedKe;
                return d;
            }

            public IdentifyParameters ToParameters()
            {
                IdentifyParameters p = new IdentifyParameters();
                p.NaturalFreqHz = NaturalFreqHz;
                p.DampingZeta = HasDampingZeta ? DampingZeta : double.NaN;
                p.ThetaOffsetDeg = HasThetaOffset ? ThetaOffsetDeg : double.NaN;
                p.CatchRateLimitDegPerSec = HasCatchRateLimit ? CatchRateLimitDegPerSec : double.NaN;
                p.Confidence = Confidence;
                p.IdentifiedAtUtc = ParseTimestamp(IdentifiedAt);
                p.KeSource = KeSource;
                p.HalfPeriodSamples = HalfPeriodSamples;
                p.ExtremaSamples = ExtremaSamples;
                p.CaptureSamples = CaptureSamples;
                p.RecommendedPumpHz = RecommendedPumpHz;
                p.RecommendedPumpAmpDeg = RecommendedPumpAmpDeg;
                p.HasRecommendedPumpAmp = HasRecommendedPumpAmp;
                p.RecommendedKe = RecommendedKe;
                return p;
            }
        }

        /// <summary>
        /// 软限位：**唯一单位 = 当前 0x6064 坐标系下的原始计数**。旧文件的 min_deg / max_deg 一律忽略、不换算。
        /// 「绝对计数」不等于跨掉电的物理绝对位置：EC1 开环步进的 0x6064 在重新上电后必须重新与机械零点
        /// 对齐；raw 坐标一变，本段的 min/max 必须随零点一起「规整化」或重新采集，不能自动猜偏移。
        /// </summary>
        [DataContract]
        internal sealed class SoftLimitDoc
        {
            [DataMember(Name = "min_counts", Order = 0)] public int MinCounts;
            [DataMember(Name = "max_counts", Order = 1)] public int MaxCounts;
            // 历史键 min_deg / max_deg 不再读写：用户从没给过摆臂行程比例，角度行程是程序自己编的。

            public static SoftLimitDoc From(SoftLimitParameters p)
            {
                SoftLimitDoc d = new SoftLimitDoc();
                d.MinCounts = p.MinCounts;
                d.MaxCounts = p.MaxCounts;
                return d;
            }

            public SoftLimitParameters ToParameters()
            {
                SoftLimitParameters p = new SoftLimitParameters();
                p.MinCounts = MinCounts;
                p.MaxCounts = MaxCounts;
                return p;
            }
        }

        /// <summary>
        /// 机器标定。摆杆每转计数 / 摆杆零点 / 摆臂每转计数 / 减速比 / 摆臂零点全部按原始 counts 存。
        /// 位置点是**当前 raw 坐标系**下的计数：掉电后 raw 起点变了，has_* 仍为 true 也只说明「曾经标过」，
        /// 不说明这些计数仍对应同一机械位置；必须由用户显式规整化（保持行程）或重新采集。
        /// `calibration_cleared` 是跨重启的“用户明确清空”标志，控制层的运行态 <c>CalibrationCleared</c>
        /// 必须由它恢复，禁止另立第二套 cleared 语义。
        /// <para>
        /// 另有三类如实落盘的状态键（只描述事实，不参与控制律）：
        /// ① `pendulum_pair_complete` / `pendulum_pair_missing` —— 0°/360° 是否成对、缺哪一半；
        /// ② `arm_pose_session_tag` / `pendulum_pose_session_tag` —— 该组点位是在哪一个进程里被
        ///    整体采集 / 规整化 / 确认的（增量编码器无法自证跨掉电位置，别的进程写的标记 = 未确认）；
        /// ③ `*_pose_captured_at` —— 上述确认的 UTC 时间，只作溯源。
        /// </para>
        /// </summary>
        [DataContract]
        internal sealed class CalibrationDoc
        {
            [DataMember(Name = "pendulum_cpr", Order = 0)] public double PendulumCpr;
            [DataMember(Name = "arm_counts_per_rev", Order = 1)] public int ArmCountsPerRev;
            [DataMember(Name = "arm_gear_ratio", Order = 2, EmitDefaultValue = false)] public double ArmGearRatio;
            /// <summary>摆臂零点（原始 0x6064 计数）。has_arm_zero_count 才表示设过；旧文件缺键 = 未设定。</summary>
            [DataMember(Name = "arm_zero_count", Order = 3, EmitDefaultValue = false)] public int ArmZeroCount;
            [DataMember(Name = "has_arm_zero_count", Order = 4, EmitDefaultValue = false)] public bool HasArmZeroCount;
            /// <summary>摆杆零点（自然下垂 / 最底部的原始计数，32 位位型）。has_pendulum_zero 才表示设过。</summary>
            [DataMember(Name = "pendulum_zero_count", Order = 5, EmitDefaultValue = false)] public int PendulumZeroCount;
            [DataMember(Name = "has_pendulum_zero", Order = 6, EmitDefaultValue = false)] public bool HasPendulumZero;
            /// <summary>摆杆 360° 标定点（与 0° 一起算出一圈的量 = |360−0|）。</summary>
            [DataMember(Name = "pendulum_360_count", Order = 7, EmitDefaultValue = false)] public int Pendulum360Count;
            [DataMember(Name = "has_pendulum_360", Order = 8, EmitDefaultValue = false)] public bool HasPendulum360;
            /// <summary>用户明确清空过位置标定，跨重启保留。缺键 = false（旧文件 / 从未清空）。</summary>
            [DataMember(Name = "calibration_cleared", Order = 9, EmitDefaultValue = false)] public bool CalibrationCleared;
            /// <summary>
            /// 摆杆 0°/360° 两点是否成对且能派生出一圈的量。**如实落盘**：只采到一半时这里写 false，
            /// 并由 <see cref="PendulumPairMissing"/> 写出缺的是哪一半；载入时仍以 has_* 实际值为准，
            /// 不拿这个键当真相（旧文件没有它 = false，但两点齐全的旧文件仍然成对）。
            /// </summary>
            [DataMember(Name = "pendulum_pair_complete", Order = 10)] public bool PendulumPairComplete;
            /// <summary>缺哪一半的机器可读标记：zero / 360 / zero+360 / same_count / span_overflow；完成时不写。</summary>
            [DataMember(Name = "pendulum_pair_missing", Order = 11, EmitDefaultValue = false)] public string PendulumPairMissing;
            /// <summary>摆臂三点最后一次整体确认的进程会话标记（见 CalibrationSession）。</summary>
            [DataMember(Name = "arm_pose_session_tag", Order = 12, EmitDefaultValue = false)] public string ArmPoseSessionTag;
            /// <summary>摆臂三点最后一次整体确认的 UTC 时间（ISO-8601，只作溯源）。</summary>
            [DataMember(Name = "arm_pose_captured_at", Order = 13, EmitDefaultValue = false)] public string ArmPoseCapturedAt;
            /// <summary>摆杆两点最后一次整体确认的进程会话标记。</summary>
            [DataMember(Name = "pendulum_pose_session_tag", Order = 14, EmitDefaultValue = false)] public string PendulumPoseSessionTag;
            /// <summary>摆杆两点最后一次整体确认的 UTC 时间（ISO-8601，只作溯源）。</summary>
            [DataMember(Name = "pendulum_pose_captured_at", Order = 15, EmitDefaultValue = false)] public string PendulumPoseCapturedAt;

            public static CalibrationDoc From(CalibrationParameters p)
            {
                CalibrationDoc d = new CalibrationDoc();
                d.PendulumCpr = p.PendulumCpr;
                d.ArmCountsPerRev = p.ArmCountsPerRev;
                d.ArmGearRatio = p.ArmGearRatio;
                d.ArmZeroCount = p.ArmZeroCount;
                d.HasArmZeroCount = p.HasArmZeroCount;
                d.PendulumZeroCount = p.PendulumZeroCount;
                d.HasPendulumZero = p.HasPendulumZero;
                d.Pendulum360Count = p.Pendulum360Count;
                d.HasPendulum360 = p.HasPendulum360;
                d.CalibrationCleared = p.CalibrationCleared;
                // 成对性按实际点位算，不照抄对象上可能过期的状态：写盘值必须与同一份文件里的
                // has_pendulum_zero / has_pendulum_360 自洽，外部工具据此就能看出「标定没做完」。
                PendulumPairCheck pair = p.CheckPendulumPair();
                d.PendulumPairComplete = pair.IsComplete;
                d.PendulumPairMissing = pair.MissingToken;
                d.ArmPoseSessionTag = p.HasArmZeroCount ? p.ArmPoseSessionTag : null;
                d.ArmPoseCapturedAt = p.HasArmZeroCount ? FormatTimestamp(p.ArmPoseCapturedAtUtc) : null;
                d.PendulumPoseSessionTag = p.HasPendulumZero && p.HasPendulum360 ? p.PendulumPoseSessionTag : null;
                d.PendulumPoseCapturedAt = p.HasPendulumZero && p.HasPendulum360
                    ? FormatTimestamp(p.PendulumPoseCapturedAtUtc)
                    : null;
                return d;
            }

            public CalibrationParameters ToParameters()
            {
                CalibrationParameters p = new CalibrationParameters();
                p.PendulumCpr = PendulumCpr;
                p.ArmCountsPerRev = ArmCountsPerRev;
                // 旧文件没有这个键（DataMember 缺省 = 0）：按无减速 1.0 处理，不把 0 当合法减速比。
                p.ArmGearRatio = ArmGearRatio > 0 ? ArmGearRatio : 1.0;
                p.ArmZeroCount = ArmZeroCount;
                p.HasArmZeroCount = HasArmZeroCount;
                p.PendulumZeroCount = PendulumZeroCount;
                p.HasPendulumZero = HasPendulumZero;
                p.Pendulum360Count = Pendulum360Count;
                p.HasPendulum360 = HasPendulum360;
                p.CalibrationCleared = CalibrationCleared;
                // 会话标记只读回原样，不在载入时伪造：与本次进程不匹配由上层判定为「未确认」。
                p.ArmPoseSessionTag = ArmPoseSessionTag;
                p.ArmPoseCapturedAtUtc = ParseTimestamp(ArmPoseCapturedAt);
                p.PendulumPoseSessionTag = PendulumPoseSessionTag;
                p.PendulumPoseCapturedAtUtc = ParseTimestamp(PendulumPoseCapturedAt);
                return p;
            }
        }

        /// <summary>
        /// 一次整圈捕获记录。旧字段（caught / hold_sec / fell / release_*）保留用于读回旧文件；
        /// 新字段带 has_valid / has_release 标志 —— 旧文件缺这些键时按旧口径解释，绝不把旧记录误判成新语义。
        /// </summary>
        [DataContract]
        internal sealed class CaptureDoc
        {
            [DataMember(Name = "timestamp", Order = 0, EmitDefaultValue = false)] public string Timestamp;
            [DataMember(Name = "started_at", Order = 1, EmitDefaultValue = false)] public string StartedAt;
            [DataMember(Name = "has_release", Order = 2)] public bool HasRelease;
            [DataMember(Name = "release_theta_deg", Order = 3)] public double ReleaseThetaDeg;
            [DataMember(Name = "release_omega_rad_per_s", Order = 4)] public double ReleaseOmegaRadPerSec;
            [DataMember(Name = "valid", Order = 5)] public bool Valid;
            [DataMember(Name = "has_valid", Order = 6)] public bool HasValid;
            [DataMember(Name = "invalid_reason", Order = 7, EmitDefaultValue = false)] public string InvalidReason;
            [DataMember(Name = "start_theta_deg", Order = 8)] public double StartThetaDeg;
            [DataMember(Name = "start_pendulum_count", Order = 9)] public int StartPendulumCount;
            [DataMember(Name = "lift_sec", Order = 10)] public double LiftSeconds;
            [DataMember(Name = "rev_sec", Order = 11)] public double RevolutionSeconds;
            [DataMember(Name = "sweep_deg", Order = 12)] public double SweepDeg;
            [DataMember(Name = "max_sweep_deg", Order = 13)] public double MaxSweepDeg;
            [DataMember(Name = "lift_sign", Order = 14)] public int LiftSign;
            [DataMember(Name = "max_omega_rad_per_s", Order = 15)] public double MaxOmegaRadPerSec;
            [DataMember(Name = "max_dev_deg", Order = 16)] public double MaxDeviationDeg;
            [DataMember(Name = "key_points", Order = 17, EmitDefaultValue = false)] public List<KeyPointDoc> KeyPoints;
            [DataMember(Name = "caught", Order = 18)] public bool Caught;
            [DataMember(Name = "hold_sec", Order = 19)] public double HoldSeconds;
            [DataMember(Name = "capture_delay_sec", Order = 20)] public double CaptureDelaySeconds;
            [DataMember(Name = "has_capture_delay", Order = 21)] public bool HasCaptureDelay;
            [DataMember(Name = "fell", Order = 22)] public bool Fell;

            public static CaptureDoc From(CaptureRecord r)
            {
                CaptureDoc d = new CaptureDoc();
                d.Timestamp = FormatTimestamp(r.TimestampUtc);
                d.StartedAt = FormatTimestamp(r.StartTimestampUtc);
                d.HasRelease = ControlMath.IsFinite(r.ReleaseThetaDeg);
                d.ReleaseThetaDeg = d.HasRelease ? r.ReleaseThetaDeg : 0;
                d.ReleaseOmegaRadPerSec = d.HasRelease && ControlMath.IsFinite(r.ReleaseOmegaRadPerSec)
                    ? r.ReleaseOmegaRadPerSec
                    : 0;
                d.Valid = r.Valid;
                d.HasValid = true;
                d.InvalidReason = string.IsNullOrEmpty(r.InvalidReason) ? null : r.InvalidReason;
                d.StartThetaDeg = r.StartThetaDeg;
                d.StartPendulumCount = r.StartPendulumCount;
                d.LiftSeconds = r.LiftSeconds;
                d.RevolutionSeconds = r.RevolutionSeconds;
                d.SweepDeg = r.SweepDeg;
                d.MaxSweepDeg = r.MaxSweepDeg;
                d.LiftSign = r.LiftSign;
                d.MaxOmegaRadPerSec = r.MaxOmegaRadPerSec;
                d.MaxDeviationDeg = r.MaxDeviationDeg;
                d.KeyPoints = KeyPointDoc.FromList(r.KeyPoints);
                // 旧口径字段跟随新语义写（读旧程序的调用方至少能看到「有效/无效」与整圈耗时）。
                d.Caught = r.Caught;
                d.HoldSeconds = r.HoldSeconds;
                d.HasCaptureDelay = !double.IsNaN(r.CaptureDelaySeconds) && ControlMath.IsFinite(r.CaptureDelaySeconds);
                d.CaptureDelaySeconds = d.HasCaptureDelay ? r.CaptureDelaySeconds : 0;
                d.Fell = r.Fell;
                return d;
            }

            public bool TryToRecord(out CaptureRecord record, out string error)
            {
                record = default(CaptureRecord);
                error = null;
                DateTime? timestamp = ParseTimestamp(Timestamp);
                if (!timestamp.HasValue)
                {
                    error = "timestamp 缺失或无法解析";
                    return false;
                }
                if (!ControlMath.IsFinite(HoldSeconds) || !ControlMath.IsFinite(MaxDeviationDeg) ||
                    HoldSeconds < 0 || MaxDeviationDeg < 0)
                {
                    error = "保持时长 / 最大偏离不合法（非有限数或负值）";
                    return false;
                }

                // 旧文件没有 has_valid：按旧口径 valid = caught，且释放值一定存在。
                bool hasValid = HasValid;
                bool valid = HasValid ? Valid : Caught;
                bool hasRelease = HasValid ? HasRelease : true;
                if (hasRelease &&
                    (!ControlMath.IsFinite(ReleaseThetaDeg) || !ControlMath.IsFinite(ReleaseOmegaRadPerSec)))
                {
                    error = "释放角 / 角速度含非有限数";
                    return false;
                }
                if (!ControlMath.IsFinite(LiftSeconds) || !ControlMath.IsFinite(RevolutionSeconds) ||
                    !ControlMath.IsFinite(SweepDeg) || !ControlMath.IsFinite(MaxSweepDeg) ||
                    !ControlMath.IsFinite(MaxOmegaRadPerSec) || !ControlMath.IsFinite(MaxDeviationDeg))
                {
                    error = "整圈统计字段含非有限数";
                    return false;
                }

                record.TimestampUtc = timestamp.Value.ToUniversalTime();
                record.StartTimestampUtc = ParseTimestamp(StartedAt) ?? record.TimestampUtc;
                record.ReleaseThetaDeg = hasRelease ? ReleaseThetaDeg : double.NaN;
                record.ReleaseOmegaRadPerSec = hasRelease ? ReleaseOmegaRadPerSec : double.NaN;
                record.Valid = valid;
                record.Caught = valid;
                record.InvalidReason = valid
                    ? null
                    : (string.IsNullOrEmpty(InvalidReason) ? "未绕满一整圈" : InvalidReason);
                record.StartThetaDeg = ControlMath.IsFinite(StartThetaDeg) ? StartThetaDeg : 0;
                record.StartPendulumCount = StartPendulumCount;
                record.LiftSeconds = LiftSeconds < 0 ? 0 : LiftSeconds;
                record.RevolutionSeconds = RevolutionSeconds < 0 ? 0 : RevolutionSeconds;
                record.HoldSeconds = HoldSeconds;
                record.SweepDeg = SweepDeg;
                record.MaxSweepDeg = MaxSweepDeg;
                record.LiftSign = LiftSign == 0 ? 1 : LiftSign;
                record.MaxOmegaRadPerSec = MaxOmegaRadPerSec;
                record.MaxDeviationDeg = MaxDeviationDeg;
                record.CaptureDelaySeconds = HasCaptureDelay && ControlMath.IsFinite(CaptureDelaySeconds) &&
                                             CaptureDelaySeconds >= 0
                    ? CaptureDelaySeconds
                    : double.NaN;
                // 新记录不按最大偏离推「跌倒」：整圈本来就会跑到 180° 偏离，旧推导会把有效记录误标成跌倒。
                record.Fell = hasValid
                    ? Fell
                    : (Fell || MaxDeviationDeg > CaptureSession.FallRad * (180.0 / Math.PI));
                record.KeyPoints = KeyPointDoc.ToArray(KeyPoints);
                return true;
            }

            public static List<CaptureDoc> FromList(List<CaptureRecord> records)
            {
                List<CaptureDoc> list = new List<CaptureDoc>();
                if (records == null)
                    return list;
                int skip = records.Count > PersistedExtras.CaptureRecordLimit
                    ? records.Count - PersistedExtras.CaptureRecordLimit
                    : 0;
                for (int i = skip; i < records.Count; i++)
                    list.Add(From(records[i]));
                return list;
            }
        }

        /// <summary>
        /// 整圈捕获的一个关键点。键名用短名（一次记录最多 24 点 × 20 条），单位在 <see cref="CaptureKeyPoint"/> 注释里。
        /// </summary>
        [DataContract]
        internal sealed class KeyPointDoc
        {
            [DataMember(Name = "t", Order = 0)] public double TimeSeconds;
            [DataMember(Name = "k", Order = 1)] public int Kind;
            [DataMember(Name = "theta", Order = 2)] public double ThetaDeg;
            [DataMember(Name = "sweep", Order = 3)] public double SweepDeg;
            [DataMember(Name = "w", Order = 4)] public double Omega;
            [DataMember(Name = "a", Order = 5)] public double Alpha;
            [DataMember(Name = "arm", Order = 6)] public double ArmDeg;
            [DataMember(Name = "armw", Order = 7)] public double ArmOmega;
            [DataMember(Name = "deriv", Order = 8)] public bool HasDerivative;

            public static List<KeyPointDoc> FromList(CaptureKeyPoint[] points)
            {
                if (points == null || points.Length == 0)
                    return null;
                List<KeyPointDoc> list = new List<KeyPointDoc>(points.Length);
                int count = points.Length > CaptureRecord.MaxKeyPoints + 2
                    ? CaptureRecord.MaxKeyPoints + 2
                    : points.Length;
                for (int i = 0; i < count; i++)
                {
                    KeyPointDoc d = new KeyPointDoc();
                    d.TimeSeconds = points[i].TimeSeconds;
                    d.Kind = (int)points[i].Kind;
                    d.ThetaDeg = points[i].ThetaDeg;
                    d.SweepDeg = points[i].SweepDeg;
                    d.Omega = points[i].OmegaRadPerSec;
                    d.Alpha = points[i].AlphaRadPerSec2;
                    d.ArmDeg = points[i].ArmDeg;
                    d.ArmOmega = points[i].ArmOmegaRadPerSec;
                    d.HasDerivative = points[i].HasDerivative;
                    list.Add(d);
                }
                return list;
            }

            public static CaptureKeyPoint[] ToArray(List<KeyPointDoc> docs)
            {
                if (docs == null || docs.Count == 0)
                    return null;
                List<CaptureKeyPoint> points = new List<CaptureKeyPoint>(docs.Count);
                for (int i = 0; i < docs.Count && points.Count < CaptureRecord.MaxKeyPoints + 2; i++)
                {
                    KeyPointDoc d = docs[i];
                    if (d == null) continue;
                    if (!ControlMath.IsFinite(d.TimeSeconds) || !ControlMath.IsFinite(d.ThetaDeg) ||
                        !ControlMath.IsFinite(d.SweepDeg) || !ControlMath.IsFinite(d.Omega) ||
                        !ControlMath.IsFinite(d.Alpha) || !ControlMath.IsFinite(d.ArmDeg) ||
                        !ControlMath.IsFinite(d.ArmOmega))
                        continue;
                    if (d.Kind < (int)CaptureKeyPointKind.Start || d.Kind > (int)CaptureKeyPointKind.End)
                        continue;
                    CaptureKeyPoint p = new CaptureKeyPoint();
                    p.TimeSeconds = d.TimeSeconds;
                    p.Kind = (CaptureKeyPointKind)d.Kind;
                    p.ThetaDeg = d.ThetaDeg;
                    p.SweepDeg = d.SweepDeg;
                    p.OmegaRadPerSec = d.Omega;
                    p.AlphaRadPerSec2 = d.Alpha;
                    p.ArmDeg = d.ArmDeg;
                    p.ArmOmegaRadPerSec = d.ArmOmega;
                    p.HasDerivative = d.HasDerivative;
                    points.Add(p);
                }
                return points.Count == 0 ? null : points.ToArray();
            }
        }
    }
}
