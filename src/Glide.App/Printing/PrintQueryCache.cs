using System.Collections.Concurrent;
using System.Diagnostics;

namespace Glide.App.Printing;

/// <summary>
/// The printer-driver queries the Print window needs. All of them can be slow (a driver call per query; seconds for a
/// network/WSD or Remote Desktop redirected printer) and are only ever called off the UI thread.
/// </summary>
public interface IPrintQueries
{
    IReadOnlyList<string> GetPrinterNames();
    string? GetDefaultPrinterName();
    PrintPrinterCapabilities GetCapabilities(string printerName);
    /// <summary>Portrait-orientation paper + printable geometry (the layout engine swaps for landscape itself).</summary>
    PrintPaperGeometry GetPaperGeometry(string printerName, string paperName, string sourceName);
}

/// <summary>The real driver queries (<see cref="PrintService"/>). Never used by tests that must not touch printers.</summary>
public sealed class WindowsPrintQueries : IPrintQueries
{
    public static WindowsPrintQueries Instance { get; } = new();
    public IReadOnlyList<string> GetPrinterNames() => PrintService.GetPrinterNames();
    public string? GetDefaultPrinterName() => PrintService.GetDefaultPrinterName();
    public PrintPrinterCapabilities GetCapabilities(string printerName) => PrintService.GetCapabilities(printerName);
    public PrintPaperGeometry GetPaperGeometry(string printerName, string paperName, string sourceName) =>
        PrintService.GetPaperGeometry(printerName, paperName, sourceName, landscape: false);
}

/// <summary>
/// Caches the slow driver answers per printer (capabilities) and per printer + paper + source (geometry), so the Print
/// window reads each of them once instead of on every option change, and a second Print in the same session opens
/// with an instant preview. Concurrent requests for the same key share one in-flight driver call; a failed call is
/// not cached. Entries expire after <see cref="TimeToLive"/> (a printer's defaults can change in Windows) and the
/// driver Properties sheet invalidates its printer. Only the preview reads from here: printing itself always re-reads
/// the live driver geometry (<see cref="PrintService.PrintAsync"/>, unchanged).
/// </summary>
public sealed class PrintQueryCache
{
    private readonly IPrintQueries _queries;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, Entry<PrintPrinterCapabilities>> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Entry<PrintPaperGeometry>> _geometry = new(StringComparer.OrdinalIgnoreCase);
    private int _capabilityQueries;
    private int _geometryQueries;
    private int _printerListQueries;

    private sealed record Entry<T>(Task<T> Task, long CreatedTicks);

    public PrintQueryCache(IPrintQueries queries, TimeSpan? timeToLive = null, Func<long>? clock = null)
    {
        _queries = queries;
        TimeToLive = timeToLive ?? TimeSpan.FromMinutes(10);
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Process-wide cache over the real drivers, shared by every Print window.</summary>
    public static PrintQueryCache Shared { get; } = new(WindowsPrintQueries.Instance);

    public TimeSpan TimeToLive { get; }

    /// <summary>Driver calls actually made (tests and the perf trace).</summary>
    public int CapabilityQueries => Volatile.Read(ref _capabilityQueries);
    public int GeometryQueries => Volatile.Read(ref _geometryQueries);
    public int PrinterListQueries => Volatile.Read(ref _printerListQueries);

    /// <summary>Installed printers and the default printer. Cheap (no driver DEVMODE work for the list); not cached so a newly added printer shows up.</summary>
    public Task<(IReadOnlyList<string> Names, string? Default)> GetPrintersAsync() =>
        Task.Run(() =>
        {
            Interlocked.Increment(ref _printerListQueries);
            return (_queries.GetPrinterNames(), _queries.GetDefaultPrinterName());
        });

    public bool TryGetCachedCapabilities(string printer, out PrintPrinterCapabilities capabilities) =>
        TryGetCompleted(_capabilities, printer, out capabilities);

    public bool TryGetCachedGeometry(string printer, string paper, string source, out PrintPaperGeometry geometry) =>
        TryGetCompleted(_geometry, GeometryKey(printer, paper, source), out geometry);

    public Task<PrintPrinterCapabilities> GetCapabilitiesAsync(string printer) =>
        GetOrAdd(_capabilities, printer, () =>
        {
            Interlocked.Increment(ref _capabilityQueries);
            return _queries.GetCapabilities(printer);
        });

    public Task<PrintPaperGeometry> GetGeometryAsync(string printer, string paper, string source) =>
        GetOrAdd(_geometry, GeometryKey(printer, paper, source), () =>
        {
            Interlocked.Increment(ref _geometryQueries);
            return _queries.GetPaperGeometry(printer, paper, source);
        });

    /// <summary>Forgets everything cached for <paramref name="printer"/> (after its driver Properties sheet was used).</summary>
    public void Invalidate(string printer)
    {
        _capabilities.TryRemove(printer, out _);
        var prefix = printer + "\u001F";
        foreach (var key in _geometry.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            _geometry.TryRemove(key, out _);
    }

    public void Clear()
    {
        _capabilities.Clear();
        _geometry.Clear();
    }

    private static string GeometryKey(string printer, string paper, string source) =>
        printer + "\u001F" + (paper ?? "") + "\u001F" + (source ?? "");

    private bool IsFresh<T>(Entry<T> entry) =>
        Stopwatch.GetElapsedTime(entry.CreatedTicks, _clock()) < TimeToLive;

    private bool TryGetCompleted<T>(ConcurrentDictionary<string, Entry<T>> map, string key, out T value)
    {
        if (map.TryGetValue(key, out var entry) && entry.Task.IsCompletedSuccessfully && IsFresh(entry))
        {
            value = entry.Task.Result;
            return true;
        }
        value = default!;
        return false;
    }

    private Task<T> GetOrAdd<T>(ConcurrentDictionary<string, Entry<T>> map, string key, Func<T> query)
    {
        while (true)
        {
            if (map.TryGetValue(key, out var existing))
            {
                if (!existing.Task.IsFaulted && !existing.Task.IsCanceled && (IsFresh(existing) || !existing.Task.IsCompleted))
                    return existing.Task;
                map.TryRemove(new KeyValuePair<string, Entry<T>>(key, existing));
                continue;
            }
            // Created cold and started only once it owns the key, so a lost race never runs a second driver call.
            var task = new Task<T>(query);
            var entry = new Entry<T>(task, _clock());
            if (!map.TryAdd(key, entry)) continue;
            task.Start(TaskScheduler.Default);
            // A failed driver call must be retried next time, not remembered.
            _ = task.ContinueWith(t => map.TryRemove(new KeyValuePair<string, Entry<T>>(key, entry)),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
}
