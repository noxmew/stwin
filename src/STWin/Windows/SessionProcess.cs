using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using STWin.Infrastructure;
using STWin.Sessions;

namespace STWin.Windows;

internal sealed class SessionProcess : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly SafeWaitHandle thread;
    private bool started;
    public LaunchedProgram Information { get; }

    private SessionProcess(SafeProcessHandle process, SafeWaitHandle thread, LaunchedProgram information)
    { this.process = process; this.thread = thread; Information = information; }

    public static SessionProcess Prepare(ProgramLaunchRequest request)
    {
        if (request.Elevated && !IsElevated((uint)Environment.ProcessId))
            throw new UnauthorizedAccessException("启动助手未获得管理员权限，已取消启动。");
        var directory = request.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"工作目录不存在：{directory}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
        // The program inherits the helper's token in the child session, including requested elevation.
        if (!CreateProcessW(null, new StringBuilder(CommandLine(request.FileName, request.Arguments)), IntPtr.Zero,
            IntPtr.Zero, false, 4, IntPtr.Zero, directory, ref startup, out var created)) // CREATE_SUSPENDED
            throw Error("在分身内创建程序失败");
        var process = new SafeProcessHandle(created.Process, ownsHandle: true);
        var thread = new SafeWaitHandle(created.Thread, ownsHandle: true);
        try
        {
            var sessionId = SessionId(created.ProcessId);
            if (sessionId != request.SessionId) throw new InvalidOperationException("程序不在指定分身会话中，已取消启动。");
            var elevated = IsElevated(process);
            if (request.Elevated && !elevated) throw new UnauthorizedAccessException("程序未获得管理员权限，已取消启动。");
            var fileName = ImagePath(process);
            if (!GetProcessTimes(process, out var creation, out _, out _, out _)) throw Error("读取程序创建时间失败");
            var information = new LaunchedProgram(checked((int)created.ProcessId), sessionId, fileName, request.Arguments,
                directory, new DateTimeOffset(DateTime.FromFileTimeUtc(creation)), elevated);
            return new(process, thread, information);
        }
        catch
        {
            TerminateProcess(process, 1);
            thread.Dispose();
            process.Dispose();
            throw;
        }
    }

    public void Start()
    {
        var previous = ResumeThread(thread);
        if (previous == uint.MaxValue) throw Error("启动分身内程序失败");
        if (previous != 1) throw new InvalidOperationException("程序线程状态已变化，已取消启动。");
        started = true;
    }

    public void Dispose()
    {
        // An interrupted request cannot leave an unapproved suspended program behind.
        if (!started && !TerminateProcess(process, 1)) Settings.Log(Error("清理未启动的程序失败").Message);
        thread.Dispose();
        process.Dispose();
    }

    public static uint SessionId(uint processId)
    {
        // ProcessIdToSessionId requires QUERY_INFORMATION, which a normal helper cannot
        // obtain for its elevated parent. TokenSessionId only needs the inspection rights we grant.
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw Error("读取进程会话失败");
        if (!OpenProcessToken(process, 8, out var token)) throw Error("读取进程会话令牌失败"); // TOKEN_QUERY
        using (token)
        {
            if (!GetTokenInformation(token, 12, out var sessionId, sizeof(int), out _)) // TokenSessionId
                throw Error("读取进程会话失败");
            return unchecked((uint)sessionId);
        }
    }

    public static string ImagePath(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw Error("读取启动助手进程失败");
        return ImagePath(process);
    }

    public static bool IsElevated(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw Error("读取启动助手进程失败");
        return IsElevated(process);
    }

    public static string UserSid(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) throw Error("读取进程用户失败");
        if (!OpenProcessToken(process, 8, out var token)) throw Error("读取进程令牌失败");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new InvalidOperationException("进程没有用户 SID。");
    }

    internal static SafeProcessHandle OpenForInspection(uint processId)
    {
        var process = OpenProcess(0x101000, false, processId);
        if (!process.IsInvalid) return process;
        var error = Error("读取后台进程失败");
        process.Dispose();
        throw error;
    }

    internal static async Task<int> WaitForExitAsync(SafeProcessHandle process, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var state = WaitForSingleObject(process, 0);
            if (state == 0) break;
            if (state != 258) throw Error("等待后台更新失败");
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        if (!GetExitCodeProcess(process, out var code)) throw Error("读取后台更新结果失败");
        return checked((int)code);
    }

    internal static async Task WaitForConnectionAsync(NamedPipeServerStream pipe, SafeProcessHandle process, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var connection = pipe.WaitForConnectionAsync(wait.Token);
        var exited = WaitForExitAsync(process, wait.Token);
        try
        {
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) == exited && !pipe.IsConnected)
            {
                var code = await exited.ConfigureAwait(false);
                throw new InvalidOperationException($"分身桌面助手在建立连接前退出（退出码 {code}）。");
            }
            await connection.ConfigureAwait(false);
        }
        finally
        {
            wait.Cancel();
            try { await Task.WhenAll(connection, exited).ConfigureAwait(false); }
            catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        }
    }

    internal static void AllowInspection(string userSid, int processId = 0)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new UnauthorizedAccessException("只有系统后台可以设置进程查询权限。");
        using var current = processId == 0 ? System.Diagnostics.Process.GetCurrentProcess() : System.Diagnostics.Process.GetProcessById(processId);
        SetInspectionAcl(current.Handle, userSid, "0x00101000"); // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (!OpenProcessToken(current.SafeHandle, 0x40008, out var token)) throw Error("打开后台令牌失败");
        using (token) SetInspectionAcl(token.DangerousGetHandle(), userSid, "0x00000008"); // TOKEN_QUERY only
    }

    private static void SetInspectionAcl(IntPtr handle, string sid, string rights)
    {
        var descriptorText = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;{rights};;;{new SecurityIdentifier(sid).Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptorText, 1, out var descriptor, out _))
            throw Error("创建后台查询权限失败");
        try { if (!SetKernelObjectSecurity(handle, 4, descriptor)) throw Error("设置后台查询权限失败"); }
        finally { LocalFree(descriptor); }
    }

    private static bool IsElevated(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw Error("读取进程令牌失败"); // TOKEN_QUERY
        using (token)
        {
            if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _)) // TokenElevation
                throw Error("读取进程提权状态失败");
            return elevated != 0;
        }
    }

    private static string ImagePath(SafeProcessHandle process)
    {
        var size = 32768u;
        var path = new StringBuilder((int)size);
        if (!QueryFullProcessImageNameW(process, 0, path, ref size)) throw Error("读取程序路径失败");
        return path.ToString();
    }

    public static uint PipeClientProcessId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id)) throw Error("验证启动助手失败");
        return id;
    }

    public static uint PipeServerProcessId(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id)) throw Error("验证 STWin 进程失败");
        return id;
    }

    public static string CommandLine(string fileName, string[] arguments)
    {
        var command = new StringBuilder(Quote(fileName));
        foreach (var argument in arguments) command.Append(' ').Append(Quote(argument));
        return command.ToString();
    }

    // Windows argv quoting preserves empty arguments, quotes and trailing backslashes.
    private static string Quote(string value)
    {
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            output.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            output.Append(character);
            slashes = 0;
        }
        output.Append('\\', slashes * 2).Append('"');
        return output.ToString();
    }

    private static Win32Exception Error(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation}（Windows {code}）：{new Win32Exception(code).Message}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCharacters, YCharacters, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr security, out uint length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information,
        int informationLength, out int returnLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder fileName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
