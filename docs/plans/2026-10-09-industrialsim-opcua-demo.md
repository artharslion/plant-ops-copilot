# IndustrialSim OPC UA Demo Implementation Plan

**Goal:** 用外部 IndustrialSim 的一个振动点位跑通 Plant Ops 的实际订阅、入库、告警与恢复，为 AI 调查提供可追溯业务证据。

**Architecture:** 两仓库独立运行，经回环 OPC UA 通信。Plant Ops 新增仅 Development 可执行的有限 demo 命令，复用 IMeasurementSource、标准化器、节点校验与业务采集管道；只创建全新 plantops_tests schema，不升级或改写原业务库。协议 None 只用于明确限定的本机 demo。普通 Worker/管理 API 继续保持原禁用策略。

**Tech Stack:** 现有 .NET 10、固定 OPC Foundation SDK、PostgreSQL、IndustrialSim YAML。

## Stage 1：外部虚拟设备

修改外部仓库 IndustrialOpcUaServer.cs，给支持的数值单位发布标准 EngineeringUnits 属性。新增 vibration-plantops.yaml 与 vibration-plantops.yaml 场景（不同目录）：正常4→高12.6→恢复6。先添加真实客户端属性读取失败测试，再实现，运行 OPC UA 测试与配置校验。未知/无单位不伪造标准身份。提交已验证阶段。

## Stage 2：有限订阅与业务管道

新增 OpcUaDemoSource.cs：严格回环 endpoint、一个明确 Namespace URI/String node、只读、真实 server revised 参数、StatusValueTimestamp、有限队列与期限、取消释放。默认不注册常驻 Worker，也不放开任意来源启用。SDK 回调只做有界标准化/入队；业务写入复用 MeasurementSourceRunner。

Worker 新增 --opcua-demo 入口：限制 Development、plantops_tests、新 schema/run，初始化虚构业务和文档，仅在新 schema 切换 P-101 映射；订阅建立时保存真实修正快照，停止/故障关闭区间，重试保留可能缺口。执行期限不超过60秒、本轮一个点位；保存原值/状态/时间/采样参数、告警/恢复结果与计数。先验证回环/期限/节点边界，再做真实协议与 PostgreSQL 实验。

## Stage 3：复现与验收

新增有限启动脚本，仅启动并停止本轮所属进程，不打印凭据。运行前检查宿主余量、已有端口、全新目录；使用全新 schema/资料路径，保留历史。实际订阅验证正常4、高12.6、恢复6，单次告警且恢复；重复样本不重复告警；属性单位、采样修正身份与区间落库。完整回归、前端无改动不重复构建。保存结果、限制及启动说明，提交已验证阶段。

## 后续 AI 主线

该增量完成后按 AI 演示计划推进 P3；完整证书运维/Browse/长期协议负载仍后续增强。本次不恢复 G0 满量任务。若真实 Provider 选择/凭据缺失，先实现无密钥 Fake 与授权证据闭环，再通过用户选择配置真实模型，不能假称真实 AI 验收完成。
