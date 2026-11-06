# 第二批：受控工程副本、程序写入与保存

本批把“读取工程—生成程序—修改—取得诊断—修复—保存版本”接到同一个 MCP 服务中。应用用例、文件管理、SQLite 记录、子进程协议和原创 ScriptEngine 调用均已有实现；在独立测试宿主中可以跨平台执行。真实 InoProShop / IronPython 兼容性与实际 OpenCode 客户端联调仍未验收。

生产宿主仍只开放两个状态/能力工具，`engineeringReady=false`。第二批时测试宿主共 **22 个工具**，该批新增 13 个；P3 扩为 49 个，见 [当前说明](14-engineering-configuration.md)。返回 `executionMode=fixture`、`evidenceKind=simulated`。测试数据是 JSON，不是汇川工程，模拟诊断不代表 PLC 编译。

## 本批交付

| 能力 | 已实现行为 | 边界 |
|---|---|---|
| 模板目录 | 列出模板、声明文件、版本、Profile、控制器及库要求 | 模板由本机维护者登记，不从 AI 输入路径任意复制 |
| 创建工作区 | 从登记的模板或现有工程副本复制，保存、重开、验证后发布 | 不凭空生成厂商二进制包，不覆盖源工程 |
| POU | 创建 Program / FunctionBlock / Function；按 GUID 读取、替换声明或文本实现 | ST/IL 文本；LD/FBD/SFC/CFC 仅空对象创建，未实现网络编辑；SFC 不用于 Function |
| GVL / DUT | 创建、读取、更新声明；DUT 类型包括结构体、枚举、别名、联合 | 不单独解析或验证 PLC 语义；数组可通过别名表达 |
| 保存交付版本 | 从当前版本另建候选，保存重开并标记 `isDelivery=true` | 每次修改已保存工作版本；交付标记不代表编译、任务绑定或运行通过 |
| 故障记录与对账 | 保留备份、候选、执行步骤和终止证据；证据一致时恢复任务终态 | 不自动重试未知操作，不接受调用者伪造的结果或强制解锁 |

Method、Property、Action、Task 配置/调用绑定、Device/IO 编辑、Library 编辑及 XML 往返属于后续能力，不以空工具占位。没有增加在线下载、启动或停止 PLC 的工具。

## Mac 启动与 OpenCode 接入

在仓库根目录使用 .NET 8 SDK 与 Python 3：

```bash
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
export DOTNET_ENVIRONMENT=Development
export PlcMcp__ApiKey="$(openssl rand -hex 32)"
export PlcMcp__DataDirectory="$PWD/.plcmcp/fixture-data"
export PlcMcp__WorkspaceRoot="$PWD/.plcmcp/fixture-projects"
python3 eng/init-fixture.py --workspace-root "$PlcMcp__WorkspaceRoot"
dotnet run --project tests/PlcMcp.TestHost --no-launch-profile
```

初始化命令创建 `templates/sample`，已有同名模板时拒绝覆盖，只需首次运行。`DOTNET_ENVIRONMENT=Development` 是测试宿主显式启用条件。旧的第一批样例文件可以保留，但写入流程必须先创建受控工作区。

Windows PowerShell 在完成 restore/build/test 后：

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:PlcMcp__DataDirectory = Join-Path $PWD '.plcmcp/fixture-data'
$env:PlcMcp__WorkspaceRoot = Join-Path $PWD '.plcmcp/fixture-projects'
python eng/init-fixture.py --workspace-root $env:PlcMcp__WorkspaceRoot
dotnet run --project tests/PlcMcp.TestHost --no-launch-profile
```

服务监听 `http://127.0.0.1:5088/mcp`。在另一个拥有相同 `PlcMcp__ApiKey` 的终端启动 OpenCode，把 [opencode.example.json](../config/opencode.example.json) 的 `mcp.plc` 合并到项目配置，不覆盖已有设置。不要在第二个终端重新生成密钥。执行 `opencode mcp list` 检查连接，再使用 [编辑流程提示](../config/opencode-editing-scenario.md)。配置依据 [OpenCode 官方 MCP 文档](https://opencode.ai/docs/mcp-servers/)，本批以 HTTP 自动客户端验证服务端协议，未声称实际 OpenCode/模型已联调。

