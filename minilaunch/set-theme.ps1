# Pick and apply a minilaunch theme: desktop wallpaper + colours, Windows Terminal scheme, lock screen.
#   set-theme.ps1                 interactive picker (Enter keeps the default, cyberpunk)
#   set-theme.ps1 -Theme zen      apply without asking
#   set-theme.ps1 -List           list themes
# Options: -NoLockScreen (skip the admin step), -NoRestart (don't restart minilaunch).
# Works in Windows PowerShell 5.1 and PowerShell 7.
param([string]$Theme, [switch]$List, [switch]$NoLockScreen, [switch]$NoRestart, [switch]$LockOnly, [string]$TerminalSettings)
$ErrorActionPreference = 'Stop'
if (-not $TerminalSettings) {
    # the portable Terminal minilaunch uses (same setting and default as minilaunch.exe)
    $wt = try { (Get-ItemProperty 'HKCU:\Software\minilaunch' -EA Stop).TerminalExe } catch { $null }
    if (-not $wt) { $wt = Join-Path $env:LOCALAPPDATA 'postmodern-windows\terminal\WindowsTerminal.exe' }
    $TerminalSettings = Join-Path (Split-Path ([Environment]::ExpandEnvironmentVariables($wt))) 'settings\settings.json'
}
$themesDir = Join-Path $PSScriptRoot 'themes'
$lockDst = 'C:\Windows\Web\Screen\minilaunch-lock.jpg'

function Read-Ini($path) {
    $h = [ordered]@{}
    foreach ($line in Get-Content $path -Encoding UTF8) {
        $l = $line.Trim()
        if (-not $l -or $l[0] -in '#', ';', '[') { continue }
        $i = $l.IndexOf('='); if ($i -lt 1) { continue }
        $h[$l.Substring(0, $i).Trim()] = $l.Substring($i + 1).Trim()
    }
    $h
}

function Get-Themes {
    Get-ChildItem $themesDir -Directory | ? { Test-Path (Join-Path $_.FullName 'theme.ini') } | % {
        $ini = Read-Ini (Join-Path $_.FullName 'theme.ini')
        [pscustomobject]@{ Id = $_.Name; Name = $ini['name']; Description = $ini['description']; Dir = $_.FullName; Ini = $ini }
    } | Sort-Object { if ($_.Id -eq 'cyberpunk') { 0 } else { 1 } }, Id
}

# Elevated half: copy the theme's lock image into place, point the lock-screen policy at it and
# re-render LogonUI's caches (the LockScreenSync task, if installed).
function Set-LockScreen($t) {
    Copy-Item (Join-Path $t.Dir 'lock.jpg') $lockDst -Force
    $csp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
    New-Item $csp -Force | Out-Null
    New-ItemProperty $csp LockScreenImagePath -Value $lockDst -PropertyType String -Force | Out-Null
    New-ItemProperty $csp LockScreenImageUrl -Value $lockDst -PropertyType String -Force | Out-Null
    New-ItemProperty $csp LockScreenImageStatus -Value 1 -PropertyType DWord -Force | Out-Null
    if (Get-ScheduledTask -TaskName LockScreenSync -EA SilentlyContinue) { Start-ScheduledTask -TaskName LockScreenSync }
}

$themes = @(Get-Themes)
if (-not $themes) { throw "no themes found in $themesDir" }
$current = try { (Get-ItemProperty 'HKCU:\Software\minilaunch' -EA Stop).Theme } catch { $null }
if (-not $current) { $current = 'cyberpunk' }

if ($List) {
    $themes | % { '{0,-14} {1,-18} {2}{3}' -f $_.Id, $_.Name, $_.Description, $(if ($_.Id -eq $current) { '   [current]' }) }
    return
}

if (-not $Theme) {
    Write-Host "`n  Pick a desktop theme`n"
    for ($i = 0; $i -lt $themes.Count; $i++) {
        $t = $themes[$i]
        $mark = if ($t.Id -eq $current) { '  [current]' } else { '' }
        Write-Host ('   {0}) {1,-18} {2}{3}' -f ($i + 1), $t.Name, $t.Description, $mark)
    }
    $answer = Read-Host "`n  Number or name (Enter = cyberpunk)"
    if (-not $answer) { $Theme = 'cyberpunk' }
    elseif ($answer -match '^\d+$' -and [int]$answer -ge 1 -and [int]$answer -le $themes.Count) { $Theme = $themes[[int]$answer - 1].Id }
    else { $Theme = $answer }
}
$t = $themes | ? { $_.Id -eq $Theme -or $_.Name -eq $Theme } | Select -First 1
if (-not $t) { throw "unknown theme '$Theme' - try: $(($themes.Id) -join ', ')" }

if ($LockOnly) { Set-LockScreen $t; return }

# 1. desktop: minilaunch reads this at startup
New-Item 'HKCU:\Software\minilaunch' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\minilaunch' Theme $t.Id
Write-Host "  desktop      -> $($t.Name)"

# 2. Windows Terminal colour scheme (portable install; it reloads settings.json on its own)
if (Test-Path $TerminalSettings) {
    $json = Get-Content $TerminalSettings -Raw -Encoding UTF8 | ConvertFrom-Json
    $scheme = [ordered]@{ name = $t.Ini['term.name'] }
    foreach ($k in $t.Ini.Keys) { if ($k -like 'term.*' -and $k -ne 'term.name') { $scheme[$k.Substring(5)] = $t.Ini[$k] } }
    if ($scheme.name) {
        $json.schemes = @(@($json.schemes | ? { $_.name -ne $scheme.name }) + [pscustomobject]$scheme)
        $json.profiles.defaults | Add-Member colorScheme $scheme.name -Force
        $json | ConvertTo-Json -Depth 20 | Set-Content $TerminalSettings -Encoding UTF8
        Write-Host "  terminal     -> $($scheme.name)"
    }
} else { Write-Host "  terminal     -> skipped (no $TerminalSettings)" }

# 3. lock + sign-in screen (machine-wide, needs admin)
if (-not $NoLockScreen) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    try {
        if ($admin) { Set-LockScreen $t }
        else {
            $exe = (Get-Process -Id $PID).Path
            $p = Start-Process $exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Theme', $t.Id, '-LockOnly'
            if ($p.ExitCode -ne 0) { throw "elevated step exited with $($p.ExitCode)" }
        }
        Write-Host "  lock screen  -> $($t.Name)"
    } catch { Write-Host "  lock screen  -> skipped ($($_.Exception.Message))" }
}

# 4. restart minilaunch so it picks the theme up. Started through WMI so it gets a clean user
#    environment instead of inheriting this console's variables.
if (-not $NoRestart) {
    $ml = Get-CimInstance Win32_Process -Filter "Name='minilaunch.exe'" | Select -First 1
    if ($ml) {
        $cmd = $ml.CommandLine
        Stop-Process -Id $ml.ProcessId -Force
        Start-Sleep -Milliseconds 500
        Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd } | Out-Null
        Write-Host "  minilaunch   -> restarted"
    }
}
Write-Host ''
