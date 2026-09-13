using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Glide.App.Platform;

public enum ExplorerHostState
{
    NotCreated,
    Creating,
    Initialized,
    Navigating,
    Ready,
    Failed
}

/// <summary>
/// Real Windows Shell Explorer embedded inside an Avalonia NativeControlHost. The surrounding Glide
/// BrowserTabState stays platform-neutral; non-Windows platforms simply retain the lightweight fallback.
/// Native readiness is explicit: MainWindow must keep its managed fallback visible until IsReady is true.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WindowsExplorerHost : NativeControlHost, IExplorerBrowserEventsNative, IServiceProviderNative, ICommDlgBrowserNative
{
    private static readonly Guid ExplorerBrowserClsid = new("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");
    private static readonly Guid CommDlgBrowserIid = new("000214F1-0000-0000-C000-000000000046");
    private IExplorerBrowser? _browser;
    private string? _pendingFolder;
    private IPlatformHandle? _hostHandle;
    private uint _eventsCookie;
    private IntPtr _eventsInterface;
    private bool _viewCreated;
    private bool _navigationCompleted;
    private string _themeChoice = "Dark";

    public event Action<string>? FolderNavigated;
    public event Action<string>? FileActivated;
    public event Action<ExplorerHostState>? StateChanged;
    public event Action<string, object?>? Diagnostic;

    public Func<string, bool>? CanActivateFile { get; set; }
    public ExplorerHostState State { get; private set; } = ExplorerHostState.NotCreated;
    public bool IsReady => State == ExplorerHostState.Ready;
    public string? LastRequestedFolder => _pendingFolder;
    public string? LastError { get; private set; }
    public IntPtr HostHandle => _hostHandle?.Handle ?? IntPtr.Zero;

    public WindowsExplorerHost()
    {
        SizeChanged += (_, _) => ResizeBrowser();
    }

    public bool IsShellAvailable => _browser is not null;

    /// <summary>Apply a light/dark treatment to the embedded native Shell view without affecting Glide itself.</summary>
    public void ApplyTheme(string? themeChoice)
    {
        _themeChoice = themeChoice is "Light" or "Neutral" or "Dark" ? themeChoice : "Dark";
        if (!OperatingSystem.IsWindows() || HostHandle == IntPtr.Zero) return;
        ApplyThemeToNativeTree();
    }

    private void SetState(ExplorerHostState state, string? error = null)
    {
        State = state;
        LastError = error;
        Diagnostic?.Invoke("state_changed", new { state = state.ToString(), error, folder = _pendingFolder, hwnd = HostHandle.ToInt64() });
        StateChanged?.Invoke(state);
    }

    private void Emit(string name, object? data = null) => Diagnostic?.Invoke(name, data);

    public void Navigate(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        _pendingFolder = Path.GetFullPath(folder);
        _navigationCompleted = false;
        if (_browser is not null) NavigateCore(_pendingFolder);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _hostHandle = handle;
        if (!OperatingSystem.IsWindows()) return handle;
        SetState(ExplorerHostState.Creating);
        try
        {
            var type = Type.GetTypeFromCLSID(ExplorerBrowserClsid, throwOnError: true)!;
            _browser = (IExplorerBrowser)Activator.CreateInstance(type)!;
            Emit("com_activated", new { clsid = ExplorerBrowserClsid, hwnd = handle.Handle.ToInt64() });
            ThrowIfFailed(IUnknown_SetSite(_browser, this));
            Emit("site_set");
            var rect = CurrentRect();
            var folderSettings = new FolderSettings { ViewMode = 4, Flags = 0 };
            ThrowIfFailed(_browser.Initialize(handle.Handle, ref rect, ref folderSettings));
            Emit("initialized", new { rect.Left, rect.Top, rect.Right, rect.Bottom });
            Emit("com_callback_visibility", new
            {
                explorerEvents = Marshal.IsTypeVisibleFromCom(typeof(IExplorerBrowserEventsNative)),
                serviceProvider = Marshal.IsTypeVisibleFromCom(typeof(IServiceProviderNative)),
                commDlgBrowser = Marshal.IsTypeVisibleFromCom(typeof(ICommDlgBrowserNative))
            });
            _eventsInterface = Marshal.GetComInterfaceForObject(this, typeof(IExplorerBrowserEventsNative));
            ThrowIfFailed(_browser.Advise(_eventsInterface, out _eventsCookie));
            Emit("advised", new { cookie = _eventsCookie });
            ThrowIfFailed(_browser.SetOptions(ExplorerBrowserOptions.ShowFrames | ExplorerBrowserOptions.AlwaysNavigate | ExplorerBrowserOptions.NoBorder));
            Emit("options_set");
            SetState(ExplorerHostState.Initialized);
            if (!string.IsNullOrWhiteSpace(_pendingFolder)) NavigateCore(_pendingFolder);
        }
        catch (Exception ex)
        {
            Emit("create_failed", new { error = ex.GetType().Name, ex.Message });
            ReleaseBrowser();
            SetState(ExplorerHostState.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Emit("host_destroying", new { hwnd = control.Handle.ToInt64() });
        ReleaseBrowser();
        _hostHandle = null;
        _viewCreated = false;
        _navigationCompleted = false;
        SetState(ExplorerHostState.NotCreated);
        base.DestroyNativeControlCore(control);
    }

    private void ResizeBrowser()
    {
        if (_browser is null || _hostHandle is null) return;
        try
        {
            var rect = CurrentRect();
            var hr = _browser.SetRect(IntPtr.Zero, ref rect);
            Emit("set_rect", new { hr, rect.Left, rect.Top, rect.Right, rect.Bottom, hwnd = HostHandle.ToInt64() });
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        catch (Exception ex)
        {
            Emit("set_rect_failed", new { error = ex.GetType().Name, ex.Message });
        }
    }

    private NativeRect CurrentRect()
    {
        // IExplorerBrowser::SetRect consumes physical HWND pixels, while Avalonia Bounds are DIPs.
        // Passing DIPs directly made the native Explorer occupy only 1 / DPI-scale of the window
        // (for example ~2/3 at 150% scaling) and prevented correct live resize.
        var scale = Math.Max(1.0, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        return new NativeRect { Left = 0, Top = 0, Right = width, Bottom = height };
    }

    private void ApplyThemeToNativeTree()
    {
        if (HostHandle == IntPtr.Zero) return;
        var dark = string.Equals(_themeChoice, "Dark", StringComparison.OrdinalIgnoreCase);
        var neutral = string.Equals(_themeChoice, "Neutral", StringComparison.OrdinalIgnoreCase);
        // Windows Shell has native light/dark visual classes but no third arbitrary palette.
        // Neutral deliberately means the unforced Windows Explorer palette; Dark and Light are forced.
        ApplyThemeToWindow(HostHandle, dark, neutral);
        try
        {
            EnumChildWindows(HostHandle, (hwnd, _) =>
            {
                ApplyThemeToWindow(hwnd, dark, neutral);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        Emit("theme_applied", new { theme = _themeChoice, hwnd = HostHandle.ToInt64() });
    }

    private static void ApplyThemeToWindow(IntPtr hwnd, bool dark, bool neutral)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (neutral) SetWindowTheme(hwnd, null, null);
            else SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null);
            var darkValue = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref darkValue, sizeof(int));
            _ = SendMessage(hwnd, 0x031A /* WM_THEMECHANGED */, IntPtr.Zero, IntPtr.Zero);
            _ = InvalidateRect(hwnd, IntPtr.Zero, true);
        }
        catch { }
    }

    private void NavigateCore(string folder)
    {
        if (_browser is null) return;
        IntPtr pidl = IntPtr.Zero;
        SetState(ExplorerHostState.Navigating);
        try
        {
            var parseHr = SHParseDisplayName(folder, IntPtr.Zero, out pidl, 0, out _);
            Emit("parse_display_name", new { folder, hr = parseHr });
            if (parseHr < 0 || pidl == IntPtr.Zero)
            {
                var message = $"SHParseDisplayName failed: 0x{parseHr:X8}";
                SetState(ExplorerHostState.Failed, message);
                return;
            }
            var browseHr = _browser.BrowseToIDList(pidl, 0);
            Emit("browse_to_id_list", new { folder, hr = browseHr });
            if (browseHr < 0) SetState(ExplorerHostState.Failed, $"BrowseToIDList failed: 0x{browseHr:X8}");
        }
        catch (Exception ex)
        {
            Emit("navigate_failed_exception", new { folder, error = ex.GetType().Name, ex.Message });
            SetState(ExplorerHostState.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private void ReleaseBrowser()
    {
        if (_browser is null) return;
        if (_eventsCookie != 0)
        {
            try { _browser.Unadvise(_eventsCookie); } catch { }
            _eventsCookie = 0;
        }
        if (_eventsInterface != IntPtr.Zero)
        {
            try { Marshal.Release(_eventsInterface); } catch { }
            _eventsInterface = IntPtr.Zero;
        }
        try { IUnknown_SetSite(_browser, null); } catch { }
        try { _browser.Destroy(); } catch { }
        try { Marshal.FinalReleaseComObject(_browser); } catch { }
        _browser = null;
        Emit("browser_released");
    }

    int IExplorerBrowserEventsNative.OnNavigationPending(IntPtr pidlFolder)
    {
        Emit("navigation_pending", new { folder = TryGetFileSystemPath(pidlFolder) });
        SetState(ExplorerHostState.Navigating);
        return 0;
    }

    int IExplorerBrowserEventsNative.OnViewCreated(IntPtr shellView)
    {
        _viewCreated = shellView != IntPtr.Zero;
        Emit("view_created", new { shellView = shellView.ToInt64() });
        ApplyThemeToNativeTree();
        if (_viewCreated && _navigationCompleted) SetState(ExplorerHostState.Ready);
        return 0;
    }

    int IExplorerBrowserEventsNative.OnNavigationComplete(IntPtr pidlFolder)
    {
        var path = TryGetFileSystemPath(pidlFolder);
        _navigationCompleted = true;
        Emit("navigation_complete", new { folder = path, viewCreated = _viewCreated });
        ApplyThemeToNativeTree();
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) FolderNavigated?.Invoke(path);
        if (_viewCreated) SetState(ExplorerHostState.Ready);
        return 0;
    }

    int IExplorerBrowserEventsNative.OnNavigationFailed(IntPtr pidlFolder)
    {
        var path = TryGetFileSystemPath(pidlFolder);
        Emit("navigation_failed", new { folder = path });
        SetState(ExplorerHostState.Failed, "IExplorerBrowserEvents.OnNavigationFailed");
        return 0;
    }

    int IServiceProviderNative.QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (guidService != CommDlgBrowserIid || riid != CommDlgBrowserIid)
            return HResultNoInterface;
        try
        {
            ppvObject = Marshal.GetComInterfaceForObject(this, typeof(ICommDlgBrowserNative));
            return 0;
        }
        catch
        {
            ppvObject = IntPtr.Zero;
            return HResultNoInterface;
        }
    }

    int ICommDlgBrowserNative.OnDefaultCommand(IntPtr shellView)
    {
        var path = TryGetSingleSelectedFile(shellView);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 1; // S_FALSE: let Shell handle it.
        bool supported;
        try { supported = CanActivateFile?.Invoke(path) == true; } catch { supported = false; }
        if (!supported) return 1;
        try
        {
            FileActivated?.Invoke(path);
            return 0; // S_OK: Glide handled the image; suppress the external default command.
        }
        catch
        {
            return 1;
        }
    }

    int ICommDlgBrowserNative.OnStateChange(IntPtr shellView, uint change) => 0;

    int ICommDlgBrowserNative.IncludeObject(IntPtr shellView, IntPtr pidl) => 0;

    private static string? TryGetSingleSelectedFile(IntPtr shellView)
    {
        if (shellView == IntPtr.Zero) return null;
        object? viewObject = null;
        object? dataObjectRaw = null;
        try
        {
            viewObject = Marshal.GetObjectForIUnknown(shellView);
            if (viewObject is not IShellViewNative view) return null;
            var iidDataObject = IDataObjectIid;
            dataObjectRaw = view.GetItemObject(SvgioSelection, ref iidDataObject);
            if (dataObjectRaw is not IDataObject dataObject) return null;

            var format = new FORMATETC
            {
                cfFormat = CfHDrop,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_HGLOBAL
            };
            dataObject.GetData(ref format, out var medium);
            try
            {
                if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero) return null;
                var count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
                if (count != 1) return null;
                var length = DragQueryFile(medium.unionmember, 0, null, 0);
                if (length == 0) return null;
                var buffer = new StringBuilder(checked((int)length + 1));
                return DragQueryFile(medium.unionmember, 0, buffer, (uint)buffer.Capacity) > 0
                    ? buffer.ToString()
                    : null;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dataObjectRaw is not null && Marshal.IsComObject(dataObjectRaw))
            {
                try { Marshal.ReleaseComObject(dataObjectRaw); } catch { }
            }
            if (viewObject is not null && Marshal.IsComObject(viewObject))
            {
                try { Marshal.ReleaseComObject(viewObject); } catch { }
            }
        }
    }

    private static string? TryGetFileSystemPath(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return null;
        IntPtr text = IntPtr.Zero;
        try
        {
            if (SHGetNameFromIDList(pidl, ShellDisplayName.FileSystemPath, out text) < 0 || text == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(text);
        }
        catch { return null; }
        finally
        {
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
        }
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private const int HResultNoInterface = unchecked((int)0x80004002);
    private const uint SvgioSelection = 0x1;
    private const short CfHDrop = 15;
    private static readonly Guid IDataObjectIid = new("0000010E-0000-0000-C000-000000000046");

    private enum ShellDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    [Flags]
    private enum ExplorerBrowserOptions : uint
    {
        ShowFrames = 0x2,
        AlwaysNavigate = 0x4,
        NoBorder = 0x40
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FolderSettings
    {
        public int ViewMode;
        public uint Flags;
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellViewNative
    {
        void VTableGap01();
        void VTableGap02();
        void VTableGap03();
        void VTableGap04();
        void VTableGap05();
        void VTableGap06();
        void VTableGap07();
        void VTableGap08();
        void VTableGap09();
        void VTableGap10();
        void VTableGap11();
        void VTableGap12();
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint aspectOfView, ref Guid riid);
    }

    [ComImport, Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerBrowser
    {
        [PreserveSig] int Initialize(IntPtr hwndParent, ref NativeRect rect, ref FolderSettings folderSettings);
        [PreserveSig] int Destroy();
        [PreserveSig] int SetRect(IntPtr deferredWindowPosition, ref NativeRect rect);
        [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string propertyBag);
        [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string emptyText);
        [PreserveSig] int SetFolderSettings(ref FolderSettings folderSettings);
        [PreserveSig] int Advise(IntPtr events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(ExplorerBrowserOptions options);
        [PreserveSig] int GetOptions(out ExplorerBrowserOptions options);
        [PreserveSig] int BrowseToIDList(IntPtr pidl, uint flags);
        [PreserveSig] int BrowseToObject(IntPtr unknown, uint flags);
        [PreserveSig] int FillFromObject(IntPtr unknown, uint flags);
        [PreserveSig] int RemoveAll();
        [PreserveSig] int GetCurrentView(ref Guid iid, out IntPtr view);
    }

    [DllImport("Shlwapi.dll", PreserveSig = true)]
    private static extern int IUnknown_SetSite(
        [MarshalAs(UnmanagedType.IUnknown)] object target,
        [MarshalAs(UnmanagedType.IUnknown)] object? site);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint fileIndex, [Out] StringBuilder? fileName, uint fileNameLength);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, ShellDisplayName sigdnName, out IntPtr ppszName);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        IntPtr bindContext,
        out IntPtr pidl,
        uint attributesIn,
        out uint attributesOut);
}


[ComVisible(true)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IServiceProviderNative
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComVisible(true)]
[Guid("000214F1-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICommDlgBrowserNative
{
    [PreserveSig] int OnDefaultCommand(IntPtr shellView);
    [PreserveSig] int OnStateChange(IntPtr shellView, uint change);
    [PreserveSig] int IncludeObject(IntPtr shellView, IntPtr pidl);
}

[ComVisible(true)]
[Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerBrowserEventsNative
{
    [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
    [PreserveSig] int OnViewCreated(IntPtr shellView);
    [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
    [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
}
