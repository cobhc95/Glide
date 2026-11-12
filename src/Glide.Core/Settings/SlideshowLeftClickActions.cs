namespace Glide.Core.Settings;

/// <summary>What a left-click on the picture does while a slideshow is active. Persisted by display name.</summary>
public static class SlideshowLeftClickActions
{
    public const string PauseResume = "Pause / resume slideshow";
    public const string NextImage = "Next image";
    public const string PreviousImage = "Previous image";
    public const string StopSlideshow = "Stop slideshow";
    public const string DoNothing = "Do nothing";

    public static readonly string[] All = [PauseResume, NextImage, PreviousImage, StopSlideshow, DoNothing];

    /// <summary>Returns a known action, falling back to the default for unknown or empty values.</summary>
    public static string Normalize(string? value)
        => All.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) ?? PauseResume;
}
