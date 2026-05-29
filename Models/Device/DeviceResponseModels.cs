using System.Collections.Generic;

namespace ShockUI.Models.Device;

// ============================================================================
// §3.3.1  General Status Response  (Length = 0x1D = 29 payload bytes)
// ============================================================================
public sealed class DeviceGeneralStatusResponse
{
    // --- Byte 9: General Status ---
    public bool PowerFailure { get; init; }  // bit 0
    public bool FibreError { get; init; }  // bit 1
    public bool ImuError { get; init; }  // bit 2

    // --- Byte 10: Laser Status ---
    public bool LrfNotSafe { get; init; }  // bit 0
    public bool LpiNotSafe { get; init; }  // bit 1

    // --- Byte 11: System Operation State (bits 0:2) ---
    public byte SystemState { get; init; }
    public string SystemStateText => SystemState switch
    {
        0x00 => "Operational (C)",
        0x01 => "Maintenance (D)",
        0x02 => "Built-In-Test (E)",
        0x03 => "Error (F)",
        0x04 => "Initialization (B)",
        0x05 => "Boresight (G)",
        _ => $"Unknown (0x{SystemState:X2})"
    };

    // --- Bytes 12-15: Operational Time (uint32 LE, hours) ---
    public uint OperationalHours { get; init; }

    // --- Byte 16: Humidity/Pressure/Temp Status ---
    public bool HumidityTriggered { get; init; }  // bit 0
    public bool PressureTriggered { get; init; }  // bit 1
    public bool TemperatureOutOfRange { get; init; }  // bit 2

    // --- Bytes 17-18: Humidity % (uint16 LE) ---
    public ushort HumidityPercent { get; init; }

    // --- Bytes 19-22: Pressure psi (uint32 LE) ---
    public uint PressurePsi { get; init; }

    // --- Bytes 23-24: Temperature °C (int16 LE) ---
    public short TemperatureCelsius { get; init; }

    // --- Byte 25: MWIR NUC Status (bits 0:2) ---
    public byte MwirNucMode { get; init; }
    public string MwirNucText => MwirNucMode switch
    {
        0 => "1 Point",
        1 => "2 Point",
        2 => "3 Point",
        3 => "Factory Map",
        _ => "Unknown"
    };

    // --- Byte 26: MWIR Sub-assembly Calibration ---
    public bool MwirCoolerBusy { get; init; }  // bit 0
    public bool MwirDetectorBusy { get; init; }  // bit 1
    public bool MwirZg1CalBusy { get; init; }  // bit 2
    public bool MwirZg2CalBusy { get; init; }  // bit 3
    public bool MwirMotorFault { get; init; }  // bit 4

    // --- Byte 27: NIR Sub-assembly Calibration ---
    public bool NirSensorBusy { get; init; }   // bit 0
    public bool NirZg1CalBusy { get; init; }   // bit 1
    public bool NirZg2CalBusy { get; init; }   // bit 2
    public bool NirMotorFault { get; init; }   // bit 3

    // --- Byte 28: Pan Motor ---
    public bool PanCalBusy { get; init; }    // bit 0
    public bool PanMotorFault { get; init; }    // bit 1

    // --- Byte 29: Tilt Motor ---
    public bool TiltCalBusy { get; init; }   // bit 0
    public bool TiltMotorFault { get; init; }   // bit 1

    // --- Bytes 30-31: Last Error (uint16) ---
    public ushort LastError { get; init; }

    // --- Byte 32: LRF Status / Byte 33: LPI Status ---
    public bool LrfFault { get; init; }         // bit 0
    public bool LpiFault { get; init; }         // bit 0

