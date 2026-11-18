using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.Speech;

public static class Wav
{
    /// <summary>Reads a 16-bit PCM or 32-bit float WAV (first channel) into floats.</summary>
    public static float[] ReadMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12, channels = 1, bits = 16, format = 1;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(bytes, pos + 8);
                channels = BitConverter.ToInt16(bytes, pos + 10);
                bits = BitConverter.ToInt16(bytes, pos + 22);
                if (format == unchecked((short)0xFFFE)) format = BitConverter.ToInt16(bytes, pos + 32);
            }
            else if (id == "data")
            {
                int frameBytes = bits / 8 * channels;
                int frames = Math.Min(size, bytes.Length - pos - 8) / frameBytes;
                var result = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    int o = pos + 8 + i * frameBytes;
                    result[i] = format == 3 ? BitConverter.ToSingle(bytes, o)
                        : bits == 16 ? BitConverter.ToInt16(bytes, o) / 32768f
                        : bits == 24 ? ((bytes[o] | bytes[o + 1] << 8 | (sbyte)bytes[o + 2] << 16)) / 8388608f
                        : BitConverter.ToInt32(bytes, o) / 2147483648f;
                }
                return result;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"{Path.GetFileName(path)} has no audio data.");
    }
}

public sealed record CleanupStage(string Name, double Progress);

public sealed class CleanupResult
{
    public required string CleanPath;
    public required double GainDb;
    public required long Duration;
    public required List<Word> Words;
    public required List<CleanupDecision> Decisions;
}

/// <summary>
/// The narration cleanup pipeline: decode, denoise, transcribe, align, detect speech, plan cuts and match loudness.
/// Every expensive stage is cached by source fingerprint and settings, so reruns after a settings change are fast.
/// </summary>
public static class CleanupEngine
{
    static string Cache(string name) => Path.Combine(Tools.CacheDir("speech"), name);

