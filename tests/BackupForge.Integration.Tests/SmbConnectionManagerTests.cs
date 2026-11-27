using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Windows;

namespace BackupForge.Integration.Tests;

/// <summary>CR-002 A2.: reference counting of SMB connections, with a fake mpr.dll (runs on Linux too).</summary>
public class SmbConnectionManagerTests
{
    private sealed class FakeWNet : INativeWNet
    {
        public readonly List<string> Calls = [];
        public Func<string, string?, int> OnAdd { get; set; } = (_, _) => 0;
        private int _active;
        public int MaxActiveAdds;
        public int Add(string share, string? username, string? password)
        {
            var a = Interlocked.Increment(ref _active);
            lock (Calls) { MaxActiveAdds = Math.Max(MaxActiveAdds, a); Calls.Add($"add {share} {username}"); }
            Thread.Sleep(20); // widen the race window
            Interlocked.Decrement(ref _active);
            return OnAdd(share, username);
        }
        public int Cancel(string share) { lock (Calls) Calls.Add($"cancel {share}"); return 0; }
        public string Describe(int error) => "error text";
        public int Count(string prefix) { lock (Calls) return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal)); }
    }

    private static readonly ISecretStore Secrets = new InMemorySecretStore([new("target:t", "test-password-123")]);

    private static TargetConfig T(string path = @"\\NAS01\backup\Company", string user = @"NAS01\bf") =>
        new() { Id = "t", Type = TargetTypes.Smb, Path = path, Username = user, PasswordRef = "target:t" };

    [Fact]
    public async Task Parallel_acquires_on_one_server_make_one_connection_and_disconnect_after_the_last()
    {
        var net = new FakeWNet();
        var m = new SmbConnectionManager(net);
        var leases = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => m.Acquire(T(), Secrets))));
        Assert.Equal(1, net.Count("add"));
        Assert.Equal(1, m.OpenOwnConnections);
        foreach (var l in leases.Skip(1)) l.Dispose();
        Assert.Equal(0, net.Count("cancel"));
        leases[0].Dispose();
        leases[0].Dispose(); // idempotent
        Assert.Equal(1, net.Count("cancel"));
        Assert.Equal(0, m.OpenOwnConnections);
    }

    [Fact]
    public void Server_name_is_case_insensitive_and_other_shares_reuse_the_user()
    {
        var net = new FakeWNet();
        var m = new SmbConnectionManager(net);
        using var a = m.Acquire(T(@"\\NAS01\backup\x"), Secrets);
        using var b = m.Acquire(T(@"\\nas01\BACKUP\y"), Secrets);
        using var c = m.Acquire(T(@"\\nas01\other"), Secrets);
        Assert.Equal(2, net.Count("add")); // backup + other, same user
    }

    [Fact]
    public void Foreign_connection_is_used_but_never_disconnected()
    {
        var net = new FakeWNet { OnAdd = (_, _) => SmbConnectionManager.ERROR_ALREADY_ASSIGNED };
        var m = new SmbConnectionManager(net);
        m.Acquire(T(), Secrets).Dispose();
        Assert.Equal(0, net.Count("cancel"));

        var conflict = new SmbConnectionManager(new FakeWNet { OnAdd = (_, _) => SmbConnectionManager.ERROR_SESSION_CREDENTIAL_CONFLICT });
        var ex = Assert.Throws<TargetConnectException>(() => conflict.Acquire(T(), Secrets));
        Assert.Equal(1219, ex.ErrorCode);
        Assert.Contains("other credentials", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-password-123", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_switch_waits_for_the_old_leases_then_reconnects()
    {
        var net = new FakeWNet();
        var m = new SmbConnectionManager(net);
        var old = m.Acquire(T(user: @"NAS01\old"), Secrets);
        var pending = Task.Run(() => m.Acquire(T(user: @"NAS01\new"), Secrets));
        await Task.Delay(200);
        Assert.False(pending.IsCompleted);
        old.Dispose();
        using var lease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["add \\\\NAS01\\backup NAS01\\old", "cancel \\\\NAS01\\backup", "add \\\\NAS01\\backup NAS01\\new"], net.Calls);

        var quick = new SmbConnectionManager(new FakeWNet()) { UserSwitchWait = TimeSpan.FromMilliseconds(50) };
        using var held = quick.Acquire(T(user: @"NAS01\old"), Secrets);
        Assert.Throws<TargetConnectException>(() => quick.Acquire(T(user: @"NAS01\new"), Secrets));
    }

    [Fact]
    public void Exception_in_the_run_releases_the_lease()
    {
        var net = new FakeWNet();
        var m = new SmbConnectionManager(net);
        try
        {
            using (m.Acquire(T(), Secrets)) throw new IOException("target lost");
        }
        catch (IOException) { }
        Assert.Equal(1, net.Count("cancel"));
        Assert.Equal(0, m.OpenOwnConnections);
    }

    [Fact]
    public void Local_and_service_account_targets_need_no_connection()
    {
        var net = new FakeWNet();
        var m = new SmbConnectionManager(net);
        m.Acquire(new TargetConfig { Id = "l", Path = @"D:\Backup" }, Secrets).Dispose();
        m.Acquire(new TargetConfig { Id = "s", Type = TargetTypes.Smb, Path = @"\\NAS01\backup" }, Secrets).Dispose();
        Assert.Empty(net.Calls);
        Assert.Throws<TargetConnectException>(() => m.Acquire(T(path: @"D:\not-unc"), Secrets));
    }
}
