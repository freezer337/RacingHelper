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
| `Ctrl+Shift+F5` / `F6` / `F7` / `F8` | Ask: tyres / fuel / gaps / repeat the last message |

These are the defaults: change any of them, or add your own, in Settings → Buttons & keys. The version number is at the bottom of the dashboard's sidebar.

The dashboard also runs at http://127.0.0.1:5199. Enable LAN access in Settings to open it on a tablet next to the rig.

## What it does (GO Fast feature → here)

| GO Fast | Racing Helper |
|---|---|
| **Telemetry data**: segment analysis, track indicators, compare inputs, multi-session compare, line analysis | **Telemetry** page:<br>• up to 6 laps from any sessions on synced lanes (speed, delta, throttle, brake, steering, gear, RPM, lateral g)<br>• drag / wheel zoom, corner chips to jump to a segment<br>• track map with racing lines coloured by lap, speed, inputs or gain/loss<br>• markers for braking point ■, apex ●, throttle pickup ▲<br>• corner-by-corner table with "what to do" advice |
| **Line comparison** | GPS racing lines from your .ibt files on the map, plus the live *Line Comparison* overlay (your line vs the reference through the current corner) |
| **Leaderboards**: personal bests, compare with rivals | **Leaderboards**:<br>• PBs for every car/track<br>• your fastest laps and theoretical best<br>• "rivals" = laps from any other driver's .ibt you import (friend, coach, pro)<br>• any lap can become your live delta reference |
| **Setup optimiser** (descriptive questions) | **Setup → Optimiser**: answer what / where / which corners / how bad / preference and get prioritised changes. Each change shows the current value from your actual setup (e.g. "Rear ARB = Soft"), with driving-technique tips |
| — | **Setup → Auto-detect**: understeer / oversteer by corner phase and speed, measured from steering vs yaw response at the limit, plus wheelspin, lock-ups and tyre temps → suggested changes **for this car only**: iRacing's live setup lists exactly what the car can adjust, so nothing it doesn't have is suggested (and it says what it skipped), fixed setups get only in-car dials, and the target is given where it's clear ("blade 5 → 4", "7 → 6 clicks", "57.0% → 57.5%") |
| — | **Setup → Journal**: every setup you drive is fingerprinted automatically; see the fastest one and exactly what changed between them |
| **Setup auto installer** (type / version / car filters) | **Setup → Installer**: keep setups in `Documents\RacingHelper\SetupLibrary\<car folder>\…`. When you join a session they're copied into `iRacing\setups\<car>\RacingHelper`, filtered by race / quali / wet, latest version only, and optionally matching the track |
| **Tyre tool**: pressure & temperature analysis, smart feedback, one-click adjustments, stint/lap, avg/max | **Tyres**: hot pressures and inner/middle/outer temps per lap or stint (average or peak), against a per-car target window. Gives camber and pressure advice, suggested cold pressures, and **"Send to pit"** sets them in iRacing's pit service |
| **Fuel calculator**: auto and manual | Live fuel overlay and page (per-lap usage, laps in tank, fuel to finish / to add, one-click pit fuel), plus a race planner that fills itself in from your history |
| **Stint tracking** | Session page: stints table (best, average, consistency, pace trend in s/lap, fuel per lap) and a pace chart through the session |
| **Overlays** (all 20) | Essential Inputs, Input Telemetry, Input Comparison, Speed Comparison, Brake Indicator, Delta Sectors, Delta Bar, Standings (with projected iRating change), Relatives, Comparison Target, Corner Analysis, Track Map, Mini Map, Fuel, Tyres, Weather, Radar, Damage, Line Comparison, Weather Forecast, **plus** a Race Engineer feed |

### The engineer (feedback while you drive)

The engineer speaks through CrewChief's radio (see *CrewChief V4 as the voice*; there is no other voice) and also writes everything to the feed overlay and dashboard:

- **Session start:** greets you with the car, track and your PB here; announces setup changes since your last run and the track conditions.
- **Every lap:** lap time and delta to the reference, plus the corner where you lost the most and why ("Most time lost at turn 5: 0.38, apex −8 km/h").
- **Personal and session bests.**
- **Fuel:** warnings, laps left, and how much to add.
- **Race control:** flags (blue, yellow, white, checkered, black, meatball) and incidents against the limit.
- **Car:** engine warnings, damage / repair times, and pace loss after contact.
- **Weather:** track temperature changes and rain.
- **Optional corner callouts** after every corner where you lost time.
- **Session end:** a debrief summary with what to focus on next time.

### Live pace: your session analysis while you drive

