using AutoBrightnessApp.Models;
using AutoBrightnessApp.Services.Camera;
using AutoBrightnessApp.Services.Display;

namespace AutoBrightnessApp.Services;

public record HistoryEntry(DateTime Timestamp, double NormalizedScore, int AppliedBrightness);

public record AutomationStatus(
    DateTime Timestamp,
    IlluminanceMeasurement? Measurement,
    double? FilteredScore,
    int? TargetBaseBrightness,
    string NoteKey,
    object[] NoteArgs
);

public sealed class AutomationController : IDisposable
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(5);
    private static readonly double[] TimeConstantsSec = [0.0, 1.5, 3.0, 5.0, 8.0, 12.0, 18.0, 26.0, 38.0, 55.0];

    private readonly object _historyLock = new();
    private readonly List<HistoryEntry> _history = new();
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    public AppConfig Config { get; private set; }
    public CameraIlluminanceService CameraService { get; } = new();
    public BrightnessManager BrightnessManager { get; } = new();

    public IReadOnlyList<CameraDeviceItem> AvailableCameras { get; private set; } = [];
    public IReadOnlyList<MonitorInfo> AvailableMonitors { get; private set; } = [];

    public IReadOnlyList<HistoryEntry> RecentHistory
    {
        get
        {
            lock (_historyLock)
            {
                PruneOldHistoryLocked();
                return _history.ToList();
            }
        }
    }

    private volatile bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetEnabled(value, pausedByManualChange: false);
    }

    private volatile bool _isPausedByManualOverride;
    public bool IsPausedByManualOverride => _isPausedByManualOverride;

    private volatile bool _isDisplayOff;
    public bool IsDisplayOff => _isDisplayOff;

    public event Action<bool>? EnabledStateChanged;
    public event Action<AutomationStatus>? StatusUpdated;

    private CancellationTokenSource? _loopCts;
    private ContinuousCameraSession? _continuousSession;
    private double? _filteredScore;
    private DateTime _lastFilterTime = DateTime.MinValue;
    private double? _lastAppliedFilteredScore;
    private DateTime _lastExternalCheckUtc = DateTime.MinValue;

    public AutomationController()
    {
        Config = AppConfig.Load();
        DebugLogger.IsEnabled = Config.DebugMode;
        LocalizationService.SetLanguage(Config.LanguageCode);

        BrightnessManager.ConfigureMonitoring(true, IsMonitorEnabled);
        BrightnessManager.ManualBrightnessChanged += OnManualBrightnessChanged;

        DebugLogger.Log("APP_INIT", $"設定ロード完了: Language={Config.LanguageCode}, ContinuousMode={Config.ContinuousCaptureMode}, Flicker={Config.PowerlineFrequencyMode}, Interval={Config.IntervalSeconds}s, DebugMode={Config.DebugMode}");
    }

    public async Task InitializeAsync()
    {
        DebugLogger.Log("DEVICE_INIT", "カメラおよびディスプレイの検出を開始します。");
        AvailableCameras = await CameraService.DiscoverColorCamerasAsync();
        AvailableMonitors = BrightnessManager.RefreshAllMonitors();

        if (string.IsNullOrEmpty(Config.SelectedCameraId) && AvailableCameras.Count > 0)
        {
            Config.SelectedCameraId = AvailableCameras[0].Id;
        }

        foreach (var cam in AvailableCameras)
        {
            var profile = Config.GetOrCreateProfile(cam.Id, cam.DisplayName);
            if ((profile.PreferredWidth == null || profile.PreferredHeight == null) && cam.DefaultResolution != null)
            {
                profile.PreferredWidth = cam.DefaultResolution.Width;
                profile.PreferredHeight = cam.DefaultResolution.Height;
            }
            DebugLogger.Log("CAMERA_FOUND", $"検出カメラ: {cam.DisplayName} (Id={cam.Id}), 対応解像度数={cam.SupportedResolutions.Count}, 初期解像度={cam.DefaultResolution}");
        }

        foreach (var mon in AvailableMonitors)
        {
            if (!Config.MonitorSettings.ContainsKey(mon.Id))
            {
                Config.MonitorSettings[mon.Id] = new MonitorSetting { MonitorId = mon.Id, Name = mon.Name };
            }
            DebugLogger.Log("MONITOR_FOUND", $"検出モニター: {mon.Name} (Id={mon.Id}, Type={mon.ConnectionType}, CurrentBrightness={mon.CurrentBrightness}%)");
        }
    }

    private bool IsMonitorEnabled(string monitorId)
    {
        return !Config.MonitorSettings.TryGetValue(monitorId, out var setting) || setting.IsEnabled;
    }

    private void OnManualBrightnessChanged(string monitorId, int detectedBrightness)
    {
        if (!_isEnabled || _isDisplayOff) return;

        DebugLogger.Log("MANUAL_PAUSE", $"手動輝度変更を検知したため自動調整を一時停止します: Monitor={monitorId}, Brightness={detectedBrightness}%");
        SetEnabled(false, pausedByManualChange: true);
    }

    public CameraResolutionOption? GetSelectedResolutionForCamera(CameraDeviceItem camera)
    {
        var profile = Config.GetOrCreateProfile(camera.Id, camera.DisplayName);
        if (profile.PreferredWidth is int w && profile.PreferredHeight is int h)
        {
            var matched = camera.SupportedResolutions.FirstOrDefault(r => r.Width == w && r.Height == h);
            if (matched != null) return matched;
        }
        return camera.DefaultResolution;
    }

    public void OnDisplayPowerStateChanged(bool isDisplayOn)
    {
        bool isOff = !isDisplayOn;
        if (_isDisplayOff == isOff) return;
        _isDisplayOff = isOff;

        DebugLogger.Log("DISPLAY_POWER", isOff ? "画面オフを検知しました。処理を停止しカメラを解放します。" : "画面オン復帰を検知しました。");

        if (isOff)
        {
            BrightnessManager.ConfigureMonitoring(false);
            BrightnessManager.CancelAllFades();
            _ = HandleDisplayTurnedOffAsync();
        }
        else
        {
            if (_isEnabled)
            {
                BrightnessManager.ConfigureMonitoring(true, IsMonitorEnabled);
                _ = HandleDisplayTurnedOnAsync();
            }
            else
            {
                string key = _isPausedByManualOverride ? "StatePausedManual" : "StateDisabled";
                StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, key, []));
            }
        }
    }

    private async Task HandleDisplayTurnedOffAsync()
    {
        await _cycleLock.WaitAsync();
        try
        {
            if (_continuousSession != null)
            {
                await _continuousSession.DisposeAsync();
                _continuousSession = null;
            }
            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, "StateDisplayOff", []));
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    private async Task HandleDisplayTurnedOnAsync()
    {
        await Task.Delay(300);
        if (!_isDisplayOff && _isEnabled)
        {
            await RunSingleCycleAsync(forceApply: true);
        }
    }

    private void SetEnabled(bool enabled, bool pausedByManualChange)
    {
        if (_isEnabled == enabled)
        {
            if (!enabled && pausedByManualChange && !_isPausedByManualOverride)
            {
                _isPausedByManualOverride = true;
                StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, "StatePausedManual", []));
            }
            return;
        }

        _isEnabled = enabled;
        _isPausedByManualOverride = !enabled && pausedByManualChange;

        DebugLogger.Log("ENABLE_STATE", $"自動輝度調整の有効状態変更: IsEnabled={enabled}, ManualPause={_isPausedByManualOverride}");

        if (!enabled)
        {
            BrightnessManager.ConfigureMonitoring(false);
            BrightnessManager.CancelAllFades();
            EnabledStateChanged?.Invoke(false);
            _ = ReleaseCameraOnDisableAsync(_isPausedByManualOverride ? "StatePausedManual" : "StateDisabled");
        }
        else
        {
            BrightnessManager.ResetAllTracking();
            BrightnessManager.ConfigureMonitoring(!_isDisplayOff, IsMonitorEnabled);
            _lastAppliedFilteredScore = null;
            EnabledStateChanged?.Invoke(true);

            if (!_isDisplayOff)
            {
                _ = RunSingleCycleAsync(forceApply: true);
            }
        }
    }

    private async Task ReleaseCameraOnDisableAsync(string statusKey)
    {
        await _cycleLock.WaitAsync();
        try
        {
            if (_continuousSession != null)
            {
                await _continuousSession.DisposeAsync();
                _continuousSession = null;
            }
            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, statusKey, []));
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    public void StartLoop(bool runImmediately = true)
    {
        CancelLoopOnly();
        _loopCts = new CancellationTokenSource();
        DebugLogger.Log("LOOP_START", $"計測ループ開始 (runImmediately={runImmediately})");
        _ = Task.Run(() => RunLoopAsync(runImmediately, _loopCts.Token));
    }

    private void CancelLoopOnly()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
    }

    public async Task RestartCameraAndApplyAsync()
    {
        DebugLogger.Log("CAMERA_RESTART", "設定適用に伴いカメラセッションの再初期化を実行します。");
        CancelLoopOnly();

        await _cycleLock.WaitAsync();
        try
        {
            if (_continuousSession != null)
            {
                await _continuousSession.DisposeAsync();
                _continuousSession = null;
            }

            _filteredScore = null;
            _lastAppliedFilteredScore = null;

            // 全カメラ再列挙は高負荷なため、未検出または選択カメラ不在時のみ実行する
            if (AvailableCameras.Count == 0 ||
                !AvailableCameras.Any(c => c.Id == Config.SelectedCameraId))
            {
                AvailableCameras = await CameraService.DiscoverColorCamerasAsync();
            }

            AvailableMonitors = BrightnessManager.RefreshAllMonitors();

            await Task.Delay(200);
        }
        finally
        {
            _cycleLock.Release();
        }

        bool canRun = _isEnabled && !_isDisplayOff;
        StartLoop(runImmediately: canRun);

        if (!_isEnabled)
        {
            string key = _isPausedByManualOverride ? "StatePausedManual" : "StateDisabled";
            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, null, null, key, []));
        }
        else if (_isDisplayOff)
        {
            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, null, null, "StateDisplayOff", []));
        }
    }

    private async Task RunLoopAsync(bool runImmediately, CancellationToken ct)
    {
        PowerEfficiencyService.ApplyBackgroundThreadEfficiency();

        if (runImmediately && _isEnabled && !_isDisplayOff)
        {
            await RunSingleCycleAsync(forceApply: true, ct);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int delayMs = Config.ContinuousCaptureMode
                    ? 500
                    : Math.Clamp(Config.IntervalSeconds, 5, 300) * 1000;

                await Task.Delay(delayMs, ct);

                if (_isEnabled && !_isDisplayOff)
                {
                    await RunSingleCycleAsync(forceApply: false, ct);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunSingleCycleAsync(bool forceApply = true, CancellationToken ct = default)
    {
        if (!_isEnabled)
        {
            string key = _isPausedByManualOverride ? "StatePausedManual" : "StateDisabled";
            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, key, []));
            return;
        }

        if (_isDisplayOff)
        {
            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, "StateDisplayOff", []));
            return;
        }

        if (!await _cycleLock.WaitAsync(0, ct))
        {
            if (!forceApply) return;
            await _cycleLock.WaitAsync(ct);
        }

        try
        {
            if (!_isEnabled || _isDisplayOff) return;

            // 外部モニター（DDC/CI）の手動変更チェック（I2C負荷を抑えるため最小3秒間隔）
            if (!forceApply && (DateTime.UtcNow - _lastExternalCheckUtc).TotalSeconds >= 3.0)
            {
                _lastExternalCheckUtc = DateTime.UtcNow;
                var enabledIds = AvailableMonitors.Select(m => m.Id).Where(IsMonitorEnabled);
                if (BrightnessManager.CheckExternalManualOverride(enabledIds, includeWmi: false))
                {
                    return;
                }
            }

            if (!_isEnabled || _isDisplayOff) return;

            var camera = AvailableCameras.FirstOrDefault(c => c.Id == Config.SelectedCameraId);
            if (camera == null)
            {
                DebugLogger.Log("CYCLE_SKIP", "選択されたカメラが見つかりません。");
                StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, null, null, "StateNoCamera", []));
                return;
            }

            var targetRes = GetSelectedResolutionForCamera(camera);
            string powerlineMode = Config.PowerlineFrequencyMode ?? "Auto";
            IlluminanceMeasurement? measurement;

            if (Config.ContinuousCaptureMode)
            {
                bool needRestartSession = _continuousSession == null ||
                                          !_continuousSession.IsRunning ||
                                          _continuousSession.CameraId != camera.Id ||
                                          _continuousSession.TargetResolution != targetRes ||
                                          !string.Equals(_continuousSession.PowerlineMode, powerlineMode, StringComparison.OrdinalIgnoreCase);

                if (needRestartSession)
                {
                    if (_continuousSession != null)
                    {
                        await _continuousSession.DisposeAsync();
                        _continuousSession = null;
                        await Task.Delay(200, ct);
                    }

                    _continuousSession = new ContinuousCameraSession(camera, targetRes, powerlineMode);
                    if (!await _continuousSession.StartAsync())
                    {
                        await _continuousSession.DisposeAsync();
                        _continuousSession = null;
                        DebugLogger.Log("CYCLE_SKIP", $"連続撮影セッション開始失敗（カメラ使用中の可能性）: {camera.DisplayName}");
                        StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, "StateSkipped", []));
                        return;
                    }
                    await Task.Delay(450, ct);
                }

                measurement = await _continuousSession!.GetLatestMeasurementAsync();
            }
            else
            {
                if (_continuousSession != null)
                {
                    await _continuousSession.DisposeAsync();
                    _continuousSession = null;
                    await Task.Delay(200, ct);
                }

                measurement = await CameraService.MeasureOnceAsync(camera, targetRes, powerlineMode, ct);
            }

            if (!_isEnabled || _isDisplayOff) return;

            if (measurement == null)
            {
                DebugLogger.Log("CYCLE_SKIP", $"フレーム取得失敗またはカメラ使用中スキップ: {camera.DisplayName}");
                StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, null, _filteredScore, null, "StateSkipped", []));
                return;
            }

            double smoothedScore = ApplyInputDampingFilter(measurement.NormalizedScore, forceApply);

            var profile = Config.GetOrCreateProfile(camera.Id, camera.DisplayName);
            int baseTarget = (int)Math.Round(ErgonomicCurveInterpolator.Evaluate(profile.Curve, smoothedScore));

            RecordHistoryIfDue(smoothedScore, baseTarget);

            string imgLogStr = measurement.SavedImageFileName != null
                ? $", 保存画像={measurement.SavedImageFileName}"
                : "";

            string visualLogStr =
                $"カメラ={measurement.CameraName} ({measurement.Width}x{measurement.Height}), " +
                $"RawY={measurement.RawMedianY:F1}, LinearY={measurement.LinearBaseLuma:F1}, " +
                $"暗所補正={measurement.DarkDampingFactor:F2}x, 高照度補正={measurement.HighLightMultiplier:F2}x, " +
                $"純ノイズ={measurement.NoiseLevel:F2}, 正規化ノイズ={measurement.NormalizedNoise:F2}, " +
                $"暗部率={measurement.DarkRatio:P1}, 明部率={measurement.BrightRatio:P1}, 飽和率={measurement.ClippedRatio:P1}, " +
                $"フリッカー={measurement.AppliedPowerlineFrequency}";

            if (!forceApply && _lastAppliedFilteredScore is double prevScore && prevScore > 0)
            {
                double changeRatio = Math.Abs(smoothedScore - prevScore) / prevScore * 100.0;
                if (changeRatio < Config.HysteresisPercent)
                {
                    DebugLogger.Log(
                        "MEASUREMENT_HYSTERESIS",
                        $"{visualLogStr}, 生スコア={measurement.NormalizedScore:F2}, フィルター後スコア={smoothedScore:F2}, 目標輝度={baseTarget}%, 変化率={changeRatio:F2}% (<閾値{Config.HysteresisPercent:F1}%) のため維持{imgLogStr}");

                    StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, measurement, smoothedScore, baseTarget, "StateHysteresis", [changeRatio]));
                    return;
                }
            }

            _lastAppliedFilteredScore = smoothedScore;

            DebugLogger.Log(
                "MEASUREMENT_APPLIED",
                $"{visualLogStr}, 生スコア={measurement.NormalizedScore:F2}, フィルター後スコア={smoothedScore:F2}, 基準目標輝度={baseTarget}%{imgLogStr}");

            foreach (var mon in AvailableMonitors)
            {
                if (!IsMonitorEnabled(mon.Id))
                {
                    DebugLogger.Log("MONITOR_SKIP", $"モニター無効設定のためスキップ: {mon.Name} ({mon.Id})");
                    continue;
                }

                int offset = Config.MonitorSettings.TryGetValue(mon.Id, out var setting) ? setting.BrightnessOffset : 0;
                int finalTarget = Math.Clamp(baseTarget + offset, 0, 100);
                DebugLogger.Log("MONITOR_SET", $"モニター輝度適用開始: {mon.Name} ({mon.Id}), 基準={baseTarget}%, オフセット={offset:+#;-#;0}%, 最終目標={finalTarget}%, フェードLv={Config.OutputFadeLevel}");
                BrightnessManager.StartFadeToBrightness(mon.Id, finalTarget, Config.OutputFadeLevel, ct);
            }

            StatusUpdated?.Invoke(new AutomationStatus(DateTime.Now, measurement, smoothedScore, baseTarget, "StateUpdated", []));
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    private double ApplyInputDampingFilter(double rawScore, bool resetFilter)
    {
        DateTime now = DateTime.UtcNow;
        int levelIdx = Math.Clamp(Config.InputSmoothingLevel, 1, 10) - 1;
        double tau = TimeConstantsSec[levelIdx];

        if (resetFilter || _filteredScore == null || tau <= 0.0)
        {
            _filteredScore = rawScore;
            _lastFilterTime = now;
            return rawScore;
        }

        double dt = Math.Clamp((now - _lastFilterTime).TotalSeconds, 0.3, 60.0);
        double alpha = 1.0 - Math.Exp(-dt / tau);
        _filteredScore = _filteredScore.Value + alpha * (rawScore - _filteredScore.Value);
        _lastFilterTime = now;

        return _filteredScore.Value;
    }

    private void RecordHistoryIfDue(double score, int brightness)
    {
        DateTime now = DateTime.Now;
        lock (_historyLock)
        {
            if (_history.Count == 0 || (now - _history[^1].Timestamp).TotalSeconds >= 1.5)
            {
                _history.Add(new HistoryEntry(now, score, brightness));
                PruneOldHistoryLocked();
            }
        }
    }

    private void PruneOldHistoryLocked()
    {
        DateTime cutoff = DateTime.Now - HistoryWindow;
        _history.RemoveAll(h => h.Timestamp < cutoff);
    }

    public void ResetAllManualOverrides()
    {
        DebugLogger.Log("OVERRIDE_RESET", "手動変更による一時停止を解除し、自動調整を再開します。");
        BrightnessManager.ResetAllTracking();
        _lastAppliedFilteredScore = null;
        _filteredScore = null;

        if (!_isEnabled)
        {
            SetEnabled(true, pausedByManualChange: false);
        }
        else if (!_isDisplayOff)
        {
            _ = RunSingleCycleAsync(forceApply: true);
        }
    }

    public async Task ShutdownAsync()
    {
        DebugLogger.Log("APP_EXIT", "アプリケーションの終了処理を開始します。");
        BrightnessManager.ManualBrightnessChanged -= OnManualBrightnessChanged;
        CancelLoopOnly();
        BrightnessManager.CancelAllFades();

        if (_continuousSession != null)
        {
            var session = _continuousSession;
            _continuousSession = null;
            try
            {
                await Task.WhenAny(session.DisposeAsync().AsTask(), Task.Delay(1500));
            }
            catch { }
        }

        try
        {
            BrightnessManager.Dispose();
        }
        catch { }

        DebugLogger.Log("APP_EXIT", "アプリケーションの終了処理が完了しました。");
    }

    public void Dispose()
    {
        BrightnessManager.ManualBrightnessChanged -= OnManualBrightnessChanged;
        CancelLoopOnly();
        if (_continuousSession != null)
        {
            var session = _continuousSession;
            _continuousSession = null;
            _ = Task.Run(async () =>
            {
                try { await session.DisposeAsync(); } catch { }
            });
        }

        try { BrightnessManager.Dispose(); } catch { }
    }
}