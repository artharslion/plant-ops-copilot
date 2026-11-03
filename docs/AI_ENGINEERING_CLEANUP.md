# AI Engineering Cleanup review

基线：`origin/main` 的 `8aef275`。本次只实施兼容边界、测试职责、证据排序与界面状态整理。

## 修改及依据

- **兼容边界**：v3 `InvestigationEngine` 原先公开转发 v1/v2 的 `ValidateFacts`、安全摘要和历史模型打包方法。删除这些转发，历史调用直接指向 internal `LegacyInvestigationEngine`；历史测试组命名为 Legacy，混合网关测试的历史方法也加前缀，并更新执行脚本过滤器。没有修改历史验证器、存储读取和生产 DI 注册。
- **编排测试职责**：新增 `ScriptedLlmGateway`，按显式响应顺序执行，耗尽即失败，沿用原来的轮数、请求预留、用量对账与工具计数。原脚本调用迁移到该类；`FakeLlmGateway` 保留本地 Demo、固定工具和模拟建议。两者都不能证明真实推理能力。新增 v3 两轮读取/结束、末轮禁止读取，以及脚本耗尽、轮数上限和取消测试。
- **证据处理缺陷**：原始变化事实按中文“非下降观测段”识别优先级，甚至点名包含该短语也会误分类。改由 `RiseSegments` 生成独立优先列表；警告在产生处记录 Priority，再排序，不解析中文前缀。相同 Priority 的警告仍保留原有文字排序作为稳定展示次序，不赋予业务状态含义。新增改写中英文警告、误导前缀、包含旧关键词的点名、存在/不存在上升段及空趋势反例。持久化事实文本、哈希输入结构和 v3 schema 均不变。
- **界面状态缺陷**：Overview 改用 `checking / healthy / unavailable` 状态设置样式。保留 HTTP 状态加 `Healthy` 协议体校验、中英文文案和健康检查；新增两种语言的 HTTP 错误、错误响应体、网络失败和恢复测试。
- `InvestigationEnglish.cs` 与 `web/src/evidencePresentation.ts` 的模板匹配仅用于展示翻译，未发现用其推导业务状态；保持原实现和历史未知模板透传。历史字符串仍由展示层处理，不重写已保存证据。

## 代码确认的剩余问题与评估边界

1. `InvestigationService` 对同设备启用候选按 Code 排序并 `Take(2)`；`get_recent_measurements` 在引擎 schema 和 `InvestigationToolPolicy` 中仅接受 `{}`，Agent 不能指定点位或窗口。这是取证能力限制，不是诊断规则，本次不改契约。
2. `FakeLlmGateway` 仍有固定工具列表、状态到模拟建议的映射及历史中文模板匹配，均保留在明确的 Demo 路径。`InvestigationScenarioTests`、`InvestigationOnDemandTests` 等仍组合脚本工具决定和模拟最终答案，只验证结构、授权和管线。不得把这些通过数称为模型语义验收。
3. 旧 `InvestigationRealQualityTests` 类现明确标识 Legacy：它通过当前服务发起调查，却仍用旧 `InvestigationResult` / `ValidateFacts` 及 `Observations`、`MissingInformation` 等断言结果，另有固定 SingleRequest 假设。它不是有效的当前 v3 质量门槛，需 P8 迁移。现有开关及请求预算不变，本次未运行，也未通过修改生产规则迎合它。
4. 本次检查的活跃 v3 Engine、AdviceContract 和证据投影未发现根据模型自然语言关键词判定诊断、固定症状建议或单 Case 特判。引用集合、枚举、当前点位可信度/阈值矛盾、只读工具、窗口和质量计算属于有权威来源的确定性规则；不能当作语义硬编码删掉。Legacy 的关键词验证仅保留于历史实现。

## 后续 P6 设计建议（本次未实施）

先向 Agent 提供冻结、授权的候选点位目录：ID、Code、物理量、单位、来源、映射及已有关系，不把同设备候选视为物理关联。为工具引入可选的 `pointIds` 和窗口选择（事件/恢复/当前，或受限 UTC from/to），由模型选择相关点位和需要的时间范围。

服务端逐项校验冻结集合、实时 ACL、资产范围、取证截止、最大窗口跨度、点数/样本数及总工具预算；以规范化参数缓存、冻结结果与哈希，缺失和不可查询保留显式状态。先制定版本和旧 `{}` 调用兼容策略，再实现契约；禁止任意数据库查询与模型扩权。验收覆盖多物理量、任意 Code 排序、范围外 ID、越界窗口、无数据、重复读取及预算耗尽。

