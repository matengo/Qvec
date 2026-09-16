using System.Runtime.InteropServices;

namespace Qvec.Benchmarks;

/// <summary>
/// Opts the benchmark process out of Windows power throttling (EcoQoS).
/// </summary>
/// <remarks>
/// Windows 11 classifies a console process that is not in the foreground as background work
/// and, on a Balanced power plan, schedules it on few cores at reduced clock speed. On a
/// 12-core laptop this showed up as a 12-thread build getting about four cores' worth of CPU
/// time with no lock contention to explain it, and as a 40 % day-to-day spread in single-thread
/// QPS. A benchmark wants the hardware it reports, so it asks to be exempted. This is a hint
/// the OS may ignore; it is not available to the library itself.
/// </remarks>
internal static partial class PowerThrottling
{
    private const uint ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, uint informationClass, ref ProcessPowerThrottlingState information, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>Returns true when the request was accepted; false on other platforms or failure.</summary>
    public static bool TryDisable()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed,
            StateMask = 0,
        };

        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
    }
}
