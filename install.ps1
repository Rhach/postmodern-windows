<#
postmodern-windows installer. Run from a normal PowerShell window in the repo folder:

    powershell -ExecutionPolicy Bypass -File .\install.ps1            install (asks for a theme first)
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf    show what would change, change nothing

It asks for administrator rights once (machine-wide part), does the per-user part as you, records every
original value so uninstall.ps1 can put it back, and needs one restart at the end.

Options
  -Theme <id>          skip the theme question (cyberpunk, hacker, zen, ...; see minilaunch\themes)
  -KeepUpdates         leave Windows Update alone (default: switched off)
  -NoPrinting          also switch off printing and scanning
  -NoSharing           also switch off file sharing, Remote Desktop and network discovery
  -Aggressive          also switch off smart cards, Windows Hello, EFS, offline files and other rarely used services
  -NoLocation          switch off location services
  -NoHypervisor        switch off the hypervisor (breaks WSL2, Docker, Sandbox)
  -NoCpuMitigations    switch off Spectre/Meltdown mitigations (faster on old CPUs, less safe)
  -Font Iosevka        use Iosevka in the terminal (169 MB download; default: Cascadia Mono)
  -VpnInterface <name> show this network adapter's address on the desktop (e.g. Tailscale)
  -NoReboot            don't offer to restart at the end
  -Yes                 don't ask: install without confirming, and don't offer a restart (for scripts)
  -Force               run on Windows versions other than Windows 10 22H2 (untested)
#>
param(
    [string]$Theme,
    [switch]$WhatIf,
    [switch]$KeepUpdates, [switch]$NoPrinting, [switch]$NoSharing, [switch]$Aggressive,
    [switch]$NoLocation, [switch]$NoHypervisor, [switch]$NoCpuMitigations,
    [ValidateSet('Cascadia', 'Iosevka')][string]$Font = 'Cascadia',
    [string]$VpnInterface,
    [switch]$NoReboot, [switch]$Force, [switch]$Yes,
    [switch]$Doctor,                                                   # used by doctor.ps1
    [ValidateSet('All', 'Machine', 'User')][string]$Part = 'All',     # internal: the elevated half
    [string]$Log                                                       # internal
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$installDir = Join-Path $env:ProgramFiles 'postmodern-windows'
$stateDir = Join-Path $installDir 'state'
$optionNames = 'Theme', 'KeepUpdates', 'NoPrinting', 'NoSharing', 'Aggressive', 'NoLocation', 'NoHypervisor', 'NoCpuMitigations', 'Font', 'VpnInterface'

# Windows PowerShell 5.1 only (System Restore cmdlets, consistent behaviour): relaunch from PowerShell 7.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath) + @($PSBoundParameters.GetEnumerator() | % {
        if ($_.Value -is [switch]) { if ($_.Value) { "-$($_.Key)" } } else { "-$($_.Key)", "$($_.Value)" } })
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @argList
    exit $LASTEXITCODE
}

. (Join-Path $src 'lib\engine.ps1')
$mode = if ($Doctor) { 'Check' } elseif ($WhatIf) { 'WhatIf' } else { 'Apply' }

function Get-Options {
    $o = @{}
    if ($Doctor -and (Test-Path "$stateDir\options.json")) {   # check against what was actually installed
        $saved = Get-Content "$stateDir\options.json" -Raw | ConvertFrom-Json
        foreach ($n in $optionNames) { $o[$n] = $saved.$n }
    } else { foreach ($n in $optionNames) { $o[$n] = (Get-Variable $n -ValueOnly) } }
    $o.InstallDir = $installDir
    $o.SourceDir = $src
    $o
}

function Invoke-Modules([string]$Which, [hashtable]$Opt, [string]$Journal) {
    Initialize-Engine $mode $Journal
    foreach ($m in Get-ChildItem (Join-Path $src "modules\$Which") -Filter *.ps1 | Sort-Object Name) {
        try { $null = . $m.FullName } catch { Write-Step $m.BaseName 'module' 'fail' $_.Exception.Message }
    }
    Get-Counts
}

# ---------------------------------------------------------------- the elevated, machine-wide half
if ($Part -eq 'Machine') {
    if ($Log) { Start-Transcript -Path $Log -Force | Out-Null }
    try {
        $opt = Get-Options
        if ($mode -eq 'Apply') {
            New-Item -ItemType Directory -Force $stateDir | Out-Null
            # switches as plain booleans: a SwitchParameter serialises to {"IsPresent":false}, which reads back as true
            $saved = @{}; foreach ($n in $optionNames) { $saved[$n] = if ($opt[$n] -is [System.Management.Automation.SwitchParameter]) { [bool]$opt[$n] } else { $opt[$n] } }
            $saved | ConvertTo-Json | Set-Content "$stateDir\options.json" -Encoding UTF8
        }
        $c = Invoke-Modules 'machine' $opt (Join-Path $stateDir 'journal-machine.json')
        Write-Host ("`n  machine: {0} changed, {1} already fine, {2} failed" -f $c.change, $c.ok, $c.fail)
        $code = if ($c.fail) { 1 } elseif ($mode -eq 'Check' -and $c.change) { 3 } else { 0 }   # 3: doctor found differences
    } catch { Write-Host "  machine part stopped: $($_.Exception.Message)" -ForegroundColor Red; $code = 2 }
    if ($Log) { Stop-Transcript | Out-Null }
    exit $code
}

