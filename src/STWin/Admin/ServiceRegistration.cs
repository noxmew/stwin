using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using STWin.Windows;

namespace STWin.Admin;

internal static class ServiceRegistration
{
    private const uint QueryConfig = 1, QueryStatus = 4, Start = 16;
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "STWin");
    public static string ValidateSid(string value)
    {
        var sid = new SecurityIdentifier(value);
        if (!sid.IsAccountSid()) throw new ArgumentException("管理员支持的用户 SID 无效。");
        return sid.Value;
    }
    public static string Name(string sid) => $"STWin.Admin.{ValidateSid(sid)}";
    public static string Folder(string sid) => Path.Combine(Root, ValidateSid(sid));
    public static string Executable(string sid) => Path.Combine(Folder(sid), "STWin.exe");
    public static string PipeName(string sid) => $"{Name(sid)}.commands";
    private static string BinaryPath(string sid) => SessionProcess.CommandLine(Executable(sid), [AdminServiceHost.Argument, ValidateSid(sid)]);

    public static bool IsInstalled(string sid)
    {
        using var service = Open(sid, QueryConfig, true);
        if (service is null) return false;
        Validate(service, sid);
        return true;
    }

    public static bool IsRunning(string sid)
    {
        using var service = Open(sid, QueryConfig | QueryStatus, true);
        if (service is null) return false;
        Validate(service, sid);
        return Status(service).State == 4;
    }

    public static void Install(string sid)
    {
        using var manager = OpenSCManagerW(null, null, 3);
        if (manager.IsInvalid) throw Error();
        using var service = CreateServiceW(manager, Name(sid), "STWin 管理员支持", 0xF01FF,
            0x10, 3, 1, BinaryPath(sid), null, IntPtr.Zero, null, null, null);
        if (service.IsInvalid) throw Error();
        try
        {
            // The account may query/start, but cannot replace the service executable or change its identity.
            var sddl = $"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCRP;;;{ValidateSid(sid)})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _)) throw Error();
            try { if (!SetServiceObjectSecurity(service, 5, descriptor)) throw Error(); }
            finally { LocalFree(descriptor); }
        }
        catch { DeleteService(service); throw; }
    }

    public static Task StartAsync(string sid, CancellationToken token) => Task.Run(async () =>
    {
        using var service = Open(sid, QueryConfig | QueryStatus | Start, false)!;
        Validate(service, sid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (Status(service).State == 3) await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        if (Status(service).State == 1 && !StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
            throw Error();
        while (true)
        {
            var status = Status(service);
            if (status.State == 4) return;
            if (status.State == 1) throw new InvalidOperationException($"管理员后台启动失败（Windows {status.Win32ExitCode}）。");
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }, token);

    private static ServiceHandle? Open(string sid, uint access, bool allowMissing)
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw Error();
        var service = OpenServiceW(manager, Name(sid), access);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        if (allowMissing && error == 1060) return null;
        throw new Win32Exception(error, error == 1060 ? "尚未启用管理员支持，请执行 STWin.exe admin enable。" : new Win32Exception(error).Message);
    }

    private static void Validate(ServiceHandle service, string sid)
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var required);
        if (required == 0) throw Error();
        var memory = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfigW(service, memory, required, out _)) throw Error();
            var config = Marshal.PtrToStructure<ServiceConfig>(memory);
            if (config.Type != 0x10 || config.StartType != 3 ||
                Marshal.PtrToStringUni(config.BinaryPath) != BinaryPath(sid) ||
                Marshal.PtrToStringUni(config.Account) != "LocalSystem")
                throw new InvalidOperationException("管理员服务配置不符合要求，请检查服务配置。");
            if (!File.Exists(Executable(sid))) throw new FileNotFoundException("管理员后台程序缺失。", Executable(sid));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static ServiceStatus Status(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _)) throw Error();
        return status;
    }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    {
        public uint Type, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint Tag;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public uint Type, State, Controls, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string displayName,
        uint access, uint type, uint startType, uint errorControl, string path, string? group,
        IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(ServiceHandle service, uint information, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision,
        out IntPtr descriptor, out uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr config, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, uint size, out uint required);
}
