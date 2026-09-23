# Changelog

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
