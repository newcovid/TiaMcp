# TIA-MCP · 用自然语言操作西门子 TIA Portal V18

> 一个 C# 控制台程序，通过 **Siemens TIA Portal V18 Openness** 读取/编辑/分析运行中的 **PLC + HMI** 工程。
> **命令行子命令 + MCP(stdio) 服务器两形态均已实现**——把 **54 条命令**包成 MCP 工具，供 Claude Code 等用自然语言调用。
> 自用 / 单机 / 单用户。目标工程：单台 **S7-1200**（约 118 块，LAD+SCL 混用，大量中文名）+ 1 台 **KTP700 Basic** HMI。

**状态：阶段 1–5 全部完成**（连接 → SCL/STL 读写 → 全语言读 → 图形/HMI 写 → 排查套件 → MCP 打包）。

---

## 能做什么（54 工具，逐条用法+输出见 [`docs/COMMAND-MANUAL.md`](docs/COMMAND-MANUAL.md)）

- **读取/导出**：列块、读变量(含中文注释)/UDT、导出 SCL/STL 源码、导出任意块/DB 的 SimaticML XML、块元数据。
- **硬件/库**：设备清单、CPU/模块订货号固件、机架/槽位/模块树(本地+分布式IO)、子网/IoSystem/IP、项目库/全局库。
- **排查**：where-used / 依赖 / 死代码 / 调用树 / 被调树 / 全项目交叉引用报表（内容扫描式，V18 无原生交叉引用 API）。
- **写/重构**：导入 SCL/SimaticML/UDT、建/删/改 PLC 变量、删块/改块名/改块号（破坏性操作带 `--dry-run` + 引用影响告警）。
- **编译/工程**：编译块/整 PLC、整项目导出给 Git、保存、归档 .zapXX、监控/强制表导出、全工程注释/文本 xlsx 批量改。
- **HMI**：画面/模板(母版)/变量/连接/文本图形列表 的读+整屏(整模板)替换+变量增删改（经典 KTP700 Basic）。

> **不做**（Openness 设计上不可行，工具不提供）：在线实际值 / force / 诊断缓冲区 / RUN-STOP / 上传(Upload) / 在线分配PN名IP / 通道级诊断。在线/下载类命令需安全策略授权后再加。详见 [`docs/COMMAND-MANUAL.md`](docs/COMMAND-MANUAL.md) 附录 B。

---

## 快速开始

### 1. 编译
```powershell
.\build.ps1
```
产物：`bin\Release\net48\TiaMcp.exe`（net48 + x64）。

### 2. 打开 TIA
先**打开 TIA Portal V18 并打开目标项目**。每次重编译后第一次连接会在 TIA 窗口弹 Openness 授权框，点**"是"**（同一 exe 之后不再弹）。

### 3a. 命令行用
```powershell
.\bin\Release\net48\TiaMcp.exe list
.\bin\Release\net48\TiaMcp.exe export-source FB_Motor
.\bin\Release\net48\TiaMcp.exe find-unused
```
不带参数会打印所有命令分组。

### 3b. 作为 MCP 服务器用（接 Claude Code）
```powershell
.\bin\Release\net48\TiaMcp.exe mcp     # 启动 stdio JSON-RPC 服务器
```
在 Claude Code 里加（或放 `.mcp.json`）：
```json
{ "mcpServers": { "tia": { "type": "stdio",
  "command": "<TiaMcp.exe 全路径>", "args": ["mcp"] } } }
```
然后自然语言即可：「列出所有 PLC 块」「把 FB_Motor 导出成 SCL」「找死代码」「读 HMI 变量」。
完整接入说明（前提/限制/自检）见 **[`docs/MCP-SETUP.md`](docs/MCP-SETUP.md)**。

---

## 关键技术决策

