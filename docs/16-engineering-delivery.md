# 第五批：批量工程修改、大文件交换与版本交付

> 本文保留对应批次的实现记录。当前工具数量、启动和剩余范围以 [P10 交付说明](21-delivery-closure.md)、[工具清单](22-current-tools.md) 和 [能力缺口](23-capability-closure.md) 为准。

目标仍是 OpenCode 调用本项目 MCP，生成和修改 PLC 工程。本批把多个对象的编辑汇成一个可审阅的计划，把大 XML 从请求正文移到受控文件，并提供可校验的工程交付包。没有增加模型服务或下游汇川 MCP。

TestHost / ValidationHost 新增 9 个工具，总数为 **67**；默认生产宿主保持 2 个查询工具。新增工具契约版本为 1.4，既有工具的输入/输出结构保持兼容。汇川能力保持 `Unverified`、`engineeringReady=false`；代码、fixture、CPython 和 OpenCode 连接检查均不能代替目标 InoProShop/IronPython 验收。

## 1. 批量修改

`plc_project_batch_preview` 接收 `workspaceName + expectedRevision + plan`。plan 指定一个 Application，以及按依赖顺序排列的 1～32 个步骤。预览检查参数、前向引用和图形制品，并返回绑定工作区、基准修订及规范化计划的 `planSha256`。它不启动 IDE，不能提前证明对象名称、厂商类型和编译正确。

`plc_project_batch_apply` 提交相同计划和 `expectedPlanSha256`，返回持久化 job。`plc_batch_get` 联合读取 job 与逐步日志；最终是否发布以 job 成功结果的 `published/revision` 为准，日志的 `verified` 只是内部验证状态。

| 支持的步骤 operation | 输入要点 |
|---|---|
| `dut.create/update` | 父对象或目标 GUID、声明、DUT 类型；别名类型提供 baseType |
| `gvl.create/update` | 父对象或目标 GUID、声明 |
| `pou.create/update` | 类型、语言、声明/文本实现；Function 的返回类型使用 baseType |
| `pou.graph.write` | 事先生成的 artifactId；更新还需原 objectId 和 expectedGraphSha256 |
| `task.configuration.create` | Application 或其中适用的父对象 |
| `task.create/update` | TaskSettings；创建使用父任务配置对象 |
| `task.calls.set` | 完整调用列表，保留真实 POU 身份与名称 |

共 11 种步骤操作。批量暂不包含设备/库修改、删除、移动、一般 XML 导入和工程创建；这些已有独立工具。计划不是任意脚本容器。Schema 中复用的 configuration DTO 字段不代表所有组态操作都允许进入批量。

步骤 ID 为唯一 IEC 风格标识，最多 48 字符。`parentId`、`objectId`、`configuration.calls[].pouId` 可写 `@前序步骤ID`。例如创建 `configuration` 后，Task 的父对象填 `@configuration`；创建 `program` 后，调用列表中的 pouId 填 `@program`。仅对象标识会替换，声明、程序文本和名称不会插值。服务按给定顺序执行，不替模型猜测源码依赖；前向、循环或重复引用会拒绝。

执行前完整读取 Application 子树，截断则停止；父对象、目标对象和绑定 POU 必须在该范围内。后续创建的真实 GUID 加入可引用集合。每一步复用现有官方操作和保存、关闭、重开、读回机制，整批共享一个候选目录。全部步骤完成后再验证工程、完整读取任务绑定并编译指定 Application，最终只发布一个 m2 版本。

这是一批修改的一次发布，并非只调用一次 IDE、只保存一次或一个数据库事务包住所有工程文件。当前每步仍可能启动独立 Worker。超时为整批总预算，最大 600 秒；实际冷启动成本待真实环境验证。

任何确定失败，包括编译失败，均保留原当前头与候选/备份证据。结果未知会隔离执行槽，后续步骤停止；同键重试返回原任务，不从中断位置继续。服务崩溃后逐步日志可以辅助检查，不能作为自动续跑或解锁凭证。批量仍受现有约 112 KiB 入队参数预算限制，源码很多时应拆成明确的交付阶段。

## 2. XML 大文件传递

| 限制 | 当前值 |
|---|---|
| `plc_exchange_stage` 和 graph-v1 XML | 64 KiB UTF-8；图形范围没有随传输扩容而放宽 |
| 通用 XML 制品默认上限 | 8 MiB UTF-8 |
| 配置范围 | `PlcMcp__Exchange__MaxXmlBytes`，64 KiB～64 MiB，修改后重启宿主 |
| 上传分块 | 每块最多 32,768 个原始字节，base64 传输 |
| 活跃上传 | 最多 32 个，完成或 abort 后释放名额 |
| XML 解析 | 最深 32 层、最多 100,000 个元素；拒绝 DTD/外部实体 |
| Worker JSON | 请求 128 KiB、结果 1 MiB，保持原上限 |

