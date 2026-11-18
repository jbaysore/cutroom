using Cutroom.Core;

namespace Cutroom.Speech;

/// <summary>Per-10 ms analysis of the narration used to find breaths and mouth noises in pauses.</summary>
public sealed class AudioFeatures
{
    public const int HopMs = 10;
    public required float[] Rms;
    public required float[] Zcr;
    public float NoiseFloor { get; init; }

    public static AudioFeatures From(ReadOnlySpan<float> mono16k)
    {
        const int hop = 160;
        int frames = mono16k.Length / hop;
        var rms = new float[frames];
        var zcr = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            var w = mono16k.Slice(f * hop, hop);
            double sum = 0;
            int crossings = 0;
            for (int i = 0; i < w.Length; i++)
            {
                sum += w[i] * w[i];
                if (i > 0 && (w[i] >= 0) != (w[i - 1] >= 0)) crossings++;
            }
            rms[f] = (float)Math.Sqrt(sum / w.Length);
            zcr[f] = crossings / (float)w.Length;
        }
        var sorted = rms.Where(x => x > 1e-6f).OrderBy(x => x).ToArray();
        float floor = sorted.Length > 0 ? sorted[(int)(sorted.Length * .1)] : 1e-4f;
        return new AudioFeatures { Rms = rms, Zcr = zcr, NoiseFloor = Math.Max(floor, 1e-4f) };
    }

    public int Frame(long ticks) => (int)Math.Clamp(ticks * 1000 / Time.Rate / HopMs, 0, Math.Max(0, Rms.Length - 1));

    /// <summary>Energy above the noise floor in [a, b), used to pick the quietest pause to keep.</summary>
    public double Energy(long a, long b)
    {
        double e = 0;
        for (int f = Frame(a); f < Frame(b); f++) e += Math.Max(0, Rms[f] - NoiseFloor * 2);
        return e;
    }

    /// <summary>Labels what is in a pause: nothing, a breath, or a mouth noise (click, smack, swallow).</summary>
    public (DecisionKind Kind, string What) Classify(long a, long b)
    {
        int f0 = Frame(a), f1 = Frame(b);
        float threshold = NoiseFloor * 4;
        int longest = 0, run = 0, eventFrames = 0;
        double zcrSum = 0;
        int shortBursts = 0;
        for (int f = f0; f < f1; f++)
        {
            if (Rms[f] > threshold)
            {
                run++;
                eventFrames++;
                zcrSum += Zcr[f];
            }
            else
            {
                if (run is > 0 and <= 4) shortBursts++;
                longest = Math.Max(longest, run);
                run = 0;
            }
        }
        if (run is > 0 and <= 4) shortBursts++;
        longest = Math.Max(longest, run);
        if (eventFrames == 0) return (DecisionKind.Silence, "silence");
        double zcr = zcrSum / eventFrames;
        if (longest >= 12 && zcr > .12) return (DecisionKind.Breath, "a breath");
        if (shortBursts > 0 && longest <= 6) return (DecisionKind.MouthNoise, shortBursts > 1 ? "mouth clicks" : "a mouth click");
        if (longest >= 8 && zcr <= .12) return (DecisionKind.MouthNoise, "a swallow");
        return longest >= 12 ? (DecisionKind.Breath, "a breath") : (DecisionKind.MouthNoise, "a mouth noise");
    }
}

public sealed record PlannerInput(
    List<Word> Words,
    Script? Script,
    float[] Vad,
    AudioFeatures Features,
    IReadOnlyList<long> RetakeMarks,
    CleanupSettings Settings,
    long Duration,
    Func<long, long, string>? Verbatim = null);

/// <summary>
/// Turns transcript, script and audio analysis into reviewable cleanup decisions. Pure logic: given the same
/// inputs it always produces the same decisions, and it never cuts inside a word.
/// </summary>
public static class CleanupPlanner
{
    const double VadHopSeconds = Vad.Chunk / 16000.0;

