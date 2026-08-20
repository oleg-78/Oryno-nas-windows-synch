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
    public static readonly EventWaitHandle ActivateEvent = new(false, EventResetMode.AutoReset, "Local\\OrynoSync.Activate");
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\OrynoSync.SingleInstance", out var first);
        if (!first) { try { using var signal = EventWaitHandle.OpenExisting("Local\\OrynoSync.Activate"); signal.Set(); } catch { } Shutdown(); return; }
        base.OnStartup(e); MainWindow = new MainWindow();
        if (int.TryParse(e.Args.FirstOrDefault(x => x.StartsWith("--width=", StringComparison.OrdinalIgnoreCase))?["--width=".Length..], out var width)) MainWindow.Width = width;
        MainWindow.Show();
        var capture = e.Args.FirstOrDefault(x => x.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase));
        if (capture is not null)
        {
            var path = capture["--capture=".Length..];
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                await Task.Delay(1200); var window = (MainWindow)MainWindow; window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
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

