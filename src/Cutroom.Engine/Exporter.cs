using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Cutroom.Audio;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.Engine;

public sealed record ExportProgress(double Fraction, TimeSpan Elapsed, TimeSpan? Remaining, string Stage);

public sealed record ExportResult(string VideoPath, string? SrtPath, string? ChaptersPath, string Encoder);

/// <summary>
/// YouTube export: 1080p60 H.264 (AMD AMF, Media Foundation fallback), AAC 320 kbps, two-pass loudness to −14 LUFS
/// with a −1 dBTP ceiling, +faststart. Frames come from the same compositor as the preview.
/// </summary>
public static class Exporter
{
    public const string TargetLufs = "-14", TruePeak = "-1";

    /// <summary>Problems worth knowing before a long export.</summary>
    public static List<string> PreFlight(Project p, AppSettings settings)
    {
        var issues = new List<string>();
        foreach (var m in p.Media.Where(m => p.AllClips.Any(c => c.MediaId == m.Id || c.Card?.ContentMediaId == m.Id)))
            if (!File.Exists(m.Path)) issues.Add($"Missing media: {m.Path}");
        int empty = p.Slots.Count(s => Derive.StatusOf(p, s) == SlotStatus.Empty);
        if (empty > 0) issues.Add($"{empty} script slot{(empty == 1 ? " has" : "s have")} no footage or card yet.");
        foreach (var r in p.SubtitleRegions.Where(r => settings.SubtitlePresets.All(x => x.Id != r.PresetId)))
            issues.Add($"Subtitle region \"{r.Label}\" uses a preset that no longer exists (the default will be used).");
        if (p.Duration <= 0) issues.Add("The timeline is empty.");
        return issues;
    }

