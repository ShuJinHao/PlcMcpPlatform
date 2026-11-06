# P11：图形 POU 的数组与结构体初始化

候选版本 `0.1.0-rc.2`。OpenCode 通过现有 74 个标准 MCP 工具生成和修改 PLC 工程；本批扩展图形变量的数据表达，没有增加服务端模型、会话或客户端适配系统。生产宿主仍按实际验收范围开放工程能力；当前真实 InoProShop、IronPython、模型生成与 PLC 运行均未验收。

## 可以生成什么

LD/FBD/SFC 的 graph-v2 变量现在可以携带数组、结构体、结构体数组，以及结构成员中的数组和结构。原来的 `initialValue` 字符串保持兼容；聚合内容使用新增的 `initializer`，二者不能同时提供。

```json
{
  "name": "Thresholds",
  "type": "INT",
  "dimensions": [{ "lower": 0, "upper": 3 }],
  "initializer": {
    "kind": "array",
    "items": [
      { "repeat": 2, "value": { "kind": "simple", "value": "10" } },
      { "repeat": 2, "value": { "kind": "simple", "value": "20" } }
    ]
  }
}
```

这表示按声明顺序提供四个数组元素。`repeat` 默认 1，可压缩连续相同条目；不会在服务中展开为大量 XML 节点。多维数组沿用 TC6 声明顺序，初始化列表保持原始次序，不自行把下标转换为另一种存储顺序。

```json
{
  "name": "Settings",
  "type": "DriveSettings",
  "initializer": {
    "kind": "struct",
    "members": [
      { "name": "Enabled", "value": { "kind": "simple", "value": "TRUE" } },
      { "name": "Speed", "value": { "kind": "simple", "value": "100" } }
    ]
  }
}
```

`DriveSettings` 必须是目标工程实际可解析的类型。可以在同一批次先通过 `dut.create` 创建，再通过 `pou.graph.write` 导入图形 POU，最后编译整个候选。结构体定义仍由 DUT 工具管理，不内嵌在 GraphVariable 中。

## 契约、边界与失败行为

| 内容 | 规则 |
|---|---|
| 根初值 | 数组变量使用 `kind=array`；命名派生类型可使用 `kind=struct`。简单根初值继续使用原 `initialValue` |
| 递归值 | `simple` 只有 value；`array` 只有 items；`struct` 只有 members；不适用的字段必须省略或为 null |
| 数组容量 | 允许非空的部分初始化；重复计数之和不能超过声明维度总容量。未指定部分交给目标类型的默认初始化，不在服务内补零 |
| 数组重复 | repeat 为 1～65536 的整数；不接受变量、表达式、负值或零 |
| 结构成员 | 名称必须是合法标识符，同一结构中大小写不敏感唯一；不使用带点路径代替嵌套结构 |
| 规模 | 最深 8 层；每个数组/结构最多 256 项；整个程序聚合值最多 4096 个实际节点；单变量展开后的标量叶最多 65536，全程序最多 262144 |
| 文本与 XML | 单个 simple 值最多 256 字符；既有图形 XML 64 KiB 和外部交换限制继续有效 |
| 类型核验 | 可检查已知基本类型与顶层数组形状；派生类型成员、嵌套数组容量、表达式和数值范围由真实 IDE 编译确认 |

拒绝超出当前模型的初始化属性和元素，包括被放在初始化内容中的 width、height、position 等。它们不能被图形坐标忽略逻辑悄悄删除。XML 中显式 `repetitionValue="1"` 与省略该默认属性等价；其他重复形式、成员和数组条目顺序保守保留，不推断重排或展开后的等价性。

新增错误码：`invalid_graph_initializer`、`graph_initializer_exceeds_array`、`graph_initializer_too_large`。旧 profile 提交新初值返回 `graph_profile_mismatch`；无法保留的 XML 仍返回 `graph_contains_unrepresented_data`。

## 工程操作

复用 `plc_graph_generate/decode`、`plc_pou_graph_read/write`、批量预览/执行、工程差异与交付工具。初始化内容进入图形语义摘要；修改旧初值后，旧图形摘要会失效。保存重开后的读回必须保留初始化内容，才能发布候选。

仅修改一个变量的初值时，`plc_project_diff` 的 `objectDifference.changes` 将返回该变量前后的完整 initializer。批次后续编译失败时，已修改初值的候选不能替代当前工程版本。图形 POU 子对象保护、未知结果隔离、精确修订交付和现有验收门禁继续有效。

没有新增 CFC 网络编辑、PDO 布局、在线下载或启停；这些仍按 [缺口清单](23-capability-closure.md) 登记。

## 回归与使用

启动与 OpenCode 配置沿用 [运行手册](21-delivery-closure.md)。新版本仍提供 production / fixture / validation 三种包；Mac 使用 fixture，后续 Windows + InoProShop 使用独立 validation。

新增三个原创样例：`ld-v2-initializers.json`、`fbd-v2-initializers.json`、`sfc-v2-initializers.json`。其中 FBD 包含二维部分初始化、重复条目、嵌套结构和结构体数组。22 项集中场景创建两个 DUT、三个图形 POU、主程序和周期任务，完成 9 步批量编译、初值修改、读回、差异、旧摘要拒绝及精确版本 ZIP。

```sh
# 源码完成 Release 构建后：
python eng/verify-initializers.py

# 已启动 fixture 或 validation 时；real 仅用于实际 Windows 验收：
python eng/run-regressions.py --suite p11 --expect-mode fixture --variables config/regression/fixture-variables.json --report ../reports/p11.json
```

`--suite all` 现在包含 P3～P8 与 P11。真实模板须具备目标 Application 且尚无 Task Configuration；图形子对象保护触发时记录实际失败，不绕过后继续宣称验收成功。HTTP 检查额外验证同一候选先修改初值、再遇到模拟编译失败时，旧工程头和原始图形仍保留。

## 依据与验证边界

- [PLCopen 官方下载](https://www.plcopen.org/downloads/)中的 TC6 XML 2.01 XSD：value 选择 simpleValue/arrayValue/structValue，数组条目有 repetitionValue，结构条目有 member。开发时使用官方 XSD 验证生成格式，未将该文件复制进仓库。
- [CODESYS 数组](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_datatype_array.html)：固定维度、多维、重复和部分初始化以及结构体数组。
- [CODESYS 结构体](https://content.helpme-codesys.com/en/CODESYS%20Development%20System/_cds_datatype_structure.html)：结构成员和嵌套初始化。
- 供应商 CHM 的对象交换调用仍沿用 [官方接口基线](10-official-api-baseline.md)。本批没有假定或新增未确认的 ScriptEngine 方法。

单元测试、HTTP fixture 回归和 XSD 校验不能证明旧 InoProShop 的导入、初始化运行行为或类型解析正确。所有厂商能力保持 Unverified，engineeringReady=false，留待统一回归。
