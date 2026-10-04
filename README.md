# Racing Helper — personal race engineer for iRacing

A private, local-only take on the GO Fast feature set. It runs on your PC and starts working automatically when you get in the car in iRacing:

- records every lap
- talks to you like an engineer
- draws overlays on top of the sim
- debriefs every session in a dashboard

No accounts, no cloud: everything lives in `Documents\RacingHelper`.

## Start it

```powershell
.\build.ps1
```

That builds `dist\RacingHelper.exe` and puts a **Racing Helper** shortcut on your desktop. It needs the .NET 9 Desktop Runtime and the WebView2 runtime, both already on this PC.

During development you can also run it straight from source:

```powershell
dotnet run --project src\RacingHelper
```

In iRacing, set **Options → Graphics → Window mode = Borderless window**, otherwise the overlays can't appear on top.

Closing the dashboard window keeps the app running in the tray, so recording, overlays and voice carry on. Use the tray icon to reopen it or exit.

| Hotkey | |
|---|---|
| `Ctrl+Shift+F9` | Move / resize overlays (drag, mouse wheel), press again to lock |
| `Ctrl+Shift+F10` | Hide / show all overlays |
| `Ctrl+Shift+F11` | Switch delta reference: PB → session best → last lap |
| `Ctrl+Shift+F12` | Open the dashboard |

The dashboard also runs at http://127.0.0.1:5199. Enable LAN access in Settings to open it on a tablet next to the rig.

## What it does (GO Fast feature → here)

| GO Fast | Racing Helper |
|---|---|
| **Telemetry data**: segment analysis, track indicators, compare inputs, multi-session compare, line analysis | **Telemetry** page:<br>• up to 6 laps from any sessions on synced lanes (speed, delta, throttle, brake, steering, gear, RPM, lateral g)<br>• drag / wheel zoom, corner chips to jump to a segment<br>• track map with racing lines coloured by lap, speed, inputs or gain/loss<br>• markers for braking point ■, apex ●, throttle pickup ▲<br>• corner-by-corner table with "what to do" advice |
| **Line comparison** | GPS racing lines from your .ibt files on the map, plus the live *Line Comparison* overlay (your line vs the reference through the current corner) |
| **Leaderboards**: personal bests, compare with rivals | **Leaderboards**:<br>• PBs for every car/track<br>• your fastest laps and theoretical best<br>• "rivals" = laps from any other driver's .ibt you import (friend, coach, pro)<br>• any lap can become your live delta reference |
| **Setup optimiser** (descriptive questions) | **Setup → Optimiser**: answer what / where / which corners / how bad / preference and get prioritised changes. Each change shows the current value from your actual setup (e.g. "Rear ARB = Soft"), with driving-technique tips |
| — | **Setup → Auto-detect**: understeer / oversteer by corner phase and speed, measured from steering vs yaw response at the limit, plus wheelspin, lock-ups and tyre temps → suggested changes |
| — | **Setup → Journal**: every setup you drive is fingerprinted automatically; see the fastest one and exactly what changed between them |
| **Setup auto installer** (type / version / car filters) | **Setup → Installer**: keep setups in `Documents\RacingHelper\SetupLibrary\<car folder>\…`. When you join a session they're copied into `iRacing\setups\<car>\RacingHelper`, filtered by race / quali / wet, latest version only, and optionally matching the track |
| **Tyre tool**: pressure & temperature analysis, smart feedback, one-click adjustments, stint/lap, avg/max | **Tyres**: hot pressures and inner/middle/outer temps per lap or stint (average or peak), against a per-car target window. Gives camber and pressure advice, suggested cold pressures, and **"Send to pit"** sets them in iRacing's pit service |
| **Fuel calculator**: auto and manual | Live fuel overlay and page (per-lap usage, laps in tank, fuel to finish / to add, one-click pit fuel), plus a race planner that fills itself in from your history |
| **Stint tracking** | Session page: stints table (best, average, consistency, pace trend in s/lap, fuel per lap) and a pace chart through the session |
| **Overlays** (all 20) | Essential Inputs, Input Telemetry, Input Comparison, Speed Comparison, Brake Indicator, Delta Sectors, Delta Bar, Standings (with projected iRating change), Relatives, Comparison Target, Corner Analysis, Track Map, Mini Map, Fuel, Tyres, Weather, Radar, Damage, Line Comparison, Weather Forecast, **plus** a Race Engineer feed |

### The engineer (feedback while you drive)

The engineer speaks through Windows voices and also writes everything to the feed overlay and dashboard:

- **Session start:** greets you with the car, track and your PB here; announces setup changes since your last run and the track conditions.
- **Every lap:** lap time and delta to the reference, plus the corner where you lost the most and why ("Most time lost at turn 5: 0.38, apex −8 km/h").
- **Personal and session bests.**
- **Fuel:** warnings, laps left, and how much to add.
- **Race control:** flags (blue, yellow, white, checkered, black, meatball) and incidents against the limit.
- **Car:** engine warnings, damage / repair times, and pace loss after contact.
- **Weather:** track temperature changes and rain.
- **Optional corner callouts** after every corner where you lost time.
- **Session end:** a debrief summary with what to focus on next time.

Verbosity is set in Settings.

### After a session

iRacing's .ibt file is imported automatically, which adds GPS lines, tyre data and wheel speeds. The **Sessions** page then gives:

- best, theoretical best, consistency and stints
- a prioritised debrief:
  - corners losing the most time vs your PB in the same conditions
  - braking-point consistency
  - coasting and trail braking
  - shift points and lock-ups
  - tyre pressures and camber
  - handling balance
  - fuel use
- a corner table and per-lap sector table

## Limits

- **iRacing only.** The sim layer is behind an `ITelemetrySource` interface, so ACC / LMU could be added later.
- **Damage:** iRacing doesn't expose per-part damage, so the Damage overlay uses what it does expose: mandatory/optional repair time, the meatball flag, engine warnings, and pace loss after contact.
- **Weather forecast:** iRacing provides no forecast, so the forecast overlay projects the last ~20 minutes' trend and is labelled as a projection.
- **Live tyre data:** during a live session iRacing only gives pit-measured carcass temps and cold pressures. Full surface temps and hot pressures come from the .ibt file after the session; keep iRacing telemetry logging on (the app can switch it on for you).
- **Setup pressures:** `.sto` files are binary, so the installer copies setups but can't rewrite pressures. Use the Tyre tool's "send to pit" instead.
- **Tyre target windows:** these are generic starting points per car type. Tune them per car on the Tyres page.

## Project layout

```
src/RacingHelper.Core   telemetry (iRacing shared memory + .ibt), lap recording, analysis, storage (SQLite),
                        live engine + race engineer, web API, dashboard (wwwroot)
src/RacingHelper        Windows app: dashboard window (WebView2), overlays (WPF), voice, tray, hotkeys
tools/RacingHelper.Cli  developer tool: inspect / import .ibt, session reports, replay through the live engine, headless dashboard
```

Useful developer commands:

```powershell
dotnet run --project tools\RacingHelper.Cli -- import <db> "$env:USERPROFILE\Documents\iRacing\telemetry" 10
dotnet run --project tools\RacingHelper.Cli -- report <db> <sessionId>
dotnet run --project tools\RacingHelper.Cli -- serve <db> 5299
```
