using Microsoft.Data.Sqlite;

namespace OrynoSync.Core;

public enum RemotePlanningState { MetadataOnly, NeedsDownload, PotentialConflict, Deleted, RemotePathIncompatible, LocalPathCollision }
public enum ConflictType { LocalModifiedRemoteModified, LocalModifiedRemoteDeleted, LocalPathCollision, RemotePathIncompatible }
public sealed record RemoteItemState(Guid ItemId, Guid RootId, Guid? ParentItemId, string Name, string RelativePath, string ItemType, long? SizeBytes, DateTimeOffset? MtimeUtc, string? ContentHash, long Version, long LastRevision, RemotePlanningState PlanningState, bool IsDeleted = false);
public sealed record RemoteRootState(Guid RootId, long LastRevision, bool InventoryComplete, bool InventoryInProgress, long? InventoryAnchor, string? InventoryCursor, long Generation, DateTimeOffset? InventoryStartedAt);

public interface IRemoteStateStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<RemoteRootState> GetRootStateAsync(Guid rootId, CancellationToken ct = default);
    Task BeginInventoryAsync(Guid rootId, long anchor, CancellationToken ct = default);
    Task SaveInventoryPageAsync(Guid rootId, long generation, IReadOnlyList<RemoteItemDto> items, string? nextCursor, CancellationToken ct = default);
    Task CompleteInventoryAsync(Guid rootId, long anchor, CancellationToken ct = default);
    Task RecordSuccessfulFileSyncAsync(Guid rootId, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLastSuccessfulFileSyncAsync(Guid rootId, CancellationToken ct = default);
    Task ApplyChangesPageAsync(Guid rootId, IReadOnlyList<RemoteChangeDto> changes, long nextRevision, CancellationToken ct = default);
    Task ResetInventoryAsync(Guid rootId, CancellationToken ct = default);
    Task RemoveRootStateAsync(Guid rootId, CancellationToken ct = default);
    Task<int> CountRemoteItemsAsync(Guid rootId, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteItemState>> GetRemoteItemsAsync(Guid rootId, CancellationToken ct = default);
    Task<int> CountPlanningAsync(Guid rootId, RemotePlanningState state, CancellationToken ct = default);
    Task MarkItemMissingAsync(Guid rootId, string serverItemId, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLastSuccessfulFileSyncForMappingAsync(Guid mappingId, CancellationToken ct = default);
    Task RecordSuccessfulFileSyncForMappingAsync(Guid mappingId, CancellationToken ct = default);
    Task RefreshFromServerAsync(Guid rootId, Func<Guid, CancellationToken, Task<IReadOnlyList<RemoteItemDto>>> fetcher, CancellationToken ct = default);
}

public sealed class RemoteStateStore(string databasePath) : IRemoteStateStore
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
        CREATE TABLE IF NOT EXISTS remote_items(server_item_id TEXT PRIMARY KEY,root_id TEXT NOT NULL,parent_item_id TEXT,name TEXT NOT NULL,relative_path TEXT NOT NULL,item_type TEXT NOT NULL,size_bytes INTEGER,mtime_utc TEXT,content_hash TEXT,server_version INTEGER NOT NULL,last_revision INTEGER NOT NULL,planning_state TEXT NOT NULL,is_deleted INTEGER NOT NULL DEFAULT 0,inventory_generation INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_remote_root_path ON remote_items(root_id,relative_path COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS remote_sync_state(root_id TEXT PRIMARY KEY,last_revision INTEGER NOT NULL DEFAULT 0,inventory_complete INTEGER NOT NULL DEFAULT 0,inventory_in_progress INTEGER NOT NULL DEFAULT 0,inventory_anchor INTEGER,inventory_cursor TEXT,inventory_generation INTEGER NOT NULL DEFAULT 0,inventory_started_at TEXT,last_successful_file_sync TEXT);
        CREATE TABLE IF NOT EXISTS sync_conflicts(conflict_id TEXT PRIMARY KEY,root_id TEXT NOT NULL,server_item_id TEXT,relative_path TEXT NOT NULL,conflict_type TEXT NOT NULL,created_at TEXT NOT NULL,resolved_at TEXT);
        """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RemoteRootState> GetRootStateAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("SELECT last_revision,inventory_complete,inventory_in_progress,inventory_anchor,inventory_cursor,inventory_generation,inventory_started_at FROM remote_sync_state WHERE root_id=$r");
        Add(cmd.Parameters, "$r", rootId.ToString());
        await using var x = await cmd.ExecuteReaderAsync(ct);
        if (await x.ReadAsync(ct)) return new(rootId, x.GetInt64(0), x.GetBoolean(1), x.GetBoolean(2), x.IsDBNull(3) ? null : x.GetInt64(3), x.IsDBNull(4) ? null : x.GetString(4), x.GetInt64(5), x.IsDBNull(6) ? null : DateTimeOffset.Parse(x.GetString(6)));
        return new(rootId, 0, false, false, null, null, 0, null);
    }

    public async Task BeginInventoryAsync(Guid rootId, long anchor, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("INSERT INTO remote_sync_state(root_id,inventory_in_progress,inventory_complete,inventory_anchor,inventory_generation,inventory_started_at) VALUES($r,1,0,$a,1,$t) ON CONFLICT(root_id) DO UPDATE SET inventory_in_progress=1,inventory_complete=0,inventory_anchor=$a,inventory_cursor=NULL,inventory_generation=inventory_generation+1,inventory_started_at=$t");
        Add(cmd.Parameters, "$r", rootId.ToString());
        Add(cmd.Parameters, "$a", anchor);
        Add(cmd.Parameters, "$t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveInventoryPageAsync(Guid rootId, long generation, IReadOnlyList<RemoteItemDto> items, string? nextCursor, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = c.BeginTransaction();
        foreach (var i in items) await UpsertAsync(c, tx, rootId, i, 0, generation, ct);
        await using var cmd = c.CreateCommand("UPDATE remote_sync_state SET inventory_cursor=$c WHERE root_id=$r AND inventory_generation=$g");
        cmd.Transaction = tx;
        Add(cmd.Parameters, "$c", nextCursor);
        Add(cmd.Parameters, "$r", rootId.ToString());
        Add(cmd.Parameters, "$g", generation);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task CompleteInventoryAsync(Guid rootId, long anchor, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = c.BeginTransaction();
        var state = await StateAsync(c, tx, rootId, ct);
        await using (var del = c.CreateCommand("DELETE FROM remote_items WHERE root_id=$r AND inventory_generation<>$g")) { del.Transaction = tx; Add(del.Parameters, "$r", rootId.ToString()); Add(del.Parameters, "$g", state.Generation); await del.ExecuteNonQueryAsync(ct); }
        await using (var cmd = c.CreateCommand("UPDATE remote_sync_state SET inventory_complete=1,inventory_in_progress=0,inventory_cursor=NULL,last_revision=$a WHERE root_id=$r")) { cmd.Transaction = tx; Add(cmd.Parameters, "$a", anchor); Add(cmd.Parameters, "$r", rootId.ToString()); await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    public async Task ApplyChangesPageAsync(Guid rootId, IReadOnlyList<RemoteChangeDto> changes, long nextRevision, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = c.BeginTransaction();
        var state = await StateAsync(c, tx, rootId, ct);
        foreach (var change in changes.Where(x => x.Revision > state.LastRevision).OrderBy(x => x.Revision))
        {
            if (change.Change == "DELETE")
            {
                var path = await ExistingPathAsync(c, tx, change.ItemId, ct) ?? change.RelativePath ?? change.Name ?? change.ItemId.ToString();
                var dirty = await HasPendingAsync(c, tx, path, rootId, ct);
                await using var cmd = c.CreateCommand("UPDATE remote_items SET is_deleted=1,last_revision=$v,server_version=$sv,planning_state=$p WHERE server_item_id=$id");
                cmd.Transaction = tx;
                Add(cmd.Parameters, "$v", change.Revision);
                Add(cmd.Parameters, "$sv", change.Version);
                Add(cmd.Parameters, "$p", dirty ? RemotePlanningState.PotentialConflict.ToString() : RemotePlanningState.Deleted.ToString());
                Add(cmd.Parameters, "$id", change.ItemId.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
                if (dirty) await ConflictAsync(c, tx, rootId, change.ItemId, path, ConflictType.LocalModifiedRemoteDeleted, ct);
            }
            else
            {
                var dto = new RemoteItemDto(change.ItemId, change.ParentItemId, change.Name ?? Path.GetFileName(change.RelativePath ?? ""), change.RelativePath ?? "", change.ItemType ?? "file", change.SizeBytes, change.MtimeUtc, change.ContentHash, change.Version);
                var dirty = await HasPendingAsync(c, tx, dto.RelativePath, rootId, ct);
                await UpsertAsync(c, tx, rootId, dto, change.Revision, state.Generation, ct, dirty);
                if (dirty && change.Change == "UPDATE") await ConflictAsync(c, tx, rootId, change.ItemId, dto.RelativePath, ConflictType.LocalModifiedRemoteModified, ct);
            }
        }
        await using (var cursor = c.CreateCommand("UPDATE remote_sync_state SET last_revision=$n WHERE root_id=$r")) { cursor.Transaction = tx; Add(cursor.Parameters, "$n", Math.Max(state.LastRevision, nextRevision)); Add(cursor.Parameters, "$r", rootId.ToString()); await cursor.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    private async Task UpsertAsync(SqliteConnection c, SqliteTransaction tx, Guid root, RemoteItemDto i, long revision, long generation, CancellationToken ct, bool dirty = false)
    {
        var planning = dirty ? RemotePlanningState.PotentialConflict : i.ItemType == "directory" ? RemotePlanningState.MetadataOnly : RemotePlanningState.NeedsDownload;
        if (!PathRules.IsWindowsCompatible(i.RelativePath)) planning = RemotePlanningState.RemotePathIncompatible;
        await using var cmd = c.CreateCommand("INSERT INTO remote_items VALUES($id,$r,$parent,$name,$path,$type,$size,$mtime,$hash,$ver,$rev,$plan,0,$gen) ON CONFLICT(server_item_id) DO UPDATE SET parent_item_id=$parent,name=$name,relative_path=$path,item_type=$type,size_bytes=$size,mtime_utc=$mtime,content_hash=$hash,server_version=$ver,last_revision=MAX(last_revision,$rev),planning_state=$plan,is_deleted=0,inventory_generation=$gen");
        cmd.Transaction = tx;
        Add(cmd.Parameters, "$id", i.ItemId.ToString());
        Add(cmd.Parameters, "$r", root.ToString());
        Add(cmd.Parameters, "$parent", i.ParentItemId?.ToString());
        Add(cmd.Parameters, "$name", i.Name);
        Add(cmd.Parameters, "$path", i.RelativePath);
        Add(cmd.Parameters, "$type", i.ItemType);
        Add(cmd.Parameters, "$size", i.SizeBytes);
        Add(cmd.Parameters, "$mtime", i.MtimeUtc?.ToString("O"));
        Add(cmd.Parameters, "$hash", i.ContentHash);
        Add(cmd.Parameters, "$ver", i.Version);
        Add(cmd.Parameters, "$rev", revision);
        Add(cmd.Parameters, "$plan", planning.ToString());
        Add(cmd.Parameters, "$gen", generation);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var collision = c.CreateCommand("SELECT COUNT(*) FROM remote_items WHERE root_id=$r AND relative_path=$p COLLATE NOCASE AND server_item_id<>$id AND is_deleted=0");
        collision.Transaction = tx;
        Add(collision.Parameters, "$r", root.ToString());
        Add(collision.Parameters, "$p", i.RelativePath);
        Add(collision.Parameters, "$id", i.ItemId.ToString());
        if (Convert.ToInt32(await collision.ExecuteScalarAsync(ct)) > 0)
        {
            await using var mark = c.CreateCommand("UPDATE remote_items SET planning_state='LocalPathCollision' WHERE root_id=$r AND relative_path=$p COLLATE NOCASE AND is_deleted=0");
            mark.Transaction = tx;
            Add(mark.Parameters, "$r", root.ToString());
            Add(mark.Parameters, "$p", i.RelativePath);
            await mark.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<RemoteRootState> StateAsync(SqliteConnection c, SqliteTransaction tx, Guid root, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand("SELECT last_revision,inventory_complete,inventory_in_progress,inventory_anchor,inventory_cursor,inventory_generation,inventory_started_at FROM remote_sync_state WHERE root_id=$r");
        cmd.Transaction = tx;
        Add(cmd.Parameters, "$r", root.ToString());
        await using var x = await cmd.ExecuteReaderAsync(ct);
        if (!await x.ReadAsync(ct)) throw new InvalidOperationException("Remote root state is not initialized.");
        return new(root, x.GetInt64(0), x.GetBoolean(1), x.GetBoolean(2), x.IsDBNull(3) ? null : x.GetInt64(3), x.IsDBNull(4) ? null : x.GetString(4), x.GetInt64(5), x.IsDBNull(6) ? null : DateTimeOffset.Parse(x.GetString(6)));
    }

    private static async Task<string?> ExistingPathAsync(SqliteConnection c, SqliteTransaction tx, Guid item, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand("SELECT relative_path FROM remote_items WHERE server_item_id=$id");
        cmd.Transaction = tx;
        Add(cmd.Parameters, "$id", item.ToString());
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Section J FIX: HasPendingAsync now scoped by mapping_id to prevent cross-mapping false positives.
    /// Only checks mapping_pending_operations for the specific mapping.
    /// </summary>
    private static async Task<bool> HasPendingAsync(SqliteConnection c, SqliteTransaction tx, string path, Guid rootId, CancellationToken ct)
    {
        var normalized = PathRules.NormalizeRelative(path);
        // Check if sync_mappings table exists (defensive: in isolated test DBs it may not)
        await using var chkTable = c.CreateCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sync_mappings'");
        chkTable.Transaction = tx;
        if (Convert.ToInt32(await chkTable.ExecuteScalarAsync(ct)) == 0)
            return false;
        await using var current = c.CreateCommand($"SELECT EXISTS(SELECT 1 FROM mapping_pending_operations mp JOIN sync_mappings m ON mp.mapping_id=m.mapping_id WHERE m.server_root_id=$root AND mp.relative_path=$p AND mp.state IN ({SqliteSyncMappingStore.LiveStatesSql},{SqliteSyncMappingStore.ActiveErrorStatesSql}))");
        current.Transaction = tx;
        Add(current.Parameters, "$root", rootId.ToString());
        Add(current.Parameters, "$p", normalized);
        return Convert.ToInt32(await current.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task ConflictAsync(SqliteConnection c, SqliteTransaction tx, Guid root, Guid item, string path, ConflictType type, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand("INSERT INTO sync_conflicts VALUES($id,$r,$item,$p,$t,$at,NULL)");
        cmd.Transaction = tx;
        Add(cmd.Parameters, "$id", Guid.NewGuid().ToString());
        Add(cmd.Parameters, "$r", root.ToString());
        Add(cmd.Parameters, "$item", item.ToString());
        Add(cmd.Parameters, "$p", path);
        Add(cmd.Parameters, "$t", type.ToString());
        Add(cmd.Parameters, "$at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetInventoryAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("INSERT INTO remote_sync_state(root_id) VALUES($r) ON CONFLICT(root_id) DO UPDATE SET inventory_complete=0,inventory_in_progress=0,inventory_anchor=NULL,inventory_cursor=NULL");
        Add(cmd.Parameters, "$r", rootId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkItemMissingAsync(Guid rootId, string serverItemId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("UPDATE remote_items SET planning_state='RemotePathIncompatible',is_deleted=1 WHERE root_id=$r AND server_item_id=$id");
        Add(cmd.Parameters, "$r", rootId.ToString());
        Add(cmd.Parameters, "$id", serverItemId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveRootStateAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = c.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM remote_items WHERE root_id=$r", "DELETE FROM remote_sync_state WHERE root_id=$r", "DELETE FROM sync_conflicts WHERE root_id=$r" })
        {
            await using var cmd = c.CreateCommand(sql);
            cmd.Transaction = tx;
            Add(cmd.Parameters, "$r", rootId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<int> CountRemoteItemsAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("SELECT COUNT(*) FROM remote_items WHERE root_id=$r AND is_deleted=0");
        Add(cmd.Parameters, "$r", rootId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> CountPlanningAsync(Guid rootId, RemotePlanningState state, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("SELECT COUNT(*) FROM remote_items WHERE root_id=$r AND planning_state=$s AND is_deleted=0");
        Add(cmd.Parameters, "$r", rootId.ToString());
        Add(cmd.Parameters, "$s", state.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<RemoteItemState>> GetRemoteItemsAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("SELECT server_item_id,parent_item_id,name,relative_path,item_type,size_bytes,mtime_utc,content_hash,server_version,last_revision,planning_state,is_deleted FROM remote_items WHERE root_id=$r ORDER BY relative_path");
        Add(cmd.Parameters, "$r", rootId.ToString());
        await using var x = await cmd.ExecuteReaderAsync(ct);
        var result = new List<RemoteItemState>();
        while (await x.ReadAsync(ct)) result.Add(new(Guid.Parse(x.GetString(0)), rootId, x.IsDBNull(1) ? null : Guid.Parse(x.GetString(1)), x.GetString(2), x.GetString(3), x.GetString(4), x.IsDBNull(5) ? null : x.GetInt64(5), x.IsDBNull(6) ? null : DateTimeOffset.Parse(x.GetString(6)), x.IsDBNull(7) ? null : x.GetString(7), x.GetInt64(8), x.GetInt64(9), Enum.Parse<RemotePlanningState>(x.GetString(10)), x.GetBoolean(11)));
        return result;
    }

    /// <summary>
    /// Section H: Record successful file sync timestamp.
    /// </summary>
    public async Task RecordSuccessfulFileSyncAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("UPDATE remote_sync_state SET last_successful_file_sync=$t WHERE root_id=$r");
        Add(cmd.Parameters, "$t", DateTimeOffset.UtcNow.ToString("O"));
        Add(cmd.Parameters, "$r", rootId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Section H: Retrieve last successful file sync timestamp.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastSuccessfulFileSyncAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("SELECT last_successful_file_sync FROM remote_sync_state WHERE root_id=$r");
        Add(cmd.Parameters, "$r", rootId.ToString());
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is string s && DateTimeOffset.TryParse(s, out var dt)) return dt;
        return null;
    }

    /// <summary>
    /// Section 9 fix: Retrieve last successful file sync timestamp for a specific mapping.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastSuccessfulFileSyncForMappingAsync(Guid mappingId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("SELECT last_successful_file_sync FROM sync_mappings WHERE mapping_id=$m");
        Add(cmd.Parameters, "$m", mappingId.ToString());
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is string s && DateTimeOffset.TryParse(s, out var dt)) return dt;
        return null;
    }

    /// <summary>
    /// Section 9 fix: Record successful file sync timestamp for a specific mapping.
    /// </summary>
    public async Task RecordSuccessfulFileSyncForMappingAsync(Guid mappingId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand("UPDATE sync_mappings SET last_successful_file_sync=$t WHERE mapping_id=$m");
        Add(cmd.Parameters, "$t", DateTimeOffset.UtcNow.ToString("O"));
        Add(cmd.Parameters, "$m", mappingId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Section 2 fix: Real server refresh — fetch authoritative items from server API
    /// and persist into RemoteStateStore. This is NOT the same as GetRemoteItemsAsync
    /// which reads the local SQLite cache.
    /// </summary>
    public async Task RefreshFromServerAsync(Guid rootId, Func<Guid, CancellationToken, Task<IReadOnlyList<RemoteItemDto>>> fetcher, CancellationToken ct = default)
    {
        var freshItems = await fetcher(rootId, ct);

        await using var c = Open();
        await using var tx = c.BeginTransaction();
        foreach (var i in freshItems)
        {
            await UpsertAsync(c, tx, rootId, i, 0, 0, ct);
        }
        await tx.CommitAsync(ct);
    }
}
