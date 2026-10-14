# P2 小规模真实协议存储与 WAL 观测

后续2026-10-08：[小规模联合备份恢复](P2_SMALL_BACKUP_VALIDATION.md) 在新schema复制本轮12,000行后完成两文档三版本的磁盘备份/恢复与权限一致性，原容量accepted未改；正式峰值/满量/G0仍开放。

2026-10-08，基线 `1c3f47f`，`codex/p2-opcua`。本点关闭有限的真实 OPC UA raw 字段/索引存储实测，**G0、完整 P2.2、满量与持续采集仍未通过**。计划：[小规模边界](plans/2026-10-08-p2-small-capacity.md)。

## 执行范围与证据

使用原固定 SDK 1.5.378.182 的原创模拟器，Basic256Sha256 / SignAndEncrypt，先拒绝未受信服务器，再显式配对本轮两张证书。证据 `artifacts/p2/capacity-20261008_accepted/protocol-session.json`。100 个节点均经现有单节点元数据 Reader 核验，然后 120 轮明确 Read(maxAge=0, timestamps=Both)，经过 `OpcUaNormalizer` / `SamplePolicy.ToReading`，直接 EF 写入全新迁移后的真实分区表。来源/映射保持 Disabled；没有 Worker Adapter 注册、业务持续采集、订阅、采样修正快照、分钟覆盖率或完整业务 Ingestor 验收。

前置上限写于 `planned-limits.json`：最多 12,000 次值读取、180 秒实验期限、schema 64 MiB、集群 WAL 生成/占用增长 128 MiB、C/D 各允许消耗 256 MiB，至少各剩余 2 GiB。schema 必须全新，search path 仅允许该 `p2_capacity_*`，数据库必须 `plantops_tests`；运行目录也必须全新。每批仅 100 行，写前/后持久化探测并在阈值越界后停止；这些是采样停止阈值，不能保证其他进程在探测间不增长。无 checkpoint/switch/清理命令。

接受轮：`plantops_tests.p2_capacity_20261008_accepted`，资料根 `artifacts/p2/capacity-20261008_accepted`，总实验 125.07 秒，读取窗口 119.94 秒。不是七天持续试验。244 次探测保存在 `capacity-probes.jsonl`，前/后只读完整快照在 `before.json` / `after.json`。

| 项目 | 接受轮实测 |
|---|---:|
| 协议读取 / 实际 raw 行 | 12,000 / 12,000 |
| Good / Bad / Uncertain | 10,000 / 1,000 / 1,000 |
| 重复事件 / 全量回读差异 | 0 / 0 |
| 全部叶分区 table（含 TOAST） | 4,243,456 字节 |
| 全部叶分区 index | 3,538,944 字节 |
| raw 总量 | 7,782,400 字节 |
| table / index / total 每行 | 353.621 / 294.912 / 648.533 字节 |
| schema 总量 | 8,921,088 字节 |
| 集群 insert-LSN 生成差值 | 18,059,648 字节 |
| 采样 WAL 目录占用峰值 / 相对起点增长 | 134,217,728 / 0 字节 |
| C / D 最大采样消耗 | 154,312,704 / 0 字节 |
| 最后 C / D 可用 | 144,446,738,432 / 60,183,646,208 字节 |

table 已含 TOAST，不能再次相加。WAL 生成量与目录保留量分别记录：占用增长为 0 不代表没有生成 WAL。上述为采样观测的共享集群/宿主值，包含迁移和其他活动，不归因于单实验，不当作瞬时、并行维护或满量峰值。

`capacity-result.json` 保存全部结果与三类回读例子。逐值先核对 SDK 状态码/当前 SDK 表达，再全量回读核对原值 JSON 语义、状态码/符号、质量、数值、Source/Server/Received/Effective 时间及时间来源；时间按 PostgreSQL 微秒精度核对。BadSensorFailure 在真实 Read 中原值确为 null，保留 `{type:Null,value:null}`，没有凭模拟器内部数值捏造原值。完整状态码 2156658688 与符号 BadSensorFailure 保留，数值拒绝原因独立置于原值 JSON 的 `normalizationError`。

## 预算含义

`raw-budget.json` 以本轮 7,782,400 / 12,000 外推 100 点 1 Hz 八天的 69,120,000 raw 行：**44,826,624,000 字节，约 41.75 GiB**。这是小样本外推，索引填充/质量混合/后续字段会影响行宽，不是满量实测。未把 Simulation 分钟/Outbox 行宽合入本轮实际 raw 声称完整 OPC UA 预算；本轮未产生业务分钟或逐样本 Outbox，只产生资产初始化的一条待发送事件。

