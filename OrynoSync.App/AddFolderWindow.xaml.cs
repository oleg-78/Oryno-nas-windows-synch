using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;
using OrynoSync.Core;

namespace OrynoSync.App;
public partial class AddFolderWindow : Window
{
    public string LocalPath => PathBox.Text.Trim();
    public SyncRootDto? SelectedRoot => RootBox.SelectedItem as SyncRootDto;
    public Guid? DestinationItemId { get; private set; }
    public string? DestinationRelativePath { get; private set; }
    public bool DestinationOnly { get; }
    private IReadOnlyList<SyncRootDto> _roots = Array.Empty<SyncRootDto>();
    private readonly Func<Task<IReadOnlyList<SyncRootDto>>>? _refreshRoots;
    private readonly Func<Guid, Guid?, string, Task>? _createNasFolder;
    private readonly Func<Guid, Task<IReadOnlyList<RemoteItemDto>>>? _loadInventory;

    public AddFolderWindow(IReadOnlyList<SyncRootDto> roots,
        Func<Task<IReadOnlyList<SyncRootDto>>>? refreshRoots = null,
        Func<Guid, Guid?, string, Task>? createNasFolder = null,
        Func<Guid, Task<IReadOnlyList<RemoteItemDto>>>? loadInventory = null,
        string? initialPath = null, bool destinationOnly = false)
    {
        InitializeComponent();
        _refreshRoots = refreshRoots;
        _createNasFolder = createNasFolder;
        _loadInventory = loadInventory;
        DestinationOnly = destinationOnly;
        PathBox.Text = initialPath ?? "";
        if (destinationOnly)
        {
            Title = "Oryno NAS - choose destination";
            LocalSection.Visibility = Visibility.Collapsed;
        }
        SetRoots(roots);
    }

    private void SetRoots(IReadOnlyList<SyncRootDto> roots)
    {
        _roots = roots.ToArray();
        RootBox.ItemsSource = roots.Where(x => x.Enabled).ToArray();
        RootBox.SelectedIndex = -1;
        OfflineText.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NasDestText.Text = "Oryno NAS";
        UpdateAddState();
    }

    private void UpdateAddState()
    {
        var localOk = DestinationOnly || (!string.IsNullOrWhiteSpace(LocalPath) && Directory.Exists(LocalPath));
        AddButton.IsEnabled = localOk && SelectedRoot != null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationOnly) return; // destination-only repair never opens the Windows folder picker
        using var d = new Forms.FolderBrowserDialog { Description = "Choose an existing local sync folder" };
        if (d.ShowDialog() == Forms.DialogResult.OK) PathBox.Text = d.SelectedPath;
    }

    private async void BrowseNAS_Click(object sender, RoutedEventArgs e)
    {
        if (_loadInventory is null || _createNasFolder is null || _refreshRoots is null)
        {
            System.Windows.MessageBox.Show(this, "NAS tree browsing is not available here.", "Oryno Sync",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var picker = new NasDestinationPickerDialog(_loadInventory, _refreshRoots, _createNasFolder) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedRootId is not Guid id) return;
            var root = _roots.FirstOrDefault(x => x.RootId == id);
            if (root is not null)
            {
                RootBox.SelectedItem = root;
                DestinationItemId = picker.SelectedDestinationItemId;
                DestinationRelativePath = picker.SelectedDestinationRelativePath ?? "";
                NasDestText.Text = "Oryno NAS" + (string.IsNullOrEmpty(DestinationRelativePath)
                    ? "" : " / " + DestinationRelativePath.Replace('\\', '/'));
                UpdateAddState();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Unable to browse the NAS tree.\n\n{ex.Message}", "Oryno NAS",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Root_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RootBox.SelectedItem is SyncRootDto r) { NasDestText.Text = "Oryno NAS / " + r.Name; DestinationItemId = null; DestinationRelativePath = null; }
        UpdateAddState();
    }

    private void Path_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateAddState();

    private async void CreateRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootBox.SelectedItem is not SyncRootDto root)
        {
            System.Windows.MessageBox.Show(this, "Select a NAS root first, then create a folder inside it.", "NAS root required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new CreateNasFolderDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.FolderName ?? "").Trim();
        if (name.Length == 0) return;
        if (_createNasFolder is null)
        {
            System.Windows.MessageBox.Show(this, "NAS folder creation is not available here.", "Oryno Sync",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { await _createNasFolder(root.RootId, null, name); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Unable to create the NAS folder.\n\n{ex.Message}", "Create NAS folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_refreshRoots != null) { try { SetRoots(await _refreshRoots()); } catch { } }
        System.Windows.MessageBox.Show(this, $"NAS folder \"{name}\" created in \"{root.Name}\".", "Folder created",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RefreshRoots_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshRoots is null) return;
        try { SetRoots(await _refreshRoots()); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Unable to refresh NAS roots.\n\n{ex.Message}", "Refresh failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!DestinationOnly && (string.IsNullOrWhiteSpace(LocalPath) || !Directory.Exists(LocalPath)))
        {
            System.Windows.MessageBox.Show(this, "Choose an existing local folder.", "Oryno Sync",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SelectedRoot is null)
        {
            System.Windows.MessageBox.Show(this, "Select a NAS destination before adding the folder.", "NAS destination required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}