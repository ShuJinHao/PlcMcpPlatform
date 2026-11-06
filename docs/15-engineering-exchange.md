# 第四批：工程交换与图形程序内容编辑

本批为 OpenCode 调用同一个 MCP 服务增加 9 个工具，P4 交付时 TestHost / ValidationHost 共 58 个，P5 当前共 67 个。实现包含应用用例、领域图形规则、原创 PLCopen 编解码器、制品存储、原始 ScriptEngine 调用、候选工程发布保护及回归场景。没有引入第三方 PLC MCP、第二层 MCP 服务或模型编排层。

**当前状态：代码与离线回归；真实 InoProShop / IronPython 与模型生成闭环尚未验证。** P5 已补 OpenCode CLI 连接检查，见 docs/16。 所有汇川能力继续 Unverified，默认生产宿主仍只开放两个查询工具。当前无需 Windows 或 PLC；将来使用独立 ValidationHost 集中验证。

## 实现范围

| 路径 | 已实现内容 | 明确边界 |
|---|---|---|
| PLCopen 交换 | 指定对象/子树导出、导入、冲突策略、报告、保存重开重导出 | TC6 2.01；格式本身不保证厂商属性无损；不自动降级语言或丢弃不支持对象 |
| CODESYS 原生交换 | `export_native/import_native` 的文件调用，私有临时文件、报告与重开核对 | 平台不逆向原生 XML，不声称 CFC/硬件/PDO 已兼容；真实格式由 IDE 解释 |
| LD graph-v1 | 电源轨、触点、线圈、并联输入、取反/边沿/置位复位、块、输入输出变量 | 有界无环网络；完整图形 POU 替换，不是任意厂商梯形图编辑器 |
| FBD graph-v1 | 函数/FB 块、命名引脚、连接、变量、执行顺序 | FB 实例需显式声明；不含 inOut 块引脚、连接标签、反馈环及厂商扩展 |
| SFC graph-v1 | 单初始步、顺序步、内联 ST 转移、跳转、动作引用、N/S/R/P 动作限定符 | 不含选择/并行分支、定时限定符、厂商步骤属性；有独立 ScriptObject 子成员的现存 POU 暂拒绝读写 |
| CFC | 通过原生交换保留不透明内容的调用路径 | 没有结构化 CFC 生成/解码工具；不把 FBD 当作 CFC |

图形 POU 支持 Program / FunctionBlock；Function 返回类型和复杂声明不在 graph-v1 中。变量支持 local/input/output/inOut、基本 IEC 类型、STRING/WSTRING 和命名派生类型；数组、结构声明、地址、常量分组、文档与厂商 addData 等不能表示的内容，解码明确拒绝。已有含 Method/Property/Action 等 ScriptObject 子成员的 POU，真实图形读写返回 `graph_object_has_children`；原生交换仍可用于独立验证。SFC XML 动作内容的模型往返不等于已经证明目标 IDE 的子对象表示可编辑。

## MCP 工具

新增工具返回契约版本 1.3；原有工具版本保持兼容。完整输入/输出见 [MCP 快照](../contracts/mcp-tools.v1.json)，图形参数的独立传输结构见 [graph-v1](../contracts/graph-v1.schema.json)。Schema 之外仍执行领域和 XML 规则。

| 工具 | 作用与结果 |
|---|---|
| `plc_exchange_stage` | 检查 XML 并存为不可变制品，返回 artifactId、XML SHA 和字节数 |
| `plc_exchange_read` | 分块读取制品，不接受任意服务器文件路径 |
| `plc_exchange_validate` | 返回对象、语言、扩展与校验范围；不冒充编译或完整 XSD 校验 |
| `plc_graph_generate` | 从结构化变量、节点、连接和动作生成 PLCopen 制品与语义哈希 |
| `plc_graph_decode` | 从恰好一个 graph-v1 POU 解码；遇到无法表示的数据拒绝编辑 |
| `plc_project_export` | 提交官方 API 导出任务，返回制品、实际选择对象及报告 |
| `plc_project_import` | 将制品导入受控候选工程，明确 reject/replace/skip 策略 |
| `plc_pou_graph_read` | 通过 IDE 导出读取图形模型、对象身份与语义哈希 |
| `plc_pou_graph_write` | 创建或完整替换图形 POU，核对身份、旧哈希和工程修订 |

