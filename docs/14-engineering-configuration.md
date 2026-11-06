# 第三批：工程组态与真实回归入口

本批交付完整的工具入口、输入验证、候选版本执行、原创 ScriptEngine 调用、保存重开核对、测试和回归运行器。目标仍然是 OpenCode 调用本服务完成 PLC 工程编程。没有接入第三方 PLC MCP，也没有增加第二层 MCP 服务。

当前实际 InoProShop、IronPython 和 OpenCode 客户端未运行。以下“已实现”表示代码与自动检查范围；汇川兼容性仍为 Unverified。真实回归可以等取得 Windows 环境后集中进行，不阻塞后续代码开发。

## 27 个新增工具

第三批交付时加上前两批 22 个共有 49 个工具；第五批追加后当前为 67 个，见 docs/16。所有写操作都提交持久化任务，使用工作区的 m2 修订，修改独立候选副本。成功后返回新版本；原版本和原模板保留。

| 工具 | 工程行为 |
|---|---|
| `plc_task_list` | 指定 Application 下的任务与调用列表 |
| `plc_task_read` | 任务类型、周期、单位、优先级、看门狗与有序调用 |
| `plc_task_configuration_create` | Application 下创建尚不存在的 Task Configuration |
| `plc_task_create` | 创建任务并写入调度设置 |
| `plc_task_update` | 更新调度设置；未提供的看门狗和核绑定保持原值 |
| `plc_task_delete` | 核对原名称后删除任务 |
| `plc_task_set_calls` | 用完整有序列表绑定、解绑、排序；空列表解绑全部 |
| `plc_pou_member_read` | Method、Property、Action 内容读取 |
| `plc_pou_member_create` | 创建成员并写入声明、实现或 Property 访问器 |
| `plc_pou_member_update` | 更新成员文本或 Get/Set 文本 |
| `plc_object_inspect` | 对象类型、标识、父节点与直接子节点 |
| `plc_folder_create` | 创建工程文件夹 |
| `plc_object_rename` | 检查原名称和同级冲突后重命名 |
| `plc_object_move` | 按对象标识移动，检查循环、名称和 Application 边界 |
| `plc_object_delete` | 核对原名称，明确递归意图后删除 |
| `plc_library_list` | 指定 Library Manager 的直接引用、版本、占位符解析 |
| `plc_library_list_installed` | IDE 当前库仓库的安装清单 |
| `plc_library_add` | 使用确切的已安装库显示名添加引用 |
| `plc_library_remove` | 删除指定引用，不卸载库包 |
| `plc_library_set_placeholder` | 创建/更新占位符的默认解析、重定向；空重定向清除覆盖 |
| `plc_device_get_tree` | 工程设备标识、描述版本及父子关系 |
| `plc_device_add` | 使用确切设备描述添加或 plug 子设备 |
| `plc_device_remove` | 删除设备，保护模板控制器与 Application |
| `plc_device_get_parameters` | 读取设备/连接器的离线参数叶子、类型和权限 |
| `plc_device_set_parameter` | 校验类型、范围、枚举和离线读写权限后修改 |
| `plc_device_get_io_mappings` | 读取可映射叶子的变量表达式与映射方式 |
| `plc_device_set_io_mapping` | 映射已有变量、明确创建映射变量，或清除映射 |

实际 JSON Schema 由 [MCP 契约快照](../contracts/mcp-tools.v1.json)记录。新增工具的返回契约版本为 1.2；Worker 协议主版本仍为 1，采用可选字段扩展。

## 语义和边界

### Task

Task 的设置按类型校验，周期和看门狗单位为 us/ms/s，优先级使用非负整数字符串。目标设备允许的优先级范围仍需由其 SDK 校验并读回；通用接口不虚构一个适用于所有控制器的范围。事件类型必须提供对应事件字段。

调用列表每项包含 `pouId/pouName/comment`。真实脚本同时核对 GUID、名称、PROGRAM 声明与 Application 可见范围，拒绝歧义和直接绑定 Function/FB；不会仅凭字符串绑定同名对象。列表有 128 项上限，不允许重复 POU GUID。替换后核对顺序和注释，保存重开后再核对一次。当前支持文档集合返回的字符串或二元序列；未知元素形态返回 `task_call_representation_unverified`，不丢弃字段伪造成功。

### POU 成员和图形语言

Method/Action 当前创建于 Program/FB，Property 创建于 Program/FB/GVL；Interface 成员签名不在本批范围。Method、Property 使用声明标记校验对象类型。Action 没有同样的声明标记，其读取和更新要求 `expectedTypeId`：使用创建结果或 `plc_object_inspect` 返回的厂商类型 GUID。该字段用于固定对象类型身份，不等于已认证所有 IDE 的 Action 类型表。

