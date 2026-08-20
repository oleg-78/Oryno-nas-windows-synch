using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class SettingsView : WpfUserControl
{
    public SettingsView() => InitializeComponent();
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || vm.Connect is null) return;
        var token = TokenBox.Password;
        if (string.IsNullOrWhiteSpace(token)) return;
        await vm.Connect(vm.ServerUrl, token);
        TokenBox.Clear();
    }
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.TestConnection is not null) await vm.TestConnection(vm.ServerUrl);
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e) => (DataContext as SettingsViewModel)?.Disconnect?.Invoke();
    private void Reauthorize_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) { vm.Reauthorize?.Invoke(); TokenBox.Focus(); }
    }
}
