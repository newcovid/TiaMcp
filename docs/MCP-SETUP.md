# TIA-MCP 接入文档（MCP stdio 服务器）

把本工具的 **60 条命令**包成一个 **MCP (Model Context Protocol) stdio 服务器**，供 Claude Code / Claude Desktop 等 MCP 客户端用自然语言调用，直接读写运行中的 TIA Portal V18 工程。

> 命令逐条用法+输出示例见 [`COMMAND-MANUAL.md`](COMMAND-MANUAL.md)；审计与设计见 [`AUDIT-2026-06-04.md`](AUDIT-2026-06-04.md)。

---

## 1. 它是什么 / 怎么跑

- **启动**：`TiaMcp.exe mcp`（不带 `mcp` 就是原来的命令行子命令模式）。
- **传输**：stdio 上的 **行分隔 JSON-RPC 2.0**（每条消息一行 JSON，无 Content-Length 帧）。
- **协议版本**：`2024-11-05`。`serverInfo` = `{name: "tia-mcp", version: "1.0.0"}`。
- **JSON 库**：.NET Framework 自带 `System.Web.Script.Serialization.JavaScriptSerializer`（**零 NuGet 依赖**，clone 即可编译）。
- **实现**：每条工具调用 → 组装成 argv → 重定向捕获该命令的 stdout → 调现有 `Program.Dispatch(argv)` → 把捕获文本作为工具结果返回。**不改动 60 条命令本身**。

### 支持的方法
| 方法 | 说明 |
|---|---|
| `initialize` | 返回 protocolVersion / capabilities.tools / serverInfo |
| `notifications/initialized` | 通知，无响应 |
| `tools/list` | 返回 54 个工具的 name / description / inputSchema(JSON Schema) |
| `tools/call` | `{name, arguments}` → 运行 → `{content:[{type:"text",text:...}], isError}` |
| `ping` | 心跳 |

---

## 2. 前提条件（务必满足，否则工具会报错）

1. **先打开 TIA Portal V18 并打开目标项目**——服务器 attach 运行中的实例（不会自己启动 TIA）。
2. **首次连接弹授权框**：每次**重新编译** `TiaMcp.exe`（SHA256 变了）后，第一次 attach 会在 TIA 窗口弹 Openness 授权框，需**手动点"是"**。之后同一 exe 不再弹。**建议在正式接 MCP 前，先手动 `TiaMcp.exe list` 跑一次、点掉授权框**，免得 MCP 客户端的首个调用卡在授权上。
3. **单机单用户、串行**：Openness 是 COM、非线程安全；服务器 `[STAThread]` 串行处理请求，请勿并发压。

---

## 3. 接入 Claude Code

### 方式 A：项目级 `.mcp.json`（推荐，随项目走）
在你要使用的工作目录放一个 `.mcp.json`：
```json
{
  "mcpServers": {
    "tia": {
      "type": "stdio",
      "command": "D:\\path\\to\\TiaMcp\\bin\\Release\\net48\\TiaMcp.exe",
      "args": ["mcp"]
    }
  }
}
```
> 路径里的反斜杠在 JSON 里要写成 `\\`。也可把 exe 拷到无中文/空格的路径以减少转义麻烦。

### 方式 B：CLI 一行添加
```
claude mcp add tia -- "D:\path\to\TiaMcp\bin\Release\net48\TiaMcp.exe" mcp
```
加好后在 Claude Code 里 `/mcp` 可查看连接状态与工具列表；自然语言即可调用（如"列出所有 PLC 块"、"把 FB_Motor 导出成 SCL"、"找死代码"）。

### 方式 C：其它 MCP 客户端（Claude Desktop 等）
同样是 stdio 服务器，配置形如：
```json
{
  "mcpServers": {
    "tia": { "command": "<TiaMcp.exe 全路径>", "args": ["mcp"] }
  }
}
```

---

## 4. 54 个工具（按类）

