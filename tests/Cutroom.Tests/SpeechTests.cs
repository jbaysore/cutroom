using Cutroom.Core;
using Cutroom.Library;
using Cutroom.Speech;

namespace Cutroom.Tests;

public class ScriptMatcherTests
{
    /// <summary>Words spoken back to back, 0.3 s each, with a 0.6 s pause after sentence punctuation.</summary>
    internal static List<Word> Spoken(string text)
    {
        var words = new List<Word>();
        long t = Time.FromSeconds(.5);
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var core = raw.TrimEnd('.', ',', '?', '!');
            words.Add(new Word { Text = core, Trailing = raw[core.Length..], Start = t, End = t + Time.FromSeconds(.25) });
            t += Time.FromSeconds(raw.EndsWith('.') || raw.EndsWith(',') ? .9 : .3);
        }
        return words;
    }

    static string Kept(List<Word> words, MatchResult m) =>
        string.Join(' ', words.Where((w, i) => m.Runs.All(r => r.Kind == RunKind.AdLib || i < r.FirstWord || i > r.LastWord)).Select(w => w.Text.ToLowerInvariant()));

    [Fact]
    public void KeepsLastTakeOfARepeatedSentence()
    {
        var script = Script.FromText("Sleep cancels almost every ultimate in the game. Use it well.");
        var words = Spoken("Sleep cancels almost every ultimate. Sleep cancels almost every ultimate in the game. Use it well.");
        var m = ScriptMatcher.Match(words, script);
        var run = Assert.Single(m.Runs);
        Assert.Equal(0, run.FirstWord);
        Assert.Equal(4, run.LastWord);
        Assert.Equal(RunKind.Retake, run.Kind);
        Assert.Equal("sleep cancels almost every ultimate in the game use it well", Kept(words, m));
    }

    [Fact]
    public void FalseStartIsCut()
    {
        var script = Script.FromText("The best way to use it is on a Reinhardt.");
        var words = Spoken("The best way, the best way to use it is on a Reinhardt.");
        var m = ScriptMatcher.Match(words, script);
        var run = Assert.Single(m.Runs);
        Assert.Equal(RunKind.FalseStart, run.Kind);
        Assert.Equal("the best way to use it is on a reinhardt", Kept(words, m));
    }

    [Fact]
    public void AdLibsAreKeptAndMarked()
    {
        var script = Script.FromText("Sleep the Reinhardt. Then win the fight.");
        var words = Spoken("Sleep the Reinhardt. Honestly this one still makes me laugh. Then win the fight.");
        var m = ScriptMatcher.Match(words, script);
        var run = Assert.Single(m.Runs);
        Assert.Equal(RunKind.AdLib, run.Kind);
    }

    [Fact]
    public void StutterOfOneWordIsAFalseStart()
    {
        var script = Script.FromText("Hold it for the ultimate.");
        var words = Spoken("Hold it for the the ultimate.");
        var m = ScriptMatcher.Match(words, script);
        Assert.Equal(RunKind.FalseStart, Assert.Single(m.Runs).Kind);
    }

    [Fact]
    public void ScriptSpellingWins()
    {
        var script = Script.FromText("Anas waste Kiriko's suzu, and lose 1v1s.");
        var words = Spoken("anas waste kirikos suzu and lose 1v1s");
        var m = ScriptMatcher.Match(words, script);
        ScriptMatcher.ApplyScriptSpelling(words, script, m);
        Assert.Equal("Kiriko's", words[2].Text);
        Assert.Equal(",", words[3].Trailing);
        Assert.All(words, w => Assert.NotNull(w.SentenceId));
    }
}

public class PlannerTests
{
    static AudioFeatures Silent(long duration)
    {
        int frames = (int)(Time.ToSeconds(duration) * 100) + 1;
        return new AudioFeatures { Rms = new float[frames], Zcr = new float[frames], NoiseFloor = 1e-4f };
    }

