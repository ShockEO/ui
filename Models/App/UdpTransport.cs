using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ShockUI.Services.App;

/// <summary>
/// LAN (UDP) transport. Sends each call to <see cref="Write"/> as a single
/// UDP datagram to <see cref="RemoteHost"/>:<see cref="RemotePort"/>, and
/// listens continuously on <see cref="LocalPort"/> for incoming datagrams.
/// Bytes arrive in <see cref="DataReceived"/> exactly as the framer expects.
///
/// Same wire bytes as UART — only the medium changes.
/// </summary>
public sealed class UdpTransport : ITransport
{
    public string RemoteHost { get; }
    public int RemotePort { get; }
    public int LocalPort { get; }

    private UdpClient? _client;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private IPEndPoint? _remoteEp;

    public UdpTransport(string remoteHost, int remotePort, int localPort)
    {
        RemoteHost = remoteHost;
        RemotePort = remotePort;
        LocalPort = localPort;
    }

    public bool IsOpen => _client is not null;

    public string Description => $"UDP {RemoteHost}:{RemotePort}  ←  :{LocalPort}";

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ErrorReceived;

    public bool Open()
    {
        try
        {
            Close();

            // Bind to the local listen port (any interface). The UdpClient
            // is used both for transmit and receive — sending from the same
            // bound endpoint makes sure replies come back to us.
            _client = new UdpClient(LocalPort);
            _remoteEp = new IPEndPoint(ResolveHost(RemoteHost), RemotePort);

            _listenCts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_listenCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(ex.Message);
            Close();
            return false;
        }
    }

    public void Close()
    {
        try { _listenCts?.Cancel(); } catch { }
        try { _client?.Close(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _listenCts = null;
        _listenTask = null;
        _remoteEp = null;
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (_client is null || _remoteEp is null)
            throw new InvalidOperationException("UDP transport is not open.");

        // UDP is datagram-oriented, so we send the slice as a single datagram.
        // The framer always passes whole frames so this maps 1:1.
        if (offset == 0 && count == data.Length)
        {
            _client.Send(data, count, _remoteEp);
        }
        else
        {
            var slice = new byte[count];
            Array.Copy(data, offset, slice, 0, count);
            _client.Send(slice, count, _remoteEp);
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Loop until cancelled. Each datagram is delivered as one DataReceived
        // event — the framer accumulates if frames are fragmented (they shouldn't
        // be over LAN since frames fit comfortably in a single MTU).
        while (!ct.IsCancellationRequested && _client is not null)
        {
            try
            {
                var result = await _client.ReceiveAsync(ct).ConfigureAwait(false);
                DataReceived?.Invoke(result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke(ex.Message);
                // Short pause to avoid tight error loops on transient socket issues
                try { await Task.Delay(50, ct).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
            return ip;

        // Hostname lookup — synchronous is fine here; only runs at Open()
        var entry = Dns.GetHostAddresses(host);
        if (entry.Length == 0)
            throw new InvalidOperationException($"Could not resolve host '{host}'.");
        return entry[0];
    }

    public void Dispose() => Close();
}