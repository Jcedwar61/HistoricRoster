using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Shared;

namespace HistoricRosterApp;

/// <summary>
/// Paints a driver's race number on a race-number texture (number panel, plate or decal sheet) in place of the number
/// that is there: finds shapes that stand out from the background (digits next to each other grouped into one number,
/// plates looked into), keeps those that look like the old number (the base livery's number drawn in each of the 8 ways
/// a texture can hold it: turned and/or mirrored), wipes them with the background colour and draws the new number in
/// the old digits' colour, as large as the old number and turned/mirrored the same way.
/// </summary>
internal static class NumberPainter
{
    /// <summary>Colour difference (sum over R, G, B, A) above which a pixel is ink rather than background.</summary>
    private const int InkThreshold = 90;
    /// <summary>A shape mostly (this share) of one colour other than the background is a plate the digits may be on.</summary>
    private const double PlateShare = 0.45;
    private const int MaxShapes = 40;
    /// <summary>How well (intersection over union) a shape must match the old number to be taken for it.</summary>
    private const double MinMatch = 0.5;
    /// <summary>The same without the old number (shapes are compared with "8", "88" and "888").</summary>
    private const double MinMatchUnknown = 0.4;
    /// <summary>How far a shape's width/height may be from the number's (as drawn that way) and still be taken for it.</summary>
    private const double MaxAspectOff = 1.8;

    /// <param name="Plate">For a shape found on a plate: the plate's index (the plate itself is a candidate too).</param>
    private sealed record Blob(int X0, int Y0, int X1, int Y1, ColorRgba32 Ink, ColorRgba32 Background, int Cell, int Plate = -1)
    {
        public int W => X1 - X0;
        public int H => Y1 - Y0;
    }

    /// <summary>A way a number lies on a texture: quarter turns (counter-clockwise), mirrored or not.</summary>
    private readonly record struct Orientation(int Quarter, bool Mirror);

    private static readonly Orientation[] Orientations =
        Enumerable.Range(0, 8).Select(i => new Orientation(i % 4, i >= 4)).ToArray();

    /// <summary>Returns null and the new pixels on success, else why the number couldn't be painted.</summary>
    /// <param name="oldNumber">The base livery's number (its ui_skin.json), used to tell its number from logos and
    /// to see which way it lies. Without it, every shape found is taken for a number.</param>
    public static string? Repaint(ColorRgba32[] src, int w, int h, string number, string? oldNumber, out ColorRgba32[]? result)
        => Repaint(src, w, h, number, oldNumber, out result, out _);