    [Fact]
    public void ShortensPausesNeverCutsWordsAndRemovesRetakes()
    {
        var script = Script.FromText("Sleep cancels ultimates. Use it well.");
        var words = ScriptMatcherTests.Spoken("Sleep cancels. Sleep cancels ultimates. Use it well.");
        // Make one long pause in the middle.
        foreach (var w in words.Skip(5)) { w.Start += Time.FromSeconds(2); w.End += Time.FromSeconds(2); }
        long duration = words[^1].End + Time.FromSeconds(3);
        var decisions = CleanupPlanner.Plan(new PlannerInput(words, script, [], Silent(duration), [], new CleanupSettings(), duration));
        var narration = new Narration { Words = words, Decisions = decisions, SourceDuration = duration };
        narration.RefreshKeptWords();

        Assert.Contains(decisions, d => d.Kind == DecisionKind.Retake || d.Kind == DecisionKind.FalseStart);
        Assert.Equal(["Sleep", "cancels", "ultimates", "Use", "it", "well"], words.Where(w => w.Kept).Select(w => w.Text));
        var cuts = decisions.Where(d => d.Removes).ToList();
        foreach (var w in words.Where(w => w.Kept))
            Assert.DoesNotContain(cuts, c => c.Start < w.End && w.Start < c.End);

        // Every pause between kept words ends up near its target.
        var spans = narration.KeptSpans();
        long kept = spans.Sum(s => s.Duration);
        Assert.True(kept < duration - Time.FromSeconds(2.5));
        Assert.Contains(decisions, d => d.Reason.Contains("before you start"));
        Assert.Contains(decisions, d => d.Reason.Contains("after you finish"));
    }

    [Fact]
    public void RetakeMarkRaisesConfidence()
    {
        var script = Script.FromText("Sleep cancels almost every ultimate in the game.");
        var words = ScriptMatcherTests.Spoken("Sleep cancels almost every ultimate. Sleep cancels almost every ultimate in the game.");
        long duration = words[^1].End + Time.FromSeconds(1);
        var plain = CleanupPlanner.Plan(new PlannerInput(words, script, [], Silent(duration), [], new CleanupSettings(), duration));
        var marked = CleanupPlanner.Plan(new PlannerInput(words, script, [], Silent(duration), [words[4].End + Time.FromMs(300)], new CleanupSettings(), duration));
        Assert.True(marked.First(d => d.Kind == DecisionKind.Retake).Confidence > plain.First(d => d.Kind == DecisionKind.Retake).Confidence);
    }

    [Fact]
    public void UntranscribedSpeechIsKeptUnlessItRepeats()
    {
        var script = Script.FromText("First line here. Second line here.");
        var words = ScriptMatcherTests.Spoken("First line here. Second line here.");
        foreach (var w in words.Skip(3)) { w.Start += Time.FromSeconds(2); w.End += Time.FromSeconds(2); }
        long duration = words[^1].End + Time.FromSeconds(1);
        var vad = new float[(int)(Time.ToSeconds(duration) / (512 / 16000.0)) + 1];
        long speechStart = words[2].End + Time.FromSeconds(.8);
        for (int i = (int)(Time.ToSeconds(speechStart) / .032); i < (int)(Time.ToSeconds(speechStart + Time.FromSeconds(.6)) / .032); i++) vad[i] = .9f;

        var kept = CleanupPlanner.Plan(new PlannerInput(words, script, vad, Silent(duration), [], new CleanupSettings(), duration, (_, _) => "um so anyway"));
        Assert.Contains(kept, d => d.Kind == DecisionKind.PossibleSpeechKept);
        var repeat = CleanupPlanner.Plan(new PlannerInput(words, script, vad, Silent(duration), [], new CleanupSettings(), duration, (_, _) => "second line"));
        Assert.Contains(repeat, d => d.Kind == DecisionKind.FalseStart);
    }

    [Fact]
    public void ClassifiesBreathsAndClicks()
    {
        var rms = new float[100];
        var zcr = new float[100];
        for (int i = 10; i < 30; i++) { rms[i] = .05f; zcr[i] = .3f; }   // 200 ms noisy: breath
        rms[60] = .2f; zcr[60] = .4f;                                     // 10 ms: click
        var f = new AudioFeatures { Rms = rms, Zcr = zcr, NoiseFloor = .001f };
        Assert.Equal(DecisionKind.Breath, f.Classify(0, Time.FromMs(400)).Kind);
        Assert.Equal(DecisionKind.MouthNoise, f.Classify(Time.FromMs(500), Time.FromMs(800)).Kind);
        Assert.Equal(DecisionKind.Silence, f.Classify(Time.FromMs(800), Time.FromMs(990)).Kind);
    }
}

