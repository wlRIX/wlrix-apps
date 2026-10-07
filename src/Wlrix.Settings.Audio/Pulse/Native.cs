// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Wlrix.Settings.Audio.Pulse;

// The slice of libpulse the panel uses, and nothing more: the threaded mainloop, a context,
// the introspection and control calls for sinks, sources, cards and modules, and record
// streams with peak detection for the meters. The same library is the client side of both
// PulseAudio and PipeWire's pipewire-pulse, which is the reason to use it at all.
//
// The structs below are declared field for field after <pulse/introspect.h>, <pulse/sample.h>,
// <pulse/channelmap.h> and <pulse/volume.h>. libpulse only ever appends fields to them, never
// reorders or removes, and the panel never allocates one of the *_info structs itself: it only
// reads them through the pointers libpulse passes to a callback, which stay valid for that call
// only. So declaring a prefix of a newer struct is safe, and anything copied out has to be copied
// before the callback returns. Only pa_cvolume, pa_sample_spec and pa_channel_map are ever built
// on this side, and those three have been fixed since 0.9.

internal enum ContextState
{
    Unconnected = 0,
    Connecting = 1,
    Authorizing = 2,
    SettingName = 3,
    Ready = 4,
    Failed = 5,
    Terminated = 6,
}

internal enum StreamState
{
    Unconnected = 0,
    Creating = 1,
    Ready = 2,
    Failed = 3,
    Terminated = 4,
}

internal static class PaConst
{
    public const uint InvalidIndex = uint.MaxValue;
    public const uint VolumeNorm = 0x10000;
    public const int ChannelsMax = 32;

    public const uint ContextNoFail = 0x0002;

    public const uint SubscriptionMaskSink = 0x0001;
    public const uint SubscriptionMaskSource = 0x0002;
    public const uint SubscriptionMaskModule = 0x0010;
    public const uint SubscriptionMaskServer = 0x0080;
    public const uint SubscriptionMaskCard = 0x0200;

    public const uint StreamDontMove = 0x0200;
    public const uint StreamPeakDetect = 0x0800;
    public const uint StreamAdjustLatency = 0x2000;

    public const int SampleFloat32Le = 5;

    public const int StateRunning = 0;
    public const int StateIdle = 1;
    public const int StateSuspended = 2;

    public const int PortAvailableNo = 1;

