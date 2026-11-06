# PLC MCP Platform

让 **OpenCode 通过 MCP 操作 InoProShop，生成、修改和编译汇川 PLC 工程**。

OpenCode 负责理解需求、生成程序与迭代修复；本项目负责工程访问、能力发现、任务管理、厂商执行与真实结果反馈。后续通过版本化适配器支持西门子、欧姆龙。

## 当前阶段：P0 核心架构

本仓库提供 .NET 8 / C# 12 核心工程与中文设计文档。当前 MCP Host 只开放两个真实只读工具：

| 工具 | 当前行为 |
|---|---|
| `plc_capabilities_list` | 返回汇川能力规划；工程操作全部标为 `Unverified` |
| `plc_runtime_get_status` | 明确返回 `ideConnected=false`、`engineeringReady=false` |

**当前不能生成或编译真实 PLC 工程。** InoProShop Worker、脚本实现、持久化调度和工程写入属于后续阶段；没有模拟成功的工程工具。SQLite 当前提供领域映射与测试，Host 尚未接入运行数据库。

P0 已通过 Windows/Linux 的 Release 构建、9 项测试和 HTTP MCP 冒烟验证，详见 [验收记录](docs/07-validation.md)。

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

## 核心工程

| 位置 | 项目 |
|---|---|
| `src/shared` | `PlcMcp.SharedKernel` |
| `src/core` | `PlcMcp.Core.Workspaces`、`PlcMcp.Core.Automation` |
| `src/services` | `PlcMcp.Services.Contracts`、`PlcMcp.EngineeringService` |
| `src/infrastructure` | `PlcMcp.EntityFrameworkCore` |
| `src/adapters` | `PlcMcp.Inovance` |
| `src/hosts` | `PlcMcp.McpHost` |
| `tests` | `PlcMcp.Tests` |

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

下一项工程交付是：在目标 Windows/InoProShop 版本上验证脚本宿主、工程读取和编译诊断，再实现工程写入闭环。所有编程语言均在能力范围内，具体实现路径按证据决定。
