using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.Hmi;
using HmiTarget = Siemens.Engineering.Hmi.HmiTarget;

namespace TiaMcp
{
    /// <summary>
    /// 一次 TIA 连接的封装：attach 运行中的实例 → 拿到当前项目，
    /// 并提供"找所有 PLC""按名字找块"等导航辅助。
    /// 用 using 包裹，结束时自动断开（不影响你继续在界面里手动操作 TIA）。
    /// </summary>
    internal sealed class TiaSession : IDisposable
    {
        public TiaPortal Portal { get; }
        public Project Project { get; }

        private TiaSession(TiaPortal portal, Project project)
        {
            Portal = portal;
            Project = project;
        }

        /// <summary>attach 到"已打开项目"的第一个实例。</summary>
        public static TiaSession AttachFirst()
        {
            var procs = TiaPortal.GetProcesses();
            if (procs == null || procs.Count == 0)
                throw new InvalidOperationException("没有正在运行的 TIA Portal 实例。请先打开 TIA 并打开项目。");

            var target = procs.FirstOrDefault(p => p.ProjectPath != null) ?? procs[0];
            Logger.Info($"Attach 到 PID={target.Id}");
            TiaPortal portal = target.Attach();

            Project project = portal.Projects.FirstOrDefault();
            if (project == null)
            {
                portal.Dispose();
                throw new InvalidOperationException("该实例没有打开的项目。");
            }
            Logger.Info($"已连接项目: {project.Name}");
            // 诊断走 stderr（不污染 stdout）：多开 TIA 时让用户/AI 确认连对了项目，避免写错工程
            try { Console.Error.WriteLine($"[连接] PID={target.Id} 项目={project.Name} 路径={target.ProjectPath}"); } catch { }
            return new TiaSession(portal, project);
        }

        /// <summary>项目里所有 PLC（设备名 -> PlcSoftware）。</summary>
        public List<KeyValuePair<string, PlcSoftware>> FindPlcs()
        {
            var result = new List<KeyValuePair<string, PlcSoftware>>();
            foreach (Device device in Project.Devices)
            {
                foreach (DeviceItem item in EnumerateDeviceItems(device.DeviceItems))
                {
                    var container = item.GetService<SoftwareContainer>();
                    if (container != null && container.Software is PlcSoftware plc)
                        result.Add(new KeyValuePair<string, PlcSoftware>(device.Name, plc));
                }
            }
            return result;
        }

        /// <summary>项目里所有 HMI 设备（设备名 -> HmiTarget）。对称 FindPlcs。</summary>
        public List<KeyValuePair<string, HmiTarget>> FindHmis()
        {
            var result = new List<KeyValuePair<string, HmiTarget>>();
            foreach (Device device in Project.Devices)
            {
                foreach (DeviceItem item in EnumerateDeviceItems(device.DeviceItems))
                {
                    var container = item.GetService<SoftwareContainer>();
                    if (container != null && container.Software is HmiTarget hmi)
                        result.Add(new KeyValuePair<string, HmiTarget>(device.Name, hmi));
                }
            }
            return result;
        }

        /// <summary>在所有 PLC 里按名字找第一个匹配的块。找不到返回 null，owner 也为 null。</summary>
        public PlcBlock FindBlock(string blockName, out PlcSoftware owner)
        {
            foreach (var kv in FindPlcs())
            {
                var block = FindBlockInGroup(kv.Value.BlockGroup, blockName);
                if (block != null) { owner = kv.Value; return block; }
            }
            owner = null;
            return null;
        }

        /// <summary>
        /// 判断块是否受 know-how(专有技术)保护。主用 Openness 属性 IsKnowHowProtected；
        /// 属性名不支持时返回 false（由调用方的导出兜底检测：SCL 抛 know-how 异常、图形块无 CompileUnit）。
        /// </summary>
        public static bool IsKnowHowProtected(PlcBlock b)
        {
            try { var v = b.GetAttribute("IsKnowHowProtected"); return v is bool bv && bv; }
            catch { return false; }
        }

        private static PlcBlock FindBlockInGroup(PlcBlockGroup group, string name)
        {
            foreach (PlcBlock b in group.Blocks)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) // TIA 标识符大小写不敏感
                    return b;
            foreach (PlcBlockGroup sub in group.Groups)
            {
                var found = FindBlockInGroup(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>递归枚举一组 DeviceItem（含其所有子项）。Device 和 DeviceItem 都有 .DeviceItems。</summary>
        public static IEnumerable<DeviceItem> EnumerateDeviceItems(DeviceItemComposition items)
        {
            foreach (DeviceItem item in items)
            {
                yield return item;
                foreach (DeviceItem child in EnumerateDeviceItems(item.DeviceItems))
                    yield return child;
            }
        }

        public void Dispose() => Portal?.Dispose();
    }
}
