param(
    [string]$SimulatorUrl = 'http://127.0.0.1:8081',
    [string]$SimulatorRoot = 'D:/Repos/industrial-device-simulation-framework',
    [string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
if ($SimulatorUrl -notmatch '^http://127\.0\.0\.1:\d+$') { throw 'Use the local simulator URL.' }
# The simulator control plane is a local development service with Auth disabled.
# Replaying this scenario creates real business alarms; it never invokes an LLM.
$yaml = Get-Content -LiteralPath (Join-Path $SimulatorRoot 'examples/scenarios/plantops-workbench.yaml') -Raw
$runtime = Invoke-RestMethod "$SimulatorUrl/api/v1/devices/plantops-p101/runtime"
if ($runtime.state -ne 'Running') { throw 'Start the simulator before replaying the scenario.' }
# IndustrialSim `at` is absolute host time. Rebase this known template without
# resetting the host clock, interrupting OPC UA, or changing Core semantics.
$anchor = [Math]::Ceiling([TimeSpan]::Parse($runtime.time, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds) + 2
$pattern = '(?m)^(\s*- at: )(\d+)s\s*$'
if ([regex]::Matches($yaml, $pattern).Count -ne 9) { throw 'Expected the nine-step workbench template; inspect YAML before replaying.' }
$yaml = [regex]::Replace($yaml, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $match.Groups[1].Value + ($anchor + [int]$match.Groups[2].Value).ToString([Globalization.CultureInfo]::InvariantCulture) + 's' })
if ($EvidenceRoot) {
    if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) { throw 'Evidence directory must already exist.' }
    if (Test-Path -LiteralPath "$EvidenceRoot/submitted-scenario.yaml") { throw 'Scenario evidence already exists; choose a new directory.' }
    $yaml | Set-Content -LiteralPath "$EvidenceRoot/submitted-scenario.yaml"
    @{ StartedAt = [DateTimeOffset]::UtcNow; AnchorSeconds = $anchor; RuntimeBefore = $runtime } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$EvidenceRoot/scenario-start.json"
}
Invoke-RestMethod "$SimulatorUrl/api/v1/devices/plantops-p101/scenario" -Method Post -ContentType 'application/yaml' -Body $yaml
