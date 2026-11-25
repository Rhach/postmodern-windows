# Defender stays fully on. This only makes it quieter on old hardware and independent of Windows Update.
# With Tamper Protection on, Windows may refuse some of these; protection itself is never lowered.
if (-not (Get-Command Get-MpPreference -EA SilentlyContinue) -or -not (Get-MpComputerStatus -EA SilentlyContinue).AMServiceEnabled) {
    Write-Step 'defender' 'Microsoft Defender not active (another antivirus?)' 'skip'
    return
}
$st = Get-MpComputerStatus
Write-Step 'defender' 'real-time protection' $(if ($st.RealTimeProtectionEnabled) { 'ok' } else { 'fail' }) $(if (-not $st.RealTimeProtectionEnabled) { 'OFF' })
Write-Step 'defender' 'definitions current' $(if ($st.AntivirusSignatureAge -le 3) { 'ok' } else { 'fail' }) $(if ($st.AntivirusSignatureAge -gt 3) { "$($st.AntivirusSignatureAge) days old" })

Set-MpPref ScanAvgCPULoadFactor 25            # scans yield the CPU
Set-MpPref EnableLowCpuPriority $true
Set-MpPref ScanOnlyIfIdleEnabled $true
Set-MpPref DisableCatchupQuickScan $false     # a missed daily scan still happens later
Set-MpPref SignatureUpdateInterval 8          # definitions every 8 hours...
Set-MpPref SignatureFallbackOrder 'MMPC|MicrosoftUpdateServer'   # ...straight from Microsoft, not via Windows Update
