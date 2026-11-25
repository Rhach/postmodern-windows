# Windows Terminal, portable edition, per user. The Store edition can't start without Explorer, and
# conhost mangles modern TUIs. Pinned release, verified by SHA-256.
$ver = '1.25.3131.0'
$url = "https://github.com/microsoft/terminal/releases/download/v$ver/Microsoft.WindowsTerminal_${ver}_x64.zip"
$sha = '273D6F1502ECA1B5BD95C43F5D4D7EC72CC503DF340102F8659717E5BEAD3437'
$dir = Join-Path $env:LOCALAPPDATA 'postmodern-windows\terminal'
$exe = Join-Path $dir 'WindowsTerminal.exe'

$have = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { $null }
if ($have -like "$ver*") { Write-Step 'terminal' "Windows Terminal $ver" 'ok' }
elseif ($script:Mode -ne 'Apply') { Write-Step 'terminal' "Windows Terminal $ver" 'change' $(if ($have) { "has $have" } else { 'not installed' }) }
else {
    try {
        if (-not (Test-Path $dir)) { Add-CreatedPath $dir }
        $tmp = Join-Path $env:TEMP "pmw-terminal-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $zip = Get-PinnedFile $url $sha (Join-Path $tmp 'terminal.zip')
        Expand-Archive $zip (Join-Path $tmp 'x') -Force
        $inner = Get-ChildItem (Join-Path $tmp 'x') -Directory | Select -First 1   # terminal-<version>\
        New-Item -ItemType Directory -Force $dir | Out-Null
        Copy-Item (Join-Path $inner.FullName '*') $dir -Recurse -Force
        New-Item -ItemType File (Join-Path $dir '.portable') -Force | Out-Null   # settings next to the exe
        Remove-Item $tmp -Recurse -Force
        Write-Step 'terminal' "Windows Terminal $ver" 'change'
    } catch { Write-Step 'terminal' "Windows Terminal $ver" 'fail' $_.Exception.Message }
}

# Starting settings (only when there are none: never overwrite the user's own). set-theme.ps1 adds the
# colour scheme.
$settings = Join-Path $dir 'settings\settings.json'
if (Test-Path $settings) { Write-Step 'terminal' 'settings.json' 'ok' }
elseif ($script:Mode -ne 'Apply') { Write-Step 'terminal' 'settings.json' 'change' 'missing' }
else {
    $pwsh = @(Get-Command pwsh.exe -EA SilentlyContinue) + @(Get-Item "$env:ProgramFiles\PowerShell\7\pwsh.exe" -EA SilentlyContinue) | Select -First 1
    $shell = if ($pwsh) { 'pwsh.exe -NoLogo' } else { 'powershell.exe -NoLogo' }
    $font = if ($Opt.Font -eq 'Iosevka') { 'Iosevka' } else { 'Cascadia Mono' }
    $json = [ordered]@{
        '$schema' = 'https://aka.ms/terminal-profiles-schema'
        copyOnSelect = $true; defaultProfile = '{6f2a1c3e-8b4d-4e7a-9c15-3d0b7e2f9a41}'; disableAnimations = $true
        launchMode = 'maximized'; theme = 'dark'; useAcrylicInTabRow = $false; 'warning.confirmCloseAllTabs' = $false
        profiles = [ordered]@{
            defaults = [ordered]@{ cursorShape = 'bar'; font = @{ face = $font; size = 12 }; historySize = 20000
                                   opacity = 100; padding = '8, 6'; useAcrylic = $false }
            list = @(
                [ordered]@{ guid = '{6f2a1c3e-8b4d-4e7a-9c15-3d0b7e2f9a41}'; name = 'PowerShell'; commandline = $shell; startingDirectory = '%USERPROFILE%' },
                [ordered]@{ guid = '{0caa0dad-35be-5f56-a8ff-afceeeaa6101}'; name = 'Command Prompt'; commandline = 'cmd.exe'; startingDirectory = '%USERPROFILE%' }
            )
        }
        schemes = @()
    }
    New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
    $json | ConvertTo-Json -Depth 8 | Set-Content $settings -Encoding UTF8
    Write-Step 'terminal' 'settings.json' 'change'
}
