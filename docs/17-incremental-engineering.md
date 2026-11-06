# 第六批：已有工程接入、增量编程与版本恢复

> 本文保留对应批次的实现记录。当前工具数量、启动和剩余范围以 [P10 交付说明](21-delivery-closure.md)、[工具清单](22-current-tools.md) 和 [能力缺口](23-capability-closure.md) 为准。

目标是让 OpenCode 在已有汇川工程上读取上下文、提出可审阅修改、得到编译诊断，再交付或恢复工程内容。MCP 不调用模型，也不引入第三方 PLC MCP。服务继续使用 .NET 8；P6 新增 6 个工具，TestHost / ValidationHost 合计 73 个，默认生产宿主仍为 2 个。

新增工具契约版本 1.5，Worker 主版本仍为 1。所有汇川能力保持 `Unverified`，`engineeringReady=false`。自动测试中的 JSON 工程、编译诊断和任务组态均是 fixture，不代表真实 InoProShop、IronPython 或 PLC 运行通过。

## 1. 接入已经保存的工程

把准备接入的已保存文件放在配置的 `WorkspaceRoot/imports/<sourceName>/` 下。sourceName 使用与工作区相同的短名称规则；工具不接受任意服务器目录。`plc_project_adopt_preview` 接收：

```json
{
  "source": {
    "sourceName": "existing",
    "projectFile": "plant.project",
    "files": ["plant.project", "dependencies/required-file.bin"],
    "ideProfile": "填写实际 Profile",
    "targetController": "填写实际控制器名称",
    "requiredLibraries": [],
    "controller": { "type": 0, "id": "填写实际设备标识", "version": "填写实际版本" }
  }
}
```

示例文件名和设备标识必须按实际环境填写。伴随文件由工程师明确声明，不根据扩展名猜测，不自动扫描系统库仓库。预览返回 a1 修订、文件清单和控制器/依赖要求；`plc_project_adopt` 提交同一 source、`expectedSourceRevision`、新 workspaceName 与幂等键。

执行时重新计算 source 的摘要；复制前后核对已声明文件，验证备份和候选副本，随后由已有 ScriptEngine 生命周期完成控制器/库引用检查、保存、重开和读回，最后发布初始 m2 修订。原文件和未声明文件均不写入。不保证另一个 IDE 会话尚未保存的 GUI 内容被包含，也不把依赖引用匹配解释成目标机器已安装全部设备包/库。接入本身不自动编译；批量修改、恢复和交付另有编译门槛。

沿用文件边界：最多 512 个声明文件，托管版本最多 2000 个文件，总计 256 MiB，路径遍历、重解析点/符号链接、重复大小写路径被拒绝。源文件丢失或修改后，同一个已接收请求的幂等重试仍返回原任务；换参数复用键被拒绝。

## 2. 带版本的上下文

`plc_project_context` 接收 workspaceName、expectedHeadRevision、revision、applicationId，可指定 objectId 优先读取。即使查询历史版本，也要绑定准确当前头，防止多页阅读期间有人推进版本而调用方未察觉。

读取经过串行任务队列，在私有副本上调用现有官方工程 API；不打开已发布版本写缓存。工程树完整性最多核对 2000 个对象；超出返回 `context_scope_incomplete`，不假设未返回的对象不存在。

返回内容：

| 内容 | 语义与范围 |
|---|---|
| Objects | 真实 ID、父 ID、名称、类型、声明/实现窗口；指定对象优先，随后为该 Application 其余对象，包括 GVL/DUT，需逐页读取 |
| 声明与实现 | 完整 LF 规范化源码的 UTF-8 SHA-256、完整长度、当前窗口、下一窗口位置；空值表示不存在该文本部分 |
| Tasks | 当前 Application 的任务和明确的 POU 调用绑定，保留 resolved/truncated；不是从源码猜测的调用关系 |
| Libraries | 实际库管理器的引用，区分 selected_application 与 project_or_other_application，不能把其他 Application 的引用视为当前直接依赖 |

