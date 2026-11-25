using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupForge.Core.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BackupConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>Parse / serialize config.json.</summary>
public static class ConfigJson
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static BackupConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.BackupConfig)
                   ?? throw new ConfigFormatException("$", "empty document");
        }
        catch (JsonException ex)
        {
            throw new ConfigFormatException(ex.Path ?? "$", ex.Message, ex);
        }
    }

    public static string Serialize(BackupConfig config) =>
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.BackupConfig);

    public static BackupConfig Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Deep copy via JSON round trip.</summary>
    public static BackupConfig Clone(BackupConfig config) => Parse(Serialize(config));

    public static byte[] ToUtf8(BackupConfig config) => Utf8NoBom.GetBytes(Serialize(config));
}

public sealed class ConfigFormatException : Exception
{
    public ConfigFormatException(string jsonPath, string message, Exception? inner = null)
        : base($"config.json: {jsonPath}: {message}", inner) => JsonPath = jsonPath;

    public string JsonPath { get; }
}

/// <summary>Accepts a JSON number or string, stores it as string (used by day_of_month: 1..28 | "last").</summary>
public sealed class IntOrStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString(),
            _ => throw new JsonException("expected number or string"),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) writer.WriteNumberValue(n);
        else writer.WriteStringValue(value);
    }
}
