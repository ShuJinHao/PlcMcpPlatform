> P13 / 0.1.0-rc.4：新增 [Mac 接入、类型补全与 Windows 回归入口](docs/26-mac-windows-handoff.md)，见 [运行手册](docs/21-delivery-closure.md)、[74 个工具清单](docs/22-current-tools.md) 和 [剩余缺口](docs/23-capability-closure.md)。

# PLC MCP Platform

让 **OpenCode 通过 MCP 读取工程、生成和修改 PLC 程序、编译并根据诊断修复，最终保存真实工程**。

OpenCode 负责理解需求、生成程序与迭代修复；本项目负责工程访问、能力发现、任务管理、厂商执行与真实结果反馈。**第一阶段只接入汇川 InoProShop，后续接入西门子、欧姆龙**，复用同一个 MCP 服务并增加厂商适配器与版本执行器。当前仅有汇川适配器实现，西门子和欧姆龙尚未接入。厂商路线与第一阶段完成标准见 [需求文档](docs/01-requirements.md#厂商接入路线与第一阶段完成标准)。

**产品定位：面向 PLC 工程自动化的 MCP Server。** 仓库名称中的 Platform 表示可扩展的 PLC 工程能力，不表示通用 MCP 管理平台。对外由一个 MCP 服务提供工程工具；内部汇川模块是自主实现的适配器和脚本执行器，不是另一个 MCP Server。厂商 Worker 使用内部执行协议，不承担 MCP 工具发现或代理转发。

通用 MCP 服务目录、第三方服务托管/聚合、组织级网关和模型网关不属于本项目。未来需要这些功能时可评估 Obot 等已有产品接入本服务；PLC 工程修改、编译诊断和恢复仍由本项目负责。详见[产品与协议边界](docs/02-architecture.md#产品与协议边界)。

## 当前阶段 P13 候选交付

P0～P13 是汇川阶段内的开发批次。当前工作分支承接 P12，候选版本为 `0.1.0-rc.4`；实现、Mac 模型测试和 Windows 真实兼容性分别记录，见 [本批交付说明](docs/26-mac-windows-handoff.md)。CFC/PDO 等阻塞项未解决，不能称第一阶段全量适配完成。

正式宿主提供 22 个不依赖 IDE 的本地工具；其余工程工具根据实际安装、Profile、脚本与执行代码指纹及人工验收范围开放。发现、调用、任务提交和执行共用门禁；批次缺少依赖或参数越界时先拒绝再写入。

共保留 **74 个工具实现**。TestHost 可离线回归全部实现；ValidationHost 按显式 allowlist 收集真实执行证据。OpenCode CLI 连接已有 CI 检查；Mac 的 OpenCode 1.18.2 + DeepSeek Flash 已通过五组真实模型回归：ST 诊断修复、LD/FBD/SFC、声明/成员、组态及增量/恢复/交换，工程执行来源仍是 fixture。当前没有真实 IDE 验收记录，所有汇川能力仍为 `Unverified`、`engineeringReady=false`；PLC 运行也未验证。Mac 可以继续开发和离线测试。

既有 graph-v1/v2、POU/成员、设备/任务/库、批量编辑、修订历史、精确补丁与交付包能力继续保留。.NET 8、DDD、官方 MCP C# SDK、原创厂商脚本和 OpenCode 标准 MCP 连接保持不变。

详见 [P9 运行和验收手册](docs/20-production-readiness.md)。统一检查 `python eng/verify.py`；集中工程场景 `eng/run-regressions.py --suite all` 包含 P3～P8、P11～P13。CI 的 Windows 作业生成 production / validation / fixture 三种预发布 ZIP、SHA256 和来源提交清单，保存在 Actions artifacts 中；main 全量检查通过后创建版本化 GitHub prerelease。P10 的停服清理、可选模型回归及 SFC 定时动作继续保留；P11 的有界聚合初值继续保留；P12 补齐声明分组、常量/保持属性、地址/注释、临时/外部变量、多动作 SFC 和 26 项工程场景，工具数量保持 74。

## Mac 直接使用

在源码仓库根目录运行（发布包使用包内 `eng/start.py`）：

```bash
python3 eng/dev.py setup
python3 eng/dev.py opencode --no-build
```

在 OpenCode 中用自然语言提出 PLC 编程需求。启动器打开独立模拟工作区，保留现有 OpenCode 用户配置。DeepSeek 凭据通过环境或仓库外私有文件提供；启停、认证和故障定位见 [运行说明](docs/26-mac-windows-handoff.md)。

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
| [第九批正式执行与预发布](docs/20-production-readiness.md) | 实际身份、验收范围、正式工具门禁、三种交付包与诊断 |
| [第十批交付与统一回归](docs/21-delivery-closure.md) | 启动、保留策略、模型回归、候选发布 |
| [当前工具清单](docs/22-current-tools.md) | 74 个实现的契约与代码入口 |
| [当前功能范围与缺口](docs/23-capability-closure.md) | 已实现范围、厂商待验证和未实现项 |
| [第十一批图形初始化](docs/24-graph-initializers.md) | 数组、结构、重复初值和 22 项工程回归 |
| [第十二批工程语义](docs/25-engineering-semantics.md) | 声明组、地址、注释、多动作 SFC 和 26 项工程回归 |

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

上面启动的是生产宿主；没有真实验收记录时开放 22 个本地工具，IDE 操作仍受范围门禁控制。要在 Mac 验证编辑流程，请按 [第二批启动说明](docs/13-managed-editing.md) 使用独立 TestHost；已有实现继续接受离线测试，真实支持声明须待目标 IDE 验收。
