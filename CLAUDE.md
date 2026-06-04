# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 这是什么
一个 C# 控制台程序，通过 **Siemens TIA Portal V18 Openness**（Siemens.Engineering.dll）读取/编辑/分析 PLC+HMI 工程。**命令行子命令 + MCP(stdio) 服务器（`TiaMcp.exe mcp`）两形态均已实现**（54 命令，详见 docs/MCP-SETUP.md），供 Claude Code 自然语言调用。自用、单机、单用户。被操作的目标工程：单台 **S7-1200**、约 118 块、**LAD+SCL 混用**、大量中文块名/成员名 + 1 台 KTP700 Basic HMI。

## 编译 / 运行
- **编译**：`.\build.ps1`（即 `& "C:\Program Files\dotnet\dotnet.exe" build TiaMcp.csproj -c Release`）。
  - 系统 PATH 里的 `dotnet` 是 **x86 且无 SDK**（会报 "No SDKs were found"）。**必须用 `C:\Program Files\dotnet` 的 x64 SDK**。产物：`bin\Release\net48\TiaMcp.exe`。
  - **目标固定 net48 + x64**（Openness 跑在 .NET Framework、TIA 是 64 位），csproj 写死 `<PlatformTarget>x64</PlatformTarget>`，**绝不能 AnyCPU**。
- **运行**：`bin\Release\net48\TiaMcp.exe <命令> [参数]`。需先打开 TIA V18 与一个项目。**每次重编译后首次连接会弹 TIA 授权框**（白名单按 exe 的 SHA256 记，改一次哈希就重弹），到 TIA 窗口点"是"。
- **无自动化测试**。验证方式：① `dotnet build` 会拿真实 Siemens.Engineering.dll 校验 Openness API 签名；② 跑对应子命令、对照实物结果。**所有 .ps1 必须纯 ASCII**（中文 Windows 的 PowerShell 5.1 会把无 BOM 的 UTF-8 脚本读乱）。
- **命令行路径参数注意**：用户用 `!` 前缀跑命令时走的是 **bash**。未加引号的 Windows 反斜杠会被吃掉（`D:\tmp\x` → `D:tmpx`）。给路径参数**一律用正斜杠**（`D:/tmp/x`，.NET 在 Windows 上接受）或双引号。`!` 命令里别用 PowerShell 反引号。需要给命令准备明文输入文件时，**直接用文件工具写**，别用 shell 拼。

## 架构（大图）
- **`Program.cs`（入口）**：**先**注册 `AppDomain.AssemblyResolve`，**再**分发：`mcp` → `McpServer.Run()`，否则 → `Dispatch(argv)`（switch 在此）。**关键坑**：`Main` 里不能出现任何 `Siemens.Engineering.*` 类型——JIT 进入方法前会解析其类型，那时解析器还没生效。所有 Openness 类型的代码都在 `Dispatch` 及以下（含 McpServer 之外的类）；`McpServer` 自身也不碰 Siemens 类型。
- **`McpServer.cs`（阶段5）**：手写 stdio JSON-RPC 2.0（不引 SDK），JSON 用 .NET 自带 `System.Web.Script.Serialization`（非 Newtonsoft，零 NuGet）。54 命令登记成 ToolDef，通用组装 argv（位置参/旗标/inline 文本经 %TEMP%），重定向捕获命令 stdout 作工具结果。stdout 被 JSON-RPC 独占，命令输出全程捕获不泄漏。
- **`OpennessAssemblyResolver`**：把 `Siemens.*` 重定向到 `D:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18`。csproj 引用 Siemens.Engineering 用 `<Private>false</Private>`（不拷贝，运行时从那里加载）。
- **`TiaSession`**：attach 运行中的实例（`TiaPortal.GetProcesses().Attach()`）；暴露当前 Project、FindPlcs（递归 DeviceItems→SoftwareContainer→PlcSoftware）、FindBlock（大小写不敏感）、`IsKnowHowProtected`。
- **`Commands`**：list / export-source / export-xml / export-udt / import-scl / import-xml / import-udt / write-tags / delete-block(`--dry-run`/`--force`) / compile / compile-device / export-all / **project-save**(把改动落盘，写命令闭环)。
- **`CrossRef`**：内容扫描式交叉引用套件——where-used / block-deps / find-unused / call-tree / callers-tree（+ `FindReferrers` 供 delete-block 删前查引用）。**Openness V18 无原生交叉引用 API（已全文核实 0 命中，结论固化）**，故把每块导出成文本再扫描；非语义级。
- **`Reads`**：read-tags / read-udts。
- **`IoUtil` / `Logger`**：临时文件+加密辅助 / 文件日志。

