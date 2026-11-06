# P10 功能范围与剩余缺口

核查日期：2026-11-06。实现状态与厂商支持状态分开。当前无真实 IDE 验收记录，表中的“已实现”均不能改写为汇川 Supported。逐工具入口见 [74 个工具清单](22-current-tools.md)。

| 能力域 | 已有代码范围 | 尚缺或需验证 |
|---|---|---|
| 工程与版本 | 模板创建、已有工程接入、受控副本、保存重开、历史/差异/恢复、准确修订交付 | 真实模板、设备/库依赖、IDE 版本与人工并发编辑回归 |
| 文本对象 | POU/GVL/DUT；Method/Property/Action 及属性访问器；精确补丁和批量编译 | 目标版本重载、语言和成员类型证据；不把图形内容当文本 |
| LD/FBD | graph-v1/v2、返回类型、数组/字符串、标签和双向引脚 | 复杂初值、内嵌结构/附加声明属性、厂商扩展仍不能无损表达 |
| SFC | 基础流程、成对选择/并行分支、跳步、ST Action；P10 新增有界定时动作 | 任意跨分支跳转、未汇合图、多个动作顺序及厂商标志、独立图形子对象安全替换 |
| CFC | 语言对象创建入口、官方原生交换调用 | 无结构化网络编辑器；原生交换也待目标样例验证 |
| Device/Task/Library | 设备树/参数/IO 变量映射、任务配置与调用绑定、库和占位符 | 全部目标版本仍需验证；通用 IO 不包含 PDO 布局编辑 |
| EtherCAT PDO | 设备与通用参数/IO 路径可复用 | 专用 PDO 条目增删、分配、排序和布局编辑未实现 |
| 执行与交付 | 串行持久化作业、未知结果隔离、冲突控制、证据门禁、ZIP 和停服保留策略 | 真实安装兼容与统一工程回归；Windows Session 0 未验证 |
| OpenCode | 标准 MCP 配置、CLI 连接检查、可选模型任务验收脚本 | 真实模型场景尚未执行，不开发模型/会话系统 |
| 在线 PLC | 能力规划与独立边界 | 下载、监视、启停未作为已实现工具开放，不属于本候选版本完成项 |

## 本轮供应商文档核对

重新读取用户提供的 ScriptEngine.chm，SHA-256 与既有基线一致：`cbd1577977333a8cea8a143389826e8f93f6f06a32a4d3a3d054d1633f8e7483`。本地解包 1618 个 HTML 页面，程序集版本基线为 3.5.9.0。原始文件、解包内容和辅助解码库均未加入公有仓库或发布包。

- CFC 对应 `IScriptImplementationLanguages.cfc/page_oriented_cfc` 两个属性页。语言 GUID 是对象创建依据，不是网络节点、连线或执行顺序编辑 API。
- `IScriptIecLanguageMemberContainer.create_action` 与 `IScriptObject.import_xml` 等提供对象及交换接口；没有因此取消“目标图形 POU 含子对象时拒绝整体替换”的保护。
- 在该 CHM 的页面名与正文中检索 PDO/EtherCAT 未发现对应条目；这仅说明本份资料未给出专用路径，不证明所有厂商版本不存在扩展。
- `IScriptDataElement2.io_mapping` 提供已有可映射数据项的映射关系，不能据此推导可任意改变 PDO 分配与布局。

## 官网对照与本轮结论

| 项目 | 官方依据 | 决策与下一份必要证据 |
|---|---|---|
| CFC | [语言对象](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html)、[CFC 说明](https://content.helpme-codesys.com/en/CODESYS%20CFC/_cfc_start_page.html) | 当前资料能证明语言/编辑器存在，不能建立脚本网络编辑契约。需目标 IDE 官方扩展 API，或有文档定义的原生交换结构及往返样例 |
| SFC 定时动作 | [动作限定符](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_action_qualifier.html)、[动作对象](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_element_action.html)、[PLCopen XML](https://www.plcopen.org/standards/logic/iec-61131-10/) | 现有 TC6 2.01 XSD 的 action 支持 qualifier/duration，因此补齐有界常量表达、往返和校验。实际对象形态与执行行为留待目标 IDE 验证 |
| 图形子成员 | [成员创建](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html)、[对象导入导出](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html) | 需要导入前后对象树、GUID、任务/源代码引用、成员/访问器内容及重开证据；没有它们不放开整体覆盖 |
| EtherCAT PDO | [设备参数与 IO 映射](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceParameters.html) | 参数集合由设备描述决定且不允许任意增删。需设备/插件型号版本和专用映射 API 或明确可往返的配置格式；保持未实现 |

定时动作的实现范围与时间常量限制详见 docs/21；32 位毫秒上限依据 [TIME 类型](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_datatype_time.html)，只接受正整数单单位是本项目当前的有界子集。保留 graph-v1 兼容，既有 graph-v2 只增加可选字段，不新建空能力工具。未确认路径的项目保持“本平台未实现/证据待补”，不标记供应商绝对 Unsupported。

## 验证分层

1. 当前代码回归：领域、持久化、HTTP、脚本替身、文件与包完整性。
2. 可选模型回归：真实 OpenCode + 指定模型 + fixture，验证实际调用和诊断修复；报告仍是模拟工程。
3. Windows/IDE 回归：记录 EXE/加载引擎/Profile、模板、设备/库、保存重开和编译结果，按实际范围批准。
4. PLC 运行验收：后续独立范围，不能由编译通过代替。

官网更新不自动提升目标 InoProShop 兼容范围，也不按未经观察的 IDE 版本号创建“已适配”实现。