    public static List<CleanupDecision> Plan(PlannerInput input)
    {
        var s = input.Settings;
        var words = input.Words.OrderBy(w => w.Start).ToList();
        var decisions = new List<CleanupDecision>();
        long pad = Time.FromMs(s.PaddingMs);

        // 1. Script match: retakes, false starts, ad-libs; spelling from the script.
        MatchResult? match = null;
        if (input.Script is { } script && script.Sentences.Any())
        {
            match = ScriptMatcher.Match(words, script);
            ScriptMatcher.ApplyScriptSpelling(words, script, match);
            foreach (var run in match.Runs)
            {
                var runWords = words.Skip(run.FirstWord).Take(run.LastWord - run.FirstWord + 1).ToList();
                long start = runWords[0].Start, end = runWords[^1].End;
                var context = Context(words, run.FirstWord, run.LastWord);
                if (run.Kind == RunKind.AdLib)
                {
                    decisions.Add(new CleanupDecision
                    {
                        Kind = DecisionKind.AdLibKept, Start = start, End = end, Confidence = .5, Context = context,
                        Reason = "Not in the script, so it was kept. Toggle to cut it.", WordIds = runWords.Select(w => w.Id).ToList(),
                    });
                    continue;
                }
                // Cut from the first word to just before the next word, so the pause before the retake remains.
                long next = run.LastWord + 1 < words.Count ? words[run.LastWord + 1].Start - pad : end + pad;
                bool marked = input.RetakeMarks.Any(m => m >= start - Time.FromSeconds(1) && m <= next + Time.FromSeconds(4));
                double confidence = marked ? .97 : Math.Clamp(.55 + .4 * run.Similarity, 0, .93);
                decisions.Add(new CleanupDecision
                {
                    Kind = run.Kind == RunKind.Retake ? DecisionKind.Retake : DecisionKind.FalseStart,
                    Start = Math.Max(0, start - pad), End = Math.Max(start, next), Confidence = confidence, Context = context,
                    Reason = run.Kind == RunKind.Retake
                        ? $"Earlier take of a line you said again{(marked ? " (you marked a retake)" : "")}; keeping the last complete take."
                        : $"False start: repeats the words that follow{(marked ? " (you marked a retake)" : "")}.",
                    WordIds = runWords.Select(w => w.Id).ToList(),
                });
            }
        }
        var removedWords = decisions.Where(d => d.Kind is DecisionKind.Retake or DecisionKind.FalseStart).SelectMany(d => d.WordIds).ToHashSet();

        // 2. Paragraph starts, for longer pauses at script paragraph breaks.
        var paragraphStarts = new HashSet<string>();
        if (input.Script is { } sc)
            foreach (var p in sc.Sections.SelectMany(x => x.Paragraphs)) if (p.Sentences.Count > 0) paragraphStarts.Add(p.Sentences[0].Id);

        // 3. Pauses between consecutive words (all words, so toggling a retake back on restores natural pauses).
        long leadEnd = words.Count > 0 ? words[0].Start - pad : input.Duration;
        if (leadEnd > Time.FromMs(150)) AddCut(decisions, input, 0, leadEnd - Time.FromMs(100), "Dead air before you start", .95);
        for (int i = 0; i + 1 < words.Count; i++)
        {
            var a = words[i];
            var b = words[i + 1];
            long gapStart = a.End + pad, gapEnd = b.Start - pad;
            long natural = gapEnd - gapStart;
            if (natural <= 0) continue;
            double targetMs = a.Trailing.IndexOfAny(['.', '?', '!']) >= 0
                ? (b.SentenceId is { } sid && sid != a.SentenceId && paragraphStarts.Contains(sid) ? s.ParagraphGapMs : s.SentenceGapMs)
                : a.Trailing.IndexOfAny([',', ';', ':', '—']) >= 0 ? s.CommaGapMs : Math.Min(s.CommaGapMs, 100);
            if (removedWords.Contains(a.Id) || removedWords.Contains(b.Id)) targetMs = s.SentenceGapMs;
            long target = Time.FromMs(targetMs);
            if (natural <= target + Time.FromMs(40)) continue;

            // Possible speech the transcript missed: keep it unless it repeats upcoming script text.
            var speech = SpeechInside(input.Vad, gapStart, gapEnd, s.VadThreshold, Time.FromMs(s.VadMinMs));
            if (speech is { } sp)
            {
                string heard = input.Verbatim?.Invoke(sp.Start, sp.End) ?? "";
                bool repeat = heard.Length > 0 && match is not null && RepeatsUpcoming(heard, words, i + 1);
                if (repeat)
                {
                    decisions.Add(new CleanupDecision
                    {
                        Kind = DecisionKind.FalseStart, Start = sp.Start, End = sp.End, Confidence = .55, Context = $"“{heard}”",
                        Reason = "Untranscribed speech that repeats the next words; looks like a false start.",
                    });
                }
                else
                {
                    decisions.Add(new CleanupDecision
                    {
                        Kind = DecisionKind.PossibleSpeechKept, Start = sp.Start, End = sp.End, Confidence = .3,
                        Context = heard.Length > 0 ? $"“{heard}”" : Context(words, i, i + 1),
                        Reason = "Sounds like speech the transcript missed, so it was kept. Toggle to cut it.",
                    });
                }
                // Shorten only the silent parts on either side of the possible speech.
                ShortenGap(decisions, input, gapStart, sp.Start, Math.Min(target, sp.Start - gapStart), words, i);
                ShortenGap(decisions, input, sp.End, gapEnd, Math.Min(target, gapEnd - sp.End), words, i);
                continue;
            }
            ShortenGap(decisions, input, gapStart, gapEnd, target, words, i);
        }
        if (words.Count > 0)
        {
            long tailStart = words[^1].End + pad + Time.FromMs(300);
            if (input.Duration - tailStart > Time.FromMs(100)) AddCut(decisions, input, tailStart, input.Duration, "Dead air after you finish", .95);
        }
        return decisions.OrderBy(d => d.Start).ToList();
    }

