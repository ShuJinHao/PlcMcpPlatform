# PLC MCP Platform 开发约定

## 产品目标

OpenCode 调用 MCP，操作真实 InoProShop 工程并取得编译反馈。先读 README，再按变更范围读取 docs/01～15；文档与实际实现不一致时必须明确指出并修正状态。

## 实现规则

- 当前主工程统一 .NET 8 / C# 12；新代码按 hosts/core/services/infrastructure/adapters/shared 分层。
- Core 维护领域行为，不引用 EF、MCP SDK、进程 API 或厂商程序集。
- Tool 只承担协议入口，应用用例通过端口访问外部能力。
- PLC 工程业务和脚本自主实现，不依赖、调用或移植第三方 PLC MCP 的服务、bundle 或脚本；MCP 协议层继续使用官方 C# SDK。
- 汇川接口依据供应商 API 文档与 CODESYS 官方资料交叉核对，参见 docs/10；文档存在、运行时可见、操作验收通过必须分别记录。
- 平台聚合不等于厂商工程对象；数据库不能复制成第二套 PLC 工程事实来源。
- 所有工程能力必须有真实版本与样例证据才标 Supported。未实现工具不注册，不返回假成功。
- 不限制能力规划为 ST；各图形语言、设备、任务、库、导入导出按能力矩阵推进。
- 禁止自动重试结果未知的工程写操作。取消请求不等于执行器已经终止。
- SQLite 入队、执行所有权、修订检查、备份、读回、保存、对账需整体设计。
- 厂商 SDK/脚本运行在经过验证的 Worker 环境；旧运行时 Worker 走 JSON 协议，不引用 net8 DLL。
- 公有仓库只放本项目原创代码、公开资料链接和脱敏样例；不得复制其他私有仓库内容、生产工程或凭据。

## 验证与交付

- 运行受影响的领域、契约、持久化和架构测试；正常交付运行 restore/build/test。
- IDE 相关变更还需真实 Windows/InoProShop 验收；环境不可用时明确写未验证。
- 执行与 MCP 变更还需运行 `python eng/verify-mcp.py` 、`python eng/verify-execution.py` 、`python eng/verify-editing.py` 、`python eng/verify-configuration.py` 和 `python eng/verify-exchange.py`；fixture 证据不能替代真实 IDE 验证。
- Python 探针变更运行 `python -m unittest discover -s tests/script_engine -v`；CPython 测试不替代目标 IronPython/IDE 验证。
- 提交更新实现状态和相关文档；保持锁文件，不使用预览依赖或关闭检查掩盖错误。
- 后续功能使用独立分支和 PR，保持变更聚焦；发布、在线 PLC 操作按用户明确范围执行。

- P3 真实集成验收使用独立 ValidationHost 与显式操作白名单，不通过生产配置开启未验证能力。当前依赖快照与模板初始要求分离，变更后需保存重开核对；实际报告与供应商文件不提交。

- P4 的图形语义核对必须位于候选发布之前；XML 导入后的重开一致不能宣称源工程无损。graph-v1 范围、制品限制与未知数据拒绝规则见 docs/15。
