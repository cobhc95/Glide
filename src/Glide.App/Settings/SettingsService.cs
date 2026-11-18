using Glide.App.Services;
using Glide.App.Updates;

namespace Glide.App.Settings;

/// <summary>Raised after the live settings changed; <see cref="Delta"/> is computed on first use (no cost without readers).</summary>
internal sealed class SettingsChangedEventArgs : EventArgs
{
    private readonly Lazy<SettingsDelta> _delta;

    public SettingsChangedEventArgs(GlideSettingsState? previous, GlideSettingsState current, SettingsDelta? delta)
    {
        Current = current;
        _delta = delta is not null ? new Lazy<SettingsDelta>(delta) : new Lazy<SettingsDelta>(() => previous is null ? SettingsDelta.Compute(new GlideSettingsState(), current) : SettingsDelta.Compute(previous, current));
    }

    /// <summary>A snapshot of the new live settings (do not mutate).</summary>
    public GlideSettingsState Current { get; }

    /// <summary>The changed setting ids (and whether anything without an accessor changed).</summary>
    public SettingsDelta Delta => _delta.Value;
}

/// <summary>
/// Stage C (C2): the one owner of the process-wide live settings. The main windows and the Settings window no longer write
/// glide.settings.json themselves:
/// - <see cref="Save"/> composes the live sub-stores (dialog placements from <see cref="WindowPlacementService"/>, the update
///   checker's memory) into the state, persists it and publishes it;
/// - <see cref="Publish"/> makes a state the live one without saving (a Settings preview);
/// - <see cref="SavePresetSnapshot"/> persists a Settings-window snapshot without making it live (the "save User custom
///   preset" button, which must not commit unrelated staged edits);
/// - <see cref="Current"/> is a snapshot of the live settings (new windows, Reset/Backup, diagnostics);
/// - <see cref="Changed"/> carries the changed setting ids so a subsystem can react only to what it owns
///   (MainWindow routes its own preview/apply by <see cref="Glide.Core.Settings.SettingEffectRegistry"/> owner).
/// Load and migration stay in <see cref="SettingsStore"/>; this class never reads the disk.
/// </summary>
internal static class SettingsService
{
    private static readonly object Gate = new();
    private static GlideSettingsState? _current;

    /// <summary>After every publish (save or preview). Raised on the publishing thread, outside the lock.</summary>
    public static event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>A copy of the live settings, or null before the first publish.</summary>
    public static GlideSettingsState? Current
    {
        get { lock (Gate) return _current?.CloneState(); }
    }

    /// <summary>Makes <paramref name="settings"/> the live state (a copy is kept). <paramref name="delta"/> may be precomputed.</summary>
    public static void Publish(GlideSettingsState settings, SettingsDelta? delta = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GlideSettingsState? previous;
        var snapshot = settings.CloneState();
        lock (Gate)
        {
            previous = _current;
            _current = snapshot;
        }
        Changed?.Invoke(null, new SettingsChangedEventArgs(previous, snapshot, delta));
    }

    /// <summary>
    /// Persists <paramref name="settings"/> and publishes it. The live dialog placements and the update checker's memory are
    /// copied into <paramref name="settings"/> first (their stores are authoritative), exactly as the main window always did.
    /// </summary>
    public static void Save(GlideSettingsState settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WindowPlacementService.ApplyTo(settings);
        UpdateService.Current?.ApplyTo(settings);
        SettingsStore.Save(settings);
        Publish(settings);
    }

    /// <summary>Persists a Settings-window snapshot (with the live dialog placements) without publishing it.</summary>
    public static void SavePresetSnapshot(GlideSettingsState settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WindowPlacementService.ApplyTo(settings);
        SettingsStore.Save(settings);
    }

    /// <summary>Tests only: forget the live state.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _current = null;
    }
}
