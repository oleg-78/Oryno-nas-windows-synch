using System.Windows;
using System.IO;
using Forms=System.Windows.Forms;
using OrynoSync.Core;

namespace OrynoSync.App;
public partial class AddFolderWindow : Window
{
    public string LocalPath => PathBox.Text.Trim();
    public SyncRootDto? SelectedRoot => RootBox.SelectedItem as SyncRootDto;
    public AddFolderWindow(IReadOnlyList<SyncRootDto> roots)
    {
        InitializeComponent();RootBox.ItemsSource=roots;RootBox.SelectedIndex=-1;OfflineText.Visibility=roots.Count==0?Visibility.Visible:Visibility.Collapsed;AddButton.Content=roots.Count==0?"Add for later":"Add folder";
    }
    private void Browse_Click(object sender,RoutedEventArgs e){using var d=new Forms.FolderBrowserDialog{Description="Choose an existing local sync folder"};if(d.ShowDialog()==Forms.DialogResult.OK)PathBox.Text=d.SelectedPath;}
    private void Add_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(LocalPath)||!Directory.Exists(LocalPath)){System.Windows.MessageBox.Show(this,"Choose an existing local folder.","Oryno Sync",MessageBoxButton.OK,MessageBoxImage.Information);return;}DialogResult=true;Close();}
    private void Cancel_Click(object sender,RoutedEventArgs e){DialogResult=false;Close();}
}
