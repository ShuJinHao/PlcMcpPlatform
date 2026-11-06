PLC MCP Platform 0.1.0-rc.3 候选发布。新增工程声明语义与多动作 SFC，供后续集中回归；真实 InoProShop、模型生成与 PLC 运行仍未验收。

- graph-v2 新增有序声明分组：常量、RETAIN/NON_RETAIN、绝对地址、纯文本注释、temp/external 变量；保留旧 graph-v1/v2 请求与语义摘要。
- SFC actionBlock 支持多项关联，保留独立 ID、引用、限定符、延时、顺序元数据及列表次序。列表顺序不等于控制器执行顺序。
- portable 模型与汇川目标规则分层；图形写入和批量预览/执行检查目标约束，恢复任务在进入候选生命周期前重新校验制品。
- 新字段接入生成/解码、批量、读回证明、变量/分组/动作差异、编译失败保护及准确修订 ZIP；原图形子对象保护保留。
- 扩展 P8/P11 原创样例为完整电机、参数统计、多动作顺控工程，新增 26 项集中场景；统一回归包含 P3～P8、P11、P12。
- 74 个标准 MCP 工具实现；生产宿主在未验收环境开放 22 个本地工具，其余按实际验收范围开放。
- production、validation、fixture 三种隔离包，附来源提交、逐文件清单和 SHA256；不分发真实验收记录或供应商文件。

启动阅读 `docs/21-delivery-closure.md`，新增能力阅读 `docs/25-engineering-semantics.md`。需要 .NET 8 ASP.NET Core Runtime；Python 用于辅助命令。Mac 可运行 fixture；真实工程验证需要 Windows 与目标 InoProShop，离线 IDE 工程验证不要求连接 PLC。

CFC 结构化编辑、EtherCAT PDO 专用布局、在线 PLC 操作、图形子对象安全替换仍未完成。fixture、XSD 和 CLI 连接检查不能晋升为厂商能力支持，也不能替代真实模型或 PLC 验收。
