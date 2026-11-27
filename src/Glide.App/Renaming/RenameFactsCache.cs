using System.Collections.Concurrent;
using Glide.App.Explorer;
using Glide.Core;
using Glide.Core.Renaming;

namespace Glide.App.Renaming;

/// <summary>
/// What the batch rename dialog knows about its files besides their names: the EXIF date taken, the date modified and the picture
/// size. Each is read at most once per file, only when the template (or the sort order) needs it, and only on a background thread
/// (<see cref="Load"/>); the UI thread just asks <see cref="IsReady"/> and <see cref="Get"/>. The probes are the Explorer's own
/// (<see cref="ExplorerModel.ProbeDateTaken"/>, <see cref="ExplorerModel.ProbePixelSize"/>) and can be replaced by tests.
/// </summary>
internal sealed class RenameFactsCache
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public DateTime? Taken, Modified;
        public int Width, Height;
        public bool HasTaken, HasModified, HasSize;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Test seam: the EXIF date taken of a picture (null: none).</summary>
    internal Func<string, DateTime?> TakenProbe { get; set; } = path => ExplorerModel.ProbeDateTaken(path);

    /// <summary>Test seam: the date modified of a file or folder (null: unknown).</summary>
    internal Func<BatchRenameSource, DateTime?> ModifiedProbe { get; set; } = DefaultModified;

    /// <summary>Test seam: the pixel size of a picture ((0, 0): unknown).</summary>
    internal Func<string, (int Width, int Height)> SizeProbe { get; set; } = path =>
    {
        var size = ExplorerModel.ProbePixelSize(path);
        return size is { } s ? (s.Width, s.Height) : (0, 0);
    };

    /// <summary>How many probes ran (the "once per file" guarantee is tested with it).</summary>
    public int ProbeCount => Volatile.Read(ref _probes);
    private int _probes;

    private static DateTime? DefaultModified(BatchRenameSource source)
    {
        try
        {
            if (source.IsDirectory) return Directory.Exists(source.Path) ? Directory.GetLastWriteTime(source.Path) : null;
            return File.Exists(source.Path) ? File.GetLastWriteTime(source.Path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private Entry EntryOf(BatchRenameSource source) => _entries.GetOrAdd(source.Path, _ => new Entry());

    /// <summary>True when every wanted fact of every source has been read already (nothing has to wait for the disk).</summary>
    public bool IsReady(IReadOnlyList<BatchRenameSource> sources, bool taken, bool modified, bool size)
    {
        if (!taken && !modified && !size) return true;
        foreach (var source in sources)
        {
            if (!_entries.TryGetValue(source.Path, out var e)) return false;
            lock (e.Gate)
                if ((taken && !e.HasTaken) || (modified && !e.HasModified) || (size && !e.HasSize)) return false;
        }
        return true;
    }

    /// <summary>Reads what is still missing. Call on a background thread; a file already read is not read again.</summary>
    public void Load(IReadOnlyList<BatchRenameSource> sources, bool taken, bool modified, bool size, CancellationToken token = default)
    {
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var e = EntryOf(source);
            bool needTaken, needModified, needSize;
            lock (e.Gate)
            {
                needTaken = taken && !e.HasTaken;
                needModified = modified && !e.HasModified;
                needSize = size && !e.HasSize;
            }
            var isPicture = !source.IsDirectory && ImageNavigator.IsSupported(source.Path);
            if (needTaken)
            {
                var value = isPicture ? Probe(() => TakenProbe(source.Path)) : null;
                lock (e.Gate) { e.Taken = value; e.HasTaken = true; }
            }
            if (needModified)
            {
                var value = Probe(() => ModifiedProbe(source));
                lock (e.Gate) { e.Modified = value; e.HasModified = true; }
            }
            if (needSize)
            {
                var value = isPicture ? Probe(() => SizeProbe(source.Path)) : (0, 0);
                lock (e.Gate) { e.Width = value.Item1; e.Height = value.Item2; e.HasSize = true; }
            }
        }
    }

    private T? Probe<T>(Func<T?> read)
    {
        Interlocked.Increment(ref _probes);
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { return default; }
    }

    /// <summary>What is known about the file now (anything not read yet is unknown).</summary>
    public RenameFileFacts? Get(BatchRenameSource source)
    {
        if (!_entries.TryGetValue(source.Path, out var e)) return null;
        lock (e.Gate) return new RenameFileFacts(e.Taken, e.Modified, e.Width, e.Height);
    }
}
