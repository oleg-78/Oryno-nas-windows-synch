using Blake3;

namespace OrynoSync.Core;

public sealed class Blake3ContentHasher : IContentHasher
{
    public async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeStreamAsync(input, ct);
    }

    public static async Task<string> ComputeStreamAsync(Stream input, CancellationToken ct = default)
    {
        using var hasher = Hasher.New();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0) hasher.Update(buffer.AsSpan(0, read));
        var result = new byte[Hash.Size];
        hasher.Finalize(result);
        return Convert.ToHexString(result).ToLowerInvariant();
    }
}

public sealed record UploadTarget(Guid RootId, Guid? ParentItemId, string Name, Guid? ItemId = null, long? BaseVersion = null, Guid? OperationId = null, Guid? MappingId = null);
public sealed record DownloadTarget(Guid ItemId, long Version, string RelativePath, long ExpectedSize, string ExpectedHash, DateTimeOffset? ExpectedMtime = null);
public sealed record TransferResult(Guid? ItemId, long? Version, long Bytes, string Hash, bool Resumed, bool Replayed = false);

public sealed class ResumableTransferClient(IContentTransferApi api, string tempDirectory, int maxActiveTransfers = 3, ITransferSessionStore? sessionStore = null)
{
    private readonly SemaphoreSlim _active = new(Math.Max(1, maxActiveTransfers));
    private readonly IContentHasher _hasher = new Blake3ContentHasher();
    private readonly ITransferSessionStore? _sessions = sessionStore;

