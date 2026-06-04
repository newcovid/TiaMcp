using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;             // ExportOptions
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features; // SoftwareContainer
using Siemens.Engineering.Hmi.Screen;  // Screen, ScreenComposition, ScreenSystemFolder, ScreenUserFolder
using Siemens.Engineering.Hmi.Tag;     // TagTable, TagComposition, Tag, TagSystemFolder, TagUserFolder
using Siemens.Engineering.Hmi.Communication; // Connection, ConnectionComposition
using HmiTarget = Siemens.Engineering.Hmi.HmiTarget;

namespace TiaMcp
{
    /// <summary>
    /// hmi-probe：只读探测 HMI 设备暴露的画面/画面项/变量/连接，输出能力小结。
    /// 沿用项目铁律：只读、逐项 try/catch、画面导出经 %TEMP%、stdout 出结果。
    /// 【HMI 签名官方手册确证，编译再校验，报错即按错误改。】
    /// </summary>
    internal static class HmiProbe
    {
        public static int Probe(bool tryScreenExport = true)
        {
            using (var s = TiaSession.AttachFirst())
            {
                var hmis = s.FindHmis();
                if (hmis.Count == 0)
                {
                    Console.WriteLine("未找到 HMI 设备。本工程可能只有 PLC。");
                    return 0;
                }

                var summary = new List<string>();
                foreach (var kv in hmis)
                {
                    Console.WriteLine($"==== HMI 设备: {kv.Key} ====");
                    ProbeDevice(kv.Value, kv.Key, tryScreenExport, summary);
                    Console.WriteLine();
                }

                Console.WriteLine("==== 能力小结 ====");
                foreach (var line in summary) Console.WriteLine(line);
                return 0;
            }
        }

        private static void ProbeDevice(HmiTarget hmi, string deviceName, bool tryScreenExport, List<string> summary)
        {
            // 探测项 1：设备发现（能走到这里说明已拿到 HmiTarget）
            Console.WriteLine($"  运行时类型: {Safe(() => hmi.GetType().Name)}");
            summary.Add($"OK   设备发现：{deviceName} 拿到 HmiTarget");

            ProbeScreens(hmi, tryScreenExport, summary);
            ProbeTags(hmi, summary);
            ProbeConnections(hmi, summary);
        }

        // 探测项 2：画面枚举 + 挑首张试 Export 到 %TEMP%
        private static void ProbeScreens(HmiTarget hmi, bool tryExport, List<string> summary)
        {
            try
            {
                var screens = new List<Screen>();
                CollectScreens(hmi.ScreenFolder, screens);
                Console.WriteLine($"  [画面] 共 {screens.Count} 张：" +
                    string.Join(", ", screens.Take(20).Select(x => x.Name)));
                summary.Add($"OK   画面枚举：{screens.Count} 张");

                if (tryExport && screens.Count > 0)
                {
                    Screen first = screens[0];
                    string tmp = IoUtil.NewTempFile(".xml");
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp); // Export 要求目标不存在
                        first.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        string xml = IoUtil.ReadPlaintext(tmp); // 校验非密文 + 去 BOM
                        Console.WriteLine($"  [画面导出] {first.Name}：成功，明文 {xml.Length} 字符。");
                        summary.Add($"OK   画面导出({first.Name})：明文 {xml.Length} 字符");
                        ProbeScreenItems(xml, summary);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [画面] 失败：" + ex.Message);
                summary.Add("WARN 画面：" + ex.Message);
                Logger.Error("ProbeScreens", ex);
            }
        }

        // 递归收集系统画面文件夹下所有画面（含用户子文件夹）
        private static void CollectScreens(ScreenSystemFolder root, List<Screen> acc)
        {
            foreach (Screen sc in root.Screens) acc.Add(sc);
            foreach (ScreenUserFolder f in root.Folders) CollectScreensInUserFolder(f, acc);
        }

        private static void CollectScreensInUserFolder(ScreenUserFolder folder, List<Screen> acc)
        {
            foreach (Screen sc in folder.Screens) acc.Add(sc);
            foreach (ScreenUserFolder f in folder.Folders) CollectScreensInUserFolder(f, acc);
        }

