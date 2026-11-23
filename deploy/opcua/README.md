# 隔离 OPC UA 实验

[常驻采集](../../docs/OPCUA_INTEGRATION.md)通过普通 Worker 接入，要求明确开关和精确节点白名单。`local-runtime.example.json` 需由部署方显式加载。下文 `selftest` 和 `server` 使用加密连接，常驻采集实验使用 Development 回环地址和 `None`。

应用预配置示例见 `appsettings.example.json`（需由部署端加载），停用配置可使用 NamespaceUri + String IdentifierPrefix 或 Nodes 精确 `Type:Identifier` 列表。启用常驻采集另须满足 `local-runtime.example.json` 所示的显式 Development 回环策略及精确节点列表；通用远程或前缀范围不能直接启用。当前没有工业凭据解析、浏览命令或证书运维入口；当前任务归入 [开发计划 P5](../../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座)。

固定 SDK Client/Server **1.5.378.182**，源码 tag 对应 commit `6cca32dd6d79c8b57d2e00e4d2071b991d2cf71b`；.NET SDK 10.0.112 / net10.0。使用官方 Server 库及原创 NodeManager，不复制示例项目、不使用第三方镜像。版本与内容哈希见 spike 的 `packages.lock.json`，许可与通知随此目录保存。

```powershell
dotnet restore spikes/PlantOps.OpcUa.Spike --locked-mode
dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
dotnet run --project spikes/PlantOps.OpcUa.Spike --no-build -- selftest artifacts/p2/my_unique_run
```

每次自检必须选新的独立目录。通过元数据检查后订阅 8 秒并自动退出，结果在 `result.json`。不连接 PostgreSQL。端口 **48480**，endpoint `opc.tcp://localhost:48480/plantops`，使用 Basic256Sha256 / SignAndEncrypt。自检先拒绝未受信服务端，然后将本次创建、指纹可核对的两个公钥加入对端信任目录。私钥保存在 Git 忽略的实验目录。

```powershell
dotnet run --project spikes/PlantOps.OpcUa.Spike --no-build -- server artifacts/p2/server_unique deploy/opcua/simulator.example.json
```

独立 server 模式持续运行，需自行结束本次进程。客户端证书需预先放入该 server 的 trusted/certs，server 公钥需明确批准；无自动接受回调。匿名身份只读，变量的 AccessLevel/UserAccessLevel 均为 CurrentRead，未注册任何业务 Method。无工业凭据。

100 个业务节点，Namespace URI `urn:plant-ops:simulator`。首节点 `North.Utilities.P101.VibrationRms`；其他为 `North.Point001.VibrationRms` 至 `North.Point049.VibrationRms` 和 `South.Point050.VibrationRms` 至 `South.Point099.VibrationRms`。全部 Double / Scalar，已发布 `0:EngineeringUnits` HasProperty / EUInformation（UN/CEFACT Namespace URI、C16 UnitId 4403510、mm/s）。

额外 `Metadata.*` 原创静态节点用于对象/数组/读权限/字符串/单位缺失、未知、冲突、畸形和读取拒绝实验，`Metadata.Missing` 故意不存在。默认自检逐映射校验 17 个结果，包括 Namespace 缺失/非法标识符；错误后有效映射仍通过。元数据读取仅针对确切节点/属性，不是授权 Worker Browse 或持续源；历史实验记录可从 Git 历史查阅。自检元数据阶段最长 15 秒，随后订阅 8 秒；应使用默认参数或仅 ReorderNamespaces 复现成功自检，其他故障配置可能故意使订阅断言失败。

示例配置为确定性 120 秒周期：0–9 秒 4.0，10–29 秒 12.6，其余 7.0；40–49 秒 Bad，50–59 秒 Uncertain；默认自检用恒值 4.0，每秒更新源时间。可设 `Constant`、`Quality`（Good/Bad/Uncertain/Cycle）、`Timestamp`（Normal/Missing/Future/Frozen）、`Overflow`、`DenyRead`、`MinimumSamplingMs` 和 `ReorderNamespaces`。重排将业务 Namespace 从 2 移至 3，客户端每次新会话按 URI 解析。Missing/Frozen 故障可能使恒值订阅静默；不应把通知数量等同物理采样。Overflow 注入保留 DataValue 信息位，真实队列溢出另行验收。

隔离自检覆盖发现、会话、有限浏览、节点元数据、读取、订阅和关闭。本实验的只读边界来自匿名身份和模拟节点的读取权限。生产凭据管理及外部受信加密接入属于 [P5.5](../../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座)。

再分发应一并提供 `licenses/` 中版权、许可和第三方通知。所有新增运行依赖为 MIT；SDK、Server 分别核实，原始 NuGet 元数据清单见 `package-licenses.json`。SDK 示例未复制，容器镜像未使用，所以无对应分发产物。
