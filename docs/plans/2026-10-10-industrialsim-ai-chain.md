# IndustrialSim AI Chain Implementation Plan

> 用户已授权直接实施；不委派、不 push、不创建 PR。沿用当前干净分支，每个验证完成阶段立即提交。

**Goal:** 在独立测试 schema 串通 IndustrialSim → 真实 OPC UA → 落库告警 → 一次真实模型调查 → 授权引用展开。

**Architecture:** 复用有限采集与 API/单次 Worker，新增编排脚本，按触发样本关联证据。采集后停用映射，来源证据仍从历史样本读取；模型真实标识与仿真观测独立。真实失败保留，禁止自动降为 Fake 或自动重跑。

**Tech Stack:** .NET 10、PostgreSQL、PowerShell 7、Vue、IndustrialSim。

## 已核查

- 基线 3a5a824，codex/p2-opcua，工作区干净；仓库及父目录无 AGENTS.md。
- 已读 AI_INVESTIGATION_VALIDATION、AI_PROVIDER_SETUP、IndustrialSim 验收、现有两份计划及 ROADMAP。
- 上次约 30.67 秒 Timeout 与 RequestTimeoutSeconds=30 一致；无证据证明上游协议失败。现有非流式 strict schema、low reasoning，业务总期限 60 秒。
- 不修改本地密钥/业务库/旧 schema；保留现有服务。来源/映射结束停用，普通 Worker 不启用 OPC UA。

## 阶段 1：请求期限及历史来源证据

Files: AiOptions.cs、InvestigationService.cs、OpcUaDemo.cs、AiGatewayTests.cs、InvestigationServiceTests.cs。

1. 单次请求配置允许 1–60 秒，默认仍 30；演示显式 55 秒、零重试，总预算仍 60 秒/4轮/8工具/16k输入/2k输出。
2. 从固定 actor/设备告警的 TriggerReadingId 查询同工厂设备点位样本，证据含协议来源、映射/来源标识、值/单位/时间。来源登记名明确仿真演示。
3. 测试停用映射仍可展开正确触发样本及请求期限边界，运行现有专项回归并提交。

## 阶段 2：有限编排

Files: deploy/Run-IndustrialSimInvestigation.ps1、Test-IndustrialSimDemo.ps1（复用）。

1. 新 run 跑现有 50 秒/最多100条采集，预检2GiB/256MiB边界。真实调用前检查配置与磁盘；仅测试库独立schema/资料目录。
2. 新临时端口 API 登录、按 result.json 的告警创建一次调查；单次 Worker 领取执行，不运行采集/清理。
3. 保存业务结果/展开证据/样本关联及匿名/跨厂拒绝；失败同样保存，脚本不重试。只关闭本轮进程，恢复环境。
4. 先 Fake 验证脚本，再唯一一次真实调用，保留所有失败记录；提交。

## 阶段 3：页面与验收

1. 独立 API/UI 指向真实轮 schema；Chrome 展开触发样本、资料证据，保存截图，不重复调用模型。
2. 更新验收文档及计划为实测结果，说明费用未知、仿真观测、真实模型结果及剩余范围。
3. 核对工作区与进程；完成验证后提交。不宣称完整 P2/G0/20场景P3 通过。

## 阶段 1 完成
135/135、零跳过：artifacts/ai-investigation/chain_stage1final_20261010/results.trx。首轮旧API锁文件与次轮分析器失败均保留；使用独立构建目录解决，旧进程未停止。来源按原始触发样本查询，不依赖当前启用映射。
