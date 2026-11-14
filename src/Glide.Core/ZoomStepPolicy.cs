namespace Glide.Core;

/// <summary>
/// Maps the "Viewport zoom step" setting to the zoom factors used by keyboard, menu, button and wheel zoom.
/// The default step (15%) reproduces the historical hard-coded values exactly: 15% per keyboard / menu / button
/// step and 12% per wheel notch. The wheel step keeps that 12:15 ratio when the setting changes.
/// Minimum and maximum zoom are clamped by the viewport itself and are unaffected by the step.
/// </summary>
public static class ZoomStepPolicy
{
    public const int MinimumStepPercent = 1;
    public const int MaximumStepPercent = 100;
    /// <summary>Default for the setting; also the step the pre-5.0.0 build hard-coded for keys, menus and buttons.</summary>
    public const int DefaultStepPercent = 15;
    /// <summary>Wheel step at the default setting (the historical 12%).</summary>
    public const int DefaultWheelPercent = 12;

    public static int Clamp(int stepPercent) => Math.Clamp(stepPercent, MinimumStepPercent, MaximumStepPercent);

    /// <summary>Scale factor of one zoom-in key / menu / button step (zoom out is the reciprocal).</summary>
    public static double KeyFactor(int stepPercent) => (100 + Clamp(stepPercent)) / 100.0;

    /// <summary>Scale factor of one wheel notch: the setting scaled by 12/15 (exactly 1.12 at the default).</summary>
    public static double WheelFactor(int stepPercent) =>
        (100.0 * DefaultStepPercent + (double)Clamp(stepPercent) * DefaultWheelPercent) / (100.0 * DefaultStepPercent);
}
