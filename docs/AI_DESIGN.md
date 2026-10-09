# AI Copilot 设计 v0.1

2026-10-09 实现进度：[P3.1 接入基础与密钥说明](AI_PROVIDER_SETUP.md) 已落地独立适配、Fake、预算与版本化输出。用户指定 `gpt-6.1-sol` 中转，四项能力分别真实成功，完整套件稳定性仍未通过。70 项适配/结构/健康检查通过。本文 AiRun、actor 授权检索、证据账本、事实语义校验、告警 UI 与 P4 仍是待实现设计；不能把 Provider spike 计为完整 P3 验收。

2026-10-09 当前增量：按 [AI 优先验收计划](plans/2026-10-09-ai-demo-acceptance.md)，直接在 P1 Simulation 告警上推进 P3，再做 P4；不等待完整 OPC UA 或长期 G0。首轮限定已发布授权 TXT/Markdown，使用版本化片段与确定性术语检索。下文 pgvector/embedding、完整 PDF 解析及混合检索为后续增强，不作为首轮前置；未解析资料明确提示。只读工具、actor 授权、引用校验、预算、20 案例评估和模型失败降级仍必验。

日期：2026-10-03。Phase 3 为 Investigation Copilot，针对告警联合查询测量历史、告警、维护记录和文档；Phase 4 为有限业务动作提案。**AI is not the source of truth**：数据库与业务服务决定业务事实和动作，OPC UA 数据源提供质量/时间可追溯的现场观测，发布文档提供知识证据；AI 仅检索、解释和提出建议。

## 1. 边界与执行流

`用户选择告警 → API 授权并创建 AiRun → Worker 领取作业 → 重新校验 actor → 授权工具/RAG → Provider → schema/引用/语义校验 → 保存结果 → UI`。

Worker 的数据库连接具备系统服务权限，但 AI 工具只调用带 actor 的 Application read 服务；模型不能指定 UserId、角色或允许的 PlantId 集合。当前告警/资产范围由服务端固定，模型参数只能在范围内收窄。

- Phase 3：只读，有最大轮数和预算；工具失败会显式反馈，禁止把缺数据编成事实。
- Phase 4：只读调查后生成 ActionProposal，UI 确认后由 CommandService 执行。批准不是模型工具调用。
- 不提供任意 SQL、任意 HTTP/URL fetch、文件路径读写、Shell、OPC UA Write/Call、规则修改或删除工具。
- UI 可以复制调查文本，但不以复制/浏览调查等操作隐式批准动作。

## 2. RAG 索引与检索

### 索引管道

1. 文档由正常上传服务校验并存储，解析器在受限 Worker 中运行；超时 30 秒、文本上限和页面上限配置化，不执行嵌入脚本或宏。
2. PDF / Markdown / TXT 解析出正文与页码/标题路径；扫描 PDF 无可提取文本时 NeedsText，OCR 留待后续，不虚构页码。
3. 初始按段落/标题切 chunk，目标 600 tokens、重叠 100，保留完整标题；这是可调初值，通过检索样本验证。表格尽量保留行与列标题，不跨设备上下文拼接。
4. Chunk 记录 documentId、versionId、ordinal、page?/section?、text hash、parserVersion；embedding 记录 Provider/modelId、维度、生成时间。
5. 同版本/内容 hash/embeddingModelId 的索引任务幂等；新版本新建 chunks，成功后事务切换索引 Ready。失败不发布半套索引。

### 授权与检索

