using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Glide.Core.Tests.AvaloniaHeadlessFixture.HeadlessTestApplication))]

namespace Glide.Core.Tests;

/// <summary>
/// Image decoder/export tests intentionally exercise real Avalonia bitmaps.  The test runner has
/// no desktop platform, so initialise Avalonia's official headless render backend once rather than
/// substituting those tests with inert fakes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaHeadlessCollection : ICollectionFixture<AvaloniaHeadlessFixture>
{
    public const string Name = "Avalonia headless";
}

public sealed class AvaloniaHeadlessFixture
{
    private static readonly object Gate = new();
    private static bool _initialised;
    private static HeadlessUnitTestSession? _session;

    public AvaloniaHeadlessFixture()
    {
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialised) return;
            AppBuilder.Configure<HeadlessTestApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            try
            {
                _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaHeadlessFixture).Assembly);
            }
            catch { }
            _initialised = true;
        }
    }

    public static void RunOnUIThread(Action action)
    {
        // Route through the awaitable overload and block. The previous fire-and-forget Dispatch let
        // callers (and xUnit) continue before the UI action completed, so assertions could silently
        // no-op and pass without exercising anything.
        RunOnUIThreadAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();
    }

    public static async Task RunOnUIThreadAsync(Func<Task> action)
    {
        EnsureInitialized();
        if (_session is not null)
        {
            // Dispatch(Func<Task>) binds to the generic Dispatch<TResult>(Func<TResult>) overload with
            // TResult = Task, which only waits until the body reaches its FIRST await: everything after
            // an `await Task.Delay(...)` used to run after the test had already finished, so later
            // assertions were never observed (registry audit, 5.0.x). Route through the
            // Func<Task<TResult>> overload so the whole async body runs inside the dispatch scope.
            await _session.Dispatch(async () => { await action().ConfigureAwait(true); return true; }, CancellationToken.None);
        }
        else
        {
            await action();
        }
    }

    public sealed class HeadlessTestApplication : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<HeadlessTestApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
