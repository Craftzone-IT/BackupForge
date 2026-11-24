using System.Runtime.InteropServices;
using BackupForge.Core.Abstractions;

namespace BackupForge.Windows;

/// <summary>
/// Thin COM interop over vssapi.dll (SPEC 2./6.6): one copy-only (VSS_BT_COPY) backup snapshot per volume with writer
/// involvement, auto-released (deleted) when the session is disposed. Needs an elevated process (LocalSystem / Administrators).
/// </summary>
public sealed partial class VssSnapshotProvider : ISnapshotProvider
{
    private const int VSS_CTX_BACKUP = 0;
    private const int VSS_CTX_ALL = unchecked((int)0xffffffff);
    private const int VSS_BT_COPY = 5;
    private const int VSS_OBJECT_SNAPSHOT_SET = 2;
    private const int VSS_OBJECT_SNAPSHOT = 3;
    private const int VSS_E_OBJECT_NOT_FOUND = unchecked((int)0x80042308);
    private const int RPC_E_TOO_LATE = unchecked((int)0x80010119);

    private static readonly Lock InitLock = new();
    private static bool _securityInitialised;

    public bool IsSupported => Environment.IsPrivilegedProcess;

    public ISnapshotSession Create(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        // another backup (or our previous set being released) may still be in progress: wait and retry a few times
        for (var attempt = 1; ; attempt++)
        {
            try { return CreateOnce(paths, ct); }
            catch (VssException ex) when (attempt < 4 && unchecked((uint)ex.Code) is 0x80042316 or 0x80042302)
            {
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10 * attempt))) ct.ThrowIfCancellationRequested();
            }
        }
    }

    private ISnapshotSession CreateOnce(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        EnsureComSecurity();
        var volumes = paths.Select(VolumeOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Check(CreateVssBackupComponentsInternal(out var bc), "CreateVssBackupComponents");
        var session = new Session(bc);
        try
        {
            Check(bc.InitializeForBackup(null), "InitializeForBackup");
            Check(bc.SetContext(VSS_CTX_BACKUP), "SetContext");
            Check(bc.SetBackupState(false, false, VSS_BT_COPY, false), "SetBackupState");
            Wait(bc.GatherWriterMetadata(out var gather), gather, "GatherWriterMetadata", ct);
            bc.FreeWriterMetadata();
            Check(bc.StartSnapshotSet(out var setId), "StartSnapshotSet");
            session.SetId = setId;
            var ids = new List<(Guid Id, string Volume)>();
            foreach (var v in volumes)
            {
                Check(bc.AddToSnapshotSet(v, Guid.Empty, out var sid), $"AddToSnapshotSet({v})");
                ids.Add((sid, v));
            }
            Wait(bc.PrepareForBackup(out var prep), prep, "PrepareForBackup", ct);
            Wait(bc.DoSnapshotSet(out var doSet), doSet, "DoSnapshotSet", ct);
            foreach (var (sid, v) in ids)
            {
                var device = DeviceObject(bc, sid);
                session.Add(sid, v, device);
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> DeleteOrphans(IEnumerable<string> snapshotIds)
    {
        var deleted = new List<string>();
        var list = snapshotIds.ToList();
        if (list.Count == 0 || !IsSupported) return deleted;
        EnsureComSecurity();
        if (CreateVssBackupComponentsInternal(out var bc) != 0) return deleted;
        try
        {
            if (bc.InitializeForBackup(null) != 0 || bc.SetContext(VSS_CTX_ALL) != 0) return deleted;
            foreach (var s in list)
            {
                if (!Guid.TryParse(s, out var id)) continue;
                var hr = bc.DeleteSnapshots(id, VSS_OBJECT_SNAPSHOT, true, out _, out _);
                if (hr == 0 || hr == VSS_E_OBJECT_NOT_FOUND) deleted.Add(s);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(bc);
        }
        return deleted;
    }

    private static string DeviceObject(IVssBackupComponents bc, Guid snapshotId)
    {
        var size = Marshal.SizeOf<VssSnapshotProp>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Check(bc.GetSnapshotProperties(snapshotId, buf), "GetSnapshotProperties");
            var prop = Marshal.PtrToStructure<VssSnapshotProp>(buf);
            var device = Marshal.PtrToStringUni(prop.SnapshotDeviceObject) ?? throw new InvalidOperationException("snapshot has no device object");
            VssFreeSnapshotPropertiesInternal(buf);
            return device;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>The volume mount point of a path ("C:\", or a mounted folder).</summary>
    public static string VolumeOf(string path)
    {
        var buffer = new char[1024];
        if (!GetVolumePathName(Path.GetFullPath(path), buffer, buffer.Length))
            throw new IOException($"cannot determine the volume of {path}: {Marshal.GetLastPInvokeErrorMessage()}");
        var s = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        return s.EndsWith('\\') ? s : s + "\\";
    }

    private static void EnsureComSecurity()
    {
        lock (InitLock)
        {
            if (_securityInitialised) return;
            _securityInitialised = true;
            const int RPC_C_AUTHN_LEVEL_PKT_PRIVACY = 6;
            const int RPC_C_IMP_LEVEL_IDENTIFY = 2;
            const int EOAC_DYNAMIC_CLOAKING = 0x40;
            var hr = CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, RPC_C_AUTHN_LEVEL_PKT_PRIVACY, RPC_C_IMP_LEVEL_IDENTIFY, IntPtr.Zero, EOAC_DYNAMIC_CLOAKING, IntPtr.Zero);
            if (hr != 0 && hr != RPC_E_TOO_LATE) { /* best effort; VSS reports its own error if security is wrong */ }
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new VssException($"{what} failed: 0x{hr:X8} {VssException.Describe(hr)}", hr);
    }

    private static void Wait(int hr, IVssAsync? op, string what, CancellationToken ct)
    {
        Check(hr, what);
        if (op is null) return;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Check(op.Wait(1000), what + " (wait)");
                Check(op.QueryStatus(out var status, IntPtr.Zero), what + " (status)");
                if (status == 0x0004230A) return;           // VSS_S_ASYNC_FINISHED
                if (status == 0x00042309) continue;          // VSS_S_ASYNC_PENDING
                if (status == 0x0004230B) throw new OperationCanceledException(what + " cancelled"); // VSS_S_ASYNC_CANCELLED
                Check(status, what);
                return;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(op);
        }
    }

    private sealed class Session(IVssBackupComponents bc) : ISnapshotSession
    {
        private readonly List<(string SnapshotId, string? SetId, string Volume)> _snapshots = [];
        private readonly List<(string Volume, string Device)> _map = [];
        private bool _disposed;
        public Guid SetId { get; set; }

        public IReadOnlyList<(string SnapshotId, string? SetId, string Volume)> Snapshots => _snapshots;

        public void Add(Guid id, string volume, string device)
        {
            _snapshots.Add((id.ToString(), SetId.ToString(), volume));
            _map.Add((volume, device));
        }

        public string Translate(string path)
        {
            var full = Path.GetFullPath(path);
            foreach (var (volume, device) in _map.OrderByDescending(m => m.Volume.Length))
                if (full.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
                    return device + "\\" + full[volume.Length..];
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (bc.BackupComplete(out var done) == 0 && done is not null)
                {
                    done.Wait(60_000);
                    Marshal.ReleaseComObject(done);
                }
                if (SetId != Guid.Empty) bc.DeleteSnapshots(SetId, VSS_OBJECT_SNAPSHOT_SET, true, out _, out _);
            }
            finally
            {
                Marshal.ReleaseComObject(bc);
            }
        }
    }

    // ------------------------------------------------------------------ interop

    [DllImport("vssapi.dll", ExactSpelling = true)]
    private static extern int CreateVssBackupComponentsInternal([MarshalAs(UnmanagedType.Interface)] out IVssBackupComponents ppBackup);

    [DllImport("vssapi.dll", ExactSpelling = true)]
    private static extern void VssFreeSnapshotPropertiesInternal(IntPtr pProp);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(IntPtr pSecDesc, int cAuthSvc, IntPtr asAuthSvc, IntPtr pReserved1, int dwAuthnLevel, int dwImpLevel, IntPtr pAuthList, int dwCapabilities, IntPtr pReserved3);

    [StructLayout(LayoutKind.Sequential)]
    private struct VssSnapshotProp
    {
        public Guid SnapshotId;
        public Guid SnapshotSetId;
        public int SnapshotsCount;
        public IntPtr SnapshotDeviceObject;
        public IntPtr OriginalVolumeName;
        public IntPtr OriginatingMachine;
        public IntPtr ServiceMachine;
        public IntPtr ExposedName;
        public IntPtr ExposedPath;
        public Guid ProviderId;
        public int SnapshotAttributes;
        public long CreationTimestamp;
        public int Status;
    }

    [ComImport, Guid("507C37B4-CF5B-4e95-B0AF-14EB9767467E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssAsync
    {
        [PreserveSig] int Cancel();
        [PreserveSig] int Wait(uint milliseconds);
        [PreserveSig] int QueryStatus(out int hrResult, IntPtr reserved);
    }

    /// <summary>vsbackup.h IVssBackupComponents — the vtable order matters, unused slots are placeholders.</summary>
    [ComImport, Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssBackupComponents
    {
        [PreserveSig] int GetWriterComponentsCount(out uint count);
        [PreserveSig] int GetWriterComponents(uint index, out IntPtr writer);
        [PreserveSig] int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? xml);
        [PreserveSig] int SetBackupState([MarshalAs(UnmanagedType.U1)] bool selectComponents, [MarshalAs(UnmanagedType.U1)] bool backupBootableSystemState, int backupType, [MarshalAs(UnmanagedType.U1)] bool partialFileSupport);
        [PreserveSig] int InitializeForRestore([MarshalAs(UnmanagedType.BStr)] string xml);
        [PreserveSig] int SetRestoreState(int restoreType);
        [PreserveSig] int GatherWriterMetadata([MarshalAs(UnmanagedType.Interface)] out IVssAsync? async);
        [PreserveSig] int GetWriterMetadataCount(out uint count);
        [PreserveSig] int GetWriterMetadata(uint index, out Guid instanceId, out IntPtr metadata);
        [PreserveSig] int FreeWriterMetadata();
        [PreserveSig] int AddComponent();
        [PreserveSig] int PrepareForBackup([MarshalAs(UnmanagedType.Interface)] out IVssAsync? async);
        [PreserveSig] int AbortBackup();
        [PreserveSig] int GatherWriterStatus([MarshalAs(UnmanagedType.Interface)] out IVssAsync? async);
        [PreserveSig] int GetWriterStatusCount(out uint count);
        [PreserveSig] int FreeWriterStatus();
        [PreserveSig] int GetWriterStatus();
        [PreserveSig] int SetBackupSucceeded();
        [PreserveSig] int SetBackupOptions();
        [PreserveSig] int SetSelectedForRestore();
        [PreserveSig] int SetRestoreOptions();
        [PreserveSig] int SetAdditionalRestores();
        [PreserveSig] int SetPreviousBackupStamp();
        [PreserveSig] int SaveAsXML();
        [PreserveSig] int BackupComplete([MarshalAs(UnmanagedType.Interface)] out IVssAsync? async);
        [PreserveSig] int AddAlternativeLocationMapping();
        [PreserveSig] int AddRestoreSubcomponent();
        [PreserveSig] int SetFileRestoreStatus();
        [PreserveSig] int AddNewTarget();
        [PreserveSig] int SetRangesFilePath();
        [PreserveSig] int PreRestore();
        [PreserveSig] int PostRestore();
        [PreserveSig] int SetContext(int context);
        [PreserveSig] int StartSnapshotSet(out Guid snapshotSetId);
        [PreserveSig] int AddToSnapshotSet([MarshalAs(UnmanagedType.LPWStr)] string volumeName, Guid providerId, out Guid snapshotId);
        [PreserveSig] int DoSnapshotSet([MarshalAs(UnmanagedType.Interface)] out IVssAsync? async);
        [PreserveSig] int DeleteSnapshots(Guid sourceObjectId, int sourceObjectType, [MarshalAs(UnmanagedType.Bool)] bool forceDelete, out int deletedSnapshots, out Guid nondeletedSnapshotId);
        [PreserveSig] int ImportSnapshots();
        [PreserveSig] int BreakSnapshotSet();
        [PreserveSig] int GetSnapshotProperties(Guid snapshotId, IntPtr prop);
    }
}

public sealed class VssException(string message, int hresult) : Exception(message)
{
    public int Code { get; } = hresult;

    public static string Describe(int hr) => unchecked((uint)hr) switch
    {
        0x80042302 => "VSS_E_UNEXPECTED (VSS service problem)",
        0x80042306 => "VSS_E_PROVIDER_VETO",
        0x80042308 => "VSS_E_OBJECT_NOT_FOUND",
        0x8004230C => "VSS_E_VOLUME_NOT_SUPPORTED",
        0x8004230F => "VSS_E_UNEXPECTED_PROVIDER_ERROR",
        0x80042312 => "VSS_E_MAXIMUM_NUMBER_OF_VOLUMES_REACHED",
        0x80042313 => "VSS_E_FLUSH_WRITES_TIMEOUT",
        0x80042314 => "VSS_E_HOLD_WRITES_TIMEOUT",
        0x80042317 => "VSS_E_MAXIMUM_NUMBER_OF_SNAPSHOTS_REACHED",
        0x8004231F => "VSS_E_INSUFFICIENT_STORAGE (not enough shadow storage space)",
        0x80042316 => "VSS_E_SNAPSHOT_SET_IN_PROGRESS (another backup is running)",
        0x80070005 => "access denied (run elevated / as LocalSystem)",
        _ => "",
    };
}