从当前 actor 的工厂和文档 ACL 构造服务器侧过滤，限制 Published、当前版本、Ready、未撤回/归档。第一版使用 pgvector 对授权集合做 exact search；数据量小时先保证可验证的范围与召回，不急于 HNSW。后续近似索引需评估过滤后的召回，不能只在模型回答后做权限过滤。参考 [pgvector 官方说明](https://github.com/pgvector/pgvector)。

优先精确设备/model/点位术语，再向量检索；初始候选 20，最终上下文最多 6 个 chunk。中文资料不假设 PostgreSQL 默认英文分词有效；先使用元数据、精确术语与向量，专用分词/混合检索在评估后再加。

检索服务返回 `evidenceId, documentId, versionId, chunkId, page?/section?, text, score`。模型仅引用 evidenceId，URL 由服务端生成；展示引用/下载时再次检查权限。术语命中与向量距离不等于事实可信度。

撤回/删除/ACL 变更后查询立即失效，向量清理可后台做。缓存键包含 actor/权限版本、文档版本与查询范围，不跨用户复用未过滤片段；权限变化撤销缓存。AiRun 阅读结果也重校验证据权限，必要时隐藏旧结果并提供重新调查。

## 3. Tool calling 契约

工具执行遵循：模型提出调用 → JSON schema 校验 → 范围/能力校验 → 业务读取 → 输出大小限制 → 证据登记 → 回送。工具结果属于不可信数据，不能升级为系统指令。

| 工具 | 参数（模型可见） | 服务端限制与输出 |
|---|---|---|
| get_asset_details | assetId | 必须等于本次调查设备；返回类型、型号、位置和静态状态 |
| get_recent_measurements | pointId、from、to、bucket | 本设备点位、最长 24 小时、最多 1,440 桶；值/单位/质量/缺口 |
| get_recent_alarms | assetId、from、to | 本设备、最长 30 天、最多 100 个；包含确认/恢复语义 |
| get_maintenance_history | assetId、limit | 最多 20 条工单摘要；详情可受控补读 |
| search_documents | query、limit | 本设备及本工厂授权资料、limit ≤ 6；返回证据和片段 |

初始调查默认拉取最近 1 小时趋势和当前告警，避免模型为了发现上下文反复调用。所有 from/to 用 UTC；提供执行时刻以防模型混淆“当前”和历史。

工具 schema `additionalProperties=false`，枚举/长度/范围显式限制；非法参数返回结构错误，不把原始异常、SQL 或连接字符串回送模型。记录每次调用结果 Evidence ledger：来源、时间范围、quality、取数时刻、内容 hash 与授权范围。

预算默认：一个调查总时限 60 秒；最多 4 个模型轮次、8 次工具调用、16,000 input tokens、2,000 output tokens；单工具 5 秒，Provider 单次 30 秒，最多 1 次可重试错误重试并受总时限约束。精确美元预算按选定 Provider 单价配置；不能把未配置单价当零成本。

## 4. Structured output

定义版本化 `InvestigationResult.v1`，采用 JSON Schema；支持时用 Provider 原生 schema / strict tool 协议，应用仍需自行校验。拒绝、截断、超时、schema 不支持都作为明确状态处理。参考 [Structured Outputs 官方说明](https://openai.com/index/introducing-structured-outputs-in-the-api/)。结构合规不等于业务事实正确。

示例（只说明形状，E1/D1 必须由实际 Evidence ledger 生成）：

```json
{
  "schemaVersion": "1",
  "summary": "观测到持续高振动，原因仍需验证。",
  "observations": [
    { "text": "告警触发值为 12.6 mm/s。", "evidenceIds": ["E1"] }
  ],
  "hypotheses": [
    {
      "text": "可能与机械松动有关。",
      "support": "weak",
      "evidenceIds": ["D1"],
      "verification": "由维护人员按资料规定检查连接部位。"
    }
  ],
  "nextChecks": [
    { "text": "核实测量质量及趋势是否连续。", "evidenceIds": ["E1"] }
  ],
  "missingInformation": ["缺少现场检查结果。"],
  "dataWarnings": [],
  "evidenceIds": ["E1", "D1"]
}
```

每个字段必填，数组允许空；限制字符串长度、条目数和总输出大小。support = weak / moderate / strong，仅是证据强弱标签，不展示未经校准的“故障概率”。

后校验：所有 ID 必须属于本 AiRun 的授权证据；事实至少一个证据；数值、单位、时刻与引用数据匹配。引用不存在/越权直接拒绝结果，不把伪造 ID 生成为链接。仅允许一次受预算限制的格式修复；仍失败则 AiRun Failed，返回已有证据列表，不显示不合规答案。非数值事实也需检查来源关联，评估中验证语义，不能声称 schema 能保证真实性。

dataWarnings 由服务端追加质量、陈旧、gap、文档版本/缺失信息，模型无权删掉。UI 将“观测”“假设”“建议检查”“缺失信息”分别展示；具体设备停机/安全操作只能指向已授权规程和现场人员判断。

## 5. Authorization 与数据发送

- API 创建调查时与 Worker 执行时都校验 User enabled、能力和工厂范围；每次工具调用与返回调查结果也重新授权。
- 不使用管理员身份代替请求人调用工具；后台 actor context 不允许模型修改。
- 数据发送给 Provider 仅限本次授权必要 DTO/片段，脱敏不必要的人名、联系方式和编号；不发送整个数据库、原始凭据、私钥或无限文档。
- Provider 的凭据在服务器配置/密钥存储，浏览器看不到；环境决定是否允许外部 Provider，用户 UI 显示当前 Provider 与发送范围。
- 文档权限更改不能只更新向量 metadata；检索、缓存、证据展开和旧 AiRun 返回都必须实时依据源记录授权。
- Phase 3 不注册写工具；工具 allowlist 在部署配置和代码层双重固定，Prompt 不能启用被禁用工具。

## 6. Human-in-the-loop 与动作执行

仅两类动作：`CreateWorkOrder(assetId, alarmId?, title, description, priority)`；`AppendWorkOrderNote(workOrderId, text)`。动作目标必须属于当前调查设备。模型只填业务草稿，责任人由人工选择并校验，不能默认分配给任意用户。

提案保存 schemaVersion、AiRunId、actionType、目标 ID、payload/hash、所有依赖资源的版本快照、证据引用、发起人、expiresAt（初始 15 分钟）。创建工单校验 Asset 和关联 Alarm；追加备注校验 WorkOrder 与 Asset。只供发起用户确认；不能把一个用户的提案链接转成他人的写权限。

审批页面展示完整将写入内容、目标、优先级和关联告警；提供批准/拒绝。内容编辑后生成新的 payload hash / proposal version，重新展示并确认；旧批准不能应用到新内容。

批准端点流程：

1. 校验会话、防伪、审批角色、工厂范围和发起人一致；查提案是否已 Executed，已执行返回原结果。
2. 锁定提案记录，检查 Pending、未过期、确认的 payload hash/version 与当前一致。
3. 重读目标，检查资源 version、归属和允许状态；工单已关闭、告警/资产条件变化或用户撤权则失败/409，要求重新调查或提案。
4. 同一数据库事务执行 CommandService，写工单/备注、AuditLog、Outbox，将提案改 Executed 并保存 ResultEntityId；不在事务内调用模型。
5. 幂等标识使用 ProposalId + 客户端 Idempotency-Key；同 key 不同内容拒绝，ProposalId 唯一约束保证换 key 重试也不会二次写入。
6. 提交结果未知时查询 ProposalId 的最终状态；没有查清前不创建第二个动作。Rejected / Expired 不可执行，确知无副作用的失败可记录 Failed。

这是业务数据库内的有限动作，不需要外部设备补偿事务。未来增加外部动作必须新写 ADR 和恢复契约。第一版审批可由发起的维护工程师完成，不要求双人批准，但必须是一次明确人工操作。

## 7. Prompt injection 防护

Prompt 层分离系统规则、用户问题、工具结果和检索资料，引用文本明确标为 data。文档、设备名、工单备注甚至工具返回可能包含攻击内容，不能因为来源于数据库就当可信指令。

| 威胁 | 强制控制 |
|---|---|
| 文档写“忽略规则/读取其他厂数据” | 服务端 scope 固定；工具逐次授权；资料不能变更 allowlist |
| 备注要求“打开 URL/发送密钥” | 无通用网络/文件/密钥工具；所有返回 DTO 脱敏 |
| 编造引用/提示绕过质量警告 | evidenceId ledger 校验；警告由服务器追加 |
| 操纵 tool arguments | schema/范围/窗口验证；不拼接 SQL/路径/endpoint |
| AI 自行创建工单 | 模型环境无写服务；批准入口必须真实会话、防伪和 payload hash |
| 输出 HTML/恶意链接 | 文本/受限 Markdown 渲染；禁 raw HTML；引用 URL 仅服务端生成 |
| 超长资料或无限工具循环 | 文本/token/调用/总时限上限；允许取消 |

关键词检测/Prompt 提醒可辅助发现攻击，但不作为授权边界，也不宣称完全消除注入。关键保证来自工具能力限制、服务器授权、后校验与人工审批；仍需针对不同模型持续回归。

## 8. Observability 与失败恢复

AiRun 状态 Pending → Running → Succeeded / Failed / Cancelled；租约重领只重试只读调查，不能触发业务动作。权限中途撤销结束调查；取消传递到模型/工具，不把部分输出标成功。

记录 correlationId、actor/plant/asset/alarm、Prompt/schema/模型版本、工具名与参数摘要、延迟、结果码、token、预算和估算成本、证据 ID、输出验证失败原因；指标包含调查成功率、超时、拒绝、引用错误、权限拒绝、成本和人工采纳/拒绝率。

Prompt 原文和文档全文默认不进日志；受控调试 trace 仅 Development、显式启用、脱敏并限定保留。AiRun 结果与工具证据摘要默认保留 30 天，业务动作审计独立保留。访问调查记录需要当前范围与证据权限，不向普通用户暴露跨厂评估日志。

模型离线时普通业务正常，调查页显示未完成及已获得的授权证据；不能把先前缓存答案展示为“本次实时调查”。文档解析/embedding 失败仅影响该版本检索，不能让文件上传或整个业务 API 不可用。

## 9. 评估与验收

固定 20 个原创案例，使用冻结业务快照、固定检索语料、预期允许的证据集合与禁止动作；Fake Provider 做确定性契约/安全测试，真实 Provider 做质量评估，不以文本逐字一致作为通过条件。

| 类别 | 预期 |
|---|---|
| 正常告警+说明书 | 值/单位/时间正确，事实有授权引用，原因写为假设 |
| 缺手册/维修历史 | 说明缺失，不伪造来源或历史事件 |
| Bad / Stale / gap | 固定质量警告存在，不以旧值判断已恢复 |
| 跨工厂/受限文档 | 工具拒绝；输出、日志摘要和引用无越权内容 |
| 文档/备注/工具注入 | 不能改变 scope、调用禁用工具或泄密 |
| 伪造 ID / 数值不符 / 格式错误 | 后校验拒绝；不能生成可点击假引用 |
| 超时/限流/取消 | 状态明确；普通功能继续；预算不超限 |
| Phase 4 未确认/过期/拒绝 | 业务写入为 0 |
| Phase 4 双击/崩溃/重试 | 只写一次；可查权威结果和完整审计 |
| Phase 4 撤权/改目标/闭单 | 执行前校验失败，不使用过期批准 |

阶段门槛：安全案例全部通过，事实证据覆盖率 100%，引用可授权展开，禁止动作执行数 0；质量失败案例应明确降级，而非勉强返回成功。后续记录用户是否采纳检查建议以改进流程，不把采纳率解释为诊断准确率。