    public static async Task<CleanupResult> Run(MediaRef narration, Script? script, IReadOnlyList<long> retakeMarks, CleanupSettings settings,
        string whisperModel, IProgress<CleanupStage>? progress, CancellationToken ct)
    {
        void Report(string stage, double p) => progress?.Report(new CleanupStage(stage, p));
        var fp = narration.Fingerprint.Length > 0 ? narration.Fingerprint : Tools.Fingerprint(narration.Path);
        double seconds = Time.ToSeconds(narration.Duration);

        Report("Downloading speech models (first run only)", 0);
        await Models.Ensure(Models.Required(whisperModel), new Progress<double>(p => Report("Downloading speech models (first run only)", p)), ct).ConfigureAwait(false);

        // 1. Decode to 48 kHz mono.
        var raw48 = Cache($"{fp}-48k.wav");
        if (!File.Exists(raw48))
        {
            Report("Reading the recording", 0);
            await Convert(narration.Path, raw48, 48000, seconds, p => Report("Reading the recording", p), ct).ConfigureAwait(false);
        }

        // 2. Denoise (cached per denoiser).
        var clean = Cache($"{fp}-clean-{settings.Denoiser}.wav");
        if (!File.Exists(clean))
        {
            Report("Removing background noise", 0);
            await Denoise(raw48, clean, settings.Denoiser, seconds, p => Report("Removing background noise", p), ct).ConfigureAwait(false);
        }

        // 3. 16 kHz copy for the speech models.
        var wav16 = Cache($"{fp}-clean-{settings.Denoiser}-16k.wav");
        if (!File.Exists(wav16)) await Convert(clean, wav16, 16000, seconds, null, ct).ConfigureAwait(false);
        var audio = Wav.ReadMono(wav16);
        long duration = Time.FromSeconds(audio.Length / 16000.0);

        // 4. Transcribe.
        var vocabulary = script?.Vocabulary() ?? [];
        var wordsCache = Cache($"{fp}-{settings.Denoiser}-{Path.GetFileNameWithoutExtension(whisperModel)}-{Hash(string.Join(",", vocabulary))}.words.json");
        List<RawWord> raw;
        if (File.Exists(wordsCache)) raw = JsonSerializer.Deserialize<List<RawWord>>(await File.ReadAllTextAsync(wordsCache, ct).ConfigureAwait(false))!;
        else
        {
            Report("Transcribing", 0);
            raw = await Transcriber.Transcribe(wav16, Path.Combine(Models.Folder, whisperModel), vocabulary, new Progress<double>(p => Report("Transcribing", p)), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(wordsCache, JsonSerializer.Serialize(raw), ct).ConfigureAwait(false);
        }

        // 5. Refine word edges with forced alignment; 6. speech probabilities.
        Report("Finding exact word edges", 0);
        using var aligner = new CtcAligner(Models.PathOf(Models.Wav2Vec2), Models.PathOf(Models.Wav2Vec2Vocab));
        var words = Refine(raw, audio, aligner, new Progress<double>(p => Report("Finding exact word edges", p)), ct);
        foreach (var w in words)
        {
            w.End = Math.Min(w.End, duration);
            w.Start = Math.Min(w.Start, Math.Max(0, w.End - Time.FromMs(10)));
        }

        var vadCache = Cache($"{fp}-{settings.Denoiser}.vad");
        float[] vad;
        if (File.Exists(vadCache)) vad = MemoryMarshalFloats(await File.ReadAllBytesAsync(vadCache, ct).ConfigureAwait(false));
        else
        {
            Report("Checking for missed speech", 0);
            using var v = new Vad(Models.PathOf(Models.SileroVad));
            vad = v.Probabilities(audio, new Progress<double>(p => Report("Checking for missed speech", p)));
            await File.WriteAllBytesAsync(vadCache, System.Runtime.InteropServices.MemoryMarshal.AsBytes(vad.AsSpan()).ToArray(), ct).ConfigureAwait(false);
        }

        // 7. Plan.
        Report("Planning cuts", 0);
        var features = AudioFeatures.From(audio);
        string Verbatim(long a, long b)
        {
            int s0 = (int)Math.Clamp(Time.ToSeconds(a) * 16000, 0, audio.Length), s1 = (int)Math.Clamp(Time.ToSeconds(b) * 16000, s0, audio.Length);
            if (s1 - s0 < 3200) return "";
            return aligner.Greedy(aligner.Emissions(audio.AsSpan(s0, s1 - s0)));
        }
        var decisions = CleanupPlanner.Plan(new PlannerInput(words, script, vad, features, retakeMarks, settings, duration, Verbatim));

        // 8. Loudness to the narration target.
        Report("Matching loudness", 0);
        double gain = settings.TargetLufs - await IntegratedLoudness(clean, ct).ConfigureAwait(false);
        Report("Done", 1);
        return new CleanupResult { CleanPath = clean, GainDb = Math.Clamp(gain, -20, 30), Duration = duration, Words = words, Decisions = decisions };
    }

    static float[] MemoryMarshalFloats(byte[] bytes) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    static string Hash(string s) => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];

    static Task Convert(string input, string output, int rate, double seconds, Action<double>? progress, CancellationToken ct) =>
        Atomic(output, temp => Tools.RunFfmpeg(["-i", input, "-vn", "-map", "0:a:0", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "wav", temp],
            seconds, progress is null ? null : new Progress<double>(progress), ct));

