using System.Configuration;
using System.Data;
using System.Windows;

namespace OrynoSync.App;
using System.Threading;
using System.Windows;

public partial class App : Application
{
    public static readonly EventWaitHandle ActivateEvent = new(false, EventResetMode.AutoReset, "Local\\OrynoSync.Activate");
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\OrynoSync.SingleInstance", out var first);
        if (!first) { try { using var signal = EventWaitHandle.OpenExisting("Local\\OrynoSync.Activate"); signal.Set(); } catch { } Shutdown(); return; }
        base.OnStartup(e); MainWindow = new MainWindow(); MainWindow.Show();
    }
}

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
}

