param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run
)
$ErrorActionPreference = 'Stop'
$evidenceSchema = "ai_investigation_$Run"
$evidenceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/ai-investigation/$Run"
if (Test-Path -LiteralPath $evidenceRoot) { throw 'Use a new run name; existing evidence must be retained.' }
$evidenceVariables = @('ConnectionStrings__PlantOps', 'Demo__Password', 'PLANTOPS_TEST_CONNECTION', 'PLANTOPS_TEST_DOCUMENT_ROOT')
$evidencePrevious = @{}
foreach ($name in $evidenceVariables) { $evidencePrevious[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    . "$PSScriptRoot/Use-LocalDatabase.ps1"
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
    # CREATE only in the dedicated test database; never reset or remove an existing schema.
    docker compose -f "$PSScriptRoot/compose.yaml" exec -T database psql -U plantops -d plantops_tests -v ON_ERROR_STOP=1 -c "CREATE SCHEMA $evidenceSchema"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create a fresh isolated test schema.' }
    $env:PLANTOPS_TEST_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;').Replace('Command Timeout=5', 'Command Timeout=60') + ";Search Path=$evidenceSchema"
    $env:PLANTOPS_TEST_DOCUMENT_ROOT = Join-Path $evidenceRoot 'documents'
    @{ Schema = $evidenceSchema; Documents = $env:PLANTOPS_TEST_DOCUMENT_ROOT; RealProviderVerified = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'scope.json')
    dotnet test "$PSScriptRoot/../tests/PlantOps.Tests" --artifacts-path "$evidenceRoot/build" --filter 'FullyQualifiedName~PlantOps.Tests.InvestigationScenarioTests|FullyQualifiedName~PlantOps.Tests.InvestigationServiceTests|FullyQualifiedName~PlantOps.Tests.InvestigationEngineTests|FullyQualifiedName~PlantOps.Tests.DocumentEvidenceTests|FullyQualifiedName~PlantOps.Tests.DocumentTests|FullyQualifiedName~PlantOps.Tests.DocumentParserTests|FullyQualifiedName~PlantOps.Tests.Ai|FullyQualifiedName~PlantOps.Tests.HealthEndpointTests|FullyQualifiedName~PlantOps.Tests.MeasurementMaintenanceTests|FullyQualifiedName~PlantOps.Tests.Sampling|FullyQualifiedName~PlantOps.Tests.TrendSamplingTests|FullyQualifiedName~PlantOps.Tests.OpcUaDemoPolicyTests' --logger "trx;LogFileName=$evidenceRoot/results.trx" --logger 'console;verbosity=minimal'
    $testExitCode = $LASTEXITCODE
    if (Test-Path -LiteralPath "$evidenceRoot/results.trx") {
        [xml]$trx = Get-Content -LiteralPath "$evidenceRoot/results.trx" -Raw
        $cases = @($trx.TestRun.Results.UnitTestResult | Where-Object testName -like '*InvestigationScenarioTests.FrozenBusinessScenario*' | ForEach-Object {
            if ($_.testName -notmatch 'S(?<id>\d{2})-[a-z-]+') { throw 'Scenario ID missing from test result.' }
            @{ Id = $Matches[0]; Number = [int]$Matches.id; Outcome = $_.outcome; Name = $_.testName }
        } | Sort-Object Number)
        $complete = $cases.Count -eq 20 -and @($cases.Number | Select-Object -Unique).Count -eq 20 -and @($cases | Where-Object Outcome -ne 'Passed').Count -eq 0
        @{ Passed = $complete; Cases = $cases; DeterministicOnly = $true; RealProviderVerified = $false; RealQualityAcceptance = $false; P4Acceptance = $false } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$evidenceRoot/scenario-check.json"
    } else { $complete = $false }
    if ($testExitCode -ne 0 -or -not $complete) { throw "Investigation tests or twenty-scenario checks failed. Results retained in $evidenceRoot." }
    Write-Output "Investigation tests passed. Results retained in $evidenceRoot."
} finally {
    foreach ($name in $evidenceVariables) { [Environment]::SetEnvironmentVariable($name, $evidencePrevious[$name], 'Process') }
}
