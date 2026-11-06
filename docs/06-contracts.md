# MCP 与 Worker 契约

## MCP 工具与宿主

端点为 `http://127.0.0.1:5088/mcp`，协议由官方 C# SDK 实现。默认真实宿主注册 `plc_capabilities_list`、`plc_runtime_get_status` 两个查询工具；真实汇川能力尚未验证，不注册工程工具。

独立测试宿主共 73 个工具，以下是第一批七个工程/任务工具，复用生产用例与基础设施，执行结果显式标识 fixture/simulated。它们有实际实现，不是返回常量成功的占位工具；当前不能据此宣称目标 InoProShop 已支持。

| 工具 | 主要输入 | 结果 |
|---|---|---|
| `plc_project_open` | projectPath、idempotencyKey、expectedRevision、timeoutSeconds | 打开校验任务；会话在任务结束关闭 |
| `plc_project_get_structure` | projectPath、idempotencyKey、expectedRevision、maxObjects、timeoutSeconds | 工程对象树任务，包含截断标识 |
| `plc_pou_read` | projectPath、objectId、idempotencyKey、expectedRevision、timeoutSeconds | 声明与实现读取任务 |
| `plc_project_compile` | projectPath、applicationId、idempotencyKey、expectedRevision、timeoutSeconds | 指定 Application 编译任务及本次诊断 |
| `plc_job_get` | jobId | 持久化状态与已有结果 |
| `plc_job_cancel` | jobId | 取消请求后的实际状态；CancelRequested 不表示已停止 |
| `plc_job_get_result` | jobId | 终态结果；失败、取消、未知或未完成时 isError=true |

第二批新增以下 13 个工具；编辑工具返回 `contractVersion=1.1`，既有工具维持 1.0，内部 Worker 主版本仍为 1（可选字段扩展）。

| 工具 | 主要输入 | 结果 |
|---|---|---|
| `plc_templates_list` | 无 | 模板描述、t2 修订或该模板错误 |
| `plc_workspace_get` | workspaceName | 当前 m2 修订、版本工程路径、文件清单、交付标记 |
| `plc_project_create` | workspaceName、templateId、idempotencyKey、expectedTemplateRevision? | 从登记源复制并保存重开，创建工作区任务 |
| `plc_project_save` | workspaceName、expectedRevision、idempotencyKey | 新交付版本任务，不表示编译/绑定/运行验收 |
| `plc_pou_create` | workspaceName、expectedRevision、parentId、name、pouType、language、declaration、implementation?、returnType?、idempotencyKey | 新 POU 与版本；图形语言仅空对象 |
| `plc_pou_update` | workspaceName、expectedRevision、objectId、declaration?、implementation?、idempotencyKey | 候选修改、保存重开后发布版本 |
| `plc_gvl_create` | workspaceName、expectedRevision、parentId、name、declaration、idempotencyKey | 创建 GVL 任务 |
| `plc_gvl_read` | projectPath、objectId、idempotencyKey、expectedRevision? | GVL 声明读取任务，修订是主文件 SHA |
| `plc_gvl_update` | workspaceName、expectedRevision、objectId、declaration、idempotencyKey | 更新声明任务 |
| `plc_dut_create` | workspaceName、expectedRevision、parentId、name、dutType、declaration、baseType?、idempotencyKey | 创建 DUT 任务 |
| `plc_dut_read` | projectPath、objectId、idempotencyKey、expectedRevision? | DUT 声明读取任务，修订是主文件 SHA |
| `plc_dut_update` | workspaceName、expectedRevision、objectId、declaration、idempotencyKey | 更新声明任务 |
| `plc_job_reconcile` | jobId | 根据服务已有证据确认未知任务；无证据时保持阻塞 |

查询工具返回 value/error；异步工程操作返回 job/error。写入成功的 job.result 包含 baseRevision、revision、revisionScope、projectPath、backupPath、candidatePath、published、isDelivery、steps、engineeringResult 与依赖验证范围。候选失败返回 published=false 与已知路径/步骤，不承诺候选未修改。详细语义见 [第二批说明](13-managed-editing.md)。

