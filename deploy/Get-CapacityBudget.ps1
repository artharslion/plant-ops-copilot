param(
    [Parameter(Mandatory)][string]$SnapshotPath,
    [string]$OutputPath = 'artifacts/p2/capacity-budget.json',
    [ValidateRange(1, 100)][int]$Points = 100,
    [ValidateRange(1, 86400)][int]$SentOutboxEventsPerSecond = 100,
    [Nullable[long]]$WalPeakAdditionalBytes,
    [Nullable[long]]$MaintenanceAdditionalBytes,
    [Nullable[long]]$RestoreAdditionalBytes,
    [Nullable[long]]$DocumentAdditionalBytes,
    [Nullable[long]]$BackupAdditionalBytes,
    [string]$BackupDrive = 'D'
)
$ErrorActionPreference = 'Stop'
$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
$db = $snapshot.Database
foreach ($name in @('rawRows', 'rawBytes', 'minuteRows', 'minuteBytes', 'outboxBytes', 'allDatabaseBytes')) {
    if ($null -eq $db.$name -or $db.$name -le 0) { throw "Snapshot needs a positive measured $name; estimated rows are insufficient." }
}
$outboxRows = [double]$db.pendingOutbox + [double]$db.sentOutbox
if ($outboxRows -le 0) { throw 'Outbox row width cannot be measured from an empty table.' }
$databaseDrive = $snapshot.DefaultDockerVhd.HostDrive
if ([string]::IsNullOrWhiteSpace($databaseDrive)) { throw 'Database host drive is unknown; do not substitute container free space.' }
$missing = @()
foreach ($name in @('WalPeakAdditionalBytes', 'MaintenanceAdditionalBytes', 'RestoreAdditionalBytes', 'DocumentAdditionalBytes', 'BackupAdditionalBytes')) {
    $value = Get-Variable -Name $name -ValueOnly
    if ($null -eq $value) { $missing += $name }
    elseif ($value -lt 0) { throw "$name must be nonnegative." }
}
$rawRowBytes = [double]$db.rawBytes / [double]$db.rawRows
$minuteRowBytes = [double]$db.minuteBytes / [double]$db.minuteRows
$outboxRowBytes = [double]$db.outboxBytes / $outboxRows
# New acceptance history is retained alongside every existing database/schema.
# Existing bytes are already absent from host FreeBytes; count them only in reserve.
$raw = [math]::Ceiling($rawRowBytes * $Points * 86400 * 8)
$minutes = [math]::Ceiling($minuteRowBytes * $Points * 1440 * 90)
$outbox = [math]::Ceiling($outboxRowBytes * $SentOutboxEventsPerSecond * 86400)
$databaseAdditional = $raw + $minutes + $outbox + [double]$WalPeakAdditionalBytes + [double]$MaintenanceAdditionalBytes + [double]$RestoreAdditionalBytes + [double]$DocumentAdditionalBytes
$allocations = @{}
$allocations[$databaseDrive] = $databaseAdditional
if (-not $allocations.ContainsKey($BackupDrive)) { $allocations[$BackupDrive] = 0.0 }
$allocations[$BackupDrive] += [double]$BackupAdditionalBytes
$drives = @($allocations.Keys | Sort-Object | ForEach-Object {
    $drive = $_
    $hostDisk = @($snapshot.HostDisks | Where-Object { $_.Name -eq $drive })
    if ($hostDisk.Count -ne 1) { throw "Host disk $drive is missing or ambiguous." }
    $existing = if ($drive -eq $databaseDrive) { [double]$db.allDatabaseBytes } else { 0.0 }
    $reserve = [math]::Ceiling(($existing + $allocations[$drive]) * 0.30)
    [pscustomobject]@{
        Drive = $drive; MeasuredFreeBytes = $hostDisk[0].FreeBytes
        AdditionalAllocationBytes = $allocations[$drive]; ReserveBytes = $reserve
        RequiredFreeBytes = $allocations[$drive] + $reserve
        FitsSnapshot = ($hostDisk[0].FreeBytes -ge $allocations[$drive] + $reserve)
    }
})
$report = [pscustomobject]@{
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); SnapshotAtUtc = $snapshot.CheckedAtUtc
    SourceSchema = $db.schema; Points = $Points
    MeasuredBytesPerRow = @{ Raw = $rawRowBytes; Minute = $minuteRowBytes; Outbox = $outboxRowBytes }
    ForecastBytes = @{ EightDayRaw = $raw; NinetyDayMinutes = $minutes; OneDaySentOutbox = $outbox }
    PeakInputsBytes = @{ Wal = $WalPeakAdditionalBytes; Maintenance = $MaintenanceAdditionalBytes; Restore = $RestoreAdditionalBytes; Documents = $DocumentAdditionalBytes; Backup = $BackupAdditionalBytes }
    MissingPeakInputs = $missing; Drives = $drives
    BudgetComplete = ($missing.Count -eq 0)
    FitsSnapshot = ($missing.Count -eq 0 -and @($drives | Where-Object { -not $_.FitsSnapshot }).Count -eq 0)
    CapacityPassed = $false
    Notes = @('Extrapolation from source schema, not OPC UA row-width or full-load evidence.', 'Snapshot free space may be stale; take a fresh measurement before any run.', 'Peak inputs must include concurrent gap/audit/file growth; zeros require measured justification.', 'Outbox retention is a forecast only; cleanup remains disabled and existing history is retained.', 'Full-load, rolling retention, concurrent latency and backup/restore validation remain required.')
}
$target = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
$repo = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd('\') + '\'
if (-not $target.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep budget output in this repository.' }
New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding utf8
Write-Output "Capacity forecast saved: $target (G0 remains unpassed)"
