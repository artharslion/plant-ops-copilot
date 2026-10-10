param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run,
    [string]$SimulatorRoot = 'D:/Repos/industrial-device-simulation-framework',
    [switch]$RealAi
)
$ErrorActionPreference = 'Stop'
if ($RealAi) {
    # Preflight without sending any request or printing private settings.
    try { $privateSettings = Get-Content "$PSScriptRoot/../appsettings.Ai.Local.json" -Raw | ConvertFrom-Json } catch { throw 'Cannot read private AI configuration.' }
    if ($privateSettings.AI.Provider -ne 'OpenAiCompatible' -or [string]::IsNullOrWhiteSpace($privateSettings.AI.ApiKey)) { throw 'Configure the real provider before collecting protocol data.' }
    Remove-Variable privateSettings
}
$volumeReport = docker compose -f "$PSScriptRoot/compose.yaml" exec -T database df -Pk /var/lib/postgresql/data
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify database volume reserve.' }
$volumeFreeBytes = [long](($volumeReport[-1].Trim() -split '\s+')[3]) * 1024
if ($volumeFreeBytes -lt 2GB) { throw 'Database volume needs at least 2 GiB reserve.' }
# No fixture alarms: the only alarm is produced by the finite OPC UA ingestion run.
& "$PSScriptRoot/Run-IndustrialSimDemo.ps1" -Run $Run -SimulatorRoot $SimulatorRoot
@{ InitialDatabaseVolumeFreeBytes = $volumeFreeBytes; MinimumFreeBytes = 2GB; Note = 'Host disks are monitored during collection; container volume checked before collection.' } | ConvertTo-Json | Set-Content "$PSScriptRoot/../artifacts/opcua-demo/$Run/database-disk.json"
dotnet build "$PSScriptRoot/../src/PlantOps.Api" --nologo --artifacts-path "$PSScriptRoot/../artifacts/opcua-demo-launch/$Run/build"
if ($LASTEXITCODE -ne 0) { throw 'API build failed; protocol evidence retained.' }
& "$PSScriptRoot/Test-IndustrialSimDemo.ps1" -Run $Run -Investigate -RealAi:$RealAi
