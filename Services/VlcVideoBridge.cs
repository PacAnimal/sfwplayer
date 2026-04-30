using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using SfwPlayer.Platform.MacOS;

namespace SfwPlayer.Services;

public sealed class VlcVideoBridge : IDisposable
{
    // max buffer size caps what we'll accept from VLC (covers up to 1080p)
    private const int MaxW = 1920;
    private const int MaxH = 1080;
    private const int MaxYSize = MaxW * MaxH;
    private const int MaxUVSize = (MaxW / 2) * (MaxH / 2);

    private readonly LibVLC _vlc;
    private Media? _media;

    // double-buffer: two sets of Y/U/V planes; VLC writes to the back, UI reads the front
    private readonly byte[] _y0 = new byte[MaxYSize];
    private readonly byte[] _u0 = new byte[MaxUVSize];
    private readonly byte[] _v0 = new byte[MaxUVSize];
    private readonly byte[] _y1 = new byte[MaxYSize];
    private readonly byte[] _u1 = new byte[MaxUVSize];
    private readonly byte[] _v1 = new byte[MaxUVSize];
    private readonly GCHandle _pinY0, _pinU0, _pinV0;
    private readonly GCHandle _pinY1, _pinU1, _pinV1;

    private volatile int _front; // index (0 or 1) of the buffer the UI thread reads
    private int _postPending;    // 1 when a Flush is already queued to the UI thread
    private bool _disposed;
    private bool _firstFrame = true;
    private bool _bitmapNeedsRebuild;

    // actual coded dimensions negotiated in VideoFormat; volatile so Flush reads a consistent pair
    private volatile uint _videoW;
    private volatile uint _videoH;

    private WriteableBitmap? _bitmap;
    public WriteableBitmap? Bitmap => _bitmap;
    public MediaPlayer Player { get; }

    // called on the UI thread after each frame is converted — set to VideoImage.InvalidateVisual
    public Action? FrameReady { get; set; }

    // called on the UI thread when a new WriteableBitmap is created (video format negotiated)
    public Action? BitmapSourceChanged { get; set; }

    public event EventHandler? FirstFrameRendered;

    private static readonly string[] options =
        ["--no-video-title-show", "--no-osd", "--no-stats"];

    public VlcVideoBridge(string[] vlcArgs)
    {
        _pinY0 = GCHandle.Alloc(_y0, GCHandleType.Pinned);
        _pinU0 = GCHandle.Alloc(_u0, GCHandleType.Pinned);
        _pinV0 = GCHandle.Alloc(_v0, GCHandleType.Pinned);
        _pinY1 = GCHandle.Alloc(_y1, GCHandleType.Pinned);
        _pinU1 = GCHandle.Alloc(_u1, GCHandleType.Pinned);
        _pinV1 = GCHandle.Alloc(_v1, GCHandleType.Pinned);

        _vlc = new LibVLC(false, [.. options, .. vlcArgs]);
        Player = new MediaPlayer(_vlc);

        // SetVideoCallbacks internally sets vout=vmem; SetVideoFormatCallbacks negotiates I420
        // so the H.264 decoder output (I420/YUV420P) reaches vmem with zero chroma conversion
        Player.SetVideoFormatCallbacks(VideoFormat, null);
        Player.SetVideoCallbacks(Lock, null, Display);
    }

    // VLC negotiation: accept whatever chroma/dimensions the decoder proposes — don't override
    // them, which would create a format mismatch and force a converter insertion.
    // Cap at max buffer dimensions, then set per-plane pitches/lines for correct strides.
    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        width = Math.Min(width, MaxW);
        height = Math.Min(height, MaxH);
        _videoW = width;
        _videoH = height;
        _bitmapNeedsRebuild = true;

