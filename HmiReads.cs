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

        // ===== 布局解析专用作用域助手(修复"父控件吸入子控件属性") =====
        // SimaticML 结构:每个控件自身的标量属性都在它"直接的" <AttributeList> 子节点里;
        // 子控件/动画/事件/字体则在 <ObjectList> 里。ChildText 用 Descendants() 会递归穿透,
        // 导致父容器(ScreenLayer/Group)误读到第一个子控件的属性、并把子控件的动画/事件计入自己。
        // 故下列助手按"控件边界"剪枝,确保属性只归属真正拥有它的控件。

        // 控件自身标量属性:只认它自己的 <AttributeList>,绝不递归进 <ObjectList>。
        private static string AttrText(XElement obj, string key)
        {
            var al = obj.Elements().FirstOrDefault(x => x.Name.LocalName == "AttributeList");
            if (al == null) return null;
            var c = al.Elements().FirstOrDefault(x => x.Name.LocalName == key && !string.IsNullOrWhiteSpace(x.Value));
            return c != null ? c.Value.Trim() : null;
        }

        // 是否"嵌套子控件边界"——作用域遍历遇到它就剪枝,不并入父控件。
        // 子控件 = CompositionName=="ScreenItems" 的 Hmi.Screen.* 控件,或 ScreenLayer 图层容器。
        // Hmi.Screen.Property(ProcessValue 等)是控件自身的子属性、非独立控件,不剪。
        private static bool IsControlBoundary(XElement e)
        {
            if (!e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)) return false;
            if (e.Name.LocalName == "Hmi.Screen.Property") return false;
            string comp = e.Attribute("CompositionName")?.Value;
            return comp == "ScreenItems" || e.Name.LocalName == "Hmi.Screen.ScreenLayer";
        }

        // 控件自身作用域内的后代:遇嵌套子控件即剪掉整棵子树,但保留 Property/Font/动画/事件等自身组成。
        private static IEnumerable<XElement> ScopedDescendants(XElement obj)
        {
            foreach (var child in obj.Elements())
            {
                if (IsControlBoundary(child)) continue;
                yield return child;
                foreach (var d in ScopedDescendants(child)) yield return d;
            }
        }

        // 找控件的父容器(组):最近的 Hmi.Screen.* 祖先,排除图层/画面根。无则返回 null(顶层控件)。
        private static string ParentContainer(XElement o)
        {
            var anc = o.Ancestors().FirstOrDefault(a =>
                a.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
                && a.Name.LocalName != "Hmi.Screen.Screen"
                && a.Name.LocalName != "Hmi.Screen.ScreenTemplate"
                && a.Name.LocalName != "Hmi.Screen.ScreenLayer");
            return anc != null ? (AttrText(anc, "ObjectName") ?? AttrText(anc, "Name")) : null;
        }

        // 控件标题/显示文本:在自身作用域内取 MultilingualText 正文(去 body/p 包裹),跳过 HelpText。
        private static string ControlText(XElement o)
        {
            foreach (var mt in ScopedDescendants(o).Where(x => x.Name.LocalName == "MultilingualText"))
            {
                string comp = mt.Attribute("CompositionName")?.Value ?? "";
                if (comp == "HelpText") continue;
                var t = mt.Descendants().FirstOrDefault(x => x.Name.LocalName == "Text" && !string.IsNullOrWhiteSpace(x.Value));
                if (t != null) return t.Value.Trim();
            }
            return AttrText(o, "Text");
        }

        // 控件的"直接绑定变量"= 其 Property(过程值)下 TagConnectionDynamic 引用的 Tag;
        // 不含动画触发器变量(那些在动画段单列,避免把触发器误报成绑定)。
        private static string FindBoundTag(XElement o)
        {
            foreach (var prop in ScopedDescendants(o).Where(x => x.Name.LocalName == "Hmi.Screen.Property"))
            {
                var tag = prop.Descendants().FirstOrDefault(x =>
                    x.Name.LocalName.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0
                    && x.Elements().Any(c => c.Name.LocalName == "Name"));
                if (tag != null)
                {
                    string n = ChildText(tag, "Name");
                    if (!string.IsNullOrWhiteSpace(n)) return n;
                }
            }
            return null;
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

        // 解析动画信息(仅本控件自身的动画;作用域剪枝,不并入子控件,修复父容器动画聚合)
        private static void PrintAnimations(XElement controlEl, string controlName)
        {
            // 仅在控件自身作用域内查动画元素
            var animations = ScopedDescendants(controlEl)
                .Where(e => e.Name.LocalName.StartsWith("Hmi.Dynamic.", StringComparison.Ordinal))
                .ToList();

            // 动画"块"只数顶层动画类型(RangeAppearance/Visibility/TagConnection),
            // 不把内部的 Trigger/Range 也计进个数(旧实现把这些也计入导致虚高)。
            int animCount = animations.Count(e =>
                e.Name.LocalName == "Hmi.Dynamic.RangeAppearanceAnimation"
                || e.Name.LocalName == "Hmi.Dynamic.VisibilityAnimation"
                || e.Name.LocalName == "Hmi.Dynamic.TagConnectionDynamic");
            if (animCount == 0) return;

            Console.WriteLine($"       ── 动画 ({animCount} 个) ──");

            // 解析范围外观动画
            var rangeAnimations = animations
                .Where(e => e.Name.LocalName == "Hmi.Dynamic.RangeAppearanceAnimation")
                .ToList();

            foreach (var rangeAnim in rangeAnimations)
            {
                string animName = ChildText(rangeAnim, "Name") ?? "RangeAppearanceAnimation";
                Console.WriteLine($"         [{animName}]");

                // 查找触发器（绑定变量）
                var triggers = rangeAnim.Descendants()
                    .Where(e => e.Name.LocalName == "Hmi.Dynamic.TagElementTrigger")
                    .ToList();

                foreach (var trigger in triggers)
                {
                    string triggerType = trigger.Attribute("CompositionName")?.Value ?? "?";
                    string tagName = ChildText(trigger, "Name") ?? "?";
                    Console.WriteLine($"           触发器: {triggerType} → 变量: {tagName}");
                }

                // 查找范围值
                var ranges = rangeAnim.Descendants()
                    .Where(e => e.Name.LocalName == "Hmi.Dynamic.Range")
                    .ToList();

                foreach (var range in ranges)
                {
                    string lower = AttrText(range, "LowerLimit") ?? "?";
                    string upper = AttrText(range, "UpperLimit") ?? "?";
                    string backColor = AttrText(range, "BackColor") ?? "-";
                    string foreColor = AttrText(range, "ForeColor") ?? "-";
                    string flashing = AttrText(range, "FlashingType") ?? "No";

                    Console.WriteLine($"           范围 [{lower} ~ {upper}]:");
                    Console.WriteLine($"             背景色: {backColor}");
                    Console.WriteLine($"             前景色: {foreColor}");
                    if (flashing != "No") Console.WriteLine($"             闪烁: {flashing}");
                }
            }

            // 解析可见性动画
            var visibilityAnimations = animations
                .Where(e => e.Name.LocalName == "Hmi.Dynamic.VisibilityAnimation")
                .ToList();

            foreach (var visAnim in visibilityAnimations)
            {
                string rangeStart = ChildText(visAnim, "RangeStart") ?? "?";
                string rangeEnd = ChildText(visAnim, "RangeEnd") ?? "?";
                string visWhenTrue = ChildText(visAnim, "Visible") ?? "?";
                Console.WriteLine($"         [VisibilityAnimation] 范围=[{rangeStart}~{rangeEnd}] 值在范围内时{(visWhenTrue == "true" ? "可见" : "隐藏")}");

                // 查找触发器
                var triggers = visAnim.Descendants()
                    .Where(e => e.Name.LocalName == "Hmi.Dynamic.TagElementTrigger")
                    .ToList();

                foreach (var trigger in triggers)
                {
                    string triggerType = trigger.Attribute("CompositionName")?.Value ?? "?";
                    string tagName = ChildText(trigger, "Name") ?? "?";
                    Console.WriteLine($"           触发器: {triggerType} → 变量: {tagName}");
                }
            }

            // 解析标签连接动态（用于IO域等）
            var tagConnections = animations
                .Where(e => e.Name.LocalName == "Hmi.Dynamic.TagConnectionDynamic")
                .ToList();

            foreach (var tagConn in tagConnections)
            {
                string tagName = ChildText(tagConn, "Name") ?? "?";
                string indirect = ChildText(tagConn, "Indirect") ?? "false";
                Console.WriteLine($"         [TagConnection] 变量: {tagName} (间接: {indirect})");
            }
        }

        // 解析事件信息(仅本控件自身的事件;作用域剪枝,不并入子控件)
        private static void PrintEvents(XElement controlEl, string controlName)
        {
            // 仅在控件自身作用域内查事件元素
            var events = ScopedDescendants(controlEl)
                .Where(e => e.Name.LocalName == "Hmi.Event.Event")
                .ToList();

            if (events.Count == 0) return;

            Console.WriteLine($"       ── 事件 ({events.Count} 个) ──");

            foreach (var evt in events)
            {
                string eventName = ChildText(evt, "Name") ?? "?";
                Console.WriteLine($"         [{eventName}]");

                // 查找事件处理器
                var handlers = evt.Descendants()
                    .Where(e => e.Name.LocalName == "Hmi.Event.FunctionListEventHandler")
                    .ToList();

                foreach (var handler in handlers)
                {
                    // 查找函数条目
                    var entries = handler.Descendants()
                        .Where(e => e.Name.LocalName == "Hmi.Event.FunctionListEntry")
                        .ToList();

                    foreach (var entry in entries)
                    {
                        string funcName = ChildText(entry, "Name") ?? "?";
                        string funcType = ChildText(entry, "Type") ?? "?";
                        Console.WriteLine($"           函数: {funcName} ({funcType})");

                        // 查找参数
                        var parameters = entry.Descendants()
                            .Where(e => e.Name.LocalName == "Hmi.Event.FunctionListEntryParameter")
                            .ToList();

                        foreach (var param in parameters)
                        {
                            string paramName = ChildText(param, "Name") ?? "?";
                            // 优先取 LinkList/Value/Name（变量名/画面名等链接值）
                            var linkVal = param.Descendants()
                                .FirstOrDefault(e => e.Name.LocalName == "Value"
                                    && e.Parent?.Name.LocalName == "LinkList"
                                    && e.Elements().Any(c => c.Name.LocalName == "Name"));
                            string paramValue;
                            if (linkVal != null)
                            {
                                paramValue = ChildText(linkVal, "Name") ?? "?";
                            }
                            else
                            {
                                // 回退到 AttributeList/Value（带 Type 属性的字面值）
                                paramValue = ChildText(param, "Value") ?? "?";
                            }

                            Console.WriteLine($"             参数: {paramName} = {paramValue}");
                        }
                    }
                }
            }
        }

        // ===== hmi-read-screen-layout <名>:单画面视觉布局信息(位置/大小/颜色/字体/动画/事件) =====
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

        // 解析画面XML，提取视觉布局信息(位置/大小/颜色/字体 + 动画 + 事件)
        // 关键:控件属性一律走 AttrText/作用域助手——只取控件"自身 AttributeList"的值,
        // 绝不像旧实现那样用 Descendants() 递归穿透、把子控件属性误算到父容器上。
        private static void PrintScreenLayout(string xml, string screenName)
        {
            var doc = XDocument.Parse(xml);
            // 支持普通画面和模板画面两种根元素
            var screenEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.Screen")
                        ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.ScreenTemplate");
            if (screenEl == null) { Console.WriteLine("  [错误] 无法解析画面根元素"); return; }

            // 画面基本信息(取画面根自身 AttributeList;画面号字段名是 Number)
            string width = AttrText(screenEl, "Width") ?? "?";
            string height = AttrText(screenEl, "Height") ?? "?";
            string backColor = AttrText(screenEl, "BackColor") ?? "?";
            string screenNumber = AttrText(screenEl, "Number") ?? "?";

            Console.WriteLine($"  画面: {screenName} (#{screenNumber})");
            Console.WriteLine($"  尺寸: {width} x {height} (DIU)");
            Console.WriteLine($"  背景色: {backColor}");

            // 画面级事件(ClearScreen/GenerateScreen 等) — 作用域剪到图层之前,只取画面根自身事件
            var screenEvents = ScopedDescendants(screenEl).Where(e => e.Name.LocalName == "Hmi.Event.Event").ToList();
            if (screenEvents.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  ── 画面级事件 ({screenEvents.Count} 个) ──");
                foreach (var evt in screenEvents)
                {
                    string evtName = ChildText(evt, "Name") ?? "?";
                    var entries = evt.Descendants().Where(e => e.Name.LocalName == "Hmi.Event.FunctionListEntry").ToList();
                    foreach (var entry in entries)
                    {
                        string funcName = ChildText(entry, "Name") ?? "?";
                        string funcType = ChildText(entry, "Type") ?? "";
                        // 提取关键参数
                        var pars = entry.Descendants().Where(e => e.Name.LocalName == "Hmi.Event.FunctionListEntryParameter").ToList();
                        var parStrs = new List<string>();
                        foreach (var p in pars)
                        {
                            string pName = ChildText(p, "Name") ?? "?";
                            // 链接值(变量名/画面名等)
                            var linkVal = p.Descendants().FirstOrDefault(x => x.Name.LocalName == "Name" && x.Parent?.Name.LocalName == "Value");
                            string pVal = linkVal?.Value ?? ChildText(p, "Value") ?? "?";
                            parStrs.Add($"{pName}={pVal}");
                        }
                        Console.WriteLine($"    [{evtName}] → {funcName}({funcType}) {string.Join(", ", parStrs)}");
                    }
                }
            }
            Console.WriteLine();

            // 提取所有"真实控件":排除画面根、模板根、结构性图层(ScreenLayer)、控件子属性(Property/ProcessValue)。
            // 旧实现把 ScreenLayer 和 Property 也算控件 → 数目虚高 + 图层吸入全部子控件属性/动画。
            int layerCount = doc.Descendants().Count(e => e.Name.LocalName == "Hmi.Screen.ScreenLayer");
            int propCount = doc.Descendants().Count(e => e.Name.LocalName == "Hmi.Screen.Property");
            var objs = doc.Descendants()
                          .Where(e => e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
                                      && e.Name.LocalName != "Hmi.Screen.Screen"
                                      && e.Name.LocalName != "Hmi.Screen.ScreenTemplate"
                                      && e.Name.LocalName != "Hmi.Screen.ScreenLayer"
                                      && e.Name.LocalName != "Hmi.Screen.Property")
                          .ToList();

            Console.WriteLine($"  控件总数: {objs.Count}（不含 {layerCount} 个图层容器、{propCount} 个控件子属性ProcessValue）");
            Console.WriteLine();

            // 控件摘要列表（每行一个控件，结构化输出）
            Console.WriteLine("  === 控件摘要 ===");
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                string type = o.Name.LocalName.Replace("Hmi.Screen.", "");
                string oname = AttrText(o, "ObjectName") ?? AttrText(o, "Name") ?? "?";
                string left = AttrText(o, "Left") ?? AttrText(o, "X") ?? "?";
                string top = AttrText(o, "Top") ?? AttrText(o, "Y") ?? "?";
                string w = AttrText(o, "Width") ?? "?";
                string h = AttrText(o, "Height") ?? "?";
                string backClr = AttrText(o, "BackColor") ?? AttrText(o, "BackgroundColor") ?? "-";
                string parent = ParentContainer(o);
                string parentTag = parent != null ? $"  ∈组 \"{parent}\"" : "";

                Console.WriteLine($"  [{i + 1}] {type} \"{oname}\"{parentTag}");
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
                    string oname = AttrText(o, "ObjectName") ?? AttrText(o, "Name") ?? "?";
                    string parent = ParentContainer(o);
                    Console.WriteLine(parent != null ? $"    ── {oname} (∈组 \"{parent}\") ──" : $"    ── {oname} ──");

                    // 位置和大小(仅控件自身 AttributeList)
                    string left = AttrText(o, "Left") ?? AttrText(o, "X");
                    string top = AttrText(o, "Top") ?? AttrText(o, "Y");
                    string w = AttrText(o, "Width");
                    string h = AttrText(o, "Height");
                    if (left != null || top != null) Console.WriteLine($"       位置: ({left ?? "?"}, {top ?? "?"})");
                    if (w != null || h != null) Console.WriteLine($"       大小: {w ?? "?"} × {h ?? "?"}");

                    // 颜色属性
                    string backClr = AttrText(o, "BackColor") ?? AttrText(o, "BackgroundColor");
                    string altBackClr = AttrText(o, "AlternateBackColor");
                    string foreClr = AttrText(o, "ForeColor") ?? AttrText(o, "ForegroundColor");
                    string borderClr = AttrText(o, "BorderColor");
                    string altBorderClr = AttrText(o, "AlternateBorderColor");
                    if (backClr != null) Console.WriteLine($"       背景色: {backClr}");
                    if (altBackClr != null) Console.WriteLine($"       替代背景色: {altBackClr}");
                    if (foreClr != null) Console.WriteLine($"       前景色: {foreClr}");
                    if (borderClr != null) Console.WriteLine($"       边框色: {borderClr}");
                    if (altBorderClr != null) Console.WriteLine($"       替代边框色: {altBorderClr}");

                    // 边框属性
                    string borderW = AttrText(o, "BorderWidth");
                    string dashType = AttrText(o, "DashType");
                    if (borderW != null) Console.WriteLine($"       边框宽度: {borderW}");
                    if (dashType != null) Console.WriteLine($"       线型: {dashType}");

                    // 字体属性:经典 HMI 字体存为 Hmi.Globalization.FontItem(FontFamily/FontSize/FontStyle)
                    var fontItem = ScopedDescendants(o).FirstOrDefault(x => x.Name.LocalName == "Hmi.Globalization.FontItem");
                    if (fontItem != null)
                    {
                        string fam = AttrText(fontItem, "FontFamily");
                        string size = AttrText(fontItem, "FontSize");
                        string style = AttrText(fontItem, "FontStyle"); // 如 Regular/Bold/"Bold, Italic"
                        string line = $"       字体: {fam ?? "?"} {size ?? "?"}pt";
                        if (!string.IsNullOrEmpty(style) && !style.Equals("Regular", StringComparison.OrdinalIgnoreCase))
                            line += $" [{style}]";
                        Console.WriteLine(line);
                    }

                    // 文本内容(经典 HMI 在 MultilingualText 正文里,去 body/p 包裹)
                    string text = ControlText(o);
                    if (text != null) Console.WriteLine($"       文本: \"{text}\"");

                    // 变量绑定(仅过程值绑定,不含动画触发器变量)
                    string tagRef = FindBoundTag(o);
                    if (tagRef != null) Console.WriteLine($"       绑定变量: {tagRef}");

                    // 透明度
                    string opacity = AttrText(o, "Opacity");
                    if (opacity != null) Console.WriteLine($"       不透明度: {opacity}");

                    // 可见性
                    string visible = AttrText(o, "Visible");
                    if (visible != null) Console.WriteLine($"       可见: {visible}");

                    // 旋转
                    string rotation = AttrText(o, "RotationAngle") ?? AttrText(o, "Rotation");
                    if (rotation != null) Console.WriteLine($"       旋转角度: {rotation}°");

                    // 圆角:标量 CornerRadius(矩形/按钮)或 Corners 元素(分角半径)
                    string cornerRadius = AttrText(o, "CornerRadius");
                    if (cornerRadius != null && cornerRadius != "0") Console.WriteLine($"       圆角半径: {cornerRadius}");
                    var cornersEl = ScopedDescendants(o).FirstOrDefault(x => x.Name.LocalName == "Corners");
                    if (cornersEl != null)
                    {
                        string tl = ChildText(cornersEl, "TopLeftRadius");
                        string tr = ChildText(cornersEl, "TopRightRadius");
                        string bl = ChildText(cornersEl, "BottomLeftRadius");
                        string br = ChildText(cornersEl, "BottomRightRadius");
                        if (tl != null || tr != null || bl != null || br != null)
                            Console.WriteLine($"       圆角: TL={tl ?? "0"} TR={tr ?? "0"} BL={bl ?? "0"} BR={br ?? "0"}");
                    }

                    // ★ 动画信息(仅本控件自身)
                    PrintAnimations(o, oname);

                    // ★ 事件信息(仅本控件自身)
                    PrintEvents(o, oname);
                }
            }

            // 统计摘要
            Console.WriteLine();
            Console.WriteLine("  === 布局统计 ===");
            Console.WriteLine($"  • 控件类型分布: {string.Join(", ", grouped.Select(g => $"{g.Key}×{g.Count()}"))}");

            // 检测重叠控件(几何取自控件自身 AttributeList);可能很多(背景矩形覆盖众控件),故只列前若干对
            var overlapping = DetectOverlappingControls(objs);
            if (overlapping.Count > 0)
            {
                const int cap = 20;
                Console.WriteLine($"  • 重叠控件: {overlapping.Count} 对（含背景框与其上控件的正常覆盖）");
                foreach (var pair in overlapping.Take(cap))
                    Console.WriteLine($"    - {pair.Item1} 与 {pair.Item2}");
                if (overlapping.Count > cap)
                    Console.WriteLine($"    …其余 {overlapping.Count - cap} 对略(可导出 XML 自行核对)");
            }
            else
            {
                Console.WriteLine("  • 重叠控件: 无");
            }
        }

        // 检测重叠控件(几何只取控件自身 AttributeList,不递归子控件)
        private static List<Tuple<string, string>> DetectOverlappingControls(List<XElement> objs)
        {
            var result = new List<Tuple<string, string>>();
            var rects = new List<Tuple<string, int, int, int, int>>();

            foreach (var o in objs)
            {
                string name = AttrText(o, "ObjectName") ?? AttrText(o, "Name") ?? "?";
                if (!int.TryParse(AttrText(o, "Left") ?? AttrText(o, "X"), out int left)) continue;
                if (!int.TryParse(AttrText(o, "Top") ?? AttrText(o, "Y"), out int top)) continue;
                if (!int.TryParse(AttrText(o, "Width"), out int width)) continue;
                if (!int.TryParse(AttrText(o, "Height"), out int height)) continue;
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
