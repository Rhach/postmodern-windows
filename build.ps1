# Builds minilaunch with the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
# Output: build\minilaunch\ - minilaunch.exe plus its scripts and themes, laid out ready to run or install.
#   .\build.ps1              build
#   .\build.ps1 -Clean       delete build\ first
param([switch]$Clean)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'minilaunch'
$out = Join-Path $PSScriptRoot 'build\minilaunch'
if ($Clean -and (Test-Path (Join-Path $PSScriptRoot 'build'))) { Remove-Item (Join-Path $PSScriptRoot 'build') -Recurse -Force }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x C# compiler (csc.exe) not found' }

New-Item -ItemType Directory -Force $out | Out-Null
& $csc /nologo /target:winexe /optimize+ /out:"$out\minilaunch.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "$src\minilaunch.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

Copy-Item "$src\set-theme.ps1", "$src\wifi.ps1" $out -Force
Copy-Item "$src\themes" $out -Recurse -Force
Write-Host "built $out\minilaunch.exe"
