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

    /// <summary>Upper bound for one VSS async step (writers can hang); when_available then continues without VSS.</summary>
    public static TimeSpan AsyncLimit { get; set; } = TimeSpan.FromMinutes(3);

    public ISnapshotSession Create(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        try
        {
            return CreateOnce(paths, withWriters: true, ct, []);
        }
        catch (VssStepException ex) when (ex.Message.StartsWith("GatherWriterMetadata", StringComparison.Ordinal))
        {
            // a hung writer must not block the backup: a fresh requester without writer metadata (crash-consistent snapshot)
            var session = CreateOnce(paths, withWriters: false, ct, [.. ex.Trace]);
            session.WritersSkippedReasons.Add(ex.Message);
            return session;
        }
    }

    /// <summary>
    /// One requester, one snapshot set. Every step (HRESULT, async status, time) is recorded in <paramref name="trace"/>; the
    /// trace is attached to failures (<see cref="VssStepException"/>, <see cref="VssSnapshotIncompleteException"/>) and to the
    /// session (<c>bf diag vss</c>). The trace holds volume names, never the machine name.
    /// </summary>
    private Session CreateOnce(IReadOnlyCollection<string> paths, bool withWriters, CancellationToken ct, List<string> trace, Action<string>? raw = null)
    {
        EnsureComSecurity();
        var volumes = paths.Select(VolumeOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        trace.Add($"requester ({(withWriters ? "with writers" : "without writers")}) for {string.Join(", ", volumes)}");
        Step(CreateVssBackupComponentsInternal(out var bc), "CreateVssBackupComponents", trace);
        var session = new Session(bc, trace);
        try
        {
            Step(bc.InitializeForBackup(null), "InitializeForBackup", trace);
            Step(bc.SetContext(VSS_CTX_BACKUP), "SetContext(BACKUP)", trace);
            Step(bc.SetBackupState(false, false, VSS_BT_COPY, false), "SetBackupState(COPY)", trace);
            if (withWriters)
            {
                Wait(bc.GatherWriterMetadata(out var gather), gather, "GatherWriterMetadata", ct, trace, TimeSpan.FromSeconds(90));
                var hrCount = bc.GetWriterMetadataCount(out var writers);
                trace.Add($"GetWriterMetadataCount: {VssAsyncWait.Hex(hrCount)}, {writers} writer(s)");
                session.Writers = (int)writers;
            }
            Step(bc.StartSnapshotSet(out var setId), "StartSnapshotSet", trace);
            session.SetId = setId;
            var ids = new List<(Guid Id, string Volume)>();
            foreach (var v in volumes)
            {
                Step(bc.AddToSnapshotSet(v, Guid.Empty, out var sid), $"AddToSnapshotSet({v})", trace);
                trace.Add($"  snapshot id {sid}");
                ids.Add((sid, v));
            }
            Wait(bc.PrepareForBackup(out var prep), prep, "PrepareForBackup", ct, trace);
            Wait(bc.DoSnapshotSet(out var doSet), doSet, "DoSnapshotSet", ct, trace);
            // the writer metadata stays valid until the snapshot set is done (CR-002 A1. hypothesis 5)
            if (withWriters) trace.Add($"FreeWriterMetadata: {VssAsyncWait.Hex(bc.FreeWriterMetadata())}");
            foreach (var (sid, v) in ids)
            {
                // the snapshot record of a fresh set may lag a moment behind DoSnapshotSet on a busy system: retry before giving up
                string? device = null;
                for (var attempt = 1; attempt <= 5 && device is null; attempt++)
                {
                    device = DeviceObject(bc, sid, trace, raw);
                    if (device is null && attempt < 5) Thread.Sleep(500);
                }
                if (device is null)
                    throw new VssSnapshotIncompleteException($"snapshot {sid} of {v} has no device object", trace);
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

    /// <summary>
    /// <c>bf diag vss</c> (CR-002 A1.): takes one snapshot of the volume of <paramref name="path"/>, prints every step and
    /// reads the first 4 KB of the first file under <paramref name="path"/> from the snapshot, then deletes the snapshot.
    /// Exit code 0 = device path present and readable, 1 otherwise.
    /// </summary>
    public int Diagnose(string path, bool withWriters, TextWriter o, CancellationToken ct = default)
    {
        var trace = new List<string>();
        o.WriteLine($"process elevated: {Environment.IsPrivilegedProcess}");
        Session? session = null;
        try
        {
            session = CreateOnce([path], withWriters, ct, trace, line => trace.Add(line));
            foreach (var t in trace) o.WriteLine(t);
            trace.Clear();
            var file = FirstFile(path);
            if (file is null)
            {
                o.WriteLine($"no file under {path} to read back — snapshot device present, read test skipped");
                return 0;
            }
            var inSnapshot = session.Translate(file);
            using var fs = new FileStream(inSnapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[4096];
            var n = fs.Read(buf, 0, buf.Length);
            o.WriteLine($"read {n} byte(s) of {file} from {inSnapshot}");
            o.WriteLine("RESULT: ok");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var t = ex switch { VssStepException v => v.Trace, VssSnapshotIncompleteException v => v.Trace, _ => trace };
            foreach (var line in t) o.WriteLine(line);
            o.WriteLine($"RESULT: failed — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            session?.Dispose();
            o.WriteLine("cleanup: snapshot set deleted");
        }
    }

    private static string? FirstFile(string path)
    {
        if (File.Exists(path)) return path;
        try
        {
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
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

    private const int VSS_SS_CREATED = 12;

    /// <summary>GetSnapshotProperties → the device path, or null (everything that was seen goes to the trace).</summary>
    private static string? DeviceObject(IVssBackupComponents bc, Guid snapshotId, List<string> trace, Action<string>? raw)
    {
        var size = Marshal.SizeOf<VssSnapshotProp>();
        var buf = Marshal.AllocHGlobal(size);
        var filled = false;
        try
        {
            unsafe { new Span<byte>((void*)buf, size).Clear(); }
            var hr = bc.GetSnapshotProperties(snapshotId, buf);
            trace.Add($"GetSnapshotProperties({snapshotId}): {VssAsyncWait.Hex(hr)} {VssException.Describe(hr)}");
            if (hr < 0) return null;
            filled = true;
            if (raw is not null)
            {
                var bytes = new byte[size];
                Marshal.Copy(buf, bytes, 0, size);
                raw($"  VSS_SNAPSHOT_PROP ({size} bytes): {Convert.ToHexString(bytes)}");
            }
            var prop = Marshal.PtrToStructure<VssSnapshotProp>(buf);
            var device = Marshal.PtrToStringUni(prop.SnapshotDeviceObject);
            trace.Add($"  SnapshotId {prop.SnapshotId} ({(prop.SnapshotId == snapshotId ? "matches" : prop.SnapshotId == Guid.Empty ? "EMPTY — struct not filled" : "DIFFERS")}), " +
                      $"count {prop.SnapshotsCount}, status {prop.Status}{(prop.Status == VSS_SS_CREATED ? " (CREATED)" : "")}, " +
                      $"original volume {Marshal.PtrToStringUni(prop.OriginalVolumeName) ?? "-"}, device {device ?? "NONE"}");
            return string.IsNullOrEmpty(device) ? null : device;
        }
        finally
        {
            // free the strings VSS allocated inside the struct on every path (was only on success)
            if (filled) VssFreeSnapshotPropertiesInternal(buf);
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

    /// <summary>
    /// Must run before anything else in the process touches COM (service Main, test module initializer): VSS writers call
    /// back into the requester, which fails (GatherWriterMetadata never completes) with the default COM security.
    /// </summary>
    public static void InitializeComSecurity() => EnsureComSecurity();

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

    private static void Step(int hr, string what, List<string> trace)
    {
        trace.Add($"{what}: {VssAsyncWait.Hex(hr)}");
        if (hr < 0) throw new VssStepException($"{what} failed: {VssAsyncWait.Hex(hr)} {VssException.Describe(hr)}", hr, trace);
    }

    private static void Wait(int hr, IVssAsync? op, string what, CancellationToken ct, List<string> trace, TimeSpan? limit = null)
    {
        Step(hr, what, trace);
        if (op is null) return;
        try
        {
            VssAsyncWait.Run(new AsyncAdapter(op), what, limit ?? AsyncLimit, ct, trace);
        }
        catch (VssStepException ex)
        {
            throw new VssStepException($"{ex.Message} {VssException.Describe(ex.Code)}".TrimEnd(), ex.Code, trace);
        }
        finally
        {
            Marshal.ReleaseComObject(op);
        }
    }

    private sealed class AsyncAdapter(IVssAsync op) : IVssAsyncOperation
    {
        public int Cancel() => op.Cancel();
        public int Wait(uint milliseconds) => op.Wait(milliseconds);
        public int QueryStatus(out int status) => op.QueryStatus(out status, IntPtr.Zero);
    }

    private sealed class Session(IVssBackupComponents bc, List<string> trace) : ISnapshotSession
    {
        public List<string> Trace { get; } = trace;
        public int Writers { get; set; }
        private readonly List<(string SnapshotId, string? SetId, string Volume)> _snapshots = [];
        private readonly List<(string Volume, string Device)> _map = [];
        private bool _disposed;
        public Guid SetId { get; set; }

        public IReadOnlyList<(string SnapshotId, string? SetId, string Volume)> Snapshots => _snapshots;
        public List<string> WritersSkippedReasons { get; } = [];
        IReadOnlyList<string> ISnapshotSession.WritersSkippedReasons => WritersSkippedReasons;

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
