<#
Puts back everything install.ps1 changed, from the journals it kept of the original values, newest first.

    powershell -ExecutionPolicy Bypass -File "C:\Program Files\postmodern-windows\uninstall.ps1"

Your account goes back to the Explorer desktop at the next sign-in. Two things it can't undo: removed
Store apps (reinstall them from the Microsoft Store) and the restore point it made (that one stays, as a
second way back). Needs administrator rights once and a restart.
#>
param([switch]$NoReboot, [switch]$Yes, [ValidateSet('All', 'Machine')][string]$Part = 'All', [string]$Log)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}
. (Join-Path $PSScriptRoot 'lib\engine.ps1')
$installDir = Join-Path $env:ProgramFiles 'postmodern-windows'
$script:Mode = 'Apply'
$notes = New-Object System.Collections.Generic.List[string]

function Undo([string]$Journal) {
    if (-not (Test-Path $Journal)) { Write-Host '  nothing recorded'; return 0 }
    $entries = Read-Journal $Journal
    [array]::Reverse($entries)
    # files and folders go last: the install folder holds the journal and this script
    $entries = @($entries | ? { $_.Type -ne 'path' }) + @($entries | ? { $_.Type -eq 'path' })
    $script:SystemQueue.Clear(); $fails = 0
    foreach ($e in $entries) {
        try {
            switch ($e.Type) {
                'reg' { Restore-Reg $e; Write-Step 'restored' "$($e.Path)\$($e.Name)" 'change' }
                'svc' {
                    $p = "HKLM:\SYSTEM\CurrentControlSet\Services\$($e.Name)"
                    try { Set-ItemProperty $p Start $e.Start -EA Stop; Write-Step 'restored' "service $($e.Name)" 'change' }
                    catch { $script:SystemQueue.Add(@{ Kind = 'svc-restore'; Name = $e.Name; Start = $e.Start; Area = 'restored' }) }
                }
                'task' {
                    try { Enable-ScheduledTask -TaskPath $e.TaskPath -TaskName $e.TaskName -EA Stop | Out-Null; Write-Step 'restored' "task $($e.TaskPath)$($e.TaskName)" 'change' }
                    catch { $script:SystemQueue.Add(@{ Kind = 'task-restore'; TaskPath = $e.TaskPath; TaskName = $e.TaskName; Area = 'restored' }) }
                }
                'feature' { Enable-WindowsOptionalFeature -Online -FeatureName $e.Name -All -NoRestart -EA Stop | Out-Null; Write-Step 'restored' "feature $($e.Name)" 'change' }
                'appx' { $notes.Add("$($e.Name) was removed; reinstall it from the Microsoft Store if you want it back.") }
                'mppref' { $p = @{ $e.Name = $e.Value }; Set-MpPreference @p -EA Stop; Write-Step 'restored' "Defender $($e.Name)" 'change' }
                'hibernate' { powercfg -h on; Write-Step 'restored' 'hibernation' 'change' }
                'fsutil' { if ($null -ne $e.Value) { fsutil behavior set $e.Name $e.Value | Out-Null }; Write-Step 'restored' "fsutil $($e.Name)" 'change' }
                'bcd' {
                    if ($e.Value) { bcdedit /set '{current}' $e.Name $e.Value | Out-Null } else { bcdedit /deletevalue '{current}' $e.Name | Out-Null }
                    Write-Step 'restored' "boot setting $($e.Name)" 'change'
                }
                'schtask' { Unregister-ScheduledTask -TaskName $e.Name -Confirm:$false -EA SilentlyContinue; Write-Step 'removed' "task $($e.Name)" 'change' }
                'driver' { pnputil /delete-driver $e.Published /uninstall /force | Out-Null; Write-Step 'removed' "driver $($e.Published) (Apple's driver takes over)" 'change' }
                'onedrive' { if (Test-Path $e.Setup) { Start-Process $e.Setup '/silent' }; Write-Step 'restored' 'OneDrive (reinstalling)' 'change' }
                'path' {
                    if (-not (Test-Path $e.Path)) { continue }
                    try { Remove-Item $e.Path -Recurse -Force -EA Stop; Write-Step 'removed' $e.Path 'change' }
                    catch {
                        # in use (minilaunch is still the running shell): delete at the next sign-in instead
                        $cmd = if ((Get-Item $e.Path) -is [IO.DirectoryInfo]) { "cmd.exe /c rmdir /s /q `"$($e.Path)`"" } else { "cmd.exe /c del /f /q `"$($e.Path)`"" }
                        New-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' "postmodern-windows-$([guid]::NewGuid().ToString('N').Substring(0, 6))" -Value $cmd -Force | Out-Null
                        Write-Step 'removed' "$($e.Path) (after restart)" 'change'
                    }
                }
                default { Write-Step 'unknown' $e.Key 'skip' }
            }
        } catch { $fails++; Write-Step 'FAILED' "$($e.Key)" 'fail' $_.Exception.Message }
    }
    Invoke-SystemQueue
    $fails + (Get-Counts).fail
}

if ($Part -eq 'Machine') {
    if ($Log) { Start-Transcript -Path $Log -Force | Out-Null }
    $j = Join-Path $installDir 'state\journal-machine.json'
    $f = Undo $j
    foreach ($n in $notes) { Write-Host "  note: $n" }
    if (-not $f -and (Test-Path $j)) { Remove-Item (Join-Path $installDir 'state') -Recurse -Force -EA SilentlyContinue }
    if ($Log) { Stop-Transcript | Out-Null }
    exit $(if ($f) { 1 } else { 0 })
}

Write-Host "`n  postmodern-windows  ·  uninstall`n" -ForegroundColor Cyan
if (-not $Yes -and (Read-Host '  Put everything back and return to the Explorer desktop? [y/N]') -notmatch '^(y|yes)$') { exit 0 }

# your account first (as you), so the shell is Explorer again before minilaunch's files go
Write-Host "`n  [user: $env:USERNAME]" -ForegroundColor Cyan
$uj = Join-Path $env:LOCALAPPDATA 'postmodern-windows\journal-user.json'
$uf = Undo $uj
if (-not $uf -and (Test-Path $uj)) { Remove-Item $uj -Force }

Write-Host "`n  [machine]" -ForegroundColor Cyan
$log = Join-Path $env:TEMP "postmodern-windows-uninstall-$(Get-Date -f yyyyMMdd-HHmmss).log"
if (Test-Admin) { & $PSCommandPath -Part Machine -Log $log | Out-Host; $mf = $LASTEXITCODE }
else {
    try {
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Minimized -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Part', 'Machine', '-Log', "`"$log`""
        $mf = $p.ExitCode
    } catch { Write-Host '  Administrator rights were declined; machine-wide settings are unchanged.' -ForegroundColor Yellow; exit 1 }
    if (Test-Path $log) { Get-Content $log | ? { $_ -match '^\s{2}\S' } | % { Write-Host $_ }; Remove-Item $log -Force -EA SilentlyContinue }
}

if ($uf -or $mf) { Write-Host "`n  Finished with some failures (listed above); the journals were kept so you can run this again." -ForegroundColor Yellow }
else { Write-Host "`n  Uninstalled. Restart to get the Explorer desktop back." -ForegroundColor Green }
if (-not $NoReboot -and -not $Yes -and (Read-Host "`n  Restart now? [y/N]") -match '^(y|yes)$') { Restart-Computer -Force }
