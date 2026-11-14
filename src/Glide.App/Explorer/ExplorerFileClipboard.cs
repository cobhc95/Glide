using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Glide.App.Explorer;

internal sealed record ExplorerClipboardContent(IReadOnlyList<string> Paths, bool Cut);

/// <summary>File copy/cut clipboard used by the built-in Explorer (Windows shell compatible on Windows).</summary>
internal interface IExplorerFileClipboard
{
    bool TrySet(IReadOnlyList<string> paths, bool cut);
    ExplorerClipboardContent? TryGet();
    void Clear();
}

/// <summary>In-process clipboard: used by tests and as the non-Windows fallback.</summary>
internal sealed class MemoryExplorerFileClipboard : IExplorerFileClipboard
{
    private readonly object _gate = new();
    private ExplorerClipboardContent? _content;

    public bool TrySet(IReadOnlyList<string> paths, bool cut)
    {
        var copy = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (copy.Length == 0) return false;
        lock (_gate) _content = new ExplorerClipboardContent(copy, cut);
        return true;
    }

    public ExplorerClipboardContent? TryGet()
    {
        lock (_gate) return _content;
    }

    public void Clear()
    {
        lock (_gate) _content = null;
    }
}

internal static class ExplorerFileClipboard
{
    public static IExplorerFileClipboard Current { get; set; } =
        OperatingSystem.IsWindows() ? new WindowsExplorerFileClipboard() : new MemoryExplorerFileClipboard();
}

/// <summary>
/// Real Win32 clipboard: CF_HDROP (wide DROPFILES) plus the "Preferred DropEffect" DWORD, so
/// copy/cut interoperates with Windows Explorer in both directions.
/// </summary>
internal sealed partial class WindowsExplorerFileClipboard : IExplorerFileClipboard
{
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const int DropFilesHeaderSize = 20;
    private const string PreferredDropEffectName = "Preferred DropEffect";

    public bool TrySet(IReadOnlyList<string> paths, bool cut)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var full = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) continue;
            string resolved;
            try { resolved = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (seen.Add(resolved)) full.Add(resolved);
        }
        if (full.Count == 0) return false;

        var effectFormat = RegisterClipboardFormatW(PreferredDropEffectName);
        if (effectFormat == 0) return false;

        var dropMemory = Allocate(BuildDropFiles(full));
        if (dropMemory == IntPtr.Zero) return false;
        var effectBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(effectBytes, cut ? DropEffectMove : DropEffectCopy);
        var effectMemory = Allocate(effectBytes);
        if (effectMemory == IntPtr.Zero)
        {
            GlobalFree(dropMemory);
            return false;
        }

        try
        {
            if (!TryOpenClipboard()) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                if (SetClipboardData(CfHDrop, dropMemory) == IntPtr.Zero) return false;
                dropMemory = IntPtr.Zero; // Windows owns the HGLOBAL now.
                if (SetClipboardData(effectFormat, effectMemory) == IntPtr.Zero)
                {
                    EmptyClipboard(); // never leave a "cut" that would paste as a copy
                    return false;
                }
                effectMemory = IntPtr.Zero;
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (dropMemory != IntPtr.Zero) GlobalFree(dropMemory);
            if (effectMemory != IntPtr.Zero) GlobalFree(effectMemory);
        }
    }

    public ExplorerClipboardContent? TryGet()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var effectFormat = RegisterClipboardFormatW(PreferredDropEffectName);
        if (!TryOpenClipboard()) return null;
        try
        {
            if (!IsClipboardFormatAvailable(CfHDrop)) return null;
            var drop = ReadGlobal(GetClipboardData(CfHDrop));
            if (drop is null) return null;

            var existing = UsablePastePaths(ParseDropFiles(drop));
            if (existing.Length == 0) return null;

            var cut = false;
            if (effectFormat != 0 && IsClipboardFormatAvailable(effectFormat))
            {
                var effect = ReadGlobal(GetClipboardData(effectFormat));
                if (effect is { Length: >= 4 }) cut = (BinaryPrimitives.ReadInt32LittleEndian(effect) & DropEffectMove) != 0;
            }
            return new ExplorerClipboardContent(existing, cut);
        }
        finally { CloseClipboard(); }
    }

    public void Clear()
    {
        if (!OperatingSystem.IsWindows() || !TryOpenClipboard()) return;
        try
        {
            // Only clear our own kind of content; leave a user's copied text/image alone.
            if (IsClipboardFormatAvailable(CfHDrop)) EmptyClipboard();
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Builds a wide DROPFILES block (header + NUL-separated paths + extra NUL).</summary>
    internal static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        var text = new StringBuilder();
        foreach (var path in paths) text.Append(path).Append('\0');
        text.Append('\0');
        var payload = Encoding.Unicode.GetBytes(text.ToString());
        var buffer = new byte[DropFilesHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), DropFilesHeaderSize); // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), 1);                    // fWide
        payload.CopyTo(buffer, DropFilesHeaderSize);
        return buffer;
    }

    /// <summary>
    /// Clipboard paths that a paste may act on. Any program can put CF_HDROP on the clipboard; a relative
    /// entry would otherwise resolve against Glide's current directory, so a "cut" could move files the
    /// user never selected. Windows Explorer always writes fully qualified paths, so nothing real is lost.
    /// </summary>
    internal static string[] UsablePastePaths(IEnumerable<string> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p) && p.IndexOf('\0') < 0 && Path.IsPathFullyQualified(p) &&
                         (File.Exists(p) || Directory.Exists(p)))
             .ToArray();

    /// <summary>Parses a DROPFILES block (wide or ANSI) into its paths.</summary>
    internal static IReadOnlyList<string> ParseDropFiles(ReadOnlySpan<byte> buffer)
    {
        var result = new List<string>();
        if (buffer.Length < DropFilesHeaderSize) return result;
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(16, 4)) != 0;
        if (offset < DropFilesHeaderSize || offset > buffer.Length) return result;

        var data = buffer[(int)offset..];
        if (wide) data = data[..(data.Length & ~1)];
        var text = wide ? Encoding.Unicode.GetString(data) : Encoding.Default.GetString(data);
        foreach (var part in text.Split('\0'))
        {
            if (part.Length == 0) break; // double NUL terminates the list
            result.Add(part);
        }
        return result;
    }

    private static IntPtr Allocate(byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }

    private static byte[]? ReadGlobal(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var size = (int)Math.Min((nuint)int.MaxValue, GlobalSize(handle));
        if (size <= 0) return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            // The clipboard can be briefly held by another process; callers are synchronous.
            Thread.Sleep(15);
        }
        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetClipboardData(uint format, IntPtr memory);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(IntPtr memory);
}
