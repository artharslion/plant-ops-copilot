[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,30}$')][string]$Run,
    [string]$SimulatorRoot = 'D:/Repos/industrial-device-simulation-framework',
    [string]$ApiImage = 'plantops-api:docker-live-20261022',
    [string]$WorkerImage = 'plantops-worker:docker-live-20261022'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo "artifacts/docker-live/$Run"
$protocolRoot = Join-Path $repo "artifacts/opcua-runtime/$Run"
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $protocolRoot)) { throw 'Use a fresh run; no retry or overwrite of prior evidence.' }
$sim = (Resolve-Path -LiteralPath $SimulatorRoot).Path
$schema = "opcua_runtime_$Run"
try {
    $ai = Get-Content (Join-Path $repo 'appsettings.Ai.Local.json') -Raw | ConvertFrom-Json
    $database = Get-Content (Join-Path $repo 'artifacts/p4/database.local.json') -Raw | ConvertFrom-Json
    $demo = (Get-Content (Join-Path $repo 'appsettings.Local.json') -Raw | ConvertFrom-Json).Demo.Password
} catch { throw 'Cannot read private local settings; their contents are not printed.' }
if ($ai.AI.Provider -ne 'OpenAiCompatible' -or [string]::IsNullOrWhiteSpace($ai.AI.ApiKey) -or -not $demo) { throw 'Configured real provider and local demo password required.' }
foreach ($value in @($ai.AI.BaseUrl, $ai.AI.Model, $ai.AI.ApiKey, $database.Password, $demo)) {
    if ([string]$value -match '[\r\n]') { throw 'Private environment values must be single line.' }
}
foreach ($diskPath in @($repo, $env:LOCALAPPDATA)) {
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($diskPath)).AvailableFreeSpace -lt 2GB) { throw 'At least 2 GiB host disk reserve required.' }
}
docker exec plantops-p4-tests pg_isready -U plantops -d plantops_tests
if ($LASTEXITCODE -ne 0) { throw 'Dedicated test database must be ready.' }
New-Item -ItemType Directory -Path $root, $protocolRoot | Out-Null
$names = @{ Collector = "plantops-live-$Run-collector"; Api = "plantops-live-$Run-api"; Worker = "plantops-live-$Run-investigation" }
$created = [Collections.Generic.List[string]]::new()
$envFiles = @('collector.env','api.env','worker.env') | ForEach-Object { Join-Path $root $_ }
$connection = "Host=host.docker.internal;Port=$($database.Port);Database=plantops_tests;Username=plantops;Password=$($database.Password);Pooling=false;GSS Encryption Mode=Disable;Search Path=$schema"
$sdk = 'mcr.microsoft.com/dotnet/sdk:10.0@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510'
$started = [DateTimeOffset]::UtcNow
function Save-Json($Value, $File) { $Value | ConvertTo-Json -Depth 35 | Set-Content -LiteralPath (Join-Path $root $File) }
function Db-Scalar([string]$Sql) {
    $value = docker exec plantops-p4-tests psql -U plantops -d plantops_tests -v ON_ERROR_STOP=1 -tAc $Sql
    if ($LASTEXITCODE -ne 0) { throw 'Dedicated schema verification failed.' }
    return ($value | Out-String).Trim()
}
function Wait-Container([string]$Name, [int]$Seconds, [string]$LogFile) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $state = docker inspect $Name --format '{{.State.Running}}'
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect owned container.' }
        if ($state -eq 'false') { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($state -ne 'false') { docker stop --time 10 $Name | Out-Null }
    docker logs $Name 2>&1 | Out-File -LiteralPath (Join-Path $root $LogFile)
    return [int](docker inspect $Name --format '{{.State.ExitCode}}')
}
try {
    Save-Json @{ StartedAt=$started; Run=$Run; Schema=$schema; Database='plantops_tests'; MaxInvestigations=1; MaxRetries=0; MaxModelRounds=4; MaxToolCalls=8; MaxInputTokens=16000; MaxOutputTokens=2000; RequestTimeoutSeconds=120; RunTimeoutSeconds=180; ObservationSimulated=$true; Model=$ai.AI.Model; PaidRequestNotYetSent=$true } 'scope.json'
    @('DOTNET_ENVIRONMENT=Development', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', "PLANTOPS_RUNTIME_CONNECTION=$connection", "PLANTOPS_RUNTIME_DEMO_PASSWORD=$demo") | Set-Content -LiteralPath $envFiles[0]
    $common = @('ASPNETCORE_ENVIRONMENT=Development', "ConnectionStrings__PlantOps=$connection", 'Realtime__Enabled=false', 'Telemetry__SimulationEnabled=false', 'Documents__StorageRoot=/var/lib/plantops/documents', 'AI__Provider=OpenAiCompatible', "AI__Model=$($ai.AI.Model)", 'Logging__LogLevel__Default=Warning')
    $common | Set-Content -LiteralPath $envFiles[1]
    @($common; "AI__BaseUrl=$($ai.AI.BaseUrl)"; "AI__ApiKey=$($ai.AI.ApiKey)"; 'AI__ReasoningEffort=low'; 'AI__StreamResponses=true'; 'AI__MaxRetries=0'; 'AI__RequestTimeoutSeconds=120'; 'AI__RunTimeoutSeconds=180'; 'AI__MaxModelRounds=4'; 'AI__MaxToolCalls=8'; 'AI__MaxInputTokens=16000'; 'AI__MaxOutputTokens=2000') | Set-Content -LiteralPath $envFiles[2]
    $collectorArgs = @('run','-d','--name',$names.Collector,'--env-file',$envFiles[0],'-w','/workspace/plantops',
        '--mount',"type=bind,source=$repo/spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0,target=/workspace/plantops/spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0,readonly",
        '--mount',"type=bind,source=$sim/examples,target=/workspace/industrial/examples,readonly",
        '--mount',"type=bind,source=$protocolRoot,target=/workspace/plantops/artifacts/opcua-runtime/$Run",
        $sdk,'dotnet','spikes/PlantOps.OpcUa.Spike/bin/Debug/net10.0/PlantOps.OpcUa.Spike.dll','industrial-scenario',"artifacts/opcua-runtime/$Run",'/workspace/industrial')
    $collectorId = docker @collectorArgs
    if ($LASTEXITCODE -ne 0) { throw 'Collector container could not start.' }
    $created.Add($names.Collector)
    $collectorExit = Wait-Container $names.Collector 175 'collector.log'
    if ($collectorExit -ne 0) { throw 'Docker OPC UA collection failed before any model request; inspect collector.log.' }
    $protocol = Get-Content (Join-Path $protocolRoot 'result.json') -Raw | ConvertFrom-Json
    if (-not $protocol.Passed -or $protocol.Schema -ne $schema -or $protocol.Alarms.Count -ne 1) { throw 'Accepted three-point protocol evidence required before investigation.' }
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start(); $port = $portProbe.LocalEndpoint.Port; $portProbe.Stop()
    $base = "http://127.0.0.1:$port"
    $documents = Join-Path $protocolRoot 'documents'
    docker run -d --name $names.Api --env-file $envFiles[1] -p "127.0.0.1:${port}:8080" --mount "type=bind,source=$documents,target=/var/lib/plantops/documents" $ApiImage | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker API could not start.' }
    $created.Add($names.Api)
    $ready = $false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        try { $ready = (Invoke-WebRequest "$base/health/ready" -TimeoutSec 1 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Docker API readiness failed before any model request.' }
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $csrf = Invoke-RestMethod "$base/api/v1/auth/csrf" -WebSession $session
    $login = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -WebSession $session -Headers @{'X-CSRF-TOKEN'=$csrf.token} -ContentType 'application/json' -Body (@{userName='admin';password=$demo} | ConvertTo-Json -Compress) -SkipHttpErrorCheck
    if ($login.StatusCode -ne 204) { throw 'Dedicated Docker API login failed before any model request.' }
    $alarmId = $protocol.Alarms[0].Id
    $before = Invoke-RestMethod "$base/api/v1/alarms/$alarmId" -WebSession $session
    $workBefore = Db-Scalar "SELECT COUNT(*) FROM `"$schema`".`"WorkOrders`""
    if ((Db-Scalar "SELECT COUNT(*) FROM `"$schema`".`"AiRuns`"") -ne '0') { throw 'This schema already contains an investigation; refusing another attempt.' }
    $csrf = Invoke-RestMethod "$base/api/v1/auth/csrf" -WebSession $session
    # Persist the one-attempt marker BEFORE the write. Never retry an ambiguous POST or start a second Worker.
    Save-Json @{ AttemptMarkedAt=[DateTimeOffset]::UtcNow; AlarmId=$alarmId; MaxInvestigations=1; AutoRetry=$false } 'investigation-attempt.json'
    $queued = Invoke-RestMethod "$base/api/v1/alarms/$alarmId/investigations" -Method Post -WebSession $session -Headers @{'X-CSRF-TOKEN'=$csrf.token}
    Save-Json $queued 'investigation-queued.json'
    docker run -d --name $names.Worker --no-healthcheck --env-file $envFiles[2] --mount "type=bind,source=$documents,target=/var/lib/plantops/documents" $WorkerImage --investigation-once | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'One-shot Docker investigation Worker failed to start; do not retry automatically.' }
    $created.Add($names.Worker)
    $workerExit = Wait-Container $names.Worker 195 'investigation-worker.log'
    $runResult = Invoke-RestMethod "$base/api/v1/investigations/$($queued.id)" -WebSession $session
    Save-Json $runResult 'investigation.json'
    $expanded = @()
    foreach ($item in $runResult.evidence) { $expanded += Invoke-RestMethod "$base/api/v1/investigations/$($queued.id)/evidence/$($item.id)" -WebSession $session }
    Save-Json $expanded 'expanded-evidence.json'
    $after = Invoke-RestMethod "$base/api/v1/alarms/$alarmId" -WebSession $session
    $count = Db-Scalar "SELECT COUNT(*) FROM `"$schema`".`"AiRuns`""
    $attempts = Db-Scalar "SELECT `"Attempts`" FROM `"$schema`".`"AiRuns`""
    $workAfter = Db-Scalar "SELECT COUNT(*) FROM `"$schema`".`"WorkOrders`""
    $trigger = @($protocol.Readings | Where-Object Id -eq $before.triggerReadingId)
    $linked = @($expanded | Where-Object { $_.kind -eq 'TriggerReading' -and $_.resourceId -eq $before.triggerReadingId })
    $imageEvidence = @()
    foreach ($name in $created) { $imageEvidence += @{ Name=$name; Id=(docker inspect $name --format '{{.Id}}'); Image=(docker inspect $name --format '{{.Image}}') } }
    $passed = $workerExit -eq 0 -and $runResult.status -eq 'Succeeded' -and -not $runResult.simulated -and $runResult.provider -eq 'OpenAiCompatible' -and $count -eq '1' -and $attempts -eq '1' -and $trigger.Count -eq 1 -and $linked.Count -eq 1 -and $before.version -eq $after.version -and $workBefore -eq $workAfter
    Save-Json @{ Passed=$passed; StartedAt=$started; CompletedAt=[DateTimeOffset]::UtcNow; Docker=$true; Containers=$imageEvidence; ObservationSimulated=$true; RealOpcUa=$true; ProtocolReadings=$protocol.Readings.Count; AiRunId=$queued.id; AlarmId=$alarmId; TriggerReadingId=$before.triggerReadingId; Status=$runResult.status; FailureCode=$runResult.failureCode; Provider=$runResult.provider; Model=$runResult.model; AiSimulated=$runResult.simulated; Investigations=$count; Attempts=$attempts; ModelRounds=$runResult.modelRounds; ToolCalls=$runResult.toolCalls; InputTokenAccounting=$runResult.inputTokens; OutputTokenAccounting=$runResult.outputTokens; TokenAccountingIsNotGuaranteedBilledUsage=$true; EstimatedCost=$null; AlarmUnchanged=($before.version -eq $after.version); WorkOrdersUnchanged=($workBefore -eq $workAfter); TriggerEvidenceLinked=($linked.Count -eq 1); QualityAcceptance=$false; AutoRetry=$false } 'result.json'
    if (-not $passed) { throw 'One real investigation did not pass; evidence retained, no automatic retry.' }
    Write-Output "Docker + IndustrialSim + one real LLM investigation accepted: $root/result.json"
} catch {
    Save-Json @{ Passed=$false; At=[DateTimeOffset]::UtcNow; Message=$_.Exception.Message; NoAutomaticRetry=$true } 'failure.json'
    throw
} finally {
    foreach ($name in $created) {
        docker logs $name 2>&1 | Out-File -LiteralPath (Join-Path $root "$name.log")
        docker rm -f $name | Out-Null
    }
    foreach ($file in $envFiles) { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
    Remove-Variable ai,database,demo,connection,common -ErrorAction SilentlyContinue
}
