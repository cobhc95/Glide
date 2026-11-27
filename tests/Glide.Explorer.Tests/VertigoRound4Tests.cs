using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.Core.Commands;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Vertigo220 round 4 (docs/feedback/vertigo-round4.md): mouse side buttons (A1-A3), duplicated tab history (F1),
/// Space with a focused Settings button and paused GIFs across tab switches (H1, H2), and an image that disappeared
/// from disk while its tab was in the background (J1, J2).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class VertigoRound4Tests
{
    private static NavigationHistory History(ExplorerHarness h) => h.Window.TabHistories.Find(h.Workspace.Active!.Id)!;

    private static bool RaiseSideButton(ExplorerHarness h, Visual target, bool back)
    {
        var props = new PointerPointProperties(back ? RawInputModifiers.XButton1MouseButton : RawInputModifiers.XButton2MouseButton,
            back ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed);
        var args = new PointerPressedEventArgs(target, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), h.Window,
            new Point(100, 100), 0, props, KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent };
        ((Interactive)target).RaiseEvent(args);
        return args.Handled;
    }

    private static async Task ActivateAsync(ExplorerHarness h, Guid id)
    {
        h.Call("CaptureActiveTabRuntimeState");
        Assert.True(h.Workspace.Select(id));
        await (Task)h.Call("ActivateWorkspaceAsync")!;
        await h.SettleAsync();
    }

    private static async Task OpenImageInTabAsync(ExplorerHarness h, string name)
    {
        h.DoubleClick(name);
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
        await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), name, StringComparison.OrdinalIgnoreCase), "image loaded");
    }

    // ---- A1 / A3: side buttons go Back / Forward in an Explorer tab by default -------------------------------

    [Fact]
    public async Task A1_default_mouse_back_and_forward_navigate_the_Explorer_tab_history()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.Equal(GestureCatalog.Default, h.Settings.Gestures["explorer.backButton"]);   // a fresh install: the row is on Default
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);

            Assert.True(RaiseSideButton(h, h.BrowserView, back: true), "the mouse back button is claimed by default");
            await h.WaitUntil(() => string.Equals(h.CurrentFolder, h.Root, StringComparison.OrdinalIgnoreCase), "Back to the parent folder");
            Assert.True(RaiseSideButton(h, h.BrowserView, back: false), "the mouse forward button is claimed by default");
            await h.WaitUntil(() => string.Equals(h.CurrentFolder, Path.Combine(h.Root, "Alpha"), StringComparison.OrdinalIgnoreCase), "Forward again");

            // An explicit "Do nothing" still blocks them.
            h.Settings.Gestures["explorer.backButton"] = GestureCatalog.Nothing;
            Assert.True(RaiseSideButton(h, h.BrowserView, back: true));
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);
        });
    }

    // ---- A2: Back on an image opened from the Explorer returns there, unless bound to Previous image ---------

    [Fact]
    public async Task A2_default_mouse_back_on_an_image_opened_from_the_Explorer_returns_to_the_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageA);
            Assert.True(RaiseSideButton(h, h.Get<Control>("Viewport"), back: true));
            await h.WaitUntil(() => h.Workspace.Active is BrowserTabState b && string.Equals(b.Folder, h.Root, StringComparison.OrdinalIgnoreCase), "back in the Explorer");
        });
    }

    [Fact]
    public async Task A2_mouse_back_bound_to_previous_image_keeps_browsing_images()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageB);
            await h.WaitUntil(() => h.Get<bool>("_folderIndexReady"), "folder indexed");
            h.Settings.Gestures["windowed.backButtonImage"] = GestureCatalog.Previous;
            Assert.True(RaiseSideButton(h, h.Get<Control>("Viewport"), back: true));
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), ExplorerHarness.ImageA, StringComparison.OrdinalIgnoreCase), "previous image");
            Assert.True(h.Workspace.Active is ImageTabState);
        });
    }

    [Fact]
    public async Task A2_default_mouse_back_on_an_image_without_a_folder_behind_it_is_not_claimed()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Workspace.AddImage(Path.Combine(h.Root, ExplorerHarness.ImageA));
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.WaitUntil(() => h.Get<ImageViewport>("Viewport").Bitmap is not null, "picture shown");
            Assert.False(History(h).CanGoBack);
            Assert.False(RaiseSideButton(h, h.Get<Control>("Viewport"), back: true), "nothing to go back to: Windows keeps the press");
        });
    }

    // ---- F1: a duplicated tab keeps the history ------------------------------------------------------------

    [Fact]
    public async Task F1_duplicating_a_tab_copies_its_Back_Forward_history()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageA);
            var original = h.Workspace.Active!.Id;
            Assert.True(History(h).CanGoBack);

            await h.Window.ExecuteActionAsync(AppActionRegistry.Find("tab.duplicate")!, "test");
            await h.SettleAsync();
            Assert.NotEqual(original, h.Workspace.Active!.Id);
            Assert.True(History(h).CanGoBack, "the duplicate can go Back to the Explorer folder");
            Assert.Equal(h.Window.TabHistories.Find(original)!.Count, History(h).Count);
            Assert.NotSame(h.Window.TabHistories.Find(original), History(h));

            h.Key(Key.Left, RawInputModifiers.Alt);
            await h.WaitUntil(() => h.Workspace.Active is BrowserTabState b && string.Equals(b.Folder, h.Root, StringComparison.OrdinalIgnoreCase), "Back on the duplicate");
        });
    }

    // ---- H1: Space with Settings = Ctrl+, and Space = play/pause GIF --------------------------------------

    [Fact]
    public async Task H1_Space_never_clicks_a_focused_Settings_button()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageA);
            h.Settings.Hotkeys["nav.nextSpace"] = new List<string>();
            h.Settings.Hotkeys["gif.playPause"] = new List<string> { "Space" };
            var candidates = h.Window.GetVisualDescendants().OfType<Button>()
                .Where(b => (ToolTip.GetTip(b)?.ToString() ?? string.Empty).StartsWith("Settings", StringComparison.Ordinal)).ToList();
            var settingsButton = candidates.FirstOrDefault(b => b.IsEffectivelyVisible);
            Assert.True(settingsButton is not null, "visible Settings buttons: " + string.Join(", ", candidates.Select(b => b.Name + ":" + b.IsEffectivelyVisible)));
            Assert.True(settingsButton!.Focus(), "focus " + settingsButton.Name + " focusable=" + settingsButton.Focusable + " enabled=" + settingsButton.IsEffectivelyEnabled);
            var routed = new List<string>();
            h.Window.ActionRouted += (a, s) => routed.Add(a.Id + "/" + s);
            h.Key(Key.Space);
            await h.SettleAsync();
            Assert.True(h.Get<object?>("_openSettingsWindow") is null, "routed: " + string.Join(",", routed) + " focused=" + settingsButton.Name);

            // Also when Space is bound to nothing at all.
            h.Settings.Hotkeys["gif.playPause"] = new List<string>();
            Assert.True(settingsButton.Focus());
            h.Key(Key.Space);
            await h.SettleAsync();
            Assert.Null(h.Get<object?>("_openSettingsWindow"));
        });
    }

    // ---- H2: a paused GIF stays paused on its frame across a tab switch ----------------------------------

    private static byte[] AnimatedGif(int frames)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF });
        for (var i = 0; i < frames; i++)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00 });
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0x02, 0x02, 0x44, 0x01, 0x00 });
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    [Fact]
    public async Task H2_a_paused_GIF_is_still_paused_on_the_same_frame_after_switching_tabs()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Active!.Id;
            var gifPath = Path.Combine(h.Root, "anim.gif");
            File.WriteAllBytes(gifPath, AnimatedGif(4));
            h.Settings.AnimateGifs = true;
            h.Workspace.AddImage(gifPath);
            var gifTab = h.Workspace.Active!.Id;
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            dynamic Player() => h.Get<object>("_gifPlayer");
            await h.WaitUntil(() => h.Get<object?>("_gifPlayer") is { } p && (bool)((dynamic)p).HasAnimation, "GIF playing");
            Assert.True((bool)Player().Step(2));
            Assert.True((bool)Player().IsPaused);
            int frame = Player().FrameIndex;

            await ActivateAsync(h, explorerId);
            await ActivateAsync(h, gifTab);
            await h.WaitUntil(() => (bool)Player().HasAnimation, "GIF frames decoded again");
            Assert.True((bool)Player().IsPaused, "still paused");
            Assert.Equal(frame, (int)Player().FrameIndex);
        });
    }

    // ---- J1 / J2: the image of a background tab was deleted ------------------------------------------------

    [Fact]
    public async Task J2_returning_to_a_tab_whose_image_was_deleted_keeps_its_own_frame_and_shows_the_indicator()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Active!.Id;
            var pathA = Path.Combine(h.Root, ExplorerHarness.ImageA);
            h.Workspace.AddImage(pathA);
            var tabA = h.Workspace.Active!.Id;
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            var viewport = h.Get<ImageViewport>("Viewport");
            await h.WaitUntil(() => viewport.Bitmap is not null && string.Equals(h.Get<string?>("_presentedPath"), pathA, StringComparison.OrdinalIgnoreCase), "A shown");
            var frameA = viewport.Bitmap;

            h.Workspace.AddImage(Path.Combine(h.Root, ExplorerHarness.ImageB));
            var tabB = h.Workspace.Active!.Id;
            await ActivateAsync(h, tabB);
            await h.WaitUntil(() => string.Equals(h.Get<string?>("_presentedPath"), Path.Combine(h.Root, ExplorerHarness.ImageB), StringComparison.OrdinalIgnoreCase), "B shown");

            File.Delete(pathA);
            await ActivateAsync(h, tabA);
            Assert.Same(frameA, viewport.Bitmap);
            Assert.Equal(pathA, h.Get<string?>("_presentedPath"));
            Assert.True(h.Get<Control>("MissingFileIndicator").IsVisible, "the 'no longer on disk' indicator is shown");

            await ActivateAsync(h, tabB);
            Assert.False(h.Get<Control>("MissingFileIndicator").IsVisible);
            _ = explorerId;
        });
    }

    [Fact]
    public async Task J2_a_deleted_image_with_no_kept_frame_never_shows_another_tabs_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            var pathA = Path.Combine(h.Root, ExplorerHarness.ImageA);
            h.Workspace.AddImage(pathA);
            var tabA = h.Workspace.Active!.Id;
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            var viewport = h.Get<ImageViewport>("Viewport");
            await h.WaitUntil(() => string.Equals(h.Get<string?>("_presentedPath"), pathA, StringComparison.OrdinalIgnoreCase), "A shown");

            var pathB = Path.Combine(h.Root, ExplorerHarness.ImageB);
            h.Workspace.AddImage(pathB);
            await ActivateAsync(h, h.Workspace.Active!.Id);
            await h.WaitUntil(() => string.Equals(h.Get<string?>("_presentedPath"), pathB, StringComparison.OrdinalIgnoreCase), "B shown");
            var frameB = viewport.Bitmap;

            File.Delete(pathA);
            h.Call("ClearWarmTabsForTests");
            await ActivateAsync(h, tabA);
            Assert.NotSame(frameB, viewport.Bitmap);
            Assert.NotEqual(pathB, h.Get<string?>("_presentedPath"));
            Assert.True(h.Get<Control>("MissingFileIndicator").IsVisible);
        });
    }
}
