using System.Runtime.Serialization;
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// An all-or-nothing batch refused before its transaction is sent leaves the caller's entities as they
/// were (id, stamps, ETag), whatever refused it: the batch rules, the serializer's size limits, and a
/// refusal of the serializer that has nothing to do with size. The Azure provider and the row-store
/// engine keep the same rule: anything thrown before the rows are sent restores the entities.
/// </summary>
public class AtomicBatchRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Stamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A plain type whose nested object can hold a cycle, which the serializer refuses.</summary>
    public sealed class LoopEntity : Entity
    {
        public string Name { get; set; } = "";
        public LoopNode? Node { get; set; }
    }

    public sealed class LoopNode
    {
        public string Label { get; set; } = "";
        public LoopNode? Next { get; set; }
    }

    /// <summary>A plain type with a property named with the path delimiter, which the serializer refuses.</summary>
    public sealed class DelimitedEntity : Entity
    {
        public string Name { get; set; } = "";
        public string? Foo_Bar { get; set; }
    }

    /// <summary>A type with a split value and a property named with the path delimiter.</summary>
    public sealed class SplitDelimitedEntity : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public string? Foo_Bar { get; set; }
    }

    private const string Cycle = "a cycle in a nested object";
    private const string Delimiter = "a property named with the path delimiter";
    private const string OversizedCell = "a cell above 64 KiB after compression";
    private const string SplitDelimiter = "a split-value type with a property named with the path delimiter";
    private const string PastContinuationRows = "a split value past its continuation rows, with no blob offload";
    private const string NotOneTransaction = "a batch that is not one transaction";

    public static TheoryData<string, string> Refusals
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var why in new[] { Cycle, Delimiter, OversizedCell, SplitDelimiter, PastContinuationRows, NotOneTransaction })
            {
                data.Add(why, "Azure");
                data.Add(why, "InMemory");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AnAtomicBatchRefusedBeforeItIsSent_LeavesTheCallersEntitiesAsTheyWere(string why, string provider)
    {
        switch (why)
        {
            case Cycle:
            {
                var node = new LoopNode { Label = "a" };
                node.Next = node;
                await AssertRefusedAndUntouchedAsync<LoopEntity, SerializationException>(provider,
                    [Fresh<LoopEntity>("P|Good"), Fresh<LoopEntity>("P|Loop", e => e.Node = node)]);
                break;
            }
            case Delimiter:
                await AssertRefusedAndUntouchedAsync<DelimitedEntity, SerializationException>(provider,
                    [Fresh<DelimitedEntity>("P|Good"), Fresh<DelimitedEntity>("P|Bad", e => e.Foo_Bar = "x")]);
                break;
            case OversizedCell:
                await AssertRefusedAndUntouchedAsync<TestEntity, OversizedCellException>(provider,
                    [Fresh<TestEntity>("P|Good"), Fresh<TestEntity>("P|Big", e => e.Name = Incompressible(200_000))]);
                break;
            case SplitDelimiter:
                await AssertRefusedAndUntouchedAsync<SplitDelimitedEntity, SerializationException>(provider,
                    [Fresh<SplitDelimitedEntity>("P|Good", e => e.Body = "x"),
                     Fresh<SplitDelimitedEntity>("P|Bad", e => (e.Body, e.Foo_Bar) = (Incompressible(100_000), "x"))]);
                break;
            case PastContinuationRows:
                await AssertRefusedAndUntouchedAsync<SplitTextEntity, OversizedValueException>(provider,
                    [Fresh<SplitTextEntity>("P|Good", e => e.Body = "x"), Fresh<SplitTextEntity>("P|Huge", e => e.Body = Incompressible(2_400_000))]);
                break;
            default:
                await AssertRefusedAndUntouchedAsync<TestEntity, BatchNotAtomicException>(provider,
                    [Fresh<TestEntity>("P|Good"), Fresh<TestEntity>("Q|Other")]);
                break;
        }
    }

    private static string Incompressible(int length)
    {
        var random = new Random(7);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(length, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[r.Next(alphabet.Length)];
        });
    }

    private static T Fresh<T>(string id, Action<T>? set = null) where T : Entity, new()
    {
        var entity = new T { Id = id, CreatedAt = Stamp, UpdatedAt = Stamp, ETag = "kept " + id };
        set?.Invoke(entity);
        return entity;
    }

    private static async Task AssertRefusedAndUntouchedAsync<T, TRefusal>(string provider, List<T> batch)
        where T : Entity, new()
        where TRefusal : Exception
    {
        var before = batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)).ToList();
        if (provider == "Azure")
        {
            using var h = new StorageHarness<T>();
            var sent = 0;
            h.Table.SubmitTransactionAsync(Arg.Do<IEnumerable<TableTransactionAction>>(_ => sent++), Arg.Any<CancellationToken>())
                .Returns(Substitute.For<Response<IReadOnlyList<Response>>>());
            h.SetupQueryAll();
            IStorage<T> store = h.Store;

            await Assert.ThrowsAnyAsync<TRefusal>(() => store.CreateAtomicAsync(batch, Ct));
            Assert.Equal(before, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)));
            await Assert.ThrowsAnyAsync<TRefusal>(() => store.UpsertAtomicAsync(batch, Ct));
            Assert.Equal(before, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)));
            Assert.Equal(0, sent);
        }
        else
        {
            IStorage<T> store = new InMemoryStorage<T>();

            await Assert.ThrowsAnyAsync<TRefusal>(() => store.CreateAtomicAsync(batch, Ct));
            Assert.Equal(before, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)));
            await Assert.ThrowsAnyAsync<TRefusal>(() => store.UpsertAtomicAsync(batch, Ct));
            Assert.Equal(before, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)));
            Assert.Equal(0, await store.CountAsync(ct: Ct));
        }
    }
}
