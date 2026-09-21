# DUPLO train controller — implementation plan

## Context

The goal is to drive a **2018 LEGO DUPLO train** (10874 Steam / 10875 Cargo, "Train
Base" hub) from an **Xbox controller** with a real analog throttle.

The original handoff assumed a Python daemon on the Raspberry Pi, bridged to Home
Assistant over MQTT, with a Matter remote as the user-facing control. Research
changed that decision twice:

1. **`sharpbrick/powered-up` (C#, MIT, 111★, June 2026) already implements the
   entire DUPLO train base** — a typed `DuploTrainBaseHub` exposing `Motor`
   (arbitrary power, not presets), `Speaker` (all five sounds), `Speedometer`,
   `ColorSensor`, `RgbLight` and `Voltage` as Rx observables. No maintained
   Python library covers the speaker or speedometer at all, so the Python route
   meant writing the protocol layer by hand.
2. **SharpBrick has no BlueZ backend** — only WinRT, a BlueGiga BLED112 dongle
   and Xamarin. That rules out the Pi's built-in radio, and makes Windows the
   natural host.

The Pi alternative (reusing `micschr0/duplo-train-10427-ble2mqtt` for a
gen-2 train, driven by a Thread remote) was rejected: its MQTT vocabulary is four
discrete states (`Forward/Boost/Backward/Stop`) with no speed setpoint, so an
analog trigger has nothing to talk to — and it would have required deploying
Mosquitto, a `python-matter-server` container and a Thread border router, none of
which is train work.

**Outcome:** a self-contained .NET console app on the Windows PC. No broker, no
containers, no Home Assistant, nothing to deploy. Trade-off accepted knowingly:
the train only responds while that PC is on, and there is no Matter remote. If an
always-on child-facing control is wanted later, that is the Pi option and it
targets the *other* train — the two stacks can coexist, since each drives its own
hub.

## Status

All milestones are complete. This document is kept for the reasoning behind the
decisions, not as a task list.

| Milestone | State |
|---|---|
| 0 — prove the PC can drive the train | **done** — all six probe steps passed on real hardware |
| 1 — core train wrapper, arbiter, config | **done** |
| 2 — Xbox controller | **done**, after two real-hardware fixes (below) |
| 3 — README and CI | **done** |

What real hardware changed, none of which was predictable from the desk:

- `Windows.Gaming.Input` enumerates pads in a console app and then returns
  **empty readings** — its input stack wants a window and message pump. Switched
  to XInput, kept the WinRT path behind config.
- XInput must be read from **all four user slots**, not slot 0. Windows assigns
  per device and a reconnected pad lands elsewhere.
- DUPLO motors have a **large deadband**: below ~30 they hum. Power is mapped
  into `MinPower..MaxSpeed` so the first notch and the bottom of the trigger
  travel both do something.
- A time-based **acceleration ramp was built and then removed**. It felt worse
  than direct control: with an analog trigger the finger is already the ramp, and
  a time term makes the throttle mushy. Power is now a pure function of trigger
  position, and the rate limit governs only how often a value is sent.
- The host stopped cleanly but the **process would not exit**, because the WinRT
  bluetooth stack leaves threads behind. The app now disposes the host to flush
  logs and then terminates.
- Config was read from the **working directory**, so `appsettings.json` beside
  the dll was ignored. Rooted at the binary now.

Deliberately not built: **mouse control** (needs a message-only window and a
`WM_INPUT` hook — disproportionate for a third input device) and anything
MQTT or Home Assistant related.

> The Windows PC and the machine hosting the Linux dev VM turned out to be the
> same box, so "copy to the target" is a transfer between a VM and its own host.
> Every step below still states where it runs.

## Development environment

Three machines, each with a distinct role:

| Machine | Role |
|---|---|
| `hapc-ubuntu-resolute` (Ubuntu 26.04, x86_64) | **the only dev env** — write, build, unit-test |
| The Windows PC | runs the app against real hardware; not a dev box |
| This Windows workstation | holds the repo; no toolchain assumed |

Ubuntu 26.04 ships only **.NET 10 SDK**, which targets `net8.0` happily, so that
is what we build with.

This works because of how SharpBrick is factored: `SharpBrick.PoweredUp` targets
`netstandard2.1;net8.0` and is fully portable — **only the WinRT bluetooth adapter
is Windows-only**. Better still, it ships `PoweredUpBluetoothAdapterMock`. So the
train wrapper, arbiter and mapping are all developed and tested on Linux against
the mock adapter, and the real WinRT adapter is swapped in at composition time.

That makes the split below load-bearing rather than tidiness: anything that
touches `net8.0-windows10.0.19041.0` cannot be *run* on the VM, only cross-built,
so all logic worth testing must live in portable projects.

### Cross-build — verified, not assumed

I proved this on the VM before planning around it. Installed .NET SDK **10.0.112**
from the Ubuntu feed, then built a console app targeting
`net8.0-windows10.0.19041.0` that references `SharpBrick.PoweredUp` 5.0.2,
`SharpBrick.PoweredUp.WinRT` 5.0.2 **and** `Windows.Gaming.Input.Gamepad`. Both
packages restored on Linux and the WinRT projection compiled. The publish produced:

```
spike.exe: PE32+ executable for MS Windows (console), x86-64
```

Two findings that go straight into the csproj:

- **`<EnableWindowsTargeting>true</EnableWindowsTargeting>` is mandatory.** Without
  it the build fails with `NETSDK1100: To build a project targeting Windows on
  this operating system, set the EnableWindowsTargeting property to true`.
- **Self-contained single-file comes out at 173 MB**, untrimmed. That is the price
  of needing no prerequisites on the Windows PC. If that is annoying to copy
  around, publish framework-dependent instead (a few MB) and install the .NET
  runtime on that PC once. I would not enable `PublishTrimmed`: SharpBrick leans
  on DI and reflection, and trimming is a plausible source of runtime-only
  failures that would be painful to debug on a machine we cannot iterate on.

So shipping is one command from the VM:

```bash
dotnet publish src/DuploTrain.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Copy the single file to the Windows PC, which then needs no SDK, no runtime and no
toolchain at all. WPF and WinForms would *not* cross-build this way — a console
app using WinRT projections does, which is why the app stays a console app.

## Milestone 0 — prove the PC can drive the train

Nothing else is worth building until this passes. **Gate: do not proceed to
milestone 1 until step 0.4 makes the train move.**

### 0.1 Confirm the Windows PC's prerequisites

The dev toolchain is already proven (see above). What is still unknown is the
target PC, and I cannot check it from here:

- **Windows build ≥ 10.0.19041** (Windows 10 2004). `SharpBrick.PoweredUp.WinRT`
  targets `net8.0-windows10.0.19041.0`; older builds cannot load it. `winver`.
- A **BLE-capable** radio — Bluetooth 4.0+. A 2.x/3.x radio enumerates happily and
  then never sees the train, which is a confusing failure worth ruling out first.

No SDK or runtime is needed there: we copy the self-contained exe.

### 0.2 Zero-code attempt — only if that PC happens to have a .NET SDK

`dotnet tool install` needs an SDK on the machine that runs it, so this is
opportunistic. **If the Windows PC has no SDK, skip straight to 0.4** rather than
installing one; the probe exe needs nothing.

```bash
dotnet tool install -g SharpBrick.PoweredUp.Cli
```

```bash
poweredup device list --EnableTrace true
```

**Caveat:** the CLI package is stale at **3.4.0** while the library is at
**5.0.2**, so a failure here is not conclusive — it may be the tool rather than the
PC. Success is strong evidence the BLE path works; failure means go to 0.4.

### 0.3 Wake the train

The hub sleeps on inactivity and on a button press. Press the button on the train
until its LED blinks before every attempt. This is the single most common cause of
"it doesn't work".

Gen-1 hubs need **no pairing or bonding**. Do *not* pair the train in Windows
Bluetooth settings — SharpBrick connects directly. If the train was previously
paired there, remove it; a stale Windows pairing is a plausible cause of
discovery failure, though unconfirmed.

### 0.4 The probe app — `src/DuploTrain.Probe`

A deliberately minimal console app, ~60 lines, `net8.0-windows10.0.19041.0`,
referencing `SharpBrick.PoweredUp` **5.0.2** and `SharpBrick.PoweredUp.WinRT`
**5.0.2**. **Built on the VM** with the recipe verified above, published
self-contained, and the single exe copied to the Windows PC — which needs nothing
installed. Bootstrap per the library's README:

```csharp
var serviceProvider = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug))
    .AddPoweredUp()
    .AddWinRTBluetooth()
    .BuildServiceProvider();

