using System;
using System.Collections.Generic;
using System.Linq;

namespace OrynoSync.Core;

/// <summary>Pure, UI-free NAS destination tree. Never yields a Windows drive: nodes come only
/// from server roots/inventory. Root nodes (from GET /api/sync/roots) are the sync-boundary
/// destinations; inventory items are their descendants linked by ParentItemId.</summary>
public sealed record NasNode(Guid ItemId, string Name, Guid? ParentItemId, string RelativePath, bool IsFolder);

public sealed class NasDestinationTree
{
    private readonly Guid _rootScope;
    private readonly Dictionary<Guid, NasNode> _byId = new();
    private readonly Dictionary<Guid, List<NasNode>> _childrenOf = new();
    public IReadOnlyList<NasNode> Roots { get; }

    public NasDestinationTree(IReadOnlyList<SyncRootDto> roots, IReadOnlyList<RemoteItemDto> items, Guid rootScope)
    {
        _rootScope = rootScope;
        var rootsList = new List<NasNode>();
        foreach (var r in roots) { var rn = new NasNode(r.RootId, r.Name, null, r.RelativePath ?? "", true); rootsList.Add(rn); _byId[r.RootId] = rn; }
        Roots = rootsList;

        foreach (var it in items)
        {
            var isDir = it.ItemType != null && (it.ItemType.Equals("directory", StringComparison.OrdinalIgnoreCase)
                                                || it.ItemType.Equals("folder", StringComparison.OrdinalIgnoreCase));
            _byId[it.ItemId] = new NasNode(it.ItemId, it.Name, it.ParentItemId, it.RelativePath ?? "", isDir);
        }
        foreach (var it in items)
        {
            // items whose ParentItemId is null are top-of-root -> attach to the browsed root scope
            var parent = it.ParentItemId ?? (it.ItemId == rootScope ? Guid.Empty : rootScope);
            if (!_childrenOf.TryGetValue(parent, out var list)) { list = new List<NasNode>(); _childrenOf[parent] = list; }
            if (_byId.TryGetValue(it.ItemId, out var node)) list.Add(node);
        }
        foreach (var k in _childrenOf.Keys) _childrenOf[k] = _childrenOf[k].OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public NasNode? Find(Guid id) => _byId.TryGetValue(id, out var n) ? n : null;
    public IReadOnlyList<NasNode> ChildrenOf(Guid id) => _childrenOf.TryGetValue(id, out var l) ? l : Array.Empty<NasNode>();

    public IReadOnlyList<string> Breadcrumb(Guid id)
    {
        var segs = new List<string>();
        Guid cur = id;
        for (var guard = 0; cur != Guid.Empty && cur != _rootScope && guard < 256; guard++)
        {
            if (!_byId.TryGetValue(cur, out var n)) break;
            segs.Add(n.Name);
            cur = n.ParentItemId ?? _rootScope;
        }
        if (cur == _rootScope && _byId.TryGetValue(_rootScope, out var rootNode)) segs.Add(rootNode.Name);
        segs.Reverse();
        return segs;
    }
}