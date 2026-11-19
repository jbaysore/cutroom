using System.Globalization;
using System.Text.Json;
using Cutroom.Core;
using Cutroom.Engine;
using Cutroom.Library;
using Cutroom.Speech;

namespace Cutroom.Tests;

/// <summary>
/// The whole v1 flow without the UI: TTS narration with retakes and a script, cleanup, captions, slots filled with
/// 1080p60 and 30 fps gameplay, a blur card, a zoom, music, then a full 1080p60 export checked with ffprobe, loudness
/// and a re-transcription of the export. Needs the speech models; run with scripts/test-e2e.ps1.
/// </summary>
[Trait("Category", "Models")]
public class EndToEndTests
{
    const string ScriptText =
        "Sleep dart is the most important cooldown in the game. The best way to use it is on a Reinhardt. " +
        "Sleep cancels almost every ultimate in the game. Practice it in the range before you queue.";

    [Fact]
    public async Task RecordCleanEditExport()
    {
        if (!Models.Ready(Models.WhisperTurbo) || !Models.Ready(Models.Wav2Vec2)) return;
        var dir = Directory.CreateTempSubdirectory("cutroom-e2e").FullName;
        Tools.CacheRoot = Path.Combine(dir, "cache");

        // Narration as Joshua records it: flubbed lines re-said, long pauses.
        var wav = Path.Combine(dir, "narration.wav");
        Sta.Run(() =>
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            synth.SetOutputToWaveFile(wav);
            var b = new System.Speech.Synthesis.PromptBuilder();
            b.AppendText("Sleep dart is the most important cooldown in the game.");
            b.AppendBreak(TimeSpan.FromSeconds(1.5));
            b.AppendText("The best way.");
            b.AppendBreak(TimeSpan.FromSeconds(.6));
            b.AppendText("The best way to use it is on a Reinhardt.");
            b.AppendBreak(TimeSpan.FromSeconds(2));
            b.AppendText("Sleep cancels almost every ultimate.");
            b.AppendBreak(TimeSpan.FromSeconds(.8));
            b.AppendText("Sleep cancels almost every ultimate in the game.");
            b.AppendBreak(TimeSpan.FromSeconds(1.2));
            b.AppendText("Practice it in the range before you queue.");
            synth.Speak(b);
        });
        var game60 = Path.Combine(dir, "game60.mp4");
        var game30 = Path.Combine(dir, "game30.mp4");
        var music = Path.Combine(dir, "music.wav");
        Media.Run("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=12", "-f", "lavfi", "-i", "sine=f=330:d=12", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-shortest", game60);
        Media.Run("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=30:d=12", "-f", "lavfi", "-i", "sine=f=550:d=12", "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-shortest", game30);
        Media.Run("-f", "lavfi", "-i", "aevalsrc=0.2*sin(2*PI*220*t)+0.15*sin(2*PI*277*t)+0.12*sin(2*PI*330*t):s=48000:d=40", "-ac", "2", music);

        var settings = new AppSettings { Denoiser = Denoiser.Off };
        var p = Project.CreateDefault("E2E");
        p.Script = Script.FromText(ScriptText);
        var narration = await MediaProbe.Probe(wav);
        p.Media.Add(narration);
        p.Narration = new Narration { MediaId = narration.Id, SourceDuration = narration.Duration, Settings = new CleanupSettings { Denoiser = Denoiser.Off } };
        p.Track(TrackRole.Narration).Clips.Add(new Clip { MediaId = narration.Id, Start = 0, Duration = narration.Duration, Label = "Narration" });

        // Clean, exactly as Session.ApplyCleanup does.
        var r = await CleanupEngine.Run(narration, p.Script, [], p.Narration.Settings, settings.WhisperModel, null, CancellationToken.None);
        var n = p.Narration;
        (n.CleanPath, n.GainDb, n.Words, n.Decisions, n.SourceDuration, n.Processed) = (r.CleanPath, r.GainDb, r.Words, r.Decisions, r.Duration, true);
        Derive.RebuildNarration(p);
        Derive.GenerateSlots(p);
        p.Captions = Subtitles.Chunk(Derive.TimelineWords(p), new Subtitles.ChunkOptions(MaxChars: settings.Preset(SubtitlePreset.DefaultId).MaxChars), p.Script.Vocabulary());
        Derive.EnsureIntroRegion(p, settings.SubtitlePresets[0].Id);
        Derive.RefreshIntroRegion(p);
        Assert.True(p.Slots.Count >= 4, $"expected a slot per sentence, got {p.Slots.Count}");
        Assert.NotEmpty(p.Captions);
        Assert.True(Time.ToSeconds(p.Duration) < Time.ToSeconds(narration.Duration) - 3, "cleanup should remove the retakes and long pauses");

        // Fill slots alternately with 60 and 30 fps gameplay; blur card on slot 2, zoom on slot 3, music under everything.
        var v60 = await MediaProbe.Probe(game60);
        var v30 = await MediaProbe.Probe(game30);
        var bed = await MediaProbe.Probe(music);
        p.Media.AddRange([v60, v30, bed]);
        for (int i = 0; i < p.Slots.Count; i++)
        {
            var slot = p.Slots[i];
            var m = i % 2 == 0 ? v60 : v30;
            var clip = new Clip { MediaId = m.Id, Start = slot.Start, Duration = Time.SnapToFrame(slot.End - slot.Start), SourceIn = Time.FromSeconds(i), SlotId = slot.Id };
            var audio = new Clip { MediaId = m.Id, Start = clip.Start, Duration = clip.Duration, SourceIn = clip.SourceIn, SlotId = slot.Id };
            clip.LinkId = audio.LinkId = Ids.New();
            TimelineOps.Overwrite(p, p.Track(TrackRole.Footage), clip);
            TimelineOps.Overwrite(p, p.Track(TrackRole.Game), audio);
        }
        var cardSlot = p.Slots[1];
        var card = new Clip { Kind = ClipKind.Card, Start = cardSlot.Start, Duration = cardSlot.End - cardSlot.Start, SlotId = cardSlot.Id, Card = Cards.NewCard(CardTemplateKind.Blur, cardSlot.Text, "blur") };
        TimelineOps.Overwrite(p, p.Track(TrackRole.Cards), card);
        Cards.AutoTime(p, card);
        var zoomed = p.Track(TrackRole.Footage).Clips.First(c => c.SlotId == p.Slots[2].Id);
        zoomed.Effects.Add(new Effect { Kind = EffectKind.ZoomToBox, Offset = 0, Duration = Math.Min(Time.FromSeconds(2), zoomed.Duration), Easing = Easing.Smooth, BoxX = .3, BoxY = .3, BoxW = .4, BoxH = .4 });
        p.Track(TrackRole.Music).Clips.Add(new Clip { MediaId = bed.Id, Start = 0, Duration = Time.SnapToFrame(p.Duration) });
        Assert.Empty(Exporter.PreFlight(p, settings).Where(x => !x.Contains("no footage or card")));

        // Audio paths as the app resolves them: narration plays its cleaned derivative.
        var audioPaths = new Dictionary<string, string>();
        foreach (var m in p.Media)
        {
            var source = m.Id == narration.Id ? r.CleanPath : m.Path;
            audioPaths[m.Id] = await Derivatives.EnsureAudio(source, Tools.Fingerprint(source), 0, null, CancellationToken.None);
        }
        var output = Path.Combine(dir, "e2e.mp4");
        var result = await Exporter.Run(p, settings, output, m => audioPaths.GetValueOrDefault(m.Id), null, CancellationToken.None);

        // Container: 1080p60 H.264 + AAC, video and audio the same length as the timeline.
        var json = await Tools.Run(Tools.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_name,codec_type,avg_frame_rate,width,height,duration:format=duration", "-of", "json", output]);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var vs = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
        var aus = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal("h264", vs.GetProperty("codec_name").GetString());
        Assert.Equal(1920, vs.GetProperty("width").GetInt32());
        Assert.Equal(1080, vs.GetProperty("height").GetInt32());
        Assert.Equal("60/1", vs.GetProperty("avg_frame_rate").GetString());
        Assert.Equal("aac", aus.GetProperty("codec_name").GetString());
        double Seconds(JsonElement e) => double.Parse(e.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(Seconds(vs), Time.ToSeconds(p.Duration) - .1, Time.ToSeconds(p.Duration) + .1);
        Assert.InRange(Math.Abs(Seconds(vs) - Seconds(aus)), 0, .1);
        Assert.InRange(await Exporter.MeasureFile(output), -15, -13);
        Assert.NotNull(result.SrtPath);
        Assert.True(File.Exists(result.SrtPath));

        // Listen back: the export says the script once, in order, with no retake left in.
        var check16k = Path.Combine(dir, "check16k.wav");
        Media.Run("-i", output, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", check16k);
        var heard = await Transcriber.Transcribe(check16k, Models.PathOf(Models.WhisperTurbo), p.Script.Vocabulary(), null, CancellationToken.None);
        var tokens = Normalizer.Tokens(string.Join(' ', heard.Select(w => w.Text)));
        var expected = p.Script.Sentences.SelectMany(s => s.Tokens).ToList();
        Assert.True(EditDistance(tokens, expected) <= 2, $"heard: {string.Join(' ', tokens)} | A1: " + string.Join("; ", p.Track(TrackRole.Narration).Clips.Select(c => $"{Time.ToSeconds(c.Start):0.00}+{Time.ToSeconds(c.Duration):0.00} src {Time.ToSeconds(c.SourceIn):0.00}")) + " | words: " + string.Join(" ", Derive.TimelineWords(p).TakeLast(8).Select(w => $"{w.Word.Text}@{Time.ToSeconds(w.TimeRange.Start):0.00}-{Time.ToSeconds(w.TimeRange.End):0.00}(src {Time.ToSeconds(w.Word.Start):0.00})")) + $" | dur {Time.ToSeconds(p.Duration):0.00}");
        Assert.Equal(1, tokens.Count(t => t == "best"));
        Assert.Equal(1, tokens.Count(t => t == "cancels"));
    }

    static int EditDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var d = new int[a.Count + 1, b.Count + 1];
        for (int i = 0; i <= a.Count; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Count; j++) d[0, j] = j;
        for (int i = 1; i <= a.Count; i++)
            for (int j = 1; j <= b.Count; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Count, b.Count];
    }
}
