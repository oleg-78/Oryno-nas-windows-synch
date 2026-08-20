using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class ActivityView : WpfUserControl
{
    public ActivityView() => InitializeComponent();
    private void Pause_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.PauseOrResume?.Invoke();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.OpenFolder?.Invoke();
}