    internal static string? Repaint(ColorRgba32[] src, int w, int h, string number, string? oldNumber,
        out ColorRgba32[]? result, out List<string> trace)
    {
        result = null;
        trace = new List<string>();
        if (string.IsNullOrWhiteSpace(number)) return "no race number";
        if (w < 16 || h < 16) return "texture too small";

        // Alpha matters only when the texture uses it (some see-through, some solid); many leave it all 0 or all 255.
        long clear = 0, solid = 0;
        for (var i = 0; i < src.Length; i += 3)
        {
            if (src[i].a < 32) clear++;
            else if (src[i].a > 224) solid++;
        }
        var sampled = (src.Length + 2) / 3;
        var alpha = clear > sampled / 100 && solid > sampled / 100;
        var blobs = new List<Blob>();
        var bg = Dominant(alpha, src, w, 0, 0, w, h, _ => true, out _);
        Find(src, w, h, 0, 0, w, h, bg, depth: 0, alpha, blobs);
        if (blobs.Count == 0) return "no number found on it";
        if (blobs.Count > MaxShapes) return $"{blobs.Count} shapes on it, can't tell which are the number";

        var knownOld = !string.IsNullOrWhiteSpace(oldNumber);
        var templates = knownOld ? new[] { oldNumber!.Trim() } : new[] { "8", "88", "888" };
        var minMatch = knownOld ? MinMatch : MinMatchUnknown;
        var log = trace;
        var painted = RunSta(() =>
        {
            var scored = new List<(Blob Blob, Orientation O, double Score)>();
            foreach (var b in blobs)
            {
                // The shape's own ink, then the old number drawn each way and stretched over it: the best match wins.
                var ink = new byte[b.W * b.H];
                for (var y = 0; y < b.H; y++)
                    for (var x = 0; x < b.W; x++)
                        ink[y * b.W + x] = Dist(alpha, src[(b.Y0 + y) * w + b.X0 + x], b.Background) > InkThreshold ? (byte)255 : (byte)0;
                var shape = (double)b.W / b.H;
                var best = templates
                    .SelectMany(t => Orientations.Select(o => (T: t, O: o)))
                    .Where(c =>
                    {
                        var a = Aspect(c.T);
                        if (c.O.Quarter % 2 == 1) a = 1 / a;
                        return Math.Max(shape / a, a / shape) <= MaxAspectOff;
                    })
                    .Select(c => (c.T, c.O, Score: Match(ink, Mask(c.T, b.W, b.H, c.O, stretch: true))))
                    .DefaultIfEmpty((T: "", O: default(Orientation), Score: 0d))
                    .MaxBy(c => c.Score);
                log.Add($"shape {b.X0},{b.Y0} {b.W}x{b.H}: " + (best.T == "" ? "not shaped like a number"
                    : $"looks like {best.T} turned {best.O.Quarter * 90} deg{(best.O.Mirror ? " mirrored" : "")} ({best.Score:0.00})"));
                scored.Add((b, best.O, best.Score));
            }
            // A plate or what's on it (plates within plates too), whichever looks more like the number.
            (List<int> Picked, double Score) Pick(int i)
            {
                var kids = Enumerable.Range(0, scored.Count).Where(k => scored[k].Blob.Plate == i).Select(Pick).ToList();
                var kidsBest = kids.Count == 0 ? -1 : kids.Max(k => k.Score);
                return scored[i].Score >= kidsBest ? (new List<int> { i }, scored[i].Score) : (kids.SelectMany(k => k.Picked).ToList(), kidsBest);
            }
            return Enumerable.Range(0, scored.Count).Where(i => scored[i].Blob.Plate < 0).SelectMany(i => Pick(i).Picked)
                .Where(i => scored[i].Score >= minMatch)
                .Select(i => (scored[i].Blob, Mask: Mask(number, scored[i].Blob.W, scored[i].Blob.H, scored[i].O, stretch: false))).ToList();
        });
        if (painted.Count == 0) return knownOld ? $"no shape on it looks like the old number {oldNumber!.Trim()}" : "no shape on it looks like a number";

        var px = (ColorRgba32[])src.Clone();
        foreach (var (b, mask) in painted)
        {
            // Wipe the old digits (and their soft edges) a little beyond their outline.
            var pad = Math.Max(1, b.Cell);
            int x0 = Math.Max(0, b.X0 - pad), y0 = Math.Max(0, b.Y0 - pad);
            int x1 = Math.Min(w, b.X1 + pad), y1 = Math.Min(h, b.Y1 + pad);
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    if (Dist(alpha, px[y * w + x], b.Background) > InkThreshold / 3) px[y * w + x] = b.Background;
            for (var y = 0; y < b.H; y++)
                for (var x = 0; x < b.W; x++)
                {
                    var a = mask[y * b.W + x];
                    if (a == 0) continue;
                    var i = (b.Y0 + y) * w + b.X0 + x;
                    px[i] = Lerp(px[i], b.Ink, a / 255.0);
                }
        }
        result = px;
        return null;
    }

