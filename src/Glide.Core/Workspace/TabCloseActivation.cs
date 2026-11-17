namespace Glide.Core.Workspace;

/// <summary>Which tab becomes active when the active tab is closed.</summary>
public enum TabCloseActivationMode
{
    /// <summary>The tab to the right (the last tab when the closed tab was the last one). The behaviour before 5.0.6.</summary>
    Right,
    /// <summary>The tab to the left (the first tab when the closed tab was the first one).</summary>
    Left,
    /// <summary>The most recently used remaining tab.</summary>
    PreviouslyUsed,
}

/// <summary>Values of the "Ctrl+Tab switching order" setting (tabs.cycleOrder).</summary>
public static class TabCycleOrders
{
    public const string TabOrder = "Tab order";
    public const string MostRecentlyUsed = "Most recently used";

    /// <summary>True when Ctrl+Tab cycles in most-recently-used order (Ctrl+Tab cycling and "Match Ctrl+Tab order" both ask).</summary>
    public static bool IsMostRecentlyUsed(string? cycleOrder) =>
        string.Equals(cycleOrder, MostRecentlyUsed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Values of the "After closing a tab, activate" setting (5.0.6).</summary>
public static class TabCloseActivation
{
    public const string MatchCycleOrder = "Match Ctrl+Tab order";
    public const string PreviouslyUsed = "Previously used tab";
    public const string Right = "Tab to the right";
    public const string Left = "Tab to the left";
    // 5.0.6 release: Previously used tab (dev builds of 5.0.6 shipped Match Ctrl+Tab order; SettingsStore migrates that stored value once).
    public const string Default = PreviouslyUsed;

    /// <summary>In the Settings combo order.</summary>
    public static IReadOnlyList<string> All { get; } = [MatchCycleOrder, PreviouslyUsed, Right, Left];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>
    /// The mode in effect. "Match Ctrl+Tab order" is Previously used tab when Ctrl+Tab cycles in most-recently-used
    /// order and Tab to the right when it cycles in tab order. Unknown values use the default, Previously used tab.
    /// </summary>
    public static TabCloseActivationMode Resolve(string? setting, string? cycleOrder) => Normalize(setting) switch
    {
        PreviouslyUsed => TabCloseActivationMode.PreviouslyUsed,
        Right => TabCloseActivationMode.Right,
        Left => TabCloseActivationMode.Left,
        MatchCycleOrder => TabCycleOrders.IsMostRecentlyUsed(cycleOrder)
            ? TabCloseActivationMode.PreviouslyUsed
            : TabCloseActivationMode.Right,
        _ => TabCloseActivationMode.PreviouslyUsed,
    };

    /// <summary>
    /// Index (in the list after removal) of the tab to activate after the active tab at <paramref name="closedIndex"/>
    /// was removed. <paramref name="recentlyUsed"/> is most recent first and may hold ids that are gone.
    /// </summary>
    public static int NextActiveIndex(IReadOnlyList<TabState> remaining, int closedIndex, TabCloseActivationMode mode, IReadOnlyList<Guid>? recentlyUsed)
    {
        if (remaining.Count == 0) return -1;
        if (mode == TabCloseActivationMode.PreviouslyUsed && recentlyUsed is not null)
        {
            foreach (var id in recentlyUsed)
            {
                for (var i = 0; i < remaining.Count; i++)
                    if (remaining[i].Id == id) return i;
            }
        }
        var right = Math.Clamp(closedIndex, 0, remaining.Count - 1);
        return mode == TabCloseActivationMode.Left ? Math.Clamp(closedIndex - 1, 0, remaining.Count - 1) : right;
    }
}
