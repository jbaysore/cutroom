using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cutroom.Core;
using Cutroom.Engine;
using Cutroom.Library;
using Cutroom.Speech;

namespace Cutroom.Tests;

/// <summary>Writes benchmark results to artifacts/bench.md; scripts/bench.ps1 prints the file.</summary>
internal static class Bench
{
    static readonly Lock gate = new();

    public static string Artifacts([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "artifacts"));

    /// <summary>Records a measurement and fails the test if it is over budget.</summary>
    public static void Record(string name, double seconds, double budget, string note = "") =>
        Assert.True(Write(name, seconds, budget, note), $"{name}: {seconds:0.00} s, budget {budget} s");

    /// <summary>Records a measurement; returns whether it is within budget.</summary>
    public static bool Write(string name, double seconds, double budget, string note = "")
    {
        var line = $"| {name} | {seconds:0.00} s | {budget:0.##} s | {(seconds <= budget ? "ok" : "OVER")} | {note} |";
        Console.WriteLine(line);
        lock (gate)
        {
            Directory.CreateDirectory(Artifacts());
            File.AppendAllText(Path.Combine(Artifacts(), "bench.md"), line + Environment.NewLine);
        }
        return seconds <= budget;
    }

    /// <summary>A finished-looking 90-minute project: ~1100 script sentences, every slot filled, cards, captions everywhere.</summary>
    public static Project NinetyMinuteProject()
    {
        var rng = new Random(7);
        string[] vocab = ["sleep", "dart", "ana", "kiriko", "suzu", "cleanse", "ultimate", "cooldown", "reinhardt", "shield", "range", "angle",
            "pressure", "space", "tempo", "trade", "peel", "dive", "poke", "brawl", "flank", "high", "ground", "nade", "heal", "burst"];
        var sentences = Enumerable.Range(0, 1100)
            .Select(_ => string.Join(' ', Enumerable.Range(0, 14).Select(_ => vocab[rng.Next(vocab.Length)])) + ".")
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]).ToArray();
        var p = Fixtures.Narrated(string.Join(' ', sentences), sentences);
        var sources = Enumerable.Range(0, 20).Select(i => new MediaRef
        {
            Kind = MediaKind.Video, Path = $"match{i}.mp4", Duration = Time.FromSeconds(3 * 3600), Width = 1920, Height = 1080, Fps = 60, HasAudio = true,
        }).ToList();
        p.Media.AddRange(sources);
        for (int i = 0; i < p.Slots.Count; i++)
        {
            var slot = p.Slots[i];
            if (i % 10 == 5)
            {
                var card = new Clip { Kind = ClipKind.Card, Start = slot.Start, Duration = slot.End - slot.Start, SlotId = slot.Id, Card = Cards.NewCard(CardTemplateKind.Blur, slot.Text) };
                p.Track(TrackRole.Cards).Clips.Add(card);
            }
            var m = sources[i % sources.Count];
            var video = new Clip { MediaId = m.Id, Start = slot.Start, Duration = slot.End - slot.Start, SourceIn = Time.FromSeconds(rng.Next(10000)), SlotId = slot.Id };
            var audio = new Clip { MediaId = m.Id, Start = video.Start, Duration = video.Duration, SourceIn = video.SourceIn, SlotId = slot.Id };
            video.LinkId = audio.LinkId = Ids.New();
            p.Track(TrackRole.Footage).Clips.Add(video);
            p.Track(TrackRole.Game).Clips.Add(audio);
        }
        p.Captions = Subtitles.Chunk(Derive.TimelineWords(p));
        p.SubtitleRegions.Add(new SubtitleRegion { Start = 0, End = p.Duration });
        return p;
    }
}

