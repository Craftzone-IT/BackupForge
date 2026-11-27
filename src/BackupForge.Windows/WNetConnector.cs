using System.ComponentModel;
using System.Runtime.InteropServices;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;

namespace BackupForge.Windows;

/// <summary>
/// SMB target with its own credentials (SPEC 14.): WNetAddConnection2 to \\server\share without a drive letter.
/// Every caller shares one <see cref="SmbConnectionManager"/> (CR-002 A2.): reference-counted per server, disconnected
/// after the last user. Targets without a username use the service account (LocalSystem → the computer account).
/// </summary>
public sealed partial class WNetConnector : ITargetConnector
{
    private const int RESOURCETYPE_DISK = 1;

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

    private sealed class Native : INativeWNet
    {
        public int Add(string share, string? username, string? password)
        {
            var res = new NetResource { Type = RESOURCETYPE_DISK, RemoteName = share };
            return WNetAddConnection2(ref res, password, username, 0);
        }

        public int Cancel(string share) => WNetCancelConnection2(share, 0, true);
        public string Describe(int error) => new Win32Exception(error).Message;
    }

    public WNetConnector() : this(new Native()) { }

    public WNetConnector(INativeWNet native) => Manager = new SmbConnectionManager(native);

    public SmbConnectionManager Manager { get; }

    public IDisposable Connect(TargetConfig target, ISecretStore secrets) => Manager.Acquire(target, secrets);

    /// <summary>\\server\share\a\b → \\server\share.</summary>
    public static string? ShareRoot(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : $@"\\{parts[0]}\{parts[1]}";
    }
}
