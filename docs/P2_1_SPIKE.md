# P2.1 SDK、许可与隔离协议实验

2026-10-06。基线 `9dcc65a`，分支 `codex/p2-opcua`。G1 的固定包/许可和最小安全连接通过；P2 整体验收、持续应用接入与容量 G0 **未通过**。

## 固定产物及许可

[官方稳定 Client 版本](https://www.nuget.org/packages/OPCFoundation.NetStandard.Opc.Ua.Client/1.5.378.182)、Server 及 Configuration/Core/Types/Security.Certificates 均 1.5.378.182；包元数据中的 repository commit 与 [固定 tag](https://github.com/OPCFoundation/UA-.NETStandard/tree/1.5.378.182) 一致：`6cca32dd6d79c8b57d2e00e4d2071b991d2cf71b`。没有以 master 或预发布版编程。所有传递依赖和内容哈希锁在 [packages.lock.json](../spikes/PlantOps.OpcUa.Spike/packages.lock.json)。

SDK 与 Server 各自包内 LICENSE 为 OPC Foundation MIT 1.00，与 [不可变源码 LICENSE](https://github.com/OPCFoundation/UA-.NETStandard/blob/6cca32dd6d79c8b57d2e00e4d2071b991d2cf71b/LICENSE.txt) 一致。允许使用、修改、分发，分发时保留版权和许可通知；不包含任何认证声明。原创模拟器只继承官方 Server 类型，没有复制官方业务节点代码。没有使用 UA-.NETStandard-Samples、专有系统或容器镜像。

| 包族 | 固定版本 | 许可 / 保留通知 |
|---|---|---|
| OPCFoundation Client/Server/Configuration/Core/Types/Security.Certificates | 1.5.378.182 | MIT 1.00，分别保存包内 LICENSE |
| BitFaster.Caching | 2.6.0，commit 43a8822785038dd90f8376b202243ad28c53f845 | MIT，Alex Peck 2020 |
| Microsoft.Extensions DI/DI.Abstractions/Logging/Logging.Abstractions/Options/Primitives | 10.0.8，commit 94ea82652cdd4e0f8046b5bd5becbd11461482ca | MIT，包内第三方通知保留 |
| Newtonsoft.Json | 13.0.4，commit 4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a | MIT，包内 LICENSE |
| StyleCop.Analyzers / Unstable（开发构建） | 1.2.0-beta.556 / 1.2.0.556 | MIT，包内 LICENSE/第三方通知 |

完整 package 元数据与分发通知见 [运行指南](../deploy/opcua/README.md) 和 `deploy/opcua/licenses/`。没有未知运行依赖许可。

## 真实实验

Windows / .NET SDK 10.0.112 / net10.0，本机端口 48480，独立证书目录；未访问数据库。每次从新目录构造 Client/Server 证书。`dotnet restore --locked-mode`、构建与真实协议自检命令见运行指南。

- `artifacts/p2/spike_run03/result.json`：未受信拒绝=true；Basic256Sha256/SignAndEncrypt；Browse 返回 50 且 continuation 显式释放；Good Read；8 秒收到 7 个恒值、不同源时间通知；修正 publishing/sampling 1000 ms、queue 10。
- `artifacts/p2/spike_reorder01/result.json`：额外 Namespace 使业务 index 2→3；同 URI/identifier 正确读取和订阅，收到 7 个不同源时间通知。各自证书指纹保存在结果文件；私钥不入库。
- 自检进程均自行退出。原数据库、既有 schema、演示进程、卷及资料未修改。

## 固定 API 与能力边界

| 能力 | 锁定版 API / 结论 |
|---|---|
| 创建安全会话 | `CoreClientUtils.SelectEndpointAsync` + `DefaultSessionFactory(ITelemetryContext).CreateAsync`；checkDomain=true，禁降级；SDK 会话校验 server ApplicationUri |
| 证书 | `ApplicationInstance.Build`、CertificateIdentifierCollection、`CheckApplicationInstanceCertificatesAsync`；Directory trust/rejected；AutoAccept=false |
| 读与浏览 | `ISession.ReadValueAsync`、`BrowseAsync`、`BrowseNextAsync`；后两者显式 CancellationToken |
| 订阅 | `Subscription.CreateAsync`、MonitoredItem.Notification / DequeueValues；StatusValueTimestamp 支持且恒值源时间更新实测通过 |
| 恢复 | ISession 提供 ReconnectAsync / RepublishAsync / TransferSubscriptionsAsync，SessionReconnectHandler 可用；本次未执行恢复服务，服务器支持/失效分支仍须 P2.4 真实故障验证 |
| 关闭 | Subscription.DeleteAsync、ISession.CloseAsync、StandardServer.StopAsync，资源 Dispose |
| 无通知点 | 尚未实现应用受限健康 Read，不能判定周期采样完整 |

故障开关已实现，正常与 Namespace 重排已实测。坏质量、权限拒绝、缺/未来时间、Overflow 与修正间隔须在标准化/生命周期阶段逐项验证。只读变量和客户端无 Write/Call 是静态边界；服务调用记录器与账号拒绝仍开放。
