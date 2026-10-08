param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run
)
$ErrorActionPreference = 'Stop'
$schema = "p2_capacity_$Run"
$root = "artifacts/p2/capacity-$Run"
if (Test-Path -LiteralPath $root) { throw 'Use a new run name; existing evidence must be retained.' }
New-Item -ItemType Directory -Path $root | Out-Null
# Read local credentials into process environment only; never output the connection.
. "$PSScriptRoot/Use-LocalDatabase.ps1"
$env:PLANTOPS_CAPACITY_SCHEMA = $schema
$env:PLANTOPS_CAPACITY_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;') + ";Search Path=$schema"
$options = Join-Path $root 'simulator.json'
@{ Constant = $false; Quality = 'Cycle'; Timestamp = 'Normal' } | ConvertTo-Json | Set-Content -LiteralPath $options -Encoding utf8
@{
    Schema = $schema; ProtocolReadsMaximum = 12000; Points = 100; Rounds = 120; DeadlineSeconds = 180
    SchemaLimitBytes = 67108864; ClusterWalLimitBytes = 134217728; EachHostDriveGrowthLimitBytes = 268435456
    MinimumHostFreeBytes = 2147483648; CapacityPassed = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'planned-limits.json') -Encoding utf8
try {
    & "$PSScriptRoot/Measure-Capacity.ps1" -OutputPath "$root/before.json"
    dotnet run --project spikes/PlantOps.OpcUa.Spike --no-build -- capacity $root $options *> "$root/run.log"
    if ($LASTEXITCODE -ne 0) { throw "Finite capacity experiment failed; preserved evidence in $root." }
    & "$PSScriptRoot/Measure-Capacity.ps1" -Schema $schema -OutputPath "$root/after.json"
    Write-Output "Small-scale evidence saved: $root. G0 remains unpassed."
} finally {
    Remove-Item Env:PLANTOPS_CAPACITY_CONNECTION -ErrorAction SilentlyContinue
    Remove-Item Env:PLANTOPS_CAPACITY_SCHEMA -ErrorAction SilentlyContinue
}
