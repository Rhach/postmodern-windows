# Shared by install.ps1, uninstall.ps1 and doctor.ps1. Windows PowerShell 5.1 compatible.
#
# Every change goes through the Set-* helpers below. They compare the current state with the wanted one and
#   Apply   record the original in the journal (first record wins, so re-running never loses the real
#           original), then change it
#   WhatIf  report what would change
#   Check   report what differs (doctor)
# Writes that even an administrator is refused (some services and tasks) are queued and retried as SYSTEM.

$script:Mode = 'WhatIf'
$script:JournalPath = $null
$script:PmwJournal = New-Object System.Collections.Generic.List[object]
$script:PmwJournalKeys = @{}
$script:SystemQueue = New-Object System.Collections.Generic.List[object]
$script:Counts = @{ ok = 0; change = 0; fail = 0; skip = 0 }

function Initialize-Engine([string]$Mode, [string]$JournalPath) {
    $script:Mode = $Mode
    $script:JournalPath = $JournalPath
    $script:PmwJournal.Clear(); $script:PmwJournalKeys = @{}; $script:SystemQueue.Clear()
    $script:Counts = @{ ok = 0; change = 0; fail = 0; skip = 0 }
    if ($JournalPath -and (Test-Path $JournalPath)) {
        foreach ($e in (Read-Journal $JournalPath)) { $script:PmwJournal.Add($e); $script:PmwJournalKeys[$e.Key] = $true }
    }
}

# The journal's entries as an array. Windows PowerShell 5.1's ConvertFrom-Json emits a JSON array as ONE
# pipeline object, so @(... | ConvertFrom-Json) is a one-element array holding the whole list; assigning
# to a variable first and then wrapping it gives the elements.
function Read-Journal([string]$Path) {
    $parsed = Get-Content $Path -Raw | ConvertFrom-Json
    , @($parsed)
}

function Save-Journal {
    if (-not $script:JournalPath) { return }
    $dir = Split-Path $script:JournalPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    ConvertTo-Json -InputObject @($script:PmwJournal.ToArray()) -Depth 6 | Set-Content $script:JournalPath -Encoding UTF8
}

function Add-JournalEntry([string]$Key, [hashtable]$Entry) {
    if ($script:PmwJournalKeys.ContainsKey($Key)) { return }
    $Entry.Key = $Key
    $script:PmwJournal.Add([pscustomobject]$Entry); $script:PmwJournalKeys[$Key] = $true
    Save-Journal
}

# state: ok (already as wanted) | change (changed / would change) | fail | skip (not applicable)
function Write-Step([string]$Area, [string]$Item, [string]$State, [string]$Detail = '') {
    $script:Counts[$State]++
    if ($State -eq 'ok' -and $script:Mode -ne 'Check') { return }   # keep install output to what changes
    $verb = switch ($State) {
        'ok' { 'ok      ' }
        'change' { if ($script:Mode -eq 'Apply') { 'changed ' } elseif ($script:Mode -eq 'WhatIf') { 'would   ' } else { 'DIFFERS ' } }
        'fail' { 'FAILED  ' }
        'skip' { 'skip    ' }
    }
    $color = @{ ok = 'DarkGray'; change = $(if ($script:Mode -eq 'Check') { 'Yellow' } else { 'Cyan' }); fail = 'Red'; skip = 'DarkGray' }[$State]
    Write-Host ("  {0} {1,-12} {2}{3}" -f $verb, $Area, $Item, $(if ($Detail) { "  ($Detail)" })) -ForegroundColor $color
}

function Get-Counts { $script:Counts }

# One-off actions (restore point, uninstalling an app): run on Apply, announce on WhatIf, silent on Check.
function Invoke-Action([string]$Area, [string]$Item, [scriptblock]$Do) {
    if ($script:Mode -eq 'Check') { return }
    if ($script:Mode -eq 'WhatIf') { Write-Step $Area $Item 'change'; return }
    try { & $Do; Write-Step $Area $Item 'change' } catch { Write-Step $Area $Item 'fail' $_.Exception.Message }
}

# ---------------------------------------------------------------- registry

