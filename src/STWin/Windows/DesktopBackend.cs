using STWin.Infrastructure;
using STWin.Sessions;

namespace STWin.Windows;

internal sealed class SessionPlatform : ISessionPlatform
{
    public uint? ChildId() => SessionApi.ChildId();
    public void Enable() => SessionApi.Enable();
    public void Logoff(uint id) => SessionApi.Logoff(id);
}
internal sealed class DesktopConnection(RdpHost host, Settings settings) : IDesktopConnection
{
    public int State => host.ConnectionState;
    public void Connect() { host.Configure(settings); host.Connect(); }
    public void Disconnect() => host.Disconnect();
}