## 后续 P8 建议（本次未实施）

- 将旧真实模型 harness 迁到 v3，分开直接答案、Agent 自主工具读取、离线历史回放、Demo 与 proposal API 结果；不继续用 v1/v2 规则过滤 v3。
- 保留语义正反例、改写、矛盾场景，设立未被实现者读取的 reviewer holdout；分别记录误接受、误拒绝、事实引用、建议适切性和真实工具选择。可见 fixture 不是 holdout。
- 延续现有请求账本、逐轮预算和诊断证据；任何真实模型评估须另有当次授权，不自动增加额度。

## 兼容、成本与验证

v1/v2 历史解析、回放和展示路径保留；v3 输出契约、prompt/version、只读/ACL、人审与生产注册不变。删除的旧静态入口是有意的源码 API 清理，仓库内调用已迁移。新增分支仅用于明确状态/脚本耗尽与排序元数据；没有新增语义关键词、提示词、模型轮次、工具读取或外部请求成本。结构验证仍可能接受语义错误，也不能排除未覆盖的误拒绝。

验证结果见下方最终记录。未配置 `PLANTOPS_TEST_CONNECTION`，数据库用例跳过，没有连接业务数据库；如后续补跑，只能使用 `plantops_tests` 和 `TestDatabase.IsolatedConnection()` 的隔离 schema。没有真实模型调用、部署或模型预算调整。


最终验证（2026-11-03）：

| 检查 | 结果 |
| --- | --- |
| `dotnet build PlantOps.sln --no-restore` | 通过，0 warning / 0 error |
| `dotnet test tests/PlantOps.Tests/PlantOps.Tests.csproj --no-build` | 658 通过、0 失败、144 跳过，共 802；数据库、真实模型和缺少历史捕获等 opt-in 用例未执行 |
| `cd web; npm test` | 56 通过、0 失败 |
| `cd web; npm run build` | 通过；依赖 PURE 注释及打包体积提示仍存在 |
| `cd web; npm run lint` | 通过 |
| 两个改动前端文件的 Prettier 检查 | 通过 |
| `git diff --check` | 通过 |

本地原始证据保留于 `artifacts/test-results/ai-cleanup/ai-cleanup-final-verified.trx`、`artifacts/test-results/ai-cleanup-final-verified.log`、`artifacts/test-results/ai-cleanup-build.log`、`artifacts/test-results/ai-cleanup-web.log` 和 `artifacts/test-results/ai-cleanup-web-build.log`；前期失败及中间结果也保留。首次新增编排测试误填超过现有上限的测试预算，改回既有 32000 上限后通过；没有扩大产品配置。未执行数据库集成、真实模型、浏览器人工验收或部署，因此不能据此宣称这些路径已完成验收。


## PR #2 CI follow-up

CI job `125673087578` initially reported 22 failures: one historical-upgrade fixture failed with PostgreSQL `42703` (the old readings table has no `PointVersion` column), followed by connection exhaustion (`53300`). Both Docker smoke checks passed. The earlier local run had skipped database tests and could not detect these problems.

- `OpcUaUpgradeTests` now seeds readings with the historical column set and compares all original columns across the upgrade. It also asserts that newly introduced `PointVersion` / `SourceVersion` remain null for historical readings. Production migrations are unchanged; the failing test was reproduced before the fix and passed afterward.
- `TestDatabase.IsolatedConnection()` disables pooling for unique schema connections. Otherwise each distinct search path creates a process-wide pool whose idle connections survive host disposal. This is test-only connection lifetime management; production pooling and PostgreSQL connection limits are unchanged. A regression checks `pg_stat_activity` after isolated connection disposal.
- Verification uses a separate local PostgreSQL 17 container, database `plantops_tests`, an isolated root schema and generated per-test schemas, with the default 100-connection limit. Existing demo/business database containers are untouched. Real-model flags are disabled.


Follow-up verification: the historical-upgrade regression failed before the fix and passed after it. The complete solution test run with the isolated PostgreSQL database passed **824 tests, 0 failures, 8 explicit skips (832 total)** in 10m25s. `.NET format --verify-no-changes`, frontend format/lint/build, all 56 frontend tests, and the diff check passed. The test command rebuilt the solution successfully. Reports are retained under `artifacts/test-results/pr2-database/`, `pr2-database.log`, `pr2-upgrade-before.log`, `pr2-upgrade-after.log`, `pr2-format.log`, and `pr2-web-*.log`. These local results do not imply a real-model run or deployment.
