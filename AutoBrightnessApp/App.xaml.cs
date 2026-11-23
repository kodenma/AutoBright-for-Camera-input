using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoBrightnessApp.Services;
using AutoBrightnessApp.Views;
using WinForms = System.Windows.Forms;

namespace AutoBrightnessApp;

public partial class App : Application
{
    private const string MutexName = "AutoBright_CameraInput_SingleInstance_Mutex";
    private const string ActivateEventName = "AutoBright_CameraInput_ActivateWindow_Event";

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid GUID_MONITOR_POWER_ON = new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private AutomationController? _controller;
    private MainWindow? _mainWindow;

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _contextMenu;
    private WinForms.ToolStripMenuItem? _openMenuItem;
    private WinForms.ToolStripMenuItem? _measureMenuItem;
    private WinForms.ToolStripMenuItem? _pauseMenuItem;
    private WinForms.ToolStripMenuItem? _resetOverrideMenuItem;
    private WinForms.ToolStripMenuItem? _exitMenuItem;

    private System.Drawing.Icon? _normalTrayIcon;
    private System.Drawing.Icon? _pausedTrayIcon;
    private IntPtr _normalTrayIconHandle = IntPtr.Zero;
    private IntPtr _pausedTrayIconHandle = IntPtr.Zero;

    private HwndSource? _powerMessageSource;
    private IntPtr _consoleDisplayNotifyHandle = IntPtr.Zero;
    private IntPtr _monitorPowerNotifyHandle = IntPtr.Zero;

    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 起動直後からバックグラウンド効率モード（LPE/Eコア優先）を有効化
        PowerEfficiencyService.SetProcessEcoQos(true);

