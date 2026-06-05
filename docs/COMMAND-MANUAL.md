# TIA-MCP 指令集手册

> 面向人工审查与即将到来的 MCP 打包。覆盖当前 **32 条命令**（PLC 读/写/排查/编译 + 工程 + HMI 读/写）。
> 每条给出：**用途 / 输入 / 输出示例 / 退出码 / MCP 备注**。
> 配套文档：审计与路线图见 [`AUDIT-2026-06-04.md`](AUDIT-2026-06-04.md)；技术约束见仓库根 `CLAUDE.md`；进度见 `PROGRESS.md`。
> 最近更新：2026-06-04（命令审计 + 修复 + 新增 `project-save`）。

---

## 0. 通用约定（看命令前先读）

| 项 | 约定 |
|---|---|
| **运行前提** | 先打开 TIA V18 并打开一个项目；工具 attach 运行中的实例。重编译后首次连接会弹 TIA 授权框（按 exe 的 SHA256 记白名单），到 TIA 窗口点"是"。 |
| **连接诊断** | 每条命令连接时往 **stderr** 打 `[连接] PID=.. 项目=.. 路径=..`，多开 TIA 时据此确认连对了工程。 |
| **路径参数** | 一律用**正斜杠**（`D:/tmp/x`）或双引号。`!` 前缀走 bash，裸反斜杠会被吃掉。 |
| **明文/加密** | 本机 E-SafeNet 透明加密：所有 TIA 文件 I/O 经 `%TEMP%` 中转 + 明文校验 + 即用即删。**落盘明文副本不可信任**（全盘扫描会稍后加密它）——交付物是 stdout 文本，落盘只为临时给 Git。 |
| **受保护块** | know-how 保护块读不了内部逻辑，工具自动识别并单列、不计入分析。需手动在博图解锁。 |
| **多 PLC/HMI** | 当前写命令默认作用于**第一个** PLC/HMI（多设备时打印提示）。显式选设备的 `--device` 见路线图。 |
| **stdout 纪律** | 结果走 stdout，诊断/进度/`[连接]` 走 stderr，日志走文件（`bin/Release/net48/logs/`）。MCP 阶段 stdout 被 JSON-RPC 独占。 |

### 退出码（当前 CLI 语义，MCP 阶段将改为结构化 errorCode，见附录 A）

| 码 | 含义（重载，待 MCP 收敛） |
|---|---|
| 0 | 成功（注意：多数读命令即使无命中也返回 0） |
| 1 | 用法错误 / 找不到目标 / 无 PLC / dry-run 跳过 |
| 2 | 编译有错误 / 导出失败 / 导入失败 / 被引用拒删 |
| 3 | 输入文件是密文（被 E-SafeNet 加密） |

---

## 1. PLC 读取

### `list`
- **用途**：列出所有 PLC 及其全部块（类型/名称/语言/路径/受保护标记）。工程总览入口。
- **输入**：无。
- **输出示例**：
  ```
  当前项目: 站点_1

  ==== PLC: PLC_1 ====
    [OB         ] Main                             语言=LAD    路径=Program blocks
    [FB         ] FB_Motor                         语言=SCL    路径=Program blocks/电机
    [FB         ] PID_Control                      语言=LAD   🔒 路径=Program blocks
    [GlobalDB   ] DB_Demo                 语言=DB     路径=Program blocks

  共 118 个块。
  ```
- **退出码**：0。
- **MCP 备注**：118 块逐行约 10–20KB，**污染中等**。MCP 化应返回结构化数组（type/name/lang/protected/path），由 AI 自渲染；中文宽字符让 `{,-N}` 对齐错位，别依赖对齐解析。

### `read-tags`
- **用途**：列出 PLC 变量表与变量（名称/类型/地址）。
- **输入**：无。
- **输出示例**：
  ```
  ==== PLC: PLC_1 · 变量表 ====
    [表] DI
        Start_Button                     Bool             @ %I0.0         // 启动按钮
        Stop_Button                      Bool             @ %I0.1         // 停止按钮
  -- 本 PLC 共 124 个变量 --
  ```
- **退出码**：0。
- **MCP 备注**：**污染中等**（数百~上千行）。建议加 `--table`/`--filter`（照搬 hmi-read-tags），无过滤只回计数 + 表名。**[已优化] 现读中文注释**（`tag.Comment` 多语言文本）追加在 `//` 后——AI 排查最有价值的语义。

### `read-udts`
- **用途**：列出所有 UDT（自定义数据类型）名字。
- **输入**：无。
- **输出示例**：
  ```
  ==== PLC: PLC_1 · UDT 自定义类型 ====
    [UDT] MyStruct
    [UDT] 通用/MotorParam
  -- 本 PLC 共 6 个 UDT --

  提示：要看某个 UDT 的完整成员定义，下一步可加 export-udt ...
  ```
- **退出码**：0。
- **MCP 备注**：只给名字，体积小。看成员定义用 `export-udt`。

### `export-source <块名> [目录]`
- **用途**：导出 **SCL/STL** 块源码文本（文本即交付物）。
- **输入**：块名（必填）；可选输出目录（另存明文副本给 Git）。
- **输出示例**：
  ```
  ==== FB_Motor (SCL) 源码开始 ====
  FUNCTION_BLOCK "FB_Motor"
  VAR_INPUT
      Enable : Bool;
  END_VAR
  BEGIN
      ...
  END_FUNCTION_BLOCK
  ==== 源码结束（1247 字符）====
  [已存明文副本] D:/out/FB_Motor.scl（注意：本机全盘扫描可能稍后将其加密，请尽快提交 Git）
  ```
- **退出码**：0 成功；1 找不到块；2 块是图形语言（提示改用 `export-xml`）。
- **导出失败排查（实测 2026-06-04）**：块若 `IsConsistent=False`（被改过但未重新编译），`Export` 会抛通用错 `Error when calling method 'Export' of type '...'`——**这是"未编译"，不是加密**（`受保护=False` 时即此因）。判别：`block-info` 看"一致(IsConsistent)"。对策：先对该块 `compile`（0 错即转 True），再导出即可。**AGENT 遇此错应先 compile 重试，勿当作块损坏/工程报废。**
- **MCP 备注**：**污染大户**（大块上万行）。MCP 化应回 `{lineCount,charCount,sha256,artifactPath}` + 可选 `head` 片段，全文写 `%TEMP%` 回传而非塞进 result。

### `export-xml <块名> [目录]`
- **用途**：把**任意块**（含图形 LAD/FBD/GRAPH、DB）导出为 SimaticML XML。
- **输入**：块名（必填）；可选输出目录。
- **输出示例**：
  ```
  ==== FB_Pump SimaticML 开始 ====
  <?xml version="1.0" encoding="utf-8"?>
  <Document>
    <SW.Blocks.FB ID="0"> ... 整块 XML ... </SW.Blocks.FB>
  </Document>
  ==== 结束（86214 字符）====
  ```
