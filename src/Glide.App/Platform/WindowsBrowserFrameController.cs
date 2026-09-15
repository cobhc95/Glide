using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Glide.Diagnostics.Runtime;

namespace Glide.App.Platform;

/// <summary>
/// Windows-only browser-style frame integration.
///
/// Chromium and Firefox do not place ordinary application Buttons beside a stock OS caption.
/// They draw the top-row controls themselves, then teach Win32 which rectangles semantically are
/// HTMINBUTTON / HTMAXBUTTON / HTCLOSE and which edge pixels are resize borders. That preserves
/// Windows shell behaviour (notably the Windows 11 Snap Layout hover contract) while allowing tabs
/// and arbitrary application UI to occupy the same visual row.
///
/// Glide follows that architecture here. Avalonia owns pixels; Win32 owns non-client semantics.
/// </summary>
internal sealed class WindowsBrowserFrameController : IDisposable
{
    private const int GwlpWndProc = -4;
    private const int GwlStyle = -16;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const long WsSysMenu = 0x00080000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;

    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcMouseMove = 0x00A0;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcLButtonUp = 0x00A2;
    private const uint WmNcLButtonDblClk = 0x00A3;
    private const uint WmNcMouseLeave = 0x02A2;
    private const uint WmSysCommand = 0x0112;
    private const uint WmSetCursor = 0x0020;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmCtlColorStatic = 0x0138;
    private const uint WmSize = 0x0005;
    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;
    private const int VkRButton = 0x02;
    private const int IdcArrow = 32512;

    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int HtMinButton = 8;
    private const int HtMaxButton = 9;
    private const int HtClose = 20;

    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private const int ScClose = 0xF060;
    private const int ScRestore = 0xF120;

    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint WmSetRedraw = 0x000B;

    private readonly Window _window;
    private readonly Button _minimize;
    private readonly Button _maximize;
    private readonly Button _close;
    private readonly IntPtr _hwnd;
    private readonly WndProc _wndProc;
    private readonly IntPtr _previousWndProc;
    private readonly Func<PixelPoint, bool>? _selectionHitTest;
    private readonly Func<PixelPoint, bool>? _imageHitTest;
    private readonly Action? _minimizeAction;
    private readonly Action? _maximizeAction;
    private readonly Action? _closeAction;
    private readonly Action? _nativeResizeStarted;
    private readonly Action? _nativeResizeEnded;
    private readonly Control? _protectedClientChrome;
    private readonly IntPtr _zoomInCursor;
    private readonly IntPtr _zoomOutCursor;
    private int _hotHit = HtClient;
    private int _pressedHit = HtClient;
    private bool _disposed;
    private bool _selectionZoomOutPressed;
    private IntPtr _privacyCurtain;
    private IntPtr _privacyBrush;
    private uint _privacyColor;

    private WindowsBrowserFrameController(Window window, Button minimize, Button maximize, Button close, IntPtr hwnd, Func<PixelPoint, bool>? selectionHitTest, Func<PixelPoint, bool>? imageHitTest, Action? minimizeAction, Action? maximizeAction, Action? closeAction, Action? nativeResizeStarted, Action? nativeResizeEnded, Control? protectedClientChrome)
    {
        _window = window;
        _minimize = minimize;
        _maximize = maximize;
        _close = close;
        _hwnd = hwnd;
        _selectionHitTest = selectionHitTest;
        _imageHitTest = imageHitTest;
        _minimizeAction = minimizeAction;
        _maximizeAction = maximizeAction;
        _closeAction = closeAction;
        _nativeResizeStarted = nativeResizeStarted;
        _nativeResizeEnded = nativeResizeEnded;
        _protectedClientChrome = protectedClientChrome;
        _zoomInCursor = GlideCreateZoomCursor(0);
        _zoomOutCursor = GlideCreateZoomCursor(1);
        _wndProc = WindowProc;

        // Keep the standard capabilities advertised on the HWND even though the caption pixels are
        // client-drawn. Windows 11 uses these capabilities together with HTMAXBUTTON for Snap Layouts.
        var style = GetWindowLongPtrCompat(_hwnd, GwlStyle).ToInt64();
        style |= WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;
        SetWindowLongPtrCompat(_hwnd, GwlStyle, new IntPtr(style));

        var proc = Marshal.GetFunctionPointerForDelegate(_wndProc);
        _previousWndProc = SetWindowLongPtrCompat(_hwnd, GwlpWndProc, proc);
    }

