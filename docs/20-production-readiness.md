# P9：正式执行门禁与预发布交付

> 本文保留对应批次的实现记录。当前工具数量、启动和剩余范围以 [P10 交付说明](21-delivery-closure.md)、[工具清单](22-current-tools.md) 和 [能力缺口](23-capability-closure.md) 为准。

本批继续交付一个标准 MCP 服务。OpenCode 负责模型生成、对话和任务规划；本服务负责工程操作、结果和文件，不管理 Agent 上下文。真实 InoProShop/IronPython、模型生成闭环和 PLC 运行仍未验收，当前没有随软件发布的真实验收记录。

## 正式宿主现在能做什么

共 74 个工具实现，`contracts/tool-access.v1.json` 列出其中 22 个本地工具：图形生成/解码、XML 暂存/检查/分块传输、模板/工作区/历史查询、任务查询/取消/对账、批次预览/查询、交付包读取等。无需 IDE 即可使用；需要已存在工作区或历史的查询仍有相应前置条件。它们只处理本地数据，不等于 IDE 已接受生成的 XML。

其余工具通过同一能力规则控制 `tools/list`、`tools/call`、应用提交和任务执行。客户端缓存旧清单也无法绕过调用门禁。工具出现在清单只表示存在至少一个可用范围，实际参数必须满足 `plc_capabilities_list.acceptedScopes` 的条件。环境变化后的下一次发现/调用重新评估；无需重新实现 OpenCode 适配。

| 模式 | 可发现范围 | 工程证据 |
|---|---|---|
| McpHost，无验收记录 | 22 个本地工具 | local；IDE 操作未验证 |
| ValidationHost，Windows | 本地工具及显式 allowlist 允许的工具 | unverified；用于收集真实执行记录 |
| McpHost，有匹配的人工验收记录 | 本地工具及已批准范围、依赖齐全的工具 | accepted_scope；仅限记录中的条件 |
| TestHost，Development | 74 个工具 | simulated；永远不能作为真实验收依据 |

`plc_runtime_get_status.tools` 返回全部工具的 available/errorCode；`readiness` 返回验收状态、身份摘要、范围、Profile、策略和观察到的文件版本。`engineeringReady=true` 只表示当前批准范围可执行且队列未隔离，不表示整个 IDE、所有语言或 PLC 运行通过验收。`ideConnected` 保持 false：服务按任务启动 IDE 进程，没有常驻在线连接。作业/交付记录中的工程就绪标志仍不作为 PLC 运行正确性的承诺。

## 三层身份检查

1. 配置选择 `ScriptEngine35LaunchProfile`（当前策略标识 `scriptengine-3.5-cli-unverified`），使用官方 CLI 基线 `--profile` / `--runscript` / `--noUI`。标识保留基线名称，支持状态由验收决定。不按猜测的 InoProShop 版本号建立自动兼容映射。
2. 主进程读取实际 EXE、配置的 ScriptEngine 程序集及额外依赖文件的 SHA-256、FileVersion、ProductVersion；绑定 Profile、四个工作脚本和平台执行程序集指纹。空版本资源不会变成用户填报版本；文件哈希仍必须完全匹配。
3. 脚本在访问工程之前，通过 CLR 枚举实际加载程序集，并读取当前进程映像，核对 EXE/引擎哈希。引擎不存在、多个同名引擎或启动器与实际进程不同均拒绝。执行完成后再检查文件身份；不同版本不会复用已有验收。

`IdentityFiles` 可补充供应商插件、设备或库包文件。这里不是自动扫描整个安装目录，也不宣称已验证所有已安装依赖。工程本身仍执行既有控制器/库引用检查，SQLite 不保存工程内容副本。

## 收集与人工验收

现在的 Mac/Linux 环境可完成本地工具与 fixture 回归。以下步骤留给后续集中 Windows 验收，当前无需准备机器。

1. 解压 validation 包，按 docs/14 建立独立的验证数据目录和工作区；验证配置仍是显式操作 allowlist。
2. 配置 `PlcMcp__Inovance__ExecutablePath`、`PlcMcp__Inovance__Profile`，另加 `PlcMcp__Inovance__EngineAssemblyPath` 指向实际加载的引擎程序集。额外文件使用 `PlcMcp__Inovance__IdentityFiles__0` 等。只填版本文字不能产生可批准记录。
3. 运行真实回归。只有实际进程身份匹配、操作成功的底层调用才保存到数据目录 `validation-evidence/<requestId>.json`。每个记录包含实际身份、输入、结果、操作、时间。旧验证配置可以继续运行不带身份检查的基线测试，但不能生成正式验收证据。
4. 使用 `python eng/review-acceptance.py --record <记录.json> --record <记录2.json> --output <新的审阅目录>` 收集记录和哈希，生成 `acceptance.draft.json`。不会自动批准；未填写审阅字段的草稿不可加载。
5. 人工核对实际 IDE 结果、版本、对象/语言、保存重开和限制。填写 `acceptanceId`、`reviewedBy`、UTC `reviewedAt`、具体 `reviewStatement`，删去尚未通过的 grants，再保存为 `acceptance.json`。部署记录与软件包分开保存；含工程代码/本地路径的原始证据不得提交公有仓库。
6. 正式宿主配置同一 EXE、引擎、Profile、额外文件，并设置 `PlcMcp__Inovance__AcceptancePath` 为记录绝对路径。启动后用 `doctor.py --mode production` 检查实际清单与范围。

