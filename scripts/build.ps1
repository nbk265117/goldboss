<# Restores and builds the whole solution. Usage: .\scripts\build.ps1 [-Configuration Release] #>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
$dotnet = Get-Dotnet
Invoke-Checked $dotnet @('restore', $Solution)
Invoke-Checked $dotnet @('build', $Solution, '-c', $Configuration, '--no-restore', '-warnaserror')
Write-Host "Build succeeded ($Configuration)." -ForegroundColor Green
