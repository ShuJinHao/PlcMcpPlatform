# P13：Mac OpenCode 联调与 Windows 集中回归

核查日期：2026-11-07。开发分支承接 P12 `c0c3c8b78e8bdc41a6c77318155826c6412105ce`，保留既有产品路线文档。汇川仍是第一阶段；西门子、欧姆龙是后续适配器。本批不增加模型、会话或代理服务。

**CFC 结构化网络、PDO 专用布局、图形子对象安全替换仍存在资料/目标样例阻塞。本候选包供准备和验证已实现范围，不能据此宣布全覆盖完成或通过进入全量 Windows 验收的门槛。** 不把这些缺口统一标成“只待 Windows 测试”。

## 能力状态

| 范围 | 实现与 Mac 检查 | 资料与真实验证状态 |
|---|---|---|
| POU/GVL/DUT、ST、Method/Property/Action | 既有读取、创建、修改、访问器、批量与精确补丁；完整离线回归 | CHM/官方 API 有依据；目标重载、保存重开、类型 GUID 和编译待真实 IDE |
| LD/FBD | graph-v1/v2、变量组、保持属性、地址、注释、初值、连线、标签、inOut；P13 增加枚举/整数子范围 | TC6 2.01 + 官方类型文档；目标导入/导出和编译待真实 IDE |
| SFC | 定时动作、有界选择/并行流程、多动作关联；沿用 P12 校验和子对象保护 | XML 数据和动作次序元数据已有依据；控制器执行顺序不可由列表推断 |
| 图形中的内嵌 struct/pointer、PERSISTENT、通配地址、格式化注释、任意厂商扩展 | 未实现该结构化无损编辑范围，遇到未表达内容拒绝；命名 DUT 和文本声明是独立已有能力 | 标准格式与厂商扩展不能混同；需要目标声明/交换样例和保留规则，不能把拒绝当作无损处理 |
| CFC 网络与执行顺序 | 创建语言对象、原生导入导出调用已实现；专用网络编辑器未实现 | **阻塞**：CHM 只给语言 GUID；官网说明 CFC 不属于 IEC 61131-10 交换覆盖。缺官方扩展 API/原生序列化结构与往返样例 |
| 图形子对象 | 原有成员工具可独立操作；有子对象的图形 POU 整体覆盖仍拒绝 | **阻塞**：缺目标版本中成员 GUID、引用、访问器内容、任务绑定的完整保留与保存重开证据 |
| Device/Task/Library/IO | 设备树、参数、IO 变量映射、任务/调用绑定、库/占位符已实现 | 真实设备描述、插槽、库解析和重开仍待 Windows |
| EtherCAT PDO | 专用条目增删、分配、排序及布局未实现 | **阻塞**：通用参数只读集合与 IO 通道映射不提供专用 PDO 结构编辑契约；缺具体设备/插件版本及官方接口或已定义交换格式 |
| 作业与交付 | 幂等、冲突、取消、超时、重启、未知结果隔离；准确修订 ZIP；原故障门禁继续回归 | 模拟不提升 Supported；真实进程/IDE 故障仍需记录目标环境证据 |
| Mac/OpenCode | .NET 8 独立工具链；统一启停；原生远程 MCP；实际 DeepSeek Flash 模型回归 | 模型调用和工程模拟分别标记；无 InoProShop/PLC 运行证明 |
| Windows 交付 | 三种隔离包、源提交/文件摘要、统一预检/集中场景/报告 | 本机未执行 Windows 入口的真实 IDE 路径；包内代码检查不等于现场兼容 |

以上真实厂商状态均为 Unverified；未新增空工具，仍为 74 个工具实现。生产宿主没有真实批准时只开放 22 个本地工具。

本机最终 `eng/verify.py` 全量检查通过：Release 构建零警告/错误，253 个 .NET 测试、102 个 Python 测试；HTTP、执行故障、编辑、组态、交换、交付、增量、成员、图形、初值、声明语义、维护及发布检查全部通过。新增类型 XML 另通过官方 TC6 2.01 XSD 校验。完整日志为 `artifacts/mac/full-verification.log`；这些检查的工程执行均为模拟。

