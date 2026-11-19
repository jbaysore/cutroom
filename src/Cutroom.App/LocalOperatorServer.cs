using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;

namespace Cutroom.App;

/// <summary>Loopback-only health and graceful-stop endpoints for the local Project Hub operator.</summary>
internal sealed class LocalOperatorServer(Dispatcher dispatcher, Action closeWindow) : IDisposable
{
    public const int Port = 3891;

    readonly TcpListener listener = new(IPAddress.Loopback, Port);
    readonly CancellationTokenSource stop = new();

    public void Start()
    {
        listener.Start();
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Handle(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            var requestStop = false;
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(stop.Token);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }

                var parts = first?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) ?? [];
                bool health = parts.Length >= 2 && parts[0] == "GET" && parts[1] == "/health";
                requestStop = parts.Length >= 2 && parts[0] == "POST" && parts[1] == "/stop";
                int status = health ? 200 : requestStop ? 202 : 404;
                string reason = health ? "OK" : requestStop ? "Accepted" : "Not Found";
                string body = health ? "{\"service\":\"cutroom\"}" : requestStop ? "{\"stopping\":true}" : "{\"error\":\"not found\"}";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                string headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (SocketException) { }

            if (requestStop && !stop.IsCancellationRequested)
                _ = dispatcher.BeginInvoke(closeWindow);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
