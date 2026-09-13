using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.App.Diagnostics;

/// <summary>
/// Live visual evidence. This intentionally records both pixels and geometry because screenshot-only
/// diagnostics are hard for weak agents to reason about and geometry-only diagnostics miss styling.
/// </summary>
internal static class UiDiagnosticCapture
{
    public static bool TryCapture(Control control, string path, out string? error)
    {
        try
        {
            var width = Math.Max(1, (int)Math.Ceiling(control.Bounds.Width));
            var height = Math.Max(1, (int)Math.Ceiling(control.Bounds.Height));
            var scale = TopLevel.GetTopLevel(control)?.RenderScaling ?? 1.0;
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(control);
            using var stream = File.Create(path);
            bitmap.Save(stream);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            return false;
        }
    }

    public static void WriteGeometry(Control root, string path)
    {
        var sb = new StringBuilder();
        var top = TopLevel.GetTopLevel(root);
        var scale = top?.RenderScaling ?? 1.0;
        sb.AppendLine($"renderScaling={scale:F3}");
        sb.AppendLine($"root={root.GetType().Name} logical={Format(root.Bounds)} physical={FormatPhysical(root.Bounds, scale)}");
        foreach (var control in NamedControls(root))
        {
            var bounds = RelativeBounds(control, root);
            sb.Append(control.Name).Append(" | ").Append(control.GetType().Name)
              .Append(" | visible=").Append(control.IsVisible)
              .Append(" | enabled=").Append(control.IsEnabled)
              .Append(" | logical=").Append(Format(bounds))
              .Append(" | physical=").Append(FormatPhysical(bounds, scale))
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteLayoutAudit(Control root, string path)
    {
        var scale = TopLevel.GetTopLevel(root)?.RenderScaling ?? 1.0;
        var controls = NamedControls(root).Where(x => IsEffectivelyVisible(x, root) && !x.Name!.StartsWith("PART_", StringComparison.Ordinal)).ToArray();
        var rootRect = new Rect(0, 0, root.Bounds.Width, root.Bounds.Height);
        var lines = new List<string>();
        var fails = 0;
        var warns = 0;

        foreach (var control in controls)
        {
            var r = RelativeBounds(control, root);
            if (r.Width < 0 || r.Height < 0 || double.IsNaN(r.Width) || double.IsNaN(r.Height))
            {
                lines.Add($"FAIL invalid_bounds {control.Name} {Format(r)}");
                fails++;
                continue;
            }
            // A ScrollViewer intentionally keeps off-viewport children alive. Those children are not
            // layout failures merely because their translated coordinates are above/below the Window.
            if (IsOutsideScrollViewport(control, root)) continue;

            // Allow a tiny tolerance for borders/rounding; substantial clipping is actionable.
            if (r.Right < -2 || r.Bottom < -2 || r.X > rootRect.Width + 2 || r.Y > rootRect.Height + 2)
            {
                lines.Add($"FAIL outside_root {control.Name} {Format(r)}");
                fails++;
            }
            else if (r.X < -2 || r.Y < -2 || r.Right > rootRect.Width + 2 || r.Bottom > rootRect.Height + 2)
            {
                lines.Add($"WARN clipped_by_root {control.Name} {Format(r)}");
                warns++;
            }
        }

        // Named interactive controls should not collapse to unusably tiny hit targets.
        foreach (var control in controls.Where(x => (x is Button or TextBox or ComboBox or NumericUpDown) && !IsOutsideScrollViewport(x, root)))
        {
            var r = RelativeBounds(control, root);
            if (r.Width > 0 && r.Height > 0 && (r.Width * scale < 18 || r.Height * scale < 18))
            {
                lines.Add($"WARN tiny_hit_target {control.Name} physical={FormatPhysical(r, scale)}");
                warns++;
            }
        }

        lines.Insert(0, $"SUMMARY PASS={(fails == 0 ? 1 : 0)} FAIL={fails} WARN={warns} namedVisible={controls.Length} renderScaling={scale:F3}");
        if (fails == 0) lines.Insert(1, "PASS no named control is wholly outside the root visual bounds");
        // The geometry and hit-target assertions above did execute in this capture. Keep their
        // PASS/FAIL result truthful; settings-effect coverage is a separate SKIP-only report.
        File.WriteAllLines(path, lines);
    }

    private static bool IsEffectivelyVisible(Control control, Control root)
    {
        if (!control.IsVisible) return false;
        if (ReferenceEquals(control, root)) return root.IsVisible;
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is Control parent && !parent.IsVisible) return false;
            if (ReferenceEquals(ancestor, root)) return root.IsVisible;
        }
        return false; // detached/stale descendants are not effectively visible in this capture root.
    }

