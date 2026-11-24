using BackupForge.Core.Abstractions;

namespace BackupForge.Engine.Files;

public enum CompareVerdict { Same, Different, SameWithDstShift, NeedsContentCheck }

/// <summary>size_mtime comparison (SPEC 6.2): 2 s tolerance, exactly ±1 h (DST / FAT local time) with equal size counts as same.</summary>
public static class Comparison
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DstShift = TimeSpan.FromHours(1);

    public static CompareVerdict Compare(FsEntry source, FsEntry target, bool contentMode)
    {
        if (source.Length != target.Length) return CompareVerdict.Different;
        var diff = (source.LastWriteUtc - target.LastWriteUtc).Duration();
        if (diff <= Tolerance) return CompareVerdict.Same;
        if ((diff - DstShift).Duration() <= Tolerance) return CompareVerdict.SameWithDstShift;
        return contentMode ? CompareVerdict.NeedsContentCheck : CompareVerdict.Different;
    }

    public static ulong Hash(IFileSystem fs, string path, CancellationToken ct)
    {
        var h = new System.IO.Hashing.XxHash64();
        using var s = fs.OpenRead(path);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            h.Append(buffer.AsSpan(0, n));
        }
        return h.GetCurrentHashAsUInt64();
    }
}
