namespace OrynoSync.Core;

public enum ItemType { File, Directory }
public enum SyncItemState { Synced, Waiting, Uploading, Downloading, Conflict, Error, Ignored }
public enum OperationType { CreateFile, UpdateFile, CreateDirectory, Move, Delete }
public enum OperationState { Pending, InProgress, Completed, Failed, BlockedWaitingForServerCapability }
public enum EngineState { Disconnected, Connecting, AuthenticationRequired, InitialInventory, Reconciling, OnlineIdle, SyncingMetadata, Offline, Paused, ProtocolError, Error, Syncing=SyncingMetadata, UpToDate=OnlineIdle }

public sealed record LocalItem(string RelativePath, ItemType ItemType, long Size, DateTimeOffset Mtime, string? ContentHash = null, string? ServerItemId = null, long? ServerVersion = null, long? LastServerRevision = null, SyncItemState SyncState = SyncItemState.Synced, string? LastError = null);
public sealed record PendingOperation(Guid OperationId, OperationType Type, string RelativePath, string? SecondaryPath = null, DateTimeOffset? CreatedAt = null, int AttemptCount = 0, DateTimeOffset? NextAttemptAt = null, OperationState State = OperationState.Pending, string? LastError = null);
public sealed record ActivityEntry(string RelativePath, string Action, string Status, DateTimeOffset Timestamp, string? Error = null);

public interface ILocalStateStore
{
    Task InitializeAsync(CancellationToken ct = default); Task<IReadOnlyList<LocalItem>> GetItemsAsync(CancellationToken ct = default); Task<LocalItem?> GetItemAsync(string relativePath, CancellationToken ct = default); Task UpsertItemAsync(LocalItem item, CancellationToken ct = default); Task RemoveItemAsync(string relativePath, CancellationToken ct = default);
    Task EnqueueAsync(PendingOperation operation, CancellationToken ct = default); Task<IReadOnlyList<PendingOperation>> GetPendingAsync(DateTimeOffset now, CancellationToken ct = default); Task UpdateOperationAsync(PendingOperation operation, CancellationToken ct = default); Task<int> PendingCountAsync(CancellationToken ct = default); Task SaveSettingAsync(string key, string value, CancellationToken ct = default); Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
}
public interface ISyncApi
{
    Task AuthenticateAsync(CancellationToken ct = default); Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken ct = default); Task<IReadOnlyList<LocalItem>> GetInventoryAsync(CancellationToken ct = default); Task CreateUploadAsync(PendingOperation operation, CancellationToken ct = default); Task UploadChunkAsync(PendingOperation operation, Stream content, CancellationToken ct = default); Task CommitUploadAsync(PendingOperation operation, CancellationToken ct = default); Task CreateDirectoryAsync(PendingOperation operation, CancellationToken ct = default); Task MoveAsync(PendingOperation operation, CancellationToken ct = default); Task DeleteAsync(PendingOperation operation, CancellationToken ct = default);
}
public interface ICredentialStore { Task SaveAsync(string account, string secret, CancellationToken ct = default); Task<string?> ReadAsync(string account, CancellationToken ct = default); Task DeleteAsync(string account, CancellationToken ct = default); }
public interface IContentHasher { Task<string> ComputeAsync(string path, CancellationToken ct = default); }
public interface IAtomicFileWriter { Task ReplaceAsync(string targetPath, Stream content, string expectedHash, CancellationToken ct = default); }
public sealed class ServerCapabilityException(string capability) : Exception($"Server capability is unavailable: {capability}") { public string Capability { get; } = capability; }
public sealed class SyncApiException(System.Net.HttpStatusCode status, string code, string message) : Exception(message) { public System.Net.HttpStatusCode Status { get; } = status; public string Code { get; } = code; }
public static class PathRules
{
    public static string NormalizeRelative(string path) => path.Replace('/', '\\').TrimStart('\\');
    public static string ToRelative(string root, string fullPath) => NormalizeRelative(Path.GetRelativePath(root, fullPath));
    public static string ToAbsolute(string root, string relativePath) => Path.GetFullPath(Path.Combine(root, NormalizeRelative(relativePath)));
    public static bool IsCaseCollision(IEnumerable<string> paths) => paths.GroupBy(p => NormalizeRelative(p), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
    public static bool IsWindowsCompatible(string relativePath)
    {
        foreach (var part in NormalizeRelative(relativePath).Split('\\')) { if (string.IsNullOrWhiteSpace(part) || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false; var stem = Path.GetFileNameWithoutExtension(part).TrimEnd('.').ToUpperInvariant(); if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) return false; }
        return true;
    }
}
public sealed class IgnoreRules(IEnumerable<string>? patterns = null)
{
    private readonly string[] _patterns = (patterns ?? new[] { "Thumbs.db", "desktop.ini", "~$*" }).ToArray();
    public bool IsIgnored(string relativePath) { var name = Path.GetFileName(relativePath); return _patterns.Any(p => p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase)); }
}
