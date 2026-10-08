using System.Windows;
using System.Windows.Input;
using STWin.Sessions;

namespace STWin.Views;

public partial class ExitDialog : Window
{
    internal ExitChoice Choice { get; private set; } = ExitChoice.Cancel;

    public ExitDialog(bool canEndSession = true)
    {
        InitializeComponent();
        if (!canEndSession)
        {
            Description.Text = "暂时无法确认分身状态。\n可以保留分身并退出，稍后重新打开尝试连接。";
            EndAction.IsEnabled = false;
        }
        Loaded += (_, _) => KeepAction.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void KeepClick(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.KeepSession;
        DialogResult = true;
    }

    private void EndClick(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.EndSession;
        DialogResult = true;
    }
}