/// <summary>An IProgress that reports on the caller's thread, so timings aren't delayed by a synchronization context.</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Performance budgets from IMPLEMENTATION_PLAN.md §6. Slow; run with scripts/bench.ps1.</summary>
[Trait("Category", "Bench")]
[Collection(SharedCache.Name)]
public class BenchTests
{
    [Fact]
    public void OpenNinetyMinuteProject()
    {
        var p = Bench.NinetyMinuteProject();
        Assert.InRange(Time.ToSeconds(p.Duration), 80 * 60, 100 * 60);
        Assert.True(p.Slots.Count >= 1000, $"expected a slot per sentence, got {p.Slots.Count}");
        var path = Path.Combine(Bench.Artifacts(), "bench", "bench-90min.cutroom.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProjectJson.Serialize(p));

        // What the editor computes before it can draw: the document, timeline words, caption timings and slot status.
        var sw = Stopwatch.StartNew();
        var doc = Document.Open(path);
        var words = Derive.TimelineWords(doc.Project);
        var timings = Subtitles.Timings(doc.Project, words);
        foreach (var slot in doc.Project.Slots) Derive.StatusOf(doc.Project, slot);
        sw.Stop();
        Assert.NotEmpty(timings);
        Bench.Record("Open a 90-minute project (data)", sw.Elapsed.TotalSeconds, 2,
            $"{doc.Project.Slots.Count} slots, {words.Count} words, {doc.Project.Captions.Count} captions, {new FileInfo(path).Length / 1024} KB");
    }

    [Fact]
    public async Task ScrubShowsAFrameQuickly()
    {
        var dir = Directory.CreateTempSubdirectory("cutroom-bench-scrub").FullName;
        Tools.CacheRoot = Path.Combine(dir, "cache");
        var source = Path.Combine(dir, "match.mp4");
        Media.Run("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=120", "-c:v", "mpeg4", "-q:v", "4", source);
        var m = await MediaProbe.Probe(source);
        await Derivatives.EnsureProxy(m, null, CancellationToken.None);
        var proxy = Derivatives.ProxyPath(m);

        var rng = new Random(3);
        var times = new List<double>();
        using var e = new NativeEngine(1920, 1080);
        int h = e.Open(proxy, 1080);
        var frame = new byte[1920 * 1080 * 4];
        for (int i = 0; i < 60; i++)
        {
            double t = rng.NextDouble() * 118;
            var sw = Stopwatch.StartNew();
            e.Begin();
            var l = Layer.Full(LayerKind.Video);
            l.Media = h;
            l.SourceSeconds = t;
            e.Draw(l);
            e.Readback(frame);
            times.Add(sw.Elapsed.TotalSeconds);
        }
        times.Sort();
        Bench.Record("Scrub to a random frame (95th percentile)", times[(int)(times.Count * .95)], .1,
            $"median {times[times.Count / 2] * 1000:0} ms, 540p proxy of 1080p60, includes GPU readback");
    }

    [Fact]
    public async Task ExportTwelveMinutes()
    {
        var dir = Directory.CreateTempSubdirectory("cutroom-bench-export").FullName;
        Tools.CacheRoot = Path.Combine(dir, "cache");
        var game = Path.Combine(dir, "game.mp4");
        var music = Path.Combine(dir, "music.wav");
        Media.Run("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=180", "-f", "lavfi", "-i", "sine=f=330:d=180", "-c:v", "mpeg4", "-q:v", "4", "-g", "60", "-c:a", "aac", "-shortest", game);
        Media.Run("-f", "lavfi", "-i", "aevalsrc=0.2*sin(2*PI*220*t)+0.15*sin(2*PI*277*t):s=48000:d=720", "-ac", "2", music);
        var settings = new AppSettings();
        var p = Project.CreateDefault("Bench export");
        var v = await MediaProbe.Probe(game);
        var bed = await MediaProbe.Probe(music);
        p.Media.AddRange([v, bed]);
        long total = Time.FromSeconds(720), step = Time.FromSeconds(4);
        for (long t = 0, i = 0; t < total; t += step, i++)
        {
            var src = Time.FromSeconds(i * 7 % 170);
            var clip = new Clip { MediaId = v.Id, Start = t, Duration = step, SourceIn = src };
            var audio = new Clip { MediaId = v.Id, Start = t, Duration = step, SourceIn = src };
            clip.LinkId = audio.LinkId = Ids.New();
            if (i % 5 == 2) clip.Effects.Add(new Effect { Kind = EffectKind.ZoomToBox, Duration = step, BoxX = .3, BoxY = .3, BoxW = .4, BoxH = .4 });
            p.Track(TrackRole.Footage).Clips.Add(clip);
            p.Track(TrackRole.Game).Clips.Add(audio);
            if (i % 8 == 4)
                p.Track(TrackRole.Cards).Clips.Add(new Clip { Kind = ClipKind.Card, Start = t, Duration = step,
                    Card = Cards.NewCard(CardTemplateKind.Blur, "Sleep Dart cancels almost every ultimate, so hold it for the one that wins the fight.") });
            p.Captions.Add(new Caption { Text = $"Caption number {i} over the gameplay", FixedStart = t, FixedEnd = t + step - Time.FromMs(200) });
        }
        p.Track(TrackRole.Music).Clips.Add(new Clip { MediaId = bed.Id, Start = 0, Duration = total });
        p.SubtitleRegions.Add(new SubtitleRegion { Start = 0, End = total });

        var audioPaths = new Dictionary<string, string>();
        foreach (var m in p.Media) audioPaths[m.Id] = await Derivatives.EnsureAudio(m.Path, Tools.Fingerprint(m.Path), 0, null, CancellationToken.None);
        var output = Path.Combine(dir, "bench.mp4");
        double renderStart = 0;
        var sw = Stopwatch.StartNew();
        var progress = new SyncProgress<ExportProgress>(x => { if (x.Stage == "Rendering" && renderStart == 0) renderStart = sw.Elapsed.TotalSeconds; });
        await Exporter.Run(p, settings, output, m => audioPaths.GetValueOrDefault(m.Id), progress, CancellationToken.None);
        sw.Stop();
        Assert.True(File.Exists(output));
        double seconds = sw.Elapsed.TotalSeconds;
        Bench.Record("Export a 12-minute video", seconds, 180,
            $"1080p60 gameplay cut every 4 s, zooms, blur cards, captions throughout, music; audio mix and loudness {renderStart:0} s, " +
            $"render and encode {seconds - renderStart:0} s ({43200 / Math.Max(1, seconds - renderStart):0} fps)");
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task CleanFifteenMinuteNarration()
    {
        if (!Models.Ready(Models.WhisperTurbo) || !Models.Ready(Models.Wav2Vec2)) return;
        var dir = Directory.CreateTempSubdirectory("cutroom-bench-clean").FullName;
        Tools.CacheRoot = Path.Combine(dir, "cache");
        var wav = Path.Combine(dir, "narration.wav");
        string[] lines =
        [
            "Sleep dart is the most important cooldown Ana has, and most players throw it away in the first ten seconds of a fight.",
            "The best way to use it is on a Reinhardt who has just committed to a charge or is holding his shield too far forward.",
            "Sleep cancels almost every ultimate in the game, so hold it until the enemy team shows you what they plan to do.",
            "Practice it in the range before you queue, aiming at bots that strafe left and right at different speeds.",
            "When you miss, do not panic; reposition behind your tank and wait for the cooldown before taking another duel.",
        ];
        var script = new List<string>();
        Sta.Run(() =>
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            synth.SetOutputToWaveFile(wav);
            var b = new System.Speech.Synthesis.PromptBuilder();
            for (int i = 0; i < 104; i++)
            {
                var line = lines[i % lines.Length];
                if (i % 9 == 4) { b.AppendText(line[..(line.Length / 3)] + "."); b.AppendBreak(TimeSpan.FromSeconds(.7)); }
                b.AppendText(line);
                b.AppendBreak(TimeSpan.FromSeconds(1.1));
                script.Add(line);
            }
            synth.Speak(b);
        });
        var media = await MediaProbe.Probe(wav);
        Assert.InRange(Time.ToSeconds(media.Duration), 13 * 60, 17 * 60);
        var settings = new CleanupSettings();
        var parsed = Script.FromText(string.Join(' ', script));

        // Time per stage, from the stage names cleanup reports.
        var stages = new List<(string Name, double Start)>();
        var sw = Stopwatch.StartNew();
        var progress = new SyncProgress<CleanupStage>(st =>
        {
            lock (stages) if (stages.Count == 0 || stages[^1].Name != st.Name) stages.Add((st.Name, sw.Elapsed.TotalSeconds));
        });
        await CleanupEngine.Run(media, parsed, [], settings, Models.WhisperTurbo.Name, progress, CancellationToken.None);
        double cold = sw.Elapsed.TotalSeconds;
        var breakdown = string.Join(", ", stages.Select((st, i) => (st.Name, Seconds: (i + 1 < stages.Count ? stages[i + 1].Start : cold) - st.Start))
            .Where(x => x.Seconds >= 1).OrderByDescending(x => x.Seconds).Select(x => $"{x.Name} {x.Seconds:0} s"));
        sw.Restart();
        await CleanupEngine.Run(media, parsed, [], settings, Models.WhisperTurbo.Name, null, CancellationToken.None);
        double cached = sw.Elapsed.TotalSeconds;
        string note = $"{Time.ToSeconds(media.Duration) / 60:0.0} min TTS narration, DeepFilterNet, {(Transcriber.UsesGpu ? "GPU" : "CPU")} whisper";
        bool coldOk = Bench.Write("Clean a 15-minute narration (first run)", cold, 240, $"{note}; {breakdown}");
        bool cachedOk = Bench.Write("Clean a 15-minute narration (cached rerun)", cached, 5, note);
        Assert.True(coldOk && cachedOk, $"cleanup: first run {cold:0.0} s (budget 240), cached rerun {cached:0.0} s (budget 5)");
    }
}