    public static WindowsBrowserFrameController? TryAttach(Window window, Button minimize, Button maximize, Button close, Func<PixelPoint, bool>? selectionHitTest = null, Func<PixelPoint, bool>? imageHitTest = null, Action? minimizeAction = null, Action? maximizeAction = null, Action? closeAction = null, Action? nativeResizeStarted = null, Action? nativeResizeEnded = null, Control? protectedClientChrome = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var platform = window.TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero ||
            !string.Equals(platform.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
            return null;

        try { return new WindowsBrowserFrameController(window, minimize, maximize, close, platform.Handle, selectionHitTest, imageHitTest, minimizeAction, maximizeAction, closeAction, nativeResizeStarted, nativeResizeEnded, protectedClientChrome); }
        catch { return null; }
    }

    public void SetSelectionZoomOutPressed(bool pressed)
    {
        if (_disposed) return;
        _selectionZoomOutPressed = pressed;
        RefreshZoomCursorAtPointer();
    }

    /// <summary>
    /// Reassert the native frame capabilities after a hidden warm window is shown again.
    /// Windows can stop offering Snap Layouts for a custom-framed HWND after a Hide/Show
    /// transition unless the style and non-client frame are refreshed.
    /// </summary>
    public void RefreshForWarmRestore()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLongPtrCompat(_hwnd, GwlStyle).ToInt64();
            style |= WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;
            SetWindowLongPtrCompat(_hwnd, GwlStyle, new IntPtr(style));
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch { }
    }

    /// <summary>Cover the client area of a warm HWND before it is shown.</summary>
    public void ShowPrivacyCurtain(uint rgb)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        try
        {
            HidePrivacyCurtain();
            _privacyColor = rgb;
            _privacyBrush = CreateSolidBrush(new IntPtr((int)rgb));
            _privacyCurtain = CreateWindowExW(WsExNoActivate, "STATIC", "", WsChild | WsVisible,
                0, 0, 0, 0, _hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            ResizePrivacyCurtain(show: true);
        }
        catch { HidePrivacyCurtain(); }
    }

    public void HidePrivacyCurtain()
    {
        if (_privacyCurtain != IntPtr.Zero)
        {
            try { DestroyWindow(_privacyCurtain); } catch { }
            _privacyCurtain = IntPtr.Zero;
        }
        if (_privacyBrush != IntPtr.Zero)
        {
            try { DeleteObject(_privacyBrush); } catch { }
            _privacyBrush = IntPtr.Zero;
        }
    }

    private void ResizePrivacyCurtain(bool show)
    {
        if (_privacyCurtain == IntPtr.Zero || !GetClientRect(_hwnd, out var client)) return;
        SetWindowPos(_privacyCurtain, IntPtr.Zero, 0, 0, client.Right - client.Left, client.Bottom - client.Top,
            SwpNoMove | SwpNoActivate | (show ? SwpShowWindow : SwpHideWindow));
    }

