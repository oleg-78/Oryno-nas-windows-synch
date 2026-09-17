namespace OrynoSync.Core;

public sealed record DesiredLocalItem(string RelativePath, ItemType ItemType, long Size, DateTimeOffset Mtime, string? ContentHash);
public sealed record NormalizedOperation(OperationType Type, string RelativePath);
public sealed record QueueNormalizationResult(IReadOnlyList<NormalizedOperation> Operations, IReadOnlyList<string> Conflicts, int OldPendingCount) { public int NormalizedPendingCount => Operations.Count; }
public static class QueueNormalizer
{
    public static QueueNormalizationResult Plan(IEnumerable<DesiredLocalItem> local, IEnumerable<RemoteItemState> server, IEnumerable<MappingPendingOperation> oldPending, IReadOnlyDictionary<string, MappingLocalItem>? tracked = null)
    {
        var ignore = new IgnoreRules();
        var l = local.Where(x => !ignore.IsIgnored(x.RelativePath)).GroupBy(x => PathRules.NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var s = server.Where(x => !x.IsDeleted && !ignore.IsIgnored(x.RelativePath)).GroupBy(x => PathRules.NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase); var ops = new List<NormalizedOperation>(); var conflicts = new List<string>();
        foreach (var item in l.Values.OrderBy(x => x.RelativePath.Count(c => c == '\\')))
        { if (!s.TryGetValue(item.RelativePath, out var remote)) { ops.Add(new(item.ItemType == ItemType.Directory ? OperationType.CreateDirectory : OperationType.CreateFile, item.RelativePath)); continue; } if (!string.Equals(item.ItemType == ItemType.Directory ? "directory" : "file", remote.ItemType, StringComparison.OrdinalIgnoreCase)) { conflicts.Add(item.RelativePath); continue; } if (item.ItemType == ItemType.File && (!string.Equals(item.ContentHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase) || item.Size != (remote.SizeBytes ?? -1))) conflicts.Add(item.RelativePath); }
        // §1: a path that was Synced and then deleted locally is a local DELETE intent, not a remote-only
        // item to download. Without this check the queue planner re-created the file (resurrection) and the
        // user's delete never reached the NAS.
        foreach (var item in s.Values.Where(x => !l.ContainsKey(x.RelativePath)).OrderBy(x => x.RelativePath.Count(c => c == '\\')))
        {
            if (tracked is not null && tracked.TryGetValue(item.RelativePath, out var t) && t.SyncState == SyncItemState.DeletedLocal)
            {
                ops.Add(new(OperationType.Delete, item.RelativePath));
                continue;
            }
            ops.Add(new(item.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase) ? OperationType.DownloadDirectory : OperationType.DownloadFile, item.RelativePath));
        }
        return new(ops, conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), oldPending.Count());
    }
}
