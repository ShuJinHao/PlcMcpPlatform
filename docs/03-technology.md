# 技术选型

核对日期：2026-11-06。可执行依赖版本的唯一来源为 `Directory.Packages.props` 和各项目 `packages.lock.json`。

| 项目 | 决策 | 采用原因与边界 |
|---|---|---|
| 主语言与运行时 | C# 12 / .NET 8 | 满足现有开发环境；不使用 C# 14 扩展块 |
| Host | ASP.NET Core 8 | MCP HTTP 宿主、DI、配置和运行生命周期 |
| MCP | 官方 `ModelContextProtocol.AspNetCore` 2.3.1 | 包提供 net8.0 目标；框架处理协议，项目处理业务 |
| 架构 | DDD + Clean Architecture + 模块化单体 | 领域行为、应用端口与外部实现隔离 |
| 应用用例 | Command/Query + Handler 风格 | P0 使用小型接口和构造函数注入；暂不额外引入 MediatR |
| 持久化 | EF Core 8.0.33 + SQLite | 本机单节点工作区/任务元数据；不存全量 PLC 工程对象 |
| 调度 | `System.Threading.Channels` + 持久化状态机 | P2 实现；内存队列只做唤醒，不承担持久性 |
| 日志 | Serilog.AspNetCore 8.0.3 | 结构化事件；不记录密钥、原始工程全文或厂商凭据 |
| 汇川执行 | IDE 内置 ScriptEngine / IronPython | 与 IDE、Profile 和设备/库环境一起验证 |
| 汇川实现来源 | 依据供应商文档和 CODESYS 官方 API 自主编写 | 不集成第三方 PLC MCP 服务、bundle 或脚本；具体对照见 [官方接口基线](10-official-api-baseline.md) |
| IPC | 版本化 JSON；Windows Named Pipe 为首选候选 | P1 验证脚本环境兼容，P2 定稿传输和 ACL |
| 测试 | xUnit 2.9.3、官方 Assert、真实 SQLite | 无须额外断言库；真实 IDE 测试单独运行 |
| 部署 | Windows 工程师本机 | Host 与 Worker 解耦；服务化在真实会话测试后确定 |
| OpenCode | 外部 MCP 客户端 | 不向服务端加入模型调用逻辑 |

## 版本和升级

`global.json` 选择 .NET 8 已安装的较新 feature band。发布包与构建记录应记录精确 SDK、IDE 和依赖版本。NuGet 采用集中版本与锁文件，CI 使用 locked-mode，升级通过独立变更说明与回归验证。

.NET 8 官方支持截止日期为 **2026-11-10**。当前按用户环境使用 .NET 8；长期生产部署前，应在获准环境中迁移至受支持 LTS 并完成兼容验证。进程隔离使 Host 的升级不要求同步升级厂商 SDK 宿主。

P0 不引入 OBOT、RAG、消息中间件、分布式工作流平台或完整 Agent 框架。需要的 AI 编程编排由 OpenCode 完成；未来如出现多机、多用户或事件触发要求，再评估服务器数据库与队列。

## SQLite 使用约束

P2 启用迁移、连接超时、并发令牌与短数据库事务，按实测决定 WAL 配置。数据库必须位于本机可靠磁盘，不将共享网络目录作为多节点数据库；IDE 长操作不能占着数据库事务。租约和原子领取要由数据库约束验证，而不能只用进程内锁。

## 依据

- [官方 MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [MCP ASP.NET Core 2.3.1 包](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore/2.3.1)
- [OpenCode MCP 配置](https://opencode.ai/docs/mcp-servers/)
- [.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [CODESYS ScriptEngine](https://content.helpme-codesys.com/en/CODESYS%20Scripting/_script_scripting_with_codesys.html)
- [CODESYS 命令行脚本](https://content.helpme-codesys.com/en/CODESYS%20Scripting/_cds_starting_script_via_command_line.html)
- [Windows 服务与交互式会话](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)

上述 CODESYS 文档证明技术机制，不证明汇川某版本已经通过验证。厂商兼容性必须补充现场证据。
