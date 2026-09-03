using System;
using System.Collections.Generic;
using System.Linq;
using OrynoSync.Core;
using Xunit;

namespace OrynoSync.Tests;

public class NasDestinationTreeTests
{
    private static readonly Guid RootA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RootB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Folder1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Folder2 = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid FileX = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static IReadOnlyList<SyncRootDto> Roots() => new List<SyncRootDto>
    {
        new(RootA, "Work", true, "complete", 1) { RelativePath = "Work" },
        new(RootB, "Private", true, "complete", 1) { RelativePath = "Private" },
    };

    private static IReadOnlyList<RemoteItemDto> Items() => new List<RemoteItemDto>
    {
        new(Folder1, RootA, "Docs", "Work/Docs", "folder", null, null, null, 1),
        new(Folder2, Folder1, "2026", "Work/Docs/2026", "folder", null, null, null, 1),
        new(FileX, Folder2, "a.txt", "Work/Docs/2026/a.txt", "file", 5, null, null, 1),
    };

    private static NasDestinationTree Tree() => new(Roots(), Items(), RootA);

    [Fact] public void RootsLoadFromApi() =>
        Assert.Equal(new[] { "Work", "Private" }, Tree().Roots.Select(r => r.Name).ToArray());

    [Fact] public void DestinationNeverExposesWindowsDrives()
    {
        var t = Tree();
        var all = t.Roots.Concat(t.ChildrenOf(RootA)).Concat(t.ChildrenOf(Folder1)).Concat(t.ChildrenOf(Folder2));
        foreach (var n in all) Assert.False(IsDrivePath(n.RelativePath) || IsDrivePath(n.Name), $"exposed drive path: {n.RelativePath}");
        static bool IsDrivePath(string s) => (s.Length >= 2 && (s[0] is 'C' or 'D' or 'E' or 'c' or 'd' or 'e') && s[1] == ':');
    }

    [Fact] public void ChildrenLoadFromApi()
    {
        var t = Tree();
        Assert.Equal(new[] { "Docs" }, t.ChildrenOf(RootA).Select(n => n.Name).ToArray());
        Assert.Equal(new[] { "2026" }, t.ChildrenOf(Folder1).Select(n => n.Name).ToArray());
        Assert.Equal(new[] { "a.txt" }, t.ChildrenOf(Folder2).Select(n => n.Name).ToArray());
    }

    [Fact] public void BreadcrumbUpdatesAndIncludesRoot()
    {
        var t = Tree();
        Assert.Equal(new[] { "Work", "Docs", "2026" }, t.Breadcrumb(Folder2).ToArray());
    }

    [Fact] public void BackAndUpNavigateToParent()
    {
        var t = Tree();
        var parent = t.Find(Folder2)!.ParentItemId;
        Assert.Equal(Folder1, parent);          // "up" resolves to the parent folder id
        Assert.Equal(new[] { "Work", "Docs" }, t.Breadcrumb(Folder1).ToArray());
    }

    [Fact] public void RootIsSelectableDestination()
    {
        var t = Tree();
        Assert.NotNull(t.Find(RootA));          // root present and can be selected/bound
        Assert.Equal("Work", t.Find(RootA)!.Name);
    }
}