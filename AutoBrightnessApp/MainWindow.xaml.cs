using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AutoBrightnessApp.Models;
using AutoBrightnessApp.Services;
using AutoBrightnessApp.Services.Camera;
using AutoBrightnessApp.Views;

namespace AutoBrightnessApp;

public partial class MainWindow : Window
{
    private readonly AutomationController _controller;
    private ObservableCollection<MonitorSetting> _monitorItems = new();
    private volatile AutomationStatus? _lastStatus;
    private volatile bool _isWindowVisible;
    private bool _isInitializing = true;
    private bool _isSyncingEnableState;

    public MainWindow(AutomationController controller)
    {
        _isInitializing = true;
        InitializeComponent();
        Icon = AppIconGenerator.GetAppIcon();

        _controller = controller;
        _controller.StatusUpdated += OnStatusUpdated;
        _controller.EnabledStateChanged += OnControllerEnabledStateChanged;
        LocalizationService.LanguageChanged += ApplyLocalization;

        IsVisibleChanged += (_, _) => _isWindowVisible = IsVisible;

        LoadUiFromConfig();
        ApplyLocalization();
        _isInitializing = false;
    }

    private void LoadUiFromConfig()
    {
        _isInitializing = true;

        MasterEnableSwitch.IsChecked = _controller.IsEnabled;
        UpdateMasterSwitchVisual(_controller.IsEnabled);

        LanguageCombo.ItemsSource = LocalizationService.SupportedLanguages;
        LanguageCombo.SelectedValue = LocalizationService.CurrentLanguage;
        StartupCheck.IsChecked = StartupService.IsStartupEnabled();

        CameraCombo.ItemsSource = _controller.AvailableCameras;
        CameraCombo.SelectedItem = _controller.AvailableCameras
            .FirstOrDefault(c => c.Id == _controller.Config.SelectedCameraId);

        RefreshFlickerCombo();

        ContinuousCheck.IsChecked = _controller.Config.ContinuousCaptureMode;
        DebugModeCheck.IsChecked = _controller.Config.DebugMode;
        IntervalBox.Text = _controller.Config.IntervalSeconds.ToString();
        UpdateIntervalEnabledState();

        InputFilterSlider.Value = Math.Clamp(_controller.Config.InputSmoothingLevel, 1, 10);
        OutputFilterSlider.Value = Math.Clamp(_controller.Config.OutputFadeLevel, 1, 10);
        InputFilterValueText.Text = ((int)InputFilterSlider.Value).ToString();
        OutputFilterValueText.Text = ((int)OutputFilterSlider.Value).ToString();

        _monitorItems = new ObservableCollection<MonitorSetting>(_controller.Config.MonitorSettings.Values);
        MonitorGrid.ItemsSource = _monitorItems;

        LoadSelectedCameraProfile();
        HistoryChart.UpdateHistory(_controller.RecentHistory);

        _isInitializing = false;
    }

    private void RefreshFlickerCombo()
    {
        if (FlickerCombo == null) return;
        string currentCode = _controller?.Config.PowerlineFrequencyMode ?? "Auto";
        FlickerCombo.ItemsSource = LocalizationService.GetFlickerOptions();
        FlickerCombo.SelectedValue = currentCode;
        if (FlickerCombo.SelectedIndex < 0)
        {
            FlickerCombo.SelectedValue = "Auto";
        }
    }

