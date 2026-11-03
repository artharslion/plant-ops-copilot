$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = Join-Path $root 'deploy/Test-RealInvestigationQuality.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($target, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Deprecated harness must parse successfully.' }
# Check this before invoking the target: a missing guard must not start paid work.
if ($ast.EndBlock.Statements.Count -ne 1 -or $ast.EndBlock.Statements[0] -isnot [System.Management.Automation.Language.ThrowStatementAst]) {
    throw 'Deprecated harness must contain only an unconditional throw after parameter binding.'
}
$names = @('PLANTOPS_REAL_FIVE', 'AI__Provider', 'AI__ApiKey')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    # Enabling the old opt-in flag must not bypass deprecation. The key is fictional.
    $env:PLANTOPS_REAL_FIVE = '1'
    $env:AI__Provider = 'OpenAiCompatible'
    $env:AI__ApiKey = 'guard-test-placeholder'
    $cases = @(@{}, @{ Run = 'guard_test' }, @{ Run = 'guard_test'; Scenario = 'S01-normal-minute' })
    foreach ($parameters in $cases) {
        $failure = $null
        try { & $target @parameters } catch { $failure = $_.Exception }
        if ($null -eq $failure -or $failure -isnot [System.InvalidOperationException] -or $failure.Message -notlike 'DEPRECATED:*v1/v2*current v3*P8*') {
            throw 'Every supported invocation must fail with the explicit deprecation reason.'
        }
        if ($env:PLANTOPS_REAL_FIVE -ne '1' -or $env:AI__Provider -ne 'OpenAiCompatible' -or $env:AI__ApiKey -ne 'guard-test-placeholder') {
            throw 'Deprecated harness must not modify provider configuration.'
        }
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
Write-Output 'PASS: deprecated harness rejects all 3 invocations before any work, even with real-provider flags enabled.'
