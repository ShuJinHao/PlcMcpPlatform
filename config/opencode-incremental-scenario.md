# OpenCode：修改已有汇川工程

连接仍使用 opencode.example.json。Mac 使用独立 TestHost；Windows 真实测试使用 ValidationHost 和本地版本/操作白名单。所有汇川能力仍未认证；不要把 fixture 工程发给真实 IDE，也不要向 PLC 下载。

建议任务提示：

> 接入我已保存的 existing 工程。在指定 Application 的 PLC_PRG 中增加联锁条件：只有 InterlockData.Enabled 为 TRUE 才允许执行原有逻辑。先读取相关程序、GVL/DUT、任务绑定和库引用，给出修改预览；修改、保存重开并编译后提供版本差异和 ZIP。失败时保留当前版本并依据诊断修复；不执行 PLC 在线操作。

操作顺序：

1. `plc_runtime_get_status`、`plc_capabilities_list` 核对模式和执行槽。首次运行前由工程师将保存文件放入 WorkspaceRoot/imports/existing，并提供声明文件清单、控制器与 Profile。
2. `plc_project_adopt_preview` 取得 sourceRevision；`plc_project_adopt` 使用同一 source、准确 sourceRevision 和稳定幂等键。等待 `plc_job_get` 终态，保存初始 m2 revision。
3. 从工程结构选择真实 Application/POU ID。`plc_project_context` 绑定 expectedHeadRevision 和 revision，指定 POU 优先读取，并遍历对象/源码的 nextOffset。检查任务/库 truncated 和绑定 resolved；不能把截断内容当成完整工程或把同名文本命中当成完整调用图。
4. 准备 batch 计划：创建必要 GVL/DUT，再用 text.patch 修改具体文本。每个 patch 带 section、完整源码摘要、唯一 oldText 和 replacement。保留原有变量、任务、库与设备约束；图形内容使用 graph-v1 工具。
5. `plc_project_batch_preview` 获取 planSha256，展示具体改动；`plc_project_batch_apply` 提交原计划、准确 revision、planSha256 与幂等键。通过 `plc_batch_get` 查看步骤，通过 `plc_job_get_result` 查看结果。
6. 如果失败，先检查错误的 jobId、activeStep、对象与编译诊断，再读取当前已发布头，重新生成/预览修复计划。`OutcomeUnknown` 立即停止新写操作，仅凭 `plc_job_reconcile` 的实际证据处理，不重复执行或强制解锁。
7. `plc_project_diff` 对比原始与当前 m2，按需读取文本窗口或 graph-v1 组件变化。图形 XML 替换导致 GUID 改变时显式提供 toObjectId。
8. `plc_delivery_create` 绑定准确当前 m2，完成任务/编译检查；`eng/transfer.py` 下载 ZIP 并校验完整摘要。报告编译、保存、任务绑定、模式和未验证内容。
9. 如果任务要求恢复，调用 `plc_project_history` 选取历史修订，再使用 `plc_project_restore`；恢复完成应得到全新 m2，重新读取上下文核对内容。旧版本和修改后的版本仍保留。

自动复现见 config/regression/p6-incremental.json（14 步）。其中源码摘要从 MCP 读取，不手填虚构值。模型生成与修复由 OpenCode 完成；MCP 没有模型 API Key、聊天入口或自动调用 LLM 的逻辑。