    private void RefreshZoomCursorAtPointer()
    {
        if (!GetCursorPos(out var cursorPoint)) return;
        var screenPoint = new PixelPoint(cursorPoint.X, cursorPoint.Y);
        var rightDown = (GetKeyState(VkRButton) & 0x8000) != 0;

        if (_selectionZoomOutPressed && rightDown && _imageHitTest?.Invoke(screenPoint) == true && _zoomOutCursor != IntPtr.Zero)
        {
            SetCursor(_zoomOutCursor);
            return;
        }

        if (_selectionHitTest?.Invoke(screenPoint) == true && _zoomInCursor != IntPtr.Zero)
        {
            SetCursor(_zoomInCursor);
            return;
        }

        var arrow = LoadCursor(IntPtr.Zero, new IntPtr(IdcArrow));
        if (arrow != IntPtr.Zero) SetCursor(arrow);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // This delegate is a reverse P/Invoke boundary. A managed exception escaping it can tear down
        // the entire process before Avalonia's normal exception handling sees anything. Keep this as
        // a no-throw boundary and persist the offending Win32 message first.
        try
        {
        switch (message)
        {
            case WmSize:
                ResizePrivacyCurtain(show: true);
                break;
            case WmCtlColorStatic when lParam == _privacyCurtain && _privacyBrush != IntPtr.Zero:
                SetBkColor(wParam, _privacyColor);
                return _privacyBrush;
            case WmSetCursor:
            {
                // Legacy 1.2.125 path: let Win32 own the +/- magnifier. Cursor movement is then
                // composed by Windows and never causes an Avalonia property/layout/render update.
                if ((unchecked((long)lParam) & 0xFFFF) == HtClient && GetCursorPos(out var cursorPoint))
                {
                    var screenPoint = new PixelPoint(cursorPoint.X, cursorPoint.Y);
                    var rightDown = (GetKeyState(VkRButton) & 0x8000) != 0;
                    if (!rightDown) _selectionZoomOutPressed = false;

                    // A right-click zoom consumes the rectangle immediately on PointerPressed. Keep
                    // the native minus magnifier latched independently until the button comes up so
                    // visual feedback exactly matches legacy without changing gesture/capture logic.
                    if (_selectionZoomOutPressed && rightDown && _imageHitTest?.Invoke(screenPoint) == true)
                    {
                        if (_zoomOutCursor != IntPtr.Zero)
                        {
                            SetCursor(_zoomOutCursor);
                            return new IntPtr(1);
                        }
                    }
                    else if (_selectionHitTest?.Invoke(screenPoint) == true && _zoomInCursor != IntPtr.Zero)
                    {
                        SetCursor(_zoomInCursor);
                        return new IntPtr(1);
                    }
                }
                break;
            }
            case WmRButtonUp:
                SetSelectionZoomOutPressed(false);
                break;

            case WmEnterSizeMove:
                // Any client gesture must yield completely to the native sizing loop.
                _pressedHit = HtClient;
                ClearVisualState();
                // Invoke synchronously: WM_ENTERSIZEMOVE starts a modal Win32 loop and a dispatcher
                // post may not run until that loop has already ended. MainWindow needs the guard set
                // before the first WM_SIZING/mouse movement so routed image/overlay gestures cannot
                // race the native resize operation.
                try { _nativeResizeStarted?.Invoke(); } catch { }
                break;

            case WmExitSizeMove:
                // WM_EXITSIZEMOVE is the authoritative end of the Win32 modal resize loop.
                // Avalonia can otherwise retain stale secondary-button/capture state into the next
                // PointerPressed, which is perceived as a left click becoming a right click.
                _pressedHit = HtClient;
                ClearVisualState();
                Dispatcher.UIThread.Post(() => _nativeResizeEnded?.Invoke(), DispatcherPriority.Input);
                break;
            case WmNcHitTest:
            {
                var x = GetSignedLowWord(lParam);
                var y = GetSignedHighWord(lParam);
                var resize = HitTestResizeBorder(x, y);
                if (resize != HtClient) return new IntPtr(resize);

                // In fullscreen the absolute top-right physical pixels are owned natively rather than
                // relying only on Avalonia layout. This makes a muscle-memory throw to the monitor
                // corner deterministic across DPI, auto-hide chrome timing and compositor/layout races.
                // The tiny Avalonia fallback target remains as a second line of defence.
                if (_window.WindowState == WindowState.FullScreen)
                {
                    // Prefer the actual cursor position over WM_NCHITTEST's packed LPARAM. This is
                    // robust on mixed/negative-coordinate multi-monitor desktops as well as high DPI.
                    var closeX = x;
                    var closeY = y;
                    if (GetCursorPos(out var fullscreenCursor))
                    {
                        closeX = fullscreenCursor.X;
                        closeY = fullscreenCursor.Y;
                    }
                    if (IsFullscreenCornerCloseHit(closeX, closeY)) return new IntPtr(HtClose);
                    return new IntPtr(HtClient);
                }

                // The configurable title-action strip (Home, navigation, settings, etc.) is always
                // ordinary client UI. Protect it explicitly before caption hit-testing. This prevents
                // a stale/custom-frame non-client classification from turning a Home click into HTCLOSE.
                if (_protectedClientChrome is not null && ContainsScreenPoint(_protectedClientChrome, x, y))
                    return new IntPtr(HtClient);

                if (ContainsScreenPoint(_close, x, y)) return new IntPtr(HtClose);
                if (ContainsScreenPoint(_maximize, x, y)) return new IntPtr(HtMaxButton);
                if (ContainsScreenPoint(_minimize, x, y)) return new IntPtr(HtMinButton);
                break;
            }

            case WmNcMouseMove:
            {
                var hit = wParam.ToInt32();
                if (IsCaptionButtonHit(hit))
                {
                    SetHot(hit);
                    RequestNonClientMouseLeave();
                    return IntPtr.Zero;
                }
                ClearVisualState();
                break;
            }

            case WmNcMouseLeave:
                ClearVisualState();
                return IntPtr.Zero;

            case WmNcLButtonDown:
            {
                var hit = wParam.ToInt32();
                if (IsCaptionButtonHit(hit))
                {
                    LiveDiagnosticTrace.Write("win32", "nc_button_down", new { hit, message = "WM_NCLBUTTONDOWN" });
                    _pressedHit = hit;
                    SetHot(hit);
                    SetPressed(hit, true);
                    return IntPtr.Zero;
                }
                break;
            }

            case WmNcLButtonDblClk:
                if (IsCaptionButtonHit(wParam.ToInt32())) return IntPtr.Zero;
                break;

            case WmNcLButtonUp:
            {
                var hit = wParam.ToInt32();
                var pressed = _pressedHit;
                _pressedHit = HtClient;
                SetPressed(pressed, false);
                if (IsCaptionButtonHit(pressed))
                {
                    LiveDiagnosticTrace.Write("win32", "nc_button_up", new { pressed, hit, dispatch = pressed == hit });
                    SetHot(hit);
                    if (pressed == hit) DispatchSystemButton(pressed);
                    return IntPtr.Zero;
                }
                break;
            }
        }

        return CallWindowProcW(_previousWndProc, hwnd, message, wParam, lParam);
    
        }
        catch (Exception ex)
        {
            LiveDiagnosticTrace.WriteException("win32", "window_proc_exception", ex, new
            {
                hwnd = hwnd.ToInt64(),
                message = $"0x{message:X4}",
                wParam = wParam.ToInt64(),
                lParam = lParam.ToInt64(),
                windowState = _window.WindowState.ToString()
            });
            try { return CallWindowProcW(_previousWndProc, hwnd, message, wParam, lParam); }
            catch { return IntPtr.Zero; }
        }
    }
    private bool IsFullscreenCornerCloseHit(int screenX, int screenY)
    {
        if (!GetWindowRect(_hwnd, out var rect)) return false;
        var scale = Math.Max(1.0, _window.RenderScaling);
        var width = Math.Max(46, (int)Math.Ceiling(46 * scale));
        var height = Math.Max(44, (int)Math.Ceiling(44 * scale));
        return screenX >= rect.Right - width && screenX < rect.Right &&
               screenY >= rect.Top && screenY < rect.Top + height;
    }

