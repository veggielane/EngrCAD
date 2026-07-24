using System.Runtime.InteropServices;

namespace EngrCAD.Viewer;

/// <summary>
/// Minimal EGL binding over the ANGLE runtime Avalonia already ships on Windows
/// (<c>av_libglesv2.dll</c> from Avalonia.Angle.Windows.Natives exports both the EGL
/// and the GLES entry points — Avalonia's own ANGLE backend loads EGL from the same
/// dll). Creates an offscreen pbuffer surface plus a GLES3 context with no window and
/// no Avalonia platform initialization: the basis for headless rendering. Tries the
/// D3D11 hardware backend first, then D3D11-on-WARP (software — works on CI and in
/// locked sessions), then the default display.
/// </summary>
internal sealed unsafe class EglContext : IDisposable
{
    // EGL constants (EGL/egl.h and ANGLE's eglext.h).
    private const int EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_BLUE_SIZE = 0x3022;
    private const int EGL_GREEN_SIZE = 0x3023;
    private const int EGL_RED_SIZE = 0x3024;
    private const int EGL_DEPTH_SIZE = 0x3025;
    private const int EGL_SURFACE_TYPE = 0x3033;
    private const int EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_NONE = 0x3038;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_ES3_BIT = 0x0040;
    private const int EGL_HEIGHT = 0x3056;
    private const int EGL_WIDTH = 0x3057;
    private const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    private const int EGL_OPENGL_ES_API = 0x30A0;
    private const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;
    private const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
    private const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x3208;
    private const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE = 0x3209;
    private const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE = 0x320A;
    private const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_D3D_WARP_ANGLE = 0x320B;

    // ---- native library + function pointers (loaded once per process) ----

    private static readonly object LoadLock = new();
    private static nint _lib;
    private static string? _loadError;

    private static delegate* unmanaged[Cdecl]<byte*, nint> _eglGetProcAddress;
    private static delegate* unmanaged[Cdecl]<nint, nint> _eglGetDisplay;
    private static delegate* unmanaged[Cdecl]<int, nint, int*, nint> _eglGetPlatformDisplayExt;
    private static delegate* unmanaged[Cdecl]<nint, int*, int*, int> _eglInitialize;
    private static delegate* unmanaged[Cdecl]<int, int> _eglBindApi;
    private static delegate* unmanaged[Cdecl]<nint, int*, nint*, int, int*, int> _eglChooseConfig;
    private static delegate* unmanaged[Cdecl]<nint, nint, int*, nint> _eglCreatePbufferSurface;
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint> _eglCreateContext;
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int> _eglMakeCurrent;
    private static delegate* unmanaged[Cdecl]<nint, nint, int> _eglDestroySurface;
    private static delegate* unmanaged[Cdecl]<nint, nint, int> _eglDestroyContext;
    private static delegate* unmanaged[Cdecl]<int> _eglGetError;

