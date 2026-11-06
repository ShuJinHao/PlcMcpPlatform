# 验收方案与当前证据

## P0 自动验证

| 范围 | 核验 |
|---|---|
| 编译 | .NET 8 / C# 12、所有工程、Release、零警告错误 |
| 任务行为 | 禁止未开始成功、取消须确认、取消与完成竞争、未知结果禁止重启 |
| 修订 | 旧修订不能覆盖新状态 |
| 能力 | 全部未验证能力不能宣称支持、重复身份拒绝、包含图形语言及任务绑定 |
| SQLite | 唯一幂等约束、两个 Context 的乐观并发冲突 |
| 架构 | Core/Contracts/Service 不引用 MCP、EF、厂商实现或 Host |
| HTTP MCP | initialize、tools/list、两个 tools/call、错误工具名、无凭据拒绝、Origin 拒绝 |

执行 `eng/verify.ps1` 或 README 中的 restore/build/test。Release 构建后执行 `python eng/verify-mcp.py`，它启动测试 Host、运行 `eng/mcp-smoke.py` 并清理自己的进程，使用 Python 标准库；它验证协议连接，不需要模型、OpenCode 或 PLC。CI 的 Windows/Linux/macOS 构建结果与本地结果分别记录。

## P0 历史验证记录

记录日期：2026-11-06。环境：Linux，.NET SDK 8.0.427，.NET / ASP.NET Core Runtime 8.0.33。

| 检查 | 结果与边界 |
|---|---|
| NuGet 还原 | 全部 9 个项目通过；生成并提交锁文件，`restore --locked-mode` 通过 |
| Release 编译 | 全部 9 个项目通过，零警告、零错误；MCP 配置修订后 Host 及其依赖再次通过 |
| HTTP MCP 冒烟 | 实际启动 Host 后通过：鉴权、Origin 拒绝、协议初始化、工具发现、两个工具调用、能力状态及不存在的编译工具拒绝 |
| 9 个测试方法 | 使用仓库外临时调用器逐一执行原有 xUnit Fact 方法及断言，9/9 通过，包括真实 SQLite 内存数据库约束与并发检查 |
| 标准 `dotnet test` | 当前执行环境的 VSTest 在测试分发前抛出 `NullReferenceException`，没有得到标准测试运行器结果；直接调用结果不能替代此项 |
| GitHub Actions | Windows/Linux 均通过锁定还原、Release 构建、标准 `dotnet test`（每个平台 9/9，0 跳过）和 HTTP MCP 冒烟 |
| InoProShop / OpenCode | 尚未实机联调；Windows CI 只验证核心工程和 MCP 接口，没有宣称已生成、保存或编译真实 PLC 工程 |

