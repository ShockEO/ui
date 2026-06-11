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

    // ── PTSC-native command bytes (frames addressed to dst 0x20) ─────────
    // These are the command identifiers the Pan/Tilt Stab Controller (C2000)
    // expects on its OWN bus. They are a SEPARATE namespace from the EOA
    // commands above: e.g. 0x09 here means "Motor Set to the PTSC", which is
    // unrelated to MwirFovChange (also 0x09) sent to an EOA at dst 0x52/0x53/
    // 0x54. RX responses are therefore disambiguated by SourceId == 0x20, not
    // by command byte alone. Verified against the C2000 reference frames.
    public static class Ptsc
    {
        public const byte GeneralStatusGet = 0x01;   // GET, payload 00 00
        public const byte MotorStatusGet = 0x08;   // GET, payload 00 00
        public const byte StabStatusGet = 0x0A;   // GET, payload 00 00
        public const byte MotorSet = 0x09;   // SET, 20-byte payload
        public const byte StabSet = 0x0B;   // SET, 20-byte payload
        public const byte Ibit = 0x23;   // [mode, 00]; mode 0=Read,1=Full,2=Sensors
    }

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