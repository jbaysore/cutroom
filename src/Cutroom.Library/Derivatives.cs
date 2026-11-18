using Cutroom.Core;

namespace Cutroom.Library;

/// <summary>
/// Cached derivatives of source media, keyed by fingerprint: scrub-friendly proxies, decoded audio (48 kHz float
/// stereo), waveform peaks and filmstrip thumbnails. Sources are never modified.
/// </summary>
public static class Derivatives
{
    public const int PeakBlock = 480; // 10 ms per peak at 48 kHz

    public static string ProxyPath(MediaRef m) => Path.Combine(Tools.CacheDir("proxies"), m.Fingerprint + ".mp4");
    public static string AudioPath(string fingerprint) => Path.Combine(Tools.CacheDir("audio"), fingerprint + ".f32");
    public static string PeaksPath(string fingerprint) => Path.Combine(Tools.CacheDir("waveforms"), fingerprint + ".peaks");
    public static string StripPath(MediaRef m) => Path.Combine(Tools.CacheDir("thumbs"), m.Fingerprint + ".jpg");

    public static bool HasProxy(MediaRef m) => m.Kind == MediaKind.Video && File.Exists(ProxyPath(m));

    /// <summary>Preview path: the proxy when it exists, otherwise the original.</summary>
    public static string PreviewPath(MediaRef m) => HasProxy(m) ? ProxyPath(m) : m.Path;

    /// <summary>540p, short-GOP H.264 for instant scrubbing. Uses AMF, then Media Foundation, then MPEG-4 as fallbacks.</summary>
    public static async Task EnsureProxy(MediaRef m, IProgress<double>? progress, CancellationToken ct)
    {
        if (m.Kind != MediaKind.Video || m.Height <= 540 && m.Fps <= 61 && Path.GetExtension(m.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return;
        var target = ProxyPath(m);
        if (File.Exists(target)) return;
        var temp = target + ".partial.mp4";
        string[][] encoders =
        [
            ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "speed", "-rc", "cqp", "-qp_i", "24", "-qp_p", "26", "-g", "15", "-bf", "0"],
            ["-c:v", "h264_mf", "-b:v", "5M", "-g", "15"],
            ["-c:v", "mpeg4", "-q:v", "5", "-g", "15"],
        ];
        Exception? last = null;
        foreach (var enc in encoders)
        {
            try
            {
                await Tools.RunFfmpeg(["-i", m.Path, "-map", "0:v:0", "-vf", "scale=-2:540:flags=bilinear", .. enc, "-pix_fmt", "yuv420p", "-an", "-f", "mp4", temp],
                    Time.ToSeconds(m.Duration), progress, ct).ConfigureAwait(false);
                File.Move(temp, target, true);
                return;
            }
            catch (ToolException ex) { last = ex; }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        throw last ?? new ToolException("Could not make a proxy.");
    }

    /// <summary>Decodes the audio of a file to raw 48 kHz float32 stereo, used for playback, mixing and analysis.</summary>
    public static async Task<string> EnsureAudio(string path, string fingerprint, double duration, IProgress<double>? progress, CancellationToken ct)
    {
        var target = AudioPath(fingerprint);
        if (File.Exists(target)) return target;
        var temp = target + ".partial";
        try
        {
            await Tools.RunFfmpeg(["-i", path, "-vn", "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-f", "f32le", temp], duration, progress, ct).ConfigureAwait(false);
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }

    /// <summary>Waveform peaks: one byte (0..255 of full scale) per 10 ms block.</summary>
    public static string EnsurePeaks(string audioPath, string fingerprint)
    {
        var target = PeaksPath(fingerprint);
        if (File.Exists(target)) return target;
        using var reader = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var buffer = new byte[PeakBlock * 8];
        var peaks = new List<byte>((int)(reader.Length / buffer.Length) + 1);
        int read;
        while ((read = reader.ReadAtLeast(buffer, buffer.Length, false)) > 0)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, read - read % 4));
            float max = 0;
            foreach (var s in samples) max = Math.Max(max, Math.Abs(s));
            peaks.Add((byte)Math.Clamp(Math.Sqrt(Math.Min(1, max)) * 255, 0, 255));
        }
        var temp = target + ".partial";
        File.WriteAllBytes(temp, peaks.ToArray());
        File.Move(temp, target, true);
        return target;
    }

    public static byte[] LoadPeaks(string fingerprint)
    {
        var path = PeaksPath(fingerprint);
        return File.Exists(path) ? File.ReadAllBytes(path) : [];
    }

    /// <summary>A 10-frame filmstrip (each 192 px wide) used for hover previews and timeline thumbnails.</summary>
    public static async Task EnsureStrip(MediaRef m, CancellationToken ct)
    {
        if (m.Kind != MediaKind.Video) return;
        var target = StripPath(m);
        if (File.Exists(target)) return;
        double seconds = Math.Max(.1, Time.ToSeconds(m.Duration));
        var temp = target + ".partial.jpg";
        await Tools.Run(Tools.Ffmpeg, [.. Tools.FfmpegBase, "-i", PreviewPath(m), "-vf", $"fps={Tools.Num(10 / seconds)},scale=192:-2,tile=10x1", "-frames:v", "1", "-q:v", "4", temp], ct).ConfigureAwait(false);
        File.Move(temp, target, true);
    }
}

public sealed record JobInfo(string Name, double Progress, string? Error);

/// <summary>Background work (proxies, audio decode, cleanup) with progress for the job panel. Never blocks the UI.</summary>
public sealed class JobQueue
{
    sealed class Job
    {
        public required string Name;
        public double Progress;
        public string? Error;
    }

    readonly SemaphoreSlim heavy = new(2);
    readonly Lock gate = new();
    readonly List<Job> jobs = [];
    public event Action? Changed;
    public static JobQueue Shared { get; } = new();

    public IReadOnlyList<JobInfo> Snapshot()
    {
        lock (gate) return jobs.Select(j => new JobInfo(j.Name, j.Progress, j.Error)).ToList();
    }

    public async Task<T> Run<T>(string name, Func<IProgress<double>, CancellationToken, Task<T>> work, CancellationToken ct = default, bool background = false)
    {
        var job = new Job { Name = name };
        lock (gate) jobs.Add(job);
        Changed?.Invoke();
        var progress = new Progress<double>(p => { job.Progress = p; Changed?.Invoke(); });
        if (background) await heavy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(progress, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Error = ex.Message;
            job.Progress = 1;
            throw;
        }
        finally
        {
            if (background) heavy.Release();
            lock (gate) if (job.Error is null) jobs.Remove(job);
            Changed?.Invoke();
        }
    }

    public Task Run(string name, Func<IProgress<double>, CancellationToken, Task> work, CancellationToken ct = default, bool background = false) =>
        Run<bool>(name, async (p, c) => { await work(p, c).ConfigureAwait(false); return true; }, ct, background);

    public void DismissErrors()
    {
        lock (gate) jobs.RemoveAll(j => j.Error is not null);
        Changed?.Invoke();
    }
}
