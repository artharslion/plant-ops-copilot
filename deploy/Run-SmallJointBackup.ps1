param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,30}$')][string]$Run)
$ErrorActionPreference = 'Stop'
$relativeRoot = "artifacts/p2/backup-$Run"
$root = Join-Path (Get-Location) $relativeRoot
if (Test-Path -LiteralPath $root) { throw 'Use a new run; all existing evidence is retained.' }
New-Item -ItemType Directory -Path $root | Out-Null
@{
    SourceSchema = "p2_backup_${Run}_source"; RestoreSchema = "p2_backup_${Run}_restore"
    ProtocolRows = 12000; Documents = 2; Files = 3; FileBytesMaximum = 1048576
    DumpBytesMaximum = 33554432; EachSchemaBytesMaximum = 67108864; DeadlineSeconds = 180
    EachDriveGrowthMaximum = 268435456; MinimumDriveFreeBytes = 2147483648
    ClusterWalGrowthMaximum = 134217728; CapacityPassed = $false
    Mode = 'Quiescent logical joint backup; independent sampled backup/restore phases'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'planned-limits.json') -Encoding utf8
. "$PSScriptRoot/Use-LocalDatabase.ps1"
$env:PLANTOPS_TEST_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;')
$env:PLANTOPS_SMALL_BACKUP_RUN = $Run
$env:PLANTOPS_SMALL_BACKUP_ROOT = $root
try {
    & "$PSScriptRoot/Measure-Capacity.ps1" -Schema p2_capacity_20261008_accepted -OutputPath "$relativeRoot/before.json"
    dotnet test tests/PlantOps.Tests --no-build --no-restore --filter FullyQualifiedName~SmallBackupRestoreTests --logger "trx;LogFileName=experiment.trx" --results-directory $root *> "$root/run.log"
    if ($LASTEXITCODE -ne 0) { throw "Small backup failed; evidence retained in $root." }
    & "$PSScriptRoot/Measure-Capacity.ps1" -Schema "p2_backup_${Run}_restore" -OutputPath "$relativeRoot/after.json"
    Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object Name -ne 'evidence-manifest.json' | ForEach-Object {
        @{ Path = [IO.Path]::GetRelativePath($root, $_.FullName); Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$root/evidence-manifest.json" -Encoding utf8
    Write-Output "Small joint backup evidence saved: $root. G0 remains unpassed."
} finally {
    Remove-Item Env:PLANTOPS_SMALL_BACKUP_RUN, Env:PLANTOPS_SMALL_BACKUP_ROOT -ErrorAction SilentlyContinue
}