OpenCode 每次提交先取得 jobId，轮询 `plc_job_get`，再用 `plc_job_get_result` 判断业务成功。写入成功后重新查询 `plc_workspace_get`，后续读/编译使用新 `projectPath`，后续写使用新 `revision`。

## 版本和保存语义

| 标记 | 取得方式 | 使用场景 |
|---|---|---|
| `t2:<摘要>` | `plc_templates_list` | 创建时可传 `expectedTemplateRevision`；摘要覆盖规范化模板描述和已声明文件 |
| `m2:<任务 ID>:<摘要>` | `plc_workspace_get`、写入任务成功结果 | 更新与保存必须传 `expectedRevision`；覆盖受控目录的文件清单与内容，新版本有独立身份 |
| 主文件 SHA-256 | 读取/编译结果，`revisionScope=project_file_sha256` | 既有读取、编译工具的可选修订；不能当作写入用的 m2 修订 |

即使保存后字节没有变化，也生成新的版本身份，旧请求不会静默覆盖新的工作区头。重复同一幂等键和相同请求返回原任务；同键不同请求拒绝。两个请求同时使用同一旧修订，最多一个能发布，另一个得到修订冲突。

修改过程如下：

1. 持久化任务和执行意图，核对当前头与源文件；文件新增、删除或内容变化均纳入检查。
2. 将源文件复制到该任务的 `changes/<jobId>/before` 和 `candidate`，验证备份与源一致。
3. 只在候选副本上运行脚本，拒绝接管已有主工程会话；编辑前核对声明的控制器和库引用。
4. 执行写入、读回、保存、关闭、重开；核对 GUID、声明/实现、依赖引用和主文件修订，并确认自有会话关闭。
5. 核对源仍未变化，将候选移到唯一的 `versions/<jobId>`，采集完整文件清单。
6. SQLite 事务同时更新工作区头、领域修订与已发布证据；最后完成任务状态。

每次更新整个声明/实现；null 表示保持该部分不变，空字符串表示清空。文本比对只归一化换行，不把 IDE 自动改写后的不同代码视为成功。声明保守核对对象种类与名称，不承担完整 IEC 语法分析。

`versions` 通过应用约束保持历史内容不变，不是操作系统只读存储。人工直接修改这些目录会造成修订冲突。平台没有覆盖工程师的原工程，也没有与其 GUI 未保存缓冲区做合并；需要继续外部编辑时，应登记新的受控副本。单数据目录实例锁不代表对本机所有 IDE 实例的全局锁。

## 模板与依赖登记

模板位于 `WorkspaceRoot/templates/<templateId>/template.json`；格式见 [template-v1.schema.json](../contracts/template-v1.schema.json)。`files` 列出需要复制的主工程与本地依赖文件，路径相对模板目录。主文件必须在列表内，路径不可越界或包含符号链接/重解析点。工作区和模板名使用小写字母开头、字母数字连字符，最多 48 字符，拒绝 Windows 保留设备名。

| 字段 | 含义 |
|---|---|
| `id` / `projectFile` / `files` | 模板身份、主工程文件、需要复制的本地文件 |
| `ideProfile` | 目标 IDE Profile，执行器必须匹配；不是 IDE 版本兼容认证 |
| `targetController` | 人可读的目标说明 |
| `controller` | real 模板必填的 `{type, id, version}`，取自实际设备标识，不填写猜测型号 |
| `requiredLibraries` | 期望工程引用中存在的精确库字符串列表；允许空列表 |
| `executionMode` | `real` 或 `fixture`，模板与宿主模式必须一致 |

