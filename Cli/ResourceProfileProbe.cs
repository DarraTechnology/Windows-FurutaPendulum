using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using FurutaPendulum.Control;

namespace FurutaPendulum.Cli
{
    /// <summary>
    /// 资源档位三档，语义与 PLC Service 的 <c>ResourceProfile</c> 一致：
    /// Dev = 开发 / 仿真，Test = HIL 测试台，Production = 现场工业机。
    /// </summary>
    internal enum ResourceProfileTier
    {
        Dev = 0,
        Test = 1,
        Production = 2,
    }

    /// <summary>
    /// 一次资源指示的解析结果。
    /// <see cref="Resolved"/>=false 表示没读到合法档位（缺文件 / 读不到 / JSON 坏 / 档位非法），
    /// 此时 <see cref="Label"/> 是回落标签、<see cref="Note"/> 必须写清回落原因 ——
    /// 状态行要让人一眼看出「这是回落，不是真的 Dev」，绝不冒充 <c>[TEST/RIG]</c> 或 <c>[PROD]</c>。
    /// </summary>
    internal sealed class ResourceIndicatorReading
    {
        public ResourceProfileTier Tier { get; set; }
        public bool Resolved { get; set; }
        /// <summary>状态行显示的环境标签：<c>[DEV/SIM]</c> / <c>[TEST/RIG]</c> / <c>[PROD]</c>。</summary>
        public string Label { get; set; }
        /// <summary>回落原因（短，进状态行）；已解析时为 null。</summary>
        public string Note { get; set; }
        /// <summary>回落原因（完整，含异常类型 / 消息 / 堆栈前 500 字，进日志）；已解析时为 null。</summary>
        public string LogDetail { get; set; }
    }

