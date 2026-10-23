param(
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [guid]$PlantId = '10000000-0000-0000-0000-000000000001',
    [guid]$AssetId = '30000000-0000-0000-0000-000000000001',
    [string]$EnvFile = "$PSScriptRoot/.env.docker"
)
$ErrorActionPreference = 'Stop'
if ($BaseUrl -notmatch '^http://127\.0\.0\.1:\d+$') { throw 'Use the local workbench URL.' }
$passwordLine = Get-Content -LiteralPath $EnvFile | Where-Object { $_ -match '^DEMO_PASSWORD=' } | Select-Object -First 1
if (-not $passwordLine) { throw 'DEMO_PASSWORD is required in the local Docker env file.' }
$password = $passwordLine.Substring('DEMO_PASSWORD='.Length)
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$csrf = Invoke-RestMethod "$BaseUrl/api/v1/auth/csrf" -WebSession $session
$null = Invoke-RestMethod "$BaseUrl/api/v1/auth/login" -Method Post -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -ContentType 'application/json' -Body (@{ userName = 'admin'; password = $password } | ConvertTo-Json -Compress)
$password = $null
$csrf = Invoke-RestMethod "$BaseUrl/api/v1/auth/csrf" -WebSession $session
function Read-Api([string]$Path) {
    $response = Invoke-RestMethod "$BaseUrl/api/v1/$Path" -WebSession $session
    foreach ($item in $response) { $item }
}
function Write-Api([string]$Path, [string]$Method, $Body) {
    Invoke-RestMethod "$BaseUrl/api/v1/$Path" -Method $Method -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 12 -Compress)
}
$sources = @(Read-Api "plants/$PlantId/data-sources")
$points = @(Read-Api "assets/$AssetId/measurement-points")
if (@($points | Where-Object code -eq 'VIBRATION_RMS').Count -ne 1) { throw 'Expected one existing VIBRATION_RMS point.' }
$beforeMappings = @($points | ForEach-Object { Read-Api "assets/$AssetId/measurement-points/$($_.id)/source-mappings" })
$evidence = Join-Path (Split-Path $PSScriptRoot) ('artifacts/industrial-sim-workbench/' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff'))
$null = New-Item -ItemType Directory -Path $evidence
@{ AssetId = $AssetId; PlantId = $PlantId; Sources = $sources; Points = $points; Mappings = $beforeMappings } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath "$evidence/before.json"
$sourceName = 'IndustrialSim P-101 · 仿真设备 / 真实 OPC UA'
$source = @($sources | Where-Object { $_.kind -eq 'OpcUa' -and $_.opcUa.endpointReference -eq 'industrial-sim-workbench' })
if ($source.Count -gt 1) { throw 'Duplicate workbench sources; inspect configuration before continuing.' }
if ($source.Count -eq 0) {
    $source = Write-Api "plants/$PlantId/data-sources" Post @{ name = $sourceName; kind = 'OpcUa'; enabled = $true; opcUa = @{ endpointReference = 'industrial-sim-workbench'; publishingIntervalMs = 1000 } }
} else {
    $source = $source[0]
    if (-not $source.enabled) {
        $source = Write-Api "plants/$PlantId/data-sources/$($source.id)" Patch @{ name = $source.name; kind = 'OpcUa'; enabled = $true; version = $source.version; opcUa = $source.opcUa }
    }
}
$configured = @()
foreach ($target in @(
    @{ Code = 'VIBRATION_RMS'; Name = '振动 RMS'; Quantity = 'Vibration'; Unit = 'mm/s'; Node = 'vibration' },
    @{ Code = 'TEMPERATURE'; Name = 'IndustrialSim 温度'; Quantity = 'Temperature'; Unit = 'degC'; Node = 'temperature' },
    @{ Code = 'REFERENCE_TEMPERATURE'; Name = 'IndustrialSim 参考温度'; Quantity = 'Temperature'; Unit = 'degC'; Node = 'referenceTemperature' }
)) {
    $point = @($points | Where-Object code -eq $target.Code)
    if ($point.Count -gt 1) { throw "Duplicate point code $($target.Code)." }
    if ($point.Count -eq 0) {
        $point = Write-Api "assets/$AssetId/measurement-points" Post @{ code = $target.Code; name = $target.Name; quantity = $target.Quantity; canonicalUnit = $target.Unit; expectedIntervalMs = 1000; staleAfterMs = 5000; enabled = $true }
    } else { $point = $point[0] }
    if (-not $point.enabled -or $point.quantity -ne $target.Quantity -or $point.canonicalUnit -ne $target.Unit) { throw "Point $($target.Code) has incompatible metadata." }
    $path = "assets/$AssetId/measurement-points/$($point.id)/source-mappings"
    $mappings = @(Read-Api $path)
    $node = 'plantops-p101/' + $target.Node
    $matching = @($mappings | Where-Object { $_.dataSourceId -eq $source.id -and ($_.sourceParameters | ConvertFrom-Json).Identifier -eq $node })
    if ($matching.Count -gt 1) { throw "Duplicate IndustrialSim mappings for $($target.Code)." }
    foreach ($old in @($mappings | Where-Object { $_.enabled -and ($matching.Count -eq 0 -or $_.id -ne $matching[0].id) })) {
        $body = @{ dataSourceId = $old.dataSourceId; sourceKey = $old.sourceKey; rawUnit = $old.rawUnit; scale = $old.scale; offset = $old.offset; enabled = $false; version = $old.version }
        $oldSource = $sources | Where-Object id -eq $old.dataSourceId
        if ($oldSource.kind -eq 'OpcUa') { $body.opcUa = $old.sourceParameters | ConvertFrom-Json }
        $null = Write-Api "$path/$($old.id)" Patch $body
    }
    $body = @{ dataSourceId = $source.id; sourceKey = 'industrial-sim'; rawUnit = $target.Unit; scale = 1; offset = 0; enabled = $true; opcUa = @{ namespaceUri = 'urn:industrial-sim:runtime'; identifierType = 'String'; identifier = $node; samplingIntervalMs = 1000; queueSize = 10; samplingMode = 'Periodic' } }
    if ($matching.Count -eq 0) { $mapping = Write-Api $path Post $body }
    elseif (-not $matching[0].enabled) { $body.version = $matching[0].version; $mapping = Write-Api "$path/$($matching[0].id)" Patch $body }
    else { $mapping = $matching[0] }
    $configured += @{ Code = $target.Code; PointId = $point.id; MappingId = $mapping.id; Node = $node }
}
@{ AssetId = $AssetId; PlantId = $PlantId; SourceId = $source.id; Points = $configured; ObservationSimulated = $true; Protocol = 'OpcUa'; ConfiguredAt = [DateTimeOffset]::UtcNow } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$evidence/configured.json"
Write-Output "IndustrialSim mappings configured through authenticated APIs. Evidence: $evidence"
