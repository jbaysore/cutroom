using Cutroom.Core;

namespace Cutroom.Tests;

/// <summary>
/// Writes a realistic, part-edited project to artifacts/smoke-project so scripts/check.ps1 can run the UI smoke on
/// populated screens (slots, clips, cleanup decisions, captions), not just empty ones.
/// </summary>
[Collection(SharedCache.Name)]
public class UiSampleTests
{
    static readonly string[] Spoken =
    [
        "Ana's sleep dart is the most important cooldown in the game.",
        "And most Ana players waste it in the first ten seconds.",
        "Sleep cancels almost every ultimate in the game.",
        "That means one dart can win the whole fight.",
        "But only if you hold it for the right target.",
        "Here the enemy Reinhardt is about to Earthshatter.",
        "I wait for the wind up, then sleep him mid swing.",
        "Practice it in the range before you queue.",
    ];

    [Fact]
    public void WritesPopulatedSmokeProject()
    {
        var dir = Path.Combine(Bench.Artifacts(), "smoke-project");
        Directory.CreateDirectory(dir);
        string script = $"{Spoken[0]} {Spoken[1]}\n\n# Why sleep matters\n{Spoken[2]} {Spoken[3]} {Spoken[4]}\n\n# Example one\n{Spoken[5]} {Spoken[6]}\n\n# Practice\n{Spoken[7]}";
        var p = Fixtures.Narrated(script, Spoken);
        p.Name = "Ana sleep dart guide";

        var narration = p.Media[0];
        narration.Path = Path.Combine(dir, "narration.wav");
        narration.Duration = p.Narration.SourceDuration;
        Media.Run("-f", "lavfi", "-i", FormattableString.Invariant($"aevalsrc=0.3*sin(2*PI*180*t)*abs(sin(2*PI*2.3*t)):s=48000:d={Time.ToSeconds(narration.Duration):0.###}"), narration.Path);
        p.Narration.CleanPath = narration.Path;
        p.Narration.Processed = true;

        // Cleanup decisions in the pauses between sentences, as a real run would leave them.
        var words = p.Narration.Words;
        var sentences = p.Script!.Sentences.ToList();
        var kinds = new[] { DecisionKind.Retake, DecisionKind.Silence, DecisionKind.FalseStart, DecisionKind.Breath, DecisionKind.Silence, DecisionKind.AdLibKept, DecisionKind.Silence };
        for (int s = 0; s < kinds.Length; s++)
        {
            var last = words.Last(w => w.SentenceId == sentences[s].Id);
            var kind = kinds[s];
            p.Narration.Decisions.Add(new CleanupDecision
            {
                Kind = kind, Start = last.End + Time.FromMs(60), End = last.End + Time.FromMs(420), Confidence = .35 + .08 * s,
                Context = kind is DecisionKind.Retake or DecisionKind.FalseStart or DecisionKind.AdLibKept ? Spoken[s + 1] : "",
                Reason = kind switch
                {
                    DecisionKind.Retake => "Said again right after; the later take is complete",
                    DecisionKind.FalseStart => "Restarted the same line within 2 s",
                    DecisionKind.AdLibKept => "Not in the script, kept",
                    DecisionKind.Breath => "Breath between sentences",
                    _ => "Pause shortened to 200 ms",
                },
            });
        }
        Derive.RebuildNarration(p);
        Derive.GenerateSlots(p);

        // Footage in about two thirds of the slots, a blur card, music under everything, captions in the intro.
        var footage = new List<MediaRef>();
        foreach (var (name, src) in new[] { ("Rein shatter.mp4", "testsrc2"), ("Junkertown full match.mp4", "smptehdbars"), ("Practice range.mp4", "rgbtestsrc") })
        {
            var path = Path.Combine(dir, name);
            Media.Run("-f", "lavfi", "-i", $"{src}=s=960x540:r=30:d=20", "-c:v", "mpeg4", "-q:v", "6", path);
            footage.Add(new MediaRef { Kind = MediaKind.Video, Path = path, Duration = Time.FromSeconds(20), Width = 960, Height = 540, Fps = 30, HasAudio = false });
        }
        p.Media.AddRange(footage);
        for (int i = 0; i < p.Slots.Count; i++)
        {
            var slot = p.Slots[i];
            if (i % 3 == 2) continue;
            if (i == 4)
            {
                var card = new Clip { Kind = ClipKind.Card, Start = slot.Start, Duration = slot.End - slot.Start, SlotId = slot.Id, Card = Cards.NewCard(CardTemplateKind.Blur, "Sleep cancels ultimates") };
                p.Track(TrackRole.Cards).Clips.Add(card);
            }
            var m = footage[i % footage.Count];
            p.Track(TrackRole.Footage).Clips.Add(new Clip { MediaId = m.Id, Start = slot.Start, Duration = slot.End - slot.Start, SourceIn = Time.FromSeconds(i % 10), SlotId = slot.Id, Label = Path.GetFileNameWithoutExtension(m.Path) });
        }
        var music = new MediaRef { Kind = MediaKind.Audio, Path = narration.Path, Duration = narration.Duration, HasAudio = true };
        p.Media.Add(music);
        p.Track(TrackRole.Music).Clips.Add(new Clip { MediaId = music.Id, Start = 0, Duration = p.Duration, Label = "lofi_focus_03" });
        p.Captions = Subtitles.Chunk(Derive.TimelineWords(p));
        Derive.EnsureIntroRegion(p, SubtitlePreset.DefaultId);
        Derive.RefreshIntroRegion(p);

        var file = Path.Combine(dir, "sample" + ProjectCatalog.Extension);
        File.WriteAllBytes(file, ProjectJson.Serialize(p));
        var reopened = Document.Open(file).Project;
        Assert.True(reopened.Slots.Count >= 8);
        Assert.NotEmpty(reopened.Narration.Decisions);
    }
}
