# Inventory: everything changed on the reference machine

Reference machine: MacBook Pro 9,2 (2012), Windows 10 Pro 22H2 (19045), 8 GB RAM.
Sources: the pre-change snapshots (`services.reg`, `services-before.csv`, `tasks-before.csv`),
the Claude Code session transcripts of 2026-11-25, and a diff against the live machine.

Disposition column — what the installer does with each item:
- **core**: always applied
- **opt**: applied only with an explicit switch
- **mac**: applied only when Apple hardware is detected
- **no**: not part of the installer (machine-specific, superseded, or too hacky)

## 1. Shell and desktop

| Change | Mechanism | Disposition |
|---|---|---|
| minilaunch replaces Explorer as the shell | `Winlogon\Shell` in HKLM **and** HKCU | core, **HKCU only**; files in `C:\Program Files\postmodern-windows` (admin-only, because a SYSTEM task runs a script from there) |
| Notification area (tray) for apps like Boot Camp | minilaunch's own `Shell_TrayWnd`; icons shown in the dock | core (added after the reference setup: without a tray Boot Camp quit, taking the MacBook brightness keys with it) |
| Launcher, desktop HUD, dock, swipe switcher | minilaunch.exe (C#, built with in-box csc) | core |
| Wallpaper | `minilaunch\wallpaper.jpg` | core (bundled, credited) |
| Shell switch scripts | `shell-explorer.cmd` / `shell-minimal.cmd` | core (generalised) |
| Wi-Fi picker | `wifi.ps1` | core |
| Lock + sign-in image | PersonalizationCSP keys, `DisableLogonBackgroundImage=0`, `DisableAcrylicBackgroundOnLogon=1`, plus the `LockScreenSync` SYSTEM task (`lockscreen-sync.ps1`) that writes LogonUI's SYSTEM-profile copies | core |
| Iosevka font, conhost font = Iosevka→Consolas | font install + `Console\TrueTypeFont` + `HKCU\Console` | opt (`-Font Iosevka`, a 169 MB download); default is the Terminal's bundled Cascadia Mono |
| Portable Windows Terminal, per-theme colour scheme | unpacked to `bin\terminal` | core: v1.25.3131.0, SHA-256 pinned, per user in `%LOCALAPPDATA%\postmodern-windows\terminal` |
| `AutoRestartShell` | toggled during development, now back to 1 | no |
| First attempt: `minishell.vbs` shell | superseded by minilaunch | no |

## 2. Services (131 Start values differ from the snapshot)

Template user services (`*UserSvc`, type 96) were disabled at the template; per-user
instances (`_6a68b`, now `_3cd7e8`) follow the template on each logon.

| Group | Services | Disposition |
|---|---|---|
| Telemetry / diagnostics | DiagTrack, dmwappushservice, DPS, WdiServiceHost, WdiSystemHost, PcaSvc, WerSvc, wercplsupport, diagnosticshub.standardcollector.service, TroubleshootingSvc | core |
| Update stack | UsoSvc, WaaSMedicSvc, DoSvc, InstallService, PushToInstall, uhssvc | core (`-KeepUpdates` opts out) |
| Xbox / gaming | XblAuthManager, XblGameSave, XboxGipSvc, XboxNetApiSvc, BcastDVRUserService, CaptureService | core |
| Hyper-V guest | vmic* (×8), HvHost | core |
| Edge / vendor updaters | edgeupdate, edgeupdatem, MicrosoftEdgeElevationService, igfxCUIService1.0.0.0, LMS | core (skips if absent) |
| Sync / cloud / phone | CDPSvc, CDPUserSvc, OneSyncSvc, PimIndexMaintenanceSvc, UnistoreSvc, UserDataSvc, MessagingService, PhoneSvc, SmsRouter, WpnService, WpnUserService, MapsBroker, lfsvc, WalletService, RetailDemo | core |
| Sensors / mixed reality | SensorService, SensrSvc, SensorDataService, MixedRealityOpenXRSvc, spectrum, SharedRealitySvc, perceptionsimulation | core |
| Explorer-only extras | WSearch, SysMain, Themes, FontCache, TabletInputService, DisplayEnhancementService, TrkWks, UdkUserSvc | core |
| Printing / scanning / fax | Spooler, PrintNotify, PrintWorkflowUserSvc, stisvc, Fax | opt (`-NoPrinting`) |
| Remote desktop / sharing | TermService, UmRdpService, SessionEnv, LanmanServer, RasAuto, ALG, AJRouter, icssvc, SSDPSRV, upnphost, lmhosts, iphlpsvc, QWAVE | opt (`-NoSharing`) |
| Smart card / biometrics / enterprise | SCardSvr, ScDeviceEnum, SCPolicySvc, CertPropSvc, WbioSrvc, NaturalAuthentication, DmEnrollmentSvc, EntAppSvc, embeddedmode, CscService, EFS, MSDTC, DsSvc, WpcMonSvc, SEMgrSvc, svsvc, WarpJITSvc, DusmSvc, autotimesvc, wisvc, FrameServer | opt (`-Aggressive`) — EFS and WbioSrvc break encrypted files and Windows Hello |
| Defender + Security Center | WinDefend, WdNisSvc, WdNisDrv, WdFilter, WdBoot, MDCoreSvc, Sense, SecurityHealthService, wscsvc | **no** — disabled, then restored to original values |
| NordVPN extras | nordsec-threatprotection-service, NordUpdaterService | no |
| Changed by Windows, not us | BITS 3→2, WSLService 2→3 (WSL, deliberately manual) | no |

## 3. Scheduled tasks

Disabled by path: Application Experience, Autochk, CEIP, DiskDiagnostic, Feedback\Siuf, Windows
Error Reporting, Maps, CloudExperienceHost, Diagnosis, Power Efficiency Diagnostics, Location,
Flighting, FileHistory, Shell, Work Folders, Workplace Join, Mobile Broadband Accounts, NetTrace,
RetailDemo, Speech, WDI, XblGameSave, MemoryDiagnostic, DiskFootprint, PI, SettingSync, Device
Information, Windows Media Sharing, Sysmain, MicrosoftEdgeUpdate*, OneDrive* → **core**.

WindowsUpdate, UpdateOrchestrator, InstallService, Servicing, WaaSMedic → **core** (`-KeepUpdates` opts out).
Windows Defender tasks → re-enabled (**no**).

Disabled on the reference machine but **left alone by the installer**, because they matter more on
someone else's PC: Defrag (on SSDs it sends TRIM), Maintenance, Chkdsk, Registry (registry backups),
StateRepository, Time Zone, Management\Provisioning, Office (keeps Office itself updated).

## 4. Policies and registry tweaks

| Area | Values | Disposition |
|---|---|---|
| Telemetry | DataCollection `AllowTelemetry=0`, `DoNotShowFeedbackNotifications=1`; AppCompat `AITEnable=0`, `DisableInventory=1`, `DisablePCA=1`; SQMClient `CEIPEnable=0`; WER `Disabled=1`; Siuf `NumberOfSIUFInPeriod=0` | core |
| Search / Cortana / web | `AllowCortana=0`, `DisableWebSearch=1`, `ConnectedSearchUseWeb=0`, `AllowCloudSearch=0`; HKCU Search toggles | core |
| Consumer content / ads | CloudContent `DisableWindowsConsumerFeatures=1`, `DisableSoftLanding=1`; AdvertisingInfo; 17 ContentDeliveryManager values; Feeds | core |
| Background apps / activity | `LetAppsRunInBackground=2`, `GlobalUserDisabled=1`; `EnableActivityFeed`/`PublishUserActivities`/`UploadUserActivities=0` | core |
| Game DVR | policy + HKCU GameConfigStore / GameBar | core |
| OneDrive | `DisableFileSyncNGSC=1`; Run entries removed; OneDriveSetup `/uninstall` | core |
| Edge | `StartupBoostEnabled=0`, `BackgroundModeEnabled=0`, `HubsSidebarEnabled=0` | core |
| Storage / maps / store | `AllowStorageSenseGlobal=0`, `AutoDownloadAndUpdateMapData=0`, Store `AutoDownload=2` | core |
| Location | `DisableLocation=1` | opt (breaks auto time zone / Find my device) |
| Maintenance | `ScheduledDiagnostics\EnabledExecution=0`, `Schedule\Maintenance\MaintenanceDisabled=1` | core |
| Misc | AutoPlay off for non-volume devices, Remote Assistance off | core |
| Windows Update | `NoAutoUpdate=1`, `ExcludeWUDriversInQualityUpdate=1`, `DODownloadMode=99` | core (`-KeepUpdates` opts out) |
| svchost grouping | `Control\SvcHostSplitThresholdInKB=0xFFFFFF` (fewer svchost processes) | core |
| Visual effects | best performance: `VisualFXSetting=2`, `UserPreferencesMask`, `MenuShowDelay=0`, `DragFullWindows=0`, `MinAnimate=0`, transparency/Aero Peek/list shadows off | core |
| Explorer prefs | Task View / Cortana buttons off, no recent tracking, open to This PC, no startup delay | core |
| Ink / typing data | `RestrictImplicitInkCollection=1`, `RestrictImplicitTextCollection=1` | core |
| Touchpad | `PrecisionTouchPad\ScrollDirection=0xFFFFFFFF` (natural scrolling) | mac |
| CPU mitigations | `FeatureSettingsOverride=3` / `Mask=3` — Spectre/Meltdown mitigations off | opt (`-NoCpuMitigations`); mitigations stay on by default. Restored on the reference machine 2026-11-25 |
| Kernel paging | `DisablePagingExecutive=1` | core |
| Fast startup | `HiberbootEnabled=0` | core |
| Defender policies | DisableAntiSpyware/AntiVirus, real-time, Spynet, signature, systray, SmartScreen | **no** — removed again |

## 5. System settings

| Change | Disposition |
|---|---|
| `powercfg -h off` (no hibernation, frees hiberfil.sys) | core |
| `fsutil behavior set disablelastaccess 1`, `disable8dot3 1` | core |
| `bcdedit /set hypervisorlaunchtype off` | opt — breaks WSL2, Docker/Podman, VBS |
| Restore point "Before debloat" created first | core (installer does the same) |

## 6. Removed software and features

| Item | Disposition |
|---|---|
| AppX: OutlookForWindows, DevHome, Wallet, Advertising.Xaml, MicrosoftEdgeDevToolsClient (also deprovisioned) | core |
| Optional features: PowerShell v2, WorkFolders-Client, XPS services, MSRDC-Infrastructure, SmbDirect, WCF-TCP-PortSharing45, Internet Printing client | core (PowerShell v2 removal is a security win) |
| `SearchApp.exe` renamed via takeown | **no** — fragile file-ownership hack, irrelevant without Explorer |

## 7. Hardware

| Change | Disposition |
|---|---|
| imbushuo mac-precision-touchpad driver replaces Apple's Boot Camp driver | mac (rollback included) |
| D-Link USB Wi-Fi adapter disabled (driver held ~140 MB non-paged pool) | no |
| NordVPN OpenVPN DCO adapter disabled | no |

## 8. Defender (final state)

On: real-time, behavior, IOAV, network inspection, cloud (MAPS advanced), PUA, Tamper Protection.
Signatures from MMPC first, every 8 h. Scans: daily quick scan at 12:00, idle-only, low priority,
25 % CPU cap, catch-up scans on. The installer applies only this tuning; it never disables anything.

## 9. Not included anywhere

NordVPN meshnet-only logon task (`NordMeshnetOnly`, `meshnet-start.ps1`), diagnostics left on
the reference machine (`PROCMON25` driver, `optimize-backup\procmon`, `pooltags.ps1`, lockdiag files).
