# Wi-Fi picker for the minimal shell (replaces the taskbar network flyout).
$iface = (netsh wlan show interfaces | Select-String '^\s+Name\s+:\s+(.+)$' | Select -First 1).Matches.Groups[1].Value.Trim()
$cur = (netsh wlan show interfaces | Select-String '^\s+SSID\s+:\s+(.+)$' | Select -First 1)
Write-Host "Interface: $iface    Connected to: $(if ($cur) { $cur.Matches.Groups[1].Value.Trim() } else { '(nothing)' })`n"

$nets = @(); $ssid = $null
foreach ($line in netsh wlan show networks mode=bssid interface="$iface") {
    if ($line -match '^SSID \d+ : (.*)$') { $ssid = $matches[1].Trim() }
    elseif ($line -match '^\s+Authentication\s+:\s+(.+)$' -and $ssid) { $auth = $matches[1].Trim() }
    elseif ($line -match '^\s+Signal\s+:\s+(\d+)%' -and $ssid) {
        if (-not ($nets | ? Ssid -eq $ssid)) { $nets += [pscustomobject]@{ Ssid = $ssid; Signal = [int]$matches[1]; Auth = $auth } }
    }
}
$nets = @($nets | ? Ssid | Sort Signal -Descending)
$known = @(netsh wlan show profiles | Select-String 'All User Profile\s+:\s+(.+)$' | % { $_.Matches.Groups[1].Value.Trim() })
if (-not $nets) { Write-Host 'No networks found.'; Read-Host 'Enter to close'; return }

for ($i = 0; $i -lt $nets.Count; $i++) {
    $n = $nets[$i]
    "{0,2}) {1,-32} {2,3}%  {3}{4}" -f ($i + 1), $n.Ssid, $n.Signal, $n.Auth, $(if ($n.Ssid -in $known) { '  [saved]' } else { '' })
}
$pick = Read-Host "`nNumber to connect (Enter to cancel)"
if (-not $pick) { return }
if ($pick -notmatch '^\d+$' -or [int]$pick -lt 1 -or [int]$pick -gt $nets.Count) { Write-Host "No network $pick."; Read-Host 'Enter to close'; return }
$n = $nets[[int]$pick - 1]

if ($n.Ssid -notin $known) {
    $esc = [Security.SecurityElement]::Escape($n.Ssid)
    $hex = -join ([Text.Encoding]::UTF8.GetBytes($n.Ssid) | % { $_.ToString('X2') })
    if ($n.Auth -eq 'Open') {
        $sec = '<security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security>'
    } else {
        $pw = Read-Host "Password for $($n.Ssid)" -AsSecureString
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw))
        $authType = if ($n.Auth -match 'WPA3') { 'WPA3SAE' } else { 'WPA2PSK' }
        $sec = "<security><authEncryption><authentication>$authType</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>$([Security.SecurityElement]::Escape($plain))</keyMaterial></sharedKey></security>"
    }
    $xml = "<?xml version=`"1.0`"?><WLANProfile xmlns=`"http://www.microsoft.com/networking/WLAN/profile/v1`"><name>$esc</name><SSIDConfig><SSID><hex>$hex</hex><name>$esc</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM>$sec</MSM></WLANProfile>"
    $tmp = Join-Path $env:TEMP "wifi-$hex.xml"
    $xml | Set-Content $tmp -Encoding UTF8
    netsh wlan add profile filename="$tmp" user=current | Out-Null
    Remove-Item $tmp -Force
}
netsh wlan connect name="$($n.Ssid)" interface="$iface"
Start-Sleep 5
netsh wlan show interfaces | Select-String '^\s+(State|SSID|Signal)\s+:'
Read-Host "`nEnter to close"
