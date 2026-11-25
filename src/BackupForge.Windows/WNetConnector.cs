using System.ComponentModel;
using System.Runtime.InteropServices;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;

namespace BackupForge.Windows;

/// <summary>
/// SMB target with its own credentials (SPEC 14.): WNetAddConnection2 to \\server\share without a drive letter,
/// cancelled when the run ends. Targets without a username use the service account (LocalSystem → the computer account).
/// </summary>
public sealed partial class WNetConnector : ITargetConnector
{
    private const int RESOURCETYPE_DISK = 1;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_ALREADY_ASSIGNED = 85;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    public IDisposable Connect(TargetConfig target, ISecretStore secrets)
    {
        if (target.Type != TargetTypes.Smb || string.IsNullOrEmpty(target.Username)) return NullDisposable.Instance;
        var share = ShareRoot(target.Path) ?? throw new TargetConnectException($"not a UNC path: {target.Path}");
        var password = target.PasswordRef is null ? null : secrets.Get(target.PasswordRef);
        if (target.PasswordRef is not null && password is null) throw new TargetConnectException($"secret '{target.PasswordRef}' is not set");
        var res = new NetResource { Type = RESOURCETYPE_DISK, RemoteName = share };
        var rc = WNetAddConnection2(ref res, password, target.Username, 0);
        if (rc == ERROR_ALREADY_ASSIGNED) return NullDisposable.Instance;
        if (rc == ERROR_SESSION_CREDENTIAL_CONFLICT)
            throw new TargetConnectException($"{share}: this computer already has a connection to the server with other credentials (error 1219) — use the server's other name / IP, or remove the other connection", rc);
        if (rc != 0) throw new TargetConnectException($"{share}: {new Win32Exception(rc).Message} (error {rc})", rc);
        return new Connection(share);
    }

    /// <summary>\\server\share\a\b → \\server\share.</summary>
    public static string? ShareRoot(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : $@"\\{parts[0]}\{parts[1]}";
    }

    private sealed class Connection(string share) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            WNetCancelConnection2(share, 0, true);
        }
    }
}
