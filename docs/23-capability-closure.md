# 当前功能范围与剩余缺口（P14）

核查日期：2026-11-07。实现状态与厂商支持状态分开。当前无真实 IDE 验收记录，表中的“已实现”均不能改写为汇川 Supported。逐工具入口见 [74 个工具清单](22-current-tools.md)。

| 能力域 | 已有代码范围 | 尚缺或需验证 |
|---|---|---|
| 工程与版本 | 模板创建、已有工程接入、受控副本、保存重开、历史/差异/恢复、准确修订交付 | 真实模板、设备/库依赖、IDE 版本与人工并发编辑回归 |
| 文本对象 | POU/GVL/DUT；Method/Property/Action 及属性访问器；精确补丁和批量编译 | 目标版本重载、语言和成员类型证据；不把图形内容当文本 |
| LD/FBD | graph-v1/v2、返回类型、数组/字符串、标签和双向引脚；有界初值；声明分组、常量/保持属性、地址、注释、临时/外部变量；有界枚举及整数子范围；P14 的标准结构体/指针、持久性标志和 XHTML 声明注释见下节 | 目标匿名类型导入/编译、持久变量列表及实例路径关联待真实验证；通配地址、未建模的身份元数据和任意厂商扩展仍不开放 |
| SFC | 基础流程、成对选择/并行分支、跳步、ST Action；有界定时动作、变量聚合初值、多动作关联与次序元数据 | 真实 IEC Action 执行顺序；任意跨分支跳转、未汇合图、厂商标志、独立图形子对象安全替换 |
| CFC | 语言对象创建入口、官方原生交换调用 | 无结构化网络编辑器；原生交换也待目标样例验证 |
| Device/Task/Library | 设备树/参数/IO 变量映射、任务配置与调用绑定、库和占位符 | 全部目标版本仍需验证；通用 IO 不包含 PDO 布局编辑 |
| EtherCAT PDO | 设备与通用参数/IO 路径可复用 | 专用 PDO 条目增删、分配、排序和布局编辑未实现 |
| 执行与交付 | 串行持久化作业、未知结果隔离、冲突控制、证据门禁、ZIP 和停服保留策略 | 真实安装兼容与统一工程回归；Windows Session 0 未验证 |
| OpenCode | 标准 MCP 配置、CLI 连接检查、可选模型任务验收脚本 | Mac 实际模型结果见 [P14](27-standard-declarations.md)，P13 历史结果见 docs/26；不开发模型/会话系统 |
| 在线 PLC | 能力规划与独立边界 | 下载、监视、启停未作为已实现工具开放，不属于本候选版本完成项 |

## 标准表达与厂商证据分开

| 状态 | 对应范围 | 下一步 |
|---|---|---|
| 已实现待真实验证 | 标准结构体、指针、持久性声明标志、XHTML 注释及已有离线工程能力 | 最终 Windows 核验导入、保存重开、编译与版本兼容 |
| 缺资料，尚未实现 | CFC 结构化网络及顺序、PDO 条目/分配/排序、含图形子对象的安全整体替换 | 按下文补齐目标接口/格式和成对样例后实现，保护保持有效 |
| 未实现，后续独立范围 | 西门子、欧姆龙适配；在线下载、监视、启停 | 不计入本批完成范围 |

P13 将内嵌结构体、指针、PERSISTENT、格式化注释与任意厂商扩展放在同一缺口行，分类过粗。TC6 2.01 XSD 明确定义 `struct/varListPlain`、`pointer/baseType`、`varList.persistent/nonpersistent` 和 `formattedText` 的 XHTML 内容。这些标准表达不缺格式依据，属于本批 Mac 实现范围；具体契约、限制和验证状态见 [P14 标准声明](27-standard-declarations.md)。

持久性标志可往返，并不等于已经创建持久变量列表、关联实例路径或证明断电保持。后两类证据不能用标准 XML 校验或 fixture 编译代替。未建模的 `addData`、通配地址和对象身份数据仍明确拒绝，不以删除未知数据的方式完成图形修改。

## 本轮供应商文档核对

重新读取用户提供的 ScriptEngine.chm，SHA-256 与既有基线一致：`cbd1577977333a8cea8a143389826e8f93f6f06a32a4d3a3d054d1633f8e7483`。本地解包 1618 个 HTML 页面，程序集版本基线为 3.5.9.0。原始文件、解包内容和辅助解码库均未加入公有仓库或发布包。

- CFC 对应 `IScriptImplementationLanguages.cfc/page_oriented_cfc` 两个属性页。语言 GUID 是对象创建依据，不是网络节点、连线或执行顺序编辑 API。
- `IScriptIecLanguageMemberContainer.create_action` 与 `IScriptObject.import_xml` 等提供对象及交换接口；没有因此取消“目标图形 POU 含子对象时拒绝整体替换”的保护。
- 本轮对全部 1618 个 HTML 页面检索 `PDO`，未出现匹配；`EtherCAT` 匹配 3 页，均属于 Application Composer 的 `IScriptModuleIoInstance.set_io_channel_mapping` 及其接口/方法索引。这是物理 IO 通道路径示例，不是 PDO 条目、分配或布局 API；不能据此判定所有厂商版本均无扩展。
- `IScriptDataElement2.io_mapping` 提供已有可映射数据项的映射关系，不能据此推导可任意改变 PDO 分配与布局。

## 官网对照与本轮结论