## Mac 日常使用

以下开发命令在源码仓库根目录运行；发布包使用包内 eng/start.py。需要 Python 3.10+、.NET 8 SDK、原生 OpenCode 1.x。项目 `global.json` 固定 .NET 8；可以将官方 SDK 安装到忽略目录 `.plcmcp/dotnet`，不影响全局 .NET 版本。本机使用 8.0.427 ARM64。

```bash
python3 eng/dev.py setup
python3 eng/dev.py start --no-build
python3 eng/dev.py status
python3 eng/dev.py opencode --no-build
python3 eng/dev.py stop
python3 eng/dev.py restart --no-build
```

`setup` 还原锁定依赖并构建。`start`/`restart` 默认重新构建，`--no-build` 使用已有 Release 构建。入口仅用于 POSIX 开发机，宿主始终是独立 TestHost。Windows 真实环境使用下一节入口。

- `.plcmcp/dev/workspaces`：原创 sample 模板、imports/existing 和模型工程；已有文件不覆盖。
- `.plcmcp/dev/data`：独立 SQLite、候选、作业及交付证据。
- `.plcmcp/dev/mcp-key`：随机本机 MCP 口令，权限 600；不回显。
- `.plcmcp/dev/host.log`、`host.json`：日志和本入口的进程身份；服务固定监听 `127.0.0.1:5088/mcp`。
- `.plcmcp/client/opencode.json`：生成的原生项目配置，MCP 使用 Bearer 环境引用；默认 `deepseek/deepseek-flash`。保留现有全局用户配置，已编辑的本地项目配置不覆盖。

通过 `dev.py opencode` 打开该项目目录后可直接提出 PLC 编程需求。界面说明和所有服务结果标记 fixture/simulated。直接在其他终端启动 OpenCode 时，需使用该目录并自行提供同一 MCP 环境变量；普通 MCP 连接不需要特制 OpenCode。

DeepSeek 默认直连 `https://api.deepseek.com`。认证依次使用 `DEEPSEEK_API_KEY`、`PLCMCP_DEEPSEEK_KEY_FILE` 指向的私有文件（默认 `~/.config/plcmcp/deepseek-api-key`），或 OpenCode 原生 DeepSeek API 认证。不要将密钥写入 Git、配置样例或报告。生成的配置仅包含环境引用，不改动已有 Kimi 等提供商。

若 5088 被占用，入口拒绝启动，不终止占用者。停止前核对 PID、启动时间、命令和本入口随机标识；身份不符时拒绝发信号。停止超时不会强杀或自动重试作业。出现未知结果时先读取作业证据，按既有恢复流程处理。

故障定位顺序：`status` → `host.log` → 运行时 `schedulerBlocked`/作业状态 → 检查同一 MCP 口令和 OpenCode `mcp list`。鉴权失败与模型提供商失败分开处理。

## 模型回归

```bash
python3 eng/dev.py model --no-build --scenario repair --report artifacts/mac/repair.json
python3 eng/dev.py model --no-build --scenario graphics --report artifacts/mac/graphics.json --timeout 1200
python3 eng/dev.py model --no-build --scenario members --report artifacts/mac/members.json --timeout 1200
python3 eng/dev.py model --no-build --scenario configuration --report artifacts/mac/configuration.json --timeout 1200
python3 eng/dev.py model --no-build --scenario incremental --report artifacts/mac/incremental.json --timeout 1200
```

每次创建独立工作区；已有报告不覆盖。模型只可使用 PLC 工具。repair 检查模型观察失败后修复、失败版本未发布、最终源码和任务绑定；其他场景分别检查图形、声明/成员、组态、已有工程/恢复/交换的实际服务作业和最终读回。所有场景检查最终修订、ZIP 文件摘要及编译报告。PDO 未实现，不将 IO 映射场景算作 PDO 通过。

本机 OpenCode 1.18.2 + DeepSeek Flash 五组实际模型流程均通过，成功运行合计 333 次工具调用：

