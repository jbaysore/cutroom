using System.Text;
using System.Text.RegularExpressions;

namespace Cutroom.Core;

/// <summary>
/// Everything that follows from the narration: the A1 clips, script slots, intro subtitle region,
/// section headers and chapters. Called after cleanup decisions change.
/// </summary>
public static class Derive
{
    /// <summary>Rebuilds the narration track from the kept spans, then re-times everything anchored to it.</summary>
    public static void RebuildNarration(Project p)
    {
        var n = p.Narration;
        if (n.MediaId is null) return;
        n.RefreshKeptWords();
        var track = p.Track(TrackRole.Narration);
        track.Clips.RemoveAll(c => c.MediaId == n.MediaId);
        long fade = Time.FromMs(n.Settings.CrossfadeMs);
        long t = 0;
        var spans = n.KeptSpans();
        for (int i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            track.Clips.Add(new Clip
            {
                MediaId = n.MediaId, Start = t, SourceIn = s.Start, Duration = s.Duration, GainDb = n.GainDb,
                FadeIn = i > 0 ? fade : 0, FadeOut = i < spans.Count - 1 ? fade : 0, Label = "Narration",
            });
            t += s.Duration;
        }
        RefreshSlots(p);
    }

    /// <summary>Kept words with their timeline ranges, in order.</summary>
    public static List<(Word Word, TimeRange TimeRange)> TimelineWords(Project p)
    {
        var map = new NarrationMap(p);
        var result = new List<(Word, TimeRange)>();
        foreach (var w in p.Narration.Words.Where(w => w.Kept).OrderBy(w => w.Start))
        {
            var r = map.WordRange(w);
            if (r is not null) result.Add((w, r.Value));
        }
        return result;
    }

    /// <summary>Creates slots from the script match (keeping ids where a slot starts on the same word) and re-times slot clips.</summary>
    public static void GenerateSlots(Project p)
    {
        var words = TimelineWords(p);
        var old = p.Slots.ToDictionary(s => s.FirstWordId);
        var groups = new List<List<Word>>();
        var script = p.Script;
        string? Key(Word w)
        {
            if (w.SentenceId is null || script is null) return null;
            return p.SlotsPerParagraph ? script.ParagraphOf(w.SentenceId)?.Id ?? w.SentenceId : w.SentenceId;
        }
        string? lastKey = null;
        foreach (var (w, _) in words)
        {
            var key = Key(w);
            bool newGroup = groups.Count == 0
                || key is not null && key != lastKey
                || key is null && script is null && groups[^1].Count > 0 && groups[^1][^1].Trailing.IndexOfAny(['.', '?', '!']) >= 0;
            if (newGroup) groups.Add([]);
            groups[^1].Add(w);
            if (key is not null) lastKey = key;
        }

        var slots = new List<Slot>();
        var ranges = words.ToDictionary(x => x.Word.Id, x => x.TimeRange);
        foreach (var g in groups)
        {
            var sentence = g.FirstOrDefault(w => w.SentenceId is not null)?.SentenceId;
            var section = sentence is null || script is null ? null : script.SectionOf(sentence);
            bool intro = script is not null && script.HasHeadings && section is not null && section.Heading is null
                         || script is { HasHeadings: false } && slots.Count == 0;
            foreach (var part in intro ? SplitPhrases(g, ranges, script!) : [g])
            {
                old.TryGetValue(part[0].Id, out var existing);
                slots.Add(new Slot
                {
                    Id = existing?.Id ?? Ids.New(),
                    Suggestion = existing?.Suggestion,
                    FirstWordId = part[0].Id,
                    LastWordId = part[^1].Id,
                    Text = Join(part),
                    SectionTitle = section?.Heading,
                    IsIntro = intro,
                });
            }
        }
        p.Slots = slots;
        RefreshSlots(p);
    }

