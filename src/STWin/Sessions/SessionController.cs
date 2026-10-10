using System;
using System.Threading;
using System.Threading.Tasks;

namespace STWin.Sessions;

internal interface ISessionPlatform
{
    uint? ChildId();
    void Enable();
    void Logoff(uint id);
}
internal interface IDesktopConnection
{
    int State { get; }
    void Connect();
    void Disconnect();
}

internal enum ExitChoice { Cancel, KeepSession, EndSession }

internal sealed class SessionController(ISessionPlatform platform, IDesktopConnection connection)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public uint? ManagedId { get; private set; }
    public uint? AvailableId { get; private set; }
    public bool Busy { get; private set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public uint? RefreshSession() => Busy ? AvailableId : ReadSession();

    private uint? ReadSession()
    {
        // Discover the live child on every launch; a saved session ID is not an identity.
        var current = platform.ChildId();
        AvailableId = current;
        if (ManagedId != current) ManagedId = null;
        return current;
    }

    public Task StartAsync(bool acceptExisting, CancellationToken cancellationToken = default)
        => StartCoreAsync(acceptExisting, null, cancellationToken);

    public Task StartAsync(uint confirmedExistingId, CancellationToken cancellationToken = default)
        => StartCoreAsync(true, confirmedExistingId, cancellationToken);

    private async Task StartCoreAsync(bool acceptExisting, uint? expectedId, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("正在处理桌面操作，请稍后再试。");
        Busy = true;
        try
        {
            var existing = ReadSession();
            if (expectedId.HasValue && existing != expectedId)
                throw new InvalidOperationException("之前的桌面会话已变化，已取消连接。请重新选择。");
            if (existing.HasValue && existing != ManagedId && !acceptExisting)
                throw new InvalidOperationException("已有桌面会话，需要先确认是否连接。");
            if (connection.State == 1 && existing == ManagedId && existing.HasValue) return;
            if (connection.State != 0) throw new InvalidOperationException("上一次连接尚未结束，请稍后重试。");
            platform.Enable();
            connection.Connect();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                while (true)
                {
                    await Task.Delay(PollInterval, timeout.Token);
                    var id = ReadSession();
                    if (connection.State == 1 && id.HasValue)
                    {
                        if (existing.HasValue && id != existing) throw new InvalidOperationException("连接期间会话发生变化，已取消连接。");
                        ManagedId = id;
                        return;
                    }
                    if (connection.State == 0) throw new InvalidOperationException("连接已断开。请检查系统登录提示或查看诊断信息。");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("等待桌面连接超时。请检查系统登录提示后重试。"); }
        }
        catch
        {
            try { connection.Disconnect(); } catch { /* Preserve the original failure. */ }
            throw;
        }
        finally { Busy = false; gate.Release(); }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => StopCoreAsync(ManagedId, cancellationToken);

    // The caller must obtain explicit consent for this exact live session before invoking this overload.
    public Task StopAsync(uint confirmedSessionId, CancellationToken cancellationToken = default) => StopCoreAsync(confirmedSessionId, cancellationToken);

    private async Task StopCoreAsync(uint? expectedId, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("正在处理桌面操作，请稍后再试。");
        Busy = true;
        try
        {
            var current = ReadSession();
            if (current is null) { connection.Disconnect(); ManagedId = null; return; }
            if (current != expectedId) throw new InvalidOperationException("桌面会话已变化或未经确认，已取消结束操作。请重新选择。");
            platform.Logoff(current.Value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                while (ReadSession() == current) await Task.Delay(PollInterval, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Windows 尚未确认注销完成。请稍后重试，或在任务管理器中检查会话。"); }
            connection.Disconnect();
            ManagedId = null;
        }
        finally { Busy = false; gate.Release(); }
    }

    public async Task<bool> ExitAsync(ExitChoice choice, uint? confirmedSessionId, CancellationToken cancellationToken = default)
    {
        switch (choice)
        {
            case ExitChoice.Cancel:
                return false;
            case ExitChoice.KeepSession:
                await DisconnectAsync(cancellationToken);
                return true;
            case ExitChoice.EndSession when confirmedSessionId.HasValue:
                await StopAsync(confirmedSessionId.Value, cancellationToken);
                return true;
            default:
                throw new InvalidOperationException("尚未确认要结束的桌面，请重新选择。");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("正在处理桌面操作，请稍后再试。");
        Busy = true;
        try
        {
            connection.Disconnect();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                while (connection.State != 0) await Task.Delay(PollInterval, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("等待连接断开超时，请稍后重试退出。"); }
            // Disconnect never calls Logoff or clears ownership: the live desktop remains available.
        }
        finally { Busy = false; gate.Release(); }
    }
}
