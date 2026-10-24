# IndustrialSim 接入 Docker 工作台

本配置让 8080 工作台的 North / P-101 使用独立 IndustrialSim 容器的三个点位。设备仍为仿真设备，协议为真实 OPC UA。其他点位继续使用原有来源，历史告警和读数保留。

## 启动

在 Plant Ops 仓库根目录执行：

```powershell
docker compose --env-file deploy/.env.docker -f compose.yaml -f deploy/compose.industrial-sim.yaml build industrial-sim
docker compose --env-file deploy/.env.docker -f compose.yaml -f deploy/compose.industrial-sim.yaml up -d --no-build --wait
./deploy/Connect-IndustrialSimWorkbench.ps1
```

IndustrialSim 默认位于相邻目录 `../industrial-device-simulation-framework`；可用 `INDUSTRIALSIM_ROOT` 指定另一仓库路径。复用它现有 Dockerfile 和 `examples/devices/plantops-three-point.yaml`，不启动有限测试进程。配置文件只读挂载，SQLite 使用独立命名卷。启动后持续运行，初始振动 4 mm/s、温度 60 degC、参考温度 62 degC。

工作台：<http://127.0.0.1:8080>；IndustrialSim 开发控制台：<http://127.0.0.1:8081>。控制台本机使用 Auth Disabled，不能将端口暴露给公网。

IndustrialSim 与 Worker 共享网络空间，OPC UA 仅绑定该空间的 `127.0.0.1:48481`，不发布宿主机协议端口。API 和 Worker 均配置现有 Development 运行开关、ApplicationUri、Namespace URI 和三个精确节点白名单；没有放宽现有协议策略。Worker 仍使用 8080 健康端口，模拟器 Web 服务使用 8081。

后续启动、更新和重建必须继续带上这两个 Compose 文件。Worker 被重建时，应同时重建/重启 `industrial-sim`，以更新共享的网络空间；不要只单独重建 Worker。

该工作台覆盖配置为 API/Worker 的外部域名解析指定 DNS `1.1.1.1`，可通过 `WORKBENCH_DNS_SERVER` 换为环境允许的解析服务器；Docker 内部服务名仍由内置 DNS 处理。2026-10-24 排查中，宿主机默认 DNS 与继承它的容器解析超时，公共 DNS 的独立查询通过；因此需要该容器级配置。它不修改 Windows 网络设置；企业网络应设置其批准的 DNS。DNS 故障发生在收到模型响应之前，开启流式也不能解决域名解析失败。

API/Worker 同时设置 `DOTNET_SYSTEM_NET_DISABLEIPV6=1`：当前 Docker 网络只有 IPv4 默认路由，中转域名返回的 AAAA 地址直接连接会失败。2026-10-24 的后续调查 `51f56f69-e765-4897-9898-17c73b676b61` 在收到响应头前报 `ConnectionError/NetworkUnreachable`。同容器 .NET HttpClient 首页 GET 复现同样错误，而仅检查 OpenSSL TLS 握手曾经成功；禁用 IPv6 后，.NET 域名解析仅返回 IPv4，首页及未认证 API GET 可以收到响应。此设置仅限这套工作台容器；未来网络具备 IPv6 路由时应重新验证。首页 200、未认证 API 的 401/404 仅验证传输和路由，不证明真实模型推理或流式生成成功。

更新后另以 Worker 现有配置进行只读模型目录鉴权验证：`GET /v1/models` 返回 200，当前配置模型存在；未发送模型生成 POST。验证记录为 `artifacts/investigation-quality/network-probe/worker-ipv4-verification.txt`。调查总数前后均为 17，无活动调查；这次验证不能替代真实调查输出验收。

## 点位与场景

| 工作台点位 | IndustrialSim String 节点 | 单位 |
| --- | --- | --- |
| VIBRATION_RMS | plantops-p101/vibration | mm/s |
| TEMPERATURE | plantops-p101/temperature | degC |
| REFERENCE_TEMPERATURE | plantops-p101/referenceTemperature | degC |

Namespace URI 为 `urn:industrial-sim:runtime`，ApplicationUri 为 `urn:industrial-sim:server`。采样与发布请求均为 1000 ms，队列上限 10，StatusValueTimestamp 通知过滤。请求频率不保证实际通知频率，SourceTimestamp 为协议投影/读取时间，不能解释为现场物理采样时间。

接入脚本使用本地 Docker env 文件的管理员密码，通过现有登录、CSRF、授权、版本控制及审计 API 创建来源和温度点位；仅停用目标点位的冲突映射。原配置和结果保存到新的 `artifacts/industrial-sim-workbench/<timestamp>/`。重复执行复用来源、点位和映射，不创建重复行。不改变 LLM 设置，也不创建调查。

手动执行一轮现有 YAML 场景：

```powershell
./deploy/Invoke-IndustrialSimWorkbenchScenario.ps1
```

脚本读取 `examples/scenarios/plantops-workbench.yaml`：先读取当前仿真时钟，将模板中的九个整数秒 `at` 平移到当前时钟之后（约 2–3 秒准备期），不重置设备时钟、不重建订阅。IndustrialSim 原生 `at` 是设备绝对仿真时间，不能直接在长时间运行的设备上加载原模板来模拟相对延迟。

