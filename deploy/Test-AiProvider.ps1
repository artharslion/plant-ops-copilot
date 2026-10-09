param([switch]$Fake, [string]$Run = ('run_' + (Get-Date -Format 'yyyyMMdd_HHmmss')))
$ErrorActionPreference = 'Stop'
if ($Run -cnotmatch '^[a-z0-9_]{1,60}$') { throw 'Use a new lowercase run name (letters, digits, underscore).' }
$aiRepoRoot = Split-Path $PSScriptRoot
$aiReportPath = Join-Path $aiRepoRoot "artifacts/ai-provider/$Run/result.json"
if (Test-Path -LiteralPath $aiReportPath) { throw 'AI report already exists. Choose a new run name.' }
$aiSavedVariables = @{}
foreach ($aiEnvEntry in Get-ChildItem Env: | Where-Object Name -like 'AI__*') { $aiSavedVariables[$aiEnvEntry.Name] = $aiEnvEntry.Value }
try {
    if (-not $Fake) { . "$PSScriptRoot/Use-LocalAi.ps1" }
    & dotnet restore "$aiRepoRoot/spikes/PlantOps.Ai.Spike" --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'AI provider probe restore failed.' }
    & dotnet build "$aiRepoRoot/spikes/PlantOps.Ai.Spike" --nologo --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'AI provider probe build failed.' }
    $aiArguments = @("$aiRepoRoot/spikes/PlantOps.Ai.Spike/bin/Debug/net10.0/PlantOps.Ai.Spike.dll", '--output', $aiReportPath)
    if ($Fake) { $aiArguments += '--fake' }
    & dotnet @aiArguments
    if ($LASTEXITCODE -ne 0) { throw "AI provider probe failed. If created, its sanitized report is at $aiReportPath" }
    Write-Output "AI provider report: $aiReportPath"
} finally {
    foreach ($aiEnvEntry in @(Get-ChildItem Env: | Where-Object Name -like 'AI__*')) {
        Remove-Item -LiteralPath "Env:$($aiEnvEntry.Name)" -ErrorAction SilentlyContinue
    }
    foreach ($aiEnvName in $aiSavedVariables.Keys) {
        [Environment]::SetEnvironmentVariable($aiEnvName, $aiSavedVariables[$aiEnvName], 'Process')
    }
    Remove-Variable aiSavedVariables, aiEnvEntry, aiEnvName, aiArguments -ErrorAction SilentlyContinue
}
