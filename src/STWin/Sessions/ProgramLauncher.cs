using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Commands;
using STWin.Infrastructure;
using STWin.Windows;
using STWin.Admin;

namespace STWin.Sessions;

internal sealed record ProgramLaunchRequest(uint SessionId, string FileName, string[] Arguments, string? WorkingDirectory, bool Elevated)
{
    public static ProgramLaunchRequest Parse(uint sessionId, JsonElement arguments)
    {
        foreach (var property in arguments.EnumerateObject())
            if (property.Name is not ("fileName" or "arguments" or "workingDirectory" or "sessionId" or "elevated"))
                throw new ArgumentException($"不支持的启动参数：{property.Name}");
        if (!arguments.TryGetProperty("fileName", out var fileName) || fileName.ValueKind != JsonValueKind.String)
            throw new ArgumentException("fileName 必须是程序路径或名称。");
        var values = new List<string>();
        if (arguments.TryGetProperty("arguments", out var args))
        {
            if (args.ValueKind != JsonValueKind.Array) throw new ArgumentException("arguments 必须是字符串数组。");
            foreach (var argument in args.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String) throw new ArgumentException("arguments 中的每一项必须是字符串。");
                values.Add(argument.GetString()!);
            }
        }
        string? directory = null;
        if (arguments.TryGetProperty("workingDirectory", out var cwd))
        {
            if (cwd.ValueKind != JsonValueKind.String) throw new ArgumentException("workingDirectory 必须是绝对路径。");
            directory = cwd.GetString();
        }
        var elevated = false;
        if (arguments.TryGetProperty("elevated", out var elevation))
        {
            if (elevation.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("elevated 必须是布尔值。");
            elevated = elevation.GetBoolean();
        }
        var request = new ProgramLaunchRequest(sessionId, fileName.GetString()!, values.ToArray(), directory, elevated);
        request.Validate();
        return request;
    }

    public void Validate()
    {
        if (SessionId is 0 or uint.MaxValue) throw new ArgumentException("分身会话 ID 无效。");
        if (string.IsNullOrWhiteSpace(FileName) || FileName.IndexOfAny(['\0', '"']) >= 0) throw new ArgumentException("fileName 必须是程序路径或名称。");
        if (FileName.IndexOfAny(['\\', '/', ':']) >= 0 && !Path.IsPathFullyQualified(FileName))
            throw new ArgumentException("程序路径必须是绝对路径，也可使用 PATH 中的程序名称。");
        if (Arguments is null || Arguments.Length > 256) throw new ArgumentException("arguments 必须是字符串数组，最多 256 项。");
        foreach (var argument in Arguments)
            if (argument is null || argument.Contains('\0')) throw new ArgumentException("程序参数不能为 null 或含空字符。");
        if (WorkingDirectory is not null && (WorkingDirectory.Contains('\0') || !Path.IsPathFullyQualified(WorkingDirectory)))
            throw new ArgumentException("workingDirectory 必须是绝对路径。");
        if (SessionProcess.CommandLine(FileName, Arguments).Length >= 32767) throw new ArgumentException("程序命令行超过 Windows 的长度限制。");
    }
}

internal sealed record LaunchedProgram(int ProcessId, uint SessionId, string FileName, string[] Arguments,
    string WorkingDirectory, DateTimeOffset StartedAt, bool Elevated);

internal static class ProgramLauncher
{
    public const string WorkerArgument = "--launch-worker";

    public static Task<LaunchedProgram> LaunchAsync(ProgramLaunchRequest request, CancellationToken token)
        => AdminClient.LaunchAsync(request, token);

