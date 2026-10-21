[CmdletBinding()]
param(
    [string]$SimulatorRoot = 'D:/Repos/industrial-device-simulation-framework',
    [string]$Run = ('industrial_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Run-LocalOpcUaRuntime.ps1" -Run $Run -IndustrialSimRoot $SimulatorRoot