offset/pageSize 按对象分页，pageSize 为 1～8；textOffset/maxTextChars 按 UTF-16 代码单元读取规范化源码窗口，maxTextChars 为 1～8192。源码哈希总是覆盖完整文本部分，不是当前窗口。UTF-16 偏移不可落在代理对中间，过小窗口不能切开补充字符。任务最多返回 128 条，每个库管理器最多 128 条引用，最多 16 个管理器；truncated 必须保留并由调用方检查。任务/库的进一步读取使用已有领域查询工具。最终上下文 JSON 最大 512 KiB，超限明确失败。

`object.source.read` 是原创 Worker 端口，通过 `has_textual_declaration`、`has_textual_implementation` 和 `get_text` 读取实际支持的文本部分。对象类别在真实身份之上结合声明头判定；无法确认时保留 object。不会凭语言枚举猜出 ST/IL 的实际语言 GUID，不会把图形网络转换为伪造 ST。完整调用图、外部库源码和隐藏厂商属性不属于本次上下文声明。

## 3. 局部修改沿用批量计划

P6 在 batch-v1 中增加 `text.patch` 步骤；继续使用 `plc_project_batch_preview` / `plc_project_batch_apply`。不增加任意 Python/命令执行入口，也不允许绕过批量最终编译直接发布补丁。

```json
{
  "id": "add_interlock",
  "operation": "text.patch",
  "change": {
    "objectId": "填写上下文返回的真实对象 GUID",
    "patch": {
      "section": "implementation",
      "expectedSha256": "填写完整 implementation 的源码摘要",
      "oldText": "Motor := Start;",
      "newText": "Motor := Start AND InterlockReady;"
    }
  }
}
```

支持已确认 POU/GVL/DUT 的 declaration，以及 POU 的实际文本 implementation（ST/IL 文本路径）。GVL/DUT 不存在实现部分；图形 POU 的实现须继续使用 graph-v1。Method/Property/Action 继续使用已有成员工具，本次 text.patch 不扩展到成员或属性访问器。

计划摘要绑定工作区、当前 m2 修订和完整步骤。执行时再读取对象，要求完整源码摘要一致、oldText 恰好出现一次；重叠重复也视为歧义。CRLF/CR 统一为 LF 后匹配，SHA 为小写十六进制；不做模糊匹配，不用行号猜测，不对源码做 @stepId 文本插值。单个修改结果最多 65536 个 UTF-16 单元。

正确修改依次经过当前候选主文件摘要检查、写入读回、保存重开、全批任务绑定检查与编译，再发布一次。任何步骤失败都保留旧头。编译失败会保留 jobId、batch activeStep、逐步结果及原始编译诊断中的对象/位置；OpenCode 根据证据重新读取已发布版本，重新预览修复计划，不自动续跑失败候选。

## 4. 历史、差异和恢复

`plc_project_history` 只列已发布版本，以任务创建时间和 ID 升序稳定分页（单页 1～64）。需提供 expectedHeadRevision；失败候选通过任务证据查询。列表本身不声明每个历史文件都仍完好，实际 diff/restore 会重新校验。

P6 把已发布 ProjectSnapshot 元数据与头和证据放在同一 SQLite 事务中保存。已有 P2～P5 的记录仍可读取：从历史目录重新枚举清单，只有与原始 m2 中已持久化的完整清单摘要一致才接受；依赖快照取该历史响应，模板约束沿用工作区原模板。没有新增 PLC 对象数据库，也不将历史查询伪造成当前工程状态。

`plc_project_diff` 比较 fromRevision/toRevision：

- 文件层返回增加、删除、长度和 SHA 变化；厂商二进制只报告字节差异，不报告语义相同。
- 可指定 applicationId/objectId，对声明和实现分别报告前后窗口、完整摘要和 changed；对象一侧不存在则明确返回空。
- XML 替换可能改变厂商 GUID，可用 toObjectId 明确指出目标版本对象，不根据同名对象自动配对。
- graph=true 只支持现有 graph-v1。比较变量、节点、节点输入/输出和连接、动作、顺序以及图形属性；返回语义摘要和分页组件差异。geometry 变化也可见，但不影响 graph-v1 的语义摘要。超出 profile、无法完整解码的对象直接报错。

