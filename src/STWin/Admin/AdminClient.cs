using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Commands;
using STWin.Infrastructure;
using STWin.Sessions;
using STWin.Updates;
using STWin.Windows;
using Microsoft.Win32.SafeHandles;

namespace STWin.Admin;

internal sealed record AdminRequest(string Name, ProgramLaunchRequest? Launch = null, string? Version = null, string? CleanupPipe = null);

internal static class AdminClient
{
    private static readonly SemaphoreSlim setupGate = new(1, 1);
    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确定 Windows 用户。");
    public static bool Installed => ServiceRegistration.IsInstalled(UserSid);

    public static async Task<bool> InitializeAsync(bool explicitRequest, CancellationToken token, bool synchronize = true)
    {
        await setupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Installed)
            {
                var settings = Settings.Load();
                if (settings.AdminSetupAttempted && !explicitRequest) return false;
                // An automatic authorization attempt happens once, including when the user declines it.
                settings.AdminSetupAttempted = true;
                settings.Save();
                var name = $"STWin.install.{Guid.NewGuid():N}";
                using var pipe = AdminInstallation.UserPipe(name, UserSid, installer: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add(AdminInstallation.Argument);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                start.ArgumentList.Add(name);
                using var process = await Task.Run(() => Process.Start(start), token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("无法启动首次授权。");
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                var installer = SessionProcess.PipeClientProcessId(pipe);
                if (installer != process.Id || !SessionProcess.IsElevated(installer) ||
                    !string.Equals(SessionProcess.ImagePath(installer), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("首次授权助手身份不匹配。");
                CheckReply(await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumRequest, timeout.Token));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode != 0 || !Installed) throw new InvalidOperationException("管理员支持没有完成安装。");
            }
            if (synchronize) await SynchronizeAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("首次管理员授权已取消，可在主窗口点击“启用管理员支持”重试。", token); }
        finally { setupGate.Release(); }
    }

    internal static async Task SynchronizeAsync(CancellationToken token)
    {
        if (!Installed) return;
        var version = Version.Parse(FileVersionInfo.GetVersionInfo(ServiceRegistration.Executable(UserSid)).FileVersion!);
        if (UpdateRelease.Normalize(version) < UpdateRelease.CurrentVersion)
            await UpdateAsync(UpdateRelease.CurrentVersionText, token).ConfigureAwait(false);
    }

    public static async Task<LaunchedProgram> LaunchAsync(ProgramLaunchRequest request, CancellationToken token)
    {
        var reply = await SendAsync(new("launch", request), token).ConfigureAwait(false);
        return reply.Data?.Deserialize<LaunchedProgram>(CommandPipe.JsonOptions) ?? throw new InvalidDataException("后台未返回进程信息。");
    }

    public static async Task EnableSessionsAsync(CancellationToken token)
    {
        if (!SessionApi.IsEnabled()) await SendAsync(new("enable"), token).ConfigureAwait(false);
    }

    public static async Task UpdateAsync(string version, CancellationToken token)
    {
        if (!Installed) return;
        SafeProcessHandle? maintenance = null;
        try
        {
            var reply = await SendAsync(new("update", Version: version), token, result =>
            {
                if (result.Data?.GetProperty("restarting").GetBoolean() != true) return;
                var id = result.Data.Value.GetProperty("maintenanceProcessId").GetUInt32();
                var stage = result.Data.Value.GetProperty("stageId").GetString()!;
                if (!Guid.TryParseExact(stage, "N", out _)) throw new InvalidDataException("后台更新进程目录无效。");
                maintenance = SessionProcess.OpenForInspection(id);
                if (SessionProcess.UserSid(id) != "S-1-5-18" || !string.Equals(SessionProcess.ImagePath(id),
                    Path.Combine(ServiceRegistration.Folder(UserSid), "updates", stage, "STWin.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("后台更新进程身份不匹配。");
            }).ConfigureAwait(false);
            if (reply.Data?.GetProperty("restarting").GetBoolean() != true) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            if (await SessionProcess.WaitForExitAsync(maintenance!, timeout.Token).ConfigureAwait(false) != 0)
                throw new InvalidOperationException("管理员后台更新未完成，请查看后台状态。");
            var status = await SendAsync(new("status"), timeout.Token).ConfigureAwait(false);
            if (status.Data?.GetProperty("version").GetString() != version)
                throw new InvalidOperationException("管理员后台更新后的版本不匹配。");
        }
        finally { maintenance?.Dispose(); }
    }

    public static async Task RemoveAsync(CancellationToken token)
    {
        var settings = Settings.Load();
        settings.AdminSetupAttempted = true;
        settings.Save();
        if (!Installed) return;
        var name = $"STWin.remove.{Guid.NewGuid():N}";
        using var pipe = AdminInstallation.UserPipe(name, UserSid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var connected = pipe.WaitForConnectionAsync(timeout.Token);
        await SendAsync(new("remove", CleanupPipe: name), timeout.Token, reply =>
        {
            connected.GetAwaiter().GetResult();
            var processId = SessionProcess.PipeClientProcessId(pipe);
            if (processId != reply.Data?.GetProperty("cleanupProcessId").GetUInt32() ||
                SessionProcess.UserSid(processId) != "S-1-5-18" ||
                !string.Equals(SessionProcess.ImagePath(processId), AdminMaintenance.PowerShellPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("后台移除进程身份不匹配。");
        }).ConfigureAwait(false);
        CheckReply(await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumRequest, timeout.Token));
        if (ServiceRegistration.IsInstalled(UserSid) || Directory.Exists(ServiceRegistration.Folder(UserSid)))
            throw new InvalidOperationException("管理员后台没有完全移除。");
    }

    internal static async Task<CommandReply> SendAsync(AdminRequest request, CancellationToken token, Action<CommandReply>? beforeAcknowledge = null)
    {
        if (!Installed) throw new UnauthorizedAccessException("尚未启用管理员支持，请在主窗口点击“启用管理员支持”，或执行 STWin.exe admin enable。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Name == "update" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(75));
        await ServiceRegistration.StartAsync(UserSid, timeout.Token).ConfigureAwait(false);
        using var pipe = new NamedPipeClientStream(".", ServiceRegistration.PipeName(UserSid), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var server = SessionProcess.PipeServerProcessId(pipe);
        if (SessionProcess.SessionId(server) != 0 || SessionProcess.UserSid(server) != "S-1-5-18" ||
            !string.Equals(SessionProcess.ImagePath(server), ServiceRegistration.Executable(UserSid), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("管理员后台身份不匹配。");
        await CommandPipe.WriteAsync(pipe, request, CommandPipe.MaximumRequest, timeout.Token).ConfigureAwait(false);
        var reply = CheckReply(await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumRequest, timeout.Token).ConfigureAwait(false));
        beforeAcknowledge?.Invoke(reply);
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        return reply;
    }

    private static CommandReply CheckReply(CommandReply reply)
    {
        if (!reply.Ok) throw new CommandFailedException(reply.Error ?? "管理员后台操作失败。", reply.ExitCode);
        return reply;
    }
}
