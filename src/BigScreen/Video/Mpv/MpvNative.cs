using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BigScreen.Video.Mpv;

/// <summary>
/// Direct P/Invoke bindings for libmpv (mpv-2.dll on Windows/Proton, libmpv.so.2 on Linux).
/// Uses NativeLibrary resolver for seamless cross-platform loading.
/// </summary>
internal static class MpvNative
{
    private const string LibraryName = "mpv";

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, ResolveDll);
    }

    private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return IntPtr.Zero;

        // 1. Check custom configured path
        var customPath = Plugin.MpvPath?.Value;
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            if (NativeLibrary.TryLoad(customPath, out var handle)) return handle;
        }

        var pluginDir = Path.GetDirectoryName(assembly.Location) ?? "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows / Proton search order
            string[] winCandidates =
            [
                Path.Combine(pluginDir, "mpv-2.dll"),
                Path.Combine(pluginDir, "libmpv-2.dll"),
                "mpv-2.dll",
                "libmpv-2.dll"
            ];

            foreach (var cand in winCandidates)
            {
                if (NativeLibrary.TryLoad(cand, assembly, searchPath, out var handle))
                    return handle;
            }
        }
        else
        {
            // Linux search order
            string[] linuxCandidates =
            [
                Path.Combine(pluginDir, "libmpv.so.2"),
                Path.Combine(pluginDir, "libmpv.so"),
                "libmpv.so.2",
                "libmpv.so",
                "/usr/lib/libmpv.so.2",
                "/usr/lib/x86_64-linux-gnu/libmpv.so.2",
                "/usr/local/lib/libmpv.so.2"
            ];

            foreach (var cand in linuxCandidates)
            {
                if (NativeLibrary.TryLoad(cand, assembly, searchPath, out var handle))
                    return handle;
            }
        }

        return IntPtr.Zero;
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                var handle = ResolveDll(LibraryName, typeof(MpvNative).Assembly, null);
                if (handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }

    // --- Core Enums & Formats ---

    public enum MpvFormat
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6,
        NodeArray = 7,
        NodeMap = 8,
        ByteArray = 9
    }

    public enum MpvRenderParamType
    {
        Invalid = 0,
        ApiType = 1,
        OpenGlInitParams = 2,
        OpenGlFbo = 3,
        FlipY = 4,
        Depth = 5,
        IccProfile = 6,
        AmbientLight = 7,
        X11Display = 8,
        WlDisplay = 9,
        AdvancedControl = 10,
        NextFrameInfo = 11,
        BlockForTargetTime = 12,
        SkipRendering = 13,
        DrmDisplay = 14,
        DrmDrawSurfaceSize = 15,
        DrmMode = 16,
        SwSize = 17,
        SwFormat = 18,
        SwStride = 19,
        SwPointer = 20
    }

    [Flags]
    public enum MpvRenderContextFlag : ulong
    {
        None = 0,
        UpdateFrame = 1 << 0
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvRenderParam
    {
        public MpvRenderParamType Type;
        public IntPtr Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvRenderUpdateFn(IntPtr cbCtx);

    // --- P/Invoke Declarations ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, [In] IntPtr[] args);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    // --- Render Context P/Invoke ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out IntPtr res, IntPtr ctx, [In] MpvRenderParam[] @params);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr cbCtx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(IntPtr ctx, [In] MpvRenderParam[] @params);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(IntPtr ctx);

    // --- Helper Utilities ---

    public static string GetError(int code)
    {
        if (code >= 0) return null;
        var ptr = mpv_error_string(code);
        return ptr != IntPtr.Zero ? Marshal.PtrToStringUTF8(ptr) : $"Error {code}";
    }

    public static string GetPropertyString(IntPtr ctx, string name)
    {
        var ptr = mpv_get_property_string(ctx, name);
        if (ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(ptr); }
        finally { mpv_free(ptr); }
    }

    public static double GetPropertyDouble(IntPtr ctx, string name, double fallback = 0.0)
    {
        var ptr = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            int err = mpv_get_property(ctx, name, MpvFormat.Double, ptr);
            return err >= 0 ? Marshal.PtrToStructure<double>(ptr) : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static bool GetPropertyBool(IntPtr ctx, string name, bool fallback = false)
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            int err = mpv_get_property(ctx, name, MpvFormat.Flag, ptr);
            return err >= 0 ? Marshal.ReadInt32(ptr) != 0 : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static int SetPropertyDouble(IntPtr ctx, string name, double value)
    {
        var ptr = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            return mpv_set_property(ctx, name, MpvFormat.Double, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static int SetPropertyBool(IntPtr ctx, string name, bool value)
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(ptr, value ? 1 : 0);
            return mpv_set_property(ctx, name, MpvFormat.Flag, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Sets a string-valued option or property (track ids, languages, ...). Once mpv has been
    /// initialized, options are frozen and mpv_set_property* is the only way to change them.
    /// </summary>
    public static int SetPropertyString(IntPtr ctx, string name, string value)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(value ?? "");
        try
        {
            return mpv_set_property(ctx, name, MpvFormat.String, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;

            return mpv_command(ctx, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ptrs[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(ptrs[i]);
            }
        }
    }
}
