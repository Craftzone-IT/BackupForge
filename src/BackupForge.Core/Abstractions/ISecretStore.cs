namespace BackupForge.Core.Abstractions;

/// <summary>
/// Secret storage (secrets.dat on Windows: DPAPI LocalMachine). Values never leave the service process
/// except into child-process environment variables or authentication calls.
/// </summary>
public interface ISecretStore
{
    bool Has(string reference);
    string? Get(string reference);
    void Set(string reference, string value);
    bool Remove(string reference);
    IReadOnlyCollection<string> References { get; }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public InMemorySecretStore(IEnumerable<KeyValuePair<string, string>>? initial = null)
    {
        if (initial is null) return;
        foreach (var (k, v) in initial) _values[k] = v;
    }

    public event Action<string>? SecretSet;

    public bool Has(string reference) { lock (_lock) return _values.ContainsKey(reference); }
    public string? Get(string reference) { lock (_lock) return _values.GetValueOrDefault(reference); }
    public void Set(string reference, string value) { lock (_lock) _values[reference] = value; SecretSet?.Invoke(value); }
    public bool Remove(string reference) { lock (_lock) return _values.Remove(reference); }
    public IReadOnlyCollection<string> References { get { lock (_lock) return [.. _values.Keys]; } }
}