    private static string? EnsureLoaded()
    {
        lock (LoadLock)
        {
            if (_lib != 0)
                return null;
            if (_loadError is not null)
                return _loadError;

            nint lib = 0;
            // Default probing first (covers published apps and deps.json-driven test
            // hosts), then the runtimes folder layout of non-published build output.
            if (!NativeLibrary.TryLoad("av_libglesv2.dll", typeof(EglContext).Assembly, null, out lib))
            {
                string rid = $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
                string candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
                if (!File.Exists(candidate) || !NativeLibrary.TryLoad(candidate, out lib))
                    return _loadError = $"ANGLE library av_libglesv2.dll not found (looked in default probe paths and {candidate}).";
            }

            // Avalonia's ANGLE build (av_libglesv2.dll) exports the EGL entry points with
            // an "EGL_" prefix (e.g. EGL_GetDisplay) rather than the standard "egl" names,
            // while GL functions keep their standard "gl" names. Try both spellings.
            nint Get(string standard, string prefixed) =>
                NativeLibrary.TryGetExport(lib, standard, out var a) ? a
                : NativeLibrary.TryGetExport(lib, prefixed, out var b) ? b : 0;

            _eglGetProcAddress = (delegate* unmanaged[Cdecl]<byte*, nint>)Get("eglGetProcAddress", "EGL_GetProcAddress");
            _eglGetDisplay = (delegate* unmanaged[Cdecl]<nint, nint>)Get("eglGetDisplay", "EGL_GetDisplay");
            _eglInitialize = (delegate* unmanaged[Cdecl]<nint, int*, int*, int>)Get("eglInitialize", "EGL_Initialize");
            _eglBindApi = (delegate* unmanaged[Cdecl]<int, int>)Get("eglBindAPI", "EGL_BindAPI");
            _eglChooseConfig = (delegate* unmanaged[Cdecl]<nint, int*, nint*, int, int*, int>)Get("eglChooseConfig", "EGL_ChooseConfig");
            _eglCreatePbufferSurface = (delegate* unmanaged[Cdecl]<nint, nint, int*, nint>)Get("eglCreatePbufferSurface", "EGL_CreatePbufferSurface");
            _eglCreateContext = (delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint>)Get("eglCreateContext", "EGL_CreateContext");
            _eglMakeCurrent = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)Get("eglMakeCurrent", "EGL_MakeCurrent");
            _eglDestroySurface = (delegate* unmanaged[Cdecl]<nint, nint, int>)Get("eglDestroySurface", "EGL_DestroySurface");
            _eglDestroyContext = (delegate* unmanaged[Cdecl]<nint, nint, int>)Get("eglDestroyContext", "EGL_DestroyContext");
            _eglGetError = (delegate* unmanaged[Cdecl]<int>)Get("eglGetError", "EGL_GetError");

            if (_eglGetProcAddress is null || _eglGetDisplay is null || _eglInitialize is null
                || _eglBindApi is null || _eglChooseConfig is null || _eglCreatePbufferSurface is null
                || _eglCreateContext is null || _eglMakeCurrent is null
                || _eglDestroySurface is null || _eglDestroyContext is null || _eglGetError is null)
                return _loadError = "av_libglesv2.dll is missing required EGL exports.";

            // Extension entry point; optional (falls back to eglGetDisplay).
            nint platformDisplay = GetProcAddressRaw("eglGetPlatformDisplayEXT");
            if (platformDisplay == 0)
                platformDisplay = Get("eglGetPlatformDisplayEXT", "EGL_GetPlatformDisplayEXT");
            _eglGetPlatformDisplayExt = (delegate* unmanaged[Cdecl]<int, nint, int*, nint>)platformDisplay;

            _lib = lib;
            return null;
        }
    }

    private static nint GetProcAddressRaw(string name)
    {
        Span<byte> utf8 = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++)
            utf8[i] = (byte)name[i]; // EGL/GL entry point names are pure ASCII
        utf8[name.Length] = 0;
        fixed (byte* p = utf8)
            return _eglGetProcAddress(p);
    }

    // ---- instance state ----

    private readonly nint _display;
    private readonly nint _surface;
    private readonly nint _context;
    private bool _disposed;

    private EglContext(nint display, nint surface, nint context)
    {
        _display = display;
        _surface = surface;
        _context = context;
    }

    /// <summary>
    /// Attempts to create an offscreen GLES3 context with a width x height pbuffer,
    /// current on the calling thread. Returns null and an error description on failure
    /// (no GPU, no ANGLE natives, remote session without D3D, ...).
    /// </summary>
    public static EglContext? TryCreate(int width, int height, out string? error)
    {
        error = EnsureLoaded();
        if (error is not null)
            return null;

        nint display = GetInitializedDisplay();
        if (display == 0)
        {
            error = $"no EGL display could be initialized (eglGetError=0x{_eglGetError():X})";
            return null;
        }

        _eglBindApi(EGL_OPENGL_ES_API);

        // 24-bit depth first: with only 16 bits, PolygonOffset(1,1) pushes glancing-angle
        // fills past their own silhouettes and the background bites through as sawtooth
        // notches. Fall back to 16 for drivers without a 24-bit pbuffer config.
        nint config = 0;
        int configCount = 0;
        int* configAttribs = stackalloc int[]
        {
            EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
            EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
            EGL_RED_SIZE, 8,
            EGL_GREEN_SIZE, 8,
            EGL_BLUE_SIZE, 8,
            EGL_ALPHA_SIZE, 8,
            EGL_DEPTH_SIZE, 24,   // patched to 16 for the fallback attempt below
            EGL_NONE,
        };
        const int depthSizeValueSlot = 13; // index of the EGL_DEPTH_SIZE value
        foreach (int depthBits in stackalloc int[] { 24, 16 })
        {
            configAttribs[depthSizeValueSlot] = depthBits;
            if (_eglChooseConfig(display, configAttribs, &config, 1, &configCount) != 0 && configCount >= 1)
                break;
        }
        if (configCount < 1)
        {
            error = $"eglChooseConfig found no pbuffer-capable GLES3 config (eglGetError=0x{_eglGetError():X})";
            return null;
        }

        int* surfaceAttribs = stackalloc int[] { EGL_WIDTH, width, EGL_HEIGHT, height, EGL_NONE };
        nint surface = _eglCreatePbufferSurface(display, config, surfaceAttribs);
        if (surface == 0)
        {
            error = $"eglCreatePbufferSurface failed (eglGetError=0x{_eglGetError():X})";
            return null;
        }

        int* contextAttribs = stackalloc int[] { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE };
        nint context = _eglCreateContext(display, config, 0, contextAttribs);
        if (context == 0)
        {
            _eglDestroySurface(display, surface);
            error = $"eglCreateContext (ES3) failed (eglGetError=0x{_eglGetError():X})";
            return null;
        }

        if (_eglMakeCurrent(display, surface, surface, context) == 0)
        {
            _eglDestroyContext(display, context);
            _eglDestroySurface(display, surface);
            error = $"eglMakeCurrent failed (eglGetError=0x{_eglGetError():X})";
            return null;
        }

        return new EglContext(display, surface, context);
    }

    private static nint GetInitializedDisplay()
    {
        if (_eglGetPlatformDisplayExt is not null)
        {
            // Same backend preference as Avalonia's ANGLE integration: D3D11 hardware,
            // then D3D11 on WARP (software rasterizer - survives CI and locked sessions).
            Span<int> deviceTypes =
                [EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE, EGL_PLATFORM_ANGLE_DEVICE_TYPE_D3D_WARP_ANGLE];
            int* attribs = stackalloc int[]
            {
                EGL_PLATFORM_ANGLE_TYPE_ANGLE, EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
                EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE, 0, // device type filled in per attempt
                EGL_NONE,
            };
            foreach (int deviceType in deviceTypes)
            {
                attribs[3] = deviceType;
                nint display = _eglGetPlatformDisplayExt(EGL_PLATFORM_ANGLE_ANGLE, 0, attribs);
                if (display != 0 && _eglInitialize(display, null, null) != 0)
                    return display;
            }
        }

        nint fallback = _eglGetDisplay(0); // EGL_DEFAULT_DISPLAY
        if (fallback != 0 && _eglInitialize(fallback, null, null) != 0)
            return fallback;
        return 0;
    }

    /// <summary>GL/EGL entry point loader for Silk.NET (eglGetProcAddress, with a
    /// direct-export fallback for anything ANGLE does not route through it).</summary>
    public nint GetFunction(string name)
    {
        nint address = GetProcAddressRaw(name);
        if (address == 0 && NativeLibrary.TryGetExport(_lib, name, out var export))
            address = export;
        return address;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _eglMakeCurrent(_display, 0, 0, 0);
        _eglDestroySurface(_display, _surface);
        _eglDestroyContext(_display, _context);
        // Intentionally no eglTerminate: the EGL display is process-global and may be
        // shared with a live Avalonia viewer in the same process.
    }
}
