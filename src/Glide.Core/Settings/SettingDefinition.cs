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