## 不可回退的硬约束（来之不易）
1. **本机 E-SafeNet 透明加密**：**TIA 进程**写的文件被加密（头含 `E-SafeNet`），**我们进程**写的是明文，**`%TEMP%` 是豁免区**。故**所有 TIA 文件 I/O 必经 `%TEMP%`（IoUtil.NewTempFile），写后/读后校验非密文（IoUtil.LooksEncrypted），即用即删**。还有定期全盘扫描会把落盘明文再加密，**永不信任持久化明文文件**——交付物是内存文本，临时文件是一次性的。
   - **1a. CJK/UTF-8 BOM 铁律（来之不易，import-scl/udt 乱码根因）**：写给 TIA 读的 TEMP 文本文件（外部源 `.scl/.udt`）**必须带 UTF-8 BOM**。TIA 在中文 Windows(ANSI 码页 936/GBK)上读**无 BOM** 文本时按 GBK 解码 → 中文标识符被 UTF-8→GBK 误解码成乱码、找不到中文 DB 引用。坑：`Encoding.GetBytes()` **永不含 BOM**（参数 `UTF8Encoding(true)` 形同虚设），只有 `GetPreamble()`/`StreamWriter` 才落 BOM。统一出口 `IoUtil.WriteTempPlaintextVerified` 已显式拼 `GetPreamble()` 并把含 BOM 的完整字节交回读校验——**所有给 TIA 读的中转文本都必须走它，别另写 `WriteAllBytes(GetBytes(...))`**。导出给用户/Git 的交付物则用无 BOM(`UTF8Encoding(false)`)，那条路不经 TIA。
2. **stdout 纪律**：进度/诊断走 stderr，日志走文件。阶段5（MCP stdio）stdout 被 JSON-RPC 独占，那条路上绝不能往 stdout 打调试。
3. **STA + 串行**：Openness 有 COM 交互、非线程安全。`[STAThread]`、请求串行处理。
4. **双路径读写**：SCL/STL 走文本源（GenerateSource / 外部源 CreateFromFile+GenerateBlocksFromSource）；图形(LAD/FBD/GRAPH)走 SimaticML XML（PlcBlock.Export / Blocks.Import(Override)）。
5. **图形块策略（已定）**：**不精修 SimaticML**。图形块只做：读/导出、整块替换(import-xml)、或重写为 SCL。**CEM 不做**。
6. **know-how 受保护块**：不解锁读不了内容（**不做自动解密，手动在博图解锁**）。检测：`IsKnowHowProtected` 属性 / GenerateSource 抛 "know-how" / 图形块 XML 无 `SW.Blocks.CompileUnit`。统一单列、不计入分析。
7. **交叉引用精度**：SCL 匹配前用 `StripSclNonCode` 去注释+字符串字面量；XML 用 XDocument 解析 `<Access><Symbol><Component>` 路径与 `<CallInfo>`。多 PLC 同名块未完全支持（索引按裸名，重名时告警）。

## SimaticML 速查（V18）
- 块根：`<SW.Blocks.FC|FB|OB>`，内含 `<AttributeList>`(Interface/ProgrammingLanguage/Number…) 与 `<ObjectList>`（每个网络一个 `<SW.Blocks.CompileUnit>`）。
- 操作数/DB成员访问：`<Access><Symbol><Component Name="DB"/><Component Name="成员"/>…</Symbol></Access>`，成员路径=各 Component 顺序拼 `.`。
- 块调用：`<CallInfo Name="被调块" BlockType="FB|FC">`；背景DB在其 `<Instance>` 下。

## MCP 打包 I/O 约定（2026-06-04 审计定，详见 docs/COMMAND-MANUAL.md 附录 A/B）
- **统一返回信封**：`{ok, tool, target{device,defaulted,devicesPresent}, dryRun, summary, data, artifacts[], diagnostics}`。错误用 `{ok:false,errorCode,message}`，errorCode 枚举取代重载 exit code。
- **大产物不内联**：凡 >~8KB 文本（源码/XML/画面/大消息）写 `%TEMP%` 回 `{path,charCount,sha256}` + 结构化摘要，别灌爆上下文。**污染大户**：hmi-read-screen / export-xml / export-source / hmi-probe（均数万字符）。
- **入参** inline 文本（不要求 AI 传磁盘路径）、named 参数、`targetDevice` 缺省第一个但在 diagnostics.warnings 显式记、破坏性工具必须 `dryRun`。
- **Openness 永久不可行边界**（工具说明须写死，防 AI 幻觉）：在线实际值 / force / 诊断缓冲区 / RUN-STOP / Upload / 在线分配PN名IP / 通道级诊断。
- **MCP 长驻进程优化**：交叉引用应缓存"块名→导出文本/引用集合"索引（现 CLI 每命令独立进程做不到，每次全量导出）。

## 项目记忆与进度
- 长期事实/决策：本地 Claude 记忆目录 `~/.claude/projects/<本项目>/memory/`（索引 MEMORY.md）。
- 进度跟踪：本仓库 `PROGRESS.md`。**每次新增/改动工具都要同步更新 PROGRESS.md。**
- 指令集手册（用法+输出示例）：`docs/COMMAND-MANUAL.md`；审计与新命令路线图：`docs/AUDIT-2026-06-04.md`。
