# Unified.Data.Tables

[![Build & Analyze](https://github.com/SergiySeletsky/Unified.Data.Tables/actions/workflows/sonarqube.yml/badge.svg)](https://github.com/SergiySeletsky/Unified.Data.Tables/actions/workflows/sonarqube.yml)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=testasm_unified-data-tables&metric=alert_status)](https://sonarcloud.io/summary/overall?id=testasm_unified-data-tables)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=testasm_unified-data-tables&metric=coverage)](https://sonarcloud.io/summary/overall?id=testasm_unified-data-tables)
[![Unified.Data.Tables on NuGet](https://img.shields.io/nuget/v/Unified.Data.Tables.svg?label=Unified.Data.Tables)](https://www.nuget.org/packages/Unified.Data.Tables)
[![Unified.Data.Tables.Abstractions on NuGet](https://img.shields.io/nuget/v/Unified.Data.Tables.Abstractions.svg?label=Unified.Data.Tables.Abstractions)](https://www.nuget.org/packages/Unified.Data.Tables.Abstractions)
[![Downloads](https://img.shields.io/nuget/dt/Unified.Data.Tables.svg)](https://www.nuget.org/packages/Unified.Data.Tables)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg?style=flat)](https://github.com/SergiySeletsky/Unified.Data.Tables/compare)

A small, reusable **Azure Table Storage** data layer for .NET: a generic `IStorage<T>` repository with
in-memory caching, optimistic concurrency, builder-driven partial updates, role-gated properties, and a
reflection-based object-graph serializer that transparently handles nested types and the 64&nbsp;KB
per-cell limit.

It exists so the same battle-tested storage primitives can be shared across projects instead of being
copy-pasted into each one.

---

## Features

- **Generic repository** — `IStorage<T>` over Azure Tables, one table per entity type (`typeof(T).Name`).
- **Reflection-based serializer** — flattens nested objects into `Parent_Child` columns, stores enums as
  strings and money-style `decimal`s as doubles, and falls back to JSON (then GZip) for collections and
  complex graphs. Oversized cells are compressed — and, as a last resort, truncated — so a single large
  property can never blow the 64&nbsp;KB limit and lose the whole row.
- **In-memory caching** — reads are served from `IMemoryCache` (1&nbsp;hour sliding TTL); writes keep the
  entity cache coherent and invalidate the relevant query caches automatically.
- **Optimistic concurrency (ETag)** — a caller-supplied `ETag` enforces strict concurrency (a conflict
  surfaces as `RequestFailedException` 412 → map to 409); without one, a stale-cache conflict is retried once.
- **Partial (Merge) updates** — `UpdateAsync(id, builder)` writes only the columns you declare, leaving the
  rest of the row untouched with no read required.
- **Protected properties** — mark a property `[ProtectedProperty("admin,...")]` and role-gate writes through
  a pluggable `IProtectedPropertyAuthorizer` (the package itself has **no** ASP.NET Core dependency).
- **Batch partition delete** — `DeletePartitionAsync` removes a whole partition in 100-entity transactions.
- **Composite id convention** — `Id` is `"{PartitionKey}|{RowKey}"`; row keys may themselves contain `|`.

---

## Installation

```bash
dotnet add package Unified.Data.Tables
```

This is shipped as two packages:

| Package | Contents | Use it in |
| --- | --- | --- |
| **Unified.Data.Tables** | `TableStorage<T>`, the serializer, DI helpers (Azure dependencies) | server / host projects |
| **Unified.Data.Tables.Abstractions** | `Entity`, `IStorage<T>`, `UpdateBuilder<T>`, `[ProtectedProperty]`, `IProtectedPropertyAuthorizer` — no Azure/hosting deps | shared/domain & Blazor WebAssembly projects |

`Unified.Data.Tables` references the abstractions transitively, so most apps just install it. In a **browser-safe** shared library that only defines entities and repository contracts, reference the abstractions alone:

```bash
dotnet add package Unified.Data.Tables.Abstractions
```

Requires **.NET 10** and an Azure Storage account (or the local [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) emulator).

---

## Quick start

### 1. Define an entity

Derive from `Entity`. `Id` is the composite `"{PartitionKey}|{RowKey}"`; `Created`, `Modified` and `ETag`
are managed for you.

```csharp
using Unified.Data.Tables;

public sealed class Customer : Entity
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public decimal Balance { get; set; }
}
```

### 2. Register it

```csharp
using Azure.Data.Tables;
using Unified.Data.Tables;

// Option A — build the TableServiceClient from a connection string:
builder.Services.AddUnifiedTableStorage(connectionString);

// Option B — you already register a TableServiceClient yourself:
builder.Services.AddSingleton(_ => new TableServiceClient(connectionString));
builder.Services.AddUnifiedTableStorage();
```

Both register `IMemoryCache` and the open-generic `IStorage<T>` → `TableStorage<T>` mapping as singletons.

### 3. Use it

```csharp
public sealed class CustomerService(IStorage<Customer> storage)
{
    public Task<Customer> Add(Customer c)          => storage.CreateAsync(c);
    public Task<Customer?> Get(string id)          => storage.OneAsync(id);
    public Task<bool> Exists(string id)            => storage.ExistsAsync(id);
    public Task<IEnumerable<Customer>> InRegion(string region) => storage.QueryAsync(region);
    public Task Remove(string id)                  => storage.DeleteAsync(id);
}
```

```csharp
// PartitionKey = "eu", RowKey = "alice@example.com"
var customer = new Customer
{
    Id = "eu|alice@example.com",
    Name = "Alice",
    Email = "alice@example.com",
    Balance = 100m
};

await storage.CreateAsync(customer);           // ids are normalized: trim → spaces to '-' → lower-case
var loaded  = await storage.OneAsync("eu|alice@example.com");
var euOnes  = await storage.QueryAsync("eu");  // scope to a partition (omit for the whole table)
```

---

## `IStorage<T>`

| Method | Description |
| --- | --- |
| `CreateAsync(entity)` | Insert a new row; returns it with its populated `ETag`. |
| `OneAsync(id)` | Fetch one row by composite id, or `null`. |
| `ExistsAsync(id)` | Whether a row exists (cache-aware). |
| `QueryAsync(partition?)` | All rows, or just one partition when supplied. |
| `UpdateAsync(entity)` | Full replace with ETag concurrency. |
| `UpdateAsync(id, builder)` | Partial `Merge` — writes only the declared columns. |
| `DeleteAsync(id)` | Delete one row. |
| `DeletePartitionAsync(partition)` | Batch-delete a whole partition; returns the count. |

### Partial updates

Only the properties you set are written; everything else on the row is preserved, and no read is needed.

```csharp
await storage.UpdateAsync("eu|alice@example.com", b => b
    .SetProperty(x => x.Balance, 250m)
    .SetProperty(x => x.Name, "Alice A."));
```

### Optimistic concurrency

`OneAsync`/`QueryAsync` populate `Entity.ETag`. Pass it back on `UpdateAsync(entity)` for a strict
check — a concurrent modification throws `RequestFailedException` with status `412`.

```csharp
var c = await storage.OneAsync(id);
c!.Balance += 100;
await storage.UpdateAsync(c);   // 412 if the row changed since it was read
```

---

## Protected properties

Gate sensitive columns behind roles. Enforcement runs on the whole-entity update path and needs an
`IProtectedPropertyAuthorizer`; if none is registered, changing a protected value is denied.

```csharp
public sealed class Employee : Entity
{
    public string Name { get; set; } = "";

    [ProtectedProperty("admin,accountant")]
    public decimal Salary { get; set; }
}
```

Provide an authorizer from your host (this is where the `ClaimsPrincipal` lives — kept out of the package
so it stays dependency-light):

```csharp
public sealed class RoleAuthorizer(IHttpContextAccessor accessor) : IProtectedPropertyAuthorizer
{
    public bool IsAllowed(string roles) =>
        roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
             .Any(role => accessor.HttpContext?.User.IsInRole(role) == true);
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IProtectedPropertyAuthorizer, RoleAuthorizer>();
```

For the builder path, protected properties are rejected unless you opt in after verifying authorization:

```csharp
await storage.UpdateAsync(id, b => b.AllowProtected().SetProperty(x => x.Salary, 5000m));
```

---

## Serialization

`TableStorage<T>` uses `TableEntitySerializer` under the hood, but you can call it directly:

```csharp
TableEntity row = customer.ToTableEntity(partitionKey, rowKey);
Customer     back = row.FromTableEntity<Customer>();
```

- **Scalars** map to native cells; **enums** become strings; **`decimal`** is stored as `double`.
- **Nested objects** fan out to `Parent_Child` columns.
- **Collections / complex graphs** serialize to JSON (`__Json` column suffix), GZip-compressed
  (`__GZip`) when they exceed the cell limit.
- **Legacy tolerance** — a stored date surfaced by the SDK as a `DateTime` or a `string` still
  deserializes into a `DateTimeOffset` (or `DateTime`) property without throwing.
- Set `persistType: true` to embed the type name and use the late-bound `FromTableEntity()` overload.

---

## Id convention

`Entity.Id` is a composite string split on the **first** `|`:

| `Id` | PartitionKey | RowKey |
| --- | --- | --- |
| `"eu|alice"` | `eu` | `alice` |
| `"vision|exec|agent"` | `vision` | `exec|agent` |
| `"single"` | `single` | `single` |

Ids are normalized on write (`trim → replace spaces with '-' → ToLowerInvariant`). `Id` is also stored as
a data column, so the full composite id is preserved on read.

---

## Building & testing

```bash
dotnet build Unified.Data.Tables.slnx -c Release
dotnet test  Unified.Data.Tables.slnx -c Release
```

The test suite mixes fast unit tests (Azure SDK mocked with NSubstitute) with integration tests that run
against a local **Azurite** emulator. The integration tests self-skip when Azurite is not reachable, so
`dotnet test` is safe to run anywhere.

---

## License

Licensed under the [MIT License](LICENSE). Copyright © Serhii Seletskyi.
