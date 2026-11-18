using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cutroom.Core;

public sealed class SubtitlePreset
{
    public const string DefaultId = "bold-white";
    public string Id { get; set; } = DefaultId;
    public string Name { get; set; } = "Bold White";
    public string FontFamily { get; set; } = "Montserrat ExtraBold";
    public double Size { get; set; } = 64;
    public string Fill { get; set; } = "#FFFFFF";
    public string Outline { get; set; } = "#000000";
    public double OutlineWidth { get; set; } = 8;
    public double ShadowOpacity { get; set; } = .4;
    /// <summary>Vertical center as a fraction of frame height.</summary>
    public double Y { get; set; } = .78;
    public string Animation { get; set; } = "None";
    public int MaxChars { get; set; } = 42;
}

public sealed record CaptionTiming(Caption Caption, long Start, long End);

public static class Subtitles
{
    public sealed record ChunkOptions(int MinWords = 3, int MaxWords = 8, int MaxChars = 42, double MinSeconds = .8, double MaxSeconds = 6);

    /// <summary>Greedy chunking into one-line captions of about 3-8 words, breaking at natural phrase boundaries.</summary>
    public static List<Caption> Chunk(IReadOnlyList<(Word Word, TimeRange TimeRange)> words, ChunkOptions? options = null, IReadOnlyCollection<string>? vocabulary = null)
    {
        var o = options ?? new ChunkOptions();
        var names = new HashSet<string>(vocabulary ?? [], StringComparer.OrdinalIgnoreCase);
        var captions = new List<Caption>();
        var current = new List<(Word Word, TimeRange TimeRange)>();
        void Flush()
        {
            if (current.Count == 0) return;
            captions.Add(new Caption { FirstWordId = current[0].Word.Id, LastWordId = current[^1].Word.Id, Text = Derive.Join(current.Select(c => c.Word)) });
            current.Clear();
        }
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (current.Count > 0)
            {
                var candidate = Derive.Join(current.Select(c => c.Word).Append(w.Word));
                long gap = w.TimeRange.Start - current[^1].TimeRange.End;
                bool tooLong = candidate.Length > o.MaxChars || current.Count >= o.MaxWords
                               || w.TimeRange.End - current[0].TimeRange.Start > Time.FromSeconds(o.MaxSeconds);
                if (tooLong || gap > Time.FromMs(700) && current.Count >= 1) Flush();
            }
            current.Add(w);
            if (i + 1 >= words.Count) break;
            var next = words[i + 1].Word;
            string trail = w.Word.Trailing;
            bool sentenceEnd = trail.IndexOfAny(['.', '?', '!']) >= 0;
            bool clause = trail.IndexOfAny([',', ';', ':']) >= 0 || trail.Contains('—');
            bool keepTogether = IsNumber(w.Word.Text) && !IsNumber(next.Text)
                                || names.Contains(w.Word.Text) && names.Contains(next.Text)
                                || char.IsUpper(w.Word.Text.FirstOrDefault()) && char.IsUpper(next.Text.FirstOrDefault()) && trail.Length == 0 && current.Count > 1;
            if (keepTogether) continue;
            if (sentenceEnd && current.Count >= 1 || clause && current.Count >= o.MinWords
                || current.Count >= 5 && IsBreakWord(next.Text) && CharsAhead(words, i + 1, o.MaxWords - current.Count) + Derive.Join(current.Select(c => c.Word)).Length > o.MaxChars)
                Flush();
        }
        Flush();
        MergeOrphans(captions, words);
        return captions;
    }

    static int CharsAhead(IReadOnlyList<(Word Word, TimeRange TimeRange)> words, int from, int count) =>
        words.Skip(from).Take(Math.Max(0, count)).Sum(w => w.Word.Text.Length + 1);

    static readonly HashSet<string> BreakWords = new(StringComparer.OrdinalIgnoreCase)
    { "and", "but", "or", "so", "because", "which", "that", "when", "while", "if", "then", "to", "for", "with", "from", "into", "on", "in", "at", "of" };
    static bool IsBreakWord(string w) => BreakWords.Contains(w);
    static bool IsNumber(string w) => w.Length > 0 && char.IsDigit(w[0]);

    /// <summary>Single-word captions after a sentence end are merged back when that still fits.</summary>
    static void MergeOrphans(List<Caption> captions, IReadOnlyList<(Word Word, TimeRange TimeRange)> words)
    {
        for (int i = captions.Count - 1; i > 0; i--)
        {
            if (captions[i].Text.Split(' ').Length == 1 && captions[i - 1].Text.Length + captions[i].Text.Length + 1 <= 42 &&
                !captions[i - 1].Text.EndsWith('.') && !captions[i - 1].Text.EndsWith('?'))
            {
                captions[i - 1].Text += " " + captions[i].Text;
                captions[i - 1].LastWordId = captions[i].LastWordId;
                captions.RemoveAt(i);
            }
        }
    }

    /// <summary>Resolves caption times from their anchor words, applying the 0.8 s minimum without overlapping the next caption.</summary>
    public static List<CaptionTiming> Timings(Project p, IReadOnlyList<(Word Word, TimeRange TimeRange)>? words = null)
    {
        words ??= Derive.TimelineWords(p);
        var ranges = words.ToDictionary(w => w.Word.Id, w => w.TimeRange);
        var result = new List<CaptionTiming>();
        foreach (var c in p.Captions)
        {
            if (c.FixedStart is { } fs && c.FixedEnd is { } fe) { result.Add(new(c, fs, fe)); continue; }
            if (!ranges.TryGetValue(c.FirstWordId, out var a)) continue;
            var b = ranges.TryGetValue(c.LastWordId, out var r) ? r : a;
            result.Add(new(c, a.Start, Math.Max(a.Start + Time.Frame, b.End)));
        }
        result.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (int i = 0; i < result.Count; i++)
        {
            long min = result[i].Start + Time.FromMs(800);
            long limit = i + 1 < result.Count ? result[i + 1].Start : long.MaxValue;
            long end = Math.Min(Math.Max(result[i].End, min), limit);
            // Hold short gaps so captions do not flicker off between phrases.
            if (i + 1 < result.Count && limit - end < Time.FromMs(250)) end = limit;
            result[i] = result[i] with { End = end };
        }
        return result;
    }

    /// <summary>The caption to draw at t, honoring subtitle regions and hiding under text cards.</summary>
    public static (CaptionTiming Caption, SubtitleRegion Region)? Visible(Project p, long t, IReadOnlyList<CaptionTiming> timings)
    {
        var region = p.SubtitleRegions.FirstOrDefault(r => t >= r.Start && t < r.End);
        if (region is null) return null;
        if (!region.ShowOverCards && p.Track(TrackRole.Cards).Clips.Any(c => c.Kind == ClipKind.Card && c.TimeRange.Contains(t) && c.Card?.Content == CardContent.Text))
            return null;
        foreach (var c in timings)
            if (t >= c.Start && t < c.End) return (c, region);
        return null;
    }

    public static string ToSrt(IEnumerable<CaptionTiming> timings)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var c in timings)
            sb.Append(n++).Append('\n').Append(Time.Srt(c.Start)).Append(" --> ").Append(Time.Srt(c.End)).Append('\n').Append(c.Caption.Text).Append("\n\n");
        return sb.ToString();
    }

    public static List<Caption> FromSrt(string text)
    {
        var result = new List<Caption>();
        var blocks = Regex.Split(text.Replace("\r\n", "\n").Trim(), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timeLine = lines.FirstOrDefault(l => l.Contains("-->"));
            if (timeLine is null) continue;
            var parts = timeLine.Split("-->");
            var body = string.Join(" ", lines.SkipWhile(l => l != timeLine).Skip(1)).Trim();
            if (body.Length == 0) continue;
            result.Add(new Caption { Text = Regex.Replace(body, "<[^>]+>", ""), FixedStart = ParseSrtTime(parts[0]), FixedEnd = ParseSrtTime(parts[1]) });
        }
        return result;
    }

    static long ParseSrtTime(string s)
    {
        var m = Regex.Match(s.Trim(), @"(\d+):(\d+):(\d+)[,.](\d+)");
        if (!m.Success) return 0;
        double seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                         + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
        return Time.FromSeconds(seconds);
    }
}
