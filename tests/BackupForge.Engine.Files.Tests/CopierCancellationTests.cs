using System.Diagnostics;
using BackupForge.Core.Abstractions;
using BackupForge.TestKit;

namespace BackupForge.Engine.Files.Tests;

/// <summary>CR-002 A3.: stopping during a big file copy returns within seconds and leaves nothing behind.</summary>
public class CopierCancellationTests
{
    [Fact]
    public void Cancel_during_a_256_MB_copy_returns_quickly_without_leftovers()
    {
        using var d = new TempDir();
        var src = d.Sub("big.bin");
        using (var f = File.Create(src)) f.SetLength(256L << 20);
        var dest = d.Sub("out", "big.bin");
        Directory.CreateDirectory(d.Sub("out"));
        var fs = PhysicalFileSystem.Instance;
        // 400 Mbit/s ≈ 50 MB/s: the copy would take ~5 s, the token fires after 200 ms (also exercises the throttle in the loop)
        var copier = new FileCopier(fs, new Throttle(400));
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        TimeSpan? cancelledAt = null;
        long seen = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => copier.Copy(fs.GetEntry(src)!, dest, null, n =>
        {
            seen += n;
            if (cancelledAt is null && sw.Elapsed >= TimeSpan.FromMilliseconds(200)) { cancelledAt = sw.Elapsed; cts.Cancel(); } // 200 ms into the copy
        }, cts.Token));
        sw.Stop();
        Assert.NotNull(cancelledAt);
        // from the cancellation to the return (incl. deleting the .bfpart) — opening the file before the copy is not counted
        Assert.True(sw.Elapsed - cancelledAt < TimeSpan.FromSeconds(2), $"returned {sw.Elapsed - cancelledAt} after the cancellation (total {sw.Elapsed})");
        Assert.True(seen > 0 && seen < 256L << 20, $"copied {seen}");
        Assert.False(File.Exists(dest + FileCopier.PartSuffix));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Chunked_copy_with_intermediate_flushes_is_byte_identical()
    {
        using var d = new TempDir();
        var src = d.Sub("data.bin");
        var bytes = new byte[(int)(FileCopier.FlushEveryBytes + FileCopier.ChunkBytes + 12345)];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(src, bytes);
        var dest = d.Sub("copy.bin");
        var fs = PhysicalFileSystem.Instance;
        Assert.Equal(bytes.Length, new FileCopier(fs, null).Copy(fs.GetEntry(src)!, dest, null, null, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(dest));
    }
}
