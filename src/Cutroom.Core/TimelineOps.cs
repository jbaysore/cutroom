namespace Cutroom.Core;

/// <summary>Timeline editing primitives. All operate on a project inside <see cref="Document.Edit"/>.</summary>
public static class TimelineOps
{
    /// <summary>Splits every clip that spans <paramref name="t"/> on the given tracks (all when null). Returns the new right-hand clips.</summary>
    public static List<Clip> Blade(Project p, long t, IEnumerable<Track>? tracks = null)
    {
        var created = new List<Clip>();
        foreach (var track in tracks ?? p.Tracks)
        {
            if (track.Role == TrackRole.Subtitles) continue;
            foreach (var clip in track.Clips.Where(c => t > c.Start && t < c.End).ToList())
            {
                var right = Split(clip, t);
                track.Clips.Add(right);
                created.Add(right);
            }
        }
        RelinkSplits(p, created);
        return created;
    }

    static Clip Split(Clip clip, long t)
    {
        var right = Clone(clip);
        right.Id = Ids.New();
        long left = t - clip.Start;
        right.Start = t;
        right.Duration = clip.Duration - left;
        right.SourceIn = clip.SourceIn + left;
        right.FadeIn = 0;
        right.TransitionIn = null;
        right.Effects = clip.Effects.Where(e => e.Offset >= left).Select(e => { var c = CloneEffect(e); c.Offset -= left; return c; }).ToList();
        clip.Effects = clip.Effects.Where(e => e.Offset < left).ToList();
        clip.Duration = left;
        clip.FadeOut = 0;
        return right;
    }

    /// <summary>Right halves of linked clips get a fresh shared link id so they stay linked to each other.</summary>
    static void RelinkSplits(Project p, List<Clip> created)
    {
        foreach (var group in created.Where(c => c.LinkId is not null).GroupBy(c => (c.LinkId, c.Start)))
        {
            var link = Ids.New();
            foreach (var c in group) c.LinkId = link;
        }
    }

