# 文档导航

[项目首页](../README.md) · [当前开发计划](../ROADMAP.md)

按任务选择所需文档，不需要通读整个目录。首页提供功能与启动入口；路线图集中管理待办。新调查优先查 v3 契约；历史记录不作为当前待办或操作授权。设计文档中的后续契约不代表已经实现，验收记录也不代表完整质量或生产可靠性通过。

## 运行与开发

- [Northflank 部署](../deploy/northflank/README.md) · [Docker Hub 联合部署](../deploy/dockerhub/README.md)
- [Docker 指南](DOCKER.md) · [本地开发](DEVELOPMENT.md) · [代码风格与检查](CODE_STYLE.md)
- [AI 接入配置](AI_PROVIDER_SETUP.md) · [AI 模式设置](AI_SETTINGS_VALIDATION.md)
- [IndustrialSim 工作台接入](INDUSTRIALSIM_WORKBENCH.md) · [P-101 演示规程](INDUSTRIALSIM_P101_DEMO_MANUAL.md)（发布脚本使用）

## 设计与契约

- [项目规格](../PROJECT_SPEC.md) · [系统架构](../ARCHITECTURE.md) · [领域模型](DOMAIN_MODEL.md)
- [调查建议 v3](INVESTIGATION_RESPONSE_V3.md)：新调查输出与工单就绪契约；[调查诊断](INVESTIGATION_DIAGNOSTICS.md)：失败定位与留存。
- [AI 链路概述](AI_DESIGN.md) · [证据与工单质量](AI_INVESTIGATION_QUALITY.md)：仅在相应边界需要核对时读取。
- [OPC UA 集成](OPCUA_INTEGRATION.md) · [最小三点场景](P5_3_MINIMAL_SCENARIO.md)

## 验收证据（按对应任务查阅）

- 调查：[逐点可信度](P6_POINT_TRUST_VALIDATION.md) · [升温分析](P6_HEATING_VALIDATION.md) · [按需取证](P6_2_ON_DEMAND_VALIDATION.md) · [运行版本与用量](P8_1_RUN_RECORD_VALIDATION.md)
- 真实模型：当前 v3 先查 [10 月 30 日 v3 报告](INVESTIGATION_V3_REAL_VALIDATION_20261030.md)；早期版本回放才查 [10 月 29 日](P6_1_REAL_VALIDATION_20261029.md) 或 [10 月 30 日早期报告](P6_1_REAL_VALIDATION_20261030.md)。均保留失败与未验收范围。
- 采集：[受控 OPC UA](OPCUA_RUNTIME_VALIDATION.md) · [IndustrialSim 三点协议](INDUSTRIALSIM_RUNTIME_VALIDATION.md)
- 人工工单：[创建](P4_WORK_ORDER_VALIDATION.md) · [追加备注](P4_NOTE_VALIDATION.md) · [页面交互](UI_INTERACTION_VALIDATION.md)

## 历史资料（默认不读）

- [历史索引](archive/README.md)：已结束的按需取证/v3 实施计划、旧路线图及详细验收基线、旧 AI 设计与质量记录。未完成验收已汇入当前路线图。
- `docs/archive/` 由 `.ignore` 排除常规搜索；需要核对历史版本或详细验收标准时，再按路径与关键词定位。

已删除的早期计划、P1/P2 与早期 AI/Docker 验收报告、旧开发日志仍可从 Git 历史查阅。许可证、运行指南、测试及真实验证证据继续保留；`artifacts/`、`work/`、`.vs/` 已由 Git 忽略，不递归加载，诊断时只读指定运行的必要片段。