var host = serviceProvider.GetService<PoweredUpHost>();
var train = await host.DiscoverAsync<DuploTrainBaseHub>();
await train.ConnectAsync();
```

Then, in order, so a partial failure localises the problem:

1. Log hub properties and `Voltage` — proves notifications flow inbound.
2. `train.Speaker.PlaySoundAsync(DuploTrainBaseSound.Horn)` — audible proof a
   write reached the hub, with no moving parts.
3. `train.RgbLight.SetRgbColorNoAsync(PoweredUpColor.Red)` then `Green`.
4. `train.Motor.StartPowerAsync(30)`, wait 2s, `train.Motor.StopByFloatAsync()`.
5. Subscribe `train.Speedometer.SpeedObservable` and log during the run.

`examples/SharpBrick.PoweredUp.Examples/ExampleDuploTrainBase.cs` upstream does
all of this and is the reference to crib from.

**If this fails:** report which step, with the trace log. Fallbacks in order —
remove any Windows pairing; try a different BLE radio or a USB BLE dongle; try a
BLED112 with `--BluetoothAdapter BlueGigaBLE`; last resort, reconsider the Pi.

## Milestone 1 — core train wrapper and config

`src/DuploTrain.Core`

- `TrainController` — wraps `DuploTrainBaseHub` behind the operations we need:
  `SetSpeed(int)`, `Stop()`, `PlaySound(...)`, `SetColor(...)`, `NextColor()`,
  plus observables for measured speed, battery, colour under the train and
  connection state. Everything else stays SharpBrick's problem.
- **Command arbiter** — single owner of the requested speed. Applies, in order:
  deadzone, configurable **max speed cap** (it is a child's toy), and a
  rate limit of ~10 Hz that only writes on change. Input sources request; the
  arbiter decides and is the only thing that talks to `TrainController`.
- **Reconnect** — on disconnect, clear the requested speed so the train cannot
  lurch on reconnect, then rediscover with exponential backoff, logging clearly
  when the train needs waking by hand.
- **Config** via the standard .NET route: `appsettings.json` + `IOptions<T>`, no
  bespoke loader. Everything parameterised — no hardcoded speeds, ports or
  bindings: adapter choice, optional train name/address filter, max speed, step
  size, deadzone, rate limit, poll rate, and the full action→input binding table.

Driving from the keyboard lands here, which proves the whole chain before the
controller is introduced.

## Milestone 2 — Xbox controller

`src/DuploTrain.Input`

Use **`Windows.Gaming.Input`** (`Gamepad.Gamepads`), not a third-party wrapper:
the TFM already required by `SharpBrick.PoweredUp.WinRT` makes the WinRT
projection available at no cost, triggers arrive as `0.0..1.0` doubles, and
`GamepadAdded`/`GamepadRemoved` give **hotplug for free** — a brief requirement
that would otherwise need building. If it misbehaves in a console app, fall back
to `XInputGetState` P/Invoke (~30 lines, triggers as `0..255`).

Poll at ~50 Hz and feed the arbiter, which rate-limits to the BLE write budget.

Default mapping, all configurable:

| Action | Xbox | Keyboard |
|---|---|---|
| Throttle | RT forward, LT reverse (analog) | Up/Down step ±10 |
| Stop | B | Space |
| Horn | A | H |
| Cycle LED colour | X | L |
| Water refill / steam / departure | Y / LB / RB | W / S / D |

**Safety** — all of these, not just the easy ones:

- Stop the motor when the controller disconnects (`GamepadRemoved`).
- Watchdog: if polling stops succeeding while the train is moving, stop.
- Stop on Ctrl+C and on shutdown, via `IHostApplicationLifetime`.
- On BLE drop mid-run, clear the setpoint (milestone 1).

## Milestone 3 — finish and hand over

- `README.md` — setup, config reference, and troubleshooting that leads with
  *wake the train*, then Windows pairing, radio capability, `winver`.
- GitHub Actions workflow on an **`ubuntu-latest`** runner — the cross-build is
  proven, so no Windows runner is needed — running the tests and publishing the
  `win-x64` single-file artifact, with `EnableWindowsTargeting` set.
- Optional, only if wanted: run under `Microsoft.Extensions.Hosting.WindowsServices`
  via `UseWindowsService()` so it starts with the PC.

**Mouse control was dropped by decision.** On Linux `evdev` gives keyboard,
mouse and pad through one API; on Windows the mouse wheel needs a message-only
window and a `WM_INPUT` hook, which is disproportionate machinery for a tertiary
control. It was in the original brief and is explicitly out of scope now.

## Layout

The TFM column is the important one: it decides what can be *run* on the VM.

| Project | TFM | Runs on Linux? |
|---|---|---|
| `src/DuploTrain.Core` | `net8.0` | yes — TrainController, arbiter, config, mapping, reconnect |
| `test/DuploTrain.Tests` | `net8.0` | yes — unit tests + mock-adapter integration |
| `src/DuploTrain.Windows` | `net8.0-windows10.0.19041.0` | cross-build only — WinRT BLE + `Windows.Gaming.Input` |
| `src/DuploTrain.App` | `net8.0-windows10.0.19041.0` | cross-build only — the shipped exe |
| `src/DuploTrain.Probe` | `net8.0-windows10.0.19041.0` | cross-build only — milestone 0, kept as a diagnostic |

```
DuploTrain.sln
src/DuploTrain.Core/       portable: all logic worth testing
src/DuploTrain.Windows/    thin: WinRT adapter + gamepad, behind Core's interfaces
src/DuploTrain.App/        generic host wiring input -> arbiter -> train
src/DuploTrain.Probe/      milestone 0 spike
test/DuploTrain.Tests/     runs on the VM
appsettings.json
docs/PLAN.md
README.md
```

Rule of thumb for the split: `DuploTrain.Windows` should contain no decisions —
only adapters that turn WinRT and `Windows.Gaming.Input` into the interfaces Core
defines. Any logic that lands there becomes untestable on the only dev env we have.

A **Linux dev harness** falls out of this for free: run Core against SharpBrick's
`PoweredUpBluetoothAdapterMock` plus a scripted input source, and the whole
throttle/arbiter/safety path is exercisable on the VM with no train and no
controller. That is how milestones 1 and 2 get developed between trips to the
Windows PC.

Cleanup: delete the abandoned Python gen-1 work — `pyproject.toml` and
`duplo_train/lwp3/{const,messages}.py`. Its verified gen-1 constants are
superseded by SharpBrick, which implements all of them already.

## Verification

| What | How |
|---|---|
| BLE reaches the train | Milestone 0.4: horn sounds, LED changes, wheels turn |
| Protocol layer | Upstream's own `ExampleDuploTrainBase` behaviour, reproduced by our probe |
| Arbiter maths (deadzone, cap, rate limit, change detection) | Unit tests, no hardware — pure functions |
| Analog throttle | Squeeze RT progressively; the train should accelerate smoothly, not in steps |
| Max speed cap | Set cap to 30, hold RT fully, confirm it never exceeds it |
| Hotplug | Yank the controller's USB mid-run; **the train must stop**; replug and drive again |
| BLE drop | Press the train's button mid-run; confirm reconnect, and that it does **not** lurch |
| Shutdown | Ctrl+C while moving; the motor must stop |

Rows 3 and the mock-adapter paths run on the VM. Everything involving the train or
the controller runs on the Windows PC against a copied exe.

## Open risks

- **SharpBrick's WinRT backend on your specific Windows build** is the one real
  unknown, which is why milestone 0 exists and is gated. Note that the cross-build
  being proven does *not* prove the runtime behaviour — a self-contained exe that
  compiles on Linux can still fail at runtime on Windows, and BLE is exactly the
  kind of thing that would.
- **The dev loop has no hardware in it.** Everything up to milestone 2 is developed
  against a mock, so expect a batch of real-hardware surprises the first time the
  exe meets the train. Milestone 0 is deliberately placed before any of that work
  so the surprises arrive early and cheap.
- SharpBrick 5.0.2 is current (June 2026) but the CLI tool is stale at 3.4.0;
  only the CLI step depends on that, and it is optional.
- Everything here targets the **2018** hub. The 2025 trains (10427/10428) use a
  new controller that **requires BLE bonding** and different LED/horn
  subcommands; SharpBrick does not support them. Don't test with a 2025 train and
  conclude the code is broken.