    private void ApplyLocalization()
    {
        if (_controller == null) return;

        FlowDirection = LocalizationService.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        Title = LocalizationService.Get("AppTitle");
        UpdateMasterSwitchVisual(_controller.IsEnabled);
        ExitAppBtn.Content = LocalizationService.Get("ExitAppBtn");
        LanguageLabel.Text = LocalizationService.Get("LanguageLabel");
        StartupCheck.Content = LocalizationService.Get("StartupCheck");
        StatusTitleText.Text = LocalizationService.Get("StatusTitle");
        MeasureNowBtn.Content = LocalizationService.Get("MeasureNowBtn");
        SectionCameraText.Text = LocalizationService.Get("SectionCamera");
        CameraLabelText.Text = LocalizationService.Get("CameraLabel");
        ResolutionLabelText.Text = LocalizationService.Get("ResolutionLabel");
        if (FlickerLabelText != null)
        {
            FlickerLabelText.Text = LocalizationService.Get("FlickerLabel");
        }
        RefreshFlickerCombo();
        ContinuousCheck.Content = LocalizationService.Get("ContinuousCheck");
        DebugModeCheck.Content = LocalizationService.Get("DebugModeCheck");
        IntervalLabelText.Text = LocalizationService.Get("IntervalLabel");
        InputFilterLabelText.Text = LocalizationService.Get("InputFilterLabel");
        OutputFilterLabelText.Text = LocalizationService.Get("OutputFilterLabel");
        SectionCurveText.Text = LocalizationService.Get("SectionCurve");
        ResetCurveBtn.Content = LocalizationService.Get("ResetCurveBtn");
        CurveHintText.Text = LocalizationService.Get("CurveHint");
        SectionHistoryText.Text = LocalizationService.Get("SectionHistory");
        SectionMonitorsText.Text = LocalizationService.Get("SectionMonitors");
        ColEnabled.Header = LocalizationService.Get("ColEnabled");
        ColDisplayName.Header = LocalizationService.Get("ColDisplayName");
        ColOffset.Header = LocalizationService.Get("ColOffset");
        AboutBtn.Content = LocalizationService.Get("AboutBtn");
        SaveBtn.Content = LocalizationService.Get("SaveBtn");

        CurveEditor.Redraw();
        HistoryChart.Redraw();
        RenderStatusText();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ExitApplication();
        }
        else
        {
            Application.Current.Shutdown();
        }
    }

    private void MasterEnableSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingEnableState || _controller == null) return;

        bool enabled = MasterEnableSwitch.IsChecked == true;
        _controller.IsEnabled = enabled;
        UpdateMasterSwitchVisual(enabled);
    }

    private void OnControllerEnabledStateChanged(bool enabled)
    {
        if (!_isWindowVisible) return;

        Dispatcher.BeginInvoke(() =>
        {
            _isSyncingEnableState = true;
            MasterEnableSwitch.IsChecked = enabled;
            UpdateMasterSwitchVisual(enabled);
            _isSyncingEnableState = false;
        }, DispatcherPriority.Background);
    }

    private void UpdateMasterSwitchVisual(bool enabled)
    {
        if (MasterEnableSwitch == null || MeasureNowBtn == null || MasterEnableBanner == null) return;

        MasterEnableSwitch.Content = LocalizationService.Get(enabled ? "MasterEnableOn" : "MasterEnableOff");
        MeasureNowBtn.IsEnabled = enabled;

        if (enabled)
        {
            MasterEnableBanner.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
            MasterEnableBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254));
        }
        else
        {
            MasterEnableBanner.Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
            MasterEnableBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
        }
    }

    private void ContinuousCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateIntervalEnabledState();
    }

    private void UpdateIntervalEnabledState()
    {
        if (ContinuousCheck == null || IntervalBox == null || IntervalLabelText == null) return;

        bool isContinuous = ContinuousCheck.IsChecked == true;
        IntervalBox.IsEnabled = !isContinuous;
        IntervalLabelText.IsEnabled = !isContinuous;
    }

    private void FilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (InputFilterValueText != null && InputFilterSlider != null)
            InputFilterValueText.Text = ((int)InputFilterSlider.Value).ToString();

        if (OutputFilterValueText != null && OutputFilterSlider != null)
            OutputFilterValueText.Text = ((int)OutputFilterSlider.Value).ToString();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _controller == null || LanguageCombo.SelectedValue is not string langCode) return;

        LocalizationService.SetLanguage(langCode);
        _controller.Config.LanguageCode = langCode;
        _controller.Config.Save();
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        StartupService.SetStartupEnabled(StartupCheck.IsChecked == true);
    }

    private void LoadSelectedCameraProfile()
    {
        if (_controller == null || CameraCombo.SelectedItem is not CameraDeviceItem cam) return;

        var profile = _controller.Config.GetOrCreateProfile(cam.Id, cam.DisplayName);
        CurveEditor.SetPoints(profile.Curve);

        ResolutionCombo.ItemsSource = cam.SupportedResolutions;
        ResolutionCombo.SelectedItem = _controller.GetSelectedResolutionForCamera(cam);
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        LoadSelectedCameraProfile();
    }

    private void ResetCurve_Click(object sender, RoutedEventArgs e)
    {
        if (_controller == null || CameraCombo.SelectedItem is not CameraDeviceItem cam) return;

        var defaultProfile = CameraProfile.CreateDefault(cam.Id, cam.DisplayName);
        CurveEditor.SetPoints(defaultProfile.Curve);
    }

    private void OnStatusUpdated(AutomationStatus status)
    {
        // 非表示中（バックグラウンド常駐中）は最新状態だけ保持し、UIスレッドを一切起こさない
        _lastStatus = status;
        if (!_isWindowVisible) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isWindowVisible) return;

            RenderStatusText();
            HistoryChart.UpdateHistory(_controller.RecentHistory);

            var latest = _lastStatus;
            if (latest?.FilteredScore is double score)
            {
                CurveEditor.SetCurrentMeasuredScore(score);
            }
        }, DispatcherPriority.Background);
    }

    public void RefreshOnShow()
    {
        _isWindowVisible = true;

        _isSyncingEnableState = true;
        MasterEnableSwitch.IsChecked = _controller.IsEnabled;
        UpdateMasterSwitchVisual(_controller.IsEnabled);
        _isSyncingEnableState = false;

        RenderStatusText();
        HistoryChart.UpdateHistory(_controller.RecentHistory);
        if (_lastStatus?.FilteredScore is double score)
        {
            CurveEditor.SetCurrentMeasuredScore(score);
        }
    }

    private void RenderStatusText()
    {
        if (StatusText == null) return;

        var status = _lastStatus;
        if (status == null)
        {
            StatusText.Text = LocalizationService.Get("StatusWaiting");
            return;
        }

        string note = LocalizationService.Format(status.NoteKey, status.NoteArgs);
        if (status.Measurement is { } m)
        {
            string flickerInfo = LocalizationService.FormatAppliedFlickerStatus(m.AppliedPowerlineFrequency);

            StatusText.Text = LocalizationService.Format(
                "StatusFormat",
                status.Timestamp,
                m.CameraName,
                m.Width,
                m.Height,
                status.FilteredScore ?? m.NormalizedScore,
                m.NormalizedScore,
                m.RawMedianY,
                m.DarkDampingFactor,
                m.HighLightMultiplier,
                status.TargetBaseBrightness ?? 0,
                note,
                flickerInfo,
                m.BrightRatio * 100.0
            );
        }
        else
        {
            StatusText.Text = $"[{status.Timestamp:HH:mm:ss}] {note}";
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var aboutDialog = new DisclaimerWindow(isAboutMode: true)
        {
            Owner = this
        };
        aboutDialog.ShowDialog();

        _isInitializing = true;
        LanguageCombo.SelectedValue = LocalizationService.CurrentLanguage;
        _controller.Config.LanguageCode = LocalizationService.CurrentLanguage;
        _controller.Config.Save();
        _isInitializing = false;
    }

    private async void MeasureNow_Click(object sender, RoutedEventArgs e)
    {
        SaveToConfig();
        await _controller.RunSingleCycleAsync(forceApply: true);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveBtn.IsEnabled = false;
        try
        {
            SaveToConfig();
            _controller.Config.Save();
            DebugLogger.Log("CONFIG_SAVED", $"設定保存完了: CameraId={_controller.Config.SelectedCameraId}, Flicker={_controller.Config.PowerlineFrequencyMode}, Continuous={_controller.Config.ContinuousCaptureMode}, DebugMode={_controller.Config.DebugMode}");

            await _controller.RestartCameraAndApplyAsync();

            MessageBox.Show(LocalizationService.Get("SavedMsg"), "AutoBright for Camera input", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private void SaveToConfig()
    {
        if (CameraCombo.SelectedItem is CameraDeviceItem camera)
        {
            _controller.Config.SelectedCameraId = camera.Id;
            var profile = _controller.Config.GetOrCreateProfile(camera.Id, camera.DisplayName);

            if (ResolutionCombo.SelectedItem is CameraResolutionOption selectedRes)
            {
                profile.PreferredWidth = selectedRes.Width;
                profile.PreferredHeight = selectedRes.Height;
            }

            profile.Curve = CurveEditor.Points
                .OrderBy(p => p.InputScore)
                .Select(p => new BrightnessPoint(p.InputScore, Math.Clamp(p.TargetBrightness, 0, 100)))
                .ToList();
        }

        if (FlickerCombo?.SelectedValue is string flickerCode)
        {
            _controller.Config.PowerlineFrequencyMode = flickerCode;
        }

        _controller.Config.ContinuousCaptureMode = ContinuousCheck.IsChecked == true;
        _controller.Config.DebugMode = DebugModeCheck.IsChecked == true;
        DebugLogger.IsEnabled = _controller.Config.DebugMode;

        if (int.TryParse(IntervalBox.Text, out int interval))
        {
            _controller.Config.IntervalSeconds = Math.Clamp(interval, 5, 300);
        }

        _controller.Config.InputSmoothingLevel = (int)InputFilterSlider.Value;
        _controller.Config.OutputFadeLevel = (int)OutputFilterSlider.Value;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        _isWindowVisible = false;
        Hide();
        PowerEfficiencyService.SetProcessEcoQos(true);
    }
}