- **读取**：`list` `read-tags` `read-udts` `export-source` `export-xml` `export-udt` `block-info` `hmi-probe`
- **硬件/库**：`device-list` `device-info` `device-modules` `device-network` `library-list`
- **排查**：`where-used` `block-deps` `find-unused` `call-tree` `callers-tree` `crossref-report`
- **PLC 写/重构**：`import-scl` `import-xml` `import-udt` `write-tags` `delete-tags` `edit-tags` `delete-block` `rename-block` `set-block-number`
- **编译/工程**：`compile` `compile-device` `export-all` `export-watchtable` `project-info` `project-save` `project-archive` `export-project-texts` `import-project-texts`
- **HMI 读**：`hmi-list` `hmi-read-tags` `hmi-read-screens` `hmi-read-screen` `hmi-read-templates` `hmi-read-connections` `hmi-export-all`
- **HMI 变量使用分析**：`hmi-find-unused-tags`（孤儿/死代码候选）`hmi-tag-usage`（单变量反查，对标 where-used）
- **HMI 写**：`hmi-write-tags` `hmi-delete-tags` `hmi-export-screen` `hmi-import-screen` `hmi-delete-screen` `hmi-export-template` `hmi-import-template` `hmi-delete-template` `hmi-import-list` `hmi-delete-list`

### 入参约定（见各工具 inputSchema）
- **文本类写入工具**（import-scl/xml/udt、write/delete/edit-tags、hmi-write/delete-tags、hmi-import-screen/template/list）：接 **inline 文本字段**（`sclText`/`xmlText`/`udtText`/`listText`），服务器内部经 `%TEMP%` 写明文临时文件再传给命令、即用即删。**不要传磁盘路径**。
- **位置参**：如 `blockName`/`oldName`+`newName`/`screenName`+`outDir` 等，按 schema 的 `required`/可选给。
- **布尔旗标**：`dry-run`（破坏性工具都支持，强烈建议先 dry-run）、`force`、`no-screen-export`、`text`/`graphic`。
- **带值旗标**：`hmi-read-tags` 的 `table`/`filter`。
- **输出**：工具结果是该命令的完整 stdout 文本（含末尾 `(退出码 N)`）。`isError` 仅在命令抛异常时为 true（编译报错等"领域错误"不算 isError，体现在文本里）。

---

## 5. 已知限制 / 下一步（v1.0）

- **大输出未做摘要**：`export-xml`/`export-source`/`hmi-read-screen`/`hmi-export-template`/`crossref-report` 等会把**完整文本**塞进工具结果（图形块/画面 XML 可达数万~数十万字符）。v1 优先**信息完整可信**（AI 改块必须拿到完整 XML/SCL 才能整块导回），不做有损摘要。**v2 计划**按 `COMMAND-MANUAL.md` 附录 A 的信封约定：默认回结构化摘要 + `artifacts` 指针，`full=true`/`maxChars` 按需取完整原文（摘要绝不作为唯一出口）。
- **未实现**：`compare-software`（本工程单 PLC+空库无可比目标）；`download-plc`/`plc-go-online`（对物理 PLC 有真实副作用，需先定安全策略，**默认不进 MCP 自动链路**）。
- **Openness 永久不可行边界**（工具不会提供，AI 勿尝试）：在线实际值 / force / 诊断缓冲区 / RUN-STOP / Upload / 在线分配 PN名IP / 通道级诊断（见 `COMMAND-MANUAL.md` 附录 B）。
- **E-SafeNet**：所有 TIA 文件 I/O 经 `%TEMP%` 中转 + 明文校验 + 即用即删；落盘明文（export 类的 [目录] 产物、xlsx、.zap 归档）可能被全盘扫描稍后加密，尽快转存/提交 Git。
- **诊断纪律**：`[连接] PID/项目/路径` 等诊断走 stderr，日志走文件（`bin/Release/net48/logs/`），stdout 被 JSON-RPC 独占。

---

## 6. 快速自检（不接客户端，手动验证服务器）

PowerShell / bash 里把两行 JSON 喂给 `TiaMcp.exe mcp`（initialize + tools/list 不需要 TIA）：
```
printf '%s\n%s\n' \
 '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
 '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
 | "bin/Release/net48/TiaMcp.exe" mcp
```
应回 2 行 JSON：第一行含 `serverInfo`，第二行含 54 个工具。
调一条需要 TIA 的工具（先开好 TIA 并点掉授权框）：
```
printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list","arguments":{}}}' \
 | "bin/Release/net48/TiaMcp.exe" mcp
```
应回一行，`result.content[0].text` 为完整块清单 + `(退出码 0)`，`isError:false`。
