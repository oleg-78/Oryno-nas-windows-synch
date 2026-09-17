using System.Configuration;
using System.Data;
using System.Windows;

namespace OrynoSync.App;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public partial class App : Application
{
    public static bool SampleMappings { get; private set; }
    public static bool CleanupSampleMappings { get; private set; }
    public static readonly EventWaitHandle ActivateEvent = new(false, EventResetMode.AutoReset, "Local\\OrynoSync.Activate");
    private Mutex? _mutex;
    private FileStream? _instanceLock;
    internal static bool StartedFromAutostart { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        StartedFromAutostart = e.Args.Any(x => x.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        DiagnosticsLogger.Write(StartedFromAutostart ? "AUTOSTART_LAUNCH" : "APP_LAUNCH", $"executable={Environment.ProcessPath ?? "unknown"} args={string.Join(" ", e.Args)}");
        _mutex = new Mutex(true, "Local\\OrynoSync.SingleInstance", out var first);
        // §8: the "Local\" mutex is scoped to ONE Windows session, so a second engine could still start
        // from another session (console + RDP, or a script). A per-user lock file is session-independent.
        try
        {
            var lockDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrynoSync");
            Directory.CreateDirectory(lockDir);
            _instanceLock = new FileStream(Path.Combine(lockDir, "oryno-sync.instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            first = false; // another instance already holds the lock
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write("SINGLE_INSTANCE_LOCK_FAILED", ex.Message); // never block startup on a lock quirk
        }
        if (!first) { try { using var signal = EventWaitHandle.OpenExisting("Local\\OrynoSync.Activate"); signal.Set(); } catch { } Shutdown(); return; }
        base.OnStartup(e); MainWindow = new MainWindow();
        SampleMappings = e.Args.Any(x => x.Equals("--sample-mappings", StringComparison.OrdinalIgnoreCase));
        CleanupSampleMappings = e.Args.Any(x => x.Equals("--cleanup-sample-mappings", StringComparison.OrdinalIgnoreCase));
        if (int.TryParse(e.Args.FirstOrDefault(x => x.StartsWith("--width=", StringComparison.OrdinalIgnoreCase))?["--width=".Length..], out var width)) MainWindow.Width = width;
        MainWindow.Show();
        if (StartedFromAutostart) MainWindow.Hide();
        var capture = e.Args.FirstOrDefault(x => x.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase));
        if (capture is not null)
        {
            var path = capture["--capture=".Length..];
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                await Task.Delay(1200); var window = (MainWindow)MainWindow; var page = e.Args.FirstOrDefault(x => x.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?["--page=".Length..]; if (Enum.TryParse<AppPage>(page, true, out var selectedPage)) window.SelectPage(selectedPage); if(e.Args.Any(x=>x.Equals("--add-folder-dialog",StringComparison.OrdinalIgnoreCase))) window.ShowAddFolderDialogForCapture(); window.UpdateLayout();
                var target = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is AddFolderWindow && w.IsVisible) ?? window; target.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var stream = File.Create(path); encoder.Save(stream); window.Close();
            }));
        }
    }
}

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
}

