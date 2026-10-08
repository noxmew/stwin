using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace STWin.Windows;

internal static class SessionApi
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint id);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr server, uint id, [MarshalAs(UnmanagedType.Bool)] bool wait);

    public static bool IsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled)) throw Error("读取桌面分身能力失败");
        return enabled;
    }
    public static void Enable()
    {
        if (!IsEnabled() && !WTSEnableChildSessions(true)) throw Error("启用桌面分身失败");
    }
    public static uint? ChildId()
    {
        var success = WTSGetChildSessionId(out var id);
        var error = success ? 0 : Marshal.GetLastWin32Error();
        return DecodeChildQuery(success, id, error);
    }
    internal static uint? DecodeChildQuery(bool success, uint id, int error)
    {
        if (success) return id == uint.MaxValue ? null : id;
        // Windows also reports an absent child as ERROR_NOT_FOUND.
        // Do not infer absence from the output ID when other errors occur.
        if (error == 1168) return null;
        throw new Win32Exception(error, $"查询桌面分身失败（Windows {error}）：{new Win32Exception(error).Message}");
    }
    public static void Logoff(uint expectedId)
    {
        if (ChildId() != expectedId) throw new InvalidOperationException("桌面会话已变化，已取消结束操作。请重新连接后再试。");
        if (!WTSLogoffSession(IntPtr.Zero, expectedId, false)) throw Error("结束桌面分身失败");
    }
    private static Win32Exception Error(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation}（Windows {code}）：{new Win32Exception(code).Message}");
    }
}
