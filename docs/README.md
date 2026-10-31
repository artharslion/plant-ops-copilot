# 文档导航

[项目首页](../README.md) · [当前开发计划](../ROADMAP.md)

按任务选择所需文档，不需要通读整个目录。首页提供功能与启动入口；路线图集中管理待办。设计文档中的后续契约不代表已经实现，近期验收记录也不代表完整质量或生产可靠性通过。

## 运行与开发

- [Northflank 部署](../deploy/northflank/README.md) · [Docker Hub 联合部署](../deploy/dockerhub/README.md)
- [Docker 指南](DOCKER.md) · [本地开发](DEVELOPMENT.md) · [代码风格与检查](CODE_STYLE.md)
- [AI 接入配置](AI_PROVIDER_SETUP.md) · [AI 模式设置](AI_SETTINGS_VALIDATION.md)
- [IndustrialSim 工作台接入](INDUSTRIALSIM_WORKBENCH.md) · [P-101 演示规程](INDUSTRIALSIM_P101_DEMO_MANUAL.md)（发布脚本使用）

## 设计与契约

- [项目规格](../PROJECT_SPEC.md) · [系统架构](../ARCHITECTURE.md) · [领域模型](DOMAIN_MODEL.md)
- [AI 基础设计](AI_DESIGN.md) · [证据与工单质量](AI_INVESTIGATION_QUALITY.md)
- [调查建议 v3](INVESTIGATION_RESPONSE_V3.md) · [调查诊断](INVESTIGATION_DIAGNOSTICS.md)
- [OPC UA 集成](OPCUA_INTEGRATION.md) · [最小三点场景](P5_3_MINIMAL_SCENARIO.md)

## 当前验收证据（按需查阅）

- 调查：[逐点可信度](P6_POINT_TRUST_VALIDATION.md) · [升温分析](P6_HEATING_VALIDATION.md) · [按需取证](P6_2_ON_DEMAND_VALIDATION.md) · [运行版本与用量](P8_1_RUN_RECORD_VALIDATION.md)
- 真实模型：[10 月 29 日](P6_1_REAL_VALIDATION_20261029.md) · [10 月 30 日](P6_1_REAL_VALIDATION_20261030.md) · [调查 v3](INVESTIGATION_V3_REAL_VALIDATION_20261030.md)；包括失败与未验收范围。
- 采集：[受控 OPC UA](OPCUA_RUNTIME_VALIDATION.md) · [IndustrialSim 三点协议](INDUSTRIALSIM_RUNTIME_VALIDATION.md)
- 人工工单：[创建](P4_WORK_ORDER_VALIDATION.md) · [追加备注](P4_NOTE_VALIDATION.md) · [页面交互](UI_INTERACTION_VALIDATION.md)
- [近期增量计划](plans/)：仅保留当前工作区的按需取证与调查 v3 计划；优先级以路线图为准。

已结束的早期计划、P1/P2 与早期 AI/Docker 验收报告、旧开发日志已从工作树删除，可从 Git 历史查阅。许可证、运行指南、测试及当前真实验证证据继续保留。
