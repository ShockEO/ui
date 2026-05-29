using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using ShockUI.Services.App;

namespace ShockUI.Views;

/// <summary>
/// Visual Xbox controller. Interactive when <see cref="InteractionEnabled"/>
/// is true: click the on-screen face buttons, bumpers, D-pad, or center
/// buttons to fire <see cref="VirtualButtonPressed"/>. Click and drag the
/// stick wells to drive the sticks; release returns to centre and emits a
/// final zero state.
///
/// Implementation: invisible Button controls are overlaid on each
/// interactive region. Buttons are guaranteed to receive pointer events
/// reliably across Avalonia versions and layout setups, sidestepping the
/// quirks of putting raw pointer handlers on shapes inside a Viewbox.
/// </summary>
public partial class XboxControllerVisual : UserControl
{
    private const double LeftWellCx = 63;
    private const double LeftWellCy = 78;
    private const double RightWellCx = 183;
    private const double RightWellCy = 108;
    private const double WellRadius = 23;

    public static readonly StyledProperty<XboxControllerState?> StateProperty =
        AvaloniaProperty.Register<XboxControllerVisual, XboxControllerState?>(nameof(State));

    public static readonly StyledProperty<string> ConnectionLabelProperty =
        AvaloniaProperty.Register<XboxControllerVisual, string>(nameof(ConnectionLabel), "Not detected");

    public static readonly StyledProperty<bool> InteractionEnabledProperty =
        AvaloniaProperty.Register<XboxControllerVisual, bool>(nameof(InteractionEnabled), true);

    static XboxControllerVisual()
    {
        StateProperty.Changed.AddClassHandler<XboxControllerVisual>((c, _) => c.ApplyState());
        InteractionEnabledProperty.Changed.AddClassHandler<XboxControllerVisual>((c, _) => c.OnInteractionEnabledChanged());
    }

    /// <summary>Re-run ApplyState when InteractionEnabled flips, so the label updates.</summary>
    private void OnInteractionEnabledChanged()
    {
        ApplyState();
        // Dim the whole visual when interaction is off
        Opacity = InteractionEnabled ? 1.0 : 0.5;
    }

    public XboxControllerVisual()
    {
        InitializeComponent();
        AttachHotSpots();
        Debug.WriteLine("[XboxControllerVisual] ctor: hot-spots attached");
    }

    public XboxControllerState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string ConnectionLabel
    {
        get => GetValue(ConnectionLabelProperty);
        set => SetValue(ConnectionLabelProperty, value);
    }

    public bool InteractionEnabled
    {
        get => GetValue(InteractionEnabledProperty);
        set => SetValue(InteractionEnabledProperty, value);
    }

    public event Action<XboxControllerState>? VirtualStateChanged;
    public event Action<XboxButton>? VirtualButtonPressed;

    // ── Drag state ─────────────────────────────────────────────────────
    private enum DragTarget { None, LeftStick, RightStick }
    private DragTarget _dragging = DragTarget.None;
    private double _virtLeftX, _virtLeftY;
    private double _virtRightX, _virtRightY;

    private void AttachHotSpots()
    {
        // Stick wells use pointer-press/move/release for drag tracking
        AttachStickHotSpot("HotLeftStick", isLeft: true);
        AttachStickHotSpot("HotRightStick", isLeft: false);

        // Buttons just fire Click → VirtualButtonPressed
        AttachButtonHotSpot("HotButtonA", XboxButton.A);
        AttachButtonHotSpot("HotButtonB", XboxButton.B);
        AttachButtonHotSpot("HotButtonX", XboxButton.X);
        AttachButtonHotSpot("HotButtonY", XboxButton.Y);
        AttachButtonHotSpot("HotLeftBumper", XboxButton.LeftBumper);
        AttachButtonHotSpot("HotRightBumper", XboxButton.RightBumper);
        AttachButtonHotSpot("HotButtonBack", XboxButton.Back);
        AttachButtonHotSpot("HotButtonStart", XboxButton.Start);
        AttachButtonHotSpot("HotDPadUp", XboxButton.DPadUp);
        AttachButtonHotSpot("HotDPadDown", XboxButton.DPadDown);
        AttachButtonHotSpot("HotDPadLeft", XboxButton.DPadLeft);
        AttachButtonHotSpot("HotDPadRight", XboxButton.DPadRight);
    }