    internal static Task<LaunchedProgram> LaunchInSessionAsync(ProgramLaunchRequest request, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        request.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (SessionApi.ChildId() != request.SessionId) throw new InvalidOperationException("分身会话已变化，已取消启动。");
            var pipeName = $"STWin.launch.{Guid.NewGuid():N}";
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
            if (request.Elevated && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("分身启动助手没有获得管理员权限。");
            var security = new PipeSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            // An explicit user ACL also permits the non-elevated helper when STWin is elevated.
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            using var task = new SessionLaunchTask(pipeName, request.SessionId, sid.Value, request.Elevated);
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            var workerId = SessionProcess.PipeClientProcessId(pipe);
            using var worker = SessionProcess.OpenForInspection(workerId);
            if (SessionProcess.SessionId(workerId) != request.SessionId ||
                !string.Equals(SessionProcess.ImagePath(workerId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("启动助手不在指定分身会话中，已取消启动。");
            if (SessionApi.ChildId() != request.SessionId) throw new InvalidOperationException("分身会话已变化，已取消启动。");
            await CommandPipe.WriteAsync(pipe, request, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            var prepared = await ReadResultAsync(pipe, token).ConfigureAwait(false);
            if (prepared.SessionId != request.SessionId) throw new InvalidDataException("程序创建到了错误的会话，已取消启动。");
            if (request.Elevated && !prepared.Elevated) throw new UnauthorizedAccessException("程序未获得管理员权限，已取消启动。");
            token.ThrowIfCancellationRequested();
            if (SessionApi.ChildId() != request.SessionId) throw new InvalidOperationException("分身会话已变化，已取消启动。");
            await CommandPipe.WriteAsync(pipe, "START", CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            var result = await ReadResultAsync(pipe, token).ConfigureAwait(false);
            if (await SessionProcess.WaitForExitAsync(worker, token).ConfigureAwait(false) != 0)
                throw new InvalidOperationException("分身启动助手没有正常退出，请检查进程状态，勿重复提交。");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("等待分身内程序启动超时；请检查分身，勿自动重复提交。"); }
    });

    private static async Task<LaunchedProgram> ReadResultAsync(Stream pipe, CancellationToken token)
    {
        var reply = await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
        if (!reply.Ok)
            throw new CommandFailedException(reply.Error ?? throw new InvalidDataException("启动助手未返回错误信息。"), reply.ExitCode);
        var data = reply.Data ?? throw new InvalidDataException("启动助手未返回进程信息。");
        return data.Deserialize<LaunchedProgram>(CommandPipe.JsonOptions) ?? throw new InvalidDataException("进程信息为空。");
    }

    // Runs before GUI, CLI and MCP initialization, inside the selected interactive session.
    public static async Task<int> RunWorkerAsync(string[] arguments)
    {
        if (arguments is not [WorkerArgument, var sessionText, var pipeName, var parentText] ||
            !uint.TryParse(sessionText, out var sessionId) || !uint.TryParse(parentText, out var parentId) ||
            !pipeName.StartsWith("STWin.launch.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName["STWin.launch.".Length..], "N", out _)) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = timeout.Token;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // Connect before validation so the caller receives bootstrap failures instead of waiting for a timeout.
            await pipe.ConnectAsync(token).ConfigureAwait(false);
            if (SessionProcess.PipeServerProcessId(pipe) != parentId) throw new InvalidOperationException("启动助手连接到了错误的 STWin 进程。");
            if (SessionProcess.SessionId((uint)Environment.ProcessId) != sessionId ||
                SessionProcess.SessionId(parentId) == sessionId)
                throw new InvalidOperationException("启动助手的分身会话不匹配。");
            var request = await CommandPipe.ReadAsync<ProgramLaunchRequest>(pipe, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            request.Validate();
            if (request.SessionId != sessionId) throw new InvalidOperationException("启动请求的分身会话不匹配。");
            using var program = SessionProcess.Prepare(request);
            var reply = new CommandReply(true, "程序已创建，等待启动。", JsonSerializer.SerializeToElement(program.Information, CommandPipe.JsonOptions));
            await CommandPipe.WriteAsync(pipe, reply, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            var approval = await CommandPipe.ReadAsync<string>(pipe, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            if (approval != "START") throw new InvalidOperationException("程序启动已取消。");
            token.ThrowIfCancellationRequested();
            program.Start();
            reply = reply with { Message = "程序已启动。" };
            await CommandPipe.WriteAsync(pipe, reply, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Settings.Log($"分身内启动程序失败：{ex}");
            if (pipe.IsConnected)
            {
                try { await CommandPipe.WriteAsync(pipe, CommandReply.Failure(ex), CommandPipe.MaximumRequest, token).ConfigureAwait(false); }
                catch (Exception writeError) { Settings.Log($"启动助手回复失败：{writeError.Message}"); }
            }
            return CommandReply.Failure(ex).ExitCode;
        }
    }
}

internal sealed class SessionLaunchTask : IDisposable
{
    private readonly List<object> objects = new();
    private dynamic? folder;
    private readonly string name;
    private bool registered;

    public SessionLaunchTask(string name, uint sessionId, string userSid, bool elevated, string workerArgument = ProgramLauncher.WorkerArgument)
    {
        this.name = name;
        try
        {
            dynamic service = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!);
            service.Connect();
            folder = Keep(service.GetFolder("\\"));
            dynamic definition = Keep(service.NewTask(0));
            dynamic principal = Keep(definition.Principal);
            principal.UserId = userSid;
            principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN; no credentials are stored.
            principal.RunLevel = elevated ? 1 : 0; // TASK_RUNLEVEL_HIGHEST / TASK_RUNLEVEL_LUA
            dynamic settings = Keep(definition.Settings);
            settings.Enabled = true;
            settings.Hidden = true;
            settings.AllowDemandStart = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";
            dynamic actions = Keep(definition.Actions);
            dynamic action = Keep(actions.Create(0)); // TASK_ACTION_EXEC
            action.Path = Environment.ProcessPath!;
            action.Arguments = $"{workerArgument} {sessionId} {name} {Environment.ProcessId}";
            action.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            dynamic task = Keep(folder.RegisterTaskDefinition(name, definition, 2, userSid, null, 3, null));
            registered = true;
            // https://learn.microsoft.com/windows/win32/taskschd/registeredtask-runex
            Keep(task.RunEx(null, 4, checked((int)sessionId), null)); // TASK_RUN_USE_SESSION_ID
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            Dispose();
            // Keep the permission error as the base exception so the CLI returns exit code 5.
            throw new UnauthorizedAccessException("Windows 拒绝在分身中启动程序，请检查管理员后台及当前登录会话。");
        }
        catch { Dispose(); throw; }
    }

    private object Keep(object value) { objects.Add(value); return value; }

    public void Dispose()
    {
        if (registered)
        {
            try { folder!.DeleteTask(name, 0); registered = false; }
            catch (COMException ex) { Settings.Log($"删除临时启动任务失败：{ex.Message}"); }
        }
        for (var i = objects.Count - 1; i >= 0; i--)
            if (Marshal.IsComObject(objects[i])) Marshal.ReleaseComObject(objects[i]);
        objects.Clear();
    }
}
