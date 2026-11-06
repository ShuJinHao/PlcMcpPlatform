# 第一批执行实现与使用说明

> 本文保留对应批次的实现记录。当前工具数量、启动和剩余范围以 [P10 交付说明](21-delivery-closure.md)、[工具清单](22-current-tools.md) 和 [能力缺口](23-capability-closure.md) 为准。

本批交付持久化任务、进程执行器、四个工程工具、三个任务工具和对应的原创 ScriptEngine 调用脚本。独立测试宿主可以在没有 InoProShop 的环境中验证完整 MCP 流程。本页保留第一批行为说明；第二批已经增加创建、修改、保存和对账，当前入口见 [第二批交付](13-managed-editing.md)。真实汇川能力仍为 Unverified。

## 两个启动入口

| 入口 | 注册工具 | 执行来源 |
|---|---|---|
| `src/hosts/PlcMcp.McpHost` | 两个状态/能力查询工具 | real；没有已验证 IDE 时不能执行工程操作 |
| `tests/PlcMcp.TestHost` | 当前为 22 个工具（第一批 9 个，第二批增加 13 个） | fixture；测试替身，每个执行结果包含 simulated 标识 |

测试宿主引用生产的应用用例、SQLite Store、调度器、MCP Tools 和进程通信实现，仅执行器依赖替换为测试进程。生产宿主不引用测试程序集，没有配置项可以自动回退到 fixture。两者不能同时占用本机 5088 端口。

## Mac 上运行测试宿主

准备 .NET 8 SDK，在仓库根目录运行：

```bash
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
export DOTNET_ENVIRONMENT=Development
export PlcMcp__ApiKey="$(openssl rand -hex 32)"
export PlcMcp__DataDirectory="$PWD/.plcmcp/fixture-data"
export PlcMcp__WorkspaceRoot="$PWD/.plcmcp/fixture-projects"
mkdir -p "$PlcMcp__WorkspaceRoot"
cp tests/fixtures/sample.fixture.json "$PlcMcp__WorkspaceRoot/sample.fixture.json"
dotnet run --project tests/PlcMcp.TestHost --no-launch-profile
```

在另一个配置了相同 `PlcMcp__ApiKey` 的终端启动 OpenCode，合并 [现有配置](../config/opencode.example.json)。也可以在同一已设置环境变量的终端中先后台启动服务，再启动 OpenCode。不要在两个终端分别生成密钥。

提示示例：使用 plc MCP 查询状态，确认这是 fixture 环境；读取样例 JSON 的对象树，根据返回的 GUID 读取程序，再提交模拟编译并查询诊断。调用时使用复制后样例的绝对路径，每次新操作提供独立 idempotencyKey；重复同一请求沿用原键。

样例是本项目原创测试数据，不是可供 InoProShop 打开的工程文件。默认 `compileError=true` 用于验证错误返回，测试进程不会解释或编译其中的程序文本。真实 OpenCode 客户端联调与本项目 HTTP 自动客户端验证分别记录。

