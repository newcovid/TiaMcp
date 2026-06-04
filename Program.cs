using System;
using System.IO;
using System.Linq;

namespace TiaMcp
{
    /// <summary>
    /// 入口。顺序仍然很重要：先注册程序集解析器（本方法不碰 Siemens 类型），
    /// 再按命令行参数分发到 Commands（碰 Siemens 类型的代码都在那里，JIT 时解析器已就位）。
    ///
    /// 阶段2 用法（命令行子命令；阶段5 会改包成 MCP stdio 工具）：
    ///   TiaMcp.exe list                                列出所有 PLC 与块
    ///   TiaMcp.exe export-source &lt;块名&gt; [输出目录]      把 SCL/STL 块导出为 .scl/.awl 文本
    ///   TiaMcp.exe import-scl &lt;scl文件路径&gt;             导入 SCL -> 生成块 -> 编译报错
    ///   TiaMcp.exe compile &lt;块名&gt;                       编译指定块，结构化返回错误
    /// </summary>
    internal static class Program
    {
        [STAThread] // Openness 内部有 COM 交互，主线程用 STA、全程串行
        private static int Main(string[] args)
        {
            // 控制台 UTF-8（无 BOM），中文不乱码；也契合阶段5 stdio 要求。
            try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }

            // 第一步：最先注册程序集解析器（本方法不碰任何 Siemens 类型）
            AppDomain.CurrentDomain.AssemblyResolve += OpennessAssemblyResolver.Resolve;

            Logger.Init();

            // 阶段5：mcp 子命令启动 stdio JSON-RPC 服务器（本方法不碰 Siemens 类型；McpServer 内部调 Dispatch）
            if (args.Length > 0 && string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
                return McpServer.Run();

            return Dispatch(args);
        }