    public static void WriteControlInventory(Control root, string path)
    {
        var lines = NamedControls(root).Select(c =>
            $"{c.Name}\t{c.GetType().Name}\tvisible={c.IsVisible}\tenabled={c.IsEnabled}\thitTest={c.IsHitTestVisible}");
        File.WriteAllLines(path, lines);
    }

    public static void WriteSettingsEffectCoverage(GlideSettingsState state, string path)
    {
        var registryIssues = SettingEffectRegistry.Validate();
        var registryLines = new List<string>
        {
            registryIssues.Count == 0
                ? $"PASS settings effect registry contract -> {SettingEffectRegistry.All.Count} catalog IDs have unique typed ownership, editor semantics and evidence IDs"
                : "FAIL settings effect registry contract -> " + string.Join("; ", registryIssues)
        };
        registryLines.AddRange(SettingEffectRegistry.All.Select(binding =>
            $"INFO {binding.EvidenceId} -> owner={binding.EffectOwner}; editor={binding.EditorKind}; apply={binding.ApplySemantics}; evidence={binding.EvidenceStatus}"));
        var lines = registryLines.Concat(new[]
        {
            "PASS theme -> live palette + persisted settings",
            "PASS accent -> live palette accent + persisted settings",
            "PASS home tips -> Home cards visibility",
            "PASS recent history -> separate inspectable file/folder history store + Home recent actions + configurable trim",
            "PASS remember placement -> safe main-window placement save/restore",
            "PASS status visible -> viewer/Home status surface",
            "PASS full path title -> main title",
            "PASS tabs enabled -> tab strip visibility",
            "PASS always on top -> Window.Topmost",
            "PASS default view -> viewport initial view mode",
            "PASS pointer zoom -> viewport zoom anchor",
            "PASS preserve zoom -> navigation bitmap replacement",
            "PASS Escape hierarchy -> active slideshow restore, fullscreen exit, selection clear, then optional windowed close",
            "PASS double-click fullscreen settings -> viewport fullscreen toggle",
            "PASS fullscreen click navigation -> one authoritative routed handler",
            "PASS wheel mode/inversion/Ctrl zoom -> viewport input policy",
            "PASS left/right/middle drag settings -> viewport input policy",
            "PASS selection actions -> hover +/- affordance; left-click zoom-in consumes selection; new-selection right-click zoom-out consumes selection; right-drag pan/window ownership is classified separately",
            "PASS background drag -> left uses native BeginMoveDrag where appropriate; right-drag uses physical screen-coordinate whole-window movement so pointer ownership stays stable",
            "PASS status groups/stats -> live status composition",
            "PASS status collapse/expand/close -> session collapse affordance plus persisted hide",
            "PASS slideshow start/pause/resume/stop + interval/loop/shuffle -> contextual controls and exact pre-session view/fullscreen restore",
            "PASS tab min/max width -> fluid rendered tab sizing with scroll overflow",
            "PASS closed history limit -> workspace history trim",
            "PASS (STRUCTURAL) tab reorder/detach/cross-window attach -> tab-origin only; sole-tab and detached paths use destination-DPI-aware forgiving attach zones, accent preview state, and typed runtime-state transfer; live Windows merge remains WARN until exercised",
            "WARN tab native-move certification -> native move loop architecture exists; Aero Snap, monitor-edge, target-destroy and cross-window attach still require live Windows exercise",
            "PASS 68-action hotkey registry -> editable multiple shortcuts + conflict reassignment + Shift+O overlay add + Settings search editing",
            "PASS 24-slot gesture matrix -> persisted semantic action routing through InputRouter",
            "PASS picture counter -> template/font/weight/opacity/position/colour/shadow",
            "PASS viewport scrollbars -> synchronized with bounded pan and only visible when required",
            "PASS session transparency -> interactive slider; resets to 100% every process launch",
            "PASS external program slots -> persisted + programmable hotkey launch",
            "PASS profiles/presets -> five stock presets, three profile slots, complete JSON import/export",
            "PASS startup diagnostics flag -> lightweight append-only JSONL timings; never launches the full diagnostic suite during startup",
            "PASS settings persistence -> portable/installed JSON store",
            "PASS diagnostic export location -> Windows Downloads known folder",
            "PASS live diagnostics capture path -> main + all Settings categories immediate/settled + search + per-page/resize geometry/layout + real image fixture timing evidence",
            "PASS performance controls -> coordinated quality profiles + decoder-scaled first frame + idle full-resolution refinement + separate compressed/decoded budgets + direction-aware neighbour preparation + purge-on-minimize",
            "PASS progressive JPEG colour-first preview -> active persisted Performance setting; Balanced/Maximum quality default on, Maximum speed default off",
            "PASS sibling-folder group visibility -> FolderNavShowGroup gates the parent navigation group; child controls remain independently configurable",
            "PASS sibling-folder hidden policy -> explicit include/exclude-hidden setting participates in persisted navigation policy",
            "SKIP Window-in-Window -> source contract and layout persistence are checked headlessly; drag/resize/zoom/z-order rendering requires Windows live capture",
            "ACTIVE Open With/file associations -> per-user Windows registration controls enabled"
        });
        // This method records a pending-live checklist alongside the current snapshot. Every row
        // other than the computed geometry result is deliberately emitted as SKIP.
        lines = lines.Select(x => x.StartsWith("PASS ", StringComparison.Ordinal) &&
                                      !x.Contains("settings effect registry contract", StringComparison.OrdinalIgnoreCase) &&
                                      !x.Contains("settings.effect.", StringComparison.Ordinal)
            ? "SKIP (pending live assertion) " + x[5..]
            : x).ToArray();
        File.WriteAllLines(path, lines);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "settings_current.json"),
            System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsOutsideScrollViewport(Control control, Control root)
    {
        var controlRect = RelativeBounds(control, root);
        for (var current = control.GetVisualParent(); current is not null && !ReferenceEquals(current, root); current = current.GetVisualParent())
        {
            if (current is not ScrollViewer scroll) continue;
            var scrollRect = RelativeBounds(scroll, root);
            var intersects = controlRect.Right > scrollRect.Left && controlRect.Left < scrollRect.Right &&
                             controlRect.Bottom > scrollRect.Top && controlRect.Top < scrollRect.Bottom;
            if (!intersects) return true;
        }
        return false;
    }

    private static IEnumerable<Control> NamedControls(Control root) =>
        root.GetLogicalDescendants().OfType<Control>().Where(c => !string.IsNullOrWhiteSpace(c.Name));

    private static Rect RelativeBounds(Control control, Control root)
    {
        try
        {
            var p = control.TranslatePoint(new Point(0, 0), root) ?? new Point(control.Bounds.X, control.Bounds.Y);
            return new Rect(p, control.Bounds.Size);
        }
        catch
        {
            return control.Bounds;
        }
    }

    private static string Format(Rect r) => $"{r.X:F1},{r.Y:F1},{r.Width:F1},{r.Height:F1}";
    private static string FormatPhysical(Rect r, double scale) => $"{r.X * scale:F0},{r.Y * scale:F0},{r.Width * scale:F0},{r.Height * scale:F0}";
}
