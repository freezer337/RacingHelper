using System.IO.Compression;
using System.Text;

namespace RacingHelper.Recording;

/// <summary>Channel names used for stored lap telemetry.</summary>
public static class Ch
{
    public const string T = "t", D = "d", Speed = "spd", Throttle = "thr", Brake = "brk", Clutch = "clu", Steer = "str", Gear = "gear", Rpm = "rpm";
    public const string LatG = "latg", LonG = "long", YawRate = "yawr", X = "x", Y = "y", Abs = "abs";
    public static readonly string[] WheelSpeed = { "wsLF", "wsRF", "wsLR", "wsRR" };

    public static double Scale(string ch) => ch switch
    {
        T => 1e-4, Gear or Abs or Rpm => 1, Steer or YawRate => 1e-4, _ => 1e-3,
    };
}

/// <summary>Time-based (sim tick rate) samples for one lap, stored per channel.</summary>
public sealed class LapData
{
    public readonly Dictionary<string, float[]> Channels = new();
    public int Count { get; private set; }

    public LapData(int count) { Count = count; }

    public float[] this[string name] => Channels[name];
    public bool Has(string name) => Channels.ContainsKey(name);
    public float[]? Get(string name) => Channels.TryGetValue(name, out var a) ? a : null;

    public void Set(string name, float[] values)
    {
        if (values.Length != Count) throw new ArgumentException($"channel {name} length {values.Length} != {Count}");
        Channels[name] = values;
    }

    // ---- compact binary encoding: quantised, delta + zigzag varint, then Brotli ----
    const int Magic = 0x314C4852; // "RHL1"

    public byte[] Serialize()
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, Encoding.ASCII, true))
        {
            w.Write(Magic);
            w.Write(Count);
            w.Write(Channels.Count);
            foreach (var (name, data) in Channels)
            {
                w.Write(name);
                double scale = Ch.Scale(name);
                w.Write(scale);
                long prev = 0;
                for (int i = 0; i < Count; i++)
                {
                    float v = data[i];
                    long q = float.IsFinite(v) ? (long)Math.Round(v / scale) : long.MinValue / 4;
                    WriteVarint(w, ZigZag(q - prev));
                    prev = q;
                }
            }
        }
        using var outMs = new MemoryStream();
        using (var br = new BrotliStream(outMs, CompressionLevel.Optimal, true)) raw.WriteTo(br);
        return outMs.ToArray();
    }

    public static LapData Deserialize(byte[] blob)
    {
        using var input = new BrotliStream(new MemoryStream(blob), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.ASCII);
        if (r.ReadInt32() != Magic) throw new InvalidDataException("bad lap blob");
        int count = r.ReadInt32();
        int nch = r.ReadInt32();
        var lap = new LapData(count);
        for (int c = 0; c < nch; c++)
        {
            string name = r.ReadString();
            double scale = r.ReadDouble();
            var arr = new float[count];
            long prev = 0;
            for (int i = 0; i < count; i++)
            {
                prev += UnZigZag(ReadVarint(r));
                arr[i] = prev == long.MinValue / 4 ? float.NaN : (float)(prev * scale);
            }
            lap.Channels[name] = arr;
        }
        return lap;
    }

    static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    static void WriteVarint(BinaryWriter w, ulong v)
    {
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }
    static ulong ReadVarint(BinaryReader r)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            byte b = r.ReadByte();
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
    }
}

public sealed class TyreLapStats
{
    // pressures kPa; temps C normalised to inner / middle / outer (independent of car side)
    public float PressAvg = float.NaN, PressMax = float.NaN, PressEnd = float.NaN, ColdPress = float.NaN;
    public float TempInAvg = float.NaN, TempMidAvg = float.NaN, TempOutAvg = float.NaN;
    public float TempInMax = float.NaN, TempMidMax = float.NaN, TempOutMax = float.NaN;
    public float CarcassIn = float.NaN, CarcassMid = float.NaN, CarcassOut = float.NaN;
    public float WearIn = float.NaN, WearMid = float.NaN, WearOut = float.NaN;
    public float RideHeightMin = float.NaN, RideHeightAvg = float.NaN;

    public float TempAvg => Avg3(TempInAvg, TempMidAvg, TempOutAvg);
    public float TempMax => MathF.Max(TempInMax, MathF.Max(TempMidMax, TempOutMax));
    static float Avg3(float a, float b, float c) => (a + b + c) / 3f;
}

/// <summary>A completed lap as produced by the recorder (live or .ibt import).</summary>
public sealed class RecordedLap
{
    public long Id, SessionId;
    public int LapNumber, Stint;
    public DateTime StartedAt;
    public double LapTime;
    public bool Valid, OutLap, InLap;
    public string InvalidReason = "";
    public int Incidents, OffTracks;
    public float FuelStart = float.NaN, FuelEnd = float.NaN, FuelUsed = float.NaN;
    public float AirTemp = float.NaN, TrackTemp = float.NaN;
    public int TrackWetness = -1;
    public float[] SectorTimes = Array.Empty<float>();
    public TyreLapStats[]? Tyres;
    public float BrakeBias = float.NaN, MaxSpeed;
    public int SetupVersion;
    public string SetupHash = "";
    public bool HasGps;
    public string Source = "live";
    public string? DriverName;
    public int TyreCompound = -1;
    public LapData? Data;
    public Dictionary<string, double> Metrics = new();
}
