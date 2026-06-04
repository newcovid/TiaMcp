using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;             // ExportOptions, ImportOptions
using Siemens.Engineering.Hmi.Screen;  // Screen, ScreenSystemFolder, ScreenUserFolder
using Siemens.Engineering.Hmi.Tag;     // TagTable, TagSystemFolder, TagUserFolder, Tag
using Siemens.Engineering.Hmi.TextGraphicList; // TextList, GraphicList
using HmiTarget = Siemens.Engineering.Hmi.HmiTarget;

namespace TiaMcp
{
    /// <summary>
    /// HMI 写命令集(经典 HmiTarget)。建/改走 导出→改XML→Import(Override) 克隆模板;删走 API。
    /// 全带 --dry-run、结构化逐条回调([建]/[改]/[删]/[跳]/[错])、逐条非致命、幂等。
    /// </summary>
    internal static class HmiWrites
    {
        // ===== hmi-write-tags <清单> [--dry-run] =====
        public static int WriteTags(string listFile, bool dryRun)
        {
            if (listFile == null || !File.Exists(listFile)) { Console.WriteLine("找不到清单文件: " + listFile); return 1; }
            var lines = ParseLines(listFile);
            if (lines.Count == 0) { Console.WriteLine("清单为空（每行: 表名 | 变量名 | 连接 | PLC符号 | [访问模式] | [注释]）。"); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0];
                HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}{(hmis.Count > 1 ? "（多 HMI，默认第一个）" : "")}{(dryRun ? "  [DRY-RUN 预演，不写入]" : "")}");

                var tables = new List<TagTable>(); CollectTagTables(hmi.TagFolder, tables);
                int created = 0, updated = 0, skipped = 0, errors = 0;

                foreach (var g in lines.GroupBy(f => f[0]))
                {
                    string tableName = g.Key;
                    TagTable table = tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
                    if (table == null) { foreach (var _ in g) { Line("错", "表不存在: " + tableName); errors++; } continue; }

                    XDocument doc;
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        table.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        doc = XDocument.Parse(IoUtil.ReadPlaintext(tmp));
                    }
                    catch (Exception ex) { foreach (var _ in g) { Line("错", tableName + " 导出失败: " + ex.Message); errors++; } continue; }
                    finally { try { File.Delete(tmp); } catch { } }

                    var tagEls = doc.Descendants().Where(e => e.Name.LocalName == "Hmi.Tag.Tag").ToList();
                    XElement template = tagEls.FirstOrDefault();
                    if (template == null) { foreach (var _ in g) { Line("错", "表 " + tableName + " 为空，无模板可克隆"); errors++; } continue; }

                    long maxId = MaxId(doc); // 克隆出的新变量按"现有最大ID+1"重编，保证 ID 存在且唯一
                    bool templateBindable = HasBindingStructure(template);
                    bool changed = false;
                    foreach (var f in g)
                    {
                        string name = f[1], conn = f[2], plc = f[3];
                        string access = (f.Length > 4 && f[4].Length > 0) ? f[4] : "Symbolic";
                        // 变量名含 . 或 \ 都会致导入失败（plc 字段含 . 是合法的 DB.成员，不在此校验）
                        if (name.Contains("\\") || name.Contains("."))
                        { Line("错", name + " 变量名含非法字符(. 或 \\)，导入会失败"); errors++; continue; }

                        XElement existing = tagEls.FirstOrDefault(te => string.Equals(TagName(te), name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            // 修复：没有绑定结构时不能假装绑定成功
                            if (!HasBindingStructure(existing))
                            { Line("错", name + " 现有变量无 PLC 绑定结构(LinkList/ControllerTag/Connection)，无法改绑"); errors++; continue; }
                            if (!dryRun) { SetBinding(existing, conn, plc, access); changed = true; }
                            Line("改", $"{name}  conn={conn} plc={plc}"); updated++;
                        }
                        else
                        {
                            // 修复：模板变量本身无绑定结构时，克隆出来也绑不上，直接报错而非假成功
                            if (!templateBindable)
                            { Line("错", name + " 模板变量无 PLC 绑定结构，无法新建带绑定变量（请选一张含已绑定变量的表）"); errors++; continue; }
                            XElement clone = new XElement(template);
                            SetTagName(clone, name);
                            SetBinding(clone, conn, plc, access);
                            RenumberIds(clone, ref maxId);
                            if (!dryRun) { template.Parent.Add(clone); changed = true; }
                            Line("建", $"{name}  conn={conn} plc={plc}"); created++;
                        }
                    }

                    if (!dryRun && changed)
                    {
                        string outTmp = null;
                        try
                        {
                            outTmp = SaveXml(doc);
                            hmi.TagFolder.TagTables.Import(new FileInfo(outTmp), ImportOptions.Override);
                        }
                        catch (Exception ex) { Line("错", tableName + " 导入失败: " + ex.Message); errors++; Logger.Error("write-tags import", ex); }
                        finally { if (outTmp != null) { try { File.Delete(outTmp); } catch { } } }
                    }
                }

