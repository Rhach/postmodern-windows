# Runs as SYSTEM at boot. The lock and sign-in screens draw from pre-rendered caches under
# C:\ProgramData\Microsoft\Windows\SystemData\<SID>\ReadOnly\LockScreen_<letter>. With the debloated
# services Windows never renders them (the user's folder isn't even created), so the screen comes up
# blank. Render the configured lock image into every slot ourselves: the system account's, plus every
# user whose SystemProtectedUserData\...\LockScreen value lists slot letters.
Add-Type -AssemblyName System.Drawing
# the image the lock-screen policy points at (set-theme.ps1 sets it)
$src = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP' -EA SilentlyContinue).LockScreenImagePath
if (-not $src) { $src = 'C:\Windows\Web\Screen\minilaunch-lock.jpg' }
$data = 'C:\ProgramData\Microsoft\Windows\SystemData'
$log = 'C:\Windows\Temp\lockscreen-sync.log'
& {
    if (-not (Test-Path $src)) { "no source image"; return }
    $img = [Drawing.Image]::FromFile($src)
    $enc = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | ? MimeType -eq 'image/jpeg'
    $ep = New-Object Drawing.Imaging.EncoderParameters 1
    $ep.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality), 92L

    $sizes = @(@(1280, 800), @(1280, 768))
    $vc = Get-CimInstance Win32_VideoController | Select -First 1
    if ($vc.CurrentHorizontalResolution) { $sizes += , @([int]$vc.CurrentHorizontalResolution, [int]$vc.CurrentVerticalResolution) }
    $sizes = $sizes | Sort-Object { "$($_[0])x$($_[1])" } -Unique

    # LogonUI picks the system slot from this value (current letter first); the policy pipeline that
    # should create it never ran, and without it LogonUI ignores the image (Procmon: NAME NOT FOUND).
    $sysKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SystemProtectedUserData\S-1-5-18\AnyoneRead\LockScreen'
    $sysSlots = @(Get-ChildItem "$data\S-1-5-18\ReadOnly" -Directory -Filter 'LockScreen_*' -EA SilentlyContinue | % { $_.Name.Substring(11) })
    $order = 'P' + (-join ($sysSlots | ? { $_ -ne 'P' }))
    New-Item $sysKey -Force | Out-Null
    Set-ItemProperty $sysKey '(default)' $order
    # ...and checks which source image the slot was made from against the policy path (GPImagePath_<slot>)
    New-ItemProperty $sysKey 'GPImagePath_P' -Value $src -PropertyType String -Force | Out-Null
    # ...and the source's last-write FILETIME, split into two DWORDs, to know the copy is current
    $ft = (Get-Item $src).LastWriteTimeUtc.ToFileTimeUtc()
    # (in Windows PowerShell 0xFFFFFFFF is -1, so split arithmetically; DWORD values are stored as signed int32)
    $lo = [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]($ft % 4294967296)), 0)
    $hi = [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32][math]::Floor($ft / 4294967296)), 0)
    New-ItemProperty $sysKey 'GPImageModifiedTimeLow_P' -Value $lo -PropertyType DWord -Force | Out-Null
    New-ItemProperty $sysKey 'GPImageModifiedTimeHigh_P' -Value $hi -PropertyType DWord -Force | Out-Null
    "source FILETIME $ft -> low 0x{0:X8} high 0x{1:X8}" -f $lo, $hi
    "system LockScreen slot order: $order, GPImagePath_P = $src"

    # slot folders to fill: existing system slots + every letter a user's LockScreen value names
    $targets = @()
    $targets += Get-ChildItem "$data\S-1-5-18\ReadOnly" -Directory -Filter 'LockScreen_*' -EA SilentlyContinue | % FullName
    if (-not $targets) { $targets += "$data\S-1-5-18\ReadOnly\LockScreen_P" }
    $spud = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SystemProtectedUserData'
    foreach ($sidKey in Get-ChildItem $spud -EA SilentlyContinue) {
        $ls = Get-ItemProperty "$($sidKey.PSPath)\AnyoneRead\LockScreen" -EA SilentlyContinue
        if (-not $ls) { continue }
        $letters = ([string]$ls.'(default)').ToCharArray() | ? { $_ -match '[A-Z]' }
        foreach ($l in $letters) { $targets += "$data\$($sidKey.PSChildName)\ReadOnly\LockScreen_$l" }
    }

    foreach ($dir in $targets | Select -Unique) {
        New-Item -ItemType Directory $dir -Force | Out-Null
        # LogonUI (policy/CSP lock image) opens <slot>\LockScreen.jpg - an unscaled copy of the source.
        # Procmon showed it NAME NOT FOUND -> solid-color sign-in screen.
        Copy-Item $src (Join-Path $dir 'LockScreen.jpg') -Force
        "copied $($dir.Replace($data + '\', ''))\LockScreen.jpg"
        foreach ($s in $sizes) {
            $W = $s[0]; $H = $s[1]
            $bmp = New-Object Drawing.Bitmap $W, $H
            $g = [Drawing.Graphics]::FromImage($bmp)
            $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
            $k = [math]::Max($W / $img.Width, $H / $img.Height)
            $g.DrawImage($img, [float](($W - $img.Width * $k) / 2), [float](($H - $img.Height * $k) / 2), [float]($img.Width * $k), [float]($img.Height * $k))
            $name = 'LockScreen___{0:D4}_{1:D4}_notdimmed.jpg' -f $W, $H
            $bmp.Save((Join-Path $dir $name), $enc, $ep)
            $g.Dispose(); $bmp.Dispose()
            "wrote $($dir.Replace($data + '\', ''))\$name"
        }
    }
    $img.Dispose()
} *> $log