        // 探测项 2b：从导出的画面 XML 解析画面项。
        // 实测 V18 HMI 画面 XML 方言：根下 Engineering/DocumentInfo/Hmi.Screen.Screen，
        // 画面对象是 local-name 以 "Hmi.Screen." 开头的元素（根 Screen 本身除外）。
        // 不假死某个元素名，靠"元素类型频次表"把真实 schema 暴露出来。
        private static void ProbeScreenItems(string xml, List<string> summary)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var all = doc.Descendants().ToList();
                var freq = all.GroupBy(e => e.Name.LocalName)
                              .Select(g => new { Name = g.Key, Count = g.Count() })
                              .OrderByDescending(x => x.Count).ToList();

                // 画面对象启发式：Hmi.Screen.* 但不是根 Screen
                var objs = all.Where(e => e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
                                          && e.Name.LocalName != "Hmi.Screen.Screen").ToList();

                Console.WriteLine($"  [画面项] XML 元素总数 {all.Count}；疑似画面对象(Hmi.Screen.*) {objs.Count} 个。");
                Console.WriteLine("  [画面项] 元素类型 Top15：" +
                    string.Join(", ", freq.Take(15).Select(x => $"{x.Name}×{x.Count}")));
                summary.Add($"OK   画面项：疑似对象 {objs.Count} 个 / 元素类型 {freq.Count} 种（详见输出）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [画面项] 解析失败：" + ex.Message);
                summary.Add("WARN 画面项解析：" + ex.Message);
                Logger.Error("ProbeScreenItems", ex);
            }
        }

        // 探测项 3：HMI 变量表与变量
        private static void ProbeTags(HmiTarget hmi, List<string> summary)
        {
            try
            {
                int tables = 0, tags = 0;
                var sample = new List<string>();
                TagSystemFolder root = hmi.TagFolder;
                foreach (TagTable tt in root.TagTables) WalkHmiTagTable(tt, ref tables, ref tags, sample);
                foreach (TagUserFolder f in root.Folders) WalkHmiTagFolder(f, ref tables, ref tags, sample);
                Console.WriteLine($"  [变量] {tables} 张表 / {tags} 个变量。抽样：" + string.Join("; ", sample.Take(10)));
                summary.Add($"OK   变量：{tables} 表 / {tags} 变量");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [变量] 失败：" + ex.Message);
                summary.Add("WARN 变量：" + ex.Message);
                Logger.Error("ProbeTags", ex);
            }
        }

        private static void WalkHmiTagFolder(TagUserFolder folder, ref int tables, ref int tags, List<string> sample)
        {
            foreach (TagTable tt in folder.TagTables) WalkHmiTagTable(tt, ref tables, ref tags, sample);
            foreach (TagUserFolder f in folder.Folders) WalkHmiTagFolder(f, ref tables, ref tags, sample);
        }

        private static void WalkHmiTagTable(TagTable table, ref int tables, ref int tags, List<string> sample)
        {
            tables++;
            foreach (Tag tag in table.Tags)
            {
                tags++;
                if (sample.Count < 10)
                    // HMI Tag 无 DataTypeName 属性；用通用 GetAttribute 取数据类型（名字不对则 Safe 兜底为 ?）
                    sample.Add($"{Safe(() => tag.Name)}:{Safe(() => tag.GetAttribute("DataType"))}");
            }
        }

        // 探测项 4：HMI 通讯连接
        private static void ProbeConnections(HmiTarget hmi, List<string> summary)
        {
            try
            {
                int n = 0;
                foreach (Connection c in hmi.Connections)
                {
                    Console.WriteLine($"  [连接] {Safe(() => c.Name)}");
                    n++;
                }
                Console.WriteLine($"  [连接] 共 {n} 个。");
                summary.Add($"OK   连接：{n} 个");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [连接] 失败：" + ex.Message);
                summary.Add("WARN 连接：" + ex.Message);
                Logger.Error("ProbeConnections", ex);
            }
        }

        private static string Safe(Func<object> f)
        {
            try { return f()?.ToString() ?? ""; } catch { return "?"; }
        }
    }
}
