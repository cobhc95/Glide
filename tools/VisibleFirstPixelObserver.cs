using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

public sealed class GlideVisibleObservation
{
    public GlideVisibleObservation() { Failure = string.Empty; }
    public bool Detected { get; set; }
    public int ProcessId { get; set; }
    public long WindowHandle { get; set; }
    public double CaptureMilliseconds { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public string Failure { get; set; }
}

public static class GlideVisibleFirstPixelObserver
{
    private static readonly object CaptureGate = new object();
    private static Bitmap _captureBuffer;
    private static ReferenceFingerprint _reference;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private sealed class ReferenceFingerprint
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public Sample[] Samples { get; set; }
    }
    private struct Sample
    {
        public readonly double X;
        public readonly double Y;
        public readonly Color Color;
        public Sample(double x, double y, Color color) { X = x; Y = y; Color = color; }
    }

    /// <summary>
    /// Configures an image-specific spatial reference for photographic/specialist corpus cases.
    /// The reference may be a decodable companion image containing the same expected pixels as the
    /// launched file (for example a PNG reference for an AVIF/WebP routing fixture).
    /// </summary>
    public static void ConfigureReference(string referencePath)
    {
        using (var source = new Bitmap(referencePath))
        {
            _reference = BuildReference(source);
        }
        if (_reference.Samples.Length < 12)
            throw new InvalidOperationException("Reference fingerprint has too few opaque spatial samples.");
    }

    public static void ClearReference() { _reference = null; }

    public static bool MatchesConfiguredReference(Bitmap bitmap)
    {
        var reference = _reference;
        return reference != null && MatchesReference(bitmap, reference);
    }

