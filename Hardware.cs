using System;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using HmiTarget = Siemens.Engineering.Hmi.HmiTarget;

namespace TiaMcp
{
    /// <summary>
    /// 硬件/设备组态只读命令：device-list / device-info / device-modules。
    /// 全只读、纯属性读、无文件 I/O（不涉 E-SafeNet/%TEMP%）。动态属性一律 GetAttribute+try/catch 兜底。
    /// 关键：本地中央机架模块与分布式 IO 站点都是 Device/DeviceItem，统一遍历即可拿到二者基础信息。
    /// </summary>
    internal static class Hardware
    {
        // ===== device-list：项目内所有设备 + 类型(PLC/HMI/其他) + 型号/订货号（含分布式 IO 站） =====
        public static int DeviceList()
        {
            using (var s = TiaSession.AttachFirst())
            {
                int n = 0;
                Console.WriteLine($"==== 项目设备清单: {s.Project.Name} ====");
                foreach (Device dev in s.Project.Devices)
                {
                    string kind = ClassifyDevice(dev);
                    bool gsd = Attr(dev, "IsGsd") == "True";
                    Console.WriteLine($"  [{kind,-4}] {dev.Name,-28} 型号={Attr(dev, "TypeName")} 订货号={Attr(dev, "OrderNumber")}{(gsd ? " (GSD/分布式IO)" : "")}");
                    n++;
                }
                Console.WriteLine($"-- 共 {n} 个设备 --");
                Console.WriteLine("提示：模块/机架明细用 device-modules，单设备详情用 device-info。");
                return 0;
            }
        }

