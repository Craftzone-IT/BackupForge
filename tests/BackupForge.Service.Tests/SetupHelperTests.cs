using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

public class SetupHelperTests
{
    [Fact]
    public void Install_validates_and_places_config_uninstall_purge_removes_only_data_dir()
    {
        using var d = new TempDir();
        var paths = new DataPaths(d.Sub("data"));
        var target = d.Sub("backup-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "backup");
        var cfg = TestConfigs.Jobs(1);
        cfg.Targets[0].Path = target;
        var cfgFile = d.Sub("acme.json");
        File.WriteAllText(cfgFile, ConfigJson.Serialize(cfg));
        var o = new StringWriter();
        Assert.Equal(0, SetupHelper.Run(["--setup-install", "--config", cfgFile, "--log", d.Sub("setup.log")], paths, o));
        Assert.Equal("SRV01", ConfigJson.Load(paths.ConfigFile).InstanceName);
        Assert.True(File.Exists(d.Sub("setup.log")));

        var bad = TestConfigs.Jobs(1);
        bad.Jobs[0].Target!.TargetId = "missing";
        File.WriteAllText(cfgFile, ConfigJson.Serialize(bad));
        Assert.Equal(1, SetupHelper.Run(["--setup-install", "--config", cfgFile], paths, o));
        Assert.Contains("missing", o.ToString(), StringComparison.Ordinal);
        Assert.Equal("t", ConfigJson.Load(paths.ConfigFile).Jobs[0].Target!.TargetId); // the valid one stays

        var secrets = d.Sub("acme.secrets.json");
        File.WriteAllText(secrets, "{\"target:nas1\":\"test-password-123\"}");
        Assert.Equal(0, SetupHelper.Run(["--setup-install", "--secrets", secrets], paths, o));
        Assert.True(File.Exists(secrets)); // never deleted by the installer, only warned
        Assert.Contains("delete", o.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, SetupHelper.Run(["--setup-uninstall"], paths, o));
        Assert.True(Directory.Exists(paths.Root));
        Assert.Equal(0, SetupHelper.Run(["--setup-uninstall", "--purge"], paths, o));
        Assert.False(Directory.Exists(paths.Root));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Purge_only_deletes_a_recognisable_data_folder()
    {
        using var d = new TempDir();
        var paths = new DataPaths(d.Sub("not-ours"));
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(Path.Combine(paths.Root, "important.docx"), "customer data");
        var o = new StringWriter();
        Assert.Equal(0, SetupHelper.Run(["--setup-uninstall", "--purge"], paths, o));
        Assert.True(File.Exists(Path.Combine(paths.Root, "important.docx")));
        Assert.Contains("not a BackupForge data folder", o.ToString(), StringComparison.Ordinal);

        File.WriteAllText(paths.HistoryDb, "");
        Assert.Equal(0, SetupHelper.Run(["--setup-uninstall", "--purge"], paths, o));
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void Install_paths_ignore_the_data_override()
    {
        var old = Environment.GetEnvironmentVariable(DataPaths.DataDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DataPaths.DataDirEnvVar, Path.Combine(Path.GetTempPath(), "elsewhere"));
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupForge");
            Assert.Equal(expected, SetupHelper.InstallPaths().Root);
            Assert.NotEqual(expected, DataPaths.Default().Root);
        }
        finally { Environment.SetEnvironmentVariable(DataPaths.DataDirEnvVar, old); }
    }
}