上传顺序为 `plc_exchange_upload_begin → append → complete`。begin 使用稳定 uploadKey、format、UTF-8 字节数和完整 SHA-256；相同键与描述返回已保存偏移。append 只接受准确字节偏移；重复的已写入同内容块可重试，内容或偏移冲突明确拒绝。服务重启后可用同一 begin 恢复。complete 检查总长、摘要、严格 UTF-8（包括带 BOM 的文件）和 XML 结构，才生成不可变 artifact；它不调用 IDE、不导入工程。

`plc_exchange_upload_abort` 删除暂存数据和上传回执，不删除已完成的不可变制品。P5 当时未实现清理；P10 已提供上传、确定失败候选及历史交付 ZIP 的停服预览/执行，详见 docs/21。交换制品和执行证据仍保留。停止中的任务和未知结果的证据不得自动删除。

工程导入的数据库请求只保存制品身份和策略，不复制完整 XML。执行时核验制品并加载；超过 inline 上限后使用调用目录内固定的 `exchange-input.xml` / `exchange-output.xml`，传递名字、字节数和摘要，禁止任意路径。接收方核验固定名称、文件长度、摘要及 UTF-8，再交给原有接口。JSON 与侧文件必须使用同一发布版本的 Worker，不能混用旧脚本。

失败证据中的 XML 也转成制品引用，保留报告。制品保存异常时保留摘要、字节数和明确错误，原始 Worker 文件仍在调用目录供检查。失败原文可能不是有效可导入 XML，制品存在不代表可用。

本地传输辅助脚本不需要模型把大段 base64 逐字生成：

```sh
python eng/transfer.py upload ./program.xml --format plcopen --key design-001
```

使用环境变量 `PLCMCP_API_KEY`，不要把密钥写进命令或提交。脚本只上传，不自动导入；得到 artifactId 后再由工程工具按工作区修订执行导入。XML 下载仍可通过 `plc_exchange_read` 分块读取文本，偏移为返回的 UTF-16 单元；上传偏移是字节，两者不可混用。

## 3. 工程交付 ZIP

`plc_delivery_create(workspaceName, expectedRevision, applicationId, idempotencyKey)` 针对当前准确的 m2 版本排队。服务验证源清单，在私有副本中读任务配置和编译，确认副本清单、源清单及当前头没有变化后打包。编译失败或任务清单截断均不交付；它不会推进工作区头。

| ZIP 路径 | 内容 |
|---|---|
| `project/…` | 已发布版本清单内的所有工程文件，逐项校验长度与 SHA-256 |
| `manifest.json` | m2 修订、主文件摘要、文件清单、模板要求、当前依赖引用、执行来源和验证标签 |
| `reports/compile.json` | 该副本、该主文件摘要、指定 Application 的编译结果与诊断 |
| `reports/tasks.json` | 对应版本的任务设置与 POU 调用绑定 |

工程总文件上限仍为 256 MiB，ZIP 上限为 272 MiB，单报告最多 1 MiB。交付不会自动包含已安装在系统目录中的商业库、设备包、IDE、许可证或运行时。若 IDE 编译产生或改变额外文件，当前实现保守拒绝 `workspace_revision_conflict`，不悄悄改变交付版本；真实版本的缓存策略需要另行验证。

`plc_delivery_read` 返回最多 32 KiB 原始数据的 base64 块及块摘要。完整 ZIP 的 artifactId 与 SHA-256 相同。下载必须同时校验各块与最终摘要，辅助脚本已实现：

```sh
python eng/transfer.py download ARTIFACT_ID --sha256 ZIP_SHA256 --output ./delivery.zip
```

不覆盖现有目标或 `.part` 文件；失败时保留 partial 供人工检查。发布本地下载使用同文件系统硬链接完成无覆盖提交，目标文件系统须支持。ZIP 完整不代表 PLC 运行正确；fixture ZIP 标 `simulated`，真实验证包仍标 `unverified`，两者均 `engineeringReady=false`。

## 4. 发布、启动与集中回归

仓库需要 .NET 8 SDK、Python 3.10+。代码统一检查入口：

```sh
python eng/verify.py
```

PowerShell 可运行 `./eng/verify.ps1`。入口串行执行锁定还原、Release 构建、标准 .NET 测试、六组 HTTP 检查、发布检查和 Python 脚本测试，失败即停。5088 端口必须空闲；检查不会停止用户已有宿主。

创建供本机运行的 framework-dependent 发布目录，不部署或安装 Windows 服务：

