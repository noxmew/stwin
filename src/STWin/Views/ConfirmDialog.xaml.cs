using System.Windows;
using System.Windows.Media;

namespace STWin.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string description, string action, string cancel = "取消", bool destructive = false)
    {
        InitializeComponent();
        Title = Heading.Text = title;
        Description.Text = description;
        ConfirmAction.Content = action;
        CancelAction.Content = cancel;
        if (destructive)
        {
            ConfirmAction.Background = new SolidColorBrush(Color.FromRgb(190, 67, 82));
            Badge.Background = new SolidColorBrush(Color.FromRgb(255, 241, 242));
            BadgeIcon.Foreground = ConfirmAction.Background;
            BadgeIcon.Text = "\uE7BA";
        }
        Loaded += (_, _) => CancelAction.Focus();
    }
    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
