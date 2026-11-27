using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Glide.Imaging;
using Glide.PerfBench;

// Format speed audit helpers (5.2.0). Complements Glide.PerfBench (thumb / thumb-one / decode / decode-one).
//   route      <file> [--width 236]                          which thumbnail decoder the Explorer tab would use, and whether it lists the extension
//   vsmall     --data <corpus> --out <dir> --label L [--runs 7] [--filter text]     viewer first-frame decode bounded to a small box ("as-if thumbnail" for formats the Explorer tab does not list), warm
//   vsmall-one <file> [--json out]                           the same, first decode in a fresh process
//   shell      <inbox|path\to\Glide.ShellThumbnail.dll> --data <corpus> --out <dir> --label L [--runs 7] [--width 236] [--filter text]   IThumbnailProvider warm
//   shell-one  <inbox|dll> <file> [--width 236] [--json out]   IThumbnailProvider, first request in a fresh process
//   provider / provider-one   the codec plug-in's own preview call at a tile size (DecodePreviewSurface, 236 px by default): what the Explorer pipeline asks of HEIC / AVIF / JPEG XL / JPEG 2000 (5.2.0), warm / fresh process
//   queue      --data <corpus> --out <dir> [--screen 40] [--runs 7] [--lanes N] [--width 152]   first-screen fill time, old FIFO gate vs the 5.2.0 scheduler (needs a 5.2.0 build)
//   view / view-one   viewer first frame, settled, full decode through the app's backend (codec plugins included), warm / fresh process
//   factory    <file> [--width 236] [--runs 7]                 IShellItemImageFactory on fresh copies (refuses a type Glide's provider handles)
// "inbox" = Windows' own Photo Thumbnail Provider (CLSID C7657C4A-...), created in-process (no registry change, no dllhost).
ThreadPool.SetMinThreads(Math.Max(8, Environment.ProcessorCount), 8);
if (args.Length == 0) { Console.Error.WriteLine("usage: Glide.FormatAudit <route|vsmall|vsmall-one|shell|shell-one|factory> ..."); return 2; }
var mode = args[0].ToLowerInvariant();
if (mode is "route" or "vsmall" or "vsmall-one" or "view" or "view-one" or "provider" or "provider-one" or "queue")
{
    AppBuilder.Configure<AuditApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
    SynchronizationContext.SetSynchronizationContext(null);
}
static int Registry()
{
    var all = Glide.Core.CodecCapabilityRegistry.All;
    Console.WriteLine($"recognised {all.Count}, decode-enabled {all.Count(c => c.DecodeEnabled)}");
    foreach (var g in all.GroupBy(c => c.Tier + " | " + c.Provider).OrderBy(g => g.Key))
        Console.WriteLine($"{g.Key} | {g.Count()} | {string.Join(' ', g.Select(c => c.Extension))}");
    return 0;
}
try
{
    var code = mode switch
    {
        "route" => ThumbRoute.Run(args[1], Args.GetInt(args, "--width", 236)),
        "registry" => Registry(),
        "vsmall" => await ViewerSmall.RunAsync(args).ConfigureAwait(false),
        "vsmall-one" => await ViewerSmall.RunOneAsync(args[1], Args.Get(args, "--json")).ConfigureAwait(false),
        "queue" => await QueueBench.RunAsync(args).ConfigureAwait(false),
        "view" => await ViewerOpen.RunAsync(args).ConfigureAwait(false),
        "view-one" => await ViewerOpen.RunOneAsync(args[1], Args.Get(args, "--json"), args.Contains("--preload")).ConfigureAwait(false),
        "provider" => await ProviderPreview.RunAsync(args).ConfigureAwait(false),
        "provider-one" => await ProviderPreview.RunOneAsync(args[1], Args.GetInt(args, "--width", 236), Args.Get(args, "--json")).ConfigureAwait(false),
        "shell" => ShellBench.Run(args),
        "shell-one" => ShellBench.RunOne(args[1], args[2], Args.GetInt(args, "--width", 236), Args.Get(args, "--json")),
        "factory" => ShellBench.Factory(args[1], Args.GetInt(args, "--width", 236), Args.GetInt(args, "--runs", 7)),
        _ => 2
    };
    Console.Out.Flush();
    Exit.Now(code);
    return code;
}
catch (Exception ex)
{
    Console.Error.WriteLine("AUDIT FAILED: " + ex);
    Exit.Now(1);
    return 1;
}

public sealed class AuditApp : Application { }


