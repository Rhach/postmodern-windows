# Scheduled tasks to switch off: telemetry, diagnostics, maintenance chores and bundled-app updaters.
# Defrag stays on: on SSDs it is what sends TRIM. Defender's tasks are never touched.

$patterns = @(
    '\Microsoft\Windows\Application Experience\*', '\Microsoft\Windows\Autochk\*',
    '\Microsoft\Windows\Customer Experience Improvement Program\*', '\Microsoft\Windows\DiskDiagnostic\*',
    '\Microsoft\Windows\Feedback\Siuf\*', '\Microsoft\Windows\Windows Error Reporting\*', '\Microsoft\Windows\Maps\*',
    '\Microsoft\Windows\CloudExperienceHost\*', '\Microsoft\Windows\Diagnosis\*',
    '\Microsoft\Windows\Power Efficiency Diagnostics\*', '\Microsoft\Windows\Location\*', '\Microsoft\Windows\Flighting\*',
    '\Microsoft\Windows\FileHistory\*', '\Microsoft\Windows\Shell\*', '\Microsoft\Windows\Work Folders\*',
    '\Microsoft\Windows\Workplace Join\*', '\Microsoft\Windows\Mobile Broadband Accounts\*', '\Microsoft\Windows\NetTrace\*',
    '\Microsoft\Windows\RetailDemo\*', '\Microsoft\Windows\Speech\*', '\Microsoft\Windows\WDI\*',
    '\Microsoft\Windows\XblGameSave\*', '\Microsoft\Windows\MemoryDiagnostic\*', '\Microsoft\Windows\DiskFootprint\*',
    '\Microsoft\Windows\PI\*', '\Microsoft\Windows\SettingSync\*', '\Microsoft\Windows\Device Information\*',
    '\Microsoft\Windows\Windows Media Sharing\*', '\Microsoft\Windows\Sysmain\*', '\Microsoft\XblGameSave\*',
    '\MicrosoftEdgeUpdate*', '\OneDrive*'
)
if (-not $Opt.KeepUpdates) {
    $patterns += '\Microsoft\Windows\WindowsUpdate\*', '\Microsoft\Windows\UpdateOrchestrator\*',
                 '\Microsoft\Windows\InstallService\*', '\Microsoft\Windows\Servicing\*', '\Microsoft\Windows\WaaSMedic\*'
}
foreach ($p in $patterns) { Set-TasksDisabled $p }
Invoke-SystemQueue