- **退出码**：0 成功；1 找不到块；2 导出失败。
- **导出失败排查（实测 2026-06-04）**：与 `export-source` 同源——块 `IsConsistent=False`（改过未重编）时 `Export` 抛 `Error when calling method 'Export' of type '...SW.Blocks.FC'`，**是未编译非加密**。先 `compile` 该块再导出。实例：`PN_SEW_Speed_Control`/`_1` 两个 LAD FC 改后未编译→导出失败；`compile` 0 错→`IsConsistent=True`→交叉引用扫描从 115 块升到 117、导出失败清零。交叉引用套件（`where-used`/`call-tree` 等）内部也走 `Export`，故其诊断里的"导出失败：X"同理——先 compile 那些块再重跑。
- **MCP 备注**：**污染最高**（图形块 3 万~15 万字符）。MCP 化必须只回解析摘要（网络数/CallInfo 调用/Access 符号）+ `artifactPath`，全文不进 result。也用于读 DB 结构（DB 是 PlcBlock，无语言门槛）。

### `export-udt <UDT名> [目录]`
- **用途**：导出一个 UDT 的完整成员定义文本（`.udt`）。
- **输入**：UDT 名（必填）；可选目录。
- **输出示例**：
  ```
  ==== UDT MyStruct 定义 ====
  TYPE "MyStruct"
  STRUCT
      SOC : Real;
      Voltage : Real;
  END_STRUCT
  END_TYPE
  [已存] D:/out（全盘扫描可能稍后加密）
  ```
- **退出码**：0 成功；1 找不到；2 导出失败。
- **MCP 备注**：中等体积，建议回成员列表 + artifactPath。

### `block-info <块名>` 🆕
- **用途**：读单块元数据（块号/语言/存储布局/一致性/保护/作者/版本/创建修改编译日期；InstanceDB 额外给所属 FB）。纯只读。
- **输出示例**：
  ```
  ==== 块信息: FB_Example_DB ====
    类型: InstanceDB   语言: DB
    块号(Number): 36   自动编号: True   存储布局: Standard
    一致(IsConsistent): True   受保护: False   写保护: ?
    作者/族/标题/版本:  /  /  / 0.1
    创建/修改/编译: 2022/11/2 ... / 2022/11/2 ... / 2024/11/18 ...
    所属FB(InstanceOfName): FB_Example
  ```
- **退出码**：0 成功；1 找不到块。
- **MCP 备注**：所有动态属性 GetAttribute+try/catch，不支持的属性显示 `?`（如本机 `IsWriteProtected`）。

---

## 1B. 工程 / 硬件信息（🆕，纯只读，无文件 I/O）

### `project-info`
- **用途**：项目级元信息（名称/路径/作者/版本/创建与修改时间/注释/是否有未保存改动）。连接后第一手上下文。
- **输出示例**：
  ```
  ==== 项目信息 ====
    名称: Demo_Project
    路径: D:\...\xxx.ap18
    作者: Engineer
    创建时间: 2022/5/10 8:46:38
    最后修改: 2026/6/4 0:12:20  by Engineer
    版本:
    注释:
    有未保存改动(IsModified): True
  ```
- **退出码**：0。注释走 `MultilingualText` 取首语言项文本。

### `device-list`
- **用途**：项目内所有设备 + 类型(PLC/HMI/其他) + 型号 + 订货号 + GSD 标记。**分布式 IO 站点也会作为 Device 列出（带 (GSD/分布式IO)）**。
- **输出示例**：
  ```
  ==== 项目设备清单: Demo_Project... ====
    [PLC ] S7-1200 station_1            型号=S7-1200 Station 订货号=?
    [HMI ] HMI_1                        型号=? 订货号=?
  -- 共 2 个设备 --
  ```
- **退出码**：0。说明：顶层 Device 的订货号常为 `?`（订货号在 CPU/head **模块**上，见 device-info/device-modules）。

### `device-info [设备名]`
- **用途**：设备型号/订货号/作者/注释 + CPU/head 与各模块的订货号/固件。省略设备名=全部设备。
- **输出示例**：
  ```
  ==== 设备: S7-1200 station_1 (PLC) ====
    顶层  型号=S7-1200 Station  订货号=?  作者=Engineer  注释=
      模块 PLC_1            分类=CPU  订货号=6ES7 214-1AG40-0XB0  固件=V4.6
      模块 CM CANopen_1     分类=None 订货号=021620-B  固件=V1.0
      模块 DI 16/DQ 16...   分类=None 订货号=6ES7 223-1BL32-0XB0  固件=V2.0
  ```
- **退出码**：0 找到；1 指定设备名未找到。

### `device-modules [设备名]`
- **用途**：机架/槽位/模块树（递归 DeviceItem）。**覆盖本地中央机架模块 + 分布式 IO 站点的模块**。省略设备名=全部设备。
- **输出示例**：
  ```
  ==== S7-1200 station_1 (PLC) 模块树 ====
    [槽0] Rack_0  <System:Rack.S71200>  订货号=Rack  固件=V1.0
    [槽1] PLC_1  <OrderNumber:6ES7 214-1AG40-0XB0/V4.6>  订货号=...  固件=V4.6
      [槽1] DI 14/DQ 10_1
      [槽32768] PROFINET 接口_1
        [槽32769] 端口_1
    [槽102] CM CANopen_1  订货号=021620-B  固件=V1.0
    [槽2] DI 16/DQ 16x24VDC SINK_1  订货号=6ES7 223-1BL32-0XB0  固件=V2.0
  ```
- **退出码**：0。
- **MCP 备注**：模块树中等体积；MCP 化建议回嵌套 JSON（设备→机架→模块→子模块）。

### `device-network [设备名]` 🆕
- **用途**：子网/IO 系统拓扑 + 各设备网络接口节点地址（IP/PROFINET），分布式 IO 网络排查。
- **输出示例**：
  ```
  ==== 子网 / IO 系统 ====
    子网 PN/IE_1  类型=System:Subnet.Ethernet
      IO系统 PROFINET IO-System  号=100
  ==== S7-1200 station_1 网络接口 ====
    PROFINET 接口_1: 地址=192.168.0.10  类型=Ethernet
  ```
- **退出码**：0。

### `library-list` 🆕
- **用途**：项目库（类型 Types + 母版副本 MasterCopies）+ 全局库枚举（名/路径/类型/是否打开）。盘点可复用资产。
- **输出示例**：
  ```
  ==== 项目库 ProjectLibrary ====
    -- 类型 Types: 0 --
    -- 母版副本 MasterCopies: 0 --
  ==== 全局库 GlobalLibraries（已打开/已知）====
    Buttons-and-Switches  路径=...as18  类型=System  已打开=False
  ```
- **退出码**：0。打开磁盘全局库（.alXX）不在此命令范围（需 Open/Close 生命周期）。

---

## 2. PLC 排查（交叉引用套件）

