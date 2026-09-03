using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrynoSync.Core;

namespace OrynoSync.App;

/// <summary>NAS destination picker. Opens directly inside the production sync root and shows
/// only folders. Technical/test sync roots are never rendered: the user never picks a protocol root.
/// First screen = the user's real NAS folders (breadcrumb: Oryno NAS / Работа / ...).</summary>
public partial class NasDestinationPickerDialog : Window
{
    private readonly Func<Guid, Task<IReadOnlyList<RemoteItemDto>>> _loadInventory;
    private readonly Func<Task<IReadOnlyList<SyncRootDto>>> _refreshRoots;
    private readonly Func<Guid, Guid?, string, Task> _createFolder;

    private IReadOnlyList<SyncRootDto> _roots = Array.Empty<SyncRootDto>();
    private NasDestinationTree? _tree;
    private readonly List<NasNode> _rowsNodes = new();

    private Guid _productionRoot;                 // production sync root entered automatically
    private Guid _treeRoot;                       // root whose inventory _tree was built from
    private Guid _current;                        // selected folder item id (== _productionRoot at top level)

    private const string _displayRootName = "Oryno NAS";

    /// <summary>Chosen root id (destination boundary of the current engine), or null if cancelled.</summary>
    public Guid? SelectedRootId { get; private set; }
    public Guid? SelectedDestinationItemId { get; private set; }
    public string? SelectedDestinationRelativePath { get; private set; }

    public NasDestinationPickerDialog(
        Func<Guid, Task<IReadOnlyList<RemoteItemDto>>> loadInventory,
        Func<Task<IReadOnlyList<SyncRootDto>>> refreshRoots,
        Func<Guid, Guid?, string, Task> createFolder,
        Guid? initialRootId = null)
    {
        InitializeComponent();
        _loadInventory = loadInventory;
        _refreshRoots = refreshRoots;
        _createFolder = createFolder;
        _productionRoot = initialRootId ?? Guid.Empty;
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    private async Task OnLoadedAsync()
    {
        try
        {
            await OpenProductionRootAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task OpenProductionRootAsync()
    {
        _roots = await _refreshRoots();
        // Производственный корень — предпочитаем тот, что отмечен purpose=production;
        // только если его нет — используем переданный initialRootId, иначе первый доступный.
        var production = _roots
            .FirstOrDefault(r => string.Equals(r.RootPurpose, "production", StringComparison.OrdinalIgnoreCase))
            ?? _roots.FirstOrDefault(r => r.RootId == _productionRoot)
            ?? _roots.FirstOrDefault();
        if (production is null)
        {
            StatusText.Text = "No NAS sync root available.";
            return;
        }
        _productionRoot = production.RootId;
        _treeRoot = production.RootId;
        var items = await _loadInventory(production.RootId);
        _tree = new NasDestinationTree(
            // Передаём только production root (test roots исключены из отображения/навигации).
            new[] { production },
            items, production.RootId);
        _current = production.RootId;
        Render();
    }

    private bool AtTop => _current == _productionRoot;

    private bool IsFolderSelected(Guid id) => _tree?.Find(id)?.IsFolder ?? true;

    private void Render()
    {
        _rowsNodes.Clear();
        var breadcrumb = "Oryno NAS";
        if (!AtTop && _tree != null)
        {
            var segs = _tree.Breadcrumb(_current);
            breadcrumb = segs.Count > 0 ? string.Join(" / ", segs) : breadcrumb;
        }
        BreadcrumbText.Text = breadcrumb;

        if (_tree != null)
        {
            // Показываем только папки; файлы в destination picker не отображаются.
            // Служебные/staging-папки (ignore-класс: .tmp.drive*, ~$*, Thumbs.db, ...)
            // тоже скрыты — пользователь выбирает только реальное NAS-назначение.
            var ignores = new IgnoreRules();
            foreach (var n in _tree.ChildrenOf(_current).Where(n => n.IsFolder && !ignores.IsIgnored(n.RelativePath)))
            {
                _rowsNodes.Add(n);
            }
        }

        ItemsList.ItemsSource = _rowsNodes
            .Select(n => "\uD83D\uDCC1 " + n.Name)
            .ToList();

        UpButton.IsEnabled = !AtTop;
        SelectButton.IsEnabled = true; // выбор доступен и на уровне корня (Oryno NAS целиком)
        StatusText.Text = _rowsNodes.Count == 0 ? "No folders here." : "";
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_rowsNodes.Count == 0 || ItemsList.SelectedIndex < 0) return;
        var node = _rowsNodes[ItemsList.SelectedIndex]; // only folders are rendered
        try
        {
            _current = node.ItemId;
            Render();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // single click = select; double click enters via ItemsList_MouseDoubleClick
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (AtTop || _tree == null) return;
        try
        {
            var parent = _tree.Find(_current)?.ParentItemId;
            _current = parent ?? _productionRoot;
            Render();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await OpenProductionRootAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_tree == null) return;
        var dlg = new CreateNasFolderDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var name = (dlg.FolderName ?? "").Trim();
        if (name.Length == 0) return;
        try
        {
            Guid? parent = AtTop ? null : (Guid?)_current;
            await _createFolder(_treeRoot, parent, name);
            await OpenProductionRootAsync(); // refresh; new folder appears and is selectable
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void Button_Click_Select(object sender, RoutedEventArgs e) => SelectDestination();

    private void SelectDestination()
    {
        if (_tree == null) return;
        if (AtTop)
        {
            SelectedRootId = _treeRoot;
            SelectedDestinationItemId = null;
            SelectedDestinationRelativePath = "";
        }
        else if (_tree.Find(_current) is NasNode n)
        {
            SelectedRootId = _treeRoot;
            SelectedDestinationItemId = n.ItemId;
            SelectedDestinationRelativePath = n.RelativePath ?? "";
        }
        else return;
        DialogResult = true;
        Close();
    }
}