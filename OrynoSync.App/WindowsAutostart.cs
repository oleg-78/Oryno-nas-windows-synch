using Microsoft.Win32;
using System.IO;
namespace OrynoSync.App;
internal static class WindowsAutostart
{
    private const string KeyPath="Software\\Microsoft\\Windows\\CurrentVersion\\Run"; private const string ValueName="Oryno Sync";
    public static string? RegisteredCommand { get { using var k=Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) as string; } }
    public static bool Enabled => !string.IsNullOrWhiteSpace(RegisteredCommand);
    public static bool IsValid() => TryGetExecutable(RegisteredCommand, out var path) && PathsEqual(path, CurrentExecutablePath());
    public static void Set(bool enabled)
    {
        using var key=Registry.CurrentUser.OpenSubKey(KeyPath,true)??Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key?.DeleteValue(ValueName,false); DiagnosticsLogger.Write("AUTOSTART_REGISTER", "enabled=false mechanism=HKCU_Run"); return; }
        var executable=CurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) throw new InvalidOperationException("The current executable path is unavailable.");
        var command=$"\"{executable}\" --autostart";
        key?.SetValue(ValueName,command,RegistryValueKind.String);
        DiagnosticsLogger.Write("AUTOSTART_REGISTER", $"enabled=true mechanism=HKCU_Run executable={executable}");
    }
    public static void ValidateAndRepair()
    {
        var command=RegisteredCommand; var current=CurrentExecutablePath();
        DiagnosticsLogger.Write("AUTOSTART_VALIDATE", $"mechanism=HKCU_Run registered={command ?? "missing"} current={current ?? "unknown"} valid={IsValid()}");
        if (Enabled && !IsValid()) Set(true);
    }
    private static string? CurrentExecutablePath() => Environment.ProcessPath is { } p && Path.GetExtension(p).Equals(".exe",StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(p) : null;
    private static bool TryGetExecutable(string? command,out string? path){path=null;if(string.IsNullOrWhiteSpace(command))return false;var value=command.Trim();if(value.StartsWith('"')){var end=value.IndexOf('"',1);if(end>1)path=value[1..end];}else{var end=value.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);if(end>=0)path=value[..(end+4)];}return !string.IsNullOrWhiteSpace(path);}
    private static bool PathsEqual(string? a,string? b)=>a is not null&&b is not null&&string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
}
