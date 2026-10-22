# Potato PitGlow

Free, open-source sim-racing lights for the RGB you already own. Your fans become an RPM gauge, and your pump, GPU, RAM and mouse show the race flags.

- **Fans:** each fan ring is an RPM gauge. It fills green → yellow → red, goes solid red at the shift point and flashes at the limiter. With the pit limiter on, the ring halves alternate blue.
- **Pump, GPU, RAM, mouse:** show the current flag (flashing green, flashing yellow, pulsing blue and so on), then go back to your normal colors.
- **Leaving the car:** in menus, the garage, replays, or when the game closes, everything returns to the lighting you had before.

| Game | RPM gauge | Flags |
|---|---|---|
| iRacing | ✅ each car's real shift-light points | ✅ including local vs full-course yellow, blue, black, meatball |
| Assetto Corsa EVO | ✅ | ✅ |
| Assetto Corsa Rally (also ACC / AC1 RPM) | ✅ | – |
| Wreckfest 2 | ✅ | ✅ (enable telemetry below) |
| BeamNG.drive | ✅ | – (enable OutGauge below) |

In games without flags (–), the flag devices glow with the rev color instead: green at idle, through yellow and orange, red at the shift point. Set `when_no_flags = "clear"` under `[flags]` to turn that off.

PitGlow watches for all of them and follows whichever one you're driving.

## Setup

1. Install [OpenRGB](https://openrgb.org) and make sure it shows your devices.
   - Run it **as administrator** if you want RAM (and some GPUs) to show up.
   - Fully quit iCUE, Armoury Crate, Mystic Light, SignalRGB and similar apps.
   - On the **SDK Server** tab, click **Start Server** (port 6742).
2. Unzip PitGlow anywhere and double-click **Setup.bat**. It writes `pitglow.toml` from your devices.
3. Double-click **Calibrate.bat** to set where each fan's gauge starts and which way it fills.
4. Double-click **Demo.bat** to check everything: a fake rev sweep on the fans and every flag in turn.
5. To race, double-click **PitGlow.bat** and leave it running. Press Ctrl+C or close the window to stop; your lighting is restored either way.

**Devices.bat** lists everything OpenRGB reports, with each zone's LED count.

### Wreckfest 2
In `Documents\My Games\Wreckfest 2\<your id>\savegame\telemetry\config.json`, set the `udp` entry to `"enabled": 1`, IP `127.0.0.1`, port `23123`.

### BeamNG.drive
Options → Other → Protocols → turn on **OutGauge UDP**, IP `127.0.0.1`, port `4444`.

## What's different from the Python version

- **No Python, no setup scripts.** One `PitGlow.exe`.
- **Survives OpenRGB updates.** Devices and zones are remembered by hardware location, so a rename (like OpenRGB 1.x calling fans "Corsair Fan Header" instead of "RGB Port") is picked up automatically and your calibration is kept.
- **One stuck device can't take everything down.** Every OpenRGB call has a time limit, and each device runs on its own.
- **Never rescans OpenRGB.** Rescans can drop USB devices until a power cycle.
- **Backs off** if another app takes over a device mid-session, instead of fighting it.
- **Crash-safe.** If PitGlow is killed, the next start puts your original lighting back.

## If something's wrong

- *"Can't reach OpenRGB"*: OpenRGB isn't running, or its SDK server isn't started.
- *"OpenRGB is not responding"*: it's still detecting devices, or a device driver has hung. Wait for detection to finish; if it never does, shut down, switch the power supply off for 30 seconds, and start again.
- *A fan stays dark:* run Devices.bat. If its zone shows 0 LEDs, set the size in OpenRGB (8 for most fans).
- *RAM missing:* run OpenRGB as administrator.
- *Lights don't react in a game:* start the game, get in the car, run **Telemetry.bat** and drive for a minute. It shows what PitGlow reads from the game and saves `telemetry-check.txt`.
- `pitglow.log` next to PitGlow.exe records problems.

## Building

```
dotnet test tests/PitGlow.Tests
dotnet publish src/PitGlow.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Every push to `main` is built and tested on Windows by GitHub Actions; the ready-to-run zip is attached to each run as **PitGlow-win-x64**.

- `src/PitGlow.Core`: game readers, lighting effects, settings, the device model and the backend contract.
- `src/PitGlow.OpenRgb`: the OpenRGB backend (its own SDK client, protocol v3).
- `src/PitGlow.Cli`: `PitGlow.exe`.
- `tests/`: runs everything against a fake OpenRGB server shaped like a real rig.

## License

GPL-3.0-or-later. Copyright (C) 2026 Dirtiest Potato Entertainment.
