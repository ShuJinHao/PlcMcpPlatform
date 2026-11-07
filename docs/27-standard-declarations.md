# P14：标准声明补全与模型编程稳定性

本批从 `0ffa17b` 继续，仍使用一个标准 MCP Server；OpenCode 负责模型和会话，DeepSeek Flash 负责实际生成。Windows 操作及样例采集全部留到最后。实现标准格式、Mac 自动回归、模型真实调用、目标 InoProShop 兼容性分别报告。

## 标准依据与能力边界

[PLCopen 官方下载](https://www.plcopen.org/downloads/)中的 [XML 2.01 XSD](https://www.plcopen.org/download_file/force/d397ab98-6a21-47d5-9fb1-37da9e94ef34/342/) 定义了 `struct/varListPlain`、`pointer/baseType`、声明组的 `persistent/nonpersistent` 属性以及 `formattedText` XHTML 内容。P13 把这些与任意厂商扩展并列为资料不足，分类不准确；本批把标准字段实现和真实厂商验证拆开。

| 范围 | 本批标准表达 | 不能据此推导 |
|---|---|---|
| 内嵌结构体 | 成员名称/顺序、递归类型、已有初值表示、受支持地址及注释 | 所有目标版本都接受匿名结构，或知道实际内存布局 |
| 指针 | 目标类型、数组/结构组合、返回类型 | 实际地址有效、解引用安全或 PLC 运行正确 |
| 持久性 | 独立于 retention 的 persistent/nonpersistent 标志，允许表达 RETAIN + PERSISTENT | 已创建持久变量列表、建立实例路径、验证下载/重启/断电行为 |
| XHTML 注释 | 保留命名空间、结构、属性、文字与空白，作为数据往返 | 支持任意厂商扩展，或可把内容当界面 HTML 渲染执行 |

[CODESYS PERSISTENT 说明](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_var_persistent.html)要求结合持久变量列表或实例路径理解声明行为。函数块声明及 AT 地址也有目标约束。仅在局部声明出现 PERSISTENT，不能作为完整持久化配置验收；断电保持属于本批范围外的 PLC 运行验收。

## graph-v2 契约

保持原有 `graph-v1/v2` 请求、默认行为和旧固定摘要。新增字段只在 graph-v2 使用；不存在的新字段不会改变旧摘要，未知结构不通过丢弃或猜测处理。

- `structMembers`：`type="STRUCT"` 时提供有序成员。每个成员包含 `name`、嵌套 `type`，以及可选 `initialValue`/`initializer`、`address`、`documentation`/`documentationXhtml`。简单初值与聚合初值互斥。
- `pointerTo`：`type="POINTER"` 时提供目标 `GraphDataType`。类型可递归组合数组、结构体、指针和已有标量/枚举/子范围；不求值指针地址。
- `persistence`：变量组可选 `default`、`persistent`、`nonPersistent`，与 `retention` 分别表达。XML 的显式 false 与默认 false 在语义上归一；互相冲突的标志拒绝。
- `documentationXhtml`：变量、变量组和结构成员的可选 XHTML 片段，与同位置的纯文本 `documentation` 互斥。保留数据结构，不渲染、不执行、不解析外部资源。

类型限制为深度 8、每请求最多 4096 个类型节点、每结构最多 256 个成员。XHTML 每项最多 16384 个字符、深度 16、1024 个节点；元素限定 XHTML 命名空间，属性限定无命名空间以及 `xml:lang`/`xml:space`。DTD、外部实体、处理指令、XML 注释及未表示数据拒绝，不静默剥除。结构成员地址继续使用已有明确地址范围，不因此开放通配地址。

新字段必须进入语义摘要、修订差异、批量修改的候选发布前核对、保存重开读回和准确修订 ZIP。类型/格式校验与汇川执行策略分层；真实持久变量关联未验证不改为 Supported。有图形子对象的 POU 整体替换保护保持有效。

## 模型诊断与验收

图形错误信息给出可以核查的字段位置、违反的规则和修改方向，重点覆盖 SFC 端口数量、连接归属、动作关联、可空字段和执行顺序。保留错误码与严格校验；不能删除字段或忽略未知内容来制造成功。

MCP 文本结果直接保留中文路径，结构化结果、Worker 序列化与工程摘要不变。路径读取说明要求原样使用服务端返回值；HTTP 检查使用中文工作目录验证文本与结构化数据一致，路径权限校验不放宽。

自动检查覆盖：

1. 官方 XSD 校验；结构/指针嵌套及数组组合往返；成员顺序、初值、地址和注释保留。
2. 持久性与保持属性组合、显式 false/默认值、互斥标志、目标约束；不把标准字段校验当作持久存储验收。
3. XHTML 结构/属性/混合文字保留、规范化摘要、复杂度上限和未知/非法数据拒绝。
4. 新声明进入差异、失败批次不发布、最终读回及工程文件摘要与 ZIP 一致；旧固定摘要与旧请求仍通过。
5. 原五组 DeepSeek Flash 模型流程、新增声明流程，以及三个独立工作区的图形流程；报告调用次数、无效调用数和耗时，结论由服务作业、最终工程和准确修订交付共同确认。

本批自动检查通过：Release 构建零警告/错误，294 项 .NET、116 项 Python 测试和全部 HTTP/故障/发布校验；新声明 XML 通过官方 TC6 2.01 XSD。集中工程回归为十套、241 个场景步骤。OpenCode 1.18.2 + 官方 `deepseek/deepseek-flash` 的本批八次实际模型回归全部通过，不使用 P13 的结果冒充本批。原 P13 证据保留在 [P13 说明](26-mac-windows-handoff.md)。Windows/InoProShop 与 PLC 运行验收尚未执行。

模型集中入口为 `python3 eng/dev.py model-suite --no-build --report artifacts/p14/models --timeout 1200`，必须选择新的报告目录；模型凭据延用仓库外配置。八次执行串行且各用独立工作区，失败即停止，不自动重试；每次保留 report.json、server-evidence.json 和准确修订 delivery.zip。指标包含工具调用总数、无效调用数、错误码分布和模型/总耗时，成功读取失败作业不会被误算成无效调用。声明场景同时核对初态和终态，要求模型观察初态读回后才提交修改，差异必须关联这两次语义摘要；仅生成最终内容不能通过。

## 本批实际模型结果

最终运行位于 `artifacts/p14/models-final/`，八个独立工作区全部通过；共 361 次工具调用、35 次无效调用，模型执行耗时合计 764.718 秒。无效调用包含严格校验拒绝和模型使用错误参数的调用，后续修正不删除前面的失败记录。这是有限场景的实测结果，不表示所有自然语言需求均可一次成功。

| 场景 | 工具调用 | 无效调用 | 模型耗时（秒） |
|---|---:|---:|---:|
| repair | 33 | 3 | 71.171 |
| graphics-1 | 46 | 4 | 124.276 |
| graphics-2 | 50 | 7 | 107.929 |
| graphics-3 | 50 | 6 | 108.613 |
| members | 56 | 5 | 109.947 |
| configuration | 52 | 3 | 79.826 |
| incremental | 41 | 3 | 80.238 |
| declarations | 33 | 4 | 82.718 |

每项均保留实际报告、独立查询的服务作业、最终工作区头和准确修订 ZIP。三次图形流程均未再出现中文路径误写；声明场景已证明 Count 从 1 改为 2、Cursor 从 POINTER TO DINT 改为 POINTER TO INT，以及 XHTML 加粗文字的修改；嵌套数组、成员顺序和持久性分组保留。

此前两轮失败保留在 `artifacts/p14/models/` 和 `models-retest/`：首轮图形调用 99 次、45 次无效后人工停止，依据诊断补齐规则说明；第二轮图形第二次调用 59 次、28 次无效，主要是模型误译转义中文路径，未满足最终读回而判失败。两次均查询服务作业，确认全部终态且调度器未阻塞，未自动重放未知写入。最后一轮在修正后的相同工程代码上完整重跑八次通过。

## 仍然阻塞的厂商能力

CFC 专用网络、PDO 条目/分配/布局以及图形子对象整体替换仍缺目标接口或完整往返证据。查阅到的 CHM、官方接口、汇川 A00 手册、CFC 示例与最小缺失材料已登记在 [当前缺口清单](23-capability-closure.md)。标准 struct/pointer/PERSISTENT/XHTML 不再与它们混为一类。

本批不提前执行 Windows，也不放宽图形子对象保护。最终阶段仍需补齐目标版本证据、实现阻塞项并复验后，才判断汇川全覆盖是否完成。候选包始终将 `engineeringReady`、真实 IDE 和 PLC 运行验证状态与 Mac 结果分开。