    private int HitTestResizeBorder(int screenX, int screenY)
    {
        if (_window.WindowState is WindowState.Maximized or WindowState.FullScreen) return HtClient;
        if (!GetWindowRect(_hwnd, out var rect)) return HtClient;

        // Firefox/Chromium deliberately keep the outermost edge available to resize even when
        // tab/title content paints to the top. One physical pixel above caption buttons wins.
        const int topPriorityPixels = 1;
        var scale = Math.Max(1.0, _window.RenderScaling);
        var edge = Math.Max(5, (int)Math.Round(6 * scale));
        var corner = Math.Max(12, (int)Math.Round(16 * scale));

        var onLeft = screenX >= rect.Left && screenX < rect.Left + edge;
        var onRight = screenX < rect.Right && screenX >= rect.Right - edge;
        var onTop = screenY >= rect.Top && screenY < rect.Top + edge;
        var onBottom = screenY < rect.Bottom && screenY >= rect.Bottom - edge;

        if (screenY >= rect.Top && screenY < rect.Top + topPriorityPixels)
        {
            if (screenX < rect.Left + corner) return HtTopLeft;
            if (screenX >= rect.Right - corner) return HtTopRight;
            return HtTop;
        }
        if (onTop && onLeft) return HtTopLeft;
        if (onTop && onRight) return HtTopRight;
        if (onBottom && onLeft) return HtBottomLeft;
        if (onBottom && onRight) return HtBottomRight;
        if (onLeft) return HtLeft;
        if (onRight) return HtRight;
        if (onTop) return HtTop;
        if (onBottom) return HtBottom;
        return HtClient;
    }

