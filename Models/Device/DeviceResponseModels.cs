using System;
using System.Collections.Generic;

namespace ShockUI.Models.Device;

// ============================================================================
// §3.3.1  General Status Response  (Length = 0x1D = 29 payload bytes)
// ============================================================================
public sealed class DeviceGeneralStatusResponse
{
    // §3.3.1 General Status, 10-byte payload (frame bytes 11-20; CRC 21-22).
    // Per SRS v2 Table 11 (payload index = frame byte - 11):
    //   p[0] System State (bits0-2) + init/IMU/encoder/power health bits
    //   p[1] Pan/Tilt ADC + Motor Brake
    //   p[2] Pan motor status & calibration
    //   p[3] Tilt motor status & calibration
    //   p[4] Control Mode (Pan bits0-1, Tilt bits2-3)
    //   p[5..6] Bus Voltage (uint16 LE, raw ADC counts)
    //   p[7..9] Reserve

    // p[0]
    public byte SystemState { get; init; }       // bits 0-2
    public bool SystemReady { get; init; }        // bit 3 (1 = initialised/ready)
    public bool ImuError { get; init; }           // bit 4 (1 = error)
    public bool PanEncoderError { get; init; }    // bit 5
    public bool TiltEncoderError { get; init; }   // bit 6
    public bool PowerFailure { get; init; }       // bit 7

    // p[1]
    public bool PanAdcError { get; init; }        // bit 0
    public bool TiltAdcError { get; init; }       // bit 1
    public bool MotorBrakeOn { get; init; }       // bit 2

    // p[2] Pan motor
    public bool PanMotorDisengaged { get; init; } // bit 0 (1 = deactivated)
    public bool PanCalBusy { get; init; }         // bit 1 (1 = busy)
    public bool PanMotorFault { get; init; }      // bit 2

    // p[3] Tilt motor
    public bool TiltMotorDisengaged { get; init; }
    public bool TiltCalBusy { get; init; }
    public bool TiltMotorFault { get; init; }

    // p[4] Control mode
    public byte PanControlMode { get; init; }     // bits 0-1
    public byte TiltControlMode { get; init; }    // bits 2-3

    // p[5..6] Bus voltage (raw uint16 ADC counts on Pan ADC CH3)
    public ushort BusVoltageRaw { get; init; }

    public string SystemStateText => SystemState switch
    {
        0x00 => "Operational (C)",
        0x01 => "Maintenance (D)",
        0x02 => "Built-In-Test (E)",
        0x03 => "Error (F)",
        0x04 => "Initialization (B)",
        _ => $"Unknown (0x{SystemState:X2})"
    };

    public static string ModeName(byte m) => m switch
    {
        0 => "Safe / Damping",
        1 => "Rate / Velocity",
        2 => "Position",
        3 => "Stabilised",
        _ => "?"
    };

    public string PanModeText => ModeName(PanControlMode);
    public string TiltModeText => ModeName(TiltControlMode);

    public static DeviceGeneralStatusResponse? Parse(byte[] p)
    {
        if (p.Length < 7) return null;   // need through the bus-voltage bytes
        return new DeviceGeneralStatusResponse
        {
            SystemState = (byte)(p[0] & 0x07),
            SystemReady = (p[0] & 0x08) != 0,
            ImuError = (p[0] & 0x10) != 0,
            PanEncoderError = (p[0] & 0x20) != 0,
            TiltEncoderError = (p[0] & 0x40) != 0,
            PowerFailure = (p[0] & 0x80) != 0,

            PanAdcError = (p[1] & 0x01) != 0,
            TiltAdcError = (p[1] & 0x02) != 0,
            MotorBrakeOn = (p[1] & 0x04) != 0,

            PanMotorDisengaged = (p[2] & 0x01) != 0,
            PanCalBusy = (p[2] & 0x02) != 0,
            PanMotorFault = (p[2] & 0x04) != 0,

            TiltMotorDisengaged = (p[3] & 0x01) != 0,
            TiltCalBusy = (p[3] & 0x02) != 0,
            TiltMotorFault = (p[3] & 0x04) != 0,

            PanControlMode = (byte)(p[4] & 0x03),
            TiltControlMode = (byte)((p[4] >> 2) & 0x03),

            BusVoltageRaw = (ushort)(p[5] | (p[6] << 8)),
        };
    }
}

