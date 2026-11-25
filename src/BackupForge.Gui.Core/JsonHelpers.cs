using System.Text.Json.Nodes;

namespace BackupForge.Gui.Core;

/// <summary>Tolerant number reads (an int node must not crash a double / long read).</summary>
internal static class J
{
    public static long? L(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (long)d : null
        : null;

    public static double? D(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null
        : null;
}
