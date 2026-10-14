param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run)
$ErrorActionPreference = 'Stop'
$liveRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/ai-investigation/$Run"
if (Test-Path -LiteralPath $liveRoot) { throw 'Use a fresh run name; prior evidence must be retained.' }
$liveSaved = @{}
$liveNames = @('ConnectionStrings__PlantOps','Demo__Password','PLANTOPS_TEST_CONNECTION','PLANTOPS_TEST_DOCUMENT_ROOT','PLANTOPS_LIVE_INVESTIGATION','PLANTOPS_LIVE_REPORT_ROOT')
foreach ($name in $liveNames) { $liveSaved[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
foreach ($entry in Get-ChildItem Env: | Where-Object Name -like 'AI__*') { $liveSaved[$entry.Name] = $entry.Value }
try {
    . "$PSScriptRoot/Use-LocalDatabase.ps1"
    . "$PSScriptRoot/Use-LocalAi.ps1"
    if ($env:AI__Provider -ne 'OpenAiCompatible') { throw 'A configured real provider is required for this explicit live test.' }
    New-Item -ItemType Directory -Path $liveRoot | Out-Null
    $schema = "ai_live_$Run"
    docker compose -f "$PSScriptRoot/compose.yaml" exec -T database psql -U plantops -d plantops_tests -v ON_ERROR_STOP=1 -c "CREATE SCHEMA $schema"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create a fresh dedicated test schema.' }
    $env:PLANTOPS_TEST_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;').Replace('Command Timeout=5','Command Timeout=60') + ";Search Path=$schema"
    $env:PLANTOPS_TEST_DOCUMENT_ROOT = Join-Path $liveRoot 'documents'
    $env:PLANTOPS_LIVE_REPORT_ROOT = $liveRoot
    $env:PLANTOPS_LIVE_INVESTIGATION = '1'
    @{ Schema = $schema; FixtureOnly = $true; MaxInvestigations = 1; MaxRunSeconds = 60; QualityAcceptance = $false } | ConvertTo-Json | Set-Content (Join-Path $liveRoot 'scope.json')
    dotnet test "$PSScriptRoot/../tests/PlantOps.Tests" --no-restore --filter 'FullyQualifiedName~PlantOps.Tests.InvestigationLiveTests' --logger "trx;LogFileName=$liveRoot/results.trx" --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "Finite real investigation did not pass. Sanitized evidence retained in $liveRoot; do not automatically retry." }
    Write-Output "Finite real investigation passed. Evidence retained in $liveRoot; this is not quality acceptance."
} finally {
    foreach ($entry in @(Get-ChildItem Env: | Where-Object Name -like 'AI__*')) { Remove-Item -LiteralPath "Env:$($entry.Name)" }
    foreach ($name in $liveSaved.Keys) { [Environment]::SetEnvironmentVariable($name,$liveSaved[$name],'Process') }
}
