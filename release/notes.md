PLC MCP Platform 0.1.0-rc.5 本地候选交付。从 0ffa17b 延续开发，Windows 操作和样例采集全部留到最后。

- graph-v2 新增有界内嵌结构体、指针、独立持久性声明和 XHTML 注释；已有请求与固定摘要兼容。
- 新字段进入生成、解码、语义摘要、差异、候选批量、读回及准确修订 ZIP；未知数据和图形子对象保护保持严格。
- 图形诊断提供可核查字段位置、规则和修改方向；模型报告增加无效调用数和耗时。
- 新增串行 model-suite：原五组、新声明场景、三个独立图形工作区；保留失败报告、服务作业与最终交付。
- 同一源码 production、validation、fixture 包附摘要、源码快照、能力清单和回归证据，保留旧候选包。

具体实现与实际检查结果见 docs/27-standard-declarations.md。工程执行仍为 fixture/simulated，CFC/PDO/图形子对象仍缺目标证据；不能称汇川全覆盖完成。所有厂商能力保持 Unverified，真实持久列表关联、InoProShop 保存重开/编译及版本兼容仍待 Windows。PLC 在线与断电保持不在本批。
