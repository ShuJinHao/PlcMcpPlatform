# PLC MCP Platform

让 **OpenCode 通过 MCP 操作 InoProShop，生成、修改和编译汇川 PLC 工程**。

OpenCode 负责理解需求、生成程序与迭代修复；本项目负责工程访问、能力发现、任务管理、厂商执行与真实结果反馈。后续通过版本化适配器支持西门子、欧姆龙。

**产品定位：面向 PLC 工程自动化的 MCP Server。** 仓库名称中的 Platform 表示可扩展的 PLC 工程能力，不表示通用 MCP 管理平台。对外由一个 MCP 服务提供工程工具；内部汇川模块是自主实现的适配器和脚本执行器，不是另一个 MCP Server。厂商 Worker 使用内部执行协议，不承担 MCP 工具发现或代理转发。

通用 MCP 服务目录、第三方服务托管/聚合、组织级网关和模型网关不属于本项目。未来需要这些功能时可评估 Obot 等已有产品接入本服务；PLC 工程修改、编译诊断和恢复仍由本项目负责。详见[产品与协议边界](docs/02-architecture.md#产品与协议边界)。

## 当前阶段 P3 工程组态与回归入口

在受控工程编辑基础上，已补齐 Task 和 POU 调用绑定、Method/Property/Action、文件夹和对象管理、库引用与占位符、设备与离线参数、基础 IO 变量映射。新增操作复用备份、修订检查、保存重开和新版本发布；工程操作与厂商脚本均自主实现。

独立 `tests/PlcMcp.TestHost` 提供 **49 个工具**，支持 Mac、Windows、Linux 自动开发检查。新增 42 步 HTTP 场景覆盖第三批组态操作。测试 JSON 不是真实工程，模拟诊断不编译 PLC 代码。

`src/hosts/PlcMcp.ValidationHost` 为后续 Windows 集中回归提供真实执行入口：显式版本、Profile 与操作白名单，输出脚本指纹和回归报告。默认生产宿主仍只开放两个查询工具；所有汇川能力仍为 `Unverified`，`engineeringReady=false`。**真实 InoProShop/IronPython 与实际 OpenCode 客户端尚未联调**。

详情见 [第三批实现与回归说明](docs/14-engineering-configuration.md)。Mac 的初始模板和编辑启动继续使用 [第二批说明](docs/13-managed-editing.md)。CI 对 Windows、Ubuntu、macOS 运行锁定还原、Release 构建、标准 .NET/Python 测试及四组 HTTP 检查。第四批继续 XML 交换与图形内容编辑，不增加没有实现的工具。

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
| [第一批执行实现与使用说明](docs/12-execution.md) | 读取/编译、调度与进程边界 |
| [第二批受控工程编辑](docs/13-managed-editing.md) | Mac 编辑启动、模板、备份、修订、保存与对账 |
| [第三批工程组态与真实回归](docs/14-engineering-configuration.md) | 任务、成员、对象、库、设备/IO、Windows 验证入口和自动报告 |

## 核心工程

| 位置 | 项目 |
|---|---|
| `src/shared` | `PlcMcp.SharedKernel` |
| `src/core` | `PlcMcp.Core.Workspaces`、`PlcMcp.Core.Automation` |
| `src/services` | `PlcMcp.Services.Contracts`、`PlcMcp.EngineeringService` |
| `src/infrastructure` | `PlcMcp.EntityFrameworkCore`、`PlcMcp.Execution` |
| `src/adapters` | `PlcMcp.Inovance` |
| `src/hosts` | `PlcMcp.McpHost`、独立 `PlcMcp.ValidationHost` |
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

上面启动的是生产宿主，当前只开放两个查询工具。要在 Mac 验证编辑流程，请按 [第二批启动说明](docs/13-managed-editing.md) 使用独立 TestHost；已有实现继续接受离线测试，真实支持声明须待目标 IDE 验收。
