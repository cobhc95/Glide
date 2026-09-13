namespace Glide.Core;

public enum ViewMode
{
    FitImage,
    FitWidth,
    FitHeight,
    ActualSize,
    Manual
}

/// <summary>
/// Headless, inspectable viewer state. UI controls render this state but do not own it.
/// Keeping this small and explicit is a core AI-debugging invariant.
/// </summary>
public sealed class ViewerState
{
    public string? CurrentPath { get; internal set; }
    public int CurrentIndex { get; internal set; } = -1;
    public int ImageCount { get; internal set; }
    public ViewMode ViewMode { get; internal set; } = ViewMode.FitImage;
    public double Zoom { get; internal set; } = 1.0;
    public double PanX { get; internal set; }
    public double PanY { get; internal set; }
    public long DecodeGeneration { get; internal set; }
    public bool IsLoading { get; internal set; }
    public string? LastError { get; internal set; }

    public ViewerStateSnapshot Snapshot() => new(
        CurrentPath,
        CurrentIndex,
        ImageCount,
        ViewMode,
        Zoom,
        PanX,
        PanY,
        DecodeGeneration,
        IsLoading,
        LastError);
}

public sealed record ViewerStateSnapshot(
    string? CurrentPath,
    int CurrentIndex,
    int ImageCount,
    ViewMode ViewMode,
    double Zoom,
    double PanX,
    double PanY,
    long DecodeGeneration,
    bool IsLoading,
    string? LastError);
