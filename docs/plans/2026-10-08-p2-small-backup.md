# P2 Small Joint Backup Implementation Plan

**Goal:** Observe a bounded joint database/file backup and restore with real protocol rows and document versions; G0 stays open.

**Architecture:** An opt-in integration experiment clones the accepted schema read-only into a fresh p2_backup_* schema, seeds documents through the existing API, freezes writes, dumps that schema and copies its three immutable files, then restores into a second fresh schema/root. SQL remapping changes schema identifiers outside COPY payload only. All old and new data/evidence are retained. This is a quiescent logical backup, not a concurrent production backup procedure.

**Tech Stack:** Existing .NET test host, Npgsql, Docker PostgreSQL pg_dump/psql, Windows local file storage.

## Single acceptance point

1. Test remapping preserves COPY content and rejects unsafe/equal/non-p2 identifiers; test stopping boundaries before implementing the helper.
2. Require a new run/root and two absent schemas. Preflight accepted source exactly 12,000 rows, no enabled sources/mappings, no documents. Maximum three files/1 MiB, SQL dump 32 MiB, each schema 64 MiB, run 180 seconds, C/D each 256 MiB observed growth and 2 GiB minimum free; WAL generated/occupancy growth 128 MiB. Preserve failure evidence. No cleanup, checkpoint or business migration.
3. Clone accepted protocol data, upload/publish two public versions and one Admin restricted version through API. Close seed hosts before the frozen backup. Sample backup and restore separately (including boundary probes), keeping baseline/history distinct from incremental host and artifact bytes. Poll every 250 ms plus SQL latency, not an instantaneous peak guarantee.
4. Verify every logical parent/base table with sorted canonical JSON SHA-256/count; compare constraints/index definitions, ownership and PostgreSQL ACLs; verify three version keys, hashes, sizes and Windows file ACLs. Restore all files using CreateNew. Recheck public downloads, restricted-role denial, foreign plant denial and anonymous denial with restored API hosts. Keep the original schema untouched.
5. Run targeted and appropriate sequential regression/build checks with fresh schema/document roots, record real measurements and limitations, commit locally. Create/open next project chat and confirm it started, then stop writing this workspace. No push/PR or continuous Adapter.

Files: tests/PlantOps.Tests/SmallBackupRestoreTests.cs, tests/PlantOps.Tests/SmallBackupExperiment.cs, deploy/Run-SmallJointBackup.ps1, docs/P2_SMALL_BACKUP_VALIDATION.md, capacity and P2 plan status.
