using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TauConfigManager.Core;

public sealed class LocalHttpFileServer : IAsyncDisposable
{
    private readonly byte[] _payload;
    private readonly string _fileName;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public LocalHttpFileServer(IPAddress bindAddress, byte[] payload, string fileName = "payload.tar.gz")
    {
        _payload = payload;
        _fileName = fileName;
        _listener = new TcpListener(bindAddress, 0);
    }

    public int Port { get; private set; }

    public string GetUrl(IPAddress address)
    {
        return $"http://{address}:{Port}/{_fileName}";
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => ServeAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener.Stop();
        if (_serverTask != null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore listener shutdown exceptions.
            }
        }

        _cts?.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var stream = client.GetStream();
            var readBuffer = new byte[2048];
            _ = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                $"Content-Length: {_payload.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(_payload, cancellationToken).ConfigureAwait(false);
        }
    }
}

