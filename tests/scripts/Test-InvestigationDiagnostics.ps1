$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$reader = Join-Path $root 'deploy/Get-InvestigationDiagnostics.ps1'
$path = [IO.Path]::GetTempFileName()
function Read-Fixture($Run) {
    $json = $Run | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($path, $json)
    $before = (Get-FileHash -LiteralPath $path).Hash
    $result = & $reader -Path $path
    if ((Get-FileHash -LiteralPath $path).Hash -ne $before) { throw 'Reader changed evidence.' }
    if (($result | ConvertTo-Json -Depth 20) -like '*PRIVATE_*') { throw 'Reader exposed private payloads.' }
    return $result
}
function Event($Stage, $Outcome, $Details, $Round = 1, $Attempt = 1) {
    return @{ stage = $Stage; outcome = $Outcome; details = $Details; round = $Round; attempt = $Attempt }
}
try {
    $report = @{ run = @{
        status = 'Failed'; result = $null; evidence = @('PRIVATE_EVIDENCE');
        runRecord = @{
            omittedDiagnosticCount = 0
            requests = @(@{ toolBudget = @{ offeredToolCount = 2; offeredToolNames = @('search_documents','get_recent_alarms') }; messages = 'PRIVATE_REQUEST' })
            diagnostics = @(
                (Event 'dispatch' 'Started' @{})
                (Event 'response-headers' 'Received' @{ httpStatus = 200; elapsedMs = 10 })
                (Event 'model-response' 'Received' @{ toolCallCount = 1; content = 'PRIVATE_RESPONSE' })
                (Event 'tool-decision' 'Selected' @{ tools = @('get_recent_alarms') })
                (Event 'dispatch' 'Started' @{} 2 2)
                (Event 'response-headers' 'Received' @{ httpStatus = 502; elapsedMs = 20 } 2 2)
            )
        }
    } }
    $report.run.runRecord.requests += @{ toolBudget = @{ offeredToolCount = 0; offeredToolNames = @() } }
    $result = Read-Fixture $report
    if ($result.rounds[0].toolOffered -ne 'Offered' -or $result.rounds[0].toolSelected -ne 'NotSelected') { throw 'Missed tool selection was not separated from HTTP failure.' }
    if ($result.rounds[1].toolOffered -ne 'Withheld' -or $result.rounds[1].toolSelected -ne 'Unknown' -or
        $result.rounds[1].attempts[0].httpStatus -ne 502 -or $result.rounds[1].attempts[0].parsedCompletion) { throw '502 incorrectly established a model decision.' }
    if ($result.answerAvailability -ne 'Unavailable' -or $result.semanticAssessment -ne 'NotPerformed') { throw 'Failure counted as a semantic assessment.' }

    $record = $report.run.runRecord
    $record.requests = @(@{ toolBudget = @{ offeredToolCount = 5 } })
    $record.diagnostics = @(
        (Event 'dispatch' 'Started' @{})
        (Event 'response-headers' 'Received' @{ httpStatus = 502; elapsedMs = 10 })
        (Event 'dispatch' 'Started' @{} 1 2)
        (Event 'response-headers' 'Received' @{ httpStatus = 200; elapsedMs = 30 } 1 2)
        (Event 'model-response' 'Received' @{ toolCallCount = 1 } 1 2)
        (Event 'read-tool' 'Started' @{ name = 'search_documents' } 1 2)
        (Event 'read-tool' 'Unavailable' @{ name = 'search_documents' } 1 2)
    )
    $result = Read-Fixture $report.run
    if ($result.rounds[0].toolOffered -ne 'Unknown' -or $result.rounds[0].toolSelected -ne 'Selected' -or
        $result.rounds[0].attempts.Count -ne 2 -or $result.rounds[0].toolReadOutcomes[-1] -ne 'Unavailable') { throw 'Historical tool names, retries or failed reads misreported.' }

    $record.diagnostics = @((Event 'model-response' 'Received' @{ toolCallCount = 1 }), (Event 'read-tool' 'Started' @{ name = 'get_recent_alarms' }))
    $result = Read-Fixture $report
    if ($result.rounds[0].toolSelected -ne 'NotSelected') { throw 'Complete historical executed choices were not recognized.' }
    $record.omittedDiagnosticCount = 1
    $result = Read-Fixture $report
    if ($result.rounds[0].toolSelected -ne 'Unknown' -or $result.timelineComplete) { throw 'Truncated timeline treated as proof of absence.' }
    $record.omittedDiagnosticCount = 0
    $record.diagnostics = @((Event 'model-response' 'Received' @{ toolCallCount = 2 }), (Event 'read-tool' 'Started' @{ name = 'get_recent_alarms' }))
    if ((Read-Fixture $report).rounds[0].toolSelected -ne 'Unknown') { throw 'Unexecuted batched choice inferred from another tool.' }
    $record.diagnostics = @((Event 'tool-decision' 'Selected' @{ tools = @('search_documents') }), (Event 'tool-decision' 'Rejected' @{ arguments = 'PRIVATE_ARGUMENTS' }))
    if ((Read-Fixture $report).rounds[0].toolSelected -ne 'Selected') { throw 'Rejected selection confused with execution.' }

    $record.previousAttempt = @{ diagnostics = @((Event 'tool-decision' 'Selected' @{ tools = @('search_documents') })) }
    $record.diagnostics = @((Event 'model-response' 'Received' @{ toolCallCount = 0 }))
    $report.run.status = 'Succeeded'
    $report.run.result = @{ summary = 'PRIVATE_ADVICE' }
    $result = Read-Fixture $report
    if ($result.rounds[0].toolSelected -ne 'NotSelected' -or -not $result.previousLeasePresent -or
        $result.answerAvailability -ne 'AvailableForReview' -or $result.semanticAssessment -ne 'NotPerformed') { throw 'Previous lease or valid structure implied semantic quality.' }
    $result = Read-Fixture @{ Status = 'Succeeded'; Result = @{}; RunRecord = $record }
    if ($result.answerAvailability -ne 'AvailableForReview' -or $result.rounds.Count -ne 1) { throw 'PascalCase saved DTOs were not recognized.' }
    $result = Read-Fixture @{ status = 'Failed'; runRecord = $null }
    if ($result.timelineComplete -or $result.rounds.Count -ne 0) { throw 'Missing historical diagnostics invented.' }
} finally {
    [IO.File]::Delete($path)
}
Write-Output 'PASS: selection, availability, retries, failed reads and unknown historical fields remain separate; no configuration, model or database access.'