    // pa_channel_position_t, the few the panel names.
    public const int ChannelMono = 0;
    public const int ChannelFrontLeft = 1;
    public const int ChannelFrontRight = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SampleSpec
{
    public int Format;
    public uint Rate;
    public byte Channels;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ChannelMap
{
    public byte Channels;
    public fixed int Map[PaConst.ChannelsMax];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CVolume
{
    public byte Channels;
    public fixed uint Values[PaConst.ChannelsMax];
}

[StructLayout(LayoutKind.Sequential)]
internal struct BufferAttr
{
    public uint MaxLength;
    public uint TLength;
    public uint PreBuf;
    public uint MinReq;
    public uint FragSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PortInfo
{
    public byte* Name;
    public byte* Description;
    public uint Priority;
    public int Available;
}

/// <summary>
/// pa_sink_info and pa_source_info, which are the same shape: the source's
/// <c>monitor_of_sink</c> sits where the sink's <c>monitor_source</c> does.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DeviceInfo
{
    public byte* Name;
    public uint Index;
    public byte* Description;
    public SampleSpec SampleSpec;
    public ChannelMap ChannelMap;
    public uint OwnerModule;
    public CVolume Volume;
    public int Mute;
    public uint Monitor;
    public byte* MonitorName;
    public ulong Latency;
    public byte* Driver;
    public uint Flags;
    public nint Proplist;
    public ulong ConfiguredLatency;
    public uint BaseVolume;
    public int State;
    public uint NVolumeSteps;
    public uint Card;
    public uint NPorts;
    public PortInfo** Ports;
    public PortInfo* ActivePort;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ServerInfo
{
    public byte* UserName;
    public byte* HostName;
    public byte* ServerVersion;
    public byte* ServerName;
    public SampleSpec SampleSpec;
    public byte* DefaultSinkName;
    public byte* DefaultSourceName;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CardProfileInfo2
{
    public byte* Name;
    public byte* Description;
    public uint NSinks;
    public uint NSources;
    public uint Priority;
    public int Available;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CardInfo
{
    public uint Index;
    public byte* Name;
    public uint OwnerModule;
    public byte* Driver;
    public uint NProfiles;
    public nint Profiles;
    public nint ActiveProfile;
    public nint Proplist;
    public uint NPorts;
    public nint Ports;
    public CardProfileInfo2** Profiles2;
    public CardProfileInfo2* ActiveProfile2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ModuleInfo
{
    public uint Index;
    public byte* Name;
    public byte* Argument;
}

internal static unsafe partial class Native
{
    private const string Lib = "libpulse.so.0";

    // ---- Threaded mainloop ----

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_new")]
    public static partial nint MainloopNew();

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_start")]
    public static partial int MainloopStart(nint mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_stop")]
    public static partial void MainloopStop(nint mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_free")]
    public static partial void MainloopFree(nint mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_lock")]
    public static partial void MainloopLock(nint mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_unlock")]
    public static partial void MainloopUnlock(nint mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_in_thread")]
    public static partial int MainloopInThread(nint mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_get_api")]
    public static partial nint MainloopGetApi(nint mainloop);

    // ---- Proplist ----

    [LibraryImport(Lib, EntryPoint = "pa_proplist_new")]
    public static partial nint ProplistNew();

    [LibraryImport(Lib, EntryPoint = "pa_proplist_free")]
    public static partial void ProplistFree(nint proplist);

    [LibraryImport(Lib, EntryPoint = "pa_proplist_sets", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ProplistSets(nint proplist, string key, string value);

    [LibraryImport(Lib, EntryPoint = "pa_proplist_gets", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ProplistGets(nint proplist, string key);

    // ---- Context ----

    [LibraryImport(Lib, EntryPoint = "pa_context_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ContextNew(nint api, string name, nint proplist);

    [LibraryImport(Lib, EntryPoint = "pa_context_unref")]
    public static partial void ContextUnref(nint context);

    [LibraryImport(Lib, EntryPoint = "pa_context_connect")]
    public static partial int ContextConnect(nint context, nint server, uint flags, nint spawnApi);

    [LibraryImport(Lib, EntryPoint = "pa_context_disconnect")]
    public static partial void ContextDisconnect(nint context);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_state")]
    public static partial ContextState ContextGetState(nint context);

    [LibraryImport(Lib, EntryPoint = "pa_context_errno")]
    public static partial int ContextErrno(nint context);

    [LibraryImport(Lib, EntryPoint = "pa_strerror")]
    public static partial nint StrError(int error);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_state_callback")]
    public static partial void ContextSetStateCallback(
        nint context, delegate* unmanaged<nint, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_subscribe_callback")]
    public static partial void ContextSetSubscribeCallback(
        nint context, delegate* unmanaged<nint, uint, uint, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_subscribe")]
    public static partial nint ContextSubscribe(
        nint context, uint mask, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_operation_unref")]
    public static partial void OperationUnref(nint operation);

    // ---- Introspection ----

    [LibraryImport(Lib, EntryPoint = "pa_context_get_server_info")]
    public static partial nint GetServerInfo(
        nint context, delegate* unmanaged<nint, ServerInfo*, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_sink_info_list")]
    public static partial nint GetSinkInfoList(
        nint context, delegate* unmanaged<nint, DeviceInfo*, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_source_info_list")]
    public static partial nint GetSourceInfoList(
        nint context, delegate* unmanaged<nint, DeviceInfo*, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_card_info_list")]
    public static partial nint GetCardInfoList(
        nint context, delegate* unmanaged<nint, CardInfo*, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_module_info_list")]
    public static partial nint GetModuleInfoList(
        nint context, delegate* unmanaged<nint, ModuleInfo*, int, nint, void> callback, nint userdata);

    // ---- Control ----

    [LibraryImport(Lib, EntryPoint = "pa_context_set_sink_volume_by_index")]
    public static partial nint SetSinkVolume(
        nint context, uint index, CVolume* volume, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_source_volume_by_index")]
    public static partial nint SetSourceVolume(
        nint context, uint index, CVolume* volume, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_sink_mute_by_index")]
    public static partial nint SetSinkMute(
        nint context, uint index, int mute, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_source_mute_by_index")]
    public static partial nint SetSourceMute(
        nint context, uint index, int mute, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_default_sink", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SetDefaultSink(
        nint context, string name, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_default_source", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SetDefaultSource(
        nint context, string name, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_sink_port_by_index", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SetSinkPort(
        nint context, uint index, string port, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_source_port_by_index", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SetSourcePort(
        nint context, uint index, string port, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_card_profile_by_index", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SetCardProfile(
        nint context, uint index, string profile, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_load_module", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint LoadModule(
        nint context, string name, string argument, delegate* unmanaged<nint, uint, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_context_unload_module")]
    public static partial nint UnloadModule(
        nint context, uint index, delegate* unmanaged<nint, int, nint, void> callback, nint userdata);

    // ---- Record streams, for the meters ----

    [LibraryImport(Lib, EntryPoint = "pa_stream_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint StreamNew(nint context, string name, SampleSpec* spec, ChannelMap* map);

    [LibraryImport(Lib, EntryPoint = "pa_stream_unref")]
    public static partial void StreamUnref(nint stream);

    [LibraryImport(Lib, EntryPoint = "pa_stream_connect_record", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int StreamConnectRecord(nint stream, string device, BufferAttr* attr, uint flags);

    [LibraryImport(Lib, EntryPoint = "pa_stream_disconnect")]
    public static partial int StreamDisconnect(nint stream);

    [LibraryImport(Lib, EntryPoint = "pa_stream_get_state")]
    public static partial StreamState StreamGetState(nint stream);

    [LibraryImport(Lib, EntryPoint = "pa_stream_set_read_callback")]
    public static partial void StreamSetReadCallback(
        nint stream, delegate* unmanaged<nint, nuint, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_stream_set_state_callback")]
    public static partial void StreamSetStateCallback(
        nint stream, delegate* unmanaged<nint, nint, void> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "pa_stream_peek")]
    public static partial int StreamPeek(nint stream, void** data, nuint* nbytes);

    [LibraryImport(Lib, EntryPoint = "pa_stream_drop")]
    public static partial int StreamDrop(nint stream);

    /// <summary>A C string libpulse owns, copied out; null stays null.</summary>
    public static string? Str(byte* p) => p is null ? null : Marshal.PtrToStringUTF8((nint)p);

    /// <summary>A proplist value, copied out; null when the key is not set.</summary>
    public static string? Prop(nint proplist, string key) =>
        proplist == 0 ? null : Marshal.PtrToStringUTF8(ProplistGets(proplist, key));
}
