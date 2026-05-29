using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShockUI.Services.App;

/// <summary>
/// Snapshot of one polling cycle from the Xbox controller. All analog axes
/// are normalised: sticks to [-1.0, 1.0] (deadzone-corrected), triggers to
/// [0.0, 1.0]. Buttons are simple bools.
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

    // Buttons
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
}

/// <summary>
/// Polls an Xbox controller (XInput user index 0) at 20 Hz and raises
/// <see cref="StateChanged"/> with a <see cref="XboxControllerState"/>
/// snapshot. Also raises one-shot <see cref="ButtonPressed"/> events for
/// edge-triggered actions (e.g. "A pressed" — not "A held").
///
/// Implementation notes:
/// - Windows-only — Vortice.XInput wraps Microsoft XInput, which has no
///   Linux/macOS counterpart. On non-Windows hosts every public entry
///   point short-circuits to a no-op so the rest of the app keeps
///   running normally with no controller support.
/// - Uses Vortice.XInput. Add the NuGet package "Vortice.XInput".
/// </summary>
public sealed class XboxControllerService : IDisposable
{
    /// <summary>
    /// Cached OS check. Computed once at class load so every method
    /// that would otherwise touch Vortice.XInput can fast-exit on
    /// Linux/macOS without ever triggering a P/Invoke and the
    /// DllNotFoundException that would follow.
    /// </summary>
    private static readonly bool IsSupported = OperatingSystem.IsWindows();

    /// <summary>Polling rate in milliseconds (20 Hz default).</summary>
    public int PollIntervalMs { get; set; } = 50;

    /// <summary>Deadzone radius applied to the analog sticks (normalised).</summary>
    public double StickDeadzone { get; set; } = 0.10;

    /// <summary>Threshold below which trigger pull is treated as 0.</summary>
    public double TriggerDeadzone { get; set; } = 0.05;

    /// <summary>Raised on every poll while the controller is connected.</summary>
    public event Action<XboxControllerState>? StateChanged;

    /// <summary>Raised once when a button transitions from up to down.</summary>
    public event Action<XboxButton>? ButtonPressed;

    /// <summary>Raised when the controller is detected (newly plugged in).</summary>
    public event Action? ControllerConnected;

    /// <summary>Raised when the controller is removed (was connected, now not).</summary>
    public event Action? ControllerDisconnected;

    /// <summary>True while a controller is currently being polled.</summary>
    public bool IsConnected { get; private set; }

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private XboxControllerState? _lastState;

    /// <summary>Start polling. Safe to call repeatedly; subsequent calls are no-ops.
    /// On non-Windows hosts this is a no-op and the controller is reported as
    /// permanently disconnected.</summary>
    public void Start()
    {
        if (!IsSupported)
        {
            Debug.WriteLine("[XboxControllerService] XInput unavailable on this OS — controller polling disabled.");
            return;
        }
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>Stop polling and release the polling task.</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _pollTask = null;
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
                    if (state.IsConnected) ControllerConnected?.Invoke();
                    else ControllerDisconnected?.Invoke();
                }

                if (state.IsConnected)
                {
                    // Detect button edges so callers can wire "A pressed" without
                    // re-firing every poll while the button is held.
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

                    StateChanged?.Invoke(state);
                }

                _lastState = state;
            }
            catch
            {
                // Swallow read errors so the loop keeps polling — XInput will
                // simply report not-connected next cycle.
            }

            try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    /// <summary>
    /// Reads the raw controller state via Vortice.XInput. If the package is
    /// not available the method returns a not-connected state so the rest of
    /// the app keeps working.
    /// </summary>
    private XboxControllerState ReadControllerState()
    {
        // Hard short-circuit on non-Windows. We must check this BEFORE
        // any Vortice.XInput type is referenced or the JIT will attempt
        // to resolve the P/Invoke target and fail with
        // DllNotFoundException on Linux/macOS.
        if (!IsSupported)
            return new XboxControllerState { IsConnected = false };

        try
        {
            // Vortice.XInput.XInput.GetState returns (success, state)
            if (!Vortice.XInput.XInput.GetState(0, out var raw))
            {
                if (_lastState is null || _lastState.IsConnected)
                    Debug.WriteLine("[XboxControllerService] XInput.GetState returned false (no controller)");
                return new XboxControllerState { IsConnected = false };
            }
            if (_lastState is null || !_lastState.IsConnected)
                Debug.WriteLine($"[XboxControllerService] XInput controller DETECTED — buttons=0x{(int)raw.Gamepad.Buttons:X4}");

            var g = raw.Gamepad;

            return new XboxControllerState
            {
                IsConnected = true,
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

    private double NormalizeStick(short raw)
    {
        // Raw is short, range -32768..32767. Normalize to -1..1 then deadzone.
        double v = raw / 32767.0;
        if (Math.Abs(v) < StickDeadzone) return 0.0;
        // Rescale so values just outside deadzone start at 0 (clean ramp).
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

/// <summary>Enumeration of the buttons we expose via <see cref="XboxControllerService.ButtonPressed"/>.</summary>
public enum XboxButton
{
    A, B, X, Y,
    LeftBumper, RightBumper,
    Start, Back,
    LeftStickClick, RightStickClick,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}