Windows PowerShell 对应配置：

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:PlcMcp__DataDirectory = Join-Path $PWD '.plcmcp/fixture-data'
$env:PlcMcp__WorkspaceRoot = Join-Path $PWD '.plcmcp/fixture-projects'
New-Item -ItemType Directory -Force $env:PlcMcp__WorkspaceRoot | Out-Null
Copy-Item tests/fixtures/sample.fixture.json (Join-Path $env:PlcMcp__WorkspaceRoot 'sample.fixture.json')
dotnet run --project tests/PlcMcp.TestHost --no-launch-profile
```

## 工具语义与结果

四个工程工具统一返回持久化任务回执。回执的接受成功不表示工程操作成功；用 `plc_job_get` 查询状态，再用 `plc_job_get_result` 取终态结果。后者遇到未完成、失败、取消、结果未知时设置 MCP `isError=true`，保留可用的编译诊断。状态查询本身成功时可以返回一个 Failed 任务，不能把查询成功当成业务成功。

每个工程请求包含受控目录下的绝对路径和幂等键，POU/编译还要求显式对象/Application GUID。参数约束：幂等键最多 128 字符；超时 1～600 秒；树对象数 1～2000，默认 256；脚本遍历深度上限 64。失去对象遍历完整性时不把未找到对象直接当成不存在。

工具 Schema 固定在 [mcp-tools.v1.json](../contracts/mcp-tools.v1.json)，由官方 SDK 输出。`python eng/verify-execution.py` 检查当前定义与快照相同；有意修改契约时运行 `--write-contracts`，审查差异后提交。Tool 的 ReadOnly=false 包括提交持久化任务的状态变化，不表示这些首批脚本会修改 PLC 程序。

`plc_project_open` 当前是打开校验任务：脚本建立自己拥有的工程会话，操作结束关闭，不保持工程师 GUI 的项目窗口。后续读取与编译各自重新打开受控会话。检测到已有主工程则拒绝接管；脚本只关闭自己打开的工程，不保存、不下载、不执行在线操作。

## 持久化与调度

- Host 启动先取得数据目录独占所有权，再应用 EF Core 迁移。fixture 与 real 数据库记录各自模式，不能交叉复用。
- 同一实例只有一个执行槽。SQLite 先提交任务，Channel 仅唤醒；另有周期扫描，因此通知丢失不会丢失已经接受的任务。
- 幂等身份是工作区、操作与请求键，指纹包含规范化路径、参数和超时。同键同请求返回原任务，即使其源文件后来被删除；同键不同请求拒绝。
- 默认最多 256 个排队/执行中任务，超过时返回 `queue_full`；重试已经接受的请求仍返回原任务。
- 排队取消直接终止；运行中取消先进入 CancelRequested。只有确认执行停止才能成为 Cancelled，正常完成也可能赢得取消竞争。
- 异常退出、损坏/超限/串号结果、执行超时或无法确认取消，进入 OutcomeUnknown，并持久化阻塞执行槽。重启不会解除阻塞，也不会重放执行中的任务。
- 第二批提供基于已保存写入证据的有限对账，见 docs/13。没有写入证据的旧读/编译未知任务不能强制解锁；不能以删除数据库或清除阻塞标记作为恢复方案。

数据目录是本机单实例边界，不代表对机器上所有独立 IDE 实例提供了全局互斥。实际 IDE 进程归属、会话委托和多实例行为仍需厂商验证。

数据库保存工作区元数据、请求、任务状态和结果，结果可能包含读取的程序文本；未构建第二套 PLC 对象数据库。每次进程调用另保留私有请求/结果文件供排查。本批没有自动历史清理功能，后续需加入保留期与清理策略。Serilog 不输出原始工程正文或 IDE 原始控制台内容。

## 内部进程协议

[worker-v1.schema.json](../contracts/worker-v1.schema.json) 定义版本化内部 JSON，不是第二层 MCP。每次调用创建独立目录，UTF-8 请求上限 128 KiB、结果上限 1 MiB。结果用临时文件写完后原子改名，协议检查版本、请求/任务 ID、执行模式、操作结果形状及修订。

进程通过 ArgumentList 启动，不经过 shell。标准输出与错误输出使用固定缓冲区排空，与协议文件分离。退出码 0 只是检查之一，缺少合法结果不能判成功；编译还检查本次诊断的错误级别与结果是否一致。

`IInovanceLaunchProfile` 隔离 IDE 启动差异，首个策略依据官方 ScriptEngine CLI 文档使用 `--profile`、`--runscript`、`--noUI`，通过子进程环境变量传递请求/结果路径。它仍未获得任何 InoProShop 产品版本的兼容认证，配置一个可执行文件不会使生产工具自动开放。

停止父进程不证明供应商委托会话或所有子进程已停止，因此真实执行器在强制终止后保守返回结果未知。只有测试专用的已知无子进程 fixture worker 可以据退出确认取消；这个分支不用于汇川 IDE。

## ScriptEngine 调用依据与限制

脚本 [engineering_worker.py](../src/adapters/PlcMcp.Inovance/Scripts/engineering_worker.py) 自主实现，采用 Python 2.7 兼容语法，未在目标 IronPython 中验收。

| 调用 | 文档依据与实现 |
|---|---|
| 工程打开 | CHM `IScriptProjects4.open` 与 [官网 ScriptProjects](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptProjects.html)；NoUpdates、allow_readonly、primary 参数 |
| 树遍历 | `get_children(False)`、GUID、get_name；节点/深度限制及截断 |
| 文本读取 | CHM `IScriptTextDocument.length/get_text(offset,length)`；不假定参数为空的重载；非文本对象明确拒绝 |
| 编译 | [ScriptApplication.build](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptApplication.html)；显式 Application，不能用第一个对象猜测目标 |
| 本次诊断 | [ScriptSystem](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptSystem.html) 的 get_message_categories、clear_messages、get_message_objects；清除自有会话旧消息后编译，再采集各分类 |
| 启动 | [官方命令行文档](https://content.helpme-codesys.com/en/CODESYS%20Scripting/_cds_starting_script_via_command_line.html)允许脚本读取环境变量；供应商具体参数仍待确认 |

诊断保留厂商位置文本与偏移，不把不透明位置值猜成行号。语言未可靠识别时返回 null，不能把所有文本对象默认标为 ST。诊断超过 500 条或结果超限时明确失败，不能截断错误后宣称编译通过。

本批修订是主工程文件的 SHA-256，结果标注 `revisionScope=project_file_sha256`；它不能代表所有依赖文件、未保存的 IDE 状态或完整工程事务。脚本检查打开前后主文件变化，拒绝打开后已脏的会话，编译引起的脏标记单独记录。第二批写入已采用独立 m2 文件清单修订、备份和候选会话，不改变本页读取工具的主文件 SHA 语义。

## 验证入口

```bash
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
python eng/verify-mcp.py
python eng/verify-execution.py
python eng/verify-editing.py
python -m unittest discover -s tests/script_engine -v
```

CI 在 Windows、Ubuntu、macOS 执行上述检查，均无需安装供应商软件。三组 HTTP 检查各自启动并清理测试进程和临时目录，不操作已有服务。CI 与真实 IDE、OpenCode 客户端验收不是同一种证据，运行记录见 PR 与 [Actions](https://github.com/ShuJinHao/PlcMcpPlatform/actions)。
