# Auto-switch the playback device for dock-charging headsets (HECATE G5 MAX dock version)

English | [简体中文](README.md)

My headset comes with a charging base (HECATE G5 MAX dock version). After I put it back on the base and it
powers itself off, **the headset is still listed** in Windows' playback devices and is still the default device —
so the whole system goes silent, and I have to open Sound settings and switch back every single time.
So I built a small background tool that does it for me. Once installed:

| Action | Headset | What the tool does |
|---|---|---|
| Lift the headset off the base | Voice prompt "power on, 2.4G connected" | Audio switches to the headset |
| Put it back on the base | Voice prompt "power off" | Audio switches back to the previous speakers |

No driver to install, no window, starts automatically at logon.

## Quick start

### Install

**Step 1 — extract to a fixed folder**

Download the zip from Releases and extract it to a folder that will not move, e.g. `C:\Tools\HECATE-DockAudioSwitch`.
Do not keep it in Downloads or on the Desktop: the scheduled task starts the exe by absolute path, so moving the folder means reinstalling.

**Step 2 — run the installer once**

Right-click `install.ps1` → **Run as administrator** and accept the UAC prompt.

Or open PowerShell in the extracted folder:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy','Bypass','-File','install.ps1'
```

> Administrator is only needed to register a "run at logon" scheduled task. The tool itself runs with normal user rights afterwards.

**Step 3 — verify**

Lift the headset and check that the playback device switches to it; put it back and check that it switches back.
`g5-switch.log` in the same folder should show:

```
[00:29:14.919] frame: cmd=0x0A [...] -> inuse
[00:29:15.258] audio: HEADSET (Show hr=0)
```

### Uninstall

Right-click `uninstall.ps1` → Run as administrator, then delete the folder.

It removes the scheduled task, stops the process and makes the headset audio endpoint visible again —
**the device is back to its pre-install state, no reboot required.**

## Advanced

### Problem analysis

**Symptom**: once the dock's USB cable is plugged in, a playback device named "HECATE ..." appears.
If you make it the default, audio does not switch away after you put the headset back on the base — it keeps
playing into the headset and the system ends up silent. If you do not make it the default, nothing changes at
all whether you pick the headset up or put it down.

**Root cause**:

1. The base is both the 2.4G receiver and the charger. It connects over a single USB cable and enumerates a USB audio device (`MI_00`)
2. Powering the headset on or off only affects the 2.4G radio link; it **does not affect USB enumeration** — the base neither knows nor tells Windows that the headset has been switched off
3. Windows only switches the playback device when an endpoint appears or disappears. Neither happens here, so it never switches

Any approach that waits for the device to disconnect is therefore doomed; the endpoint has to be removed **actively**.

**Rejected alternatives**:

| Approach | Why not |
|---|---|
| Disable the audio interface with `pnputil` | needs administrator; repeated disable/enable pushes the device into a "pending reboot" state; tearing down the USB audio interface also changes the base's own reported state, causing misdetection and oscillation |
| Only switch the default playback device | the endpoint is still there, so some apps keep sending audio into a device nobody is listening to |
| Wait for Windows to switch by itself | the endpoint never disappears, so the event never arrives |
| Disable the headset audio device outright | needs administrator, and you have to re-enable it to use the headset |

**When this does not apply**:

- The headset **stays powered on** when put back on the base (no "power off" prompt) → the base never reports a state change
- The base only charges and audio goes through a **separate 2.4G dongle** → simply plugging that dongle into a switchable USB port is simpler
- The wired variant (`Pid_A229`) is likely compatible but untested

### How it works

In one sentence: **listen to the status frames the base pushes, and hide/show the headset audio endpoint accordingly.**

#### 1. Data source: frames pushed by the base

The base **pushes** 64-byte status frames on the HID control interface `MI_04 & Col03`; no host polling is needed:

```
D0 35 BB <cmd> <sub> <payload ...> ... 04
│  └──┴── VID = 0x35BB (little endian)
└─ fixed header                          last byte is always 0x04
```

Only bytes 5..10 of the `0x0A` frame are used for the decision:

| State | `0x0A` payload (bytes 5..10) | `0x03` byte 5 | Meaning |
|---|---|---|---|
| **In use** | `01 64 00 01 01 09` | `EF` | headset powered on, full telemetry (`0x64` = 100 % battery) |
| **On the base** | `00 00 00 00 00 00` | `01` | headset powered off, no telemetry at all |
| Ignored | `00 64 01 00 00 00` | — | **not a pushed value**, see pitfall 1 below |

Full frame examples:

```
in use      D0 35 BB 0A 00 01 64 00 01 01 09 01 01 03 ... 04
on the base D0 35 BB 0A 00 00 00 00 00 00 00 01 01 03 ... 04
```

#### 2. Four pitfalls

1. **Do not poll.** If you write a `0x0A` query yourself, the base answers with a **stale record unrelated to the headset** (always `00 64 01 00 00 00`). The first version of this tool polled, and was wrong for a long time because of it.
2. **All zeros = on the base**, not a data error. It means the headset is powered off and reports nothing.
3. **`01 64 00 ...` = in use**, not "charging". I once treated byte 5 as a charging flag; when the battery filled up the flag disappeared, the state was misread as "left the base", and audio went to a headset that was switched off.
4. **Pushes are event driven**, there is no periodic heartbeat. That is why the last known state from `last-state.txt` is applied once at startup.

#### 3. The switch itself

Hide/show the endpoint with `IPolicyConfig::SetEndpointVisibility` (the API behind the "Disable device" button in
Sound settings) and set the default device with `SetDefaultEndpoint`. **No PnP device state is touched, no
administrator rights are needed, charging is unaffected.**

```
pick up  → pushes inuse  → show headset endpoint + make it the default device
put back → pushes docked → hide headset endpoint + restore the previous default device
```

#### 4. Safety nets

- **Re-assert every second**: idempotently push endpoint visibility and the default device back to the target state, so Windows cannot quietly move the default device when the headset powers on or off
- **Remember the previous device**: the default device in effect before switching to the headset is stored in `last-default.txt` and restored exactly
- **Manual override wins**: running `G5DockSwitch.exe headset|external` writes `override.txt`; the daemon keeps your choice until the next headset state change

### Build & extend

No .NET SDK or Visual Studio needed — the C# compiler shipped with Windows is enough:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# output: dist\G5DockSwitch.exe (~18 KB)
```