internal static class Exit
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    public static void Now(int code) { Console.Out.Flush(); Console.Error.Flush(); TerminateProcess(GetCurrentProcess(), (uint)code); }
}

internal static class FileSet
{
    public static string[] List(string data, string? filter) =>
        Directory.GetFiles(Path.Combine(data, "single"))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Where(f => filter is null || Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Which decoder <c>ThumbnailDecode.DecodeUncached</c> reaches for a file, mirrored through the internal NativeImageDecoder members.</summary>
internal static class ThumbRoute
{
    // MainWindow.CanUseBrowserThumbnailDecoder (MainWindow.Browser.cs): the only extensions the Explorer tab asks a thumbnail for.
    private static readonly HashSet<string> Listed = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jxr", ".wdp", ".hdp" };

    private static readonly Type Native = typeof(ImageLoadCoordinator).Assembly.GetType("Glide.Imaging.NativeImageDecoder", true)!;

    public static bool IsListed(string path) => Listed.Contains(Path.GetExtension(path));

    private static bool Flag(string name, string path) =>
        (bool)Native.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(string) })!.Invoke(null, new object[] { path })!;

    private static (bool Ok, string Size) Try(string name, string path, int width)
    {
        var m = Native.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var a = new object?[] { path, width, null };
        var ok = (bool)m.Invoke(null, a)!;
        var bmp = a[2] as Bitmap;
        var size = bmp is null ? "" : $"{bmp.PixelSize.Width}x{bmp.PixelSize.Height}";
        bmp?.Dispose();
        return (ok, size);
    }

    public static int Run(string path, int width)
    {
        string route; string size = "";
        if (Flag("IsJpegPath", path))
        {
            var (ok, s) = Try("TryDecodeJpegThumbnail", path, width);
            route = ok ? "libjpeg-turbo (native, DCT-scaled / MPF / DC-only)" : "Skia (native JPEG declined)"; size = s;
        }
        else if (Flag("SupportsNativeThumbnail", path))
        {
            var (ok, s) = Try("TryDecodeThumbnail", path, width);
            route = ok ? "native bridge (" + (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "libwebp" : "WIC") + ")" : "Skia (native bridge declined)"; size = s;
        }
        else route = "Skia";
        var cap = Glide.Core.CodecCapabilityRegistry.Describe(path);
        var node = new JsonObject { ["file"] = Path.GetFileName(path), ["listed"] = IsListed(path), ["route"] = route, ["nativeSize"] = size, ["ext"] = cap.Extension, ["tier"] = cap.Tier.ToString(), ["provider"] = cap.Provider, ["decodeEnabled"] = cap.DecodeEnabled };
        Console.WriteLine(node.ToJsonString());
        return 0;
    }
}

/// <summary>
/// Viewer first frame bounded to a small box: what a tile thumbnail would cost for a format the Explorer tab does not
/// list, if it reused the viewer's first-view decoder (any route: WIC, codec plugin, managed, SVG, embedded preview).
/// The viewer's policy has a 480 px floor (<c>ImagePerformancePolicy.Normalize</c>), so the frames are about 480 px.
/// </summary>
internal static class ViewerSmall
{
    private const int Box = 480;

    private static Glide.Imaging.ImagePerformancePolicy Policy()
    {
        var p = BenchPolicies.AppDefault(prefetch: false);
        return (p with
        {
            DecoderScaledFirstFrame = true, PreviewLongestSide = Box, PreviewMaxWidth = Box, PreviewMaxHeight = Box,
            BackgroundRefinement = false, PredictivePrefetch = false, PrefetchDepth = 0, FullNeighbourPredecodeCount = 0
        }).Normalize();
    }

    public sealed record Sample(double Ms, string Size, string Route, string? Error);

