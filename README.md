# duplo-train

Drive a LEGO DUPLO train from an Xbox controller on Windows, over Bluetooth LE.

Analog throttle on the triggers, buttons for the horn, sounds and LED, and a
speed cap so it stays a toy. No broker, no containers, no Home Assistant —
one console app.

## Which trains

Works with the **2018 hubs**: 10874 Steam Train and 10875 Cargo Train, both of
which advertise as "Train Base". They need no pairing or bonding.

**Not** the 2025 trains (10427 / 10428). Those use a new BLE controller that
requires bonding before it will accept anything, and different LED and horn
subcommands. They fail in a confusing way — the hub connects, writes succeed,
and every command is silently ignored. `SharpBrick.PoweredUp` does not support
them. If you need one of those, start from
[micschr0/duplo-train-10427-ble2mqtt](https://github.com/micschr0/duplo-train-10427-ble2mqtt)
instead.

## Requirements

| | |
|---|---|
| Windows | build **10.0.19041** or newer (`winver`) |
| Runtime | **.NET Desktop Runtime 8** — `winget install Microsoft.DotNet.DesktopRuntime.8` |
| Radio | a **Bluetooth 4.0+** adapter that Windows shows under Device Manager → Bluetooth |
| Controller | any XInput pad, wired or wireless |

The Desktop Runtime specifically, not the base runtime: the Windows target
framework puts both `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`
in the runtimeconfig. Check with `dotnet --list-runtimes`.

## Running

```powershell
dotnet path\to\duplo-train.dll
```

Press the button on the train until its light blinks, then start the app. It
scans for 30 seconds, and reconnects on its own with exponential backoff if the
link drops or the train falls asleep.

`appsettings.json` must sit next to the dll. Config is read relative to the
binary, not the working directory, so it does not matter where you run from.

## Controls

| Action | Xbox | Keyboard |
|---|---|---|
| Throttle | RT forward, LT reverse (analog) | Up / Down arrows, ±10 per press |
| Stop | B | Space |
| Horn | A | H |
| Cycle LED colour | X | L |
| Water refill | Y | W |
| Steam | LB | S |
| Station departure | RB | D |

Keyboard input needs the app window focused — deliberately, so that typing `h`
in another application does not honk the train. Closing the window stops the
motor and exits.

Mouse control is not implemented. It would now be possible via `WM_INPUT` on the
app window, but it remains out of scope by decision.

### Why there is a window

The window is not decoration. Windows routes gamepad navigation at the focused
window, and when this was a console app a nudge of the stick would walk the
focus onto the console's own close button, where a button press shut the app
down mid-run. A real focusable window gives that navigation somewhere harmless
to land.

Nothing inside the window is keyboard-focusable — the STOP button and the log
both set `TabStop = false` — because a focusable STOP would simply have made
stray presses land on the one control that matters most.

This does **not** help if something else is grabbing the controller globally.
**Steam Input** is the usual culprit: with Steam running, its desktop
configuration maps the stick to mouse and window switching regardless of which
window has focus. Xbox Game Bar can do the same. If the pointer still jumps
around, close Steam before suspecting the app.

## Configuration

All of `appsettings.json`, under `DuploTrain`.

### Motion

| Setting | Default | What it does |
|---|---|---|
| `MaxSpeed` | 60 | Speed cap, 1–100. The throttle is *scaled* into this range, not clipped, so full trigger always means "as fast as allowed". |
| `MinPower` | 30 | Lowest power that actually moves the train. Below roughly 30 a DUPLO motor hums and crawls, so any non-zero request maps into `MinPower..MaxSpeed`. Raise it for heavier loads or tired batteries; 0 disables the mapping. |
| `Step` | 10 | Power per arrow-key press. |
| `Deadzone` | 0.08 | Trigger travel ignored near rest, 0–1. |
| `RateLimitHz` | 10 | Cap on motor writes per second. The BLE link is the scarce resource. |
| `PollHz` | 50 | Input sampling rate. |
| `HeartbeatMs` | 2000 | How often to poke the hub, so the watchdog has traffic to watch while standing still. |
| `LinkTimeoutMs` | 6000 | Declare the link dead after this much silence. Must exceed `HeartbeatMs`. |

### Train

| Setting | Default | What it does |
|---|---|---|
| `DiscoverTimeoutSeconds` | 30 | Scan timeout per connection attempt. |
| `ReconnectInitialMs` | 1000 | First retry delay. |
| `ReconnectMaxMs` | 30000 | Backoff ceiling. |
| `ReconnectFactor` | 2.0 | Backoff multiplier. |
| `ColorCycle` | rainbow, ending `Black` | Colours the cycle button walks. `Black` is the LED's off state, so the same button reaches "off". |

### Input

`Input.Gamepad.Buttons` and `Input.Keyboard.Keys` map names to actions. Actions
are `Stop`, `Horn`, `Brake`, `StationDeparture`, `WaterRefill`, `Steam`,
`CycleColor`, `ThrottleUp`, `ThrottleDown`.

Button names use the `Windows.Gaming.Input` spelling — `A`, `B`, `X`, `Y`,
`LeftShoulder`, `RightShoulder`, `DPadUp`, `Menu`, `View` and so on. Keys are
WinForms `Keys` names — `H`, `L`, `Space`, `W` — and `Up` and `Down` are
reserved for the throttle.

### Session

| Setting | Default | What it does |
|---|---|---|
| `KeepDisplayAwake` | `true` | Stops Windows blanking the display or idle-locking while the app runs. Once locked, the lock screen also receives the gamepad and drives its on-screen keyboard with it, and no user-session process can take that away. Does not prevent an explicit Win+L. |

`Input.Gamepad.Backend` selects `XInput` (default) or `WindowsGamingInput`. Use
XInput: `Windows.Gaming.Input` enumerates pads in a console app but returns
empty readings, because its input stack wants a window and message pump. The
WinRT path is kept for a future windowed host.

Either source can be switched off with `Enabled: false`, but not both.

## Building

The Windows targets **cross-build from Linux**, which is how this is developed:

```bash
dotnet publish src/DuploTrain.App -c Release -o out
```

`EnableWindowsTargeting` is set in `Directory.Build.props`; without it, restore
fails with `NETSDK1100`. WPF and WinForms would *not* cross-build this way,
which is one reason the app stays a console app.

Tests run anywhere, because everything worth testing lives in the portable
`DuploTrain.Core`:

```bash
dotnet test test/DuploTrain.Tests
```

## Troubleshooting

**The train is not found.** Press its button until the light blinks — hubs sleep
on inactivity, and this is by far the most common cause. Then check Windows
actually has a working BLE radio.

**No Bluetooth at all in Device Manager.** On combo Wi-Fi/Bluetooth cards the
Bluetooth half is a *USB* device: the card's USB cable must be connected to a
`JUSB` / `F_USB` 2.0 header on the motherboard. Without it, Wi-Fi works
perfectly and Bluetooth never appears. Wi-Fi and Bluetooth drivers are usually
separate installers, too.

**The gamepad does nothing.** Check the startup line says
`starting input source 'gamepad (xinput)'`. If it says plain `'gamepad'` you are
running an old build. If no controller is found you get a warning every ten
seconds naming the four XInput slots that were tried — all four are scanned,
because Windows does not always use slot 0. If Windows sees the pad but XInput
does not, it is being exposed as a generic HID device rather than an XInput one.

**It drives very slowly.** Raise `MinPower`. DUPLO motors have a large deadband
and a tired battery widens it.

**The controller moves the mouse or switches windows.** Something else is
grabbing it globally — check for Steam Input first, then Xbox Game Bar. A
focused window contains gamepad *navigation*, but nothing in user mode can take
a controller away from another application.

**The app will not close.** Fixed, but worth knowing why: SharpBrick's discovery
accepts a cancellation token and does not reliably honour it, so stopping while
scanning used to wait out the host's shutdown timeout. Discovery is now raced
against the token, the timeout is 5s, and shutdown is bounded.

**The link keeps dropping.** `SharpBrick` raises no event on BLE disconnect, so
this app detects it with an RSSI heartbeat plus an inbound-traffic watchdog. If
it fires spuriously, raise `LinkTimeoutMs`. Note that a hub keeps executing its
last motor command when BLE goes away, so a train that was moving is still
moving — the first write after reconnecting is always an explicit zero.

**Run with debug logging** to see raw trigger and button values:

```powershell
$env:Logging__LogLevel__Default="Debug"; dotnet duplo-train.dll
```

## Layout

| Project | Target | Notes |
|---|---|---|
| `src/DuploTrain.Core` | `net8.0` | Arbiter, config, reconnect, action dispatch. Portable, so it builds and tests off Windows. |
| `src/DuploTrain.Windows` | `net8.0-windows10.0.19041.0` | WinRT bluetooth adapter and input devices. Adapters only, no decisions. |
| `src/DuploTrain.App` | `net8.0-windows10.0.19041.0` | WinForms window, host wiring and `appsettings.json`. The window is the keyboard source and the status display. |
| `src/DuploTrain.Probe` | `net8.0-windows10.0.19041.0` | Standalone BLE diagnostic; self-contained, needs no runtime installed. |
| `test/DuploTrain.Tests` | `net8.0` | Runs anywhere. |

The split is load-bearing rather than tidiness: anything in a Windows-targeted
project can be cross-built but not *run* on the Linux dev box, so all logic
worth testing has to stay in `Core`.

## Design notes

**Why SharpBrick.** `SharpBrick.PoweredUp` already implements the entire DUPLO
train base — motor, speaker, speedometer, colour sensor, RGB light and voltage,
as typed Rx observables. No maintained Python library covers the speaker or
speedometer at all. The cost is that its only BLE backends are WinRT, a BlueGiga
BLED112 dongle and Xamarin, which is why this runs on Windows rather than on a Pi.

**One arbiter.** Input sources only ever *request*; a single `SpeedArbiter` owns
the setpoint and applies deadzone, cap, deadband mapping, rate limit and change
detection. It is pure — no BLE, no timers, no threads — so all of those rules
are tested without a train.

**Power is a pure function of trigger position.** There is no acceleration ramp.
One was built and removed: with an analog trigger your finger already is the
ramp, and a time term only makes the throttle feel mushy and unpredictable. The
rate limit decides how *often* a value is sent, never what it is.

**Safety.** The motor stops when the controller disappears, when the BLE link
goes quiet, and on shutdown. Losing an input device is treated as a stop
condition because an unplugged controller cannot be used to stop the train.

## Probe

`src/DuploTrain.Probe` is a standalone diagnostic that discovers, connects,
reads the battery, plays the horn, sets the LED and runs the motor for two
seconds. Publish it self-contained and it needs nothing installed on the target:

```bash
dotnet publish src/DuploTrain.Probe -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Use it to answer "is this a Bluetooth problem or an app problem" without
involving config, controllers or the arbiter.
