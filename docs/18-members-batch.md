# P7：汇川工程成员操作与批量修改

> 本文保留对应批次的实现记录。当前工具数量、启动和剩余范围以 [P10 交付说明](21-delivery-closure.md)、[工具清单](22-current-tools.md) 和 [能力缺口](23-capability-closure.md) 为准。

目标：补齐现有 PLC MCP Server 的工程对象读取与修改。OpenCode 或其他 MCP 客户端负责需求理解、模型调用、生成代码、会话历史与上下文压缩；服务负责读取实际工程、执行明确操作、检查版本、保存、调用 IDE 编译和返回结果。没有新增 OpenCode 业务适配器，也没有引入其他 PLC MCP。

沿用 .NET 8、现有 DDD 分层、工程候选副本和版本发布机制。本批工程工具契约为 1.6，Worker 主协议仍为 1；测试/验证宿主共 74 个工具，生产宿主继续只开放 2 个查询工具。所有汇川能力仍为 `Unverified`，`engineeringReady=false`。

## 工程数据查询

新增标准工具 `plc_project_objects_read`，内部用例为 `project.objects.read`。请求包括 workspaceName、expectedHeadRevision、revision、applicationId、idempotencyKey；经任务队列在所选历史版本的私有副本读取，结果通过 `plc_job_get` 查询。

| collection | 范围 | 返回内容 |
|---|---|---|
| objects | 指定 Application；parentId 可限定子树，objectId 可限定一个对象 | GUID、父对象、名称、类别、实际厂商 type GUID、源码窗口及完整文本摘要 |
| members | parentId 指向 POU/GVL/Property，读取其直接子对象 | 方法、属性、访问器或尚未分类的子对象；保持真实身份，不强行给未知对象定类 |
| tasks | 当前 Application | 实际任务设置及显式 POU 调用绑定，保留 resolved 状态 |
| libraries | 当前 Application 及共享工程/设备库管理器 | 实际库引用、管理器 GUID 与作用范围；不混入其他 Application 的私有引用 |

Method/Property 依据实际声明头和对象名判定。Get/Set 必须是实际 Property 的对应直接子对象，返回 ownerId、accessor 和 typeEvidence。缺失访问器是合法状态，读取返回 null；修改缺失访问器返回明确错误，不自动创建。

Action 没有通用的声明头。本实现先返回未分类对象及其实际源码/type GUID；调用方确认类型证据后，可使用 objectId 与 `sourceQuery: { "memberKind": "Action", "expectedTypeId": "实际类型 GUID" }` 读取。结果注明 `caller_pinned_action_type`；它不表示服务识别了所有 IDE 版本的 Action 类型。类型、父对象、源码标志不符合要求时拒绝。

分页 pageSize 为 1～8，源码窗口 maxTextChars 为 1～8192，偏移为 UTF-16 代码单元；完整摘要始终覆盖 LF 规范化后的完整 UTF-8 文本。后续页面传回 nextCursor，保留相同工程头、历史修订、查询范围和窗口参数，使用新的幂等键。游标是查询续读标识，不承担授权；每页仍检查工程头和历史文件完整性。结果上限 512 KiB；对象树最多 2000 项、库管理器最多 16 个，每个管理器最多 1024 项引用。无法完整枚举时明确失败，不声称数据齐全。

原 `plc_project_context` 保留兼容，文档统一称为工程数据查询。两种查询均不维护聊天历史、不选择模型上下文、不自动分析完整程序调用图。

## 成员精确修改

继续通过 `plc_project_batch_preview` / `plc_project_batch_apply` 提交 `text.patch`。原 POU/GVL/DUT 用法保持兼容。成员增加以下字段：

```json
{
  "id": "reset_patch",
  "operation": "text.patch",
  "change": {
    "objectId": "实际 Method GUID",
    "patch": {
      "memberKind": "Method",
      "section": "implementation",
      "expectedSha256": "读取结果中的完整源码 SHA-256",
      "oldText": "alarmState := FALSE;",
      "newText": "(* Clear alarm *)\nalarmState := FALSE;"
    }
  }
}
```

Property 访问器修改以 Property GUID 为 objectId，使用 `memberKind=Property`、`accessor=Get|Set`；摘要取对应访问器源码，不是属性声明。结果包含实际 targetObjectId。Action 使用 `memberKind=Action` 并固定 expectedTypeId，只允许其实际存在的文本实现部分。图形实现不能走文本补丁。

