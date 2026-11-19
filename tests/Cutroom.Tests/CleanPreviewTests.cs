using Cutroom.App;
using Cutroom.Audio;
using Cutroom.Core;

namespace Cutroom.Tests;

public sealed class CleanPreviewTests
{
    static long S(double seconds) => Time.FromSeconds(seconds);

    static CleanupDecision Cut(DecisionKind kind, int start, int end) =>
        new() { Kind = kind, Start = S(start), End = S(end) };

    [Fact]
    public void MapsOverlappingCutsAndRecordingEdgesOntoTheCleanedTake()
    {
        var first = Cut(DecisionKind.Silence, 0, 1);
        var selected = Cut(DecisionKind.Retake, 3, 5);
        var overlap = Cut(DecisionKind.MouthNoise, 4, 6);
        var last = Cut(DecisionKind.Silence, 9, 10);
        var project = ProjectWithNarration(first, selected, overlap, last);
        var take = new CleanPreview(project);

        Assert.Equal(S(5), take.Duration);
        Assert.Equal(new[] { 0L, S(2), S(2), S(5) }, take.Markers.Select(marker => marker.Position));
        Assert.Equal(S(6), take.Map.ToSource(S(2)));
        Assert.Equal(new TimeRange(0, S(1.5)), take.Around(first));
        Assert.Equal(new TimeRange(S(3.5), S(5)), take.Around(last));

        var uncut = new CleanPreview(project, selected);
        Assert.Equal(S(6), uncut.Duration);
        Assert.Equal(new TimeRange(S(.5), S(3.5)), uncut.Around(selected));
        Assert.Equal(new TimeRange(S(.5), S(2.5)), take.Around(selected));
        Assert.True(selected.Removes);
        Assert.True(overlap.Removes);
    }

    [Fact]
    public void PreviewPlaysOnlyKeptAudioAndFollowsTheRestoredCutThroughUndo()
    {
        var decision = Cut(DecisionKind.FalseStart, 2, 4);
        var project = ProjectWithNarration(decision);
        var n = project.Narration;
        var removedWord = new Word { Start = S(2.5), End = S(3), Text = "wrong", Trailing = "." };
        decision.WordIds.Add(removedWord.Id);
        n.Words =
        [
            new Word { Start = S(1), End = S(1.5), Text = "First", Trailing = "." },
            removedWord,
            new Word { Start = S(4.5), End = S(5), Text = "Second", Trailing = "." },
        ];
        Derive.RebuildNarration(project);
        var doc = new Document(project);
        var take = new CleanPreview(doc.Project);
        Assert.Equal("First.", take.PassageAt(S(2)));
        Assert.Equal("Second.", take.PassageAt(S(2.5)));
        Assert.Single(take.Audio.Tracks);

        var path = Path.GetTempFileName();
        try
        {
            using (var audio = new BinaryWriter(File.Create(path)))
            {
                for (int i = 0; i < S(10); i++)
                {
                    float sample = i < S(2) ? .25f : i < S(4) ? -.5f : .75f;
                    audio.Write(sample);
                    audio.Write(sample);
                }
            }
            using var mixer = new Mixer(_ => path);
            mixer.Prepare(take.Audio, 0);
            Assert.Equal(.25f, mixer.RenderRange(S(1), S(1) + 1)[0]);
            Assert.Equal(.75f, mixer.RenderRange(S(3), S(3) + 1)[0]);

            var audition = new CleanPreview(project, decision);
            mixer.Prepare(audition.Audio, 0);
            Assert.Equal(-.5f, mixer.RenderRange(S(3), S(3) + 1)[0]);
            Assert.Equal(S(8), project.Duration);

            doc.Edit("Restore audio", p =>
            {
                p.Narration.Decisions.Single().Applied = false;
                Derive.RebuildNarration(p);
            });
            Assert.Equal(S(10), doc.Project.Duration);
            Assert.Empty(new CleanPreview(doc.Project).Markers);
            Assert.True(doc.Undo());
            Assert.Equal(S(8), new CleanPreview(doc.Project).Duration);
        }
        finally { File.Delete(path); }
    }

    static Project ProjectWithNarration(params CleanupDecision[] decisions)
    {
        var project = Project.CreateDefault("Clean preview");
        var media = new MediaRef { Kind = MediaKind.Audio, Duration = S(10) };
        project.Media.Add(media);
        project.Narration = new Narration { MediaId = media.Id, SourceDuration = media.Duration, Processed = true, Decisions = [.. decisions] };
        return project;
    }
}
