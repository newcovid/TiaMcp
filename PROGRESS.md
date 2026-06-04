# 进度跟踪 PROGRESS.md

> **规则**：每次新增/修改工具或做出重要决策，**同步更新本文件**。详细技术决策见
> 本地 Claude 记忆目录 `~/.claude/projects/<本项目>/memory/`（索引 MEMORY.md）。
> 本文件只记"状态/清单/决策"，技术细节看 CLAUDE.md 与 memory。

最近更新：2026-06-04（**指令集审计 + 8 修复 + 22 新命令(A+B+C) + 阶段5 MCP 打包完成**；审计 `docs/AUDIT-2026-06-04.md`，手册 `docs/COMMAND-MANUAL.md`，MCP 接入 `docs/MCP-SETUP.md`）

## 阶段状态
| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 环境/决策（net48+x64、手写JSON-RPC、E-SafeNet经%TEMP%） | ✅ |
| 1 | 连接 + 列设备/PLC/块 | ✅ |
| 2 | SCL/STL 读写 + 编译闭环 | ✅ |
| 3 | 全语言"读"（图形块 XML 导出 + 结构化解析） | ✅ |
| 2.5 超纲 | 排查套件（交叉引用/死代码/调用树） | ✅ |
| 4 | 图形块"写"✅ ｜ PLC侧批量✅ ｜ **HMI 探针✅ + 2a读✅ + 2b写✅** | ✅ |
| 5 | **打包成 MCP(stdio) 接入 Claude Code** | ✅ |

## 已实现命令（54）
- **读取**：`list` `read-tags` `read-udts` `export-source` `export-xml` `export-udt` **`block-info`🆕** `hmi-probe`
- **工程/硬件/库**🆕：`project-info` `device-list` `device-info [设备名]` `device-modules [设备名]` `device-network [设备名]`(子网/IoSystem/IP) `library-list`（项目库类型/母版 + 全局库枚举）（纯只读；device-modules 展开本地机架模块 + 分布式IO站模块树）
- **HMI 读**：`hmi-list` `hmi-read-tags`(--table/--filter) `hmi-read-screens` `hmi-read-screen <名>` **`hmi-read-templates`🆕** `hmi-read-connections` `hmi-export-all <目录>`(含模板)
- **HMI 写**：`hmi-write-tags` `hmi-delete-tags` `hmi-export-screen` `hmi-import-screen` `hmi-delete-screen`（全带 `--dry-run`）
- **HMI 模板(母版)**🆕：`hmi-export-template <名> <目录>` `hmi-import-template <xml>[--dry-run]` `hmi-delete-template <名>[--dry-run]`
- **HMI 列表**🆕：`hmi-import-list <xml>[--text|--graphic][--dry-run]` `hmi-delete-list <名>[--text|--graphic][--dry-run]`（读/导出已在 hmi-export-all）
- **排查**：`where-used` `block-deps` `find-unused` `call-tree` `callers-tree` **`crossref-report`🆕**(全项目交叉引用Markdown报表)
- **写入/重构**：`import-scl` `import-xml` `import-udt` `write-tags` **`delete-tags`/`edit-tags`🆕**(删/改PLC变量) `delete-block`(`--dry-run`/`--force`+删前查引用) **`rename-block`🆕**(引用影响告警) **`set-block-number`🆕**(`--dry-run`)
- **编译/批量/工程**：`compile` `compile-device` `export-all` **`project-save`🆕** **`project-archive`🆕**(归档.zapXX,需先save) **`export-project-texts`/`import-project-texts`🆕**(注释xlsx批改) **`export-watchtable`🆕**(监控/强制表)

## 指令集审计（2026-06-04，详见 docs/AUDIT-2026-06-04.md + docs/COMMAND-MANUAL.md）
- **覆盖率**：现有命令对"已实现能力"映射扎实；作为完整工业需求清单覆盖约 **82%**（≈79 条需求）。
- **已修 8 缺陷**（编译 0 错 0 警，**运行待实跑复核**）：find-unused 误报FB死代码(iDB→InstanceOfName传播)、write-tags 地址必填、StripSclNonCode 单引号吞码、hmi-write-tags 静默不绑定、hmi 变量名 `.` 校验、delete-block 加 dry-run/force/引用检查、import 新增块判定大小写、attach 打印项目诊断。
- **新命令路线图**：① MUST `project-info`/`device-list`/`block-info` ✅已建并实跑；②已加 `device-info`/`device-modules` ✅（答"本地模块/分布式IO"）；待做 SHOULD `rename-block`/PLC `delete-tags`/`library-list`/交叉引用报表/`ExportProjectTexts`、HMI 模板读写(`hmi-*-template`)、`device-network`；③ NICE 在线/下载/比较/归档/写列表。否决 `read-db`/`hmi-export-lists`(已覆盖)、`create-block-group`(无法闭环)、master copy(哲学不符)。
- **2026-06-04 续：路线图第一档(可见性/硬件)已落地并实跑**——project-info/device-list/device-info/device-modules/block-info 全通；block-info 实测 `FB_TIME_SET_DB→InstanceOfName=FB_TIME_SET` 顺带坐实 find-unused 修复的数据源。
- **手册优化**：原生交叉引用 V18 确无(维持扫描)；`GetAttributes` 批量读、编译结果按 State 过滤、`tag.Comment`(MultilingualText) 读注释——三项高价值。
- **Openness 边界(永久不可行，MCP 说明须写死)**：在线值/force/诊断缓冲/RUN-STOP/Upload/在线分配PN名/通道诊断。

