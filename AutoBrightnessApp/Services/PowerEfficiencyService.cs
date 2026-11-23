using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoBrightnessApp.Services;

public static class PowerEfficiencyService
{
    private const int ProcessPowerThrottling = 4;
    private const int ThreadPowerThrottling = 3;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint THREAD_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref POWER_THROTTLING_STATE ProcessInformation,
        int ProcessInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadInformation(
        IntPtr hThread,
        int ThreadInformationClass,
        ref POWER_THROTTLING_STATE ThreadInformation,
        int ThreadInformationSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    public static bool IsEcoQosEnabled { get; private set; }

    /// <summary>
    /// プロセス全体の EcoQoS（Windows 11 効率モード）を有効/無効化し、
    /// バックグラウンド時に LPEコア / Eコア へ優先割り当てさせる
    /// </summary>
    public static void SetProcessEcoQos(bool enable)
    {
        try
        {
            var state = new POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            using var proc = Process.GetCurrentProcess();
            if (SetProcessInformation(proc.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<POWER_THROTTLING_STATE>()))
            {
                IsEcoQosEnabled = enable;
                DebugLogger.Log("POWER_QOS", $"プロセス EcoQoS (LPE/Eコア優先) を {(enable ? "有効化" : "解除")} しました。");
            }
        }
        catch { }
    }

    /// <summary>
    /// 現在のワーカースレッドに対して EcoQoS と低優先度を設定する
    /// </summary>
    public static void ApplyBackgroundThreadEfficiency()
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            var state = new POWER_THROTTLING_STATE
            {
                Version = THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED
            };

            SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref state, Marshal.SizeOf<POWER_THROTTLING_STATE>());
        }
        catch { }
    }
}