using System.Text.Json.Serialization;

namespace Cutroom.Core;

public sealed class Word
{
    public string Id { get; set; } = Ids.New();
    /// <summary>Display text, with spelling taken from the script when the match is confident.</summary>
    public string Text { get; set; } = "";
    public string Norm { get; set; } = "";
    /// <summary>Source-time range in the narration recording.</summary>
    public long Start { get; set; }
    public long End { get; set; }
    public double Confidence { get; set; } = 1;
    public string? SentenceId { get; set; }
    public int ScriptTokenIndex { get; set; } = -1;
    /// <summary>Punctuation that followed the word in the transcript (".", ",", "?").</summary>
    public string Trailing { get; set; } = "";
    /// <summary>False when a retake/false-start decision removed this word.</summary>
    public bool Kept { get; set; } = true;
    [JsonIgnore] public TimeRange TimeRange => new(Start, End);
}

public enum DecisionKind { Silence, Breath, MouthNoise, Retake, FalseStart, AdLibKept, PossibleSpeechKept }

public sealed class CleanupDecision
{
    public string Id { get; set; } = Ids.New();
    public DecisionKind Kind { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public double Confidence { get; set; }
    /// <summary>True = the cut is in effect (or, for "kept" kinds, the region is kept).</summary>
    public bool Applied { get; set; } = true;
    public string Reason { get; set; } = "";
    public string Context { get; set; } = "";
    /// <summary>Words covered by a retake/false-start, so toggling restores them.</summary>
    public List<string> WordIds { get; set; } = [];

    [JsonIgnore] public bool IsCut => Kind is not (DecisionKind.AdLibKept or DecisionKind.PossibleSpeechKept);
    [JsonIgnore] public TimeRange TimeRange => new(Start, End);
}

public enum Denoiser { DeepFilterNet, RNNoise, Off }

public sealed class CleanupSettings
{
    public double SentenceGapMs { get; set; } = 200;
    public double ParagraphGapMs { get; set; } = 400;
    public double CommaGapMs { get; set; } = 120;
    public double PaddingMs { get; set; } = 30;
    public double CrossfadeMs { get; set; } = 8;
    public double TakeCoverage { get; set; } = .9;
    public double VadThreshold { get; set; } = .5;
    public double VadMinMs { get; set; } = 120;
    public double TargetLufs { get; set; } = -16;
    public Denoiser Denoiser { get; set; } = Denoiser.DeepFilterNet;
}

public sealed class Narration
{
    public string? MediaId { get; set; }
    /// <summary>Denoised, loudness-matched derivative actually played on A1.</summary>
    public string? CleanPath { get; set; }
    public double GainDb { get; set; }
    public List<Word> Words { get; set; } = [];
    public List<CleanupDecision> Decisions { get; set; } = [];
    public List<long> RetakeMarks { get; set; } = [];
    public CleanupSettings Settings { get; set; } = new();
    public bool Processed { get; set; }
    public long SourceDuration { get; set; }

    public Word? FindWord(string id) => Words.FirstOrDefault(w => w.Id == id);

    /// <summary>Source ranges that survive all applied cuts, in order.</summary>
    public List<TimeRange> KeptSpans()
    {
        var cuts = Decisions.Where(d => d.IsCut && d.Applied).Select(d => d.TimeRange).OrderBy(r => r.Start).ToList();
        var spans = new List<TimeRange>();
        long cursor = 0;
        foreach (var cut in cuts)
        {
            if (cut.Start > cursor) spans.Add(new TimeRange(cursor, cut.Start));
            cursor = Math.Max(cursor, cut.End);
        }
        if (SourceDuration > cursor) spans.Add(new TimeRange(cursor, SourceDuration));
        return spans.Where(s => s.Duration >= Time.FromMs(4)).ToList();
    }

    /// <summary>Recompute <see cref="Word.Kept"/> from retake/false-start decisions.</summary>
    public void RefreshKeptWords()
    {
        var removed = Decisions.Where(d => d.Applied && d.Kind is DecisionKind.Retake or DecisionKind.FalseStart)
            .SelectMany(d => d.WordIds).ToHashSet();
        foreach (var w in Words) w.Kept = !removed.Contains(w.Id);
    }
}

/// <summary>Maps narration source time to timeline time through the clips on the narration track.</summary>
public sealed class NarrationMap
{
    readonly List<(long SourceStart, long SourceEnd, long TimelineStart)> segments;

    public NarrationMap(Project project)
    {
        var track = project.Tracks.FirstOrDefault(t => t.Role == TrackRole.Narration);
        segments = track is null ? [] : track.Clips.Where(c => c.MediaId == project.Narration.MediaId)
            .OrderBy(c => c.Start).Select(c => (c.SourceIn, c.SourceIn + c.Duration, c.Start)).ToList();
    }

    public bool IsEmpty => segments.Count == 0;

    /// <summary>Timeline time of a source instant, or null when that instant was cut.</summary>
    public long? ToTimeline(long source)
    {
        foreach (var s in segments)
            if (source >= s.SourceStart && source < s.SourceEnd) return s.TimelineStart + (source - s.SourceStart);
        return null;
    }

    /// <summary>Like <see cref="ToTimeline"/> but snaps cut instants to the next kept instant.</summary>
    public long ToTimelineNearest(long source)
    {
        foreach (var s in segments)
        {
            if (source < s.SourceStart) return s.TimelineStart;
            if (source < s.SourceEnd) return s.TimelineStart + (source - s.SourceStart);
        }
        return segments.Count > 0 ? segments[^1].TimelineStart + (segments[^1].SourceEnd - segments[^1].SourceStart) : 0;
    }

    public long? ToSource(long timeline)
    {
        foreach (var s in segments)
        {
            long end = s.TimelineStart + (s.SourceEnd - s.SourceStart);
            if (timeline >= s.TimelineStart && timeline < end) return s.SourceStart + (timeline - s.TimelineStart);
        }
        return null;
    }

    public TimeRange? WordRange(Word w)
    {
        var a = ToTimeline(w.Start) ?? ToTimeline(Math.Max(w.Start, w.End - 1));
        if (a is null) return null;
        var b = ToTimelineNearest(w.End);
        return new TimeRange(a.Value, Math.Max(a.Value + 1, b));
    }
}
