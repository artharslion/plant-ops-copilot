[CmdletBinding()]
param([string]$Run = ('run_' + (Get-Date -Format 'yyyyMMdd_HHmmss')), [switch]$MinimalScenario,
    [string]$IndustrialSimRoot)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Run -cnotmatch '^[a-z0-9_]{1,40}$') { throw 'Use a new lowercase run name.' }
$resultRoot = Join-Path $repoRoot "artifacts/opcua-runtime/$Run"
if (Test-Path -LiteralPath $resultRoot) { throw 'Run already exists; all retained evidence is preserved.' }
$port = if ($IndustrialSimRoot) { 48481 } else { 48480 }
if (@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq $port).Count) {
    throw "Port $port is occupied; existing processes are preserved."
}
$testConfigPath = Join-Path $repoRoot 'artifacts/p4/database.local.json'
if (-not (Test-Path -LiteralPath $testConfigPath)) { throw 'Dedicated local test database configuration is required.' }
$testConfig = Get-Content -LiteralPath $testConfigPath | ConvertFrom-Json
$savedConnection = $env:PLANTOPS_RUNTIME_CONNECTION
$savedLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    if ($IndustrialSimRoot) {
        $IndustrialSimRoot = (Resolve-Path -LiteralPath $IndustrialSimRoot).Path
        & dotnet build (Join-Path $IndustrialSimRoot 'examples/PlantOps.Scenario')
        if ($LASTEXITCODE -ne 0) { throw 'IndustrialSim example build failed.' }
    }
    # This private configuration holds only the dedicated test container credentials.
    $env:PLANTOPS_RUNTIME_CONNECTION = "Host=127.0.0.1;Port=$($testConfig.Port);Database=plantops_tests;Username=plantops;Password=$($testConfig.Password);Pooling=false;Search Path=opcua_runtime_$Run"
    & dotnet restore spikes/PlantOps.OpcUa.Spike --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $mode = if ($IndustrialSimRoot) { 'industrial-scenario' } elseif ($MinimalScenario) { 'scenario' } else { 'runtime' }
    $runtimeArgs = @($mode, $resultRoot)
    if ($IndustrialSimRoot) { $runtimeArgs += $IndustrialSimRoot }
    & dotnet spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0/PlantOps.OpcUa.Spike.dll @runtimeArgs
    if ($LASTEXITCODE -ne 0) { throw "Runtime acceptance failed; retain $resultRoot." }
} finally {
    $env:PLANTOPS_RUNTIME_CONNECTION = $savedConnection
    Set-Location -LiteralPath $savedLocation
}