function Get-RegState([string]$Path, [string]$Name) {
    $k = Get-Item -LiteralPath $Path -EA SilentlyContinue
    if (-not $k) { return @{ Exists = $false; KeyExists = $false } }
    if ($k.GetValueNames() -notcontains $Name) { return @{ Exists = $false; KeyExists = $true } }
    $kind = [string]$k.GetValueKind($Name)
    $val = $k.GetValue($Name, $null, 'DoNotExpandEnvironmentNames')
    if ($kind -eq 'Binary') { $val = [Convert]::ToBase64String([byte[]]$val) }
    @{ Exists = $true; KeyExists = $true; Kind = $kind; Value = $val }
}

function ConvertTo-RegValue($Value, [string]$Type) {
    switch ($Type) {
        'Binary' { if ($Value -is [string]) { [Convert]::FromBase64String($Value) } else { [byte[]]$Value } }
        'DWord' { [int]$Value }
        'QWord' { [long]$Value }
        default { $Value }
    }
}

function Write-RegValue([string]$Path, [string]$Name, $Value, [string]$Type) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -LiteralPath $Path -Name $Name -Value (ConvertTo-RegValue $Value $Type) -PropertyType $Type -Force -EA Stop | Out-Null
}

function Set-Reg([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord', [string]$Area = 'registry') {
    $cur = Get-RegState $Path $Name
    $want = if ($Type -eq 'Binary') { [Convert]::ToBase64String((ConvertTo-RegValue $Value 'Binary')) } else { $Value }
    $label = "$($Path -replace '^HK(LM|CU):\\', 'HK$1\')\$Name"
    if ($cur.Exists -and $cur.Kind -eq $Type -and "$($cur.Value)" -eq "$want") { Write-Step $Area $label 'ok'; return }
    if ($script:Mode -ne 'Apply') { Write-Step $Area $label 'change' "now: $(if ($cur.Exists) { $cur.Value } else { 'unset' })"; return }
    Add-JournalEntry "reg|$Path|$Name" @{ Type = 'reg'; Path = $Path; Name = $Name; Exists = $cur.Exists; KeyExisted = $cur.KeyExists; Kind = $cur.Kind; Value = $cur.Value }
    try { Write-RegValue $Path $Name $Value $Type; Write-Step $Area $label 'change' }
    catch { Write-Step $Area $label 'fail' $_.Exception.Message }
}

function Restore-Reg($e) {
    if ($e.Exists) { Write-RegValue $e.Path $e.Name $e.Value $e.Kind }
    else {
        Remove-ItemProperty -LiteralPath $e.Path -Name $e.Name -EA SilentlyContinue
        $k = Get-Item -LiteralPath $e.Path -EA SilentlyContinue
        if (-not $e.KeyExisted -and $k -and -not $k.GetValueNames() -and -not $k.GetSubKeyNames()) { Remove-Item -LiteralPath $e.Path -Force }
    }
}

# Records a value the installer will change through another tool (set-theme.ps1), so uninstall restores it.
function Save-RegOriginal([string]$Path, [string]$Name) {
    if ($script:Mode -ne 'Apply') { return }
    $cur = Get-RegState $Path $Name
    Add-JournalEntry "reg|$Path|$Name" @{ Type = 'reg'; Path = $Path; Name = $Name; Exists = $cur.Exists; KeyExisted = $cur.KeyExists; Kind = $cur.Kind; Value = $cur.Value }
}

# ---------------------------------------------------------------- services

function Set-ServiceStart([string]$Name, [int]$Start, [string]$Area = 'service') {
    $path = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -LiteralPath $path)) { if ($script:Mode -eq 'Check') { Write-Step $Area $Name 'skip' 'not on this PC' }; return }
    $cur = (Get-ItemProperty -LiteralPath $path -EA SilentlyContinue).Start
    if ($cur -eq $Start) { Write-Step $Area $Name 'ok'; return }
    if ($script:Mode -ne 'Apply') { Write-Step $Area $Name 'change' "start $cur -> $Start"; return }
    Add-JournalEntry "svc|$Name" @{ Type = 'svc'; Name = $Name; Start = $cur }
    try { Set-ItemProperty -LiteralPath $path -Name Start -Value $Start -EA Stop; Write-Step $Area $Name 'change' }
    catch { $script:SystemQueue.Add(@{ Kind = 'svc'; Name = $Name; Start = $Start; Area = $Area }) }
}