Property 的 Get/Set 是独立子对象。返回值包含访问器标识和各自文本；缺失访问器返回明确错误，不猜测供应商未公开的创建方法。更新操作不接收语言/返回类型切换选项；需要修改声明时显式传递完整声明文本。

创建成员可传入已有语言标识；非文本语言只创建空对象，不接收网络文本。第四批已增加 LD/FBD/基础 SFC graph-v1 与 XML 往返；CFC 结构化编辑和复杂图形仍不在实现范围，见 docs/15。不能把空图形 POU/成员算作图形程序已生成。

### 对象管理

写前核对名称、身份、父子关系与完整有界对象树。重名、循环移动、跨 Application 移动、未确认的递归删除会被拒绝。原始工程、Application、Task Configuration、库管理器以及模板控制器不通过通用删除入口移除。

对重命名、移动和删除，脚本检查已知任务调用引用，发现冲突即拒绝。返回 `referenceCheckScope=task_calls_only`。目前没有经过验证的全文引用重构 API，因此不宣称自动更新 ST 表达式、设备引用或所有符号；完成这些操作后应单独编译并检查诊断。

### 库与依赖快照

模板描述继续表示不可变的初始要求。工作区版本增加 `currentDependencies`，记录保存工程中观察到的设备标识与库直接引用；它是派生校验数据，不是第二套工程内容数据库。

第一次从模板创建时核对初始控制器与必需库引用。后续写入对照当前版本的依赖快照，允许显式增加/移除引用。保存前的完整依赖快照必须与重开后相同，然后和新版本一起发布。已有工作区没有快照时仍先校验模板要求，再建立快照。SQLite 使用现有版本 JSON 字段存储，无额外表迁移。

安装清单、工程引用和占位符有效解析是不同事实。添加引用和设置明确解析时校验指定库已安装；不会自动安装、下载或卸载供应商包。占位符返回默认解析、有效解析与重定向；不能仅凭字符串设置成功就保证依赖整体可编译。当前 `installedDependenciesVerified=false` 仍表示没有完整安装环境认证。

### Device、Parameter 和 IO

添加设备前通过设备仓库检查描述身份，再调用 add/plug；新增对象通过父节点子对象 GUID 差集定位，不假定返回值为对象。设备、模块、插槽组合仍需要目标版本验证。

参数地址为 `parameterId + connectorId? + elements[]?`。复合参数逐层定位到叶子，不把数组/结构整体当作一个字符串覆盖。写入要求离线可读且可写，检查常见 IEC 整数、REAL/LREAL、BOOL、简单带引号字符串、枚举标识和数值范围。当前整数输入限定十进制，复杂时间/日期及厂商类型返回 `parameter_type_unverified`；避免把 SDK 接受任意字符串误认为参数有效。SDK 读回文本必须与提交值一致，目标版本如有规范化差异会显式失败，需要据真实样例调整。

IO 映射按 `is_mappable_io` 和映射对象是否存在判断可映射性，不把离线参数值的写权限当作映射权限。已有变量映射使用限定表达式，例如 `GVL.Input`；新变量必须是简单标识符并显式 `createVariable=true`。空表达式清除映射，此时逻辑 createsVariable/mapsExistingVariable 均为 false，另保留 SDK 原始标志用于版本核对。返回映射方式并读回核对；仍须编译确认变量和通道类型兼容。通用 IO 变量映射不等于 EtherCAT PDO 布局编辑。本批没有在线参数读写、下载、启动或停止工具。

## 三个运行入口

| 入口 | 用途 | 工具/执行来源 |
|---|---|---|
| `src/hosts/PlcMcp.McpHost` | 默认生产入口 | 2 个状态查询；不执行未验证工程能力 |
| `tests/PlcMcp.TestHost` | Mac/Linux/Windows 自动开发检查 | 当前 67 个工具；fixture/simulated |
| `src/hosts/PlcMcp.ValidationHost` | 后续真实 Windows 集中回归 | 当前 67 个工具；real/unverified；仅显式操作白名单可执行 |

验证宿主不是默认宿主的一个配置开关。它要求 Windows、独立且明确的数据目录和工作区、验证配置文件、实际 IDE 路径与匹配 Profile。执行器在入队及实际执行时检查白名单；模板创建/保存的内部 project.verify 也受原操作范围约束。没有模拟回退，也不会自动提升能力状态。

`plc_runtime_get_status.validation` 和真实操作结果记录验证 ID、声明的 IDE/ScriptEngine 版本、Profile、允许操作和脚本包 SHA-256。包指纹由 engineering_worker.py、configuration_worker.py、exchange_worker.py 的文件名和各文件摘要组成；脚本变更后必须重启宿主。`versionsVerified=false` 明确版本是操作者登记值，尚未由本服务确认。IDE 命令行和进程模型也仍待真实验证。

