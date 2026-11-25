@echo off
rem Use minilaunch as this user's shell instead of Explorer. Per-user (HKCU), so no admin needed and
rem other accounts on the PC keep the normal desktop. Takes effect at the next sign-in.
set "ML=%~dp0..\minilaunch\minilaunch.exe"
for %%I in ("%ML%") do set "ML=%%~fI"
if not exist "%ML%" ( echo minilaunch.exe not found at %ML% & exit /b 1 )
reg add "HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /t REG_SZ /d "\"%ML%\" --shell" /f >nul
echo Minimal shell set for %USERNAME%. Sign out to switch:  logoff