    public static DeviceGeneralStatusResponse? Parse(byte[] p)
    {
        if (p.Length < 29) return null;
        return new DeviceGeneralStatusResponse
        {
            PowerFailure = (p[0] & 0x01) != 0,
            FibreError = (p[0] & 0x02) != 0,
            ImuError = (p[0] & 0x04) != 0,
            LrfNotSafe = (p[1] & 0x01) != 0,
            LpiNotSafe = (p[1] & 0x02) != 0,
            SystemState = (byte)(p[2] & 0x07),
            OperationalHours = (uint)(p[3] | (p[4] << 8) | (p[5] << 16) | (p[6] << 24)),
            HumidityTriggered = (p[7] & 0x01) != 0,
            PressureTriggered = (p[7] & 0x02) != 0,
            TemperatureOutOfRange = (p[7] & 0x04) != 0,
            HumidityPercent = (ushort)(p[8] | (p[9] << 8)),
            PressurePsi = (uint)(p[10] | (p[11] << 8) | (p[12] << 16) | (p[13] << 24)),
            TemperatureCelsius = (short)(p[14] | (p[15] << 8)),
            MwirNucMode = (byte)(p[16] & 0x07),
            MwirCoolerBusy = (p[17] & 0x01) != 0,
            MwirDetectorBusy = (p[17] & 0x02) != 0,
            MwirZg1CalBusy = (p[17] & 0x04) != 0,
            MwirZg2CalBusy = (p[17] & 0x08) != 0,
            MwirMotorFault = (p[17] & 0x10) != 0,
            NirSensorBusy = (p[18] & 0x01) != 0,
            NirZg1CalBusy = (p[18] & 0x02) != 0,
            NirZg2CalBusy = (p[18] & 0x04) != 0,
            NirMotorFault = (p[18] & 0x08) != 0,
            PanCalBusy = (p[19] & 0x01) != 0,
            PanMotorFault = (p[19] & 0x02) != 0,
            TiltCalBusy = (p[20] & 0x01) != 0,
            TiltMotorFault = (p[20] & 0x02) != 0,
            LastError = (ushort)(p[21] | (p[22] << 8)),
            LrfFault = (p[23] & 0x01) != 0,
            LpiFault = (p[24] & 0x01) != 0,
        };
    }
}

// ============================================================================
// §3.3.5  Pan and Tilt Response  (Length = 0x10 = 16 bytes)
// ============================================================================
public sealed class DevicePanTiltResponse
{
    public bool PanSpeedMode { get; init; }   // byte 9 bit 0
    public bool TiltSpeedMode { get; init; }   // byte 10 bit 0
    public float PanAngleDeg { get; init; }   // bytes 11-14, int32 LE, value/1000
    public float TiltAngleDeg { get; init; }   // bytes 15-18
    public ushort PanSpeed { get; init; }   // bytes 19-20
    public ushort TiltSpeed { get; init; }   // bytes 21-22

    public static DevicePanTiltResponse? Parse(byte[] p)
    {
        if (p.Length < 14) return null;
        int panRaw = p[2] | (p[3] << 8) | (p[4] << 16) | (p[5] << 24);
        int tiltRaw = p[6] | (p[7] << 8) | (p[8] << 16) | (p[9] << 24);
        return new DevicePanTiltResponse
        {
            PanSpeedMode = (p[0] & 0x01) != 0,
            TiltSpeedMode = (p[1] & 0x01) != 0,
            PanAngleDeg = panRaw / 1000.0f,
            TiltAngleDeg = tiltRaw / 1000.0f,
            PanSpeed = (ushort)(p[10] | (p[11] << 8)),
            TiltSpeed = (ushort)(p[12] | (p[13] << 8)),
        };
    }
}

// ============================================================================
// §3.3.8  FOV Change Response  (shared NIR 0x08 / MWIR 0x09, Length = 0x04)
// ============================================================================
public sealed class DeviceFovResponse
{
    public byte FovValue { get; init; }   // byte 9 bits 0:2
    public bool FovReached { get; init; }   // byte 10 bit 0

    public string FovName => FovValue switch
    {
        1 => "WFOV",
        2 => "MFOV",
        3 => "NFOV",
        4 => "VNFOV",
        _ => "Unknown"
    };

    public static DeviceFovResponse? Parse(byte[] p)
    {
        if (p.Length < 2) return null;
        return new DeviceFovResponse
        {
            FovValue = (byte)(p[0] & 0x07),
            FovReached = (p[1] & 0x01) != 0,
        };
    }
}

// ============================================================================
// §3.3.9  Focus Response  (shared NIR 0x0A / MWIR 0x0B, Length = 0x09)
// ============================================================================
public sealed class DeviceFocusResponse
{
    // ── Byte 0: Focus Control Byte (mode echo) ──────────────────────────
    // bits 0:2 hold the focus mode. Bits 3:7 reserved.
    //   0 = Giving feedback (Get)        1 = Setting focus position
    //   2 = Setting focus speed          3 = Moving to infinite focus
    //   4 = Stopped control
    public byte FocusMode { get; init; }

    // ── Byte 1: Focus Status Byte ───────────────────────────────────────
    /// <summary>Bit 0 — 1 = control loop active, 0 = inactive.</summary>
    public bool ControlActive { get; init; }

    /// <summary>Bit 1 — 1 = commanded focus position reached.</summary>
    public bool PosReached { get; init; }

    /// <summary>Bits 2:3 — movement freedom.
    /// 0=Free, 1=Min reached, 2=Max reached, 3=Blocked by other lens.</summary>
    public byte MovementFreedom { get; init; }