        // ===== device-info [设备名]：设备型号/订货号/作者/注释 + CPU/head 模块订货号/固件 =====
        public static int DeviceInfo(string filter)
        {
            using (var s = TiaSession.AttachFirst())
            {
                int n = 0;
                foreach (Device dev in s.Project.Devices)
                {
                    if (filter != null && dev.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Console.WriteLine($"==== 设备: {dev.Name} ({ClassifyDevice(dev)}) ====");
                    Console.WriteLine($"  顶层  型号={Attr(dev, "TypeName")}  订货号={Attr(dev, "OrderNumber")}  作者={Attr(dev, "Author")}  注释={Attr(dev, "Comment")}");
                    foreach (DeviceItem it in TiaSession.EnumerateDeviceItems(dev.DeviceItems))
                    {
                        string cls = Attr(it, "Classification");
                        string order = Attr(it, "OrderNumber");
                        if (cls == "CPU" || cls == "HM" || (order.Length > 0 && order != "?"))
                            Console.WriteLine($"    模块 {it.Name,-26} 分类={cls}  订货号={order}  固件={Attr(it, "FirmwareVersion")}");
                    }
                    n++;
                }
                if (n == 0) { Console.WriteLine(filter != null ? $"未找到设备: {filter}" : "项目无设备。"); return 1; }
                return 0;
            }
        }

        // ===== device-modules [设备名]：机架/槽位/模块树（本地中央机架 + 分布式 IO 站点的模块） =====
        public static int DeviceModules(string filter)
        {
            using (var s = TiaSession.AttachFirst())
            {
                foreach (Device dev in s.Project.Devices)
                {
                    if (filter != null && dev.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Console.WriteLine($"==== {dev.Name} ({ClassifyDevice(dev)}) 模块树 ====");
                    foreach (DeviceItem top in dev.DeviceItems) PrintItem(top, 1);
                }
                return 0;
            }
        }

        private static void PrintItem(DeviceItem it, int depth)
        {
            string indent = new string(' ', depth * 2);
            string pos = Attr(it, "PositionNumber");
            string order = Attr(it, "OrderNumber");
            string tid = Attr(it, "TypeIdentifier");
            string fw = Attr(it, "FirmwareVersion");
            string tail = "";
            if (order.Length > 0 && order != "?") tail += $"  订货号={order}";
            if (fw.Length > 0 && fw != "?") tail += $"  固件={fw}";
            Console.WriteLine($"{indent}[槽{pos}] {it.Name}{(tid.Length > 0 && tid != "?" ? "  <" + tid + ">" : "")}{tail}");
            foreach (DeviceItem child in it.DeviceItems) PrintItem(child, depth + 1);
        }

        // ===== device-network [设备名]：子网/IO系统拓扑 + 各设备网络接口节点地址（分布式 IO 网络排查）=====
        public static int DeviceNetwork(string filter)
        {
            using (var s = TiaSession.AttachFirst())
            {
                Console.WriteLine("==== 子网 / IO 系统 ====");
                try
                {
                    int sn = 0;
                    foreach (Subnet sub in s.Project.Subnets)
                    {
                        Console.WriteLine($"  子网 {sub.Name}  类型={Attr(sub, "TypeIdentifier")}");
                        try { foreach (IoSystem ios in sub.IoSystems) Console.WriteLine($"    IO系统 {ios.Name}  号={Attr(ios, "Number")}"); }
                        catch { }
                        sn++;
                    }
                    if (sn == 0) Console.WriteLine("  （无子网）");
                }
                catch (Exception ex) { Console.WriteLine("  (读子网失败: " + ex.Message + ")"); }

                foreach (Device dev in s.Project.Devices)
                {
                    if (filter != null && dev.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool header = false;
                    foreach (DeviceItem it in TiaSession.EnumerateDeviceItems(dev.DeviceItems))
                    {
                        NetworkInterface ni = null;
                        try { ni = it.GetService<NetworkInterface>(); } catch { }
                        if (ni == null) continue;
                        if (!header) { Console.WriteLine($"==== {dev.Name} 网络接口 ===="); header = true; }
                        try
                        {
                            string ifPn = FirstAttr(it, "PnDeviceNameSetByUser", "PnDeviceName", "ProfinetDeviceName");
                            foreach (Node node in ni.Nodes)
                            {
                                string nodePn = FirstAttr(node, "PnDeviceNameSetByUser", "PnDeviceName", "ProfinetDeviceName");
                                string pn = !string.IsNullOrEmpty(nodePn) ? nodePn : ifPn;
                                Console.WriteLine($"  {it.Name}: 地址={Attr(node, "Address")}  类型={Attr(node, "NodeType")}"
                                    + (string.IsNullOrEmpty(pn) ? "" : $"  PN名={pn}"));
                            }
                        }
                        catch (Exception ex) { Console.WriteLine("  (读节点失败: " + ex.Message + ")"); }
                    }
                }
                return 0;
            }
        }

        // 设备分类：看其 DeviceItem 是否含 PlcSoftware/HmiTarget（沿用 FindPlcs/FindHmis 同款判定）
        private static string ClassifyDevice(Device dev)
        {
            foreach (DeviceItem it in TiaSession.EnumerateDeviceItems(dev.DeviceItems))
            {
                var c = it.GetService<SoftwareContainer>();
                if (c != null && c.Software is PlcSoftware) return "PLC";
                if (c != null && c.Software is HmiTarget) return "HMI";
            }
            return "其他";
        }

        // 动态属性读取，统一 try/catch 兜底为 "?"（GSD/自动生成项部分属性会缺/抛）
        private static string Attr(IEngineeringObject o, string name)
        {
            try { var v = o.GetAttribute(name); return v?.ToString() ?? ""; }
            catch { return "?"; }
        }

        // 依次尝试候选属性名，返回首个读到的非空值（读不到/不支持回空）。用于不同型号/固件属性名不一的字段（如 PROFINET 设备名）。
        private static string FirstAttr(IEngineeringObject o, params string[] names)
        {
            foreach (var n in names)
            {
                try { var s = o.GetAttribute(n)?.ToString(); if (!string.IsNullOrEmpty(s) && s != "?") return s; }
                catch { }
            }
            return "";
        }
    }
}