| 项 | 选择 | 理由 |
|---|---|---|
| 目标框架 | **net48 + x64**（csproj 写死） | Openness 跑在 .NET Framework、TIA 是 64 位；AnyCPU/x86 会 BadImageFormatException |
| MCP 协议层 | **手写 stdio JSON-RPC 2.0**（不引官方 SDK） | 官方包在 net48 上要拖一堆现代依赖；只需 initialize/tools-list/tools-call |
| JSON 库 | **.NET 自带 `System.Web.Script.Serialization`** | 零 NuGet、自包含，clone 即编译；**整个项目无第三方依赖** |
| 连接方式 | attach 运行中的 TIA 实例 | 在界面实时观察改动 |
| 读写架构 | **双路径**：SCL/STL 走文本源，图形(LAD/FBD/GRAPH)走 SimaticML XML 整块替换 | Openness 限制使然 |

---

## 架构 / 文件

| 文件 | 作用 |
|---|---|
| `Program.cs` | 入口：先注册程序集解析器（不碰 Siemens 类型），再 `mcp`→`McpServer` 或 `Dispatch(argv)` |
| `McpServer.cs` | 手写 stdio JSON-RPC；54 命令登记成工具，捕获命令 stdout 作工具结果，不改命令本身 |
| `OpennessAssemblyResolver.cs` | 把 `Siemens.*` 重定向到 PublicAPI\V18（注册表自动定位 + 兜底路径）|
| `TiaSession.cs` | attach 实例 → 取项目 → FindPlcs/FindHmis/FindBlock/IsKnowHowProtected |
| `Commands.cs` / `Reads.cs` / `Hardware.cs` / `Library.cs` | PLC 读写编译 / 变量 UDT / 硬件设备 / 库 |
| `CrossRef.cs` | 内容扫描式交叉引用套件 |
| `Hmi.cs` / `HmiReads.cs` / `HmiWrites.cs` | HMI 探针 / 读 / 写（画面+模板+变量+列表）|
| `IoUtil.cs` / `Logger.cs` | %TEMP% 中转+明文校验 / 文件日志 |

---

## ⚠️ 本机特有：E-SafeNet 透明加密与 %TEMP% 中转

本机装了 **E-SafeNet 透明加密**驱动，**按写入进程加密**：TIA 进程写出的文件被加密（头含 `E-SafeNet`/`LOCK`），我们自己进程写的是明文，**`%TEMP%` 是豁免区**。
因此**所有 TIA 文件 I/O 都经 `%TEMP%` 中转 + 明文校验 + 即用即删**：导出=TIA 写 %TEMP% → 我们读出明文；导入=我们写 %TEMP% 明文 → TIA 读。落盘明文副本不可信任（全盘扫描会稍后加密），交付物是内存文本。换没有此加密的机器，中转无害可保留。

---

## 文档索引

| 文档 | 内容 |
|---|---|
| [`docs/COMMAND-MANUAL.md`](docs/COMMAND-MANUAL.md) | **54 命令逐条**：用途/输入/输出示例/退出码/MCP备注 + MCP I/O 约定 + Openness 不可行边界 |
| [`docs/MCP-SETUP.md`](docs/MCP-SETUP.md) | MCP 接入：Claude Code 配置 / 协议 / 前提 / 限制 / 自检 |
| [`docs/AUDIT-2026-06-04.md`](docs/AUDIT-2026-06-04.md) | 指令集审计：覆盖矩阵 / 新命令路线图 / 手册优化 / 缺陷修复日志 |
| `PROGRESS.md` / `CLAUDE.md` | 进度状态 / 给 AI 的工程约定 |

## 已知坑速查

- **BadImageFormatException** → 平台不是 x64。
- **找不到 Siemens.Engineering** → Openness DLL 路径不对，或没注册 AssemblyResolve。
- **No SDKs were found** → 用了 x86 的 dotnet；用 `build.ps1`（已写 x64 全路径）。
- **连接没反应** → 在等你点 TIA 的 Openness 授权框；去 TIA 窗口看一眼。
- **GetProcesses 为空** → TIA 没开，或没用同一 Windows 账号 / 账号不在 Openness 组。