代码调用 `get_device_identification()` 和 `get_libraries(False)` 核对工程内引用，但**不证明机器已安装并可解析所有设备包和库**。成功结果明确 `installedDependenciesVerified=false`，上层标记 `project_references_checked_installation_unverified`。真实环境验收仍需检查依赖解析与真实编译，不以文件齐全或引用名称匹配替代。

读取工作区返回 `dependencyValidation=declared_files_only`，表示该查询仅核对当前文件清单，不重新启动 IDE 验证。m2 不覆盖全局库仓库、设备仓库或未保存的 GUI 内存。

当前模板描述上限 64 KiB、声明文件数 512、受控版本文件数 2000、总文件大小 256 MiB、深度 32；请求最多 128 KiB，响应最多 1 MiB。正文单字段最多 65536 字符，并继续检查 UTF-8 请求总大小，超限明确失败。

## 结果未知与恢复

文件移动与数据库提交不是同一事务。故障可能留下孤立候选或版本，但这些文件不会自动成为当前工程。备份和候选路径、阶段、已完成步骤记录在持久化证据中。失败不会被描述成“所有文件已回滚”。

`plc_job_reconcile(jobId)` 只读取服务已有证据，不执行厂商脚本：

| 持久化事实 | 对账行为 |
|---|---|
| 发布事务已提交，任务完成记录丢失 | 核对当前头指向该任务、修订和实际文件清单，再确认 Succeeded |
| 未启动执行或已验证但未发布，且执行已停止 | 条件更新阶段，确认 Failed；迟到执行器不能再发布 |
| 已确认失败/取消，任务终态丢失 | 核对终止证据和当前文件，再恢复原终态 |
| 进程已写文件后失联、响应损坏、无法证明委托执行停止 | 返回 `reconciliation_unproven`，保留 OutcomeUnknown 与执行槽阻塞 |
| 已发布文件被外部更改 | 返回 `reconciliation_conflict`，保留阻塞 |

旧读取/编译任务没有写入证据，也不能借这个工具强行解除未知状态。取消仍分请求与确认；一旦开始发布事务，已提交的成功可以赢得取消竞争。没有删除数据库、编辑终态或清空阻塞标记的恢复步骤。

## 实现位置与验证

- `Core.Workspaces/ProjectChangeLifecycle` 约束阶段，`AutomationJob.ReconcileVerified` 约束未知结果确认。
- `EngineeringService/Editing` 编排提交、候选执行、发布和对账，通过 Contracts 端口访问外部。
- `Execution/ManagedProjectFiles` 管理允许路径、文件清单、复制和新版本。
- `EntityFrameworkCore/SqliteProjectChangeStore` 保存当前头与变更证据；新增迁移保持已有任务，旧迁移 ID 不改名。
- `Inovance/Scripts/engineering_worker.py` 使用供应商 CHM 与官网对照的原创调用；不引入第三方 PLC MCP。
- `McpHost/Tools/EditingTools` 提供协议入口，生产组合根仍不注册未验证工程工具。

执行 `eng/verify.ps1`，或依次运行锁定还原、Release 构建、标准 .NET 测试、四组 HTTP 检查和 Python 测试。HTTP 检查都使用 5088 端口，需依次运行：

```bash
python eng/verify-mcp.py
python eng/verify-execution.py
python eng/verify-editing.py
python eng/verify-configuration.py
python -m unittest discover -s tests/script_engine -v
```

新增测试覆盖文件/依赖冲突、同修订竞争、幂等、保存失败、重开不一致、写后失联、发布应答丢失、重启对账、迟到执行器、旧数据库升级与链接路径。HTTP 编辑检查完成程序创建、模拟错误、修复、GVL/DUT 读写、保存及重启读取。模拟编译仅由 `compileError` 或实现中的 `FIXTURE_ERROR` 标记驱动，不解释 PLC 代码。

本批验收分开报告：离线实现与自动测试；实际 OpenCode 客户端/模型联调；真实 Windows/InoProShop/IronPython 验收；PLC 在线运行。后面三项当前未完成，不影响继续开发有文档依据的下一批工程能力。
