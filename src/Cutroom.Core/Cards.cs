namespace Cutroom.Core;

public static class Cards
{
    /// <summary>Splits card text into lines: explicit line breaks first, then sentences, then wraps at ~45 characters.</summary>
    public static List<string> SplitLines(string text, int maxChars = 45)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = para.Trim();
            if (t.Length == 0) continue;
            var sentences = t.Split('\n').Length > 1 ? [t] : Script.SplitSentences(t);
            foreach (var s in sentences) lines.Add(s);
        }
        return lines;
    }

    /// <summary>Greedy word wrap used for layout and overflow checks.</summary>
    public static List<string> Wrap(string text, int maxChars)
    {
        var result = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars) { result.Add(current); current = word; }
            else current = current.Length == 0 ? word : current + " " + word;
        }
        if (current.Length > 0) result.Add(current);
        return result;
    }

    public static CardData NewCard(CardTemplateKind kind, string text, string templateId = "")
    {
        var card = new CardData { Template = kind, TemplateId = templateId };
        SetText(card, text);
        if (kind is CardTemplateKind.SectionHeader or CardTemplateKind.TitleCard or CardTemplateKind.EndScreen) card.Reveal = RevealMode.AllAtOnce;
        return card;
    }

    public static void SetText(CardData card, string text)
    {
        var lines = SplitLines(text);
        var old = card.Lines;
        card.Lines = lines.Select((l, i) => new CardLine { Text = l, Reveal = i < old.Count && old[i].Manual ? old[i].Reveal : 0, Manual = i < old.Count && old[i].Manual }).ToList();
    }

    /// <summary>Times line-by-line reveals by matching each line to where it is spoken in the card's range.</summary>
    public static void AutoTime(Project p, Clip clip, List<(Word Word, TimeRange TimeRange)>? words = null)
    {
        if (clip.Card is not { } card || card.Lines.Count == 0) return;
        words ??= Derive.TimelineWords(p);
        var inRange = words.Where(w => w.TimeRange.Start >= clip.Start - Time.FromMs(200) && w.TimeRange.Start < clip.End).ToList();
        var tokens = inRange.Select(w => Normalizer.Tokens(w.Word.Text)).ToList();
        int cursor = 0;
        var times = new long?[card.Lines.Count];
        for (int i = 0; i < card.Lines.Count; i++)
        {
            if (card.Lines[i].Manual) { times[i] = card.Lines[i].Reveal; continue; }
            var lineTokens = Normalizer.Tokens(card.Lines[i].Text).Where(t => t.Length > 2).Take(6).ToHashSet();
            if (lineTokens.Count == 0) continue;
            int best = -1; double bestScore = 0;
            for (int j = cursor; j < inRange.Count; j++)
            {
                // A line is revealed when its first distinctive word is spoken, so the window must start on one.
                if (!tokens[j].Any(lineTokens.Contains)) continue;
                // Score a 6-word window by how many distinctive line tokens it contains; earlier wins ties.
                var window = tokens.Skip(j).Take(6).SelectMany(x => x).ToHashSet();
                double score = lineTokens.Count(window.Contains) / (double)lineTokens.Count;
                if (score > bestScore + 1e-9) { best = j; bestScore = score; }
            }
            if (best >= 0 && bestScore >= .34)
            {
                times[i] = Math.Max(0, inRange[best].TimeRange.Start - clip.Start);
                cursor = best + 1;
            }
        }
        // First line always shows when the card appears; unmatched lines are spread between their neighbours.
        times[0] = card.Lines[0].Manual ? times[0] : 0;
        for (int i = 0; i < times.Length; i++)
        {
            if (times[i] is not null) continue;
            int prev = i - 1, next = i + 1;
            while (next < times.Length && times[next] is null) next++;
            long a = times[prev] ?? 0;
            long b = next < times.Length ? times[next]!.Value : clip.Duration * 3 / 4;
            int gaps = next - prev;
            for (int k = i; k < next; k++) times[k] = a + (b - a) * (k - prev) / Math.Max(1, gaps);
            i = next - 1;
        }
        long last = 0;
        for (int i = 0; i < times.Length; i++)
        {
            long t = Math.Clamp(times[i] ?? 0, last, Math.Max(last, clip.Duration - Time.FromMs(300)));
            card.Lines[i].Reveal = t;
            last = t;
        }
    }

    public static void AutoTimeAll(Project p)
    {
        var words = Derive.TimelineWords(p);
        foreach (var clip in p.AllClips.Where(c => c.Card is { Reveal: RevealMode.LineByLine }))
            AutoTime(p, clip, words);
    }

    /// <summary>Applies tap-to-retime: each tap time (absolute timeline) becomes the reveal of the next line.</summary>
    public static void ApplyTaps(Clip clip, IReadOnlyList<long> taps)
    {
        if (clip.Card is not { } card) return;
        for (int i = 0; i < taps.Count && i + 1 < card.Lines.Count; i++)
        {
            card.Lines[i + 1].Reveal = Math.Clamp(taps[i] - clip.Start, 0, clip.Duration);
            card.Lines[i + 1].Manual = true;
        }
    }

    /// <summary>Opacity 0..1 of a line at time t (relative to the card) given a 200 ms fade.</summary>
    public static double LineOpacity(CardData card, int line, long t)
    {
        long reveal = card.Reveal == RevealMode.AllAtOnce ? 0 : card.Lines[line].Reveal;
        double fade = Time.FromMs(200);
        return Math.Clamp((t - reveal) / fade, 0, 1);
    }
}
