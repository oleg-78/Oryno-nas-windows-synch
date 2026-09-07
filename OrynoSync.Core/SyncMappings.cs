using Microsoft.Data.Sqlite;

namespace OrynoSync.Core;

public enum MappingStatus { UpToDate, Scanning, Syncing, Offline, Paused, AuthenticationRequired, LocalFolderUnavailable, ServerRootUnavailable, WaitingForContentSupport, Conflict, Error, ReadyForPreflight, ReadyToSync, Stopped }
public sealed record SyncMapping(Guid MappingId, string LocalPath, Guid? ServerRootId, string? ServerRootName, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long? LastServerRevision, string InventoryState, DateTimeOffset? LastScanAt, string? LastError, MappingStatus Status = MappingStatus.Offline, Guid? ServerDestinationItemId = null, string? ServerDestinationRelativePath = null)
{
    /// <summary>Root-relative server path of the nested destination (e.g. "Work" or "Work\Contracts");
    /// empty when the destination is the root itself.</summary>
    private string DestPrefix() =>
        ServerDestinationRelativePath is { Length: > 0 } p ? PathRules.NormalizeRelative(p).TrimEnd('\\') : "";

    /// <summary>Maps a local-relative path to the root-relative server path it targets inside the
    /// chosen destination (e.g. local "Price.xlsx" + dest "Work" => "Work\Price.xlsx").</summary>
    public string Scope(string localRel)
    {
        var prefix = DestPrefix();
        var rel = PathRules.NormalizeRelative(localRel).TrimEnd('\\');
        return prefix.Length == 0 ? rel : prefix + "\\" + rel;
    }