    private void AttachStickHotSpot(string name, bool isLeft)
    {
        // Stick hot-spots are Border controls (NOT Button) so PointerMoved
        // bubbles up reliably while the pointer is down. Button intercepts
        // a lot of pointer routing internally to support its Click semantics.
        var hot = this.FindControl<Border>(name);
        if (hot is null)
        {
            Debug.WriteLine($"[XboxControllerVisual] WARN: {name} not found");
            return;
        }

        hot.PointerPressed += (s, e) =>
        {
            if (!InteractionEnabled) return;
            Debug.WriteLine($"[XboxControllerVisual] Stick pressed: isLeft={isLeft}");
            _dragging = isLeft ? DragTarget.LeftStick : DragTarget.RightStick;
            e.Pointer.Capture(hot);
            UpdateStickFromEvent(e, isLeft);
            e.Handled = true;
        };
        hot.PointerMoved += (s, e) =>
        {
            if (!InteractionEnabled) return;
            if ((isLeft && _dragging != DragTarget.LeftStick) ||
                (!isLeft && _dragging != DragTarget.RightStick)) return;
            UpdateStickFromEvent(e, isLeft);
        };
        hot.PointerReleased += (s, e) =>
        {
            if (!InteractionEnabled) return;
            if ((isLeft && _dragging != DragTarget.LeftStick) ||
                (!isLeft && _dragging != DragTarget.RightStick)) return;
            Debug.WriteLine($"[XboxControllerVisual] Stick released: isLeft={isLeft}");
            e.Pointer.Capture(null);
            SnapStickBackToCenter();
        };
        hot.PointerCaptureLost += (s, e) =>
        {
            if ((isLeft && _dragging != DragTarget.LeftStick) ||
                (!isLeft && _dragging != DragTarget.RightStick)) return;
            SnapStickBackToCenter();
        };
    }

    private void AttachButtonHotSpot(string name, XboxButton btn)
    {
        var hot = this.FindControl<Button>(name);
        if (hot is null)
        {
            Debug.WriteLine($"[XboxControllerVisual] WARN: {name} not found");
            return;
        }
        hot.Click += (s, e) =>
        {
            if (!InteractionEnabled) return;
            Debug.WriteLine($"[XboxControllerVisual] Click: {btn}");
            FlashButton(btn);
            VirtualButtonPressed?.Invoke(btn);
            RaiseVirtualState(transientButton: btn);
        };
    }

    private void UpdateStickFromEvent(PointerEventArgs e, bool isLeft)
    {
        if (this.FindControl<Canvas>("RootCanvas") is not { } canvas) return;
        var p = e.GetPosition(canvas);

        double cx = isLeft ? LeftWellCx : RightWellCx;
        double cy = isLeft ? LeftWellCy : RightWellCy;
        double dx = (p.X - cx) / WellRadius;
        double dy = (p.Y - cy) / WellRadius;

        double mag = Math.Sqrt(dx * dx + dy * dy);
        if (mag > 1.0) { dx /= mag; dy /= mag; }

        if (isLeft) { _virtLeftX = dx; _virtLeftY = -dy; }
        else { _virtRightX = dx; _virtRightY = -dy; }

        ApplyStickOffset(isLeft ? "PartLeftStick" : "PartRightStick", dx, dy);
        RaiseVirtualState();
    }

    private void SnapStickBackToCenter()
    {
        if (_dragging == DragTarget.LeftStick)
        {
            _virtLeftX = _virtLeftY = 0;
            ApplyStickOffset("PartLeftStick", 0, 0);
        }
        else if (_dragging == DragTarget.RightStick)
        {
            _virtRightX = _virtRightY = 0;
            ApplyStickOffset("PartRightStick", 0, 0);
        }
        _dragging = DragTarget.None;
        RaiseVirtualState();
    }

