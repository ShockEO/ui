using System;
using System.Diagnostics;
using System.Linq;

namespace ShockUI.Services.App;

/// <summary>
/// DirectInput (SharpDX) backend for generic / PlayStation-style "D-input"
/// gamepads such as the NiTHO ADONIS over Bluetooth.
///
/// IMPORTANT — mapping is device-specific. DirectInput does NOT impose a
/// standard button/axis layout the way XInput does, so the indices below are
/// the *common DualShock-style* layout and may need tweaking for a particular
/// pad. To recalibrate:
///   1. Set the environment variable SHOCKUI_GAMEPAD_DEBUG=1 (or attach a
///      debugger) and watch the Debug output — every poll prints the raw axis
///      values and the indices of any pressed buttons / POV angle.
///   2. Press each control in turn, note the index/axis that changes, and
///      adjust the constants in <see cref="Map"/> accordingly.
///
/// This file references SharpDX.DirectInput types directly. It is only ever
/// instantiated on Windows (guarded by the caller's OS check), and only when
/// XInput finds no device, so the SharpDX P/Invokes are never touched on
/// other platforms.
///
/// Requires NuGet: SharpDX (4.2.0), SharpDX.DirectInput (4.2.0).
/// </summary>
internal sealed class DirectInputBackend : IDisposable
{
    private static readonly bool DebugDump =
        Environment.GetEnvironmentVariable("SHOCKUI_GAMEPAD_DEBUG") == "1";

    private SharpDX.DirectInput.DirectInput? _di;
    private SharpDX.DirectInput.Joystick? _joystick;
    private Guid _deviceGuid = Guid.Empty;

    // Transient Poll() failures (sleep/focus loss) are retried via Acquire();
    // only after this many in a row do we drop and re-enumerate the device.
    private int _consecutiveErrors;
    private const int MaxConsecutiveErrors = 20;

    // ── DualShock-style default mapping ───────────────────────────────
    // Button indices (0-based) for a typical PS-style DirectInput pad.
    // Order on many clones is: 0 Square, 1 Cross, 2 Circle, 3 Triangle,
    // 4 L1, 5 R1, 6 L2(digital), 7 R2(digital), 8 Share/Back, 9 Options/Start,
    // 10 L3, 11 R3, 12 PS/Guide, 13 Touchpad.
    private const int BtnSquare = 0;   // → X
    private const int BtnCross = 1;    // → A
    private const int BtnCircle = 2;   // → B
    private const int BtnTriangle = 3; // → Y
    private const int BtnL1 = 4;       // → LeftBumper
    private const int BtnR1 = 5;       // → RightBumper
    private const int BtnL2 = 6;       // digital fallback for LeftTrigger
    private const int BtnR2 = 7;       // digital fallback for RightTrigger
    private const int BtnShare = 8;    // → Back
    private const int BtnOptions = 9;  // → Start
    private const int BtnL3 = 10;      // → LeftStickClick
    private const int BtnR3 = 11;      // → RightStickClick

    public XboxControllerState Read(XboxControllerService owner)
    {
        EnsureAcquired();

        if (_joystick is null)
            return new XboxControllerState { IsConnected = false };

        SharpDX.DirectInput.JoystickState js;
        try
        {
            _joystick.Poll();
            js = _joystick.GetCurrentState();
        }
        catch (SharpDX.SharpDXException ex)
        {
            // InputLost (0x8007001E) / NotAcquired (0x8007000C) happen when a
            // Bluetooth pad sleeps or the foreground window changes. Try to
            // re-acquire the SAME device in place rather than disposing and
            // recreating the native COM object every cycle — rapid dispose/
            // recreate under the BT stack is what triggers heap corruption.
            _consecutiveErrors++;
            try { _joystick.Acquire(); } catch { }

            // Only after repeated failures do we fully drop the device so the
            // next cycle re-enumerates (e.g. controller actually unplugged).
            if (_consecutiveErrors >= MaxConsecutiveErrors)
            {
                Debug.WriteLine($"[DInput] Dropping device after {_consecutiveErrors} errors ({ex.ResultCode}).");
                DisposeDevice();
            }
            return new XboxControllerState { IsConnected = false };
        }
        catch
        {
            _consecutiveErrors++;
            if (_consecutiveErrors >= MaxConsecutiveErrors)
                DisposeDevice();
            return new XboxControllerState { IsConnected = false };
        }

        _consecutiveErrors = 0;

        if (js is null)
            return new XboxControllerState { IsConnected = false };

        return Map(owner, js);
    }

