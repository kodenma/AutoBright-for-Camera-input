using System.Runtime.InteropServices;

namespace AutoBrightnessApp.Services.Display;

public sealed class DdcCiBrightnessDriver : IMonitorBrightnessDriver
{
    private sealed record PhysicalMonitorEntry(string Id, string Description, IntPtr Handle, uint Min, uint Max);
    private readonly Dictionary<string, PhysicalMonitorEntry> _monitors = new();

    public IReadOnlyList<MonitorInfo> DiscoverMonitors()
    {
        ClearMonitors();
        var result = new List<MonitorInfo>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                return true;

            var physicalMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
                return true;

            foreach (var pm in physicalMonitors)
            {
                // DDC/CI 輝度取得が成功するモニター（外部対応機）のみ登録する
                // ※ノートPC内蔵液晶はここで false を返すため、WMIドライバと重複しない
                if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out uint current, out uint max) && max > min)
                {
                    string id = $"DDCCI:{index++}_{pm.szPhysicalMonitorDescription}";
                    var entry = new PhysicalMonitorEntry(id, pm.szPhysicalMonitorDescription, pm.hPhysicalMonitor, min, max);
                    _monitors[id] = entry;

                    int pct = (int)Math.Round((current - min) * 100.0 / (max - min));
                    result.Add(new MonitorInfo(
                        Id: id,
                        Name: string.IsNullOrWhiteSpace(pm.szPhysicalMonitorDescription) ? $"外部モニター {index}" : pm.szPhysicalMonitorDescription,
                        ConnectionType: "External (DDC/CI)",
                        CurrentBrightness: pct,
                        MinBrightness: 0,
                        MaxBrightness: 100
                    ));
                }
                else
                {
                    // 非対応モニターのハンドルは即座に解放
                    DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                }
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public int? GetBrightness(string monitorId)
    {
        if (!_monitors.TryGetValue(monitorId, out var entry)) return null;

        if (GetMonitorBrightness(entry.Handle, out uint min, out uint current, out uint max) && max > min)
        {
            return (int)Math.Round((current - min) * 100.0 / (max - min));
        }
        return null;
    }

    public bool SetBrightness(string monitorId, int percentage)
    {
        if (!_monitors.TryGetValue(monitorId, out var entry)) return false;

        int clamped = Math.Clamp(percentage, 0, 100);
        uint targetRaw = (uint)Math.Round(entry.Min + (entry.Max - entry.Min) * (clamped / 100.0));
        return SetMonitorBrightness(entry.Handle, targetRaw);
    }

    private void ClearMonitors()
    {
        foreach (var entry in _monitors.Values)
        {
            DestroyPhysicalMonitor(entry.Handle);
        }
        _monitors.Clear();
    }

    public void Dispose()
    {
        ClearMonitors();
        GC.SuppressFinalize(this);
    }

    #region Win32 / dxva2.dll P/Invoke

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    #endregion
}