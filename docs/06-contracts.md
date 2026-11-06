# MCP 与 Worker 契约

## 已实现的 P0 MCP 工具

端点：`http://127.0.0.1:5088/mcp`；协议实现由官方 SDK 管理。当前只注册以下工具，不注册未实现的工程写入工具。

| 名称 | 输入 | 输出 | 行为 |
|---|---|---|---|
| `plc_capabilities_list` | 无业务参数 | contractVersion、stage、capabilities | 返回能力规划与验证状态 |
| `plc_runtime_get_status` | 无业务参数 | contractVersion、stage、ideConnected、engineeringReady、reason | 真实说明尚未连接 IDE |

两者均声明只读、非破坏、幂等。工具注解是给客户端的元数据，不能替代服务端鉴权和执行校验。

## 工程工具规划（尚未注册）

| 能力组 | 规划工具 | 主要输入/结果 |
|---|---|---|
| 工作区/工程 | `plc_project_open`、`plc_project_create`、`plc_project_save`、`plc_project_get_structure` | 模板或已批准路径、workspaceId、revision、对象列表 |
| 程序 | `plc_pou_read`、`plc_pou_create`、`plc_pou_update` | 对象 ID、语言、内容、expectedRevision；jobId 或读取结果 |
| 数据定义 | `plc_gvl_*`、`plc_dut_*` | 数据定义与类型校验结果 |
| 设备/任务/库 | `plc_device_*`、`plc_task_*`、`plc_library_*` | 能力专属结构、真实配置与诊断 |
| 交换/编译 | `plc_project_import`、`plc_project_export`、`plc_project_compile` | 格式、对象范围、产物或编译诊断 |
| 执行任务 | `plc_job_get`、`plc_job_cancel`、`plc_job_get_result` | jobId、状态、取消确认、结果 |

规划名称不是最终冻结的 Schema。实现时按工具逐个保存输入/输出 Schema、契约版本、权限、超时、幂等和真实样例，禁止一个通用 `execute_python` 替代全部工具。

## 长任务与幂等

写入与长编译操作先持久化，快速返回 jobId。客户端通过任务工具获取结果；不假定 OpenCode 所有版本都支持 MCP Tasks 扩展。请求断连后任务可继续，取消必须调用任务取消用例。

幂等身份为 `(workspaceId, operation, idempotencyKey)`，同时保存规范化请求指纹。相同键/相同内容返回原任务；同键不同内容返回 `idempotency_conflict`。P0 数据库已有唯一约束，完整提交用例待 P2。

## 错误语义

| 错误码 | 含义 | 建议 |
|---|---|---|
| `capability_unverified` | 目标版本未经验证 | 查询能力，完成验证 |
| `capability_unsupported` | 已确认不支持 | 改用已验证方案或人工处理 |
| `ide_unavailable` | IDE/执行器不可用 | 修复环境后重新检查 |
| `workspace_revision_conflict` | 工程已变化 | 重新读取，重新形成修改 |
| `workspace_busy` | 已被其他执行者占用 | 等待或重新排队 |
| `idempotency_conflict` | 相同键对应不同请求 | 更正请求身份 |
| `compile_failed` | 真实编译出现错误 | 根据结构化诊断修复 |
| `execution_outcome_unknown` | 是否完成无法确认 | 对账；禁止自动重试写操作 |
| `path_not_allowed` | 路径超出授权工作区 | 使用已批准工作区 |

应用层使用 `Result<T>`；MCP 层将工具执行失败映射为 `isError=true` 和结构化错误，不将错误字符串放入看似成功的结果。JSON-RPC 协议错误由 SDK 处理。编译诊断保留错误/警告、对象、位置和工程修订，原始堆栈不暴露给模型。

## Worker v1 草案

请求包含 protocolVersion、requestId、jobId、operation、workspaceId、expectedRevision、deadlineUtc、payload；响应回传关联 ID、outcome、errorCode、payload。兼容规则：未知主版本拒绝；只在同一已验证主版本内增加可选字段；枚举未知值不当作成功。

P2 需确定帧长度、编码、消息大小上限、握手与心跳、执行器身份、所有权租约、取消消息和连接中断语义。Named Pipe 的访问控制限定受信任本机账户；厂商脚本能否参与该链在 P1 验证。

该 JSON 线协议独立于 .NET DLL。未来 .NET Framework Worker 使用自己的 DTO 和 Schema 测试实现；不能直接引用本期 net8.0 Contracts。