验收文件是本机管理员的人工声明，哈希用于识别改动，不是供应商签名或安全认证。拥有本机文件写权限的人可以伪造数据；本功能不试图建立 PKI。MCP 中没有“自动转 Supported”的工具。每份调用证据当前最多 2 MiB，验收清单最多 256 KiB；超出上限的原始记录不能直接用于批准，不能裁剪后伪装成已验证结果。记录更换需重启；旧身份失配或证据被改动时 IDE 操作关闭，本地工具保留。

## 范围定义

每个 grant 指向一个真实底层 operation、一组成功 evidenceIds，以及下列两种明确覆盖方式之一。复合用例不接受伪造的底层成功记录，而是根据依赖计算可用性。

- `coverage: constrained`：必须提供非空 `conditions`，字段和值严格相等。例如 `edit.language: ST` 和 `edit.pouType: Program` 只允许创建 ST Program。多个 grant 为“或”，同一 grant 内条件为“且”。缺失字段不匹配，拼错或不支持的字段拒绝整个记录。
- `coverage: operation`：conditions 必须为空；表示审阅人明确接受该操作所实现的完整范围。不能因为一个样本成功就据此批准所有语言/对象。复合操作中，运行时才发现目标的读取、保存、编译依赖需要这种审阅，范围不足时提前拒绝。

支持的条件是工程输入字段：objectId/applicationId，edit.parentId/objectId/language/pouType/dutType，成员 kind/language/expectedTypeId，设备 identification.type/id/version，交换 artifactId/format，以及 query.memberKind/expectedTypeId、exchange.format。字段完整定义见 `InovanceAcceptance.ConditionFields`，都是精确匹配，不能执行表达式。

既有文本更新接口不携带经验证的原语言，不能用 `edit.language=ST` 宣称限定既有对象语言；应约束确切 objectId（并在验证中确认对象）或明确审阅完整文本更新范围。图形写入可以用内容寻址 artifactId 限定已验收 XML；本批未提供按导入 XML 内部语言自动推广的规则。未知语言/对象条件不会被忽略。这些约束会限制复用范围，不能把它们默认为通配。

## 批次与异常

创建/另存/接管依赖 project.verify；批次与恢复还依赖结构读取、任务读取、编译。预检覆盖批次所有步骤及图形回读、文本补丁辅助操作；缺失依赖/条件不匹配时，在候选复制和任何脚本写入之前拒绝。引用未来对象的别名保留为符号值，不能满足某个既有 GUID 的审批条件。实际启动底层调用时再次检查。

运行时解析目标的复合用例采用保守依赖：例如对象查询包含成员读取、差异包含文本及图形读取。部分审阅不足时返回 dependency_scope_not_accepted；不会在已有写入之后才请求放开权限。

| 错误 | 含义 |
|---|---|
| ide_unavailable | 当前没有可执行的 Windows IDE |
| acceptance_required | 未设置验收记录 |
| acceptance_invalid / acceptance_grant_invalid | 记录格式、字段、范围或证据无效 |
| acceptance_identity_mismatch | 安装/Profile/脚本/平台执行代码不同 |
| acceptance_evidence_changed | 记录引用的证据文件哈希变化 |
| acceptance_changed_restart_required | 进程中发现验收记录变化，需重新审阅后重启 |
| operation_not_accepted / operation_scope_not_accepted | 操作或实际参数未获批准 |
| dependency_scope_not_accepted | 复合用例依赖不具备完整可执行范围 |
| runtime_identity_mismatch / runtime_identity_unproven | 实际 IDE 进程/加载引擎无法证明匹配 |

沿用候选、备份、修订冲突、未知结果隔离和人工对账；取消不等于 IDE 已终止。相同幂等键已经记录的作业可通过本地 job_get 读取，不重新写入。失效工具调用不会为了重放缓存而重新开放能力。

## 预发布与检查

Windows CI 在全量检查后生成 production、validation、fixture 三个 ZIP，附 `SHA256SUMS` 和 `prerelease.json`，上传到 Actions artifact `PlcMcpPlatform-P9-windows-bundles`（保留 30 天）。这是预发布交付包；不是已通过真实 IDE/PLC 验收的正式版。需要长期保存时下载归档，或从对应提交重新执行 `python eng/package-release.py --output <仓库外的新目录>`。

各包包含 .NET 8 framework-dependent 程序、脚本、契约、配置示例、操作文档和启动/诊断/回归辅助程序。安装 ASP.NET Core 8 运行时；Python 仅用于这些运维辅助命令，IDE 内仍由其 IronPython 执行工程脚本。三种包不可共用运行数据目录。`start.py` 前台启动，不注册 Windows 服务；真实 IDE 的交互会话/Session 0 支持仍待验收。

运行 `python eng/start.py --bundle <解压包> --data-directory <数据目录> --workspace-root <工程目录>`；API key 使用环境变量。空生产配置即可运行 22 个本地工具。doctor 按宿主实时门禁检查清单，不再假设生产只有两个工具，也不会把部分验收当成全部通过。

检查覆盖：三系统 .NET 构建/单元测试，Python 脚本单元测试，既有 P3–P8 HTTP 回归，生产无 IDE 图形生成/解码、未知调用拒绝、契约一致性，以及版本/脚本漂移、模拟证据拒绝、范围越界和批次副作用前置拒绝。离线测试只证明这些软件机制；后续真实回归清单仍按 docs/14–19 执行。

官方 SDK 的请求过滤器使用锁定的 2.3.1 API：[McpRequestFilterBuilderExtensions](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.1/src/ModelContextProtocol/McpRequestFilterBuilderExtensions.cs)。未引入第三方 PLC MCP。