可选参数与实际 Schema 以 [mcp-tools.v1.json](../contracts/mcp-tools.v1.json) 为准。结果统一携带 contractVersion、executionMode、evidenceKind、engineeringReady、job、error。job 内保留 jobId、workspaceId、operation、status、errorCode、result 和时间；错误具有 code、message、retryable。查询一个 Failed 任务本身可以成功，而取其业务结果必须返回错误语义。

工具注解只是客户端提示，不能替代服务端鉴权、路径限制和业务校验。提交任务的 Tool 标为 ReadOnly=false，因为会持久化执行意图；第一批脚本不修改 PLC 程序内容。

## 幂等与异步任务

工程操作先持久化，快速返回 jobId。客户端轮询任务工具，不依赖 OpenCode 的特定 MCP Tasks 扩展。HTTP 断连不自动取消已经接受的任务。

幂等身份为 `(workspaceId, operation, idempotencyKey)`，指纹包含规范化路径、操作参数、预期修订与超时。同键同请求返回原任务，包括源文件后来被删除的情况；同键不同请求返回 `idempotency_conflict`。默认最多 256 个未结束任务，队列满时不会拒绝查询/重试已有请求。

读取/编译的 expectedRevision 是主工程文件 SHA-256，未指定时采集文件修订。创建的 expectedTemplateRevision 是可选 t2 模板修订；更新/保存必须使用 plc_workspace_get 的 m2 修订。m2 覆盖版本身份及已复制文件清单，不能替代 IDE 会话状态、全局库/设备仓库验证。不同作用域的修订不能混用。

## 错误语义

| 错误码 | 含义 |
|---|---|
| `invalid_request` | 参数、对象 GUID、数量或超时范围不合法 |
| `job_not_found` / `job_not_finished` | 任务不存在或还没有终态结果 |
| `path_not_allowed` / `project_unavailable` | 路径越界/链接，或文件不存在、不可读、被占用 |
| `workspace_revision_conflict` | 工程主文件与请求修订不一致 |
| `idempotency_conflict` / `queue_full` | 同键请求内容冲突，或执行队列已满 |
| `ide_unavailable` / `capability_unverified` | IDE 环境不可用，或尚无真实版本支持证据 |
| `compile_failed` | 当前编译有错误，诊断保留在结果中；fixture 来源表示模拟诊断 |
| `expected_revision_required` / `workspace_not_found` / `workspace_exists` | 缺少写入修订、工作区未创建或名称已存在 |
| `template_unavailable` / `invalid_template` | 模板不可用或执行模式不匹配 |
| `object_kind_mismatch` / `object_name_conflict` | 对象种类与请求不匹配，或同级名称冲突 |
| `graphical_content_requires_specialized_editor` | 不能用文本写入图形网络 |
| `save_failed` / `reopen_readback_mismatch` | 候选保存失败，或重开后读回不一致，不能发布 |
| `reconciliation_unproven` / `reconciliation_conflict` / `reconciliation_raced` | 终止证据不足、文件事实冲突或证据并发变化，不能据此解除阻塞 |
| `execution_outcome_unknown` | 结果无法确认，执行槽阻塞，禁止自动重放 |

Worker 还会返回更具体的原因，例如 `worker_protocol_error`、`execution_timeout`、`project_close_unconfirmed`。任务进入 OutcomeUnknown 时对外统一给出结果未知，运行状态保留执行槽阻塞原因。无自动重试写操作或强制解锁工具；plc_job_reconcile 的有限恢复必须匹配持久化证据。

应用层使用 `Result<T>`；MCP 工具将业务失败映射为 `isError=true` 和结构化错误。SDK 处理协议层错误。未知状态、退出码 0、没有错误文本都不能单独证明工程操作成功。

