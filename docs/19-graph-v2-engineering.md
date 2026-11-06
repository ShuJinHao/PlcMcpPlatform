# P8：图形编程扩展与混合语言工程

本批扩展同一个标准 MCP Server 的工程能力。OpenCode 负责生成程序、选择工具和根据诊断修正；服务负责模型校验、官方 ScriptEngine 交换调用、工程修改及结果反馈。没有新增客户端业务适配、会话管理或模型编排。

当前交付为代码与离线回归，真实 InoProShop/IronPython、模型生成与 PLC 运行仍未验收。TestHost / ValidationHost 保持 74 个工具；当时的生产宿主仅开放两个查询工具；P9 已改为本地工具及按范围验收的 IDE 工具，见 docs/20，厂商能力均为 Unverified、engineeringReady=false。

## 模型与兼容性

`plc_graph_generate` 的 program 增加可选 `profile`，默认 `graph-v1`；新结构使用 `graph-v2`。旧契约文件 [graph-v1](../contracts/graph-v1.schema.json)保留，新传输形状见 [graph-v2](../contracts/graph-v2.schema.json)。已有 graph-v1 的内容与语义哈希算法保持兼容，不自动将新增结构混入旧 profile。

XML 解码根据实际内容识别所需 profile，不能依赖厂商保留本项目文件头。若一个仅含旧结构的 v2 文档被 IDE 重写文件头，可能返回 v1；profile 标签不是工程语义，也不是厂商支持证明。`GraphDocument.profile` 表示本次模型使用的范围。

| 扩展 | 请求表达 | 当前范围 |
|---|---|---|
| 函数 | `pouType="function"`、`returnType={type:"INT"}` | LD/FBD；Function 必须返回类型，其他 POU 禁止 returnType；SFC Function 拒绝 |
| 字符串长度 | 变量或 returnType 的 `stringLength` | STRING/WSTRING，1～65535；表达式长度暂不支持 |
| 数组 | 变量或 returnType 的 `dimensions:[{lower,upper}]` | 1～3 维、整数上下界、总元素数最多65536；元素可以是基本类型、字符串或命名派生类型 |
| 派生类型 | `type="MachineState"` 或 FB 类型名 | 引用已有 DUT/FB，不内嵌结构体定义；实际类型解析交给 IDE |
| 连接标签 | `connector` / `continuation` 的 `name` | LD/FBD；一个标签源、可有多个续接点；大小写不敏感解析，重复源/缺失源拒绝 |
| 双向引脚 | block 的 `inOuts:[{name,connections}]` | 同一引脚同时输入/输出；输入须为变量引用，可经标签连接；不接受常量、表达式、取反或猜测块输出为可写变量 |
| SFC 选择分支 | selectionDivergence / selectionConvergence | 至少两个命名输出、完整对应汇合；转移 priority=1..N 与输出顺序、从左到右 X 坐标一致 |
| SFC 并行分支 | simultaneousDivergence / simultaneousConvergence | 各分支以步开始/结束，完整汇合后进入转移；允许成对嵌套 |

`GraphVariable.type` 仍保留字符串形状，新增字段不会要求旧客户端改写变量格式。数组的复杂初值、内嵌结构声明、地址、保留/常量分组、额外厂商属性不在本批结构模型内，遇到不能表达的数据明确拒绝。数组初值不能用 simpleValue 冒充聚合初始化。

## 图形规则与失败行为

连接标签产生隐式边，必须参与循环与传递执行顺序检查。不能通过连接标签绕过图形循环检测，也不能让下游块先于上游块执行。双向引脚名称与普通输入/输出名称不能冲突，双向输出可被其他节点明确引用。

SFC 采用有界、完整汇合的结构化分支。检查初始步唯一、步骤名唯一、连接端口存在、节点可达、选择分支优先级、分支类型、汇合对应与完整性。流程循环通过 jumpStep 明确表达；任意跨分支跳转、未汇合分支和厂商专用步骤属性继续拒绝。这些检查不是 PLC 执行仿真，也不证明流程业务正确。

主要错误：

| 错误码 | 含义 |
|---|---|
| graph_profile_mismatch | 用旧 profile 提交新增字段/结构，或 profile 未知 |
| invalid_graph_return_type / invalid_graph_variables | 返回类型、数组或字符串声明不符合当前范围 |
| ambiguous_graph_connector / unresolved_graph_continuation | 标签源重复或续接源缺失 |
| graph_inout_requires_variable | 双向参数输入不能确认是变量引用 |
| graph_cycle_requires_state_variable / graph_execution_order_conflict | 数据连线形成循环或执行顺序冲突 |
| invalid_sfc_priority / invalid_sfc_branch_ports | 选择顺序不一致、分支端口缺失或被重复使用 |
| sfc_branch_requires_matching_join / unreachable_sfc_node | 分支无法完整对应汇合、存在不可达节点 |
| graph_contains_unrepresented_data | XML 中有模型无法保留的工程内容 |
| graph_object_has_children | 图形 POU 包含独立 ScriptObject 子对象，整体替换无法证明安全 |

