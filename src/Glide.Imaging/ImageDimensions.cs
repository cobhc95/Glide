namespace Glide.Imaging;

public readonly record struct ImageDimensions(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
    public int LongestSide => Math.Max(Width, Height);
}
