# 文档导航

[项目首页](../README.md) · [开发计划与验收状态](../ROADMAP.md)

先定位任务涉及的源码和测试，再按下表查所需说明。部署命令、设计契约和已验证范围各有对应入口。

| 任务 | 查阅 |
| --- | --- |
| 本地开发、代码检查 | [开发指南](DEVELOPMENT.md) · [代码风格](CODE_STYLE.md) |
| 启动或维护容器 | [源码 Docker](DOCKER.md) · [Docker Hub](../deploy/dockerhub/README.md) · [Northflank](../deploy/northflank/README.md) |
| 接入 IndustrialSim 或复验采集 | [工作台接入](INDUSTRIALSIM_WORKBENCH.md) · [OPC UA 集成](OPCUA_INTEGRATION.md) · [隔离三点复验](INDUSTRIALSIM_RUNTIME_VALIDATION.md) |
| 核对业务边界、数据关系或服务分层 | [项目规格](../PROJECT_SPEC.md) · [领域模型](DOMAIN_MODEL.md) · [系统架构](../ARCHITECTURE.md) |
| 配置 AI 模型与工作台模式 | [模型接入与密钥](AI_PROVIDER_SETUP.md) · [工作台 AI 模式](AI_SETTINGS.md) |
| 修改调查行为或工单建议 | [AI 工程约束](AI_ENGINEERING_GUARDRAILS.md) · [调查 v3 契约](INVESTIGATION_RESPONSE_V3.md) · [证据质量与语义评估](AI_INVESTIGATION_QUALITY.md) |
| 核对调查编排、排查单次运行 | [AI 链路](AI_DESIGN.md) · [诊断、用量与留存](INVESTIGATION_DIAGNOSTICS.md) |

IndustrialSim 的 [P-101 演示规程](INDUSTRIALSIM_P101_DEMO_MANUAL.md) 是随演示发布的资料。需要调整演示知识证据时按原路径查阅。

阶段验收以 [开发计划](../ROADMAP.md) 的对应章节为入口。旧设计、已结束计划与验证报告在 [历史索引](archive/README.md)，默认搜索跳过 `docs/archive/`。仅在回放、追溯验证记录或核对链接到的验收基线时打开所需章节。本机证据按报告中的运行路径定位。
