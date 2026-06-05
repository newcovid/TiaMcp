using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaMcp
{
    /// <summary>
    /// 阶段5：手写 stdio JSON-RPC 2.0 的 MCP 服务器（不引官方 SDK）。
    /// 协议走 stdin/stdout 行分隔 JSON；诊断/日志走 stderr+文件，绝不污染 stdout。
    /// 做法：把每条工具调用映射成一个 argv，重定向 Console.Out 到 StringWriter 捕获命令输出，
    /// 调用现有 Program.Dispatch(argv)，把捕获文本作为工具结果返回——不改动 54 条命令本身。
    /// 文本类入参(SCL/XML/UDT/清单)接 inline 字符串，内部经 %TEMP% 写临时文件再传路径，即用即删。
    /// 本类不出现任何 Siemens.Engineering.* 类型（Siemens 代码都在 Dispatch 以下）。
    /// </summary>
    internal static class McpServer
    {
        private const string ProtocolVersion = "2024-11-05";
        private const string ServerName = "tia-mcp";
        private const string ServerVersion = "1.0.0";

        public static int Run()
        {
            // 协议独占的真实 stdout（独立于 Console.Out，命令的 Console.WriteLine 会被重定向走）
            var rpc = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            Console.SetOut(TextWriter.Null); // 空闲态：任何 stray Console.WriteLine 不落真实 stdout
            Logger.Info("MCP 服务器启动 (stdio JSON-RPC, 54 工具)");

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                Dictionary<string, object> msg;
                try { msg = ser.Deserialize<Dictionary<string, object>>(line); }
                catch (Exception ex) { Logger.Error("MCP 解析失败: " + line, ex); continue; }

                object id = msg.ContainsKey("id") ? msg["id"] : null;
                string method = msg.ContainsKey("method") ? msg["method"] as string : null;
                var prms = msg.ContainsKey("params") ? msg["params"] as Dictionary<string, object> : null;
                try { Handle(rpc, ser, id, method, prms); }
                catch (Exception ex)
                {
                    Logger.Error("MCP 处理失败: " + method, ex);
                    if (id != null) WriteError(rpc, ser, id, -32603, "Internal error: " + ex.Message);
                }
            }
            Logger.Info("MCP 服务器退出 (stdin EOF)");
            return 0;
        }

        private static void Handle(TextWriter o, JavaScriptSerializer ser, object id, string method, Dictionary<string, object> prms)
        {
            switch (method)
            {
                case "initialize":
                    WriteResult(o, ser, id, new Dictionary<string, object>
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                        ["serverInfo"] = new Dictionary<string, object> { ["name"] = ServerName, ["version"] = ServerVersion }
                    });
                    break;
                case "notifications/initialized":
                case "initialized":
                case "notifications/cancelled":
                    break; // 通知，无响应
                case "ping":
                    WriteResult(o, ser, id, new Dictionary<string, object>());
                    break;
                case "tools/list":
                    WriteResult(o, ser, id, new Dictionary<string, object> { ["tools"] = ToolsList() });
                    break;
                case "tools/call":
                    HandleToolCall(o, ser, id, prms);
                    break;
                default:
                    if (id != null) WriteError(o, ser, id, -32601, "Method not found: " + method);
                    break;
            }
        }

        private static void HandleToolCall(TextWriter o, JavaScriptSerializer ser, object id, Dictionary<string, object> prms)
        {
            string name = prms != null && prms.ContainsKey("name") ? prms["name"] as string : null;
            var args = prms != null && prms.ContainsKey("arguments") ? prms["arguments"] as Dictionary<string, object> : null;
            if (args == null) args = new Dictionary<string, object>();
            var tool = Tools.FirstOrDefault(t => t.Name == name);
            if (tool == null) { WriteError(o, ser, id, -32602, "Unknown tool: " + name); return; }

            var result = RunTool(tool, args);
            WriteResult(o, ser, id, new Dictionary<string, object>
            {
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = result.Item1 } },
                ["isError"] = result.Item2
            });
        }

        // 运行一条工具：建 argv -> 重定向捕获 stdout -> Program.Dispatch -> 还原 -> 删临时文件
        private static Tuple<string, bool> RunTool(ToolDef t, Dictionary<string, object> a)
        {
            var tempFiles = new List<string>();
            var sw = new StringWriter();
            bool isError = false;
            int rc = 0;
            string note = null;
            try
            {
                string buildErr;
                var argv = BuildArgs(t, a, tempFiles, out note, out buildErr);
                if (buildErr != null) return Tuple.Create(buildErr, true);
                Logger.Info("MCP 工具调用: " + string.Join(" ", argv.Take(1)) + " (" + (argv.Length - 1) + " args)");
                Console.SetOut(sw);
                try { rc = Program.Dispatch(argv); }
                catch (Exception ex) { isError = true; sw.Write("\n[异常] " + ex.Message); Logger.Error("MCP 工具异常: " + t.Name, ex); }
            }
            finally
            {
                Console.SetOut(TextWriter.Null);
                foreach (var f in tempFiles) { try { File.Delete(f); } catch { } }
            }
            string text = sw.ToString();
            if (note != null) text = note + "\n" + text;
            if (text.Length == 0) text = "(无输出)";
            if (!isError) text += $"\n(退出码 {rc})";
            return Tuple.Create(text, isError);
        }

        // 工具的有效"文件路径"入参名: 显式 PathParam 优先; 否则 TextParam 以 "Text" 结尾时自动派生 <名>Path; 都不满足回退 <名>Path/null
        private static string PathParamName(ToolDef t)
        {
            if (!string.IsNullOrEmpty(t.PathParam)) return t.PathParam;
            if (t.TextParam == null) return null;
            if (t.TextParam.EndsWith("Text", StringComparison.Ordinal))
                return t.TextParam.Substring(0, t.TextParam.Length - 4) + "Path";
            return t.TextParam + "Path";
        }

        // inline 文本入参既可 inline(写 %TEMP%) 也可传磁盘路径(<名>Path 直通)。
        // note: 两者都给时的提示(path 优先); error: 两者都没给时的报错(短路, 不进 Dispatch)。
        private static string[] BuildArgs(ToolDef t, Dictionary<string, object> a, List<string> tempFiles, out string note, out string error)
        {
            note = null; error = null;
            var argv = new List<string> { t.Name };
            if (t.TextParam != null)
            {
                string pathName = PathParamName(t);
                string path = pathName != null ? Str(a, pathName) : null;
                string text = Str(a, t.TextParam);
                bool hasPath = !string.IsNullOrEmpty(path);
                bool hasText = !string.IsNullOrEmpty(text);
                if (hasPath)
                {
                    if (hasText) note = $"[注意] 同时提供了 {t.TextParam} 与 {pathName}，已采用文件路径 {pathName}。";
                    argv.Add(path); // 路径直通，不写 TEMP（避免大文件内联）
                }
                else if (hasText)
                {
                    string tmp = IoUtil.NewTempFile(t.TextExt);
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    tempFiles.Add(tmp);
                    argv.Add(tmp);
                }
                else
                {
                    error = $"需提供 {t.TextParam}（内联文本）或 {pathName}（磁盘文件路径）之一。大文件请用 {pathName}。";
                    return null;
                }
            }
            foreach (var r in t.Req) { var v = Str(a, r); if (v != null) argv.Add(v); }
            foreach (var op in t.Opt) { var v = Str(a, op); if (!string.IsNullOrEmpty(v)) argv.Add(v); }
            foreach (var vf in t.ValueFlags) { var v = Str(a, vf); if (!string.IsNullOrEmpty(v)) { argv.Add("--" + vf); argv.Add(v); } }
            foreach (var f in t.Flags) { if (Bool(a, f)) argv.Add("--" + f); }
            return argv.ToArray();
        }

        // ---------- tools/list schema ----------
        private static object[] ToolsList()
        {
            return Tools.Select(t => (object)new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Desc,
                ["inputSchema"] = Schema(t)
            }).ToArray();
        }

        private static Dictionary<string, object> Schema(ToolDef t)
        {
            var props = new Dictionary<string, object>();
            var required = new List<string>();
            if (t.TextParam != null)
            {
                string pathName = PathParamName(t);
                // 有路径伴生参时 text 不再 required(二选一)；否则维持必填
                props[t.TextParam] = StrProp(PDesc(t, t.TextParam, "inline 文本内容（小片段可内联；大文件改用 " + pathName + "，工具内部经 %TEMP% 中转，勿在此填磁盘路径）"));
                if (pathName == null) required.Add(t.TextParam);
                else props[pathName] = StrProp(PDesc(t, pathName, "本地磁盘文件路径（明文 .xml/.scl/.udt/.txt）；大产物走此参免内联爆 token，与 " + t.TextParam + " 二选一。常用对应 export-* 工具的产物文件路径"));
            }
            foreach (var r in t.Req) { props[r] = StrProp(PDesc(t, r, r)); required.Add(r); }
            foreach (var op in t.Opt) props[op] = StrProp(PDesc(t, op, op + "（可选）"));
            foreach (var vf in t.ValueFlags) props[vf] = StrProp(PDesc(t, vf, vf + "（可选）"));
            foreach (var f in t.Flags) props[f] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = PDesc(t, f, f) };
            var s = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) s["required"] = required.ToArray();
            return s;
        }

        // 参数/旗标描述：优先取 ToolDef.P 登记的，缺省回退（默认仅为参数名，故 P 是 AI 正确选参的关键）
        private static string PDesc(ToolDef t, string key, string fallback)
        {
            string d;
            return (t.P != null && t.P.TryGetValue(key, out d)) ? d : fallback;
        }

        private static Dictionary<string, object> StrProp(string desc) =>
            new Dictionary<string, object> { ["type"] = "string", ["description"] = desc };

        // ---------- JSON-RPC 写出 ----------
        private static void WriteResult(TextWriter o, JavaScriptSerializer ser, object id, object result)
        {
            o.WriteLine(ser.Serialize(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }));
        }
        private static void WriteError(TextWriter o, JavaScriptSerializer ser, object id, int code, string message)
        {
            o.WriteLine(ser.Serialize(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
            }));
        }

        private static string Str(Dictionary<string, object> a, string key)
        {
            object v; return (a != null && a.TryGetValue(key, out v) && v != null) ? v.ToString() : null;
        }
        private static bool Bool(Dictionary<string, object> a, string key)
        {
            object v;
            if (a != null && a.TryGetValue(key, out v) && v != null)
                return v is bool b ? b : string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private sealed class ToolDef
        {
            public string Name;
            public string Desc;
            public string[] Req = new string[0];        // 必填位置参（按序）
            public string[] Opt = new string[0];        // 可选位置参（按序，在必填之后）
            public string[] Flags = new string[0];      // 布尔旗标 -> --name
            public string[] ValueFlags = new string[0]; // 带值旗标 -> --name value
            public string TextParam;                    // inline 文本入参名（写临时文件，路径作首个位置参）
            public string TextExt = ".txt";
            public string PathParam;                    // 显式路径入参名(兜底); 未给且 TextParam 以 Text 结尾则自动派生 <名>Path
            public Dictionary<string, string> P = new Dictionary<string, string>(); // 参数/旗标名->描述（覆盖 Schema 默认；零上下文 AI 选参填参的关键）
        }

        // ===================== 54 工具登记表 =====================
        private static readonly ToolDef[] Tools = new[]
        {
            // 读取
            new ToolDef{ Name="list", Desc="列出所有 PLC 及全部块(类型/名称/语言/路径/受保护标记)。只列 PLC 程序块;要设备清单用 device-list,要 HMI 用 hmi-list" },
            new ToolDef{ Name="read-tags", Desc="列出 PLC 变量表与变量(名/类型/地址/中文注释)。仅离线组态值:Openness 无在线实际值/强制值,也不读 DB 成员当前值" },
            new ToolDef{ Name="read-udts", Desc="列出所有 UDT 自定义类型名" },
            new ToolDef{ Name="export-source", Desc="导出 SCL/STL 块源码文本(图形块改用 export-xml)。源码直接随结果返回,无需另存。【导出前置:块须 IsConsistent=True(已编译)。块被改过未重新编译→IsConsistent=False→Export 抛通用错 'Error when calling method Export'(是未编译,非加密;受保护=False 时即此因);对策:先对该块 compile(0 错即转一致)再导出。故导出失败应先 compile 重试,勿当作块损坏。可先 block-info 看一致性】", Req=new[]{"blockName"}, Opt=new[]{"outDir"},
                P=new Dictionary<string,string>{ ["blockName"]="要导出的块名(大小写不敏感)", ["outDir"]="可选;源码已随结果返回,仅当需要额外磁盘明文副本时给目录(注意落盘明文可能被本机 E-SafeNet 加密)" } },
            new ToolDef{ Name="export-xml", Desc="导出任意块(含图形块/DB)为 SimaticML XML;体积可大,XML 已随结果返回。AI 整块改 LAD/FBD 必须先用它拿完整 XML。【导出前置:块须 IsConsistent=True(已编译)。改过未编译的块 IsConsistent=False→Export 抛通用错 'Error when calling method Export'(是未编译,非加密;受保护=False 时即此因);对策:先对该块 compile 再导出即可。故导出失败应先 compile 重试,勿当作块损坏。可先 block-info 看一致性】", Req=new[]{"blockName"}, Opt=new[]{"outDir"},
                P=new Dictionary<string,string>{ ["blockName"]="要导出的块名(大小写不敏感)", ["outDir"]="可选;XML 已随结果返回,仅需磁盘副本时给目录(落盘明文可能被加密)" } },
            new ToolDef{ Name="export-udt", Desc="导出一个 UDT 的完整成员定义文本(已随结果返回)", Req=new[]{"udtName"}, Opt=new[]{"outDir"},
                P=new Dictionary<string,string>{ ["udtName"]="要导出的 UDT 名", ["outDir"]="可选;定义已随结果返回,仅需磁盘副本时给目录" } },
            new ToolDef{ Name="block-info", Desc="读块元数据(块号/语言/布局/一致性/作者/版本/日期)", Req=new[]{"blockName"},
                P=new Dictionary<string,string>{ ["blockName"]="要查元数据的块名" } },
            new ToolDef{ Name="hmi-probe", Desc="只读探测 HMI(画面/变量/连接);输出极大,日常优先用 hmi-list,要落盘快照用 hmi-export-all", Flags=new[]{"no-screen-export"},
                P=new Dictionary<string,string>{ ["no-screen-export"]="true=跳过画面 XML 导出以大幅减小输出体积" } },
            // 硬件/库
            new ToolDef{ Name="device-list", Desc="项目所有设备的全局一览(一行一设备:类型 PLC/HMI/其他 + 型号/订货号,分布式IO站带GSD标记)。要单设备详情用 device-info" },
            new ToolDef{ Name="device-info", Desc="单设备详情:型号/订货号/固件/作者(CPU与各模块)", Opt=new[]{"deviceName"},
                P=new Dictionary<string,string>{ ["deviceName"]="可选;设备名,不传默认第一个设备(已在 diagnostics 标注)" } },
            new ToolDef{ Name="device-modules", Desc="机架/槽位/模块树(本地中央机架 + 分布式IO站模块)", Opt=new[]{"deviceName"},
                P=new Dictionary<string,string>{ ["deviceName"]="可选;设备名,不传默认第一个设备" } },
            new ToolDef{ Name="device-network", Desc="子网/IoSystem 拓扑 + 各网络接口 IP/PN名。均为离线组态值;Openness 不能在线分配/读取实际 IP", Opt=new[]{"deviceName"},
                P=new Dictionary<string,string>{ ["deviceName"]="可选;设备名,不传默认第一个设备" } },
            new ToolDef{ Name="library-list", Desc="项目库类型/母版副本 + 全局库枚举" },
            // 排查
            new ToolDef{ Name="where-used", Desc="谁引用了该符号(单跳、任意符号:块/DB/变量/UDT,支持 DB.成员)。要递归的块级被调树用 callers-tree", Req=new[]{"symbol"},
                P=new Dictionary<string,string>{ ["symbol"]="符号名;块/DB/变量/UDT 均可,DB 成员写 DB.成员" } },
            new ToolDef{ Name="block-deps", Desc="某块直接依赖的符号(单跳、向下、列出全部被引用符号)。要递归块树用 call-tree", Req=new[]{"blockName"},
                P=new Dictionary<string,string>{ ["blockName"]="要分析依赖的块名" } },
            new ToolDef{ Name="find-unused", Desc="无人引用的块/DB(死代码候选;删前务必复核)" },
            new ToolDef{ Name="call-tree", Desc="某块的向下调用树(递归、仅块)。只要单跳全部依赖符号用 block-deps", Req=new[]{"blockName"},
                P=new Dictionary<string,string>{ ["blockName"]="调用树根块名" } },
            new ToolDef{ Name="callers-tree", Desc="某块的被调用树(递归、仅块、向上影响分析)。只要单跳引用者(含变量/DB)用 where-used", Req=new[]{"blockName"},
                P=new Dictionary<string,string>{ ["blockName"]="被调树根块名" } },
            new ToolDef{ Name="crossref-report", Desc="全项目交叉引用 Markdown 报表(体积可大)" },
            // PLC 写/重构
            new ToolDef{ Name="import-scl", Desc="导入 SCL 明文->生成块->自动编译报错。会覆盖同名块且无 dry-run,建议先 export-source 备份。【大 SCL 用 sclPath 传磁盘路径,勿大段内联——内联体积过大会失败;典型来源 export-source 的产物】", TextParam="sclText", TextExt=".scl",
                P=new Dictionary<string,string>{ ["sclText"]="SCL 源码明文(小片段内联;大文件改用 sclPath 传磁盘路径);含同名块将被覆盖", ["sclPath"]="SCL 明文文件磁盘路径(.scl);大源码走此参免内联爆 token,与 sclText 二选一。常用 export-source 的产物" } },
            new ToolDef{ Name="import-xml", Desc="导入 SimaticML 整块覆盖同名块(图形块的'写')。Override 静默覆盖且无 dry-run,务必先 export-xml 备份。【大 XML 用 xmlPath 传磁盘路径,勿大段内联——内联体积过大会失败;典型来源 export-xml 的产物】", TextParam="xmlText", TextExt=".xml",
                P=new Dictionary<string,string>{ ["xmlText"]="SimaticML XML 明文(小片段内联;大文件改用 xmlPath 传磁盘路径);同名块将被 Override 整块覆盖", ["xmlPath"]="SimaticML XML 文件磁盘路径(.xml);大 XML 走此参免内联爆 token,与 xmlText 二选一。常用 export-xml 的产物" } },
            new ToolDef{ Name="import-udt", Desc="从明文 .udt 生成/覆盖 UDT。会覆盖同名 UDT 且无 dry-run", TextParam="udtText", TextExt=".udt",
                P=new Dictionary<string,string>{ ["udtText"]="UDT 定义明文(小片段内联;大文件改用 udtPath 传磁盘路径);同名 UDT 将被覆盖", ["udtPath"]="UDT 明文文件磁盘路径(.udt);与 udtText 二选一。常用 export-udt 的产物" } },
            new ToolDef{ Name="write-tags", Desc="批量建 PLC 变量。无 dry-run", TextParam="listText", TextExt=".txt",
                P=new Dictionary<string,string>{ ["listText"]="每行一条: 表名|变量名|类型|地址(地址留空=符号变量)。inline 文本(大清单改用 listPath)", ["listPath"]="清单 .txt 文件磁盘路径;与 listText 二选一" } },
            new ToolDef{ Name="delete-tags", Desc="删 PLC 变量", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["listText"]="每行一条: 表名|变量名 或 仅 变量名。inline 文本(大清单改用 listPath)", ["listPath"]="清单 .txt 文件磁盘路径;与 listText 二选一", ["dry-run"]="true=只预览将删哪些、不实际删(建议先跑)" } },
            new ToolDef{ Name="edit-tags", Desc="改 PLC 变量类型/地址", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["listText"]="每行一条: 表名|变量名|新类型|新地址(留空=该项不改)。inline 文本(大清单改用 listPath)", ["listPath"]="清单 .txt 文件磁盘路径;与 listText 二选一", ["dry-run"]="true=只预览改动、不实际写" } },
            new ToolDef{ Name="delete-block", Desc="删块(删前查引用,被引用需 force)", Req=new[]{"blockName"}, Flags=new[]{"dry-run","force"},
                P=new Dictionary<string,string>{ ["blockName"]="要删的块名", ["dry-run"]="true=只预览(含引用检查)、不实际删", ["force"]="即使该块仍被引用也强制删(危险,会留下悬空引用)" } },
            new ToolDef{ Name="rename-block", Desc="改块名(Openness 改名不更新引用,会告警影响面)", Req=new[]{"oldName","newName"}, Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["oldName"]="现块名", ["newName"]="新块名", ["dry-run"]="true=只预览影响面、不实际改名" } },
            new ToolDef{ Name="set-block-number", Desc="改块号(号冲突会拒)", Req=new[]{"blockName","number"}, Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["blockName"]="块名", ["number"]="目标块号(整数;与现有块冲突会被拒)", ["dry-run"]="true=只预览、不实际改号" } },
            // know-how 保护：解锁/加锁（官方 PlcBlockProtectionProvider.Unprotect/Protect）
            new ToolDef{ Name="unlock-block", Desc="移除块的 know-how(专有技术)保护(Openness Unprotect)。【博图里双击+输密码只是临时打开,Openness 仍读不到;必须本工具或博图取消保护】。解锁后 export-source/export-xml 才出完整代码。【DB 解不了:Unprotect 仅对代码块 FC/FB/OB 提供;背景/全局 DB 调 GetService<PlcBlockProtectionProvider> 返回 null→报'服务不可用',密码根本不被测试(与密码对错无关);受保护 DB 只能在博图手动取消保护。实例DB损失小:其结构由可读的母FB接口决定】。不传块名=对全部受保护块逐个试该密码,密码不符的跳过(支持多块不同密码:换密码再调一次)。内存改动不落盘,读完用 lock-block 复原",
                Opt=new[]{"blockNames"}, ValueFlags=new[]{"password"}, Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{
                    ["blockNames"]="要解锁的块名,逗号分隔多个;留空=对项目内全部受 know-how 保护的块逐个尝试",
                    ["password"]="know-how 保护密码(必填;dry-run 预览可不填)。绝不存储,用完即弃",
                    ["dry-run"]="true=只列将解锁哪些块及当前保护状态、不实际改、无需密码" } },
            new ToolDef{ Name="lock-block", Desc="给块设置 know-how(专有技术)保护(Openness Protect)。块名必填(只按显式块名,防误锁无保护块)。配合 unlock-block 做解锁→读→复原。内存改动不落盘,要持久化用 project-save",
                Req=new[]{"blockNames"}, ValueFlags=new[]{"password"}, Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{
                    ["blockNames"]="要加锁的块名,逗号分隔多个(必填)",
                    ["password"]="设置的保护密码(必填;dry-run 预览可不填)。绝不存储",
                    ["dry-run"]="true=只列将加锁哪些块及是否可加锁、不实际改、无需密码" } },
            // 编译/工程
            new ToolDef{ Name="compile", Desc="编译指定块,结构化返回 Error/Warning。仅离线编译;不下载到 PLC、不联机、不读诊断缓冲区", Req=new[]{"blockName"},
                P=new Dictionary<string,string>{ ["blockName"]="要编译的块名" } },
            new ToolDef{ Name="compile-device", Desc="编译整个 PLC,结构化返回 Error/Warning。仅离线编译;不下载到 PLC、不联机" },
            new ToolDef{ Name="export-all", Desc="所有块导出明文(SCL/XML)到目录,给 Git", Req=new[]{"outDir"},
                P=new Dictionary<string,string>{ ["outDir"]="输出目录(必填);落盘明文可能被 E-SafeNet 加密,尽快提交 Git/转存" } },
            new ToolDef{ Name="export-watchtable", Desc="导出监控表/强制表定义(非活值)到目录", Req=new[]{"outDir"},
                P=new Dictionary<string,string>{ ["outDir"]="输出目录(必填)" } },
            new ToolDef{ Name="project-info", Desc="项目元信息(名/作者/路径/版本/时间/注释/IsModified)" },
            new ToolDef{ Name="project-save", Desc="把 Openness 改动落盘(写命令闭环的关键)" },
            new ToolDef{ Name="project-archive", Desc="归档项目为 .zapXX(需先 project-save)", Req=new[]{"outDir"}, Opt=new[]{"archiveName"},
                P=new Dictionary<string,string>{ ["outDir"]="归档输出目录(必填)", ["archiveName"]="可选;归档名,不传按项目名+时间自动取" } },
            new ToolDef{ Name="export-project-texts", Desc="全工程注释/文本导出 xlsx 批改(默认语言 zh-CN)", Req=new[]{"outDir"}, Opt=new[]{"language"},
                P=new Dictionary<string,string>{ ["outDir"]="xlsx 输出目录(必填)", ["language"]="可选;语言代码如 zh-CN(默认 zh-CN)" } },
            new ToolDef{ Name="import-project-texts", Desc="把批改后的 xlsx 文本导回项目", Req=new[]{"xlsxPath"},
                P=new Dictionary<string,string>{ ["xlsxPath"]="export-project-texts 产出并人工批改后的 xlsx 磁盘路径(本工具是唯一接磁盘路径、非 inline 的写工具)" } },
            // HMI 读
            new ToolDef{ Name="hmi-list", Desc="HMI 设备+画面/变量/连接计数+名字地图" },
            new ToolDef{ Name="hmi-read-tags", Desc="HMI 变量(名/类型/连接/绑定的 PLC 变量)", ValueFlags=new[]{"table","filter"},
                P=new Dictionary<string,string>{ ["table"]="可选;只读该变量表(精确表名,非子串)", ["filter"]="可选;变量名子串筛选" } },
            new ToolDef{ Name="hmi-read-screens", Desc="画面文件夹树(仅画面名,无控件详情)" },
            new ToolDef{ Name="hmi-read-screen", Desc="⚠ 仅基础摘要:画面控件类型+变量绑定,不含位置/大小/颜色/字体等视觉信息。需要布局信息请用 hmi-read-screen-layout", Req=new[]{"screenName"},
                P=new Dictionary<string,string>{ ["screenName"]="画面名" } },
            new ToolDef{ Name="hmi-read-screen-layout", Desc="✅ 画面视觉布局:所有控件的位置(X,Y)/大小(W×H)/颜色(背景/前景/边框)/字体(名/大小/粗体/斜体)/文本/变量绑定/圆角/透明度/重叠检测。用于理解画面结构、规划UI修改", Req=new[]{"screenName"},
                P=new Dictionary<string,string>{ ["screenName"]="画面名" } },
            new ToolDef{ Name="hmi-read-templates", Desc="模板画面(母版)树(仅名称,无控件详情)。需要模板布局请用 hmi-export-template 导出完整XML" },
            new ToolDef{ Name="hmi-read-connections", Desc="HMI 连接名" },
            new ToolDef{ Name="hmi-export-all", Desc="HMI 全快照(画面+模板+变量表+连接+列表)到目录", Req=new[]{"outDir"},
                P=new Dictionary<string,string>{ ["outDir"]="输出目录(必填)" } },
            new ToolDef{ Name="hmi-find-unused-tags", Desc="HMI 死代码候选:声明了但未被任何画面/模板引用的变量(扫画面+模板,不扫报警/调度器/多路复用;未引用!=可安全删,删前博图复核)" },
            new ToolDef{ Name="hmi-tag-usage", Desc="单 HMI 变量反查:被哪些画面/模板/控件引用(对标 PLC where-used;扫画面+模板,不扫报警/调度器)", Req=new[]{"tagName"},
                P=new Dictionary<string,string>{ ["tagName"]="HMI 变量名(精确名,大小写不敏感;查准名用 hmi-read-tags)" } },
            // HMI 写
            new ToolDef{ Name="hmi-write-tags", Desc="建/改 HMI 变量", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["listText"]="每行: 表名|变量名|连接|PLC符号|[访问]|[注释]([]内可省)。inline 文本(大清单改用 listPath)", ["listPath"]="清单 .txt 文件磁盘路径;与 listText 二选一", ["dry-run"]="true=只预览、不实际写" } },
            new ToolDef{ Name="hmi-delete-tags", Desc="删 HMI 变量", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["listText"]="每行: [表名|]变量名。inline 文本(大清单改用 listPath)", ["listPath"]="清单 .txt 文件磁盘路径;与 listText 二选一", ["dry-run"]="true=只预览、不实际删" } },
            new ToolDef{ Name="hmi-export-screen", Desc="导出单画面 XML 供编辑", Req=new[]{"screenName","outDir"},
                P=new Dictionary<string,string>{ ["screenName"]="画面名", ["outDir"]="输出目录(必填)" } },
            new ToolDef{ Name="hmi-import-screen", Desc="整屏替换/新建。【大画面 XML 用 xmlPath 传磁盘路径,勿把整份 XML 内联——画面 XML 常达数十万字符,内联会失败;典型来源 hmi-export-screen 的产物】", TextParam="xmlText", TextExt=".xml", Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["xmlText"]="画面 SimaticML XML 明文(小画面可内联;大画面改用 xmlPath 传磁盘路径);同名画面整屏替换,否则新建", ["xmlPath"]="画面 XML 文件磁盘路径(.xml);画面 XML 常达数十万字符,大文件务必走此参,与 xmlText 二选一。常用 hmi-export-screen 的产物", ["dry-run"]="true=只预览、不实际写" } },
            new ToolDef{ Name="hmi-delete-screen", Desc="删画面", Req=new[]{"screenName"}, Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["screenName"]="画面名", ["dry-run"]="true=只预览、不实际删" } },
            new ToolDef{ Name="hmi-export-template", Desc="导出模板(母版) XML 供编辑", Req=new[]{"templateName","outDir"},
                P=new Dictionary<string,string>{ ["templateName"]="模板(母版)名", ["outDir"]="输出目录(必填)" } },
            new ToolDef{ Name="hmi-import-template", Desc="整模板替换/新建;改母版影响所有继承画面。【大模板 XML 用 xmlPath 传磁盘路径,勿大段内联;典型来源 hmi-export-template 的产物】", TextParam="xmlText", TextExt=".xml", Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["xmlText"]="模板 SimaticML XML 明文(小可内联;大改用 xmlPath 传磁盘路径);改母版会影响所有继承它的画面", ["xmlPath"]="模板 XML 文件磁盘路径(.xml);大文件务必走此参,与 xmlText 二选一。常用 hmi-export-template 的产物", ["dry-run"]="true=只预览、不实际写" } },
            new ToolDef{ Name="hmi-delete-template", Desc="删模板", Req=new[]{"templateName"}, Flags=new[]{"dry-run"},
                P=new Dictionary<string,string>{ ["templateName"]="模板名", ["dry-run"]="true=只预览、不实际删" } },
            new ToolDef{ Name="hmi-import-list", Desc="导入文本/图形列表(未指定 text/graphic 则按 XML 推断)。【列表 XML 较大时用 xmlPath 传磁盘路径】", TextParam="xmlText", TextExt=".xml", Flags=new[]{"dry-run","text","graphic"},
                P=new Dictionary<string,string>{ ["xmlText"]="文本/图形列表 SimaticML XML 明文(小可内联;大改用 xmlPath 传磁盘路径)", ["xmlPath"]="列表 XML 文件磁盘路径(.xml);与 xmlText 二选一", ["dry-run"]="true=只预览、不实际写", ["text"]="指定导入为文本列表", ["graphic"]="指定导入为图形列表(text/graphic 都不给则按 XML 自动推断)" } },
            new ToolDef{ Name="hmi-delete-list", Desc="删文本/图形列表", Req=new[]{"listName"}, Flags=new[]{"dry-run","text","graphic"},
                P=new Dictionary<string,string>{ ["listName"]="列表名", ["dry-run"]="true=只预览、不实际删", ["text"]="指定为文本列表", ["graphic"]="指定为图形列表(都不给则按类型推断)" } },
        };
    }
}
