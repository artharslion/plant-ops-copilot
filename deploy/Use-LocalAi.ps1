# Dot-source in the terminal used to launch the API, Worker or provider probe.
param([string]$Path = (Join-Path $PSScriptRoot '../appsettings.Ai.Local.json'))
$aiLocalSettings = $null
try {
    if (-not (Test-Path -LiteralPath $Path)) { throw 'Missing local AI settings.' }
    $aiLocalSettings = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if (-not $aiLocalSettings.AI) { throw 'Missing AI section.' }
    $aiLocalNames = @('Provider', 'BaseUrl', 'Model', 'ApiKey', 'ReasoningEffort', 'RequestTimeoutSeconds', 'RunTimeoutSeconds',
        'MaxModelRounds', 'MaxToolCalls', 'MaxInputTokens', 'MaxOutputTokens', 'MaxRetries', 'MaxResponseBytes', 'StreamResponses')
    foreach ($aiLocalName in $aiLocalNames) {
        $aiLocalProperty = $aiLocalSettings.AI.PSObject.Properties[$aiLocalName]
        # Missing and empty settings clear stale values in this terminal.
        if ($aiLocalProperty) {
            $aiLocalValue = [string]$aiLocalProperty.Value
            [Environment]::SetEnvironmentVariable("AI__$aiLocalName", $aiLocalValue, 'Process')
        } else {
            Remove-Item -LiteralPath "Env:AI__$aiLocalName" -ErrorAction SilentlyContinue
        }
    }
} catch {
    # A JSON parser error can contain the original line, including the key.
    throw 'Unable to load local AI settings. Check the file and JSON format in your local editor; its contents are not printed.'
} finally {
    Remove-Variable aiLocalSettings, aiLocalNames, aiLocalName, aiLocalProperty, aiLocalValue -ErrorAction SilentlyContinue
}
