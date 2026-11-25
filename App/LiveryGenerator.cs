using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using Microsoft.Toolkit.HighPerformance;
using HistoricRosterPlugin;

namespace HistoricRosterApp;

/// <summary>
/// Makes a livery of its own for drivers that don't have one: a copy of an installed livery of the same car with its
/// paint recolored to a colour per driver (neutral parts - white, black, grey, carbon - stay as they are, so most
/// logos and details survive), and the driver's name, number, team and nationality in ui_skin.json. The driver's race
/// number (from the race information) is also painted on the car's race-number textures (e.g. "numberplate.dds",
/// "RaceNumber.dds"): the base livery's number found there is replaced. A number painted into the main livery texture
/// stays as it is, because where it sits is different for every car. Generated liveries are new folders starting with
/// "hr_"; the original livery is never changed.
/// </summary>
public static class LiveryGenerator
{
    /// <summary>Texture names that are not paint (normal, spec, AO maps, glass...) and are copied unchanged.</summary>
    private static readonly Regex NotPaint = new(
        @"(^|[_\-\s.])(nm|normal|norm|nrm|n|map|maps|txmaps|spec|specular|ao|occ|occlusion|detail|glass|dirt|damage|mask|rough|roughness|metal|metallic|bump|height)($|[_\-\s.\d])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Textures that hold just the race number (number panels, plates, decals): repainted with the driver's number.</summary>
    private static readonly Regex NumberTexture = new(
        @"(?<![a-z])(race|driver_?|car_?)?(num|nums|number|numbers|numberplate|numberplates|digit|digits|dorsal)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record Result(List<string> Created, List<string> Notes);

    /// <param name="realHues">Optional: each driver's real livery colour (hue), e.g. from the AI; null entries get a colour of their own.</param>
    public static Result Generate(string content, string model, string baseSkin, IReadOnlyList<RosterEntry> drivers,
        IProgress<string> log, CancellationToken ct, IReadOnlyDictionary<string, double?>? realHues = null)
    {
        var created = new List<string>();
        var notes = new List<string>();
        var skins = Path.Combine(content, "cars", model, "skins");
        var source = Path.Combine(skins, baseSkin);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Livery {baseSkin} of {model} not found.");

        // Decode each paint texture once; every driver gets a recolored copy.
        var files = Directory.GetFiles(source);
        var paint = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        var numbers = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(f);
            if (name.Equals("ui_skin.json", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = Path.GetFileNameWithoutExtension(name);
            if (NotPaint.IsMatch(stem) && !IsPreview(name)) continue;
            var isNumber = NumberTexture.IsMatch(stem) && !IsPreview(name);
            try
            {
                var img = Image.Load(f);
                if (img != null) (isNumber ? numbers : paint)[f] = img;
                else notes.Add($"{name}: texture format not supported, copied unchanged.");
            }
            catch (Exception ex) { notes.Add($"{name}: couldn't read ({ex.Message}), copied unchanged."); }
        }
        var baseNumber = ReadUiSkinNumber(source);
        if (numbers.Count > 0)
            log.Report($"Race-number textures of {baseSkin}: {string.Join(", ", numbers.Keys.Select(Path.GetFileName))}; each driver's number is painted on them.");
        else
            log.Report($"{baseSkin} has no separate race-number texture; the number is only set in the livery's info (name tags).");
        if (!paint.Keys.Any(k => !IsPreview(Path.GetFileName(k))))
            throw new InvalidOperationException($"Livery {baseSkin} has no paint texture the program can recolor.");

        // The livery's main colour: the hue most of its coloured paint has.
        var main = paint.Where(p => !IsPreview(Path.GetFileName(p.Key))).OrderByDescending(p => p.Value.Pixels.Length).First().Value;
        var (baseHue, colouredShare) = DominantHue(main);
        var neutralBase = colouredShare < 0.08; // white/grey/black car: paint the light parts instead
        log.Report($"Base livery {baseSkin}: {(neutralBase ? "mostly white/grey/black, the light parts are painted" : $"main colour hue {baseHue:0}°, recoloured")}.");

        var hues = HuesFor(drivers.Count, baseHue);
        if (realHues != null)
            for (var i = 0; i < drivers.Count; i++)
                if (realHues.TryGetValue(drivers[i].Driver, out var h) && h is { } real) hues[i] = real;
        for (var i = 0; i < drivers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = drivers[i];
            var folder = FolderName(e);
            var target = Path.Combine(skins, folder);
            log.Report($"Making a livery for {e.Driver}{(e.Number != null ? " #" + e.Number : "")} ({i + 1}/{drivers.Count}) ...");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.Equals("ui_skin.json", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(target, name);
                if (paint.TryGetValue(f, out var img)) img.SaveRecolored(dest, baseHue, hues[i], neutralBase);
                else if (numbers.TryGetValue(f, out var num))
                {
                    ColorRgba32[]? px = null;
                    var why = string.IsNullOrWhiteSpace(e.Number) ? "no race number in the race information"
                        : NumberPainter.Repaint(num.Pixels, num.Width, num.Height, e.Number.Trim().TrimStart('#'), baseNumber, out px);
                    if (why == null) num.Save(dest, px!);
                    else
                    {
                        File.Copy(f, dest, overwrite: true);
                        notes.Add($"{folder}/{name}: number not painted ({why}), copied unchanged.");
                    }
                }
                else File.Copy(f, dest, overwrite: true);
            }
            WriteUiSkin(target, e);
            created.Add(folder);
        }
        ContentLibrary.Invalidate();
        return new Result(created, notes);
    }

    /// <summary>The race number in a livery's ui_skin.json (string or number), or null.</summary>
    private static string? ReadUiSkinNumber(string skinFolder)
    {
        try
        {
            var path = Path.Combine(skinFolder, "ui_skin.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("number", out var n)) return null;
            var s = n.ValueKind == JsonValueKind.Number ? n.GetRawText() : n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimStart('#');
        }
        catch { return null; }
    }

    private static bool IsPreview(string name) => name.Equals("preview.jpg", StringComparison.OrdinalIgnoreCase)
                                                 || name.Equals("livery.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>"hr_12_queen": generated, the number, the surname.</summary>
    public static string FolderName(RosterEntry e)
    {
        var surname = e.Driver.Split('/')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "driver";
        var slug = RosterIds.Slugify($"{e.Number} {surname}").Replace('-', '_');
        return Liveries.GeneratedPrefix + slug;
    }

    private static void WriteUiSkin(string folder, RosterEntry e)
    {
        var ui = new Dictionary<string, string>
        {
            ["skinname"] = $"{(e.Number != null ? "#" + e.Number + " " : "")}{e.Driver} (generated)",
            ["drivername"] = e.Driver,
            ["country"] = e.Nation ?? "",
            ["team"] = e.Team ?? "",
            ["number"] = e.Number ?? "",
        };
        File.WriteAllText(Path.Combine(folder, "ui_skin.json"), JsonSerializer.Serialize(ui, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Hues for n drivers, spread around the colour wheel and away from the base livery's own colour.</summary>
    private static double[] HuesFor(int n, double baseHue)
    {
        var hues = new double[n];
        for (var i = 0; i < n; i++)
        {
            // Golden-angle steps give well separated colours for any number of drivers.
            var h = baseHue + 60 + i * 137.508;
            hues[i] = h % 360;
        }
        return hues;
    }

    private static (double Hue, double ColouredShare) DominantHue(Image img)
    {
        var bins = new double[36];
        long coloured = 0, sampled = 0;
        var px = img.Pixels;
        for (var i = 0; i < px.Length; i += 7)
        {
            var p = px[i];
            if (p.a < 32) continue;
            sampled++;
            Rgb2Hsv(p.r, p.g, p.b, out var h, out var s, out var v);
            if (s < 0.25 || v < 0.12) continue;
            coloured++;
            bins[(int)(h / 10) % 36] += s * v;
        }
        var best = Array.IndexOf(bins, bins.Max());
        return (best * 10 + 5, sampled == 0 ? 0 : (double)coloured / sampled);
    }

    // ------------------------------------------------------------------ colour

    internal static void Rgb2Hsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 1e-6) { h = 0; return; }
        if (max == rf) h = 60 * (((gf - bf) / d) % 6);
        else if (max == gf) h = 60 * ((bf - rf) / d + 2);
        else h = 60 * ((rf - gf) / d + 4);
        if (h < 0) h += 360;
    }

    internal static (byte R, byte G, byte B) Hsv2Rgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Recolours one pixel: coloured paint is turned to the new hue; on a neutral livery, light parts are painted.</summary>
    internal static ColorRgba32 Recolor(ColorRgba32 p, double shift, double targetHue, bool neutralBase)
    {
        Rgb2Hsv(p.r, p.g, p.b, out var h, out var s, out var v);
        if (!neutralBase)
        {
            if (s < 0.25 || v < 0.12) return p;
            var (r, g, b) = Hsv2Rgb(h + shift, s, v);
            return new ColorRgba32(r, g, b, p.a);
        }
        if (s < 0.15 && v > 0.55)
        {
            var (r, g, b) = Hsv2Rgb(targetHue, 0.75, v * 0.92);
            return new ColorRgba32(r, g, b, p.a);
        }
        return p;
    }

    // ------------------------------------------------------------------ images (DDS via BCnEncoder, PNG/JPG via WPF)

    private sealed class Image
    {
        public required ColorRgba32[] Pixels { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public CompressionFormat? DdsFormat { get; init; }
        public bool HadMips { get; init; }

        public static Image? Load(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds")
            {
                DdsFile dds;
                using (var fs = File.OpenRead(path)) dds = DdsFile.Load(fs);
                var dec = new BcDecoder();
                var fmt = dec.GetFormat(dds);
                if (!Encodable(fmt)) return null;
                var img = dec.Decode2D(dds);
                var pixels = new ColorRgba32[img.Width * img.Height];
                img.Span.CopyTo(pixels);
                return new Image { Pixels = pixels, Width = img.Width, Height = img.Height, DdsFormat = fmt, HadMips = dds.header.dwMipMapCount > 1 };
            }
            if (ext is ".png" or ".jpg" or ".jpeg")
            {
                BitmapSource src;
                using (var fs = File.OpenRead(path))
                    src = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                var w = bgra.PixelWidth; var h = bgra.PixelHeight;
                var raw = new byte[w * h * 4];
                bgra.CopyPixels(raw, w * 4, 0);
                var pixels = new ColorRgba32[w * h];
                for (int i = 0, j = 0; i < pixels.Length; i++, j += 4) pixels[i] = new ColorRgba32(raw[j + 2], raw[j + 1], raw[j], raw[j + 3]);
                return new Image { Pixels = pixels, Width = w, Height = h };
            }
            return null;
        }

        private static bool Encodable(CompressionFormat f) => f is CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc2
            or CompressionFormat.Bc3 or CompressionFormat.Bc7 or CompressionFormat.Rgba or CompressionFormat.Bgra or CompressionFormat.Rgb;

        public void SaveRecolored(string path, double baseHue, double targetHue, bool neutralBase)
        {
            var shift = targetHue - baseHue;
            var px = new ColorRgba32[Pixels.Length];
            Parallel.For(0, Pixels.Length / 4096 + 1, chunk =>
            {
                var end = Math.Min(Pixels.Length, (chunk + 1) * 4096);
                for (var i = chunk * 4096; i < end; i++) px[i] = Recolor(Pixels[i], shift, targetHue, neutralBase);
            });
            Save(path, px);
        }

        /// <summary>Writes pixels of this image's size in its own format (same DDS compression and mipmaps).</summary>
        public void Save(string path, ColorRgba32[] px)
        {
            if (DdsFormat is { } fmt)
            {
                var enc = new BcEncoder(fmt);
                enc.OutputOptions.GenerateMipMaps = HadMips;
                enc.OutputOptions.Quality = CompressionQuality.Fast;
                enc.OutputOptions.FileFormat = OutputFileFormat.Dds;
                var dds = enc.EncodeToDds(new ReadOnlyMemory2D<ColorRgba32>(px, Height, Width));
                using var o = File.Create(path);
                dds.Write(o);
                return;
            }

            var raw = new byte[px.Length * 4];
            for (int i = 0, j = 0; i < px.Length; i++, j += 4) { raw[j] = px[i].b; raw[j + 1] = px[i].g; raw[j + 2] = px[i].r; raw[j + 3] = px[i].a; }
            var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, raw, Width * 4);
            BitmapEncoder encoder = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
    }
}
