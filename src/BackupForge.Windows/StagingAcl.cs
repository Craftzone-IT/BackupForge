using System.Security.AccessControl;
using System.Security.Principal;

namespace BackupForge.Windows;

/// <summary>SPEC 8.3: the SQL Server service account may write the job's staging folder (nothing else of ProgramData).</summary>
public sealed class StagingAcl : BackupForge.Engine.Sql.IStagingPermissions
{
    public void AllowWrite(string folder, string account)
    {
        var di = new DirectoryInfo(folder);
        var sec = di.GetAccessControl();
        var id = new NTAccount(account);
        var rule = new FileSystemAccessRule(id, FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        sec.ModifyAccessRule(AccessControlModification.Add, rule, out _);
        di.SetAccessControl(sec);
    }
}