    public static GlideVisibleObservation Observe(int[] pids)
    {
        var sw = Stopwatch.StartNew();
        var allowed = new HashSet<int>(pids ?? new int[0]);
        var result = new GlideVisibleObservation();
        if (allowed.Count == 0) { result.Failure = "no_owned_pids"; return result; }
        EnumWindows(delegate(IntPtr h, IntPtr ignored)
        {
            if (result.Detected || !IsWindowVisible(h)) return true;
            uint rawPid;
            GetWindowThreadProcessId(h, out rawPid);
            var pid = unchecked((int)rawPid);
            if (!allowed.Contains(pid)) return true;
            RECT r;
            if (!GetClientRect(h, out r)) return true;
            var w = r.Right - r.Left; var hgt = r.Bottom - r.Top;
            if (w < 24 || hgt < 24) return true;
            var origin = new POINT();
            if (!ClientToScreen(h, ref origin)) return true;
            try
            {
                lock (CaptureGate)
                {
                    var buffer = _captureBuffer;
                    if (buffer == null || buffer.Width != w || buffer.Height != hgt)
                    {
                        if (buffer != null) buffer.Dispose();
                        buffer = new Bitmap(w, hgt, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        _captureBuffer = buffer;
                    }
                    using (var g = Graphics.FromImage(buffer))
                        g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, hgt), CopyPixelOperation.SourceCopy);
                    var reference = _reference;
                    var matches = reference != null ? MatchesReference(buffer, reference) : MatchesTarget(buffer);
                    if (matches)
                    {
                        result.Detected = true;
                        result.ProcessId = pid;
                        result.WindowHandle = h.ToInt64();
                        result.ClientWidth = w;
                        result.ClientHeight = hgt;
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        sw.Stop();
        result.CaptureMilliseconds = sw.Elapsed.TotalMilliseconds;
        if (!result.Detected && result.Failure.Length == 0) result.Failure = "fingerprint_not_found_in_owned_windows";
        return result;
    }

    private static ReferenceFingerprint BuildReference(Bitmap source)
    {
        var samples = new List<Sample>();
        // A 9x7 interior grid is spatially ordered and intentionally asymmetric. Transparent/near-
        // transparent cells are omitted because desktop composition legitimately changes their RGB.
        for (var gy = 1; gy <= 7; gy++)
        for (var gx = 1; gx <= 9; gx++)
        {
            var nx = gx / 10.0;
            var ny = gy / 8.0;
            var x = ClampInt((int)Math.Round(nx * (source.Width - 1)), 0, source.Width - 1);
            var y = ClampInt((int)Math.Round(ny * (source.Height - 1)), 0, source.Height - 1);
            var c = source.GetPixel(x, y);
            if (c.A < 210) continue;
            samples.Add(new Sample(nx, ny, c));
        }
        return new ReferenceFingerprint { Width = source.Width, Height = source.Height, Samples = samples.ToArray() };
    }

    private static bool MatchesReference(Bitmap screen, ReferenceFingerprint reference)
    {
        if (screen.Width < 24 || screen.Height < 24 || reference.Width < 2 || reference.Height < 2) return false;
        var aspect = reference.Width / (double)reference.Height;
        double fitWidth = screen.Width;
        double fitHeight = fitWidth / aspect;
        if (fitHeight > screen.Height)
        {
            fitHeight = screen.Height;
            fitWidth = fitHeight * aspect;
        }

        // Most viewers center the fitted image, but custom chrome/status bars shift the viewport.
        // Search a bounded family of plausible fit rectangles plus the source's native 1:1 size.
        // This is still ROI/window-scoped and image-specific; it never scans the whole desktop.
        double[] scaleFactors = { 1.00, 0.96, 0.92, 0.88, 0.84, 0.80, 0.72, 0.64, 0.56, 0.48, 0.40 };
        double[] xOffsets = { 0.0, -0.035, 0.035 };
        double[] yOffsets = { 0.0, -0.12, -0.08, -0.04, 0.04, 0.08, 0.12 };
        foreach (var factor in scaleFactors)
        {
            var w = fitWidth * factor;
            var h = fitHeight * factor;
            if (w < 12 || h < 12) continue;
            if (TryCandidateFamily(screen, reference, w, h, xOffsets, yOffsets)) return true;
        }

        if (reference.Width <= screen.Width && reference.Height <= screen.Height &&
            TryCandidateFamily(screen, reference, reference.Width, reference.Height, xOffsets, yOffsets)) return true;
        return false;
    }

    private static bool TryCandidateFamily(Bitmap screen, ReferenceFingerprint reference, double width, double height,
        double[] xOffsets, double[] yOffsets)
    {
        foreach (var xo in xOffsets)
        foreach (var yo in yOffsets)
        {
            var left = (screen.Width - width) * 0.5 + screen.Width * xo;
            var top = (screen.Height - height) * 0.5 + screen.Height * yo;
            if (left < -1 || top < -1 || left + width > screen.Width + 1 || top + height > screen.Height + 1) continue;
            if (CandidateMatches(screen, reference, left, top, width, height)) return true;
        }
        return false;
    }

    private static bool CandidateMatches(Bitmap screen, ReferenceFingerprint reference, double left, double top, double width, double height)
    {
        var totalDiff = 0.0;
        var close = 0;
        var used = 0;
        foreach (var sample in reference.Samples)
        {
            var x = ClampInt((int)Math.Round(left + sample.X * Math.Max(1, width - 1)), 0, screen.Width - 1);
            var y = ClampInt((int)Math.Round(top + sample.Y * Math.Max(1, height - 1)), 0, screen.Height - 1);
            var actual = screen.GetPixel(x, y);
            var diff = (Math.Abs(actual.R - sample.Color.R) + Math.Abs(actual.G - sample.Color.G) + Math.Abs(actual.B - sample.Color.B)) / 3.0;
            totalDiff += diff;
            if (diff <= 72) close++;
            used++;
        }
        if (used < 12) return false;
        return close >= Math.Ceiling(used * 0.72) && totalDiff / used <= 50.0;
    }

    private static int ClampInt(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    // Deterministic synthetic target detector retained for calibration/quick harness checks.
    private enum C { None, Magenta, Lime, Cyan, Yellow, White }
    private static C Classify(Color p)
    {
        if (p.R > 220 && p.G > 220 && p.B > 220) return C.White;
        if (p.R > 175 && p.B > 175 && p.G < 165) return C.Magenta;
        if (p.G > 175 && p.R < 165 && p.B < 175) return C.Lime;
        if (p.G > 165 && p.B > 175 && p.R < 165) return C.Cyan;
        if (p.R > 175 && p.G > 160 && p.B < 165) return C.Yellow;
        return C.None;
    }

    public static bool MatchesTarget(Bitmap b)
    {
        int minX=b.Width, minY=b.Height, maxX=-1, maxY=-1;
        int step = Math.Max(2, Math.Min(b.Width,b.Height)/180);
        for (int y=0;y<b.Height;y+=step) for(int x=0;x<b.Width;x+=step)
        {
            var c=Classify(b.GetPixel(x,y));
            if (c == C.Magenta || c == C.Lime || c == C.Cyan || c == C.Yellow || c == C.White)
            { minX=Math.Min(minX,x); maxX=Math.Max(maxX,x); minY=Math.Min(minY,y); maxY=Math.Max(maxY,y); }
        }
        if (maxX-minX < 80 || maxY-minY < 80) return false;
        int midX=(minX+maxX)/2, midY=(minY+maxY)/2;
        var counts = new Dictionary<C,int>[] { new Dictionary<C,int>(), new Dictionary<C,int>(), new Dictionary<C,int>(), new Dictionary<C,int>() };
        int[,] rects={{minX,minY,midX,midY},{midX,minY,maxX,midY},{minX,midY,midX,maxY},{midX,midY,maxX,maxY}};
        for(int q=0;q<4;q++)
        {
            int sx=Math.Max(2,(rects[q,2]-rects[q,0])/28), sy=Math.Max(2,(rects[q,3]-rects[q,1])/28);
            for(int y=rects[q,1];y<rects[q,3];y+=sy) for(int x=rects[q,0];x<rects[q,2];x+=sx)
            { var c=Classify(b.GetPixel(x,y)); int n; if(counts[q].TryGetValue(c,out n)) counts[q][c]=n+1; else counts[q][c]=1; }
        }
        C[] expected={C.Magenta,C.Lime,C.Cyan,C.Yellow};
        for(int q=0;q<4;q++)
        {
            int good; counts[q].TryGetValue(expected[q],out good);
            var coloured=0; foreach(var kv in counts[q]) if(kv.Key!=C.None && kv.Key!=C.White) coloured+=kv.Value;
            if(coloured < 12 || good < coloured*0.55) return false;
        }
        var markerX=minX+(maxX-minX)/8; var markerY=minY+(maxY-minY)/8;
        var mirrorX=maxX-(maxX-minX)/8;
        return WhiteNearby(b,markerX,markerY,Math.Max(3,step*3)) && !WhiteNearby(b,mirrorX,markerY,Math.Max(3,step*2));
    }

    private static bool WhiteNearby(Bitmap b,int cx,int cy,int radius)
    {
        for(int y=Math.Max(0,cy-radius);y<Math.Min(b.Height,cy+radius);y+=2)
            for(int x=Math.Max(0,cx-radius);x<Math.Min(b.Width,cx+radius);x+=2)
                if(Classify(b.GetPixel(x,y))==C.White) return true;
        return false;
    }
}
