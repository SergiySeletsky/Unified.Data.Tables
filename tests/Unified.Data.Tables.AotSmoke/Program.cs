using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
#pragma warning disable UDT0001 // the raw-table capability asserts the stored JSON contract
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Unified.Data.Tables;
using Unified.Data.Tables.Identity;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Sqlite;

// The AOT contract: a source-generated JSON context for every JSON-stored shape, and explicit
// metadata registration for every reflected type in the graph.
TableJson.UseContext(new AotJsonContext(new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
}));

TableEntityMetadata.Register<StudentModel>();
TableEntityMetadata.Register<Address>();
TableEntityMetadata.Register<Language>();
TableEntityMetadata.Register<Proficiency>();
TableEntityMetadata.Register<PaymentModel>();
TableEntityMetadata.Register<TimeRecord>();
TableEntityMetadata.Register<Document>();
TableEntityMetadata.Register<JsonDocument>();
TableEntityMetadata.Register<Contact>();
TableEntityMetadata.Register<Entity>();
TableEntityMetadata.RegisterFilterOperators<decimal>();

await InMemoryAsync();
await JsonAsync();
await SplitAndOffloadAsync();
await PolymorphicAsync();
await DependencyInjectionAsync();
await SqliteAsync();
await IdentityAsync();
await UnregisteredTypeFailsFastAsync();

Console.WriteLine("AOT SMOKE OK");

static void Check(bool condition, string what)
{
    if (!condition)
        throw new InvalidOperationException($"AOT smoke failed: {what}");
    Console.WriteLine($"ok: {what}");
}

static async Task InMemoryAsync()
{
    var store = new InMemoryStorage<StudentModel>();
    var created = await store.CreateAsync(new StudentModel
    {
        Id = "students|alice",
        Name = "Alice",
        Language = Language.Ukrainian,
        Proficiency = Proficiency.Advanced,
        Balance = 12.5m,
        Rank = 3,
        Blocked = false,
        Home = new Address { City = "Kyiv", Zip = "01001" },
    });

    var read = await store.OneAsync("students|alice");
    Check(read is not null, "in-memory read returns the row");
    Check(read!.Name == "Alice" && read.Language == Language.Ukrainian && read.Balance == 12.5m,
        "in-memory scalar, enum and decimal round-trip");
    Check(read.Home.City == "Kyiv" && read.Home.Zip == "01001",
        "in-memory flattened nested object round-trips");

    var query = await store.QueryAsync(s => s.Balance > 10m && s.Language == Language.Ukrainian);
    Check(query.Count == 1, "in-memory expression query works");

    var captured = 2;
    var capturedQuery = await store.QueryAsync(s => s.Rank > captured);
    Check(capturedQuery.Count == 1, "in-memory captured-value query works");

    await store.DeleteAsync(created.Id);
    Check(await store.OneAsync(created.Id) is null, "in-memory delete works");
}

static async Task JsonAsync()
{
    var store = new InMemoryStorage<StudentModel>();
    await store.CreateAsync(new StudentModel
    {
        Id = "students|json",
        Name = "Json",
        Tags = ["one", "two"],
        Languages = [Language.Ukrainian],
        Attributes = new Dictionary<string, string> { ["region"] = "eu" },
        Contact = new Contact("Alice Smith", 42),
    });

    var read = await store.OneAsync("students|json");
    Check(read?.Tags.SequenceEqual(["one", "two"]) == true, "JSON list cell round-trips");
    Check(read?.Languages.SequenceEqual([Language.Ukrainian]) == true, "JSON enum list cell uses string names");
    Check(read?.Attributes["region"] == "eu", "JSON dictionary cell round-trips");
    Check(read?.Contact == new Contact("Alice Smith", 42), "JSON record cell round-trips");

    if (store.TryGetCapability<IRawTable>(out var raw))
    {
        var row = await raw.GetAsync("students", "json");
        Check(row?.Cells.TryGetValue("Tags__Json", out var tags) == true
            && tags.Value as string == "[\"one\",\"two\"]",
            "JSON list cell uses the same camelCase/array contract as the reflection serializer");
        Check(row?.Cells.TryGetValue("Languages__Json", out var languages) == true
            && languages.Value as string == "[\"Ukrainian\"]",
            "JSON enum list uses the same PascalCase names as the reflection serializer");
        Check(row?.Cells.TryGetValue("Contact__Json", out var contact) == true
            && contact.Value as string == "{\"fullName\":\"Alice Smith\",\"age\":42}",
            "JSON record cell uses the source-generated camelCase contract");
    }
}

