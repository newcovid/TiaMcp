# 进度跟踪 PROGRESS.md

> **规则**：每次新增/修改工具或做出重要决策，**同步更新本文件**。详细技术决策见
> 本地 Claude 记忆目录 `~/.claude/projects/<本项目>/memory/`（索引 MEMORY.md）。
> 本文件只记"状态/清单/决策"，技术细节看 CLAUDE.md 与 memory。

最近更新：2026-06-05（**新增 `hmi-read-screen-layout` 命令 — 画面视觉布局信息**；更新 MCP 工具说明让 AI 明确区分摘要 vs 布局 vs 完整XML）

> **2026-06-05 新增 `hmi-read-screen-layout` 命令 — 画面视觉布局信息**：
> - **问题**：`hmi-read-screen` 只返回控件类型+变量绑定摘要，不含位置/大小/颜色/字体等视觉信息；`hmi-export-screen` 导出完整XML（200K+字符）占用大量上下文，AI不愿主动调用。
> - **方案**：新增 `hmi-read-screen-layout <画面名>` 命令，解析画面XML提取视觉属性：
>   - 控件位置 (X, Y) 和大小 (W × H)
>   - 颜色属性 (背景色/前景色/边框色/替代色)
>   - 字体属性 (字体名/大小/粗体/斜体/下划线/删除线)
>   - 边框属性 (宽度/线型)
>   - 文本内容
>   - 变量绑定
>   - 圆角/透明度/可见性/旋转
>   - 重叠控件检测
> - **输出格式**：表格化摘要 + 按类型分组的详细属性 + 布局统计
> - **MCP 工具说明优化**：明确标注 `hmi-read-screen` 仅基础摘要、`hmi-read-screen-layout` 提供视觉布局、`hmi-export-screen` 提供完整XML
> - **验证**：编译 0 错 0 警

> **2026-06-04 能力增强 · inline 导入工具统一支持文件路径入口（解决大文件内联报错）**：`hmi-import-screen` 等只接 inline 文本，大画面 XML（实测单画面达 201,279 字符）内联导入会失败。
> - **根因**：非 TIA/Openness 限制，也非服务器 JSON 上限（`MaxJsonLength=int.MaxValue`）；瓶颈在传输上游——inline 要求 AI 把整份文本作单个 JSON 实参逐字复现，撞 token 上限/截断/转义错。违反项目自己「大产物不内联」约定（原仅写给输出，输入侧漏了）。
> - **修复（仅 `McpServer.cs`，底层命令零改）**：任何带 `TextParam` 的工具自动获得伴生 `<名>Path`（`xmlText`↔`xmlPath`/`sclText`↔`sclPath`/`listText`↔`listPath`，`PathParamName` 自动派生 + `ToolDef.PathParam` 兜底）。`BuildArgs` 见路径直通、不写 TEMP。**text/path 二选一；都给路径优先 + 前置 `[注意]`；都不给干净报错**。覆盖全部 11 个 inline 导入工具。大负载工具 Desc + 各 `<名>Path` 参数描述均写入 AGENT 引导（大文件走路径、典型来源 export-* 产物）。
> - **安全不破**：路径直通后由底层命令读，已有 `LooksEncrypted` 拒密文、`DecodeUtf8StripBom` 容 BOM、对 TIA 二次中转仍 `WriteTempPlaintextVerified` 带 BOM。
> - **验证**：build 0 错（1 警 CS0649=`PathParam` 兜底字段未显式赋值,无害）；`tools/list` 确认各工具出 `<名>Path` 且 text 不再 required；error 路径(都不给)、both-given(路径优先+提示)、path 直通(verbatim 转发不写 TEMP) 均经 MCP 烟雾测试通过。**剩活体闭环（真实画面 import 成功）需 TIA 开 + 手点 Openness 授权，留用户手测**。

> **2026-06-04 实测加固（McpServer.cs 工具说明，build 0 错 0 警）**：真实项目跑出两条事实写进 MCP 接口描述，供新 AGENT 零上下文即知：
> 1. `export-source`/`export-xml`：块被改过未重编→`IsConsistent=False`→Export 抛通用错 `Error when calling method Export`（是未编译,非加密）；**导出失败应先 `compile` 该块再导出**，勿当损坏。
> 2. `unlock-block`：`Unprotect` 仅对代码块 FC/FB/OB 提供；**背景/全局 DB `GetService<PlcBlockProtectionProvider>` 返回 null→"服务不可用",密码不被测试**；受保护 DB 只能博图手动取消。
> ⚠️ 仅改了源码描述串；**新描述需重编 `bin\Release\net48\TiaMcp.exe` 并重启 MCP 服务器才生效**（重编 SHA256 变→真连 TIA 会重弹一次 Openness 授权）。

