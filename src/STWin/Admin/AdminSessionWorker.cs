using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Commands;
using STWin.Sessions;
using STWin.Windows;

namespace STWin.Admin;

internal static class AdminSessionWorker
{
    public const string Argument = "--admin-parent-worker";

    internal static async Task<CommandReply> ExecuteAsync(string sid, uint parentSession, AdminRequest request, CancellationToken token)
    {
        ValidateRequest(request);
        var name = $"STWin.parent.{Guid.NewGuid():N}";
        using var pipe = AdminInstallation.UserPipe(name, sid);
        using var task = new SessionLaunchTask(name, parentSession, sid, true, Argument);
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        var worker = SessionProcess.PipeClientProcessId(pipe);
        using var process = SessionProcess.OpenForInspection(worker);
        if (SessionProcess.SessionId(worker) != parentSession || SessionProcess.UserSid(worker) != sid ||
            !SessionProcess.IsElevated(worker) ||
            !string.Equals(SessionProcess.ImagePath(worker), ServiceRegistration.Executable(sid), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("分身管理助手身份不匹配。");
        await CommandPipe.WriteAsync(pipe, request, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
        var reply = await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumReply, token).ConfigureAwait(false);
        if (await SessionProcess.WaitForExitAsync(process, token).ConfigureAwait(false) != 0 && reply.Ok)
            throw new InvalidOperationException("分身管理助手没有正常退出，请检查进程状态，勿重复提交。");
        return reply;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [Argument, var sessionText, var name, var pidText] ||
            !uint.TryParse(sessionText, out var parentSession) || parentSession == 0 ||
            !uint.TryParse(pidText, out var serverId) || !AdminInstallation.ValidPipe(name, "STWin.parent.")) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            if (SessionProcess.SessionId((uint)Environment.ProcessId) != parentSession ||
                !SessionProcess.IsElevated((uint)Environment.ProcessId))
                throw new UnauthorizedAccessException("分身管理助手必须位于原登录会话并具有管理员权限。");
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (SessionProcess.PipeServerProcessId(pipe) != serverId || SessionProcess.SessionId(serverId) != 0 ||
                SessionProcess.UserSid(serverId) != "S-1-5-18" ||
                !string.Equals(SessionProcess.ImagePath(serverId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("分身管理助手没有连接到系统后台。");
            var request = await CommandPipe.ReadAsync<AdminRequest>(pipe, CommandPipe.MaximumRequest, timeout.Token);
            ValidateRequest(request);
            // WTSGetChildSessionId is scoped to the calling session. Run this check in the parent login,
            // never in the service's Session 0, and never trust a client-supplied session ID alone.
            var disconnected = pipe.ReadAsync(new byte[1], timeout.Token).AsTask();
            var action = request.Name == "desktop"
                ? DesktopSessionWorker.ExecuteInParentSessionAsync(request.Desktop!, timeout.Token)
                : LaunchProgramAsync(request.Launch!, timeout.Token);
            if (await Task.WhenAny(action, disconnected).ConfigureAwait(false) == disconnected) timeout.Cancel();
            var reply = await action.ConfigureAwait(false);
            await CommandPipe.WriteAsync(pipe, reply, CommandPipe.MaximumReply, timeout.Token);
            return 0;
        }
        catch (Exception ex)
        {
            STWin.Infrastructure.Settings.Log($"分身管理操作失败：{ex}");
            if (pipe.IsConnected && !timeout.IsCancellationRequested)
            {
                try { await CommandPipe.WriteAsync(pipe, CommandReply.Failure(ex), CommandPipe.MaximumReply, timeout.Token); }
                catch (Exception writeError) { STWin.Infrastructure.Settings.Log($"分身管理助手回复失败：{writeError.Message}"); }
            }
            return 1;
        }
        finally { timeout.Cancel(); }
    }

    private static void ValidateRequest(AdminRequest request)
    {
        switch (request.Name)
        {
            case "launch" when request.Launch is not null && request.Desktop is null:
                request.Launch.Validate();
                break;
            case "desktop" when request.Desktop is not null && request.Launch is null:
                request.Desktop.Validate();
                break;
            default: throw new ArgumentException("分身管理请求无效。");
        }
    }

    private static async Task<CommandReply> LaunchProgramAsync(ProgramLaunchRequest request, CancellationToken token)
    {
        var result = await ProgramLauncher.LaunchInSessionAsync(request, token).ConfigureAwait(false);
        return new(true, "程序已在分身中启动。", JsonSerializer.SerializeToElement(result, CommandPipe.JsonOptions));
    }
}