## Worker v1

正式 JSON Schema 见 [worker-v1.schema.json](../contracts/worker-v1.schema.json)。请求包含 protocolVersion、requestId、jobId、operation、workspaceId、expectedRevision、deadlineUtc、executionMode、payload；结果回传关联 ID、outcome、errorCode、executionMode、payload。

- 协议为每次受控进程调用的 UTF-8 JSON 请求/结果文件，分别限制 128 KiB 与 1 MiB。结果先写临时文件再原子改名。
- 请求路径和结果路径由服务创建，以子进程环境变量传给 ScriptEngine，不能由 MCP 调用者指定任意脚本或命令。
- outcome 为 succeeded、failed、cancelled、unknown。校验版本、关联 ID、模式、结果形状、修订及编译诊断一致性；错误结果不能被当作成功。
- 日志与协议文件分离；标准输出/错误输出按固定缓冲区排空。真实 IDE 的父进程结束不保证所有委托执行终止，因此强制终止后保守返回结果未知。
- 主版本不匹配拒绝；新增可选字段需要同步 Schema 与契约测试。既有字段不能在同一版本改变含义。

该协议不引用 CLR 类型名或 net8 DLL。未来旧运行时 Worker 可以独立实现相同契约。当前未采用 Named Pipe、HTTP 子服务或第二层 MCP。

## 第三批与下一批能力

第三批已新增 POU 成员、对象、设备/任务/库的 27 个工具，完整清单见 [第三批说明](14-engineering-configuration.md)。默认生产宿主仍仅两个查询工具；真实验证宿主按白名单执行，结果始终 real/unverified。第四批已增加有界 XML 交换与 graph-v1 内容编辑，范围见 docs/15。不提供通用 execute_python 代替这些业务接口。第一批行为与使用限制见 [执行说明](12-execution.md)。

## 编辑 Worker 扩展

外层托管任务以 workspace 路径和 t2/m2 修订记录意图。ManagedProjectExecutor 将候选实际文件路径、主文件 SHA、candidate=true、edit、ideProfile、requirements 传入原始 Worker；外层 project.create/save 转为内部 project.verify。原始 Worker 不接受任意输出路径或绕开候选的写入请求。

成功写入必须包含保存、重开、读回、自有会话关闭证明，以及请求内容/对象 ID 和修订匹配；只返回退出码 0 或 saved=true 不足以发布。dependencies 明确 controllerMatched、libraryReferencesMatched 与 installedDependenciesVerified=false，后者不能被错误提升成真实安装验证。

## P3 契约扩展

ConfigurationTools 返回版本 1.2。查询参数增加 query，写入增加结构化 configuration；不能提交任意脚本代码。工程结果形状为 configuration={operation,objectId,data}，再包在托管版本结果 engineeringResult 内。依赖检查增加 dependencySnapshot/currentDependencies；模板初始要求与当前实际引用分别存储。运行状态增加 validation，版本来源仍标 versionsVerified=false。

具体错误包括：operation_not_in_validation_scope、dependency_snapshot_conflict、dependency_readback_mismatch、task_program_resolution_conflict、task_reference_conflict、task_call_representation_unverified、member_type_evidence_required、property_accessor_unavailable、protected_controller、library_not_installed、protected_system_library、device_description_not_installed、parameter_not_read_write、parameter_out_of_range、parameter_type_unverified、invalid_io_expression。均无自动写入重试；确切代码与参数限制以实现和 Schema 为准。

## P4 契约扩展

9 个新工具返回 contractVersion=1.3，清单与规则见 [docs/15](15-engineering-exchange.md)。Worker 主版本仍为 1：读取参数增加 exchange={format,objectIds,recursive}，写入 edit.exchange 包含 artifactId、format、xml、conflictPolicy、allowWarnings、expectedGraphSha256。上层制品存储不向调用者开放任意文件路径。