    /// <summary>True if a root-relative server path lives inside this destination subtree.</summary>
    public bool InDestination(string serverRel)
    {
        var prefix = DestPrefix();
        var sr = PathRules.NormalizeRelative(serverRel).TrimStart('\\').TrimEnd('\\');
        return prefix.Length == 0
            || sr.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || sr.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strips the destination prefix from a root-relative server path, returning the local-relative
    /// path, or null when the path is exactly the destination boundary itself (not a local item).</summary>
    public string? ToLocalRel(string serverRel)
    {
        var prefix = DestPrefix();
        var sr = PathRules.NormalizeRelative(serverRel).TrimStart('\\').TrimEnd('\\');
        if (prefix.Length == 0) return sr;
        if (sr.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return sr.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) ? sr.Substring(prefix.Length + 1) : null;
    }
}
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
    Task ReplaceItemsAsync(Guid id, IReadOnlyList<MappingLocalItem> items, CancellationToken ct = default);
    Task<IReadOnlyList<MappingLocalItem>> GetItemsAsync(Guid mappingId, CancellationToken ct = default);
    Task UpsertItemAsync(MappingLocalItem item, CancellationToken ct = default);
    Task RemoveItemAsync(Guid mappingId, string relativePath, CancellationToken ct = default);
    Task IndexBatchAsync(IReadOnlyList<MappingLocalItem> items, IReadOnlyList<MappingPendingOperation> operations, CancellationToken ct = default);
    Task EnqueueAsync(MappingPendingOperation operation, CancellationToken ct = default);
    Task<IReadOnlyList<MappingPendingOperation>> GetPendingAsync(Guid mappingId, DateTimeOffset now, CancellationToken ct = default);
    Task UpdateOperationAsync(MappingPendingOperation operation, CancellationToken ct = default);
    Task ReplacePendingAsync(Guid mappingId, IReadOnlyList<MappingPendingOperation> operations, CancellationToken ct = default);
    Task<int> PendingCountAsync(Guid? mappingId = null, CancellationToken ct = default);
    Task<SyncDashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SyncMappingSummary>> GetMappingSummariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SyncFileError>> GetErrorsAsync(int limit = 50, CancellationToken ct = default);
    Task RecordActivityAsync(SyncActivityEvent activity, CancellationToken ct = default);
    Task<IReadOnlyList<SyncActivityEvent>> GetRecentActivityAsync(int limit = 20, CancellationToken ct = default);
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
CREATE TABLE IF NOT EXISTS sync_mappings(mapping_id TEXT PRIMARY KEY,local_path TEXT NOT NULL,canonical_local_path TEXT NOT NULL UNIQUE,server_root_id TEXT,server_root_name TEXT,enabled INTEGER NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,last_server_revision INTEGER,inventory_state TEXT NOT NULL,last_scan_at TEXT,last_error TEXT,status TEXT NOT NULL,server_destination_item_id TEXT,server_destination_relative_path TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mapping_server_root ON sync_mappings(server_root_id) WHERE server_root_id IS NOT NULL;
CREATE TABLE IF NOT EXISTS mapping_local_items(mapping_id TEXT NOT NULL,relative_path TEXT NOT NULL,item_type TEXT NOT NULL,size INTEGER NOT NULL,mtime TEXT NOT NULL,sync_state TEXT NOT NULL,PRIMARY KEY(mapping_id,relative_path));
CREATE TABLE IF NOT EXISTS mapping_pending_operations(operation_id TEXT PRIMARY KEY,mapping_id TEXT NOT NULL,operation_type TEXT NOT NULL,relative_path TEXT NOT NULL,secondary_path TEXT,created_at TEXT NOT NULL,attempt_count INTEGER NOT NULL,next_attempt_at TEXT NOT NULL,state TEXT NOT NULL,last_error TEXT);
CREATE INDEX IF NOT EXISTS ix_mapping_pending_ready ON mapping_pending_operations(mapping_id,state,next_attempt_at);
CREATE TABLE IF NOT EXISTS sync_activity(event_id TEXT PRIMARY KEY,mapping_id TEXT,relative_path TEXT,action TEXT NOT NULL,status TEXT NOT NULL,timestamp TEXT NOT NULL,error_code TEXT,error_message TEXT);
CREATE INDEX IF NOT EXISTS ix_sync_activity_time ON sync_activity(timestamp DESC);
""";
        await cmd.ExecuteNonQueryAsync(ct);
        await MigrateLegacyAsync(c, ct);
        await EnsureDestinationColumnsAsync(c, ct);
    }

    private async Task EnsureDestinationColumnsAsync(SqliteConnection c, CancellationToken ct)
    {
        foreach (var col in new[] { "server_destination_item_id", "server_destination_relative_path" })
        {
            await using var chk = c.CreateCommand("SELECT COUNT(*) FROM pragma_table_info('sync_mappings') WHERE name=$c");
            chk.Parameters.AddWithValue("$c", col);
            if (Convert.ToInt32(await chk.ExecuteScalarAsync(ct)) == 0)
            {
                await using var alt = c.CreateCommand($"ALTER TABLE sync_mappings ADD COLUMN {col} TEXT");
                await alt.ExecuteNonQueryAsync(ct);
            }
        }
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
        await using var insert = c.CreateCommand("INSERT INTO sync_mappings(mapping_id,local_path,canonical_local_path,server_root_id,server_root_name,enabled,created_at,updated_at,last_server_revision,inventory_state,last_scan_at,last_error,status,server_destination_item_id,server_destination_relative_path) VALUES($id,$path,$canonical,$root,NULL,1,$created,$updated,NULL,'Migrated',NULL,NULL,'Offline',NULL,NULL)");
        Add(insert.Parameters,"$id",id.ToString()); Add(insert.Parameters,"$path",root); Add(insert.Parameters,"$canonical",SyncMappingRules.CanonicalLocalPath(root)); Add(insert.Parameters,"$root",null); Add(insert.Parameters,"$created",now.ToString("O")); Add(insert.Parameters,"$updated",now.ToString("O")); await insert.ExecuteNonQueryAsync(ct);
        await using var copyItems = c.CreateCommand("INSERT OR IGNORE INTO mapping_local_items SELECT $id,relative_path,item_type,size,mtime,sync_state FROM local_items"); Add(copyItems.Parameters,"$id",id.ToString()); await copyItems.ExecuteNonQueryAsync(ct);
        await using var copyOps = c.CreateCommand("INSERT OR IGNORE INTO mapping_pending_operations SELECT operation_id,$id,operation_type,relative_path,secondary_path,created_at,attempt_count,next_attempt_at,state,last_error FROM pending_operations"); Add(copyOps.Parameters,"$id",id.ToString()); await copyOps.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SyncMapping>> GetMappingsAsync(CancellationToken ct = default) { await using var c=Open(); await using var cmd=c.CreateCommand("SELECT mapping_id,local_path,server_root_id,server_root_name,enabled,created_at,updated_at,last_server_revision,inventory_state,last_scan_at,last_error,status,server_destination_item_id,server_destination_relative_path FROM sync_mappings ORDER BY created_at"); await using var r=await cmd.ExecuteReaderAsync(ct); var a=new List<SyncMapping>(); while(await r.ReadAsync(ct)) a.Add(ReadMapping(r)); return a; }
    public async Task<SyncMapping?> GetMappingAsync(Guid id,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT mapping_id,local_path,server_root_id,server_root_name,enabled,created_at,updated_at,last_server_revision,inventory_state,last_scan_at,last_error,status,server_destination_item_id,server_destination_relative_path FROM sync_mappings WHERE mapping_id=$id");Add(cmd.Parameters,"$id",id.ToString());await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadMapping(r):null;}
    private static SyncMapping ReadMapping(SqliteDataReader r)=>new(Guid.Parse(r.GetString(0)),r.GetString(1),r.IsDBNull(2)?null:Guid.Parse(r.GetString(2)),r.IsDBNull(3)?null:r.GetString(3),r.GetBoolean(4),DateTimeOffset.Parse(r.GetString(5)),DateTimeOffset.Parse(r.GetString(6)),r.IsDBNull(7)?null:r.GetInt64(7),r.GetString(8),r.IsDBNull(9)?null:DateTimeOffset.Parse(r.GetString(9)),r.IsDBNull(10)?null:r.GetString(10),Enum.Parse<MappingStatus>(r.GetString(11)),r.IsDBNull(12)?null:Guid.Parse(r.GetString(12)),r.IsDBNull(13)?null:r.GetString(13));
    public async Task AddMappingAsync(SyncMapping m,CancellationToken ct=default){try{await WriteMappingAsync(m,"INSERT INTO sync_mappings VALUES($id,$path,$canonical,$root,$name,$enabled,$created,$updated,$rev,$inventory,$scan,$error,$status,$destItem,$destRel)",ct);}catch(SqliteException e) when(e.SqliteErrorCode==19&&e.Message.Contains("server_root_id",StringComparison.OrdinalIgnoreCase)){throw new MappingAlreadyLinkedException(m.ServerRootId??Guid.Empty);}}
    public async Task UpdateMappingAsync(SyncMapping m,CancellationToken ct=default)=>await WriteMappingAsync(m,"UPDATE sync_mappings SET local_path=$path,canonical_local_path=$canonical,server_root_id=$root,server_root_name=$name,enabled=$enabled,updated_at=$updated,last_server_revision=$rev,inventory_state=$inventory,last_scan_at=$scan,last_error=$error,status=$status,server_destination_item_id=$destItem,server_destination_relative_path=$destRel WHERE mapping_id=$id",ct);
    private async Task WriteMappingAsync(SyncMapping m,string sql,CancellationToken ct){await using var c=Open();await using var cmd=c.CreateCommand(sql);Add(cmd.Parameters,"$id",m.MappingId.ToString());Add(cmd.Parameters,"$path",m.LocalPath);Add(cmd.Parameters,"$canonical",SyncMappingRules.CanonicalLocalPath(m.LocalPath));Add(cmd.Parameters,"$root",m.ServerRootId?.ToString());Add(cmd.Parameters,"$name",m.ServerRootName);Add(cmd.Parameters,"$enabled",m.Enabled);Add(cmd.Parameters,"$created",m.CreatedAt.ToString("O"));Add(cmd.Parameters,"$updated",m.UpdatedAt.ToString("O"));Add(cmd.Parameters,"$rev",m.LastServerRevision);Add(cmd.Parameters,"$inventory",m.InventoryState);Add(cmd.Parameters,"$scan",m.LastScanAt?.ToString("O"));Add(cmd.Parameters,"$error",m.LastError);Add(cmd.Parameters,"$status",m.Status.ToString());Add(cmd.Parameters,"$destItem",m.ServerDestinationItemId?.ToString());Add(cmd.Parameters,"$destRel",m.ServerDestinationRelativePath);await cmd.ExecuteNonQueryAsync(ct);}
    public async Task RemoveMappingAsync(Guid id,CancellationToken ct=default){await using var c=Open();await using var tx=c.BeginTransaction();foreach(var sql in new[]{"DELETE FROM mapping_local_items WHERE mapping_id=$id","DELETE FROM mapping_pending_operations WHERE mapping_id=$id","DELETE FROM sync_mappings WHERE mapping_id=$id"}){await using var cmd=c.CreateCommand(sql);cmd.Transaction=tx;Add(cmd.Parameters,"$id",id.ToString());await cmd.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
    public async Task ReplaceItemsAsync(Guid id,IReadOnlyList<MappingLocalItem> items,CancellationToken ct=default){await using var c=Open();await using var tx=c.BeginTransaction();await using(var del=c.CreateCommand("DELETE FROM mapping_local_items WHERE mapping_id=$id")){del.Transaction=tx;Add(del.Parameters,"$id",id.ToString());await del.ExecuteNonQueryAsync(ct);}foreach(var i in items){await using var cmd=c.CreateCommand("INSERT INTO mapping_local_items VALUES($id,$p,$t,$s,$m,$state)");cmd.Transaction=tx;Add(cmd.Parameters,"$id",id.ToString());Add(cmd.Parameters,"$p",PathRules.NormalizeRelative(i.RelativePath));Add(cmd.Parameters,"$t",i.ItemType.ToString());Add(cmd.Parameters,"$s",i.Size);Add(cmd.Parameters,"$m",i.Mtime.ToString("O"));Add(cmd.Parameters,"$state",i.SyncState.ToString());await cmd.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
    public async Task<IReadOnlyList<MappingLocalItem>> GetItemsAsync(Guid id,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT mapping_id,relative_path,item_type,size,mtime,sync_state FROM mapping_local_items WHERE mapping_id=$id");Add(cmd.Parameters,"$id",id.ToString());await using var r=await cmd.ExecuteReaderAsync(ct);var a=new List<MappingLocalItem>();while(await r.ReadAsync(ct))a.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),Enum.Parse<ItemType>(r.GetString(2)),r.GetInt64(3),DateTimeOffset.Parse(r.GetString(4)),Enum.Parse<SyncItemState>(r.GetString(5))));return a;}
    public async Task UpsertItemAsync(MappingLocalItem i,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("INSERT INTO mapping_local_items VALUES($id,$p,$t,$s,$m,$state) ON CONFLICT(mapping_id,relative_path) DO UPDATE SET item_type=$t,size=$s,mtime=$m,sync_state=$state");Add(cmd.Parameters,"$id",i.MappingId.ToString());Add(cmd.Parameters,"$p",i.RelativePath);Add(cmd.Parameters,"$t",i.ItemType.ToString());Add(cmd.Parameters,"$s",i.Size);Add(cmd.Parameters,"$m",i.Mtime.ToString("O"));Add(cmd.Parameters,"$state",i.SyncState.ToString());await cmd.ExecuteNonQueryAsync(ct);}
    public async Task RemoveItemAsync(Guid id,string path,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("DELETE FROM mapping_local_items WHERE mapping_id=$id AND relative_path=$p");Add(cmd.Parameters,"$id",id.ToString());Add(cmd.Parameters,"$p",PathRules.NormalizeRelative(path));await cmd.ExecuteNonQueryAsync(ct);}
    public async Task IndexBatchAsync(IReadOnlyList<MappingLocalItem> items,IReadOnlyList<MappingPendingOperation> operations,CancellationToken ct=default){if(items.Count==0&&operations.Count==0)return;await using var c=Open();await using var tx=c.BeginTransaction();foreach(var i in items){await using var cmd=c.CreateCommand("INSERT INTO mapping_local_items VALUES($id,$p,$t,$s,$m,$state) ON CONFLICT(mapping_id,relative_path) DO UPDATE SET item_type=$t,size=$s,mtime=$m,sync_state=$state");cmd.Transaction=tx;Add(cmd.Parameters,"$id",i.MappingId.ToString());Add(cmd.Parameters,"$p",i.RelativePath);Add(cmd.Parameters,"$t",i.ItemType.ToString());Add(cmd.Parameters,"$s",i.Size);Add(cmd.Parameters,"$m",i.Mtime.ToString("O"));Add(cmd.Parameters,"$state",i.SyncState.ToString());await cmd.ExecuteNonQueryAsync(ct);}foreach(var o in operations){await using var cmd=c.CreateCommand("INSERT INTO mapping_pending_operations VALUES($id,$mapping,$type,$path,$secondary,$created,$attempt,$next,$state,$error) ON CONFLICT(operation_id) DO UPDATE SET state=$state,last_error=$error,next_attempt_at=$next,attempt_count=$attempt");cmd.Transaction=tx;Add(cmd.Parameters,"$id",o.OperationId.ToString());Add(cmd.Parameters,"$mapping",o.MappingId.ToString());Add(cmd.Parameters,"$type",o.Type.ToString());Add(cmd.Parameters,"$path",o.RelativePath);Add(cmd.Parameters,"$secondary",o.SecondaryPath);Add(cmd.Parameters,"$created",o.CreatedAt.ToString("O"));Add(cmd.Parameters,"$attempt",o.AttemptCount);Add(cmd.Parameters,"$next",o.NextAttemptAt.ToString("O"));Add(cmd.Parameters,"$state",o.State.ToString());Add(cmd.Parameters,"$error",o.LastError);await cmd.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
    public async Task EnqueueAsync(MappingPendingOperation o,CancellationToken ct=default){
        // Canonical path form everywhere so `a\b` and `a/b` never diverge (fixes
        // repeated CreateFile/Delete for the same file — the infinite .txt loop).
        o = o with { RelativePath = PathRules.NormalizeRelative(o.RelativePath), SecondaryPath = o.SecondaryPath is null ? null : PathRules.NormalizeRelative(o.SecondaryPath) };
        await using var c=Open();
        // Deduplicate by (mapping, relative_path): a live Pending op for the same
        // path is merged instead of stacking unique-operation_id duplicates.
        await using var find=c.CreateCommand("SELECT operation_id,state FROM mapping_pending_operations WHERE mapping_id=$mapping AND relative_path=$p AND state IN ('Pending','Failed') ORDER BY created_at DESC LIMIT 1");
        Add(find.Parameters,"$mapping",o.MappingId.ToString()); Add(find.Parameters,"$p",o.RelativePath);
        await using var rd=await find.ExecuteReaderAsync(ct);
        if(await rd.ReadAsync(ct))
        {
            var existingId=rd.GetString(0); var existingState=rd.GetString(1);
            // Reuse the live Pending op (merge type/dest), don't stack a duplicate.
            if(existingState=="Pending")
            {
                await using var u=c.CreateCommand("UPDATE mapping_pending_operations SET operation_type=$type,secondary_path=$secondary,next_attempt_at=$next WHERE operation_id=$id");
                Add(u.Parameters,"$id",existingId); Add(u.Parameters,"$type",o.Type.ToString()); Add(u.Parameters,"$secondary",o.SecondaryPath); Add(u.Parameters,"$next",DateTimeOffset.UtcNow.ToString("O"));
                await u.ExecuteNonQueryAsync(ct);
                return;
            }
            // A permanent semantic failure (NextAttemptAt==MaxValue) must NOT be
            // resurrected by a watcher echo — only a real reconcile clears it.
            if(existingState=="Failed")
            {
                await using var f=c.CreateCommand("SELECT next_attempt_at FROM mapping_pending_operations WHERE operation_id=$id");
                Add(f.Parameters,"$id",existingId);
                var existingNext=await f.ExecuteScalarAsync(ct) as string;
                if(existingNext is not null && DateTimeOffset.TryParse(existingNext,out var nx) && nx>=DateTimeOffset.MaxValue.AddDays(-1))
                {
                    return; // permanent semantic fail: keep it, do not re-enqueue
                }
            }
        }
        rd.Close();
        await using var cmd=c.CreateCommand("INSERT INTO mapping_pending_operations VALUES($id,$mapping,$type,$path,$secondary,$created,$attempt,$next,$state,$error)");
        Add(cmd.Parameters,"$id",o.OperationId.ToString());Add(cmd.Parameters,"$mapping",o.MappingId.ToString());Add(cmd.Parameters,"$type",o.Type.ToString());Add(cmd.Parameters,"$path",o.RelativePath);Add(cmd.Parameters,"$secondary",o.SecondaryPath);Add(cmd.Parameters,"$created",o.CreatedAt.ToString("O"));Add(cmd.Parameters,"$attempt",o.AttemptCount);Add(cmd.Parameters,"$next",o.NextAttemptAt.ToString("O"));Add(cmd.Parameters,"$state",o.State.ToString());Add(cmd.Parameters,"$error",o.LastError);await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<MappingPendingOperation>> GetPendingAsync(Guid id,DateTimeOffset now,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT operation_id,mapping_id,operation_type,relative_path,secondary_path,created_at,attempt_count,next_attempt_at,state,last_error FROM mapping_pending_operations WHERE mapping_id=$id AND state IN ('Pending','Failed') AND next_attempt_at <= $now ORDER BY created_at");Add(cmd.Parameters,"$id",id.ToString());Add(cmd.Parameters,"$now",now.ToString("O"));await using var r=await cmd.ExecuteReaderAsync(ct);var a=new List<MappingPendingOperation>();while(await r.ReadAsync(ct))a.Add(new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Enum.Parse<OperationType>(r.GetString(2)),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),DateTimeOffset.Parse(r.GetString(5)),r.GetInt32(6),DateTimeOffset.Parse(r.GetString(7)),Enum.Parse<OperationState>(r.GetString(8)),r.IsDBNull(9)?null:r.GetString(9)));return a;}
    public async Task UpdateOperationAsync(MappingPendingOperation o,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("UPDATE mapping_pending_operations SET attempt_count=$attempt,next_attempt_at=$next,state=$state,last_error=$error WHERE operation_id=$id");Add(cmd.Parameters,"$id",o.OperationId.ToString());Add(cmd.Parameters,"$attempt",o.AttemptCount);Add(cmd.Parameters,"$next",o.NextAttemptAt.ToString("O"));Add(cmd.Parameters,"$state",o.State.ToString());Add(cmd.Parameters,"$error",o.LastError);await cmd.ExecuteNonQueryAsync(ct);}
    public async Task ReplacePendingAsync(Guid mappingId,IReadOnlyList<MappingPendingOperation> operations,CancellationToken ct=default){await using var c=Open();await using var tx=c.BeginTransaction();await using(var delete=c.CreateCommand("DELETE FROM mapping_pending_operations WHERE mapping_id=$mapping")){delete.Transaction=tx;Add(delete.Parameters,"$mapping",mappingId.ToString());await delete.ExecuteNonQueryAsync(ct);}foreach(var o in operations){await using var insert=c.CreateCommand("INSERT INTO mapping_pending_operations VALUES($id,$mapping,$type,$path,$secondary,$created,$attempt,$next,$state,$error)");insert.Transaction=tx;Add(insert.Parameters,"$id",o.OperationId.ToString());Add(insert.Parameters,"$mapping",mappingId.ToString());Add(insert.Parameters,"$type",o.Type.ToString());Add(insert.Parameters,"$path",o.RelativePath);Add(insert.Parameters,"$secondary",o.SecondaryPath);Add(insert.Parameters,"$created",o.CreatedAt.ToString("O"));Add(insert.Parameters,"$attempt",o.AttemptCount);Add(insert.Parameters,"$next",o.NextAttemptAt.ToString("O"));Add(insert.Parameters,"$state",o.State.ToString());Add(insert.Parameters,"$error",o.LastError);await insert.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);}
    public async Task<int> PendingCountAsync(Guid? id=null,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand(id is null?"SELECT COUNT(*) FROM mapping_pending_operations WHERE state <> 'Completed'":"SELECT COUNT(*) FROM mapping_pending_operations WHERE mapping_id=$id AND state <> 'Completed'");if(id is not null)Add(cmd.Parameters,"$id",id.Value.ToString());return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));}
    public async Task<SyncDashboardSummary> GetDashboardSummaryAsync(CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT (SELECT COUNT(*) FROM mapping_local_items WHERE item_type='File'),(SELECT COUNT(*) FROM mapping_pending_operations WHERE state <> 'Completed' AND state <> 'Failed'),(SELECT COUNT(*) FROM mapping_pending_operations WHERE state='Failed'),(SELECT COUNT(*) FROM sync_mappings)");await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);return new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),null);}
    public async Task<IReadOnlyList<SyncMappingSummary>> GetMappingSummariesAsync(CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT m.mapping_id,(SELECT COUNT(*) FROM mapping_local_items i WHERE i.mapping_id=m.mapping_id AND i.item_type='File'),(SELECT COUNT(*) FROM mapping_pending_operations p WHERE p.mapping_id=m.mapping_id AND p.state <> 'Completed' AND p.state <> 'Failed'),(SELECT COUNT(*) FROM mapping_pending_operations p WHERE p.mapping_id=m.mapping_id AND p.state='Failed') FROM sync_mappings m ORDER BY m.created_at");await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<SyncMappingSummary>();while(await r.ReadAsync(ct))result.Add(new(Guid.Parse(r.GetString(0)),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),null));return result;}
    public async Task<IReadOnlyList<SyncFileError>> GetErrorsAsync(int limit=50,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT operation_id,mapping_id,relative_path,operation_type,last_error,attempt_count,created_at FROM mapping_pending_operations WHERE state='Failed' ORDER BY created_at DESC LIMIT $limit");Add(cmd.Parameters,"$limit",Math.Clamp(limit,1,500));await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<SyncFileError>();while(await r.ReadAsync(ct)){var raw=r.IsDBNull(4)?null:r.GetString(4);var mapped=UserFacingErrorMapper.Map(raw);result.Add(new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),mapped.Code,mapped.Message,raw,r.GetInt32(5),DateTimeOffset.Parse(r.GetString(6))));}return result;}
    public async Task RecordActivityAsync(SyncActivityEvent a,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("INSERT OR REPLACE INTO sync_activity VALUES($id,$mapping,$path,$action,$status,$timestamp,$code,$message); DELETE FROM sync_activity WHERE event_id NOT IN (SELECT event_id FROM sync_activity ORDER BY timestamp DESC LIMIT 500)");Add(cmd.Parameters,"$id",a.EventId.ToString());Add(cmd.Parameters,"$mapping",a.MappingId?.ToString());Add(cmd.Parameters,"$path",a.RelativePath);Add(cmd.Parameters,"$action",a.Action);Add(cmd.Parameters,"$status",a.Status);Add(cmd.Parameters,"$timestamp",a.Timestamp.ToString("O"));Add(cmd.Parameters,"$code",a.ErrorCode);Add(cmd.Parameters,"$message",a.ErrorMessage);await cmd.ExecuteNonQueryAsync(ct);}
    public async Task<IReadOnlyList<SyncActivityEvent>> GetRecentActivityAsync(int limit=20,CancellationToken ct=default){await using var c=Open();await using var cmd=c.CreateCommand("SELECT event_id,mapping_id,relative_path,action,status,timestamp,error_code,error_message FROM sync_activity ORDER BY timestamp DESC LIMIT $limit");Add(cmd.Parameters,"$limit",Math.Clamp(limit,1,100));await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<SyncActivityEvent>();while(await r.ReadAsync(ct))result.Add(new(Guid.Parse(r.GetString(0)),r.IsDBNull(1)?null:Guid.Parse(r.GetString(1)),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),DateTimeOffset.Parse(r.GetString(5)),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7)));return result;}
}
