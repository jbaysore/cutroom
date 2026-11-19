using System.Text.Json;
using System.Text.RegularExpressions;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.Speech;

public sealed record RawWord(string Text, double Start, double End, double Probability, string Trailing);

/// <summary>Local speech-to-text with whisper.cpp. Keeps disfluencies and repeated phrases (needed to find retakes).</summary>
public static class Transcriber
{
    /// <summary>Performance-core count: whisper on the 13600K is ~4x faster on 6 P-cores than on all 20 threads.</summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 3, 4, 8);

    /// <summary>Use the Vulkan GPU build when it is bundled (Settings → Narration). It falls back to the CPU build on any failure.</summary>
    public static bool PreferGpu { get; set; } = true;
    static bool gpuFailed;

    /// <summary>True when the next transcription will run on the GPU.</summary>
    public static bool UsesGpu => PreferGpu && !gpuFailed && File.Exists(Tools.WhisperGpu);

    public static async Task<List<RawWord>> Transcribe(string wav16k, string modelPath, IReadOnlyList<string> vocabulary, IProgress<double>? progress, CancellationToken ct)
    {
        var outBase = Path.Combine(Path.GetTempPath(), "cutroom-whisper-" + Guid.NewGuid().ToString("N"));
        // Whisper's timestamps drift on the last words when a recording stops right after speech; two seconds of
        // trailing silence keeps them anchored.
        var padded = outBase + "-padded.wav";
        await Tools.Run(Tools.Ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav16k, "-af", "apad=pad_dur=2", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", padded], ct).ConfigureAwait(false);
        var args = new List<string> { "-m", modelPath, "-f", padded, "-l", "en", "-ojf", "-of", outBase, "-t", Threads.ToString(),
            "-bs", "2", "-bo", "2", "-mc", "64", "-pp", "-np" };
        if (vocabulary.Count > 0)
        {
            // Names only (not sentences): a sentence prompt biases whisper toward misreading short words as names.
            args.Add("--prompt");
            args.Add("Glossary: " + string.Join(", ", vocabulary.Take(40)) + ".");
        }
        Task Run(string exe) => Tools.Run(exe, args, ct, onError: line =>
        {
            var m = Regex.Match(line, @"progress\s*=\s*(\d+)%");
            if (m.Success) progress?.Report(int.Parse(m.Groups[1].Value) / 100.0);
        });
        async Task<List<RawWord>> Attempt(string exe)
        {
            await Run(exe).ConfigureAwait(false);
            return Parse(await File.ReadAllTextAsync(outBase + ".json", ct).ConfigureAwait(false));
        }
        try
        {
            if (UsesGpu)
            {
                try { return await Attempt(Tools.WhisperGpu).ConfigureAwait(false); }
                catch (Exception ex) when (ex is ToolException or System.ComponentModel.Win32Exception or IOException or JsonException)
                {
                    // Driver or device trouble: remember it for this session and redo the work on the CPU.
                    gpuFailed = true;
                    progress?.Report(0);
                }
            }
            return await Attempt(Tools.Whisper).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(outBase + ".json")) File.Delete(outBase + ".json");
            if (File.Exists(padded)) File.Delete(padded);
        }
    }

    static readonly Regex PunctuationOnly = new(@"^[\s.,!?;:…""'”“’)\-—]+$", RegexOptions.Compiled);

    /// <summary>Merges whisper tokens into words with trailing punctuation.</summary>
    public static List<RawWord> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var words = new List<RawWord>();
        string text = "", trailing = "";
        double start = 0, end = 0, minP = 1;
        void Flush()
        {
            var t = text.Trim();
            if (t.Length > 0) words.Add(new RawWord(t, start, Math.Max(end, start + .02), minP, trailing.Trim()));
            text = trailing = "";
            minP = 1;
        }
        foreach (var segment in doc.RootElement.GetProperty("transcription").EnumerateArray())
        {
            if (!segment.TryGetProperty("tokens", out var tokens)) continue;
            foreach (var token in tokens.EnumerateArray())
            {
                var t = token.GetProperty("text").GetString() ?? "";
                if (t.StartsWith("[_", StringComparison.Ordinal) || t.StartsWith("<|", StringComparison.Ordinal)) continue;
                var offsets = token.GetProperty("offsets");
                double from = offsets.GetProperty("from").GetInt32() / 1000.0, to = offsets.GetProperty("to").GetInt32() / 1000.0;
                double p = token.TryGetProperty("p", out var pp) ? pp.GetDouble() : 1;
                if (PunctuationOnly.IsMatch(t))
                {
                    trailing += t.Trim();
                    continue;
                }
                bool newWord = t.StartsWith(' ') || text.Length == 0 || trailing.Length > 0 && !t.StartsWith('\'');
                if (newWord)
                {
                    Flush();
                    start = from;
                }
                text += t;
                end = to;
                minP = Math.Min(minP, p);
            }
            Flush();
        }
        // Split leading punctuation (quotes) off words, keep hyphenated words whole.
        return words.Select(w => w with { Text = w.Text.Trim('"', '“', '”', '(', ')') }).Where(w => w.Text.Length > 0).ToList();
    }
}
