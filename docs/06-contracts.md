# MCP 与 Worker 契约

## MCP 工具与宿主

端点为 `http://127.0.0.1:5088/mcp`，协议由官方 C# SDK 实现。默认真实宿主注册 `plc_capabilities_list`、`plc_runtime_get_status` 两个查询工具；真实汇川能力尚未验证，不注册工程工具。

独立测试宿主已注册以下七个新增工具，复用生产用例与基础设施，执行结果显式标识 fixture/simulated。它们有实际实现，不是返回常量成功的占位工具；当前不能据此宣称目标 InoProShop 已支持。

| 工具 | 主要输入 | 结果 |
|---|---|---|
| `plc_project_open` | projectPath、idempotencyKey、expectedRevision、timeoutSeconds | 打开校验任务；会话在任务结束关闭 |
| `plc_project_get_structure` | projectPath、idempotencyKey、expectedRevision、maxObjects、timeoutSeconds | 工程对象树任务，包含截断标识 |
| `plc_pou_read` | projectPath、objectId、idempotencyKey、expectedRevision、timeoutSeconds | 声明与实现读取任务 |
| `plc_project_compile` | projectPath、applicationId、idempotencyKey、expectedRevision、timeoutSeconds | 指定 Application 编译任务及本次诊断 |
| `plc_job_get` | jobId | 持久化状态与已有结果 |
| `plc_job_cancel` | jobId | 取消请求后的实际状态；CancelRequested 不表示已停止 |
| `plc_job_get_result` | jobId | 终态结果；失败、取消、未知或未完成时 isError=true |

可选参数与实际 Schema 以 [mcp-tools.v1.json](../contracts/mcp-tools.v1.json) 为准。结果统一携带 contractVersion、executionMode、evidenceKind、engineeringReady、job、error。job 内保留 jobId、workspaceId、operation、status、errorCode、result 和时间；错误具有 code、message、retryable。查询一个 Failed 任务本身可以成功，而取其业务结果必须返回错误语义。

工具注解只是客户端提示，不能替代服务端鉴权、路径限制和业务校验。提交任务的 Tool 标为 ReadOnly=false，因为会持久化执行意图；第一批脚本不修改 PLC 程序内容。

## 幂等与异步任务

工程操作先持久化，快速返回 jobId。客户端轮询任务工具，不依赖 OpenCode 的特定 MCP Tasks 扩展。HTTP 断连不自动取消已经接受的任务。

幂等身份为 `(workspaceId, operation, idempotencyKey)`，指纹包含规范化路径、操作参数、预期修订与超时。同键同请求返回原任务，包括源文件后来被删除的情况；同键不同请求返回 `idempotency_conflict`。默认最多 256 个未结束任务，队列满时不会拒绝查询/重试已有请求。

当前 expectedRevision 表示主工程文件 SHA-256；它不是完整工程/IDE 会话的事务修订，不能直接支撑下一批写入。未指定时，提交时采集该文件修订，执行前后再次检查。

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
| `execution_outcome_unknown` | 结果无法确认，执行槽阻塞，禁止自动重放 |

Worker 还会返回更具体的原因，例如 `worker_protocol_error`、`execution_timeout`、`project_close_unconfirmed`。任务进入 OutcomeUnknown 时对外统一给出结果未知，运行状态保留执行槽阻塞原因。无自动重试写操作或自动解锁工具。

应用层使用 `Result<T>`；MCP 工具将业务失败映射为 `isError=true` 和结构化错误。SDK 处理协议层错误。未知状态、退出码 0、没有错误文本都不能单独证明工程操作成功。

## Worker v1

正式 JSON Schema 见 [worker-v1.schema.json](../contracts/worker-v1.schema.json)。请求包含 protocolVersion、requestId、jobId、operation、workspaceId、expectedRevision、deadlineUtc、executionMode、payload；结果回传关联 ID、outcome、errorCode、executionMode、payload。

- 协议为每次受控进程调用的 UTF-8 JSON 请求/结果文件，分别限制 128 KiB 与 1 MiB。结果先写临时文件再原子改名。
- 请求路径和结果路径由服务创建，以子进程环境变量传给 ScriptEngine，不能由 MCP 调用者指定任意脚本或命令。
- outcome 为 succeeded、failed、cancelled、unknown。校验版本、关联 ID、模式、结果形状、修订及编译诊断一致性；错误结果不能被当作成功。
- 日志与协议文件分离；标准输出/错误输出按固定缓冲区排空。真实 IDE 的父进程结束不保证所有委托执行终止，因此强制终止后保守返回结果未知。
- 主版本不匹配拒绝；新增可选字段需要同步 Schema 与契约测试。既有字段不能在同一版本改变含义。

该协议不引用 CLR 类型名或 net8 DLL。未来旧运行时 Worker 可以独立实现相同契约。当前未采用 Named Pipe、HTTP 子服务或第二层 MCP。

## 下一批能力

工程创建/保存、POU 创建/更新、GVL/DUT、设备/任务/库、导入导出和各语言编辑仍按能力域开发，尚未注册为工具。不提供通用 execute_python 代替这些业务接口。第一批行为与使用限制见 [执行说明](12-execution.md)。