    /// <summary>In the intro, split a sentence at phrase boundaries, merging pieces until each is at least 2 s.</summary>
    static IEnumerable<List<Word>> SplitPhrases(List<Word> words, Dictionary<string, TimeRange> ranges, Script script)
    {
        var sentence = script.Sentences.FirstOrDefault(s => s.Id == words.FirstOrDefault(w => w.SentenceId is not null)?.SentenceId);
        var pieces = new List<List<Word>> { new() };
        foreach (var w in words)
        {
            bool boundary = pieces[^1].Count > 0 && (sentence is not null && w.ScriptTokenIndex > 0 && sentence.PhraseStarts.Contains(w.ScriptTokenIndex)
                || pieces[^1][^1].Trailing.IndexOfAny([',', ';', ':']) >= 0);
            if (boundary) pieces.Add([]);
            pieces[^1].Add(w);
        }
        long Duration(List<Word> ws) => ranges[ws[^1].Id].End - ranges[ws[0].Id].Start;
        var merged = new List<List<Word>>();
        foreach (var piece in pieces.Where(x => x.Count > 0))
        {
            if (merged.Count > 0 && (Duration(merged[^1]) < Time.FromSeconds(2) || Duration(piece) < Time.FromSeconds(1.2)) &&
                Duration(merged[^1]) + Duration(piece) <= Time.FromSeconds(4.5))
                merged[^1].AddRange(piece);
            else merged.Add(piece);
        }
        return merged;
    }

