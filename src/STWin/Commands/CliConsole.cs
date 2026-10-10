using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace STWin.Commands;

internal static class CliConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int id, IntPtr handle);

    public static void Attach()
    {
        var ids = new[] { -10, -11, -12 };
        var handles = Array.ConvertAll(ids, GetStdHandle);
        var types = Array.ConvertAll(handles, GetFileType);
        // Attaching must preserve stdin/stdout/stderr already redirected by the caller.
        var attached = AttachConsole(uint.MaxValue);
        for (var i = 0; i < ids.Length; i++)
            if (types[i] is 1 or 3) _ = SetStdHandle(ids[i], handles[i]);
        if (attached) Console.OutputEncoding = new UTF8Encoding(false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }
}
