namespace Glide.App.Settings;

/// <summary>
/// What changed between two settings states, as SettingsCatalog ids (Stage C, C3). Every id in
/// <see cref="SettingAccessors.Values"/> whose value differs is listed; any other difference (derived fields, hotkeys,
/// gestures, title-bar buttons, legacy values without an accessor...) sets <see cref="HasUnmappedChanges"/>, which means
/// "ownership unknown": consumers must fall back to re-applying everything.
/// </summary>
internal sealed class SettingsDelta
{
    public static SettingsDelta None { get; } = new(Array.Empty<string>(), false);

    private SettingsDelta(IReadOnlyList<string> changedIds, bool hasUnmappedChanges)
    {
        ChangedIds = changedIds;
        HasUnmappedChanges = hasUnmappedChanges;
    }

    /// <summary>Changed catalogue ids, in accessor-table order.</summary>
    public IReadOnlyList<string> ChangedIds { get; }

    /// <summary>True when something without a single-property accessor changed (route: apply everything).</summary>
    public bool HasUnmappedChanges { get; }

    public bool IsEmpty => ChangedIds.Count == 0 && !HasUnmappedChanges;

    /// <summary>True when only (a non-empty subset of) <paramref name="ids"/> changed and nothing unmapped did.</summary>
    public bool OnlyTouches(IReadOnlySet<string> ids) =>
        !HasUnmappedChanges && ChangedIds.Count > 0 && ChangedIds.All(ids.Contains);

    public static SettingsDelta Compute(GlideSettingsState before, GlideSettingsState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.ContentEquals(after)) return None;
        List<string>? changed = null;
        GlideSettingsState? probe = null;
        foreach (var (id, accessor) in SettingAccessors.Values)
        {
            var was = accessor.Get(before);
            if (Equals(was, accessor.Get(after))) continue;
            (changed ??= new List<string>()).Add(id);
            // Undo the mapped change on a copy; whatever still differs afterwards has no accessor.
            accessor.Set(probe ??= after.CloneState(), was);
        }
        if (changed is null) return new SettingsDelta(Array.Empty<string>(), true);
        return new SettingsDelta(changed, !before.ContentEquals(probe!));
    }

    public override string ToString() =>
        (HasUnmappedChanges ? "unmapped+" : "") + string.Join(",", ChangedIds);
}