    public static async Task<ExportResult> Run(Project p, AppSettings settings, string output, Func<MediaRef, string?> audioPathFor,
        IProgress<ExportProgress>? progress, CancellationToken ct, int width = 1920, int height = 1080)
    {
        var started = Stopwatch.StartNew();
        long duration = Time.SnapToFrame(p.Duration);
        if (duration <= 0) throw new InvalidOperationException("The timeline is empty.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var work = Directory.CreateTempSubdirectory("cutroom-export").FullName;
        try
        {
            // 1. Mix audio and measure loudness (first loudnorm pass).
            progress?.Report(new ExportProgress(0, started.Elapsed, null, "Mixing audio"));
            var wav = Path.Combine(work, "mix.wav");
            using (var mixer = new Mixer(audioPathFor))
            {
                mixer.Prepare(p, -1);
                WriteFloatWav(wav, mixer, duration, ct);
            }
            var measured = await MeasureLoudness(wav, ct).ConfigureAwait(false);

            // 2. Render frames into the encoder.
            string[][] encoders =
            [
                ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "quality", "-profile:v", "high", "-rc", "vbr_peak", "-b:v", "40M", "-maxrate", "50M", "-bufsize", "80M", "-g", "120", "-bf", "0"],
                ["-c:v", "h264_mf", "-b:v", "40M", "-g", "120"],
            ];
            string loudnorm = $"loudnorm=I={TargetLufs}:TP={TruePeak}:LRA=11:measured_I={measured.I}:measured_TP={measured.TP}:measured_LRA={measured.LRA}:measured_thresh={measured.Thresh}:offset={measured.Offset}:linear=true,aresample=48000";
            Exception? last = null;
            foreach (var enc in encoders)
            {
                try
                {
                    await Encode(p, settings, output, wav, loudnorm, enc, duration, width, height, started, progress, ct).ConfigureAwait(false);
                    var (srt, chapters) = WriteSidecars(p, output);
                    progress?.Report(new ExportProgress(1, started.Elapsed, TimeSpan.Zero, "Done"));
                    return new ExportResult(output, srt, chapters, enc[1]);
                }
                catch (ToolException ex) { last = ex; }
            }
            throw last ?? new ToolException("Export failed.");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    static async Task Encode(Project p, AppSettings settings, string output, string wav, string loudnorm, string[] encoder, long duration,
        int width, int height, Stopwatch started, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var temp = output + ".partial.mp4";
        var psi = new ProcessStartInfo(Tools.Ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in (string[])[.. Tools.FfmpegBase, "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-r", "60", "-i", "pipe:0",
                     "-i", wav, "-map", "0:v:0", "-map", "1:a:0", .. encoder, "-pix_fmt", "nv12", "-r", "60",
                     "-af", loudnorm, "-c:a", "aac", "-b:a", "320k", "-movflags", "+faststart", "-shortest", "-f", "mp4", temp])
            psi.ArgumentList.Add(a);
        using var ffmpeg = Process.Start(psi) ?? throw new ToolException("Could not start ffmpeg.");
        var errors = new System.Text.StringBuilder();
        ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        ffmpeg.BeginErrorReadLine();

        // Rendering uses WPF text rasterization, which needs an STA thread.
        Exception? renderError = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var engine = new NativeEngine(width, height);
                using var composer = new Composer(engine, settings);
                composer.Prepare(p, -1);
                var frame = new byte[width * height * 4];
                var stdin = ffmpeg.StandardInput.BaseStream;
                long frames = duration / Time.Frame;
                for (long f = 0; f < frames; f++)
                {
                    ct.ThrowIfCancellationRequested();
                    composer.Draw(p, f * Time.Frame);
                    engine.Readback(frame);
                    stdin.Write(frame);
                    if (f % 30 == 0)
                    {
                        double fraction = (f + 1) / (double)frames;
                        var elapsed = started.Elapsed;
                        progress?.Report(new ExportProgress(fraction, elapsed, TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction)), "Rendering"));
                    }
                }
                stdin.Close();
            }
            catch (Exception ex) { renderError = ex; }
        }) { IsBackground = true, Name = "Cutroom export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await Task.Run(thread.Join, CancellationToken.None).ConfigureAwait(false);
        if (renderError is not null)
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(true); } catch (InvalidOperationException) { }
            if (File.Exists(temp)) File.Delete(temp);
            if (renderError is OperationCanceledException) throw renderError;
            if (renderError is IOException) throw new ToolException("Encoder stopped: " + errors);
            throw renderError;
        }
        await ffmpeg.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (ffmpeg.ExitCode != 0)
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw new ToolException("Encoder failed: " + errors);
        }
        File.Move(temp, output, true);
    }

    static void WriteFloatWav(string path, Mixer mixer, long duration, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        long dataBytes = duration * 8;
        w.Write("RIFF"u8); w.Write((uint)(36 + dataBytes)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)2); w.Write(48000); w.Write(48000 * 8); w.Write((short)8); w.Write((short)32);
        w.Write("data"u8); w.Write((uint)dataBytes);
        var buffer = new float[48000 * 2];
        for (long t = 0; t < duration; t += 48000)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(48000, duration - t);
            mixer.Render(t, buffer.AsSpan(0, n * 2));
            w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(0, n * 2)));
        }
    }

    sealed record Loudness(string I, string TP, string LRA, string Thresh, string Offset);

    static async Task<Loudness> MeasureLoudness(string wav, CancellationToken ct)
    {
        var log = new System.Text.StringBuilder();
        await Tools.Run(Tools.Ffmpeg, ["-nostdin", "-hide_banner", "-i", wav, "-af", $"loudnorm=I={TargetLufs}:TP={TruePeak}:LRA=11:print_format=json", "-f", "null", "-"],
            ct, onError: l => log.AppendLine(l)).ConfigureAwait(false);
        string Get(string key)
        {
            var m = Regex.Match(log.ToString(), $"\"{key}\"\\s*:\\s*\"([^\"]+)\"");
            var v = m.Success ? m.Groups[1].Value : "0";
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? v : key == "input_i" ? "-70" : "0";
        }
        return new Loudness(Get("input_i"), Get("input_tp"), Get("input_lra"), Get("input_thresh"), Get("target_offset"));
    }

    /// <summary>SRT for YouTube captions (whole video, regardless of visual regions) and the chapters text.</summary>
    static (string? Srt, string? Chapters) WriteSidecars(Project p, string output)
    {
        string? srt = null, chapters = null;
        var timings = Subtitles.Timings(p);
        if (timings.Count > 0)
        {
            srt = Path.ChangeExtension(output, ".srt");
            File.WriteAllText(srt, Subtitles.ToSrt(timings));
        }
        var text = Derive.Chapters(p);
        if (text.Length > 0)
        {
            chapters = Path.ChangeExtension(output, ".chapters.txt");
            File.WriteAllText(chapters, text);
        }
        return (srt, chapters);
    }

    /// <summary>Measured integrated loudness of a finished file (tests and the export summary).</summary>
    public static async Task<double> MeasureFile(string path, CancellationToken ct = default)
    {
        var log = new System.Text.StringBuilder();
        await Tools.Run(Tools.Ffmpeg, ["-nostdin", "-hide_banner", "-i", path, "-vn", "-af", "ebur128", "-f", "null", "-"], ct, onError: l => log.AppendLine(l)).ConfigureAwait(false);
        var matches = Regex.Matches(log.ToString(), @"I:\s+(-?[\d.]+) LUFS");
        return matches.Count > 0 ? double.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
    }
}
