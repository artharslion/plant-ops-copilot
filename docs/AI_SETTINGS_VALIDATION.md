# 工作台 AI 模式设置

2026-10-22。管理员可在工作台侧栏“设置”(`/settings`)中选择模拟AI或真实LLM并保存。设置对整个工作台之后新建的调查生效；采集、告警、读取设置和保存设置本身均不调用模型。

## 行为与配置

- 设置只保存 `Fake` / `OpenAiCompatible` 和乐观并发版本，写入新增单行表 `AiRuntimeSettings`；变更与既有AuditLog同事务提交。API/Worker每次创建/处理作业通过同一个数据库协调，无需为每次切换重启容器。
- 新建AiRun固定Provider/Model。已排队或运行中的作业继续使用创建时的模式，之后切换设置不会把Fake作业变成付费作业，也不会把真实作业伪装成Fake。真实模型名称若与当前服务端配置不一致，执行失败，不静默换模型。改变模式不是取消旧作业；取消仍使用调查页原入口。
- 每次执行使用独立作用域的AiOptions副本；先按AiRun选择，再解析原有Gateway和Engine，避免API/Worker或不同调查共享可变选项。原Fake、真实兼容适配、受控工具、预算和事实/引用校验继续复用。
- GET/PUT `/api/v1/settings/ai` 都要求Admin；服务层再核对当前启用状态及角色，PUT沿用CSRF防护。版本冲突返回409，不能覆盖另一管理员的更新。
- 接口仅返回Provider、RealModel、RealConfigured、Version；没有密钥/地址字段，浏览器不能填写任意端点或读取密钥。RealConfigured表示本机配置通过格式检查，不是连通性或供应商稳定性证明。未配置真实模型时不能启用。

Docker仍由 `deploy/.env.docker` 向API/Worker注入同一组 `AI_BASE_URL` / `AI_MODEL` / `AI_API_KEY`，模式缺省 `AI_PROVIDER=Fake`。已有本机 `appsettings.Ai.Local.json` 不会自动被容器读取；本轮部署只将既有真实配置安全地写入已忽略的Docker环境文件，保持缺省Fake。源码启动仍可先加载 `deploy/Use-LocalAi.ps1`；尚无设置行时沿用该进程既有Provider配置。

首次安装新版本需要迁移 `20261022023112_AiRuntimeModeSettings`，只新增设置表，不改动业务表内容或历史调查。设置不保存密钥，迁移中也没有默认启用真实调用。普通restart不应用迁移；沿用现有显式升级流程。

## 本次验证

核心后端56项通过（0失败/跳过）：管理员/匿名/Viewer权限、CSRF、无密钥拒绝启用、返回不泄露密钥/端点、版本冲突、审计、独立Host共享选择、两种方向切换时旧队列保持原模式、作用域隔离和模型变更拒绝，以及既有Gateway/调查服务回归。真实分支测试使用虚构凭据和记录引擎验证实际Gateway类型，不调用外网。

兼容回归123通过（0失败/跳过）：预算、流式响应、脱敏传输诊断、事实契约、调查引擎/20固定场景、P4动作提案和健康入口；约5分54秒。TRX在 `artifacts/ai-settings/20261022/ai-settings.trx` 和 `ai-settings-regression.trx`。

前端33项通过（新增3项设置页面逻辑）：加载不保存、只提交模式和版本、冲突保留原状态与刷新、非管理员不请求设置。类型/构建、lint和格式检查通过；保留既有SignalR注释和包体大小提示。

本轮没有因设置功能再发送真实LLM请求；2026-10-22此前唯一真实请求的HTTP502失败记录仍保留在 [Docker真实模型测试](DOCKER_REAL_LLM_VALIDATION.md)，不能由本功能验证改写为成功。

## 8080部署与验证

已部署到 `http://127.0.0.1:8080/settings`，Admin登录后侧栏可见“设置”。API/Worker/Web镜像先使用独立 `settings-20261022` 标签构建，验证后激活；原镜像保留为 `before-settings-20261022`。更新前对19GB演示库做了在线完整逻辑备份（自定义格式文件1,439,733,980字节），备份目录可被pg_restore列出，另保留资料文件及原环境文件；没有声称重跑完整恢复实验。备份与回滚资料位于本地忽略目录 `artifacts/ai-settings/upgrade_20261022/`。

部署核对发现原环境最新迁移为 `20261020081752_ApprovedInvestigationNotes`，因此升级同时补齐源码已有的 `20261020093426_ControlledOpcUaSourceHealth`，再应用本次设置表迁移。实际SQL只新增DataSourceHealth/AiRuntimeSettings及迁移记录，不重置账号、业务历史或演示种子；旧OPC UA常驻开关仍保持关闭。迁移前暂停写服务，之后重启并确认四个服务健康、8080 ready返回200。

实际8080授权HTTP验证：初始Fake/version0 → OpenAiCompatible/version1 → Fake/version2，读取模型为 `gpt-6.1-sol`、RealConfigured=true；双向切换和数据库回读通过。调查仍为原来的9条Succeeded，没有新增AiRun或模型请求。结果在 `artifacts/ai-settings/20261022/deployed-switch.json`，浏览器未登录Admin做视觉验收；前端交互依据33项组件状态测试与实际部署接口验证。

最终保持模拟模式，由用户自行在设置页启用真实模式后手动发起调查。启用真实模式不是对服务可用性的保证，也不会重试此前失败的调查。
