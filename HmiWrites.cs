using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;             // ExportOptions, ImportOptions
using Siemens.Engineering.SW;          // PlcSoftware
using Siemens.Engineering.SW.Blocks;   // PlcBlock, PlcBlockGroup
using Siemens.Engineering.SW.Tags;     // PlcTag, PlcTagTable...
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
            if (lines.Count == 0) { Console.WriteLine("清单为空（每行: 表名 | 变量名 | 连接 | PLC符号 | [访问模式] | [注释] | [类型] | [采集周期]）。访问模式=Absolute 时第4列填绝对地址(如 %M0.0)且第7列[类型]必填。"); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0) { Console.WriteLine("未找到 HMI 设备。"); return 1; }
                var kv = hmis[0];
                HmiTarget hmi = kv.Value;
                Console.WriteLine($"目标 HMI: {kv.Key}{(hmis.Count > 1 ? "（多 HMI，默认第一个）" : "")}{(dryRun ? "  [DRY-RUN 预演，不写入]" : "")}");

                // 类型自动解析所需:PLC 列表 + DB 导出缓存 + (按需构建的)同类型样本变量索引(donor)。
                // 新建/改绑变量时,据所绑 PLC 符号的真实 S7 类型重写"类型四元组",否则克隆沿用模板(本表首个变量)类型 → 与 PLC 符号类型不符被 TIA 拒。
                var plcs = s.FindPlcs();
                var dbCache = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, TypeSpec> donorIndex = null;

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
                        string comment = (f.Length > 5 && f[5].Length > 0) ? f[5] : null;
                        string typeOverride = (f.Length > 6 && f[6].Length > 0) ? f[6] : null;
                        string cycle = (f.Length > 7 && f[7].Length > 0) ? f[7] : null; // 采集周期名(如 "1 s"/"100 ms"/"500 ms"),空=沿用模板/现值
                        bool absolute = string.Equals(access, "Absolute", StringComparison.OrdinalIgnoreCase);
                        // 绝对地址模式: 第4列 plc 当成 LogicalAddress(如 %M0.0/%DB1.DBX0.0),无 PLC 符号可解析类型,故必须给[类型]列
                        if (absolute && typeOverride == null)
                        { Line("错", name + " 访问模式=Absolute 时第4列为绝对地址,必须在第7列[类型]显式给出 S7 类型(如 Bool/Int/Real)"); errors++; continue; }
                        // 变量名含 . 或 \ 都会致导入失败（plc 字段含 . 是合法的 DB.成员，不在此校验）
                        if (name.Contains("\\") || name.Contains("."))
                        { Line("错", name + " 变量名含非法字符(. 或 \\)，导入会失败"); errors++; continue; }
                        string bindLabel = absolute ? $"addr={plc}" : $"plc={plc}"; // 日志按模式显示

                        XElement existing = tagEls.FirstOrDefault(te => string.Equals(TagName(te), name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            // 修复：没有绑定结构时不能假装绑定成功
                            if (!HasBindingStructure(existing))
                            { Line("错", name + " 现有变量无 PLC 绑定结构(LinkList/ControllerTag/Connection)，无法改绑"); errors++; continue; }
                            // 改绑同样要按新 PLC 符号重定类型(否则沿用旧类型与新符号不符,同根因)
                            string note = SetTagType(existing, plc, typeOverride, plcs, dbCache, ref donorIndex, hmi);
                            if (!dryRun) { SetBinding(existing, conn, plc, access); if (comment != null) SetComment(existing, comment); if (cycle != null) SetLinkName(existing, "AcquisitionCycle", cycle); changed = true; }
                            Line("改", $"{name}  conn={conn} {bindLabel}{(cycle != null ? " 周期=" + cycle : "")}  {note}"); updated++;
                        }
                        else
                        {
                            // 修复：模板变量本身无绑定结构时，克隆出来也绑不上，直接报错而非假成功
                            if (!templateBindable)
                            { Line("错", name + " 模板变量无 PLC 绑定结构，无法新建带绑定变量（请选一张含已绑定变量的表）"); errors++; continue; }
                            XElement clone = new XElement(template);
                            SetTagName(clone, name);
                            SetBinding(clone, conn, plc, access);
                            // 关键修复:克隆继承模板(本表首个变量)的类型四元组,必须按所绑 PLC 符号真实类型重写,否则类型不符被拒。
                            string note = SetTagType(clone, plc, typeOverride, plcs, dbCache, ref donorIndex, hmi);
                            if (comment != null) SetComment(clone, comment);
                            if (cycle != null) SetLinkName(clone, "AcquisitionCycle", cycle);
                            RenumberIds(clone, ref maxId);
                            if (!dryRun) { template.Parent.Add(clone); changed = true; }
                            Line("建", $"{name}  conn={conn} {bindLabel}{(cycle != null ? " 周期=" + cycle : "")}  {note}"); created++;
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
        // 绑定 PLC: symbolic 模式写 ControllerTag(符号名)+清空 LogicalAddress;
        // absolute 模式写 LogicalAddress(plcOrAddr 当地址)+清空 ControllerTag。两模式都写 Connection。
        private static void SetBinding(XElement tagEl, string conn, string plcOrAddr, string access)
        {
            bool absolute = string.Equals(access, "Absolute", StringComparison.OrdinalIgnoreCase);
            var al = tagEl.Elements().First(e => e.Name.LocalName == "AttributeList");
            var am = al.Elements().FirstOrDefault(e => e.Name.LocalName == "AddressAccessMode");
            if (am != null) am.Value = access;
            var la = al.Elements().FirstOrDefault(e => e.Name.LocalName == "LogicalAddress");
            if (la != null) la.Value = absolute ? plcOrAddr : "";
            var ll = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            if (ll != null)
            {
                var ct = ll.Elements().FirstOrDefault(e => e.Name.LocalName == "ControllerTag");
                // 绝对模式无 PLC 符号:必须整段移除 ControllerTag 链接,空 Name 的开放链接会被 TIA 拒("open link is empty")。
                if (absolute) ct?.Remove();
                else if (ct != null) ct.Elements().First(e => e.Name.LocalName == "Name").Value = plcOrAddr;
                var cn = ll.Elements().FirstOrDefault(e => e.Name.LocalName == "Connection");
                if (cn != null) cn.Elements().First(e => e.Name.LocalName == "Name").Value = conn;
            }
        }
        // ============ 变量类型自动解析(B方案:据所绑 PLC 符号真实 S7 类型定 HMI 变量类型) ============
        // 背景:经典 HMI 变量的类型由四元组共同定义且必须一致——
        //   AttributeList: <Coding>(如 IEEE754Float/Binary) + <Length>(字节宽);
        //   LinkList:      <DataType><Name>(PLC 侧 S7 类型,如 Word) + <HmiDataType><Name>(HMI 侧类型,如 UInt)。
        // 旧实现克隆模板(本表首个变量)只改名字+绑定,类型四元组原样继承模板 → 与所绑 PLC 符号类型不符,
        // 符号访问下 TIA 拒绝导入("会覆盖 PLC 数据")。本方法把四元组按 PLC 符号真实类型重写。

        private sealed class TypeSpec
        {
            public string Hmi;     // HmiDataType 名(HMI 侧)
            public string Coding;  // 编码
            public string Length;  // 字节长度
            public TypeSpec(string hmi, string coding, string length) { Hmi = hmi; Coding = coding; Length = length; }
        }

        // S7 类型 -> (HmiDataType, Coding, Length)。整数/布尔/字节族实测均 Coding=Binary + 字节宽;
        // 浮点 Real=IEEE754Float/4(实测)、LReal=IEEE754Double/8(标准配对);DTL=Binary/12->DateTime(实测)。
        // 实测来源:本机 KTP700 默认变量表导出(Bool/Byte/Int/UInt/Word/DInt/Real/DTL 已直接核实)。
        private static readonly Dictionary<string, TypeSpec> S7Map = new Dictionary<string, TypeSpec>(StringComparer.OrdinalIgnoreCase)
        {
            { "Bool",  new TypeSpec("Bool",  "Binary",        "1") },
            { "Byte",  new TypeSpec("USInt", "Binary",        "1") },  // 实测 Byte->USInt
            { "SInt",  new TypeSpec("SInt",  "Binary",        "1") },
            { "USInt", new TypeSpec("USInt", "Binary",        "1") },
            { "Char",  new TypeSpec("Char",  "Binary",        "1") },
            { "Word",  new TypeSpec("UInt",  "Binary",        "2") },  // 实测 Word->UInt
            { "Int",   new TypeSpec("Int",   "Binary",        "2") },
            { "UInt",  new TypeSpec("UInt",  "Binary",        "2") },
            { "DWord", new TypeSpec("UDInt", "Binary",        "4") },  // DWord->UDInt
            { "DInt",  new TypeSpec("DInt",  "Binary",        "4") },
            { "UDInt", new TypeSpec("UDInt", "Binary",        "4") },
            { "Real",  new TypeSpec("Real",  "IEEE754Float",  "4") },
            { "LReal", new TypeSpec("LReal", "IEEE754Double", "8") },
            { "DTL",   new TypeSpec("DateTime", "Binary",    "12") },  // 实测 DTL->DateTime
        };

        // DTL(日期时间)子字段类型(DTL 在 DB 导出里不展开成员,故内置)。用于符号路径形如 DB.dtl成员.SECOND。
        private static readonly Dictionary<string, string> DtlFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "YEAR", "UInt" }, { "MONTH", "USInt" }, { "DAY", "USInt" }, { "WEEKDAY", "USInt" },
            { "HOUR", "USInt" }, { "MINUTE", "USInt" }, { "SECOND", "USInt" }, { "NANOSECOND", "UDInt" },
        };

        // 给变量(克隆或现有)按所绑 PLC 符号真实类型重写类型四元组。返回逐条日志用的说明/警告串。
        private static string SetTagType(XElement tagEl, string plcSymbol, string typeOverride,
            List<KeyValuePair<string, PlcSoftware>> plcs, Dictionary<string, XDocument> dbCache,
            ref Dictionary<string, TypeSpec> donorIndex, HmiTarget hmi)
        {
            string s7, rdiag = null;
            if (!string.IsNullOrEmpty(typeOverride)) s7 = NormalizeType(typeOverride); // 第7列[类型]显式指定优先
            else s7 = ResolvePlcSymbolType(plcs, plcSymbol, dbCache, out rdiag);

            if (s7 == null)
                return $"[警告]未能自动解析 PLC 符号类型({rdiag});沿用模板类型,导入可能因类型不符被拒。可在清单第7列[类型]显式指定 S7 类型(如 Bool/Int/Word/Real)。";

            TypeSpec spec;
            if (!S7Map.TryGetValue(s7, out spec))
            {
                if (donorIndex == null) donorIndex = BuildDonorIndex(hmi); // 内置表未覆盖才扫项目里同类型样本变量
                donorIndex.TryGetValue(s7, out spec);
            }
            if (spec == null)
                return $"[警告]已解析 PLC 类型={s7},但内置映射未覆盖且项目内无同类型样本变量;沿用模板类型,导入可能失败。";

            ApplyTypeSpec(tagEl, s7, spec);
            return $"类型={s7}→HMI {spec.Hmi}(Coding={spec.Coding},Len={spec.Length})";
        }

        // 落实类型四元组到变量 XML(元素均已存在于克隆/现有变量,直接改值)。
        private static void ApplyTypeSpec(XElement tagEl, string s7Type, TypeSpec spec)
        {
            SetAttrValue(tagEl, "Coding", spec.Coding);
            SetAttrValue(tagEl, "Length", spec.Length);
            SetLinkName(tagEl, "DataType", s7Type);    // PLC 侧
            SetLinkName(tagEl, "HmiDataType", spec.Hmi); // HMI 侧
        }

        private static void SetAttrValue(XElement tagEl, string key, string val)
        {
            var al = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var c = al?.Elements().FirstOrDefault(e => e.Name.LocalName == key);
            if (c != null) c.Value = val;
        }
        private static void SetLinkName(XElement tagEl, string linkLocalName, string nameVal)
        {
            var ll = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            var link = ll?.Elements().FirstOrDefault(e => e.Name.LocalName == linkLocalName);
            var nm = link?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nm != null) nm.Value = nameVal;
        }
        private static string AttrOf(XElement tagEl, string key)
        {
            var al = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var c = al?.Elements().FirstOrDefault(e => e.Name.LocalName == key && !string.IsNullOrWhiteSpace(e.Value));
            return c?.Value.Trim();
        }
        private static string LinkNameOf(XElement tagEl, string linkLocalName)
        {
            var ll = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
            var link = ll?.Elements().FirstOrDefault(e => e.Name.LocalName == linkLocalName);
            var nm = link?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            return nm != null && !string.IsNullOrWhiteSpace(nm.Value) ? nm.Value.Trim() : null;
        }

        // 写变量注释(MultilingualText CompositionName="Comment" 首个语言项)。结构缺失则静默跳过(不致命)。
        private static void SetComment(XElement tagEl, string comment)
        {
            var ol = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
            var mt = ol?.Elements().FirstOrDefault(e => e.Name.LocalName == "MultilingualText"
                        && e.Attribute("CompositionName")?.Value == "Comment");
            var item = mt?.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultilingualTextItem");
            var text = item?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text");
            if (text != null) text.Value = comment;
        }

        // 项目内现有变量按 PLC 侧 DataType 名建索引(donor):内置 S7Map 未覆盖的类型用它兜底——
        // 直接采信 TIA 自己产出的四元组,正确性最高。仅在需要时构建一次。
        private static Dictionary<string, TypeSpec> BuildDonorIndex(HmiTarget hmi)
        {
            var idx = new Dictionary<string, TypeSpec>(StringComparer.OrdinalIgnoreCase);
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
                        string pt = LinkNameOf(te, "DataType");
                        if (string.IsNullOrEmpty(pt) || idx.ContainsKey(pt)) continue;
                        string hmiT = LinkNameOf(te, "HmiDataType");
                        string cod = AttrOf(te, "Coding");
                        string len = AttrOf(te, "Length");
                        if (hmiT != null && cod != null && len != null) idx[pt] = new TypeSpec(hmiT, cod, len);
                    }
                }
                catch (Exception ex) { Logger.Error("write-tags donor index export " + Safe(() => t.Name), ex); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            return idx;
        }

        // 解析 PLC 符号(如 BatteryDataBlock.Battery_SOC / "DB"."成员"./DTL.SECOND / 裸 PLC 变量)的 S7 元素类型。
        // 解析失败返回 null 并经 diag 说明原因。
        private static string ResolvePlcSymbolType(List<KeyValuePair<string, PlcSoftware>> plcs, string symbol,
            Dictionary<string, XDocument> dbCache, out string diag)
        {
            diag = null;
            var segs = SplitSymbol(symbol);
            if (segs.Count == 0) { diag = "符号为空"; return null; }

            // 单段:可能是裸 PLC 变量
            if (segs.Count == 1)
            {
                string t = FindPlcTagType(plcs, segs[0]);
                if (t != null) return NormalizeType(t);
                diag = "未找到 PLC 变量 " + segs[0];
                return null;
            }

            // 多段:首段当 DB,逐段下钻接口成员
            string dbName = segs[0];
            XDocument doc = GetDbXml(plcs, dbName, dbCache, out string e1);
            if (doc == null)
            {
                string t = FindPlcTagType(plcs, symbol); // 兜底:整体当带点的 PLC 变量名(罕见)
                if (t != null) return NormalizeType(t);
                diag = e1 ?? ("未找到 DB/变量 " + dbName);
                return null;
            }

            var sectionsEl = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Sections");
            if (sectionsEl == null) { diag = "DB " + dbName + " 无接口段"; return null; }
            var members = sectionsEl.Elements().Where(x => x.Name.LocalName == "Section")
                          .SelectMany(sec => sec.Elements().Where(m => m.Name.LocalName == "Member")).ToList();

            string curType = null;
            for (int i = 1; i < segs.Count; i++)
            {
                string seg = StripArrayIndex(segs[i]);
                var mem = members.FirstOrDefault(m => string.Equals((string)m.Attribute("Name"), seg, StringComparison.OrdinalIgnoreCase));
                if (mem == null)
                {
                    if (curType != null && curType.Equals("DTL", StringComparison.OrdinalIgnoreCase)
                        && DtlFields.TryGetValue(seg, out var ft)) { curType = ft; members = new List<XElement>(); continue; }
                    diag = "DB " + dbName + " 中找不到成员 " + seg; return null;
                }
                curType = NormalizeType((string)mem.Attribute("Datatype"));
                if (i < segs.Count - 1) // 还有更深层
                {
                    if (curType.Equals("DTL", StringComparison.OrdinalIgnoreCase)) { members = new List<XElement>(); }
                    else
                    {
                        var children = mem.Elements().Where(x => x.Name.LocalName == "Member").ToList();
                        if (children.Count > 0) members = children; // 内联 struct 下钻
                        else { diag = "成员 " + seg + " 类型 " + curType + " 非内联结构(可能是 UDT),自动解析未支持,请用[类型]列显式指定"; return null; }
                    }
                }
            }
            return curType;
        }

        // 按引号边界拆分符号路径(TIA 用 "..." 包含特殊字符标识符);各段去引号、丢空段。
        private static List<string> SplitSymbol(string s)
        {
            var res = new List<string>(); var sb = new System.Text.StringBuilder(); bool q = false;
            foreach (char c in s ?? "")
            {
                if (c == '"') { q = !q; continue; }
                if (c == '.' && !q) { res.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            res.Add(sb.ToString());
            return res.Where(x => x.Length > 0).ToList();
        }

        // 规整数据类型名:剥 Array[..] of X -> X、String[n]/WString[n] -> String/WString、去引号空白。
        private static string NormalizeType(string dt)
        {
            if (dt == null) return null;
            dt = dt.Trim();
            var m = Regex.Match(dt, @"^Array\[.*?\]\s+of\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) dt = m.Groups[1].Value.Trim();
            var ms = Regex.Match(dt, @"^(W?String)\s*\[", RegexOptions.IgnoreCase);
            if (ms.Success) dt = ms.Groups[1].Value;
            return dt.Trim('"').Trim();
        }
        private static string StripArrayIndex(string seg) => Regex.Replace(seg ?? "", @"\[.*\]$", "");

        // 在所有 PLC 的变量表里递归找裸变量,返回其 DataTypeName。
        private static string FindPlcTagType(List<KeyValuePair<string, PlcSoftware>> plcs, string name)
        {
            foreach (var kv in plcs)
            {
                var grp = kv.Value.TagTableGroup;
                string t = SearchTagTables(grp.TagTables, grp.Groups, name);
                if (t != null) return t;
            }
            return null;
        }
        private static string SearchTagTables(PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string name)
        {
            foreach (PlcTagTable tt in tables)
                foreach (PlcTag tag in tt.Tags)
                    if (string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)) return Safe(() => tag.DataTypeName);
            foreach (PlcTagTableUserGroup g in groups)
            {
                string r = SearchTagTables(g.TagTables, g.Groups, name);
                if (r != null) return r;
            }
            return null;
        }

        // 导出 DB 块为 SimaticML(经 %TEMP% 校验明文)并缓存,供成员类型下钻。非 DB/受保护/失败 → null + diag。
        private static XDocument GetDbXml(List<KeyValuePair<string, PlcSoftware>> plcs, string dbName,
            Dictionary<string, XDocument> cache, out string diag)
        {
            diag = null;
            if (cache.TryGetValue(dbName, out var cached)) { if (cached == null) diag = "未找到 DB " + dbName; return cached; }

            PlcBlock blk = null;
            foreach (var kv in plcs) { blk = FindBlockByName(kv.Value.BlockGroup, dbName); if (blk != null) break; }
            if (blk == null) { cache[dbName] = null; diag = "未找到 DB " + dbName; return null; }

            string tmp = IoUtil.NewTempFile(".xml");
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                blk.Export(new FileInfo(tmp), ExportOptions.None);
                var doc = XDocument.Parse(IoUtil.ReadPlaintext(tmp));
                cache[dbName] = doc;
                return doc;
            }
            catch (Exception ex) { cache[dbName] = null; diag = "导出 " + dbName + " 失败: " + ex.Message + "(图形/受保护块或非 DB)"; Logger.Error("write-tags resolve db " + dbName, ex); return null; }
            finally { try { File.Delete(tmp); } catch { } }
        }
        private static PlcBlock FindBlockByName(PlcBlockGroup group, string name)
        {
            foreach (PlcBlock b in group.Blocks)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            foreach (PlcBlockGroup sub in group.Groups)
            {
                var found = FindBlockByName(sub, name);
                if (found != null) return found;
            }
            return null;
        }
        private static string Safe(Func<object> f) { try { return f()?.ToString(); } catch { return null; } }

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