// ============================================================================
// §3.3.5  Pan and Tilt Response  (Length = 0x10 = 16 bytes)
// ============================================================================
public sealed class DevicePanTiltResponse
{
    // §3.3.5 Motor Status (cmd 0x09 response), SRS v2 Table 19:
    //   p[0]      Pan control status  (bit0 active, bits1:2 mode)
    //   p[1]      Tilt control status
    //   p[2..5]   Pan angular position  int32 LE (encoder ticks)
    //   p[6..9]   Tilt angular position int32 LE (ticks)
    //   p[10..13] Pan speed   int32 LE (ticks/second)
    //   p[14..17] Tilt speed  int32 LE (ticks/second)
    //
    // 21-bit absolute encoder: 1 tick = 0.00017166° (= 360 / 2^21).
    private const double DegPerTick = 0.00017166;

    public byte PanMode { get; init; }
    public byte TiltMode { get; init; }
    public bool PanActive { get; init; }
    public bool TiltActive { get; init; }
    public float PanAngleDeg { get; init; }    // converted from ticks
    public float TiltAngleDeg { get; init; }
    public float PanSpeed { get; init; }        // °/s, converted from ticks/s
    public float TiltSpeed { get; init; }

    public bool PanSpeedMode => PanMode == 1;
    public bool TiltSpeedMode => TiltMode == 1;

    public static string ModeName(byte m) => m switch
    {
        0 => "Safe / Damping",
        1 => "Rate / Velocity",
        2 => "Position",
        3 => "Stabilised",
        _ => "?",
    };

