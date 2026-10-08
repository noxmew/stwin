using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using STWin.Infrastructure;
using STWin.Mcp;
using STWin.Sessions;
using STWin.Windows;
using STWin.Views;
using System.Windows.Media;

namespace STWin;

public partial class MainWindow : Window
{
    private readonly Settings settings = Settings.Load();
    private readonly RdpHost host = new();
    private readonly RdpConnectionBarTracker connectionBarTracker = new();
    private readonly SessionController controller;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private bool exiting;
    private WindowState restoreState = WindowState.Normal;
    private bool nativeFullscreen;
    private bool hiddenForFullscreen;
    private bool resumeFullscreenAfterMinimize;
    private bool reanchoringFullscreen;
    private System.Drawing.Rectangle? lastMonitorReanchor;
    private DesktopOperation operation;
    private int connectionState;
    private bool requiresElevation;
    private bool monitoringError;
    private string lastError = "无";
    private readonly bool previewMode;
    private bool confirmationOpen;
    private bool closeRequested;
    private McpServer? mcpServer;
    private readonly SemaphoreSlim mcpActionGate = new(1, 1);

    public MainWindow(bool preview = false)
    {
        previewMode = preview;
        InitializeComponent();
        host.FullscreenModeChanged += () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncFullscreenFromHost));
        host.MinimizeRequested += () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(MinimizeNativeFullscreen));
        connectionBarTracker.MonitorMismatch += monitor =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ReanchorNativeFullscreen(monitor)));
        controller = new(new SessionPlatform(), new DesktopConnection(host, settings));
        DesktopView.Child = host;
        Width = Math.Clamp(settings.Width, 920, 3840);
        Height = Math.Clamp(settings.Height, 580, 2160);
        if (settings.Left >= SystemParameters.VirtualScreenLeft && settings.Top >= SystemParameters.VirtualScreenTop &&
            settings.Left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            settings.Top + 100 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { Left = settings.Left; Top = settings.Top; }
        Topmost = settings.Topmost;
        PinBox.IsChecked = settings.Topmost;
        SizingBox.IsChecked = settings.SmartSizing;
        MuteBox.IsChecked = settings.Muted;
        KeysBox.IsChecked = settings.RemoteKeys;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) restoreState = WindowState;
            UpdateWindowControls();
        };
        UpdateWindowControls();
        UpdateUi();
        Loaded += (_, _) => RefreshConnection();
        timer.Tick += (_, _) => RefreshConnection();
        timer.Start();
        if (!previewMode) StartMcpService();
    }

    private void StartMcpService()
    {
        try
        {
            mcpServer = new McpServer(HandleMcpToolAsync);
            mcpServer.Start();
            Settings.Log($"本机 MCP 服务已启动：{mcpServer.Endpoint}；连接配置：{Path.Combine(Settings.Folder, "mcp-connection.json")}");
        }
        catch (Exception ex)
        {
            mcpServer?.Dispose();
            mcpServer = null;
            Settings.Log($"启动 MCP 服务失败：{ex}");
        }
    }

    public void Reveal()
    {
        if (hiddenForFullscreen)
        {
            SyncFullscreenFromHost();
            if (hiddenForFullscreen) return;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = restoreState;
        Activate();
        if (!resumeFullscreenAfterMinimize) return;
        resumeFullscreenAfterMinimize = false;
        try
        {
            if (host.ConnectionState != 1) return;
            host.SetFullscreen(true);
            SetFullscreenPresentation(true);
        }
        catch (Exception ex) { Error(ex); }
    }
    internal void FinishPreview() => Close();
    internal async Task VerifyHostAsync()
    {
        DesktopView.Visibility = Visibility.Visible;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        host.CreateControl();
        host.Configure(settings);
        DesktopView.Visibility = Visibility.Collapsed;
        UpdateLayout();
    }
    internal async Task CaptureUiGalleryAsync()
    {
        static void Capture(FrameworkElement element, string file)
        {
            element.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, file));
            encoder.Save(stream);
        }
        var scenarios = new[] {
            ("连接已有桌面？", "检测到一个正在运行的桌面。连接后可由 STWin 管理和结束；若其他工具正在使用，请暂不连接。", "连接桌面", "暂不连接", false, "dialog-connect.png"),
            ("结束这个桌面？", "桌面内的应用将关闭，未保存的内容可能丢失。", "结束桌面", "继续使用", true, "dialog-stop.png")
        };
        foreach (var (title, body, action, cancel, destructive, file) in scenarios)
        {
            var dialog = new ConfirmDialog(title, body, action, cancel, destructive) { Owner = this, Opacity = 0 };
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture((FrameworkElement)dialog.Content, file);
            dialog.Close();
        }
        var exitDialog = new ExitDialog { Owner = this, Opacity = 0 };
        exitDialog.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Capture((FrameworkElement)exitDialog.Content, "dialog-exit.png");
        exitDialog.Close();
        MoreButton.ContextMenu.Opacity = 0;
        MoreClick(this, new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Capture((FrameworkElement)VisualTreeHelper.GetChild(MoreButton.ContextMenu, 0), "menu.png");
        MoreButton.ContextMenu.IsOpen = false;
        MoreButton.ContextMenu.Opacity = 1;
        ErrorBanner.Visibility = Visibility.Visible;
        ErrorText.Text = "连接等待超时，请检查 Windows 登录提示后重试。";
        UpdateLayout();
        Capture((FrameworkElement)Content, "state-error.png");
        ErrorBanner.Visibility = Visibility.Collapsed;
        Status("正在连接桌面…");
        SetOperation(DesktopOperation.Preparing);
        UpdateLayout();
        Capture((FrameworkElement)Content, "state-connecting.png");
        SetOperation(DesktopOperation.None);
        WindowState = WindowState.Minimized;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Reveal();
        if (WindowState != WindowState.Normal || !IsVisible)
            throw new InvalidOperationException("窗口最小化后恢复失败。");
        SetFullscreenPresentation(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (IsVisible || !host.IsHandleCreated)
            throw new InvalidOperationException("RDP 全屏时未隐藏空白主窗口，或桌面控件被销毁。");
        SetFullscreenPresentation(false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!IsVisible || !host.IsHandleCreated)
            throw new InvalidOperationException("退出 RDP 全屏后未恢复主窗口和桌面控件。");
        FullscreenMonitorAnchor.Move(this, System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).Bounds);
    }
    private void Status(string text) => Settings.Log(text);
    private DesktopUiState UiState => new(operation, connectionState, controller.AvailableId.HasValue, confirmationOpen);
    private void SetOperation(DesktopOperation value)
    {
        operation = value;
        UpdateUi();
    }
    private void UpdateUi()
    {
        var state = UiState;
        DesktopView.Visibility = state.ShowDesktop ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = state.ShowDesktop ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled = state.CanStart;
        StartButton.Content = state.StartText;
        StopButton.IsEnabled = state.CanStop;
        FullscreenButton.Visibility = state.Connected ? Visibility.Visible : Visibility.Collapsed;
        FullscreenButton.IsEnabled = state.CanFullscreen;
        SizingBox.IsEnabled = MuteBox.IsEnabled = KeysBox.IsEnabled = !state.Busy;
        ElevationButton.Visibility = requiresElevation && !state.HasSession && !state.Connected
            ? Visibility.Visible : Visibility.Collapsed;
        ElevationButton.IsEnabled = !state.Busy;
        if (connectionState == 0 && (nativeFullscreen || hiddenForFullscreen)) SetFullscreenPresentation(false);
    }
    private void SetFullscreenPresentation(bool fullscreen)
    {
        if (!fullscreen && !reanchoringFullscreen) lastMonitorReanchor = null;
        if (fullscreen)
        {
            connectionBarTracker.Start();
            connectionBarTracker.Synchronize();
        }
        else connectionBarTracker.Stop();
        if (nativeFullscreen != fullscreen)
        {
            nativeFullscreen = fullscreen;
            UpdateWindowControls();
        }
        if (fullscreen && !hiddenForFullscreen)
        {
            // The ActiveX control owns a separate full-screen window. Leaving this
            // WPF container visible exposes an empty white window on the old screen.
            hiddenForFullscreen = true;
            Hide();
        }
        else if (!fullscreen && hiddenForFullscreen)
        {
            hiddenForFullscreen = false;
            if (!exiting) Show();
        }
    }
    private void SyncFullscreenFromHost()
    {
        if (exiting || reanchoringFullscreen) return;
        try { SetFullscreenPresentation(host.ConnectionState == 1 && host.IsFullscreen); }
        catch (Exception ex) { Settings.Log($"同步 RDP 全屏状态失败：{ex.Message}"); }
    }
    private void ReanchorNativeFullscreen(System.Drawing.Rectangle monitor)
    {
        if (exiting || reanchoringFullscreen || lastMonitorReanchor == monitor ||
            !nativeFullscreen || host.ConnectionState != 1 || !host.IsFullscreen) return;
        lastMonitorReanchor = monitor;
        reanchoringFullscreen = true;
        var previousOpacity = Opacity;
        var previousShowActivated = ShowActivated;
        try
        {
            // The native bar belongs to the ActiveX full-screen session. Moving
            // its HWND is undone by the control. Move the hidden container to
            // the new monitor, then ask RDP to re-enter its own full-screen mode.
            Opacity = 0;
            ShowActivated = false;
            Show();
            FullscreenMonitorAnchor.Move(this, monitor);
            host.SetFullscreen(false);
            host.SetFullscreen(true);
            Settings.Log($"已请求 RDP 在显示器 {monitor} 重新进入原生全屏；等待连接条位置确认。");
        }
        catch (Exception ex)
        {
            Settings.Log($"RDP 跨屏重建失败：{ex}");
        }
        finally
        {
            ShowActivated = previousShowActivated;
            Opacity = previousOpacity;
            reanchoringFullscreen = false;
            try
            {
                if (host.ConnectionState == 1 && host.IsFullscreen)
                {
                    hiddenForFullscreen = true;
                    Hide();
                    connectionBarTracker.Synchronize();
                    var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                    check.Tick += (_, _) =>
                    {
                        check.Stop();
                        if (exiting || !nativeFullscreen) return;
                        connectionBarTracker.Synchronize();
                        if (connectionBarTracker.IsAligned != true)
                            Settings.Log($"RDP 重建后连接条仍未确认跟随画面：{connectionBarTracker.LastGeometry}");
                    };
                    check.Start();
                }
                else SetFullscreenPresentation(false);
            }
            catch (Exception ex) { Settings.Log($"恢复 RDP 跨屏状态失败：{ex}"); }
        }
    }
    private void MinimizeNativeFullscreen()
    {
        if (exiting) return;
        try
        {
            resumeFullscreenAfterMinimize = host.ConnectionState == 1 && host.IsFullscreen;
            RestoreNativeFullscreen();
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex) { resumeFullscreenAfterMinimize = false; Error(ex); }
    }
    private void RestoreNativeFullscreen()
    {
        // The RDP connection bar and hotkeys can leave fullscreen without our button.
        // Read the control directly: the cached value may lag until the next timer tick.
        if (host.ConnectionState == 1 && host.IsFullscreen) host.SetFullscreen(false);
        SetFullscreenPresentation(false);
    }
    private bool Confirm(string title, string description, string action, string cancel = "取消", bool destructive = false)
    {
        if (confirmationOpen) return false;
        confirmationOpen = true;
        UpdateUi();
        try { RestoreNativeFullscreen(); Reveal(); return new ConfirmDialog(title, description, action, cancel, destructive) { Owner = this }.ShowDialog() == true; }
        finally { confirmationOpen = false; UpdateUi(); }
    }
    private ExitChoice ChooseExit(bool canEndSession)
    {
        if (confirmationOpen) return ExitChoice.Cancel;
        confirmationOpen = true;
        UpdateUi();
        try
        {
            RestoreNativeFullscreen();
            Reveal();
            var dialog = new ExitDialog(canEndSession) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Choice : ExitChoice.Cancel;
        }
        finally { confirmationOpen = false; UpdateUi(); }
    }
    private void DismissErrorClick(object sender, RoutedEventArgs e) => ErrorBanner.Visibility = Visibility.Collapsed;
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreButton.ContextMenu.IsOpen = true;
    }
    private void Error(Exception exception, bool fromMonitor = false)
    {
        var e = exception.GetBaseException();
        lastError = $"{e.Message} (0x{e.HResult:X8})";
        Settings.Log(lastError);
        Status("操作未完成");
        ErrorText.Text = e is Win32Exception { NativeErrorCode: 5 }
            ? "当前操作需要管理员权限。"
            : e is TimeoutException ? e.Message
            : e is System.Runtime.InteropServices.COMException ? "桌面组件暂时不可用，请重试。详细原因可从菜单复制诊断信息。"
            : e.Message;
        ErrorBanner.Visibility = Visibility.Visible;
        requiresElevation = e is Win32Exception { NativeErrorCode: 5 };
        monitoringError = fromMonitor;
        if (fromMonitor) connectionState = -1;
        else
        {
            try { connectionState = host.ConnectionState; }
            catch { connectionState = -1; }
        }
        UpdateUi();
    }

    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if (controller.Busy || UiState.Busy) return;
        Reveal();
        var offeredSessionId = controller.AvailableId;
        SetOperation(DesktopOperation.Preparing);
        try
        {
            var existing = controller.RefreshSession();
            if (offeredSessionId.HasValue && !existing.HasValue)
                throw new InvalidOperationException("之前的桌面分身已结束。请点击“启动桌面”创建新桌面。");
            var accept = existing.HasValue && existing != controller.ManagedId;
            // Clicking the offered Continue action explicitly accepts that exact discovered session.
            if (accept && existing != offeredSessionId && !Confirm("连接已有桌面？", "检测到一个正在运行的桌面。连接后可由 STWin 管理和结束；若其他工具正在使用，请暂不连接。", "连接桌面", "暂不连接")) return;
            ErrorBanner.Visibility = Visibility.Collapsed;
            monitoringError = false;
            requiresElevation = false;
            SetOperation(DesktopOperation.Connecting);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            host.CreateControl();
            Status("正在连接桌面，请留意 Windows 登录提示…");
            if (existing.HasValue) await controller.StartAsync(existing.Value, lifetime.Token);
            else await controller.StartAsync(false, lifetime.Token);
            connectionState = 1;
            timer.Start();
            Status("已连接");
        }
        catch (Exception ex) { Error(ex); }
        finally { SetOperation(DesktopOperation.None); }
    }
    private async void StopClick(object sender, RoutedEventArgs e)
    {
        if (controller.Busy || UiState.Busy) return;
        try
        {
            var current = controller.RefreshSession();
            UpdateUi();
            if (!current.HasValue) return;
            if (!Confirm("结束这个桌面？", "桌面内的应用将关闭，未保存的内容可能丢失。", "结束桌面", "继续使用", true)) return;
            SetOperation(DesktopOperation.Ending);
            Status("正在结束桌面…");
            await controller.StopAsync(current.Value);
            connectionState = 0;
            ErrorBanner.Visibility = Visibility.Collapsed;
            requiresElevation = false;
            Status("桌面已结束");
        }
        catch (Exception ex) { Error(ex); }
        finally { SetOperation(DesktopOperation.None); }
    }
    private void RefreshConnection()
    {
        if (controller.Busy || UiState.Busy || exiting || reanchoringFullscreen) return;
        try
        {
            controller.RefreshSession();
            var current = host.ConnectionState;
            var fullscreen = current == 1 && host.IsFullscreen;
            var previous = connectionState;
            connectionState = current;
            SetFullscreenPresentation(fullscreen);
            if (monitoringError)
            {
                monitoringError = false;
                ErrorBanner.Visibility = Visibility.Collapsed;
                requiresElevation = false;
            }
            if (previous > 0 && current == 0)
            {
                lastError = $"RDP 断开代码：{host.DisconnectReason}";
                Settings.Log(lastError);
                Status("连接已断开");
            }
            UpdateUi();
        }
        catch (Exception ex)
        {
            // A transient COM failure must not permanently stop status updates or flood the error banner.
            if (connectionState != -1) Error(ex, fromMonitor: true);
        }
    }

    private Task<McpToolResult> HandleMcpToolAsync(string name, JsonElement arguments)
    {
        if (Dispatcher.CheckAccess()) return HandleMcpToolOnUiAsync(name, arguments);
        return Dispatcher.InvokeAsync(() => HandleMcpToolOnUiAsync(name, arguments)).Task.Unwrap();
    }

    private async Task<McpToolResult> HandleMcpToolOnUiAsync(string name, JsonElement arguments)
    {
        var readOnlyStatus = name == "get_desktop_status";
        if (!readOnlyStatus && !mcpActionGate.Wait(0))
            throw new InvalidOperationException("另一个 MCP 桌面操作尚未完成，请稍后重试。");
        try
        {
            switch (name)
            {
                case "get_desktop_status":
                {
                    var state = host.ConnectionState;
                    var size = state == 1 ? host.DesktopSize : System.Drawing.Size.Empty;
                    return new McpToolResult(JsonSerializer.Serialize(new
                    {
                        connected = state == 1,
                        connectionState = state,
                        resolution = state == 1 ? new { width = size.Width, height = size.Height } : null,
                        fullscreen = state == 1 && host.IsFullscreen
                    }));
                }
                case "take_screenshot":
                    await PrepareAutomationTargetAsync();
                    var screenshot = CaptureDesktopPng();
                    return new McpToolResult($"分身桌面截图，PNG 尺寸 {lastScreenshotSize.Width} × {lastScreenshotSize.Height} 像素。鼠标坐标使用此图像的像素坐标。", screenshot);
                case "mouse_move":
                    await PrepareAutomationTargetAsync();
                    var move = MapDesktopPoint(RequiredInt(arguments, "x"), RequiredInt(arguments, "y"));
                    DesktopInput.MoveMouse(host, move.Window, move.X, move.Y, move.ScreenX, move.ScreenY);
                    return new McpToolResult("鼠标已移动到分身桌面指定坐标。");
                case "mouse_click":
                    await PrepareAutomationTargetAsync();
                    var click = MapDesktopPoint(RequiredInt(arguments, "x"), RequiredInt(arguments, "y"));
                    var button = OptionalString(arguments, "button", "left");
                    var count = OptionalInt(arguments, "count", 1);
                    if (count is < 1 or > 2) throw new ArgumentOutOfRangeException("count", "点击次数只能为 1 或 2。");
                    DesktopInput.ClickMouse(host, click.Window, click.X, click.Y, click.ScreenX, click.ScreenY, button, count);
                    return new McpToolResult("已发送鼠标点击事件。");
                case "mouse_scroll":
                    await PrepareAutomationTargetAsync();
                    var scroll = MapDesktopPoint(RequiredInt(arguments, "x"), RequiredInt(arguments, "y"));
                    var delta = RequiredInt(arguments, "delta");
                    if (delta is < -1200 or > 1200) throw new ArgumentOutOfRangeException("delta", "滚轮值需介于 -1200 至 1200。");
                    DesktopInput.Scroll(host, scroll.Window, scroll.X, scroll.Y, scroll.ScreenX, scroll.ScreenY, delta);
                    return new McpToolResult("已发送鼠标滚轮事件。");
                case "key_press":
                    await PrepareAutomationTargetAsync();
                    var key = DesktopInput.ParseKey(RequiredString(arguments, "key"));
                    var modifiers = ReadModifiers(arguments);
                    DesktopInput.PressKey(host, key, modifiers);
                    return new McpToolResult("已发送键盘按键事件。");
                case "type_text":
                    await PrepareAutomationTargetAsync();
                    var text = RequiredString(arguments, "text");
                    if (text.Length > 10000) throw new ArgumentOutOfRangeException("text", "一次最多输入 10000 个字符。");
                    await PasteTextToRemoteAsync(text);
                    return new McpToolResult($"已通过 RDP 剪贴板向分身粘贴 {text.Length} 个字符。");
                case "run_script":
                    var script = RequiredString(arguments, "script");
                    if (script.Length is < 1 or > 4000) throw new ArgumentOutOfRangeException("script", "脚本长度需介于 1 至 4000 个字符。");
                    return await RunPowerShellScriptAsync(script);
                default:
                    throw new ArgumentException($"未知的 STWin MCP 工具：{name}");
            }
        }
        finally
        {
            if (!readOnlyStatus) mcpActionGate.Release();
        }
    }

    private async Task PrepareAutomationTargetAsync()
    {
        EnsureMcpDesktopAvailable();
        connectionState = 1;
        UpdateUi();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        EnsureMcpDesktopAvailable();
    }

    private void EnsureMcpDesktopAvailable()
    {
        if (controller.Busy || operation != DesktopOperation.None || confirmationOpen)
            throw new InvalidOperationException("STWin 正在处理桌面操作或等待确认，请稍后重试。");
        if (host.ConnectionState != 1) throw new InvalidOperationException("当前没有已连接的分身桌面。请先在 STWin 中启动或继续连接桌面。");
    }

    private System.Drawing.Size lastScreenshotSize;

    private byte[] CaptureDesktopPng() => host.CaptureRemotePng(out lastScreenshotSize);

    private (IntPtr Window, int X, int Y, int ScreenX, int ScreenY) MapDesktopPoint(int x, int y)
    {
        var viewport = host.RemoteViewport;
        var bounds = viewport.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("分身桌面当前没有可操作的显示区域。");
        var reference = lastScreenshotSize.Width > 0 && lastScreenshotSize.Height > 0 ? lastScreenshotSize : bounds.Size;
        if (x < 0 || x >= reference.Width || y < 0 || y >= reference.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"坐标需位于最近截图范围 0..{reference.Width - 1} × 0..{reference.Height - 1}。");
        var targetX = (int)((long)x * bounds.Width / reference.Width);
        var targetY = (int)((long)y * bounds.Height / reference.Height);
        return (viewport.Handle, targetX, targetY, bounds.Left + targetX, bounds.Top + targetY);
    }

    private async Task<McpToolResult> RunPowerShellScriptAsync(string script)
    {
        await PrepareAutomationTargetAsync();
        DesktopInput.PressKey(host, System.Windows.Forms.Keys.Escape, new[] { System.Windows.Forms.Keys.ControlKey });
        await Task.Delay(350);
        await PasteTextToRemoteAsync("powershell");
        await Task.Delay(700);
        DesktopInput.PressKey(host, System.Windows.Forms.Keys.Enter, Array.Empty<System.Windows.Forms.Keys>());
        await Task.Delay(1800);

        // Base64 keeps the submitted PowerShell on one line and the clipboard paste preserves Unicode source text.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var command = $"Invoke-Expression ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')))";
        if (command.Length > 7000)
            throw new InvalidOperationException("脚本编码后超过 PowerShell 控制台单行输入限制，请缩短脚本或移除多余注释。");
        await PasteTextToRemoteAsync(command);
        DesktopInput.PressKey(host, System.Windows.Forms.Keys.Enter, Array.Empty<System.Windows.Forms.Keys>());
        await Task.Delay(1500);
        var screenshot = CaptureDesktopPng();
        return new McpToolResult($"已在分身的 PowerShell 中提交脚本；请查看截图中的控制台输出。PowerShell 窗口会留在分身中。截图尺寸 {lastScreenshotSize.Width} × {lastScreenshotSize.Height} 像素。", screenshot);
    }

    private async Task PasteTextToRemoteAsync(string text)
    {
        System.Windows.IDataObject? previous = null;
        try { previous = Clipboard.GetDataObject(); }
        catch (System.Runtime.InteropServices.ExternalException ex) { throw new InvalidOperationException("无法读取本机剪贴板，未向分身粘贴文本。", ex); }
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
        try
        {
            DesktopInput.PressKey(host, System.Windows.Forms.Keys.V, new[] { System.Windows.Forms.Keys.ControlKey });
            await Task.Delay(700);
        }
        finally
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText) && Clipboard.GetText(TextDataFormat.UnicodeText) == text)
                {
                    if (previous is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(previous, true);
                }
            }
            catch (Exception ex) { Settings.Log($"恢复 MCP 输入前的剪贴板失败：{ex.Message}"); }
        }
    }

    private static int RequiredInt(JsonElement args, string name)
    {
        if (args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)) return result;
        throw new ArgumentException($"参数 {name} 必须是整数。");
    }

    private static int OptionalInt(JsonElement args, string name, int fallback)
        => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static string RequiredString(JsonElement args, string name)
    {
        if (args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString()!;
        throw new ArgumentException($"参数 {name} 必须是字符串。");
    }

    private static string OptionalString(JsonElement args, string name, string fallback)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;

    private static System.Windows.Forms.Keys[] ReadModifiers(JsonElement args)
    {
        if (!args.TryGetProperty("modifiers", out var value)) return Array.Empty<System.Windows.Forms.Keys>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4)
            throw new ArgumentException("参数 modifiers 必须是最多包含 4 项的数组。");
        var keys = new System.Collections.Generic.List<System.Windows.Forms.Keys>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("修饰键名称必须是字符串。");
            var modifier = DesktopInput.ParseKey(item.GetString()!);
            if (modifier is not (System.Windows.Forms.Keys.ControlKey or System.Windows.Forms.Keys.ShiftKey or System.Windows.Forms.Keys.Menu or System.Windows.Forms.Keys.LWin or System.Windows.Forms.Keys.RWin))
                throw new ArgumentException("modifiers 仅支持 CTRL、SHIFT、ALT 或 WIN。");
            keys.Add(modifier);
        }
        return keys.ToArray();
    }

    private void PinClick(object sender, RoutedEventArgs e) { Topmost = settings.Topmost = PinBox.IsChecked == true; Save(); }
    private void SizingClick(object sender, RoutedEventArgs e)
    {
        var value = SizingBox.IsChecked == true;
        try { if (host.IsHandleCreated) host.SetSizing(value); settings.SmartSizing = value; Save(); }
        catch (Exception ex) { SizingBox.IsChecked = settings.SmartSizing; Error(ex); }
    }
    private void PreferencesClick(object sender, RoutedEventArgs e)
    {
        settings.Muted = MuteBox.IsChecked == true;
        settings.RemoteKeys = KeysBox.IsChecked == true;
        Save();
        Status("设置已保存，将在下次连接时生效。");
    }
    private void FullscreenClick(object sender, RoutedEventArgs e)
    {
        if (!UiState.CanFullscreen) return;
        try
        {
            var enter = !host.IsFullscreen;
            host.SetFullscreen(enter);
            SetFullscreenPresentation(enter);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void UpdateWindowControls()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
        FullscreenButton.Content = nativeFullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = nativeFullscreen ? "退出全屏" : "全屏";
        System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, nativeFullscreen ? "退出全屏" : "全屏");
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Save()
    {
        if (previewMode) return;
        try
        {
            if (WindowState == WindowState.Normal) { settings.Width = Width; settings.Height = Height; settings.Left = Left; settings.Top = Top; }
            settings.Save();
        }
        catch (Exception ex) { Settings.Log(ex.Message); }
    }
    private void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"STWin 0.1.0\n系统：{Environment.OSVersion}\n运行时：{Environment.Version}\nChild Sessions：{SessionApi.IsEnabled()}\n子会话：{SessionApi.ChildId()?.ToString() ?? "无"}\n连接状态：{host.ConnectionState}\nMCP：{mcpServer?.Endpoint ?? "未启动"}\nMCP 客户端配置：{Path.Combine(Settings.Folder, "mcp-connection.json")}\nRDP 窗口：{connectionBarTracker.LastGeometry}\n最近错误：{lastError}");
            Status("诊断信息已复制。");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ElevateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (controller.Busy || UiState.Busy || controller.ManagedId.HasValue) throw new InvalidOperationException("请先结束受管分身，再重新打开程序。");
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = $"--wait-for {Environment.ProcessId}" });
            CompleteExit();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private async Task ExitAsync()
    {
        if (previewMode) { CompleteExit(); return; }
        if (confirmationOpen) return;
        if (controller.Busy || UiState.Busy) { Reveal(); Status("正在处理桌面操作，请完成后再退出。"); return; }
        try
        {
            uint? current = null;
            var queryFailed = false;
            try { current = controller.RefreshSession(); }
            catch (Exception ex) { Error(ex); queryFailed = true; }
            var choice = current.HasValue || queryFailed ? ChooseExit(current.HasValue) : ExitChoice.KeepSession;
            if (choice == ExitChoice.Cancel) return;
            SetOperation(choice == ExitChoice.EndSession ? DesktopOperation.Ending : DesktopOperation.Disconnecting);
            if (await controller.ExitAsync(choice, current, lifetime.Token))
            {
                Status(choice == ExitChoice.EndSession ? "分身已结束，退出 STWin。" : "已断开连接，保留分身并退出 STWin。");
                CompleteExit();
            }
        }
        catch (Exception ex) { Reveal(); Error(ex); SetOperation(DesktopOperation.None); }
    }
    private void CompleteExit()
    {
        if (exiting) return;
        exiting = true;
        lifetime.Cancel();
        timer.Stop();
        mcpServer?.Dispose();
        mcpServer = null;
        connectionBarTracker.Dispose();
        Save();
        try { host.Dispose(); }
        catch (Exception ex) { Settings.Log($"释放桌面组件失败：{ex.Message}"); }
        finally { Application.Current.Shutdown(); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await ExitAsync(); }
            finally { closeRequested = false; }
        }));
    }
}


