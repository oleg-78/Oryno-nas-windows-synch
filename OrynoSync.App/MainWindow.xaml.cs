using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using OrynoSync.Core;
using OrynoSync.App.Views;

namespace OrynoSync.App;

public partial class MainWindow : Window
{
    private readonly string _appData=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Oryno Sync");
    private readonly ILocalStateStore _store;private readonly IRemoteStateStore _remoteStore;private readonly ICredentialStore _credentials;
    private readonly List<string> _activity=[];private LocalWatcher? _watcher;private CancellationTokenSource? _localCts;private CancellationTokenSource? _remoteCts;private HttpClient? _http;private OrynoNasSyncApi? _api;private MetadataSyncCoordinator? _metadata;private Forms.NotifyIcon? _tray;private string? _root;private Guid? _selectedRootId;private bool _paused;private bool _allowClose;private int _failures;private AppPage _currentPage=AppPage.Activity;
    private readonly ActivityViewModel _activityVm=new();private readonly FoldersViewModel _foldersVm=new();private readonly SettingsViewModel _settingsVm=new();
    private string DatabasePath=>Path.Combine(_appData,"oryno-sync.db");
    public MainWindow()
    {
        InitializeComponent();_store=new SqliteLocalStateStore(DatabasePath);_remoteStore=new RemoteStateStore(DatabasePath);_credentials=new WindowsCredentialStore(Path.Combine(_appData,"Credentials"));_activityVm.PauseOrResume=TogglePause;_activityVm.OpenFolder=OpenFolder_Click;_foldersVm.ChangeFolder=ChooseFolder;_foldersVm.OpenFolder=OpenFolder_Click;_settingsVm.Connect=(url,token)=>ConnectAsync(url,token);_settingsVm.TestConnection=url=>TestConnectionAsync(url);_settingsVm.DatabasePath=DatabasePath;_settingsVm.Device=Environment.MachineName+" · Windows";Loaded+=LoadedAsync;NavigateTo(AppPage.Activity);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=(_,_)=>{if(App.ActivateEvent.WaitOne(0))ShowFromTray();};timer.Start();
    }
    private async void LoadedAsync(object? sender,RoutedEventArgs e)
    {
        await _store.InitializeAsync();await _remoteStore.InitializeAsync();SetupTray();DeviceText.Text=Environment.MachineName+" · Windows";var server=await _store.GetSettingAsync("server_url")??"https://oryno-nas.remo78.ru";_settingsVm.ServerUrl=server;_root=await _store.GetSettingAsync("sync_root")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Oryno NAS");_foldersVm.LocalFolder=_root;var rootSetting=await _store.GetSettingAsync("selected_root_id");if(Guid.TryParse(rootSetting,out var rootId))_selectedRootId=rootId;if(Directory.Exists(_root)){_foldersVm.LocalStatus="Watcher running";await StartLocalAsync(_root);}else{_foldersVm.LocalStatus="Choose an existing local sync folder.";SetStatus(EngineState.Offline,"Choose an existing local sync folder. No folder was created automatically.");}InitializeProductionClient();_remoteCts=new CancellationTokenSource();_=RemoteLoopAsync(_remoteCts.Token);await RefreshCountersAsync();
    }
    private string CredentialAccount(Uri uri)=>$"oryno-sync:{uri.Scheme}://{uri.Host}:{uri.Port}";
    private void InitializeProductionClient()
    {
        _http?.Dispose();if(!TryServerUri(_settingsVm.ServerUrl,out var uri))return;var handler=new BearerTokenHandler(ct=>_credentials.ReadAsync(CredentialAccount(uri),ct)){InnerHandler=new SocketsHttpHandler{ConnectTimeout=TimeSpan.FromSeconds(8),PooledConnectionLifetime=TimeSpan.FromMinutes(10)}};_http=new HttpClient(handler);_api=new OrynoNasSyncApi(_http,uri);_metadata=new MetadataSyncCoordinator(_api,_remoteStore);_metadata.Progress+=p=>Dispatcher.Invoke(()=>SetStatus(p.State,p.Message));
    }
    private async Task StartLocalAsync(string root)
    {
        _watcher?.Dispose();_localCts?.Cancel();_localCts?.Dispose();_localCts=new CancellationTokenSource();var ignore=new IgnoreRules();var processor=new DebouncedChangeProcessor(_store,root,ignore,new LocalMutationSuppression());processor.Activity+=a=>AddActivity($"{a.RelativePath} · Local {a.Action} · Waiting to upload");_watcher=new LocalWatcher(root,processor);_watcher.Overflowed+=()=>_ = Task.Run(()=>new LocalReconciler(_store,ignore).ScanAsync(root,_localCts.Token));await Task.Run(()=>new LocalReconciler(_store,ignore).ScanAsync(root,_localCts.Token));_watcher.Start();
    }
    private async Task RemoteLoopAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){if(_paused){await Delay(1000,ct);continue;}try{if(_api is null||_metadata is null){await Delay(4000,ct);continue;}var connection=await _api.TestConnectionAsync(ct);if(connection.Status!=ConnectionStatus.Connected){SetConnection(connection);_failures++;await Delay(BackoffMs(),ct);continue;}_failures=0;SetStatus(EngineState.Connecting,"Connected. Reading sync roots…");var roots=await _metadata.GetRootsAsync(ct);await Dispatcher.InvokeAsync(()=>PopulateRoots(roots));if(_selectedRootId is Guid root){await _metadata.RunCycleAsync(root,ct);await RefreshRemoteAsync(root,ct);}else SetStatus(EngineState.OnlineIdle,roots.Count==0?"No sync folders are configured on Oryno NAS.":"Select an Oryno NAS sync root.");await RefreshCountersAsync();await Delay(4000,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(SyncApiException ex)when(ex.Status==System.Net.HttpStatusCode.Unauthorized){SetStatus(EngineState.AuthenticationRequired,"Oryno NAS authorization is required. Your local changes are safe.");_failures++;await Delay(BackoffMs(),ct);}catch(Exception ex){SetStatus(EngineState.Offline,"Server unavailable. Local changes remain queued.");AddActivity($"Connection error · {SafeError(ex)}");_failures++;await Delay(BackoffMs(),ct);}}
    }
    private int BackoffMs()=>Math.Min(60000,(int)(2000*Math.Pow(2,Math.Min(_failures,5))));private static async Task Delay(int ms,CancellationToken ct){try{await Task.Delay(ms,ct);}catch(OperationCanceledException){}}
    private void PopulateRoots(IReadOnlyList<SyncRootDto> roots){var enabled=roots.Where(x=>x.Enabled).ToArray();if(_selectedRootId is null&&enabled.Length==1)_selectedRootId=enabled[0].RootId;_foldersVm.SetRoots(enabled,_selectedRootId);}
    private async Task RefreshRemoteAsync(Guid rootId,CancellationToken ct){var state=await _remoteStore.GetRootStateAsync(rootId,ct);var count=await _remoteStore.CountRemoteItemsAsync(rootId,ct);var downloads=await _remoteStore.CountPlanningAsync(rootId,RemotePlanningState.NeedsDownload,ct);await Dispatcher.InvokeAsync(()=>_foldersVm.Stats=$"Remote metadata: {count:N0} items · Waiting for content: {downloads:N0} · Revision: {state.LastRevision:N0}");}
    private async Task RefreshCountersAsync(){var count=await _store.PendingCountAsync();await Dispatcher.InvokeAsync(()=>{_activityVm.QueueText=$"{count:N0} local changes waiting";_foldersVm.Stats=$"Files: —   Folders: —   Pending changes: {count:N0}";_settingsVm.QueueLength=count.ToString("N0");});}
    private void SetConnection(ConnectionResult result){var state=result.Status switch{ConnectionStatus.AuthenticationRequired or ConnectionStatus.AuthenticationRevoked=>EngineState.AuthenticationRequired,ConnectionStatus.ProtocolError or ConnectionStatus.TlsError=>EngineState.ProtocolError,_=>EngineState.Offline};var message=result.Status switch{ConnectionStatus.AuthenticationRequired=>"Server reachable — device authorization required.",ConnectionStatus.AuthenticationRevoked=>"Device authorization was revoked. Local changes are safe.",ConnectionStatus.TlsError=>"TLS certificate or handshake error.",ConnectionStatus.ProtocolError=>"Server reached, but the Sync protocol response is invalid.",_=>"Server unavailable. Local changes continue to queue."};SetStatus(state,message);}
    private void SetStatus(EngineState state,string message)=>Dispatcher.Invoke(()=>{SidebarStatus.Text=state.ToString();StatusText.Text=message;_activityVm.Status=message;_settingsVm.ConnectionStatus=state.ToString();if(_tray is not null)_tray.Text=("Oryno Sync — "+state).Substring(0,Math.Min(63,("Oryno Sync — "+state).Length));});
    private void AddActivity(string text)=>Dispatcher.Invoke(()=>_activityVm.Add($"{DateTime.Now:t}  {text}"));
    private static string SafeError(Exception ex)=>ex is SyncApiException api?$"{api.Code} ({(int)api.Status})":ex.GetType().Name;
    private async void ConnectAsync(string serverUrl,string token)
    {
        if(!TryServerUri(serverUrl,out var uri))return;if(string.IsNullOrWhiteSpace(token)){SetStatus(EngineState.AuthenticationRequired,"Enter the one-time device authorization token issued by Oryno NAS.");return;}SetStatus(EngineState.Connecting,"Testing device authorization…");using var tempHttp=new HttpClient(new BearerTokenHandler(_=>Task.FromResult<string?>(token)){InnerHandler=new SocketsHttpHandler{ConnectTimeout=TimeSpan.FromSeconds(8)}});var tempApi=new OrynoNasSyncApi(tempHttp,uri);var result=await tempApi.TestConnectionAsync();if(result.Status!=ConnectionStatus.Connected){SetConnection(result);return;}var old=await _store.GetSettingAsync("server_url");if(!string.Equals(old,uri.GetLeftPart(UriPartial.Authority),StringComparison.OrdinalIgnoreCase)){_selectedRootId=null;await _store.SaveSettingAsync("selected_root_id",string.Empty);}await _credentials.SaveAsync(CredentialAccount(uri),token);await _store.SaveSettingAsync("server_url",uri.GetLeftPart(UriPartial.Authority));_settingsVm.ServerUrl=uri.GetLeftPart(UriPartial.Authority);InitializeProductionClient();RestartRemoteLoop();SetStatus(EngineState.Connecting,"Device authorized. Loading roots…");
    }
    private async void TestConnectionAsync(string serverUrl){if(!TryServerUri(serverUrl,out _))return;InitializeProductionClient();if(_api is not null)SetConnection(await _api.TestConnectionAsync());}
    private bool TryServerUri(string value,out Uri uri){if(Uri.TryCreate(value.Trim(),UriKind.Absolute,out var parsed)&&parsed.Scheme==Uri.UriSchemeHttps){uri=parsed;return true;}uri=null!;SetStatus(EngineState.ProtocolError,"Production server URL must use HTTPS.");return false;}
    private async void ChooseFolder(){using var dialog=new Forms.FolderBrowserDialog{Description="Choose an existing Oryno Sync folder",SelectedPath=_root??string.Empty};if(dialog.ShowDialog()!=Forms.DialogResult.OK)return;_paused=true;_watcher?.Dispose();_localCts?.Cancel();_root=dialog.SelectedPath;await _store.SaveSettingAsync("sync_root",_root);_foldersVm.LocalFolder=_root;await StartLocalAsync(_root);_paused=false;RestartRemoteLoop();AddActivity($"Local folder changed · {_root}");}
    private void RestartRemoteLoop(){_remoteCts?.Cancel();_remoteCts?.Dispose();_remoteCts=new CancellationTokenSource();_failures=0;_=RemoteLoopAsync(_remoteCts.Token);}
    private void TogglePause(){_paused=!_paused;_activityVm.IsPaused=_paused;SetStatus(_paused?EngineState.Paused:EngineState.Connecting,_paused?"Metadata transfers paused. Local watcher remains active.":"Resuming metadata connection…");if(!_paused)RestartRemoteLoop();}
    private void OpenFolder_Click(){if(_root is not null&&Directory.Exists(_root))Process.Start(new ProcessStartInfo("explorer.exe",_root){UseShellExecute=true});}
    private void SetupTray(){_tray=new Forms.NotifyIcon{Icon=System.Drawing.SystemIcons.Application,Text="Oryno Sync",Visible=true};var menu=new Forms.ContextMenuStrip();menu.Items.Add("Oryno Sync");menu.Items.Add("Open Oryno NAS Folder",null,(_,_)=>OpenFolder_Click());menu.Items.Add("Open Oryno Sync",null,(_,_)=>ShowFromTray());menu.Items.Add("Pause Syncing",null,(_,_)=>TogglePause());menu.Items.Add("Exit",null,(_,_)=>{_allowClose=true;Close();});_tray.ContextMenuStrip=menu;_tray.DoubleClick+=(_,_)=>ShowFromTray();}
    private void ShowFromTray(){Show();WindowState=WindowState.Normal;Activate();}private void Window_Closing(object? sender,System.ComponentModel.CancelEventArgs e){if(!_allowClose){e.Cancel=true;Hide();return;}_remoteCts?.Cancel();_localCts?.Cancel();_watcher?.Dispose();_http?.Dispose();_tray?.Dispose();Dispatcher.BeginInvoke(()=>System.Windows.Application.Current.Shutdown());}
    private void Window_SizeChanged(object sender,SizeChangedEventArgs e){var narrow=ActualWidth<650;NavColumn.Width=narrow?new GridLength(0):new GridLength(190);Sidebar.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;CompactMenu.Visibility=narrow?Visibility.Visible:Visibility.Collapsed;}
    private void CompactMenu_Click(object sender,RoutedEventArgs e)=>NavigateTo(_currentPage);
    private void Activity_Click(object sender,RoutedEventArgs e)=>NavigateTo(AppPage.Activity);
    private void Folders_Click(object sender,RoutedEventArgs e)=>NavigateTo(AppPage.Folders);
    private void Settings_Click(object sender,RoutedEventArgs e)=>NavigateTo(AppPage.Settings);
    public void SelectPage(AppPage page)=>NavigateTo(page);
    private void NavigateTo(AppPage page){_currentPage=page;PageTitle.Text=page.ToString();CurrentView.Content=page switch{AppPage.Activity=>CreateActivityView(),AppPage.Folders=>CreateFoldersView(),AppPage.Settings=>CreateSettingsView(),_=>null};ActivityNav.Background=page==AppPage.Activity?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43,50,56)):System.Windows.Media.Brushes.Transparent;FoldersNav.Background=page==AppPage.Folders?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43,50,56)):System.Windows.Media.Brushes.Transparent;SettingsNav.Background=page==AppPage.Settings?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43,50,56)):System.Windows.Media.Brushes.Transparent;}
    private ActivityView CreateActivityView(){var view=new ActivityView{DataContext=_activityVm};return view;}
    private FoldersView CreateFoldersView(){var view=new FoldersView{DataContext=_foldersVm};return view;}
    private SettingsView CreateSettingsView(){var view=new SettingsView{DataContext=_settingsVm};return view;}
}

public enum AppPage { Activity, Folders, Settings }
