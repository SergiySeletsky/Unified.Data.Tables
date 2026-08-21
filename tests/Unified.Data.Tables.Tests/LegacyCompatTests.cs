using System.Collections;
using System.Collections.Immutable;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Regression tests for legacy-row read compatibility: shapes written by the historical
/// Newtonsoft-based serializers that System.Text.Json alone cannot reconstruct, and rows whose
/// <c>_TypeName</c> token names a type in a namespace that has since been renamed.
/// </summary>
public class LegacyCompatTests
{
    private const string PartitionKey = "p";
    private const string RowKey = "r";

    /// <summary>
    /// A single public parameterized constructor whose parameters do not exactly match the property
    /// TYPES (IEnumerable vs ImmutableList) — System.Text.Json's native binding rejects it with
    /// "Each parameter in the deserialization constructor ... must bind to an object property or
    /// field", while Newtonsoft bound by name and deserialized per parameter type. Real-world shape:
    /// IntelliGrowth.Feedbacks...PaceQuestionGroup, ~5k rows in production event stores.
    /// </summary>
    [Fact]
    public void ConstructorParameterTypesDifferingFromPropertyTypes_RoundTripThroughJson()
    {
        var source = new QuestionGroupHolder
        {
            Group = new QuestionGroup(
                "g1",
                "Group",
                4.0,
                [new Question("q1", "Q1")],
                [new AnswerOption("o1", 3.0)])
        };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        Assert.True(entity.ContainsKey("Group__Json"), "group should be a __Json cell");

        var restored = entity.FromTableEntity<QuestionGroupHolder>();

        Assert.Equal("g1", restored.Group.Id);
        Assert.Equal("q1", Assert.Single(restored.Group.Questions).Id);
        Assert.Equal("o1", Assert.Single(restored.Group.AnswerOptions).Id);
    }

    /// <summary>
    /// A type with a single public parameterized constructor whose parameters DO match: the
    /// converter takes the same path and the result is unchanged.
    /// </summary>
    [Fact]
    public void ConstructorParametersMatchingPropertyTypes_StillRoundTrip()
    {
        var source = new MatchingHolder { Value = new Matching("a", 1) };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<MatchingHolder>();

        Assert.Equal("a", restored.Value.Name);
        Assert.Equal(1, restored.Value.Count);
    }

    /// <summary>
    /// A token written before a namespace move resolves to the current type once the rename is
    /// registered — for both the discriminator and the untyped FromTableEntity read path.
    /// </summary>
    [Fact]
    public void LegacyNamespaceToken_ResolvesAfterRenameRegistration()
    {
        AssemblyQualifiedTypeDiscriminator.RegisterLegacyTypeNamespace(
            "Unified.Data.Tables.Tests.LegacyNs.",
            "Unified.Data.Tables.Tests.CurrentNs.");

        var legacyToken = typeof(CurrentNs.Moved).AssemblyQualifiedName!
            .Replace("Unified.Data.Tables.Tests.CurrentNs.", "Unified.Data.Tables.Tests.LegacyNs.", StringComparison.Ordinal);

        var resolved = AssemblyQualifiedTypeDiscriminator.Instance.Resolve(legacyToken, typeof(object));
        Assert.Equal(typeof(CurrentNs.Moved), resolved);

        // The row's _TypeName carries the LEGACY token; the untyped read path must apply the same
        // rename and materialize the current type.
        var entity = new CurrentNs.Moved { Value = "kept" }.ToTableEntity(PartitionKey, RowKey, persistType: true);
        entity[TableEntitySerializer.TypeNameColumnName] = legacyToken;

        var restored = Assert.IsType<CurrentNs.Moved>(entity.FromTableEntity());

        Assert.Equal("kept", restored.Value);
    }

    /// <summary>
    /// A getter-only wrapper whose single constructor parameter IS the stored JSON — a bare array,
    /// not an object. The historical Newtonsoft shape for collection wrappers (e.g. a
    /// JobLevelCollection stored as Levels__Json). The converter must feed the array itself into
    /// the parameter rather than default it to null.
    /// </summary>
    [Fact]
    public void SingleParameterConstructorWithArrayRootedJson_RoundTrips()
    {
        var source = new CollectionWrapperHolder
        {
            Wrapper = CollectionWrapper.Create(new[] { new Item { Name = "a" }, new Item { Name = "b" } })
        };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        Assert.True(entity.ContainsKey("Wrapper__Json"), "wrapper should be a __Json cell");

        var restored = entity.FromTableEntity<CollectionWrapperHolder>();

        Assert.Equal(2, restored.Wrapper.Count);
        Assert.Equal("a", restored.Wrapper[0].Name);
        Assert.Equal("b", restored.Wrapper[1].Name);
    }

    /// <summary>An unregistered, unresolvable token still fails loudly.</summary>
    [Fact]
    public void UnknownToken_StillThrows()
    {
        Assert.Throws<TypeLoadException>(() =>
            AssemblyQualifiedTypeDiscriminator.Instance.Resolve(
                "Unified.Data.Tables.Tests.DoesNotExist, Unified.Data.Tables.Tests", typeof(object)));
    }

    private sealed class QuestionGroupHolder
    {
        public QuestionGroup Group { get; set; } = null!;
    }

    /// <summary>Mirror of the production shape: IEnumerable ctor parameters, ImmutableList properties.</summary>
    private sealed class QuestionGroup
    {
        public QuestionGroup(string id, string title, double? score, IEnumerable<Question> questions, IEnumerable<AnswerOption> answerOptions)
        {
            Id = id;
            Title = title;
            Score = score;
            Questions = ImmutableList<Question>.Empty.AddRange(questions);
            AnswerOptions = ImmutableList<AnswerOption>.Empty.AddRange(answerOptions);
        }

        public string Id { get; }

        public string Title { get; }

        public double? Score { get; }

        public ImmutableList<Question> Questions { get; }

        public ImmutableList<AnswerOption> AnswerOptions { get; }
    }

    private sealed class Question
    {
        public Question(string id, string title)
        {
            Id = id;
            Title = title;
        }

        public string Id { get; }

        public string Title { get; }
    }

    private sealed class AnswerOption
    {
        public AnswerOption(string id, double? score)
        {
            Id = id;
            Score = score;
        }

        public string Id { get; }

        public double? Score { get; }
    }

    private sealed class MatchingHolder
    {
        public Matching Value { get; set; } = null!;
    }

    private sealed class Matching
    {
        public Matching(string name, int count)
        {
            Name = name;
            Count = count;
        }

        public string Name { get; }

        public int Count { get; }
    }

    private sealed class CollectionWrapperHolder
    {
        public CollectionWrapper Wrapper { get; set; } = null!;
    }

    /// <summary>Mirror of the production shape: private JsonConstructor taking the collection itself.</summary>
    private sealed class CollectionWrapper
    {
        [Foreign.JsonConstructor]
        private CollectionWrapper(IEnumerable<Item> items)
        {
            Items = ImmutableList<Item>.Empty.AddRange(items);
        }

        public ImmutableList<Item> Items { get; }

        public int Count => Items.Count;

        public Item this[int index] => Items[index];

        public static CollectionWrapper Create(IEnumerable<Item> items)
        {
            return new CollectionWrapper(items);
        }
    }

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
