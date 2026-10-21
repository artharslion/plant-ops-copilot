# 受控本机 OPC UA 常驻采集增量

2026-10-20。本轮新增独立 `OpcUaRuntimeSource`，由普通 `MeasurementWorker` 管理；原 `OpcUaDemoSource` 继续保持有限单点入口。不是完整 P2 或工业网络部署验收。

## 实现范围

- 默认关闭。API 与 Worker 都须显式设置 `OpcUa:LocalRuntimeEnabled=true`，且环境为 Development。只接受明确批准的 `localhost` / `127.0.0.1` 地址、服务端 ApplicationUri、Namespace URI 和精确节点列表。当前只允许 None 本机演示；没有远程端点、证书自动接受或安全降级路径。
- 每源 1–10 个点，1 Hz Periodic，申请 queue 1–10；接受服务端修正的 sampling / publishing 1000–2000ms、queue 1–10。每次会话重新按 Namespace URI 解析并核验所有节点的类型、访问和单位；任一点失败整源 Faulted，不静默替代其他值。
- SDK 回调只规范化并写入 100 条有界通知缓冲。缓冲满使连接失败并登记可能缺口，不静默丢弃；随后复用 Worker 现有可追踪队列、批写、幂等、规则和 Outbox。
- 实际修正参数在共享事务锁及工厂配置锁内重新核对配置版本后持久化，之后才向入库管道放行。配置变化由 Worker 撤销旧枚举；入库仍会再核对完整配置。关闭只匹配本会话的采样身份/生效时间，晚到关闭不能结束后继会话。
- 每源一个串行重连流程，约 1/2/4/8/16/30 秒指数退避并加抖动，最终上限 30 秒。重连重建会话/订阅，不承诺 Republish、TransferSubscriptions 或离线历史回补。安全/身份/元数据/配置失败进入 Faulted，等来源配置版本变化或 Worker 重启。
- `DataSourceHealth` 保存 Connecting / Connected / Reconnecting / Faulted / Stopped、协议保活/最后接收、重连次数与固定脱敏错误码。按授权来源列表返回；超过 10 秒无运行心跳、来源版本变化或来源停用时 `isCurrent=false`。页面将连接存活与最新值/LastGood 的新鲜度分别展示，复用现有坏质量断线、时间异常和趋势缺口提示。
- 部署配置变更需要重启 Worker 或重新保存来源触发重建；不提供热重载证书/端点审批界面。单 Worker 假设保持，未引入多实例租约。

## 已运行的真实协议实验

`plantops_tests.opcua_runtime_trial3_20261020`，证据 [result.json](../artifacts/opcua-runtime/trial3_20261020/result.json)。北京时间 17:46:42–17:47:19，普通 MeasurementWorker 实际运行；观测为仿真，SDK 订阅与 PostgreSQL 写入为真实行为。

| 核查 | 结果 |
| --- | --- |
| 点位 | 3 个明确批准节点，实际 87 条读数 |
| 告警 | 从观测超限触发 1 个告警，重连后恢复，最终仅 1 个 Recovered |
| 故障 | 停止真实 OPC UA Server；Worker 进入 Reconnecting，累计重连 2 次 |
| Namespace | 重启改顺序后重新按 URI 解析，映射正确 |
| 采样区间 | 共 6 段，退出后全部关闭 |
| 缺口 | 6 条，恢复后全部闭合；不解释为精确丢失条数 |
| 清理 | 仅本轮 Server/Worker 停止，本轮来源/映射停用；保留 schema/读数/报告 |
| AI / 业务库 | 0 次模型调用；不连接业务 plantops，不写业务工单 |

trial1 在启动采集前因运行器缺少 IHostEnvironment 失败；trial2 在启动采集前因 SDK 的 None 配置 API 用法失败。分别补齐宿主依赖、改用锁定 SDK 的 AddUnsecurePolicyNone 后，trial3 通过；失败目录/schema 保留，不复用。原 secure selftest/server 默认继续要求 Basic256Sha256 / SignAndEncrypt。

最终代码经 `Run-LocalOpcUaRuntime.ps1 -Run accepted_20261020` 再次复现通过：北京时间 17:52:57–17:53:33，81 条/3 点、6 段采样、6 条闭合缺口、2 次重连、唯一告警 Recovered，退出健康 Stopped。完整原始结果保留在 `artifacts/opcua-runtime/accepted_20261020/result.json`。两轮样本数差异来自实际采集/停服时序，未补造读数。

## 自动检查与部署状态

- 完整后端回归：516 通过、0 失败、3 个条件跳过（备份恢复/真实模型两项），约 9 分 56 秒，`artifacts/opcua-runtime/20261020/runtime-full.trx`。本轮没有打开真实模型测试开关。
- 完整回归后又增加“持续通知不能饿死连接心跳”回归用例；最终带专用测试连接的 OPC UA / Worker / 管道 / 测量定向检查 **83 通过、0 失败、0 跳过**，保存在 `artifacts/opcua-runtime/20261020/runtime-focused-final.trx`。此前未加载数据库连接的单独运行只有 11 项纯测试通过/1 项跳过，不计入数据库验证。
- 前端 30 项测试通过，类型/构建、lint、格式检查通过。构建保留既有 SignalR 注释与包体大小提示。
- 解决方案与协议 spike 构建均通过，0 warning/error；锁定恢复与本轮修改文件格式检查通过。spike 新增对现有 Worker 的项目引用，未更换 SDK/传递包版本。
- 本轮未迁移或更新业务 Compose，`localhost:8080` 仍为原部署；未重置种子/账号、未提交或推送。新迁移只在隔离测试 schema 应用。页面当前用组件状态测试及编译检查验证，尚未做本轮浏览器视觉验收。

## 复现与启用边界

先启动专用 `plantops-p4-tests` 容器；私有连接配置仍在忽略的 `artifacts/p4/database.local.json`。在仓库根目录运行（每轮使用新名称）：

```powershell
./deploy/Run-LocalOpcUaRuntime.ps1 -Run my_unique_runtime_run
```

实验上限 150 秒/500 条，观测磁盘至少留 2 GiB、新增消耗不超过 256 MiB；不是系统硬配额或长期容量验收。运行器只创建新 `opcua_runtime_*` schema，端口 48480 被占用则脚本提前拒绝，不停止已有进程。

手工配置常驻本机演示时，参考 [local-runtime.example.json](../deploy/opcua/local-runtime.example.json)，由部署方将键写入 API/Worker 实际加载的配置或环境变量（环境变量以 `__` 分段）；该文件不会自动加载。环境必须 Development，迁移须包含 `20261020093426_ControlledOpcUaSourceHealth`。先启用来源/点位，再启用精确映射；不直接修改数据库绕过 API。示例只批准已有 IndustrialSim 单节点；扩点必须由部署方逐项批准真实存在的节点。

Docker 容器的 loopback 仍指容器本身。当前策略明确拒绝 host.docker.internal / 服务名 / 工业网地址，因此不会把宿主模拟器悄悄转为容器远程连接；现有 Compose 默认不开启此运行模式。完整证书运维、工业网络、长周期容量、数据库停机/进程崩溃恢复、多实例及新增实时 AI 联合验收仍开放。