    // ── Bytes 2..5: Position (int32 LE), Bytes 6..9: Speed (int32 LE) ──
    public int Position { get; init; }
    public int Speed { get; init; }

    public string ModeText => FocusMode switch
    {
        0 => "Get",
        1 => "Set Position",
        2 => "Set Speed",
        3 => "Infinity",
        4 => "Stop",
        _ => "Unknown"
    };

    public string MovementText => MovementFreedom switch
    {
        0 => "Free",
        1 => "Min Reached",
        2 => "Max Reached",
        3 => "Blocked",
        _ => "—"
    };

    /// <summary>One-line status summary: "Active | Reached | Free" etc.</summary>
    public string StatusText =>
        $"{(ControlActive ? "Active" : "Idle")} | " +
        $"{(PosReached ? "Reached" : "Moving")} | {MovementText}";

    // Kept for source-compat with older callers that referenced EndstopText.
    public string EndstopText => MovementText;

    public static DeviceFocusResponse? Parse(byte[] p)
    {
        // New (Focus_Commands_Updated.txt) — 10-byte payload:
        //   [0] Focus Control Byte (mode, bits 0:2)
        //   [1] Focus Status Byte (active=bit0, reached=bit1, freedom=bits2:3)
        //   [2..5] Position (int32 LE)
        //   [6..9] Speed    (int32 LE)
        if (p.Length < 10) return null;

        byte ctl = p[0];
        byte st = p[1];
        return new DeviceFocusResponse
        {
            FocusMode = (byte)(ctl & 0x07),
            ControlActive = (st & 0x01) != 0,
            PosReached = (st & 0x02) != 0,
            MovementFreedom = (byte)((st >> 2) & 0x03),
            Position = p[2] | (p[3] << 8) | (p[4] << 16) | (p[5] << 24),
            Speed = p[6] | (p[7] << 8) | (p[8] << 16) | (p[9] << 24),
        };
    }
}

// ============================================================================
// §3.3.11  LRF Range Measurement Response  (Length = 0x15 = 21 bytes)
// ============================================================================
public sealed class DeviceLrfResponse
{
    public byte MeasurementMode { get; init; }
    public uint Range1Meters { get; init; }
    public ushort Signal1Ms { get; init; }
    public uint Range2Meters { get; init; }
    public ushort Signal2Ms { get; init; }
    public uint Range3Meters { get; init; }
    public ushort Signal3Ms { get; init; }

    public static DeviceLrfResponse? Parse(byte[] p)
    {
        if (p.Length < 19) return null;
        return new DeviceLrfResponse
        {
            MeasurementMode = (byte)(p[0] & 0x3F),
            Range1Meters = (uint)(p[1] | (p[2] << 8) | (p[3] << 16) | (p[4] << 24)),
            Signal1Ms = (ushort)(p[5] | (p[6] << 8)),
            Range2Meters = (uint)(p[7] | (p[8] << 8) | (p[9] << 16) | (p[10] << 24)),
            Signal2Ms = (ushort)(p[11] | (p[12] << 8)),
            Range3Meters = (uint)(p[13] | (p[14] << 8) | (p[15] << 16) | (p[16] << 24)),
            Signal3Ms = (ushort)(p[17] | (p[18] << 8)),
        };
    }
}

// ============================================================================
// §3.3.16  IBIT Response  (Length = 0x1E = 30 payload bytes)
// ============================================================================
public sealed class DeviceIbitResponse
{
    public bool PowerFailure { get; init; }
    public bool FibreError { get; init; }
    public bool ImuError { get; init; }
    public bool MwirCoolerDone { get; init; }
    public byte MwirZg1Bits { get; init; }
    public byte MwirZg2Bits { get; init; }
    public byte MwirSolBits { get; init; }
    public byte MwirTecBits { get; init; }
    public byte NirZg1Bits { get; init; }
    public byte NirZg2Bits { get; init; }
    public byte PanMotorBits { get; init; }
    public byte TiltMotorBits { get; init; }
    public byte LrfStatus1 { get; init; }
    public byte LrfStatus2 { get; init; }
    public byte LrfStatus3 { get; init; }
    public bool HumidityOutOfRange { get; init; }
    public ushort HumidityOorPercent { get; init; }
    public bool PressureOutOfRange { get; init; }
    public uint PressureOorPsi { get; init; }
    public bool TempOutOfRange { get; init; }
    public short TempOorCelsius { get; init; }

