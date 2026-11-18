using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cutroom.Core;

public sealed class ScriptSentence
{
    public string Id { get; set; } = Ids.New();
    public string Text { get; set; } = "";
    public List<string> Tokens { get; set; } = [];
    /// <summary>Phrase boundaries (token indices where a new phrase starts), used for intro slots.</summary>
    public List<int> PhraseStarts { get; set; } = [];
}

public sealed class ScriptParagraph
{
    public string Id { get; set; } = Ids.New();
    public List<ScriptSentence> Sentences { get; set; } = [];
}

public sealed class ScriptSection
{
    public string Id { get; set; } = Ids.New();
    /// <summary>Null for the part before the first heading, which is the intro.</summary>
    public string? Heading { get; set; }
    public List<ScriptParagraph> Paragraphs { get; set; } = [];
}

public sealed class Script
{
    public List<ScriptSection> Sections { get; set; } = [];

    public IEnumerable<ScriptSentence> Sentences => Sections.SelectMany(s => s.Paragraphs).SelectMany(p => p.Sentences);
    public bool HasHeadings => Sections.Any(s => s.Heading is not null);

    public ScriptSection? SectionOf(string sentenceId) =>
        Sections.FirstOrDefault(s => s.Paragraphs.Any(p => p.Sentences.Any(x => x.Id == sentenceId)));
    public ScriptParagraph? ParagraphOf(string sentenceId) =>
        Sections.SelectMany(s => s.Paragraphs).FirstOrDefault(p => p.Sentences.Any(x => x.Id == sentenceId));

    /// <summary>Distinct capitalized/unusual words (hero, ability, map names) used to prime transcription and fix spelling.</summary>
    public IReadOnlyList<string> Vocabulary()
    {
        var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Sentences)
        {
            var raw = Regex.Matches(s.Text, @"[\p{L}\p{N}][\p{L}\p{N}'’\-:.]*[\p{L}\p{N}]|[\p{L}\p{N}]");
            for (int i = 0; i < raw.Count; i++)
            {
                var w = raw[i].Value;
                bool proper = char.IsUpper(w[0]) && (i > 0 || !CommonWords.Contains(w)) || w.Any(char.IsDigit) && w.Any(char.IsLetter) || w.Skip(1).Any(char.IsUpper);
                if (proper && w.Length > 1) words[w] = words.GetValueOrDefault(w) + 1;
            }
        }
        return words.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>Common sentence-initial words that are not names.</summary>
    static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","about","after","again","all","also","always","an","and","another","any","are","as","at","be","because","been","before","being","best",
        "but","by","can","could","did","do","does","doing","don't","down","each","even","every","first","for","from","get","gets","getting","go","going",
        "good","great","had","has","have","he","her","here","his","how","however","i","i'm","if","in","instead","into","is","it","it's","its","just",
        "keep","last","let","let's","like","look","make","many","maybe","more","most","much","my","never","next","no","not","now","of","often","on",
        "once","one","only","or","other","our","out","over","play","really","right","same","second","see","she","should","since","so","some",
        "sometimes","still","such","take","than","that","that's","the","their","them","then","there","there's","these","they","they're","think",
        "third","this","those","though","three","through","to","too","try","two","up","us","use","using","very","was","watch","way","we","well",
        "were","what","what's","when","where","which","while","who","why","will","with","without","would","yes","yet","you","you'll","you're","your",
    };

    public static Script Parse(IEnumerable<(string Text, bool Heading)> blocks)
    {
        var script = new Script();
        var section = new ScriptSection();
        script.Sections.Add(section);
        foreach (var (raw, heading) in blocks)
        {
            var text = Regex.Replace(raw, @"\s+", " ").Trim();
            if (text.Length == 0) continue;
            if (heading)
            {
                section = new ScriptSection { Heading = text.TrimStart('#', ' ') };
                script.Sections.Add(section);
                continue;
            }
            var para = new ScriptParagraph();
            foreach (var sentence in SplitSentences(text))
            {
                var tokens = Normalizer.Tokens(sentence);
                if (tokens.Count == 0) continue;
                para.Sentences.Add(new ScriptSentence { Text = sentence, Tokens = tokens, PhraseStarts = PhraseStarts(sentence) });
            }
            if (para.Sentences.Count > 0) section.Paragraphs.Add(para);
        }
        if (script.Sections.Count > 1 && script.Sections[0].Paragraphs.Count == 0) script.Sections.RemoveAt(0);
        return script;
    }

    /// <summary>Plain text: lines starting with '#' are headings; blank lines separate paragraphs.</summary>
    public static Script FromText(string text)
    {
        var blocks = new List<(string, bool)>();
        var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { blocks.Add((para.ToString(), false)); para.Clear(); } }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) { Flush(); continue; }
            if (t.StartsWith('#')) { Flush(); blocks.Add((t, true)); continue; }
            para.Append(para.Length > 0 ? " " : "").Append(t);
        }
        Flush();
        return Parse(blocks);
    }

    /// <summary>Google Docs puts HTML on the clipboard; its h1-h6 elements become section headings.</summary>
    public static Script FromHtml(string html)
    {
        var start = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (start >= 0) html = html[start..];
        var blocks = new List<(string, bool)>();
        foreach (Match m in Regex.Matches(html, @"<(h[1-6]|p|li|div)\b[^>]*>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var inner = Regex.Replace(m.Groups[2].Value, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            inner = WebUtility.HtmlDecode(Regex.Replace(inner, "<[^>]+>", ""));
            blocks.Add((inner, m.Groups[1].Value.StartsWith('h') || m.Groups[1].Value.StartsWith('H')));
        }
        return Parse(blocks);
    }

    public static Script FromDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var blocks = new List<(string, bool)>();
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return new Script();
        foreach (var p in body.Descendants<Paragraph>())
        {
            var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
            bool heading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.Equals("Title", StringComparison.OrdinalIgnoreCase);
            blocks.Add((p.InnerText, heading));
        }
        return Parse(blocks);
    }

    public static Script Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => FromDocx(path),
        ".html" or ".htm" => FromHtml(File.ReadAllText(path)),
        _ => FromText(File.ReadAllText(path)),
    };

    static readonly Regex SentenceEnd = new(@"(?<=[.!?…])[""”’)]*\s+(?=[""“‘(]?[\p{Lu}\p{N}])", RegexOptions.Compiled);
    static readonly string[] Abbreviations = ["vs.", "e.g.", "i.e.", "etc.", "mr.", "mrs.", "dr.", "st.", "approx."];

    public static List<string> SplitSentences(string text)
    {
        var parts = SentenceEnd.Split(text);
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (result.Count > 0 && Abbreviations.Any(a => result[^1].EndsWith(a, StringComparison.OrdinalIgnoreCase)))
                result[^1] += " " + part.Trim();
            else if (part.Trim().Length > 0) result.Add(part.Trim());
        }
        return result;
    }

    /// <summary>Token indices that start a phrase (after a comma, colon, semicolon or dash).</summary>
    static List<int> PhraseStarts(string sentence)
    {
        var starts = new List<int> { 0 };
        int count = 0;
        foreach (Match m in Regex.Matches(sentence, @"[^\s]+"))
        {
            var tokens = Normalizer.Tokens(m.Value).Count;
            count += tokens;
            if (Regex.IsMatch(m.Value, @"[,;:—–]$") || m.Value is "-" or "—") starts.Add(count);
        }
        return starts.Distinct().Where(i => i < count).ToList();
    }
}

