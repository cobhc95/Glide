using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Glide.App.Platform;

/// <summary>
/// Small Windows-only clipboard adapter for the two Glide semantics that Avalonia intentionally
/// abstracts poorly: decoded image pixels (CF_DIBV5) and the underlying image file (CF_HDROP).
/// No UI state lives here; callers supply an already-decoded bitmap/path.
/// </summary>
internal static class WindowsClipboard
{
    private const uint CfDibV5 = 17;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint BiBitFields = 3;
    private const uint LcsSrgb = 0x73524742;

    public static bool TrySetFileDrop(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var payload = Encoding.Unicode.GetBytes(path + "\0\0");
        var headerSize = Marshal.SizeOf<DropFiles>();
        var memory = GlobalAlloc(GmemMoveable, (nuint)(headerSize + payload.Length));
        if (memory == IntPtr.Zero) return false;

        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                var header = new DropFiles { pFiles = (uint)headerSize, fWide = 1 };
                Marshal.StructureToPtr(header, pointer, false);
                Marshal.Copy(payload, 0, IntPtr.Add(pointer, headerSize), payload.Length);
            }
            finally { GlobalUnlock(memory); }

            if (!TryOpenClipboard()) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                if (SetClipboardData(CfHDrop, memory) == IntPtr.Zero) return false;
                memory = IntPtr.Zero; // Windows now owns the HGLOBAL.
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    public static bool TrySetBitmap(Bitmap bitmap, Avalonia.PixelRect? requestedSource = null)
    {
        if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) return false;
        var full = new Avalonia.PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var source = requestedSource is { } requested ? Intersect(requested, full) : full;
        if (source.Width <= 0 || source.Height <= 0) return false;

        // Force one explicit BGRA8/unpremultiplied representation before crossing the Win32 ABI.
        // The source selection is always in original image pixels, never scaled viewport pixels.
        using var converted = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = converted.Lock();
        bitmap.CopyPixels(framebuffer, AlphaFormat.Unpremul);

        checked
        {
            var outputStride = source.Width * 4;
            var pixelBytes = new byte[outputStride * source.Height];
            for (var row = 0; row < source.Height; row++)
            {
                var sourceAddress = IntPtr.Add(framebuffer.Address, (source.Y + row) * framebuffer.RowBytes + source.X * 4);
                Marshal.Copy(sourceAddress, pixelBytes, row * outputStride, outputStride);
            }

            var header = BitmapV5Header.Create(source.Width, source.Height, (uint)pixelBytes.Length);
            var headerSize = Marshal.SizeOf<BitmapV5Header>();
            var memory = GlobalAlloc(GmemMoveable, (nuint)(headerSize + pixelBytes.Length));
            if (memory == IntPtr.Zero) return false;

            try
            {
                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero) return false;
                try
                {
                    Marshal.StructureToPtr(header, pointer, false);
                    Marshal.Copy(pixelBytes, 0, IntPtr.Add(pointer, headerSize), pixelBytes.Length);
                }
                finally { GlobalUnlock(memory); }

                if (!TryOpenClipboard()) return false;
                try
                {
                    if (!EmptyClipboard()) return false;
                    if (SetClipboardData(CfDibV5, memory) == IntPtr.Zero) return false;
                    memory = IntPtr.Zero;
                    return true;
                }
                finally { CloseClipboard(); }
            }
            finally
            {
                if (memory != IntPtr.Zero) GlobalFree(memory);
            }
        }
    }

    private static Avalonia.PixelRect Intersect(Avalonia.PixelRect a, Avalonia.PixelRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new Avalonia.PixelRect(left, top, right - left, bottom - top) : default;
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DropFiles
    {
        public uint pFiles;
        public int x;
        public int y;
        public int fNC;
        public int fWide;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CieXyz { public int X; public int Y; public int Z; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CieXyzTriple { public CieXyz Red; public CieXyz Green; public CieXyz Blue; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BitmapV5Header
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint AlphaMask;
        public uint CsType;
        public CieXyzTriple Endpoints;
        public uint GammaRed;
        public uint GammaGreen;
        public uint GammaBlue;
        public uint Intent;
        public uint ProfileData;
        public uint ProfileSize;
        public uint Reserved;

        public static BitmapV5Header Create(int width, int height, uint sizeImage) => new()
        {
            Size = 124,
            Width = width,
            Height = -height, // top-down DIB: row order matches Avalonia framebuffer order
            Planes = 1,
            BitCount = 32,
            Compression = BiBitFields,
            SizeImage = sizeImage,
            XPelsPerMeter = 3780,
            YPelsPerMeter = 3780,
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            AlphaMask = 0xFF000000,
            CsType = LcsSrgb
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
