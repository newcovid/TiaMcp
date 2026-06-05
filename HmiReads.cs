using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;             // ExportOptions
using Siemens.Engineering.Hmi.Screen;  // Screen, ScreenSystemFolder, ScreenUserFolder
using Siemens.Engineering.Hmi.Tag;     // TagTable, TagSystemFolder, TagUserFolder, Tag
using Siemens.Engineering.Hmi.Communication; // Connection
using Siemens.Engineering.Hmi.TextGraphicList; // TextList, GraphicList
using HmiTarget = Siemens.Engineering.Hmi.HmiTarget;

namespace TiaMcp
{
    /// <summary>
    /// HMI 读命令集(经典 HmiTarget,非 Unified HmiSoftware)。只读 + 导出。
    /// 全程沿用铁律:只读、逐项 try/catch、导出经 %TEMP%、结果走 stdout、诊断走 stderr。
    /// </summary>
    internal static class HmiReads
    {
        // ===== hmi-list:设备 + 计数 + 名字地图 =====
        public static int HmiList()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }

                foreach (var kv in hmis)
                {
                    HmiTarget hmi = kv.Value;
                    var screens = new List<Screen>(); CollectScreens(hmi.ScreenFolder, screens);
                    var tables = new List<TagTable>(); CollectTagTables(hmi.TagFolder, tables);
                    int tagCount = tables.Sum(t => CountTags(t));
                    int connCount = 0; foreach (Connection c in hmi.Connections) connCount++;

                    Console.WriteLine($"==== HMI 设备: {kv.Key} ====");
                    Console.WriteLine($"  画面 {screens.Count} | 变量表 {tables.Count} | 变量 {tagCount} | 连接 {connCount}");
                    Console.WriteLine("  变量表: " + string.Join(", ", tables.Select(t => t.Name)));
                    Console.WriteLine("  画面: " + string.Join(", ", screens.Select(x => x.Name)));
                    Console.WriteLine();
                }
                return 0;
            }
        }

        private static int CountTags(TagTable t)
        {
            int n = 0; foreach (Tag tag in t.Tags) n++; return n;
        }

        // ===== 共享遍历 =====
        private static void CollectScreens(ScreenSystemFolder root, List<Screen> acc)
        {
            foreach (Screen sc in root.Screens) acc.Add(sc);
            foreach (ScreenUserFolder f in root.Folders) CollectScreensU(f, acc);
        }
        private static void CollectScreensU(ScreenUserFolder folder, List<Screen> acc)
        {
            foreach (Screen sc in folder.Screens) acc.Add(sc);
            foreach (ScreenUserFolder f in folder.Folders) CollectScreensU(f, acc);
        }
        private static void CollectTagTables(TagSystemFolder root, List<TagTable> acc)
        {
            foreach (TagTable t in root.TagTables) acc.Add(t);
            foreach (TagUserFolder f in root.Folders) CollectTagTablesU(f, acc);
        }
        private static void CollectTagTablesU(TagUserFolder folder, List<TagTable> acc)
        {
            foreach (TagTable t in folder.TagTables) acc.Add(t);
            foreach (TagUserFolder f in folder.Folders) CollectTagTablesU(f, acc);
        }

        // ===== hmi-read-screens:画面文件夹树 =====
        public static int HmiReadScreens()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    Console.WriteLine($"==== HMI 设备: {kv.Key} · 画面树 ====");
                    // 经典 Screen 只暴露 Name 属性，画面号只在导出 XML 里，故树只列名字
                    PrintScreenSysFolder(kv.Value.ScreenFolder, "");
                    Console.WriteLine();
                }
                return 0;
            }
        }
        private static void PrintScreenSysFolder(ScreenSystemFolder root, string indent)
        {
            foreach (Screen sc in root.Screens)
                Console.WriteLine($"{indent}  {Safe(() => sc.Name)}");
            foreach (ScreenUserFolder f in root.Folders)
            {
                Console.WriteLine($"{indent}[{f.Name}]");
                PrintScreenUserFolder(f, indent + "  ");
            }
        }
        private static void PrintScreenUserFolder(ScreenUserFolder folder, string indent)
        {
            foreach (Screen sc in folder.Screens)
                Console.WriteLine($"{indent}  {Safe(() => sc.Name)}");
            foreach (ScreenUserFolder f in folder.Folders)
            {
                Console.WriteLine($"{indent}[{f.Name}]");
                PrintScreenUserFolder(f, indent + "  ");
            }
        }

        // ===== hmi-read-templates: 模板画面(母版)树。模板=ScreenTemplateFolder，与普通画面分开存。 =====
        public static int HmiReadTemplates()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    Console.WriteLine($"==== HMI 设备: {kv.Key} · 模板画面(母版)树 ====");
                    var templates = new List<ScreenTemplate>(); CollectTemplates(kv.Value.ScreenTemplateFolder, templates);
                    foreach (var t in templates) Console.WriteLine($"  {Safe(() => t.Name)}");
                    Console.WriteLine($"  共 {templates.Count} 个模板。（模板=页眉/页脚/永久区母版，被普通画面继承；详情/控件用 hmi-export-template 导 XML）");
                    Console.WriteLine();
                }
                return 0;
            }
        }
        private static void CollectTemplates(ScreenTemplateSystemFolder root, List<ScreenTemplate> acc)
        {
            foreach (ScreenTemplate t in root.ScreenTemplates) acc.Add(t);
            foreach (ScreenTemplateUserFolder f in root.Folders) CollectTemplatesU(f, acc);
        }
        private static void CollectTemplatesU(ScreenTemplateUserFolder folder, List<ScreenTemplate> acc)
        {
            foreach (ScreenTemplate t in folder.ScreenTemplates) acc.Add(t);
            foreach (ScreenTemplateUserFolder f in folder.Folders) CollectTemplatesU(f, acc);
        }

        // ===== hmi-read-connections =====
        public static int HmiReadConnections()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    Console.WriteLine($"==== HMI 设备: {kv.Key} · 连接 ====");
                    int n = 0;
                    foreach (Connection c in kv.Value.Connections)
                    {
                        Console.WriteLine($"  {Safe(() => c.Name)}");
                        n++;
                    }
                    Console.WriteLine($"  共 {n} 个连接。（经典 Connection 仅暴露 Name；伙伴/驱动详情见 hmi-export-all 的连接 XML）");
                    Console.WriteLine();
                }
                return 0;
            }
        }

        // ===== hmi-read-tags [--table 名] [--filter 子串] =====
        // 经典 Tag 只暴露 Name 属性，数据类型/连接/地址只在 TagTable.Export 的 XML 里，故导出+解析。
        public static int HmiReadTags(string tableFilter, string nameFilter)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    Console.WriteLine($"==== HMI 设备: {kv.Key} · 变量 ====");
                    var tables = new List<TagTable>(); CollectTagTables(kv.Value.TagFolder, tables);
                    int shown = 0;
                    foreach (TagTable t in tables)
                    {
                        if (tableFilter != null && t.Name.IndexOf(tableFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Console.WriteLine($"  [表] {t.Name}");
                        string tmp = IoUtil.NewTempFile(".xml");
                        try
                        {
                            if (File.Exists(tmp)) File.Delete(tmp);
                            t.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                            string xml = IoUtil.ReadPlaintext(tmp);
                            shown += PrintTagsFromXml(xml, nameFilter);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("    [表导出失败] " + ex.Message);
                            Logger.Error("read-tags export " + t.Name, ex);
                        }
                        finally { try { File.Delete(tmp); } catch { } }
                    }
                    Console.WriteLine($"  -- 显示 {shown} 个变量 --");
                    Console.WriteLine();
                }
                return 0;
            }
        }

        // 解析变量表导出 XML 里的变量。变量元素=<Hmi.Tag.Tag>。
        // 经典 HMI 无显式 DataType 元素:类型由 <Coding>(如 IEEE754Float=Real) 表示;
        // LinkList 下 <Connection> 是 HMI 连接、<ControllerTag> 是绑定的 PLC 变量。
        private static int PrintTagsFromXml(string xml, string nameFilter)
        {
            var doc = XDocument.Parse(xml);
            var tagEls = doc.Descendants().Where(e => e.Name.LocalName == "Hmi.Tag.Tag").ToList();
            int shown = 0;
            foreach (var te in tagEls)
            {
                string name = ChildText(te, "Name") ?? "";
                if (nameFilter != null && name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string coding = ChildText(te, "Coding") ?? "?";       // 编码≈类型:IEEE754Float=Real 等
                string conn = LinkName(te, "Connection") ?? "-";
                string plc = LinkName(te, "ControllerTag") ?? "-";     // 绑定的 PLC 变量
                Console.WriteLine($"      {name,-38} {coding,-14} conn={conn,-10} ←PLC {plc}");
                shown++;
            }
            return shown;
        }

        // 取 <Hmi.Tag.Tag> 下某 LinkList 子节点(Connection/ControllerTag) 的 Name
        private static string LinkName(XElement tagEl, string linkLocalName)
        {
            var el = tagEl.Descendants().FirstOrDefault(x => x.Name.LocalName == linkLocalName);
            return el != null ? ChildText(el, "Name") : null;
        }

        // 取某元素下第一个 local-name==key 的后代文本(非空)
        private static string ChildText(XElement e, string key)
        {
            var c = e.Descendants().FirstOrDefault(x => x.Name.LocalName == key && !string.IsNullOrWhiteSpace(x.Value));
            return c != null ? c.Value.Trim() : null;
        }

        // ===== hmi-read-screen <名>:单张画面控件摘要 + 可识别的绑定变量 =====
        public static int HmiReadScreen(string screenName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    var screens = new List<Screen>(); CollectScreens(kv.Value.ScreenFolder, screens);
                    Screen target = screens.FirstOrDefault(x => string.Equals(x.Name, screenName, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;

                    Console.WriteLine($"==== {kv.Key} · 画面 {target.Name} ====");
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        target.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string xml = IoUtil.ReadPlaintext(tmp);
                        PrintScreenObjects(xml);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                    return 0;
                }
                Console.WriteLine($"找不到画面: {screenName}");
                return 1;
            }
        }

        private static void PrintScreenObjects(string xml)
        {
            var doc = XDocument.Parse(xml);
            var objs = doc.Descendants()
                          .Where(e => e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
                                      && e.Name.LocalName != "Hmi.Screen.Screen")
                          .ToList();
            Console.WriteLine($"  画面对象 {objs.Count} 个:");
            bool diagDumped = false;
            foreach (var o in objs)
            {
                string type = o.Name.LocalName.Replace("Hmi.Screen.", "");
                string oname = ChildText(o, "ObjectName") ?? ChildText(o, "Name") ?? "";
                string tagRef = FindTagRef(o);
                string bind = tagRef != null ? "  →变量 " + tagRef : "";
                Console.WriteLine($"    {type,-26} {oname,-24}{bind}");
                if (!diagDumped && tagRef == null)
                {
                    string xmlText = o.ToString();
                    Console.Error.WriteLine("[diag] 对象结构样例(" + type + "):\n" + xmlText.Substring(0, Math.Min(700, xmlText.Length)));
                    diagDumped = true;
                }
            }
        }

        // 找绑定变量:后代里 local-name 含 "Tag" 且带 <Name> 子节点的元素，取其 Name(避免拼上兄弟布尔值)。
        private static string FindTagRef(XElement e)
        {
            var tagEl = e.Descendants().FirstOrDefault(x => x.Name.LocalName.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0
                                                            && x.Elements().Any(c => c.Name.LocalName == "Name"));
            return tagEl != null ? ChildText(tagEl, "Name") : null;
        }

        // ===== hmi-export-all <目录>:画面+变量表+连接+文本/图形列表 各导 XML 进目录 =====
        public static int HmiExportAll(string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                Directory.CreateDirectory(outDir);

                foreach (var kv in hmis)
                {
                    HmiTarget hmi = kv.Value;
                    string baseDir = Path.Combine(outDir, MakeSafe(kv.Key));
                    int okScreen = 0, okTemplate = 0, okTable = 0, okConn = 0, okList = 0, skip = 0;

                    var screens = new List<Screen>(); CollectScreens(hmi.ScreenFolder, screens);
                    foreach (Screen sc in screens)
                        if (TryExport(fi => sc.Export(fi, ExportOptions.WithDefaults), Path.Combine(baseDir, "Screens", MakeSafe(sc.Name) + ".xml"))) okScreen++; else skip++;

                    var templates = new List<ScreenTemplate>(); CollectTemplates(hmi.ScreenTemplateFolder, templates);
                    foreach (ScreenTemplate t in templates)
                        if (TryExport(fi => t.Export(fi, ExportOptions.WithDefaults), Path.Combine(baseDir, "Templates", MakeSafe(t.Name) + ".xml"))) okTemplate++; else skip++;

                    var tables = new List<TagTable>(); CollectTagTables(hmi.TagFolder, tables);
                    foreach (TagTable t in tables)
                        if (TryExport(fi => t.Export(fi, ExportOptions.WithDefaults), Path.Combine(baseDir, "Tags", MakeSafe(t.Name) + ".xml"))) okTable++; else skip++;

                    foreach (Connection c in hmi.Connections)
                        if (TryExport(fi => c.Export(fi, ExportOptions.WithDefaults), Path.Combine(baseDir, "Connections", MakeSafe(c.Name) + ".xml"))) okConn++;
                        else { skip++; Console.WriteLine($"  跳过连接 {Safe(() => c.Name)}（集成连接不支持导出）。"); }

                    foreach (TextList tl in hmi.TextLists)
                        if (TryExport(fi => tl.Export(fi, ExportOptions.WithDefaults), Path.Combine(baseDir, "Lists", "Text_" + MakeSafe(tl.Name) + ".xml"))) okList++; else skip++;
                    foreach (GraphicList gl in hmi.GraphicLists)
                        if (TryExport(fi => gl.Export(fi, ExportOptions.WithDefaults), Path.Combine(baseDir, "Lists", "Graphic_" + MakeSafe(gl.Name) + ".xml"))) okList++; else skip++;

                    Console.WriteLine($"==== {kv.Key}: 画面 {okScreen} / 模板 {okTemplate} / 变量表 {okTable} / 连接 {okConn} / 列表 {okList}；跳过 {skip} ====");
                }
                Console.WriteLine($"导出完成 -> {Path.GetFullPath(outDir)}");
                Console.WriteLine("注意：本机全盘扫描可能稍后加密这些文件，请尽快 git add/commit。");
                return 0;
            }
        }

        // 导出经 %TEMP%(校验明文)再写明文到目标;失败返回 false
        private static bool TryExport(Action<FileInfo> exportFn, string targetPath)
        {
            string tmp = IoUtil.NewTempFile(".xml");
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                exportFn(new FileInfo(tmp));
                string xml = IoUtil.ReadPlaintext(tmp);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.WriteAllText(targetPath, xml, new System.Text.UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Logger.Error("导出失败 " + targetPath, ex); return false; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static string MakeSafe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ===== 变量画面使用分析(hmi-find-unused-tags / hmi-tag-usage 共用) =====
        private sealed class TagDecl { public string Name; public string Table; public string Plc; }
        private sealed class TagRef { public string TagName; public string Container; public string ContainerKind; public string ObjectType; public string ObjectName; }

        // 解析所有变量表导出 XML,取声明变量全集 + 绑定的 PLC 变量。只读,经 %TEMP%。
        private static List<TagDecl> CollectDeclaredTags(HmiTarget hmi)
        {
            var result = new List<TagDecl>();
            var tables = new List<TagTable>(); CollectTagTables(hmi.TagFolder, tables);
            foreach (TagTable t in tables)
            {
                string tmp = IoUtil.NewTempFile(".xml");
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    t.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                    var doc = XDocument.Parse(IoUtil.ReadPlaintext(tmp));
                    foreach (var te in doc.Descendants().Where(e => e.Name.LocalName == "Hmi.Tag.Tag"))
                    {
                        string name = ChildText(te, "Name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        result.Add(new TagDecl { Name = name, Table = t.Name, Plc = LinkName(te, "ControllerTag") ?? "-" });
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[diag] 变量表导出失败 {Safe(() => t.Name)}: {ex.Message}"); Logger.Error("tag-usage tags export", ex); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            return result;
        }

        // 一次性导出该 HMI 全部画面+模板,解析出 变量名(OrdinalIgnoreCase) -> 引用列表。
        private static Dictionary<string, List<TagRef>> BuildTagUsageIndex(HmiTarget hmi, out int screenCount, out int templateCount)
        {
            var index = new Dictionary<string, List<TagRef>>(StringComparer.OrdinalIgnoreCase);
            var screens = new List<Screen>(); CollectScreens(hmi.ScreenFolder, screens);
            var templates = new List<ScreenTemplate>(); CollectTemplates(hmi.ScreenTemplateFolder, templates);
            screenCount = screens.Count; templateCount = templates.Count;
            foreach (Screen sc in screens) ScanContainer(fi => sc.Export(fi, ExportOptions.WithDefaults), Safe(() => sc.Name), "画面", index);
            foreach (ScreenTemplate t in templates) ScanContainer(fi => t.Export(fi, ExportOptions.WithDefaults), Safe(() => t.Name), "模板", index);
            return index;
        }

        // 导出单容器 XML(经 %TEMP% 校验明文)→ 解析全部变量引用 → 灌入 index。零引用/失败走 stderr [diag]。
        private static void ScanContainer(Action<FileInfo> exportFn, string container, string kind, Dictionary<string, List<TagRef>> index)
        {
            string xml; string tmp = IoUtil.NewTempFile(".xml");
            try { if (File.Exists(tmp)) File.Delete(tmp); exportFn(new FileInfo(tmp)); xml = IoUtil.ReadPlaintext(tmp); }
            catch (Exception ex) { Console.Error.WriteLine($"[diag] 导出失败 {kind} {container}: {ex.Message}"); Logger.Error("tag-usage export " + container, ex); return; }
            finally { try { File.Delete(tmp); } catch { } }

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex) { Console.Error.WriteLine($"[diag] 解析失败 {kind} {container}: {ex.Message}"); return; }

            var refs = CollectTagRefs(doc, container, kind);
            foreach (var r in refs)
            {
                if (!index.TryGetValue(r.TagName, out var list)) { list = new List<TagRef>(); index[r.TagName] = list; }
                list.Add(r);
            }
            if (refs.Count == 0) Console.Error.WriteLine($"[diag] 零引用容器: {kind} {container}");
        }

        // 收集容器 XML 里全部变量引用:后代 local-name 含 "Tag" 且带直接子 <Name>;归属最近 Hmi.Screen.* 祖先对象。
        private static List<TagRef> CollectTagRefs(XDocument doc, string container, string kind)
        {
            var result = new List<TagRef>();
            var refEls = doc.Descendants().Where(x =>
                x.Name.LocalName.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0 &&
                x.Elements().Any(c => c.Name.LocalName == "Name"));
            foreach (var refEl in refEls)
            {
                string tagName = ChildText(refEl, "Name");
                if (string.IsNullOrWhiteSpace(tagName)) continue;
                var obj = refEl.Ancestors().FirstOrDefault(a =>
                    a.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal) && a.Name.LocalName != "Hmi.Screen.Screen");
                string objType = obj != null ? obj.Name.LocalName.Replace("Hmi.Screen.", "") : "";
                string objName = obj != null ? (ChildText(obj, "ObjectName") ?? ChildText(obj, "Name") ?? "") : "";
                result.Add(new TagRef { TagName = tagName, Container = container, ContainerKind = kind, ObjectType = objType, ObjectName = objName });
            }
            return result;
        }

        // ===== hmi-find-unused-tags:声明变量 vs 画面/模板实际引用,列孤儿 =====
        public static int HmiFindUnusedTags()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    HmiTarget hmi = kv.Value;
                    var declared = CollectDeclaredTags(hmi);
                    var index = BuildTagUsageIndex(hmi, out int screenCount, out int templateCount);
                    var orphans = declared.Where(d => !index.ContainsKey(d.Name)).OrderBy(d => d.Table).ThenBy(d => d.Name).ToList();
                    int inUse = declared.Count - orphans.Count;

                    Console.WriteLine($"==== HMI 设备: {kv.Key} · 变量画面使用审计(扫 画面+模板) ====");
                    Console.WriteLine($"  声明变量 {declared.Count} | 在用 {inUse} | 孤儿 {orphans.Count}");
                    Console.WriteLine($"  扫描范围: {screenCount} 画面 + {templateCount} 模板");
                    Console.WriteLine("  -- 孤儿变量(未被任何画面/模板引用) --");
                    foreach (var o in orphans) Console.WriteLine($"    [表 {o.Table}] {o.Name,-40} ←PLC {o.Plc}");
                    Console.WriteLine($"  (共 {orphans.Count} 个孤儿)");
                    Console.WriteLine("  ⚠ 边界: \"未引用\" 仅指画面/模板;报警(离散/模拟报警触发变量)、调度器、变量多路复用本工具不扫描 —— 未在用 != 可安全删,删前在博图确认。");

                    var sb = new System.Text.StringBuilder();
                    foreach (var d in declared.OrderBy(d => d.Table).ThenBy(d => d.Name))
                    {
                        if (index.TryGetValue(d.Name, out var refs))
                        {
                            var distinct = refs.Select(r => $"{r.ContainerKind} {r.Container}").Distinct().ToList();
                            sb.AppendLine($"[在用] {d.Name}  ({distinct.Count} 处): " + string.Join(", ", distinct));
                        }
                        else sb.AppendLine($"[孤儿] {d.Name}  (表 {d.Table}, PLC {d.Plc})");
                    }
                    string map = sb.ToString();
                    if (map.Length > 8000)
                    {
                        string outPath = IoUtil.NewTempFile(".txt");
                        File.WriteAllText(outPath, map, new System.Text.UTF8Encoding(false));
                        Console.WriteLine($"  [大产物] 全量\"变量->引用画面\"映射 已写 {outPath} (charCount={map.Length})");
                    }
                    else { Console.WriteLine("  -- 全量映射 --"); Console.Write(map); }
                    Console.WriteLine();
                }
                return 0;
            }
        }

        // ===== hmi-tag-usage <变量名>:单变量反查被哪些画面/模板/控件引用 =====
        public static int HmiTagUsage(string tagName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                bool found = false;
                foreach (var kv in hmis)
                {
                    HmiTarget hmi = kv.Value;
                    var declared = CollectDeclaredTags(hmi);
                    var decl = declared.FirstOrDefault(d => string.Equals(d.Name, tagName, StringComparison.OrdinalIgnoreCase));
                    if (decl == null) continue;
                    found = true;

                    var index = BuildTagUsageIndex(hmi, out _, out _);
                    Console.WriteLine($"==== {kv.Key} · 变量使用 \"{decl.Name}\" ====");
                    Console.WriteLine($"  声明于变量表: {decl.Table}   (绑定 PLC: {decl.Plc})");
                    if (index.TryGetValue(decl.Name, out var refs) && refs.Count > 0)
                    {
                        var rows = refs.Select(r => new { r.ContainerKind, r.Container, r.ObjectType, r.ObjectName }).Distinct().ToList();
                        Console.WriteLine($"  被 {rows.Count} 处引用:");
                        foreach (var r in rows)
                        {
                            string obj = string.IsNullOrEmpty(r.ObjectType) ? "(容器级)" : $"{r.ObjectType} {r.ObjectName}";
                            Console.WriteLine($"    {r.ContainerKind} {r.Container,-22} {obj}");
                        }
                        Console.WriteLine("  状态: 在用");
                    }
                    else
                    {
                        Console.WriteLine("  被 0 处引用 -> 孤儿");
                        Console.WriteLine("  ⚠ 边界: 仅扫画面/模板;报警/调度器/多路复用未扫,删前博图确认。");
                    }
                    Console.WriteLine();
                }
                if (!found) { Console.WriteLine($"找不到 HMI 变量: {tagName}（提示: 用 hmi-read-tags 查准确名）"); return 1; }
                return 0;
            }
        }

        // ===== hmi-read-screen-layout <名>:单画面视觉布局信息(位置/大小/颜色/字体等) =====
        public static int HmiReadScreenLayout(string screenName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    var screens = new List<Screen>(); CollectScreens(kv.Value.ScreenFolder, screens);
                    Screen target = screens.FirstOrDefault(x => string.Equals(x.Name, screenName, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;

                    Console.WriteLine($"==== {kv.Key} · 画面布局 {target.Name} ====");
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        target.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string xml = IoUtil.ReadPlaintext(tmp);
                        PrintScreenLayout(xml, target.Name);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                    return 0;
                }
                Console.WriteLine($"找不到画面: {screenName}");
                return 1;
            }
        }

        // ===== hmi-read-template-layout <名>:模板画面(母版)视觉布局信息 =====
        public static int HmiReadTemplateLayout(string templateName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 0; }
                foreach (var kv in hmis)
                {
                    var templates = new List<ScreenTemplate>(); CollectTemplates(kv.Value.ScreenTemplateFolder, templates);
                    ScreenTemplate target = templates.FirstOrDefault(x => string.Equals(x.Name, templateName, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;

                    Console.WriteLine($"==== {kv.Key} · 模板布局 {target.Name} ====");
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        target.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string xml = IoUtil.ReadPlaintext(tmp);
                        PrintScreenLayout(xml, target.Name);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                    return 0;
                }
                Console.WriteLine($"找不到模板: {templateName}");
                return 1;
            }
        }

        // 解析画面XML，提取视觉布局信息
        private static void PrintScreenLayout(string xml, string screenName)
        {
            var doc = XDocument.Parse(xml);
            // 支持普通画面和模板画面两种根元素
            var screenEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.Screen")
                        ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.ScreenTemplate");
            if (screenEl == null) { Console.WriteLine("  [错误] 无法解析画面根元素"); return; }

            // 画面基本信息
            string width = ChildText(screenEl, "Width") ?? "?";
            string height = ChildText(screenEl, "Height") ?? "?";
            string backColor = ChildText(screenEl, "BackColor") ?? "?";
            string screenNumber = ChildText(screenEl, "ScreenNumber") ?? "?";

            Console.WriteLine($"  画面: {screenName} (#{screenNumber})");
            Console.WriteLine($"  尺寸: {width} x {height} (DIU)");
            Console.WriteLine($"  背景色: {backColor}");
            Console.WriteLine();

            // 提取所有画面对象
            var objs = doc.Descendants()
                          .Where(e => e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
                                      && e.Name.LocalName != "Hmi.Screen.Screen")
                          .ToList();

            Console.WriteLine($"  控件总数: {objs.Count}");
            Console.WriteLine();

            // 控件摘要列表（每行一个控件，结构化输出）
            Console.WriteLine("  === 控件摘要 ===");
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                string type = o.Name.LocalName.Replace("Hmi.Screen.", "");
                string oname = ChildText(o, "ObjectName") ?? ChildText(o, "Name") ?? "?";
                string left = ChildText(o, "Left") ?? ChildText(o, "X") ?? "?";
                string top = ChildText(o, "Top") ?? ChildText(o, "Y") ?? "?";
                string w = ChildText(o, "Width") ?? "?";
                string h = ChildText(o, "Height") ?? "?";
                string backClr = ChildText(o, "BackColor") ?? ChildText(o, "BackgroundColor") ?? "-";

                Console.WriteLine($"  [{i + 1}] {type} \"{oname}\"");
                Console.WriteLine($"      位置=({left},{top}) 大小={w}×{h} 背景色={backClr}");
            }
            Console.WriteLine();

            // 提取详细属性（按类型分组）
            Console.WriteLine("  === 详细属性 ===");
            var grouped = objs.GroupBy(e => e.Name.LocalName.Replace("Hmi.Screen.", "")).OrderBy(g => g.Key);
            foreach (var group in grouped)
            {
                Console.WriteLine($"\n  [{group.Key}] ({group.Count()} 个)");
                foreach (var o in group)
                {
                    string oname = ChildText(o, "ObjectName") ?? ChildText(o, "Name") ?? "?";
                    Console.WriteLine($"    ── {oname} ──");

                    // 位置和大小
                    string left = ChildText(o, "Left") ?? ChildText(o, "X");
                    string top = ChildText(o, "Top") ?? ChildText(o, "Y");
                    string w = ChildText(o, "Width");
                    string h = ChildText(o, "Height");
                    if (left != null || top != null) Console.WriteLine($"       位置: ({left ?? "?"}, {top ?? "?"})");
                    if (w != null || h != null) Console.WriteLine($"       大小: {w ?? "?"} × {h ?? "?"}");

                    // 颜色属性
                    string backClr = ChildText(o, "BackColor") ?? ChildText(o, "BackgroundColor");
                    string altBackClr = ChildText(o, "AlternateBackColor");
                    string foreClr = ChildText(o, "ForeColor") ?? ChildText(o, "ForegroundColor");
                    string borderClr = ChildText(o, "BorderColor");
                    string altBorderClr = ChildText(o, "AlternateBorderColor");
                    if (backClr != null) Console.WriteLine($"       背景色: {backClr}");
                    if (altBackClr != null) Console.WriteLine($"       替代背景色: {altBackClr}");
                    if (foreClr != null) Console.WriteLine($"       前景色: {foreClr}");
                    if (borderClr != null) Console.WriteLine($"       边框色: {borderClr}");
                    if (altBorderClr != null) Console.WriteLine($"       替代边框色: {altBorderClr}");

                    // 边框属性
                    string borderW = ChildText(o, "BorderWidth");
                    string dashType = ChildText(o, "DashType");
                    if (borderW != null) Console.WriteLine($"       边框宽度: {borderW}");
                    if (dashType != null) Console.WriteLine($"       线型: {dashType}");

                    // 字体属性（如果有）
                    var fontEl = o.Descendants().FirstOrDefault(x => x.Name.LocalName == "Font" || x.Name.LocalName == "HmiFontPart");
                    if (fontEl != null)
                    {
                        string fontName = ChildText(fontEl, "FontName") ?? ChildText(fontEl, "Name");
                        string fontSize = ChildText(fontEl, "Size");
                        string bold = ChildText(fontEl, "Bold") ?? ChildText(fontEl, "Weight");
                        string italic = ChildText(fontEl, "Italic");
                        string underline = ChildText(fontEl, "Underline");
                        string strikeout = ChildText(fontEl, "StrikeOut");
                        Console.WriteLine($"       字体: {fontName ?? "?"} {fontSize ?? "?"}pt");
                        if (bold != null && bold.Equals("true", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("         ✓ 粗体");
                        if (italic != null && italic.Equals("true", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("         ✓ 斜体");
                        if (underline != null && underline.Equals("true", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("         ✓ 下划线");
                        if (strikeout != null && strikeout.Equals("true", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("         ✓ 删除线");
                    }

                    // 文本内容（如果有，完整输出）
                    string text = ChildText(o, "Text");
                    if (text != null)
                    {
                        Console.WriteLine($"       文本: \"{text}\"");
                    }

                    // 变量绑定
                    string tagRef = FindTagRef(o);
                    if (tagRef != null) Console.WriteLine($"       绑定变量: {tagRef}");

                    // 透明度
                    string opacity = ChildText(o, "Opacity");
                    if (opacity != null) Console.WriteLine($"       不透明度: {opacity}");

                    // 可见性
                    string visible = ChildText(o, "Visible");
                    if (visible != null) Console.WriteLine($"       可见: {visible}");

                    // 旋转
                    string rotation = ChildText(o, "RotationAngle") ?? ChildText(o, "Rotation");
                    if (rotation != null) Console.WriteLine($"       旋转角度: {rotation}°");

                    // 圆角（矩形专用）
                    var cornersEl = o.Descendants().FirstOrDefault(x => x.Name.LocalName == "Corners");
                    if (cornersEl != null)
                    {
                        string tl = ChildText(cornersEl, "TopLeftRadius");
                        string tr = ChildText(cornersEl, "TopRightRadius");
                        string bl = ChildText(cornersEl, "BottomLeftRadius");
                        string br = ChildText(cornersEl, "BottomRightRadius");
                        if (tl != null || tr != null || bl != null || br != null)
                            Console.WriteLine($"       圆角: TL={tl ?? "0"} TR={tr ?? "0"} BL={bl ?? "0"} BR={br ?? "0"}");
                    }
                }
            }

            // 统计摘要
            Console.WriteLine();
            Console.WriteLine("  === 布局统计 ===");
            Console.WriteLine($"  • 控件类型分布: {string.Join(", ", grouped.Select(g => $"{g.Key}×{g.Count()}"))}");

            // 检测重叠控件（完整输出，不截断）
            var overlapping = DetectOverlappingControls(objs);
            if (overlapping.Count > 0)
            {
                Console.WriteLine($"  • 重叠控件: {overlapping.Count} 对");
                foreach (var pair in overlapping)
                {
                    Console.WriteLine($"    - {pair.Item1} 与 {pair.Item2}");
                }
            }
            else
            {
                Console.WriteLine("  • 重叠控件: 无");
            }
        }

        // 检测重叠控件
        private static List<Tuple<string, string>> DetectOverlappingControls(List<XElement> objs)
        {
            var result = new List<Tuple<string, string>>();
            var rects = new List<Tuple<string, int, int, int, int>>();

            foreach (var o in objs)
            {
                string name = ChildText(o, "ObjectName") ?? ChildText(o, "Name") ?? "?";
                if (!int.TryParse(ChildText(o, "Left") ?? ChildText(o, "X"), out int left)) continue;
                if (!int.TryParse(ChildText(o, "Top") ?? ChildText(o, "Y"), out int top)) continue;
                if (!int.TryParse(ChildText(o, "Width"), out int width)) continue;
                if (!int.TryParse(ChildText(o, "Height"), out int height)) continue;
                rects.Add(Tuple.Create(name, left, top, width, height));
            }

            for (int i = 0; i < rects.Count; i++)
            {
                for (int j = i + 1; j < rects.Count; j++)
                {
                    var a = rects[i];
                    var b = rects[j];
                    // 检查矩形重叠
                    if (a.Item2 < b.Item2 + b.Item4 && a.Item2 + a.Item4 > b.Item2 &&
                        a.Item3 < b.Item3 + b.Item5 && a.Item3 + a.Item5 > b.Item3)
                    {
                        result.Add(Tuple.Create(a.Item1, b.Item1));
                    }
                }
            }
            return result;
        }

        // [接入点-后续方法]

        private static string Safe(Func<object> f)
        {
            try { return f()?.ToString() ?? ""; } catch { return "?"; }
        }
    }
}
