using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Infrastructure;

namespace STWin.Updates;

internal sealed class UpdateService : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim workerGate = new(1, 1);
    private readonly string executable = Environment.ProcessPath!;
    private readonly object processGate = new();
    private Process? activeWorker;
    private FileStream? downloadLease;
    private bool started;
    public event Action<UpdateRelease>? UpdateAvailable;

    public void Start()
    {
        if (started) return;
        started = true;
        _ = Task.Run(MonitorAsync);
    }

    private async Task MonitorAsync()
    {
        Version offered = UpdateRelease.CurrentVersion;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    var result = await RunWorkerAsync(TimeSpan.FromSeconds(30), "check");
                    var release = JsonSerializer.Deserialize<UpdateRelease>(result);
                    if (release is not null && release.Validate() > offered)
                    {
                        offered = release.Validate();
                        UpdateAvailable?.Invoke(release);
                    }
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                { Settings.Log($"检查更新失败：{ex.Message}"); }
                await Task.Delay(TimeSpan.FromHours(6), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release)
    {
        var stageId = Guid.NewGuid().ToString("N");
        var output = await RunWorkerAsync(TimeSpan.FromMinutes(10), "download", JsonSerializer.Serialize(release), stageId);
        var download = JsonSerializer.Deserialize<DownloadedUpdate>(output) ?? throw new InvalidDataException("更新进程未返回下载信息。");
        if (download.Release != release || download.StageId != stageId) throw new InvalidDataException("更新下载结果不一致。");
        await Task.Run(() =>
        {
            // Keep a pending update intact until the user chooses to restart or exits.
            var lease = new FileStream(download.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            lock (processGate)
            {
                if (lifetime.IsCancellationRequested)
                {
                    lease.Dispose();
                    lifetime.Token.ThrowIfCancellationRequested();
                }
                downloadLease?.Dispose();
                downloadLease = lease;
            }
        });
        return download;
    }

    private Task<string> RunWorkerAsync(TimeSpan deadline, params string[] arguments) => Task.Run(async () =>
    {
        await workerGate.WaitAsync(lifetime.Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(deadline);
            using var process = new Process { StartInfo = WorkerStart(executable, arguments) };
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException("无法启动更新进程。");
                lock (processGate)
                {
                    activeWorker = process;
                    if (lifetime.IsCancellationRequested) StopProcess(process);
                }
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var result = await output;
                var message = await error;
                if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "更新进程执行失败。" : message.Trim());
                return result;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            { throw new TimeoutException("更新请求超时，请稍后再试。"); }
            finally
            {
                lock (processGate)
                {
                    activeWorker = null;
                    StopProcess(process);
                }
            }
        }
        finally { workerGate.Release(); }
    });

    internal static ProcessStartInfo WorkerStart(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add(UpdateWorker.Argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex) { Settings.Log($"结束更新进程失败：{ex.Message}"); }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (processGate)
        {
            if (activeWorker is not null) StopProcess(activeWorker);
            downloadLease?.Dispose();
            downloadLease = null;
        }
    }
}

// The installer belongs to the window until it explicitly hands over the restart.
internal sealed class UpdateInstaller(Process process, string temporaryPath) : IAsyncDisposable
{
    private bool committed;

    internal static string TemporaryPath(DownloadedUpdate download, string target)
    {
        _ = download.DirectoryPath;
        return Path.Combine(Path.GetDirectoryName(target)!, $".stwin-update-{download.StageId}.tmp");
    }

    public static Task<UpdateInstaller> PrepareAsync(DownloadedUpdate download, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var temporary = TemporaryPath(download, Environment.ProcessPath!);
        var process = new Process
        {
            StartInfo = UpdateService.WorkerStart(download.ExecutablePath, "apply", JsonSerializer.Serialize(download),
                Environment.ProcessId.ToString(), Environment.ProcessPath!)
        };
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("无法启动更新安装进程。");
            var error = process.StandardError.ReadToEndAsync();
            if (await process.StandardOutput.ReadLineAsync(timeout.Token) != "READY")
            {
                var message = await error.WaitAsync(timeout.Token);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "更新安装准备失败。" : message.Trim());
            }
            return new UpdateInstaller(process, temporary);
        }
        catch
        {
            UpdateService.StopProcess(process);
            await FinishCleanupAsync(process, temporary);
            process.Dispose();
            throw;
        }
    });

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync("INSTALL".AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        if (await process.StandardOutput.ReadLineAsync(cancellationToken) != "ACCEPTED")
            throw new InvalidOperationException("更新安装进程未确认重启，已取消更新。");
        committed = true;
    }

    public ValueTask DisposeAsync()
    {
        if (committed) { process.Dispose(); return ValueTask.CompletedTask; }
        return new ValueTask(Task.Run(async () =>
        {
            try
            {
                // EOF cancels an installer waiting for approval and lets it delete its prepared file.
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) { Settings.Log($"取消更新安装：{ex.Message}"); }
            finally
            {
                UpdateService.StopProcess(process);
                await FinishCleanupAsync(process, temporaryPath);
                process.Dispose();
            }
        }));
    }

    private static async Task FinishCleanupAsync(Process process, string temporary)
    {
        try
        {
            if (process.Id > 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { Settings.Log($"等待更新安装进程结束失败：{ex.Message}"); }
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (Exception ex) { Settings.Log($"清理更新安装文件失败：{ex.Message}"); }
    }
}
