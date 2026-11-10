using System.Runtime.InteropServices;
using DexHub.App.Input;
using DexHub.Core.Libretro;
using Silk.NET.SDL;

namespace DexHub.App.Video;

/// <summary>
/// OpenGL-Kontext für 3D-Cores über ein unsichtbares SDL-Fenster. Der Core zeichnet in einen Framebuffer,
/// das Bild wird zurückgelesen und ganz normal im Spielfenster angezeigt.
/// </summary>
internal sealed unsafe class SdlGlContext : IGlContext
{
    private const uint InitVideo = 0x00000020;
    private const uint GlContextProfileCore = 0x0001, GlContextProfileCompatibility = 0x0002;
    private const int GlContextDebugFlag = 0x0001;

    private readonly Sdl _sdl = SdlApi.Get();
    private Window* _window;
    private void* _context;

    public string Description { get; private set; } = "";

    public bool Create(int major, int minor, bool coreProfile, bool debug)
    {
        lock (SdlApi.Lock)
        {
            if (_sdl.InitSubSystem(InitVideo) < 0)
            {
                Console.WriteLine("[DexHub] SDL-Video: " + _sdl.GetErrorS());
                return false;
            }

            _sdl.GLResetAttributes();
            _sdl.GLSetAttribute(GLattr.RedSize, 8);
            _sdl.GLSetAttribute(GLattr.GreenSize, 8);
            _sdl.GLSetAttribute(GLattr.BlueSize, 8);
            _sdl.GLSetAttribute(GLattr.DepthSize, 24);
            _sdl.GLSetAttribute(GLattr.StencilSize, 8);
            _sdl.GLSetAttribute(GLattr.Doublebuffer, 1);
            if (coreProfile)
            {
                _sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GlContextProfileCore);
                _sdl.GLSetAttribute(GLattr.ContextMajorVersion, major > 0 ? major : 3);
                _sdl.GLSetAttribute(GLattr.ContextMinorVersion, major > 0 ? minor : 3);
            }
            else
            {
                _sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GlContextProfileCompatibility);
                if (major > 0)
                {
                    _sdl.GLSetAttribute(GLattr.ContextMajorVersion, major);
                    _sdl.GLSetAttribute(GLattr.ContextMinorVersion, minor);
                }
            }
            if (debug) _sdl.GLSetAttribute(GLattr.ContextFlags, GlContextDebugFlag);

            _window = _sdl.CreateWindow("DexHub OpenGL", 0, 0, 64, 64,
                (uint)(WindowFlags.Opengl | WindowFlags.Hidden));
            if (_window == null)
            {
                Console.WriteLine("[DexHub] SDL-Fenster für OpenGL: " + _sdl.GetErrorS());
                return false;
            }

            _context = _sdl.GLCreateContext(_window);
            if (_context == null && !coreProfile)
            {
                // Manche Treiber (macOS) können kein Kompatibilitätsprofil -> ohne Vorgaben erneut versuchen
                _sdl.GLResetAttributes();
                _context = _sdl.GLCreateContext(_window);
            }
            if (_context == null)
            {
                Console.WriteLine("[DexHub] OpenGL-Kontext: " + _sdl.GetErrorS());
                return false;
            }

            _sdl.GLMakeCurrent(_window, _context);
            _sdl.GLSetSwapInterval(0);
            Description = DescribeDriver();
            return true;
        }
    }

    public IntPtr GetProcAddress(string name) => (IntPtr)_sdl.GLGetProcAddress(name);

    public void MakeCurrent()
    {
        if (_window != null && _context != null) _sdl.GLMakeCurrent(_window, _context);
    }

    private string DescribeDriver()
    {
        const uint GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02;
        var getString = (delegate* unmanaged<uint, byte*>)GetProcAddress("glGetString");
        if (getString == null) return "?";
        string Get(uint what) => Marshal.PtrToStringUTF8((IntPtr)getString(what)) ?? "?";
        return $"{Get(GL_RENDERER)} ({Get(GL_VENDOR)}), OpenGL {Get(GL_VERSION)}";
    }

    public void Dispose()
    {
        lock (SdlApi.Lock)
        {
            if (_context != null) { _sdl.GLDeleteContext(_context); _context = null; }
            if (_window != null) { _sdl.DestroyWindow(_window); _window = null; }
        }
    }
}
