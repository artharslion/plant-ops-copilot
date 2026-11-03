$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = Join-Path $root 'deploy/Test-InvestigationAgentReal.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($target, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Agent harness must parse.' }
# Verify the authorization guard is before all configuration/DB/model access before invoking it.
if ($ast.EndBlock.Statements[1].Extent.Text -notlike 'if (-not $AuthorizePaidModelCalls)*throw*') { throw 'Authorization guard must be the first operation after error handling setup.' }
$saved = $env:PLANTOPS_TEST_CONNECTION
try {
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
Write-Output 'PASS: missing current authorization and business databases are blocked without model or database access.'
