using Cutroom.Core;

namespace Cutroom.Tests;

internal static class Fixtures
{
    /// <summary>A project whose narration is the given sentences spoken back to back (0.3 s per word, 0.5 s between sentences).</summary>
    public static Project Narrated(string scriptText, params string[] spoken)
    {
        var p = Project.CreateDefault("Test");
        p.Script = Script.FromText(scriptText);
        var media = new MediaRef { Kind = MediaKind.Audio, Path = "narration.wav", HasAudio = true };
        p.Media.Add(media);
        p.Narration.MediaId = media.Id;
        long t = Time.FromSeconds(.5);
        var sentences = p.Script.Sentences.ToList();
        for (int s = 0; s < spoken.Length; s++)
        {
            var tokens = spoken[s].Split(' ');
            for (int i = 0; i < tokens.Length; i++)
            {
                var raw = tokens[i];
                var text = raw.TrimEnd('.', ',', '?', '!');
                p.Narration.Words.Add(new Word
                {
                    Text = text, Norm = text.ToLowerInvariant(), Start = t, End = t + Time.FromSeconds(.25),
                    Trailing = raw[text.Length..], SentenceId = s < sentences.Count ? sentences[s].Id : null, ScriptTokenIndex = i,
                });
                t += Time.FromSeconds(.3);
            }
            t += Time.FromSeconds(.5);
        }
        p.Narration.SourceDuration = t + Time.FromSeconds(.5);
        Derive.RebuildNarration(p);
        Derive.GenerateSlots(p);
        return p;
    }

    public static Clip Video(Project p, long start, long duration, long sourceIn = 0)
    {
        var media = new MediaRef { Kind = MediaKind.Video, Path = $"clip{p.Media.Count}.mp4", Duration = Time.FromSeconds(600), Width = 1920, Height = 1080, Fps = 60, HasAudio = true };
        p.Media.Add(media);
        var clip = new Clip { MediaId = media.Id, Start = start, Duration = duration, SourceIn = sourceIn };
        p.Track(TrackRole.Footage).Clips.Add(clip);
        return clip;
    }
}

public class TimeTests
{
    [Fact]
    public void FrameIsExact() => Assert.Equal(800, Time.Frame);

    [Fact]
    public void Timecodes()
    {
        Assert.Equal("00:00:01:30", Time.Timecode(Time.FromSeconds(1.5)));
        Assert.Equal("1:05", Time.Chapter(Time.FromSeconds(65)));
        Assert.Equal("00:00:01,500", Time.Srt(Time.FromSeconds(1.5)));
    }
}

public class NormalizerTests
{
    [Theory]
    [InlineData("Ana's sleep dart!", "ana's sleep dart")]
    [InlineData("It's a 1v1", "it's a one v one")]
    [InlineData("Deal 250 damage", "deal two hundred fifty damage")]
    [InlineData("the 2nd fight", "the two fight")]
    public void Tokens(string text, string expected) => Assert.Equal(expected, string.Join(' ', Normalizer.Tokens(text)));
}

public class ScriptTests
{
    [Fact]
    public void HeadingsAndSentences()
    {
        var s = Script.FromText("Sleep is great. It cancels ults, vs. most heroes.\n\n# Why sleep matters\nOne dart wins fights! Really?");
        Assert.Equal(2, s.Sections.Count);
        Assert.Null(s.Sections[0].Heading);
        Assert.Equal("Why sleep matters", s.Sections[1].Heading);
        Assert.Equal(4, s.Sentences.Count());
        Assert.Contains(s.Sentences, x => x.Text == "It cancels ults, vs. most heroes.");
        var phrased = s.Sentences.First(x => x.Text.StartsWith("It cancels"));
        Assert.Equal([0, 3], phrased.PhraseStarts);
    }

    [Fact]
    public void GoogleDocsHtml()
    {
        var html = "<html><body><p>Hook line here.</p><h1>Section <b>One</b></h1><p>Body &amp; more.</p></body></html>";
        var s = Script.FromHtml(html);
        Assert.Equal("Section One", s.Sections[1].Heading);
        Assert.Equal("Body & more.", s.Sentences.Last().Text);
    }

    [Fact]
    public void VocabularyFindsNames()
    {
        var s = Script.FromText("Use Sleep Dart on Reinhardt when Earthshatter starts. Kiriko and Junker Queen too.");
        var v = s.Vocabulary();
        Assert.Contains("Reinhardt", v);
        Assert.Contains("Kiriko", v);
        Assert.Contains("Earthshatter", v);
    }
}

