param(
    [string]$Schema = 'p17_ui_20261005',
    [string]$OutputPath = 'artifacts/reviewfix/capacity.json'
)
$ErrorActionPreference = 'Stop'
if ($Schema -notmatch '^[a-z][a-z0-9_]{0,62}$') { throw 'Use a safe test schema name.' }
# Read-only. No credentials/connection strings are read or printed; no migrations or cleanup.
$mountsText = docker inspect plantops-database-1 --format '{{json .Mounts}}'
if ($LASTEXITCODE -ne 0) { throw 'Database container is unavailable.' }
$mounts = $mountsText | ConvertFrom-Json
$disk = docker exec plantops-database-1 df -B1 /var/lib/postgresql/data
if ($LASTEXITCODE -ne 0) { throw 'Container disk probe failed.' }
$sql = @"
SELECT json_build_object(
  'database', current_database(), 'databaseBytes', pg_database_size(current_database()),
  'schema', '$Schema',
  'schemaBytes', (SELECT coalesce(sum(pg_total_relation_size(c.oid)),0) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='$Schema' AND c.relkind IN ('r','m')),
  'relations', (SELECT json_agg(row_to_json(r)) FROM (SELECT c.relname AS name, c.reltuples::bigint AS estimated_rows, pg_total_relation_size(c.oid) AS total_bytes FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='$Schema' AND c.relkind IN ('r','m') ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 30) r),
  'walBytes', (SELECT coalesce(sum(size),0) FROM pg_ls_waldir()),
  'pendingOutbox', (SELECT count(*) FROM "$Schema"."OutboxEvents" WHERE "DispatchedAt" IS NULL),
  'sentOutbox', (SELECT count(*) FROM "$Schema"."OutboxEvents" WHERE "DispatchedAt" IS NOT NULL),
  'dirtyMinutes', (SELECT count(*) FROM "$Schema"."DirtyMeasurementMinutes"),
  'openGaps', (SELECT count(*) FROM "$Schema"."IngestionGaps" WHERE "To" IS NULL)
);
"@
$raw = $sql | docker exec -i plantops-database-1 psql -U plantops -d plantops_tests -X -A -t -v ON_ERROR_STOP=1
if ($LASTEXITCODE -ne 0) { throw 'Test schema capacity query failed.' }
$hostDisks = @(Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name.Length -eq 1 } | ForEach-Object {
    [pscustomobject]@{ Name=$_.Name; FreeBytes=$_.Free; UsedBytes=$_.Used }
})
$vhd = Join-Path $env:LOCALAPPDATA 'Docker/wsl/disk/docker_data.vhdx'
$vhdInfo = if (Test-Path -LiteralPath $vhd) { $file = Get-Item -LiteralPath $vhd; [pscustomobject]@{ Path=$file.FullName; FileLengthBytes=$file.Length; HostDrive=$file.PSDrive.Name } } else { $null }
$result = [pscustomobject]@{
    CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); Mounts=$mounts; ContainerDisk=$disk;
    HostDisks=$hostDisks; DefaultDockerVhd=$vhdInfo; Database=($raw | ConvertFrom-Json);
    CapacityPassed=$false; Note='Virtual disk available bytes are not host free bytes. Full seven-day, rolling boundary, WAL/backup peak and concurrent maintenance validation are still required.'
}
$target = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
$repo = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd('\') + '\'
if (-not $target.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep capacity output in this repository.' }
New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding utf8
Write-Output "Read-only capacity snapshot saved: $target"