    public async Task<TransferResult> UploadAsync(string path, UploadTarget target, CancellationToken ct = default)
    {
        await _active.WaitAsync(ct);
        try
        {
            var before = new FileInfo(path);
            var expectedHash = await _hasher.ComputeAsync(path, ct);
            before.Refresh();
            var check = new FileInfo(path);
            if (before.Length != check.Length || before.LastWriteTimeUtc != check.LastWriteTimeUtc) throw new FileChangedDuringTransferException(path);
            var key = $"{Path.GetFullPath(path).ToUpperInvariant()}|{target.RootId:D}|{target.ItemId?.ToString("D")}|{target.Name}|{before.Length}|{expectedHash}";
            UploadCreateResponse session;
            var saved = _sessions is null ? null : await _sessions.GetAsync(key, ct);
            if (saved is Guid savedId)
            {
                try
                {
                    var existing = await api.GetUploadStatusAsync(savedId, ct);
                    // §19 (recursive audit): a session the server already expired/aborted/failed can never be
                    // resumed — its staging data is gone, so the client sent no bytes and the commit answered
                    // 409/500 forever. Observed in production: 6 files kept retrying poisoned sessions created
                    // on 2026-09-07 (state=FAILED, staging already reaped). Start a fresh session instead.
                    if (existing.State is "EXPIRED" or "ABORTED" or "FAILED")
                    {
                        if (_sessions is not null) await _sessions.RemoveAsync(key, ct);
                        session = await CreateAsync();
                    }
                    else session = new(existing.UploadId, existing.State, 8, existing.ReceivedBytes, existing.ExpectedSize, existing.ExpiresAt);
                }
                catch (SyncApiException e) when (e.Code == "SYNC_UPLOAD_NOT_FOUND")
                {
                    if (_sessions is not null) await _sessions.RemoveAsync(key, ct);
                    session = await CreateAsync();
                }
            }
            else session = await CreateAsync();
            if (_sessions is not null) await _sessions.SaveAsync(key, session.UploadId, before.Length, expectedHash, target.MappingId, ct);
            var status = await api.GetUploadStatusAsync(session.UploadId, ct);
            var offset = Math.Clamp(status.ReceivedBytes, 0, before.Length);
            var resumed = offset > 0;
            var chunkSize = checked((long)Math.Max(1, session.ChunkSizeMb) * 1024 * 1024);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = offset;
            var bufferLength = chunkSize > int.MaxValue ? 8 * 1024 * 1024 : (int)chunkSize;
            var buffer = new byte[bufferLength];
            while (offset < before.Length)
            {
                var wanted = (int)Math.Min(buffer.Length, before.Length - offset);
                var read = await ReadExactlyAsync(input, buffer.AsMemory(0, wanted), ct);
                if (read == 0) break;
                try { await api.UploadChunkAsync(session.UploadId, offset, new MemoryStream(buffer, 0, read, false), ct); }
                catch (Exception e) when (!ct.IsCancellationRequested && (e is HttpRequestException || e is TaskCanceledException))
                {
                    status = await api.GetUploadStatusAsync(session.UploadId, ct);
                    offset = Math.Clamp(status.ReceivedBytes, 0, before.Length);
                    input.Position = offset;
                    continue;
                }
                offset += read;
            }
            var commit = await CommitWithSessionResetAsync(session.UploadId, target, expectedHash, key, ct);
            if (_sessions is not null) await _sessions.RemoveAsync(key, ct);
            var after = new FileInfo(path);
            if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) throw new FileChangedDuringTransferException(path);
            return new TransferResult(commit.ItemId, commit.Version, before.Length, expectedHash, resumed, commit.Replayed);

            Task<UploadCreateResponse> CreateAsync() => api.CreateUploadAsync(new UploadCreateRequest(target.RootId, target.ParentItemId, target.ItemId is null ? target.Name : null, target.ItemId, target.BaseVersion, before.Length, target.OperationId), ct);
        }
        finally { _active.Release(); }
    }

    /// <summary>
    /// §19 (recursive audit): a commit that fails because the server no longer has the session's staging data
    /// must also drop the saved session, otherwise every retry resumes the same dead upload id and the file
    /// can never reach the NAS (observed: 6 files looping forever with `upload EXPIRED` / 500).
    /// </summary>
    private async Task<TransferCommit> CommitWithSessionResetAsync(Guid uploadId, UploadTarget target, string expectedHash, string key, CancellationToken ct)
    {
        try { return await api.CommitUploadAsync(uploadId, target.OperationId, expectedHash, ct); }
        catch (SyncApiException e) when (e.Code is "SYNC_UPLOAD_STALE" or "SYNC_UPLOAD_STATE" or "SYNC_UPLOAD_INCOMPLETE")
        {
            if (_sessions is not null) await _sessions.RemoveAsync(key, ct);
            throw;
        }
    }

    public async Task<TransferResult> DownloadAsync(DownloadTarget target, string destinationPath, CancellationToken ct = default)
    {
        await _active.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var temp = Path.Combine(tempDirectory, $"{target.ItemId:N}-{target.Version}.part");
            var resumed = false;
            if (File.Exists(temp))
            {
                var existingLength = new FileInfo(temp).Length;
                if (existingLength == target.ExpectedSize && string.Equals(await HashFileAsync(temp, ct), target.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    await MaterializeAsync(temp, destinationPath, target);
                    return new(target.ItemId, target.Version, target.ExpectedSize, target.ExpectedHash, true);
                }
                resumed = existingLength > 0 && existingLength < target.ExpectedSize;
                if (!resumed) File.Delete(temp);
            }
            if (target.ExpectedSize == 0)
            {
                await File.WriteAllBytesAsync(temp, [], ct);
                await MaterializeAsync(temp, destinationPath, target);
                return new(target.ItemId, target.Version, 0, target.ExpectedHash, false);
            }
            try { await DownloadToTempAsync(temp, target, resumed, ct); }
            catch (SyncApiException e) when (resumed && e.Code == "SYNC_RANGE_INVALID")
            {
                File.Delete(temp); resumed = false; await DownloadToTempAsync(temp, target, false, ct);
            }
            var hash = await HashFileAsync(temp, ct);
            if (!string.Equals(hash, target.ExpectedHash, StringComparison.OrdinalIgnoreCase)) throw new SyncApiException(System.Net.HttpStatusCode.BadRequest, "SYNC_HASH_MISMATCH", "Downloaded content hash does not match server metadata.");
            if (new FileInfo(temp).Length != target.ExpectedSize) throw new SyncApiException(System.Net.HttpStatusCode.BadRequest, "SYNC_SIZE_MISMATCH", "Downloaded content size does not match server metadata.");
            await MaterializeAsync(temp, destinationPath, target);
            return new(target.ItemId, target.Version, target.ExpectedSize, hash, resumed);
        }
        finally { _active.Release(); }
    }

    private async Task DownloadToTempAsync(string temp, DownloadTarget target, bool resumed, CancellationToken ct)
    {
        await using var output = new FileStream(temp, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await api.DownloadRangeAsync(target.ItemId, target.Version, output.Length, output, ct);
    }

    private static async Task MaterializeAsync(string temp, string destination, DownloadTarget target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(temp, destination, true); if (target.ExpectedMtime is { } mtime) File.SetLastWriteTimeUtc(destination, mtime.UtcDateTime); await Task.CompletedTask;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); return await Blake3ContentHasher.ComputeStreamAsync(input, ct);
    }

    private static async Task<int> ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0; while (total < buffer.Length) { var n = await input.ReadAsync(buffer[total..], ct); if (n == 0) break; total += n; } return total;
    }
}

public sealed class FileChangedDuringTransferException(string path) : IOException($"File changed during transfer: {path}");
