using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class SettingsView : WpfUserControl
{
    public SettingsView() => InitializeComponent();
    private void Connect_Click(object sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) vm.Connect?.Invoke(vm.ServerUrl, TokenBox.Password); }
    private void TestConnection_Click(object sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) vm.TestConnection?.Invoke(vm.ServerUrl); }
}