    private XboxControllerState Map(XboxControllerService owner, SharpDX.DirectInput.JoystickState js)
    {
        bool[] btn = js.Buttons ?? Array.Empty<bool>();
        bool B(int i) => i >= 0 && i < btn.Length && btn[i];

        // Axes: DirectInput reports 0..65535, centre ~32767.
        // Left stick → X / Y ; Right stick → Z / RotationZ (DualShock norm).
        double lx = owner.NormalizeAxisCentered(js.X);
        double ly = -owner.NormalizeAxisCentered(js.Y);   // invert: up = +
        double rx = owner.NormalizeAxisCentered(js.Z);
        double ry = -owner.NormalizeAxisCentered(js.RotationZ);

        // Triggers: prefer analog (RotationX/RotationY on many PS clones map
        // L2/R2). If those read flat, fall back to the digital L2/R2 buttons.
        double lt = AnalogTrigger(js.RotationX);
        double rt = AnalogTrigger(js.RotationY);
        if (lt <= 0.0 && B(BtnL2)) lt = 1.0;
        if (rt <= 0.0 && B(BtnR2)) rt = 1.0;

        // D-pad: POV hat in centidegrees (0=N,9000=E,18000=S,27000=W), -1 = centre.
        int pov = (js.PointOfViewControllers is { Length: > 0 }) ? js.PointOfViewControllers[0] : -1;
        bool up = pov is 0 or 4500 or 31500;
        bool right = pov is 4500 or 9000 or 13500;
        bool down = pov is 13500 or 18000 or 22500;
        bool left = pov is 22500 or 27000 or 31500;

        if (DebugDump)
        {
            var pressed = string.Join(",", Enumerable.Range(0, btn.Length).Where(i => btn[i]));
            Debug.WriteLine($"[DInput] X={js.X} Y={js.Y} Z={js.Z} Rx={js.RotationX} Ry={js.RotationY} Rz={js.RotationZ} POV={pov} btns=[{pressed}]");
        }

        return new XboxControllerState
        {
            IsConnected = true,
            Backend = GamepadBackend.DirectInput,
            LeftStickX = lx,
            LeftStickY = ly,
            RightStickX = rx,
            RightStickY = ry,
            LeftTrigger = lt,
            RightTrigger = rt,
            A = B(BtnCross),
            B = B(BtnCircle),
            X = B(BtnSquare),
            Y = B(BtnTriangle),
            LeftBumper = B(BtnL1),
            RightBumper = B(BtnR1),
            Start = B(BtnOptions),
            Back = B(BtnShare),
            LeftStickClick = B(BtnL3),
            RightStickClick = B(BtnR3),
            DPadUp = up,
            DPadDown = down,
            DPadLeft = left,
            DPadRight = right,
        };
    }

    private static double AnalogTrigger(int raw)
    {
        // Idle for an unused axis is usually centre (32767) on DualShock clones
        // where the trigger axis rests at centre and only moves one way, or 0
        // if it rests low. Treat anything below ~40% of centre as "released".
        double v = raw / 65535.0;            // 0..1
        return v < 0.05 ? 0.0 : v;
    }

    private void EnsureAcquired()
    {
        if (_joystick is not null) return;

        _di ??= new SharpDX.DirectInput.DirectInput();

        // Find the first attached game controller. Enumerate by GameControl
        // class first; if nothing comes back, fall back to explicit Gamepad
        // then Joystick device types (some pads report only one of these).
        SharpDX.DirectInput.DeviceInstance? dev =
            FirstAttached(SharpDX.DirectInput.DeviceClass.GameControl)
            ?? FirstAttachedOfType(SharpDX.DirectInput.DeviceType.Gamepad)
            ?? FirstAttachedOfType(SharpDX.DirectInput.DeviceType.Joystick);

        if (dev is null)
            return;

        _deviceGuid = dev.InstanceGuid;
        _joystick = new SharpDX.DirectInput.Joystick(_di, _deviceGuid);

        // Set a generous buffer and acquire.
        try { _joystick.Properties.BufferSize = 128; } catch { }
        _joystick.Acquire();

        Debug.WriteLine($"[DInput] Acquired '{dev.InstanceName}' ({dev.ProductName}).");
    }

    private SharpDX.DirectInput.DeviceInstance? FirstAttached(SharpDX.DirectInput.DeviceClass cls)
    {
        try
        {
            return _di!.GetDevices(cls, SharpDX.DirectInput.DeviceEnumerationFlags.AttachedOnly)
                       .FirstOrDefault();
        }
        catch { return null; }
    }

    private SharpDX.DirectInput.DeviceInstance? FirstAttachedOfType(SharpDX.DirectInput.DeviceType type)
    {
        try
        {
            return _di!.GetDevices(type, SharpDX.DirectInput.DeviceEnumerationFlags.AttachedOnly)
                       .FirstOrDefault();
        }
        catch { return null; }
    }

    private void DisposeDevice()
    {
        try { _joystick?.Unacquire(); } catch { }
        try { _joystick?.Dispose(); } catch { }
        _joystick = null;
        _deviceGuid = Guid.Empty;
    }

    public void Dispose()
    {
        DisposeDevice();
        try { _di?.Dispose(); } catch { }
        _di = null;
    }
}