    public static Clip Clone(Clip c)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(c, ProjectJson.Options);
        return System.Text.Json.JsonSerializer.Deserialize<Clip>(bytes, ProjectJson.Options)!;
    }

    static Effect CloneEffect(Effect e)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e, ProjectJson.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Effect>(bytes, ProjectJson.Options)!;
        copy.Id = Ids.New();
        return copy;
    }

    /// <summary>Clips that move with <paramref name="clip"/> (same link id), including itself.</summary>
    public static List<Clip> Linked(Project p, Clip clip) =>
        clip.LinkId is null ? [clip] : p.AllClips.Where(c => c.LinkId == clip.LinkId).ToList();

    /// <summary>Removes clips and closes the gap on their own tracks.</summary>
    public static void RippleDelete(Project p, IEnumerable<Clip> clips)
    {
        foreach (var clip in clips.SelectMany(c => Linked(p, c)).Distinct().OrderByDescending(c => c.Start).ToList())
        {
            var track = p.TrackOf(clip);
            if (track is null) continue;
            track.Clips.Remove(clip);
            foreach (var later in track.Clips.Where(c => c.Start >= clip.End)) later.Start -= clip.Duration;
            RemovePaired(p, clip);
        }
    }

    /// <summary>Removes clips and leaves a gap.</summary>
    public static void Delete(Project p, IEnumerable<Clip> clips)
    {
        foreach (var clip in clips.SelectMany(c => Linked(p, c)).Distinct().ToList())
        {
            p.TrackOf(clip)?.Clips.Remove(clip);
            RemovePaired(p, clip);
        }
    }

    static void RemovePaired(Project p, Clip clip)
    {
        foreach (var t in p.Tracks) t.Clips.RemoveAll(c => c.PairedWith == clip.Id);
    }

    /// <summary>Removes a time range from every track and pulls everything after it left.</summary>
    public static void RippleDeleteRange(Project p, TimeRange range, IEnumerable<Track>? tracks = null)
    {
        if (range.Duration <= 0) return;
        var list = (tracks ?? p.Tracks.Where(t => t.Role != TrackRole.Subtitles)).ToList();
        Blade(p, range.Start, list);
        Blade(p, range.End, list);
        foreach (var track in list)
        {
            track.Clips.RemoveAll(c => c.Start >= range.Start && c.End <= range.End);
            foreach (var c in track.Clips.Where(c => c.Start >= range.End)) c.Start -= range.Duration;
        }
        foreach (var m in p.Markers.Where(m => m.Time >= range.End)) m.Time -= range.Duration;
    }

    /// <summary>Places a clip, overwriting whatever it covers on the track.</summary>
    public static void Overwrite(Project p, Track track, Clip clip)
    {
        ClearRange(p, track, clip.TimeRange);
        track.Clips.Add(clip);
    }

    /// <summary>Removes material in a range on one track, trimming clips that straddle it.</summary>
    public static void ClearRange(Project p, Track track, TimeRange range)
    {
        Blade(p, range.Start, [track]);
        Blade(p, range.End, [track]);
        foreach (var removed in track.Clips.Where(c => c.Start >= range.Start && c.End <= range.End).ToList())
        {
            track.Clips.Remove(removed);
            RemovePaired(p, removed);
        }
    }

    /// <summary>Inserts a clip at its start, pushing later clips on that track right.</summary>
    public static void Insert(Project p, Track track, Clip clip)
    {
        Blade(p, clip.Start, [track]);
        foreach (var c in track.Clips.Where(c => c.Start >= clip.Start)) c.Start += clip.Duration;
        track.Clips.Add(clip);
    }

    /// <summary>Moves a clip's start edge (positive delta trims in), keeping its end fixed.</summary>
    public static void TrimStart(Clip clip, long delta, long minDuration = Time.Frame)
    {
        delta = Math.Clamp(delta, -clip.SourceIn, clip.Duration - minDuration);
        clip.Start += delta;
        clip.SourceIn += delta;
        clip.Duration -= delta;
    }

    public static void TrimEnd(Clip clip, long delta, MediaRef? media, long minDuration = Time.Frame)
    {
        long max = media is { Kind: not MediaKind.Image, Duration: > 0 } ? media.Duration - clip.SourceIn : long.MaxValue;
        clip.Duration = Math.Clamp(clip.Duration + delta, minDuration, max);
    }

    /// <summary>Changes which part of the source plays without moving the clip.</summary>
    public static void Slip(Clip clip, long delta, MediaRef? media)
    {
        long max = media is { Kind: not MediaKind.Image, Duration: > 0 } ? Math.Max(0, media.Duration - clip.Duration) : long.MaxValue;
        clip.SourceIn = Math.Clamp(clip.SourceIn + delta, 0, max);
    }

    public static void Move(Project p, Clip clip, long newStart, Track? toTrack = null)
    {
        long delta = Math.Max(-clip.Start, newStart - clip.Start);
        foreach (var c in Linked(p, clip)) c.Start += delta;
        foreach (var paired in p.AllClips.Where(c => c.PairedWith == clip.Id)) paired.Start += delta;
        if (toTrack is not null)
        {
            var from = p.TrackOf(clip);
            if (from is not null && from != toTrack && from.Kind == toTrack.Kind)
            {
                from.Clips.Remove(clip);
                toTrack.Clips.Add(clip);
            }
        }
    }

    /// <summary>Clip edges across all tracks plus markers, sorted and distinct.</summary>
    public static List<long> EditPoints(Project p)
    {
        var points = new SortedSet<long> { 0 };
        foreach (var c in p.AllClips) { points.Add(c.Start); points.Add(c.End); }
        foreach (var m in p.Markers) points.Add(m.Time);
        return points.ToList();
    }

    public static long NextEdit(Project p, long t) => EditPoints(p).FirstOrDefault(x => x > t, p.Duration);
    public static long PreviousEdit(Project p, long t) => EditPoints(p).LastOrDefault(x => x < t, 0);

    /// <summary>Snaps to the nearest edit point or playhead within <paramref name="tolerance"/>.</summary>
    public static long Snap(Project p, long t, long tolerance, long? playhead = null, Clip? ignore = null)
    {
        var candidates = p.AllClips.Where(c => c != ignore).SelectMany(c => new[] { c.Start, c.End })
            .Concat(p.Markers.Select(m => m.Time)).Append(0L);
        if (playhead is not null) candidates = candidates.Append(playhead.Value);
        long best = t, bestDistance = tolerance + 1;
        foreach (var c in candidates)
        {
            long d = Math.Abs(c - t);
            if (d < bestDistance) { best = c; bestDistance = d; }
        }
        return bestDistance <= tolerance ? best : t;
    }

    /// <summary>Adds the default (or given) transition at the edit point nearest to t on video tracks.</summary>
    public static Clip? AddTransition(Project p, long t, TransitionKind kind = TransitionKind.CrossDissolve)
    {
        var target = p.Tracks.Where(tr => tr.Kind == TrackKind.Video).SelectMany(tr => tr.Clips)
            .Where(c => c.Start > 0 || c.Kind == ClipKind.Card).OrderBy(c => Math.Abs(c.Start - t)).FirstOrDefault();
        if (target is null) return null;
        target.TransitionIn = new Transition { Kind = kind };
        return target;
    }
}
