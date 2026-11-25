# A System Restore point before anything else changes: the safety net under uninstall.ps1.
Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' SystemRestorePointCreationFrequency 0 -Area 'restore'
Invoke-Action 'restore' 'restore point "Before postmodern-windows"' {
    Enable-ComputerRestore -Drive "$env:SystemDrive\" -EA Stop
    Checkpoint-Computer -Description 'Before postmodern-windows' -RestorePointType MODIFY_SETTINGS -EA Stop
}
