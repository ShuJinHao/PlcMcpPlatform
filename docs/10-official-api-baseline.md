# 官方接口对照与原创 ScriptEngine 探针

核对日期：2026-11-06。适用阶段：P3。目标：由 OpenCode 调用自主实现的 .NET MCP 服务，通过官方 ScriptEngine 接口完成汇川工程编程。

## 实现来源

PLC 工程用例、汇川适配器、脚本与工具契约由本项目自主编写，不调用或移植第三方 PLC MCP 服务、打包文件或脚本。MCP 协议继续采用官方 C# SDK；IDE 的工程 API 仍由供应商运行环境提供。

供应商提供的 `ScriptEngine.chm` 是本次离线对照基线。标题为 `Script Engine Plugin API Reference`，相关页面标注 `ScriptEngine.dll Version: 3.5.9.0`。文件 SHA-256：`cbd1577977333a8cea8a143389826e8f93f6f06a32a4d3a3d054d1633f8e7483`。只记录来源、摘要和接口名，不在公有仓库上传原始 CHM 或其整站解包内容。

[汇川官方介绍](https://www.inovance.com/global/content/details_759_363227.html)确认 InoProShop 与 CODESYS 的技术关系；本次检索没有找到与这份 CHM 精确对应的汇川独立公开 ScriptEngine API 页面。具体 API 以供应商 CHM 和下列 CODESYS 官网文档对照，不把通用 CODESYS 的文档保证自动扩大为某个 InoProShop 版本的兼容保证。

## 三种版本必须分开

| 标识 | 本次证据 | 用途 |
|---|---|---|
| InoProShop 产品版本和 Profile | 用户目标环境尚未采集 | 决定真正执行环境 |
| 离线 CHM 中程序集版本 | `ScriptEngine.dll 3.5.9.0` | 定位供应商交付文档基线 |
| 当前在线文档包版本 | [索引](https://content.helpme-codesys.com/en/ScriptingEngine/idx-codesys_scripting.html)标注 CODESYS Scripting AddOn `4.2.0.0` | 查找扩展和差异；成员的 `Version added` 另行记录 |

不能将三者互相替换，也不能仅凭程序集版本号推导完整兼容。在线资料随产品更新；下表记录的是本次阅读结果。

## CHM、官网与 MCP 能力对照

表中包含已实现及后续规划的工具名称。生产 Host 当前只注册两个平台查询工具；独立测试宿主已有 49 个工具，包括读/编译、程序写入、保存和对账；生产工程能力仍未开放；专用真实回归入口见 [第三批实现](14-engineering-configuration.md)。

| 工程能力 | CHM 证据 | 官网核对 | 实现目标与待验证项 |
|---|---|---|---|
| 工程生命周期 | `IScriptProjects.open/create`；`IScriptProject.save/save_as/close` | [ScriptProjects][projects]、[ScriptProject][project] | `plc_project_open/create/save`；模板、依赖与保存重开；`close` 会丢弃未保存修改，不能机械包装 |
| 工程对象树 | `IScriptTreeObject.get_children/find`、`IScriptObject.guid/get_name` | [ScriptTreeObject][tree]、[ScriptObject][object] | `plc_project_get_structure`；对象身份、分页和截断，GUID 与工作区绑定 |
| POU/GVL/DUT | `IScriptIecLanguageObjectContainer.create_pou/create_gvl/create_dut` | [IEC 对象][iec] | `plc_pou_create`、`plc_gvl_*`、`plc_dut_*`；以目标版本实际重载为准 |
| Method/Property/Action | `IScriptIecLanguageMemberContainer` 对应创建方法 | [IEC 对象][iec] | 校验父对象及语言适用范围 |
| 文本程序读写 | `textual_declaration/textual_implementation`、`IScriptTextDocument.get_text/replace` | [ScriptTextualObject][text] | `plc_pou_read/update`；区分声明和实现，重载参数和位置基准按版本测试 |
| 图形语言 | `IScriptImplementationLanguages` 包含 LD/FBD/SFC/CFC 等 GUID，`create_pou` 有语言参数 | [IEC 对象][iec] | 单独验证创建、网络编辑、XML 往返、保存重开与编译；语言标识不等于完整网络编辑 |
| 设备 | `IScriptDeviceObject.add/plug/update`、`device_parameters` | [ScriptDeviceObject][device]、[ScriptDeviceParameters][parameters] | `plc_device_*`；设备描述、插槽、参数错误与读回 |
| IO 变量映射 | `IScriptDataElement2.io_mapping`、`IScriptIoVariableMapping.variable`；设备对象 CSV 导入导出 | [参数与映射][parameters]、[设备对象][device] | `device.io_mapping` 有直接文档依据，验证具体可映射通道和 CSV 路径 |
| Task 与调用绑定 | 未在此 CHM 检出通用 `create_task_configuration/create_task/pous`；Module TaskInfo 不能替代 | [ScriptApplication][application]、[ScriptTaskConfigObject][tasks]：对应类/方法自 `3.5.10.0` 引入 | `plc_task_*`；读取/创建/编辑/POU 绑定分别验收，不能直接标记不支持 |
| 库管理 | `IScriptLibManObject.get_libraries/add_library/remove_library/add_placeholder` | [ScriptLibManObject][libraries] | `plc_library_*`；已安装库、占位符解析和真实版本 |
| XML 交换 | `import_xml/export_xml`、`import_native/export_native` | [ScriptProject][project]、[ScriptObject][object] | `plc_project_import/export`；记录格式、目标对象与丢失项 |
| 编译与诊断 | `IScriptApplication.build/rebuild`、`ISystem3.get_message_objects`、`IScriptMessage` | [ScriptApplication][application]、[ScriptSystem][system] | `plc_project_compile`；主工程 Application、当次消息范围、对象和位置转换 |

## 必须修正的边界判断

1. **Task 调用有官方接口依据。** 官网定义 `task.pous` 及其集合 `add/insert/replace/remove`。该接口族在 CHM 基线之后加入，这是本次对照得到的差异；目标 InoProShop 是否暴露仍需实测。
2. **IO 变量映射有双重文档依据。** 映射的 `variable` 可读写，设备对象也提供 CSV 导入导出。不能因为其他 MCP 未实现就判定平台不能实现。
3. **IO 映射不等于 PDO 配置。** 本轮查阅未确认完整的 EtherCAT PDO 专用编辑路径，保持 `Unverified`，继续按目标设备和官方扩展资料验证。
4. **普通 POU 与特殊检查 POU 的重载分开。** CHM 明确给出按名称、类型和语言创建的 `create_pou`；当前网页的同名入口展示 `SpecialPouType`，另有 `create_program/create_function_block/create_function`。不能据此认定旧签名被删除或新方法可用于旧 IDE。
5. **图形对象与图形网络分开。** [PLCopen XML 官方说明][xml]包含格式表达限制；每种语言需独立样例。SFC 的语言适用范围也要遵循 IEC 对象页说明。

## 文档证据到支持声明

| 层次 | 证明什么 | 不能证明什么 |
|---|---|---|
| Documented | 某份官方资料描述接口和条件 | 当前厂商安装已提供该能力 |
| API observed | 当前对象上可读取某个成员或方法引用 | 签名匹配、调用成功、修改持久化 |
| Operation verified | 在明确版本、样例和前提下执行并读回、重开、编译 | 其他版本、其他对象或在线运行已验收 |

前两层仅作为开发证据，不改变 MCP 能力的 `Unverified` 状态。验证成功后再按实际范围登记 `Supported`；缺少对象、库、插件或设备描述不直接归类为 `Unsupported`。

## 已编写的原创只读探针

脚本：[inspect_session.py](../src/adapters/PlcMcp.Inovance/Scripts/inspect_session.py)。这是内部开发探针，不是生产 MCP 工具，也不是完整 Worker。

行为：读取现有脚本绑定、Python 运行时、已加载的相关程序集版本、语言标识、当前主工程的对象树和白名单成员。对任务对象检查 `pous` 集合的操作成员是否可见。默认最多 256 个对象、深度 16；截断、重复对象和检查错误显式记录。

脚本不读取程序正文，不自动打开或关闭工程，不保存、编译、修改或访问在线 PLC。它读取工程脏标记，但脏标记不能替代修订检查或一致性快照。输出对象名称可能包含用户工程信息，实测报告在本机保留，公开提交前脱敏。

在目标 Windows 机上：

1. 用 InoProShop 打开一个脱敏样例工程，记录“帮助/关于”中的完整产品版本与 Profile。
2. 通过该 IDE 提供的脚本执行入口运行上述文件；CODESYS 官方说明支持界面入口与命令行。具体菜单是否被厂商改名需按安装版本确认。
3. 在脚本输出中获取以 `PLCMCP_PROBE_V1 ` 开头的一行，其后为 JSON。报告的 `runtime.inoproshopVersion/profile` 留空，需要结合现场记录补齐；程序集版本不能冒充产品版本。
4. 核对 `status`、`issues`、`truncated` 和具体对象的 `members`。没有打开工程时返回 `no_primary_project`；缺少脚本绑定时返回 `scriptengine_bindings_missing`。
5. `evidenceKind=api_presence_only`、`engineeringReady=false` 是有意保留的边界。探针成功不能使工程能力自动变为支持。

[官方命令行文档][cli]提供 `--profile`、`--runscript`、`--scriptargs` 和 `--noUI`。命令行与用户会话路径分别实测；本探针不传工程路径、不自动打开样例，因此命令行新会话无主工程时只产生环境证据。已实现的工程执行脚本使用自己的受控工程生命周期，但目标 IDE 启动方式尚未验证。

普通 `python inspect_session.py` 不具备 IDE 对象；其返回 `unavailable` 是预期行为。CPython 只用于以下边界测试：

```sh
python -m unittest discover -s tests/script_engine -v
```

测试覆盖无 IDE/无工程、只读约束、Task 成员检查、节点/深度上限、循环、部分枚举失败、属性异常与结果序列化。测试替身不模拟厂商真实实现，不能作为 IronPython 或 InoProShop 验收证据。

## 实现顺序与当前进度

1. 固定请求/结果契约，先实现持久化任务、互斥、进程执行器、超时和结果未知恢复；无需目标 IDE 即可进行服务开发和自动测试。
2. 按官方 API 编写工程打开、结构读取、程序读取和编译诊断脚本，接入 .NET 应用端口与 MCP 工具；解析本次诊断，不从进程退出码推断编译成功。
3. 在工作区修订、备份和读回规则就绪后实现程序创建/修改/保存，再扩展 Task、IO、库和各图形语言。独立测试宿主用于离线契约与流程验证，不改变真实能力状态。
4. 以后取得 Windows/InoProShop 环境，再运行探针并完成各项真实操作验收，补齐版本、Profile、样例与证据，逐项开放生产能力。

上述第 1～2 项和第 3 项中的 POU/GVL/DUT 创建、写入、保存重开已完成离线实现；其余工程能力继续开发，真实 IDE 仍未验证。完整顺序和每批验收标准见[下一阶段开发与交付计划](11-offline-development-plan.md)。

## 第二批调用补充

原创编辑脚本采用 CHM 中的普通 `create_pou(name, type, language, return_type)`、`create_gvl(name)`、`create_dut(name, type, baseType)` 和文本 `replace(new_text)`，对照 [IEC 容器][iec]、[文本对象][text] 与 [工程保存][project] 官网页面；不把不同重载当作可互换签名。Function 返回类型、DUT Alias 基础类型和 SFC 适用范围分别检查。

创建图形语言对象只确认类型标识，不等于编辑网络。保存后关闭并重开自有候选会话，核对实际对象与文本；替身检查调用流程，不提供真实版本支持证明。设备标识取自 [设备对象][device]，库引用取自 [库管理器][libraries] 的 `get_libraries(False)`，不据此宣称设备包/库已经安装并解析。

[projects]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptProjects.html
[project]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptProject.html
[tree]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptTreeObject.html
[object]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html
[iec]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html
[text]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptTextualObject.html
[device]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceObject.html
[parameters]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptDeviceParameters.html
[tasks]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptTaskConfigObject.html
[libraries]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptLibManObject.html
[application]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptApplication.html
[system]: https://content.helpme-codesys.com/en/ScriptingEngine/ScriptSystem.html
[xml]: https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_cmd_import_plcopenxml.html
[cli]: https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_commandline.html

## P3 调用实现对照

新增 `configuration_worker.py` 负责 Task、成员、对象、Library、Device 与参数映射；`engineering_worker.py` 保持统一自有会话与保存重开边界。没有复制供应商文档正文或第三方脚本到仓库。

| 路径 | 采用的官方接口 | 不能据此宣称的内容 |
|---|---|---|
| Task | create_task_configuration、create_task、kind_of_task、watchdog、pous.add/remove | 3.5.9 CHM 没有完整对应接口；目标 IDE 集合形态和限制仍待验收 |
| Member | create_method/create_property/create_action、textual_declaration/textual_implementation | 图形网络编辑；所有 IDE 共用 Action 类型标识 |
| Object | create_folder、rename、move、remove、guid/type | 全文引用自动重构 |
| Library | librarymanager 安装清单、references、add/remove、占位符解析与重定向 | 整个依赖环境已经安装、解析和编译正确 |
| Device | device_repository 描述查询、add/plug、device_parameters、connectors.host_parameters | 所有设备插槽、EtherCAT PDO 或在线功能都可操作 |
| Parameter/IO | 叶子 value、offline_access_rights、范围/枚举、io_mapping.variable | SDK 接收字符串即代表值有效；变量映射替代类型编译检查 |

实际完成范围、明确限制及回归入口见 [第三批实现说明](14-engineering-configuration.md)。

## P4 官网补充对照

本批实现进一步核对官方 [ScriptProject](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptProject.html) 与 [ScriptObject](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html) 的 PLCopen/native 导入导出、冲突和报告接口。Project 与对象级 import_xml 的参数不同；当前使用 reporter 处理冲突，不给 Project 传入对象重载专有参数。原生接口按 filenames/handler 与 destination/reporter 调用，并检查 NativeImportResult。

官方报告区分非导出对象、警告、取消与丢失序列化成员。递归导出可能静默忽略不可导出子对象，因此本实现先展开目标，再显式选择对象以取得报告。TC6 2.01 图形格式依据 [PLCopen 官方下载](https://www.plcopen.org/downloads/) 中的 schema；三个原创样例做过 schema 校验。此处是官网文档对照和离线代码证据，没有补作目标汇川 IDE 的运行确认；CHM 的既有版本基线也没有据此升级。细节见 [docs/15](15-engineering-exchange.md)。