> **2026-06-04 关键修复 · CJK 标识符乱码（import-scl/import-udt）**：实跑发现导入含中文块名/常量/DB 引用的 SCL 后，块名/标识符变乱码（如 `速度转换测试`→`閫熷害杞崲娴嬭瘯`），未覆盖原块反而新建乱码块。
> - **根因**：`IoUtil.WriteTempPlaintextVerified` 注释声称"UTF-8 带 BOM"，但实现用 `new UTF8Encoding(true).GetBytes()` + `WriteAllBytes`——`Encoding.GetBytes()` **永不含 BOM**（只有 `GetPreamble()`/`StreamWriter` 才写 BOM）。结果 TEMP 中转的 `.scl/.udt` 无 BOM，TIA 在中文 Windows(ANSI 码页 936/GBK)上按 GBK 回读 → UTF-8 字节被误解码成乱码。
> - **波及**：所有走外部源文本路径的写工具——`import-scl`、`import-udt`（中文成员名同样会废）。XML 路径(import-xml/HMI 各 import)靠 `encoding` 声明本不受影响。
> - **修复**：在 `WriteTempPlaintextVerified` 显式拼上 `GetPreamble()`（EF BB BF），含 BOM 的完整字节再交字节级回读校验。一处修复覆盖全部文本/ XML 中转。XML 加 BOM 无害（TIA 自己导出的 XML 也带 BOM）。
> - **遗留**：本次 bug 已在 TIA 内存里产生过一个乱码垃圾块（真实存储名比显示名多一个 GBK 不可显字符 → 按显示名 delete/block-info 命中不了）；尚未 `project-save`，磁盘未受影响，**在博图里手动删除该块即可**，或重启 TIA 丢弃未保存改动。

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

## 已实现命令（59）
- **读取**：`list` `read-tags` `read-udts` `export-source` `export-xml` `export-udt` **`block-info`🆕** `hmi-probe`
- **工程/硬件/库**🆕：`project-info` `device-list` `device-info [设备名]` `device-modules [设备名]` `device-network [设备名]`(子网/IoSystem/IP) `library-list`（项目库类型/母版 + 全局库枚举）（纯只读；device-modules 展开本地机架模块 + 分布式IO站模块树）
- **HMI 读**：`hmi-list` `hmi-read-tags`(--table/--filter) `hmi-read-screens` `hmi-read-screen <名>` **`hmi-read-screen-layout <名>`🆕** `hmi-read-templates` `hmi-read-connections` `hmi-export-all <目录>`(含模板)
- **HMI 变量使用分析**🆕：**`hmi-find-unused-tags`**(声明变量 vs 画面/模板实际引用→列孤儿/HMI死代码候选) **`hmi-tag-usage <变量>`**(单变量反查被哪些画面/模板/控件引用,对标 PLC where-used)。扫画面+模板,**不扫报警/调度器/多路复用→未引用≠可安全删,删前博图复核**
- **HMI 写**：`hmi-write-tags` `hmi-delete-tags` `hmi-export-screen` `hmi-import-screen` `hmi-delete-screen`（全带 `--dry-run`）
- **HMI 模板(母版)**🆕：`hmi-export-template <名> <目录>` `hmi-import-template <xml>[--dry-run]` `hmi-delete-template <名>[--dry-run]`
- **HMI 列表**🆕：`hmi-import-list <xml>[--text|--graphic][--dry-run]` `hmi-delete-list <名>[--text|--graphic][--dry-run]`（读/导出已在 hmi-export-all）
- **排查**：`where-used` `block-deps` `find-unused` `call-tree` `callers-tree` **`crossref-report`🆕**(全项目交叉引用Markdown报表)
- **写入/重构**：`import-scl` `import-xml` `import-udt` `write-tags` **`delete-tags`/`edit-tags`🆕**(删/改PLC变量) `delete-block`(`--dry-run`/`--force`+删前查引用) **`rename-block`🆕**(引用影响告警) **`set-block-number`🆕**(`--dry-run`)
- **know-how 保护**🆕：**`unlock-block [块,逗号分隔] --password<pwd>[--dry-run]`**(移除保护;省略块名=对全部受保护块逐个试该密码,密码不符跳过) **`lock-block <块,逗号分隔> --password<pwd>[--dry-run]`**(设置保护)。官方 `PlcBlockProtectionProvider.Unprotect/Protect`;密码绝不落盘(MCP 仅记 argv[0]+参数个数)
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
### 2d 变量使用分析（spec/计划 2026-06-04-hmi-tag-usage-*，分支 feat/hmi-tag-usage）— 编译通过，**实跑待用户在 TIA 机器复核**
- [ ] `hmi-find-unused-tags`：声明变量全集(解析变量表XML) vs 画面+模板实际引用(`BuildTagUsageIndex` 一次性导出全部画面/模板→`CollectTagRefs` 收集全部含"Tag"+`<Name>`引用并归属最近 `Hmi.Screen.*` 祖先对象)→列孤儿。摘要打头+孤儿清单内联，全量"变量→引用画面"映射 >8KB 写 %TEMP%。
- [ ] `hmi-tag-usage <变量>`：单变量反查引用它的(画面/模板,控件类型,控件名)，回显绑定的 PLC 变量，给在用/孤儿状态；变量找不到提示用 hmi-read-tags 查准名。
- **解决的体验缺口**：`hmi-read-tags` 只到变量表层(建了没摆上画面的孤儿照样列)；这两条从**画面层**下结论，便于查 HMI 死代码/重构前查影响面。
- **诚实边界(写进工具说明+输出)**：经典 `HmiTarget` 不暴露报警/调度器/多路复用消费者→「未被画面引用」≠「可安全删」，孤儿列表必带 ⚠ 删前博图确认。检测口径=结构化 XML 解析(非文本子串扫描,避免中文文本标签误判成"在用")。
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

