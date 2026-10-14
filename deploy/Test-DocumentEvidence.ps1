param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run
)
$ErrorActionPreference = 'Stop'
$evidenceSchema = "ai_evidence_$Run"
$evidenceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/ai-evidence/$Run"
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
    dotnet test "$PSScriptRoot/../tests/PlantOps.Tests" --no-restore --filter 'FullyQualifiedName~PlantOps.Tests.DocumentEvidenceTests|FullyQualifiedName~PlantOps.Tests.DocumentTests|FullyQualifiedName~PlantOps.Tests.DocumentParserTests|FullyQualifiedName~PlantOps.Tests.Ai|FullyQualifiedName~PlantOps.Tests.HealthEndpointTests' --logger "trx;LogFileName=$evidenceRoot/results.trx" --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "Evidence tests failed. Results retained in $evidenceRoot." }
    Write-Output "Evidence tests passed. Results retained in $evidenceRoot."
} finally {
    foreach ($name in $evidenceVariables) { [Environment]::SetEnvironmentVariable($name, $evidencePrevious[$name], 'Process') }
}
