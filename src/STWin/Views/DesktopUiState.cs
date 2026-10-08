namespace STWin.Views;

internal enum DesktopOperation { None, Preparing, Connecting, Ending, Disconnecting }

// All connection controls use the same snapshot; host visibility is an output, not a connection signal.
internal readonly record struct DesktopUiState(DesktopOperation Operation, int ConnectionState, bool HasSession, bool ConfirmationOpen)
{
    public bool Busy => Operation != DesktopOperation.None || ConfirmationOpen;
    public bool Connected => ConnectionState == 1;
    public bool ShowDesktop => Connected || Operation == DesktopOperation.Connecting;
    public bool CanStart => !Busy && !Connected;
    public bool CanStop => !Busy && HasSession;
    public bool CanFullscreen => !Busy && Connected;
    public string StartText => Operation switch
    {
        DesktopOperation.Preparing => "正在准备…",
        DesktopOperation.Connecting => "正在连接…",
        DesktopOperation.Ending => "正在结束…",
        DesktopOperation.Disconnecting => "正在退出…",
        _ => HasSession ? "继续连接" : "启动桌面"
    };
}
