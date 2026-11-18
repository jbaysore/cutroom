using System.IO.MemoryMappedFiles;
using Cutroom.Core;

namespace Cutroom.Audio;

/// <summary>Random access to a cached 48 kHz float32 stereo file.</summary>
public sealed class AudioSource : IDisposable
{
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    public long Frames { get; }

    public AudioSource(string path)
    {
        var length = new FileInfo(path).Length;
        Frames = length / 8;
        file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
    }

    /// <summary>Reads stereo frames starting at <paramref name="frame"/>; out-of-range frames are silent.</summary>
    public void Read(long frame, Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        long first = Math.Max(0, frame), last = Math.Min(Frames, frame + frames);
        stereo.Clear();
        if (last <= first) return;
        int offset = (int)(first - frame) * 2;
        int count = (int)(last - first) * 2;
        var tmp = new float[count];
        view.ReadArray(first * 8, tmp, 0, count);
        tmp.CopyTo(stereo[offset..]);
    }

    public void Dispose()
    {
        view.Dispose();
        file.Dispose();
    }
}

/// <summary>
/// Mixes the project's audio tracks: narration, game, music and SFX, with track volume, clip gain,
/// equal-power fades, mute/solo and narration-driven auto-duck. Used for playback and export.
/// </summary>
public sealed class Mixer : IDisposable
{
    readonly Func<MediaRef, string?> audioPathFor;
    readonly Dictionary<string, AudioSource?> sources = [];
    Project? project;
    DuckEnvelope? duck;
    int preparedRevision = -1;
    readonly Lock gate = new();

    public Mixer(Func<MediaRef, string?> audioPathFor) => this.audioPathFor = audioPathFor;

    public void Prepare(Project p, int revision)
    {
        lock (gate)
        {
            if (ReferenceEquals(p, project) && revision == preparedRevision) return;
            project = p;
            preparedRevision = revision;
            duck = DuckEnvelope.For(p);
        }
    }

    AudioSource? Source(MediaRef m)
    {
        var path = audioPathFor(m);
        if (path is null) return null;
        if (sources.TryGetValue(path, out var s)) return s;
        try { s = File.Exists(path) ? new AudioSource(path) : null; }
        catch (IOException) { s = null; }
        if (s is not null) sources[path] = s;
        return s;
    }

    /// <summary>Renders stereo frames for timeline ticks [start, start + frames).</summary>
    public void Render(long start, Span<float> stereo)
    {
        stereo.Clear();
        lock (gate)
        {
            var p = project;
            if (p is null) return;
            int frames = stereo.Length / 2;
            var range = new TimeRange(start, start + frames);
            var audioTracks = p.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
            bool anySolo = audioTracks.Any(t => t.Solo);
            var scratch = new float[stereo.Length];
            foreach (var track in audioTracks)
            {
                if (track.Muted || anySolo && !track.Solo) continue;
                double trackGain = Gain.DbToLinear(track.VolumeDb);
                foreach (var clip in track.Clips)
                {
                    if (!clip.TimeRange.Overlaps(range)) continue;
                    var media = p.FindMedia(clip.MediaId);
                    if (media is null) continue;
                    var source = Source(media);
                    if (source is null) continue;
                    long a = Math.Max(range.Start, clip.Start), b = Math.Min(range.End, clip.End);
                    int offset = (int)(a - start), count = (int)(b - a);
                    var slice = scratch.AsSpan(offset * 2, count * 2);
                    source.Read(clip.SourceAt(a), slice);
                    double clipGain = trackGain * Gain.DbToLinear(clip.GainDb);
                    const int block = 64;
                    for (int i = 0; i < count; i += block)
                    {
                        long t = a + i;
                        double g = clipGain * Gain.Fade(clip, t) * (duck is null ? 1 : Gain.DbToLinear(duck.GainDb(track, t)));
                        int n = Math.Min(block, count - i);
                        for (int k = 0; k < n * 2; k++) stereo[(offset + i) * 2 + k] += (float)(slice[i * 2 + k] * g);
                    }
                }
            }
        }
    }

    /// <summary>Renders a whole range into a float array (export and tests).</summary>
    public float[] RenderRange(long start, long end)
    {
        var buffer = new float[(end - start) * 2];
        const int chunk = 48_000;
        for (long t = start; t < end; t += chunk)
        {
            int n = (int)Math.Min(chunk, end - t);
            Render(t, buffer.AsSpan((int)(t - start) * 2, n * 2));
        }
        return buffer;
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var s in sources.Values) s?.Dispose();
            sources.Clear();
        }
    }
}