写前核对完整摘要及唯一原文；通过已有 member.update 保存并重开，核对目标访问器身份与修改后摘要；最终任务检查、编译通过才发布。失败保留原工程头，未知结果保持隔离。属性源码与访问器源码分别更新，不能互相替代。

## 批量操作与作用范围

保留最多 32 步、显式执行顺序、`@stepId` 引用、一个候选工程和一次最终发布。新增纳入批量的都是已有工程操作端口：

- member.create/update；folder.create；object.rename/move/delete。
- task.delete，连同已有任务创建、编辑及调用绑定。
- library.add/remove/placeholder.set。
- device.add/remove/parameter.write/io.map。

每次修改保存重开后重新读取对象树。后续步骤不能使用已删除的对象或旧子树范围；供应商新建的子对象也以重新读取的身份为准。

| 目标 | 作用范围 | 最终检查 |
|---|---|---|
| 程序、成员、文件夹、任务 | plan.applicationId 内部 | 当前 Application 的任务绑定和编译 |
| 设备及参数/普通 IO 映射 | plan.deviceScopeIds 显式列出的设备及其子设备 | 全部 Application 的任务绑定和编译 |
| 库管理器 | 当前 Application 内，或 plan.libraryManagerIds 显式列出的管理器 | 全部 Application 的任务绑定和编译 |

作用范围 GUID 必须真实存在并且类别匹配；最多各 32 项。共享修改后枚举全部 Application，要求 1～16 个且全部检查通过。批量结果返回 affectedApplicationIds、compileScope 和逐个检查结果；保留原 compile/tasks 字段用于兼容所选 Application 的结果读取。

批量步骤记录保留成员/访问器身份及源码摘要、库变更项、任务调用数量；完整源码和清单使用查询工具读取。最终批量结果上限 1 MiB，过大时明确失败且不发布。

静态预览只校验契约、引用顺序、作用范围格式和计划摘要，实际对象、已安装设备描述、库解析及 IDE 是否支持，仍在执行阶段检查。重命名/移动/删除不承诺源码引用自动重构；必要的引用修改由调用方作为明确步骤提交。已有任务引用冲突会阻止操作，调用方应先解除绑定，再修改并重建绑定。

## 验证与交付

统一入口 `python eng/verify.py` 增加 `eng/verify-members.py`。新增 `config/regression/p7-members-batch.json`，可通过集中入口运行：

```sh
python eng/run-regressions.py --suite p7 --variables <本地变量文件> --expect-mode fixture --report <本地报告文件>
```

真实环境使用 `--expect-mode real`，运行已配置的 ValidationHost，补齐版本、Profile、操作白名单及真实 GUID。新增用例需要一个没有既有任务配置且没有同名示例对象的干净模板；选择支持示例 BOOL 映射的设备通道与实际可用的库。变量示例提供字段说明，不代表任意工程可直接使用。`--suite all` 按 P3～P7 顺序执行，各套件独立工作区。

本批示例包含 FB_Motor、Start/Stop/Reset 方法、Running/Alarm 属性、Action、GVL/DUT、MAIN 实例调用和周期任务，并覆盖文件夹重命名/移动/删除、任务删除、库引用、设备参数/IO 映射及工程包。回归还覆盖访问器区分、源码冲突、游标条件变化、删除后陈旧身份、共享资源的多应用编译失败及旧头保护。

自动测试使用原创 fixture。它验证服务契约、执行流程和故障行为，不解释或编译真实 PLC 语言。真实 InoProShop/IronPython、OpenCode 模型生成及 PLC 运行留待统一验收；不因此升级能力状态。现有 graph-v1 能力保持原边界，复杂 SFC、CFC 结构化编辑和 PDO 未纳入本批新增支持声明。

## 实现位置

| 模块 | 变化 |
|---|---|
| IncrementalTools / IncrementalService | 标准工程查询入口与校验，兼容已有工具 |
| EngineeringQueryReader / EngineeringCursor | 修订一致的对象/任务/库读取及续读 |
| IncrementalEditExecutor | 成员与访问器精确修改，复用既有厂商端口 |
| BatchScope / BatchExecutor | 当前对象树范围、共享资源检查、一次发布前的完整检查 |
| 原创 ScriptEngine Worker | 分别读取声明和实现，保留无声明 Action 的实现，确认属性访问器身份 |

API 依据延续供应商 CHM 既有版本基线，交叉核对 CODESYS 官方 [文本对象](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptTextualObject.html)、[对象身份与父对象](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptObject.html) 和 [IEC 容器](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptIecLanguageObjectContainer.html)。没有新增或猜测语言、Action 类型 GUID；官网文档不能替代目标汇川 IDE 验证。
