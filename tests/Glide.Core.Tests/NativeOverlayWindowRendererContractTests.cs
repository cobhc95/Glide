using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Structural contracts for the Windows-only overlay presentation boundary. Runtime HWND and
/// DIB behavior requires the Windows desktop host, so these checks prevent a future change from
/// silently restoring Avalonia Window-per-overlay ownership or per-frame bitmap work.
/// </summary>
public sealed class NativeOverlayWindowRendererContractTests
{
    [Fact]
    public void RendererUsesWorkerOwnedLayeredPopupsAndMessagePump()
    {
        var source = Source("src/Glide.App/Controls/NativeOverlayWindowRenderer.cs");

        Assert.Contains("CreateWindowExW", source, StringComparison.Ordinal);
        Assert.Contains("WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE", source, StringComparison.Ordinal);
        Assert.Contains("WS_POPUP", source, StringComparison.Ordinal);
        Assert.Contains("PeekMessage", source, StringComparison.Ordinal);
        Assert.Contains("DispatchMessage", source, StringComparison.Ordinal);
        Assert.Contains("DestroyWindow", source, StringComparison.Ordinal);
        Assert.Contains("UpdateLayeredWindow", source, StringComparison.Ordinal);
        Assert.Contains("CreateDIBSection", source, StringComparison.Ordinal);
        Assert.Contains("new Thread(Loop)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("class OverlayWindow : Window", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Show(owner)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SWP_ASYNCWINDOWPOS", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererCachesPixelsAndKeepsMotionPathBitmapFree()
    {
        var source = Source("src/Glide.App/Controls/NativeOverlayWindowRenderer.cs");

        Assert.Contains("TryCopyBitmap", source, StringComparison.Ordinal);
        Assert.Contains("SourcePixels", source, StringComparison.Ordinal);
        Assert.Contains("NativeSurface? Surface", source, StringComparison.Ordinal);
        Assert.Contains("if (item.Running) Advance(item, dt)", source, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(item.Hwnd", source, StringComparison.Ordinal);
        Assert.Contains("SWP_NOZORDER", source, StringComparison.Ordinal);
        Assert.Contains("SWP_NOSIZE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererHidesOwnedPopupsWhenOwnerIsUnavailable()
    {
        var source = Source("src/Glide.App/Controls/NativeOverlayWindowRenderer.cs");

        Assert.Contains("IsWindowVisible(_ownerHwnd)", source, StringComparison.Ordinal);
        Assert.Contains("IsIconic(_ownerHwnd)", source, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _suppressed)", source, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(item.Hwnd, SW_HIDE)", source, StringComparison.Ordinal);
    }


    [Fact]
    public void RendererFollowsOwnerWithoutGlobalTopmostAndKeepsHotLoopAllocationLight()
    {
        var source = Source("src/Glide.App/Controls/NativeOverlayWindowRenderer.cs");

        Assert.Contains("snapshot.HostScreenOrigin.X - ownerOrigin.X", source, StringComparison.Ordinal);
        Assert.Contains("origin.X + Volatile.Read(ref _hostClientOffsetX)", source, StringComparison.Ordinal);
        Assert.Contains("IsWindowEnabled(_ownerHwnd)", source, StringComparison.Ordinal);
        Assert.Contains("var insertAfter = _ownerHwnd", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HWND_TOPMOST", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HWND_NOTOPMOST", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_items.Values.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("items.ToDictionary", source, StringComparison.Ordinal);
        Assert.Contains("_relayMotion.Clear()", source, StringComparison.Ordinal);
        Assert.Contains("AvSetMmThreadCharacteristicsW", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererSteersAtBoundsInsteadOfReflectingVelocity()
    {
        var source = Source("src/Glide.App/Controls/NativeOverlayWindowRenderer.cs");

        Assert.Contains("AddBoundarySteer", source, StringComparison.Ordinal);
        Assert.Contains("RotateTowards", source, StringComparison.Ordinal);
        Assert.Contains("ClampWithoutBounce", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nextVelocity = -Math.Abs(velocity)", source, StringComparison.Ordinal);
    }
    private static string Source(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate repository source file '{relativePath}'.");
    }
}
