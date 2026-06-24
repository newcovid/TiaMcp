using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;                       // SecureString（know-how 保护密码）
using System.Text;
using System.Text.RegularExpressions;        // dry-run 块名启发式扫描
using System.Xml.Linq;                        // dry-run 解析 SimaticML 块名
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace TiaMcp
{
    /// <summary>
    /// 阶段2 的读写/编译能力，先做成命令行子命令，阶段5 再包成 MCP 工具。
    /// 文件 I/O 一律经 IoUtil（%TEMP% 中转 + 明文校验），应对本机 E-SafeNet 加密。
    /// </summary>
    internal static class Commands
    {
        // ========== list ==========
        public static int List()
        {
            using (var s = TiaSession.AttachFirst())
            {
                Console.WriteLine($"当前项目: {s.Project.Name}");
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 0; }

                int total = 0;
                foreach (var kv in plcs)
                {
                    Console.WriteLine();
                    Console.WriteLine($"==== PLC: {kv.Key} ====");
                    total += ListBlocks(kv.Value.BlockGroup, "Program blocks");
                }
                Console.WriteLine();
                Console.WriteLine($"共 {total} 个块。");
                return 0;
            }
        }

        private static int ListBlocks(PlcBlockGroup group, string path)
        {
            int count = 0;
            foreach (PlcBlock block in group.Blocks)
            {
                string kind = block.GetType().Name;
                string lang; bool isProt;
                GetLangAndProtected(block, out lang, out isProt);
                Console.WriteLine($"  [{kind,-11}] {block.Name,-32} 语言={lang,-6}{(isProt ? " 🔒" : "")} 路径={path}");
                count++;
            }
            foreach (PlcBlockGroup sub in group.Groups)
                count += ListBlocks(sub, path + "/" + sub.Name);
            return count;
        }

        // ========== export-source：读出 SCL/STL 源码（文本即交付物） ==========
        public static int ExportSource(string blockName, string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock block = s.FindBlock(blockName, out owner);
                if (block == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }

                string lang = SafeLang(block);
                bool isScl = lang.Equals("SCL", StringComparison.OrdinalIgnoreCase);
                bool isStl = lang.Equals("STL", StringComparison.OrdinalIgnoreCase);
                if (!isScl && !isStl)
                {
                    Console.WriteLine($"块 {blockName} 语言={lang}。GenerateSource 只支持 SCL/STL；");
                    Console.WriteLine("图形语言(LAD/FBD/GRAPH)请用 export-xml（整块 SimaticML 导出）。");
                    return 2;
                }

                string ext = isScl ? ".scl" : ".awl";
                string tempFile = IoUtil.NewTempFile(ext);
                try
                {
                    // TIA 导出到 %TEMP%（加密豁免区）
                    owner.ExternalSourceGroup.GenerateSource(
                        new List<PlcBlock> { block }, new FileInfo(tempFile), GenerateOptions.None);

                    // 读出明文（内部会校验不是密文、去 BOM）
                    string sourceText = IoUtil.ReadPlaintext(tempFile);

                    // 文本就是交付物：CLI 下整段打印；MCP 阶段会作为返回值。
                    Console.WriteLine($"==== {blockName} ({lang}) 源码开始 ====");
                    Console.WriteLine(sourceText);
                    Console.WriteLine($"==== 源码结束（{sourceText.Length} 字符）====");

                    // 可选：另存明文副本（给 Git 用），并告警全盘扫描可能稍后加密它。
                    if (!string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        string outFile = Path.Combine(outDir, MakeSafeFileName(blockName) + ext);
                        File.WriteAllText(outFile, sourceText, new UTF8Encoding(false));
                        if (IoUtil.LooksEncrypted(File.ReadAllBytes(outFile)))
                            Console.WriteLine($"[警告] 刚写出的 {outFile} 已是密文，不能当明文留存。");
                        else
                            Console.WriteLine($"[已存明文副本] {outFile}（注意：本机全盘扫描可能稍后将其加密，请尽快提交 Git）");
                    }
                    Logger.Info($"export-source {blockName} 成功");
                    return 0;
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        // ========== export-xml：把任意块导出为 SimaticML XML（图形块也行） ==========
        // 经 %TEMP% 明文中转；默认整段打印，给目录则另存明文副本。也是阶段3 的基础。
        public static int ExportXml(string blockName, string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock block = s.FindBlock(blockName, out owner);
                if (block == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }

                string tempFile = IoUtil.NewTempFile(".xml");
                try
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile); // Export 要求目标不存在
                    block.Export(new FileInfo(tempFile), ExportOptions.None);
                    string xml = IoUtil.ReadPlaintext(tempFile);

                    Console.WriteLine($"==== {blockName} SimaticML 开始 ====");
                    Console.WriteLine(xml);
                    Console.WriteLine($"==== 结束（{xml.Length} 字符）====");

                    if (!string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        string outFile = Path.Combine(outDir, MakeSafeFileName(blockName) + ".xml");
                        File.WriteAllText(outFile, xml, new UTF8Encoding(false));
                        if (IoUtil.LooksEncrypted(File.ReadAllBytes(outFile)))
                            Console.WriteLine($"[警告] 刚写出的 {outFile} 已是密文。");
                        else
                            Console.WriteLine($"[已存明文副本] {outFile}（全盘扫描可能稍后加密它）");
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Logger.Error($"导出 XML {blockName} 失败", ex);
                    Console.WriteLine("[错误] 导出失败: " + ex.Message);
                    return 2;
                }
                finally { try { File.Delete(tempFile); } catch { } }
            }
        }

        // ========== import-scl：明文 SCL -> TEMP(验证) -> 生成块 -> 编译报错 ==========
        public static int ImportScl(string sclFilePath, bool dryRun = false, string device = null)
        {
            if (!File.Exists(sclFilePath)) { Console.WriteLine($"SCL 文件不存在: {sclFilePath}"); return 1; }

            byte[] inBytes = File.ReadAllBytes(sclFilePath);
            if (IoUtil.LooksEncrypted(inBytes))
            {
                Console.WriteLine("输入 SCL 文件是密文（被 E-SafeNet 加密）。");
                Console.WriteLine("请改用明文来源（MCP 阶段由 AI 直接传入文本，不经磁盘）。");
                return 3;
            }
            string sclText = IoUtil.DecodeUtf8StripBom(inBytes);

            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                string selErr, plcLabel;
                PlcSoftware plc = SelectPlc(plcs, device, out plcLabel, out selErr);
                if (plc == null) { Console.WriteLine(selErr); return 1; }
                Console.WriteLine($"目标 PLC: {plcLabel}");

                var before = new HashSet<string>(AllBlockNames(plc.BlockGroup), StringComparer.OrdinalIgnoreCase);

                if (dryRun)
                {
                    var names = ScanSclBlockNames(sclText);
                    Console.WriteLine($"[DRY-RUN] 启发式扫描到 {names.Count} 个块定义（未导入）：");
                    foreach (var nm in names)
                        Console.WriteLine($"  {nm}  {(before.Contains(nm) ? "→ 覆盖现有同名块" : "→ 新建")}");
                    if (names.Count == 0) Console.WriteLine("  (未识别到块头;实际导入仍可能生成块——以去掉 --dry-run 的真实结果为准)");
                    Console.WriteLine("（预演，未导入。去掉 --dry-run 实际执行。）");
                    return 0;
                }

                // 写到 %TEMP% 并校验仍是明文（防全盘扫描），失败自动重试
                string tempScl = IoUtil.WriteTempPlaintextVerified(sclText, ".scl");

                string srcName = "mcp_import_" + DateTime.Now.ToString("HHmmss");
                PlcExternalSource src = null;
                try
                {
                    Console.WriteLine($"创建外部源 {srcName} <- (TEMP 明文中转, 已校验)");
                    src = plc.ExternalSourceGroup.ExternalSources.CreateFromFile(srcName, tempScl);
                    Console.WriteLine("从源生成块 ...");
                    src.GenerateBlocksFromSource();
                }
                catch (Exception ex)
                {
                    Logger.Error("从源生成块失败", ex);
                    Console.WriteLine("[错误] 从源生成块失败: " + ex.Message);
                    Console.WriteLine("（若怀疑临时文件被全盘扫描加密，请重试一次。）");
                    return 2;
                }
                finally
                {
                    if (src != null) { try { src.Delete(); } catch (Exception ex) { Logger.Error("删除外部源失败(可忽略)", ex); } }
                    try { File.Delete(tempScl); } catch { }
                }

                var added = AllBlockNames(plc.BlockGroup).Where(n => !before.Contains(n)).ToList();
                Console.WriteLine(added.Count > 0
                    ? "新增块: " + string.Join(", ", added)
                    : "没有新增块（可能覆盖了已有同名块）。");

                Console.WriteLine();
                // 只编译新增的块（信号干净）；没有新增就编译整个 PLC。
                if (added.Count > 0)
                {
                    int rc = 0;
                    foreach (var name in added)
                    {
                        PlcSoftware ignore;
                        PlcBlock b = s.FindBlock(name, out ignore);
                        if (b == null) continue;
                        Console.WriteLine($"编译 {name} ...");
                        rc = Math.Max(rc, PrintAndCode(CompileObject(b)));
                    }
                    return rc;
                }
                Console.WriteLine("编译该 PLC ...");
                return PrintAndCode(CompileObject(plc));
            }
        }

        // ========== compile <块名> ==========
        public static int Compile(string blockName)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock block = s.FindBlock(blockName, out owner);
                if (block == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }

                Console.WriteLine($"编译块 {blockName} ...");
                return PrintAndCode(CompileObject(block));
            }
        }

        // ========== import-xml：把(AI改好的)SimaticML 整块导入，覆盖同名块（图形块的"写"）==========
        public static int ImportXml(string xmlFilePath, bool dryRun = false, string device = null)
        {
            if (!File.Exists(xmlFilePath)) { Console.WriteLine($"XML 文件不存在: {xmlFilePath}"); return 1; }
            byte[] inBytes = File.ReadAllBytes(xmlFilePath);
            if (IoUtil.LooksEncrypted(inBytes))
            {
                Console.WriteLine("输入 XML 是密文（被加密）。请改用明文来源（MCP 阶段由 AI 直接传入）。");
                return 3;
            }
            string xmlText = IoUtil.DecodeUtf8StripBom(inBytes);

            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                string selErr, plcLabel;
                PlcSoftware plc = SelectPlc(plcs, device, out plcLabel, out selErr);
                if (plc == null) { Console.WriteLine(selErr); return 1; }
                Console.WriteLine($"目标 PLC: {plcLabel}");

                var before = new HashSet<string>(AllBlockNames(plc.BlockGroup), StringComparer.OrdinalIgnoreCase);

                if (dryRun)
                {
                    var names = ScanXmlBlockNames(xmlText);
                    Console.WriteLine($"[DRY-RUN] SimaticML 含 {names.Count} 个块（未导入）：");
                    foreach (var nm in names)
                        Console.WriteLine($"  {nm}  {(before.Contains(nm) ? "→ Override 覆盖现有" : "→ 新建")}");
                    if (names.Count == 0) Console.WriteLine("  (未能从 XML 解析出块名——结构可能非标准 SimaticML)");
                    Console.WriteLine("（预演，未导入。去掉 --dry-run 实际执行。）");
                    return 0;
                }

                string tmp = IoUtil.WriteTempPlaintextVerified(xmlText, ".xml");
                try
                {
                    Console.WriteLine("导入 SimaticML（Override 覆盖同名块）...");
                    // 【待核实签名】PlcBlockGroup.Blocks.Import(FileInfo, ImportOptions.Override)
                    plc.BlockGroup.Blocks.Import(new FileInfo(tmp), ImportOptions.Override);
                }
                catch (Exception ex)
                {
                    Logger.Error("导入 XML 失败", ex);
                    Console.WriteLine("[错误] 导入失败: " + ex.Message);
                    Console.WriteLine("（SimaticML 对 schema 极敏感：结构/版本不符会被拒。建议先 export-xml 拿正确骨架再改；复杂逻辑改动优先重写为 SCL。）");
                    return 2;
                }
                finally { try { File.Delete(tmp); } catch { } }

                var added = AllBlockNames(plc.BlockGroup).Where(n => !before.Contains(n)).ToList();
                Console.WriteLine(added.Count > 0 ? "新增块: " + string.Join(", ", added) : "覆盖了已有同名块（无新增）。");

                Console.WriteLine();
                Console.WriteLine("提示：图形块整块替换后，建议对该块及其调用方做一次编译核对。");
                return 0;
            }
        }

        // ========== delete-block：删除一个块（破坏性）。删前查引用 + know-how 检查 + --dry-run/--force ==========
        public static int DeleteBlock(string blockName, bool dryRun, bool force)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock block = s.FindBlock(blockName, out owner);
                if (block == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }
                string kind = block.GetType().Name;
                if (TiaSession.IsKnowHowProtected(block))
                    Console.WriteLine($"[提示] {blockName} 受 know-how 保护（删除会一并删掉其受保护逻辑）。");

                // 删前引用检查（内容扫描，复用交叉引用套件）
                var referrers = CrossRef.FindReferrers(s, blockName);
                if (referrers.Count > 0)
                {
                    Console.WriteLine($"[警告] {blockName} 仍被 {referrers.Count} 个块引用：{string.Join(", ", referrers)}");
                    if (!force)
                    {
                        Console.WriteLine("拒绝删除（仍被引用）。确认无误后加 --force 强删，或先解除引用。");
                        return 2;
                    }
                }
                if (dryRun)
                {
                    Console.WriteLine($"[DRY-RUN] 将删除 {blockName} ({kind})"
                        + (referrers.Count > 0 ? "（被引用，需 --force 才会真的删）" : "") + "。未删除。");
                    return 0;
                }
                try { block.Delete(); }
                catch (Exception ex) { Logger.Error($"删除 {blockName} 失败", ex); Console.WriteLine("[错误] 删除失败: " + ex.Message); return 2; }
                Console.WriteLine($"已删除块 {blockName} ({kind})。");
                return 0;
            }
        }

        // ========== project-save：把 Openness 所做改动落盘（写命令闭环关键，否则只停在 TIA 内存）==========
        // 注意：Save() 是 TIA 进程自身写 .apXX 工程包，不经 %TEMP%、不涉 E-SafeNet 明文中转。
        public static int ProjectSave()
        {
            using (var s = TiaSession.AttachFirst())
            {
                bool modified = true;
                try { modified = s.Project.IsModified; } catch { }
                if (!modified) { Console.WriteLine("项目无未保存改动（IsModified=false），跳过保存。"); return 0; }
                try { s.Project.Save(); Console.WriteLine($"已保存项目 {s.Project.Name}（注意：会保存整个项目，含你在博图里的手改）。"); return 0; }
                catch (Exception ex) { Logger.Error("保存项目失败", ex); Console.WriteLine("[错误] 保存失败: " + ex.Message); return 2; }
            }
        }

        // ========== rename-block：改块名(SetAttribute Name)。Openness 改名不更新引用——告警 + 改后编译核对 ==========
        public static int RenameBlock(string oldName, string newName, bool dryRun)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock b = s.FindBlock(oldName, out owner);
                if (b == null) { Console.WriteLine($"找不到块: {oldName}"); return 1; }
                if (TiaSession.IsKnowHowProtected(b))
                { Console.WriteLine($"[拒绝] {oldName} 受 know-how 保护，无法改名（请先在博图解锁）。"); return 2; }

                Console.WriteLine($"重命名 {oldName} -> {newName}{(dryRun ? "  [DRY-RUN 预演]" : "")}");
                var referrers = CrossRef.FindReferrers(s, oldName);
                if (referrers.Count > 0)
                {
                    Console.WriteLine($"[警告] {oldName} 被 {referrers.Count} 个块引用：{string.Join(", ", referrers)}");
                    Console.WriteLine("  注意：Openness 改名【不会】自动更新这些引用——改后它们会出现未解析引用，须逐一重新编译并手动修复！");
                }
                if (dryRun) { Console.WriteLine("（预演，未改名）"); return 0; }
                try { ((IEngineeringObject)b).SetAttribute("Name", newName); }
                catch (Exception ex) { Console.WriteLine("[错误] 改名失败: " + ex.Message + "（命名规则违例或重名？）"); Logger.Error("rename-block", ex); return 2; }
                Console.WriteLine($"已改名为 {newName}。建议 compile {newName} 及各引用方核对。");
                return 0;
            }
        }

        // ========== unlock-block：移除 know-how 保护(Openness Unprotect)。块名可选,省略=对全部受保护块逐个尝试该密码 ==========
        // 官方依据：手册 §5.11 p566-568，block.GetService<PlcBlockProtectionProvider>().Unprotect(SecureString)。
        // 注意：博图里"双击+输密码"只是临时打开,Openness 仍读不到;必须本工具或博图取消保护,IsKnowHowProtected 才变 false。
        public static int UnlockBlock(string blockNamesCsv, string password, bool dryRun)
        {
            if (!dryRun && string.IsNullOrEmpty(password))
            { Console.WriteLine("[错误] 需提供 --password（dry-run 预览可不填）。"); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                // 1) 目标集合
                var targets = new List<PlcBlock>();
                bool explicitList = !string.IsNullOrWhiteSpace(blockNamesCsv);
                if (explicitList)
                {
                    foreach (var raw in blockNamesCsv.Split(','))
                    {
                        var name = raw.Trim();
                        if (name.Length == 0) continue;
                        PlcSoftware owner;
                        var b = s.FindBlock(name, out owner);
                        if (b == null) { Console.WriteLine($"[未找到] {name}"); continue; }
                        targets.Add(b);
                    }
                }
                else
                {
                    foreach (var kv in s.FindPlcs())
                        foreach (var blk in EnumerateAllBlocks(kv.Value.BlockGroup))
                            if (TiaSession.IsKnowHowProtected(blk)) targets.Add(blk);
                }

                if (targets.Count == 0)
                { Console.WriteLine(explicitList ? "无有效目标块。" : "项目中没有受 know-how 保护的块。"); return 0; }

                // 2) dry-run：只列目标 + 当前保护状态，不调 API、不需密码
                if (dryRun)
                {
                    Console.WriteLine($"[DRY-RUN] 将尝试解锁 {targets.Count} 个块（未改动）：");
                    foreach (var b in targets)
                        Console.WriteLine($"  {b.Name}  保护={(TiaSession.IsKnowHowProtected(b) ? "是" : "否")}");
                    return 0;
                }

                // 3) 逐块 Unprotect
                int ok = 0, badPwd = 0, already = 0, unavail = 0, failed = 0;
                foreach (var b in targets)
                {
                    string name = b.Name;
                    if (explicitList && !TiaSession.IsKnowHowProtected(b))
                    { Console.WriteLine($"[跳过] {name}：本就无保护"); already++; continue; }

                    PlcBlockProtectionProvider prov;
                    try { prov = b.GetService<PlcBlockProtectionProvider>(); }
                    catch { prov = null; }
                    if (prov == null)
                    { Console.WriteLine($"[不可用] {name}：服务不可用（需先编译/非代码块或全局DB/在线/不支持）"); unavail++; continue; }

                    try
                    {
                        using (var ss = MakeSecure(password)) prov.Unprotect(ss);
                        if (TiaSession.IsKnowHowProtected(b))
                        { Console.WriteLine($"[失败] {name}：调用后仍显示受保护"); failed++; }
                        else { Console.WriteLine($"[OK] {name} 已解锁"); ok++; }
                    }
                    catch (Exception ex)
                    {
                        string m = ex.Message ?? "";
                        if (m.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.Contains("拒绝") || m.Contains("密码"))
                        { Console.WriteLine($"[跳过] {name}：密码不符"); badPwd++; }
                        else if (m.IndexOf("without protection", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.Contains("未受保护"))
                        { Console.WriteLine($"[跳过] {name}：已无保护"); already++; }
                        else { Console.WriteLine($"[失败] {name}：{m}"); Logger.Error($"unlock-block {name}", ex); failed++; }
                    }
                }

                Console.WriteLine($"小结：解锁 {ok} / 密码不符 {badPwd} / 已无保护 {already} / 不可用 {unavail} / 失败 {failed}");
                Console.WriteLine("提示：解锁是内存改动，未落盘。读完用 lock-block 重新加锁；要持久化另调 project-save（会连同博图手改一并落盘）。");
                return (badPwd > 0 || failed > 0) ? 2 : 0;
            }
        }

        // ========== lock-block：设置 know-how 保护(Openness Protect)。块名必填(只按显式块名,防误锁) ==========
        // 官方依据：手册 §5.11 p566，block.GetService<PlcBlockProtectionProvider>().Protect(SecureString)。
        public static int LockBlock(string blockNamesCsv, string password, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(blockNamesCsv))
            { Console.WriteLine("用法: lock-block <块名,逗号分隔> --password <pwd> [--dry-run]"); return 1; }
            if (!dryRun && string.IsNullOrEmpty(password))
            { Console.WriteLine("[错误] 需提供 --password。"); return 1; }

            using (var s = TiaSession.AttachFirst())
            {
                var targets = new List<PlcBlock>();
                foreach (var raw in blockNamesCsv.Split(','))
                {
                    var name = raw.Trim();
                    if (name.Length == 0) continue;
                    PlcSoftware owner;
                    var b = s.FindBlock(name, out owner);
                    if (b == null) { Console.WriteLine($"[未找到] {name}"); continue; }
                    targets.Add(b);
                }
                if (targets.Count == 0) { Console.WriteLine("无有效目标块。"); return 0; }

                if (dryRun)
                {
                    Console.WriteLine($"[DRY-RUN] 将尝试加锁 {targets.Count} 个块（未改动）：");
                    foreach (var b in targets)
                    {
                        bool prot = TiaSession.IsKnowHowProtected(b);
                        bool svc; try { svc = b.GetService<PlcBlockProtectionProvider>() != null; } catch { svc = false; }
                        Console.WriteLine($"  {b.Name}  当前保护={(prot ? "是" : "否")}  可加锁={(svc && !prot ? "是" : "否")}");
                    }
                    return 0;
                }

                int ok = 0, already = 0, unavail = 0, failed = 0;
                foreach (var b in targets)
                {
                    string name = b.Name;
                    if (TiaSession.IsKnowHowProtected(b))
                    { Console.WriteLine($"[跳过] {name}：已受保护"); already++; continue; }

                    PlcBlockProtectionProvider prov;
                    try { prov = b.GetService<PlcBlockProtectionProvider>(); }
                    catch { prov = null; }
                    if (prov == null)
                    { Console.WriteLine($"[不可用] {name}：服务不可用（需先编译/非代码块或全局DB/在线/不支持）"); unavail++; continue; }

                    try
                    {
                        using (var ss = MakeSecure(password)) prov.Protect(ss);
                        Console.WriteLine($"[OK] {name} 已加锁"); ok++;
                    }
                    catch (Exception ex)
                    { Console.WriteLine($"[失败] {name}：{ex.Message}"); Logger.Error($"lock-block {name}", ex); failed++; }
                }

                Console.WriteLine($"小结：加锁 {ok} / 已受保护 {already} / 不可用 {unavail} / 失败 {failed}");
                Console.WriteLine("提示：加锁是内存改动，未落盘。要持久化用 project-save。");
                return failed > 0 ? 2 : 0;
            }
        }

        // ========== export-watchtable <目录>：导出所有监控表 + 强制表(表定义，非运行期活值)到 XML ==========
        public static int ExportWatchtables(string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                Directory.CreateDirectory(outDir);
                int ok = 0, fail = 0, dup = 0;
                var usedPaths = new HashSet<string>();   // 防跨 PLC 同名表互相覆盖
                foreach (var kv in plcs)
                {
                    var grp = kv.Value.WatchAndForceTableGroup;
                    foreach (PlcWatchTable wt in grp.WatchTables)
                        if (ExportOneXml(fi => wt.Export(fi, ExportOptions.None), UniqueExportPath(outDir, "Watch_" + MakeSafeFileName(wt.Name), ".xml", usedPaths, ref dup))) ok++; else fail++;
                    foreach (PlcForceTable ft in grp.ForceTables)
                        if (ExportOneXml(fi => ft.Export(fi, ExportOptions.None), UniqueExportPath(outDir, "Force_" + MakeSafeFileName(ft.Name), ".xml", usedPaths, ref dup))) ok++; else fail++;
                }
                Console.WriteLine($"导出完成：{ok} 个表 -> {Path.GetFullPath(outDir)}；失败 {fail}（多为'表不一致'/空强制表，需先在博图修复该表；详情见日志）。");
                if (dup > 0) Console.WriteLine($"[警告] {dup} 个表名重复（多为跨 PLC 同名），已加 _dupN 后缀避免互相覆盖。");
                Console.WriteLine("监控/强制表=表定义(非运行期活值)。注意：全盘扫描可能稍后加密这些文件，尽快 git add。");
                return 0;
            }
        }

        // 导出一个对象到 XML：经 %TEMP% 明文校验再写目标
        private static bool ExportOneXml(Action<FileInfo> exportFn, string targetPath)
        {
            string tmp = IoUtil.NewTempFile(".xml");
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                exportFn(new FileInfo(tmp));
                string xml = IoUtil.ReadPlaintext(tmp);
                File.WriteAllText(targetPath, xml, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Logger.Error("导出表失败 " + targetPath, ex); return false; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ========== set-block-number <块名> <编号>：改块号(先关 AutoNumber 再设 Number)，号冲突会拒 ==========
        public static int SetBlockNumber(string blockName, int number, bool dryRun)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcBlock b = s.FindBlock(blockName, out owner);
                if (b == null) { Console.WriteLine($"找不到块: {blockName}"); return 1; }
                if (TiaSession.IsKnowHowProtected(b))
                { Console.WriteLine($"[拒绝] {blockName} 受 know-how 保护，无法改块号（V15.1+ 限制）。"); return 2; }
                var eo = (IEngineeringObject)b;
                string cur = "?"; try { cur = eo.GetAttribute("Number")?.ToString(); } catch { }
                Console.WriteLine($"块 {blockName} 块号 {cur} -> {number}{(dryRun ? "  [DRY-RUN 预演]" : "")}");
                if (dryRun) { Console.WriteLine("（预演，未改）"); return 0; }
                try
                {
                    try { if (eo.GetAttribute("AutoNumber") is bool an && an) eo.SetAttribute("AutoNumber", false); } catch { }
                    eo.SetAttribute("Number", number);
                }
                catch (Exception ex) { Console.WriteLine("[错误] 改块号失败: " + ex.Message + "（号被占用/越界？）"); Logger.Error("set-block-number", ex); return 2; }
                Console.WriteLine($"已设块号为 {number}。建议 compile 核对。");
                return 0;
            }
        }

        // ========== export-project-texts <目录> [语言]：导出全工程可翻译文本(块/网络注释+HMI文本)到 xlsx 批改 ==========
        // xlsx 是二进制：TIA 写 %TEMP%(豁免)，我们进程拷到目标(明文未加密)。明文校验不适用(非文本)。
        public static int ExportProjectTexts(string outDir, string langCode)
        {
            using (var s = TiaSession.AttachFirst())
            {
                Directory.CreateDirectory(outDir);
                CultureInfo ci;
                try { ci = CultureInfo.GetCultureInfo(string.IsNullOrEmpty(langCode) ? "zh-CN" : langCode); }
                catch { Console.WriteLine($"无效语言代码: {langCode}"); return 1; }
                string tmp = IoUtil.NewTempFile(".xlsx");
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    s.Project.ExportProjectTexts(new FileInfo(tmp), ci, ci); // 导出语言=参考语言，编辑该语言文本
                    string outFile = Path.Combine(outDir, $"ProjectTexts_{ci.Name}.xlsx");
                    File.Copy(tmp, outFile, true);
                    Console.WriteLine($"已导出项目文本 -> {Path.GetFullPath(outFile)}（语言 {ci.Name}）");
                    Console.WriteLine("用 Excel 批改注释/文本后，用 import-project-texts 导回。注意：全盘扫描可能稍后加密它，尽快编辑。");
                    return 0;
                }
                catch (Exception ex) { Logger.Error("ExportProjectTexts", ex); Console.WriteLine("[错误] 导出失败: " + ex.Message + "（语言是否在项目中？）"); return 2; }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        // ========== import-project-texts <xlsx>：把批改后的文本导回工程 ==========
        public static int ImportProjectTexts(string xlsxFile)
        {
            if (!File.Exists(xlsxFile)) { Console.WriteLine("文件不存在: " + xlsxFile); return 1; }
            using (var s = TiaSession.AttachFirst())
            {
                string tmp = IoUtil.NewTempFile(".xlsx");
                try
                {
                    File.Copy(xlsxFile, tmp, true); // 经 %TEMP%(豁免)导入，避免源文件被加密/锁
                    var result = s.Project.ImportProjectTexts(new FileInfo(tmp), true);
                    Console.WriteLine("已导入项目文本（状态/日志见 TIA）。建议 read-tags 复核注释，并 project-save 落盘。");
                    return 0;
                }
                catch (Exception ex) { Logger.Error("ImportProjectTexts", ex); Console.WriteLine("[错误] 导入失败: " + ex.Message); return 2; }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        // ========== compile-device：编译整个 PLC ==========
        public static int CompileDevice()
        {
            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                int rc = 0;
                foreach (var kv in plcs)
                {
                    Console.WriteLine($"编译 PLC {kv.Key} ...");
                    rc = Math.Max(rc, PrintAndCode(CompileObject(kv.Value))); // PlcSoftware 实现 IEngineeringServiceProvider
                }
                return rc;
            }
        }

        // ========== export-all：所有块导出为明文(SCL/STL 源码 或 图形块 XML)，给 Git ==========
        public static int ExportAll(string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                Directory.CreateDirectory(outDir);
                int ok = 0, skipped = 0, dup = 0;
                var usedPaths = new HashSet<string>();   // 防同名块(跨组/跨PLC或清洗后重名)平铺互相覆盖
                foreach (var kv in s.FindPlcs())
                    foreach (var blk in EnumerateAllBlocks(kv.Value.BlockGroup))
                    {
                        if (TiaSession.IsKnowHowProtected(blk)) { skipped++; continue; }
                        string lang = SafeLang(blk);
                        bool isStl = lang.Equals("STL", StringComparison.OrdinalIgnoreCase);
                        bool textual = isStl || lang.Equals("SCL", StringComparison.OrdinalIgnoreCase);
                        string ext = textual ? (isStl ? ".awl" : ".scl") : ".xml";
                        string tmp = IoUtil.NewTempFile(ext);
                        try
                        {
                            if (textual)
                                kv.Value.ExternalSourceGroup.GenerateSource(
                                    new List<PlcBlock> { blk }, new FileInfo(tmp), GenerateOptions.None);
                            else
                            {
                                if (File.Exists(tmp)) File.Delete(tmp);
                                blk.Export(new FileInfo(tmp), ExportOptions.None);
                            }
                            string text = IoUtil.ReadPlaintext(tmp);
                            string outFile = UniqueExportPath(outDir, MakeSafeFileName(blk.Name), ext, usedPaths, ref dup);
                            File.WriteAllText(outFile, text, new UTF8Encoding(false));
                            ok++;
                        }
                        catch (Exception ex) { Logger.Error($"导出 {blk.Name} 失败", ex); skipped++; }
                        finally { try { File.Delete(tmp); } catch { } }
                    }
                Console.WriteLine($"导出完成：{ok} 个块 -> {Path.GetFullPath(outDir)}；跳过(受保护/失败) {skipped} 个。");
                if (dup > 0) Console.WriteLine($"[警告] {dup} 个块名清洗后重复（跨组/跨PLC同名或含非法文件名字符），已加 _dupN 后缀避免互相覆盖。");
                Console.WriteLine("注意：本机全盘扫描可能稍后加密这些文件，请尽快 git add/commit。");
                return 0;
            }
        }

        // ========== export-udt：导出一个 UDT 的定义文本 ==========
        public static int ExportUdt(string udtName, string outDir)
        {
            using (var s = TiaSession.AttachFirst())
            {
                PlcSoftware owner;
                PlcType udt = FindUdt(s, udtName, out owner);
                if (udt == null) { Console.WriteLine($"找不到 UDT: {udtName}"); return 1; }
                string tmp = IoUtil.NewTempFile(".udt");
                try
                {
                    owner.ExternalSourceGroup.GenerateSource(
                        new List<PlcType> { udt }, new FileInfo(tmp), GenerateOptions.None);
                    string text = IoUtil.ReadPlaintext(tmp);
                    Console.WriteLine($"==== UDT {udtName} 定义 ====");
                    Console.WriteLine(text);
                    if (!string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        File.WriteAllText(Path.Combine(outDir, MakeSafeFileName(udtName) + ".udt"), text, new UTF8Encoding(false));
                        Console.WriteLine($"[已存] {outDir}（全盘扫描可能稍后加密）");
                    }
                    return 0;
                }
                catch (Exception ex) { Logger.Error($"导出 UDT {udtName} 失败", ex); Console.WriteLine("[错误] " + ex.Message); return 2; }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        // ========== import-udt：从明文 .udt 生成/覆盖 UDT ==========
        public static int ImportUdt(string udtFilePath, bool dryRun = false, string device = null)
        {
            if (!File.Exists(udtFilePath)) { Console.WriteLine($"UDT 文件不存在: {udtFilePath}"); return 1; }
            byte[] inBytes = File.ReadAllBytes(udtFilePath);
            if (IoUtil.LooksEncrypted(inBytes)) { Console.WriteLine("输入是密文，请用明文来源。"); return 3; }
            string text = IoUtil.DecodeUtf8StripBom(inBytes);

            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                string selErr, plcLabel;
                PlcSoftware plc = SelectPlc(plcs, device, out plcLabel, out selErr);
                if (plc == null) { Console.WriteLine(selErr); return 1; }
                Console.WriteLine($"目标 PLC: {plcLabel}");

                if (dryRun)
                {
                    var names = ScanUdtNames(text);
                    var existing = new HashSet<string>(AllUdtNames(plc), StringComparer.OrdinalIgnoreCase);
                    Console.WriteLine($"[DRY-RUN] 扫描到 {names.Count} 个 UDT 定义（未导入）：");
                    foreach (var nm in names)
                        Console.WriteLine($"  {nm}  {(existing.Contains(nm) ? "→ 覆盖现有" : "→ 新建")}");
                    if (names.Count == 0) Console.WriteLine("  (未识别到 TYPE 头;以去掉 --dry-run 的真实结果为准)");
                    Console.WriteLine("（预演，未导入。去掉 --dry-run 实际执行。）");
                    return 0;
                }

                string tmp = IoUtil.WriteTempPlaintextVerified(text, ".udt");
                PlcExternalSource src = null;
                try
                {
                    string srcName = "mcp_udt_" + DateTime.Now.ToString("HHmmss");
                    src = plc.ExternalSourceGroup.ExternalSources.CreateFromFile(srcName, tmp);
                    src.GenerateBlocksFromSource();
                    Console.WriteLine("已从源生成/覆盖 UDT。");
                }
                catch (Exception ex) { Logger.Error("生成 UDT 失败", ex); Console.WriteLine("[错误] " + ex.Message); return 2; }
                finally
                {
                    if (src != null) { try { src.Delete(); } catch { } }
                    try { File.Delete(tmp); } catch { }
                }
                Console.WriteLine("编译该 PLC 核对 ...");
                return PrintAndCode(CompileObject(plc));
            }
        }

        // ========== write-tags：批量建变量。行格式 表名|变量名|类型|地址，# 注释 ==========
        public static int WriteTags(string listFile, bool dryRun = false, string device = null)
        {
            if (!File.Exists(listFile)) { Console.WriteLine($"清单不存在: {listFile}"); return 1; }
            byte[] inBytes = File.ReadAllBytes(listFile);
            if (IoUtil.LooksEncrypted(inBytes)) { Console.WriteLine("清单是密文，请用明文。"); return 3; }
            string[] lines = IoUtil.DecodeUtf8StripBom(inBytes).Replace("\r\n", "\n").Split('\n');

            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                string selErr, plcLabel;
                PlcSoftware plc = SelectPlc(plcs, device, out plcLabel, out selErr);
                if (plc == null) { Console.WriteLine(selErr); return 1; }
                Console.WriteLine($"目标 PLC: {plcLabel}" + (dryRun ? "  [DRY-RUN 预演，不写入]" : ""));
                int created = 0, failed = 0;
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var p = line.Split('|');
                    if (p.Length < 3) { Console.WriteLine($"跳过(列不足): {line}"); failed++; continue; }
                    string table = p[0].Trim(), name = p[1].Trim(), type = p[2].Trim();
                    // 地址可空：无地址 = PLC 内部符号变量（手册支持空串地址 Create(name,type,"")）。
                    string addr = p.Length >= 4 ? p[3].Trim() : "";
                    if (dryRun)
                    {
                        PlcTagTable tt = FindTagTableTop(plc, table);
                        bool tagExists = tt != null && tt.Tags.Find(name) != null;
                        Console.WriteLine($"  [预] {table}/{name} : {type}{(string.IsNullOrEmpty(addr) ? "（符号变量）" : " @ " + addr)}"
                            + (tt == null ? "  → 将新建变量表 " + table : (tagExists ? "  → [警告]变量已存在,真实导入会失败" : "")));
                        created++; continue;
                    }
                    try
                    {
                        PlcTagTable tt = FindOrCreateTagTable(plc, table);
                        // PlcTagComposition.Create(name, dataType, logicalAddress)；地址空串=不绑定外设的符号变量
                        PlcTag tag = tt.Tags.Create(name, type, addr ?? "");
                        created++;
                        Console.WriteLine($"  + {table}/{name} : {type}{(string.IsNullOrEmpty(addr) ? "（无地址/符号变量）" : " @ " + addr)}");
                    }
                    catch (Exception ex) { Logger.Error($"建变量 {name} 失败", ex); Console.WriteLine($"  ! {name} 失败: {ex.Message}"); failed++; }
                }
                Console.WriteLine($"完成：{(dryRun ? "预览将新建" : "新建")} {created}，失败 {failed}。{(dryRun ? "（预演，未写入。去掉 --dry-run 实际执行。）" : "")}");
                return failed > 0 ? 2 : 0;
            }
        }

        // ========== delete-tags：按清单删 PLC 变量。行 表名|变量名 或 变量名（表名省略=全表搜）==========
        public static int DeleteTags(string listFile, bool dryRun, string device = null)
        {
            if (!File.Exists(listFile)) { Console.WriteLine($"清单不存在: {listFile}"); return 1; }
            byte[] inBytes = File.ReadAllBytes(listFile);
            if (IoUtil.LooksEncrypted(inBytes)) { Console.WriteLine("清单是密文，请用明文。"); return 3; }
            string[] lines = IoUtil.DecodeUtf8StripBom(inBytes).Replace("\r\n", "\n").Split('\n');

            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                string selErr, plcLabel;
                PlcSoftware plc = SelectPlc(plcs, device, out plcLabel, out selErr);
                if (plc == null) { Console.WriteLine(selErr); return 1; }
                Console.WriteLine($"目标 PLC: {plcLabel}" + (dryRun ? "  [DRY-RUN 预演，不删除]" : ""));
                Console.WriteLine("警告: 删除被程序/HMI 引用的变量会破坏引用方；建议先 where-used 核对。");

                var tables = new List<PlcTagTable>();
                CollectPlcTagTables(plc.TagTableGroup.TagTables, plc.TagTableGroup.Groups, tables);
                int deleted = 0, skipped = 0;
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var p = line.Split('|').Select(x => x.Trim()).ToArray();
                    string tableName = p.Length >= 2 ? p[0] : null;
                    string name = p.Length >= 2 ? p[1] : p[0];
                    var candidates = tableName != null
                        ? tables.Where(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
                        : tables;
                    PlcTag tag = null;
                    foreach (var t in candidates) { tag = t.Tags.Find(name); if (tag != null) break; }
                    if (tag == null) { Console.WriteLine($"  [跳] 找不到变量: {name}"); skipped++; continue; }
                    try { if (!dryRun) tag.Delete(); Console.WriteLine($"  [删] {name}"); deleted++; }
                    catch (Exception ex) { Console.WriteLine($"  [错] {name} 删除失败: {ex.Message}"); skipped++; Logger.Error("delete-tags", ex); }
                }
                Console.WriteLine($"汇总: 删 {deleted} / 跳 {skipped}{(dryRun ? "（预演，未删除）" : "")}");
                return skipped > 0 ? 2 : 0;   // 与 edit-tags 一致：有跳过/失败时退出码非零（MCP isError 据此置位）
            }
        }

        private static void CollectPlcTagTables(PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, List<PlcTagTable> acc)
        {
            foreach (PlcTagTable t in tables) acc.Add(t);
            foreach (PlcTagTableUserGroup g in groups) CollectPlcTagTables(g.TagTables, g.Groups, acc);
        }

        // ========== edit-tags：改已有 PLC 变量的类型/地址。行 表名|变量名|新类型|新地址（空=不改该项）==========
        public static int EditTags(string listFile, bool dryRun, string device = null)
        {
            if (!File.Exists(listFile)) { Console.WriteLine($"清单不存在: {listFile}"); return 1; }
            byte[] inBytes = File.ReadAllBytes(listFile);
            if (IoUtil.LooksEncrypted(inBytes)) { Console.WriteLine("清单是密文，请用明文。"); return 3; }
            string[] lines = IoUtil.DecodeUtf8StripBom(inBytes).Replace("\r\n", "\n").Split('\n');

            using (var s = TiaSession.AttachFirst())
            {
                var plcs = s.FindPlcs();
                if (plcs.Count == 0) { Console.WriteLine("没有找到 PLC。"); return 1; }
                string selErr, plcLabel;
                PlcSoftware plc = SelectPlc(plcs, device, out plcLabel, out selErr);
                if (plc == null) { Console.WriteLine(selErr); return 1; }
                Console.WriteLine($"目标 PLC: {plcLabel}" + (dryRun ? "  [DRY-RUN 预演，不改]" : ""));
                var tables = new List<PlcTagTable>();
                CollectPlcTagTables(plc.TagTableGroup.TagTables, plc.TagTableGroup.Groups, tables);
                int changed = 0, skipped = 0;
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var p = line.Split('|').Select(x => x.Trim()).ToArray();
                    if (p.Length < 2) { Console.WriteLine($"  [跳] 列不足: {line}"); skipped++; continue; }
                    string table = p[0], name = p[1];
                    string newType = p.Length >= 3 && p[2].Length > 0 ? p[2] : null;
                    string newAddr = p.Length >= 4 && p[3].Length > 0 ? p[3] : null;
                    PlcTag tag = null;
                    foreach (var t in tables.Where(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)))
                    { tag = t.Tags.Find(name); if (tag != null) break; }
                    if (tag == null) { Console.WriteLine($"  [跳] 找不到变量: {table}/{name}"); skipped++; continue; }
                    if (dryRun) { Console.WriteLine($"  [改] {table}/{name} -> 类型={newType ?? "(不变)"} 地址={newAddr ?? "(不变)"}"); changed++; continue; }
                    try
                    {
                        var eo = (IEngineeringObject)tag;
                        if (newType != null) eo.SetAttribute("DataTypeName", newType);
                        if (newAddr != null) eo.SetAttribute("LogicalAddress", newAddr);
                        Console.WriteLine($"  [改] {table}/{name} -> 类型={newType ?? "(不变)"} 地址={newAddr ?? "(不变)"}"); changed++;
                    }
                    catch (Exception ex) { Console.WriteLine($"  [错] {name}: {ex.Message}"); skipped++; Logger.Error("edit-tags", ex); }
                }
                Console.WriteLine($"汇总: 改 {changed} / 跳 {skipped}{(dryRun ? "（预演，未改）" : "")}");
                return skipped > 0 ? 2 : 0;
            }
        }

        // ========== project-archive <目录> [归档名]：归档项目为 .zapXX（经 %TEMP% 豁免再拷出，避免被加密）==========
        public static int ProjectArchive(string outDir, string name)
        {
            using (var s = TiaSession.AttachFirst())
            {
                // Archive 要求项目已保存（Openness 硬性前提）。不替用户静默保存——明确要求先 project-save。
                bool modified = false; try { modified = s.Project.IsModified; } catch { }
                if (modified)
                {
                    Console.WriteLine("[拒绝] 项目有未保存改动，Archive 要求先保存。请先运行 project-save（或在博图保存）后再归档。");
                    return 2;
                }
                Directory.CreateDirectory(outDir);
                string archiveName = string.IsNullOrEmpty(name) ? "ProjectArchive" : name;
                string tmpDir = Path.Combine(Path.GetTempPath(), "TiaMcp_arch_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
                try
                {
                    s.Project.Archive(new DirectoryInfo(tmpDir), archiveName, ProjectArchivationMode.Compressed);
                    var files = Directory.GetFiles(tmpDir);
                    if (files.Length == 0) { Console.WriteLine("[错误] 归档未生成文件。"); return 2; }
                    string dst = Path.Combine(outDir, Path.GetFileName(files[0]));
                    File.Copy(files[0], dst, true);
                    Console.WriteLine($"已归档项目 -> {Path.GetFullPath(dst)}（{new FileInfo(dst).Length / 1024} KB，压缩）");
                    Console.WriteLine("注意：全盘扫描可能稍后加密它，尽快转存/上传。");
                    return 0;
                }
                catch (Exception ex) { Logger.Error("ProjectArchive", ex); Console.WriteLine("[错误] 归档失败: " + ex.Message); return 2; }
                finally { try { Directory.Delete(tmpDir, true); } catch { } }
            }
        }

        // ---------- 私有辅助 ----------

        // F12 选目标 PLC：device 空=第一个(多 PLC 时 label 提示可选项);指定名=精确(大小写不敏感)匹配,找不到回 null + 列可选。
        private static PlcSoftware SelectPlc(List<KeyValuePair<string, PlcSoftware>> plcs, string device, out string label, out string err)
        {
            err = null; label = null;
            if (!string.IsNullOrEmpty(device))
            {
                foreach (var kv in plcs)
                    if (string.Equals(kv.Key, device, StringComparison.OrdinalIgnoreCase)) { label = kv.Key + "（按 --device 指定）"; return kv.Value; }
                err = $"找不到 PLC: {device}。可选: {string.Join(", ", plcs.Select(p => p.Key))}";
                return null;
            }
            label = plcs[0].Key + (plcs.Count > 1 ? $"（项目有多个 PLC，默认第一个；--device <名> 可指定，可选: {string.Join(", ", plcs.Select(p => p.Key))}）" : "");
            return plcs[0].Value;
        }

        private static IEnumerable<string> AllBlockNames(PlcBlockGroup group)
        {
            foreach (PlcBlock b in group.Blocks) yield return b.Name;
            foreach (PlcBlockGroup sub in group.Groups)
                foreach (var n in AllBlockNames(sub)) yield return n;
        }

        private static string SafeLang(PlcBlock b)
        {
            try { return b.ProgrammingLanguage.ToString(); } catch { return "?"; }
        }

        // 一次 GetAttributes 批量取 语言+保护，减少 COM 往返（手册 2854）；失败回退逐个属性。
        private static void GetLangAndProtected(PlcBlock b, out string lang, out bool isProtected)
        {
            try
            {
                var vals = ((IEngineeringObject)b).GetAttributes(new[] { "ProgrammingLanguage", "IsKnowHowProtected" });
                lang = vals[0]?.ToString() ?? "?";
                isProtected = vals[1] is bool bv && bv;
            }
            catch
            {
                lang = SafeLang(b);
                isProtected = TiaSession.IsKnowHowProtected(b);
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // 在 outDir 下给 baseName+ext 取一个本次导出未用过的路径；重名则加 _dupN 后缀并计数。
        // 只防"同一次导出"内的互相覆盖；重跑同一目录仍按原名覆盖旧导出（不会无限堆 _dupN）。
        private static string UniqueExportPath(string dir, string baseName, string ext, HashSet<string> used, ref int dupCount)
        {
            string key = (baseName + ext).ToLowerInvariant();
            if (used.Add(key)) return Path.Combine(dir, baseName + ext);
            dupCount++;
            int i = 2; string b;
            do { b = baseName + "_dup" + i; i++; } while (!used.Add((b + ext).ToLowerInvariant()));
            return Path.Combine(dir, b + ext);
        }

        // dry-run 用：启发式扫描 SCL 文本里的块定义名（先去注释，避免注释里的关键字误报）。
        private static List<string> ScanSclBlockNames(string scl)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(scl)) return names;
            string code = Regex.Replace(scl, @"\(\*.*?\*\)", " ", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\r\n]*", " ");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(code,
                @"\b(?:FUNCTION_BLOCK|ORGANIZATION_BLOCK|DATA_BLOCK|FUNCTION|TYPE)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
                RegexOptions.IgnoreCase))
            {
                string nm = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (nm.Length > 0 && seen.Add(nm)) names.Add(nm);
            }
            return names;
        }

        // dry-run 用：扫描 .udt 文本里的 TYPE 名。
        private static List<string> ScanUdtNames(string udt)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(udt)) return names;
            string code = Regex.Replace(udt, @"\(\*.*?\*\)", " ", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\r\n]*", " ");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(code, @"\bTYPE\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))", RegexOptions.IgnoreCase))
            {
                string nm = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (nm.Length > 0 && seen.Add(nm)) names.Add(nm);
            }
            return names;
        }

        // dry-run 用：从 SimaticML XML 解析块名（SW.Blocks.* 元素的 AttributeList/Name）。
        private static List<string> ScanXmlBlockNames(string xml)
        {
            var names = new List<string>();
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var el in doc.Descendants())
                {
                    if (!el.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)) continue;
                    var al = el.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                    var nm = al?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                    if (!string.IsNullOrEmpty(nm)) names.Add(nm);
                }
            }
            catch { }
            return names;
        }

        // 顶层变量表(不建)查找——与 WriteTags 的 FindOrCreateTagTable 同口径，供 dry-run 预览判存在。
        private static PlcTagTable FindTagTableTop(PlcSoftware plc, string tableName)
        {
            foreach (PlcTagTable tt in plc.TagTableGroup.TagTables)
                if (string.Equals(tt.Name, tableName, StringComparison.OrdinalIgnoreCase)) return tt;
            return null;
        }

        // 项目内全部 UDT 名（含分组），供 import-udt dry-run 判新建/覆盖。
        private static IEnumerable<string> AllUdtNames(PlcSoftware plc)
        {
            return CollectUdtNames(plc.TypeGroup.Types, plc.TypeGroup.Groups);
        }
        private static IEnumerable<string> CollectUdtNames(PlcTypeComposition types, PlcTypeUserGroupComposition groups)
        {
            foreach (PlcType t in types) yield return t.Name;
            foreach (PlcTypeUserGroup g in groups)
                foreach (var n in CollectUdtNames(g.Types, g.Groups)) yield return n;
        }

        private static IEnumerable<PlcBlock> EnumerateAllBlocks(PlcBlockGroup group)
        {
            foreach (PlcBlock b in group.Blocks) yield return b;
            foreach (PlcBlockGroup sub in group.Groups)
                foreach (var b in EnumerateAllBlocks(sub)) yield return b;
        }

        // 由明文构造只读 SecureString，供 PlcBlockProtectionProvider.Protect/Unprotect 用。
        // 注意：密码只在内存停留，绝不落盘/记日志/回显。
        private static SecureString MakeSecure(string s)
        {
            var ss = new SecureString();
            if (s != null) foreach (char c in s) ss.AppendChar(c);
            ss.MakeReadOnly();
            return ss;
        }

        private static PlcType FindUdt(TiaSession s, string name, out PlcSoftware owner)
        {
            foreach (var kv in s.FindPlcs())
            {
                var found = FindUdtIn(kv.Value.TypeGroup.Types, kv.Value.TypeGroup.Groups, name);
                if (found != null) { owner = kv.Value; return found; }
            }
            owner = null;
            return null;
        }

        private static PlcType FindUdtIn(PlcTypeComposition types, PlcTypeUserGroupComposition groups, string name)
        {
            foreach (PlcType t in types)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            foreach (PlcTypeUserGroup g in groups)
            {
                var f = FindUdtIn(g.Types, g.Groups, name);
                if (f != null) return f;
            }
            return null;
        }

        private static PlcTagTable FindOrCreateTagTable(PlcSoftware plc, string tableName)
        {
            foreach (PlcTagTable tt in plc.TagTableGroup.TagTables)
                if (string.Equals(tt.Name, tableName, StringComparison.OrdinalIgnoreCase)) return tt;
            return plc.TagTableGroup.TagTables.Create(tableName);
        }

        /// <summary>对一个工程对象(块或 PLC 软件)执行编译。
        /// GetService&lt;T&gt; 在 IEngineeringServiceProvider 上（PlcBlock/PlcSoftware 都实现它）。</summary>
        private static CompilerResult CompileObject(IEngineeringServiceProvider obj)
        {
            ICompilable compilable = obj.GetService<ICompilable>();
            if (compilable == null) { Console.WriteLine("该对象不支持编译。"); return null; }
            return compilable.Compile();
        }

        /// <summary>打印编译结果并返回退出码：0=无错误，2=有错误，1=没拿到结果。</summary>
        private static int PrintAndCode(CompilerResult result)
        {
            if (result == null) { Console.WriteLine("编译未返回结果。"); return 1; }
            Console.WriteLine($"编译状态: {result.State}  错误: {result.ErrorCount}  警告: {result.WarningCount}");
            PrintMessages(result.Messages, 1);
            return result.ErrorCount > 0 ? 2 : 0;
        }

        private static void PrintMessages(CompilerResultMessageComposition messages, int depth)
        {
            if (messages == null) return;
            string indent = new string(' ', depth * 2);
            foreach (CompilerResultMessage m in messages)
            {
                // 只打印 Error/Warning（丢 Info/Success 噪声，省上下文）；但始终递归下钻——真错误可能在子节点
                string st = m.State.ToString();
                if (st.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || st.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0)
                    Console.WriteLine($"{indent}[{m.State}] {m.Description}  ({m.Path})");
                PrintMessages(m.Messages, depth + 1);
            }
        }
    }
}
