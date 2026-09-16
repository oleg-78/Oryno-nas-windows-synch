using System.IO;

namespace OrynoSync.Core;

/// <summary>
/// Registry-independent autostart rules (§1/§3/§14) so they can be unit-tested without touching
/// the user's real HKCU hive. The Windows layer supplies the registry and process paths.
/// </summary>
public static class AutostartPolicy
{
    public const string Mechanism = "HKCU_Run";
    public const string ValueName = "Oryno Sync";
    public const string RegistryKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    /// <summary>Command line written to the Run key. --autostart makes the app start into the tray.</summary>
    public static string BuildCommand(string executable) => $"\"{Path.GetFullPath(executable)}\" --autostart";

    /// <summary>Extracts the executable from a Run value (quoted or bare).</summary>
    public static bool TryParseExecutable(string? command, out string? executable)
    {
        executable = null;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var value = command.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1) executable = value[1..end];
        }
        else
        {
            var end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end >= 0) executable = value[..(end + 4)];
        }
        return !string.IsNullOrWhiteSpace(executable);
    }

    /// <summary>Build output and temp folders are not durable: an update may delete them.</summary>
    public static bool IsTransientPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Stable target: the published build when it exists, otherwise the running executable.</summary>
    public static string? PreferredExecutable(string? publishedPath, string? currentProcessPath)
    {
        if (publishedPath is { Length: > 0 } p && File.Exists(p)) return p;
        return currentProcessPath;
    }

    /// <summary>True when the registered command still points at the executable we want to use (§14).</summary>
    public static bool CommandMatches(string? registeredCommand, string? preferredExecutable, bool publishedExists)
    {
        if (!TryParseExecutable(registeredCommand, out var registered) || registered is null) return false;
        if (!File.Exists(registered)) return false;
        if (IsTransientPath(registered) && publishedExists) return false;
        if (preferredExecutable is null) return false;
        return string.Equals(Path.GetFullPath(registered), Path.GetFullPath(preferredExecutable), StringComparison.OrdinalIgnoreCase);
    }
}