    public static string Join(IEnumerable<Word> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words) sb.Append(sb.Length > 0 ? " " : "").Append(w.Text).Append(w.Trailing);
        return sb.ToString();
    }

    /// <summary>Recomputes slot times from their anchor words and re-times clips attached to slots.</summary>
    public static void RefreshSlots(Project p)
    {
        var words = TimelineWords(p);
        var index = words.Select((x, i) => (x.Word.Id, i)).ToDictionary(x => x.Id, x => x.i);
        p.Slots.RemoveAll(s => !index.ContainsKey(s.FirstWordId));
        p.Slots.Sort((a, b) => index[a.FirstWordId].CompareTo(index[b.FirstWordId]));
        SetSlotTimes(p, words, index);
        foreach (var slot in p.Slots) RetimeSlotClips(p, slot);
        RefreshIntroRegion(p);
        Cards.AutoTimeAll(p);
    }

    /// <summary>
    /// Slot times aren't saved (they follow the narration), so a loaded project computes them here without touching
    /// any clips. Slots whose anchor word is gone keep zero times until the next <see cref="RefreshSlots"/>.
    /// </summary>
    public static void TimeSlots(Project p)
    {
        if (p.Slots.Count == 0 || !p.Tracks.Any(t => t.Role == TrackRole.Narration)) return;
        var words = TimelineWords(p);
        SetSlotTimes(p, words, words.Select((x, i) => (x.Word.Id, i)).ToDictionary(x => x.Id, x => x.i));
    }

    static void SetSlotTimes(Project p, List<(Word Word, TimeRange TimeRange)> words, Dictionary<string, int> index)
    {
        long narrationEnd = p.Track(TrackRole.Narration).Clips.Select(c => c.End).DefaultIfEmpty(0).Max();
        var placed = p.Slots.Where(s => index.ContainsKey(s.FirstWordId)).ToList();
        for (int i = 0; i < placed.Count; i++)
        {
            var s = placed[i];
            s.Start = i == 0 ? 0 : words[index[s.FirstWordId]].TimeRange.Start;
            s.End = i + 1 < placed.Count ? words[index[placed[i + 1].FirstWordId]].TimeRange.Start : Math.Max(narrationEnd, s.Start + Time.Frame);
            if (index.TryGetValue(s.LastWordId, out var last) && last < index[s.FirstWordId]) s.LastWordId = s.FirstWordId;
        }
    }

    static void RetimeSlotClips(Project p, Slot slot)
    {
        foreach (var track in p.Tracks.Where(t => t.Kind == TrackKind.Video))
        {
            var clips = track.Clips.Where(c => c.SlotId == slot.Id).OrderBy(c => c.Start).ToList();
            if (clips.Count == 0) continue;
            long oldStart = clips[0].Start, oldSpan = Math.Max(1, clips[^1].End - oldStart);
            long newSpan = slot.End - slot.Start;
            if (oldStart == slot.Start && oldSpan == newSpan) continue;
            foreach (var c in clips)
            {
                long a = slot.Start + (c.Start - oldStart) * newSpan / oldSpan;
                long b = slot.Start + (c.End - oldStart) * newSpan / oldSpan;
                long delta = a - c.Start;
                foreach (var linked in TimelineOps.Linked(p, c)) { linked.Start += delta; linked.Duration = Math.Max(Time.Frame, b - a); }
                var media = p.FindMedia(c.MediaId);
                if (media is { Kind: MediaKind.Video, Duration: > 0 } && c.SourceIn + c.Duration > media.Duration)
                    foreach (var linked in TimelineOps.Linked(p, c)) linked.SourceIn = Math.Max(0, media.Duration - linked.Duration);
            }
        }
    }

    public static SlotStatus StatusOf(Project p, Slot slot)
    {
        var range = new TimeRange(slot.Start, slot.End);
        if (p.Track(TrackRole.Cards).Clips.Any(c => c.TimeRange.Overlaps(range) && c.TimeRange.Intersect(range).Duration > range.Duration / 2)) return SlotStatus.Card;
        var footage = p.Track(TrackRole.Footage).Clips.Where(c => c.TimeRange.Overlaps(range)).Sum(c => c.TimeRange.Intersect(range).Duration);
        if (footage >= range.Duration * 9 / 10) return SlotStatus.Footage;
        if (p.Track(TrackRole.Footage).Clips.Any(c => c.Kind == ClipKind.Card && c.TimeRange.Overlaps(range))) return SlotStatus.Card;
        return SlotStatus.Empty;
    }

    /// <summary>The intro ends where the first script heading is spoken (or at 30 s without headings).</summary>
    public static long IntroEnd(Project p)
    {
        var marker = p.Markers.FirstOrDefault(m => m.Label.Equals("Intro end", StringComparison.OrdinalIgnoreCase));
        if (marker is not null) return marker.Time;
        var section = p.Slots.FirstOrDefault(s => !s.IsIntro && s.SectionTitle is not null);
        if (section is not null) return section.Start;
        var duration = p.Track(TrackRole.Narration).Clips.Select(c => c.End).DefaultIfEmpty(0).Max();
        return Math.Min(duration, Time.FromSeconds(30));
    }

    public static void RefreshIntroRegion(Project p)
    {
        var intro = p.SubtitleRegions.FirstOrDefault(r => r.IsIntro);
        if (intro is null) return;
        intro.Start = 0;
        intro.End = IntroEnd(p);
    }

    /// <summary>Creates the default Intro subtitle region if the project has none.</summary>
    public static void EnsureIntroRegion(Project p, string presetId)
    {
        if (p.SubtitleRegions.Count > 0) return;
        p.SubtitleRegions.Add(new SubtitleRegion { IsIntro = true, Label = "Intro", PresetId = presetId, End = IntroEnd(p) });
    }

    /// <summary>Places a section-header card and section marker at each script heading.</summary>
    public static void PlaceSectionHeaders(Project p, long duration)
    {
        var cards = p.Track(TrackRole.Cards);
        cards.Clips.RemoveAll(c => c.Card?.Template == CardTemplateKind.SectionHeader);
        p.Markers.RemoveAll(m => m.IsSection);
        foreach (var group in p.Slots.Where(s => s.SectionTitle is not null && !s.IsIntro).GroupBy(s => s.SectionTitle))
        {
            var first = group.First();
            p.Markers.Add(new Marker { Time = first.Start, Label = first.SectionTitle!, IsSection = true });
            var clip = new Clip
            {
                Kind = ClipKind.Card, Start = first.Start, Duration = Math.Min(duration, Math.Max(Time.FromSeconds(1), first.End - first.Start)),
                Label = first.SectionTitle!,
                Card = new CardData { Template = CardTemplateKind.SectionHeader, TemplateId = "section", Heading = first.SectionTitle!, Reveal = RevealMode.AllAtOnce },
            };
            TimelineOps.ClearRange(p, cards, clip.TimeRange);
            cards.Clips.Add(clip);
        }
    }

    /// <summary>YouTube chapter list: "0:00 Intro" plus one line per section marker (YouTube needs 10 s+ chapters).</summary>
    public static string Chapters(Project p)
    {
        var sections = p.Markers.Where(m => m.IsSection).OrderBy(m => m.Time).ToList();
        if (sections.Count == 0) return "";
        var lines = new List<string>();
        long last = -Time.FromSeconds(10);
        // YouTube chapters must be at least 10 s; a very short intro folds into the first section.
        if (sections[0].Time >= Time.FromSeconds(10)) { lines.Add("0:00 Intro"); last = 0; }
        foreach (var m in sections)
        {
            if (m.Time - last < Time.FromSeconds(10) && lines.Count > 0) continue;
            lines.Add($"{(lines.Count == 0 ? "0:00" : Time.Chapter(m.Time))} {m.Label}");
            last = m.Time;
        }
        return string.Join("\n", lines);
    }

    public static string SafeFileName(string name) => Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", "_").Trim();
}
