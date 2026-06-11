using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShockUI.Services.App;

/// <summary>
/// Snapshot of one polling cycle from a gamepad. All analog axes are
/// normalised: sticks to [-1.0, 1.0] (deadzone-corrected), triggers to
/// [0.0, 1.0]. Buttons are simple bools.
///
/// NOTE: the type is still named <c>XboxControllerState</c> for source
/// compatibility with the view-models and the on-screen controller visual
/// that already consume it. It represents a *generic* gamepad regardless of
/// whether the backend is XInput (Xbox / X-input pads) or DirectInput
/// (PlayStation-style / generic D-input pads).
/// </summary>
public sealed class XboxControllerState
{
    public bool IsConnected { get; init; }

    // Sticks — deadzone-corrected and normalised to [-1.0, 1.0]
    public double LeftStickX { get; init; }
    public double LeftStickY { get; init; }
    public double RightStickX { get; init; }
    public double RightStickY { get; init; }

    // Triggers — normalised to [0.0, 1.0]
    public double LeftTrigger { get; init; }
    public double RightTrigger { get; init; }

    // Buttons (Xbox names kept as the canonical contract; on a PlayStation
    // pad A/B/X/Y map to Cross/Circle/Square/Triangle respectively).
    public bool A { get; init; }
    public bool B { get; init; }
    public bool X { get; init; }
    public bool Y { get; init; }
    public bool LeftBumper { get; init; }
    public bool RightBumper { get; init; }
    public bool Start { get; init; }
    public bool Back { get; init; }
    public bool LeftStickClick { get; init; }
    public bool RightStickClick { get; init; }
    public bool DPadUp { get; init; }
    public bool DPadDown { get; init; }
    public bool DPadLeft { get; init; }
    public bool DPadRight { get; init; }

    /// <summary>Which backend produced this snapshot (diagnostics / UI label).</summary>
    public GamepadBackend Backend { get; init; }
}

public enum GamepadBackend
{
    None,
    XInput,
    DirectInput,
}

/// <summary>
/// Polls a gamepad at ~20 Hz and raises <see cref="StateChanged"/> with a
/// snapshot, plus edge-triggered <see cref="ButtonPressed"/> events.
///
/// Backend strategy (Windows only):
///   1. Try XInput first (Vortice.XInput, user index 0). This covers real
///      Xbox controllers and any pad in "X-input" mode (e.g. a NiTHO ADONIS
///      connected by USB).
///   2. If no XInput device is present, fall back to DirectInput
///      (SharpDX.DirectInput) and poll the first attached game controller.
///      This covers PlayStation-style / generic "D-input" pads — including
///      the NiTHO ADONIS over Bluetooth, which only speaks D-input wirelessly.
///
/// On non-Windows hosts every entry point is a no-op so the rest of the app
/// runs normally with no controller support. (DirectInput is Windows-only;
/// Linux gamepad support would need a separate evdev/SDL backend — not
/// implemented here.)
///
/// Requires NuGet packages: Vortice.XInput, SharpDX, SharpDX.DirectInput.
/// </summary>
public sealed class XboxControllerService : IDisposable
{
    private static readonly bool IsSupported = OperatingSystem.IsWindows();

    /// <summary>Polling rate in milliseconds (20 Hz default).</summary>
    public int PollIntervalMs { get; set; } = 50;

    /// <summary>Deadzone radius applied to the analog sticks (normalised).</summary>
    public double StickDeadzone { get; set; } = 0.10;

    /// <summary>Threshold below which trigger pull is treated as 0.</summary>
    public double TriggerDeadzone { get; set; } = 0.05;

    public event Action<XboxControllerState>? StateChanged;
    public event Action<XboxButton>? ButtonPressed;
    public event Action? ControllerConnected;
    public event Action? ControllerDisconnected;

    public bool IsConnected { get; private set; }

    /// <summary>Which backend is currently supplying input.</summary>
    public GamepadBackend ActiveBackend { get; private set; } = GamepadBackend.None;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private XboxControllerState? _lastState;

    // DirectInput backend — created lazily, only on Windows and only if XInput
    // finds nothing. Kept as object references so this file still *compiles*
    // even before the SharpDX packages are restored (the actual types are
    // resolved inside the DirectInput helper, which is JIT-isolated by the
    // IsSupported guard exactly like the XInput path).
    private DirectInputBackend? _dinput;
    private readonly object _dinputLock = new();

