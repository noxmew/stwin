using System;
using STWin.Admin;
using STWin.Sessions;
using STWin.Updates;

namespace STWin;

internal static class EntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Service and helper processes never initialize WPF, RDP or the MCP listener.
        if (args is [AdminServiceHost.Argument, var sid]) return AdminServiceHost.Run(sid);
        if (args.Length > 0 && args[0] == AdminInstallation.Argument)
            return AdminInstallation.RunAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == AdminSessionWorker.Argument)
            return AdminSessionWorker.RunAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == AdminMaintenance.Argument)
            return AdminMaintenance.RunAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == ProgramLauncher.WorkerArgument)
            return ProgramLauncher.RunWorkerAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == DesktopSessionWorker.Argument)
            return DesktopSessionWorker.RunAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == UpdateWorker.Argument)
            return UpdateWorker.RunAsync(args).GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
