using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class ActivityView : WpfUserControl
{
    public ActivityView() => InitializeComponent();
    private void StartSync_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.StartSync?.Invoke();
    private void StopSync_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.StopSync?.Invoke();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.OpenFolder?.Invoke();
    /// <summary>§18: manual retry for one card, or for every active card at once.</summary>
    private void RetryOperation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ActivityViewModel vm) return;
        if ((sender as System.Windows.FrameworkElement)?.DataContext is SyncFileErrorRowViewModel row) row.Retry?.Invoke();
        else vm.RetryOperation?.Invoke(Guid.Empty);
    }
    private void RetryAll_Click(object sender, RoutedEventArgs e) => (DataContext as ActivityViewModel)?.RetryErrors?.Invoke();
    private void ToggleErrors_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ActivityViewModel vm) return;
        vm.ErrorsExpanded = !vm.ErrorsExpanded;
        ErrorList.Visibility = vm.ErrorsExpanded ? Visibility.Visible : Visibility.Collapsed;
        ((System.Windows.Controls.Button)sender).Content = vm.ErrorsExpanded ? "Show fewer" : "Show files";
    }
}
