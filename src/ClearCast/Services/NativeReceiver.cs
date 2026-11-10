using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ClearCast;

internal static class NativeReceiver
{
    private const string LibraryName = "clearcast_receiver.dll";
    private const uint SearchDllLoadDirectory = 0x00000100;
    private const uint SearchSystem32 = 0x00000800;
    private const uint SearchDefaultDirectories = 0x00001000;

    private static readonly object ModuleLock = new();
    private static nint _module;

    static NativeReceiver()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeReceiver).Assembly,
            (name, _, _) => name == LibraryName ? GetModule() : 0);
    }

    private static nint GetModule()
    {
        lock (ModuleLock)
        {
            if (_module == 0)
                _module = LoadModule();
            return _module;
        }
    }

    private static nint LoadModule()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "receiver");
        string path = Path.Combine(directory, LibraryName);
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到接收组件。请使用完整的 ClearCast 发布文件夹。", path);

        // GLib loads GStreamer plugins later. Let their imports find the same app-local DLLs.
        if (!SetDefaultDllDirectories(SearchDefaultDirectories))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化接收组件的 DLL 搜索路径。");
        nint cookie = AddDllDirectory(directory);
        if (cookie == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册接收组件目录。");

        nint module = LoadLibraryExW(path, 0, SearchDllLoadDirectory | SearchSystem32);
        if (module == 0)
        {
            int error = Marshal.GetLastWin32Error();
            RemoveDllDirectory(cookie);
            throw new Win32Exception(error, "无法加载接收组件或其依赖。");
        }

        if (!NativeLibrary.TryGetExport(module, "clearcast_receiver_start_v2", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_get_name", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_stop", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_set_decoder", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_read_event", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_read_stats", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_read_video_frame", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_set_video_rotation", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_read_media", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_read_artwork", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_media_command", out _) ||
            !NativeLibrary.TryGetExport(module, "clearcast_receiver_media_control", out _))
        {
            NativeLibrary.Free(module);
            RemoveDllDirectory(cookie);
            throw new InvalidOperationException("接收组件版本不兼容。请使用完整的 ClearCast 发布文件夹。");
        }

        return module;
    }

    internal static string AdvertisedName => Marshal.PtrToStringUTF8(GetName()) ?? "ClearCast";

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_get_name", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetName();

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_start_v2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string state,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string size,
        int fps, int decoder, int hevc,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pin,
        nint videoHost, uint adapterAddress);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_stop", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Stop();

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_set_decoder", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetDecoder(int decoder);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_set_video_rotation", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetVideoRotation(int quarterTurnsClockwise);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_read_event", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadEvent(out int type, out int width, out int height, out int codec, out int error);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_read_stats", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadStats(out ulong rendered, out ulong dropped, out int width, out int height);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_read_video_frame", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadVideoFrame([Out] byte[] pixels, int capacity, out ulong generation);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_read_media", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadMedia([Out] byte[] buffer, int capacity);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_read_artwork", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadArtwork(ulong revision, [Out] byte[] buffer, int capacity);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_media_command", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MediaCommand(int command);

    [DllImport(LibraryName, EntryPoint = "clearcast_receiver_media_control", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MediaControl(ulong session, ulong content, int command, long value);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string fileName, nint file, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string directory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDllDirectory(nint cookie);
}
