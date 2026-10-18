using System.Runtime.InteropServices;

namespace BluetoothBattery.Devices;

/// <summary>系统主音量（默认播放设备）。</summary>
internal readonly record struct SystemVolume
{
    /// <summary>系统里没有可用的播放设备 / 读取失败。</summary>
    public bool Available { get; init; }

    /// <summary>0~100。</summary>
    public int Percent { get; init; }

    public bool Muted { get; init; }

    public static SystemVolume Unavailable => new() { Available = false };
}

/// <summary>
/// 通过 Windows Core Audio（IMMDeviceEnumerator → IAudioEndpointVolume）读默认播放设备的
/// 主音量和静音状态。纯 COM 互操作，无第三方依赖；任意线程可调用，永不抛异常。
/// </summary>
internal static class SystemVolumeReader
{
    private const uint ClsCtxAll = 23;              // CLSCTX_ALL
    private const int EDataFlowRender = 0;          // eRender
    private const int ERoleConsole = 0;             // eConsole
    private const int SOk = 0;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    internal static SystemVolume Read()
    {
        object? enumeratorObject = null;
        object? deviceObject = null;
        object? volumeObject = null;
        try
        {
            var clsid = ClsidMmDeviceEnumerator;
            var iid = IidMmDeviceEnumerator;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var enumeratorRaw) != SOk
                || enumeratorRaw is not IMMDeviceEnumerator enumerator)
            {
                return SystemVolume.Unavailable;
            }

            enumeratorObject = enumeratorRaw;

            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out var device) != SOk
                || device is null)
            {
                return SystemVolume.Unavailable;
            }

            deviceObject = device;

            var volumeIid = IidAudioEndpointVolume;
            if (device.Activate(ref volumeIid, ClsCtxAll, IntPtr.Zero, out var volume) != SOk
                || volume is null)
            {
                return SystemVolume.Unavailable;
            }

            volumeObject = volume;

            if (volume.GetMasterVolumeLevelScalar(out var scalar) != SOk || volume.GetMute(out var muted) != SOk)
            {
                return SystemVolume.Unavailable;
            }

            var percent = (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);
            return new SystemVolume
            {
                Available = true,
                Percent = Math.Clamp(percent, 0, 100),
                Muted = muted,
            };
        }
        catch
        {
            // 没有声卡 / COM 被禁用 / 音频服务未启动：当作不可用，界面隐藏音量行
            return SystemVolume.Unavailable;
        }
        finally
        {
            if (volumeObject is not null) Marshal.ReleaseComObject(volumeObject);
            if (deviceObject is not null) Marshal.ReleaseComObject(deviceObject);
            if (enumeratorObject is not null) Marshal.ReleaseComObject(enumeratorObject);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    // ---- COM 接口（只声明到用得到的方法；未用到的排在后面，不影响调用位置） ----

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.Interface)] out IAudioEndpointVolume? ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