public class DocumentTests
{
    [Fact]
    public void UndoRedoRestoresExactState()
    {
        var doc = new Document(Fixtures.Narrated("One two three. Four five six.", "One two three.", "Four five six."));
        var before = ProjectJson.SerializePretty(doc.Project);
        doc.Edit("blade", p => TimelineOps.Blade(p, Time.FromSeconds(1)));
        Assert.NotEqual(before, ProjectJson.SerializePretty(doc.Project));
        doc.Undo();
        Assert.Equal(before, ProjectJson.SerializePretty(doc.Project));
        doc.Redo();
        doc.Undo();
        Assert.Equal(before, ProjectJson.SerializePretty(doc.Project));
    }

    [Fact]
    public void RandomEditsUndoToOriginal()
    {
        var rng = new Random(7);
        for (int run = 0; run < 20; run++)
        {
            var p = Project.CreateDefault("Prop");
            for (int i = 0; i < 6; i++) Fixtures.Video(p, Time.FromSeconds(i * 3), Time.FromSeconds(3));
            var doc = new Document(p);
            var original = ProjectJson.SerializePretty(doc.Project);
            int steps = rng.Next(5, 25);
            for (int s = 0; s < steps; s++)
            {
                long t = Time.FromSeconds(rng.NextDouble() * 18);
                switch (rng.Next(6))
                {
                    case 0: doc.Edit("blade", x => TimelineOps.Blade(x, t)); break;
                    case 1: doc.Edit("ripple", x => { var c = x.AllClips.FirstOrDefault(); if (c is not null) TimelineOps.RippleDelete(x, [c]); }); break;
                    case 2: doc.Edit("slip", x => { var c = x.AllClips.LastOrDefault(); if (c is not null) TimelineOps.Slip(c, Time.FromSeconds(1), x.FindMedia(c.MediaId)); }); break;
                    case 3: doc.Edit("trim", x => { var c = x.AllClips.FirstOrDefault(); if (c is not null) TimelineOps.TrimStart(c, Time.Frame * 10); }); break;
                    case 4: doc.Edit("range", x => TimelineOps.RippleDeleteRange(x, new TimeRange(t, t + Time.FromSeconds(1)))); break;
                    default: doc.Edit("move", x => { var c = x.AllClips.FirstOrDefault(); if (c is not null) TimelineOps.Move(x, c, c.Start + Time.FromSeconds(2)); }); break;
                }
            }
            while (doc.Undo()) { }
            Assert.Equal(original, ProjectJson.SerializePretty(doc.Project));
        }
    }

    [Fact]
    public void FailedEditRollsBack()
    {
        var doc = new Document(Project.CreateDefault("X"));
        var before = ProjectJson.SerializePretty(doc.Project);
        Assert.Throws<InvalidOperationException>(() => doc.Edit("bad", p => { p.Name = "changed"; throw new InvalidOperationException(); }));
        Assert.Equal(before, ProjectJson.SerializePretty(doc.Project));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void SaveRotatesBackupsAndMigrates()
    {
        var dir = Directory.CreateTempSubdirectory("cutroom").FullName;
        var path = Path.Combine(dir, "a.cutroom.json");
        var doc = new Document(Project.CreateDefault("A"));
        for (int i = 0; i < 7; i++) { doc.Edit("rename", p => p.Name = "A" + i); doc.Save(path); }
        Assert.True(File.Exists(path + ".bak5"));
        Assert.False(File.Exists(path + ".bak6"));
        Assert.Equal("A6", Document.Open(path).Project.Name);

        var legacy = """{"Name":"Old","Media":[]}""";
        var migrated = ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(legacy));
        Assert.Equal(Project.CurrentVersion, migrated.Version);
        Assert.Throws<InvalidDataException>(() => ProjectJson.Deserialize("""{"Version":99}"""u8));
    }
}

public class TimelineTests
{
    [Fact]
    public void BladeSplitsSourceCorrectly()
    {
        var p = Project.CreateDefault("B");
        var c = Fixtures.Video(p, 0, Time.FromSeconds(10), Time.FromSeconds(5));
        var right = TimelineOps.Blade(p, Time.FromSeconds(4)).Single();
        Assert.Equal(Time.FromSeconds(4), c.Duration);
        Assert.Equal(Time.FromSeconds(4), right.Start);
        Assert.Equal(Time.FromSeconds(9), right.SourceIn);
        Assert.Equal(Time.FromSeconds(6), right.Duration);
    }

