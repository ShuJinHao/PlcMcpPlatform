# P10：功能收口与统一回归交付

> 本文记录 rc.1 的运行基础；rc.2 的新增初始化能力及回归入口见 [P11](24-graph-initializers.md)。

候选版本 `0.1.0-rc.1`。仍是一个标准 PLC MCP Server：OpenCode 负责模型、会话和规划；服务负责工程操作。74 个工具实现不变，无验收记录的正式宿主提供 22 个本地工具。当前权威入口是 [完整工具清单](22-current-tools.md)、[功能范围与缺口](23-capability-closure.md) 和 [P9 真实验收说明](20-production-readiness.md)。

本批新增停服清理、可选真实模型回归入口、SFC 定时动作、代码与契约一致的工具清单及版本化预发布。没有真实 Windows/InoProShop、模型提供商或 PLC 验收记录；不会因代码实现完成自动转为 Supported。

## 取得和启动候选包

从 [GitHub Releases](https://github.com/ShuJinHao/PlcMcpPlatform/releases) 取得对应版本的 production、fixture 或 validation ZIP，并核对同页 `SHA256SUMS`。Actions 附件保留 30 天；版本化 Release 附件不依赖这项保留期限。

- production：实际安装和验收范围不齐全时，只开放本地工具。
- fixture：Mac/Linux/Windows 可运行模拟工程回归，结果明确为 simulated。
- validation：后续 Windows + 目标 InoProShop 集中验证使用，需显式操作白名单；先完成 docs/20 的环境配置。

三种模式分别使用数据和工程目录。服务与维护命令同时锁定数据根和工程根，不能用不同数据目录绕过同一工程根的互斥。需要 .NET 8 ASP.NET Core Runtime；源码开发使用 .NET 8 SDK。辅助命令需要 Python 3，工程脚本仍由 IDE 内的 IronPython 执行。命令中的目录都由使用者选择；包本身保持只读。

Mac/Linux fixture 示例（已解压 fixture 包，命令从包目录执行）：

```bash
export PlcMcp__ApiKey="$(openssl rand -hex 32)"
python3 eng/init-fixture.py --workspace-root "$PWD/../fixture-workspace"
python3 eng/start.py --bundle "$PWD" --data-directory "$PWD/../fixture-data" --workspace-root "$PWD/../fixture-workspace"
```

另一个继承相同密钥的终端：

```bash
python3 eng/doctor.py --mode fixture
python3 eng/run-regressions.py --suite all --expect-mode fixture --variables config/regression/fixture-variables.json --report ../reports/fixture-all.json
```

标准 OpenCode 连接使用 `config/opencode.example.json`。production 使用相同启动命令和单独数据目录，不运行 init-fixture。Windows 按 docs/20 配置实际 EXE、引擎、Profile 与验证白名单；本批没有引入 Windows 服务注册或假设 Session 0 可运行 IDE。

## 停服清理

使用包内 `eng/retention.py`，或源码构建后的任一 Host DLL 的 `--maintenance` 入口。命令复用 .NET `InstanceOwnership` 独占锁，不启动 HTTP、执行器或供应商进程；不更改数据库任务状态，也不执行迁移。必须先正常停止 Host，并确保没有运行、取消待确认、排队或结果未知的任务。维护命令不能替代异常对账。

策略见 `config/retention.example.json`：默认上传 14 天、失败候选 30 天、历史交付 ZIP 90 天；`pinnedDeliveryIds` 可长期保留指定交付。时间按 UTC 最后修改时间保守计算；失败候选还要求持久化任务终止时间超过保留期。

```bash
python3 eng/retention.py preview --bundle . --data-directory ../fixture-data --workspace-root ../fixture-workspace --policy config/retention.example.json --report ../cleanup-preview.json
# 阅读预览中的逐项路径、大小与摘要后，执行完全相同的计划：
python3 eng/retention.py apply --bundle . --data-directory ../fixture-data --workspace-root ../fixture-workspace --plan ../cleanup-preview.json
```

预览文件应放在数据和工程目录之外，不能覆盖已有文件。预览 24 小时有效。执行前在独占锁下重读数据库、校验文件摘要和目录成员；状态、文件或引用变化导致整份计划在删除前拒绝。配置变化须重新预览。重复执行已完成计划只返回原回执；部分失败保留回执，必须重新预览剩余内容，不自动续删。

| 类别 | 可清理范围 | 永久保留或保护 |
|---|---|---|
| 上传 | 过期 `.part` 和会话回执 | 完成上传生成的 XML 制品不删除；留下过期标记，旧 uploadKey 返回 upload_expired，显式 abort 后可重新开始 |
| 候选 | 已确定 Failed/Cancelled、ExecutorStopped=true、路径精确匹配的 candidate 内容 | before 备份、所有已发布 versions、源工程、模板、imports；无记录孤儿候选保留 |
| 交付 | 超期、非当前头、未 pin 的 ZIP | 当前工程版本交付、pin 交付、交付描述和任务审计；过期读取返回 delivery_expired |
| 其他 | 不自动回收 | SQLite、调用与批次日志、真实验收证据、交换制品及清理回执 |

这是受限文件回收，不是所有磁盘内容的自动归档器。未知文件、孤儿文件和没有充分终止证据的候选不会为了释放空间而被删除。维护默认手动执行，可由操作者在既定停服窗口调用；不在后台与工程写入并发清理。若重新生成完全相同的交付 ZIP，成功写入后撤销过期标记；旧历史版本本身始终可用来重新打包。

回执保存在数据目录 `maintenance/<planSha256>.json`。主要错误：`maintenance_execution_unresolved`、`maintenance_storage_busy_or_changed`、`maintenance_plan_stale`、`maintenance_plan_expired`、`maintenance_link_rejected`、`maintenance_partial_requires_new_preview`。半途失败可能已清理部分条目，应以回执和重新预览为准，不宣称文件删除事务回滚。

## 真实模型回归入口

`eng/verify-opencode.py` 仍只检查 CLI 连接。新入口 `eng/model-regression.py` 使用官方 OpenCode **1.19.5** 的 `run --format json --model`，是验收辅助脚本，不进入服务依赖或生产用例。

没有模型或没有显式 `--execute` 时写出 `status=skipped`，退出码 3；失败退出码 2；仅该场景通过时退出码 0。CI 不自动使用模型密钥或产生模型费用。本批实际模型运行仍未执行。

```bash
# 可以现在运行：明确记录模型尚未执行，不要求启动服务。
python3 eng/model-regression.py --report ../reports/model-not-run.json

# 已准备模型与独立 fixture 环境时，使用自身模型配置：
python3 eng/model-regression.py --execute --model PROVIDER/MODEL --provider-env PROVIDER_API_KEY --variables config/regression/fixture-variables.json --expect-mode fixture --report ../reports/model-fixture.json
```

提供商的密钥只从 `--provider-env` 明确列出的 `*_API_KEY`/`*_AUTH_TOKEN` 环境变量传入。自定义端点可用 `--provider-config <本机JSON>`；该文件仅允许顶层 `provider`，敏感字段使用 `{env:PROVIDER_API_KEY}`，不提交仓库。脚本隔离 OpenCode 配置/数据目录，禁用非 PLC 工具；不复用个人会话或自动登录。

场景要求模型在新工作区生成 P10Main 与 20 ms 周期任务，提交一次有意失败的批次，读诊断后修正为 `Counter := Counter + 2;`，回读源码并取得准确修订的交付包。fixture 使用 FIXTURE_ERROR 哨兵，真实 validation 使用语法错误；两者分别记录。脚本读取实际 CLI 工具事件，再独立查询 MCP 中的作业结果，并校验最终修订、源码、任务绑定、ZIP 和编译报告。模型最终说“成功”不作为证据。

报告分开记录 connectionVerified、llmGenerationVerified、realIdeScenarioPassed 和 plcRuntimeValidated。模型+fixture 通过时 realIdeScenarioPassed 仍是 false。真实模式要求独立 ValidationHost、`--expect-mode real` 和实际模板变量；不连接 PLC，不自动批准生产能力。

CLI 超时不意味着工程任务停止。脚本只停止它启动的 CLI，不重放写入、不自动取消/解锁服务任务；后续使用工作区、任务记录确认结果。报告不包含模型密钥、供应商错误日志或整份源码，原始会话数据位于临时隔离目录，摘要仅用于关联这次运行。

## SFC 定时动作

graph-v2 的 actionBlock 新增可选 `duration`，支持 L、D、SD、DS、SL。示例：`{"kind":"actionBlock","action":"Run","qualifier":"D","duration":"T#250ms"}`，其余端口和 localId 按现有模型提供。

仅接受正整数、单单位的 T#/TIME# 常量，单位 ms/s/m/h/d，总量不超过 4294967295 ms；不支持变量时间、组合时间、负数、零、LTIME 或表达式。非定时动作禁止 duration。持续时间参与语义摘要、XML 往返、差异和候选发布前检查；graph-v1 的既有语义摘要保持兼容。

新增样例 `tests/fixtures/graphs/sfc-v2-timed.json`。图形子对象整体替换保护继续启用；动作 XML 能被表达不意味着目标 InoProShop 的所有 SFC 对象结构都能无损替换。依据与剩余范围见 docs/23。

## 发布和验收

版本由 `release/version.json` 决定。PR 与三系统 CI 执行构建、测试、HTTP 回归、维护和打包检查。合并 main 后，只有完整检查通过，发布作业才验证三包来源提交、逐文件摘要与共享执行代码，创建 prerelease；已有版本保持不变，不覆盖标签或附件。下一次发布须明确修改版本号和说明。

统一代码检查：`python eng/verify.py`；清单检查：`python eng/capability-inventory.py`；维护 HTTP 检查：`python eng/verify-maintenance.py`。这些不替代模型或真实厂商验收。

后续 Windows 集中验收顺序为：创建/接入 → 程序及任务 → 编译诊断 → 修改修复 → 保存重开 → 回读 → 交付包；先完成离线工程闭环，不要求 PLC 硬件。在线下载、启停及运行正确性另行安排。