语义哈希包含返回类型、数组边界、字符串长度、标签名称、双向引脚、分支结构、转移优先级及原有节点/连接/声明。一般坐标仍不作为逻辑摘要；选择分支的位置顺序先通过规则验证，不能靠调换左右位置改变选择规则却保持合法摘要。

## 与工程生命周期的集成

复用 `plc_graph_generate/decode`、`plc_pou_graph_read/write`、`plc_project_batch_preview/apply`、`plc_project_diff` 与 `plc_delivery_create`。工具数量不增加；MCP 输入输出 Schema 和描述已同步。

修改仍要求当前工程修订、对象 ID 与旧图形语义哈希。完整 POU 替换可能改变 GUID，后续请求使用返回的新标识；已绑定对象的替换仍应显式处理任务引用，不声称自动改写所有源代码引用。

每个候选修改经过官方导入、保存、关闭、重开、重导出和模型核对，批量最终编译通过才发布一个新版本。未知数据、读回语义变化或编译失败均不能推进当前头。工程版本差异新增 returnType 和 v2 节点/声明比较；旧 v1 comparison 标记保持兼容。

真实 Worker 继续使用原来的原创 `import_xml/export_xml` 实现，无需为了新图形模型另造厂商 API。子对象保护保持启用，尤其是目标 IDE 可能把 SFC Action 展开为独立对象的情况，需后续记录对象树与往返证据后另行处理。

## 回归样例

四个原创样例见 [graphs](../tests/fixtures/graphs)：

- `ld-v2-motor.json`：启停、自保持、故障互锁、报警复位及命名连线。
- `fbd-v2-data.json`：TON、CTU、ST FB 的双向参数、数组变量和连接标签。
- `fbd-v2-function.json`：带明确返回类型的 FBD Function。
- `sfc-v2-branches.json`：选择分支、并行分支、汇合、ST 动作和跳转。

这些是工程表达与回归样例，不是已经验收的设备控制程序。发布前应按实际需求审查运行逻辑。

[31 个综合场景](../config/regression/p8-graph-v2.json)创建 DUT、GVL、ST FB、四种图形 POU 和 MAIN_Mixed，通过周期任务调用 MAIN_Mixed；11 步批量创建后读取四种图形，分别修改并核对，比较 Function 返回类型变更，拒绝旧图形哈希，最后交付准确修订的 ZIP。HTTP 检查还覆盖编译失败保留旧头以及包内文件摘要。

自动检查入口：

```sh
python eng/verify.py
# 已完成 Release 构建时单独执行本批 HTTP 检查
python eng/verify-graphs.py
# 已启动 TestHost 或 ValidationHost 时复用集中场景
python eng/run-regressions.py --suite p8 --variables .plcmcp/variables.json --report .plcmcp/reports/p8.json --expect-mode fixture
```

针对真实 ValidationHost 将最后一个参数改为 `real`。模板须有目标 Application、尚无 Task Configuration、具备 TON/CTU 所需库；FB_Adjust 由场景自主创建。该入口不触发在线下载/启停，也不自动提升能力支持状态。无需用户现在提供 Windows/IDE/PLC。

新增四个与原有三个图形样例的生成 XML 在开发检查中通过从 PLCopen 官网取得的 TC6 2.01 XSD；Schema 未复制到公有仓库。这项校验只证明样例格式，不等同于目标 IDE 兼容或完整 IEC 语义校验。三系统 CI 执行 .NET、Python、HTTP 与发布回归；所有 fixture 编译与工程结果明确标记 simulated。

## 官方依据与保留缺口

- [PLCopen 官方下载](https://www.plcopen.org/downloads/)的 TC6 XML 2.01 XSD：dataType/array/dimension、returnType、connector/continuation、inOutVariables、四种 SFC 分支元素及 transition.priority。
- [CODESYS SFC Branch](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_element_branch.html)：选择分支从左至右判断，并行分支同步进入各分支；不同分支的起止元素要求不同。
- [ScriptObject](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html)：对象级 XML 导入导出、冲突与报告。继续使用供应商 CHM 已核对的调用路径，新模型不改变厂商版本支持结论。

定时动作限定符、任意复杂 SFC、图形 POU 子成员重建、CFC 结构化编辑、EtherCAT PDO 重组仍为明确缺口。本批没有取消保护或通过 UI 点击模拟宣称完整支持。