    /// <summary>Shapes in a region: groups of ink cells; a plate is looked into with its own colour as background.</summary>
    private static void Find(ColorRgba32[] src, int w, int h, int rx, int ry, int rw, int rh, ColorRgba32 bg, int depth, bool alpha, List<Blob> found, int plate = -1)
    {
        var cell = Math.Max(1, Math.Min(rw, rh) / 1024);
        int gw = (rw + cell - 1) / cell, gh = (rh + cell - 1) / cell;
        var ink = new bool[gw * gh];
        for (var gy = 0; gy < gh; gy++)
            for (var gx = 0; gx < gw; gx++)
            {
                int n = 0, total = 0;
                for (var y = ry + gy * cell; y < Math.Min(ry + rh, ry + (gy + 1) * cell); y++)
                    for (var x = rx + gx * cell; x < Math.Min(rx + rw, rx + (gx + 1) * cell); x++)
                    {
                        total++;
                        if (Dist(alpha, src[y * w + x], bg) > InkThreshold) n++;
                    }
                ink[gy * gw + gx] = total > 0 && n * 3 >= total;
            }

        // Connected groups of ink cells (cell bounding boxes, end exclusive) and how many cells each has.
        var groups = new List<(int X0, int Y0, int X1, int Y1, int Cells)>();
        var seen = new bool[ink.Length];
        var stack = new Stack<int>();
        for (var start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || seen[start]) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0, cells = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                int cx = c % gw, cy = c / gw;
                cells++;
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx + 1); y1 = Math.Max(y1, cy + 1);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                        var ni = ny * gw + nx;
                        if (ink[ni] && !seen[ni]) { seen[ni] = true; stack.Push(ni); }
                    }
            }
            if (cells >= 4) groups.Add((x0, y0, x1, y1, cells));
        }
        if (groups.Count > 2000) return; // noise or a photo, not a number sheet
        // Inside a plate, what touches its edge is the plate's surroundings (rounded corners, border), not digits.
        if (depth > 0) groups.RemoveAll(g => g.X0 == 0 || g.Y0 == 0 || g.X1 == gw || g.Y1 == gh);

        // Digits of one number: side by side (or stacked, for a turned number) and close, or one inside another.
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < groups.Count && !merged; i++)
                for (var j = i + 1; j < groups.Count && !merged; j++)
                {
                    var a = groups[i]; var b = groups[j];
                    int ha = a.Y1 - a.Y0, hb = b.Y1 - b.Y0, wa = a.X1 - a.X0, wb = b.X1 - b.X0;
                    var overlapY = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
                    var overlapX = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0);
                    var sideBySide = overlapY > 0.7 * Math.Min(ha, hb) && Math.Abs(ha - hb) < 0.25 * Math.Max(ha, hb) && -overlapX < 0.35 * Math.Max(ha, hb);
                    var stacked = overlapX > 0.7 * Math.Min(wa, wb) && Math.Abs(wa - wb) < 0.25 * Math.Max(wa, wb) && -overlapY < 0.35 * Math.Max(wa, wb);
                    var inside = overlapX >= Math.Min(wa, wb) && overlapY >= Math.Min(ha, hb);
                    if (!sideBySide && !stacked && !inside) continue;
                    groups[i] = (Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1), a.Cells + b.Cells);
                    groups.RemoveAt(j);
                    merged = true;
                }
        }

        foreach (var g in groups)
        {
            int gwid = g.X1 - g.X0, ghei = g.Y1 - g.Y0;
            // Too small to be a number, a line (border, stripe), or the whole region (a pattern, not a number).
            if (Math.Max(gwid, ghei) < 8 || Math.Min(gwid, ghei) < 3 || (double)gwid * ghei < 0.00005 * gw * gh) continue;
            if (gwid > 8 * ghei || ghei > 8 * gwid) continue;
            if (gwid > 0.97 * gw && ghei > 0.97 * gh) continue;
            int px0 = rx + g.X0 * cell, py0 = ry + g.Y0 * cell;
            int px1 = Math.Min(rx + rw, rx + g.X1 * cell), py1 = Math.Min(ry + rh, ry + g.Y1 * cell);
            // On a plate, digits fill most of its height or width; a smaller shape inside a bold digit is its hole.
            if (depth > 0 && Math.Max((double)(px1 - px0) / rw, (double)(py1 - py0) / rh) < 0.55) continue;

            var colour = Dominant(alpha, src, w, px0, py0, px1, py1, p => Dist(alpha, p, bg) > InkThreshold, out _);
            var index = found.Count;
            found.Add(new Blob(px0, py0, px1, py1, colour, bg, cell, plate));

            // Maybe a plate: mostly one colour of its own. Shapes on it (against that colour) are candidates too.
            if (depth < 2)
            {
                var plateColour = Dominant(alpha, src, w, px0, py0, px1, py1, _ => true, out var share);
                if (Dist(alpha, plateColour, bg) > InkThreshold && share > PlateShare)
                {
                    Find(src, w, h, px0, py0, px1 - px0, py1 - py0, plateColour, depth + 1, alpha, found, index);
                }
            }
        }
    }

    /// <summary>The most common colour of a region (colours close together count as one) over pixels that pass the
    /// filter, and the share of those pixels that have it.</summary>
    private static ColorRgba32 Dominant(bool alpha, ColorRgba32[] src, int w, int x0, int y0, int x1, int y1, Func<ColorRgba32, bool> filter, out double share)
    {
        share = 0;
        long counted = 0;
        var bins = new Dictionary<int, (long N, long R, long G, long B, long A)>();
        var step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 250_000));
        for (var y = y0; y < y1; y += step)
            for (var x = x0; x < x1; x += step)
            {
                var p = src[y * w + x];
                if (!filter(p)) continue;
                counted++;
                // See-through pixels are one colour when the alpha holds the shapes, whatever colour they keep.
                var key = alpha && p.a < 32 ? -1 : (p.r >> 4) | (p.g >> 4) << 4 | (p.b >> 4) << 8 | (p.a >> 6) << 12;
                bins.TryGetValue(key, out var v);
                bins[key] = (v.N + 1, v.R + p.r, v.G + p.g, v.B + p.b, v.A + p.a);
            }
        if (bins.Count == 0) return new ColorRgba32(255, 255, 255, 255);
        var t = bins.Values.MaxBy(v => v.N);
        var dominant = new ColorRgba32((byte)(t.R / t.N), (byte)(t.G / t.N), (byte)(t.B / t.N), (byte)(t.A / t.N));
        // Share of pixels near that colour (shading and noise included).
        long near = 0;
        for (var y = y0; y < y1; y += step)
            for (var x = x0; x < x1; x += step)
            {
                var p = src[y * w + x];
                if (filter(p) && Dist(alpha, p, dominant) <= InkThreshold / 2) near++;
            }
        share = (double)near / counted;
        return dominant;
    }

    /// <summary>Colour difference. With <paramref name="alpha"/> (the texture's alpha holds the shapes), alpha counts too.</summary>
    private static int Dist(bool alpha, ColorRgba32 p, ColorRgba32 q)
    {
        var colour = Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b);
        if (!alpha) return colour;
        // The shapes are in the alpha channel: see-through pixels are alike whatever colour they hold.
        return Math.Abs(p.a - q.a) + (p.a >= 32 && q.a >= 32 ? colour : 0);
    }

    private static ColorRgba32 Lerp(ColorRgba32 p, ColorRgba32 q, double t)
        => new((byte)Math.Round(p.r + (q.r - p.r) * t), (byte)Math.Round(p.g + (q.g - p.g) * t),
               (byte)Math.Round(p.b + (q.b - p.b) * t), (byte)Math.Round(p.a + (q.a - p.a) * t));

    /// <summary>Intersection over union of two coverage masks.</summary>
    private static double Match(byte[] a, byte[] b)
    {
        long both = 0, any = 0;
        for (var i = 0; i < a.Length; i++)
        {
            bool x = a[i] > 127, y = b[i] > 127;
            if (x && y) both++;
            if (x || y) any++;
        }
        return any == 0 ? 0 : (double)both / any;
    }

    // ------------------------------------------------------------------ text (WPF, on an STA thread)

    private static readonly Typeface Font = new(new FontFamily("Arial Black, Arial"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    /// <summary>Width / height of a number as drawn (its ink, not its line box).</summary>
    private static double Aspect(string text)
    {
        var b = Glyphs(text).Bounds;
        return b.IsEmpty || b.Height <= 0 ? 0.7 * text.Length : b.Width / b.Height;
    }

    private static Geometry Glyphs(string text)
        => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 100, Brushes.White, 1.0)
            .BuildGeometry(new Point(0, 0));

    /// <summary>
    /// Coverage (0-255) of the number drawn in w x h, lying the given way. Stretched, it fills the box (for comparing
    /// shapes); otherwise it keeps its proportions and is as large as fits.
    /// </summary>
    private static byte[] Mask(string text, int w, int h, Orientation o, bool stretch)
    {
        var geo = Glyphs(text);
        var b = geo.Bounds;
        var mask = new byte[w * h];
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return mask;
        var turned = o.Quarter % 2 == 1;
        double availW = turned ? h : w, availH = turned ? w : h;
        double sx = availW / b.Width, sy = availH / b.Height;
        if (!stretch) sx = sy = Math.Min(sx, sy) * 0.96;
        var t = new TransformGroup();
        t.Children.Add(new TranslateTransform(-b.X - b.Width / 2, -b.Y - b.Height / 2));
        t.Children.Add(new ScaleTransform(o.Mirror ? -sx : sx, sy));
        t.Children.Add(new RotateTransform(-90 * o.Quarter));
        t.Children.Add(new TranslateTransform(w / 2.0, h / 2.0));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(t);
            dc.DrawGeometry(Brushes.White, null, geo);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var raw = new byte[w * h * 4];
        rtb.CopyPixels(raw, w * 4, 0);
        for (var i = 0; i < mask.Length; i++) mask[i] = raw[i * 4 + 3];
        return mask;
    }

    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Couldn't draw the race number: " + error.Message, error);
        return result;
    }
}