    public static async Task<Sample> MeasureAsync(string path)
    {
        var policy = Policy();
        try
        {
            using var loader = new ImageLoadCoordinator(new CodecProviderDecoderBackend(lazyResolver: p => CodecProviderRuntime.Shared.ResolveProvider(p))) { Policy = policy };
            loader.SetViewportDemand(path, new ImageViewportDemand(Box, Box));
            var t = Stopwatch.GetTimestamp();
            var result = await loader.LoadForegroundAsync(path, policy).ConfigureAwait(false);
            var ms = Timing.Ms(t);
            if (result is null) return new Sample(double.NaN, "", "", "null result");
            var size = $"{result.Bitmap.PixelSize.Width}x{result.Bitmap.PixelSize.Height}";
            var route = result.DecodeRoute;
            Ownership.Release(loader, result.Bitmap);
            return new Sample(ms, size, route, null);
        }
        catch (Exception ex) { return new Sample(double.NaN, "", "", ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
    }

    public static async Task<int> RunOneAsync(string path, string? jsonOut)
    {
        var s = await MeasureAsync(path).ConfigureAwait(false);
        var node = new JsonObject { ["file"] = Path.GetFileName(path), ["coldMs"] = double.IsFinite(s.Ms) ? s.Ms : null, ["size"] = s.Size, ["route"] = s.Route, ["error"] = s.Error };
        if (jsonOut is not null) File.WriteAllText(jsonOut, node.ToJsonString());
        Console.WriteLine(node.ToJsonString());
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? ".";
        var label = Args.Get(args, "--label") ?? "run";
        var runs = Args.GetInt(args, "--runs", 7);
        var items = new JsonArray();
        foreach (var file in FileSet.List(data, Args.Get(args, "--filter")))
        {
            await MeasureAsync(file).ConfigureAwait(false); // untimed warm-up
            var samples = new List<Sample>();
            for (var i = 0; i < runs; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); samples.Add(await MeasureAsync(file).ConfigureAwait(false)); }
            var ok = samples.Where(s => s.Error is null).ToList();
            var times = ok.Select(s => s.Ms).ToArray();
            var med = Stats.Median(times);
            items.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(file), ["warmMs"] = double.IsFinite(med) ? med : null,
                ["minMs"] = times.Length > 0 ? times.Min() : null, ["maxMs"] = times.Length > 0 ? times.Max() : null,
                ["size"] = (ok.LastOrDefault() ?? samples.Last()).Size, ["route"] = (ok.LastOrDefault() ?? samples.Last()).Route,
                ["error"] = samples.FirstOrDefault(s => s.Error is not null)?.Error,
                ["samples"] = new JsonArray(times.Select(t => (JsonNode)t).ToArray())
            });
            Console.Error.WriteLine($"vsmall {Path.GetFileName(file)}: {Stats.Ms(med)} ms {(ok.LastOrDefault() ?? samples.Last()).Size} {(ok.LastOrDefault() ?? samples.Last()).Route} {samples.FirstOrDefault(s => s.Error is not null)?.Error}");
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"vsmall-{label}.json"), new JsonObject { ["items"] = items }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

/// <summary>
/// The bundled codec provider's preview call at a tile size, exactly as the Explorer thumbnail pipeline reaches it for HEIC / AVIF / JPEG XL / JPEG 2000
/// (ICodecSurfaceProvider.DecodePreviewSurfaceAsync): provider resolve + load included in the cold run, nothing else.
/// </summary>
internal static class ProviderPreview
{
    public sealed record Sample(double Ms, string Size, string? Error);