| 场景 | 工具调用 | 独立核对 |
|---|---:|---|
| repair | 30 | ST + 任务、观察编译失败后修复、失败版本未发布、最终源码与 ZIP |
| graphics | 155 | LD/FBD/SFC 各两种写入语义、变量声明、最终读回与 ZIP |
| members | 50 | GVL/DUT、Method、Property 双访问器、Action 的最终内容 |
| configuration | 51 | 新设备、参数、任务/绑定、库及 IO；不包含 PDO |
| incremental | 47 | 接入、精确补丁、差异、历史恢复、工程导出/导入、最终读回 |

SFC 运行包含多次格式错误和自行修正，未隐藏失败尝试。增量场景首次未满足工程导出及最终读回证明，82 次调用后严格判失败；补充字段诊断与明确场景要求后，新工作区复验通过。另保留了一份 CLI 帮助流检查的初始失败报告。没有把失败输出改写为通过，也没有降低断言或重试未知写入。

本地完整证据在 `artifacts/mac/model-regression-summary.json`、逐次 JSON 和 `deliveries/*.zip`；汇总含事件流、报告和交付摘要。真实模型结果仍是模拟工程证据，`realIdeScenarioPassed=false`。


CI 保留固定 1.19.5 以便复现。本地检查 `run --format json --model` 接口、工具事件、服务作业和交付完整性，不按版本字符串拦截。DeepSeek [官方接入说明](https://api-docs.deepseek.com/quick_start/agent_integrations/opencode/) 建议不低于 1.18.30；当前已观察到 1.18.2 可完成上述场景，因此没有替换全局安装。

没有显式执行/模型或没有所选认证时报告 skipped，不能以 MCP 连接成功替代模型验收。超时只结束本次 CLI；报告保留已观察作业 ID，工程结果未知时必须先对账，不能自动重放。

## 图形类型增量

`enumValues` 是有序 `{name,value?}` 数组，`type=ENUM` 表示未显式指定基类的枚举，或使用明确整数基类（例如 UDINT）。本批有界支持十进制整数值，保留省略值的隐式次序；重复名称、溢出和未知数据拒绝。

`subrange={lower,upper}` 与 `enumValues` 互斥；边界为十进制字符串，支持对应整数基类的完整宽度。可作为数组元素类型与函数返回类型；不自动求值 ST 表达式。生成使用 TC6 的 enum、subrangeSigned/subrangeUnsigned，不把类型声明塞入普通 derived 名称。

旧 graph-v1/v2 请求保持兼容，旧摘要不变。新增字段进入 XML 语义摘要、读回和变量/返回类型差异；通过原有批量最终编译、发布和 ZIP 机制。`p13-inline-types.json` 覆盖生成、解码、边界拒绝、两次批量、读回、差异和交付。真实匿名类型导入及目标编译仍需专门验证。

依据：[PLCopen TC6 2.01 下载](https://www.plcopen.org/downloads/)、[CODESYS 枚举](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_datatype_enum.html)、[整数子范围](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_datatype_subint.html)。供应商 CHM 与官方 XSD 保留在仓库外，不随包分发。

## Windows 准备与集中入口

使用同一源码提交生成的 production、validation、fixture ZIP 和 SHA256SUMS。校验摘要后分别解压，不在包内写入运行数据。目标机需要 Python 3.10+、.NET 8 ASP.NET Core Runtime、InoProShop、正确 Profile、设备描述和库；不要求连接 PLC。

准备 `C:\PlcMcpValidation` 下的三个本机配置：复制 `config/windows-regression.example.json`、`config/inovance-validation.example.json`、`config/regression/windows-variables.example.json`，保留包内原件。配置集中指定：IDE EXE、ScriptEngine DLL、精确 Profile、产品版本、允许的离线操作、工作区和报告路径。

在 `workspaces/templates/<templateId>` 放入已保存的脱敏工程、完整声明文件列表及 template.json；模板要满足各场景前置条件，尤其是用于创建 Task Configuration 的 Application 不得已有任务配置，所需功能块/库须存在。已有工程副本放在 imports/existing；不得使用正在编辑的生产工程。

```powershell
py .\eng\windows-regression.py --bundle . --settings C:\PlcMcpValidation\settings.json --report-directory C:\PlcMcpValidation\reports\preflight-001
py .\eng\windows-regression.py --bundle . --settings C:\PlcMcpValidation\settings.json --report-directory C:\PlcMcpValidation\reports\run-001 --execute
```

第一条只检查包、操作系统、运行时、EXE/引擎/Profile 文件和版本/摘要及端口，不启动 IDE。第二条启动本入口独占 ValidationHost，复制独立预检工程，读取对象树、设备树和已安装库，并编译独立模板副本检查依赖，随后执行 P3～P8、P11～P13 共九套、225 个场景步骤。首次发现缺少人工配置时写出 resolved-variables.json，完善该文件并在 settings 中选择它；重新运行使用新的报告目录。

自动选择仅限唯一观察到的 Application、指定名称的 Program、所属控制器和库管理器；Program 再用声明读回验证。有多个候选时列出候选，不按第一个猜测。设备类型/ID/版本、待测试库、可写参数与 IO 地址、导入源文件和产品版本需要操作者根据实际安装指定。EXE FileVersion/ProductVersion 与引擎程序集版本分别记录，不互相冒充。

报告目录包括 summary、templates、discovery、resolved-variables、逐套件结果和 host.log。场景记录作业 ID、编译诊断、修订、摘要和失败位置；首个失败/未知结果即停止后续写入。脚本只终止自己启动的宿主；宿主停止不证明 IDE Worker 或未知作业已完成。报告和真实工程留在本地，不提交公有仓库。

实际 Windows 验收清单：

- [ ] 版本/Profile/引擎和依赖与报告一致，模板可手动打开。
- [ ] 代表性 ST、声明/成员、LD/FBD/SFC、设备/任务/库/IO 场景通过。
- [ ] 失败批次未发布，保存关闭重开后读回一致。
- [ ] 接入、差异、历史恢复和导入导出保留所选范围。
- [ ] 最终工程修订与 ZIP 清单及全部文件摘要一致。
- [ ] 在隔离副本上复验取消、超时、重启、结果未知和版本差异；现场异常要检查实际进程及证据，不用 fixture 故障结果代替。
- [ ] 单独完成 CFC/PDO/图形子对象阻塞调查后，补齐实现及对应验收，再判断全量阶段是否完成。

成功报告不自动批准生产能力。仍通过既有 review-acceptance 和人工范围审核形成真实验收记录。

## 本轮供应商资料复核

用户提供 CHM：SHA-256 `cbd1577977333a8cea8a143389826e8f93f6f06a32a4d3a3d054d1633f8e7483`，1618 个 HTML 页面，ScriptEngine 3.5.9.0。此版本不代表 InoProShop 产品版本。

CHM 的 EtherCAT 字样出现在 Application Composer 的物理 IO 通道路径示例；此前“没有 EtherCAT 条目”的表述已修正。未发现 PDO 专用结构契约。

官网核对补充：[汇川产品页](https://www.inovance.com/global/product/secondlevel.html?fid=100607&pid=100057)确认语言与 EtherCAT 产品能力；[汇川运动控制指南](https://portal-file.inovance.com/owfile/ProdDoc/SC/19012378-SC/A00/19012378-SCY_A00%20Medium-sized%20PLC%20Programming%20Guide%20%28Motion%20Control%29-EN.pdf)给出 PDO 配置概念与界面，未据此构造脚本接口。[CODESYS 交换说明](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_cmd_export_iec_61131_10.html)区分标准交换和 CFC 等原生对象；[CFC 执行顺序](https://content.helpme-codesys.com/en/CODESYS%20CFC/_cds_dlg_properties_cfc_execution_order.html)还区分自动数据流与显式顺序。不能把已有 FBD executionOrderId 直接当作 CFC 格式。

[官方设备参数接口](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceParameters.html)说明参数集合由设备描述决定，IO 映射操作已有可映射项。当前调查不能推导 PDO 任意增删/重排，也不能断言厂商所有版本均无扩展。下一份必要资料是目标版本的官方 CFC/PDO 扩展 API，或定义明确的原生交换格式及成对修改样例。
