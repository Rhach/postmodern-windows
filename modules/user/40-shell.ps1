# minilaunch becomes this user's shell (per user: other accounts on the PC keep Explorer) and the chosen
# theme is applied to the desktop and the Terminal. Takes effect at the next sign-in.
$ml = Join-Path $Opt.InstallDir 'minilaunch\minilaunch.exe'
Set-Reg 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' Shell "`"$ml`" --shell" 'String' -Area 'shell'

$k = 'HKCU:\Software\minilaunch'
if ($Opt.VpnInterface) { Set-Reg $k VpnInterface $Opt.VpnInterface 'String' -Area 'shell' }

if ((Get-RegState $k 'Theme').Value -eq $Opt.Theme) { Write-Step 'theme' $Opt.Theme 'ok' }
elseif ($script:Mode -ne 'Apply') { Write-Step 'theme' $Opt.Theme 'change' }
else {
    Save-RegOriginal $k 'Theme'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Opt.InstallDir 'minilaunch\set-theme.ps1') -Theme $Opt.Theme -NoLockScreen -NoRestart | Out-Null
    Write-Step 'theme' $Opt.Theme $(if ((Get-RegState $k 'Theme').Value -eq $Opt.Theme) { 'change' } else { 'fail' })
}
