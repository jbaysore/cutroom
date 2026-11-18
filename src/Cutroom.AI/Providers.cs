using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cutroom.Library;

namespace Cutroom.AI;

public sealed class AiException(string message, bool limitOrSignIn = false) : Exception(message)
{
    /// <summary>Usage limit or sign-in problems are shown plainly and never retried automatically.</summary>
    public bool LimitOrSignIn { get; } = limitOrSignIn;
}

public interface IAiProvider
{
    string Name { get; }
    bool Installed { get; }
    /// <summary>Returns a JSON document matching <paramref name="schema"/>.</summary>
    Task<string> Ask(string prompt, string schema, CancellationToken ct);
}

/// <summary>
/// Runs the Claude Code or Codex CLI the user is already signed into, never an API key: the child environment always
/// has API-key variables removed so a request cannot fall back to per-use billing. Requests run in an empty
/// temporary folder with tools disabled or read-only; only text is sent.
/// </summary>
public abstract class CliProvider : IAiProvider
{
    public static readonly string[] StrippedVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_API_KEY", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
        "AWS_BEARER_TOKEN_BEDROCK", "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "AZURE_OPENAI_API_KEY", "CODEX_API_KEY",
    ];

    public static IDictionary<string, string?> SafeEnvironment() => StrippedVariables.ToDictionary(v => v, _ => (string?)null);

    public abstract string Name { get; }
    protected abstract string Executable { get; }
    public bool Installed => Resolve() is not null;

    protected string? Resolve()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), Executable + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", Executable + ".exe");
        return File.Exists(local) ? local : null;
    }

    public async Task<string> Ask(string prompt, string schema, CancellationToken ct)
    {
        var exe = Resolve() ?? throw new AiException($"{Name} is not installed.", true);
        var dir = Directory.CreateTempSubdirectory("cutroom-ai").FullName;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var errors = new StringBuilder();
            string output;
            try
            {
                output = await Tools.Run(exe, Arguments(dir, schema), timeout.Token, onError: l => errors.AppendLine(l), workingDirectory: dir,
                    env: SafeEnvironment(), stdin: prompt).ConfigureAwait(false);
            }
            catch (ToolException ex)
            {
                throw Classify(ex.Message + "\n" + errors);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiException($"{Name} took longer than 2 minutes and was stopped.");
            }
            var json = Extract(output, dir);
            if (json is null) throw Classify(output + "\n" + errors);
            return json;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    protected abstract IEnumerable<string> Arguments(string workDir, string schema);
    protected abstract string? Extract(string stdout, string workDir);

    AiException Classify(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("usage limit") || t.Contains("limit reached") || t.Contains("rate limit") || t.Contains("quota"))
            return new AiException($"Your {Name} subscription has hit its usage limit. Try again later; nothing was charged.", true);
        if (t.Contains("log in") || t.Contains("login") || t.Contains("not logged") || t.Contains("unauthorized") || t.Contains("authenticat"))
            return new AiException($"{Name} is not signed in. Open {Name} once and sign in with your subscription.", true);
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no response";
        return new AiException($"{Name} did not return a usable answer ({first[..Math.Min(first.Length, 160)]}).");
    }
}

public sealed class ClaudeProvider : CliProvider
{
    public override string Name => "Claude";
    protected override string Executable => "claude";

    protected override IEnumerable<string> Arguments(string workDir, string schema) =>
        ["-p", "--output-format", "json", "--json-schema", schema, "--tools", "", "--no-session-persistence",
         "--system-prompt", "You help a YouTube creator edit videos. Answer only with the requested JSON."];

    protected override string? Extract(string stdout, string workDir)
    {
        try
        {
            var node = JsonNode.Parse(stdout);
            if (node?["is_error"]?.GetValue<bool>() == true) return null;
            if (node?["structured_output"] is JsonNode structured) return structured.ToJsonString();
            var result = node?["result"]?.GetValue<string>();
            return result is null ? null : JsonText.FirstObject(result);
        }
        catch (JsonException) { return JsonText.FirstObject(stdout); }
    }
}

public sealed class CodexProvider : CliProvider
{
    public override string Name => "Codex";
    protected override string Executable => "codex";

    protected override IEnumerable<string> Arguments(string workDir, string schema)
    {
        var schemaFile = Path.Combine(workDir, "schema.json");
        File.WriteAllText(schemaFile, schema);
        return ["exec", "--skip-git-repo-check", "--ephemeral", "-s", "read-only", "-C", workDir, "--output-schema", schemaFile,
                "-o", Path.Combine(workDir, "answer.json"), "--color", "never", "-"];
    }

    protected override string? Extract(string stdout, string workDir)
    {
        var file = Path.Combine(workDir, "answer.json");
        return File.Exists(file) ? JsonText.FirstObject(File.ReadAllText(file)) : JsonText.FirstObject(stdout);
    }
}

public static class JsonText
{
    /// <summary>The first complete JSON object in a text (models sometimes wrap answers in prose or code fences).</summary>
    public static string? FirstObject(string text)
    {
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    var candidate = text[start..(i + 1)];
                    try { JsonDocument.Parse(candidate).Dispose(); return candidate; }
                    catch (JsonException) { break; }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20].ToLowerInvariant();
}
