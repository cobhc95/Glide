namespace Glide.Core.Settings;

/// <summary>
/// One declarative user-facing setting. Settings metadata lives here rather than in UI code.
/// Phase 2 established this as the permanent schema consumed by search, profiles and diagnostics.
/// </summary>
public sealed record SettingDefinition(
    string Id,
    string Category,
    string Label,
    string Description,
    SettingKind Kind,
    object DefaultValue,
    string[] SearchTerms,
    int? FuturePhase = null);

public enum SettingKind
{
    Toggle,
    Choice,
    Number,
    Text,
    Action
}

/// <summary>
/// Shared choice literals (theme / quality). Single source for the magic strings validated in
/// <c>Glide.App.Settings.SettingsStore</c> and the performance-profile UI.
/// </summary>
public static class SettingChoiceNames
{
    public const string Dark = "Dark";
    public const string Neutral = "Neutral";
    public const string Light = "Light";
    public const string MaximumSpeed = "Maximum speed";
    public const string Balanced = "Balanced";
    public const string MaximumQuality = "Maximum quality";
}