```sh
python eng/publish.py --mode fixture --output artifacts/publish/fixture
python eng/publish.py --mode production --output artifacts/publish/production
python eng/publish.py --mode validation --output artifacts/publish/validation
```

输出目录必须不存在。发布包含 bin、脚本、配置样例、契约、文档及 release.json，记录源提交、工作区是否未提交和所有文件摘要。依赖锁文件保留在仓库用于构建，不复制为运行时内容；不会关闭锁定还原或发布冲突检测。清单提供完整性检查，不是签名或软件来源认证。当前没有捆绑 .NET 运行时或汇川安装包。

Mac 运行 fixture 示例，先按本地环境设置 `PlcMcp__ApiKey`：

```sh
python artifacts/publish/fixture/eng/init-fixture.py --workspace-root .plcmcp/p5/workspaces
python artifacts/publish/fixture/eng/start.py --bundle artifacts/publish/fixture --data-directory .plcmcp/p5/data --workspace-root .plcmcp/p5/workspaces --check-only
python artifacts/publish/fixture/eng/start.py --bundle artifacts/publish/fixture --data-directory .plcmcp/p5/data --workspace-root .plcmcp/p5/workspaces
```

start 检查发布摘要、密钥是否设置、.NET 8 ASP.NET Core 运行时、数据位置与宿主模式；数据和工程须放在发布目录之外。validation 模式还要求 Windows、实际可执行文件、已填写的版本/Profile 与本地白名单。它不把安装路径存在视为 API 验证。先在工程师会话中验证 IDE 行为，Windows 服务账户/session 0 部署尚未验收。

另一个继承相同密钥的终端：

```sh
python eng/doctor.py --mode fixture --report .plcmcp/reports/doctor.json
python eng/run-regressions.py --suite all --expect-mode fixture --variables config/regression/fixture-variables.json --report .plcmcp/reports/all.json
```

doctor 只做 MCP 初始化、工具契约、模式和调度状态查询，不修改工程、不解除未知任务。run-regressions 顺序执行 P3 的 42 步、P4 的 38 步、P5 的 18 步；每组使用独立 runId，首个失败停止。真实 Windows 后续改用 `--expect-mode real` 和本地变量文件，且必须运行 ValidationHost。真实报告可能包含源码、路径和厂商诊断，只保存在本地。

P5 场景涵盖 DUT/GVL/FB/Program/Task 的依赖生成、计划摘要冲突、重复对象失败保护、混合文本与 LD、旧版本拒绝和版本打包。大 XML Unicode 分块、进程侧文件、完整 ZIP 哈希另外在 verify-delivery.py 中验证。

## 5. OpenCode 接入证据

继续使用 [opencode.example.json](../config/opencode.example.json) 的 remote MCP 配置。CI 固定官方 `opencode-ai@1.19.5`，在独立临时配置目录执行 `opencode mcp list`；已观察到 `plc connected`。同一端点的 HTTP 契约检查为 67 个工具。脚本不传模型提供商凭据、不调用模型。主版本变化需重新核对配置，不把此结果扩大到所有 OpenCode 版本。

本机已有 CLI 后可运行 `python eng/verify-opencode.py --opencode opencode`；它验证连接，不自动生成程序。完整生成提示与验收记录见 [OpenCode 批量场景](../config/opencode-delivery-scenario.md)。实际模型的工具选择、需求理解、修复循环、真实 IDE 工程生成与 PLC 运行均是独立待验收项。

## 6. 实现位置、边界与验收

调用组合为 `DeliveryExecutor → ManagedProjectExecutor → BatchExecutor → ExchangeExecutor → 厂商/fixture Worker`。package 在最外层建立只用于验证的副本；batch 在候选协调器内复用已有修改步骤；Exchange 仍在发布前完成图形语义比较。DeliveryTools 只映射输入输出，状态机、数据库所有权和发布事务沿用既有实现。

批量日志、上传暂存、XML 制品与 ZIP 分别保存在 data 下的 batches、uploads、exchange、deliveries；工程内容事实来源仍是版本目录。没有新增第二套 PLC 工程数据库、工作流平台或 ORM 镜像。

自动检查为 81 项 .NET、71 项 Python、六组 HTTP 检查与发布回归；各提交实际状态以 [PR #6 checks](https://github.com/ShuJinHao/PlcMcpPlatform/pull/6/checks) 为准。Windows/Ubuntu/macOS 验证服务代码与 fixture，Linux 额外验证真实 OpenCode CLI 连接。真实 Windows/InoProShop/IronPython 尚未验收。

复杂 SFC 分支/定时限定符、现存图形 POU 子成员编辑、CFC 结构化编辑和 EtherCAT PDO 布局没有在本批伪装成已完成；本批官网调查与继续实施条件见 [官方基线](10-official-api-baseline.md#p5-继续调查的边界)。