场景起点先恢复三个初始值；随后振动 8 秒升到 12.6，20 秒降到 7，32 秒回到 4；温度 12 秒升到 75，24 秒升到 90，48 秒回到 60；参考温度保持 62。步骤完成后服务继续运行，可重新执行。该场景不自动注入断线，不自动发起模型请求。若需调查，在工作台找到本次 OPC UA 告警，按需从设置切换模型后手动发起。可传 `-EvidenceRoot <existing-directory>` 保存实际提交的绝对时间 YAML 和起始时钟，已存在的场景证据不会被覆盖。

## 回退

先通过工作台的点位映射管理停用本次三个 OPC UA 映射，按保存的 `before.json` 恢复原振动映射；新增温度点可保留并停用。然后停用 IndustrialSim 来源、停止模拟器，使用原 `compose.yaml` 重建 API/Worker。保留数据库卷和历史，勿执行 `down -v`。旧告警仍保留原来源快照，查看新数据不能改变旧调查的 Simulation 标识。

## 验证记录

2026-10-23 已部署到现有工作台，证据目录 `artifacts/industrial-sim-workbench/20261023_114842_301/`：

- 三个点位均通过授权趋势 API 读到真实 OPC UA 订阅数据；窗口内分别为 153、154、151 条，质量 Good，包含对应来源/映射、状态码和 SourceTimestamp。振动出现 4/12.6/7，温度出现 60/75/90，参考温度为 62；最终恢复到初始值。
- 告警 `40d66e95-d474-4c99-8cc0-8f3fe9021e81` 触发值 12.6，已恢复。其触发读数 `146df8fe-8f95-48d8-b8d0-37a1abf55a9b` 经数据库核对为本次来源的 OpcUa / Good / 状态码 0，见 `alarm-trigger-link.json`。
- 模拟器容器停止 13 秒，来源报告 Reconnecting / ConnectionLost；重启后自动回到 Connected，接收时间继续更新。这里验证的是模拟器进程中断与恢复，不能声称中断期间设备仿真仍在推进。重启的初次 10 秒探测仍在重连，后续观察确认恢复，见 `health-during-outage.json`、`health-after-restart.json`、`health-reconnected.json`。
- 接入脚本重复执行后，来源 ID、三个点位及映射 ID 一致，只有一个 OPC UA 来源、三个启用映射。
- Plant Ops 相关测试 40 项通过（先运行 38 项，随后配置专用测试库补跑此前跳过的 2 项）；IndustrialSim 两个 YAML 场景测试通过。Compose 配置、网络白名单、端口隔离与 PowerShell 语法检查通过。未改前端。
- 本次未调用模型，AiRuns 总数前后均为 11。旧 Simulation 告警/调查保留原来源，不应作为本次协议接入证据。

初次直接加载旧 YAML 的验证未产生预期的持续场景，已通过显式时间平移修正；首次趋势验收请求因客户端时间转换产生非 UTC 窗口被拒绝，修正为 UTC 后通过，失败记录保留在 `scenario-result-attempt1.json`。这两次检查未创建 LLM 请求。

2026-10-23 下午排查“脚本执行但不产生新告警”：16:48:25–16:48:37 振动已维持 12.6 且订阅读数正常入库，规则保持启用。数据库执行计划显示分钟汇总把待处理分钟与全部历史分区做 hash join/顺序扫描；汇总持有的维护锁阻塞采集，现场观测到锁等待约 14 秒、最新入库值落后约 16 秒。告警判断要求读数在 5000 ms 内有效，因此延迟处理的超限读数不能累计 3000 ms 触发时长。来源 Connected 和采集时的 Good 状态不能证明告警判断时仍然新鲜。

汇总的统计与 OPC UA 分钟识别两处查询已改为按测点、UTC 日期和分钟范围的 LATERAL 查询，并保留优化屏障，防止重新展开为历史全表扫描。当前数据量下只读执行计划实测约 0.67 秒，旧日期分区未执行；保留原有事务、质量检查、阈值和 5000 ms 新鲜度限制。21 项汇总/告警回归通过。更新本地 API/Worker 后重放脚本，新告警 `410247ee-6717-4740-ae4e-82d1c77938aa` 于 16:58:32.728+08:00 触发、16:58:47.725+08:00 恢复，读数延迟恢复约 1 秒。证据位于 `artifacts/investigation-quality/alarm-aggregation-replay/` 和 `aggregation-fixed-query-plan.txt`；没有补建先前漏触发的历史告警，没有调用模型。

## 调查资料与工单质量

可发布给调查检索的资料为 [P-101 仿真演示手册](INDUSTRIALSIM_P101_DEMO_MANUAL.md)，通过 `deploy/Publish-IndustrialSimManual.ps1` 使用现有授权上传、发布、检索和引用展开机制接入。它包含点位语义、实际规则、只读排查与记录要求及完成/升级条件；不包含本页的场景注入时间线或预设根因，也不构成现场操作授权。重复发布前核对当前配置与适用版本。

事件/恢复与调查当前窗口、质量定位、输出校验、工单草稿及验证边界见 [调查证据质量说明](AI_INVESTIGATION_QUALITY.md)。旧调查保持历史快照；新版本的结构需要重新调查才能生成。真实模型验收需要单独授权，Fake/记录网关与历史只读核对不能替代真实模型验收。
