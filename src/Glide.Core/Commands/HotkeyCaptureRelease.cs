namespace Glide.Core.Commands;

/// <summary>
/// 5.1.4: a captured shortcut is applied only when its key is released. Applying it on key-down opened the
/// "Shortcut already assigned" dialog while the key was still down, so its key-up (Space) or auto-repeat (Enter)
/// pressed the dialog's focused Cancel button. Waiting for the release keeps the captured key away from the dialog.
/// </summary>
public sealed class HotkeyCaptureRelease<TKey> where TKey : struct
{
    private TKey? _key;
    private string? _shortcut;

    public bool IsPending => _key is not null;

    /// <summary>Records the captured key; a later press before release replaces it.</summary>
    public void Press(TKey key, string shortcut) { _key = key; _shortcut = shortcut; }

    /// <summary>Returns the shortcut to apply when <paramref name="key"/> is the captured key, otherwise null.</summary>
    public string? Release(TKey key)
    {
        if (_key is not { } pending || !EqualityComparer<TKey>.Default.Equals(pending, key)) return null;
        var shortcut = _shortcut;
        Clear();
        return shortcut;
    }

    /// <summary>Returns and clears any pending shortcut (window lost focus before the key-up arrived).</summary>
    public string? Flush()
    {
        var shortcut = _key is null ? null : _shortcut;
        Clear();
        return shortcut;
    }

    public void Clear() { _key = null; _shortcut = null; }
}
