using System.Windows;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace OrynoSync.App.Views;
public partial class FoldersView : WpfUserControl
{
    public FoldersView() => InitializeComponent();
    private void AddFolder_Click(object sender, RoutedEventArgs e) => (DataContext as FoldersViewModel)?.AddFolder?.Invoke();
    private static MappingCardViewModel? Mapping(object sender) => (sender as FrameworkElement)?.DataContext as MappingCardViewModel;
    private void Open_Click(object sender, RoutedEventArgs e) => Mapping(sender)?.Open?.Invoke();
    private void Pause_Click(object sender, RoutedEventArgs e) => Mapping(sender)?.Pause?.Invoke();
    private void Remove_Click(object sender, RoutedEventArgs e) => Mapping(sender)?.Remove?.Invoke();
    private void Rebuild_Click(object sender, RoutedEventArgs e) => Mapping(sender)?.Rebuild?.Invoke();
}
