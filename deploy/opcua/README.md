# 隔离 OPC UA 实验

固定 SDK Client/Server **1.5.378.182**，源码 tag 对应 commit `6cca32dd6d79c8b57d2e00e4d2071b991d2cf71b`；.NET SDK 10.0.112 / net10.0。使用官方 Server 库及原创 NodeManager，不复制示例项目、不使用第三方镜像。版本与内容哈希见 spike 的 `packages.lock.json`，许可与通知随此目录保存。

```powershell
dotnet restore spikes/PlantOps.OpcUa.Spike --locked-mode
dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
dotnet run --project spikes/PlantOps.OpcUa.Spike --no-build -- selftest artifacts/p2/my_unique_run
```

每次自检必须选新的独立目录；8 秒后自动退出，结果在 `result.json`。不连接任何 PostgreSQL。端口 **48480**，endpoint `opc.tcp://localhost:48480/plantops`；只支持 Basic256Sha256 / SignAndEncrypt，无 None。自检先拒绝未受信服务端，然后将本次创建、指纹可核对的两个公钥加入对端信任目录；这仅为隔离实验的显式配对，不是应用证书审批流程。私钥在忽略的实验目录，不复制到版本库。

```powershell
dotnet run --project spikes/PlantOps.OpcUa.Spike --no-build -- server artifacts/p2/server_unique deploy/opcua/simulator.example.json
```

独立 server 模式持续运行，需自行结束本次进程。客户端证书需预先放入该 server 的 trusted/certs，server 公钥需明确批准；无自动接受回调。匿名身份只读，变量的 AccessLevel/UserAccessLevel 均为 CurrentRead，未注册任何业务 Method。无工业凭据。

100 个节点，Namespace URI `urn:plant-ops:simulator`。首节点 `North.Utilities.P101.VibrationRms`；其他为 `North.Point001.VibrationRms` 至 `North.Point049.VibrationRms` 和 `South.Point050.VibrationRms` 至 `South.Point099.VibrationRms`。全部 Double / Scalar，实验约定 mm/s（当前节点尚未发布 EngineeringUnits 元数据，应用不能据此自动批准单位）。

示例配置为确定性 120 秒周期：0–9 秒 4.0，10–29 秒 12.6，其余 7.0；40–49 秒 Bad，50–59 秒 Uncertain；默认自检用恒值 4.0，每秒更新源时间。可设 `Constant`、`Quality`（Good/Bad/Uncertain/Cycle）、`Timestamp`（Normal/Missing/Future/Frozen）、`Overflow`、`DenyRead`、`MinimumSamplingMs` 和 `ReorderNamespaces`。重排将业务 Namespace 从 2 移至 3，客户端每次新会话按 URI 解析。Missing/Frozen 故障可能使恒值订阅静默；不应把通知数量等同物理采样。Overflow 注入保留 DataValue 信息位，真实队列溢出另行验收。

客户端业务服务调用仅 GetEndpoints、Session、Browse/BrowseNext（50 条且释放 continuation）、Read、Subscription/MonitoredItem 与 Publish/关闭；不提供 Write/Call/HistoryRead。运行服务拦截记录及独立只读用户名账号尚待 P2.3 验收，不能仅凭变量声明称所有权限实验通过。

再分发应一并提供 `licenses/` 中版权、许可和第三方通知。所有新增运行依赖为 MIT；SDK、Server 分别核实，原始 NuGet 元数据清单见 `package-licenses.json`。SDK 示例未复制，容器镜像未使用，所以无对应分发产物。
