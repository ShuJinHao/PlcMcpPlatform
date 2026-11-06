# OpenCode 第二批编辑流程提示

先按 [启动说明](../docs/13-managed-editing.md) 创建 sample 模板、启动 TestHost，合并 MCP 配置并确认 `opencode mcp list` 连接成功。将下面的提示交给 OpenCode；工作区名称如已使用，请换一个新的名称。

> 使用 plc MCP 验证程序编辑流程。先查询运行状态，确认 executionMode=fixture，报告这是模拟工程。列出模板，从 sample 创建名为 counter-demo 的工作区，使用模板返回的 revision。每次提交等待任务终态并取结果，不把任务接受当作操作成功。
>
> 查询工作区与对象树，取得实际 Application GUID，不猜测对象 ID。创建 ST Program Counter，声明为 `PROGRAM Counter` 加一个 INT 变量 count，实现在每次调用时使 count 加一，并加注释 `(* FIXTURE_ERROR *)`。这是专门用于验证错误路径的标记。
>
> 用当前版本路径提交模拟编译，读取错误诊断，再按 GUID 读回程序，删除该标记并更新程序。重新读取和模拟编译，解释标记驱动的模拟结果，不能声称 PLC 语义已编译通过。
>
> 创建一个含 enable 布尔变量的 GVL Globals，和一个含 value 整数字段的结构体 DUT CounterData，分别调用 GVL/DUT 读取工具核对声明。每次修改前取得工作区最新 m2 修订，读取/编译使用最新 projectPath；不要把读取返回的主文件 SHA-256 用作写修订。
>
> 最后执行 plc_project_save，报告交付版本路径、修订、备份路径和完成步骤。说明本流程未验证任务调用绑定、真实 InoProShop 编译或 PLC 运行。每项新操作生成独立幂等键；重试同一请求保留原键。遇到 OutcomeUnknown 时停止后续写入，读取任务证据；只有 plc_job_reconcile 确认恢复后才能继续，不反复提交新键绕过阻塞。

此提示是实际客户端联调场景，不是已执行的 OpenCode 会话记录。自动验收对应 `eng/verify-editing.py`。
