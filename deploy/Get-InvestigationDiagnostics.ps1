param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateSet('get_asset_details', 'get_recent_measurements', 'get_recent_alarms', 'get_maintenance_history', 'search_documents')]
    [string]$Tool = 'search_documents'
)
$ErrorActionPreference = 'Stop'
# Read one saved API run or agent acceptance report. Never load configuration,
# contact a service, inspect private captures, follow previous leases or judge prose.
$file = Get-Item -LiteralPath $Path
if ($file.PSIsContainer -or $file.Length -gt 32MB) { throw 'Expected one saved JSON report no larger than 32 MiB.' }
try { $document = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 100 }
catch { throw 'Invalid saved JSON report; content suppressed.' }
function Has-Field($Value, [string]$Name) { return $null -ne $Value -and $Value.PSObject.Properties.Name -contains $Name }
function Test-ToolNameList($Value) {
    if ($Value -isnot [array]) { return $false }
    foreach ($name in $Value) {
        if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name)) { return $false }
    }
    return $true
}
if ($document -isnot [pscustomobject]) { throw 'Expected a saved investigation run.' }
$run = if (Has-Field $document 'run') { $document.run } else { $document }
if ($run -isnot [pscustomobject]) { throw 'Expected a saved investigation run.' }
$record = $run.runRecord
$events = @($record.diagnostics | Where-Object { $null -ne $_ })
$timelineComplete = (Has-Field $record 'omittedDiagnosticCount') -and $record.omittedDiagnosticCount -eq 0
$requests = @($record.requests | Where-Object { $null -ne $_ })
$rounds = @(for ($index = 0; $index -lt $requests.Count; $index++) {
    $round = $index + 1
    $request = $requests[$index]
    $roundEvents = @($events | Where-Object { $_.round -eq $round })
    $decisions = @($roundEvents | Where-Object {
        $_.stage -eq 'tool-decision' -and (Test-ToolNameList $_.details.tools) -and
        (($_.outcome -eq 'Selected' -and $_.details.tools.Count -gt 0) -or
            ($_.outcome -eq 'NoToolCalls' -and $_.details.tools.Count -eq 0))
    })
    $responses = @($roundEvents | Where-Object { $_.stage -eq 'model-response' -and $_.outcome -eq 'Received' })
    $reads = @($roundEvents | Where-Object { $_.stage -eq 'read-tool' -and $_.outcome -eq 'Started' })
    $selected = 'Unknown'
    if ($decisions.Count -eq 1) {
        $selected = if ($decisions[0].details.tools -contains $Tool) { 'Selected' } else { 'NotSelected' }
    } elseif (@($reads | Where-Object { $_.details.name -eq $Tool }).Count -gt 0) {
        $selected = 'Selected'
    } elseif ($responses.Count -eq 1 -and $timelineComplete -and (Has-Field $responses[0].details 'toolCallCount') -and
        $responses[0].details.toolCallCount -eq $reads.Count) {
        # Historical records can establish absence only when every selected call
        # has a matching read-start event (including zero calls). Otherwise unknown.
        $selected = 'NotSelected'
    }
    $offered = 'Unknown'
    if (Has-Field $request.toolBudget 'offeredToolNames') {
        $names = $request.toolBudget.offeredToolNames
        if ((Test-ToolNameList $names) -and ((-not (Has-Field $request.toolBudget 'offeredToolCount')) -or
            $request.toolBudget.offeredToolCount -eq $names.Count)) {
            $offered = if ($names -contains $Tool) { 'Offered' } else { 'Withheld' }
        }
    } elseif ((Has-Field $request.toolBudget 'offeredToolCount') -and $request.toolBudget.offeredToolCount -eq 0) {
        $offered = 'Withheld'
    }
    $attempts = @($roundEvents | Where-Object { $_.stage -eq 'dispatch' -and $_.outcome -eq 'Started' } | ForEach-Object {
        $attempt = $_.attempt
        $attemptEvents = @($roundEvents | Where-Object { $_.attempt -eq $attempt })
        $headers = @($attemptEvents | Where-Object { $_.stage -eq 'response-headers' -and $_.outcome -eq 'Received' })
        $completion = @($attemptEvents | Where-Object { $_.stage -eq 'model-response' -and $_.outcome -eq 'Received' })
        [pscustomobject]@{
            attempt = $attempt
            httpStatus = if ($headers.Count -eq 1) { $headers[0].details.httpStatus } else { $null }
            headersElapsedMs = if ($headers.Count -eq 1) { $headers[0].details.elapsedMs } else { $null }
            parsedCompletion = $completion.Count -gt 0
        }
    })
    [pscustomobject]@{
        round = $round
        toolOffered = $offered
        toolSelected = $selected
        toolReadOutcomes = @($roundEvents | Where-Object { $_.stage -eq 'read-tool' -and $_.details.name -eq $Tool } |
            ForEach-Object { if ($_.outcome -in 'Started','Succeeded','Unavailable','TimedOut','Failed') { $_.outcome } else { 'Unknown' } })
        attempts = $attempts
    }
})
[pscustomobject]@{
    formatVersion = 1
    scope = 'current-lease'
    tool = $Tool
    timelineComplete = $timelineComplete
    previousLeasePresent = $null -ne $record.previousAttempt
    answerAvailability = if ($run.status -eq 'Succeeded' -and $null -ne $run.result) { 'AvailableForReview' } else { 'Unavailable' }
    semanticAssessment = 'NotPerformed'
    rounds = $rounds
}
