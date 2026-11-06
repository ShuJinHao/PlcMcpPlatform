# P12：工程声明与多动作 SFC

候选版本 `0.1.0-rc.3`。仍由 OpenCode 理解需求、生成代码和管理会话，调用本项目已有的 74 个标准 MCP 工具。P12 扩展图形程序的数据模型、XML 编解码、汇川预检查及工程回归；不增加客户端适配服务或第二个 MCP。

## 变量声明契约

`graph-v2` 的 `variables` 是唯一变量清单；可选 `variableGroups` 以名称引用清单中的变量，保留声明组的边界、顺序及组内变量顺序。存在显式组时，每个变量必须且只能被引用一次，名称大小写精确匹配，scope 必须一致。XML 及读回中的变量顺序服从组顺序；不要依赖输入清单的跨组排序。省略分组时沿用旧的 scope 分组行为，旧 graph-v1/v2 摘要不变。

```json
{
  "profile": "graph-v2",
  "variables": [
    { "name": "Limit", "type": "INT", "initialValue": "12" },
    { "name": "Count", "type": "DINT", "initialValue": "0", "documentation": "累计次数\n保持效果需要目标验证。" },
    { "name": "Start", "type": "BOOL", "scope": "local", "address": "%IX0.0" }
  ],
  "variableGroups": [
    { "scope": "local", "variables": ["Limit"], "constant": true, "name": "Parameters" },
    { "scope": "local", "variables": ["Count"], "retention": "retain" },
    { "scope": "local", "variables": ["Start"] }
  ]
}
```

以上是字段片段；完整生成请求还需要 name、pouType、language 和 nodes。

| 字段 | 当前范围 |
|---|---|
| group.scope / variable.scope | local、input、output、inOut、temp、external；变量默认 local |
| constant | 默认 false；常量组要求标量或聚合初值，portable 模型的 external 常量例外；不能和非默认保持属性组合 |
| retention | default、retain、nonRetain；映射 TC6 retain/nonretain。default 不代表显式 NON_RETAIN |
| name / documentation | 可选组名与纯文本注释；注释也可放在变量上；不是 ST 源码或格式化 HTML |
| address | 有界绝对地址 `%[IQM][XBWDL]?[数字][.数字…]`；最多四段 uint32、64 字符。拒绝通配符、AT 前缀和多行内容 |
| 规模 | 最多 128 个非空组、512 个变量；每段注释最多 4096 个 UTF-16 单元，XML 1.0 合法字符；既有 64 KiB 图形 XML 上限仍有效 |

注释保留中文、空字符串、换行和 XML 转义字符，CRLF/CR 规范化为 LF。组和变量只接受已建模的 XML 属性；包括位于声明里的 width/height，也不能当作图形几何数据丢弃。重复组、组名、边界和组顺序参与语义摘要。默认空修饰的传统单 scope 组可规范化回省略分组的旧模型。

## Portable 模型与汇川执行规则

领域层验证可表达性。`IPlcGraphPolicy` 是应用端口，`InovanceGraphPolicy` 在适配器中维护本轮保守子集；不向 Core 引入厂商 SDK。生成和解码可以处理 portable 结构，写入汇川工程时再应用目标限制：

- TEMP 仅用于 program/functionBlock，不能带 constant、保持修饰或地址。
- EXTERNAL 不在本地初始化或分配地址；本轮不开放其 constant/保持修饰，实际全局定义交由 IDE 检查。
- CONSTANT 本轮只开放 local/input，并要求初值；不解析 ST 常量表达式。
- 保持修饰不用于 function、temp、external；RETAIN 与绝对地址组合拒绝。
- 图形 POU 的地址声明仅开放 local，不能用于 constant 或 function；input/output/inOut/temp/external 均拒绝。合法语法不代表地址适合该数据类型、硬件布局正确或没有地址重叠。

这些是当前适配器的执行子集，不是对其他厂商的通用限制，也不是对汇川所有版本作 Unsupported 结论。实际保持内存、外部变量关联、地址类型、结构成员及初值表达式需要目标 IDE/控制器证据。

`plc_pou_graph_write`、批量 preview/apply 先做目标检查。组合根在候选工程管理器外再包一层执行前检查，对整批所有图形制品完成检查后才进入候选生命周期；重启后恢复的任务也重新读取并校验制品。通用原生工程交换仍按自身契约处理，不承诺把任意原生 XML 解释为本图形子集。

## SFC 多动作关联

```json
{
  "id": 4,
  "kind": "actionBlock",
  "inputs": [{ "name": "", "connections": [{ "sourceId": 3 }] }],
  "associations": [
    { "id": 40, "action": "DriveOutput", "qualifier": "N", "executionOrder": 1 },
    { "id": 41, "action": "CaptureParameter", "qualifier": "D", "duration": "T#100ms", "executionOrder": 2 }
  ]
}
```

