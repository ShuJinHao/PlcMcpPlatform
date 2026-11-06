# 模块设计

## 当前项目责任

| 项目 | 职责 | 当前已实现 | 后续工作 |
|---|---|---|---|
| SharedKernel | 最小领域标记、结果模型 | 聚合标记、`Result<T>` 与错误结构 | 按真实共性增加，禁止公共工具杂物箱 |
| Core.Workspaces | 平台工作区生命周期 | 注册、修订条件更新、变更阶段、任务与 graph-v1 规则 | 更丰富的工作区访问策略；路径授权在应用/基础设施执行 |
| Core.Automation | 自动化任务状态规则 | 入队、开始、取消、结果未知、证据确认终态 | 扩展恢复场景 |
| Services.Contracts | 用例端口与稳定 DTO | 能力/任务/编辑端口、模板/文件清单/变更证据、Worker v1 与 Schema | 后续工程能力契约 |
| EngineeringService | 工程用例编排 | 查询、幂等提交、调度、候选写入/备份/重开、组态与交换、图形核对、批量编排、发布、交付与证据对账 | 真实版本差异与能力扩展 |
| EntityFrameworkCore | SQLite 持久化实现 | 任务与编辑迁移、领取、恢复隔离、背压、当前头与证据事务 | 保留期与更多审计查询 |
| Execution | 外部执行基础设施 | 路径限制、实例所有权、Channel、进程/JSON、文件清单/副本/版本目录、不可变 XML 制品、分块上传、批量日志与 ZIP 交付 | 实机进程归属与存储保留策略 |
| Inovance | 厂商能力与 ACL | 探针、启动策略、读取/编译/POU/GVL/DUT、组态、交换与保存重开脚本 | 真实 IDE 验收及其他工程能力 |
| McpHost | 组合根与 MCP 协议入口 | HTTP、鉴权、两个默认工具、72 个已实现但生产未开放的工程/任务工具、状态和错误映射 | 验证后按能力开放生产工程工具 |
| Tests | 架构和业务行为证据 | 领域、SQLite、依赖、任务故障注入、真实子进程和 HTTP 契约；独立测试宿主 | 真实 IDE 与 OpenCode 客户端验收 |

## 工程对象模型

`ProjectObject`、`ProgramSource`、`CompileReport` 是外部工程的读模型或结果，不能直接视为可持久化聚合。语言编辑应按能力拆开：文本语言可使用声明/实现；图形语言需要语言专属结构或经过验证的交换格式，不能向 `Implementation` 塞一段 XML 后宣称通用编辑已完成。

对象标识由适配器映射，稳定性范围至少明确为当前工作区/工程修订；名称不是唯一 ID。复制、移动、重命名后的身份变化要在结果中说明。分页和截断属于查询契约，不能把截断结果当成完整工程。

## 厂商适配层

`Inovance` 是本服务内部的厂商适配模块，不是接入平台的另一个 MCP 服务。它通过 C# 应用端口被调用，通过内部执行协议协调 Worker。Worker 负责厂商运行时隔离，脚本负责官方 API 调用；两者均不需要提供 MCP 工具发现、MCP 会话或通用服务代理。未来厂商遵循同一边界。

统一基础语义，保留差异。例如编译返回统一诊断；设备槽位、总线参数、程序组织方式在厂商专属模型中描述。能力身份采用 `vendor + ideVersion + capabilityId`，支持状态与当前可用状态分别核验。未识别版本必须从未验证开始。

汇川适配器依赖版本执行器；执行器只运行受控操作对应的脚本。生产 MCP 不开放任意 Python/命令行执行。能力普查可使用内部开发探针，探针与面向模型的工具注册分离。

`Scripts/inspect_session.py` 是按官方文档编写的内部只读探针，仅检查当前已打开的工程和 API 可见性；不自动打开、关闭、保存、编译或修改工程，探针不用于执行工程任务；实际调用脚本为 `Scripts/engineering_worker.py`，仍待 IDE 验收。使用方法和结果边界见 [官方接口基线](10-official-api-baseline.md)。

## 调度模块

P1 已新增实际承载进程执行的 Execution 基础设施工程。当前采用单实例、单执行槽、有限排队与保守恢复；已有进程测试，P2 可按持久化证据对部分未知写入对账；未声称取得真实 IDE 全局所有权或恢复所有未知场景。

一项编译也是对 IDE 会话的操作，不能因为它属于工程语义的“读”就与写操作无条件并发。HTTP 请求中断不等于取消已经持久化的任务。任务和执行器生命周期应独立于 MCP 会话。

