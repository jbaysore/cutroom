using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cutroom.Core;

namespace Cutroom.Library;

public sealed class ToolException(string message) : Exception(message);

/// <summary>Locations of bundled tools and the cache, plus a process runner with cancellation and progress.</summary>
public static class Tools
{
    public static string Root { get; set; } = AppContext.BaseDirectory;
    public static string Ffmpeg => Path.Combine(Root, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(Root, "ffprobe.exe");
    public static string Whisper => Path.Combine(Root, "whisper", "whisper-cli.exe");
    public static string DeepFilter => Path.Combine(Root, "tools", "deep-filter.exe");
    public static string RnnoiseModel => Path.Combine(Root, "tools", "sh.rnnn");

    public static string CacheRoot { get; set; } = Path.Combine(AppSettings.Folder, "cache");
    public static string CacheDir(string kind)
    {
        var dir = Path.Combine(CacheRoot, kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Runs a tool, returning stdout. Lines of stderr go to <paramref name="onError"/>; non-zero exit throws with the last error lines.</summary>
    public static async Task<string> Run(string exe, IEnumerable<string> args, CancellationToken ct = default,
        Action<string>? onOutput = null, Action<string>? onError = null, string? workingDirectory = null, IDictionary<string, string?>? env = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) { if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var tail = new Queue<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (stdout) stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail) { tail.Enqueue(e.Data); if (tail.Count > 12) tail.Dequeue(); }
            onError?.Invoke(e.Data);
        };
        if (!p.Start()) throw new ToolException($"Could not start {Path.GetFileName(exe)}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            p.StandardInput.Close();
        }
        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }))
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string detail;
            lock (tail) detail = string.Join("\n", tail);
            if (detail.Trim().Length == 0)
                lock (stdout) detail = stdout.Length > 600 ? stdout.ToString(stdout.Length - 600, 600) : stdout.ToString();
            throw new ToolException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}): {detail}");
        }
        lock (stdout) return stdout.ToString();
    }

    public static string[] FfmpegBase => ["-nostdin", "-hide_banner", "-loglevel", "error", "-y"];

    /// <summary>Runs ffmpeg with machine-readable progress reported as a 0..1 fraction of <paramref name="duration"/>.</summary>
    public static Task RunFfmpeg(IEnumerable<string> args, double duration, IProgress<double>? progress, CancellationToken ct) =>
        Run(Ffmpeg, FfmpegBase.Concat(["-progress", "pipe:1", "-nostats"]).Concat(args), ct, line =>
        {
            if (progress is null || duration <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
            if (long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                progress.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
        });

    public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A stable cache key: full path, size, mtime and hashes of the first and last megabyte.</summary>
    public static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        using var sha = SHA256.Create();
        var header = Encoding.UTF8.GetBytes($"{Path.GetFullPath(path).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        sha.TransformBlock(header, 0, header.Length, null, 0);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var buffer = new byte[Math.Min(1 << 20, info.Length)];
            int read = fs.Read(buffer, 0, buffer.Length);
            sha.TransformBlock(buffer, 0, read, null, 0);
            if (info.Length > buffer.Length)
            {
                fs.Seek(-buffer.Length, SeekOrigin.End);
                read = fs.Read(buffer, 0, buffer.Length);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..24].ToLowerInvariant();
    }
}

public static class MediaProbe
{
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
    static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma" };
    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".m4v", ".ts", ".wmv" };

    public static bool IsMedia(string path) => KindFromExtension(path) is not null;

    public static MediaKind? KindFromExtension(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        return null;
    }

    public static async Task<MediaRef> Probe(string path, CancellationToken ct = default)
    {
        var kind = KindFromExtension(path) ?? throw new ToolException($"{Path.GetFileName(path)} is not a supported media file.");
        var media = new MediaRef { Path = Path.GetFullPath(path), Kind = kind, Fingerprint = Tools.Fingerprint(path) };
        if (kind == MediaKind.Image)
        {
            var json = await Tools.Run(Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json", path], ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var s = doc.RootElement.GetProperty("streams")[0];
            media.Width = s.GetProperty("width").GetInt32();
            media.Height = s.GetProperty("height").GetInt32();
            media.Duration = Time.FromSeconds(5);
            return media;
        }
        var text = await Tools.Run(Tools.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,width,height,avg_frame_rate,r_frame_rate:format=duration", "-of", "json", path], ct).ConfigureAwait(false);
        using var probe = JsonDocument.Parse(text);
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.FirstOrDefault(x => x.GetProperty("codec_type").GetString() == "video");
        media.HasAudio = streams.Any(x => x.GetProperty("codec_type").GetString() == "audio");
        if (probe.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d) &&
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            media.Duration = Time.FromSeconds(seconds);
        if (kind == MediaKind.Video && video.ValueKind != JsonValueKind.Undefined)
        {
            media.Width = video.GetProperty("width").GetInt32();
            media.Height = video.GetProperty("height").GetInt32();
            media.Fps = ParseRate(video.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null)
                        ?? ParseRate(video.TryGetProperty("r_frame_rate", out var r) ? r.GetString() : null) ?? 60;
        }
        else if (kind == MediaKind.Video)
        {
            media.Kind = MediaKind.Audio; // e.g. an audio-only OBS recording in an .mkv
        }
        if (media.Duration <= 0) throw new ToolException($"{Path.GetFileName(path)} has no playable length.");
        return media;
    }

    static double? ParseRate(string? s)
    {
        if (s is null) return null;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var dd) && dd > 0 && n > 0)
            return n / dd;
        return null;
    }
}
