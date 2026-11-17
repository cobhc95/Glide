namespace Glide.Core.Navigation;

/// <summary>
/// Spots a sticky or bouncing next / previous key: one physical press that makes Glide accept several keyboard
/// navigations (5.0.6). The main window feeds it keyboard next / previous key-downs (with whether the navigation was
/// accepted) and key-ups; when it fires, the "Skipping several pictures at once?" hint may show (navigation.skipHint). Pure and
/// clock-free (times are milliseconds from any monotonic clock) so it is unit-tested without a window.
/// <para>
/// It fires on either rule:
/// <list type="bullet">
/// <item><b>Bounce.</b> A key-down after a key-up of the same key (so not auto-repeat) whose navigation is accepted less
/// than <see cref="BounceGapMs"/> after the previous accepted navigation of that key is an "extra" navigation;
/// <see cref="ExtraNavigationsToFire"/> extras in one chain fire. A person tapping on purpose does not keep a
/// gap that short for three presses in a row.</item>
/// <item><b>Fast pseudo-repeat.</b> One press (no key-up in between) that got <see cref="PressNavigationsToFire"/> or more
/// accepted navigations and whose key-up arrives less than <see cref="FastPressMs"/> after the key-down. Windows'
/// auto-repeat starts no sooner than about 250 ms after the press, so a real held key never does this.</item>
/// </list>
/// Repeat key-downs (no key-up since the last key-down) never count as bounces, so holding a key is never mistaken for a
/// sticky one; the mouse wheel only breaks a chain (<see cref="OnWheelNavigation"/>). Glide's own held-key playback
/// (arrow keys held past the hold threshold) is not reported at all.
/// </para>
/// </summary>
public sealed class NavigationSkipDetector
{
    /// <summary>An accepted navigation from a fresh key-down closer than this to the previous one counts as an extra.</summary>
    public const double BounceGapMs = 150;
    /// <summary>Extra navigations in one bounce chain that fire the detector (the press itself plus two extras).</summary>
    public const int ExtraNavigationsToFire = 2;
    /// <summary>A press released sooner than this cannot contain Windows auto-repeat (the shortest repeat delay is ~250 ms).</summary>
    public const double FastPressMs = 250;
    /// <summary>Accepted navigations inside one fast press that fire the detector.</summary>
    public const int PressNavigationsToFire = 3;

    private int? _downKey;
    private double _pressStartMs;
    private int _pressNavigations;

    private int? _chainKey;
    private double _chainLastMs;
    private int _chainExtras;

    /// <summary>
    /// A next / previous key-down. <paramref name="key"/> identifies the physical key; <paramref name="navigated"/> is true
    /// when this key-down's navigation was accepted (not stopped by the navigation minimum interval). Returns true when a
    /// sticky / bouncing key has just been detected.
    /// </summary>
    public bool OnKeyDown(int key, double timeMs, bool navigated)
    {
        var repeat = _downKey == key;
        if (!repeat)
        {
            _downKey = key;
            _pressStartMs = timeMs;
            _pressNavigations = 0;
        }
        if (!navigated) return false;
        _pressNavigations++;
        if (repeat) return false;

        if (_chainKey == key && timeMs - _chainLastMs >= 0 && timeMs - _chainLastMs < BounceGapMs)
            _chainExtras++;
        else
            _chainExtras = 0;
        _chainKey = key;
        _chainLastMs = timeMs;
        if (_chainExtras < ExtraNavigationsToFire) return false;
        ResetChain();
        return true;
    }

    /// <summary>A key-up. Returns true when it ends a press that was too short for auto-repeat but navigated several times.</summary>
    public bool OnKeyUp(int key, double timeMs)
    {
        if (_downKey != key) return false;
        _downKey = null;
        var fired = _pressNavigations >= PressNavigationsToFire && timeMs - _pressStartMs < FastPressMs;
        _pressNavigations = 0;
        if (fired) ResetChain();
        return fired;
    }

    /// <summary>A mouse-wheel navigation: never a sign of a sticky key; it ends any bounce chain.</summary>
    public bool OnWheelNavigation(double timeMs)
    {
        _ = timeMs;
        ResetChain();
        return false;
    }

    /// <summary>Forgets every key and chain (the window lost focus, so a key-up may never arrive).</summary>
    public void Reset()
    {
        _downKey = null;
        _pressNavigations = 0;
        ResetChain();
    }

    private void ResetChain()
    {
        _chainKey = null;
        _chainExtras = 0;
        _chainLastMs = 0;
    }
}
