<#
Checks this PC against what install.ps1 set up (with the options it was installed with) and lists anything
that has drifted, for example after a Windows update re-enabled a service. Changes nothing.

    powershell -ExecutionPolicy Bypass -File .\doctor.ps1
#>
& (Join-Path $PSScriptRoot 'install.ps1') -Doctor -Force
exit $LASTEXITCODE