> 共性：Openness V18 **无原生交叉引用 API**（已全文核实，见 AUDIT），故为**内容扫描式**——把每块导出文本（SCL 源/图形块 XML）经 `%TEMP%` 中转，SCL 正则匹配带引号名字、XML 解析 `<Access>`/`<CallInfo>`。**非语义级**，受保护块单列不计入。每条结尾有 `[诊断]` footer。

### `where-used <符号名>`
- **用途**：谁引用了该符号（块/DB/变量/UDT，支持成员路径 `DB.成员`）。
- **输入**：符号名或带点的成员路径。
- **输出示例**：
  ```
  == "FB_Motor" 的引用者（谁用了它）==
    [OB/LAD] Main                                   3 处
    [FB/SCL] FB_Control                             1 处
  合计 2 个块引用了它。

  [诊断] 精确扫描 113 块；受保护未读 5 个
    受保护(know-how，内部引用未计入)：FB_Protected_1, FB_Protected_2, FB_Protected_3, ...
  ```
- **退出码**：0。
- **MCP 备注**：输出结构清晰但 `[诊断]` footer 应剥到 `diagnostics` 字段。**已知局限**：仅经 SCL/iDB 调用的 FB，其 FB 名不出现在调用方文本中，`where-used <FB>` 可能漏 SCL 调用方（find-unused 已修此误报，见 AUDIT）。

### `block-deps <块名>`
- **用途**：某块依赖了哪些顶层符号（它向下引用谁）。
- **输入**：块名。
- **输出示例**：
  ```
  == FB_Control 依赖的符号（7 个）==
    DB_Demo
    FB_Motor
    Motor_iDB
  ```
- **退出码**：0 成功；1 找不到；2 导出失败。受保护块直接提示未知。
- **MCP 备注**：只导单块，开销小。

### `find-unused`
- **用途**：找没有任何块引用的块/DB（死代码候选）。
- **输入**：无。
- **输出示例**：
  ```
  == 死代码候选（项目内没有任何块引用它）==
  -- 未被引用的代码块 FB/FC：2 个 --
    FB_Old (FB)
  -- 未被引用的 DB：1 个 --
    Temp_DB (GlobalDB)

  ⚠ 仅按 PLC 块/DB 间的引用判断；HMI/外部/间接调用、以及受保护块的内部引用未计入，删除前务必人工复核。
  [诊断] 精确扫描 113 块；受保护未读 5 个 ...
  ```
- **退出码**：0。
- **MCP 备注**：**[已修复]** 现会把"引用了背景DB"传播为"也引用了其 FB"（`InstanceOfName`），修掉"仅经 SCL/iDB 调用的 FB 被误报死代码"。仍须人工复核后再删。

### `call-tree <块名>` / `callers-tree <块名>`
- **用途**：`call-tree` 向下展开它调用的 FB/FC；`callers-tree` 向上递归谁调用它（影响分析）。
- **输入**：块名。
- **输出示例**：
  ```
  == Main 调用树（向下，只展开 FB/FC）==
  Main (OB)
    FB_Control (FB)
      FB_Motor (FB)
  [诊断] ...
  ```
- **退出码**：0 成功；1 找不到块。
- **MCP 备注**：缩进树"难解析"而非"体积大"；MCP 化建议回嵌套 JSON。同会话连续多条交叉引用会重复全量导出（性能），MCP 长驻进程应缓存索引（见 AUDIT）。

### `crossref-report` 🆕
- **用途**：全项目交叉引用报表（每块的依赖 + 被调用者），Markdown 表，内存交付可重定向给 Git/审计。
- **输出示例**：
  ```
  # 交叉引用报表（内容扫描，非语义级）
  扫描 113 块；受保护未读 5。
  | 块 | 类型 | 依赖(向下引用) | 被调用者(向上) |
  |---|---|---|---|
  | FC_Example | FC | Sig_A Sig_B EStop ... | FB_Caller |
  ```
- **退出码**：0。
- **MCP 备注**：体积可大（113 块×依赖），MCP 化走 artifact 回传。

---

## 3. PLC 写入

> 共性：输入文件先校验**非密文**（密文返回 3）。MCP 阶段 AI 可直接传 inline 文本（`sclText`/`xmlText`/`udtText`/`listText`，工具内部经 `%TEMP%` 中转），**也可传磁盘路径**（伴生参 `sclPath`/`xmlPath`/`udtPath`/`listPath`）。**二选一；都给则路径优先并提示；都不给报错。大文件（尤其画面 XML、整块 XML）务必用 `<名>Path` 传路径——内联会让 AI 逐字复现整份文本撞 token 上限而失败**。改完**不自动保存**——需显式 `project-save` 落盘。
>
> **闭环范式（export→编辑→import by path，推荐大产物用）**：
> ```
> hmi-export-screen 启动画面 D:/tmp        # 出 D:/tmp/启动画面.xml（明文）
> # 编辑该 XML
> hmi-import-screen xmlPath=D:/tmp/启动画面.xml dry-run=true   # 先看目标判定
> hmi-import-screen xmlPath=D:/tmp/启动画面.xml               # 实写
> ```

### `import-scl <scl文件路径>`
- **用途**：导入 SCL 明文 → 生成块 → 自动编译新增块并结构化报错。
- **输入**：SCL 文件路径（明文）。
- **输出示例**：
  ```
  目标 PLC: PLC_1
  创建外部源 mcp_import_153045 <- (TEMP 明文中转, 已校验)
  从源生成块 ...
  新增块: FB_New

  编译 FB_New ...
  编译状态: Success  错误: 0  警告: 1
    [Warning] 变量 X 未使用  (FB_New)
  ```
- **退出码**：0 无错；2 编译有错/生成失败；3 输入密文；1 文件不存在/无 PLC。
- **MCP 备注**：覆盖同名既有块时仅提示"可能覆盖"，**无 dry-run/确认**（见 AUDIT 增强项）。

### `import-xml <SimaticML文件>`
- **用途**：把（AI 改好的）SimaticML 整块导入，Override 覆盖同名块（图形块的"写"）。
- **输入**：SimaticML XML 文件（明文）。
- **输出示例**：
  ```
  目标 PLC: PLC_1
  导入 SimaticML（Override 覆盖同名块）...
  覆盖了已有同名块（无新增）。

  提示：图形块整块替换后，建议对该块及其调用方做一次编译核对。
  ```
- **退出码**：0 成功；2 导入失败；3 密文。
- **MCP 备注**：SimaticML 对 schema 极敏感；建议先 `export-xml` 拿骨架再改，复杂逻辑优先重写为 SCL。

### `import-udt <.udt文件>`
- **用途**：从明文 `.udt` 生成/覆盖 UDT，并编译 PLC 核对。
- **输入**：`.udt` 文件（明文）。
- **输出示例**：
  ```
  目标 PLC: PLC_1
  已从源生成/覆盖 UDT。
  编译该 PLC 核对 ...
  编译状态: Success  错误: 0  警告: 0
  ```
