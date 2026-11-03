param(
    [ValidatePattern('^[a-z0-9_]{1,40}$')][string]$Run,
    [ValidateSet('S01-normal-minute', 'S02-S03-missing-materials', 'S04-bad-current', 'S06-ingestion-gap', 'S11-document-injection')][string]$Scenario
)
# This historical paid-model harness validates v1/v2 against the active v3 service.
# Keep the historical C# tests for P8 migration; do not execute this entry point.
throw [System.InvalidOperationException]::new(
    'DEPRECATED: Test-RealInvestigationQuality.ps1 is disabled. LegacyInvestigationRealQualityTests uses the v1/v2 contract and cannot accept current v3 results. P8 must migrate this harness before it can run again. No configuration, database, report or model request has been started. See docs/INVESTIGATION_RESPONSE_V3.md.'
)
