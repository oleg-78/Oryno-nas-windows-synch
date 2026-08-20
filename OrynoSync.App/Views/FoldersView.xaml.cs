using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class FoldersView : WpfUserControl
{
    public FoldersView() => InitializeComponent();
    private void ChangeFolder_Click(object sender, RoutedEventArgs e) => (DataContext as FoldersViewModel)?.ChangeFolder?.Invoke();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => (DataContext as FoldersViewModel)?.OpenFolder?.Invoke();
}
