using System.Runtime.InteropServices;

namespace DexHub.Core.Libretro;

/// <summary>
/// Ein OpenGL-Kontext für Cores mit 3D-Grafik (N64, GameCube, 3DS). Die App stellt ihn bereit
/// (Desktop: unsichtbares SDL-Fenster). Er wird im Emulator-Thread angelegt und benutzt.
/// </summary>
public interface IGlContext : IDisposable
{
    /// <param name="major">Gewünschte OpenGL-Version (0 = egal).</param>
    /// <param name="coreProfile">Core-Profil (ohne alte Funktionen) statt Kompatibilitätsprofil.</param>
    bool Create(int major, int minor, bool coreProfile, bool debug);
    IntPtr GetProcAddress(string name);
    void MakeCurrent();
    string Description { get; }
}

/// <summary>Wer OpenGL anbieten kann, trägt hier eine Fabrik ein (sonst bleiben 3D-Cores aus).</summary>
public static class GlContextFactory
{
    public static Func<IGlContext>? Create { get; set; }
}

/// <summary>libretro: struct retro_hw_render_callback.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetroHwRenderCallback
{
    public uint ContextType;
    public IntPtr ContextReset;
    public IntPtr GetCurrentFramebuffer;
    public IntPtr GetProcAddress;
    [MarshalAs(UnmanagedType.U1)] public bool Depth;
    [MarshalAs(UnmanagedType.U1)] public bool Stencil;
    [MarshalAs(UnmanagedType.U1)] public bool BottomLeftOrigin;
    public uint VersionMajor;
    public uint VersionMinor;
    [MarshalAs(UnmanagedType.U1)] public bool CacheContext;
    public IntPtr ContextDestroy;
    [MarshalAs(UnmanagedType.U1)] public bool DebugContext;
}

/// <summary>
/// Framebuffer, in den der Core zeichnet. Nach jedem Bild lesen wir die Pixel zurück und geben sie
/// wie bei den 2D-Cores als normales Bild weiter – so bleibt der Rest des Programms unverändert.
/// </summary>
internal sealed unsafe class HwFramebuffer : IDisposable
{
    private const uint GL_FRAMEBUFFER = 0x8D40, GL_READ_FRAMEBUFFER = 0x8CA8, GL_TEXTURE_2D = 0x0DE1,
        GL_RGBA8 = 0x8058, GL_RGBA = 0x1908, GL_BGRA = 0x80E1, GL_UNSIGNED_BYTE = 0x1401,
        GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_RENDERBUFFER = 0x8D41, GL_DEPTH24_STENCIL8 = 0x88F0,
        GL_DEPTH_STENCIL_ATTACHMENT = 0x821A, GL_DEPTH_COMPONENT24 = 0x81A6, GL_DEPTH_ATTACHMENT = 0x8D00,
        GL_PACK_ALIGNMENT = 0x0D05, GL_FRAMEBUFFER_COMPLETE = 0x8CD5,
        GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_NEAREST = 0x2600;

    private readonly delegate* unmanaged<int, uint*, void> _genFramebuffers, _genTextures, _genRenderbuffers;
    private readonly delegate* unmanaged<int, uint*, void> _deleteFramebuffers, _deleteTextures, _deleteRenderbuffers;
    private readonly delegate* unmanaged<uint, uint, void> _bindFramebuffer, _bindTexture, _bindRenderbuffer;
    private readonly delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> _texImage2D;
    private readonly delegate* unmanaged<uint, uint, int, void> _texParameteri;
    private readonly delegate* unmanaged<uint, uint, uint, uint, int, void> _framebufferTexture2D;
    private readonly delegate* unmanaged<uint, uint, int, int, void> _renderbufferStorage;
    private readonly delegate* unmanaged<uint, uint, uint, uint, void> _framebufferRenderbuffer;
    private readonly delegate* unmanaged<uint, uint> _checkFramebufferStatus;
    private readonly delegate* unmanaged<int, int, int, int, uint, uint, void*, void> _readPixels;
    private readonly delegate* unmanaged<uint, int, void> _pixelStorei;

    private readonly bool _depth, _stencil;
    private uint _fbo, _texture, _depthBuffer;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Framebuffer => _fbo;

    private byte[] _pixels = [], _flipped = [];

