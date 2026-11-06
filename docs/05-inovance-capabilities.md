# InoProShop ScriptEngine 能力清单与验证矩阵

## 状态规则

本表是完整覆盖目标，不是支持声明。P0 没有连接真实 InoProShop，下面所有能力均为 **Unverified（未验证）**。

| 状态 | 含义 |
|---|---|
| Unverified | 尚无本项目对目标版本的有效证据 |
| Supported | 指定版本/对象/条件下已通过测试 |
| Unsupported | 有证据证明指定范围不支持，必须记录原因和范围 |
| Unavailable | 能力可能支持，但当前环境缺依赖、授权或执行器不可用 |

API 名称以 P1 实际探测为准。本表不编造厂商方法名。第三方项目和新版 CODESYS 文档只用作调查线索。

## 全量能力登记

| 能力 ID | 风险类别 | P0 状态 |
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

## 已知调查线索

[LIMIT-LMT 的开源实现](https://github.com/LIMIT-LMT/InoProShop_LIMIT_MCP)声明面向 InoProShop V1.9.1.6/SP11，公开工程、POU、设备和任务工具，并列出任务调用绑定、IO/PDO 映射限制。这些限制应进入本项目专项测试，不能直接套用为所有汇川版本的结论。

[CODESYS PLCopen XML 文档](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_cmd_import_plcopenxml.html)说明该格式存在表达限制，因此“可以导入”不等于所有对象、布局和语义无损。
