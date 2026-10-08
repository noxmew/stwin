using STWin.Sessions;
using STWin.Windows;
using STWin.Views;
using System.ComponentModel;
using System.Drawing;

var passed = 0;
async Task Check(string name, Func<Task> run)
{
    await run();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
async Task Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
SessionController Controller(FakePlatform p, FakeConnection c) => new(p, c) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromMilliseconds(100) };

await Check("Native connection bar mismatch and target window placement", () =>
{
    var left = new Rectangle(0, 0, 1920, 1080);
    var right = new Rectangle(1920, 0, 1920, 1080);
    var below = new Rectangle(0, 1080, 1920, 1080);
    Assert(ConnectionBarGeometry.IsOnFullscreen(new Rectangle(627, 0, 665, 33), left));
    Assert(!ConnectionBarGeometry.IsOnFullscreen(new Rectangle(627, 0, 665, 33), right));
    Assert(!ConnectionBarGeometry.IsOnFullscreen(new Rectangle(627, 0, 665, 33), below));
    Assert(ConnectionBarGeometry.IsOnFullscreen(new Rectangle(2547, -33, 665, 33), right));
    Assert(ConnectionBarGeometry.CenterWindow(new Rectangle(0, 0, 1180, 760), right) == new Point(2290, 160));
    Assert(ConnectionBarGeometry.CenterWindow(new Rectangle(0, 0, 1180, 760), below) == new Point(370, 1240));
    return Task.CompletedTask;
});

await Check("Native no-child results normalize to absent", () =>
{
    Assert(SessionApi.DecodeChildQuery(false, uint.MaxValue, 1168) is null);
    Assert(SessionApi.DecodeChildQuery(true, uint.MaxValue, 0) is null);
    Assert(SessionApi.DecodeChildQuery(true, 42, 0) == 42);
    return Task.CompletedTask;
});
await Check("Native access and other failures remain errors", async () =>
{
    foreach (var code in new[] { 5, 50, 1722 })
    {
        try { SessionApi.DecodeChildQuery(false, uint.MaxValue, code); }
        catch (Win32Exception ex) { Assert(ex.NativeErrorCode == code); continue; }
        throw new Exception("Native query error was swallowed");
    }
    await Task.CompletedTask;
});

await Check("1168 before creation and after logoff completes the lifecycle", async () =>
{
    var p = new FakePlatform { UseNativeNotFound = true };
    var c = new FakeConnection { OnConnect = () => p.Id = 42 };
    var s = Controller(p, c);
    await s.StartAsync(false);
    Assert(s.ManagedId == 42 && c.ConnectCount == 1);
    await s.StopAsync();
    Assert(s.ManagedId is null && p.Logoffs == 1 && c.State == 0);
});

