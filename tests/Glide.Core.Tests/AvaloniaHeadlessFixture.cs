using System.Reflection;
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
            // Root platform: decoder/export tests use Avalonia bitmaps directly on xUnit worker threads, so the
            // root AvaloniaLocator needs the Skia/headless services. Setting it up on this worker thread also
            // registers a ManagedDispatcherImpl bound to THIS (worker) thread in the root scope.
            AppBuilder.Configure<HeadlessTestApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            var root = LocatorApi.CurrentMutable();

            // Root cause of the intermittent "Call from invalid thread" (RenderLoop.Add during
            // AvaloniaHeadlessPlatform.Initialize): for every dispatch the session runs EnsureIsolatedApplication,
            // which enters a child locator scope, clears Dispatcher.s_uiThread, then re-initialises the platform.
            // If any other thread (a parallel test class, or a continuation left over from an earlier UI test)
            // reads Dispatcher.UIThread between that reset and Initialize binding the new IDispatcherImpl, the
            // dispatcher is recreated from the ROOT impl, which belonged to the worker thread above, and the
            // session thread's RenderLoop.Add then fails VerifyAccess. Fix: rebind the root impl to one owned by
            // the session thread, so a dispatcher recreated in that window is still the session's. The session
            // is started eagerly here (under the lock), and failures are no longer swallowed.
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaHeadlessFixture).Assembly);
            session.Dispatch(() =>
            {
                LocatorApi.RebindDispatcherImpl(from: LocatorApi.CurrentMutable(), to: root);
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
            _session = session;
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

    /// <summary>
    /// AvaloniaLocator and IDispatcherImpl are public at runtime but hidden from Avalonia 11's reference
    /// assemblies, so the one rebinding above goes through reflection. Any API drift throws loudly.
    /// </summary>
    private static class LocatorApi
    {
        private static readonly Type LocatorType = typeof(AppBuilder).Assembly.GetType("Avalonia.AvaloniaLocator")
            ?? typeof(Avalonia.Threading.Dispatcher).Assembly.GetType("Avalonia.AvaloniaLocator", throwOnError: true)!;
        private static readonly Type DispatcherImplType = typeof(Avalonia.Threading.Dispatcher).Assembly
            .GetType("Avalonia.Threading.IDispatcherImpl", throwOnError: true)!;

        public static object CurrentMutable() =>
            LocatorType.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null) ?? throw new InvalidOperationException("AvaloniaLocator.CurrentMutable is null.");

        public static void RebindDispatcherImpl(object from, object to)
        {
            var impl = LocatorType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance, [typeof(Type)])!
                .Invoke(from, [DispatcherImplType])
                ?? throw new InvalidOperationException("Headless session has no dispatcher implementation.");
            var helper = LocatorType.GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!
                .MakeGenericMethod(DispatcherImplType).Invoke(to, null)!;
            helper.GetType().GetMethod("ToConstant")!.MakeGenericMethod(DispatcherImplType).Invoke(helper, [impl]);
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