    [Fact]
    public void RippleDeleteClosesGap()
    {
        var p = Project.CreateDefault("R");
        var a = Fixtures.Video(p, 0, Time.FromSeconds(2));
        var b = Fixtures.Video(p, Time.FromSeconds(2), Time.FromSeconds(3));
        var c = Fixtures.Video(p, Time.FromSeconds(5), Time.FromSeconds(1));
        TimelineOps.RippleDelete(p, [b]);
        Assert.Equal(Time.FromSeconds(2), c.Start);
        Assert.Equal(2, p.Track(TrackRole.Footage).Clips.Count);
    }

    [Fact]
    public void LinkedAudioMovesAndDeletesTogether()
    {
        var p = Project.CreateDefault("L");
        var v = Fixtures.Video(p, 0, Time.FromSeconds(2));
        var a = TimelineOps.Clone(v);
        a.Id = Ids.New();
        v.LinkId = a.LinkId = "link";
        p.Track(TrackRole.Game).Clips.Add(a);
        TimelineOps.Move(p, v, Time.FromSeconds(3));
        Assert.Equal(Time.FromSeconds(3), a.Start);
        TimelineOps.Delete(p, [v]);
        Assert.Empty(p.Track(TrackRole.Game).Clips);
    }

    [Fact]
    public void OverwriteTrimsNeighbours()
    {
        var p = Project.CreateDefault("O");
        Fixtures.Video(p, 0, Time.FromSeconds(10));
        var track = p.Track(TrackRole.Footage);
        TimelineOps.Overwrite(p, track, new Clip { Start = Time.FromSeconds(2), Duration = Time.FromSeconds(3) });
        Assert.Equal(3, track.Clips.Count);
        Assert.Equal(Time.FromSeconds(5), track.Clips.Max(c => c.End) - Time.FromSeconds(5));
    }

    [Fact]
    public void EditPointsAndSnap()
    {
        var p = Project.CreateDefault("E");
        Fixtures.Video(p, 0, Time.FromSeconds(2));
        Fixtures.Video(p, Time.FromSeconds(2), Time.FromSeconds(2));
        Assert.Equal(Time.FromSeconds(2), TimelineOps.NextEdit(p, Time.FromSeconds(1)));
        Assert.Equal(0, TimelineOps.PreviousEdit(p, Time.FromSeconds(1)));
        Assert.Equal(Time.FromSeconds(2), TimelineOps.Snap(p, Time.FromSeconds(2.05), Time.FromSeconds(.1)));
        Assert.Equal(Time.FromSeconds(3), TimelineOps.Snap(p, Time.FromSeconds(3), Time.FromSeconds(.1)));
    }
}

public class NarrationTests
{
    [Fact]
    public void CutsRebuildNarrationAndShiftWords()
    {
        var p = Fixtures.Narrated("Alpha beta gamma. Delta epsilon zeta.", "Alpha beta gamma.", "Delta epsilon zeta.");
        var words = Derive.TimelineWords(p);
        Assert.Equal(6, words.Count);
        long before = words[3].TimeRange.Start;
        p.Narration.Decisions.Add(new CleanupDecision { Kind = DecisionKind.Silence, Start = Time.FromSeconds(1.4), End = Time.FromSeconds(1.8) });
        Derive.RebuildNarration(p);
        var after = Derive.TimelineWords(p);
        Assert.Equal(before - Time.FromSeconds(.4), after[3].TimeRange.Start);
        Assert.Equal(2, p.Track(TrackRole.Narration).Clips.Count);
        Assert.True(p.Track(TrackRole.Narration).Clips[1].FadeIn > 0);
    }

    [Fact]
    public void RetakeDecisionRemovesWords()
    {
        var p = Fixtures.Narrated("One two.", "One two.");
        var w = p.Narration.Words[0];
        p.Narration.Decisions.Add(new CleanupDecision { Kind = DecisionKind.Retake, Start = w.Start, End = w.End, WordIds = [w.Id] });
        Derive.RebuildNarration(p);
        Assert.False(p.Narration.Words[0].Kept);
        Assert.Single(Derive.TimelineWords(p));
    }

