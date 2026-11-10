using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Azure;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The Azurite probe skips the integration tests only when nothing is listening; a reachable emulator
/// that fails (a timeout, a dropped connection) fails the run, and so does an absent one when required.
/// </summary>
public class AzuriteProbeTests
{
    // The emulator's well-known development account (public, documented), pointed at the given port.
    private static string Endpoint(int port) =>
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        $"TableEndpoint=http://127.0.0.1:{port}/devstoreaccount1;BlobEndpoint=http://127.0.0.1:{port}/devstoreaccount1;";

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public void Unreachable_IsARefusedOrUnroutableConnection_NotATimeout()
    {
        var refused = new RequestFailedException("refused",
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));
        Assert.True(AzuriteProbe.IsUnreachable(refused));
        Assert.True(AzuriteProbe.IsUnreachable(new SocketException((int)SocketError.HostUnreachable)));
        Assert.True(AzuriteProbe.IsUnreachable(new AggregateException(new SocketException((int)SocketError.NetworkUnreachable))));

        Assert.False(AzuriteProbe.IsUnreachable(new TaskCanceledException("timeout", new TimeoutException())));
        Assert.False(AzuriteProbe.IsUnreachable(new RequestFailedException(403, "AuthenticationFailed")));
        Assert.False(AzuriteProbe.IsUnreachable(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
    }

    [Fact]
    public async Task NothingListening_Skips_UnlessRequired()
    {
        var connection = Endpoint(UnusedPort());

        Assert.False(await AzuriteProbe.ProbeAsync(connection, blobs: true, required: false));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AzuriteProbe.ProbeAsync(connection, blobs: true, required: true));
        Assert.Contains(AzuriteProbe.RequireVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListeningButFailing_FailsAfterRetries_InsteadOfSkipping()
    {
        // Accepts every connection and drops it at once: reachable, but every request fails.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var accepted = 0;
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                    client.Client.LingerState = new LingerOption(true, 0);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, TestContext.Current.CancellationToken);

        var connection = Endpoint(((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AzuriteProbe.ProbeAsync(connection, blobs: false, required: false));
        Assert.True(accepted >= AzuriteProbe.Attempts, $"{accepted} connections");

        await stop.CancelAsync();
        listener.Stop();
        await server;
    }
}
