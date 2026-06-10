using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Reframe.Core;

/// <summary>
/// 按进程 PID 静音/取消静音(对标 BG 的"后台静音")。WASAPI 链路:
/// IMMDeviceEnumerator → 默认渲染端点 IMMDevice → 激活 IAudioSessionManager2 →
/// 枚举会话(IAudioSessionEnumerator)→ 每个 IAudioSessionControl2.GetProcessId 找到目标 →
/// QueryInterface 出 ISimpleAudioVolume → SetMute。
///
/// 说明:只遍历"默认渲染端点"的会话(够覆盖游戏外放;若用户切了输出设备或多端点同时出声,
/// 个别会话可能漏掉——后台静音是体验项,不追求 100%)。所有调用都吞掉异常/失败 HRESULT:
/// 静音失败不应影响窗口接管主流程。
/// </summary>
internal static class AudioMute
{
    /// <summary>把指定进程的音频会话设为静音/取消静音。失败静默返回。</summary>
    public static void SetMuteByPid(uint pid, bool mute)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? managerObj = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device is null)
                return;

            var iidMgr = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out managerObj) != 0 || managerObj is null)
                return;
            manager = (IAudioSessionManager2)managerObj;

            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null)
                return;
            if (sessions.GetCount(out int count) != 0)
                return;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctl = null;
                IAudioSessionControl2? ctl2 = null;
                ISimpleAudioVolume? vol = null;
                try
                {
                    if (sessions.GetSession(i, out ctl) != 0 || ctl is null) continue;
                    // QI 出 IAudioSessionControl2 / ISimpleAudioVolume 各产生独立 RCW,均需在 finally 释放。
                    ctl2 = ctl as IAudioSessionControl2;
                    if (ctl2 is null) continue;
                    if (ctl2.GetProcessId(out uint sessionPid) != 0) continue;
                    if (sessionPid != pid) continue;

                    vol = ctl as ISimpleAudioVolume;
                    if (vol != null)
                    {
                        Guid noEvent = Guid.Empty;
                        int hr = vol.SetMute(mute, ref noEvent);
                        if (hr != 0)
                            Debug.WriteLine($"[AudioMute] SetMute(pid={pid}, mute={mute}) hr=0x{hr:X8}");
                    }
                    // 不 break:同一进程可能有多个会话,全设一遍
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioMute] session[{i}] pid={pid} 异常: {ex.Message}");
                }
                finally
                {
                    if (vol != null) Marshal.ReleaseComObject(vol);
                    if (ctl2 != null) Marshal.ReleaseComObject(ctl2);
                    if (ctl != null) Marshal.ReleaseComObject(ctl);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioMute] pid={pid} COM 链路失败: {ex.Message}"); }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
            if (manager != null) Marshal.ReleaseComObject(manager);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private const uint CLSCTX_ALL = 0x17;

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    // 以下接口方法必须按 vtable 顺序声明,且 IUnknown 三方法由 ComImport 隐含,不写。
    // 全部 PreserveSig:自己判 HRESULT(0 = S_OK)。

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);
        // 其余方法用不到,省略会改变 vtable → 必须补齐占位
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager(基)的两方法在前,必须占位
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr vol);
        // IAudioSessionManager2 自身
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? enumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl? session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // 继承 IAudioSessionControl 的 9 个方法,必须全部占位
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
        // IAudioSessionControl2 自身
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
