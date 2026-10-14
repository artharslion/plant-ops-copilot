$ErrorActionPreference = 'Stop'
$run = 'artifacts/p2/budget-tests_' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $run | Out-Null
$fixture = @{
    CheckedAtUtc = '2026-10-08T00:00:00Z'
    DefaultDockerVhd = @{ HostDrive = 'C' }
    HostDisks = @(@{ Name = 'C'; FreeBytes = 100000000000 }, @{ Name = 'D'; FreeBytes = 100 })
    Database = @{ schema = 'synthetic'; rawRows = 1000; rawBytes = 600000; minuteRows = 1000; minuteBytes = 200000; outboxBytes = 300000; pendingOutbox = 10; sentOutbox = 990; allDatabaseBytes = 1000000000 }
}
$inputPath = Join-Path $run 'fixture.json'
$outputPath = Join-Path $run 'result.json'
$fixture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inputPath
& "$PSScriptRoot/Get-CapacityBudget.ps1" -SnapshotPath $inputPath -OutputPath $outputPath
$result = Get-Content $outputPath -Raw | ConvertFrom-Json
if ($result.BudgetComplete -or $result.FitsSnapshot -or $result.CapacityPassed -or $result.MissingPeakInputs.Count -ne 5) { throw 'Missing peaks must fail closed.' }
if ($result.ForecastBytes.EightDayRaw -ne 41472000000 -or $result.ForecastBytes.NinetyDayMinutes -ne 2592000000) { throw 'Eight-day / ninety-day arithmetic is wrong.' }
$peaks = @{ WalPeakAdditionalBytes = 100; MaintenanceAdditionalBytes = 200; RestoreAdditionalBytes = 300; DocumentAdditionalBytes = 400; BackupAdditionalBytes = 1000 }
& "$PSScriptRoot/Get-CapacityBudget.ps1" -SnapshotPath $inputPath -OutputPath $outputPath @peaks
$result = Get-Content $outputPath -Raw | ConvertFrom-Json
if (-not $result.BudgetComplete -or $result.FitsSnapshot) { throw 'C drive cannot cover a D drive shortage.' }
$c = $result.Drives | Where-Object Drive -eq 'C'
if ($c.ReserveBytes -ne [math]::Ceiling((1000000000 + $c.AdditionalAllocationBytes) * .3)) { throw 'Reserve must include existing history without adding it twice to allocations.' }
& "$PSScriptRoot/Get-CapacityBudget.ps1" -SnapshotPath $inputPath -OutputPath $outputPath -BackupDrive C @peaks
$result = Get-Content $outputPath -Raw | ConvertFrom-Json
if ($result.Drives.Count -ne 1 -or -not $result.FitsSnapshot -or $result.CapacityPassed) { throw 'Same-drive backup must share allocation and remain only a forecast.' }
$fixture.Database.Remove('rawRows')
$fixture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inputPath
$rejected = $false
try { & "$PSScriptRoot/Get-CapacityBudget.ps1" -SnapshotPath $inputPath -OutputPath $outputPath } catch { $rejected = $true }
if (-not $rejected) { throw 'Old snapshots with estimated row counts must be rejected.' }
Write-Output 'Capacity budget checks passed: missing peaks, row arithmetic, separate/shared disks, reserve, old snapshot rejection.'