## PLC 侧补满（计划 docs/superpowers/plans/2026-06-03-plc-side-completion.md）— ✅ 全部实跑验证
- [x] `compile-device`（整 PLC 编译）✅ 实跑：错误0
- [x] `export-all`（整项目导出给 Git）✅ 实跑：113块导出，跳过5受保护
- [x] UDT 定义读写（`export-udt`/`import-udt`）✅ export-udt 实跑通过（GenerateSource 接受 PlcType）
- [x] 批量变量写（`write-tags`，表名按行输入，地址必填）✅ 实跑：建2变量

## HMI 子系统（spec docs/superpowers/specs/2026-06-03-hmi-probe-design.md ｜ 计划 docs/superpowers/plans/2026-06-03-hmi-probe.md）
- 目标 HMI = **KTP700 Basic PN**（精简系列面板）。官方手册表 5-1~5-7 确证其支持矩阵。
- [x] `hmi-probe`（只读探针，分支 feat/hmi-probe）：设备发现 / 画面枚举+整屏导出 / 画面项XML解析 / 变量 / 连接 / 能力小结。
- **实跑结论（HMI_1）**：画面 **44 张**；首张整屏导出 **201279 字符明文**（经 %TEMP%，E-SafeNet 豁免确认）；单张画面 **64 个画面对象**（Hmi.Screen.* 元素，XML 共 3262 元素）；变量 **2 表 / 562 个**；连接 **1 个**（Connection_1）。
- **关键事实**：① 画面写路径 = 整屏 `Export(WithDefaults)` + `Import(Override)`（需同设备类型+同尺寸+画面号唯一），与 PLC 图形块同策略；② HMI `Tag` 无 `DataTypeName` 属性（用 GetAttribute 兜底，数据类型属性名待第二块 spec 钉）；③ 画面 XML 方言：根下 Engineering/DocumentInfo/Hmi.Screen.Screen。
### 2a 读命令集（spec 2026-06-03-hmi-read-commands-design.md ｜ 计划 2026-06-03-hmi-read-commands.md，分支 feat/hmi-read-commands）— ✅ 全部实跑
- [x] `hmi-list`：设备+计数+名字地图（实跑 44画面/2表/562变量/1连接，与探针一致）。
- [x] `hmi-read-tags [--table 名][--filter 子串]`：导出变量表XML解析，出 名/Coding(类型)/连接/**绑定的PLC变量**。实跑 `--filter Battery`=14个、`--table` 正确限表。
- [x] `hmi-read-screens`：画面文件夹树（只名字）。
- [x] `hmi-read-screen <名>`：单屏控件 + 绑定变量。实跑 MainPicture=20控件（IOField→Battery_SOC 等绑定清晰）。
- [x] `hmi-read-connections`：连接名（经典 Connection 仅暴露 Name）。
- [x] `hmi-export-all <目录>`：画面+变量表+连接+列表全快照。实跑落 44+2+1+5=52 个明文XML。
- **实测钉死的事实**（见 memory hmi-probe-results）：① **两套 API**——经典 `HmiTarget`(本机用)vs Unified `HmiSoftware`(.DataType 等，对 Basic 不适用)；② 经典 Screen/Connection/Tag 对象**只暴露 Name 属性**，详情全在导出 XML；③ 变量 XML：`<Hmi.Tag.Tag>` 无 DataType 元素，类型看 `<Coding>`(IEEE754Float=Real)，PLC 绑定在 `<LinkList><ControllerTag>`；④ 画面 XML：`Hmi.Screen.*` 控件，绑定变量在含 "Tag" 且带 `<Name>` 的子节点。
### 2b 写子系统（spec/计划 2026-06-03-hmi-write-commands-*，分支 feat/hmi-write-commands）— ✅ 全部实跑
- [x] `hmi-write-tags <清单>[--dry-run]`：克隆模板建/改符号绑定变量。实跑建 MCP_Test_HMITag 绑 BatteryDataBlock.TotalVoltage，类型Real正确继承，562→563现有不丢。
- [x] `hmi-delete-tags <清单>[--dry-run]`：API 删；删回测试变量(563→562)；dry-run 删真实变量验证不动工程。
- [x] `hmi-export-screen <名> <目录>`：单屏导出（2000CESHI 44931字符明文）。
- [x] `hmi-import-screen <xml>[--dry-run]`：整屏替换+明文校验。实跑往返 2000CESHI 原样导回[改]成功，画面44不变、内容完好（往返恒等）。
- [x] `hmi-delete-screen <名>[--dry-run]`：API 删；dry-run 验证（不删真实画面）。
- **MCP 优化**：全带 `--dry-run` 预演 + 结构化逐条回调（[建]/[改]/[删]/[跳]/[错]）+ 幂等非致命。
- **写侧实测钉死**（见 memory hmi-probe-results）：① 克隆模板建变量**必须重编 ID**（导入要求 ID 存在且唯一，不能去除）；② Coding/Length 等类型字段由 TIA 按绑定的 PLC 符号正确继承（首测同 Real）；③ Import(Override) 导入完整表，现有变量不丢；④ 画面往返恒等，整屏替换安全。
- **下一步**：打包 MCP（把 31 条命令包成 stdio JSON-RPC 工具）。

## 健壮性改进（2026-06-03 review 硬编码参数后）
- Openness DLL 目录**改为从注册表自动定位**（`HKLM\...\Openness\18.0\PublicAPI\18.0.0.0\Siemens.Engineering`），不再写死 D 盘；保留兜底路径。
- 所有写命令（import-scl/xml/udt、write-tags）统一**打印目标 PLC 名**（多 PLC 时提示默认第一个）。
- 遗留（低优先）：csproj `<HintPath>` 仍写死（仅编译时）；多 PLC"指定 PLC"参数待加。
- **临时文件落 %TEMP% 根**（不再用固定子目录 %TEMP%\TiaMcp）：实测固定子目录持久后被 E-SafeNet 加访问保护（连本进程都拒写，导出报 "Cannot create file"）。改 IoUtil.NewTempFile 每文件直接落 %TEMP% 根 + GUID 名。惠及所有导出命令。

## 阶段5 MCP 打包（✅ 2026-06-04，详见 docs/MCP-SETUP.md）
- `McpServer.cs`：手写 stdio JSON-RPC 2.0（protocolVersion 2024-11-05），`TiaMcp.exe mcp` 启动。
- **JSON 用 .NET 自带 `System.Web.Script.Serialization`（非 Newtonsoft）**——零 NuGet、自包含，clone 即编译（修正了 decision-net-compat 的旧设想）。
- 54 命令全包成 MCP 工具：ToolDef 登记表 + 通用 argv 组装（位置参/旗标/带值旗标/inline 文本经 %TEMP%）；`Program.Main` 拆出 `Dispatch`，MCP 重定向捕获命令 stdout 作工具结果、不改命令本身。
- 实测：initialize/tools-list(54工具) 不需 TIA 通过；tools/call(list) 活 TIA 通过，命令输出被捕获未泄漏到协议流，isError=false。
- **v1 大输出回完整文本**（保真，AI 整块导回需完整 XML/SCL）；v2 再按手册附录A做摘要+artifacts(full 按需)。

## 待补（后续）
- [ ] MCP v2：大输出摘要+artifacts 信封（附录A）；`--device` 多设备选择下沉 TiaSession
- [ ] D 类在线/下载（需安全策略授权后再做）；compare-software（多PLC/有库时）

## 关键决策（速记）
- 框架 net48 + x64；MCP 层手写 stdio JSON-RPC（不引官方 SDK）；JSON 用 Newtonsoft（打包时引）。
- **E-SafeNet**：所有 TIA 文件 I/O 经 `%TEMP%` 中转 + 明文校验 + 即用即删；不信任持久化明文。
- **图形块**：不精修 SimaticML，只整块替换(import-xml)或重写为 SCL；**CEM 不做**。
- **know-how 保护块**：不自动解密，需手动在博图解锁；工具自动识别并单列。

## 已知受保护块（目标工程，读不了内部）
PID_S、PID_Control、PN_SEW_Speed_Control、PN_SEW_Speed_Control_1、PID_Control_DB。
