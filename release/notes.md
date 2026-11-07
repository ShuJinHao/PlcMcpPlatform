PLC MCP Platform 0.1.0-rc.4 候选交付。承接 P12，补充 Mac OpenCode 接入、图形类型和 Windows 集中回归准备。CFC/PDO/图形子对象仍有阻塞，未达到汇川全覆盖完成口径。

- graph-v2 新增有界枚举、整数子范围及数组元素/函数返回类型表达；64 位边界用十进制字符串保留。旧 graph-v1/v2 摘要兼容。
- 新类型接入语义摘要、读回、批量修改、差异和准确修订 ZIP；新增 P13 集中场景，不增加空工具。
- Mac dev 入口提供 setup/start/stop/restart/status/opencode/model，独立模拟工作区和认证，保留现有 OpenCode 用户配置；停止只处理自己的进程。
- 本地 OpenCode 依据实际接口/事件/作业/交付验证；CI 仍固定 1.19.5。Mac 1.18.2 + DeepSeek Flash 已完成真实模型调用，实际场景结果见 docs/26。
- Windows 统一入口提供只读环境预检、对象身份发现、已有全套场景、逐场景诊断/修订/失败位置；操作值集中配置，模糊身份不猜测。
- production、validation、fixture 三包绑定同一源码提交，附 SHA256、逐文件清单和运行说明；不包含凭据、供应商原文或真实工程。

从 docs/26-mac-windows-handoff.md 开始。Mac 的工程执行仍是 fixture/simulated。目标 Windows/InoProShop、保存重开、编译和版本差异待真实回归，所有厂商能力保持 Unverified，PLC 在线功能不在本批。