- **退出码**：0 / 2 / 3。

### `write-tags <清单文件>`
- **用途**：批量建 PLC 变量。行格式 `表名|变量名|类型|地址`，`#` 注释。
- **输入**：清单文件（明文）。**[已修复]** 地址列**可空**=PLC 内部符号变量（不再强制地址）。
- **输出示例**：
  ```
  目标 PLC: PLC_1
    + 默认变量表/Flag_Internal : Bool（无地址/符号变量）
    + IO表/Start : Bool @ %I0.0
  完成：新建 2，失败 0。
  ```
- **退出码**：0 全成功；2 有失败；3 密文。
- **MCP 备注**：MCP 化把管道行换成结构化数组 `items:[{table,name,type,address?}]`。删变量用 `delete-tags`；改变量(upsert)见 AUDIT 路线图。

### `delete-tags <清单文件> [--dry-run]` 🆕
- **用途**：按清单删 PLC 变量（补齐 PLC 变量 CRUD 的"删"）。
- **输入**：清单行 `表名|变量名` 或 `变量名`（省略表名=全表搜）。
- **输出示例**：
  ```
  目标 PLC: S7-1200 station_1  [DRY-RUN 预演，不删除]
  警告: 删除被程序/HMI 引用的变量会破坏引用方；建议先 where-used 核对。
    [删] Tag_Example
  汇总: 删 1 / 跳 0（预演，未删除）
  ```
- **退出码**：0；3 清单密文；1 清单无/无 PLC。
- **MCP 备注**：找不到=`[跳]`、删除失败=`[错]`，逐条非致命。

### `delete-block <块名> [--dry-run] [--force]`
- **用途**：删除一个块（破坏性，重构清理用）。**[已增强]** 删前自动查引用 + know-how 检查。
- **输入**：块名；`--dry-run` 只预演；`--force` 被引用时仍强删。
- **输出示例**：
  ```
  [警告] FB_Old 仍被 2 个块引用：Main, FB_Control
  拒绝删除（仍被引用）。确认无误后加 --force 强删，或先解除引用。
  ```
  dry-run / 无引用：
  ```
  [DRY-RUN] 将删除 FB_Spare (FC)。未删除。
  ```
- **退出码**：0 删除/预演；1 找不到；2 被引用拒删（无 --force）/删除失败。
- **MCP 备注**：删前引用检查会触发一次全块扫描（开销可接受，删除低频）。

### `rename-block <旧块名> <新块名> [--dry-run]` 🆕
- **用途**：改块名（`SetAttribute("Name")`）。**Openness 改名不会更新引用**——改后引用方出现未解析引用，须重编译/手动修复。
- **输出示例**：
  ```
  重命名 FB_Login -> FB_Login_X  [DRY-RUN 预演]
  [警告] FB_Login 被 1 个块引用：FB_Caller
    注意：Openness 改名【不会】自动更新这些引用——改后须逐一重新编译并手动修复！
  （预演，未改名）
  ```
- **退出码**：0 成功/预演；1 找不到；2 受保护/改名失败（命名违例或重名）。
- **MCP 备注**：默认先 dry-run 看影响面；know-how 块拒绝改名。

---

## 4. 编译 / 工程 / 批量

### `compile <块名>`
- **用途**：编译指定块，结构化返回错误/警告。
- **输出示例**：
  ```
  编译块 FB_Motor ...
  编译状态: Success  错误: 0  警告: 0
  ```
- **退出码**：0 无错；2 有错；1 找不到。
- **MCP 备注**：**[已优化]** `PrintMessages` 现只打印 `Error`/`Warning`、丢 Info/Success 噪声，但**始终递归下钻**（真错误可能在子节点）；退出码逻辑（ErrorCount>0→2）不变。list 的 语言+保护 改 `GetAttributes` 批量读（一次 COM 往返，失败回退）。

### `compile-device`
- **用途**：编译整个 PLC。
- **输出示例**：
  ```
  编译 PLC PLC_1 ...
  编译状态: Success  错误: 0  警告: 3
    [Warning] ...
  ```
- **退出码**：0 / 2。
- **MCP 备注**：多 PLC 全量消息可能很长；超阈值写 `%TEMP%` 回 artifactPath。

### `export-all <输出目录>`
- **用途**：所有块导出为明文（SCL/STL 源码或图形块 XML），给 Git 做快照。
- **输入**：输出目录（必填）。
- **输出示例**：
  ```
  导出完成：113 个块 -> D:\out；跳过(受保护/失败) 5 个。
  注意：本机全盘扫描可能稍后加密这些文件，请尽快 git add/commit。
  ```
- **退出码**：0。
- **MCP 备注**：落盘明文为脆弱产物，导出后尽快 `git add`。

### `project-save` 🆕
- **用途**：把 Openness 所做改动落盘（`project.Save()`）。**写命令闭环的关键**——否则改动只停在 TIA 内存，进程退出/未手动 Ctrl+S 则丢失。
- **输入**：无。
- **输出示例**：
  ```
  已保存项目 站点_1（注意：会保存整个项目，含你在博图里的手改）。
  ```
  或 `项目无未保存改动（IsModified=false），跳过保存。`
- **退出码**：0 成功/无改动；2 保存失败。
- **MCP 备注**：Save() 是 TIA 进程写 `.apXX` 工程包，**不经 %TEMP%、不涉 E-SafeNet 中转**。会保存整个项目（含用户在 UI 的手改），不只本次改动。

### `export-project-texts <输出目录> [语言代码]` / `import-project-texts <xlsx>` 🆕
- **用途**：把全工程**可翻译文本**（块/网络注释 + HMI 文本）导出到 `.xlsx` 批量编辑，改完导回。`批量改注释`的标准做法。语言默认 `zh-CN`。
- **输出示例**：`已导出项目文本 -> ...\ProjectTexts_zh-CN.xlsx（语言 zh-CN）`（实测 ~366KB）。
- **退出码**：0；2 导出/导入失败（语言不在项目中？）。
- **特别说明**：xlsx 是**二进制**，不走明文校验；TIA 写 `%TEMP%`(豁免) 后由本进程拷到目标(未加密)，**全盘扫描可能稍后加密它，尽快编辑**。导入经 `%TEMP%` 中转；导入后建议 `project-save` 落盘。

---

## 5. HMI 读取（经典 HmiTarget，目标=KTP700 Basic）

> 共性：经典 HMI 的 Screen/Tag/Connection 对象**只暴露 Name**，详情全在导出 XML（已确证无被忽略的强类型属性，见 AUDIT）。导出统一 `ExportOptions.WithDefaults`。

