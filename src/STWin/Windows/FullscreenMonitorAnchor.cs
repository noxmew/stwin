using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace STWin.Windows;

internal static class FullscreenMonitorAnchor
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void Move(Window window, Rectangle monitor)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 STWin 窗口位置。");
        var width = current.Right - current.Left;
        var height = current.Bottom - current.Top;
        var target = ConnectionBarGeometry.CenterWindow(new Rectangle(0, 0, width, height), monitor);
        if (!SetWindowPos(handle, IntPtr.Zero, target.X, target.Y, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法把 STWin 窗口移到目标显示器。");
        if (System.Windows.Forms.Screen.FromHandle(handle).Bounds != monitor)
            throw new InvalidOperationException("STWin 宿主窗口移动后仍未处于目标显示器。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
