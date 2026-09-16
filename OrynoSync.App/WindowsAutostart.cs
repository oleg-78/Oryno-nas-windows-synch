using Microsoft.Win32;
using System.IO;
using OrynoSync.Core;
namespace OrynoSync.App;

/// <summary>
/// App-level autostart (§1, §3, §14): a per-user HKCU Run entry — no Task Scheduler, no elevation.
/// The entry always points at a stable executable, never at a path an update would delete.
/// The registry-independent rules live in <see cref="AutostartPolicy"/> and are unit-tested.
/// </summary>
internal static class WindowsAutostart
{
    private const string KeyPath=AutostartPolicy.RegistryKeyPath;
    private const string ValueName=AutostartPolicy.ValueName;

    /// <summary>Stable install location produced by `dotnet publish`; survives rebuilds and updates.</summary>
    public static string PublishedExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OrynoSync","app","OrynoSync.App.exe");

    public static string? RegisteredCommand { get { using var k=Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) as string; } }
    public static bool Enabled => !string.IsNullOrWhiteSpace(RegisteredCommand);

    public static string? PreferredExecutablePath() => AutostartPolicy.PreferredExecutable(PublishedExecutablePath, CurrentExecutablePath());

    public static bool IsValid() => AutostartPolicy.CommandMatches(RegisteredCommand, PreferredExecutablePath(), File.Exists(PublishedExecutablePath));

    public static bool IsTransientPath(string path) => AutostartPolicy.IsTransientPath(path);

    public static void Set(bool enabled)
    {
        using var key=Registry.CurrentUser.OpenSubKey(KeyPath,true)??Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key?.DeleteValue(ValueName,false); DiagnosticsLogger.Write("AUTOSTART_REGISTER", $"enabled=false mechanism={AutostartPolicy.Mechanism}"); return; }
        var executable=PreferredExecutablePath();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) throw new InvalidOperationException("The current executable path is unavailable.");
        var command=AutostartPolicy.BuildCommand(executable);
        key?.SetValue(ValueName,command,RegistryValueKind.String);
        DiagnosticsLogger.Write("AUTOSTART_REGISTER", $"enabled=true mechanism={AutostartPolicy.Mechanism} executable={executable} transient={IsTransientPath(executable)}");
    }

    /// <summary>Called on every launch: keeps the entry pointing at the executable that actually exists.</summary>
    public static bool ValidateAndRepair()
    {
        var command=RegisteredCommand; var current=CurrentExecutablePath(); var preferred=PreferredExecutablePath();
        var valid=IsValid();
        DiagnosticsLogger.Write("AUTOSTART_VALIDATE", $"mechanism={AutostartPolicy.Mechanism} registered={command ?? "missing"} current={current ?? "unknown"} preferred={preferred ?? "unknown"} valid={valid}");
        if (Enabled && !valid) { Set(true); return true; }
        return false;
    }

    /// <summary>Report block for the acceptance report (§14).</summary>
    public static string Describe()
    {
        var preferred=PreferredExecutablePath();
        return $"REGISTRY/STARTUP ENTRY: HKCU\\{KeyPath}\\{ValueName}{Environment.NewLine}" +
               $"METHOD: {AutostartPolicy.Mechanism} (per-user, no elevation, no Task Scheduler){Environment.NewLine}" +
               $"COMMAND: {RegisteredCommand ?? "(not registered)"}{Environment.NewLine}" +
               $"EXECUTABLE: {preferred ?? "(unknown)"}{Environment.NewLine}" +
               $"TRANSIENT PATH: {(preferred is not null && IsTransientPath(preferred) ? "YES (dev build output)" : "NO")}";
    }

    private static string? CurrentExecutablePath() => Environment.ProcessPath is { } p && Path.GetExtension(p).Equals(".exe",StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(p) : null;
}
