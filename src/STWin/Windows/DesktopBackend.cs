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
    public DesktopResolution Resolution { get; set; } = DesktopResolution.Default;
    public int State => host.ConnectionState;
    public void Connect() { host.Configure(settings, Resolution); host.Connect(); }
    public void Disconnect() => host.Disconnect();
}