static async Task SplitAndOffloadAsync()
{
    var body = new string('x', 100_000);
    var splitStore = new InMemoryStorage<Document>();
    await splitStore.CreateAsync(new Document { Id = "documents|split", Body = body });
    Check((await splitStore.OneAsync("documents|split"))?.Body == body, "split value round-trips");

    var offloadStore = new InMemoryStorage<Document>(options: new StorageOptions
    {
        Offload = new OffloadOptions { ContainerName = "values", ThresholdBytes = 1024 },
    });
    await offloadStore.CreateAsync(new Document { Id = "documents|offload", Body = body });
    Check((await offloadStore.OneAsync("documents|offload"))?.Body == body, "offloaded value round-trips");

    var jsonBody = Enumerable.Range(0, 20_000).Select(i => $"item-{i:D5}").ToList();
    var jsonStore = new InMemoryStorage<JsonDocument>();
    await jsonStore.CreateAsync(new JsonDocument { Id = "documents|json-split", Values = jsonBody });
    var jsonBack = await jsonStore.OneAsync("documents|json-split");
    Check(jsonBack?.Values.SequenceEqual(jsonBody) == true, "split JSON value round-trips through the context");

    try
    {
        await jsonStore.CreateAsync(new JsonDocument
        {
            Id = "documents|ill-formed",
            Values = [new string('\uD800', 1)],
        });
        throw new InvalidOperationException("AOT smoke failed: ill-formed split JSON was accepted");
    }
    catch (NotSupportedException)
    {
        Console.WriteLine("ok: ill-formed text in a split JSON value is refused");
    }
}

static async Task PolymorphicAsync()
{
    var map = new TypeDiscriminatorMap()
        .Map<StudentModel>("student")
        .Map<PaymentModel>("payment");

    var store = new InMemoryPolymorphicStorage<Entity>("polymorphic", new StorageOptions
    {
        TypeDiscriminator = map,
    });
    await store.InsertAsync(new PolymorphicWrite<Entity>(
        new TableKey("rows", "one"),
        new StudentModel { Id = "rows|one", Name = "Poly" }));

    var read = await store.GetAsync(new TableKey("rows", "one"));
    Check(read?.Item is StudentModel { Name: "Poly" }, "polymorphic map round-trips a subtype");

    var threw = false;
    try
    {
        _ = new InMemoryPolymorphicStorage<Entity>("unmapped");
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    Check(threw, "polymorphic storage without a map fails clearly in AOT");
}

static async Task DependencyInjectionAsync()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddUnifiedInMemoryStorage<StudentModel>();

    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredService<IStorage<StudentModel>>();
    await store.CreateAsync(new StudentModel { Id = "students|di", Name = "DI" });
    Check((await store.OneAsync("students|di"))?.Name == "DI", "closed-generic DI registration works");

    var sqliteServices = new ServiceCollection();
    sqliteServices.AddLogging();
    sqliteServices.AddUnifiedSqliteStorage<PaymentModel>(":memory:");

    await using var sqliteProvider = sqliteServices.BuildServiceProvider();
    var sqliteStore = sqliteProvider.GetRequiredService<IStorage<PaymentModel>>();
    await sqliteStore.CreateAsync(new PaymentModel { Id = "payments|di", Student = "Alice", Amount = 3.5m });
    Check((await sqliteStore.OneAsync("payments|di"))?.Student == "Alice",
        "closed-generic SQLite DI registration works");
}

static async Task SqliteAsync()
{
    using var account = new SqliteStorageAccount(":memory:");
    var store = new SqliteStorage<StudentModel>(account);
    await store.CreateAsync(new StudentModel
    {
        Id = "students|sqlite",
        Name = "SQLite",
        Language = Language.German,
        Balance = 7.25m,
        Tags = ["db"],
    });

    var read = await store.OneAsync("students|sqlite");
    Check(read?.Name == "SQLite" && read.Language == Language.German && read.Balance == 7.25m,
        "SQLite scalar, enum and decimal round-trip");
    Check(read?.Tags.SequenceEqual(["db"]) == true, "SQLite JSON cell round-trips");

    var split = new SqliteStorage<Document>(account);
    var body = new string('y', 100_000);
    await split.CreateAsync(new Document { Id = "documents|sqlite", Body = body });
    Check((await split.OneAsync("documents|sqlite"))?.Body == body, "SQLite split value round-trips");
}