### `hmi-probe [--no-screen-export]`
- **用途**：只读探针——设备/画面枚举 + 首张整屏导出试探 + 画面项 XML 频次 + 变量 + 连接 + 能力小结。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 ====
    运行时类型: HmiTarget
    [画面] 共 44 张：MainPicture, Screen_Test, ...
    [画面导出] MainPicture：成功，明文 201279 字符。
    [画面项] XML 元素总数 3262；疑似画面对象(Hmi.Screen.*) 64 个。
    [画面项] 元素类型 Top15：Hmi.Screen.IOField×20, ...
    [变量] 2 张表 / 562 个变量。抽样：Tag_Demo:?; ...
    [连接] Connection_1
  ==== 能力小结 ====
  OK   设备发现：HMI_1 拿到 HmiTarget
  OK   画面导出(MainPicture)：明文 201279 字符
  ```
- **退出码**：0。
- **MCP 备注**：**污染大户**（首张画面 ~20 万字符 + stderr 700 字符 XML 样例）。本是一次性探针，MCP 化应降级/并入 `hmi-list`，只回计数 + 元素类型表，**绝不回画面 XML**。

### `hmi-list`
- **用途**：HMI 设备 + 计数 + 名字地图。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 ====
    画面 44 | 变量表 2 | 变量 562 | 连接 1
    变量表: 默认变量表, Demo_Tags
    画面: MainPicture, Screen_Test, ...
  ```
- **退出码**：0。

### `hmi-read-tags [--table 名] [--filter 子串]`
- **用途**：导出变量表 XML 解析，出 名/Coding(类型)/连接/绑定的 PLC 变量。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 · 变量 ====
    [表] Demo_Tags
        Tag_Demo                            IEEE754Float   conn=Connection_1 ←PLC DB_Demo.SOC
    -- 显示 14 个变量 --
  ```
- **退出码**：0。
- **MCP 备注**：无过滤时 562 变量全打印（**污染**）；建议默认须给 `--table`/`--filter`，否则只回各表计数。

### `hmi-read-screens`
- **用途**：画面文件夹树（只名字；画面号只在导出 XML 里）。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 · 画面树 ====
    MainPicture
    [子文件夹A]
      Screen_Test
  ```
- **退出码**：0。

### `hmi-read-screen <画面名>`
- **用途**：单张画面控件摘要 + 可识别的绑定变量。
- **输出示例**：
  ```
  ==== HMI_1 · 画面 MainPicture ====
    画面对象 20 个:
      IOField                    Field_Demo                 →变量 Tag_Demo
      Button                     Start_Btn
  ```
- **退出码**：0 成功；1 找不到画面。
- **MCP 备注**：解析摘要打 stdout，但底层导出 XML 实测 ~20 万字符；**stderr 还 dump 700 字符样例**。MCP 化只回 `{objectCount,objects[],xmlCharCount,artifactPath}`，删 stderr dump。**最高优先污染项**。**⚠ 建议优先使用 `hmi-read-screen-layout` 获取完整视觉布局+动态行为。**

### `hmi-read-screen-layout <画面名>` 🆕
- **用途**：单画面完整视觉布局 + 动态行为。包含控件位置/大小/颜色/字体/文本/变量绑定/圆角/透明度/重叠检测 + **动画**（范围外观变色/闪烁、可见性控制）+ **事件**（点击跳转/按下动作、画面级事件）。**建议优先使用此命令而非 `hmi-read-screen`。**
- **动画解析**：
  - `RangeAppearanceAnimation`：根据变量值范围改变颜色/闪烁。含触发变量 + 范围值（上下限+背景色+前景色+闪烁类型）
  - `VisibilityAnimation`：根据变量值控制可见性。含触发变量 + 范围（RangeStart~RangeEnd）+ 可见/隐藏
  - `TagConnectionDynamic`：IO 域的动态变量绑定
- **事件解析**：
  - 控件级事件：Click（点击跳转画面）、Press（按下触发动作）等
  - 画面级事件：ClearScreen（清屏）、GenerateScreen（生成画面）等
  - 含函数名+类型+参数（链接值如画面名/变量名、字面值如数值）
- **属性归属准确（2026-06-05 重写）**：每个控件的位置/大小/颜色/字体/文本/动画/事件**只取它自身**（限定在控件直接的 `<AttributeList>` 与自身作用域内解析，遇到嵌套子控件即剪枝），**不会再把子控件的值误算到父容器上**。具体：
  1. **控件总数只数真实控件**：已剔除结构性图层 `ScreenLayer` 和控件子属性 `Property`（ProcessValue，IOField/Bar 的过程值节点）。计数行会标注「不含 N 个图层容器、M 个控件子属性ProcessValue」。
  2. **Group 内控件单列并标注归属**：嵌套在 Group 里的控件照常单列，并加 `∈组 "X"` 标注；父 Group 的动画/事件只统计它自己的，不再 = 自身+所有子控件之和。
  3. **绑定变量 vs 动画触发器分离**：「绑定变量」仅指过程值（ProcessValue）绑定；动画的触发器变量在该控件的「动画」段单列，不再被误报成「绑定变量」。
  4. **字体/文本解析已修正**：字体取自 `Hmi.Globalization.FontItem`（FontFamily/FontSize/FontStyle）；文本取自 `MultilingualText` 正文（去 `<body><p>` 包裹）。
  5. **画面号字段为 `Number`**（旧实现误用 `ScreenNumber` 恒显示 `#?`，已修；模板无画面号仍显示 `#?`）。
  6. **重叠对仅列前 20 对**：背景框正常覆盖其上控件会产生大量「重叠」，超出 20 对的标注「…其余 N 对略」，避免污染上下文。
- **输出示例**（实测 `0010 车体状态`，已与 `hmi-export-screen` 导出 XML 交叉验证一致）：
  ```
  ==== HMI_1 · 画面布局 0010 车体状态 ====
    画面: 0010 车体状态 (#34)
    尺寸: 800 x 480 (DIU)
    背景色: 232, 237, 243

    ── 画面级事件 (2 个) ──
      [ClearScreen] → SetTag(SystemFunction) Tag=Tag_ScreenNumber, Value=0
      [GenerateScreen] → SetTag(SystemFunction) Tag=Tag_ScreenNumber, Value=10

    控件总数: 59（不含 1 个图层容器、12 个控件子属性ProcessValue）

    === 控件摘要 ===
    [5] Circle "圆_1"
        位置=(408,77) 大小=32×32 背景色=217, 217, 217
    ...

    === 详细属性 ===
    [Button] (6 个)
      ── 模板_按钮_8 ──
         位置: (408, 330)
         大小: 112 × 36
         字体: 宋体 16pt [Bold]
         文本: "通讯状态"
         圆角半径: 3
         ── 动画 (1 个) ──
           [RangeAppearanceAnimation]
             触发器: RangeTag → 变量: Tag_ScreenNumber
             范围 [20 ~ 20]:
               背景色: 206, 255, 255
               前景色: 0, 0, 255
         ── 事件 (1 个) ──
           [Click]
             函数: ActivateScreen (SystemFunction)
               参数: Screen name = 0020 通讯状态

    [IOField] (12 个)
      ── I/O 域_13 ──
         位置: (101, 183)
         大小: 49 × 26
         字体: 宋体 12pt [Bold]
         绑定变量: DirAxis_Common_Data_V12_Current_Angle

    === 布局统计 ===
    • 控件类型分布: Button×6, Circle×6, GraphicView×6, IOField×12, Rectangle×18, TextField×11
    • 重叠控件: 75 对（含背景框与其上控件的正常覆盖）
      - 卡片_整车态势 与 图形视图_6
      ...
      …其余 55 对略(可导出 XML 自行核对)
  ```
