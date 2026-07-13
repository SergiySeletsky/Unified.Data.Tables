// Two entity types that share a simple name ("Widget") but live in different namespaces, so
// typeof(T).Name collides while typeof(T).FullName does not. Used to prove the 0.5.2 cache-key
// fix (G5): entries are keyed by FullName, so a shared IMemoryCache no longer cross-contaminates
// two same-named types. Block-scoped namespaces so both live in one file.

namespace Unified.Data.Tables.Tests.CollisionA
{
    public sealed class Widget : Entity
    {
        public string Name { get; set; } = "";
    }
}

namespace Unified.Data.Tables.Tests.CollisionB
{
    public sealed class Widget : Entity
    {
        public string Name { get; set; } = "";
    }
}
