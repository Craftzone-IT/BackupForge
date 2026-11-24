namespace BackupForge.Core.Abstractions;

/// <summary>Small persistent state (history.db kv table) engines may use, e.g. the delete-guard approval.</summary>
public interface IKeyValueStore
{
    string? GetValue(string key);
    void SetValue(string key, string? value);
}

public sealed class MemoryKeyValueStore : IKeyValueStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? GetValue(string key) { lock (_values) return _values.GetValueOrDefault(key); }
    public void SetValue(string key, string? value)
    {
        lock (_values)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }
}
