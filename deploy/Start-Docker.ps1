[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $PSScriptRoot '.env.docker'

if (-not (Test-Path -LiteralPath $envPath)) {
    $databasePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $demoPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'a!'
    @(
        "POSTGRES_PASSWORD=$databasePassword"
        "DEMO_PASSWORD=$demoPassword"
        'WEB_PORT=8080'
        'AI_PROVIDER=Fake'
    ) | Set-Content -LiteralPath $envPath -Encoding utf8
    Remove-Variable databasePassword, demoPassword
    Write-Host 'Created deploy/.env.docker. Read DEMO_PASSWORD in your local editor to log in.'
}

$composeArgs = @('compose', '--env-file', $envPath, '-f', (Join-Path $repoRoot 'compose.yaml'))
function Invoke-Compose {
    param([string[]]$DockerArguments)
    & docker @composeArgs @DockerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose failed (exit $LASTEXITCODE)."
    }
}

Invoke-Compose @('build', 'api', 'worker', 'web')
Invoke-Compose @('up', '-d', '--wait', 'database')
# Explicitly stop writers before applying migrations to an existing demo stack.
Invoke-Compose @('stop', 'web', 'worker', 'api')
Invoke-Compose @('run', '--rm', '--no-deps', 'db-init')
Invoke-Compose @('up', '-d', '--wait', '--wait-timeout', '180', 'api', 'worker', 'web')
Write-Host 'Docker demo is ready. The published address is shown below:'
Invoke-Compose @('port', 'web', '8080')
