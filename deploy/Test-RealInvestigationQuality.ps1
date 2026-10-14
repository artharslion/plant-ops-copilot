param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run,
    [ValidateSet('S01-normal-minute', 'S02-S03-missing-materials', 'S04-bad-current', 'S06-ingestion-gap', 'S11-document-injection')][string]$Scenario
)
$ErrorActionPreference = 'Stop'
$reportRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/ai-real-quality/$Run"
if (Test-Path -LiteralPath $reportRoot) { throw 'Use a fresh run name; reports must be retained.' }
$names = @('ConnectionStrings__PlantOps', 'Demo__Password', 'PLANTOPS_TEST_CONNECTION', 'PLANTOPS_TEST_DOCUMENT_ROOT', 'PLANTOPS_LIVE_REPORT_ROOT', 'PLANTOPS_REAL_FIVE', 'PLANTOPS_REAL_SCENARIO')
$names += @('Provider', 'BaseUrl', 'Model', 'ApiKey', 'ReasoningEffort', 'RequestTimeoutSeconds', 'RunTimeoutSeconds', 'MaxModelRounds', 'MaxToolCalls', 'MaxInputTokens', 'MaxOutputTokens', 'MaxRetries', 'MaxResponseBytes', 'StreamResponses') | ForEach-Object { "AI__$_" }
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    . "$PSScriptRoot/Use-LocalDatabase.ps1"
    . "$PSScriptRoot/Use-LocalAi.ps1"
    if ($env:AI__Provider -ne 'OpenAiCompatible' -or [string]::IsNullOrWhiteSpace($env:AI__ApiKey)) { throw 'Real configured provider required.' }
    $env:AI__RequestTimeoutSeconds = '120'
    $env:AI__RunTimeoutSeconds = '180'
    $env:AI__StreamResponses = 'true'
    $env:AI__MaxRetries = '0'
    $env:AI__MaxModelRounds = '4'
    $env:AI__MaxToolCalls = '8'
    $env:AI__MaxInputTokens = '16000'
    $env:AI__MaxOutputTokens = '2000'
    $env:PLANTOPS_TEST_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;').Replace('Command Timeout=5', 'Command Timeout=60') + ';Options=-c max_parallel_workers_per_gather=0'
    $env:PLANTOPS_TEST_DOCUMENT_ROOT = Join-Path $reportRoot 'documents'
    $env:PLANTOPS_LIVE_REPORT_ROOT = $reportRoot
    $env:PLANTOPS_REAL_FIVE = '1'
    $env:PLANTOPS_REAL_SCENARIO = $Scenario
    $caseLimit = if ($Scenario) { 1 } else { 5 }
    $testFilter = 'FullyQualifiedName~PlantOps.Tests.InvestigationRealQualityTests'
    if ($Scenario) { $testFilter += "&DisplayName~$Scenario" }
    New-Item -ItemType Directory -Path $reportRoot | Out-Null
    @{ FixtureOnly = $true; Scenario = $Scenario; MaxInvestigations = $caseLimit; MaxExternalRequests = $caseLimit; MaxRequestsPerInvestigation = 1; MaxRetries = 0; RequestTimeoutSeconds = 120; RunTimeoutSeconds = 180; StreamResponses = $true; Cost = $null; P4Acceptance = $false } | ConvertTo-Json | Set-Content -LiteralPath "$reportRoot/plan.json"
    dotnet test "$PSScriptRoot/../tests/PlantOps.Tests" --artifacts-path "$reportRoot/build" --filter $testFilter --logger "trx;LogFileName=$reportRoot/results.trx" --logger 'console;verbosity=minimal'
    $testExit = $LASTEXITCODE
    $cases = @(Get-ChildItem -LiteralPath $reportRoot -File | Where-Object Name -match '^S\d.*\.json$' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
    $passed = $testExit -eq 0 -and $cases.Count -eq $caseLimit -and @($cases | Where-Object { -not $_.Passed }).Count -eq 0
    @{ Passed = $passed; CompletedReports = $cases.Count; Requests = ($cases.Requests | Measure-Object -Sum).Sum; Cases = @($cases | ForEach-Object { @{ Scenario = $_.Scenario; Passed = $_.Passed; Status = $_.Run.Status; FailureCode = $_.Run.FailureCode; DurationSeconds = $_.DurationSeconds; ActualUsage = $_.ActualUsage; FailedChecks = @($_.Checks.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object Name) } }); Cost = $null; FullTwentyRealQualityAcceptance = $false; P4Acceptance = $false } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$reportRoot/summary.json"
    if (-not $passed) { throw "Finite real quality acceptance did not pass; evidence retained in $reportRoot. No retry was performed." }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
