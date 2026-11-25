# Defender stays fully on. This only makes it quieter on old hardware and independent of Windows Update.
# With Tamper Protection on, Windows may refuse some of these; protection itself is never lowered.
if (-not (Get-Command Get-MpPreference -EA SilentlyContinue) -or -not (Get-MpComputerStatus -EA SilentlyContinue).AMServiceEnabled) {
    Write-Step 'defender' 'Microsoft Defender not active (another antivirus?)' 'skip'
    return
}
$st = Get-MpComputerStatus
Write-Step 'defender' 'real-time protection' $(if ($st.RealTimeProtectionEnabled) { 'ok' } else { 'fail' }) $(if (-not $st.RealTimeProtectionEnabled) { 'OFF' })
# A fresh install media ships years-old definitions; get current ones now instead of at the next schedule.
if ($st.AntivirusSignatureAge -gt 1 -and $script:Mode -ne 'Check') {
    Invoke-Action 'defender' "update definitions ($($st.AntivirusSignatureAge) days old)" {
        # Defender often starts updating by itself the moment it's pointed at Microsoft; if so, wait for that one
        foreach ($source in 'MMPC', 'MicrosoftUpdateServer') {
            try { Update-MpSignature -UpdateSource $source -EA Stop; break }
            catch { if ($_.Exception.Message -match 'already in progress') { break } }
        }
        $deadline = (Get-Date).AddMinutes(5)
        while ((Get-MpComputerStatus).AntivirusSignatureAge -gt 1 -and (Get-Date) -lt $deadline) { Start-Sleep 10 }
    }
    if ($script:Mode -eq 'Apply') { $st = Get-MpComputerStatus }
}
if ($script:Mode -ne 'WhatIf') {
    Write-Step 'defender' 'definitions current' $(if ($st.AntivirusSignatureAge -le 3) { 'ok' } else { 'fail' }) $(if ($st.AntivirusSignatureAge -gt 3) { "$($st.AntivirusSignatureAge) days old" })
}

Set-MpPref ScanAvgCPULoadFactor 25            # scans yield the CPU
Set-MpPref EnableLowCpuPriority $true
Set-MpPref ScanOnlyIfIdleEnabled $true
Set-MpPref DisableCatchupQuickScan $false     # a missed daily scan still happens later
Set-MpPref SignatureUpdateInterval 8          # definitions every 8 hours...
Set-MpPref SignatureFallbackOrder 'MMPC|MicrosoftUpdateServer'   # ...straight from Microsoft, not via Windows Update
