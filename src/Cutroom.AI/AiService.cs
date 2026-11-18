using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.AI;

public sealed record CardDrafts(string Condensed, string KeyPoints, string ExactWords);
public sealed record SlotSuggestion(string SlotId, string Kind, string? ClipDescription, string? CardText, string Reason);
public sealed record PackageDraft(List<string> Titles, string Description, List<string> Tags);

/// <summary>
/// The three v1 AI helpers. AI only proposes: drafts card text, suggests a visual per slot and drafts YouTube
/// packaging. Results are cached by prompt, and output is validated against the expected shape.
/// </summary>
public sealed class AiService
{
    readonly Func<string, IAiProvider?> providers;
    public string Preferred { get; set; }
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public AiService(string preferred = "claude", Func<string, IAiProvider?>? providerFactory = null)
    {
        Preferred = preferred;
        providers = providerFactory ?? (name => name switch { "claude" => new ClaudeProvider(), "codex" => new CodexProvider(), _ => null });
    }

    /// <summary>Installed providers in preference order; empty means the AI buttons are greyed out.</summary>
    public IReadOnlyList<IAiProvider> Available()
    {
        var order = Preferred == "codex" ? new[] { "codex", "claude" } : ["claude", "codex"];
        return order.Select(providers).OfType<IAiProvider>().Where(p => p.Installed).ToList();
    }

    public bool Enabled => Available().Count > 0;

