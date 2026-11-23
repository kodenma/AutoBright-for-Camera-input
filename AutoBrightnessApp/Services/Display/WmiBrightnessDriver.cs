using System.Management;
using System.Runtime.Versioning;

namespace AutoBrightnessApp.Services.Display;

[SupportedOSPlatform("windows")]
public sealed class WmiBrightnessDriver : IMonitorBrightnessDriver
{
    private const string WmiScope = @"\\.\root\wmi";

    public IReadOnlyList<MonitorInfo> DiscoverMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope, "SELECT InstanceName, CurrentBrightness, Active FROM WmiMonitorBrightness");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                if (obj["Active"] is true)
                {
                    string instanceName = obj["InstanceName"]?.ToString() ?? "InternalDisplay";
                    int current = Convert.ToInt32(obj["CurrentBrightness"]);
                    list.Add(new MonitorInfo(
                        Id: $"WMI:{instanceName}",
                        Name: "Internal Display",
                        ConnectionType: "Internal (WMI)",
                        CurrentBrightness: current,
                        MinBrightness: 0,
                        MaxBrightness: 100
                    ));
                }
            }
        }
        catch { }
        return list;
    }

    public int? GetBrightness(string monitorId)
    {
        if (!monitorId.StartsWith("WMI:", StringComparison.Ordinal)) return null;
        string targetInstance = monitorId[4..];

        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope, "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                if (obj["InstanceName"]?.ToString() == targetInstance)
                {
                    return Convert.ToInt32(obj["CurrentBrightness"]);
                }
            }
        }
        catch { }
        return null;
    }

    public bool SetBrightness(string monitorId, int percentage)
    {
        if (!monitorId.StartsWith("WMI:", StringComparison.Ordinal)) return false;
        string targetInstance = monitorId[4..];
        byte clamped = (byte)Math.Clamp(percentage, 0, 100);

        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope, "SELECT InstanceName FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                if (obj["InstanceName"]?.ToString() == targetInstance)
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { 0u, clamped });
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public void Dispose() { }
}