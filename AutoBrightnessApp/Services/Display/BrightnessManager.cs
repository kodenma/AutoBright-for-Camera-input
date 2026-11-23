using System.Management;

namespace AutoBrightnessApp.Services.Display;

public sealed class BrightnessManager : IDisposable
{
    private static readonly int[] StepDelayTableMs = [0, 15, 30, 50, 80, 120, 170, 230, 300, 400];

    private readonly WmiBrightnessDriver _wmiDriver = new();
    private readonly DdcCiBrightnessDriver _ddcDriver = new();

    private readonly object _stateLock = new();
    private readonly Dictionary<string, int> _lastAppliedBrightness = new();
    private readonly Dictionary<string, DateTime> _lastAppliedUtc = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeFades = new();
    private readonly HashSet<string> _fadingMonitorIds = new();

    private ManagementEventWatcher? _wmiBrightnessWatcher;
    private volatile bool _isMonitoringActive = true;
    private volatile bool _isDisposed;
    private Func<string, bool>? _isMonitorEnabledPredicate;

    /// <summary>
    /// ユーザーによる手動輝度変更を検知した際に発火する（引数: monitorId, detectedBrightness）
    /// </summary>
    public event Action<string, int>? ManualBrightnessChanged;

    public BrightnessManager()
    {
        StartWmiBrightnessEventWatcher();
    }

    public void ConfigureMonitoring(bool isActive, Func<string, bool>? isMonitorEnabledPredicate = null)
    {
        _isMonitoringActive = isActive;
        if (isMonitorEnabledPredicate != null)
        {
            _isMonitorEnabledPredicate = isMonitorEnabledPredicate;
        }
    }

    public IReadOnlyList<MonitorInfo> RefreshAllMonitors()
    {
        var all = new List<MonitorInfo>();
        try { all.AddRange(_wmiDriver.DiscoverMonitors()); } catch { }
        try { all.AddRange(_ddcDriver.DiscoverMonitors()); } catch { }
        return all;
    }

    private void StartWmiBrightnessEventWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            _wmiBrightnessWatcher = new ManagementEventWatcher(scope, query);
            _wmiBrightnessWatcher.EventArrived += OnWmiBrightnessEventArrived;
            _wmiBrightnessWatcher.Start();
            DebugLogger.Log("BRIGHTNESS_WATCH", "WMI 手動輝度変更イベント監視を開始しました。");
        }
        catch (Exception ex)
        {
            // デスクトップPC等、内蔵WMI輝度イベントが存在しない環境では静かにスキップ
            DebugLogger.Log("BRIGHTNESS_WATCH", $"WMI イベント監視は非対応のためポーリング検知のみ使用します: {ex.Message}");
            _wmiBrightnessWatcher?.Dispose();
            _wmiBrightnessWatcher = null;
        }
    }

    private void OnWmiBrightnessEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (_isDisposed || !_isMonitoringActive) return;

        try
        {
            if (e.NewEvent?["Brightness"] is not object rawLevel ||
                e.NewEvent["InstanceName"] is not string instanceName)
            {
                return;
            }

            int detectedLevel = Convert.ToInt32(rawLevel);
            string monitorId = "WMI:" + instanceName;

            if (_isMonitorEnabledPredicate != null && !_isMonitorEnabledPredicate(monitorId))
            {
                return;
            }

            bool isManualChange = false;
            int lastSetSnapshot = -1;

            lock (_stateLock)
            {
                if (!_lastAppliedBrightness.TryGetValue(monitorId, out int lastSet))
                {
                    return;
                }

                lastSetSnapshot = lastSet;
                int diff = Math.Abs(detectedLevel - lastSet);
                bool isFading = _fadingMonitorIds.Contains(monitorId);
                double elapsedMs = _lastAppliedUtc.TryGetValue(monitorId, out var appliedTime)
                    ? (DateTime.UtcNow - appliedTime).TotalMilliseconds
                    : double.MaxValue;

                if (isFading || elapsedMs < 1500.0)
                {
                    // アプリ自身がフェード・設定中の直後イベントは ±2% 以内なら自前更新とみなす
                    if (diff >= 3)
                    {
                        isManualChange = true;
                    }
                }
                else
                {
                    // 静止中に ±2% 以上動いた場合は手動輝度変更と判定
                    if (diff >= 2)
                    {
                        isManualChange = true;
                    }
                }
            }

            if (isManualChange)
            {
                DebugLogger.Log("MANUAL_DETECT", $"WMIイベントで手動輝度変更を検知: {monitorId} (前回設定={lastSetSnapshot}% -> 現在={detectedLevel}%)");
                CancelAllFades();
                ManualBrightnessChanged?.Invoke(monitorId, detectedLevel);
            }
        }
        catch { }
    }

    /// <summary>
    /// 外部モニター（DDC/CI）やWMIイベント非対応環境向けに、現在輝度と前回設定値のズレをチェックする
    /// </summary>
    public bool CheckExternalManualOverride(IEnumerable<string> enabledMonitorIds, bool includeWmi = false)
    {
        if (_isDisposed || !_isMonitoringActive) return false;

        foreach (var monitorId in enabledMonitorIds)
        {
            if (!includeWmi && monitorId.StartsWith("WMI:", StringComparison.Ordinal))
            {
                continue;
            }

            int lastSet;
            lock (_stateLock)
            {
                if (_fadingMonitorIds.Contains(monitorId)) continue;
                if (!_lastAppliedBrightness.TryGetValue(monitorId, out lastSet)) continue;
                if (_lastAppliedUtc.TryGetValue(monitorId, out var appliedUtc) &&
                    (DateTime.UtcNow - appliedUtc).TotalMilliseconds < 1500.0)
                {
                    continue;
                }
            }

            int? currentOpt = GetCurrentBrightness(monitorId);
            if (currentOpt is int current && Math.Abs(current - lastSet) >= 3)
            {
                DebugLogger.Log("MANUAL_DETECT", $"ポーリング監視で手動輝度変更を検知: {monitorId} (前回設定={lastSet}% -> 現在={current}%)");
                CancelAllFades();
                ManualBrightnessChanged?.Invoke(monitorId, current);
                return true;
            }
        }

        return false;
    }

    public void StartFadeToBrightness(
        string monitorId,
        int targetPercentage,
        int fadeLevel,
        CancellationToken globalCt)
    {
        if (_isDisposed) return;

        CancellationTokenSource linkedCts;
        lock (_stateLock)
        {
            if (_activeFades.TryGetValue(monitorId, out var existingCts))
            {
                try { existingCts.Cancel(); } catch { }
                existingCts.Dispose();
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
            _activeFades[monitorId] = linkedCts;
            _fadingMonitorIds.Add(monitorId);
        }

        _ = Task.Run(() => ExecuteFadeAsync(monitorId, targetPercentage, fadeLevel, linkedCts), CancellationToken.None);
    }

    public void CancelAllFades()
    {
        List<CancellationTokenSource> toCancel;
        lock (_stateLock)
        {
            toCancel = _activeFades.Values.ToList();
            _activeFades.Clear();
            _fadingMonitorIds.Clear();
        }

        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task ExecuteFadeAsync(
        string monitorId,
        int targetPercentage,
        int fadeLevel,
        CancellationTokenSource ownCts)
    {
        var ct = ownCts.Token;
        try
        {
            PowerEfficiencyService.ApplyBackgroundThreadEfficiency();

            targetPercentage = Math.Clamp(targetPercentage, 0, 100);
            int levelIdx = Math.Clamp(fadeLevel, 1, 10) - 1;
            int stepDelayMs = StepDelayTableMs[levelIdx];

            int? currentOpt = GetCurrentBrightness(monitorId);
            if (currentOpt is not int current) return;

            // フェード開始前の手動変更チェック
            if (TryDetectManualChangeLocked(monitorId, current, threshold: 3, out int prevSet))
            {
                DebugLogger.Log("MANUAL_DETECT", $"適用前チェックで手動輝度変更を検知: {monitorId} (前回設定={prevSet}% -> 現在={current}%)");
                CancelAllFades();
                ManualBrightnessChanged?.Invoke(monitorId, current);
                return;
            }

            if (current == targetPercentage)
            {
                RecordAppliedBrightness(monitorId, current);
                return;
            }

            if (stepDelayMs == 0)
            {
                RecordAppliedBrightness(monitorId, targetPercentage);
                SetRawBrightness(monitorId, targetPercentage);
                return;
            }

            int step = targetPercentage > current ? 1 : -1;
            int value = current;

            while (value != targetPercentage && !ct.IsCancellationRequested)
            {
                value += step;
                RecordAppliedBrightness(monitorId, value);
                SetRawBrightness(monitorId, value);

                if (value != targetPercentage)
                {
                    await Task.Delay(stepDelayMs, ct);
                    if (ct.IsCancellationRequested) break;

                    // フェード途中での手動介入（キーボード操作等）を検知
                    int? checkOpt = GetCurrentBrightness(monitorId);
                    if (checkOpt is int actual && Math.Abs(actual - value) >= 3)
                    {
                        DebugLogger.Log("MANUAL_DETECT", $"フェード中に手動輝度変更を検知: {monitorId} (想定={value}% -> 現在={actual}%)");
                        CancelAllFades();
                        ManualBrightnessChanged?.Invoke(monitorId, actual);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_stateLock)
            {
                if (_activeFades.TryGetValue(monitorId, out var currentCts) && ReferenceEquals(currentCts, ownCts))
                {
                    _activeFades.Remove(monitorId);
                    _fadingMonitorIds.Remove(monitorId);
                }
            }
            try { ownCts.Dispose(); } catch { }
        }
    }

    private bool TryDetectManualChangeLocked(string monitorId, int currentBrightness, int threshold, out int previousSet)
    {
        lock (_stateLock)
        {
            if (_lastAppliedBrightness.TryGetValue(monitorId, out previousSet) &&
                Math.Abs(currentBrightness - previousSet) >= threshold)
            {
                return true;
            }
        }
        previousSet = -1;
        return false;
    }

    private void RecordAppliedBrightness(string monitorId, int value)
    {
        lock (_stateLock)
        {
            _lastAppliedBrightness[monitorId] = value;
            _lastAppliedUtc[monitorId] = DateTime.UtcNow;
        }
    }

    public void ClearManualOverride(string monitorId)
    {
        lock (_stateLock)
        {
            _lastAppliedBrightness.Remove(monitorId);
            _lastAppliedUtc.Remove(monitorId);
        }
    }

    public void ResetAllTracking()
    {
        lock (_stateLock)
        {
            _lastAppliedBrightness.Clear();
            _lastAppliedUtc.Clear();
        }
    }

    private int? GetCurrentBrightness(string monitorId)
    {
        try
        {
            return monitorId.StartsWith("WMI:", StringComparison.Ordinal)
                ? _wmiDriver.GetBrightness(monitorId)
                : _ddcDriver.GetBrightness(monitorId);
        }
        catch
        {
            return null;
        }
    }

    private bool SetRawBrightness(string monitorId, int value)
    {
        try
        {
            return monitorId.StartsWith("WMI:", StringComparison.Ordinal)
                ? _wmiDriver.SetBrightness(monitorId, value)
                : _ddcDriver.SetBrightness(monitorId, value);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isMonitoringActive = false;

        if (_wmiBrightnessWatcher != null)
        {
            try
            {
                _wmiBrightnessWatcher.EventArrived -= OnWmiBrightnessEventArrived;
                _wmiBrightnessWatcher.Stop();
            }
            catch { }
            try { _wmiBrightnessWatcher.Dispose(); } catch { }
            _wmiBrightnessWatcher = null;
        }

        CancelAllFades();
        try { _wmiDriver.Dispose(); } catch { }
        try { _ddcDriver.Dispose(); } catch { }
    }
}