# ---------------------------------------------------------------- checks
$os = [Environment]::OSVersion.Version
if (-not $Force -and -not ($os.Major -eq 10 -and $os.Build -ge 19041 -and $os.Build -le 19045 -and [Environment]::Is64BitOperatingSystem)) {
    Write-Host "This was built and tested for 64-bit Windows 10 22H2 (build 19045); this is build $($os.Build). Use -Force to try anyway." -ForegroundColor Yellow
    exit 1
}

Write-Host "`n  postmodern-windows  ·  $(@{ Apply = 'install'; WhatIf = 'dry run: nothing will change'; Check = 'doctor' }[$mode])`n" -ForegroundColor Cyan
if ($mode -eq 'Check' -and -not (Test-Path "$stateDir\options.json")) {
    Write-Host '  postmodern-windows is not installed on this PC (nothing to check). To preview an install: install.ps1 -WhatIf'
    exit 0
}

if ($mode -ne 'Check') {
    if (-not $Theme) {
        if ($mode -eq 'WhatIf') { $Theme = 'cyberpunk' }
        else { $Theme = & (Join-Path $src 'minilaunch\set-theme.ps1') -Pick }
    }
    if (-not (Test-Path (Join-Path $src 'build\minilaunch\minilaunch.exe'))) {
        Write-Host '  building minilaunch...'
        & (Join-Path $src 'build.ps1') | Out-Null
    }
}

if ($mode -eq 'Apply') {
    $extras = @(); foreach ($n in 'KeepUpdates', 'NoPrinting', 'NoSharing', 'Aggressive', 'NoLocation', 'NoHypervisor', 'NoCpuMitigations') { if ((Get-Variable $n -ValueOnly)) { $extras += "-$n" } }
    Write-Host "  Theme: $Theme    Font: $Font    Options: $(if ($extras) { $extras -join ' ' } else { 'defaults' })"
    Write-Host '  This switches off background services, telemetry and bundled apps, replaces the Explorer desktop'
    Write-Host '  with minilaunch for your account, and needs a restart. A restore point is made first, and'
    Write-Host '  uninstall.ps1 puts every setting back.'
    if (-not $Yes -and (Read-Host "`n  Continue? [y/N]") -notmatch '^(y|yes)$') { Write-Host '  Nothing changed.'; exit 0 }
}

# ---------------------------------------------------------------- machine half (elevated once)
Write-Host "`n  [machine]" -ForegroundColor Cyan
$machineLog = Join-Path $env:TEMP "postmodern-windows-machine-$(Get-Date -f yyyyMMdd-HHmmss).log"
$pass = @('-Part', 'Machine', '-Log', $machineLog)
if ($mode -eq 'WhatIf') { $pass += '-WhatIf' }
if ($Doctor) { $pass += '-Doctor' }
if ($Force) { $pass += '-Force' }
foreach ($n in $optionNames) { $v = Get-Variable $n -ValueOnly; if ($v -is [switch]) { if ($v) { $pass += "-$n" } } elseif ($v) { $pass += "-$n", "$v" } }
if (Test-Admin) {
    & $PSCommandPath @pass | Out-Host
    $machineCode = $LASTEXITCODE
} else {
    try {
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Minimized -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") + @($pass | % { if ($_ -like '-*') { $_ } else { "`"$_`"" } }))
        $machineCode = $p.ExitCode
    } catch { Write-Host '  Administrator rights were declined; nothing machine-wide was changed.' -ForegroundColor Yellow; exit 1 }
    if (Test-Path $machineLog) {   # replay the elevated window's output here
        Get-Content $machineLog | ? { $_ -match '^\s{2}\S' } | % { Write-Host $_ }
        Remove-Item $machineLog -Force -EA SilentlyContinue
    }
}
if ($machineCode -eq 2) { Write-Host '  The machine part stopped early; the per-user part was not run.' -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- user half (as you)
Write-Host "`n  [user: $env:USERNAME]" -ForegroundColor Cyan
$c = Invoke-Modules 'user' (Get-Options) (Join-Path $env:LOCALAPPDATA 'postmodern-windows\journal-user.json')
Write-Host ("`n  user: {0} changed, {1} already fine, {2} failed" -f $c.change, $c.ok, $c.fail)

$failed = ($machineCode -ne 0 -and $machineCode -ne 3) -or $c.fail
switch ($mode) {
    'WhatIf' { Write-Host "`n  Dry run finished: nothing was changed." -ForegroundColor Cyan }
    'Check' {
        if ($c.change -or $machineCode -eq 3) { Write-Host "`n  Some settings differ from the installed state (listed above). Re-run install.ps1 to fix them." -ForegroundColor Yellow }
        elseif ($machineCode) { Write-Host "`n  The machine-wide checks could not all run (listed above)." -ForegroundColor Yellow }
        else { Write-Host "`n  Everything matches the installed state." -ForegroundColor Green }
    }
    'Apply' {
        if ($failed) { Write-Host "`n  Finished with some failures (listed above). The rest is applied." -ForegroundColor Yellow }
        else { Write-Host "`n  Installed." -ForegroundColor Green }
        Write-Host "  Restart to switch to the new desktop. Afterwards: tap the Windows key to search, right-click"
        Write-Host "  the desktop for a menu. To undo everything: $installDir\uninstall.ps1"
        if (-not $NoReboot -and -not $Yes -and (Read-Host "`n  Restart now? [y/N]") -match '^(y|yes)$') { Restart-Computer -Force }
    }
}
exit $(if ($failed) { 1 } else { 0 })
