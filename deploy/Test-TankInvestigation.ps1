param([Parameter(Mandatory)][ValidateSet('filling','missing','idle')][string]$Case,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run,
    [switch]$RealAi, [string]$Model, [string]$LedgerPath, [ValidateRange(1,100)][int]$MaxRequests = 4)
$ErrorActionPreference = 'Stop'
if (-not $env:PLANTOPS_TEST_CONNECTION) { throw 'Set PLANTOPS_TEST_CONNECTION for the isolated plantops_tests database.' }
$db = [System.Data.Common.DbConnectionStringBuilder]::new()
$db.set_ConnectionString($env:PLANTOPS_TEST_CONNECTION)
if ($db['Database'] -cne 'plantops_tests') { throw 'Dedicated test database required.' }
$db['Search Path'] = "opcua_runtime_$Run"
$db['Pooling'] = $false
if ($RealAi) { $budget = & "$PSScriptRoot/Get-AgentRequestBudget.ps1" -LedgerPath $LedgerPath -ExpectedLimit $MaxRequests }
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $root = "artifacts/opcua-runtime/$Run"
    if (Test-Path -LiteralPath $root) { throw 'Choose a new run to preserve existing evidence.' }
    if (@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq 48480).Count) { throw 'Local simulator port is in use.' }
    & dotnet build spikes/PlantOps.OpcUa.Spike --no-restore -v:q
    if ($LASTEXITCODE) { throw 'Build failed.' }
    # Run with child-process environment so local credentials and model settings stay scoped.
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['PLANTOPS_RUNTIME_CONNECTION'] = $db.ConnectionString
    $start.Environment['PLANTOPS_TANK_REAL'] = if ($RealAi) { '1' } else { '0' }
    if ($RealAi) {
        $settings = (Get-Content appsettings.Ai.Local.json -Raw | ConvertFrom-Json).AI
        foreach ($key in 'ApiKey','BaseUrl','Model','ReasoningEffort') {
            $start.Environment["AI__$key"] = [string]$settings.$key
        }
        if ($Model) { $start.Environment['AI__Model'] = $Model }
        $start.Environment['PLANTOPS_LLM_REQUEST_LEDGER'] = $budget.Path
        $start.Environment['PLANTOPS_LLM_CAPTURE_REQUESTS'] = '1'
    }
    foreach ($arg in 'spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0/PlantOps.OpcUa.Spike.dll','tank-review',$root,$Case) {
        $start.ArgumentList.Add($arg)
    }
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    if ($process.ExitCode) { throw "Run failed. Retain $root." }
    $answer = Get-Content -LiteralPath "$root/investigation.json" -Raw | ConvertFrom-Json
    if ($answer.Status -ne 'Succeeded' -or -not $answer.Result -or ($RealAi -and $answer.Simulated)) {
        throw "Protocol captured. Investigation status $($answer.Status) $($answer.FailureCode). See $root/investigation.json."
    }
} finally { Pop-Location }
