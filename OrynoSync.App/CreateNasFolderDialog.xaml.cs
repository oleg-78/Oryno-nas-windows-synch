using System.Windows;

namespace OrynoSync.App;

public partial class CreateNasFolderDialog : Window
{
    private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public string? FolderName { get; private set; }

    public CreateNasFolderDialog()
    {
        InitializeComponent();
        FolderNameBox.Focus();
    }

    private static bool IsValidFolderName(string name) =>
        name.Length > 0 && name.IndexOfAny(InvalidNameChars) < 0;

    private void FolderNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        Confirm();
    }

    private void Create_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        var name = FolderNameBox.Text.Trim();
        if (!IsValidFolderName(name))
        {
            ErrorText.Text = "Enter a folder name without: \\ / : * ? \" < > |";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        FolderName = name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}