<#
.SYNOPSIS
  UI smoke test of a running (or freshly started) Dofus Companion, through Windows UI Automation.
  Drives only Dofus Companion's own windows. Exit code 0 = all checks passed.
.PARAMETER Exe
  Path to DofusCompanion.exe. If given and the app is not running, it is started.
#>
param([string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$results = [System.Collections.Generic.List[string]]::new()
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $script:results.Add(("{0} {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail))
    if (-not $ok) { $script:failed++ }
}

$proc = Get-Process DofusCompanion -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc -and $Exe) {
    $proc = Start-Process $Exe -PassThru
}
if (-not $proc) { Write-Error 'Dofus Companion is not running.'; exit 2 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)

function WaitFor([scriptblock]$probe, [int]$timeoutMs = 15000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $r = & $probe
        if ($r) { return $r }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Windows() { $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond) }
function ByName($parent, [string]$name) {
    $c = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function AllText($parent) {
    $c = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    ($parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | ForEach-Object { $_.Current.Name }) -join "`n"
}
function SetText($element, [string]$text) {
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}
function Click($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$main = WaitFor { Windows | Where-Object { $_.Current.Name -eq 'Dofus Companion' } | Select-Object -First 1 } 60000
Check 'main window shown' ($null -ne $main)
if (-not $main) { $results; exit 1 }

# --- Search by alias-insensitive name and open a verified mechanic
$search = WaitFor { ByName $main 'Rechercher par nom, alias, état, effet…' }
Check 'search box found' ($null -ne $search)
$sw = [Diagnostics.Stopwatch]::StartNew()
SetText $search 'KRALAMOURE geant'
$item = WaitFor {
    $c = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | Where-Object { (AllText $_) -match 'Kralamoure Géant' } | Select-Object -First 1
}
Check 'accent/case-insensitive search returns the boss' ($null -ne $item) ("{0} ms incl. 200 ms debounce" -f $sw.ElapsedMilliseconds)
if ($item) {
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $detail = WaitFor { $t = AllText $main; if ($t -match 'Faire tomber les 900') { $t } }
    Check 'mechanic detail displayed' ($null -ne $detail)
    Check 'ordered steps displayed' ($detail -match 'ÉTAPES')
    Check 'verification status displayed' ($detail -match 'Vérifié')
    Check 'source displayed' ($detail -match 'Antre du Kralamoure Géant')
}

# --- Unknown entity: explicit "no mechanic" rather than invented content
SetText $search 'Capitaine Ekarlatte'
$ring = WaitFor {
    $c = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | Where-Object { (AllText $_) -match 'aucune mécanique documentée' } | Select-Object -First 1
}
if ($ring) {
    $ring.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $none = WaitFor { (AllText $main) -match 'Aucune mécanique documentée' }
    Check 'missing knowledge is reported explicitly' ([bool]$none)
} else { Check 'boss without mechanic found' $false }

# --- Credit footer
Check 'credit footer shown' ([bool](WaitFor { (AllText $main) -match 'AYOUB YOUB JOE' }))

# --- No result
SetText $search 'zzzz entité inexistante'
Check 'no-result message' ([bool](WaitFor { (AllText $main) -match 'Aucun résultat' }))

# --- Overlay toggle through the main window button
function OverlayVisible() { [bool](Windows | Where-Object { $_.Current.Name -like '*overlay*' }) }
$toggle = ByName $main "Afficher / masquer l'overlay"
$before = OverlayVisible
$sw = [Diagnostics.Stopwatch]::StartNew()
Click $toggle
$changed = WaitFor { if ((OverlayVisible) -ne $before) { $true } } 3000
Check 'overlay toggles' ([bool]$changed) ("{0} ms" -f $sw.ElapsedMilliseconds)
Click $toggle
Check 'overlay toggles back' ([bool](WaitFor { if ((OverlayVisible) -eq $before) { $true } } 3000))

# --- Overlay quick search
$overlay = WaitFor { Windows | Where-Object { $_.Current.Name -like '*overlay*' } | Select-Object -First 1 } 3000
if ($overlay) {
    $os = ByName $overlay 'Rechercher un boss, un monstre…'
    SetText $os 'kimbo'
    $res = WaitFor {
        $c = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $overlay.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | Where-Object { $_.Current.Name -eq 'Kimbo' } | Select-Object -First 1
    }
    Check 'overlay search lists results' ($null -ne $res)
    if ($res) {
        Click $res   # UI Automation Invoke on our own button; no keystrokes are sent anywhere
        # Styled compact card: mechanic title, numbered step badges and the expected-result box.
        Check 'overlay shows compact guidance' ([bool](WaitFor { (AllText $overlay) -match 'Glyphes paires' }))
        Check 'overlay compact shows ordered steps' ([bool](WaitFor { $t = AllText $overlay; $t -match '(?m)^1$' -and $t -match '(?m)^2$' -and $t -match 'Frapper le Kimbo' }))
    }
}

$results
if ($failed -gt 0) { Write-Host "$failed check(s) failed"; exit 1 }
Write-Host 'All UI checks passed'
exit 0
