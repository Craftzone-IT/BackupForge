using System.Runtime.CompilerServices;

namespace BackupForge.Integration.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (OperatingSystem.IsWindows()) BackupForge.Windows.VssSnapshotProvider.InitializeComSecurity();
    }
}
