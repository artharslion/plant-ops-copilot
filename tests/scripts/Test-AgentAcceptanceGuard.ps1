$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = Join-Path $root 'deploy/Test-InvestigationAgentReal.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($target, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Agent harness must parse.' }
# Verify the authorization guard is before all configuration/DB/model access before invoking it.
if ($ast.EndBlock.Statements[1].Extent.Text -notlike 'if (-not $AuthorizePaidModelCalls)*throw*') { throw 'Authorization guard must be the first operation after error handling setup.' }
# Exercise the exact production preflight, including positive inputs, without executing
# the later configuration/report/database/model operations.
if ($ast.EndBlock.Statements[5].Extent.Text -notlike '*Only plantops_tests is permitted.*' -or
    $ast.EndBlock.Statements[6].Extent.Text -notlike '*existing authorization LedgerPath*') { throw 'Review preflight extraction before running this regression.' }
$preflight = [scriptblock]::Create(($ast.EndBlock.Statements[0..5].Extent.Text -join "`n"))
$saved = $env:PLANTOPS_TEST_CONNECTION
try {
    foreach ($connection in @(
        'Host=127.0.0.1;Database=plantops_tests;Username=fictional;Password=fictional',
        'Host=127.0.0.1;database="plantops_tests";Username=fictional;Password="fictional;value"'
    )) {
        $env:PLANTOPS_TEST_CONNECTION = $connection
        $AuthorizePaidModelCalls = $true
        & $preflight
    }
    $env:PLANTOPS_TEST_CONNECTION = 'Database="unterminated'
    $failure = $null
    try { & $preflight } catch { $failure = $_.Exception.Message }
    if ($failure -ne 'Invalid dedicated database configuration; connection details suppressed.') { throw 'Malformed connections must fail without exposing their contents.' }
    $env:PLANTOPS_TEST_CONNECTION = 'Host=127.0.0.1;Database=forbidden_business_database'
    $failure = $null
    try { & $target -Run 'guard_never_execute' } catch { $failure = $_.Exception.Message }
    if ($failure -notlike 'Current-run explicit paid-model authorization*') { throw 'Missing authorization must reject before configuration access.' }
    $failure = $null
    try { & $target -Run 'guard_never_execute' -AuthorizePaidModelCalls } catch { $failure = $_.Exception.Message }
    if ($failure -ne 'Only plantops_tests is permitted.') { throw 'Business database must be blocked before configuration access.' }
    if (Test-Path -LiteralPath (Join-Path $root 'artifacts/investigation-quality/guard_never_execute')) { throw 'Guard unexpectedly created a report directory.' }
} finally {
    $env:PLANTOPS_TEST_CONNECTION = $saved
}
Write-Output 'PASS: dedicated database positive inputs accepted; missing authorization and business databases blocked; no model or database access.'
