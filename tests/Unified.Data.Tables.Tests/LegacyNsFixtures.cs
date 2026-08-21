namespace Unified.Data.Tables.Tests.CurrentNs
{
    public sealed class Moved
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class MovedHolder
    {
        public Moved Item { get; set; } = null!;
    }
}
