using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace STWin.Windows;

internal static class LocalClientIdentity
{
    public static void Verify(TcpClient client)
    {
        try
        {
            if (client.Client.LocalEndPoint is not IPEndPoint server || client.Client.RemoteEndPoint is not IPEndPoint peer ||
                !IPAddress.IsLoopback(server.Address) || !IPAddress.IsLoopback(peer.Address))
                throw new UnauthorizedAccessException("MCP 只允许本机连接。");
            var processId = FindProcess(peer, server);
            using var process = SessionProcess.OpenForInspection(processId);
            using var identity = WindowsIdentity.GetCurrent();
            if (SessionProcess.UserSid(processId) != identity.User?.Value ||
                SessionProcess.SessionId(processId) != SessionProcess.SessionId((uint)Environment.ProcessId))
                throw new UnauthorizedAccessException("MCP 只允许当前 Windows 用户和登录会话调用。");
        }
        catch (Win32Exception) { throw new UnauthorizedAccessException("无法验证 MCP 调用用户。"); }
    }

    private static uint FindProcess(IPEndPoint local, IPEndPoint remote)
    {
        uint size = 0;
        var error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0); // AF_INET, TCP_TABLE_OWNER_PID_ALL
        if (error != 122) throw new Win32Exception(checked((int)error));
        while (true)
        {
            if (size < 4 || size > 32 * 1024 * 1024) throw new InvalidDataException("本机 TCP 连接表大小无效。");
            var memory = Marshal.AllocHGlobal(checked((int)size));
            var capacity = size;
            try
            {
                error = GetExtendedTcpTable(memory, ref size, false, 2, 5, 0);
                if (error == 122) continue; // The connection table grew between the two native calls.
                if (error != 0) throw new Win32Exception(checked((int)error));
                var count = Marshal.ReadInt32(memory);
                var rowSize = Marshal.SizeOf<TcpRow>();
                if (count < 0 || 4L + (long)count * rowSize > capacity) throw new InvalidDataException("本机 TCP 连接表无效。");
                var localAddress = BitConverter.ToUInt32(local.Address.GetAddressBytes());
                var remoteAddress = BitConverter.ToUInt32(remote.Address.GetAddressBytes());
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(memory + 4 + index * rowSize);
                    if (row.LocalAddress == localAddress && row.RemoteAddress == remoteAddress &&
                        Port(row.LocalPort) == local.Port && Port(row.RemotePort) == remote.Port && row.ProcessId != 0)
                        return row.ProcessId;
                }
                throw new UnauthorizedAccessException("没有找到 MCP 调用进程。");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }

    private static uint Port(uint value) => ((value & 255) << 8) | ((value >> 8) & 255);
    [StructLayout(LayoutKind.Sequential)] private struct TcpRow
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        uint family, uint tableClass, uint reserved);
}