Open **Live pace** in the dashboard (a second monitor, alt-tab, or a phone/tablet: turn on **Settings → Allow opening the dashboard from other devices**, restart Racing Helper, and the address to type on your phone is shown there and at the bottom of the sidebar) and keep driving. You don't need to leave the session: it updates a moment after every lap.

- **Last lap, best lap, theoretical best** (all your best corners together, and which corners hold the time), **trend** (s per lap over your last clean laps: getting faster / slower / steady) and the average and spread of your last 5 clean laps.
- **This lap**, live: delta, predicted lap, sectors and the last corner.
- **Where your time is:** the corners where you lose the most on average against your own best there, with apex speeds, braking-point spread and lock-ups.
- **What to work on:** the debrief (consistency, coasting, trail braking, shift points, lock-ups, tyres), updated every lap.
- **Setup, live:** what to change in the car right now (brake bias, TC, ABS, in-car bars), the setup session and setup sheet, and the garage changes the handling analysis suggests from this session, plus the handling grid. The Live page shows the top three garage suggestions too.
- Pace chart and the lap table.

When you're not on track it shows your last session.

### iPad / tablet: the pit-wall view

Open Racing Helper's address on an iPad or phone (Settings shows it once *Allow opening the dashboard from other devices* is on) and you get a focused, full-screen page made for a tablet next to the rig, in portrait or landscape: last / best lap, how much is in your best corners, the trend, **where you're slow and how to be faster** (the corners where your last clean laps keep losing time to your reference, each with what to do: "Get back to throttle earlier, 59 m later than the reference", "Carry 4 km/h more at the apex"), **what to change on the car** (in-car dials now, the setup session, garage changes for this car with target values), tyres and the last incident. *Open the full dashboard* at the bottom gets you everything else. Share → Add to Home Screen makes it an app icon.

**Phone says "unreachable"?** Settings → *Phone & tablet access* checks it for you: it shows the right address (your real Wi-Fi/Ethernet one, not a VPN or virtual adapter), whether Windows Firewall blocks Racing Helper (Windows adds a hidden block rule when its firewall popup is cancelled, and most home networks count as "Public"), and an **Allow through Windows Firewall** button that fixes it after the admin prompt. It removes the block rules and lets in only devices on your own network. The phone also has to be on the same Wi-Fi (not mobile data or a guest network), and some routers keep Wi-Fi devices apart ("AP isolation").

### Radio mode: practice, qualifying, race

By default the radio changes with the session (**Settings → Race engineer → Radio mode → Automatic by session**):

| Session | What you hear |
|---|---|
| **Practice** | Lots of info: lap times and deltas, the corner where you lost the most and the ones where you gained, conditions, setup-session and in-car advice, tyre calls, corner coaching. |
| **Qualifying** | Half silent: "Out lap, get heat into the tyres", "Tyres are in. Push now.", tyre overheating, the corner where you lost the most, corner tips, crash analysis, "time for one more lap", and anything urgent. No lap times, setup chatter or general info. |
| **Race** | The normal race radio (as before). |

When a new session starts on automatic, the engineer says once which radio it's on ("Qualifying radio. Half silent…"). You can also fix it to one mode in Settings (or Minimal: flags, fuel, damage, PBs), or cycle it while driving with the **Radio mode** button/key (`Ctrl+Shift+F4`): automatic → practice → qualifying → race → automatic. The current mode shows on the Live page and the VR panel. Answers to your own questions always come through, in every mode.

### Managing the car while you drive

- **Tyres: cold → push → cool → push.**
  - Leaving the pits it tells you the tyres are cold, then "up to temperature, you can push".
  - When you start overheating a tyre it says which one and how long cooling should take ("Right front overheating… Cooling them takes about a lap and a half"), and "Tyres have cooled down. Good to push again" once you've driven a normal stretch.
  - The cool-down estimate keeps updating from how you actually drive. Cooling faster than expected: "You're doing better than expected. About half a lap more." Still sliding: "Still sliding the rears, they can't cool like this. Ease off a bit more." Slower than planned: "About one lap more, keep it smooth".
  - iRacing doesn't publish tyre temperatures while you drive, so this measures the cause instead: how much each tyre is **sliding** (steering vs how much the car actually rotates, weighted by speed, g-force and which side is loaded).
  - The last lap is compared with your own clean laps at the same places on track. It learns that after 3 clean laps, then remembers it per car and track (`tyre-baselines.json`), so next time the calls work from lap 1.
  - Checked against real .ibt surface temperatures: the calls land on the laps where the measured temperatures and peaks climb.
