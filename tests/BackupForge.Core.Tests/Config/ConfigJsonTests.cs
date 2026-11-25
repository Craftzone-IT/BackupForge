using BackupForge.Core.Config;
using BackupForge.TestKit;

namespace BackupForge.Core.Tests.Config;

public class ConfigJsonTests
{
    [Fact]
    public void Parses_spec_example()
    {
        var c = ConfigFixtures.Example();
        Assert.Equal(1, c.Schema);
        Assert.Equal("SRV01 mentés", c.InstanceName);
        Assert.Equal(3, c.Jobs.Count);
        Assert.Equal(240, c.Jobs[0].Schedule.EveryMin);
        Assert.Equal(@"\\192.168.1.10\backup\srv01", c.Targets[0].Path);
        Assert.Equal(["sql-to-offsite"], c.Jobs[1].OnSuccess);
        Assert.Equal("@staging:sql-main", c.Jobs[2].Sources[0].Path);
        Assert.Equal(20, c.Jobs[0].Files!.DeleteGuardPct);
        Assert.True(c.Jobs[0].CatchUp);
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var once = ConfigJson.Serialize(ConfigFixtures.Example());
        var twice = ConfigJson.Serialize(ConfigJson.Parse(once));
        Assert.Equal(once, twice);
        Assert.Contains("\"instance_name\"", once, StringComparison.Ordinal);
        Assert.Contains("\"every_min\": 240", once, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_property_is_rejected_with_path()
    {
        var json = ConfigFixtures.ExampleJson.Replace("\"max_parallel_jobs\"", "\"max_paralel_jobs\"", StringComparison.Ordinal);
        var ex = Assert.Throws<ConfigFormatException>(() => ConfigJson.Parse(json));
        Assert.Contains("max_paralel_jobs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_throws_format_exception()
    {
        Assert.Throws<ConfigFormatException>(() => ConfigJson.Parse("{ \"schema\": 1, "));
    }

    [Theory]
    [InlineData("15", "15")]
    [InlineData("\"last\"", "last")]
    [InlineData("null", null)]
    public void Day_of_month_accepts_number_or_last(string raw, string? expected)
    {
        var json = "{\"schema\":1,\"instance_name\":\"x\",\"jobs\":[{\"id\":\"a\",\"name\":\"a\",\"type\":\"files\",\"schedule\":{\"kind\":\"monthly\",\"time\":\"01:00\",\"day_of_month\":" + raw + "}}]}";
        Assert.Equal(expected, ConfigJson.Parse(json).Jobs[0].Schedule.DayOfMonth);
    }

    [Fact]
    public void Monthly_day_is_written_back_as_number()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Monthly, DayOfMonth = "5", Time = "01:00" };
        Assert.Contains("\"day_of_month\": 5", ConfigJson.Serialize(c), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialized_output_has_no_bom()
    {
        var bytes = ConfigJson.ToUtf8(ConfigFixtures.Minimal());
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var c = ConfigJson.Parse("""{"schema":1,"instance_name":"x","jobs":[]}""");
        Assert.Equal(2, c.General.MaxParallelJobs);
        Assert.Equal(400, c.General.HistoryRetentionDays);
        Assert.Equal(30, c.General.LogRetentionDays);
        var f = new FilesOptions();
        Assert.Equal(21, f.Versioning.KeepDays);
        Assert.Equal(4, f.Parallelism);
        Assert.Equal(VssModes.WhenAvailable, f.Vss);
        Assert.Equal(720, new JobConfig { Type = JobTypes.Files }.EffectiveTimeoutMin);
        Assert.Equal(1440, new JobConfig { Type = JobTypes.Restic }.EffectiveTimeoutMin);
        Assert.Equal(360, new JobConfig { Type = JobTypes.Sql }.EffectiveTimeoutMin);
    }
}

public class ConfigSchemaTests
{
    [Fact]
    public void Spec_example_matches_schema() => SchemaAssert.Valid("config.schema.json", ConfigFixtures.ExampleJson);

    [Fact]
    public void Serialized_example_matches_schema() =>
        SchemaAssert.Valid("config.schema.json", ConfigJson.Serialize(ConfigFixtures.Example()));

    [Fact]
    public void Serialized_minimal_and_default_objects_match_schema()
    {
        var c = ConfigFixtures.Minimal();
        c.ResticRepos.Add(new ResticRepoConfig { Id = "r", Location = @"D:\repo", PasswordRef = "restic:r" });
        c.Jobs.Add(new JobConfig
        {
            Id = "s", Name = "s", Type = JobTypes.Sql, Schedule = new ScheduleConfig { Kind = ScheduleKinds.Weekly, Day = "Sun", Time = "01:00" },
            Sql = new SqlJobOptions { Instance = ".", Checkdb = "monthly last 02:00" },
        });
        SchemaAssert.Valid("config.schema.json", ConfigJson.Serialize(c));
    }

    [Theory]
    [InlineData("\"schema\": 1", "\"schema\": 2")]
    [InlineData("\"type\": \"smb\"", "\"type\": \"ftp\"")]
    [InlineData("\"every_min\": 240", "\"every_min\": 1")]
    [InlineData("\"times\": [\"12:30\", \"22:30\"]", "\"times\": [\"25:30\"]")]
    [InlineData("\"delete_guard_pct\": 20", "\"delete_guard_pct\": 0")]
    [InlineData("\"forget_prune\": \"weekly Sun 03:00\"", "\"forget_prune\": \"sometimes\"")]
    [InlineData("\"id\": \"company-mirror\"", "\"id\": \"Company Mirror\"")]
    public void Schema_rejects_invalid_values(string from, string to)
    {
        var json = ConfigFixtures.ExampleJson.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(ConfigFixtures.ExampleJson, json);
        Assert.NotEmpty(SchemaAssert.Errors("config.schema.json", json));
    }
}
