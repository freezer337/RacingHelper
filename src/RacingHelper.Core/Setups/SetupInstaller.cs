using System.Text.RegularExpressions;

namespace RacingHelper.Setups;

public sealed class SetupFile
{
    public string Path { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string CarPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "other";     // race / quali / wet / other
    public DateTime Modified { get; set; }
    public long Size { get; set; }
    public bool Installed { get; set; }
}

public sealed class InstallResult
{
    public string CarPath { get; set; } = "";
    public string TargetFolder { get; set; } = "";
    public List<string> Installed { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Keeps a personal setup library (Documents\RacingHelper\SetupLibrary\&lt;car folder&gt;\...\*.sto)
/// and copies the right setups into iRacing's setups folder — automatically when you join a session.
/// </summary>
public sealed class SetupInstaller
{
    public const string TargetSubfolder = "RacingHelper";
    readonly Func<AppSettings> _settings;

    public SetupInstaller(Func<AppSettings> settings) { _settings = settings; }

    public List<SetupFile> Scan()
    {
        var s = _settings();
        var list = new List<SetupFile>();
        if (!Directory.Exists(s.SetupLibraryFolder)) return list;
        var knownCars = Directory.Exists(s.IRacingSetupsFolder)
            ? new HashSet<string>(Directory.GetDirectories(s.IRacingSetupsFolder).Select(System.IO.Path.GetFileName)!, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(s.SetupLibraryFolder, "*.sto", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(s.SetupLibraryFolder, file);
            var parts = rel.Split(System.IO.Path.DirectorySeparatorChar);
            // the car is the first folder in the path that iRacing knows as a car folder (falls back to the top folder)
            string car = parts.Take(parts.Length - 1).FirstOrDefault(p => knownCars.Contains(p)) ?? (parts.Length > 1 ? parts[0] : "");
            if (car.Length == 0) continue;
            var fi = new FileInfo(file);
            var sf = new SetupFile
            {
                Path = file, RelativePath = rel, CarPath = car, Name = fi.Name, Modified = fi.LastWriteTime, Size = fi.Length,
                Type = Classify(rel),
            };
            var target = TargetPath(sf);
            sf.Installed = File.Exists(target) && new FileInfo(target).Length == sf.Size;
            list.Add(sf);
        }
        return list.OrderBy(x => x.CarPath).ThenBy(x => x.RelativePath).ToList();
    }

    public static string Classify(string path)
    {
        var p = path.ToLowerInvariant();
        if (Regex.IsMatch(p, @"(wet|rain)")) return "wet";
        if (Regex.IsMatch(p, @"(quali|qualy|qual\b|_q\b|\bq\b|hotlap|hot lap)")) return "quali";
        if (Regex.IsMatch(p, @"(race|_r\b|\br\b|endurance|sprint)")) return "race";
        return "other";
    }

    string TargetPath(SetupFile f)
    {
        var s = _settings();
        // keep the library's sub-structure below the car folder so iRacing shows the same grouping
        var rel = f.RelativePath;
        int carIdx = rel.IndexOf(f.CarPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var below = carIdx >= 0 ? rel[(carIdx + f.CarPath.Length + 1)..] : f.Name;
        return System.IO.Path.Combine(s.IRacingSetupsFolder, f.CarPath, TargetSubfolder, below);
    }

    /// <summary>Installs library setups for one car, applying the type / version / track filters.</summary>
    public InstallResult Install(string carPath, string trackName = "", string trackDisplay = "", bool ignoreFilters = false)
    {
        var s = _settings();
        var res = new InstallResult { CarPath = carPath, TargetFolder = System.IO.Path.Combine(s.IRacingSetupsFolder, carPath, TargetSubfolder) };
        try
        {
            var files = Scan().Where(f => f.CarPath.Equals(carPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!ignoreFilters)
            {
                if (s.SetupTypeFilter != "all") files = files.Where(f => f.Type == s.SetupTypeFilter || f.Type == "other").ToList();
                if (s.SetupMatchTrack && trackName.Length > 0)
                {
                    var tokens = TrackTokens(trackName, trackDisplay);
                    var matching = files.Where(f => tokens.Any(t => f.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (matching.Count > 0) files = matching; // no track-specific setups → keep the generic ones
                }
                if (s.SetupLatestOnly)
                    files = files.GroupBy(f => BaseName(f.RelativePath)).Select(g => g.OrderByDescending(f => f.Modified).First()).ToList();
            }
            foreach (var f in files)
            {
                var target = TargetPath(f);
                if (File.Exists(target) && new FileInfo(target).Length == f.Size && File.GetLastWriteTime(target) >= f.Modified)
                {
                    res.Skipped.Add(f.RelativePath);
                    continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(f.Path, target, true);
                res.Installed.Add(f.RelativePath);
            }
        }
        catch (Exception e) { res.Error = e.Message; }
        return res;
    }

    /// <summary>Copies a file the user picked into the library under the given car folder.</summary>
    public string AddToLibrary(string sourceFile, string carPath)
    {
        var s = _settings();
        var dir = System.IO.Path.Combine(s.SetupLibraryFolder, carPath);
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(sourceFile));
        File.Copy(sourceFile, target, true);
        return target;
    }

    static string BaseName(string rel)
    {
        var name = System.IO.Path.ChangeExtension(rel, null);
        // strip version-ish suffixes: _v2, v1.3, 2024S3, (1), -new
        name = Regex.Replace(name, @"([ _\-]?v\d+(\.\d+)*|[ _\-]?\d{2,4}\s?s\d|\(\d+\)|[ _\-]new)$", "", RegexOptions.IgnoreCase);
        return name.ToLowerInvariant();
    }

    static List<string> TrackTokens(string trackName, string display)
    {
        var tokens = new List<string>();
        foreach (var part in (trackName + " " + display).Split(' ', '_', '-', '(', ')'))
            if (part.Length >= 4 && !new[] { "circuit", "raceway", "international", "speedway", "full", "course", "grand", "prix" }.Contains(part.ToLowerInvariant()))
                tokens.Add(part);
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