    async Task<T> Ask<T>(string prompt, string schema, Func<T, string?> validate, CancellationToken ct)
    {
        var cacheFile = Path.Combine(Tools.CacheDir("ai"), JsonText.Hash(prompt + schema) + ".json");
        if (File.Exists(cacheFile))
        {
            var cached = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false), Json);
            if (cached is not null && validate(cached) is null) return cached;
        }
        var available = Available();
        if (available.Count == 0) throw new AiException("Neither Claude Code nor Codex is installed and signed in.", true);
        AiException? last = null;
        foreach (var provider in available)
        {
            string attempt = prompt;
            for (int tries = 0; tries < 2; tries++)
            {
                string json;
                try { json = await provider.Ask(attempt, schema, ct).ConfigureAwait(false); }
                catch (AiException ex) when (!ex.LimitOrSignIn || available.Count > 1)
                {
                    last = ex;
                    break;
                }
                T? value = default;
                string? problem;
                try
                {
                    value = JsonSerializer.Deserialize<T>(json, Json);
                    problem = value is null ? "empty answer" : validate(value);
                }
                catch (JsonException ex) { problem = ex.Message; }
                if (problem is null)
                {
                    await File.WriteAllTextAsync(cacheFile, json, ct).ConfigureAwait(false);
                    return value!;
                }
                attempt = prompt + $"\n\nYour previous answer was invalid ({problem}). Answer again with JSON that matches the schema exactly.";
                last = new AiException($"{provider.Name} gave an answer in the wrong shape ({problem}).");
            }
        }
        throw last ?? new AiException("No AI answer.");
    }

    const string CardSchema = """
        {"type":"object","additionalProperties":false,"required":["condensed","keyPoints","exactWords"],
         "properties":{"condensed":{"type":"string"},"keyPoints":{"type":"string"},"exactWords":{"type":"string"}}}
        """;

    /// <summary>Three drafts of on-screen text for a blur card, from what was said over its range.</summary>
    public Task<CardDrafts> DraftCardText(string spoken, string scriptParagraph, IReadOnlyList<string> names, CancellationToken ct)
    {
        var prompt = $"""
            Write on-screen text for an educational Overwatch YouTube video. The text appears on a blurred-footage card while the narrator says the passage below.
            Return three versions, each with one line of text per line (use \n between lines):
            - condensed: a shorter rewrite of the explanation in 1 to 4 lines, keeping key terms and numbers.
            - keyPoints: 2 to 5 short punchy lines, one point each, in the order they are said.
            - exactWords: the narrator's own wording with filler removed, split into readable lines of at most 45 characters.
            Spell these names exactly as written: {string.Join(", ", names.Take(40))}.
            Do not add facts that are not in the passage. No emojis, no hashtags, no quotes around lines.

            Spoken passage:
            {spoken}

            Surrounding script paragraph (context only):
            {scriptParagraph}
            """;
        return Ask<CardDrafts>(prompt, CardSchema, d =>
            string.IsNullOrWhiteSpace(d.Condensed) || string.IsNullOrWhiteSpace(d.KeyPoints) || string.IsNullOrWhiteSpace(d.ExactWords) ? "a draft was empty" : null, ct);
    }

    sealed record PlanAnswer(List<SlotSuggestion> Suggestions);

    const string PlanSchema = """
        {"type":"object","additionalProperties":false,"required":["suggestions"],
         "properties":{"suggestions":{"type":"array","items":{"type":"object","additionalProperties":false,
           "required":["slotId","kind","clipDescription","cardText","reason"],
           "properties":{"slotId":{"type":"string"},"kind":{"type":"string","enum":["clip","textCard","backgroundCard","zoom","freezeOutline","sectionHeader"]},
             "clipDescription":{"type":["string","null"]},"cardText":{"type":["string","null"]},"reason":{"type":"string"}}}}}}
        """;

    /// <summary>One visual suggestion per slot. Suggestions never place footage; clip ideas become a library search.</summary>
    public async Task<List<SlotSuggestion>> PlanVisuals(IReadOnlyList<Slot> slots, CancellationToken ct)
    {
        var all = new List<SlotSuggestion>();
        foreach (var batch in slots.Chunk(60))
        {
            var sb = new StringBuilder();
            foreach (var s in batch) sb.Append(s.Id).Append(" | ").Append(s.IsIntro ? "INTRO | " : s.SectionTitle is { } t ? $"{t} | " : "").AppendLine(s.Text);
            var prompt = $"""
                You are planning visuals for an educational Overwatch YouTube video. Each line below is one slot of narration: "slotId | section | text".
                For every slot, suggest one visual the editor could use, choosing kind from:
                - clip: gameplay footage; put a short search-style description of what the clip should show in clipDescription (e.g. "Ana sleeps Reinhardt mid Earthshatter").
                - textCard: blurred footage with explanation text; put 1-4 short lines in cardText (use \n between lines).
                - backgroundCard: notebook background with text; put text in cardText.
                - zoom: zoom into part of the gameplay (HUD, kill feed, cooldowns); describe what to zoom on in reason.
                - freezeOutline: freeze the frame and circle a player or object; describe it in reason.
                - sectionHeader: only for a slot that starts a new section.
                Prefer clip for most slots. In the INTRO, prefer short punchy clips. Keep reasons under 15 words. Use null for unused fields.
                Return exactly one suggestion per slotId.

                {sb}
                """;
            var ids = batch.Select(s => s.Id).ToHashSet();
            var answer = await Ask<PlanAnswer>(prompt, PlanSchema, a => a.Suggestions.Count(x => ids.Contains(x.SlotId)) < ids.Count * 0.8 ? "missing slots" : null, ct).ConfigureAwait(false);
            all.AddRange(answer.Suggestions.Where(x => ids.Contains(x.SlotId)));
        }
        return all;
    }

    const string PackageSchema = """
        {"type":"object","additionalProperties":false,"required":["titles","description","tags"],
         "properties":{"titles":{"type":"array","items":{"type":"string"}},"description":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}}}
        """;

    /// <summary>Title options, a description summary and tags in the channel's style. Fixed blocks are added by <see cref="AssembleDescription"/>, not by AI.</summary>
    public Task<PackageDraft> DraftPackage(Script script, ChannelProfile profile, CancellationToken ct)
    {
        var text = string.Join("\n\n", script.Sections.Select(s => (s.Heading is null ? "" : "## " + s.Heading + "\n") +
            string.Join("\n", s.Paragraphs.Select(p => string.Join(" ", p.Sentences.Select(x => x.Text))))));
        if (text.Length > 24_000) text = text[..24_000];
        var prompt = $"""
            Draft YouTube packaging for this educational Overwatch video in the creator's own style.
            Return: titles (5 options), description (the summary paragraph(s) only, no links, no chapters, no hashtags), tags (10-20 search tags).
            Match the style of the creator's past titles and descriptions below. Do not invent claims not in the script.

            Creator's style notes: {profile.StyleNotes}
            Past titles:
            {string.Join("\n", profile.PastTitles.Take(15).Select(t => "- " + t))}
            Past description openings:
            {string.Join("\n---\n", profile.PastDescriptions.Take(4).Select(d => d.Length > 600 ? d[..600] : d))}

            Script:
            {text}
            """;
        return Ask<PackageDraft>(prompt, PackageSchema, d => d.Titles.Count < 3 ? "fewer than 3 titles" : string.IsNullOrWhiteSpace(d.Description) ? "empty description" : null, ct);
    }

    sealed record StyleAnswer(string StyleNotes);

    public async Task<string> LearnStyle(ChannelProfile profile, CancellationToken ct)
    {
        var prompt = $"""
            Describe this YouTube creator's title and description style in 3-6 short bullet points (tone, length, capitalization, use of numbers, hooks, question titles, etc.).
            Titles:
            {string.Join("\n", profile.PastTitles.Take(25).Select(t => "- " + t))}
            Descriptions:
            {string.Join("\n---\n", profile.PastDescriptions.Take(5).Select(d => d.Length > 800 ? d[..800] : d))}
            """;
        var answer = await Ask<StyleAnswer>(prompt, """{"type":"object","additionalProperties":false,"required":["styleNotes"],"properties":{"styleNotes":{"type":"string"}}}""",
            a => string.IsNullOrWhiteSpace(a.StyleNotes) ? "empty" : null, ct).ConfigureAwait(false);
        return answer.StyleNotes;
    }

    /// <summary>Summary, then chapters, then the fixed block word for word.</summary>
    public static string AssembleDescription(string summary, string chapters, string fixedBlock)
    {
        var parts = new List<string> { summary.Trim() };
        if (chapters.Trim().Length > 0) parts.Add(chapters.Trim());
        if (fixedBlock.Trim().Length > 0) parts.Add(fixedBlock.Trim());
        return string.Join("\n\n", parts);
    }

    /// <summary>Lines that appear in at least half of the past descriptions: links, socials and sign-offs.</summary>
    public static string FixedBlock(IReadOnlyList<string> descriptions)
    {
        if (descriptions.Count < 2) return "";
        var counts = new Dictionary<string, int>();
        foreach (var d in descriptions)
            foreach (var line in d.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct())
                counts[line] = counts.GetValueOrDefault(line) + 1;
        var common = counts.Where(kv => kv.Value * 2 >= descriptions.Count).Select(kv => kv.Key).ToHashSet();
        var ordered = descriptions[0].Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(common.Contains).Distinct();
        return string.Join("\n", ordered);
    }

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Recent titles and descriptions from the channel's public RSS feed (free, no key). Accepts a channel URL, @handle or UC id.</summary>
    public static async Task<(string ChannelId, List<string> Titles, List<string> Descriptions)> FetchChannel(string input, CancellationToken ct)
    {
        input = input.Trim();
        var id = Regex.Match(input, @"UC[\w-]{22}").Value;
        if (id.Length == 0)
        {
            var url = input.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? input : "https://www.youtube.com/" + (input.StartsWith('@') ? input : "@" + input);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.AcceptLanguage.ParseAdd("en-US");
            var html = await (await http.SendAsync(request, ct).ConfigureAwait(false)).Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            id = Regex.Match(html, @"""(?:channelId|externalId)""\s*:\s*""(UC[\w-]{22})""").Groups[1].Value;
            if (id.Length == 0) id = Regex.Match(html, @"channel/(UC[\w-]{22})").Groups[1].Value;
            if (id.Length == 0) throw new AiException("Could not find that YouTube channel. Paste the channel URL instead.");
        }
        var xml = await http.GetStringAsync($"https://www.youtube.com/feeds/videos.xml?channel_id={id}", ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom", media = "http://search.yahoo.com/mrss/";
        var entries = doc.Root!.Elements(atom + "entry").ToList();
        return (id, entries.Select(e => (string?)e.Element(atom + "title") ?? "").Where(t => t.Length > 0).ToList(),
            entries.Select(e => (string?)e.Element(media + "group")?.Element(media + "description") ?? "").Where(d => d.Length > 0).ToList());
    }
}