        /// <summary>按 argv 分发到具体命令。碰 Siemens 类型的代码都在这里及以下（JIT 时解析器已就位）。</summary>
        internal static int Dispatch(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            Logger.Info($"启动，命令={cmd}");

            try
            {
                switch (cmd)
                {
                    case "list":
                        return Commands.List();

                    case "export-source":
                        if (args.Length < 2) { Console.WriteLine("用法: export-source <块名> [可选:另存明文副本的目录]"); return 1; }
                        // 默认只打印源码、不落盘（不信任落盘明文）；给了目录才另存一份副本。
                        return Commands.ExportSource(args[1], args.Length >= 3 ? args[2] : null);

                    case "import-scl":
                        if (args.Length < 2) { Console.WriteLine("用法: import-scl <scl文件路径>"); return 1; }
                        return Commands.ImportScl(args[1]);

                    case "compile":
                        if (args.Length < 2) { Console.WriteLine("用法: compile <块名>"); return 1; }
                        return Commands.Compile(args[1]);

                    case "export-xml":
                        if (args.Length < 2) { Console.WriteLine("用法: export-xml <块名> [可选:另存目录]"); return 1; }
                        return Commands.ExportXml(args[1], args.Length >= 3 ? args[2] : null);

                    case "where-used":
                        if (args.Length < 2) { Console.WriteLine("用法: where-used <符号名(块/DB/变量/UDT)>"); return 1; }
                        return CrossRef.WhereUsed(args[1]);

                    case "block-deps":
                        if (args.Length < 2) { Console.WriteLine("用法: block-deps <块名>"); return 1; }
                        return CrossRef.BlockDeps(args[1]);

                    case "find-unused":
                        return CrossRef.FindUnused();

                    case "call-tree":
                        if (args.Length < 2) { Console.WriteLine("用法: call-tree <块名>"); return 1; }
                        return CrossRef.CallTree(args[1]);

                    case "read-tags":
                        return Reads.ReadTags();

                    case "read-udts":
                        return Reads.ReadUdts();

                    case "import-xml":
                        if (args.Length < 2) { Console.WriteLine("用法: import-xml <SimaticML文件>"); return 1; }
                        return Commands.ImportXml(args[1]);

                    case "delete-block":
                        if (args.Length < 2) { Console.WriteLine("用法: delete-block <块名> [--dry-run] [--force]"); return 1; }
                        return Commands.DeleteBlock(args[1], args.Contains("--dry-run"), args.Contains("--force"));

                    case "rename-block":
                        if (args.Length < 3) { Console.WriteLine("用法: rename-block <旧块名> <新块名> [--dry-run]"); return 1; }
                        return Commands.RenameBlock(args[1], args[2], args.Contains("--dry-run"));

                    case "set-block-number":
                        if (args.Length < 3 || !int.TryParse(args[2], out int blkNum))
                        { Console.WriteLine("用法: set-block-number <块名> <编号> [--dry-run]"); return 1; }
                        return Commands.SetBlockNumber(args[1], blkNum, args.Contains("--dry-run"));

                    case "unlock-block":
                    {
                        // 块名为可选首位置参（省略=全部受保护块）；--password 值参；--dry-run
                        string names = (args.Length >= 2 && !args[1].StartsWith("--")) ? args[1] : null;
                        return Commands.UnlockBlock(names, GetOpt(args, "--password"), args.Contains("--dry-run"));
                    }

                    case "lock-block":
                    {
                        if (args.Length < 2 || args[1].StartsWith("--"))
                        { Console.WriteLine("用法: lock-block <块名,逗号分隔> --password <pwd> [--dry-run]"); return 1; }
                        return Commands.LockBlock(args[1], GetOpt(args, "--password"), args.Contains("--dry-run"));
                    }

                    case "callers-tree":
                        if (args.Length < 2) { Console.WriteLine("用法: callers-tree <块名>"); return 1; }
                        return CrossRef.CallersTree(args[1]);

                    case "crossref-report":
                        return CrossRef.CrossRefReport();

                    case "compile-device":
                        return Commands.CompileDevice();

                    case "project-save":
                        return Commands.ProjectSave();

                    case "project-info":
                        return Reads.ProjectInfo();

                    case "library-list":
                        return Library.LibraryList();

                    case "export-project-texts":
                        if (args.Length < 2) { Console.WriteLine("用法: export-project-texts <输出目录> [语言代码,默认zh-CN]"); return 1; }
                        return Commands.ExportProjectTexts(args[1], args.Length >= 3 ? args[2] : null);

                    case "import-project-texts":
                        if (args.Length < 2) { Console.WriteLine("用法: import-project-texts <xlsx文件>"); return 1; }
                        return Commands.ImportProjectTexts(args[1]);

                    case "block-info":
                        if (args.Length < 2) { Console.WriteLine("用法: block-info <块名>"); return 1; }
                        return Reads.BlockInfo(args[1]);

                    case "device-list":
                        return Hardware.DeviceList();

                    case "device-info":
                        return Hardware.DeviceInfo(args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null);

                    case "device-modules":
                        return Hardware.DeviceModules(args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null);

                    case "device-network":
                        return Hardware.DeviceNetwork(args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null);

                    case "export-all":
                        if (args.Length < 2) { Console.WriteLine("用法: export-all <输出目录>"); return 1; }
                        return Commands.ExportAll(args[1]);

                    case "export-watchtable":
                        if (args.Length < 2) { Console.WriteLine("用法: export-watchtable <输出目录>"); return 1; }
                        return Commands.ExportWatchtables(args[1]);

                    case "project-archive":
                        if (args.Length < 2) { Console.WriteLine("用法: project-archive <输出目录> [归档名]"); return 1; }
                        return Commands.ProjectArchive(args[1], args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : null);

                    case "export-udt":
                        if (args.Length < 2) { Console.WriteLine("用法: export-udt <UDT名> [目录]"); return 1; }
                        return Commands.ExportUdt(args[1], args.Length >= 3 ? args[2] : null);

                    case "import-udt":
                        if (args.Length < 2) { Console.WriteLine("用法: import-udt <.udt文件>"); return 1; }
                        return Commands.ImportUdt(args[1]);

                    case "write-tags":
                        if (args.Length < 2) { Console.WriteLine("用法: write-tags <清单文件 表名|变量名|类型|地址>"); return 1; }
                        return Commands.WriteTags(args[1]);

                    case "delete-tags":
                        if (args.Length < 2) { Console.WriteLine("用法: delete-tags <清单文件 表名|变量名 或 变量名> [--dry-run]"); return 1; }
                        return Commands.DeleteTags(args[1], args.Contains("--dry-run"));

                    case "edit-tags":
                        if (args.Length < 2) { Console.WriteLine("用法: edit-tags <清单文件 表名|变量名|新类型|新地址> [--dry-run]"); return 1; }
                        return Commands.EditTags(args[1], args.Contains("--dry-run"));

                    case "hmi-probe":
                        // 只读探测 HMI 设备；可选 --no-screen-export 跳过画面导出试探
                        return HmiProbe.Probe(tryScreenExport: !args.Contains("--no-screen-export"));

                    case "hmi-list":
                        return HmiReads.HmiList();

                    case "hmi-read-screens":
                        return HmiReads.HmiReadScreens();

                    case "hmi-read-templates":
                        return HmiReads.HmiReadTemplates();

                    case "hmi-export-template":
                        if (args.Length < 3) { Console.WriteLine("用法: hmi-export-template <模板名> <输出目录>"); return 1; }
                        return HmiWrites.ExportTemplate(args[1], args[2]);

                    case "hmi-import-template":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-import-template <模板XML文件> [--dry-run]"); return 1; }
                        return HmiWrites.ImportTemplate(args[1], args.Contains("--dry-run"));

                    case "hmi-delete-template":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-delete-template <模板名> [--dry-run]"); return 1; }
                        return HmiWrites.DeleteTemplate(args[1], args.Contains("--dry-run"));

                    case "hmi-import-list":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-import-list <列表XML> [--text|--graphic] [--dry-run]"); return 1; }
                        return HmiWrites.ImportList(args[1], args.Contains("--graphic") ? "graphic" : (args.Contains("--text") ? "text" : null), args.Contains("--dry-run"));

                    case "hmi-delete-list":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-delete-list <列表名> [--text|--graphic] [--dry-run]"); return 1; }
                        return HmiWrites.DeleteList(args[1], args.Contains("--graphic") ? "graphic" : (args.Contains("--text") ? "text" : null), args.Contains("--dry-run"));

                    case "hmi-read-connections":
                        return HmiReads.HmiReadConnections();

                    case "hmi-read-tags":
                        return HmiReads.HmiReadTags(GetOpt(args, "--table"), GetOpt(args, "--filter"));

                    case "hmi-read-screen":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-read-screen <画面名>"); return 1; }
                        return HmiReads.HmiReadScreen(args[1]);

                    case "hmi-export-all":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-export-all <输出目录>"); return 1; }
                        return HmiReads.HmiExportAll(args[1]);

                    case "hmi-write-tags":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-write-tags <清单文件> [--dry-run]"); return 1; }
                        return HmiWrites.WriteTags(args[1], args.Contains("--dry-run"));

                    case "hmi-delete-tags":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-delete-tags <清单文件> [--dry-run]"); return 1; }
                        return HmiWrites.DeleteTags(args[1], args.Contains("--dry-run"));

                    case "hmi-export-screen":
                        if (args.Length < 3) { Console.WriteLine("用法: hmi-export-screen <画面名> <输出目录>"); return 1; }
                        return HmiWrites.ExportScreen(args[1], args[2]);

                    case "hmi-import-screen":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-import-screen <画面XML文件> [--dry-run]"); return 1; }
                        return HmiWrites.ImportScreen(args[1], args.Contains("--dry-run"));

                    case "hmi-delete-screen":
                        if (args.Length < 2) { Console.WriteLine("用法: hmi-delete-screen <画面名> [--dry-run]"); return 1; }
                        return HmiWrites.DeleteScreen(args[1], args.Contains("--dry-run"));

                    default:
                        Console.WriteLine($"未知命令: {cmd}");
                        Console.WriteLine("可用命令：");
                        Console.WriteLine("  读取: list | read-tags | read-udts | export-source <块> [目录] | export-xml <块> [目录] | block-info <块> | hmi-probe");
                        Console.WriteLine("  硬件: device-list | device-info [设备名] | device-modules [设备名] | device-network [设备名] | library-list");
                        Console.WriteLine("  HMI: hmi-list | hmi-read-tags [--table 名][--filter 子串] | hmi-read-screens | hmi-read-screen <名> | hmi-read-templates | hmi-read-connections | hmi-export-all <目录>");
                        Console.WriteLine("  HMI写: hmi-write-tags <清单>[--dry-run] | hmi-delete-tags <清单>[--dry-run] | hmi-export-screen <名> <目录> | hmi-import-screen <xml>[--dry-run] | hmi-delete-screen <名>[--dry-run]");
                        Console.WriteLine("  HMI模板/列表: hmi-export-template <名> <目录> | hmi-import-template <xml>[--dry-run] | hmi-delete-template <名>[--dry-run] | hmi-import-list <xml>[--text|--graphic][--dry-run] | hmi-delete-list <名>[--text|--graphic][--dry-run]");
                        Console.WriteLine("  排查: where-used <符号> | block-deps <块> | find-unused | call-tree <块> | callers-tree <块> | crossref-report");
                        Console.WriteLine("  写入/重构: import-scl <文件> | import-xml <文件> | write-tags <清单> | delete-tags <清单>[--dry-run] | edit-tags <清单>[--dry-run] | delete-block <块>[--dry-run][--force] | rename-block <旧> <新>[--dry-run] | set-block-number <块> <号>[--dry-run] | compile <块>");
                        Console.WriteLine("  保护: unlock-block [块,逗号分隔] --password <pwd>[--dry-run] | lock-block <块,逗号分隔> --password <pwd>[--dry-run]");
                        Console.WriteLine("  工程: project-info | project-save | project-archive <目录>[名] | compile-device | export-all <目录> | export-watchtable <目录> | export-project-texts <目录>[语言] | import-project-texts <xlsx>");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                // 捕获所有异常，给出可读信息，不让进程裸崩
                Logger.Error("运行失败", ex);
                Console.WriteLine();
                Console.WriteLine("[错误] " + ex.Message);
                Console.WriteLine("详细堆栈见日志: " + Logger.LogFilePath);
                return 1;
            }
        }

        // 取 "--name value" 形式的可选参数值；没有返回 null
        private static string GetOpt(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }
    }
}
