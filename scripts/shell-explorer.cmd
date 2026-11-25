@echo off
rem Back to the normal Explorer desktop for this user: removes the per-user shell override.
rem Takes effect at the next sign-in. If the machine-wide shell (HKLM) was also changed, this
rem cannot fix that without admin; it says so below.
reg delete "HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /f >nul 2>&1
for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell 2^>nul ^| find "Shell"') do set "HKLMSHELL=%%B"
if /i not "%HKLMSHELL%"=="explorer.exe" echo Note: the machine-wide shell is "%HKLMSHELL%", not explorer.exe - an admin must reset it.
echo Explorer desktop restored for %USERNAME%. Sign out to switch:  logoff
