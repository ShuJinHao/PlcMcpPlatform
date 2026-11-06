# PLC MCP Platform 开发约定

## 产品目标

OpenCode 调用 MCP，操作真实 InoProShop 工程并取得编译反馈。先读 README，再按变更范围读取 docs/01～24；文档与实际实现不一致时必须明确指出并修正状态。

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
- 执行与 MCP 变更还需运行 `python eng/verify-mcp.py` 、`python eng/verify-execution.py` 、`python eng/verify-editing.py` 、`python eng/verify-configuration.py` 、`python eng/verify-exchange.py`、`python eng/verify-delivery.py`、`python eng/verify-incremental.py` 、`python eng/verify-members.py`、`python eng/verify-graphs.py`、`python eng/verify-initializers.py` 和发布检查 `python eng/verify-publish.py`；fixture 证据不能替代真实 IDE 验证。
- Python 探针变更运行 `python -m unittest discover -s tests/script_engine -v`；CPython 测试不替代目标 IronPython/IDE 验证。
- 提交更新实现状态和相关文档；保持锁文件，不使用预览依赖或关闭检查掩盖错误。
- 后续功能使用独立分支和 PR，保持变更聚焦；发布、在线 PLC 操作按用户明确范围执行。

- P3 真实集成验收使用独立 ValidationHost 与显式操作白名单，不通过生产配置开启未验证能力。当前依赖快照与模板初始要求分离，变更后需保存重开核对；实际报告与供应商文件不提交。

- P4 的图形语义核对必须位于候选发布之前；XML 导入后的重开一致不能宣称源工程无损。graph-v1 范围、制品限制与未知数据拒绝规则见 docs/15。

- P5 批量是一个候选的一次发布，verified 日志不等于已发布；未知步骤不可自动续跑。大 XML 走制品与固定私有文件，ZIP 必须绑定准确 m2 修订及完整摘要。完整规则见 docs/16；统一代码检查入口为 python eng/verify.py。

- P6 接入只处理 imports 下明确声明的已保存文件；上下文和差异绑定历史修订与当前头。补丁必须验证完整源码摘要与唯一原文，经过批量最终编译后发布。恢复备份当前头并创建新版本，未知结果继续隔离。完整约束见 docs/17。

- P7 保留标准 MCP Server 边界，不管理模型与会话。查询续读绑定修订与范围；成员补丁校验实际成员/访问器身份。批量设备范围和外部库管理器必须显式声明，共享修改检查并编译全部 Application。详见 docs/18。

- P8 graph-v1 保留兼容，graph-v2 标签边参与循环及传递顺序检查；SFC 选择优先级与布局顺序一致、分支必须完整对应汇合。新增类型/端口/分支需纳入语义哈希、差异及发布前证明；子对象保护不取消。完整范围见 docs/19。

## P9 正式执行
- 本地工具与 IDE 能力分开。工具发现、调用、提交、执行使用同一门禁，必须先验证批次依赖再复制/写候选。
- 真实验收绑定 EXE、引擎、Profile、脚本、平台执行代码和成功调用证据；模拟证据不能晋升。范围未知时拒绝，不能把 ST 或单对象测试推广成所有语言。
- 实际 CLR 进程与加载引擎须在工程操作前匹配；接入版本/策略差异不得猜测。
- 预发布交付包含三种隔离模式、SHA256、提交来源，不随包分发真实工程或本机验收资料。

## P10 收口
- 维护清理只能复用服务独占锁，在停服且无未解决作业的情况下预览/执行；不删除已发布版本、备份、交换制品或执行证据。
- 修改工具后同步契约与自动生成清单；真实模型回归未执行必须报告 skipped，不拿 CLI 连接或模拟编译代替。
- 定时 SFC duration 必须参与语义摘要与往返验证，旧 graph-v1 保持兼容，图形子对象保护不取消。
- 候选版本由 release/version.json 决定；完整 main CI 通过后发布 prerelease，已有标签/附件不可覆盖。

## P11 图形初始化
- 聚合初值必须使用 TC6 arrayValue/structValue；简单初值与聚合初值互斥，不用文本冒充数组结构。
- 保留成员与数组条目顺序、重复次数；总节点、嵌套深度和重复展开规模都必须有界。
- 初值参与语义摘要、读回证明和修订差异；未知初始化属性不能被几何属性忽略规则丢弃。
- DUT 成员类型和 ST 初值表达式由真实 IDE 编译核验，fixture 不证明类型或设备逻辑正确。
