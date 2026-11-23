param(
    [Parameter(Mandatory)][string]$LedgerPath,
    [ValidateRange(1, 100)][int]$ExpectedLimit = 4
)
$ErrorActionPreference = 'Stop'
$file = $null
try {
    $resolved = (Resolve-Path -LiteralPath $LedgerPath -ErrorAction Stop).ProviderPath
    $file = [IO.File]::Open($resolved, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $reader = [IO.StreamReader]::new($file, [Text.Encoding]::UTF8, $true, 1024, $true)
    try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if (-not $content.EndsWith("`n")) { throw 'Incomplete ledger.' }
    $lines = @($content.Split("`n", [StringSplitOptions]::RemoveEmptyEntries))
    $header = $lines[0] | ConvertFrom-Json -ErrorAction Stop
    if ($header.kind -cne 'authorization' -or $header.limit -isnot [long] -and $header.limit -isnot [int] -or $header.limit -ne $ExpectedLimit) { throw 'Authorization limit mismatch.' }
    for ($i = 1; $i -lt $lines.Count; $i++) {
        $entry = $lines[$i] | ConvertFrom-Json -ErrorAction Stop
        if ($entry.sequence -isnot [long] -and $entry.sequence -isnot [int] -or $entry.sequence -ne $i) { throw 'Invalid sequence.' }
    }
    $reserved = $lines.Count - 1
    if ($reserved -ge $header.limit) { throw 'Authorization exhausted.' }
    [pscustomobject]@{ Path = $resolved; Limit = $header.limit; Reserved = $reserved; Remaining = $header.limit - $reserved }
} catch {
    throw 'Existing authorization ledger is missing, locked, corrupt, exhausted or its total limit differs. No ledger was created or reset.'
} finally {
    if ($file) { $file.Dispose() }
}
