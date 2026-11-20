using System.Text.Json;
using Cutroom.Core;
using Cutroom.Engine;
using Cutroom.Library;

namespace Cutroom.Tests;

/// <summary>Tests that point the static Tools.CacheRoot at their own folder must not run in parallel.</summary>
[CollectionDefinition(Name)]
public sealed class SharedCache { public const string Name = "Shared Tools.CacheRoot"; }

[Collection(SharedCache.Name)]
public class ExportTests
{
    [Fact]
    public void PreflightFindsPartialGapsAtOutputFramesAcrossScriptSlots()
    {
        var p = Project.CreateDefault("Coverage");
        long frame = Time.Frame;
        p.Slots.Add(new Slot { Start = 0, End = 10 * frame });
        p.Slots.Add(new Slot { Start = 10 * frame, End = 15 * frame });
        var footage = p.Track(TrackRole.Footage);
        footage.Clips.Add(new Clip { Start = 0, Duration = 3 * frame });
        footage.Clips.Add(new Clip { Start = 2 * frame, Duration = 3 * frame - 1 });
        footage.Clips.Add(new Clip { Start = 5 * frame, Duration = 4 * frame });
        var cards = p.Track(TrackRole.Cards);
        cards.Clips.Add(new Clip { Start = 9 * frame, Duration = 2 * frame, Card = new CardData() });
        p.Track(TrackRole.Narration).Clips.Add(new Clip { Duration = 15 * frame });

        var warnings = Exporter.PreFlight(p, new AppSettings(), out var firstGap);
        Assert.Equal(11 * frame, firstGap);
        Assert.Contains(warnings, message => message.StartsWith("1 visual gap "));

        cards.Muted = true;
        Exporter.PreFlight(p, new AppSettings(), out firstGap);
        Assert.Equal(9 * frame, firstGap);

        footage.Muted = true;
        Exporter.PreFlight(p, new AppSettings(), out firstGap);
        Assert.Equal(0L, firstGap);
    }

    [Fact]
    public async Task ExportsH264At60fpsWithLoudnessTarget()
    {
        var dir = Directory.CreateTempSubdirectory("cutroom-export-test").FullName;
        Tools.CacheRoot = Path.Combine(dir, "cache");
        var video = Path.Combine(dir, "game.mp4");
        var tone = Path.Combine(dir, "voice.wav");
        Media.Run("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=3", "-f", "lavfi", "-i", "sine=f=220:d=3", "-c:v", "mpeg4", "-q:v", "3", "-c:a", "aac", "-shortest", video);
        Media.Run("-f", "lavfi", "-i", "sine=f=440:d=3", "-af", "volume=0.05", tone);

        var p = Project.CreateDefault("Export");
        var v = await MediaProbe.Probe(video);
        var a = await MediaProbe.Probe(tone);
        p.Media.AddRange([v, a]);
        p.Track(TrackRole.Footage).Clips.Add(new Clip { MediaId = v.Id, Start = 0, Duration = Time.FromSeconds(3) });
        p.Track(TrackRole.Game).Clips.Add(new Clip { MediaId = v.Id, Start = 0, Duration = Time.FromSeconds(3) });
        p.Track(TrackRole.Narration).Clips.Add(new Clip { MediaId = a.Id, Start = 0, Duration = Time.FromSeconds(3) });
        var paths = new Dictionary<string, string>();
        foreach (var m in p.Media) paths[m.Id] = await Derivatives.EnsureAudio(m.Path, m.Fingerprint, Time.ToSeconds(m.Duration), null, CancellationToken.None);

        var output = Path.Combine(dir, "out.mp4");
        Assert.Empty(Exporter.PreFlight(p, new AppSettings()));
        var result = await Exporter.Run(p, new AppSettings(), output, m => paths.GetValueOrDefault(m.Id), null, CancellationToken.None, 640, 360);

        var json = await Tools.Run(Tools.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_name,codec_type,avg_frame_rate,width:format=duration", "-of", "json", output]);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var vs = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.Equal("h264", vs.GetProperty("codec_name").GetString());
        Assert.Equal("60/1", vs.GetProperty("avg_frame_rate").GetString());
        Assert.Contains(streams, s => s.GetProperty("codec_name").GetString() == "aac");
        double seconds = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 2.9, 3.1);
        Assert.InRange(await Exporter.MeasureFile(output), -15.5, -12.5);
        Assert.StartsWith("h264_", result.Encoder);
    }
}
