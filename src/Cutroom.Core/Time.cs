using System.Globalization;

namespace Cutroom.Core;

/// <summary>
/// All project time is integer ticks at 48 kHz, so audio cuts are sample-accurate.
/// At 60 fps one frame is exactly 800 ticks.
/// </summary>
public static class Time
{
    public const long Rate = 48_000;
    public const int Fps = 60;
    public const long Frame = Rate / Fps;

    public static long FromSeconds(double seconds) => (long)Math.Round(seconds * Rate);
    public static long FromMs(double ms) => (long)Math.Round(ms * Rate / 1000.0);
    public static double ToSeconds(long ticks) => ticks / (double)Rate;
    public static long SnapToFrame(long ticks) => (long)Math.Round(ticks / (double)Frame) * Frame;
    public static long FloorFrame(long ticks) => ticks >= 0 ? ticks / Frame * Frame : -((-ticks + Frame - 1) / Frame * Frame);
    public static long CeilingFrame(long ticks) => -FloorFrame(-ticks);

    /// <summary>HH:MM:SS:FF at 60 fps.</summary>
    public static string Timecode(long ticks)
    {
        if (ticks < 0) ticks = 0;
        long frames = ticks / Frame;
        long f = frames % Fps, s = frames / Fps;
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}:{f:00}");
    }

    /// <summary>M:SS or H:MM:SS, used for YouTube chapters.</summary>
    public static string Chapter(long ticks)
    {
        long s = Math.Max(0, ticks) / Rate;
        return s >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
    }

    public static string Srt(long ticks)
    {
        long ms = Math.Max(0, ticks) * 1000 / Rate;
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    public static string Short(long ticks) => string.Create(CultureInfo.InvariantCulture, $"{ToSeconds(ticks):0.0}s");
}

public readonly record struct TimeRange(long Start, long End)
{
    public long Duration => End - Start;
    public bool Contains(long t) => t >= Start && t < End;
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;
    public TimeRange Intersect(TimeRange other) => new(Math.Max(Start, other.Start), Math.Min(End, other.End));
    public static TimeRange FromDuration(long start, long duration) => new(start, start + duration);
}
