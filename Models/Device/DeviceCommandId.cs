namespace ShockUI.Models.Device;

/// <summary>
/// Command byte 2 values for all SIRS protocol messages (§3.3).
/// Command byte 1 is always 0x00.
/// </summary>
public static class DeviceCommandId
{
    // ── Core SIRS commands (existing) ────────────────────────────────
    public const byte GeneralStatus = 0x01;
    public const byte Boresight = 0x02;
    public const byte NirSensorSettings = 0x03;
    public const byte MwirSensorSettings = 0x04;
    public const byte PanTiltMotorControl = 0x05;
    public const byte StabControl = 0x06;
    public const byte VideoSourceSelection = 0x07;
    public const byte NirFovChange = 0x08;
    public const byte MwirFovChange = 0x09;
    public const byte NirFocusChange = 0x0A;
    public const byte MwirFocusChange = 0x0B;
    public const byte MwirImageEnhancement = 0x0C;
    public const byte NirImageEnhancement = 0x0D;
    public const byte LrfRangeMeasurement = 0x0E;
    public const byte LrfStopCmm = 0x0F;
    public const byte LrfMeasurementRange = 0x10;
    public const byte NirBrightnessContrast = 0x11;
    public const byte MwirBrightnessContrast = 0x12;
    public const byte Symbology = 0x13;  // legacy alias — same as Stream1Symbology
    public const byte Stream1Symbology = 0x13;  // §3.3.15  Stream 1 (primary) symbology on/off
    public const byte Stream2Symbology = 0x16;  // §3.3.15  Stream 2 (secondary) symbology on/off
    public const byte NirExposure = 0x17;  // Auto/Manual exposure + gain + value for NIR sensor
    public const byte VisExposure = 0x18;  // Auto/Manual exposure + gain + value for VIS (RGB) sensor
    public const byte Ibit = 0x14;
    public const byte RgbImageEnhancement = 0x15;  // §3.3.10  RGB (visible-spectrum) image enhancement

    // ── Maintenance-only commands ─────────────────────────────────────
    /// <summary>Start NUC (Non-Uniformity Correction) on the RGB sensor.
    /// Payload: [nucType, 0x00, 0x00] — nucType = 1 (1-Point) or 2 (2-Point).
    /// Maintenance-mode only; the card is hidden when maintenance is locked.</summary>
    public const byte RgbStartNuc = 0x19;

    /// <summary>Start NUC on the NIR sensor. Same payload shape as
    /// <see cref="RgbStartNuc"/>; different command ID so the firmware
    /// can route to the right sensor.</summary>
    public const byte NirStartNuc = 0x1A;


    // ── Extended LRF commands (Noptel LRX ICD O50090DE) ──────────────
    // The System Controller forwards these to the LRX module via its
    // native serial protocol. Will require matching firmware on the
    // System Controller MCU. Until then, simulation handlers cover the
    // request/response cycle.
    public const byte LrfStatusQuery = 0x20;  // Noptel 0xC7
    public const byte LrfOpticalCrosstalk = 0x21;  // Noptel 0xDE
    public const byte LrfAlignmentPointer = 0x22;  // Noptel 0xC5
    public const byte LrfBaudRate = 0x23;  // Noptel 0xC8
    public const byte LrfIdentification = 0x24;  // Noptel 0xC0
    public const byte LrfDiagnostics = 0x25;  // Noptel 0xC2
    public const byte LrfResetErrorCounter = 0x26;  // Noptel 0xCB
}