后四项通过已有持久化任务与单执行槽执行。前五项不启动 IDE，也不宣称工程已修改。导出/图形读取仍提交任务，因此 MCP 注解 ReadOnly=false 不代表它们意图写入工程。

## 修改和往返证明

工程文件仍是事实来源；SQLite 保存工作区、任务和发布证据，XML 制品保存在 DataDirectory/exchange，不能当作另一套可编辑工程数据库。artifactId 为 `SHA256(format + NUL + xml)`，另有原 XML SHA；每次读取重新校验，提交制品不覆盖旧文件。

图形创建：生成制品 → 使用当前 m2 修订提交写入 → 候选副本导入 → 保存/关闭/重开 → 再导出 → 比较图形语义 → 发布新版本。更新必须额外提供读取所得 `objectId + expectedGraphSha256`；在候选上先重新读取原图形，禁止通过更新偷偷改变名称、POU 类型或语言。

语义哈希覆盖声明、表达式、节点身份、块类型/实例、端口、连接、取反、边沿、存储、显式执行顺序、步骤与动作；几何坐标和节点宽高不纳入逻辑比较。节点 localId、节点顺序和引脚顺序保留；若目标 IDE 重新编号或排序，当前实现保守失败，不猜测图同构或声明逻辑等价。布局是否符合 IDE 使用要求是后续单独验收项。

一般 XML 导入的 `reexportVerified=true` 仅证明“导入后的导出内容与保存重开后的导出内容相符”，**不证明源工程全部信息被 XML 无损表达**。返回 `verificationScope=post_import_vs_reopened_export`、`sourceToExportLosslessness=not_proven`。图形写入另有上述源图形到重开结果的模型比较。记录警告、错误、非导出对象、跳过对象和新旧 GUID；声明 skip 仅允许已知名称冲突跳过。原生导出跳过序列化成员、取消、未确认导入对象、部分失败都不能作为成功发布。

replace 可能改变 GUID，后续调用使用返回的新标识。已知任务引用会阻止替换，应先显式解绑，更新后再按新标识绑定；不宣称自动重写所有 ST/设备/符号引用。失败时保留候选、备份、步骤和已取得的厂商报告；当前头不推进。结果未知继续沿用执行槽隔离与证据对账，禁止换幂等键盲目重放。

## 大小和解析限制

- P5 后通用 XML 制品默认 8 MiB、可配置最高 64 MiB；内联 stage 与 graph-v1 XML 仍限制 65,536 个 UTF-8 字节。XML 最深 32 层、最多 100,000 个元素，禁用 DTD 和外部实体。分块上传与私有 Worker 文件传递见 docs/16；这是有界文件传递，IDE 解析仍可能在内存中加载完整 XML。
- 请求最多选择 128 个对象或单独 `["project"]`；内部展开和结果最多 2,000 个对象。PLCopen 对不可导出对象明确失败，不能假设整个硬件工程可用 PLCopen 表达。
- graph-v1 最多 256 节点、512 变量、256 动作、每节点每侧 64 引脚。检查重复 ID、悬空连接、引脚名、执行顺序、无状态反馈环和基础 SFC 拓扑；真实类型/库解析仍靠 IDE 编译。
- 制品分块最多 8,192 个 UTF-16 代码单元，沿用返回的 nextOffset；不在代理对中间截断。字节数、XML 哈希和读取偏移不是同一单位。
- Worker JSON 请求仍有 128 KiB 总上限；超过 64 KiB 的 XML 改用私有侧文件，较小的内联 XML 若 JSON 转义后总请求超限仍拒绝。
- PLCopen 检查是有界结构检查；原生格式仅检查 XML 可解析。产品未内置官方 XSD 或声明完整标准一致性。

## 源码分工