# ---------------------------------------------------------------- scheduled tasks

# $Pattern: '\Microsoft\Windows\Maps\*' (every task in a folder and below) or '\Folder\TaskName'
function Get-TasksByPattern([string]$Pattern) {
    $folder = $Pattern -replace '[^\\]*$', ''
    $name = $Pattern -replace '^.*\\', ''
    @(Get-ScheduledTask -TaskPath "$folder*" -EA SilentlyContinue | ? { $_.TaskName -like $name })
}

function Set-TasksDisabled([string]$Pattern, [string]$Area = 'task') {
    foreach ($t in Get-TasksByPattern $Pattern) {
        $id = "$($t.TaskPath)$($t.TaskName)"
        if ($t.State -eq 'Disabled') { Write-Step $Area $id 'ok'; continue }
        if ($script:Mode -ne 'Apply') { Write-Step $Area $id 'change' 'enabled now'; continue }
        Add-JournalEntry "task|$id" @{ Type = 'task'; TaskPath = $t.TaskPath; TaskName = $t.TaskName }
        try { $t | Disable-ScheduledTask -EA Stop | Out-Null; Write-Step $Area $id 'change' }
        catch { $script:SystemQueue.Add(@{ Kind = 'task'; TaskPath = $t.TaskPath; TaskName = $t.TaskName; Area = $Area }) }
    }
}

# ---------------------------------------------------------------- running as SYSTEM

# Runs PowerShell as SYSTEM through a short-lived scheduled task. The script travels inline as
# -EncodedCommand, so there's no file on disk for anyone to swap before it runs.
function Invoke-AsSystem([string]$Script, [int]$TimeoutSec = 120) {
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    $name = 'postmodern-windows-system-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -EncodedCommand $enc"
    $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSec)
    Register-ScheduledTask -TaskName $name -Action $a -Settings $s -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $name
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        Start-Sleep -Milliseconds 500
        while ((Get-ScheduledTask -TaskName $name).State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    } finally { Unregister-ScheduledTask -TaskName $name -Confirm:$false }
}

# Retries queued writes as SYSTEM, then re-checks each one: the verdict comes from the real state.
function Invoke-SystemQueue {
    if ($script:SystemQueue.Count -eq 0) { return }
    $lines = foreach ($q in $script:SystemQueue) {
        if ($q.Kind -eq 'svc') { "try { Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\$($q.Name)' Start $($q.Start) } catch {}" }
        elseif ($q.Kind -eq 'task') { "try { Disable-ScheduledTask -TaskPath '$($q.TaskPath)' -TaskName '$($q.TaskName)' | Out-Null } catch {}" }
        elseif ($q.Kind -eq 'svc-restore') { "try { Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\$($q.Name)' Start $($q.Start) } catch {}" }
        elseif ($q.Kind -eq 'task-restore') { "try { Enable-ScheduledTask -TaskPath '$($q.TaskPath)' -TaskName '$($q.TaskName)' | Out-Null } catch {}" }
    }
    Invoke-AsSystem ($lines -join "`n")
    foreach ($q in $script:SystemQueue) {
        if ($q.Kind -like 'svc*') {
            $now = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$($q.Name)" -EA SilentlyContinue).Start
            Write-Step $q.Area $q.Name $(if ($now -eq $q.Start) { 'change' } else { 'fail' }) 'as SYSTEM'
        } else {
            $t = Get-ScheduledTask -TaskPath $q.TaskPath -TaskName $q.TaskName -EA SilentlyContinue
            $wantDisabled = $q.Kind -eq 'task'
            $good = $t -and (($t.State -eq 'Disabled') -eq $wantDisabled)
            Write-Step $q.Area "$($q.TaskPath)$($q.TaskName)" $(if ($good) { 'change' } else { 'fail' }) 'as SYSTEM'
        }
    }
    $script:SystemQueue.Clear()
}

# ---------------------------------------------------------------- Windows features and apps

