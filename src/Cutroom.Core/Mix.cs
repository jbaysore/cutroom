namespace Cutroom.Core;

/// <summary>
/// Auto-duck: narration word spans are known exactly, so music and game audio duck ahead of speech
/// (attack ends as the first word starts) and recover after it. Short pauses stay ducked.
/// </summary>
public sealed class DuckEnvelope
{
    readonly List<TimeRange> speech;

    public IReadOnlyList<TimeRange> Speech => speech;

    public DuckEnvelope(IEnumerable<TimeRange> speechRanges, IEnumerable<NoDuckRange> noDuck, long holdGap)
    {
        var merged = new List<TimeRange>();
        foreach (var r in speechRanges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start - merged[^1].End < holdGap) merged[^1] = new TimeRange(merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        }
        // Remove no-duck ranges.
        foreach (var nd in noDuck)
        {
            var hole = new TimeRange(nd.Start, nd.End);
            var next = new List<TimeRange>();
            foreach (var r in merged)
            {
                if (!r.Overlaps(hole)) { next.Add(r); continue; }
                if (r.Start < hole.Start) next.Add(new TimeRange(r.Start, hole.Start));
                if (r.End > hole.End) next.Add(new TimeRange(hole.End, r.End));
            }
            merged = next;
        }
        speech = merged;
    }

    public static DuckEnvelope For(Project p) =>
        new(Derive.TimelineWords(p).Select(w => w.TimeRange), p.NoDuck, Time.FromMs(600));

    /// <summary>0 = not ducked, 1 = fully ducked.</summary>
    public double Amount(long t, long attack, long release)
    {
        double best = 0;
        // Binary search for the nearest ranges; only neighbours can influence t.
        int lo = 0, hi = speech.Count - 1, idx = speech.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (speech[mid].End + release >= t) { idx = mid; hi = mid - 1; } else lo = mid + 1;
        }
        for (int i = idx; i < speech.Count && i <= idx + 1; i++)
        {
            var r = speech[i];
            double a;
            if (t >= r.Start && t < r.End) a = 1;
            else if (t < r.Start) a = attack <= 0 ? 0 : Math.Clamp(1 - (r.Start - t) / (double)attack, 0, 1);
            else a = release <= 0 ? 0 : Math.Clamp(1 - (t - r.End) / (double)release, 0, 1);
            best = Math.Max(best, a);
        }
        return best;
    }

    public double GainDb(Track track, long t) =>
        track.DuckEnabled ? track.DuckDepthDb * Amount(t, Time.FromMs(track.DuckAttackMs), Time.FromMs(track.DuckReleaseMs)) : 0;
}

public static class Gain
{
    public static double DbToLinear(double db) => Math.Pow(10, db / 20);

    /// <summary>Clip fade multiplier (equal-power curve) at timeline time t.</summary>
    public static double Fade(Clip c, long t)
    {
        double g = 1;
        if (c.FadeIn > 0 && t < c.Start + c.FadeIn) g *= Math.Sin(Math.PI / 2 * Math.Clamp((t - c.Start) / (double)c.FadeIn, 0, 1));
        if (c.FadeOut > 0 && t > c.End - c.FadeOut) g *= Math.Sin(Math.PI / 2 * Math.Clamp((c.End - t) / (double)c.FadeOut, 0, 1));
        return g;
    }
}
