using Avalonia;
using Avalonia.Headless;
using Glide.Explorer.Tests;

[assembly: AvaloniaTestApplication(typeof(ExplorerTestApplication))]

namespace Glide.Explorer.Tests;

/// <summary>
/// The Explorer tab tests drive the real, themed MainWindow with simulated input, so they need a headless platform
/// that is set up ONCE, by the headless session itself (its UI thread, Dispatcher, render timer and compositor must all
/// belong together). They therefore live in their own test assembly instead of sharing Glide.Core.Tests'
/// AvaloniaHeadlessFixture, which sets Avalonia up on two threads for its simpler tests.
/// </summary>
public sealed class ExplorerTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ExplorerTestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