    [Fact]
    public void SlotsFollowSentencesAndClipsRetime()
    {
        var p = Fixtures.Narrated("Hook one here. Hook two here.\n# Main\nBody sentence one. Body two.",
            "Hook one here.", "Hook two here.", "Body sentence one.", "Body two.");
        Assert.True(p.Slots.Count >= 4);
        Assert.True(p.Slots[0].IsIntro);
        Assert.Equal("Main", p.Slots.Last().SectionTitle);
        Assert.Equal(0, p.Slots[0].Start);
        for (int i = 1; i < p.Slots.Count; i++) Assert.Equal(p.Slots[i - 1].End, p.Slots[i].Start);

        var slot = p.Slots.Last();
        var clip = Fixtures.Video(p, slot.Start, slot.End - slot.Start);
        clip.SlotId = slot.Id;
        Assert.Equal(SlotStatus.Footage, Derive.StatusOf(p, slot));
        p.Narration.Decisions.Add(new CleanupDecision { Kind = DecisionKind.Silence, Start = Time.FromSeconds(.6), End = Time.FromSeconds(1) });
        Derive.RebuildNarration(p);
        Assert.Equal(p.Slots.Last().Start, clip.Start);
        Assert.Equal(p.Slots.Last().End - p.Slots.Last().Start, clip.Duration);
    }

    [Fact]
    public void IntroRegionAndSectionHeadersAndChapters()
    {
        var p = Fixtures.Narrated("Hook line now.\n# First part\nAlpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi.\n# Second part\nOmega omega.",
            string.Join(' ', Enumerable.Repeat("hook", 40)) + ".", string.Join(' ', Enumerable.Repeat("word", 40)) + ".", "Omega omega.");
        Derive.EnsureIntroRegion(p, SubtitlePreset.DefaultId);
        var intro = p.SubtitleRegions.Single();
        Assert.Equal(p.Slots.First(s => !s.IsIntro).Start, intro.End);
        Derive.PlaceSectionHeaders(p, Time.FromSeconds(2.5));
        Assert.Equal(2, p.Track(TrackRole.Cards).Clips.Count);
        var chapters = Derive.Chapters(p).Split('\n');
        Assert.Equal("0:00 Intro", chapters[0]);
        Assert.EndsWith("First part", chapters[1]);
        Assert.EndsWith("Second part", chapters[2]);
    }
}

public class SubtitleTests
{
    static List<(Word, TimeRange)> Words(string text)
    {
        var list = new List<(Word, TimeRange)>();
        long t = 0;
        foreach (var raw in text.Split(' '))
        {
            var core = raw.TrimEnd('.', ',', '?', '!');
            list.Add((new Word { Text = core, Trailing = raw[core.Length..] }, new TimeRange(t, t + Time.FromSeconds(.25))));
            t += Time.FromSeconds(.3);
        }
        return list;
    }

    [Fact]
    public void ChunksAreOneLineThreeToEightWords()
    {
        var words = Words("Ana's sleep dart is the most game-changing cooldown in Overwatch, and most Anas waste it on the wrong target every single fight. Hold it.");
        var caps = Subtitles.Chunk(words);
        Assert.All(caps, c => Assert.True(c.Text.Length <= 42, c.Text));
        Assert.All(caps, c => Assert.True(c.Text.Split(' ').Length <= 8, c.Text));
        Assert.Contains(caps, c => c.Text.EndsWith("Overwatch,"));
        Assert.Equal("Hold it.", caps[^1].Text);
    }

    [Fact]
    public void NumbersStayWithUnits()
    {
        var caps = Subtitles.Chunk(Words("It heals for exactly 250 health over two seconds which is a lot of healing for one ability."));
        Assert.DoesNotContain(caps, c => c.Text.EndsWith("250"));
    }

    [Fact]
    public void SrtRoundTrip()
    {
        var p = Project.CreateDefault("S");
        p.Captions.Add(new Caption { Text = "Hello there", FixedStart = Time.FromSeconds(1), FixedEnd = Time.FromSeconds(2) });
        var srt = Subtitles.ToSrt(Subtitles.Timings(p, []));
        Assert.Contains("00:00:01,000 --> 00:00:02,000", srt);
        var back = Subtitles.FromSrt(srt).Single();
        Assert.Equal("Hello there", back.Text);
        Assert.Equal(Time.FromSeconds(1), back.FixedStart);
    }