    public static async Task<Sample> MeasureAsync(string path, int width)
    {
        try
        {
            var t = Stopwatch.GetTimestamp();
            var provider = CodecProviderRuntime.Shared.ResolveProvider(path) as ICodecSurfaceProvider;
            if (provider is null) return new Sample(double.NaN, "", "no provider");
            using var surface = await provider.DecodePreviewSurfaceAsync(path, width).ConfigureAwait(false);
            var ms = Timing.Ms(t);
            return surface is null ? new Sample(double.NaN, "", "null surface") : new Sample(ms, $"{surface.Width}x{surface.Height}", null);
        }
        catch (Exception ex) { return new Sample(double.NaN, "", ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
    }

    public static async Task<int> RunOneAsync(string path, int width, string? jsonOut)
    {
        var s = await MeasureAsync(path, width).ConfigureAwait(false);
        var node = new JsonObject { ["file"] = Path.GetFileName(path), ["coldMs"] = double.IsFinite(s.Ms) ? s.Ms : null, ["size"] = s.Size, ["error"] = s.Error };
        if (jsonOut is not null) File.WriteAllText(jsonOut, node.ToJsonString());
        Console.WriteLine(node.ToJsonString());
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? ".";
        var label = Args.Get(args, "--label") ?? "run";
        var runs = Args.GetInt(args, "--runs", 7);
        var width = Args.GetInt(args, "--width", 236);
        var items = new JsonArray();
        foreach (var file in FileSet.List(data, Args.Get(args, "--filter")))
        {
            var first = await MeasureAsync(file, width).ConfigureAwait(false); // untimed warm-up (also resolves and loads the provider)
            if (first.Error == "no provider") continue;
            var samples = new List<Sample>();
            for (var i = 0; i < runs; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); samples.Add(await MeasureAsync(file, width).ConfigureAwait(false)); }
            var ok = samples.Where(s => s.Error is null).ToList();
            var times = ok.Select(s => s.Ms).ToArray();
            var med = Stats.Median(times);
            items.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(file), ["warmMs"] = double.IsFinite(med) ? med : null,
                ["minMs"] = times.Length > 0 ? times.Min() : null, ["maxMs"] = times.Length > 0 ? times.Max() : null,
                ["size"] = (ok.LastOrDefault() ?? samples.Last()).Size, ["error"] = samples.FirstOrDefault(s => s.Error is not null)?.Error,
                ["samples"] = new JsonArray(times.Select(t => (JsonNode)t).ToArray())
            });
            Console.Error.WriteLine($"provider {Path.GetFileName(file)}: {Stats.Ms(med)} ms {(ok.LastOrDefault() ?? samples.Last()).Size}");
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"provider-{label}.json"), new JsonObject { ["items"] = items }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

internal static class ShellBench
{
    private static readonly Guid ClsidInbox = new("C7657C4A-9F68-40fa-A4DF-96BC08EB3551");
    private static readonly Guid ClsidGlide = new("6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    [ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailProvider { [PreserveSig] int GetThumbnail(uint cx, out IntPtr hbmp, out int alpha); }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { [PreserveSig] int Initialize(IStream stream, uint mode); }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool l);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectFn(ref Guid clsid, ref Guid riid, out IntPtr ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(string path, uint mode, uint attrs, [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IStream stream);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out Bitmapinfo bm);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmapinfo { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

    private static Guid GuidThumb = new("e357fccd-a995-4576-b01f-234630154e96");

    private static object CreateProvider(string provider)
    {
        if (provider.Equals("inbox", StringComparison.OrdinalIgnoreCase))
            return Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidInbox, true)!)!;
        var lib = NativeLibrary.Load(Path.GetFullPath(provider));
        var fn = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectFn>(NativeLibrary.GetExport(lib, "DllGetClassObject"));
        var clsid = ClsidGlide; var iid = new Guid("00000001-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(fn(ref clsid, ref iid, out var factoryPtr));
        var factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryPtr);
        var unk = IidUnknown;
        Marshal.ThrowExceptionForHR(factory.CreateInstance(IntPtr.Zero, ref unk, out var obj));
        return Marshal.GetObjectForIUnknown(obj);
    }

