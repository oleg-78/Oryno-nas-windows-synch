using System;
using System.Linq;
using System.Threading.Tasks;
using OrynoSync.Core;
using Xunit;

namespace OrynoSync.Tests;

public class DestinationScopeTests
{
    private static SyncMapping Map(string? dest)
        => new(Guid.NewGuid(), @"C:\Work", Guid.NewGuid(), "Main", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "NotStarted", null, null, MappingStatus.Scanning,
            Guid.NewGuid(), dest);

    [Fact]
    public void LocalCreateUsesSelectedDestinationParent()
    {
        // A local root-level file Price.xlsx under destination Work must resolve to Work\Price.xlsx
        // so FindParent returns the destination folder (Work), not the null root.
        var m = Map("Work");
        Assert.Equal(@"Work\Price.xlsx", m.Scope("Price.xlsx"));
        Assert.Equal(@"Work\Documents\a.txt", m.Scope(@"Documents\a.txt"));
        Assert.Equal("Price.xlsx", System.IO.Path.GetFileName(m.Scope("Price.xlsx")));
    }

    [Fact]
    public void ScopeIsRootRelativeWhenNoDestination()
    {
        var m = Map(null);
        Assert.Equal(@"Documents\a.txt", m.Scope(@"Documents\a.txt"));
        Assert.Equal(@"a.txt", m.Scope("a.txt"));
    }

    [Fact]
    public void ReconciliationScopedToDestinationSubtree()
    {
        var m = Map(@"Work\Contracts");
        Assert.True(m.InDestination(@"Work\Contracts\x.txt"));
        Assert.True(m.InDestination(@"Work\Contracts"));
        Assert.False(m.InDestination(@"Work\Price.xlsx"));
        Assert.False(m.InDestination(@"Photos\1.jpg"));
        Assert.Equal(@"x.txt", m.ToLocalRel(@"Work\Contracts\x.txt"));
        Assert.Null(m.ToLocalRel(@"Work\Contracts")); // destination boundary itself is not a local item
        Assert.Null(m.ToLocalRel(@"Photos\1.jpg"));   // outside subtree
    }

    [Fact]
    public async Task NestedDestinationPersistsAcrossReloads()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dests-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var m = Map(@"Work\Contracts");
            var store = new SqliteSyncMappingStore(db);
            await store.InitializeAsync();
            await store.AddMappingAsync(m);
            var reloaded = await store.GetMappingAsync(m.MappingId);
            Assert.NotNull(reloaded);
            Assert.Equal(m.ServerDestinationItemId, reloaded.ServerDestinationItemId);
            Assert.Equal(@"Work\Contracts", reloaded.ServerDestinationRelativePath);
            Assert.True(reloaded.InDestination(@"Work\Contracts\a.txt"));
        }
        finally { try { if (System.IO.File.Exists(db)) System.IO.File.Delete(db); } catch (System.IO.IOException) { /* pooled connection still holds the temp db */ } }
    }
}