五项正式峰值仍缺，`BudgetComplete`、`FitsSnapshot`、`CapacityPassed` 仍 false。目录增长为 0 不用于填充正式 WAL 峰值的零值。独立备份/恢复、文件资料、并行维护/实时写入/五客户端、90 天分钟、七天满量、第八天滚动与阈值验收继续开放；用户尚未授权 60,480,000 行满量或大备份。

## 发现、修正与验证

首轮 `p2_capacity_20261008_trial1` / `capacity-20261008_trial1` 保留：12,000 行，raw 7,757,824 字节，原标准化器把 Bad + null 的原始状态符号替换为 UnsupportedOrInvalidNumericValue。该轮仅完成旧标准化结果的存储一致性，不作为修正后的权威结果。新增失败测试后修正：StatusSymbol 保留 SDK 协议表达，拒绝数值的诊断移至 raw JSON；旧行不重写，只有新标准化样本的诊断字段/事件键随之变化。

预算保护测试先 11 失败/1 通过（`p2-capacity-limits-red.trx`），再 12 全过（`p2-capacity-limits-green.trx`）。状态保持最小测试先失败（`p2-capacity-status-red.trx`）；初次修正后的测试使用固定符号字符串，SDK 尚未初始化符号查询时返回十六进制导致测试失败（`p2-capacity-targeted.trx`）。定向修订后 17 全过（`p2-capacity-targeted-green.trx`）。首轮完整回归又暴露符号查询在并行 Core 初始化之间切换的测试竞态，接受同码的 SDK 符号/十六进制两种合法表达，仍拒绝数值错误覆盖协议表达；失败回归 `p2-capacity-final.trx` 保留。真实协议实验在已初始化会话逐值核对 SDK 原始表达不变。

首次保护 stub 构建和首次 spike 构建因格式规则失败，修正后构建通过，不以失败轮计验收。spike 增加现有 Infrastructure 项目引用以复用迁移与实体，锁文件因此纳入该项目的现有 EF/Npgsql/Identity 依赖；SDK 版本保持固定，未升级业务项目包或引入新的业务依赖。

完整后端回归 `p2-capacity-accepted.trx` 已 **154 通过/0失败/0跳过**。解决方案/spike locked restore 成功，二者构建均 **0 warning/error**（`p2-capacity-build.log` / `p2-capacity-spike-build.log`）。EF 模型核对最初误用无 Design 引用的 API 作为启动项目，失败日志 `p2-capacity-model.log` 保留；改用 Infrastructure 设计时工厂后无 pending model changes（`p2-capacity-model-accepted.log`）。无 UI 修改，未重复前端检查。

前两轮回归虽然均使用新 schema，但资料环境变量误设为 Documents__RootPath，测试 helper 实际读取 PLANTOPS_TEST_DOCUMENT_ROOT，因此使用默认 `artifacts/test-documents`；该库用随机 Guid + CreateNew 新增，失败回滚只移除本次生成 key，不覆盖已有资料，全部成功新增文件保留。最终使用正确变量，在新增 `p2_capacity_finaltests_20261008` 与 `artifacts/p2/documents/capacity_finaltests_20261008` 独立复核；前轮 `p2_capacity_tests_20261008` / `p2_capacity_accepttests_20261008` 和 helper 随机 schema 均保留。

最终资料隔离复核 **154 通过/0失败/0跳过**，证据 `artifacts/p2/tests/p2-capacity-authoritative.trx`（2 分 3 秒），使用正确 `PLANTOPS_TEST_DOCUMENT_ROOT`。入口脚本语法检查及原预算离线回归通过。关键实测/快照/测试文件的 SHA-256 清单保存于 `capacity-20261008_accepted/evidence-manifest.json`；这些实际数据与私钥保持本地、未加入版本库。

## 复现与后续

构建/测试/实验必须顺序执行，避免 Windows 输出文件锁；本地 `.env` 凭据仅注入子进程环境，不打印连接串，证据根的私钥不提交。

```powershell
dotnet restore spikes/PlantOps.OpcUa.Spike --locked-mode
dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
./deploy/Run-SmallOpcUaCapacity.ps1 -Run <全新小写字母数字下划线后缀>
```

保留所有原 schema、卷、demo、旧 Outbox、此前元数据与容量证据；业务 plantops 不迁移，原 `p17_ui_20261005` 不加 SamplingProfiles。下一独立点在新增且保留的 schema/资料目录做小规模联合数据库/文件备份恢复，先固定文件数、备份量、运行期限和新增增长上限；继续不批准满量、不启用业务 OPC UA Adapter。

只读保护核对：业务 plantops 仍只有 `20261003071706_InitialAssetDirectory`；原 schema raw 仍为 1,609,600 行，SamplingProfiles 不存在；容量接受 schema 的 Enabled sources/mappings 均为 0，见 `capacity-20261008_accepted/protection.json`。未停止无关进程，未清理旧 Outbox。