    /// <summary>Removes part of a pause, keeping <paramref name="keep"/> total, placed where it is quietest.</summary>
    static void ShortenGap(List<CleanupDecision> decisions, PlannerInput input, long gapStart, long gapEnd, long keep, List<Word> words, int before)
    {
        long natural = gapEnd - gapStart;
        if (natural - keep < Time.FromMs(40)) return;
        long cut = natural - keep;
        long bestPre = keep / 2;
        double bestEnergy = double.MaxValue;
        for (long pre = 0; pre <= keep; pre += Time.FromMs(10))
        {
            double e = input.Features.Energy(gapStart, gapStart + pre) + input.Features.Energy(gapStart + pre + cut, gapEnd);
            // Slight preference for keeping the pause balanced around the cut.
            e += Math.Abs(pre - keep / 2.0) / Time.Rate * 1e-3;
            if (e < bestEnergy) { bestEnergy = e; bestPre = pre; }
        }
        long a = gapStart + bestPre, b = a + cut;
        var (kind, what) = input.Features.Classify(gapStart, gapEnd);
        double vad = MaxVad(input.Vad, a, b);
        double confidence = vad > .3 ? .7 : kind == DecisionKind.Silence ? .97 : .9;
        decisions.Add(new CleanupDecision
        {
            Kind = kind, Start = a, End = b, Confidence = confidence, Context = Context(words, before, before + 1),
            Reason = $"{Time.ToSeconds(natural) * 1000:0} ms pause with {what}, shortened to {Time.ToSeconds(keep) * 1000:0} ms.",
        });
    }

    static void AddCut(List<CleanupDecision> decisions, PlannerInput input, long a, long b, string reason, double confidence)
    {
        if (b - a < Time.FromMs(40)) return;
        decisions.Add(new CleanupDecision { Kind = DecisionKind.Silence, Start = a, End = b, Confidence = MaxVad(input.Vad, a, b) > .5 ? .6 : confidence, Reason = reason });
    }

    static double MaxVad(float[] vad, long a, long b)
    {
        if (vad.Length == 0) return 0;
        int i0 = (int)(Time.ToSeconds(a) / VadHopSeconds), i1 = (int)Math.Ceiling(Time.ToSeconds(b) / VadHopSeconds);
        double max = 0;
        for (int i = Math.Max(0, i0); i < Math.Min(vad.Length, i1); i++) max = Math.Max(max, vad[i]);
        return max;
    }

    /// <summary>The longest run of VAD speech inside a pause, if it lasts at least <paramref name="minimum"/>.</summary>
    static TimeRange? SpeechInside(float[] vad, long a, long b, double threshold, long minimum)
    {
        if (vad.Length == 0) return null;
        int i0 = (int)Math.Ceiling(Time.ToSeconds(a) / VadHopSeconds), i1 = (int)(Time.ToSeconds(b) / VadHopSeconds);
        int bestStart = -1, bestLen = 0, runStart = -1;
        for (int i = Math.Max(0, i0); i <= Math.Min(vad.Length, i1); i++)
        {
            bool on = i < Math.Min(vad.Length, i1) && vad[i] >= threshold;
            if (on && runStart < 0) runStart = i;
            if (!on && runStart >= 0)
            {
                if (i - runStart > bestLen) { bestLen = i - runStart; bestStart = runStart; }
                runStart = -1;
            }
        }
        if (bestStart < 0 || Time.FromSeconds(bestLen * VadHopSeconds) < minimum) return null;
        return new TimeRange(Time.FromSeconds(bestStart * VadHopSeconds), Time.FromSeconds((bestStart + bestLen) * VadHopSeconds));
    }

    static bool RepeatsUpcoming(string heard, List<Word> words, int next)
    {
        var tokens = Normalizer.Tokens(heard);
        if (tokens.Count == 0) return false;
        var upcoming = words.Skip(next).Take(tokens.Count + 4).SelectMany(w => Normalizer.Tokens(w.Text)).ToList();
        int hits = tokens.Count(t => upcoming.Any(u => u == t || ScriptMatcher.Near(u, t)));
        return hits / (double)tokens.Count >= .6;
    }

    static string Context(List<Word> words, int a, int b)
    {
        var before = words.Skip(Math.Max(0, a - 4)).Take(Math.Min(4, a)).Select(w => w.Text);
        var inside = words.Skip(a).Take(b - a + 1).Select(w => w.Text);
        var after = words.Skip(b + 1).Take(4).Select(w => w.Text);
        return $"…{string.Join(' ', before)} [{string.Join(' ', inside)}] {string.Join(' ', after)}…".Replace("  ", " ");
    }
}