Core.Workspaces/GraphicalProgram 维护图形规则。EngineeringService/Exchange 负责格式、编解码和应用用例；ExchangeExecutor 包裹原始厂商执行器、位于 ManagedProjectExecutor 内部，保证逻辑核对发生在候选发布之前。Execution/ExchangeArtifacts 实现制品存储。ExchangeTools 只做协议映射。

`exchange_worker.py` 调用官方导入导出与报告接口，不管理另一个 MCP 服务；工程打开、备份副本、保存重开、依赖检查及自有会话关闭继续由已有链路负责。`engineering_worker.py`、`configuration_worker.py`、`exchange_worker.py` 一起纳入验证脚本指纹。

## 自动回归和 OpenCode

构建完成、5088 端口空闲时运行：

```sh
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
python eng/verify-mcp.py
python eng/verify-execution.py
python eng/verify-editing.py
python eng/verify-configuration.py
python eng/verify-exchange.py
python -m unittest discover -s tests/script_engine -v
```

P4 场景为 [p4-exchange.json](../config/regression/p4-exchange.json)：38 步涵盖 LD/FBD/SFC 生成/解码/创建/读取、LD 修改、过期图形哈希、PLCopen/native 交换、冲突 reject/skip、任务绑定、模拟编译、保存及非法 XML/制品/连接。三个原创模型见 [graphs](../tests/fixtures/graphs)。开发检查中三个模型生成的 XML 也已通过从 PLCopen 官网下载的 TC6 2.01 XSD；此项只证明样例结构符合 schema，不是目标 IDE 验收。

TestHost 仅模拟已导入 POU XML 的保存往返。原生 fixture 使用明确标注的 `<FixtureNative simulated="true">`，不能解析真实 CODESYS 原生文件；未建模的工程对象、旧文本 POU 导出或 XML 对象的文本更新明确拒绝。它不是 InoProShop 模拟器，编译结果仍标 simulated。

实际 OpenCode 操作提示见 [图形编辑场景](../config/opencode-exchange-scenario.md)，复用现有 HTTP MCP 配置。OpenCode 生成模型并调用工具、读取诊断和迭代；本服务不包含大模型。

## 后续 Windows 集中验收

沿用 [P3 ValidationHost 启动方法](14-engineering-configuration.md)，显式允许 `project.export`、`project.import`、`pou.graph.read`、`pou.graph.write` 以及场景需要的工程创建/保存、任务配置和编译。图形更新的内部预读也需要允许 `pou.graph.read`。

```powershell
$env:PLCMCP_API_KEY = $env:PlcMcp__ApiKey
python eng/run-regression.py --scenario config/regression/p4-exchange.json --variables .plcmcp/variables.json --report .plcmcp/reports/p4.json --expect-mode real
```

模板要求有目标 Application、未创建 Task Configuration，并已具备样例 TON/CTU 等库和设备依赖。运行前核对对象 GUID、目标版本和本地配置；供应商命名/类型不同应记录并调整脱敏样例。对真实原生交换另选含硬件/CFC 的工程验证，fixture 的 native 路径不能代替此项。

逐项记录：API 重载与回调形态、节点/属性规范化、SFC 动作子对象、图形布局、保存重开、编译诊断和调用绑定。不能表达的项记为受限/未验证，不通过 UI 点击模拟声称支持。下载、启停、真实运行和 EtherCAT PDO 布局不属于本批。

## 官方依据

- [ScriptProject](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptProject.html)：`export_xml/import_xml`、`export_native/import_native`、导入/导出报告与 NativeImportResult。原生导入导出接口标注始于 3.4.4.0，仍须验证汇川目标 Profile。
- [ScriptObject](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html)：对象级导入与冲突策略；Project 的 import_xml 没有对象接口同样的 conflictResolve 参数，本实现使用 reporter。
- [PLCopen 官方下载](https://www.plcopen.org/downloads/)：TC6 XML 2.01 schema 与技术说明。未把供应商文档或官方 schema 复制到公有仓库。
- [CODESYS PLCopen 导入限制](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_cmd_import_plcopenxml.html)：对象与声明表达限制不能由“导入成功”消除。供应商 CHM 基线与官网版本差异仍见 [docs/10](10-official-api-baseline.md)。
