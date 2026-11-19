using Cutroom.Core;

namespace Cutroom.App;

/// <summary>Narration-only playback in cleanup order, independent of the Edit timeline.</summary>
internal sealed class CleanPreview
{
    internal sealed record Passage(long Start, string Text);
    internal sealed record CutMarker(CleanupDecision Decision, long Position);

    public Project Audio { get; }
    public NarrationMap Map { get; }
    public long Duration { get; }
    public IReadOnlyList<Passage> Passages { get; }
    public IReadOnlyList<CutMarker> Markers { get; }

    public CleanPreview(Project project, CleanupDecision? audition = null, bool remove = false)
    {
        var narration = project.Narration;
        var decisions = narration.Decisions;
        if (audition is not null)
        {
            decisions = decisions.Select(d => d.Id == audition.Id
                ? new CleanupDecision { Id = d.Id, Kind = d.Kind, Start = d.Start, End = d.End, Applied = d.IsCut == remove }
                : d).ToList();
        }
        Audio = new Project
        {
            Media = project.FindMedia(narration.MediaId) is { } media ? [media] : [],
            Tracks = [new Track { Role = TrackRole.Narration, Kind = TrackKind.Audio }],
            Narration = new Narration
            {
                MediaId = narration.MediaId, SourceDuration = narration.SourceDuration, GainDb = narration.GainDb,
                Settings = narration.Settings, Decisions = decisions,
            },
        };
        Derive.RebuildNarration(Audio);
        Map = new NarrationMap(Audio);
        Duration = Audio.Duration;
        Passages = audition is null ? BuildPassages(narration) : [];
        Markers = audition is null
            ? decisions.Where(d => d.Removes).OrderBy(d => d.Start).Select(d => new CutMarker(d, Map.ToTimelineNearest(d.Start))).ToList()
            : [];
    }

    public TimeRange Around(CleanupDecision decision)
    {
        long pad = Time.FromSeconds(1.5);
        return new(Map.ToTimelineNearest(Math.Max(0, decision.Start - pad)), Map.ToTimelineNearest(decision.End + pad));
    }

    public string PassageAt(long position)
    {
        int low = 0, high = Passages.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Passages[middle].Start <= position) low = middle + 1;
            else high = middle;
        }
        return Passages.Count == 0 ? "" : Passages[Math.Max(0, low - 1)].Text;
    }

    List<Passage> BuildPassages(Narration narration)
    {
        var words = new List<Word>();
        var passages = new List<Passage>();
        long start = 0;
        foreach (var word in narration.Words.Where(w => w.Kept).OrderBy(w => w.Start))
        {
            if (Map.WordRange(word) is not { } range) continue;
            if (words.Count > 0)
            {
                var last = words[^1];
                bool newSentence = word.SentenceId is { } sentence && last.SentenceId is { } previous
                    ? sentence != previous
                    : last.Trailing.IndexOfAny(['.', '?', '!']) >= 0;
                if (newSentence || words.Count >= 40)
                {
                    passages.Add(new(start, Derive.Join(words)));
                    words.Clear();
                }
            }
            if (words.Count == 0) start = range.Start;
            words.Add(word);
        }
        if (words.Count > 0) passages.Add(new(start, Derive.Join(words)));
        return passages;
    }
}
