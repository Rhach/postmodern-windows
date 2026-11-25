# Machine-wide policies: telemetry, ads and consumer content, web search, background activity.
$P = 'HKLM:\SOFTWARE\Policies\Microsoft'

Set-Reg "$P\Windows\DataCollection" AllowTelemetry 0 -Area 'telemetry'
Set-Reg "$P\Windows\DataCollection" DoNotShowFeedbackNotifications 1 -Area 'telemetry'
Set-Reg "$P\Windows\AppCompat" AITEnable 0 -Area 'telemetry'
Set-Reg "$P\Windows\AppCompat" DisableInventory 1 -Area 'telemetry'
Set-Reg "$P\Windows\AppCompat" DisablePCA 1 -Area 'telemetry'
Set-Reg "$P\SQMClient\Windows" CEIPEnable 0 -Area 'telemetry'
Set-Reg "$P\Windows\Windows Error Reporting" Disabled 1 -Area 'telemetry'

Set-Reg "$P\Windows\Windows Search" AllowCortana 0 -Area 'search'
Set-Reg "$P\Windows\Windows Search" DisableWebSearch 1 -Area 'search'
Set-Reg "$P\Windows\Windows Search" ConnectedSearchUseWeb 0 -Area 'search'
Set-Reg "$P\Windows\Windows Search" AllowCloudSearch 0 -Area 'search'

Set-Reg "$P\Windows\CloudContent" DisableWindowsConsumerFeatures 1 -Area 'ads'
Set-Reg "$P\Windows\CloudContent" DisableSoftLanding 1 -Area 'ads'
Set-Reg "$P\Windows\AdvertisingInfo" DisabledByGroupPolicy 1 -Area 'ads'
Set-Reg "$P\Windows\Windows Feeds" EnableFeeds 0 -Area 'ads'

Set-Reg "$P\Windows\AppPrivacy" LetAppsRunInBackground 2 -Area 'background'
Set-Reg "$P\Windows\System" EnableActivityFeed 0 -Area 'background'
Set-Reg "$P\Windows\System" PublishUserActivities 0 -Area 'background'
Set-Reg "$P\Windows\System" UploadUserActivities 0 -Area 'background'
Set-Reg "$P\Windows\GameDVR" AllowGameDVR 0 -Area 'background'
Set-Reg "$P\Windows\OneDrive" DisableFileSyncNGSC 1 -Area 'background'
Set-Reg "$P\Windows\StorageSense" AllowStorageSenseGlobal 0 -Area 'background'
Set-Reg "$P\Windows\Maps" AutoDownloadAndUpdateMapData 0 -Area 'background'
Set-Reg "$P\WindowsStore" AutoDownload 2 -Area 'background'
Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows\ScheduledDiagnostics' EnabledExecution 0 -Area 'background'
Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' MaintenanceDisabled 1 -Area 'background'

Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' NoAutoplayfornonVolume 1 -Area 'misc'
Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance' fAllowToGetHelp 0 -Area 'misc'

Set-Reg "$P\Edge" StartupBoostEnabled 0 -Area 'edge'
Set-Reg "$P\Edge" BackgroundModeEnabled 0 -Area 'edge'
Set-Reg "$P\Edge" HubsSidebarEnabled 0 -Area 'edge'

if ($Opt.NoLocation) { Set-Reg "$P\Windows\LocationAndSensors" DisableLocation 1 -Area 'location' }

if (-not $Opt.KeepUpdates) {
    Set-Reg "$P\Windows\WindowsUpdate\AU" NoAutoUpdate 1 -Area 'updates'
    Set-Reg "$P\Windows\WindowsUpdate" ExcludeWUDriversInQualityUpdate 1 -Area 'updates'
    Set-Reg "$P\Windows\DeliveryOptimization" DODownloadMode 99 -Area 'updates'
}
