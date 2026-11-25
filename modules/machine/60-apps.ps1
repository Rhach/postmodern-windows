# Bundled apps and optional Windows features nobody on this desktop uses.
# Removed apps can't be put back by the uninstaller (reinstall them from the Microsoft Store if wanted);
# features are re-enabled by it.

# (Wallet, Advertising.Xaml and EdgeDevToolsClient were on this list too: Windows refuses to remove them.)
foreach ($a in 'Microsoft.OutlookForWindows', 'Microsoft.Windows.DevHome') { Remove-AppxEverywhere $a }

# PowerShell 2.0 is also a security win: it bypasses the logging and AMSI that newer versions have.
foreach ($f in 'MicrosoftWindowsPowerShellV2Root', 'MicrosoftWindowsPowerShellV2', 'WorkFolders-Client',
               'Printing-XPSServices-Features', 'MSRDC-Infrastructure', 'SmbDirect', 'WCF-TCP-PortSharing45',
               'Printing-Foundation-InternetPrinting-Client') { Set-FeatureDisabled $f }