    public void Start()
    {
        if (!IsSupported)
        {
            Debug.WriteLine("[GamepadService] Controller polling unavailable on this OS.");
            return;
        }
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        // Cancel and WAIT for the poll loop to finish before touching the
        // native DirectInput objects. SharpDX COM objects are not thread-safe,
        // and disposing one while the poll thread is mid-Poll()/GetCurrentState()
        // corrupts the native heap (STATUS_HEAP_CORRUPTION, 0xC0000374).
        try { _cts?.Cancel(); } catch { }

        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        _cts = null;
        _pollTask = null;

        // Now that the poll loop has stopped, it is safe to release the device.
        lock (_dinputLock)
        {
            _dinput?.Dispose();
            _dinput = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var state = ReadControllerState();

                if (state.IsConnected != IsConnected)
                {
                    IsConnected = state.IsConnected;
                    ActiveBackend = state.IsConnected ? state.Backend : GamepadBackend.None;
                    if (state.IsConnected) ControllerConnected?.Invoke();
                    else ControllerDisconnected?.Invoke();
                }

                if (state.IsConnected)
                {
                    if (_lastState is not null)
                    {
                        if (state.A && !_lastState.A) ButtonPressed?.Invoke(XboxButton.A);
                        if (state.B && !_lastState.B) ButtonPressed?.Invoke(XboxButton.B);
                        if (state.X && !_lastState.X) ButtonPressed?.Invoke(XboxButton.X);
                        if (state.Y && !_lastState.Y) ButtonPressed?.Invoke(XboxButton.Y);
                        if (state.LeftBumper && !_lastState.LeftBumper) ButtonPressed?.Invoke(XboxButton.LeftBumper);
                        if (state.RightBumper && !_lastState.RightBumper) ButtonPressed?.Invoke(XboxButton.RightBumper);
                        if (state.Start && !_lastState.Start) ButtonPressed?.Invoke(XboxButton.Start);
                        if (state.Back && !_lastState.Back) ButtonPressed?.Invoke(XboxButton.Back);
                        if (state.LeftStickClick && !_lastState.LeftStickClick) ButtonPressed?.Invoke(XboxButton.LeftStickClick);
                        if (state.RightStickClick && !_lastState.RightStickClick) ButtonPressed?.Invoke(XboxButton.RightStickClick);
                        if (state.DPadUp && !_lastState.DPadUp) ButtonPressed?.Invoke(XboxButton.DPadUp);
                        if (state.DPadDown && !_lastState.DPadDown) ButtonPressed?.Invoke(XboxButton.DPadDown);
                        if (state.DPadLeft && !_lastState.DPadLeft) ButtonPressed?.Invoke(XboxButton.DPadLeft);
                        if (state.DPadRight && !_lastState.DPadRight) ButtonPressed?.Invoke(XboxButton.DPadRight);
                    }

                    // Only raise StateChanged when something meaningful changed:
                    // a button edge, or a stick/trigger moving beyond a small
                    // deadband. A still or barely-jittering stick no longer
                    // floods the consumer (which was backing up the TX queue and
                    // spinning the CPU/fans).
                    if (_lastState is null || StateChangedMeaningfully(_lastState, state))
                        StateChanged?.Invoke(state);
                }

                _lastState = state;
            }
            catch
            {
                // Swallow read errors so the loop keeps polling.
            }

            try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    // Deadband for stick/trigger axes: changes smaller than this don't count
    // as "meaningful", so a resting or lightly-jittering stick stops flooding
    // StateChanged (and therefore the TX queue).
    private const double AxisDeadband = 0.02;

    private static bool StateChangedMeaningfully(XboxControllerState a, XboxControllerState b)
    {
        // Any button difference is always meaningful.
        if (a.A != b.A || a.B != b.B || a.X != b.X || a.Y != b.Y ||
            a.LeftBumper != b.LeftBumper || a.RightBumper != b.RightBumper ||
            a.Start != b.Start || a.Back != b.Back ||
            a.LeftStickClick != b.LeftStickClick || a.RightStickClick != b.RightStickClick ||
            a.DPadUp != b.DPadUp || a.DPadDown != b.DPadDown ||
            a.DPadLeft != b.DPadLeft || a.DPadRight != b.DPadRight)
            return true;

        // Axis moved beyond the deadband?
        return Math.Abs(a.LeftStickX - b.LeftStickX) > AxisDeadband
            || Math.Abs(a.LeftStickY - b.LeftStickY) > AxisDeadband
            || Math.Abs(a.RightStickX - b.RightStickX) > AxisDeadband
            || Math.Abs(a.RightStickY - b.RightStickY) > AxisDeadband
            || Math.Abs(a.LeftTrigger - b.LeftTrigger) > AxisDeadband
            || Math.Abs(a.RightTrigger - b.RightTrigger) > AxisDeadband;
    }

    private XboxControllerState ReadControllerState()
    {
        if (!IsSupported)
            return new XboxControllerState { IsConnected = false };

        // 1) XInput first.
        var xi = ReadXInput();
        if (xi.IsConnected)
        {
            // release DInput device if XInput became available
            lock (_dinputLock)
            {
                _dinput?.Dispose();
                _dinput = null;
            }
            return xi;
        }

        // 2) DirectInput fallback.
        return ReadDirectInput();
    }

    // ── XInput backend ───────────────────────────────────────────────
    private XboxControllerState ReadXInput()
    {
        try
        {
            if (!Vortice.XInput.XInput.GetState(0, out var raw))
                return new XboxControllerState { IsConnected = false };

            var g = raw.Gamepad;
            return new XboxControllerState
            {
                IsConnected = true,
                Backend = GamepadBackend.XInput,
                LeftStickX = NormalizeStick(g.LeftThumbX),
                LeftStickY = NormalizeStick(g.LeftThumbY),
                RightStickX = NormalizeStick(g.RightThumbX),
                RightStickY = NormalizeStick(g.RightThumbY),
                LeftTrigger = NormalizeTrigger(g.LeftTrigger),
                RightTrigger = NormalizeTrigger(g.RightTrigger),
                A = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.A),
                B = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.B),
                X = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.X),
                Y = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.Y),
                LeftBumper = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.LeftShoulder),
                RightBumper = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.RightShoulder),
                Start = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.Start),
                Back = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.Back),
                LeftStickClick = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.LeftThumb),
                RightStickClick = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.RightThumb),
                DPadUp = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.DPadUp),
                DPadDown = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.DPadDown),
                DPadLeft = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.DPadLeft),
                DPadRight = HasFlag(g.Buttons, Vortice.XInput.GamepadButtons.DPadRight),
            };
        }
        catch
        {
            return new XboxControllerState { IsConnected = false };
        }
    }

    // ── DirectInput backend ──────────────────────────────────────────
    private XboxControllerState ReadDirectInput()
    {
        // All native DirectInput access is serialized so a concurrent Stop()/
        // Dispose() can never run while a Poll()/GetCurrentState() is in flight.
        lock (_dinputLock)
        {
            try
            {
                _dinput ??= new DirectInputBackend();
                return _dinput.Read(this);
            }
            catch
            {
                _dinput?.Dispose();
                _dinput = null;
                return new XboxControllerState { IsConnected = false };
            }
        }
    }

    // ── Normalisation helpers (shared) ───────────────────────────────
    private double NormalizeStick(short raw)
    {
        double v = raw / 32767.0;
        if (Math.Abs(v) < StickDeadzone) return 0.0;
        double sign = Math.Sign(v);
        return sign * (Math.Abs(v) - StickDeadzone) / (1.0 - StickDeadzone);
    }

    /// <summary>Normalise a DirectInput axis (0..65535, centre 32767) to [-1,1].</summary>
    internal double NormalizeAxisCentered(int raw)
    {
        double v = (raw - 32767.5) / 32767.5;   // → roughly [-1, 1]
        if (v > 1.0) v = 1.0; else if (v < -1.0) v = -1.0;
        if (Math.Abs(v) < StickDeadzone) return 0.0;
        double sign = Math.Sign(v);
        return sign * (Math.Abs(v) - StickDeadzone) / (1.0 - StickDeadzone);
    }

    private double NormalizeTrigger(byte raw)
    {
        double v = raw / 255.0;
        return v < TriggerDeadzone ? 0.0 : (v - TriggerDeadzone) / (1.0 - TriggerDeadzone);
    }

    private static bool HasFlag(Vortice.XInput.GamepadButtons mask, Vortice.XInput.GamepadButtons flag)
        => (mask & flag) == flag;

    public void Dispose() => Stop();
}

/// <summary>Buttons exposed via <see cref="XboxControllerService.ButtonPressed"/>.</summary>
public enum XboxButton
{
    A, B, X, Y,
    LeftBumper, RightBumper,
    Start, Back,
    LeftStickClick, RightStickClick,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}