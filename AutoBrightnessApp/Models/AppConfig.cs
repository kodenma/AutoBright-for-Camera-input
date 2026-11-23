using System.IO;
using System.Text.Json;

namespace AutoBrightnessApp.Models;

public record BrightnessPoint(double InputScore, int TargetBrightness);

public sealed class CameraProfile
{
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int? PreferredWidth { get; set; }
    public int? PreferredHeight { get; set; }
    public List<BrightnessPoint> Curve { get; set; } = new();

    public static CameraProfile CreateDefault(string id, string name)
    {
        return new CameraProfile
        {
            DeviceId = id,
            DisplayName = name,
            Curve =
            [
                new(0, 15),
                new(45, 28),
                new(120, 48),
                new(260, 72),
                new(600, 100)
            ]
        };
    }
}

public sealed class MonitorSetting
{
    public string MonitorId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int BrightnessOffset { get; set; } = 0;
}

public sealed class AppConfig
{
    // AutoBrightnessApp.exe と同じディレクトリに config.json を配置
    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public bool HasAcceptedDisclaimer { get; set; } = false;
    public string LanguageCode { get; set; } = "en";
    public string? SelectedCameraId { get; set; }
    public string PowerlineFrequencyMode { get; set; } = "Auto";
    public bool ContinuousCaptureMode { get; set; } = false;
    public bool DebugMode { get; set; } = false;
    public int IntervalSeconds { get; set; } = 15;
    public int InputSmoothingLevel { get; set; } = 4;
    public int OutputFadeLevel { get; set; } = 5;
    public double HysteresisPercent { get; set; } = 8.0;
    public int ManualOverrideMinutes { get; set; } = 60;
    public Dictionary<string, CameraProfile> CameraProfiles { get; set; } = new();
    public Dictionary<string, MonitorSetting> MonitorSettings { get; set; } = new();

    public CameraProfile GetOrCreateProfile(string deviceId, string displayName)
    {
        if (!CameraProfiles.TryGetValue(deviceId, out var profile))
        {
            profile = CameraProfile.CreateDefault(deviceId, displayName);
            CameraProfiles[deviceId] = profile;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(profile.DeviceId))
            {
                profile.DeviceId = deviceId;
            }
            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                profile.DisplayName = displayName;
            }
            if (profile.Curve == null || profile.Curve.Count < 2)
            {
                profile.Curve = CameraProfile.CreateDefault(deviceId, displayName).Curve;
            }
        }
        return profile;
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 書き込み権限がないフォルダへ配置された場合でもクラッシュさせない
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.CameraProfiles ??= new();
                    loaded.MonitorSettings ??= new();
                    if (string.IsNullOrWhiteSpace(loaded.PowerlineFrequencyMode))
                    {
                        loaded.PowerlineFrequencyMode = "Auto";
                    }
                    return loaded;
                }
            }
        }
        catch { }
        return new AppConfig();
    }
}