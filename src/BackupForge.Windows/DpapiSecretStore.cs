using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;

namespace BackupForge.Windows;

/// <summary>
/// secrets.dat (SPEC 14.): the whole reference → value map as JSON, protected with DPAPI LocalMachine scope and an
/// application-specific entropy. The file ACL is SYSTEM only when the service runs as LocalSystem (otherwise SYSTEM + the
/// current user, for development and tests). Values are never returned through IPC.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BackupForge/secrets/v1/7f3c2a9e");
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _values;

    public DpapiSecretStore(string path)
    {
        _path = path;
        _values = Load();
    }

    public bool Has(string reference) { lock (_lock) return _values.ContainsKey(reference); }
    public string? Get(string reference) { lock (_lock) return _values.GetValueOrDefault(reference); }
    public IReadOnlyCollection<string> References { get { lock (_lock) return [.. _values.Keys]; } }

    public void Set(string reference, string value)
    {
        lock (_lock)
        {
            _values[reference] = value;
            Save();
        }
    }

    public bool Remove(string reference)
    {
        lock (_lock)
        {
            if (!_values.Remove(reference)) return false;
            Save();
            return true;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        var blob = File.ReadAllBytes(_path);
        var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) is { } d
                ? new Dictionary<string, string>(d, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(_values);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            AtomicFile.Write(_path, blob);
            RestrictAcl(_path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void RestrictAcl(string path)
    {
        var fi = new FileInfo(path);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        using var me = WindowsIdentity.GetCurrent();
        if (me.User is { } user && !user.Equals(system))
            sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        fi.SetAccessControl(sec);
    }
}