static async Task IdentityAsync()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddUnifiedInMemoryStorage();
    services.AddIdentityCore<IdentityUser>(options => options.Password.RequiredLength = 8)
        .AddRoles<IdentityRole>()
        .AddUnifiedIdentityStores();

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

    var user = new IdentityUser { UserName = "alice", Email = "alice@example.com" };
    var created = await users.CreateAsync(user, "P@ssw0rd!");
    Check(created.Succeeded, "Identity creates a user");

    var found = await users.FindByNameAsync("alice");
    Check(found is not null && await users.CheckPasswordAsync(found, "P@ssw0rd!"),
        "Identity finds the user and checks the password");

    Check((await users.AddClaimAsync(found!, new Claim("role", "admin"))).Succeeded,
        "Identity adds a claim");
    Check((await users.GetClaimsAsync(found!)).Count == 1, "Identity reads the claim");

    var queryable = (IQueryableUserStore<IdentityUser>)scope.ServiceProvider.GetRequiredService<IUserStore<IdentityUser>>();
    Check(queryable.Users.Where(u => u.UserName == "alice").Count() == 1,
        "Identity IQueryable user store works");

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    Check((await roles.CreateAsync(new IdentityRole("teacher"))).Succeeded, "Identity creates a role");
    var roleStore = (IQueryableRoleStore<IdentityRole>)scope.ServiceProvider.GetRequiredService<IRoleStore<IdentityRole>>();
    Check(roleStore.Roles.Count() == 1, "Identity IQueryable role store works");
    Check((await users.AddToRoleAsync(found!, "teacher")).Succeeded, "Identity assigns the role");
}

static async Task UnregisteredTypeFailsFastAsync()
{
    var previous = TableEntityMetadata.RequireRegistration;
    TableEntityMetadata.RequireRegistration = true;
    try
    {
        var store = new InMemoryStorage<Unregistered>();
        await store.CreateAsync(new Unregistered { Id = "x|1" });
        throw new InvalidOperationException("AOT smoke failed: an unregistered type was accepted");
    }
    catch (Exception ex) when (HasRegistrationMessage(ex))
    {
        Console.WriteLine("ok: an unregistered type fails with the registration message");
    }
    finally
    {
        TableEntityMetadata.RequireRegistration = previous;
    }

    static bool HasRegistrationMessage(Exception exception) =>
        exception.Message.Contains("Register")
        || (exception.InnerException is { } inner && HasRegistrationMessage(inner));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Language>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(Contact))]
internal sealed partial class AotJsonContext : JsonSerializerContext;

public enum Language { English, Ukrainian, German }

public enum Proficiency { Beginner, Advanced }

public sealed class Address
{
    public string City { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
}

public sealed class StudentModel : Entity
{
    public string Name { get; set; } = string.Empty;
    public Language Language { get; set; }
    public Proficiency Proficiency { get; set; }
    public decimal Balance { get; set; }
    public int Rank { get; set; }
    public bool Blocked { get; set; }
    public Address Home { get; set; } = new();
    public List<string> Tags { get; set; } = [];
    public List<Language> Languages { get; set; } = [];
    public Dictionary<string, string> Attributes { get; set; } = [];
    public Contact? Contact { get; set; }
}

public sealed record Contact(string FullName, int Age);

public sealed class PaymentModel : Entity
{
    public string Student { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime? Date { get; set; }
}

public sealed class TimeRecord : Entity
{
    public DateTimeOffset Date { get; set; }
    public int DurationMinutes { get; set; }
    public decimal HourlyPrice { get; set; }
}

public sealed class Document : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string Body { get; set; } = string.Empty;
}

public sealed class JsonDocument : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public List<string> Values { get; set; } = [];
}

public sealed class Unregistered : Entity
{
    public string Value { get; set; } = string.Empty;
}
