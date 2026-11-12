using System.Net.Http;
using System.Net.Sockets;
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// Decides whether the Azurite integration tests run. Only an emulator that is not there at all (the
/// connection is refused, or the host or network is unreachable) makes them skip; any other probe
/// failure (a timeout under load, an authentication or protocol error) is retried and then fails the
/// run, so a broken emulator never passes as "skipped". With <see cref="RequireVariable"/> set to
/// <c>1</c> (as CI sets it) an unreachable emulator fails the run too.
/// </summary>
internal static class AzuriteProbe
{
    /// <summary>Set to <c>1</c> to turn "Azurite is not reachable" from a skip into a failure.</summary>
    internal const string RequireVariable = "UNIFIED_TABLES_REQUIRE_AZURITE";

    internal const int Attempts = 3;

    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>True when <see cref="RequireVariable"/> is <c>1</c> or <c>true</c>.</summary>
    internal static bool Required =>
        Environment.GetEnvironmentVariable(RequireVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the table endpoint (and, with <paramref name="blobs"/>, the blob endpoint) answers; false
    /// when it is unreachable and not <paramref name="required"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The emulator is unreachable and required, or it is reachable but kept failing.
    /// </exception>
    internal static async Task<bool> ProbeAsync(string connectionString, bool blobs, bool required)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var tableOptions = new TableClientOptions();
                tableOptions.Retry.MaxRetries = 0;
                tableOptions.Retry.NetworkTimeout = AttemptTimeout;
                await foreach (var _ in new TableServiceClient(connectionString, tableOptions).QueryAsync(maxPerPage: 1))
                    break;
                if (blobs)
                {
                    var blobOptions = new BlobClientOptions();
                    blobOptions.Retry.MaxRetries = 0;
                    blobOptions.Retry.NetworkTimeout = AttemptTimeout;
                    await foreach (var _ in new BlobServiceClient(connectionString, blobOptions).GetBlobContainersAsync())
                        break;
                }
                return true;
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                if (required)
                    throw new InvalidOperationException(
                        $"Azurite is not reachable ({ex.GetBaseException().Message}) and {RequireVariable}=1 requires it.", ex);
                return false;
            }
            catch (Exception ex) when (attempt < Attempts)
            {
                // Reachable but failing (a timeout under load, a transient error): try again.
                _ = ex;
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Azurite answered but the probe failed {Attempts} times; refusing to skip the integration tests.", ex);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or an exception it wraps) says nothing is listening: the connection
    /// was refused, or the host or network cannot be reached. A timeout is not one of these.
    /// </summary>
    internal static bool IsUnreachable(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket
                && socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable or SocketError.HostNotFound or SocketError.AddressNotAvailable)
                return true;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
                return true;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsUnreachable))
                return true;
        }
        return false;
    }
}
