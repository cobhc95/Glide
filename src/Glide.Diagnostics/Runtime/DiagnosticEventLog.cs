using System.Collections.Concurrent;
using System.Text.Json;

namespace Glide.Diagnostics.Runtime;

/// <summary>
/// Tiny in-process diagnostic event recorder. Keep payloads factual and structured so an AI can
/// reconstruct sequence/state without scraping prose logs.
/// </summary>
public sealed class DiagnosticEventLog
{
    private readonly ConcurrentQueue<DiagnosticEvent> _events = new();
    private readonly int _limit;

    public DiagnosticEventLog(int limit = 2000) => _limit = Math.Max(100, limit);

    public void Write(string subsystem, string name, object? data = null)
    {
        _events.Enqueue(new DiagnosticEvent(DateTimeOffset.UtcNow, subsystem, name, data));
        while (_events.Count > _limit) _events.TryDequeue(out _);
    }

    public DiagnosticEvent[] Snapshot() => _events.ToArray();

    public void ExportJsonLines(string path)
    {
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        foreach (var item in Snapshot()) writer.WriteLine(JsonSerializer.Serialize(item));
    }
}

public sealed record DiagnosticEvent(DateTimeOffset Time, string Subsystem, string Name, object? Data);
