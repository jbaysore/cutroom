using System.Text.Json;
using Cutroom.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cutroom.Speech;

internal static class Sessions
{
    // CPU only: the DirectML provider crashes natively on the quantized wav2vec2 model, and CPU is fast enough
    // (about 0.3 s per minute of narration for alignment windows on the 13600K).
    public static InferenceSession Create(string path) =>
        new(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Transcriber.Threads });
}

/// <summary>Silero VAD: speech probability per 32 ms of 16 kHz audio. The independent safety net for cleanup.</summary>
public sealed class Vad : IDisposable
{
    public const int Chunk = 512;
    const int Context = 64;
    readonly InferenceSession session;
    readonly string inputName, stateName, srName, outputName, stateOutName;

    public Vad(string modelPath)
    {
        session = Sessions.Create(modelPath);
        var inputs = session.InputMetadata.Keys.ToList();
        inputName = inputs.First(n => n.Contains("input", StringComparison.OrdinalIgnoreCase) || n == "x");
        stateName = inputs.First(n => n.Contains("state", StringComparison.OrdinalIgnoreCase));
        srName = inputs.First(n => n.Equals("sr", StringComparison.OrdinalIgnoreCase));
        var outputs = session.OutputMetadata.Keys.ToList();
        stateOutName = outputs.First(n => n.Contains("state", StringComparison.OrdinalIgnoreCase));
        outputName = outputs.First(n => n != stateOutName);
    }

    /// <summary>One probability per <see cref="Chunk"/> samples.</summary>
    public float[] Probabilities(ReadOnlySpan<float> audio16k, IProgress<double>? progress = null)
    {
        int chunks = audio16k.Length / Chunk;
        var probs = new float[chunks];
        var state = new DenseTensor<float>(new[] { 2, 1, 128 });
        var sr = new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>());
        var context = new float[Context];
        var window = new float[Context + Chunk];
        for (int c = 0; c < chunks; c++)
        {
            context.CopyTo(window, 0);
            audio16k.Slice(c * Chunk, Chunk).CopyTo(window.AsSpan(Context));
            var input = new DenseTensor<float>(window.ToArray(), new[] { 1, Context + Chunk });
            using var results = session.Run([
                NamedOnnxValue.CreateFromTensor(inputName, input),
                NamedOnnxValue.CreateFromTensor(stateName, state),
                NamedOnnxValue.CreateFromTensor(srName, sr),
            ]);
            probs[c] = results.First(r => r.Name == outputName).AsTensor<float>().GetValue(0);
            var newState = results.First(r => r.Name == stateOutName).AsTensor<float>();
            state = new DenseTensor<float>(newState.ToArray(), new[] { 2, 1, 128 });
            window.AsSpan(Chunk, Context).CopyTo(context);
            if (c % 500 == 0) progress?.Report(c / (double)Math.Max(1, chunks));
        }
        return probs;
    }

    public void Dispose() => session.Dispose();
}

/// <summary>
/// wav2vec2 CTC model: forced alignment of known words to audio (precise word edges, 20 ms frames)
/// and verbatim greedy decoding of short regions whisper did not transcribe.
/// </summary>
public sealed class CtcAligner : IDisposable
{
    public const int SamplesPerFrame = 320;
    readonly InferenceSession session;
    readonly Dictionary<char, int> vocab;
    readonly string[] idToChar;
    readonly int blank, delimiter;
    readonly string inputName;

