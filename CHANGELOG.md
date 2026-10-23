# Changelog

## 0.1.0

- Passive HID listener on the dock control interface (no polling)
- Switch playback device by hiding/showing the headset audio endpoint
- Auto-discovery of the HID interface path and the audio endpoint (nothing machine specific is hardcoded)
- Per-second enforcement so Windows cannot silently move the default device away
- Manual override: `G5DockSwitch.exe headset|external`
- Diagnostics: `--probe` and `--capture`
- Headless WinExe build (no console window at logon)

### Compliance

- No vendor binaries, no decompiled code, no firmware included
- Uses only public Windows APIs (`IPolicyConfig`, `IMMDeviceEnumerator`)
- Full rollback via `uninstall.ps1`; no PnP or driver state is modified
- Take-down friendly: the project will adapt or remove itself if the vendor objects
