using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.AppHost;

/// <summary>
/// The clamav container's health check (ARV-092b): healthy once clamd answers PING with PONG on its TCP port, which it does
/// only after its signatures are loaded (about 20 seconds). WaitFor waits for a dependency's health checks, so api-main
/// starts only once its uploads can be scanned, as it waits for Redis; without a check it would wait for the container to
/// run, not for clamd to answer.
/// </summary>
public static class ClamdPing
{
    /// <summary>The key the container names in WithHealthCheck.</summary>
    public const string HealthCheckKey = "clamav-ping";

    /// <summary>Healthy when clamd on host:port answers zPING with PONG; unhealthy on any other answer or none.</summary>
    public static async Task<HealthCheckResult> CheckAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            var stream = client.GetStream();
            await stream.WriteAsync("zPING\0"u8.ToArray(), cancellationToken);

            // clamd answers PONG and a NUL, then closes; anything longer is not its answer.
            var buffer = new byte[16];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0)
                    break;
                length += read;
                if (buffer[length - 1] == 0)
                    break;
            }

            return Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\0') == "PONG"
                ? HealthCheckResult.Healthy("clamd answers PONG")
                : HealthCheckResult.Unhealthy("clamd did not answer PONG");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return HealthCheckResult.Unhealthy("clamd does not answer yet", exception);
        }
    }
}
