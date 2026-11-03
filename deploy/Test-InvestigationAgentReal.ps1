param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_-]{1,60}$')][string]$Run,
    [ValidateSet('AutonomousRead', 'DirectFinish')][string]$Mode = 'AutonomousRead',
    [ValidateRange(1, 4)][int]$MaxRequests = 4,
    [switch]$AuthorizePaidModelCalls,
    [string]$LedgerPath = $env:PLANTOPS_LLM_REQUEST_LEDGER,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
# This switch records the operator's current-run authorization, not historical permission.
if (-not $AuthorizePaidModelCalls) { throw 'Current-run explicit paid-model authorization is required. No configuration, database or model accessed.' }
if ([string]::IsNullOrWhiteSpace($env:PLANTOPS_TEST_CONNECTION)) { throw 'Dedicated plantops_tests connection required.' }
$database = [System.Data.Common.DbConnectionStringBuilder]::new()
try { $database.set_ConnectionString($env:PLANTOPS_TEST_CONNECTION) } catch { throw 'Invalid dedicated database configuration; connection details suppressed.' }
if ($database['Database'] -cne 'plantops_tests') { throw 'Only plantops_tests is permitted.' }
if ([string]::IsNullOrWhiteSpace($LedgerPath)) { throw 'An existing authorization LedgerPath is required; Run names never initialize budgets.' }
$budget = & "$PSScriptRoot/Get-AgentRequestBudget.ps1" -LedgerPath $LedgerPath -ExpectedLimit $MaxRequests
if ($env:PLANTOPS_LLM_REQUEST_LEDGER -and [IO.Path]::GetFullPath($env:PLANTOPS_LLM_REQUEST_LEDGER) -ne $budget.Path) { throw 'LedgerPath conflicts with the active authorization ledger.' }
$reportRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/investigation-quality/$Run"
if (Test-Path -LiteralPath $reportRoot) { throw 'Use a fresh run name; reports are immutable.' }
if ($ValidateOnly) { return $budget }
$names = @('PLANTOPS_ADVICE_AGENT_REAL', 'PLANTOPS_LLM_REQUEST_LEDGER', 'PLANTOPS_LLM_CAPTURE_REQUESTS', 'PLANTOPS_LIVE_REPORT_ROOT')
$names += @('Provider', 'BaseUrl', 'Model', 'ApiKey', 'ReasoningEffort', 'RequestTimeoutSeconds', 'RunTimeoutSeconds', 'MaxModelRounds', 'MaxToolCalls', 'MaxInputTokens', 'MaxOutputTokens', 'MaxRetries', 'MaxResponseBytes', 'StreamResponses') | ForEach-Object { "AI__$_" }
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    . "$PSScriptRoot/Use-LocalAi.ps1"
    if ($env:AI__Provider -ne 'OpenAiCompatible' -or [string]::IsNullOrWhiteSpace($env:AI__ApiKey)) { throw 'Configured real provider required.' }
    $env:AI__MaxRetries = '0'
    $env:AI__MaxInputTokens = '32000'
    $env:AI__MaxOutputTokens = '6000'
    $env:AI__MaxModelRounds = if ($Mode -eq 'AutonomousRead') { '4' } else { '1' }
    $env:AI__MaxToolCalls = '8'
    $env:AI__StreamResponses = 'true'
    $env:AI__RequestTimeoutSeconds = '120'
    $env:AI__RunTimeoutSeconds = '180'
    New-Item -ItemType Directory -Path $reportRoot | Out-Null
    $ledger = $budget.Path
    $env:PLANTOPS_ADVICE_AGENT_REAL = '1'
    $env:PLANTOPS_LLM_REQUEST_LEDGER = $ledger
    $env:PLANTOPS_LLM_CAPTURE_REQUESTS = '1'
    $env:PLANTOPS_LIVE_REPORT_ROOT = $reportRoot
    $method = if ($Mode -eq 'AutonomousRead') { 'RealAgentReadsScopedEvidenceAndProducesAReviewableProposal' } else { 'RealInitialSnapshotProducesAReviewableProposalWithoutExtraReads' }
    # Select exactly one test, so a failed autonomous attempt never triggers a fallback paid test.
    & dotnet test "$PSScriptRoot/../tests/PlantOps.Tests/PlantOps.Tests.csproj" --no-restore --filter "FullyQualifiedName=PlantOps.Tests.InvestigationAdviceAgentLiveTests.$method" --logger "trx;LogFileName=$reportRoot/results.trx" *> "$reportRoot/test.log"
    $testExit = $LASTEXITCODE
    @{ mode = $Mode; ledgerPath = $ledger; externalSendLimit = $budget.Limit; reservedBefore = $budget.Reserved; reservedSends = @(Get-Content -LiteralPath $ledger | Select-Object -Skip 1).Count; automaticRetries = 0; testExit = $testExit; semanticReviewRequired = $true; manualApprovalPerformed = $false } |
        ConvertTo-Json | Set-Content -LiteralPath "$reportRoot/summary.json"
    Write-Output "Reports: $reportRoot; test exit: $testExit. No automatic retry or approval."
    if ($testExit -ne 0) { throw 'Acceptance failed; preserve reports and classify before any further external send.' }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
