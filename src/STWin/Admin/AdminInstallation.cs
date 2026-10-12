using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using STWin.Commands;
using STWin.Windows;

namespace STWin.Admin;

internal static class AdminInstallation
{
    public const string Argument = "--admin-install";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [Argument, var pidText, var pipeName] || !uint.TryParse(pidText, out var parentId) ||
            !ValidPipe(pipeName, "STWin.install.")) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var parent = Process.GetProcessById(checked((int)parentId));
            _ = parent.SafeHandle;
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (SessionProcess.PipeServerProcessId(pipe) != parentId ||
                SessionProcess.SessionId(parentId) != SessionProcess.SessionId((uint)Environment.ProcessId) ||
                !string.Equals(SessionProcess.ImagePath(parentId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("首次授权的 STWin 进程不匹配。");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = SessionProcess.UserSid(parentId);
            if (identity.User?.Value != sid || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("首次授权需要当前登录账号的管理员身份。");
            await Task.Run(() => Install(sid), timeout.Token).ConfigureAwait(false);
            await CommandPipe.WriteAsync(pipe, new CommandReply(true, "管理员支持已启用。"), CommandPipe.MaximumRequest, timeout.Token);
            return 0;
        }
        catch (Exception ex)
        {
            if (pipe.IsConnected)
            {
                try { await CommandPipe.WriteAsync(pipe, CommandReply.Failure(ex), CommandPipe.MaximumRequest, timeout.Token); }
                catch (Exception writeError) { STWin.Infrastructure.Settings.Log($"首次授权回复失败：{writeError.Message}"); }
            }
            return 1;
        }
    }

    private static void Install(string sid)
    {
        if (ServiceRegistration.IsInstalled(sid)) throw new InvalidOperationException("管理员支持已经安装。");
        ProtectedDirectory(ServiceRegistration.Root, sid);
        var folder = ServiceRegistration.Folder(sid);
        if (Directory.Exists(folder)) throw new InvalidOperationException("管理员支持目录已存在，但服务未注册，请检查安装状态。");
        ProtectedDirectory(folder, sid);
        try
        {
            var target = ServiceRegistration.Executable(sid);
            using (var source = new FileStream(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                source.CopyTo(destination);
                source.Position = destination.Position = 0;
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(destination)))
                    throw new InvalidDataException("后台程序复制校验失败。");
            }
            ProtectFile(target, sid);
            ServiceRegistration.Install(sid);
        }
        catch { Directory.Delete(folder, true); throw; }
    }

    internal static void ProtectedDirectory(string path, string sid)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("后台安装目录不能是链接。");
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var privileged in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(privileged, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var reader = path == ServiceRegistration.Root ? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null) : new SecurityIdentifier(sid);
        security.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ProtectFile(string path, string sid)
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var privileged in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(privileged, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    internal static bool ValidPipe(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[prefix.Length..], "N", out _);

    internal static NamedPipeServerStream UserPipe(string name, string sid, bool installer = false, int instances = 1)
    {
        var user = new SecurityIdentifier(sid);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (installer) security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }
}
