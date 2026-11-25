# System settings: hibernation, filesystem bookkeeping, memory, svchost grouping. CPU mitigations and the
# hypervisor are only touched with their opt-in switches.

$mm = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control' SvcHostSplitThresholdInKB 0x00FFFFFF -Area 'system'   # fewer svchost processes
Set-Reg $mm DisablePagingExecutive 1 -Area 'system'
Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' HiberbootEnabled 0 -Area 'system'  # no fast startup

# hibernation off (frees hiberfil.sys, which is the size of the RAM)
$hiberOn = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -EA SilentlyContinue).HibernateEnabled -eq 1
if (-not $hiberOn) { Write-Step 'system' 'hibernation off' 'ok' }
elseif ($script:Mode -ne 'Apply') { Write-Step 'system' 'hibernation off' 'change' 'on now' }
else {
    Add-JournalEntry 'hibernate' @{ Type = 'hibernate'; Enabled = $true }
    powercfg -h off; Write-Step 'system' 'hibernation off' $(if ($LASTEXITCODE -eq 0) { 'change' } else { 'fail' })
}

# NTFS: no last-access timestamps, no 8.3 short names for new files
foreach ($b in @(@('disablelastaccess', '1'), @('disable8dot3', '1'))) {
    $q = (fsutil behavior query $b[0] 2>$null | Out-String)
    $cur = if ($q -match '(?:=|is:)\s*(\d+)') { $matches[1] } else { $null }   # "X = 1" or "state is: 1"
    if ($cur -eq $b[1]) { Write-Step 'filesystem' $b[0] 'ok'; continue }
    if ($script:Mode -ne 'Apply') { Write-Step 'filesystem' $b[0] 'change' "now: $cur"; continue }
    Add-JournalEntry "fsutil|$($b[0])" @{ Type = 'fsutil'; Name = $b[0]; Value = $cur }
    fsutil behavior set $b[0] $b[1] | Out-Null
    Write-Step 'filesystem' $b[0] $(if ($LASTEXITCODE -eq 0) { 'change' } else { 'fail' })
}

if ($Opt.NoCpuMitigations) {
    # Spectre/Meltdown mitigations off: faster on old CPUs, weaker against local exploits
    Set-Reg $mm FeatureSettingsOverride 3 -Area 'cpu'
    Set-Reg $mm FeatureSettingsOverrideMask 3 -Area 'cpu'
}

if ($Opt.NoHypervisor) {
    # breaks WSL2, Docker/Podman, Windows Sandbox and virtualization-based security
    $cur = ((bcdedit /enum '{current}' | Select-String '^hypervisorlaunchtype\s+(\S+)').Matches | Select -First 1)
    $val = if ($cur) { $cur.Groups[1].Value } else { $null }
    if ($val -eq 'Off') { Write-Step 'hypervisor' 'off' 'ok' }
    elseif ($script:Mode -ne 'Apply') { Write-Step 'hypervisor' 'off' 'change' "now: $(if ($val) { $val } else { 'default' })" }
    else {
        Add-JournalEntry 'bcd|hypervisorlaunchtype' @{ Type = 'bcd'; Name = 'hypervisorlaunchtype'; Value = $val }
        bcdedit /set '{current}' hypervisorlaunchtype off | Out-Null
        Write-Step 'hypervisor' 'off' $(if ($LASTEXITCODE -eq 0) { 'change' } else { 'fail' })
    }
}
