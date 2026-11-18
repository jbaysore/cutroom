using System.Text.RegularExpressions;
using Cutroom.Core;

namespace Cutroom.Speech;

public enum RunKind { Retake, FalseStart, AdLib }

/// <summary>A run of transcript words that did not align to the script, classified.</summary>
public sealed record UnmatchedRun(int FirstWord, int LastWord, RunKind Kind, double Similarity, double SentenceCoverage);

public sealed class MatchResult
{
    /// <summary>Per transcript word: (sentence id, token index within the sentence) or null when unmatched.</summary>
    public required (string SentenceId, int Token)?[] WordMatch;
    public required List<UnmatchedRun> Runs;
}

/// <summary>
/// Aligns the transcript to the script with a monotonic DP. Ties prefer matching, and the traceback runs from
/// the end, so when a line was said twice the LAST take aligns and earlier attempts fall out as unmatched runs.
/// Those runs are then classified as retakes/false starts (they repeat nearby script text) or ad-libs (they do not).
/// </summary>
public static class ScriptMatcher
{
    sealed record ScriptToken(string Token, string SentenceId, int Index, int Flat);

    public static MatchResult Match(IReadOnlyList<Word> words, Script script)
    {
        var sTokens = new List<ScriptToken>();
        foreach (var s in script.Sentences)
            for (int i = 0; i < s.Tokens.Count; i++) sTokens.Add(new ScriptToken(s.Tokens[i], s.Id, i, sTokens.Count));
        var tTokens = new List<(string Token, int Word)>();
        for (int w = 0; w < words.Count; w++)
            foreach (var tok in Normalizer.Tokens(words[w].Text)) tTokens.Add((tok, w));

        int n = tTokens.Count, m = sTokens.Count;
        var matchOfToken = new int[n];
        Array.Fill(matchOfToken, -1);
        if (n > 0 && m > 0)
        {
            // Costs are doubled integers: exact 0, near-miss 1, mismatch 3, insertion 2, script skip 2.
            var back = new byte[(long)(n + 1) * (m + 1)];
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) { prev[j] = j * 2; back[j] = 2; }
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i * 2;
                back[(long)i * (m + 1)] = 1;
                for (int j = 1; j <= m; j++)
                {
                    int sub = prev[j - 1] + Cost(tTokens[i - 1].Token, sTokens[j - 1].Token);
                    int ins = prev[j] + 2;
                    int del = cur[j - 1] + 2;
                    byte move = 0;
                    int best = sub;
                    if (ins < best) { best = ins; move = 1; }
                    if (del < best) { best = del; move = 2; }
                    cur[j] = best;
                    back[(long)i * (m + 1) + j] = move;
                }
                (prev, cur) = (cur, prev);
            }
            int ii = n, jj = m;
            while (ii > 0 || jj > 0)
            {
                byte move = back[(long)ii * (m + 1) + jj];
                if (ii > 0 && jj > 0 && move == 0)
                {
                    if (Cost(tTokens[ii - 1].Token, sTokens[jj - 1].Token) < 3) matchOfToken[ii - 1] = jj - 1;
                    ii--; jj--;
                }
                else if (ii > 0 && (move == 1 || jj == 0)) ii--;
                else jj--;
            }
        }

        var wordMatch = new (string, int)?[words.Count];
        var wordFlat = new int[words.Count];
        Array.Fill(wordFlat, -1);
        for (int t = 0; t < n; t++)
        {
            if (matchOfToken[t] < 0) continue;
            int w = tTokens[t].Word;
            if (wordMatch[w] is null)
            {
                var st = sTokens[matchOfToken[t]];
                wordMatch[w] = (st.SentenceId, st.Index);
                wordFlat[w] = st.Flat;
            }
        }

        // Classify unmatched runs.
        var runs = new List<UnmatchedRun>();
        var sentenceLength = script.Sentences.ToDictionary(s => s.Id, s => s.Tokens.Count);
        int k = 0;
        while (k < words.Count)
        {
            if (wordMatch[k] is not null) { k++; continue; }
            int a = k;
            while (k < words.Count && wordMatch[k] is null) k++;
            int b = k - 1;
            int prevFlat = a > 0 ? wordFlat[a - 1] : -1;
            int nextFlat = k < words.Count ? wordFlat[k] : m;
            var runTokens = Enumerable.Range(a, b - a + 1).SelectMany(w => Normalizer.Tokens(words[w].Text)).ToList();
            if (runTokens.Count == 0) continue;
            // Compare to script text around where the speaker resumed: a retake repeats nearby script words.
            int from = Math.Max(0, Math.Min(prevFlat < 0 ? 0 : prevFlat, nextFlat) - runTokens.Count - 4);
            int to = Math.Min(m, Math.Max(nextFlat, prevFlat) + runTokens.Count + 6);
            double best = 0;
            int bestStart = -1;
            for (int s = from; s < to; s++)
            {
                int len = Math.Min(m - s, runTokens.Count + 3);
                if (len <= 0) break;
                double sim = Lcs(runTokens, sTokens.Skip(s).Take(len).Select(x => x.Token).ToList()) / (double)runTokens.Count;
                if (sim > best + 1e-9) { best = sim; bestStart = s; }
            }
            // Repeats of the words right before the run ("the the", "sleep is, sleep is") are stutters too.
            if (a > 0)
            {
                var before = Enumerable.Range(Math.Max(0, a - runTokens.Count - 2), a - Math.Max(0, a - runTokens.Count - 2))
                    .SelectMany(w => Normalizer.Tokens(words[w].Text)).ToList();
                double rep = Lcs(runTokens, before) / (double)runTokens.Count;
                if (rep > best) best = rep;
            }
            bool repeat = best >= .6 && (runTokens.Count >= 2 || best >= .99);
            double coverage = 0;
            if (repeat && bestStart >= 0)
            {
                var sid = sTokens[bestStart].SentenceId;
                coverage = runTokens.Count / (double)Math.Max(1, sentenceLength[sid]);
            }
            var kind = !repeat ? RunKind.AdLib : coverage >= .6 ? RunKind.Retake : RunKind.FalseStart;
            runs.Add(new UnmatchedRun(a, b, kind, best, coverage));
        }
        return new MatchResult { WordMatch = wordMatch, Runs = runs };
    }

    static int Cost(string a, string b)
    {
        if (a == b) return 0;
        if (Near(a, b)) return 1;
        return 3;
    }

    /// <summary>Near-misses from recognition ("anna" for "ana", "rein" for "rein's").</summary>
    public static bool Near(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3) return false;
        if (a.TrimEnd('s') == b.TrimEnd('s') || a.Replace("'s", "") == b.Replace("'s", "")) return true;
        int d = Edit(a, b);
        return d <= 1 || d <= 2 && Math.Min(a.Length, b.Length) >= 6;
    }

    static int Edit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var d = new int[a.Count + 1, b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
            for (int j = 1; j <= b.Count; j++)
                d[i, j] = a[i - 1] == b[j - 1] || Near(a[i - 1], b[j - 1]) ? d[i - 1, j - 1] + 1 : Math.Max(d[i - 1, j], d[i, j - 1]);
        return d[a.Count, b.Count];
    }

    /// <summary>
    /// Spelling and punctuation from the script for confidently matched words ("anna's" becomes "Ana's" when the
    /// script says so). Words whose tokens map onto one script word take that word's form.
    /// </summary>
    public static void ApplyScriptSpelling(IReadOnlyList<Word> words, Script script, MatchResult match)
    {
        var surfaces = new Dictionary<string, List<(string Surface, string Trailing, int Start, int Count)>>();
        foreach (var s in script.Sentences)
        {
            var list = new List<(string, string, int, int)>();
            int index = 0;
            foreach (var piece in s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int count = Normalizer.Tokens(piece).Count;
                if (count == 0) continue;
                var m = Regex.Match(piece, @"^[""“‘(]*(.*?)([.,!?;:…""”’)\-—]*)$");
                list.Add((m.Groups[1].Value, m.Groups[2].Value.Trim('"', '”', '’', ')'), index, count));
                index += count;
            }
            surfaces[s.Id] = list;
        }
        for (int w = 0; w < words.Count; w++)
        {
            if (match.WordMatch[w] is not { } at) continue;
            var word = words[w];
            word.SentenceId = at.SentenceId;
            word.ScriptTokenIndex = at.Token;
            var surface = surfaces[at.SentenceId].FirstOrDefault(x => at.Token >= x.Start && at.Token < x.Start + x.Count);
            if (surface.Surface is null) continue;
            var tokens = Normalizer.Tokens(word.Text);
            if (surface.Start == at.Token && tokens.Count == surface.Count && surface.Surface.Length > 0)
            {
                var scriptTokens = Normalizer.Tokens(surface.Surface);
                if (scriptTokens.Count == tokens.Count && scriptTokens.Zip(tokens).All(p => p.First == p.Second || Near(p.First, p.Second)))
                {
                    word.Text = surface.Surface;
                    word.Trailing = surface.Trailing;
                }
            }
        }
    }
}
