param([Parameter(Mandatory)][string]$Run, [int]$Port = 0)
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
$savedValues = @{}
foreach ($name in @('ConnectionStrings__PlantOps','Demo__Password','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','Realtime__Enabled','Documents__StorageRoot')) {
    $savedValues[$name] = [Environment]::GetEnvironmentVariable($name)
}
$apiProcess = $null
try {
    . "$PSScriptRoot/Use-LocalDatabase.ps1"
    $env:ConnectionStrings__PlantOps = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', "Database=plantops_tests;Search Path=opcua_demo_$Run;")
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:Realtime__Enabled = 'false'
    $env:Documents__StorageRoot = Join-Path $root 'documents'
    $base = "http://127.0.0.1:$Port"
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("$repoRoot/src/PlantOps.Api/bin/Debug/net10.0/PlantOps.Api.dll", "--urls=$base", '--Logging:LogLevel:Default=Warning')) { $start.ArgumentList.Add($argument) }
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
    }
    @{ Passed = $true; AnonymousStatus = [int]$anonymous; Checks = $checks; Readings = $result.Readings.Count; BrowserVerified = $false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'api-check.json')
    Write-Output 'Retained OPC UA data is accessible through authorized trend/alarm APIs; cross-plant and anonymous reads are rejected.'
} finally {
    if ($apiProcess) {
        if (-not $apiProcess.HasExited) { $apiProcess.Kill(); $apiProcess.WaitForExit(5000) | Out-Null }
        $apiOutput.GetAwaiter().GetResult() | Add-Content -LiteralPath (Join-Path $root 'api.log')
        $apiError.GetAwaiter().GetResult() | Add-Content -LiteralPath (Join-Path $root 'api-error.log')
        $apiProcess.Dispose()
    }
    foreach ($name in $savedValues.Keys) { [Environment]::SetEnvironmentVariable($name, $savedValues[$name]) }
}