    public static DeviceIbitResponse? Parse(byte[] p)
    {
        if (p.Length < 24) return null;
        return new DeviceIbitResponse
        {
            PowerFailure = (p[0] & 0x01) != 0,
            FibreError = (p[0] & 0x02) != 0,
            ImuError = (p[0] & 0x04) != 0,
            MwirCoolerDone = (p[1] & 0x01) != 0,
            MwirZg1Bits = p[2],
            MwirZg2Bits = p[3],
            MwirSolBits = p[4],
            MwirTecBits = p[5],
            NirZg1Bits = p[6],
            NirZg2Bits = p[7],
            PanMotorBits = p[8],
            TiltMotorBits = p[9],
            LrfStatus1 = p[10],
            LrfStatus2 = p[11],
            LrfStatus3 = p[12],
            HumidityOutOfRange = (p[13] & 0x01) != 0,
            HumidityOorPercent = (ushort)(p[14] | (p[15] << 8)),
            PressureOutOfRange = (p.Length > 16 && (p[16] & 0x01) != 0),
            PressureOorPsi = p.Length > 20 ? (uint)(p[17] | (p[18] << 8) | (p[19] << 16) | (p[20] << 24)) : 0,
            TempOutOfRange = p.Length > 21 && (p[21] & 0x01) != 0,
            TempOorCelsius = p.Length > 23 ? (short)(p[22] | (p[23] << 8)) : (short)0,
        };
    }

    private static readonly string[] Zg7Labels =
        ["Motor Start", "Motor Connection", "Motor Polarity", "Min EndStop", "Max EndStop", "Motor Stall", "Quadrature Encoder"];
    private static readonly string[] Pan5Labels =
        ["Motor Start", "Motor Connection", "Motor Polarity", "Motor Stall", "Encoder"];
    private static readonly string[] Tilt7Labels =
        ["Motor Start", "Motor Connection", "Motor Polarity", "Min EndStop", "Max EndStop", "Motor Stall", "Encoder"];
    private static readonly string[] Sol4Labels =
        ["Solenoid Start", "Solenoid Polarity", "Min EndStop", "Max EndStop"];
    private static readonly string[] Tec5Labels =
        ["TEC Electrical", "TEC Connection", "TEC Polarity", "TEC Range Min", "TEC Range Max"];

    public string MwirZg1Summary => DecodeBits(MwirZg1Bits, Zg7Labels);
    public string MwirZg2Summary => DecodeBits(MwirZg2Bits, Zg7Labels);
    public string MwirSolSummary => DecodeBits(MwirSolBits, Sol4Labels);
    public string MwirTecSummary => DecodeBits(MwirTecBits, Tec5Labels);
    public string NirZg1Summary => DecodeBits(NirZg1Bits, Zg7Labels);
    public string NirZg2Summary => DecodeBits(NirZg2Bits, Zg7Labels);
    public string PanSummary => DecodeBits(PanMotorBits, Pan5Labels);
    public string TiltSummary => DecodeBits(TiltMotorBits, Tilt7Labels);

    private static string DecodeBits(byte bits, string[] labels)
    {
        if (bits == 0) return "Pass";
        var fails = new List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) != 0) fails.Add(labels[i] + " FAIL");
        return string.Join(" | ", fails);
    }
}

// ============================================================================
// §3.3.6  Stab Control Response
// ============================================================================
public sealed class DeviceStabResponse
{
    public bool StabActive { get; init; }   // byte 9 bit 0: 0=Active, 1=Inactive

    public static DeviceStabResponse? Parse(byte[] p)
        => p.Length < 1 ? null : new DeviceStabResponse { StabActive = (p[0] & 0x01) == 0 };
}

// ============================================================================
// §3.3.7  Video Source Selection Response
// ============================================================================
public sealed class DeviceVideoSourceResponse
{
    public bool Sdi1IsNir { get; init; }   // bit 0: 0=MWIR, 1=NIR
    public bool Sdi2IsNir { get; init; }   // bit 1

    public static DeviceVideoSourceResponse? Parse(byte[] p)
        => p.Length < 1 ? null : new DeviceVideoSourceResponse
        {
            Sdi1IsNir = (p[0] & 0x01) != 0,
            Sdi2IsNir = (p[0] & 0x02) != 0,
        };
}

// ============================================================================
// §3.3.14  Brightness & Contrast Response  (shared NIR / MWIR)
// ============================================================================
public sealed class DeviceBrightnessContrastResponse
{
    public ushort Brightness { get; init; }
    public ushort Contrast { get; init; }

    public static DeviceBrightnessContrastResponse? Parse(byte[] p)
    {
        if (p.Length < 5) return null;
        return new DeviceBrightnessContrastResponse
        {
            Brightness = (ushort)(p[1] | (p[2] << 8)),
            Contrast = (ushort)(p[3] | (p[4] << 8)),
        };
    }
}