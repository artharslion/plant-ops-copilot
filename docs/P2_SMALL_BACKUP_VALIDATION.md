# P2 小规模联合数据库/文件备份恢复

2026-10-08，基线 `dbcd00a`，分支 `codex/p2-opcua`。本点完成**有明确上限的静止逻辑备份恢复前置试验**，G0、满量、并行备份及完整 P2.2 仍未通过。计划：[执行边界](plans/2026-10-08-p2-small-backup.md)。

## 接受轮与一致性

证据根 `artifacts/p2/backup-20261008_accepted`。只读 `p2_capacity_20261008_accepted`，先完整逻辑 dump 并复制到新 `plantops_tests.p2_backup_20261008_accepted_source`，核对原 12,000 行协议数据及所有逻辑表行数/哈希完全一致，然后只在新 schema 中增加三个实验账户、两厂授权和文档。使用现有上传/发布 API 创建两份原创 Markdown 文档：公开文档两版、仅 Admin 可读文档一版，共三个不可变文件键，262,216 字节。没有重新生成或覆盖已验收协议行。

停止所有本实验 API 测试宿主后取静止备份：`pg_dump --schema ... --format=p --no-owner --no-privileges`，保存 UTF-8 `database.sql` 与三个版本文件、Windows ACL。该库使用 plantops 所有者与默认 PostgreSQL ACL；已逐关系和 schema 对比所有者/ACL，不能把此命令推广为保留任意生产权限的工具。读取**磁盘上的** SQL 文件核对 SHA-256，再将 schema 标识符映射到全新 `p2_backup_20261008_accepted_restore`，从保存的 `restore.sql` 经 `psql -X -v ON_ERROR_STOP=1 --single-transaction` 恢复；三个文件使用 CreateNew 写入全新 `restored-files`。SQL 重映射只作用于 COPY 数据段以外，COPY 原始值不替换。本工具针对本仓库受控 schema，并非任意 SQL 的通用解析器。

恢复后对 **33 张逻辑基表/分区父表**逐表比较排序后的完整 `to_jsonb(row)` SHA-256与精确行数，避免父子分区重复计数；包含 raw 原值/SDK状态/时间、身份授权、文档版本/发布指针/角色权限、审计和 Outbox。索引定义、约束、关系所有者/ACL、schema 所有者/ACL均一致；两条 Identity 序列配置与 last_value/is_called 另行只读核对一致（`sequence-verification.json`）。三版元数据的 StorageKey/SizeBytes/Sha256 与源、备份、恢复目录全部对应，三目录各三文件，文件内容与 Windows owner/group/DACL 的 SDDL 一致。源实验表及原容量接受 schema 在恢复验证时再次完整比对未改。

使用恢复后的数据库/文件根启动新 API 测试宿主：原 Admin/Viewer 凭据可登录、公开两版逐字节下载正确；Admin 专属文档允许 Admin，Viewer 返回 404；仅南厂授权的 Viewer 对北厂文档下载返回 404；匿名请求 401。测试宿主 Realtime 关闭，Outbox 清理保持默认关闭。没有 Worker、持续 OPC UA Adapter 或新的采集生命周期。

证据包括 `database-backup.json`、`source-tables.json` / `restored-tables.json`、`source-files.json` / `restored-files.json`、`source-structure.json`、`probes.jsonl`、`result.json`、前后容量快照、保护/历史文件与 SHA-256 清单。备份 SQL 含本地实验身份哈希，仅保留本地，不提交原始数据/文件/凭据。

## 上限与真实采样

`planned-limits.json` 在写入前保存：12,000 协议行，两文档/三文件合计 ≤1 MiB，每份 SQL ≤32 MiB，每个新 schema ≤64 MiB，实验期限180秒；C/D 各观测新增消耗 ≤256 MiB且各余量 ≥2 GiB，共享集群 WAL 生成量/目录占用增长 ≤128 MiB。两个 schema 合计另外检查 ≤128 MiB，证据文件总量 ≤96 MiB。数据库语句 timeout30秒，实验 HTTP timeout10秒；采样越界后停止并保留证据，这些不是宿主分配硬配额或两次采样间增长保证。

运行 **17.23 秒，49 次探测**。Docker dump/restore 子进程期间每250毫秒加SQL延迟采样，文件复制前后及阶段边界也探测。其余准备/完整回读/API操作可能有更长间隔；以下是采样观测，不能称瞬时峰值或本实验独占峰值。

| 项目（字节） | 备份阶段 | 恢复阶段 |
|---|---:|---:|
| 采样数 / 阶段窗口 | 9 / 1.05秒 | 9 / 1.21秒 |
| 阶段起点已占证据文件 | 326,128 | 6,754,950 |
| 证据文件采样最大总量 | 6,754,779 | 13,183,950 |
| 相对阶段起点新增证据文件 | 6,428,651 | 6,429,000 |
| C盘相对阶段起点最大观测消耗 | 65,536 | 73,728 |
| D盘相对阶段起点最大观测消耗 | 6,443,008 | 6,443,008 |
| 集群 WAL 生成差值 | 0 | 7,390,832 |
| 集群 WAL 占用采样最大量 | 134,217,728 | 134,217,728 |
| WAL 占用相对阶段起点增长 | 0 | 0 |
| 阶段结束实验 schema 合计 | 8,478,720 | 16,957,440 |