        _instanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                existingEvent.Set();
            }
            catch { }

            Shutdown();
            Environment.Exit(0);
            return;
        }

        StartActivateListener();

        _controller = new AutomationController();
        await _controller.InitializeAsync();

        if (!_controller.Config.HasAcceptedDisclaimer)
        {
            var disclaimer = new DisclaimerWindow(isAboutMode: false);
            bool? agreed = disclaimer.ShowDialog();
            if (agreed != true && !_controller.Config.HasAcceptedDisclaimer)
            {
                ExitApplication();
                return;
            }

            _controller.Config.HasAcceptedDisclaimer = true;
            _controller.Config.Save();
        }

        SetupTrayIcon();
        SetupDisplayPowerNotifications();

        LocalizationService.LanguageChanged += UpdateTrayMenuLocalization;
        _controller.EnabledStateChanged += OnControllerEnabledStateChanged;

        _controller.StartLoop(runImmediately: true);

        bool isSilentStartup = e.Args.Any(a =>
            a.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-startup", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-minimized", StringComparison.OrdinalIgnoreCase));

        if (!isSilentStartup)
        {
            ShowSettingsWindow();
        }
    }

    private void StartActivateListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _ = Task.Run(() =>
        {
            PowerEfficiencyService.ApplyBackgroundThreadEfficiency();
            while (!_isExiting)
            {
                try
                {
                    if (_activateEvent.WaitOne(500) && !_isExiting)
                    {
                        Dispatcher.BeginInvoke(ShowSettingsWindow);
                    }
                }
                catch
                {
                    break;
                }
            }
        });
    }

    private void SetupTrayIcon()
    {
        _contextMenu = new WinForms.ContextMenuStrip();

        _openMenuItem = new WinForms.ToolStripMenuItem();
        _openMenuItem.Font = new System.Drawing.Font(_openMenuItem.Font, System.Drawing.FontStyle.Bold);
        _openMenuItem.Click += (_, _) => ShowSettingsWindow();

        _measureMenuItem = new WinForms.ToolStripMenuItem();
        _measureMenuItem.Click += async (_, _) =>
        {
            if (_controller != null)
            {
                await _controller.RunSingleCycleAsync(forceApply: true);
            }
        };

        _pauseMenuItem = new WinForms.ToolStripMenuItem();
        _pauseMenuItem.Click += (_, _) =>
        {
            if (_controller != null)
            {
                _controller.IsEnabled = !_controller.IsEnabled;
            }
        };

        _resetOverrideMenuItem = new WinForms.ToolStripMenuItem();
        _resetOverrideMenuItem.Click += (_, _) =>
        {
            _controller?.ResetAllManualOverrides();
        };

        _exitMenuItem = new WinForms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => ExitApplication();

        _contextMenu.Items.Add(_openMenuItem);
        _contextMenu.Items.Add(_measureMenuItem);
        _contextMenu.Items.Add(_pauseMenuItem);
        _contextMenu.Items.Add(_resetOverrideMenuItem);
        _contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenu.Items.Add(_exitMenuItem);

        // 通常アイコンと一時停止中（右下赤停止マーク付き）アイコンを事前生成してキャッシュ
        _normalTrayIcon = CreateTrayIcon(withStopBadge: false, out _normalTrayIconHandle);
        _pausedTrayIcon = CreateTrayIcon(withStopBadge: true, out _pausedTrayIconHandle);

        bool isEnabled = _controller?.IsEnabled ?? true;

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = isEnabled ? _normalTrayIcon : _pausedTrayIcon,
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        UpdateTrayMenuLocalization();
        UpdateTrayVisualState(isEnabled);

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowSettingsWindow();
            }
        };
    }

    private static System.Drawing.Icon CreateTrayIcon(bool withStopBadge, out IntPtr iconHandle)
    {
        iconHandle = IntPtr.Zero;
        try
        {
            var imageSource = AppIconGenerator.GetAppIcon();
            if (imageSource != null)
            {
                const int size = 32;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(imageSource, new Rect(0, 0, size, size));
                }

                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Position = 0;

                using var bmp = new System.Drawing.Bitmap(ms);

                if (withStopBadge)
                {
                    using var g = System.Drawing.Graphics.FromImage(bmp);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // 右下に小さな赤い停止マーク（白縁＋赤丸＋白い停止四角■）を描画
                    const float badgeSize = 14.5f;
                    const float badgeX = size - badgeSize - 0.5f;
                    const float badgeY = size - badgeSize - 0.5f;

                    using var redBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 220, 38, 38));
                    using var whitePen = new System.Drawing.Pen(System.Drawing.Color.White, 1.8f);
                    using var whiteBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);

                    g.FillEllipse(redBrush, badgeX, badgeY, badgeSize, badgeSize);
                    g.DrawEllipse(whitePen, badgeX, badgeY, badgeSize, badgeSize);

                    const float stopSquareSize = 5.5f;
                    float stopX = badgeX + (badgeSize - stopSquareSize) / 2.0f;
                    float stopY = badgeY + (badgeSize - stopSquareSize) / 2.0f;
                    g.FillRectangle(whiteBrush, stopX, stopY, stopSquareSize, stopSquareSize);
                }

                iconHandle = bmp.GetHicon();
                return System.Drawing.Icon.FromHandle(iconHandle);
            }
        }
        catch { }

        return System.Drawing.SystemIcons.Application;
    }

    private void UpdateTrayVisualState(bool isEnabled)
    {
        if (_notifyIcon == null) return;

        _notifyIcon.Icon = isEnabled
            ? (_normalTrayIcon ?? System.Drawing.SystemIcons.Application)
            : (_pausedTrayIcon ?? _normalTrayIcon ?? System.Drawing.SystemIcons.Application);

        string tip = isEnabled
            ? "AutoBright for Camera input"
            : LocalizationService.Get("TrayTooltipPaused");

        // NotifyIcon.Text は最大63文字制限があるため安全に切り詰める
        _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void UpdateTrayMenuLocalization()
    {
        if (_openMenuItem == null ||
            _measureMenuItem == null ||
            _pauseMenuItem == null ||
            _resetOverrideMenuItem == null ||
            _exitMenuItem == null)
        {
            return;
        }

        _openMenuItem.Text = LocalizationService.Get("TrayOpen");
        _measureMenuItem.Text = LocalizationService.Get("TrayMeasureNow");
        _pauseMenuItem.Text = LocalizationService.Get("TrayPause");
        _resetOverrideMenuItem.Text = LocalizationService.Get("TrayResetOverride");
        _exitMenuItem.Text = LocalizationService.Get("TrayExit");

        if (_controller != null)
        {
            _pauseMenuItem.Checked = !_controller.IsEnabled;
            UpdateTrayVisualState(_controller.IsEnabled);
        }
    }

    private void OnControllerEnabledStateChanged(bool enabled)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting) return;

            if (_pauseMenuItem != null)
            {
                _pauseMenuItem.Checked = !enabled;
            }
            UpdateTrayVisualState(enabled);
        }, DispatcherPriority.Background);
    }

    private void ShowSettingsWindow()
    {
        if (_isExiting || _controller == null) return;

        PowerEfficiencyService.SetProcessEcoQos(false);

        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_controller);
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        _mainWindow.RefreshOnShow();

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void SetupDisplayPowerNotifications()
    {
        try
        {
            var parameters = new HwndSourceParameters("AutoBrightPowerSink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _powerMessageSource = new HwndSource(parameters);
            _powerMessageSource.AddHook(WndProc);

            Guid consoleGuid = GUID_CONSOLE_DISPLAY_STATE;
            Guid monitorGuid = GUID_MONITOR_POWER_ON;
            _consoleDisplayNotifyHandle = RegisterPowerSettingNotification(_powerMessageSource.Handle, ref consoleGuid, 0);
            _monitorPowerNotifyHandle = RegisterPowerSettingNotification(_powerMessageSource.Handle, ref monitorGuid, 0);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            try
            {
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE ||
                    setting.PowerSetting == GUID_MONITOR_POWER_ON)
                {
                    bool isDisplayOn = setting.Data != 0;
                    _controller?.OnDisplayPowerStateChanged(isDisplayOn);
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void CleanupPowerNotifications()
    {
        try
        {
            if (_consoleDisplayNotifyHandle != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_consoleDisplayNotifyHandle);
                _consoleDisplayNotifyHandle = IntPtr.Zero;
            }
            if (_monitorPowerNotifyHandle != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_monitorPowerNotifyHandle);
                _monitorPowerNotifyHandle = IntPtr.Zero;
            }
            if (_powerMessageSource != null)
            {
                _powerMessageSource.RemoveHook(WndProc);
                _powerMessageSource.Dispose();
                _powerMessageSource = null;
            }
        }
        catch { }
    }

    private void DisposeTrayResources()
    {
        try
        {
            if (_contextMenu != null)
            {
                _contextMenu.Hide();
                _contextMenu.Close(WinForms.ToolStripDropDownCloseReason.ItemClicked);
            }
        }
        catch { }

        try
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.ContextMenuStrip = null;
                _notifyIcon.Icon = null;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_contextMenu != null)
            {
                _contextMenu.Dispose();
                _contextMenu = null;
            }

            _normalTrayIcon?.Dispose();
            _normalTrayIcon = null;
            if (_normalTrayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_normalTrayIconHandle);
                _normalTrayIconHandle = IntPtr.Zero;
            }

            _pausedTrayIcon?.Dispose();
            _pausedTrayIcon = null;
            if (_pausedTrayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_pausedTrayIconHandle);
                _pausedTrayIconHandle = IntPtr.Zero;
            }
        }
        catch { }
    }

    public void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;

        LocalizationService.LanguageChanged -= UpdateTrayMenuLocalization;
        if (_controller != null)
        {
            _controller.EnabledStateChanged -= OnControllerEnabledStateChanged;
        }

        DisposeTrayResources();

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            CleanupPowerNotifications();

            try
            {
                _mainWindow?.Hide();
            }
            catch { }

            try
            {
                if (_controller != null)
                {
                    await _controller.ShutdownAsync();
                }
            }
            catch { }

            try
            {
                _activateEvent?.Dispose();
                _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
            }
            catch { }

            try
            {
                Shutdown();
            }
            catch { }

            Environment.Exit(0);
        }), DispatcherPriority.Background);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_isExiting)
        {
            _isExiting = true;
            LocalizationService.LanguageChanged -= UpdateTrayMenuLocalization;
            if (_controller != null)
            {
                _controller.EnabledStateChanged -= OnControllerEnabledStateChanged;
            }
            DisposeTrayResources();
            CleanupPowerNotifications();
            _controller?.Dispose();
        }
        base.OnExit(e);
    }
}