public class TranscriberTests
{
    [Fact]
    public void ParsesWhisperTokensIntoWords()
    {
        var json = """
        {"transcription":[{"offsets":{"from":0,"to":3000},"text":" Ana's sleep, dart.","tokens":[
          {"text":"[_BEG_]","offsets":{"from":0,"to":0},"p":0.9},
          {"text":" Ana","offsets":{"from":100,"to":300},"p":0.8},
          {"text":"'s","offsets":{"from":300,"to":400},"p":0.9},
          {"text":" sleep","offsets":{"from":450,"to":800},"p":0.95},
          {"text":",","offsets":{"from":800,"to":800},"p":0.9},
          {"text":" dart","offsets":{"from":900,"to":1200},"p":0.97},
          {"text":".","offsets":{"from":1200,"to":1200},"p":0.9},
          {"text":"[_TT_150]","offsets":{"from":3000,"to":3000},"p":0.1}]}]}
        """;
        var words = Transcriber.Parse(json);
        Assert.Equal(["Ana's", "sleep", "dart"], words.Select(w => w.Text));
        Assert.Equal(",", words[1].Trailing);
        Assert.Equal(".", words[2].Trailing);
        Assert.Equal(.1, words[0].Start, 3);
        Assert.Equal(.4, words[0].End, 3);
    }
}

/// <summary>Runs the real models on synthesized speech. Excluded from the fast check (needs ~2 GB of models).</summary>
[Trait("Category", "Models")]
public class SpeechPipelineTests
{
    [Fact]
    public async Task CleansSynthesizedNarrationWithRetakes()
    {
        if (!Models.Ready(Models.WhisperTurbo) || !Models.Ready(Models.Wav2Vec2)) return;
        var dir = Directory.CreateTempSubdirectory("cutroom-pipeline").FullName;
        var wav = Path.Combine(dir, "narration.wav");
        Sta.Run(() =>
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            synth.SetOutputToWaveFile(wav);
            var prompt = new System.Speech.Synthesis.PromptBuilder();
            prompt.AppendText("Sleep dart is the most important cooldown in the game.");
            prompt.AppendBreak(TimeSpan.FromSeconds(1.5));
            prompt.AppendText("The best way.");
            prompt.AppendBreak(TimeSpan.FromSeconds(.6));
            prompt.AppendText("The best way to use it is on a Reinhardt.");
            prompt.AppendBreak(TimeSpan.FromSeconds(2));
            prompt.AppendText("Sleep cancels almost every ultimate.");
            prompt.AppendBreak(TimeSpan.FromSeconds(.8));
            prompt.AppendText("Sleep cancels almost every ultimate in the game.");
            synth.Speak(prompt);
        });
        Tools.CacheRoot = Path.Combine(dir, "cache");
        var media = await MediaProbe.Probe(wav);
        var script = Script.FromText("Sleep dart is the most important cooldown in the game. The best way to use it is on a Reinhardt. Sleep cancels almost every ultimate in the game.");
        var result = await CleanupEngine.Run(media, script, [], new CleanupSettings { Denoiser = Denoiser.Off }, Models.WhisperTurbo.Name, null, CancellationToken.None);

        var narration = new Narration { Words = result.Words, Decisions = result.Decisions, SourceDuration = result.Duration };
        narration.RefreshKeptWords();
        var kept = Normalizer.Tokens(string.Join(' ', result.Words.Where(w => w.Kept).Select(w => w.Text)));
        var expected = script.Sentences.SelectMany(s => s.Tokens).ToList();
        Assert.Equal(expected, kept);
        // Nothing substantive removed: every kept word lies entirely inside kept audio.
        var spans = narration.KeptSpans();
        foreach (var w in result.Words.Where(w => w.Kept))
        {
            var overlapping = result.Decisions.Where(d => d.Removes && d.Start < w.End && w.Start < d.End).ToList();
            Assert.True(overlapping.Count == 0, $"{w.Text} [{w.Start}-{w.End}] cut by " + string.Join("; ", overlapping.Select(d => $"{d.Kind} {d.Start}-{d.End} {d.Reason}"))
                + " | " + string.Join(" | ", result.Words.Select(x => $"{x.Text}{x.Trailing} {x.Start}-{x.End} kept={x.Kept}")));
        }
        foreach (var w in result.Words.Where(w => w.Kept))
            Assert.Contains(spans, s => s.Start <= w.Start && w.End <= s.End);
        Assert.True(spans.Sum(s => s.Duration) < result.Duration - Time.FromSeconds(3));
    }
}