    private void RaiseVirtualState(XboxButton? transientButton = null)
    {
        var s = new XboxControllerState
        {
            IsConnected = true,
            LeftStickX = _virtLeftX,
            LeftStickY = _virtLeftY,
            RightStickX = _virtRightX,
            RightStickY = _virtRightY,
            A = transientButton == XboxButton.A,
            B = transientButton == XboxButton.B,
            X = transientButton == XboxButton.X,
            Y = transientButton == XboxButton.Y,
            LeftBumper = transientButton == XboxButton.LeftBumper,
            RightBumper = transientButton == XboxButton.RightBumper,
            Start = transientButton == XboxButton.Start,
            Back = transientButton == XboxButton.Back,
            DPadUp = transientButton == XboxButton.DPadUp,
            DPadDown = transientButton == XboxButton.DPadDown,
            DPadLeft = transientButton == XboxButton.DPadLeft,
            DPadRight = transientButton == XboxButton.DPadRight,
        };
        VirtualStateChanged?.Invoke(s);
    }

    private void FlashButton(XboxButton btn)
    {
        string? part = btn switch
        {
            XboxButton.A => "PartButtonA",
            XboxButton.B => "PartButtonB",
            XboxButton.X => "PartButtonX",
            XboxButton.Y => "PartButtonY",
            XboxButton.LeftBumper => "PartLeftBumper",
            XboxButton.RightBumper => "PartRightBumper",
            XboxButton.Back => "PartButtonBack",
            XboxButton.Start => "PartButtonStart",
            XboxButton.DPadUp => "PartDPadUp",
            XboxButton.DPadDown => "PartDPadDown",
            XboxButton.DPadLeft => "PartDPadLeft",
            XboxButton.DPadRight => "PartDPadRight",
            _ => null
        };
        if (part is not null) ApplyOpacity(part, 1.0);
    }

    // ── State-driven rendering ─────────────────────────────────────────

    private void ApplyState()
    {
        var s = State;
        if (s is null)
        {
            ConnectionLabel = InteractionEnabled
                ? "Virtual (click & drag)"
                : "Connect the module to enable";
            return;
        }

        if (this.FindControl<Ellipse>("PartConnected") is { } dot)
        {
            dot.Fill = s.IsConnected
                ? new SolidColorBrush(Color.Parse("#22C55E"))
                : new SolidColorBrush(Color.Parse("#475569"));
        }
        ConnectionLabel = s.IsConnected
            ? "Controller connected"
            : (InteractionEnabled ? "Virtual (click & drag)" : "Connect the module to enable");

        if (_dragging != DragTarget.LeftStick)
            ApplyStickOffset("PartLeftStick", s.LeftStickX, -s.LeftStickY);
        if (_dragging != DragTarget.RightStick)
            ApplyStickOffset("PartRightStick", s.RightStickX, -s.RightStickY);

        ApplyOpacity("PartLeftTrigger", 0.2 + 0.8 * s.LeftTrigger);
        ApplyOpacity("PartRightTrigger", 0.2 + 0.8 * s.RightTrigger);
        ApplyOpacity("PartLeftBumper", s.LeftBumper ? 1.0 : 0.4);
        ApplyOpacity("PartRightBumper", s.RightBumper ? 1.0 : 0.4);
        ApplyOpacity("PartButtonA", s.A ? 1.0 : 0.4);
        ApplyOpacity("PartButtonB", s.B ? 1.0 : 0.4);
        ApplyOpacity("PartButtonX", s.X ? 1.0 : 0.4);
        ApplyOpacity("PartButtonY", s.Y ? 1.0 : 0.4);
        ApplyOpacity("PartDPadUp", s.DPadUp ? 1.0 : 0.5);
        ApplyOpacity("PartDPadDown", s.DPadDown ? 1.0 : 0.5);
        ApplyOpacity("PartDPadLeft", s.DPadLeft ? 1.0 : 0.5);
        ApplyOpacity("PartDPadRight", s.DPadRight ? 1.0 : 0.5);
        ApplyOpacity("PartButtonBack", s.Back ? 1.0 : 0.5);
        ApplyOpacity("PartButtonStart", s.Start ? 1.0 : 0.5);
    }

    private void ApplyStickOffset(string partName, double normalizedX, double normalizedY)
    {
        if (this.FindControl<Ellipse>(partName) is not { } stick) return;
        const double maxOffset = 10.0;
        stick.RenderTransform = new TranslateTransform(normalizedX * maxOffset, normalizedY * maxOffset);
    }

    private void ApplyOpacity(string partName, double opacity)
    {
        if (this.FindControl<Control>(partName) is { } el)
            el.Opacity = opacity;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}