    public HwFramebuffer(IGlContext gl, int width, int height, bool depth, bool stencil)
    {
        void* Fn(string name)
        {
            var p = gl.GetProcAddress(name);
            if (p == IntPtr.Zero) throw new InvalidOperationException($"OpenGL-Funktion {name} fehlt.");
            return (void*)p;
        }
        _genFramebuffers = (delegate* unmanaged<int, uint*, void>)Fn("glGenFramebuffers");
        _genTextures = (delegate* unmanaged<int, uint*, void>)Fn("glGenTextures");
        _genRenderbuffers = (delegate* unmanaged<int, uint*, void>)Fn("glGenRenderbuffers");
        _deleteFramebuffers = (delegate* unmanaged<int, uint*, void>)Fn("glDeleteFramebuffers");
        _deleteTextures = (delegate* unmanaged<int, uint*, void>)Fn("glDeleteTextures");
        _deleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)Fn("glDeleteRenderbuffers");
        _bindFramebuffer = (delegate* unmanaged<uint, uint, void>)Fn("glBindFramebuffer");
        _bindTexture = (delegate* unmanaged<uint, uint, void>)Fn("glBindTexture");
        _bindRenderbuffer = (delegate* unmanaged<uint, uint, void>)Fn("glBindRenderbuffer");
        _texImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)Fn("glTexImage2D");
        _texParameteri = (delegate* unmanaged<uint, uint, int, void>)Fn("glTexParameteri");
        _framebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)Fn("glFramebufferTexture2D");
        _renderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)Fn("glRenderbufferStorage");
        _framebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)Fn("glFramebufferRenderbuffer");
        _checkFramebufferStatus = (delegate* unmanaged<uint, uint>)Fn("glCheckFramebufferStatus");
        _readPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)Fn("glReadPixels");
        _pixelStorei = (delegate* unmanaged<uint, int, void>)Fn("glPixelStorei");

        _depth = depth;
        _stencil = stencil;
        Resize(Math.Max(width, 1), Math.Max(height, 1));
    }

    /// <summary>Legt Textur und Tiefenpuffer in der gewünschten (maximalen) Größe an.</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height && _fbo != 0) return;
        DeleteObjects();
        Width = width;
        Height = height;

        uint id;
        _genFramebuffers(1, &id); _fbo = id;
        _bindFramebuffer(GL_FRAMEBUFFER, _fbo);

        _genTextures(1, &id); _texture = id;
        _bindTexture(GL_TEXTURE_2D, _texture);
        _texImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        _texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
        _texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        _framebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _texture, 0);

        if (_depth)
        {
            _genRenderbuffers(1, &id); _depthBuffer = id;
            _bindRenderbuffer(GL_RENDERBUFFER, _depthBuffer);
            _renderbufferStorage(GL_RENDERBUFFER, _stencil ? GL_DEPTH24_STENCIL8 : GL_DEPTH_COMPONENT24, width, height);
            _framebufferRenderbuffer(GL_FRAMEBUFFER, _stencil ? GL_DEPTH_STENCIL_ATTACHMENT : GL_DEPTH_ATTACHMENT,
                GL_RENDERBUFFER, _depthBuffer);
        }

        var status = _checkFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE)
            Console.WriteLine($"[DexHub] OpenGL-Framebuffer unvollständig: 0x{status:X}");
        _bindTexture(GL_TEXTURE_2D, 0);
        _bindFramebuffer(GL_FRAMEBUFFER, 0);
    }

    /// <summary>
    /// Liest das fertige Bild (BGRA) aus dem Framebuffer. OpenGL zählt Zeilen von unten –
    /// bei bottomLeftOrigin wird gespiegelt, damit oben oben ist.
    /// </summary>
    public IntPtr Read(int width, int height, bool bottomLeftOrigin)
    {
        width = Math.Min(width, Width);
        height = Math.Min(height, Height);
        int size = width * height * 4;
        // Fest im Speicher (pinned), damit der Zeiger nach dem Zurückgeben gültig bleibt
        if (_pixels.Length < size) { _pixels = GC.AllocateArray<byte>(size, pinned: true); _flipped = GC.AllocateArray<byte>(size, pinned: true); }

        _bindFramebuffer(GL_READ_FRAMEBUFFER, _fbo);
        _pixelStorei(GL_PACK_ALIGNMENT, 4);
        fixed (byte* p = _pixels) _readPixels(0, 0, width, height, GL_BGRA, GL_UNSIGNED_BYTE, p);

        if (!bottomLeftOrigin)
        {
            fixed (byte* p = _pixels) return (IntPtr)p;
        }

        int row = width * 4;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(_pixels, y * row, _flipped, (height - 1 - y) * row, row);
        fixed (byte* p = _flipped) return (IntPtr)p;
    }

    private void DeleteObjects()
    {
        uint id;
        if (_fbo != 0) { id = _fbo; _deleteFramebuffers(1, &id); _fbo = 0; }
        if (_texture != 0) { id = _texture; _deleteTextures(1, &id); _texture = 0; }
        if (_depthBuffer != 0) { id = _depthBuffer; _deleteRenderbuffers(1, &id); _depthBuffer = 0; }
    }

    public void Dispose() => DeleteObjects();
}
