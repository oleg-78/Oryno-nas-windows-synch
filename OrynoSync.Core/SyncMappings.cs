using Microsoft.Data.Sqlite;

namespace OrynoSync.Core;

public enum MappingStatus { UpToDate, Scanning, Syncing, Offline, Paused, AuthenticationRequired, LocalFolderUnavailable, ServerRootUnavailable, WaitingForContentSupport, Conflict, Error }
public sealed record SyncMapping(Guid MappingId, string LocalPath, Guid? ServerRootId, string? ServerRootName, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long? LastServerRevision, string InventoryState, DateTimeOffset? LastScanAt, string? LastError, MappingStatus Status = MappingStatus.Offline);
public sealed record MappingLocalItem(Guid MappingId, string RelativePath, ItemType ItemType, long Size, DateTimeOffset Mtime, SyncItemState SyncState = SyncItemState.Waiting);
public sealed record MappingPendingOperation(Guid OperationId, Guid MappingId, OperationType Type, string RelativePath, string? SecondaryPath, DateTimeOffset CreatedAt, int AttemptCount, DateTimeOffset NextAttemptAt, OperationState State, string? LastError);

public static class SyncMappingRules
{
    public static string CanonicalLocalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    public static bool Overlaps(string left, string right)
    {
        var a = CanonicalLocalPath(left) + Path.DirectorySeparatorChar;
        var b = CanonicalLocalPath(right) + Path.DirectorySeparatorChar;
        var x = CanonicalLocalPath(left);
        var y = CanonicalLocalPath(right);
        return x.Equals(y, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
}

public interface ISyncMappingStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SyncMapping>> GetMappingsAsync(CancellationToken ct = default);
    Task<SyncMapping?> GetMappingAsync(Guid id, CancellationToken ct = default);
    Task AddMappingAsync(SyncMapping mapping, CancellationToken ct = default);
    Task UpdateMappingAsync(SyncMapping mapping, CancellationToken ct = default);
    Task RemoveMappingAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<MappingLocalItem>> GetItemsAsync(Guid mappingId, CancellationToken ct = default);
    Task UpsertItemAsync(MappingLocalItem item, CancellationToken ct = default);
    Task RemoveItemAsync(Guid mappingId, string relativePath, CancellationToken ct = default);
    Task IndexBatchAsync(IReadOnlyList<MappingLocalItem> items, IReadOnlyList<MappingPendingOperation> operations, CancellationToken ct = default);
    Task EnqueueAsync(MappingPendingOperation operation, CancellationToken ct = default);
    Task<int> PendingCountAsync(Guid? mappingId = null, CancellationToken ct = default);
}

public sealed class SqliteSyncMappingStore(string databasePath) : ISyncMappingStore
{
    private readonly string _cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private static void Add(SqliteParameterCollection p, string n, object? v) => p.AddWithValue(n, v ?? DBNull.Value);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
CREATE TABLE IF NOT EXISTS sync_mappings(mapping_id TEXT PRIMARY KEY,local_path TEXT NOT NULL,canonical_local_path TEXT NOT NULL UNIQUE,server_root_id TEXT,server_root_name TEXT,enabled INTEGER NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,last_server_revision INTEGER,inventory_state TEXT NOT NULL,last_scan_at TEXT,last_error TEXT,status TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mapping_server_root ON sync_mappings(server_root_id) WHERE server_root_id IS NOT NULL;
CREATE TABLE IF NOT EXISTS mapping_local_items(mapping_id TEXT NOT NULL,relative_path TEXT NOT NULL,item_type TEXT NOT NULL,size INTEGER NOT NULL,mtime TEXT NOT NULL,sync_state TEXT NOT NULL,PRIMARY KEY(mapping_id,relative_path));
CREATE TABLE IF NOT EXISTS mapping_pending_operations(operation_id TEXT PRIMARY KEY,mapping_id TEXT NOT NULL,operation_type TEXT NOT NULL,relative_path TEXT NOT NULL,secondary_path TEXT,created_at TEXT NOT NULL,attempt_count INTEGER NOT NULL,next_attempt_at TEXT NOT NULL,state TEXT NOT NULL,last_error TEXT);
CREATE INDEX IF NOT EXISTS ix_mapping_pending_ready ON mapping_pending_operations(mapping_id,state,next_attempt_at);
""";
        await cmd.ExecuteNonQueryAsync(ct);
        await MigrateLegacyAsync(c, ct);
    }

    private async Task MigrateLegacyAsync(SqliteConnection c, CancellationToken ct)
    {
        await using var legacyTable = c.CreateCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='settings'");
        if (Convert.ToInt32(await legacyTable.ExecuteScalarAsync(ct)) == 0) return;
        await using var count = c.CreateCommand("SELECT COUNT(*) FROM sync_mappings");
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) != 0) return;
        await using var setting = c.CreateCommand("SELECT value FROM settings WHERE key='sync_root'");
        var root = await setting.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(root)) return;
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var insert = c.CreateCommand("INSERT INTO sync_mappings VALUES($id,$path,$canonical,$root,NULL,1,$created,$updated,NULL,'Migrated',NULL,NULL,'Offline')");
        Add(insert.Parameters,"$id",id.ToString()); Add(insert.Parameters,"$path",root); Add(insert.Parameters,"$canonical",SyncMappingRules.CanonicalLocalPath(root)); Add(insert.Parameters,"$root",null); Add(insert.Parameters,"$created",now.ToString("O")); Add(insert.Parameters,"$updated",now.ToString("O")); await insert.ExecuteNonQueryAsync(ct);
        await using var copyItems = c.CreateCommand("INSERT OR IGNORE INTO mapping_local_items SELECT $id,relative_path,item_type,size,mtime,sync_state FROM local_items"); Add(copyItems.Parameters,"$id",id.ToString()); await copyItems.ExecuteNonQueryAsync(ct);
        await using var copyOps = c.CreateCommand("INSERT OR IGNORE INTO mapping_pending_operations SELECT operation_id,$id,operation_type,relative_path,secondary_path,created_at,attempt_count,next_attempt_at,state,last_error FROM pending_operations"); Add(copyOps.Parameters,"$id",id.ToString()); await copyOps.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SyncMapping>> GetMappingsAsync(CancellationToken ct = default) { await using var c=Open(); await using var cmd=c.CreateCommand("SELECT mapping_id,local_path,server_root_id,server_root_name,enabled,created_at,updated_at,last_server_revision,inventory_state,last_scan_at,last_error,status FROM sync_mappings ORDER BY created_at"); await using var r=await cmd.ExecuteReaderAsync(ct); var a=new List<SyncMapping>(); while(await r.ReadAsync(ct)) a.Add(ReadMapping(r)); return a; }
    public async Task<SyncMapping?> GetMappingAsync(Guid id,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT mapping_id,local_path,server_root_id,server_root_name,enabled,created_at,updated_at,last_server_revision,inventory_state,last_scan_at,last_error,status FROM sync_mappings WHERE mapping_id=$id");Add(cmd.Parameters,"$id",id.ToString());await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadMapping(r):null;}
    private static SyncMapping ReadMapping(SqliteDataReader r)=>new(Guid.Parse(r.GetString(0)),r.GetString(1),r.IsDBNull(2)?null:Guid.Parse(r.GetString(2)),r.IsDBNull(3)?null:r.GetString(3),r.GetBoolean(4),DateTimeOffset.Parse(r.GetString(5)),DateTimeOffset.Parse(r.GetString(6)),r.IsDBNull(7)?null:r.GetInt64(7),r.GetString(8),r.IsDBNull(9)?null:DateTimeOffset.Parse(r.GetString(9)),r.IsDBNull(10)?null:r.GetString(10),Enum.Parse<MappingStatus>(r.GetString(11)));
    public async Task AddMappingAsync(SyncMapping m,CancellationToken ct=default)=>await WriteMappingAsync(m,"INSERT INTO sync_mappings VALUES($id,$path,$canonical,$root,$name,$enabled,$created,$updated,$rev,$inventory,$scan,$error,$status)",ct);
    public async Task UpdateMappingAsync(SyncMapping m,CancellationToken ct=default)=>await WriteMappingAsync(m,"UPDATE sync_mappings SET local_path=$path,canonical_local_path=$canonical,server_root_id=$root,server_root_name=$name,enabled=$enabled,updated_at=$updated,last_server_revision=$rev,inventory_state=$inventory,last_scan_at=$scan,last_error=$error,status=$status WHERE mapping_id=$id",ct);
    private async Task WriteMappingAsync(SyncMapping m,string sql,CancellationToken ct){await using var c=Open();await using var cmd=c.CreateCommand(sql);Add(cmd.Parameters,"$id",m.MappingId.ToString());Add(cmd.Parameters,"$path",m.LocalPath);Add(cmd.Parameters,"$canonical",SyncMappingRules.CanonicalLocalPath(m.LocalPath));Add(cmd.Parameters,"$root",m.ServerRootId?.ToString());Add(cmd.Parameters,"$name",m.ServerRootName);Add(cmd.Parameters,"$enabled",m.Enabled);Add(cmd.Parameters,"$created",m.CreatedAt.ToString("O"));Add(cmd.Parameters,"$updated",m.UpdatedAt.ToString("O"));Add(cmd.Parameters,"$rev",m.LastServerRevision);Add(cmd.Parameters,"$inventory",m.InventoryState);Add(cmd.Parameters,"$scan",m.LastScanAt?.ToString("O"));Add(cmd.Parameters,"$error",m.LastError);Add(cmd.Parameters,"$status",m.Status.ToString());await cmd.ExecuteNonQueryAsync(ct);}
    public async Task RemoveMappingAsync(Guid id,CancellationToken ct=default){await using var c=Open();await using var tx=c.BeginTransaction();foreach(var sql in new[]{"DELETE FROM mapping_local_items WHERE mapping_id=$id","DELETE FROM mapping_pending_operations WHERE mapping_id=$id","DELETE FROM sync_mappings WHERE mapping_id=$id"}){await using var cmd=c.CreateCommand(sql);cmd.Transaction=tx;Add(cmd.Parameters,"$id",id.ToString());await cmd.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
    public async Task<IReadOnlyList<MappingLocalItem>> GetItemsAsync(Guid id,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT mapping_id,relative_path,item_type,size,mtime,sync_state FROM mapping_local_items WHERE mapping_id=$id");Add(cmd.Parameters,"$id",id.ToString());await using var r=await cmd.ExecuteReaderAsync(ct);var a=new List<MappingLocalItem>();while(await r.ReadAsync(ct))a.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),Enum.Parse<ItemType>(r.GetString(2)),r.GetInt64(3),DateTimeOffset.Parse(r.GetString(4)),Enum.Parse<SyncItemState>(r.GetString(5))));return a;}
    public async Task UpsertItemAsync(MappingLocalItem i,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("INSERT INTO mapping_local_items VALUES($id,$p,$t,$s,$m,$state) ON CONFLICT(mapping_id,relative_path) DO UPDATE SET item_type=$t,size=$s,mtime=$m,sync_state=$state");Add(cmd.Parameters,"$id",i.MappingId.ToString());Add(cmd.Parameters,"$p",i.RelativePath);Add(cmd.Parameters,"$t",i.ItemType.ToString());Add(cmd.Parameters,"$s",i.Size);Add(cmd.Parameters,"$m",i.Mtime.ToString("O"));Add(cmd.Parameters,"$state",i.SyncState.ToString());await cmd.ExecuteNonQueryAsync(ct);}
    public async Task RemoveItemAsync(Guid id,string path,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("DELETE FROM mapping_local_items WHERE mapping_id=$id AND relative_path=$p");Add(cmd.Parameters,"$id",id.ToString());Add(cmd.Parameters,"$p",PathRules.NormalizeRelative(path));await cmd.ExecuteNonQueryAsync(ct);}
    public async Task IndexBatchAsync(IReadOnlyList<MappingLocalItem> items,IReadOnlyList<MappingPendingOperation> operations,CancellationToken ct=default){if(items.Count==0&&operations.Count==0)return;await using var c=Open();await using var tx=c.BeginTransaction();foreach(var i in items){await using var cmd=c.CreateCommand("INSERT INTO mapping_local_items VALUES($id,$p,$t,$s,$m,$state) ON CONFLICT(mapping_id,relative_path) DO UPDATE SET item_type=$t,size=$s,mtime=$m,sync_state=$state");cmd.Transaction=tx;Add(cmd.Parameters,"$id",i.MappingId.ToString());Add(cmd.Parameters,"$p",i.RelativePath);Add(cmd.Parameters,"$t",i.ItemType.ToString());Add(cmd.Parameters,"$s",i.Size);Add(cmd.Parameters,"$m",i.Mtime.ToString("O"));Add(cmd.Parameters,"$state",i.SyncState.ToString());await cmd.ExecuteNonQueryAsync(ct);}foreach(var o in operations){await using var cmd=c.CreateCommand("INSERT INTO mapping_pending_operations VALUES($id,$mapping,$type,$path,$secondary,$created,$attempt,$next,$state,$error) ON CONFLICT(operation_id) DO UPDATE SET state=$state,last_error=$error,next_attempt_at=$next,attempt_count=$attempt");cmd.Transaction=tx;Add(cmd.Parameters,"$id",o.OperationId.ToString());Add(cmd.Parameters,"$mapping",o.MappingId.ToString());Add(cmd.Parameters,"$type",o.Type.ToString());Add(cmd.Parameters,"$path",o.RelativePath);Add(cmd.Parameters,"$secondary",o.SecondaryPath);Add(cmd.Parameters,"$created",o.CreatedAt.ToString("O"));Add(cmd.Parameters,"$attempt",o.AttemptCount);Add(cmd.Parameters,"$next",o.NextAttemptAt.ToString("O"));Add(cmd.Parameters,"$state",o.State.ToString());Add(cmd.Parameters,"$error",o.LastError);await cmd.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
    public async Task EnqueueAsync(MappingPendingOperation o,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("INSERT INTO mapping_pending_operations VALUES($id,$mapping,$type,$path,$secondary,$created,$attempt,$next,$state,$error) ON CONFLICT(operation_id) DO UPDATE SET state=$state,last_error=$error,next_attempt_at=$next,attempt_count=$attempt");Add(cmd.Parameters,"$id",o.OperationId.ToString());Add(cmd.Parameters,"$mapping",o.MappingId.ToString());Add(cmd.Parameters,"$type",o.Type.ToString());Add(cmd.Parameters,"$path",o.RelativePath);Add(cmd.Parameters,"$secondary",o.SecondaryPath);Add(cmd.Parameters,"$created",o.CreatedAt.ToString("O"));Add(cmd.Parameters,"$attempt",o.AttemptCount);Add(cmd.Parameters,"$next",o.NextAttemptAt.ToString("O"));Add(cmd.Parameters,"$state",o.State.ToString());Add(cmd.Parameters,"$error",o.LastError);await cmd.ExecuteNonQueryAsync(ct);}
    public async Task<int> PendingCountAsync(Guid? id=null,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand(id is null?"SELECT COUNT(*) FROM mapping_pending_operations WHERE state <> 'Completed'":"SELECT COUNT(*) FROM mapping_pending_operations WHERE mapping_id=$id AND state <> 'Completed'");if(id is not null)Add(cmd.Parameters,"$id",id.Value.ToString());return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));}
}
