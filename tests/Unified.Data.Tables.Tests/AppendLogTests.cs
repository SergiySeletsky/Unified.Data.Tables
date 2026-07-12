using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Append-log helpers: <see cref="RowKeys.AppendKey"/> encoding (later events sort first, sub-stream
/// isolation) and the <see cref="AppendLogExtensions"/> append/read-recent pair over the fake.
/// </summary>
public class AppendLogTests
{
    public sealed class Evt : Entity
    {
        public string Payload { get; set; } = string.Empty;
    }

    // ── RowKeys.AppendKey / TryParseAppendKey ─────────────────────────────────

    [Fact]
    public void AppendKey_LaterEvents_SortLexicallyFirst()
    {
        var earlier = RowKeys.AppendKey(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = RowKeys.AppendKey(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(later, earlier) < 0);
    }

    [Fact]
    public void AppendKey_WithSubStream_IsPrefixedForRangeScan()
    {
        var key = RowKeys.AppendKey(DateTimeOffset.UtcNow, subStream: "session-7");
        Assert.StartsWith(RowKeys.SubStreamPrefix("session-7"), key);
    }

    [Fact]
    public void TryParseAppendKey_RoundTripsTimestampAndSubStream()
    {
        var when = new DateTimeOffset(2026, 7, 11, 8, 30, 0, TimeSpan.Zero);
        var key = RowKeys.AppendKey(when, subStream: "s1", uniquifier: "abcd1234");

        Assert.True(RowKeys.TryParseAppendKey(key, out var parsed, out var sub));
        Assert.Equal(when, parsed);
        Assert.Equal("s1", sub);
    }

    [Fact]
    public void TryParseAppendKey_NonAppendKey_ReturnsFalse()
    {
        Assert.False(RowKeys.TryParseAppendKey("not-an-append-key", out _, out _));
    }

    [Fact]
    public void TryParseAppendKey_SubStreamContainingTilde_RoundTrips()
    {
        // The sub-stream itself may contain '~'; parsing must split on the LAST '~', not the first.
        var when = new DateTimeOffset(2026, 7, 11, 8, 30, 0, TimeSpan.Zero);
        var key = RowKeys.AppendKey(when, subStream: "a~b", uniquifier: "abcd1234");

        Assert.True(RowKeys.TryParseAppendKey(key, out var parsed, out var sub));
        Assert.Equal(when, parsed);
        Assert.Equal("a~b", sub);
    }

    // ── AppendAsync / RecentAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RecentAsync_ReturnsNewestFirst()
    {
        var store = new InMemoryStorage<Evt>();
        var t0 = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
            await store.AppendAsync("stream", new Evt { Payload = $"e{i}" }, at: t0.AddMinutes(i));

        var recent = await store.RecentAsync("stream", 3);

        Assert.Equal(3, recent.Count);
        Assert.Equal(new[] { "e4", "e3", "e2" }, recent.Select(e => e.Payload)); // newest first
    }

    [Fact]
    public async Task RecentAsync_IsolatesBySubStream()
    {
        var store = new InMemoryStorage<Evt>();
        var t0 = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

        await store.AppendAsync("chat", new Evt { Payload = "a1" }, subStream: "sessionA", at: t0);
        await store.AppendAsync("chat", new Evt { Payload = "b1" }, subStream: "sessionB", at: t0.AddSeconds(1));
        await store.AppendAsync("chat", new Evt { Payload = "a2" }, subStream: "sessionA", at: t0.AddSeconds(2));

        var sessionA = await store.RecentAsync("chat", 10, subStream: "sessionA");

        Assert.Equal(2, sessionA.Count);
        Assert.All(sessionA, e => Assert.StartsWith("a", e.Payload));
        Assert.Equal(new[] { "a2", "a1" }, sessionA.Select(e => e.Payload)); // newest first within the sub-stream
    }

    [Fact]
    public async Task RecentAsync_BareRead_AcrossSubStreams_IsGroupedBySubStream_NotGloballyNewest()
    {
        // Documented behaviour: with multiple sub-streams, a bare read is grouped by the "{sub}~" band,
        // so Take stops inside the first band rather than returning globally-newest events.
        var store = new InMemoryStorage<Evt>();
        var t0 = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
            await store.AppendAsync("p", new Evt { Payload = $"a{i}" }, subStream: "aaa", at: t0.AddMinutes(i));
        // "bbb" events are strictly NEWER in time, but "aaa~..." sorts before "bbb~..." lexically.
        for (var i = 0; i < 3; i++)
            await store.AppendAsync("p", new Evt { Payload = $"b{i}" }, subStream: "bbb", at: t0.AddHours(1).AddMinutes(i));

        var recent = await store.RecentAsync("p", 3); // no subStream

        Assert.Equal(3, recent.Count);
        Assert.Equal(new[] { "a4", "a3", "a2" }, recent.Select(e => e.Payload)); // "aaa" band, newest-first, despite bbb being newer
        // The correct way to get one stream's newest N is to pass the sub-stream:
        var newestB = await store.RecentAsync("p", 3, subStream: "bbb");
        Assert.Equal(new[] { "b2", "b1", "b0" }, newestB.Select(e => e.Payload));
    }

    [Fact]
    public async Task AppendAsync_AssignsPartition_AndIsReadableById()
    {
        var store = new InMemoryStorage<Evt>();

        var appended = await store.AppendAsync("logs", new Evt { Payload = "hello" });

        Assert.StartsWith("logs|", appended.Id);
        var read = await store.OneAsync(appended.Id);
        Assert.NotNull(read);
        Assert.Equal("hello", read!.Payload);
    }

    [Fact]
    public async Task RecentAsync_MixedCasePartition_MatchesNormalizedStoredKeys()
    {
        var store = new InMemoryStorage<Evt>();
        await store.AppendAsync("MyStream", new Evt { Payload = "x" });

        // RecentAsync must normalize the partition the same way AppendAsync/CreateAsync did.
        var recent = await store.RecentAsync("MyStream", 10);

        Assert.Single(recent);
    }
}
