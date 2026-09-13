using Glide.Diagnostics.Runtime;

namespace Glide.App.Services;

/// <summary>
/// Application diagnostics boundary. The event queue is intentionally lazy so an ordinary cold file
/// launch does not allocate/timestamp diagnostic records before first useful pixels. Developer startup
/// diagnostics can opt in earlier; otherwise MainWindow enables the recorder after first presentation.
/// </summary>
public sealed class DiagnosticsCoordinator
{
    private DiagnosticEventLog? _events;

    public bool Enabled => Volatile.Read(ref _events) is not null;

    public void Enable()
    {
        if (Volatile.Read(ref _events) is not null) return;
        Interlocked.CompareExchange(ref _events, new DiagnosticEventLog(), null);
    }

    public void Write(string subsystem, string name, object? data = null)
        => Volatile.Read(ref _events)?.Write(subsystem, name, data);

    public void WriteCritical(string subsystem, string name, object? data = null)
    {
        Enable();
        Volatile.Read(ref _events)!.Write(subsystem, name, data);
    }

    public void ExportJsonLines(string path)
    {
        Enable();
        Volatile.Read(ref _events)!.ExportJsonLines(path);
    }
}