- **退出码**：0 成功；1 找不到画面。

### `hmi-read-template-layout <模板名>` 🆕
- **用途**：模板画面（母版）完整视觉布局 + 动态行为。与 `hmi-read-screen-layout` **相同的信息和解析精度**，但针对模板画面。**建议优先使用此命令而非 `hmi-read-templates`。**
- **输出格式**：同 `hmi-read-screen-layout`（属性归属同样准确，控件总数同样剔除图层/ProcessValue）。模板无画面号，画面号显示 `#?`。
- **退出码**：0 成功；1 找不到模板。

### `hmi-find-unused-tags` 🆕
- **用途**：HMI **死代码候选**——把声明变量(解析变量表 XML 的全集)与画面+模板里**实际引用**的变量交叉比对，列出「建了但没摆上任何画面/模板」的孤儿。对标 PLC `find-unused`。`hmi-read-tags` 只到变量表层下不了「真在用」的结论，本命令补这一步。
- **检测口径**：结构化 XML 解析——画面/模板导出 XML 里「local-name 含 'Tag' 且带直接子 `<Name>`」的元素即变量引用（非文本子串扫描，避免中文文本标签被误判成「在用」）。扫**画面 + 模板**两类容器。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 · 变量画面使用审计(扫 画面+模板) ====
    声明变量 562 | 在用 540 | 孤儿 22
    扫描范围: 44 画面 + 2 模板
    -- 孤儿变量(未被任何画面/模板引用) --
      [表 BatteryDataBlock] OldTag_X                          ←PLC BatteryDataBlock.OldX
    (共 22 个孤儿)
    ⚠ 边界: "未引用" 仅指画面/模板;报警/调度器/变量多路复用本工具不扫描 —— 未在用 != 可安全删,删前在博图确认。
    [大产物] 全量"变量->引用画面"映射 已写 %TEMP%\TiaMcp_<guid>.txt (charCount=...)
  ```
- **退出码**：0。
- **MCP 备注**：摘要计数 + 孤儿清单内联（可操作部分恒内联）；全量「变量→引用画面」映射 >8KB 写 `%TEMP%` 回路径 + charCount，别灌爆上下文。**孤儿 ≠ 可删**：工具说明须写死「不扫报警/调度器/多路复用」边界，防 AI 误判可安全删。

### `hmi-tag-usage <变量名>` 🆕
- **用途**：单 HMI 变量**反查**——列出引用它的(画面/模板, 控件类型, 控件名)，回显其绑定的 PLC 变量，给出在用/孤儿状态。对标 PLC `where-used`，重构某变量前查影响面。
- **输出示例**：
  ```
  ==== HMI_1 · 变量使用 "BatteryDataBlock_Battery_SOC" ====
    声明于变量表: BatteryDataBlock   (绑定 PLC: BatteryDataBlock.Battery_SOC)
    被 3 处引用:
      画面 0100_信号交互          IOField SOC_Display
      画面 0040_ManualScreen      Bar bar_soc
      模板 template_1             TextField hdr_soc
    状态: 在用
  ```
- **退出码**：0；变量找不到时打提示(用 `hmi-read-tags` 查准名)返回 1。
- **MCP 备注**：单变量输出小、可全内联。变量名精确匹配（大小写不敏感）。同 `hmi-find-unused-tags` 的报警/调度器边界：0 引用 → 孤儿但不等于可删。

### `hmi-read-templates` 🆕
- **用途**：列出 HMI **模板画面(母版**：页眉/页脚/永久区，被普通画面继承)。模板独立于普通画面，存于 `ScreenTemplateFolder`。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 · 模板画面(母版)树 ====
    全局画面
    Template_1
    共 2 个模板。
  ```
- **退出码**：0。详情/控件用 `hmi-export-template` 导 XML。

### `hmi-read-connections`
- **用途**：连接名（经典 Connection 仅暴露 Name）。
- **输出示例**：
  ```
  ==== HMI 设备: HMI_1 · 连接 ====
    Connection_1
    共 1 个连接。（经典 Connection 仅暴露 Name；伙伴/驱动详情见 hmi-export-all 的连接 XML）
  ```
- **退出码**：0。

### `hmi-export-all <输出目录>`
- **用途**：画面 + 变量表 + 连接 + 文本/图形列表 各导 XML 进目录（全快照）。
- **输出示例**：
  ```
  ==== HMI_1: 画面 44 / 变量表 2 / 连接 1 / 列表 5；跳过 0 ====
  导出完成 -> D:\out
  ```
- **退出码**：0。
- **MCP 备注**：集成连接不支持导出会跳过并提示。**注意**：文本/图形列表的读/导出已被本命令覆盖（无需单独的 `hmi-export-lists`）。

---

## 6. HMI 写入（全带 `--dry-run` 预演 + 结构化逐条回调 + 幂等非致命）

> 回调标记：`[建]/[改]/[删]/[跳]/[错]`。建/改走 导出→改XML→Import(Override) 克隆模板；删走 API。

### `hmi-write-tags <清单文件> [--dry-run]`
- **用途**：克隆模板建/改 HMI 符号变量（绑定 PLC 符号）。
- **输入**：清单行 `表名 | 变量名 | 连接 | PLC符号 | [访问模式] | [注释]`。
- **输出示例**：
  ```
  目标 HMI: HMI_1  [DRY-RUN 预演，不写入]
    [建] MCP_Test_HMITag  conn=Connection_1 plc=DB_Demo.TotalVoltage
  汇总: 建 1 / 改 0 / 跳 0 / 错 0（预演，未写入）
  ```
