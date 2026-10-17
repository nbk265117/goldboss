<#
 Produces a self-contained Windows x64 release folder (no .NET installation needed on the target PC)
 and a zip next to it, plus the Velopack installer / update packages.
 Usage: .\scripts\publish.ps1 [-SkipTests] [-SkipOcr]
#>
param([switch]$SkipTests, [switch]$SkipOcr)
. (Join-Path $PSScriptRoot 'common.ps1')
$dotnet = Get-Dotnet
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') -SkipOcr:$SkipOcr }

$version = ([xml](Get-Content (Join-Path $Root 'Directory.Build.props'))).Project.PropertyGroup.Version
$out = Join-Path $Root "artifacts\publish\DofusGoldBoss-$version-win-x64"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Invoke-Checked $dotnet @('publish', (Join-Path $Root 'src\DofusCompanion.App\DofusCompanion.App.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    # Single file: every managed assembly (ours and third-party) lives inside DofusGoldBoss.exe, so signing that
    # one file covers them. Native runtime files stay beside it and are already signed by Microsoft.
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false',
    '-p:PublishReadyToRun=true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false',
    '-o', $out)

# Ship the schema and seed packages as readable files too (the app embeds them; these are for contributors).
New-Item -ItemType Directory -Force (Join-Path $out 'data') | Out-Null
Copy-Item -Recurse -Force (Join-Path $Root 'data\schemas') (Join-Path $out 'data\schemas')
Copy-Item -Recurse -Force (Join-Path $Root 'data\seed') (Join-Path $out 'data\seed')
# DofusDB monsters / zones and their pictures, applied by the app at first start (see ApplyBundledDofusDbAsync).
Copy-Item -Recurse -Force (Join-Path $Root 'data\dofusdb') (Join-Path $out 'data\dofusdb')
Copy-Item (Join-Path $Root 'README.md'), (Join-Path $Root 'LICENSE-NOTICES.md'), (Join-Path $Root 'CHANGELOG.md') $out -ErrorAction SilentlyContinue
Get-ChildItem $out -Recurse -Include *.pdb | Remove-Item -Force

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Published $out ($size MB) and $zip" -ForegroundColor Green

# Installer + automatic-update packages (Velopack, local tool from dotnet-tools.json) in artifacts\releases.
# The pack id differs from the data folder name: Velopack installs to %LOCALAPPDATA%\<packId> and removes that
# folder on uninstall, while the user's data stays in %LOCALAPPDATA%\DofusGoldBoss.
$releases = Join-Path $Root 'artifacts\releases'
New-Item -ItemType Directory -Force $releases | Out-Null
Push-Location $Root
try {
    Invoke-Checked $dotnet @('tool', 'restore')
    # The previous release (if any) is needed to build the small delta package.
    & $dotnet vpk download github --repoUrl 'https://github.com/nbk265117/goldboss' -o $releases
    if ($LASTEXITCODE -ne 0) { Write-Host 'No previous Velopack release downloaded (full package only).' -ForegroundColor Yellow }
    $notes = Join-Path $releases "notes-$version.md"
    $changelog = Get-Content (Join-Path $Root 'CHANGELOG.md') -Raw -Encoding utf8
    $section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version)) .*?(?=^## |\z)").Value
    Set-Content -Path $notes -Value $section -Encoding utf8
    Invoke-Checked $dotnet @('vpk', 'pack', '--packId', 'DofusGoldBoss-DGB', '--packVersion', $version,
        '--packDir', $out, '--runtime', 'win-x64', '--mainExe', 'DofusGoldBoss.exe', '--packTitle', 'Dofus Gold Boss - DGB',
        '--packAuthors', 'AYOUB YOUB JOE', '--icon', (Join-Path $Root 'src\DofusCompanion.App\Assets\app.ico'),
        '--releaseNotes', $notes, '-o', $releases)
}
finally { Pop-Location }
Write-Host "Velopack release in $releases" -ForegroundColor Green