                Console.WriteLine($"汇总: 建 {created} / 改 {updated} / 跳 {skipped} / 错 {errors}{(dryRun ? "（预演，未写入）" : "")}");
                return errors > 0 ? 2 : 0;
            }
        }

        // ---- 共享辅助 ----
        private static void Line(string tag, string msg) => Console.WriteLine($"  [{tag}] {msg}");

        private static List<string[]> ParseLines(string file)
        {
            var res = new List<string[]>();
            foreach (var raw in File.ReadAllLines(file))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                var f = ln.Split('|').Select(x => x.Trim()).ToArray();
                if (f.Length >= 4) res.Add(f);
            }
            return res;
        }

        private static string TagName(XElement tagEl)
        {
            var al = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var nm = al?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            return nm?.Value;
        }
        private static void SetTagName(XElement tagEl, string name)
        {
            var al = tagEl.Elements().First(e => e.Name.LocalName == "AttributeList");
            al.Elements().First(e => e.Name.LocalName == "Name").Value = name;
        }
        // 模板/变量是否具备 PLC 绑定结构（LinkList + ControllerTag + Connection 三件套）
        private static bool HasBindingStructure(XElement tagEl)
        {
            var ll = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (ll == null) return false;
            return ll.Elements().Any(e => e.Name.LocalName == "ControllerTag")
                && ll.Elements().Any(e => e.Name.LocalName == "Connection");
        }
        private static void SetBinding(XElement tagEl, string conn, string plc, string access)
        {
            var al = tagEl.Elements().First(e => e.Name.LocalName == "AttributeList");
            var am = al.Elements().FirstOrDefault(e => e.Name.LocalName == "AddressAccessMode");
            if (am != null) am.Value = access;
            var ll = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (ll != null)
            {
                var ct = ll.Elements().FirstOrDefault(e => e.Name.LocalName == "ControllerTag");
                if (ct != null) ct.Elements().First(e => e.Name.LocalName == "Name").Value = plc;
                var cn = ll.Elements().FirstOrDefault(e => e.Name.LocalName == "Connection");
                if (cn != null) cn.Elements().First(e => e.Name.LocalName == "Name").Value = conn;
            }
        }
        // 克隆来的元素带重复 ID；导入要求 ID 存在且唯一，故重编为大于现有最大值的新 ID(十六进制)
        private static long MaxId(XDocument doc)
        {
            return doc.Descendants().Attributes().Where(a => a.Name.LocalName == "ID")
                      .Select(a => ParseHex(a.Value)).DefaultIfEmpty(0).Max();
        }
        private static void RenumberIds(XElement el, ref long maxId)
        {
            foreach (var e in el.DescendantsAndSelf())
            {
                var id = e.Attribute("ID");
                if (id != null) { maxId++; id.Value = maxId.ToString("X"); }
            }
        }
        private static long ParseHex(string s)
        {
            long v;
            return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }
        // XDocument 存为带声明的明文临时文件(经 %TEMP% 校验)
        private static string SaveXml(XDocument doc)
        {
            string decl = doc.Declaration != null ? doc.Declaration.ToString() : "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
            return IoUtil.WriteTempPlaintextVerified(decl + "\r\n" + doc.ToString(), ".xml");
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

        // ===== hmi-delete-tags <清单> [--dry-run] =====  行: 表名 | 变量名 (表名省略=默认表)
        public static int DeleteTags(string listFile, bool dryRun)
        {
            if (listFile == null || !File.Exists(listFile)) { Console.WriteLine("找不到清单文件: " + listFile); return 1; }
            var lines = ParseDeleteLines(listFile);
            if (lines.Count == 0) { Console.WriteLine("清单为空（每行: [表名 |] 变量名）。"); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}{(dryRun ? "  [DRY-RUN 预演，不删除]" : "")}");
                Console.WriteLine("警告: 删除被画面引用的变量会破坏那些画面；建议先 hmi-export-all 出快照。");

                var tables = new List<TagTable>(); CollectTagTables(hmi.TagFolder, tables);
                int deleted = 0, skipped = 0;
                foreach (var f in lines)
                {
                    string tableName = f[0], name = f[1];
                    var candidates = tableName != null
                        ? tables.Where(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
                        : tables;
                    Tag tag = null;
                    foreach (var t in candidates) { tag = t.Tags.Find(name); if (tag != null) break; }
                    if (tag == null) { Line("跳", "找不到变量: " + name); skipped++; continue; }
                    if (!dryRun) tag.Delete();
                    Line("删", name); deleted++;
                }
                Console.WriteLine($"汇总: 删 {deleted} / 跳 {skipped}{(dryRun ? "（预演，未删除）" : "")}");
                return 0;
            }
        }

        // 删除清单: "表名 | 变量名" 或 "变量名"
        private static List<string[]> ParseDeleteLines(string file)
        {
            var res = new List<string[]>();
            foreach (var raw in File.ReadAllLines(file))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                var p = ln.Split('|').Select(x => x.Trim()).ToArray();
                if (p.Length >= 2) res.Add(new[] { p[0], p[1] });
                else res.Add(new string[] { null, p[0] });
            }
            return res;
        }

        // ===== hmi-export-screen <名> <目录> =====  导单屏 XML 供编辑(供 import-screen 闭环前半)
        public static int ExportScreen(string screenName, string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                Directory.CreateDirectory(outDir);
                foreach (var kv in hmis)
                {
                    var screens = new List<Screen>(); CollectScreens(kv.Value.ScreenFolder, screens);
                    Screen target = screens.FirstOrDefault(x => string.Equals(x.Name, screenName, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        target.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string xml = IoUtil.ReadPlaintext(tmp);
                        string outPath = Path.Combine(outDir, MakeSafe(target.Name) + ".xml");
                        File.WriteAllText(outPath, xml, new System.Text.UTF8Encoding(false));
                        Console.WriteLine($"已导出 {target.Name} -> {Path.GetFullPath(outPath)}（明文 {xml.Length} 字符）");
                        Console.WriteLine("注意: 尽快编辑/用完，本机全盘扫描可能稍后加密它。");
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                    return 0;
                }
                Console.WriteLine("找不到画面: " + screenName);
                return 1;
            }
        }

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
        private static string MakeSafe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ===== hmi-import-screen <xml文件> [--dry-run] =====  整屏替换/新建
        public static int ImportScreen(string xmlFile, bool dryRun)
        {
            if (xmlFile == null || !File.Exists(xmlFile)) { Console.WriteLine("找不到画面 XML: " + xmlFile); return 1; }
            byte[] bytes = File.ReadAllBytes(xmlFile);
            if (IoUtil.LooksEncrypted(bytes)) { Console.WriteLine("该 XML 文件像密文（疑被 E-SafeNet 加密），无法导入。请重新导出/编辑后再试。"); return 1; }
            string xml = IoUtil.DecodeUtf8StripBom(bytes);

            string screenName = "?", screenNum = "?";
            try
            {
                var doc = XDocument.Parse(xml);
                var scr = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.Screen");
                var al = scr?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                screenName = al?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "?";
                screenNum = al?.Elements().FirstOrDefault(e => e.Name.LocalName == "Number")?.Value ?? "?";
            }
            catch (Exception ex) { Console.WriteLine("XML 解析失败: " + ex.Message); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                var screens = new List<Screen>(); CollectScreens(hmi.ScreenFolder, screens);
                bool exists = screens.Any(x => string.Equals(x.Name, screenName, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"目标 HMI: {kv.Key}{(dryRun ? "  [DRY-RUN 预演，不导入]" : "")}");
                Console.WriteLine($"  画面 {screenName} #{screenNum} 将 {(exists ? "覆盖现有" : "新建")}。约束: 同设备类型 + 画面宽高匹配 + 画面号唯一。");
                if (dryRun) { Console.WriteLine("（预演，未导入）"); return 0; }

                string tmp = IoUtil.WriteTempPlaintextVerified(xml, ".xml");
                try
                {
                    hmi.ScreenFolder.Screens.Import(new FileInfo(tmp), ImportOptions.Override);
                    Line(exists ? "改" : "建", $"{screenName} #{screenNum}");
                    Console.WriteLine("导入完成。建议 hmi-read-screen 复核。");
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("导入失败: " + ex.Message + "（多为尺寸/画面号/设备类型不符）"); Logger.Error("import-screen", ex); return 2; }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        // ===== hmi-delete-screen <名> [--dry-run] =====
        public static int DeleteScreen(string screenName, bool dryRun)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}{(dryRun ? "  [DRY-RUN 预演，不删除]" : "")}");
                Console.WriteLine("警告: 删除被导航/画面切换引用的画面会断链。");
                var screens = new List<Screen>(); CollectScreens(hmi.ScreenFolder, screens);
                Screen target = screens.FirstOrDefault(x => string.Equals(x.Name, screenName, StringComparison.OrdinalIgnoreCase));
                if (target == null) { Line("跳", "找不到画面: " + screenName); return 1; }
                if (!dryRun) target.Delete();
                Line("删", screenName);
                Console.WriteLine(dryRun ? "（预演，未删除）" : "已删除。");
                return 0;
            }
        }

        // ============ HMI 模板画面(母版) 写命令：模板 = ScreenTemplateFolder（与普通画面分开） ============

        // ===== hmi-export-template <名> <目录> =====
        public static int ExportTemplate(string templateName, string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                Directory.CreateDirectory(outDir);
                foreach (var kv in hmis)
                {
                    var templates = new List<ScreenTemplate>(); CollectTemplates(kv.Value.ScreenTemplateFolder, templates);
                    ScreenTemplate target = templates.FirstOrDefault(x => string.Equals(x.Name, templateName, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        target.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string xml = IoUtil.ReadPlaintext(tmp);
                        string outPath = Path.Combine(outDir, MakeSafe(target.Name) + ".xml");
                        File.WriteAllText(outPath, xml, new System.Text.UTF8Encoding(false));
                        Console.WriteLine($"已导出模板 {target.Name} -> {Path.GetFullPath(outPath)}（明文 {xml.Length} 字符）");
                        Console.WriteLine("注意: 尽快编辑/用完，本机全盘扫描可能稍后加密它。");
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                    return 0;
                }
                Console.WriteLine("找不到模板: " + templateName);
                return 1;
            }
        }

        // ===== hmi-import-template <xml文件> [--dry-run] =====  整模板替换/新建（Override）
        public static int ImportTemplate(string xmlFile, bool dryRun)
        {
            if (xmlFile == null || !File.Exists(xmlFile)) { Console.WriteLine("找不到模板 XML: " + xmlFile); return 1; }
            byte[] bytes = File.ReadAllBytes(xmlFile);
            if (IoUtil.LooksEncrypted(bytes)) { Console.WriteLine("该 XML 文件像密文（疑被 E-SafeNet 加密），无法导入。请重新导出/编辑后再试。"); return 1; }
            string xml = IoUtil.DecodeUtf8StripBom(bytes);

            string name = "?";
            try
            {
                var doc = XDocument.Parse(xml);
                var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.IndexOf("Template", StringComparison.OrdinalIgnoreCase) >= 0
                                                               && e.Elements().Any(c => c.Name.LocalName == "AttributeList"))
                         ?? doc.Descendants().FirstOrDefault(e => e.Elements().Any(c => c.Name.LocalName == "AttributeList"));
                var al = el?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                name = al?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "?";
            }
            catch (Exception ex) { Console.WriteLine("XML 解析失败: " + ex.Message); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                var templates = new List<ScreenTemplate>(); CollectTemplates(hmi.ScreenTemplateFolder, templates);
                bool exists = templates.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"目标 HMI: {kv.Key}{(dryRun ? "  [DRY-RUN 预演，不导入]" : "")}");
                Console.WriteLine($"  模板 {name} 将 {(exists ? "覆盖现有" : "新建")}（Override）。约束: 同设备类型 + 尺寸匹配。");
                if (dryRun) { Console.WriteLine("（预演，未导入）"); return 0; }

                string tmp = IoUtil.WriteTempPlaintextVerified(xml, ".xml");
                try
                {
                    hmi.ScreenTemplateFolder.ScreenTemplates.Import(new FileInfo(tmp), ImportOptions.Override);
                    Line(exists ? "改" : "建", name);
                    Console.WriteLine("导入完成。建议 hmi-read-templates 复核。");
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("导入失败: " + ex.Message + "（多为尺寸/设备类型不符）"); Logger.Error("import-template", ex); return 2; }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        // ===== hmi-delete-template <名> [--dry-run] =====
        public static int DeleteTemplate(string templateName, bool dryRun)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}{(dryRun ? "  [DRY-RUN 预演，不删除]" : "")}");
                Console.WriteLine("警告: 删除被画面继承的模板会影响所有引用它的画面。");
                var templates = new List<ScreenTemplate>(); CollectTemplates(hmi.ScreenTemplateFolder, templates);
                ScreenTemplate target = templates.FirstOrDefault(x => string.Equals(x.Name, templateName, StringComparison.OrdinalIgnoreCase));
                if (target == null) { Line("跳", "找不到模板: " + templateName); return 1; }
                if (!dryRun) target.Delete();
                Line("删", templateName);
                Console.WriteLine(dryRun ? "（预演，未删除）" : "已删除。");
                return 0;
            }
        }

        // ===== hmi-delete-list <名> [--text|--graphic] [--dry-run] =====  删文本/图形列表
        public static int DeleteList(string name, string kind, bool dryRun)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}{(dryRun ? "  [DRY-RUN 预演，不删除]" : "")}");
                Console.WriteLine("警告: 删除被画面引用的列表会破坏那些画面显示。");
                bool doText = kind != "graphic", doGraphic = kind != "text";
                if (doText)
                    foreach (TextList tl in hmi.TextLists)
                        if (string.Equals(tl.Name, name, StringComparison.OrdinalIgnoreCase))
                        { if (!dryRun) tl.Delete(); Line("删", "文本列表 " + name); Console.WriteLine(dryRun ? "（预演，未删除）" : "已删除。"); return 0; }
                if (doGraphic)
                    foreach (GraphicList gl in hmi.GraphicLists)
                        if (string.Equals(gl.Name, name, StringComparison.OrdinalIgnoreCase))
                        { if (!dryRun) gl.Delete(); Line("删", "图形列表 " + name); Console.WriteLine(dryRun ? "（预演，未删除）" : "已删除。"); return 0; }
                Line("跳", "找不到列表: " + name); return 1;
            }
        }

        // ===== hmi-import-list <xml> [--text|--graphic] [--dry-run] =====  导入/覆盖文本或图形列表
        public static int ImportList(string xmlFile, string kind, bool dryRun)
        {
            if (xmlFile == null || !File.Exists(xmlFile)) { Console.WriteLine("找不到列表 XML: " + xmlFile); return 1; }
            byte[] bytes = File.ReadAllBytes(xmlFile);
            if (IoUtil.LooksEncrypted(bytes)) { Console.WriteLine("该 XML 像密文，无法导入。"); return 1; }
            string xml = IoUtil.DecodeUtf8StripBom(bytes);
            if (kind == null) // 未指定则从 XML 推断
                kind = xml.IndexOf("GraphicList", StringComparison.OrdinalIgnoreCase) >= 0 ? "graphic" : "text";

            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0]; HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}  导入{(kind == "graphic" ? "图形" : "文本")}列表（Override）{(dryRun ? "  [DRY-RUN 预演，不导入]" : "")}");
                if (dryRun) { Console.WriteLine("（预演，未导入）"); return 0; }
                string tmp = IoUtil.WriteTempPlaintextVerified(xml, ".xml");
                try
                {
                    if (kind == "graphic") hmi.GraphicLists.Import(new FileInfo(tmp), ImportOptions.Override);
                    else hmi.TextLists.Import(new FileInfo(tmp), ImportOptions.Override);
                    Line("建/改", (kind == "graphic" ? "图形" : "文本") + "列表导入完成");
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("导入失败: " + ex.Message); Logger.Error("import-list", ex); return 2; }
                finally { try { File.Delete(tmp); } catch { } }
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
    }
}
