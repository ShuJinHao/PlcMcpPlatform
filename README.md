# PLC MCP Platform

让 **OpenCode 通过 MCP 操作 InoProShop，生成、修改和编译汇川 PLC 工程**。

OpenCode 负责理解需求、生成程序与迭代修复；本项目负责工程访问、能力发现、任务管理、厂商执行与真实结果反馈。后续通过版本化适配器支持西门子、欧姆龙。

**产品定位：面向 PLC 工程自动化的 MCP Server。** 仓库名称中的 Platform 表示可扩展的 PLC 工程能力，不表示通用 MCP 管理平台。对外由一个 MCP 服务提供工程工具；内部汇川模块是自主实现的适配器和脚本执行器，不是另一个 MCP Server。厂商 Worker 使用内部执行协议，不承担 MCP 工具发现或代理转发。

通用 MCP 服务目录、第三方服务托管/聚合、组织级网关和模型网关不属于本项目。未来需要这些功能时可评估 Obot 等已有产品接入本服务；PLC 工程修改、编译诊断和恢复仍由本项目负责。详见[产品与协议边界](docs/02-architecture.md#产品与协议边界)。

## 当前阶段 P1 离线执行开发

已实现持久化任务、串行调度、进程执行器、七个工程/任务工具和原创工程读取/编译脚本。默认生产宿主仍只注册 `plc_capabilities_list` 与 `plc_runtime_get_status`，汇川能力全部为 `Unverified`，`engineeringReady=false`。

独立的 `tests/PlcMcp.TestHost` 注册九个工具，在 Mac、Windows、Linux 上用明确标识的测试进程验证完整流程。测试宿主不调用 InoProShop，也不编译 PLC 程序。生产宿主不包含 fixture 依赖或自动模拟回退。

**当前尚不能生成、修改或保存真实 PLC 工程。** 原创脚本已包含官方 API 调用，但真实 InoProShop/IronPython 兼容性待验收；程序写入、备份和完整修订约束属于下一批。SQLite 已接入 Host，保存任务、工作区元数据与执行结果。

开始使用见 [第一批实现与 Mac/Windows 启动说明](docs/12-execution.md)，开发路线见 [下一阶段计划](docs/11-offline-development-plan.md)。CI 运行锁定还原、Release 构建、.NET/Python 测试及真实模式/测试模式 HTTP 验证，执行环境包含 Windows、Ubuntu、macOS；真实 IDE 与 OpenCode 客户端验收单独记录。

## 文档入口

| 文档 | 回答的问题 |
|---|---|
| [第一阶段计划](docs/00-first-step-plan.md) | 本次交付什么，下一步怎么推进 |
| [需求文档](docs/01-requirements.md) | 用户要完成什么，如何验收 |
| [架构设计](docs/02-architecture.md) | DDD 边界、依赖方向、进程与数据归属 |
| [技术选型](docs/03-technology.md) | 选型、锁定版本、依据与限制 |
| [模块设计](docs/04-modules.md) | 每个模块的职责、接口与当前实现状态 |
| [汇川能力矩阵](docs/05-inovance-capabilities.md) | 全部能力范围与实际验证要求 |
| [MCP 与 Worker 契约](docs/06-contracts.md) | 工具、错误、幂等、异步任务与通信 |
| [验收方案](docs/07-validation.md) | 自动测试和真实 IDE 验收方法 |
| [开发与 OpenCode 接入](docs/08-development.md) | 如何构建、启动和连接 |
| [架构决策记录](docs/09-decisions.md) | 关键取舍与调整条件 |
| [官方接口对照与原创探针](docs/10-official-api-baseline.md) | CHM 与官网如何对应，如何验证目标 IDE 接口 |
| [下一阶段开发与交付计划](docs/11-offline-development-plan.md) | 无汇川环境时先开发什么，各批如何交付与验收 |
| [第一批执行实现与使用说明](docs/12-execution.md) | 已实现什么、Mac 如何运行、故障处理与验证边界 |

## 核心工程

| 位置 | 项目 |
|---|---|
| `src/shared` | `PlcMcp.SharedKernel` |
| `src/core` | `PlcMcp.Core.Workspaces`、`PlcMcp.Core.Automation` |
| `src/services` | `PlcMcp.Services.Contracts`、`PlcMcp.EngineeringService` |
| `src/infrastructure` | `PlcMcp.EntityFrameworkCore`、`PlcMcp.Execution` |
| `src/adapters` | `PlcMcp.Inovance` |
| `src/hosts` | `PlcMcp.McpHost` |
| `tests` | `PlcMcp.Tests`、独立 `PlcMcp.TestHost` |

按 `Core / Services / Contracts / Infrastructure / Host` 分层，领域对象通过行为维护状态，应用层通过接口访问外部依赖，Host 作为组合根。

## 快速启动

准备 .NET 8 SDK。在 PowerShell 中：

```powershell
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
dotnet run --project src/hosts/PlcMcp.McpHost --no-launch-profile
```

Host 固定监听 `http://127.0.0.1:5088`，MCP 路径为 `/mcp`。OpenCode 配置参考 [示例](config/opencode.example.json)，从另一个继承相同 `PlcMcp__ApiKey` 的终端启动 OpenCode。密钥不写入仓库。完整步骤见开发文档。

第一批已落地任务持久化、执行器、读取与编译调用流程；下一项工程交付是程序写入、备份、读回和保存，再扩展其他工程能力。Windows/InoProShop 验证不阻塞这些开发工作，实际支持声明仍须真实 IDE 证据。所有编程语言均在能力范围内，具体实现路径按证据决定。详见[下一阶段计划](docs/11-offline-development-plan.md)。
