using System.Security.Cryptography;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.TestKit;

namespace BackupForge.Engine.Files.Tests;

/// <summary>CR-001 1b.: even with a bad (e.g. hand-edited, unvalidated) configuration a mirror never deletes another job's folders.</summary>
public class CrossJobSafetyTests
{
    private static string Fingerprint(string dir) =>
        string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public async Task Root_mirror_leaves_foreign_mirror_versions_and_repository_untouched()
    {
        using var h = new EngineHarness();
        // job "docs" mirrors into the target ROOT, its Versions elsewhere
        h.Job.Target!.Subdir = "";
        h.Job.Files!.Versioning.Folder = h.Dir.Sub("docs_versions");
        h.Job.Files.DeleteGuardPct = 90; // a big job: the foreign data stays below the guard
        for (var i = 0; i < 300; i++) h.Write($"own{i:000}.txt", "own");
        // job "b" writes to <root>\Company (+ Company_Versions); an unused local restic repository at <root>\restic
        h.Config.Jobs.Add(new JobConfig
        {
            Id = "b", Name = "b", Type = JobTypes.Files, Sources = [new SourceConfig { Path = h.Dir.Sub("b-src") }],
            Target = new JobTargetRef { TargetId = "t", Subdir = "Company" }, Files = new FilesOptions(),
        });
        h.Config.ResticRepos.Add(new ResticRepoConfig { Id = "r", Location = Path.Combine(h.TargetRoot, "restic"), PasswordRef = "p" });
        foreach (var (dir, n) in new[] { ("Company", 200), ("Company_Versions", 100), ("restic", 300) })
            for (var i = 0; i < n; i++) h.Write(Path.Combine(dir, "d", $"f{i:000}.dat"), dir + i, root: h.TargetRoot);
        h.Write(@"Company\half.doc.bfpart", "b is copying", root: h.TargetRoot);
        h.Write("stray.txt", "really extra", root: h.TargetRoot);
        var before = new[] { "Company", "Company_Versions", "restic" }.ToDictionary(d => d, d => Fingerprint(Path.Combine(h.TargetRoot, d)));

        var o = await h.RunAsync();

        Assert.Equal(RunResults.Warning, o.Result);
        Assert.Contains(o.Items, i => i.Message.Contains("another job", StringComparison.Ordinal));
        foreach (var (d, fp) in before) Assert.Equal(fp, Fingerprint(Path.Combine(h.TargetRoot, d)));
        Assert.True(File.Exists(Path.Combine(h.TargetRoot, "Company", "half.doc.bfpart"))); // CleanupParts did not enter b's folder
        Assert.False(File.Exists(Path.Combine(h.TargetRoot, "stray.txt")));                // own extras are still mirrored away
        Assert.Equal(1, o.FilesDeleted);
        Assert.Equal(300, o.FilesCopied);
    }

    [Fact]
    public void Validator_rejects_the_same_configuration()
    {
        using var h = new EngineHarness();
        h.Job.Target!.Subdir = "";
        h.Job.Files!.Versioning.Folder = h.Dir.Sub("docs_versions");
        h.Config.ResticRepos.Add(new ResticRepoConfig { Id = "r", Location = Path.Combine(h.TargetRoot, "restic"), PasswordRef = "p" });
        Assert.True(ConfigValidator.Validate(h.Config, null, h.Dir.Sub("data")).Has("path.overlap_job", "jobs[0].target"));
    }
}
