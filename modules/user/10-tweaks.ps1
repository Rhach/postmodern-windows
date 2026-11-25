# Per-user settings: no ads, suggestions or tracking; visual effects set for speed.
$cu = 'HKCU:\Software\Microsoft\Windows\CurrentVersion'

Set-Reg "$cu\BackgroundAccessApplications" GlobalUserDisabled 1 -Area 'background'
Set-Reg "$cu\Search" BackgroundAppGlobalToggle 0 -Area 'search'
Set-Reg "$cu\Search" BingSearchEnabled 0 -Area 'search'
Set-Reg "$cu\Search" CortanaConsent 0 -Area 'search'
Set-Reg "$cu\Search" SearchboxTaskbarMode 0 -Area 'search'
Set-Reg "$cu\AdvertisingInfo" Enabled 0 -Area 'ads'
Set-Reg "$cu\Feeds" ShellFeedsTaskbarViewMode 2 -Area 'ads'
Set-Reg "$cu\PushNotifications" ToastEnabled 0 -Area 'ads'
foreach ($n in 'ContentDeliveryAllowed', 'OemPreInstalledAppsEnabled', 'PreInstalledAppsEnabled', 'PreInstalledAppsEverEnabled',
               'SilentInstalledAppsEnabled', 'SystemPaneSuggestionsEnabled', 'SoftLandingEnabled', 'RotatingLockScreenEnabled',
               'RotatingLockScreenOverlayEnabled', 'SubscribedContent-338387Enabled', 'SubscribedContent-338388Enabled',
               'SubscribedContent-338389Enabled', 'SubscribedContent-353694Enabled', 'SubscribedContent-353696Enabled',
               'SubscribedContent-310093Enabled', 'SubscribedContent-338393Enabled', 'FeatureManagementEnabled') {
    Set-Reg "$cu\ContentDeliveryManager" $n 0 -Area 'ads'
}
Set-Reg 'HKCU:\System\GameConfigStore' GameDVR_Enabled 0 -Area 'gaming'
Set-Reg 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled 0 -Area 'gaming'
Set-Reg 'HKCU:\Software\Microsoft\Siuf\Rules' NumberOfSIUFInPeriod 0 -Area 'privacy'
Set-Reg "$cu\Privacy" TailoredExperiencesWithDiagnosticDataEnabled 0 -Area 'privacy'
Set-Reg 'HKCU:\Software\Microsoft\InputPersonalization' RestrictImplicitInkCollection 1 -Area 'privacy'
Set-Reg 'HKCU:\Software\Microsoft\InputPersonalization' RestrictImplicitTextCollection 1 -Area 'privacy'

# File Explorer (still the file manager, Win+E): open on This PC, no recent-files tracking
Set-Reg "$cu\Explorer\Advanced" LaunchTo 1 -Area 'explorer'
Set-Reg "$cu\Explorer\Advanced" Start_TrackDocs 0 -Area 'explorer'
Set-Reg "$cu\Explorer\Advanced" Start_TrackProgs 0 -Area 'explorer'
Set-Reg "$cu\Explorer\Advanced" ListviewAlphaSelect 0 -Area 'explorer'
Set-Reg "$cu\Explorer\Advanced" ListviewShadow 0 -Area 'explorer'
Set-Reg "$cu\Explorer\Advanced" TaskbarAnimations 0 -Area 'explorer'
Set-Reg "$cu\Explorer\Serialize" StartupDelayInMSec 0 -Area 'explorer'

# Visual effects: "adjust for best performance", keeping font smoothing
Set-Reg "$cu\Explorer\VisualEffects" VisualFXSetting 2 -Area 'visuals'
Set-Reg 'HKCU:\Control Panel\Desktop' UserPreferencesMask ([byte[]](0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00)) 'Binary' -Area 'visuals'
Set-Reg 'HKCU:\Control Panel\Desktop' MenuShowDelay '0' 'String' -Area 'visuals'
Set-Reg 'HKCU:\Control Panel\Desktop' DragFullWindows '0' 'String' -Area 'visuals'
Set-Reg 'HKCU:\Control Panel\Desktop\WindowMetrics' MinAnimate '0' 'String' -Area 'visuals'
Set-Reg "$cu\Themes\Personalize" EnableTransparency 0 -Area 'visuals'
Set-Reg 'HKCU:\Software\Microsoft\Windows\DWM' EnableAeroPeek 0 -Area 'visuals'
