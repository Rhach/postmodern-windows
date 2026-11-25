# minilaunch and its scripts go to Program Files, where only administrators can write. That matters: the
# lock-screen task runs its script as SYSTEM, so that script must not be editable by ordinary users.
$dir = $Opt.InstallDir
$ml = Join-Path $dir 'minilaunch\minilaunch.exe'

if ($script:Mode -eq 'Apply') {
    $fresh = -not (Test-Path $ml)
    if ($fresh) { Add-CreatedPath $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    # the installer's own files, so uninstall.ps1 and doctor.ps1 work from the install folder
    foreach ($item in 'install.ps1', 'uninstall.ps1', 'doctor.ps1', 'build.ps1', 'lib', 'modules', 'scripts', 'README.md', 'LICENSE') {
        $src = Join-Path $Opt.SourceDir $item
        if ((Test-Path $src) -and ((Resolve-Path $src).Path -ne (Join-Path $dir $item))) { Copy-Item $src $dir -Recurse -Force }
    }
    $built = Join-Path $Opt.SourceDir 'build\minilaunch'
    if ((Resolve-Path $built).Path -ne (Join-Path $dir 'minilaunch')) {
        $running = Get-Process minilaunch -EA SilentlyContinue | ? { $_.Path -eq $ml }
        if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500 }   # an update while it is the shell
        Copy-Item $built $dir -Recurse -Force
    }
    Write-Step 'files' $dir 'change'
} elseif (Test-Path $ml) { Write-Step 'files' $ml 'ok' } else { Write-Step 'files' $ml 'change' 'not installed' }

# Lock and sign-in screen: show the theme's picture, sharp rather than blurred.
$sys = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
Set-Reg $sys DisableLogonBackgroundImage 0 -Area 'lockscreen'
Set-Reg $sys DisableAcrylicBackgroundOnLogon 1 -Area 'lockscreen'

# With the trimmed services Windows never renders LogonUI's image caches; this SYSTEM task does it at boot.
$syncScript = Join-Path $dir 'scripts\lockscreen-sync.ps1'
$task = Get-ScheduledTask -TaskName 'LockScreenSync' -EA SilentlyContinue
$taskOk = $task -and ($task.Actions | ? { $_.Arguments -like "*$syncScript*" }) -and $task.Principal.UserId -match 'SYSTEM'
if ($taskOk) { Write-Step 'lockscreen' 'LockScreenSync task' 'ok' }
elseif ($script:Mode -ne 'Apply') { Write-Step 'lockscreen' 'LockScreenSync task' 'change' $(if ($task) { 'points elsewhere' } else { 'missing' }) }
else {
    if (-not $task) { Add-JournalEntry 'schtask|LockScreenSync' @{ Type = 'schtask'; Name = 'LockScreenSync' } }
    $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$syncScript`""
    $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName 'LockScreenSync' -Action $a -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $s -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Write-Step 'lockscreen' 'LockScreenSync task' 'change'
}

# The theme's lock image (set-theme.ps1 writes these; record the originals first so uninstall restores them)
$csp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
foreach ($n in 'LockScreenImagePath', 'LockScreenImageUrl', 'LockScreenImageStatus') { Save-RegOriginal $csp $n }
$lockFile = 'C:\Windows\Web\Screen\minilaunch-lock.jpg'
if ($script:Mode -eq 'Apply') {
    if (-not (Test-Path $lockFile)) { Add-CreatedPath $lockFile }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'minilaunch\set-theme.ps1') -Theme $Opt.Theme -LockOnly
    Write-Step 'lockscreen' "theme $($Opt.Theme)" $(if ($LASTEXITCODE -eq 0) { 'change' } else { 'fail' })
} elseif ((Get-RegState $csp 'LockScreenImagePath').Value -eq $lockFile) { Write-Step 'lockscreen' 'lock image policy' 'ok' }
else { Write-Step 'lockscreen' 'lock image policy' 'change' }

# Optional: Iosevka for the terminal (a 169 MB download, so opt-in; the default is the Terminal's own Cascadia Mono)
if ($Opt.Font -eq 'Iosevka') {
    $faces = 'Iosevka-Regular.ttf', 'Iosevka-Bold.ttf', 'Iosevka-Italic.ttf', 'Iosevka-BoldItalic.ttf'
    $missing = @($faces | ? { -not (Test-Path (Join-Path "$env:WINDIR\Fonts" $_)) })
    if (-not $missing) { Write-Step 'font' 'Iosevka' 'ok' }
    elseif ($script:Mode -ne 'Apply') { Write-Step 'font' 'Iosevka' 'change' '169 MB download' }
    else {
        try {
            $zip = Get-PinnedFile 'https://github.com/be5invis/Iosevka/releases/download/v34.9.1/PkgTTF-Iosevka-34.9.1.zip' `
                '7A77A3DFA0E2D713A8920A30624863E23DF2867A1F55A5E3D6D23DAD07C14C65' (Join-Path $dir 'downloads\PkgTTF-Iosevka-34.9.1.zip')
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $z = [IO.Compression.ZipFile]::OpenRead($zip)
            try {
                foreach ($f in $faces) {
                    $entry = $z.Entries | ? { $_.Name -eq $f } | Select -First 1
                    $target = Join-Path "$env:WINDIR\Fonts" $f
                    if (-not (Test-Path $target)) { Add-CreatedPath $target }
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
                    $style = (($f -replace '^Iosevka-|\.ttf$', '') -replace 'BoldItalic', 'Bold Italic' -replace '^Regular$', '')
                    Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts' ("Iosevka $style".Trim() + ' (TrueType)') $f 'String' -Area 'font'
                }
            } finally { $z.Dispose() }
            Remove-Item $zip -Force
        } catch { Write-Step 'font' 'Iosevka' 'fail' $_.Exception.Message }
    }
}
