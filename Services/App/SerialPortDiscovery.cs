// ============================================================
//  Services/App/SerialPortDiscovery.cs
//
//  Enumerates available serial ports with friendly descriptions
//  and a "looks like a real USB-to-Serial adapter" heuristic, so
//  the UI can filter out non-serial COM ports (wireless dongles,
//  CDC-class USB flash drives, Bluetooth virtual COMs, modems...)
//  that would otherwise show up next to legitimate adapters.
//
//  Windows: Win32_PnPEntity WMI query  -> friendly name
//  Linux:   /sys/class/tty/* sysfs     -> driver / idVendor
//  Other:   bare SerialPort.GetPortNames() fallback
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;

namespace ShockUI.Services.App;

/// <summary>
/// One discovered serial-port entry with display metadata.
/// </summary>
public sealed class SerialPortInfo
{
    /// <summary>OS-level port name: "COM4" on Windows, "/dev/ttyUSB0" on Linux.</summary>
    public required string PortName { get; init; }

    /// <summary>Friendly device name from WMI / sysfs (or "(unknown)" as fallback).</summary>
    public required string Description { get; init; }

    /// <summary>"COM4 — Silicon Labs CP210x USB to UART Bridge"</summary>
    public required string DisplayName { get; init; }

    /// <summary>True if the name/driver looks like a real USB-to-Serial adapter.</summary>
    public bool IsLikelyUsbSerial { get; init; }
}

public static class SerialPortDiscovery
{
    // Substrings that strongly suggest a real USB-to-Serial adapter.
    // Match is case-insensitive. If a port's description contains any of
    // these, IsLikelyUsbSerial = true.
    private static readonly string[] UsbSerialMarkers =
    {
        "usb serial",      // Generic
        "usb-serial",
        "usb to uart",
        "usb-to-uart",
        "ftdi", "ft232", "ft231",
        "cp210", "silicon labs",
        "ch340", "ch341",
        "prolific", "pl2303",
        "arduino",
        "stm32",
        "mcp2200", "microchip",
    };

    // Patterns that almost-always indicate a non-serial device that
    // happens to expose a COM port (Bluetooth radios, modems, etc.).
    // Used only when filterToLikelySerial=true — we drop these even if
    // their name happens to contain "Serial".
    private static readonly string[] ExcludeMarkers =
    {
        "bluetooth",
        "modem",
        "fax",
    };

    /// <summary>
    /// Enumerate available serial ports with metadata.
    /// </summary>
    /// <param name="filterToLikelySerial">
    /// When true, drops entries whose description doesn't match any of
    /// the known USB-to-Serial chip-set markers. When false, returns
    /// every COM port the OS reports.
    /// </param>
    public static IList<SerialPortInfo> Enumerate(bool filterToLikelySerial = true)
    {
        IList<SerialPortInfo> ports;

        if (OperatingSystem.IsWindows())
            ports = EnumerateWindows();
        else if (OperatingSystem.IsLinux())
            ports = EnumerateLinux();
        else
            ports = EnumerateBare();

        if (filterToLikelySerial)
            ports = ports.Where(p => p.IsLikelyUsbSerial).ToList();

        // Sort: real-COM ports first by numeric index, then /dev paths
        return ports
            .OrderBy(p => p.PortName, NaturalStringComparer.Instance)
            .ToList();
    }

    // -----------------------------------------------------------------
    // Windows — WMI Win32_PnPEntity. Friendly name looks like
    //   "Silicon Labs CP210x USB to UART Bridge (COM4)"
    // We pull the "(COMnn)" out of the caption to get the port name,
    // and the rest becomes the description.
    // -----------------------------------------------------------------
    [SupportedOSPlatform("windows")]
    private static IList<SerialPortInfo> EnumerateWindows()
    {
        var list = new List<SerialPortInfo>();

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Caption FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");

            foreach (var obj in searcher.Get())
            {
                var caption = obj["Caption"]?.ToString();
                if (string.IsNullOrWhiteSpace(caption)) continue;

                var m = Regex.Match(caption, @"\(COM(\d+)\)");
                if (!m.Success) continue;

                string portName = "COM" + m.Groups[1].Value;
                string desc = caption[..m.Index].TrimEnd();

                if (LooksExcluded(desc)) continue;

                list.Add(new SerialPortInfo
                {
                    PortName = portName,
                    Description = desc,
                    DisplayName = $"{portName} — {desc}",
                    IsLikelyUsbSerial = LooksLikeUsbSerial(desc),
                });
            }
        }
        catch
        {
            // WMI unavailable (rare; sandbox / locked-down host).
            // Fall through to the bare list so the dropdown isn't empty.
        }

