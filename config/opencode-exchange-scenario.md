# OpenCode 图形程序与交换场景

先按 docs/13 在 Mac 启动 TestHost，或按 docs/14 在 Windows 启动独立 ValidationHost。两者使用 config/opencode.example.json 的同一个 /mcp 配置；默认生产入口仍只有两个状态工具。实际 OpenCode 联调尚未完成，下面是可执行的验收任务说明。

可以向 OpenCode 提出：

> 使用 plc MCP，在新建测试工作区中创建一个 LD 启停自保持程序。Start 启动，Stop 优先停止，Running 为输出；这只是编程样例，不是安全回路设计。先查看运行模式、模板和能力，再读取 tests/fixtures/graphs/ld-start-stop.json 的模型形状，通过 plc_graph_generate 生成 PLCopen 制品，检查后用 plc_pou_graph_write 创建。每次写入使用最新 workspace 修订和唯一业务幂等键，轮询任务到终态。成功后读取图形并核对连接及语义哈希，再创建任务绑定返回的新 POU GUID，编译并解释诊断，最后保存交付版本。遇到失败保留报告，遇到 OutcomeUnknown 停止写入并查询证据。最终报告执行模式、工程版本、POU 标识、任务绑定及编译来源；fixture 的诊断不能表述成汇川编译通过。

修改已有图形的步骤：

1. 用 plc_workspace_get 获取当前 m2 修订和版本路径；用 plc_pou_graph_read 获取对象、模型和 semanticSha256。
2. 修改完整模型，保留名称、类型、语言；生成新制品。不可表示的厂商属性明确报错，不能擅自删除。
3. plc_pou_graph_write 带 objectId、expectedGraphSha256、expectedRevision。已有任务引用会阻止替换，先显式解绑，再修改、使用新 GUID 重新绑定。
4. 查询终态，用新路径读回、编译和保存。旧哈希/修订冲突应重新读取，不自动换键重试原修改。

工程交换用 plc_project_export 指定对象和格式，结果以 artifactId 读取。导入外部 XML 先 plc_exchange_stage/validate，再 plc_project_import，明确 reject/replace/skip；报告中的 not_proven 必须保留。CFC 使用 codesys-native 交换验证，不请求 graph-v1 生成 CFC。

自动 HTTP 场景见 config/regression/p4-exchange.json；它不依赖 LLM，也不替代实际 OpenCode 客户端验收。图形模型和脚本兼容范围见 docs/15-engineering-exchange.md。
