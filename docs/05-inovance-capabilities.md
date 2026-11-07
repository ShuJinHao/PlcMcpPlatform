# InoProShop ScriptEngine 能力清单与验证矩阵


当前 P13 承接 P12 声明与多动作 SFC，新增有界枚举/整数子范围、Mac 原生 OpenCode 接入和 Windows 集中回归入口；仍为 74 个工具。实现、资料阻塞和真实验证分列于 [本批覆盖表](26-mac-windows-handoff.md#能力状态)。厂商能力全部 Unverified，不能由模型模拟结果提升。

## 状态规则

本表是完整覆盖目标，不是支持声明。当前尚未连接真实 InoProShop，下面所有能力均为 **Unverified（未验证）**。

| 状态 | 含义 |
|---|---|
| Unverified | 尚无本项目对目标版本的有效证据 |
| Supported | 指定版本/对象/条件下已通过测试 |
| Unsupported | 有证据证明指定范围不支持，必须记录原因和范围 |
| Unavailable | 能力可能支持，但当前环境缺依赖、授权或执行器不可用 |

API 名称、重载及版本依据供应商提供的 `ScriptEngine.chm` 与 CODESYS 官方文档交叉核对，详见 [官方接口基线](10-official-api-baseline.md)。PLC 业务脚本自主实现，不引用第三方 PLC MCP 的实现或限制作为能力依据。

证据分三层：文档描述接口、运行时检查接口可见、完整操作验收通过。前两层均不能直接把运行时能力改为 `Supported`。探针发现接口缺失也不能单独证明某能力不存在，仍需检查对象类型、IDE 版本、插件与替代官方路径。

## 全量能力登记

| 能力 ID | 风险类别 | 真实能力状态 |
|---|---|---|
| `project.create` | ModifyProject | Unverified |
| `project.open` | Read | Unverified |
| `project.close` | ModifyProject | Unverified |
| `project.save` | ModifyProject | Unverified |
| `project.save_as` | ModifyProject | Unverified |
| `project.backup` | ModifyProject | Unverified |
| `project.tree.read` | Read | Unverified |
| `pou.create` | ModifyProject | Unverified |
| `pou.read` | Read | Unverified |
| `pou.update` | ModifyProject | Unverified |
| `pou.delete` | ModifyProject | Unverified |
| `pou.method.manage` | ModifyProject | Unverified |
| `pou.property.manage` | ModifyProject | Unverified |
| `pou.action.manage` | ModifyProject | Unverified |
| `language.st.edit` | ModifyProject | Unverified |
| `language.ld.edit` | ModifyProject | Unverified |
| `language.fbd.edit` | ModifyProject | Unverified |
| `language.sfc.edit` | ModifyProject | Unverified |
| `language.cfc.edit` | ModifyProject | Unverified |
| `gvl.manage` | ModifyProject | Unverified |
| `dut.manage` | ModifyProject | Unverified |
| `object.move` | ModifyProject | Unverified |
| `object.rename` | ModifyProject | Unverified |
| `folder.manage` | ModifyProject | Unverified |
| `device.tree.read` | Read | Unverified |
| `device.add` | ModifyProject | Unverified |
| `device.remove` | ModifyProject | Unverified |
| `device.parameter.read` | Read | Unverified |
| `device.parameter.write` | ModifyProject | Unverified |
| `device.io_mapping` | ModifyProject | Unverified |
| `device.ethercat_pdo` | ModifyProject | Unverified |
| `task.read` | Read | Unverified |
| `task.create` | ModifyProject | Unverified |
| `task.update` | ModifyProject | Unverified |
| `task.pou_binding` | ModifyProject | Unverified |
| `library.list` | Read | Unverified |
| `library.reference.manage` | ModifyProject | Unverified |
| `exchange.codesys_xml` | ModifyProject | Unverified |
| `exchange.plcopen_xml` | ModifyProject | Unverified |
| `project.compile` | Read | Unverified |
| `online.connect` | OnlineControl | Unverified |
| `online.monitor` | OnlineControl | Unverified |
| `online.download` | OnlineControl | Unverified |
| `online.start` | OnlineControl | Unverified |
| `online.stop` | OnlineControl | Unverified |

`Read` 表示不意图改变 PLC 工程内容，不代表可以绕过 IDE 串行调度或不产生编译缓存。在线监视仍会访问真实设备，按独立在线权限处理。

## 按能力域验证

| 能力域 | 重点验证 | 证据要求 |
|---|---|---|
| 工程 | 创建模板、真实控制器、打开/关闭/保存/另存/备份 | 重开工程、控制器一致、文件指纹 |
| POU | Program/FB/Function/Method/Property/Action、声明/实现、删除 | 写前读取、写后读回、保存重开、编译 |
| ST | 文本编辑、中文、类型与变量引用 | 读回一致及编译诊断 |
| LD/FBD/SFC/CFC | 分别调查原生图形接口和 XML 路径 | 每种语言至少一个可重开的网络/步骤样例；保留连接/执行语义 |
| GVL/DUT | 结构体、枚举、联合体、数组、常量与变量 | 类型声明完整、引用可编译 |
| 设备 | 节点插槽、设备描述、参数、IO 与 PDO | 版本绑定、配置读回、目标工程可重开 |
| Task | 周期单位、优先级、创建/修改、POU 绑定 | 绑定列表真实包含目标 POU，不能用编译成功代替 |
| Library | 查询、添加、删除、版本与占位符 | 本机已安装库、解析结果与编译 |
| XML | CODESYS/PLCopen 按对象往返 | 列出属性、布局、扩展和对象丢失项 |
| 编译 | 错误/警告/对象定位、应用选择 | 故意错误样例和修复样例；保留本次诊断，不复用历史结果 |
| 在线 | 连接、监视、下载、启停 | 独立隔离环境、独立授权；当前不实施 |

## 每条验证记录的字段

`capabilityId / vendorId / ideVersion / profile / engineVersion / os / targetController / deviceDescriptionVersion / libraryVersions / templateFingerprint / scriptRevision / sampleId / expected / actual / status / evidenceReference / verifiedAt`。

测试失败需判断原因是 API 缺失、版本差异、前提缺失还是实现错误。只在确认限定范围不支持后标记 Unsupported。证据不包含公司生产程序和商业库内容。

## 官方资料核对结果

- 供应商 CHM 的相关程序集标注为 `3.5.9.0`，不是本项目已确认的 InoProShop 产品版本。
- 官网 [ScriptTaskConfigObject](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptTaskConfigObject.html)列出 `create_task`、`pous` 和集合的 `add/insert/replace/remove`，对应类标注自 `3.5.10.0` 引入。此 CHM 未检出这套接口，因此应补充验证，不能登记为不支持。
- CHM 的 `IScriptIoVariableMapping.variable` 与官网 [ScriptDeviceParameters](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceParameters.html)均描述映射变量读写；CHM 与官网设备对象文档也列出 IO 映射 CSV 导入导出。
- 图形语言的创建标识与网络编辑分别验证；通用 IO 变量映射与 EtherCAT PDO 配置分别验证，不相互替代。

[CODESYS PLCopen XML 文档](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_cmd_import_plcopenxml.html)说明该格式存在表达限制，因此“可以导入”不等于所有对象、布局和语义无损。

## P3 实现范围

Task、Member、Object、Library、Device/Parameter/IO 已有完整工具和原创脚本实现；新增 27 个工具的映射与限制见 [第三批说明](14-engineering-configuration.md)。能力目录同时保留领域范围 ID 与实际操作 ID。生产查询仍为 Unverified；专用验证宿主可按明确白名单执行并收集真实证据，不自动改变支持状态。

## P4 实现范围

新增 project.export / project.import / pou.graph.read / pou.graph.write 操作与 5 个本地制品/图形工具。LD、FBD、基础 SFC 的 graph-v1 内容已实现；CFC 仅走不透明原生交换调用路径，EtherCAT PDO 仍无独立编辑实现。SFC 子成员、复杂声明与未知扩展有明确拒绝规则，见 [第四批说明](15-engineering-exchange.md)。这些实现与自动检查均不改变厂商能力的 Unverified 状态。

## P5 实现范围

| 能力 | 代码状态 | 支持声明 |
|---|---|---|
| project.batch | 单 Application、有序 32 步上限、逐步保存重开、最终编译、一次发布 | 复用基础操作，仍 Unverified；不是新的厂商 SDK 方法 |
| project.package | 私有副本读取任务/编译，准确版本 ZIP 与摘要 | 真实环境、编译缓存与依赖兼容待验收 |
| XML 大文件 | 默认 8 MiB、上限 64 MiB、分块与固定私有文件 | 传输扩容不扩大图形语言或 XML 无损声明 |
| OpenCode CLI | 1.19.5 连接成功；HTTP 契约 67 工具 | 只证明连接；LLM 生成仍未验收 |

复杂 SFC、图形 POU 子成员、CFC 结构和 PDO 的证据调查见 docs/10；本批没有据语言枚举或通用 IO 参数接口提升这些能力。

## P6 实现记录

已实现工程接入、版本绑定上下文、text.patch 批量步骤、历史/差异/恢复的服务、原创 Worker 文本读取及自动回归。能力仍为 Unverified；源文本由实际文本标记读取，未推断语言 GUID。图形差异只覆盖 graph-v1，复杂 SFC、图形 POU 子成员、CFC 和 PDO 限制保持。详见 docs/17。

## P8 实现范围

LD/FBD 增加 Function 返回类型、数组/字符串长度、连接标签与 FBD 双向引脚；SFC 增加有界、完整汇合的选择/并行分支。旧 graph-v1 与语义哈希兼容，现有工具按 graph-v2 契约扩展。新增 31 个混合语言回归场景。复杂初值、任意跨分支流程、定时动作、图形子成员、CFC 结构与 PDO 仍有明确限制。代码和离线检查不提升厂商支持状态，详见 [P8](19-graph-v2-engineering.md)。
