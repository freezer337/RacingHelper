using System.Buffers.Binary;
using System.Text;

namespace RacingHelper.Sim;

/// <summary>Reader for iRacing disk telemetry (.ibt) files.</summary>
public sealed class IbtFile : IDisposable
{
    readonly FileStream _fs;
    public string Path { get; }
    public IrsdkHeader Header { get; }
    public VarMap Vars { get; }
    public string SessionYaml { get; }
    public DateTime SessionStartLocal { get; }
    public double StartTime { get; }
    public double EndTime { get; }
    public int LapCount { get; }
    public int RecordCount { get; }
    public int DataOffset => Header.BufOffset[0];

    IbtFile(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var head = new byte[IrsdkHeader.Size + IrsdkHeader.DiskSubHeaderSize];
        _fs.ReadExactly(head);
        Header = IrsdkHeader.Parse(head);
        var sub = head.AsSpan(IrsdkHeader.Size);
        long startUnix = BinaryPrimitives.ReadInt64LittleEndian(sub);
        StartTime = BinaryPrimitives.ReadDoubleLittleEndian(sub[8..]);
        EndTime = BinaryPrimitives.ReadDoubleLittleEndian(sub[16..]);
        LapCount = BinaryPrimitives.ReadInt32LittleEndian(sub[24..]);
        int rec = BinaryPrimitives.ReadInt32LittleEndian(sub[28..]);
        SessionStartLocal = startUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(startUnix).LocalDateTime : File.GetCreationTime(path);

        if (Header.NumVars <= 0 || Header.NumVars > 10000 || Header.BufLen <= 0) throw new InvalidDataException("Not a valid .ibt file");

        var vh = new byte[Header.NumVars * IrsdkHeader.VarHeaderSize];
        _fs.Position = Header.VarHeaderOffset;
        _fs.ReadExactly(vh);
        Vars = VarMap.Parse(vh, Header.NumVars);

        var yaml = new byte[Math.Max(0, Header.SessionInfoLen)];
        _fs.Position = Header.SessionInfoOffset;
        _fs.ReadExactly(yaml);
        SessionYaml = Irsdk.DecodeYaml(yaml);

        // iRacing leaves the record count at 0 if the file was not closed cleanly; derive from size instead.
        long available = (_fs.Length - DataOffset) / Header.BufLen;
        RecordCount = rec > 0 ? (int)Math.Min(rec, available) : (int)available;
    }

    public static IbtFile Open(string path) => new(path);

    /// <summary>Streams every record into the same reusable buffer.</summary>
    public IEnumerable<int> Records(byte[] buffer, int start = 0)
    {
        if (buffer.Length < Header.BufLen) throw new ArgumentException("buffer too small");
        _fs.Position = DataOffset + (long)start * Header.BufLen;
        for (int i = start; i < RecordCount; i++)
        {
            int read = 0;
            while (read < Header.BufLen)
            {
                int n = _fs.Read(buffer, read, Header.BufLen - read);
                if (n <= 0) yield break;
                read += n;
            }
            yield return i;
        }
    }

    public void Dispose() => _fs.Dispose();
}
