using System.Globalization;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.Speech;

/// <summary>
/// Runs cleanup over a corpus folder and scores each case. A case is a subfolder holding <c>narration.*</c> (WAV or an
/// OBS video), <c>script.txt</c>/<c>script.docx</c>, <c>expected.txt</c> (what the cleaned narration should say, in
/// order) and optionally <c>marks.txt</c> (retake-mark seconds, one per line).
/// </summary>
public static class CleanupCorpus
{
    public static IEnumerable<string> Cases(string corpus) =>
        Directory.EnumerateDirectories(corpus).Where(d => File.Exists(Path.Combine(d, "expected.txt"))).Order(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<CleanupScore>> Run(string corpus, Denoiser denoiser, string whisperModel, Action<string>? log, CancellationToken ct)
    {
        var scores = new List<CleanupScore>();
        foreach (var dir in Cases(corpus))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            log?.Invoke($"{name}: cleaning");
            scores.Add(await RunCase(dir, denoiser, whisperModel, ct).ConfigureAwait(false));
            log?.Invoke($"{name}: done");
        }
        return scores;
    }

    public static async Task<CleanupScore> RunCase(string dir, Denoiser denoiser, string whisperModel, CancellationToken ct)
    {
        var name = Path.GetFileName(dir);
        var narrationPath = Directory.EnumerateFiles(dir, "narration.*").FirstOrDefault()
            ?? throw new FileNotFoundException($"{name}: no narration.* file");
        var scriptPath = new[] { "script.docx", "script.txt" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        var script = scriptPath is null ? null : Script.Load(scriptPath);
        var marksPath = Path.Combine(dir, "marks.txt");
        var marks = File.Exists(marksPath)
            ? File.ReadAllLines(marksPath).Where(l => l.Trim().Length > 0).Select(l => Time.FromSeconds(double.Parse(l.Trim(), CultureInfo.InvariantCulture))).ToList()
            : [];

        var media = await MediaProbe.Probe(narrationPath).ConfigureAwait(false);
        var settings = new CleanupSettings { Denoiser = denoiser };
        var r = await CleanupEngine.Run(media, script, marks, settings, whisperModel, null, ct).ConfigureAwait(false);
        var n = new Narration { Words = r.Words, Decisions = r.Decisions, SourceDuration = r.Duration, Settings = settings, Processed = true };
        return CleanupEvaluation.Score(name, File.ReadAllText(Path.Combine(dir, "expected.txt")), r.Words, CleanupEvaluation.KeptWords(n), r.Decisions);
    }
}
