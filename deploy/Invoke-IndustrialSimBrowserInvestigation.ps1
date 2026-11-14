[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run,
    [switch]$RealAi,
    [string]$LedgerPath,
    [ValidateRange(1, 100)][int]$MaxRequests = 4,
    [switch]$Disconnected,
    [switch]$Stable
)
$ErrorActionPreference = 'Stop'
if ($Disconnected -and $Stable) { throw 'Choose one live protocol variant.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repoRoot "artifacts/opcua-runtime/$Run"
$reportName = if ($Disconnected) { 'disconnected-ready.json' } elseif ($Stable) { 'stable-ready.json' } else { 'result.json' }
$result = Get-Content -LiteralPath (Join-Path $root $reportName) -Raw | ConvertFrom-Json
$schema = "opcua_runtime_$Run"
$expectedState = if ($Disconnected -or $result.ScenarioVersion -ceq 'plantops-brief-low-v1') { 'Active' } else { 'Recovered' }
if (-not $result.Passed -or -not $result.RealOpcUa -or $result.Schema -cne $schema -or
    @($result.Alarms).Count -ne 1 -or $result.Alarms[0].State -cne $expectedState) {
    throw 'A successful isolated protocol run with the expected alarm state is required.'
}
if ($Disconnected -and ($result.ScenarioVersion -cne 'plantops-disconnected-v1' -or
    $result.Health.State -cne 'Reconnecting' -or (Test-Path -LiteralPath (Join-Path $root 'result.json')))) {
    throw 'The disconnected protocol run must still be held for investigation.'
}
if ($Stable -and ($result.ScenarioVersion -cne 'plantops-stable-v1' -or $result.Health.State -cne 'Connected' -or
    -not $result.Alarms[0].RecoveredAt -or (Test-Path -LiteralPath (Join-Path $root 'result.json')))) {
    throw 'The stable protocol run must still provide fresh observations for investigation.'
}
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
try { $connection.set_ConnectionString($env:PLANTOPS_TEST_CONNECTION) } catch { throw 'Invalid dedicated test connection.' }
if ($connection['Database'] -cne 'plantops_tests') { throw 'The connection must explicitly name plantops_tests.' }
$connection['Search Path'] = $schema
$connection['Pooling'] = $false

$names = @('ConnectionStrings__PlantOps', 'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'Documents__StorageRoot',
    'PLANTOPS_LLM_REQUEST_LEDGER', 'PLANTOPS_LLM_CAPTURE_REQUESTS')
$names += @('Provider', 'BaseUrl', 'Model', 'ApiKey', 'ReasoningEffort', 'MaxInputTokens', 'MaxOutputTokens',
    'MaxToolCalls', 'MaxModelRounds', 'MaxResponseBytes', 'RequestTimeoutSeconds', 'RunTimeoutSeconds',
    'MaxRetries', 'StreamResponses') | ForEach-Object { "AI__$_" }
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:ConnectionStrings__PlantOps = $connection.ConnectionString
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:Documents__StorageRoot = Join-Path $root 'documents'
    $env:AI__Provider = 'Fake'
    if ($RealAi) {
        if (-not $LedgerPath) { throw 'Provide a ledger for the current authorization before starting the Worker.' }
        $budget = & "$PSScriptRoot/Get-AgentRequestBudget.ps1" -LedgerPath $LedgerPath -ExpectedLimit $MaxRequests
        . "$PSScriptRoot/Use-LocalAi.ps1"
        if ($env:AI__Provider -ne 'OpenAiCompatible' -or -not $env:AI__ApiKey) { throw 'Configured real provider required.' }
        $env:PLANTOPS_LLM_REQUEST_LEDGER = $budget.Path
        $env:PLANTOPS_LLM_CAPTURE_REQUESTS = '1'
    }
    $env:AI__MaxRetries = '0'
    $env:AI__MaxInputTokens = '32000'
    $env:AI__MaxOutputTokens = '6000'
    $env:AI__MaxModelRounds = '4'
    $env:AI__MaxToolCalls = '8'
    $env:AI__RequestTimeoutSeconds = '120'
    $env:AI__RunTimeoutSeconds = '180'
    $env:AI__StreamResponses = 'true'
    $worker = Join-Path $repoRoot 'src/PlantOps.Worker/bin/Debug/net10.0/PlantOps.Worker.dll'
    & dotnet $worker --investigation-once --Logging:LogLevel:Default=Warning --Logging:LogLevel:PlantOps=Information
    if ($LASTEXITCODE -ne 0) { throw 'Finite investigation Worker failed.' }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
