using Avalonia;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ShockUI;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Install native-library resolvers BEFORE any managed code touches
        // System.IO.Ports. Once the runtime caches a failed lookup we can't
        // retry, so this must be the very first thing Main does.
        InstallNativeLibraryShims();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Handles a Linux-specific quirk where the System.IO.Ports package
    /// declares its P/Invoke as <c>[DllImport("libSystem.IO.Ports.Native")]</c>
    /// (with the "lib" prefix baked into the DLL name). On Linux, dlopen
    /// then mangles that into <c>liblibSystem.IO.Ports.Native.so</c> when
    /// searching the application directory — which doesn't match the file
    /// the publish actually drops there (<c>libSystem.IO.Ports.Native.so</c>),
    /// and Connect fails with "transport could not open".
    ///
    /// The fix: register a DllImportResolver that intercepts requests for
    /// "libSystem.IO.Ports.Native" and explicitly loads the .so from
    /// <see cref="AppContext.BaseDirectory"/>. Avoids the symlink workaround.
    /// Windows / macOS hit the no-op path and load normally.
    /// </summary>
    private static void InstallNativeLibraryShims()
    {
        if (!OperatingSystem.IsLinux()) return;

        // The assembly to attach the resolver to is the one that owns the
        // SerialPort type — that's where the DllImport declarations live.
        var serialPortAssembly = typeof(System.IO.Ports.SerialPort).Assembly;

        NativeLibrary.SetDllImportResolver(serialPortAssembly, ResolveLinuxNativeLib);
    }

    private static IntPtr ResolveLinuxNativeLib(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only intercept the one library that's broken; everything else
        // falls through to the default loader (return IntPtr.Zero).
        if (libraryName is not "libSystem.IO.Ports.Native" and not "System.IO.Ports.Native")
            return IntPtr.Zero;

        // Try the actual .so next to the executable first. That's where
        // `dotnet publish -r linux-x64` drops the file.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libSystem.IO.Ports.Native.so"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64",
                         "native", "libSystem.IO.Ports.Native.so"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }

        // Fall through; the runtime will produce its usual error message
        // if nothing else picks it up.
        return IntPtr.Zero;
    }
}