    public CtcAligner(string modelPath, string vocabPath)
    {
        session = Sessions.Create(modelPath);
        inputName = session.InputMetadata.Keys.First();
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))!;
        vocab = map.Where(kv => kv.Key.Length == 1).ToDictionary(kv => kv.Key[0], kv => kv.Value);
        idToChar = new string[map.Values.Max() + 1];
        foreach (var (k, v) in map) idToChar[v] = k;
        blank = map.TryGetValue("<pad>", out var b) ? b : 0;
        delimiter = vocab.TryGetValue('|', out var d) ? d : 4;
    }

    /// <summary>Log-probabilities [frames, vocab] for a 16 kHz clip.</summary>
    public float[,] Emissions(ReadOnlySpan<float> audio)
    {
        var data = audio.ToArray();
        double mean = data.Length > 0 ? data.Average() : 0;
        double var = data.Length > 0 ? data.Sum(x => (x - mean) * (x - mean)) / data.Length : 1;
        float scale = (float)(1 / Math.Sqrt(var + 1e-7));
        for (int i = 0; i < data.Length; i++) data[i] = (float)((data[i] - mean) * scale);
        var input = new DenseTensor<float>(data, new[] { 1, data.Length });
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var logits = results.First().AsTensor<float>();
        int frames = logits.Dimensions[1], classes = logits.Dimensions[2];
        var e = new float[frames, classes];
        for (int t = 0; t < frames; t++)
        {
            float max = float.MinValue;
            for (int c = 0; c < classes; c++) max = Math.Max(max, logits[0, t, c]);
            double sum = 0;
            for (int c = 0; c < classes; c++) sum += Math.Exp(logits[0, t, c] - max);
            float log = (float)(max + Math.Log(sum));
            for (int c = 0; c < classes; c++) e[t, c] = logits[0, t, c] - log;
        }
        return e;
    }

    /// <summary>Letters the CTC model knows for a word (numbers spelled out).</summary>
    public string Letters(string word)
    {
        var tokens = Normalizer.Tokens(word);
        var s = string.Join("|", tokens).ToUpperInvariant();
        return new string(s.Where(c => vocab.ContainsKey(c)).ToArray()).Trim('|');
    }

    /// <summary>
    /// Viterbi forced alignment. Returns per-word (startFrame, endFrame, score) or null for words with no letters.
    /// </summary>
    public List<(int Start, int End, double Score)?>? Align(float[,] emissions, IReadOnlyList<string> words)
    {
        var letters = words.Select(Letters).ToList();
        var tokens = new List<int>();
        var owner = new List<int>();
        for (int w = 0; w < letters.Count; w++)
        {
            if (letters[w].Length == 0) continue;
            if (tokens.Count > 0) { tokens.Add(delimiter); owner.Add(-1); }
            foreach (var ch in letters[w]) { tokens.Add(vocab[ch]); owner.Add(w); }
        }
        int T = emissions.GetLength(0), J = tokens.Count;
        if (J == 0 || T < J) return null;
        var trellis = new float[T + 1, J + 1];
        for (int t = 0; t <= T; t++) for (int j = 0; j <= J; j++) trellis[t, j] = float.NegativeInfinity;
        trellis[0, 0] = 0;
        for (int t = 0; t < T; t++)
        {
            trellis[t + 1, 0] = trellis[t, 0] + emissions[t, blank];
            for (int j = 1; j <= J; j++)
            {
                float stay = trellis[t, j] + emissions[t, blank];
                float stayToken = trellis[t, j] + emissions[t, tokens[j - 1]];
                float change = trellis[t, j - 1] + emissions[t, tokens[j - 1]];
                trellis[t + 1, j] = Math.Max(Math.Max(stay, stayToken), change);
            }
        }
        if (float.IsNegativeInfinity(trellis[T, J])) return null;
        // Backtrack: for each token, the frames where it was emitted.
        var first = new int[J];
        var last = new int[J];
        var score = new double[J];
        var count = new int[J];
        Array.Fill(first, -1);
        int jj = J;
        for (int t = T; t > 0 && jj > 0; t--)
        {
            int tok = tokens[jj - 1];
            float change = trellis[t - 1, jj - 1] + emissions[t - 1, tok];
            float stayToken = trellis[t - 1, jj] + emissions[t - 1, tok];
            float stay = trellis[t - 1, jj] + emissions[t - 1, blank];
            if (change >= stay && change >= stayToken)
            {
                Mark(jj - 1, t - 1, emissions[t - 1, tok]);
                jj--;
            }
            else if (stayToken >= stay) Mark(jj - 1, t - 1, emissions[t - 1, tok]);
        }
        void Mark(int j, int t, float p)
        {
            if (last[j] == 0 && first[j] == -1) last[j] = t;
            first[j] = t;
            score[j] += Math.Exp(p);
            count[j]++;
        }
        var result = new List<(int, int, double)?>();
        for (int w = 0; w < words.Count; w++)
        {
            var idx = Enumerable.Range(0, J).Where(j => owner[j] == w && first[j] >= 0).ToList();
            if (idx.Count == 0) { result.Add(null); continue; }
            int s = idx.Min(j => first[j]), e = idx.Max(j => last[j]) + 1;
            double sc = idx.Sum(j => score[j]) / Math.Max(1, idx.Sum(j => count[j]));
            result.Add((s, e, sc));
        }
        return result;
    }

    /// <summary>Verbatim transcription of a short region (greedy CTC): catches false starts whisper smoothed over.</summary>
    public string Greedy(float[,] emissions)
    {
        var sb = new System.Text.StringBuilder();
        int prev = -1;
        for (int t = 0; t < emissions.GetLength(0); t++)
        {
            int best = 0;
            for (int c = 1; c < emissions.GetLength(1); c++) if (emissions[t, c] > emissions[t, best]) best = c;
            if (best != prev && best != blank)
            {
                var s = idToChar[best];
                sb.Append(best == delimiter ? " " : s.Length == 1 ? s : "");
            }
            prev = best;
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().ToLowerInvariant();
    }

    public void Dispose() => session.Dispose();
}
