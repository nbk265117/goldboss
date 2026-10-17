<#
 Runs every automated test project and the localization check. Usage: .\scripts\test.ps1 [-SkipOcr]
 OCR tests need the Windows OCR engine with the fr-FR language installed.
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$SkipOcr)
. (Join-Path $PSScriptRoot 'common.ps1')
$dotnet = Get-Dotnet
$results = Join-Path $Root 'artifacts\test-results'
New-Item -ItemType Directory -Force $results | Out-Null
$projects = @('DofusCompanion.Core.Tests', 'DofusCompanion.Infrastructure.Tests')
if (-not $SkipOcr) { $projects += 'DofusCompanion.Ocr.Tests' }
foreach ($p in $projects) {
    Invoke-Checked $dotnet @('test', (Join-Path $Root "tests\$p"), '-c', $Configuration, '--logger', "trx;LogFileName=$p.trx", '--results-directory', $results)
}
# The Microsoft Store "python" alias exists but does not run; only use an interpreter that actually works.
$py = @('py', 'python') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } |
    Where-Object { & $_.Source -c "print(1)" 2>$null; $LASTEXITCODE -eq 0 } | Select-Object -First 1
if ($py) { Invoke-Checked $py.Source @('-I', (Join-Path $PSScriptRoot 'check-localization.py')) }
else { Write-Warning 'Python not found: localization key check skipped.' }
Write-Host "All tests passed. Results: $results" -ForegroundColor Green
