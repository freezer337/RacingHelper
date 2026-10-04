using RacingHelper.Recording;

namespace RacingHelper.Analysis;

public sealed class TyreTarget
{
    public float HotPressureMin { get; set; }   // kPa
    public float HotPressureMax { get; set; }
    public float TempMin { get; set; }          // C (surface average)
    public float TempMax { get; set; }
    public float IdealSpread { get; set; }      // inner − outer, C (positive = inside hotter, expected with negative camber)
}

public sealed class TyreAdvice
{
    public string Tyre { get; set; } = "";
    public float PressHot { get; set; } = float.NaN;
    public float PressCold { get; set; } = float.NaN;
    public string PressStatus { get; set; } = "";   // low / ok / high
    public float SuggestedCold { get; set; } = float.NaN;
    public float TempAvg { get; set; } = float.NaN;
    public float TempIn { get; set; } = float.NaN;
    public float TempMid { get; set; } = float.NaN;
    public float TempOut { get; set; } = float.NaN;
    public float Spread { get; set; } = float.NaN;
    public float MidVsEdges { get; set; } = float.NaN; // middle − avg(edges): + = over-inflated shape
    public string TempStatus { get; set; } = "";
    public float Wear { get; set; } = float.NaN;
    public List<string> Notes { get; set; } = new();
}

public static class TyreAnalyzer
{
    public static readonly string[] Names = { "LF", "RF", "LR", "RR" };

    /// <summary>Starting-point targets by car category; users can override per car in settings.</summary>
    public static TyreTarget DefaultTarget(string category) => category switch
    {
        "gt" => new TyreTarget { HotPressureMin = 186, HotPressureMax = 193, TempMin = 75, TempMax = 95, IdealSpread = 6 },       // ≈ 27.0–28.0 psi
        "prototype" => new TyreTarget { HotPressureMin = 165, HotPressureMax = 180, TempMin = 80, TempMax = 105, IdealSpread = 8 },
        "formula" => new TyreTarget { HotPressureMin = 135, HotPressureMax = 160, TempMin = 75, TempMax = 100, IdealSpread = 8 },
        "oval" => new TyreTarget { HotPressureMin = 150, HotPressureMax = 230, TempMin = 70, TempMax = 110, IdealSpread = 0 },
        _ => new TyreTarget { HotPressureMin = 170, HotPressureMax = 200, TempMin = 70, TempMax = 95, IdealSpread = 6 },
    };

    /// <summary>Aggregates a set of laps (a stint, or a single lap) into per-tyre advice.</summary>
    public static List<TyreAdvice> Analyze(IReadOnlyList<TyreLapStats[]> laps, TyreTarget target, bool useMax = false)
    {
        var res = new List<TyreAdvice>();
        if (laps.Count == 0) return res;
        for (int t = 0; t < 4; t++)
        {
            var a = new TyreAdvice { Tyre = Names[t] };
            var s = laps.Select(l => l[t]).ToList();
            a.PressHot = Avg(s.Select(x => useMax ? x.PressMax : x.PressAvg));
            a.PressCold = s.Select(x => x.ColdPress).LastOrDefault(float.IsFinite, float.NaN);
            float tin = Avg(s.Select(x => useMax ? x.TempInMax : x.TempInAvg));
            float tmid = Avg(s.Select(x => useMax ? x.TempMidMax : x.TempMidAvg));
            float tout = Avg(s.Select(x => useMax ? x.TempOutMax : x.TempOutAvg));
            a.TempIn = tin; a.TempMid = tmid; a.TempOut = tout;
            a.TempAvg = (tin + tmid + tout) / 3;
            a.Spread = tin - tout;
            a.MidVsEdges = tmid - (tin + tout) / 2;
            a.Wear = s.Select(x => float.IsFinite(x.WearMid) ? (x.WearIn + x.WearMid + x.WearOut) / 3 : float.NaN).LastOrDefault(float.IsFinite, float.NaN);

            if (float.IsFinite(a.PressHot))
            {
                float mid = (target.HotPressureMin + target.HotPressureMax) / 2;
                if (a.PressHot < target.HotPressureMin) a.PressStatus = "low";
                else if (a.PressHot > target.HotPressureMax) a.PressStatus = "high";
                else a.PressStatus = "ok";
                if (a.PressStatus != "ok" && float.IsFinite(a.PressCold))
                {
                    // hot pressure moves roughly 1:1 with a cold change at similar temperatures (≈0.95 for typical rises)
                    a.SuggestedCold = MathF.Round(a.PressCold + (mid - a.PressHot) * 0.95f);
                    a.Notes.Add($"{(a.PressStatus == "low" ? "Raise" : "Lower")} cold pressure by ~{Math.Abs(a.SuggestedCold - a.PressCold):0} kPa (to {a.SuggestedCold:0} kPa) to reach the {target.HotPressureMin:0}–{target.HotPressureMax:0} kPa hot window.");
                }
            }
            if (float.IsFinite(a.TempAvg))
            {
                a.TempStatus = a.TempAvg < target.TempMin ? "cold" : a.TempAvg > target.TempMax ? "hot" : "ok";
                if (a.TempStatus == "cold") a.Notes.Add($"Below the {target.TempMin:0}–{target.TempMax:0}°C target ({a.TempAvg:0}°C). More pressure or a softer setup on this axle builds heat.");
                if (a.TempStatus == "hot") a.Notes.Add($"Above the {target.TempMin:0}–{target.TempMax:0}°C target ({a.TempAvg:0}°C). Check for sliding on this axle; lower pressure slightly or reduce load.");
                if (target.IdealSpread > 0)
                {
                    string spread = a.Spread >= 0 ? $"inside {a.Spread:0}°C hotter than outside" : $"outside {-a.Spread:0}°C hotter than inside";
                    if (a.Spread > target.IdealSpread + 6) a.Notes.Add($"Camber: {spread} (aim ≈{target.IdealSpread:0}°C) — too much negative camber; try ~0.2–0.3° less.");
                    else if (a.Spread < target.IdealSpread - 6) a.Notes.Add($"Camber: {spread} (aim ≈{target.IdealSpread:0}°C inside-hotter) — add ~0.2–0.3° negative camber for more mid-corner grip.");
                }
                if (a.MidVsEdges > 4) a.Notes.Add("Middle hotter than the edges — tyre over-inflated.");
                else if (a.MidVsEdges < -4) a.Notes.Add("Middle cooler than the edges — tyre under-inflated.");
            }
            res.Add(a);
        }
        // axle balance
        float front = (res[0].TempAvg + res[1].TempAvg) / 2, rear = (res[2].TempAvg + res[3].TempAvg) / 2;
        if (float.IsFinite(front) && float.IsFinite(rear))
        {
            if (front - rear > 8) res[0].Notes.Add($"Fronts run {front - rear:0}°C hotter than rears — the front is doing more sliding (understeer tendency).");
            else if (rear - front > 8) res[2].Notes.Add($"Rears run {rear - front:0}°C hotter than fronts — rear sliding / wheelspin (oversteer tendency).");
        }
        return res;
    }

    static float Avg(IEnumerable<float> v)
    {
        var l = v.Where(float.IsFinite).ToList();
        return l.Count > 0 ? l.Average() : float.NaN;
    }
}