await Check("Existing session is never adopted without consent", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await Throws<InvalidOperationException>(() => s.StartAsync(false));
    Assert(c.ConnectCount == 0 && p.Logoffs == 0 && s.ManagedId is null);
});
await Check("Query failure does not create a session", async () =>
{
    var p = new FakePlatform { QueryFails = true }; var c = new FakeConnection(); var s = Controller(p, c);
    await Throws<IOException>(() => s.StartAsync(false)); Assert(c.ConnectCount == 0);
});
await Check("Connection success requires a child session", async () =>
{
    var p = new FakePlatform(); var c = new FakeConnection(); var s = Controller(p, c);
    await Throws<TimeoutException>(() => s.StartAsync(false)); Assert(s.ManagedId is null && c.State == 0);
});
await Check("Duplicate start is coalesced; verified session can be ended", async () =>
{
    var p = new FakePlatform(); var c = new FakeConnection { OnConnect = () => p.Id = 42 }; var s = Controller(p, c);
    await Task.WhenAll(s.StartAsync(false), s.StartAsync(false));
    Assert(c.ConnectCount == 1 && s.ManagedId == 42);
    await s.StopAsync(); Assert(p.Logoffs == 1 && s.ManagedId is null);
});
await Check("Changed session cannot be logged off", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await s.StartAsync(true); p.Id = 43;
    await Throws<InvalidOperationException>(s.StopAsync); Assert(p.Logoffs == 0);
});
await Check("Cancellation disconnects and releases operation gate", async () =>
{
    var p = new FakePlatform(); var c = new FakeConnection(); var s = Controller(p, c);
    using var cancel = new CancellationTokenSource(10);
    await Throws<OperationCanceledException>(() => s.StartAsync(false, cancel.Token));
    Assert(!s.Busy && c.State == 0);
    c.OnConnect = () => p.Id = 99;
    await s.StartAsync(false); Assert(s.ManagedId == 99);
});
await Check("Keep and exit preserves the desktop; a new instance reconnects to the same session", async () =>
{
    var p = new FakePlatform { Id = 42 };
    var firstConnection = new FakeConnection();
    var first = Controller(p, firstConnection);
    await first.StartAsync(true);
    Assert(await first.ExitAsync(ExitChoice.KeepSession, 42));
    Assert(p.Id == 42 && p.Logoffs == 0 && firstConnection.State == 0 && firstConnection.DisconnectCount == 1);
    var nextConnection = new FakeConnection();
    var next = Controller(p, nextConnection);
    Assert(next.RefreshSession() == 42 && next.ManagedId is null);
    var welcome = new DesktopUiState(DesktopOperation.None, 0, next.AvailableId.HasValue, false);
    Assert(welcome.StartText == "继续连接" && welcome.CanStart && welcome.CanStop);
    await next.StartAsync(42u);
    Assert(next.ManagedId == 42 && nextConnection.State == 1 && p.Id == 42 && p.Logoffs == 0);
});
await Check("End and exit can end an explicitly confirmed retained session before connecting", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    var chosen = s.RefreshSession();
    Assert(s.ManagedId is null);
    Assert(await s.ExitAsync(ExitChoice.EndSession, chosen));
    Assert(p.Id is null && p.Logoffs == 1 && c.ConnectCount == 0 && c.State == 0 && s.AvailableId is null);
});
await Check("Cancel exit leaves the connection untouched and performs no session queries", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await s.StartAsync(true);
    var queries = p.QueryCount;
    p.QueryFails = true;
    Assert(!await s.ExitAsync(ExitChoice.Cancel, 42));
    Assert(c.State == 1 && c.DisconnectCount == 0 && p.Logoffs == 0 && p.QueryCount == queries);
});
await Check("A session replaced while the exit dialog is open cannot be ended", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    var chosen = s.RefreshSession();
    p.Id = 43;
    await Throws<InvalidOperationException>(() => s.ExitAsync(ExitChoice.EndSession, chosen));
    Assert(p.Id == 43 && p.Logoffs == 0 && c.DisconnectCount == 0 && !s.Busy);
});
await Check("Continue does not replace a vanished or changed desktop with a new connection", async () =>
{
    foreach (var replacement in new uint?[] { null, 43 })
    {
        var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
        var offered = s.RefreshSession()!.Value;
        p.Id = replacement;
        await Throws<InvalidOperationException>(() => s.StartAsync(offered));
        Assert(c.ConnectCount == 0 && p.Logoffs == 0 && p.Id == replacement);
    }
});
await Check("End and exit requires a confirmed session", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await Throws<InvalidOperationException>(() => s.ExitAsync(ExitChoice.EndSession, null));
    Assert(p.QueryCount == 0 && p.Logoffs == 0 && c.DisconnectCount == 0);
});
await Check("Keep and exit remains available when querying Windows session state fails", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await s.StartAsync(true);
    p.QueryFails = true;
    var queries = p.QueryCount;
    Assert(await s.ExitAsync(ExitChoice.KeepSession, null));
    Assert(p.Id == 42 && p.Logoffs == 0 && p.QueryCount == queries && c.State == 0);
});
await Check("Failed disconnect never falls back to logoff and can be retried", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await s.StartAsync(true);
    c.DisconnectFails = true;
    await Throws<IOException>(() => s.ExitAsync(ExitChoice.KeepSession, 42));
    Assert(p.Id == 42 && p.Logoffs == 0 && c.State == 1 && s.ManagedId == 42 && !s.Busy);
    c.DisconnectFails = false;
    Assert(await s.ExitAsync(ExitChoice.KeepSession, 42));
    Assert(p.Id == 42 && p.Logoffs == 0 && c.State == 0);
});
await Check("Keep and exit waits for asynchronous disconnection before allowing shutdown", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await s.StartAsync(true);
    c.HoldDisconnect = true;
    var exit = s.ExitAsync(ExitChoice.KeepSession, 42);
    Assert(!exit.IsCompleted && s.Busy && p.Id == 42 && p.Logoffs == 0);
    c.CompleteDisconnect();
    Assert(await exit && !s.Busy && p.Id == 42 && p.Logoffs == 0);
});
await Check("Exit cannot report completion while another desktop operation holds the gate", async () =>
{
    var p = new FakePlatform(); var c = new FakeConnection(); var s = Controller(p, c);
    using var cancel = new CancellationTokenSource();
    var starting = s.StartAsync(false, cancel.Token);
    Assert(s.Busy);
    await Throws<InvalidOperationException>(() => s.ExitAsync(ExitChoice.KeepSession, null));
    Assert(c.DisconnectCount == 0 && p.Logoffs == 0);
    cancel.Cancel();
    await Throws<OperationCanceledException>(() => starting);
    Assert(!s.Busy);
});
await Check("A retained session ending externally removes Continue and End actions", async () =>
{
    var p = new FakePlatform { Id = 42 }; var c = new FakeConnection(); var s = Controller(p, c);
    await s.StartAsync(true);
    p.Id = null;
    Assert(s.RefreshSession() is null && s.ManagedId is null);
    var welcome = new DesktopUiState(DesktopOperation.None, 0, s.AvailableId.HasValue, false);
    Assert(welcome.StartText == "启动桌面" && welcome.CanStart && !welcome.CanStop);
});
await Check("Connecting host does not enable fullscreen; failure restores retry controls", () =>
{
    var preparing = new DesktopUiState(DesktopOperation.Preparing, 0, false, false);
    Assert(!preparing.ShowDesktop && !preparing.CanStart && !preparing.CanFullscreen);
    var connecting = preparing with { Operation = DesktopOperation.Connecting, ConnectionState = 2 };
    Assert(connecting.ShowDesktop && !connecting.CanStart && !connecting.CanFullscreen);
    var failed = connecting with { Operation = DesktopOperation.None, ConnectionState = 0 };
    Assert(!failed.ShowDesktop && failed.CanStart && !failed.CanStop && !failed.CanFullscreen);
    var unavailable = failed with { ConnectionState = -1, HasSession = true };
    Assert(!unavailable.ShowDesktop && unavailable.CanStart && unavailable.CanStop && !unavailable.CanFullscreen);
    var recovered = unavailable with { ConnectionState = 1 };
    Assert(recovered.ShowDesktop && recovered.CanFullscreen && !recovered.CanStart);
    return Task.CompletedTask;
});
await Check("Connected, disconnected and stopped views preserve only valid actions", () =>
{
    var connected = new DesktopUiState(DesktopOperation.None, 1, true, false);
    Assert(connected.ShowDesktop && !connected.CanStart && connected.CanStop && connected.CanFullscreen);
    var disconnected = connected with { ConnectionState = 0 };
    Assert(!disconnected.ShowDesktop && disconnected.CanStart && disconnected.CanStop && !disconnected.CanFullscreen);
    Assert(disconnected.StartText == "继续连接");
    var stopping = disconnected with { Operation = DesktopOperation.Ending };
    Assert(!stopping.CanStart && !stopping.CanStop && !stopping.CanFullscreen);
    var stopped = stopping with { Operation = DesktopOperation.None, HasSession = false };
    Assert(stopped.CanStart && !stopped.CanStop && stopped.StartText == "启动桌面");
    return Task.CompletedTask;
});
await Check("Confirmation blocks conflicting actions and cancellation restores them", () =>
{
    var connected = new DesktopUiState(DesktopOperation.None, 1, true, false);
    var modal = connected with { ConfirmationOpen = true };
    Assert(modal.ShowDesktop && !modal.CanStop && !modal.CanFullscreen);
    Assert((modal with { ConfirmationOpen = false }) == connected);
    return Task.CompletedTask;
});
Console.WriteLine($"{passed} lifecycle and UI state tests passed.");

sealed class FakePlatform : ISessionPlatform
{
    public uint? Id;
    public bool QueryFails;
    public bool UseNativeNotFound;
    public int Logoffs;
    public int QueryCount;
    public uint? ChildId()
    {
        QueryCount++;
        return QueryFails ? throw new IOException("query unavailable") :
            UseNativeNotFound && Id is null ? SessionApi.DecodeChildQuery(false, uint.MaxValue, 1168) : Id;
    }
    public void Enable() { }
    public void Logoff(uint id) { if (id != Id) throw new Exception("wrong target"); Logoffs++; Id = null; }
}
sealed class FakeConnection : IDesktopConnection
{
    public int State { get; private set; }
    public int ConnectCount;
    public int DisconnectCount;
    public bool DisconnectFails;
    public bool HoldDisconnect;
    public Action? OnConnect;
    public void Connect() { ConnectCount++; State = 1; OnConnect?.Invoke(); }
    public void Disconnect()
    {
        DisconnectCount++;
        if (DisconnectFails) throw new IOException("disconnect unavailable");
        if (!HoldDisconnect) CompleteDisconnect();
    }
    public void CompleteDisconnect() => State = 0;
}

