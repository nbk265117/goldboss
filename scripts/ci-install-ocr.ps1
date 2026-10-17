<#
 CI only (GitHub-hosted Windows runner, administrator): installs Windows' French OCR language so the OCR tests
 can run, and adds fr-FR to the user's languages (Windows.Media.Ocr only offers the profile's languages).
#>
$ErrorActionPreference = 'Stop'
$name = 'Language.OCR~~~fr-FR~0.0.1.0'
$cap = Get-WindowsCapability -Online -Name $name
if ($cap.State -ne 'Installed') {
    Write-Host "Installing $name..."
    Add-WindowsCapability -Online -Name $name | Out-Null
}
$list = Get-WinUserLanguageList
if (-not ($list | Where-Object LanguageTag -eq 'fr-FR')) {
    $list.Add('fr-FR')
    Set-WinUserLanguageList $list -Force
}
Write-Host "fr-FR OCR: $((Get-WindowsCapability -Online -Name $name).State)"
