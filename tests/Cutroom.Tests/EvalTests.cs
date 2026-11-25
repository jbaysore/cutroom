using Cutroom.Core;
using Cutroom.Library;
using Cutroom.Speech;

namespace Cutroom.Tests;

public class CleanupEvaluationTests
{
    static List<Word> Words(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((t, i) => new Word { Text = t, Start = i * 1000, End = i * 1000 + 900 }).ToList();

    [Fact]
    public void PerfectCleanupScoresFull()
    {
        var heard = Words("the best way the best way to use it");
        var kept = heard.Skip(3).ToList();
        var s = CleanupEvaluation.Score("case", "The best way to use it.", heard, kept, []);
        Assert.Equal(0, s.SubstantiveRemoved);
        Assert.Equal(0, s.Leftover);
        Assert.Equal(3, s.ToCut);
        Assert.Equal(1, s.RetakeRecall);
        Assert.Equal(1, s.WordAccuracy);
        Assert.True(s.Passes);
    }

    [Fact]
    public void CountsRemovedAndLeftoverWords()
    {
        var heard = Words("the best way the best way to use it on a reinhardt");
        var kept = Words("best way the best way to use it a reinhardt");
        var s = CleanupEvaluation.Score("case", "the best way to use it on a reinhardt", heard, kept, []);
        Assert.Equal(1, s.SubstantiveRemoved);
        Assert.Equal(["on"], s.RemovedSamples);
        Assert.Equal(2, s.Leftover);
        Assert.Equal(1 - 2 / 3.0, s.RetakeRecall, 6);
        Assert.False(s.Passes);
        Assert.Contains("(miss)", CleanupEvaluation.Report([s]));
    }

    [Fact]
    public void KeptWordsExcludeRetakesAndCutAudio()
    {
        var words = Words("a b c d");
        var n = new Narration
        {
            Words = words, SourceDuration = 4000,
            Decisions =
            [
                new CleanupDecision { Kind = DecisionKind.Retake, Start = 0, End = 900, WordIds = [words[0].Id] },
                new CleanupDecision { Kind = DecisionKind.Silence, Start = 2000, End = 2950 },
                new CleanupDecision { Kind = DecisionKind.FalseStart, Start = 3000, End = 3900, WordIds = [words[3].Id], Applied = false },
            ],
        };
        Assert.Equal(["b", "d"], CleanupEvaluation.KeptWords(n).Select(w => w.Text));
    }
}

/// <summary>The evaluator end to end on a synthesized corpus case. Needs the speech models.</summary>
[Trait("Category", "Models")]
[Collection(SharedCache.Name)]
public class CleanupCorpusTests
{
    [Fact]
    public async Task ScoresSynthesizedCase()
    {
        if (!Models.Ready(Models.WhisperTurbo) || !Models.Ready(Models.Wav2Vec2)) return;
        var corpus = Directory.CreateTempSubdirectory("cutroom-corpus").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(corpus, "retakes")).FullName;
        Tools.CacheRoot = Path.Combine(corpus, "cache");
        Sta.Run(() =>
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            synth.SetOutputToWaveFile(Path.Combine(dir, "narration.wav"));
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
            synth.Speak(b);
        });
        const string script = "Sleep dart is the most important cooldown in the game. The best way to use it is on a Reinhardt. Sleep cancels almost every ultimate in the game.";
        File.WriteAllText(Path.Combine(dir, "script.txt"), script);
        File.WriteAllText(Path.Combine(dir, "expected.txt"), script);

        var scores = await CleanupCorpus.Run(corpus, Denoiser.Off, Models.WhisperTurbo.Name, null, CancellationToken.None);
        var s = Assert.Single(scores);
        Assert.Equal(0, s.SubstantiveRemoved);
        Assert.True(s.ToCut >= 6, $"expected the retakes to be heard, to-cut {s.ToCut}");
        Assert.True(s.RetakeRecall >= CleanupScore.RecallTarget, CleanupEvaluation.Report(scores));
        Assert.True(s.WordAccuracy >= CleanupScore.AccuracyTarget, CleanupEvaluation.Report(scores));
    }
}