- **退出码**：0 无错；2 有错。
- **MCP 备注**：**[已修复]** ① 变量名含 `.` 或 `\` 在解析阶段即报错（plc 字段含 `.` 仍合法）；② 模板/现有变量**无 PLC 绑定结构时报 `[错]` 而非假成功**（此前会回显成功但实际没绑定）。克隆体 ID 自动重编保证唯一。

### `hmi-delete-tags <清单文件> [--dry-run]`
- **用途**：按清单从 HMI 变量表删变量（API 删，幂等）。
- **输入**：清单行 `[表名 |] 变量名`。
- **输出示例**：
  ```
  目标 HMI: HMI_1
  警告: 删除被画面引用的变量会破坏那些画面；建议先 hmi-export-all 出快照。
    [删] MCP_Test_HMITag
  汇总: 删 1 / 跳 0
  ```
- **退出码**：0。

### `hmi-export-screen <画面名> <输出目录>`
- **用途**：导单屏 XML 供编辑（import-screen 闭环前半）。
- **输出示例**：
  ```
  已导出 Screen_Test -> D:\out\Screen_Test.xml（明文 44931 字符）
  注意: 尽快编辑/用完，本机全盘扫描可能稍后加密它。
  ```
- **退出码**：0 成功；1 找不到画面。
- **MCP 备注**：体积大（数万字符），落盘明文脆弱。

### `hmi-import-screen <画面XML文件> [--dry-run]`
- **用途**：整屏替换/新建（明文校验 + 约束提示）。
- **输出示例**：
  ```
  目标 HMI: HMI_1
    画面 Screen_Test #2 将 覆盖现有。约束: 同设备类型 + 画面宽高匹配 + 画面号唯一。
    [改] Screen_Test #2
  导入完成。建议 hmi-read-screen 复核。
  ```
- **退出码**：0 成功/预演；1 文件无/密文/解析失败/找不到；2 导入失败（多为尺寸/画面号/设备类型不符）。
- **MCP 备注**：往返恒等已实测；整屏替换安全。**画面 XML 常达数十万字符，MCP 调用务必用 `xmlPath` 传 `hmi-export-screen` 的产物路径，勿用 `xmlText` 内联**。

### `hmi-delete-screen <画面名> [--dry-run]`
- **用途**：删除画面（API）。
- **输出示例**：
  ```
  目标 HMI: HMI_1
  警告: 删除被导航/画面切换引用的画面会断链。
    [删] Screen_Test
  已删除。
  ```
- **退出码**：0 成功/预演；1 找不到画面。

### `hmi-export-template <模板名> <输出目录>` 🆕
- **用途**：导出模板(母版) XML 供编辑（import-template 闭环前半）。
- **输出示例**：`已导出模板 Template_1 -> ...\Template_1.xml（明文 111191 字符）`
- **退出码**：0 成功；1 找不到模板。
- **MCP 备注**：模板 XML 体积大（实测 ~11 万字符），同 export-xml 走 artifact 回传 + 摘要。

### `hmi-import-template <模板XML文件> [--dry-run]` 🆕
- **用途**：整模板替换/新建（Override），明文校验 + 约束提示。
- **输出示例**：
  ```
  目标 HMI: HMI_1  [DRY-RUN 预演，不导入]
    模板 Template_1 将 覆盖现有（Override）。约束: 同设备类型 + 尺寸匹配。
  （预演，未导入）
  ```
- **退出码**：0 成功/预演；1 文件无/密文/解析失败；2 导入失败。
- **MCP 备注**：与画面整屏替换同机制（往返恒等）。**改母版即改所有继承它的画面公共区**，影响面大，默认 dry-run 复核。

### `hmi-delete-template <模板名> [--dry-run]` 🆕
- **用途**：删除模板。
- **输出示例**：`目标 HMI: HMI_1 ... [删] Template_1 ... 已删除。`
- **退出码**：0 成功/预演；1 找不到模板。**警告**：删被画面继承的模板会影响所有引用它的画面。

> 注：`hmi-export-all` 现已一并导出模板到 `Templates/` 子目录（汇总行含 `模板 N`）。

---

## 7. 其余命令（batch C，🆕）

### `set-block-number <块名> <编号> [--dry-run]`
- **用途**：改块号（关 `AutoNumber` 再设 `Number`）。号被占用会拒绝；know-how 块拒绝。
- **输出示例**：`块 FB_Ramp 块号 7 -> 999  [DRY-RUN 预演]` / `（预演，未改）`。退出码：0/1/2。

### `unlock-block [块名,逗号分隔] --password <pwd> [--dry-run]` 🆕
- **用途**：移除块的 know-how(专有技术)保护（Openness `PlcBlockProtectionProvider.Unprotect(SecureString)`，手册 §5.11 p567 / §6.4.2.4 p1499）。**解锁后 `export-source`/`export-xml` 才出完整代码**（否则只出公共接口）。
- **关键事实**：博图里"双击块+输密码"只是 GUI 编辑器会话内**临时打开**，`IsKnowHowProtected` 仍为 true，Openness/MCP **仍读不到**——必须本命令或博图"取消保护"才真正移除。
- **DB 解不了（实测 2026-06-04）**：`Unprotect` 服务（`PlcBlockProtectionProvider`）**只对代码块 FC/FB/OB 提供**；背景/全局 DB 的 `GetService<PlcBlockProtectionProvider>()` 返回 **null** → 输出 `[不可用]：服务不可用`，**密码根本没被测试**（结果与密码对错无关）。所以受保护的 DB 用本命令解不了，只能在博图手动取消保护。实例：本项目唯一受保护块 `PID_Control_DB`（实例DB，其母 FB `PID_Control` 反而未保护），传正确密码仍报"不可用"。实例DB损失小——其结构由可读的母 FB 接口决定，没有独立代码逻辑。
- **输入**：块名（逗号分隔多个）**可省略**——省略=对项目内全部受保护块逐个尝试该密码（密码不符的跳过，支持多块不同密码：换密码再调一次）。`--password` 必填（dry-run 预览可不填）。
- **输出示例**：
  ```
  [OK] FB_Secret 已解锁
  [跳过] PN_SEW_Speed_Control：密码不符
  [不可用] FC_Raw：服务不可用（需先编译/非代码块或全局DB/在线/不支持）
  小结：解锁 1 / 密码不符 1 / 已无保护 0 / 不可用 1 / 失败 0
  提示：解锁是内存改动，未落盘。读完用 lock-block 重新加锁；要持久化另调 project-save…
  ```
- **退出码**：0=全成功/无目标；2=有"密码不符"或失败；1=参数错（非 dry-run 缺密码）。
- **MCP 备注**：`blockNames`(可选)/`password`(值参,必填)/`dry-run`(旗标)。密码**绝不**写日志/stderr/临时文件（MCP 仅记 `argv[0]`+参数个数）；MCP 路径 argv 进程内组装，不进 OS 进程列表。

### `lock-block <块名,逗号分隔> --password <pwd> [--dry-run]` 🆕
- **用途**：给块设置 know-how 保护（Openness `Protect(SecureString)`）。配合 `unlock-block` 做"解锁→读→复原"。块名**必填**（只按显式块名，防误锁本无保护的块）。
- **输入**：块名（逗号分隔多个，必填）；`--password` 必填（dry-run 可不填）。
- **输出示例**：`[OK] FB_Secret 已加锁` / `[跳过] FB_X：已受保护` / `小结：加锁 1 / 已受保护 0 / 不可用 0 / 失败 0`。退出码 0/1/2。
- **MCP 备注**：内存改动不落盘，要持久化用 `project-save`（会连同博图手改一并落盘）。密码同样绝不存储。

### `edit-tags <清单文件> [--dry-run]`
- **用途**：改已有 PLC 变量的类型/地址。行 `表名|变量名|新类型|新地址`（空列=不改该项）。
- **输出示例**：`[改] DI/Tag_Example -> 类型=Bool 地址=(不变)` … `汇总: 改 1 / 跳 0`。逐条非致命（`[跳]`/`[错]`）。

### `export-watchtable <输出目录>`
- **用途**：导出所有监控表 + 强制表（**表定义，非运行期活值**）到 XML（经 %TEMP% 明文）。
- **输出示例**：`导出完成：1 个表 -> ...；失败 8（多为'表不一致'/空强制表，需先在博图修复）`。退出码 0。

### `project-archive <输出目录> [归档名]`
- **用途**：归档项目为 `.zapXX`（压缩）。经 %TEMP%(豁免) 再拷出，避免被 E-SafeNet 加密。
- **前提**：Openness 要求项目**已保存**——有未保存改动会被拒绝并提示先 `project-save`（**不替用户静默保存**）。
- **输出示例**：`[拒绝] 项目有未保存改动，Archive 要求先保存。请先运行 project-save…`。退出码 0/2。

### `hmi-import-list <列表XML> [--text|--graphic] [--dry-run]` / `hmi-delete-list <名> [--text|--graphic] [--dry-run]`
- **用途**：导入/删除 HMI 文本列表/图形列表（读/导出已在 `hmi-export-all`）。未指定 `--text/--graphic` 时从 XML 推断类型。
- **输出示例**：`目标 HMI: HMI_1 … [跳] 找不到列表: X` / `[建/改] 文本列表导入完成`。退出码 0/1/2。

> **未实现（已评估，本工程无价值）**：`compare-software`（CompareTo）——本工程单 PLC（无第二台可比）、项目库为空（无库目标）、在线比较属 D 类（需安全授权）。三种比较目标在此工程均不可用，故不纳入；将来扩为多 PLC 或建了项目库再做。

---

## 附录 A：MCP 打包 I/O 约定（建议，包在现有命令之上，不动 Openness 调用）

1. **入参**：一律 JSON named 参数，弃位置参与管道字符串。公共可选参数：`targetDevice`（缺省=第一个，但在 `diagnostics.warnings` 显式记 `defaulted to <name>, N devices present`，**绝不静默**）、`dryRun`（所有破坏性工具必须支持）。文本类输入**双入口**：inline 字符串（`sclText`/`xmlText`/`udtText`/`listText`，工具内部经 `%TEMP%` 校验明文）**或**磁盘路径（伴生参 `<名>Path`，由 `McpServer.PathParamName` 自动派生）。**二选一；都给路径优先 + 前置 `[注意]`；都不给报错。大文件务必走 `<名>Path`**（内联让 AI 逐字复现大文本会撞 token 上限/截断——这是「大文件内联报错」的根因，非 TIA/服务器限制；2026-06-04 修）。
2. **路径**：正斜杠；工具内部产物落 `%TEMP%`，即用即删或回传路径，绝不让 AI 指定持久目录。
3. **dry-run**：`dryRun=true` 只做预检 + 回"将发生什么"，不调任何写 API。
4. **大产物回传**：凡可能 > ~8KB 文本（源码/XML/画面/大消息列表）默认不塞 result，回结构化摘要 + `{charCount,sha256}`；提供可选 `maxChars`/`head` 让调用方索要片段。
5. **信息真实性红线（不可违反）**：摘要只是**默认视图，绝不能是唯一出口**。必须给 AGENT 一条取回**完整、无损、可信**原文的途径——通过工具入参 `full=true`/`maxChars` **inline 返回完整文本**（必要时分块），而**不是**只丢一个可能被 E-SafeNet 再加密、还违反"永不信任持久化明文"的 `%TEMP%` 路径让 AGENT 自己去读。理由：**任何实际修改都是整块导入，AGENT 最终必须生成完整的 XML/SCL**——拿不到完整原文就改不动。`%TEMP%`/`artifacts` 路径只作辅助与落盘 Git，不作唯一可信来源。摘要默认省 token、`full` 按需保真，二者并存。
6. **错误**：统一 `{ok:false, errorCode, message}`，`errorCode` 枚举 `NOT_FOUND/INVALID_INPUT/ENCRYPTED_INPUT/EXPORT_FAILED/IMPORT_FAILED/COMPILE_ERROR/NO_DEVICE/KNOWHOW_PROTECTED`，替代重载 exit code。

**统一返回信封**：
```jsonc
{
  "ok": true,
  "tool": "export-xml",
  "target": { "device": "PLC_1", "defaulted": false, "devicesPresent": 1 },
  "dryRun": false,
  "summary": "导出块 FB_Pump 成功，1247 行，12 个调用，34 个符号引用。",
  "data": { /* 小而结构化：counts / lists / 树 / 解析摘要，绝不含大blob全文 */ },
  "artifacts": [
    { "kind": "xml", "path": "C:/.../Temp/TiaMcp_xxx.xml", "charCount": 48213, "sha256": "…", "encryptedCheck": "plaintext", "ephemeral": true }
  ],
  "diagnostics": { "scanned": 116, "protected": ["FB_Secret"], "warnings": ["multi-PLC present, defaulted to PLC_1"] }
}
```

**上下文污染大户优先改造序**（MCP 化时务必处理）：
`hmi-read-screen` / `export-xml` / `export-source` / `hmi-probe`（均 ≥数万字符）> `export-udt` / `read-tags` / `hmi-read-tags`（无过滤）> `list` / `read-udts`（中等）。

---

## 附录 B：Openness V18 能力边界（**永久不可行**，MCP 工具说明必须写死，避免 AI 幻觉式调用）

| 操作 | 结论 | 依据 |
|---|---|---|
| 读在线变量**实际值** / 监控表活值刷新 | ❌ 不可行 | Openness 在线只读属性、不读过程值 |
| **强制(force)** 变量 / 解除强制 | ❌ 不可行 | 无任何运行时强制 API |
| 读 CPU **诊断缓冲区** | ❌ 不可行 | 仅 `OnlineProvider.State` 给连接态枚举 |
| 读 CPU **运行/停止(RUN/STOP)** 模式 | ❌ 不可行 | `OnlineState` 枚举只有 Offline/Online/Connecting/… 无运行模式 |
| **上传(Upload)** 在线程序到离线项目 | ❌ 不可行 | 有 DownloadProvider，无 Upload API |
| 在线分配 **PROFINET 设备名 / IP** | ❌ 不可行 | Openness 不支持在线写 |
| **模块/通道级** 在线诊断（LED/禁用通道） | ❌ 不可行 | 无通道级诊断 API |
| 精修图形块 SimaticML / CEM | 🚫 不做（策略） | 只整块替换或重写 SCL |
| 自动解密 know-how 保护块 | 🚫 不做（策略） | 手动在博图解锁 |

> 这些与 `download-plc`/`plc-go-online`/`plc-online-state`（**可行**但带现场副作用）要在工具描述里清楚区分。