    public sealed record Sample(double CreateMs, double InitMs, double GetMs, string Size, int Hr);

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!; Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error is not null) throw new InvalidOperationException(error.Message, error);
        return result;
    }

    public static int RunOne(string provider, string path, int width, string? jsonOut)
    {
        var s = OnSta(() => Once(provider, path, width));
        var node = new JsonObject { ["file"] = Path.GetFileName(path), ["provider"] = provider, ["createMs"] = s.CreateMs, ["initMs"] = s.InitMs, ["getMs"] = s.GetMs, ["size"] = s.Size, ["hr"] = s.Hr };
        if (jsonOut is not null) File.WriteAllText(jsonOut, node.ToJsonString());
        Console.WriteLine(node.ToJsonString());
        return 0;
    }

    private static Sample Once(string provider, string path, int width)
    {
        var t0 = Stopwatch.GetTimestamp();
        var instance = CreateProvider(provider);
        var create = Timing.Ms(t0);
        var t1 = Stopwatch.GetTimestamp();
        SHCreateStreamOnFileEx(path, 0x40, 0, false, IntPtr.Zero, out var stream);
        var init = ((IInitializeWithStream)instance).Initialize(stream, 0);
        var initMs = Timing.Ms(t1);
        var t2 = Stopwatch.GetTimestamp();
        IntPtr hbmp = IntPtr.Zero;
        var hr = init != 0 ? init : ((IThumbnailProvider)instance).GetThumbnail((uint)width, out hbmp, out _);
        var get = Timing.Ms(t2);
        var size = "";
        if (hr == 0 && hbmp != IntPtr.Zero)
        {
            if (GetObject(hbmp, Marshal.SizeOf<Bitmapinfo>(), out var bm) != 0) size = $"{bm.Width}x{bm.Height}";
            DeleteObject(hbmp);
        }
        Marshal.ReleaseComObject(instance);
        Marshal.ReleaseComObject(stream);
        return new Sample(create, initMs, get, size, hr);
    }

    public static int Run(string[] args)
    {
        var provider = args[1];
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? ".";
        var label = Args.Get(args, "--label") ?? "run";
        var runs = Args.GetInt(args, "--runs", 7);
        var width = Args.GetInt(args, "--width", 236);
        var items = new JsonArray();
        foreach (var file in FileSet.List(data, Args.Get(args, "--filter")))
        {
            var (samples, first) = OnSta(() =>
            {
                var list = new List<Sample>();
                var warm = Once(provider, file, width); // untimed
                if (warm.Hr != 0) return (list, warm);
                for (var i = 0; i < runs; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); list.Add(Once(provider, file, width)); }
                return (list, warm);
            });
            var times = samples.Where(s => s.Hr == 0).Select(s => s.GetMs).ToArray();
            var med = Stats.Median(times);
            items.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(file), ["provider"] = provider, ["warmMs"] = double.IsFinite(med) ? med : null,
                ["minMs"] = times.Length > 0 ? times.Min() : null, ["maxMs"] = times.Length > 0 ? times.Max() : null,
                ["size"] = samples.LastOrDefault()?.Size ?? first.Size, ["hr"] = first.Hr,
                ["samples"] = new JsonArray(times.Select(t => (JsonNode)t).ToArray())
            });
            Console.Error.WriteLine($"shell {Path.GetFileName(file)}: {Stats.Ms(med)} ms {(samples.LastOrDefault()?.Size ?? first.Size)} hr=0x{first.Hr:X}");
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"shell-{(provider.Equals("inbox", StringComparison.OrdinalIgnoreCase) ? "inbox" : "glide")}-{label}.json"), new JsonObject { ["items"] = items }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // ---- IShellItemImageFactory (what Explorer itself calls) -------------------------------------------------------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(long size, int flags, out IntPtr hbitmap); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    private static string? HandlerServer(string ext)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext + @"\ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}");
            var clsid = k?.GetValue("") as string;
            if (clsid is null) return null;
            using var s = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32");
            return Environment.ExpandEnvironmentVariables(s?.GetValue("") as string ?? "");
        }
        catch { return null; }
    }

    /// <summary>Fresh copy per request (the shell thumbnail cache is keyed by the file, so a new name always misses). Refuses a type whose handler is Glide's installed DLL.</summary>
    public static int Factory(string path, int width, int runs)
    {
        var ext = Path.GetExtension(path);
        var server = HandlerServer(ext);
        if (server is not null && server.Contains("Glide", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(new JsonObject { ["file"] = Path.GetFileName(path), ["skipped"] = "handler is Glide's installed provider: " + server }.ToJsonString());
            return 0;
        }
        var dir = Path.Combine(Path.GetTempPath(), "glide-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var times = new List<double>(); var size = ""; var hrs = new List<int>();
        try
        {
            OnSta(() =>
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                for (var i = 0; i <= runs; i++)
                {
                    var copy = Path.Combine(dir, $"c{i}" + ext);
                    File.Copy(path, copy);
                    SHCreateItemFromParsingName(copy, IntPtr.Zero, ref iid, out var item);
                    var t = Stopwatch.GetTimestamp();
                    var hr = ((IShellItemImageFactory)item).GetImage(((long)width) | ((long)width << 32), 0x8 /* THUMBNAILONLY */, out var h);
                    var ms = Timing.Ms(t);
                    if (hr == 0 && h != IntPtr.Zero) { if (GetObject(h, Marshal.SizeOf<Bitmapinfo>(), out var bm) != 0) size = $"{bm.Width}x{bm.Height}"; DeleteObject(h); }
                    hrs.Add(hr);
                    times.Add(hr == 0 ? ms : double.NaN); // index 0 = the first request (provider / host start-up), the rest warm
                    Marshal.ReleaseComObject(item);
                }
                return 0;
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        var first = times.Count > 0 ? times[0] : double.NaN;
        var warm = times.Skip(1).Where(double.IsFinite).ToArray();
        Console.WriteLine(new JsonObject
        {
            ["file"] = Path.GetFileName(path), ["handler"] = server, ["firstMs"] = double.IsFinite(first) ? first : null, ["warmMs"] = warm.Length > 0 ? Stats.Median(warm) : null, ["size"] = size,
            ["hr"] = hrs.FirstOrDefault(h => h != 0)
        }.ToJsonString());
        return 0;
    }
}
