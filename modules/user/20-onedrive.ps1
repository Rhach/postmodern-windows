# OneDrive: installed per user, so it's removed per user. Files already synced stay on disk in the
# OneDrive folder; uninstall.ps1 reinstalls the client.
$exe = Join-Path $env:LOCALAPPDATA 'Microsoft\OneDrive\OneDrive.exe'
$setup = @("$env:WINDIR\SysWOW64\OneDriveSetup.exe", "$env:WINDIR\System32\OneDriveSetup.exe") | ? { Test-Path $_ } | Select -First 1
if (-not (Test-Path $exe)) { Write-Step 'onedrive' 'OneDrive' 'ok'; return }
if ($script:Mode -ne 'Apply') { Write-Step 'onedrive' 'OneDrive' 'change' 'installed'; return }
if (-not $setup) { Write-Step 'onedrive' 'OneDrive' 'fail' 'OneDriveSetup.exe not found'; return }
Add-JournalEntry 'onedrive' @{ Type = 'onedrive'; Setup = $setup }
Get-Process OneDrive -EA SilentlyContinue | Stop-Process -Force
Start-Process $setup '/uninstall' -Wait
Write-Step 'onedrive' 'OneDrive' $(if (Test-Path $exe) { 'fail' } else { 'change' })