/// <summary>Turns text into comparable tokens: lowercase, no punctuation, numbers spelled out.</summary>
public static class Normalizer
{
    static readonly Regex Word = new(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.Compiled);

    public static List<string> Tokens(string text)
    {
        var result = new List<string>();
        foreach (Match m in Word.Matches(text.Replace('’', '\'')))
        {
            var w = m.Value.ToLowerInvariant();
            // Split mixed tokens like "1v1" or "2nd" into number words and letters.
            foreach (Match part in Regex.Matches(w, @"\d+|[^\d]+"))
            {
                if (char.IsDigit(part.Value[0]))
                {
                    if (long.TryParse(part.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) result.AddRange(NumberWords(n));
                }
                else if (part.Value is "st" or "nd" or "rd" or "th" && result.Count > 0) { }
                else result.Add(part.Value);
            }
        }
        return result;
    }

    static readonly string[] Ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    public static List<string> NumberWords(long n)
    {
        var words = new List<string>();
        if (n < 20) { words.Add(Ones[n]); return words; }
        void Below1000(long v)
        {
            if (v >= 100) { words.Add(Ones[v / 100]); words.Add("hundred"); v %= 100; }
            if (v >= 20) { words.Add(Tens[v / 10]); v %= 10; if (v > 0) words.Add(Ones[v]); }
            else if (v > 0) words.Add(Ones[v]);
        }
        foreach (var (scale, name) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1_000L, "thousand") })
        {
            if (n >= scale) { Below1000(n / scale); words.Add(name); n %= scale; }
        }
        if (n > 0) Below1000(n);
        return words;
    }
}