### Mac 自动回归

```bash
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
python eng/verify-mcp.py
python eng/verify-execution.py
python eng/verify-editing.py
python eng/verify-configuration.py
python -m unittest discover -s tests/script_engine -v
```

第四个 HTTP 检查会创建临时测试模板、启动独立 TestHost，执行 42 步组态场景，检查运行模式拒绝和输入错误，再停止自己启动的进程。已有端口被占用时退出，不停止其他服务。

### 后续 Windows 真实回归

目前无需执行这部分。准备好目标环境后，先按 [模板规范](13-managed-editing.md)把合法测试工程注册为 `executionMode=real`，填写真实 controller、必需库引用、Profile 和完整工程文件清单。只使用可修改的副本；目标工程由 IDE 内置 ScriptEngine 执行。

把 [验证配置示例](../config/inovance-validation.example.json)复制到本地 `.plcmcp/validation.json`，替换版本和 Profile，根据当次回归选择 allowedOperations。将 [变量示例](../config/regression/windows-variables.example.json)复制为 `.plcmcp/variables.json`，填入测试模板里的 Application/Program/设备/库管理器 GUID、已安装的未引用库、可加入的设备描述、可写参数和可映射通道。示例不是一个虚构的可用汇川工程。

```powershell
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:PlcMcp__DataDirectory = 'C:\PlcMcpValidation\data'
$env:PlcMcp__WorkspaceRoot = 'C:\PlcMcpValidation\workspaces'
$env:PlcMcp__Inovance__ExecutablePath = 'C:\REPLACE\InoProShop.exe'
$env:PlcMcp__Inovance__Profile = 'REPLACE_WITH_EXACT_PROFILE'
$env:PlcMcp__ValidationProfilePath = (Resolve-Path '.plcmcp/validation.json').Path
dotnet run --project src/hosts/PlcMcp.ValidationHost --no-launch-profile
```

在继承相同密钥的终端执行：

```powershell
$env:PLCMCP_API_KEY = $env:PlcMcp__ApiKey
python eng/run-regression.py --scenario config/regression/p3-engineering.json --variables .plcmcp/variables.json --report .plcmcp/reports/p3.json --expect-mode real
```

标准场景要求模板有 Application、Program、Library Manager、控制器，且该 Application 尚无 Task Configuration。已有任务配置的模板可在本地场景删除创建配置那一步，并在变量文件指定 taskConfigurationId。参数/设备不符合测试目标时应编辑当地场景，而非把另一种设备当作通用示例。

运行器通过真实 HTTP MCP 初始化、工具发现、提交与轮询执行。每步支持 RFC6901 JSON Pointer 断言和变量绑定，生成带执行来源、配置与脚本指纹的 JSON 报告；失败立即停止后续步骤。轮询超时不自动重试或取消，OutcomeUnknown 不继续写入。报告和实际工程仅保存在本地，不提交到公有仓库。

统一回归不要求连接真实 PLC。真实编译、程序运行和在线操作是不同验收项。

## OpenCode 使用

复用 [OpenCode HTTP 配置](../config/opencode.example.json)。服务仍是一个 `/mcp` 端点，外部 Agent 无需了解脚本进程协议。Mac 使用 TestHost；后续真实回归使用 ValidationHost，并先查看运行状态和允许操作列表。

工作流是：列模板/创建工程 → 查询当前修订与对象身份 → 创建程序与组态任务 → 查询任务终态 → 使用最新版本路径读取/编译 → 根据诊断修改 → 保存交付版本。每次写操作必须使用刚取得的 m2 修订；不得沿用旧路径当作当前工程。对同一业务请求重试应复用幂等键；对未知任务先读证据，不能换键重放。

本仓库的 HTTP 自动客户端测试不等于已运行 OpenCode，也不等于生成程序经过真实 PLC 验收。

## 官方依据与下一批

- [Task 配置接口](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptTaskConfigObject.html)：相关扩展从 3.5.10.0 引入，不能由供应商 CHM 的 3.5.9.0 程序集标识推断目标 IDE 一定包含。
- [IEC 成员容器](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html)、[对象操作](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html)：成员与工程对象 API。
- [库管理器](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptLibManObject.html)：安装库、引用与占位符属于不同对象。
- [设备对象](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceObject.html)、[参数及映射](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceParameters.html)：通用离线组态与 IO 路径。

第四批已交付有界 CODESYS/PLCopen XML 交换与 graph-v1 内容编辑，见 [第四批说明](15-engineering-exchange.md)；EtherCAT PDO 按独立官方依据推进。
