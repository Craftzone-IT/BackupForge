using System.Text.Json;
using Json.Schema;

using BackupForge.TestKit;

namespace BackupForge.Core.Tests.Infra;

public static class SchemaAssert
{
    private static readonly Dictionary<string, JsonSchema> Cache = [];

    public static JsonSchema Load(string file)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(file, out var s))
            {
                s = JsonSchema.FromText(File.ReadAllText(TestPaths.Schema(file)));
                Cache[file] = s;
            }
            return s;
        }
    }

    public static IReadOnlyList<string> Errors(string schemaFile, string json)
    {
        var schema = Load(schemaFile);
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        var list = new List<string>();
        foreach (var d in result.Details ?? [])
            if (d.Errors is { Count: > 0 })
                foreach (var (k, v) in d.Errors) list.Add($"{d.InstanceLocation}: {k}: {v}");
        return list.Count > 0 ? list : ["invalid"];
    }

    public static void Valid(string schemaFile, string json)
    {
        var errors = Errors(schemaFile, json);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
