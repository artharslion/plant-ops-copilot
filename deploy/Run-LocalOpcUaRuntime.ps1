[CmdletBinding()]
param([string]$Run = ('run_' + (Get-Date -Format 'yyyyMMdd_HHmmss')), [switch]$MinimalScenario,
    [string]$IndustrialSimRoot, [switch]$UseTestConnection, [switch]$BriefLow, [switch]$Disconnected, [switch]$Stable)

$ErrorActionPreference = 'Stop'
if ($BriefLow -and -not $IndustrialSimRoot) { throw 'BriefLow requires IndustrialSim.' }
if ($Disconnected -and (-not $IndustrialSimRoot -or $BriefLow)) { throw 'Disconnected requires IndustrialSim without BriefLow.' }
if ($Stable -and (-not $IndustrialSimRoot -or $Disconnected -or $BriefLow)) { throw 'Stable requires IndustrialSim without another variant.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Run -cnotmatch '^[a-z0-9_]{1,40}$') { throw 'Use a new lowercase run name.' }
$resultRoot = Join-Path $repoRoot "artifacts/opcua-runtime/$Run"
if (Test-Path -LiteralPath $resultRoot) { throw 'Run already exists; all retained evidence is preserved.' }
$port = if ($IndustrialSimRoot) { 48481 } else { 48480 }
if (@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq $port).Count) {
    throw "Port $port is occupied; existing processes are preserved."
}
$schema = "opcua_runtime_$Run"
if ($UseTestConnection) {
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    try { $connection.set_ConnectionString($env:PLANTOPS_TEST_CONNECTION) } catch { throw 'Invalid dedicated test connection.' }
    if ($connection['Database'] -cne 'plantops_tests') { throw 'The connection must explicitly name plantops_tests.' }
    $connection['Pooling'] = $false
    $connection['Search Path'] = $schema
    $runtimeConnection = $connection.ConnectionString
} else {
    $testConfigPath = Join-Path $repoRoot 'artifacts/p4/database.local.json'
    if (-not (Test-Path -LiteralPath $testConfigPath)) { throw 'Dedicated local test database configuration is required.' }
    $testConfig = Get-Content -LiteralPath $testConfigPath | ConvertFrom-Json
    $runtimeConnection = "Host=127.0.0.1;Port=$($testConfig.Port);Database=plantops_tests;Username=plantops;Password=$($testConfig.Password);Pooling=false;Search Path=$schema"
}
$savedConnection = $env:PLANTOPS_RUNTIME_CONNECTION
$savedLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    if ($IndustrialSimRoot) {
        $IndustrialSimRoot = (Resolve-Path -LiteralPath $IndustrialSimRoot).Path
        & dotnet build (Join-Path $IndustrialSimRoot 'examples/PlantOps.Scenario')
        if ($LASTEXITCODE -ne 0) { throw 'IndustrialSim example build failed.' }
    }
    $env:PLANTOPS_RUNTIME_CONNECTION = $runtimeConnection
    & dotnet restore spikes/PlantOps.OpcUa.Spike --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $mode = if ($Disconnected) { 'industrial-disconnected' } elseif ($Stable) { 'industrial-stable' } elseif ($BriefLow) { 'industrial-brief-low' } elseif ($IndustrialSimRoot) { 'industrial-scenario' } elseif ($MinimalScenario) { 'scenario' } else { 'runtime' }
    $runtimeArgs = @($mode, $resultRoot)
    if ($IndustrialSimRoot) { $runtimeArgs += $IndustrialSimRoot }
    & dotnet spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0/PlantOps.OpcUa.Spike.dll @runtimeArgs
    if ($LASTEXITCODE -ne 0) { throw "Runtime acceptance failed; retain $resultRoot." }
} finally {
    $env:PLANTOPS_RUNTIME_CONNECTION = $savedConnection
    Set-Location -LiteralPath $savedLocation
}
