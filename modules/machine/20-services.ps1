# Background services to switch off (Start = 4, disabled). Defender and Security Center are never touched.
# Per-user services (CDPUserSvc and friends) are disabled at their template; Windows copies that to the
# per-user instance at each sign-in.

$groups = [ordered]@{
    telemetry = 'DiagTrack', 'dmwappushservice', 'DPS', 'WdiServiceHost', 'WdiSystemHost', 'PcaSvc', 'WerSvc',
                'wercplsupport', 'diagnosticshub.standardcollector.service', 'TroubleshootingSvc'
    xbox      = 'XblAuthManager', 'XblGameSave', 'XboxGipSvc', 'XboxNetApiSvc', 'BcastDVRUserService', 'CaptureService'
    hyperv    = 'vmicguestinterface', 'vmicheartbeat', 'vmickvpexchange', 'vmicrdv', 'vmicshutdown', 'vmictimesync',
                'vmicvmsession', 'vmicvss', 'HvHost'
    updaters  = 'edgeupdate', 'edgeupdatem', 'MicrosoftEdgeElevationService', 'igfxCUIService1.0.0.0', 'LMS', 'jhi_service'
    cloud     = 'CDPSvc', 'CDPUserSvc', 'OneSyncSvc', 'PimIndexMaintenanceSvc', 'UnistoreSvc', 'UserDataSvc',
                'MessagingService', 'PhoneSvc', 'SmsRouter', 'WpnService', 'WpnUserService', 'MapsBroker', 'lfsvc',
                'WalletService', 'RetailDemo'
    sensors   = 'SensorService', 'SensrSvc', 'SensorDataService', 'MixedRealityOpenXRSvc', 'spectrum',
                'SharedRealitySvc', 'perceptionsimulation'
    explorer  = 'WSearch', 'SysMain', 'Themes', 'FontCache', 'TabletInputService', 'DisplayEnhancementService',
                'TrkWks', 'UdkUserSvc'
}
if (-not $Opt.KeepUpdates) { $groups.updates = 'UsoSvc', 'WaaSMedicSvc', 'DoSvc', 'InstallService', 'PushToInstall', 'uhssvc' }
if ($Opt.NoPrinting) { $groups.printing = 'Spooler', 'PrintNotify', 'PrintWorkflowUserSvc', 'stisvc', 'Fax' }
if ($Opt.NoSharing) {
    $groups.sharing = 'TermService', 'UmRdpService', 'SessionEnv', 'LanmanServer', 'RasAuto', 'ALG', 'AJRouter',
                      'icssvc', 'SSDPSRV', 'upnphost', 'lmhosts', 'iphlpsvc', 'QWAVE'
}
if ($Opt.Aggressive) {
    # breaks smart cards, Windows Hello/fingerprint, EFS-encrypted files, offline files, work accounts
    $groups.aggressive = 'SCardSvr', 'ScDeviceEnum', 'SCPolicySvc', 'CertPropSvc', 'WbioSrvc', 'NaturalAuthentication',
                         'DmEnrollmentSvc', 'EntAppSvc', 'embeddedmode', 'CscService', 'EFS', 'MSDTC', 'DsSvc',
                         'WpcMonSvc', 'SEMgrSvc', 'svsvc', 'WarpJITSvc', 'DusmSvc', 'autotimesvc', 'wisvc', 'FrameServer'
}

foreach ($g in $groups.Keys) { foreach ($s in $groups[$g]) { Set-ServiceStart $s 4 "svc:$g" } }
Invoke-SystemQueue
