using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using STWin.Infrastructure;

namespace STWin.Windows;

// Observe the two windows created by the RDP control. Their positions are
// controlled by RDP; moving BBarWindowClass directly is not a stable API.
internal sealed class RdpConnectionBarTracker : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint GwOwner = 4;
    private readonly WinEventCallback callback;
    private IntPtr showHook;
    private IntPtr locationHook;
    private IntPtr fullscreenWindow;
    private IntPtr barWindow;
    private bool active;
    private bool loggedFailure;

    public event Action<Rectangle>? MonitorMismatch;
    public bool? IsAligned { get; private set; }
    public string LastGeometry { get; private set; } = "尚未检测到原生全屏窗口";

    public RdpConnectionBarTracker() => callback = OnWinEvent;

    public void Start()
    {
        if (active) return;
        active = true;
        var process = (uint)Environment.ProcessId;
        showHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, callback, process, 0, 0);
        locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, callback, process, 0, 0);
        if (showHook == IntPtr.Zero || locationHook == IntPtr.Zero)
        {
            Settings.Log($"RDP 窗口位置监听不可用：{new Win32Exception(Marshal.GetLastWin32Error()).Message}；使用定时检查。");
            if (showHook != IntPtr.Zero) UnhookWinEvent(showHook);
            if (locationHook != IntPtr.Zero) UnhookWinEvent(locationHook);
            showHook = locationHook = IntPtr.Zero;
        }
        Synchronize();
    }

    public void Stop()
    {
        active = false;
        if (showHook != IntPtr.Zero) UnhookWinEvent(showHook);
        if (locationHook != IntPtr.Zero) UnhookWinEvent(locationHook);
        showHook = locationHook = fullscreenWindow = barWindow = IntPtr.Zero;
        loggedFailure = false;
        IsAligned = null;
    }

    public void Dispose() => Stop();

    public void Synchronize()
    {
        if (!active) return;
        try
        {
            // Re-entering RDP fullscreen can replace both HWNDs while an older
            // hidden HWND still exists. Always select the current visible pair.
            DiscoverWindows();
            if (fullscreenWindow == IntPtr.Zero || barWindow == IntPtr.Zero || !IsWindowVisible(fullscreenWindow))
            { IsAligned = null; return; }
            if (!GetWindowRect(fullscreenWindow, out var full) || !GetWindowRect(barWindow, out var bar))
            { IsAligned = null; return; }
            var fullRect = ToRectangle(full);
            var barRect = ToRectangle(bar);
            if (fullRect.Width <= 0 || fullRect.Height <= 0 || barRect.Width <= 0 || barRect.Height <= 0)
            { IsAligned = null; return; }
            IsAligned = ConnectionBarGeometry.IsOnFullscreen(barRect, fullRect);
            LastGeometry = $"画面 {fullRect}；连接条 {barRect}；同屏 {IsAligned.Value}";
            if (IsAligned.Value) return;
            MonitorMismatch?.Invoke(System.Windows.Forms.Screen.FromHandle(fullscreenWindow).Bounds);
        }
        catch (Exception ex)
        {
            if (loggedFailure) return;
            loggedFailure = true;
            Settings.Log($"检查 RDP 连接条位置失败：{ex.Message}");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (!active || objectId != 0 || childId != 0) return;
        try
        {
            if (!IsWindow(fullscreenWindow) || !IsWindow(barWindow)) DiscoverWindows();
            if (hwnd == fullscreenWindow || hwnd == barWindow) Synchronize();
        }
        catch (Exception ex)
        {
            if (loggedFailure) return;
            loggedFailure = true;
            Settings.Log($"RDP 窗口事件处理失败：{ex.Message}");
        }
    }

    private void DiscoverWindows()
    {
        fullscreenWindow = barWindow = IntPtr.Zero;
        var fullCandidates = new List<IntPtr>();
        var barCandidates = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var process);
            if (process != (uint)Environment.ProcessId) return true;
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "UIMainClass") fullCandidates.Add(hwnd);
            else if (name.ToString() == "BBarWindowClass") barCandidates.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var full in fullCandidates)
        {
            if (!IsWindowVisible(full)) continue;
            foreach (var bar in barCandidates)
            {
                if (GetWindow(bar, GwOwner) != full) continue;
                fullscreenWindow = full;
                barWindow = bar;
                return;
            }
        }
    }

    private static Rectangle ToRectangle(NativeRect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr state);
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
