# 工作台 AI 模式

Admin 可在工作台侧栏的“设置”（`/settings`）选择模拟 AI 或真实 LLM，然后保存。模式对共享数据库的工作台生效，用于此后创建的调查。读取或保存设置、采集和告警均不发起模型请求。

- 新建调查固定 Provider 和 Model。已排队、运行中的任务沿用创建时的配置。真实模型名称变更导致不匹配时，原任务会失败。需要终止任务时，在调查页面使用取消操作。
- 首次保存前使用服务端默认 Provider。Docker 源码部署默认 `Fake`，Api 和 Worker 使用同一份服务端配置。真实模型的端点、模型和密钥由部署环境提供，具体步骤见 [模型接入指南](AI_PROVIDER_SETUP.md)。
- `GET /api/v1/settings/ai` 与 `PUT /api/v1/settings/ai` 均要求 Admin。保存仅提交 `provider`（`Fake` 或 `OpenAiCompatible`）与 `version`，并校验防伪令牌。并发冲突返回 409，刷新后再选择。模式与版本存入 `AiRuntimeSettings`，变更与审计在同一事务提交。
- 接口返回 Provider、RealModel、RealConfigured 与 Version。真实模式要求服务端配置通过格式检查。需要验证网络或模型能力时，使用接入指南中的对应检查。
- 旧库通过现有显式升级流程应用 `20261022023112_AiRuntimeModeSettings` 及后续迁移。升级前按部署说明备份。常规重启只重启进程。
