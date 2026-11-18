using System.Security.Cryptography;
using Cutroom.Core;

namespace Cutroom.Speech;

public sealed record ModelFile(string Name, string Url, string Sha256, long Size);

/// <summary>Pinned local models, downloaded once to %LOCALAPPDATA%\Cutroom\models and verified by sha256.</summary>
public static class Models
{
    public static string Folder { get; set; } = Path.Combine(AppSettings.Folder, "models");

    public static readonly ModelFile WhisperTurbo = new("ggml-large-v3-turbo.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo.bin",
        "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", 1_624_555_275);
    public static readonly ModelFile WhisperBase = new("ggml-base.en.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.en.bin",
        "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", 147_964_211);
    public static readonly ModelFile SileroVad = new("silero_vad.onnx",
        "https://huggingface.co/onnx-community/silero-vad/resolve/e71cae966052b992a7eca6b17738916ce0eca4ec/onnx/model.onnx",
        "a4a068cd6cf1ea8355b84327595838ca748ec29a25bc91fc82e6c299ccdc5808", 2_243_022);
    public static readonly ModelFile Wav2Vec2 = new("wav2vec2_base_960h_q.onnx",
        "https://huggingface.co/Xenova/wav2vec2-base-960h/resolve/a19f851b3d42865797e410752b4c570c871e4825/onnx/model_quantized.onnx",
        "cd5040c147381580ed73258143dd8e0c28e800a09e74ee42ee2b3e8cb4d760a3", 95_286_046);
    public static readonly ModelFile Wav2Vec2Vocab = new("wav2vec2_vocab.json",
        "https://huggingface.co/Xenova/wav2vec2-base-960h/resolve/a19f851b3d42865797e410752b4c570c871e4825/vocab.json",
        "4178db26b3c7570f6a47f14ac6a1c7b32950b8c2800fb097287e53776934f1c5", 358);

    public static IReadOnlyList<ModelFile> Required(string whisperName) =>
        [whisperName == WhisperBase.Name ? WhisperBase : WhisperTurbo, SileroVad, Wav2Vec2, Wav2Vec2Vocab];

    public static string PathOf(ModelFile m) => Path.Combine(Folder, m.Name);

    public static bool Ready(ModelFile m) => File.Exists(PathOf(m)) && new FileInfo(PathOf(m)).Length == m.Size;

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromHours(2) };

    /// <summary>Downloads missing models with progress (0..1 over total bytes). Verifies hashes of new downloads.</summary>
    public static async Task Ensure(IEnumerable<ModelFile> models, IProgress<double>? progress, CancellationToken ct)
    {
        var missing = models.Where(m => !Ready(m)).ToList();
        if (missing.Count == 0) return;
        Directory.CreateDirectory(Folder);
        long total = missing.Sum(m => m.Size), done = 0;
        foreach (var m in missing)
        {
            var target = PathOf(m);
            var temp = target + ".download";
            using (var response = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(Math.Min(1, done / (double)total));
                }
            }
            await using (var check = File.OpenRead(temp))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (hash != m.Sha256)
                {
                    File.Delete(temp);
                    throw new InvalidDataException($"{m.Name} did not match its pinned checksum; the download was discarded.");
                }
            }
            File.Move(temp, target, true);
        }
    }
}
