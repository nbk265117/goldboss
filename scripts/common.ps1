# Shared helpers for build/test/publish. Finds a .NET 10 SDK (PATH or %USERPROFILE%\.dotnet).
$ErrorActionPreference = 'Stop'
$script:Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$script:Solution = Join-Path $Root 'DofusCompanion.slnx'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Get-Dotnet {
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks -match '^10\.') {
                $env:DOTNET_ROOT = Split-Path $c
                return $c
            }
        }
    }
    throw "A .NET 10 SDK is required. Install it from https://dot.net or run: powershell -c `"& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0`""
}

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $exe $($arguments -join ' ')" }
}
