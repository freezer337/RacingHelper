using System.Text.RegularExpressions;
using RacingHelper.Sim;

namespace RacingHelper.Analysis;

public sealed class SetupRequest
{
    public string Symptom { get; set; } = "understeer";  // see SetupOptimiser.Symptoms
    public string Phase { get; set; } = "mid";           // entry / mid / exit / all
    public string Speed { get; set; } = "all";           // slow / fast / all
    public int Severity { get; set; } = 2;               // 1 slight, 2 moderate, 3 severe
    public string Preference { get; set; } = "neutral";  // stable / neutral / pointy
    public string Category { get; set; } = "gt";
}

public sealed class SetupChange
{
    public int Priority { get; set; }
    public string Area { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string Action { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Why { get; set; } = "";
    public List<string> Current { get; set; } = new(); // "Chassis.Front.ArbSize = Medium"
}

public sealed class SetupAdvice
{
    public string Title { get; set; } = "";
    public List<SetupChange> Changes { get; set; } = new();
    public List<string> DrivingTips { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public static class SetupOptimiser
{
    public static readonly (string id, string label)[] Symptoms =
    {
        ("understeer", "Understeer — the front won't turn"),
        ("oversteer", "Oversteer — the rear steps out"),
        ("braking-instability", "Unstable / rear light under braking"),
        ("front-lockup", "Front wheels lock under braking"),
        ("rear-lockup", "Rear wheels lock under braking"),
        ("traction", "Wheelspin / poor traction on exit"),
        ("bottoming", "Bottoming out / scraping"),
        ("kerbs", "Car is upset by kerbs and bumps"),
        ("lazy", "Lazy / slow in direction changes"),
        ("top-speed", "Lacking straight-line speed"),
        ("high-speed-grip", "Lacking grip in fast corners"),
        ("tyres-hot-front", "Front tyres overheating"),
        ("tyres-hot-rear", "Rear tyres overheating"),
    };

    // parameter → regex over flattened setup keys
    static readonly Dictionary<string, string> ParamKeys = new()
    {
        ["Brake bias"] = @"BrakePressureBias|BrakeBias|FrontBrakeBias",
        ["Front anti-roll bar"] = @"(Front|^Chassis\.Front).*?(Arb|AntiRoll|Sway)|FrontArb|ArbFront",
        ["Rear anti-roll bar"] = @"(Rear|^Chassis\.Rear).*?(Arb|AntiRoll|Sway)|RearArb|ArbRear",
        ["Front springs"] = @"(LeftFront|RightFront|Front).*?(SpringRate|TorsionBarOD|HeaveSpring)",
        ["Rear springs"] = @"(LeftRear|RightRear|Rear).*?(SpringRate|TorsionBarOD|ThirdSpring|HeaveSpring)",
        ["Front ride height"] = @"(LeftFront|RightFront|Front).*?RideHeight",
        ["Rear ride height"] = @"(LeftRear|RightRear|Rear).*?RideHeight",
        ["Front camber"] = @"(LeftFront|RightFront).*?Camber",
        ["Rear camber"] = @"(LeftRear|RightRear).*?Camber",
        ["Front toe"] = @"(LeftFront|RightFront|Front).*?Toe",
        ["Rear toe"] = @"(LeftRear|RightRear|Rear).*?Toe",
        ["Front bump damping"] = @"(LeftFront|RightFront|Front).*?(Comp|Bump)",
        ["Front rebound damping"] = @"(LeftFront|RightFront|Front).*?(Rbd|Rebound)",
        ["Rear bump damping"] = @"(LeftRear|RightRear|Rear).*?(Comp|Bump)",
        ["Rear rebound damping"] = @"(LeftRear|RightRear|Rear).*?(Rbd|Rebound)",
        ["Rear wing"] = @"RearWing|WingAngle|RearUpperFlap|RearBeamWing|WingSetting",
        ["Front wing"] = @"FrontFlap|FrontWing|FrontFlapAngle|DiveP",
        // must be inside a differential section (TorsionBarPreload etc. are not diff settings)
        ["Differential (coast)"] = @"^(?=.*Diff)(?=.*(Coast|Ramp|Preload|Friction|Clutch)).*",
        ["Differential (power)"] = @"^(?=.*Diff)(?=.*(Drive|Power|Ramp|Preload|Friction|Clutch)).*",
        ["Traction control"] = @"TractionControl|TcSetting|^.*\bTc\b",
        ["ABS"] = @"AbsSetting|\bAbs\b|ABS",
        ["Front tyre pressure"] = @"(LeftFront|RightFront).*?(ColdPressure|StartingPressure)",
        ["Rear tyre pressure"] = @"(LeftRear|RightRear).*?(ColdPressure|StartingPressure)",
        ["Gearing"] = @"GearStack|FinalDrive|Gear\d|TopSpeed",
        ["Bump stops / packers"] = @"BumpStop|Packer|BumpRubber",
    };

    record Rule(string Symptom, string Phases, string Speeds, string Cats, string Area, string Param, string Action, string Why, int Weight);

    // Phases/Speeds/Cats: "*" = any, otherwise comma lists. Cats: gt, formula, prototype, touring, oval
    static readonly Rule[] Rules =
    {
        // ---------- understeer ----------
        new("understeer", "entry", "*", "*", "Brakes", "Brake bias", "Move rearward 0.5–1%", "More rear braking helps the car rotate on turn-in.", 9),
        new("understeer", "entry", "*", "*", "Differential", "Differential (coast)", "Less coast locking / preload", "A freer diff off-throttle lets the rear rotate into the corner.", 7),
        new("understeer", "entry", "*", "*", "Dampers", "Front bump damping", "Soften 1–2 clicks", "Lets the front tyres take load more progressively on turn-in.", 5),
        new("understeer", "entry", "*", "*", "Dampers", "Rear rebound damping", "Stiffen 1–2 clicks", "Slows the rear rising under braking, helping rotation.", 5),
        new("understeer", "entry,mid", "*", "*", "Alignment", "Front toe", "More toe-out (small step)", "Sharper initial response.", 3),
        new("understeer", "mid,entry,exit", "*", "*", "Anti-roll bars", "Front anti-roll bar", "Soften one step", "Less front roll stiffness = more front mechanical grip.", 10),
        new("understeer", "mid", "*", "*", "Anti-roll bars", "Rear anti-roll bar", "Stiffen one step", "Shifts balance rearward so the car rotates more mid-corner.", 8),
        new("understeer", "mid", "slow", "*", "Springs", "Front springs", "Soften slightly", "More front mechanical grip in slow corners.", 6),
        new("understeer", "mid", "fast", "formula,prototype,gt", "Aero", "Front wing", "Add front wing / flap", "More front downforce in high-speed corners.", 9),
        new("understeer", "mid", "fast", "formula,prototype,gt", "Aero", "Rear wing", "Reduce rear wing slightly", "Moves aero balance forward (also gains top speed).", 6),
        new("understeer", "mid", "fast", "formula,prototype,gt", "Ride height", "Rear ride height", "Raise rear / lower front (more rake)", "More rake moves aero balance forward.", 7),
        new("understeer", "mid", "*", "*", "Alignment", "Front camber", "Add ~0.2° negative (check tyre temps)", "Only if the inside of the front tyre is not already much hotter than the outside.", 4),
        new("understeer", "exit", "*", "*", "Differential", "Differential (power)", "Less power-side locking", "A tightly locked diff pushes the car straight on throttle.", 8),
        new("understeer", "exit", "*", "*", "Dampers", "Front rebound damping", "Soften 1–2 clicks", "Keeps the front planted as weight moves back on throttle.", 5),
        new("understeer", "exit", "*", "*", "Dampers", "Rear bump damping", "Stiffen 1–2 clicks", "Slows rear squat so the front keeps grip longer.", 4),
        new("understeer", "*", "*", "*", "Tyres", "Front tyre pressure", "Check hot pressures — lower if above window", "Over-inflated fronts reduce contact patch.", 3),

        // ---------- oversteer ----------
        new("oversteer", "entry", "*", "*", "Brakes", "Brake bias", "Move forward 0.5–1%", "Less rear braking keeps the rear stable on turn-in.", 9),
        new("oversteer", "entry", "*", "*", "Differential", "Differential (coast)", "More coast locking / preload", "A more locked diff off-throttle stabilises the rear.", 8),
        new("oversteer", "entry", "*", "*", "Dampers", "Rear rebound damping", "Soften 1–2 clicks", "Lets the rear settle instead of unloading on the brakes.", 5),
        new("oversteer", "entry", "*", "*", "Dampers", "Front bump damping", "Stiffen 1–2 clicks", "Slows nose dive, keeping more load on the rear.", 4),
        new("oversteer", "entry,mid,exit", "*", "*", "Alignment", "Rear toe", "Add rear toe-in", "Rear toe-in is the strongest stabiliser.", 7),
        new("oversteer", "mid,entry,exit", "*", "*", "Anti-roll bars", "Rear anti-roll bar", "Soften one step", "More rear mechanical grip.", 10),
        new("oversteer", "mid", "*", "*", "Anti-roll bars", "Front anti-roll bar", "Stiffen one step", "Moves balance forward.", 7),
        new("oversteer", "mid", "slow", "*", "Springs", "Rear springs", "Soften slightly", "More rear grip in slow corners.", 6),
        new("oversteer", "mid,exit", "fast", "formula,prototype,gt", "Aero", "Rear wing", "Add rear wing", "More rear downforce for high-speed stability.", 10),
        new("oversteer", "mid", "fast", "formula,prototype,gt", "Ride height", "Rear ride height", "Lower rear / raise front (less rake)", "Less rake moves aero balance rearward.", 7),
        new("oversteer", "exit", "*", "gt,prototype,touring", "Electronics", "Traction control", "Increase TC 1 step", "Catches the power oversteer while you work on throttle application.", 9),
        new("oversteer", "exit", "*", "*", "Differential", "Differential (power)", "Adjust power-side locking", "Snappy exits with both rears spinning → reduce locking; inside rear spinning up → increase it.", 7),
        new("oversteer", "exit", "*", "*", "Springs", "Rear springs", "Soften slightly", "Better rear traction under power.", 6),
        new("oversteer", "exit", "*", "*", "Dampers", "Rear bump damping", "Soften 1–2 clicks", "Lets the rear squat and find traction.", 5),
        new("oversteer", "*", "*", "*", "Tyres", "Rear tyre pressure", "Check hot pressures — lower if above window", "Over-inflated rears lose grip and overheat.", 3),

        // ---------- braking ----------
        new("braking-instability", "*", "*", "*", "Brakes", "Brake bias", "Move forward 1%", "Rear brakes doing too much work.", 10),
        new("braking-instability", "*", "*", "*", "Differential", "Differential (coast)", "More coast locking / preload", "Locks the rear axle together under braking.", 8),
        new("braking-instability", "*", "*", "*", "Alignment", "Rear toe", "Add rear toe-in", "Straight-line stability under braking.", 6),
        new("braking-instability", "*", "*", "*", "Dampers", "Rear rebound damping", "Soften", "Rear stays planted as weight transfers forward.", 4),
        new("front-lockup", "*", "*", "*", "Brakes", "Brake bias", "Move rearward 0.5–1%", "Fronts reaching the lock limit first.", 10),
        new("front-lockup", "*", "*", "gt,prototype,touring", "Electronics", "ABS", "Increase ABS 1 step", "More intervention prevents flat-spots.", 7),
        new("front-lockup", "*", "*", "*", "Tyres", "Front tyre pressure", "Check window — slightly lower if high", "Bigger contact patch under braking.", 3),
        new("rear-lockup", "*", "*", "*", "Brakes", "Brake bias", "Move forward 0.5–1%", "Rears reaching the lock limit first.", 10),
        new("rear-lockup", "*", "*", "*", "Differential", "Differential (coast)", "More coast locking", "Stabilises the rear axle under braking and downshifts.", 6),

        // ---------- traction ----------
        new("traction", "*", "*", "gt,prototype,touring", "Electronics", "Traction control", "Increase 1 step", "Limits wheelspin while you refine throttle application.", 9),
        new("traction", "*", "*", "*", "Anti-roll bars", "Rear anti-roll bar", "Soften one step", "Keeps the inside rear loaded on exit.", 8),
        new("traction", "*", "*", "*", "Springs", "Rear springs", "Soften slightly", "More rear grip under power.", 7),
        new("traction", "*", "*", "*", "Differential", "Differential (power)", "More power-side locking if the inside rear spins", "Shares torque to the loaded wheel.", 7),
        new("traction", "*", "*", "*", "Dampers", "Rear bump damping", "Soften 1–2 clicks", "Lets the rear squat onto the tyres.", 5),
        new("traction", "*", "*", "*", "Tyres", "Rear tyre pressure", "Lower slightly if above window", "Larger contact patch.", 4),

        // ---------- platform ----------
        new("bottoming", "*", "*", "*", "Ride height", "Front ride height", "Raise 1–2 mm (front if scraping on braking)", "Prevents the floor hitting the track.", 9),
        new("bottoming", "*", "*", "*", "Ride height", "Rear ride height", "Raise 1–2 mm (rear if scraping on exits / compressions)", "Prevents the floor hitting the track.", 8),
        new("bottoming", "*", "*", "*", "Springs", "Front springs", "Stiffen (or heave/third spring)", "Less travel under aero load and braking.", 7),
        new("bottoming", "*", "*", "*", "Suspension", "Bump stops / packers", "Add packers / earlier bump stop", "Limits travel at the bottom of the stroke.", 6),
        new("kerbs", "*", "*", "*", "Dampers", "Front bump damping", "Soften (fast bump if available)", "Absorbs kerb strikes instead of launching the car.", 9),
        new("kerbs", "*", "*", "*", "Dampers", "Rear bump damping", "Soften (fast bump if available)", "Absorbs kerb strikes.", 8),
        new("kerbs", "*", "*", "*", "Ride height", "Front ride height", "Raise slightly", "More clearance over kerbs.", 5),
        new("kerbs", "*", "*", "*", "Anti-roll bars", "Front anti-roll bar", "Soften", "Lets each wheel ride the kerb independently.", 5),
        new("lazy", "*", "*", "*", "Anti-roll bars", "Front anti-roll bar", "Stiffen", "Faster roll response in direction changes.", 8),
        new("lazy", "*", "*", "*", "Anti-roll bars", "Rear anti-roll bar", "Stiffen", "Faster roll response in direction changes.", 7),
        new("lazy", "*", "*", "*", "Dampers", "Front bump damping", "Stiffen low-speed bump", "Quicker weight transfer = sharper response.", 6),
        new("lazy", "*", "*", "*", "Ride height", "Front ride height", "Lower slightly", "Lower centre of gravity.", 4),

        // ---------- speed / aero ----------
        new("top-speed", "*", "*", "formula,prototype,gt", "Aero", "Rear wing", "Reduce 1 step (rebalance front)", "Less drag. Balance with front wing on open-wheelers.", 10),
        new("top-speed", "*", "*", "*", "Gearing", "Gearing", "Check top gear vs end of longest straight", "If you hit the limiter before braking, lengthen the top gear; if you never reach it, shorten.", 8),
        new("top-speed", "*", "*", "formula,prototype", "Aero", "Front wing", "Reduce to match rear", "Keep aero balance after trimming rear wing.", 6),
        new("high-speed-grip", "*", "*", "formula,prototype,gt", "Aero", "Rear wing", "Add 1 step", "More downforce; check balance afterwards.", 9),
        new("high-speed-grip", "*", "*", "formula,prototype", "Aero", "Front wing", "Add to keep balance", "Keep aero balance after adding rear wing.", 7),
        new("high-speed-grip", "*", "*", "*", "Ride height", "Front ride height", "Lower slightly (watch bottoming)", "Lower = more ground-effect downforce.", 7),
        new("high-speed-grip", "*", "*", "*", "Springs", "Front springs", "Stiffen (heave)", "Holds the aero platform at speed.", 5),

        // ---------- tyres ----------
        new("tyres-hot-front", "*", "*", "*", "Tyres", "Front tyre pressure", "Lower 2–4 kPa if hot pressures are high", "Reduces heat build-up.", 6),
        new("tyres-hot-front", "*", "*", "*", "Anti-roll bars", "Front anti-roll bar", "Soften", "Less front sliding (understeer) = less heat.", 7),
        new("tyres-hot-front", "*", "*", "*", "Alignment", "Front toe", "Reduce toe-out", "Toe scrub generates heat on straights.", 5),
        new("tyres-hot-rear", "*", "*", "*", "Anti-roll bars", "Rear anti-roll bar", "Soften", "Less rear sliding = less heat.", 7),
        new("tyres-hot-rear", "*", "*", "gt,prototype,touring", "Electronics", "Traction control", "Increase", "Less wheelspin = cooler rears.", 7),
        new("tyres-hot-rear", "*", "*", "*", "Alignment", "Rear toe", "Reduce rear toe-in slightly", "Less scrub.", 4),
        new("tyres-hot-rear", "*", "*", "*", "Tyres", "Rear tyre pressure", "Lower 2–4 kPa if hot pressures are high", "Reduces heat build-up.", 5),
    };

    static readonly Dictionary<string, string[]> Tips = new()
    {
        ["understeer"] = new[]
        {
            "Brake a little earlier and trail off the brake into the apex — a loaded front axle turns better.",
            "Avoid turning in while the car is still at peak braking; release progressively as steering goes in.",
            "Be patient with the throttle — applying power before the car has rotated pushes the front wide.",
        },
        ["oversteer"] = new[]
        {
            "Release the brake more gradually on turn-in — snapping off the brake unloads the rear.",
            "Squeeze the throttle in progressively; wait until steering starts unwinding before going to full power.",
            "Shift up a gear earlier in slow corners to reduce torque at the rear wheels.",
        },
        ["braking-instability"] = new[] { "Brake in a straight line first, then trail off as you turn in.", "Blip-and-downshift more gently to avoid rear lock-ups." },
        ["front-lockup"] = new[] { "Reduce peak brake pressure slightly and bleed it off as speed drops — grip falls with downforce.", "Don't add steering while at peak pressure." },
        ["rear-lockup"] = new[] { "Downshift a little later / slower to avoid engine-braking spikes." },
        ["traction"] = new[] { "Straighten the steering before adding big throttle.", "Use a higher gear in slow corners." },
        ["kerbs"] = new[] { "Avoid the tall sausage kerbs; use the flat part of the kerb only." },
        ["top-speed"] = new[] { "Exit speed matters more than top speed — prioritise the corner before the long straight." },
        ["high-speed-grip"] = new[] { "Smooth, small steering inputs at high speed keep the aero platform stable." },
    };

    public static SetupAdvice Advise(SetupRequest req, YNode? carSetup)
    {
        var flat = new List<KeyValuePair<string, string>>();
        carSetup?.Flatten("", flat);
        var advice = new SetupAdvice
        {
            Title = Symptoms.FirstOrDefault(s => s.id == req.Symptom).label ?? req.Symptom,
        };

        var matches = Rules.Where(r => r.Symptom == req.Symptom
                                       && Match(r.Phases, req.Phase)
                                       && Match(r.Speeds, req.Speed)
                                       && Match(r.Cats, req.Category))
                           .Select(r =>
                           {
                               int w = r.Weight;
                               // preference nudges: "stable" favours stabilising aids, "pointy" favours rotation changes
                               if (req.Preference == "stable" && (r.Param.Contains("toe") || r.Param.Contains("Traction") || r.Param.Contains("coast"))) w += 2;
                               if (req.Preference == "pointy" && (r.Param.Contains("anti-roll") || r.Param.Contains("bias"))) w += 2;
                               // parameters that don't exist in this car's setup are deprioritised
                               bool exists = flat.Count == 0 || CurrentValues(r.Param, flat).Count > 0;
                               if (!exists) w -= 6;
                               return (rule: r, weight: w, exists);
                           })
                           .OrderByDescending(x => x.weight)
                           .ToList();

        string amount = req.Severity switch { 1 => "small step (1 click)", 3 => "larger step (2–3 clicks), or combine the top two changes", _ => "1–2 clicks" };
        int pr = 1;
        var seen = new HashSet<string>();
        foreach (var (rule, weight, exists) in matches)
        {
            if (!seen.Add(rule.Param)) continue;
            var change = new SetupChange
            {
                Priority = pr++,
                Area = rule.Area,
                Parameter = rule.Param,
                Action = rule.Action,
                Amount = amount,
                Why = rule.Why + (exists ? "" : " (not found in this car's setup — may not be adjustable)"),
                Current = CurrentValues(rule.Param, flat).Take(4).ToList(),
            };
            advice.Changes.Add(change);
        }
        if (Tips.TryGetValue(req.Symptom, out var tips)) advice.DrivingTips.AddRange(tips);
        advice.Notes.Add("Change one thing at a time and do 3+ consistent laps before judging it. The setup journal tracks lap times per setup automatically.");
        if (flat.Count == 0) advice.Notes.Add("No live setup available — join a session (or pick a recorded one) to see your current values next to each suggestion.");
        return advice;
    }

    static bool Match(string list, string value) => list == "*" || value == "all" || value == "*" || list.Split(',').Contains(value);

    static List<string> CurrentValues(string param, List<KeyValuePair<string, string>> flat)
    {
        if (!ParamKeys.TryGetValue(param, out var pattern)) return new();
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        return flat.Where(kv => rx.IsMatch(kv.Key) && !kv.Key.Contains("LastHot") && !kv.Key.Contains("LastTemps") && !kv.Key.Contains("TreadRemaining")
                                && !kv.Key.Contains("Defl") && !kv.Key.Contains("CornerWeight"))
                   .Select(kv => $"{Short(kv.Key)} = {kv.Value}")
                   .Distinct()
                   .ToList();
    }

    static string Short(string key)
    {
        var parts = key.Split('.');
        return parts.Length <= 2 ? key : string.Join(" › ", parts[^2..]);
    }

    /// <summary>Maps telemetry findings to optimiser requests (auto mode).</summary>
    public static List<SetupRequest> FromHandling(HandlingReport rep, string category)
    {
        var list = new List<SetupRequest>();
        foreach (var c in rep.Cells.Where(c => c.Tendency != "neutral").OrderByDescending(c => Math.Max(c.UndersteerRate, c.OversteerRate)).Take(3))
        {
            float rate = c.Tendency == "understeer" ? c.UndersteerRate : c.OversteerRate;
            list.Add(new SetupRequest
            {
                Symptom = c.Tendency,
                Phase = c.Phase,
                Speed = c.SpeedBand == "medium" ? "all" : c.SpeedBand,
                Severity = rate > 20 ? 3 : rate > 10 ? 2 : 1,
                Category = category,
            });
        }
        return list;
    }

    /// <summary>Differences between two setups (flattened), for the setup journal.</summary>
    public static List<(string key, string a, string b)> Diff(YNode? a, YNode? b)
    {
        var fa = new List<KeyValuePair<string, string>>(); a?.Flatten("", fa);
        var fb = new List<KeyValuePair<string, string>>(); b?.Flatten("", fb);
        var da = fa.Where(k => !Volatile(k.Key)).ToDictionary(k => k.Key, k => k.Value);
        var db = fb.Where(k => !Volatile(k.Key)).ToDictionary(k => k.Key, k => k.Value);
        var res = new List<(string, string, string)>();
        foreach (var k in da.Keys.Union(db.Keys))
        {
            da.TryGetValue(k, out var va); db.TryGetValue(k, out var vb);
            if (va != vb) res.Add((k, va ?? "—", vb ?? "—"));
        }
        return res;
    }

    // values that change while driving rather than being setup choices
    static bool Volatile(string key) => key.Contains("LastHot") || key.Contains("LastTemps") || key.Contains("TreadRemaining") || key.EndsWith("UpdateCount")
                                        || key.Contains("CornerWeight") || key.Contains("Defl") || key.Contains("AeroCalculator");
}
