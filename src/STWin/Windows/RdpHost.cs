using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using STWin.Infrastructure;

namespace STWin.Windows;

internal sealed class RdpHost : AxHost
{
    public const string ClassId = "A0C63C30-F08D-4AB4-907C-34905D770C7D";
    private static readonly Guid NonScriptable3Id = new("B3378D90-0728-45C7-8ED7-B6159FB92219");
    private static readonly Guid EventsId = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    private readonly Action fullscreenCallback;
    private readonly Action minimizeCallback;
    private readonly Action<int, int> desktopSizeCallback;
    private object? eventClient;

    public event Action? FullscreenModeChanged;
    public event Action? MinimizeRequested;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutConnectionBarTextDelegate(IntPtr client, [MarshalAs(UnmanagedType.BStr)] string text);

    public RdpHost() : base(ClassId)
    {
        Dock = DockStyle.Fill;
        fullscreenCallback = () => FullscreenModeChanged?.Invoke();
        minimizeCallback = () => MinimizeRequested?.Invoke();
        desktopSizeCallback = (width, height) => DesktopSize = new(width, height);
    }

    protected override void CreateSink()
    {
        base.CreateSink();
        var client = Client;
        // DISPIDs from the installed MsTscAx type library's IMsTscAxEvents.
        ComEventsHelper.Combine(client, EventsId, 5, fullscreenCallback); // OnEnterFullScreenMode
        try
        {
            ComEventsHelper.Combine(client, EventsId, 6, fullscreenCallback); // OnLeaveFullScreenMode
            ComEventsHelper.Combine(client, EventsId, 12, desktopSizeCallback); // OnRemoteDesktopSizeChange
            ComEventsHelper.Combine(client, EventsId, 14, minimizeCallback); // OnRequestContainerMinimize
            eventClient = client;
        }
        catch
        {
            ComEventsHelper.Remove(client, EventsId, 5, fullscreenCallback);
            ComEventsHelper.Remove(client, EventsId, 6, fullscreenCallback);
            ComEventsHelper.Remove(client, EventsId, 12, desktopSizeCallback);
            throw;
        }
    }

    protected override void DetachSink()
    {
        if (eventClient is { } client)
        {
            eventClient = null;
            ComEventsHelper.Remove(client, EventsId, 5, fullscreenCallback);
            ComEventsHelper.Remove(client, EventsId, 6, fullscreenCallback);
            ComEventsHelper.Remove(client, EventsId, 12, desktopSizeCallback);
            ComEventsHelper.Remove(client, EventsId, 14, minimizeCallback);
        }
        base.DetachSink();
    }
    private object Client => GetOcx() ?? throw new InvalidOperationException("桌面显示组件尚未准备好。");
    public int ConnectionState => IsHandleCreated ? Convert.ToInt32(Get(Client, "Connected")) : 0;
    public int DisconnectReason => Convert.ToInt32(Get(Client, "ExtendedDisconnectReason"));
    public bool IsFullscreen => IsHandleCreated && Convert.ToBoolean(Get(Client, "FullScreen"));
    public Size DesktopSize { get; private set; }

    public void Configure(Settings settings, DesktopResolution? resolution = null)
    {
        if (ConnectionState != 0) throw new InvalidOperationException("连接期间不能更改连接设置。");
        // Window resizing only changes presentation (SmartSizing).
        var size = resolution ?? DesktopResolution.Default;
        var client = Client;
        Put(client, "Server", "localhost");
        Put(client, "DesktopWidth", size.Width);
        Put(client, "DesktopHeight", size.Height);
        DesktopSize = new(size.Width, size.Height);
        Settings.Log($"启动分辨率：{size.Width} × {size.Height}");
        Put(client, "ColorDepth", 32);
        var advanced = Get(client, "AdvancedSettings7");
        Put(advanced, "SmartSizing", settings.SmartSizing);
        Put(advanced, "RedirectClipboard", true);
        Put(advanced, "EnableCredSspSupport", true);
        Put(advanced, "DisplayConnectionBar", true);
        Put(advanced, "PinConnectionBar", true);
        Put(advanced, "ContainerHandledFullScreen", false);
        SetConnectionBarText(client, "桌面分身");
        var secured = Get(client, "SecuredSettings2");
        Put(secured, "KeyboardHookMode", settings.RemoteKeys ? 1 : 0);
        Put(secured, "AudioRedirectionMode", settings.Muted ? 2 : 0);
        object value = true;
        ((IExtendedSettings)client).SetProperty("ConnectToChildSession", ref value);
    }

    public void Connect() => Call(Client, "Connect");
    public void Disconnect() { if (ConnectionState != 0) Call(Client, "Disconnect"); }
    public async Task SetResolutionAsync(DesktopResolution resolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionState != 1) throw new InvalidOperationException("只能在桌面已连接时调整分辨率。");
        var expected = new Size(resolution.Width, resolution.Height);
        if (DesktopSize == expected) return;
        // IMsRdpClient9 updates the display without disconnecting the child session.
        var client = Client;
        client.GetType().InvokeMember("UpdateSessionDisplaySettings", BindingFlags.InvokeMethod, null, client,
            new object[] { (uint)resolution.Width, (uint)resolution.Height, 0u, 0u, 0u, 100u, 100u });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (DesktopSize != expected)
            {
                if (ConnectionState != 1) throw new InvalidOperationException("调整分辨率期间桌面连接已断开。");
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Windows 未确认分辨率调整为 {expected.Width} × {expected.Height}；当前为 {DesktopSize.Width} × {DesktopSize.Height}。请用 status 查询状态。");
        }
        Settings.Log($"已调整分辨率：{DesktopSize.Width} × {DesktopSize.Height}");
    }
    public void SetSizing(bool enabled) => Put(Get(Client, "AdvancedSettings7"), "SmartSizing", enabled);
    public void SetFullscreen(bool enabled)
    {
        if (ConnectionState != 1) throw new InvalidOperationException("只能在桌面已连接时切换全屏。");
        Put(Client, "FullScreen", enabled);
    }

    private static void SetConnectionBarText(object client, string text)
    {
        var unknown = Marshal.GetIUnknownForObject(client);
        IntPtr nonScriptable = IntPtr.Zero;
        try
        {
            var id = NonScriptable3Id;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in id, out nonScriptable));
            // IMsRdpClientNonScriptable3::put_ConnectionBarText is at byte offset 280
            // in the IID's COM vtable (as declared by the installed MsTscAx type library).
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(nonScriptable), 280);
            var put = Marshal.GetDelegateForFunctionPointer<PutConnectionBarTextDelegate>(method);
            Marshal.ThrowExceptionForHR(put(nonScriptable, text));
        }
        finally
        {
            if (nonScriptable != IntPtr.Zero) Marshal.Release(nonScriptable);
            Marshal.Release(unknown);
        }
    }
    private static object Get(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)!;
    private static void Put(object target, string name, object value) => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
    private static void Call(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, null);
}

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    void GetProperty([MarshalAs(UnmanagedType.BStr)] string name, [Out, MarshalAs(UnmanagedType.Struct)] out object value);
}
