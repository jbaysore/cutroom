using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cutroom.Tests;

/// <summary>
/// Compares a rendered BGRA frame with a reference PNG in tests/Cutroom.Tests/golden, with a perceptual tolerance so
/// driver-level rounding passes but a changed look fails. Set CUTROOM_UPDATE_GOLDEN=1 to write the references instead.
/// On a mismatch, the actual frame and a diff image go to artifacts/golden.
/// </summary>
internal static class GoldenImage
{
    const int ChannelTolerance = 8;
    const double MaxOffPixels = .005, MaxMeanDiff = 2;

    static string Folder([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "golden");

    public static void Assert(string name, byte[] bgra, int width, int height)
    {
        var path = Path.Combine(Folder(), name + ".png");
        if (Environment.GetEnvironmentVariable("CUTROOM_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Folder());
            Save(path, bgra, width, height);
            return;
        }
        Xunit.Assert.True(File.Exists(path), $"No golden image {path}; run the tests once with CUTROOM_UPDATE_GOLDEN=1 and review it.");
        var (expected, w, h) = Load(path);
        Xunit.Assert.True(w == width && h == height, $"{name}: size {width}x{height}, golden {w}x{h}");

        int off = 0;
        long total = 0;
        var diff = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int worst = 0;
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(bgra[i + c] - expected[i + c]);
                total += d;
                worst = Math.Max(worst, d);
            }
            if (worst > ChannelTolerance) off++;
            byte v = (byte)Math.Min(255, worst * 4);
            (diff[i], diff[i + 1], diff[i + 2], diff[i + 3]) = (v, v, v, 255);
        }
        int pixels = width * height;
        double mean = total / (pixels * 3.0), offShare = off / (double)pixels;
        if (offShare <= MaxOffPixels && mean < MaxMeanDiff) return;

        var artifacts = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Folder()))!, "..", "artifacts", "golden");
        Directory.CreateDirectory(artifacts);
        Save(Path.Combine(artifacts, name + ".actual.png"), bgra, width, height);
        Save(Path.Combine(artifacts, name + ".diff.png"), diff, width, height);
        Xunit.Assert.Fail($"{name} differs from its golden image: {offShare:P2} of pixels off by more than {ChannelTolerance}, mean difference {mean:0.00}. " +
            $"See {Path.GetFullPath(artifacts)}.");
    }

    static void Save(string path, byte[] bgra, int width, int height)
    {
        var opaque = (byte[])bgra.Clone();
        for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, opaque, width * 4)));
        using var file = File.Create(path);
        png.Save(file);
    }

    static (byte[] Pixels, int Width, int Height) Load(string path)
    {
        using var file = File.OpenRead(path);
        var frame = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }
}
