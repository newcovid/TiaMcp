using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;

namespace TiaMcp
{
    /// <summary>
    /// library-list：项目库(类型/母版副本) + 已打开全局库 的只读枚举。纯读、内存交付、不落盘。
    /// 用于盘点可复用类型/模板。打开磁盘全局库(.alXX)不在此命令范围（需 Open/Close 生命周期管理）。
    /// </summary>
    internal static class Library
    {
        public static int LibraryList()
        {
            using (var s = TiaSession.AttachFirst())
            {
                Console.WriteLine("==== 项目库 ProjectLibrary ====");
                try
                {
                    var pl = s.Project.ProjectLibrary;
                    var types = new List<LibraryType>(); CollectTypes(pl.TypeFolder, types);
                    Console.WriteLine($"  -- 类型 Types: {types.Count} --");
                    foreach (var t in types)
                    {
                        int vers = 0; try { foreach (var v in t.Versions) vers++; } catch { }
                        Console.WriteLine($"    [类型] {t.Name}  版本数={vers}");
                    }
                    var copies = new List<MasterCopy>(); CollectCopies(pl.MasterCopyFolder, copies);
                    Console.WriteLine($"  -- 母版副本 MasterCopies: {copies.Count} --");
                    foreach (var c in copies) Console.WriteLine($"    [副本] {c.Name}");
                }
                catch (Exception ex) { Console.WriteLine("  (读项目库失败: " + ex.Message + ")"); }

                Console.WriteLine("==== 全局库 GlobalLibraries（已打开/已知）====");
                try
                {
                    int n = 0;
                    foreach (GlobalLibraryInfo info in s.Portal.GlobalLibraries.GetGlobalLibraryInfos())
                    {
                        Console.WriteLine($"  {info.Name}  路径={info.Path}  类型={info.LibraryType}  已打开={info.IsOpen}");
                        n++;
                    }
                    if (n == 0) Console.WriteLine("  （无已打开的全局库）");
                }
                catch (Exception ex) { Console.WriteLine("  (读全局库失败: " + ex.Message + ")"); }
                return 0;
            }
        }

        private static void CollectTypes(LibraryTypeSystemFolder root, List<LibraryType> acc)
        {
            foreach (LibraryType t in root.Types) acc.Add(t);
            foreach (LibraryTypeUserFolder f in root.Folders) CollectTypesU(f, acc);
        }
        private static void CollectTypesU(LibraryTypeUserFolder folder, List<LibraryType> acc)
        {
            foreach (LibraryType t in folder.Types) acc.Add(t);
            foreach (LibraryTypeUserFolder f in folder.Folders) CollectTypesU(f, acc);
        }
        private static void CollectCopies(MasterCopySystemFolder root, List<MasterCopy> acc)
        {
            foreach (MasterCopy c in root.MasterCopies) acc.Add(c);
            foreach (MasterCopyUserFolder f in root.Folders) CollectCopiesU(f, acc);
        }
        private static void CollectCopiesU(MasterCopyUserFolder folder, List<MasterCopy> acc)
        {
            foreach (MasterCopy c in folder.MasterCopies) acc.Add(c);
            foreach (MasterCopyUserFolder f in folder.Folders) CollectCopiesU(f, acc);
        }
    }
}
