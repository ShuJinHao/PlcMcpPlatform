# 开发与 OpenCode 接入

## 环境

核心工程需要 .NET 8 SDK；`global.json` 允许已安装的 .NET 8 新 feature band。P0 可在 Windows/Linux 构建；汇川 IDE 执行必须在后续目标 Windows 环境验证。运行 Host 不需要安装 InoProShop，但它目前只能查询平台能力和状态。

## 构建与启动

在仓库根目录执行：

```powershell
dotnet restore PlcMcpPlatform.sln --locked-mode
dotnet build PlcMcpPlatform.sln -c Release --no-restore
dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
dotnet run --project src/hosts/PlcMcp.McpHost --no-launch-profile
```

P0 固定回环端口 5088，避免误配成公网监听。`/health/live` 只说明进程存活；`engineeringReady=false` 表示尚不具备真实工程操作能力。

首次锁文件需要已提交到仓库；有意升级依赖时先修改集中版本、重新 restore，再审查锁文件变化并运行验证。普通开发不删除锁文件绕过依赖漂移。

## OpenCode

将 [配置示例](../config/opencode.example.json) 的 `mcp.plc` 合并进自己的配置，保持原有模型与其他 MCP 设置。示例对应核对时官方文档的 `mcp.<name>` 格式；其他主版本若更改配置结构，应按实际版本文档调整并记录。

Host 与 OpenCode 必须使用相同的 `PlcMcp__ApiKey`。推荐在一个已设置该环境变量的 PowerShell 中启动 Host 子进程，再从同一父终端启动 OpenCode，使两者继承相同值；不要在两个终端分别生成不同密钥。

```powershell
$env:PlcMcp__ApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
Start-Process dotnet -ArgumentList 'run --project src/hosts/PlcMcp.McpHost --no-launch-profile'
opencode
```

OpenCode 提示示例：“使用 plc MCP 查询平台状态和汇川能力，只报告当前真正可用的能力。”预期：发现两个平台工具，IDE 未连接，工程能力未验证。不能预期当前阶段生成 PLC 工程。

`type: remote` 表示 HTTP MCP 传输，即使服务运行在本机也使用该类型。示例禁用 OAuth 自动发现，使用环境变量提供的 Bearer 密钥。当前没有浏览器交互入口，Host 拒绝带 Origin 的 MCP 请求。

## 后续真实执行准备

P1 已增加 [原创只读会话探针与官方接口对照](10-official-api-baseline.md)。先在已打开脱敏样例的目标 IDE 中验证接口可见性；普通 CPython 只能运行边界测试，无法提供厂商工程对象。

P1 确认 InoProShop 的实际脚本入口与 `--runscript`/`--noUI` 支持情况。受控脚本由 IDE 内置环境执行，不自行安装一个 IronPython 后假定能访问工程 API。P2 加入 Worker 后再提供其启动配置和通信文档。

目标 Windows 机的路径、版本、模板和运行账户通过本地配置填写，不提交用户绝对路径、生产程序、访问令牌、IDE 安装包或商业库。离线依赖包在恢复构建成功后按版本和许可单独制作，当前仓库不是完整离线安装包。
