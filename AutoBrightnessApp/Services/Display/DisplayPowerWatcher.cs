using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AutoBrightnessApp.Services.Display;

/// <summary>
/// Windows のディスプレイ電源状態（画面オン / 画面オフ）およびスリープ復帰を監視するクラス
/// </summary>
public sealed class DisplayPowerWatcher : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    // モダンスタンバイ / Windows 10・11 標準のディスプレイ電源状態 GUID (0=Off, 1=On, 2=Dimmed)
    private static Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    // 従来のモニター電源状態 GUID (0=Off, 1=On)
    private static Guid GUID_MONITOR_POWER_ON = new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    private HwndSource? _hwndSource;
    private IntPtr _consoleDisplayNotifyHandle;
    private IntPtr _monitorPowerNotifyHandle;
    private bool _isDisplayOn = true;

    public bool IsDisplayOn => _isDisplayOn;
    public event Action<bool>? DisplayPowerChanged;

    public DisplayPowerWatcher()
    {
        var parameters = new HwndSourceParameters("AutoBrightDisplayPowerWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _consoleDisplayNotifyHandle = RegisterPowerSettingNotification(
            _hwndSource.Handle, ref GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_WINDOW_HANDLE);
        _monitorPowerNotifyHandle = RegisterPowerSettingNotification(
            _hwndSource.Handle, ref GUID_MONITOR_POWER_ON, DEVICE_NOTIFY_WINDOW_HANDLE);

        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
    }

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            UpdateDisplayState(false);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            UpdateDisplayState(true);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE ||
                setting.PowerSetting == GUID_MONITOR_POWER_ON)
            {
                int dataOffset = Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32();
                int state = Marshal.ReadInt32(lParam, dataOffset);
                // state == 0 が画面オフ、1 (On) および 2 (Dimmed) は画面オン扱い
                UpdateDisplayState(state != 0);
            }
        }
        return IntPtr.Zero;
    }

    private void UpdateDisplayState(bool isOn)
    {
        if (_isDisplayOn == isOn) return;
        _isDisplayOn = isOn;
        DisplayPowerChanged?.Invoke(isOn);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;

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

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);
}