    private static int I32(byte[] p, int o)
        => p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24);

    public static DevicePanTiltResponse? Parse(byte[] p)
    {
        if (p.Length < 18) return null;
        return new DevicePanTiltResponse
        {
            PanMode = (byte)((p[0] >> 1) & 0x03),
            TiltMode = (byte)((p[1] >> 1) & 0x03),
            PanActive = (p[0] & 0x01) == 0,    // bit0 == 0 → ACTIVE
            TiltActive = (p[1] & 0x01) == 0,
            PanAngleDeg = (float)(I32(p, 2) * DegPerTick),
            TiltAngleDeg = (float)(I32(p, 6) * DegPerTick),
            PanSpeed = (float)(I32(p, 10) * DegPerTick),    // ticks/s → °/s
            TiltSpeed = (float)(I32(p, 14) * DegPerTick),
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
    // Range values are little-endian IEEE-754 float32 METRES on the wire
    // (confirmed against the Noptel LRF debugger: bytes reinterpreted via
    // BitConverter.ToSingle, value already in metres — NOT a scaled integer).
    // A non-positive value means "no return" and is reported as 0.
    public double Range1Meters { get; init; }
    public ushort Signal1Ms { get; init; }
    public double Range2Meters { get; init; }
    public ushort Signal2Ms { get; init; }
    public double Range3Meters { get; init; }
    public ushort Signal3Ms { get; init; }

    private static double ReadRangeFloat(byte[] p, int off)
    {
        // bytes [off..off+3] little-endian float32
        float v = BitConverter.ToSingle(p, off);
        // Reference debugger: if (int)value <= 0, treat as no-return (0.0).
        return (int)v <= 0 ? 0.0 : v;
    }

    public static DeviceLrfResponse? Parse(byte[] p)
    {
        // Payload layout (sync + cmd already stripped):
        //   [0..3]  Range 1  float32 LE (m)   [4..5]  Signal 1 (ms)
        //   [6..9]  Range 2  float32 LE (m)   [10..11] Signal 2 (ms)
        //   [12..15] Range 3 float32 LE (m)   [16..17] Signal 3 (ms)
        if (p.Length < 18) return null;
        return new DeviceLrfResponse
        {
            Range1Meters = ReadRangeFloat(p, 0),
            Signal1Ms = (ushort)(p[4] | (p[5] << 8)),
            Range2Meters = ReadRangeFloat(p, 6),
            Signal2Ms = (ushort)(p[10] | (p[11] << 8)),
            Range3Meters = ReadRangeFloat(p, 12),
            Signal3Ms = (ushort)(p[16] | (p[17] << 8)),
        };
    }
}

// ============================================================================
// §3.3.16  IBIT Response  (Length = 0x1E = 30 payload bytes)
// ============================================================================
public sealed class DeviceIbitResponse
{
    // §3.3.16 IBIT response, 11-byte payload (frame bytes 11-21; CRC 22-23).
    // Per SRS v2 Table 26 (payload index = frame byte - 11):
    //   p[0]  Status byte: bit0 in-progress, bit1 last PASSED, bit2 last FAILED
    //   p[1]  Progress %, 0-100
    //   p[2]  Pan Motor test bits
    //   p[3]  Tilt Motor test bits
    //   p[4]  Sensor / Central electronics test bits
    //   p[5]  Pan Extended (CBIT) faults
    //   p[6]  Tilt Extended (CBIT) faults
    //   p[7]  IMU status bits (comms, BIT, accel/gyro PBIT+CBIT)
    //   p[8]  IMU status bits 2 (range)
    //   p[9..10] Reserve
    // For every test bit: 0 = PASS, 1 = FAIL (except IMU bytes 7-8 where
    // 1 = PASS — handled separately).
    public byte StatusByte { get; init; }
    public byte ProgressPercent { get; init; }
    public byte PanMotorBits { get; init; }
    public byte TiltMotorBits { get; init; }
    public byte SensorBits { get; init; }
    public byte PanExtBits { get; init; }
    public byte TiltExtBits { get; init; }
    public byte ImuBits1 { get; init; }
    public byte ImuBits2 { get; init; }

    public bool TestInProgress => (StatusByte & 0x01) != 0;
    public bool LastTestPassed => (StatusByte & 0x02) != 0;
    public bool LastTestFailed => (StatusByte & 0x04) != 0;

    /// <summary>True when all fault bytes (frame 13-17) are 0x00 (no faults).</summary>
    public bool NoFaults =>
        PanMotorBits == 0 && TiltMotorBits == 0 && SensorBits == 0 &&
        PanExtBits == 0 && TiltExtBits == 0;

    public bool IsComplete => ProgressPercent >= 100;

    public string FaultSummary => NoFaults ? "No faults"
        : $"FAULT  Pan:0x{PanMotorBits:X2} Tilt:0x{TiltMotorBits:X2} " +
          $"Sensor:0x{SensorBits:X2} PanExt:0x{PanExtBits:X2} TiltExt:0x{TiltExtBits:X2}";

    // ── Per-subsystem decoded summaries (1 = FAIL) ──────────────────────────
    private static readonly string[] PanMotorLabels =
        ["Motor Start", "Motor Connection", "Motor Polarity", "Stall",
         "Encoder", "Motion Range", "Zero Offset"];          // bit7 reserved
    private static readonly string[] TiltMotorLabels =
        ["Motor Start", "Motor Connection", "Motor Polarity", "Stall",
         "Encoder", "Min EndStop (-30°)", "Max EndStop (+75°)", "Zero Offset"];
    private static readonly string[] SensorLabels =
        ["Power", "Motor Brake Active", "IMU Self-Test", "IMU Active",
         "Pan ADC", "Tilt ADC"];                              // bits6-7 reserved
    private static readonly string[] ExtLabels =
        ["Overcurrent", "Control Saturation", "KCL Error",
         "Rail Short", "Phantom Voltage"];                    // bits5-7 reserved

    public string PanMotorSummary => DecodeFailBits(PanMotorBits, PanMotorLabels);
    public string TiltMotorSummary => DecodeFailBits(TiltMotorBits, TiltMotorLabels);
    public string SensorSummary => DecodeFailBits(SensorBits, SensorLabels);
    public string PanExtSummary => DecodeFailBits(PanExtBits, ExtLabels);
    public string TiltExtSummary => DecodeFailBits(TiltExtBits, ExtLabels);

    // IMU bytes 7-8 use 1 = PASS (inverted), so report failures where bit == 0.
    private static readonly string[] Imu1Labels =
        ["Comms", "BIT", "Acc X", "Acc Y", "Acc Z", "Gyro X", "Gyro Y", "Gyro Z"];
    public string ImuSummary
    {
        get
        {
            var fails = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Imu1Labels.Length; i++)
                if ((ImuBits1 & (1 << i)) == 0) fails.Add(Imu1Labels[i] + " FAIL");
            // byte 8: bit0 accel range, bit1 gyro range (1 = pass)
            if ((ImuBits2 & 0x01) == 0) fails.Add("Accel Range FAIL");
            if ((ImuBits2 & 0x02) == 0) fails.Add("Gyro Range FAIL");
            return fails.Count == 0 ? "Pass" : string.Join(" | ", fails);
        }
    }

    public static DeviceIbitResponse? Parse(byte[] p)
    {
        if (p.Length < 9) return null;   // need through the IMU status bytes
        return new DeviceIbitResponse
        {
            StatusByte = p[0],
            ProgressPercent = p[1],
            PanMotorBits = p[2],
            TiltMotorBits = p[3],
            SensorBits = p[4],
            PanExtBits = p[5],
            TiltExtBits = p[6],
            ImuBits1 = p[7],
            ImuBits2 = p[8],
        };
    }

    private static string DecodeFailBits(byte bits, string[] labels)
    {
        if (bits == 0) return "Pass";
        var fails = new System.Collections.Generic.List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) != 0) fails.Add(labels[i] + " FAIL");
        return fails.Count == 0 ? "Pass" : string.Join(" | ", fails);
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
// ============================================================================
// §3.3  GET Stab Control Response  (Command 0x000A, Length = 0x34 = 52 bytes)
//
// Per the SQT/ICD, the C2000 returns "high-density stabilisation telemetry":
//   System Uptime, IMU status, Raw Accels (X,Y,Z), Raw Gyros (X,Y,Z),
//   Temperature, EKF Pitch/Yaw, and Nudge Rates.
//
// ── PROVISIONAL byte layout (VERIFY against firmware) ───────────────────────
// The document lists the field ORDER but not explicit byte offsets, so the
// offsets below follow the documented order using conventional C2000 widths.
// If the firmware packs these differently, only the offset constants in
// Parse() need adjusting — the rest of the stack is layout-agnostic.
//
//   [ 0.. 3] System Uptime    uint32 LE  (seconds)
//   [ 4.. 5] IMU Status       uint16 LE  (status/flags)
//   [ 6.. 9] Accel X          float32 LE
//   [10..13] Accel Y          float32 LE
//   [14..17] Accel Z          float32 LE
//   [18..21] Gyro X           float32 LE
//   [22..25] Gyro Y           float32 LE
//   [26..29] Gyro Z           float32 LE
//   [30..33] Temperature      float32 LE  (°C)
//   [34..37] EKF Pitch        float32 LE  (degrees)
//   [38..41] EKF Yaw          float32 LE  (degrees)
//   [42..45] Pan Nudge Rate   int32 LE    (ticks/s)
//   [46..49] Tilt Nudge Rate  int32 LE    (ticks/s)
//   [50..51] Reserve
// ============================================================================
public sealed class DeviceStabStatusResponse
{
    public uint UptimeMicros { get; init; }      // TIME_STAMP: µs since power-up
    public ushort ImuStatus { get; init; }
    public float AccelX { get; init; }
    public float AccelY { get; init; }
    public float AccelZ { get; init; }
    public float GyroX { get; init; }
    public float GyroY { get; init; }
    public float GyroZ { get; init; }
    public float TemperatureC { get; init; }
    public float EkfPitchDeg { get; init; }
    public float EkfYawDeg { get; init; }
    public int PanNudgeTicks { get; init; }
    public int TiltNudgeTicks { get; init; }

