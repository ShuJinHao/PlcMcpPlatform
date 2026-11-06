# 开发与 OpenCode 接入

## 环境

核心工程需要 .NET 8 SDK；`global.json` 允许已安装的 .NET 8 新 feature band。CI 已扩展到 Windows/Linux/macOS；汇川 IDE 执行必须在后续目标 Windows 环境验证。运行 Host 不需要安装 InoProShop，但它目前只能查询平台能力和状态。

## 构建与启动

在仓库根目录执行：

```powershell
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
dotnet run --project src/hosts/PlcMcp.McpHost --no-launch-profile
```

Host 固定回环端口 5088，避免误配成公网监听。`/health/live` 只说明进程存活；`engineeringReady=false` 表示尚不具备真实工程操作能力。

首次锁文件需要已提交到仓库；有意升级依赖时先修改集中版本、重新 restore，再审查锁文件变化并运行验证。普通开发不删除锁文件绕过依赖漂移。

## OpenCode

将 [配置示例](../config/opencode.example.json) 的 `mcp.plc` 合并进自己的配置，保持原有模型与其他 MCP 设置。示例对应核对时官方文档的 `mcp.<name>` 格式；其他主版本若更改配置结构，应按实际版本文档调整并记录。

Host 与 OpenCode 必须使用相同的 `PlcMcp__ApiKey`。推荐在一个已设置该环境变量的 PowerShell 中启动 Host 子进程，再从同一父终端启动 OpenCode，使两者继承相同值；不要在两个终端分别生成不同密钥。

```powershell
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
Start-Process dotnet -ArgumentList 'run --project src/hosts/PlcMcp.McpHost --no-launch-profile'
opencode
```

OpenCode 提示示例：“使用 plc MCP 查询平台状态和汇川能力，只报告当前真正可用的能力。”预期：发现两个平台工具，IDE 未连接，工程能力未验证。此生产入口当前不能执行工程编辑。

`type: remote` 表示 HTTP MCP 传输，即使服务运行在本机也使用该类型。示例禁用 OAuth 自动发现，使用环境变量提供的 Bearer 密钥。当前没有浏览器交互入口，Host 拒绝带 Origin 的 MCP 请求。

## 离线开发与真实执行准备

P2 已实现受控副本编辑、保存重开与证据恢复。Mac/Windows 编辑启动见 [第二批说明](13-managed-editing.md)，读取/调度背景见 [第一批说明](12-execution.md)。默认真实宿主仅注册两个查询工具；测试宿主提供 74 个工具，明确标注 fixture/simulated，不能将模拟结果视为汇川支持证据。

真实环境后续确认 InoProShop 脚本入口、Profile、无 UI、进程归属和官方 API 兼容性。`IInovanceLaunchProfile` 隔离启动差异；当前首个策略仍为未验证。目标工程脚本由 IDE 内置环境执行，普通 CPython 只用于测试调用逻辑。

用户当前没有 Windows/IDE 不阻塞后续工程能力开发。目标机器的安装路径、版本、模板和运行账户通过本地配置填写，不提交生产程序、凭据、IDE 安装包或商业库。当前仓库不是包含所有供应商依赖的离线安装包。

## P3 组态与真实回归

新增组态工具继续使用相同 MCP 端点。Mac 自动回归运行 `python eng/verify-configuration.py`。后续 Windows 真实回归使用独立 ValidationHost，要求本地版本/Profile/白名单和工程变量；完整步骤见 [第三批说明](14-engineering-configuration.md)。这不是生产宿主的自动降级或模拟模式。

## P4 图形与交换

使用同一 TestHost / ValidationHost 和 OpenCode MCP 配置。构建后运行 `python eng/verify-exchange.py` 自动执行 38 步场景；手工 OpenCode 提示见 [config/opencode-exchange-scenario.md](../config/opencode-exchange-scenario.md)。完整模型、工具、实际导入策略与后续 Windows 回归见 [第四批说明](15-engineering-exchange.md)。不需要额外启动汇川 MCP 或模型服务。

## P5 批量、发布与集中回归

运行 `python eng/verify.py` 进行完整代码检查，或 `python eng/verify-delivery.py` 检查 P5 的 HTTP 场景和大文件。发布入口 `eng/publish.py`，已发布宿主启动前检查 `eng/start.py --check-only`，连接诊断 `eng/doctor.py`，大文件助手 `eng/transfer.py`，跨 P3/P4/P5 场景入口 `eng/run-regressions.py`。

[第五批说明](16-engineering-delivery.md) 包含 Mac fixture 和 Windows validation 的配置约束、准确版本 ZIP、上传恢复与错误含义；[OpenCode 完整提示](../config/opencode-delivery-scenario.md) 用于后续模型回归。CLI 连接已验证到固定 1.19.5，模型闭环与真实 IDE 仍待验证。已有脚本全部调用本项目同一个 MCP 端点，不依赖第三方 PLC MCP。

## P6 已有工程与增量编程

运行 `python eng/verify-incremental.py` 可检查完整 fixture 增量链路；`python eng/verify.py` 包含该检查。已有宿主使用 `eng/run-regressions.py --suite p6`；`all` 包含 P3/P4/P5/P6，P6 需提前准备 imports/sourceName 中的已保存工程和 adoptionSource 变量。Windows 样例配置已增加 6 个操作白名单项，仍必须填写实际版本和 Profile。

用户在 Mac 时可以完成代码检查和 fixture 场景；真实工程统一验收按 [第六批说明](17-incremental-engineering.md) 和 [OpenCode 场景](../config/opencode-incremental-scenario.md) 执行。

## P7 接入范围

继续使用现有标准 MCP 连接配置，无需安装本项目专用 OpenCode 插件。工程数据通过 `plc_project_objects_read` 按需读取；对话历史、上下文压缩与生成决策由客户端和模型负责。新增用例通过 `eng/run-regressions.py --suite p7` 运行，详见 [P7](18-members-batch.md)。