实际数据库备份 **6,164,950** 字节，加三版文件 **262,216** 字节，共 **6,427,166** 字节；阶段新增文件观测还包含清单和探测记录。恢复工作 SQL 另占6,165,393字节，恢复文件另占262,216字节；保留所有备份/工作副本，不删除以降低观测峰值。恢复两个 schema 各8,478,720字节，索引重建后物理大小与原8,921,088字节可不同，完整数据/结构一致性已核对。

运行前所有数据库历史 **2,022,069,139** 字节；包含旧失败轮/既有试验，既占空间不可当本轮新增。C/D历史余量148,695,654,400 / 60,168,679,424字节。全实验C/D最大采样消耗3,141,632 / 13,238,272字节，WAL生成14,914,408字节；结束C/D余量148,692,512,768 / 60,155,428,864字节。全实验包括克隆/账户/资料准备，不只备份恢复。目录增长0不表示无WAL生成；备份短窗口生成差值0仅描述该窗口，禁止填成正式峰值零值。

所有阈值通过。共享宿主/VHD实际分配和其他活动可能影响余量；不把文件逻辑长度、容器虚拟余量与宿主实际消耗互相替代。本轮没有实时写入/维护/五客户端并发，没有满量或备份失败中断恢复验收。五项正式峰值仍缺，`BudgetComplete`、`FitsSnapshot`、`CapacityPassed`继续 false；小规模成功不能填充满量预算的备份/恢复峰值。

## 测试、失败证据与保护

测试先写，首次缺实现构建失败（`p2-backup-guards-red.log`，没有伪造 red TRX）；首实现格式与 Windows ACL平台检查失败日志保留。修正后原10个保护用例通过，补入 payload及截断COPY检查后 **15通过/0失败/0跳过**（`p2-backup-guards-accepted.trx`）。实际接受轮 **1通过/0失败/0跳过**（`backup-20261008_accepted/experiment.trx`）。

`backup-20261008_trial1` 在容量工具收到绝对路径时拒绝目录写入，无新schema；修改入口传相对快照路径后继续，原证据保留。`trial2` 克隆后调用旧测试账户初始化，意外生成十份demo版本及默认Simulation配置，三版文件断言失败。全部13版资料/raw/失败证据保留，不当作接受轮。发现后仅将本轮新 `p2_backup_20261008_trial2_source` 中默认启用的2个Simulation源/100个映射禁用，前后配置及更新日志在该轮目录；没有启动Worker、没有启用OPC UA源，也没有修改旧schema。`trial3` 仅创建账户时因容量fixture没有固定北厂实体导致外键失败；后续在新schema仅补两厂实体，避免调用demo初始化。`trial4` 33表/文件/访问校验成功（17秒），但当时SQL恢复来自内存字符串；后续改为磁盘备份读取+哈希核对，使用全新accepted轮验证，不把trial4当权威磁盘恢复证据。

解决方案locked restore成功，build **0 warning/error**。完整常规后端回归在新增 `p2_backup_finaltests_20261008` 与正确 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/backup_finaltests_20261008` 下 **169通过/0失败/0跳过**（`p2-backup-regression.trx`，1分58秒）。显式排除须全新run的备份实验，避免重用/复制备份；该实验独立1项已执行通过，因此本轮合计170项实际通过。未改业务模型/UI/依赖，未重复前端或EF迁移验收。

只读保护：业务plantops仍只有InitialAssetDirectory；原p17_ui_20261005仍1,609,600行且无SamplingProfiles；原容量accepted仍12,000行；本轮接受源/恢复源与恢复映射Enabled均0。所有新旧schema/卷/资料/Outbox/失败证据保留，无push/PR。

## 复现与后续

```powershell
dotnet restore PlantOps.sln --locked-mode
dotnet build PlantOps.sln --no-restore
./deploy/Run-SmallJointBackup.ps1 -Run <全新小写后缀>
```

本工具固定只读容量accepted作为受控源，必须存在；入口拒绝已有run目录，实验拒绝既有目标schema。只在Windows/Docker本地运行，不是生产备份部署。构建/测试/实验顺序执行，禁止并行写共享工作区。

下一独立点优先评审小样本预算与正式峰值缺口，制定更大规模试验所需授权/上限；也可推进G0前允许的纯离线P2.2采样修正契约测试。仍不得运行60,480,000行满量或大备份，禁止业务持续Adapter注册/启用；Worker授权分页Browse、逐映射业务状态持久化及真实修正参数生命周期仍缺。