原始 Worker 的 exchange 结果包括格式、XML、对象标识和报告；ExchangeExecutor 校验后保存为制品，将成功结果中的原始 XML 替换成 artifact，图形读写另返回 graph。失败保留已取得的报告与候选证据。一般导入 reexportVerified 仅证明导入后与重开后的导出一致；图形写入另比对源模型语义，均不表示 PLC 运行验收。

新增错误包括 invalid_exchange_xml、exchange_structure_too_large、artifact_integrity_mismatch、exchange_reported_error、exchange_warning_requires_acknowledgement、exchange_reopen_mismatch、graph_revision_conflict、graph_contains_unrepresented_data、graph_object_has_children、graph_semantic_readback_mismatch。未知执行仍按原状态机隔离，不自动重放。独立 [graph-v1 schema](../contracts/graph-v1.schema.json) 与 MCP 参数快照一起核对，领域约束由 GraphRules 和编解码器额外执行。

## P5 契约扩展

新增 9 个工具返回 contractVersion=1.4：plc_project_batch_preview/apply、plc_batch_get、plc_exchange_upload_begin/append/complete/abort、plc_delivery_create/read。既有工具结构不变，运行状态的版本和阶段更新。独立 [batch-v1 schema](../contracts/batch-v1.schema.json) 与 MCP 工具快照一起核对；步骤数量、操作字段、前序引用和 Application 范围还需运行时验证。

batch 计划摘要绑定 workspace、m2 修订与规范化内容；原有幂等键规则不变。上传键绑定格式、UTF-8 长度和摘要；分块偏移是字节。交付任务绑定当前 m2 修订，返回 ZIP 身份和主文件摘要；分块下载需完整 ZIP SHA 验证。

Worker 仍为主版本 1。新增 batch/package 属于应用协调操作，不作为新方法传给厂商脚本。队列中的 edit.exchange 可省略 xml，执行时验证 artifactId 后加载；线上传输允许固定 file={name,sha256,byteLength}，与非空 xml 互斥。侧文件只在单次私有调用目录中读取，默认 8 MiB、最高 64 MiB；JSON 请求/响应仍限制 128 KiB / 1 MiB。新 Host 和三份配套脚本须一起发布。

新增错误包括 batch_plan_changed、batch_reference_not_ready、batch_application_scope_mismatch、batch_scope_incomplete、task_inventory_incomplete、invalid_upload_request、upload_offset_conflict、upload_chunk_conflict、upload_incomplete、upload_hash_mismatch、upload_requires_utf8、invalid_exchange_file、exchange_file_integrity_mismatch、invalid_delivery_id、delivery_integrity_mismatch。失败、取消、未知状态均沿用既有任务语义。完整范围、事务含义与示例见 [docs/16](16-engineering-delivery.md)。

## P6 契约扩展

新增 6 个工具返回 contractVersion=1.5：plc_project_adopt_preview、plc_project_adopt、plc_project_context、plc_project_history、plc_project_diff、plc_project_restore。batch-v1 增加 text.patch 步骤；旧工具和 Worker 主版本保持兼容。源码摘要覆盖完整 LF 规范化 UTF-8 文本，窗口位置是 UTF-16 单元；工程 a1/m2 和主文件 SHA 不可混用。

Worker 输入新增 adoption/restoreRevision/patch/incremental 可选字段。project.adopt/restore/context/diff/text.patch 是应用协调操作，仅 object.source.read 是新增原始脚本端口。MCP 工具、batch 与 Worker Schema 已同步。graph diff 的 toObjectId 用于显式配对 XML 替换后改变的对象 GUID。

新增错误包括 adoption_preview_required、invalid_source_name、source_hash_conflict、patch_ambiguous、patch_old_text_missing、patch_object_kind_not_supported、context_scope_incomplete、object_application_scope_mismatch、version_not_found、restore_application_scope_invalid、task_binding_unresolved。失败候选不会推进头，未知结果不可自动重试。完整协议语义见 docs/17。