| 项目 | 官方依据 | 决策与下一份必要证据 |
|---|---|---|
| CFC | [语言对象](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html)、[执行顺序](https://content.helpme-codesys.com/en/CODESYS%20CFC/_cds_dlg_properties_cfc_execution_order.html)、[标准交换范围](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_cmd_export_iec_61131_10.html) | 标准交换明确不覆盖 CFC。自动数据流顺序与显式顺序是不同模式；官方把 SP14 及以前列为显式模式的历史行为，不能套到未知 InoProShop 版本。需目标接口或定义明确的原生结构、模式/反馈起点/顺序的成对样例 |
| SFC 定时动作 | [动作限定符](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_action_qualifier.html)、[动作对象](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_element_action.html)、[PLCopen XML](https://www.plcopen.org/standards/logic/iec-61131-10/) | 现有 TC6 2.01 XSD 的 action 支持 qualifier/duration，因此补齐有界常量表达、往返和校验。实际对象形态与执行行为留待目标 IDE 验证 |
| 图形子成员 | [成员创建](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html)、[对象导入导出](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html)、[导出回调](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptProject.html#ScriptProject.ExportReporter.nonexportable) | `recursive` 不是完整保留证明；标准导出对不可导出的后代可能静默跳过。需要导入前后对象树、GUID、任务/源代码引用、成员/访问器内容及重开证据；没有它们不放开整体覆盖 |
| EtherCAT PDO | [设备参数与 IO 映射](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceParameters.html) | 参数集合由设备描述决定且不允许任意增删。需设备/插件型号版本和专用映射 API 或明确可往返的配置格式；保持未实现 |

定时动作的实现范围与时间常量限制详见 docs/21；32 位毫秒上限依据 [TIME 类型](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_datatype_time.html)，只接受正整数单单位是本项目当前的有界子集。保留 graph-v1 兼容，既有 graph-v2 只增加可选字段，不新建空能力工具。未确认路径的项目保持“本平台未实现/证据待补”，不标记供应商绝对 Unsupported。

本轮继续检索汇川官网/官方文件域的 `InoProShop ScriptEngine`、`CFC 脚本 API`、`PDO 映射`，以及 CODESYS 官方脚本参考和 CFC 示例。核查的汇川官方 [19012378 A00 运动控制编程指南](https://portal-file.inovance.com/owfile/ProdDoc/SC/19012378-SC/A00/19012378-SCY_A00%20Medium-sized%20PLC%20Programming%20Guide%20%28Motion%20Control%29-EN.pdf) 为 2024-09 A00，适用于手册列出的 AM400/AM500/AM600/AC700/AP700/AC800，不能由其推断用户安装版本。PDF 页 17、32、63～64 介绍 PDO 配置和运动功能所需对象，但没有给出 IDE 脚本条目增删、分配与重排契约。文件 SHA-256：`3700c87e6e6cd5c461232e895cc974c50023a2f8a09e0cf0dce9f76cdcaa828b`；原件仅留仓库外。

[官方 CFC 编辑示例](https://content.helpme-codesys.com/en/CODESYS%20CFC/_cds_f_programming_language_cfc.html)提供图形操作和功能块调用示例，但本轮没有取得与目标 InoProShop 版本绑定的成对原生导出样例。`export_native/import_native` 的公开接口自 3.4.4.0 提供，CHM 3.5.9.0 也有对应接口；这证明存在交换调用，尚不定义各对象的可编辑序列化结构。官网当前的部分比较 API 从 3.5.12.0 才提供，不能默认存在于 CHM 对应的旧引擎。

## 阻塞解除所需材料（Windows 阶段最后采集）

| 项目 | 必须补齐的最小证据 | 开放条件 |
|---|---|---|
| CFC 网络 | 精确 IDE/Profile/引擎版本；普通及分页 CFC 的原生格式依据；节点/端口/连线、布局、反馈起点及两种顺序模式的修改前后样例 | 可识别全部所选数据，生成后导入、保存重开、读回及编译一致；不能用 FBD 顺序字段冒充 CFC |
| PDO 配置 | 主站/从站型号、设备描述与插件版本；官方专用接口或明确配置格式；Rx/Tx 条目索引、子索引、长度、分配、排序/偏移的修改前后样例 | 能独立读回条目与分配布局，验证不改动未选范围；ESI 默认对象字典或已有 IO 映射不能替代工程配置证明 |
| 图形子对象 | 带 Method、Property/get/set、Action 及引用/任务绑定的独立脱敏图形 POU；替换前、后、重开后的对象树、GUID、内容和导入回调 | 所有需保留的对象、引用和内容可核对；发生跳过、身份变化无法对账、丢失或未知数据时拒绝发布 |

用户已明确 Windows 全部留到最后，包括样例采集。因此本批不提前启动 IDE 或索取 Windows 导出；缺上述材料的项目继续标为阻塞。最终 Windows 阶段先补证据、完成对应实现并复验，再判断全覆盖是否达到，而不是直接把已有候选包称作全量适配完成。

## 验证分层

1. 当前代码回归：领域、持久化、HTTP、脚本替身、文件与包完整性。
2. 可选模型回归：真实 OpenCode + 指定模型 + fixture，验证实际调用和诊断修复；报告仍是模拟工程。
3. Windows/IDE 回归：记录 EXE/加载引擎/Profile、模板、设备/库、保存重开和编译结果，按实际范围批准。
4. PLC 运行验收：后续独立范围，不能由编译通过代替。

官网更新不自动提升目标 InoProShop 兼容范围，也不按未经观察的 IDE 版本号创建“已适配”实现。

P11 按 TC6 2.01 的 value/arrayValue/structValue 结构补齐有界聚合初值；支持部分数组初始化、重复条目、结构成员及嵌套组合，限制与官网依据见 [P11](24-graph-initializers.md)。不自动展开重复、不推断成员类型、不因此解除图形子对象保护。

P12 扩展声明组和 SFC 动作关联，并在应用端口后增加汇川目标规则及执行前重检；详见 [P12](25-engineering-semantics.md)。公开资料与 XSD 支持格式设计，不代表目标旧版 IDE 已通过导入和运行验收。
