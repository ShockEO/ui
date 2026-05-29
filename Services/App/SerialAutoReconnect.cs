using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShockUI.Services.App;

/// <summary>
/// Simple auto-reconnect helper used by each module's serial service.
/// Periodically calls <c>tryReconnect</c> in the background while enabled;
/// stops as soon as <c>isConnected</c> returns true.
///
/// Usage inside a service:
///
///   private readonly SerialAutoReconnect _reconnect;
///
///   public MyService()
///   {
///       _reconnect = new SerialAutoReconnect(
///           isConnected: () =&gt; _serialPort.IsOpen,
///           tryReconnect: async ct =&gt; { TryOpen(_lastPortName); await Task.CompletedTask; },
///           onStatus: msg =&gt; PublishLog("RC", msg),
///           intervalMs: 2000);
///   }
///
///   public bool Connect(string port) { ... _reconnect.Arm(port); return ok; }
///   public void Disconnect()          { _reconnect.Disarm(); ... }
///
/// When the DataReceived handler or the port's IsOpen flips to false, call
/// <c>_reconnect.Trigger()</c> to kick off the reconnect loop.
/// </summary>
public sealed class SerialAutoReconnect : IDisposable
{
    private readonly Func<bool> _isConnected;
    private readonly Func<CancellationToken, Task> _tryReconnect;
    private readonly Action<string>? _onStatus;
    private readonly int _intervalMs;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _enabled;

    public SerialAutoReconnect(
        Func<bool> isConnected,
        Func<CancellationToken, Task> tryReconnect,
        Action<string>? onStatus = null,
        int intervalMs = 2000)
    {
        _isConnected = isConnected;
        _tryReconnect = tryReconnect;
        _onStatus = onStatus;
        _intervalMs = intervalMs;
    }

    /// <summary>
    /// True when the reconnect loop is enabled. Flip to false to stop retrying.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Disarm();
        }
    }

    /// <summary>Call after a successful manual connect so later drops will retry automatically.</summary>
    public void Arm()
    {
        // Nothing to start until a disconnect is detected via Trigger().
        // Just records that reconnection is allowed.
        _enabled = true;
    }

    /// <summary>Call on a deliberate user-initiated disconnect to cancel any pending retry loop.</summary>
    public void Disarm()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Call this from an IOException / SerialPort error path to kick off the retry loop.
    /// Safe to call repeatedly — only one loop runs at a time.
    /// </summary>
    public void Trigger()
    {
        if (!_enabled) return;
        if (_cts is not null && !_cts.IsCancellationRequested) return;  // already running

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _onStatus?.Invoke("Connection lost — auto-reconnect armed.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_intervalMs, ct);
                if (ct.IsCancellationRequested) break;

                if (_isConnected())
                {
                    _onStatus?.Invoke("Connection already restored.");
                    return;
                }

                _onStatus?.Invoke("Reconnect attempt...");
                await _tryReconnect(ct);

                if (_isConnected())
                {
                    _onStatus?.Invoke("Reconnected.");
                    return;
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                _onStatus?.Invoke($"Reconnect error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Disarm();
    }
}