`plc_project_restore` 输入准确当前头与 sourceRevision。备份当前头，另从目标历史版本生成候选；核对历史清单、历史依赖、保存重开，然后枚举全部 Application（最多 16 个），逐一检查完整任务绑定并编译。未解析调用会阻止批量、恢复和交付，不能只根据不含错误的空编译报告放行。

恢复成功是一个新的 m2 修订，baseRevision 指向恢复前当前头；不移动历史目录，不回拨数据库指针，不删除后续版本。IDE 保存可能改变二进制表达，因此不承诺恢复产物与历史文件逐字节相同。恢复后的内容仍应通过上下文/差异核对。历史被篡改、当前头冲突、缺少 Application、依赖不匹配、编译失败均不发布。未知执行保持隔离，现有 `plc_job_reconcile` 仅接受可验证的终止与发布证据。

## 5. 模块与组合

| 模块 | 本次职责 |
|---|---|
| Core.Workspaces / TextPatchRules | 源码规范化、摘要及唯一精确替换 |
| Services.Contracts / Incremental | 接入声明、上下文窗口、版本记录、差异结果和历史端口 |
| IncrementalService | 预览、历史分页、幂等提交、版本及权限范围校验 |
| IncrementalReadExecutor | 受控历史读取、Application 上下文、文本/图形比较 |
| IncrementalEditExecutor | 精确补丁与历史恢复的全部 Application 验证，复用厂商端口 |
| ManagedProjectExecutor / Files | a1 接入、备份当前头、候选/历史版本核对、一次发布 |
| SqliteProjectChangeStore | 同事务保存头与完整版本元数据；历史查询只读既有发布记录 |
| Inovance / engineering_worker.py | 直接读取官方文本标记和源文档；未增加外部 MCP 依赖 |

组合顺序为 IncrementalReadExecutor → DeliveryExecutor → ManagedProjectExecutor → BatchExecutor → IncrementalEditExecutor → ExchangeExecutor → 原始厂商执行器。每层只拦截自己的用例；只有 ManagedProjectExecutor 发布工程版本，所有 IDE 调用使用同一个串行执行槽。

## 6. 回归与后续统一验收

`python eng/verify.py` 包含完整构建、.NET/Python、七组 HTTP 检查和发布验证。独立 `python eng/verify-incremental.py` 准备带既有任务绑定的 fixture 工程，执行 14 步 P6 场景，核对源码保护、摘要、ZIP、编译失败、预览后源变更和未知执行隔离。

已有运行中宿主可使用 `eng/run-regressions.py --suite p6 --variables <本地变量文件> --expect-mode fixture|real --report <本地报告>`。P6 需要先放好 imports/sourceName 文件并填写 adoptionSource；`--suite all` 现在顺序包含 P3/P4/P5/P6。真实样例要求指定 POU 实现可在 8192 单元窗口内完整读取；更大的程序应使用分页上下文和针对性局部补丁。真实变量、工程和报告不提交公共仓库。

代码与契约结果见 [PR #7 checks](https://github.com/ShuJinHao/PlcMcpPlatform/pull/7/checks)。官方 API 依据见 [docs/10](10-official-api-baseline.md)，OpenCode 操作顺序见 [增量场景](../config/opencode-incremental-scenario.md)。本阶段不要求用户现在提供 Windows/实机；实际模型生成、具体 IDE/内核/Profile、安装依赖及 PLC 运行待后续统一验收。复杂 SFC、图形 POU 子成员、CFC 结构化编辑、PDO 仍保留原能力边界。

## P7 后续扩展

本文记录 P6 的交付范围。后续 [P7](18-members-batch.md) 扩展了成员/访问器精确修改，并增加名称明确的 `plc_project_objects_read`。本文“上下文”均指工程数据读取，不指 OpenCode 会话或模型上下文管理。
