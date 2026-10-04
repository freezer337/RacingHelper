using System.Globalization;

namespace RacingHelper.Sim;

/// <summary>
/// Minimal, tolerant YAML reader for the iRacing session-info string.
/// iRacing's YAML is block-style only (maps, "- " lists, scalars) but contains unescaped values
/// (team names with colons etc.) that trip strict parsers, so we parse it by indentation ourselves.
/// </summary>
public sealed class YNode
{
    public string? Value;
    public List<KeyValuePair<string, YNode>>? Map;
    public List<YNode>? List;

    public static readonly YNode Empty = new() { Value = "" };

    public bool IsMap => Map != null;
    public bool IsList => List != null;

    public YNode? this[string key]
    {
        get
        {
            if (Map == null) return null;
            foreach (var kv in Map) if (kv.Key == key) return kv.Value;
            return null;
        }
    }

    public YNode? this[int i] => List != null && i >= 0 && i < List.Count ? List[i] : null;

    public IEnumerable<YNode> Items => List ?? (IEnumerable<YNode>)Array.Empty<YNode>();

    /// <summary>Dotted path lookup, e.g. "WeekendInfo.TrackName".</summary>
    public YNode? Path(string path)
    {
        YNode? n = this;
        foreach (var part in path.Split('.'))
        {
            if (n == null) return null;
            n = n[part];
        }
        return n;
    }

    public string Str(string path, string def = "") => Path(path)?.Value ?? def;

    /// <summary>Parses the leading number of a scalar such as "4.2834 km" or "124 kPa".</summary>
    public double Num(string path, double def = double.NaN) => ParseNum(Path(path)?.Value, def);

    public int Int(string path, int def = 0)
    {
        var d = Num(path, double.NaN);
        return double.IsNaN(d) ? def : (int)d;
    }

    public static double ParseNum(string? s, double def = double.NaN)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim();
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or '-' or '+' || (end > 0 && (s[end] is 'e' or 'E') && end + 1 < s.Length && (char.IsDigit(s[end + 1]) || s[end + 1] is '-' or '+'))))
            end++;
        if (end == 0) return def;
        return double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
    }

    public static YNode Parse(string text)
    {
        var lines = new List<(int indent, string text)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t', '\0');
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart(' ');
            if (trimmed.StartsWith("---") || trimmed == "...") continue;
            lines.Add((line.Length - trimmed.Length, trimmed));
        }
        int idx = 0;
        if (lines.Count == 0) return new YNode { Map = new() };
        return ParseMap(lines, ref idx, lines[0].indent, null);
    }

    static bool IsListItem(string t) => t == "-" || t.StartsWith("- ");

    static YNode ParseNode(List<(int indent, string text)> lines, ref int idx, int indent)
        => IsListItem(lines[idx].text) ? ParseList(lines, ref idx, indent) : ParseMap(lines, ref idx, indent, null);

    static YNode ParseMap(List<(int indent, string text)> lines, ref int idx, int indent, string? firstLine)
    {
        var node = new YNode { Map = new() };
        bool first = firstLine != null;
        while (idx < lines.Count)
        {
            string txt;
            if (first) { txt = firstLine!; first = false; }
            else
            {
                var (ind, t) = lines[idx];
                if (ind < indent) break;
                if (ind > indent) { idx++; continue; } // stray continuation line; ignore
                if (IsListItem(t)) break;
                txt = t;
            }
            idx++;

            string key, val;
            int c = txt.IndexOf(": ", StringComparison.Ordinal);
            if (c < 0)
            {
                key = txt.EndsWith(':') ? txt[..^1] : txt;
                val = "";
            }
            else
            {
                key = txt[..c];
                val = txt[(c + 2)..].Trim();
            }

            YNode child;
            if (val.Length == 0)
            {
                if (idx < lines.Count && lines[idx].indent > indent) child = ParseNode(lines, ref idx, lines[idx].indent);
                else if (idx < lines.Count && lines[idx].indent == indent && IsListItem(lines[idx].text)) child = ParseList(lines, ref idx, indent);
                else child = new YNode { Value = "" };
            }
            else child = new YNode { Value = Unquote(val) };
            node.Map!.Add(new(key.Trim(), child));
        }
        return node;
    }

    static YNode ParseList(List<(int indent, string text)> lines, ref int idx, int indent)
    {
        var node = new YNode { List = new() };
        while (idx < lines.Count && lines[idx].indent == indent && IsListItem(lines[idx].text))
        {
            var t = lines[idx].text;
            var content = t.Length > 1 ? t[1..].TrimStart(' ') : "";
            int contentIndent = indent + (t.Length - content.Length);
            if (content.Contains(": ") || content.EndsWith(':'))
                node.List.Add(ParseMap(lines, ref idx, contentIndent, content));
            else
            {
                idx++;
                node.List.Add(new YNode { Value = Unquote(content) });
            }
        }
        return node;
    }

    static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) return v[1..^1];
        return v;
    }

    /// <summary>Flattens a subtree to "a.b.c" → value pairs (used for setup diffs).</summary>
    public void Flatten(string prefix, List<KeyValuePair<string, string>> into)
    {
        if (Map != null)
            foreach (var kv in Map) kv.Value.Flatten(prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, into);
        else if (List != null)
            for (int i = 0; i < List.Count; i++) List[i].Flatten($"{prefix}[{i}]", into);
        else into.Add(new(prefix, Value ?? ""));
    }
}
