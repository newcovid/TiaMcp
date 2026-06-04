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
            try
            {
                var argv = BuildArgs(t, a, tempFiles);
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
            if (text.Length == 0) text = "(无输出)";
            if (!isError) text += $"\n(退出码 {rc})";
            return Tuple.Create(text, isError);
        }

        private static string[] BuildArgs(ToolDef t, Dictionary<string, object> a, List<string> tempFiles)
        {
            var argv = new List<string> { t.Name };
            // inline 文本入参 -> 写 %TEMP% 明文，路径作首个位置参
            if (t.TextParam != null)
            {
                string text = Str(a, t.TextParam) ?? "";
                string tmp = IoUtil.NewTempFile(t.TextExt);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                tempFiles.Add(tmp);
                argv.Add(tmp);
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
            if (t.TextParam != null) { props[t.TextParam] = StrProp("inline 文本内容（直接传文本，工具内部经 %TEMP% 中转，勿传磁盘路径）"); required.Add(t.TextParam); }
            foreach (var r in t.Req) { props[r] = StrProp(r); required.Add(r); }
            foreach (var op in t.Opt) props[op] = StrProp(op + "（可选）");
            foreach (var vf in t.ValueFlags) props[vf] = StrProp(vf + "（可选筛选子串）");
            foreach (var f in t.Flags) props[f] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = f };
            var s = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) s["required"] = required.ToArray();
            return s;
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
        }

        // ===================== 54 工具登记表 =====================
        private static readonly ToolDef[] Tools = new[]
        {
            // 读取
            new ToolDef{ Name="list", Desc="列出所有 PLC 及全部块(类型/名称/语言/路径/受保护标记)" },
            new ToolDef{ Name="read-tags", Desc="列出 PLC 变量表与变量(名/类型/地址/中文注释)" },
            new ToolDef{ Name="read-udts", Desc="列出所有 UDT 自定义类型名" },
            new ToolDef{ Name="export-source", Desc="导出 SCL/STL 块源码文本(图形块改用 export-xml)", Req=new[]{"blockName"}, Opt=new[]{"outDir"} },
            new ToolDef{ Name="export-xml", Desc="导出任意块(含图形块/DB)为 SimaticML XML;体积可大", Req=new[]{"blockName"}, Opt=new[]{"outDir"} },
            new ToolDef{ Name="export-udt", Desc="导出一个 UDT 的完整成员定义文本", Req=new[]{"udtName"}, Opt=new[]{"outDir"} },
            new ToolDef{ Name="block-info", Desc="读块元数据(块号/语言/布局/一致性/作者/版本/日期)", Req=new[]{"blockName"} },
            new ToolDef{ Name="hmi-probe", Desc="只读探测 HMI(画面/变量/连接);输出大,日常优先用 hmi-list", Flags=new[]{"no-screen-export"} },
            // 硬件/库
            new ToolDef{ Name="device-list", Desc="项目所有设备+类型(PLC/HMI/其他)+型号/订货号(分布式IO站带GSD标记)" },
            new ToolDef{ Name="device-info", Desc="设备型号/订货号/固件/作者(CPU与各模块)", Opt=new[]{"deviceName"} },
            new ToolDef{ Name="device-modules", Desc="机架/槽位/模块树(本地中央机架 + 分布式IO站模块)", Opt=new[]{"deviceName"} },
            new ToolDef{ Name="device-network", Desc="子网/IoSystem 拓扑 + 各网络接口 IP/PN名", Opt=new[]{"deviceName"} },
            new ToolDef{ Name="library-list", Desc="项目库类型/母版副本 + 全局库枚举" },
            // 排查
            new ToolDef{ Name="where-used", Desc="谁引用了该符号(块/DB/变量/UDT,支持 DB.成员)", Req=new[]{"symbol"} },
            new ToolDef{ Name="block-deps", Desc="某块依赖了哪些符号(向下引用)", Req=new[]{"blockName"} },
            new ToolDef{ Name="find-unused", Desc="无人引用的块/DB(死代码候选;删前务必复核)" },
            new ToolDef{ Name="call-tree", Desc="某块向下调用树", Req=new[]{"blockName"} },
            new ToolDef{ Name="callers-tree", Desc="某块被调用树(向上影响分析)", Req=new[]{"blockName"} },
            new ToolDef{ Name="crossref-report", Desc="全项目交叉引用 Markdown 报表(体积可大)" },
            // PLC 写/重构
            new ToolDef{ Name="import-scl", Desc="导入 SCL 明文->生成块->自动编译报错", TextParam="sclText", TextExt=".scl" },
            new ToolDef{ Name="import-xml", Desc="导入 SimaticML 整块覆盖同名块(图形块的'写')", TextParam="xmlText", TextExt=".xml" },
            new ToolDef{ Name="import-udt", Desc="从明文 .udt 生成/覆盖 UDT", TextParam="udtText", TextExt=".udt" },
            new ToolDef{ Name="write-tags", Desc="批量建 PLC 变量;每行 表名|变量名|类型|地址(地址可空=符号变量)", TextParam="listText", TextExt=".txt" },
            new ToolDef{ Name="delete-tags", Desc="删 PLC 变量;每行 表名|变量名 或 变量名", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"} },
            new ToolDef{ Name="edit-tags", Desc="改 PLC 变量类型/地址;每行 表名|变量名|新类型|新地址(空=不改)", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"} },
            new ToolDef{ Name="delete-block", Desc="删块(删前查引用,被引用需 force)", Req=new[]{"blockName"}, Flags=new[]{"dry-run","force"} },
            new ToolDef{ Name="rename-block", Desc="改块名(Openness 改名不更新引用,会告警影响面)", Req=new[]{"oldName","newName"}, Flags=new[]{"dry-run"} },
            new ToolDef{ Name="set-block-number", Desc="改块号(号冲突会拒)", Req=new[]{"blockName","number"}, Flags=new[]{"dry-run"} },
            // 编译/工程
            new ToolDef{ Name="compile", Desc="编译指定块,结构化返回 Error/Warning", Req=new[]{"blockName"} },
            new ToolDef{ Name="compile-device", Desc="编译整个 PLC" },
            new ToolDef{ Name="export-all", Desc="所有块导出明文(SCL/XML)到目录,给 Git", Req=new[]{"outDir"} },
            new ToolDef{ Name="export-watchtable", Desc="导出监控表/强制表定义(非活值)到目录", Req=new[]{"outDir"} },
            new ToolDef{ Name="project-info", Desc="项目元信息(名/作者/路径/版本/时间/注释/IsModified)" },
            new ToolDef{ Name="project-save", Desc="把 Openness 改动落盘(写命令闭环的关键)" },
            new ToolDef{ Name="project-archive", Desc="归档项目为 .zapXX(需先 project-save)", Req=new[]{"outDir"}, Opt=new[]{"archiveName"} },
            new ToolDef{ Name="export-project-texts", Desc="全工程注释/文本导出 xlsx 批改(默认语言 zh-CN)", Req=new[]{"outDir"}, Opt=new[]{"language"} },
            new ToolDef{ Name="import-project-texts", Desc="把批改后的 xlsx 文本导回(传磁盘路径)", Req=new[]{"xlsxPath"} },
            // HMI 读
            new ToolDef{ Name="hmi-list", Desc="HMI 设备+画面/变量/连接计数+名字地图" },
            new ToolDef{ Name="hmi-read-tags", Desc="HMI 变量(名/类型/连接/绑定的 PLC 变量)", ValueFlags=new[]{"table","filter"} },
            new ToolDef{ Name="hmi-read-screens", Desc="画面文件夹树" },
            new ToolDef{ Name="hmi-read-screen", Desc="单画面控件 + 绑定变量", Req=new[]{"screenName"} },
            new ToolDef{ Name="hmi-read-templates", Desc="模板画面(母版)树" },
            new ToolDef{ Name="hmi-read-connections", Desc="HMI 连接名" },
            new ToolDef{ Name="hmi-export-all", Desc="HMI 全快照(画面+模板+变量表+连接+列表)到目录", Req=new[]{"outDir"} },
            // HMI 写
            new ToolDef{ Name="hmi-write-tags", Desc="建/改 HMI 变量;行 表名|变量名|连接|PLC符号|[访问]|[注释]", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"} },
            new ToolDef{ Name="hmi-delete-tags", Desc="删 HMI 变量;行 [表名|]变量名", TextParam="listText", TextExt=".txt", Flags=new[]{"dry-run"} },
            new ToolDef{ Name="hmi-export-screen", Desc="导出单画面 XML 供编辑", Req=new[]{"screenName","outDir"} },
            new ToolDef{ Name="hmi-import-screen", Desc="整屏替换/新建(传 inline XML)", TextParam="xmlText", TextExt=".xml", Flags=new[]{"dry-run"} },
            new ToolDef{ Name="hmi-delete-screen", Desc="删画面", Req=new[]{"screenName"}, Flags=new[]{"dry-run"} },
            new ToolDef{ Name="hmi-export-template", Desc="导出模板(母版) XML 供编辑", Req=new[]{"templateName","outDir"} },
            new ToolDef{ Name="hmi-import-template", Desc="整模板替换/新建(传 inline XML);改母版影响所有继承画面", TextParam="xmlText", TextExt=".xml", Flags=new[]{"dry-run"} },
            new ToolDef{ Name="hmi-delete-template", Desc="删模板", Req=new[]{"templateName"}, Flags=new[]{"dry-run"} },
            new ToolDef{ Name="hmi-import-list", Desc="导入文本/图形列表(传 inline XML;未指定 text/graphic 则按 XML 推断)", TextParam="xmlText", TextExt=".xml", Flags=new[]{"dry-run","text","graphic"} },
            new ToolDef{ Name="hmi-delete-list", Desc="删文本/图形列表", Req=new[]{"listName"}, Flags=new[]{"dry-run","text","graphic"} },
        };
    }
}
