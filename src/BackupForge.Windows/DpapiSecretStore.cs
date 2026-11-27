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
/// application-specific entropy. The file ACL is SYSTEM only (SPEC 14.) for the service (LocalSystem) and for the installer
/// helper (<c>systemOnly</c>, CR-002 A6.); only a non-SYSTEM development / test process adds itself. Values are never
/// returned through IPC.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BackupForge/secrets/v1/7f3c2a9e");
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _values;

    private readonly bool _systemOnly;

    public DpapiSecretStore(string path, bool systemOnly = false)
    {
        _path = path;
        _systemOnly = systemOnly || IsLocalSystem();
        _values = Load();
        // start-up migration: a file written by an older installer may still list the installing administrator
        if (_systemOnly && File.Exists(_path) && !HasSystemOnlyAcl(_path))
        {
            RestrictAcl(_path, systemOnly: true);
            AclRepaired = true;
        }
    }

    /// <summary>The existing file's ACL had other entries than SYSTEM and was reset (the service logs this).</summary>
    public bool AclRepaired { get; }

    private static bool IsLocalSystem()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User is { } u && u.IsWellKnown(WellKnownSidType.LocalSystemSid);
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
        byte[] blob;
        try { blob = File.ReadAllBytes(_path); }
        catch (UnauthorizedAccessException) when (_systemOnly)
        {
            // the elevated installer (an administrator, owner of the file) re-imports into a SYSTEM-only file: let itself in
            // for the read, RestrictAcl puts SYSTEM-only back right after
            RestrictAcl(_path, systemOnly: false);
            blob = File.ReadAllBytes(_path);
            RestrictAcl(_path, systemOnly: true);
        }
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
            RestrictAcl(_path, _systemOnly);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>No inheritance; SYSTEM full control; the current (non-SYSTEM) user too unless <paramref name="systemOnly"/>.</summary>
    public static void RestrictAcl(string path, bool systemOnly)
    {
        var fi = new FileInfo(path);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        using var me = WindowsIdentity.GetCurrent();
        if (!systemOnly && me.User is { } user && !user.Equals(system))
            sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        fi.SetAccessControl(sec);
    }

    /// <summary>Protected (no inheritance) and every access rule is for SYSTEM.</summary>
    public static bool HasSystemOnlyAcl(string path)
    {
        var sec = new FileInfo(path).GetAccessControl();
        if (!sec.AreAccessRulesProtected) return false;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return rules.Count > 0 && rules.Cast<FileSystemAccessRule>().All(r => r.IdentityReference.Equals(system));
    }
}