    [Fact]
    public void HiddenUnderTextCardsAndOutsideRegions()
    {
        var p = Project.CreateDefault("H");
        p.Captions.Add(new Caption { Text = "x", FixedStart = 0, FixedEnd = Time.FromSeconds(10) });
        var timings = Subtitles.Timings(p, []);
        Assert.Null(Subtitles.Visible(p, Time.FromSeconds(1), timings));
        p.SubtitleRegions.Add(new SubtitleRegion { Start = 0, End = Time.FromSeconds(5) });
        Assert.NotNull(Subtitles.Visible(p, Time.FromSeconds(1), timings));
        Assert.Null(Subtitles.Visible(p, Time.FromSeconds(6), timings));
        p.Track(TrackRole.Cards).Clips.Add(new Clip { Kind = ClipKind.Card, Start = 0, Duration = Time.FromSeconds(2), Card = Cards.NewCard(CardTemplateKind.Blur, "hi") });
        Assert.Null(Subtitles.Visible(p, Time.FromSeconds(1), timings));
        Assert.NotNull(Subtitles.Visible(p, Time.FromSeconds(3), timings));
    }
}

public class CardTests
{
    [Fact]
    public void LinesRevealWhenSpoken()
    {
        var p = Fixtures.Narrated("Sleep cancels ultimates. Graviton and Earthshatter included. Hold it for the ult.",
            "Sleep cancels ultimates.", "Graviton and Earthshatter included.", "Hold it for the ult.");
        var clip = new Clip { Kind = ClipKind.Card, Start = 0, Duration = p.Duration, Card = Cards.NewCard(CardTemplateKind.Blur, "Sleep cancels ultimates\nGraviton, Earthshatter…\nHold it for the ult") };
        p.Track(TrackRole.Cards).Clips.Add(clip);
        Cards.AutoTime(p, clip);
        var words = Derive.TimelineWords(p);
        Assert.Equal(0, clip.Card!.Lines[0].Reveal);
        Assert.Equal(words.First(w => w.Word.Text == "Graviton").TimeRange.Start, clip.Card.Lines[1].Reveal);
        Assert.Equal(words.First(w => w.Word.Text == "Hold").TimeRange.Start, clip.Card.Lines[2].Reveal);

        Cards.ApplyTaps(clip, [Time.FromSeconds(1), Time.FromSeconds(2)]);
        Cards.AutoTime(p, clip);
        Assert.Equal(Time.FromSeconds(1), clip.Card.Lines[1].Reveal);
    }

    [Fact]
    public void WrapAndSplit()
    {
        Assert.Equal(["one two", "three"], Cards.Wrap("one two three", 8));
        Assert.Equal(3, Cards.SplitLines("First point. Second point.\nThird").Count);
    }
}

public class MixTests
{
    [Fact]
    public void DuckAheadOfSpeechAndHoldShortGaps()
    {
        var env = new DuckEnvelope([new TimeRange(Time.FromSeconds(1), Time.FromSeconds(2)), new TimeRange(Time.FromSeconds(2.3), Time.FromSeconds(3))], [], Time.FromMs(600));
        Assert.Single(env.Speech);
        long attack = Time.FromMs(150), release = Time.FromMs(400);
        Assert.Equal(1, env.Amount(Time.FromSeconds(1), attack, release));
        Assert.True(env.Amount(Time.FromSeconds(.9), attack, release) is > 0 and < 1);
        Assert.Equal(0, env.Amount(Time.FromSeconds(.5), attack, release));
        Assert.Equal(1, env.Amount(Time.FromSeconds(2.15), attack, release));
        Assert.Equal(0, env.Amount(Time.FromSeconds(3.5), attack, release));
    }

    [Fact]
    public void NoDuckRangesPunchHoles()
    {
        var env = new DuckEnvelope([new TimeRange(0, Time.FromSeconds(10))], [new NoDuckRange { Start = Time.FromSeconds(4), End = Time.FromSeconds(6) }], 0);
        Assert.Equal(0, env.Amount(Time.FromSeconds(5), 0, 0));
        Assert.Equal(1, env.Amount(Time.FromSeconds(3), 0, 0));
    }
}

public class SettingsTests
{
    [Fact]
    public void DefaultKeysHaveNoConflicts()
    {
        var s = new AppSettings();
        Assert.Empty(s.Conflicts());
        Assert.Equal("Ctrl+B", s.KeyFor("edit.blade"));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("cutroom").FullName, "settings.json");
        var s = new AppSettings();
        s.Keys["edit.blade"] = "B";
        s.FootageFolders.Add(@"D:\Footage");
        s.Save(path);
        var back = AppSettings.Load(path);
        Assert.Equal("B", back.KeyFor("edit.blade"));
        Assert.Equal(6, back.CardTemplates.Count);
    }
}
