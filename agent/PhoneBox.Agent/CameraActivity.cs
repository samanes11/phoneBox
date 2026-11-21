#if WINDOWS
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Which apps are streaming from a camera, as Media Foundation reports it (Windows 10 2004 and later). Windows calls
/// back when a camera starts or stops; the last report is kept. The monitor starts on the first read, so nothing runs
/// until the "On air" widget or a rule asks.
/// </summary>
internal static class CameraActivity
{
    private static readonly Lock Gate = new();
    private static IMFSensorActivityMonitor? _monitor;
    private static Callback? _callback;
    private static bool _failed;
    private static volatile int[] _streaming = [];

    /// <summary>Names of the apps streaming now; null when the monitor is unavailable (Windows before 10 2004).</summary>
    public static List<string>? Streaming()
    {
        if (!Start()) return null;
        var apps = new List<string>();
        foreach (var pid in _streaming)
            if (PrivacyMonitor.ProcessName(pid) is { } name) apps.Add(name); // an app that exited no longer counts
        return PrivacyMonitor.Distinct(apps);
    }

    private static bool Start()
    {
        lock (Gate)
        {
            if (_monitor is not null || _failed) return !_failed;
            try
            {
                Marshal.ThrowExceptionForHR(MFStartup(0x0002_0070 /*MF_VERSION*/, 0 /*MFSTARTUP_FULL*/));
                _callback = new Callback();
                Marshal.ThrowExceptionForHR(MFCreateSensorActivityMonitor(_callback, out var monitor));
                Marshal.ThrowExceptionForHR(monitor.Start());
                _monitor = monitor; // kept for the life of the agent; Windows stops it when the process ends
                return true;
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
            {
                _failed = true; // an older Windows: the consent store remains the only source
                _callback = null;
                return false;
            }
        }
    }

    /// <summary>The process ids that are streaming in a report. Each report covers every camera.</summary>
    internal static int[] StreamingPids(IMFSensorActivitiesReport report)
    {
        var pids = new HashSet<int>();
        if (report.GetCount(out var devices) < 0) return [];
        for (uint d = 0; d < devices; d++)
        {
            if (report.GetActivityReport(d, out var device) < 0 || device is null) continue;
            try
            {
                if (device.GetProcessCount(out var count) < 0) continue;
                for (uint p = 0; p < count; p++)
                {
                    if (device.GetProcessActivity(p, out var process) < 0 || process is null) continue;
                    try
                    {
                        if (process.GetStreamingState(out var streaming) >= 0 && streaming && process.GetProcessId(out var pid) >= 0)
                            pids.Add((int)pid);
                    }
                    finally { Marshal.ReleaseComObject(process); }
                }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        return [.. pids];
    }

    [ComVisible(true)]
    private sealed class Callback : IMFSensorActivitiesReportCallback
    {
        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            try { _streaming = StreamingPids(report); }
            catch (COMException) { }
            return 0;
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfsensorgroup.dll", ExactSpelling = true)]
    private static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback, out IMFSensorActivityMonitor monitor);

    [ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivitiesReportCallback
    {
        [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
    }

    [ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivitiesReport
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
        [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string symbolicName, out IMFSensorActivityReport report);
    }

    [ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivityReport
    {
        [PreserveSig] int GetFriendlyName(IntPtr name, uint chars, out uint written);
        [PreserveSig] int GetSymbolicLink(IntPtr link, uint chars, out uint written);
        [PreserveSig] int GetProcessCount(out uint count);
        [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
    }

    [ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorProcessActivity
    {
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int GetStreamingState([MarshalAs(UnmanagedType.Bool)] out bool streaming);
        [PreserveSig] int GetStreamingMode(out int mode);
        [PreserveSig] int GetReportTime(out long fileTime);
    }

    [ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivityMonitor
    {
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
    }
}
#endif
