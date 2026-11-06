# PLC MCP Platform

让 **OpenCode 通过 MCP 操作 InoProShop，生成、修改和编译汇川 PLC 工程**。

OpenCode 负责理解需求、生成程序与迭代修复；本项目负责工程访问、能力发现、任务管理、厂商执行与真实结果反馈。后续通过版本化适配器支持西门子、欧姆龙。

**产品定位：面向 PLC 工程自动化的 MCP Server。** 仓库名称中的 Platform 表示可扩展的 PLC 工程能力，不表示通用 MCP 管理平台。对外由一个 MCP 服务提供工程工具；内部汇川模块是自主实现的适配器和脚本执行器，不是另一个 MCP Server。厂商 Worker 使用内部执行协议，不承担 MCP 工具发现或代理转发。

通用 MCP 服务目录、第三方服务托管/聚合、组织级网关和模型网关不属于本项目。未来需要这些功能时可评估 Obot 等已有产品接入本服务；PLC 工程修改、编译诊断和恢复仍由本项目负责。详见[产品与协议边界](docs/02-architecture.md#产品与协议边界)。

## 当前阶段 P8 图形编程扩展

在保留 graph-v1 的基础上新增 graph-v2：函数返回类型、数组边界、字符串长度、LD/FBD 连接标签、FBD 双向引脚及结构化 SFC 选择/并行分支与汇合。新结构通过既有 MCP 工具生成、读取、修改，接入批量发布、版本差异和准确修订交付包。

沿用 P7 成员/访问器与共享组态批量修改、P6 工程接入和历史恢复。TestHost / ValidationHost 仍为 **74 个工具**；.NET 8、DDD 分层、官方 MCP C# SDK 和原创厂商脚本保持不变。OpenCode 侧使用标准 MCP 连接。

默认生产宿主只开放两个查询工具，所有汇川能力仍为 `Unverified`、`engineeringReady=false`。目标 InoProShop/IronPython、模型生成和 PLC 运行尚未验收，Mac 可以继续开发与离线回归。图形子对象、未知 XML 数据保护保持启用。

详情见 [第八批说明](docs/19-graph-v2-engineering.md)。统一检查 `python eng/verify.py`，集中场景 `eng/run-regressions.py --suite all` 包含 P3～P8；本批 31 个场景和 11 步混合语言工程创建见 [回归文件](config/regression/p8-graph-v2.json)。三系统检查见 [PR #9 checks](https://github.com/ShuJinHao/PlcMcpPlatform/pull/9/checks)。

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
| [第四批交换与图形内容](docs/15-engineering-exchange.md) | XML 制品、LD/FBD/SFC 模型、导入报告、语义核对和 OpenCode 场景 |
| [第五批批量与交付](docs/16-engineering-delivery.md) | 批量计划、大文件传递、ZIP、发布启动、OpenCode 与集中回归 |
| [第六批已有工程与增量编程](docs/17-incremental-engineering.md) | 工程接入、上下文、精确补丁、历史、差异和恢复 |
| [第七批成员与批量修改](docs/18-members-batch.md) | 工程对象查询、访问器修改、设备/库作用范围和多应用检查 |
| [第八批图形扩展](docs/19-graph-v2-engineering.md) | graph-v2 类型、LD/FBD 连线、SFC 分支、混合语言与回归边界 |

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
