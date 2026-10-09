param(
    [string]$SimulatorRoot = 'D:/Repos/industrial-device-simulation-framework',
    [string]$Run = ('run_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
if ($Run -cnotmatch '^[a-z0-9_]{1,40}$') { throw 'Use a new lowercase run name (letters, digits, underscore).' }
$simRoot = (Resolve-Path -LiteralPath $SimulatorRoot).Path
$outputRoot = Join-Path $repoRoot "artifacts/opcua-demo/$Run"
$launchRoot = Join-Path $repoRoot "artifacts/opcua-demo-launch/$Run"
if ((Test-Path -LiteralPath $outputRoot) -or (Test-Path -LiteralPath $launchRoot)) { throw 'Run already exists. Choose a new run.' }
function Test-DemoListener { return @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq 48481).Count -gt 0 }
if (Test-DemoListener) { throw 'Port 48481 is already in use; existing processes will be preserved.' }
foreach ($diskPath in @($repoRoot, $env:LOCALAPPDATA)) {
    if (([IO.DriveInfo]::new([IO.Path]::GetPathRoot($diskPath))).AvailableFreeSpace -lt 2GB) { throw 'Demo needs at least 2 GiB reserve on database/evidence host disks.' }
}
$savedConnection = $env:ConnectionStrings__PlantOps
$savedPassword = $env:Demo__Password
$savedEnvironment = $env:ASPNETCORE_ENVIRONMENT
$savedDotnetEnvironment = $env:DOTNET_ENVIRONMENT
$savedLocation = Get-Location
$simProcess = $null
try {
    Set-Location -LiteralPath $repoRoot
    . "$PSScriptRoot/Use-LocalDatabase.ps1"
    if (-not $env:Demo__Password) { throw 'Configure a local Demo.Password before running the demo.' }
    $env:ConnectionStrings__PlantOps = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', "Database=plantops_tests;Search Path=opcua_demo_$Run;")
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    & dotnet build "$simRoot/src/IndustrialSim.Cli" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Simulator build failed.' }
    & dotnet build "$repoRoot/src/PlantOps.Worker" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
    New-Item -ItemType Directory -Path $launchRoot | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $simRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("$simRoot/src/IndustrialSim.Cli/bin/Debug/net10.0/IndustrialSim.Cli.dll", 'scenario', 'run',
        "$simRoot/examples/scenarios/vibration-plantops.yaml", '--config', "$simRoot/examples/devices/vibration-plantops.yaml", '--duration', '80')) {
        $start.ArgumentList.Add($argument)
    }
    $simProcess = [Diagnostics.Process]::Start($start)
    $simOutput = $simProcess.StandardOutput.ReadToEndAsync()
    $simError = $simProcess.StandardError.ReadToEndAsync()
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($simProcess.HasExited) { throw 'Simulator exited before listening; inspect retained launch logs.' }
        if (Test-DemoListener) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $readyDeadline)
    if (-not (Test-DemoListener)) { throw 'Simulator readiness timed out.' }
    & dotnet "$repoRoot/src/PlantOps.Worker/bin/Debug/net10.0/PlantOps.Worker.dll" --opcua-demo "--OpcUaDemo:Run=$Run" '--Logging:LogLevel:Default=Warning'
    if ($LASTEXITCODE -ne 0) { throw "Demo failed. Evidence is retained under $outputRoot." }
    Write-Output "Accepted finite demo: $outputRoot/result.json"
    Write-Output "To inspect in the existing UI, use Database=plantops_tests and Search Path=opcua_demo_$Run for an API instance. Do not start a normal Worker for this retained run."
} finally {
    if ($simProcess) {
        if (-not $simProcess.HasExited) { $simProcess.Kill(); $simProcess.WaitForExit(5000) | Out-Null }
        $simOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $launchRoot 'simulator.log')
        $simError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $launchRoot 'simulator-error.log')
        $simProcess.Dispose()
    }
    $env:ConnectionStrings__PlantOps = $savedConnection
    $env:Demo__Password = $savedPassword
    $env:ASPNETCORE_ENVIRONMENT = $savedEnvironment
    $env:DOTNET_ENVIRONMENT = $savedDotnetEnvironment
    Set-Location -LiteralPath $savedLocation
}
