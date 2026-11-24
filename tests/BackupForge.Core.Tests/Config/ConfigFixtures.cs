using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Tests.Infra;
using BackupForge.TestKit;

namespace BackupForge.Core.Tests.Config;

public static class ConfigFixtures
{
    public static string ExampleJson => File.ReadAllText(TestPaths.Example("config.example.json"));

    public static BackupConfig Example() => ConfigJson.Parse(ExampleJson);

    public static InMemorySecretStore ExampleSecrets() => new([
        new("target:nas1", "test-password-123"),
        new("restic:offsite", "test-restic-pw-456"),
        new("smtp", "test-smtp-pw-789"),
    ]);

    /// <summary>Minimal valid config: one local target, one files job.</summary>
    public static BackupConfig Minimal() => new()
    {
        InstanceName = "SRV01",
        Targets = [new TargetConfig { Id = "usb", Type = TargetTypes.Local, Path = @"X:\Backup" }],
        Jobs =
        [
            new JobConfig
            {
                Id = "docs", Name = "Docs", Type = JobTypes.Files,
                Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["22:00"] },
                Sources = [new SourceConfig { Path = @"D:\Docs" }],
                Target = new JobTargetRef { TargetId = "usb", Subdir = "Docs" },
                Files = new FilesOptions(),
            },
        ],
    };
}