The whole source is a single file: [src/G5DockSwitch.cs](src/G5DockSwitch.cs), about 640 lines, no third-party dependencies.

**Adapting another model**: copy `config.ini.example` to `config.ini` (next to the exe) and edit:

```ini
vid=35BB
pid=A228
desc=HECATE
iface=MI_04&Col03
```

`desc` is the **prefix** of the audio device description, `iface` is the HID collection carrying the status frames.

**Diagnostics**:

```powershell
G5DockSwitch.exe --probe          # print auto-detected paths/endpoint/state, then listen for 3 seconds
G5DockSwitch.exe --capture 60     # record 60 seconds of raw frames into capture.log
```

The full reverse-engineering notes (frame layout, field guesses, pitfalls) are in [docs/protocol.md](docs/protocol.md).

### Verified environment

| Item | Value |
|---|---|
| Headset | Edifier HECATE **G5 MAX dock version** |
| Dock USB ID | `VID_35BB&PID_A228` (A228 = wireless dock version) |
| Audio interface | `MI_00`, device description `HECATE G5 MAX底座版` |
| HID control interface | `MI_04 & Col03` |
| OS | **Windows 11 Pro for Workstations 25H2, Build 26200.9711** |
| Resource usage | ~12–18 MB RAM, idle CPU close to zero |
| Window | none (native WinExe, no console flash at logon) |

## FAQ

**Q1: Installed it, but nothing happens when I pick the headset up or put it down.**

A: Check in this order:

1. Does the headset **announce "power off"** when placed on the base? If not, it stays powered on and the base state never changes — this approach does not apply. Use a hotkey tool such as [SoundSwitch](https://github.com/Belphemir/SoundSwitch) instead.
2. Run `G5DockSwitch.exe --probe` and check that `hid path` and `endpoint id` are real paths. `(not found)` means the model is not auto-detected and `config.ini` needs to be adjusted.
3. Run `G5DockSwitch.exe --capture 60`, pick the headset up and put it back two or three times, then attach `capture.log` and `probe.txt` to an issue.

**Q2: The logic is inverted (picking up switches to the speakers, putting down switches to the headset).**

A: Different firmware batches may use the opposite encoding. Record with `--capture` and post the raw frames from both states; adapting is straightforward.

**Q3: Does it modify my driver or firmware?**

A: No. It only uses Windows' built-in audio APIs to do two things: hide/show the headset audio device and switch the default playback device — the same as clicking disable/enable in Sound settings. Uninstalling restores everything.

**Q4: Does it affect charging or the warranty?**

A: No. Charging is handled by hardware the tool cannot reach, and no device state or driver is modified.

**Q5: Do I have to keep the Edifier driver running?**

A: No. Tested with the HECATE driver fully closed; the tool keeps working.

**Q6: Does it survive a reboot?**

A: Yes, it starts automatically at logon.

**Q7: Why can't it live in Downloads or on the Desktop?**

A: The scheduled task starts the exe by absolute path, so moving or renaming the folder breaks it. If you move it, run `install.ps1` again.

**Q8: Will a Windows feature update or a headset firmware update break it?**

A: Feature updates generally do not. A firmware update may change the frame layout; record with `--capture` and it can be adapted.

**Q9: Does it work with other models?**

A: Only verified on the HECATE G5 MAX dock version. Other dock models from the same vendor very likely share the protocol — try `--probe`. If not, post `capture.log`.

**Q10: Is it safe?**

A: Open source, single file, no third-party dependencies, buildable yourself with `build.ps1`, no network access, no data collection.

**Q11: I want to force a switch manually.**

A: Run in the extracted folder:

```powershell
G5DockSwitch.exe headset     # switch to the headset
G5DockSwitch.exe external    # switch to the speakers
```

A manual switch stays in effect until the next time you pick the headset up or put it down.

## Author

Designed, developed and maintained by **AlsoCode**.

| Item | Info |
|---|---|
| Author | AlsoCode |
| Repository | https://github.com/YukinaMano/hecate-dock-audio-switch |
| First release | October 2026 |
| Feedback | GitHub Issues (preferred) |

**Notes on authorship**

- Every conclusion in this document (problem analysis, protocol inference, state byte mapping) and all of the code were derived by the author from packet captures, trial and error, and verification on real hardware; no existing implementation was consulted
- The Git history and GitHub timestamps of this repository serve as proof of authorship and first publication
- The device identifiers quoted here (`VID_35BB&PID_A228` etc.) are facts reported by the device itself and are included to document compatibility
- **Not affiliated with Edifier / HECATE.** Unofficial third-party tool, built for personal interoperability needs, not representing the vendor's position
- Contains and calls no vendor binaries, firmware or drivers; circumvents no encryption or technical protection measures
- Only changes **audio endpoint visibility and the default device** through public Windows APIs; no driver, firmware or PnP device state is modified, and uninstalling rolls everything back
- Collects and uploads no data
- If the vendor considers anything here inappropriate (naming, wording, scope), please open an issue — the author will adjust or take it down
- Provided "as is" under the MIT license, without warranty of any kind; use at your own risk

**Attribution and redistribution**

- Code: [MIT](LICENSE) — free to use, keep the copyright notice
- Documentation (README and everything under `docs/`): [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), **please credit the author and link back to this repository**
- If you integrate it into another project or tool, please state the source
- Please do not remove the author information and republish, or rewrite this document and present it as original work

> The repository URL in this document is its provenance mark. If you find material elsewhere with the same content but without this information, it is most likely an unattributed copy.
