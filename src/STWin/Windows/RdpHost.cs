using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    private object? eventClient;

    public event Action? FullscreenModeChanged;
    public event Action? MinimizeRequested;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutConnectionBarTextDelegate(IntPtr client, [MarshalAs(UnmanagedType.BStr)] string text);

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    public RdpHost() : base(ClassId)
    {
        Dock = DockStyle.Fill;
        fullscreenCallback = () => FullscreenModeChanged?.Invoke();
        minimizeCallback = () => MinimizeRequested?.Invoke();
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
            ComEventsHelper.Combine(client, EventsId, 14, minimizeCallback); // OnRequestContainerMinimize
            eventClient = client;
        }
        catch
        {
            ComEventsHelper.Remove(client, EventsId, 5, fullscreenCallback);
            ComEventsHelper.Remove(client, EventsId, 6, fullscreenCallback);
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
            ComEventsHelper.Remove(client, EventsId, 14, minimizeCallback);
        }
        base.DetachSink();
    }
    private object Client => GetOcx() ?? throw new InvalidOperationException("桌面显示组件尚未准备好。");
    public int ConnectionState => IsHandleCreated ? Convert.ToInt32(Get(Client, "Connected")) : 0;
    public int DisconnectReason => Convert.ToInt32(Get(Client, "ExtendedDisconnectReason"));
    public bool IsFullscreen => IsHandleCreated && Convert.ToBoolean(Get(Client, "FullScreen"));
    public System.Drawing.Size DesktopSize => new(Convert.ToInt32(Get(Client, "DesktopWidth")), Convert.ToInt32(Get(Client, "DesktopHeight")));

    public (IntPtr Handle, Rectangle Bounds) RemoteViewport => FindRemoteViewport();

    public byte[] CaptureRemotePng(out Size screenshotSize)
    {
        var (window, bounds) = FindRemoteViewport();
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("无法读取分身画面窗口。");
        screenshotSize = bounds.Size;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法创建分身截图设备上下文。");
        IntPtr compatibleDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            compatibleDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (compatibleDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("无法分配分身截图缓冲区。");
            previous = SelectObject(compatibleDc, bitmap);
            if (!PrintWindow(window, compatibleDc, 2)) throw new InvalidOperationException("Windows 无法从 RDP 画面窗口获取后台截图。");
            _ = SelectObject(compatibleDc, previous);
            previous = IntPtr.Zero;
            using var image = Image.FromHbitmap(bitmap);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            if (previous != IntPtr.Zero && compatibleDc != IntPtr.Zero) _ = SelectObject(compatibleDc, previous);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (compatibleDc != IntPtr.Zero) _ = DeleteDC(compatibleDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void PostRemoteMouse(IntPtr window, uint message, UIntPtr wParam, int x, int y, int screenX, int screenY, bool wheel = false)
    {
        var targetX = wheel ? screenX : x;
        var targetY = wheel ? screenY : y;
        var point = unchecked((int)((((uint)targetY & 0xffff) << 16) | ((uint)targetX & 0xffff)));
        if (window == IntPtr.Zero || !PostMessage(window, message, wParam, new IntPtr(point)))
            throw new InvalidOperationException($"无法向后台 RDP 画面发送鼠标事件。Windows 错误：{Marshal.GetLastWin32Error()}。");
    }

    public void PostRemoteKey(IntPtr window, uint message, Keys key, int keyData)
    {
        if (window == IntPtr.Zero || !PostMessage(window, message, new UIntPtr((uint)key), new IntPtr(keyData)))
            throw new InvalidOperationException($"无法向后台 RDP 画面发送键盘事件。Windows 错误：{Marshal.GetLastWin32Error()}。");
    }

    private (IntPtr Handle, Rectangle Bounds) FindRemoteViewport()
    {
        var roots = new List<IntPtr>();
        _ = EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == (uint)Environment.ProcessId) roots.Add(window);
            return true;
        }, IntPtr.Zero);

        IntPtr best = IntPtr.Zero;
        Rectangle bestBounds = Rectangle.Empty;
        var bestVisible = false;
        long bestArea = 0;
        void Consider(IntPtr window)
        {
            var className = new StringBuilder(128);
            if (GetClassName(window, className, className.Capacity) == 0 || className.ToString() != "OPWindowClass") return;
            if (!GetWindowRect(window, out var rect)) return;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var area = (long)bounds.Width * bounds.Height;
            if (area <= 0) return;
            var visible = IsWindowVisible(window);
            if ((visible && !bestVisible) || (visible == bestVisible && area > bestArea))
            {
                best = window;
                bestBounds = bounds;
                bestVisible = visible;
                bestArea = area;
            }
        }

        foreach (var root in roots)
        {
            Consider(root);
            _ = EnumChildWindows(root, (window, _) => { Consider(window); return true; }, IntPtr.Zero);
        }
        if (best == IntPtr.Zero) throw new InvalidOperationException("没有找到 RDP 分身画面窗口。");
        return (best, bestBounds);
    }

    public void Configure(Settings settings)
    {
        if (ConnectionState != 0) throw new InvalidOperationException("连接期间不能更改连接设置。");
        // Capture the primary display's pixel dimensions once per connection.
        // Window resizing only changes presentation (SmartSizing).
        var display = Screen.PrimaryScreen
            ?? throw new InvalidOperationException("无法读取主显示器分辨率，请确认显示器已连接后重试。");
        var size = display.Bounds.Size;
        var client = Client;
        Put(client, "Server", "localhost");
        Put(client, "DesktopWidth", size.Width);
        Put(client, "DesktopHeight", size.Height);
        Settings.Log($"启动分辨率：{size.Width} × {size.Height}（主显示器）");
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
