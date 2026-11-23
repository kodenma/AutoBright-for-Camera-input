namespace AutoBrightnessApp.Services.Display;

public record MonitorInfo(
    string Id,
    string Name,
    string ConnectionType, // "Internal (WMI)" or "External (DDC/CI)"
    int CurrentBrightness,
    int MinBrightness,
    int MaxBrightness
);

public interface IMonitorBrightnessDriver : IDisposable
{
    IReadOnlyList<MonitorInfo> DiscoverMonitors();
    int? GetBrightness(string monitorId);
    bool SetBrightness(string monitorId, int percentage);
}