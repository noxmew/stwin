using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Commands;
using STWin.Infrastructure;
using STWin.Updates;
using STWin.Windows;

namespace STWin.Admin;

internal sealed class AdminServiceHost : ServiceBase
{
    public const string Argument = "--admin-service";
    private readonly string sid;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connections = new(3, 3);
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim updates = new(1, 1);
    private NamedPipeServerStream? listener;
    private long lastActivity = Environment.TickCount64;
    private int active;
    private bool maintaining;

    private AdminServiceHost(string ownerSid)
    {
        sid = ServiceRegistration.ValidateSid(ownerSid);
        ServiceName = ServiceRegistration.Name(sid);
        CanStop = true;
        AutoLog = false;
    }

    public static int Run(string sid)
    {
        try { using var service = new AdminServiceHost(sid); ServiceBase.Run(service); return 0; }
        catch (Exception ex) { Settings.Log($"管理员服务失败：{ex.Message}"); return 1; }
    }

    protected override void OnStart(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem || !string.Equals(Environment.ProcessPath, ServiceRegistration.Executable(sid), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("管理员服务必须从受保护目录以系统身份运行。");
        SessionProcess.AllowInspection(sid);
        listener = NewListener();
        _ = AcceptAsync();
        _ = IdleAsync();
        _ = AdminMaintenance.CleanupAsync(sid);
    }

    private NamedPipeServerStream NewListener() => AdminInstallation.UserPipe(ServiceRegistration.PipeName(sid), sid, instances: 4);

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await connections.WaitAsync(lifetime.Token).ConfigureAwait(false);
                var pipe = listener!;
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref active);
                    Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                    listener = NewListener();
                }
                catch { connections.Release(); pipe.Dispose(); throw; }
                _ = HandleAsync(pipe);
            }
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception ex) { Settings.Log($"管理员服务通信失败：{ex.Message}"); Stop(); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        AdminOperation? operation = null;
        var committed = false;
        try
        {
            using (pipe)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(75));
                var clientId = SessionProcess.PipeClientProcessId(pipe);
                using var client = Process.GetProcessById(checked((int)clientId));
                _ = client.SafeHandle;
                var parentSession = SessionProcess.SessionId(clientId);
                if (parentSession == 0 || SessionProcess.UserSid(clientId) != sid)
                    throw new UnauthorizedAccessException("管理员后台只接受已授权用户的本机登录会话请求。");
                var request = await CommandPipe.ReadAsync<AdminRequest>(pipe, CommandPipe.MaximumRequest, timeout.Token).ConfigureAwait(false);
                if (request.Name == "update") timeout.CancelAfter(TimeSpan.FromMinutes(10));
                // Cancelling/disconnecting a caller also cancels unapproved launches and downloads.
                var disconnected = pipe.ReadAsync(new byte[1], timeout.Token).AsTask();
                var action = ExecuteAsync(request, parentSession, timeout.Token);
                try
                {
                    if (await Task.WhenAny(action, disconnected).ConfigureAwait(false) == disconnected) timeout.Cancel();
                    operation = await action.ConfigureAwait(false);
                }
                catch (Exception ex) { operation = new(CommandReply.Failure(ex)); }
                // The caller pins and authenticates maintenance processes before sending a one-byte ACK.
                await CommandPipe.WriteAsync(pipe, operation.Reply, CommandPipe.MaximumReply, timeout.Token).ConfigureAwait(false);
                if (await disconnected.ConfigureAwait(false) != 1) throw new IOException("管理员后台回复未被接收。");
                operation.Commit?.Invoke();
                committed = true;
                timeout.Cancel();
            }
        }
        catch (Exception ex) { Settings.Log($"管理员请求结束：{ex.Message}"); }
        finally
        {
            if (!committed) operation?.Cancel?.Invoke();
            if (!committed && operation?.Commit is not null) maintaining = false;
            Interlocked.Decrement(ref active);
            Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
            connections.Release();
        }
    }

    private async Task<AdminOperation> ExecuteAsync(AdminRequest request, uint parentSession, CancellationToken token)
    {
        if (request.Name == "status") return new(new CommandReply(true, "管理员支持已启用。",
            JsonSerializer.SerializeToElement(new { installed = true, running = true, version = UpdateRelease.CurrentVersionText, autoStart = false })));
        if (request.Name == "update")
        {
            if (!Version.TryParse(request.Version, out var version)) throw new ArgumentException("后台更新版本无效。");
            if (version <= UpdateRelease.CurrentVersion)
                return new(new CommandReply(true, "管理员后台无需更新。", JsonSerializer.SerializeToElement(new { restarting = false })));
            if (!await updates.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("管理员后台已有更新请求。");
            try
            {
                var stage = await AdminMaintenance.DownloadAsync(sid, request.Version!, token).ConfigureAwait(false);
                await operations.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (maintaining) throw new InvalidOperationException("管理员后台正在更新或移除。");
                    var result = await AdminMaintenance.PrepareUpdateAsync(sid, stage, Stop, token).ConfigureAwait(false);
                    maintaining = true;
                    return result;
                }
                finally { operations.Release(); }
            }
            finally { updates.Release(); }
        }
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (maintaining) throw new InvalidOperationException("管理员后台正在更新或移除。");
            switch (request.Name)
            {
                case "enable":
                    SessionApi.Enable();
                    return new(new CommandReply(true, "桌面分身能力已启用。"));
                case "launch" when request.Launch is not null:
                case "desktop" when request.Desktop is not null:
                    return new(await AdminSessionWorker.ExecuteAsync(sid, parentSession, request, token).ConfigureAwait(false));
                case "remove" when request.CleanupPipe is not null:
                    if (updates.CurrentCount == 0) throw new InvalidOperationException("后台正在下载更新，请完成后再移除。");
                    var result = await AdminMaintenance.PrepareRemoveAsync(sid, request.CleanupPipe, Stop, token).ConfigureAwait(false);
                    maintaining = true;
                    return result;
                default: throw new ArgumentException("管理员后台请求无效。");
            }
        }
        finally { operations.Release(); }
    }

    private async Task IdleAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false);
                if (Volatile.Read(ref active) == 0 && Environment.TickCount64 - Interlocked.Read(ref lastActivity) >= 60_000)
                { Stop(); return; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    protected override void OnStop()
    {
        lifetime.Cancel();
        listener?.Dispose();
    }
}
