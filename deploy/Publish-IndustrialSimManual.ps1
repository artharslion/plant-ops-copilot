param(
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [string]$EnvFile = "$PSScriptRoot/.env.docker",
    [string]$EvidenceRoot = "$PSScriptRoot/../artifacts/investigation-quality"
)
$ErrorActionPreference = 'Stop'
if ($BaseUrl -notmatch '^http://127\.0\.0\.1:\d+$') { throw 'Use the local workbench URL.' }
$plant = '10000000-0000-0000-0000-000000000001'
$asset = '30000000-0000-0000-0000-000000000001'
$point = '40000000-0000-0000-0000-000000000001'
$title = 'IndustrialSim P-101 仿真演示手册 v1'
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$csrf = Invoke-RestMethod "$BaseUrl/api/v1/auth/csrf" -WebSession $session
$passwordLine = Get-Content -LiteralPath $EnvFile | Where-Object { $_ -match '^DEMO_PASSWORD=' } | Select-Object -First 1
if (-not $passwordLine) { throw 'Local demo credential missing.' }
$null = Invoke-RestMethod "$BaseUrl/api/v1/auth/login" -Method Post -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -ContentType 'application/json' -Body (@{ userName = 'admin'; password = $passwordLine.Substring('DEMO_PASSWORD='.Length) } | ConvertTo-Json -Compress)
$passwordLine = $null
$csrf = Invoke-RestMethod "$BaseUrl/api/v1/auth/csrf" -WebSession $session
function Read-Api([string]$Path) {
    $response = Invoke-RestMethod "$BaseUrl/api/v1/$Path" -WebSession $session
    foreach ($item in $response) { $item }
}
$selection = Read-Api 'settings/ai'
$rules = @(Read-Api "assets/$asset/measurement-points/$point/alarm-rules")
$rule = @($rules | Where-Object { $_.name -eq 'P-101 high vibration' })
if ($rule.Count -ne 1 -or $rule[0].raiseAbove -ne 10 -or $rule[0].clearBelow -ne 8 -or $rule[0].raiseForMs -ne 3000 -or $rule[0].clearForMs -ne 5000 -or $rule[0].maxSampleGapMs -ne 2500 -or $rule[0].version -ne 1) {
    throw 'Current demo rule differs from the manual. Review the manual before publishing.'
}
$bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../docs/INDUSTRIALSIM_P101_DEMO_MANUAL.md'))
$sha = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
$page = Read-Api "documents?plantId=$plant&assetId=$asset&limit=200"
if ($page.nextCursor) { throw 'Document inventory is paged; inspect before publishing to avoid duplicates.' }
$matches = @($page.items | Where-Object { $_.title -eq $title -and $_.assetId -eq $asset })
if ($matches.Count -gt 1) { throw 'Duplicate manual documents require review.' }
$document = $matches | Select-Object -First 1
$version = $null
if ($document) {
    $versions = @(Read-Api "documents/$($document.id)/versions")
    $version = $versions | Where-Object { $_.sha256 -eq $sha -and $_.status -eq 'Published' -and $_.id -eq $document.currentPublishedVersionId } | Select-Object -First 1
}
if (-not $version) {
    $metadata = @{ plantId = $plant; assetId = $asset; title = $title; category = 'Maintenance'; visibility = 'PlantReaders'; version = $(if ($document) { $document.version } else { 0 }) } | ConvertTo-Json -Compress
    $boundary = 'plantops-' + [guid]::NewGuid().ToString('N')
    $content = "--$boundary`r`nContent-Disposition: form-data; name=`"metadata`"`r`nContent-Type: application/json`r`n`r`n$metadata`r`n--$boundary`r`nContent-Disposition: form-data; name=`"file`"; filename=`"INDUSTRIALSIM_P101_DEMO_MANUAL.md`"`r`nContent-Type: text/markdown`r`n`r`n" + [Text.Encoding]::UTF8.GetString($bytes) + "`r`n--$boundary--`r`n"
    $path = if ($document) { "documents/$($document.id)/versions" } else { 'documents' }
    $document = Invoke-RestMethod "$BaseUrl/api/v1/$path" -Method Post -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -ContentType "multipart/form-data; boundary=$boundary" -Body ([Text.Encoding]::UTF8.GetBytes($content))
    $version = @(Read-Api "documents/$($document.id)/versions") | Where-Object sha256 -eq $sha | Sort-Object versionNumber -Descending | Select-Object -First 1
    if (-not $version) { throw 'Uploaded manual hash not found.' }
    $document = Invoke-RestMethod "$BaseUrl/api/v1/documents/$($document.id)/publish" -Method Post -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -ContentType 'application/json' -Body (@{ version = $document.version; versionNumber = $version.versionNumber } | ConvertTo-Json -Compress)
}
$search = Read-Api "assets/$asset/document-evidence?query=$([uri]::EscapeDataString('P-101 VIBRATION_RMS Vibration 排查 记录 完成'))&limit=6"
$found = @($search.items | Where-Object { $_.documentId -eq $document.id -and $_.versionId -eq $version.id })
if ($found.Count -lt 1) { throw 'Published manual was not returned by authorized retrieval.' }
foreach ($chunk in $found) {
    $expanded = Read-Api "assets/$asset/document-evidence/$($chunk.documentId)/$($chunk.versionId)/$($chunk.chunkId)"
    if ($expanded.sha256 -ne $chunk.sha256) { throw 'Expanded evidence hash changed.' }
}
$after = Read-Api 'settings/ai'
if ($selection.provider -ne $after.provider -or $selection.version -ne $after.version) { throw 'AI selection changed during publication; inspect concurrent changes.' }
$null = New-Item -ItemType Directory -Force -Path $EvidenceRoot
@{ document = $document; sourceSha256 = $sha; version = $version; search = $search; aiSelection = $after; realModelRequested = $false } | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $EvidenceRoot 'manual-publication.json')
Write-Output "Manual published and authorized retrieval verified: $($document.id), version $($version.versionNumber), $($found.Count) chunks. AI selection preserved."
