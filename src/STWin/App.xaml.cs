using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using STWin.Infrastructure;
using STWin.Commands;
using STWin.Updates;
using STWin.Windows;
using STWin.Sessions;
using STWin.Admin;

namespace STWin;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationWait;
    private CommandServer? commands;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke-test"))
        {
            var output = Path.Combine(AppContext.BaseDirectory, "smoke-test.txt");
            try
            {
                using var form = new System.Windows.Forms.Form();
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                using var rdp = new RdpHost();
                form.Controls.Add(rdp);
                form.CreateControl();
                _ = form.Handle;
                rdp.CreateControl();
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                rdp.Configure(new Settings());
                _ = rdp.IsFullscreen;
                var expected = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Size;
                var configured = rdp.DesktopSize;
                if (configured != expected) throw new InvalidOperationException($"分辨率配置不匹配：{configured} / {expected}");
                form.ClientSize = new System.Drawing.Size(640, 480);
                System.Windows.Forms.Application.DoEvents();
                if (rdp.DesktopSize != configured) throw new InvalidOperationException("调整窗口意外改变了桌面分辨率。");
                File.WriteAllText(output, $"PASS: RDP ActiveX created and configured; native fullscreen connection bar and ConnectToChildSession accepted. Primary display / configured desktop: {configured.Width} x {configured.Height}; unchanged after window resize. Connection={rdp.ConnectionState}; Enabled={SessionApi.IsEnabled()}; ChildId={SessionApi.ChildId()}\nNo session was enabled, connected or logged off.");
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] is not ("--preview" or "--control-host" or "--update-cleanup"))
        {
            Shutdown(await Cli.RunAsync(e.Args));
            return;
        }
        var name = InstanceIdentity.MutexName;
        if (e.Args.Contains("--preview")) name += ".preview";
        instance = new Mutex(true, name, out var created);
        if (!created)
        {
            try { if (!e.Args.Contains("--control-host")) { using var signal = EventWaitHandle.OpenExisting(name + ".activate"); signal.Set(); } }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
        Exception? setupError = null;
        if (!e.Args.Contains("--preview") && !e.Args.Contains("--control-host"))
        {
            try { await AdminClient.InitializeAsync(false, CancellationToken.None, synchronize: false); }
            catch (Exception ex) { setupError = ex; }
        }
        var window = new MainWindow(e.Args.Contains("--preview"));
        if (e.Args.Contains("--control-host"))
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Opacity = 0;
            window.Loaded += (_, _) => { window.HideForControl(); window.Opacity = 1; window.ShowInTaskbar = true; };
        }
        MainWindow = window;
        activationWait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Dispatcher.BeginInvoke(window.Reveal), null, Timeout.Infinite, false);
        if (e.Args.Contains("--preview"))
        {
            window.ShowInTaskbar = false;
            window.Opacity = 0;
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                await window.VerifyHostAsync();
                var content = (FrameworkElement)window.Content;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "preview.png"))) encoder.Save(file);
                await window.CaptureUiGalleryAsync();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke-test.txt"), "PASS: WPF window and RDP control initialized; preview rendered; native RDP fullscreen and connection bar configured; empty host window hide/restore verified. No connection initiated.");
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke-test.txt"), ex.ToString()); Environment.ExitCode = 1; }
                window.FinishPreview();
            };
        }
        window.Show();
        if (setupError is not null) window.ShowStartupError(setupError);
        if (!e.Args.Contains("--preview"))
        {
            try
            {
                commands = new CommandServer(window.ExecuteCommandAsync, window.RequestCliExit);
                commands.Start();
            }
            catch (Exception ex)
            {
                Settings.Log($"启动 CLI 服务失败：{ex}");
                MessageBox.Show(ex.Message, "STWin CLI 服务启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                window.RequestCliExit();
            }
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--update-cleanup" && int.TryParse(e.Args[2], out var installerId))
            _ = System.Threading.Tasks.Task.Run(() => UpdateWorker.CleanupAsync(e.Args[1], installerId));
    }
    protected override void OnExit(ExitEventArgs e)
    {
        commands?.Dispose();
        activationWait?.Unregister(null);
        activate?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
