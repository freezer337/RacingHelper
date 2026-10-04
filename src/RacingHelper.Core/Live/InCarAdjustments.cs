using System.Globalization;
using System.Text.RegularExpressions;

namespace RacingHelper.Live;

/// <summary>An in-car adjustment iRacing exposes as a live "dc" variable (the driver changes it; we only advise).</summary>
public sealed record Adjustable(string Id, string Label, string Spoken, string Var, string Param);

/// <summary>A change to tell the driver, e.g. TC +1 or brake bias −0.5.</summary>
public sealed record AdjustAdvice(Adjustable Adj, int Dir, float Amount, bool ByValue, string Reason)
{
    /// <summary>"increase TC by 1", "move brake bias back 0.5", "rear bar softer by 1".</summary>
    public string Words => Adj.Id switch
    {
        "bb" => $"move brake bias {(Dir > 0 ? "forward" : "back")} {InCarAdjustments.Num(Amount)}",
        "arbf" or "arbr" => $"{Adj.Spoken} {(Dir > 0 ? "stiffer" : "softer")} by {InCarAdjustments.Num(Amount)}",
        _ => $"{(Dir > 0 ? "increase" : "reduce")} {Adj.Spoken} by {InCarAdjustments.Num(Amount)}",
    };
}

public static class InCarAdjustments
{
    public static readonly Adjustable[] All =
    {
        new("bb", "Brake bias", "brake bias", "dcBrakeBias", "Brake bias"),
        new("tc", "Traction control", "TC", "dcTractionControl", "Traction control"),
        new("abs", "ABS", "ABS", "dcABS", "ABS"),
        new("arbf", "Front anti-roll bar", "front bar", "dcAntiRollFront", "Front anti-roll bar"),
        new("arbr", "Rear anti-roll bar", "rear bar", "dcAntiRollRear", "Rear anti-roll bar"),
    };

    public static Adjustable? ForParam(string param) => All.FirstOrDefault(a => a.Param == param);
    public static Adjustable Get(string id) => All.First(a => a.Id == id);

    /// <summary>Direction and amount from an optimiser action ("Move rearward 0.5–1%", "Increase TC 1 step", "Soften one step").</summary>
    public static AdjustAdvice? FromAction(Adjustable a, string action, string reason)
    {
        var s = action.ToLowerInvariant();
        int dir = s.Contains("rearward") || s.Contains("soften") || s.Contains("reduce") || s.Contains("decrease") || s.Contains("less") ? -1
                : s.Contains("forward") || s.Contains("stiffen") || s.Contains("increase") || s.Contains("more") || s.Contains("add") ? 1 : 0;
        if (dir == 0) return null;
        if (a.Id == "bb")
        {
            var m = Regex.Match(s, @"(\d+(?:\.\d+)?)");
            float amt = m.Success ? float.Parse(m.Value, CultureInfo.InvariantCulture) : 0.5f;
            return new AdjustAdvice(a, dir, Math.Clamp(amt, 0.1f, 2f), true, reason);
        }
        var n = Regex.Match(s, @"(\d+)\s*(step|click)");
        return new AdjustAdvice(a, dir, n.Success ? int.Parse(n.Groups[1].Value) : 1, false, reason);
    }

    /// <summary>One sentence for several changes: "increase TC by 1, and move brake bias back 0.5".</summary>
    public static string Join(IReadOnlyList<AdjustAdvice> list)
    {
        var w = list.Select(x => x.Words).ToList();
        return w.Count <= 1 ? string.Join("", w) : string.Join(", ", w.Take(w.Count - 1)) + ", and " + w[^1];
    }

    public static string Num(float v) => v.ToString(Math.Abs(v - MathF.Round(v)) < 1e-3f ? "0" : "0.0#", CultureInfo.InvariantCulture);
}
