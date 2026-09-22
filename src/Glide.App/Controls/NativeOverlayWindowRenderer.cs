using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Diagnostics;

namespace Glide.App.Controls;

/// <summary>
/// Worker-owned Win32 presentation for animated overlays. Avalonia supplies state and bitmap
/// copies; the worker owns the popup HWNDs, message queue, retained DIBs and motion-only moves.
/// </summary>
internal sealed class NativeOverlayWindowRenderer : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_DISABLED = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004;
    private const uint SWP_NOREDRAW = 0x0008, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_NOSENDCHANGING = 0x0400;
    private const uint ULW_ALPHA = 2, BI_RGB = 0, DIB_RGB_COLORS = 0, PM_REMOVE = 1;
    private const uint WM_QUIT = 0x12, WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
    private const int MA_NOACTIVATE = 3, ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly string ClassName = "Glide.NativeOverlay.4.2.4";
    private static readonly WindowProc WindowProcedure = NativeWindowProcedure;

    private sealed class Item
    {
        public required Guid Id;
        public IntPtr Hwnd;
        public Bitmap? SourceToken;
        public byte[] SourcePixels = Array.Empty<byte>();
        public int SourceWidth, SourceHeight;
        public double X, Y, Width, Height, Opacity;
        public Vector Velocity;
        public bool Running, AvoidOverlap, Removed, HasNativeRect;
        public Rect Bounds, SourceRect;
        public int Z, Hz, NativeX, NativeY, NativeWidth, NativeHeight, AppliedZ = int.MinValue;
        public long Revision, VisualRevision, RenderedVisualRevision;
        public OverlayChromeSelection Chrome;
        public Color Accent;
        public double PreviousX, PreviousY;
        public bool IsShown;
        public NativeSurface? Surface;
    }

    /// <summary>Top-down premultiplied BGRA DIB retained by one layered window.</summary>
    private sealed class NativeSurface : IDisposable
    {
        private readonly byte[] _pixels;
        private readonly int _width, _height;
        private readonly IntPtr _dc, _bitmap, _previousBitmap;
        private bool _disposed;
        public IntPtr Dc { get; }
        public IntPtr Bits { get; }
        public int Width => _width;
        public int Height => _height;

        private NativeSurface(int width, int height, IntPtr dc, IntPtr bitmap, IntPtr previousBitmap, IntPtr bits)
        {
            _width = width; _height = height; _dc = dc; _bitmap = bitmap; _previousBitmap = previousBitmap;
            Dc = dc; Bits = bits; _pixels = new byte[checked(width * height * 4)];
        }

        public static NativeSurface? Create(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 12000 || height > 12000) return null;
            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            var info = new NativeBitmapInfo { Header = new NativeBitmapInfoHeader {
                Size = (uint)Marshal.SizeOf<NativeBitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, Compression = BI_RGB } };
            var bitmap = CreateDIBSection(dc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) { DeleteDC(dc); return null; }
            var previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero) { DeleteObject(bitmap); DeleteDC(dc); return null; }
            try { return new NativeSurface(width, height, dc, bitmap, previous, bits); }
            catch { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); return null; }
        }

        public void Rasterize(byte[] source, int sourceWidth, int sourceHeight, Rect sourceRect, double opacity, double scale, OverlayChromeSelection chrome, Color accent)
        {
            Array.Clear(_pixels, 0, _pixels.Length);
            if (sourceWidth > 0 && sourceHeight > 0 && source.Length >= (long)sourceWidth * sourceHeight * 4)
            {
                var sx = Math.Clamp(sourceRect.X, 0, Math.Max(0, sourceWidth - 1));
                var sy = Math.Clamp(sourceRect.Y, 0, Math.Max(0, sourceHeight - 1));
                var sw = Math.Clamp(sourceRect.Width, 1.0, sourceWidth - sx);
                var sh = Math.Clamp(sourceRect.Height, 1.0, sourceHeight - sy);
                var imageOpacity = Math.Clamp(opacity, 0, 1);
                for (var y = 0; y < _height; y++)
                {
                    var sourceY = sy + (y + .5) * sh / _height - .5;
                    var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
                    var y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
                    var ty = sourceY - Math.Floor(sourceY);
                    for (var x = 0; x < _width; x++)
                    {
                        var sourceX = sx + (x + .5) * sw / _width - .5;
                        var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                        var x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                        var tx = sourceX - Math.Floor(sourceX);
                        var p00 = (y0 * sourceWidth + x0) * 4;
                        var p10 = (y0 * sourceWidth + x1) * 4;
                        var p01 = (y1 * sourceWidth + x0) * 4;
                        var p11 = (y1 * sourceWidth + x1) * 4;
                        var destinationOffset = (y * _width + x) * 4;
                        for (var channel = 0; channel < 4; channel++)
                        {
                            var top = source[p00 + channel] + (source[p10 + channel] - source[p00 + channel]) * tx;
                            var bottom = source[p01 + channel] + (source[p11 + channel] - source[p01 + channel]) * tx;
                            _pixels[destinationOffset + channel] = (byte)Math.Clamp((int)Math.Round((top + (bottom - top) * ty) * imageOpacity), 0, 255);
                        }
                    }
                }
            }
            if ((chrome & OverlayChromeSelection.Controls) != 0)
                DrawChrome(scale, opacity, accent, (chrome & OverlayChromeSelection.Active) != 0);
            Marshal.Copy(_pixels, 0, Bits, _pixels.Length);
        }

        private static (byte B, byte G, byte R, byte A) C(byte r, byte g, byte b, byte a) =>
            ((byte)(b * a / 255), (byte)(g * a / 255), (byte)(r * a / 255), a);

        private void DrawChrome(double scale, double opacity, Color accent, bool showGlow)
        {
            scale = Math.Max(.1, scale);
            var p = (double dip) => Math.Max(1, (int)Math.Round(dip * scale));
            var w = _width / scale; var h = _height / scale;
            var a = C(accent.R, accent.G, accent.B, accent.A); var white = C(255, 255, 255, 255); var glass = C(36, 41, 48, 220);
            if (showGlow) Border(0, 0, _width - 1, _height - 1, a, p(1));
            Ellipse(p(w - 21), p(20), p(13), glass); Line(p(w - 25), p(16), p(w - 17), p(24), white, p(2)); Line(p(w - 17), p(16), p(w - 25), p(24), white, p(2));
            RectFill(p(w - 28), p(h - 28), p(24), p(24), glass); Border(p(w - 28), p(h - 28), p(w - 5), p(h - 5), a, p(1));
            for (var k = 0; k < 3; k++) Line(p(w - 7 - k * 5), p(h - 5), p(w - 5), p(h - 7 - k * 5), a, p(1));
            // Large explicit axis-resize buttons matching managed hit rectangles.
            var widthLeft = Math.Max(0, p(w - 22)); var widthTop = Math.Max(0, p(h / 2 - 22));
            RectFill(widthLeft, widthTop, p(22), p(44), glass); Border(widthLeft, widthTop, Math.Min(_width - 1, widthLeft + p(21)), Math.Min(_height - 1, widthTop + p(43)), a, p(1));
            var wcx = widthLeft + p(11); var wcy = widthTop + p(22);
            Line(wcx - p(7), wcy, wcx + p(7), wcy, white, p(2));
            Line(wcx - p(7), wcy, wcx - p(3), wcy - p(4), white, p(2)); Line(wcx - p(7), wcy, wcx - p(3), wcy + p(4), white, p(2));
            Line(wcx + p(7), wcy, wcx + p(3), wcy - p(4), white, p(2)); Line(wcx + p(7), wcy, wcx + p(3), wcy + p(4), white, p(2));

            var heightLeft = Math.Max(0, p(w / 2 - 22)); var heightTop = 0;
            RectFill(heightLeft, heightTop, p(44), p(22), glass); Border(heightLeft, heightTop, Math.Min(_width - 1, heightLeft + p(43)), Math.Min(_height - 1, p(21)), a, p(1));
            var hcx = heightLeft + p(22); var hcy = p(11);
            Line(hcx, hcy - p(6), hcx, hcy + p(6), white, p(2));
            Line(hcx, hcy - p(6), hcx - p(4), hcy - p(2), white, p(2)); Line(hcx, hcy - p(6), hcx + p(4), hcy - p(2), white, p(2));
            Line(hcx, hcy + p(6), hcx - p(4), hcy + p(2), white, p(2)); Line(hcx, hcy + p(6), hcx + p(4), hcy + p(2), white, p(2));
            var sliderHeight = Math.Min(130.0, Math.Max(70.0, h - 80)); var sliderTop = Math.Max(0, (h - sliderHeight) / 2); RectFill(p(8), p(sliderTop), p(22), p(sliderHeight), glass); Line(p(19), p(sliderTop + 10), p(19), p(sliderTop + sliderHeight - 10), white, p(2)); Ellipse(p(19), p(sliderTop + sliderHeight - 10 - (sliderHeight - 20) * Math.Clamp(opacity, .1, 1)), p(5), a);
            var minusX = Math.Max(p(4), p(w / 2 - 36)); var plusX = Math.Max(p(4), p(w / 2 + 6)); RectFill(minusX, p(h - 38), p(30), p(30), glass); Border(minusX, p(h - 38), minusX + p(29), p(h - 9), a, p(1)); RectFill(plusX, p(h - 38), p(30), p(30), glass); Border(plusX, p(h - 38), plusX + p(29), p(h - 9), a, p(1));
            Line(minusX + p(5), p(h - 23), minusX + p(25), p(h - 23), white, p(2)); Line(plusX + p(5), p(h - 23), plusX + p(25), p(h - 23), white, p(2)); Line(plusX + p(15), p(h - 28), plusX + p(15), p(h - 18), white, p(2));
        }

        private void Border(int left, int top, int right, int bottom, (byte B, byte G, byte R, byte A) c, int thickness) { Line(left, top, right, top, c, thickness); Line(left, bottom, right, bottom, c, thickness); Line(left, top, left, bottom, c, thickness); Line(right, top, right, bottom, c, thickness); }
        private void RectFill(int left, int top, int width, int height, (byte B, byte G, byte R, byte A) c) { for (var y = Math.Max(0, top); y < Math.Min(_height, top + height); y++) for (var x = Math.Max(0, left); x < Math.Min(_width, left + width); x++) Blend(x, y, c); }
        private void Ellipse(int cx, int cy, int radius, (byte B, byte G, byte R, byte A) c) { var r2 = radius * radius; for (var y = cy - radius; y <= cy + radius; y++) for (var x = cx - radius; x <= cx + radius; x++) if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2) Blend(x, y, c); }
        private void Line(int x0, int y0, int x1, int y1, (byte B, byte G, byte R, byte A) c, int thickness)
        {
            var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1; var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1; var error = dx + dy;
            while (true) { for (var oy = -thickness / 2; oy <= thickness / 2; oy++) for (var ox = -thickness / 2; ox <= thickness / 2; ox++) Blend(x0 + ox, y0 + oy, c); if (x0 == x1 && y0 == y1) return; var e2 = 2 * error; if (e2 >= dy) { error += dy; x0 += sx; } if (e2 <= dx) { error += dx; y0 += sy; } }
        }
        private void Blend(int x, int y, (byte B, byte G, byte R, byte A) s)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height || s.A == 0) return; var o = (y * _width + x) * 4; var inverse = 255 - s.A;
            _pixels[o] = (byte)Math.Min(255, s.B + _pixels[o] * inverse / 255); _pixels[o + 1] = (byte)Math.Min(255, s.G + _pixels[o + 1] * inverse / 255); _pixels[o + 2] = (byte)Math.Min(255, s.R + _pixels[o + 2] * inverse / 255); _pixels[o + 3] = (byte)Math.Min(255, s.A + _pixels[o + 3] * inverse / 255);
        }
        public void Dispose() { if (_disposed) return; _disposed = true; if (_previousBitmap != IntPtr.Zero) SelectObject(_dc, _previousBitmap); if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap); if (_dc != IntPtr.Zero) DeleteDC(_dc); }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Item> _items = new();
    private readonly List<Item> _frameItems = new();
    private readonly List<Guid> _removeIds = new();
    private readonly HashSet<Guid> _publishIds = new();
    private readonly Dictionary<Guid, OverlayCompositionMotion> _relayMotion = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly IntPtr _ownerHwnd;
    private readonly string _windowClassName = ClassName;
    private Action<IReadOnlyDictionary<Guid, OverlayCompositionMotion>>? _sink;
    private int _postPending, _ownerHidden = -1, _suppressed;
    private long _lastRelayTicks;
    private int _hostClientOffsetX, _hostClientOffsetY;
    private double _hostScale = 1;
    private bool _classRegistered, _canStart;
    private int _nativeReady;

    public bool IsAvailable => _canStart && Volatile.Read(ref _nativeReady) != 0 && !_stop.IsCancellationRequested;

    public NativeOverlayWindowRenderer(Window owner)
    {
        _ownerHwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _canStart = OperatingSystem.IsWindows() && _ownerHwnd != IntPtr.Zero;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Glide native overlay motion" };
        try { _thread.Priority = ThreadPriority.AboveNormal; } catch { }
        if (_canStart) { _thread.Start(); _ready.Wait(1000); }
        OverlayNavigationTrace.Mark(IsAvailable ? OverlayNavigationTrace.Kind.BackendStarted : OverlayNavigationTrace.Kind.BackendUnavailable,
            a: IsAvailable ? 1 : 0, detail: "NativeOverlayWindowRenderer");
    }

    public void Publish(OverlayRenderSnapshot snapshot)
    {
        if (!IsAvailable || _stop.IsCancellationRequested) return;
        _sink = snapshot.PositionSink;
        var ownerOrigin = new NativePoint();
        if (ClientToScreen(_ownerHwnd, ref ownerOrigin))
        {
            Volatile.Write(ref _hostClientOffsetX, snapshot.HostScreenOrigin.X - ownerOrigin.X);
            Volatile.Write(ref _hostClientOffsetY, snapshot.HostScreenOrigin.Y - ownerOrigin.Y);
        }
        Volatile.Write(ref _hostScale, snapshot.RenderScaling);
        Dictionary<Guid, (byte[] Pixels, int Width, int Height)>? preparedBitmaps = null;
        lock (_gate)
        {
            foreach (var source in snapshot.Items)
            {
                if (source.Bitmap is not null &&
                    (!_items.TryGetValue(source.Id, out var existing) || !ReferenceEquals(existing.SourceToken, source.Bitmap)))
                {
                    preparedBitmaps ??= new();
                    preparedBitmaps[source.Id] = default;
                }
            }
        }
        if (preparedBitmaps is not null)
        {
            foreach (var source in snapshot.Items)
            {
                if (preparedBitmaps.ContainsKey(source.Id))
                {
                    if (source.Bitmap is not null && TryCopyBitmap(source.Bitmap, out var pixels, out var width, out var height))
                        preparedBitmaps[source.Id] = (pixels, width, height);
                    else
                        preparedBitmaps[source.Id] = (Array.Empty<byte>(), 0, 0);
                }
            }
        }

        var publishWait = OverlayNavigationTrace.Now();
        Monitor.Enter(_gate);
        var publishAcquired = OverlayNavigationTrace.Now();
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.GateWait, a: publishAcquired - publishWait, detail: "publish");
        try
        {
            _publishIds.Clear();
            foreach (var source in snapshot.Items) _publishIds.Add(source.Id);
            foreach (var item in _items.Values) if (!_publishIds.Contains(item.Id)) { item.Removed = true; item.Running = false; }
            foreach (var source in snapshot.Items)
            {
                if (!_items.TryGetValue(source.Id, out var item)) { item = new Item { Id = source.Id }; _items.Add(source.Id, item); }
                item.Removed = false;
                if (!ReferenceEquals(item.SourceToken, source.Bitmap))
                {
                    if (preparedBitmaps is not null && preparedBitmaps.TryGetValue(source.Id, out var prepared))
                    {
                        item.SourcePixels = prepared.Pixels; item.SourceWidth = prepared.Width; item.SourceHeight = prepared.Height;
                    }
                    else if (TryCopyBitmap(source.Bitmap, out var pixels, out var width, out var height))
                    {
                        item.SourcePixels = pixels; item.SourceWidth = width; item.SourceHeight = height;
                    }
                    else
                    {
                        item.SourcePixels = Array.Empty<byte>(); item.SourceWidth = item.SourceHeight = 0;
                    }
                    item.SourceToken = source.Bitmap; item.VisualRevision++;
                }
                if (item.SourceRect != source.SourceRect || Math.Abs(item.Width - source.DestinationRect.Width) > .0001 || Math.Abs(item.Height - source.DestinationRect.Height) > .0001 || Math.Abs(item.Opacity - source.Opacity) > .0001 || item.Chrome != source.Chrome || item.Accent != snapshot.AccentColor) item.VisualRevision++;
                item.SourceRect = source.SourceRect; item.Opacity = Math.Clamp(source.Opacity, 0, 1); item.Chrome = source.Chrome; item.Accent = snapshot.AccentColor; item.Width = source.DestinationRect.Width; item.Height = source.DestinationRect.Height;
                var wasRunning = item.Running; var motionChanged = item.Revision != source.MotionRevision;
                // Match the compositor ownership model: while native animation is running, the
                // worker owns X/Y. MotionRevision is used for velocity/direction changes and must
                // not rewind position to the UI's throttled (therefore slightly stale) relay. That
                // rewind was the source of periodic zig-zag/jitter. A stopped/paused item still
                // accepts manager geometry immediately, preserving drag/resize semantics.
                if (!wasRunning || !source.Running) { item.X = source.DestinationRect.X; item.Y = source.DestinationRect.Y; }
                if (!wasRunning || !source.Running || motionChanged) item.Velocity = source.Velocity;
                item.Revision = source.MotionRevision;
                item.Running = source.Running; item.AvoidOverlap = source.AvoidOverlap; item.Bounds = source.LegalBounds; item.Z = source.Z; item.Hz = source.TargetHz;
                if (OverlayNavigationTrace.Enabled && (wasRunning != item.Running || motionChanged))
                    OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MotionState, item.Id,
                        a: item.Running ? 1 : 0, b: motionChanged ? 1 : 0, x: item.X, y: item.Y,
                        u: item.Velocity.X, v: item.Velocity.Y, detail: "publish");
            }
        }
        finally
        {
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.GateHeld, a: OverlayNavigationTrace.Now() - publishAcquired, detail: "publish");
            Monitor.Exit(_gate);
        }
        _wake.Set();
    }

    private void Loop()
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        if (!RegisterNativeClass()) { _ready.Set(); return; }
        Volatile.Write(ref _nativeReady, 1); _ready.Set(); timeBeginPeriod(1);
        try
        {
            BeginMmcss();
            var clock = Stopwatch.StartNew(); var last = clock.Elapsed.TotalSeconds;
            var lastTick = Stopwatch.GetTimestamp();
            while (!_stop.IsCancellationRequested)
            {
                var tickNow = Stopwatch.GetTimestamp();
                var tickInterval = tickNow - lastTick; lastTick = tickNow;
                var gcPauseTicks = GC.GetTotalPauseDuration().Ticks;
                PumpMessages();
                var syncStart = OverlayNavigationTrace.Now(); SyncWindows();
                OverlayNavigationTrace.Duration(OverlayNavigationTrace.Kind.SyncWindows, syncStart);
                var start = Stopwatch.GetTimestamp(); var now = clock.Elapsed.TotalSeconds;
                var rawDt = now - last; var dt = Math.Clamp(rawDt, 0, .1); last = now;
                OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.LoopTick, a: tickInterval, b: gcPauseTicks,
                    x: rawDt * 1000.0, y: dt * 1000.0);

                var advanceWait = OverlayNavigationTrace.Now(); Monitor.Enter(_gate); var advanceAcquired = OverlayNavigationTrace.Now();
                OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.GateWait, a: advanceAcquired - advanceWait, detail: "advance");
                try
                {
                    _frameItems.Clear(); foreach (var item in _items.Values) if (!item.Removed) _frameItems.Add(item); _frameItems.Sort(static (a, b) => a.Z.CompareTo(b.Z));
                    foreach (var item in _frameItems) { item.PreviousX = item.X; item.PreviousY = item.Y; if (item.Running) Advance(item, dt); } Collide(_frameItems, dt);
                    Position(_frameItems, dt);
                    PublishMotion(_frameItems);
                }
                finally
                {
                    OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.GateHeld, a: OverlayNavigationTrace.Now() - advanceAcquired, detail: "advance");
                    Monitor.Exit(_gate);
                }

                var hz = 60; foreach (var item in _frameItems) if (item.Running && item.Hz > hz) hz = item.Hz; var period = Math.Max(1L, (long)Math.Round(Stopwatch.Frequency / (double)Math.Clamp(hz, 1, 360))); var deadline = start + period; WaitForNextFrame(deadline);
            }
        }
        finally { EndMmcss(); DestroyAllWindowsOnWorker(); if (_classRegistered) UnregisterClassW(_windowClassName, GetModuleHandleW(null)); timeEndPeriod(1); }
    }

    private void SyncWindows()
    {
        var wait = OverlayNavigationTrace.Now(); Monitor.Enter(_gate); var acquired = OverlayNavigationTrace.Now();
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.GateWait, a: acquired - wait, detail: "sync");
        try
        {
            _removeIds.Clear();
            foreach (var item in _items.Values)
            {
                if (item.Removed) { DestroyItemWindow(item); _removeIds.Add(item.Id); continue; }
                if (item.Hwnd == IntPtr.Zero) { item.Hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, _windowClassName, "Glide overlay", WS_POPUP | WS_DISABLED, 0, 0, 1, 1, _ownerHwnd, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero); if (item.Hwnd != IntPtr.Zero) ShowWindow(item.Hwnd, SW_HIDE); }
                if (item.Hwnd != IntPtr.Zero && item.SourcePixels.Length != 0)
                {
                    var ensureStart = OverlayNavigationTrace.Now(); EnsureSurface(item);
                    OverlayNavigationTrace.Duration(OverlayNavigationTrace.Kind.EnsureSurface, ensureStart, item.Id);
                }
            }
            foreach (var id in _removeIds) _items.Remove(id);
        }
        finally
        {
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.GateHeld, a: OverlayNavigationTrace.Now() - acquired, detail: "sync");
            Monitor.Exit(_gate);
        }
    }

    private void EnsureSurface(Item item)
    {
        var scale = Math.Max(.1, Volatile.Read(ref _hostScale)); var width = Math.Max(1, (int)Math.Round(item.Width * scale)); var height = Math.Max(1, (int)Math.Round(item.Height * scale));
        if (item.Surface is null || item.Surface.Width != width || item.Surface.Height != height)
        {
            item.Surface?.Dispose(); item.Surface = NativeSurface.Create(width, height); item.RenderedVisualRevision = 0;
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.SurfaceRecreated, item.Id, a: width, b: height);
        }
        if (item.Surface is null || item.RenderedVisualRevision == item.VisualRevision) return;
        item.Surface.Rasterize(item.SourcePixels, item.SourceWidth, item.SourceHeight, item.SourceRect, item.Opacity, scale, item.Chrome, item.Accent); var size = new NativeSize { Width = item.Surface.Width, Height = item.Surface.Height }; var source = new NativePoint(); var blend = new NativeBlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // A null destination point preserves the position set by worker-owned SetWindowPos.
        var ulwStart = OverlayNavigationTrace.Now();
        UpdateLayeredWindow(item.Hwnd, IntPtr.Zero, IntPtr.Zero, ref size, item.Surface.Dc, ref source, 0, ref blend, ULW_ALPHA);
        OverlayNavigationTrace.Duration(OverlayNavigationTrace.Kind.UpdateLayeredWindow, ulwStart, item.Id, a: width, b: height);
        item.RenderedVisualRevision = item.VisualRevision;
    }

    private void Position(IReadOnlyList<Item> items, double dt)
    {
        var visible = IsWindowVisible(_ownerHwnd) && IsWindowEnabled(_ownerHwnd) && !IsIconic(_ownerHwnd) && Volatile.Read(ref _suppressed) == 0;
        if (!visible) { if (Interlocked.Exchange(ref _ownerHidden, 1) != 1) foreach (var item in items) SetShown(item, false); return; }
        Interlocked.Exchange(ref _ownerHidden, 0);
        var origin = new NativePoint(); if (!ClientToScreen(_ownerHwnd, ref origin) || !GetClientRect(_ownerHwnd, out var client)) return; var hostOrigin = new NativePoint { X = origin.X + Volatile.Read(ref _hostClientOffsetX), Y = origin.Y + Volatile.Read(ref _hostClientOffsetY) }; var scale = Math.Max(.1, Volatile.Read(ref _hostScale)); ApplyZOrder(items);
        foreach (var item in items)
        {
            if (item.Hwnd == IntPtr.Zero || item.Surface is null) continue; var x = hostOrigin.X + (int)Math.Round(item.X * scale); var y = hostOrigin.Y + (int)Math.Round(item.Y * scale); var width = Math.Max(1, (int)Math.Round(item.Width * scale)); var height = Math.Max(1, (int)Math.Round(item.Height * scale));
            if (item.Surface.Width != width || item.Surface.Height != height) { SetShown(item, false); item.HasNativeRect = false; continue; }
            var inside = x >= origin.X && y >= origin.Y && x + width <= origin.X + client.Right && y + height <= origin.Y + client.Bottom;
            if (!inside) { SetShown(item, false); item.HasNativeRect = false; continue; }
            if (!item.HasNativeRect || item.NativeX != x || item.NativeY != y || item.NativeWidth != width || item.NativeHeight != height)
            {
                var swpStart = OverlayNavigationTrace.Now();
                var flags = SWP_NOZORDER | SWP_NOREDRAW | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
                if (item.HasNativeRect && item.NativeWidth == width && item.NativeHeight == height)
                    flags |= SWP_NOSIZE;
                SetWindowPos(item.Hwnd, IntPtr.Zero, x, y, width, height, flags);
                OverlayNavigationTrace.Duration(OverlayNavigationTrace.Kind.SetWindowPos, swpStart, item.Id, a: x, b: y, x: item.X, y: item.Y, u: dt * 1000.0, v: item.Running ? 1 : 0);
                item.NativeX = x; item.NativeY = y; item.NativeWidth = width; item.NativeHeight = height; item.HasNativeRect = true;
            }
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MotionSample, item.Id, a: x, b: y,
                x: item.X, y: item.Y, u: dt * 1000.0, v: item.Running ? 1 : 0);
            SetShown(item, true);
        }
    }

    private static void SetShown(Item item, bool shown)
    {
        if (item.Hwnd == IntPtr.Zero || item.IsShown == shown) return;
        ShowWindow(item.Hwnd, shown ? SW_SHOWNOACTIVATE : SW_HIDE);
        item.IsShown = shown;
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MotionState, item.Id, a: shown ? 1 : 0, detail: "visibility");
    }

    private void ApplyZOrder(IReadOnlyList<Item> items)
    {
        // Keep overlays in the owner's z-order island instead of promoting each HWND into the
        // global TOPMOST/NONTOPMOST bands.  Chaining each retained popup directly above Glide keeps
        // overlays above the canvas, preserves per-overlay Z, follows Glide's own topmost state via
        // ownership, and leaves later Glide-owned menus/dialogs above the overlays.
        var dirty = false;
        foreach (var item in items)
            if (item.Hwnd != IntPtr.Zero && item.AppliedZ != item.Z) { dirty = true; break; }
        if (!dirty) return;

        var insertAfter = _ownerHwnd;
        foreach (var item in items)
        {
            if (item.Hwnd == IntPtr.Zero) continue;
            SetWindowPos(item.Hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            item.AppliedZ = item.Z;
            insertAfter = item.Hwnd;
        }
    }

    private void WaitForNextFrame(long deadline)
    {
        while (!_stop.IsCancellationRequested) { PumpMessages(); var remaining = deadline - Stopwatch.GetTimestamp(); if (remaining <= 0) return; var ms = remaining * 1000.0 / Stopwatch.Frequency; if (ms > 2) _wake.WaitOne(Math.Max(1, (int)Math.Floor(ms - 1))); else Thread.SpinWait(100); }
    }
    private void PumpMessages() { while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PM_REMOVE)) { if (message.Message == WM_QUIT) { _stop.Cancel(); break; } TranslateMessage(ref message); DispatchMessage(ref message); } }
    private void DestroyItemWindow(Item item) { if (item.Hwnd != IntPtr.Zero) { SetShown(item, false); DestroyWindow(item.Hwnd); } item.Hwnd = IntPtr.Zero; item.Surface?.Dispose(); item.Surface = null; item.HasNativeRect = false; }
    private void DestroyAllWindowsOnWorker() { lock (_gate) { foreach (var item in _items.Values) DestroyItemWindow(item); _items.Clear(); } }

    private void PublishMotion(IReadOnlyList<Item> items)
    {
        var sink = _sink;
        if (sink is null) return;
        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref _lastRelayTicks);
        // Native HWND motion remains at the configured 120/144+ Hz.  The UI only needs a modest
        // position relay for hit testing/persistence; keeping it at 45 Hz avoids dispatcher/GC
        // pressure during rapid image navigation without making interaction feel detached.
        if (previous != 0 && (now - previous) / (double)Stopwatch.Frequency < 1.0 / 45.0) return;
        if (Interlocked.Exchange(ref _postPending, 1) != 0) return;
        Volatile.Write(ref _lastRelayTicks, now);
        _relayMotion.Clear();
        foreach (var item in items)
            _relayMotion[item.Id] = new OverlayCompositionMotion(new Point(item.X, item.Y), item.Velocity);
        Dispatcher.UIThread.Post(() =>
        {
            try { sink(_relayMotion); }
            finally { Interlocked.Exchange(ref _postPending, 0); }
        }, DispatcherPriority.Background);
    }

    private IntPtr _mmcssHandle;

    private IntPtr BeginMmcss()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        try
        {
            _mmcssHandle = AvSetMmThreadCharacteristicsW("Games", out _);
            if (_mmcssHandle != IntPtr.Zero) AvSetMmThreadPriority(_mmcssHandle, 1);
        }
        catch { _mmcssHandle = IntPtr.Zero; }
        return _mmcssHandle;
    }

    private void EndMmcss()
    {
        var handle = _mmcssHandle;
        _mmcssHandle = IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }

    private static bool TryCopyBitmap(Bitmap bitmap, out byte[] pixels, out int width, out int height)
    {
        pixels = Array.Empty<byte>(); width = height = 0; try { var size = bitmap.PixelSize; width = size.Width; height = size.Height; if (width <= 0 || height <= 0 || (long)width * height > 268_000_000) return false; var stride = checked(width * 4); var length = checked(stride * height); pixels = new byte[length]; var memory = Marshal.AllocHGlobal(length); try { bitmap.CopyPixels(new PixelRect(0, 0, width, height), memory, length, stride); Marshal.Copy(memory, pixels, 0, length); return true; } finally { Marshal.FreeHGlobal(memory); } } catch { pixels = Array.Empty<byte>(); width = height = 0; return false; }
    }

    private bool RegisterNativeClass()
    {
        var className = Marshal.StringToHGlobalUni(_windowClassName);
        try
        {
            var registration = new NativeWindowClass { Size = (uint)Marshal.SizeOf<NativeWindowClass>(), WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure), Instance = GetModuleHandleW(null), ClassName = className };
            if (RegisterClassExW(ref registration) != 0) { _classRegistered = true; return true; }
            if (Marshal.GetLastWin32Error() == ERROR_CLASS_ALREADY_EXISTS) { _classRegistered = true; return true; }
            return false;
        }
        finally { Marshal.FreeHGlobal(className); }
    }

    public void SetTopmost(bool topmost)
    {
        // Native ownership follows the Glide owner's topmost band automatically. Re-chain the
        // overlay island after a policy change without promoting any overlay globally.
        lock (_gate) foreach (var item in _items.Values) item.AppliedZ = int.MinValue;
        _wake.Set();
    }
    public void SetModalSuppressed(bool suppressed)
    {
        Volatile.Write(ref _suppressed, suppressed ? 1 : 0);
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MotionState, a: suppressed ? 1 : 0, detail: "modal_suppressed");
        _wake.Set();
    }
    public void Dispose() { if (_stop.IsCancellationRequested) return; _stop.Cancel(); _wake.Set(); if (_thread.IsAlive) _thread.Join(); _ready.Dispose(); _wake.Dispose(); _stop.Dispose(); }

    private static void Advance(Item item, double dt)
    {
        var speed = Math.Max(1, item.Velocity.Length);
        var velocity = item.Velocity.Length > .001 ? item.Velocity * (speed / item.Velocity.Length) : new Vector(speed, 0);
        var margin = Math.Clamp(speed * 0.9 + 30, 48, 360);
        var steer = new Vector();
        AddBoundarySteer(ref steer, item.X - item.Bounds.Left, velocity.X < 0, new Vector(speed, 0), margin, speed);
        AddBoundarySteer(ref steer, item.Bounds.Right - item.X, velocity.X > 0, new Vector(-speed, 0), margin, speed);
        AddBoundarySteer(ref steer, item.Y - item.Bounds.Top, velocity.Y < 0, new Vector(0, speed), margin, speed);
        AddBoundarySteer(ref steer, item.Bounds.Bottom - item.Y, velocity.Y > 0, new Vector(0, -speed), margin, speed);
        var desired = velocity + steer;
        if (desired.Length > .001) desired *= speed / desired.Length;
        velocity = RotateTowards(velocity, desired, Math.PI * 2.2 * Math.Clamp(dt, 0, .05));
        item.X += velocity.X * dt;
        item.Y += velocity.Y * dt;
        ClampWithoutBounce(item, ref velocity);
        item.Velocity = velocity;
    }

    private static void AddBoundarySteer(ref Vector steer, double distance, bool headingOutward, Vector inward, double margin, double speed)
    {
        if (!headingOutward || distance >= margin) return;
        var strength = 1 - Math.Clamp(distance / margin, 0, 1);
        steer += inward * (0.65 + strength * strength * 2.35);
    }

    private static Vector RotateTowards(Vector current, Vector desired, double maxRadians)
    {
        var speed = Math.Max(1, current.Length);
        if (desired.Length <= .001 || maxRadians <= 0) return current;
        var from = Math.Atan2(current.Y, current.X);
        var to = Math.Atan2(desired.Y, desired.X);
        var delta = Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
        var angle = from + Math.Clamp(delta, -maxRadians, maxRadians);
        return new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed);
    }

    private static void ClampWithoutBounce(Item item, ref Vector velocity)
    {
        var speed = Math.Max(1, velocity.Length);
        var inward = Math.Max(1, speed * 0.18);
        if (item.Bounds.Right <= item.Bounds.Left) { item.X = item.Bounds.Left; velocity = new Vector(0, velocity.Y); }
        else if (item.X <= item.Bounds.Left) { item.X = item.Bounds.Left; if (velocity.X < inward) velocity = new Vector(inward, velocity.Y); }
        else if (item.X >= item.Bounds.Right) { item.X = item.Bounds.Right; if (velocity.X > -inward) velocity = new Vector(-inward, velocity.Y); }
        if (item.Bounds.Bottom <= item.Bounds.Top) { item.Y = item.Bounds.Top; velocity = new Vector(velocity.X, 0); }
        else if (item.Y <= item.Bounds.Top) { item.Y = item.Bounds.Top; if (velocity.Y < inward) velocity = new Vector(velocity.X, inward); }
        else if (item.Y >= item.Bounds.Bottom) { item.Y = item.Bounds.Bottom; if (velocity.Y > -inward) velocity = new Vector(velocity.X, -inward); }
        if (velocity.Length > .001 && Math.Abs(velocity.Length - speed) > .001) velocity *= speed / velocity.Length;
    }

    private static void Collide(IReadOnlyList<Item> items, double dt)
    {
        for (var x = 0; x < items.Count - 1; x++) for (var y = x + 1; y < items.Count; y++)
        {
            var p = items[x]; var q = items[y];
            if ((!p.Running || !p.AvoidOverlap) && (!q.Running || !q.AvoidOverlap)) continue;
            var pr = new Rect(p.X, p.Y, p.Width, p.Height); var qr = new Rect(q.X, q.Y, q.Width, q.Height);
            var ox = Math.Min(pr.Right, qr.Right) - Math.Max(pr.Left, qr.Left);
            var oy = Math.Min(pr.Bottom, qr.Bottom) - Math.Max(pr.Top, qr.Top);
            if (ox <= 0 || oy <= 0) continue;
            var horizontal = ox <= oy;
            var d = horizontal ? (pr.Center.X <= qr.Center.X ? -1d : 1d) : (pr.Center.Y <= qr.Center.Y ? -1d : 1d);
            var distance = (horizontal ? ox : oy) + .05;
            var both = p.Running && p.AvoidOverlap && q.Running && q.AvoidOverlap;
            var turn = Math.PI * 4.0 * Math.Clamp(dt, 0.002, .05);
            if (p.Running && p.AvoidOverlap)
            {
                if (horizontal) p.X += d * distance / (both ? 2 : 1); else p.Y += d * distance / (both ? 2 : 1);
                p.Velocity = SteerAway(p.Velocity, horizontal, d, turn); Clamp(p);
            }
            if (q.Running && q.AvoidOverlap)
            {
                if (horizontal) q.X -= d * distance / (both ? 2 : 1); else q.Y -= d * distance / (both ? 2 : 1);
                q.Velocity = SteerAway(q.Velocity, horizontal, -d, turn); Clamp(q);
            }
        }
    }

    private static Vector SteerAway(Vector velocity, bool horizontal, double direction, double maxTurn)
    {
        var speed = Math.Max(1, velocity.Length);
        var tangent = horizontal ? new Vector(direction * speed, velocity.Y) : new Vector(velocity.X, direction * speed);
        if (tangent.Length > .001) tangent *= speed / tangent.Length;
        return RotateTowards(velocity, tangent, maxTurn);
    }
    private static void Clamp(Item item) { item.X = Math.Clamp(item.X, item.Bounds.Left, item.Bounds.Right); item.Y = Math.Clamp(item.Y, item.Bounds.Top, item.Bounds.Bottom); }
    private static IntPtr NativeWindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) => message == WM_NCHITTEST ? new IntPtr(-1) : message == WM_MOUSEACTIVATE ? new IntPtr(MA_NOACTIVATE) : DefWindowProcW(hwnd, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width; public int Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmapInfo { public NativeBitmapInfoHeader Header; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeWindowClass { public uint Size, Style; public IntPtr WindowProc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public NativePoint Point; public uint Private; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref NativeWindowClass windowClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(int exStyle, string className, string? title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? moduleName);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref NativeBitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, IntPtr destinationPoint, ref NativeSize size, IntPtr sourceDc, ref NativePoint sourcePoint, uint colorKey, ref NativeBlendFunction blend, uint flags);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);
    [DllImport("avrt.dll")] private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
