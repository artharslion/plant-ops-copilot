# IndustrialSim → OPC UA → 真实 AI 调查验收

2026-10-10。本轮串通现有有限能力，未启用常驻 OPC UA，未扩展 P4 写动作。

后续同日增量：[一次性分钟聚合与趋势证据](FINITE_MINUTE_AI_VALIDATION.md)已实现并通过221项检查、原始样本/分钟API核对；新增真实轮模型Unavailable，失败如实保留。本文以下为此前chain_real_20261010成功轮的历史记录，其分钟缺失限制不能套用到后续新轮。

## 实测结论

`chain_real_20261010` 的一次真实 `gpt-6.1-sol` 调查成功；`RealProviderVerified=true` 仅表示这次业务调用成功且输出通过现有事实/引用契约。不能据此宣称供应商稳定、完整 P2/G0 或固定二十场景 P3 质量验收通过。

- 独立库/schema：`plantops_tests.opcua_demo_chain_real_20261010`，独立资料及证据目录；无人工告警 fixture、无历史 raw 预填，旧业务库和所有旧报告保留。
- IndustrialSim 外部进程通过 `opc.tcp://127.0.0.1:48481` 提供仿真观测，namespace `urn:industrial-sim:runtime`、String node `plantops-p101/vibration`。通信、订阅、入库是真的，设备观测为仿真；`AiSimulated=false` 与 `ObservationSimulated=true` 分别记录。
- 50 秒采集48条，观察到4 → 12.6 → 6 mm/s；一个告警触发并恢复，零缺口，重复批次不增加读数。最终所有来源/映射停用，仿真进程退出。
- 告警 `6616cb43-2f6d-4dee-b0a6-d0ee2be21689` → 触发样本 `1f621fa9-22ba-4098-a5d3-22153237462b` → 调查 `24b93cc0-a6f6-455b-b6e6-287891852186`。
- 调查由授权 HTTP 创建，有限 `--investigation-once` Worker 领取；一次领取、一轮模型、五次默认只读工具，约11.22秒，保守预留输入11319/输出900 token。供应商实际用量与费用没有持久化对账，本轮费用未知。
- 十项证据包含历史告警、触发样本/来源/映射/质量/协议源时间与状态码、设备、趋势窗口、告警历史、维护摘要、三项当前发布资料片段及质量提示。结果十条事实逐字匹配所引证据，可能原因与建议仍需人工核验。
- admin 趋势/告警200；viewer 趋势/告警200、他人调查/引用404；南厂 engineer 上述全部404；匿名趋势401。调查完成后告警版本未变、未人工确认、工单仍为零。

## 运行边界与限制

只运行本轮采集50秒/最多100条；模型总预算60秒/4轮/8工具/16k输入/2k输出。请求默认仍30秒，本轮显式55秒且零重试；同一 run 已有调查尝试时脚本拒绝再次执行。真实调用只有这一次，没有探针连发，没有 Fake 降级。

采集前数据库容器卷可用988311715840字节。宿主D盘初始59436474368、结束59436003328字节，观测增长471040；C盘初始121257013248、结束121228328960字节，观测增长28684288。均满足2GiB余量/256MiB新增观测界限，每批提交前复核。宿主消耗包含同期其他活动，并非WAL峰值或硬配额；容器卷为采集前检查。

有限 Worker 不启动聚合/保留服务，所以这次 AI 的已完成分钟趋势没有可用数值，即使 raw 已真实入库。结果明确报告缺数据，不补造分钟趋势；原始触发样本12.6 mm/s通过E2引用可回查。停止后观测陈旧/配置停用提示保留，历史恢复状态不等于当前设备正常。资料是原创虚构维护指南，节选须展开全文片段核对。

## 复现与证据

在仓库根目录，用全新小写 run：

```powershell
# Fake 编排验证，通信仍为真实 OPC UA
./deploy/Run-IndustrialSimInvestigation.ps1 -Run chain_fake_new
# 显式加载本地真实模型配置，仅一次业务调查
./deploy/Run-IndustrialSimInvestigation.ps1 -Run chain_real_new -RealAi
```

不输出/覆盖本地密钥。脚本构建到本轮独立目录，选临时API端口，保留旧服务，只关闭自己启动的仿真/API/Worker。API与Worker只连接新测试schema；不迁移/重置业务plantops。

真实证据根 `artifacts/opcua-demo/chain_real_20261010/`：

| 文件 | 用途 |
|---|---|
| result.json | 全部48条协议样本、告警、映射、采样快照、缺口及磁盘观测 |
| database-disk.json | 数据库容器卷余量检查 |
| investigation-scope.json | 唯一调查标识与期限/重试范围 |
| investigation.json | 持久化业务结果、模型标识、证据及预算预留 |
| expanded-evidence.json | 每项重新授权的展开结果，资料含版本与行号 |
| investigation-check.json | 触发样本关联、真实/仿真独立标识、告警未改及本次成功 |
| api-check.json | 三用户/匿名、工厂/所有权拒绝结果 |

Fake 编排证据在 `chain_fake_20261010`；明确 `AiSimulated=true`、`RealProviderVerified=false`。135项专项/回归最终报告：`artifacts/ai-investigation/chain_stage1final_20261010/results.trx`，135/135、零跳过。首次旧服务锁文件、次轮分析器失败记录均保留；后续独立构建零警告/错误。上轮约30.67秒的真实超时报告仍保留，不能由本轮覆盖或改成成功。

## Chrome 实际验证

独立API/前端指向本轮schema，保留此前5080/5173服务。实际登录admin，切换已恢复告警，页面读取本轮真实模型完成快照，无额外模型调用；事实/假设/建议/缺失信息、仿真来源及分钟缺失/停止后陈旧提示均可见。

E2展开包含与告警事件相同的触发样本标识、来源与映射标识、12.6 mm/s、Good、OpcUa、协议源时间及原码0。E8展开为当前授权资料版本1/行5–8及完整片段；每次展开都重新授权。截图在同一证据根：`browser-trigger.jpg`（整页）、`browser-trigger-expanded.jpg`及`browser-document-expanded.jpg`（可见区域），检查记录为`browser-check.json`；资料整页截图曾超时，改为可见区域截图成功，未重复模型调用。

页面本轮审阅地址及进程记录在 `review-session.json`，只留API/UI供审阅，无采集/调查Worker。浏览器仅完成admin单会话；跨用户/跨工厂仍以独立HTTP会话检查为准，不声称完整两个浏览器会话验收通过。
