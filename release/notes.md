PLC MCP Platform 首个版本化候选发布。供后续集中回归，不代表真实 InoProShop 或 PLC 运行已经验收。

- 74 个标准 MCP 工具实现；生产宿主在未验收环境开放 22 个本地工具，其余按实际验收范围开放。
- SFC graph-v2 增加 L、D、SD、DS、SL 定时动作与有界 TIME 常量；CFC 结构化编辑和 EtherCAT PDO 专用映射仍未实现。
- 新增停服保留策略、清理预览、状态/文件复核、过期标记与维护回执。
- 新增可选 OpenCode 模型回归入口；没有模型配置时明确报告 skipped，不能据此声明模型闭环通过。
- production、validation、fixture 三种隔离包，共用执行代码，包含来源提交、逐文件清单和 SHA256 校验值。

先阅读包内 `docs/21-delivery-closure.md`。本包需要 .NET 8 ASP.NET Core Runtime；Python 仅用于辅助命令。真实工程验证需要 Windows 与目标 InoProShop，离线工程验证不要求连接 PLC。

没有随包分发真实验收记录。请保留供应商环境和工程样例的真实回归结果；fixture 成功不能晋升为厂商能力支持。