关联引用同一 POU 的已定义 ST Action。每块 1～64 项，全程序最多 256 项；ID 是块内正整数且唯一，和外层图节点 ID 独立。每项自己的 action、qualifier、duration、executionOrder 及列表次序全部参与摘要。重复引用同一 Action 不去重。限定符沿用 N/S/R/P/L/D/SD/DS/SL；定时限定符必须有既有有界 TIME 常量，其他限定符不得携带 duration。

可选 executionOrder 保存 TC6 executionOrderId 元数据，在块内必须为唯一正整数。列表按 XML 次序编码为从上到下的 relPosition；导入时多个项的 Y 必须严格递增，避免把不一致的布局与列表顺序擅自合并。具体坐标不进入逻辑摘要。

**关联排列与 executionOrder 元数据不是控制器运行顺序保证。** CODESYS 的 IEC Action 处理包含特定阶段和排序规则。示例中的多个 Action 分别写不同状态，不能依赖“先写入列表就先执行”。目标版本的限定符、激活/去激活和周期行为需要实际验证。

旧 action/qualifier/duration 单动作请求继续工作。使用 associations 时不得同时设置旧 action、非默认 qualifier 或 duration；一项列表且 ID 等于外层 ID、没有执行顺序元数据时，可规范化为旧表示，摘要仍相同。indicator、inline Action、globalId、厂商扩展标志和未知内容保持显式拒绝。

## 完整工程与增量修改

`config/regression/p12-engineering-semantics.json` 将既有 P8 电机/顺控样例和 P11 参数样例扩展为同一套工程，26 项场景包含：

1. 创建受控工程；生成/解码 LD 电机、FBD 参数、SFC 多动作图形。
2. 10 步批次创建两个 DUT、全局信号、三个图形 POU、ST 主程序、任务配置、周期任务和调用绑定，最终编译后发布。
3. 读回确认地址、常量组、多动作内容与摘要；主程序连接运行状态，并对顺控状态上升沿累计次数。
4. 一次候选同时修改 FBD 地址/注释/保持属性，以及 SFC 动作排列/延时；读回与差异返回 variables、variableGroups、nodes 的前后内容。
5. 旧摘要拒绝；portable 制品可生成但非法目标声明拒绝写入；按准确修订生成 ZIP。

`eng/verify-semantics.py` 额外核对 ZIP 每个文件的长度/摘要，并验证后续编译失败时，已修改的候选不能发布，当前头和原图形摘要保持不变。

样例文件为 `ld-v2-declarations.json`、`fbd-v2-declarations.json`、`sfc-v2-multi-actions.json`。它们保留原样例作为兼容回归基线，增加本轮工程语义。地址只是声明示例，统计溢出、停机逻辑与控制器保持行为不在 fixture 中认证。模板需要目标 Application 且没有预先存在的 Task Configuration；SFC 导入产生子对象后若触发整体替换保护，记录实际阻断结果，不取消保护。

```sh
# Release 构建后，启动临时 fixture 完整检查：
python eng/verify-semantics.py
# 已启动 fixture 时：
python eng/run-regressions.py --suite p12 --expect-mode fixture --variables config/regression/fixture-variables.json --report ../reports/p12.json
```

OpenCode 可直接读取三个样例以及回归场景中的需求与步骤，调用标准 MCP 完成同样流程。Windows 验收时使用真实模板变量与 `--expect-mode real`，按 validation allowlist 执行。可选模型回归仍独立，不把确定性 HTTP 调用说成模型生成成功。

## 依据与验收边界

- [PLCopen 官方下载](https://www.plcopen.org/downloads/)中的 TC6 XML 2.01：varList/varListPlain、formattedText、actionBlock/action 的公开结构。开发时对生成 XML 使用官方 XSD 检查，未复制该文件到公有仓库。
- [CODESYS RETAIN](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_var_retain.html)、[VAR_TEMP](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_vartypes_var_temp.html)、[VAR_EXTERNAL](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_vartypes_var_external.html)、[变量修饰与常量](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_struct_reference_operands.html)。
- [CODESYS I/O 映射与 AT 声明限制](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_configuring_devices_mapping_ios.html)：AT 用于 local/global，不能据此给 POU 形参分配地址；本图形 POU 模型只开放 local。
- [SFC Action](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_element_action.html)、[SFC 处理顺序](https://content.helpme-codesys.com/en/CODESYS%20SFC/_cds_sfc_sequence_of_processing.html)。
- 原创 ScriptEngine 导入导出仍沿用 [供应商接口基线](10-official-api-baseline.md)，本轮没有新增猜测的供应商 API。

新增单元测试覆盖分组、注释、地址、目标规则、动作数量/顺序、完整场景、入队与执行前检查。旧 11 个 graph-v1/v2 样例与独立下载的 rc.2 二进制逐项对比摘要；8 个 v2 摘要加入固定回归，原 v1 固定回归继续保留。标准 Windows/macOS/Linux CI 执行完整构建、测试和 HTTP 检查。

真实 InoProShop、IronPython、模型生成、PLC 运行及 Windows Session 0 仍未验证。所有厂商能力保持 Unverified；CFC 结构化编辑、PDO 专用布局、在线操作与图形子对象安全替换仍见 [缺口清单](23-capability-closure.md)。