        // Anything SerialPort sees that WMI missed → add as "unknown".
        foreach (var name in SafeGetPortNames())
        {
            if (list.Any(p => p.PortName == name)) continue;
            list.Add(new SerialPortInfo
            {
                PortName = name,
                Description = "(unknown)",
                DisplayName = name,
                IsLikelyUsbSerial = false,
            });
        }

        return list;
    }

    // -----------------------------------------------------------------
    // Linux — walk /sys/class/tty/*/device. ttyUSB* and ttyACM* are the
    // standard USB-serial sysfs names; everything else (ttyS*, etc.) is
    // a native UART. We read the driver name and the USB vendor string
    // (if available) for the description.
    // -----------------------------------------------------------------
    [SupportedOSPlatform("linux")]
    private static IList<SerialPortInfo> EnumerateLinux()
    {
        var list = new List<SerialPortInfo>();

        foreach (var name in SafeGetPortNames())
        {
            // SerialPort.GetPortNames() on Linux returns "/dev/ttyUSB0" etc.
            string basename = Path.GetFileName(name);
            string sysClass = $"/sys/class/tty/{basename}";
            string desc = "(unknown)";

            try
            {
                // Driver symlink: /sys/class/tty/ttyUSB0/device/driver
                string driverLink = Path.Combine(sysClass, "device", "driver");
                if (Directory.Exists(driverLink))
                {
                    string drvName = Path.GetFileName(
                        new DirectoryInfo(driverLink).LinkTarget ?? driverLink);
                    if (!string.IsNullOrEmpty(drvName)) desc = drvName;
                }

                // USB vendor product strings:
                //   /sys/class/tty/ttyUSB0/device/../manufacturer
                //   /sys/class/tty/ttyUSB0/device/../product
                string parent = Path.Combine(sysClass, "device", "..");
                string manu = TryReadAll(Path.Combine(parent, "manufacturer"));
                string prod = TryReadAll(Path.Combine(parent, "product"));
                if (!string.IsNullOrWhiteSpace(prod))
                    desc = string.IsNullOrWhiteSpace(manu) ? prod : $"{manu} {prod}";
            }
            catch
            {
                // sysfs unreadable — keep desc = "(unknown)"
            }

            if (LooksExcluded(desc)) continue;

            bool isUsbSerial =
                basename.StartsWith("ttyUSB", StringComparison.Ordinal) ||
                basename.StartsWith("ttyACM", StringComparison.Ordinal) ||
                LooksLikeUsbSerial(desc);

            list.Add(new SerialPortInfo
            {
                PortName = name,
                Description = desc,
                DisplayName = desc == "(unknown)" ? name : $"{name} — {desc}",
                IsLikelyUsbSerial = isUsbSerial,
            });
        }

        return list;
    }

    // -----------------------------------------------------------------
    // Fallback for unknown OSes — names only, no metadata.
    // -----------------------------------------------------------------
    private static IList<SerialPortInfo> EnumerateBare()
        => SafeGetPortNames()
            .Select(n => new SerialPortInfo
            {
                PortName = n,
                Description = "(unknown)",
                DisplayName = n,
                IsLikelyUsbSerial = false,
            })
            .ToList();

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Pulls the bare port name back out of a display string, so calling
    /// code can pass it to <see cref="SerialPort"/>. If the input has
    /// no " — " separator the input is returned as-is (it's already a
    /// bare name).
    /// </summary>
    public static string ExtractPortName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return displayName;
        int dashIdx = displayName.IndexOf(" — ", StringComparison.Ordinal);
        return dashIdx > 0 ? displayName[..dashIdx] : displayName;
    }

    private static bool LooksLikeUsbSerial(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        string d = desc.ToLowerInvariant();
        return UsbSerialMarkers.Any(m => d.Contains(m));
    }

    private static bool LooksExcluded(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        string d = desc.ToLowerInvariant();
        return ExcludeMarkers.Any(m => d.Contains(m));
    }

    private static string[] SafeGetPortNames()
    {
        try { return SerialPort.GetPortNames(); }
        catch { return Array.Empty<string>(); }
    }

    private static string TryReadAll(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Sorts "COM2" before "COM10" instead of the lexicographic
    /// "COM10" before "COM2". Works for both COMnn and /dev/tty<n>.
    /// </summary>
    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int ix = 0, iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                {
                    long nx = 0, ny = 0;
                    while (ix < x.Length && char.IsDigit(x[ix])) nx = nx * 10 + (x[ix++] - '0');
                    while (iy < y.Length && char.IsDigit(y[iy])) ny = ny * 10 + (y[iy++] - '0');
                    if (nx != ny) return nx.CompareTo(ny);
                }
                else
                {
                    int c = x[ix].CompareTo(y[iy]);
                    if (c != 0) return c;
                    ix++; iy++;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}