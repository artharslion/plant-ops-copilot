# IndustrialSim 单点 OPC UA 演示验收

2026-10-09。两个仓库独立进程运行，经本机 OPC UA 通信。该点完成有限订阅、标准化、实际 PostgreSQL 入库、告警触发/恢复与授权 HTTP 读取；不是完整 P2、长期运行或 AI 调查验收。

## 实际结果

接受轮：`plantops_tests.opcua_demo_accepted_20261009`，证据 `artifacts/opcua-demo/accepted_20261009/result.json` 与 `api-check.json`。北京时间14:25:49至14:26:39，采集窗口50秒。

| 项目 | 接受轮结果 |
|---|---|
| 节点 | Namespace URI `urn:industrial-sim:runtime`，String `plantops-p101/vibration` |
| 连接 | `opc.tcp://127.0.0.1:48481`；None，仅 Development 回环 demo |
| 单位 | 标准 EngineeringUnits UN/CEFACT C16，mm/s；经过现有元数据 Reader 核验 |
| 入库 | 48条：4有15条、12.6有15条、6有18条，均保留 OPC UA 来源、原码和源时间 |
| 重试 | 每批用相同不可变样本再次提交，48次重复事件被识别；无额外读数或重复告警 |
| 告警 | 一个，触发值12.6；14:26:07触发，14:26:24恢复，最终Recovered |
| 采样快照 | 实际 SDK 返回 sampling1000ms、publishing1000ms、queue10；保存生效身份和开始/关闭时刻 |
| 缺口 | 正常接受轮0条；此前失败轮的可能缺口保留 |
| HTTP | admin/viewer读取趋势和告警200，与入库48条一致；南厂engineer两接口404；匿名401 |
| 停止 | 本轮进程关闭，本轮来源/映射停用；普通 Worker 没有注册常驻 OPC UA Adapter |

仿真服务器新增可选 `observationIntervalMs`，默认0关闭，100–60000ms有效。演示显式为1000ms：从 StateStore 重新观测当前值并刷新协议时间戳，不改变设备状态、不推进仿真时钟。恒定值通知与故障抑制/恢复已用真实客户端验证；缺失时段不补造数据。

## 复现

前置：现有本地PostgreSQL及plantops_tests、deploy/.env、满足Identity规则的本地Demo.Password，.NET10与外部框架源码。脚本使用PowerShell7，默认外部路径 `D:/Repos/industrial-device-simulation-framework`，支持SimulatorRoot参数。不要同时构建同仓库或复用run。

在 Plant Ops 仓库根目录：

```powershell
./deploy/Run-IndustrialSimDemo.ps1 -Run demo_20261009_new
./deploy/Test-IndustrialSimDemo.ps1 -Run demo_20261009_new
```

第一条构建两项目，启动本轮仿真场景、有限Worker并保存结果；第二条启动本轮独立API，用临时本机端口验证授权读取，随后关闭。仅停止本轮拥有的进程，保留所有新旧schema与资料。不会修改业务plantops或原p17_ui_20261005。

演示最多60秒、一个点位、100条提交上限、100条通知队列；宿主资料盘与LocalApplicationData所在盘各检查2GiB余量及256MiB观测新增消耗。每批提交前复核，不是操作系统硬配额或正式WAL峰值测量；非默认Docker数据盘需先核对其实际余量。API检查禁用Realtime与默认Outbox清理，不启动普通Worker。

查看已有UI时，API连接须显式选本轮测试schema，Documents:StorageRoot选本轮documents；Web仍代理5080。不能把旧API页面当本轮数据，不启动普通Worker重新采集。停止后最新值应陈旧；历史趋势和已恢复告警仍可读。本轮完成HTTP验证，没有新增浏览器验收。

## 验证与失败记录

- 外部框架：OPC UA23项全过（含4项标准单位与恒定值周期通知/故障恢复）；配置22项全过；CLI/运行时组合/共享状态定向15项全过，均无跳过。真实周期通知测试先只收到1条失败，再实现显式周期观测通过。
- Plant Ops：常规后端回归312通过/0失败/0跳过，显式排除须独立run的SmallBackupRestoreTests；新增11项覆盖回环endpoint及Development/测试库/run/期限保护。后续真实批次重试在接受轮独立验证，最后定向11项再次通过。
- 锁定恢复与最终解决方案构建通过，0 warning/error。前端仅更新停用提示，首次格式检查失败后修正，最终格式检查、lint、类型检查及构建通过，保留既有两条SignalR/Rollup注释提示；没有改变启用按钮或放开管理API。
- 初始单位节点读取失败，补EngineeringUnits并明确HasProperty浏览后4项通过。trial1启动探测慢，trial2暴露场景set缺显式device；修正场景并经确定性CLI验证。trial3只有3条变化通知、无告警，触发周期观测修正。失败schema/资料/报告均保留。
- 首次HTTP检查端口5188被Windows拒绝绑定，改用系统分配的可用临时端口；随后检查脚本将JSON时间解析为本地offset导致趋势400，改为UTC后成功。原错误日志追加保留，以api-check.json的明确通过结果为准。

## 剩余范围

SDK版本各自锁定，外部Server1.5.378.134与本项目Client1.5.378.182已在本轮互通。没有把外部源码/包复制进本项目；Worker新增对现有Integrations项目的引用，相应锁文件更新，无SDK升级。

采样快照持久化只用于单进程独立demo，不声称解决通用并发配置切换或旧回调屏障。证书加密/批准、授权Browse、逐映射健康、完整故障矩阵与浏览器协议闭环仍待增强；当前None端点不能用于生产。完整P2/G0仍未验收，G0按AI演示计划延期。P3/P4尚未实现，下一主线是Provider选择及AI授权证据调查。
