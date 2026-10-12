using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using STWin.Commands;
using STWin.Infrastructure;
using STWin.Updates;
using STWin.Windows;

namespace STWin.Admin;

internal sealed record AdminStage(string StageId, UpdateRelease Release);
internal sealed record AdminCleanup(string StageId, int ProcessId);
internal sealed record AdminOperation(CommandReply Reply, Action? Commit = null, Action? Cancel = null);

internal static class AdminMaintenance
{
    public const string Argument = "--admin-maintenance";
    public static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string StageFolder(string sid, string stageId)
    {
        if (!Guid.TryParseExact(stageId, "N", out _)) throw new ArgumentException("后台更新暂存目录无效。");
        return Path.Combine(ServiceRegistration.Folder(sid), "updates", stageId);
    }

    internal static async Task<AdminStage> DownloadAsync(string sid, string version, CancellationToken token)
    {
        using var process = new Process { StartInfo = Start(ServiceRegistration.Executable(sid), "download", sid, version) };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动后台更新下载进程。");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var result = await output;
            var message = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(message.Trim());
            var stage = JsonSerializer.Deserialize<AdminStage>(result) ?? throw new InvalidDataException("后台更新下载结果为空。");
            if (stage.Release.Version != version) throw new InvalidDataException("后台更新版本不匹配。");
            _ = StageFolder(sid, stage.StageId);
            return stage;
        }
        finally { UpdateService.StopProcess(process); }
    }

    internal static async Task<AdminOperation> PrepareUpdateAsync(string sid, AdminStage stage, Action stop, CancellationToken token)
    {
        var executable = Path.Combine(StageFolder(sid, stage.StageId), "STWin.exe");
        var process = new Process { StartInfo = Start(executable, "apply", sid, stage.StageId, Environment.ProcessId.ToString()) };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动后台更新安装进程。");
            if (await process.StandardOutput.ReadLineAsync(token) != "READY")
                throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(token));
            var reply = new CommandReply(true, "管理员后台正在更新。", JsonSerializer.SerializeToElement(new
            { restarting = true, maintenanceProcessId = process.Id, stageId = stage.StageId }, CommandPipe.JsonOptions));
            return new(reply, Commit: () =>
            {
                process.StandardInput.WriteLine("INSTALL");
                process.StandardInput.Flush();
                process.Dispose();
                stop();
            }, Cancel: () => { UpdateService.StopProcess(process); process.Dispose(); });
        }
        catch { UpdateService.StopProcess(process); process.Dispose(); throw; }
    }

    internal static async Task<AdminOperation> PrepareRemoveAsync(string sid, string pipeName, Action stop, CancellationToken token)
    {
        if (!AdminInstallation.ValidPipe(pipeName, "STWin.remove.")) throw new ArgumentException("后台移除通道无效。");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        // A system-owned Windows executable can clean the entire backend directory after our EXE exits.
        // Every interpolated value comes from the fixed install location, SID, PID or validated pipe GUID.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $parent = [System.Diagnostics.Process]::GetProcessById({{Environment.ProcessId}})
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', {{Quote(pipeName)}}, [System.IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(15000)
            [Console]::Out.WriteLine('READY')
            [Console]::Out.Flush()
            if ([Console]::In.ReadLine() -ne 'REMOVE') { $pipe.Dispose(); exit 1 }
            try {
                if (!$parent.WaitForExit(30000)) { throw '管理员后台未停止。' }
                $parent.Dispose()
                & {{Quote(sc)}} delete {{Quote(ServiceRegistration.Name(sid))}} | Out-Null
                if ($LASTEXITCODE -ne 0) { throw '无法删除管理员服务登记。' }
                Remove-Item -LiteralPath {{Quote(ServiceRegistration.Folder(sid))}} -Recurse -Force
                $reply = @{ ok = $true; message = '管理员支持已移除。'; exitCode = 0 }
            } catch {
                $reply = @{ ok = $false; message = ''; error = $_.Exception.Message; exitCode = 1 }
            }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes(($reply | ConvertTo-Json -Compress))
            $writer = [System.IO.BinaryWriter]::new($pipe)
            $writer.Write([int]$bytes.Length)
            $writer.Write($bytes)
            $writer.Flush()
            $pipe.Dispose()
            """;
        var start = new ProcessStartInfo(PowerShellPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动后台移除进程。");
            SessionProcess.AllowInspection(sid, process.Id);
            if (await process.StandardOutput.ReadLineAsync(token) != "READY")
                throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(token));
            return new(new CommandReply(true, "正在移除管理员支持。", JsonSerializer.SerializeToElement(new { cleanupProcessId = process.Id })),
                Commit: () =>
                {
                    process.StandardInput.WriteLine("REMOVE");
                    process.StandardInput.Flush();
                    process.Dispose();
                    stop();
                }, Cancel: () => { UpdateService.StopProcess(process); process.Dispose(); });
        }
        catch { UpdateService.StopProcess(process); process.Dispose(); throw; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem) throw new UnauthorizedAccessException("后台更新进程需要系统身份。");
            switch (args)
            {
                case [Argument, "download", var sid, var version]:
                    ServiceRegistration.ValidateSid(sid);
                    if (!string.Equals(Environment.ProcessPath, ServiceRegistration.Executable(sid), StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException("后台更新下载进程路径不匹配。");
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
                    {
                        // Obtain publisher metadata ourselves; never trust hashes or executable paths from IPC.
                        var release = await UpdateSource.ReadAsync(version, timeout.Token);
                        if (release.Validate() <= UpdateRelease.CurrentVersion) throw new InvalidOperationException("后台更新版本必须更高。");
                        var stage = new AdminStage(Guid.NewGuid().ToString("N"), release);
                        var folder = StageFolder(sid, stage.StageId);
                        AdminInstallation.ProtectedDirectory(folder, sid);
                        try
                        {
                            await UpdateSource.DownloadAsync(release, Path.Combine(folder, "STWin.exe"), timeout.Token);
                            await File.WriteAllTextAsync(Path.Combine(folder, "release.json"), JsonSerializer.Serialize(release), timeout.Token);
                            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(stage));
                        }
                        catch { Directory.Delete(folder, true); throw; }
                    }
                    break;
                case [Argument, "apply", var sid, var stageId, var pidText] when int.TryParse(pidText, out var parentId):
                    await ApplyAsync(sid, stageId, parentId);
                    break;
                default: throw new ArgumentException("后台更新参数无效。");
            }
            return 0;
        }
        catch (Exception ex) { await Console.Error.WriteLineAsync(ex.Message); return 1; }
    }

    private static async Task ApplyAsync(string sid, string stageId, int parentId)
    {
        var folder = StageFolder(sid, stageId);
        var executable = Path.Combine(folder, "STWin.exe");
        if (!string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("后台安装进程路径不匹配。");
        SessionProcess.AllowInspection(sid);
        using var parent = Process.GetProcessById(parentId);
        _ = parent.SafeHandle;
        if (SessionProcess.UserSid((uint)parentId) != "S-1-5-18" ||
            !string.Equals(SessionProcess.ImagePath((uint)parentId), ServiceRegistration.Executable(sid), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("后台安装进程的父进程不匹配。");
        var release = JsonSerializer.Deserialize<UpdateRelease>(await File.ReadAllTextAsync(Path.Combine(folder, "release.json")))
            ?? throw new InvalidDataException("后台更新信息为空。");
        var temporary = Path.Combine(ServiceRegistration.Folder(sid), $".update-{stageId}.tmp");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var source = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(destination, timeout.Token);
            await UpdateSource.VerifyAsync(temporary, release, timeout.Token);
            await Console.Out.WriteLineAsync("READY");
            await Console.Out.FlushAsync();
            if (await Task.Run(Console.In.ReadLine).WaitAsync(timeout.Token) != "INSTALL")
                throw new OperationCanceledException("后台更新已取消。");
            await parent.WaitForExitAsync(timeout.Token);
            File.Replace(temporary, ServiceRegistration.Executable(sid), null);
            await File.WriteAllTextAsync(Path.Combine(ServiceRegistration.Folder(sid), "cleanup.json"),
                JsonSerializer.Serialize(new AdminCleanup(stageId, Environment.ProcessId)), timeout.Token);
            await ServiceRegistration.StartAsync(sid, timeout.Token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task CleanupAsync(string sid)
    {
        var marker = Path.Combine(ServiceRegistration.Folder(sid), "cleanup.json");
        if (!File.Exists(marker)) return;
        try
        {
            var cleanup = JsonSerializer.Deserialize<AdminCleanup>(await File.ReadAllTextAsync(marker))
                ?? throw new InvalidDataException("后台清理信息为空。");
            var folder = StageFolder(sid, cleanup.StageId);
            try
            {
                using var process = Process.GetProcessById(cleanup.ProcessId);
                _ = process.SafeHandle;
                if (!string.Equals(SessionProcess.ImagePath((uint)cleanup.ProcessId), Path.Combine(folder, "STWin.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("后台清理进程身份已变化。");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            Directory.Delete(folder, true);
            File.Delete(marker);
        }
        catch (Exception ex) { Settings.Log($"后台更新清理失败：{ex.Message}"); }
    }

    private static ProcessStartInfo Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Argument);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }
}
