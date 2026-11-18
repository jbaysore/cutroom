using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cutroom.Core;

namespace Cutroom.Engine;

public sealed record Bitmap(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Rasterizes text and vector shapes with WPF into premultiplied BGRA bitmaps that the native compositor draws.
/// Must run on an STA thread.
/// </summary>
public static class TextRaster
{
    static string fontsFolder = Path.Combine(AppContext.BaseDirectory, "fonts");
    public static void SetFontsFolder(string folder) => fontsFolder = folder;

    public static Typeface Typeface(string name)
    {
        var weight = FontWeights.Normal;
        var family = name;
        foreach (var (suffix, w) in new[] { ("ExtraBold", FontWeights.ExtraBold), ("SemiBold", FontWeights.SemiBold), ("Black", FontWeights.Black), ("Bold", FontWeights.Bold), ("Medium", FontWeights.Medium) })
        {
            if (name.EndsWith(" " + suffix, StringComparison.OrdinalIgnoreCase)) { weight = w; family = name[..^(suffix.Length + 1)]; break; }
        }
        FontFamily ff = family.Equals("Montserrat", StringComparison.OrdinalIgnoreCase) && Directory.Exists(fontsFolder)
            ? new FontFamily(new Uri(Path.GetFullPath(fontsFolder) + Path.DirectorySeparatorChar), "./#Montserrat")
            : new FontFamily(family);
        return new Typeface(ff, FontStyles.Normal, weight, FontStretches.Normal);
    }

    public static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Colors.White; }
    }

    static FormattedText Format(string text, Typeface face, double size, Brush brush, double maxWidth = 0, TextAlignment align = TextAlignment.Left, double lineHeight = 0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0)
        { TextAlignment = align };
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        if (lineHeight > 0) ft.LineHeight = lineHeight;
        return ft;
    }

    static Bitmap Render(int width, int height, Action<DrawingContext> draw)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var pixels = new byte[width * height * 4];
        rtb.CopyPixels(pixels, width * 4, 0);
        return new Bitmap(width, height, pixels);
    }

    /// <summary>A subtitle caption: fill, thick outline and soft shadow, one line.</summary>
    public static Bitmap Caption(string text, SubtitlePreset preset)
    {
        var face = Typeface(preset.FontFamily);
        var fill = new SolidColorBrush(ParseColor(preset.Fill));
        var ft = Format(text, face, preset.Size, fill);
        var geometry = ft.BuildGeometry(new Point(0, 0));
        double pad = preset.OutlineWidth + 14;
        int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace + pad * 2), h = (int)Math.Ceiling(ft.Height + pad * 2);
        return Render(w, h, dc =>
        {
            dc.PushTransform(new TranslateTransform(pad, pad));
            if (preset.ShadowOpacity > 0)
            {
                var shadow = new SolidColorBrush(Color.FromArgb((byte)(preset.ShadowOpacity * 255), 0, 0, 0));
                dc.PushTransform(new TranslateTransform(0, 5));
                dc.DrawGeometry(shadow, new Pen(shadow, preset.OutlineWidth * 2 + 4) { LineJoin = PenLineJoin.Round }, geometry);
                dc.Pop();
            }
            if (preset.OutlineWidth > 0)
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(ParseColor(preset.Outline)), preset.OutlineWidth * 2) { LineJoin = PenLineJoin.Round }, geometry);
            dc.DrawGeometry(fill, null, geometry);
            dc.Pop();
        });
    }

    /// <summary>A wrapped block of text with a soft shadow, used for card lines and headings.</summary>
    public static Bitmap Block(string text, string font, double size, string color, double maxWidth, TextAlignment align, double shadowOpacity = .35, double lineHeight = 0)
    {
        var face = Typeface(font);
        var brush = new SolidColorBrush(ParseColor(color));
        var ft = Format(text, face, size, brush, maxWidth, align, lineHeight);
        const double pad = 12;
        int w = (int)Math.Ceiling(maxWidth + pad * 2), h = (int)Math.Ceiling(ft.Height + pad * 2);
        return Render(w, h, dc =>
        {
            if (shadowOpacity > 0)
            {
                var shadow = Format(text, face, size, new SolidColorBrush(Color.FromArgb((byte)(shadowOpacity * 255), 0, 0, 0)), maxWidth, align, lineHeight);
                dc.DrawText(shadow, new Point(pad + 2, pad + 3));
            }
            dc.DrawText(ft, new Point(pad, pad));
        });
    }

    /// <summary>Measures wrapped text height in pixels.</summary>
    public static double Measure(string text, string font, double size, double maxWidth, double lineHeight = 0) =>
        Format(text, Typeface(font), size, Brushes.White, maxWidth, TextAlignment.Left, lineHeight).Height;

    public static double MeasureWidth(string text, string font, double size) =>
        Format(text, Typeface(font), size, Brushes.White).WidthIncludingTrailingWhitespace;

    /// <summary>An outline shape drawn on with <paramref name="progress"/> 0..1, sized to its bounds in pixels.</summary>
    public static Bitmap Shape(ShapeKind kind, int width, int height, double progress, string color, double thickness,
        double ax = 0, double ay = 0, double bx = 1, double by = 1)
    {
        var brush = new SolidColorBrush(ParseColor(color));
        double pad = thickness * 2 + 6;
        int w = width + (int)(pad * 2), h = height + (int)(pad * 2);
        return Render(w, h, dc =>
        {
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), thickness + 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            Geometry geometry = kind switch
            {
                ShapeKind.Ellipse => new EllipseGeometry(new Rect(pad, pad, width, height)),
                ShapeKind.Box => new RectangleGeometry(new Rect(pad, pad, width, height), thickness, thickness),
                _ => Arrow(new Point(pad + ax * width, pad + ay * height), new Point(pad + bx * width, pad + by * height), thickness),
            };
            var flat = geometry.GetFlattenedPathGeometry(.5, ToleranceType.Absolute);
            var partial = Partial(flat, Math.Clamp(progress, 0, 1));
            dc.PushTransform(new TranslateTransform(1.5, 2.5));
            dc.DrawGeometry(null, shadowPen, partial);
            dc.Pop();
            dc.DrawGeometry(null, pen, partial);
        });
    }

    static Geometry Arrow(Point a, Point b, double thickness)
    {
        var v = b - a;
        if (v.Length < 1) v = new Vector(1, 0);
        v.Normalize();
        var n = new Vector(-v.Y, v.X);
        double head = thickness * 4.5;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.LineTo(b, true, true);
            ctx.LineTo(b - v * head + n * head * .6, true, true);
            ctx.BeginFigure(b, false, false);
            ctx.LineTo(b - v * head - n * head * .6, true, true);
        }
        return g;
    }

    /// <summary>The first <paramref name="fraction"/> of a flattened path's length (draw-on animation).</summary>
    static Geometry Partial(PathGeometry flat, double fraction)
    {
        if (fraction >= 1) return flat;
        double total = 0;
        var figures = new List<List<Point>>();
        foreach (var fig in flat.Figures)
        {
            var pts = new List<Point> { fig.StartPoint };
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pl) pts.AddRange(pl.Points);
                else if (seg is LineSegment ls) pts.Add(ls.Point);
            }
            if (fig.IsClosed) pts.Add(fig.StartPoint);
            figures.Add(pts);
            for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).Length;
        }
        double remaining = total * fraction;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var pts in figures)
            {
                if (remaining <= 0 || pts.Count < 2) break;
                ctx.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Count && remaining > 0; i++)
                {
                    double len = (pts[i] - pts[i - 1]).Length;
                    if (len <= remaining) { ctx.LineTo(pts[i], true, true); remaining -= len; }
                    else { ctx.LineTo(pts[i - 1] + (pts[i] - pts[i - 1]) * (remaining / len), true, true); remaining = 0; }
                }
            }
        }
        return g;
    }

    /// <summary>Decodes an image file to premultiplied BGRA, downscaled to fit within maxSize.</summary>
    public static Bitmap Image(string path, int maxSize = 2048)
    {
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        double scale = Math.Min(1, maxSize / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        return new Bitmap(w, h, pixels);
    }
}