远端证据：[GitHub Actions 首次验证记录](https://github.com/ShuJinHao/PlcMcpPlatform/actions/runs/42149181790)，验证代码提交为 `524a84625817c7f62bc625307664ddb7627aa6df`。本次记录更新仅修改文档。

临时调用器只用于定位当前本地环境下的测试运行器问题，没有改变测试代码或弱化断言，不作为产品交付。标准运行器验证已在 Windows/Linux CI 完成。InoProShop 和 OpenCode 客户端现场联调仍需在用户目标环境单独验收。

## P1 执行验证

已加入任务恢复、通知丢失、重复/冲突请求、排队和运行中取消、响应损坏/串号/超限、超时隔离、路径限制、实例所有权和模式隔离测试。HTTP 检查覆盖独立测试宿主的九个工具、结果错误语义、重启持久化和阻塞状态；工具 Schema 与已提交快照比较。Python 共有 22 项探针/工程脚本测试。运行入口和当前边界见 [执行实现](12-execution.md)。

首轮执行代码提交 `1cafbeacf34196b808c8b0d0205faa1aa00b1a7b` 已通过 Windows、Ubuntu、macOS 的锁定还原、Release 构建、26 项 .NET 测试、两组 HTTP 验证及 22 项 Python 测试，证据：[Actions](https://github.com/ShuJinHao/PlcMcpPlatform/actions/runs/42158837155)。后续补充的迁移快照、文件删除后的幂等重试、队列背压和生产依赖隔离，以最终 PR 的 CI 为准，不把首轮结果扩大为后续提交证据。

## P2 编辑验证

第二批新增 19 个 .NET 场景（含理论用例）、13 个 Python 编辑脚本测试；与前批合计为 48 个 .NET 用例、35 个 Python 测试。三组 HTTP 检查分别覆盖生产两个工具、测试宿主 22 个工具契约与原执行流程、完整副本编辑/模拟诊断修复/保存/重启流程。

本地 Release 构建、HTTP 与 Python 检查以及新增 .NET 用例的直接调用用于开发定位。标准 `dotnet test` 必须以本批 PR 的 Windows、Ubuntu、macOS CI 为准，不将临时调用器或前批 CI 当作新提交的标准测试证据。

新增故障断言包括同修订竞争、源/依赖变化、保存与重开失败、写后失联、发布事务应答丢失、受控证据恢复、迟到执行器无法发布和旧数据库升级。测试未关闭检查、跳过实际 IDE 失败后伪造成功，或引入第三方 PLC MCP。实现边界和命令见 [第二批说明](13-managed-editing.md)。

## 故障场景与剩余验收

当前 35 项 Python 测试包含 10 项探针、12 项读取/编译与 13 项编辑脚本测试。调度、协议和副本写入故障已有离线覆盖；下列场景涉及真实 IDE 的部分仍需真实验收，Task/图形网络等缺口还需后续实现。探针依据与使用方式见 [官方接口基线](10-official-api-baseline.md)。

1. 数据库入队后、Channel 通知前崩溃：恢复后任务仍可识别。
2. 脚本已保存工程、服务未记录完成时断连：进入结果待确认，不自动重复写入。
3. 人工修改或另一个 IDE 打开同工程：拒绝覆盖并说明冲突。
4. 重复工具请求与同键不同内容：分别返回原任务和幂等冲突。
5. 编译超时：区分已确认终止和仍可能执行，不以 HTTP 超时推断 IDE 已停止。
6. 修改中取消：保留备份、部分修改事实和执行器确认状态。
7. 模板/设备描述/库缺失：明确依赖，不创建错误型号的软 PLC 工程冒充成功。
8. 图形程序 XML 往返：网络连接、变量、动作、步骤与布局按语言检查。

## 真实 IDE 验收

在脱敏工程副本上记录版本、Profile、设备/库清单、模板指纹和脚本版本。至少验证读取、修改、读回、保存、关闭重开、故意编译失败、修复后成功。任务绑定必须读取真实任务配置验证。最终通过 OpenCode 完成需求到工程产物的全过程。

在线功能仅在独立阶段与隔离环境验证；当前离线开发不需要连接 PLC。编译成功、工程保存、任务绑定和运行验证分别记录结果。

## P3 工程组态验证

第三批新增任务、成员、对象、库、设备参数与 IO 组态的应用校验、真实脚本 API 调用测试，以及保存重开的持久化集成测试。测试宿主现有 49 个工具；`eng/verify-configuration.py` 通过同一回归运行器执行 42 步 HTTP 场景，并检查错误模式与非法映射请求。

当前本地直接调用 57 个 .NET 用例通过，52 个 Python 测试通过，四组 HTTP 检查通过。本机标准 VSTest 启动受执行环境限制；正式标准测试结果以本批 PR 的 Windows/Ubuntu/macOS CI 为准。CI 链接在合并交付时给出，不将之前的通过结果用作本次证据。

真实验证入口、版本记录、报告格式与边界见 [第三批说明](14-engineering-configuration.md)。本批没有执行真实 Windows/InoProShop/IronPython 或 OpenCode 客户端联调，没有改变任何汇川能力为 Supported。
