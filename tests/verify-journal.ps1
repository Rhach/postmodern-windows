# Test helper: after uninstall.ps1, checks that every value recorded in (copies of) the install journals is
# back to its original. Copy the journals out before uninstalling: a successful uninstall deletes them.
#   verify-journal.ps1 -Journal journal-machine.json, journal-user.json
param([string[]]$Journal)
. (Join-Path $PSScriptRoot '..\lib\engine.ps1')
$bad = 0; $checked = 0
foreach ($j in $Journal) {
    foreach ($e in @(Get-Content $j -Raw | ConvertFrom-Json)) {
        $ok = $null
        switch ($e.Type) {
            'reg' {
                $cur = Get-RegState $e.Path $e.Name
                $ok = if ($e.Exists) { $cur.Exists -and "$($cur.Value)" -eq "$($e.Value)" } else { -not $cur.Exists }
            }
            'svc' { $ok = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$($e.Name)" -EA SilentlyContinue).Start -eq $e.Start }
            'task' { $t = Get-ScheduledTask -TaskPath $e.TaskPath -TaskName $e.TaskName -EA SilentlyContinue; $ok = $t -and $t.State -ne 'Disabled' }
            'feature' { $ok = (Get-WindowsOptionalFeature -Online -FeatureName $e.Name).State -eq 'Enabled' }
            'mppref' { $ok = "$((Get-MpPreference).($e.Name))" -eq "$($e.Value)" }
            'schtask' { $ok = -not (Get-ScheduledTask -TaskName $e.Name -EA SilentlyContinue) }
            'path' { $ok = -not (Test-Path $e.Path) }
            'hibernate' { $ok = (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\Power).HibernateEnabled -eq 1 }
            default { continue }   # appx (not restorable), onedrive, driver, fsutil, bcd: checked by hand
        }
        if ($null -eq $ok) { continue }
        $checked++
        if (-not $ok) { $bad++; "NOT RESTORED  $($e.Key)" }
    }
}
"$checked checked, $bad not restored"
