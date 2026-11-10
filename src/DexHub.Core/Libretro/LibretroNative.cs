using System.Runtime.InteropServices;

namespace DexHub.Core.Libretro;

// Übersetzung der wichtigsten Teile von libretro.h nach C#.
// Original: https://github.com/libretro/libretro-common/blob/master/include/libretro.h

/// <summary>Pixelformate, in denen ein Core seine Bilder liefern kann.</summary>
public enum RetroPixelFormat
{
    XRGB1555 = 0, // Standard, falls der Core nichts anderes sagt
    XRGB8888 = 1,
    RGB565 = 2,
}

/// <summary>Tasten des libretro-"RetroPad" (angelehnt an einen SNES-Controller).</summary>
public enum JoypadButton
{
    B = 0, Y = 1, Select = 2, Start = 3,
    Up = 4, Down = 5, Left = 6, Right = 7,
    A = 8, X = 9, L = 10, R = 11,
    L2 = 12, R2 = 13, L3 = 14, R3 = 15,
}

internal static class Retro
{
    public const uint DeviceJoypad = 1;
    public const uint DeviceAnalog = 5;
    public const uint DevicePointer = 6;
    public const uint JoypadMask = 256;

    public const uint MemorySaveRam = 0;
    public const uint MemorySystemRam = 2;

    public const uint Experimental = 0x10000;

    // Environment-Befehle (Auswahl)
    public const uint EnvSetRotation = 1;
    public const uint EnvGetCanDupe = 3;
    public const uint EnvSetMessage = 6;
    public const uint EnvShutdown = 7;
    public const uint EnvSetPerformanceLevel = 8;
    public const uint EnvGetSystemDirectory = 9;
    public const uint EnvSetPixelFormat = 10;
    public const uint EnvSetHwRender = 14;
    public const uint EnvGetPreferredHwRender = 56;
    public const uint EnvSetInputDescriptors = 11;
    public const uint EnvGetVariable = 15;
    public const uint EnvSetVariables = 16;
    public const uint EnvGetVariableUpdate = 17;
    public const uint EnvSetSupportNoGame = 18;
    public const uint EnvGetLibretroPath = 19;
    public const uint EnvGetCoreAssetsDirectory = 30;
    public const uint EnvGetSaveDirectory = 31;
    public const uint EnvSetSystemAvInfo = 32;
    public const uint EnvSetSubsystemInfo = 34;
    public const uint EnvSetControllerInfo = 35;
    public const uint EnvSetMemoryMaps = 36;
    public const uint EnvSetGeometry = 37;
    public const uint EnvGetUsername = 38;
    public const uint EnvGetLanguage = 39;
    public const uint EnvSetSupportAchievements = 42;
    public const uint EnvSetSerializationQuirks = 44;
    public const uint EnvGetAudioVideoEnable = 47;
    public const uint EnvGetFastForwarding = 49;
    public const uint EnvGetTargetRefreshRate = 50;
    public const uint EnvGetInputBitmasks = 51;
    public const uint EnvGetCoreOptionsVersion = 52;
    public const uint EnvSetCoreOptionsDisplay = 55;
    public const uint EnvSetMinimumAudioLatency = 63;
    public const uint EnvSetFastForwardingOverride = 64;
    public const uint EnvSetContentInfoOverride = 65;
    public const uint EnvSetCoreOptionsUpdateDisplayCallback = 69;
    public const uint EnvGetGameInfoExt = 66;
    public const uint EnvSetVariable = 70;

    public const uint LanguageGerman = 4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroSystemInfo
{
    public IntPtr LibraryName;
    public IntPtr LibraryVersion;
    public IntPtr ValidExtensions;
    public byte NeedFullpath;   // C-bool = 1 Byte
    public byte BlockExtract;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroGameGeometry
{
    public uint BaseWidth;
    public uint BaseHeight;
    public uint MaxWidth;
    public uint MaxHeight;
    public float AspectRatio;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemTiming
{
    public double Fps;
    public double SampleRate;
}

[StructLayout(LayoutKind.Sequential)]
public struct RetroSystemAvInfo
{
    public RetroGameGeometry Geometry;
    public RetroSystemTiming Timing;
}

/// <summary>Ein Speicherbereich des emulierten Systems (für das Auslesen per Adresse).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetroMemoryDescriptor
{
    public ulong Flags;
    public IntPtr Ptr;
    public nuint Offset;
    public nuint Start;
    public nuint Select;
    public nuint Disconnect;
    public nuint Len;
    public IntPtr AddrSpace;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroMemoryMap
{
    public IntPtr Descriptors;
    public uint NumDescriptors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroGameInfo
{
    public IntPtr Path;
    public IntPtr Data;
    public nuint Size;
    public IntPtr Meta;
}

/// <summary>Erweiterte Spielinfos (Pfad, Name, Endung) – manche Cores bilden daraus den Spielstand-Namen.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetroGameInfoExt
{
    public IntPtr FullPath;
    public IntPtr ArchivePath;
    public IntPtr ArchiveFile;
    public IntPtr Dir;
    public IntPtr Name;
    public IntPtr Ext;
    public IntPtr Meta;
    public IntPtr Data;
    public nuint Size;
    public byte FileInArchive;
    public byte PersistentData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RetroVariable
{
    public IntPtr Key;
    public IntPtr Value;
}

// ---------- Funktionszeiger ----------
// Alle libretro-Funktionen nutzen die C-Aufrufkonvention (Cdecl).

internal static class RetroFn
{
    // Callbacks, die WIR dem Core geben
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool Environment(uint cmd, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoRefresh(IntPtr data, uint width, uint height, nuint pitch);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioSample(short left, short right);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nuint AudioSampleBatch(IntPtr data, nuint frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InputPoll();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nuint GetCurrentFramebuffer();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetProcAddress(IntPtr symbol);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate short InputState(uint port, uint device, uint index, uint id);

    // Funktionen, die der Core UNS anbietet
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Void();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint ApiVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetCallback(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetSystemInfo(ref RetroSystemInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetSystemAvInfo(ref RetroSystemAvInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool LoadGame(ref RetroGameInfo game);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool LoadGameSpecial(uint gameType, IntPtr info, nuint numInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetControllerPortDevice(uint port, uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nuint SerializeSize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool Serialize(IntPtr data, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetMemoryData(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nuint GetMemorySize(uint id);
}