## 扩展流程

1. 记录新厂商/IDE 的运行环境和真实能力。
2. 实现已有能力端口及 ACL，注册适配器；专有能力采用命名空间扩展。
3. 补充协议契约和故障/真实 IDE 验收。
4. 对通过验证的能力登记支持证据，生产 Host 才开放相应工程工具；独立测试宿主按[开发计划](11-offline-development-plan.md)显式标识测试来源。
5. 如需 .NET Framework，创建独立 Worker 工程与构建环境；本期 .NET 8 解决方案不加入无法构建的旧运行时项目。

## P3 模块扩展

- Core.Workspaces 增加任务类型、周期与单位规则。
- Services.Contracts.Configuration 定义按能力区分的请求、依赖快照与结果校验；EngineeringService.Configuration 在入队前校验业务输入。
- ConfigurationTools 只映射 MCP 参数、提交任务和返回结果；复用已有 ManagedEditingJobs / ManagedProjectExecutor，不新建另一套写入通道。
- Inovance 的 configuration_worker.py 隔离厂商对象、集合与参数行为；工程生命周期仍由 engineering_worker.py 管理。
- ValidationHost 是真实集成回归的独立组合根，使用同一个 InovanceExecutor；TestHost 才使用 fixture。
- 外层版本元数据带当前依赖快照，与模板要求分开。新旧 JSON 记录保持可读，无额外数据库迁移。

## P4 模块扩展

- GraphicalProgram 是应用传输与校验使用的领域图形模型，不是 SQLite 聚合或厂商全部对象的副本。
- ExchangeService/ExchangeXml/PlcOpenGraphCodec 承担制品用例、解析与原创 graph-v1 编解码；IExchangeArtifacts 由 Execution 实现。
- ExchangeExecutor 位于候选协调器之内、原始 Worker 之外；真实读回的模型语义验证通过后才允许外层发布。
- exchange_worker.py 封装官方导入导出、冲突与报告回调；工程会话生命周期仍由 engineering_worker.py 统一管理。
- 制品与 Worker 契约、身份变化、范围及验收方法见 [第四批说明](15-engineering-exchange.md)。

## P5 模块扩展

- Services.Contracts.Batch / Delivery 定义计划、进度、交付 manifest 与分块协议；不把 IDE 对象保存为新聚合。
- EngineeringService.Batch 在已有候选生命周期内解析有序引用、记录进度、最终编译；Delivery 在私有副本上准备报告，不改变已发布工程。
- Execution.ExchangeUploads / WorkerExchangeFiles 实现有界传递；BatchJournalStore 和 DeliveryArtifacts 保存进度证据及不可变 ZIP。
- DeliveryTools 增加 9 个协议入口，不新增第二套 MCP 服务。默认宿主的工程工具开放策略不变。
- eng 下的发布、启动、诊断、传输与统一回归脚本用于工程交付；不做模型编排。详情见 docs/16。

## P6 模块扩展

Core 的 TextPatchRules 管理精确文本替换；IncrementalService 负责接入预览、版本查询和任务提交；IncrementalReadExecutor 负责上下文/差异，IncrementalEditExecutor 负责候选中的补丁/恢复验证。IProjectHistoryStore 只暴露历史发布元数据，不泄漏 EF 到服务层。ManagedProjectFiles 增加 imports 声明文件和历史清单校验，ManagedProjectExecutor 仍是唯一工程发布入口。新增 6 个 MCP 工具，合计 73 个；模块位置与输入输出见 docs/17。

## P7 模块补充

`EngineeringQueryReader` / `EngineeringCursor` 提供版本一致的工程读取；`IncrementalEditExecutor` 通过既有 member 端口修改访问器；`BatchScope` 在每步修改后用实际对象树重建作用范围，共享资源修改由 `BatchExecutor` 编译全部 Application。未新增 Agent、模型或通用 MCP 平台模块。见 [P7](18-members-batch.md)。

## P8 模块补充

Core.Workspaces 的 GraphV2Rules / GraphSfcRules 定义类型、隐式标签边、双向端口及结构化 SFC 规则；PlcOpenGraphCodec 扩展 TC6 编解码且保留旧模型语义哈希。IncrementalReadExecutor 将 returnType 纳入差异。既有 ExchangeExecutor/BatchExecutor/ManagedProjectExecutor 继续承担读回、编译与发布；无新增宿主或厂商协议。见 [P8](19-graph-v2-engineering.md)。