### MCP 工具说明审计 + 加固（2026-06-04，针对"零上下文 AI 自主选参填参"）
- **背景**：MCP 在新会话只注入工具 `name`/`description`/`inputSchema`（非 docs），AI 选对工具/填对参全靠这三者。原 `Schema()` 把**每个参数描述自动=参数名**（`blockName` 的描述就是 "blockName"），是准确率最大短板。
- **P0 结构性**：`ToolDef` 加 `Dictionary<string,string> P`（参数/旗标名→描述），`Schema()` 经新 `PDesc()` 优先取 P、缺省回退；逐工具补齐 44/54 的参数与旗标说明（`dry-run`/`force`/`text`/`graphic`/`no-screen-export` 全部给出语义）。
- **修一个 bug**：原 ValueFlags 默认描述"（可选筛选子串）"把 `hmi-read-tags` 的 `table`（精确表名选择器）误标为子串筛选——改默认为中性"（可选）"，并给 `table`/`filter` 各自精确描述。
- **P1 反幻觉边界写死**（CLAUDE.md 要求，原 0/54 合规）：`read-tags`(无在线实际值/不读DB成员当前值)、`compile`/`compile-device`(不下载/不联机/不读诊断缓冲)、`device-network`(组态IP非在线)。
- **P1 覆盖警告**：`import-scl`/`import-xml`/`import-udt` 说明写明"覆盖同名块且无 dry-run,先 export 备份"（与功能对等的 HMI import 都有 dry-run 不一致，描述层先补上;补 PLC import 真 dry-run 列为后续）。
- **P2 重叠区分轴**：where-used(单跳/任意符号)↔callers-tree(递归/仅块)、block-deps↔call-tree、device-list(全项目一览)↔device-info(单设备)、`list` 点名 device-list/hmi-list、`outDir` 全部说明"内容已随结果返回,落盘明文可能被加密"、`import-project-texts.xlsxPath` 标注"唯一接磁盘路径的写工具"。
- **验证**：`build.ps1` 0 错 0 警；自检 initialize+tools/list 仍 54 工具，抽查 export-source.outDir / hmi-read-tags.table / delete-block.force / read-tags.desc 新描述均已生效（无需 TIA）。**重编后 SHA256 变,下次真连 TIA 会重弹一次 Openness 授权框。**

## 待补（后续）
- [ ] MCP v2：大输出摘要+artifacts 信封（附录A）；`--device` 多设备选择下沉 TiaSession
- [ ] D 类在线/下载（需安全策略授权后再做）；compare-software（多PLC/有库时）

## 关键决策（速记）
- 框架 net48 + x64；MCP 层手写 stdio JSON-RPC（不引官方 SDK）；JSON 用 Newtonsoft（打包时引）。
- **E-SafeNet**：所有 TIA 文件 I/O 经 `%TEMP%` 中转 + 明文校验 + 即用即删；不信任持久化明文。
- **图形块**：不精修 SimaticML，只整块替换(import-xml)或重写为 SCL；**CEM 不做**。
- **know-how 保护块**：工具自动识别并单列；**博图里双击+输密码=临时打开≠取消保护,Openness 仍读不到**（IsKnowHowProtected 不变）。要读须真正移除保护：`unlock-block`(Openness Unprotect,需密码) 或博图取消保护；读完用 `lock-block`(Protect) 复原。不做自动解密(密码每次显式传、绝不存储)。

## 已知受保护块（目标工程，读不了内部）
PID_S、PID_Control、PN_SEW_Speed_Control、PN_SEW_Speed_Control_1、PID_Control_DB。
（现可用 `unlock-block <块> --password <pwd>` 移除保护后读取——前提是有该块密码；PN_SEW_*/PID_* 若为厂商库块无密码则 Unprotect 报"密码不符"，仍读不了。）
