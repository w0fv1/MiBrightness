# Changelog

## 1.3.2 - 2026-09-23

- Restored the WPF-UI title bar with minimize and close buttons.
- Fixed standalone configuration mode overwriting shared bridge status with "Starting" after brightness tests.
- Restricted shared status persistence to the actual background bridge process.
- Verified brightness testing keeps the Bemfa bridge online and status cache intact.
## 1.3.1 - 2026-09-23

- Removed visual separators around the Windows autostart section.
- Removed the DPAPI explanatory footer text and save/reconnect footer hint.
- Rebalanced vertical spacing so the settings page keeps clear visual hierarchy without divider lines.- Unified field heading typography, including the autostart setting.`r`n`r`n## 1.3.0 - 2026-09-23

- Rebuilt the settings UI from WinForms to WPF.
- Added WPF-UI Fluent styling and native Windows 11 controls.
- Replaced fixed pixel positioning with responsive Grid/StackPanel layouts.
- Fixed high-DPI overlap issues at 150%/200% display scaling.
- Added a resizable, scrollable settings window.
- Preserved tray operation, Bemfa TCP bridge, WMI brightness control, DPAPI secret storage and autostart behavior.
## 1.2.0 - 2026-09-23

- Redesigned the configuration UI with Windows 11-inspired cards and spacing.
- Added a clear online/offline status badge and current brightness indicator.
- Added a brightness slider with one-click test application.
- Improved connection, autostart, credential and advanced settings layout.
- Improved high-DPI behavior for 200% Windows display scaling.
- Kept all existing Bemfa, DPAPI, reconnect and physical-adapter binding behavior.
## 1.1.0 - 2026-09-23

- Added Windows system tray UI.
- Added graphical settings window.
- Added `--config` standalone configuration mode.
- Added GUI brightness test.
- Added autostart toggle and reconnect action.
- Added Windows DPAPI `secret.dat` storage.
- Added one-time migration from the prototype `bemfa.uid.dpapi` format.
- Added state cache for the standalone configuration window.
- Added Start Menu shortcuts in the installer.
- Redacted Bemfa UID from runtime logs.
- Kept physical-interface binding to work around Clash/Mihomo TUN + Fake-IP TCP issues.
- Added release build automation and documentation.

## 1.0.0 - 2026-09-23

- Initial working bridge.
- Bemfa TCP subscription, heartbeat and reconnect.
- Windows WMI internal-display brightness control.
- User-level logon autostart.





