using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace TiaMcp
{
    /// <summary>
    /// 把对 Siemens.* 程序集的加载请求，重定向到 TIA 安装目录下的 PublicAPI\V18。
    /// 这些 DLL 不在 GAC、也没拷到输出目录（csproj Private=false），必须运行时手动指路。
    /// 官方推荐做法（Siemens 文档号 109815895）。
    /// 目录**优先从注册表自动定位**（不写死路径），找不到再用兜底路径。
    /// </summary>
    internal static class OpennessAssemblyResolver
    {
        // 换机器/换盘符时的兜底路径（实测本机在 D 盘）
        private const string FallbackDir =
            @"D:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18";

        private static string _dir;
        /// <summary>Openness DLL 所在目录（首次访问时解析一次）。</summary>
        public static string Dir => _dir ?? (_dir = ResolveDir());

        private static string ResolveDir()
        {
            try
            {
                // 注册表里直接记着 Siemens.Engineering.dll 的全路径
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Siemens\Automation\Openness\18.0\PublicAPI\18.0.0.0"))
                {
                    string dll = k?.GetValue("Siemens.Engineering") as string;
                    if (!string.IsNullOrEmpty(dll) && File.Exists(dll))
                        return Path.GetDirectoryName(dll);
                }
            }
            catch { /* 读注册表失败就用兜底 */ }
            return FallbackDir;
        }

        public static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                // args.Name 形如 "Siemens.Engineering, Version=18.0.0.0, Culture=..., PublicKeyToken=..."
                string requested = new AssemblyName(args.Name).Name; // 取出 "Siemens.Engineering"
                if (string.IsNullOrEmpty(requested))
                    return null;

                // 只接管 Siemens 家族，避免误伤别的程序集
                if (!requested.StartsWith("Siemens", StringComparison.OrdinalIgnoreCase))
                    return null;

                string dllPath = Path.Combine(Dir, requested + ".dll");
                if (File.Exists(dllPath))
                {
                    Logger.Info($"解析程序集 {requested} -> {dllPath}");
                    return Assembly.LoadFrom(dllPath);
                }

                Logger.Error($"在 {Dir} 找不到 {requested}.dll", null);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("程序集解析器异常: " + args.Name, ex);
                return null;
            }
        }
    }
}