    /// <summary>
    /// IMU health: the C2000 (French) firmware uses 1 = test PASSED. Our older
    /// convention treated a set bit as a fault, which inverted the result. Here
    /// a non-zero status means the IMU self-tests passed (OK); zero means no
    /// pass bits set (fault). Verified against GUI feedback (0x7302 → OK).
    /// </summary>
    public bool ImuOk => ImuStatus != 0;

    private static float F32(byte[] p, int o) => BitConverter.ToSingle(p, o);
    private static int I32(byte[] p, int o)
        => p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24);
    private static uint U32(byte[] p, int o) => (uint)I32(p, o);

    public static DeviceStabStatusResponse? Parse(byte[] p)
    {
        // Require the full 50 data bytes we read (2-byte reserve may be absent).
        if (p.Length < 50) return null;
        return new DeviceStabStatusResponse
        {
            UptimeMicros = U32(p, 0),
            ImuStatus = (ushort)(p[4] | (p[5] << 8)),
            AccelX = F32(p, 6),
            AccelY = F32(p, 10),
            AccelZ = F32(p, 14),
            GyroX = F32(p, 18),
            GyroY = F32(p, 22),
            GyroZ = F32(p, 26),
            TemperatureC = F32(p, 30),
            EkfPitchDeg = F32(p, 34),
            EkfYawDeg = F32(p, 38),
            PanNudgeTicks = I32(p, 42),
            TiltNudgeTicks = I32(p, 46),
        };
    }
}