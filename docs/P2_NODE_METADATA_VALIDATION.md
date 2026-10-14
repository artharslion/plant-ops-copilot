# P2.2 节点元数据独立增量验收

2026-10-08，基线 `3a0dd99`，分支 `codex/p2-opcua`。本点实现离线元数据规则与隔离会话的逐映射协议读取校验。**G0 未通过，完整 P2.2 未完成，业务 Worker 持续 OPC UA Adapter 未注册或启用。**

## 实现与边界

`OpcUaMetadataReader` 接收已创建的会话和一个明确映射，每次依当前会话 NamespaceArray 按 URI 解析；不接收 endpoint、不创建连接、不登记业务采集。先读 NodeClass，再读 ValueRank/AccessLevel/UserAccessLevel/DataType；拒绝非 Variable、非 Scalar、缺少 CurrentRead 权限与非支持类型。数值只允许 ns=0 的 SByte/Byte/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double；抽象 Number、自定义 subtype 不猜测继承关系，后续值的有限性与整数精度仍由已有标准化器校验。

仅 Translate 一个确切 `0:EngineeringUnits` / HasProperty 路径；拒绝多目标、未完成路径、远程 ExpandedNodeId、不可读或非 EUInformation 值。工程单位按 Namespace URI + UnitId 白名单核对 rawUnit，再核对已有量纲/scale/offset 白名单，不使用 DisplayName 来批准单位。支持 mm/s、m/s、degC、K。标准依据：[OPC UA Part 8 UN/CEFACT 映射](https://reference.opcfoundation.org/specs/OPC-10000-8/5.6.4.4)，mm/s UnitId 4403510 另见[泵模型单位定义](https://reference.opcfoundation.org/specs/OPC-40223/7)。

最后 Read Value 验证实际身份的访问拒绝，不把权限位当作完整读取证明；BadSensorFailure 的节点配置可以有效，但其样本仍由质量规则拒绝为可信值。返回 MappingKey/Accepted/Error，不写数据库、不返回 0 替代值，不把单点错误提升成其他映射错误。调用方提供取消/期限，服务级异常与取消向调用方传播；自检使用 15 秒元数据期限。会话地址批准、消息/操作限额、身份、Worker 权限、配置版本屏障及单位变更重新校验仍由后续生命周期负责。

原创模拟器的 100 个业务节点现在发布 EngineeringUnits 属性；额外 Metadata.* 节点用于故障验证，不参加周期更新。未复制 SDK 示例。SDK 仍为 Client/Server 1.5.378.182；spike 新引用已有 Integrations/Application/Domain 项目，锁文件仅新增项目关系，没有更换第三方包版本。

## 验证与真实协议证据

官方映射表最终核对来源为 [UA-Nodeset 固定 tag 的 CSV](https://github.com/OPCFoundation/UA-Nodeset/blob/UA-1.05.07-2026-04-15/Schema/UNECE_to_OPCUA.csv)，四行摘录保留于 `artifacts/p2/metadata-unit-identities.json`。C16=4403510、MTS=5067859、CEL=4408652、KEL=4932940。初实现 m/s 编码错误，使用官方值测试先确认 1 失败/3 通过（`p2-metadata-unit-red.trx`），再修正；不以初始通过替代官方一致性校验。

- 最小失败测试使用允许所有元数据的临时实现：22 项中 11 失败/11 通过，拒绝断言确因行为缺失失败。证据 `artifacts/p2/tests/p2-metadata-red.trx`；实现后原 22 项全过，`p2-metadata-green.trx`。最终新增 27 项，补充四个单位身份与跨 Namespace 类型/未知 UnitId 拒绝。
- 完整后端回归 **141 通过、0 失败、0 跳过**，含真实 PostgreSQL 原有回归。证据 `artifacts/p2/tests/p2-metadata-final.trx`；身份错误拆分后完整复核 `p2-metadata-accepted.trx`。官方单位编码修正后，27 项定向测试 `p2-metadata-unit-green.trx` 全过，最终完整回归 `p2-metadata-authoritative.trx` 同为 141 全过。
- 解决方案及 spike 构建 0 warning/error；两者 locked restore 通过，EF 无 pending model changes。无 UI 修改，本轮未重复前端测试，前轮记录保留。

末轮测试运行期间误启动构建复核，testhost 占用 xunit.abstractions.dll 导致 MSB3026/3027/3021 失败；未终止其他进程。等待 `p2-metadata-authoritative.trx` 成功且测试退出后顺序重建，最终两构建均 0 warning/error。失败构建不计为验收。
- 普通会话 `artifacts/p2/metadata-20261008-accepted/result.json`：NamespaceIndex=2，17 个结果全符合断言，恒值订阅 8 条/8 个不同源时间。
- 重排会话 `artifacts/p2/metadata-20261008-accepted-reordered/result.json`：NamespaceIndex=3，相同 URI 映射的 17 个结果全符合断言，恒值订阅 7 条/7 个不同源时间。每轮仅 8 秒订阅，不是持续接入或容量验收。
- 两轮先拒绝未受信服务器，再显式配对本轮两个公钥；Basic256Sha256 / SignAndEncrypt，修正发布/采样间隔 1000ms，队列 10；故障映射之后 South.Point099 仍通过，首业务节点订阅成功。

真实协议覆盖 NodeMissing、NotVariable、NotScalar、NotReadable、UserNotReadable、ValueNotReadable、UnsupportedDataType、EngineeringUnitsMissing/Unknown/Mismatch/Malformed/Unreadable、NamespaceMissing、InvalidNodeIdentity，以及两个 Good 业务节点与一个 BadSensorFailure 配置有效节点。首轮协议因模拟器 Property 的 HasProperty 引用缺失失败；修正 ReferenceTypeId 后通过，首次目录 `metadata-20261008-first` 保留，不以失败轮计作验收。所有中间成功目录也保留。

## 数据保护与后续

回归主 schema `plantops_tests.p2_metadata_20261008`，资料 `artifacts/p2/documents/metadata_20261008`；原有 helper 新增随机隔离/升级 schema 均保留。没有清理任何 schema、卷、文件或旧 Outbox。只读复核业务 plantops 仍仅 `20261003071706_InitialAssetDirectory`；原 `p17_ui_20261005` 无 SamplingProfiles。协议实验无数据库连接，自动退出，不停止其他实例。

此证据关闭本独立点的协议节点元数据校验，**不关闭 M02 的 Worker 运行集成、授权分页 Browse、逐映射业务持久状态与完整 P2.2**。采样修正快照生产者/关闭区间/版本屏障、证书/地址与 Worker-only CredentialRef、调用记录器/独立用户名账号均未完成。下一聊天优先推进已授权的小规模 OPC UA 结构行宽、WAL 与联合数据库/文件备份恢复实测，按明确行数/磁盘增长上限保存新 schema/目录；五项容量峰值和满量验收仍开放。用户尚未授权 60,480,000 行满量或大备份试验。
