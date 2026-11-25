# Test helper: writes the parts of the system the installer touches to a JSON file, so a snapshot taken
# before install.ps1 can be compared with one taken after uninstall.ps1 (Compare-Snapshots below).
#   state-snapshot.ps1 -Out before.json
#   state-snapshot.ps1 -Compare before.json after.json
# Run elevated (optional features and other users' apps need it). Windows PowerShell 5.1.
param([string]$Out, [string[]]$Compare)

if ($Compare) {
    $a = Get-Content $Compare[0] -Raw | ConvertFrom-Json
    $b = Get-Content $Compare[1] -Raw | ConvertFrom-Json
    $diffs = 0
    foreach ($section in 'Services', 'Tasks', 'Features', 'Appx', 'Defender', 'Values') {
        $x = $a.$section; $y = $b.$section
        $names = @($x.PSObject.Properties.Name) + @($y.PSObject.Properties.Name) | Sort-Object -Unique
        foreach ($n in $names) {
            if ("$($x.$n)" -ne "$($y.$n)") { $diffs++; "{0,-9} {1}: {2} -> {3}" -f $section, $n, $(if ($null -eq $x.$n) { '(absent)' } else { $x.$n }), $(if ($null -eq $y.$n) { '(absent)' } else { $y.$n }) }
        }
    }
    "$diffs difference(s)"
    return
}

$s = [ordered]@{}
$s.Services = [ordered]@{}
foreach ($k in Get-ChildItem HKLM:\SYSTEM\CurrentControlSet\Services) {
    $start = (Get-ItemProperty $k.PSPath -EA SilentlyContinue).Start
    if ($null -ne $start -and $k.PSChildName -notmatch '_[0-9a-f]{4,6}$') { $s.Services[$k.PSChildName] = $start }   # skip per-session instances
}
$s.Tasks = [ordered]@{}
foreach ($t in Get-ScheduledTask) { $s.Tasks["$($t.TaskPath)$($t.TaskName)"] = if ($t.State -eq 'Disabled') { 'Disabled' } else { 'Enabled' } }
$s.Features = [ordered]@{}
foreach ($f in Get-WindowsOptionalFeature -Online) { $s.Features[$f.FeatureName] = [string]$f.State }
$s.Appx = [ordered]@{}
foreach ($p in Get-AppxProvisionedPackage -Online) { $s.Appx[$p.DisplayName] = 'provisioned' }
$s.Defender = [ordered]@{}
$mp = Get-MpPreference
foreach ($n in 'ScanAvgCPULoadFactor', 'EnableLowCpuPriority', 'ScanOnlyIfIdleEnabled', 'DisableCatchupQuickScan', 'SignatureUpdateInterval', 'SignatureFallbackOrder') { $s.Defender[$n] = "$($mp.$n)" }
$s.Values = [ordered]@{
    hibernate         = (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\Power).HibernateEnabled
    disablelastaccess = (fsutil behavior query disablelastaccess | Out-String).Trim()
    disable8dot3      = (fsutil behavior query disable8dot3 | Out-String) -replace '\s+', ' '
    userShell         = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -EA SilentlyContinue).Shell
    machineShell      = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon').Shell
    lockScreenTask    = [bool](Get-ScheduledTask -TaskName LockScreenSync -EA SilentlyContinue)
    installDir        = Test-Path "$env:ProgramFiles\postmodern-windows"
}
$s | ConvertTo-Json -Depth 4 | Set-Content $Out -Encoding UTF8
"snapshot: $($s.Services.Count) services, $($s.Tasks.Count) tasks, $($s.Features.Count) features -> $Out"
