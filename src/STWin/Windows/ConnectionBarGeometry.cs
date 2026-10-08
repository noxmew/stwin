using System.Drawing;

namespace STWin.Windows;

internal static class ConnectionBarGeometry
{
    public static bool IsOnFullscreen(Rectangle bar, Rectangle fullscreen) =>
        bar.Left >= fullscreen.Left && bar.Right <= fullscreen.Right &&
        bar.Top >= fullscreen.Top - bar.Height && bar.Top <= fullscreen.Top;

    public static Point CenterWindow(Rectangle window, Rectangle monitor) => new(
        monitor.Left + (monitor.Width - window.Width) / 2,
        monitor.Top + (monitor.Height - window.Height) / 2);
}
