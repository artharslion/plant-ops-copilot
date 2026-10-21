[CmdletBinding()]
param([string]$Run = ('run_' + (Get-Date -Format 'yyyyMMdd_HHmmss')), [switch]$MinimalScenario)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Run -cnotmatch '^[a-z0-9_]{1,40}$') { throw 'Use a new lowercase run name.' }
$resultRoot = Join-Path $repoRoot "artifacts/opcua-runtime/$Run"
if (Test-Path -LiteralPath $resultRoot) { throw 'Run already exists; all retained evidence is preserved.' }
if (@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq 48480).Count) {
    throw 'Port 48480 is occupied; existing processes are preserved.'
}
$testConfigPath = Join-Path $repoRoot 'artifacts/p4/database.local.json'
if (-not (Test-Path -LiteralPath $testConfigPath)) { throw 'Dedicated local test database configuration is required.' }
$testConfig = Get-Content -LiteralPath $testConfigPath | ConvertFrom-Json
$savedConnection = $env:PLANTOPS_RUNTIME_CONNECTION
$savedLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    # This private configuration holds only the dedicated test container credentials.
    $env:PLANTOPS_RUNTIME_CONNECTION = "Host=127.0.0.1;Port=$($testConfig.Port);Database=plantops_tests;Username=plantops;Password=$($testConfig.Password);Pooling=false;Search Path=opcua_runtime_$Run"
    & dotnet restore spikes/PlantOps.OpcUa.Spike --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $mode = if ($MinimalScenario) { 'scenario' } else { 'runtime' }
    & dotnet spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0/PlantOps.OpcUa.Spike.dll $mode $resultRoot
    if ($LASTEXITCODE -ne 0) { throw "Runtime acceptance failed; retain $resultRoot." }
} finally {
    $env:PLANTOPS_RUNTIME_CONNECTION = $savedConnection
    Set-Location -LiteralPath $savedLocation
}
