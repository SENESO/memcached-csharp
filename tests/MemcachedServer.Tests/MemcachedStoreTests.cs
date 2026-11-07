using System.Text;
using MemcachedServer.Core;

namespace MemcachedServer.Tests;

public class MemcachedStoreTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly CacheStats _stats = new();

    private MemcachedStore NewStore(long maxMemoryBytes = 64 * 1024 * 1024, int maxValueBytes = 1024 * 1024) =>
        new(_clock, _stats, maxMemoryBytes, maxValueBytes);

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Set_ThenGet_ReturnsValueAndFlags()
    {
        var store = NewStore();
        Assert.Equal(StorageOutcome.Stored, store.Store(StorageKind.Set, "key", 7, 0, Bytes("hello")));

        var outcome = store.Get("key");
        Assert.True(outcome.Found);
        Assert.Equal("hello", Encoding.ASCII.GetString(outcome.Value));
        Assert.Equal(7u, outcome.Flags);
    }

    [Fact]
    public void Get_MissingKey_IsMiss()
    {
        var store = NewStore();
        Assert.False(store.Get("nope").Found);
    }

    [Fact]
    public void Add_OnExistingKey_IsNotStored_AndKeepsOriginal()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 0, Bytes("first"));
        Assert.Equal(StorageOutcome.NotStored, store.Store(StorageKind.Add, "k", 0, 0, Bytes("second")));
        Assert.Equal("first", Encoding.ASCII.GetString(store.Get("k").Value));
    }

    [Fact]
    public void Add_OnMissingKey_Stores()
    {
        var store = NewStore();
        Assert.Equal(StorageOutcome.Stored, store.Store(StorageKind.Add, "k", 0, 0, Bytes("v")));
        Assert.True(store.Get("k").Found);
    }

    [Fact]
    public void Replace_OnMissingKey_IsNotStored()
    {
        var store = NewStore();
        Assert.Equal(StorageOutcome.NotStored, store.Store(StorageKind.Replace, "k", 0, 0, Bytes("v")));
    }

    [Fact]
    public void Replace_OnExistingKey_Stores()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 0, Bytes("old"));
        Assert.Equal(StorageOutcome.Stored, store.Store(StorageKind.Replace, "k", 0, 0, Bytes("new")));
        Assert.Equal("new", Encoding.ASCII.GetString(store.Get("k").Value));
    }

    [Fact]
    public void Append_AndPrepend_CombineAndPreserveFlags()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 42, 0, Bytes("middle"));
        Assert.Equal(StorageOutcome.Stored, store.Store(StorageKind.Append, "k", 0, 0, Bytes("-end")));
        Assert.Equal(StorageOutcome.Stored, store.Store(StorageKind.Prepend, "k", 0, 0, Bytes("start-")));

        var outcome = store.Get("k");
        Assert.Equal("start-middle-end", Encoding.ASCII.GetString(outcome.Value));
        Assert.Equal(42u, outcome.Flags); // append/prepend keep the original flags
    }

    [Fact]
    public void Append_OnMissingKey_IsNotStored()
    {
        var store = NewStore();
        Assert.Equal(StorageOutcome.NotStored, store.Store(StorageKind.Append, "k", 0, 0, Bytes("x")));
    }

    [Fact]
    public void Delete_ExistingThenMissing()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 0, Bytes("v"));
        Assert.True(store.Delete("k"));
        Assert.False(store.Get("k").Found);
        Assert.False(store.Delete("k"));
    }

    [Fact]
    public void Cas_WithWrongId_IsExists_WithRightId_Stores()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 0, Bytes("v1"));
        var casId = store.Get("k").CasId;

        Assert.Equal(StorageOutcome.Exists, store.Store(StorageKind.Cas, "k", 0, 0, Bytes("v2"), casId + 999));
        Assert.Equal(StorageOutcome.Stored, store.Store(StorageKind.Cas, "k", 0, 0, Bytes("v2"), casId));
        Assert.Equal("v2", Encoding.ASCII.GetString(store.Get("k").Value));
        // A successful store assigns a fresh cas id.
        Assert.NotEqual(casId, store.Get("k").CasId);
    }

    [Fact]
    public void Cas_OnMissingKey_IsNotFound()
    {
        var store = NewStore();
        Assert.Equal(StorageOutcome.NotFound, store.Store(StorageKind.Cas, "k", 0, 0, Bytes("v"), 123));
    }

    [Fact]
    public void Incr_AndDecr_FollowMemcachedSemantics()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "n", 0, 0, Bytes("10"));

        Assert.Equal(DeltaOutcome.Ok, store.ApplyDelta("n", 5, increment: true, out var afterIncr));
        Assert.Equal(15ul, afterIncr);

        // decr never goes below zero.
        Assert.Equal(DeltaOutcome.Ok, store.ApplyDelta("n", 100, increment: false, out var afterDecr));
        Assert.Equal(0ul, afterDecr);
        Assert.Equal("0", Encoding.ASCII.GetString(store.Get("n").Value));
    }

    [Fact]
    public void Incr_WrapsAtUInt64Max()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "n", 0, 0, Bytes(ulong.MaxValue.ToString()));
        Assert.Equal(DeltaOutcome.Ok, store.ApplyDelta("n", 1, increment: true, out var wrapped));
        Assert.Equal(0ul, wrapped);
    }

    [Fact]
    public void Incr_OnMissingKey_IsNotFound_OnNonNumeric_IsNonNumeric()
    {
        var store = NewStore();
        Assert.Equal(DeltaOutcome.NotFound, store.ApplyDelta("missing", 1, true, out _));
        store.Store(StorageKind.Set, "s", 0, 0, Bytes("abc"));
        Assert.Equal(DeltaOutcome.NonNumeric, store.ApplyDelta("s", 1, true, out _));
    }

    [Fact]
    public void Expiry_RelativeSeconds_ItemDisappearsAfterDeadline()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 10, Bytes("v")); // 10 seconds from the fake now
        Assert.True(store.Get("k").Found);

        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(store.Get("k").Found);
        Assert.Equal(0, store.Count); // lazily purged by the failed get
    }

    [Fact]
    public void Expiry_Zero_NeverExpires()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 0, Bytes("v"));
        _clock.Advance(TimeSpan.FromDays(3650));
        Assert.True(store.Get("k").Found);
    }

    [Fact]
    public void Expiry_AbsoluteUnixTime()
    {
        var store = NewStore();
        var deadline = _clock.GetUtcNow().AddSeconds(120);
        store.Store(StorageKind.Set, "k", 0, deadline.ToUnixTimeSeconds(), Bytes("v"));

        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(store.Get("k").Found);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(store.Get("k").Found);
    }

    [Fact]
    public void Touch_ExtendsExpiry_AndReportsMissing()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 10, Bytes("v"));

        Assert.True(store.Touch("k", 1000));
        _clock.Advance(TimeSpan.FromSeconds(500));
        Assert.True(store.Get("k").Found);

        Assert.False(store.Touch("missing", 1000));
    }

    [Fact]
    public void Maintenance_PurgesExpiredEntries_WithoutAccess()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k", 0, 5, Bytes("v"));
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, store.Count); // still stored until something looks

        store.RunMaintenance();
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.CurrentBytes);
    }

    [Fact]
    public void FlushAll_Immediate_ClearsEverything()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "a", 0, 0, Bytes("1"));
        store.Store(StorageKind.Set, "b", 0, 0, Bytes("2"));
        store.FlushAll(0);
        Assert.Equal(0, store.Count);
        Assert.False(store.Get("a").Found);
    }

    [Fact]
    public void FlushAll_Delayed_ClearsOnlyWhenDue()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "a", 0, 0, Bytes("1"));
        store.FlushAll(60);
        Assert.True(store.Get("a").Found);

        _clock.Advance(TimeSpan.FromSeconds(61));
        // Any access runs the due flush first.
        Assert.False(store.Get("a").Found);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Store_ValueLargerThanItemLimit_IsTooLarge()
    {
        var store = NewStore(maxValueBytes: 4);
        Assert.Equal(StorageOutcome.TooLarge, store.Store(StorageKind.Set, "k", 0, 0, Bytes("12345")));
        Assert.False(store.Get("k").Found);
    }

    [Fact]
    public void Append_ResultLargerThanItemLimit_IsTooLarge_AndKeepsOriginal()
    {
        var store = NewStore(maxValueBytes: 8);
        store.Store(StorageKind.Set, "k", 0, 0, Bytes("123456"));
        Assert.Equal(StorageOutcome.TooLarge, store.Store(StorageKind.Append, "k", 0, 0, Bytes("789")));
        Assert.Equal("123456", Encoding.ASCII.GetString(store.Get("k").Value));
    }

    [Fact]
    public void Eviction_LeastRecentlyUsedGoesFirst_JustStoredStays()
    {
        // Entries cost key(1) + value(9) = 10 bytes; the cache holds exactly 3.
        var store = NewStore(maxMemoryBytes: 30);
        store.Store(StorageKind.Set, "a", 0, 0, Bytes("123456789"));
        store.Store(StorageKind.Set, "b", 0, 0, Bytes("123456789"));
        store.Store(StorageKind.Set, "c", 0, 0, Bytes("123456789"));

        Assert.True(store.Get("a").Found); // 'a' is now most-recently-used; 'b' is the LRU entry

        store.Store(StorageKind.Set, "d", 0, 0, Bytes("123456789"));

        Assert.False(store.Get("b").Found);
        Assert.True(store.Get("a").Found);
        Assert.True(store.Get("c").Found);
        Assert.True(store.Get("d").Found);
        Assert.Equal(1, _stats.Evictions);
        Assert.Equal(30, store.CurrentBytes);
    }

    [Fact]
    public void CurrentBytes_TracksStoresAndDeletes()
    {
        var store = NewStore();
        store.Store(StorageKind.Set, "k1", 0, 0, Bytes("12345")); // 2 + 5
        store.Store(StorageKind.Set, "k2", 0, 0, Bytes("12"));    // 2 + 2
        Assert.Equal(11, store.CurrentBytes);

        store.Store(StorageKind.Set, "k1", 0, 0, Bytes("1"));     // shrink to 2 + 1
        Assert.Equal(7, store.CurrentBytes);

        store.Delete("k2");
        Assert.Equal(3, store.CurrentBytes);
    }

    [Fact]
    public async Task ConcurrentIncrements_AreAtomic()
    {
        var store = new MemcachedStore(TimeProvider.System, new CacheStats(), 64 * 1024 * 1024, 1024 * 1024);
        store.Store(StorageKind.Set, "counter", 0, 0, Bytes("0"));

        const int workers = 8;
        const int incrementsPerWorker = 250;
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < incrementsPerWorker; i++)
                store.ApplyDelta("counter", 1, increment: true, out var ignored);
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(
            (workers * incrementsPerWorker).ToString(),
            Encoding.ASCII.GetString(store.Get("counter").Value));
    }

    [Fact]
    public async Task ConcurrentMixedLoad_DoesNotCorruptTheStore()
    {
        var store = new MemcachedStore(TimeProvider.System, new CacheStats(), 64 * 1024 * 1024, 1024 * 1024);
        var tasks = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var key = $"key-{worker}-{i % 25}";
                store.Store(StorageKind.Set, key, 0, 0, Bytes("value"));
                store.Get(key);
                if (i % 7 == 0)
                    store.Delete(key);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        // Every key the last write left standing must be internally consistent.
        Assert.True(store.CurrentBytes >= 0);
        Assert.True(store.Count > 0);
    }
}
