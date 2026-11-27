using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;

namespace BackupForge.Windows;

/// <summary>The two mpr.dll calls the connection manager needs (an interface so the reference counting is testable on Linux).</summary>
public interface INativeWNet
{
    /// <summary>WNetAddConnection2 to <paramref name="share"/> without a drive letter; returns the Win32 error code (0 = ok).</summary>
    int Add(string share, string? username, string? password);
    /// <summary>WNetCancelConnection2 (force); returns the Win32 error code.</summary>
    int Cancel(string share);
    string Describe(int error);
}

/// <summary>
/// One SMB connection owner for the whole process (CR-002 A2.): runs, the target probe, <c>target.test</c>, restores and the
/// free-space query all lease the connection here instead of calling WNetAddConnection2 / WNetCancelConnection2 on their own.
/// <list type="bullet">
/// <item>Key: the server name (lower case) — Windows allows one credential set per server per logon session (error 1219).</item>
/// <item>A share already connected by BackupForge with the same user is reused (reference count +1, no WNet call).</item>
/// <item>Disconnect only for BackupForge's own connection, when its last lease is released.</item>
/// <item>A connection BackupForge did not create (error 85, or 1219 because of a foreign session) is never disconnected.</item>
/// <item>A different user for a server that still has leases waits for them (up to <see cref="UserSwitchWait"/>), then
///   the old connection is gone and the new one is created.</item>
/// </list>
/// </summary>
public sealed class SmbConnectionManager(INativeWNet net)
{
    public const int ERROR_ALREADY_ASSIGNED = 85;
    public const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

    public TimeSpan UserSwitchWait { get; init; } = TimeSpan.FromMinutes(2);

    private sealed class Server
    {
        public string? User;                                                       // user of BackupForge's own connections
        public readonly Dictionary<string, int> Own = new(StringComparer.OrdinalIgnoreCase);     // share → leases
        public readonly Dictionary<string, int> Foreign = new(StringComparer.OrdinalIgnoreCase); // share → leases (never cancelled)
        public int OwnLeases => Own.Values.Sum();
    }

    private readonly Dictionary<string, Server> _servers = new(StringComparer.Ordinal);

    /// <summary>Number of shares BackupForge itself holds open (tests / diagnostics).</summary>
    public int OpenOwnConnections { get { lock (_servers) return _servers.Values.Sum(s => s.Own.Count); } }

    public IDisposable Acquire(TargetConfig target, ISecretStore secrets)
    {
        if (target.Type != TargetTypes.Smb || string.IsNullOrEmpty(target.Username)) return NullDisposable.Instance;
        var share = WNetConnector.ShareRoot(target.Path) ?? throw new TargetConnectException($"not a UNC path: {target.Path}");
        var password = target.PasswordRef is null ? null : secrets.Get(target.PasswordRef);
        if (target.PasswordRef is not null && password is null) throw new TargetConnectException($"secret '{target.PasswordRef}' is not set");
        var key = share[2..].Split('\\')[0].ToLowerInvariant();
        var user = target.Username;

        Server s;
        lock (_servers)
        {
            if (!_servers.TryGetValue(key, out s!)) _servers[key] = s = new Server();
        }
        lock (s)
        {
            // another user still holds this server: wait for its last lease (then it is disconnected)
            var deadline = DateTime.UtcNow + UserSwitchWait;
            while (s.OwnLeases > 0 && !string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase))
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                    throw new TargetConnectException($"{share}: BackupForge is still connected to this server as another user — try again when the running job has finished", ERROR_SESSION_CREDENTIAL_CONFLICT);
                Monitor.Wait(s, left);
            }
            if (s.Own.TryGetValue(share, out var n))
            {
                s.Own[share] = n + 1;
                return new Lease(this, s, share, own: true);
            }
            if (s.Foreign.TryGetValue(share, out var f))
            {
                s.Foreign[share] = f + 1;
                return new Lease(this, s, share, own: false);
            }
            var rc = net.Add(share, user, password);
            switch (rc)
            {
                case 0:
                    s.User = user;
                    s.Own[share] = 1;
                    return new Lease(this, s, share, own: true);
                case ERROR_ALREADY_ASSIGNED:
                    s.Foreign[share] = 1; // somebody else's connection: use it, never disconnect it
                    return new Lease(this, s, share, own: false);
                case ERROR_SESSION_CREDENTIAL_CONFLICT:
                    throw new TargetConnectException(
                        $"{share}: this computer already has a connection to the server with other credentials (error 1219), not made by BackupForge " +
                        "(e.g. a mapped drive or another program in the service's session) — use the server's other name / IP, or remove the other connection", rc);
                default:
                    throw new TargetConnectException($"{share}: {net.Describe(rc)} (error {rc})", rc);
            }
        }
    }

    private void Release(Server s, string share, bool own)
    {
        lock (s)
        {
            var map = own ? s.Own : s.Foreign;
            if (!map.TryGetValue(share, out var n)) return;
            if (n > 1) { map[share] = n - 1; return; }
            map.Remove(share);
            if (own)
            {
                net.Cancel(share);
                if (s.Own.Count == 0) s.User = null;
            }
            Monitor.PulseAll(s);
        }
    }

    private sealed class Lease(SmbConnectionManager owner, Server server, string share, bool own) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Release(server, share, own);
        }
    }
}
