using System;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaMcp
{
    /// <summary>
    /// 读取类工具：变量表 / UDT。都是内存对象，不落盘、无加密问题。
    /// 【待核实签名】类型名按官方理解写，编译器拿真实 DLL 校验，报错就改。
    /// </summary>
    internal static class Reads
    {
        // ========== read-tags：列出 PLC 变量表及变量(名称/类型/地址) ==========
        public static int ReadTags()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }

                foreach (var kv in plcs)
                {
                    Console.WriteLine($"==== PLC: {kv.Key} · 变量表 ====");
                    PlcTagTableSystemGroup group = kv.Value.TagTableGroup;
                    int n = WalkTagTables(group.TagTables, group.Groups, "");
                    Console.WriteLine($"-- 本 PLC 共 {n} 个变量 --");
                    Console.WriteLine();
                }
                return 0;
            }
        }

        private static int WalkTagTables(PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string path)
        {
            int count = 0;
            foreach (PlcTagTable tt in tables)
            {
                string here = (path.Length > 0 ? path + "/" : "") + tt.Name;
                Console.WriteLine($"  [表] {here}");
                foreach (PlcTag tag in tt.Tags)
                {
                    // 读中文注释(MultilingualText)喂给 AI——最有价值的语义；空则不显示
                    string cmt = CommentText(() => tag.Comment);
                    Console.WriteLine($"      {tag.Name,-32} {Safe(() => tag.DataTypeName),-16} @ {Safe(() => tag.LogicalAddress),-12}{(cmt.Length > 0 ? "  // " + cmt : "")}");
                    count++;
                }
            }
            foreach (PlcTagTableUserGroup g in groups)
                count += WalkTagTables(g.TagTables, g.Groups, (path.Length > 0 ? path + "/" : "") + g.Name);
            return count;
        }

        // ========== read-udts：列出所有 UDT(PlcType) ==========
        public static int ReadUdts()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }

                foreach (var kv in plcs)
                {
                    Console.WriteLine($"==== PLC: {kv.Key} · UDT 自定义类型 ====");
                    PlcTypeSystemGroup tg = kv.Value.TypeGroup;
                    int n = WalkTypes(tg.Types, tg.Groups, "");
                    Console.WriteLine($"-- 本 PLC 共 {n} 个 UDT --");
                    Console.WriteLine();
                }
                Console.WriteLine("提示：要看某个 UDT 的完整成员定义，下一步可加 export-udt（GenerateSource 导出 .udt 文本）。");
                return 0;
            }
        }

        private static int WalkTypes(PlcTypeComposition types, PlcTypeUserGroupComposition groups, string path)
        {
            int count = 0;
            foreach (PlcType t in types)
            {
                Console.WriteLine($"  [UDT] {(path.Length > 0 ? path + "/" : "")}{t.Name}");
                count++;
            }
            foreach (PlcTypeUserGroup g in groups)
                count += WalkTypes(g.Types, g.Groups, (path.Length > 0 ? path + "/" : "") + g.Name);
            return count;
        }

        // ========== project-info：项目级元信息（名称/作者/路径/版本/时间/注释） ==========
        public static int ProjectInfo()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var p = s.Project;
                Console.WriteLine("==== 项目信息 ====");
                Console.WriteLine($"  名称: {p.Name}");
                Console.WriteLine($"  路径: {Safe(() => p.Path)}");
                Console.WriteLine($"  作者: {Safe(() => p.Author)}");
                Console.WriteLine($"  创建时间: {Safe(() => p.CreationTime)}");
                Console.WriteLine($"  最后修改: {Safe(() => p.LastModified)}  by {Safe(() => p.LastModifiedBy)}");
                Console.WriteLine($"  版本: {Safe(() => p.Version)}");
                Console.WriteLine($"  注释: {CommentText(() => p.Comment)}");
                Console.WriteLine($"  有未保存改动(IsModified): {Safe(() => p.IsModified)}");
                return 0;
            }
        }

        // ========== block-info <块名>：块元数据（块号/语言/日期/一致性/作者/版本/布局），纯只读 ==========
        public static int BlockInfo(string blockName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock b = s.FindBlock(blockName, out owner);
                if (b == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }
                Console.WriteLine($"==== 块信息: {b.Name} ====");
                Console.WriteLine($"  类型: {b.GetType().Name}   语言: {Attr(b, "ProgrammingLanguage")}");
                Console.WriteLine($"  块号(Number): {Attr(b, "Number")}   自动编号: {Attr(b, "AutoNumber")}   存储布局: {Attr(b, "MemoryLayout")}");
                Console.WriteLine($"  一致(IsConsistent): {Attr(b, "IsConsistent")}   受保护: {Attr(b, "IsKnowHowProtected")}   写保护: {Attr(b, "IsWriteProtected")}");
                Console.WriteLine($"  作者/族/标题/版本: {Attr(b, "HeaderAuthor")} / {Attr(b, "HeaderFamily")} / {Attr(b, "HeaderName")} / {Attr(b, "HeaderVersion")}");
                Console.WriteLine($"  创建/修改/编译: {Attr(b, "CreationDate")} / {Attr(b, "ModifiedDate")} / {Attr(b, "CompileDate")}");
                if (b.GetType().Name == "InstanceDB")
                    Console.WriteLine($"  所属FB(InstanceOfName): {Attr(b, "InstanceOfName")}");
                return 0;
            }
        }

        // MultilingualText 取首个语言项文本（注释是多语言对象，空则空串）
        private static string CommentText(Func<MultilingualText> f)
        {
            try
            {
                var ml = f();
                if (ml == null) return "";
                var item = ml.Items.FirstOrDefault();
                return item != null ? (item.Text ?? "") : "";
            }
            catch { return "?"; }
        }

        private static string Attr(IEngineeringObject o, string name)
        {
            try { var v = o.GetAttribute(name); return v?.ToString() ?? ""; }
            catch { return "?"; }
        }

        private static string Safe(Func<object> f)
        {
            try { return f()?.ToString() ?? ""; } catch { return "?"; }
        }
    }
}
