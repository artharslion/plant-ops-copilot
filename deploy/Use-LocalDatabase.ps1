# Dot-source this file in each terminal that runs PlantOps.
$localEnvPath = Join-Path $PSScriptRoot '.env'
if (-not (Test-Path -LiteralPath $localEnvPath)) {
    throw 'Copy deploy/.env.example to deploy/.env and set a local password first.'
}
$passwordLine = Get-Content -LiteralPath $localEnvPath | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
if (-not $passwordLine) { throw 'POSTGRES_PASSWORD is missing from deploy/.env.' }
$localDatabasePassword = $passwordLine.Substring('POSTGRES_PASSWORD='.Length).Trim()
if ($localDatabasePassword -notmatch '^[A-Za-z0-9_-]{16,128}$' -or $localDatabasePassword -eq 'replace-with-a-local-password') {
    throw 'Use a 16–128 character local password containing letters, digits, underscores or hyphens.'
}
$env:ConnectionStrings__PlantOps = "Host=127.0.0.1;Port=5432;Database=plantops;Username=plantops;Password=$localDatabasePassword;Timeout=3;Command Timeout=5"
Remove-Variable localDatabasePassword, passwordLine
$localDemoConfigPath = Join-Path $PSScriptRoot '../appsettings.Local.json'
if (Test-Path -LiteralPath $localDemoConfigPath) {
    $localDemoConfig = Get-Content -LiteralPath $localDemoConfigPath -Raw | ConvertFrom-Json
    if ($localDemoConfig.Demo.Password) { $env:Demo__Password = $localDemoConfig.Demo.Password }
    Remove-Variable localDemoConfig
}
