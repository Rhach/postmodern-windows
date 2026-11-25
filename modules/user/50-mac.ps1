# MacBooks: natural (Mac-style) scrolling on the precision touchpad.
if ((Get-CimInstance Win32_ComputerSystem).Manufacturer -notmatch 'Apple') { return }
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad' ScrollDirection -1 -Area 'trackpad'
