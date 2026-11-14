param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_-]{1,60}$')][string]$Run,
    [ValidateRange(1, 100)][int]$MaxRequests = 6,
    [ValidateSet('trusted-anomaly', 'partial-bad-high-vibration', 'all-unverifiable', 'applicable-procedure', 'injected-procedure',
        'recorded-recovery-later-high', 'brief-low-no-recovery', 'recovery-pair')][string]$Scenario
)
$ErrorActionPreference = 'Stop'
$reportRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/investigation-quality/$Run"
if (Test-Path -LiteralPath $reportRoot) { throw 'Use a fresh run name; reports are immutable.' }
$names = @('PLANTOPS_ADVICE_REAL', 'PLANTOPS_LLM_REQUEST_LEDGER', 'PLANTOPS_LLM_CAPTURE_REQUESTS', 'PLANTOPS_LIVE_REPORT_ROOT')
$names += @('Provider', 'BaseUrl', 'Model', 'ApiKey', 'ReasoningEffort', 'RequestTimeoutSeconds', 'RunTimeoutSeconds', 'MaxModelRounds', 'MaxToolCalls', 'MaxInputTokens', 'MaxOutputTokens', 'MaxRetries', 'MaxResponseBytes', 'StreamResponses') | ForEach-Object { "AI__$_" }
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    . "$PSScriptRoot/Use-LocalAi.ps1"
    if ($env:AI__Provider -ne 'OpenAiCompatible' -or [string]::IsNullOrWhiteSpace($env:AI__ApiKey)) { throw 'Configured real provider required.' }
    New-Item -ItemType Directory -Path $reportRoot | Out-Null
    $ledger = Join-Path $reportRoot 'requests.jsonl'
    [System.IO.File]::WriteAllText($ledger, (@{ kind = 'authorization'; limit = $MaxRequests; purpose = 'User-requested bounded v3 real-model advice validation; fictional evidence only.' } | ConvertTo-Json -Compress) + "`n", [System.Text.UTF8Encoding]::new($false))
    $env:PLANTOPS_ADVICE_REAL = '1'
    $env:PLANTOPS_LLM_REQUEST_LEDGER = $ledger
    $env:PLANTOPS_LLM_CAPTURE_REQUESTS = '1'
    $env:PLANTOPS_LIVE_REPORT_ROOT = $reportRoot
    $filter = 'FullyQualifiedName~PlantOps.Tests.InvestigationAdviceLiveTests'
    if ($Scenario -eq 'recovery-pair') {
        $filter += '&FullyQualifiedName~FinalAdviceRecoveryReview'
    } elseif ($Scenario) {
        $filter += "&DisplayName~$Scenario"
    } else {
        $filter += '&FullyQualifiedName~FinalAdviceUsesRealModelAndPreservesEvidenceBoundaries'
    }
    Write-Output "Real model: $env:AI__Model; hard cap: $MaxRequests sends; automatic retries: 0. Reports: $reportRoot"
    dotnet test "$PSScriptRoot/../tests/PlantOps.Tests/PlantOps.Tests.csproj" --no-build --no-restore --filter $filter --verbosity quiet --logger "trx;LogFileName=$reportRoot/results.trx"
    $testExit = $LASTEXITCODE
    $reports = @(Get-ChildItem -LiteralPath $reportRoot -File -Filter '*.json' | Where-Object { $_.Name -notlike 'requests.*' -and $_.Name -ne 'summary.json' } | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
    $summary = @{
        model = $env:AI__Model; requestLimit = $MaxRequests; sends = @(Get-Content -LiteralPath $ledger | Select-Object -Skip 1).Count
        automaticRetries = 0; fixtureOnly = $true; realProvider = $true; autonomousToolSelectionTested = $false
        testExit = $testExit; semanticReviewRequired = $true
        cases = @($reports | ForEach-Object { @{ scenario = $_.scenario; failure = $_.failure; passedStructuralChecks = $_.passedStructuralChecks; status = $_.advice.status; workOrderStatus = $_.advice.workOrder.status; inputTokens = $_.actualInputTokens; outputTokens = $_.actualOutputTokens } })
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json')
    Write-Output ($summary | ConvertTo-Json -Depth 8)
    if ($testExit -ne 0) { throw 'Real-model validation has failures; reports retained. No automatic retry.' }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
