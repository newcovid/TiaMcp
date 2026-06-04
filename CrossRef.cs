using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaMcp
{
    /// <summary>
    /// 交叉引用 / 依赖 / 死代码 / 调用树——排查老旧大项目"屎山"的核心。
    ///
    /// Openness V18 无原生交叉引用 API，自建"内容扫描"：每块导出文本(SCL源/图形块XML)经 %TEMP% 中转，
    /// SCL 用正则找带引号的名字(先去注释)、图形块用 XDocument 解析 &lt;Access&gt;/&lt;CallInfo&gt; 得到引用路径。
    /// 受保护(know-how)块逻辑导不出，统一识别并单列、不计入。
    /// 局限：名字级匹配，不做完整语义分析（间接调用/指针可能漏；HMI/外部调用不在 PLC 块内）。
    /// </summary>
    internal static class CrossRef
    {
        // ===================== 对外命令 =====================

        /// <summary>where-used：谁引用了 query（query 可为符号名，或带点的成员路径）。</summary>
        public static int WhereUsed(string query)
        {
            bool memberMode = query.Contains(".");
            using (var s = TiaSession.AttachFirst())
            {
                var hits = new List<KeyValuePair<string, int>>();
                var scan = ScanAllBlocks(s, (blk, kind, text) =>
                {
                    // 不把目标块自身算进去（SCL 头部 FUNCTION "X" 会出现自身名字）
                    if (string.Equals(blk.Name, query, StringComparison.OrdinalIgnoreCase)) return;
                    int cnt = CountMatches(text, kind, query, memberMode);
                    if (cnt > 0) hits.Add(new KeyValuePair<string, int>(BlockLabel(blk), cnt));
                });

                Console.WriteLine($"== \"{query}\" 的引用者（谁用了它）==");
                foreach (var h in hits.OrderByDescending(x => x.Value))
                    Console.WriteLine($"  {h.Key,-46} {h.Value} 处");
                Console.WriteLine(hits.Count == 0
                    ? "（没有任何块引用它——可能是死代码，或名字拼写不对）"
                    : $"合计 {hits.Count} 个块引用了它。");
                PrintDiagnostics(scan);
                return 0;
            }
        }

        /// <summary>返回引用了 query 的块名列表（供 delete-block 删前检查复用，与 where-used 同口径）。</summary>
        public static List<string> FindReferrers(TiaSession s, string query)
        {
            bool memberMode = query.Contains(".");
            var hits = new List<string>();
            ScanAllBlocks(s, (blk, kind, text) =>
            {
                if (string.Equals(blk.Name, query, StringComparison.OrdinalIgnoreCase)) return;
                if (CountMatches(text, kind, query, memberMode) > 0) hits.Add(blk.Name);
            });
            return hits;
        }

        /// <summary>block-deps：某块依赖了哪些符号（它向下引用谁）。</summary>
        public static int BlockDeps(string blockName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock block = s.FindBlock(blockName, out owner);
                if (block == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }
                if (TiaSession.IsKnowHowProtected(block))
                {
                    Console.WriteLine($"{blockName} 受 know-how 保护，依赖未知（需在博图解锁后才能读）。");
                    return 0;
                }

                string kind, text;
                try { text = ExportBlockText(owner, block, out kind); }
                catch (Exception ex) { Console.WriteLine("[错误] 导出失败: " + ex.Message); return 2; }

                var names = ExtractReferencedNames(text, kind);
                names.Remove(blockName);
                Console.WriteLine($"== {blockName} 依赖的符号（{names.Count} 个）==");
                foreach (var n in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine("  " + n);
                return 0;
            }
        }

        /// <summary>crossref-report：全项目交叉引用报表（每块的依赖 + 被调用者），Markdown 表，内存交付可重定向给 Git。</summary>
        public static int CrossRefReport()
        {
            using (var s = TiaSession.AttachFirst())
            {
                Index idx = BuildIndex(s);
                // 反向映射：被引用者 -> 引用它的块们
                var callers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in idx.Deps)
                    foreach (var dep in kvp.Value)
                    {
                        if (!callers.TryGetValue(dep, out var list)) { list = new List<string>(); callers[dep] = list; }
                        list.Add(kvp.Key);
                    }

                Console.WriteLine("# 交叉引用报表（内容扫描，非语义级）");
                Console.WriteLine($"扫描 {idx.Scan.Scanned} 块；受保护未读 {idx.Scan.Protected.Count}。");
                Console.WriteLine();
                Console.WriteLine("| 块 | 类型 | 依赖(向下引用) | 被调用者(向上) |");
                Console.WriteLine("|---|---|---|---|");
                foreach (var name in idx.Scan.Kind.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string type = idx.Scan.Kind[name];
                    string deps = idx.Deps.TryGetValue(name, out var d)
                        ? string.Join(" ", d.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) : "";
                    string cal = callers.TryGetValue(name, out var c)
                        ? string.Join(" ", c.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) : "";
                    Console.WriteLine($"| {name} | {type} | {deps} | {cal} |");
                }
                PrintDiagnostics(idx.Scan);
                return 0;
            }
        }

        /// <summary>find-unused：没有任何块引用的块/DB（死代码候选）。</summary>
        public static int FindUnused()
        {
            using (var s = TiaSession.AttachFirst())
            {
                Index idx = BuildIndex(s);
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var set in idx.Deps.Values)
                    foreach (var nm in set)
                    {
                        referenced.Add(nm);
                        // 背景DB被引用 => 它指向的FB也算被引用（修复：仅经SCL/iDB调用的FB被误报死代码）
                        if (idx.Scan.InstanceOf.TryGetValue(nm, out var fb)) referenced.Add(fb);
                    }

                var unusedCode = new List<string>();
                var unusedDb = new List<string>();
                foreach (var kvp in idx.Scan.Kind)
                {
                    string name = kvp.Key, type = kvp.Value;
                    if (type == "OB") continue;                       // OB 是入口，天然没人调用
                    if (idx.Scan.Protected.Contains(name)) continue;  // 受保护块不下结论
                    if (referenced.Contains(name)) continue;          // 有人引用
                    if (type.EndsWith("DB")) unusedDb.Add($"{name} ({type})");
                    else unusedCode.Add($"{name} ({type})");
                }

                Console.WriteLine("== 死代码候选（项目内没有任何块引用它）==");
                Console.WriteLine($"-- 未被引用的代码块 FB/FC：{unusedCode.Count} 个 --");
                foreach (var x in unusedCode.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) Console.WriteLine("  " + x);
                Console.WriteLine($"-- 未被引用的 DB：{unusedDb.Count} 个 --");
                foreach (var x in unusedDb.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) Console.WriteLine("  " + x);
                Console.WriteLine();
                Console.WriteLine("⚠ 仅按 PLC 块/DB 间的引用判断；HMI/外部/间接调用、以及受保护块的内部引用未计入，删除前务必人工复核。");
                PrintDiagnostics(idx.Scan);
                return 0;
            }
        }

        /// <summary>call-tree：某块向下的调用树（递归展开它调用的 FB/FC）。</summary>
        public static int CallTree(string blockName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                Index idx = BuildIndex(s);
                if (!idx.Scan.Kind.ContainsKey(blockName)) { Console.WriteLine($"找不到块: {blockName}"); return 1; }
                Console.WriteLine($"== {blockName} 调用树（向下，只展开 FB/FC）==");
                PrintCallTree(idx, blockName, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                PrintDiagnostics(idx.Scan);
                return 0;
            }
        }

        /// <summary>callers-tree：某块的"被调用树"（向上递归：谁调用/引用它，影响分析）。</summary>
        public static int CallersTree(string blockName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                Index idx = BuildIndex(s);
                if (!idx.Scan.Kind.ContainsKey(blockName)) { Console.WriteLine($"找不到块: {blockName}"); return 1; }

                // 反向映射：被引用者 -> 引用它的块们
                var callers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in idx.Deps)
                    foreach (var dep in kvp.Value)
                    {
                        if (!callers.TryGetValue(dep, out var list)) { list = new List<string>(); callers[dep] = list; }
                        list.Add(kvp.Key);
                    }

                Console.WriteLine($"== {blockName} 的被调用树（向上：谁用了它，递归）==");
                PrintCallersTree(callers, idx, blockName, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                PrintDiagnostics(idx.Scan);
                return 0;
            }
        }

        private static void PrintCallersTree(Dictionary<string, List<string>> callers, Index idx,
                                             string name, int depth, HashSet<string> stack)
        {
            string type = idx.Scan.Kind.TryGetValue(name, out var t) ? t : "?";
            string indent = new string(' ', depth * 2);
            Console.WriteLine($"{indent}{name} ({type})");

            if (!callers.ContainsKey(name)) return;
            if (!stack.Add(name)) { Console.WriteLine($"{indent}  …(递归，已展开过)"); return; }

            foreach (var c in callers[name].Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                PrintCallersTree(callers, idx, c, depth + 1, stack);

            stack.Remove(name);
        }

        // ===================== 共用扫描 =====================

        /// <summary>一次全项目扫描的结果（块类型表 + 受保护清单 + 导出失败清单 + 已扫描数）。</summary>
        private sealed class ScanResult
        {
            public Dictionary<string, string> Kind =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 所有块名 -> 类型
            public List<string> Protected = new List<string>();
            public List<KeyValuePair<string, string>> Failed = new List<KeyValuePair<string, string>>();
            public List<string> Collisions = new List<string>(); // 多 PLC 重名块（索引按名字合并会不准）
            public Dictionary<string, string> InstanceOf =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 背景DB名 -> 其FB名
            public int Scanned;
        }

        /// <summary>
        /// 遍历所有 PLC 的所有块：记录类型；受保护/导出失败归类；可扫描的块回调 onScannable(块, kind, 文本)。
        /// 不打印逐块进度（避免刷屏）。
        /// </summary>
        private static ScanResult ScanAllBlocks(TiaSession s, Action<PlcBlock, string, string> onScannable)
        {
            var r = new ScanResult();
            foreach (var kv in s.FindPlcs())
                foreach (var blk in AllBlocks(kv.Value.BlockGroup))
                {
                    // 多 PLC 项目里若出现同名块，本工具按裸名字建索引会相互覆盖 → 记下来告警。
                    if (r.Kind.ContainsKey(blk.Name)) r.Collisions.Add(blk.Name);
                    r.Kind[blk.Name] = blk.GetType().Name;

                    // 记下背景DB→其FB：SCL经iDB调用FB时文本里只出现iDB名，find-unused据此把FB也算"被引用"
                    if (r.Kind[blk.Name] == "InstanceDB")
                    {
                        try { if (blk.GetAttribute("InstanceOfName") is string io && io.Length > 0) r.InstanceOf[blk.Name] = io; }
                        catch { }
                    }

                    // 1) 先用 Openness 属性判保护（最可靠）
                    if (TiaSession.IsKnowHowProtected(blk)) { r.Protected.Add(blk.Name); continue; }

                    // 2) 导出文本
                    string kind, text;
                    try { text = ExportBlockText(kv.Value, blk, out kind); }
                    catch (Exception ex)
                    {
                        Logger.Error($"导出 {blk.Name} 失败", ex);
                        if ((ex.Message ?? "").IndexOf("know-how", StringComparison.OrdinalIgnoreCase) >= 0)
                            r.Protected.Add(blk.Name);     // SCL 受保护块 GenerateSource 抛 know-how
                        else
                            r.Failed.Add(new KeyValuePair<string, string>(blk.Name, ShortReason(ex)));
                        continue;
                    }

                    // 3) 图形代码块无网络 = 逻辑没导出来 = 受保护
                    if (kind == "xml" && IsCodeBlock(blk) && !XmlHasLogic(text)) { r.Protected.Add(blk.Name); continue; }

                    r.Scanned++;
                    onScannable(blk, kind, text);
                }
            return r;
        }

        /// <summary>所有工具统一的诊断 footer。</summary>
        private static void PrintDiagnostics(ScanResult r)
        {
            Console.WriteLine();
            string line = $"[诊断] 精确扫描 {r.Scanned} 块";
            if (r.Protected.Count > 0) line += $"；受保护未读 {r.Protected.Count} 个";
            if (r.Failed.Count > 0) line += $"；导出失败 {r.Failed.Count} 个";
            Console.WriteLine(line);
            if (r.Protected.Count > 0)
                Console.WriteLine("  受保护(know-how，内部引用未计入)：" + string.Join(", ", r.Protected));
            foreach (var f in r.Failed)
                Console.WriteLine($"  导出失败：{f.Key} —— {f.Value}");
            if (r.Collisions.Count > 0)
                Console.WriteLine("  ⚠ 检测到多 PLC 重名块，分析按名字合并、结果可能不准：" +
                    string.Join(", ", r.Collisions.Distinct()));
        }

        // ===================== 索引（find-unused / call-tree 用）=====================

        private sealed class Index
        {
            public Dictionary<string, HashSet<string>> Deps =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // 块 -> 它引用的顶层符号
            public ScanResult Scan;
        }

        private static Index BuildIndex(TiaSession s)
        {
            var idx = new Index();
            idx.Scan = ScanAllBlocks(s, (blk, kind, text) =>
            {
                var deps = ExtractReferencedNames(text, kind);
                deps.Remove(blk.Name); // 去掉自身引用
                idx.Deps[blk.Name] = deps;
            });
            return idx;
        }

        private static void PrintCallTree(Index idx, string name, int depth, HashSet<string> stack)
        {
            string type = idx.Scan.Kind.TryGetValue(name, out var t) ? t : "?";
            string indent = new string(' ', depth * 2);
            Console.WriteLine($"{indent}{name} ({type})");

            if (idx.Scan.Protected.Contains(name)) { Console.WriteLine($"{indent}  …受保护，调用未知"); return; }
            if (!idx.Deps.ContainsKey(name)) return;
            if (!stack.Add(name)) { Console.WriteLine($"{indent}  …(递归，已展开过)"); return; }

            foreach (var dep in idx.Deps[name].OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                if (idx.Scan.Kind.TryGetValue(dep, out var dt) && (dt == "FB" || dt == "FC"))
                    PrintCallTree(idx, dep, depth + 1, stack);

            stack.Remove(name);
        }

        // ===================== 导出 + 解析 =====================

        private static IEnumerable<PlcBlock> AllBlocks(PlcBlockGroup group)
        {
            foreach (PlcBlock b in group.Blocks) yield return b;
            foreach (PlcBlockGroup sub in group.Groups)
                foreach (var b in AllBlocks(sub)) yield return b;
        }

        private static string BlockLabel(PlcBlock b)
        {
            string lang; try { lang = b.ProgrammingLanguage.ToString(); } catch { lang = "?"; }
            return $"[{b.GetType().Name}/{lang}] {b.Name}";
        }

        /// <summary>导出块文本：SCL/STL→源码(GenerateSource)；其它图形语言→SimaticML XML(Export)。经 %TEMP% 明文中转。</summary>
        private static string ExportBlockText(PlcSoftware owner, PlcBlock block, out string kind)
        {
            string lang; try { lang = block.ProgrammingLanguage.ToString(); } catch { lang = ""; }
            bool isStl = lang.Equals("STL", StringComparison.OrdinalIgnoreCase);
            bool textual = isStl || lang.Equals("SCL", StringComparison.OrdinalIgnoreCase);

            if (textual)
            {
                kind = "scl";
                string tmp = IoUtil.NewTempFile(isStl ? ".awl" : ".scl");
                try
                {
                    owner.ExternalSourceGroup.GenerateSource(
                        new List<PlcBlock> { block }, new FileInfo(tmp), GenerateOptions.None);
                    return IoUtil.ReadPlaintext(tmp);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
            {
                kind = "xml";
                string tmp = IoUtil.NewTempFile(".xml");
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp); // Export 要求目标不存在
                    block.Export(new FileInfo(tmp), ExportOptions.None);
                    return IoUtil.ReadPlaintext(tmp);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        /// <summary>统计 text 里对 query 的引用次数（SCL 文本正则；XML 解析路径）。</summary>
        private static int CountMatches(string text, string kind, string query, bool memberMode)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            if (kind == "xml")
            {
                // 路径==query 或以 query. 开头 → 命中（符号级/成员级统一、精确）
                int c = 0;
                foreach (var r in ExtractXmlReferences(text))
                    if (r.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || r.StartsWith(query + ".", StringComparison.OrdinalIgnoreCase))
                        c++;
                return c;
            }

            // SCL/STL：先去注释/字符串，避免里面的名字误报
            var opt = RegexOptions.IgnoreCase;
            string code = StripSclNonCode(text);
            if (!memberMode)
                return Regex.Matches(code, "\"" + Regex.Escape(query) + "\"", opt).Count;

            var segs = query.Split('.');
            string p = "\"" + Regex.Escape(segs[0]) + "\""
                     + string.Concat(segs.Skip(1).Select(x => "\\." + Regex.Escape(x)))
                     + "(?![A-Za-z0-9_])";
            return Regex.Matches(code, p, opt).Count;
        }

        /// <summary>抽出块依赖的顶层符号（块/DB/变量名）。</summary>
        private static HashSet<string> ExtractReferencedNames(string text, string kind)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return set;
            if (kind == "xml")
            {
                foreach (var r in ExtractXmlReferences(text))
                {
                    int dot = r.IndexOf('.');
                    set.Add(dot > 0 ? r.Substring(0, dot) : r); // 顶层符号
                }
            }
            else
            {
                // SCL 引用都写成 "名字"，成员 .x.y 不带引号 → 天然只取顶层。先去注释/字符串。
                foreach (Match m in Regex.Matches(StripSclNonCode(text), "\"([^\"]+)\""))
                    set.Add(m.Groups[1].Value);
            }
            return set;
        }

        /// <summary>解析 SimaticML：每个 &lt;Access&gt;&lt;Symbol&gt; 的 Component 链拼成 a.b.c；&lt;CallInfo Name&gt; 是被调块、其 Instance 是背景DB。</summary>
        private static List<string> ExtractXmlReferences(string xml)
        {
            var refs = new List<string>();
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return refs; }

            foreach (var el in doc.Descendants())
            {
                string ln = el.Name.LocalName;
                if (ln == "Access")
                {
                    var symbol = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
                    string path = ComponentsPath(symbol);
                    if (path.Length > 0) refs.Add(path);
                }
                else if (ln == "CallInfo")
                {
                    string name = (string)el.Attribute("Name");
                    if (!string.IsNullOrEmpty(name)) refs.Add(name);
                    var inst = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                    string ipath = ComponentsPath(inst);
                    if (ipath.Length > 0) refs.Add(ipath);
                }
            }
            return refs;
        }

        private static string ComponentsPath(XElement container)
        {
            if (container == null) return "";
            var names = container.Elements()
                .Where(e => e.Name.LocalName == "Component")
                .Select(e => (string)e.Attribute("Name"))
                .Where(n => !string.IsNullOrEmpty(n));
            return string.Join(".", names);
        }

        /// <summary>
        /// 去掉 SCL 中"非代码"文本——块注释 (* *)、字符串字面量 '...'、行注释 //，
        /// 避免它们里面出现的 "名字" 被误当成引用。顺序：块注释→字符串→行注释
        /// （先去字符串，避免字符串里的 // 被当行注释；'' 是 SCL 里单引号的转义）。
        /// </summary>
        private static string StripSclNonCode(string scl)
        {
            if (string.IsNullOrEmpty(scl)) return scl;
            // 顺序：块注释→行注释→字符串。先去注释，避免注释里落单的单引号(it's / motor's)
            // 与后文某个单引号配对、把中间的真实代码("块名"引用)整段吞掉而漏报引用。
            scl = Regex.Replace(scl, @"\(\*.*?\*\)", " ", RegexOptions.Singleline); // 块注释，可跨行
            scl = Regex.Replace(scl, @"//[^\r\n]*", " ");                            // 行注释
            scl = Regex.Replace(scl, @"'(?:[^'\r\n]|'')*'", " ");                    // 字符串字面量 '...'（不跨行，贴合 SCL 语义）
            return scl;
        }

        private static string ShortReason(Exception ex)
        {
            string m = ex.Message ?? "";
            if (m.IndexOf("know-how", StringComparison.OrdinalIgnoreCase) >= 0)
                return "无法导出：可能受专有技术保护(know-how)，或含非 SCL/STL 内容";
            int cut = m.IndexOfAny(new[] { '\r', '\n' });
            return cut > 0 ? m.Substring(0, cut) : m;
        }

        private static bool IsCodeBlock(PlcBlock b)
        {
            string t = b.GetType().Name;
            return t == "OB" || t == "FB" || t == "FC";
        }

        /// <summary>正常代码块的 SimaticML 含 &lt;SW.Blocks.CompileUnit&gt;(网络)；受保护块没有。</summary>
        private static bool XmlHasLogic(string xml)
        {
            return xml.IndexOf("SW.Blocks.CompileUnit", StringComparison.Ordinal) >= 0;
        }
    }
}
