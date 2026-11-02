using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace Homeostat.Tests;

/// <summary>
/// A Redis server for integration tests: the one in <c>HOMEOSTAT_TEST_REDIS</c> (for example in CI), otherwise a
/// local <c>redis-server</c> started on a free port. Tests are skipped when neither is available.
/// </summary>
internal static class RedisFixture
{
    private static readonly Lazy<string?> Server = new(Start);

    public static string? ConnectionString => Server.Value;

    /// <summary>Skips the test when no Redis server is available.</summary>
    public static string Require()
    {
        var connection = ConnectionString;
        Assert.SkipWhen(connection is null, "No Redis server: set HOMEOSTAT_TEST_REDIS or install redis-server.");
        return connection!;
    }

    public static IConnectionMultiplexer Connect() => ConnectionMultiplexer.Connect(Require());

    /// <summary>A key prefix unique to the calling test.</summary>
    public static string Prefix() => $"homeostat-test:{Guid.NewGuid():N}:";

    private static string? Start()
    {
        if (Environment.GetEnvironmentVariable("HOMEOSTAT_TEST_REDIS") is { Length: > 0 } configured)
        {
            return configured;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var process = Process.Start(new ProcessStartInfo("redis-server", $"--port {port} --save \"\" --appendonly no")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            };

            var connection = $"127.0.0.1:{port}";
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using var probe = ConnectionMultiplexer.Connect(connection + ",abortConnect=false,connectTimeout=200");
                    if (probe.IsConnected)
                    {
                        return connection;
                    }
                }
                catch (RedisConnectionException)
                {
                    // Not ready yet.
                }

                Thread.Sleep(100);
            }

            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // redis-server is not installed.
            return null;
        }
    }
}