- **Corner coaching.** When you keep losing time in a corner for the same reason, you get one short tip *before* that corner on the next lap, timed to finish before the braking point. For example: "Turn 4, turn it in a bit later and sharper, so you can get on the power earlier", or "Turn 2, brake later. You're about 10 metres early". If the next attempt is quicker you hear "Better through turn 2". It runs in practice by default; you can switch it on for qualifying and races, or off.
- **Guided setup session (practice).** Like working with a real engineer:
  1. "Give me 5 clean laps at a steady pace." It counts down ("2 more laps", "last lap of this run").
  2. It analyses your handling at the limit: "You've got oversteer on exit in medium corners. Box, and in the garage: Rear anti-roll bar, soften one step. It's on 2 now. Then 5 laps."
  3. It notices when you've made the change ("Got it: Rear ArbBlade 2 to 1"), runs 5 more laps and compares pace and balance: "That change works: 0.25 quicker, and the oversteer is down 40 percent. Keep it." If it got worse, it asks you to put it back.
  4. It repeats this for up to four changes.
  - iRacing doesn't let other apps change the garage setup (springs, wing, garage anti-roll bars…). Its chat / pit commands only cover pit service: fuel, tyres, pressures, compound, tear-off, fast repair. So for garage items you make the change and the app checks it. In fixed-setup sessions it only suggests in-car adjustments.
  - It starts on its own in practice, or from the Live page or a wheel button. The run length (3–10 laps) is in Settings.
- **In-car adjustments, said not done.** Every few clean laps it checks your balance. If brake bias, TC, ABS or an in-car anti-roll bar would help, it tells you all of them in one sentence. For example: "Car adjustments: increase TC by 1, and move brake bias back 0.5. That's for oversteer on exit and understeer on entry." You make the change; it notices ("Got it: TC 4") and judges the new settings on fresh laps. It only suggests what your car actually has.
- **Setup sheet / preset (one per car and track).** Every change that proves quicker in a setup session goes into a preset for that car and track. Example: "Rear wing: add rear wing. It's on 1 deg now" → "That change works: 0.35 quicker. Keep it" → preset `RearWingAngle 2 deg`.
  - It's kept in one file that is rewritten in place, never duplicated: `Documents\RacingHelper\SetupSheets\<car>\<track>.txt`.
  - When you're in the pit box in practice (or at the start of a session), the engineer reads out any preset values your current setup doesn't have: "Setup sheet: Aero RearWingAngle to 2 deg, you're on 1 deg."
  - iRacing doesn't let other apps write `.sto` setup files or change the garage setup, so you set the values in the garage and save. The app checks that you did.
- **Crash analysis.** After a spin, crash or contact (2x and above), it looks at the seconds before and tells you what started it once you've slowed down:
  - power oversteer (throttle while still turning)
  - the rear stepping out under braking
  - lift-off oversteer
  - a kerb strike
  - running wide, or carrying too much speed compared with your reference
  - contact with another car. In races it also says whether it was your doing ("Contact from behind at turn 1. Not your doing.").

  Each comes with a driving tip. The second time the same thing happens, it adds the setup fix (in-car first, e.g. "increase TC by 1"). Real example from a Bathurst session: "Power oversteer at turn 2: throttle on while the car was still turning, and the rear let go. Into the wall. Squeeze the throttle in and wait until the steering starts to unwind."
