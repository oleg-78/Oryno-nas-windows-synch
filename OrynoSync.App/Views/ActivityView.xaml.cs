using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class ActivityView : WpfUserControl
{
    public ActivityView() => InitializeComponent();
    private void Pause_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.PauseOrResume?.Invoke();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.OpenFolder?.Invoke();
    private void ToggleErrors_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ActivityViewModel vm) return;
        vm.ErrorsExpanded = !vm.ErrorsExpanded;
        ErrorList.Visibility = vm.ErrorsExpanded ? Visibility.Visible : Visibility.Collapsed;
        ((System.Windows.Controls.Button)sender).Content = vm.ErrorsExpanded ? "Show fewer" : "Show files";
    }
}
