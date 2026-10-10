param([Parameter(Mandatory)][string]$Run, [int]$Port = 0, [switch]$Investigate, [switch]$RealAi)
$ErrorActionPreference = 'Stop'
if ($Port -eq 0) {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $Port = $probe.LocalEndpoint.Port
    $probe.Stop()
}
if ($Run -cnotmatch '^[a-z0-9_]{1,40}$' -or $Port -lt 1024 -or $Port -gt 65535) { throw 'Invalid retained run or API port.' }
if (@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq $Port).Count -gt 0) { throw 'API port is occupied; existing process will be preserved.' }
$repoRoot = Split-Path $PSScriptRoot
$root = Join-Path $repoRoot "artifacts/opcua-demo/$Run"
$result = Get-Content -LiteralPath (Join-Path $root 'result.json') -Raw | ConvertFrom-Json
if (-not $result.Passed -or $result.Schema -ne "opcua_demo_$Run") { throw 'An accepted finite demo is required.' }
if (Test-Path -LiteralPath (Join-Path $root 'api-check.json')) { throw 'API check evidence already exists.' }
if ($Investigate -and (Test-Path -LiteralPath (Join-Path $root 'investigation-scope.json'))) { throw 'An investigation was already attempted for this run; preserve it and do not retry.' }
$savedValues = @{}
foreach ($name in @('ConnectionStrings__PlantOps','Demo__Password','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','Realtime__Enabled','Documents__StorageRoot')) {
    $savedValues[$name] = [Environment]::GetEnvironmentVariable($name)
}
$apiProcess = $null
$workerProcess = $null
$savedAi = @{}
foreach ($entry in Get-ChildItem Env: | Where-Object Name -like 'AI__*') { $savedAi[$entry.Name] = $entry.Value }
try {
    . "$PSScriptRoot/Use-LocalDatabase.ps1"
    $env:ConnectionStrings__PlantOps = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', "Database=plantops_tests;Search Path=opcua_demo_$Run;")
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:Realtime__Enabled = 'false'
    $env:Documents__StorageRoot = Join-Path $root 'documents'
    if ($RealAi -and -not $Investigate) { throw 'RealAi requires Investigate.' }
    if ($Investigate) {
        if ($RealAi) {
            . "$PSScriptRoot/Use-LocalAi.ps1"
            if ($env:AI__Provider -ne 'OpenAiCompatible') { throw 'A configured real provider is required.' }
        } else {
            $env:AI__Provider = 'Fake'
        }
        # Explicit finite demo settings; never edit the user's private configuration.
        $env:AI__RequestTimeoutSeconds = '55'
        $env:AI__RunTimeoutSeconds = '60'
        $env:AI__MaxRetries = '0'
        $env:AI__MaxModelRounds = '4'
        $env:AI__MaxToolCalls = '8'
        $env:AI__MaxInputTokens = '16000'
        $env:AI__MaxOutputTokens = '2000'
    }
    $base = "http://127.0.0.1:$Port"
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $apiDll = "$repoRoot/artifacts/opcua-demo-launch/$Run/build/bin/PlantOps.Api/debug/PlantOps.Api.dll"
    if (-not (Test-Path -LiteralPath $apiDll)) { $apiDll = "$repoRoot/src/PlantOps.Api/bin/Debug/net10.0/PlantOps.Api.dll" }
    foreach ($argument in @($apiDll, "--urls=$base", '--Logging:LogLevel:Default=Warning')) { $start.ArgumentList.Add($argument) }
    $apiProcess = [Diagnostics.Process]::Start($start)
    $apiOutput = $apiProcess.StandardOutput.ReadToEndAsync()
    $apiError = $apiProcess.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($apiProcess.HasExited) { throw 'API exited before readiness.' }
        try { $ready = (Invoke-WebRequest "$base/health/ready" -TimeoutSec 1 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'API readiness timed out.' }
    $point = $result.Configuration.PointId
    $asset = $result.Configuration.AssetId
    $from = [Uri]::EscapeDataString(([DateTimeOffset]$result.Start).ToUniversalTime().AddSeconds(-5).ToString('O'))
    $to = [Uri]::EscapeDataString(([DateTimeOffset]$result.End).ToUniversalTime().AddSeconds(1).ToString('O'))
    $trendPath = "$base/api/v1/assets/$asset/measurements?pointId=$point&from=$from&to=$to&bucket=raw"
    $anonymous = (Invoke-WebRequest $trendPath -SkipHttpErrorCheck).StatusCode
    if ($anonymous -ne 401) { throw 'Anonymous trend access was not rejected.' }
    $checks = @()
    foreach ($user in @('admin','viewer','engineer')) {
        $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $csrf = Invoke-RestMethod "$base/api/v1/auth/csrf" -WebSession $session
        $login = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -ContentType 'application/json' -Body (@{ userName = $user; password = $env:Demo__Password } | ConvertTo-Json -Compress) -SkipHttpErrorCheck
        if ($login.StatusCode -ne 204) { throw "Demo login failed for $user." }
        $trendResponse = Invoke-WebRequest $trendPath -WebSession $session -SkipHttpErrorCheck
        $alarmResponse = Invoke-WebRequest "$base/api/v1/alarms/$($result.Alarms[0].Id)" -WebSession $session -SkipHttpErrorCheck
        $expected = if ($user -eq 'engineer') { 404 } else { 200 }
        if ($trendResponse.StatusCode -ne $expected -or $alarmResponse.StatusCode -ne $expected) { throw "Role/plant read check failed for $user. Trend=$($trendResponse.StatusCode), Alarm=$($alarmResponse.StatusCode)." }
        if ($expected -eq 200) {
            $trend = $trendResponse.Content | ConvertFrom-Json
            $alarm = $alarmResponse.Content | ConvertFrom-Json
            if ($trend.samples.Count -ne $result.Readings.Count -or @($trend.samples | Where-Object sourceKind -ne 'OpcUa').Count -gt 0 -or $alarm.state -ne 'Recovered') { throw 'Public API differs from protocol/database evidence.' }
        }
        $checks += @{ User = $user; TrendStatus = [int]$trendResponse.StatusCode; AlarmStatus = [int]$alarmResponse.StatusCode }
        if ($Investigate -and $user -eq 'admin') {
            $csrf = Invoke-RestMethod "$base/api/v1/auth/csrf" -WebSession $session
            $before = Invoke-RestMethod "$base/api/v1/alarms/$($result.Alarms[0].Id)" -WebSession $session
            $queued = Invoke-RestMethod "$base/api/v1/alarms/$($before.id)/investigations" -Method Post -WebSession $session -Headers @{ 'X-CSRF-TOKEN' = $csrf.token }
            @{ Id = $queued.id; AlarmId = $before.id; Schema = $result.Schema; RealAi = [bool]$RealAi; MaxInvestigations = 1; RequestTimeoutSeconds = 55; RunTimeoutSeconds = 60; MaxRetries = 0 } | ConvertTo-Json | Set-Content (Join-Path $root 'investigation-scope.json')
            $workerStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
            $workerStart.WorkingDirectory = $repoRoot
            $workerStart.UseShellExecute = $false
            $workerStart.CreateNoWindow = $true
            $workerStart.RedirectStandardOutput = $true
            $workerStart.RedirectStandardError = $true
            foreach ($argument in @("$repoRoot/artifacts/opcua-demo-launch/$Run/build/bin/PlantOps.Worker/debug/PlantOps.Worker.dll", '--investigation-once', '--Logging:LogLevel:Default=Warning')) { $workerStart.ArgumentList.Add($argument) }
            $workerProcess = [Diagnostics.Process]::Start($workerStart)
            $workerOutput = $workerProcess.StandardOutput.ReadToEndAsync()
            $workerError = $workerProcess.StandardError.ReadToEndAsync()
            $workerDeadline = [DateTime]::UtcNow.AddSeconds(75)
            do { $finished = $workerProcess.WaitForExit(1000) } while (-not $finished -and [DateTime]::UtcNow -lt $workerDeadline)
            if (-not $finished) { $workerProcess.Kill(); $workerProcess.WaitForExit(5000) | Out-Null }
            $workerOutput.GetAwaiter().GetResult() | Set-Content (Join-Path $root 'investigation-worker.log')
            $workerError.GetAwaiter().GetResult() | Set-Content (Join-Path $root 'investigation-worker-error.log')
            $investigation = Invoke-RestMethod "$base/api/v1/investigations/$($queued.id)" -WebSession $session
            $investigation | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $root 'investigation.json')
            $expanded = @()
            foreach ($item in $investigation.evidence) {
                $expanded += Invoke-RestMethod "$base/api/v1/investigations/$($queued.id)/evidence/$($item.id)" -WebSession $session
            }
            $expanded | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $root 'expanded-evidence.json')
            $trigger = @($result.Readings | Where-Object Id -eq $before.triggerReadingId)
            $linked = @($expanded | Where-Object { $_.kind -eq 'TriggerReading' -and $_.resourceId -eq $before.triggerReadingId })
            $after = Invoke-RestMethod "$base/api/v1/alarms/$($before.id)" -WebSession $session
            $investigationCheck = @{
                Passed = $finished -and $workerProcess.ExitCode -eq 0 -and $investigation.status -eq 'Succeeded' -and $trigger.Count -eq 1 -and $linked.Count -eq 1 -and $before.version -eq $after.version -and $investigation.simulated -eq (-not [bool]$RealAi)
                Status = $investigation.status; FailureCode = $investigation.failureCode; AiRunId = $queued.id; AlarmId = $before.id
                TriggerReadingId = $before.triggerReadingId; DataSourceId = $trigger[0].DataSourceId; SourceMappingId = $trigger[0].SourceMappingId
                Protocol = 'OpcUa'; ObservationSimulated = $true; AiSimulated = $investigation.simulated
                RealProviderVerified = $RealAi -and $investigation.status -eq 'Succeeded' -and -not $investigation.simulated
                AlarmUnchanged = $before.version -eq $after.version; EvidenceExpanded = $expanded.Count; QualityAcceptance = $false; EstimatedCost = $null
            }
            $investigationCheck | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'investigation-check.json')
        }
        if ($Investigate -and $user -ne 'admin') {
            $otherRun = Invoke-WebRequest "$base/api/v1/investigations/$($queued.id)" -WebSession $session -SkipHttpErrorCheck
            $otherEvidence = Invoke-WebRequest "$base/api/v1/investigations/$($queued.id)/evidence/E1" -WebSession $session -SkipHttpErrorCheck
            if ($otherRun.StatusCode -ne 404 -or $otherEvidence.StatusCode -ne 404) { throw 'Other users must not read this investigation or evidence.' }
            $checks[-1].InvestigationStatus = [int]$otherRun.StatusCode
            $checks[-1].EvidenceStatus = [int]$otherEvidence.StatusCode
        }
    }
    @{ Passed = $true; AnonymousStatus = [int]$anonymous; Checks = $checks; Readings = $result.Readings.Count; BrowserVerified = $false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'api-check.json')
    Write-Output 'Retained OPC UA data is accessible through authorized trend/alarm APIs; cross-plant and anonymous reads are rejected.'
    if ($Investigate -and -not $investigationCheck.Passed) { throw 'Finite investigation failed; retained investigation evidence must be inspected. Do not automatically retry.' }
} finally {
    if ($workerProcess) {
        if (-not $workerProcess.HasExited) { $workerProcess.Kill(); $workerProcess.WaitForExit(5000) | Out-Null }
        $workerProcess.Dispose()
    }
    if ($apiProcess) {
        if (-not $apiProcess.HasExited) { $apiProcess.Kill(); $apiProcess.WaitForExit(5000) | Out-Null }
        $apiOutput.GetAwaiter().GetResult() | Add-Content -LiteralPath (Join-Path $root 'api.log')
        $apiError.GetAwaiter().GetResult() | Add-Content -LiteralPath (Join-Path $root 'api-error.log')
        $apiProcess.Dispose()
    }
    foreach ($name in $savedValues.Keys) { [Environment]::SetEnvironmentVariable($name, $savedValues[$name]) }
    foreach ($entry in @(Get-ChildItem Env: | Where-Object Name -like 'AI__*')) { Remove-Item -LiteralPath "Env:$($entry.Name)" }
    foreach ($name in $savedAi.Keys) { [Environment]::SetEnvironmentVariable($name, $savedAi[$name]) }
}
