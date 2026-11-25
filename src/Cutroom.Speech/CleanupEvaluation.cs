using System.Globalization;
using System.Text;
using Cutroom.Core;

namespace Cutroom.Speech;

/// <summary>
/// Scores a cleanup run against a hand-written reference: the words the cleaned narration should say, in order
/// (ad-libs included, retakes and false starts removed). Used by <c>scripts/eval-cleanup.ps1</c> on the corpus.
/// </summary>
public sealed record CleanupScore(
    string Case,
    int ExpectedWords,
    int HeardWords,
    int KeptWords,
    int SubstantiveRemoved,
    int Leftover,
    int ToCut,
    int AdLibsKept,
    double WordAccuracy,
    IReadOnlyList<string> RemovedSamples,
    IReadOnlyList<string> LeftoverSamples)
{
    public const double RecallTarget = .95, AccuracyTarget = .98;

    /// <summary>Share of the speech that should go (retakes, false starts) that was actually cut.</summary>
    public double RetakeRecall => ToCut == 0 ? 1 : Math.Max(0, 1 - (double)Leftover / ToCut);

    public bool Passes => SubstantiveRemoved == 0;
}

public static class CleanupEvaluation
{
    /// <summary>Words that survive the applied cuts, in source order.</summary>
    public static List<Word> KeptWords(Narration n)
    {
        n.RefreshKeptWords();
        var cuts = n.Decisions.Where(d => d.Removes).Select(d => d.TimeRange).ToList();
        return n.Words.Where(w => w.Kept && !cuts.Any(c => c.Contains((w.Start + w.End) / 2))).OrderBy(w => w.Start).ToList();
    }

    public static CleanupScore Score(string name, string expectedText, IReadOnlyList<Word> heard, IReadOnlyList<Word> kept, IEnumerable<CleanupDecision> decisions)
    {
        var expected = Normalizer.Tokens(expectedText);
        var heardTokens = Tokens(heard);
        var keptTokens = Tokens(kept);
        var (_, toCut, _) = Align(expected, heardTokens);
        var (removed, leftover, edits) = Align(expected, keptTokens);
        return new CleanupScore(name, expected.Count, heardTokens.Count, keptTokens.Count, removed.Count, leftover.Count, toCut.Count,
            decisions.Count(d => d.Kind == DecisionKind.AdLibKept && d.Applied),
            expected.Count == 0 ? 1 : Math.Max(0, 1 - (double)edits / expected.Count),
            Samples(removed), Samples(leftover));
    }

    static List<string> Tokens(IEnumerable<Word> words) => words.SelectMany(w => Normalizer.Tokens(w.Text)).ToList();

    static List<string> Samples(List<string> words) => words.Take(12).ToList();

    /// <summary>
    /// Word-level Levenshtein alignment of <paramref name="actual"/> to <paramref name="expected"/>. Returns the expected
    /// words that are missing (deletions, substitutions count here too), the actual words with no expected counterpart
    /// (insertions, substitutions too) and the total edit count.
    /// </summary>
    public static (List<string> Missing, List<string> Extra, int Edits) Align(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        int n = expected.Count, m = actual.Count;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
        var missing = new List<string>();
        var extra = new List<string>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && expected[i - 1] == actual[j - 1] && d[i, j] == d[i - 1, j - 1]) { i--; j--; }
            else if (j > 0 && d[i, j] == d[i, j - 1] + 1) { extra.Add(actual[--j]); }
            else if (i > 0 && d[i, j] == d[i - 1, j] + 1) { missing.Add(expected[--i]); }
            else { missing.Add(expected[--i]); extra.Add(actual[--j]); }
        }
        missing.Reverse();
        extra.Reverse();
        return (missing, extra, d[n, m]);
    }

    public static string Report(IReadOnlyList<CleanupScore> scores)
    {
        static string Pct(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        var sb = new StringBuilder();
        sb.AppendLine("# Cleanup evaluation").AppendLine();
        sb.AppendLine($"Targets: substantive words removed = 0, retake recall >= {Pct(CleanupScore.RecallTarget)}, word accuracy >= {Pct(CleanupScore.AccuracyTarget)}.").AppendLine();
        sb.AppendLine("| Case | Expected | Heard | Kept | Removed (must be 0) | Leftover | Retake recall | Word accuracy | Ad-libs kept |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in scores)
            sb.AppendLine($"| {s.Case} | {s.ExpectedWords} | {s.HeardWords} | {s.KeptWords} | {s.SubstantiveRemoved}{(s.Passes ? "" : " (miss)")} | {s.Leftover} | " +
                $"{Pct(s.RetakeRecall)}{(s.RetakeRecall < CleanupScore.RecallTarget ? " (miss)" : "")} | {Pct(s.WordAccuracy)}{(s.WordAccuracy < CleanupScore.AccuracyTarget ? " (miss)" : "")} | {s.AdLibsKept} |");
        if (scores.Count > 1)
        {
            int expected = scores.Sum(s => s.ExpectedWords), leftover = scores.Sum(s => s.Leftover), toCut = scores.Sum(s => s.ToCut);
            double accuracy = expected == 0 ? 1 : scores.Sum(s => s.WordAccuracy * s.ExpectedWords) / expected;
            sb.AppendLine($"| **All** | {expected} | {scores.Sum(s => s.HeardWords)} | {scores.Sum(s => s.KeptWords)} | {scores.Sum(s => s.SubstantiveRemoved)} | {leftover} | " +
                $"{Pct(toCut == 0 ? 1 : Math.Max(0, 1 - (double)leftover / toCut))} | {Pct(accuracy)} | {scores.Sum(s => s.AdLibsKept)} |");
        }
        foreach (var s in scores.Where(s => s.RemovedSamples.Count > 0 || s.LeftoverSamples.Count > 0))
        {
            sb.AppendLine().AppendLine($"## {s.Case}");
            if (s.RemovedSamples.Count > 0) sb.AppendLine($"- Removed but expected: {string.Join(' ', s.RemovedSamples)}");
            if (s.LeftoverSamples.Count > 0) sb.AppendLine($"- Kept but not expected: {string.Join(' ', s.LeftoverSamples)}");
        }
        return sb.ToString();
    }
}
