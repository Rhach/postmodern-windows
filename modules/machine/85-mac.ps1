# MacBooks: replace Apple's Boot Camp trackpad driver with imbushuo/mac-precision-touchpad (Microsoft-signed),
# which makes the trackpad a Windows Precision Touchpad: proper scrolling, and the three-finger swipes
# minilaunch uses to switch windows. Only runs when the driver matches a trackpad that is present.
if ((Get-CimInstance Win32_ComputerSystem).Manufacturer -notmatch 'Apple') { return }

$url = 'https://github.com/imbushuo/mac-precision-touchpad/releases/download/2105-3979/Drivers-amd64-ReleaseMSSigned.zip'
$sha = 'CEA0449AA773DBBB70ABDFC787BE99335DABBD2719588CEBBB8806F397B4FAD2'

$installed = Get-WindowsDriver -Online -EA SilentlyContinue | ? { $_.OriginalFileName -like '*\AmtPtpDevice.inf' } | Select -First 1
if ($installed) { Write-Step 'trackpad' "precision touchpad driver ($($installed.Driver))" 'ok'; return }
if ($script:Mode -ne 'Apply') { Write-Step 'trackpad' 'precision touchpad driver' 'change' 'Apple driver now'; return }

try {
    $work = Join-Path $Opt.InstallDir 'downloads\mac-ptp'   # admin-only folder: nothing can swap the files before pnputil
    $zip = Get-PinnedFile $url $sha (Join-Path $work 'driver.zip')
    Expand-Archive $zip $work -Force
    $inf = Get-ChildItem $work -Recurse -Filter 'AmtPtpDevice.inf' | Select -First 1
    $cat = Get-ChildItem $work -Recurse -Filter '*.cat' | Select -First 1
    if ((Get-AuthenticodeSignature $cat.FullName).Status -ne 'Valid') { throw 'driver catalog signature is not valid' }

    # the hardware IDs the driver supports vs the devices actually present
    $ids = Select-String $inf.FullName -Pattern '(USB|HID|ACPI)\\[^\s,]+' -AllMatches | % { $_.Matches.Value.ToUpper() } | Sort -Unique
    $present = Get-PnpDevice -PresentOnly -EA SilentlyContinue | ? { $_.InstanceId -match 'VID_05AC|APP000D|SPI' } |
        % { (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_HardwareIds -EA SilentlyContinue).Data } | % { $_.ToUpper() }
    if (-not ($present | ? { $ids -contains $_ })) { Write-Step 'trackpad' 'no supported Apple trackpad found' 'skip'; return }

    pnputil /add-driver $inf.FullName /install | Out-Null
    $new = Get-WindowsDriver -Online | ? { $_.OriginalFileName -like '*\AmtPtpDevice.inf' } | Select -First 1
    if (-not $new) { throw "pnputil exit $LASTEXITCODE" }
    Add-JournalEntry 'driver|AmtPtpDevice' @{ Type = 'driver'; Published = $new.Driver }
    Write-Step 'trackpad' "precision touchpad driver ($($new.Driver))" 'change'
} catch { Write-Step 'trackpad' 'precision touchpad driver' 'fail' $_.Exception.Message }
