using System.Buffers.Binary;
using System.Text;

namespace RacingHelper.Sim;

public enum VarType { Char = 0, Bool = 1, Int = 2, BitField = 3, Float = 4, Double = 5 }

public sealed record VarHeader(string Name, VarType Type, int Offset, int Count, string Unit, string Desc)
{
    public int ElementSize => Type switch { VarType.Char or VarType.Bool => 1, VarType.Double => 8, _ => 4 };
}

/// <summary>Layout of the shared irsdk header used by both live memory and .ibt files.</summary>
public sealed class IrsdkHeader
{
    public const int Size = 112;
    public const int VarHeaderSize = 144;
    public const int DiskSubHeaderSize = 32;

    public int Ver, Status, TickRate, SessionInfoUpdate, SessionInfoLen, SessionInfoOffset;
    public int NumVars, VarHeaderOffset, NumBuf, BufLen;
    public readonly int[] BufTick = new int[4];
    public readonly int[] BufOffset = new int[4];

    public bool Connected => (Status & 1) != 0;

    public static IrsdkHeader Parse(ReadOnlySpan<byte> b)
    {
        var h = new IrsdkHeader
        {
            Ver = I32(b, 0), Status = I32(b, 4), TickRate = I32(b, 8),
            SessionInfoUpdate = I32(b, 12), SessionInfoLen = I32(b, 16), SessionInfoOffset = I32(b, 20),
            NumVars = I32(b, 24), VarHeaderOffset = I32(b, 28), NumBuf = I32(b, 32), BufLen = I32(b, 36),
        };
        for (int i = 0; i < 4; i++)
        {
            h.BufTick[i] = I32(b, 48 + i * 16);
            h.BufOffset[i] = I32(b, 48 + i * 16 + 4);
        }
        return h;
    }

    public int LatestBuffer()
    {
        int best = 0;
        for (int i = 1; i < Math.Clamp(NumBuf, 1, 4); i++)
            if (BufTick[i] > BufTick[best]) best = i;
        return best;
    }

    static int I32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
}

public sealed class VarMap
{
    readonly Dictionary<string, VarHeader> _byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<VarHeader> All => _byName.Values;

    public VarHeader? Get(string name) => _byName.TryGetValue(name, out var v) ? v : null;
    public bool Has(string name) => _byName.ContainsKey(name);

    public static VarMap Parse(ReadOnlySpan<byte> headers, int numVars)
    {
        var map = new VarMap();
        for (int i = 0; i < numVars; i++)
        {
            var h = headers.Slice(i * IrsdkHeader.VarHeaderSize, IrsdkHeader.VarHeaderSize);
            var type = (VarType)BinaryPrimitives.ReadInt32LittleEndian(h);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
            string name = CStr(h.Slice(16, 32));
            string desc = CStr(h.Slice(48, 64));
            string unit = CStr(h.Slice(112, 32));
            if (name.Length > 0) map._byName[name] = new VarHeader(name, type, offset, Math.Max(1, count), unit, desc);
        }
        return map;
    }

    static string CStr(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(n < 0 ? s : s[..n]).Trim();
    }

    public static double Read(ReadOnlySpan<byte> buf, VarHeader v, int index = 0)
    {
        int o = v.Offset + index * v.ElementSize;
        if (o < 0 || o + v.ElementSize > buf.Length) return double.NaN;
        return v.Type switch
        {
            VarType.Float => BinaryPrimitives.ReadSingleLittleEndian(buf[o..]),
            VarType.Double => BinaryPrimitives.ReadDoubleLittleEndian(buf[o..]),
            VarType.Int => BinaryPrimitives.ReadInt32LittleEndian(buf[o..]),
            VarType.BitField => BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]),
            _ => buf[o],
        };
    }
}

/// <summary>Values from irsdk enums used across the app.</summary>
public static class Irsdk
{
    static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, true);

    /// <summary>
    /// The session YAML claims ISO-8859-1 but names arrive UTF-8 encoded in practice; decode as UTF-8 and
    /// fall back to Latin-1 for older files with genuine single-byte accents.
    /// </summary>
    public static string DecodeYaml(ReadOnlySpan<byte> bytes)
    {
        int z = bytes.IndexOf((byte)0);
        if (z >= 0) bytes = bytes[..z];
        try { return StrictUtf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.Latin1.GetString(bytes); }
    }

    public static class TrkLoc { public const int NotInWorld = -1, OffTrack = 0, InPitStall = 1, ApproachingPits = 2, OnTrack = 3; }
    public static class SessionState { public const int Invalid = 0, GetInCar = 1, Warmup = 2, ParadeLaps = 3, Racing = 4, Checkered = 5, CoolDown = 6; }

    [Flags]
    public enum Flags : uint
    {
        Checkered = 0x1, White = 0x2, Green = 0x4, Yellow = 0x8, Red = 0x10, Blue = 0x20, Debris = 0x40, Crossed = 0x80,
        YellowWaving = 0x100, OneLapToGreen = 0x200, GreenHeld = 0x400, TenToGo = 0x800, FiveToGo = 0x1000,
        RandomWaving = 0x2000, Caution = 0x4000, CautionWaving = 0x8000,
        Black = 0x10000, Disqualify = 0x20000, Servicible = 0x40000, Furled = 0x80000, Repair = 0x100000,
    }

    [Flags]
    public enum EngineWarnings : uint
    {
        WaterTemp = 0x01, FuelPressure = 0x02, OilPressure = 0x04, EngineStalled = 0x08, PitSpeedLimiter = 0x10,
        RevLimiterActive = 0x20, OilTemp = 0x40,
    }

    public static class LeftRight { public const int Off = 0, Clear = 1, CarLeft = 2, CarRight = 3, CarsBothSides = 4, TwoCarsLeft = 5, TwoCarsRight = 6; }

    public static string SkiesName(int s) => s switch { 0 => "Clear", 1 => "Partly Cloudy", 2 => "Mostly Cloudy", 3 => "Overcast", _ => "—" };

    public static string WetnessName(int w) => w switch
    {
        1 => "Dry", 2 => "Mostly Dry", 3 => "Very Lightly Wet", 4 => "Lightly Wet", 5 => "Moderately Wet", 6 => "Very Wet", 7 => "Extremely Wet", _ => "—",
    };
}
