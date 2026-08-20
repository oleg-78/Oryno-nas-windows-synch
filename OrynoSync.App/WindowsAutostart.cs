using Microsoft.Win32;
namespace OrynoSync.App;
internal static class WindowsAutostart
{
    private const string KeyPath="Software\\Microsoft\\Windows\\CurrentVersion\\Run"; private const string ValueName="Oryno Sync";
    public static bool Enabled { get { using var k=Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) is not null; } }
    public static void Set(bool enabled){using var k=Registry.CurrentUser.OpenSubKey(KeyPath,true)??Registry.CurrentUser.CreateSubKey(KeyPath);if(enabled)k?.SetValue(ValueName,$"\"{Environment.ProcessPath}\"");else k?.DeleteValue(ValueName,false);}
}
