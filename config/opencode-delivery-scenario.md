# OpenCode：生成程序、绑定任务、获取工程交付包

连接使用 opencode.example.json；Host 与 OpenCode 继承同一个 PlcMcp__ApiKey。先运行 `opencode mcp list` 检查 plc connected，再给出以下任务。替换模板、Application GUID 和工作区名称，不要使用生产工程做初次回归。

## 给 OpenCode 的任务

```text
使用 plc MCP 在登记模板 TEMPLATE_ID 上创建工作区 WORKSPACE_NAME，指定 Application 为 APPLICATION_GUID。
先查询运行模式和能力。若是 fixture，最终明确写模拟，不称为真实汇川工程或真实编译。若真实能力不在验证范围中，报告具体原因并停止。

生成 MachineState 枚举、MachineData 全局变量、FB_Counter、Main 程序及 20 ms 周期的 MainTask：
- MachineData.Enabled 为 TRUE 时，每个周期调用一次 FB_Counter，使 Count 递增。
- Main 显式声明 Counter 实例并调用它。
- MainTask 绑定 Main，返回并核对真实 POU GUID。

从工具读取模板修订和工程对象 GUID，不猜造身份。模板没有任务配置时创建任务配置；已有配置时使用读取到的 GUID。
把 DUT、GVL、FB、Program、Task 与调用绑定整理成一个有依赖顺序的 batch plan。
先调用 plc_project_batch_preview，展示修改清单与基准修订；依照本会话的写入授权提交完全相同的计划和摘要。
引用新对象时使用 @stepId，不把它插进 PLC 源码文本。计划改变或基准修订变化时重新预览。
轮询 job 和 batch 日志；只有 job 成功且 published=true 才接受新修订。failed 保留诊断，OutcomeUnknown 停止，不换幂等键重放。

成功后读取 Main 的声明与实现，核对任务调用顺序和最终编译诊断。
使用准确的 m2 修订调用 plc_delivery_create，等待成功，返回 ZIP 的 artifactId、完整 SHA-256、字节数、工程修订和执行来源。
大文件只传制品引用；需要下载时调用本地 eng/transfer.py 并校验完整摘要，不在聊天中输出大段 XML/base64。
不要连接、下载、启动或停止 PLC。编译成功不能写成程序已在设备上运行正确。
```

## 人工回归记录

| 项目 | 记录 |
|---|---|
| OpenCode / 模型 | 实际 CLI 版本、模型与提供商；不能以固定 HTTP 场景代替模型行为 |
| Host 来源 | sourceCommit、fixture 或 ValidationHost、实际发现工具数（fixture 为 74）、schedulerBlocked |
| IDE 证据 | 真实模式的 IDE/ScriptEngine/Profile、模板、设备与库版本；fixture 填“不适用” |
| 修改 | 原修订、计划摘要、jobId、逐步对象身份、最终 m2 修订 |
| 程序 | 实际声明/实现、任务绑定、编译错误/警告，模型是否依据真实诊断修复 |
| 交付 | ZIP 摘要、文件清单、manifest、任务报告、编译报告、完整下载校验 |
| 未完成项 | IDE 兼容、真实运行或其他未验证部分分别登记 |

出现编译错误时，批量不会发布部分结果。模型应读取失败诊断，修订计划并以仍有效的旧头重新预览；只有确认结果为确定失败后，才按新的修改内容使用新幂等键。结果未知始终先处理证据与隔离，不能自动开始下一轮。
