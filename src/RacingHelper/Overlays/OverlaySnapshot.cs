using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RacingHelper.Analysis;
using RacingHelper.Live;
using RacingHelper.Storage;

namespace RacingHelper.App.Overlays;

/// <summary>
/// Diagnostic: replays an .ibt for a while, then renders every overlay to PNG
/// (RacingHelper.exe --render-overlays &lt;outDir&gt; &lt;file.ibt&gt; &lt;db&gt; [seconds]).
/// </summary>
public static class OverlaySnapshot
{
    public static async Task Run(string outDir, string ibt, string dbPath, double seconds)
    {
        Directory.CreateDirectory(outDir);
        var settings = new SettingsStore(Path.Combine(outDir, "settings.json"));
        settings.Current.AutoInstallSetups = false;
        settings.Current.AutoStartDiskTelemetry = false;
        var store = new SessionStore(new Database(dbPath));
        var hub = new TelemetryHub(settings, store, new AnalysisService(store));
        hub.Start();
        hub.StartReplay(ibt, 12);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var state = hub.State;
        InjectFakeField(hub, state);

        foreach (var info in RacingHelper.Web.OverlayCatalog.All)
        {
            var make = typeof(OverlayManager).GetField("Factory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null) as Dictionary<string, Func<OverlayView>>;
            var view = make![info.Id]();
            view.Ctx = new OverlayContext { Settings = settings.Current, Hub = hub, Opacity = 0.92 };
            view.State = state;
            view.Width = view.BaseWidth; view.Height = view.BaseHeight;
            // render on a "game-like" backdrop so translucency is visible
            var host = new System.Windows.Controls.Grid { Width = view.BaseWidth + 20, Height = view.BaseHeight + 20, Background = new SolidColorBrush(Color.FromRgb(70, 90, 110)) };
            view.Margin = new Thickness(10);
            host.Children.Add(view);
            host.Measure(new Size(host.Width, host.Height));
            host.Arrange(new Rect(0, 0, host.Width, host.Height));
            host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)(host.Width * 1.5), (int)(host.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(outDir, info.Id + ".png"));
            enc.Save(fs);
        }
        File.WriteAllText(Path.Combine(outDir, "state.txt"), $"lap={state.Lap} pct={state.LapPct:0.00} delta={state.Delta:0.000} ref={state.ReferenceLabel} corners={state.LapCorners.Count} status={state.Status}\n"
            + string.Join("\n", state.Standings.Take(30).Select(r => $"P{r.Position}/{r.ClassPosition} #{r.Number} {r.Name} ir={r.IRating} d={r.IRatingChange:0} gap={r.Gap:0.0} int={r.Interval:0.0} rel={r.Relative:0.0} lapdiff={r.LapDiff} pit={r.InPit}"))
            + "\nRELATIVE:\n" + string.Join("\n", state.Relative.Select(r => $"#{r.Number} {r.Name} rel={r.Relative:0.00} player={r.IsPlayer}"))
            + "\nRADAR: " + string.Join(", ", state.Radar.Select(r => $"{r.Dist:0}m")) + $"\nSOF: {string.Join(",", state.ClassSof.Select(kv => kv.Key + "=" + kv.Value))}");
        hub.Dispose();
    }

    /// <summary>.ibt files carry no other-car data, so fabricate a plausible field from the session's real entry list.</summary>
    static void InjectFakeField(TelemetryHub hub, LiveState state)
    {
        var si = hub.Info;
        if (si == null) return;
        var f = new RacingHelper.Sim.Frame { HasCarIdx = true, SessionNum = si.CurrentSessionNum, LapDistPct = state.LapPct, CarLeftRight = RacingHelper.Sim.Irsdk.LeftRight.CarLeft };
        var rnd = new Random(7);
        var cars = si.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator).ToList();
        int pos = 1;
        foreach (var d in cars.OrderBy(_ => rnd.Next()))
        {
            int i = d.CarIdx;
            bool me = i == si.PlayerCarIdx;
            float pct = me ? state.LapPct : (float)((state.LapPct + (pos - cars.Count / 2) * 0.012 + 1) % 1);
            f.CarIdxLapDistPct[i] = pct;
            f.CarIdxEstTime[i] = pct * 85f;
            f.CarIdxLap[i] = 10 + (pos < 3 ? 1 : 0);
            f.CarIdxPosition[i] = pos;
            f.CarIdxClassPosition[i] = pos;
            f.CarIdxF2Time[i] = (pos - 1) * 1.7f;
            f.CarIdxLastLapTime[i] = 85.5f + (float)rnd.NextDouble() * 2;
            f.CarIdxBestLapTime[i] = 85.2f + pos * 0.08f;
            f.CarIdxTrackSurface[i] = pos == 7 ? RacingHelper.Sim.Irsdk.TrkLoc.InPitStall : RacingHelper.Sim.Irsdk.TrkLoc.OnTrack;
            f.CarIdxOnPitRoad[i] = pos == 7;
            f.CarIdxTireCompound[i] = 0;
            pos++;
        }
        // two cars close enough for the radar: one alongside-ish, one just behind
        var near = cars.Where(d => d.CarIdx != si.PlayerCarIdx).Take(2).ToList();
        if (near.Count == 2 && si.TrackLengthM > 0)
        {
            f.CarIdxLapDistPct[near[0].CarIdx] = (state.LapPct + 3f / si.TrackLengthM) % 1;
            f.CarIdxLapDistPct[near[1].CarIdx] = (state.LapPct - 14f / si.TrackLengthM + 1) % 1;
            f.CarIdxTrackSurface[near[0].CarIdx] = f.CarIdxTrackSurface[near[1].CarIdx] = RacingHelper.Sim.Irsdk.TrkLoc.OnTrack;
            f.CarIdxOnPitRoad[near[0].CarIdx] = f.CarIdxOnPitRoad[near[1].CarIdx] = false;
        }
        // race session so gaps / iRating projections are exercised
        var raceNum = si.Sessions.FirstOrDefault(s => s.IsRace)?.Num ?? si.CurrentSessionNum;
        f.SessionNum = raceNum;
        typeof(TelemetryHub).GetMethod("ComputeField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(hub, new object[] { f, si });
        T Get<T>(string field) => (T)typeof(TelemetryHub).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hub)!;
        state.Standings = Get<List<CarRow>>("_standings");
        state.Relative = Get<List<CarRow>>("_relative");
        state.Radar = Get<List<RadarCar>>("_radar");
        state.ClassSof = Get<Dictionary<string, int>>("_sof");
        state.MapCars = Get<List<MapCar>>("_mapCars");
        state.CarLeftRight = f.CarLeftRight;
        state.SessionType = "Race";
        state.CarsInClass = cars.Count;
        state.ClassPosition = f.CarIdxClassPosition[si.PlayerCarIdx];
        state.Position = state.ClassPosition;
    }
}