function Set-FeatureDisabled([string]$Name, [string]$Area = 'feature') {
    $f = Get-WindowsOptionalFeature -Online -FeatureName $Name -EA SilentlyContinue
    if (-not $f) { if ($script:Mode -eq 'Check') { Write-Step $Area $Name 'skip' 'not on this PC' }; return }
    if ($f.State -ne 'Enabled') { Write-Step $Area $Name 'ok'; return }
    if ($script:Mode -ne 'Apply') { Write-Step $Area $Name 'change' 'enabled now'; return }
    Add-JournalEntry "feature|$Name" @{ Type = 'feature'; Name = $Name }
    try { Disable-WindowsOptionalFeature -Online -FeatureName $Name -NoRestart -EA Stop | Out-Null; Write-Step $Area $Name 'change' }
    catch { Write-Step $Area $Name 'fail' $_.Exception.Message }
}

# AppX removal can't be undone by the uninstaller; the journal only lists what went (Store reinstall).
function Remove-AppxEverywhere([string]$Name, [string]$Area = 'app') {
    $all = @(Get-AppxPackage -AllUsers -Name $Name -EA SilentlyContinue)
    $installed = @($all | ? { -not $_.NonRemovable })   # system components Windows won't remove
    $prov = @(Get-AppxProvisionedPackage -Online -EA SilentlyContinue | ? DisplayName -eq $Name)
    if (-not $installed -and -not $prov) {
        if ($all) { Write-Step $Area $Name 'skip' 'part of Windows, not removable' } else { Write-Step $Area $Name 'ok' }
        return
    }
    if ($script:Mode -ne 'Apply') { Write-Step $Area $Name 'change' 'installed'; return }
    Add-JournalEntry "appx|$Name" @{ Type = 'appx'; Name = $Name }
    try {
        foreach ($p in $installed) { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -EA Stop }
        foreach ($p in $prov) { Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -EA Stop | Out-Null }
        Write-Step $Area $Name 'change'
    } catch { Write-Step $Area $Name 'fail' ($_.Exception.Message -split "`n")[0] }
}

# ---------------------------------------------------------------- Defender preferences

function Set-MpPref([string]$Name, $Value, [string]$Area = 'defender') {
    $cur = (Get-MpPreference -EA SilentlyContinue).$Name
    if ("$cur" -eq "$Value") { Write-Step $Area $Name 'ok'; return }
    if ($script:Mode -ne 'Apply') { Write-Step $Area $Name 'change' "now: $cur"; return }
    Add-JournalEntry "mppref|$Name" @{ Type = 'mppref'; Name = $Name; Value = $cur }
    try {
        $p = @{ $Name = $Value }
        Set-MpPreference @p -EA Stop
        $now = (Get-MpPreference).$Name
        Write-Step $Area $Name $(if ("$now" -eq "$Value") { 'change' } else { 'fail' }) $(if ("$now" -ne "$Value") { 'blocked (Tamper Protection?)' })
    } catch { Write-Step $Area $Name 'fail' $_.Exception.Message }
}

# ---------------------------------------------------------------- files and downloads

# Downloads a pinned release asset and refuses it unless the SHA-256 matches.
function Get-PinnedFile([string]$Url, [string]$Sha256, [string]$Dest) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if (-not (Test-Path (Split-Path $Dest))) { New-Item -ItemType Directory -Force (Split-Path $Dest) | Out-Null }
    if (-not ((Test-Path $Dest) -and (Get-FileHash $Dest -Algorithm SHA256).Hash -eq $Sha256)) {
        $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'   # the progress bar makes 5.1 downloads crawl
        try { Invoke-WebRequest $Url -OutFile $Dest -UseBasicParsing -Headers @{ 'User-Agent' = 'postmodern-windows' } }
        finally { $ProgressPreference = $old }
    }
    $got = (Get-FileHash $Dest -Algorithm SHA256).Hash
    if ($got -ne $Sha256) { Remove-Item $Dest -Force; throw "checksum mismatch for $Url (got $got)" }
    $Dest
}

function Add-CreatedPath([string]$Path) {
    if ($script:Mode -eq 'Apply') { Add-JournalEntry "path|$Path" @{ Type = 'path'; Path = $Path } }
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