    /// <summary>
    /// 只读 <c>%ProgramData%\DarraPLC\runtime\profile.json</c>，给出「资源真假指示」标签。
    ///
    /// 路径与 schema 的唯一权威是 PLC Service 的 <c>ProfileLoader</c>（本类不另立一套）：
    /// <code>
    /// {
    ///   "profile": "DevSim" | "TestRig" | "Production",
    ///   "writtenAt": "...", "writtenBy": "...", "sku": "...", "transactionId": "..."
    /// }
    /// </code>
    /// 档位字符串按 ProfileLoader 的解析表认（含 <c>Dev</c> / <c>Test</c> 等旧写法，
    /// setup-profile.ps1 落盘写的正是旧写法）；多余字段一律忽略。
    ///
    /// 本类**只读**：改档唯一通道是 PLC 的 <c>setup-profile.ps1</c> + 重启 Service，
    /// 本程序永不写 profile.json（也不提供任何绕过写入的入口）。
    /// </summary>
    internal static class ResourceProfileProbe
    {
        /// <summary>profile.json 的硬编码路径，与 ProfileLoader.DefaultPath 逐字同源（不可配、不另立）。</summary>
        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DarraPLC", "runtime", "profile.json");
            }
        }

        /// <summary>本机 profile.json 的大小上限；超过按文件异常回落，不把整份读进内存。</summary>
        private const long MAX_PROFILE_BYTES = 64 * 1024;

        /// <summary>
        /// 读一次 profile.json 并给出资源指示。任何失败都不抛：回落 <c>[DEV/SIM]</c> + 写明原因，
        /// 由调用方决定显示与记日志（本方法自己不留日志，避免同一原因被重复计数）。
        /// </summary>
        public static ResourceIndicatorReading Read()
        {
            string path = DefaultPath;
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                // Path.Combine 拿到空 ProgramData 时会把相对路径当有效路径，这里必须挡住。
                return Fallback("无法定位系统 ProgramData 目录，profile.json 路径不可用", null);
            }

            string text;
            try
            {
                // 直接开流，不先用 FileInfo.Exists 探一次：Exists 会把「存在但没权限」也报成 false，
                // 那就把权限问题说成「未配置」，等于撒谎。这里只认 FileNotFound / DirectoryNotFound 为未配置。
                // FileShare.ReadWrite：文件正被别的工具打开也能读；BOM（UTF-8 / UTF-16）由 StreamReader 自动识别。
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length > MAX_PROFILE_BYTES)
                    {
                        return Fallback(
                            "文件异常（" + path + "；" + stream.Length.ToString(CultureInfo.InvariantCulture) +
                            " 字节，超过 " + MAX_PROFILE_BYTES.ToString(CultureInfo.InvariantCulture) +
                            " 字节上限）",
                            null);
                    }
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                    {
                        text = reader.ReadToEnd();
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return Fallback("未配置（" + path + " 不存在）", null);
            }
            catch (DirectoryNotFoundException)
            {
                return Fallback("未配置（" + path + " 不存在）", null);
            }
            catch (Exception ex)
            {
                return Fallback("读取失败（" + path + "；" + ShortError(ex) + "）", DiagText.Describe(ex));
            }

            ResourceProfileTier tier;
            string error;
            string logDetail;
            if (!TryParse(text, out tier, out error, out logDetail))
                return Fallback(error, logDetail);

            return new ResourceIndicatorReading
            {
                Tier = tier,
                Resolved = true,
                Label = ToLabel(tier),
                Note = null,
                LogDetail = null,
            };
        }

        /// <summary>解析 profile.json 文本（不取日志明细，供只需成败与原因的调用方使用）。</summary>
        public static bool TryParse(string json, out ResourceProfileTier tier, out string error)
        {
            string logDetail;
            return TryParse(json, out tier, out error, out logDetail);
        }

        /// <summary>
        /// 解析 profile.json 文本。成功时 <paramref name="tier"/> = 档位；失败时 <paramref name="error"/> = 短原因（进状态行）、
        /// <paramref name="logDetail"/> = 完整原因（异常类型 + 消息 + 堆栈前 500 字，进日志；非异常失败为 null）。
        /// 档位按回落口径置 Dev（调用方不得拿它当已解析结果）。
        ///
        /// 严格程度对齐 Service 侧（JsonDocument）：根必须是对象、必须有字符串 profile 字段、
        /// 档位必须是已知取值；截断 / 非对象根 / 尾随逗号 / 尾随内容一律判无法解析，不当已解析。
        /// </summary>
        public static bool TryParse(string json, out ResourceProfileTier tier, out string error, out string logDetail)
        {
            tier = ResourceProfileTier.Dev;
            error = null;
            logDetail = null;

            // 手改 / 脚本另存可能留下 BOM（甚至重复 BOM），归一掉再判形状。
            string text = (json ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();
            if (text.Length == 0)
            {
                error = "文件是空的";
                return false;
            }

            // 根必须是 JSON 对象（数组 / 标量即使本身合法也不是 profile.json）。
            if (text[0] != '{')
            {
                error = "根节点不是 JSON 对象";
                return false;
            }
            if (!TryValidateShape(text, out error))
                return false;

            ProfileDocument doc;
            try
            {
                using (var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(text)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ProfileDocument));
                    doc = serializer.ReadObject(stream) as ProfileDocument;
                }
            }
            catch (Exception ex)
            {
                error = "JSON 无法解析（" + ShortError(ex) + "）";
                logDetail = DiagText.Describe(ex);
                return false;
            }

            if (doc == null || string.IsNullOrWhiteSpace(doc.Profile))
            {
                error = "缺 profile 字段（根对象里必须有非空字符串 profile）";
                return false;
            }

            string raw = doc.Profile.Trim();
            if (!TryMap(raw, out tier))
            {
                error = "档位非法 \"" + raw + "\"（只认 DevSim / TestRig / Production，旧写法 Dev / Test 也认）";
                tier = ResourceProfileTier.Dev;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 结构门：整份文本必须是「单个、括号配对、无尾随逗号」的 JSON 值。
        ///
        /// 为什么需要它：DataContractJsonSerializer 比标准 JSON 宽松 —— 实测
        /// <c>{"profile":"Production",}</c>（尾随逗号）、<c>{} {"profile":"Production"}</c>（多个根）、
        /// <c>{"profile":"Production"} }</c>（多余右括号）都会被它照收。而 Service 侧用的是严格的
        /// JsonDocument，这些文件在 Service 里一律解析失败（FellBackToDefault）。不补这道门，
        /// 坏文件就会在本 CLI 显示成已解析档位，与 Service 判定相反 —— 那就是冒充。
        /// 只校验结构，取值仍走同一个序列化器（不另造第二套解析）。
        /// </summary>
        private static bool TryValidateShape(string text, out string error)
        {
            error = null;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            int rootEnd = -1;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                    case '[':
                        depth++;
                        break;
                    case '}':
                    case ']':
                        depth--;
                        if (depth < 0)
                        {
                            error = "括号不配对（多余的 " + c + "）";
                            return false;
                        }
                        if (depth == 0) rootEnd = i;
                        break;
                    case ',':
                        // 尾随逗号：逗号后（跳过 JSON 空白）紧跟 } 或 ]。
                        int next = i + 1;
                        while (next < text.Length && IsJsonSpace(text[next])) next++;
                        if (next < text.Length && (text[next] == '}' || text[next] == ']'))
                        {
                            error = "JSON 带尾随逗号（, 后直接跟 " + text[next] + "）";
                            return false;
                        }
                        break;
                }

                // 根值结束后只允许 JSON 空白；多个根 / 多余右括号都从这里被拦。
                if (rootEnd >= 0 && i > rootEnd && !IsJsonSpace(c))
                {
                    error = "整份不是单个 JSON 对象（根值之后还有多余内容）";
                    return false;
                }
            }

            if (inString)
            {
                error = "字符串没有闭合（文件被截断）";
                return false;
            }
            if (depth != 0)
            {
                error = "括号不配对（文件被截断）";
                return false;
            }
            return true;
        }

        /// <summary>JSON 只认这 4 个空白字符（标准口径，与 Service 侧 JsonDocument 一致）。</summary>
        private static bool IsJsonSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        /// <summary>档位 → 环境标签（与 PLC 侧 ResourceProfileEnvironmentLabel 同字面）。</summary>
        public static string ToLabel(ResourceProfileTier tier)
        {
            switch (tier)
            {
                case ResourceProfileTier.Production:
                    return "[PROD]";
                case ResourceProfileTier.Test:
                    return "[TEST/RIG]";
                default:
                    return "[DEV/SIM]";
            }
        }

        /// <summary>档位 → 产品名（与 PLC 侧 ToProductName 同字面），供 --json 信封使用。</summary>
        public static string ToToken(ResourceProfileTier tier)
        {
            switch (tier)
            {
                case ResourceProfileTier.Production:
                    return "Production";
                case ResourceProfileTier.Test:
                    return "TestRig";
                default:
                    return "DevSim";
            }
        }

        /// <summary>
        /// 档位字符串 → 枚举。取值表逐条对齐 PLC 侧 ResourceProfileExtensions.TryParse：
        /// Test / Dev 等旧词只作单向导入，显示仍走规范标签。
        /// </summary>
        private static bool TryMap(string raw, out ResourceProfileTier tier)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "production":
                case "prod":
                case "industrial":
                    tier = ResourceProfileTier.Production;
                    return true;
                case "testrig":
                case "test-rig":
                case "rig":
                case "test":
                case "ci":
                case "headless":
                    tier = ResourceProfileTier.Test;
                    return true;
                case "devsim":
                case "dev-sim":
                case "dev":
                case "development":
                case "developer":
                    tier = ResourceProfileTier.Dev;
                    return true;
                default:
                    tier = ResourceProfileTier.Dev;
                    return false;
            }
        }

        /// <summary>回落结果：标签恒为 [DEV/SIM]，Resolved=false，note（回落原因）必填。</summary>
        private static ResourceIndicatorReading Fallback(string note, string logDetail)
        {
            return new ResourceIndicatorReading
            {
                Tier = ResourceProfileTier.Dev,
                Resolved = false,
                Label = ToLabel(ResourceProfileTier.Dev),
                Note = note,
                LogDetail = logDetail,
            };
        }

        /// <summary>状态行用的短原因：类型名 + 消息，截到 160 字（完整堆栈走日志的 LogDetail）。</summary>
        private static string ShortError(Exception ex)
        {
            string text = ex.GetType().Name + ": " + ex.Message;
            if (text.Length > 160) text = text.Substring(0, 160) + "…";
            return text;
        }

        /// <summary>
        /// profile.json 里本程序只认的一个字段。多余字段（writtenAt / writtenBy / sku / transactionId）
        /// 由序列化器忽略 —— 与 Service 侧按名取字段的行为一致。
        /// </summary>
        [DataContract]
        private sealed class ProfileDocument
        {
            [DataMember(Name = "profile", Order = 0)]
            public string Profile { get; set; }
        }
    }
}
