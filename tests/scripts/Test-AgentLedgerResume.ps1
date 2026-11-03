$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = Join-Path $root 'deploy/Test-InvestigationAgentReal.ps1'
$budgetReader = Join-Path $root 'deploy/Get-AgentRequestBudget.ps1'
$path = [IO.Path]::GetTempFileName()
$savedConnection = $env:PLANTOPS_TEST_CONNECTION
$savedLedger = $env:PLANTOPS_LLM_REQUEST_LEDGER
function Assert-Rejected([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Expected fail-closed rejection.' }
}
try {
    $env:PLANTOPS_TEST_CONNECTION = 'Host=127.0.0.1;Database=plantops_tests;Username=fictional;Password=fictional'
    $env:PLANTOPS_LLM_REQUEST_LEDGER = $null
    $prefix = "{`"kind`":`"authorization`",`"limit`":4}`n{`"sequence`":1}`n{`"sequence`":2}`n"
    [IO.File]::WriteAllText($path, $prefix)
    foreach ($run in @('resume_guard_first', 'resume_guard_different_name')) {
        $budget = & $target -Run $run -LedgerPath $path -AuthorizePaidModelCalls -ValidateOnly
        if ($budget.Limit -ne 4 -or $budget.Reserved -ne 2 -or $budget.Remaining -ne 2) { throw 'Run name reset cumulative authorization.' }
        if (Test-Path -LiteralPath (Join-Path $root "artifacts/investigation-quality/$run")) { throw 'Preflight created reports.' }
        if ([IO.File]::ReadAllText($path) -cne $prefix) { throw 'Preflight modified original reservations.' }
    }
    Assert-Rejected { & $target -Run 'resume_guard_missing' -AuthorizePaidModelCalls -ValidateOnly }
    Assert-Rejected { & $target -Run 'resume_guard_mismatch' -LedgerPath $path -MaxRequests 3 -AuthorizePaidModelCalls -ValidateOnly }
    $env:PLANTOPS_LLM_REQUEST_LEDGER = "$path.conflict"
    Assert-Rejected { & $target -Run 'resume_guard_conflict' -LedgerPath $path -AuthorizePaidModelCalls -ValidateOnly }
    $env:PLANTOPS_LLM_REQUEST_LEDGER = $null
    $locked = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Rejected { & $budgetReader -LedgerPath $path } } finally { $locked.Dispose() }
    foreach ($bad in @('', '{"kind":"authorization","limit":4}', "{`"kind`":`"authorization`",`"limit`":4}`n{`"sequence`":2}`n", "$prefix{`"sequence`":3}`n{`"sequence`":4}`n")) {
        [IO.File]::WriteAllText($path, $bad)
        Assert-Rejected { & $budgetReader -LedgerPath $path }
        if ([IO.File]::ReadAllText($path) -cne $bad) { throw 'Rejected ledger was modified.' }
    }
    [IO.File]::Delete($path)
    Assert-Rejected { & $budgetReader -LedgerPath $path }
    if (Test-Path -LiteralPath $path) { throw 'Missing ledger was recreated.' }
} finally {
    [IO.File]::Delete($path)
    $env:PLANTOPS_TEST_CONNECTION = $savedConnection
    $env:PLANTOPS_LLM_REQUEST_LEDGER = $savedLedger
}
Write-Output 'PASS: new report names reuse 2/4 cumulative reservations; missing, mismatched, conflicting, locked, corrupt and exhausted ledgers fail closed; no model/DB/config access.'