- **Race debrief → next practice.** After a race: your position, incidents, crashes (yours vs others), pace drop over the race, tyre overheating and any of the engineer's calls you didn't act on. Anything worth fixing is saved for that car and track. In the next practice there, the setup session starts with it: "Before you go out, from the race: 2 × power oversteer. In the garage, rear anti-roll bar, soften one step." Then it runs and judges it as usual, and a change that proves good goes into the preset. The session briefing also reminds you where you went off last time.
- **Automatic pit service.** As you enter pit road, it fills in iRacing's pit menu for you, then says what it set:
  - fuel to the finish (plus your safety margin)
  - four tyres if at least *N* laps are left after the stop (Settings, default 6: new tyres for the last couple of laps aren't worth the time), with the cold pressures your last run here says you need
  - fast repair if there's damage
  - a tear-off

  It uses iRacing's own pit-service commands, the same ones as the `#fuel`, `#lf` chat macros. It's on in races by default and can be switched to every session or off in Settings. Only pit service can be set this way; wing, springs and other garage items can't be changed at a stop.
- **Every pit stop:** iRacing's crew measures the tyres in the box. Those readings become a short report with camber and pressure advice (inside/outside spread, middle vs edges, front vs rear balance).
- **Qualifying:** "Time for one more lap after this one" / "This is your last lap. Make it count."
- **Only talks on straights.** Messages wait until you're not braking or cornering and there's room to finish the sentence before the next braking point. Urgent calls, corner tips (already timed before the braking point) and answers to your own questions go out straight away. Old low-priority chatter is dropped rather than read out late.

### VR: the setup panel in your cockpit (OpenKneeboard)

Racing Helper serves a VR panel at `http://127.0.0.1:5199/kneeboard.html`. It's built for [OpenKneeboard](https://openkneeboard.com), which shows it inside iRacing. You can place it, rotate it, resize it and show/hide it with a button, and OpenKneeboard remembers where you put it.

The panel shows:
- **Change on the car:** the setup session's instruction, the in-car adjustments it suggests, and preset values your setup is missing
- the setup session progress
- your pace: last and best lap, the trend, and how much is in your best corners
- tyre state and load per tyre, with the cool-down estimate
- fuel and what the pit service will set
- the last incident and why it happened
- the latest engineer calls

Setup (once):
1. Install OpenKneeboard.
2. Run iRacing in **OpenXR** mode, not the legacy Oculus mode; OpenKneeboard can't draw into that. With a Meta headset: in the Meta Quest Link app → Settings → General, set it as the active OpenXR runtime, then choose OpenXR in iRacing's VR settings.
3. In OpenKneeboard → Tabs → add a **Web Dashboard** tab with the address `http://127.0.0.1:5199/kneeboard.html`.
4. In OpenKneeboard → VR, set the position, angle and size. Under Input, bind a wheel button to show/hide it.
5. If it doesn't appear in iRacing, set `DepthLayerExtensionEnabled=0` in iRacing's ini file (see OpenKneeboard's known issues for iRacing).

### Your own keys and buttons

In **Settings → Buttons & keys** every action can have your own keyboard shortcut (**Set key**, then press the combination) and a wheel/button-box button (**Bind wheel**, then press the button). That includes the questions, moving/hiding the overlays, switching the delta reference and opening the dashboard. If Windows says another program already uses a key, you're told to pick another.

### Ask the engineer from your wheel

Bind any question to a button on your wheel or button box in **Settings → Wheel buttons & questions**: click **Bind**, then press the button. It works with Moza, Fanatec, Simucube or any controller Windows sees, while iRacing has focus, and on every button the device has.

| Question | Example answer | Keyboard |
|---|---|---|
| How are my tyres? | "The right front is hot. Fronts at 124 percent of normal, rears 98. About one lap more cooling." | `Ctrl+Shift+F5` |
| Fuel | "8.3 litres, 8.3 laps at 1.00 a lap. Enough to the end, 3.9 spare." | `Ctrl+Shift+F6` |
| Gaps | "P4. Car ahead 2.2 seconds, you're gaining 0.4 a lap, on him in about 4 laps. Car behind 1.5 back and steady." | `Ctrl+Shift+F7` |
| My pace | "Last lap 1:31.60, plus 0.29 to the reference. Most lost at turn 4, 0.39, throttle 12 m late. Last 5 clean laps average 1:31.87, spread 1.2." | |
| Where's the time? | "Your best corners add up to 0.46 quicker than your best lap. Most of it at turn 1, 0.24 and turn 3, 0.13." | |
| Setup session status, and start/stop | "Testing Rear anti-roll bar: 2 of 5 clean laps done." | |
| What should I change in the car? | "Car adjustments: increase TC by 1, and move brake bias back 0.5." | |
| What's on my setup sheet? | "Your preset here differs from the car: Aero RearWingAngle should be 2 deg, you're on 1 deg." | |
| Why did I crash? | the last crash explanation | |
| What will you set at my pit stop? | "When you box I'll set: 7 litres, four tyres at 176, 177, 171, 172 kPa, tear-off." | |
| Repeat the last message | | `Ctrl+Shift+F8` |
| Quiet mode on/off | Only important calls until you switch it back | |
| Radio mode | "Qualifying radio. Half silent. Only tyre warm-up, when to push, and where you're losing time." | `Ctrl+Shift+F4` |
| Radio check | "Radio check, loud and clear through CrewChief. Automatic radio, race right now." | `Ctrl+Shift+F3` |

### More calls you don't get from iRacing

- **Gap trends (race):** every few laps, who's catching whom and when. "Car ahead 2.2 seconds, you're gaining 0.4 a lap, on him in about 4 laps." / "Car behind is 0.3 a lap quicker, with you in about 3 laps."
- **Off-track hot spots:** before a corner where you've gone off twice this session: "Careful at turn 6. You've been off there twice."
- **Fuel-save coaching:** when you're short to the finish, how much to save and exactly where. "0.8 litres short. Save 0.10 a lap: lift and coast about 60 metres before the braking points into turn 1 and turn 4." It says when the saving has done the job, or tells you straight that you need a splash.
- **Braking consistency:** "Turn 2: your braking point moved around 30 metres over the last five laps. Pick one marker and brake there every lap."
- **Potential:** in practice and qualifying, every few laps, how much your best corners are worth together and where.

### CrewChief V4 as the voice

If you use [CrewChief](https://thecrewchief.org/), it can speak Racing Helper's messages over its radio, so you hear one engineer and it never talks over the spotter. CrewChief has an MQTT feature, and Racing Helper runs a small MQTT server on `127.0.0.1` for it to connect to.

1. **Settings → CrewChief → Set up CrewChief.** This points CrewChief at Racing Helper by editing `Documents\CrewChiefV4\mqtt_telemetry.json`. The original is backed up as `mqtt_telemetry.json.before-racinghelper`.
2. **In CrewChief → Properties:**
   - tick **MQTT Telemetry enabled**
   - type any **MQTT drivername**
   - leave text-to-speech on (anything but "Never")
3. Save and restart CrewChief, and press **Start Application** in CrewChief. The status in Settings turns green: "Connected as …".
4. Get in the car and do a radio check (Settings button, or your bound key/button, `Ctrl+Shift+F3`). Jim answers in his own voice.

**CrewChief only talks while you're in a session.** It drops messages that arrive while you're in the menus, while it isn't started, or before the session is running. Racing Helper sees whether CrewChief is live (it only sends its telemetry then) and holds anything said before that, for up to 2 minutes, until CrewChief can play it. A radio check from the menus tells you on screen why there's no answer yet.

**Jim's voice vs the TTS voice.** CrewChief plays Jim's real recordings only for phrases he recorded; any other sentence it can only read with a Windows text-to-speech voice. So every call Jim has a recording for goes out as his clip: cold tyres, hot / cooking tyres (front, rear, each corner, all round), good tyre temps, last lap, radio check. The rest (corner coaching, setup and in-car changes, crash analysis, cool-down estimates) has no recording, so CrewChief reads it with TTS. Settings → CrewChief → *Only Jim's own recorded voice* skips those instead; they still show on the dashboard and the iPad view.

**Nothing gets forgotten when there's a lot to say.** Everything waiting is sent together on the next straight that has room for it, straight into CrewChief's immediate queue (its normal queue throws a message away if it hasn't played within 10 seconds). What doesn't fit waits for the next straight, for up to 2–4 minutes.

Details:

- Nothing from CrewChief at all, even on track? Check CrewChief → Properties → text-to-speech isn't "Never" (Jim's clips still play, but the rest needs TTS). CrewChief prefers a male Windows voice (e.g. Microsoft David); with only another voice installed it uses that one.
- Things CrewChief already says itself (flags, fuel, lap times, PBs, incidents, damage) aren't sent twice. They still appear in the feed.
- Corner tips are sent with a distance window, so CrewChief drops a tip rather than play it after the braking point.
- iRacing has no in-game race engineer that other apps can control. CrewChief is how Racing Helper talks to you.

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
- **Gap trends** need other cars' data, which iRacing only gives live (not in .ibt replays).
- **Wheel buttons** use Windows Raw Input; if a button doesn't register, check that Windows sees the device in *Set up USB game controllers*.
- **How the balance is measured.** For every moment near the grip limit (70% of the sideways grip the car shows at that speed, so downforce cars count in slow corners too), it compares how much steering the car needs with what the same car needs at moderate cornering in the same speed range and phase (braking / mid / throttle). That cancels the normal speed effect (every car needs relatively more lock at speed) and the normal rotation under braking. Within ±10% is neutral. A hot spot is a corner where it happened on at least two laps. Lock-ups count when a wheel turns 30%+ slower than the car for 6 m (5–15% under hard braking is normal), and they and wheelspin only become setup suggestions when they happen on at least every other lap. Validated on two F4 sessions.
- **Tyre management is relative, not in degrees.** It knows "you're sliding the fronts 25% more than your normal clean laps here", not "the fronts are 104°C". It was validated on two F4 sessions (Bathurst, Phillip Island). The thresholds may need tuning for very different cars, and it can't see straight-line lock-ups (no live wheel speeds).
- **CrewChief link:** needs CrewChief's MQTT feature (CrewChief V4 4.16+). Racing Helper only listens on this PC (127.0.0.1); change the port in Settings if another MQTT broker already uses 1883.
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