    private bool ContainsScreenPoint(Control control, int screenX, int screenY)
    {
        if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var origin = control.PointToScreen(new Point(0, 0));
        var scale = Math.Max(0.1, _window.RenderScaling);
        var width = (int)Math.Ceiling(control.Bounds.Width * scale);
        var height = (int)Math.Ceiling(control.Bounds.Height * scale);
        return screenX >= origin.X && screenX < origin.X + width &&
               screenY >= origin.Y && screenY < origin.Y + height;
    }

    private void DispatchSystemButton(int hit)
    {
        // Keep native HT* semantics for Windows 11 hover/Snap behaviour, but let Glide decide the
        // click action. This is what makes caption buttons user-configurable without sacrificing
        // the browser-style non-client integration.
        var action = hit switch
        {
            HtMinButton => _minimizeAction,
            HtMaxButton => _maximizeAction,
            HtClose => _closeAction,
            _ => null
        };
        if (action is not null)
        {
            LiveDiagnosticTrace.Write("win32", "caption_action_dispatch", new { hit });
            Dispatcher.UIThread.Post(action);
            return;
        }

        var command = hit switch
        {
            HtMinButton => ScMinimize,
            HtMaxButton => _window.WindowState == WindowState.Maximized ? ScRestore : ScMaximize,
            HtClose => ScClose,
            _ => 0
        };
        if (command != 0) PostMessageW(_hwnd, WmSysCommand, new IntPtr(command), IntPtr.Zero);
    }

    private void SetHot(int hit)
    {
        if (_hotHit == hit) return;
        RemoveClass(_minimize, "nativeHot");
        RemoveClass(_maximize, "nativeHot");
        RemoveClass(_close, "nativeHot");
        _hotHit = hit;
        var button = ButtonForHit(hit);
        if (button is not null && !button.Classes.Contains("nativeHot")) button.Classes.Add("nativeHot");
    }

    private void SetPressed(int hit, bool pressed)
    {
        var button = ButtonForHit(hit);
        if (button is null) return;
        if (pressed)
        {
            if (!button.Classes.Contains("nativePressed")) button.Classes.Add("nativePressed");
        }
        else RemoveClass(button, "nativePressed");
    }

    private void ClearVisualState()
    {
        _hotHit = HtClient;
        _pressedHit = HtClient;
        RemoveClass(_minimize, "nativeHot");
        RemoveClass(_maximize, "nativeHot");
        RemoveClass(_close, "nativeHot");
        RemoveClass(_minimize, "nativePressed");
        RemoveClass(_maximize, "nativePressed");
        RemoveClass(_close, "nativePressed");
    }

    private Button? ButtonForHit(int hit) => hit switch
    {
        HtMinButton => _minimize,
        HtMaxButton => _maximize,
        HtClose => _close,
        _ => null
    };

    private static bool IsCaptionButtonHit(int hit) => hit is HtMinButton or HtMaxButton or HtClose;

    private void RequestNonClientMouseLeave()
    {
        var track = new NativeTrackMouseEvent
        {
            cbSize = (uint)Marshal.SizeOf<NativeTrackMouseEvent>(),
            dwFlags = TmeLeave | TmeNonClient,
            hwndTrack = _hwnd,
            dwHoverTime = 0
        };
        TrackMouseEventNative(ref track);
    }

    private static void RemoveClass(Control control, string name)
    {
        while (control.Classes.Remove(name)) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HidePrivacyCurtain();
        ClearVisualState();
        if (_hwnd != IntPtr.Zero && _previousWndProc != IntPtr.Zero)
        {
            try { SetWindowLongPtrCompat(_hwnd, GwlpWndProc, _previousWndProc); } catch { }
        }
        if (_zoomInCursor != IntPtr.Zero) DestroyCursor(_zoomInCursor);
        if (_zoomOutCursor != IntPtr.Zero) DestroyCursor(_zoomOutCursor);
        GC.KeepAlive(_wndProc);
    }

    private static int GetSignedLowWord(IntPtr value) => unchecked((short)((long)value & 0xFFFF));
    private static int GetSignedHighWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xFFFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTrackMouseEvent
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr SetWindowLongPtrCompat(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

    private static IntPtr GetWindowLongPtrCompat(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateSolidBrush(IntPtr colorRef);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("gdi32.dll")]
    private static extern uint SetBkColor(IntPtr hdc, uint colorRef);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    private static extern bool TrackMouseEventNative(ref NativeTrackMouseEvent trackEvent);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("Glide.Native", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GlideCreateZoomCursor(int zoomOut);
}