    static async Task Atomic(string target, Func<string, Task> write)
    {
        var temp = target + ".partial.wav";
        try
        {
            await write(temp).ConfigureAwait(false);
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    static async Task Denoise(string input, string output, Denoiser denoiser, double seconds, Action<double> progress, CancellationToken ct)
    {
        if (denoiser == Denoiser.DeepFilterNet && File.Exists(Tools.DeepFilter))
        {
            var dir = Path.Combine(Path.GetTempPath(), "cutroom-df-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // Keep a little of the room (attenuation limit) so speech never sounds gated or underwater.
                await Tools.Run(Tools.DeepFilter, ["--compensate-delay", "--atten-lim-db", "30", "-o", dir, input], ct).ConfigureAwait(false);
                var produced = Directory.GetFiles(dir, "*.wav").FirstOrDefault() ?? throw new ToolException("DeepFilterNet produced no output.");
                await Convert(produced, output, 48000, seconds, null, ct).ConfigureAwait(false);
                progress(1);
                return;
            }
            catch (ToolException) { /* fall back to RNNoise below */ }
            finally { Directory.Delete(dir, true); }
        }
        if (denoiser != Denoiser.Off && File.Exists(Tools.RnnoiseModel))
        {
            var model = Tools.RnnoiseModel.Replace('\\', '/').Replace(":", "\\\\:");
            await Atomic(output, temp => Tools.RunFfmpeg(["-i", input, "-af", $"arnndn=m='{model}':mix=0.85", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", temp],
                seconds, new Progress<double>(progress), ct)).ConfigureAwait(false);
            return;
        }
        File.Copy(input, output, true);
    }

    public static async Task<double> IntegratedLoudness(string path, CancellationToken ct)
    {
        string log = "";
        try
        {
            await Tools.Run(Tools.Ffmpeg, ["-nostdin", "-hide_banner", "-i", path, "-af", "loudnorm=print_format=json", "-f", "null", "-"], ct,
                onError: line => log += line + "\n").ConfigureAwait(false);
        }
        catch (ToolException) { return -23; }
        var m = Regex.Match(log, "\"input_i\"\\s*:\\s*\"(-?[\\d.]+|-inf)\"");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -23;
    }

    /// <summary>Snaps whisper's approximate word times to CTC forced-alignment edges, window by window.</summary>
    public static List<Word> Refine(List<RawWord> raw, float[] audio16k, CtcAligner aligner, IProgress<double>? progress, CancellationToken ct)
    {
        var words = raw.Select(r => new Word
        {
            Text = r.Text, Norm = string.Join(' ', Normalizer.Tokens(r.Text)), Start = Time.FromSeconds(r.Start), End = Time.FromSeconds(r.End),
            Confidence = r.Probability, Trailing = r.Trailing,
        }).ToList();
        int i = 0;
        while (i < raw.Count)
        {
            ct.ThrowIfCancellationRequested();
            int j = i;
            while (j + 1 < raw.Count && raw[j + 1].Start - raw[j].End < 1.5 && raw[j + 1].End - raw[i].Start < 14) j++;
            double from = Math.Max(0, raw[i].Start - .35), to = Math.Min(audio16k.Length / 16000.0, raw[j].End + .35);
            int s0 = (int)(from * 16000), s1 = (int)(to * 16000);
            if (s1 - s0 > 1600)
            {
                var emissions = aligner.Emissions(audio16k.AsSpan(s0, s1 - s0));
                var aligned = aligner.Align(emissions, raw.Skip(i).Take(j - i + 1).Select(r => r.Text).ToList());
                if (aligned is not null)
                {
                    for (int k = 0; k < aligned.Count; k++)
                    {
                        if (aligned[k] is not { } a) continue;
                        double start = from + a.Start * 0.02, end = from + a.End * 0.02;
                        var w = words[i + k];
                        if (Math.Abs(start - raw[i + k].Start) > .6 || end <= start) continue;
                        w.Start = Time.FromSeconds(start);
                        w.End = Time.FromSeconds(end);
                        w.Confidence = Math.Min(w.Confidence, .5 + a.Score / 2);
                    }
                }
            }
            progress?.Report((j + 1) / (double)raw.Count);
            i = j + 1;
        }
        // Keep words ordered and non-overlapping.
        for (int k = 1; k < words.Count; k++)
            if (words[k].Start < words[k - 1].End) words[k].Start = Math.Min(words[k].End - Time.FromMs(10), words[k - 1].End);
        return words;
    }
}
