# 文档导航

[返回项目首页](../README.md)

## 运行与开发

- [Docker 指南](DOCKER.md)：容器构建、完整演示栈、持久化与可选 AI 配置。
- [本地开发指南](DEVELOPMENT.md)：环境准备、账号、模拟演示、配置、健康检查、测试与关闭服务。
- [代码风格](CODE_STYLE.md)：格式化、检查命令与持续检查。
- [AI 接入配置](AI_PROVIDER_SETUP.md)：可选模型配置、密钥加载与能力探针。
- [OPC UA 集成](OPCUA_INTEGRATION.md)：映射、质量、重连与模拟器实验。
- [受控 OPC UA 常驻采集](OPCUA_RUNTIME_VALIDATION.md)：本机多点 Worker、连接健康、停服重连与复现范围。

## 设计与规划

- [项目规格](../PROJECT_SPEC.md)：业务范围、场景、权限、API 与验收契约。
- [系统架构](../ARCHITECTURE.md)：模块、部署、数据流、故障处理与架构决策。
- [领域模型](DOMAIN_MODEL.md)：实体、字段、关系与状态机。
- [AI 设计](AI_DESIGN.md)：证据检索、工具、输出、权限与审批边界。
- [开发计划（当前执行入口）](../ROADMAP.md)：代码基线、最小 OPC UA→AI 闭环顺序、观测时效/双维度判断、P5–P8 验收及可选外部安全 Server 里程碑。
- [历史增量计划](plans/)：按日期保留的设计与实施快照；当前优先级和剩余任务统一以开发计划为准。

## 历史与验证

- [P4 创建工单首增量](P4_WORK_ORDER_VALIDATION.md)与[追加调查备注](P4_NOTE_VALIDATION.md)：人工确认、固定目标与版本、事务重校验、幂等执行、审计与执行结果恢复。

- [Docker 本地验证](DOCKER_VALIDATION.md)：容器构建、认证、实时连接、资料与重建持久化检查。
- [开发日志](DEVELOPMENT_HISTORY.md)：从旧 README 迁出的阶段更新与历史验收摘要。
- [P1 整体验收](P1_7_VALIDATION.md)与[P1 复盘整改](P1_REVIEW_FIX_VALIDATION.md)：业务闭环、规模、恢复与采集管道验证。
- [AI 授权证据验证](AI_EVIDENCE_VALIDATION.md)与[告警调查验证](AI_INVESTIGATION_VALIDATION.md)：检索、授权、引用、调查编排与页面接入。
- [AI 固定场景验证](AI_SCENARIO_VALIDATION.md)、[流式调查验证](AI_STREAMING_VALIDATION.md)与[真实模型质量验证](AI_REAL_QUALITY_VALIDATION.md)：确定性测试、流式样本与有限真实质量复核。
- [IndustrialSim 协议演示](INDUSTRIALSIM_DEMO_VALIDATION.md)与[AI 联合演示](INDUSTRIALSIM_AI_VALIDATION.md)：有限 OPC UA 采集及调查联调。

历史日志、计划和验收报告记录的是对应时间与范围内的结果；旧测试数量、失败记录和阶段结论不代表当前完整能力。当前功能概览见项目首页，具体证据与限制以对应验收文档为准。