        var uvW = (width + 1) / 2;
        var uvH = (height + 1) / 2;
        pitches = width;
        Unsafe.Add(ref pitches, 1) = uvW;
        Unsafe.Add(ref pitches, 2) = uvW;
        lines = height;
        Unsafe.Add(ref lines, 1) = uvH;
        Unsafe.Add(ref lines, 2) = uvH;
        return 1; // number of picture surfaces; 0 = failure
    }

    // VLC render thread: hand it pointers to the Y/U/V planes of the back buffer
    private unsafe IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        var (yPin, uPin, vPin) = _front == 0 ? (_pinY1, _pinU1, _pinV1) : (_pinY0, _pinU0, _pinV0);
        IntPtr* p = (IntPtr*)planes;
        p[0] = yPin.AddrOfPinnedObject();
        p[1] = uPin.AddrOfPinnedObject();
        p[2] = vPin.AddrOfPinnedObject();
        return IntPtr.Zero;
    }

    // VLC render thread: swap buffers, queue one flush (drop duplicates)
    private void Display(IntPtr opaque, IntPtr picture)
    {
        _front ^= 1; // only this thread writes _front, volatile read in Flush is safe
        if (Interlocked.CompareExchange(ref _postPending, 1, 0) == 0)
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    // UI thread: convert I420 front buffer → BGRA into the WriteableBitmap
    private unsafe void Flush()
    {
        Interlocked.Exchange(ref _postPending, 0);
        if (_disposed) return;

        if (_bitmapNeedsRebuild)
        {
            _bitmapNeedsRebuild = false;
            _bitmap = new WriteableBitmap(
                new PixelSize((int)_videoW, (int)_videoH),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque);
            BitmapSourceChanged?.Invoke();
        }

        if (_bitmap == null) return;

        var (yPin, uPin, vPin) = _front == 0 ? (_pinY0, _pinU0, _pinV0) : (_pinY1, _pinU1, _pinV1);
        using var fb = _bitmap.Lock();
        ConvertI420ToBgra(
            (byte*)yPin.AddrOfPinnedObject(),
            (byte*)uPin.AddrOfPinnedObject(),
            (byte*)vPin.AddrOfPinnedObject(),
            (byte*)fb.Address, fb.RowBytes,
            (int)_videoW, (int)_videoH);

        FrameReady?.Invoke();

        if (!_firstFrame) return;
        _firstFrame = false;
        FirstFrameRendered?.Invoke(this, EventArgs.Empty);
    }

    // BT.601 YCbCr → BGRA; integer fast-path, no lookup tables
    private static unsafe void ConvertI420ToBgra(byte* y, byte* u, byte* v, byte* dst, int dstRowBytes, int w, int h)
    {
        var uvStride = (w + 1) / 2;
        for (int row = 0; row < h; row++)
        {
            byte* yRow = y + row * w;
            byte* uRow = u + (row >> 1) * uvStride;
            byte* vRow = v + (row >> 1) * uvStride;
            uint* dstRow = (uint*)(dst + row * dstRowBytes);

            for (int col = 0; col < w; col++)
            {
                int c = yRow[col] - 16;
                int d = uRow[col >> 1] - 128;
                int e = vRow[col >> 1] - 128;

                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;

                if (r < 0) r = 0; else if (r > 255) r = 255;
                if (g < 0) g = 0; else if (g > 255) g = 255;
                if (b < 0) b = 0; else if (b > 255) b = 255;

                // BGRA: B=byte0, G=byte1, R=byte2, A=byte3 (little-endian uint32)
                dstRow[col] = (uint)b | ((uint)g << 8) | ((uint)r << 16) | 0xFF000000u;
            }
        }
    }

    public void Play(string url)
    {
        _media?.Dispose();
        _media = new Media(_vlc, new Uri(url));
        Player.Play(_media);
    }

    // replay from the start after EndReached
    public void Replay()
    {
        Player.Stop();
        Player.Play();
    }

    // VLC 3.x: FFmpeg's picture pool teardown races with frame-decode threads during Stop(),
    // printing harmless get_buffer() errors. Suppress fd 2 for the duration of Stop() only.
    public Task StopAsync() => Task.Run(() =>
    {
        int saved = -1;
        if (OperatingSystem.IsMacOS())
        {
            var devNull = Native.open("/dev/null", Native.O_WRONLY);
            saved = Native.dup(2);
            Native.dup2(devNull, 2);
            Native.close(devNull);
        }
        try { Player.Stop(); }
        finally
        {
            if (saved >= 0) { Native.dup2(saved, 2); Native.close(saved); }
        }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // only stop if not already stopped (StopAsync may have already done it)
        if (Player.State is not (VLCState.Stopped or VLCState.NothingSpecial or VLCState.Error))
            Player.Stop();
        Player.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
        _pinY0.Free(); _pinU0.Free(); _pinV0.Free();
        _pinY1.Free(); _pinU1.Free(); _pinV1.Free();
    }
}
