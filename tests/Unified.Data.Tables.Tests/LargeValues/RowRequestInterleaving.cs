using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Pipeline policies for the row-race tests that run against Azurite: they let another caller in just
/// before a chosen request of one store is sent, and change nothing of what either caller sends. Put on
/// a table client with <see cref="AzuriteCleanupTestBase.TableServiceWith"/>.
/// </summary>
internal static class RowRequestInterleaving
{
    /// <summary>
    /// The continuation rows a split-aware store writes for <paramref name="body"/> of a
    /// <see cref="SplitTextEntity"/> under <paramref name="rk"/>: what a test puts in by hand as a leftover.
    /// </summary>
    internal static List<TableEntity> ContinuationRowsOf(string pk, string rk, string body)
    {
        var logical = TableEntitySerializer.ToTableEntity(
            new SplitTextEntity { Id = pk + "|" + rk, Body = body }, pk, rk, ValueWriteContext.Public, []);
        return [.. SplitRowLayout.Plan(logical).Parts];
    }

    /// <summary>A GET that is neither a point read of one entity nor a call on the table collection: a query page.</summary>
    private static bool IsRangeQuery(HttpMessage message) =>
        message.Request.Method == RequestMethod.Get
        && !message.Request.Uri.Path.Contains("(PartitionKey=", StringComparison.Ordinal)
        && !message.Request.Uri.Path.EndsWith("/Tables", StringComparison.Ordinal);

    private static bool IsTransaction(HttpMessage message) =>
        message.Request.Method == RequestMethod.Post && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal);

    /// <summary>Runs an action once, just before the Nth query page (a GET that is not a point read; each page is one request) is sent.</summary>
    internal sealed class BeforeRangeQuery(int ordinal, Func<Task> action) : HttpPipelinePolicy
    {
        private int seen;
        private int fired;

        /// <summary>1 once the action has run.</summary>
        public int Fired => Volatile.Read(ref fired);

        /// <summary>How many query pages were sent.</summary>
        public int Queries => Volatile.Read(ref seen);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsRangeQuery(message) && Interlocked.Increment(ref seen) == ordinal)
            {
                Interlocked.Exchange(ref fired, 1);
                await action();
            }
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsRangeQuery(message) && Interlocked.Increment(ref seen) == ordinal)
            {
                Interlocked.Exchange(ref fired, 1);
                action().GetAwaiter().GetResult();
            }
            ProcessNext(message, pipeline);
        }
    }

    /// <summary>Runs an action once, just before the Nth entity-group transaction (<c>POST $batch</c>) is sent.</summary>
    internal sealed class BeforeTransaction(int ordinal, Func<Task> action) : HttpPipelinePolicy
    {
        private int seen;
        private int fired;

        /// <summary>1 once the action has run.</summary>
        public int Fired => Volatile.Read(ref fired);

        /// <summary>How many transactions were sent.</summary>
        public int Transactions => Volatile.Read(ref seen);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsTransaction(message) && Interlocked.Increment(ref seen) == ordinal)
            {
                Interlocked.Exchange(ref fired, 1);
                await action();
            }
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsTransaction(message) && Interlocked.Increment(ref seen) == ordinal)
            {
                Interlocked.Exchange(ref fired, 1);
                action().GetAwaiter().GetResult();
            }
            ProcessNext(message, pipeline);
        }
    }

    /// <summary>
    /// Records each table request as its method and kind: <c>POST $batch</c> (a transaction),
    /// <c>GET entity</c> (a point read), <c>GET query</c> (a query page), and so on.
    /// </summary>
    internal sealed class RequestLog : HttpPipelineSynchronousPolicy
    {
        private readonly List<string> log = [];

        public List<string> Snapshot()
        {
            lock (log) return [.. log];
        }

        public void Clear()
        {
            lock (log) log.Clear();
        }

        public override void OnSendingRequest(HttpMessage message)
        {
            var path = message.Request.Uri.Path;
            var kind = path.EndsWith("$batch", StringComparison.Ordinal) ? "$batch"
                : path.Contains("(PartitionKey=", StringComparison.Ordinal) ? "entity"
                : path.EndsWith("/Tables", StringComparison.Ordinal) ? "Tables"
                : message.Request.Method == RequestMethod.Post ? "entity" // an insert is a POST to the table
                : "query";
            lock (log) log.Add(message.Request.Method.Method + " " + kind);
        }
    }
}
