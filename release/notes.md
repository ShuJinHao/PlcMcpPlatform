PLC MCP Platform 0.1.0-rc.2 候选发布。新增图形变量的数组与结构体初始化，供后续集中回归；真实 InoProShop 和 PLC 运行仍未验收。

- graph-v2 新增有界聚合初值：部分数组、重复条目、结构成员、嵌套结构及结构体数组；保留原 initialValue 和 graph-v1。
- 初始化内容参与语义摘要、读回证明、变量差异及编译失败保护；未知初始化 XML 明确拒绝，原有图形子对象保护继续生效。
- 新增 LD/FBD/SFC 样例与 22 项 P11 集中场景，`--suite all` 已纳入。入口及限制见 `docs/24-graph-initializers.md`。

- 74 个标准 MCP 工具实现；生产宿主在未验收环境开放 22 个本地工具，其余按实际验收范围开放。
- SFC graph-v2 增加 L、D、SD、DS、SL 定时动作与有界 TIME 常量；CFC 结构化编辑和 EtherCAT PDO 专用映射仍未实现。
- 新增停服保留策略、清理预览、状态/文件复核、过期标记与维护回执。
- 新增可选 OpenCode 模型回归入口；没有模型配置时明确报告 skipped，不能据此声明模型闭环通过。
- production、validation、fixture 三种隔离包，共用执行代码，包含来源提交、逐文件清单和 SHA256 校验值。

启动阅读包内 `docs/21-delivery-closure.md`，新增能力阅读 `docs/24-graph-initializers.md`。本包需要 .NET 8 ASP.NET Core Runtime；Python 仅用于辅助命令。真实工程验证需要 Windows 与目标 InoProShop，离线工程验证不要求连接 PLC。

没有随包分发真实验收记录。请保留供应商环境和工程样例的真实回归结果；fixture 成功不能晋升为厂商能力支持。
