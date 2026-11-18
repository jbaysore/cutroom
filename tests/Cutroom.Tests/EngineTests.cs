using System.Diagnostics;
using Cutroom.Core;
using Cutroom.Engine;

namespace Cutroom.Tests;

internal static class Sta
{
    /// <summary>WPF text rasterization needs an STA thread; xUnit runs tests on MTA threads.</summary>
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}

internal static class Media
{
    static readonly Lock gate = new();

    public static string Ffmpeg => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    public static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(err);
    }

    /// <summary>One second of red then one second of blue at 60 fps, 320x180.</summary>
    public static string RedThenBlue()
    {
        lock (gate)
        {
            var path = Path.Combine(Path.GetTempPath(), "cutroom-red-blue.mp4");
            if (!File.Exists(path))
                Run("-f", "lavfi", "-i", "color=c=red:s=320x180:r=60:d=1", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=60:d=1",
                    "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-c:v", "mpeg4", "-q:v", "2", "-g", "30", path);
            return path;
        }
    }
}

public class EngineTests
{
    static (byte B, byte G, byte R, byte A) Pixel(byte[] frame, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]);
    }

    [Fact]
    public void ColorLayersCompositeWithAlpha()
    {
        using var e = new NativeEngine(320, 180);
        e.Begin(0, 0, 0);
        var full = Layer.Full(LayerKind.Color);
        (full.R, full.G, full.B, full.A) = (1, 0, 0, 1);
        e.Draw(full);
        var half = Layer.Full(LayerKind.Color);
        (half.R, half.G, half.B, half.A) = (0, 0, 1, .5f);
        half.W = .5f; half.Cx = .75f;
        e.Draw(half);
        var frame = new byte[320 * 180 * 4];
        e.Readback(frame);
        var left = Pixel(frame, 320, 40, 90);
        var right = Pixel(frame, 320, 280, 90);
        Assert.True(left.R > 250 && left.B < 5);
        Assert.InRange(right.R, 120, 135);
        Assert.InRange(right.B, 120, 135);
    }

    [Fact]
    public void BlurSoftensEdgesAndDims()
    {
        using var e = new NativeEngine(320, 180);
        e.Begin(0, 0, 0);
        var white = Layer.Full(LayerKind.Color);
        (white.R, white.G, white.B, white.A) = (1, 1, 1, 1);
        white.W = .5f; white.Cx = .25f;
        e.Draw(white);
        var blur = Layer.Full(LayerKind.Blur);
        blur.Blur = 1; blur.Dim = .5f;
        e.Draw(blur);
        var frame = new byte[320 * 180 * 4];
        e.Readback(frame);
        var inside = Pixel(frame, 320, 40, 90);
        var edge = Pixel(frame, 320, 166, 90);
        Assert.InRange(inside.R, 100, 140);
        Assert.InRange(edge.R, 10, 110);
    }

    [Fact]
    public void PaperBackgroundIsLight()
    {
        using var e = new NativeEngine(320, 180);
        e.Begin(0, 0, 0);
        var paper = Layer.Full(LayerKind.Paper);
        (paper.R, paper.G, paper.B) = (.5f, .5f, .5f);
        e.Draw(paper);
        var frame = new byte[320 * 180 * 4];
        e.Readback(frame);
        var center = Pixel(frame, 320, 160, 90);
        Assert.True(center.R > 150 && center.G > 150, $"{center}");
    }

    [Fact]
    public void DecodesSeeksForwardAndBack()
    {
        var video = Media.RedThenBlue();
        using var e = new NativeEngine(320, 180);
        int m = e.Open(video);
        var info = e.Info(m);
        Assert.Equal(320, info.Width);
        Assert.InRange(info.Fps, 59.9, 60.1);
        Assert.InRange(info.Duration, 1.9, 2.1);
        var frame = new byte[320 * 180 * 4];
        foreach (var (t, red) in new[] { (.2, true), (.5, true), (1.5, false), (1.6, false), (.1, true), (1.99, false) })
        {
            e.Begin();
            var l = Layer.Full(LayerKind.Video);
            l.Media = m;
            l.SourceSeconds = t;
            e.Draw(l);
            e.Readback(frame);
            var px = Pixel(frame, 320, 160, 90);
            Assert.True(red ? px.R > 200 && px.B < 60 : px.B > 200 && px.R < 60, $"t={t} px={px}");
        }
    }

    [Fact]
    public void ComposerDrawsCardsAndSubtitles()
    {
        Sta.Run(() =>
        {
            TextRaster.SetFontsFolder(Path.Combine(AppContext.BaseDirectory, "fonts"));
            var p = Fixtures.Narrated("Sleep cancels ultimates. Hold it.", "Sleep cancels ultimates.", "Hold it.");
            p.Captions = Subtitles.Chunk(Derive.TimelineWords(p));
            p.SubtitleRegions.Add(new SubtitleRegion { Start = 0, End = p.Duration, ShowOverCards = true });
            var card = new Clip { Kind = ClipKind.Card, Start = 0, Duration = Time.FromSeconds(2), Card = Cards.NewCard(CardTemplateKind.Background, "Sleep cancels ultimates") };
            p.Track(TrackRole.Cards).Clips.Add(card);
            using var e = new NativeEngine(640, 360);
            using var c = new Composer(e, new AppSettings());
            c.Prepare(p, 1);
            c.Draw(p, Time.FromSeconds(1));
            var frame = new byte[640 * 360 * 4];
            e.Readback(frame);
            // Paper background is light; card text is dark ink in the middle area.
            int dark = 0, light = 0;
            for (int y = 100; y < 260; y += 2)
                for (int x = 120; x < 520; x += 2)
                {
                    var px = Pixel(frame, 640, x, y);
                    if (px.R < 90) dark++; else if (px.R > 170) light++;
                }
            Assert.True(light > 1000, $"light={light}");
            Assert.True(dark > 50, $"dark={dark}");
        });
    }

    [Fact]
    public void CardLayoutFitsLongTextAndFlagsOverflow()
    {
        Sta.Run(() =>
        {
            TextRaster.SetFontsFolder(Path.Combine(AppContext.BaseDirectory, "fonts"));
            var template = CardTemplate.Defaults()[0];
            var shortCard = Cards.NewCard(CardTemplateKind.Blur, "Sleep cancels ultimates");
            var shortLayout = CardLayout.Compute(shortCard, template, 1920, 1080);
            Assert.Equal(64, shortLayout.FontSize);
            Assert.Equal(System.Windows.TextAlignment.Center, shortLayout.Lines[0].Align);

            var longText = string.Join(" ", Enumerable.Repeat("Sleep Dart puts an enemy to sleep for several seconds and cancels most ultimates.", 6));
            var longLayout = CardLayout.Compute(Cards.NewCard(CardTemplateKind.Blur, longText), template, 1920, 1080);
            Assert.True(longLayout.FontSize < 64);
            Assert.All(longLayout.Lines, l => Assert.True(l.Width <= 1920 * .7 + 1));

            var huge = CardLayout.Compute(Cards.NewCard(CardTemplateKind.Blur, string.Join(" ", Enumerable.Repeat(longText, 4))), template, 1920, 1080);
            Assert.True(huge.Overflow);
        });
    }
}
